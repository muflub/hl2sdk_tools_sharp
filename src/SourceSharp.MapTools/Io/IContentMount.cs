//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// One entry in a game's search path: a loose directory, a VPK, or a BSP's
/// embedded pak.
/// </summary>
/// <remarks>
/// <para>
/// A mount owns the case folding, and that is the whole reason this interface
/// exists rather than the content file system talking to
/// <see cref="IFileSystem"/> and <see cref="IPackedArchive"/> directly. Maps
/// reference <c>Metal/Metalwall048a</c>, the disk holds
/// <c>metal/metalwall048a.vmt</c>, and on Linux those are different files.
/// </para>
/// <para>
/// So <see cref="TryResolve"/> is a lookup in an index built ONCE at mount
/// time, not a series of probes for candidate spellings. Probing is slow —
/// a material with three path segments has eight plausible spellings before
/// anyone tries the extension — and worse, it cannot answer "which file did I
/// get?" the same way twice when two spellings both exist. An index answers
/// with the one path that is really there, which is what the dependency
/// recorder needs to write down.
/// </para>
/// </remarks>
public interface IContentMount : IAsyncDisposable
{
    /// <summary>
    /// A description of the mount — a directory path or an archive name — for
    /// diagnostics and the cache's <c>explain</c>. Not a key.
    /// </summary>
    string Name { get; }

    /// <summary>Every path the mount holds, in the mount's own spelling.</summary>
    IReadOnlyCollection<VPath> Paths { get; }

    /// <summary>Folds a content path onto the one this mount really holds.</summary>
    /// <param name="path">A content-relative path, in any casing.</param>
    /// <param name="actual">The path in the mount's own spelling.</param>
    /// <returns>True when this mount holds the file.</returns>
    bool TryResolve(VPath path, out VPath actual);

    /// <summary>Reads a file out of this mount.</summary>
    /// <param name="actual">
    /// A path in the mount's own spelling, as <see cref="TryResolve"/> returned it.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its bytes, or null when this mount does not hold it.</returns>
    ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath actual, CancellationToken cancellationToken = default);

    /// <summary>Reads part of a file out of this mount.</summary>
    /// <param name="actual">
    /// A path in the mount's own spelling, as <see cref="TryResolve"/> returned it.
    /// </param>
    /// <param name="offset">Where the range starts.</param>
    /// <param name="length">How many bytes to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The range and the file's length, or null when this mount does not hold
    /// the file. Short or empty past the end, as
    /// <see cref="IFileSystem.ReadRangeAsync"/> describes.
    /// </returns>
    ValueTask<FileRange?> ReadRangeAsync(
        VPath actual,
        long offset,
        int length,
        CancellationToken cancellationToken = default);
}
