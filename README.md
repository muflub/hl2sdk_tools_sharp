# MapTools

A fully managed compile chain for Source-format maps: a `vbsp` / `vvis` /
`vrad` equivalent in C# (.NET 10), plus the BSP/VMF/VPK readers and writers,
the geometry kernel, and the collision cooker it is built on. No native tool
binaries are invoked; the whole chain runs in process.

## Layout

- `src/SourceSharp.MapFormats/` — BSP, VMF, VPK, PRT, LIN reading and
  writing; lump codecs.
- `src/SourceSharp.MapTools/` — the compile passes: BSP (clip, portals,
  leafs, areaportals, displacements, static/detail props, writing), VVIS
  (potentially-visible set, portal flow, occur-visibility, tighten), VRAD
  (direct lighting, radiosity bounce, lightmaps, leaf ambient, static-prop
  and detail-prop lighting, cubemaps).
- `src/SourceSharp.MapCompile/` — the `ssmap` command line: `vbsp`, `vvis`,
  `vrad`, `all`, `room`, `link`, `diff`, `check`, `bench`, `cache`, `phys`,
  `compliance`.
- `src/SourceSharp.MapTools.Cache.Sqlite/` — the on-disk incremental cache.
- `src/SourceSharp.MapTools.Gpu/` — optional GPU radiosity backend.
- `src/SourceSharp.MapGen/` — generated feature catalogue and sandbox
  artefacts used by the test suite.
- `src/SourceSharp.Tests/` — 4,600+ xUnit facts covering the whole chain.
- `game/`, `maps/` — the test corpus (maps with `gameinfo.txt` sidecars).

## Building

    cd src
    dotnet build SourceSharp.MapTools.slnx -c Release

## Running

    cd src
    dotnet run --project SourceSharp.MapCompile -c Release -- <command> [options]

`ssmap -h` prints the full usage. The everyday forms:

    ssmap vbsp <map> -game <dir> [-threads <n>]
    ssmap all <map> -game <dir>          # vbsp + vvis + vrad, one process
    ssmap diff <a.bsp> <b.bsp>           # lump-by-lump comparison
    ssmap compliance                     # the quirk switches per tool

`-game <dir>` names the directory holding `gameinfo.txt` and the map. Each
stage also accepts its stock-compatible argument spelling.

## The `correct` / `stock` rule

Some reference behaviours are CPU-dependent, order-dependent, or simply
wrong, and a fix would change the output bytes. Every one of these is a
named entry in a central compliance catalogue and is governed by one
command-line rule:

    -compliance correct                       # the fixed behaviour (default)
    -compliance stock                         # every reference behaviour
    -compliance correct,+EdgeBevelNormalise   # one quirk on the stock side

`-listcompliance` prints the catalogue: each entry names the tool whose
output moves, the behaviour, and the managed methods that decide it. A
build-time fact checks the built assembly: every method listed as deciding
a quirk must actually pass that quirk to the compliance check, so the
ledger cannot drift from the code.

## Measuring performance

    tools/compile-perf.sh --map maps/ss_sandbox.vmf --game game/mod_sharp --threads 1,max --trace

times the whole chain (`ssmap all`), then vbsp, vvis and vrad each on
their own, then one `ssmap vrad --bench` run for vrad's own stages and ray
counts, and with `--trace` a sampled CPU profile of vrad (needs
`dotnet tool install -g dotnet-trace`). Everything lands in
`perf-results/<timestamp>/`, with `summary.md` on top and `env.txt`
recording the machine, the revision and the .NET runtime. `tools/compile-perf.sh -h`
lists the options.

## Tests

    cd src
    dotnet test SourceSharp.Tests/SourceSharp.Tests.csproj -c Release

Facts that compare against reference outputs skip themselves when the
reference corpus is not present; the rest run everywhere.
