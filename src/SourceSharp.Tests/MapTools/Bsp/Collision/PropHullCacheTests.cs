//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// <see cref="PropHullCache"/>: the host-owned static-prop hull cache shared
/// between compiles. Each <see cref="StaticPropHullCache"/> here stands for
/// one compile, as it does in the product.
/// </summary>
public sealed class PropHullCacheTests
{
    private static Vec3[] Box(float half, float z = 0f)
    {
        Vec3[] corners = new Vec3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vec3(
                (i & 1) != 0 ? half : -half,
                (i & 2) != 0 ? half : -half,
                ((i & 4) != 0 ? 2 * half : 0f) + z);
        }

        return corners;
    }

    private static Func<CancellationToken, Task<IReadOnlyList<Vec3[]>?>> Meshes(params Vec3[][] meshes) =>
        _ => Task.FromResult<IReadOnlyList<Vec3[]>?>(meshes);

    private static PropHullKey KeyOf(ICollisionCooker cooker, ComplianceOptions compliance, params Vec3[][] meshes) =>
        PropHullKey.Of(PropHullKey.Context(cooker.CookerIdentity, compliance), meshes);

    // Stands for one compile: a fresh per-compile cache over the shared one.
    private static Task<StaticPropHull> CompileOnceAsync(
        ICollisionCooker cooker,
        PropHullCache? shared,
        string name,
        Vec3[][] meshes,
        ComplianceOptions? compliance = null,
        CancellationToken cancellationToken = default) =>
        new StaticPropHullCache(cooker, shared, compliance ?? ComplianceOptions.Correct)
            .GetOrCookAsync(name, Meshes(meshes), cancellationToken);

    [Fact]
    public async Task ASecondCompileHitsWithTheSameBytesAndDoesNotCook()
    {
        using ManagedCollisionCooker managed = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        CountingCooker cooker = new(managed);
        using PropHullCache shared = new();

        StaticPropHull first = await CompileOnceAsync(cooker, shared, "models/crate.mdl", [Box(8f)]);
        StaticPropHull second = await CompileOnceAsync(cooker, shared, "models/crate.mdl", [Box(8f)]);

        Assert.NotNull(first.Blob);
        Assert.Equal(first.Blob, second.Blob);
        Assert.Equal("models/crate.mdl", second.ModelName);
        Assert.Equal(1, cooker.Runs);
        Assert.Equal(new PropHullCacheStatistics(1, PropHullCache.EntryOverheadBytes + first.Blob!.Length, 1, 1, 1, 0, 0), shared.Statistics);
    }

    [Fact]
    public async Task AHitIsTheBytesACookWithoutTheCacheGives()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using PropHullCache shared = new();
        Vec3[][] meshes = [Box(8f), Box(4f, 20f)];

        StaticPropHull plain = await CompileOnceAsync(cooker, null, "m.mdl", meshes);
        await CompileOnceAsync(cooker, shared, "m.mdl", meshes);
        StaticPropHull hit = await CompileOnceAsync(cooker, shared, "m.mdl", meshes);

        Assert.Equal(1, shared.Statistics.Hits);
        Assert.Equal(plain.Blob, hit.Blob);
    }

    [Fact]
    public async Task WithoutASharedCacheEveryCompileCooks()
    {
        CountingCooker cooker = new(new FakeCollisionCooker());

        await CompileOnceAsync(cooker, null, "m.mdl", [Box(8f)]);
        await CompileOnceAsync(cooker, null, "m.mdl", [Box(8f)]);

        Assert.Equal(2, cooker.Runs);
    }

    [Fact]
    public async Task EveryHitIsTheCallersOwnCopy()
    {
        FakeCollisionCooker cooker = new();
        using PropHullCache shared = new();
        StaticPropHull cooked = await CompileOnceAsync(cooker, shared, "m.mdl", [Box(8f)]);
        byte[] expected = [.. cooked.Blob!];

        // A compile scribbling on its arrays reaches neither the cache nor
        // the next compile.
        Array.Clear(cooked.Blob!);
        StaticPropHull hit = await CompileOnceAsync(cooker, shared, "m.mdl", [Box(8f)]);
        Array.Clear(hit.Blob!);
        StaticPropHull again = await CompileOnceAsync(cooker, shared, "m.mdl", [Box(8f)]);

        Assert.Equal(expected, again.Blob);
        Assert.NotSame(hit.Blob, again.Blob);
    }

    [Fact]
    public async Task MeshesThatGiveNoConvexAreRememberedAsAHitWithNoHull()
    {
        CountingCooker cooker = new(new FakeCollisionCooker());
        using PropHullCache shared = new();
        Vec3[][] flat = [[Vec3.Zero, new Vec3(1, 0, 0)]];

        StaticPropHull first = await CompileOnceAsync(cooker, shared, "bad.mdl", flat);
        StaticPropHull second = await CompileOnceAsync(cooker, shared, "bad.mdl", flat);

        Assert.Null(first.Blob);
        Assert.Null(second.Blob);
        Assert.Equal(1, cooker.Runs);
        Assert.Equal(1, shared.Statistics.Hits);
    }

    [Fact]
    public async Task AModelThatDoesNotLoadIsNeverStored()
    {
        // Whether a name loads is the compile's content, not the cook's.
        using PropHullCache shared = new();
        StaticPropHull hull = await new StaticPropHullCache(new FakeCollisionCooker(), shared, ComplianceOptions.Correct)
            .GetOrCookAsync("missing.mdl", _ => Task.FromResult<IReadOnlyList<Vec3[]>?>(null));

        Assert.Null(hull.Blob);
        Assert.Equal(default, shared.Statistics);
    }

    [Fact]
    public async Task TheKeyIsTheMeshesNotTheName()
    {
        CountingCooker cooker = new(new FakeCollisionCooker());
        using PropHullCache shared = new();

        await CompileOnceAsync(cooker, shared, "models/a.mdl", [Box(8f)]);
        StaticPropHull renamed = await CompileOnceAsync(cooker, shared, "models/b.mdl", [Box(8f)]);
        await CompileOnceAsync(cooker, shared, "models/a.mdl", [Box(9f)]);

        Assert.Equal("models/b.mdl", renamed.ModelName);
        Assert.Equal(2, cooker.Runs);
        Assert.Equal(1, shared.Statistics.Hits);
    }

    [Fact]
    public void TheKeyCoversTheMeshBoundariesAndEveryBit()
    {
        FakeCollisionCooker cooker = new();
        Vec3[] a = Box(8f);
        ComplianceOptions c = ComplianceOptions.Correct;

        PropHullKey whole = KeyOf(cooker, c, a);
        Assert.Equal(whole, KeyOf(cooker, c, [.. a]));
        Assert.NotEqual(whole, KeyOf(cooker, c, a[..4], a[4..]));
        Assert.NotEqual(KeyOf(cooker, c, a, []), KeyOf(cooker, c, [], a));
        Assert.NotEqual(KeyOf(cooker, c, Array.Empty<Vec3[]>()), KeyOf(cooker, c, new[] { Array.Empty<Vec3>() }));

        // -0 and 0 cook alike in most places, but not necessarily all.
        Vec3[] negativeZero = [.. a];
        negativeZero[0] = new Vec3(-0f, negativeZero[0].Y, negativeZero[0].Z);
        Vec3[] positiveZero = [.. a];
        positiveZero[0] = new Vec3(0f, positiveZero[0].Y, positiveZero[0].Z);
        Assert.NotEqual(KeyOf(cooker, c, negativeZero), KeyOf(cooker, c, positiveZero));
    }

    [Fact]
    public async Task AnotherCookerMisses()
    {
        using PropHullCache shared = new();
        CountingCooker fake = new(new FakeCollisionCooker());
        CountingCooker other = new(new FakeCollisionCooker(), "fake, another build");

        await CompileOnceAsync(fake, shared, "m.mdl", [Box(8f)]);
        await CompileOnceAsync(other, shared, "m.mdl", [Box(8f)]);

        Assert.Equal(1, fake.Runs);
        Assert.Equal(1, other.Runs);
        Assert.Equal(0, shared.Statistics.Hits);
        Assert.Equal(2, shared.Statistics.Count);
    }

    [Fact]
    public async Task TheManagedCookersOfTwoPoliciesMissEachOther()
    {
        using PropHullCache shared = new();
        using ManagedCollisionCooker correct = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using ManagedCollisionCooker stock = ManagedCollisionCooker.Create(ComplianceOptions.Stock);

        StaticPropHull a = await CompileOnceAsync(correct, shared, "m.mdl", [Box(8f)], ComplianceOptions.Correct);
        StaticPropHull b = await CompileOnceAsync(stock, shared, "m.mdl", [Box(8f)], ComplianceOptions.Stock);

        Assert.Equal(0, shared.Statistics.Hits);
        Assert.Equal(2, shared.Statistics.Count);
        Assert.NotNull(a.Blob);
        Assert.NotNull(b.Blob);
    }

    [Fact]
    public async Task AChangedComplianceMissesEvenWithTheSameCooker()
    {
        // The native cooker ignores compliance, but the key folds it anyway,
        // so a quirk that starts to reach the hull cook cannot be served a
        // hull cooked without it.
        CountingCooker cooker = new(new FakeCollisionCooker());
        using PropHullCache shared = new();

        await CompileOnceAsync(cooker, shared, "m.mdl", [Box(8f)], ComplianceOptions.Correct);
        await CompileOnceAsync(cooker, shared, "m.mdl", [Box(8f)], ComplianceOptions.Stock);
        await CompileOnceAsync(
            cooker, shared, "m.mdl", [Box(8f)], ComplianceOptions.Correct.Flipping(StockQuirk.CollisionCookerSinglePrecision));

        Assert.Equal(3, cooker.Runs);
        Assert.Equal(0, shared.Statistics.Hits);
    }

    [Fact]
    public void TheManagedCookersIdentityNamesEveryCookReachingQuirk()
    {
        // The identity is the cooker's share of every collision cache key,
        // so two cookers that cook differently must not share it.
        ComplianceOptions correct = ComplianceOptions.Correct;
        ComplianceOptions stock = ComplianceOptions.Stock;
        using ManagedCollisionCooker plainCorrect = ManagedCollisionCooker.Create(correct);
        using ManagedCollisionCooker plainStock = ManagedCollisionCooker.Create(stock);
        using ManagedCollisionCooker inertia = ManagedCollisionCooker.Create(correct.Flipping(StockQuirk.CollisionInertiaZeroLengthEdge));
        using ManagedCollisionCooker polysoup = ManagedCollisionCooker.Create(correct.Flipping(StockQuirk.CollisionPolysoupMaterialOverrun));
        using ManagedCollisionCooker stockInertia = ManagedCollisionCooker.Create(stock.Flipping(StockQuirk.CollisionInertiaZeroLengthEdge));
        using ManagedCollisionCooker stockPolysoup = ManagedCollisionCooker.Create(stock.Flipping(StockQuirk.CollisionPolysoupMaterialOverrun));

        string[] identities =
        [
            plainCorrect.CookerIdentity, plainStock.CookerIdentity, inertia.CookerIdentity,
            polysoup.CookerIdentity, stockInertia.CookerIdentity, stockPolysoup.CookerIdentity,
        ];
        Assert.Equal(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());

        // The plain policies keep the strings existing cache rows were keyed on.
        Assert.DoesNotContain('+', plainCorrect.CookerIdentity);
        Assert.DoesNotContain('+', plainStock.CookerIdentity);
        Assert.EndsWith("+inertia-edge:stock", inertia.CookerIdentity, StringComparison.Ordinal);
        Assert.EndsWith("+polysoup-material:stock", polysoup.CookerIdentity, StringComparison.Ordinal);
        Assert.EndsWith("+inertia-edge:correct", stockInertia.CookerIdentity, StringComparison.Ordinal);
        Assert.EndsWith("+polysoup-material:correct", stockPolysoup.CookerIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLeastRecentlyUsedGoesFirstWhenTheBoundIsReached()
    {
        const int Blob = 100;
        long entry = PropHullCache.EntryOverheadBytes + Blob;
        using PropHullCache cache = new(3 * entry);
        PropHullKey[] keys = [.. Enumerable.Range(0, 4).Select(Key)];

        cache.Add(keys[0], new byte[Blob]);
        cache.Add(keys[1], new byte[Blob]);
        cache.Add(keys[2], new byte[Blob]);
        Assert.True(cache.TryGet(keys[0], out _)); // 0 is now the most recent; 1 the least
        cache.Add(keys[3], new byte[Blob]);

        Assert.True(cache.Contains(keys[0]));
        Assert.False(cache.Contains(keys[1]));
        Assert.True(cache.Contains(keys[2]));
        Assert.True(cache.Contains(keys[3]));
        Assert.Equal(new PropHullCacheStatistics(3, 3 * entry, 1, 0, 4, 1, 0), cache.Statistics);
    }

    [Fact]
    public void ALargeEntryEvictsAsManyAsItNeeds()
    {
        using PropHullCache cache = new(1000);
        for (int i = 0; i < 4; i++)
        {
            cache.Add(Key(i), new byte[100]); // 228 each: 912 held
        }

        cache.Add(Key(9), new byte[500]); // 628: three must go

        Assert.Equal(new PropHullCacheStatistics(2, 228 + 628, 0, 0, 5, 3, 0), cache.Statistics);
        Assert.True(cache.Contains(Key(3)));
        Assert.True(cache.Contains(Key(9)));
    }

    [Fact]
    public void AnEntryLargerThanTheWholeBoundIsNotStoredAndEvictsNothing()
    {
        using PropHullCache cache = new(1000);
        cache.Add(Key(0), new byte[100]);

        cache.Add(Key(1), new byte[1000]);

        Assert.True(cache.Contains(Key(0)));
        Assert.False(cache.Contains(Key(1)));
        Assert.Equal(1, cache.Statistics.Rejected);
        Assert.Equal(0, cache.Statistics.Evictions);
    }

    [Fact]
    public void AZeroBoundStoresNothing()
    {
        using PropHullCache cache = new(0);
        cache.Add(Key(0), null);

        Assert.Equal(0, cache.Statistics.Count);
        Assert.Equal(1, cache.Statistics.Rejected);
    }

    [Fact]
    public void ANegativeBoundIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropHullCache(-1));

    [Fact]
    public void TheFirstInsertOfAKeyWins()
    {
        // Two compiles that missed together both offer their cook; the
        // bytes are equal, and the second offer is only a use.
        using PropHullCache cache = new();
        cache.Add(Key(0), [1, 2, 3]);
        cache.Add(Key(0), [1, 2, 3]);

        Assert.Equal(1, cache.Statistics.Stored);
        Assert.Equal(PropHullCache.EntryOverheadBytes + 3, cache.Statistics.Bytes);
    }

    [Fact]
    public void ARepeatedOfferCountsAsAUse()
    {
        long entry = PropHullCache.EntryOverheadBytes;
        using PropHullCache cache = new(2 * entry);
        cache.Add(Key(0), null);
        cache.Add(Key(1), null);
        cache.Add(Key(0), null);

        cache.Add(Key(2), null);

        Assert.True(cache.Contains(Key(0)));
        Assert.False(cache.Contains(Key(1)));
    }

    [Fact]
    public void ClearDropsEverythingAndZeroesTheCounters()
    {
        using PropHullCache cache = new();
        cache.Add(Key(0), [1]);
        cache.TryGet(Key(0), out _);

        cache.Clear();

        Assert.Equal(default, cache.Statistics);
        Assert.False(cache.TryGet(Key(0), out _));
        cache.Add(Key(0), [1]);
        Assert.True(cache.Contains(Key(0)));
    }

    [Fact]
    public async Task ADisposedCacheIsAPassThroughForACompileStillHoldingIt()
    {
        CountingCooker cooker = new(new FakeCollisionCooker());
        PropHullCache shared = new();
        await CompileOnceAsync(cooker, shared, "m.mdl", [Box(8f)]);

        shared.Dispose();
        StaticPropHull after = await CompileOnceAsync(cooker, shared, "m.mdl", [Box(8f)]);

        Assert.NotNull(after.Blob);
        Assert.Equal(2, cooker.Runs);
        Assert.Equal(0, shared.Statistics.Count);
        Assert.Equal(0, shared.Statistics.Bytes);
        shared.Dispose(); // twice is fine
    }

    [Fact]
    public async Task ACookThatFailsLeavesNoEntry()
    {
        using PropHullCache shared = new();
        ThrowingCooker cooker = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() => CompileOnceAsync(cooker, shared, "m.mdl", [Box(8f)]));

        Assert.Equal(0, shared.Statistics.Count);
        Assert.Equal(0, shared.Statistics.Stored);
    }

    [Fact]
    public async Task ACookWhoseCompileIsCancelledWhileItRunsLeavesNoEntry()
    {
        // The cook itself finishes, as a native cook already inside the
        // library does; the compile was cancelled meanwhile.
        using PropHullCache shared = new();
        GatedCooker cooker = new(new FakeCollisionCooker());
        using CancellationTokenSource cancel = new();

        Task<StaticPropHull> compile = CompileOnceAsync(cooker, shared, "m.mdl", [Box(8f)], cancellationToken: cancel.Token);
        Assert.True(await cooker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        await cancel.CancelAsync();
        cooker.Release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compile);
        Assert.Equal(0, shared.Statistics.Count);
        Assert.False(shared.Contains(KeyOf(cooker, ComplianceOptions.Correct, Box(8f))));

        // The cache is fit for the next compile.
        StaticPropHull next = await CompileOnceAsync(new FakeCollisionCooker(), shared, "m.mdl", [Box(8f)]);
        Assert.NotNull(next.Blob);
        Assert.Equal(1, shared.Statistics.Count);
    }

    [Fact]
    public async Task ACompileCancelledBeforeItsCookStoresNothing()
    {
        using PropHullCache shared = new();
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CompileOnceAsync(new FakeCollisionCooker(), shared, "m.mdl", [Box(8f)], cancellationToken: cancel.Token));

        Assert.Equal(0, shared.Statistics.Count);
    }

    [Fact]
    public async Task ConcurrentCompilesSharingTheCacheGetTheSameBytes()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using PropHullCache shared = new();
        Vec3[][][] models = [.. Enumerable.Range(0, 12).Select(i => new[] { Box(4f + i), Box(2f + i, 30f) })];
        StaticPropHull[] expected = await Task.WhenAll(models.Select((m, i) => CompileOnceAsync(cooker, null, $"m{i}.mdl", m)));

        // Eight compiles at once, each naming every model in its own order.
        StaticPropHull[][] got = await Task.WhenAll(Enumerable.Range(0, 8).Select(c => Task.Run(async () =>
        {
            StaticPropHullCache compile = new(cooker, shared, ComplianceOptions.Correct);
            StaticPropHull[] hulls = new StaticPropHull[models.Length];
            foreach (int i in Enumerable.Range(0, models.Length).OrderBy(i => ((i * 7) + c) % models.Length))
            {
                hulls[i] = await compile.GetOrCookAsync($"m{i}.mdl", Meshes(models[i]));
            }

            return hulls;
        })));

        foreach (StaticPropHull[] compile in got)
        {
            Assert.Equal(expected.Select(h => h.Blob), compile.Select(h => h.Blob));
        }

        PropHullCacheStatistics stats = shared.Statistics;
        Assert.Equal(models.Length, stats.Count);
        Assert.Equal(8 * models.Length, stats.Hits + stats.Misses);
    }

    [Fact]
    public async Task TheBoundHoldsUnderConcurrentLoad()
    {
        const long Bound = 16 * 1024;
        using PropHullCache cache = new(Bound);
        long worst = 0;
        using CancellationTokenSource stop = new();
        Task watcher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                PropHullCacheStatistics s = cache.Statistics;
                Interlocked.Exchange(ref worst, Math.Max(Interlocked.Read(ref worst), s.Bytes));
                await Task.Yield();
            }
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            Random random = new(t);
            for (int i = 0; i < 4000; i++)
            {
                int k = random.Next(400);
                if (!cache.TryGet(Key(k), out _))
                {
                    cache.Add(Key(k), new byte[k % 3 == 0 ? 0 : random.Next(1, 1500)]);
                }
            }
        })));
        await stop.CancelAsync();
        await watcher;

        PropHullCacheStatistics end = cache.Statistics;
        Assert.InRange(worst, 0, Bound);
        Assert.InRange(end.Bytes, 0, Bound);
        Assert.True(end.Evictions > 0);
        Assert.Equal(end.Stored - end.Evictions, end.Count);
        Assert.Equal(8 * 4000, end.Hits + end.Misses);
    }

    private static PropHullKey Key(int i) => new((ulong)i, 0, 0, 0);

    // Counts cooks, optionally under another identity.
    private sealed class CountingCooker(ICollisionCooker inner, string? identity = null) : ICollisionCooker
    {
        private int _runs;

        public int Runs => _runs;

        public string CookerIdentity => identity ?? inner.CookerIdentity;

        public Task<T> RunAsync<T>(Func<ICollisionSession, T> work, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runs);
            return inner.RunAsync(work, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingCooker : ICollisionCooker
    {
        public string CookerIdentity => "throws";

        public Task<T> RunAsync<T>(Func<ICollisionSession, T> work, CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new InvalidOperationException("the library fell over"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Holds each cook inside the library until released, then returns its
    // result whatever the token says, as a cook already running does.
    private sealed class GatedCooker(ICollisionCooker inner) : ICollisionCooker
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string CookerIdentity => inner.CookerIdentity;

        public async Task<T> RunAsync<T>(Func<ICollisionSession, T> work, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await Release.Task.ConfigureAwait(false);
            return await inner.RunAsync(work, CancellationToken.None).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
