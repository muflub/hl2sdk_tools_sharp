#!/usr/bin/env python3
"""Summarises a tools/compile-perf.sh results folder into summary.md, summary.json and cells.csv.

    python3 tools/compile_perf_summary.py <results folder>

Each cell's own findings also go to cells/<cell>/report.md. Everything here
reads what the driver wrote; nothing is re-run, so the summary can be
rebuilt after editing this file.
"""

import csv
import json
import math
import os
import re
import statistics
import sys
from collections import Counter, defaultdict

STAGE_ORDER = ["chain", "vbsp", "vvis", "vrad"]
NAN = float("nan")


def median(values):
    values = [v for v in values if v is not None and not math.isnan(v)]
    return statistics.median(values) if values else NAN


def geomean(values):
    values = [v for v in values if v and v > 0 and not math.isnan(v)]
    return math.exp(sum(math.log(v) for v in values) / len(values)) if values else NAN


def f(v, fmt="{:.2f}"):
    return "" if v is None or (isinstance(v, float) and math.isnan(v)) else fmt.format(v)


def pct(ratio):
    return "" if ratio is None or math.isnan(ratio) else f"{(ratio - 1) * 100:+.0f}%"


# ------------------------------------------------------------------ readers

def read_ledger(path):
    """The timed, successful runs of one bench ledger."""
    runs = []
    if not os.path.exists(path):
        return runs
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line:
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
    walls = [r["WallSeconds"] for r in runs]
    return {
        "runs": len(runs),
        "wall_s": median(walls),
        "wall_min_s": min(walls, default=NAN),
        "wall_max_s": max(walls, default=NAN),
        "wall_cv": statistics.pstdev(walls) / statistics.mean(walls) if len(walls) > 1 else NAN,
        "cpu_s": median([r["CpuSeconds"] for r in runs]),
        "peak_rss_mb": median([r["PeakRssBytes"] / 1048576 for r in runs]),
        "gc_pause_s": median([r["GcPauseSeconds"] for r in runs]),
        "substages_s": {k: median(v) for k, v in sub.items()},
        "cache_reused": median([r.get("CacheReused", 0) for r in runs]),
    }


def read_stages(path):
    """`bench <stage> <seconds>s` and `bench work ...` lines from vbsp/vvis/vrad --bench."""
    if not os.path.exists(path):
        return None
    stages, work = {}, []
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = re.match(r"^bench (\S+) ([0-9.]+)s$", line.strip())
            if m:
                stages[m.group(1)] = float(m.group(2))
            elif line.startswith("bench work "):
                work.append(line.strip()[len("bench work "):])
    return {"stages_s": stages, "work": work}


def stage_column(tool, stage):
    """A stage's column heading: the last part of its name, except for vbsp,
    whose names repeat a last part under different parents (vbsp.world.write
    and vbsp.write), so only the tool's own prefix is dropped."""
    if tool == "vbsp":
        return stage[len("vbsp."):] if stage.startswith("vbsp.") else stage
    return stage.split(".")[-1]


def read_speedscope(path, top=30):
    """Self and inclusive CPU time per function from a sampled thread-time trace."""
    with open(path, encoding="utf-8") as fh:
        d = json.load(fh)
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
                        "RunFinalizers" in name(fr) for fr in stack):
                    dt = at - last
                    total += dt
                    self_ms[stack[-2]] += dt
                    for fr in set(stack[:-1]):
                        incl_ms[fr] += dt
            if e["type"] == "O":
                stack.append(e["frame"])
            elif stack:
                stack.pop()
            last = at

    def short(i):
        return name(i).split("(")[0].split("!")[-1]

    def rows(counter, ours_only):
        out = []
        for fr, ms in counter.most_common():
            if ours_only and ("!SourceSharp" not in name(fr) or name(fr).startswith(("Process", "Thread"))
                              or is_plumbing(name(fr))):
                continue
            out.append({"function": short(fr), "cpu_s": ms / 1000, "share": ms / total if total else 0})
            if len(out) == top:
                break
        return out

    return {"cpu_s": total / 1000, "self": rows(self_ms, False), "inclusive": rows(incl_ms, True)}


