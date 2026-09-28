//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen.Content;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;

using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// <see cref="ParallelWorldPass"/>: a world pass with its blocks built side by
/// side is the serial pass, node for node and plane for plane; it runs only
/// where that is guaranteed; and whatever happens it leaves every fork's
/// arena returned and the plane table writable.
/// </summary>
public sealed class ParallelWorldPassTests
{
    // ---- when it applies ------------------------------------------------------

    [Fact]
    public async Task ItAppliesToARootWithAParallelSchedulerAndMoreThanOneBlock()
    {
        (BspBuildContext build, _) = await Scattered();
        using CompilePool pool = new(2);
        build.TreeParallelism = Parallel(pool.Scheduler, degree: 2);

        Assert.True(ParallelWorldPass.Applies(build, 16));
        Assert.False(ParallelWorldPass.Applies(build, 1));
        Assert.False(ParallelWorldPass.Applies(build.Fork(), 16));

        build.TreeParallelism = Parallel(pool.Scheduler, degree: 1);
        Assert.False(ParallelWorldPass.Applies(build, 16));

        build.TreeParallelism = new BspTreeParallelism { MaxDegree = 8 };
        Assert.False(ParallelWorldPass.Applies(build, 16));

        build.TreeParallelism = null;
        Assert.False(ParallelWorldPass.Applies(build, 16));
    }

    /// <summary>
    /// The areaportal-water fixup writes map brushes that later blocks read,
    /// so a world where the pair can meet keeps the serial pass.
    /// </summary>
    [Fact]
    public async Task AnAreaportalThatMayMeetWaterKeepsThePassSerial()
    {
        (BspBuildContext build, MapFile map) = await Scattered();
        using CompilePool pool = new(2);
        build.TreeParallelism = Parallel(pool.Scheduler, degree: 2);

        map.Brushes[0].Contents |= (int)BrushContents.AreaPortal;
        map.Brushes[1].Contents |= (int)BrushContents.Water;
        Assert.False(ParallelWorldPass.AreaportalMayMeetWater(build), "the two boxes are far apart");

        MapBrush portal = map.Brushes[2];
        portal.Contents |= (int)BrushContents.AreaPortal;
        Assert.True(ParallelWorldPass.AreaportalMayMeetWater(build), "brush 2 overlaps the water brush 1");
        Assert.False(ParallelWorldPass.Applies(build, 16));
    }

    [Theory]
    [InlineData(0.5f, true)]
    [InlineData(0.75f, true)]
    [InlineData(1.0f, false)]
    [InlineData(64f, false)]
    public async Task APortalAndWaterMeetWithinAUnitOfTouching(float gap, bool meet)
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(CsgFixture.World(
            (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
            (UnitMap.Plain, (64 + gap, 0, 0), (128 + gap, 64, 64))));
        map.Brushes[0].Contents |= (int)BrushContents.AreaPortal;
        map.Brushes[1].Contents |= (int)BrushContents.Slime;

        Assert.Equal(meet, ParallelWorldPass.AreaportalMayMeetWater(build));
    }

    [Fact]
    public async Task OnlyTheBuildsBrushRangeIsLookedAt()
    {
        (BspBuildContext build, MapFile map) = await Scattered();
        map.Brushes[1].Contents |= (int)BrushContents.AreaPortal;
        map.Brushes[2].Contents |= (int)BrushContents.Water;
        Assert.True(ParallelWorldPass.AreaportalMayMeetWater(build));

        build.BrushStart = 2;
        Assert.False(ParallelWorldPass.AreaportalMayMeetWater(build));
    }

    // ---- the serial pass's tree -------------------------------------------------

    /// <summary>
    /// Boxes in far corners of the grid, so the first blocks are empty: the
    /// head volume's z planes are then appended after the first block with
    /// brushes, part way through the bounding planes, which the plane table
    /// in the dump shows in order.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task AScatteredWorldIsTheSerialPassAtEveryDegree(int degree)
    {
        string serial = await DumpAsync(Scattered, degree: null);

        for (int run = 0; run < 3; run++)
        {
            string parallel = await DumpAsync(Scattered, degree);
            BrushBspTreeParallelTests.AssertSame(serial, parallel, $"degree {degree}, run {run}");
        }
    }

