# CLAUDE.md

Guidance for Claude Code (and anyone else) working in this repository.
`README.md` covers what the tools do and how to use them; this file covers
how to change them.

## What this is

A C# (.NET 10) port of the Source map compile chain: `vbsp`, `vvis` and
`vrad`, plus the file formats and the collision cooker under them. The CLI
is `ssmap` (`src/SourceSharp.MapCompile`). Everything runs in process; no
native tool binary is invoked. The optional native path is loading a game's
vphysics library with `-cooker native`.

The goal is output that matches the stock tools byte for byte where the stock
behaviour is right, and a documented, switchable fix where it is not (see
[Compliance quirks](#compliance-quirks)).

## Commands

```sh
# build (from src/)
dotnet build SourceSharp.MapTools.slnx -c Release

# test the whole suite
dotnet test SourceSharp.Tests/SourceSharp.Tests.csproj -c Release

# one class or one test
dotnet test SourceSharp.Tests/SourceSharp.Tests.csproj -c Release --filter "FullyQualifiedName~VvisComputeTests"

# run the CLI
dotnet run --project SourceSharp.MapCompile -c Release -- all ../maps/ss_sandbox.vmf -game <dir>
dotnet ../bin/Release/ssmap.dll -h                           # after a build
```

All projects build into one folder at the repo root, `bin/<Configuration>/`
(set in `src/Directory.Build.props`; no project overrides `OutputPath`).
`obj/` stays per project.

The full suite is large (4,600+ facts). While iterating,
run the folder or class you touched with `--filter`, then the full suite
before pushing.

## Standing rules

These come from the project owner and apply to every change.

1. **Every logic path gets an xUnit test.** New code comes with facts that
   exercise each branch; a bug fix comes with a fact that fails without it.
   Tests live in `src/SourceSharp.Tests/`, in the folder that mirrors the
   code (`MapTools/Rad/Light/` tests `SourceSharp.MapTools/Rad/Light/`).
2. **All file IO goes through `IFileSystem`**
   (`src/SourceSharp.MapTools/Io/IFileSystem.cs`), never `System.IO`
   directly. Only `PhysicalFileSystem` and `MappedMemoryOwner` may use
   `File`, `FileInfo`, `Directory`, `DirectoryInfo`, `FileStream`,
   `Path.GetTempPath`, `Path.GetTempFileName` or
   `Environment.CurrentDirectory`. Tests use `InMemoryFileSystem`.
   `FileSystemSeamTests` scans the built IL and fails on any other caller.
3. **The primary target is a long-lived service on Linux.** The libraries
   are hosted in one process that runs many compiles, one after another and
   concurrently, without restarting. `ssmap` as a standalone tool is useful, but the service is
   what decides. So:
   - nothing may accumulate across runs: no caches keyed by map that never
     evict, no growing registries, no leaked native handles, file handles or
     pooled buffers. Everything a compile acquires is released when it ends,
     including when it fails or is cancelled;
   - a failed or cancelled compile must leave the process fit for the next
     one: no half-initialised shared state, no poisoned pools;
   - compiles run concurrently in one process, so they must not share
     mutable state; this is why mutable statics are banned (below);
   - no process-wide side effects from the libraries: no changing the
     current directory, environment, culture, GC settings or console.
   Windows and macOS are built and tested in CI, but Linux behaviour is what
   decides.

## Rules the tests enforce

`src/SourceSharp.Tests/MapTools/LibraryRuleTests.cs`,
`MapTools/DeterministicMathRuleTests.cs` and
`MapTools/Io/FileSystemSeamTests.cs` check these on the built assemblies, so
breaking one fails the suite rather than a review.

- **No mutable static fields** in `SourceSharp.MapFormats` or
  `SourceSharp.MapTools`. `const` and `static readonly` of an immutable type
  are fine; a `static readonly` array is not, because its elements are
  writable. Two compiles must be able to share one process.
- **No package references** in `MapFormats` or `MapTools`, direct or
  transitive. SQLite belongs in `Cache.Sqlite` and Silk.NET in `Gpu`, and
  those reference the core, not the other way round. `MapFormats` has no
  project references at all.
- **Every public async method takes its `CancellationToken` last.**
  `DisposeAsync` is the only exemption.
- **No platform math** in `SourceSharp.MapFormats` or `SourceSharp.MapTools`.
  Elementary functions (`sin`, `atan2`, `pow`, `log`, ...) go through
  `DetMath` / `DetMathF` (`src/SourceSharp.MapFormats/Numerics/`), never
  `Math.Sin`, `MathF.Pow`, the generic-math statics or the vector types'
  transcendentals. The platform's C library rounds differently on glibc,
  the Windows UCRT and macOS, so any call to it makes output depend on the
  OS. Only the IEEE-exact members (`Sqrt`, `Abs`, `Floor`, `Min`, ...) are
  allowed; widening that allow-list is a deliberate decision, not a fix for
  a failing build.

Enforced by the compiler:

- `CS0108` / `CS0114` (member hiding) are errors everywhere
  (`src/Directory.Build.props`). Do not switch on `TreatWarningsAsErrors`
  globally; the props file explains why.
- In `SourceSharp.MapCompile`, `CS1591` (missing XML doc on a public member),
  `CA1068` (token not last) and `CA2016` (token not forwarded) are errors.

Enforced by convention:

- **Libraries never touch the process.** No `Console`, no environment
  variables, no `Environment.Exit` in `MapFormats` or `MapTools`. Output,
  exit codes and Ctrl-C belong to the host in `MapCompile`.
- **The CLI gets no `InternalsVisibleTo`.** If `ssmap` needs something
  internal, make it public and document it. Anything the CLI can do, a
  third-party host must be able to do.
- **Pass cancellation tokens through.** Long loops observe the token so a
  cancelled compile leaves no half-written file.

## Compliance quirks

Any behaviour where this code deliberately differs from the stock tools'
output is a named entry in the compliance catalogue
(`src/SourceSharp.MapTools/Options/ComplianceCatalogue.cs`), switchable with
`-compliance correct|stock|correct,+Name`.

When you find a stock behaviour that is wrong, CPU-dependent or
order-dependent:

1. Add a catalogue entry: name, title, tools affected, what the reference
   does, what the correct side does, and how the difference was observed.
2. Make the deciding method check that quirk through `Compliance`, and list
   the method in the entry.
3. Add facts for both sides and cite them in the entry.

A build-time fact fails if a listed method does not actually consult its
quirk, or if a cited fact no longer exists. Never change output bytes
silently: a difference from stock that is not in the catalogue is a bug.

`-compliance stock` reproduces stock's estimate-based arithmetic (`rsqrtss`
and the like). Estimates come from `FloatEstimate`: SSE on x86, ARM's
`frecpe`/`frsqrte` on arm64. Results that depend on them differ in the last
bits between AMD, Intel and arm64; README.md's "Platform differences"
section has the details. Never call `Sse.*` directly on a path that must also
run on arm64: use `Vector128` and, for x86-specific lane rules, `SseLanes`.

## Writing comments and messages

The tree describes **behaviour and format, not provenance**. A run of
`scrub` commits removed engine source citations, and new code should not
bring them back:

- no C++ file and line references (`vrad.h:123`, `bspfile.h`), disassembly
  offsets or hex addresses;
- describe what the reference does ("the reference implementation clamps
  to 4 styles"), not where it does it;
- the same goes for exception messages and other user-visible strings.

Doc comments here are long and explain why, not only what. Match that when
you touch a file: say why a choice was made, especially a surprising one,
and keep explanations next to the code they justify (the csproj files are
a good example).

Every C# file starts with the Source SDK 2013 copyright header. Copy it from
any existing file.

## Tests

- xUnit 2.9. Folders mirror the libraries: `MapFormats/`, `MapTools/Bsp/`,
  `MapTools/Vis/`, `MapTools/Rad/...`, and so on.
- Test classes run in parallel (`src/SourceSharp.Tests/AssemblyInfo.cs`),
  which also exercises the service's concurrent compiles. A test that
  shares something process-wide (a loaded native library, a fixture file it
  rewrites) goes in a named xUnit `[Collection]` with the tests it would
  race; never switch parallelism off for the whole assembly.
- Facts that need something the machine may not have skip themselves with a
  reason instead of failing:
  - reference-output comparisons skip when the reference corpus is absent;
  - `[SandboxMapFact]` skips when the generated sandbox map is not in the
    tree;
  - `[RepoSourceFact("path/...")]` skips when that file is not above the
    test binary.
- Never skip, disable or delete a failing test to get green. Find the
  cause. If a golden value is CPU-dependent, make that explicit in the test
  rather than loosening it: use `[ReferenceRsqrtFact]` and
  `VendorGolden.Expected`, which read per-CPU delta files from
  `Fixtures/rsqrt-vendor/`.
- Fixture data lives under `src/SourceSharp.Tests/Fixtures/` or in a
  `Fixtures/` folder next to the tests that use it.
- `SourceSharp.MapGen` generates the feature catalogue and
  `maps/ss_sandbox.vmf`, which many compile tests use.

## Environment notes

- **Use Microsoft's .NET runtime.** Ubuntu's packaged `libcoreclr.so`
  (10.0.12) aborts `ssmap vvis` with `Internal CLR error (0x80131506)` in
  roughly one run in seven. The evidence is in
  `src/SourceSharp.MapCompile/SourceSharp.MapCompile.csproj`. If a vvis run
  dies that way on a distro-packaged runtime, suspect the runtime before the
  code.
- In a sandbox where Microsoft's installer is blocked,
  `apt-get install dotnet-sdk-10.0` is the fallback. It is fine for building
  and testing, but it is the runtime above, so rerun a vvis abort once before
  treating it as a code bug.
- The bundled `game/mod_*/gameinfo.txt` files mount Steam content through
  `|appid_N|` search paths. With no Steam install, copy a game directory to
  a scratch location, remove the `appid_` lines, and put the map in its
  `maps/` folder. The compile then runs with warnings for missing materials
  and models.
- The `game/` directories and `maps/` are test corpus, not scratch space.
  Do not write compile output into them in a commit.

## Where things are

| Need to change | Look in |
| --- | --- |
| a stock command-line flag | `src/SourceSharp.MapTools/Options/StockArgs.cs`, then `VbspOptions` / `VvisOptions` / `VradOptions` |
| a new `ssmap` verb | `src/SourceSharp.MapCompile/Program.cs` (dispatch and usage text) and a `*Command.cs` beside it |
| running stages in process | `src/SourceSharp.MapTools/Compile/MapCompiler.cs` |
| BSP lumps or structs | `src/SourceSharp.MapFormats/Bsp/` |
| VMF, KeyValues, VMT, `.rad`, portal files | `src/SourceSharp.MapFormats/Text/` |
| game mounting and search paths | `src/SourceSharp.MapTools/Io/` (`GameContentMounter`, `GameInfo`, `SteamLibraryFolders`) |
| vrad direct light / bounce / ambient | `src/SourceSharp.MapTools/Rad/Light/`, `Rad/Bounce/`, `Rad/Ambient/` |
| ray tracing | `src/SourceSharp.MapTools/Tracing/` (CPU KD tree), `src/SourceSharp.MapTools.Gpu/` (Vulkan) |
| collision cooking | `src/SourceSharp.MapTools/Phys/` |
| incremental cache | `src/SourceSharp.MapTools/Compile/Cache/` and `src/SourceSharp.MapTools.Cache.Sqlite/` |
| loader validation (`ssmap check`) | `src/SourceSharp.MapTools/Validation/` |

## CI and releases

`.github/workflows/ci.yml` builds and tests on Linux, Windows and macOS
(Intel and Apple Silicon) for every push to `main` and every PR, and publishes AOT and
framework-dependent `ssmap` builds for linux-x64, win-x64 and osx-arm64
(no osx-x64 archive). Pushing a tag attaches those archives to the GitHub
release. Keep the Intel macOS test runner even though osx-x64 is not
packaged: it is CI's only Intel CPU (Linux and Windows run on AMD), so it is
the one place the `GenuineIntel` rsqrt-vendor deltas are checked. Keep CI
green: run the full suite locally before pushing.
