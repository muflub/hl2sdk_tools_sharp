//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;

namespace SourceSharp.Tests.MapTools.Compile;

/// <summary>
/// The compile's disk with seams: every call goes to an
/// <see cref="InMemoryFileSystem"/>; <see cref="BeforeReplace"/> runs
/// before each whole-file write, where a fact can wait for something or
/// throw to fail that write; <see cref="BeforeOpenRead"/> does the same for
/// a stream open, and <see cref="OpenReads"/> counts the read streams not
/// yet closed, which is how a fact sees a leaked handle.
/// </summary>
/// <param name="inner">The disk the calls reach.</param>
internal sealed class ProbeFileSystem(InMemoryFileSystem inner) : IFileSystem
{
    private int _openReads;
    private int _opened;

    /// <summary>Runs before every <see cref="ReplaceAsync"/>; may throw.</summary>
    public Func<VPath, Task>? BeforeReplace { get; init; }

    /// <summary>Runs before every <see cref="OpenReadAsync"/>; may wait or throw.</summary>
    public Func<VPath, Task>? BeforeOpenRead { get; init; }

    /// <summary>Read streams opened and not yet disposed.</summary>
    public int OpenReads => Volatile.Read(ref _openReads);

    /// <summary>Read streams opened in all.</summary>
    public int Opened => Volatile.Read(ref _opened);

    public async ValueTask<Stream> OpenReadAsync(VPath path, CancellationToken cancellationToken = default)
    {
        if (BeforeOpenRead is { } before)
        {
            await before(path);
        }

        Stream stream = await inner.OpenReadAsync(path, cancellationToken);
        Interlocked.Increment(ref _openReads);
        Interlocked.Increment(ref _opened);
        return new CountedStream(stream, () => Interlocked.Decrement(ref _openReads));
    }

    // A read stream that reports its close once.
    private sealed class CountedStream(Stream inner, Action closed) : Stream
    {
        private int _closed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                inner.Dispose();
                closed();
            }

            base.Dispose(disposing);
        }
    }

    public ValueTask<IMemoryOwner<byte>> ReadAllAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.ReadAllAsync(path, cancellationToken);

    public ValueTask<FileRange> ReadRangeAsync(
        VPath path, long offset, int length, CancellationToken cancellationToken = default) =>
        inner.ReadRangeAsync(path, offset, length, cancellationToken);

    public ValueTask<Stream> OpenWriteAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.OpenWriteAsync(path, cancellationToken);

    public async ValueTask ReplaceAsync(
        VPath path, Func<Stream, CancellationToken, ValueTask> write, CancellationToken cancellationToken = default)
    {
        if (BeforeReplace is { } before)
        {
            await before(path);
        }

        await inner.ReplaceAsync(path, write, cancellationToken);
    }

    public ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(path, cancellationToken);

    public ValueTask<FileInfoSnapshot?> GetInfoAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.GetInfoAsync(path, cancellationToken);

    public IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory, string searchPattern = "*", bool recursive = false, CancellationToken cancellationToken = default) =>
        inner.EnumerateAsync(directory, searchPattern, recursive, cancellationToken);

    public ValueTask DeleteAsync(VPath path, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(path, cancellationToken);
}

/// <summary>
/// A cache store with seams: every call goes to an <see cref="InMemoryCacheStore"/>,
/// and a fact can hold a blob write, fail the stats read, or count vacuums.
/// </summary>
/// <param name="inner">The store the calls reach.</param>
internal sealed class ProbeCacheStore(InMemoryCacheStore inner) : ICacheStore
{
    private int _putBlobsInFlight;
    private int _vacuums;

    /// <summary>The store behind the seams.</summary>
    public InMemoryCacheStore Inner => inner;

    /// <summary>Runs inside every <see cref="PutBlobAsync"/>, before the blob is staged; may wait or throw.</summary>
    public Func<string, CancellationToken, Task>? BeforePutBlob { get; set; }

    /// <summary>When set, <see cref="ReadStatsAsync"/> throws it.</summary>
    public Exception? FailReadStats { get; set; }

    /// <summary>How many <see cref="PutBlobAsync"/> calls are running now.</summary>
    public int PutBlobsInFlight => Volatile.Read(ref _putBlobsInFlight);

    /// <summary>How many times <see cref="VacuumAsync"/> ran.</summary>
    public int Vacuums => Volatile.Read(ref _vacuums);

    public bool IsUsable => inner.IsUsable;

    public int RunsInFlight => inner.RunsInFlight;

    public IDisposable BeginRun() => inner.BeginRun();

    public ValueTask OpenAsync(string location, CancellationToken cancellationToken) => inner.OpenAsync(location, cancellationToken);

    public ValueTask<CacheRecord?> LookupAsync(string key, CancellationToken cancellationToken) => inner.LookupAsync(key, cancellationToken);

