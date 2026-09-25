using System.Text;

using SourceSharp.MapFormats.Bsp;
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
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap room</c> and <c>ssmap link</c>: the two
/// verbs of the room pipeline. The room half is vbsp's host half — mount the
/// game, parse the stock line, compile, write one file — with the write being
/// a <see cref="RoomObjectStore"/> container instead of a .bsp. The link half
/// needs no game at all: rooms arrive as objects, the layout as JSON, and the
/// only host decision is where the files are.
/// </summary>
/// <remarks>
/// Like every other verb, the host knowledge stays here: the filesystem, the
/// Steam roots, the cooker. The room library reads no environment variable
/// and touches no console; it takes a document, a definition, and a context,
/// which is what lets a fact run the whole verb against an in-memory fixture
/// (the <c>phys</c> precedent, <c>Program.cs</c>).
/// </remarks>
public static class RoomCommands
{
    /// <summary>The exit code for a compile, link, or file the run could not deliver.</summary>
    public const int ExitFailed = 1;

    /// <summary>How many rooms one layout may place; a guard, not a limit the format has.</summary>
    private const int MaxRooms = 4096;

    /// <summary>
    /// <c>ssmap room &lt;in.vmf&gt; [-out &lt;roomdir&gt;] [-def &lt;file&gt;] [stock vbsp options]</c>:
    /// compile one room's VMF into a <c>&lt;roomdir&gt;/&lt;roomname&gt;.room</c> object.
    /// </summary>
    /// <param name="disk">Where the VMF, the game content and the output live.</param>
    /// <param name="searchRoots">Where game installs are, for the cooker's library discovery.</param>
    /// <param name="args">The arguments after <c>room</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The process exit code.</returns>
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