# The worker loop every parallel stage runs under, and the generic job
# wrappers of the work queue: inclusive time through them is "all parallel
# work", which ranks them top in every cell and says nothing about what to
# change. Self time in them (spinning, waiting) still shows in the self table.
PLUMBING = ("SourceSharp.MapTools.Parallel.CompilePool", "SourceSharp.MapTools.Parallel.WorkQueue")


def is_plumbing(frame_name):
    return frame_name.split("!")[-1].startswith(PLUMBING)


def read_counters(path):
    """dotnet-counters CSV -> each counter's peak and total over the run."""
    series = defaultdict(list)
    with open(path, encoding="utf-8", errors="replace") as fh:
        for row in csv.reader(fh):
            if len(row) < 5 or row[0] == "Timestamp":
                continue
            try:
                series[row[2]].append(float(row[4]))
            except ValueError:
                pass

    def pick(prefix):
        for k, v in series.items():
            if k.startswith(prefix):
                return v
        return []

    def total_of(prefix):
        return sum(sum(v) for k, v in series.items() if k.startswith(prefix))

    heaps = [v for k, v in series.items() if k.startswith("dotnet.gc.last_collection.heap.size")]
    heap = [sum(x) for x in zip(*heaps)] if heaps else []
    ws = pick("dotnet.process.memory.working_set")
    committed = pick("dotnet.gc.last_collection.memory.committed_size")
    alloc = pick("dotnet.gc.heap.total_allocated")
    queue = pick("dotnet.thread_pool.queue.length")
    threads = pick("dotnet.thread_pool.thread.count")
    gen = "dotnet.gc.collections ({collection} / 1 sec)[gc.heap.generation="
    return {
        "samples": len(ws),
        "working_set_peak_mb": max(ws, default=NAN) / 1048576,
        "gc_committed_peak_mb": max(committed, default=NAN) / 1048576,
        "gc_heap_peak_mb": max(heap, default=NAN) / 1048576,
        "alloc_rate_mean_mb_s": statistics.mean(alloc) / 1048576 if alloc else NAN,
        "alloc_rate_peak_mb_s": max(alloc, default=NAN) / 1048576,
        "allocated_total_mb": sum(alloc) / 1048576 if alloc else NAN,
        "gc_pause_total_s": total_of("dotnet.gc.pause.time"),
        "gen0": total_of(gen + "gen0]"),
        "gen1": total_of(gen + "gen1]"),
        "gen2": total_of(gen + "gen2]"),
        "lock_contentions": total_of("dotnet.monitor.lock_contentions"),
        "threadpool_queue_peak": max(queue, default=NAN),
        "threadpool_threads_peak": max(threads, default=NAN),
        "jit_time_s": total_of("dotnet.jit.compilation.time"),
        "cpu_user_s": total_of("dotnet.process.cpu.time (s / 1 sec)[cpu.mode=user]"),
        "cpu_system_s": total_of("dotnet.process.cpu.time (s / 1 sec)[cpu.mode=system]"),
    }


def read_folded(path, top=30):
    """perf.folded -> the leaf functions with the most samples (native, kernel and managed)."""
    leaf, total = Counter(), 0
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            stack, _, n = line.rstrip().rpartition(" ")
            if not stack:
                continue
            total += int(n)
            leaf[stack.split(";")[-1]] += int(n)
    return {"samples": total,
            "self": [{"function": k, "share": v / total} for k, v in leaf.most_common(top)] if total else []}


def load_json(path):
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def load_cell(folder, meta):
    d = os.path.join(folder, "cells", meta["id"])
    rec = load_json(os.path.join(d, "cell.json")) or {"status": "not run"}
    cell = dict(meta)
    cell.update({"status": rec.get("status"), "failure": rec.get("failure"), "threads_n": rec.get("threads"),
                 "options": rec.get("options"), "env": rec.get("env"),
                 "profile_errors": {k: v["error"] for k, v in (rec.get("profiles") or {}).items()
                                    if isinstance(v, dict) and v.get("error")}})
    # A run from before profile errors were recorded: an exit code that is
    # not 0 is the same failure.
    for k, v in (rec.get("profiles") or {}).items():
        if isinstance(v, dict) and v.get("exit") not in (None, 0) and k not in cell["profile_errors"]:
            cell["profile_errors"][k] = f"exited {v['exit']}, see cells/{meta['id']}/{k}.log"
    cell["bench"] = summarise_ledger(read_ledger(os.path.join(d, "bench.jsonl")))
    cell["stages"] = read_stages(os.path.join(d, "stages.log"))
    cell["rusage"] = load_json(os.path.join(d, "rusage.json"))
    cell["gc"] = load_json(os.path.join(d, "gc.json"))
    cell["heap"] = load_json(os.path.join(d, "heap.json"))
    speed = os.path.join(d, "cpu.speedscope.json")
    cell["cpu"] = read_speedscope(speed) if os.path.exists(speed) else None
    counters = os.path.join(d, "counters.csv")
    cell["counters"] = read_counters(counters) if os.path.exists(counters) else None
    folded = os.path.join(d, "perf.folded")
    cell["perf"] = read_folded(folded) if os.path.exists(folded) else None
    return cell


