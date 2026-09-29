//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// The scratch pool of one vrad compile: arrays handed back by one worker or
/// stage are handed out again to the next that fits, and every array the
/// pool still holds is dropped when the compile ends.
/// </summary>
/// <remarks>
/// <para>
/// WHY NOT <see cref="System.Buffers.ArrayPool{T}.Shared"/>. vrad's scratch
/// used to come from the process's shared pool, and a profile of 2fort showed
/// it handing out far more large arrays than a compile ever holds at once.
/// Three properties of the shared pool were behind that, and each is wrong
/// for this workload:
/// </para>
/// <list type="bullet">
/// <item><description>
/// It rounds every request up to a power of two. A leaf-ambient batch of
/// 217,599 segments got a 262,144-ray array, 20 % more than it asked for, on
/// every worker; the chunk buffers of the transfer build fell just under a
/// bucket and were lucky. Here a fresh array is exactly as long as asked.
/// </description></item>
/// <item><description>
/// It keeps at most a handful of arrays per size per core, and a stage hands
/// its workers' arrays back from ONE thread when it ends, so on a machine
/// with many workers most of them were dropped on the floor and the next
/// stage allocated them again. Here nothing handed back is refused.
/// </description></item>
/// <item><description>
/// It is process-wide and keeps what it holds until a full collection finds
/// it idle for a minute: a long-lived service kept one map's worth of large
/// scratch alive after that compile ended, however small the next map. Here
/// the pool belongs to the compile and is dropped with it.
/// </description></item>
/// </list>
/// <para>
/// BEST FIT, ANY SIZE. A rental takes the SHORTEST idle array of the type
/// that is long enough, so a large array stays for a large request while a
/// smaller one fits. Every renter already treats the array as "at least" the
/// length it asked for and may use what is beyond; nothing may read what it
/// did not write (an idle array holds whatever its last renter left).
/// </para>
/// <para>
/// TRIMMED BETWEEN STAGES. Idle arrays cost resident memory, and scratch
/// shapes change from stage to stage: the face-lighting workers' ray logs
/// are no use to the transfer build, whose chunk buffer is no use to the
/// prop and leaf-ambient batches. The compile calls <see cref="Trim"/> where
/// one stage's shapes stop being useful to the next, and the collector gets
/// them back there instead of the pool carrying them to the end.
/// </para>
/// <para>
/// BOUNDED BY THE COMPILE. The pool only ever holds arrays the compile itself
/// allocated and handed back, so it cannot grow past what the compile's
/// scratch needed; <see cref="Dispose"/> drops everything and refuses any
/// later rental, and an array returned after that is simply let go. No state
/// is shared with another compile, so two compiles in one process run over
/// two pools that never see each other's arrays.
/// </para>
/// <para>
/// Thread-safe: the workers of a stage rent and return concurrently. The
/// lock is taken once per rental or return, and rentals happen when a
/// worker's storage grows or a stage starts -- a few per worker per stage --
/// so a plain list scanned under the lock is enough.
/// </para>
/// </remarks>
internal sealed class CompileScratchPool : IScratchArrayPool, IDisposable
{
    private readonly object _sync = new();
    private readonly List<Idle> _idle = [];
    private long _idleBytes;
    private long _allocatedBytes;
    private int _allocations;
    private int _reuses;
    private int _outstanding;
    private bool _disposed;

    /// <summary>Bytes of every array this pool has allocated, over its life.</summary>
    public long AllocatedBytes
    {
        get
        {
            lock (_sync)
            {
                return _allocatedBytes;
            }
        }
    }

    /// <summary>How many arrays this pool has allocated, over its life.</summary>
    public int Allocations
    {
        get
        {
            lock (_sync)
            {
                return _allocations;
            }
        }
    }

    /// <summary>How many rentals were served from an idle array rather than a new one.</summary>
    public int Reuses
    {
        get
        {
            lock (_sync)
            {
                return _reuses;
            }
        }
    }

