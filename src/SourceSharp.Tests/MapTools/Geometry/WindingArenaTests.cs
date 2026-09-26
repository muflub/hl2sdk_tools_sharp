//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Geometry;

/// <summary>
/// The pool itself: the replacement for <c>winding_pool</c> and the
/// lock-per-allocation.
/// </summary>
public class WindingArenaTests
{
    [Fact]
    public void AnAllocatedWindingStartsWithNoPoints()
    {
        // -- "None are occupied yet even though allocated".
        var arena = new WindingArena();
        Winding w = arena.Alloc(8);
        Assert.Equal(0, w.Count);
        Assert.Equal(8, w.Capacity);
    }

    [Fact]
    public void TheDefaultHandleIsNull()
    {
        // A real winding always has a capacity of at least one, so
        // default(Winding) cannot be confused with a handle to slot zero.
        Assert.True(default(Winding).IsNull);
        Assert.True(Winding.Null.IsNull);
    }

    [Fact]
    public void AnAllocatedWindingIsNotNull()
    {
        var arena = new WindingArena();
        Assert.False(arena.Alloc(1).IsNull);
    }

    [Fact]
    public void AllocRejectsAZeroCapacity()
    {
        var arena = new WindingArena();
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Alloc(0));
    }

    [Fact]
    public void FreedStorageIsRecycledRatherThanGrowingTheSlab()
    {
        // The whole justification for the arena. Stock recycles too, behind a
        // global lock; this measures that the recycling actually happens.
        var arena = new WindingArena();
        arena.Free(arena.Alloc(8));
        int slab = arena.SlabCapacity;

        arena.Alloc(8);

        Assert.Equal(1, arena.RecycledAllocations);
        Assert.Equal(slab, arena.SlabCapacity);
    }

    [Fact]
    public void RecyclingIsKeyedOnCapacitySoADifferentSizeTakesFreshStorage()
    {
        // winding_pool is indexed by maxpoints, so a freed
        // 8-point winding is no use to a request for 9.
        var arena = new WindingArena();
        arena.Free(arena.Alloc(8));
        arena.Alloc(9);

        Assert.Equal(0, arena.RecycledAllocations);
    }

    [Fact]
    public void FreeingAWindingTwiceIsDetected()
    {
        // stamps 0xdeaddead and errors with "freed a freed
        // winding". A double free in the clipper otherwise surfaces much later
        // as a winding holding someone else's points.
        var arena = new WindingArena();
        Winding w = arena.Alloc(4);
        arena.Free(w);

        Assert.Throws<InvalidWindingException>(() => arena.Free(w));
    }

    [Fact]
    public void FreeingTheNullWindingDoesNothing()
    {
        // The `if (b) FreeWinding(b)` pattern wants this.
        var arena = new WindingArena();
        arena.Free(Winding.Null);
        Assert.Equal(0, arena.ActiveWindings);
    }

    [Fact]
    public void ActiveWindingsCountsWhatIsOutstanding()
    {
        var arena = new WindingArena();
        Winding a = arena.Alloc(4);
        arena.Alloc(4);
        Assert.Equal(2, arena.ActiveWindings);

        arena.Free(a);
        Assert.Equal(1, arena.ActiveWindings);
    }

    [Fact]
    public void PeakWindingsRemembersTheHighWaterMark()
    {
        // Stock's c_peak_windings, which it can only maintain single-threaded
        // because the counters are, in its own words, "an awefull coherence
        // problem". Per-worker arenas remove the problem.
        var arena = new WindingArena();
        Winding a = arena.Alloc(4);
        Winding b = arena.Alloc(4);
        arena.Free(a);
        arena.Free(b);

        Assert.Equal(0, arena.ActiveWindings);
        Assert.Equal(2, arena.PeakWindings);
    }

    [Fact]
    public void TotalAllocationsCountsEveryRequestIncludingRecycledOnes()
    {
        var arena = new WindingArena();
        arena.Free(arena.Alloc(4));
        arena.Alloc(4);

        Assert.Equal(2, arena.TotalAllocations);
    }

    [Fact]
    public void SetCountRejectsACountAboveTheCapacity()
    {
        var arena = new WindingArena();
        Winding w = arena.Alloc(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.SetCount(w, 5));
    }

    [Fact]
    public void PointsIsTheLivePrefixAndStorageIsTheWholeReservation()
    {
        var arena = new WindingArena();
        Winding w = arena.SetCount(arena.Alloc(8), 3);
        Assert.Equal(3, arena.Points(w).Length);
        Assert.Equal(8, arena.Storage(w).Length);
    }

    [Fact]
    public void CreateFillsAWindingFromASpan()
    {
        var arena = new WindingArena();
        Vec3[] points = [new(1f, 2f, 3f), new(4f, 5f, 6f), new(7f, 8f, 9f)];
        Winding w = arena.Create(points);

        Assert.Equal(3, w.Count);
        Assert.Equal(points, arena.Points(w).ToArray());
    }

    [Fact]
    public void RecycledStorageIsHandedOutIntactAndTheArenaDoesNotZeroIt()
    {
        // Stock does not clear on free either -- AllocWinding only resets
        // numpoints. A caller that reads Storage before
        // filling it sees the previous winding's points, which is why Alloc
        // hands back a count of zero and nothing else.
        var arena = new WindingArena();
        Winding first = arena.Create([new Vec3(1f, 2f, 3f)]);
        arena.Free(first);

        Winding second = arena.Alloc(1);
        Assert.Equal(0, second.Count);
        Assert.Equal(new Vec3(1f, 2f, 3f), arena.Storage(second)[0]);
    }

    [Fact]
    public void TheSlabGrowsToHoldMoreThanItStartedWith()
    {
        var arena = new WindingArena(4);
        for (int i = 0; i < 100; i++)
        {
            arena.Alloc(4);
        }

        Assert.True(arena.SlabCapacity >= 400);
    }

    [Fact]
    public void AHandleTakenBeforeAGrowthStillNamesTheSameWinding()
    {
        // Spans are invalidated by growth; HANDLES are not. That is the point
        // of an (offset, count) handle rather than a pointer.
        var arena = new WindingArena(4);
        Winding first = arena.Create([new Vec3(1f, 2f, 3f)]);

        for (int i = 0; i < 100; i++)
        {
            arena.Alloc(4);
        }

        Assert.Equal(new Vec3(1f, 2f, 3f), arena.Points(first)[0]);
    }

    [Fact]
    public void MaxPointsOnWindingIsSixtyFour() =>
        Assert.Equal(64, WindingArena.MaxPointsOnWinding);
}
