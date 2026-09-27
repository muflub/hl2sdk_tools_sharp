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
    private const int IdleTimeoutMs = 100;

    private readonly object _sync = new();
    private readonly List<PoolJob> _jobs = [];
    private readonly List<Thread> _threads = [];
    private readonly PoolScheduler _scheduler;
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

    /// <summary>Stops the threads once their current chunk ends and waits for them.</summary>
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
            lock (pool._sync)
            {
                ObjectDisposedException.ThrowIf(pool._disposed, pool);
                pool.EnsureThreads();
            }

            _tasks.Enqueue(task);
            pool.Signal();
        }

        // Inline only on the pool's own threads, so a task never runs on a
        // caller's thread.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
            !taskWasPreviouslyQueued && pool.IsPoolThread && TryExecuteTask(task);

        protected override IEnumerable<Task> GetScheduledTasks() => [.. _tasks];
    }
}
