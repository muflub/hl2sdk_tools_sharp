//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapTools.Rad.Bounce;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// A scratch pool built to catch every way pooled scratch goes wrong, for the
/// facts about the batches and leaf-ambient scratch that rent from one.
/// </summary>
/// <remarks>
/// <para>
/// It RECYCLES, like the shared pool: a returned array is handed to the next
/// renter that fits, so reuse is actually exercised -- the next leaf, the
/// next batch, the next build, or another worker at the same time.
/// </para>
/// <para>
/// It POISONS: an array is filled with 0xFF bytes when it is returned and
/// again when it is rented (a float or a <c>Vec3</c> component reads NaN, a
/// bool reads true, a ray is garbage). Code that reads past what it wrote,
/// or keeps using an array after giving it back, then produces a different
/// answer rather than a lucky one. Arrays are rented a few elements longer
/// than asked, as the shared pool's power-of-two buckets are, so a slice
/// taken from the array's length rather than the asked length shows too.
/// </para>
/// <para>
/// It AUDITS: it knows which arrays are out. Returning one that is not out
/// (twice, or one it never lent) is counted as a bad return, and a rental can
/// be made to fail, for the paths that must give back what they already hold.
/// </para>
/// </remarks>
internal sealed class RecyclingScratchPool : IScratchArrayPool
{
    private readonly object _sync = new();
    private readonly HashSet<Array> _out = new(ReferenceEqualityComparer.Instance);
    private readonly List<Array> _free = [];
    private readonly Dictionary<Type, int> _rentedByType = [];

    /// <summary>When set, the rental with this 1-based number throws.</summary>
    public int FailRentNumber { get; set; }

    /// <summary>Every rental so far.</summary>
    public int Rented { get; private set; }

    /// <summary>Every good return so far.</summary>
    public int Returned { get; private set; }

    /// <summary>Returns of arrays that were not out: double returns and strangers.</summary>
    public int BadReturns { get; private set; }

    /// <summary>Rentals served from a returned array rather than a new one.</summary>
    public int Recycled { get; private set; }

    /// <summary>The most arrays out at once.</summary>
    public int PeakOutstanding { get; private set; }

    /// <summary>How many arrays of <typeparamref name="T"/> have been rented.</summary>
    public int RentedOf<T>()
    {
        lock (_sync)
        {
            return _rentedByType.GetValueOrDefault(typeof(T[]));
        }
    }

    /// <summary>Arrays rented and not yet returned.</summary>
    public int Outstanding
    {
        get
        {
            lock (_sync)
            {
                return _out.Count;
            }
        }
    }

    /// <inheritdoc/>
    public T[] Rent<T>(int minimumLength)
    {
        T[]? array = null;
        lock (_sync)
        {
            Rented++;
            _rentedByType[typeof(T[])] = _rentedByType.GetValueOrDefault(typeof(T[])) + 1;
            if (Rented == FailRentNumber)
            {
                throw new OutOfMemoryException("the pool was told to fail this rental");
            }

            for (int i = 0; i < _free.Count; i++)
            {
                if (_free[i] is T[] candidate && candidate.Length >= minimumLength)
                {
                    _free.RemoveAt(i);
                    array = candidate;
                    Recycled++;
                    break;
                }
            }

            array ??= new T[minimumLength + 5];
            _out.Add(array);
            PeakOutstanding = Math.Max(PeakOutstanding, _out.Count);
        }

        Poison(array);
        return array;
    }

    /// <inheritdoc/>
    public void Return<T>(T[] array)
    {
        lock (_sync)
        {
            if (!_out.Remove(array))
            {
                BadReturns++;
                return;
            }

            Returned++;
        }

        // Poisoned outside the lock but before it can be lent again: an
        // array still in use by whoever returned it reads garbage from now.
        Poison(array);
        lock (_sync)
        {
            _free.Add(array);
        }
    }

    /// <summary>Fills an array's bytes with 0xFF, or clears a reference array.</summary>
    private static void Poison<T>(T[] array)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(array);
            return;
        }

        MemoryMarshal.CreateSpan(
            ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array)),
            array.Length * Unsafe.SizeOf<T>()).Fill(0xFF);
    }
}
