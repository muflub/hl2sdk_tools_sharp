#!/usr/bin/env python3
"""Summarises a tools/compile-perf.sh results folder into summary.md and summary.json.

    python3 tools/compile_perf_summary.py <results folder>

Reads the <stage>-t<N>.jsonl ledgers `ssmap bench` wrote, the
vrad-stages-t<N>.log files `ssmap vrad --bench` wrote, and, when present,
profile.speedscope.json from dotnet-trace.
"""

import glob
import json
import os
import re
import statistics
import sys
from collections import Counter, defaultdict

STAGE_ORDER = ["chain", "vbsp", "vvis", "vrad"]


def median(values):
    return statistics.median(values) if values else float("nan")


def read_ledger(path):
    """The timed, successful runs of one ledger."""
    runs = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            if row.get("Timed") and row.get("Ok"):
                runs.append(row)
    return runs


def summarise_ledger(runs):
    sub = defaultdict(list)
    for r in runs:
        for s in r.get("Stages", []):
            name, _, secs = s.rpartition("|")
            try:
                sub[name].append(float(secs))
            except ValueError:
                pass
    return {
        "runs": len(runs),
        "wall_s": median([r["WallSeconds"] for r in runs]),
        "wall_min_s": min((r["WallSeconds"] for r in runs), default=float("nan")),
        "wall_max_s": max((r["WallSeconds"] for r in runs), default=float("nan")),
        "cpu_s": median([r["CpuSeconds"] for r in runs]),
        "peak_rss_mb": median([r["PeakRssBytes"] / 1048576 for r in runs]),
        "gc_pause_s": median([r["GcPauseSeconds"] for r in runs]),
        "substages_s": {k: median(v) for k, v in sub.items()},
    }


def read_vrad_stages(path):
    """`bench <stage> <seconds>s` and `bench work ...` lines."""
    stages, work = {}, []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.match(r"^bench (\S+) ([0-9.]+)s$", line.strip())
            if m:
                stages[m.group(1)] = float(m.group(2))
            elif line.startswith("bench work "):
                work.append(line.strip()[len("bench work "):])
    return {"stages_s": stages, "work": work}


def read_profile(path, top=30):
    """Self and inclusive CPU time per function from a sampled thread-time trace."""
    with open(path, encoding="utf-8") as f:
        d = json.load(f)
    frames = d["shared"]["frames"]

    def name(i):
        return frames[i]["name"]

    self_ms, incl_ms = Counter(), Counter()
    total = 0.0
    for p in d["profiles"]:
        stack, last = [], None
        for e in p.get("events", []):
            at = e["at"]
            if stack and last is not None and at > last:
                # Only time on the CPU in managed code counts; waiting threads
                # and the finalizer's idle loop do not.
                if name(stack[-1]) == "CPU_TIME" and len(stack) > 1 and not any(
                    "RunFinalizers" in name(f) for f in stack
                ):
                    dt = at - last
                    total += dt
                    self_ms[stack[-2]] += dt
                    for f in set(stack[:-1]):
                        incl_ms[f] += dt
            if e["type"] == "O":
                stack.append(e["frame"])
            else:
                stack.pop()
            last = at

    def short(i):
        n = name(i).split("(")[0]
        return n.split("!")[-1]

    def rows(counter, managed_only):
        out = []
        for f, ms in counter.most_common():
            if managed_only and ("!SourceSharp" not in name(f) or name(f).startswith(("Process", "Thread"))):
                continue
            out.append({"function": short(f), "cpu_s": ms / 1000, "share": ms / total if total else 0})
            if len(out) == top:
                break
        return out

    return {"cpu_s": total / 1000, "self": rows(self_ms, False), "inclusive": rows(incl_ms, True)}


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    folder = sys.argv[1]
    result = {"cells": [], "vrad_stages": {}, "profile": None}

    for path in glob.glob(os.path.join(folder, "*-t*.jsonl")):
        m = re.match(r"^(\w+)-t(\d+)\.jsonl$", os.path.basename(path))
        if not m:
            continue
        cell = summarise_ledger(read_ledger(path))
        cell.update({"stage": m.group(1), "threads": int(m.group(2))})
        result["cells"].append(cell)
    result["cells"].sort(key=lambda c: (c["threads"], STAGE_ORDER.index(c["stage"]) if c["stage"] in STAGE_ORDER else 99))

    for path in glob.glob(os.path.join(folder, "vrad-stages-t*.log")):
        t = int(re.search(r"-t(\d+)\.log$", path).group(1))
        result["vrad_stages"][t] = read_vrad_stages(path)

    speedscope = os.path.join(folder, "profile.speedscope.json")
    if os.path.exists(speedscope):
        result["profile"] = read_profile(speedscope)

    md = ["# Compile performance", ""]
    env = os.path.join(folder, "env.txt")
    if os.path.exists(env):
        md += ["```", open(env, encoding="utf-8").read().rstrip(), "```", ""]

    md += ["## Each tool (median of the timed runs)", "",
           "| tool | threads | runs | wall s | min–max s | CPU s | CPU/wall | peak RSS MB | GC pause s |",
           "|---|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for c in result["cells"]:
        ratio = c["cpu_s"] / c["wall_s"] if c["wall_s"] else float("nan")
        md.append(f"| {c['stage']} | {c['threads']} | {c['runs']} | {c['wall_s']:.2f} | "
                  f"{c['wall_min_s']:.2f}–{c['wall_max_s']:.2f} | {c['cpu_s']:.2f} | {ratio:.2f} | "
                  f"{c['peak_rss_mb']:.0f} | {c['gc_pause_s']:.2f} |")
    md.append("")

    chains = [c for c in result["cells"] if c["stage"] == "chain" and c["substages_s"]]
    if chains:
        md += ["## The chain's own stages (median s)", ""]
        names = list(chains[0]["substages_s"].keys())
        md += ["| threads | " + " | ".join(names) + " |", "|---:|" + "---:|" * len(names)]
        for c in chains:
            md.append(f"| {c['threads']} | " + " | ".join(f"{c['substages_s'].get(n, float('nan')):.2f}" for n in names) + " |")
        md.append("")

    if result["vrad_stages"]:
        md += ["## vrad's stages (one `ssmap vrad --bench` run, s)", ""]
        for t in sorted(result["vrad_stages"]):
            v = result["vrad_stages"][t]
            md += [f"**{t} threads**", "", "| stage | s |", "|---|---:|"]
            md += [f"| {k} | {s:.3f} |" for k, s in v["stages_s"].items()]
            md += [""] + [f"- {w}" for w in v["work"]] + [""]

    p = result["profile"]
    if p:
        md += ["## vrad CPU profile", "",
               f"{p['cpu_s']:.1f} s of CPU sampled. Open profile.speedscope.json at https://www.speedscope.app for the full picture.", "",
               "**Self time**", "", "| function | CPU s | share |", "|---|---:|---:|"]
        md += [f"| `{r['function']}` | {r['cpu_s']:.2f} | {r['share']:.1%} |" for r in p["self"]]
        md += ["", "**Inclusive time (this tree's code)**", "", "| function | CPU s | share |", "|---|---:|---:|"]
        md += [f"| `{r['function']}` | {r['cpu_s']:.2f} | {r['share']:.1%} |" for r in p["inclusive"]]
        md.append("")

    with open(os.path.join(folder, "summary.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(md))
    with open(os.path.join(folder, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, default=str)
    return 0


if __name__ == "__main__":
    sys.exit(main())
