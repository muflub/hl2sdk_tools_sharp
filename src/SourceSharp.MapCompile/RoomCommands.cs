//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Rooms;

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
public static class RoomCommands
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
    public static async Task<int> RunRoomAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(searchRoots);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        // -out is this verb's, not stock vbsp's: take it out of the line first
        // so the stock parser never sees an option it would (correctly) refuse.
        List<string> stock = [];
        string? outDirectory = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (Take(args, i, "out", out string o))
            {
                outDirectory = o;
                i++;
            }
            else
            {
                stock.Add(args[i]);
            }
        }

        StockArgsResult<VbspOptions> parsed = StockArgs.ParseVbsp(stock);
        foreach (CompileDiagnostic diagnostic in parsed.Diagnostics)
        {
            await output.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}").ConfigureAwait(false);
        }

        if (parsed.HasErrors || parsed.MapPath is null)
        {
            await output.WriteLineAsync("usage: ssmap room <library.vmf> [-out <pack.roompack>] [stock vbsp options]")
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
        try
        {
            rooms = RoomLibraryVmf.Split(await ReadVmfAsync(disk, libraryPath, cancellationToken).ConfigureAwait(false));
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

        RoomLibraryCompileSettings settings = new(options, mounted.Content)
        {
            CollisionCooker = cooker,
            Parallelism = parsed.Threads is int degree && degree > 0
                ? new CompileParallelism { MaxDegree = degree }
                : CompileParallelism.Default,
        };

        // Called in library order, one room at a time: the lines, the
        // failure count and the pack's room list come out the same whatever
        // order the rooms finished in.
        int failed = 0;
        List<RoomPackItem> packed = [];
        async ValueTask ReportAsync(RoomCompileOutcome outcome, CancellationToken token)
        {
            RoomDefinition definition = outcome.Room.Definition;
            if (outcome.Compiled is { } compiled)
            {
                using MemoryStream container = new();
                await RoomObjectStore.SaveAsync(compiled, container, token).ConfigureAwait(false);
                packed.Add(new RoomPackItem(definition.Name, container.ToArray()));
                await output.WriteLineAsync(
                    $"ssmap room: compiled {definition.Name}"
                    + $" ({compiled.ClusterCount} clusters, {definition.Sockets.Count} sockets)")
                    .ConfigureAwait(false);
                return;
            }

            failed++;
            await output.WriteLineAsync(outcome.Error is RoomLintException
                ? $"ssmap room: room \"{definition.Name}\" is not linkable: {outcome.Error.Message}"
                : $"ssmap room: room \"{definition.Name}\": {outcome.Error!.Message}")
                .ConfigureAwait(false);
        }

        await RoomLibraryCompiler.CompileAsync(rooms, settings, ReportAsync, cancellationToken).ConfigureAwait(false);

        try
        {
            await disk.ReplaceAsync(
                packPath,
                async (stream, token) => await RoomPack.SaveAsync(packed, stream, token).ConfigureAwait(false),
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
        string? roomsPack = null;
        string? outPath = null;
        bool flatten = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (Take(args, i, "rooms", out string r))
            {
                roomsPack = r;
                i++;
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
            else
            {
                rest.Add(args[i]);
            }
        }

        if (rest.Count != 1 || (flatten && roomsPack is not null))
        {
            await output.WriteLineAsync(
                "usage: ssmap link <level.yaml> [-rooms <pack.roompack>] [-out <map.bsp>]\n"
                + "       ssmap link <level.yaml> --flatten [-out <map.vmf>]")
                .ConfigureAwait(false);
            return Program.ExitUsage;
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
        try
        {
            await using Stream stream = await disk.OpenReadAsync(levelVPath, cancellationToken).ConfigureAwait(false);
            string text = await new StreamReader(stream, Encoding.UTF8)
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
            ? await FlattenAsync(disk, level, levelPath, libraryPath, targetPath, output, cancellationToken).ConfigureAwait(false)
            : await LinkAsync(disk, level, levelPath, libraryPath, roomsPack, targetPath, output, cancellationToken).ConfigureAwait(false);
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
            "usage: ssmap layout <library.vmf> -rows <n> -columns <n> -seed <n> [-empty <ratio>] [-out <level.yaml>]";
        List<string> rest = [];
        string? rows = null, columns = null, seed = null, empty = null, outPath = null;
        for (int i = 0; i < args.Count; i++)
        {
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
            else
            {
                rest.Add(args[i]);
                continue;
            }

            i++;
        }

        if (rest.Count != 1 || rows is null || columns is null || seed is null)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
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

        string libraryPath = Path.GetFullPath(rest[0]);
        string? target = outPath is null ? null : Path.GetFullPath(outPath);
        if (!VPath.TryCreate(libraryPath, out VPath libraryVPath))
        {
            await output.WriteLineAsync($"ssmap layout: \"{libraryPath}\" is not a usable path").ConfigureAwait(false);
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

        string text;
        try
        {
            IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(
                await ReadVmfAsync(disk, libraryVPath, cancellationToken).ConfigureAwait(false));
            string from = target is null ? Path.GetFullPath(".") : Path.GetDirectoryName(target)!;
            string library = Path.GetRelativePath(from, libraryPath).Replace('\\', '/');
            string name = target is null ? "level" : Path.GetFileNameWithoutExtension(target);
            LevelGrid level = LevelGenerator.Generate([.. rooms.Select(r => r.Definition)], options, name, library);
            text = LevelYaml.Write(level, LevelGenerator.Header(options, level));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ChunkFileException or RoomLibraryException or LinkException or ArgumentException)
        {
            await output.WriteLineAsync($"ssmap layout: {libraryPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        if (target is null)
        {
            await output.WriteAsync(text).ConfigureAwait(false);
            return Program.ExitSuccess;
        }

        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        try
        {
            await disk.ReplaceAsync(
                targetPath,
                async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap layout: cannot write {target}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        await output.WriteLineAsync($"ssmap layout: wrote {HostPaths.Display(targetPath)}").ConfigureAwait(false);
        return Program.ExitSuccess;
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
    /// The library is read and checked exactly as <c>ssmap room</c> reads it
    /// (<see cref="RoomLibraryVmf.Split"/>), so a library this lists is one
    /// the room compile accepts, and one it refuses is refused with the same
    /// message. Nothing is compiled and no game is mounted.
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

        if (args.Count != 1 || args[0].StartsWith('-'))
        {
            await output.WriteLineAsync("usage: ssmap rooms <library.vmf>").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string libraryPath = Path.GetFullPath(args[0]);
        if (!VPath.TryCreate(libraryPath, out VPath libraryVPath))
        {
            await output.WriteLineAsync($"ssmap rooms: \"{libraryPath}\" is not a usable path").ConfigureAwait(false);
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

        await output.WriteAsync(DescribeLibrary(rooms)).ConfigureAwait(false);
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
    public static string DescribeLibrary(IReadOnlyList<LibraryRoom> rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"{rooms.Count} room(s)\n");
        foreach (LibraryRoom room in rooms)
        {
            RoomDefinition definition = room.Definition;
            float cell = definition.CellSize;
            text.Append(CultureInfo.InvariantCulture,
                $"{definition.Name}: cell at ({Num(room.Corner)}), {Num(cell)} x {Num(cell)} x {Num(cell)}, "
                + $"{definition.Sockets.Count} door(s)\n");
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

    private static string Num(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Num(Vec3 value) => $"{Num(value.X)}, {Num(value.Y)}, {Num(value.Z)}";

    private static async Task<int> LinkAsync(
        IFileSystem disk,
        LevelGrid level,
        string levelPath,
        string libraryPath,
        string? roomsPack,
        VPath mapPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!TryHostPath(roomsPack ?? DefaultPack(libraryPath), out VPath packPath))
        {
            await output.WriteLineAsync($"ssmap link: -rooms \"{roomsPack}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // Exactly the rooms the level places, in the order it first places
        // them: the pack's index is read, then those rooms and nothing else,
        // so a stale or broken room the level does not name is never read.
        List<LevelCell> first = [];
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach ((_, _, LevelCell cell) in level.Placed)
        {
            if (named.Add(cell.Room))
            {
                first.Add(cell);
            }
        }

        if (first.Count == 0)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: the level places no room").ConfigureAwait(false);
            return ExitFailed;
        }

        string pack = HostPaths.Display(packPath);
        RoomLibrary library;
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
                .LoadRoomsAsync(stream, index, [.. first.Select(c => c.Room)], cancellationToken).ConfigureAwait(false);
            // The first room sets the grid; RoomLibrary.Add refuses any other.
            library = new RoomLibrary(rooms[0].Definition.Kit, rooms[0].Definition.CellSize);
            foreach (RoomObject room in rooms)
            {
                library.Add(room);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap link: cannot read the room pack {pack}: {exception.Message}")
                .ConfigureAwait(false);
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

        // The link reads no content — only the context's parallelism — so the
        // context needs mounts for none. A linked map carries no content lump
        // for the link to want.
        await using ContentFileSystem content = new([]);
        VbspContext context = new(VbspOptions.Default, content);

        try
        {
            LevelLayout layout = level.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
            LinkedLevel link = await LevelLinker
                .LinkAsync(layout, library, context, cancellationToken).ConfigureAwait(false);

            using MemoryStream buffer = new();
            await BspFile
                .SaveAsync(link.Bsp, buffer, BspWriteMode.Canonical, cancellationToken).ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();
            await disk.ReplaceAsync(
                mapPath,
                async (stream, token) => await stream
                    .WriteAsync(bytes, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync(
                $"ssmap link: wrote {HostPaths.Display(mapPath)}"
                + $" ({link.Plan.Layout.Rooms.Count} rooms, {link.Vis.ClusterCount} clusters)")
                .ConfigureAwait(false);
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

    private static async Task<int> FlattenAsync(
        IFileSystem disk,
        LevelGrid level,
        string levelPath,
        string libraryPath,
        VPath vmfPath,
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
            VmfDocument flat = LevelFlattener.Flatten(level, library);
            byte[] bytes = flat.ToBytes();
            await disk.ReplaceAsync(
                vmfPath,
                async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
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
