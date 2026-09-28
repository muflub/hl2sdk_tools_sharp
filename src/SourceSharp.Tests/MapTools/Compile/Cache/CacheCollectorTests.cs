//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapTools.Compile.Cache;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="CacheCollector"/>: every <see cref="CachePolicy"/> knob it reads
/// changes what it drops, it never drops a row of a kept generation or a blob
/// a remaining row names, and it leaves the store alone while another
/// compile is using it.
/// </summary>
public sealed class CacheCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeProvider Clock = new CompileCacheLifecycleTests.FixedTime(Now);

    private static async Task<InMemoryCacheStore> OpenAsync()
    {
        InMemoryCacheStore store = new();
        await store.OpenAsync("memory");
        return store;
    }

    /// <summary>Commits one row with one blob of <paramref name="bytes"/> bytes named by <paramref name="name"/>.</summary>
    internal static async Task<string> PutRowAsync(
        ICacheStore store, string name, DateTimeOffset created, int bytes, byte[]? shared = null)
    {
        byte[] data = shared ?? Filled(name, bytes);
        long ms = created.ToUnixTimeMilliseconds();
        string blob = CacheKey.HashBytes(data);
        await store.PutBlobAsync(blob, data, "test-tool", ms, CancellationToken.None);
        string key = CacheKey.HashComponents(["row", name]);
        await store.PutAsync(
            new CacheRecord(key, "test.stage", "test-tool", [], [], new Dictionary<string, string> { ["data"] = blob }, [], 0, ms),
            CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);
        return key;
    }

    private static byte[] Filled(string name, int bytes)
    {
        byte[] data = new byte[bytes];
        byte[] text = Encoding.UTF8.GetBytes(name);
        for (int i = 0; i < bytes; i++)
        {
            data[i] = text[i % text.Length];
        }

        return data;
    }

    private static async Task GenerationsAsync(ICacheStore store, params DateTimeOffset[] runs)
    {
        foreach (DateTimeOffset run in runs)
        {
            await store.RecordGenerationAsync(run.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), CancellationToken.None);
        }

        await store.CommitAsync(CancellationToken.None);
    }

    private static ValueTask<CacheGcResult> CollectAsync(ICacheStore store, CachePolicy policy, TimeProvider? time = null, int ownRuns = 0) =>
        CacheCollector.CollectAsync(store, policy, time ?? Clock, ownRuns, CancellationToken.None);

    private static async Task<bool> HasAsync(ICacheStore store, string key) =>
        await store.LookupAsync(key, CancellationToken.None) is not null;

    // ---- the switches that turn it off ----

    [Fact]
    public async Task GcOnCommitOffCollectsNothing()
    {
        InMemoryCacheStore store = await OpenAsync();
        string stale = await PutRowAsync(store, "stale", Now.AddDays(-365), 10);

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default with { GcOnCommit = false });

        Assert.Equal("off", result.Skipped);
        Assert.True(await HasAsync(store, stale));
    }

    [Fact]
    public async Task APostureThatDoesNotWriteCollectsNothing()
    {
        InMemoryCacheStore store = await OpenAsync();
        string stale = await PutRowAsync(store, "stale", Now.AddDays(-365), 10);

        CacheGcResult result = await CollectAsync(store, CachePolicy.ReadOnly);

        Assert.NotNull(result.Skipped);
        Assert.True(await HasAsync(store, stale));
    }

    [Fact]
    public async Task AnUnusableStoreIsNotCollected()
    {
        CacheGcResult result = await CollectAsync(new InMemoryCacheStore(), CachePolicy.Default);

        Assert.Equal("the store is unusable", result.Skipped);
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(0, 0, true)]
    public async Task ItRunsOnlyWhenNoOtherCompileIsInFlight(int ownRuns, int leases, bool runs)
    {
        InMemoryCacheStore store = await OpenAsync();
        string stale = await PutRowAsync(store, "stale", Now.AddDays(-365), 10);
        List<IDisposable> open = [.. Enumerable.Range(0, leases).Select(_ => store.BeginRun())];

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default, ownRuns: ownRuns);

        Assert.Equal(runs, result.Skipped is null);
        Assert.Equal(!runs, await HasAsync(store, stale));
        open.ForEach(l => l.Dispose());
        Assert.Equal(0, store.RunsInFlight);
    }

    // ---- age ----

    [Fact]
    public async Task RowsUnusedForLongerThanMaxAgeDaysAreDropped()
    {
        InMemoryCacheStore store = await OpenAsync();
        string stale = await PutRowAsync(store, "stale", Now.AddDays(-31), 10);
        string fresh = await PutRowAsync(store, "fresh", Now.AddDays(-29), 10);

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default with { MaxAgeDays = 30 });

        Assert.False(await HasAsync(store, stale));
        Assert.True(await HasAsync(store, fresh));
        Assert.Equal(1, result.RowsDropped);
        Assert.Equal(1, result.BlobsDropped);
        Assert.Equal(10, result.BytesFreed);
    }

    [Fact]
    public async Task MaxAgeDaysOfZeroKeepsRowsOfAnyAge()
    {
        InMemoryCacheStore store = await OpenAsync();
        string ancient = await PutRowAsync(store, "ancient", Now.AddDays(-3650), 10);

        _ = await CollectAsync(store, CachePolicy.Default with { MaxAgeDays = 0 });

        Assert.True(await HasAsync(store, ancient));
    }

    // ---- generations ----

    [Fact]
    public async Task RowsOfTheKeptGenerationsAreNeverDropped()
    {
        // Three compiles, the last two a year ago: with two kept, rows from
        // either of those stay however old they are; the first's go.
        InMemoryCacheStore store = await OpenAsync();
        DateTimeOffset first = Now.AddDays(-400);
        DateTimeOffset second = Now.AddDays(-380);
        DateTimeOffset third = Now.AddDays(-370);
        string byFirst = await PutRowAsync(store, "first", first, 10);
        string bySecond = await PutRowAsync(store, "second", second, 10);
        string byThird = await PutRowAsync(store, "third", third, 10);
        await GenerationsAsync(store, first, second, third);

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default with { GenerationsKept = 2 });

        Assert.False(await HasAsync(store, byFirst));
        Assert.True(await HasAsync(store, bySecond));
        Assert.True(await HasAsync(store, byThird));
        Assert.Equal(1, result.GenerationsDropped);
        Assert.Equal(
            [second.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), third.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)],
            (await store.GenerationsAsync(CancellationToken.None)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task NoKeptGenerationsProtectsNothing()
    {
        InMemoryCacheStore store = await OpenAsync();
        DateTimeOffset run = Now.AddDays(-370);
        string row = await PutRowAsync(store, "row", run, 10);
        await GenerationsAsync(store, run);

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default with { GenerationsKept = 0 });

        Assert.False(await HasAsync(store, row));
        Assert.Empty(await store.GenerationsAsync(CancellationToken.None));
        Assert.Equal(1, result.GenerationsDropped);
    }

    // ---- size ----

    [Fact]
    public async Task AnOversizedStoreLosesItsOldestRowsUntilItIsAtNinetyPercent()
    {
        // Five rows of 100 bytes against a cap of 400: above the cap, so the
        // oldest go until at most 360 bytes are left, which is three rows.
        InMemoryCacheStore store = await OpenAsync();
        List<string> rows = [];
        for (int i = 0; i < 5; i++)
        {
            rows.Add(await PutRowAsync(store, $"row{i}", Now.AddDays(-5 + i), 100));
        }

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default with { MaxStoreBytes = 400, GenerationsKept = 0 });

        bool[] kept = await Task.WhenAll(rows.Select(r => HasAsync(store, r)));
        Assert.Equal(new[] { false, false, true, true, true }, kept);
        Assert.Equal(2, result.RowsDropped);
        Assert.Equal(200, result.BytesFreed);
        Assert.Equal(300, (await store.ReadStatsAsync(CancellationToken.None)).SizeBytes);
    }

    [Fact]
    public async Task AStoreUnderItsCapIsNotTrimmed()
    {
        InMemoryCacheStore store = await OpenAsync();
        string row = await PutRowAsync(store, "row", Now.AddDays(-1), 100);

        _ = await CollectAsync(store, CachePolicy.Default with { MaxStoreBytes = 100, GenerationsKept = 0 });

        Assert.True(await HasAsync(store, row));
    }

    [Fact]
    public async Task MaxStoreBytesOfZeroHasNoSizeLimit()
    {
        InMemoryCacheStore store = await OpenAsync();
        string row = await PutRowAsync(store, "row", Now.AddDays(-1), 1000);

        _ = await CollectAsync(store, CachePolicy.Default with { MaxStoreBytes = 0, GenerationsKept = 0 });

        Assert.True(await HasAsync(store, row));
    }

    [Fact]
    public async Task TheSizeTrimStopsAtTheKeptGenerations()
    {
        InMemoryCacheStore store = await OpenAsync();
        DateTimeOffset run = Now.AddDays(-1);
        string row = await PutRowAsync(store, "row", run, 1000);
        await GenerationsAsync(store, run);

        _ = await CollectAsync(store, CachePolicy.Default with { MaxStoreBytes = 10, GenerationsKept = 1 });

        Assert.True(await HasAsync(store, row));
    }

    // ---- blobs ----

    [Fact]
    public async Task ABlobARemainingRowNamesIsKept()
    {
        InMemoryCacheStore store = await OpenAsync();
        byte[] shared = Filled("shared", 50);
        string stale = await PutRowAsync(store, "stale", Now.AddDays(-365), 0, shared);
        string fresh = await PutRowAsync(store, "fresh", Now, 0, shared);

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default with { GenerationsKept = 0 });

        Assert.False(await HasAsync(store, stale));
        Assert.True(await HasAsync(store, fresh));
        Assert.Equal(0, result.BlobsDropped);
        Assert.NotNull(await store.GetBlobAsync(CacheKey.HashBytes(shared), CancellationToken.None));
    }

    [Fact]
    public async Task BlobsNoRowNamesAreSwept()
    {
        InMemoryCacheStore store = await OpenAsync();
        byte[] orphan = Filled("orphan", 20);
        await store.PutBlobAsync(CacheKey.HashBytes(orphan), orphan, "test-tool", 0, CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default);

        Assert.Equal(1, result.BlobsDropped);
        Assert.Null(await store.GetBlobAsync(CacheKey.HashBytes(orphan), CancellationToken.None));
    }

    // ---- budget ----

    [Fact]
    public async Task AZeroBudgetDoesNoWork()
    {
        InMemoryCacheStore store = await OpenAsync();
        string stale = await PutRowAsync(store, "stale", Now.AddDays(-365), 10);

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default with { GcBudget = TimeSpan.Zero });

        Assert.True(result.BudgetSpent);
        Assert.True(await HasAsync(store, stale));
    }

    [Fact]
    public async Task ASpentBudgetPublishesWhatWasDroppedAndStops()
    {
        // Each clock reading is 400 ms after the last: the budget of one
        // second allows two rows, and the third waits for the next commit.
        InMemoryCacheStore store = await OpenAsync();
        List<string> rows = [];
        for (int i = 0; i < 3; i++)
        {
            rows.Add(await PutRowAsync(store, $"stale{i}", Now.AddDays(-365 + i), 10));
        }

        CacheGcResult result = await CollectAsync(
            store, CachePolicy.Default with { GcBudget = TimeSpan.FromSeconds(1) }, new SteppingTime(Now, TimeSpan.FromMilliseconds(400)));

        Assert.True(result.BudgetSpent);
        bool[] kept = await Task.WhenAll(rows.Select(r => HasAsync(store, r)));
        Assert.Equal(new[] { false, false, true }, kept);
        Assert.Equal(0, store.StagedCount);
    }

    // ---- vacuum ----

    [Theory]
    [InlineData(CacheVacuum.OnGc, true, 1)]
    [InlineData(CacheVacuum.EveryRun, true, 1)]
    [InlineData(CacheVacuum.Never, true, 0)]
    [InlineData(CacheVacuum.OnGc, false, 0)]
    public async Task TheStoreIsVacuumedWhenTheGcReclaimedAndThePolicyAsks(CacheVacuum vacuum, bool stale, int vacuums)
    {
        ProbeCacheStore store = new(await OpenAsync());
        _ = await PutRowAsync(store, "row", stale ? Now.AddDays(-365) : Now, 10);

        CacheGcResult result = await CollectAsync(store, CachePolicy.Default with { Vacuum = vacuum, GenerationsKept = 0 });

        Assert.Equal(vacuums, store.Vacuums);
        Assert.Equal(vacuums == 1, result.Vacuumed);
    }

    [Fact]
    public async Task AFailedCommitLeavesTheStoreAsItWas()
    {
        InMemoryCacheStore store = await OpenAsync();
        string stale = await PutRowAsync(store, "stale", Now.AddDays(-365), 10);
        store.FailNextCommit = new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(async () => await CollectAsync(store, CachePolicy.Default));

        Assert.True(await HasAsync(store, stale));
        Assert.Equal(0, store.StagedCount);
    }

    /// <summary>A clock whose every timestamp reading is one step after the last.</summary>
    private sealed class SteppingTime(DateTimeOffset now, TimeSpan step) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, step.Ticks);
    }
}