# ----------------------------------------------------------------- analysis

def alloc_bytes(c):
    return ((c.get("gc") or {}).get("allocations") or {}).get("estimated_bytes")


def effects(cells, axes):
    """Each setting's effect, from cells that differ only in that setting.

    For every finished cell whose value of an axis is not the baseline's, the
    cell with the same settings but the baseline's value of that axis is its
    twin; a row is the geometric mean of cell/twin over every such pair. With
    `full` that averages over every other setting, with `sweep` it is the one
    pair through the baseline, and with `pairwise` it is whatever pairs the
    covering array contains (possibly none).
    """
    by_key = {(c["stage"], tuple(sorted(c["settings"].items()))): c for c in cells if c["status"] == "ok"}
    rows = []
    for stage in STAGE_ORDER:
        stage_cells = [c for c in cells if c["stage"] == stage and c["status"] == "ok"]
        base_cell = next((c for c in cells if c["stage"] == stage and c["id"].endswith("__baseline")), None)
        if not stage_cells or not base_cell:
            continue
        for axis, base_value in base_cell["settings"].items():
            for value in axes.get(axis, {}).get("values", []):
                if value == base_value:
                    continue
                wall, rss, alloc = [], [], []
                for c in stage_cells:
                    if c["settings"][axis] != value:
                        continue
                    twin = by_key.get((stage, tuple(sorted(dict(c["settings"], **{axis: base_value}).items()))))
                    if not twin:
                        continue
                    wall.append(c["bench"]["wall_s"] / twin["bench"]["wall_s"])
                    rss.append(c["bench"]["peak_rss_mb"] / twin["bench"]["peak_rss_mb"])
                    a, b = alloc_bytes(c), alloc_bytes(twin)
                    if a and b:
                        alloc.append(a / b)
                if wall:
                    rows.append({"stage": stage, "axis": axis, "value": value, "vs": base_value, "pairs": len(wall),
                                 "wall": geomean(wall), "rss": geomean(rss), "alloc": geomean(alloc)})
    return rows


def scaling(cells):
    """Speed-up and efficiency across threads, for cells equal in every other setting."""
    groups = defaultdict(dict)
    for c in cells:
        if c["status"] != "ok" or not c.get("threads_n"):
            continue
        rest = tuple(sorted((k, v) for k, v in c["settings"].items() if k != "threads"))
        groups[(c["stage"], rest)][c["threads_n"]] = c["bench"]["wall_s"]
    rows = []
    for (stage, rest), by_t in groups.items():
        if 1 not in by_t:
            continue
        for t in sorted(by_t):
            if t == 1:
                continue
            speedup = by_t[1] / by_t[t]
            rows.append({"stage": stage, "settings": dict(rest), "threads": t, "one_s": by_t[1], "n_s": by_t[t],
                         "speedup": speedup, "efficiency": speedup / t})
    return rows


