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
        await StoreAndCommitAsync(store, "geometry-b", new TransferSet([[new(0, 1f)]], [0, 0], [0, 1], [1, 0], 1));

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

    private static async Task StoreForAsync(ICacheStore store, string? map, IReadOnlyList<string> tags, string key, TransferSet set)
    {
        using StoreTransferCache cache = new(store, CachePolicy.Default, tags, new CacheRunCounters(), mapName: map);
        await cache.StoreAsync(key, set, 1, CancellationToken.None);
        await cache.FlushAsync();
        await store.CommitAsync(CancellationToken.None);
    }

    private static async Task<TransferSet?> LookupForAsync(ICacheStore store, string? map, IReadOnlyList<string> tags, string key, int patches)
    {
        using StoreTransferCache cache = new(store, CachePolicy.Default, tags, new CacheRunCounters(), mapName: map);
        return await cache.TryGetAsync(key, patches, CancellationToken.None);
    }

    private static TransferSet Small() => new([[new(0, 1f)]], [0, 0], [0, 1], [1, 0], 1);

    [Fact]
    public async Task StoringOneMapsTransfersKeepsAnotherMaps()
    {
        // One store, two maps compiled in turn: each keeps its own newest set,
        // so alternating between them hits every time.
        InMemoryCacheStore store = await StoreAsync();
        await StoreForAsync(store, "map_a", [], "geometry-a", TransferSetCodecTests.Sample());
        await StoreForAsync(store, "map_b", [], "geometry-b", Small());

        Assert.NotNull(await LookupForAsync(store, "map_a", [], "geometry-a", 4));
        Assert.NotNull(await LookupForAsync(store, "map_b", [], "geometry-b", 2));
    }

    [Fact]
    public async Task StoringUnderOtherContextTagsKeepsTheOtherTagsSet()
    {
        InMemoryCacheStore store = await StoreAsync();
        await StoreForAsync(store, "map_a", ["preset=fast"], "geometry-a", TransferSetCodecTests.Sample());
        await StoreForAsync(store, "map_a", ["preset=final"], "geometry-b", Small());

        Assert.NotNull(await LookupForAsync(store, "map_a", ["preset=fast"], "geometry-a", 4));
        Assert.NotNull(await LookupForAsync(store, "map_a", ["preset=final"], "geometry-b", 2));
    }

    [Fact]
    public async Task StoringAMapsNewSetEvictsItsOldOne()
    {
        InMemoryCacheStore store = await StoreAsync();
        await StoreForAsync(store, "map_a", [], "geometry-a", TransferSetCodecTests.Sample());
        await StoreForAsync(store, "map_b", [], "geometry-b", Small());

        using StoreTransferCache cache = new(store, CachePolicy.Default, [], new CacheRunCounters(), mapName: "map_a");
        await cache.StoreAsync("geometry-a2", Small(), 1, CancellationToken.None);
        await cache.FlushAsync();
        await store.CommitAsync(CancellationToken.None);

        Assert.Equal(1, cache.EvictedRows);
        Assert.Null(await LookupForAsync(store, "map_a", [], "geometry-a", 4));
        Assert.NotNull(await LookupForAsync(store, "map_a", [], "geometry-a2", 2));
        Assert.NotNull(await LookupForAsync(store, "map_b", [], "geometry-b", 2));
        Assert.Equal(2, (await store.KeysAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task EvictionLeavesOtherStagesAlone()
    {
        InMemoryCacheStore store = await StoreAsync();
        string other = await CacheCollectorTests.PutRowAsync(store, "collision-row", DateTimeOffset.UnixEpoch, 10);

        await StoreForAsync(store, "map_a", [], "geometry-a", Small());

        Assert.NotNull(await store.LookupAsync(other, CancellationToken.None));
    }

    [Fact]
    public async Task AbandoningStopsARunningStagingAndWaitsForIt()
    {
        InMemoryCacheStore inner = await StoreAsync();
        ProbeCacheStore store = new(inner);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforePutBlob = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        using StoreTransferCache cache = new(store, CachePolicy.Default, [], new CacheRunCounters());
        await cache.StoreAsync("geometry-a", TransferSetCodecTests.Sample(), 1, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(cache.IsIdle);

        await cache.AbandonAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(cache.IsIdle);
        Assert.Equal(0, store.PutBlobsInFlight);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.FlushAsync());
    }

    [Fact]
    public async Task AbandoningWithNothingStagedIsANoOp()
    {
        using StoreTransferCache cache = new(await StoreAsync(), CachePolicy.Default, [], new CacheRunCounters());

        await cache.AbandonAsync();

        Assert.True(cache.IsIdle);
    }

    [Fact]
    public async Task AbandoningStopsWaitingWhenItsOwnTokenIsCancelled()
    {
        ProbeCacheStore store = new(await StoreAsync());
        TaskCompletionSource never = new();
        store.BeforePutBlob = (_, _) => never.Task;
        using StoreTransferCache cache = new(store, CachePolicy.Default, [], new CacheRunCounters());
        await cache.StoreAsync("geometry-a", Small(), 1, CancellationToken.None);
        using CancellationTokenSource stop = new();
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.AbandonAsync(stop.Token));
        never.TrySetResult();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task VerifyOnHitDecidesWhetherADamagedBlobIsServed(bool verify, bool served)
    {
        // The chunk's bytes replaced, under its key, by the packed chunk of
        // another set of the same shape (other weights): it decodes cleanly,
        // so only the re-hash tells it from the stored one.
        InMemoryCacheStore store = await StoreAsync();
        await StoreAndCommitAsync(store, "geometry-a", TransferSetCodecTests.Sample());
        string key = (await store.KeysAsync(CancellationToken.None)).Single();
        CacheRecord record = (await store.LookupAsync(key, CancellationToken.None))!;
        TransferSet other = new(
            [[new(0, 2f), new(2, 2f), new(1, 2f), new(0, 2f), new(3, 2f), new(1, 2f), new(2, 2f), new(0, 2f), new(2, 2f), new(3, 2f)]],
            [0, 0, 0, 0], [0, 3, 3, 8], [3, 0, 5, 2], 5);
        byte[] forged = TransferSetCodec.Pack(TransferSetCodec.Chunks(other, 1 << 20).Single());
        await store.PutBlobAsync(record.Blobs["chunk0"], forged, "test-tool", 0, CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);

        CacheRunCounters counters = new();
        TransferSet? hit = await new StoreTransferCache(store, CachePolicy.Default with { VerifyOnHit = verify }, [], counters)
            .TryGetAsync("geometry-a", 4, CancellationToken.None);

        Assert.Equal(served, hit is not null);
    }

    [Fact]
    public void TheReportNamesReusedStages()
    {
        CacheRunCounters counters = new();
        counters.StageHit("vrad.transfers", 1024, 5000);

        Assert.Contains("stages reused: vrad.transfers", CacheRunReport.Render(counters), StringComparison.Ordinal);
    }
}
