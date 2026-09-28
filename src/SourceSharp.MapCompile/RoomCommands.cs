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
/// the stock line, compile — run once per room of a library VMF, each
/// written as a <see cref="RoomObjectStore"/> container instead of a .bsp.
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
/// <b>One <c>.room</c> file per room</b>, not one library container: a room
/// object is the unit the linker loads, the unit the cache keys, and the
/// unit that changes when one room of the library is edited, so writing
/// each room as its own file keeps the <see cref="RoomObjectStore"/> format
/// as it is, lets a link load exactly the rooms its level places, and lets a
/// host skip rooms whose inputs did not change.
/// </para>
/// </remarks>
public static class RoomCommands
{
    /// <summary>The exit code for a compile, link, or file the run could not deliver.</summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// <c>ssmap room &lt;library.vmf&gt; [-out &lt;roomdir&gt;] [stock vbsp options]</c>:
    /// compile every room of a library VMF into <c>&lt;roomdir&gt;/&lt;roomname&gt;.room</c>.
    /// </summary>
    /// <param name="disk">Where the library, the game content and the output live.</param>
    /// <param name="searchRoots">Where game installs are, for the cooker's library discovery.</param>
    /// <param name="args">The arguments after <c>room</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the compiles.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// Every room is compiled even when one fails, so one run reports every
    /// room that needs fixing; the exit code is failed if any did. The game
    /// is found by vbsp's rule, <c>-game</c> or else the library's folder's
    /// parent, and <c>-out</c> defaults to the library's own folder, which is
    /// also where <c>ssmap link</c> looks for rooms by default.
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
            await output.WriteLineAsync("usage: ssmap room <library.vmf> [-out <roomdir>] [stock vbsp options]")
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
        if (!VPath.TryCreate(
            outDirectory is null ? Path.GetDirectoryName(source)! : Path.GetFullPath(outDirectory),
            out VPath outDir))
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

        int failed = 0;
        foreach (LibraryRoom room in rooms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoomDefinition definition = room.Definition;

            // A context per room: a context carries the tables one compile
            // fills (texinfos, planes, the loading map), which two rooms must
            // not share. The content mount and the cooker are read-only and are.
            VbspContext context = new(options, mounted.Content)
            {
                // mapbase: the room's name, lowercased
#pragma warning disable CA1308 // strlwr
                MapBase = definition.Name.ToLowerInvariant(),
#pragma warning restore CA1308
                CollisionCooker = cooker,
                Parallelism = parsed.Threads is int degree && degree > 0
                    ? new CompileParallelism { MaxDegree = degree }
                    : CompileParallelism.Default,
            };

            try
            {
                RoomObject compiled = await RoomCompiler
                    .CompileAsync(room.Document, definition, context, cancellationToken).ConfigureAwait(false);

                VPath roomFile = outDir.Combine(definition.Name + ".room");
                await disk.ReplaceAsync(
                    roomFile,
                    async (stream, token) => await RoomObjectStore
                        .SaveAsync(compiled, stream, token).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);

                await output.WriteLineAsync(
                    $"ssmap room: wrote {HostPaths.Display(roomFile)}"
                    + $" ({compiled.ClusterCount} clusters, {definition.Sockets.Count} sockets)")
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is MapCompileException or IOException
                or UnauthorizedAccessException or LinkException)
            {
                await output.WriteLineAsync($"ssmap room: room \"{definition.Name}\": {exception.Message}")
                    .ConfigureAwait(false);
                failed++;
            }
            catch (RoomLintException exception)
            {
                await output.WriteLineAsync(
                    $"ssmap room: room \"{definition.Name}\" is not linkable: {exception.Message}")
                    .ConfigureAwait(false);
                failed++;
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
    /// <c>ssmap link &lt;level.yaml&gt; [-rooms &lt;dir&gt;] [-out &lt;map.bsp&gt;]</c>:
    /// link the level's rooms into one map; or, with <c>--flatten</c>,
    /// write the same level as one VMF (<c>-out</c> then names the VMF) for
    /// vbsp to compile as the reference.
    /// </summary>
    /// <param name="disk">Where the level, the room objects, the library and the output live.</param>
    /// <param name="args">The arguments after <c>link</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the link.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// The level's <c>library</c> is resolved against the level file's
    /// folder. The link loads <c>&lt;dir&gt;/&lt;room&gt;.room</c> for each
    /// room the level places, and nothing else, with <c>-rooms</c> defaulting
    /// to the library's folder (where <c>ssmap room</c> writes by default).
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
        string? roomsDirectory = null;
        string? outPath = null;
        bool flatten = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (Take(args, i, "rooms", out string r))
            {
                roomsDirectory = r;
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

        if (rest.Count != 1 || (flatten && roomsDirectory is not null))
        {
            await output.WriteLineAsync(
                "usage: ssmap link <level.yaml> [-rooms <dir>] [-out <map.bsp>]\n"
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
            : await LinkAsync(disk, level, levelPath, libraryPath, roomsDirectory, targetPath, output, cancellationToken).ConfigureAwait(false);
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

        string text;
        try
        {
            IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(
                await ReadVmfAsync(disk, libraryVPath, cancellationToken).ConfigureAwait(false));
            string from = target is null ? Path.GetFullPath(".") : Path.GetDirectoryName(target)!;
            string library = Path.GetRelativePath(from, libraryPath).Replace('\\', '/');
            string name = target is null ? "level" : Path.GetFileNameWithoutExtension(target);
            LevelGeneratorOptions options = new(rowCount, columnCount, seedValue, ratio);
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
        string? roomsDirectory,
        VPath mapPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string roomsDir = Path.GetFullPath(roomsDirectory ?? Path.GetDirectoryName(libraryPath)!);
        if (!VPath.TryCreate(roomsDir, out VPath roomsDirPath))
        {
            await output.WriteLineAsync($"ssmap link: -rooms \"{roomsDirectory}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // Exactly the rooms the level places, in the order it first places
        // them: a stale .room file of a room no level names is never read.
        RoomLibrary? library = null;
        HashSet<string> loaded = new(StringComparer.Ordinal);
        try
        {
            foreach ((_, _, LevelCell cell) in level.Placed)
            {
                if (!loaded.Add(cell.Room))
                {
                    continue;
                }

                VPath file = roomsDirPath.Combine(cell.Room + ".room");
                if (!await disk.ExistsAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    await output.WriteLineAsync(
                        $"ssmap link: {levelPath}: {cell.Where}room \"{cell.Room}\" has no compiled room {file.Value};"
                        + " compile the library with ssmap room, or point -rooms at its rooms")
                        .ConfigureAwait(false);
                    return ExitFailed;
                }

                RoomObject room;
                await using (Stream stream = await disk.OpenReadAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    room = await RoomObjectStore.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                if (room.Definition.Name != cell.Room)
                {
                    throw new LinkException($"{file.Value} holds room \"{room.Definition.Name}\", not \"{cell.Room}\"");
                }

                library ??= new RoomLibrary(room.Definition.Kit, room.Definition.CellSize);
                library.Add(room);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap link: cannot read the rooms in {roomsDir}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }
        catch (Exception exception) when (exception is LinkException or ArgumentException)
        {
            // A file that is not a room container, or a room built for another
            // grid than the rest (RoomLibrary.Add).
            await output.WriteLineAsync($"ssmap link: {roomsDir}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        if (library is null)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: the level places no room").ConfigureAwait(false);
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
