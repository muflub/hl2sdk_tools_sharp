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
  SourceSharp.RoomContracts/              the Source Sharp mod entity contract (logic_room, cxry_ names); no references
  SourceSharp.MapTools/                   the compile passes (core library)
  SourceSharp.MapCompile/                 the ssmap CLI (assembly name: ssmap)
  SourceSharp.MapTools.Cache.Sqlite/      optional SQLite store for the incremental cache
  SourceSharp.MapTools.Gpu/               optional Vulkan radiosity backend
  SourceSharp.MapGen/                     generated feature catalogue and sandbox map
  SourceSharp.Tests/                      xUnit suite for the whole chain
game/                                     test game directories with gameinfo.txt
maps/ss_sandbox.vmf                       the generated sandbox map
maps/sdk_ctf_2fort.vmf                    Valve's SDK 2fort, a full-size map for perf runs
samples/rooms-3x3/                        the rooms sample: a five-room library and 3x3 level files
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
- `Nav/` reads and writes the `.nav3d` level navigation file
  ([`docs/nav3d-format.md`](docs/nav3d-format.md)); the game mod references
  this assembly for `Nav3dReader`, which answers from the file's bytes
  without allocating.

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
| `Rooms/` | split a room library VMF into rooms, compile them side by side into one `.roompack`, read and generate level files, and link or flatten a level into one map |
| `Nav/` | the 3D navigation: build a room's clearance grid (exact for any agent box), store it in the pack, stitch a level's `.nav3d` at link, and report on it |
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
  but under `-compliance stock` some results differ in the last bits between
  CPU families; see
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
  `-low`, `-tmpin`, `-compliance`, and this port's own `-fastflow[=N]` (below).
- **vrad:** `-hdr`, `-ldr`, `-both`, `-fast`, `-final`, `-extrasky`,
  `-bounce`, `-smooth`, `-chop`, `-maxchop`, `-dispchop`, `-softsun`,
  `-StaticPropLighting`, `-StaticPropPolys`, `-textureshadows`,
  `-ambientocclusion` (with `-aoradius`, `-aoscale` and friends),
  `-lights`, `-scale`, `-ambient`, `-threads`, `-compliance`, `-gpu`,
  `-gpu_slabs`, `-gpu_depth`.

The authoritative list for each stage is the parser in
`src/SourceSharp.MapTools/Options/StockArgs.cs`.

Every stage mounts the game's content from `gameinfo.txt` (the `-game`
directory, or the directory above the map's `maps/` folder), and a game that
cannot be mounted fails the compile with exit code `1`, as it does in the
stock tools: no `gameinfo.txt` there, an `|appid_N|` search path whose app is
not installed, or no Steam library to look it up in. `vrad` alone takes
`--no-game-content` to light anyway: the mount failure becomes a note and the
map is lit with only the level's `.rad` and the `-lights` file, without any
material's reflectivity, the game's `lights.rad` texlights or prop models.
The result is not the compile the stock tool would produce, which is why it
has to be asked for.

#### The fast vvis flow (`-fastflow[=N]`)

`ssmap vvis -fastflow` (or `ssmap all <map> --vvis -fastflow`) runs a faster
portal flow whose PVS is knowingly approximate, in the spirit of Tools++'s
vvis. It is off by default, and without it the output is byte-identical to
a compile that does not know the flag. vvis prints a one-line warning
(`VVIS0701`) naming the step count when it is on.

The exact flow stops following a chain of portals only when the chain can
mark no portal it has not marked already. The fast flow stops as soon as
everything the chain could still reach leads into clusters the portal
already sees, which leaves that portal's own PVS row as it was. The
shortened portal vectors are what the portals flowed after it prune with,
though, so those prune chains the exact flow keeps, and lose clusters.

`N` is how many exact steps each portal's walk takes before it may stop
early. `-fastflow` alone is `-fastflow=1000`. Walks shorter than `N` stay
exact, and they are the cheap portals every longer walk prunes with, so a
larger `N` is slower and loses less. `-fastflow=0` stops everywhere. `N` is
any whole number from 0 up; anything else is a usage error. Given twice, the
last one wins.

Measured on 2fort (main's correct-mode vbsp tree: 2492 clusters, 6367
portals). Pairs lost are counted out of 578,581 visible cluster pairs before
the symmetric pass and 574,014 after it. CPU is user seconds on a loaded
4-core box, so read it as a ratio:

| flow | portal-flow chains | CPU-s | pairs lost (before / after the symmetric pass) |
|---|---:|---:|---:|
| exact (no flag) | 146.3M | 122 | 0 / 0 |
| `-fastflow=0` | 61.5M | 53 | 33,432 / 60,786 (5.8 % / 10.6 %) |
| `-fastflow` (`=1000`) | 71.1M | 62 | 9,599 / 17,622 (1.7 % / 3.1 %) |
| `-fastflow=5000` | 91.6M | 79 | 3,980 / 7,286 (0.7 % / 1.3 %) |
| `-fastflow=20000` | 114.3M | 98 | 1,186 / 2,156 (0.2 % / 0.4 %) |
| `-fastflow=50000` | 128.4M | 108 | 540 / 1,036 (0.1 % / 0.2 %) |

- **Only ever fewer clusters.** At every `N` the PVS is a subset of the
  exact one: the flag can cull geometry that is in view, and never adds
  overdraw.