    [Fact]
    public async Task EveryBlockWasBuiltInAForkAndEveryArenaCameBack()
    {
        (BspBuildContext build, MapFile map) = await Scattered();
        using CompilePool pool = new(4);
        build.TreeParallelism = Parallel(pool.Scheduler, degree: 4);
        BspBlockGrid grid = BlockGrid.Clamp(build.Options.Blocks, map.Mins, map.Maxs);

        BlockGrid.BuildWorldPass(build, grid, map.Mins, map.Maxs, out IReadOnlyList<BlockBuildStatistics> blocks);

        Assert.True(blocks.Count >= 9, $"only {blocks.Count} blocks");
        Assert.Equal(blocks.Count, build.ForkedBlocks);
        Assert.Equal(0, build.ForkArenas.Rented);
        Assert.False(map.Planes.IsFrozen);
    }

    // ---- failure ------------------------------------------------------------------

    [Fact]
    public async Task AFailedPassReturnsEveryArenaAndThawsThePlanes()
    {
        (BspBuildContext build, MapFile map) = await Scattered();
        build.TreeParallelism = Parallel(new RefusingScheduler(), degree: 4);
        BspBlockGrid grid = BlockGrid.Clamp(build.Options.Blocks, map.Mins, map.Maxs);

        Assert.Throws<TaskSchedulerException>(
            () => BlockGrid.BuildWorldPass(build, grid, map.Mins, map.Maxs, out _));

        Assert.True(build.ForkArenas.Created > 0, "no block had rented an arena before the failure");
        Assert.Equal(0, build.ForkArenas.Rented);
        Assert.False(map.Planes.IsFrozen);
        int planes = map.Planes.Count;
        map.Planes.Find(new Vec3(1, 0, 0), 12345);
        Assert.Equal(planes + 2, map.Planes.Count);
    }

    [Fact]
    public async Task ACancelledPassReturnsEveryArenaAndThawsThePlanes()
    {
        (BspBuildContext build, MapFile map) = await Scattered();
        using CancellationTokenSource cancel = new();
        build.TreeParallelism = Parallel(new CancelOnQueueScheduler(cancel), degree: 4) with
        {
            CancellationToken = cancel.Token,
        };
        BspBlockGrid grid = BlockGrid.Clamp(build.Options.Blocks, map.Mins, map.Maxs);

        Assert.ThrowsAny<OperationCanceledException>(
            () => BlockGrid.BuildWorldPass(build, grid, map.Mins, map.Maxs, out _));

        Assert.True(build.ForkArenas.Created > 0, "no block had rented an arena before the cancellation");
        Assert.Equal(0, build.ForkArenas.Rented);
        Assert.False(map.Planes.IsFrozen);
    }

    // ---- JoinBlock ------------------------------------------------------------------

    [Fact]
    public async Task JoiningABlockRebasesItsHeadAndReplacesTheLiveCounters()
    {
        (BspBuildContext build, _) = await Scattered();
        build.AllocNode();
        build.AllocNode();
        build.AllocBrush(1);
        build.Nodes = 40;
        build.NonVisibleNodes = 7;
        build.Diagnostics.Add(Diagnostic("before"));

        BspBuildContext fork = build.ForkForBlock();
        BspNode head = fork.AllocNode();
        head.PlaneNumber = BspNode.Leaf;
        fork.AllocBrush(1);
        fork.Nodes = 3;
        fork.NonVisibleNodes = 1;
        fork.Diagnostics.Add(Diagnostic("block"));

        build.JoinBlock(fork, head);

        Assert.Equal(2, head.Id);
        Assert.Equal(3, build.AllocatedNodes);
        Assert.Equal(2, build.AllocatedBrushes);
        Assert.Equal(3, build.Nodes);
        Assert.Equal(1, build.NonVisibleNodes);
        Assert.Equal(1, build.ForkedBlocks);
        Assert.Equal(["before", "block"], build.Diagnostics.Select(d => d.Message).ToArray());
    }

    [Fact]
    public async Task JoiningAnEmptyBlockLeavesTheLiveCountersAndCountsItsBrushes()
    {
        (BspBuildContext build, _) = await Scattered();
        build.Nodes = 40;
        build.NonVisibleNodes = 7;

        BspBuildContext fork = build.ForkForBlock();
        fork.AllocBrush(1);
        fork.AllocBrush(1);

        build.JoinBlock(fork, head: null);

        Assert.Equal(40, build.Nodes);
        Assert.Equal(7, build.NonVisibleNodes);
        Assert.Equal(2, build.AllocatedBrushes);
        Assert.Equal(0, build.AllocatedNodes);
        Assert.Throws<ArgumentException>(() => build.JoinBlock(build, null));
        Assert.Throws<ArgumentNullException>(() => build.JoinBlock(null!, null));
    }

