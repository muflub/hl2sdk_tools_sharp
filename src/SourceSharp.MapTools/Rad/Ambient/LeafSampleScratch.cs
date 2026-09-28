//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Bounce;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// One leaf-ambient worker's reusable storage: the sample positions and ray
/// cubes of every leaf in its current batch, the boundary-plane list and the
/// sample list each leaf rebuilds.
/// </summary>
/// <remarks>
/// <para>
/// WHY IT EXISTS. Every leaf used to allocate its own position and cube
/// arrays (up to 128 samples and 768 colours), its own plane list and its own
/// sample list, and a map has tens of thousands of leaves. A profile of vrad
/// on ctf_2fort put those at about 20 MB of the leaf-ambient stage's
/// allocations, next to the 74 MB of batch growth that
/// <see cref="TestLineBatch"/> now rents. A worker's leaves run one after
/// another on one thread, so one set of buffers serves all of them.
/// </para>
/// <para>
/// A BATCH-LONG ARENA, not a per-leaf buffer: the traced path plans a whole
/// batch of leaves before any of them resolves, so each leaf
/// <see cref="Reserve"/>s its own run of samples and keeps the offset. Growing
/// copies every sample reserved so far, which keeps earlier offsets valid.
/// <see cref="Reset"/> empties it when the worker starts its next batch.
/// </para>
/// <para>
/// The arrays are rented from an <see cref="IScratchArrayPool"/> and may hold
/// an earlier renter's data. Nothing reads past what was written: a leaf
/// writes every position and every cube of its reserved run before its
/// visibility or its sample list reads them, and a slice never reaches
/// beyond the run. <see cref="Dispose"/> gives the arrays back, once; the
/// stage disposes each worker's scratch however it ends.
/// </para>
/// <para>
/// ONE PER WORKER, never shared between threads: nothing in it is
/// synchronised, and two workers writing the same run would mix leaves.
/// </para>
/// </remarks>
internal sealed class LeafSampleScratch : IDisposable
{
    /// <summary>The capacity of the first rental, in samples: one full leaf.</summary>
    private const int InitialSamples = LeafAmbientBuilder.MaxSampleCount;

    private readonly IScratchArrayPool _pool;
    private Vec3[] _positions = [];
    private Vec3[] _cubes = [];
    private int _capacity;
    private int _used;
    private bool _disposed;

    /// <summary>Makes empty scratch over a pool; nothing is rented until the first reservation.</summary>
    /// <param name="pool">Where the arrays come from and go back to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is null.</exception>
    public LeafSampleScratch(IScratchArrayPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _pool = pool;
    }

    /// <summary>A leaf's boundary planes; each leaf's gather clears it first.</summary>
    public List<LeafPlane> Planes { get; } = [];

    /// <summary>A leaf's sample list while it is built and compressed; copied out before the next leaf.</summary>
    public List<AmbientSample> Samples { get; } = [];

    /// <summary>How many samples the current batch has reserved.</summary>
    public int Used => _used;

    /// <summary>How many samples fit before the arena grows.</summary>
    public int Capacity => _capacity;

    /// <summary>Reserves a run of samples for one leaf.</summary>
    /// <param name="samples">How many.</param>
    /// <returns>The run's first sample, for <see cref="Positions"/> and <see cref="Cubes"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="samples"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The scratch was disposed.</exception>
    public int Reserve(int samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samples);
        ObjectDisposedException.ThrowIf(_disposed, this);
        int offset = _used;
        int needed = offset + samples;
        if (needed > _capacity)
        {
            Grow(needed);
        }

        _used = needed;
        return offset;
    }

    /// <summary>The positions of a reserved run.</summary>
    /// <param name="offset">What <see cref="Reserve"/> returned, or a sample inside the run.</param>
    /// <param name="count">How many samples.</param>
    /// <returns>A view of the storage, valid until the next reservation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is not inside what has been reserved.</exception>
    public Span<Vec3> Positions(int offset, int count)
    {
        CheckRange(offset, count);
        return _positions.AsSpan(offset, count);
    }

    /// <summary>The ray cubes of a reserved run, six colours per sample.</summary>
    /// <param name="offset">What <see cref="Reserve"/> returned, or a sample inside the run.</param>
    /// <param name="count">How many samples.</param>
    /// <returns>A view of the storage, valid until the next reservation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is not inside what has been reserved.</exception>
    public Span<Vec3> Cubes(int offset, int count)
    {
        CheckRange(offset, count);
        return _cubes.AsSpan(offset * AmbientCube.Sides, count * AmbientCube.Sides);
    }

    /// <summary>Forgets every reservation, keeping the storage, for the worker's next batch.</summary>
    public void Reset() => _used = 0;

    /// <summary>Gives the arrays back to the pool, once. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReturnArrays();
        _used = 0;
        Planes.Clear();
        Samples.Clear();
    }

    /// <summary>
    /// Doubles the arena until <paramref name="needed"/> samples fit: new
    /// arrays rented, the reserved samples copied across, the old ones
    /// returned.
    /// </summary>
    /// <remarks>
    /// Both arrays are rented before anything is swapped, so a rental that
    /// throws leaves the scratch as it was and gives back what it had rented.
    /// </remarks>
    private void Grow(int needed)
    {
        int size = Math.Max(_capacity, InitialSamples);
        while (size < needed)
        {
            size *= 2;
        }

        Vec3[] positions = _pool.Rent<Vec3>(size);
        Vec3[] cubes;
        try
        {
            cubes = _pool.Rent<Vec3>(size * AmbientCube.Sides);
        }
        catch
        {
            _pool.Return(positions);
            throw;
        }

        _positions.AsSpan(0, _used).CopyTo(positions);
        _cubes.AsSpan(0, _used * AmbientCube.Sides).CopyTo(cubes);
        ReturnArrays();
        _positions = positions;
        _cubes = cubes;
        _capacity = size;
    }

    private void ReturnArrays()
    {
        if (_capacity == 0)
        {
            return;
        }

        _pool.Return(_positions);
        _pool.Return(_cubes);
        _positions = [];
        _cubes = [];
        _capacity = 0;
    }

    private void CheckRange(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > _used)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), $"samples {offset}..{offset + count} are not inside the {_used} reserved");
        }
    }
}
