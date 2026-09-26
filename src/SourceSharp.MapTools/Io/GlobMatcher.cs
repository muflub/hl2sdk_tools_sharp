//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Io;

/// <summary>
/// Matches a file name against the <c>*</c>/<c>?</c> patterns
/// <see cref="IFileSystem.EnumerateAsync"/> takes.
/// </summary>
/// <remarks>
/// <para>
/// Ours rather than <c>Directory.EnumerateFiles</c>'s, for one reason: every
/// implementation of <see cref="IFileSystem"/> has to agree about what
/// <c>*.vmt</c> means, and the platform's matcher does not — it folds case on
/// Windows and does not on Linux, and the DOS-era three-character-extension
/// quirks it keeps are not something an in-memory dictionary is going to
/// reproduce by accident.
/// </para>
/// <para>
/// Matching is case-INSENSITIVE on every implementation, including the physical
/// one on a case-sensitive disk. An extension is a format tag rather than
/// content (the same reasoning as <see cref="VPath.Extension"/>): a caller
/// asking for <c>*.vmt</c> means every material, and a disk holding
/// <c>FOO.VMT</c> holds a material.
/// </para>
/// </remarks>
internal static class GlobMatcher
{
    /// <summary>The pattern that matches everything, and costs nothing to test.</summary>
    public const string MatchAll = "*";

    /// <summary>Whether <paramref name="name"/> matches <paramref name="pattern"/>.</summary>
    /// <param name="name">A file name, without any directory part.</param>
    /// <param name="pattern">A pattern using <c>*</c> and <c>?</c>.</param>
    /// <returns>True when the name matches.</returns>
    public static bool IsMatch(ReadOnlySpan<char> name, string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern is MatchAll)
        {
            return true;
        }

        // Iterative backtracking over the last '*' seen, so a pathological
        // pattern cannot recurse the stack away.
        int n = 0;
        int p = 0;
        int starPattern = -1;
        int starName = 0;

        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], name[n])))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starName = n;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                n = ++starName;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool Same(char a, char b) =>
        a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
