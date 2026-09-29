//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapTools.Compile.Cache;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="InMemoryCacheStore"/>'s figures and leases: what the report and
/// the GC read from it; and its own ceiling, which holds a long-lived host's
/// store to a byte bound whatever else is running.
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

    // One row naming one blob of `bytes` bytes, staged and committed. The
    // row's charge is fixed by its strings, so the facts can size ceilings
    // exactly from Charge().
    private static async Task<(string Row, string Blob)> CommitRowAsync(
        InMemoryCacheStore store, string name, int bytes, byte[]? shared = null)
    {
        byte[] data = shared ?? Encoding.UTF8.GetBytes(name.PadRight(bytes, '.'));
        string blob = CacheKey.HashBytes(data);
        await store.PutBlobAsync(blob, data, "t", 0, CancellationToken.None);
        CacheRecord record = Record(name, blob);
        await store.PutAsync(record, CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);
        return (record.Key, blob);
    }

    private static CacheRecord Record(string name, string blob) =>
        new(CacheKey.HashComponents(["row", name]), "s", "t", [], [], new Dictionary<string, string> { ["d"] = blob }, [], 0, 0);

    // What one CommitRowAsync row of `bytes` bytes is charged.
    private static long Charge(int bytes) => bytes + InMemoryCacheStore.RowCost(Record("x", CacheKey.HashBytes([0])));

    private static async Task<InMemoryCacheStore> OpenAsync(long maxBytes)
    {
        InMemoryCacheStore store = new(maxBytes);
        await store.OpenAsync("memory");
        return store;
    }

    private static async Task<bool> HasRowAsync(ICacheStore store, string key) =>
        await store.LookupAsync(key, CancellationToken.None) is not null;

    private static async Task<bool> HasBlobAsync(ICacheStore store, string key) =>
        await store.HasBlobAsync(key, CancellationToken.None);

    [Fact]
    public void TheDefaultCeilingIsTheCollectorsStoreCap()
    {
        Assert.Equal(CachePolicy.DefaultMaxStoreBytes, new InMemoryCacheStore().MaxBytes);
        Assert.Equal(CachePolicy.DefaultMaxStoreBytes, InMemoryCacheStore.DefaultMaxBytes);
    }

    [Fact]
    public void ARowIsChargedForEveryStringItCarries()
    {
        CacheRecord bare = new("k", "s", "t", [], [], new Dictionary<string, string>(), [], 0, 0);
        CacheRecord full = bare with
        {
            ContextTags = ["tag"],
            Parts = [("pn", "pv")],
            Blobs = new Dictionary<string, string> { ["r"] = "bk" },
            Dependencies = [("dep/path", "hash"), ("missed", null)],
        };

        Assert.Equal(InMemoryCacheStore.RowOverheadBytes + (2 * 3), InMemoryCacheStore.RowCost(bare));
        Assert.Equal(
            InMemoryCacheStore.RowOverheadBytes + (2 * (3 + 3 + 4 + 3 + 8 + 4 + 6)),
            InMemoryCacheStore.RowCost(full));
        Assert.Throws<ArgumentNullException>(() => InMemoryCacheStore.RowCost(null!));
    }

    [Fact]
    public async Task TheChargeIsEveryBlobOnceAndEveryRow()
    {
        InMemoryCacheStore store = await OpenAsync(0);
        byte[] shared = Encoding.UTF8.GetBytes(new string('s', 50));
        _ = await CommitRowAsync(store, "a", 0, shared);
        _ = await CommitRowAsync(store, "b", 0, shared);
        _ = await CommitRowAsync(store, "c", 100);

        // Staged only: not charged.
        await store.PutBlobAsync(CacheKey.HashBytes([1]), new byte[] { 1 }, "t", 0, CancellationToken.None);

        long row = InMemoryCacheStore.RowCost(Record("x", CacheKey.HashBytes([0])));
        Assert.Equal(50 + 100 + (3 * row), store.Bytes);
    }

    [Fact]
    public async Task ReplacingARowOrBlobChargesItOnce()
    {
        InMemoryCacheStore store = await OpenAsync(0);
        (string row, string blob) = await CommitRowAsync(store, "a", 40);
        long once = store.Bytes;

        _ = await CommitRowAsync(store, "a", 40);
        Assert.Equal(once, store.Bytes);

        await store.DeleteAsync(row, CancellationToken.None);
        await store.DeleteBlobsAsync([blob], CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);
        Assert.Equal(0, store.Bytes);
    }

    [Fact]
    public async Task WithNoCeilingNothingIsEvicted()
    {
        InMemoryCacheStore store = await OpenAsync(0);
        for (int i = 0; i < 50; i++)
        {
            _ = await CommitRowAsync(store, "r" + i, 1000);
        }

        Assert.Equal(50, (await store.KeysAsync(CancellationToken.None)).Count);
        Assert.Equal(0, store.RowsEvicted);
    }

    [Fact]
    public async Task ACommitUnderTheCeilingEvictsNothing()
    {
        InMemoryCacheStore store = await OpenAsync(Charge(100) * 3);
        for (int i = 0; i < 3; i++)
        {
            _ = await CommitRowAsync(store, "r" + i, 100);
        }

        Assert.Equal(Charge(100) * 3, store.Bytes);
        Assert.Equal(0, store.RowsEvicted);
    }

    [Fact]
    public async Task ACommitOverTheCeilingDropsTheOldestUntilItIsBackAtTheTarget()
    {
        // Ten rows fit; the eleventh goes over, and the trim runs down to 90 %
        // of the ceiling: the two oldest go, not just one.
        long ceiling = Charge(1000) * 10;
        InMemoryCacheStore store = await OpenAsync(ceiling);
        List<(string Row, string Blob)> rows = [];
        for (int i = 0; i < 11; i++)
        {
            rows.Add(await CommitRowAsync(store, "r" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), 1000));
        }

        Assert.False(await HasRowAsync(store, rows[0].Row));
        Assert.False(await HasRowAsync(store, rows[1].Row));
        Assert.False(await HasBlobAsync(store, rows[0].Blob));
        Assert.False(await HasBlobAsync(store, rows[1].Blob));
        for (int i = 2; i < 11; i++)
        {
            Assert.True(await HasRowAsync(store, rows[i].Row));
        }

        Assert.Equal(Charge(1000) * 9, store.Bytes);
        Assert.True(store.Bytes <= ceiling * CacheCollector.TrimTarget);
        Assert.Equal(2, store.RowsEvicted);
        Assert.Equal(2, store.BlobsEvicted);
    }

    [Fact]
    public async Task ARenewedRowIsYoungAgain()
    {
        // Three rows fit and a fourth trims exactly one (3.15 rows' target).
        InMemoryCacheStore store = await OpenAsync(Charge(1000) * 7 / 2);
        (string a, _) = await CommitRowAsync(store, "a", 1000);
        (string b, _) = await CommitRowAsync(store, "b", 1000);
        (string c, _) = await CommitRowAsync(store, "c", 1000);

        // A hit's renewal re-stages the row: a is now the newest.
        CacheRecord hit = (await store.LookupAsync(a, CancellationToken.None))!;
        await store.PutAsync(hit with { CreatedAtMs = 5 }, CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);

        _ = await CommitRowAsync(store, "d", 1000);

        Assert.True(await HasRowAsync(store, a));
        Assert.False(await HasRowAsync(store, b));
        Assert.True(await HasRowAsync(store, c));
    }

    [Fact]
    public async Task ALookupAloneIsNotAUse()
    {
        // The collector reads every row to plan; that must not make them young.
        InMemoryCacheStore store = await OpenAsync(Charge(1000) * 5 / 2);
        (string a, _) = await CommitRowAsync(store, "a", 1000);
        (string b, _) = await CommitRowAsync(store, "b", 1000);
        _ = await store.LookupAsync(a, CancellationToken.None);

        _ = await CommitRowAsync(store, "c", 1000);

        Assert.False(await HasRowAsync(store, a));
        Assert.True(await HasRowAsync(store, b));
    }

    [Fact]
    public async Task ABlobANewerRowStillNamesStays()
    {
        // Sized so that dropping a alone (its blob stays, b names it) is
        // enough to reach the target.
        byte[] shared = Encoding.UTF8.GetBytes(new string('s', 1000));
        long row = Charge(0);
        InMemoryCacheStore store = await OpenAsync((long)Math.Ceiling(((2 * row) + 2000) / CacheCollector.TrimTarget));
        (string a, string blob) = await CommitRowAsync(store, "a", 0, shared);
        (string b, _) = await CommitRowAsync(store, "b", 0, shared);
        _ = await CommitRowAsync(store, "c", 1000);

        Assert.False(await HasRowAsync(store, a));
        Assert.True(await HasRowAsync(store, b));
        Assert.True(await HasBlobAsync(store, blob));
        Assert.Equal(0, store.BlobsEvicted);
    }

    [Fact]
    public async Task BlobsNoRowNamesGoInTheSameAgeOrder()
    {
        InMemoryCacheStore store = await OpenAsync(Charge(1000) * 2);
        byte[] orphan = Encoding.UTF8.GetBytes(new string('o', 1000));
        string orphanKey = CacheKey.HashBytes(orphan);
        await store.PutBlobAsync(orphanKey, orphan, "t", 0, CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);
        (string a, _) = await CommitRowAsync(store, "a", 1000);

        // Over the ceiling: the orphan is older than a, so it goes first and
        // is enough.
        _ = await CommitRowAsync(store, "b", 500);

        Assert.False(await HasBlobAsync(store, orphanKey));
        Assert.True(await HasRowAsync(store, a));
        Assert.Equal(0, store.RowsEvicted);
        Assert.Equal(1, store.BlobsEvicted);
    }

    [Fact]
    public async Task EntriesOfOneCommitGoInKeyOrder()
    {
        InMemoryCacheStore store = await OpenAsync(Charge(1000) * 5 / 2);
        List<CacheRecord> records = [];
        foreach (string name in new[] { "p", "q" })
        {
            byte[] data = Encoding.UTF8.GetBytes(name.PadRight(1000, '.'));
            string blob = CacheKey.HashBytes(data);
            await store.PutBlobAsync(blob, data, "t", 0, CancellationToken.None);
            CacheRecord record = Record(name, blob);
            records.Add(record);
            await store.PutAsync(record, CancellationToken.None);
        }

        await store.CommitAsync(CancellationToken.None);
        _ = await CommitRowAsync(store, "z", 1000);

        string first = records.Select(r => r.Key).Min(StringComparer.Ordinal)!;
        string second = records.Select(r => r.Key).Max(StringComparer.Ordinal)!;
        Assert.False(await HasRowAsync(store, first));
        Assert.True(await HasRowAsync(store, second));
    }

    [Fact]
    public async Task ARowLargerThanTheWholeCeilingDoesNotStay()
    {
        InMemoryCacheStore store = await OpenAsync(Charge(100) * 2);
        (string small, _) = await CommitRowAsync(store, "small", 100);
        (string huge, string blob) = await CommitRowAsync(store, "huge", 10_000);

        Assert.False(await HasRowAsync(store, small));
        Assert.False(await HasRowAsync(store, huge));
        Assert.False(await HasBlobAsync(store, blob));
        Assert.Equal(0, store.Bytes);
    }

    [Fact]
    public async Task AFailedCommitAppliesNothingAndChargesNothing()
    {
        InMemoryCacheStore store = await OpenAsync(0);
        _ = await CommitRowAsync(store, "a", 10);
        long before = store.Bytes;
        store.FailNextCommit = new IOException("planted");
        byte[] data = [1, 2, 3];
        await store.PutBlobAsync(CacheKey.HashBytes(data), data, "t", 0, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(async () => await store.CommitAsync(CancellationToken.None));

        Assert.Equal(before, store.Bytes);
        Assert.Equal(1, store.StagedCount);
    }

    [Fact]
    public async Task ClearAndDisposeZeroTheCharge()
    {
        InMemoryCacheStore store = await OpenAsync(Charge(10));
        _ = await CommitRowAsync(store, "a", 10);
        _ = await CommitRowAsync(store, "b", 10);
        Assert.NotEqual(0, store.RowsEvicted);

        await store.ClearAsync(CancellationToken.None);
        Assert.Equal(0, store.Bytes);
        Assert.Equal(0, store.RowsEvicted);
        Assert.Equal(0, store.BlobsEvicted);

        _ = await CommitRowAsync(store, "c", 5);
        await store.DisposeAsync();
        Assert.Equal(0, store.Bytes);
    }

    [Fact]
    public async Task ConcurrentCompilesCommittingIntoOneStoreKeepItBoundedAndConsistent()
    {
        // Eight writers, each staging and committing its own rows, some
        // sharing blobs, into one small store. Whatever the interleaving,
        // after every commit the charge is the sum of what is held, it is
        // under the ceiling, and no committed row names a blob that is gone
        // unless the trim dropped that row too.
        long ceiling = Charge(512) * 20;
        InMemoryCacheStore store = await OpenAsync(ceiling);
        byte[] shared = Encoding.UTF8.GetBytes(new string('s', 512));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (int i = 0; i < 40; i++)
            {
                byte[]? blob = i % 5 == 0 ? shared : null;
                _ = await CommitRowAsync(store, $"w{w}-r{i}", 512, blob);
                Assert.True(store.Bytes <= ceiling);
            }
        })));

        await store.CommitAsync(CancellationToken.None);
        Assert.Equal(0, store.StagedCount);
        Assert.True(store.Bytes <= ceiling);

        long charged = 0;
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (string key in await store.KeysAsync(CancellationToken.None))
        {
            CacheRecord record = (await store.LookupAsync(key, CancellationToken.None))!;
            charged += InMemoryCacheStore.RowCost(record);
            string blob = record.Blobs["d"];
            Assert.True(await HasBlobAsync(store, blob));
            named.Add(blob);
        }

        CacheStats stats = await store.ReadStatsAsync(CancellationToken.None);
        Assert.Equal(charged + stats.SizeBytes, store.Bytes);
        Assert.Equal(named.Count, stats.BlobCount);
        Assert.True(store.RowsEvicted > 0);
    }

    [Fact]
    public async Task ACommitNeverClearsStagingThatArrivedWhileItRan()
    {
        // One writer stages rows as fast as it can while another commits in a
        // loop. A commit used to clear the whole staging set when it finished,
        // losing whatever the writer staged during it; now every staged row is
        // published by some commit.
        InMemoryCacheStore store = await OpenAsync(0);
        List<string> keys = [];
        using CancellationTokenSource done = new();

        Task committer = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                await store.CommitAsync(CancellationToken.None);
            }
        });

        for (int i = 0; i < 2000; i++)
        {
            byte[] data = BitConverter.GetBytes(i);
            string blob = CacheKey.HashBytes(data);
            await store.PutBlobAsync(blob, data, "t", 0, CancellationToken.None);
            CacheRecord record = Record("n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), blob);
            await store.PutAsync(record, CancellationToken.None);
            keys.Add(record.Key);
        }

        await done.CancelAsync();
        await committer;
        await store.CommitAsync(CancellationToken.None);

        Assert.Equal(keys.Count, (await store.KeysAsync(CancellationToken.None)).Count);
        Assert.Equal(keys.Count, (await store.ReadStatsAsync(CancellationToken.None)).BlobCount);
    }
}
