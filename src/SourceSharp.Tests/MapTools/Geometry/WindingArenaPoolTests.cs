//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Geometry;

/// <summary>
/// <see cref="WindingArena.Reset"/> and <see cref="WindingArenaPool"/>: a
/// reused arena must be indistinguishable from a new one to everything that
/// allocates in it, and a pool must give back what it was given and keep
/// nothing once released.
/// </summary>
public sealed class WindingArenaPoolTests
{
    // ---- Reset --------------------------------------------------------------

    /// <summary>
    /// The same script of allocations and frees, run on a new arena and on
    /// one that already served (and grew for) another owner, hands out the
    /// same handles in the same order: offsets, counts and capacities.
    /// </summary>
    [Fact]
    public void AResetArenaHandsOutTheHandlesANewArenaWould()
    {
        WindingArena used = new();
        for (int i = 0; i < 3 * WindingArena.SegmentLength / 64; i++)
        {
            Winding w = used.Alloc(64);
            if (i % 3 == 0)
            {
                used.Free(w);
            }
        }

        used.Reset();

        Assert.Equal(Script(new WindingArena()), Script(used));
    }

    [Fact]
    public void AResetArenaKeepsItsStorageAndForgetsItsCounters()
    {
        WindingArena arena = new(16);
        for (int i = 0; i < 200; i++)
        {
            arena.Alloc(8);
        }

        arena.Free(arena.Alloc(8));
        int grown = arena.SlabCapacity;

        arena.Reset();

        Assert.Equal(grown, arena.SlabCapacity);
        Assert.Equal(0, arena.ActiveWindings);
        Assert.Equal(0, arena.PeakWindings);
        Assert.Equal(0, arena.TotalAllocations);
        Assert.Equal(0, arena.RecycledAllocations);
    }

    /// <summary>
    /// A handle from before the reset names nothing live: freeing it is the
    /// arena's double-free check, as it would be for any freed winding.
    /// </summary>
    [Fact]
    public void AHandleFromBeforeTheResetIsDead()
    {
        WindingArena arena = new();
        Winding before = arena.Alloc(8);

        arena.Reset();

        Assert.Throws<InvalidWindingException>(() => arena.Free(before));
    }

    /// <summary>
    /// The free lists go too: a reset arena's first reservation of a
    /// capacity that was waiting on a free list is fresh storage at offset
    /// zero, as a new arena's is, not the recycled slot.
    /// </summary>
    [Fact]
    public void AResetArenaRecyclesNothingFromBeforeIt()
    {
        WindingArena arena = new();
        arena.Alloc(4);
        arena.Free(arena.Alloc(8));

        arena.Reset();
        Winding again = arena.Alloc(8);

        Assert.Equal(0, arena.RecycledAllocations);
        Assert.Equal(new WindingArena().Alloc(8), again);
    }

    [Fact]
    public void ResettingAFreshArenaIsHarmless()
    {
        WindingArena arena = new(0);

        arena.Reset();
        Winding w = arena.Create([new Vec3(1f, 2f, 3f)]);

        Assert.Equal(new Vec3(1f, 2f, 3f), arena.Points(w)[0]);
    }

    // ---- growth around a reset -------------------------------------------------

    /// <summary>
    /// A reservation that ends exactly at the end of the grown first segment
    /// fits without growing it, and the next one grows it.
    /// </summary>
    [Fact]
    public void AReservationExactlyFillingTheFirstSegmentDoesNotGrowItAndOnePastDoes()
    {
        WindingArena arena = new(64);
        arena.Alloc(64);
        arena.Reset();

        arena.Alloc(32);
        arena.Alloc(32);
        Assert.Equal(64, arena.SlabCapacity);

        arena.Alloc(1);
        Assert.Equal(128, arena.SlabCapacity);
    }

    /// <summary>
    /// After a reset the arena accepts the largest winding there is, a whole
    /// segment, where a new arena would, and places it where a new arena
    /// would: at the start of the second segment when the first holds
    /// anything, since a winding never straddles two.
    /// </summary>
    [Fact]
    public void AWholeSegmentWindingAfterAResetLandsWhereANewArenasWould()
    {
        WindingArena reused = new();
        for (int i = 0; i < 2 * WindingArena.SegmentLength / 64; i++)
        {
            reused.Alloc(64);
        }

        reused.Reset();
        WindingArena fresh = new();

        Assert.Equal(fresh.Alloc(1), reused.Alloc(1));
        Winding huge = reused.Alloc(WindingArena.SegmentLength);
        Assert.Equal(fresh.Alloc(WindingArena.SegmentLength), huge);

        Span<Vec3> storage = reused.Storage(huge);
        storage[^1] = new Vec3(5f, 6f, 7f);
        Assert.Equal(new Vec3(5f, 6f, 7f), reused.Points(reused.SetCount(huge, WindingArena.SegmentLength))[^1]);
    }

