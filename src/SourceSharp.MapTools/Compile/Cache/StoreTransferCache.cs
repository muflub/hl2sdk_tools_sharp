//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapTools.Rad.Bounce;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The store-backed <see cref="ITransferCache"/>: one row per transfer set,
/// its index and chunks as content-addressed blobs, re-hashed on replay.
/// </summary>
/// <remarks>
/// <para>
/// A transfer set is large (tens of millions of transfers is hundreds of
/// megabytes), so the store keeps only the newest set of each map: storing a
/// set stages the deletion of the older transfer rows of the same map and
/// the same context tags, and of the blobs only they held. The edit loop
/// this serves is "move a light, recompile", which reads the previous
/// compile's set of that map, never an older one. Rows of other maps (a
/// service that compiles many maps into one store, or two compiles at once)
/// are not touched: evicting them would make alternating maps miss every
/// time. What bounds the store as a whole is the size GC
/// (<see cref="CacheCollector"/>), not this eviction.
/// </para>
/// <para>
/// The map is folded into the key's options part (<see cref="ScopeOf"/>), so
/// that a row names the map it belongs to and the eviction can find the same
/// map's older rows without reading every blob. The transfers themselves do
/// not depend on the map's name; the price is that a renamed map misses once.
/// </para>
/// <para>
/// Chunks are at most <see cref="CachePolicy.MaxBlobBytes"/> each. Anything
/// suspect on replay (a missing or altered blob, an index that does not fit
/// its chunks, a patch out of range) is a miss and a corrupt row.
/// </para>
/// <para>
/// Staging runs in the background (<see cref="StoreAsync"/>), under this
/// seam's own cancellation as well as the caller's, so that a compile that
/// fails or is cancelled after the store can stop it and wait for it
/// (<see cref="AbandonAsync"/>) instead of leaving it to run on, holding the
/// whole set, after the compile has returned.
/// </para>
/// </remarks>
public sealed class StoreTransferCache : ITransferCache, IDisposable
{
    /// <summary>The stage name the rows carry.</summary>
    public const string StageName = "vrad.transfers";

    private readonly ICacheStore _store;
    private readonly CachePolicy _policy;
    private readonly IReadOnlyList<string> _contextTags;
    private readonly CacheRunCounters _counters;
    private readonly int _maxDegree;
    private readonly string _scope;
    private readonly CancellationTokenSource _abandon = new();

    /// <summary>
    /// The run's generation stamp (Unix milliseconds) every row and blob this
    /// seam stages carries; the time the seam was built unless the chain sets
    /// it.
    /// </summary>
    /// <remarks>
    /// The chain gives all of a compile's seams its one start stamp and
    /// records it as the store's generation on commit, so the GC can tell
    /// which rows the newest <see cref="CachePolicy.GenerationsKept"/>
    /// compiles made (<see cref="CacheCollector"/>). Seams built each with
    /// their own clock reading would scatter one compile over several stamps.
    /// </remarks>
    public long CreatedAtMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Builds the seam over one store for one run.</summary>
    /// <param name="store">The open store.</param>
    /// <param name="policy">Size/mode policy.</param>
    /// <param name="contextTags">The host's opaque tags, folded verbatim.</param>
    /// <param name="counters">The run's report counters.</param>
    /// <param name="maxDegree">How many chunks pack or unpack at once (the compile's <c>-threads</c>).</param>
    /// <param name="mapName">
    /// The map the transfers are of; storing a set evicts only this map's
    /// older rows. Null for a seam that is not tied to a map (the unit
    /// facts), whose rows share one scope.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public StoreTransferCache(
        ICacheStore store,
        CachePolicy policy,
        IReadOnlyList<string> contextTags,
        CacheRunCounters counters,
        int maxDegree = -1,
        string? mapName = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(contextTags);
        ArgumentNullException.ThrowIfNull(counters);

