//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Compile.Cache;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="InMemoryCacheStore"/>'s figures and leases: what the report and
/// the GC read from it.
/// </summary>
public sealed class InMemoryCacheStoreTests
{
    [Fact]
    public async Task TheStatsCountCommittedRowsBlobsGenerationsAndTools()
    {
        InMemoryCacheStore store = new();
        await store.OpenAsync("memory");
        _ = await CacheCollectorTests.PutRowAsync(store, "a", DateTimeOffset.UnixEpoch, 10);
        _ = await CacheCollectorTests.PutRowAsync(store, "b", DateTimeOffset.UnixEpoch, 30);
        await store.RecordGenerationAsync("1", CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);

        // Staged, not committed: not counted.
        await store.PutBlobAsync(CacheKey.HashBytes([7]), new byte[] { 7 }, "t", 0, CancellationToken.None);

        CacheStats stats = await store.ReadStatsAsync(CancellationToken.None);

        Assert.Equal(new CacheStats(2, 2, 40, 40, 1, 1), stats);
    }

    [Fact]
    public async Task AnUnusableStoreReportsZeroes()
    {
        Assert.Equal(new CacheStats(0, 0, 0, 0, 0, 0), await new InMemoryCacheStore().ReadStatsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ABlobsSizeIsItsCommittedLength()
    {
        InMemoryCacheStore store = new();
        await store.OpenAsync("memory");
        byte[] data = [1, 2, 3, 4, 5];
        string key = CacheKey.HashBytes(data);
        await store.PutBlobAsync(key, data, "t", 0, CancellationToken.None);

        Assert.Null(await store.BlobSizeAsync(key, CancellationToken.None));
        await store.CommitAsync(CancellationToken.None);
        Assert.Equal(5, await store.BlobSizeAsync(key, CancellationToken.None));
        Assert.Null(await store.BlobSizeAsync(CacheKey.HashBytes([9]), CancellationToken.None));
    }

    [Fact]
    public void LeasesCountCompilesInFlightAndCloseOnce()
    {
        InMemoryCacheStore store = new();
        IDisposable first = store.BeginRun();
        IDisposable second = store.BeginRun();
        Assert.Equal(2, store.RunsInFlight);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, store.RunsInFlight);

        second.Dispose();
        Assert.Equal(0, store.RunsInFlight);
    }

    [Fact]
    public void TwoStoresCountApart()
    {
        using IDisposable lease = new InMemoryCacheStore().BeginRun();

        Assert.Equal(0, new InMemoryCacheStore().RunsInFlight);
    }
}
