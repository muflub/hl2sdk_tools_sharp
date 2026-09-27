//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Parallel;

/// <summary>
/// A pooled job's worker slots and the count of threads inside it, handed out
/// and taken back without a lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no lock.</b> Every chunk a pool thread runs starts by borrowing one
/// of the job's slots and ends by giving it back, and at a job's tail every
/// chunk is one item. With both halves under one monitor, a profiled
/// 32-thread compile of a full-size map queued on that monitor about five
/// thousand times for a second and a half of waiting, more than on anything
/// else in the process. Everything the monitor guarded is a counter or a
/// per-slot flag, so each is now one interlocked operation, and a thread that
/// loses a race retries or declines instead of sleeping behind the winner.
/// </para>
/// <para>
/// <b>The state word.</b> How many threads are inside the job and whether it
/// has finished share one <see cref="int"/>: the sign bit is "finished", the
/// rest is the count. Keeping them in one word is what makes "finish exactly
/// once, and only with nobody inside" a single compare-exchange from zero:
/// a thread cannot enter a finished job (<see cref="TryEnter"/> refuses once
/// the bit is set), and the job cannot be finished while anyone is inside
/// (<see cref="TryFinishIdle"/> only succeeds from exactly zero). As two
/// separate fields, a thread could enter between the finisher's check of the
/// count and its store of the flag, and run a chunk against scratch that is
/// being disposed.
/// </para>
/// <para>
/// <b>Slots.</b> Slot <c>i</c> is worker index <c>i</c>. Unbuilt slots are
/// handed out from a counter, so the first thread in gets index 0 and every
/// slot is built exactly once. A built slot not lent to anyone has its flag
/// set; a borrower claims it by compare-exchanging the flag from one to zero,
/// so a slot is never lent to two threads at once. The flags are fronted by a
/// count of free slots: a borrower reserves one from the count before looking
/// for a flag, so a borrower that finds the count at zero declines at once
/// instead of scanning every flag, and a borrower holding a reservation is
/// guaranteed a set flag to find (a returner sets the flag before it adds to
/// the count).
/// </para>
/// <para>
/// <b>Publication.</b> A slot's scratch is written by the thread that built
/// it and read by whichever thread borrows it next. The returner's flag write
/// is a release and the borrower's compare-exchange is a full fence, so the
/// borrower sees the scratch as its builder left it. The job disposes all the
/// scratch only after <see cref="TryFinishIdle"/>, whose success means every
/// borrower has left through <see cref="Leave"/>, an interlocked decrement
/// that follows all of its writes.
/// </para>
/// <para>
/// Per job, never shared: two jobs on one pool have two of these, so nothing
/// here outlives its run.
/// </para>
/// </remarks>
internal sealed class JobSlots
{
    // The sign bit of _state. Set once, never cleared.
    private const int FinishedFlag = int.MinValue;

    // 1 while the slot is built and lent to nobody.
    private readonly int[] _free;
    private int _nextUnbuilt;
    private int _freeCount;
    private int _state;
    private int _turnedAway;

    /// <summary>Creates the slots of a job of a given degree, all unbuilt.</summary>
    /// <param name="degree">How many slots; floored at one.</param>
    public JobSlots(int degree)
    {
        Degree = Math.Max(1, degree);
        _free = new int[Degree];
    }

    /// <summary>How many slots there are.</summary>
    public int Degree { get; }

    /// <summary>Whether the job has been finished (by the last thread out, or abandoned).</summary>
    public bool IsFinished => Volatile.Read(ref _state) < 0;

    /// <summary>How many threads are inside the job: borrowers and scans that found nothing yet.</summary>
    public int InFlight => Volatile.Read(ref _state) & int.MaxValue;

    /// <summary>How many slots nobody has taken to build yet.</summary>
    /// <remarks>
    /// Taken, not built: a slot counts as gone from the moment a thread takes
    /// it to build, and that thread is inside the job until the build (and
    /// its chunk) are done, which is all the finishing rule needs.
    /// </remarks>
    public int UnbuiltLeft => Math.Max(0, Degree - Volatile.Read(ref _nextUnbuilt));

    /// <summary>How many turned-away scans no returned slot has answered yet.</summary>
    public int TurnedAway => Volatile.Read(ref _turnedAway);

