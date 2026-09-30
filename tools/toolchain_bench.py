#!/usr/bin/env python3
"""Toolchain benchmark: times the stock compile tools, Tools++ and ssmap on the same maps.

    tools/toolchain-bench.sh [--map NAME=VMF:GAME ...] [options]

Each toolset compiles each map with its own vbsp -> vvis -> vrad chain, and
every stage is timed. A chain uses its own vbsp's output, the way a mapper
would run it, so a toolset whose vbsp writes a different tree also pays (or
saves) for that in vvis and vrad. The per-stage numbers show where.

The toolsets:

  stock     the Source SDK Base 2013 Multiplayer compilers (bin/x64 of app
            243750, found through Steam's libraryfolders.vdf), under wine
  pp        Tools++ (vbspplusplus.exe and friends) with its compatibility
            DLLs, under wine
  ssmap     this repository's ssmap, the Release build run on the JIT
            (dotnet ssmap.dll; built first unless --no-build)
  ssmap-aot the same ssmap published with NativeAOT (bin/aot/ssmap, the
            publish CI makes for its AOT archives; published first unless
            --no-build). No JIT start-up, which matters most here because
            every stage is a process of its own
  ssmap-fast, ssmap-aot-fast
            ssmap and ssmap-aot with vvis -fastflow (and nothing else
            changed): the approximate portal flow, which stops walks that can
            only reach clusters already seen, so its PVS is a subset of the
            exact one (some visible pairs are culled). Its accuracy cost shows
            in the vis bytes column, next to plain ssmap's and stock's

The Windows tools cannot mount |appid_N| search paths ("Appid based mounting
is not supported on non-engine DLL projects"), so each map's game directory is
translated into a gameinfo.txt of its own that names the same content by
absolute Z: path, with its write path moved into the run's own folder so
nothing is written into the game directory. ssmap reads the original.

Runs are interleaved (stock, pp, ssmap, stock, pp, ssmap, ...) so a machine
that gets slower or faster during the run affects every toolset alike, and
each map starts with --warmups untimed chains per toolset, because a first
run pays for wine's prefix start-up and .NET's JIT and is not what a repeated
compile costs. The wine server is kept running (wineserver -p) for the same
reason.

Every run checks what it produced as well as how long it took: a toolset that
cannot find its content writes a map with no lighting and exits 0, and a fast
unlit compile is not a result. The summary reports each output's visibility
and lighting lump sizes and the "not found" lines in its logs, so an
unbalanced comparison is visible next to its numbers.

Options:
  --map NAME=VMF:GAME   a map to compile and its game directory (repeatable);
                        default: sandbox=maps/ss_sandbox.vmf:game/mod_sharp and
                        2fort=maps/sdk_ctf_2fort.vmf:game/mod_tf
  --toolsets LIST       any of stock,pp,ssmap,ssmap-aot,ssmap-fast,
                        ssmap-aot-fast (default all six)
  --runs N              timed chains per toolset and map (default 3)
  --warmups N           untimed chains first (default 1)
  --threads N           pass -threads N to every stage of every toolset
                        (default: each tool's own default)
  --vbsp ARGS / --vvis ARGS / --vrad ARGS
                        extra arguments for that stage, every toolset, as one
                        shell-quoted string (e.g. --vrad "-both -final")
  --stock-bin DIR       the stock compilers (default: SDK Base 2013 MP bin/x64)
  --pp-dir DIR          Tools++ (default ~/Downloads/tools_plusplus); its
                        tools/ and compatibility/ folders are staged together
  --wine PATH           wine (default: Proton Experimental's, then `wine`)
  --wineprefix DIR      (default ~/.local/share/source-sdk-wineprefix)
  --steam DIR           a Steam root, for |appid_N| (repeatable; default
                        ~/.steam/steam and ~/.local/share/Steam)
  --out DIR             results (default perf-results/toolchain-<timestamp>)
  --dotnet PATH         dotnet host (default ~/.dotnet/dotnet when present)
  --no-build            use the existing Release and AOT builds of ssmap
  --dry-run             print the commands of one chain per toolset and map
"""

