//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapTools.Parallel;

using Xunit;

namespace SourceSharp.Tests.MapTools.Parallel;

/// <summary>
/// <see cref="CompilePool"/>: one thread budget for every stage of a compile,
/// handed out a chunk at a time so that stages running together share it.
/// </summary>
public class CompilePoolTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CompileParallelism On(CompilePool pool, int degree) =>
        new() { MaxDegree = degree, Pool = pool };

    // ---- Threads ------------------------------------------------------------

    [Fact]
    public async Task ThreadsStartWithTheFirstJobAndNumberTheDegree()
    {
        using CompilePool pool = new(3);
        Assert.Equal(0, pool.LiveThreadCount);

        using WorkQueue queue = new(On(pool, 3));
        await queue.RunAsync(16, (_, _) => { }, null, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(3, pool.LiveThreadCount);
    }

    [Fact]
    public async Task AQueueOnASharedPoolCreatesNoThreadsOfItsOwn()
    {
        using CompilePool pool = new(2);
        using WorkQueue queue = new(On(pool, 2));
        await queue.RunAsync(16, (_, _) => { }, null, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(0, queue.LiveWorkerCount);
    }

    [Fact]
    public void AQueueIsCappedAtItsPoolsSize()
    {
        using CompilePool pool = new(2);
        using WorkQueue queue = new(On(pool, 8));

        Assert.Equal(2, queue.Degree);
    }

    [Fact]
    public async Task TwoQueuesRunningTogetherUseOnlyThePoolsThreads()
    {
        using CompilePool pool = new(2);
        using WorkQueue a = new(On(pool, 2));
        using WorkQueue b = new(On(pool, 2));
        ConcurrentDictionary<int, bool> threads = new();

        void Body(int index, WorkerContext worker)
        {
            Assert.True(pool.IsPoolThread);
            threads[Environment.CurrentManagedThreadId] = true;
            Thread.SpinWait(2000);
        }

        await Task.WhenAll(
            a.RunAsync(2000, Body, null, CancellationToken.None),
            b.RunAsync(2000, Body, null, CancellationToken.None)).WaitAsync(Patience);

        Assert.InRange(threads.Count, 1, 2);
        Assert.Equal(2, pool.LiveThreadCount);
    }

    [Fact]
    public async Task AShortJobIsNotStarvedByALongOne()
    {
        // The reason the pool hands out chunks, not whole jobs: a stage that
        // starts while another is running gets threads before the first ends.
        using CompilePool pool = new(2);
        using WorkQueue longQueue = new(On(pool, 2));
        using WorkQueue shortQueue = new(On(pool, 2));

        const int longItems = 400;
        int longDone = 0;
        using ManualResetEventSlim longStarted = new();
        Task longRun = longQueue.RunAsync(
            longItems,
            (_, _) =>
            {
                longStarted.Set();
                Thread.Sleep(2);
                Interlocked.Increment(ref longDone);
            },
            new WorkQueueOptions { ChunkSize = 1 },
            CancellationToken.None);

        Assert.True(longStarted.Wait(Patience));
        await shortQueue.RunAsync(10, (_, _) => { }, null, CancellationToken.None).WaitAsync(Patience);
        int doneWhenShortFinished = Volatile.Read(ref longDone);
        await longRun.WaitAsync(Patience);

        Assert.True(doneWhenShortFinished < longItems, $"the short job waited for all {longItems} long items");
    }

    // ---- Worker indices and scratch ----------------------------------------

    [Fact]
    public async Task ADegreeOneJobNeverRunsTwoItemsAtOnceOnABiggerPool()
    {
        using CompilePool pool = new(4);
        using WorkQueue queue = new(On(pool, 1));
        int inside = 0;
        int most = 0;

        await queue.RunAsync(
            500,
            (_, worker) =>
            {
                Assert.Equal(0, worker.WorkerIndex);
                int now = Interlocked.Increment(ref inside);
                InterlockedMax(ref most, now);
                Thread.SpinWait(500);
                Interlocked.Decrement(ref inside);
            },
            new WorkQueueOptions { ChunkSize = 1 },
            CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(1, most);
    }

    [Fact]
    public async Task NoWorkerIndexIsInTwoPlacesAtOnceWhileJobsInterleave()
    {
        using CompilePool pool = new(4);
        using WorkQueue a = new(On(pool, 3));
        using WorkQueue b = new(On(pool, 3));

        static Func<Task> Run(WorkQueue queue)
        {
            int[] inside = new int[queue.Degree];
            bool clash = false;
            return async () =>
            {
                await queue.RunAsync(
                    3000,
                    (_, worker) =>
                    {
                        if (Interlocked.Increment(ref inside[worker.WorkerIndex]) != 1)
                        {
                            clash = true;
                        }

                        Thread.SpinWait(200);
                        Interlocked.Decrement(ref inside[worker.WorkerIndex]);
                    },
                    new WorkQueueOptions { ChunkSize = 1 },
                    CancellationToken.None);
                Assert.False(clash);
            };
        }

        await Task.WhenAll(Run(a)(), Run(b)()).WaitAsync(Patience);
    }

    [Fact]
    public async Task EverySlotsScratchIsBuiltOnceAndDisposedEvenWhenItemsRunOut()
    {
        using CompilePool pool = new(4);
        using WorkQueue queue = new(On(pool, 4));
        ConcurrentBag<(int Index, Scratch Scratch)> built = [];

        await queue.RunAsync<Scratch, int>(
            1,
            (index, _, _) => index,
            workerIndex =>
            {
                Scratch scratch = new();
                built.Add((workerIndex, scratch));
                return scratch;
            },
            null,
            CancellationToken.None).WaitAsync(Patience);

        Assert.Equal([0, 1, 2, 3], built.Select(b => b.Index).Order().ToArray());
        Assert.All(built, b => Assert.True(b.Scratch.Disposed));
    }

    // ---- Faults, cancellation and disposal ---------------------------------

    [Fact]
    public async Task AFaultInOneJobLeavesTheOtherJobWhole()
    {
        using CompilePool pool = new(2);
        using WorkQueue failing = new(On(pool, 2));
        using WorkQueue healthy = new(On(pool, 2));
        int[] claims = new int[5000];

        Task bad = failing.RunAsync(
            1000,
            (index, _) =>
            {
                if (index == 500)
                {
                    throw new InvalidOperationException("item 500");
                }
            },
            null,
            CancellationToken.None);
        Task good = healthy.RunAsync(
            claims.Length, (index, _) => Interlocked.Increment(ref claims[index]), null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.WaitAsync(Patience));
        await good.WaitAsync(Patience);
        Assert.All(claims, c => Assert.Equal(1, c));
    }

    [Fact]
    public async Task CancellingOneJobLeavesTheOtherJobWhole()
    {
        using CompilePool pool = new(2);
        using WorkQueue cancelled = new(On(pool, 2));
        using WorkQueue healthy = new(On(pool, 2));
        using CancellationTokenSource cts = new();
        int[] claims = new int[5000];

        Task stopped = cancelled.RunAsync(
            100_000,
            (index, _) =>
            {
                if (index == 100)
                {
                    cts.Cancel();
                }

                Thread.SpinWait(100);
            },
            null,
            cts.Token);
        Task good = healthy.RunAsync(
            claims.Length, (index, _) => Interlocked.Increment(ref claims[index]), null, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(Patience));
        await good.WaitAsync(Patience);
        Assert.All(claims, c => Assert.Equal(1, c));
    }

    [Fact]
    public async Task ACancelWithNoThreadInsideTheJobStillEndsIt()
    {
        // Degree-one pool, held by a blocked job: the second job never gets a
        // thread, so only the cancel's own wake-up can finish it.
        using CompilePool pool = new(1);
        using WorkQueue holder = new(On(pool, 1));
        using WorkQueue waiting = new(On(pool, 1));
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim holding = new();
        using CancellationTokenSource cts = new();

        Task held = holder.RunAsync(
            1,
            (_, _) =>
            {
                holding.Set();
                release.Wait(Patience);
            },
            null,
            CancellationToken.None);
        Assert.True(holding.Wait(Patience));

        Task queued = waiting.RunAsync(10, (_, _) => { }, null, cts.Token);
        await cts.CancelAsync();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Patience));
        await held.WaitAsync(Patience);
    }

    [Fact]
    public async Task DisposingJoinsTheThreadsAndFailsJobsLeftBehind()
    {
        CompilePool pool = new(1);
        using WorkQueue holder = new(On(pool, 1));
        using WorkQueue left = new(On(pool, 1));
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim holding = new();

        Task held = holder.RunAsync(
            1,
            (_, _) =>
            {
                holding.Set();
                release.Wait(Patience);
            },
            null,
            CancellationToken.None);
        Assert.True(holding.Wait(Patience));
        Task abandoned = left.RunAsync(10, (_, _) => { }, null, CancellationToken.None);

        Task disposing = Task.Run(pool.Dispose);
        release.Set();
        await disposing.WaitAsync(Patience);

        Assert.Equal(0, pool.LiveThreadCount);
        await held.WaitAsync(Patience);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => abandoned.WaitAsync(Patience));
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        CompilePool pool = new(2);
        pool.Dispose();
        pool.Dispose();
        Assert.Equal(0, pool.LiveThreadCount);
    }

    // ---- The pool as a TaskScheduler ---------------------------------------

    [Fact]
    public async Task ItsSchedulerRunsTasksOnThePoolsThreads()
    {
        using CompilePool pool = new(2);
        bool onPool = await Task.Factory.StartNew(
            () => pool.IsPoolThread,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            pool.Scheduler).WaitAsync(Patience);

        Assert.True(onPool);
        Assert.Equal(2, pool.Scheduler.MaximumConcurrencyLevel);
    }

    [Fact]
    public async Task ParallelForAsyncOnItsSchedulerVisitsEveryIndexOnPoolThreads()
    {
        using CompilePool pool = new(3);
        int[] visits = new int[1000];
        int offPool = 0;

        await System.Threading.Tasks.Parallel.ForAsync(
            0,
            visits.Length,
            new ParallelOptions { MaxDegreeOfParallelism = 3, TaskScheduler = pool.Scheduler },
            (i, _) =>
            {
                if (!pool.IsPoolThread)
                {
                    Interlocked.Increment(ref offPool);
                }

                Interlocked.Increment(ref visits[i]);
                return ValueTask.CompletedTask;
            }).WaitAsync(Patience);

        Assert.All(visits, v => Assert.Equal(1, v));
        Assert.Equal(0, offPool);
    }

    [Fact]
    public async Task ScheduledTasksAndJobsShareThePool()
    {
        using CompilePool pool = new(2);
        using WorkQueue queue = new(On(pool, 2));

        Task job = queue.RunAsync(2000, (_, _) => Thread.SpinWait(200), null, CancellationToken.None);
        Task<bool> task = Task.Factory.StartNew(
            () => pool.IsPoolThread,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            pool.Scheduler);

        Assert.True(await task.WaitAsync(Patience));
        await job.WaitAsync(Patience);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int was = Interlocked.CompareExchange(ref target, value, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    private sealed class Scratch : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