    public ValueTask<IReadOnlyList<string>> KeysAsync(CancellationToken cancellationToken) => inner.KeysAsync(cancellationToken);

    public ValueTask<IReadOnlyList<string>> FindKeysByDepAsync(string path, CancellationToken cancellationToken) =>
        inner.FindKeysByDepAsync(path, cancellationToken);

    public ValueTask<IReadOnlyList<string>> LiveToolIdsAsync(CancellationToken cancellationToken) => inner.LiveToolIdsAsync(cancellationToken);

    public ValueTask PutAsync(CacheRecord record, CancellationToken cancellationToken) => inner.PutAsync(record, cancellationToken);

    public async ValueTask PutBlobAsync(
        string blobKey, ReadOnlyMemory<byte> data, string toolId, long createdAtMs, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _putBlobsInFlight);
        try
        {
            if (BeforePutBlob is { } before)
            {
                await before(blobKey, cancellationToken);
            }

            await inner.PutBlobAsync(blobKey, data, toolId, createdAtMs, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _putBlobsInFlight);
        }
    }

    public ValueTask<bool> HasBlobAsync(string blobKey, CancellationToken cancellationToken) => inner.HasBlobAsync(blobKey, cancellationToken);

    public ValueTask<long?> BlobSizeAsync(string blobKey, CancellationToken cancellationToken) => inner.BlobSizeAsync(blobKey, cancellationToken);

    public ValueTask<byte[]?> GetBlobAsync(string blobKey, CancellationToken cancellationToken) => inner.GetBlobAsync(blobKey, cancellationToken);

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

    public ValueTask CommitAsync(CancellationToken cancellationToken) => inner.CommitAsync(cancellationToken);

    public void DiscardStaged() => inner.DiscardStaged();

    public ValueTask ClearAsync(CancellationToken cancellationToken) => inner.ClearAsync(cancellationToken);

    public ValueTask<IReadOnlyList<string>> GenerationsAsync(CancellationToken cancellationToken) => inner.GenerationsAsync(cancellationToken);

    public ValueTask RecordGenerationAsync(string generationId, CancellationToken cancellationToken) =>
        inner.RecordGenerationAsync(generationId, cancellationToken);

    public ValueTask RemoveGenerationAsync(string generationId, CancellationToken cancellationToken) =>
        inner.RemoveGenerationAsync(generationId, cancellationToken);

    public ValueTask<CacheStats> ReadStatsAsync(CancellationToken cancellationToken) =>
        FailReadStats is { } failure ? throw failure : inner.ReadStatsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<string>> CollectGarbageAsync(int maxCount, CancellationToken cancellationToken) =>
        inner.CollectGarbageAsync(maxCount, cancellationToken);

    public ValueTask DeleteBlobsAsync(IReadOnlyList<string> blobKeys, CancellationToken cancellationToken) =>
        inner.DeleteBlobsAsync(blobKeys, cancellationToken);

    public ValueTask VacuumAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _vacuums);
        return inner.VacuumAsync(cancellationToken);
    }

    public ValueTask<bool> CheckIntegrityAsync(CancellationToken cancellationToken) => inner.CheckIntegrityAsync(cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>
/// Watches the early vvis flow of an overlapped compile through its progress
/// reports: the first vvis report is held for <see cref="Hold"/> on the
/// flow's own thread, and every vvis report after <see cref="MarkReturned"/>
/// is counted. A compile that returns while the flow is still running is
/// seen either inside the hold or in a report after the return.
/// </summary>
/// <param name="other">Receives every other report (a canceller, say), or null.</param>
internal sealed class FlowWatch(Action<CompileProgress>? other = null) : IProgress<CompileProgress>
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;
    private int _inside;
    private int _returned;
    private int _afterReturn;

    /// <summary>How long the first vvis report is held.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(2);

    /// <summary>Completes when the flow has made its first report.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Whether the flow is inside the held report now.</summary>
    public bool FlowIsHeld => Volatile.Read(ref _inside) != 0;

    /// <summary>How many vvis reports arrived after <see cref="MarkReturned"/>.</summary>
    public int ReportsAfterReturn => Volatile.Read(ref _afterReturn);

    /// <summary>Records that the compile has returned.</summary>
    public void MarkReturned() => Volatile.Write(ref _returned, 1);

    public void Report(CompileProgress value)
    {
        if (!value.Stage.StartsWith("vvis.", StringComparison.Ordinal))
        {
            other?.Invoke(value);
            return;
        }

        if (Volatile.Read(ref _returned) != 0)
        {
            Interlocked.Increment(ref _afterReturn);
        }

        if (Interlocked.Exchange(ref _held, 1) == 0)
        {
            Volatile.Write(ref _inside, 1);
            _entered.TrySetResult();
            Thread.Sleep(Hold);
            Volatile.Write(ref _inside, 0);
        }
    }
}
