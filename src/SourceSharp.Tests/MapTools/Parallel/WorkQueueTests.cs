//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Parallel;

using Xunit;

namespace SourceSharp.Tests.MapTools.Parallel;

/// <summary>
/// The replacement for: every index claimed
/// exactly once, a merge order that does not depend on completion order, no
/// leaked threads, and a compile that can actually be stopped.
/// </summary>
public class WorkQueueTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // ---- Claiming -----------------------------------------------------------

    [Fact]
    public async Task EveryIndexIsClaimedExactlyOnceUnderContention()
    {
        // Far more items than workers, and four times as many workers as the
        // machine has processors, so the claim path is genuinely contended
        // rather than run one worker at a time.
        const int items = 200_000;
        int[] claims = new int[items];

        using var queue = new WorkQueue(
            new CompileParallelism { MaxDegree = Environment.ProcessorCount * 4 });

        await queue.RunAsync(
            items,
            (index, _) => Interlocked.Increment(ref claims[index]),
            null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(items, claims.Count(c => c == 1));
    }

    [Fact]
    public async Task NoIndexIsClaimedTwiceUnderContention()
    {
        const int items = 200_000;
        int[] claims = new int[items];

        using var queue = new WorkQueue(
            new CompileParallelism { MaxDegree = Environment.ProcessorCount * 4 });

        await queue.RunAsync(
            items,
            (index, _) => Interlocked.Increment(ref claims[index]),
            null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.DoesNotContain(claims, c => c > 1);
    }

    [Fact]
    public async Task EveryIndexIsClaimedExactlyOnceWhenCostSortingReordersThem()
    {
        // The longest-job-first order is a permutation, so this is the fact
        // that says the reordering did not drop or duplicate an index.
        const int items = 50_000;
        int[] claims = new int[items];

        using var queue = new WorkQueue(
            new CompileParallelism { MaxDegree = Environment.ProcessorCount * 4 });

        await queue.RunAsync(
            items,
            (index, _) => Interlocked.Increment(ref claims[index]),
            new WorkQueueOptions { ItemCost = i => i % 977 },
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(items, claims.Count(c => c == 1));
    }

    [Fact]
    public async Task EveryIndexIsClaimedExactlyOnceWithAChunkSizeThatDoesNotDivideTheCount()
    {
        // 7 into 1000 leaves a short final chunk. A claim that clamped the
        // wrong end would either run index 999 twice or not at all.
        const int items = 1000;
        int[] claims = new int[items];

        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 8 });

        await queue.RunAsync(
            items,
            (index, _) => Interlocked.Increment(ref claims[index]),
            new WorkQueueOptions { ChunkSize = 7 },
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(items, claims.Count(c => c == 1));
    }

    // ---- Ordering -----------------------------------------------------------

    [Fact]
    public async Task ResultsAreMergedInItemOrderEvenWhenItemsFinishBackwards()
    {
        // Deterministic, not statistical: item 0 cannot finish until item 1
        // has, so the completion order is exactly the reverse of the item
        // order. The results must still come back 0, 1.
        using var oneIsDone = new ManualResetEventSlim(false);
        var completed = new ConcurrentQueue<int>();

        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 2 });

        int[] results = await queue.RunAsync<object?, int>(
            2,
            (index, _, _) =>
            {
                if (index == 0)
                {
                    oneIsDone.Wait(Patience);
                    completed.Enqueue(index);
                }
                else
                {
                    // Enqueued BEFORE the event is set, or item 0 could wake and
                    // record itself first and the fact would be a coin flip.
                    completed.Enqueue(index);
                    oneIsDone.Set();
                }

                return index * 10;
            },
            _ => null,
            null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(new[] { 1, 0 }, completed.ToArray());
        Assert.Equal(new[] { 0, 10 }, results);
    }

    [Fact]
    public async Task DegreeOneAndDegreeNAgreeExactly()
    {
        // Both halves in ONE fact, so there is no reference output to keep in
        // step and no way for the two to drift apart in separate runs. This is
        // a race detector: a stage whose output depended on the schedule would
        // differ here without anything else having to be true.
        const int items = 4096;

        static int Mix(int index) => (int)((index * 2654435761L) % 1000L);

        int[] expected;
        using (var serial = new WorkQueue(CompileParallelism.Serial))
        {
            expected = await serial.RunAsync<object?, int>(
                items,
                (index, _, _) => Mix(index),
                _ => null,
                null,
                CancellationToken.None).WaitAsync(Patience, CancellationToken.None);
        }

        int[] parallel;
        using (var wide = new WorkQueue(
            new CompileParallelism { MaxDegree = Environment.ProcessorCount * 4 }))
        {
            parallel = await wide.RunAsync<object?, int>(
                items,
                (index, _, _) => Mix(index),
                _ => null,
                null,
                CancellationToken.None).WaitAsync(Patience, CancellationToken.None);
        }

        Assert.Equal(expected, parallel);
        Assert.Equal(Mix(4095), parallel[4095]);
    }

    [Fact]
    public async Task TheMostExpensiveItemIsClaimedFirst()
    {
        // Degree 1, so the claim order IS the execution order and there is
        // nothing statistical about it. Stock hands items out in index order
        //, which leaves the expensive ones as a serial tail.
        var order = new List<int>();

        using var queue = new WorkQueue(CompileParallelism.Serial);
        await queue.RunAsync(
            10,
            (index, _) => order.Add(index),
            new WorkQueueOptions { ItemCost = i => i },
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(new[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 }, order);
    }

    [Fact]
    public async Task EqualCostsKeepAscendingIndexOrder()
    {
        // A stable sort, so the claim order is a pure function of the costs
        // and does not vary between runs of the same map.
        var order = new List<int>();

        using var queue = new WorkQueue(CompileParallelism.Serial);
        await queue.RunAsync(
            5,
            (index, _) => order.Add(index),
            new WorkQueueOptions { ItemCost = _ => 1 },
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order);
    }

    // ---- The async shape ----------------------------------------------------

    [Fact]
    public async Task TheTaskIsNotAlreadyCompleteWhenItIsReturned()
    {
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 1 });

        Task run = queue.RunAsync(
            1,
            (_, _) =>
            {
                started.Set();
                release.Wait(Patience);
            },
            null,
            CancellationToken.None);

        started.Wait(Patience);
        Assert.False(run.IsCompleted);

        release.Set();
        await run.WaitAsync(Patience, CancellationToken.None);
    }

    [Fact]
    public async Task WorkNeverRunsOnTheCallersThread()
    {
        int caller = Environment.CurrentManagedThreadId;
        var seen = new ConcurrentBag<int>();

        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 1 });
        await queue.RunAsync(
            64,
            (_, _) => seen.Add(Environment.CurrentManagedThreadId),
            null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.DoesNotContain(caller, seen);
    }

    [Fact]
    public async Task ZeroItemsCompletesWithNothingRun()
    {
        bool ran = false;
        using var queue = new WorkQueue();

        await queue.RunAsync(0, (_, _) => ran = true, null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.False(ran);
    }

    [Fact]
    public async Task ASecondOverlappingRunIsRefused()
    {
        // Two stages sharing per-worker scratch would corrupt it.
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 1 });

        Task first = queue.RunAsync(
            1,
            (_, _) =>
            {
                started.Set();
                release.Wait(Patience);
            },
            null,
            CancellationToken.None);

        started.Wait(Patience);
        Assert.Throws<InvalidOperationException>(() =>
        {
            // Discarded rather than awaited: the refusal is synchronous, which
            // is the point -- a second stage must not be queued behind the
            // first and then silently run against the same scratch.
            _ = queue.RunAsync(1, (_, _) => { }, null, CancellationToken.None);
        });

        release.Set();
        await first.WaitAsync(Patience, CancellationToken.None);
    }

    [Fact]
    public async Task AnExceptionFromAnItemFaultsTheRun()
    {
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 4 });

        Task run = queue.RunAsync(
            1000,
            (index, _) =>
            {
                if (index == 500)
                {
                    throw new InvalidTimeZoneException("from an item");
                }
            },
            null,
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidTimeZoneException>(
            () => run.WaitAsync(Patience, CancellationToken.None));
        Assert.Equal("from an item", error.Message);
    }

    [Fact]
    public async Task ARunIsPossibleAfterAFaultedOne()
    {
        // The busy flag has to be released on the failure path too, or one bad
        // brush would wedge the queue for the rest of the compile.
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 2 });

        await Assert.ThrowsAsync<InvalidTimeZoneException>(() =>
            queue.RunAsync(4, (_, _) => throw new InvalidTimeZoneException(), null,
                CancellationToken.None).WaitAsync(Patience, CancellationToken.None));

        int[] results = await queue.RunAsync<object?, int>(
            4, (index, _, _) => index, _ => null, null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2, 3 }, results);
    }

    // ---- Cancellation -------------------------------------------------------

    [Fact]
    public async Task APreCancelledTokenRunsNothingAtAll()
    {
        bool ran = false;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var queue = new WorkQueue();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.RunAsync(1000, (_, _) => ran = true, null, cts.Token));

        Assert.False(ran);
    }

    [Fact]
    public async Task APreCancelledTokenLeavesTheQueueUsable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 2 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.RunAsync(4, (_, _) => { }, null, cts.Token));

        int[] results = await queue.RunAsync<object?, int>(
            3, (index, _, _) => index, _ => null, null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2 }, results);
    }

    [Fact]
    public async Task CancellingMidRunStopsTheRun()
    {
        using var cts = new CancellationTokenSource();
        using var started = new ManualResetEventSlim(false);
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 2 });

        Task run = queue.RunAsync(
            1_000_000,
            (_, _) => started.Set(),
            null,
            cts.Token);

        started.Wait(Patience);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(Patience, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationReachesTheInsideOfOneLongItem()
    {
        // The reason WorkerContext.ShouldStop exists: one vvis portal's
        // RecursiveLeafFlow can run for minutes, and a compile that only
        // noticed cancellation at an item boundary would look wedged.
        using var cts = new CancellationTokenSource();
        using var inside = new ManualResetEventSlim(false);
        bool noticed = false;

        using var queue = new WorkQueue(
            new CompileParallelism { MaxDegree = 1, CancellationPollInterval = 1 });

        Task run = queue.RunAsync(
            1,
            (_, context) =>
            {
                inside.Set();
                while (!context.ShouldStop())
                {
                    Thread.SpinWait(64);
                }

                noticed = true;
                context.ThrowIfShouldStop();
            },
            null,
            cts.Token);

        inside.Wait(Patience);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(Patience, CancellationToken.None));
        Assert.True(noticed);
    }

    [Fact]
    public async Task ShouldStopIsFalseWhileTheRunIsHealthy()
    {
        bool stopped = true;
        using var queue = new WorkQueue(
            new CompileParallelism { MaxDegree = 1, CancellationPollInterval = 1 });

        await queue.RunAsync(
            1,
            (_, context) => stopped = context.ShouldStop(),
            null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.False(stopped);
    }

    // ---- Threads ------------------------------------------------------------

    [Fact]
    public void NoThreadsExistBeforeTheFirstRun() =>
        Assert.Equal(0, new WorkQueue(new CompileParallelism { MaxDegree = 4 }).LiveWorkerCount);

    [Fact]
    public async Task TheQueueOwnsItsThreadsWhileItIsAlive()
    {
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 4 });
        await queue.RunAsync(16, (_, _) => { }, null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(4, queue.LiveWorkerCount);
    }

    [Fact]
    public async Task NoThreadIsLeftRunningAfterDisposal()
    {
        // Measured over the threads this queue actually created, not over the
        // process's thread count, which the GC and tiered compilation move
        // around under a test.
        var queue = new WorkQueue(new CompileParallelism { MaxDegree = 6 });
        await queue.RunAsync(64, (_, _) => { }, null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(6, queue.LiveWorkerCount);
        queue.Dispose();
        Assert.Equal(0, queue.LiveWorkerCount);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var queue = new WorkQueue(new CompileParallelism { MaxDegree = 2 });
        queue.Dispose();
        queue.Dispose();
        Assert.Equal(0, queue.LiveWorkerCount);
    }

    [Fact]
    public async Task ThreadsAreReusedBetweenRunsRatherThanRecreated()
    {
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 3 });
        await queue.RunAsync(8, (_, _) => { }, null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);
        await queue.RunAsync(8, (_, _) => { }, null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(3, queue.LiveWorkerCount);
    }

    [Fact]
    public void DegreeIsFlooredAtOneAndHasNoCeiling()
    {
        // Stock caps at MAX_TOOL_THREADS of 16 and drops to ONE
        // above 32 processors.
        using var zero = new WorkQueue(new CompileParallelism { MaxDegree = 0 });
        using var huge = new WorkQueue(new CompileParallelism { MaxDegree = 200 });

        Assert.Equal(1, zero.Degree);
        Assert.Equal(200, huge.Degree);
    }

    // ---- Scratch ------------------------------------------------------------

    [Fact]
    public async Task ScratchIsBuiltOncePerWorkerAndNotOncePerItem()
    {
        int built = 0;
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 4 });

        await queue.RunAsync<object, int>(
            10_000,
            (index, _, _) => index,
            _ =>
            {
                Interlocked.Increment(ref built);
                return new object();
            },
            null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(4, built);
    }

    [Fact]
    public async Task ScratchIsDisposedWhenTheRunEnds()
    {
        var scratches = new ConcurrentBag<DisposableScratch>();
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 3 });

        await queue.RunAsync<DisposableScratch, int>(
            100,
            (index, _, _) => index,
            _ =>
            {
                var scratch = new DisposableScratch();
                scratches.Add(scratch);
                return scratch;
            },
            null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(3, scratches.Count);
        Assert.All(scratches, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task EachWorkerSeesItsOwnIndex()
    {
        var indices = new ConcurrentBag<int>();
        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 5 });

        await queue.RunAsync<object, int>(
            10_000,
            (index, _, _) => index,
            workerIndex =>
            {
                indices.Add(workerIndex);
                return new object();
            },
            null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, indices.Order().ToArray());
    }

    // ---- Progress -----------------------------------------------------------

    [Fact]
    public async Task ProgressCountsUpToTheItemCount()
    {
        // Collected rather than max-tracked with a read-then-exchange, which is
        // itself a race and lost a value the first time this was written.
        var seen = new ConcurrentBag<long>();
        var progress = new CallbackProgress(p => seen.Add(p.Done));

        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 4 });
        await queue.RunAsync(
            500,
            (_, _) => { },
            new WorkQueueOptions { Stage = "test.Stage", Progress = progress },
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(500, seen.Count);
        Assert.Equal(500, seen.Max());
        Assert.Equal(500, seen.Distinct().Count());
    }

    [Fact]
    public async Task ProgressCarriesTheStageName()
    {
        string stage = string.Empty;
        var progress = new CallbackProgress(p => stage = p.Stage);

        using var queue = new WorkQueue(CompileParallelism.Serial);
        await queue.RunAsync(
            1,
            (_, _) => { },
            new WorkQueueOptions { Stage = "vvis.PortalFlow", Progress = progress },
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal("vvis.PortalFlow", stage);
    }

    [Fact]
    public async Task ProgressIsReportedOutsideTheClaimPathSoABlockingHandlerCannotStallTheRun()
    {
        // Stock calls UpdatePacifier INSIDE the one global critical section it
        // takes to hand out work, so a slow pacifier -- a
        // write to a pipe or a log file rather than a console -- blocks every
        // other worker from claiming anything.
        //
        // Here the first report blocks until five more items have RUN. That can
        // only happen if the other worker is free to keep claiming while the
        // first one is inside the progress handler. Under stock's shape this
        // deadlocks and the fact fails on its timeout.
        using var enoughRan = new ManualResetEventSlim(false);
        int reports = 0;
        int bodies = 0;

        var progress = new CallbackProgress(_ =>
        {
            if (Interlocked.Increment(ref reports) == 1)
            {
                enoughRan.Wait(Patience);
            }
        });

        using var queue = new WorkQueue(new CompileParallelism { MaxDegree = 2 });
        await queue.RunAsync(
            10,
            (_, _) =>
            {
                if (Interlocked.Increment(ref bodies) >= 6)
                {
                    enoughRan.Set();
                }
            },
            new WorkQueueOptions { Progress = progress, ChunkSize = 1 },
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(10, reports);
    }

    // ---- A host-supplied scheduler -----------------------------------------

    [Fact]
    public async Task AHostSuppliedSchedulerIsUsedForEveryWorker()
    {
        var scheduler = new CountingScheduler();
        using var queue = new WorkQueue(
            new CompileParallelism { MaxDegree = 3, Scheduler = scheduler });

        int[] results = await queue.RunAsync<object?, int>(
            1000, (index, _, _) => index, _ => null, null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(3, scheduler.Queued);
        Assert.Equal(1000, results.Length);
        Assert.Equal(999, results[999]);
    }

    [Fact]
    public async Task AHostSuppliedSchedulerMeansTheQueueCreatesNoThreadsOfItsOwn()
    {
        var scheduler = new CountingScheduler();
        using var queue = new WorkQueue(
            new CompileParallelism { MaxDegree = 3, Scheduler = scheduler });

        await queue.RunAsync(100, (_, _) => { }, null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(0, queue.LiveWorkerCount);
    }

    [Fact]
    public async Task AHostSuppliedSchedulerStillClaimsEveryIndexExactlyOnce()
    {
        const int items = 20_000;
        int[] claims = new int[items];
        var scheduler = new CountingScheduler();

        using var queue = new WorkQueue(
            new CompileParallelism { MaxDegree = 8, Scheduler = scheduler });

        await queue.RunAsync(
            items,
            (index, _) => Interlocked.Increment(ref claims[index]),
            null,
            CancellationToken.None).WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(items, claims.Count(c => c == 1));
    }

    private sealed class DisposableScratch : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class CallbackProgress : IProgress<CompileProgress>
    {
        private readonly Action<CompileProgress> _report;

        public CallbackProgress(Action<CompileProgress> report) => _report = report;

        public void Report(CompileProgress value) => _report(value);
    }

    /// <summary>
    /// A scheduler that runs each task on a thread of its own and counts what
    /// it was given, standing in for a host that lends the compile its pool.
    /// </summary>
    private sealed class CountingScheduler : TaskScheduler
    {
        private int _queued;

        public int Queued => Volatile.Read(ref _queued);

        protected override IEnumerable<Task> GetScheduledTasks() => [];

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref _queued);
            var thread = new Thread(() => TryExecuteTask(task)) { IsBackground = true };
            thread.Start();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
            false;
    }
}
