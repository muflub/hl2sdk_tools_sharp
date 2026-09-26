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
- Stock mode (`-compliance stock`) reproduces SSE results and needs an x86-64
  CPU; it refuses to run on arm64. The default `correct` mode runs anywhere.

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
`-gpu_slabs <n>` and `--no-write` (compile without writing the map).

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
any capable device), and `-gpu_slabs <n>` sets how many rays go to the GPU
per batch. When no usable device is found, vrad reports that it declined the
GPU and falls back to the CPU KD-tree tracer, so a run never fails for lack
of a GPU.

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

`LibraryRuleTests` and `FileSystemSeamTests` check the built assemblies
against the [design rules](#design-rules), so breaking one fails the suite.

## CI and releases

`.github/workflows/ci.yml` has three jobs:

- **test** builds the solution and runs the suite with .NET 10 on Linux,
  Windows and macOS (Intel, since stock mode needs SSE), on every push to
  `main` and every pull request. Test results are uploaded as artefacts.
- **package** publishes `ssmap` for linux-x64, win-x64, osx-arm64 and
  osx-x64 in two forms: a native AOT executable, and a framework-dependent
  dll build (needs the .NET 10 runtime, with the GPU package staged beside
  it). Both are smoke-run with `ssmap --help` wherever the runner can execute
  them; osx-x64 is cross-built and not run.
- **release** runs on tag pushes and attaches all eight archives
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
