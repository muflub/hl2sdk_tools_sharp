//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Geometry;

/// <summary>
/// Winding arenas that one compile hands from one short-lived owner to the
/// next: the forks of the parallel tree build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A fork of the tree build gets an arena of its own, because an
/// arena is not thread safe and the fork runs beside its parent. A 2fort
/// compile forks a couple of hundred times, and each new arena grew its first
/// segment by doubling, discarding every array it outgrew; from 8192 points
/// on those are large-object-heap arrays, and large-object allocation is what
/// triggers the blocking gen2 collections that park every compile thread at
/// once. Measured on 2fort, the forks' arenas were about 145 MB of the
/// compile's 345 MB of large-object allocation. With a pool, a finished
/// fork's arena, already grown, goes to the next fork, and a compile makes
/// only as many arenas as it ever has forks alive at once.
/// </para>
/// <para>
/// <b>Output cannot change.</b> An arena comes back <see cref="WindingArena.Reset"/>,
/// and a reset arena gives out exactly the handles a new one would (see
/// there), so which arena a fork gets, new or reused, is invisible to
/// everything it computes.
/// </para>
/// <para>
/// <b>Lifetime: one compile.</b> The pool belongs to the compile's root
/// <see cref="Bsp.Csg.BspBuildContext"/> and is never shared with another
/// compile, so two compiles in one service process never see each other's
/// arenas. It keeps at most as many idle arenas as the compile once had
/// rented at the same time, which the fork depth bounds, and
/// <see cref="Release"/> drops them all when the compile ends, however it
/// ends. A fork returns its arena whether its subtree was built, failed or
/// was cancelled; an arena that is returned after the release, by a fork
/// still unwinding, is simply dropped. Nothing about a pool outlives its
/// compile, and a failed compile leaves nothing behind for the next one.
/// </para>
/// </remarks>
internal sealed class WindingArenaPool
{
    private readonly Lock _gate = new();
    private readonly Stack<WindingArena> _idle = new();
    private bool _released;

    /// <summary>Creates an empty pool whose arenas run under a compliance.</summary>
    /// <param name="compliance">
    /// The compile's compliance: every arena the pool makes carries it, as
    /// the arena it stands in for does (<see cref="WindingArena.Compliance"/>).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="compliance"/> is null.</exception>
    public WindingArenaPool(ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);
        Compliance = compliance;
    }

    /// <summary>The compliance every arena from this pool carries.</summary>
    public ComplianceOptions Compliance { get; }

    /// <summary>How many arenas the pool has made: the most it ever had out at once.</summary>
    public int Created { get; private set; }

    /// <summary>How many arenas are out now.</summary>
    public int Rented { get; private set; }

    /// <summary>How many returned arenas wait for the next <see cref="Rent"/>.</summary>
    public int Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle.Count;
            }
        }
    }

    /// <summary>Whether <see cref="Release"/> has been called.</summary>
    public bool IsReleased
    {
        get
        {
            lock (_gate)
            {
                return _released;
            }
        }
    }

    /// <summary>An empty arena: a returned one if there is one, else a new one.</summary>
    /// <returns>The arena, with no windings in it.</returns>
    /// <remarks>
    /// After <see cref="Release"/> it still works, and always makes a new
    /// arena: a compile that has released its pool keeps nothing, but a
    /// caller that races the release must not fail for it.
    /// </remarks>
    public WindingArena Rent()
    {
        lock (_gate)
        {
            Rented++;
            if (_idle.TryPop(out WindingArena? arena))
            {
                return arena;
            }

            Created++;
        }

        return new WindingArena { Compliance = Compliance };
    }

    /// <summary>Takes an arena back for the next <see cref="Rent"/>.</summary>
    /// <param name="arena">An arena this pool rented out, whose windings nobody reads any more.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="arena"/> runs under another compliance, so it cannot
    /// stand in for this pool's arenas.
    /// </exception>
    /// <remarks>
    /// The arena is reset here, outside the lock, so an idle arena holds no
    /// live flags and a rent is only a pop.
    /// </remarks>
    public void Return(WindingArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (!Equals(arena.Compliance, Compliance))
        {
            throw new ArgumentException("the arena runs under another compliance than the pool", nameof(arena));
        }

        arena.Reset();
        lock (_gate)
        {
            Rented--;
            if (!_released)
            {
                _idle.Push(arena);
            }
        }
    }

    /// <summary>Drops every idle arena and stops keeping returned ones.</summary>
    /// <remarks>
    /// Called when the compile ends, on success, failure or cancellation,
    /// so the arenas' storage goes with the compile. Idempotent.
    /// </remarks>
    public void Release()
    {
        lock (_gate)
        {
            _released = true;
            _idle.Clear();
        }
    }
}