def hot_spots(cells, top=25):
    """The functions, allocation sites and contention sites that stay hot across the matrix.

    A function's weight in a cell is its share of that cell's sampled CPU (or
    of its allocated bytes); the tables rank by the mean share over the
    profiled cells, so something hot everywhere outranks something huge in a
    single corner of the matrix, and the peak column shows the corner.
    """
    cpu_self, cpu_incl, alloc_site, contention = (defaultdict(list) for _ in range(4))
    cpu_cells = gc_cells = 0
    for c in cells:
        if c.get("cpu"):
            cpu_cells += 1
            for r in c["cpu"]["self"]:
                cpu_self[r["function"]].append((r["share"], c["id"], None, None))
            for r in c["cpu"]["inclusive"]:
                cpu_incl[r["function"]].append((r["share"], c["id"], None, None))
        g = c.get("gc")
        if g:
            gc_cells += 1
            total = g["allocations"]["estimated_bytes"] or 1
            for r in g["allocations"]["by_site"]:
                alloc_site[r["name"]].append((r["bytes"] / total, c["id"], r["bytes"], r.get("via")))
            for r in g["contention"]["by_site"]:
                contention[r["name"]].append((r["ms"], c["id"], r["count"]))

    def rank(table, n):
        out = []
        for name, hits in table.items():
            peak = max(hits, key=lambda h: h[0])
            out.append({"name": name, "mean_share": sum(h[0] for h in hits) / max(n, 1), "peak_share": peak[0],
                        "peak_cell": peak[1], "cells": len(hits), "bytes_peak": peak[2], "via": peak[3]})
        return sorted(out, key=lambda r: -r["mean_share"])[:top]

    cont = sorted(({"name": k, "total_ms": sum(h[0] for h in v), "count": sum(h[2] for h in v), "cells": len(v),
                    "peak_cell": max(v, key=lambda h: h[0])[1]} for k, v in contention.items()),
                  key=lambda r: -r["total_ms"])[:top]
    return {"cpu_cells": cpu_cells, "gc_cells": gc_cells, "cpu_self": rank(cpu_self, cpu_cells),
            "cpu_inclusive": rank(cpu_incl, cpu_cells), "alloc_sites": rank(alloc_site, gc_cells), "contention": cont}


# ------------------------------------------------------------------ writing

def label(c, base):
    diff = [f"{a}={v}" for a, v in c["settings"].items() if base and v != base["settings"].get(a)]
    return ", ".join(diff) if diff else "**baseline**"


