//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Parallel;

/// <summary>What one unit of a <see cref="WorkQueue.RunLoopAsync{TScratch}"/> body found.</summary>
internal enum LoopStep
{
    /// <summary>It took something from the schedule and did it.</summary>
    Worked,

    /// <summary>Nothing was on offer; the worker leaves until woken.</summary>
    Idle,

    /// <summary>The whole run is done.</summary>
    Finished,
}

/// <summary>
/// How a <see cref="WorkQueue.RunLoopAsync{TScratch}"/> body tells idle workers that
/// something is on offer.
/// </summary>
/// <remarks>
/// Made by the caller and handed to the run, which binds it to its job
/// before any body runs; one waker per run. Calling it before or after its
/// run does nothing.
/// </remarks>
internal sealed class LoopWaker
{
    private volatile Action<int>? _wake;

    /// <summary>Wakes up to <paramref name="count"/> idle workers; zero or less wakes none.</summary>
    /// <param name="count">How many units were put on offer.</param>
    /// <remarks>
    /// <para>
    /// On a pool, one pool signal per unit, capped at the run's degree: each
    /// brings one parked thread back (or keeps one that is about to park
    /// awake), and that thread comes back into the run for its next unit.
    /// On a host's scheduler every waiting worker is woken, since they all
    /// wait on one gate; those that find nothing go back to waiting.
    /// </para>
    /// <para>
    /// Whatever the caller published before calling is visible to a
    /// worker that is woken: every path through here is an interlocked
    /// write or a lock.
    /// </para>
    /// </remarks>
    public void Wake(int count)
    {
        if (count > 0)
        {
            _wake?.Invoke(count);
        }
    }

    internal void Attach(Action<int> wake) => _wake = wake;
}
