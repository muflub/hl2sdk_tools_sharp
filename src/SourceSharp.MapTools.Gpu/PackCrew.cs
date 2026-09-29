//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Gpu;

/// <summary>
/// A few dedicated threads that help one caller run a loop of independent
/// items: the slab batcher's packing helpers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why its own threads.</b> A compile runs on threads it owns or on a
/// scheduler the host lends it, never on the process thread pool
/// (<see cref="Parallel.CompileParallelism"/>): the host may be a web service
/// whose pool must stay free. The packing loop is synchronous (the drainer
/// cannot await in the middle of a slab), and the tracer is built by a
/// factory that is handed no scheduler, so borrowing the compile's would
/// mean a new public option and a public form of the internal
/// <c>CallerParallelFor</c>. A handful of threads the tracer owns is the
/// smaller change and keeps the rule by construction: they are created once
/// per tracer, the first time a slab is big enough to share, and joined when
/// it closes, so nothing outlives the compile, whether it ended well, failed
/// or was cancelled.
/// </para>
/// <para>
/// <b>The caller never waits for a helper that has not started.</b> It
/// claims items itself, one index at a time, until none are left, then waits
/// only for helpers already inside an item, which never block. A helper
/// counts itself in before it claims, so the caller's wait cannot miss it.
/// At worst the caller runs every item alone, which is the serial loop; the
/// bytes a slab gets do not depend on who packed which chunk.
/// </para>
/// <para>
/// A failing item stops further claims, and the first failure is rethrown
/// on the caller once every running item has finished, so the caller's
/// memory is no longer being written when it sees the exception. Packing
/// only fails on a broken plan, so which failure wins does not matter.
/// </para>
/// </remarks>
internal sealed class PackCrew : IDisposable
{
    private readonly Thread[] _threads;
    private readonly object _gate = new();
    private Job? _job;
    private long _generation;
    private bool _disposed;

    /// <summary>Starts <paramref name="helpers"/> background threads, parked until there is work.</summary>
    /// <param name="helpers">How many; at least 1.</param>
    public PackCrew(int helpers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(helpers, 1);
        _threads = new Thread[helpers];
        try
        {
            for (int i = 0; i < helpers; i++)
            {
                _threads[i] = new Thread(HelperLoop)
                {
                    IsBackground = true,
                    Name = $"ssmap gpu pack {i + 1}",
                };
                _threads[i].Start();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The helper threads, for facts that check they are gone.</summary>
    internal IReadOnlyList<Thread> Threads => _threads;

    /// <summary>
    /// Runs <paramref name="body"/> for every index below
    /// <paramref name="count"/>, on the caller and whichever helpers join.
    /// </summary>
    /// <param name="count">How many items.</param>
    /// <param name="body">One item.</param>
    /// <exception cref="ObjectDisposedException">The crew was disposed.</exception>
    public void Run(int count, Action<int> body)
    {
        Job job = new(count, body);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _job = job;
            _generation++;
            Monitor.PulseAll(_gate);
        }

        try
        {
            job.Work();
        }
        finally
        {
            // Nothing is left to claim; wait out the helpers inside an item.
            // Chunks are copies of tens of microseconds, so a spin is right.
            SpinWait spin = default;
            while (Volatile.Read(ref job.Active) != 0)
            {
                spin.SpinOnce();
            }

            lock (_gate)
            {
                if (ReferenceEquals(_job, job))
                {
                    _job = null;
                }
            }
        }

        if (job.Failure is { } failure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>Stops and joins every helper; safe to call more than once.</summary>
    /// <remarks>
    /// A helper is only ever parked or inside a short item, so the join is
    /// bounded by one item. Called from the batcher's close, after the
    /// drainer has stopped, so no <see cref="Run"/> is in progress.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        foreach (Thread t in _threads)
        {
            if (t is not null && t.IsAlive && t != Thread.CurrentThread)
            {
                t.Join();
            }
        }
    }

    private void HelperLoop()
    {
        long seen = 0;
        while (true)
        {
            Job job;
            lock (_gate)
            {
                while (!_disposed && (_job is null || _generation == seen))
                {
                    Monitor.Wait(_gate);
                }

                if (_disposed)
                {
                    return;
                }

                job = _job!;
                seen = _generation;
            }

            Interlocked.Increment(ref job.Active);
            try
            {
                job.Work();
            }
            finally
            {
                Interlocked.Decrement(ref job.Active);
            }
        }
    }

    private sealed class Job(int count, Action<int> body)
    {
        /// <summary>Helpers inside <see cref="Work"/>; the caller is not counted.</summary>
        public int Active;

        private int _next;

        // Volatile: the other participants poll it between claims.
        private volatile Exception? _failure;

        public Exception? Failure => _failure;

        public void Work()
        {
            while (Failure is null)
            {
                int i = Interlocked.Increment(ref _next) - 1;
                if (i >= count)
                {
                    return;
                }

                try
                {
                    body(i);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _failure, e, null);
                }
            }
        }
    }
}
