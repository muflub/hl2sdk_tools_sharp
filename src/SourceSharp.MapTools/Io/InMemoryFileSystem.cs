//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// A file system that is a dictionary, which is what the unit tier runs on.
/// </summary>
/// <remarks>
/// <para>
/// The intent is that a compile touching no disk at all is the NORMAL test in
/// this project, not a special one — so building a fixture has to be a line of
/// code rather than a temp directory, a cleanup and a platform caveat. Hence
/// the fluent adders: <c>new InMemoryFileSystem().AddText("materials/a.vmt",
/// "…")</c> is the whole setup.
/// </para>
/// <para>
/// There are no directories. A directory exists exactly when a file is under
/// it, which is what every consumer of <see cref="IFileSystem"/> already
/// assumes, and it means a fixture cannot be wrong about a directory that a
/// real disk would have needed created.
/// </para>
/// <para>
/// Timestamps come from an injected <see cref="TimeProvider"/>. Nothing may
/// decide correctness from them (see <see cref="FileInfoSnapshot"/>), so this
/// exists for the benefit of a UI that shows one — and so that a fact about a
/// timestamp does not have to sleep.
/// </para>
/// </remarks>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Dictionary<VPath, Entry> _files = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    /// <summary>Creates an empty file system using the system clock.</summary>
    public InMemoryFileSystem()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Creates an empty file system.</summary>
    /// <param name="timeProvider">The clock timestamps come from.</param>
    public InMemoryFileSystem(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _time = timeProvider;
    }

    /// <summary>How many files the file system holds.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _files.Count;
            }
        }
    }

    /// <summary>A snapshot of every path in the file system.</summary>
    /// <remarks>A copy, so enumerating it while a compile writes is safe.</remarks>
    public IReadOnlyList<VPath> Paths
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Keys];
            }
        }
    }

    /// <summary>Adds or replaces a file.</summary>
    /// <param name="path">Where the file goes.</param>
    /// <param name="contents">Its bytes; copied, so the caller may reuse the array.</param>
    /// <returns>This file system, so adders chain.</returns>
    public InMemoryFileSystem AddFile(string path, ReadOnlySpan<byte> contents) =>
        AddFile(VPath.Create(path), contents);

    /// <summary>Adds or replaces a file.</summary>
    /// <param name="path">Where the file goes.</param>
    /// <param name="contents">Its bytes; copied, so the caller may reuse the array.</param>
    /// <returns>This file system, so adders chain.</returns>
    public InMemoryFileSystem AddFile(VPath path, ReadOnlySpan<byte> contents)
    {
        Write(path, contents.ToArray());
        return this;
    }

    /// <summary>Adds or replaces a file holding UTF-8 text.</summary>
    /// <param name="path">Where the file goes.</param>
    /// <param name="text">The text, encoded as UTF-8 without a byte-order mark.</param>
    /// <returns>This file system, so adders chain.</returns>
    public InMemoryFileSystem AddText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return AddFile(VPath.Create(path), Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Reads a file's bytes without going through the asynchronous surface.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>A copy of its bytes, or null when there is no such file.</returns>
    /// <remarks>
    /// For assertions. A fact that has just written through
    /// <see cref="ReplaceAsync"/> wants to say what the bytes are, and making it
    /// await a read to do so buys nothing.
    /// </remarks>
    public byte[]? GetBytes(VPath path)
    {
        lock (_gate)
        {
            return _files.TryGetValue(path, out Entry entry) ? (byte[])entry.Contents.Clone() : null;
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] contents = Read(path);
        Stream stream = new MemoryStream(contents, writable: false);
        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc />
    public ValueTask<IMemoryOwner<byte>> ReadAllAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] contents = Read(path);
        IMemoryOwner<byte> owner = PooledMemoryOwner.Copy(contents);
        return ValueTask.FromResult(owner);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Copies only the range out of the stored array, so a fact that counts
    /// what a reader asked for sees the same sizes a disk would serve.
    /// </remarks>
    public ValueTask<FileRange> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();

        // The stored array is never written in place (Write swaps in a new
        // one), so copying out of it after Read has returned it is safe.
        byte[] contents = Read(path);
        return ValueTask.FromResult(FileRange.Copy(contents, offset, length));
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenWriteAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Stream stream = new CommitStream(this, path);
        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc />
    public async ValueTask ReplaceAsync(
        VPath path,
        Func<Stream, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        // The same shape as the physical implementation's temp-then-rename: the
        // buffer is the temporary, and nothing reaches the dictionary until the
        // writer has returned and the token has not fired.
        using MemoryStream buffer = new();
        await write(buffer, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        Write(path, buffer.ToArray());
    }

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(_files.ContainsKey(path));
        }
    }

    /// <inheritdoc />
    public ValueTask<FileInfoSnapshot?> GetInfoAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            FileInfoSnapshot? snapshot = _files.TryGetValue(path, out Entry entry)
                ? new FileInfoSnapshot(entry.Contents.Length, entry.LastWriteTimeUtc)
                : null;

            return ValueTask.FromResult(snapshot);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchPattern);

        foreach (VPath path in Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsUnder(path, directory, recursive))
            {
                continue;
            }

            if (GlobMatcher.IsMatch(path.FileName, searchPattern))
            {
                yield return path;
            }
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(VPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _files.Remove(path);
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsUnder(VPath path, VPath directory, bool recursive)
    {
        if (directory.IsEmpty)
        {
            return recursive || path.Directory.IsEmpty;
        }

        if (!recursive)
        {
            return path.Directory == directory;
        }

        string value = path.Value;
        string prefix = directory.Value;
        return value.Length > prefix.Length
            && value[prefix.Length] == '/'
            && value.StartsWith(prefix, StringComparison.Ordinal);
    }

    private byte[] Read(VPath path)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out Entry entry))
            {
                throw new FileNotFoundException($"no such file: {path}", path.Value);
            }

            return entry.Contents;
        }
    }

    private void Write(VPath path, byte[] contents)
    {
        lock (_gate)
        {
            _files[path] = new Entry(contents, _time.GetUtcNow());
        }
    }

    private readonly record struct Entry(byte[] Contents, DateTimeOffset LastWriteTimeUtc);

    /// <summary>
    /// A writable stream whose contents reach the dictionary when it closes.
    /// </summary>
    /// <remarks>
    /// <see cref="IFileSystem.OpenWriteAsync"/> makes no atomicity promise —
    /// that is <see cref="IFileSystem.ReplaceAsync"/>'s job — but a stream that
    /// published nothing until close would be a stronger promise than the
    /// physical implementation keeps, and a fixture that relied on it would pass
    /// here and fail on a disk. So the file appears empty as soon as the stream
    /// is opened, exactly as <c>FileMode.Create</c> truncates immediately.
    /// </remarks>
    private sealed class CommitStream : MemoryStream
    {
        private readonly InMemoryFileSystem _owner;
        private readonly VPath _path;
        private bool _closed;

        public CommitStream(InMemoryFileSystem owner, VPath path)
        {
            _owner = owner;
            _path = path;
            owner.Write(path, []);
        }

        public override void Flush()
        {
            base.Flush();
            Publish();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Publish();
            return base.FlushAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                Publish();
            }

            base.Dispose(disposing);
        }

        private void Publish() => _owner.Write(_path, ToArray());
    }
}