    [Fact]
    public async Task ABlocksForkRentsItsArenaOnlyWhenItFirstAllocates()
    {
        (BspBuildContext build, _) = await Scattered();

        BspBuildContext idle = build.ForkForBlock();
        BspBuildContext used = build.ForkForBlock();
        Assert.Equal(0, build.ForkArenas.Rented);

        used.Windings.Alloc(4);
        Assert.Equal(1, build.ForkArenas.Rented);
        Assert.Same(used.Windings, used.Windings);

        idle.ReleaseForkWindings();
        used.ReleaseForkWindings();
        Assert.Equal(0, build.ForkArenas.Rented);
        Assert.Equal(1, build.ForkArenas.Created);
        Assert.Throws<InvalidOperationException>(() => idle.Windings);
    }

    // ---- the planes a block adds -----------------------------------------------------

    [Fact]
    public async Task FindBoundsPlanesFindsBrushFromBoundsPlanesInItsOrder()
    {
        (_, MapFile firstMap) = await Scattered();
        (BspBuildContext second, MapFile secondMap) = await Scattered();
        Vec3 mins = new(-3072, 1024, -16384);
        Vec3 maxs = new(-2048, 2048, 16384);
        int before = firstMap.Planes.Count;

        Span<int> planes = stackalloc int[6];
        BrushGeometry.FindBoundsPlanes(firstMap.Planes, mins, maxs, planes);
        BspBrush volume = BrushGeometry.BrushFromBounds(second, mins, maxs);

        Assert.Equal(secondMap.Planes.Count, firstMap.Planes.Count);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(volume.Sides[i].PlaneNumber, planes[i]);
        }

        for (int i = before; i < firstMap.Planes.Count; i++)
        {
            Assert.Equal(secondMap.Planes[i], firstMap.Planes[i]);
        }

    }

    // ---- helpers ------------------------------------------------------------------------

    private static BspTreeParallelism Parallel(TaskScheduler scheduler, int degree) => new()
    {
        Scheduler = scheduler,
        MaxForkDepth = 3,
        MinBrushes = 1,
        MaxDegree = degree,
    };

    private static CompileDiagnostic Diagnostic(string message) =>
        new("TEST0001", DiagnosticSeverity.Warning, message);

    // Boxes scattered over a 4x4 block grid, none in its first block.
    private static Task<(BspBuildContext Build, MapFile Map)> Scattered() =>
        CsgFixture.LoadAsync(CsgFixture.World(
            (UnitMap.Plain, (-1900, 1500, -64), (-1600, 1800, 64)),
            (UnitMap.Plain, (1500, -1900, -64), (1800, -1600, 64)),
            (UnitMap.Plain, (1600, -1800, 32), (1700, -1500, 256)),
            (UnitMap.Plain, (-200, -200, -32), (200, 200, 32)),
            (UnitMap.Plain, (-100, -300, 0), (100, 300, 128)),
            (UnitMap.Plain, (900, 900, -64), (1200, 1200, 64)),
            (UnitMap.Plain, (1000, 1000, 0), (1100, 1100, 256))));

    private static async Task<string> DumpAsync(Func<Task<(BspBuildContext, MapFile)>> load, int? degree)
    {
        (BspBuildContext build, MapFile map) = await load();
        using CompilePool? pool = degree is int d ? new CompilePool(d) : null;
        if (pool is not null)
        {
            build.TreeParallelism = Parallel(pool.Scheduler, Math.Max(2, pool.Degree));
        }

        BspBlockGrid grid = BlockGrid.Clamp(build.Options.Blocks, map.Mins, map.Maxs);
        BspTree tree = BlockGrid.BuildWorldPass(
            build, grid, map.Mins, map.Maxs, out IReadOnlyList<BlockBuildStatistics> blocks);

        Assert.Equal(pool is null ? 0 : blocks.Count, build.ForkedBlocks);
        return BrushBspTreeParallelTests.Dump(build, map, tree, blocks);
    }

    /// <summary>Cancels the pass the moment a helper is queued, then runs it.</summary>
    private sealed class CancelOnQueueScheduler(CancellationTokenSource cancel) : TaskScheduler
    {
        protected override void QueueTask(Task task)
        {
            cancel.Cancel();
            ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }

    /// <summary>Refuses every helper, as a pool disposed under the build does.</summary>
    private sealed class RefusingScheduler : TaskScheduler
    {
        protected override void QueueTask(Task task) =>
            throw new InvalidOperationException("the pool is gone");

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
