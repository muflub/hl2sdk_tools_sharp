//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// Where vrad's pooled scratch arrays come from and go back to: a transfer
/// build's, and the per-worker batches, ray logs and sample arenas of the
/// lighting stages (<see cref="TestLineBatch"/>,
/// <see cref="Light.LightRayLog"/>, <see cref="Ambient.LeafSampleScratch"/>).
/// </summary>
/// <remarks>
/// <para>
/// A compile rents from its own <see cref="CompileScratchPool"/>, which the
/// compile drops when it ends; scratch made outside a compile uses
/// <see cref="UnpooledScratch"/>, which keeps nothing. Never the process's
/// shared array pool: that one outlives the compile and would keep a map's
/// worth of large arrays alive in a long-lived service (the pool's own
/// remarks have the measurements).
/// </para>
/// <para>
/// A seam rather than a concrete pool so a fact can count what was rented
/// against what came back -- the whole promise of the scratch is that a
/// failed or cancelled build hands every array back, and that is only
/// checkable with a pool the test owns.
/// </para>
/// </remarks>
internal interface IScratchArrayPool
{
    /// <summary>Rents an array of at least <paramref name="minimumLength"/> elements.</summary>
    T[] Rent<T>(int minimumLength);

    /// <summary>Gives back an array <see cref="Rent{T}"/> handed out.</summary>
    void Return<T>(T[] array);

    /// <summary>
    /// The view worker <paramref name="workerIndex"/> of a stage should rent
    /// through: for <see cref="CompileScratchPool"/>, that worker's own shard,
    /// so the workers of a stage do not queue on one lock.
    /// </summary>
    /// <param name="workerIndex">The worker's index in its stage, from zero.</param>
    /// <returns>A pool whose arrays are this pool's; by default this pool itself.</returns>
    /// <remarks>
    /// Only a hint about who is renting: an array rented through one view
    /// may be returned through another, or through the pool itself, and a
    /// view may be used by any thread. A pool with nothing to shard (a
    /// fact's counting pool, <see cref="UnpooledScratch"/>) keeps the
    /// default.
    /// </remarks>
    IScratchArrayPool ForWorker(int workerIndex) => this;
}

/// <summary>
/// The arrays one build rented, returned together when the build ends.
/// </summary>
/// <remarks>
/// <para>
/// Disposed by a <c>using</c> around the whole build, so every array goes
/// back however the build ends: finished, failed or cancelled. A pooled
/// array outliving its build would either leak (a service that runs
/// compiles for days would keep renting new ones) or, worse, be handed to
/// the next compile while this one still wrote into it.
/// </para>
/// <para>
/// A rented array is at least as long as asked and may hold an earlier
/// renter's data: every caller slices it to the length it asked for and
/// writes before it reads. Zero-length requests are served with an empty
/// array and never reach the pool, so an empty build rents nothing.
/// </para>
/// </remarks>
internal sealed class ScratchArrays : IDisposable
{
    private readonly IScratchArrayPool _pool;
    private readonly List<Action> _returns = [];

    /// <summary>Starts an empty set over <paramref name="pool"/>.</summary>
    public ScratchArrays(IScratchArrayPool pool) => _pool = pool;

    /// <summary>Rents an array, remembered so <see cref="Dispose"/> returns it.</summary>
    /// <param name="minimumLength">The fewest elements the caller needs.</param>
    /// <returns>An array of at least that many elements; its contents are unspecified.</returns>
    public T[] Rent<T>(int minimumLength)
    {
        if (minimumLength == 0)
        {
            return [];
        }

        T[] array = _pool.Rent<T>(minimumLength);
        _returns.Add(() => _pool.Return(array));
        return array;
    }

    /// <summary>Returns every array rented through this set, once.</summary>
    public void Dispose()
    {
        foreach (Action giveBack in _returns)
        {
            giveBack();
        }

        _returns.Clear();
    }
}