        _store = store;
        _policy = policy;
        _contextTags = contextTags;
        _counters = counters;
        _maxDegree = maxDegree > 0 ? maxDegree : -1;
        _scope = ScopeOf(mapName);
    }

    /// <summary>The key part that names a row's map: empty when there is none.</summary>
    /// <param name="mapName">The map, or null.</param>
    /// <returns>A digest of the name, or the empty string.</returns>
    internal static string ScopeOf(string? mapName) =>
        mapName is null ? string.Empty : CacheKey.HashComponents(["map", mapName]);

    /// <summary>The full key: the bounce's digest under the shared <see cref="CacheKey"/> fold.</summary>
    /// <param name="transferKey">The digest <c>RadWorld</c> computed.</param>
    /// <returns>The key.</returns>
    internal CacheKey KeyOf(string transferKey) => new()
    {
        Stage = StageName,
        ToolId = ToolIdentity.Current,
        SemanticDigest = transferKey,
        OptionsDigest = _scope,
        DependencyDigest = string.Empty,
        ContextTags = _contextTags,
    };

    /// <inheritdoc />
    public async ValueTask<TransferSet?> TryGetAsync(string key, int patchCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_policy.Reads || !_store.IsUsable)
        {
            _counters.SkipRead();
            return null;
        }

        CacheKey full = KeyOf(key);
        CacheRecord? record = await _store.LookupAsync(full.Digest, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            _counters.StageMiss(StageName);
            return null;
        }

        (TransferSet? set, long bytes) = await ReplayAsync(record, full, patchCount, cancellationToken).ConfigureAwait(false);
        if (set is null)
        {
            _counters.Corrupt();
            _counters.StageMiss(StageName);
            return null;
        }

        await CacheBlobReader.RenewAsync(_store, _policy, record, CreatedAtMs, cancellationToken).ConfigureAwait(false);
        _counters.StageHit(StageName, bytes, record.CostMs);
        return set;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Packing and staging run in the background, overlapping the rest of
    /// vrad; <see cref="FlushAsync"/> waits for them before the store commits,
    /// and <see cref="AbandonAsync"/> stops them when the compile does not get
    /// that far.
    /// </remarks>
    public ValueTask StoreAsync(string key, TransferSet transfers, long costMs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(transfers);
        if (!_policy.Writes || !_store.IsUsable)
        {
            _counters.SkipWrite();
            return default;
        }

        Task previous = _pending;
        CancellationToken abandon = _abandon.Token;
        _pending = Task.Run(
            async () =>
            {
                await previous.ConfigureAwait(false);
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, abandon);
                await StageAsync(key, transfers, costMs, linked.Token).ConfigureAwait(false);
            },
            CancellationToken.None);
        return default;
    }

    /// <summary>Waits for every store <see cref="StoreAsync"/> started; its failure surfaces here.</summary>
    /// <param name="cancellationToken">Stops waiting (the staging itself runs under its own token).</param>
    /// <returns>A task that completes when the rows are staged.</returns>
    public Task FlushAsync(CancellationToken cancellationToken = default) => _pending.WaitAsync(cancellationToken);

    /// <summary>
    /// Stops the background staging and waits until it has stopped; what it
    /// staged stays staged, for the caller to discard with the store's other
    /// uncommitted changes.
    /// </summary>
    /// <param name="cancellationToken">
    /// Stops waiting (the staging is cancelled either way); only this token's
    /// cancellation is thrown.
    /// </param>
    /// <returns>A task that completes once no staging runs.</returns>
    /// <remarks>
    /// Throws nothing of the staging's: the compile calling it is already
    /// failing, and the staging's own failure (its cancellation included) is
    /// of no further use.
    /// </remarks>
    public async Task AbandonAsync(CancellationToken cancellationToken = default)
    {
        await _abandon.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // The staging was stopped, or had already failed; either way
            // nothing it staged will be published.
        }
    }

    /// <summary>
    /// How many older rows of this map the staging evicted, so far. The chain
    /// reads it once the staging is flushed: under
    /// <see cref="CacheVacuum.EveryRun"/> an eviction is a reclaim.
    /// </summary>
    public int EvictedRows => Volatile.Read(ref _evicted);

    private int _evicted;

    /// <summary>Whether no staging is running (the facts' view of <see cref="AbandonAsync"/>).</summary>
    internal bool IsIdle => _pending.IsCompleted;

    /// <summary>Releases the cancellation source; call after the staging has been flushed or abandoned.</summary>
    public void Dispose() => _abandon.Dispose();

    private Task _pending = Task.CompletedTask;

    private async Task StageAsync(string key, TransferSet transfers, long costMs, CancellationToken cancellationToken)
    {
        CacheKey full = KeyOf(key);
        Dictionary<string, string> blobs = [];
        long bytes = 0;

        byte[] index = TransferSetCodec.Index(transfers);
        blobs["index"] = await StageBlobAsync(index, cancellationToken).ConfigureAwait(false);
        bytes += index.Length;

        // Packing is the costly part of a store, and chunks pack independently.
        // The raw chunks are cut as the packers ask for them, so only about as
        // many raw chunks as there are packers are alive at once, not the
        // whole set a second time.
        ConcurrentDictionary<int, byte[]> packed = new();
        System.Threading.Tasks.Parallel.ForEach(
            System.Collections.Concurrent.Partitioner.Create(
                TransferSetCodec.Chunks(transfers, _policy.MaxBlobBytes).Select(static (chunk, n) => (Chunk: chunk, N: n)),
                EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = _maxDegree },
            item => packed[item.N] = TransferSetCodec.Pack(item.Chunk));

        for (int n = 0; n < packed.Count; n++)
        {
            blobs[$"chunk{n}"] = await StageBlobAsync(packed[n], cancellationToken).ConfigureAwait(false);
            bytes += packed[n].Length;
        }

        await EvictOlderAsync(full, [.. blobs.Values], cancellationToken).ConfigureAwait(false);
        await _store.PutAsync(
            new CacheRecord(
                full.Digest,
                full.Stage,
                full.ToolId,
                full.ContextTags,
                full.Parts,
                blobs,
                [],
                CostMs: costMs,
                CreatedAtMs: CreatedAtMs),
            cancellationToken).ConfigureAwait(false);

        _counters.Stored(bytes);
    }

    // Stages the deletion of the other transfer rows of the same map and the
    // same context tags, whatever tool made them, and the blobs only they
    // held (a chunk the new set also holds is content-addressed and kept).
    // Every other row, another map's transfers included, is left alone.
    private async ValueTask EvictOlderAsync(CacheKey keep, HashSet<string> keepBlobs, CancellationToken cancellationToken)
    {
        List<string> dropBlobs = [];
        foreach (string key in await _store.KeysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (key == keep.Digest)
            {
                continue;
            }

            CacheRecord? old = await _store.LookupAsync(key, cancellationToken).ConfigureAwait(false);
            if (old is null || !IsSameScope(old, keep))
            {
                continue;
            }

            await _store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _evicted);
            dropBlobs.AddRange(old.Blobs.Values.Where(b => !keepBlobs.Contains(b)));
        }

        if (dropBlobs.Count > 0)
        {
            await _store.DeleteBlobsAsync([.. dropBlobs.Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
        }
    }

    // A transfer row of the same map and context: the eviction's whole scope.
    private static bool IsSameScope(CacheRecord row, CacheKey keep)
    {
        if (row.Stage != StageName || !row.ContextTags.SequenceEqual(keep.ContextTags, StringComparer.Ordinal))
        {
            return false;
        }

        foreach ((string name, string value) in row.Parts)
        {
            if (name == "options")
            {
                return value == keep.OptionsDigest;
            }
        }

        return false;
    }

    private async ValueTask<(TransferSet? Set, long Bytes)> ReplayAsync(
        CacheRecord record, CacheKey key, int patchCount, CancellationToken cancellationToken)
    {
        if (record.Stage != key.Stage
            || record.ToolId != key.ToolId
            || record.Key != key.Digest
            || !record.Parts.SequenceEqual(key.Parts)
            || !record.ContextTags.SequenceEqual(key.ContextTags)
            || !record.Blobs.TryGetValue("index", out string? indexKey))
        {
            return (null, 0);
        }

        byte[]? index = await ReadCheckedAsync(indexKey, cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            return (null, 0);
        }

        long bytes = index.Length;
        List<byte[]> packed = [];
        for (int n = 0; record.Blobs.TryGetValue($"chunk{n}", out string? chunkKey); n++)
        {
            byte[]? chunk = await ReadCheckedAsync(chunkKey, cancellationToken).ConfigureAwait(false);
            if (chunk is null)
            {
                return (null, 0);
            }

            packed.Add(chunk);
            bytes += chunk.Length;
        }

        // Unpacked straight into the set's one arena, each packed chunk let go
        // once it is in: the peak is the packed bytes plus the arena, not also
        // a raw copy of every chunk.
        byte[]?[] chunks = [.. packed];
        packed.Clear();
        TransferSet? set = TransferSetCodec.ReadPacked(
            index,
            chunks,
            patchCount,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = _maxDegree });
        return (set, set is null ? 0 : bytes);
    }

    private async ValueTask<string> StageBlobAsync(byte[] data, CancellationToken cancellationToken)
    {
        string blobKey = CacheKey.HashBytes(data);
        if (!await _store.HasBlobAsync(blobKey, cancellationToken).ConfigureAwait(false))
        {
            await _store.PutBlobAsync(blobKey, data, ToolIdentity.Current, CreatedAtMs, cancellationToken)
                .ConfigureAwait(false);
        }

        return blobKey;
    }

    private ValueTask<byte[]?> ReadCheckedAsync(string blobKey, CancellationToken cancellationToken) =>
        CacheBlobReader.ReadAsync(_store, _policy, blobKey, cancellationToken);
}
