#!/usr/bin/env python3
"""Compile performance matrix: times and profiles ssmap across the settings that change its speed.

    tools/compile-perf.sh --map <path/to/map.vmf> [--game <dir>] [options]

Every cell of the matrix (a stage and one value per axis that applies to it,
from tools/compile-perf-matrix.json) is timed with `ssmap bench`, which restores
the stage's input before every run and discards a warm-up. Each cell is then
run again, once per profiler, so no profiler's overhead is in the timings or in
another profiler's numbers:

  stages    vvis/vrad --bench: each tool's own stage times and work counts
  rusage    a plain run: wall, user/system CPU, peak RSS, page faults, context
            switches, block IO; with `perf stat` when perf is installed
            (cycles, instructions, IPC, cache and branch misses)
  cpu       dotnet-trace, sampled thread time -> cpu.speedscope.json
  gc        dotnet-trace, runtime events (GC, allocation ticks with stacks,
            lock contention, thread pool, exceptions, JIT) -> gc.json via
            tools/PerfTraceReport
  counters  dotnet-counters, System.Runtime once a second -> counters.csv
  heap      dotnet-gcdump from 0.25 s in, then every --heap-interval seconds;
            the largest snapshot
            is kept with its by-type report
  perf      with --perf-record: perf record -g with the runtime's perf map,
            native and managed frames together -> perf.folded

Results go to <out>/cells/<cell>/, and summary.md compares every cell with the
baseline, ranks each setting's effect, and lists the hot functions, allocation
sites and GC costs across the matrix.

Matrix modes (--matrix):
  pairwise  (default) every pair of values of any two axes appears in at least
            one cell: covers the interactions at a fraction of the full size
  sweep     the baseline plus each value of each axis on its own
  full      every combination; use --set to narrow the axes first (the full
            chain matrix is thousands of cells)
  baseline  the baseline cell of each stage only

Options:
  --map PATH            the .vmf (copied; nothing is written beside it)
  --game DIR            the game directory (-game)
  --strip-steam         mount a copy of --game with its |appid_N| search paths
                        removed, for a machine without that Steam app
  --synthetic           add generated stand-ins for the map's materials,
                        textures, models, lights.rad, surface properties and
                        detail.vbsp (tools/SyntheticContent) to a copy of
                        --game; files the game already has are kept
  --static-props        compile a variant of the map with a prop_static beside
                        each model entity, so static-prop work is measured
  --stages LIST         any of chain,vbsp,vvis,vrad (default all four)
  --set AXIS=V1,V2      keep only these values of an axis (repeatable)
  --matrix-file PATH    the axes (default tools/compile-perf-matrix.json)
  --runs N              timed runs per cell (default 3), after --warmups (1)
  --profile LIST        any of stages,rusage,cpu,gc,counters,heap,perf, or
                        all / none (default all but perf)
  --profile-cells MODE  all (default), baseline or sweep: which cells profile
  --heap-interval S     seconds between heap snapshots (default 1; each one is
                        a full blocking GC, in the heap profiler's own run)
  --gpu MATCH           enables the tracer=gpu value (-gpu MATCH): a
                        case-insensitive part of the Vulkan device's name,
                        e.g. RTX or 4090. Checked before any cell runs, with
                        one fast vrad on the prepared input: a device that
                        does not match or fails vrad's self-test stops the run
                        with vrad's reason, rather than letting the gpu cells
                        fall back to the CPU tracer and time that
  --vphysics GAME       enables the cooker=native value (-vphysics GAME)
  --aot                 enables build=aot: publishes a NativeAOT ssmap first
  --perf-record         adds the perf profiler (needs perf)
  --out DIR             results (default perf-results/<timestamp>)
  --resume              skip cells a previous run into --out finished
  --dry-run             list the cells and stop
  --dotnet PATH         dotnet host (default ~/.dotnet/dotnet when present)
  --no-build            use the existing Release build
"""

import argparse
import datetime
import hashlib
import itertools
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STAGES = ["chain", "vbsp", "vvis", "vrad"]
PROFILERS = ["stages", "rusage", "cpu", "gc", "counters", "heap", "perf"]
DEFAULT_PROFILERS = [p for p in PROFILERS if p != "perf"]

# Runtime events for the gc profiler: GC (0x1) at verbose level, which adds
# AllocationTick; Loader (0x8) and JIT (0x10) so stacks resolve; Contention
# (0x4000), Exception (0x8000) and Threading (0x10000).
GC_PROVIDERS = "Microsoft-Windows-DotNETRuntime:0x1C019:5"


# ---------------------------------------------------------------- the matrix

def load_matrix(path):
    with open(path, encoding="utf-8") as f:
        m = json.load(f)
    for axis, spec in m["axes"].items():
        if m["baseline"].get(axis) not in spec["values"]:
            raise ValueError(f"baseline {axis}={m['baseline'].get(axis)} is not one of its values")
    return m


def narrow(matrix, sets, capabilities):
    """Drops the values --set leaves out and those whose requirement is missing.

    Returns the axes as {axis: {"stages": [...], "values": [names]}} and the
    values skipped for a missing requirement, for the report.
    """
    axes, skipped = {}, []
    for axis, spec in matrix["axes"].items():
        keep = list(spec["values"])
        if axis in sets:
            unknown = [v for v in sets[axis] if v not in spec["values"]]
            if unknown:
                raise ValueError(f"--set {axis}: no value {', '.join(unknown)} (have {', '.join(spec['values'])})")
            keep = [v for v in keep if v in sets[axis]]
        for v in list(keep):
            need = spec["values"][v].get("requires")
            if need and need not in capabilities:
                keep.remove(v)
                skipped.append(f"{axis}={v} (needs {need})")
        if not keep:
            raise ValueError(f"axis {axis} has no value left to run")
        axes[axis] = {"stages": spec["stages"], "values": keep}
    return axes, skipped


