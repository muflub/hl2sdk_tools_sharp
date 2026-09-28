//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// An archive mounted as content: a VPK, or a BSP's embedded pak.
/// </summary>
/// <remarks>
/// The same shape as <see cref="DirectoryContentMount"/> — an index built once
/// over what the archive really holds — so that resolution order, case folding
/// and the dependency record do not depend on which kind of mount a file came
/// out of. A fact about first-match-wins can then use whichever is cheaper to
/// build.
/// </remarks>
public sealed class ArchiveContentMount : IContentMount
{
    private readonly IPackedArchive _archive;
    private readonly ContentIndex _index;
    private readonly bool _ownsArchive;

    private ArchiveContentMount(IPackedArchive archive, ContentIndex index, bool ownsArchive)
    {
        _archive = archive;
        _index = index;
        _ownsArchive = ownsArchive;
    }

    /// <inheritdoc />
    public string Name => _archive.Name;

    /// <inheritdoc />
    public IReadOnlyCollection<VPath> Paths => _index.Paths;

    /// <summary>Pairs of entries in this archive whose paths differ only by case.</summary>
    public IReadOnlyList<(VPath Kept, VPath Shadowed)> Collisions => _index.Collisions;

    /// <summary>Indexes an archive for content lookup.</summary>
    /// <param name="archive">The archive to mount.</param>
    /// <param name="ownsArchive">
    /// Whether disposing the mount disposes the archive. False when the caller
    /// keeps using the archive for something else — a BSP's pak, which the
    /// writer also reads.
    /// </param>
    /// <returns>The mount.</returns>
    public static ArchiveContentMount Mount(IPackedArchive archive, bool ownsArchive = true)
    {
        ArgumentNullException.ThrowIfNull(archive);

        ContentIndex index = new();
        foreach (VPath path in archive.Paths)
        {
            index.Add(path);
        }

        return new ArchiveContentMount(archive, index, ownsArchive);
    }

    /// <inheritdoc />
    public bool TryResolve(VPath path, out VPath actual) => _index.TryResolve(path, out actual);

    /// <inheritdoc />
    public async ValueTask<IMemoryOwner<byte>?> ReadAsync(
        VPath actual,
        CancellationToken cancellationToken = default)
    {
        if (!_index.TryResolve(actual, out VPath resolved))
        {
            return null;
        }

        return await _archive.ReadAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<FileRange?> ReadRangeAsync(
        VPath actual,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        if (!_index.TryResolve(actual, out VPath resolved))
        {
            return null;
        }

        return await _archive.ReadRangeAsync(resolved, offset, length, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownsArchive)
        {
            await _archive.DisposeAsync().ConfigureAwait(false);
        }
    }
}
