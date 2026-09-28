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
/// <see cref="WorkQueue.RunLoopAsync{TScratch}"/>: a body run over and over on
/// every worker until it says the work is done, whose idle workers leave the
/// pool's threads to other work instead of parking on them.
/// </summary>
public class WorkQueueLoopTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // Long enough that a worker which was never woken is plainly stuck.
    private static readonly TimeSpan Stuck = TimeSpan.FromSeconds(5);

    private static CompileParallelism On(CompilePool pool, int degree) => new() { MaxDegree = degree, Pool = pool };

    private static CompileParallelism OnScheduler(int degree) =>
        new() { MaxDegree = degree, Scheduler = new ThreadPerTaskScheduler() };

    public static TheoryData<bool> BothPaths => new() { false, true };

    // ---- Every unit once, then done ----------------------------------------

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task EveryUnitOnOfferRunsOnceAndTheRunEndsWhenTheBodySaysSo(bool hostScheduler)
    {
        using CompilePool pool = new(4);
        using WorkQueue queue = new(hostScheduler ? OnScheduler(4) : On(pool, 4));
        Schedule schedule = new(total: 2000, offeredAtStart: 2000);
        LoopWaker waker = new();

        await queue.RunLoopAsync<object?>(
            (_, _) => schedule.Step(),
            _ => null,
            waker,
            null,
            CancellationToken.None).WaitAsync(Patience);

        Assert.All(schedule.Runs, r => Assert.Equal(1, r));
    }

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task UnitsOfferedByTheBodyItselfReachTheIdleWorkers(bool hostScheduler)
    {
        // Each unit puts the next two on offer and wakes for them, as a -tighten
        // settlement does: the run only ends if every offer is taken.
        using CompilePool pool = new(4);
        using WorkQueue queue = new(hostScheduler ? OnScheduler(4) : On(pool, 4));
        LoopWaker waker = new();
        Schedule schedule = new(total: 4000, offeredAtStart: 1) { OnRun = (s, _) => s.Offer(2, waker) };

        await queue.RunLoopAsync<object?>(
            (_, _) => schedule.Step(),
            _ => null,
            waker,
            null,
            CancellationToken.None).WaitAsync(Patience);

        Assert.All(schedule.Runs, r => Assert.Equal(1, r));
    }

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task AWakeFromOutsideReachesEveryIdleWorker(bool hostScheduler)
    {
        // Nothing is on offer at first, so every worker goes idle; a single wake
        // for four units then has to reach four of them. On a host's
        // scheduler the backstop is off, so only the wake can.
        using CompilePool pool = new(4);
        using WorkQueue queue = new(hostScheduler ? OnScheduler(4) : On(pool, 4));
        LoopWaker waker = new();
        Schedule schedule = new(total: 4, offeredAtStart: 0);
        using Barrier together = new(4);
        schedule.OnRun = (_, _) => Assert.True(together.SignalAndWait(Patience));

        Task run = queue.RunLoopAsync<object?>(
            (_, _) => schedule.Step(),
            _ => null,
            waker,
            new WorkQueueOptions { LoopIdleWaitMs = Timeout.Infinite },
            CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => schedule.IdleAnswers >= 4, Patience));

        schedule.Offer(4, waker);
        await run.WaitAsync(Stuck);
        Assert.All(schedule.Runs, r => Assert.Equal(1, r));
    }

    [Fact]
    public async Task OnAHostsSchedulerTheWaitingWorkersSeeTheEndWithoutATimeout()
    {
        // One worker finishes the run while the other three wait with no
        // backstop: the finish itself has to wake them, or the run never
        // completes (a host scheduler's run ends when every worker has).
        using WorkQueue queue = new(OnScheduler(4));
        LoopWaker waker = new();
        Schedule schedule = new(total: 1, offeredAtStart: 0);

        Task run = queue.RunLoopAsync<object?>(
            (_, _) => schedule.Step(),
            _ => null,
            waker,
            new WorkQueueOptions { LoopIdleWaitMs = Timeout.Infinite },
            CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => schedule.IdleAnswers >= 4, Patience));

        // One unit: the wake brings every worker back, one takes the unit and
        // holds it until the other three have found nothing and are waiting
        // again, so they are woken only by the finish.
        int idleBefore = schedule.IdleAnswers;
        schedule.OnRun = (s, _) => Assert.True(SpinWait.SpinUntil(() => s.IdleAnswers >= idleBefore + 3, Patience));
        schedule.Offer(1, waker);
        await run.WaitAsync(Stuck);
    }

    // ---- Idle workers leave the pool's threads -------------------------------

    [Fact]
    public async Task AnIdleLoopHoldsNoPoolThreadSoAnotherJobGetsThemAll()
    {
        // The -tighten regression: its workers used to park inside their step,
        // holding every thread of a shared pool while they waited. Here the
        // loop has nothing on offer, and a job that needs both of the pool's
        // threads at once (the barrier) must still run.
        using CompilePool pool = new(2) { IdleTimeoutMs = Timeout.Infinite };
        using WorkQueue loopQueue = new(On(pool, 2));
        using WorkQueue other = new(On(pool, 2));
        LoopWaker waker = new();
        Schedule schedule = new(total: 10, offeredAtStart: 0);

        Task loop = loopQueue.RunLoopAsync<object?>(
            (_, _) => schedule.Step(),
            _ => null,
            waker,
            null,
            CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => schedule.IdleAnswers >= 2, Patience));

        // Nothing to do anywhere, so both threads park: an idle body sends its
        // thread away rather than straight back into the job.
        Assert.True(SpinWait.SpinUntil(() => pool.ParkedThreadCount == 2, Stuck));

        using Barrier together = new(2);
        await other.RunAsync(
            2,
            (_, _) => Assert.True(together.SignalAndWait(Patience)),
            new WorkQueueOptions { ChunkSize = 1 },
            CancellationToken.None).WaitAsync(Stuck);

        schedule.Offer(10, waker);
        await loop.WaitAsync(Patience);
        Assert.All(schedule.Runs, r => Assert.Equal(1, r));
    }

    [Fact]
    public async Task EveryWorkersScratchIsBuiltOnceAndDisposedEvenWhenTheFirstBodyFinishes()
    {
        // The first unit ends the run; the other slots are still built (once,
        // on a pool thread), their bodies told the run is over, and all four
        // disposed when it completes. Three of the pool's four threads are
        // held by another job, so the one left builds every slot in turn: a
        // build must count as work, or that thread would park after the
        // second with two slots unbuilt and nobody left to build them.
        using CompilePool pool = new(4) { IdleTimeoutMs = Timeout.Infinite };
        using WorkQueue holder = new(On(pool, 3));
        using WorkQueue queue = new(On(pool, 4));
        using ManualResetEventSlim release = new();
        int holding = 0;
        Task held = holder.RunAsync(
            3,
            (_, _) =>
            {
                Interlocked.Increment(ref holding);
                release.Wait(Patience);
            },
            new WorkQueueOptions { ChunkSize = 1 },
            CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref holding) == 3, Patience));

        ConcurrentBag<(int Index, Scratch Scratch)> built = [];
        int ran = 0;
        Task run = queue.RunLoopAsync<Scratch>(
            (_, _) =>
            {
                Interlocked.Increment(ref ran);
                return LoopStep.Finished;
            },
            workerIndex =>
            {
                Scratch scratch = new();
                built.Add((workerIndex, scratch));
                return scratch;
            },
            new LoopWaker(),
            null,
            CancellationToken.None);

        try
        {
            await run.WaitAsync(Stuck);
        }
        finally
        {
            release.Set();
        }

        await held.WaitAsync(Patience);
        Assert.Equal([0, 1, 2, 3], built.Select(b => b.Index).Order().ToArray());
        Assert.All(built, b => Assert.True(b.Scratch.Disposed));
        Assert.Equal(1, ran);
    }

    // ---- Faults, cancels, misuse ---------------------------------------------

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task AFaultInOneBodyFaultsTheRun(bool hostScheduler)
    {
        // Units 499 and 500 are held together, both taken and neither done, so
        // the run has another unit in flight on another worker when 500
        // throws. The fault must still be what the run reports.
        //
        // The unit that throws is named by its own index. This fact used to
        // pick it by the count of units done so far ("throw when 500 are
        // done"), and two units in flight at once read the same count: at 499
        // both, then the count went to 501 and no unit ever saw 500, so
        // nothing threw and the run rightly succeeded (a windows CI run).
        using CompilePool pool = new(2);
        using WorkQueue queue = new(hostScheduler ? OnScheduler(2) : On(pool, 2));
        using Barrier together = new(2);
        Schedule schedule = new(total: 1000, offeredAtStart: 1000)
        {
            OnRun = (_, unit) =>
            {
                if (unit is 499 or 500)
                {
                    Assert.True(together.SignalAndWait(Patience));
                }

                if (unit == 500)
                {
                    throw new InvalidTimeZoneException("unit 500");
                }
            },
        };

        InvalidTimeZoneException error = await Assert.ThrowsAsync<InvalidTimeZoneException>(() =>
            queue.RunLoopAsync<object?>((_, _) => schedule.Step(), _ => null, new LoopWaker(), null, CancellationToken.None)
                .WaitAsync(Patience));
        Assert.Equal("unit 500", error.Message);
    }

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task AFaultWinsOverAnotherWorkerFinishingTheRun(bool hostScheduler)
    {
        // Worker 0's body throws only after worker 1's has said the run is
        // finished: the run's end and its fault race, and a failure must never
        // be reported as success.
        using CompilePool pool = new(2);
        using WorkQueue queue = new(hostScheduler ? OnScheduler(2) : On(pool, 2));
        int finished = 0;

        Task run = queue.RunLoopAsync<object?>(
            (_, worker) =>
            {
                if (worker.WorkerIndex != 0)
                {
                    Volatile.Write(ref finished, 1);
                    return LoopStep.Finished;
                }

                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref finished) == 1, Patience));
                throw new InvalidTimeZoneException("after the finish");
            },
            _ => null,
            new LoopWaker(),
            null,
            CancellationToken.None);

        InvalidTimeZoneException error = await Assert.ThrowsAsync<InvalidTimeZoneException>(() => run.WaitAsync(Patience));
        Assert.Equal("after the finish", error.Message);
    }

    [Theory]
    [MemberData(nameof(BothPaths))]
    public async Task ACancelWhileEveryWorkerIsIdleEndsTheRunCancelled(bool hostScheduler)
    {
        // Nothing on offer, every worker idle, nobody will ever wake them: the
        // cancel alone has to end the run (on a pool it signals the pool; on a
        // host's scheduler the waiting workers notice it at their backstop).
        using CompilePool pool = new(3) { IdleTimeoutMs = Timeout.Infinite };
        using WorkQueue queue = new(hostScheduler ? OnScheduler(3) : On(pool, 3));
        using CancellationTokenSource cts = new();
        Schedule schedule = new(total: 10, offeredAtStart: 0);

        Task run = queue.RunLoopAsync<object?>(
            (_, _) => schedule.Step(), _ => null, new LoopWaker(), null, cts.Token);
        Assert.True(SpinWait.SpinUntil(() => schedule.IdleAnswers >= 3, Patience));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Stuck));
        Assert.True(run.IsCanceled, $"the run ended {run.Status}");
    }

    [Fact]
    public async Task APreCancelledTokenBuildsNothingAndLeavesTheQueueUsable()
    {
        using CompilePool pool = new(2);
        using WorkQueue queue = new(On(pool, 2));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        int built = 0;

        Task run = queue.RunLoopAsync<object?>(
            (_, _) => LoopStep.Finished,
            _ =>
            {
                Interlocked.Increment(ref built);
                return null;
            },
            new LoopWaker(),
            null,
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, built);
        await queue.RunLoopAsync<object?>((_, _) => LoopStep.Finished, _ => null, new LoopWaker(), null, CancellationToken.None)
            .WaitAsync(Patience);
    }

    [Fact]
    public async Task ASecondRunWhileALoopRunsIsRefusedAndTheQueueIsFitAfterwards()
    {
        using CompilePool pool = new(2);
        using WorkQueue queue = new(On(pool, 2));
        LoopWaker waker = new();
        Schedule schedule = new(total: 5, offeredAtStart: 0);

        Task loop = queue.RunLoopAsync<object?>((_, _) => schedule.Step(), _ => null, waker, null, CancellationToken.None);
        Refused<InvalidOperationException>(() => queue.RunAsync(1, (_, _) => { }, null, CancellationToken.None));
        Refused<InvalidOperationException>(() => queue.RunLoopAsync<object?>((_, _) => LoopStep.Finished, _ => null, new LoopWaker(), null, CancellationToken.None));

        schedule.Offer(5, waker);
        await loop.WaitAsync(Patience);
        await queue.RunAsync(4, (_, _) => { }, null, CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public void NullArgumentsAndADisposedQueueAreRefused()
    {
        WorkQueue queue = new(new CompileParallelism { MaxDegree = 1 });
        LoopWaker waker = new();
        Refused<ArgumentNullException>(() => queue.RunLoopAsync<object?>(null!, _ => null, waker, null, CancellationToken.None));
        Refused<ArgumentNullException>(() => queue.RunLoopAsync<object?>((_, _) => LoopStep.Finished, null!, waker, null, CancellationToken.None));
        Refused<ArgumentNullException>(() => queue.RunLoopAsync<object?>((_, _) => LoopStep.Finished, _ => null, null!, null, CancellationToken.None));

        queue.Dispose();
        Refused<ObjectDisposedException>(() => queue.RunLoopAsync<object?>((_, _) => LoopStep.Finished, _ => null, waker, null, CancellationToken.None));
    }

    [Fact]
    public async Task AWakerOutsideItsRunDoesNothing()
    {
        LoopWaker waker = new();
        waker.Wake(3);

        using CompilePool pool = new(2);
        using WorkQueue queue = new(On(pool, 2));
        await queue.RunLoopAsync<object?>((_, _) => LoopStep.Finished, _ => null, waker, null, CancellationToken.None)
            .WaitAsync(Patience);

        long signals = pool.SignalCount;
        waker.Wake(0);
        waker.Wake(-1);
        Assert.Equal(signals, pool.SignalCount);
    }

    [Fact]
    public async Task AWakeOnAPoolSignalsOncePerUnitUpToTheDegree()
    {
        using CompilePool pool = new(2);
        using WorkQueue queue = new(On(pool, 2));
        LoopWaker waker = new();
        Schedule schedule = new(total: 1, offeredAtStart: 0);
        Task run = queue.RunLoopAsync<object?>((_, _) => schedule.Step(), _ => null, waker, null, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => schedule.IdleAnswers >= 2, Patience));

        long before = pool.SignalCount;
        waker.Wake(1000);
        Assert.Equal(2, pool.SignalCount - before);

        schedule.Offer(1, waker);
        await run.WaitAsync(Patience);
    }

    // For the calls that throw before they return a task.
    private static void Refused<T>(Func<Task> start)
        where T : Exception =>
        Assert.Throws<T>(() => { _ = start(); });

    // A pool of units: `offered` are on offer, each taken once; the body is
    // done when `total` have run.
    private sealed class Schedule(int total, int offeredAtStart)
    {
        private int _offered = offeredAtStart;
        private int _taken;
        private int _done;
        private int _idle;

        public int[] Runs { get; } = new int[total];

        // Runs inside each unit with the unit's own index (its place in the
        // take order), before the unit counts as done.
        public Action<Schedule, int>? OnRun { get; set; }

        public int Done => Volatile.Read(ref _done);

        public int IdleAnswers => Volatile.Read(ref _idle);

        public void Offer(int count, LoopWaker waker)
        {
            while (true)
            {
                int before = Volatile.Read(ref _offered);
                int now = Math.Min(total, before + count);
                if (Interlocked.CompareExchange(ref _offered, now, before) == before)
                {
                    waker.Wake(now - before);
                    return;
                }
            }
        }

        public LoopStep Step()
        {
            if (Volatile.Read(ref _done) >= total)
            {
                return LoopStep.Finished;
            }

            int taken = Volatile.Read(ref _taken);
            while (taken < Volatile.Read(ref _offered))
            {
                int seen = Interlocked.CompareExchange(ref _taken, taken + 1, taken);
                if (seen == taken)
                {
                    Interlocked.Increment(ref Runs[taken]);
                    OnRun?.Invoke(this, taken);
                    return Interlocked.Increment(ref _done) >= total
                        ? LoopStep.Finished
                        : LoopStep.Worked;
                }

                taken = seen;
            }

            Interlocked.Increment(ref _idle);
            return LoopStep.Idle;
        }
    }

    private sealed class Scratch : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    // A host's scheduler: every task on a thread of its own.
    private sealed class ThreadPerTaskScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => [];

        protected override void QueueTask(Task task) =>
            new Thread(() => TryExecuteTask(task)) { IsBackground = true }.Start();

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }
}