- **Deterministic.** Each portal is flowed whole on one thread, once every
  portal it prunes with has finished, so the output is the same at every
  thread count and on every run (Tools++'s is not).
- **Against Tools++.** On the same tree Tools++ does about 2.5 times less
  work than the exact flow, misses 226 of stock's pairs and adds about 700:
  more accurate than `-fastflow` at a similar speed.

Use it to iterate on a layout, and compile without it for a release. `-fast`
still skips the flow altogether and wins when both are given; under
`-loose` (which prunes with no other portal's vector) the early stop is
exact and only saves work. The mechanism and the other variants measured on
the way (a depth limit instead of a step count; publishing a conservative
vector, which keeps every pair but saves only 6 %) are described on
`VisClusterStop` in `src/SourceSharp.MapTools/Vis/`.

### `all`

```sh
ssmap all [chain options] <map> [--vbsp ...] [--vvis ...] [--vrad ...]
```

Runs the three stages in one process with the BSP in memory. Each
`--vbsp` / `--vvis` / `--vrad` section takes that stage's stock options.
Chain options apply to every stage: `-game`, `-threads`, `-compliance`, `-v`,
`-fast`, `-tighten`, `-loose`, `-cooker`, `-vphysics`, `-listcompliance`,
`-nocache`, `-incremental`, `-cache-dir <dir>`, `-gpu <match|auto>`,
`-gpu_slabs <n>`, `-gpu_depth <n>`, `--no-write` (compile without writing the map) and
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

### `room`, `rooms`, `link` and `layout`

```sh
ssmap room <library.vmf> [-out <pack.roompack>] [-nav-turn0] [-nav-codec <codec>]
           [-incremental [-cache-dir <dir>] | -nocache] [vbsp options]
ssmap rooms <library.vmf> [-rooms <pack.roompack>]
ssmap rooms -rooms <pack.roompack>
ssmap link <level.yaml> [-rooms <pack.roompack>] [-entity-reserve <n>] [-mod-entities] [-nofold] [-nodoorvis] [-out <map.bsp>] [-no-nav | -require-nav] [-nav-codec <codec>]
ssmap link <level.yaml> --flatten [-mod-entities] [-out <map.vmf>]
ssmap layout <library.vmf> -rows R -columns C -seed N [-empty <ratio>]
             [-rooms <pack.roompack>] [-entity-budget <n>] [-mod-entities] [-out <level.yaml>]
ssmap nav <map.nav3d | level.yaml> [-rooms <pack.roompack>] [--obj <out.obj>] [--floor] [--agent <index|name>]
```

`ssmap rooms` lists a library without compiling it: each room's name, its
cell's corner and size, and each door's wall, plug box (in library
coordinates) and size. It reads and checks the library exactly as
`ssmap room` does, so a library it lists is one the compile accepts. When
the library's pack is there (`-rooms`, else `<library>.roompack` beside
it), the listing opens with the library's entity budget and gives each
room's entities as the room compile counted them: how many reach a linked
map, and how many of those take an edict.

A room pack is a function of its inputs: the same library and `ssmap`
build write the same bytes at any `-threads` and on every run, and so does
each room inside it. (The work counters and deepest flow that `ssmap vvis`
reports depend on the schedule, so a room does not store them.)

**Incremental room compiles.** With `-incremental`, `ssmap room` keeps each
room's finished pack sections in the same SQLite store `ssmap all
-incremental` uses (`<library>.sscache.db` beside the library, or in
`-cache-dir <dir>`; `-nocache` turns it off again for one run). A room whose
inputs have not changed since a run that stored it is copied from the store
instead of compiled, and the log says `reused` for it and ends with
`N compiled, M reused`. The pack is byte for byte the pack a run without
`-incremental` writes, pack id included (the id is derived from the library
and the options, never from the rooms). A room's key holds:

- the room as the split hands it to the compile: its room-local VMF (the
  library's `versioninfo` and worldspawn keys, its own brushes and entities,
  in its own order) written back out, and its `info_room` claims (name,
  cell, door kit, sockets, `room_role`). Whitespace, editor chunks, other
  rooms and the order of other rooms' entities do not count, and neither
  does the library's `mapversion` (the editor's save counter): every room
  is compiled with `mapversion` 0, the pack's `LOPT` section keeps the
  library's value, and `ssmap link` writes it into the linked worldspawn,
  so the linked map is what it was and a save that changes nothing else
  recompiles no room;
- the vbsp options after the format pipeline (every one but `-v` and
  `-verboseentities`), the library's navigation keys, `-nav-turn0` and
  `-nav-codec`, and `rooms_name_keys`;
- the collision cooker and format preset, and the `ssmap` build;
- the game content the room compile read, found or looked for and missed,
  checked against the content as it is now each time a room is reused.

The library-only settings (`rooms_entity_reserve`, `rooms_fold_logic`) and
the library-wide entities are link inputs: editing them rewrites those
sections and reuses every room. Rows are written only after the pack is, so
a cancelled or failed run leaves the store as it was; several runs may
share one store file. On the 256-room stress library (4 cores), a clean
compile takes about 5 s, a recompile with nothing changed about 1.7 s, and
one with one room edited about 2.1 s; the store holds about 44 MB beside
the 40 MB pack.

`ssmap rooms -rooms <pack>` without a library prints the pack's section
table: each library and room section's tag, offset, stored length, codec,
decoded length, revision and a SHA-256 prefix, so two packs can be compared
section by section.

A **room library** is one VMF holding every room of a set, each in its own
cell with gaps between them, and each marked by an `info_room` point entity
at the cell's low corner (least x, y and z). Its keys:

| Key | Meaning |
| --- | --- |
| `name` | The room's name: letters, digits, `_`, `-` and `.`, starting with a letter, digit or `_`. It names the room in the pack and is what a level calls the room. |
| `cell_size` | The cell's edge; the cell is a cube. |
| `door_width`, `door_height` | The door opening, centred on a wall. |
| `wall_depth` | The shell's thickness, and how deep a door plug reaches in from the cell face. |
| `socket_east`, `socket_west`, `socket_north`, `socket_south` | Optional names for the sockets; the default is the wall's name. East is +x, north is +y. |

Everything inside a cell belongs to its room. Point entities in the gaps
are ignored, except the level-wide ones (see **Level-wide singletons**
below) and a `sky_camera`, which is refused; a brush in the gaps or across a cell's edge, overlapping
cells, rooms of different grids or kits, and a door a standing player
(32 x 32 x 72) cannot walk through on the floor are errors that name the
problem. A room's sockets are its door plugs: a world brush exactly filling
the kit's opening on a wall, `wall_depth` deep, made of a `%compileTrigger`
material (still solid, so the room compiles sealed); a trigger brush of any
other size is refused.

`room` splits the library, moves each room to the origin, compiles every
room, and writes them all into one room pack, `<library>.roompack` beside
the library by default (`-out` names another file). Rooms compile side by
side: `-threads` (default: every core) is how many run at once, all on one
shared set of threads, so it is also the most threads the whole run uses.
The log has one line per room in library order, whatever order they
finish in, and the pack holds the rooms in library order too, so neither
depends on `-threads`. One room that fails does not stop the others: it is
reported in its place, the pack holds every room that compiled, and the
exit code says whether any failed. The pack is written once every room has
ended, and replaces the previous one in one step, so a run that is
cancelled or crashes leaves the old pack (or none), never a partial one.

The pack starts with an index of its rooms, so `link` reads the index and
the rooms its level places and nothing else of the file. Each room in it is
the room container `ssmap` has always written for a room; the format
(`RoomPack` in `Rooms/`) has room for more per room and per library, and
a build that does not know a later section reads around it. The pack is at
format version 3, which promises that every room was checked against the
library's singletons when it was built and that every room's pak holds what
vbsp packed for it, the default cubemaps built from the library's sky
included; an older pack is refused with a message to recompile the library
with `ssmap room`.

A **level** is a YAML file:

```yaml
library: ../rooms.vmf        # the room library, relative to this file
rows: 2                      # south to north
columns: 3                   # west to east
grid:                        # the NORTH row first, as a map is drawn
  - [end@270, hall@90, ~]
  - [tee,     cross,   corner@180]
```

A cell is a room's name, optionally `@` and a rotation in degrees
counter-clockwise seen from above (0, 90, 180 or 270), or `~` for no room.
Joints are implicit: two sockets facing each other across a shared wall are
joined, and every other socket is capped. Every refusal of a level file
names its line and column.

`link` links the level's rooms (read from the pack `-rooms` names, by
default `<library>.roompack` beside the library the level names) into one
map, beside the level file by default. A room the level places that the
pack does not hold is refused, naming the room and the pack. It
refuses a level in which a player could not walk from every room to every
other, naming the rooms that cannot be reached. It needs no game directory.
Every room is compiled sealed, with a plug brush in each socket; the link
removes the plug at a joined socket (the doorway becomes open space, its
brush leaves the map and the world collision, and its faces stop drawing)
and keeps it at a capped one. Dropping those brushes matters for size: on a
generated stress library a quarter of the rooms' brushes are plugs, and the
engine loads at most 8192 brushes (`MAX_MAP_BRUSHES`).

**Brush fold.** Rooms meet cell to cell, so a linked level is full of pairs
that are one box in two brushes: floors and ceilings across every shared
boundary, walls and jambs back to back, pieces in line. The link merges
them (`-nofold` turns it off): two world brushes that are exact axis-aligned
boxes (six sides, no bevel, no displacement) merge when their extents are
identical on two axes and they touch on the third, their contents are
identical, and every side that coalesces has the same material and surface
flags. Floors and ceilings go first, into rows and then sheets; then the
shell pieces, back to back and in line. A merged box reuses the planes its
pieces had, and the leaves' brush lists and the world collision's brush
numbers follow it; the collision's convexes themselves are untouched. The
solid is the same to every trace and to physics; what the fold removes is
the internal seam between the pieces, so a sweep that starts inside one
piece and ends in the next is now all solid rather than leaving the first
at the seam. The link reports the brushes it wrote and how many it folded
away; with `-nofold` it writes the unfolded brushes byte for byte.

**Visibility.** The link composes the level's PVS without running vvis
on it. Each room's own vvis is kept for sight inside the room, and
`ssmap room` stores per room which of its clusters see each doorway and
which of its doorways see each other through it (the pack's `DVIS`
section). At link, a flow like vvis's portal flow runs over the doorway
rectangles alone, treating each room as its empty cell: two rooms see each
other only where a straight line gets through the chain of doorways
between them, each room on the way lets a line from one of its doorways to
the next, and each end cluster's bounds lie in the cone of lines the chain
lets through, tested from both ends. Neighbouring rooms see each other only
through their shared doorway, and not at all across a wall. The result
keeps every sight line of the same level compiled whole (the facts check
this against vvis on the flattened level), is the same bytes at any thread
count and for a level turned as a whole, and replaces what the link wrote
before, in which every cluster of a level saw every other (`-nodoorvis`
still writes that). On the stress library at 33 x 33 the cluster pairs
marked visible fall from 26,347,689 (all of them) to 686,929 and the
visibility lump from 6,631,840 bytes to 1,958,372; at 24 x 24 from
7,193,124 to 281,908 pairs and 1,823,764 to 665,441 bytes. The link reports
the pairs and the lump's size on a line of its own.

On a generated stress library of 256 rooms (`ssmap layout -seed 1`), the
largest square level that links goes from 24 x 24 (brushes) to 27 x 27 with
the plugs dropped, and to 33 x 33 with the fold (7,029 brushes of 8,192).
There the brushes no longer bind: a primitive's first index is a 16-bit
field, and the level's primitive indices pass 65,536 at 34 x 34, and at 33 x
33 already on some seeds. The rooms' world collision, entities and areas are merged into
the map's own. Planes, materials (texdata and their names) and texture
axes (texinfo) are shared: an entry another room already brought is named,
not copied, so the engine's 2048-texdata cap counts the level's distinct
materials rather than every room's, and the plane and texinfo tables grow
only with what the rooms do not have in common. The rooms' packed files go
into the level's one pak, merged by name: a file several rooms pack with the
same bytes is written once, two rooms that pack one name with different
bytes are refused, naming both, and the rooms' default cubemaps (built from
the library's sky, `materials/maps/<room>/cubemapdefault.vtf` and its HDR
twin) are renamed to the level's map name, the output file's name, which is
where the engine looks for them; so renaming a linked `.bsp` afterwards
loses its default cubemaps, as it does for any map. Other files named after
a room (patched materials) keep the room's name, which its faces use. The
link refuses what it cannot carry: area portals, static or detail props,
displacements, water, and a mix of cooked and `-cooker none` rooms. The doorway's side walls have no faces of their
own, because in the room's compile they faced the plug, so they draw as a
gap unless something placed in the socket (a door frame model, say) covers
them.

`link --flatten` writes the same level as one ordinary VMF instead: every
placed room copied out of the library into its cell and turned, the plugs
of joined sockets left out and the capped ones kept. Compiled with
`ssmap vbsp`, it is the reference a linked map is checked against.

`layout` writes a level of the library's rooms from a seed: the same
library and seed always give the same file, sockets line up between rooms,
and every room is reachable. `-empty` leaves that share of the cells
without a room.

**Entity budget.** The engine networks at most 2048 edicts, and at runtime
the game's players, bots, weapons, projectiles and pickups take from the
same cap, so a level may use `2048 - reserve` of them. The reserve is 512
by default; a library sets its own with the worldspawn key
`rooms_entity_reserve` (kept in the pack; it reaches neither the rooms nor
the map), and `link -entity-reserve N` overrides both. `room` counts each
room's entities by class and stores the counts in the pack; `link` totals
them before it links anything (every class counts as an edict except the
ones the tools consume, such as `func_detail` and `prop_static`, which the
link strips), refuses a level over 2048, warns when one eats into the
reserve, naming the rooms that cost the most, and always prints the
headroom:

```
map entities 612 / budget 1536 (reserve 512, cap 2048); 931 entities in the entity list
```

`layout` keeps a generated level within the same budget when the pack has
the rooms' counts, or within `-entity-budget N`; a budget no level of the
library reaches changes nothing, so the same seed gives the same file.

**Room-local names.** A name that starts with `cxry_` belongs to its room:
`cxry_door` in the room at column 3, row 5 links as `c3r5_door`, so a room
placed twice has two doors, not one name fired twice. `cx+1ry_door` names
the room beyond this room's authored east wall (`cx-1`, `ry+1` and `ry-1`
likewise, and the diagonals by both), and the offset turns with the room.
Names without the prefix are global and left as written; a global name may
not begin like one the linker writes (`c3r5_`, any case) or like a
misspelt placeholder (`CXRY_`, `cx+2ry_`, `c4rocket`), and `ssmap room`
refuses the room naming the entity and key. A reference to a cell with no
room (or off the grid) is dropped with a warning; `cxry_has_east` and
`cxry_joined_east` are `logic_branch` flags the link sets to whether that
neighbour exists or that door is open (written only when a room names them,
and folded away when only tested); and `room_needs` (`east`, `!west`,
`joined_north`, a diagonal, comma-joined) keeps or drops an entity per
placement. The link folds stateless local relays and constant branches into
their callers, merges `logic_auto`s and dedupes identical filters
(`rooms_fold_logic 0` on the library turns folding off; `rooms_name_keys`
adds name-valued keys to the built-in table). A room may place or name a
`logic_room` (`cxry_room`): with `-mod-entities` the link writes the Source
Sharp mod's one server-only entity for its flags and eight relay channels
(its contract is the `SourceSharp.RoomContracts` assembly) and records the
mode on the worldspawn (`ssmap_entities mod`); without it the link writes
stock branches and relays instead. `link --flatten` runs the same resolver,
so both maps carry the same entities. `ssmap rooms` lists each room's names
from the pack.

**Level-wide singletons.** All rooms share one sun. A `light_environment`
belongs in the library's gaps, with the fog, tone map, shadow and
post-process controllers (`env_fog_controller`, `env_tonemap_controller`,
`shadow_control`, `postprocess_controller`): `room` keeps them in the pack's
library section, and the gaps may hold one sun and one of each controller
per name. A room may carry a copy only if it equals the library's (every
key but `id` and `origin`, and the outputs in order); the copy is then
dropped from the room, and a copy that differs refuses the library, naming
the room and the first key that differs:

```
room hub: its light_environment differs from the library's (angles: "-45 120 0" against "-45 30 0"); the sun is library-wide.
```

A named controller the library does not hold under that name is the room's
own (per-room fog is a trigger and a named controller, as in any map) and
stays with the room. A `sky_camera` is refused in a room and in the gaps:
it belongs to a library skybox room, which the linker does not build yet.
`link` and `link --flatten` write each library entity once, right after the
worldspawn, never turned, at the level's origin, and keep one
`water_lod_control` (vbsp adds one to every room compile with water): an
equal later copy is dropped and a different one refused. The library's
entities count once per level in the entity budget, in `layout
-entity-budget` too, and `ssmap rooms` lists them on a `library:` line after
the budget.

**Navigation.** `ssmap room` also builds each room's 3D navigation: one
clearance grid of the room's free space (16-unit voxels in runs per
column), whose records answer exactly, for any axis-aligned agent box,
whether it fits (player clip and monster clip kept apart), with each run's
floor height, walkability, water and ladder flags and cost, the doors,
movers, breakables and props as tagged dynamic obstacles, and what capping
each door changes. It is stored in the pack beside the room, under its own
section tags, at all four turns. The library's worldspawn configures it:
`nav 0` turns it off, `nav_voxel_size` (default 16), `nav_max_slope`
(default: the game's 0.7 floor normal), `nav_step_height` (18),
`nav_jump_height` (56), `nav_jump_distance` (100), `nav_cost_water` (2),
`nav_cost_ladder` (1.5) and `nav_agents`, optional named presets (default
`standing 32 72 player; flyer 32 32 npc`). `info_poi` point entities mark
points of interest (`poi_type`, `poi_tags`, `poi_radius`, `angles`,
`poi_agents`, `targetname` with `cxry_` room-local names); they are checked
against the presets they apply to, taken out of the map (they cost no
entity), and carried in the navigation. An `info_room`'s `room_role`
(`up`, `down`) marks a level-transition room, whose `arrival` point is where
the player appears, and, for the up room, spawns.

`link` writes the map first, then `<map>.nav3d` beside it (Brotli
compressed by default): the placed rooms' grids by cell with the level's
caps merged in, the doors, jump links between floors, the dynamic
obstacles under the names the map gives them, and the points of interest
in level coordinates; a reader derives neighbours and components at load.
A point of interest standing in a doorway the level caps is refused. The
map's worldspawn and the file's header carry one level id
(`ss_level_id`), so the game can tell they belong together. A pack without
navigation (or with an older build's) links with one warning and no `.nav3d` (`-require-nav` makes it
an error, `-no-nav` skips it), and a link without navigation writes no id
keys, so its map is the one it always was. `ssmap nav` prints a navigation's cells, leaves,
jump links and each preset's free volume and components, from the file or straight from a level
and its pack, and exports the free leaves or the floors as OBJ. The format
is specified in [`docs/nav3d-format.md`](docs/nav3d-format.md), with a C++
walk-through and the C# reader the mod uses (`Nav3dReader` in
`SourceSharp.MapFormats`).

`samples/rooms-3x3/` is a worked example: a library of five room kinds, a
3x3 level, its turns and some seeded levels. Its README runs it through
`room`, `link`, `link --flatten`, `vbsp` and `layout`, and the test suite
checks every linked level against its flattened compile.

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
  reciprocal. This includes brush-side plane normals
  (`PlaneFromPointsNormalise`), plane distances, displacement normals, cooked
  collision data, leaf ambient and static-prop lighting. On one CPU family
  the output is still deterministic from run to run.

  These bits do not stay in the low bits. vbsp's split heuristic penalises
  a candidate plane when any brush lies a positive distance under one unit
  in front of it, so a vertex that lies on the plane is decided by the sign
  of a residual like 6e-5. On Valve's 2fort, turning any one of
  `PlaneFromPointsNormalise`, `BaseWindingNormalise` or `EdgeBevelNormalise`
  to the stock side on an Intel Xeon gives 2476, 2500 or 2495 visibility
  clusters, against 2492 under `correct` and stock's 2480 on AMD. Cluster
  and portal counts are therefore comparable with stock's only on the CPU
  vendor stock ran on.
- **`-compliance correct`** (the default) uses exact IEEE arithmetic in
  place of every one of these estimates, so vbsp and vvis write the same
  bytes on every CPU, and so does vrad with one known exception below.
  vrad's ray tracing used to be an exception and no longer is: the KD tracer's traversal reciprocal and triangle
  normals (`KdTracerReciprocalEstimate`) and the leaf-ambient walk's sky
  windings and point-in-sky-face test (`SkyWindingNormalise`) divide
  exactly under the default policy, as the gather, transfer and ambient-cube
  estimates already did. CI pins vbsp's digests for the sandbox map and
  several displacement maps, and vrad's for the KD tracer on two committed
  scenes, leaf ambient on its committed fixture, and the whole chain on the
  sandbox map, and runs them on AMD, Intel and arm64. A digest that holds on
  one of those runners and not another is a bug: a Correct path still taking
  an estimate.

  The known exception: static-prop lighting under the default policy still
  gives different bytes on arm64 than on x86 (AMD and Intel agree). It is not
  the KD tracer, whose Correct digests agree everywhere; its source in the
  prop-lighting path is not yet identified. Until it is, that fact keeps a
  captured arm64 delta (`Fixtures/rsqrt-vendor/Arm64/static-prop-chunking.correct.txt`)
  so the difference stays declared.

Everything else is the same on every platform: file formats, vbsp, vvis,
vrad under the default policy, every exact computation, and every
elementary function.

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
result in a million it falls back to arbitrary-precision interval
arithmetic, which always decides. Double `log`, which the detail-prop
Gaussian takes once a sample, has a double-double evaluation good to 2^-64
and falls back for about one argument in two thousand (about 0.15 µs a call
on average, against the exact tier's 30). Double `sin`, `cos` and `pow`
always take the exact tier, tens of microseconds a call, which is fine for
their once-per-light uses; that is why the per-luxel gamma uses
`DetMath.PowToSingle` (the bits of `(float)DetMath.Pow`, at float cost). A fact scans the built
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
the map (`<map>.sscache.db`, or under `-cache-dir`, which is created if it
does not exist yet). A later compile reuses
every model whose inputs have not changed. Brush models are keyed by their
content, so adding or moving one brush does not invalidate the others.
`-nocache` turns the cache off for one run. The cache needs a cooker; with
`-cooker none` there is nothing to store. `ssmap room -incremental` uses
the same store for a room library's finished rooms (see
[`room`](#room-rooms-link-and-layout)); it needs no cooker.

`ssmap cache` reads the same file: `stats` summarises it, `explain` shows
what a key was built from, `gc` trims it, `clear` empties it and `check`
verifies it.

The SQLite backend lives in its own assembly so the core libraries carry no
package references. If it cannot be loaded, `ssmap` says so instead of
silently compiling without a cache.

## Running as a service

The libraries are built to be hosted in one long-lived process that runs
many compiles, one after another and several at once, without restarting.
`MapCompiler.CompileAsync` is the whole chain; everything below is what the
host owns and passes in through `CompileRequest`. None of it changes the
output: every setup here writes the same BSP as a one-shot `ssmap all`.

```csharp
// At start-up, once per game the service compiles for.
GameContentMounter.Result game = await GameContentMounter.MountAsync(
    disk, gameInfoPath, baseDirectory, cancellationToken: stopping);
await using ContentFileSystem content = game.Content;

// Once per process: the cooker, the stage cache and the prop hull cache.
await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
await using InMemoryCacheStore stages = new(maxBytes: 2L * 1024 * 1024 * 1024);
await stages.OpenAsync("memory", stopping);
using PropHullCache hulls = new(maxBytes: 64L * 1024 * 1024);

// Per compile: the same objects every time.
CompileRequest request = new()
{
    Source = MapSource.FromVmf(disk, vmfPath),
    Content = content,
    CollisionCooker = cooker,
    Cache = stages,
    PropHullCache = hulls, // see "The prop hull cache" below
    Output = CompileOutput.ToDirectory(disk, outputDirectory),
};
CompileResult result = await MapCompiler.CompileAsync(request, null, jobToken);
```

**Warm up.** The first compile in a process pays for JIT compilation: on
`sdk_ctf_2fort` (4 threads, `-fast` vvis, `-ldr -fast` vrad) it takes
89-92 CPU-s, and every later compile of the same map 75-78 CPU-s. A
service that cares about its first job's latency compiles a small map once
at start-up, before it takes work.

**One mount per game.** Mounting a game indexes every file of every VPK on
its search path, and nothing a compile does changes the mount, so mount
each game once and hand the same `ContentFileSystem` to every compile of
it, concurrent ones included. The indexes are read-only after mounting,
loose files are opened per read, and each VPK part keeps one stream that
reads take turns on. No compile disposes the content it is given. Dispose
the mount once, after the last compile that uses it; a dispose that
overlaps a compile still reading waits for the read in progress, closes
every archive stream, and makes later reads fail rather than reopen a
file. On 2fort's content packed into VPKs, sharing the mount saved the
0.3-0.5 CPU-s and 150 MB of allocation each compile spent mounting, and
kept about 90 MB live for as long as the mount is held. Two compiles at
once over one mount wrote the same bytes as two over mounts of their own,
at 153 against 155 CPU-s per pair and 4.0 against 4.3 GB allocated.

**A bounded stage cache.** With `CompileRequest.Cache` set, a compile
whose inputs have not changed since an earlier compile replays that
compile's vvis and much of its vrad instead of running them.
`InMemoryCacheStore` keeps everything in the process and is bounded by
its own `maxBytes` ceiling (1 GiB by default): every commit counts its
blobs' bytes plus an estimate per row, and when a commit goes over, the
least recently committed rows go first, down to 90% of the ceiling. A hit
re-commits the rows it replays, so rows in use stay young. The ceiling is
applied at every commit, including while other compiles are running, so a
service whose compiles always overlap stays bounded. A row that has been
evicted costs a miss, never a wrong hit, because every blob is
content-addressed. A 2fort recompile against a warm shared store took
67.2 CPU-s against 77.8 CPU-s without the store, with the same bytes. For
a cache that survives restarts, use the SQLite store (`-incremental` in
`ssmap`) instead; its size is managed by the collector (`CachePolicy`).

**The prop hull cache.** `PropHullCache`
(`CompileRequest.PropHullCache`) keeps cooked static-prop hulls
between compiles, keyed by what the cook reads rather than by model name,
and bounded in bytes. It saves most of vbsp's prop cooking (about 1.3 of
2fort's 5 warm vbsp CPU-s) on every compile that names a model an earlier
compile cooked, edited map or not. See [Collision cooking](#collision-cooking).

**GC settings.** These belong to the host process (its `runtimeconfig.json`
or `DOTNET_` environment variables); the libraries never change them.
Measured on 2fort in one process:

| Setup | Peak RSS | GC pauses | Notes |
| --- | --- | --- | --- |
| Workstation, concurrent (the default), sequential compiles | 1.08-1.17 GB | 1.0 s per compile | |
| The same, plus a compacting `GC.Collect` between compiles | 0.83-0.84 GB | 0.9-1.3 s | -28% peak |
| Server GC with DATAS (the .NET 10 default for server GC), sequential | 0.89-0.92 GB | 0.9-1.6 s | -25% peak |
| Workstation, 2 concurrent compiles | 1.41-1.63 GB | 2.4-2.8 s per pair | |
| Server GC without DATAS (`GCDynamicAdaptationMode=0`), 2 concurrent | 1.80-2.15 GB | 0.93 s per pair | 3x shorter pauses, +30% memory |
| Server GC with DATAS, 2 concurrent | 1.42-1.48 GB | 3.8-4.2 s per pair | wall time 60% longer; avoid |

So, for compiles run one at a time, either call
`GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)`
(with `GCSettings.LargeObjectHeapCompactionMode = CompactOnce`) between
compiles, or run with server GC and DATAS. For compiles run concurrently
where pauses matter more than memory, use server GC with DATAS turned off.

## GPU ray tracing

`SourceSharp.MapTools.Gpu` is an optional Vulkan ray tracer (via Silk.NET)
for vrad. It is off unless asked for: `-gpu <match>` turns it on and picks
the first capable device whose name contains `<match>` (`-gpu auto` takes
any capable device), and `-gpu_slabs <n>` sets the ray budget for the
batches ("slabs") on the GPU. The tracer keeps three slabs in flight, so the
GPU traces one while the next waits behind it and the CPU packs or unpacks a
third, and each slab holds a third of the budget (the default, 4,194,304
rays, is at most 117 MB of rays in all). A ray goes to the device in 28
bytes (origin, direction, reach), or 24 when every ray in its slab has the
same reach, which then travels once per slab: 79 % of 2fort's rays in the
default compliance mode, almost none in `-compliance stock`, whose rays
carry each segment's own length. Either way the kernel is handed exactly
the floats the caller built, so the answers are the same bits as with the
older 32-byte record. Where the device allows it (integrated
GPUs, and discrete GPUs with resizable BAR), rays are written straight into
memory the GPU reads, skipping the upload copy. When no usable device is
found, vrad reports that it declined the GPU and falls back to the CPU
KD-tree tracer, so a run never fails for lack of a GPU.

`-gpu auto` asks for no particular device and means "use the faster one":
it tries the capable devices one at a time and uses the first that passes.
The order is by type first (discrete, then integrated, virtual, CPU: a
discrete card goes before an integrated one whatever their memory), then,
within a type, the larger device-local memory heap (VRAM), then the larger
shader-core count the vendor reports (AMD compute units, NVIDIA SMs, Arm
cores; core Vulkan exposes no clock speed, so cores stand in for
throughput), then the loader's order. A device is passed over, and released
before the next is opened, when

- it is a CPU implementation of Vulkan (llvmpipe), which the built-in CPU
  tracer beats;
- it fails the two-triangle self-test; or
- it has to copy rays to itself (no resizable BAR) and a probe of that copy
  during start-up measures less than 2.5 GB/s.

When every device is passed over, vrad keeps the CPU tracer and its one
warning lists each device with its reason. On a machine with an RTX 2070 on
a slow link beside an RX 9070, the 2070 is passed over and the 9070 used.

Why 2.5 GB/s: a ray was 32 bytes when the floor was set, and the CPU
tracer answers about 80 million rays a second on 32 threads, so below that
rate the upload alone took longer than the CPU would. With 24- and 28-byte
records the break-even is nearer 2.1 GB/s; the floor was left where it was. An RTX 2070 SUPER in a PCIe Gen2 x1 slot (about
0.5 GB/s) spent 34 s of a 2fort light copying rays and 0.4 s tracing them,
and lost to the CPU by five times.

A named device (`-gpu nvidia`, `-gpu llvmpipe`) is always used if it passes
its self-test. Every device is checked first with a two-triangle self-test;
a device that fails it is declined with each test ray's expected and actual
answer, the driver name and version, and the flags used, in the warning.
Hosts using the library get the same policy with
`VulkanRayTracerOptions.DeclineSlowDevicesUnlessPinned` (off by default)
and can move the floor with `MinUploadBytesPerSecond`.

Direct light keeps the GPU fed by pipelining: each worker keeps up to four
batches of 16,384 rays traced and not yet resolved (`-gpu_depth <n>`, 1 to
64), filling the next while earlier ones trace and resolving each as its
answers arrive, in the order it filled them. The depth sets how many rays
the workers have queued, not how many slabs are on the device: that ring
is three slots, and on real hardware it is already full at the default
(`peakinflight=3/3` on both an RX 9070 and an RTX 2070 SUPER). A deeper
pipeline therefore makes the slabs bigger, because more rays are waiting
each time a slot frees up, and puts nothing more on the device. It helps
only when the workers cannot keep the ring fed, for example with few
threads; on 2fort, depths 4, 8 and 16 changed the slab size, never the
slabs in flight.

A given tracer writes the same lightmap bytes at every depth, every thread
count and every run. Different tracers do not: the GPU's ray-triangle test
is the hardware's, and it decides rays that graze an edge differently from
the CPU tracer's exact test and from another vendor's hardware, so about
0.1 % of 2fort's rays get a different answer and the output differs by
vendor. Measured on 2fort (main 65934e2): the CPU tracer's output is
`5e3e839ff16d1055`, an RX 9070 (radv) gives `3e12959462dcacf9` and an RTX
2070 SUPER (NVIDIA) `25b3eda7939de553`, each stable across runs and depths.
That is the accepted behaviour of the opt-in GPU path, not a compliance
quirk: `-compliance` governs the CPU tools' arithmetic, and a map that must
match stock or another machine byte for byte is compiled without `-gpu`.

`vrad --bench` prints where the rays went and what the device did:

    bench trace tracer=<id> gpu=on rays=N gpu.visibility=... cpu.sky=... parked=...s parked.facelights=...s ...
    bench gpu requests=N slabs=N busy=...s fencewait=...s pack=...s readback=...s raybytes=N raybytes.perray=24.00..28.00 rays=direct|staged ... peakinflight=3/3 fallbackrays=N

`gpu=off`, `gpu=declined` (with the reason on the `bench gpu` line) or
`gpu=on` says whether the GPU answered at all; `parked` is worker time spent
waiting on a batch in flight, per stage; `busy` is the host-side span with a
slab on the device, `pack` and `readback` the host's copies, `raybytes`
the bytes of rays packed (what an upload moves) and their average per ray,
and `rays=staged` a device without resizable BAR, where every slab is also
copied on the device. A big slab is packed by up to four threads, so
`pack` is wall time, not thread time.

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

- vbsp, vvis and vrad `--bench` stage times;
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

### In a long-lived process

`compile-perf.sh` and `ssmap bench` measure compiles one after another. The
libraries' main host is a service that keeps one process for many compiles,
several at a time, and some costs only show there: scratch a finished compile
keeps alive, pools that grow run to run, handles that are not released,
compiles contending for one mount. `tools/WarmBench` is that host:

    dotnet build tools/WarmBench/WarmBench.csproj -c Release
    dotnet tools/WarmBench/bin/Release/net10.0/WarmBench.dll \
        --map maps/ss_sandbox.vmf --game <dir> --runs 6 --leak

It compiles the map through `MapCompiler.CompileAsync`, using only the public
API, in waves of `--concurrency` compiles (default 1) with `--threads`
workers each, `--runs` times. Other switches:

- `--mount shared|per`: one game mount for every compile, or one each.
- `--cooker shared|per`: the same for the collision cooker.
- `--cache mem`: one in-memory incremental cache for the whole process, so
  the second wave onwards replays from it.
- `--vbsp`, `--vvis`, `--vrad` take stock arguments as one quoted string,
  for example `--vrad "-bounce 2 -compliance stock"`.
- `--substages` prints the time under each progress stage.
- `--gc-between` forces a compacting full GC between waves.
- `--help` lists them all.

Each line starts with a tag:

- `R`, one per compile: its wall time, each stage's wall and CPU seconds
  (CPU only with `--concurrency 1`, since it is the process's), the output
  BSP's SHA-256 and whether it matches the first run's, and what it read
  from the game content.
- `W`, one per wave: wall, CPU, bytes allocated, gen0/1/2 collections, total
  GC pause and that wave's peak RSS.
- `L`, with `--leak`, after each wave: the live heap before and after the
  host yields, committed memory, LOH, POH, RSS, open descriptors, threads and
  memory mappings, after full compacting collections.
- `LEAK`: the trend from wave 1 to the last. Wave 0 is the warm-up, where the
  JIT runs and every pool is sized for the first time. `verdict=GROWING`
  means the heap grew by more than 1 MB a wave, a descriptor stayed open, or
  the thread count grew by a compile pool's worth.
- `SUMMARY`: the median compile wall and wave CPU without wave 0, and
  whether every output was the same. The exit code is 1 when a compile failed
  or two outputs differed.

The game directory is mounted without a Steam locator, so use a copy
without the `|appid_N|` lines ([Without the Steam content](#without-the-steam-content)).

**GC mode.** The GC is the host's choice, so neither the libraries nor
WarmBench set it; pass it in the environment when starting the process.
The first `#` line prints the mode the runtime actually chose and every
`DOTNET_GC*` variable it saw.