import datetime
import hashlib
import json
import os
import re
import shlex
import shutil
import statistics
import struct
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOME = os.path.expanduser("~")
TOOLSETS = ["stock", "pp", "ssmap", "ssmap-aot", "ssmap-fast", "ssmap-aot-fast"]
STAGES = ["vbsp", "vvis", "vrad"]
DEFAULT_MAPS = [
    ("sandbox", "maps/ss_sandbox.vmf", "game/mod_sharp"),
    ("2fort", "maps/sdk_ctf_2fort.vmf", "game/mod_tf"),
]
PP_EXE = {"vbsp": "vbspplusplus.exe", "vvis": "vvisplusplus.exe", "vrad": "vradplusplus.exe"}

# What Tools++ needs to compile these maps at all, before any --vbsp/--vvis/--vrad.
# Without -matsyscompat, ++vbsp loads materials through the newer material
# system, which rejects the SDK 2013 and TF2 VTFs ("cached version doesn't
# exist"). Every face then compiles without a lightmap: the sandbox came out
# with 2781 faces instead of 3284 and no lighting at all, in a quarter of a
# second, and exited 0. With the flag it matches stock's face count and lights.
PP_DEFAULT_ARGS = {"vbsp": ["-matsyscompat"], "vvis": [], "vrad": []}
SDK_BASE_MP = 243750

# ssmap's approximate vvis flow. One constant, so a rename touches one line.
SSMAP_FAST_FLAG = "-fastflow"

# What the -fast toolsets add: the flag on vvis only. vbsp and vrad run as in
# plain ssmap, so the difference between the two rows is vvis's alone (and
# whatever vrad gains or loses from the PVS it is handed).
SSMAP_FAST_ARGS = {"vbsp": [], "vvis": [SSMAP_FAST_FLAG], "vrad": []}

# JIT or AOT build each ssmap toolset runs.
SSMAP_BUILDS = {"ssmap": "jit", "ssmap-fast": "jit", "ssmap-aot": "aot", "ssmap-aot-fast": "aot"}

# BSP lumps the output check reads: visibility, LDR and HDR lighting.
LUMP_VISIBILITY = 4
LUMP_LIGHTING = 8
LUMP_LIGHTING_HDR = 53

# Lines that mean a tool could not find some of its content. Counted, not
# matched exactly: stock, ++ and ssmap word these differently.
NOT_FOUND = re.compile(r"not found|couldn't (?:open|load|find)|can't (?:load|find|open)", re.IGNORECASE)


# ---- Steam and gameinfo ---------------------------------------------------


def parse_vdf_pairs(text):
    """The "key" "value" pairs of a Valve KeyValues text, in order, blocks ignored.

    Enough for libraryfolders.vdf and appmanifest_N.acf, whose interesting
    keys (path, installdir, the app ids under apps) are all plain pairs.
    """
    return re.findall(r'"([^"]*)"\s+"([^"]*)"', text)


def steam_libraries(steam_roots, read=None):
    """Every library folder the Steam roots list, each with the app ids it holds.

    Returns [(library path, {app id, ...})] in the order the files list them,
    without duplicates (~/.steam/steam is usually a link to the same install).
    """
    read = read or _read_text
    seen, out = set(), []
    for root in steam_roots:
        text = read(os.path.join(root, "steamapps", "libraryfolders.vdf"))
        if text is None:
            continue
        current = None
        for key, value in parse_vdf_pairs(text):
            if key == "path":
                current = value
                if current not in seen:
                    seen.add(current)
                    out.append((current, set()))
            elif current is not None and key.isdigit() and value.isdigit():
                for path, apps in out:
                    if path == current:
                        apps.add(int(key))
    return out


def find_app(app_id, steam_roots, read=None):
    """The install directory of a Steam app, or None when no library holds it."""
    read = read or _read_text
    for library, apps in steam_libraries(steam_roots, read):
        if app_id not in apps:
            continue
        manifest = read(os.path.join(library, "steamapps", f"appmanifest_{app_id}.acf"))
        if manifest is None:
            continue
        for key, value in parse_vdf_pairs(manifest):
            if key.lower() == "installdir":
                return os.path.join(library, "steamapps", "common", value)
    return None


