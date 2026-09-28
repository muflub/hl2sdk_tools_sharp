//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

namespace SourceSharp.MapTools.Parallel;

/// <summary>
/// One fixed set of dedicated worker threads that every stage of a compile
/// shares: the chain's single thread budget.
/// </summary>
/// <remarks>
/// <para>
/// Without it each <see cref="WorkQueue"/> starts its own
/// <see cref="CompileParallelism.MaxDegree"/> threads, so a whole chain starts
/// and joins about a dozen sets of them, and two stages running at once would
/// run twice as many threads as there are cores. With it, <c>-threads</c> is a
/// ceiling for the whole run.
/// </para>
/// <para>
/// <b>Work is handed out one chunk at a time.</b> A thread takes one chunk of
/// items from one active job, then picks again, round-robin over the jobs that
/// have work. A job therefore never holds a thread for its whole length, and two
/// overlapping stages share the cores instead of the first starving the
/// second. That is why a host's <see cref="TaskScheduler"/> with a concurrency
/// limit is not enough for this: <see cref="WorkQueue"/>'s scheduler path gives
/// each worker the whole job, and a limited scheduler would run the first job's
/// workers to completion before the second job's got a thread.
/// </para>
/// <para>
/// <b>A job's worker indices are its own.</b> A job of degree <c>d</c> sees
/// worker indices <c>0..d-1</c> exactly as it did on a private queue: an index
/// is a slot the job lends a thread for one chunk, never two threads at once,
/// and every slot's scratch is built once, on a pool thread, whether or not
/// items remain for it. The per-worker scratch contract of
/// <see cref="WorkQueue"/> is unchanged.
/// </para>
/// <para>
/// <b>The hand-out takes no pool-wide lock.</b> Every chunk starts with a
/// thread choosing a job, so that choice is on the hottest path in the pool.
/// The active jobs are published as an array that is replaced whole, each
/// thread keeps its own round-robin cursor, and the lock is taken only to add
/// or prune a job. With the monitor taken (and the
/// job list copied) for every chunk, a 32-thread compile of the sandbox map
/// queued on this pool's lock more than on anything else in the process, and
/// allocated one array per chunk doing it.
/// </para>
/// <para>
/// <b>A signal wakes one idle thread; only a new job wakes them all.</b> A
/// signal stands for one thing a thread could now do (a slot came free, a task
/// was queued, a cancelled job needs finishing), so one waiter is enough, and
/// waking every idle thread for it sent the whole herd back through the lock
/// to find the one piece of work already taken. A new job can use every
/// thread, so <see cref="Submit"/> wakes all. A wake that goes to the "wrong"
/// thread loses nothing: a thread parks only after a full scan with no signal
/// since it began, so whatever a signal announced is either taken by a thread
/// that is already awake or found by the one it woke.
/// </para>
/// <para>
/// <b>An idle thread parks on its own event, not on the pool's monitor.</b>
/// Parking on the monitor meant every thread that ran out of work took the
/// pool's lock to wait, and every wake-up had to take it back before the
/// thread could return from the wait: at the end of each job the whole pool
/// queued on one lock twice, and in a profiled 32-thread compile that
/// queueing was the most contended wait in the process. Each thread now
/// publishes itself as parked with an interlocked flag and waits on a
/// <see cref="ManualResetEventSlim"/> of its own, which also spins briefly
/// before blocking, so a job submitted a few microseconds after the last
/// one ended is picked up without a sleep. A waker claims a parked thread by
/// clearing its flag (so no thread is woken twice for one signal) and sets its
/// event; two threads only ever meet on an event when one is waking the other.
/// </para>
/// <para>
/// Nothing may block a pool thread on another job of the same pool: a body
/// that waited synchronously for a nested run would wait for threads it is
/// holding. The library has no such wait (the sync-over-async facts pin that).
/// </para>
/// </remarks>
public sealed class CompilePool : IDisposable
{
    // How long a parked thread sleeps with no signal before it scans again.
    // A safety net only: every state change that can make work runnable
    // signals.
    private const int DefaultIdleTimeoutMs = 100;

    private readonly object _sync = new();
    private readonly List<PoolJob> _jobs = [];
    private readonly List<Thread> _threads = [];
    private readonly PoolScheduler _scheduler;

    // Cancelled when Dispose begins. Never disposed: with no wait handle and
    // no timer it holds nothing to release, and leaving it undisposed means a
    // token handed out earlier can still be linked or registered on after
    // the pool is gone.
    private readonly CancellationTokenSource _stopping = new();
    private long _version;
    private int _idle;
    private bool _shutdown;
    private bool _disposed;
    private long _wakeOne;
    private long _wakeAll;

