//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// Where a transfer build's scratch arrays come from and go back to.
/// </summary>
/// <remarks>
/// A seam rather than a direct call on <see cref="ArrayPool{T}.Shared"/> so a
/// fact can count what was rented against what came back -- the whole
/// promise of the scratch is that a failed or cancelled build hands every
/// array back, and that is only checkable with a pool the test owns.
/// </remarks>
internal interface IScratchArrayPool
{
    /// <summary>Rents an array of at least <paramref name="minimumLength"/> elements.</summary>
    T[] Rent<T>(int minimumLength);

    /// <summary>Gives back an array <see cref="Rent{T}"/> handed out.</summary>
    void Return<T>(T[] array);
}

/// <summary>
/// The scratch pool of a real build: <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
/// <remarks>
/// The shared pool is the one that pays off in a long-lived service: the
/// next compile's build, in this process, rents the same large arrays back
/// instead of allocating them again on the large-object heap, and the pool
/// trims what sits unused under memory pressure. Nothing here holds on to an
/// array past the build that rented it.
/// </remarks>
internal sealed class SharedScratchArrayPool : IScratchArrayPool
{
    /// <inheritdoc/>
    public T[] Rent<T>(int minimumLength) => ArrayPool<T>.Shared.Rent(minimumLength);

    /// <inheritdoc/>
    public void Return<T>(T[] array) => ArrayPool<T>.Shared.Return(array);
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