        // -out and -def are this verb's, not stock vbsp's: take them out of
        // the line first so the stock parser never sees an option it would
        // (correctly) refuse.
        List<string> stock = [];
        string? outDirectory = null;
        string? defFile = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (Take(args, i, "-out", out string o, out int used))
            {
                outDirectory = o;
                i += used - 1;
            }
            else if (Take(args, i, "-def", out string d, out used))
            {
                defFile = d;
                i += used - 1;
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
            await output.WriteLineAsync(
                "usage: ssmap room <in.vmf> [-out <roomdir>] [-def <roomdef.json>] [stock vbsp options]")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string source = Path.GetFullPath(parsed.MapPath);
        if (!VPath.TryCreate(source, out VPath vmfPath))
        {
            await output.WriteLineAsync($"ssmap room: \"{source}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        if (!VPath.TryCreate(outDirectory ?? Path.GetDirectoryName(source)!, out VPath outDir))
        {
            await output.WriteLineAsync($"ssmap room: -out \"{outDirectory}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // The definition's home: -def names it, else the sibling sidecar
        // beside the VMF (base + ".roomdef.json") — the room's author writes
        // both files, and a sidecar means the command line names one path.
        string defPath = defFile is null
            ? source + ".roomdef.json"
            : Path.GetFullPath(defFile);
        if (!VPath.TryCreate(defPath, out VPath defVPath))
        {
            await output.WriteLineAsync($"ssmap room: -def \"{defFile}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        RoomDefinition definition;
        try
        {
            await using Stream stream = await disk.OpenReadAsync(defVPath, cancellationToken).ConfigureAwait(false);
            definition = RoomDefinitionJson.Parse(await new StreamReader(stream, Encoding.Latin1)
                .ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or LinkException)
        {
            await output.WriteLineAsync($"ssmap room: cannot read the room definition {defPath}: {exception.Message}")
                .ConfigureAwait(false);
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

        VbspContext context = new(options, mounted.Content)
        {
            // mapbase: the file's base name, lowercased
#pragma warning disable CA1308 // strlwr
            MapBase = Path.GetFileNameWithoutExtension(source).ToLowerInvariant(),
#pragma warning restore CA1308
            CollisionCooker = cooker,
            Parallelism = parsed.Threads is int degree && degree > 0
                ? new CompileParallelism { MaxDegree = degree }
                : CompileParallelism.Default,
        };

        try
        {
            VmfDocument document;
            await using (Stream vmf = await disk.OpenReadAsync(vmfPath, cancellationToken).ConfigureAwait(false))
            {
                document = await VmfDocument.ReadAsync(vmf, cancellationToken).ConfigureAwait(false);
            }

            RoomObject room = await RoomCompiler
                .CompileAsync(document, definition, context, cancellationToken).ConfigureAwait(false);

            VPath roomFile = outDir.Combine(definition.Name + ".room");
            await disk.ReplaceAsync(
                roomFile,
                async (stream, token) => await RoomObjectStore
                    .SaveAsync(room, stream, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync(
                $"ssmap room: wrote {roomFile.Value}"
                + $" ({room.ClusterCount} clusters, {definition.Sockets.Count} sockets)")
                .ConfigureAwait(false);
            return Program.ExitSuccess;
        }
        catch (Exception exception) when (exception is MapCompileException or IOException
            or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (RoomLintException exception)
        {
            await output.WriteLineAsync($"ssmap room: the room is not linkable: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }
        catch (LinkException exception)
        {
            await output.WriteLineAsync($"ssmap room: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
    }

    /// <summary>
    /// <c>ssmap link &lt;layout.json&gt; [-rooms &lt;dir&gt;] [-out &lt;map.bsp&gt;]</c>:
    /// load every <c>*.room</c> in the room directory, parse the layout, link
    /// it, and write the map.
    /// </summary>
    /// <param name="disk">Where the layout, the room objects and the output live.</param>
    /// <param name="args">The arguments after <c>link</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the link.</param>
    /// <returns>The process exit code.</returns>
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
        for (int i = 0; i < args.Count; i++)
        {
            if (Take(args, i, "-rooms", out string r, out int used))
            {
                roomsDirectory = r;
                i += used - 1;
            }
            else if (Take(args, i, "-out", out string o, out used))
            {
                outPath = o;
                i += used - 1;
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        if (rest.Count != 1)
        {
            await output.WriteLineAsync("usage: ssmap link <layout.json> [-rooms <dir>] [-out <map.bsp>]")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string layoutPath = Path.GetFullPath(rest[0]);
        if (!VPath.TryCreate(layoutPath, out VPath layoutVPath))
        {
            await output.WriteLineAsync($"ssmap link: \"{layoutPath}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string roomsDir = Path.GetFullPath(roomsDirectory ?? Path.GetDirectoryName(layoutPath)!);
        if (!VPath.TryCreate(roomsDir, out VPath roomsDirPath))
        {
            await output.WriteLineAsync($"ssmap link: -rooms \"{roomsDirectory}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string mapName = outPath is null
            ? Path.ChangeExtension(layoutPath, ".bsp")
            : Path.GetFullPath(outPath);
        if (!VPath.TryCreate(mapName, out VPath mapPath))
        {
            await output.WriteLineAsync($"ssmap link: -out \"{mapName}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string layoutJson;
        try
        {
            await using Stream stream = await disk.OpenReadAsync(layoutVPath, cancellationToken).ConfigureAwait(false);
            layoutJson = await new StreamReader(stream, Encoding.UTF8)
                .ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap link: cannot read {layoutPath}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        // The room objects first: the library's grid is the rooms' grid, and a
        // layout may restate it or trust it (LevelLayoutJson.HasGrid).
        List<RoomObject> loaded = [];
        try
        {
            await foreach (VPath file in disk
                .EnumerateAsync(roomsDirPath, "*.room", false, cancellationToken).ConfigureAwait(false))
            {
                await using Stream stream = await disk.OpenReadAsync(file, cancellationToken).ConfigureAwait(false);
                loaded.Add(await RoomObjectStore.LoadAsync(stream, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap link: cannot read the room library {roomsDir}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }
        catch (LinkException exception)
        {
            await output.WriteLineAsync($"ssmap link: {roomsDir}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        if (loaded.Count == 0)
        {
            await output.WriteLineAsync($"ssmap link: no .room files in {roomsDir}").ConfigureAwait(false);
            return ExitFailed;
        }

        if (loaded.Count > MaxRooms)
        {
            await output.WriteLineAsync(
                $"ssmap link: {roomsDir} holds {loaded.Count} room files; a level places at most {MaxRooms}")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        LevelLayout parsedLayout;
        try
        {
            parsedLayout = LevelLayoutJson.Parse(layoutJson);
        }
        catch (LinkException exception)
        {
            await output.WriteLineAsync($"ssmap link: {layoutPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        RoomLibrary library = new(
            parsedLayout.Rooms.Count > 0 && LevelLayoutJson.HasGrid(parsedLayout)
                ? parsedLayout.Kit
                : loaded[0].Definition.Kit,
            LevelLayoutJson.HasGrid(parsedLayout) ? parsedLayout.CellSize : loaded[0].Definition.CellSize);
        try
        {
            foreach (RoomObject room in loaded)
            {
                library.Add(room);
            }
        }
        catch (LinkException exception)
        {
            await output.WriteLineAsync($"ssmap link: {roomsDir}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        LevelLayout layout = LevelLayoutJson.HasGrid(parsedLayout)
            ? parsedLayout
            : parsedLayout with { CellSize = library.CellSize, Kit = library.Kit };

        // The link reads no content — only the context's parallelism — so the
        // context needs mounts for none. A linked map carries no content lump
        // for the link to want.
        await using ContentFileSystem content = new([]);
        VbspContext context = new(VbspOptions.Default, content);

        try
        {
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
                $"ssmap link: wrote {mapPath.Value}"
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
            await output.WriteLineAsync($"ssmap link: the layout is not linkable: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }
        catch (LinkException exception)
        {
            await output.WriteLineAsync($"ssmap link: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
    }

    /// <summary>
    /// Whether <paramref name="args"/> at <paramref name="index"/> is <paramref name="flag"/>
    /// with a value after it, consumed as a pair.
    /// </summary>
    private static bool Take(IReadOnlyList<string> args, int index, string flag, out string value, out int used)
    {
        used = 0;
        value = string.Empty;
        if (index >= args.Count || !string.Equals(args[index], flag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index + 1 >= args.Count)
        {
            // Flag at the end of the line: leave it for the usage check to
            // report (a dangling -out is a usage problem, not a crash).
            return false;
        }

        value = args[index + 1];
        used = 2;
        return true;
    }
}
