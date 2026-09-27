//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.Tests.MapTools.Rad.Bounce;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="StoreTransferCache"/>: transfers stored by one compile replay in
/// the next, only the newest set is kept, and a damaged row is a miss.
/// </summary>
public sealed class StoreTransferCacheTests
{
    private static async Task<InMemoryCacheStore> StoreAsync()
    {
        InMemoryCacheStore store = new();
        await store.OpenAsync("memory");
        return store;
    }

    private static async Task StoreAndCommitAsync(ICacheStore store, string key, TransferSet set, long costMs = 7)
    {
        StoreTransferCache cache = new(store, CachePolicy.Default, [], new CacheRunCounters());
        await cache.StoreAsync(key, set, costMs, CancellationToken.None);
        await cache.FlushAsync();
        await store.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoredTransfersReplayInTheNextCompile()
    {
        InMemoryCacheStore store = await StoreAsync();
        await StoreAndCommitAsync(store, "geometry-a", TransferSetCodecTests.Sample());

        CacheRunCounters counters = new();
        TransferSet? hit = await new StoreTransferCache(store, CachePolicy.Default, [], counters)
            .TryGetAsync("geometry-a", 4, CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal(TransferSetCodecTests.Sample().For(2).ToArray(), hit.For(2).ToArray());
        Assert.Equal(["vrad.transfers"], counters.StageHits);
        Assert.Equal(7, counters.EstimatedSavedMs);
    }

    [Fact]
    public async Task AnotherKeyMisses()
    {
        InMemoryCacheStore store = await StoreAsync();
        await StoreAndCommitAsync(store, "geometry-a", TransferSetCodecTests.Sample());

        CacheRunCounters counters = new();
        Assert.Null(await new StoreTransferCache(store, CachePolicy.Default, [], counters)
            .TryGetAsync("geometry-b", 4, CancellationToken.None));
        Assert.Equal(["vrad.transfers"], counters.StageMisses);
    }

    [Fact]
    public async Task OnlyTheNewestSetIsKept()
    {
        InMemoryCacheStore store = await StoreAsync();
        await StoreAndCommitAsync(store, "geometry-a", TransferSetCodecTests.Sample());
        await StoreAndCommitAsync(store, "geometry-b", new TransferSet([new(0, 1f)], [0, 1], [1, 0], 1));

        StoreTransferCache cache = new(store, CachePolicy.Default, [], new CacheRunCounters());
        Assert.Null(await cache.TryGetAsync("geometry-a", 4, CancellationToken.None));
        Assert.NotNull(await cache.TryGetAsync("geometry-b", 2, CancellationToken.None));
        Assert.Single(await store.KeysAsync(CancellationToken.None));
        Assert.Empty(await store.CollectGarbageAsync(100, CancellationToken.None));
    }

    [Fact]
    public async Task StoringTheSameSetAgainKeepsItsBlobs()
    {
        InMemoryCacheStore store = await StoreAsync();
        await StoreAndCommitAsync(store, "geometry-a", TransferSetCodecTests.Sample());
        await StoreAndCommitAsync(store, "geometry-a2", TransferSetCodecTests.Sample());

        Assert.NotNull(await new StoreTransferCache(store, CachePolicy.Default, [], new CacheRunCounters())
            .TryGetAsync("geometry-a2", 4, CancellationToken.None));
    }

    [Fact]
    public async Task AReadOnlyPostureWritesNothing()
    {
        InMemoryCacheStore store = await StoreAsync();
        CacheRunCounters counters = new();
        StoreTransferCache cache = new(store, CachePolicy.ReadOnly, [], counters);
        await cache.StoreAsync("geometry-a", TransferSetCodecTests.Sample(), 1, CancellationToken.None);
        await cache.FlushAsync();
        await store.CommitAsync(CancellationToken.None);

        Assert.Empty(await store.KeysAsync(CancellationToken.None));
        Assert.Equal(1, counters.WriteSkips);
    }

    [Fact]
    public async Task ADamagedChunkIsAMiss()
    {
        InMemoryCacheStore store = await StoreAsync();
        await StoreAndCommitAsync(store, "geometry-a", TransferSetCodecTests.Sample());
        string key = (await store.KeysAsync(CancellationToken.None)).Single();
        CacheRecord record = (await store.LookupAsync(key, CancellationToken.None))!;
        await store.DeleteBlobsAsync([record.Blobs["chunk0"]], CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);

        CacheRunCounters counters = new();
        Assert.Null(await new StoreTransferCache(store, CachePolicy.Default, [], counters)
            .TryGetAsync("geometry-a", 4, CancellationToken.None));
        Assert.Equal(1, counters.CorruptRows);
    }

    [Fact]
    public void TheReportNamesReusedStages()
    {
        CacheRunCounters counters = new();
        counters.StageHit("vrad.transfers", 1024, 5000);

        Assert.Contains("stages reused: vrad.transfers", CacheRunReport.Render(counters), StringComparison.Ordinal);
    }
}
