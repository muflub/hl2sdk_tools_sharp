using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// <c>vbsp</c>'s plane table: <c>mapplanes</c>, <c>nummapplanes</c> and
/// <c>planehash</c> from <c>CMapFile</c> (<c>utils/vbsp/vbsp.h:296-299</c>),
/// with the three functions that maintain them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A plane's INDEX is the output, so insertion order is the file format.</b>
/// Brush sides, BSP nodes and LUMP_PLANES all store plane NUMBERS; nothing
/// stores a plane by value. Sorting this table, deduplicating it by value, or
/// reordering it in any way renumbers every reference in the map even when the
/// geometry is identical down to the last bit. So this type appends and never
/// moves anything, has no <c>Sort</c>, no <c>Remove</c>, and no comparer.
/// </para>
/// <para>
/// The dedup that DOES happen is <see cref="Find"/>'s, and it is not a
/// value-equality dedup: <see cref="Plane.Equal"/> is an epsilon match that is
/// not transitive, and the hash it is driven from only searches three buckets.
/// Which planes collapse together therefore depends on the order they are
/// offered in — a second reason the order is part of the answer rather than an
/// implementation detail.
/// </para>
/// <para>
/// Planes are stored in PAIRS: every <see cref="Create"/> appends a plane and
/// its opposite, so <c>index ^ 1</c> is always the back side. The BSP builder
/// relies on that (stock spells it <c>mapplanes[s-&gt;planenum^1]</c>,
/// <c>map.cpp:639</c>), which is the third reason nothing may be inserted
/// between the halves of a pair.
/// </para>
/// <para>
/// One table per <see cref="MapFile"/>, never a static: a <c>func_instance</c>
/// is loaded into its own <see cref="MapFile"/> with its own table and then
/// merged plane by plane through <see cref="Find"/>
/// (<c>CMapFile::MergePlanes</c>, <c>map.cpp:2106</c>), which only works if the
/// two tables are separate objects.
/// </para>
/// </remarks>
public sealed class PlaneTable
{
    /// <summary>
    /// How many planes the BSP format can hold: <c>MAX_MAP_PLANES</c>,
    /// <c>public/bspfile.h:74</c>.
    /// </summary>
    public const int MaxMapPlanes = 65536;

    private readonly List<Plane> _planes = [];

    // Stock stores dplane_t::type in the table and writes it to LUMP_PLANES,
    // rather than recomputing it. It is kept here for the same reason: the
    // field is wire format. See TypeOf for why storing and recomputing agree.
    private readonly List<PlaneType> _types = [];

    // planehash, as an index chain rather than a pointer chain. _hashHead[b] is
    // the most recently added plane in bucket b, or -1; _hashNext[i] is the
    // plane added to i's bucket before it, or -1. That reproduces stock's LIFO
    // chain exactly (map.cpp:199-200 pushes at the head), which matters because
    // Find returns the FIRST epsilon match it walks into.
    private readonly int[] _hashHead = new int[Plane.PlaneHashes];
    private readonly List<int> _hashNext = [];

    /// <summary>Creates an empty table.</summary>
    public PlaneTable() => Array.Fill(_hashHead, -1);

    /// <summary>
    /// How many planes are in the table: stock's <c>nummapplanes</c>.
    /// </summary>
    /// <remarks>
    /// Always even. <see cref="Create"/> appends two at a time and nothing ever
    /// appends one.
    /// </remarks>
    public int Count => _planes.Count;

    /// <summary>The plane at an index.</summary>
    /// <param name="index">The plane number.</param>
    /// <returns>The plane.</returns>
    public Plane this[int index] => _planes[index];

    /// <summary>
    /// Every plane, in insertion order, for a caller that writes LUMP_PLANES.
    /// </summary>
    /// <remarks>
    /// A read-only view over the live list rather than a copy: a caller that
    /// wanted a different order would have to say so, in its own code, where it
    /// is visible.
    /// </remarks>
    public IReadOnlyList<Plane> Planes => _planes;

