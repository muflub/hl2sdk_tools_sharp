//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text;

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapCompile;

/// <summary>
/// A file the compile was given from outside the game's search paths, which a
/// content bundle carries beside the game's own files.
/// </summary>
/// <param name="BundlePath">Where the file goes in the bundle.</param>
/// <param name="DiskPath">Where the file is on the host's disk.</param>
/// <param name="Mount">
/// Why the compile had it: <c>map</c> for the level's <c>.rad</c> beside the
/// map, <c>lights</c> for vrad's <c>-lights</c> file, <c>fallback</c> for a
/// <c>lights.rad</c> found beside the tool or in a Steam <c>bin</c> folder.
/// </param>
public readonly record struct LooseContentFile(string BundlePath, VPath DiskPath, string Mount);

/// <summary>What <see cref="ContentRecording.WriteAsync"/> put in a bundle.</summary>
/// <param name="Files">How many content files the bundle holds, not counting its <c>gameinfo.txt</c> and manifest.</param>
/// <param name="Bytes">Their total size, uncompressed.</param>
/// <param name="Misses">How many lookups found nothing; they are in the manifest only.</param>
public sealed record ContentBundleSummary(int Files, long Bytes, int Misses)
{
    /// <summary>The one line <c>ssmap all</c> prints once the bundle is written.</summary>
    /// <param name="zipPath">Where the bundle went.</param>
    /// <returns>The line.</returns>
    public string Line(string zipPath) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"record-content: {zipPath}: {Files} files, {Bytes} bytes, {Misses} misses");
}

/// <summary>
/// Records which game content a compile touched, and writes it out as a
/// self-contained game directory in a zip: <c>ssmap all --record-content</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A compile of a real map reads hundreds of files out of a
/// Steam install -- materials and their textures, static-prop models,
/// <c>lights.rad</c>, surface properties -- and reproducing that compile on a
/// machine without the game needs exactly those files and nothing else. The
/// set is not something a person can list by hand: vrad's static-prop
/// lighting reads the models' <c>.vvd</c>/<c>.vtx</c>, <c>-textureshadows</c>
/// reads their textures, a material's <c>include</c> pulls in another. The
/// content layer already knows, because every lookup goes through it.
/// </para>
/// <para>
/// <b>How.</b> The mounted game is wrapped in a
/// <see cref="RecordingContentFileSystem"/> and the wrapper, not the mount,
/// is what the compile is given (<see cref="Content"/>), so vbsp and vrad
/// both record into one <see cref="DependencyRecorder"/>. Afterwards
/// <see cref="WriteAsync"/> reads every recorded file again from the
/// UNWRAPPED mount (<see cref="Source"/>), checks a read file's bytes against
/// the hash the compile saw, and writes it at its content-relative path. The
/// result mounts through its own generated <c>gameinfo.txt</c>, so a replay is
/// <c>ssmap all &lt;map&gt; -game &lt;unzipped&gt;</c> with no Steam at all.
/// </para>
/// <para>
/// <b>What goes in.</b> Every file whose bytes were read, and every file that
/// was only resolved (a stage branched on its existence, so the replay must
/// see it exist). A miss has no bytes; it is listed in the manifest, which is
/// also where a reviewer learns which mount each file came from and which
/// command line produced the set.
/// </para>
/// <para>
/// <b>Flattening is exact.</b> Resolution is first-match-wins over ordered
/// mounts, and the recorder keeps the path each lookup RESOLVED to, which is
/// the winner. Putting every winner in one directory therefore reproduces
/// what the compile read: a shadowed copy in a later mount is simply not in
/// the set.
/// </para>
/// <para>
/// Nothing here is shared between runs: one instance per compile, and it
/// holds no handle -- the zip is written in one
/// <see cref="IFileSystem.ReplaceAsync"/>, a file at a time, so neither a
/// failure part way nor a large bundle leaves anything behind.
/// </para>
/// </remarks>
public sealed class ContentRecording
{
    /// <summary>The bundle's <c>gameinfo.txt</c>, at its root.</summary>
    public const string GameInfoName = "gameinfo.txt";

    /// <summary>The bundle's manifest, at its root.</summary>
    public const string ManifestName = "content-manifest.txt";

    /// <summary>Where files from outside the search paths go, unless they must be mountable.</summary>
    public const string LooseDirectory = "loose";

    /// <summary>
    /// The zip entries' timestamp. Fixed, so two bundles of the same set are
    /// the same bytes and a merge or a diff sees only real differences; the
    /// earliest time a zip can hold.
    /// </summary>
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IFileSystem _looseFiles;
    private readonly List<LooseContentFile> _loose = [];

