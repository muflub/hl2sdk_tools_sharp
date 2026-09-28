# MapTools

A fully managed compile chain for Source-format maps: a `vbsp` / `vvis` /
`vrad` equivalent in C# (.NET 10), plus the BSP/VMF/VPK readers and writers,
the geometry kernel, and the collision cooker it is built on. No native tool
binaries are invoked; the whole chain runs in process.

The result is one executable, `ssmap`, that takes the same arguments as the
stock tools, runs all three stages in one process with the BSP held in
memory, and adds instruments the stock tools never had: a loader-rule
checker, a lump-by-lump diff, a benchmark harness and an incremental cache.

- [What it does](#what-it-does)
- [Layout](#layout)
- [Requirements](#requirements)
- [Building](#building)
- [Running](#running)
- [Commands](#commands)
- [The `correct` / `stock` rule](#the-correct--stock-rule)
- [Platform differences](#platform-differences)
- [Collision cooking](#collision-cooking)
- [Incremental cache](#incremental-cache)
- [GPU ray tracing](#gpu-ray-tracing)
- [Tests](#tests)
- [CI and releases](#ci-and-releases)
- [Design rules](#design-rules)
- [License](#license)

## What it does

| Stage | Input | Output | Covers |
| --- | --- | --- | --- |
| `vbsp` | `.vmf` | `.bsp`, `.prt`, `.lin` | brush CSG and clipping, BSP tree, portals, leafs, area portals, water, detail brushes, displacements, overlays, static and detail props, face merging and T-junction fixes, collision models, pakfile, BSP writing |
| `vvis` | `.bsp` + `.prt` | `.bsp` (visibility lump) | base vis, portal flow, the potentially-visible set, `-fast` and `-tighten` passes |
| `vrad` | `.bsp` | `.bsp` (lighting lumps) | direct lighting, sky and sun, radiosity bounce, lightmaps (LDR / HDR / both), bump lighting, leaf ambient, static-prop and detail-prop lighting, displacement lighting, cubemaps, ambient occlusion |

`ssmap all` chains the three stages with the BSP kept in memory between them,
so only the final map is written.

## Layout

```
src/
  SourceSharp.MapTools.slnx               the solution
  Directory.Build.props                   net10.0, nullable, CS0108/CS0114 as errors
  SourceSharp.MapFormats/                 file formats; no package or project references
  SourceSharp.MapTools/                   the compile passes (core library)
  SourceSharp.MapCompile/                 the ssmap CLI (assembly name: ssmap)
  SourceSharp.MapTools.Cache.Sqlite/      optional SQLite store for the incremental cache
  SourceSharp.MapTools.Gpu/               optional Vulkan radiosity backend
  SourceSharp.MapGen/                     generated feature catalogue and sandbox map
  SourceSharp.Tests/                      xUnit suite for the whole chain
game/                                     test game directories with gameinfo.txt
maps/ss_sandbox.vmf                       the generated sandbox map
maps/sdk_ctf_2fort.vmf                    Valve's SDK 2fort, a full-size map for perf runs
```

### `SourceSharp.MapFormats`

Readers and writers for every file the chain touches, and nothing else.

- `Bsp/` holds the BSP container, lump structs and lump codecs (including LZMA
  lumps and game-lump variants).
- `Text/` holds the text formats: VMF chunk files and manifests,
  KeyValues (`gameinfo.txt` and friends), VMT materials and patches, `.rad`
  light files, portal files, detail-object files and the surface-properties
  manifest.
- `Geometry/` holds the vector and plane types and the shared epsilons.
- `Numerics/` holds `DetMath` and `DetMathF`, the correctly rounded
  elementary functions every output-affecting computation uses in place of
  `Math` and `MathF` (see [Elementary functions](#elementary-functions)).
- `Assets/` reads studio models, `.phy` files and VTF textures.
- `Zip/` reads and writes the pakfile zip, including LZMA entries.

### `SourceSharp.MapTools`

The compile passes, grouped by stage and by concern.

| Folder | Contents |
| --- | --- |
| `Bsp/` | vbsp: brushes, CSG, tree building, portals, leafs, faces, area portals, water, writing |
| `Disp/` | displacement surfaces, shared by vbsp and vrad |
| `Vis/` | vvis: portal sets, base vis, flow, PVS compression |
| `Rad/` | vrad: `Light/` direct lighting, `Bounce/` radiosity, `Ambient/` leaf ambient, `Displacement/`, `Props/`, `Final/` lightmap packing |
| `Tracing/` | the KD-tree ray tracer vrad casts shadow and transfer rays through |
| `Phys/` | the managed collision cooker (`Managed/`, including a qhull port) and the native vphysics host |
| `Materials/` | VMT lookup and the compile flags materials carry |
| `Io/` | the `IFileSystem` seam, game mounting, VPK and pak archives, Steam library discovery |
| `Options/` | stock argument parsing (`StockArgs`), per-stage options, the compliance catalogue |
| `Compile/` | `MapCompiler`, which runs one or more stages in process |
| `Rooms/` | compile a VMF into a reusable `.room` object and link rooms into one map |
| `Validation/` | `BspValidator`, the loader rules `ssmap check` reports |
| `Compare/` | the lump-by-lump comparer behind `ssmap diff` |
| `Parallel/`, `Diagnostics/`, `Geometry/`, `Vpk/` | work scheduling, warnings and error codes, geometry kernel, VPK reading |

### `SourceSharp.MapCompile`

A thin host over the libraries. It parses stock-spelled arguments, draws the
progress output and the cache report, maps failures to exit codes and wires
Ctrl-C to cancellation. It references the libraries with no
`InternalsVisibleTo`, so anything `ssmap` can do, another host can do too.

## Requirements

- The **.NET 10 SDK** to build, or the .NET 10 runtime to run a
  framework-dependent build. A native AOT build needs neither.
- **Use Microsoft's .NET runtime on Linux.** Ubuntu's packaged
  `libcoreclr.so` (10.0.12) aborts `ssmap vvis` with
  `Internal CLR error (0x80131506)` in roughly one run in seven; Microsoft's
  build of the same version does not. The full measurement is in the comment
  in `src/SourceSharp.MapCompile/SourceSharp.MapCompile.csproj`.
- Game content for real compiles. `-game <dir>` points at a directory with a
  `gameinfo.txt`; its search paths (including Steam `|appid_N|` mounts) are
  resolved through the Steam library folders on the machine. Without that
  content a compile still runs, but missing materials and models become
  warnings.
- An x86-64 or arm64 CPU. Both modes run on both, including Apple Silicon,
  but some results differ in the last bits between CPU families; see
  [Platform differences](#platform-differences). The x86-64 native AOT
  builds need AVX2 (x86-64-v3: Intel Haswell or AMD Zen and later); on an
  older x86-64 CPU use the framework-dependent build, which writes the same
  bytes.

## Building

```sh
cd src
dotnet build SourceSharp.MapTools.slnx -c Release
```

Every project builds into one shared folder at the repo root:
`bin/Release/` or `bin/Debug/`, set once in `src/Directory.Build.props`. The
CLI lands at `bin/Release/ssmap.dll`, with the optional GPU and SQLite
backends and the test assembly beside it.

To publish a self-contained native executable:

```sh
dotnet publish src/SourceSharp.MapCompile -c Release -r linux-x64 -p:PublishAot=true -o out/aot
```

An x64 AOT publish targets x86-64-v3 (AVX2, FMA, BMI). NativeAOT fixes the
instruction set at compile time and its own default is x86-64-v2, which left
vvis on a real map about 17% slower than on v3. The output is the same bytes
either way. For a CPU without AVX2, publish with
`-p:IlcInstructionSet=x86-64-v2`.

Under AOT the GPU backend is linked in statically. Otherwise it is loaded
by name at run time from the folder `ssmap` runs from. A solution build puts
it there already; a `dotnet publish` of `ssmap` alone does not, so publish
`SourceSharp.MapTools.Gpu` to the same folder if you want it.

## Running

```sh
cd src
dotnet run --project SourceSharp.MapCompile -c Release -- <command> [options]
```

or run the built `ssmap` directly. `ssmap -h` prints the full usage.

The everyday forms:

```sh
ssmap vbsp <map> -game <dir> [-threads <n>]
ssmap vvis <map> -game <dir>
ssmap vrad <map> -game <dir> [-final] [-hdr|-ldr|-both]
ssmap all  <map> -game <dir>          # vbsp + vvis + vrad, one process
ssmap diff <a.bsp> <b.bsp>            # lump-by-lump comparison
ssmap check <map.bsp> [...]           # would the engine's loader accept it?
ssmap compliance                      # the quirk switches per tool
```

`-game <dir>` names the directory holding `gameinfo.txt` and the map. Each
stage also accepts its stock-compatible argument spelling, so existing
compile scripts work by swapping the executable name.

Exit codes: `0` success, `1` a compile failure the program understood (a map
error or running out of memory), `2` a command line it could not act on or a
cancelled run, `70` an internal error.

## Commands

### `vbsp`, `vvis`, `vrad`

Each takes the stock tool's options. Accepted flags include:

- **vbsp:** `-onlyents`, `-onlyprops`, `-nodetail`, `-fulldetail`, `-nowater`,
  `-noweld`, `-nocsg`, `-noshare`, `-notjunc`, `-noopt`, `-noprune`,
  `-nomerge`, `-micro`, `-leaktest`, `-block(s)`, `-luxelscale`,
  `-minluxelscale`, `-embed`, `-replacematerials`, `-nodrawtriggers`,
  `-bspformat`, `-lightformat`, `-staticpropformat` and the rest of the stock
  set, plus `-cooker`, `-vphysics`, `-compliance`, `-incremental`,
  `-cache-dir` and `-nocache`.
- **vvis:** `-fast`, `-nosort`, `-radius_override`, `-trace`, `-threads`,
  `-low`, `-tmpin`, `-compliance`.
- **vrad:** `-hdr`, `-ldr`, `-both`, `-fast`, `-final`, `-extrasky`,
  `-bounce`, `-smooth`, `-chop`, `-maxchop`, `-dispchop`, `-softsun`,
  `-StaticPropLighting`, `-StaticPropPolys`, `-textureshadows`,
  `-ambientocclusion` (with `-aoradius`, `-aoscale` and friends),
  `-lights`, `-scale`, `-ambient`, `-threads`, `-compliance`, `-gpu`,
  `-gpu_slabs`.

The authoritative list for each stage is the parser in
`src/SourceSharp.MapTools/Options/StockArgs.cs`.

### `all`

```sh
ssmap all [chain options] <map> [--vbsp ...] [--vvis ...] [--vrad ...]
```

Runs the three stages in one process with the BSP in memory. Each
`--vbsp` / `--vvis` / `--vrad` section takes that stage's stock options.
Chain options apply to every stage: `-game`, `-threads`, `-compliance`, `-v`,
`-fast`, `-tighten`, `-loose`, `-cooker`, `-vphysics`, `-listcompliance`,
`-nocache`, `-incremental`, `-cache-dir <dir>`, `-gpu <match>`,
`-gpu_slabs <n>`, `--no-write` (compile without writing the map) and
`--record-content <zip>`.

`--record-content <zip>` records every game file the compile looked up and
writes the ones it found to a zip, whether the compile succeeds or fails part
way. The zip is a game directory:

- every file the compile read, or only checked the existence of, at its
  content path (`materials/...`, `models/...`, `lights.rad`, ...), copied
  from whichever search path won and checked against the bytes the compile
  saw;
- a `gameinfo.txt` that is the game's own with its `SearchPaths` replaced by
  `game+mod |gameinfo_path|.`, so `SteamAppId` and the `Tools` block (which
  pick the BSP format) are unchanged;
- a `lights.rad` taken from beside the tool or a Steam `bin` folder when the
  game's search paths had none; the map's `.rad` and a `-lights` file go
  under `loose/`;
- `content-manifest.txt`: the command line, then one line per path with its
  kind (`read`, `resolved`, `missing` or `loose`), SHA-256, size and the
  search path it came from. Lookups that found nothing are listed here only.

Unzip it anywhere and compile against it with no Steam install:
`ssmap all <map> -game <unzipped dir>`.

### `room` and `link`

```sh
ssmap room <in.vmf> [-out <dir>] [-def <roomdef.json>] [vbsp options]
ssmap link <layout.json> [-rooms <dir>] [-out <map.bsp>]
```

`room` compiles one room's VMF into `<dir>/<name>.room`. The room definition
is read from the sidecar next to the VMF (`<base>.roomdef.json`) unless
`-def` names another. `link` joins every `*.room` in a directory into one
map, following a `layout.json` that names the rooms, cells, joints and caps.
Linking needs no game directory.

A room name is one path segment (no separators, no `..`). A placement's
`rotation` is a count of quarter turns, 0 to 3. Every room is compiled
sealed, with a plug brush in each socket; the link removes the plug at a
joined socket (the doorway becomes open space, drops out of the world
collision and its faces stop drawing) and keeps it at a capped one. The
rooms' world collision, entities and areas are merged into the map's own.
The link refuses what it cannot carry: area portals, static or detail
props, packed files, displacements, water, and a mix of cooked and
`-cooker none` rooms. The doorway's side walls have no faces of their own,
because in the room's compile they faced the plug, so they draw as a gap
unless something placed in the socket (a door frame model, say) covers
them.

### Instruments

| Command | What it answers |
| --- | --- |
| `check <map.bsp> [...]` | Does the engine's loader accept this map? Prints each broken rule with its diagnostic code and exits non-zero if any fired. |
| `diff <a.bsp> <b.bsp>` | Which lumps differ between two maps, and how. |
| `bench` | Timed compiles across maps, stages, options and thread counts, reporting wall time, CPU time, peak RSS, GC pause and per-stage timings. |
| `compliance [vbsp\|vvis\|vrad]` | The stock quirks `-compliance` can switch, per tool. |

### `cache`

```sh
ssmap cache stats|explain|gc|clear|check <store-or-map>
```

Inspects and maintains the incremental cache. See
[Incremental cache](#incremental-cache).

### `phys`

```sh
ssmap phys list              # the vphysics libraries this machine has
ssmap phys select <game>     # choose one for native cooking
ssmap phys cook <game>       # load one and cook a test cube
```

## The `correct` / `stock` rule

Some reference behaviours are CPU-dependent, order-dependent, or simply
wrong, and a fix would change the output bytes. Every one of these is a
named entry in a central compliance catalogue and is governed by one
command-line rule:

    -compliance correct                       # the fixed behaviour (default)
    -compliance stock                         # every reference behaviour
    -compliance correct,+EdgeBevelNormalise   # one quirk on the stock side

`-listcompliance` prints the catalogue: each entry gives the quirk's name
and a short title, the tools whose output moves, what the reference does,
what the correct side does instead, and how the difference was observed
(measured against stock output, demonstrated by facts, or read from the
reference only). A build-time fact checks the built assembly: every method
listed as deciding a quirk must actually pass that quirk to the compliance
check, and every fact an entry cites must still exist, so the ledger cannot
drift from the code.

## Platform differences

Stock's arithmetic starts from hardware ESTIMATES of `1/x` and `1/sqrt(x)`
rather than exact values, and refines them with a Newton step. The estimate
instructions are not the same everywhere, so neither are the results that
depend on them:

| CPU | Estimate instructions | Results |
| --- | --- | --- |
| AMD x86-64 | SSE `rcpss` / `rsqrtss` | Stock's own. The committed stock goldens were cut from the reference tools on an AMD Ryzen 9 9950X. |
| Intel x86-64 | SSE `rcpss` / `rsqrtss` | Intel's estimate differs from AMD's in the low bits, as it does for the stock tools. |
| arm64 (Apple Silicon, Linux arm64) | ARM `frecpe` / `frsqrte`, each refined once with `frecps` / `frsqrts` | Stock's algorithm with ARM's estimate. The stock tools never ran on ARM, so there is nothing to match, only the same arithmetic. ARM defines these instructions exactly, so every arm64 CPU should give the same bits. |

The ARM estimates are refined once before stock's own Newton step because
they carry about 8 bits against x86's 12. Stock's step was written for a
12-bit start; the extra refinement gives it at least that, so arm64 results
are as accurate as x86's.

What moves between the rows:

- **`-compliance stock`**: every quantity downstream of a stock normalise or
  reciprocal. This includes plane distances, displacement normals, cooked
  collision data, leaf ambient and static-prop lighting. On one CPU family
  the output is still deterministic from run to run.
- **`-compliance correct`** (the default) uses exact IEEE arithmetic in
  place of these estimates, with one exception: vrad's KD-tree ray tracer
  keeps stock's estimated reciprocal in its traversal in both modes. A ray
  that grazes a tree split can resolve differently from one CPU family to
  another, which changes a shadow test at the edge of an occluder.

Everything else is the same on every platform: file formats, the vbsp tree,
vvis, every exact computation, and every elementary function.

### Elementary functions

`sin`, `cos`, `tan`, `asin`, `acos`, `atan2`, `pow` and `log` do not come
from the platform's C library, whose last bits differ between glibc, the
Windows UCRT and macOS's libSystem. They come from
`SourceSharp.MapFormats.Numerics`: `DetMath` for double, `DetMathF` for
float. Each returns the correctly rounded result, the representable value
nearest the true one (ties to even). That value is unique, so it is the same
on every OS and CPU, and it matches any C library wherever that library is
right.

Each function first evaluates in double precision with a bounded error and
rounds when the whole error band rounds to one value. For about one float
result in a million, and for every double result, it falls back to
arbitrary-precision interval arithmetic, which always decides. A float
function costs a small multiple of `MathF`'s; a double function costs tens of
microseconds, which is why the per-luxel gamma uses `DetMath.PowToSingle`
(the bits of `(float)DetMath.Pow`, at float cost). A fact scans the built
libraries and fails on any call to `Math.Sin`, `MathF.Pow` and the like.

This holds under both policies. The reference tools took these functions from
Microsoft's C runtime, which no other runtime reproduces bit for bit. Before
this library the port used the host's C library in both policies, so its
output depended on the OS wherever that library misrounds; glibc 2.39's
`asinf`, for one, misrounds about one argument in ten in `[0.5, 1)`, and its
`atan2f` one in five near 1. Stock mode now computes these correctly rounded
too: it matches the reference wherever Microsoft's runtime is correctly
rounded, and it is the same everywhere.

The test suite reflects this. Facts that compare a stock-estimate result
bit for bit take their expected values per CPU family. AMD compares against
the stock goldens themselves. Intel and arm64 compare against delta files
under `src/SourceSharp.Tests/Fixtures/rsqrt-vendor/`. Those deltas were
recorded from this port's own output on that hardware, so they catch
regressions but are not evidence of parity with stock. On a CPU family with
no delta files, those facts skip with a reason. CI records deltas with a
manual run of the workflow with **capture** ticked; see
[CI and releases](#ci-and-releases).

## Collision cooking

vbsp writes collision models for the world and every brush entity. Two
cookers can build them, chosen with `-cooker`:

| `-cooker` | What it does |
| --- | --- |
| `managed` (default) | A C# cooker that loads no native library. It matches the native cooker byte for byte on the real-map gate compiles. |
| `native` (alias `vphysics`) | Loads a game's own vphysics library and cooks through it. `-vphysics <game\|path>` picks which one; the default is `source-sdk-base-2013-multiplayer`. |
| `none` | Writes no physics lumps. |

Different vphysics builds cook the same shape to different bytes, so the
physics lump depends on which game's library was used. `ssmap phys list`
shows what is available.

## Incremental cache

`-incremental` stores cooked collision models in a SQLite database next to
the map (`<map>.sscache.db`, or under `-cache-dir`). A later compile reuses
every model whose inputs have not changed. Brush models are keyed by their
content, so adding or moving one brush does not invalidate the others.
`-nocache` turns the cache off for one run. The cache needs a cooker; with
`-cooker none` there is nothing to store.

`ssmap cache` reads the same file: `stats` summarises it, `explain` shows
what a key was built from, `gc` trims it, `clear` empties it and `check`
verifies it.

The SQLite backend lives in its own assembly so the core libraries carry no
package references. If it cannot be loaded, `ssmap` says so instead of
silently compiling without a cache.

## GPU ray tracing

`SourceSharp.MapTools.Gpu` is an optional Vulkan ray tracer (via Silk.NET)
for vrad. It is off unless asked for: `-gpu <match>` turns it on and picks
the first capable device whose name contains `<match>` (an empty match takes
any capable device), and `-gpu_slabs <n>` sets the ray budget for the
batches ("slabs") on the GPU. The tracer keeps three slabs in flight, so the
GPU traces one while the next waits behind it and the CPU packs or unpacks a
third, and each slab holds a third of the budget (the default, 4,194,304
rays, is 128 MB of rays in all). Where the device allows it (integrated
GPUs, and discrete GPUs with resizable BAR), rays are written straight into
memory the GPU reads, skipping the upload copy. When no usable device is
found, vrad reports that it declined the GPU and falls back to the CPU
KD-tree tracer, so a run never fails for lack of a GPU.

## Measuring performance

    tools/compile-perf.sh --map maps/ss_sandbox.vmf --game game/mod_sharp

compiles the map across a matrix of the settings that change how fast the
tools run, then profiles each combination. The axes are in
`tools/compile-perf-matrix.json`:

- the build (JIT or NativeAOT);
- the GC and JIT runtime settings;
- the thread count;
- `-compliance`;
- the collision cooker;
- `-overlap`;
- the incremental cache (off, cold, warm);
- the ray tracer (CPU or GPU);
- vbsp, vvis and vrad presets (for example `-fast`, `-final`, `-both`, `-bounce 0`).

Each axis only applies to the stages it affects.

`maps/ss_sandbox.vmf` is small and exercises every feature; for numbers
closer to a real map, use `maps/sdk_ctf_2fort.vmf`, Valve's SDK 2fort
(18,000 faces, 2,500 vis clusters):

    tools/compile-perf.sh --map maps/sdk_ctf_2fort.vmf --game game/mod_tf

It needs Team Fortress 2 installed through Steam for its materials, models
and `lights.rad`. `--synthetic` only makes stand-ins for the sandbox map's
materials, so without Steam 2fort still compiles, but with its brushes
unlit by texlights and thousands of missing-material warnings.

The whole chain is one stage and vbsp, vvis and vrad are each timed on their
own. vvis starts from the baseline vbsp's output and vrad from the baseline
vvis's, so a tool's numbers do not depend on the other tools' settings.

`--matrix` picks how many combinations run:

- `pairwise` (the default) runs enough cells that every pair of setting
  values meets in at least one of them.
- `sweep` runs the baseline and each value on its own.
- `full` runs every combination. That is thousands of chain cells, so
  narrow it with `--set axis=v1,v2` first.
- `--dry-run` lists the cells and stops.

`--quick` is for a full-size map, where the default matrix takes hours (on
2fort, 86 cells at two to four minutes each). It still covers every stage:

- each setting is run once on its own (`sweep`, 53 cells);
- each cell gets one timed run and no warm-up;
- only each stage's baseline is profiled, with the stage times, rusage, CPU
  and GC profilers.

That is about 70 tool runs instead of about 860. Any of those options given
explicitly still wins, for example `--quick --runs 3`.

Each cell is timed with `ssmap bench`, then run again once per profiler so
that no profiler's overhead lands in another's numbers:

- vvis and vrad `--bench` stage times;
- process resource usage, with `perf stat` hardware counters when `perf` is
  installed;
- a sampled CPU profile (speedscope);
- runtime events: GC pauses by generation, allocations by type and by the
  SourceSharp method that made them, lock contention, thread pool
  starvation, exceptions and JIT, read by `tools/PerfTraceReport`;
- `dotnet-counters` once a second;
- periodic heap snapshots, keeping the largest;
- optionally `perf record` with native and managed frames together.

The profilers need `dotnet tool install -g dotnet-trace dotnet-counters dotnet-gcdump`.

Everything lands in `perf-results/<timestamp>/`:

- `summary.md`:
  - every cell against its baseline;
  - what each setting does;
  - thread scaling;
  - vvis and vrad stage breakdowns;
  - the hot functions, allocation sites and GC costs across the matrix.
- `cells/<cell>/report.md` and the raw captures beside it.
- `cells.csv`.
- `env.txt`, which records the machine, the revision and the .NET runtime.

Other switches:

- `--gpu <match>`, `--vphysics <game>` and `--aot` enable the values that need
  a GPU, a native vphysics library or a NativeAOT build.
- `--strip-steam` mounts a copy of the game without its Steam search paths.
- `--resume` continues an interrupted run.

`tools/compile-perf.sh --help` lists every option.

### Without the Steam content

`--synthetic` (with `--strip-steam` on a machine without the game) adds
generated stand-ins for what ss_sandbox mounts from Steam. They go into the
script's own copy of the game, never into `game/`. `--static-props` compiles
a variant of the map with a `prop_static` beside each model entity, because
the map itself has none. The same content can be written anywhere with:

    dotnet run --project tools/SyntheticContent -c Release -- --content <game dir> [--props-map in.vmf out.vmf]

The content is built by `SourceSharp.MapGen.Content.SyntheticContent`, with
writers for VTF and studio models. It is chosen to exercise the branches the
real content would:

- **Materials:** every material the map's brushes use, with its compile keys.
  - Tool textures: `%compilesky`, `%compiletrigger`, `%compilenodraw` and the like.
  - Water, with a `$bottommaterial`.
  - A translucent window.
  - Bump-mapped and `$envmap` surfaces.
  - An explicit `$reflectivity`.
  - A `%detailtype` floor.
- **Textures:** VTFs whose reflectivity comes from their pixels, and six
  skybox faces the default cubemap is built from.
- **Other files:** `lights.rad` with a texlight, a surface-properties table,
  and `detail.vbsp`.
- **Models:** the eight models the map names, with real MDL, VVD, VTX and PHY
  geometry.
  - Six are static props. One of them casts texture shadows and one has two LODs.
  - One is not `$staticprop`.
  - One is `allowstatic 0`.

Existing files are never overwritten, so `--synthetic` on an installed game
only fills in what is missing.

### Bundling a map's game content

    tools/bundle-content.sh [--map maps/sdk_ctf_2fort.vmf] [--game game/mod_tf] [--out <zip>] [--threads <n>]

runs `ssmap all --record-content` on the map once for each of a set of flag
combinations (the default compile, and vrad's `-StaticPropLighting
-StaticPropPolys` in `-ldr`, `-hdr` and `-both -final`, with
`-textureshadows`), then merges the zips into one. It is how to hand someone
the exact Steam content a compile reads, so they can reproduce it without the
game. The compiles run with `--no-write`, so nothing is written beside the
map or into the game; the per-run zips go to a temporary directory. The
default output is `content-bundles/<map>-content.zip`. It builds ssmap in
Release first when the build is missing or older than the sources, and needs
only bash, python3 and dotnet. The combinations are `COMBOS` in
`tools/bundle_content.py`.

## Tests

```sh
cd src
dotnet test SourceSharp.Tests/SourceSharp.Tests.csproj -c Release
```

The suite has more than 4,600 xUnit facts. Its folders mirror the
libraries (`MapFormats/`, `MapTools/Bsp`, `MapTools/Vis`, `MapTools/Rad`, and
so on), with fixture data under `Fixtures/` and next to the tests that use
it.

Some facts depend on things that are not always present, and skip with a
stated reason when they are missing:

- facts that compare against reference outputs skip when the reference
  corpus is not present;
- `[SandboxMapFact]` skips when the generated sandbox map is not in the tree;
- `[RepoSourceFact]` skips when the source files it checks are not above the
  test binary.

The rest run everywhere.

`LibraryRuleTests`, `FileSystemSeamTests` and `DeterministicMathRuleTests`
check the built assemblies against the [design rules](#design-rules), so
breaking one fails the suite.

The elementary-function facts under `MapFormats/Numerics` compare bit
patterns, not tolerances, against a table of correctly rounded results that
`tools/detmath_goldens.py` computes with mpmath; since those results are
unique, the same table must pass on every CI runner. Others walk every float
in chosen binades, measuring the fast tier's error bound and checking each
result is correctly rounded.

## CI and releases

`.github/workflows/ci.yml` has three jobs:

- **test** builds the solution and runs the suite with .NET 10 on Linux and
  Windows (both AMD runners), and on macOS on both Intel and Apple Silicon.
  The Intel Mac runner stays although osx-x64 is no longer packaged: it is
  the only Intel CPU in CI, and so the only place the Intel delta files for
  `-compliance stock` are checked and captured. Tests run
  on every push to `main` and every pull request. Test results are uploaded
  as artefacts. Running the workflow by hand with **capture** ticked
  re-records each runner's per-CPU delta files (see
  [Platform differences](#platform-differences)) and uploads them as
  `rsqrt-vendor-<os>` artefacts to review and commit.
- **package** publishes `ssmap` for linux-x64, win-x64 and osx-arm64 in
  two forms: a native AOT executable, and a framework-dependent dll build
  (needs the .NET 10 runtime, with the GPU package staged beside it). Both
  are smoke-run with `ssmap --help`. There is no osx-x64 archive; Intel Macs
  can build from source.
- **release** runs on tag pushes and attaches all six archives
  (`ssmap-<tag>-<rid>-aot` and `-dll`) to that tag's GitHub release,
  creating the release if needed.

## Design rules

These hold across the libraries, and most are enforced by tests on the built
assemblies rather than by review.

- **All file access goes through `IFileSystem`.** Only `PhysicalFileSystem`
  (and the memory-mapping helper it uses) may touch `System.IO.File`,
  `Directory`, `FileStream` and friends, the temp path or the current
  directory. Tests use `InMemoryFileSystem`.
- **No mutable static state** in `MapFormats` or `MapTools`, so two compiles
  can share one process.
- **Same output on every platform.** The same map, game content and
  options produce the same bytes on Linux, Windows and macOS, on any .NET
  runtime. The only differences allowed are the CPU-estimate ones listed
  under [Platform differences](#platform-differences): stock's `rcpss` /
  `rsqrtss` arithmetic under `-compliance stock`, and the KD-tree
  traversal reciprocal. The opt-in paths that hand work to code outside
  this repository, `-gpu` (the device's ray intersection) and
  `-cooker native` (the game's vphysics library), are outside the rule.
  Any other difference between platforms is a bug.
- **No platform math.** This is how the rule above is kept. Elementary
  functions go through `DetMath` and `DetMathF`, never `Math.Sin`,
  `MathF.Pow` and the like, so output does not depend on the OS's C
  library. A fact scans the built libraries and fails on any such call.
- **No package references** in `MapFormats` or `MapTools`. SQLite and
  Silk.NET live only in the optional `Cache.Sqlite` and `Gpu` assemblies.
- **Every public async method takes its `CancellationToken` last.**
- **Libraries never touch the console or the environment.** They write no
  `Console` output, read no environment variables and never call
  `Environment.Exit`; the host does all of that.
- **Every behaviour that deliberately differs from the reference is a
  compliance quirk**, switchable with `-compliance`.
- **The primary target is a long-lived service on Linux** that runs many
  compiles in one process, in sequence and concurrently, without
  restarting. Each compile releases
  everything it acquired, even on failure or cancellation. Windows and macOS
  are built and tested in CI.

## License

MIT; see [LICENSE](LICENSE). The source files carry the Source SDK 2013
header, as the tools are based on it. Third-party notices for the bundled
game data are in `game/thirdpartylegalnotices.txt`.
