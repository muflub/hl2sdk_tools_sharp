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
/// Nothing may block a pool thread on another job of the same pool: a body
/// that waited synchronously for a nested run would wait for threads it is
/// holding. The library has no such wait (the sync-over-async facts pin that).
/// </para>
/// </remarks>
public sealed class CompilePool : IDisposable
{
    private readonly object _sync = new();
    private readonly List<PoolJob> _jobs = [];
    private readonly List<Thread> _threads = [];
    private readonly PoolScheduler _scheduler;
    private long _version;
    private int _idle;
    private int _cursor;
    private bool _shutdown;
    private bool _disposed;

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

    /// <summary>Whether <see cref="Dispose"/> has begun: no thread starts another step.</summary>
    internal bool IsStopping
    {
        get
        {
            lock (_sync)
            {
                return _shutdown;
            }
        }
    }

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
            _shutdown = true;
            threads = [.. _threads];
            Monitor.PulseAll(_sync);
        }

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
        }

        Signal();
    }

    /// <summary>Tells idle threads something may have become runnable.</summary>
    internal void Signal()
    {
        Interlocked.Increment(ref _version);
        if (Volatile.Read(ref _idle) > 0)
        {
            lock (_sync)
            {
                Monitor.PulseAll(_sync);
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
        for (int i = first; i < Degree; i++)
        {
            int index = i;
            Thread thread = new(() => Loop(index))
            {
                IsBackground = true,
                Name = $"ssmap-work-{index}",
            };
            _threads.Add(thread);
        }

        // Published before any starts, so a thread knows itself from its first step.
        Volatile.Write(ref _started, [.. _threads]);
        for (int i = first; i < _threads.Count; i++)
        {
            _threads[i].Start();
        }
    }

    private void Loop(int threadIndex)
    {
        _ = threadIndex;
        while (true)
        {
            long seen = Interlocked.Read(ref _version);
            if (TryStep())
            {
                continue;
            }

            lock (_sync)
            {
                if (_shutdown)
                {
                    return;
                }

                _idle++;
                try
                {
                    // The timeout is a safety net only: every state change that
                    // can make work runnable signals.
                    if (Interlocked.Read(ref _version) == seen)
                    {
                        Monitor.Wait(_sync, 100);
                    }
                }
                finally
                {
                    _idle--;
                }

                if (_shutdown)
                {
                    return;
                }
            }
        }
    }

    // One step of one job (or one scheduled task), round-robin from the cursor.
    private bool TryStep()
    {
        PoolJob[] jobs;
        int start;
        lock (_sync)
        {
            if (_shutdown)
            {
                return false;
            }

            _jobs.RemoveAll(static j => j.IsFinished);
            jobs = [.. _jobs];
            start = _cursor++;
        }

        int count = jobs.Length + 1;
        for (int k = 0; k < count; k++)
        {
            int at = (int)((uint)(start + k) % (uint)count);
            bool worked = at == jobs.Length ? _scheduler.TryRunOne() : jobs[at].Step();
            if (worked)
            {
                return true;
            }
        }

        return false;
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
