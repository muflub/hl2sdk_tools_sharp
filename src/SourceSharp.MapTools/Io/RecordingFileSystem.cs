//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// A decorator that records, at the seam, every file read and every lookup that
/// missed.
/// </summary>
/// <remarks>
/// <para>
/// The dependency recorder of the incremental cache is not a pass a compiler
/// makes over its inputs; it is this. Because everything goes through
/// <see cref="IFileSystem"/> and this sits in front of it, a stage CANNOT read a
/// file without it being recorded — there is no "and also remember to declare
/// it" step for anyone to forget.
/// </para>
/// <para>
/// <see cref="OpenReadAsync"/> reads the file whole and hands back a stream over
/// those bytes, rather than wrapping the underlying stream in a hashing one.
/// The wrapper looks cheaper and is wrong: a caller that reads a header and
/// stops would leave a hash of a prefix, recorded as if it were the file. A
/// dependency hash that is sometimes of part of the file is worse than no cache
/// at all, and this is the cost of it being always right. Nothing here streams
/// files too large to hold anyway — <see cref="IFileSystem.ReadAllAsync"/> is
/// the normal shape and has the same bound.
/// </para>
/// <para>
/// Writes are NOT recorded. An output is not an input, and recording one would
/// make a stage depend on its own product.
/// </para>
/// </remarks>
public sealed class RecordingFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;

    /// <summary>Wraps a file system, recording into a new recorder.</summary>
    /// <param name="inner">The file system to record.</param>
    public RecordingFileSystem(IFileSystem inner)
        : this(inner, new DependencyRecorder())
    {
    }

    /// <summary>Wraps a file system, recording into an existing recorder.</summary>
    /// <param name="inner">The file system to record.</param>
    /// <param name="recorder">
    /// Where dependencies go. Shared with a
    /// <see cref="RecordingContentFileSystem"/> so that one stage has one input
    /// set however it reached a file.
    /// </param>
    public RecordingFileSystem(IFileSystem inner, DependencyRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(recorder);

        _inner = inner;
        Recorder = recorder;
    }

    /// <summary>The recorded input set.</summary>
    public DependencyRecorder Recorder { get; }

    /// <inheritdoc />
    public async ValueTask<Stream> OpenReadAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        using IMemoryOwner<byte> owner = await ReadAllAsync(path, cancellationToken)
            .ConfigureAwait(false);

        return new MemoryStream(owner.Memory.ToArray(), writable: false);
    }

    /// <inheritdoc />
    public async ValueTask<IMemoryOwner<byte>> ReadAllAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        IMemoryOwner<byte> owner;

        try
        {
            owner = await _inner.ReadAllAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // A read that failed because the file is not there is a miss, and a
            // miss is part of the input set. Recorded before the throw, because
            // the caller is entitled to catch it and carry on.
            Recorder.RecordMiss(path);
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            Recorder.RecordMiss(path);
            throw;
        }

        Recorder.RecordRead(path, owner.Memory.Span);
        return owner;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reads the WHOLE file through <see cref="ReadAllAsync"/>, records it, and
    /// hands back the range. That gives up the saving a range read exists for,
    /// on purpose: a <see cref="DependencyKind.Read"/> entry's hash is the
    /// hash of the file's bytes, and it is compared against a whole-file hash
    /// later. A hash of the range alone would be a different kind of entry
    /// under the same name, and an edit past the range -- which the header
    /// reader does not care about, but a later reader of the same path might
    /// -- would not change it. Recording is the rare, deliberate mode (a
    /// dependency capture, not a normal compile), so it pays for soundness.
    /// </para>
    /// <para>
    /// A miss is recorded the same way <see cref="ReadAllAsync"/> records one,
    /// because it goes through it.
    /// </para>
    /// </remarks>
    public async ValueTask<FileRange> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        using IMemoryOwner<byte> owner = await ReadAllAsync(path, cancellationToken).ConfigureAwait(false);
        return FileRange.Copy(owner.Memory.Span, offset, length);
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenWriteAsync(VPath path, CancellationToken cancellationToken = default) =>
        _inner.OpenWriteAsync(path, cancellationToken);

    /// <inheritdoc />
    public ValueTask ReplaceAsync(
        VPath path,
        Func<Stream, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default) =>
        _inner.ReplaceAsync(path, write, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default)
    {
        bool exists = await _inner.ExistsAsync(path, cancellationToken).ConfigureAwait(false);

        if (exists)
        {
            Recorder.RecordResolved(path);
        }
        else
        {
            Recorder.RecordMiss(path);
        }

        return exists;
    }

    /// <inheritdoc />
    public async ValueTask<FileInfoSnapshot?> GetInfoAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        FileInfoSnapshot? info = await _inner.GetInfoAsync(path, cancellationToken)
            .ConfigureAwait(false);

        if (info is null)
        {
            Recorder.RecordMiss(path);
        }
        else
        {
            // Deliberately NOT the timestamp this just returned. An existence
            // check contributes existence; the bytes contribute a hash when
            // something reads them.
            Recorder.RecordResolved(path);
        }

        return info;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        bool recursive = false,
        CancellationToken cancellationToken = default) =>
        _inner.EnumerateAsync(directory, searchPattern, recursive, cancellationToken);

    /// <inheritdoc />
    public ValueTask DeleteAsync(VPath path, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(path, cancellationToken);
}
