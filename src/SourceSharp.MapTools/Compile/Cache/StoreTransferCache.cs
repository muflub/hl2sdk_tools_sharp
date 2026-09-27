//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rad.Bounce;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The store-backed <see cref="ITransferCache"/>: one row per transfer set,
/// its index and chunks as content-addressed blobs, re-hashed on replay.
/// </summary>
/// <remarks>
/// <para>
/// A transfer set is large (tens of millions of transfers is hundreds of
/// megabytes), so the store keeps only the newest: storing a set stages the
/// deletion of every older transfer row and the blobs only they held. The
/// edit loop this serves is "move a light, recompile", which reads the
/// previous compile's set, never an older one.
/// </para>
/// <para>
/// Chunks are at most <see cref="CachePolicy.MaxBlobBytes"/> each. Anything
/// suspect on replay (a missing or altered blob, an index that does not fit
/// its chunks, a patch out of range) is a miss and a corrupt row.
/// </para>
/// </remarks>
public sealed class StoreTransferCache : ITransferCache
{
    /// <summary>The stage name the rows carry.</summary>
    public const string StageName = "vrad.transfers";

    private readonly ICacheStore _store;
    private readonly CachePolicy _policy;
    private readonly IReadOnlyList<string> _contextTags;
    private readonly CacheRunCounters _counters;
    private readonly long _createdAtMs;
    private readonly int _maxDegree;

    /// <summary>Builds the seam over one store for one run.</summary>
    /// <param name="store">The open store.</param>
    /// <param name="policy">Size/mode policy.</param>
    /// <param name="contextTags">The host's opaque tags, folded verbatim.</param>
    /// <param name="counters">The run's report counters.</param>
    /// <param name="maxDegree">How many chunks pack or unpack at once (the compile's <c>-threads</c>).</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public StoreTransferCache(
        ICacheStore store,
        CachePolicy policy,
        IReadOnlyList<string> contextTags,
        CacheRunCounters counters,
        int maxDegree = -1)
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
        _createdAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>The full key: the bounce's digest under the shared <see cref="CacheKey"/> fold.</summary>
    /// <param name="transferKey">The digest <c>RadWorld</c> computed.</param>
    /// <returns>The key.</returns>
    internal CacheKey KeyOf(string transferKey) => new()
    {
        Stage = StageName,
        ToolId = ToolIdentity.Current,
        SemanticDigest = transferKey,
        OptionsDigest = string.Empty,
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

        _counters.StageHit(StageName, bytes, record.CostMs);
        return set;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Packing and staging run in the background, overlapping the rest of
    /// vrad; <see cref="FlushAsync"/> waits for them before the store commits.
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
        _pending = Task.Run(
            async () =>
            {
                await previous.ConfigureAwait(false);
                await StageAsync(key, transfers, costMs, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
        return default;
    }

    /// <summary>Waits for every store <see cref="StoreAsync"/> started; its failure surfaces here.</summary>
    /// <param name="cancellationToken">Stops waiting (the staging itself runs under its own token).</param>
    /// <returns>A task that completes when the rows are staged.</returns>
    public Task FlushAsync(CancellationToken cancellationToken = default) => _pending.WaitAsync(cancellationToken);

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
        byte[][] raw = [.. TransferSetCodec.Chunks(transfers, _policy.MaxBlobBytes)];
        byte[][] packed = new byte[raw.Length][];
        System.Threading.Tasks.Parallel.For(0, raw.Length, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = _maxDegree }, n =>
        {
            packed[n] = TransferSetCodec.Pack(raw[n]);
            raw[n] = [];
        });

        for (int n = 0; n < packed.Length; n++)
        {
            blobs[$"chunk{n}"] = await StageBlobAsync(packed[n], cancellationToken).ConfigureAwait(false);
            bytes += packed[n].Length;
        }

        await EvictOlderAsync(full.Digest, [.. blobs.Values], cancellationToken).ConfigureAwait(false);
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
                CreatedAtMs: _createdAtMs),
            cancellationToken).ConfigureAwait(false);

        _counters.Stored(bytes);
    }

    // Stages the deletion of every other transfer row and the blobs only it
    // held (a chunk the new set also holds is content-addressed and kept).
    private async ValueTask EvictOlderAsync(string keep, HashSet<string> keepBlobs, CancellationToken cancellationToken)
    {
        List<string> dropBlobs = [];
        foreach (string key in await _store.KeysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (key == keep)
            {
                continue;
            }

            CacheRecord? old = await _store.LookupAsync(key, cancellationToken).ConfigureAwait(false);
            if (old is null || old.Stage != StageName)
            {
                continue;
            }

            await _store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            dropBlobs.AddRange(old.Blobs.Values.Where(b => !keepBlobs.Contains(b)));
        }

        if (dropBlobs.Count > 0)
        {
            await _store.DeleteBlobsAsync([.. dropBlobs.Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
        }
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

        byte[]?[] chunks = new byte[packed.Count][];
        System.Threading.Tasks.Parallel.For(0, packed.Count, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = _maxDegree }, n =>
            chunks[n] = TransferSetCodec.Unpack(packed[n]));
        if (chunks.Any(c => c is null))
        {
            return (null, 0);
        }

        return (TransferSetCodec.Read(index, chunks!, patchCount), bytes);
    }

    private async ValueTask<string> StageBlobAsync(byte[] data, CancellationToken cancellationToken)
    {
        string blobKey = CacheKey.HashBytes(data);
        if (!await _store.HasBlobAsync(blobKey, cancellationToken).ConfigureAwait(false))
        {
            await _store.PutBlobAsync(blobKey, data, ToolIdentity.Current, _createdAtMs, cancellationToken)
                .ConfigureAwait(false);
        }

        return blobKey;
    }

    private async ValueTask<byte[]?> ReadCheckedAsync(string blobKey, CancellationToken cancellationToken)
    {
        if (!CacheKey.LooksLikeDigest(blobKey))
        {
            return null;
        }

        byte[]? data = await _store.GetBlobAsync(blobKey, cancellationToken).ConfigureAwait(false);
        return data is not null && CacheKey.HashBytes(data) == blobKey ? data : null;
    }
}
