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
