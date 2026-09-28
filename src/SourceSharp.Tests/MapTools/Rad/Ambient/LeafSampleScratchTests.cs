//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// <see cref="LeafSampleScratch"/>: a worker's batch-long arena of sample
/// positions and ray cubes, rented from a pool, whose runs survive growth and
/// whose arrays go back exactly once.
/// </summary>
public sealed class LeafSampleScratchTests
{
    private static Vec3 Mark(int sample, int k) => new(sample, k, (sample * 7) + k);

    private static void Fill(LeafSampleScratch scratch, int offset, int count)
    {
        for (int s = 0; s < count; s++)
        {
            scratch.Positions(offset + s, 1)[0] = Mark(offset + s, -1);
            Span<Vec3> cube = scratch.Cubes(offset + s, 1);
            for (int k = 0; k < AmbientCube.Sides; k++)
            {
                cube[k] = Mark(offset + s, k);
            }
        }
    }

    private static void AssertMarked(LeafSampleScratch scratch, int offset, int count)
    {
        ReadOnlySpan<Vec3> positions = scratch.Positions(offset, count);
        ReadOnlySpan<Vec3> cubes = scratch.Cubes(offset, count);
        for (int s = 0; s < count; s++)
        {
            Assert.Equal(Mark(offset + s, -1), positions[s]);
            for (int k = 0; k < AmbientCube.Sides; k++)
            {
                Assert.Equal(Mark(offset + s, k), cubes[(s * AmbientCube.Sides) + k]);
            }
        }
    }

    /// <summary>
    /// A batch's leaves reserve runs one after another; growing past the
    /// first rental copies every run so far, so earlier offsets still read
    /// what was written there.
    /// </summary>
    [Fact]
    public void RunsSurviveGrowth()
    {
        RecyclingScratchPool pool = new();
        using LeafSampleScratch scratch = new(pool);
        int[] leaves = [1, 128, 3, 128, 128, 50];
        List<(int Offset, int Count)> runs = [];

        foreach (int count in leaves)
        {
            int offset = scratch.Reserve(count);
            Fill(scratch, offset, count);
            runs.Add((offset, count));
        }

        Assert.Equal(leaves.Sum(), scratch.Used);
        Assert.True(scratch.Capacity >= scratch.Used);
        Assert.All(runs, r => AssertMarked(scratch, r.Offset, r.Count));
        Assert.Equal(0, pool.BadReturns);
        Assert.Equal(2, pool.Outstanding);
    }

    /// <summary>
    /// Reset keeps the storage for the next batch: a small batch after a large
    /// one rents nothing, and a large one after a small grows again; either
    /// way a run reads only what its own leaf wrote.
    /// </summary>
    [Fact]
    public void ResetKeepsTheStorageAndLargerOrSmallerBatchesReadOnlyTheirOwn()
    {
        RecyclingScratchPool pool = new();
        using LeafSampleScratch scratch = new(pool);

        int big = scratch.Reserve(600);
        Fill(scratch, big, 600);
        int capacity = scratch.Capacity;
        int rented = pool.Rented;

        scratch.Reset();
        int small = scratch.Reserve(2);
        Assert.Equal(0, small);
        Assert.Equal(capacity, scratch.Capacity);
        Assert.Equal(rented, pool.Rented);
        Assert.Throws<ArgumentOutOfRangeException>(() => scratch.Positions(0, 3));
        Fill(scratch, small, 2);
        AssertMarked(scratch, small, 2);

        scratch.Reset();
        int bigger = scratch.Reserve(capacity + 1);
        Fill(scratch, bigger, capacity + 1);
        Assert.True(scratch.Capacity > capacity);
        AssertMarked(scratch, bigger, capacity + 1);
    }

    [Fact]
    public void RangesOutsideWhatWasReservedAreRefused()
    {
        using LeafSampleScratch scratch = new(new RecyclingScratchPool());
        int offset = scratch.Reserve(4);

        Assert.Equal(0, scratch.Positions(offset, 0).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => scratch.Positions(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => scratch.Positions(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => scratch.Cubes(2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => scratch.Reserve(-1));
        Assert.Throws<ArgumentNullException>(() => new LeafSampleScratch(null!));
    }

    [Fact]
    public void DisposingReturnsBothArraysOnceAndRefusesFurtherReservations()
    {
        RecyclingScratchPool pool = new();
        LeafSampleScratch scratch = new(pool);
        scratch.Reserve(10);
        scratch.Planes.Add(new LeafPlane(Vec3.Zero, 1));
        scratch.Samples.Add(default);

        scratch.Dispose();
        scratch.Dispose();

        Assert.Equal(2, pool.Rented);
        Assert.Equal(2, pool.Returned);
        Assert.Equal(0, pool.BadReturns);
        Assert.Equal(0, scratch.Used);
        Assert.Empty(scratch.Planes);
        Assert.Empty(scratch.Samples);
        Assert.Throws<ObjectDisposedException>(() => scratch.Reserve(1));
    }

    [Fact]
    public void ScratchThatNeverReservesRentsNothing()
    {
        RecyclingScratchPool pool = new();
        LeafSampleScratch scratch = new(pool);

        scratch.Reserve(0);
        scratch.Dispose();

        Assert.Equal(0, pool.Rented);
    }

    /// <summary>A failed rental while growing gives back what the growth had rented and keeps the runs.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void AFailedRentalWhileGrowingLeaksNothing(int failAt)
    {
        RecyclingScratchPool pool = new() { FailRentNumber = failAt };
        LeafSampleScratch scratch = new(pool);
        int first = scratch.Reserve(LeafAmbientBuilder.MaxSampleCount);
        Fill(scratch, first, LeafAmbientBuilder.MaxSampleCount);

        Assert.Throws<OutOfMemoryException>(() => scratch.Reserve(1));

        Assert.Equal(2, pool.Outstanding);
        Assert.Equal(LeafAmbientBuilder.MaxSampleCount, scratch.Used);
        AssertMarked(scratch, first, LeafAmbientBuilder.MaxSampleCount);
        scratch.Dispose();
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }
}