def _read_text(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return None


def _tokens(line):
    """A gameinfo line's tokens: quoted strings or bare words, // comments dropped."""
    out, i, n = [], 0, len(line)
    while i < n:
        c = line[i]
        if c.isspace():
            i += 1
        elif line.startswith("//", i):
            break
        elif c == '"':
            j = line.find('"', i + 1)
            j = n if j < 0 else j
            out.append(line[i + 1:j])
            i = j + 1
        elif c in "{}":
            out.append(c)
            i += 1
        else:
            j = i
            while j < n and not line[j].isspace() and line[j] not in '{}"':
                j += 1
            out.append(line[i:j])
            i = j
    return out


def read_gameinfo(text):
    """(SteamAppId or None, [(kinds, location)]) out of a gameinfo.txt.

    Reads the SearchPaths block only, the way the translation needs it; the
    rest of the file (titles, hidden maps, Tools) is not the tools' concern here.
    """
    app_id, paths = None, []
    depth, search_depth, pending = 0, None, None
    for raw in text.splitlines():
        toks = _tokens(raw)
        k = 0
        while k < len(toks):
            t = toks[k]
            if t == "{":
                depth += 1
                if pending == "searchpaths":
                    search_depth = depth
                pending = None
                k += 1
                continue
            if t == "}":
                if search_depth == depth:
                    search_depth = None
                depth -= 1
                k += 1
                continue
            value = toks[k + 1] if k + 1 < len(toks) and toks[k + 1] not in "{}" else None
            if t.lower() == "steamappid" and value and value.isdigit():
                app_id = int(value)
            if search_depth is not None and depth == search_depth and value is not None:
                paths.append((t, value))
                k += 2
                continue
            pending = t.lower()
            k += 2 if value is not None else 1
    return app_id, paths


def translate_search_paths(paths, game_dir, find_install, exists=os.path.exists):
    """The search paths with every location made absolute, and what was dropped.

    |gameinfo_path| is the game directory, |appid_N| that app's install, and
    a location with no token is relative to the base directory (the game
    directory's parent), which is where the engine roots it. A location whose
    folder is not on disk is dropped, with the reason, instead of handed to a
    tool that would report it differently from the next one. Write kinds are
    removed: the caller adds one write path of its own, so no tool writes into
    the game directory.

    find_install(app_id) returns an install directory or None.
    Returns ([(kinds, absolute location)], [(location, reason)]).
    """
    base = os.path.dirname(os.path.abspath(game_dir))
    kept, dropped = [], []
    for kinds, location in paths:
        parts = [p for p in kinds.split("+") if p and "write" not in p.lower()]
        if not parts:
            continue
        m = re.match(r"\|appid_(\d+)\|(.*)", location, re.IGNORECASE)
        if m:
            install = find_install(int(m.group(1)))
            if install is None:
                dropped.append((location, f"Steam app {m.group(1)} is not installed"))
                continue
            target = os.path.join(install, m.group(2))
        elif location.lower().startswith("|gameinfo_path|"):
            target = os.path.join(os.path.abspath(game_dir), location[len("|gameinfo_path|"):])
        elif location.lower().startswith("|all_source_engine_paths|"):
            target = os.path.join(base, location[len("|all_source_engine_paths|"):])
        elif os.path.isabs(location):
            target = location
        else:
            target = os.path.join(base, location)
        target = os.path.normpath(target)
        probe = os.path.dirname(target) if target.endswith("*") else target
        if target.lower().endswith(".vpk") and not exists(target):
            dir_vpk = target[:-4] + "_dir.vpk"
            probe = dir_vpk
        if not exists(probe):
            dropped.append((location, f"{probe} does not exist"))
            continue
        kept.append(("+".join(parts), target))
    return kept, dropped


def wine_path(path):
    """A host path as the Windows tools see it under wine: Z: is the root."""
    return "Z:" + os.path.abspath(path)


def write_tool_gameinfo(dest_dir, app_id, kept, write_dir):
    """Writes the translated gameinfo.txt for the Windows tools into dest_dir."""
    os.makedirs(dest_dir, exist_ok=True)
    os.makedirs(write_dir, exist_ok=True)
    lines = [
        '"GameInfo"',
        "{",
        '\tgame\t"toolchain-bench"',
        "\ttype\tmultiplayer_only",
        "\tFileSystem",
        "\t{",
        f"\t\tSteamAppId\t{app_id or SDK_BASE_MP}",
        "\t\tSearchPaths",
        "\t\t{",
        f'\t\t\tmod+mod_write+default_write_path\t"{wine_path(write_dir)}"',
    ]
    lines += [f'\t\t\t{kinds}\t"{wine_path(location)}"' for kinds, location in kept]
    lines += ["\t\t}", "\t}", "}", ""]
    path = os.path.join(dest_dir, "gameinfo.txt")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    return path


# ---- BSP output check -----------------------------------------------------


def bsp_lumps(data):
    """{lump index: length} of a BSP's lump directory, or None when it is not one."""
    if len(data) < 8 + 64 * 16 or data[:4] != b"VBSP":
        return None
    return {i: struct.unpack_from("<iiii", data, 8 + i * 16)[1] for i in range(64)}


def check_output(bsp_path, logs):
    """What a chain produced: size, hash, vis and lighting bytes, and 'not found' lines."""
    out = {"bsp_bytes": None, "sha256": None, "vis_bytes": None, "light_bytes": None,
           "light_hdr_bytes": None, "not_found": 0}
    try:
        with open(bsp_path, "rb") as f:
            data = f.read()
    except OSError:
        data = None
    if data is not None:
        out["bsp_bytes"] = len(data)
        out["sha256"] = hashlib.sha256(data).hexdigest()
        lumps = bsp_lumps(data)
        if lumps:
            out["vis_bytes"] = lumps[LUMP_VISIBILITY]
            out["light_bytes"] = lumps[LUMP_LIGHTING]
            out["light_hdr_bytes"] = lumps[LUMP_LIGHTING_HDR]
    for log in logs:
        text = _read_text(log) or ""
        out["not_found"] += sum(1 for line in text.splitlines() if NOT_FOUND.search(line))
    return out


# ---- toolsets -------------------------------------------------------------


class Toolset:
    """One toolset's commands: stage -> (argv, cwd) for a map in a work folder."""

    def __init__(self, name):
        self.name = name

    def stage(self, stage, work, stem, extra):
        raise NotImplementedError


class WineToolset(Toolset):
    def __init__(self, name, bin_dir, exe, wine, gameinfo_dir, own_args=None):
        super().__init__(name)
        self.bin_dir, self.exe, self.wine, self.gameinfo_dir = bin_dir, exe, wine, gameinfo_dir
        self.own_args = own_args or {}

    def stage(self, stage, work, stem, extra):
        target = os.path.join(work, stem + (".vmf" if stage == "vbsp" else ".bsp"))
        # Options before the map: ++vbsp reads every trailing token as a map name.
        argv = [self.wine, os.path.join(self.bin_dir, self.exe[stage]),
                "-game", wine_path(self.gameinfo_dir)] + self.own_args.get(stage, []) + extra + [wine_path(target)]
        # cwd is the tools' own folder: ++ loads filesystem_stdio.dll from the
        # current directory and fails to start anywhere else.
        return argv, self.bin_dir


class SsmapToolset(Toolset):
    """ssmap, one process per stage like the stock tools.

    command is how to start it: [dotnet, ssmap.dll] on the JIT, or the
    NativeAOT executable alone. own_args are this toolset's own options per
    stage, before the run's extra ones (SSMAP_FAST_ARGS for the -fast sets).
    """

    def __init__(self, name, command, game_dir, own_args=None):
        super().__init__(name)
        self.command, self.game_dir = list(command), game_dir
        self.own_args = own_args or {}

    def stage(self, stage, work, stem, extra):
        target = os.path.join(work, stem + (".vmf" if stage == "vbsp" else ".bsp"))
        return self.command + [stage, "-game", self.game_dir] + self.own_args.get(stage, []) + extra + [target], work


def run_timed(argv, cwd, env, log):
    """Runs one stage to completion: (exit code, wall s, CPU s of the process tree it waited for).

    CPU is None where the platform has no getrusage (Windows); the Windows
    tools run under wine, so a real benchmark is on Linux anyway.
    """
    try:
        import resource
    except ImportError:
        resource = None
    before = resource.getrusage(resource.RUSAGE_CHILDREN) if resource else None
    start = time.perf_counter()
    with open(log, "w") as f:
        f.write("$ " + " ".join(shlex.quote(a) for a in argv) + "\n")
        f.flush()
        code = subprocess.call(argv, cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=f,
                               stderr=subprocess.STDOUT)
    wall = time.perf_counter() - start
    cpu = None
    if resource:
        after = resource.getrusage(resource.RUSAGE_CHILDREN)
        cpu = (after.ru_utime - before.ru_utime) + (after.ru_stime - before.ru_stime)
    return code, wall, cpu


def run_chain(toolset, env, vmf, work, extras):
    """One vbsp -> vvis -> vrad chain in a fresh work folder; stops at the first failure."""
    if os.path.isdir(work):
        shutil.rmtree(work)
    os.makedirs(work)
    stem = os.path.splitext(os.path.basename(vmf))[0]
    shutil.copy(vmf, os.path.join(work, stem + ".vmf"))
    record = {"stages": {}, "ok": True}
    logs = []
    for stage in STAGES:
        argv, cwd = toolset.stage(stage, work, stem, extras[stage])
        log = os.path.join(work, f"{stage}.log")
        logs.append(log)
        code, wall, cpu = run_timed(argv, cwd, env, log)
        record["stages"][stage] = {"exit": code, "wall": wall, "cpu": cpu}
        if code != 0:
            record["ok"] = False
            record["failure"] = f"{stage} exited {code}, see {log}"
            break
    record.update(check_output(os.path.join(work, stem + ".bsp"), logs))
    if record["ok"] and not record["light_bytes"] and not record["light_hdr_bytes"]:
        # Exit 0 and no lighting is how a toolset that cannot read its content
        # fails; it is fast for the same reason, so it is not a timing.
        record["ok"] = False
        record["failure"] = "no lighting in the output (the map compiled unlit)"
    return record


# ---- summary ---------------------------------------------------------------


def median_or_none(values):
    return statistics.median(values) if values else None


def summarise(results, toolsets):
    """{map: {toolset: {stage|total: (median, min, max)}}} over the ok timed runs."""
    table = {}
    for map_name, by_toolset in results.items():
        table[map_name] = {}
        for name in toolsets:
            runs = [r for r in by_toolset.get(name, []) if r["ok"]]
            row = {}
            for stage in STAGES + ["total"]:
                if stage == "total":
                    values = [sum(s["wall"] for s in r["stages"].values()) for r in runs]
                else:
                    values = [r["stages"][stage]["wall"] for r in runs if stage in r["stages"]]
                row[stage] = (median_or_none(values), min(values) if values else None,
                              max(values) if values else None)
            table[map_name][name] = row
    return table


def fmt(value, digits=2):
    return "–" if value is None else f"{value:.{digits}f}"


def render_markdown(header, table, results, toolsets, dropped):
    lines = ["# Toolchain benchmark", "", "```"] + header + ["```", ""]
    for map_name, rows in table.items():
        lines += [f"## {map_name}", "", "Median wall seconds of the timed runs (min–max for the total).", ""]
        base = rows.get("stock", {}).get("total", (None,))[0]
        lines.append("| toolset | vbsp | vvis | vrad | total | min–max | vs stock | runs ok |")
        lines.append("|---|---:|---:|---:|---:|---:|---:|---:|")
        for name in toolsets:
            row = rows[name]
            total = row["total"][0]
            ratio = f"{total / base:.2f}×" if total and base else "–"
            runs = results[map_name].get(name, [])
            ok = sum(1 for r in runs if r["ok"])
            lines.append(f"| {name} | {fmt(row['vbsp'][0])} | {fmt(row['vvis'][0])} | {fmt(row['vrad'][0])} | "
                         f"{fmt(total)} | {fmt(row['total'][1])}–{fmt(row['total'][2])} | {ratio} | {ok}/{len(runs)} |")
        lines += ["", "What the last timed run of each produced (0 lighting bytes means an unlit map):", ""]
        lines.append("| toolset | bsp bytes | vis bytes | lighting bytes | HDR lighting bytes | 'not found' lines | failure |")
        lines.append("|---|---:|---:|---:|---:|---:|---|")
        for name in toolsets:
            runs = results[map_name].get(name, [])
            if not runs:
                continue
            r = runs[-1]
            lines.append(f"| {name} | {r['bsp_bytes'] or '–'} | {r['vis_bytes'] if r['vis_bytes'] is not None else '–'} | "
                         f"{r['light_bytes'] if r['light_bytes'] is not None else '–'} | "
                         f"{r['light_hdr_bytes'] if r['light_hdr_bytes'] is not None else '–'} | "
                         f"{r['not_found']} | {r.get('failure', '')} |")
        if dropped.get(map_name):
            lines += ["", "Search paths left out of the Windows tools' gameinfo:", ""]
            lines += [f"- `{loc}`: {why}" for loc, why in dropped[map_name]]
        lines.append("")
    return "\n".join(lines)


# ---- main -------------------------------------------------------------------


def parse_args(argv):
    import argparse
    p = argparse.ArgumentParser(add_help=False)
    p.add_argument("--map", action="append", default=[])
    p.add_argument("--toolsets", default=",".join(TOOLSETS))
    p.add_argument("--runs", type=int, default=3)
    p.add_argument("--warmups", type=int, default=1)
    p.add_argument("--threads", type=int)
    p.add_argument("--vbsp", default="")
    p.add_argument("--vvis", default="")
    p.add_argument("--vrad", default="")
    p.add_argument("--stock-bin")
    p.add_argument("--pp-dir", default=os.path.join(HOME, "Downloads", "tools_plusplus"))
    p.add_argument("--wine")
    p.add_argument("--wineprefix", default=os.path.join(HOME, ".local", "share", "source-sdk-wineprefix"))
    p.add_argument("--steam", action="append", default=[])
    p.add_argument("--out")
    p.add_argument("--dotnet")
    p.add_argument("--no-build", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("-h", "--help", action="store_true")
    return p.parse_args(argv)


def parse_toolsets(text):
    """--toolsets' comma list -> (toolsets in the order given, names not in TOOLSETS)."""
    toolsets = [t.strip() for t in text.split(",") if t.strip()]
    return toolsets, [t for t in toolsets if t not in TOOLSETS]


def ssmap_toolsets(toolsets, dotnet, dll, aot, game):
    """The ssmap rows asked for: {name: SsmapToolset}, the -fast ones with SSMAP_FAST_ARGS."""
    sets = {}
    for name in toolsets:
        if name not in SSMAP_BUILDS:
            continue
        command = [dotnet, dll] if SSMAP_BUILDS[name] == "jit" else [aot]
        sets[name] = SsmapToolset(name, command, game, SSMAP_FAST_ARGS if name.endswith("-fast") else None)
    return sets


def parse_map_spec(spec):
    """NAME=VMF:GAME -> (name, vmf, game). The last ':' splits, so a VMF path may hold one."""
    name, sep, rest = spec.partition("=")
    vmf, sep2, game = rest.rpartition(":")
    if not sep or not sep2 or not name or not vmf or not game:
        raise ValueError(f"--map wants NAME=VMF:GAME, got {spec!r}")
    return name, vmf, game


def find_wine(explicit):
    if explicit:
        return explicit
    proton = os.path.join(HOME, ".steam", "steam", "steamapps", "common", "Proton - Experimental", "files", "bin", "wine")
    if os.path.exists(proton):
        return proton
    return shutil.which("wine") or shutil.which("wine64")


def aot_rid(machine, platform):
    """The runtime identifier to publish NativeAOT for: the one CI uses on this OS and CPU."""
    if platform == "darwin":
        return "osx-arm64" if machine in ("arm64", "aarch64") else "osx-x64"
    if platform.startswith("win"):
        return "win-x64"
    return "linux-arm64" if machine in ("aarch64", "arm64") else "linux-x64"


def stage_pp(pp_dir, dest):
    """Tools++'s executables and compatibility DLLs, side by side in dest (it loads them from its cwd)."""
    os.makedirs(dest, exist_ok=True)
    for sub in ("tools", "compatibility"):
        src = os.path.join(pp_dir, sub)
        if not os.path.isdir(src):
            raise FileNotFoundError(f"{src} is missing: --pp-dir must hold tools/ and compatibility/")
        for name in os.listdir(src):
            if name.lower().endswith((".exe", ".dll")):
                shutil.copy2(os.path.join(src, name), os.path.join(dest, name))
    missing = [e for e in PP_EXE.values() if not os.path.exists(os.path.join(dest, e))]
    if missing:
        raise FileNotFoundError(f"Tools++ is missing {', '.join(missing)} in {pp_dir}/tools")
    return dest


def main(argv):
    args = parse_args(argv)
    if args.help:
        print(__doc__)
        return 0
    toolsets, unknown = parse_toolsets(args.toolsets)
    if unknown:
        print(f"unknown toolset {', '.join(unknown)}; choose from {', '.join(TOOLSETS)}", file=sys.stderr)
        return 2
    maps = [parse_map_spec(m) for m in args.map] if args.map else list(DEFAULT_MAPS)
    maps = [(n, os.path.abspath(os.path.join(REPO, v)), os.path.abspath(os.path.join(REPO, g))) for n, v, g in maps]
    for name, vmf, game in maps:
        for path, what in ((vmf, "map"), (os.path.join(game, "gameinfo.txt"), "gameinfo")):
            if not os.path.exists(path):
                print(f"{name}: {what} {path} does not exist", file=sys.stderr)
                return 2

    steam_roots = args.steam or [os.path.join(HOME, ".steam", "steam"), os.path.join(HOME, ".local", "share", "Steam")]
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = os.path.abspath(args.out or os.path.join(REPO, "perf-results", f"toolchain-{stamp}"))
    os.makedirs(out, exist_ok=True)

    wine = find_wine(args.wine)
    wine_needed = any(t in ("stock", "pp") for t in toolsets)
    if wine_needed and not wine:
        print("no wine: install one or pass --wine (Proton Experimental's is looked for first)", file=sys.stderr)
        return 2
    env = dict(os.environ, WINEPREFIX=args.wineprefix, WINEDEBUG="-all", SteamAppUser="sourcesharp")

    stock_bin = args.stock_bin
    if "stock" in toolsets and not stock_bin:
        install = find_app(SDK_BASE_MP, steam_roots)
        stock_bin = install and os.path.join(install, "bin", "x64")
    if "stock" in toolsets and not (stock_bin and os.path.exists(os.path.join(stock_bin, "vbsp.exe"))):
        print(f"no stock compilers at {stock_bin}: install Source SDK Base 2013 Multiplayer or pass --stock-bin",
              file=sys.stderr)
        return 2
    pp_bin = stage_pp(args.pp_dir, os.path.join(out, "toolspp")) if "pp" in toolsets else None

    dotnet = args.dotnet or (os.path.join(HOME, ".dotnet", "dotnet")
                             if os.path.exists(os.path.join(HOME, ".dotnet", "dotnet")) else "dotnet")
    dll = os.path.join(REPO, "bin", "Release", "ssmap.dll")
    aot_dir = os.path.join(REPO, "bin", "aot")
    aot = os.path.join(aot_dir, "ssmap.exe" if sys.platform.startswith("win") else "ssmap")
    builds = {SSMAP_BUILDS[t] for t in toolsets if t in SSMAP_BUILDS}
    if not args.no_build and not args.dry_run:
        steps = []
        if "jit" in builds:
            steps.append([dotnet, "build", os.path.join(REPO, "src", "SourceSharp.MapTools.slnx"),
                          "-c", "Release", "-v", "q", "-nologo"])
        if "aot" in builds:
            # The same publish CI makes for its AOT archives, and compile-perf for its build=aot cells.
            steps.append([dotnet, "publish", os.path.join(REPO, "src", "SourceSharp.MapCompile"), "-c", "Release",
                          "-r", aot_rid(os.uname().machine if hasattr(os, "uname") else "x86_64", sys.platform),
                          "-p:PublishAot=true", "-o", aot_dir, "-v", "q", "-nologo"])
        for cmd in steps:
            if subprocess.call(cmd) != 0:
                print(f"ssmap {cmd[1]} failed", file=sys.stderr)
                return 1
    for name in toolsets:
        need = {"jit": dll, "aot": aot}.get(SSMAP_BUILDS.get(name))
        if need and not args.dry_run and not os.path.exists(need):
            print(f"{name}: {need} does not exist; run without --no-build to build it", file=sys.stderr)
            return 2

    threads = ["-threads", str(args.threads)] if args.threads else []
    extras = {s: threads + shlex.split(getattr(args, s)) for s in STAGES}

    commit = subprocess.run(["git", "-C", REPO, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    header = [
        f"date: {datetime.datetime.now().isoformat(timespec='seconds')}",
        f"git: {commit}",
        f"toolsets: {', '.join(toolsets)}",
        f"runs: {args.runs} timed + {args.warmups} warm-up per toolset and map, interleaved",
        f"threads: {args.threads or 'each tool’s default'}",
        f"extra args: vbsp={args.vbsp!r} vvis={args.vvis!r} vrad={args.vrad!r}",
        f"stock: {stock_bin}" if "stock" in toolsets else "stock: not run",
        (f"pp: {args.pp_dir}, always with " + ", ".join(f"{k} {' '.join(v)}" for k, v in PP_DEFAULT_ARGS.items() if v))
        if "pp" in toolsets else "pp: not run",
        f"wine: {wine} (prefix {args.wineprefix})" if wine_needed else "wine: not used",
        f"ssmap: {dll}" if "ssmap" in toolsets else "ssmap: not run",
        f"ssmap-aot: {aot}" if "ssmap-aot" in toolsets else "ssmap-aot: not run",
        f"ssmap-fast: {dll} with vvis {SSMAP_FAST_FLAG}" if "ssmap-fast" in toolsets else "ssmap-fast: not run",
        f"ssmap-aot-fast: {aot} with vvis {SSMAP_FAST_FLAG}" if "ssmap-aot-fast" in toolsets
        else "ssmap-aot-fast: not run",
        f"cpu: {_cpu_model()}, {os.cpu_count()} threads",
    ]

    if wine_needed and not args.dry_run:
        # Keep one wine server up for the whole run, so no stage pays for
        # starting it. It lingers 30 s after the last tool rather than forever,
        # and gets no stdout: a server holding this script's output open would
        # keep a pipe reading it (`... | tail`) waiting after the run ends.
        server = os.path.join(os.path.dirname(wine), "wineserver")
        subprocess.call([server if os.path.exists(server) else "wineserver", "-p30"], env=env,
                        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    results, dropped = {}, {}
    for map_name, vmf, game in maps:
        text = _read_text(os.path.join(game, "gameinfo.txt")) or ""
        app_id, paths = read_gameinfo(text)
        kept, dropped[map_name] = translate_search_paths(paths, game, lambda a: find_app(a, steam_roots))
        gi_dir = os.path.join(out, "games", map_name)
        write_tool_gameinfo(gi_dir, app_id, kept, os.path.join(out, "games", map_name + "-write"))
        sets = {}
        if "stock" in toolsets:
            sets["stock"] = WineToolset("stock", stock_bin, {s: s + ".exe" for s in STAGES}, wine, gi_dir)
        if "pp" in toolsets:
            sets["pp"] = WineToolset("pp", pp_bin, PP_EXE, wine, gi_dir, PP_DEFAULT_ARGS)
        sets.update(ssmap_toolsets(toolsets, dotnet, dll, aot, game))

        if args.dry_run:
            stem = os.path.splitext(os.path.basename(vmf))[0]
            for name in toolsets:
                for stage in STAGES:
                    argv, cwd = sets[name].stage(stage, os.path.join(out, "runs", map_name, name, "0"), stem, extras[stage])
                    print(f"[{map_name}/{name}] (cd {cwd}) " + " ".join(shlex.quote(a) for a in argv))
            continue

        results[map_name] = {name: [] for name in toolsets}
        total = args.warmups + args.runs
        for i in range(total):
            timed = i >= args.warmups
            for name in toolsets:
                label = f"run {i - args.warmups + 1}/{args.runs}" if timed else f"warm-up {i + 1}/{args.warmups}"
                work = os.path.join(out, "runs", map_name, name, str(i))
                record = run_chain(sets[name], env, vmf, work, extras)
                record["timed"] = timed
                took = sum(s["wall"] for s in record["stages"].values())
                state = "ok" if record["ok"] else record["failure"]
                print(f"{map_name} {name} {label}: {took:.2f} s, {state}", flush=True)
                if timed:
                    results[map_name][name].append(record)
                if i < total - 1:
                    # Keep the logs and drop the compiled output of all but the last run.
                    for f in os.listdir(work):
                        if not f.endswith(".log"):
                            os.remove(os.path.join(work, f))

    if args.dry_run:
        return 0
    table = summarise(results, toolsets)
    with open(os.path.join(out, "results.json"), "w") as f:
        json.dump({"header": header, "results": results, "dropped": dropped}, f, indent=1)
    summary = render_markdown(header, table, results, toolsets, dropped)
    with open(os.path.join(out, "summary.md"), "w") as f:
        f.write(summary)
    print(summary)
    print(f"results in {out}")
    return 0


def _cpu_model():
    text = _read_text("/proc/cpuinfo") or ""
    m = re.search(r"model name\s*:\s*(.+)", text)
    return m.group(1).strip() if m else "unknown"


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
