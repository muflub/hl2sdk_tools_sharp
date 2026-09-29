//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Cache.Sqlite;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Cache.Sqlite;

/// <summary>
/// The members of <see cref="SqliteCacheStore"/> the GC reads: a committed
/// blob's size and the count of compiles in flight, which use SQLite's
/// in-memory database; and where a store on disk puts its file, which uses a
/// temporary folder.
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

    /// <summary>
    /// A writable store makes the folders its file goes in; SQLite alone
    /// fails the open when they are missing, which turned a fresh
    /// <c>-cache-dir</c> into "no cache".
    /// </summary>
    [Fact]
    public async Task AWritableStoreCreatesTheFoldersItsFileGoesIn()
    {
        using TempTree tree = new();
        string location = Path.Combine(tree.Root, "not", "there", "yet", "map.sscache.db");
        Assert.False(Directory.Exists(Path.Combine(tree.Root, "not")));

        await using (SqliteCacheStore store = new())
        {
            await store.OpenAsync(location, CancellationToken.None);
            Assert.True(store.IsUsable);
            byte[] data = [7, 8, 9];
            await store.PutBlobAsync(CacheKey.HashBytes(data), data, "t", 0, CancellationToken.None);
            await store.CommitAsync(CancellationToken.None);
        }

        Assert.True(File.Exists(location));
        await using SqliteCacheStore again = new();
        await again.OpenAsync(location, CancellationToken.None);
        Assert.Equal(3, await again.BlobSizeAsync(CacheKey.HashBytes([7, 8, 9]), CancellationToken.None));
    }

    /// <summary>A read-only store creates nothing: its open fails, and the disk is as it was.</summary>
    [Fact]
    public async Task AReadOnlyStoreCreatesNoFolder()
    {
        using TempTree tree = new();
        string folder = Path.Combine(tree.Root, "missing");
        await using SqliteCacheStore store = new(readOnly: true);

        await Assert.ThrowsAnyAsync<Exception>(
            () => store.OpenAsync(Path.Combine(folder, "map.sscache.db"), CancellationToken.None).AsTask());
        Assert.False(store.IsUsable);
        Assert.False(Directory.Exists(folder));
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
