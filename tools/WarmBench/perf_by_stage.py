#!/usr/bin/env python3
"""Buckets `perf` samples of a WarmBench run by the compile stage they fell in.

WarmBench's --marks file records, in the monotonic clock perf uses, when each
compile entered each top-level stage (vbsp, vvis, vrad, write) and each
progress substage (vrad.BuildFacelights, ...). This script reads that file
and the text `perf script` prints, puts every sample in the stage that was
running when it was taken, and prints for each stage its sample count, the
share of native, JIT, GC and managed code, and the hottest functions, self
and inclusive.

It is optional: WarmBench's own lines already give each stage's wall and CPU
time. This is for when a stage got slower and the question is where.

    DOTNET_PerfMapEnabled=1 perf record -F 400 -g -k CLOCK_MONOTONIC -o warm.data -- \\
        dotnet tools/WarmBench/bin/Release/net10.0/WarmBench.dll --map ... --game ... \\
        --runs 4 --marks warm.marks
    perf script -i warm.data > warm.script
    tools/WarmBench/perf_by_stage.py warm.script warm.marks --waves 1,2,3

-k CLOCK_MONOTONIC matters: the marks are Stopwatch timestamps, which on
Linux are CLOCK_MONOTONIC nanoseconds, and perf's default clock is not
guaranteed to be the same one. Wave 0 is left out by default because its
samples are mostly the JIT.

Only one compile at a time (--concurrency 1) gives clean buckets: with
several, samples are put in the stage of whichever compile last reported a
boundary.
"""

import argparse
import bisect
import collections
import re
import sys

# Frames that are plumbing rather than work: left out of the inclusive list
# so the functions that did something rise to the top.
PLUMBING = re.compile(
    r"Thread|ExecutionContext|ThreadPool|WorkQueue::|CompilePool|StartCallback|RunInternal|MoveNext|"
    r"AsyncStateMachineBox|Task::|\[libcoreclr.so\]$|\[unknown\]|Dispatch|start_thread|clone|ThreadNative|"
    r"CallDescr|RuntimeMethodHandle|Parallel|TaskScheduler|TaskReplicator|\[dotnet\]|libc.so|libhost|__libc|"
    r"Program::|Job::|Bench::")

HEADER = re.compile(r"^(.*?)\s+(\d+)\s+(\d+)\.(\d+):\s*$")
FRAME = re.compile(r"^\s+([0-9a-f]+)\s+(.*)\s+\((.*)\)\s*$")


def read_marks(lines):
    """Returns the stage intervals as a sorted list of (start_ns, wave, top, sub).

    A marks line is "<ns> wave<N> begin|end", "<ns> w<N>j<J> <stage>" or
    "<ns> w<N>j<J> sub <substage>". Entering a top-level stage also starts its
    ".setup" substage, which lasts until the stage's first progress report.
    """
    events = []
    for line in lines:
        p = line.split()
        if len(p) < 3 or p[1].startswith("wave"):
            continue
        m = re.match(r"w(\d+)j\d+$", p[1])
        if not m:
            continue
        ns, wave = int(p[0]), int(m.group(1))
        if p[2] == "sub":
            events.append((ns, wave, None, p[3]))
        else:
            events.append((ns, wave, p[2], None))
    events.sort(key=lambda e: e[0])

    intervals = []
    top = sub = None
    for ns, wave, t, s in events:
        if t is not None:
            top, sub = t, t + ".setup"
        if s is not None:
            sub = s
        intervals.append((ns, wave, top, sub))
    return intervals


def read_samples(lines):
    """Yields (timestamp_ns, [(symbol, dso), ...]) for each sample, leaf frame first."""
    ns, stack = None, []
    for line in lines:
        if not line.strip():
            if ns is not None and stack:
                yield ns, stack
            ns, stack = None, []
            continue
        m = HEADER.match(line)
        if m:
            if ns is not None and stack:
                yield ns, stack
            ns = int(m.group(3)) * 1_000_000_000 + int(m.group(4).ljust(9, "0")[:9])
            stack = []
            continue
        m = FRAME.match(line)
        if m:
            stack.append((m.group(2), m.group(3)))
    if ns is not None and stack:
        yield ns, stack


def clean(symbol, dso):
    """Returns (display name, namespace, JIT tier) for one frame."""
    if "perf-" in dso and ".map" in dso:
        tier = ""
        m = re.search(r"\[(\w+)\]$", symbol)
        if m:
            tier = m.group(1)
            symbol = symbol[:m.start()]
        m = re.search(r"([\w.`<>+|$,\[\]]+)::([^(]+)\(", symbol)
        if m:
            parts = m.group(1).split(".")
            return parts[-1] + "::" + m.group(2), ".".join(parts[:-1]), tier
        return symbol, "", tier
    base = dso.rsplit("/", 1)[-1]
    if symbol == "[unknown]":
        return "[" + base + "]", base, ""
    return re.sub(r"\(.*$", "", symbol) + " [" + base + "]", base, ""


