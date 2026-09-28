//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Cache.Sqlite;
using SourceSharp.MapTools.Compile.Cache;

using Xunit;

namespace SourceSharp.Tests.MapTools.Cache.Sqlite;

/// <summary>
/// The members of <see cref="SqliteCacheStore"/> the GC reads: a committed
/// blob's size and the count of compiles in flight. The database is SQLite's
/// in-memory one, so nothing touches a disk.
/// </summary>
public sealed class SqliteCacheStoreTests
{
    [Fact]
    public async Task ABlobsSizeIsItsCommittedLength()
    {
        await using SqliteCacheStore store = new();
        await store.OpenAsync(":memory:", CancellationToken.None);
        byte[] data = [1, 2, 3, 4, 5, 6];
        string key = CacheKey.HashBytes(data);
        await store.PutBlobAsync(key, data, "t", 0, CancellationToken.None);

        Assert.Null(await store.BlobSizeAsync(key, CancellationToken.None));
        await store.CommitAsync(CancellationToken.None);
        Assert.Equal(6, await store.BlobSizeAsync(key, CancellationToken.None));
        Assert.Null(await store.BlobSizeAsync(CacheKey.HashBytes([9]), CancellationToken.None));
        Assert.Null(await store.BlobSizeAsync("not-a-digest", CancellationToken.None));
    }

    [Fact]
    public async Task AnUnopenedStoreHasNoBlobs()
    {
        await using SqliteCacheStore store = new();

        Assert.Null(await store.BlobSizeAsync(CacheKey.HashBytes([1]), CancellationToken.None));
    }

    [Fact]
    public async Task LeasesCountCompilesInFlightAndCloseOnce()
    {
        await using SqliteCacheStore store = new();
        IDisposable first = store.BeginRun();
        using IDisposable second = store.BeginRun();
        Assert.Equal(2, store.RunsInFlight);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, store.RunsInFlight);
    }
}
