//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Runtime.CompilerServices;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// A decorator that records what a compile looked up in the game's content, and
/// what it failed to find.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RecordingFileSystem"/> cannot do this job on its own, and the
/// reason is the whole point of the content layer. A lookup that MISSES never
/// reaches the file system at all — it is answered by the mounts' own indexes,
/// in memory — so a recorder that only sat under <see cref="IFileSystem"/>
/// would see nothing and record nothing. The miss is exactly the input that a
/// later run can invalidate by adding one file to an earlier mount.
/// </para>
/// <para>
/// Dependencies are recorded against the RESOLVED path, so <c>Metal/A</c> and
/// <c>metal/a</c> are one entry and not two. A miss has no resolved path, so it
/// is recorded under the path that was asked for, folded, so that two spellings
/// of one absent file are also one entry.
/// </para>
/// <para>
/// Shares its <see cref="DependencyRecorder"/> with the
/// <see cref="RecordingFileSystem"/> underneath, so a stage has ONE input set
/// however it reached a file.
/// </para>
/// </remarks>
public sealed class RecordingContentFileSystem : IContentFileSystem
{
    private readonly IContentFileSystem _inner;

    /// <summary>Wraps a content file system, recording into a new recorder.</summary>
    /// <param name="inner">The content file system to record.</param>
    public RecordingContentFileSystem(IContentFileSystem inner)
        : this(inner, new DependencyRecorder())
    {
    }

    /// <summary>Wraps a content file system, recording into an existing recorder.</summary>
    /// <param name="inner">The content file system to record.</param>
    /// <param name="recorder">Where dependencies go.</param>
    public RecordingContentFileSystem(IContentFileSystem inner, DependencyRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(recorder);

        _inner = inner;
        Recorder = recorder;
    }

    /// <summary>The recorded input set.</summary>
    public DependencyRecorder Recorder { get; }

    /// <inheritdoc />
    public async ValueTask<ContentSource?> ResolveAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        ContentSource? source = await _inner.ResolveAsync(path, cancellationToken).ConfigureAwait(false);

        if (source is null)
        {
            Recorder.RecordMiss(Fold(path));
        }
        else
        {
            Recorder.RecordResolved(source.Value.Path);
        }

        return source;
    }

    /// <inheritdoc />
    public async ValueTask<IMemoryOwner<byte>?> ReadAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        // Resolved first, so the hash is recorded against the path the mount
        // really holds rather than whichever spelling this caller wrote.
        ContentSource? source = await _inner.ResolveAsync(path, cancellationToken).ConfigureAwait(false);

        if (source is null)
        {
            Recorder.RecordMiss(Fold(path));
            return null;
        }

        IMemoryOwner<byte>? owner = await _inner.ReadAsync(path, cancellationToken).ConfigureAwait(false);

        if (owner is null)
        {
            // Resolved but unreadable: the file went away between the two
            // calls, or a mount is lying. Either way this run saw no bytes.
            Recorder.RecordMiss(source.Value.Path);
            return null;
        }

        Recorder.RecordRead(source.Value.Path, owner.Memory.Span);
        return owner;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        CancellationToken cancellationToken = default) =>
        Enumerate(directory, searchPattern, cancellationToken);

    private static VPath Fold(VPath path) => VPath.Create(ContentIndex.Fold(path));

    private async IAsyncEnumerable<VPath> Enumerate(
        VPath directory,
        string searchPattern,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A listing is not a read, and the files it names are not inputs until
        // something reads one. What IS an input is the set of names, which is a
        // property of the mounts rather than of any one path -- so nothing is
        // recorded here, and a stage that branches on a listing has to read
        // what it found, which is recorded.
        await foreach (VPath path in _inner
            .EnumerateAsync(directory, searchPattern, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return path;
        }
    }
}
