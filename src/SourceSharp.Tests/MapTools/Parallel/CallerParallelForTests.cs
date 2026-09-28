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
/// <see cref="CallerParallelFor"/>: a synchronous loop the calling thread drives
/// itself, which helpers join only if a thread is free, so it can run inside a
/// pool thread without waiting on work that has not started.
/// </summary>
public class CallerParallelForTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(100)]
    public void EveryIndexRunsExactlyOnce(int degree)
    {
        int[] runs = new int[57];

        CallerParallelFor.For(runs.Length, degree, TaskScheduler.Default, () => 0, (i, _) => Interlocked.Increment(ref runs[i]), CancellationToken.None);

        Assert.All(runs, r => Assert.Equal(1, r));
    }

    [Fact]
    public void NoItemsRunsNothingAndCreatesNoScratch()
    {
        int inits = 0;

        CallerParallelFor.For(0, 4, TaskScheduler.Default, () => Interlocked.Increment(ref inits), (_, _) => throw new InvalidOperationException(), CancellationToken.None);

        Assert.Equal(0, inits);
    }

    [Fact]
    public void DegreeOneRunsEverythingOnTheCallerWithOneScratch()
    {
        int caller = Environment.CurrentManagedThreadId;
        ConcurrentBag<int> threads = [];
        int inits = 0;

        CallerParallelFor.For(20, 1, TaskScheduler.Default, () => Interlocked.Increment(ref inits), (_, _) => threads.Add(Environment.CurrentManagedThreadId), CancellationToken.None);

        Assert.Equal(1, inits);
        Assert.All(threads, t => Assert.Equal(caller, t));
    }

    [Fact]
    public void EachParticipantMakesItsScratchOnItsOwnThread()
    {
        ConcurrentBag<(int Thread, int Scratch)> seen = [];

        CallerParallelFor.For(
            200,
            4,
            TaskScheduler.Default,
            () => Environment.CurrentManagedThreadId,
            (_, scratch) =>
            {
                seen.Add((Environment.CurrentManagedThreadId, scratch));
                Thread.SpinWait(200);
            },
            CancellationToken.None);

        Assert.All(seen, s => Assert.Equal(s.Thread, s.Scratch));
        Assert.InRange(seen.Select(s => s.Scratch).Distinct().Count(), 1, 4);
    }

    [Fact]
    public void ItemsRunAtOnceAboveOneDegree()
    {
        // Two items meet at a barrier: only possible if two threads run them.
        using Barrier barrier = new(2);
        bool[] met = new bool[2];

        CallerParallelFor.For(2, 2, TaskScheduler.Default, () => 0, (i, _) => met[i] = barrier.SignalAndWait(TimeSpan.FromSeconds(30)), CancellationToken.None);

        Assert.True(met[0] && met[1]);
    }

    [Fact]
    public void TheCallerNeverWaitsForAHelperThatHasNotStarted()
    {
        // A scheduler that runs nothing until told: the loop must finish on the
        // caller alone, and the helpers, run afterwards, must do nothing at all.
        HeldScheduler held = new();
        int caller = Environment.CurrentManagedThreadId;
        ConcurrentBag<int> threads = [];
        int inits = 0;

        CallerParallelFor.For(30, 4, held, () => Interlocked.Increment(ref inits), (_, _) => threads.Add(Environment.CurrentManagedThreadId), CancellationToken.None);

        Assert.Equal(30, threads.Count);
        Assert.All(threads, t => Assert.Equal(caller, t));
        Assert.Equal(3, held.Queued);

        held.RunAll();
        Assert.Equal(30, threads.Count);
        Assert.Equal(1, inits);
    }

    [Fact]
    public void TheCallerWaitsForAHelperThatIsInsideAnItem()
    {
        // Every item is slow, so helpers are mid-item when the caller runs out
        // of work; when For returns, every item must have finished.
        int[] done = new int[12];

        CallerParallelFor.For(done.Length, 4, TaskScheduler.Default, () => 0, (i, _) =>
        {
            Thread.Sleep(20);
            Volatile.Write(ref done[i], 1);
        }, CancellationToken.None);

        Assert.All(done, d => Assert.Equal(1, d));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void TheLowestFailingIndexIsThrown(int degree)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            CallerParallelFor.For(64, degree, TaskScheduler.Default, () => 0, (i, _) =>
            {
                if (i == 40)
                {
                    throw new InvalidOperationException("40");
                }

                if (i == 9)
                {
                    Thread.Sleep(30);
                    throw new InvalidOperationException("9");
                }
            }, CancellationToken.None));

        Assert.Equal("9", error.Message);
    }

    [Fact]
    public void NothingNewIsClaimedAfterAFailure()
    {
        int started = 0;

        Assert.Throws<InvalidOperationException>(() =>
            CallerParallelFor.For(1000, 1, TaskScheduler.Default, () => 0, (i, _) =>
            {
                Interlocked.Increment(ref started);
                if (i == 3)
                {
                    throw new InvalidOperationException();
                }
            }, CancellationToken.None));

        Assert.Equal(4, started);
    }

    [Fact]
    public void AFailingScratchFactoryIsTheItemsFailure()
    {
        Assert.Throws<FormatException>(() =>
            CallerParallelFor.For<int>(5, 1, TaskScheduler.Default, () => throw new FormatException(), (_, _) => { }, CancellationToken.None));
    }

    [Fact]
    public void APreCancelledTokenRunsNothing()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        int runs = 0;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CallerParallelFor.For(10, 4, TaskScheduler.Default, () => 0, (_, _) => Interlocked.Increment(ref runs), cts.Token));

        Assert.Equal(0, runs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void CancellingMidRunStopsBeforeTheNextItem(int degree)
    {
        using CancellationTokenSource cts = new();
        int runs = 0;
        int runsAtCancel = -1;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CallerParallelFor.For(10_000, degree, TaskScheduler.Default, () => 0, (i, _) =>
            {
                Interlocked.Increment(ref runs);
                if (i == 5)
                {
                    cts.Cancel();
                    Volatile.Write(ref runsAtCancel, Volatile.Read(ref runs));
                }
            }, cts.Token));

        // Serially, exactly the six up to the cancelling one.
        //
        // In parallel the bound is relative to the moment of the cancel, not
        // to item 5's index: whoever claimed item 5 can be preempted between
        // the claim and the body while the others run hundreds of items (seen
        // on a Windows runner: 832). What the loop promises is that once the
        // token is cancelled, every item still to be started sees it, so each
        // of the other participants finishes at most the one item it had
        // already passed the check for. Counted after the Cancel call, so an
        // item that ran in between is already in runsAtCancel.
        if (degree == 1)
        {
            Assert.Equal(6, runs);
        }
        else
        {
            Assert.InRange(runs, runsAtCancel, runsAtCancel + (degree - 1));
        }
    }

    [Fact]
    public void AHelperThatCannotBeQueuedFailsTheLoopAfterTheRunningOnesFinish()
    {
        // A scheduler that refuses new tasks: the loop reports it and runs
        // nothing further on the caller.
        int runs = 0;

        Assert.ThrowsAny<Exception>(() =>
            CallerParallelFor.For(10, 3, new RefusingScheduler(), () => 0, (_, _) => Interlocked.Increment(ref runs), CancellationToken.None));

        Assert.Equal(0, runs);
    }

    [Fact]
    public void OnADisposedPoolTheLoopStillRunsEveryItemOnce()
    {
        // A disposed pool runs a task offered to it on the offering thread
        // (CompilePool.Dispose), so the helpers run inline and the loop ends
        // whole instead of failing a cook half done.
        CompilePool pool = new(2);
        pool.Dispose();
        int[] runs = new int[10];

        CallerParallelFor.For(runs.Length, 3, pool.Scheduler, () => 0, (i, _) => Interlocked.Increment(ref runs[i]), CancellationToken.None);

        Assert.All(runs, r => Assert.Equal(1, r));
    }

    [Fact]
    public async Task OnAOneThreadPoolTheCookingThreadRunsEverythingItself()
    {
        // The deadlock this loop exists to avoid: the pool's only thread runs
        // the loop, so no helper can ever start while it does.
        using CompilePool pool = new(1);
        int[] runs = new int[40];

        await Task.Factory.StartNew(
            () => CallerParallelFor.For(runs.Length, 8, pool.Scheduler, () => 0, (i, _) => runs[i]++, CancellationToken.None),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            pool.Scheduler).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(runs, r => Assert.Equal(1, r));
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => CallerParallelFor.For(1, 1, null!, () => 0, (_, _) => { }, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => CallerParallelFor.For<int>(1, 1, TaskScheduler.Default, null!, (_, _) => { }, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => CallerParallelFor.For(1, 1, TaskScheduler.Default, () => 0, null!, CancellationToken.None));
    }

    /// <summary>Refuses every task, as a scheduler that has shut down would.</summary>
    private sealed class RefusingScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => [];

        protected override void QueueTask(Task task) => throw new ObjectDisposedException(nameof(RefusingScheduler));

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    /// <summary>Queues tasks and runs them only when asked.</summary>
    private sealed class HeldScheduler : TaskScheduler
    {
        private readonly ConcurrentQueue<Task> _tasks = new();

        public int Queued => _tasks.Count;

        public void RunAll()
        {
            while (_tasks.TryDequeue(out Task? task))
            {
                TryExecuteTask(task);
            }
        }

        protected override void QueueTask(Task task) => _tasks.Enqueue(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [.. _tasks];
    }
}