The runtime reads these values as hexadecimal.

| Variable | Effect |
| --- | --- |
| (none) | Workstation GC, concurrent: the console-app default. |
| `DOTNET_gcServer=1` | Server GC, as ASP.NET hosts run by default. On .NET 10 it comes with DATAS, which starts with one heap and adds heaps as load grows. |
| `DOTNET_GCDynamicAdaptationMode=0` | With server GC, DATAS off: a heap per core from the start. |
| `DOTNET_GCHeapCount=N` | With server GC, N heaps instead of one per core. |
| `DOTNET_gcConcurrent=0` | No background GC; every gen2 collection blocks. |
| `DOTNET_GCgen0size=4000000` | The gen0 budget in bytes (here 64 MB). |
| `DOTNET_GCConserveMemory=N` | 1 to 9: compact more often to keep the heap smaller. |

For example, a service-like run, two compiles at a time on one mount:

    DOTNET_gcServer=1 dotnet tools/WarmBench/bin/Release/net10.0/WarmBench.dll \
        --map maps/ss_sandbox.vmf --game <dir> --concurrency 2 --threads 2 --mount shared --leak

**Where the time went.** `--marks <file>` records when each compile entered
each stage, in the clock `perf` uses. `tools/WarmBench/perf_by_stage.py`
puts every `perf` sample in the stage that was running when it was taken and
prints each stage's hottest functions. The script's docstring has the
`perf record` line to use. It is optional; the `R` lines already give each
stage's time.