    // _jobs as the threads read it: replaced whole under _sync, read with no lock.
    private PoolJob[] _active = [];

    /// <summary>Creates a pool; its threads start with the first job.</summary>
    /// <param name="degree">How many threads; floored at one.</param>
    public CompilePool(int degree)
    {
        Degree = Math.Max(1, degree);
        _scheduler = new PoolScheduler(this);
    }

    /// <summary>How many threads the pool runs.</summary>
    public int Degree { get; }

    /// <summary>Cancelled as soon as <see cref="Dispose"/> begins.</summary>
    /// <remarks>
    /// For work a host puts on <see cref="Scheduler"/> itself: pass it (linked
    /// with the work's own token) to a <c>Parallel.ForAsync</c> or a task's
    /// body, and that work stops taking items when the pool goes, instead of
    /// being run to its end on the disposing thread (see <see cref="Dispose"/>).
    /// The library's own loops on the scheduler already watch it.
    /// </remarks>
    public CancellationToken StoppingToken => _stopping.Token;

    /// <summary>
    /// A scheduler whose tasks run on the pool's threads, for code that is
    /// written against <see cref="TaskScheduler"/> (for example
    /// <c>Parallel.ForAsync</c>): its tasks take their turn with the jobs.
    /// </summary>
    public TaskScheduler Scheduler => _scheduler;

    /// <summary>How many of the pool's threads are alive.</summary>
    public int LiveThreadCount
    {
        get
        {
            lock (_sync)
            {
                int live = 0;
                foreach (Thread thread in _threads)
                {
                    if (thread.IsAlive)
                    {
                        live++;
                    }
                }

                return live;
            }
        }
    }

    /// <summary>Whether the calling thread is one of this pool's.</summary>
    /// <remarks>
    /// A scan of at most <see cref="Degree"/> threads rather than a thread-static
    /// owner: the library keeps no mutable static state.
    /// </remarks>
    public bool IsPoolThread => Array.IndexOf(Volatile.Read(ref _started), Thread.CurrentThread) >= 0;

    // The threads started so far, replaced whole so readers need no lock.
    private Thread[] _started = [];

    // Each started thread's parking place, index for index with _started and
    // published the same way.
    private Parker[] _parkers = [];

    /// <summary>
    /// How long a parked thread waits with no signal before it scans again;
    /// <see cref="Timeout.Infinite"/> turns the backstop off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backstop is a safety net, not part of the protocol: every change
    /// that can make work runnable signals, and the hand-out and park paths
    /// are written so that no signal is lost (the version re-read in
    /// <see cref="Park"/>, the retry after a turned-away scan in the queue's
    /// step). With it on, a lost wake costs a thread its timeout, which no
    /// test can tell from scheduling noise; with it off, a lost wake leaves
    /// work sitting unclaimed for good. So the facts that pin those paths
    /// turn it off, and deleting either one then turns them red.
    /// </para>
    /// <para>
    /// Internal and init-only: a host has no reason to change it, and a
    /// compile relying on it would hide exactly the bug it exists to survive.
    /// </para>
    /// </remarks>
    internal int IdleTimeoutMs { get; init; } = DefaultIdleTimeoutMs;

    /// <summary>
    /// For the facts: runs on a pool thread that found nothing to do, before
    /// it announces itself parked and re-reads the signal count.
    /// </summary>
    /// <remarks>
    /// Null in every real pool. It widens the one window the version
    /// re-read in <see cref="Park"/> exists for (a signal raised after the
    /// scan and before the wait) to whatever the fact does in it.
    /// </remarks>
    internal Action? BeforeParkProbe { get; init; }

    /// <summary>
    /// For the facts: runs on a pool thread that found a queue's job full,
    /// after the failed borrow and before the scan is counted as turned away.
    /// </summary>
    /// <remarks>Null in every real pool; see <see cref="SlotReturnedProbe"/>.</remarks>
    internal Action? TurnAwayGapProbe { get; init; }

    /// <summary>
    /// For the facts: runs on a pool thread that has just handed a queue's
    /// job slot back and left the job, before it looks for its next step.
    /// </summary>
    /// <remarks>
    /// Null in every real pool. With <see cref="TurnAwayGapProbe"/> it forces
    /// the interleaving the queue's retry after a turned-away scan exists
    /// for: a slot handed back between the failed borrow and the count, by a
    /// thread that then does not come back for it.
    /// </remarks>
    internal Action? SlotReturnedProbe { get; init; }

    /// <summary>Whether <see cref="Dispose"/> has begun: no thread starts another step.</summary>
    internal bool IsStopping => Volatile.Read(ref _shutdown);

