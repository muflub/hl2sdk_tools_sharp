//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.ExceptionServices;

namespace SourceSharp.MapTools.Parallel;

/// <summary>
/// A synchronous parallel loop for code that is already running as one work
/// item and cannot await: the calling thread works through the items itself,
/// and helpers queued on a scheduler join it if and when a thread is free.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="WorkQueue"/> or <c>Parallel.For</c>.</b> The one
/// user is a collision cook, which runs inside a synchronous callback on a
/// pool thread (<see cref="Phys.ICollisionCooker.RunAsync{T}"/>). Awaiting is
/// not possible there, and blocking on a nested run of the same pool is the
/// deadlock <see cref="CompilePool"/> forbids: with every pool thread holding a
/// cook that waits for helpers, no thread is left to run the helpers. So the
/// caller never waits for a helper that has not started. It claims items
/// itself until none are left, then closes the loop: a helper that starts after
/// that finds the loop closed and returns without touching anything, and the
/// caller waits only for helpers that are already inside an item, which never
/// block. At worst the caller runs every item alone, which is the serial loop.
/// </para>
/// <para>
/// <b>Items are claimed in index order,</b> one at a time. That is what makes
/// a failure deterministic: every item below a failing one was claimed before
/// it and still runs to its end, so the lowest failing index among the items
/// that ran is the one a serial loop would have stopped at, and that failure
/// is the one rethrown. Nothing new is claimed once any item has failed.
/// </para>
/// <para>
/// <b>Per-participant state</b> comes from a factory run on the participant's
/// own thread, once, when it claims its first item, so a thread-affine scratch
/// (a thread-local cook context) is created where it will be used, and a helper
/// that never gets an item creates nothing.
/// </para>
/// </remarks>
internal static class CallerParallelFor
{
    /// <summary>Runs <paramref name="body"/> for every index below <paramref name="count"/>.</summary>
    /// <typeparam name="TLocal">One participant's scratch.</typeparam>
    /// <param name="count">How many items.</param>
    /// <param name="maxDegree">The most participants, the caller included; one runs every item on the caller.</param>
    /// <param name="scheduler">Where the helpers are queued.</param>
    /// <param name="localInit">Creates a participant's scratch, on its own thread.</param>
    /// <param name="body">One item, given its index and its participant's scratch.</param>
    /// <param name="cancellationToken">Checked before every item.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled before an item started.</exception>
    public static void For<TLocal>(
        int count,
        int maxDegree,
        TaskScheduler scheduler,
        Func<TLocal> localInit,
        Action<int, TLocal> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(localInit);
        ArgumentNullException.ThrowIfNull(body);
        if (count <= 0)
        {
            return;
        }

        Loop<TLocal> loop = new(count, localInit, body, cancellationToken);
        int degree = Math.Clamp(maxDegree, 1, count);
        try
        {
            for (int k = 1; k < degree; k++)
            {
                _ = Task.Factory.StartNew(
                    static state => ((Loop<TLocal>)state!).Help(),
                    loop,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    scheduler);
            }

            loop.Participate();
        }
        finally
        {
            // Also when queueing a helper threw (a disposed pool): the ones
            // already inside an item are waited for, the rest never start.
            loop.Close();
        }

        loop.ThrowIfFailed();
    }

    /// <summary>One run's shared state.</summary>
    private sealed class Loop<TLocal>(int count, Func<TLocal> localInit, Action<int, TLocal> body, CancellationToken cancellationToken)
    {
        // Monitor rather than Lock: the caller waits on it for running helpers.
        private readonly object _gate = new();
        private int _next = -1;
        private int _running = 1; // the caller
        private bool _closed;
        private bool _stop;
        private int _failedIndex = int.MaxValue;
        private ExceptionDispatchInfo? _failure;

        /// <summary>A helper's whole life: nothing at all if the caller has already closed the loop.</summary>
        public void Help()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _running++;
            }

            try
            {
                Participate();
            }
            finally
            {
                lock (_gate)
                {
                    if (--_running == 0)
                    {
                        Monitor.PulseAll(_gate);
                    }
                }
            }
        }

        /// <summary>Claims and runs items until none are left or one has failed. Never throws.</summary>
        public void Participate()
        {
            TLocal local = default!;
            bool haveLocal = false;
            while (!Volatile.Read(ref _stop))
            {
                int index = Interlocked.Increment(ref _next);
                if (index >= count)
                {
                    return;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!haveLocal)
                    {
                        local = localInit();
                        haveLocal = true;
                    }

                    body(index, local);
                }
                catch (Exception error)
                {
                    Fail(index, error);
                    return;
                }
            }
        }

        /// <summary>The caller is done claiming: no helper starts from here on, and the running ones are waited for.</summary>
        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                Volatile.Write(ref _stop, true);
                if (--_running == 0)
                {
                    return;
                }

                while (_running > 0)
                {
                    Monitor.Wait(_gate);
                }
            }
        }

        /// <summary>Rethrows the lowest-index failure, with its own stack.</summary>
        public void ThrowIfFailed() => _failure?.Throw();

        private void Fail(int index, Exception error)
        {
            lock (_gate)
            {
                if (index < _failedIndex)
                {
                    _failedIndex = index;
                    _failure = ExceptionDispatchInfo.Capture(error);
                }

                Volatile.Write(ref _stop, true);
            }
        }
    }
}