### Against the stock tools and Tools++

`tools/toolchain-bench.sh` compiles the same maps with the stock SDK 2013
compilers, Tools++ and `ssmap`, and times each stage:

    tools/toolchain-bench.sh                     # ss_sandbox and 2fort
    tools/toolchain-bench.sh --map dustbowl=path/to/sdk_cp_dustbowl.vmf:game/mod_tf --runs 5

Each toolset runs its own vbsp, vvis and vrad chain. The runs are
interleaved, and every map starts with an untimed warm-up per toolset. The
Windows tools run under wine: Proton Experimental's wine is used when it is
installed, with the prefix `~/.local/share/source-sdk-wineprefix`. They
cannot mount `|appid_N|` paths, so each game directory's search paths are
rewritten into a gameinfo of the run's own, with absolute paths.

- **Stock tools:** found through Steam, in app 243750's `bin/x64`.
- **Tools++:** read from `~/Downloads/tools_plusplus` (its `tools/` and
  `compatibility/` folders). Its vbsp always gets `-matsyscompat`. Without
  it, Tools++ cannot load the SDK 2013 and TF2 textures, and it writes an
  unlit map in a fraction of the time.

Every output is checked as well as timed. A chain whose map has no lighting
is reported as a failure, not a time. `summary.md` lists each toolset's
visibility and lighting lump sizes and its "not found" log lines next to its
numbers.

