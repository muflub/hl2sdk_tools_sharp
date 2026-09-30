//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap roompack</c> and <c>ssmap room -namespace</c>: several libraries
/// compiled into one pack under namespaces (the rooms design, 17.10), and
/// the phases they share with <c>ssmap room</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One pool, one worldspawn, one sun.</b> Every library is split before
/// any room compiles; the libraries are held to one cell size and one door
/// kit; the singleton rule is applied once, here, and its lines printed;
/// then every room of every library compiles in one run of
/// <see cref="RoomLibraryBuild"/>, <c>-threads</c> at once, each named
/// <c>key.room</c> and carrying the first library's worldspawn, and lit
/// under the level's sun: the first library's, or the earliest library's
/// with one when the first has none (<see cref="RoomPackCombiner"/>, D26,
/// D29). A
/// level of the pack links without the worldspawn, sun and singleton lines
/// separate packs would give.
/// </para>
/// <para>
/// <b>Rebuilding one library.</b> <c>-incremental</c> sends every room
/// through the room cache, whose key covers the injected worldspawn and the
/// qualified name (both are the room's document and definition), so an edit
/// to one library compiles that library's changed rooms and reuses the rest.
/// <c>-only &lt;keys&gt;</c> compiles only those namespaces and copies every
/// other one's sections byte for byte from the existing pack, once its VMF
/// and the level's singletons (D29) are what that pack recorded.
/// </para>
/// </remarks>
public static partial class RoomCommands
{
    /// <summary>
    /// <c>ssmap roompack -out &lt;pack&gt; &lt;key&gt;=&lt;library.vmf&gt; ...</c>
    /// or <c>ssmap roompack -level &lt;level.yaml&gt;</c>: compile several
    /// libraries into one pack with namespaces.
    /// </summary>
    /// <param name="disk">Where the libraries, the game content and the output live.</param>
    /// <param name="searchRoots">Where game installs are, for the cooker's library discovery.</param>
    /// <param name="args">The arguments after <c>roompack</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the compiles.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunRoomPackAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default) =>
        RunRoomPackAsync(disk, searchRoots, args, output, OpenCacheStoreAsync, cancellationToken);

