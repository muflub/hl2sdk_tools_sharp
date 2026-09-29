//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// <see cref="CompileScratchPool"/>: exact fresh arrays, best-fit reuse by
/// type, trimming, and an end that keeps nothing; and
/// <see cref="UnpooledScratch"/>, which keeps nothing ever.
/// </summary>
public sealed class CompileScratchPoolTests
{
    [Fact]
    public void AFreshArrayIsExactlyAsLongAsAsked()
    {
        using CompileScratchPool pool = new();

        Ray[] rays = pool.Rent<Ray>(217_599);

        Assert.Equal(217_599, rays.Length);
        Assert.Equal(1, pool.Allocations);
        Assert.Equal(217_599L * System.Runtime.CompilerServices.Unsafe.SizeOf<Ray>(), pool.AllocatedBytes);
        Assert.Equal(1, pool.Outstanding);
    }

    [Fact]
    public void ZeroIsAnEmptyArrayThatCostsNothing()
    {
        using CompileScratchPool pool = new();

        int[] none = pool.Rent<int>(0);
        pool.Return(none);

        Assert.Empty(none);
        Assert.Equal(0, pool.Allocations);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.IdleArrays);
    }

    [Fact]
    public void BadArgumentsAreRefused()
    {
        using CompileScratchPool pool = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent<int>(-1));
        Assert.Throws<ArgumentNullException>(() => pool.Return<int>(null!));
    }

    /// <summary>An idle array serves a request for exactly its length, and not one element more.</summary>
    [Fact]
    public void AnIdleArrayServesItsExactLengthButNotOnePast()
    {
        using CompileScratchPool pool = new();
        int[] first = pool.Rent<int>(64);
        pool.Return(first);

        int[] again = pool.Rent<int>(64);
        pool.Return(again);
        int[] onePast = pool.Rent<int>(65);

        Assert.Same(first, again);
        Assert.NotSame(first, onePast);
        Assert.Equal(65, onePast.Length);
        Assert.Equal(1, pool.Reuses);
        Assert.Equal(2, pool.Allocations);
        Assert.Equal(1, pool.IdleArrays);
    }

    /// <summary>Of the idle arrays long enough, the shortest is lent, so a long one waits for a long request.</summary>
    [Fact]
    public void TheShortestIdleArrayThatFitsIsLent()
    {
        using CompileScratchPool pool = new();
        int[] ten = pool.Rent<int>(10);
        int[] hundred = pool.Rent<int>(100);
        int[] fifty = pool.Rent<int>(50);
        pool.Return(ten);
        pool.Return(hundred);
        pool.Return(fifty);

        Assert.Same(fifty, pool.Rent<int>(40));
        Assert.Same(hundred, pool.Rent<int>(60));
        Assert.Same(ten, pool.Rent<int>(1));
        Assert.Equal(101, pool.Rent<int>(101).Length);
        Assert.Equal(4, pool.Allocations);
        Assert.Equal(3, pool.Reuses);
    }

    [Fact]
    public void AnArrayIsLentOnlyAsItsOwnType()
    {
        using CompileScratchPool pool = new();
        pool.Return(pool.Rent<int>(100));

        float[] floats = pool.Rent<float>(10);

        Assert.Equal(10, floats.Length);
        Assert.Equal(2, pool.Allocations);
        Assert.Equal(1, pool.IdleArrays);
    }

    [Fact]
    public void IdleBytesFollowWhatIsHeld()
    {
        using CompileScratchPool pool = new();
        long[] a = pool.Rent<long>(10);
        byte[] b = pool.Rent<byte>(7);

        pool.Return(a);
        pool.Return(b);
        Assert.Equal(87, pool.IdleBytes);
        Assert.Equal(2, pool.IdleArrays);

        _ = pool.Rent<long>(3);
        Assert.Equal(7, pool.IdleBytes);
    }

    /// <summary>Trimming lets the idle arrays go; one still out is untouched, and kept once it comes back.</summary>
    [Fact]
    public void TrimmingDropsOnlyTheIdleArrays()
    {
        using CompileScratchPool pool = new();
        int[] held = pool.Rent<int>(32);
        pool.Return(pool.Rent<int>(16));

        pool.Trim();
        Assert.Equal(0, pool.IdleArrays);
        Assert.Equal(0, pool.IdleBytes);
        Assert.Equal(1, pool.Outstanding);

        pool.Return(held);
        Assert.Equal(1, pool.IdleArrays);
        Assert.Same(held, pool.Rent<int>(20));
    }

    /// <summary>
    /// The end of the compile: nothing idle is kept, a later rental is
    /// refused, a late return is let go rather than kept, and ending twice
    /// is harmless.
    /// </summary>
    [Fact]
    public void AnEndedPoolKeepsNothingAndLendsNothing()
    {
        CompileScratchPool pool = new();
        int[] late = pool.Rent<int>(8);
        pool.Return(pool.Rent<int>(4));

        pool.Dispose();
        Assert.Equal(0, pool.IdleArrays);
        Assert.Throws<ObjectDisposedException>(() => pool.Rent<int>(4));

        pool.Return(late);
        pool.Dispose();
        Assert.Equal(0, pool.IdleArrays);
        Assert.Equal(0, pool.IdleBytes);
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>An allocation that fails leaves the counts as they were.</summary>
    [Fact]
    public void AFailedAllocationIsNotCounted()
    {
        using CompileScratchPool pool = new();

        Assert.Throws<OutOfMemoryException>(() => pool.Rent<Ray>(int.MaxValue));

        Assert.Equal(0, pool.Allocations);
        Assert.Equal(0, pool.AllocatedBytes);
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>
    /// Workers renting and returning at once never get the same array, and
    /// the pool ends holding every array it made: a steady state allocates
    /// nothing once each worker's sizes exist.
    /// </summary>
    [Fact]
    public async Task ConcurrentWorkersShareThePoolWithoutSharingAnArray()
    {
        using CompileScratchPool pool = new();
        const int Workers = 8;
        int[] sizes = [64, 128, 256, 512, 1024];
        int clashes = 0;

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(() =>
        {
            for (int round = 0; round < 200; round++)
            {
                int[] mine = pool.Rent<int>(sizes[(w + round) % sizes.Length]);
                Array.Fill(mine, w);
                Thread.SpinWait(50);
                if (mine.Any(v => v != w))
                {
                    Interlocked.Increment(ref clashes);
                }

                pool.Return(mine);
            }
        })));

        Assert.Equal(0, clashes);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(pool.Allocations, pool.IdleArrays);
        Assert.True(pool.Allocations <= Workers * sizes.Length, $"{pool.Allocations} arrays for {Workers} workers");
        Assert.Equal((Workers * 200) - pool.Allocations, pool.Reuses);
    }

    /// <summary>One shard per worker index: the same index is the same shard, and a bad index is refused.</summary>
    [Fact]
    public void EachWorkerIndexHasOneShard()
    {
        using CompileScratchPool pool = new();

        IScratchArrayPool three = pool.ForWorker(3);

        Assert.Same(three, pool.ForWorker(3));
        Assert.NotSame(pool.ForWorker(0), pool.ForWorker(1));
        Assert.NotSame(pool, pool.ForWorker(0));
        Assert.Equal(4, pool.WorkerShards);
        Assert.Same(pool.ForWorker(2), three.ForWorker(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.ForWorker(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => three.Rent<int>(-1));
        Assert.Throws<ArgumentNullException>(() => three.Return<int>(null!));
        Assert.Empty(three.Rent<int>(0));
        Assert.Equal(0, pool.Statistics.Rentals);
    }

    /// <summary>A pool that has nothing to shard hands every worker itself.</summary>
    [Fact]
    public void APoolWithoutShardsIsItsOwnWorkerView()
    {
        UnpooledScratch scratch = new();
        IScratchArrayPool pool = scratch;

        Assert.Same(scratch, pool.ForWorker(5));
    }

    /// <summary>
    /// A worker's own shard is looked at first: its own long array serves it
    /// even while another shard holds a closer fit, and nothing is borrowed.
    /// </summary>
    [Fact]
    public void AWorkerIsServedFromItsOwnShardFirst()
    {
        using CompileScratchPool pool = new();
        IScratchArrayPool w0 = pool.ForWorker(0);
        IScratchArrayPool w1 = pool.ForWorker(1);
        int[] own = w0.Rent<int>(100);
        int[] closer = w1.Rent<int>(50);
        w0.Return(own);
        w1.Return(closer);

        Assert.Same(own, w0.Rent<int>(40));
        Assert.Equal(1, pool.Statistics.Hits);
        Assert.Equal(0, pool.Statistics.CrossWorkerHits);
        Assert.Same(closer, w1.Rent<int>(40));
    }

    /// <summary>
    /// A worker whose shard has nothing long enough takes the shortest idle
    /// array that fits from any other shard, the shared one included -- and
    /// an exact length fits there too, one element short does not.
    /// </summary>
    [Fact]
    public void AWorkerWithNothingThatFitsBorrowsTheBestFitFromTheOtherShards()
    {
        using CompileScratchPool pool = new();
        IScratchArrayPool w0 = pool.ForWorker(0);
        IScratchArrayPool w1 = pool.ForWorker(1);
        IScratchArrayPool w2 = pool.ForWorker(2);
        int[] hundred = w1.Rent<int>(100);
        int[] sixty = w1.Rent<int>(60);
        int[] eighty = pool.Rent<int>(80);
        int[] small = w0.Rent<int>(10);
        w1.Return(hundred);
        w1.Return(sixty);
        pool.Return(eighty);
        w0.Return(small);

        Assert.Same(sixty, w0.Rent<int>(50));
        Assert.Same(eighty, w2.Rent<int>(80));
        Assert.NotSame(hundred, w2.Rent<int>(101));
        Assert.Same(hundred, pool.Rent<int>(61));

        ScratchPoolStatistics stats = pool.Statistics;
        Assert.Equal(3, stats.CrossWorkerHits);
        Assert.Equal(3, stats.Hits);
        Assert.Equal(5, stats.Misses);
        Assert.Equal(5, pool.Allocations);
        Assert.Equal(1, pool.IdleArrays);
    }

    /// <summary>
    /// An array rented through one worker's shard and handed back through
    /// another's (a stage disposing its workers from one thread does that)
    /// is kept where it came back, and the counts still balance.
    /// </summary>
    [Fact]
    public void AnArrayReturnedThroughAnotherShardIsKeptThere()
    {
        using CompileScratchPool pool = new();
        IScratchArrayPool w0 = pool.ForWorker(0);
        IScratchArrayPool w1 = pool.ForWorker(1);
        float[] array = w0.Rent<float>(32);

        w1.Return(array);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(1, pool.IdleArrays);

        Assert.Same(array, w1.Rent<float>(32));
        Assert.Equal(0, pool.Statistics.CrossWorkerHits);
        pool.Return(array);
        Assert.Same(array, w0.Rent<float>(1));
        Assert.Equal(1, pool.Statistics.CrossWorkerHits);
        Assert.Equal(1, pool.Outstanding);
    }

    /// <summary>
    /// Workers each on a shard of their own, renting and returning at once,
    /// half of their returns through the next worker's shard: no array is
    /// ever held by two workers at once, and the pool ends holding every
    /// array it made, with counts that agree with each other.
    /// </summary>
    [Fact]
    public async Task ShardedWorkersNeverHoldTheSameArrayAndReturnAcrossShards()
    {
        using CompileScratchPool pool = new();
        const int Workers = 8;
        const int Rounds = 300;
        int[] sizes = [64, 100, 256, 1000, 4096];
        System.Collections.Concurrent.ConcurrentDictionary<int[], int> held = new(ReferenceEqualityComparer.Instance);
        int clashes = 0;

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(() =>
        {
            IScratchArrayPool mine = pool.ForWorker(w);
            IScratchArrayPool next = pool.ForWorker((w + 1) % Workers);
            for (int round = 0; round < Rounds; round++)
            {
                int[] array = mine.Rent<int>(sizes[(w + round) % sizes.Length]);
                if (!held.TryAdd(array, w))
                {
                    Interlocked.Increment(ref clashes);
                }

                Array.Fill(array, w);
                Thread.SpinWait(20);
                if (array.Any(v => v != w))
                {
                    Interlocked.Increment(ref clashes);
                }

                _ = held.TryRemove(array, out _);
                (round % 2 == 0 ? mine : next).Return(array);
            }
        })));

        ScratchPoolStatistics stats = pool.Statistics;
        Assert.Equal(0, clashes);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(pool.Allocations, pool.IdleArrays);
        Assert.Equal(Workers * Rounds, stats.Rentals);
        Assert.Equal(pool.Allocations, stats.Misses);
        Assert.Equal(pool.Reuses, stats.Hits);
        Assert.True(stats.CrossWorkerHits <= stats.Hits);
        Assert.True(pool.Allocations <= Workers * sizes.Length, $"{pool.Allocations} arrays for {Workers} workers");
        Assert.Equal(Workers, pool.WorkerShards);
    }

    /// <summary>
    /// Trimming empties every shard, and a later miss that an array the
    /// trim let go would have served is counted as the trim's cost -- once
    /// per dropped array, and only for a long enough one of the same type.
    /// </summary>
    [Fact]
    public void TrimmingEmptiesEveryShardAndCountsWhatItCostsLater()
    {
        using CompileScratchPool pool = new();
        IScratchArrayPool w0 = pool.ForWorker(0);
        IScratchArrayPool w1 = pool.ForWorker(1);
        int[] held = w1.Rent<int>(7);
        w0.Return(w0.Rent<int>(100));
        pool.Return(pool.Rent<long>(10));

        pool.Trim();
        Assert.Equal(0, pool.IdleArrays);
        Assert.Equal(0, pool.IdleBytes);
        Assert.Equal(1, pool.Outstanding);
        Assert.Equal(2, pool.Statistics.TrimmedArrays);
        Assert.Equal(480, pool.Statistics.TrimmedBytes);

        _ = w1.Rent<float>(50);
        Assert.Equal(0, pool.Statistics.TrimRegrets);
        _ = w1.Rent<int>(101);
        Assert.Equal(0, pool.Statistics.TrimRegrets);
        _ = w1.Rent<int>(80);
        Assert.Equal(1, pool.Statistics.TrimRegrets);
        Assert.Equal(320, pool.Statistics.TrimRegretBytes);
        _ = w0.Rent<int>(50);
        Assert.Equal(1, pool.Statistics.TrimRegrets);

        w1.Return(held);
        Assert.Same(held, w1.Rent<int>(7));
    }

    /// <summary>
    /// A second trim keeps what the first let go and no miss has claimed,
    /// and adds its own: every dropped array is a possible regret once, in
    /// whichever stage wants it.
    /// </summary>
    [Fact]
    public void ASecondTrimKeepsTheUnclaimedDropsOfTheFirst()
    {
        using CompileScratchPool pool = new();
        IScratchArrayPool w0 = pool.ForWorker(0);
        int[] held = w0.Rent<int>(30);
        int[] hundred = w0.Rent<int>(100);
        int[] fifty = w0.Rent<int>(50);
        w0.Return(hundred);
        w0.Return(fifty);

        pool.Trim();
        _ = w0.Rent<int>(40);
        Assert.Equal(1, pool.Statistics.TrimRegrets);

        w0.Return(held);
        pool.Trim();
        Assert.Equal(3, pool.Statistics.TrimmedArrays);
        _ = w0.Rent<int>(45);
        Assert.Equal(2, pool.Statistics.TrimRegrets);
        _ = w0.Rent<int>(45);
        Assert.Equal(2, pool.Statistics.TrimRegrets);
        _ = w0.Rent<int>(20);
        Assert.Equal(3, pool.Statistics.TrimRegrets);
        _ = w0.Rent<int>(1);
        Assert.Equal(3, pool.Statistics.TrimRegrets);
        Assert.Equal((40 + 45 + 20) * sizeof(int), pool.Statistics.TrimRegretBytes);
    }

    /// <summary>A trim with nothing idle records nothing.</summary>
    [Fact]
    public void TrimmingAnEmptyPoolCostsNothing()
    {
        using CompileScratchPool pool = new();
        int[] held = pool.ForWorker(0).Rent<int>(5);

        pool.Trim();
        _ = pool.Rent<int>(5);

        Assert.Equal(0, pool.Statistics.TrimmedArrays);
        Assert.Equal(0, pool.Statistics.TrimRegrets);
        Assert.Equal(5, held.Length);
    }

    /// <summary>Hits, misses and bytes are kept by element type and size class, a power of two apart.</summary>
    [Fact]
    public void RentalsAreCountedByTypeAndSizeClass()
    {
        using CompileScratchPool pool = new();
        IScratchArrayPool w0 = pool.ForWorker(0);
        w0.Return(w0.Rent<int>(64));
        _ = w0.Rent<int>(33);
        _ = w0.Rent<Ray>(65);
        _ = pool.Rent<int>(1);

        IReadOnlyList<ScratchRentalCounts> counts = pool.Statistics.ByTypeAndSize;

        ScratchRentalCounts ints = Assert.Single(counts, c => c.ElementType == typeof(int) && c.SizeClass == 6);
        Assert.Equal(1, ints.Hits);
        Assert.Equal(1, ints.Misses);
        Assert.Equal(256, ints.AllocatedBytes);
        ScratchRentalCounts rays = Assert.Single(counts, c => c.ElementType == typeof(Ray));
        Assert.Equal(7, rays.SizeClass);
        Assert.Equal(65L * System.Runtime.CompilerServices.Unsafe.SizeOf<Ray>(), rays.AllocatedBytes);
        Assert.Single(counts, c => c.ElementType == typeof(int) && c.SizeClass == 0);
        Assert.Equal(3, counts.Count);
        Assert.Equal(4, pool.Statistics.Rentals);
    }

    /// <summary>
    /// The pool does not clear what it hands out: a reused array still holds
    /// what its last renter wrote. A poisoned pool fills every array it
    /// hands out, fresh or reused, which is how the facts show no renter
    /// depends on the contents.
    /// </summary>
    [Fact]
    public void ThePoolDoesNotClearAndAPoisonedPoolFillsEveryArray()
    {
        using CompileScratchPool plain = new();
        int[] a = plain.Rent<int>(4);
        Array.Fill(a, 7);
        plain.Return(a);
        Assert.All(plain.Rent<int>(4), v => Assert.Equal(7, v));

        using CompileScratchPool poisoned = new() { PoisonByte = 0xCD };
        IScratchArrayPool w0 = poisoned.ForWorker(0);
        int[] fresh = w0.Rent<int>(4);
        Assert.All(fresh, v => Assert.Equal(unchecked((int)0xCDCDCDCD), v));
        Array.Fill(fresh, 7);
        w0.Return(fresh);
        int[] reused = poisoned.Rent<int>(3);
        Assert.Same(fresh, reused);
        Assert.All(reused, v => Assert.Equal(unchecked((int)0xCDCDCDCD), v));

        using CompileScratchPool nan = new() { PoisonByte = 0xFF };
        Assert.All(nan.Rent<float>(3), v => Assert.True(float.IsNaN(v)));
        string?[] references = nan.Rent<string?>(2);
        Assert.All(references, Assert.Null);
    }

    /// <summary>
    /// Ending the pool with arrays out in several shards: every shard lets
    /// its idle arrays go, a worker's later rental is refused, and returns
    /// through any shard are let go.
    /// </summary>
    [Fact]
    public void AnEndedPoolKeepsNothingInAnyShard()
    {
        CompileScratchPool pool = new();
        IScratchArrayPool w0 = pool.ForWorker(0);
        IScratchArrayPool w1 = pool.ForWorker(1);
        w0.Return(w0.Rent<int>(8));
        w1.Return(w1.Rent<int>(8));
        int[] late = w1.Rent<int>(16);
        pool.Trim();
        w0.Return(w0.Rent<int>(4));

        pool.Dispose();
        Assert.Equal(0, pool.IdleArrays);
        Assert.Throws<ObjectDisposedException>(() => w0.Rent<int>(4));
        Assert.Throws<ObjectDisposedException>(() => pool.ForWorker(1).Rent<int>(4));

        w0.Return(late);
        Assert.Equal(0, pool.IdleArrays);
        Assert.Equal(0, pool.IdleBytes);
        Assert.Equal(0, pool.Outstanding);
        pool.Dispose();
    }

    /// <summary>
    /// The end racing returns and trims on other threads, as a cancelled
    /// compile's stage ends while its workers still hand arrays back:
    /// whichever wins each race, the ended pool holds nothing and the
    /// counts still balance.
    /// </summary>
    [Fact]
    public async Task AnEndRacingReturnsAndTrimsKeepsNothing()
    {
        for (int trial = 0; trial < 20; trial++)
        {
            CompileScratchPool pool = new();
            const int Workers = 4;
            int[][][] rented = [.. Enumerable.Range(0, Workers).Select(w =>
                Enumerable.Range(0, 64).Select(i => pool.ForWorker(w).Rent<int>(8 + i)).ToArray())];
            using Barrier start = new(Workers + 2);

            Task[] returns = [.. Enumerable.Range(0, Workers).Select(w => Task.Run(() =>
            {
                IScratchArrayPool mine = pool.ForWorker(w);
                start.SignalAndWait();
                foreach (int[] array in rented[w])
                {
                    mine.Return(array);
                }
            }))];
            Task trims = Task.Run(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < 8; i++)
                {
                    pool.Trim();
                }
            });

            start.SignalAndWait();
            pool.Dispose();
            await Task.WhenAll([.. returns, trims]);

            Assert.Equal(0, pool.IdleArrays);
            Assert.Equal(0, pool.IdleBytes);
            Assert.Equal(0, pool.Outstanding);
            Assert.Throws<ObjectDisposedException>(() => pool.ForWorker(0).Rent<int>(1));
        }
    }

    [Fact]
    public void TheUnpooledScratchMakesExactArraysAndKeepsNone()
    {
        UnpooledScratch scratch = new();
        int[] a = scratch.Rent<int>(5);
        scratch.Return(a);

        Assert.Equal(5, a.Length);
        Assert.NotSame(a, scratch.Rent<int>(5));
        Assert.Empty(scratch.Rent<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => scratch.Rent<int>(-1));
        Assert.Throws<ArgumentNullException>(() => scratch.Return<int>(null!));
    }
}
