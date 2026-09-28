//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The count of compiles in flight on one store object, behind
/// <see cref="ICacheStore.BeginRun"/> and <see cref="ICacheStore.RunsInFlight"/>.
/// </summary>
/// <remarks>
/// A store implementation holds one of these as an instance field: the count
/// belongs to the store object the compiles share, never to the process
/// (two stores in one process are two unrelated counts). A lease disposed
/// twice counts once, so a <c>using</c> and an explicit dispose on one path
/// cannot drive the count below the compiles really running.
/// </remarks>
public sealed class CacheRunLeases
{
    private int _count;

    /// <summary>How many leases are open.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Opens one lease.</summary>
    /// <returns>The lease; disposing it closes it.</returns>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _count);
        return new Lease(this);
    }

    private sealed class Lease(CacheRunLeases owner) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._count);
            }
        }
    }
}
