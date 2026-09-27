//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// How a build context gets the side arrays of the brushes it carves.
/// </summary>
/// <remarks>
/// A switch on <see cref="VbspContext"/> rather than a compliance quirk,
/// because it cannot change output: the pool only decides whether an array is
/// new or recycled, and a recycled one is cleared. The facts compile the same
/// maps both ways and compare the bytes, which is what the switch is for.
/// </remarks>
internal enum BrushSidePooling
{
    /// <summary>Every brush gets a fresh array, as before the pool existed.</summary>
    Off,

    /// <summary>Freed brushes' arrays are recycled by capacity.</summary>
    Pooled,

    /// <summary>
    /// Pooled, and every return is checked against the arrays already pooled,
    /// so an array returned twice throws. Costs a hash set per compile; the
    /// facts turn it on.
    /// </summary>
    Checked,
}

/// <summary>
/// The side arrays of freed brushes, kept by length for the next brush of the
/// same capacity. One per <see cref="BspBuildContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Every <see cref="BrushGeometry.SplitBrush"/> allocates two
/// brushes of <c>numsides + 1</c>, and <see cref="BrushGeometry.CopyBrush"/>
/// one of <c>numsides</c>; nearly all of them are freed again a few calls
/// later — the fragments <c>SubtractBrush</c> throws away, the halves
/// <c>CheckPlaneAgainstVolume</c> only counts, the lists <c>BuildTree_r</c>
/// frees once it has split them. On a large map those arrays were the biggest
/// single source of garbage in vbsp. The lengths come from a handful of
/// values (a map brush's side count, plus one per split), so exact-length
/// buckets reuse nearly everything without the capacity ever differing from
/// what <c>AllocBrush</c> was asked for — <see cref="BspBrush.SideCapacity"/>,
/// and the over-append check it backs, mean what they meant before.
/// </para>
/// <para>
/// <b>Lifetime.</b> An array enters the pool only through
/// <see cref="BspBuildContext.FreeBrush"/>, which detaches it from the brush
/// first (<see cref="BspBrush.IsFreed"/>), so a freed brush cannot show a
/// later brush's sides and cannot return the same array twice. Every
/// <c>FreeBrush</c> call site frees a brush that nothing reads again; the ones
/// stock leaks (<c>ClipBrushToBox</c>'s input, the one-sided
/// <c>SubtractBrush</c> list in <c>ChopBrushes</c>, the areaportal fragments
/// <c>RemoveAreaPortalBrushes</c> unlinks, the brushes of every tree that is
/// not freed) are never returned and are simply collected.
/// </para>
/// <para>
/// <b>Not thread-safe, and not shared.</b> A build context is one compile on
/// one logical thread: the vbsp driver runs the CSG and tree stages one at a
/// time on its serial work queue, and nothing parallel (the collision cooker's
/// concurrent convexes work on the written brush lumps, not on these brushes)
/// touches a <see cref="BspBrush"/>. The pool is an instance field of the
/// context, never static, so two compiles in one process never see each
/// other's arrays. <see cref="Clear"/> drops everything when the compile ends,
/// whether it finished, failed or was cancelled.
/// </para>
/// </remarks>
internal sealed class BrushSidePool
{
    /// <summary>
    /// The longest array kept. A map brush may have up to 128 sides and a
    /// split adds one; anything longer is allocated and dropped as before.
    /// </summary>
    internal const int MaxPooledLength = 130;

    /// <summary>
    /// The most arrays kept per length. The pool never holds more than was
    /// live at once anyway; this only bounds a pathological map.
    /// </summary>
    internal const int MaxPerLength = 8192;

    private readonly Stack<BspBrushSide[]>?[] _buckets = new Stack<BspBrushSide[]>?[MaxPooledLength + 1];
    private readonly HashSet<BspBrushSide[]>? _pooled;

    /// <summary>Creates an empty pool.</summary>
    /// <param name="checkReturns">
    /// Whether to track the pooled arrays and throw on a second return of one.
    /// </param>
    internal BrushSidePool(bool checkReturns)
    {
        if (checkReturns)
        {
            _pooled = new HashSet<BspBrushSide[]>(ReferenceEqualityComparer.Instance);
        }
    }

    /// <summary>How many arrays are waiting in the pool now.</summary>
    internal int PooledArrays { get; private set; }

    /// <summary>How many rents were served from the pool rather than allocated.</summary>
    internal long Reused { get; private set; }

    /// <summary>How many arrays were accepted back.</summary>
    internal long Returned { get; private set; }

    /// <summary>A cleared array of exactly <paramref name="length"/> sides.</summary>
    /// <param name="length">The capacity: <c>AllocBrush</c>'s argument.</param>
    /// <returns>The array, every element <c>default</c>.</returns>
    internal BspBrushSide[] Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length == 0)
        {
            return [];
        }

        if (length <= MaxPooledLength && _buckets[length] is { Count: > 0 } bucket)
        {
            BspBrushSide[] array = bucket.Pop();
            _pooled?.Remove(array);
            PooledArrays--;
            Reused++;
            return array;
        }

        return new BspBrushSide[length];
    }

    /// <summary>Takes an array back, clearing it.</summary>
    /// <param name="array">The array of a brush that is gone.</param>
    /// <exception cref="InvalidOperationException">
    /// Checked mode, and the array is already in the pool.
    /// </exception>
    /// <remarks>
    /// Cleared on the way in rather than the way out so that nothing the pool
    /// holds keeps a displacement or a stale winding handle reachable, and so
    /// that <see cref="Rent"/> costs nothing.
    /// </remarks>
    internal void Return(BspBrushSide[] array)
    {
        ArgumentNullException.ThrowIfNull(array);

        if (array.Length == 0 || array.Length > MaxPooledLength)
        {
            return;
        }

        if (_pooled is not null && !_pooled.Add(array))
        {
            throw new InvalidOperationException(
                $"BrushSidePool: a {array.Length}-side array was returned twice");
        }

        Stack<BspBrushSide[]> bucket = _buckets[array.Length] ??= new Stack<BspBrushSide[]>();
        if (bucket.Count >= MaxPerLength)
        {
            _pooled?.Remove(array);
            return;
        }

        Array.Clear(array);
        bucket.Push(array);
        PooledArrays++;
        Returned++;
    }

    /// <summary>Drops every pooled array.</summary>
    /// <remarks>
    /// The pool is still usable afterwards — a rent then allocates — so a
    /// straggling <c>FreeBrush</c> after the compile has released it is
    /// harmless rather than a crash.
    /// </remarks>
    internal void Clear()
    {
        Array.Clear(_buckets);
        _pooled?.Clear();
        PooledArrays = 0;
    }
}
