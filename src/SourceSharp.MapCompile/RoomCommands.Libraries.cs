//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap link</c> and <c>ssmap rooms</c> over a level that takes its
/// rooms from several libraries (the rooms design, section 17.2 to 17.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>One pack per library.</b> Each key's rooms come from its own pack:
/// <c>&lt;library&gt;.roompack</c> beside its VMF, where <c>ssmap room</c>
/// writes by default, unless <c>-rooms &lt;key&gt;=&lt;pack&gt;</c> names
/// another (repeatable, one per key). A plain <c>-rooms &lt;pack&gt;</c> is a
/// pack of one library without a namespace, so it is accepted only for a
/// level that lists one library; combined packs, which hold several under
/// namespaces, are PR 18's.
/// </para>
/// <para>
/// Every pack's index and library sections are read first (the room names
/// resolve the level's cells, the library sections feed the singleton
/// rule), then from each pack exactly the rooms the level places of it, at
/// the turns it places them, and the first library's skybox room. Every
/// stream is closed before the link starts, whatever happens.
/// </para>
/// </remarks>
public static partial class RoomCommands
{
    private static async Task<int> LinkLibrariesAsync(
        IFileSystem disk,
        LevelGrid level,
        byte[] levelBytes,
        string levelPath,
        IReadOnlyList<string> roomsPacks,
        LevelLinkOptions linkOptions,
        VPath mapPath,
        LinkNavOptions nav,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LevelLibrary> keys = level.Libraries!;
        if (await LibraryPathsAsync(level, levelPath, "link", output).ConfigureAwait(false) is not { } libraryPaths)
        {
            return ExitFailed;
        }

        string?[] packArgs = new string?[keys.Count];
        foreach (string arg in roomsPacks)
        {
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            string key = equals > 0 ? arg[..equals] : string.Empty;
            if (equals > 0 && LevelLibraries.KeyProblem(key) is null)
            {
                int index = IndexOfKey(keys, key);
                if (index < 0)
                {
                    await output.WriteLineAsync(
                        $"ssmap link: -rooms names library {key}, which the level does not list; its libraries are {And([.. keys.Select(k => k.Key)])}.")
                        .ConfigureAwait(false);
                    return Program.ExitUsage;
                }

                packArgs[index] = arg[(equals + 1)..];
            }
            else if (keys.Count == 1)
            {
                packArgs[0] = arg;
            }
            else
            {
                await output.WriteLineAsync(
                    $"ssmap link: room pack {arg} holds one library without a namespace; give it to one key with -rooms {keys[0].Key}={arg}.")
                    .ConfigureAwait(false);
                return ExitFailed;
            }
        }

        VPath[] packPaths = new VPath[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            if (!TryHostPath(packArgs[i] ?? DefaultPack(libraryPaths[i]), out packPaths[i]))
            {
                await output.WriteLineAsync($"ssmap link: -rooms \"{packArgs[i]}\" for library {keys[i].Key} is not a usable path").ConfigureAwait(false);
                return Program.ExitUsage;
            }
        }

        if (!level.Placed.Any())
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: the level places no room").ConfigureAwait(false);
            return ExitFailed;
        }

