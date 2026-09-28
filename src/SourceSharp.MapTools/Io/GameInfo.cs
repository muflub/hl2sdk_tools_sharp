//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// One line of a <c>gameinfo.txt</c> <c>SearchPaths</c> block.
/// </summary>
/// <param name="Kinds">
/// The <c>+</c>-separated tags on the left: <c>game</c>, <c>mod</c>,
/// <c>platform</c>, <c>gamebin</c>, <c>mod_write</c>, <c>download</c> and the
/// rest. Lower-cased, because the engine compares them case-insensitively.
/// </param>
/// <param name="Location">
/// The location on the right, still holding any <c>|gameinfo_path|</c> or
/// <c>|all_source_engine_paths|</c> token and any trailing <c>/*</c>.
/// </param>
public readonly record struct GameInfoSearchPath(IReadOnlyList<string> Kinds, string Location)
{
    /// <summary>The token standing for the directory holding <c>gameinfo.txt</c>.</summary>
    public const string GameInfoPathToken = "|gameinfo_path|";

    /// <summary>The token standing for the directory holding the shared HL2 content.</summary>
    public const string AllSourceEnginePathsToken = "|all_source_engine_paths|";

    /// <summary>Whether this line carries a given tag.</summary>
    /// <param name="kind">The tag to look for, in any casing.</param>
    /// <returns>True when the line carries it.</returns>
    public bool HasKind(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);

        foreach (string mine in Kinds)
        {
            if (string.Equals(mine, kind, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The prefix of a location rooted at another Steam app's install:
    /// <c>|appid_440|tf/tf2_misc.vpk</c>.
    /// </summary>
    public const string AppIdPrefixToken = "|appid_";

    /// <summary>
    /// Whether the location ends in <c>/*</c>, meaning "mount every VPK and
    /// subdirectory in here, in alphabetical order".
    /// </summary>
    public bool IsWildcard => Location.EndsWith("/*", StringComparison.Ordinal);

    /// <summary>
    /// Reads an <c>|appid_N|</c> prefix: the location is relative to Steam
    /// app N's install directory.
    /// </summary>
    /// <param name="appId">The app id, or zero when the location has no prefix.</param>
    /// <param name="relativeLocation">
    /// The rest of the location, relative to the app's install; the whole
    /// location when there is no prefix.
    /// </param>
    /// <returns>True when the location starts with the prefix.</returns>
    /// <exception cref="InvalidDataException">
    /// The prefix has no closing <c>|</c> ("Malformed gameinfo.txt") or its id
    /// is zero or not a number ("Can't mount content from invalid appid").
    /// </exception>
    /// <remarks>
    /// <para>
    /// <c>FileSystem_LoadSearchPaths</c>:
    /// the prefix is matched case-insensitively and only at the START of the
    /// location; the id is <c>V_atoi</c> of what follows (leading digits); the
    /// rest starts after the next <c>|</c> and is made absolute against the
    /// app's install directory, which the engine gets from
    /// <c>SteamApps()-&gt;GetAppInstallDir</c>.
    /// </para>
    /// <para>
    /// The reference build supports this only inside its engine, not in
    /// its compilers: a stock compile tool refuses an appid-based mount
    /// outright. So a gameinfo that uses it cannot be given to the stock
    /// tools. The managed tools support it.
    /// </para>
    /// </remarks>
    public bool TryGetAppId(out int appId, out string relativeLocation)
    {
        appId = 0;
        relativeLocation = Location;

        if (!Location.StartsWith(AppIdPrefixToken, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string after = Location[AppIdPrefixToken.Length..];
        int bar = after.IndexOf('|', StringComparison.Ordinal);

        if (bar < 0)
        {
            throw new InvalidDataException($"Malformed gameinfo.txt: \"{Location}\" has no closing '|'");
        }

        // V_atoi: the leading digits, and zero when there are none.
        int digits = 0;
        while (digits < bar && char.IsAsciiDigit(after[digits]))
        {
            digits++;
        }

        if (digits == 0
            || !int.TryParse(after.AsSpan(0, digits), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out appId)
            || appId == 0)
        {
            appId = 0;
            throw new InvalidDataException($"Can't mount content from invalid appid: \"{Location}\"");
        }

        relativeLocation = after[(bar + 1)..];
        return true;
    }
}

/// <summary>
/// A game's <c>gameinfo.txt</c>: which content it is built from, and in what
/// order.
/// </summary>
/// <remarks>
/// <para>
/// The parser is a narrow one, reading only the bracing, quoting and
/// <c>//</c> comments a <c>gameinfo.txt</c> uses, because Phase 1c owns the
/// general KeyValues reader and two lanes writing one is how a merge becomes a
/// rewrite. When 1c lands, the body of <see cref="Parse"/> becomes a walk over
/// its tree and everything else here is unchanged. It is a dozen lines either
/// way; what matters is the SHAPE below it, which is the search-path list.
/// </para>
/// <para>
/// ORDER IS THE POINT. The file's order is the resolution order, first match
/// wins, and it is kept exactly as written — including the deliberate ordering
/// real files have, where VPKs come before the loose directories that hold the
/// same content so that a lookup does not make thousands of failing
/// <c>open()</c> calls.
/// </para>
/// </remarks>
public sealed class GameInfo
{
    private GameInfo(
        string game,
        int steamAppId,
        IReadOnlyList<GameInfoSearchPath> searchPaths,
        IReadOnlyDictionary<string, string> toolArguments,
        bool hasFlatToolsValue)
    {
        Game = game;
        SteamAppId = steamAppId;
        SearchPaths = searchPaths;
        ToolArguments = toolArguments;
        HasFlatToolsValue = hasFlatToolsValue;
    }

    /// <summary>The game's display name, or the empty string when it names none.</summary>
    public string Game { get; }

    /// <summary>The Steam application id, or zero when it names none.</summary>
    public int SteamAppId { get; }

    /// <summary>The search paths, in the order the file lists them.</summary>
    public IReadOnlyList<GameInfoSearchPath> SearchPaths { get; }

    /// <summary>
    /// The <c>Tools</c> section's per-tool default arguments: child key to the
    /// raw value as written, child keys lower-cased (<c>"vbsp"</c>,
    /// <c>"vvis"</c>, <c>"vrad"</c>, …).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference build reads exactly this shape, from the SAME mounted
    /// <c>gameinfo.txt</c> and in its file-system setup
    /// (find the <c>Tools</c> child, then read the <c>vbsp</c> string), and splices
    /// the value into the argument list before the real command line, logging
    /// <c>"Adding arguments from gameinfo: %s"</c>. So the working spelling is
    /// the section form:
    /// <code>
    /// Tools
    /// {
    ///     vbsp "-cullall -nodetail"
    /// }
    /// </code>
    /// </para>
    /// <para>
    /// The flat form — <c>Tools "-cullall"</c> at top level — is a
    /// <b>silent no-op</b> there: the lookup finds the flat node first, and
    /// ReadString asks it for a child named after the tool, which a value
    /// node does not have, so nothing is spliced and nothing is printed
    /// (byte-identical behaviour confirmed against the reference build).
    /// A flat line even SHADOWS a real <c>Tools { … }</c> section written
    /// after it — FindKey's peer walk stops at the first match. It is
    /// reported by <see cref="HasFlatToolsValue"/> so a host can warn
    /// instead of letting the user believe their defaults applied. Duplicat-
    /// ing a child key keeps the FIRST value, again FindKey's peer walk.
    /// </para>
    /// <para>
    /// A <c>Tools</c> block written INSIDE another section — <c>FileSystem</c>
    /// is the spelling people try — is never read at all: FindKey looks only
    /// at the root's own child list, and the root here is the folded
    /// <c>GameInfo</c> section, so <c>Tools</c> must be its direct child.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string> ToolArguments { get; }

    /// <summary>
    /// Whether the file carries the flat <c>Tools "…"</c> form, which the
    /// stock policy and the reference tools ignore (see <see cref="ToolArguments"/>).
    /// </summary>
    public bool HasFlatToolsValue { get; }

    /// <summary>Reads a <c>gameinfo.txt</c>.</summary>
    /// <param name="fileSystem">Where the file lives.</param>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What it says.</returns>
    public static async ValueTask<GameInfo> LoadAsync(
        IFileSystem fileSystem,
        VPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        using System.Buffers.IMemoryOwner<byte> owner = await fileSystem
            .ReadAllAsync(path, cancellationToken)
            .ConfigureAwait(false);

        return Parse(Encoding.UTF8.GetString(owner.Memory.Span));
    }

    /// <summary>Parses the text of a <c>gameinfo.txt</c>.</summary>
    /// <param name="text">The file's contents.</param>
    /// <returns>What it says.</returns>
    /// <exception cref="InvalidDataException">The text is not a readable gameinfo.</exception>
    public static GameInfo Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // The reference KeyValues loader folds the file's FIRST top-level
        // section into the root node (on the first iteration the current key
        // is the node being loaded, so it is only renamed to that
        // section's key and the section's entries become the root's own
        // children), while every LATER top-level section becomes a plain
        // child of that root. Every key the engine and the tools read is
        // then looked up as a walk of one node's CHILD list — first match
        // wins, never a descent into a subsection; the recursion there only
        // serves "a/b" path syntax, and the reference lookup chain for the
        // "Tools" key passes no such path. Parse therefore hoists the first
        // section's children into the root, and every lookup below is a
        // direct-child lookup on it. A Tools block written deeper — inside
        // FileSystem, say — is NOT what the tools read: that is the pitfall
        // pinned as a fact.
        Node root = Node.Parse(text);
        Node body = root;

        // The tools splice reads exactly ONE Tools child from the gameinfo
        // root, first match wins — and only that node's string-valued children.
        Node? tools = body.FindDirect("tools");
        Dictionary<string, string> toolArguments = new(StringComparer.OrdinalIgnoreCase);
        bool flatTools = false;
        if (tools is not null)
        {
            if (tools.Value is not null)
            {
                // Tools "…" where the reader expects a section: ReadString
                // asks that node for a child named after the tool, and it has
                // none — nothing is spliced and nothing is printed (verified
                // byte-identical against the reference build). A leftover
                // flat line ahead of a real section therefore SHADOWS that
                // section, exactly as the reference peer walk does.
                // Reported, never spliced.
                flatTools = true;
            }
            else
            {
                foreach (Node child in tools.Entries)
                {
                    // First child with the tool's name wins even when the file
                    // duplicates it — FindKey's peer walk breaks on the first
                    // Match — and a child whose value
                    // is a nested block carries no string to splice.
                    if (child.Value is { Length: > 0 } value
                        && !toolArguments.ContainsKey(child.Name))
                    {
                        toolArguments[child.Name] = value;
                    }
                }
            }
        }

        // The engine's own pass reads the game name, SteamAppId and the
        // SearchPaths list through the same direct-child FindKey: the FIRST
        // FileSystem child of the body, and inside it the FIRST SearchPaths
        // Child. A second one at
        // either level is dead text.
        List<GameInfoSearchPath> searchPaths = [];
        string game = body.FirstValueOf("game") ?? string.Empty;
        int steamAppId = 0;

        Node? fileSystem = body.FindDirect("filesystem");
        if (fileSystem is not null)
        {
            string? appIdText = fileSystem.FirstValueOf("steamappid");
            if (appIdText is not null)
            {
                // Non-numeric is not an error; the reference reader of this key ends
                // at zero just like a missing one.
                steamAppId = int.TryParse(appIdText, out int parsed) ? parsed : 0;
            }

            if (fileSystem.FindDirect("searchpaths") is { } searchPathsBlock)
            {
                // GetFirstValue/GetNextValue walks EVERY child line, duplicate
                // keys included, in file order — the file's order IS the
                // Resolution order.
                foreach (Node line in searchPathsBlock.Entries)
                {
                    if (line.Value is { Length: > 0 } location)
                    {
                        searchPaths.Add(new GameInfoSearchPath(SplitKinds(line.Name), location));
                    }
                }
            }
        }

        return new GameInfo(game, steamAppId, searchPaths, toolArguments, flatTools);
    }

    /// <summary>
    /// One KeyValues node: a name, its source order, and either a string
    /// value or ordered children. A throwaway — <see cref="Parse"/> is the
    /// only caller, and the tree exists for exactly the shape the stock
    /// reader gives lookups: ordered children, duplicates kept, first match
    /// wins. (Phase 1c owns the general KeyValues reader; when it lands this
    /// class and <c>Parse</c>'s walk are one call.)
    /// </summary>
    private sealed class Node(string name)
    {
        /// <summary>Lower-cased key, or "" for an anonymous nested block.</summary>
        public string Name { get; private set; } = name;

        /// <summary>
        /// The string this node carries — quoted or bare, as the file wrote
        /// it, original case — or null for a section. A key the reader gave
        /// both a value and a block keeps neither twice: a block child
        /// swallows the value, as <c>RecursiveLoadFromBuffer</c> does.
        /// </summary>
        public string? Value { get; private set; }

        /// <summary>The children in file order, duplicate keys included.</summary>
        public List<Node> Entries { get; } = [];

        /// <summary>
        /// The first direct child with this (lower-cased) name — FindKey's
        /// peer walk, first match wins, no descent — or null.
        /// </summary>
        public Node? FindDirect(string lowerName)
        {
            foreach (Node child in Entries)
            {
                if (child.Name == lowerName)
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>
        /// The value of the first direct child with this name that carries a
        /// string, or null — what <c>GetString(FindKey(name))</c> answers.
        /// </summary>
        public string? FirstValueOf(string lowerName) => FindDirect(lowerName)?.Value;

        /// <summary>Builds the tree the stock buffer reader would build.</summary>
        public static Node Parse(string text)
        {
            Node root = new(string.Empty);
            List<Node> stack = [root];
            string? pendingKey = null;
            Tokenizer tokens = new(text);
            bool topLevelSeen = false;

            while (tokens.Next() is { } token)
            {
                if (token.Text == "{" && !token.Quoted)
                {
                    // The file's FIRST top-level block is the fold: stock's
                    // RecursiveLoadFromBuffer renames the node being loaded to
                    // that key and loads the block's entries AS the root's own
                    // Children, so pushing the root
                    // again is all it takes and no child node is made.
                    if (!topLevelSeen && stack.Count == 1 && pendingKey is not null)
                    {
                        topLevelSeen = true;
                        stack.Add(stack[0]);
                        pendingKey = null;
                        continue;
                    }

                    // A key followed by '{' opens a block under that name; a
                    // stray '{' opens an anonymous one, the way
                    // RecursiveLoadFromBuffer adopts whatever came before it.
                    Node section = new(pendingKey?.ToLowerInvariant() ?? string.Empty);
                    stack[^1].Entries.Add(section);
                    stack.Add(section);
                    pendingKey = null;
                    continue;
                }

                if (token.Text == "}" && !token.Quoted)
                {
                    if (stack.Count == 1)
                    {
                        // A closing brace with nothing open. Stock reports
                        // "} in key" and stops reading; the port stops too —
                        // trailing garbage after an unbalanced '}' is not
                        // content anyone should mount.
                        throw new InvalidDataException(
                            "gameinfo.txt has a closing brace with nothing open.");
                    }

                    stack.RemoveAt(stack.Count - 1);
                    pendingKey = null;
                    continue;
                }

                if (pendingKey is null)
                {
                    pendingKey = token.Text;
                    continue;
                }

                // key then value-token. Stock's FIRST top-level pair renames
                // and fills the node being loaded itself (pCurrentKey is the
                // node on the first iteration), so stock has no child slot for
                // it and no lookup finds it under that key — the same silent
                // no-op the flat Tools is. Every later pair becomes a value
                // node; a value that repeats a key is a second node, not an
                // overwrite (FindKey picks the first), and a key whose "value"
                // is really the next key token adopts it, as
                // CreateKey-then-fill does.
                if (!topLevelSeen && stack.Count == 1)
                {
                    topLevelSeen = true;
                    root.Name = pendingKey.ToLowerInvariant();
                    root.Value = token.Text;
                    pendingKey = null;
                    continue;
                }

                Node entry = new(pendingKey.ToLowerInvariant()) { Value = token.Text };
                stack[^1].Entries.Add(entry);
                pendingKey = null;
            }

            return root;
        }
    }
    /// <summary>
    /// Replaces the <c>|…|</c> tokens in a location and strips a Windows
    /// drive letter the location itself starts with.
    /// </summary>
    /// <param name="location">A location as the file wrote it.</param>
    /// <param name="gameInfoPath">What <c>|gameinfo_path|</c> stands for.</param>
    /// <param name="allSourceEnginePaths">What <c>|all_source_engine_paths|</c> stands for.</param>
    /// <returns>The location with its tokens replaced.</returns>
    /// <remarks>
    /// The drive letter is stripped because this repo's own toolgame
    /// <c>gameinfo.txt</c> writes <c>Z:/…</c> for wine's benefit, and a reader
    /// that took <c>Z:</c> for a path segment would look for a directory called
    /// <c>Z:</c>. A drive inside a token's expansion is the host's and is
    /// kept.
    /// </remarks>
    public static string ExpandTokens(
        string location,
        string gameInfoPath,
        string allSourceEnginePaths)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(gameInfoPath);
        ArgumentNullException.ThrowIfNull(allSourceEnginePaths);

        // A token stands for a DIRECTORY and the file writes the rest straight
        // after it -- "|all_source_engine_paths|hl2/hl2_misc.vpk" -- so the
        // separator is part of the expansion. Without it the two halves run
        // together into a directory name that does not exist, and every shared
        // search path silently becomes a skip.
        //
        // The drive is stripped from what the file wrote, before expansion:
        // the directories the tokens stand for are the host's own, and on
        // Windows those carry their drive (D:/games/mod). Stripping after
        // expansion pointed every token path at the current drive instead.
        string written = location.Length >= 2 && location[1] == ':' && char.IsAsciiLetter(location[0])
            ? location[2..]
            : location;

        return written
            .Replace(
                GameInfoSearchPath.GameInfoPathToken,
                Separated(gameInfoPath),
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                GameInfoSearchPath.AllSourceEnginePathsToken,
                Separated(allSourceEnginePaths),
                StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '/');
    }

    private static string Separated(string directory) =>
        directory.Length == 0 || directory.EndsWith('/') ? directory : directory + "/";


    private static List<string> SplitKinds(string key)
    {
        List<string> kinds = [];

        foreach (string part in key.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            kinds.Add(part.Trim().ToLowerInvariant());
        }

        return kinds;
    }

    private readonly record struct Token(string Text, bool Quoted);

    /// <summary>
    /// The bracing, quoting and comments a <c>gameinfo.txt</c> uses, and
    /// nothing else. See the type's remarks for why this is not the general
    /// KeyValues reader.
    /// </summary>
    private sealed class Tokenizer(string text)
    {
        private int _at;

        public Token? Next()
        {
            while (true)
            {
                while (_at < text.Length && char.IsWhiteSpace(text[_at]))
                {
                    _at++;
                }

                if (_at + 1 < text.Length && text[_at] == '/' && text[_at + 1] == '/')
                {
                    while (_at < text.Length && text[_at] is not ('\n' or '\r'))
                    {
                        _at++;
                    }

                    continue;
                }

                break;
            }

            if (_at >= text.Length)
            {
                return null;
            }

            if (text[_at] == '"')
            {
                int start = ++_at;
                while (_at < text.Length && text[_at] != '"')
                {
                    _at++;
                }

                string quoted = text[start.._at];
                if (_at < text.Length)
                {
                    _at++;
                }

                return new Token(quoted, Quoted: true);
            }

            if (text[_at] is '{' or '}')
            {
                return new Token(text[_at++].ToString(), Quoted: false);
            }

            int from = _at;
            while (_at < text.Length
                && !char.IsWhiteSpace(text[_at])
                && text[_at] is not ('{' or '}' or '"'))
            {
                _at++;
            }

            return new Token(text[from.._at], Quoted: false);
        }
    }
}
