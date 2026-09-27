//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// <see cref="BrushSidePool"/> and the brush lifetime it depends on: what
/// <see cref="BspBuildContext.FreeBrush"/> gives back, what
/// <see cref="BspBuildContext.AllocBrush"/> takes, and what a freed brush may
/// no longer do.
/// </summary>
public sealed class BrushSidePoolTests
{
    private static readonly Vec3 CubeMins = new(-64f, -64f, -64f);
    private static readonly Vec3 CubeMaxs = new(64f, 64f, 64f);

    private static async Task<BspBuildContext> BuildAsync(BrushSidePooling pooling)
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        build.Compile.BrushSidePooling = pooling;

        // The context read the switch when it was made; make one that sees it.
        return new BspBuildContext(build.Compile, build.Map);
    }

    private static BspBrushSide Side(int plane) => new()
    {
        PlaneNumber = plane,
        TexInfo = 7,
        Contents = 1,
        Surface = 2,
        Visible = true,
        Tested = true,
        Bevel = true,
    };

    // ---- the pool on its own -------------------------------------------------

    [Fact]
    public void ARentedArrayHasExactlyTheLengthAskedFor()
    {
        BrushSidePool pool = new(checkReturns: false);

        Assert.Equal(7, pool.Rent(7).Length);
        Assert.Empty(pool.Rent(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1));
    }

    [Fact]
    public void AReturnedArrayIsClearedAndHandedToTheNextRentOfItsLength()
    {
        BrushSidePool pool = new(checkReturns: false);
        BspBrushSide[] array = pool.Rent(5);
        array[0] = Side(3);
        array[4] = Side(9);

        pool.Return(array);

        Assert.Equal(1, pool.PooledArrays);
        Assert.All(array, s => Assert.Equal(default, s));

        // Another length does not take it; the same length does.
        Assert.NotSame(array, pool.Rent(6));
        Assert.Same(array, pool.Rent(5));
        Assert.Equal(0, pool.PooledArrays);
        Assert.Equal(1, pool.Reused);
        Assert.Equal(1, pool.Returned);
    }

    [Fact]
    public void EmptyAndOverlongArraysAreNotKept()
    {
        BrushSidePool pool = new(checkReturns: false);

        pool.Return([]);
        pool.Return(new BspBrushSide[BrushSidePool.MaxPooledLength + 1]);

        Assert.Equal(0, pool.PooledArrays);
        Assert.Equal(0, pool.Returned);

        BspBrushSide[] longest = new BspBrushSide[BrushSidePool.MaxPooledLength];
        pool.Return(longest);
        Assert.Same(longest, pool.Rent(BrushSidePool.MaxPooledLength));
    }

    [Fact]
    public void ABucketStopsGrowingAtItsCap()
    {
        BrushSidePool pool = new(checkReturns: true);

        for (int i = 0; i < BrushSidePool.MaxPerLength; i++)
        {
            pool.Return(new BspBrushSide[3]);
        }

        BspBrushSide[] extra = new BspBrushSide[3];
        pool.Return(extra);

        Assert.Equal(BrushSidePool.MaxPerLength, pool.PooledArrays);

        // Dropped, not kept: in checked mode it can come back without tripping
        // the double-return check.
        pool.Return(extra);
        Assert.Equal(BrushSidePool.MaxPerLength, pool.PooledArrays);
    }

    [Fact]
    public void CheckedModeRefusesTheSameArrayTwice()
    {
        BrushSidePool pool = new(checkReturns: true);
        BspBrushSide[] array = new BspBrushSide[4];

        pool.Return(array);

        Assert.Throws<InvalidOperationException>(() => pool.Return(array));
        Assert.Equal(1, pool.PooledArrays);

        // Once rented out again it may come back again.
        Assert.Same(array, pool.Rent(4));
        pool.Return(array);
        Assert.Equal(1, pool.PooledArrays);
    }

    [Fact]
    public void UncheckedModeDoesNotTrackReturns()
    {
        BrushSidePool pool = new(checkReturns: false);
        BspBrushSide[] array = new BspBrushSide[4];

        pool.Return(array);
        pool.Return(array);

        Assert.Equal(2, pool.PooledArrays);
    }

    [Fact]
    public void ClearDropsEverythingAndLeavesThePoolUsable()
    {
        BrushSidePool pool = new(checkReturns: true);
        BspBrushSide[] array = new BspBrushSide[4];
        pool.Return(array);
        pool.Return(new BspBrushSide[9]);

        pool.Clear();

        Assert.Equal(0, pool.PooledArrays);
        Assert.NotSame(array, pool.Rent(4));

        // The checked set was cleared with the buckets.
        pool.Return(array);
        Assert.Equal(1, pool.PooledArrays);
    }

    [Fact]
    public void ReturnRejectsNull()
    {
        BrushSidePool pool = new(checkReturns: false);

        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
    }

    // ---- through the build context -------------------------------------------

    [Theory]
    [InlineData((int)BrushSidePooling.Pooled)]
    [InlineData((int)BrushSidePooling.Checked)]
    public async Task AFreedBrushsArrayIsTheNextBrushsArrayAndComesBackClean(int pooling)
    {
        BspBuildContext build = await BuildAsync((BrushSidePooling)pooling);
        BspBrush cube = CsgFixture.Box(build, CubeMins, CubeMaxs);
        BspBrushSide[] storage = cube.SideStorage;

        build.FreeBrush(cube);

        Assert.Equal(1, build.SidePool!.PooledArrays);
        Assert.All(storage, s => Assert.Equal(default, s));

        BspBrush next = build.AllocBrush(6);
        Assert.Same(storage, next.SideStorage);
        Assert.Equal(0, next.SideCount);
        Assert.Equal(6, next.SideCapacity);

        // The over-append check still sees the requested capacity.
        for (int i = 0; i < 6; i++)
        {
            next.AddSide(Side(i));
        }

        Assert.Throws<InvalidOperationException>(() => next.AddSide(Side(6)));
    }

    [Fact]
    public async Task WithPoolingOffEveryBrushGetsAFreshArray()
    {
        BspBuildContext build = await BuildAsync(BrushSidePooling.Off);
        BspBrush cube = CsgFixture.Box(build, CubeMins, CubeMaxs);
        BspBrushSide[] storage = cube.SideStorage;

        build.FreeBrush(cube);

        Assert.Null(build.SidePool);
        Assert.NotSame(storage, build.AllocBrush(6).SideStorage);

        // Releasing a pool that is not there is a no-op.
        build.ReleaseBrushSidePool();
    }

    [Theory]
    [InlineData((int)BrushSidePooling.Off)]
    [InlineData((int)BrushSidePooling.Pooled)]
    public async Task AFreedBrushHasNoSidesToRead(int pooling)
    {
        BspBuildContext build = await BuildAsync((BrushSidePooling)pooling);
        BspBrush cube = CsgFixture.Box(build, CubeMins, CubeMaxs);
        int windings = build.Windings.ActiveWindings;

        build.FreeBrush(cube);

        // The windings went back to the arena, as stock's FreeBrush does.
        Assert.Equal(windings - 6, build.Windings.ActiveWindings);
        Assert.True(cube.IsFreed);
        Assert.Equal(0, cube.SideCapacity);
        Assert.Throws<InvalidOperationException>(() => cube.SideCount);
        Assert.Throws<InvalidOperationException>(() => cube.Sides.Length);
        Assert.Throws<InvalidOperationException>(() => cube.AddSide(Side(0)));
        Assert.Throws<InvalidOperationException>(() => cube.SetSideCount(0));
        Assert.Throws<InvalidOperationException>(() => cube.CopySidesFrom(build.Map, build.Map.Brushes[0]));

        // The header survives: FreeBrushList and CullList read next around it.
        Assert.Equal(CubeMins, cube.Mins);
    }

    [Fact]
    public async Task FreeingABrushTwiceThrowsAndReturnsItsArrayOnce()
    {
        BspBuildContext build = await BuildAsync(BrushSidePooling.Checked);
        BspBrush cube = CsgFixture.Box(build, CubeMins, CubeMaxs);
        int active = build.ActiveBrushes;

        build.FreeBrush(cube);

        Assert.Throws<InvalidOperationException>(() => build.FreeBrush(cube));
        Assert.Equal(1, build.SidePool!.PooledArrays);
        Assert.Equal(1, build.SidePool.Returned);
        Assert.Equal(active - 1, build.ActiveBrushes);
    }

    [Fact]
    public async Task ABrushWithNoSidesFreesWithoutTouchingThePool()
    {
        BspBuildContext build = await BuildAsync(BrushSidePooling.Checked);
        BspBrush empty = build.AllocBrush(0);

        build.FreeBrush(empty);

        Assert.True(empty.IsFreed);
        Assert.Equal(0, build.SidePool!.PooledArrays);
        Assert.Throws<ArgumentOutOfRangeException>(() => build.AllocBrush(-1));
    }

    [Fact]
    public async Task ABrushMadeOutsideThePoolIsAcceptedBackIntoIt()
    {
        BspBuildContext build = await BuildAsync(BrushSidePooling.Checked);
        BspBrush outsider = new(4);
        outsider.AddSide(Side(1));

        build.FreeBrush(outsider);

        Assert.Equal(1, build.SidePool!.PooledArrays);
        Assert.Equal(0, build.AllocBrush(4).SideStorage[0].PlaneNumber);
    }

    [Fact]
    public async Task ReleaseEmptiesThePoolAndTheBuildCarriesOn()
    {
        BspBuildContext build = await BuildAsync(BrushSidePooling.Checked);
        BspBrush cube = CsgFixture.Box(build, CubeMins, CubeMaxs);
        BspBrushSide[] storage = cube.SideStorage;
        build.FreeBrush(cube);

        build.ReleaseBrushSidePool();

        Assert.Equal(0, build.SidePool!.PooledArrays);
        Assert.NotSame(storage, CsgFixture.Box(build, CubeMins, CubeMaxs).SideStorage);
    }

    [Fact]
    public async Task TwoBuildContextsNeverShareAPool()
    {
        BspBuildContext first = await BuildAsync(BrushSidePooling.Pooled);
        BspBuildContext second = await BuildAsync(BrushSidePooling.Pooled);

        first.FreeBrush(CsgFixture.Box(first, CubeMins, CubeMaxs));

        Assert.NotSame(first.SidePool, second.SidePool);
        Assert.Equal(1, first.SidePool!.PooledArrays);
        Assert.Equal(0, second.SidePool!.PooledArrays);
    }

    // ---- what it saves -------------------------------------------------------

    [Fact]
    public async Task SplittingAndFreeingInALoopAllocatesNoSideArrays()
    {
        // Every SplitBrush of a cube through its middle makes two seven-sided
        // brushes, and CheckPlaneAgainstVolume-style callers free both at
        // once. Before the pool each cycle allocated both arrays; with it the
        // steady state recycles them, so the whole loop allocates less than
        // ONE seven-side array per cycle (the two brush objects and SplitBrush's
        // pair array are all that is left).
        const int Cycles = 2000;

        BspBuildContext pooled = await BuildAsync(BrushSidePooling.Pooled);
        BspBuildContext unpooled = await BuildAsync(BrushSidePooling.Off);

        long perArray = 24 + (7L * Unsafe.SizeOf<BspBrushSide>());
        long budget = Cycles * perArray;

        long withPool = SplitAndFree(pooled, Cycles);
        long withoutPool = SplitAndFree(unpooled, Cycles);

        Assert.True(withPool < budget, $"pooled: {withPool} bytes for {Cycles} cycles, budget {budget}");
        Assert.True(
            withoutPool >= withPool + (2 * budget * 9 / 10),
            $"unpooled {withoutPool} vs pooled {withPool}: the fact is not measuring the arrays");
        Assert.True(pooled.SidePool!.Reused >= 2 * Cycles);
    }

    private static long SplitAndFree(BspBuildContext build, int cycles)
    {
        BspBrush cube = CsgFixture.Box(build, CubeMins, CubeMaxs);
        int plane = build.Planes.Find(new Vec3(1f, 0f, 0f), 0f);

        void Cycle()
        {
            BrushGeometry.SplitBrush(build, cube, plane, out BspBrush? front, out BspBrush? back);
            build.FreeBrush(front!);
            build.FreeBrush(back!);
        }

        // Warm the arena's free lists and the pool.
        for (int i = 0; i < 64; i++)
        {
            Cycle();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < cycles; i++)
        {
            Cycle();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