    /// <summary>Arrays rented and not yet returned.</summary>
    public int Outstanding
    {
        get
        {
            lock (_sync)
            {
                return _outstanding;
            }
        }
    }

    /// <summary>Bytes of the arrays the pool holds idle, waiting for a renter.</summary>
    public long IdleBytes
    {
        get
        {
            lock (_sync)
            {
                return _idleBytes;
            }
        }
    }

    /// <summary>How many arrays the pool holds idle.</summary>
    public int IdleArrays
    {
        get
        {
            lock (_sync)
            {
                return _idle.Count;
            }
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The compile has ended.</exception>
    /// <remarks>
    /// A zero-length request is served with an empty array and never counted:
    /// an empty array holds nothing to give back.
    /// </remarks>
    public T[] Rent<T>(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0)
        {
            return [];
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int best = -1;
            int bestLength = int.MaxValue;
            for (int i = 0; i < _idle.Count; i++)
            {
                if (_idle[i].Array is T[] candidate && candidate.Length >= minimumLength && candidate.Length < bestLength)
                {
                    best = i;
                    bestLength = candidate.Length;
                }
            }

            _outstanding++;
            if (best >= 0)
            {
                Idle found = _idle[best];
                _idle.RemoveAt(best);
                _idleBytes -= found.Bytes;
                _reuses++;
                return (T[])found.Array;
            }

            // Counted before the allocation so the totals never miss an
            // array a renter holds; an allocation that throws undoes it.
            _allocations++;
            _allocatedBytes += Bytes<T>(minimumLength);
        }

        try
        {
            // Uninitialised: the contract already says a rented array holds
            // unspecified data, and a large one would otherwise be zeroed
            // only to be overwritten.
            return GC.AllocateUninitializedArray<T>(minimumLength);
        }
        catch
        {
            lock (_sync)
            {
                _outstanding--;
                _allocations--;
                _allocatedBytes -= Bytes<T>(minimumLength);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// An empty array is ignored. After <see cref="Dispose"/> the array is
    /// let go rather than kept: a batch that outlived its compile must not
    /// hand anything to a pool nobody will empty.
    /// </remarks>
    public void Return<T>(T[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            _outstanding--;
            if (_disposed)
            {
                return;
            }

            long bytes = Bytes<T>(array.Length);
            _idle.Add(new Idle(array, bytes));
            _idleBytes += bytes;
        }
    }

    /// <summary>
    /// Lets go of every idle array, for the collector; rented arrays are not
    /// affected and may still be returned (and kept) afterwards.
    /// </summary>
    public void Trim()
    {
        lock (_sync)
        {
            _idle.Clear();
            _idleBytes = 0;
        }
    }

    /// <summary>
    /// Ends the pool with the compile: every idle array is let go, later
    /// rentals throw and later returns are let go. Safe to call more than
    /// once.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _idle.Clear();
            _idleBytes = 0;
        }
    }

    private static long Bytes<T>(int length) => (long)length * Unsafe.SizeOf<T>();

    /// <summary>An idle array and its size in bytes.</summary>
    private readonly record struct Idle(Array Array, long Bytes);
}

/// <summary>
/// A scratch "pool" that pools nothing: every rental is a new array and every
/// return lets the array go.
/// </summary>
/// <remarks>
/// For scratch made outside a compile -- a one-off <see cref="TestLineBatch"/>
/// a host or a fact makes on its own -- where there is no compile to own a
/// <see cref="CompileScratchPool"/> and so nobody to empty one. It keeps
/// nothing alive, so it needs no disposal and cannot leak.
/// </remarks>
internal sealed class UnpooledScratch : IScratchArrayPool
{
    /// <inheritdoc/>
    public T[] Rent<T>(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        return minimumLength == 0 ? [] : GC.AllocateUninitializedArray<T>(minimumLength);
    }

    /// <inheritdoc/>
    public void Return<T>(T[] array) => ArgumentNullException.ThrowIfNull(array);
}
