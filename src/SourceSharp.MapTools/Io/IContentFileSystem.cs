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

    /// <summary>Reads part of a content file.</summary>
    /// <param name="path">A content-relative path, in any casing.</param>
    /// <param name="offset">Where the range starts, in bytes from the start of the file.</param>
    /// <param name="length">How many bytes to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The range and the whole file's length, or null when nothing has the
    /// file -- the same answer <see cref="ReadAsync"/> gives. A range past the
    /// end of the file comes back short or empty, as
    /// <see cref="IFileSystem.ReadRangeAsync"/> describes.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> or <paramref name="length"/> is negative.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Resolution is the same first-match-wins lookup as
    /// <see cref="ReadAsync"/>: a range comes from the file a whole read would
    /// have returned, never from a shadowed copy in a later mount.
    /// </para>
    /// <para>
    /// For the header readers: vbsp opens every texture a map's materials name
    /// only for the width, height and reflectivity in its first few hundred
    /// bytes. No default implementation, for the reason
    /// <see cref="IFileSystem.ReadRangeAsync"/> gives.
    /// </para>
    /// </remarks>
    ValueTask<FileRange?> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default);

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
