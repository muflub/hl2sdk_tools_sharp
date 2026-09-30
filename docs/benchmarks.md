# Benchmarks: ssmap against the stock compilers and Tools++

This page explains how `ssmap`'s compile times are measured against the
Source SDK 2013 compilers and against Tools++, a third-party set of
optimised Source compilers. It gives the latest results, the machine they
came from, and exactly how
[`tools/toolchain-bench.sh`](../tools/toolchain-bench.sh) runs each tool, so
the numbers can be checked and reproduced. The [README](../README.md) shows
the headline tables.

Contents:

1. [Latest results](#latest-results)
2. [The machine](#the-machine)
3. [How a run works](#how-a-run-works)
4. [How each toolset is run](#how-each-toolset-is-run)
5. [Game content for the Windows tools](#game-content-for-the-windows-tools)
6. [Output checks](#output-checks)
7. [Caveats](#caveats)
8. [Running it yourself](#running-it-yourself)

## Latest results

Measured at main b6d6465 on 2026-09-29, with `tools/toolchain-bench.sh` and
its defaults (run folder `perf-results/toolchain-20260929-184448`). All 36
timed runs passed with lit output. The times are the median wall seconds of
3 timed chains after one warm-up, with the toolsets interleaved; the total is
the sum of the stages.

### `sdk_ctf_2fort`

Valve's SDK 2fort, compiled against Team Fortress 2's content. This is the
map to compare compilers on.

| toolset | vbsp | vvis | vrad | total | speed vs stock |
|---|---:|---:|---:|---:|---:|
| stock SDK 2013 | 5.55 | 9.31 | 31.60 | 46.46 | 1.00× (100%) |
| Tools++ | 4.82 | 2.58 | 7.81 | 15.20 | 3.06× (306%) |
| ssmap (JIT) | 3.56 | 4.56 | 8.10 | 16.20 | 2.87× (287%) |
| **ssmap (NativeAOT), fastest** | **1.15** | **4.34** | **5.87** | **11.34** | **4.10× (410%)** |
| ssmap (JIT), vvis `-fastflow` | 3.90 | 7.76 | 8.18 | 19.97 | 2.33× (233%) |
| ssmap (AOT), vvis `-fastflow` | 1.16 | 9.07 | 5.86 | 16.05 | 2.89× (289%) |

What each chain produced:

| toolset | bsp bytes | vis bytes | lighting bytes |
|---|---:|---:|---:|
| stock SDK 2013 | 22,342,168 | 660,492 | 9,607,452 |
| Tools++ | 27,067,972 | 658,072 | 9,610,020 |
| ssmap (JIT and NativeAOT) | 23,050,972 | 650,770 | 9,604,356 |
| ssmap, both `-fastflow` sets | 23,046,472 | 646,242 | 9,604,356 |

The logs had 2 "not found" lines each for stock and ssmap, and none for
Tools++.

### `ss_sandbox`

The repository's generated feature map: small, but it exercises every
feature. It is small enough that process start-up dominates the times, so
read it as a start-up comparison; 2fort is the one that matters.

| toolset | vbsp | vvis | vrad | total | speed vs stock |
|---|---:|---:|---:|---:|---:|
| stock SDK 2013 | 0.29 | 0.14 | 10.36 | 10.88 | 1.00× (100%) |
| Tools++ | 0.24 | 0.05 | 8.67 | 8.96 | 1.21× (121%) |
| ssmap (JIT) | 0.58 | 0.08 | 2.79 | 3.43 | 3.17× (317%) |
| ssmap (NativeAOT) | 0.13 | 0.01 | 1.45 | 1.59 | 6.84× (684%) |
| ssmap (JIT), vvis `-fastflow` | 0.72 | 0.12 | 3.30 | 4.15 | 2.62× (262%) |
| ssmap (AOT), vvis `-fastflow` | 0.13 | 0.01 | 1.34 | 1.48 | 7.35× (735%) |

### What the numbers say

- **ssmap NativeAOT is the fastest full chain.** On 2fort it runs 4.10× (410%)
  as fast as stock and beats Tools++ (11.34 s against 15.20 s). vrad is where
  ssmap gains most over stock, and its vbsp is the fastest of the four.
- **vvis is the one stage Tools++ still wins** (2.58 s against 4.34 s on
  2fort). Tools++ keeps its visibility bit vectors per cluster, where stock
  and ssmap keep them per portal; on 2fort that makes each vector about a
  fifth of the size, and every step of the portal flow reads and writes
  those vectors. It also vectorises those loops with AVX2. Its PVS is not
  the same as stock's, though (see [Caveats](#caveats)).
- **The sandbox is a start-up test.** The JIT build's 3.43 s against the
  AOT build's 1.59 s is mostly .NET start-up and JIT, paid once per
  stage.

### Notes

- **HDR.** No toolset writes HDR lighting with its default options, so
  every HDR lighting lump in this run is empty and the lighting column above
  is the LDR lump. The script records both.
- **One disturbed sandbox round.** In one of the sandbox rounds every
  toolset ran well above its median (stock, for example, took up to
  16.69 s against its median of 10.88 s). Because the toolsets are interleaved, the disturbance hit all of
  them in the same round, and the medians are unaffected.
- **`-fastflow` is slower than the exact flow on this machine.** ssmap's
  approximate vvis flow ([`-fastflow`](../README.md#the-fast-vvis-flow--fastflown))
  does less work than the exact one, but on 32 threads it finishes later:
  7.76 s against 4.56 s on the JIT build, 9.07 s against 4.34 s with AOT.
  The cause is its determinism rule. For the output to be the same at every
  thread count, a portal is flowed only once every lower-ranked neighbour it
  prunes with has finished, so portals wait on each other, and only 4 to 10
  of the 32 threads are busy at a time. `-fastflow` pays off only at 16 threads or
  fewer. Its accuracy cost shows in the vis bytes column: its PVS is a
  subset of the exact one, so its visibility lump is smaller (646,242 bytes
  against 650,770).

## The machine

- AMD Ryzen 9 9950X, 16 cores / 32 threads.
- 62 GB RAM.
- Linux (Ubuntu, kernel 7.0), with no other heavy load during the run.
- The Windows tools (stock and Tools++) run under wine.

## How a run works

- **Maps.** By default `maps/ss_sandbox.vmf` with the game directory
  `game/mod_sharp`, and `maps/sdk_ctf_2fort.vmf` with `game/mod_tf`. Other
  maps are added with `--map NAME=VMF:GAME`.
- **One chain per toolset.** Each toolset compiles each map with its own
  vbsp, then its own vvis, then its own vrad, one process per stage, in a
  fresh work folder holding a copy of the VMF. A chain uses its own vbsp's
  output, the way a mapper would run it, so a toolset whose vbsp writes a
  different tree pays (or saves) for that in vvis and vrad. The per-stage
  columns show where.
- **Timing.** Every stage is timed by wall clock, from process start to
  exit. The tables report the median over the timed chains; the total is the
  sum of the stages. The process tree's CPU time is recorded too, in
  `results.json`.
- **Warm-up and interleaving.** Each toolset gets one untimed warm-up chain
  per map (`--warmups`, default 1), which absorbs the wine prefix's start-up
  and .NET's first-run costs, then 3 timed chains (`--runs`). Within each
  round the toolsets run one after another (stock, Tools++, ssmap, ssmap-aot,
  and so on), so a machine that speeds up or slows down during the run
  affects every toolset alike. One wine server is started persistent before
  the first chain (`wineserver -p30`, which stays up until 30 seconds after
  the last tool exits), so no stage pays for starting it.
- **Threads.** Each tool uses its own default thread count. On this machine
  that is 32 for all of them, confirmed from the tools' logs. `--threads N`
  passes `-threads N` to every stage of every toolset.
- **Options.** No extra options by default. `--vbsp`, `--vvis` and `--vrad`
  take one quoted string of arguments that is added to that stage for every
  toolset, for example `--vrad "-both -final"`.
- **Results.** Everything lands in `perf-results/toolchain-<timestamp>/`
  (or `--out DIR`):
  - `summary.md`: per map, the timing table (with each total's min–max and
    how many runs passed) and the output-check table for the last timed run;
  - `results.json`: every timed run, with each stage's exit code, wall and
    CPU time, and its output checks;
  - `runs/<map>/<toolset>/<n>/`: each chain's per-stage logs. Only the last
    chain's compiled output is kept; the earlier ones keep their logs only.

## How each toolset is run

The script knows six toolsets; `--toolsets` picks a subset.

### `stock`: the Source SDK 2013 compilers

- Source SDK Base 2013 Multiplayer, Steam app 243750, build id 17413218,
  from its `bin/x64` folder. The banners read "Valve Software - vbsp.exe
  (Feb 17 2025)", "vvis.exe (Feb 17 2025)" and "vrad.exe SSE (Feb 17 2025)".
- Found through Steam's `libraryfolders.vdf` and the app's manifest, or
  given with `--stock-bin`.
- Run under Proton Experimental's wine (experimental-11.0-20260924 for these
  numbers), with the prefix `~/.local/share/source-sdk-wineprefix`,
  `WINEDEBUG=-all` and `SteamAppUser` set, from the tools' own folder.
- The commands, with host paths seen through wine's `Z:` drive:

      wine vbsp.exe -game Z:<translated game dir> Z:<work>/<map>.vmf
      wine vvis.exe -game Z:<translated game dir> Z:<work>/<map>.bsp
      wine vrad.exe -game Z:<translated game dir> Z:<work>/<map>.bsp

  with default options.

### `pp`: Tools++

- `vbspplusplus.exe` (banner dated Jun 23 2026), `vvisplusplus.exe`
  (Jun 20 2026) and `vradplusplus.exe` (Jun 22 2026), from a Tools++
  download folder (`--pp-dir`, by default `~/Downloads/tools_plusplus`).
- Its `tools/` executables and `compatibility/` DLLs are copied into one
  folder in the run's output, and the tools run from there: Tools++ loads
  `filesystem_stdio.dll` from its working directory and fails to start
  anywhere else.
- The same wine, prefix and environment as `stock`.
- **vbsp always gets `-matsyscompat`.** Without it, Tools++'s vbsp loads
  materials through a newer material system that cannot read the SDK 2013
  and TF2 textures. Every face then compiles without a lightmap, and the
  result is an unlit map with fewer faces, written quickly and with exit
  code 0. With the flag its face count matches stock's and the map is lit.
- vvis and vrad run with their defaults.
- Options go before the map path, because Tools++'s vbsp reads every
  trailing argument as a map name.

### `ssmap`: this repository on the JIT

- The Release build, run as `dotnet bin/Release/ssmap.dll`, one process per
  stage:

      dotnet bin/Release/ssmap.dll vbsp -game <game dir> <work>/<map>.vmf
      dotnet bin/Release/ssmap.dll vvis -game <game dir> <work>/<map>.bsp
      dotnet bin/Release/ssmap.dll vrad -game <game dir> <work>/<map>.bsp

- Microsoft's .NET runtime, 10.0.12 (`~/.dotnet/dotnet` when present,
  otherwise `dotnet` on the path, or `--dotnet`); ssmap targets `net10.0`.
- The script builds it first (`dotnet build src/SourceSharp.MapTools.slnx
  -c Release`) unless `--no-build` is given.
- Default `-compliance correct`: ssmap's documented fixes to stock
  behaviour are on (see the README's
  [`correct` / `stock` rule](../README.md#the-correct--stock-rule)).
- It reads the original game directory directly, `|appid_N|` search paths
  included.

### `ssmap-aot`: the same code, NativeAOT

- The same source published with NativeAOT, the same publish CI uses for
  its AOT archives:

      dotnet publish src/SourceSharp.MapCompile -c Release -r linux-x64 -p:PublishAot=true -o bin/aot

  and run as `bin/aot/ssmap` with the same arguments. The script publishes
  it first unless `--no-build` is given, for the runtime identifier of the
  machine it runs on.
- This matters here because each stage is its own process, as with the
  stock tools. The JIT build pays .NET start-up and JIT compilation three
  times per chain; the AOT build starts as native code.

### `ssmap-fast` and `ssmap-aot-fast`: the approximate vvis flow

- `ssmap` and `ssmap-aot` with `-fastflow` on vvis only; vbsp and vrad run
  exactly as in the plain sets, so the difference between the rows is vvis's
  (plus whatever vrad gains or loses from the PVS it is handed).
- The approximate flow takes 1000 exact steps per portal walk, then stops
  chains that can only reach clusters the portal already sees. Its PVS is a
  subset of the exact one: it can cull some visible cluster pairs and never
  adds any. The accuracy cost shows in the vis bytes column.

## Game content for the Windows tools

The Windows tools cannot mount `|appid_N|` search paths. They stop with
"Appid based mounting is not supported on non-engine DLL projects". So for
each map the script writes a run-local copy of the game directory's
`gameinfo.txt` for them:

- every search path is made absolute: `|gameinfo_path|` is the game
  directory, `|appid_N|` is that app's install folder (found through Steam's
  library list and the app's manifest), and a plain relative path is taken
  against the base directory, the game directory's parent;
- a path whose folder (or `_dir.vpk`) is not on disk is dropped, and the
  summary lists what was dropped and why;
- write kinds are removed, and a run-local write path is added first, so no
  tool writes anything into `game/`;
- the paths are handed to the tools through wine's `Z:` drive.

ssmap needs none of this: it reads the original `gameinfo.txt` and mounts
`|appid_N|` paths itself. Steam roots are looked for in `~/.steam/steam` and
`~/.local/share/Steam`, or given with `--steam`.

## Output checks

Every run is checked as well as timed. After each chain the script reads the
BSP it wrote and records:

- its size and SHA-256;
- the visibility lump's size;
- the LDR and HDR lighting lumps' sizes;
- how many lines in the three stages' logs report missing content ("not
  found", "couldn't open/load/find", "can't load/find/open").

**A run whose map has no lighting is a failure, not a time.** A tool that
cannot find its content (textures it cannot read, a game directory it
cannot mount) typically does not stop: it writes an unlit map, quickly, and
exits 0. Timed as if it were a result, that would look like the fastest
compile. So a chain whose output has neither LDR nor HDR lighting is marked
failed, whatever its exit code, and is left out of the medians. The same
goes for a stage that exits non-zero; the chain stops there.

The sizes are reported next to the times so an unbalanced comparison is
visible: a toolset that lit fewer faces, or computed a much smaller PVS,
shows it in the same table.

## Caveats

- **Wall time includes start-up.** Each stage's time runs from process start
  to exit, so it includes loading the tool, mounting the game content and
  writing the BSP, as a mapper would experience it.
- **Wine.** stock and Tools++ pay a small wine overhead per process, which
  a native Windows run would not.
- **Tools++'s vvis is non-deterministic.** Its output hash changes from run
  to run; the numbers are for whichever PVS each run produced.
- **Tools++'s PVS is not stock's.** On the same tree, Tools++'s PVS differs
  from stock's and ssmap's by about 700 extra visible cluster pairs and 226
  missing ones, so its vvis time is not a like-for-like comparison.
- **The trees differ.** Each toolset's vbsp writes its own tree (the BSP and
  vis sizes above differ), and vvis and vrad work on that tree. That is
  deliberate, since it is what a mapper gets, but it means a stage's time is
  not measured on identical input across toolsets.
- **One machine.** These are one machine's numbers, from one run of 3
  timed chains. Relative results on a CPU with fewer threads will differ,
  `-fastflow` in particular.

## Running it yourself

You need a Linux machine with Python 3, the .NET 10 SDK, wine (Proton
Experimental's is looked for first), Steam with Source SDK Base 2013
Multiplayer and Team Fortress 2 installed (for the stock tools and 2fort's
content), and a Tools++ download for the `pp` toolset.

    tools/toolchain-bench.sh                                   # every toolset, both default maps
    tools/toolchain-bench.sh --toolsets stock,ssmap-aot        # a subset
    tools/toolchain-bench.sh --runs 5 --warmups 1              # more timed chains
    tools/toolchain-bench.sh --threads 16                      # -threads 16 on every stage
    tools/toolchain-bench.sh --map name=path.vmf:game/mod_x    # another map
    tools/toolchain-bench.sh --dry-run                         # print the commands, run nothing
    tools/toolchain-bench.sh --help                            # every option

Other options point the script at tools in non-default places: `--stock-bin`,
`--pp-dir`, `--wine`, `--wineprefix`, `--steam`, `--dotnet`, and `--out` for
the results folder. `--no-build` uses the existing Release and AOT builds of
ssmap instead of building them.
