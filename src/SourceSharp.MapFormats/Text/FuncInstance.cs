//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The file-level half of <c>func_instance</c>: which VMF an instance entity
/// names, and where it is looked for.
/// </summary>
/// <remarks>
/// <para>
/// Only the FILE-level rules are here. What the reference compiler does after
/// loading the instance -- transforming and merging its planes, brushes, sides,
/// entities and overlays into the host map -- is compile work and belongs
/// with the geometry, not with the formats.
/// </para>
/// <para>
/// Path resolution is exposed as an ORDERED LIST OF CANDIDATES rather than as
/// a lookup, so this assembly still opens nothing. The caller probes them in
/// order through its own file system and takes the first that exists, which is
/// exactly what the reference <c>DeterminePath</c> does.
/// </para>
/// </remarks>
public static class FuncInstance
{
    /// <summary>
    /// The classname the reference compiler looks for.
    /// </summary>
    /// <remarks>
    /// Compared with <c>strcmp</c> -- CASE SENSITIVE -- unlike almost every
    /// other classname comparison in the tree. <c>Func_Instance</c> is not an
    /// instance as far as the reference compiler is concerned.
    /// </remarks>
    public const string ClassName = "func_instance";

    /// <summary>
    /// The key naming the instance's VMF.
    /// </summary>
    public const string FileKey = "file";

    /// <summary>
    /// The <c>gameinfo.txt</c> key that supplies the third search location.
    /// </summary>
    public const string GameInfoInstancePathKey = "InstancePath";

    /// <summary>
    /// The search locations for an instance, in the order
    /// <c>DeterminePath</c> tries them.
    /// </summary>
    /// <param name="baseFileName">
    /// The map that referenced the instance -- the path is taken relative to
    /// its DIRECTORY.
    /// </param>
    /// <param name="instanceFileName">The <c>file</c> key's value.</param>
    /// <param name="instancePath">
    /// <c>gameinfo.txt</c>'s <c>InstancePath</c>, or null when it has none.
    /// The reference lower-cases it and normalises its slashes when it is set,
    /// so the same is done here.
    /// </param>
    /// <returns>
    /// The candidate paths, in order. The caller takes the first that exists.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="baseFileName"/> or <paramref name="instanceFileName"/>
    /// is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// There are three, and the third one carries a quirk worth pinning:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// Beside the referring map: its directory, a separator, and the instance
    /// name with its extension forced to <c>.vmf</c>.
    /// </description></item>
    /// <item><description>
    /// Relative to the enclosing <c>maps</c> directory: the referring map's
    /// directory, lower-cased and slash-normalised, truncated just after the
    /// FIRST occurrence of <c>maps</c>, then the fixed name.
    /// "First", so a path with two
    /// <c>maps</c> components resolves against the outer one.
    /// </description></item>
    /// <item><description>
    /// Under <c>InstancePath</c> -- and this one uses the ORIGINAL,
    /// UNFIXED name: the reference concatenates
    /// <c>pszInstanceFileName</c>, not the buffer the extension was forced on.
    /// So an entry whose <c>file</c> key omits the extension is found beside
    /// the map and NOT under <c>InstancePath</c>.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<string> ResolveCandidates(
        string baseFileName,
        string instanceFileName,
        string? instancePath = null)
    {
        ArgumentNullException.ThrowIfNull(baseFileName);
        ArgumentNullException.ThrowIfNull(instanceFileName);

        List<string> candidates = [];

        // V_SetExtension then V_FixSlashes.
        string fixedName = FixSlashes(SetExtension(instanceFileName, ".vmf"));

        // Candidate 1: beside the referring map.
        string baseDirectory = StripFileName(baseFileName);
        candidates.Add(baseDirectory + "\\" + fixedName);

        // Candidate 2: relative to the enclosing maps directory.
        string normalised = FixSlashes(
            RemoveDotSlashes(StripFileName(baseFileName))).ToLowerInvariant();
        normalised = FixDoubleSlashes(normalised) + "\\";

        const string MapPath = "\\maps\\";
        int index = normalised.IndexOf(MapPath, StringComparison.Ordinal);
        if (index >= 0)
        {
            candidates.Add(normalised[..(index + MapPath.Length)] + fixedName);
        }

        // Candidate 3: under InstancePath -- the UNFIXED name.
        if (!string.IsNullOrEmpty(instancePath))
        {
            candidates.Add(FixSlashes(instancePath.ToLowerInvariant()) + instanceFileName);
        }

        return candidates;
    }

    /// <summary>
    /// <c>V_SetExtension</c>: replace whatever extension the name has.
    /// </summary>
    private static string SetExtension(string path, string extension)
    {
        int lastSeparator = path.LastIndexOfAny(['/', '\\']);
        int dot = path.LastIndexOf('.');

        return dot > lastSeparator ? path[..dot] + extension : path + extension;
    }

    /// <summary>
    /// <c>V_FixSlashes</c>: on Windows, which is what the toolset runs on, the
    /// separator is a backslash.
    /// </summary>
    private static string FixSlashes(string path) =>
        path.Replace('/', '\\');

    /// <summary><c>V_StripFilename</c>: everything up to the last separator.</summary>
    private static string StripFileName(string path)
    {
        int lastSeparator = path.LastIndexOfAny(['/', '\\']);
        return lastSeparator < 0 ? string.Empty : path[..lastSeparator];
    }

    /// <summary><c>V_RemoveDotSlashes</c>: collapse <c>.</c> and <c>..</c>.</summary>
    private static string RemoveDotSlashes(string path)
    {
        List<string> parts = [];

        foreach (string part in path.Split('/', '\\'))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('\\', parts);
    }

    /// <summary><c>V_FixDoubleSlashes</c>.</summary>
    private static string FixDoubleSlashes(string path)
    {
        while (path.Contains("\\\\", StringComparison.Ordinal))
        {
            path = path.Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return path;
    }
}
