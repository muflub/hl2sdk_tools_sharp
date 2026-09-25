namespace SourceSharp.MapTools.Io;

/// <summary>
/// Where a <c>gameinfo.txt</c>'s relative locations are rooted.
/// </summary>
/// <param name="GameInfoDirectory">
/// The directory holding <c>gameinfo.txt</c>. What <c>|gameinfo_path|</c>
/// stands for.
/// </param>
/// <param name="BaseDirectory">
/// The install directory — where <c>hl2.exe</c> is. A location with no
/// <c>|…|</c> token is relative to this, which is what the file's own comment
/// says.
/// </param>
/// <param name="AllSourceEnginePaths">
/// What <c>|all_source_engine_paths|</c> stands for; the same as
/// <paramref name="BaseDirectory"/> unless a launcher said otherwise.
/// </param>
public readonly record struct GameContentRoots(
    VPath GameInfoDirectory,
    VPath BaseDirectory,
    VPath? AllSourceEnginePaths = null)
{
    /// <summary>
    /// The install directory, or <see cref="AllSourceEnginePaths"/> when one
    /// was given.
    /// </summary>
    public VPath SharedContentDirectory => AllSourceEnginePaths ?? BaseDirectory;

    /// <summary>
    /// Finds another Steam app's install, for <c>|appid_N|</c> locations; null
    /// when the gameinfo is not expected to use any.
    /// </summary>
    public ISteamAppLocator? Steam { get; init; }
}

/// <summary>
/// Turns a <c>gameinfo.txt</c> into a mounted <see cref="ContentFileSystem"/>.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="GameInfo"/> — which only says what the file says —
/// because mounting is where the decisions are: which tags to mount, what a
/// <c>/*</c> line expands to, and what to do about an entry that is not there.
/// Those are policy, and a parser that made them would be untestable without a
/// disk.
/// </para>
/// <para>
/// A location that does not exist is SKIPPED rather than refused, matching the
/// engine: real <c>gameinfo.txt</c> files list content the install may not have
/// (a <c>_english</c> archive, a <c>download</c> directory nobody has written
/// to yet), and refusing would make the common install unmountable. Every skip
/// is reported, so a compile that cannot find its materials can say which
/// search paths were not there rather than leaving the user to guess.
/// </para>
/// </remarks>
public static class GameContentMounter
{
    /// <summary>The tags a map compile mounts by default.</summary>
    /// <remarks>
    /// <c>game</c> and <c>mod</c>: the content a map is built from.
    /// Deliberately not <c>gamebin</c> (binaries), <c>download</c> (files from
    /// servers, which the engine searches last precisely so they do not
    /// override real content) or <c>platform</c> (the UI's own files).
    /// </remarks>
    public static IReadOnlyList<string> CompileKinds { get; } = ["game", "mod"];

    /// <summary>What mounting a <c>gameinfo.txt</c> produced.</summary>
    /// <param name="Content">The mounted content, first match wins.</param>
    /// <param name="Skipped">
    /// The search-path locations that were not there, in file order.
    /// </param>
    /// <param name="GameInfo">
    /// The parsed <c>gameinfo.txt</c> the mount came from, or null for a
    /// caller that mounted bare content. The format-resolution host reads
    /// <c>SteamAppId</c> and the <see cref="GameInfo.ToolArguments"/> off
    /// exactly this — the file the content was MOUNTED from, never a re-read
    /// of whatever happens to be on disk.
    /// </param>
    public readonly record struct Result(
        ContentFileSystem Content,
        IReadOnlyList<string> Skipped,
        GameInfo? GameInfo = null);

