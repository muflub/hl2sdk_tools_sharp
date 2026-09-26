//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// Where a resolved content file actually came from.
/// </summary>
/// <param name="Path">The path as it resolved, in the mount's own spelling.</param>
/// <param name="Mount">
/// A description of the mount, for example a directory path or a VPK name. For
/// diagnostics and the cache's <c>explain</c>; not a key.
/// </param>
public readonly record struct ContentSource(VPath Path, string Mount);

/// <summary>
/// Game content: <c>gameinfo.txt</c> search paths, loose directories, VPKs and
/// the BSP's own embedded pak, behind one lookup.
/// </summary>
/// <remarks>
/// <para>
/// Layered on <see cref="IFileSystem"/> rather than replacing it, and it owns
/// one Source-specific rule the physical layer must not get wrong: content
/// lookups are CASE-INSENSITIVE. Maps and materials reference
/// <c>Metal/Metalwall048a</c> and the disk holds <c>metal/metalwall048a.vmt</c>,
/// which works on Windows by accident and on Linux not at all. A mount
/// therefore builds a case-folded index once, rather than probing spellings --
/// probing is both slow and unable to answer "which file did I get?"
/// consistently.
/// </para>
/// <para>
/// Resolution order is the search-path order, first match wins, and it is
/// observable through <see cref="ResolveAsync"/> so a fact can pin it against a
/// fixture with the same name in two mounts.
/// </para>
/// </remarks>
public interface IContentFileSystem
{
    /// <summary>
    /// Finds which mount a content path resolves to, without reading it.
    /// </summary>
    /// <param name="path">A content-relative path, in any casing.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>Where it resolved, or null when nothing has it.</returns>
    /// <remarks>
    /// A null result is RECORDED by the dependency recorder, not discarded: a
    /// file later added to an earlier mount changes what the same lookup
    /// resolves to, so a miss is part of the input set that decides whether a
    /// cached product is still valid.
    /// </remarks>
    ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Reads a content file.</summary>
    /// <param name="path">A content-relative path, in any casing.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its bytes, or null when nothing has it.</returns>
    ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default);

    /// <summary>Lists content files under a directory across every mount.</summary>
    /// <param name="directory">A content-relative directory.</param>
    /// <param name="searchPattern">A glob matched against each file name.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>
    /// The matching paths, de-duplicated so a file shadowed by an earlier mount
    /// appears once.
    /// </returns>
    IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        CancellationToken cancellationToken = default);
}