    /// <summary>Starts recording a mounted game.</summary>
    /// <param name="source">The game content, unwrapped.</param>
    /// <param name="looseFiles">Where <see cref="AddLoose"/> files are read from.</param>
    public ContentRecording(IContentFileSystem source, IFileSystem looseFiles)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(looseFiles);

        Source = source;
        _looseFiles = looseFiles;
        Content = new RecordingContentFileSystem(source);
    }

    /// <summary>The unwrapped game content the bundle re-reads files from.</summary>
    public IContentFileSystem Source { get; }

    /// <summary>
    /// What the compile must be given in place of <see cref="Source"/>: every
    /// lookup through it is recorded.
    /// </summary>
    public RecordingContentFileSystem Content { get; }

    /// <summary>What the compile touched so far.</summary>
    public DependencyRecorder Recorder => Content.Recorder;

    /// <summary>The files added with <see cref="AddLoose"/>, in the order they were added.</summary>
    public IReadOnlyList<LooseContentFile> Loose => _loose;

    /// <summary>
    /// Adds a file the compile was given from outside the search paths; it is
    /// bundled when it exists on disk at write time.
    /// </summary>
    /// <param name="bundlePath">Where it goes in the bundle.</param>
    /// <param name="diskPath">Where it is.</param>
    /// <param name="mount">Why the compile had it (see <see cref="LooseContentFile.Mount"/>).</param>
    /// <remarks>
    /// These files are not recorded per read: they are served ahead of the
    /// game, by name, from a layer that sits above the recorder, and there are
    /// at most three of them. Carrying each one that exists costs nothing and
    /// cannot miss one a stage read.
    /// </remarks>
    public void AddLoose(string bundlePath, VPath diskPath, string mount)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundlePath);
        ArgumentException.ThrowIfNullOrEmpty(mount);
        _loose.Add(new LooseContentFile(bundlePath, diskPath, mount));
    }

    /// <summary>The bundle path of a loose file that is not meant to be mounted.</summary>
    /// <param name="diskPath">The file on disk.</param>
    /// <returns><c>loose/&lt;file name&gt;</c>.</returns>
    public static string LoosePath(string diskPath)
    {
        ArgumentNullException.ThrowIfNull(diskPath);
        return LooseDirectory + "/" + Path.GetFileName(diskPath.Replace('\\', '/'));
    }

    /// <summary>
    /// Writes the recorded set as a zip: the files, a <c>gameinfo.txt</c> that
    /// mounts them, and the manifest.
    /// </summary>
    /// <param name="target">Where the zip is written.</param>
    /// <param name="zipPath">The zip's path.</param>
    /// <param name="gameInfoText">
    /// The recorded game's own <c>gameinfo.txt</c>, whose search paths are
    /// replaced (<see cref="BundleGameInfo"/>); null writes a minimal one.
    /// </param>
    /// <param name="commandLine">The command line that ran the compile, for the manifest.</param>
    /// <param name="cancellationToken">Cancels the write; the zip then does not appear.</param>
    /// <returns>What went in.</returns>
    /// <exception cref="InvalidDataException">
    /// A file the compile read is gone or has different bytes now: the
    /// content changed under the compile, and a bundle of the new bytes would
    /// not reproduce it.
    /// </exception>
    public async Task<ContentBundleSummary> WriteAsync(
        IFileSystem target,
        VPath zipPath,
        string? gameInfoText,
        string commandLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(commandLine);

        // The plan first, with no bytes held: a real map's set is hundreds of
        // megabytes, so each file is read only when its entry is written.
        Dictionary<string, Planned> plan = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileDependency dependency in Recorder.Snapshot())
        {
            plan[dependency.Path.Value] = new Planned(dependency.Path.Value, dependency, null);
        }

        foreach (LooseContentFile loose in _loose)
        {
            if (!await _looseFiles.ExistsAsync(loose.DiskPath, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // A loose file beats only a miss at its path: that is the
            // lights.rad the game lacked, which the bundle then carries where
            // the replay's search paths find it. A game file that was found
            // is what the compile used, so it keeps its place.
            if (plan.TryGetValue(loose.BundlePath, out Planned existing)
                && existing.Dependency is { Kind: not DependencyKind.Missing })
            {
                continue;
            }

            plan[loose.BundlePath] = new Planned(loose.BundlePath, null, loose);
        }

        List<Planned> ordered = [.. plan.Values];
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.BundlePath, b.BundlePath));

        int files = 0;
        long bytes = 0;
        int misses = 0;
        string gameInfo = BundleGameInfo(gameInfoText);

        await target.ReplaceAsync(
            zipPath,
            async (stream, token) =>
            {
                StringBuilder manifest = new();
                manifest.Append("# ssmap content bundle\n");
                manifest.Append("# command: ").Append(commandLine).Append('\n');
                manifest.Append("# kind\tpath\tsha256\tbytes\tsource\n");

                await using ZipArchive zip = await ZipArchive.CreateAsync(
                    stream, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, token)
                    .ConfigureAwait(false);

                foreach (Planned entry in ordered)
                {
                    token.ThrowIfCancellationRequested();
                    using IMemoryOwner<byte>? owner = await ReadAsync(entry, token).ConfigureAwait(false);
                    string kind = entry.Kind;

                    if (owner is null)
                    {
                        // A miss, or a file only resolved that is gone now:
                        // either way nothing to carry, and the line says so.
                        if (entry.Dependency is { Kind: DependencyKind.Missing })
                        {
                            misses++;
                        }

                        manifest.Append(CultureInfo.InvariantCulture, $"{kind}\t{entry.BundlePath}\t-\t-\t{await SourceOfAsync(entry, token).ConfigureAwait(false)}\n");
                        continue;
                    }

                    string hash = DependencyRecorder.Hash(owner.Memory.Span);
                    await WriteEntryAsync(zip, entry.BundlePath, owner.Memory, token).ConfigureAwait(false);
                    files++;
                    bytes += owner.Memory.Length;
                    manifest.Append(CultureInfo.InvariantCulture, $"{kind}\t{entry.BundlePath}\t{hash}\t{owner.Memory.Length}\t{await SourceOfAsync(entry, token).ConfigureAwait(false)}\n");
                }

                await WriteEntryAsync(zip, GameInfoName, Encoding.UTF8.GetBytes(gameInfo), token).ConfigureAwait(false);
                await WriteEntryAsync(zip, ManifestName, Encoding.UTF8.GetBytes(manifest.ToString()), token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return new ContentBundleSummary(files, bytes, misses);
    }

    /// <summary>
    /// The bundle's <c>gameinfo.txt</c>: the recorded game's own file with its
    /// <c>SearchPaths</c> replaced by one line that mounts the bundle itself.
    /// </summary>
    /// <param name="original">The recorded game's <c>gameinfo.txt</c>, or null.</param>
    /// <returns>The text to write.</returns>
    /// <exception cref="InvalidDataException">The original has a section that never closes.</exception>
    /// <remarks>
    /// <para>
    /// The rest of the file is kept byte for byte, and that matters for the
    /// replay's output, not only its looks: the format pipeline reads
    /// <c>SteamAppId</c> and the <c>Tools</c> block off this file, and they
    /// pick the BSP version, the static-prop lump version and default tool
    /// arguments. A generated file with only the search paths would compile a
    /// different BSP.
    /// </para>
    /// <para>
    /// The block found is the one the loader reads: the first
    /// <c>FileSystem</c> section directly inside the first top-level section,
    /// and the first <c>SearchPaths</c> section directly inside that, names
    /// matched without regard to case. A file without one gets one inserted
    /// where the loader looks. With no original at all the result is a
    /// minimal gameinfo with no <c>SteamAppId</c>, which resolves to the
    /// default format, as the recorded compile would have without one.
    /// </para>
    /// </remarks>
    public static string BundleGameInfo(string? original)
    {
        if (original is null)
        {
            return "\"GameInfo\"\n{\n\tgame\t\"ssmap content bundle\"\n\tFileSystem\n\t{\n"
                + SearchPathsBlock("\t\t") + "\t}\n}\n";
        }

        List<Token> tokens = Tokenize(original);
        int root = tokens.FindIndex(static t => t.Text == "{" && !t.Quoted);
        if (root < 0)
        {
            // No section at all: nothing the loader would read, so nothing
            // worth keeping either.
            return BundleGameInfo(null);
        }

        int rootClose = Match(tokens, root);
        int fileSystem = FindSection(tokens, root, rootClose, "FileSystem");
        if (fileSystem < 0)
        {
            return Insert(original, tokens[root].End, "\n\tFileSystem\n\t{\n" + SearchPathsBlock("\t\t") + "\t}\n");
        }

        int fileSystemClose = Match(tokens, fileSystem);
        int searchPaths = FindSection(tokens, fileSystem, fileSystemClose, "SearchPaths");
        if (searchPaths < 0)
        {
            return Insert(original, tokens[fileSystem].End, "\n" + SearchPathsBlock("\t\t"));
        }

        // From the key to the closing brace, so the replacement is one block.
        int from = tokens[searchPaths - 1].Start;
        int to = tokens[Match(tokens, searchPaths)].End;
        return original[..from] + SearchPathsBlock("\t\t").TrimStart('\t').TrimEnd('\n') + original[to..];
    }

    private static string SearchPathsBlock(string indent) =>
        $"{indent}SearchPaths\n{indent}{{\n"
        + $"{indent}\t// ssmap all --record-content: every file the recorded compile read is in this directory.\n"
        + $"{indent}\tgame+mod\t|gameinfo_path|.\n"
        + $"{indent}}}\n";

    private static string Insert(string text, int at, string block) => text[..at] + block + text[at..];

    /// <summary>The index of the <c>{</c> of the first direct child section named <paramref name="name"/>, or -1.</summary>
    private static int FindSection(List<Token> tokens, int open, int close, string name)
    {
        int depth = 0;
        for (int i = open + 1; i < close; i++)
        {
            Token token = tokens[i];
            if (!token.Quoted && token.Text == "{")
            {
                depth++;
            }
            else if (!token.Quoted && token.Text == "}")
            {
                depth--;
            }
            else if (depth == 0
                && string.Equals(token.Text, name, StringComparison.OrdinalIgnoreCase)
                && i + 1 < close
                && tokens[i + 1] is { Quoted: false, Text: "{" })
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>The index of the <c>}</c> that closes the <c>{</c> at <paramref name="open"/>.</summary>
    private static int Match(List<Token> tokens, int open)
    {
        int depth = 0;
        for (int i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Quoted)
            {
                continue;
            }

            if (tokens[i].Text == "{")
            {
                depth++;
            }
            else if (tokens[i].Text == "}" && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidDataException("gameinfo.txt has a section that is never closed");
    }

    /// <summary>
    /// The same bracing, quoting and <c>//</c> comments the gameinfo loader
    /// reads, with each token's place in the text so a block can be cut out.
    /// </summary>
    private static List<Token> Tokenize(string text)
    {
        List<Token> tokens = [];
        int at = 0;
        while (true)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }

            if (at + 1 < text.Length && text[at] == '/' && text[at + 1] == '/')
            {
                while (at < text.Length && text[at] is not ('\n' or '\r'))
                {
                    at++;
                }

                continue;
            }

            if (at >= text.Length)
            {
                return tokens;
            }

            int start = at;
            if (text[at] == '"')
            {
                at++;
                while (at < text.Length && text[at] != '"')
                {
                    at++;
                }

                string quoted = text[(start + 1)..at];
                at = Math.Min(at + 1, text.Length);
                tokens.Add(new Token(quoted, true, start, at));
                continue;
            }

            if (text[at] is '{' or '}')
            {
                at++;
                tokens.Add(new Token(text[start..at], false, start, at));
                continue;
            }

            while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not ('{' or '}' or '"'))
            {
                at++;
            }

            tokens.Add(new Token(text[start..at], false, start, at));
        }
    }

    private static async Task WriteEntryAsync(
        ZipArchive zip, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        await using Stream stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A planned entry's bytes, verified against the recorded hash when the compile read it.</summary>
    private async Task<IMemoryOwner<byte>?> ReadAsync(Planned entry, CancellationToken cancellationToken)
    {
        if (entry.Loose is { } loose)
        {
            return await _looseFiles.ReadAllAsync(loose.DiskPath, cancellationToken).ConfigureAwait(false);
        }

        FileDependency dependency = entry.Dependency!.Value;
        if (dependency.Kind == DependencyKind.Missing)
        {
            return null;
        }

        IMemoryOwner<byte>? owner = await Source.ReadAsync(dependency.Path, cancellationToken).ConfigureAwait(false);
        if (dependency.Kind != DependencyKind.Read)
        {
            return owner;
        }

        if (owner is null)
        {
            throw new InvalidDataException(
                $"{dependency.Path} was read by the compile and is gone now; the content changed under the compile");
        }

        if (!string.Equals(DependencyRecorder.Hash(owner.Memory.Span), dependency.ContentHash, StringComparison.Ordinal))
        {
            owner.Dispose();
            throw new InvalidDataException(
                $"{dependency.Path} has different bytes from the ones the compile read; the content changed under the compile");
        }

        return owner;
    }

    /// <summary>The manifest's source column: the mount a game file came from, or why a loose file was there.</summary>
    private async Task<string> SourceOfAsync(Planned entry, CancellationToken cancellationToken)
    {
        if (entry.Loose is { } loose)
        {
            return $"{loose.Mount}: {loose.DiskPath}";
        }

        FileDependency dependency = entry.Dependency!.Value;
        if (dependency.Kind == DependencyKind.Missing)
        {
            return "-";
        }

        ContentSource? source = await Source.ResolveAsync(dependency.Path, cancellationToken).ConfigureAwait(false);
        return source is { } found ? found.Mount : "(gone)";
    }

    private readonly record struct Planned(string BundlePath, FileDependency? Dependency, LooseContentFile? Loose)
    {
        public string Kind => Loose is not null
            ? "loose"
            : Dependency!.Value.Kind switch
            {
                DependencyKind.Read => "read",
                DependencyKind.Resolved => "resolved",
                _ => "missing",
            };
    }

    private readonly record struct Token(string Text, bool Quoted, int Start, int End);
}
