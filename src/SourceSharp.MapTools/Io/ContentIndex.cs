//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Io;

/// <summary>
/// The case-folded index a mount answers lookups from.
/// </summary>
/// <remarks>
/// <para>
/// THE rule this layer exists to get right: Source content lookup is
/// case-insensitive and a Linux disk is not. The index is built once, at mount
/// time, from what is really there; a lookup folds the asked-for path the same
/// way and reads one dictionary.
/// </para>
/// <para>
/// When two files in one mount fold to the same key — <c>Metal/A.vmt</c> and
/// <c>metal/a.vmt</c> genuinely both present, which happens on a Linux install
/// and never on the Windows one a mod was built on — the FIRST is kept. That is
/// arbitrary, but it is arbitrary the same way every time, which probing is
/// not, and <see cref="Collisions"/> records the pair so a linter can say so.
/// </para>
/// </remarks>
internal sealed class ContentIndex
{
    private readonly Dictionary<string, VPath> _byFoldedPath = new(StringComparer.Ordinal);
    private readonly List<(VPath Kept, VPath Shadowed)> _collisions = [];

    /// <summary>Every path the index holds, in its mount's own spelling.</summary>
    public IReadOnlyCollection<VPath> Paths => _byFoldedPath.Values;

    /// <summary>Pairs of paths in one mount that fold to the same key.</summary>
    public IReadOnlyList<(VPath Kept, VPath Shadowed)> Collisions => _collisions;

    /// <summary>Folds a path to its lookup key.</summary>
    /// <param name="path">The path to fold.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// Invariant lower-casing rather than <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// as the dictionary's comparer, so the key is a value this code can hand
    /// to a cache, log or compare elsewhere without every one of those places
    /// having to remember which comparer to use.
    /// </remarks>
    public static string Fold(VPath path) => path.Value.ToLowerInvariant();

    /// <summary>Adds a path the mount really holds.</summary>
    /// <param name="path">The path, in the mount's own spelling.</param>
    public void Add(VPath path)
    {
        string key = Fold(path);

        if (_byFoldedPath.TryGetValue(key, out VPath kept))
        {
            if (kept != path)
            {
                _collisions.Add((kept, path));
            }

            return;
        }

        _byFoldedPath[key] = path;
    }

    /// <summary>Looks a content path up, whatever its casing.</summary>
    /// <param name="path">The path as it was asked for.</param>
    /// <param name="actual">The path in the mount's own spelling.</param>
    /// <returns>True when the mount holds it.</returns>
    public bool TryResolve(VPath path, out VPath actual) =>
        _byFoldedPath.TryGetValue(Fold(path), out actual);
}
