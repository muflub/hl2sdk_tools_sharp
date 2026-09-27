//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// Where <see cref="VisTightening"/>'s idle workers wait for something to
/// flow: one event per worker and one bit per worker saying it is parked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not one semaphore.</b> The schedule first parked its idle workers
/// on a single <see cref="SemaphoreSlim"/>. Every wait takes the semaphore's
/// lock to go in and takes it again to come out, and every release takes it
/// too; a flow splitting its frame for an idle worker releases once per
/// split. Profiling 2fort at 32 threads put that lock second only to the
/// schedule's own gate among the chain's contended locks, reached from the
/// idle path, from every split and from every settled run. Here a worker
/// waits on a <see cref="ManualResetEventSlim"/> of its own, so two threads
/// only ever meet on an event when one is waking the other, and a waker that
/// finds no bit set does one read and nothing else. The thread pool under
/// the compile (<c>CompilePool</c>) parks its threads the same way, for the
/// same reason.
/// </para>
/// <para>
/// <b>The protocol.</b> A worker <see cref="Announce"/>s itself (reset its
/// event, THEN set its bit, with a full fence), reads the schedule again, and
/// only then <see cref="Wait"/>s; it always <see cref="Withdraw"/>s after. A
/// waker publishes what it has to offer, then calls <see cref="Wake"/>, which
/// fences before reading the bits. So either the worker's second read sees the
/// offer, or the waker sees the bit. A waker claims a worker by clearing its
/// bit with an interlocked AND and sets the event only if the bit was still
/// set, so no worker is counted as woken twice and none is woken after it
/// withdrew -- except by a claim that raced its withdrawal, whose set lands on
/// a later park and costs that park one early return, never a missed wake:
/// the event is reset before the bit goes up, so a set that follows the
/// claim of that bit always reaches the wait it was meant for.
/// </para>
/// <para>
/// <b>Nothing to release.</b> The events are never asked for a wait handle,
/// so they hold no kernel object and the instance needs no disposal; it lives
/// and dies with its compile's <see cref="VisTightening"/>.
/// </para>
/// </remarks>
internal sealed class VisIdleWorkers
{
    private readonly ulong[] _parked;
    private readonly ManualResetEventSlim[] _events;

    /// <summary>Makes room for a number of workers, indexed from zero.</summary>
    /// <param name="workers">How many workers; at least one.</param>
    internal VisIdleWorkers(int workers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        _parked = new ulong[(workers + 63) >> 6];
        _events = new ManualResetEventSlim[workers];
        for (int i = 0; i < workers; i++)
        {
            _events[i] = new ManualResetEventSlim(initialState: false);
        }
    }

    /// <summary>How many workers there is room for.</summary>
    internal int Capacity => _events.Length;

    /// <summary>How many workers are parked and not yet claimed by a waker.</summary>
    internal int Parked
    {
        get
        {
            int parked = 0;
            for (int w = 0; w < _parked.Length; w++)
            {
                parked += BitOperations.PopCount(Volatile.Read(ref _parked[w]));
            }

            return parked;
        }
    }

    /// <summary>
    /// Marks a worker parked. The caller must read the schedule again after
    /// this and before <see cref="Wait"/>, and must <see cref="Withdraw"/>
    /// afterwards whether it waited or not.
    /// </summary>
    /// <param name="worker">The worker's index.</param>
    internal void Announce(int worker)
    {
        // Reset BEFORE the bit goes up: a waker can only claim the bit after
        // this, so its set cannot be undone by this reset.
        _events[worker].Reset();
        Interlocked.Or(ref _parked[worker >> 6], 1UL << (worker & 63));
    }

    /// <summary>Waits until a waker claims the worker, or the timeout passes.</summary>
    /// <param name="worker">The worker's index.</param>
    /// <param name="milliseconds">The longest wait.</param>
    /// <returns>True when a waker set the event.</returns>
    internal bool Wait(int worker, int milliseconds) => _events[worker].Wait(milliseconds);

    /// <summary>Takes the worker's bit down if no waker already has.</summary>
    /// <param name="worker">The worker's index.</param>
    internal void Withdraw(int worker) =>
        Interlocked.And(ref _parked[worker >> 6], ~(1UL << (worker & 63)));

    /// <summary>
    /// Wakes up to <paramref name="count"/> parked workers, lowest index first.
    /// </summary>
    /// <remarks>
    /// Fences first, so whatever the caller published before calling is
    /// visible to a worker that is woken, and so the bits are read after it
    /// (see the protocol in the class remarks).
    /// </remarks>
    /// <param name="count">The most workers to wake; zero or less wakes none.</param>
    /// <returns>How many workers this call claimed and signalled.</returns>
    internal int Wake(int count)
    {
        Interlocked.MemoryBarrier();
        int woken = 0;
        for (int w = 0; w < _parked.Length && woken < count; w++)
        {
            ulong bits = Volatile.Read(ref _parked[w]);
            while (bits != 0 && woken < count)
            {
                ulong bit = bits & (~bits + 1);
                ulong before = Interlocked.And(ref _parked[w], ~bit);
                if ((before & bit) != 0)
                {
                    _events[(w << 6) + BitOperations.TrailingZeroCount(bit)].Set();
                    woken++;
                }

                bits = before & ~bit;
            }
        }

        return woken;
    }
}