    /// <summary>
    /// <see cref="RunRoomPackAsync(IFileSystem, IReadOnlyList{VPath}, IReadOnlyList{string}, TextWriter, CancellationToken)"/>
    /// with the cache store <c>-incremental</c> opens given by the host.
    /// </summary>
    /// <param name="disk">Where the libraries, the game content and the output live.</param>
    /// <param name="searchRoots">Where game installs are, for the cooker's library discovery.</param>
    /// <param name="args">The arguments after <c>roompack</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="openCache">
    /// Opens the store at the path <c>-incremental</c> names
    /// (<c>&lt;pack&gt;.sscache.db</c> beside the pack, or in
    /// <c>-cache-dir</c>), as for <c>ssmap room</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the compiles.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// <para>
    /// <b>Operands.</b> <c>key=path</c>, in the order that decides the
    /// singletons; a bare path takes its file name's stem as its key, and
    /// is refused when the stem is not a key. <c>-level</c> takes the keys,
    /// paths and order from a level file's <c>libraries</c> (its
    /// <c>library</c>, keyed by its stem, for one), resolved against the
    /// level file's folder, and <c>-out</c> then defaults to the level file
    /// with <c>.roompack</c>; without <c>-level</c>, <c>-out</c> is required.
    /// The game is mounted as for <c>ssmap room</c>: <c>-game</c>, else the
    /// first library's folder's parent. Every other option is
    /// <c>ssmap room</c>'s, and means what it means there.
    /// </para>
    /// <para>
    /// <b>What a namespace records</b> (<see cref="RoomPackNamespaces"/>): its
    /// key, its VMF as given (relative to the pack's folder, <c>/</c>
    /// separators), its VMF's SHA-256, the digest of the first library's
    /// singletons it was compiled under, its rooms' place in the pack, and
    /// its name keys. The pack's id is a function of every key and VMF and
    /// of the options (<see cref="RoomCompileIds.CombinedPackId"/>), so
    /// <c>-only</c> writes the pack a full build writes when nothing it
    /// copies has changed, and refuses when something has.
    /// </para>
    /// </remarks>
    public static async Task<int> RunRoomPackAsync(
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
        const string Verb = "ssmap roompack";

        PackArgs run = new(Verb);
        List<string> only = [];
        string? levelFile = null;
        List<string> rest = [];
        for (int i = 0; i < args.Count; i++)
        {
            if (TakePackOption(args, ref i, run))
            {
                continue;
            }

            if (Take(args, i, "only", out string o))
            {
                only.AddRange(o.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                i++;
            }
            else if (Take(args, i, "level", out string l))
            {
                levelFile = l;
                i++;
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        // The operands are what the stock parser would take as a map path:
        // an argument that is not an option, and not the value of the
        // option before it. Each is tried alone against the stock options
        // gathered so far, which hold no map path, so the parser says which.
        List<string> stock = [];
        List<string> operands = [];
        foreach (string arg in rest)
        {
            if (!arg.StartsWith('-') && StockArgs.ParseVbsp([.. stock, arg]).MapPath == arg)
            {
                operands.Add(arg);
            }
            else
            {
                stock.Add(arg);
            }
        }

        const string Usage =
            "usage: ssmap roompack -out <pack.roompack> <key>=<library.vmf> [<key>=<library.vmf> ...] [-only <key>[,<key> ...]]"
            + " [-nav-turn0] [-nav-codec <none|deflate[:n]|brotli[:n]>] [-nolight | -vrad \"<stock vrad options>\" [-nodoorlight]]"
            + " [-incremental [-cache-dir <dir>] | -nocache] [stock vbsp options]\n"
            + "       ssmap roompack -level <level.yaml> [-out <pack.roompack>] [...]";
        if (run.UsageError is { } usage)
        {
            await output.WriteLineAsync(usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        if ((levelFile is null) == (operands.Count == 0) || (levelFile is null && run.Out is null))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // The libraries: from the operands, or from the level file.
        List<(string Key, string Path)> libraries = [];
        string? levelPath = levelFile is null ? null : Path.GetFullPath(levelFile);
        if (levelPath is not null)
        {
            if (!VPath.TryCreate(levelPath, out VPath levelVPath))
            {
                await output.WriteLineAsync($"{Verb}: \"{levelPath}\" is not a usable path").ConfigureAwait(false);
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
                await output.WriteLineAsync($"{Verb}: {levelPath}: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
            }

            string folder = Path.GetDirectoryName(levelPath)!;
            foreach (LevelLibrary library in level.Libraries ?? [new LevelLibrary(Path.GetFileNameWithoutExtension(level.Library), level.Library)])
            {
                string path;
                try
                {
                    path = Path.GetFullPath(Path.Combine(folder, library.Path));
                }
                catch (ArgumentException)
                {
                    await output.WriteLineAsync($"{Verb}: {levelPath}: library {library.Key}, \"{library.Path}\", is not a usable path").ConfigureAwait(false);
                    return ExitFailed;
                }

                if (level.Libraries is null && LevelLibraries.KeyProblem(library.Key) is not null)
                {
                    await output.WriteLineAsync($"{Verb}: {library.Path} gives no key ({library.Key} is not a key); write key={library.Path}.").ConfigureAwait(false);
                    return Program.ExitUsage;
                }

                libraries.Add((library.Key, path));
            }
        }
        else
        {
            foreach (string operand in operands)
            {
                int equals = operand.IndexOf('=', StringComparison.Ordinal);
                string key;
                string path;
                if (equals > 0 && LevelLibraries.KeyProblem(operand[..equals]) is null)
                {
                    key = operand[..equals];
                    path = operand[(equals + 1)..];
                }
                else
                {
                    path = operand;
                    key = Path.GetFileNameWithoutExtension(operand);
                    if (LevelLibraries.KeyProblem(key) is not null)
                    {
                        await output.WriteLineAsync($"{Verb}: {operand} gives no key ({key} is not a key); write key={operand}.").ConfigureAwait(false);
                        return Program.ExitUsage;
                    }
                }

                libraries.Add((key, Path.GetFullPath(path)));
            }
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, _) in libraries)
        {
            if (!seen.Add(key))
            {
                await output.WriteLineAsync($"{Verb}: the key {key} is given twice.").ConfigureAwait(false);
                return Program.ExitUsage;
            }
        }

        foreach (string key in only)
        {
            if (!libraries.Any(l => l.Key == key))
            {
                await output.WriteLineAsync($"{Verb}: -only names {key}, which is not one of {And([.. libraries.Select(l => l.Key)])}.").ConfigureAwait(false);
                return Program.ExitUsage;
            }
        }

        (bool vradOk, VradOptions? vrad) = await ParseVradAsync(run, output).ConfigureAwait(false);
        if (!vradOk)
        {
            return Program.ExitUsage;
        }

        // The stock line with the first library as its map, as ssmap room
        // parses it: the game rule and the pack id's options read it so.
        StockArgsResult<VbspOptions> parsed = StockArgs.ParseVbsp([.. stock, libraries[0].Path]);
        foreach (CompileDiagnostic diagnostic in parsed.Diagnostics)
        {
            await output.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}").ConfigureAwait(false);
        }

        if (parsed.HasErrors)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string target = run.Out ?? Path.ChangeExtension(levelPath!, RoomPack.Extension);
        if (!TryHostPath(target, out VPath packPath))
        {
            await output.WriteLineAsync($"{Verb}: -out \"{run.Out}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        List<PackInput> inputs = [];
        foreach ((string key, string path) in libraries)
        {
            if (!VPath.TryCreate(path, out VPath vpath))
            {
                await output.WriteLineAsync($"{Verb}: \"{path}\" is not a usable path").ConfigureAwait(false);
                return Program.ExitUsage;
            }

            inputs.Add(new PackInput(key, path, vpath));
        }

        return await PackLibrariesAsync(
            disk, searchRoots, run, ["roompack", .. args], inputs, only, Path.GetFullPath(target), packPath, vrad, parsed, stock, openCache, output, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>One library of a pack with namespaces, as the command line gave it.</summary>
    /// <param name="Key">Its namespace.</param>
    /// <param name="Path">Its VMF, as the host resolves it.</param>
    /// <param name="VPath">The same, on the verb's file system.</param>
    private sealed record PackInput(string Key, string Path, VPath VPath);

    /// <summary>
    /// The options <c>ssmap room</c> and <c>ssmap roompack</c> share, taken
    /// out of the line before the stock parser sees it.
    /// </summary>
    /// <param name="verb">The verb, for messages.</param>
    private sealed class PackArgs(string verb)
    {
        public string Verb { get; } = verb;

        public string? Out { get; set; }

        public string? CacheDirectory { get; set; }

        public bool Incremental { get; set; }

        public bool NoCache { get; set; }

        public bool Light { get; set; } = true;

        public bool DoorLight { get; set; } = true;

        public string? VradLine { get; set; }

        public RoomNavPackOptions Nav { get; set; } = new();

        /// <summary>The first option that could not be read, as the line to print; the verb stops with a usage error.</summary>
        public string? UsageError { get; set; }
    }

    /// <summary>Takes one of the shared options at <paramref name="i"/>, moving past its value.</summary>
    /// <returns>Whether the argument was one.</returns>
    private static bool TakePackOption(IReadOnlyList<string> args, ref int i, PackArgs run)
    {
        if (Take(args, i, "out", out string o))
        {
            run.Out = o;
            i++;
        }
        else if (Take(args, i, "cache-dir", out string c))
        {
            run.CacheDirectory = c;
            i++;
        }
        else if (IsFlag(args[i], "incremental"))
        {
            run.Incremental = true;
        }
        else if (IsFlag(args[i], "nocache"))
        {
            run.NoCache = true;
        }
        else if (Take(args, i, "nav-codec", out string codec))
        {
            if (NavCompression.TryParse(codec, out NavCompression compression))
            {
                run.Nav = run.Nav with { Compression = compression };
            }
            else
            {
                run.UsageError ??= $"{run.Verb}: -nav-codec \"{codec}\" is not none, deflate[:0-9] or brotli[:0-11]";
            }

            i++;
        }
        else if (IsFlag(args[i], "nav-turn0"))
        {
            run.Nav = run.Nav with { StoreAllTurns = false };
        }
        else if (IsFlag(args[i], "nolight"))
        {
            run.Light = false;
        }
        else if (IsFlag(args[i], "nodoorlight"))
        {
            run.DoorLight = false;
        }
        else if (Take(args, i, "vrad", out string vrad))
        {
            run.VradLine = vrad;
            i++;
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The base bake's vrad switches (<c>-vrad "&lt;stock vrad options&gt;"</c>),
    /// parsed as <c>ssmap vrad</c> parses its own line; stock's defaults
    /// without; none with <c>-nolight</c>. False (with the message printed)
    /// for a line the rooms cannot be lit with, or a switch <c>-nolight</c>
    /// contradicts.
    /// </summary>
    private static async Task<(bool Ok, VradOptions? Options)> ParseVradAsync(PackArgs run, TextWriter output)
    {
        if (run.Light)
        {
            StockArgsResult<VradOptions> vradParsed = StockArgs.ParseVrad(
                [.. (run.VradLine ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries), "room"]);
            foreach (CompileDiagnostic diagnostic in vradParsed.Diagnostics)
            {
                await output.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}").ConfigureAwait(false);
            }

            if (vradParsed.HasErrors || vradParsed.Options.LuxelDensity < 1.0f)
            {
                await output.WriteLineAsync(
                    $"{run.Verb}: -vrad \"{run.VradLine}\" is not a vrad line a room can be lit with (-luxeldensity below 1 changes the room's geometry)")
                    .ConfigureAwait(false);
                return (false, null);
            }

            return (true, vradParsed.Options);
        }

        if (run.VradLine is not null)
        {
            await output.WriteLineAsync($"{run.Verb}: -vrad sets how the rooms are lit, and -nolight lights none").ConfigureAwait(false);
            return (false, null);
        }

        if (!run.DoorLight)
        {
            await output.WriteLineAsync($"{run.Verb}: -nodoorlight leaves out lit rooms' door light, and -nolight lights none").ConfigureAwait(false);
            return (false, null);
        }

        return (true, null);
    }

    /// <summary>
    /// The pack with namespaces: every library split and checked, the
    /// namespaces <c>-only</c> leaves out copied from the existing pack, the
    /// rest compiled in one run, and the pack written.
    /// </summary>
    private static async Task<int> PackLibrariesAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        PackArgs run,
        IReadOnlyList<string> relaunch,
        IReadOnlyList<PackInput> inputs,
        IReadOnlyList<string> only,
        string packHost,
        VPath packPath,
        VradOptions? vradOptions,
        StockArgsResult<VbspOptions> parsed,
        List<string> stock,
        Func<string, CancellationToken, Task<ICacheStore?>> openCache,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string verb = run.Verb;
        string pack = HostPaths.Display(packPath);
        string packFolder = Path.GetDirectoryName(packHost)!;

        // Every library read and hashed first.
        List<RoomPackSource> sources = [];
        foreach (PackInput input in inputs)
        {
            try
            {
                byte[] bytes = await ReadBytesAsync(disk, input.VPath, cancellationToken).ConfigureAwait(false);
                VmfDocument vmf = await VmfDocument.ParseAsync(bytes, cancellationToken).ConfigureAwait(false);
                string source = Path.GetRelativePath(packFolder, input.Path).Replace('\\', '/');
                sources.Add(new RoomPackSource(input.Key, source, vmf, RoomPackNamespaces.Digest(bytes)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ChunkFileException)
            {
                await output.WriteLineAsync($"{verb}: {input.Path}: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
            }
        }

        // -only: the existing pack's namespaces, each one kept checked against
        // its VMF now, and later against the level's singletons.
        IReadOnlyList<RoomPackNamespace>? previous = null;
        Dictionary<string, IReadOnlyList<RoomPackItem>> copied = new(StringComparer.Ordinal);
        if (only.Count > 0)
        {
            try
            {
                if (!await disk.ExistsAsync(packPath, cancellationToken).ConfigureAwait(false))
                {
                    await output.WriteLineAsync($"{verb}: -only copies the other libraries from {pack}, and there is none; build it once without -only.")
                        .ConfigureAwait(false);
                    return ExitFailed;
                }

                await using Stream stream = await disk.OpenReadAsync(packPath, cancellationToken).ConfigureAwait(false);
                RoomPackIndex index = await RoomPack.ReadIndexAsync(stream, cancellationToken).ConfigureAwait(false);
                previous = await RoomPack.ReadNamespacesAsync(stream, index, cancellationToken).ConfigureAwait(false) ?? [];
                foreach (RoomPackSource source in sources.Where(s => !only.Contains(s.Key)))
                {
                    if (RoomPackNamespaces.Find(previous, source.Key) is not { } space)
                    {
                        await output.WriteLineAsync($"{verb}: {pack} holds no library {source.Key}; rebuild it too, or leave out -only.").ConfigureAwait(false);
                        return ExitFailed;
                    }

                    if (space.VmfSha256 != source.VmfSha256)
                    {
                        await output.WriteLineAsync($"{verb}: library {source.Key} changed since {pack} was built (its VMF); rebuild it too, or leave out -only.")
                            .ConfigureAwait(false);
                        return ExitFailed;
                    }

                    copied[source.Key] = await RoomPack.ReadItemsAsync(stream, index, space.FirstRoom, space.RoomCount, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await output.WriteLineAsync($"{verb}: cannot read the room pack {pack}: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
            }
            catch (LinkException exception)
            {
                await output.WriteLineAsync($"{verb}: {pack}: {exception.Message}").ConfigureAwait(false);
                return ExitFailed;
            }
        }

        // Every library split, held to the others, and the singleton rule
        // applied: before a game is mounted, as ssmap room refuses a library.
        RoomPackPlan plan;
        NavSettings? navSettings;
        try
        {
            plan = RoomPackCombiner.Plan(sources);
            navSettings = RoomPackCombiner.NavOf(sources);
            if (navSettings is not null && plan.Spaces[0].Rooms.Count > 0)
            {
                _ = navSettings.CellVoxels(plan.Spaces[0].Rooms[0].Definition.CellSize);
            }
        }
        catch (RoomPackSplitException exception)
        {
            await output.WriteLineAsync($"{verb}: {inputs[exception.Library].Path}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (Exception exception) when (exception is LinkException or RoomLibraryException)
        {
            await output.WriteLineAsync($"{verb}: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        foreach (RoomPackSpace space in plan.Spaces.Where(s => copied.ContainsKey(s.Key)))
        {
            if (RoomPackNamespaces.Find(previous!, space.Key)!.SingletonsSha256 != plan.SingletonsSha256)
            {
                await output.WriteLineAsync(
                    $"{verb}: library {space.Key} changed since {pack} was built (the level's singletons); rebuild it too, or leave out -only.")
                    .ConfigureAwait(false);
                return ExitFailed;
            }
        }

        foreach (string warning in plan.Warnings)
        {
            await output.WriteLineAsync($"{verb}: warning: {warning}").ConfigureAwait(false);
        }

        foreach (RoomPackSpace space in plan.Spaces.Where(s => copied.ContainsKey(s.Key)))
        {
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture, $"{verb}: library {space.Key}: copied {copied[space.Key].Count} room(s) from {pack}")).ConfigureAwait(false);
        }

        // Lit under the level's sun (the first library's, or the earliest
        // library's with one, D26 and D29); the id over every library.
        RoomLightingSettings? lighting = vradOptions is null
            ? null
            : new RoomLightingSettings(vradOptions with { Compliance = parsed.Options.Compliance })
            {
                Sun = RoomLightingSettings.SunOf(plan.LibraryEntities),
                DoorLight = run.DoorLight,
            };
        Guid packId = RoomCompileIds.CombinedPackId(
            [.. sources.Select(s => (s.Key, s.VmfSha256))],
            [.. PackIdOptions(stock, null), .. (lighting is null ? Array.Empty<string>() : [lighting.Describe()])],
            Describe(navSettings, run.Nav));

        List<RoomPackSpace> built = [.. plan.Spaces.Where(s => !copied.ContainsKey(s.Key))];
        List<LibraryRoom> rooms = [.. built.SelectMany(s => s.Rooms)];
        int total = rooms.Count + copied.Values.Sum(c => c.Count);
        string gameDirectory = parsed.GameDirectory is null
            ? Path.GetDirectoryName(Path.GetDirectoryName(inputs[0].Path)!)!
            : Path.GetFullPath(parsed.GameDirectory);

        (IReadOnlyList<RoomPackSectionData>, IReadOnlyList<RoomPackItem>) Assemble(IReadOnlyList<RoomPackItem> packed)
        {
            // Grouped by namespace, in library order: a compiled room's item
            // is named key.room, so its namespace is the part before the dot.
            List<RoomPackItem> items = [];
            List<RoomPackNamespace> namespaces = [];
            foreach (RoomPackSpace space in plan.Spaces)
            {
                string prefix = space.Key + LevelLibraries.Separator;
                IReadOnlyList<RoomPackItem> own = copied.TryGetValue(space.Key, out IReadOnlyList<RoomPackItem>? copy)
                    ? copy
                    : [.. packed.Where(p => p.Name.StartsWith(prefix, StringComparison.Ordinal))];
                namespaces.Add(new RoomPackNamespace(space.Key, space.Source, space.VmfSha256, plan.SingletonsSha256, items.Count, own.Count)
                {
                    NameKeys = space.NameKeys,
                });
                items.AddRange(own);
            }

            List<RoomPackSectionData> sections = [RoomCompileIds.Section(packId)];
            if (plan.LibraryEntities.Count > 0)
            {
                sections.Add(RoomLibraryEntities.ToSection(plan.LibraryEntities));
            }

            if (plan.Options.ToSection() is { } optionsSection)
            {
                sections.Add(optionsSection);
            }

            if (plan.SkyboxRoom is not null)
            {
                sections.Add(RoomLibrarySkybox.ToSection(plan.SkyboxRoom));
            }

            sections.Add(RoomPackNamespaces.ToSection(namespaces));
            return (sections, items);
        }

        return await CompileAndWriteAsync(
            disk,
            searchRoots,
            run,
            relaunch,
            parsed,
            gameDirectory,
            HostBackends.CachePathFor(run.CacheDirectory, packFolder, Path.GetFileNameWithoutExtension(packPath.FileName)),
            rooms,
            total,
            navSettings,
            null,
            lighting,
            Assemble,
            packPath,
            openCache,
            output,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What <c>ssmap room</c> and <c>ssmap roompack</c> do once the rooms are
    /// known: mount the game, open the cooker and the cache, build every room
    /// (compiled or reused) with one line each in library order, write the
    /// pack in one replace, and commit the cache.
    /// </summary>
    /// <param name="disk">Where the game and the pack are.</param>
    /// <param name="searchRoots">Where game installs are.</param>
    /// <param name="run">The shared options.</param>
    /// <param name="relaunch">The verb's own arguments, for a cooker that must relaunch the process.</param>
    /// <param name="parsed">The stock line.</param>
    /// <param name="gameDirectory">The game to mount.</param>
    /// <param name="storePath">Where <c>-incremental</c> opens the store.</param>
    /// <param name="rooms">The rooms to build, in pack order.</param>
    /// <param name="total">How many rooms the pack is to hold, those built and any copied: the log's count.</param>
    /// <param name="navSettings">The navigation every room is built with, or null.</param>
    /// <param name="nameKeys">The run's name keys (a room of a namespace carries its own).</param>
    /// <param name="lighting">How the rooms are lit, or null.</param>
    /// <param name="assemble">The pack's library sections and rooms, from the items built, in order.</param>
    /// <param name="packPath">Where the pack goes.</param>
    /// <param name="openCache">Opens the store.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    private static async Task<int> CompileAndWriteAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        PackArgs run,
        IReadOnlyList<string> relaunch,
        StockArgsResult<VbspOptions> parsed,
        string gameDirectory,
        string storePath,
        IReadOnlyList<LibraryRoom> rooms,
        int total,
        NavSettings? navSettings,
        IReadOnlySet<string>? nameKeys,
        RoomLightingSettings? lighting,
        Func<IReadOnlyList<RoomPackItem>, (IReadOnlyList<RoomPackSectionData> Library, IReadOnlyList<RoomPackItem> Rooms)> assemble,
        VPath packPath,
        Func<string, CancellationToken, Task<ICacheStore?>> openCache,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string verb = run.Verb;
        ISteamAppLocator? steam = VbspHost.SteamFor(disk, searchRoots);
        GameContentMounter.Result mounted;
        try
        {
            mounted = await VbspCommand
                .MountGameAsync(disk, gameDirectory, steam, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            await output.WriteLineAsync($"{verb}: cannot mount {gameDirectory}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        await VbspCommand.WriteSkippedAsync(mounted, verb, output).ConfigureAwait(false);

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
            disk, searchRoots, options, relaunch, verb, output, cancellationToken)
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
            NameKeys = nameKeys,
            Lighting = lighting,
            Parallelism = parsed.Threads is int degree && degree > 0
                ? new CompileParallelism { MaxDegree = degree }
                : CompileParallelism.Default,
        };

        // -incremental: the store beside the library (or in -cache-dir),
        // opened only when asked. A store that will not open is said out
        // loud and the run compiles everything, as ssmap all does.
        ICacheStore? store = null;
        if (run.Incremental && !run.NoCache)
        {
            store = await openCache(storePath, cancellationToken).ConfigureAwait(false);
            if (store is null)
            {
                await output.WriteLineAsync(
                    $"{verb}: cache: -incremental opened no store ("
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
                    PackOptions = run.Nav,
                    NameKeys = nameKeys,
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
                    $"{verb}: {(outcome.Reused ? "reused" : "compiled")} {definition.Name}"
                    + $" ({outcome.ClusterCount} clusters, {definition.Sockets.Count} sockets)")
                    .ConfigureAwait(false);

                // What the naming rule warned of (a misplaced placeholder, a
                // local name nothing defines): the room compiles, but the
                // author should look.
                foreach (string warning in outcome.NameWarnings)
                {
                    await output.WriteLineAsync($"{verb}: warning: {warning}").ConfigureAwait(false);
                }

                // What the navigation could not read (a prop whose model the
                // content lacks): the room compiles without that obstacle. A
                // reused room replays the list its compile stored, so the log
                // is the clean run's whichever rooms came from the cache.
                foreach (string warning in outcome.NavWarnings)
                {
                    await output.WriteLineAsync($"{verb}: warning: room \"{definition.Name}\": {warning}").ConfigureAwait(false);
                }

                // A room with no floor on the level map (a player cannot
                // stand in it), read from its map section, so a reused room
                // says it too.
                foreach (string warning in outcome.MapWarnings)
                {
                    await output.WriteLineAsync($"{verb}: warning: {warning}").ConfigureAwait(false);
                }

                return;
            }

            failed++;
            await output.WriteLineAsync(outcome.Error is RoomLintException
                ? $"{verb}: room \"{definition.Name}\" is not linkable: {outcome.Error.Message}"
                : $"{verb}: room \"{definition.Name}\": {outcome.Error!.Message}")
                .ConfigureAwait(false);
        }

        await RoomLibraryBuild.BuildAsync(rooms, settings, run.Nav, cache, ReportAsync, cancellationToken).ConfigureAwait(false);

        (IReadOnlyList<RoomPackSectionData> librarySections, IReadOnlyList<RoomPackItem> packRooms) = assemble(packed);
        try
        {
            await disk.ReplaceAsync(
                packPath,
                async (stream, token) => await RoomPack.SaveAsync(librarySections, packRooms, stream, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"{verb}: cannot write {HostPaths.Display(packPath)}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        await output.WriteLineAsync(
            $"{verb}: wrote {HostPaths.Display(packPath)} ({packRooms.Count} of {total} room(s))")
            .ConfigureAwait(false);

        if (run.Incremental)
        {
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture, $"{verb}: {packed.Count - reused} compiled, {reused} reused")).ConfigureAwait(false);
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
                    await output.WriteLineAsync($"{verb}: cache: gc failed ({why}); the store was left as it was")
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await output.WriteLineAsync($"{verb}: cache: commit failed ({exception.Message}); this run's rooms were not stored")
                    .ConfigureAwait(false);
            }
        }

        if (failed > 0)
        {
            await output.WriteLineAsync($"{verb}: {failed} of {total} room(s) failed").ConfigureAwait(false);
            return ExitFailed;
        }

        return Program.ExitSuccess;
    }
}