    /// <summary>Mounts a game's content in its search-path order.</summary>
    /// <param name="fileSystem">Where the content lives.</param>
    /// <param name="gameInfo">The parsed <c>gameinfo.txt</c>.</param>
    /// <param name="roots">Where its relative locations are rooted.</param>
    /// <param name="kinds">
    /// Which tags to mount; <see cref="CompileKinds"/> when null.
    /// </param>
    /// <param name="cancellationToken">Cancels mounting.</param>
    /// <returns>The mounted content, and what was skipped.</returns>
    public static async ValueTask<Result> MountAsync(
        IFileSystem fileSystem,
        GameInfo gameInfo,
        GameContentRoots roots,
        IReadOnlyList<string>? kinds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(gameInfo);

        IReadOnlyList<string> wanted = kinds ?? CompileKinds;
        List<IContentMount> mounts = [];
        List<string> skipped = [];

        try
        {
            // Portal 2's sibling content, the way ++ mounts it during
            // FileSystem setup (dumps/vbsp.all.c:68091-68108): the caller of
            // MountPortal2ContentAsync fires once appid==620 — UNCONDITIONALLY
            // on the appid, before and independently of the gameinfo
            // SearchPaths walk at 68100+, and independently of any preset
            // flag (the report's "preset-active" phrasing observed that the
            // 620 preset always happens to apply, not that the mount checks
            // it). Being added first, the update/dlc content OUTRANKS the
            // gameinfo's own paths where both carry a file — ++'s order too.
            // <gamedir>/update and every contiguous <gamedir>/portal2_dlcN
            // join the search; DLC numbering is contiguous-from-1 and capped:
            // the dump probes dlc1 upward while the directory exists, stops
            // at the first miss, never past 99, and mounts each found DLC's
            // pak01_dir.vpk BEFORE its directory, highest-numbered DLC first.
            if (gameInfo.SteamAppId == 620)
            {
                await MountPortal2ExtraAsync(
                    fileSystem,
                    roots,
                    mounts,
                    skipped,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (GameInfoSearchPath searchPath in gameInfo.SearchPaths)
            {
                if (!Wants(searchPath, wanted))
                {
                    continue;
                }

                string location;
                bool rooted;

                if (searchPath.TryGetAppId(out int appId, out string relative))
                {
                    VPath install = await FindAppAsync(roots, appId, searchPath.Location, cancellationToken)
                        .ConfigureAwait(false);
                    location = GameInfo.ExpandTokens(
                        IsAlreadyRooted(relative) ? relative : install.Value + "/" + relative,
                        roots.GameInfoDirectory.Value,
                        roots.SharedContentDirectory.Value);
                    rooted = true;
                }
                else
                {
                    location = GameInfo.ExpandTokens(
                        searchPath.Location,
                        roots.GameInfoDirectory.Value,
                        roots.SharedContentDirectory.Value);
                    rooted = IsAlreadyRooted(searchPath.Location);
                }

                await MountOneAsync(
                    fileSystem,
                    roots,
                    location,
                    searchPath.Location,
                    rooted,
                    mounts,
                    skipped,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (IContentMount mount in mounts)
            {
                await mount.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return new Result(new ContentFileSystem(mounts), skipped, gameInfo);
    }

    /// <summary>Reads a <c>gameinfo.txt</c> and mounts what it names.</summary>
    /// <param name="fileSystem">Where the file and the content live.</param>
    /// <param name="gameInfoPath">The <c>gameinfo.txt</c> to read.</param>
    /// <param name="baseDirectory">
    /// The install directory — where <c>hl2.exe</c> is — which un-tokenised
    /// locations are relative to.
    /// </param>
    /// <param name="kinds">Which tags to mount; <see cref="CompileKinds"/> when null.</param>
    /// <param name="cancellationToken">Cancels reading and mounting.</param>
    /// <returns>The mounted content, and what was skipped.</returns>
    public static async ValueTask<Result> MountAsync(
        IFileSystem fileSystem,
        VPath gameInfoPath,
        VPath baseDirectory,
        IReadOnlyList<string>? kinds = null,
        CancellationToken cancellationToken = default)
    {
        GameInfo gameInfo = await GameInfo.LoadAsync(fileSystem, gameInfoPath, cancellationToken)
            .ConfigureAwait(false);

        return await MountAsync(
            fileSystem,
            gameInfo,
            new GameContentRoots(gameInfoPath.Directory, baseDirectory),
            kinds,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The install directory an <c>|appid_N|</c> location is relative to.
    /// </summary>
    /// <remarks>
    /// An app that is not installed is REFUSED, not skipped: the engine stops
    /// with "This mod requires %s (%d) to be installed"
    /// (<c>public/filesystem_init.cpp:711-719</c>), because a mod that names an
    /// app's content cannot be built or run without it. Skipping it would
    /// compile every material from that app as missing, with exit code 0.
    /// </remarks>
    private static async ValueTask<VPath> FindAppAsync(
        GameContentRoots roots,
        int appId,
        string location,
        CancellationToken cancellationToken)
    {
        if (roots.Steam is null)
        {
            throw new InvalidOperationException(
                $"\"{location}\" mounts Steam app {appId}'s content, and no ISteamAppLocator was given "
                + "(GameContentRoots.Steam) to find where it is installed");
        }

        VPath? install = await roots.Steam.FindInstallDirectoryAsync(appId, cancellationToken)
            .ConfigureAwait(false);

        return install
            ?? throw new DirectoryNotFoundException(
                $"This mod requires Steam app {appId} to be installed (\"{location}\"); "
                + "no Steam library lists it");
    }

    /// <summary>
    /// Mounts Portal 2's sibling content: <c>&lt;gamedir&gt;/update</c> (with
    /// its <c>pak01_dir.vpk</c> ahead of the directory, both only when the
    /// update directory exists at all) and every contiguous
    /// <c>&lt;gamedir&gt;/portal2_dlcN</c>, highest first, each DLC's
    /// <c>pak01_dir.vpk</c> before its directory.
    /// </summary>
    /// <remarks>
    /// Faithful to the dump's loops (<c>MountPortal2ContentAsync</c>,
    /// <c>dumps/vbsp.all.c</c> around <c>FUN_14004ba10</c>/<c>Local_388</c>):
    /// probe <c>dlc1</c>, <c>dlc2</c>, … while the directory exists, stop at
    /// the first miss, cap at 99, then walk back down from the highest found
    /// mounting vpk-then-directory. The paths are relative to the
    /// <c>gameinfo.txt</c> directory, not the install (the dump canonicalises
    /// the gameinfo path and appends <c>/update</c>, <c>/portal2_dlcN</c> to
    /// it), so everything goes through <see cref="MountOneAsync"/> rooted at
    /// <see cref="GameContentRoots.GameInfoDirectory"/>.
    /// </remarks>
    private static async ValueTask MountPortal2ExtraAsync(
        IFileSystem fileSystem,
        GameContentRoots roots,
        List<IContentMount> mounts,
        List<string> skipped,
        CancellationToken cancellationToken)
    {
        string gameDir = roots.GameInfoDirectory.Value;

        // The update dir gates its own pair: no directory, no update mounts.
        // The probe is an enumerate, not ExistsAsync: this file system surface
        // answers ExistsAsync only for FILES (PhysicalFileSystem.cs:267-271 is
        // File.Exists), so asking it about a directory answers false and the
        // whole block would be unreachable. ++ probes with FindFirstFile on
        // the directory name (dumps/vbsp.all.c, MountPortal2ContentAsync),
        // which is what DirectoryExistsAsync below reproduces.
        if (await DirectoryExistsAsync(fileSystem, Root(roots, gameDir + "/update", true), cancellationToken)
            .ConfigureAwait(false))
        {
            await MountOneAsync(
                fileSystem, roots, gameDir + "/update/pak01_dir.vpk",
                "update/pak01_dir.vpk", true, mounts, skipped, cancellationToken)
                .ConfigureAwait(false);
            await MountOneAsync(
                fileSystem, roots, gameDir + "/update",
                "update", true, mounts, skipped, cancellationToken)
                .ConfigureAwait(false);
        }

        // Contiguous from dlc1, capped at 99 like the dump's loop counter.
        int highest = 0;
        for (int n = 1; n <= 99; n++)
        {
            VPath probe = Root(roots, $"{gameDir}/portal2_dlc{n}", true);
            if (!await DirectoryExistsAsync(fileSystem, probe, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            highest = n;
        }

        // Highest first: dlcN's pak01_dir.vpk, then dlcN itself.
        for (int n = highest; n >= 1; n--)
        {
            await MountOneAsync(
                fileSystem, roots, $"{gameDir}/portal2_dlc{n}/pak01_dir.vpk",
                $"portal2_dlc{n}/pak01_dir.vpk", true, mounts, skipped, cancellationToken)
                .ConfigureAwait(false);
            await MountOneAsync(
                fileSystem, roots, $"{gameDir}/portal2_dlc{n}",
                $"portal2_dlc{n}", true, mounts, skipped, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a directory is there: <see cref="IFileSystem.EnumerateAsync"/>
    /// yields from it at all.
    /// </summary>
    /// <remarks>
    /// There is no directory-exists on <see cref="IFileSystem"/>, and
    /// <see cref="IFileSystem.ExistsAsync"/> is file-only in both implementations
    /// (<c>PhysicalFileSystem.cs:267-271</c> is <c>File.Exists</c>;
    /// <see cref="InMemoryFileSystem"/> has no directories at all — one exists
    /// exactly when a file is under it). Enumerating is the question that means
    /// "the directory is there" on both, and it is what
    /// <see cref="MountWildcardAsync"/> already relies on for the same reason.
    /// Recursive, so a directory holding only subdirectories still counts as
    /// present, the way the dump's directory probe answers for it.
    /// </remarks>
    private static async ValueTask<bool> DirectoryExistsAsync(
        IFileSystem fileSystem,
        VPath directory,
        CancellationToken cancellationToken)
    {
        await foreach (VPath unused in fileSystem
            .EnumerateAsync(directory, GlobMatcher.MatchAll, recursive: true, cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

    private static bool Wants(GameInfoSearchPath searchPath, IReadOnlyList<string> kinds)
    {
        foreach (string kind in kinds)
        {
            if (searchPath.HasKind(kind))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a location, AS WRITTEN, already names a place rather than
    /// something under the install directory.
    /// </summary>
    /// <remarks>
    /// Decided before expansion, not after: <c>|gameinfo_path|</c> expands to a
    /// VPath, which has no leading slash because nothing in this library's path
    /// world does, so after expansion an absolute location and a relative one
    /// are indistinguishable. The token IS the marker.
    /// </remarks>
    private static bool IsAlreadyRooted(string location) =>
        location.Contains('|', StringComparison.Ordinal)
        || location.StartsWith('/')
        || (location.Length >= 2 && location[1] == ':' && char.IsAsciiLetter(location[0]));

    private static async ValueTask MountOneAsync(
        IFileSystem fileSystem,
        GameContentRoots roots,
        string location,
        string reported,
        bool rooted,
        List<IContentMount> mounts,
        List<string> skipped,
        CancellationToken cancellationToken)
    {
        if (location.EndsWith("/*", StringComparison.Ordinal))
        {
            await MountWildcardAsync(
                fileSystem,
                roots,
                location[..^2],
                reported,
                rooted,
                mounts,
                skipped,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        VPath path = Root(roots, location, rooted);

        if (location.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            await MountVpkAsync(fileSystem, path, reported, mounts, skipped, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        DirectoryContentMount directory = await DirectoryContentMount
            .MountAsync(fileSystem, path, cancellationToken)
            .ConfigureAwait(false);

        if (directory.Paths.Count == 0)
        {
            skipped.Add(reported);
            await directory.DisposeAsync().ConfigureAwait(false);
            return;
        }

        mounts.Add(directory);
    }

    private static async ValueTask MountVpkAsync(
        IFileSystem fileSystem,
        VPath path,
        string reported,
        List<IContentMount> mounts,
        List<string> skipped,
        CancellationToken cancellationToken)
    {
        VpkArchiveOrMiss opened = await TryOpenVpkAsync(fileSystem, path, cancellationToken)
            .ConfigureAwait(false);

        if (opened.Archive is null)
        {
            skipped.Add(reported);
            return;
        }

        mounts.Add(ArchiveContentMount.Mount(opened.Archive));
    }

    private static async ValueTask MountWildcardAsync(
        IFileSystem fileSystem,
        GameContentRoots roots,
        string location,
        string reported,
        bool rooted,
        List<IContentMount> mounts,
        List<string> skipped,
        CancellationToken cancellationToken)
    {
        // "hl2mp/custom/*": every VPK and every subdirectory in there, in
        // alphabetical order, which is what the engine's own comment promises.
        VPath directory = Root(roots, location, rooted);

        SortedSet<string> archives = new(StringComparer.Ordinal);
        SortedSet<string> subdirectories = new(StringComparer.Ordinal);

        await foreach (VPath path in fileSystem
            .EnumerateAsync(directory, GlobMatcher.MatchAll, recursive: true, cancellationToken)
            .ConfigureAwait(false))
        {
            string relative = directory.IsEmpty ? path.Value : path.Value[(directory.Value.Length + 1)..];
            int slash = relative.IndexOf('/', StringComparison.Ordinal);

            if (slash >= 0)
            {
                // IFileSystem lists files, not directories, so a subdirectory
                // is inferred from the files in it. One that is empty is
                // invisible, which is harmless: it holds no content either.
                subdirectories.Add(relative[..slash]);
            }
            else if (relative.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
            {
                archives.Add(relative);
            }
        }

        if (archives.Count == 0 && subdirectories.Count == 0)
        {
            skipped.Add(reported);
            return;
        }

        foreach (string archive in archives)
        {
            // Only the _dir half of a multi-part archive is an archive to open;
            // the numbered parts are its data.
            if (IsNumberedArchivePart(archive))
            {
                continue;
            }

            await MountVpkAsync(
                fileSystem,
                directory.Combine(archive),
                reported,
                mounts,
                skipped,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (string subdirectory in subdirectories)
        {
            DirectoryContentMount mount = await DirectoryContentMount
                .MountAsync(fileSystem, directory.Combine(subdirectory), cancellationToken)
                .ConfigureAwait(false);

            mounts.Add(mount);
        }
    }

    private static bool IsNumberedArchivePart(string name)
    {
        // "<base>_000.vpk" and friends. "_dir.vpk" is the one to open.
        int dot = name.LastIndexOf('.');
        return dot >= 4
            && name[dot - 4] == '_'
            && char.IsAsciiDigit(name[dot - 3])
            && char.IsAsciiDigit(name[dot - 2])
            && char.IsAsciiDigit(name[dot - 1]);
    }

    private static async ValueTask<VpkArchiveOrMiss> TryOpenVpkAsync(
        IFileSystem fileSystem,
        VPath path,
        CancellationToken cancellationToken)
    {
        try
        {
            return new VpkArchiveOrMiss(
                await Vpk.VpkArchive.OpenAsync(fileSystem, path, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }
        catch (Vpk.InvalidVpkException)
        {
            // A corrupt archive is a SKIP with a report, not a failed mount.
            // The stock engine's path is the same shape: MountArchive ->
            // VPKFileOpen fails -> Warning + false, the mount continues
            // without it. This mounter already has the channel for it — the
            // skipped list, which the host prints. Failing the whole mount
            // over one unreadable sibling (or one corrupt custom/* pak) would
            // be louder than the tool the port models. A corrupt base pak
            // skips too, and the compile that follows fails loudly on its
            // missing materials — which is what ++'s silence also ends in.
            return default;
        }
    }

    private static VPath Root(GameContentRoots roots, string location, bool rooted)
    {
        VPath relative = VPath.Create(location.TrimEnd('/'));

        if (rooted)
        {
            return relative;
        }

        // The file's own comment: "Search paths are relative to the base
        // directory, which is where hl2.exe is found."
        return relative.IsEmpty
            ? roots.BaseDirectory
            : roots.BaseDirectory.IsEmpty ? relative : roots.BaseDirectory.Combine(relative.Value);
    }

    private readonly record struct VpkArchiveOrMiss(Vpk.VpkArchive? Archive);
}