The toolsets:

- `stock`: the SDK 2013 compilers under wine.
- `pp`: Tools++ under wine.
- `ssmap`: the Release build on the JIT.
- `ssmap-aot`: the same ssmap published with NativeAOT to `bin/aot/ssmap`.
  The script publishes it unless `--no-build` is given. The chain runs
  one process per stage, as the stock tools do, and AOT skips the .NET
  start-up and JIT that each of those processes otherwise pays.
- `ssmap-fast` and `ssmap-aot-fast`: the same two builds with vvis
  `-fastflow` (see [the fast flow](#the-fast-vvis-flow--fastflown)) and
  nothing else changed. The flag goes on the vvis stage only. Its accuracy
  cost shows in the `vis bytes` column of `summary.md`, next to plain
  `ssmap` and `stock`: the fast flow can only drop visible clusters, so its
  visibility lump is usually smaller.

`--toolsets` picks a subset.

#### Results

Measured with `tools/toolchain-bench.sh` at 9b300b1:

- Machine: Ryzen 9 9950X (16 cores, 32 threads) on Linux.
- Each tool's default options and thread count.
- Median of 3 timed chains after one warm-up.
- Wall seconds per stage.

`ss_sandbox`:

| toolset | vbsp | vvis | vrad | total | vs stock |
|---|---:|---:|---:|---:|---:|
| stock | 0.27 | 0.13 | 9.75 | 10.14 | 1.00× |
| Tools++ | 0.23 | 0.05 | 8.49 | 8.78 | 0.87× |
| ssmap (JIT) | 0.52 | 0.07 | 3.26 | 3.85 | 0.38× |
| ssmap (AOT) | 0.11 | 0.01 | 1.37 | 1.48 | 0.15× |

`sdk_ctf_2fort`:

| toolset | vbsp | vvis | vrad | total | vs stock |
|---|---:|---:|---:|---:|---:|
| stock | 5.56 | 9.49 | 31.35 | 46.40 | 1.00× |
| Tools++ | 4.82 | 2.60 | 7.92 | 15.34 | 0.33× |
| ssmap (JIT) | 3.89 | 5.22 | 9.00 | 18.15 | 0.39× |
| ssmap (AOT) | 1.22 | 4.94 | 7.39 | 13.54 | 0.29× |

All four toolsets produced lit maps with the same lighting size, to within
0.2%.

What the numbers say:

- **ssmap AOT is the fastest chain on both maps.** On the small sandbox the
  JIT's start-up is most of ssmap's time: 3.85 s against 1.48 s. vrad is
  where ssmap gains most over stock, and ssmap's vbsp is the fastest of the
  four.
- **Tools++'s vvis is about twice as fast as ssmap's on 2fort.** vvis is the
  one stage ssmap does not lead. Tools++ keeps its visibility bit vectors per
  cluster, where stock and ssmap keep them per portal. On 2fort that makes
  each vector about a fifth of the size, and every step of the portal flow
  reads and writes those vectors. Tools++ also vectorises those loops with
  AVX2.
- **ssmap's 2fort tree is close to stock's, but not identical.** ssmap's
  vbsp writes 2492 clusters and 6367 portals, against stock's 2480 and 6339,
  and its visibility lump is 667,660 bytes against stock's 660,492. The
  remaining difference is not yet in the compliance catalogue, so it is a
  bug to find, not a result.

The sandbox numbers are small enough that process start-up dominates. 2fort
is the one to compare compilers on.

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
- **No mutable static state** in `MapFormats`, `MapTools` or `RoomContracts`, so two compiles
  can share one process.
- **Same output on every platform.** The same map, game content and
  options produce the same bytes on Linux, Windows and macOS, on any .NET
  runtime. The only differences allowed are the CPU-estimate ones listed
  under [Platform differences](#platform-differences): stock's `rcpss` /
  `rsqrtss` arithmetic under `-compliance stock`. The opt-in paths that hand work to code outside
  this repository, `-gpu` (the device's ray intersection) and
  `-cooker native` (the game's vphysics library), are outside the rule.
  Any other difference between platforms is a bug.
- **No platform math.** This is how the rule above is kept. Elementary
  functions go through `DetMath` and `DetMathF`, never `Math.Sin`,
  `MathF.Pow` and the like, so output does not depend on the OS's C
  library. A fact scans the built libraries and fails on any such call.
- **No package references** in `MapFormats`, `MapTools` or `RoomContracts`. SQLite and
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