        List<Stream> streams = [];
        try
        {
            // Every pack's index and library sections, in level order.
            List<RoomPackIndex> indexes = [];
            List<(RoomLibraryOptions Options, IReadOnlyList<VmfChunk> Entities)> settings = [];
            List<string?> skyboxes = [];
            List<(string Key, Guid? Pack)> packIds = [];
            for (int i = 0; i < keys.Count; i++)
            {
                string pack = HostPaths.Display(packPaths[i]);
                try
                {
                    if (!await disk.ExistsAsync(packPaths[i], cancellationToken).ConfigureAwait(false))
                    {
                        await output.WriteLineAsync(
                            $"ssmap link: {levelPath}: there is no room pack {pack} for library {keys[i].Key};"
                            + $" compile the library with ssmap room, or point -rooms {keys[i].Key}= at its pack")
                            .ConfigureAwait(false);
                        return ExitFailed;
                    }

                    Stream stream = await disk.OpenReadAsync(packPaths[i], cancellationToken).ConfigureAwait(false);
                    streams.Add(stream);
                    RoomPackIndex index = await RoomPack.ReadIndexAsync(stream, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<VmfChunk> entities = await RoomPack.ReadLibraryEntitiesAsync(stream, index, cancellationToken).ConfigureAwait(false);
                    RoomLibraryOptions options = await RoomPack.ReadLibraryOptionsAsync(stream, index, cancellationToken).ConfigureAwait(false);
                    string? skybox = await RoomPack.ReadLibrarySkyboxAsync(stream, index, cancellationToken).ConfigureAwait(false);
                    if (skybox is not null && index.Find(skybox) is null)
                    {
                        await output.WriteLineAsync(
                            $"ssmap link: the room pack {pack} names skybox room \"{skybox}\" but does not hold it; recompile the library with ssmap room")
                            .ConfigureAwait(false);
                        return ExitFailed;
                    }

                    indexes.Add(index);
                    skyboxes.Add(skybox);
                    packIds.Add((keys[i].Key, nav.Skip ? null : await RoomNavPack.ReadPackIdAsync(stream, index, cancellationToken).ConfigureAwait(false)));
                    settings.Add((options, entities));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await output.WriteLineAsync($"ssmap link: cannot read the room pack {pack}: {exception.Message}").ConfigureAwait(false);
                    return ExitFailed;
                }
                catch (Exception exception) when (exception is LinkException or ArgumentException)
                {
                    await output.WriteLineAsync($"ssmap link: {pack}: {exception.Message}").ConfigureAwait(false);
                    return ExitFailed;
                }
            }

            // The cells resolved against the packs' rooms (17.2).
            LevelGrid resolved;
            try
            {
                resolved = LevelLibraries.Resolve(level, [.. indexes.Select(x => (IReadOnlyList<string>)[.. x.Entries.Select(e => e.Name)])]);
            }
            catch (LevelFileException exception)
            {
                await output.WriteLineAsync($"ssmap link: {levelPath}: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
            }

            // Each pack's placed rooms, at their turns, and the first
            // library's skybox; then every library with its rooms.
            (List<LevelCell> first, Dictionary<string, HashSet<int>> turns) = PlacedRooms(resolved);
            List<IReadOnlyList<RoomObject>> loaded = [];
            for (int i = 0; i < keys.Count; i++)
            {
                string prefix = keys[i].Key + LevelLibraries.Separator;
                List<RoomPackRequest> requests = [.. first
                    .Where(c => c.Room.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(c => new RoomPackRequest(c.Room[prefix.Length..], turns[c.Room]) { Navigation = !nav.Skip })];
                if (i == 0 && skyboxes[0] is { } skybox && !turns.ContainsKey(prefix + skybox))
                {
                    requests.Add(new RoomPackRequest(skybox, [0]));
                }

                try
                {
                    loaded.Add(requests.Count == 0 ? [] : await RoomPack.LoadRoomsAsync(streams[i], indexes[i], requests, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await output.WriteLineAsync($"ssmap link: cannot read the room pack {HostPaths.Display(packPaths[i])}: {exception.Message}").ConfigureAwait(false);
                    return ExitFailed;
                }
                catch (Exception exception) when (exception is LinkException or ArgumentException)
                {
                    await output.WriteLineAsync($"ssmap link: {HostPaths.Display(packPaths[i])}: {exception.Message}").ConfigureAwait(false);
                    return ExitFailed;
                }
            }

            RoomDefinition sample = loaded.SelectMany(r => r).First().Definition;
            List<RoomLibrary> withRooms = [];
            for (int i = 0; i < keys.Count; i++)
            {
                RoomDefinition grid = loaded[i].Count > 0 ? loaded[i][0].Definition : sample;
                RoomLibrary library = new(grid.Kit, grid.CellSize)
                {
                    Options = settings[i].Options,
                    LibraryEntities = settings[i].Entities,
                    SkyboxRoom = skyboxes[i],
                };
                try
                {
                    foreach (RoomObject room in loaded[i])
                    {
                        library.Add(room);
                    }
                }
                catch (ArgumentException exception)
                {
                    await output.WriteLineAsync($"ssmap link: {HostPaths.Display(packPaths[i])}: {exception.Message}").ConfigureAwait(false);
                    return ExitFailed;
                }

                withRooms.Add(library);
            }

            LevelLibrarySet set;
            try
            {
                set = LevelLibraries.Combine(resolved, withRooms);
            }
            catch (Exception exception) when (exception is LinkException or ArgumentException)
            {
                await output.WriteLineAsync($"ssmap link: {levelPath}: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
            }

            // Every stream is done with: the link reads nothing more.
            foreach (Stream stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            streams.Clear();
            return await LinkLoadedAsync(
                disk, resolved, levelBytes, levelPath, set.Rooms, nav.Skip ? null : RoomCompileIds.LevelPackId(packIds), set.Warnings,
                linkOptions, mapPath, nav, output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (Stream stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> FlattenLibrariesAsync(
        IFileSystem disk,
        LevelGrid level,
        string levelPath,
        VPath vmfPath,
        bool modEntities,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (await LibraryPathsAsync(level, levelPath, "link", output).ConfigureAwait(false) is not { } libraryPaths)
        {
            return ExitFailed;
        }

        List<VmfDocument> vmfs = [];
        for (int i = 0; i < libraryPaths.Length; i++)
        {
            if (!VPath.TryCreate(libraryPaths[i], out VPath path))
            {
                await output.WriteLineAsync($"ssmap link: the library \"{libraryPaths[i]}\" is not a usable path").ConfigureAwait(false);
                return ExitFailed;
            }

            try
            {
                vmfs.Add(await ReadVmfAsync(disk, path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ChunkFileException)
            {
                await output.WriteLineAsync($"ssmap link: {libraryPaths[i]}: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
            }
        }

        try
        {
            FlattenedLevel flat = LevelFlattener.FlattenLevel(level, vmfs, new LevelFlattenOptions { ModEntities = modEntities });
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
        catch (RoomLibraryException exception)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: a library: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (RoomLintException exception)
        {
            await output.WriteLineAsync($"ssmap link: the level is not linkable: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (Exception exception) when (exception is LevelFileException or LinkException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        await output.WriteLineAsync($"ssmap link: wrote {HostPaths.Display(vmfPath)} ({level.Placed.Count()} rooms, flattened)").ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// Each library VMF of a level of several, resolved against the level
    /// file's folder, or null (with the message printed) when one cannot be
    /// or two name the same file.
    /// </summary>
    private static async Task<string[]?> LibraryPathsAsync(LevelGrid level, string levelPath, string verb, TextWriter output)
    {
        string folder = Path.GetDirectoryName(levelPath)!;
        List<string> paths = [];
        foreach (LevelLibrary library in level.Libraries!)
        {
            try
            {
                paths.Add(Path.GetFullPath(Path.Combine(folder, library.Path)));
            }
            catch (ArgumentException)
            {
                await output.WriteLineAsync($"ssmap {verb}: {levelPath}: library {library.Key}, \"{library.Path}\", is not a usable path").ConfigureAwait(false);
                return null;
            }
        }

        try
        {
            LevelLibraries.CheckFiles(level, path => Path.GetFullPath(Path.Combine(folder, path)));
        }
        catch (LevelFileException exception)
        {
            await output.WriteLineAsync($"ssmap {verb}: {levelPath}: {exception.Message}").ConfigureAwait(false);
            return null;
        }

        return [.. paths];
    }

    private static int IndexOfKey(IReadOnlyList<LevelLibrary> keys, string key)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether an operand is a level file rather than a library VMF: by its extension.</summary>
    private static bool IsLevelFile(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>ssmap rooms &lt;level.yaml&gt;</c>: every library of the level listed, then the level's library warnings.</summary>
    private static async Task<int> RoomsOfLevelAsync(
        IFileSystem disk, string levelFile, IReadOnlyList<string> roomsPacks, TextWriter output, CancellationToken cancellationToken)
    {
        string levelPath = Path.GetFullPath(levelFile);
        if (!VPath.TryCreate(levelPath, out VPath levelVPath))
        {
            await output.WriteLineAsync($"ssmap rooms: \"{levelPath}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        LevelGrid level;
        try
        {
            byte[] bytes = await ReadBytesAsync(disk, levelVPath, cancellationToken).ConfigureAwait(false);
            level = LevelYaml.Parse(System.Text.Encoding.UTF8.GetString(bytes), Path.GetFileNameWithoutExtension(levelPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LevelFileException)
        {
            await output.WriteLineAsync($"ssmap rooms: {levelPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        IReadOnlyList<LevelLibrary> keys = level.Libraries ?? [new LevelLibrary(string.Empty, level.Library)];
        string[] libraryPaths;
        if (level.Libraries is null)
        {
            libraryPaths = [Path.GetFullPath(Path.Combine(Path.GetDirectoryName(levelPath)!, level.Library))];
        }
        else if (await LibraryPathsAsync(level, levelPath, "rooms", output).ConfigureAwait(false) is { } paths)
        {
            libraryPaths = paths;
        }
        else
        {
            return ExitFailed;
        }

        string?[] packArgs = new string?[keys.Count];
        foreach (string arg in roomsPacks)
        {
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            int index = equals > 0 && level.Libraries is not null ? IndexOfKey(keys, arg[..equals]) : -1;
            if (index >= 0)
            {
                packArgs[index] = arg[(equals + 1)..];
            }
            else if (keys.Count == 1)
            {
                packArgs[0] = arg;
            }
            else
            {
                await output.WriteLineAsync($"ssmap rooms: -rooms \"{arg}\" names no library of the level; write -rooms <key>=<pack>.").ConfigureAwait(false);
                return Program.ExitUsage;
            }
        }

        List<VmfDocument> vmfs = [];
        System.Text.StringBuilder listing = new();
        for (int i = 0; i < keys.Count; i++)
        {
            if (!VPath.TryCreate(libraryPaths[i], out VPath libraryVPath) || !TryHostPath(packArgs[i] ?? DefaultPack(libraryPaths[i]), out VPath packPath))
            {
                await output.WriteLineAsync($"ssmap rooms: \"{libraryPaths[i]}\" or its pack is not a usable path").ConfigureAwait(false);
                return Program.ExitUsage;
            }

            IReadOnlyList<LibraryRoom> rooms;
            try
            {
                VmfDocument vmf = await ReadVmfAsync(disk, libraryVPath, cancellationToken).ConfigureAwait(false);
                vmfs.Add(vmf);
                rooms = RoomLibraryVmf.Split(vmf);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ChunkFileException or RoomLibraryException)
            {
                await output.WriteLineAsync($"ssmap rooms: {libraryPaths[i]}: {exception.Message}").ConfigureAwait(false);
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

            listing.Append(level.Libraries is null ? $"library: {keys[i].Path}\n" : $"library {keys[i].Key}: {keys[i].Path}\n");
            listing.Append(counts is null
                ? DescribeLibrary(rooms)
                : DescribeLibrary(rooms, counts.Counts, counts.Options, EntityClassTable.Default, counts.Names, counts.LibraryEntities, counts.Lighting));
        }

        IReadOnlyList<string> warnings;
        try
        {
            warnings = LevelLibraries.CheckLibraries(level, vmfs);
        }
        catch (Exception exception) when (exception is LevelFileException or LinkException or RoomLibraryException)
        {
            await output.WriteLineAsync($"ssmap rooms: {levelPath}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        await output.WriteAsync(listing.ToString()).ConfigureAwait(false);
        foreach (string warning in warnings)
        {
            await output.WriteLineAsync($"ssmap rooms: warning: {warning}").ConfigureAwait(false);
        }

        return Program.ExitSuccess;
    }

    /// <summary><c>a</c>, <c>a and b</c>, <c>a, b and c</c>.</summary>
    private static string And(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
}
