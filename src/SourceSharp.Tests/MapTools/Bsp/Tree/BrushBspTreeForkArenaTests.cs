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
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Parallel;

using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// The forks of the parallel tree build take their arenas from the compile's
/// <see cref="WindingArenaPool"/> and give them back however the subtree
/// ended: built, failed or cancelled.
/// </summary>
public sealed class BrushBspTreeForkArenaTests
{
    // ---- a fork and its arena -----------------------------------------------

    [Fact]
    public async Task AForkRentsItsArenaFromTheRootsPool()
    {
        (BspBuildContext build, _) = await SixBoxes();

        BspBuildContext fork = build.Fork();
        BspBuildContext grandchild = fork.Fork();

        Assert.Same(build.ForkArenas, fork.ForkArenas);
        Assert.Same(build.ForkArenas, grandchild.ForkArenas);
        Assert.Equal(2, build.ForkArenas.Rented);
        Assert.NotSame(fork.Windings, grandchild.Windings);
    }

    [Fact]
    public async Task AReleasedForkHasNoArenaAndReleasingAgainDoesNothing()
    {
        (BspBuildContext build, _) = await SixBoxes();
        BspBuildContext fork = build.Fork();
        WindingArena arena = fork.Windings;

        fork.ReleaseForkWindings();
        fork.ReleaseForkWindings();

        Assert.Throws<InvalidOperationException>(() => fork.Windings);
        Assert.Equal(0, build.ForkArenas.Rented);
        Assert.Equal(1, build.ForkArenas.Idle);
        Assert.Same(arena, build.Fork().Windings);
    }

    [Fact]
    public async Task ReleasingTheRootsForkWindingsLeavesTheCompilesArenaAlone()
    {
        (BspBuildContext build, _) = await SixBoxes();
        WindingArena compile = build.Windings;

        build.ReleaseForkWindings();

        Assert.Same(compile, build.Windings);
        Assert.Same(build.Compile.Windings, build.Windings);
    }

    [Fact]
    public async Task ReleasingThePoolDropsTheArenasForksGaveBack()
    {
        (BspBuildContext build, _) = await SixBoxes();
        build.Fork().ReleaseForkWindings();
        Assert.Equal(1, build.ForkArenas.Idle);

        build.ReleaseWindingArenaPool();

        Assert.True(build.ForkArenas.IsReleased);
        Assert.Equal(0, build.ForkArenas.Idle);
    }

    // ---- the build gives every arena back -------------------------------------

    [Fact]
    public async Task AFinishedBuildHasGivenEveryForksArenaBack()
    {
        (BspBuildContext build, _) = await SixBoxes();
        using CompilePool pool = new(2);
        build.TreeParallelism = Forking(pool.Scheduler, CancellationToken.None);

        BrushBspTree.BrushBsp(build, CsgFixture.AllBrushes(build), Mins, Maxs);

        Assert.True(build.ForkedSubtrees > 1, $"only {build.ForkedSubtrees} forks");
        Assert.Equal(0, build.ForkArenas.Rented);
        Assert.Equal(build.ForkArenas.Created, build.ForkArenas.Idle);
    }

    /// <summary>
    /// Arenas are reused, not made per fork: with every helper run at once on
    /// the queueing thread, at most one chain of nested forks is alive at a
    /// time, so the pool never makes more arenas than the fork depth, however
    /// many forks there are.
    /// </summary>
    [Fact]
    public async Task ForksReuseArenasRatherThanMakingOneEach()
    {
        (BspBuildContext build, _) = await SixBoxes();
        build.TreeParallelism = Forking(new InlineScheduler(), CancellationToken.None);

        BrushBspTree.BrushBsp(build, CsgFixture.AllBrushes(build), Mins, Maxs);

        Assert.True(build.ForkedSubtrees > 4, $"only {build.ForkedSubtrees} forks");
        Assert.InRange(build.ForkArenas.Created, 1, 4);
        Assert.Equal(0, build.ForkArenas.Rented);
    }

    /// <summary>
    /// A build cancelled once a fork's helper has been queued: the halves
    /// throw at their next node, and the fork's arena still goes back.
    /// </summary>
    [Fact]
    public async Task ACancelledBuildGivesTheForksArenaBack()
    {
        (BspBuildContext build, _) = await SixBoxes();
        using CancellationTokenSource cancel = new();
        build.TreeParallelism = Forking(new CancelOnQueueScheduler(cancel), cancel.Token);

        Assert.ThrowsAny<OperationCanceledException>(
            () => BrushBspTree.BrushBsp(build, CsgFixture.AllBrushes(build), Mins, Maxs));

        Assert.True(build.ForkArenas.Created > 0, "nothing forked before the cancellation");
        Assert.Equal(0, build.ForkArenas.Rented);
    }

    /// <summary>
    /// A build that fails while forking (here the scheduler refuses the
    /// helper, as a disposed pool does) gives the fork's arena back too.
    /// </summary>
    [Fact]
    public async Task AFailedBuildGivesTheForksArenaBack()
    {
        (BspBuildContext build, _) = await SixBoxes();
        build.TreeParallelism = Forking(new RefusingScheduler(), CancellationToken.None);

        TaskSchedulerException refused = Assert.Throws<TaskSchedulerException>(
            () => BrushBspTree.BrushBsp(build, CsgFixture.AllBrushes(build), Mins, Maxs));
        Assert.IsType<InvalidOperationException>(refused.InnerException);

        Assert.True(build.ForkArenas.Created > 0, "nothing forked before the failure");
        Assert.Equal(0, build.ForkArenas.Rented);
    }

    // ---- helpers ------------------------------------------------------------

    private static readonly Vec3 Mins = new(-512, -512, -512);

    private static readonly Vec3 Maxs = new(512, 512, 512);

    private static BspTreeParallelism Forking(TaskScheduler scheduler, CancellationToken token) => new()
    {
        Scheduler = scheduler,
        MaxForkDepth = 4,
        MinBrushes = 1,
        CancellationToken = token,
    };

    private static Task<(BspBuildContext Build, MapFile Map)> SixBoxes() =>
        CsgFixture.LoadAsync(CsgFixture.World(
            (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
            (UnitMap.Plain, (128, 0, 0), (192, 64, 64)),
            (UnitMap.Plain, (256, 0, 0), (320, 64, 64)),
            (UnitMap.Plain, (0, 128, 0), (64, 192, 64)),
            (UnitMap.Plain, (128, 128, 0), (192, 192, 64)),
            (UnitMap.Plain, (256, 128, 0), (320, 192, 64))));

    /// <summary>Cancels the build the moment a helper is queued, then runs it.</summary>
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

    /// <summary>Runs every helper at once, on the thread that queued it.</summary>
    private sealed class InlineScheduler : TaskScheduler
    {
        protected override void QueueTask(Task task) => TryExecuteTask(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

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
