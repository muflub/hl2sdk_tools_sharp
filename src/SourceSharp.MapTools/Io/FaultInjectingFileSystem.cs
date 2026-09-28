//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// A decorator that makes a chosen I/O failure happen on demand.
/// </summary>
/// <remarks>
/// <para>
/// Every implementation of <see cref="IFileSystem"/> makes promises about what
/// happens when I/O goes wrong — above all that
/// <see cref="IFileSystem.ReplaceAsync"/> leaves the PREVIOUS output intact when
/// a write dies half-way. Those promises are the ones that matter most and are
/// the hardest to test: filling a partition to prove a compiler does not corrupt
/// a <c>.bsp</c> is not a unit test.
/// </para>
/// <para>
/// So the failure becomes a decorator. A fact wraps an
/// <see cref="InMemoryFileSystem"/> holding the previous output, asks for a
/// write that dies at byte 1024, and asserts the old bytes are still there.
/// </para>
/// </remarks>
public sealed class FaultInjectingFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;
    private readonly FaultPlan _plan;

    /// <summary>Wraps a file system, injecting the failures a plan names.</summary>
    /// <param name="inner">The file system to fault.</param>
    /// <param name="plan">Which failure to inject, and when.</param>
    public FaultInjectingFileSystem(IFileSystem inner, FaultPlan plan)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(plan);

        _inner = inner;
        _plan = plan;
    }

    /// <inheritdoc />
    public async ValueTask<Stream> OpenReadAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        Stream stream = await _inner.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        return new FaultStream(stream, _plan, leaveOpen: false);
    }

    /// <inheritdoc />
    public async ValueTask<IMemoryOwner<byte>> ReadAllAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        IMemoryOwner<byte> owner = await _inner.ReadAllAsync(path, cancellationToken)
            .ConfigureAwait(false);

        if (_plan.CancelReadAfterBytes is long cancelAfter && owner.Memory.Length > cancelAfter)
        {
            owner.Dispose();
            throw new OperationCanceledException($"the read of {path} was cancelled");
        }

        if (_plan.ShortReadAfterBytes is not long shortAfter || owner.Memory.Length <= shortAfter)
        {
            return owner;
        }

        // A truncated file, not an error: the owner simply holds less than the
        // file claims. Whatever reads it has to notice.
        try
        {
            return PooledMemoryOwner.Copy(owner.Memory.Span[..(int)shortAfter]);
        }
        finally
        {
            owner.Dispose();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The plan's byte counts are positions in the FILE, as they are for a
    /// whole read: a range that reaches past
    /// <see cref="FaultPlan.CancelReadAfterBytes"/> is cancelled, and one that
    /// reaches past <see cref="FaultPlan.ShortReadAfterBytes"/> comes back
    /// holding only the bytes before it while <see cref="FileRange.FileLength"/>
    /// still reports the length the file claims -- the same "holds less than
    /// the file says" shape a truncated whole read has, so a header reader has
    /// to notice it the same way.
    /// </remarks>
    public async ValueTask<FileRange> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        FileRange range = await _inner.ReadRangeAsync(path, offset, length, cancellationToken)
            .ConfigureAwait(false);

        long end = range.Offset + range.Memory.Length;

        if (_plan.CancelReadAfterBytes is long cancelAfter && end > cancelAfter)
        {
            range.Dispose();
            throw new OperationCanceledException($"the read of {path} was cancelled");
        }

        if (_plan.ShortReadAfterBytes is not long shortAfter || end <= shortAfter)
        {
            return range;
        }

        try
        {
            int kept = (int)Math.Max(0, shortAfter - range.Offset);
            FileRange truncated = FileRange.Rent(kept, range.Offset, range.FileLength);
            range.Memory.Span[..kept].CopyTo(truncated.Memory.Span);
            return truncated;
        }
        finally
        {
            range.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask<Stream> OpenWriteAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        Stream stream = await _inner.OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
        return new FaultStream(stream, _plan, leaveOpen: false);
    }

    /// <inheritdoc />
    public ValueTask ReplaceAsync(
        VPath path,
        Func<Stream, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        // The INNER implementation still does the temp-then-rename; only the
        // stream handed to the writer is faulted. That is what makes this a
        // test of the real atomicity path rather than of a reimplementation of
        // it.
        return _inner.ReplaceAsync(
            path,
            async (stream, token) =>
            {
                FaultStream faulted = new(stream, _plan, leaveOpen: true);
                await using (faulted.ConfigureAwait(false))
                {
                    await write(faulted, token).ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default) =>
        _inner.ExistsAsync(path, cancellationToken);

    /// <inheritdoc />
    public ValueTask<FileInfoSnapshot?> GetInfoAsync(
        VPath path,
        CancellationToken cancellationToken = default) =>
        _inner.GetInfoAsync(path, cancellationToken);

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

    private sealed class FaultStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private readonly FaultPlan _plan;
        private long _read;
        private long _written;

        public FaultStream(Stream inner, FaultPlan plan, bool leaveOpen)
        {
            _inner = inner;
            _plan = plan;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int allowed = BeforeRead(buffer.Length);
            int got = allowed == 0 ? 0 : _inner.Read(buffer[..allowed]);
            _read += got;
            return got;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int allowed = BeforeRead(buffer.Length);
            int got = allowed == 0
                ? 0
                : await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);

            _read += got;
            return got;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int allowed = BeforeWrite(buffer.Length);
            _inner.Write(buffer[..allowed]);
            _written += allowed;
            AfterWrite(buffer.Length, allowed);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int allowed = BeforeWrite(buffer.Length);
            await _inner.WriteAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
            _written += allowed;
            AfterWrite(buffer.Length, allowed);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_leaveOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int BeforeRead(int wanted)
        {
            if (_plan.CancelReadAfterBytes is long cancelAfter && _read >= cancelAfter)
            {
                throw new OperationCanceledException("the read was cancelled part-way");
            }

            if (_plan.ShortReadAfterBytes is not long shortAfter)
            {
                return wanted;
            }

            long remaining = shortAfter - _read;
            return remaining <= 0 ? 0 : (int)Math.Min(wanted, remaining);
        }

        private int BeforeWrite(int wanted)
        {
            if (_plan.FailWriteAfterBytes is not long failAfter)
            {
                return wanted;
            }

            long remaining = failAfter - _written;
            if (remaining <= 0)
            {
                throw new IOException(_plan.WriteFailureMessage);
            }

            return (int)Math.Min(wanted, remaining);
        }

        private void AfterWrite(int wanted, int allowed)
        {
            if (allowed < wanted)
            {
                // The partial write reached the stream, exactly as a filling
                // disk writes what fits before it refuses the rest.
                throw new IOException(_plan.WriteFailureMessage);
            }
        }
    }
}
