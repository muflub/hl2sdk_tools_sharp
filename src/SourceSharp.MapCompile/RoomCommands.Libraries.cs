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

        (PackChoice[]? choices, int choiceExit) = await ChoosePacksAsync("ssmap link", keys, libraryPaths, roomsPacks, output).ConfigureAwait(false);
        if (choices is null)
        {
            return choiceExit;
        }

        if (!level.Placed.Any())
        {
            await output.WriteLineAsync($"ssmap link: {levelPath}: the level places no room").ConfigureAwait(false);
            return ExitFailed;
        }

        // Each pack opened once, however many keys it serves: a combined
        // pack serves every key of the level.
        Dictionary<string, OpenedPack> opened = new(StringComparer.Ordinal);
        try
        {
            KeySource[] sources = new KeySource[keys.Count];
            List<string> moved = [];
            for (int i = 0; i < keys.Count; i++)
            {
                string pack = HostPaths.Display(choices[i].Path);
                try
                {
                    if (!opened.TryGetValue(pack, out OpenedPack? open))
                    {
                        if (!await disk.ExistsAsync(choices[i].Path, cancellationToken).ConfigureAwait(false))
                        {
                            await output.WriteLineAsync(
                                $"ssmap link: {levelPath}: there is no room pack {pack} for library {keys[i].Key};"
                                + $" compile the library with ssmap room, or point -rooms {keys[i].Key}= at its pack")
                                .ConfigureAwait(false);
                            return ExitFailed;
                        }

                        open = await OpenedPack.OpenAsync(disk, choices[i].Path, !nav.Skip, cancellationToken).ConfigureAwait(false);
                        opened[pack] = open;
                    }

                    if (KeySource.Find(open, keys[i].Key, choices[i].Shared && keys.Count > 1) is not { } found)
                    {
                        await output.WriteLineAsync($"ssmap link: {KeySource.Missing(open, keys[i].Key, keys[0].Key)}").ConfigureAwait(false);
                        return ExitFailed;
                    }

                    sources[i] = found;
                    if (found.MovedFrom(keys[i].Path) is { } recorded)
                    {
                        moved.Add($"library {keys[i].Key}: the level names {keys[i].Path}, but room pack {pack} built it from {recorded}.");
                    }

                    if (found.Skybox is { } skybox && open.Index.Find(found.PackName(skybox)) is null)
                    {
                        await output.WriteLineAsync(
                            $"ssmap link: the room pack {pack} names skybox room \"{found.PackName(skybox)}\" but does not hold it; recompile the library with ssmap room")
                            .ConfigureAwait(false);
                        return ExitFailed;
                    }
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
                resolved = LevelLibraries.Resolve(level, [.. sources.Select(x => x.Names)]);
            }
            catch (LevelFileException exception)
            {
                await output.WriteLineAsync($"ssmap link: {levelPath}: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
            }

            // Each pack's placed rooms, at their turns, and the level's
            // skybox (the first library's, or the earliest library's with one
            // when the first has none); then every library with its rooms.
            (List<LevelCell> first, Dictionary<string, HashSet<int>> turns) = PlacedRooms(resolved);
            int? skyboxSource = LevelLibraries.SkyboxSource([.. sources.Select(s => s.Skybox)]);
            List<IReadOnlyList<RoomObject>> loaded = [];
            for (int i = 0; i < keys.Count; i++)
            {
                KeySource source = sources[i];
                string prefix = keys[i].Key + LevelLibraries.Separator;
                List<RoomPackRequest> requests = [.. first
                    .Where(c => c.Room.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(c => new RoomPackRequest(source.PackName(c.Room[prefix.Length..]), turns[c.Room]) { Navigation = !nav.Skip })];
                if (i == skyboxSource && source.Skybox is { } skybox && !turns.ContainsKey(prefix + skybox))
                {
                    requests.Add(new RoomPackRequest(source.PackName(skybox), [0]));
                }

                try
                {
                    loaded.Add(requests.Count == 0 ? [] : await RoomPack.LoadRoomsAsync(source.Pack.Stream, source.Pack.Index, requests, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await output.WriteLineAsync($"ssmap link: cannot read the room pack {HostPaths.Display(choices[i].Path)}: {exception.Message}").ConfigureAwait(false);
                    return ExitFailed;
                }
                catch (Exception exception) when (exception is LinkException or ArgumentException)
                {
                    await output.WriteLineAsync($"ssmap link: {HostPaths.Display(choices[i].Path)}: {exception.Message}").ConfigureAwait(false);
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
                    Options = sources[i].Options,
                    LibraryEntities = sources[i].Entities,
                    SkyboxRoom = sources[i].Skybox,
                };
                try
                {
                    foreach (RoomObject room in loaded[i])
                    {
                        // A namespace's room is held under its name within
                        // its library, as the level's cells name it.
                        if (sources[i].Space is { } space)
                        {
                            library.Add(room.Definition.Name[space.Prefix.Length..], room, 0);
                        }
                        else
                        {
                            library.Add(room);
                        }
                    }
                }
                catch (ArgumentException exception)
                {
                    await output.WriteLineAsync($"ssmap link: {HostPaths.Display(choices[i].Path)}: {exception.Message}").ConfigureAwait(false);
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
            foreach (OpenedPack pack in opened.Values)
            {
                await pack.Stream.DisposeAsync().ConfigureAwait(false);
            }

            opened.Clear();
            return await LinkLoadedAsync(
                disk, resolved, levelBytes, levelPath, set.Rooms,
                nav.Skip ? null : RoomCompileIds.LevelPackId([.. keys.Select((k, i) => (k.Key, sources[i].Pack.PackId))]),
                [.. moved, .. set.Warnings], linkOptions, mapPath, nav, output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (OpenedPack pack in opened.Values)
            {
                await pack.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A pack that cannot give a key its library: a combined pack without
    /// that namespace, or a plain pack given to every key of a level of
    /// several. The message is the whole line after the verb.
    /// </summary>
    /// <param name="message">17.10's text.</param>
    private sealed class PackKeyException(string message) : Exception(message);

    /// <summary>Where a level's key finds its pack, and whether a plain <c>-rooms</c> gave it.</summary>
    /// <param name="Path">The pack.</param>
    /// <param name="Shared">Whether the pack came from a <c>-rooms &lt;pack&gt;</c> that names no key.</param>
    private sealed record PackChoice(VPath Path, bool Shared);

    /// <summary>
    /// Each key's pack (the rooms design, 17.10): <c>-rooms &lt;key&gt;=&lt;pack&gt;</c>
    /// for that key, else a <c>-rooms &lt;pack&gt;</c> that names no key (a
    /// combined pack, whose namespaces serve every key), else the pack beside
    /// the key's library; or null, with the message printed.
    /// </summary>
    private static async Task<(PackChoice[]? Choices, int Exit)> ChoosePacksAsync(
        string verb, IReadOnlyList<LevelLibrary> keys, IReadOnlyList<string> libraryPaths, IReadOnlyList<string> roomsPacks, TextWriter output)
    {
        string?[] packArgs = new string?[keys.Count];
        string? shared = null;
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
                        $"{verb}: -rooms names library {key}, which the level does not list; its libraries are {And([.. keys.Select(k => k.Key)])}.")
                        .ConfigureAwait(false);
                    return (null, Program.ExitUsage);
                }

                packArgs[index] = arg[(equals + 1)..];
            }
            else if (shared is null)
            {
                shared = arg;
            }
            else
            {
                await output.WriteLineAsync(
                    $"{verb}: -rooms {shared} and -rooms {arg} each name a pack for every library of the level; give one, or -rooms <key>=<pack> for each key.")
                    .ConfigureAwait(false);
                return (null, Program.ExitUsage);
            }
        }

        PackChoice[] choices = new PackChoice[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            string? given = packArgs[i] ?? shared;
            if (!TryHostPath(given ?? DefaultPack(libraryPaths[i]), out VPath path))
            {
                await output.WriteLineAsync($"{verb}: -rooms \"{given}\" for library {keys[i].Key} is not a usable path").ConfigureAwait(false);
                return (null, Program.ExitUsage);
            }

            choices[i] = new PackChoice(path, packArgs[i] is null && shared is not null);
        }

        return (choices, Program.ExitSuccess);
    }

    /// <summary>A pack open for a link: its stream, index, namespaces and library sections.</summary>
    private sealed class OpenedPack
    {
        private OpenedPack(
            string display,
            Stream stream,
            RoomPackIndex index,
            IReadOnlyList<RoomPackNamespace>? namespaces,
            IReadOnlyList<VmfChunk> entities,
            RoomLibraryOptions options,
            string? skybox,
            Guid? packId)
        {
            Display = display;
            Stream = stream;
            Index = index;
            Namespaces = namespaces;
            Entities = entities;
            Options = options;
            Skybox = skybox;
            PackId = packId;
        }

        public string Display { get; }

        public Stream Stream { get; }

        public RoomPackIndex Index { get; }

        /// <summary>The pack's namespaces, or null for a plain pack.</summary>
        public IReadOnlyList<RoomPackNamespace>? Namespaces { get; }

        public IReadOnlyList<VmfChunk> Entities { get; }

        public RoomLibraryOptions Options { get; }

        /// <summary>The skybox room as the pack's index names it (qualified in a pack with namespaces).</summary>
        public string? Skybox { get; }

        public Guid? PackId { get; }

        /// <summary>Opens a pack and reads its index and library sections; the stream is the caller's to dispose.</summary>
        public static async Task<OpenedPack> OpenAsync(IFileSystem disk, VPath path, bool packId, CancellationToken cancellationToken)
        {
            Stream stream = await disk.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            try
            {
                // In the order the library sections are written, so a pack
                // on a stream that cannot seek reads too.
                RoomPackIndex index = await RoomPack.ReadIndexAsync(stream, cancellationToken).ConfigureAwait(false);
                Guid? id = packId ? await RoomNavPack.ReadPackIdAsync(stream, index, cancellationToken).ConfigureAwait(false) : null;
                IReadOnlyList<VmfChunk> entities = await RoomPack.ReadLibraryEntitiesAsync(stream, index, cancellationToken).ConfigureAwait(false);
                RoomLibraryOptions options = await RoomPack.ReadLibraryOptionsAsync(stream, index, cancellationToken).ConfigureAwait(false);
                string? skybox = await RoomPack.ReadLibrarySkyboxAsync(stream, index, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<RoomPackNamespace>? namespaces = await RoomPack.ReadNamespacesAsync(stream, index, cancellationToken).ConfigureAwait(false);
                return new OpenedPack(HostPaths.Display(path), stream, index, namespaces, entities, options, skybox, id);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// One key's library in its pack: the whole of a plain pack, or one
    /// namespace of a combined one. A namespace's rooms are named
    /// <c>key.room</c> in the pack and <c>room</c> here, as the level's
    /// cells name them within the library.
    /// </summary>
    private sealed class KeySource
    {
        private KeySource(OpenedPack pack, RoomPackNamespace? space)
        {
            Pack = pack;
            Space = space;
            if (space is null)
            {
                Names = [.. pack.Index.Entries.Select(e => e.Name)];
                Entities = pack.Entities;
                Options = pack.Options;
                Skybox = pack.Skybox;
                return;
            }

            Names = [.. pack.Index.Entries.Skip(space.FirstRoom).Take(space.RoomCount).Select(e => e.Name[space.Prefix.Length..])];

            // The pack's singletons are its first namespace's (every other
            // library's were dropped when it was built, 17.10), so only the
            // first namespace has any; each keeps its own name keys.
            bool first = ReferenceEquals(space, pack.Namespaces![0]);
            Entities = first ? pack.Entities : [];
            Options = (first ? pack.Options : RoomLibraryOptions.None) with { NameKeys = space.NameKeys };
            Skybox = first && pack.Skybox is { } sky && sky.StartsWith(space.Prefix, StringComparison.Ordinal) ? sky[space.Prefix.Length..] : null;
        }

        public OpenedPack Pack { get; }

        /// <summary>The key's namespace, or null for a plain pack.</summary>
        public RoomPackNamespace? Space { get; }

        /// <summary>The library's rooms, as the level names them.</summary>
        public IReadOnlyList<string> Names { get; }

        public IReadOnlyList<VmfChunk> Entities { get; }

        public RoomLibraryOptions Options { get; }

        /// <summary>The library's skybox room, as the level names it, or null.</summary>
        public string? Skybox { get; }

        /// <summary>A room's name in the pack's index.</summary>
        public string PackName(string room) => Space is { } space ? space.Prefix + room : room;

        /// <summary>
        /// The source the namespace was built from, when its file name is not
        /// the level's (paths move between machines; the namespace is the
        /// identity, so the link warns and links); else null.
        /// </summary>
        public string? MovedFrom(string levelPath) =>
            Space is { } space && !string.Equals(FileName(space.Source), FileName(levelPath), StringComparison.Ordinal) ? space.Source : null;

        /// <summary>
        /// A key's library in a pack, or null when the pack cannot give it:
        /// a combined pack without that namespace, or a plain pack given to
        /// every key of a level of several (<paramref name="plainRefused"/>).
        /// </summary>
        public static KeySource? Find(OpenedPack pack, string key, bool plainRefused)
        {
            if (pack.Namespaces is not { } namespaces)
            {
                return plainRefused ? null : new KeySource(pack, null);
            }

            return RoomPackNamespaces.Find(namespaces, key) is { } space ? new KeySource(pack, space) : null;
        }

        /// <summary>Why <see cref="Find"/> gave nothing: 17.10's texts.</summary>
        public static string Missing(OpenedPack pack, string key, string firstKey) =>
            pack.Namespaces is { } namespaces
                ? $"room pack {pack.Display} combines libraries {And([.. namespaces.Select(n => n.Key)])}; it has none named {key}."
                : $"room pack {pack.Display} holds one library without a namespace; give it to one key with -rooms {firstKey}={pack.Display}.";

        private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
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

        // Each key's pack as ssmap link finds it; a library: level takes
        // -rooms as its one pack, as it always did.
        PackChoice[] choices;
        if (level.Libraries is null)
        {
            string? given = roomsPacks.Count == 0 ? null : roomsPacks[^1];
            if (!TryHostPath(given ?? DefaultPack(libraryPaths[0]), out VPath only))
            {
                await output.WriteLineAsync($"ssmap rooms: \"{libraryPaths[0]}\" or its pack is not a usable path").ConfigureAwait(false);
                return Program.ExitUsage;
            }

            choices = [new PackChoice(only, false)];
        }
        else if ((await ChoosePacksAsync("ssmap rooms", keys, libraryPaths, roomsPacks, output).ConfigureAwait(false)) is { Choices: { } chosen })
        {
            choices = chosen;
        }
        else
        {
            return Program.ExitUsage;
        }

        List<VmfDocument> vmfs = [];
        System.Text.StringBuilder listing = new();
        for (int i = 0; i < keys.Count; i++)
        {
            VPath packPath = choices[i].Path;
            if (!VPath.TryCreate(libraryPaths[i], out VPath libraryVPath))
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
                string key = level.Libraries is null ? Path.GetFileNameWithoutExtension(level.Library) : keys[i].Key;
                counts = await ReadPackCountsAsync(disk, packPath, key, choices[i].Shared && keys.Count > 1, cancellationToken).ConfigureAwait(false);
            }
            catch (PackKeyException exception)
            {
                await output.WriteLineAsync($"ssmap rooms: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
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