    /// <summary>
    /// The classification stored alongside a plane, as LUMP_PLANES writes it.
    /// </summary>
    /// <param name="index">The plane number.</param>
    /// <returns>The stored <c>dplane_t.type</c>.</returns>
    /// <remarks>
    /// <c>CreateNewFloatPlane</c> computes this once for the front half and
    /// assigns the same value to both halves (<c>map.cpp:221</c>) — before the
    /// pair may be swapped, so both halves carry the front half's answer either
    /// way. That is not a discrepancy: <c>PlaneTypeForNormal</c> is invariant
    /// under negation, because its exact tests are <c>== 1 || == -1</c> and its
    /// tie-breaks are on absolute values. Storing and recomputing therefore
    /// agree, and a fact asserts it rather than this comment being the argument.
    /// </remarks>
    public PlaneType TypeOf(int index) => _types[index];

    /// <summary>
    /// Finds a plane, or appends it and its opposite: <c>FindFloatPlane</c>,
    /// <c>utils/vbsp/map.cpp:351</c>.
    /// </summary>
    /// <param name="normal">The plane normal. Snapped before the lookup.</param>
    /// <param name="dist">The plane distance. Snapped before the lookup.</param>
    /// <returns>The plane's index in this table.</returns>
    /// <exception cref="MapCompileException">
    /// The normal is shorter than 0.5, or the table is full.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Stock takes the normal by non-const reference and lets
    /// <c>SnapPlane</c> write the snapped value back into the caller's
    /// variable; two call sites pass a reference to a plane already IN a table
    /// (<c>map.cpp:1576</c> and <c>map.cpp:2111</c>), so stock snaps table
    /// entries in place as a side effect. That is unobservable — a table entry
    /// was snapped when it was created and snapping is idempotent — so this
    /// takes the normal by value and the difference cannot be seen.
    /// </para>
    /// <para>
    /// The search covers the plane's own hash bucket and the two either side,
    /// in the order <c>bucket-1</c>, <c>bucket</c>, <c>bucket+1</c>, and returns
    /// the first epsilon match. Both the bucket order and the within-bucket
    /// order (most recently added first) decide WHICH of several matching
    /// planes is returned when the tolerant match hits more than one, so both
    /// are reproduced rather than approximated.
    /// </para>
    /// </remarks>
    public int Find(Vec3 normal, float dist)
    {
        Plane snapped = new Plane(normal, dist).Snapped();

        int bucket = Plane.HashBucket(snapped.Dist);

        for (int offset = -1; offset <= 1; offset++)
        {
            int h = (bucket + offset) & (Plane.PlaneHashes - 1);

            for (int p = _hashHead[h]; p != -1; p = _hashNext[p])
            {
                if (Plane.Equal(
                        _planes[p],
                        snapped,
                        GeometryEpsilons.RenderNormalEpsilonFloat,
                        GeometryEpsilons.RenderDistEpsilon))
                {
                    return p;
                }
            }
        }

        return Create(snapped.Normal, snapped.Dist);
    }

    /// <summary>
    /// Finds the plane through three points, or appends it:
    /// <c>CMapFile::PlaneFromPoints</c>, <c>utils/vbsp/map.cpp:384</c>.
    /// </summary>
    /// <param name="p0">The first point.</param>
    /// <param name="p1">The second point, the corner the edges meet at.</param>
    /// <param name="p2">The third point.</param>
    /// <param name="snapAxialPlanes">
    /// Stock's <c>g_snapAxialPlanes</c> (<c>-snapaxial</c>).
    /// </param>
    /// <returns>The plane's index in this table.</returns>
    /// <exception cref="MapCompileException">
    /// The three points are colinear enough that the normal is shorter than
    /// 0.5, or the table is full.
    /// </exception>
    /// <remarks>
    /// The derivation and the snapping live on <see cref="Plane"/>; the
    /// INSERTION is the half stock keeps here, and it is the half that decides
    /// the index this returns.
    /// </remarks>
    public int FromPoints(Vec3 p0, Vec3 p1, Vec3 p2, bool snapAxialPlanes)
    {
        Plane plane = Plane.FromPoints(p0, p1, p2)
            .SnappedThroughPoints(p0, p1, p2, snapAxialPlanes);

        // Stock reaches FindFloatPlane, whose own SnapPlane then runs a second
        // time over an already-snapped plane. Idempotent, so Find's snap is the
        // same call and is not skipped here.
        return Find(plane.Normal, plane.Dist);
    }

