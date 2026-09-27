//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Runtime.CompilerServices;

namespace SourceSharp.MapTools.Collections;

/// <summary>
/// A list whose growing storage is rented from <see cref="ArrayPool{T}.Shared"/>
/// and handed back on <see cref="Dispose"/>, for building a result whose
/// exact length is only known at the end.
/// </summary>
/// <remarks>
/// <para>
/// The usual way to build such a result is a <see cref="List{T}"/> followed by
/// <c>ToArray</c> (or <c>[.. list]</c>): the list's backing array, and every
/// smaller one it outgrew, is garbage the moment the copy is made. On a path
/// that runs once per face or per portal, that garbage is most of what the
/// path allocates. Here the working storage goes back to the pool, so the only
/// allocation that survives is the exact-length result the caller keeps.
/// </para>
/// <para>
/// The pool is process-wide, but nothing is kept past <see cref="Dispose"/>:
/// the storage is returned (and cleared first when <typeparamref name="T"/>
/// holds references, so a returned array never keeps a compile's objects
/// alive). A builder that is not disposed, for example because the build
/// threw, just leaves its array to the garbage collector, which the pool
/// tolerates; callers still dispose in a <c>finally</c> or <c>using</c>.
/// </para>
/// <para>
/// It is a class rather than a struct so that passing it to a helper cannot
/// silently copy the count and lose items.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
internal sealed class PooledArrayBuilder<T> : IDisposable
{
    private const int MinimumGrowth = 16;

    private T[] _items;
    private bool _disposed;

    /// <summary>Starts an empty builder.</summary>
    /// <param name="capacity">
    /// Storage to rent up front. When the caller knows an upper bound, passing
    /// it means the builder never has to grow.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public PooledArrayBuilder(int capacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = capacity == 0 ? [] : ArrayPool<T>.Shared.Rent(capacity);
    }

    /// <summary>How many items have been added.</summary>
    public int Count { get; private set; }

    /// <summary>Appends an item, growing the rented storage if it is full.</summary>
    /// <param name="item">The item.</param>
    /// <exception cref="ObjectDisposedException">The builder was disposed.</exception>
    public void Add(T item)
    {
        if (Count == _items.Length)
        {
            Grow();
        }

        _items[Count++] = item;
    }

    /// <summary>The items added so far, in place; valid until the next <see cref="Add"/> or <see cref="Dispose"/>.</summary>
    /// <returns>A span over the first <see cref="Count"/> items.</returns>
    public Span<T> AsSpan() => _items.AsSpan(0, Count);

    /// <summary>Copies the items into a new array of exactly <see cref="Count"/> elements.</summary>
    /// <returns>The copy; the shared empty array when there are none.</returns>
    public T[] ToArray() => Count == 0 ? [] : AsSpan().ToArray();

    /// <summary>Returns the storage to the pool. Safe to call more than once.</summary>
    public void Dispose()
    {
        _disposed = true;
        Count = 0;
        Return(_items);
        _items = [];
    }

    private void Grow()
    {
        // A disposed builder has empty storage, so without this it would
        // quietly rent again and nothing would ever return the new array.
        ObjectDisposedException.ThrowIf(_disposed, this);

        T[] bigger = ArrayPool<T>.Shared.Rent(Math.Max(MinimumGrowth, _items.Length * 2));
        AsSpan().CopyTo(bigger);
        Return(_items);
        _items = bigger;
    }

    private static void Return(T[] items)
    {
        if (items.Length > 0)
        {
            ArrayPool<T>.Shared.Return(items, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }
}