def cell_report(folder, c):
    lines = [f"# {c['id']}", "", "| setting | value |", "|---|---|"]
    lines += [f"| {k} | {v} |" for k, v in c["settings"].items()]
    lines += ["", f"Options: `{' '.join(c.get('options') or []) or 'none'}`",
              f"Environment: `{' '.join(f'{k}={v}' for k, v in (c.get('env') or {}).items()) or 'none'}`", ""]
    if c["status"] != "ok":
        lines += [f"**{c['status']}**: {c.get('failure') or ''}", ""]
    b = c["bench"]
    lines += ["## Timing (`ssmap bench`)", "",
              f"{b['runs']} timed runs: wall {f(b['wall_s'])} s (min {f(b['wall_min_s'])}, max {f(b['wall_max_s'])}, "
              f"cv {f(b['wall_cv'], '{:.1%}')}), CPU {f(b['cpu_s'])} s, peak RSS {f(b['peak_rss_mb'], '{:.0f}')} MB, "
              f"GC pause {f(b['gc_pause_s'], '{:.3f}')} s.", ""]
    if b["substages_s"]:
        lines += ["| sub-stage | s |", "|---|---:|"] + [f"| {k} | {f(v, '{:.3f}')} |" for k, v in b["substages_s"].items()] + [""]
    if c.get("stages") and c["stages"]["stages_s"]:
        lines += ["## Tool stages (`--bench`)", "", "| stage | s |", "|---|---:|"]
        lines += [f"| {k} | {v:.3f} |" for k, v in c["stages"]["stages_s"].items()]
        lines += [""] + [f"- {w}" for w in c["stages"]["work"]] + [""]
    r = c.get("rusage")
    if r and r.get("exit", 0) == 0:
        lines += ["## Process (one plain run)", "",
                  f"wall {f(r['wall_s'])} s, user {f(r['user_s'])} s, system {f(r['sys_s'])} s, max RSS "
                  f"{f(r['max_rss_mb'], '{:.0f}')} MB, page faults {r['minor_faults']} minor / {r['major_faults']} major, "
                  f"context switches {r['voluntary_switches']} voluntary / {r['involuntary_switches']} involuntary, "
                  f"block IO {r['block_in']} in / {r['block_out']} out.", ""]
        if r.get("perf_stat"):
            lines += ["| perf stat | value |", "|---|---:|"]
            lines += [f"| {k} | {f(v, '{:,.2f}')} |" for k, v in r["perf_stat"].items()] + [""]
    p = c.get("cpu")
    if p:
        lines += ["## CPU (sampled thread time)", "",
                  f"{p['cpu_s']:.1f} s of CPU sampled; open `cpu.speedscope.json` at https://www.speedscope.app.", "",
                  "**Self**", "", "| function | CPU s | share |", "|---|---:|---:|"]
        lines += [f"| `{x['function']}` | {x['cpu_s']:.2f} | {x['share']:.1%} |" for x in p["self"]]
        lines += ["", "**Inclusive (this tree's code)**", "", "| function | CPU s | share |", "|---|---:|---:|"]
        lines += [f"| `{x['function']}` | {x['cpu_s']:.2f} | {x['share']:.1%} |" for x in p["inclusive"]] + [""]
    g = c.get("gc")
    if g:
        gc = g.get("gc") or {}
        lines += ["## GC and allocations (runtime events)", ""]
        if gc:
            lines += [f"{gc['count']} GCs, {gc['total_pause_ms']:.0f} ms paused (max {gc['max_pause_ms']:.1f} ms), heap "
                      f"peak {gc['peak_heap_before_mb']:.0f} MB before / {gc['peak_heap_after_mb']:.0f} MB after a GC, "
                      f"{'server' if gc['server_gc'] else 'workstation'} GC.", "",
                      "| gen | GCs | pause ms | max ms | mean promoted MB |", "|---:|---:|---:|---:|---:|"]
            lines += [f"| {x['generation']} | {x['count']} | {x['pause_ms']:.1f} | {x['max_pause_ms']:.1f} | "
                      f"{x['mean_promoted_mb']:.1f} |" for x in gc["generations"]]
            lines += ["", "Reasons: " + ", ".join(f"{x['name']} {x['count']}" for x in gc["reasons"]), ""]
        a = g["allocations"]
        lines += [f"About {a['estimated_bytes'] / 1048576:.0f} MB allocated ({a['large_object_bytes'] / 1048576:.0f} MB "
                  "on the large object heap). Allocation ticks sample every ~100 KB, so rank these rather than quote them.",
                  "", "| allocation site | MB | via |", "|---|---:|---|"]
        lines += [f"| `{x['name']}` | {x['bytes'] / 1048576:.1f} | `{x.get('via') or ''}` |" for x in a["by_site"][:25]]
        lines += ["", f"Lock contention: {g['contention']['count']} waits, {g['contention']['total_ms']:.0f} ms.", ""]
        if g["contention"]["by_site"]:
            lines += ["| contended at | waits | ms |", "|---|---:|---:|"]
            lines += [f"| `{x['name']}` | {x['count']} | {x['ms']:.1f} |" for x in g["contention"]["by_site"][:10]] + [""]
        tp = g["thread_pool"]
        lines.append(f"Thread pool: up to {tp['max_workers']} workers, {tp['starvation_adjustments']} starvation adjustments.")
        if g.get("jit"):
            lines.append(f"JIT: {g['jit']['methods']} methods, {g['jit']['cpu_ms']:.0f} ms.")
        if g["exceptions"]:
            lines += ["", "| exception | count |", "|---|---:|"]
            lines += [f"| `{x['name'][:120]}` | {x['count']} |" for x in g["exceptions"]]
        lines.append("")
    k = c.get("counters")
    if k:
        lines += ["## Runtime counters (once a second)", "", "| counter | value |", "|---|---:|"]
        lines += [f"| {key} | {f(v)} |" for key, v in k.items()] + [""]
    h = c.get("heap")
    if h:
        lines += ["## Live heap at its largest snapshot", "",
                  f"{h['peak_mb']:.0f} MB live at {h['peak_at_s']:.1f} s (snapshots: "
                  + ", ".join(f"{t['at_s']:.1f} s {t['total_mb']:.0f} MB" for t in h["timeline"])
                  + "). `heap-peak.gcdump` opens in Visual Studio or PerfView.", "",
                  "| type | MB | objects |", "|---|---:|---:|"]
        lines += [f"| `{t['type']}` | {t['bytes'] / 1048576:.1f} | {t['count']:,} |" for t in h["top_types"][:25]] + [""]
    pf = c.get("perf")
    if pf:
        lines += ["## perf record (native and managed frames)", "",
                  f"{pf['samples']} samples; `perf.folded` feeds flamegraph.pl.", "", "| leaf | share |", "|---|---:|"]
        lines += [f"| `{x['function'][:120]}` | {x['share']:.1%} |" for x in pf["self"]] + [""]
    with open(os.path.join(folder, "cells", c["id"], "report.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))


def summarise(folder):
    meta = load_json(os.path.join(folder, "matrix.json"))
    if not meta:
        raise SystemExit(f"{folder} has no matrix.json")
    cells = [load_cell(folder, m) for m in meta["cells"]]
    for c in cells:
        if os.path.isdir(os.path.join(folder, "cells", c["id"])):
            cell_report(folder, c)
    bases = {c["stage"]: c for c in cells if c["id"].endswith("__baseline")}
    eff = effects(cells, meta["axes"])
    scale = scaling(cells)
    hot = hot_spots(cells)

    md = ["# Compile performance", ""]
    env = os.path.join(folder, "env.txt")
    if os.path.exists(env):
        md += ["```", open(env, encoding="utf-8").read().rstrip(), "```", ""]
    md += [f"Matrix `{meta['mode']}`: {len(cells)} cells, {sum(c['status'] == 'ok' for c in cells)} finished. Each "
           "cell's details are in `cells/<cell>/report.md`, and its raw captures beside it. Wall, CPU and GC pause are "
           "medians of the timed `ssmap bench` runs; allocation, gen-2 and contention figures come from the cell's "
           "own profiling runs, so profiler overhead is in neither.", ""]
    if meta.get("skipped"):
        md += ["Not run (requirement not given): " + ", ".join(meta["skipped"]), ""]

    for stage in STAGE_ORDER:
        rows = [c for c in cells if c["stage"] == stage]
        if not rows:
            continue
        base = bases.get(stage)
        bw = base["bench"]["wall_s"] if base else NAN
        md += [f"## {stage}: every cell", "",
               "| cell | wall s | vs base | min–max | CPU s | CPU/wall | peak RSS MB | GC pause s | alloc MB | gen2 "
               "| contention ms | IPC |",
               "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
        for c in sorted(rows, key=lambda c: c["bench"]["wall_s"] if not math.isnan(c["bench"]["wall_s"]) else 1e18):
            b = c["bench"]
            if c["status"] != "ok":
                md.append(f"| {label(c, base)} | {c['status']} |||||||||||")
                continue
            g = c.get("gc") or {}
            gens = (g.get("gc") or {}).get("generations") or []
            ipc = ((c.get("rusage") or {}).get("perf_stat") or {}).get("ipc") if (c.get("rusage") or {}).get("exit", 0) == 0 else None
            md.append(
                f"| {label(c, base)} | {f(b['wall_s'])} | {pct(b['wall_s'] / bw) if not math.isnan(bw) else ''} | "
                f"{f(b['wall_min_s'])}–{f(b['wall_max_s'])} | {f(b['cpu_s'])} | "
                f"{f(b['cpu_s'] / b['wall_s'] if b['wall_s'] else NAN)} | {f(b['peak_rss_mb'], '{:.0f}')} | "
                f"{f(b['gc_pause_s'], '{:.3f}')} | {f((alloc_bytes(c) or NAN) / 1048576, '{:.0f}')} | "
                f"{gens[2]['count'] if len(gens) > 2 else ''} | "
                f"{f((g.get('contention') or {}).get('total_ms'), '{:.0f}')} | {f(ipc)} |")
        md.append("")

    if eff:
        md += ["## What each setting does", "",
               "Geometric mean over every pair of finished cells that differ only in that setting "
               "(with `pairwise`, only the pairs the matrix happens to hold; `sweep` or `full` give every setting a pair).",
               "", "| stage | setting | vs | pairs | wall | peak RSS | allocated |", "|---|---|---|---:|---:|---:|---:|"]
        for r in sorted(eff, key=lambda r: (STAGE_ORDER.index(r["stage"]), -abs(math.log(r["wall"])))):
            md.append(f"| {r['stage']} | {r['axis']}={r['value']} | {r['vs']} | {r['pairs']} | {pct(r['wall'])} | "
                      f"{pct(r['rss'])} | {pct(r['alloc'])} |")
        md.append("")

    if scale:
        md += ["## Thread scaling", "",
               "| stage | other settings | threads | 1-thread s | n-thread s | speed-up | efficiency |",
               "|---|---|---:|---:|---:|---:|---:|"]
        for r in sorted(scale, key=lambda r: (STAGE_ORDER.index(r["stage"]), r["efficiency"])):
            base = bases.get(r["stage"])
            other = ", ".join(f"{k}={v}" for k, v in r["settings"].items()
                              if base and v != base["settings"].get(k)) or "baseline"
            md.append(f"| {r['stage']} | {other} | {r['threads']} | {r['one_s']:.2f} | {r['n_s']:.2f} | "
                      f"{r['speedup']:.2f}× | {r['efficiency']:.0%} |")
        md.append("")

    for tool in ("vbsp", "vvis", "vrad"):
        rows = [c for c in cells if c["stage"] == tool and c.get("stages") and c["stages"]["stages_s"]]
        if rows:
            names = list(dict.fromkeys(k for c in rows for k in c["stages"]["stages_s"]))
            md += [f"## {tool}'s own stages (s, one `--bench` run per cell)", "",
                   "| cell | " + " | ".join(stage_column(tool, n) for n in names) + " |", "|---|" + "---:|" * len(names)]
            md += [f"| {label(c, bases.get(tool))} | " + " | ".join(f(c['stages']['stages_s'].get(n)) for n in names) + " |"
                   for c in rows]
            md.append("")

    chains = [c for c in cells if c["stage"] == "chain" and c["status"] == "ok" and c["bench"]["substages_s"]]
    if chains:
        names = list(dict.fromkeys(k for c in chains for k in c["bench"]["substages_s"]))
        md += ["## The chain's stages (median s)", "", "| cell | " + " | ".join(names) + " |", "|---|" + "---:|" * len(names)]
        md += [f"| {label(c, bases.get('chain'))} | " + " | ".join(f(c['bench']['substages_s'].get(n)) for n in names) + " |"
               for c in chains]
        md.append("")

    md += ["## Hot spots across the matrix", "",
           f"From {hot['cpu_cells']} CPU profiles and {hot['gc_cells']} runtime-event traces. Mean share is over the "
           "profiled cells (0 where a function is not in that cell's top list), so what is hot everywhere ranks first; "
           "peak names the cell where it is hottest.", ""]
    if hot["cpu_inclusive"]:
        md += ["**CPU, inclusive, this tree's code**", "", "| function | mean share | peak | in cell | cells |",
               "|---|---:|---:|---|---:|"]
        md += [f"| `{r['name']}` | {r['mean_share']:.1%} | {r['peak_share']:.1%} | {r['peak_cell']} | {r['cells']} |"
               for r in hot["cpu_inclusive"]]
        md.append("")
    if hot["cpu_self"]:
        md += ["**CPU, self time (any code)**", "", "| function | mean share | peak | in cell | cells |",
               "|---|---:|---:|---|---:|"]
        md += [f"| `{r['name']}` | {r['mean_share']:.1%} | {r['peak_share']:.1%} | {r['peak_cell']} | {r['cells']} |"
               for r in hot["cpu_self"]]
        md.append("")
    if hot["alloc_sites"]:
        md += ["**Allocation sites** (type @ first SourceSharp frame)", "",
               "| site | mean share | peak MB | in cell | via |", "|---|---:|---:|---|---|"]
        md += [f"| `{r['name']}` | {r['mean_share']:.1%} | {f((r['bytes_peak'] or 0) / 1048576, '{:.0f}')} | "
               f"{r['peak_cell']} | `{r['via'] or ''}` |" for r in hot["alloc_sites"]]
        md.append("")
    if hot["contention"]:
        md += ["**Lock contention**", "", "| contended at | total ms | waits | cells | worst cell |", "|---|---:|---:|---:|---|"]
        md += [f"| `{r['name']}` | {r['total_ms']:.0f} | {r['count']} | {r['cells']} | {r['peak_cell']} |"
               for r in hot["contention"]]
        md.append("")
    gc_rows = [c for c in cells if c.get("gc") and (c["gc"].get("gc") or {}).get("count")]
    if gc_rows:
        md += ["**GC cost by cell** (most paused first)", "",
               "| cell | GCs | gen2 | pause ms | max pause ms | heap peak MB | LOH MB allocated |",
               "|---|---:|---:|---:|---:|---:|---:|"]
        for c in sorted(gc_rows, key=lambda c: -c["gc"]["gc"]["total_pause_ms"])[:20]:
            g = c["gc"]["gc"]
            md.append(f"| {c['id']} | {g['count']} | {g['generations'][2]['count']} | {g['total_pause_ms']:.0f} | "
                      f"{g['max_pause_ms']:.1f} | {g['peak_heap_before_mb']:.0f} | "
                      f"{c['gc']['allocations']['large_object_bytes'] / 1048576:.0f} |")
        md.append("")
    heap_rows = [c for c in cells if c.get("heap")]
    if heap_rows:
        big = max(heap_rows, key=lambda c: c["heap"]["peak_mb"])
        md += [f"**Largest live heap**: {big['heap']['peak_mb']:.0f} MB in {big['id']} (`cells/{big['id']}/heap-peak.gcdump`)",
               "", "| type | MB | objects |", "|---|---:|---:|"]
        md += [f"| `{t['type']}` | {t['bytes'] / 1048576:.1f} | {t['count']:,} |" for t in big["heap"]["top_types"][:15]]
        md.append("")

    broken = [(c["id"], k, e) for c in cells for k, e in sorted((c.get("profile_errors") or {}).items())]
    if broken:
        by_profiler = defaultdict(list)
        for cid, k, e in broken:
            by_profiler[k].append((cid, e))
        md += ["## Profiles that failed", "",
               "These cells' timings stand; only the named capture is missing or partial.", ""]
        for k, rows in sorted(by_profiler.items()):
            md.append(f"- **{k}** failed in {len(rows)} cell(s), e.g. {rows[0][0]}: {rows[0][1]}")
        md.append("")

    failed = [c for c in cells if c["status"] != "ok"]
    if failed:
        md += ["## Cells that did not finish", ""] + [f"- {c['id']}: {c['status']} {c.get('failure') or ''}" for c in failed]
        md.append("")

    with open(os.path.join(folder, "summary.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(md))
    with open(os.path.join(folder, "summary.json"), "w", encoding="utf-8") as fh:
        json.dump({"cells": [{k: c[k] for k in ("id", "stage", "settings", "status", "bench")} for c in cells],
                   "effects": eff, "scaling": scale, "hot_spots": hot}, fh, indent=2, default=str)
    with open(os.path.join(folder, "cells.csv"), "w", newline="", encoding="utf-8") as fh:
        axes = list(meta["axes"])
        w = csv.writer(fh)
        w.writerow(["id", "stage", "status"] + axes + ["wall_s", "wall_min_s", "wall_max_s", "cpu_s", "peak_rss_mb",
                                                       "gc_pause_s", "alloc_mb", "gc_count", "gen2", "gc_pause_ms",
                                                       "contention_ms", "heap_peak_mb", "ipc"])
        for c in cells:
            b, g = c["bench"], (c.get("gc") or {})
            gg = g.get("gc") or {}
            w.writerow([c["id"], c["stage"], c["status"]] + [c["settings"].get(a, "") for a in axes] + [
                f(b["wall_s"], "{:.4f}"), f(b["wall_min_s"], "{:.4f}"), f(b["wall_max_s"], "{:.4f}"),
                f(b["cpu_s"], "{:.4f}"), f(b["peak_rss_mb"], "{:.1f}"), f(b["gc_pause_s"], "{:.4f}"),
                f((alloc_bytes(c) or NAN) / 1048576, "{:.1f}"), gg.get("count", ""),
                gg["generations"][2]["count"] if gg.get("generations") else "", f(gg.get("total_pause_ms"), "{:.1f}"),
                f((g.get("contention") or {}).get("total_ms"), "{:.1f}"), f((c.get("heap") or {}).get("peak_mb"), "{:.1f}"),
                f(((c.get("rusage") or {}).get("perf_stat") or {}).get("ipc"), "{:.3f}")])
    return 0


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    return summarise(sys.argv[1])


if __name__ == "__main__":
    sys.exit(main())