    /// <summary>
    /// Appends a plane and its opposite: <c>CreateNewFloatPlane</c>,
    /// <c>utils/vbsp/map.cpp:208</c>.
    /// </summary>
    /// <param name="normal">The plane normal, already snapped.</param>
    /// <param name="dist">The plane distance, already snapped.</param>
    /// <returns>
    /// The index of the plane that was ASKED for, which is the second of the
    /// two slots when the pair was flipped.
    /// </returns>
    /// <exception cref="MapCompileException">
    /// The normal is shorter than 0.5, or the table is full.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Public because <c>MergePlanes</c>-style callers and tests need to be
    /// able to append without a lookup, and because hiding it would not make
    /// the ordering rule any less load-bearing.
    /// </para>
    /// <para>
    /// The opposite normal is <c>0 - x</c> per component and not <c>-x</c>:
    /// <c>VectorSubtract (vec3_origin, normal, (p+1)-&gt;normal)</c>,
    /// <c>map.cpp:223</c>. For an axial plane the two zero components stay
    /// <c>+0.0f</c> where unary negation would make them <c>-0.0f</c>. Same
    /// number, different bytes in LUMP_PLANES. <see cref="Plane.Flipped"/>
    /// carries that spelling.
    /// </para>
    /// <para>
    /// The flip that follows is stock's "always put axial planes facing
    /// positive first" (<c>map.cpp:228</c>), and its test is on the plane's
    /// stored type being axial AND any component being negative. Because the
    /// opposite's zeroes are <c>+0.0f</c> and <c>+0.0f &lt; 0</c> is false, only
    /// the genuinely negative axis triggers it.
    /// </para>
    /// </remarks>
    public int Create(Vec3 normal, float dist)
    {
        if (normal.Length() < 0.5f)
        {
            throw new MapCompileException("FloatPlane: bad normal");
        }

        if (Count + 2 > MaxMapPlanes)
        {
            throw new MapCompileException("MAX_MAP_PLANES");
        }

        Plane front = new(normal, dist);
        Plane back = front.Flipped;

        // One classification for both halves, from the FRONT half's normal, as
        // map.cpp:221 does it -- and before the swap below, as map.cpp does.
        PlaneType type = front.Type;

        int first = _planes.Count;
        _planes.Add(front);
        _types.Add(type);
        _hashNext.Add(-1);

        _planes.Add(back);
        _types.Add(type);
        _hashNext.Add(-1);

        int result = first;

        if (type < PlaneType.AnyX &&
            (front.Normal.X < 0f || front.Normal.Y < 0f || front.Normal.Z < 0f))
        {
            (_planes[first], _planes[first + 1]) = (_planes[first + 1], _planes[first]);

            // The plane that was asked for now lives in the second slot. Stock
            // returns nummapplanes - 1 here and nummapplanes - 2 otherwise
            // (map.cpp:240 and :246); the pair itself does not move.
            result = first + 1;
        }

        // Both halves, front slot first, AFTER any swap -- map.cpp:238-239 and
        // :244-245. They share a bucket (|dist| is the same for a plane and its
        // opposite), so the second push leaves slot first+1 at the head of the
        // chain and slot first behind it. Find walks the chain head first, so
        // that order decides which half a tolerant match lands on.
        AddToHash(first);
        AddToHash(first + 1);

        return result;
    }

    private void AddToHash(int index)
    {
        int bucket = Plane.HashBucket(_planes[index].Dist);
        _hashNext[index] = _hashHead[bucket];
        _hashHead[bucket] = index;
    }
}
