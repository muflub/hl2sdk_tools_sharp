//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap room</c>, <c>ssmap link</c> and <c>ssmap layout</c>: the verbs of
/// the room pipeline. <c>room</c> is vbsp's host half — mount the game, parse
/// the stock line, compile — run for every room of a library VMF at once
/// (<see cref="RoomLibraryCompiler"/>), the rooms written together as one
/// <see cref="RoomPack"/> instead of a .bsp each.
/// <c>link</c> needs no game at all: the rooms arrive as objects, the level
/// as a YAML grid, and the only host decision is where the files are; with
/// <c>--flatten</c> it writes the level as one VMF for vbsp instead.
/// <c>layout</c> writes a seeded level for a library.
/// </summary>
/// <remarks>
/// <para>
/// Like every other verb, the host knowledge stays here: the filesystem, the
/// Steam roots, the cooker, the paths. The room library reads no environment
/// variable and touches no console; it takes documents, text and a context,
/// which is what lets a fact run the whole verb against an in-memory fixture
/// (the <c>phys</c> precedent, <c>Program.cs</c>).
/// </para>
/// <para>
/// <b>One <c>.roompack</c> per library</b>, not a file per room: a library
/// is compiled as a whole, so its rooms are delivered as a whole, in one
/// file that is replaced in one step and so is never seen half-written or
/// half-updated. Each room inside is still exactly its
/// <see cref="RoomObjectStore"/> container, the unit the linker loads and
/// the cache keys, and the pack's index lets a link read only the rooms its
/// level places.
/// </para>
/// </remarks>
public static partial class RoomCommands
{
    /// <summary>The exit code for a compile, link, or file the run could not deliver.</summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// <c>ssmap room &lt;library.vmf&gt; [-out &lt;pack.roompack&gt;] [stock vbsp options]</c>:
    /// compile every room of a library VMF into one room pack.
    /// </summary>
    /// <param name="disk">Where the library, the game content and the output live.</param>
    /// <param name="searchRoots">Where game installs are, for the cooker's library discovery.</param>
    /// <param name="args">The arguments after <c>room</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the compiles.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// <para>
    /// Every room is compiled even when one fails, so one run reports every
    /// room that needs fixing; the pack holds the rooms that compiled, and
    /// the exit code is failed if any did not. The game is found by vbsp's
    /// rule, <c>-game</c> or else the library's folder's parent, and
    /// <c>-out</c> defaults to <c>&lt;library&gt;.roompack</c> beside the
    /// library, which is also where <c>ssmap link</c> looks by default.
    /// </para>
    /// <para>
    /// The rooms compile side by side, up to <c>-threads</c> at once on one
    /// shared pool (<see cref="RoomLibraryCompiler"/> says why), and one line
    /// per room is printed in library order whatever order they finish in,
    /// so the log, the pack and the exit code are the same at any thread
    /// count. The pack is written once every room has ended, through
    /// <see cref="IFileSystem.ReplaceAsync"/>: a run that is cancelled, or
    /// fails before the end, leaves the previous pack (or none) in place and
    /// no temporary behind.
    /// </para>
    /// </remarks>
    public static Task<int> RunRoomAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default) =>
        RunRoomAsync(disk, searchRoots, args, output, OpenCacheStoreAsync, cancellationToken);

    /// <summary>
    /// <see cref="RunRoomAsync(IFileSystem, IReadOnlyList{VPath}, IReadOnlyList{string}, TextWriter, CancellationToken)"/>
    /// with the cache store <c>-incremental</c> opens given by the host.
    /// </summary>
    /// <param name="disk">Where the library, the game content and the output live.</param>
    /// <param name="searchRoots">Where game installs are, for the cooker's library discovery.</param>
    /// <param name="args">The arguments after <c>room</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="openCache">
    /// Opens the store at the path <c>-incremental</c> names
    /// (<see cref="HostBackends.CachePathFor"/>: <c>&lt;library&gt;.sscache.db</c>
    /// beside the library, or in <c>-cache-dir</c>), or returns null when it
    /// cannot; the verb disposes what it returns. Called only under
    /// <c>-incremental</c> without <c>-nocache</c>. The default opens the
    /// SQLite store (<see cref="HostBackends.OpenCacheAsync"/>); a host or a
    /// fact passes its own to put the store elsewhere.
    /// </param>
    /// <param name="cancellationToken">Cancels the compiles.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// <para>
    /// <b>Incremental.</b> With <c>-incremental</c>, each room is looked up
    /// in the store by its key (<see cref="RoomCacheKey"/>: its own content
    /// after the split, the options, the library's navigation and name keys,
    /// the cooker, this build) and its game content is checked; a room that
    /// hits is reused section for section, and only the others compile
    /// (<see cref="RoomLibraryBuild"/>). The pack is byte for byte the pack a
    /// run without the cache writes: the log says <c>reused</c> instead of
    /// <c>compiled</c> for a reused room and adds one
    /// <c>N compiled, M reused</c> line, and nothing else changes. The rows
    /// are committed after the pack is written, so a run that is cancelled
    /// or fails leaves the store as it was.
    /// </para>
    /// <para>
    /// The cache flags are <c>ssmap all</c>'s: <c>-incremental</c> turns it
    /// on, <c>-nocache</c> turns it off again (for a script that always
    /// passes <c>-incremental</c>), and <c>-cache-dir &lt;dir&gt;</c> puts the
    /// store in another folder. None of them is a stock vbsp option, and none
    /// is an input of the pack's id, so the pack does not depend on them.
    /// </para>
    /// </remarks>
    public static async Task<int> RunRoomAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        Func<string, CancellationToken, Task<ICacheStore?>> openCache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(searchRoots);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(openCache);

        // -out is this verb's, not stock vbsp's: take it out of the line first
        // so the stock parser never sees an option it would (correctly) refuse.
        List<string> stock = [];
        string? outDirectory = null;
        string? cacheDirectory = null;
        bool incremental = false;
        bool noCache = false;
        bool light = true;
        bool doorLight = true;
        string? vradLine = null;
        RoomNavPackOptions navOptions = new();
        for (int i = 0; i < args.Count; i++)
        {
            if (Take(args, i, "out", out string o))
            {
                outDirectory = o;
                i++;
            }
            else if (Take(args, i, "cache-dir", out string c))
            {
                cacheDirectory = c;
                i++;
            }
            else if (IsFlag(args[i], "incremental"))
            {
                incremental = true;
            }
            else if (IsFlag(args[i], "nocache"))
            {
                noCache = true;
            }
            else if (Take(args, i, "nav-codec", out string codec))
            {
                if (!NavCompression.TryParse(codec, out NavCompression compression))
                {
                    await output.WriteLineAsync($"ssmap room: -nav-codec \"{codec}\" is not none, deflate[:0-9] or brotli[:0-11]")
                        .ConfigureAwait(false);
                    return Program.ExitUsage;
                }

                navOptions = navOptions with { Compression = compression };
                i++;
            }
            else if (IsFlag(args[i], "nav-turn0"))
            {
                navOptions = navOptions with { StoreAllTurns = false };
            }
            else if (IsFlag(args[i], "nolight"))
            {
                light = false;
            }
            else if (IsFlag(args[i], "nodoorlight"))
            {
                doorLight = false;
            }
            else if (Take(args, i, "vrad", out string vrad))
            {
                vradLine = vrad;
                i++;
            }
            else
            {
                stock.Add(args[i]);
            }
        }

        // The base bake's vrad switches (-vrad "<stock vrad options>"),
        // parsed as ssmap vrad parses its own line; stock's defaults without.
        VradOptions? vradOptions = null;
        if (light)
        {
            StockArgsResult<VradOptions> vradParsed = StockArgs.ParseVrad(
                [.. (vradLine ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries), "room"]);
            foreach (CompileDiagnostic diagnostic in vradParsed.Diagnostics)
            {
                await output.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}").ConfigureAwait(false);
            }

            if (vradParsed.HasErrors || vradParsed.Options.LuxelDensity < 1.0f)
            {
                await output.WriteLineAsync(
                    $"ssmap room: -vrad \"{vradLine}\" is not a vrad line a room can be lit with (-luxeldensity below 1 changes the room's geometry)")
                    .ConfigureAwait(false);
                return Program.ExitUsage;
            }

            vradOptions = vradParsed.Options;
        }
        else if (vradLine is not null)
        {
            await output.WriteLineAsync("ssmap room: -vrad sets how the rooms are lit, and -nolight lights none").ConfigureAwait(false);
            return Program.ExitUsage;
        }
        else if (!doorLight)
        {
            await output.WriteLineAsync("ssmap room: -nodoorlight leaves out lit rooms' door light, and -nolight lights none").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        StockArgsResult<VbspOptions> parsed = StockArgs.ParseVbsp(stock);
        foreach (CompileDiagnostic diagnostic in parsed.Diagnostics)
        {
            await output.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}").ConfigureAwait(false);
        }

        if (parsed.HasErrors || parsed.MapPath is null)
        {
            await output.WriteLineAsync(
                "usage: ssmap room <library.vmf> [-out <pack.roompack>] [-nav-turn0] [-nav-codec <none|deflate[:n]|brotli[:n]>]"
                + " [-nolight | -vrad \"<stock vrad options>\" [-nodoorlight]] [-incremental [-cache-dir <dir>] | -nocache] [stock vbsp options]")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string source = Path.GetFullPath(parsed.MapPath);
        if (!VPath.TryCreate(source, out VPath libraryPath))
        {
            await output.WriteLineAsync($"ssmap room: \"{source}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // -out resolves against the current directory like the map path does;
        // taken raw, a relative -out landed under the disk root and a rooted
        // one lost its Windows drive.
        if (!TryHostPath(outDirectory ?? DefaultPack(source), out VPath packPath))
        {
            await output.WriteLineAsync($"ssmap room: -out \"{outDirectory}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // The library first: a library that does not split into rooms is
        // refused before a game is mounted or a cooker loaded.
        IReadOnlyList<LibraryRoom> rooms;
        NavSettings? navSettings;
        Guid packId;
        IReadOnlyList<VmfChunk> libraryEntities;
        RoomLibraryOptions libraryOptions;
        RoomLightingSettings? lighting;
        string? skyboxRoom;
        try
        {
            byte[] libraryBytes = await ReadBytesAsync(disk, libraryPath, cancellationToken).ConfigureAwait(false);
            VmfDocument libraryVmf = await VmfDocument.ParseAsync(libraryBytes, cancellationToken).ConfigureAwait(false);

            // The library-wide entities in the gaps (the sun, fog and the
            // like) go into the pack's library section, not away.
            RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(libraryVmf);

            // The skybox room compiles and packs like any room, after them;
            // a library section names it, since no level places it.
            rooms = split.Skybox is { } skybox ? [.. split.Rooms, skybox] : split.Rooms;
            skyboxRoom = split.Skybox?.Definition.Name;
            libraryEntities = split.LibraryEntities;
            libraryOptions = split.Options;
            navSettings = NavSettings.FromLibrary(libraryVmf);
            if (navSettings is not null && rooms.Count > 0)
            {
                _ = navSettings.CellVoxels(rooms[0].Definition.CellSize);
            }

            // The pack's id: a function of what shapes it, so a rebuild of the
            // same library writes the same pack (RoomCompileIds).
            // Lit rooms add how they were lit to the id; an unlit pack keeps
            // the id it had before the bake existed.
            lighting = vradOptions is null
                ? null
                : new RoomLightingSettings(vradOptions with { Compliance = parsed.Options.Compliance })
                {
                    Sun = RoomLightingSettings.SunOf(libraryEntities),
                    DoorLight = doorLight,

                    // Every sky room's bake recasts into the skybox, which
                    // the library compile therefore compiles first.
                    Skybox = split.Skybox,
                };
            packId = RoomCompileIds.PackId(
                libraryBytes,
                [.. PackIdOptions(stock, parsed.MapPath), .. (lighting is null ? Array.Empty<string>() : [lighting.Describe()])],
                Describe(navSettings, navOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ChunkFileException or RoomLibraryException)
        {
            await output.WriteLineAsync($"ssmap room: {source}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        // vbsp's game rule: -game, else the map directory's parent (the Hammer layout).
        string gameDirectory = parsed.GameDirectory is null
            ? Path.GetDirectoryName(Path.GetDirectoryName(source)!)!
            : Path.GetFullPath(parsed.GameDirectory);

        ISteamAppLocator? steam = VbspHost.SteamFor(disk, searchRoots);
        GameContentMounter.Result mounted;
        try
        {
            mounted = await VbspCommand
                .MountGameAsync(disk, gameDirectory, steam, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            await output.WriteLineAsync($"ssmap room: cannot mount {gameDirectory}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        await VbspCommand.WriteSkippedAsync(mounted, "ssmap room", output).ConfigureAwait(false);

        // The format pipeline, exactly where vbsp runs it: after the mount
        // (it reads the appid and Tools key off the mounted gameinfo), before
        // the compile.
        FormatResolution.Result resolution = FormatResolution.Resolve(
            parsed.Format, parsed.PresetName, parsed.NoFormatDetect, parsed.NoToolsArgs, mounted.GameInfo);
        VbspOptions options = parsed.Options with { Format = resolution.Resolved };
        foreach (CompileDiagnostic diagnostic in resolution.Diagnostics)
        {
            await output.WriteLineAsync($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}")
                .ConfigureAwait(false);
        }

        VbspHost.CookerSetup setup = await VbspHost.OpenCookerAsync(
            disk, searchRoots, options, ["room", .. args], "ssmap room", output, cancellationToken)
            .ConfigureAwait(false);
        if (setup.Exit is int relaunchExit)
        {
            return relaunchExit;
        }

        await using ICollisionCooker? cooker = setup.Cooker;

        // One hull cache for the run: the rooms of a library name the same
        // few prop models, so each is cooked about once instead of once per
        // room. It lives exactly as long as this command, so the default
        // bound is only a ceiling, never a leak.
        using PropHullCache hulls = new();

        RoomLibraryCompileSettings settings = new(options, mounted.Content)
        {
            CollisionCooker = cooker,
            PropHullCache = hulls,
            Nav = navSettings,
            NameKeys = libraryOptions.NameKeySet,
            Lighting = lighting,
            Parallelism = parsed.Threads is int degree && degree > 0
                ? new CompileParallelism { MaxDegree = degree }
                : CompileParallelism.Default,
        };

        // -incremental: the store beside the library (or in -cache-dir),
        // opened only when asked. A store that will not open is said out
        // loud and the run compiles everything, as ssmap all does.
        ICacheStore? store = null;
        if (incremental && !noCache)
        {
            string storePath = HostBackends.CachePathFor(
                cacheDirectory, Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source));
            store = await openCache(storePath, cancellationToken).ConfigureAwait(false);
            if (store is null)
            {
                await output.WriteLineAsync(
                    "ssmap room: cache: -incremental opened no store ("
                    + (HostBackends.MissingReason ?? "the store could not be opened")
                    + "); every room compiles this run").ConfigureAwait(false);
            }
        }

        await using ICacheStore? ownedStore = store;
        using RoomCompileCache? cache = store is null
            ? null
            : new RoomCompileCache(
                store,
                CachePolicy.Default,
                new RoomCacheInputs(options)
                {
                    Nav = navSettings,
                    PackOptions = navOptions,
                    NameKeys = libraryOptions.NameKeySet,
                    Lighting = lighting,
                    ContextTags = HostBackends.ContextTagsFor(options.Format.PresetName, cooker),
                },
                mounted.Content);

        // Called in library order, one room at a time: the lines, the
        // failure count and the pack's room list come out the same whatever
        // order the rooms finished in, and whichever rooms were reused.
        int failed = 0;
        int reused = 0;
        List<RoomPackItem> packed = [];
        async ValueTask ReportAsync(RoomBuildOutcome outcome, CancellationToken token)
        {
            RoomDefinition definition = outcome.Room.Definition;
            if (outcome.Item is { } item)
            {
                // The container, the link work and the navigation the
                // library compile did ahead for the room (RoomPackItem.CreateAsync),
                // or the same sections from the cache.
                packed.Add(item);
                reused += outcome.Reused ? 1 : 0;
                await output.WriteLineAsync(
                    $"ssmap room: {(outcome.Reused ? "reused" : "compiled")} {definition.Name}"
                    + $" ({outcome.ClusterCount} clusters, {definition.Sockets.Count} sockets)")
                    .ConfigureAwait(false);

                // What the naming rule warned of (a misplaced placeholder, a
                // local name nothing defines): the room compiles, but the
                // author should look.
                foreach (string warning in outcome.NameWarnings)
                {
                    await output.WriteLineAsync($"ssmap room: warning: {warning}").ConfigureAwait(false);
                }

                // What the navigation could not read (a prop whose model the
                // content lacks): the room compiles without that obstacle. A
                // reused room replays the list its compile stored, so the log
                // is the clean run's whichever rooms came from the cache.
                foreach (string warning in outcome.NavWarnings)
                {
                    await output.WriteLineAsync($"ssmap room: warning: room \"{definition.Name}\": {warning}").ConfigureAwait(false);
                }

                return;
            }

            failed++;
            await output.WriteLineAsync(outcome.Error is RoomLintException
                ? $"ssmap room: room \"{definition.Name}\" is not linkable: {outcome.Error.Message}"
                : $"ssmap room: room \"{definition.Name}\": {outcome.Error!.Message}")
                .ConfigureAwait(false);
        }

        await RoomLibraryBuild.BuildAsync(rooms, settings, navOptions, cache, ReportAsync, cancellationToken).ConfigureAwait(false);

        // The compile id always; the library-wide entities and the library's
        // settings only when there are some, so a library that sets nothing
        // writes the pack it would without them. Tags are looked up, so their
        // order is the writer's.
        List<RoomPackSectionData> librarySections = [RoomCompileIds.Section(packId)];
        if (libraryEntities.Count > 0)
        {
            librarySections.Add(RoomLibraryEntities.ToSection(libraryEntities));
        }

        if (libraryOptions.ToSection() is { } optionsSection)
        {
            librarySections.Add(optionsSection);
        }

        if (skyboxRoom is not null)
        {
            librarySections.Add(RoomLibrarySkybox.ToSection(skyboxRoom));
        }


        try
        {
            await disk.ReplaceAsync(
                packPath,
                async (stream, token) => await RoomPack.SaveAsync(librarySections, packed, stream, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap room: cannot write {HostPaths.Display(packPath)}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        await output.WriteLineAsync(
            $"ssmap room: wrote {HostPaths.Display(packPath)} ({packed.Count} of {rooms.Count} room(s))")
            .ConfigureAwait(false);

        if (incremental)
        {
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture, $"ssmap room: {packed.Count - reused} compiled, {reused} reused")).ConfigureAwait(false);
        }

        // The rows go in once the pack is out: a run that stopped before
        // here staged nothing. A commit that fails is a lost cache, not a
        // lost pack.
        if (cache is not null)
        {
            try
            {
                RoomCacheCommit commit = await cache.CommitAsync(cancellationToken).ConfigureAwait(false);
                if (commit.GcFailure is { } why)
                {
                    await output.WriteLineAsync($"ssmap room: cache: gc failed ({why}); the store was left as it was")
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await output.WriteLineAsync($"ssmap room: cache: commit failed ({exception.Message}); this run's rooms were not stored")
                    .ConfigureAwait(false);
            }
        }

        if (failed > 0)
        {
            await output.WriteLineAsync($"ssmap room: {failed} of {rooms.Count} room(s) failed").ConfigureAwait(false);
            return ExitFailed;
        }

        return Program.ExitSuccess;
    }

    /// <summary>
    /// <c>ssmap link &lt;level.yaml&gt; [-rooms &lt;pack.roompack&gt;] [-out &lt;map.bsp&gt;]</c>:
    /// link the level's rooms into one map; or, with <c>--flatten</c>,
    /// write the same level as one VMF (<c>-out</c> then names the VMF) for
    /// vbsp to compile as the reference.
    /// </summary>
    /// <param name="disk">Where the level, the room pack, the library and the output live.</param>
    /// <param name="args">The arguments after <c>link</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the link.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// The level's <c>library</c> is resolved against the level file's
    /// folder. The link reads the room pack's index and then the rooms the
    /// level places, and nothing else of it, with <c>-rooms</c> defaulting to
    /// <c>&lt;library&gt;.roompack</c> beside the library (where
    /// <c>ssmap room</c> writes by default). A room the level places that the
    /// pack does not hold is refused, naming the room, where the level places
    /// it, and the pack.
    /// The map defaults to the level file with <c>.bsp</c>, the flattened VMF
    /// to the level file with <c>.vmf</c>.
    /// <para>
    /// <b>Entity budget.</b> The link totals the level's edicts from the
    /// rooms' entity counts before it links anything
    /// (<see cref="LevelEntityBudget"/>): it refuses a level over the
    /// 2048-edict cap, warns when one eats into the reserve the game needs
    /// at runtime, and always prints the headroom line. The reserve is the
    /// library's (<see cref="RoomLibraryOptions.EntityReserveKey"/> on its
    /// worldspawn, kept in the pack) unless <c>-entity-reserve N</c> gives
    /// another, and 512 when neither does. <c>--flatten</c> budgets
    /// nothing, so it takes no reserve.
    /// </para>
    /// <para>
    /// <b>Brush fold.</b> The link merges touching box brushes of the world
    /// into larger boxes (<see cref="LevelLinkOptions.FoldBrushes"/>), and
    /// the line that reports the map written gives its brushes and how many
    /// the fold merged away; <c>-nofold</c> writes the rooms' brushes as
    /// compiled. <c>--flatten</c> writes brushes for vbsp, so it takes no
    /// <c>-nofold</c>.
    /// </para>
    /// <para>
    /// <b>Visibility.</b> The link composes the level's PVS through its
    /// doorways (<see cref="LevelLinkOptions.DoorVisibility"/>) and reports
    /// the cluster pairs it marks visible and the visibility lump's size;
    /// <c>-nodoorvis</c> writes the door graph's closure instead, in which
    /// every cluster sees every other. <c>--flatten</c> leaves visibility to
    /// vvis, so it takes no <c>-nodoorvis</c>.
    /// </para>
    /// </remarks>
    public static async Task<int> RunLinkAsync(
        IFileSystem disk,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        List<string> rest = [];
        List<string> roomsPacks = [];
        string? outPath = null;
        string? reserveText = null;
        bool flatten = false;
        bool modEntities = false;
        bool noFold = false;
        bool noDoorVis = false;
        LinkNavOptions nav = new();
        for (int i = 0; i < args.Count; i++)
        {
            if (Take(args, i, "rooms", out string r))
            {
                roomsPacks.Add(r);
                i++;
            }
            else if (Take(args, i, "entity-reserve", out string e))
            {
                reserveText = e;
                i++;
            }
            else if (Take(args, i, "nav-codec", out string codec))
            {
                if (!NavCompression.TryParse(codec, out NavCompression compression))
                {
                    await output.WriteLineAsync($"ssmap link: -nav-codec \"{codec}\" is not none, deflate[:0-9] or brotli[:0-11]")
                        .ConfigureAwait(false);
                    return Program.ExitUsage;
                }

                nav = nav with { Compression = compression };
                i++;
            }
            else if (IsFlag(args[i], "no-nav"))
            {
                nav = nav with { Skip = true };
            }
            else if (IsFlag(args[i], "require-nav"))
            {
                nav = nav with { Require = true };
            }
            else if (Take(args, i, "out", out string o))
            {
                outPath = o;
                i++;
            }
            else if (IsFlag(args[i], "flatten"))
            {
                flatten = true;
            }
            else if (IsFlag(args[i], "mod-entities"))
            {
                modEntities = true;
            }
            else if (IsFlag(args[i], "nofold"))
            {
                noFold = true;
            }
            else if (IsFlag(args[i], "nodoorvis"))
            {
                noDoorVis = true;
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        if (rest.Count != 1 || (flatten && (roomsPacks.Count > 0 || reserveText is not null || noFold || noDoorVis)))
        {
            await output.WriteLineAsync(
                "usage: ssmap link <level.yaml> [-rooms <pack.roompack> | -rooms <key>=<pack.roompack> ...] [-entity-reserve <n>] [-mod-entities] [-nofold] [-nodoorvis] [-out <map.bsp>] [-no-nav | -require-nav] [-nav-codec <codec>]\n"
                + "       ssmap link <level.yaml> --flatten [-mod-entities] [-out <map.vmf>]")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        int? reserve = null;
        if (reserveText is not null)
        {
            if (!RoomLibraryOptions.TryParseReserve(reserveText, out int parsed))
            {
                await output.WriteLineAsync(
                    $"ssmap link: -entity-reserve is a whole number of edicts from 0 to {EntityClassTable.EdictCap}")
                    .ConfigureAwait(false);
                return Program.ExitUsage;
            }

            reserve = parsed;
        }

        string levelPath = Path.GetFullPath(rest[0]);
        string target = outPath is null
            ? Path.ChangeExtension(levelPath, flatten ? ".vmf" : ".bsp")
            : Path.GetFullPath(outPath);
        if (!VPath.TryCreate(levelPath, out VPath levelVPath) || !VPath.TryCreate(target, out VPath targetPath))
        {
            await output.WriteLineAsync($"ssmap link: \"{levelPath}\" or -out \"{target}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        LevelGrid level;
        byte[] levelBytes;
        try
        {
            levelBytes = await ReadBytesAsync(disk, levelVPath, cancellationToken).ConfigureAwait(false);
            string text = await new StreamReader(new MemoryStream(levelBytes), Encoding.UTF8)
                .ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            level = LevelYaml.Parse(text, Path.GetFileNameWithoutExtension(levelPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap link: cannot read {levelPath}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }
        catch (LevelFileException exception)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        LevelLinkOptions linkOptions = new() { EntityReserve = reserve, ModEntities = modEntities, FoldBrushes = !noFold, DoorVisibility = !noDoorVis };
        if (level.Libraries is not null)
        {
            // Several libraries (the rooms design, 17.2): one pack per key.
            return flatten
                ? await FlattenLibrariesAsync(disk, level, levelPath, targetPath, modEntities, output, cancellationToken).ConfigureAwait(false)
                : await LinkLibrariesAsync(disk, level, levelBytes, levelPath, roomsPacks, linkOptions, targetPath, nav, output, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (roomsPacks.Count > 1)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath} names one library; give -rooms once, with its pack.").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string? roomsPack = roomsPacks.Count == 0 ? null : roomsPacks[0];
        string libraryPath;
        try
        {
            libraryPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(levelPath)!, level.Library));
        }
        catch (ArgumentException)
        {
            // A path the host cannot hold at all (a NUL, say): the level
            // file's problem, reported as such rather than thrown.
            await output.WriteLineAsync($"ssmap link: {levelPath}: the library \"{level.Library}\" is not a usable path")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        return flatten
            ? await FlattenAsync(disk, level, levelPath, libraryPath, targetPath, modEntities, output, cancellationToken).ConfigureAwait(false)
            : await LinkAsync(
                disk, level, levelBytes, levelPath, libraryPath, roomsPack, linkOptions, targetPath, nav, output, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>ssmap layout &lt;library.vmf&gt; -rows R -columns C -seed N [-empty &lt;ratio&gt;] [-out &lt;level.yaml&gt;]</c>:
    /// write a seeded level of the library's rooms.
    /// </summary>
    /// <param name="disk">Where the library and the level live.</param>
    /// <param name="args">The arguments after <c>layout</c>.</param>
    /// <param name="output">Where the log goes, and the level when there is no <c>-out</c>.</param>
    /// <param name="cancellationToken">Cancels the reads and the write.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// The level is valid by construction (<see cref="LevelGenerator"/>):
    /// sockets line up and every room is reachable. The same library and
    /// seed always write the same file. The level names the library relative
    /// to where the level is written (or to the current folder, when it is
    /// printed), so <c>ssmap link</c> finds it from the file. The options
    /// take one dash or two.
    /// <para>
    /// <b>Transitions</b> (the rooms design, 11.2). A library with role rooms
    /// places one up room and one down room per level, so the level needs the
    /// maps above and below: <c>-up-map</c> and <c>-down-map</c>, or
    /// <c>-no-up</c> and <c>-no-down</c> for the top or bottom level;
    /// <c>-transition-distance N</c> keeps the two at least N doors apart.
    /// <c>-sequence K -name &lt;base&gt;</c> writes a run instead:
    /// <c>&lt;base&gt;_01.yaml</c> to <c>&lt;base&gt;_K.yaml</c> from seeds N,
    /// N + 1, ..., into <c>-out</c>'s folder (the current one by default),
    /// each level's <c>down_map</c> the next's name and its <c>up_map</c> the
    /// previous one's, the first <c>up: none</c> and the last
    /// <c>down: none</c>. A library without roles writes the levels it always
    /// wrote unless a transition option is given.
    /// </para>
    /// </remarks>
    public static async Task<int> RunLayoutAsync(
        IFileSystem disk,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        const string Usage =
            "usage: ssmap layout <library.vmf> -rows <n> -columns <n> -seed <n> [-empty <ratio>]"
            + " [-rooms <pack.roompack>] [-entity-budget <n>] [-mod-entities] [-out <level.yaml>]"
            + " [-up-map <map> | -no-up] [-down-map <map> | -no-down] [-transition-distance <n>]\n"
            + "       ssmap layout <library.vmf> -rows <n> -columns <n> -seed <n> -sequence <k> -name <base> [-out <folder>] [...]";
        List<string> rest = [];
        string? rows = null, columns = null, seed = null, empty = null, outPath = null, roomsPack = null, budgetText = null;
        string? upMap = null, downMap = null, distanceText = null, sequenceText = null, baseName = null;
        bool modEntities = false, noUp = false, noDown = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (IsFlag(args[i], "mod-entities"))
            {
                modEntities = true;
                continue;
            }

            if (IsFlag(args[i], "no-up"))
            {
                noUp = true;
                continue;
            }

            if (IsFlag(args[i], "no-down"))
            {
                noDown = true;
                continue;
            }

            if (Take(args, i, "rows", out string value))
            {
                rows = value;
            }
            else if (Take(args, i, "columns", out value))
            {
                columns = value;
            }
            else if (Take(args, i, "seed", out value))
            {
                seed = value;
            }
            else if (Take(args, i, "empty", out value))
            {
                empty = value;
            }
            else if (Take(args, i, "out", out value))
            {
                outPath = value;
            }
            else if (Take(args, i, "rooms", out value))
            {
                roomsPack = value;
            }
            else if (Take(args, i, "entity-budget", out value))
            {
                budgetText = value;
            }
            else if (Take(args, i, "up-map", out value))
            {
                upMap = value;
            }
            else if (Take(args, i, "down-map", out value))
            {
                downMap = value;
            }
            else if (Take(args, i, "transition-distance", out value))
            {
                distanceText = value;
            }
            else if (Take(args, i, "sequence", out value))
            {
                sequenceText = value;
            }
            else if (Take(args, i, "name", out value))
            {
                baseName = value;
            }
            else
            {
                rest.Add(args[i]);
                continue;
            }

            i++;
        }

        if (rest.Count != 1 || rows is null || columns is null || seed is null
            || (sequenceText is null) != (baseName is null)
            || (sequenceText is not null && (upMap is not null || downMap is not null || noUp || noDown))
            || (noUp && upMap is not null) || (noDown && downMap is not null))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        int sequence = 0;
        if (sequenceText is not null
            && (!int.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 1 || sequence > 999))
        {
            await output.WriteLineAsync("ssmap layout: -sequence is a whole number of levels from 1 to 999").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        int distance = 0;
        if (distanceText is not null && !int.TryParse(distanceText, NumberStyles.None, CultureInfo.InvariantCulture, out distance))
        {
            await output.WriteLineAsync("ssmap layout: -transition-distance is a whole number of doors from 0").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        foreach ((string flag, string? map) in (ReadOnlySpan<(string, string?)>)[("-up-map", upMap), ("-down-map", downMap), ("-name", baseName)])
        {
            if (map is not null && LevelTransitions.MapNameProblem(map) is { } problem)
            {
                await output.WriteLineAsync($"ssmap layout: {flag} \"{map}\" {problem}").ConfigureAwait(false);
                return Program.ExitUsage;
            }
        }

        if (!int.TryParse(rows, NumberStyles.None, CultureInfo.InvariantCulture, out int rowCount) || rowCount < 1
            || !int.TryParse(columns, NumberStyles.None, CultureInfo.InvariantCulture, out int columnCount) || columnCount < 1
            || !ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seedValue))
        {
            await output.WriteLineAsync("ssmap layout: -rows and -columns are whole numbers from 1, -seed a whole number from 0")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        double ratio = 0;
        if (empty is not null
            && (!double.TryParse(empty, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) || !(ratio >= 0 && ratio < 1)))
        {
            await output.WriteLineAsync("ssmap layout: -empty is a share of the cells, at least 0 and below 1")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        int? explicitBudget = null;
        if (budgetText is not null)
        {
            if (!int.TryParse(budgetText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                || parsed > EntityClassTable.EdictCap)
            {
                await output.WriteLineAsync(
                    $"ssmap layout: -entity-budget is a whole number of edicts from 0 to {EntityClassTable.EdictCap}")
                    .ConfigureAwait(false);
                return Program.ExitUsage;
            }

            explicitBudget = parsed;
        }

        string libraryPath = Path.GetFullPath(rest[0]);
        string? target = outPath is null ? null : Path.GetFullPath(outPath);
        if (!VPath.TryCreate(libraryPath, out VPath libraryVPath))
        {
            await output.WriteLineAsync($"ssmap layout: \"{libraryPath}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        if (!TryHostPath(roomsPack ?? DefaultPack(libraryPath), out VPath packPath))
        {
            await output.WriteLineAsync($"ssmap layout: -rooms \"{roomsPack}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        VPath targetPath = default;
        if (target is not null && !VPath.TryCreate(target, out targetPath))
        {
            await output.WriteLineAsync($"ssmap layout: -out \"{target}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // The grid is checked before the library is read: a grid past the
        // cap is refused whatever the library holds, and reading a large
        // library first cost seconds and hundreds of megabytes for nothing.
        // The refusal reads as it did when the generator made it.
        LevelGeneratorOptions options = new(rowCount, columnCount, seedValue, ratio);
        try
        {
            LevelGenerator.CheckOptions(options);
        }
        catch (ArgumentException exception)
        {
            await output.WriteLineAsync($"ssmap layout: {libraryPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        // With -sequence, -out names the folder the levels go to.
        string? folder = sequenceText is null ? null : target ?? Path.GetFullPath(".");
        List<(VPath Path, string Text)> files = [];
        string text;
        try
        {
            IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(
                await ReadVmfAsync(disk, libraryVPath, cancellationToken).ConfigureAwait(false));
            LayoutEntityBudget? budget = await LayoutBudgetAsync(disk, packPath, rooms, explicitBudget, modEntities, cancellationToken)
                .ConfigureAwait(false);
            string from = folder ?? (target is null ? Path.GetFullPath(".") : Path.GetDirectoryName(target)!);
            string library = Path.GetRelativePath(from, libraryPath).Replace('\\', '/');
            RoomDefinition[] definitions = [.. rooms.Select(r => r.Definition)];
            RoomRole[] roles = [.. rooms.Select(r => r.Role)];
            if (sequenceText is not null)
            {
                foreach (LevelGrid level in LevelGenerator.GenerateSequence(definitions, options, sequence, baseName!, library, budget, roles, distance))
                {
                    LevelGeneratorOptions own = options with { Seed = unchecked(options.Seed + (ulong)files.Count) };
                    string path = Path.Combine(folder!, level.Name + ".yaml");
                    if (!VPath.TryCreate(path, out VPath levelPath))
                    {
                        throw new IOException($"\"{path}\" is not a usable path");
                    }

                    files.Add((levelPath, LevelYaml.Write(level, LevelGenerator.Header(own, level))));
                }

                text = string.Empty;
            }
            else
            {
                string name = target is null ? "level" : Path.GetFileNameWithoutExtension(target);
                bool hasRoles = roles.Any(r => r != RoomRole.None);
                LevelTransitions? transitions = hasRoles || noUp || noDown || upMap is not null || downMap is not null
                    ? new LevelTransitions { NoUp = noUp, NoDown = noDown, UpMap = upMap, DownMap = downMap }
                    : null;
                if (hasRoles && ((!noUp && upMap is null) || (!noDown && downMap is null)))
                {
                    string role = !noUp && upMap is null ? "up" : "down";
                    throw new LinkException(
                        $"the library has role rooms, so the level holds {(role == "up" ? "an" : "a")} {role} room and names its map:"
                        + $" give -{role}-map <map>, or -no-{role} for a level without one.");
                }

                LevelGrid level = LevelGenerator.Generate(
                    definitions, options, name, library, budget,
                    new LayoutTransitions(roles) { NoUp = noUp, NoDown = noDown, MinDistance = distance }).WithTransitions(transitions);
                text = LevelYaml.Write(level, LevelGenerator.Header(options, level));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ChunkFileException or RoomLibraryException or LinkException or ArgumentException)
        {
            await output.WriteLineAsync($"ssmap layout: {libraryPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        if (sequenceText is null && target is null)
        {
            await output.WriteAsync(text).ConfigureAwait(false);
            return Program.ExitSuccess;
        }

        if (sequenceText is null)
        {
            files.Add((targetPath, text));
        }

        foreach ((VPath path, string content) in files)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            try
            {
                await disk.ReplaceAsync(
                    path,
                    async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await output.WriteLineAsync($"ssmap layout: cannot write {HostPaths.Display(path)}: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
            }

            await output.WriteLineAsync($"ssmap layout: wrote {HostPaths.Display(path)}").ConfigureAwait(false);
        }

        return Program.ExitSuccess;
    }

    /// <summary>
    /// The entity budget <c>ssmap layout</c> generates within: the given one,
    /// else the link's <c>cap − reserve</c> with the library's reserve, from
    /// the rooms' counts in the library's pack, each with what the linker may
    /// write for it in the emission mode asked for (<c>-mod-entities</c>).
    /// </summary>
    /// <returns>
    /// The budget; or null when none was given and the pack is missing or
    /// lacks a room's counts, since there is then nothing to count with.
    /// </returns>
    /// <exception cref="LinkException">
    /// A budget was given and there is no pack, or the pack lacks a room's
    /// counts; or the pack cannot be read.
    /// </exception>
    private static async Task<LayoutEntityBudget?> LayoutBudgetAsync(
        IFileSystem disk, VPath packPath, IReadOnlyList<LibraryRoom> rooms, int? explicitBudget, bool modEntities, CancellationToken cancellationToken)
    {
        string pack = HostPaths.Display(packPath);
        PackCounts? counts = await ReadPackCountsAsync(disk, packPath, cancellationToken).ConfigureAwait(false);
        if (counts is null)
        {
            return explicitBudget is null
                ? null
                : throw new LinkException(
                    $"-entity-budget counts the rooms' entities, and there is no room pack {pack};"
                    + " compile the library with ssmap room, or point -rooms at its pack.");
        }

        List<int> edicts = new(rooms.Count);
        foreach (LibraryRoom room in rooms)
        {
            string name = room.Definition.Name;
            if (counts.Counts.GetValueOrDefault(name) is not { } found)
            {
                return explicitBudget is null
                    ? null
                    : throw new LinkException(
                        $"-entity-budget counts the rooms' entities, and the room pack {pack} has no counts for room \"{name}\";"
                        + " recompile the library with ssmap room.");
            }

            // A room pays for the entities the linker writes for it too (its
            // flags, and without -mod-entities its hub's stock fallback): at
            // most what its names say, so the layout never under-counts.
            int written = counts.Names.GetValueOrDefault(name)?.WrittenEdictsBound(modEntities) ?? 0;
            // And, when the library asks for door portals, its share of its
            // joints' portals: half its sockets, rounded up.
            int doors = counts.Options.HasDoorPortals ? LevelDoorPortals.EdictsBound(room.Definition) : 0;
            edicts.Add(found.Tally(EntityClassTable.Default).Edicts + written + TransitionEdictsBound(room, modEntities) + doors);
        }

        int budget = explicitBudget
            ?? EntityClassTable.EdictCap - LevelEntityBudget.ReserveFor(LevelLinkOptions.Default, counts.Options);

        // The library's own entities are the level's whatever it places, as
        // the link counts them, and so are its skybox room's, which every
        // level carries once below its grid.
        int skybox = counts.Skybox is { } sky && counts.Counts.GetValueOrDefault(sky) is { } skyCounts
            ? skyCounts.Tally(EntityClassTable.Default).Edicts
            : 0;
        return new LayoutEntityBudget(budget, edicts)
        {
            LevelEdicts = RoomLibraryEntities.Count(counts.LibraryEntities).Tally(EntityClassTable.Default).Edicts + skybox,
        };
    }

    /// <summary>
    /// What the stock fallback writes for a role room at most, in edicts
    /// (the rooms design, 11.6): a landmark for either role, and for an up
    /// room the level's player starts, its arrival and its spawn points. The
    /// volume becomes the changelevel, one edict for one, and with
    /// <c>-mod-entities</c> the room's transition costs no edict at all
    /// (<c>logic_level_transition</c> is server-only), so nothing is added.
    /// The rooms' own player starts, which the level strips, are still
    /// counted, so this never under-counts.
    /// </summary>
    private static int TransitionEdictsBound(LibraryRoom room, bool modEntities)
    {
        if (modEntities || room.Role == RoomRole.None)
        {
            return 0;
        }

        return room.Role == RoomRole.Down
            ? 1
            : 2 + RoomPois.Extract(room.Document).Pois.Count(p => p.Type == LevelTransition.Spell(PoiType.Spawn));
    }

    /// <summary>
    /// Runs <c>ssmap rooms &lt;library.vmf&gt;</c>: lists every room in a
    /// library VMF with its cell, and every door with where it is and how big.
    /// </summary>
    /// <param name="disk">Where the library lives.</param>
    /// <param name="args">The arguments after <c>rooms</c>: the library VMF.</param>
    /// <param name="output">Where the listing goes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// <para>
    /// The library is read and checked exactly as <c>ssmap room</c> reads it
    /// (<see cref="RoomLibraryVmf.Split"/>), so a library this lists is one
    /// the room compile accepts, and one it refuses is refused with the same
    /// message. Nothing is compiled and no game is mounted.
    /// </para>
    /// <para>
    /// When the library's room pack is there (<c>-rooms</c>, else
    /// <c>&lt;library&gt;.roompack</c> beside it), each room also lists its
    /// entities as the room compile counted them: how many reach a linked
    /// map, how many of those take an edict, and how many are server-only
    /// (<see cref="EntityClassTable"/>); and the listing opens with the
    /// library's entity budget. Without a pack the listing is the library's
    /// alone, since entities are counted after the compile.
    /// </para>
    /// <para>
    /// <c>ssmap rooms &lt;level.yaml&gt;</c> lists every library the level
    /// names, in its order, each under a <c>library {key}: {path}</c> line
    /// (a <c>library:</c> level's one library under its path), and then the
    /// warnings the link and the flatten give for the level's libraries
    /// (<see cref="LevelLibraries.CheckLibraries"/>); a refusal of theirs
    /// fails the listing with the same message. Each library's pack is
    /// found as <c>ssmap link</c> finds it: beside its VMF, or named by
    /// <c>-rooms &lt;key&gt;=&lt;pack&gt;</c>.
    /// </para>
    /// <para>
    /// <c>ssmap rooms -rooms &lt;pack&gt;</c> with no library prints the
    /// pack's section table instead (<see cref="RoomPackSectionTable"/>):
    /// every library and room section with its tag, offset, stored length,
    /// codec, decoded length, revision and a hash prefix. Two packs that
    /// should be the same (an incremental run and a clean one) can be
    /// compared line by line, and a room whose sections changed shows which.
    /// </para>
    /// </remarks>
    public static async Task<int> RunRoomsAsync(
        IFileSystem disk,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        List<string> rest = [];
        string? roomsPack = null;
        List<string> roomsPacks = [];
        for (int i = 0; i < args.Count; i++)
        {
            if (Take(args, i, "rooms", out string r))
            {
                roomsPack = r;
                roomsPacks.Add(r);
                i++;
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        // A level: every library it names (the rooms design, 17.2).
        if (rest.Count == 1 && IsLevelFile(rest[0]))
        {
            return await RoomsOfLevelAsync(disk, rest[0], roomsPacks, output, cancellationToken).ConfigureAwait(false);
        }

        // A pack alone: its section table, which needs no library.
        if (rest.Count == 0 && roomsPack is not null)
        {
            return await SectionTableAsync(disk, roomsPack, output, cancellationToken).ConfigureAwait(false);
        }

        if (rest.Count != 1 || rest[0].StartsWith('-'))
        {
            await output.WriteLineAsync(
                "usage: ssmap rooms <library.vmf> [-rooms <pack.roompack>]\n       ssmap rooms <level.yaml> [-rooms <pack.roompack> | -rooms <key>=<pack.roompack> ...]\n       ssmap rooms -rooms <pack.roompack>")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string libraryPath = Path.GetFullPath(rest[0]);
        if (!VPath.TryCreate(libraryPath, out VPath libraryVPath)
            || !TryHostPath(roomsPack ?? DefaultPack(libraryPath), out VPath packPath))
        {
            await output.WriteLineAsync($"ssmap rooms: \"{libraryPath}\" or -rooms \"{roomsPack}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        IReadOnlyList<LibraryRoom> rooms;
        try
        {
            rooms = RoomLibraryVmf.Split(await ReadVmfAsync(disk, libraryVPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ChunkFileException or RoomLibraryException)
        {
            await output.WriteLineAsync($"ssmap rooms: {libraryPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        PackCounts? counts;
        try
        {
            counts = await ReadPackCountsAsync(disk, packPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LinkException)
        {
            await output.WriteLineAsync($"ssmap rooms: {HostPaths.Display(packPath)}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        await output.WriteAsync(counts is null
            ? DescribeLibrary(rooms)
            : DescribeLibrary(rooms, counts.Counts, counts.Options, EntityClassTable.Default, counts.Names, counts.LibraryEntities, counts.Lighting)).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>Prints a pack's section table for <c>ssmap rooms -rooms &lt;pack&gt;</c>.</summary>
    private static async Task<int> SectionTableAsync(IFileSystem disk, string pack, TextWriter output, CancellationToken cancellationToken)
    {
        if (!TryHostPath(pack, out VPath packPath))
        {
            await output.WriteLineAsync($"ssmap rooms: -rooms \"{pack}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        IReadOnlyList<RoomPackSectionInfo> table;
        try
        {
            await using Stream stream = await disk.OpenReadAsync(packPath, cancellationToken).ConfigureAwait(false);
            table = await RoomPackSectionTable.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LinkException)
        {
            await output.WriteLineAsync($"ssmap rooms: {HostPaths.Display(packPath)}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        await output.WriteAsync(RoomPackSectionTable.Format(table)).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// The listing <c>ssmap rooms</c> prints: one block per room, in library
    /// order, with its cell and its doors.
    /// </summary>
    /// <param name="rooms">The library's rooms, as <see cref="RoomLibraryVmf.Split"/> gives them.</param>
    /// <returns>The listing, one line per room and per door.</returns>
    /// <remarks>
    /// <para>
    /// Each room line gives the name, the cell's low corner in library
    /// coordinates (where its <c>info_room</c> stands) and the cell's size.
    /// Each door line gives the wall (east is +x, north is +y), the socket's
    /// name, the door plug's box in library coordinates, and the opening's
    /// width along the wall, its height and the plug's depth into the room.
    /// </para>
    /// <para>
    /// The box is the one the linter holds the plug to
    /// (<see cref="RoomLinter.SealBox"/>) moved to the room's corner, so it is
    /// where the plug brush has to be, and where a door is cut when the room
    /// is joined.
    /// </para>
    /// </remarks>
    public static string DescribeLibrary(IReadOnlyList<LibraryRoom> rooms) => Describe(rooms, null, RoomLibraryOptions.None, null);

    /// <summary>
    /// The listing <c>ssmap rooms</c> prints when the library's pack is
    /// there: <see cref="DescribeLibrary(IReadOnlyList{LibraryRoom})"/>'s,
    /// opened by the library's entity budget, with each room's entities
    /// after its line.
    /// </summary>
    /// <param name="rooms">The library's rooms, as <see cref="RoomLibraryVmf.Split"/> gives them.</param>
    /// <param name="counts">The rooms' entity counts from the pack, by name (<see cref="RoomPack.ReadEntityCountsAsync"/>).</param>
    /// <param name="options">The library's settings from the pack (<see cref="RoomPack.ReadLibraryOptionsAsync"/>).</param>
    /// <param name="table">The class table the entities are sorted with.</param>
    /// <returns>The listing.</returns>
    /// <remarks>
    /// <para>
    /// The budget line reads <c>entity budget {b} (reserve {r}, cap {c})</c>;
    /// a room's entity line reads <c>entities: {n} ({e} edicts, {s} server-only)</c>,
    /// where <c>n</c> is what the room adds to a linked map's entity list
    /// per placement and <c>e</c> and <c>s</c> split it by the class table.
    /// Compile-only entities (which vbsp clears, so a compiled room has
    /// none) are named on the line only when there are some, since the
    /// link strips them.
    /// </para>
    /// <para>
    /// A room the pack has no counts for reads <c>entities: not in the room
    /// pack</c> when it failed to compile or the pack is another library's,
    /// and <c>entities: not counted; recompile with ssmap room</c> when the
    /// pack was written before the counts were.
    /// </para>
    /// </remarks>
    public static string DescribeLibrary(
        IReadOnlyList<LibraryRoom> rooms,
        IReadOnlyDictionary<string, RoomEntityCounts?> counts,
        RoomLibraryOptions options,
        EntityClassTable table)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table);
        return Describe(rooms, counts, options, table);
    }

    /// <summary>
    /// The listing <c>ssmap rooms</c> prints when the library's pack is
    /// there and holds the rooms' names: the listing with counts, and after
    /// each room's entity line what its names say
    /// (<see cref="RoomNameSummary.Describe"/>): its local names, the
    /// neighbours it reaches for by authored direction, which a level must
    /// place or the references are dropped with a warning, the flags and hub
    /// it uses, its <c>room_needs</c> conditions, and what the room compile
    /// warned of.
    /// </summary>
    /// <param name="rooms">The library's rooms.</param>
    /// <param name="counts">The rooms' entity counts from the pack.</param>
    /// <param name="options">The library's settings from the pack.</param>
    /// <param name="table">The class table.</param>
    /// <param name="names">The rooms' names from the pack (<see cref="RoomPack.ReadNameSummariesAsync"/>), by name.</param>
    /// <returns>The listing.</returns>
    public static string DescribeLibrary(
        IReadOnlyList<LibraryRoom> rooms,
        IReadOnlyDictionary<string, RoomEntityCounts?> counts,
        RoomLibraryOptions options,
        EntityClassTable table,
        IReadOnlyDictionary<string, RoomNameSummary> names)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(names);
        return Describe(rooms, counts, options, table, names);
    }

    /// <summary>
    /// The listing <c>ssmap rooms</c> prints when the library's pack is
    /// there: the listing with counts and names, and after the budget line
    /// the library's own entities (the pack's library section), which every
    /// level linked from it carries once.
    /// </summary>
    /// <param name="rooms">The library's rooms.</param>
    /// <param name="counts">The rooms' entity counts from the pack.</param>
    /// <param name="options">The library's settings from the pack.</param>
    /// <param name="table">The class table.</param>
    /// <param name="names">The rooms' names from the pack.</param>
    /// <param name="libraryEntities">The library-wide entities from the pack (<see cref="RoomPack.ReadLibraryEntitiesAsync"/>).</param>
    /// <returns>The listing.</returns>
    /// <remarks>
    /// The library line reads <c>library: {n} entities per level ({e} edicts,
    /// {s} server-only): {class}, {class}, ...</c>, the classes in library
    /// order, and is left out when the library has none, so a library
    /// without a sun lists as it did before.
    /// </remarks>
    public static string DescribeLibrary(
        IReadOnlyList<LibraryRoom> rooms,
        IReadOnlyDictionary<string, RoomEntityCounts?> counts,
        RoomLibraryOptions options,
        EntityClassTable table,
        IReadOnlyDictionary<string, RoomNameSummary> names,
        IReadOnlyList<VmfChunk> libraryEntities)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(libraryEntities);
        return Describe(rooms, counts, options, table, names, libraryEntities);
    }

    /// <summary>
    /// The listing <c>ssmap rooms</c> prints for a lit library: the listing
    /// with the library's entities, and after each lit room's entity line how
    /// many turns its base lighting is stored for (the rooms design, 1.1:
    /// <c>lighting: 1 turn, no sun or sky reaches it</c>, or <c>lighting: 4
    /// turns, sun or sky reaches it</c>). A room without lighting gets no
    /// line, so an unlit library lists as it did before the bake.
    /// </summary>
    /// <param name="rooms">The library's rooms.</param>
    /// <param name="counts">The rooms' entity counts from the pack.</param>
    /// <param name="options">The library's settings from the pack.</param>
    /// <param name="table">The class table.</param>
    /// <param name="names">The rooms' names from the pack.</param>
    /// <param name="libraryEntities">The library-wide entities from the pack.</param>
    /// <param name="lighting">Per lit room, its lighting's rotation count (<see cref="RoomPack.ReadLightingTurnsAsync"/>).</param>
    /// <returns>The listing.</returns>
    public static string DescribeLibrary(
        IReadOnlyList<LibraryRoom> rooms,
        IReadOnlyDictionary<string, RoomEntityCounts?> counts,
        RoomLibraryOptions options,
        EntityClassTable table,
        IReadOnlyDictionary<string, RoomNameSummary> names,
        IReadOnlyList<VmfChunk> libraryEntities,
        IReadOnlyDictionary<string, int> lighting)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(libraryEntities);
        ArgumentNullException.ThrowIfNull(lighting);
        return Describe(rooms, counts, options, table, names, libraryEntities, lighting);
    }

    private static string Describe(
        IReadOnlyList<LibraryRoom> rooms,
        IReadOnlyDictionary<string, RoomEntityCounts?>? counts,
        RoomLibraryOptions options,
        EntityClassTable? table,
        IReadOnlyDictionary<string, RoomNameSummary>? names = null,
        IReadOnlyList<VmfChunk>? libraryEntities = null,
        IReadOnlyDictionary<string, int>? lighting = null)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"{rooms.Count} room(s)\n");
        if (counts is not null)
        {
            int reserve = LevelEntityBudget.ReserveFor(LevelLinkOptions.Default, options);
            text.Append(CultureInfo.InvariantCulture,
                $"entity budget {EntityClassTable.EdictCap - reserve} (reserve {reserve}, cap {EntityClassTable.EdictCap})\n");
            if (libraryEntities is { Count: > 0 })
            {
                EntityTally tally = RoomLibraryEntities.Count(libraryEntities).Tally(table!);
                text.Append(CultureInfo.InvariantCulture,
                    $"library: {tally.Listed} entities per level ({tally.Edicts} edicts, {tally.ServerOnly} server-only): "
                    + $"{string.Join(", ", libraryEntities.Select(e => RoomLibraryEntities.ToLinked(e).ClassName))}\n");
            }
        }

        foreach (LibraryRoom room in rooms)
        {
            RoomDefinition definition = room.Definition;
            float cell = definition.CellSize;
            // A role room says so; an ordinary room's line is as it was.
            string role = room.Role switch
            {
                RoomRole.Up => ", role up",
                RoomRole.Down => ", role down",
                _ => string.Empty,
            };
            text.Append(CultureInfo.InvariantCulture,
                $"{definition.Name}: cell at ({Num(room.Corner)}), {Num(cell)} x {Num(cell)} x {Num(cell)}, "
                + $"{definition.Sockets.Count} door(s){role}\n");
            if (counts is not null)
            {
                text.Append(EntityLine(definition.Name, counts, table!));
            }

            if (names?.GetValueOrDefault(definition.Name) is { } summary)
            {
                text.Append(summary.Describe());
            }

            if (lighting?.GetValueOrDefault(definition.Name) is int turns and > 0)
            {
                text.Append(turns == 1
                    ? "  lighting: 1 turn, no sun or sky reaches it\n"
                    : string.Create(CultureInfo.InvariantCulture, $"  lighting: {turns} turns, sun or sky reaches it\n"));
            }

            foreach (RoomSocket socket in definition.Sockets)
            {
                Box plug = RoomLinter.SealBox(definition, socket, cell);
                Vec3 mins = room.Corner + plug.Mins;
                Vec3 maxs = room.Corner + plug.Maxs;
                string wall = RoomLibraryVmf.WallName(socket.Facing);
                string name = socket.Name == wall ? wall : $"{wall} \"{socket.Name}\"";
                text.Append(CultureInfo.InvariantCulture,
                    $"  {name}: ({Num(mins)}) to ({Num(maxs)}), "
                    + $"{Num(definition.Kit.Width)} wide x {Num(definition.Kit.Height)} high x {Num(definition.Kit.Depth)} deep\n");
            }
        }

        return text.ToString();
    }

    /// <summary>A room's entity line for <c>ssmap rooms</c>.</summary>
    private static string EntityLine(string room, IReadOnlyDictionary<string, RoomEntityCounts?> counts, EntityClassTable table)
    {
        if (!counts.TryGetValue(room, out RoomEntityCounts? found))
        {
            return "  entities: not in the room pack\n";
        }

        if (found is null)
        {
            return "  entities: not counted; recompile with ssmap room\n";
        }

        EntityTally tally = found.Tally(table);
        string stripped = tally.CompileOnly == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $", {tally.CompileOnly} compile-only stripped at link");
        return string.Create(CultureInfo.InvariantCulture,
            $"  entities: {tally.Listed} ({tally.Edicts} edicts, {tally.ServerOnly} server-only{stripped})\n");
    }

    /// <summary>What <c>ssmap rooms</c> and <c>ssmap layout</c> read from a pack: its settings and its rooms' counts.</summary>
    /// <param name="Options">The library's settings.</param>
    /// <param name="Counts">
    /// Per room of the pack, its entity counts, or null when the pack holds
    /// the room without them (a pack written before the counts were).
    /// </param>
    /// <param name="Names">Per room of the pack that has them, its names (<see cref="RoomNameSummary"/>).</param>
    /// <param name="LibraryEntities">The library-wide entities from the pack's library section, in library order.</param>
    /// <param name="Lighting">Per lit room, how many turns its base lighting is stored for.</param>
    private sealed record PackCounts(
        RoomLibraryOptions Options,
        IReadOnlyDictionary<string, RoomEntityCounts?> Counts,
        IReadOnlyDictionary<string, RoomNameSummary> Names,
        IReadOnlyList<VmfChunk> LibraryEntities,
        IReadOnlyDictionary<string, int> Lighting)
    {
        /// <summary>The library's skybox room, which every level carries once, or null.</summary>
        public string? Skybox { get; init; }
    }

    /// <summary>The pack's settings and counts, or null when there is no pack at <paramref name="packPath"/>.</summary>
    private static async Task<PackCounts?> ReadPackCountsAsync(IFileSystem disk, VPath packPath, CancellationToken cancellationToken)
    {
        if (!await disk.ExistsAsync(packPath, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using Stream stream = await disk.OpenReadAsync(packPath, cancellationToken).ConfigureAwait(false);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream, cancellationToken).ConfigureAwait(false);
        // In the order ssmap room writes the library sections: entities, then settings.
        IReadOnlyList<VmfChunk> libraryEntities = await RoomPack.ReadLibraryEntitiesAsync(stream, index, cancellationToken).ConfigureAwait(false);
        RoomLibraryOptions options = await RoomPack.ReadLibraryOptionsAsync(stream, index, cancellationToken).ConfigureAwait(false);
        string? skybox = await RoomPack.ReadLibrarySkyboxAsync(stream, index, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, RoomEntityCounts> read = await RoomPack.ReadEntityCountsAsync(stream, index, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, RoomEntityCounts?> counts = new(StringComparer.Ordinal);
        foreach (RoomPackEntry entry in index.Entries)
        {
            counts[entry.Name] = read.GetValueOrDefault(entry.Name);
        }

        IReadOnlyDictionary<string, RoomNameSummary> names = await RoomPack.ReadNameSummariesAsync(stream, index, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyDictionary<string, int> lighting = await RoomPack.ReadLightingTurnsAsync(stream, index, cancellationToken).ConfigureAwait(false);
        return new PackCounts(options, counts, names, libraryEntities, lighting) { Skybox = skybox };
    }

    private static string Num(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Num(Vec3 value) => $"{Num(value.X)}, {Num(value.Y)}, {Num(value.Z)}";

    private static async Task<int> LinkAsync(
        IFileSystem disk,
        LevelGrid level,
        byte[] levelBytes,
        string levelPath,
        string libraryPath,
        string? roomsPack,
        LevelLinkOptions linkOptions,
        VPath mapPath,
        LinkNavOptions nav,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!TryHostPath(roomsPack ?? DefaultPack(libraryPath), out VPath packPath))
        {
            await output.WriteLineAsync($"ssmap link: -rooms \"{roomsPack}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        if (!level.Placed.Any())
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: the level places no room").ConfigureAwait(false);
            return ExitFailed;
        }

        string pack = HostPaths.Display(packPath);
        RoomLibrary library;
        Guid? packId;
        try
        {
            if (!await disk.ExistsAsync(packPath, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync(
                    $"ssmap link: {levelPath}: there is no room pack {pack};"
                    + " compile the library with ssmap room, or point -rooms at its pack")
                    .ConfigureAwait(false);
                return ExitFailed;
            }

            await using Stream stream = await disk.OpenReadAsync(packPath, cancellationToken).ConfigureAwait(false);
            RoomPackIndex index = await RoomPack.ReadIndexAsync(stream, cancellationToken).ConfigureAwait(false);

            // A level of one library with aliases: each replaced by the room
            // it names, which the pack's index lists (the rooms design, 17.2).
            if (level.Aliases.Count > 0)
            {
                level = LevelLibraries.Resolve(level, [[.. index.Entries.Select(e => e.Name)]]);
            }

            (List<LevelCell> first, Dictionary<string, HashSet<int>> turns) = PlacedRooms(level);

            // The sun, fog and the other library-wide entities: written once
            // into the level, and counted in its budget. Read before the
            // settings, in the order ssmap room writes the library sections,
            // so a pack on a stream that cannot seek reads too.
            IReadOnlyList<VmfChunk> libraryEntities = await RoomPack.ReadLibraryEntitiesAsync(stream, index, cancellationToken)
                .ConfigureAwait(false);
            RoomLibraryOptions libraryOptions = await RoomPack.ReadLibraryOptionsAsync(stream, index, cancellationToken)
                .ConfigureAwait(false);

            // The skybox room, which the link places below every level's
            // grid: read with the rooms, at its one turn, without navigation.
            string? skybox = await RoomPack.ReadLibrarySkyboxAsync(stream, index, cancellationToken).ConfigureAwait(false);
            if (skybox is not null && index.Find(skybox) is null)
            {
                await output.WriteLineAsync(
                    $"ssmap link: the room pack {pack} names skybox room \"{skybox}\" but does not hold it; recompile the library with ssmap room")
                    .ConfigureAwait(false);
                return ExitFailed;
            }

            foreach (LevelCell cell in first)
            {
                if (index.Find(cell.Room) is null)
                {
                    await output.WriteLineAsync(
                        $"ssmap link: {levelPath}: {cell.Where}room \"{cell.Room}\" is not in the room pack {pack};"
                        + " compile the library with ssmap room, or point -rooms at its pack")
                        .ConfigureAwait(false);
                    return ExitFailed;
                }
            }

            IReadOnlyList<RoomObject> rooms = await RoomPack
                .LoadRoomsAsync(
                    stream,
                    index,
                    [
                        .. first.Select(c => new RoomPackRequest(c.Room, turns[c.Room]) { Navigation = !nav.Skip }),
                        .. skybox is null || turns.ContainsKey(skybox) ? [] : new[] { new RoomPackRequest(skybox, [0]) },
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            // The first room sets the grid; RoomLibrary.Add refuses any other.
            library = new RoomLibrary(rooms[0].Definition.Kit, rooms[0].Definition.CellSize)
            {
                Options = libraryOptions,
                LibraryEntities = libraryEntities,
                SkyboxRoom = skybox,
            };
            foreach (RoomObject room in rooms)
            {
                library.Add(room);
            }

            // The navigation came with the rooms, at the turns they are placed
            // (RoomPackRequest.Navigation); the pack's id is its one library
            // section more, and only read when the link writes navigation.
            packId = nav.Skip ? null : await RoomNavPack.ReadPackIdAsync(stream, index, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap link: cannot read the room pack {pack}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }
        catch (LevelFileException exception)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (Exception exception) when (exception is LinkException or ArgumentException)
        {
            // A file that is not a room pack, a room in it that is not a room
            // container, or a room built for another grid than the rest
            // (RoomLibrary.Add).
            await output.WriteLineAsync($"ssmap link: {pack}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        return await LinkLoadedAsync(disk, level, levelBytes, levelPath, library, packId, [], linkOptions, mapPath, nav, output, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Exactly the rooms a level places, in the order it first places them,
    /// and the turns it places each at: the pack's index is read, then those
    /// rooms and those turns' link sections and nothing else, so a stale or
    /// broken room the level does not name is never read.
    /// </summary>
    private static (List<LevelCell> First, Dictionary<string, HashSet<int>> Turns) PlacedRooms(LevelGrid level)
    {
        List<LevelCell> first = [];
        Dictionary<string, HashSet<int>> turns = new(StringComparer.Ordinal);
        foreach ((_, _, LevelCell cell) in level.Placed)
        {
            if (!turns.TryGetValue(cell.Room, out HashSet<int>? placed))
            {
                turns[cell.Room] = placed = [];
                first.Add(cell);
            }

            placed.Add(cell.Rotation);
        }

        return (first, turns);
    }

    /// <summary>
    /// The link once its rooms are loaded: the navigation planned, the map
    /// linked and written, the warnings and the report printed, and the
    /// navigation built after the map.
    /// </summary>
    /// <param name="disk">Where the map and its navigation are written.</param>
    /// <param name="level">The level, its cells as the library names its rooms.</param>
    /// <param name="levelBytes">The level file's bytes: an input of the level id.</param>
    /// <param name="levelPath">The level file, for messages.</param>
    /// <param name="library">The loaded rooms: the pack's, or several libraries' combined.</param>
    /// <param name="packId">The pack id the map and its navigation record, or null.</param>
    /// <param name="libraryWarnings">
    /// What combining several libraries warned of (<see cref="LevelLibraries.Combine"/>),
    /// printed first; empty for a level of one.
    /// </param>
    /// <param name="linkOptions">The link's settings.</param>
    /// <param name="mapPath">Where the map goes.</param>
    /// <param name="nav">The navigation switches.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the link.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> LinkLoadedAsync(
        IFileSystem disk,
        LevelGrid level,
        byte[] levelBytes,
        string levelPath,
        RoomLibrary library,
        Guid? packId,
        IReadOnlyList<string> libraryWarnings,
        LevelLinkOptions linkOptions,
        VPath mapPath,
        LinkNavOptions nav,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        LevelNavPlan navPlan;

        // The navigation is planned before the map is linked (ids, and a point
        // of interest in a capped doorway refused), and built after the map
        // is written: the map never waits for navigation work.
        try
        {
            LevelLayout navLayout = level.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
            navPlan = LevelNavFromPack.Plan(
                navLayout, level.Columns, level.Rows, library.Get, packId, levelBytes, nav.IdOptions, !nav.Skip,
                level.Libraries is null ? null : library.SourceOf);
        }
        catch (Exception exception) when (exception is LinkException or ArgumentException)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        // The link reads no content — only the context's parallelism and map
        // name — so the context needs mounts for none (decision D1: the pack
        // is all a link reads). The map name is the output file's, as vbsp
        // takes it from the source file's: the rooms' default cubemaps are
        // renamed to it, which is where the engine looks for them.
        await using ContentFileSystem content = new([]);
        VbspContext context = new(VbspOptions.Default, content) { MapBase = MapBaseOf(mapPath) };

        try
        {
            LevelLayout layout = level.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
            LinkedLevel link = await LevelLinker
                .LinkAsync(layout, library, context, linkOptions, cancellationToken)
                .ConfigureAwait(false);

            if (navPlan.Warning is { } warning)
            {
                await output.WriteLineAsync($"ssmap link: {(nav.Require ? "error" : "warning")}: {warning}").ConfigureAwait(false);
                if (nav.Require)
                {
                    return ExitFailed;
                }
            }

            // The ids tie the map to its .nav3d, so they are written only
            // with one: a link without navigation writes the map it always did.
            if (navPlan.WritesNavigation)
            {
                RoomCompileIds.Stamp(link.Bsp, navPlan.PackId, navPlan.LevelId);
            }

            using MemoryStream buffer = new();
            await BspFile
                .SaveAsync(link.Bsp, buffer, BspWriteMode.Canonical, cancellationToken).ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();
            await disk.ReplaceAsync(
                mapPath,
                async (stream, token) => await stream
                    .WriteAsync(bytes, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            // What combining several libraries warned of (the singleton rule,
            // the worldspawn, the sun), what resolving the rooms' names
            // warned of (references to empty cells dropped, global names
            // repeated), what linking the areas warned of, then the budget's
            // warnings, then the headroom it always reports.
            foreach (string libraryWarning in libraryWarnings)
            {
                await output.WriteLineAsync($"ssmap link: warning: {libraryWarning}").ConfigureAwait(false);
            }

            foreach (string nameWarning in link.NameWarnings)
            {
                await output.WriteLineAsync($"ssmap link: warning: {nameWarning}").ConfigureAwait(false);
            }

            foreach (string lightingWarning in link.LightingWarnings)
            {
                await output.WriteLineAsync($"ssmap link: warning: {lightingWarning}").ConfigureAwait(false);
            }

            // An area portal the level joins around (its two sides one area
            // once linked), which the level lists no portal for.
            foreach (string areaWarning in link.AreaWarnings)
            {
                await output.WriteLineAsync($"ssmap link: warning: {areaWarning}").ConfigureAwait(false);
            }

            // With the mod's classes a level's arrival and spawn points are
            // read from its navigation sidecar (the rooms design, 11.5): a
            // level with transitions linked without one has none for the mod.
            if (linkOptions.ModEntities && link.HasTransitions && !navPlan.WritesNavigation)
            {
                await output.WriteLineAsync(
                    $"ssmap link: warning: level {level.Name}: -mod-entities places players at the arrival and spawn points of the"
                    + " navigation sidecar, and the level links without navigation; build the library's navigation, or link without -mod-entities.")
                    .ConfigureAwait(false);
            }

            LevelEntityReport budget = link.EntityBudget!;
            foreach (string budgetWarning in budget.Warnings)
            {
                await output.WriteLineAsync($"ssmap link: warning: {budgetWarning}").ConfigureAwait(false);
            }

            await output.WriteLineAsync($"ssmap link: {budget.Headroom}").ConfigureAwait(false);

            // The brush count, and how many the fold merged away: the brush
            // cap is what a large level meets first, so it is worth seeing.
            int brushes = BspStructView.Count<DBrush>(link.Bsp[BspLump.Brushes]);
            await output.WriteLineAsync(
                $"ssmap link: wrote {HostPaths.Display(mapPath)}"
                + $" ({link.Plan.Layout.Rooms.Count} rooms, {link.Vis.ClusterCount} clusters, {brushes} brushes"
                + (link.FoldedBrushes > 0 ? $" ({link.FoldedBrushes} folded away)" : string.Empty)
                + (link.PackedFiles > 0 ? $", {link.PackedFiles} packed files" : string.Empty)
                + (navPlan.WritesNavigation ? $", level id {navPlan.LevelId:D})" : ")"))
                .ConfigureAwait(false);

            // How much the level's visibility lets through: the pairs of
            // clusters it marks visible, of all there are, and the lump.
            long pairs = (long)link.Vis.ClusterCount * link.Vis.ClusterCount;
            await output.WriteLineAsync(
                $"ssmap link: visibility {link.Vis.TotalVisibleClusters} of {pairs} cluster pairs,"
                + $" {link.Vis.VisDataSize} bytes")
                .ConfigureAwait(false);
            if (navPlan.WritesNavigation)
            {
                // The map is on disk; now the navigation. A failure here
                // leaves the map as written and no .nav3d (the file is
                // replaced whole or not at all).
                try
                {
                    Nav3dLevel levelNav = await navPlan.BuildAsync(cancellationToken).ConfigureAwait(false);
                    VPath navPath = NavPathOf(mapPath);
                    long written = await LevelNavPlan.WriteAsync(disk, navPath, levelNav, nav.Compression, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(
                        $"ssmap link: wrote {HostPaths.Display(navPath)} ({levelNav.Leaves.Length} leaves, {levelNav.Presets.Count} presets,"
                        + $" {levelNav.Pois.Count} points of interest, {levelNav.Jumps.Count} jump links, {written} bytes)")
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is LinkException or ArgumentException or IOException
                    or UnauthorizedAccessException)
                {
                    await output.WriteLineAsync(
                        $"ssmap link: {levelPath}: the map was written, but its navigation failed: {exception.Message}")
                        .ConfigureAwait(false);
                    return ExitFailed;
                }
            }

            return Program.ExitSuccess;
        }
        catch (MapCompileException exception)
        {
            await output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (RoomLintException exception)
        {
            await output.WriteLineAsync($"ssmap link: the level is not linkable: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }
        catch (Exception exception) when (exception is LinkException or ArgumentException or IOException
            or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
    }

    /// <summary>
    /// A linked map's name, as the link's compile context takes it
    /// (<see cref="VbspContext.MapBase"/>): the output file's name without
    /// its extension, lower-cased, as vbsp takes a map's from its source file.
    /// </summary>
    /// <param name="mapPath">The map.</param>
    /// <returns>The map name the rooms' default cubemaps are renamed to.</returns>
    /// <remarks>
    /// Public because the CLI gets no <c>InternalsVisibleTo</c>: a host that
    /// links a level itself names its map the same way, and the facts pin it.
    /// </remarks>
    public static string MapBaseOf(VPath mapPath)
    {
#pragma warning disable CA1308 // mapbase is lower case, as vbsp's strlwr makes it
        return Path.GetFileNameWithoutExtension(mapPath.FileName).ToLowerInvariant();
#pragma warning restore CA1308
    }

    /// <summary>Where a linked map's navigation goes: beside it, <c>&lt;map&gt;.nav3d</c>.</summary>
    /// <param name="mapPath">The map.</param>
    /// <returns>The navigation file's path.</returns>
    public static VPath NavPathOf(VPath mapPath) =>
        VPath.Create(Path.ChangeExtension(mapPath.ToString(), Nav3dFormat.Extension));

    /// <summary>
    /// The default store opener: the SQLite store at the path, or null when
    /// it cannot open (<see cref="HostBackends.MissingReason"/> says why).
    /// </summary>
    /// <remarks>
    /// The path is always <see cref="HostBackends.CachePathFor"/>'s, so it is
    /// split back into the folder and the name that function joins, and the
    /// store opens exactly where <c>ssmap all -incremental</c> would open one
    /// for a map of the library's name.
    /// </remarks>
    private static Task<ICacheStore?> OpenCacheStoreAsync(string path, CancellationToken cancellationToken)
    {
        string folder = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(path)[..^".sscache.db".Length];
        return HostBackends.OpenCacheAsync(folder, folder, name, cancellationToken);
    }

    /// <summary>The <c>ssmap room</c> options that shape a pack, for its id: the stock line without the library path and <c>-threads</c>.</summary>
    private static List<string> PackIdOptions(List<string> stock, string? mapPath)
    {
        List<string> options = [];
        for (int i = 0; i < stock.Count; i++)
        {
            if (IsFlag(stock[i], "threads"))
            {
                i++;
                continue;
            }

            if (stock[i] != mapPath)
            {
                options.Add(stock[i]);
            }
        }

        return options;
    }

    /// <summary>The navigation settings and storage, as a pack id input.</summary>
    private static string Describe(NavSettings? settings, RoomNavPackOptions options) =>
        settings is null
            ? "none"
            : string.Create(CultureInfo.InvariantCulture,
                $"voxel {settings.VoxelSize:R} floor {settings.FloorNormalZ:R} agents {string.Join(";", settings.Agents)} turns {options.StoreAllTurns} codec {options.Compression}");

    private static async Task<byte[]> ReadBytesAsync(IFileSystem disk, VPath path, CancellationToken cancellationToken)
    {
        await using Stream stream = await disk.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes.ToArray();
    }

    /// <summary>The link's navigation switches.</summary>
    private sealed record LinkNavOptions
    {
        /// <summary><c>-no-nav</c>: write no <c>.nav3d</c>.</summary>
        public bool Skip { get; init; }

        /// <summary><c>-require-nav</c>: a pack without navigation fails the link instead of warning.</summary>
        public bool Require { get; init; }

        /// <summary><c>-nav-codec</c>: how the <c>.nav3d</c> image is stored.</summary>
        public NavCompression Compression { get; init; } = LevelNavFromPack.DefaultCompression;

        /// <summary>The switches that shape the outputs, as level id inputs.</summary>
        public IReadOnlyList<string> IdOptions => LevelNavFromPack.IdOptions(!Skip, Compression);
    }

    private static async Task<int> FlattenAsync(
        IFileSystem disk,
        LevelGrid level,
        string levelPath,
        string libraryPath,
        VPath vmfPath,
        bool modEntities,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!VPath.TryCreate(libraryPath, out VPath libraryVPath))
        {
            await output.WriteLineAsync($"ssmap link: the library \"{libraryPath}\" is not a usable path")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        try
        {
            VmfDocument library = await ReadVmfAsync(disk, libraryVPath, cancellationToken).ConfigureAwait(false);
            FlattenedLevel flat = LevelFlattener.FlattenLevel(level, library, new LevelFlattenOptions { ModEntities = modEntities });
            byte[] bytes = flat.Vmf.ToBytes();
            await disk.ReplaceAsync(
                vmfPath,
                async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            // The same warnings the link gives for the same level and mode.
            foreach (string warning in flat.Warnings)
            {
                await output.WriteLineAsync($"ssmap link: warning: {warning}").ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ChunkFileException or RoomLibraryException)
        {
            await output.WriteLineAsync($"ssmap link: {libraryPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (RoomLintException exception)
        {
            await output.WriteLineAsync($"ssmap link: the level is not linkable: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }
        catch (Exception exception) when (exception is LinkException or ArgumentException)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        await output.WriteLineAsync($"ssmap link: wrote {HostPaths.Display(vmfPath)} ({level.Placed.Count()} rooms, flattened)")
            .ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// A path from the command line, resolved against the current directory
    /// as the host resolves it, or false when the host cannot hold it at all
    /// (a NUL, say): the caller reports that as a usage error rather than let
    /// the host's refusal escape the command.
    /// </summary>
    private static bool TryHostPath(string path, out VPath result)
    {
        try
        {
            return VPath.TryCreate(Path.GetFullPath(path), out result);
        }
        catch (ArgumentException)
        {
            result = VPath.Empty;
            return false;
        }
    }

    /// <summary>Where a library's rooms are packed by default: <c>&lt;library&gt;.roompack</c> beside it.</summary>
    private static string DefaultPack(string libraryPath) => Path.ChangeExtension(libraryPath, RoomPack.Extension);

    private static async Task<VmfDocument> ReadVmfAsync(IFileSystem disk, VPath path, CancellationToken cancellationToken)
    {
        await using Stream stream = await disk.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        return await VmfDocument.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether an argument is the named flag, spelt with one dash or two.</summary>
    private static bool IsFlag(string arg, string name) =>
        string.Equals(arg, "-" + name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--" + name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="args"/> at <paramref name="index"/> is the
    /// named option with a value after it; the caller skips the value.
    /// </summary>
    private static bool Take(IReadOnlyList<string> args, int index, string name, out string value)
    {
        value = string.Empty;
        if (index >= args.Count || !IsFlag(args[index], name))
        {
            return false;
        }

        if (index + 1 >= args.Count)
        {
            // Option at the end of the line: leave it for the usage check to
            // report (a dangling -out is a usage problem, not a crash).
            return false;
        }

        value = args[index + 1];
        return true;
    }
}
