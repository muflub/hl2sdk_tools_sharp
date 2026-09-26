//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// A bundle of files addressed by path: a VPK, or a BSP's embedded pak.
/// </summary>
/// <remarks>
/// <para>
/// One interface for both because the content layer does not care which it is
/// reading from, and because the difference between "a VPK mount" and "a pak
/// mount" is a format reader rather than a lookup rule. Mounting either is then
/// the same code, and a fact about resolution order can use whichever is
/// cheaper to build.
/// </para>
/// <para>
/// Paths are in the ARCHIVE's own spelling. Case folding belongs to the mount
/// that indexes the archive, not to the archive — the same division
/// <see cref="VPath"/> makes and for the same reason.
/// </para>
/// </remarks>
public interface IPackedArchive : IAsyncDisposable
{
    /// <summary>
    /// A description of the archive, for diagnostics and the cache's
    /// <c>explain</c>. Not a key.
    /// </summary>
    string Name { get; }

    /// <summary>Every path the archive holds, in its own spelling.</summary>
    IReadOnlyCollection<VPath> Paths { get; }

    /// <summary>Reads one file out of the archive.</summary>
    /// <param name="path">A path, in the archive's own spelling.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its bytes, or null when the archive does not hold it.</returns>
    ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default);
}