    /// <summary>Counts the calling thread inside the job; false once it has finished.</summary>
    /// <remarks>
    /// Every successful call is matched by exactly one <see cref="Leave"/>.
    /// A thread enters BEFORE it looks for a slot, so a thread holding a slot
    /// is always counted and the job cannot be finished under it.
    /// </remarks>
    public bool TryEnter()
    {
        int state = Volatile.Read(ref _state);
        while (true)
        {
            if (state < 0)
            {
                return false;
            }

            int seen = Interlocked.CompareExchange(ref _state, state + 1, state);
            if (seen == state)
            {
                return true;
            }

            state = seen;
        }
    }

    /// <summary>Uncounts the calling thread; true when it was the last one inside.</summary>
    /// <remarks>
    /// A thread that was last out must read the job's run-out and stopping
    /// state AFTER this call, not before: every change toward "done" (the
    /// claim that exhausts the items, the take of the last unbuilt slot, a
    /// fault) is made by a thread that is inside, before its own leave, and
    /// the leaves are totally ordered, so the last one sees all of them. Read
    /// before, another thread could exhaust the job and leave in between, and
    /// neither would finish it.
    /// </remarks>
    public bool Leave() => Interlocked.Decrement(ref _state) == 0;

    /// <summary>Finishes the job if nobody is inside it; true for the one caller that did.</summary>
    /// <remarks>
    /// A compare-exchange from exactly zero: it fails if a thread has entered
    /// since the caller left (that thread will make the same decision when it
    /// leaves) or if another caller already finished the job.
    /// </remarks>
    public bool TryFinishIdle() => Interlocked.CompareExchange(ref _state, FinishedFlag, 0) == 0;

    /// <summary>Finishes the job whoever is counted inside; true for the one caller that did.</summary>
    /// <remarks>
    /// Only for a pool that is gone: its threads have been joined, so the
    /// count can no longer move and nobody will leave to finish the job.
    /// </remarks>
    public bool ForceFinish() => (Interlocked.Or(ref _state, FinishedFlag) & FinishedFlag) == 0;

    /// <summary>Takes a slot to build, lowest index first; -1 when every slot has been taken.</summary>
    public int TryTakeUnbuilt()
    {
        // The read first keeps the counter from climbing on every step of a
        // long job once the slots are built; the increment's overshoot past
        // Degree is then at most the number of threads racing for the last one.
        if (Volatile.Read(ref _nextUnbuilt) >= Degree)
        {
            return -1;
        }

        int slot = Interlocked.Increment(ref _nextUnbuilt) - 1;
        return slot < Degree ? slot : -1;
    }

    /// <summary>Borrows a built slot nobody is using; -1 when every built slot is lent.</summary>
    public int TryTakeFree()
    {
        int count = Volatile.Read(ref _freeCount);
        while (true)
        {
            if (count <= 0)
            {
                return -1;
            }

            int seen = Interlocked.CompareExchange(ref _freeCount, count - 1, count);
            if (seen == count)
            {
                break;
            }

            count = seen;
        }

        // Reserved: at least one flag is set that no other reservation will
        // take, so this ends. A pass can come up empty only when other
        // reservers took the flags ahead of this one while a flag was being
        // set behind it, which the next pass finds.
        int[] free = _free;
        while (true)
        {
            for (int slot = 0; slot < free.Length; slot++)
            {
                if (Volatile.Read(ref free[slot]) == 1
                    && Interlocked.CompareExchange(ref free[slot], 0, 1) == 1)
                {
                    return slot;
                }
            }
        }
    }

    /// <summary>Gives a slot back once its chunk is done (or its build has been tried).</summary>
    public void Return(int slot)
    {
        // Flag first, count second: a reservation must always have a flag to find.
        Volatile.Write(ref _free[slot], 1);
        Interlocked.Increment(ref _freeCount);
    }

    /// <summary>Records a scan that found the job full: items left, every slot lent.</summary>
    /// <remarks>
    /// The caller tries <see cref="TryTakeFree"/> once more afterwards. This
    /// increment and a returner's (in <see cref="Return"/>) are both full
    /// fences, so either the returner sees this entry and wakes a thread for
    /// it, or the retry sees the returned slot. Without the retry a slot given
    /// back between the scan and this count would answer nobody, and the
    /// turned-away thread would sleep out the pool's idle timeout beside it.
    /// </remarks>
    public void TurnAway() => Interlocked.Increment(ref _turnedAway);

    /// <summary>Whether a returned slot should wake an idle pool thread.</summary>
    /// <remarks>See <see cref="WorkQueue.TakeWakeForFreedSlot"/>, which this applies to the job's own count.</remarks>
    public bool TakeWake(bool finishing, bool stopping, bool exhausted) =>
        WorkQueue.TakeWakeForFreedSlot(finishing, stopping, exhausted, ref _turnedAway);
}