def category(name, namespace, dso):
    """Which kind of code a leaf frame is: ours, the BCL, the JIT, the GC, the runtime or native."""
    if "libclrjit" in dso:
        return "JIT"
    if "libcoreclr" in dso:
        if re.search(r"WKS::|SVR::|gc_heap|GCHeap|memclr|GCToEE|Ref_", name):
            return "GC"
        return "runtime"
    if "kernel" in dso:
        return "kernel"
    if "libc.so" in dso:
        return "libc"
    if namespace.startswith("SourceSharp"):
        return "managed:ours"
    return "managed:bcl"


class Tally:
    """Sample counts by stage: total, leaf category, JIT tier, self and inclusive."""

    def __init__(self, groups=()):
        self.groups = list(groups)
        self.grouped = collections.defaultdict(collections.Counter)
        self.total = collections.Counter()
        self.self_ = collections.defaultdict(collections.Counter)
        self.inclusive = collections.defaultdict(collections.Counter)
        self.categories = collections.defaultdict(collections.Counter)
        self.tiers = collections.defaultdict(collections.Counter)

    def add(self, key, stack):
        frames = [clean(s, d) + (d,) for s, d in stack]
        leaf = frames[0]
        for k in (key, "ALL"):
            self.total[k] += 1
            self.self_[k][leaf[0]] += 1
            self.categories[k][category(leaf[0], leaf[1], leaf[3])] += 1
            if leaf[2]:
                self.tiers[k][leaf[2]] += 1
        seen = set()
        for f in frames:
            if f[0] not in seen:
                seen.add(f[0])
                for k in (key, "ALL"):
                    self.inclusive[k][f[0]] += 1
        if self.groups:
            joined = " | ".join(f[1] + "." + f[0] for f in frames)
            for name, rx in self.groups:
                if rx.search(joined):
                    self.grouped[key][name] += 1
                    self.grouped["ALL"][name] += 1


def bucket(samples, intervals, waves, by, groups=()):
    """Puts each sample in the stage (by="top") or substage (by="sub") running when it was taken."""
    starts = [i[0] for i in intervals]
    tally = Tally(groups)
    for ns, stack in samples:
        i = bisect.bisect_right(starts, ns) - 1
        if i < 0:
            continue
        _, wave, top, sub = intervals[i]
        if wave not in waves or top is None:
            continue
        tally.add(top if by == "top" else sub, stack)
    return tally


def render(tally, top, frequency, wave_count, out):
    """Writes each stage's section, the biggest first, then ALL."""
    keys = sorted((k for k in tally.total if k != "ALL"), key=lambda k: -tally.total[k])
    for k in keys + (["ALL"] if tally.total["ALL"] else []):
        n = tally.total[k]
        out.write(f"\n=== {k}: {n} samples ({n / frequency / wave_count:.2f} cpu-s per wave)\n")
        out.write("  categories: " + ", ".join(
            f"{c} {v * 100 / n:.1f}%" for c, v in tally.categories[k].most_common()) + "\n")
        if tally.tiers[k]:
            out.write("  managed leaf tiers: " + ", ".join(
                f"{c} {v * 100 / n:.1f}%" for c, v in tally.tiers[k].most_common(5)) + "\n")
        if tally.grouped[k]:
            out.write("  groups (inclusive): " + ", ".join(
                f"{g} {v * 100 / n:.1f}% ({v / frequency / wave_count:.2f}s)"
                for g, v in tally.grouped[k].most_common()) + "\n")
        out.write("  -- self\n")
        for name, v in tally.self_[k].most_common(top):
            out.write(f"   {v * 100 / n:5.1f}%  {name}\n")
        out.write("  -- inclusive\n")
        shown = 0
        for name, v in tally.inclusive[k].most_common():
            if PLUMBING.search(name):
                continue
            out.write(f"   {v * 100 / n:5.1f}%  {name}\n")
            shown += 1
            if shown >= top:
                break


def main(argv=None, out=sys.stdout):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("script", help="the text `perf script` printed")
    ap.add_argument("marks", help="WarmBench's --marks file")
    ap.add_argument("--waves", default="1,2,3", help="waves to count, comma separated (default 1,2,3)")
    ap.add_argument("--top", type=int, default=15, help="functions listed per stage (default 15)")
    ap.add_argument("--by", choices=["top", "sub"], default="top", help="bucket by stage or by substage")
    ap.add_argument("--frequency", type=float, default=400.0, help="perf record -F, to turn samples into seconds")
    ap.add_argument("--grep", action="append", default=[], metavar="NAME=REGEX",
                    help="also report the share of samples whose stack matches REGEX, as NAME")
    args = ap.parse_args(argv)
    groups = [(g.split("=", 1)[0], re.compile(g.split("=", 1)[1])) for g in args.grep]
    waves = {int(w) for w in args.waves.split(",")}
    with open(args.marks, encoding="utf-8") as fh:
        intervals = read_marks(fh)
    with open(args.script, encoding="utf-8", errors="replace") as fh:
        tally = bucket(read_samples(fh), intervals, waves, args.by, groups)
    render(tally, args.top, args.frequency, len(waves), out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
