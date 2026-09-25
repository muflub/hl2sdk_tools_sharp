namespace SourceSharp.MapGen;

/// <summary>
/// Which checkout a build output belongs to.
///
/// <para>
/// THE DEFECT THIS EXISTS TO PREVENT. Locating a repo file by walking up from
/// <see cref="AppContext.BaseDirectory"/> until the file turns up is correct
/// exactly once: in a checkout that is not nested inside another one. Agent
/// worktrees live at &lt;main checkout&gt;/.claude/worktrees/agent-&lt;id&gt;/,
/// so a walk that keeps going when the file is missing leaves the worktree,
/// re-enters the main checkout, and finds ITS copy — and the test that did the
/// walk then reports on a file its own tree does not contain, cannot change,
/// and did not build. Four separate lanes spent effort proving those failures
/// were not theirs.
/// </para>
///
/// <para>
/// The fix is not a better search. It is to stop searching for the FILE and
/// search for the TREE instead: find the nearest enclosing checkout root, then
/// look for the file at one fixed place inside it and nowhere else. A missing
/// file is then an answer ("this tree has none") rather than a reason to keep
/// walking.
/// </para>
/// </summary>
public static class RepoTree
{
    /// <summary>
    /// A directory is a checkout root when it carries a <c>.git</c> entry,
    /// file or directory.
    ///
    /// <para>
    /// A plain checkout's root holds a <c>.git</c> DIRECTORY; a linked
    /// worktree's root holds a <c>.git</c> FILE, so the walk stops at the
    /// worktree and never escapes upward into the checkout that encloses it.
    /// The marker is not build output, so this answers the same in a tree
    /// that has never been built.
    /// </para>
    /// </summary>
    public static bool IsRoot(string directory) =>
        File.Exists(Path.Combine(directory, ".git"))
        || Directory.Exists(Path.Combine(directory, ".git"));

    /// <summary>
    /// The NEAREST enclosing checkout root at or above <paramref name="start"/>,
    /// or null when there is none.
    ///
    /// <para>
    /// Nearest is the whole point. When checkouts are nested the walk passes
    /// through the inner root before the outer one, and stopping at the first
    /// is what keeps a worktree reading its own files. Callers must treat the
    /// answer as a boundary and never look above it.
    /// </para>
    /// </summary>
    public static string? FindRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (IsRoot(directory.FullName))
                return directory.FullName;
        }

        return null;
    }
}

/// <summary>
/// What a tree has to say about its compiled sandbox map.
/// </summary>
public enum SandboxMapState
{
    /// <summary>The tree has a compiled map, and it can be read and scored.</summary>
    Present,

    /// <summary>
    /// The tree was found and has no compiled map. Legitimate: the map is build
    /// output and gitignored, so a fresh checkout has none.
    /// </summary>
    NoMapInTree,

    /// <summary>
    /// No checkout root above the running binary at all. Not a legitimate
    /// state — it means the layout this depends on has changed — so callers
    /// must fail rather than treat it as "no map".
    /// </summary>
    NoTreeFound,
}

/// <summary>
/// The compiled sandbox map belonging to one checkout.
/// </summary>
/// <param name="State">Which of the three answers this is.</param>
/// <param name="Root">The checkout the answer is about, or null when none was found.</param>
/// <param name="Path">The map, set only when <see cref="SandboxMapState.Present"/>.</param>
/// <param name="Explanation">
/// Why, in words, phrased so that a reader of a test log can tell the states
/// apart without opening the test source.
/// </param>
public sealed record SandboxMapLookup(
    SandboxMapState State,
    string? Root,
    string? Path,
    string Explanation);

/// <summary>
/// Locating the compiled sandbox map, anchored to one tree.
/// </summary>
public static class SandboxArtefact
{
    /// <summary>Where a compiled sandbox map lives inside a checkout.</summary>
    public static readonly string[] RelativeParts = ["game", "mod_sharp", "maps", "ss_sandbox.bsp"];

    /// <summary>Same path, for messages.</summary>
    public const string RelativePath = "game/mod_sharp/maps/ss_sandbox.bsp";

    /// <summary>
    /// The sandbox map of the checkout enclosing <paramref name="start"/> —
    /// that checkout's own map, or the fact that it has none. Never another
    /// checkout's map, however near it sits on disk.
    /// </summary>
    public static SandboxMapLookup Locate(string start)
    {
        string? root = RepoTree.FindRoot(start);

        if (root is null)
        {
            return new SandboxMapLookup(
                SandboxMapState.NoTreeFound,
                null,
                null,
                $"no checkout root at or above \"{start}\" — looked for a directory holding "
                + "a .git entry. Nothing can be said about "
                + $"{RelativePath} without knowing which tree it should belong to.");
        }

        string candidate = Path.Combine(root, Path.Combine(RelativeParts));

        if (!File.Exists(candidate))
        {
            return new SandboxMapLookup(
                SandboxMapState.NoMapInTree,
                root,
                null,
                $"NO COMPILED MAP IN THIS TREE: {candidate} does not exist, so there is "
                + "nothing here to compare against Sandbox.cs. This is not a stale map and "
                + "not a failure — the .bsp is build output and gitignored, so a checkout "
                + "that has never run `make map MAP=ss_sandbox` legitimately has none (about "
                + "a minute, and it needs no Steam login). SandboxStalenessTests covers the "
                + "same judgement on literal inputs, so the logic stays checked here either "
                + "way. Deliberately NOT searched for outside this tree: a map belonging to "
                + "some enclosing checkout is not this one's build output.");
        }

        return new SandboxMapLookup(
            SandboxMapState.Present,
            root,
            candidate,
            $"{candidate} is the compiled map of this tree.");
    }

    /// <summary>
    /// The same question for the running binary.
    /// </summary>
    public static SandboxMapLookup Locate() => Locate(AppContext.BaseDirectory);
}
