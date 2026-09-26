//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Geometry;

/// <summary>
/// A convex polygon held in a <see cref="WindingArena"/>: an offset, a live
/// point count and the capacity that was reserved for it.
/// </summary>
/// <remarks>
/// <para>
/// A handle, not the polygon. The points live in the arena's slab and are
/// reached with <see cref="WindingArena.Points"/>; this struct is three
/// <see cref="int"/>s and is meant to be copied freely.
/// </para>
/// <para>
/// Stock's <c>winding_t</c> is a heap node
/// with a pointer to a separate <c>calloc</c>'d point array and a
/// <c>next</c> pointer threading it onto a free list — two allocations per
/// winding, and a global <c>CRITICAL_SECTION</c> taken on every alloc and every
/// Free. vbsp and vvis create and destroy
/// windings in the millions; a class per winding here would put the whole port
/// on the GC, and a shared free list would put it back behind stock's lock.
/// Hence an arena per worker and a value handle into it.
/// </para>
/// <para>
/// <b>A handle outlives nothing.</b> Freeing a winding and then reading it is
/// exactly stock's <c>0xdeaddead</c> bug, and the arena detects it the same way
/// — see <see cref="WindingArena.Free"/>.
/// </para>
/// </remarks>
public readonly struct Winding : IEquatable<Winding>
{
    internal readonly int Offset;

    internal Winding(int offset, int count, int capacity)
    {
        Offset = offset;
        Count = count;
        Capacity = capacity;
    }

    /// <summary>
    /// How many points the winding currently has: stock's <c>numpoints</c>.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// How many points were reserved for it: stock's <c>maxpoints</c>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Count"/> because the clipper reserves
    /// <c>in-&gt;numpoints + 4</c> and then fills in fewer — stock's comment at
    /// Explains it cannot use the exact count "because of
    /// fp grouping errors". The capacity is also what the arena's free list is
    /// keyed on, exactly as stock keys <c>winding_pool</c> on
    /// <c>maxpoints</c>.
    /// </remarks>
    public int Capacity { get; }

    /// <summary>
    /// True for the absence of a winding: stock's <c>NULL</c> out-parameter.
    /// </summary>
    /// <remarks>
    /// A real winding always has a capacity of at least one, so
    /// <c>default(Winding)</c> is null and cannot be confused with a valid
    /// handle to the first slot of the arena.
    /// </remarks>
    public bool IsNull => Capacity == 0;

    /// <summary>The null winding.</summary>
    public static Winding Null => default;

    /// <inheritdoc />
    public bool Equals(Winding other) =>
        Offset == other.Offset && Count == other.Count && Capacity == other.Capacity;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Winding other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Offset, Count, Capacity);

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns>True when they name the same winding.</returns>
    public static bool operator ==(Winding left, Winding right) => left.Equals(right);

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns>True when they differ.</returns>
    public static bool operator !=(Winding left, Winding right) => !left.Equals(right);
}