    // ---- the pool -----------------------------------------------------------

    [Fact]
    public void APoolMakesAnArenaWhenItHasNoneAndCountsIt()
    {
        WindingArenaPool pool = new(ComplianceOptions.Correct);

        WindingArena arena = pool.Rent();

        Assert.Equal(1, pool.Created);
        Assert.Equal(1, pool.Rented);
        Assert.Equal(0, pool.Idle);
        Assert.Same(ComplianceOptions.Correct, arena.Compliance);
    }

    [Fact]
    public void AReturnedArenaIsTheNextOneRentedAndComesBackEmpty()
    {
        WindingArenaPool pool = new(ComplianceOptions.Correct);
        WindingArena first = pool.Rent();
        first.Alloc(8);
        first.Alloc(8);

        pool.Return(first);
        Assert.Equal(0, pool.Rented);
        Assert.Equal(1, pool.Idle);

        WindingArena second = pool.Rent();

        Assert.Same(first, second);
        Assert.Equal(1, pool.Created);
        Assert.Equal(0, second.ActiveWindings);
        Assert.Equal(new WindingArena().Alloc(8), second.Alloc(8));
    }

    [Fact]
    public void APoolMakesOnlyAsManyArenasAsWereEverOutAtOnce()
    {
        WindingArenaPool pool = new(ComplianceOptions.Correct);
        for (int round = 0; round < 10; round++)
        {
            WindingArena a = pool.Rent();
            WindingArena b = pool.Rent();
            WindingArena c = pool.Rent();
            pool.Return(b);
            pool.Return(a);
            pool.Return(c);
        }

        Assert.Equal(3, pool.Created);
        Assert.Equal(3, pool.Idle);
        Assert.Equal(0, pool.Rented);
    }

    [Fact]
    public void AnArenaOfAnotherComplianceIsRefused()
    {
        WindingArenaPool pool = new(ComplianceOptions.Correct);

        Assert.Throws<ArgumentException>(() => pool.Return(new WindingArena { Compliance = ComplianceOptions.Stock }));
        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
        Assert.Throws<ArgumentNullException>(() => new WindingArenaPool(null!));
    }

    [Fact]
    public void ReleaseDropsEveryIdleArenaAndIsIdempotent()
    {
        WindingArenaPool pool = new(ComplianceOptions.Stock);
        pool.Return(pool.Rent());
        pool.Return(pool.Rent());
        Assert.Equal(1, pool.Idle);

        pool.Release();
        pool.Release();

        Assert.True(pool.IsReleased);
        Assert.Equal(0, pool.Idle);
    }

    /// <summary>
    /// An owner still unwinding when the compile ends (a fork whose subtree
    /// failed) may return its arena after the release: it is dropped, not
    /// kept for a compile that is over, and a rent still works but keeps
    /// nothing either.
    /// </summary>
    [Fact]
    public void AfterReleaseAReturnedArenaIsDroppedAndARentMakesANewOne()
    {
        WindingArenaPool pool = new(ComplianceOptions.Correct);
        WindingArena late = pool.Rent();
        pool.Release();

        pool.Return(late);
        Assert.Equal(0, pool.Idle);
        Assert.Equal(0, pool.Rented);

        WindingArena after = pool.Rent();
        Assert.NotSame(late, after);
        Assert.Equal(2, pool.Created);
        pool.Return(after);
        Assert.Equal(0, pool.Idle);
    }

    [Fact]
    public async Task ManyThreadsRentingAndReturningLeaveThePoolConsistent()
    {
        WindingArenaPool pool = new(ComplianceOptions.Correct);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                WindingArena arena = pool.Rent();
                Winding w = arena.Alloc(4);
                Assert.Equal(1, arena.ActiveWindings);
                arena.Free(w);
                pool.Return(arena);
            }
        })));

        Assert.Equal(0, pool.Rented);
        Assert.Equal(pool.Created, pool.Idle);
        Assert.InRange(pool.Created, 1, 8);
    }

    // ---- helpers ------------------------------------------------------------

    // A fixed script of reservations and frees across several segments,
    // returning every handle it was given.
    private static List<Winding> Script(WindingArena arena)
    {
        List<Winding> handles = [];
        Winding? pending = null;
        for (int i = 0; i < (2 * WindingArena.SegmentLength / 60) + 50; i++)
        {
            Winding w = arena.Alloc(1 + (i % 68));
            handles.Add(w);
            if (i % 5 == 0)
            {
                if (pending is { } p)
                {
                    arena.Free(p);
                }

                pending = w;
            }
        }

        return handles;
    }
}