def stage_axes(axes, stage):
    return [a for a, spec in axes.items() if stage in spec["stages"]]


def base_value(axes, baseline, axis):
    """The baseline's value, or the axis's first remaining one if --set dropped it."""
    return baseline[axis] if baseline[axis] in axes[axis]["values"] else axes[axis]["values"][0]


def cells_for(axes, baseline, stage, mode):
    names = stage_axes(axes, stage)
    base = {a: base_value(axes, baseline, a) for a in names}
    if mode == "baseline":
        combos = [base]
    elif mode == "sweep":
        combos = [base] + [dict(base, **{a: v}) for a in names for v in axes[a]["values"] if v != base[a]]
    elif mode == "full":
        combos = [dict(zip(names, vs)) for vs in itertools.product(*(axes[a]["values"] for a in names))]
    elif mode == "pairwise":
        combos = [base] + pairwise([(a, axes[a]["values"]) for a in names], base)
    else:
        raise ValueError(f"unknown --matrix {mode}")
    seen, out = set(), []
    for c in combos:
        key = tuple(c[a] for a in names)
        if key not in seen:
            seen.add(key)
            out.append({"stage": stage, "settings": c})
    return out


def pairwise(axes, base):
    """A greedy all-pairs covering array: every value pair of any two axes is in some row.

    Each new row starts from the pair with the most uncovered partners and is
    filled axis by axis with the value that covers the most remaining pairs,
    ties going to the baseline's value so rows stay close to it.
    """
    names = [a for a, _ in axes]
    values = dict(axes)
    uncovered = set()
    for (i, a), (j, b) in itertools.combinations(enumerate(names), 2):
        for va in values[a]:
            for vb in values[b]:
                uncovered.add((a, va, b, vb))

    def covers(row, axis, value):
        n = 0
        for other, ov in row.items():
            if other == axis:
                continue
            i, j = names.index(axis), names.index(other)
            key = (axis, value, other, ov) if i < j else (other, ov, axis, value)
            n += key in uncovered
        return n

    rows = []
    while uncovered:
        a, va, b, vb = max(sorted(uncovered), key=lambda p: sum(1 for q in uncovered if q[0] == p[0] and q[1] == p[1]))
        row = {a: va, b: vb}
        for axis in names:
            if axis in row:
                continue
            row[axis] = max(values[axis], key=lambda v: (covers(row, axis, v), v == base[axis]))
        for (x, y) in itertools.combinations(names, 2):
            uncovered.discard((x, row[x], y, row[y]))
        rows.append({n: row[n] for n in names})
    return rows


def cell_id(cell, baseline_of_stage):
    diff = [f"{a}-{v}" for a, v in cell["settings"].items() if v != baseline_of_stage[a]]
    return "__".join([cell["stage"]] + (diff or ["baseline"]))


def find_perf():
    """A perf that runs: Ubuntu's /usr/bin/perf is a wrapper that refuses a kernel
    it has no linux-tools package for, while the versioned binaries work."""
    import glob
    for cand in [shutil.which("perf")] + sorted(glob.glob("/usr/lib/linux-tools/*/perf"), reverse=True):
        if cand and subprocess.run([cand, "--version"], capture_output=True).returncode == 0:
            return cand
    return None


def perf_allows(perf, mode):
    """Whether this machine lets perf run in mode ("stat" or "record").

    A kernel with perf_event_paranoid above what the user may use refuses
    both with an error and no compile at all, so each is probed once on
    `true` before any cell rather than discovered as an empty capture.
    """
    if not perf:
        return False
    if mode == "stat":
        cmd = [perf, "stat", "-x", ",", "-e", "task-clock", "--", "true"]
    else:
        import tempfile
        data = os.path.join(tempfile.gettempdir(), f"compile-perf-probe-{os.getpid()}.data")
        cmd = [perf, "record", "-F", "99", "-g", "-o", data, "--", "true"]
    try:
        return subprocess.run(cmd, capture_output=True, timeout=60).returncode == 0
    finally:
        if mode == "record":
            try:
                os.remove(data)
            except OSError:
                pass


def perf_paranoid():
    try:
        with open("/proc/sys/kernel/perf_event_paranoid", encoding="ascii") as f:
            return f.read().strip()
    except OSError:
        return "unknown"