    /// <summary>How many jobs the threads currently choose from, finished ones not yet pruned included.</summary>
    internal int ActiveJobCount => Volatile.Read(ref _active).Length;

    /// <summary>The job list the threads read, as the object it is published as.</summary>
    /// <remarks>For the facts that pin it is replaced only when the job set changes.</remarks>
    internal object ActiveSnapshot => Volatile.Read(ref _active);

    /// <summary>How many threads are parked (or about to park) waiting for a signal.</summary>
    internal int ParkedThreadCount => Volatile.Read(ref _idle);

    /// <summary>How many times a signal woke one parked thread.</summary>
    internal long WakeOneCount => Interlocked.Read(ref _wakeOne);

    /// <summary>How many times a signal woke every parked thread.</summary>
    internal long WakeAllCount => Interlocked.Read(ref _wakeAll);

    /// <summary>How many signals were raised, whether or not a thread was parked.</summary>
    internal long SignalCount => Interlocked.Read(ref _version);

    /// <summary>Stops the threads once their current step ends and waits for them.</summary>
    /// <remarks>
    /// <para>
    /// A pool is disposed by whoever created it, and a host that lent one to a
    /// compile may dispose it while that compile is still running. Nothing the
    /// pool has accepted is then left waiting forever:
    /// </para>
    /// <list type="bullet">
    /// <item>Each thread stops once the step it is running ends: one chunk of a
    /// job, or one task of <see cref="Scheduler"/> run until it returns or
    /// first awaits. The body is never interrupted, so this waits for those
    /// steps and no longer.</item>
    /// <item>A job still in the pool is failed with an
    /// <see cref="ObjectDisposedException"/>, its remaining items not run.</item>
    /// <item>A task that <see cref="Scheduler"/> accepted and no thread has
    /// started is run, once, on the thread calling this, after the pool's
    /// threads have stopped and before this returns. A <see cref="Task"/> the
    /// scheduler did not create cannot be failed from outside, and a task
    /// left in the queue never completes, so whatever awaits it (a
    /// <c>Parallel.ForAsync</c> waiting for its workers, a compile awaiting
    /// that loop) would hang. Run, it completes.</item>
    /// <item>A task offered to <see cref="Scheduler"/> once disposal has begun
    /// runs at once, on the thread that offers it. Refusing it looks tidier
    /// and hangs: the continuation of an <c>await</c> inside a scheduled task
    /// comes back to the scheduler it started on (measured with
    /// <c>Parallel.ForAsync</c>, whose workers and bodies both do), and a
    /// refused continuation faults a task nobody observes while the method
    /// it belongs to never resumes. So a loop whose body was awaiting I/O
    /// when the pool went would wait forever.</item>
    /// </list>
    /// <para>
    /// Running is not the same as finishing the work: code on the scheduler
    /// that watches <see cref="StoppingToken"/> stops at its next check, and
    /// the library's loops (<see cref="ForAsync"/>) then end in an
    /// <see cref="ObjectDisposedException"/>. Code that does not watch it
    /// runs to its end on whichever thread picked it up. Either way nothing
    /// the pool accepted is left waiting, which is what a service that
    /// disposes a lent pool under a running compile needs: the compile ends,
    /// and the process is fit for the next one.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        Thread[] threads;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Volatile.Write(ref _shutdown, true);
            threads = [.. _threads];
        }

        // Before the threads are joined, so a loop that watches it stops
        // taking items now rather than running on until the join.
        _stopping.Cancel();

        // After the flag, so a thread that parks from here on either sees it
        // or is claimed by this wake.
        Wake(all: true);

        foreach (Thread thread in threads)
        {
            if (thread != Thread.CurrentThread)
            {
                thread.Join();
            }
        }

        PoolJob[] left;
        lock (_sync)
        {
            left = [.. _jobs];
            _jobs.Clear();
            Volatile.Write(ref _active, []);
        }

        foreach (PoolJob job in left)
        {
            job.Abandon();
        }

        // After the jobs: a drained task may be awaiting one of them, and
        // should see it failed rather than find it still pending.
        _scheduler.RunLeftBehind();
    }

    /// <summary>
    /// <c>Parallel.ForAsync</c> over <c>0..count-1</c> on <see cref="Scheduler"/>,
    /// ending in a fault if the pool is disposed under it.
    /// </summary>
    /// <param name="count">How many indices.</param>
    /// <param name="degree">The most bodies at once; floored at one.</param>
    /// <param name="body">What to do with one index.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The loop.</returns>
    /// <exception cref="ObjectDisposedException">
    /// The pool was disposed before the loop finished (the task faults with it).
    /// </exception>
    /// <remarks>
    /// <para>
    /// A plain <c>Parallel.ForAsync</c> on <see cref="Scheduler"/> cannot be
    /// told the pool has gone: its workers still queued at disposal are run
    /// on the disposing thread (see <see cref="Dispose"/>), all of its
    /// remaining items with them. This one also watches
    /// <see cref="StoppingToken"/>, so those workers stop before taking an
    /// item and the loop ends at once.
    /// </para>
    /// <para>
    /// Ends as an <see cref="ObjectDisposedException"/>, not a cancellation:
    /// nobody cancelled the compile, and a caller that sees a cancellation it
    /// did not ask for would report the compile as stopped by the user. The
    /// same exception fails a <see cref="WorkQueue"/> job left in a disposed
    /// pool, so a compile caught by disposal fails the same way whichever
    /// stage it was in. A cancel of the caller's own token stays a cancel.
    /// </para>
    /// </remarks>
    internal async Task ForAsync(
        int count,
        int degree,
        Func<int, CancellationToken, ValueTask> body,
        CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return;
        }

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, degree),
            TaskScheduler = _scheduler,
            CancellationToken = linked.Token,
        };

        try
        {
            await System.Threading.Tasks.Parallel.ForAsync(0, count, options, body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && IsStopping)
        {
            throw new ObjectDisposedException(
                nameof(CompilePool), "The compile's thread pool was disposed while a loop on it was still running.");
        }
    }

    internal void Submit(PoolJob job)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureThreads();
            _jobs.Add(job);
            Volatile.Write(ref _active, [.. _jobs]);
        }

        Wake(all: true);
    }

    /// <summary>Tells an idle thread that one more thing may have become runnable.</summary>
    internal void Signal() => Wake(all: false);

    // The version bump comes first and is a full fence, and a parking thread
    // raises its flag and _idle (interlocked, so also fenced) before it
    // re-reads the version: so either this sees the parker and claims it, or
    // the parker sees the bump and does not wait. Nothing here takes a lock.
    private void Wake(bool all)
    {
        long version = Interlocked.Increment(ref _version);
        if (Volatile.Read(ref _idle) == 0)
        {
            return;
        }

        if (all)
        {
            Interlocked.Increment(ref _wakeAll);
        }

        // Starting at a different parker each time spreads the wake-ups, so
        // it is not always the same thread that is taken off its core.
        Parker[] parkers = Volatile.Read(ref _parkers);
        int count = parkers.Length;
        for (int k = 0; k < count; k++)
        {
            Parker parker = parkers[(int)((ulong)(version + k) % (ulong)count)];
            if (!parker.TryClaim())
            {
                continue;
            }

            Interlocked.Decrement(ref _idle);
            parker.Event.Set();
            if (!all)
            {
                Interlocked.Increment(ref _wakeOne);
                return;
            }
        }
    }

    private void EnsureThreads()
    {
        if (_threads.Count == Degree)
        {
            return;
        }

        int first = _threads.Count;
        Parker[] parkers = new Parker[Degree];
        Array.Copy(_parkers, parkers, first);
        for (int i = first; i < Degree; i++)
        {
            int index = i;
            Parker parker = new();
            parkers[i] = parker;
            Thread thread = new(() => Loop(index, parker))
            {
                IsBackground = true,
                Name = $"ssmap-work-{index}",
            };
            _threads.Add(thread);
        }

        // Published before any starts, so a thread knows itself from its first
        // step and a waker can find it from its first park.
        Volatile.Write(ref _parkers, parkers);
        Volatile.Write(ref _started, [.. _threads]);
        for (int i = first; i < _threads.Count; i++)
        {
            _threads[i].Start();
        }
    }

    private void Loop(int threadIndex, Parker parker)
    {
        // This thread's own round-robin cursor, staggered by its index so the
        // threads do not all start on the same job. A shared cursor would be
        // one more cache line every thread writes per chunk.
        int cursor = threadIndex;
        while (true)
        {
            long seen = Interlocked.Read(ref _version);
            if (TryStep(cursor++))
            {
                continue;
            }

            if (Volatile.Read(ref _shutdown))
            {
                return;
            }

            Park(parker, seen);
        }
    }

    // Waits for a signal newer than `seen`, or the timeout.
    private void Park(Parker parker, long seen)
    {
        BeforeParkProbe?.Invoke();

        // Reset before the flag goes up: a waker sets the event only after it
        // has claimed the flag, so its set can never be undone by this reset.
        // A set left over from an earlier claim that landed after that park
        // had already timed out makes this wait return early, which costs one
        // extra scan and loses nothing.
        parker.Event.Reset();
        Interlocked.Exchange(ref parker.Parked, 1);
        Interlocked.Increment(ref _idle);
        if (Interlocked.Read(ref _version) == seen && !Volatile.Read(ref _shutdown))
        {
            parker.Event.Wait(IdleTimeoutMs);
        }

        // Still unclaimed (a timeout, or a signal seen before waiting): take
        // the flag back. Claimed means the waker has already uncounted it.
        if (parker.TryClaim())
        {
            Interlocked.Decrement(ref _idle);
        }
    }

    // One step of one job (or one scheduled task), round-robin from the
    // cursor, with no lock: the job list is a published snapshot, and a job
    // that finishes while a thread holds an older snapshot simply declines
    // the step. Finished jobs are pruned by the first thread to see one.
    private bool TryStep(int start)
    {
        if (Volatile.Read(ref _shutdown))
        {
            return false;
        }

        PoolJob[] jobs = Volatile.Read(ref _active);
        bool sawFinished = false;
        bool worked = false;
        int count = jobs.Length + 1;
        for (int k = 0; k < count && !worked; k++)
        {
            int at = (int)((uint)(start + k) % (uint)count);
            if (at == jobs.Length)
            {
                worked = _scheduler.TryRunOne();
            }
            else if (jobs[at].IsFinished)
            {
                sawFinished = true;
            }
            else
            {
                worked = jobs[at].Step();
            }
        }

        if (sawFinished)
        {
            Prune();
        }

        return worked;
    }

    // Drops finished jobs so the snapshot does not grow over a long-lived
    // pool's many compiles.
    private void Prune()
    {
        lock (_sync)
        {
            if (_jobs.RemoveAll(static j => j.IsFinished) > 0)
            {
                Volatile.Write(ref _active, [.. _jobs]);
            }
        }
    }

    /// <summary>A unit of work the pool hands its threads a step at a time.</summary>
    internal abstract class PoolJob
    {
        /// <summary>Whether the job is complete and can leave the pool.</summary>
        public abstract bool IsFinished { get; }

        /// <summary>Runs at most one chunk; true when it did something.</summary>
        public abstract bool Step();

        /// <summary>Ends the job unfinished: the pool is gone.</summary>
        public abstract void Abandon();
    }

    // Where one pool thread waits for work.
    //
    // The event is never disposed: a ManualResetEventSlim that nobody asks for
    // a WaitHandle holds no kernel object, so there is nothing to release, and
    // not disposing it means a waker that claimed a thread just before the
    // pool shut down can still set its event safely.
    private sealed class Parker
    {
        public readonly ManualResetEventSlim Event = new(initialState: false);

        // 1 while the thread is parked and no waker has claimed it.
        public int Parked;

        // Clears the flag if it is up; true for the one caller that did.
        public bool TryClaim() => Interlocked.CompareExchange(ref Parked, 0, 1) == 1;
    }

    // The pool as a TaskScheduler: tasks queue, and a thread runs one per step.
    private sealed class PoolScheduler(CompilePool pool) : TaskScheduler
    {
        private readonly ConcurrentQueue<Task> _tasks = new();

        public override int MaximumConcurrencyLevel => pool.Degree;

        public bool TryRunOne()
        {
            if (!_tasks.TryDequeue(out Task? task))
            {
                return false;
            }

            TryExecuteTask(task);
            return true;
        }

        protected override void QueueTask(Task task)
        {
            // Enqueued under the same lock that Dispose raises its flag
            // under, so a task is either in the queue before disposal begins
            // (and the drain after the join finds it) or sees the flag: none
            // can slip in after the drain has looked.
            bool accepted;
            lock (pool._sync)
            {
                accepted = !pool._disposed;
                if (accepted)
                {
                    pool.EnsureThreads();
                    _tasks.Enqueue(task);
                }
            }

            if (accepted)
            {
                pool.Signal();
                return;
            }

            // Offered after disposal began: nobody is left to run it, so the
            // offering thread does, now (see Dispose for why it is run rather
            // than refused).
            TryExecuteTask(task);
        }

        // Runs every task still queued, on the calling thread: the pool's
        // threads are gone (see Dispose for why they are run, not dropped).
        public void RunLeftBehind()
        {
            while (_tasks.TryDequeue(out Task? task))
            {
                TryExecuteTask(task);
            }
        }

        // Inline only on the pool's own threads, so a task never runs on a
        // caller's thread.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
            !taskWasPreviouslyQueued && pool.IsPoolThread && TryExecuteTask(task);

        protected override IEnumerable<Task> GetScheduledTasks() => [.. _tasks];
    }
}
