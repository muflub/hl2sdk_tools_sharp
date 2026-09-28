//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Collections.Concurrent;

using SourceSharp.MapTools.Io;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A stream over another that counts the bytes read through it and, when
/// asked, refuses to seek: what the pack facts use to prove a link reads only
/// the index and the rooms it needs, and that a reader copes with a stream
/// that can only go forward.
/// </summary>
internal sealed class TapStream(Stream inner, bool seekable = true) : Stream
{
    private long _read;

    /// <summary>How many bytes have been read through this stream.</summary>
    public long BytesRead => Interlocked.Read(ref _read);

    public override bool CanRead => true;

    public override bool CanSeek => seekable && inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => CanSeek ? inner.Length : throw new NotSupportedException();

    public override long Position
    {
        get => CanSeek ? inner.Position : throw new NotSupportedException();
        set => inner.Position = CanSeek ? value : throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override long Seek(long offset, SeekOrigin origin) =>
        CanSeek ? inner.Seek(offset, origin) : throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        Interlocked.Add(ref _read, read);
        return read;
    }
}

/// <summary>
/// A filesystem over another that counts what is read through it, and lets
/// a fact act at the moment a file is read or a replace has written its
/// bytes (to cancel a run there, say).
/// </summary>
internal sealed class TapFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly ConcurrentDictionary<string, long> _bytes = new(StringComparer.Ordinal);

    /// <summary>Runs as a file is opened or read whole, with its path.</summary>
    public Action<VPath>? OnRead { get; init; }

    /// <summary>Runs inside a replace, after the writer has filled the temporary and before it is renamed into place.</summary>
    public Action<VPath>? AfterWrite { get; init; }

    /// <summary>Bytes read through <see cref="OpenReadAsync"/> streams, per file, so far.</summary>
    public long BytesReadFrom(VPath path) => _bytes.TryGetValue(path.Value, out long read) ? read : 0;

    public async ValueTask<Stream> OpenReadAsync(VPath path, CancellationToken cancellationToken = default)
    {
        OnRead?.Invoke(path);
        TapStream stream = new(await inner.OpenReadAsync(path, cancellationToken));
        return new Tally(stream, () => _bytes.AddOrUpdate(path.Value, stream.BytesRead, (_, b) => b + stream.BytesRead));
    }

    public ValueTask<IMemoryOwner<byte>> ReadAllAsync(VPath path, CancellationToken cancellationToken = default)
    {
        OnRead?.Invoke(path);
        return inner.ReadAllAsync(path, cancellationToken);
    }

    public ValueTask<Stream> OpenWriteAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.OpenWriteAsync(path, cancellationToken);

    public ValueTask ReplaceAsync(
        VPath path, Func<Stream, CancellationToken, ValueTask> write, CancellationToken cancellationToken = default) =>
        inner.ReplaceAsync(
            path,
            async (stream, token) =>
            {
                await write(stream, token);
                AfterWrite?.Invoke(path);
            },
            cancellationToken);

    public ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(path, cancellationToken);

    public ValueTask<FileInfoSnapshot?> GetInfoAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.GetInfoAsync(path, cancellationToken);

    public IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory, string searchPattern = "*", bool recursive = false, CancellationToken cancellationToken = default) =>
        inner.EnumerateAsync(directory, searchPattern, recursive, cancellationToken);

    public ValueTask DeleteAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(path, cancellationToken);

    /// <summary>The tap, which records its count against the file when the reader disposes it.</summary>
    private sealed class Tally(TapStream tap, Action closed) : Stream
    {
        private bool _closed;

        public override bool CanRead => tap.CanRead;

        public override bool CanSeek => tap.CanSeek;

        public override bool CanWrite => false;

        public override long Length => tap.Length;

        public override long Position { get => tap.Position; set => tap.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => tap.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            tap.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => tap.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                closed();
                tap.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Content over another that counts every read per path (folded, as the
/// content lookup folds it), and can hold or fail a read: for the facts on
/// the material facts shared across rooms.
/// </summary>
internal sealed class CountingContent(IContentFileSystem inner) : IContentFileSystem
{
    private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);

    /// <summary>Runs before each read, with the path and the reader's token; may wait or throw.</summary>
    public Func<VPath, CancellationToken, ValueTask>? BeforeRead { get; set; }

    /// <summary>Every path read, and how many times.</summary>
    public IReadOnlyDictionary<string, int> Reads => _reads;

    /// <summary>How many times a path was read, whatever its case.</summary>
    public int ReadsOf(string path) => _reads.TryGetValue(ContentIndex.Fold(VPath.Create(path)), out int n) ? n : 0;

    public ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.ResolveAsync(path, cancellationToken);

    public async ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default)
    {
        _reads.AddOrUpdate(ContentIndex.Fold(path), 1, (_, n) => n + 1);
        if (BeforeRead is { } before)
        {
            await before(path, cancellationToken);
        }

        return await inner.ReadAsync(path, cancellationToken);
    }

    public IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory, string searchPattern = "*", CancellationToken cancellationToken = default) =>
        inner.EnumerateAsync(directory, searchPattern, cancellationToken);
}