def thread_count(value, nproc):
    if value == "max":
        return nproc
    if value == "half":
        return max(1, nproc // 2)
    return int(value)


def cell_command(matrix, cell, subst):
    """What one cell passes: the stage options, the environment and the bench cache mode."""
    stage = cell["stage"]
    chain, sections, env, cache = [], {"vbsp": [], "vvis": [], "vrad": []}, {}, "off"
    for axis, value in cell["settings"].items():
        spec = matrix["axes"][axis]["values"][value]
        env.update(spec.get("env", {}))
        cache = spec.get("cache", cache)
        if stage == "chain":
            if "chain" in spec:
                chain += spec["chain"]
            else:
                for tool in sections:
                    sections[tool] += spec.get(tool, [])
        else:
            chain += spec.get(stage, [])
    fill = lambda xs: [x.format(**subst) for x in xs]
    if stage == "chain":
        opts = fill(chain)
        for tool in ("vbsp", "vvis", "vrad"):
            if sections[tool]:
                opts += ["--" + tool] + fill(sections[tool])
        return opts, env, cache
    return fill(chain), env, cache


# ------------------------------------------------------------- running things

class Runner:
    def __init__(self, args):
        self.args = args
        self.dotnet = args.dotnet or (os.path.expanduser("~/.dotnet/dotnet")
                                      if os.access(os.path.expanduser("~/.dotnet/dotnet"), os.X_OK)
                                      else shutil.which("dotnet"))
        self.dll = os.path.join(REPO, "bin", "Release", "ssmap.dll")
        self.aot = os.path.join(REPO, "bin", "aot", "ssmap")
        self.report_dll = os.path.join(REPO, "tools", "PerfTraceReport", "bin", "Release", "net10.0", "PerfTraceReport.dll")
        self.content_dll = os.path.join(REPO, "tools", "SyntheticContent", "bin", "Release", "net10.0", "SyntheticContent.dll")
        tools = os.path.expanduser("~/.dotnet/tools")
        self.tool = lambda name: shutil.which(name) or (os.path.join(tools, name) if os.access(os.path.join(tools, name), os.X_OK) else None)

    def ssmap(self, build):
        return [self.aot] if build == "aot" else [self.dotnet, self.dll]

    def run(self, cmd, log, env=None, cwd=None, timeout=None):
        """Runs a command to completion; returns (exit code, wall seconds, rusage dict)."""
        full_env = dict(os.environ, **(env or {}))
        start = time.monotonic()
        with open(log, "ab") as out:
            out.write(("$ " + " ".join(cmd) + "\n").encode())
            out.flush()
            proc = subprocess.Popen(cmd, stdout=out, stderr=subprocess.STDOUT, env=full_env, cwd=cwd)
            _, status, ru = os.wait4(proc.pid, 0)
            proc.returncode = os.waitstatus_to_exitcode(status)
        wall = time.monotonic() - start
        return proc.returncode, wall, {
            "wall_s": wall,
            "user_s": ru.ru_utime,
            "sys_s": ru.ru_stime,
            "max_rss_mb": ru.ru_maxrss / 1024 if sys.platform != "darwin" else ru.ru_maxrss / 1048576,
            "minor_faults": ru.ru_minflt,
            "major_faults": ru.ru_majflt,
            "voluntary_switches": ru.ru_nvcsw,
            "involuntary_switches": ru.ru_nivcsw,
            "block_in": ru.ru_inblock,
            "block_out": ru.ru_oublock,
        }


def build(runner, out, with_aot, with_content=False):
    log = os.path.join(out, "build.log")
    steps = [[runner.dotnet, "build", os.path.join(REPO, "src", "SourceSharp.MapTools.slnx"), "-c", "Release"],
             [runner.dotnet, "build", os.path.join(REPO, "tools", "PerfTraceReport", "PerfTraceReport.csproj"), "-c", "Release"]]
    if with_content:
        steps.append([runner.dotnet, "build", os.path.join(REPO, "tools", "SyntheticContent", "SyntheticContent.csproj"),
                      "-c", "Release"])
    if with_aot:
        # The same publish CI makes for its AOT archives.
        rid = {"x86_64": "linux-x64", "aarch64": "linux-arm64", "arm64": "osx-arm64"}.get(os.uname().machine, "linux-x64")
        if sys.platform == "darwin" and rid.startswith("linux"):
            rid = "osx-x64"
        steps.append([runner.dotnet, "publish", os.path.join(REPO, "src", "SourceSharp.MapCompile"), "-c", "Release",
                      "-r", rid, "-p:PublishAot=true", "-o", os.path.dirname(runner.aot)])
    for cmd in steps:
        print("  " + " ".join(os.path.relpath(c, REPO) if c.startswith(REPO) else c for c in cmd[1:4]))
        code, _, _ = runner.run(cmd, log)
        if code != 0:
            sys.exit(f"compile-perf: build failed, see {log}")


def write_env(runner, out, args, map_path, game, cells, skipped):
    def sh(cmd):
        try:
            return subprocess.run(cmd, capture_output=True, text=True, check=False).stdout.strip()
        except OSError:
            return ""

    lines = [
        f"date: {datetime.datetime.now().isoformat(timespec='seconds')}",
        f"git: {sh(['git', '-C', REPO, 'rev-parse', 'HEAD'])} {'dirty' if sh(['git', '-C', REPO, 'status', '--porcelain']) else 'clean'}",
        f"branch: {sh(['git', '-C', REPO, 'rev-parse', '--abbrev-ref', 'HEAD'])}",
        f"map: {map_path} ({os.path.getsize(map_path)} bytes, sha256 {hashlib.sha256(open(map_path, 'rb').read()).hexdigest()})",
        f"game: {game or 'none'}{' (steam paths stripped)' if args.strip_steam else ''}"
        f"{' (+ synthetic content)' if args.synthetic else ''}",
        f"static props: {'a prop_static beside every model entity' if args.static_props else 'as the map has them'}",
        f"matrix: {args.matrix}, {len(cells)} cells, runs {args.runs} + {args.warmups} warm-up",
        f"profilers: {', '.join(args.profile)}; profile cells: {args.profile_cells}",
        f"skipped values: {', '.join(skipped) or 'none'}",
        f"nproc: {os.cpu_count()}",
        f"uname: {' '.join(os.uname())}",
    ]
    if shutil.which("lscpu"):
        lines += [l for l in sh(["lscpu"]).splitlines() if re.match(r"(Model name|CPU\(s\)|Thread|Core|Socket|.*MHz|L2|L3)", l)]
    elif sys.platform == "darwin":
        lines.append("cpu: " + sh(["sysctl", "-n", "machdep.cpu.brand_string"]))
    if os.path.exists("/proc/meminfo"):
        lines += [l for l in open("/proc/meminfo").read().splitlines() if l.startswith("MemTotal")]
    lines.append(f"dotnet host: {runner.dotnet}")
    lines += [l for l in sh([runner.dotnet, "--list-runtimes"]).splitlines() if "NETCore" in l]
    rc = os.path.join(REPO, "bin", "Release", "ssmap.runtimeconfig.json")
    if os.path.exists(rc):
        lines.append("ssmap runtimeconfig: " + json.dumps(json.load(open(rc)).get("runtimeOptions", {}).get("configProperties", {})))
    lines.append("DOTNET_ environment: " + ", ".join(f"{k}={v}" for k, v in sorted(os.environ.items()) if k.startswith(("DOTNET_", "COMPlus_"))))
    for name in ("dotnet-trace", "dotnet-counters", "dotnet-gcdump"):
        path = runner.tool(name)
        lines.append(f"{name}: {sh([path, '--version']).splitlines()[0] if path else 'not installed'}")
    perf = find_perf()
    lines.append(f"perf: {sh([perf, '--version']) + ' (' + perf + ')' if perf else 'not installed'}")
    with open(os.path.join(out, "env.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def game_copy(game, out, strip):
    """A copy of the game directory to compile against, so nothing is written into the original.

    With strip, its gameinfo.txt mounts no Steam app content (the |appid_N|
    search paths are removed), for a machine without those apps.
    """
    copy = os.path.join(out, "game")
    shutil.rmtree(copy, ignore_errors=True)
    shutil.copytree(game, copy, symlinks=True)
    if strip:
        info = os.path.join(copy, "gameinfo.txt")
        with open(info, encoding="utf-8", errors="replace") as f:
            kept = [l for l in f if "|appid_" not in l]
        with open(info, "w", encoding="utf-8") as f:
            f.writelines(kept)
    return copy


def synthesise(runner, out, game, map_path, static_props):
    """Adds the generated stand-in content to the game copy and, for
    static_props, writes the prop_static variant of the map; returns the map
    to compile."""
    cmd = [runner.dotnet, runner.content_dll]
    if game:
        cmd += ["--content", game]
    variant = None
    if static_props:
        variant = os.path.join(out, "inputs", os.path.basename(map_path)[:-4] + "_props.vmf")
        os.makedirs(os.path.dirname(variant), exist_ok=True)
        cmd += ["--props-map", map_path, variant]
    log = os.path.join(out, "synthetic.log")
    code, _, _ = runner.run(cmd, log)
    if code != 0:
        sys.exit(f"compile-perf: generating content failed, see {log}")
    return variant or map_path


class Inputs:
    """The work folder each stage compiles in, and the inputs a single-tool cell starts from.

    vvis cells start from the baseline vbsp's .bsp/.prt, and vrad cells from
    the baseline vvis's .bsp, so each tool is measured on the same input
    whatever the other tools' settings are.
    """

    def __init__(self, runner, out, map_path, game_args):
        self.runner, self.game_args = runner, game_args
        self.work = os.path.join(out, "work")
        shutil.rmtree(self.work, ignore_errors=True)
        os.makedirs(self.work)
        shutil.copy(map_path, self.work)
        rad = map_path[:-4] + ".rad"
        if os.path.exists(rad):
            shutil.copy(rad, self.work)
        self.vmf = os.path.join(self.work, os.path.basename(map_path))
        self.stem = self.vmf[:-4]
        self.keep = os.path.join(out, "inputs")
        os.makedirs(self.keep, exist_ok=True)
        self.log = os.path.join(self.keep, "prepare.log")

    def prepare(self, stages, vrad_input=False):
        """Compiles the single-tool inputs the stages need; vrad_input asks
        for the vvis output even when no vrad cell runs (the --gpu check)."""
        if vrad_input and "vrad" not in stages:
            stages = list(stages) + ["vrad"]
        need_vbsp = any(s in stages for s in ("vvis", "vrad"))
        if need_vbsp:
            self._tool("vbsp", self.vmf)
            for ext in (".bsp", ".prt"):
                shutil.copy(self.stem + ext, os.path.join(self.keep, "vbsp" + ext))
        if "vrad" in stages:
            self._tool("vvis", self.stem)
            shutil.copy(self.stem + ".bsp", os.path.join(self.keep, "vvis.bsp"))

    def _tool(self, tool, target):
        code, _, _ = self.runner.run(self.runner.ssmap("jit") + [tool] + self.game_args + [target], self.log, cwd=self.work)
        if code != 0:
            sys.exit(f"compile-perf: preparing the {tool} input failed, see {self.log}")

    def restore(self, stage):
        """Puts the stage's input in place; returns the map argument the stage takes."""
        if stage == "vvis":
            for ext in (".bsp", ".prt"):
                shutil.copy(os.path.join(self.keep, "vbsp" + ext), self.stem + ext)
            return self.stem
        if stage == "vrad":
            shutil.copy(os.path.join(self.keep, "vvis.bsp"), self.stem + ".bsp")
            return self.stem
        return self.vmf


def gpu_decline(log_text):
    """vrad's decline line (VRAD0707), or None when it used the GPU."""
    for line in log_text.splitlines():
        if "VRAD0707" in line or "gpu tracer declined" in line:
            return line.strip()
    return None


def check_gpu(runner, inputs, game_args, match, log):
    """One fast vrad with -gpu on the prepared input: the same device pick and
    self-test every gpu cell will get, so a bad match stops the run here."""
    stem = inputs.restore("vrad")
    code, _, _ = runner.run(runner.ssmap("jit") + ["vrad"] + game_args + ["-fast", "-bounce", "0", "-gpu", match, stem],
                            log, cwd=inputs.work)
    with open(log, encoding="utf-8", errors="replace") as f:
        reason = gpu_decline(f.read())
    if code != 0 or reason:
        sys.exit(f"compile-perf: --gpu {match} cannot be used: {reason or f'vrad exited {code}'}\n"
                 f"(see {log}; `vulkaninfo --summary` lists device names)")


def direct_command(runner, stage, build, map_arg, game_args, threads, opts, store=None):
    """The stand-alone command a profiler runs: the same compile one bench run makes."""
    if stage == "chain":
        split = next((i for i, o in enumerate(opts) if o in ("--vbsp", "--vvis", "--vrad")), len(opts))
        cache = ["-incremental", "-cache-dir", store] if store else []
        return runner.ssmap(build) + ["all", map_arg] + game_args + ["-threads", str(threads)] + opts[:split] + cache + opts[split:]
    return runner.ssmap(build) + [stage] + game_args + ["-threads", str(threads)] + opts + [map_arg]


def run_cell(runner, inputs, cell, cid, cdir, args, matrix, subst, game_args, profile):
    stage, s = cell["stage"], cell["settings"]
    build_kind = s.get("build", "jit")
    threads = thread_count(s["threads"], os.cpu_count())
    opts, env, cache = cell_command(matrix, cell, subst)
    record = {"id": cid, "stage": stage, "settings": s, "threads": threads, "options": opts, "env": env,
              "cache": cache, "profiles": {}, "status": "running"}
    os.makedirs(cdir, exist_ok=True)

    # 1. timing
    map_arg = inputs.restore(stage)
    bench = runner.ssmap(build_kind) + [
        "bench", "--map", map_arg, "--stages", stage, "--threads", str(threads), "--runs", str(args.runs),
        "--warmups", str(args.warmups), "--cache", cache, "--options", cid, "--arm", build_kind,
        "--workdir", inputs.work, "--cache-base", os.path.join(cdir, "bench-store"),
        "--out", os.path.join(cdir, "bench.jsonl")] + (["--game", args.game_dir] if args.game_dir else []) + ["--"] + opts
    code, wall, _ = runner.run(bench, os.path.join(cdir, "bench.log"), env=env, cwd=inputs.work)
    record["bench_exit"] = code
    failed = code != 0 or "FAILED" in open(os.path.join(cdir, "bench.log"), encoding="utf-8", errors="replace").read()
    shutil.rmtree(os.path.join(cdir, "bench-store"), ignore_errors=True)
    if failed:
        record["status"] = "failed"
        record["failure"] = f"bench failed, see cells/{cid}/bench.log"
        return record

    if profile:
        record["profiles"] = profile_cell(runner, inputs, cell, cdir, args, env, cache, opts, threads, build_kind, game_args)
    record["status"] = "ok"
    return record


def profile_cell(runner, inputs, cell, cdir, args, env, cache, opts, threads, build_kind, game_args):
    stage, done = cell["stage"], {}
    managed_only = build_kind != "aot"   # EventPipe tools need the JIT host

    def fresh(name):
        """Restores the input and gives the run its own cache store: cold starts empty, warm is filled first."""
        arg = inputs.restore(stage)
        store = None
        if stage == "chain" and cache != "off":
            store = os.path.join(cdir, "store-" + name)
            shutil.rmtree(store, ignore_errors=True)
            if cache == "warm":
                runner.run(direct_command(runner, stage, build_kind, arg, game_args, threads, opts, store),
                           os.path.join(cdir, "store-fill.log"), env=env, cwd=inputs.work)
                arg = inputs.restore(stage)
        return arg, store

    def done_with(name, store):
        if store:
            shutil.rmtree(store, ignore_errors=True)

    def step(name, fn):
        if name not in args.profile:
            return
        try:
            result = fn()
        except Exception as e:  # a profiler's failure costs that profile, not the matrix
            result = {"error": f"{type(e).__name__}: {e}"}
        # A profiler whose compile exited non-zero captured nothing worth
        # reading; say so rather than leave an empty or partial file.
        if isinstance(result, dict) and result.get("exit") not in (None, 0) and "error" not in result:
            result["error"] = f"exited {result['exit']}, see cells/{os.path.basename(cdir)}/{name}.log"
        done[name] = result

    def stages_():
        if stage not in ("vvis", "vrad"):
            return {"skipped": "only vvis and vrad have --bench"}
        arg, _ = fresh("stages")
        cmd = direct_command(runner, stage, build_kind, arg, game_args, threads, ["--bench"] + opts)
        log = os.path.join(cdir, "stages.log")
        code, _, _ = runner.run(cmd, log, env=env, cwd=inputs.work)
        return {"exit": code, "file": "stages.log"}

    def rusage_():
        arg, store = fresh("rusage")
        cmd = direct_command(runner, stage, build_kind, arg, game_args, threads, opts, store)
        perf = args.perf_stat
        stat = os.path.join(cdir, "perf-stat.txt")
        if perf:
            cmd = [perf, "stat", "-x", ",", "-o", stat, "-e",
                   "task-clock,context-switches,cpu-migrations,page-faults,cycles,instructions,"
                   "cache-references,cache-misses,branches,branch-misses", "--"] + cmd
        code, _, ru = runner.run(cmd, os.path.join(cdir, "rusage.log"), env=env, cwd=inputs.work)
        done_with("rusage", store)
        ru["exit"] = code
        if perf and os.path.exists(stat):
            ru["perf_stat"] = read_perf_stat(stat)
        with open(os.path.join(cdir, "rusage.json"), "w") as f:
            json.dump(ru, f, indent=2)
        return {"exit": code, "file": "rusage.json"}

    def cpu_():
        if not managed_only:
            return {"skipped": "dotnet-trace needs the JIT build"}
        trace = runner.tool("dotnet-trace")
        arg, store = fresh("cpu")
        nettrace = os.path.join(cdir, "cpu.nettrace")
        cmd = [trace, "collect", "--profile", "dotnet-sampled-thread-time", "--format", "speedscope",
               "-o", nettrace, "--"] + direct_command(runner, stage, build_kind, arg, game_args, threads, opts, store)
        code, _, _ = runner.run(cmd, os.path.join(cdir, "cpu.log"), env=env, cwd=inputs.work)
        done_with("cpu", store)
        speed = os.path.join(cdir, "cpu.speedscope.json")
        return {"exit": code, "file": "cpu.speedscope.json" if os.path.exists(speed) else None}

    def gc_():
        if not managed_only:
            return {"skipped": "dotnet-trace needs the JIT build"}
        trace = runner.tool("dotnet-trace")
        arg, store = fresh("gc")
        nettrace = os.path.join(cdir, "gc.nettrace")
        cmd = [trace, "collect", "--providers", GC_PROVIDERS, "-o", nettrace, "--"] + direct_command(
            runner, stage, build_kind, arg, game_args, threads, opts, store)
        code, _, _ = runner.run(cmd, os.path.join(cdir, "gc.log"), env=env, cwd=inputs.work)
        done_with("gc", store)
        if not os.path.exists(nettrace):
            return {"exit": code, "error": "no trace written"}
        rc, _, _ = runner.run([runner.dotnet, runner.report_dll, nettrace, os.path.join(cdir, "gc.json")],
                              os.path.join(cdir, "gc.log"))
        return {"exit": code, "report_exit": rc, "file": "gc.json"}

    def counters_():
        if not managed_only:
            return {"skipped": "dotnet-counters needs the JIT build"}
        counters = runner.tool("dotnet-counters")
        arg, store = fresh("counters")
        cmd = [counters, "collect", "--format", "csv", "--refresh-interval", "1", "--counters", "System.Runtime",
               "-o", os.path.join(cdir, "counters.csv"), "--"] + direct_command(
            runner, stage, build_kind, arg, game_args, threads, opts, store)
        code, _, _ = runner.run(cmd, os.path.join(cdir, "counters.log"), env=env, cwd=inputs.work)
        done_with("counters", store)
        return {"exit": code, "file": "counters.csv"}

    def heap_():
        if not managed_only:
            return {"skipped": "dotnet-gcdump needs the JIT build"}
        return heap_snapshots(runner, cdir, fresh, done_with, env, inputs, stage, build_kind, game_args, threads, opts,
                              args.heap_interval)

    def perf_():
        perf = find_perf()
        if not perf:
            return {"skipped": "perf is not installed"}
        arg, store = fresh("perf")
        data = os.path.join(cdir, "perf.data")
        cmd = [perf, "record", "-F", "499", "-g", "-o", data, "--"] + direct_command(
            runner, stage, build_kind, arg, game_args, threads, opts, store)
        code, _, _ = runner.run(cmd, os.path.join(cdir, "perf.log"), env=dict(env, DOTNET_PerfMapEnabled="1"),
                                cwd=inputs.work)
        done_with("perf", store)
        folded = os.path.join(cdir, "perf.folded")
        with open(folded, "w") as out:
            script = subprocess.run([perf, "script", "-i", data], capture_output=True, text=True, errors="replace")
            for stack, n in fold_perf_script(script.stdout).items():
                out.write(f"{stack} {n}\n")
        os.remove(data)
        return {"exit": code, "file": "perf.folded"}

    for name, fn in (("stages", stages_), ("rusage", rusage_), ("cpu", cpu_), ("gc", gc_),
                     ("counters", counters_), ("heap", heap_), ("perf", perf_)):
        step(name, fn)
    return done


def heap_snapshots(runner, cdir, fresh, done_with, env, inputs, stage, build_kind, game_args, threads, opts, interval):
    """Snapshots the managed heap every interval seconds and keeps the largest.

    Each snapshot is a full blocking collection, which is why this is its own
    run; the kept one is the closest this gets to the peak live heap.
    """
    gcdump = runner.tool("dotnet-gcdump")
    arg, store = fresh("heap")
    cmd = direct_command(runner, stage, build_kind, arg, game_args, threads, opts, store)
    log = open(os.path.join(cdir, "heap.log"), "ab")
    proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, env=dict(os.environ, **env), cwd=inputs.work)
    snaps, n = [], 0
    try:
        # A compile on a fast machine is over in a couple of seconds, so the
        # first snapshot comes early; later ones follow every interval.
        time.sleep(min(interval, 0.25))
        while proc.poll() is None:
            path = os.path.join(cdir, f"heap-{n}.gcdump")
            subprocess.run([gcdump, "collect", "-p", str(proc.pid), "-o", path], stdout=log, stderr=subprocess.STDOUT,
                           check=False, timeout=600)
            if os.path.exists(path):
                report = subprocess.run([gcdump, "report", path], capture_output=True, text=True, check=False).stdout
                total = heap_total(report)
                snaps.append({"file": os.path.basename(path), "at_s": n * interval, "total_mb": total / 1048576,
                              "report": report})
            n += 1
            deadline = time.monotonic() + interval
            while proc.poll() is None and time.monotonic() < deadline:
                time.sleep(0.2)
    finally:
        if proc.poll() is None:
            proc.send_signal(signal.SIGTERM)
        proc.wait()
        log.close()
        done_with("heap", store)
    if not snaps:
        return {"exit": proc.returncode, "error": "the run ended before the first snapshot"}
    peak = max(snaps, key=lambda s: s["total_mb"])
    for s in snaps:
        if s is not peak:
            os.remove(os.path.join(cdir, s["file"]))
    os.replace(os.path.join(cdir, peak["file"]), os.path.join(cdir, "heap-peak.gcdump"))
    with open(os.path.join(cdir, "heap-peak.txt"), "w") as f:
        f.write(peak["report"])
    timeline = [{"at_s": s["at_s"], "total_mb": s["total_mb"]} for s in snaps]
    with open(os.path.join(cdir, "heap.json"), "w") as f:
        json.dump({"timeline": timeline, "peak_at_s": peak["at_s"], "peak_mb": peak["total_mb"],
                   "top_types": parse_heap_report(peak["report"])[:40]}, f, indent=2)
    return {"exit": proc.returncode, "file": "heap.json", "snapshots": len(snaps)}


def heap_total(report):
    return sum(t["bytes"] for t in parse_heap_report(report))


def parse_heap_report(report):
    """dotnet-gcdump report lines: '<bytes>  <count>  <type>' (sizes may carry commas)."""
    rows = []
    for line in report.splitlines():
        m = re.match(r"^\s*([\d,]+)\s+([\d,]+)\s+(.+?)\s*$", line)
        if m:
            rows.append({"bytes": int(m.group(1).replace(",", "")), "count": int(m.group(2).replace(",", "")),
                         "type": m.group(3)})
    return sorted(rows, key=lambda r: -r["bytes"])


def read_perf_stat(path):
    """perf stat -x , lines: value,unit,event,... -> {event: value}."""
    out = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        parts = line.strip().split(",")
        if len(parts) >= 3 and parts[0] and not line.startswith("#"):
            try:
                out[parts[2]] = float(parts[0])
            except ValueError:
                out[parts[2]] = None   # <not supported> / <not counted>
    if out.get("cycles") and out.get("instructions"):
        out["ipc"] = out["instructions"] / out["cycles"]
    return out


def fold_perf_script(text):
    """perf script output -> folded stacks ('root;...;leaf' -> samples), for flame graphs."""
    folded, stack = {}, []
    for line in text.splitlines() + [""]:
        if not line.strip():
            if stack:
                key = ";".join(reversed(stack))
                folded[key] = folded.get(key, 0) + 1
            stack = []
        elif line.startswith((" ", "\t")):
            parts = line.strip().split(None, 1)
            sym = parts[1] if len(parts) > 1 else parts[0]
            sym = re.sub(r"\s*\(.*\)$", "", sym)
            stack.append(re.sub(r"\+0x[0-9a-f]+$", "", sym).replace(";", ","))
    return folded


# ---------------------------------------------------------------------- main

def parse_args(argv):
    p = argparse.ArgumentParser(add_help=False)
    p.add_argument("--map")
    p.add_argument("--game")
    p.add_argument("--strip-steam", action="store_true")
    p.add_argument("--synthetic", action="store_true")
    p.add_argument("--static-props", action="store_true")
    p.add_argument("--stages", default=",".join(STAGES))
    p.add_argument("--set", action="append", default=[])
    p.add_argument("--matrix", default="pairwise", choices=["pairwise", "sweep", "full", "baseline"])
    p.add_argument("--matrix-file", default=os.path.join(REPO, "tools", "compile-perf-matrix.json"))
    p.add_argument("--runs", type=int, default=3)
    p.add_argument("--warmups", type=int, default=1)
    p.add_argument("--profile", default="default")
    p.add_argument("--profile-cells", default="all", choices=["all", "baseline", "sweep"])
    p.add_argument("--heap-interval", type=float, default=1)
    p.add_argument("--gpu")
    p.add_argument("--vphysics")
    p.add_argument("--aot", action="store_true")
    p.add_argument("--perf-record", action="store_true")
    p.add_argument("--out")
    p.add_argument("--resume", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--dotnet")
    p.add_argument("--no-build", action="store_true")
    p.add_argument("-h", "--help", action="store_true")
    a = p.parse_args(argv)
    if a.help:
        print(__doc__)
        sys.exit(0)
    if not a.map or not a.map.endswith(".vmf") or not os.path.isfile(a.map):
        sys.exit("compile-perf: --map must name an existing .vmf (see --help)")
    if a.gpu is not None and not a.gpu.strip():
        sys.exit("compile-perf: --gpu needs part of a device name (e.g. --gpu RTX); an empty match is refused")
    if (a.synthetic or a.strip_steam) and not a.game:
        sys.exit("compile-perf: --synthetic and --strip-steam work on a copy of --game, so they need --game")
    a.stages = [s for s in a.stages.split(",") if s]
    bad = [s for s in a.stages if s not in STAGES]
    if bad:
        sys.exit(f"compile-perf: unknown stage {', '.join(bad)}")
    a.sets = {}
    for s in a.set:
        axis, _, values = s.partition("=")
        a.sets[axis] = [v for v in values.split(",") if v]
    profile = {"default": DEFAULT_PROFILERS, "all": PROFILERS, "none": []}.get(a.profile)
    a.profile = list(profile) if profile is not None else [x for x in a.profile.split(",") if x]
    if a.perf_record and "perf" not in a.profile:
        a.profile.append("perf")
    bad = [x for x in a.profile if x not in PROFILERS]
    if bad:
        sys.exit(f"compile-perf: unknown profiler {', '.join(bad)}")
    return a


def plan(args):
    matrix = load_matrix(args.matrix_file)
    caps = {c for c, on in (("gpu", args.gpu), ("vphysics", args.vphysics), ("aot", args.aot)) if on}
    axes, skipped = narrow(matrix, args.sets, caps)
    for axis in args.sets:
        if axis not in matrix["axes"]:
            sys.exit(f"compile-perf: --set names no axis {axis} (have {', '.join(matrix['axes'])})")
    cells = []
    profile_keys = set()
    for stage in args.stages:
        base = {a: base_value(axes, matrix["baseline"], a) for a in stage_axes(axes, stage)}
        stage_cells = cells_for(axes, matrix["baseline"], stage, args.matrix)
        chosen = {"all": stage_cells, "baseline": cells_for(axes, matrix["baseline"], stage, "baseline"),
                  "sweep": cells_for(axes, matrix["baseline"], stage, "sweep")}[args.profile_cells]
        profile_keys |= {cell_id(c, base) for c in chosen}
        for c in stage_cells:
            c["id"] = cell_id(c, base)
            cells.append(c)
    for c in cells:
        c["profile"] = c["id"] in profile_keys
    return matrix, axes, skipped, cells


def main(argv):
    args = parse_args(argv)
    matrix, axes, skipped, cells = plan(args)
    print(f"{len(cells)} cells ({args.matrix}): " + ", ".join(
        f"{s} {sum(1 for c in cells if c['stage'] == s)}" for s in args.stages))
    if skipped:
        print("skipped: " + ", ".join(skipped))
    if args.dry_run:
        for c in cells:
            print(("P " if c["profile"] else "  ") + c["id"])
        return 0

    runner = Runner(args)
    # perf is checked on this machine before any cell: stat only wraps the
    # rusage run when it works, and a requested perf record that cannot run
    # stops here rather than leaving every cell's capture empty.
    perf = find_perf()
    args.perf_stat = perf if "rusage" in args.profile and perf_allows(perf, "stat") else None
    if "rusage" in args.profile and perf and not args.perf_stat:
        print(f"note: perf stat is not permitted here (kernel.perf_event_paranoid={perf_paranoid()}); "
              "rusage runs without hardware counters. `sudo sysctl kernel.perf_event_paranoid=1` enables them.")
    if "perf" in args.profile and not perf_allows(perf, "record"):
        sys.exit("compile-perf: --perf-record needs perf record, which "
                 + (f"is not permitted here (kernel.perf_event_paranoid={perf_paranoid()}); "
                    "`sudo sysctl kernel.perf_event_paranoid=1` allows it" if perf else "is not installed")
                 + ", or run without --perf-record.")

    missing = [t for t in ("dotnet-trace", "dotnet-counters", "dotnet-gcdump")
               if runner.tool(t) is None and any(p in args.profile for p in
                                                 {"dotnet-trace": ("cpu", "gc"), "dotnet-counters": ("counters",),
                                                  "dotnet-gcdump": ("heap",)}[t])]
    if missing:
        sys.exit("compile-perf: the chosen profilers need " + ", ".join(missing) + ". Install them with:\n"
                 + "\n".join(f"  dotnet tool install -g {t}" for t in missing)
                 + "\nor leave those profilers out with --profile.")

    out = os.path.abspath(args.out or os.path.join(REPO, "perf-results", datetime.datetime.now().strftime("%Y%m%d-%H%M%S")))
    os.makedirs(os.path.join(out, "cells"), exist_ok=True)
    map_path = os.path.abspath(args.map)
    game = os.path.abspath(args.game) if args.game else None
    if game and (args.strip_steam or args.synthetic):
        game = game_copy(game, out, args.strip_steam)
    args.game_dir = game
    game_args = ["-game", game] if game else []

    wants_content = args.synthetic or args.static_props
    if not args.no_build:
        print("building...")
        build(runner, out, args.aot, wants_content)
    for need in [runner.dll] + ([runner.aot] if args.aot else []) + ([runner.report_dll] if "gc" in args.profile else []) \
            + ([runner.content_dll] if wants_content else []):
        if not os.path.exists(need):
            sys.exit(f"compile-perf: no build at {need}")
    if wants_content:
        print("generating content...")
        map_path = synthesise(runner, out, game if args.synthetic else None, map_path, args.static_props)

    write_env(runner, out, args, map_path, game, cells, skipped)
    subst = {"gpu": args.gpu or "", "vphysics": args.vphysics or ""}
    with open(os.path.join(out, "matrix.json"), "w") as f:
        json.dump({"mode": args.matrix, "baseline": matrix["baseline"], "axes": axes, "skipped": skipped,
                   "cells": [{k: c[k] for k in ("id", "stage", "settings", "profile")} for c in cells]}, f, indent=2)

    inputs = Inputs(runner, out, map_path, game_args)
    print("preparing inputs...")
    gpu_cells = "gpu" in axes.get("tracer", {}).get("values", [])
    inputs.prepare(args.stages, vrad_input=gpu_cells)
    if gpu_cells:
        print(f"checking -gpu {args.gpu}...")
        check_gpu(runner, inputs, game_args, args.gpu, os.path.join(out, "gpu-check.log"))

    started = time.monotonic()
    for i, cell in enumerate(cells):
        cdir = os.path.join(out, "cells", cell["id"])
        rec_path = os.path.join(cdir, "cell.json")
        if args.resume and os.path.exists(rec_path) and json.load(open(rec_path)).get("status") == "ok":
            continue
        left = ""
        if i:
            per = (time.monotonic() - started) / i
            left = f", about {datetime.timedelta(seconds=int(per * (len(cells) - i)))} left"
        print(f"[{i + 1}/{len(cells)}] {cell['id']}{left}")
        rec = run_cell(runner, inputs, cell, cell["id"], cdir, args, matrix, subst, game_args, cell["profile"])
        with open(rec_path, "w") as f:
            json.dump(rec, f, indent=2)
        if rec["status"] != "ok":
            print(f"  failed: {rec.get('failure')}")

    shutil.rmtree(inputs.work, ignore_errors=True)
    sys.path.insert(0, os.path.join(REPO, "tools"))
    import compile_perf_summary
    compile_perf_summary.summarise(out)
    print(f"results in {os.path.join(out, 'summary.md')}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
