//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Geometry;

/// <summary>
/// A pool of winding storage and the polygon operations that work on it: the
/// Port of the reference implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not thread safe, and that is the point.</b> Stock serialises every
/// <c>AllocWinding</c> and every <c>FreeWinding</c> on one global
/// <c>CRITICAL_SECTION</c> shared with the
/// work dispatcher, so the two hottest operations in vbsp and vvis contend with
/// each other and with progress printing. Here each worker owns an arena and
/// there is no lock at all. <see cref="Parallel.WorkQueue"/> hands one out per
/// worker as scratch.
/// </para>
/// <para>
/// <b>Spans are invalidated by allocation.</b> <see cref="Points"/> and
/// <see cref="Storage"/> hand out a window onto the backing slab, and the slab
/// is grown by copying. Any <see cref="Alloc"/>, <see cref="Copy"/> or
/// operation that allocates may therefore invalidate a span taken before it.
/// Take spans after the last allocation, as every method below does.
/// </para>
/// <para>
/// <b>The slab is a list of segments.</b> An arena that keeps its windings --
/// vbsp's, or the patch windings vrad keeps for the whole bounce -- can hold
/// millions of points, and growing one array by doubling made every array it
/// outgrew garbage on the large-object heap: about as much again as the final
/// slab, copied point by point on the way. So the first segment starts at the
/// requested capacity and doubles, as before, but only up to
/// <see cref="SegmentLength"/> points; after that the arena adds whole new
/// segments and never copies a full one. A winding never straddles two
/// segments (the unused tail of a segment, at most a winding's worth, is
/// skipped), so a winding's storage is still one span. Only the first
/// segment's growth moves points, which is why the rule above still holds.
/// </para>
/// <para>
/// The arithmetic is a faithful port. The operand order, the epsilons and the
/// float-versus-double width of every comparison match the reference build;
/// where they are surprising, the method's remarks cite the line. The one
/// deliberate divergence is normalisation, and
/// <see cref="Plane"/> documents it.
/// </para>
/// </remarks>
public sealed class WindingArena
{
    /// <summary>
    /// The largest number of points a winding may end up with.
    /// </summary>
    /// <remarks>
    /// <c>#define MAX_POINTS_ON_WINDING 64</c>. The clipper checks its results
    /// against this and stock calls <c>Error()</c> when a winding exceeds it,
    /// so it is a hard limit on output rather than a hint.
    /// </remarks>
    public const int MaxPointsOnWinding = 64;

    // Room for MaxPointsOnWinding + 4 (the clipper's reservation) plus the one
    // wrap-around slot the side/dist arrays need, plus slack.
    //
    // Stock sizes these `dists[MAX_POINTS_ON_WINDING+4]`
    // -- 68 entries, indices 0..67 -- and then writes
    // `dists[i]` with i == in->numpoints after the loop. A
    // winding that a previous clip left at 68 points therefore writes one past
    // the end. That is a latent overflow in stock; the port does not reproduce
    // it, it refuses the input instead. See ClipEpsilon.
    private const int SideBufferLength = MaxPointsOnWinding + 16;

    /// <summary>log2 of the points in a full segment.</summary>
    internal const int SegmentShift = 16;

    /// <summary>
    /// The points in a full segment, and so the most one winding may reserve.
    /// </summary>
    /// <remarks>
    /// 65536 points is 768 KB, large enough that a segment is a rare
    /// allocation and three orders of magnitude above the largest winding
    /// the clipper can build (<see cref="MaxPointsOnWinding"/> plus its
    /// reservation).
    /// </remarks>
    public const int SegmentLength = 1 << SegmentShift;

    private const int SegmentMask = SegmentLength - 1;

    private Vec3[][] _slab;
    private bool[][] _live;
    private int _used;
    private Stack<int>[] _free;

    /// <summary>Creates an arena with a default starting capacity.</summary>
    public WindingArena()
        : this(4096)
    {
    }

    /// <summary>
    /// The compliance this arena's polylib operations run under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The arena is the carrier because the quirk is the arena's.</b>
    /// <see cref="BaseWindingForPlane"/> is a method on this class and is the
    /// site of <see cref="StockQuirk.BaseWindingNormalise"/>, so the policy has
    /// to reach it somehow; every other consumer of the polylib already holds
    /// an arena, which is what makes this a one-property plumb rather than a
    /// parameter threaded through six call chains.
    /// </para>
    /// <para>
    /// Defaults to <see cref="ComplianceOptions.Correct"/>, so an arena built
    /// in a unit test without an opinion gets the right answer rather than
    /// stock's.
    /// </para>
    /// </remarks>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>
    /// Creates an arena with room for a given number of points before its first
    /// growth.
    /// </summary>
    /// <param name="initialPointCapacity">How many points to reserve.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="initialPointCapacity"/> is negative.
    /// </exception>
    public WindingArena(int initialPointCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialPointCapacity);

        int first = Math.Min(initialPointCapacity, SegmentLength);
        _slab = [new Vec3[first]];
        _live = [new bool[first]];
        _free = new Stack<int>[MaxPointsOnWinding + 8];
    }

    /// <summary>How many windings are allocated and not yet freed.</summary>
    /// <remarks>
    /// Stock's <c>c_active_windings</c>, which it only
    /// maintains when <c>numthreads == 1</c> because the counters are, in its
    /// own words, "an awefull coherence problem". Per-worker arenas make that
    /// problem go away, so these are always accurate here.
    /// </remarks>
    public int ActiveWindings { get; private set; }

    /// <summary>The high-water mark of <see cref="ActiveWindings"/>.</summary>
    /// <remarks>Stock's <c>c_peak_windings</c>.</remarks>
    public int PeakWindings { get; private set; }

    /// <summary>How many times <see cref="Alloc"/> has been called.</summary>
    /// <remarks>Stock's <c>c_winding_allocs</c>.</remarks>
    public long TotalAllocations { get; private set; }

    /// <summary>
    /// How many of those were served from the free list rather than by growing
    /// the slab.
    /// </summary>
    /// <remarks>
    /// Not a stock counter. It is here because the whole justification for the
    /// arena is that windings are recycled rather than allocated, and a claim
    /// like that should be measurable rather than asserted.
    /// </remarks>
    public long RecycledAllocations { get; private set; }

    /// <summary>How many points the slab currently holds.</summary>
    public int SlabCapacity
    {
        get
        {
            int capacity = 0;
            foreach (Vec3[]? segment in _slab)
            {
                capacity += segment?.Length ?? 0;
            }

            return capacity;
        }
    }

    /// <summary>
    /// Forgets every winding, keeping the storage, so the arena can be used
    /// again as though it were new.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an arena is reused at all.</b> The parallel tree build gives
    /// every fork an arena of its own, a couple of hundred of them in a 2fort
    /// compile, and a new arena grows its first segment by doubling: every
    /// array it outgrows is garbage, and from 8192 points on each one is a
    /// large-object-heap array. Those were the compile's largest source of
    /// large-object garbage (about 145 MB of 345 MB on 2fort), and
    /// large-object allocation is what triggers the gen2 collections that
    /// park every compile thread. A <see cref="WindingArenaPool"/> hands a
    /// finished fork's arena to the next fork instead, which finds its first
    /// segment already grown.
    /// </para>
    /// <para>
    /// <b>A reset arena is indistinguishable from a new one.</b> Every
    /// handle it gives out afterwards has the offset a new arena would have
    /// given: allocation starts again at offset zero, the free lists are
    /// empty, and where the next reservation lands never depends on how
    /// large the first segment already is, only on the points reserved
    /// before it (see <see cref="EnsureRoom"/>). The counters start again
    /// from zero too. So nothing a compile computes can tell a pooled arena
    /// from a fresh one; only the allocations it no longer makes differ.
    /// </para>
    /// <para>
    /// Every handle taken before the reset is dead afterwards, and freeing
    /// one is caught as a double free only if its slot has not been handed
    /// out again. The caller must hold none: the pool resets an arena only
    /// when its fork has been joined and its windings copied home.
    /// </para>
    /// </remarks>
    internal void Reset()
    {
        // Only the segments allocation has reached can hold a live flag. The
        // one _used points into is cleared whole: that costs at most one
        // segment's worth of bytes, and saves reasoning about a reservation
        // that skipped a segment's tail.
        int lastSegment = Math.Min(_used >> SegmentShift, _live.Length - 1);
        for (int i = 0; i <= lastSegment; i++)
        {
            if (_live[i] is { } live)
            {
                Array.Clear(live);
            }
        }

        foreach (Stack<int>? bucket in _free)
        {
            bucket?.Clear();
        }

        _used = 0;
        ActiveWindings = 0;
        PeakWindings = 0;
        TotalAllocations = 0;
        RecycledAllocations = 0;
    }

    /// <summary>Reserves storage for a winding with no points in it yet.</summary>
    /// <param name="capacity">How many points to reserve.</param>
    /// <returns>A handle with <see cref="Winding.Count"/> zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capacity"/> is not positive, or is more than
    /// <see cref="SegmentLength"/>.
    /// </exception>
    /// <remarks>
    /// <c>AllocWinding</c>. Stock sets
    /// <c>numpoints = 0</c> on the way out with the comment "None are occupied
    /// yet even though allocated", and the same is true here: the caller fills
    /// <see cref="Storage"/> and then calls <see cref="SetCount"/>.
    /// </remarks>
    public Winding Alloc(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, SegmentLength);

        TotalAllocations++;
        ActiveWindings++;
        if (ActiveWindings > PeakWindings)
        {
            PeakWindings = ActiveWindings;
        }

        if (capacity < _free.Length)
        {
            Stack<int>? bucket = _free[capacity];
            if (bucket is { Count: > 0 })
            {
                RecycledAllocations++;
                int recycled = bucket.Pop();
                Live(recycled) = true;
                return new Winding(recycled, 0, capacity);
            }
        }

        int offset = EnsureRoom(capacity);
        _used = offset + capacity;
        Live(offset) = true;
        return new Winding(offset, 0, capacity);
    }

    /// <summary>Returns a winding's storage to the free list.</summary>
    /// <param name="winding">The winding to free.</param>
    /// <exception cref="InvalidWindingException">
    /// The winding has already been freed.
    /// </exception>
    /// <remarks>
    /// <c>FreeWinding</c>. Stock stamps
    /// <c>numpoints = 0xdeaddead</c> and errors out when it sees that stamp
    /// again — "freed a freed winding". The same check is here, kept because a
    /// double free in the clipper is the kind of bug that otherwise surfaces
    /// later as a winding with someone else's points in it. Freeing
    /// <see cref="Winding.Null"/> does nothing, which is what the
    /// <c>if (b) FreeWinding(b)</c> pattern wants.
    /// </remarks>
    public void Free(Winding winding)
    {
        if (winding.IsNull)
        {
            return;
        }

        ref bool live = ref Live(winding.Offset);
        if (!live)
        {
            throw new InvalidWindingException("FreeWinding: freed a freed winding");
        }

        live = false;
        ActiveWindings--;

        int capacity = winding.Capacity;
        if (capacity >= _free.Length)
        {
            Array.Resize(ref _free, capacity + 1);
        }

        (_free[capacity] ??= new Stack<int>()).Push(winding.Offset);
    }

    /// <summary>The winding's live points.</summary>
    /// <param name="winding">The winding.</param>
    /// <returns>A span of <see cref="Winding.Count"/> points.</returns>
    /// <remarks>
    /// Invalidated by any allocation on this arena. See the type's remarks.
    /// </remarks>
    public Span<Vec3> Points(Winding winding) =>
        _slab[winding.Offset >> SegmentShift].AsSpan(winding.Offset & SegmentMask, winding.Count);

    /// <summary>The winding's full reserved storage, live points and all.</summary>
    /// <param name="winding">The winding.</param>
    /// <returns>A span of <see cref="Winding.Capacity"/> points.</returns>
    /// <remarks>
    /// For building a winding up point by point, the way the clipper does.
    /// Invalidated by any allocation on this arena.
    /// </remarks>
    public Span<Vec3> Storage(Winding winding) =>
        _slab[winding.Offset >> SegmentShift].AsSpan(winding.Offset & SegmentMask, winding.Capacity);

    /// <summary>The same handle with a different live point count.</summary>
    /// <param name="winding">The winding.</param>
    /// <param name="count">The new count, at most the capacity.</param>
    /// <returns>The updated handle.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is negative or exceeds the capacity.
    /// </exception>
    /// <remarks>
    /// Stock writes <c>w-&gt;numpoints = n</c> through the pointer it is
    /// holding. A value handle cannot be mutated in place, so the count comes
    /// back out instead. This is the one ergonomic difference from the reference form and
    /// it is deliberate: it makes "which handle is current" impossible to get
    /// wrong by aliasing.
    /// </remarks>
    public Winding SetCount(Winding winding, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, winding.Capacity);
        return new Winding(winding.Offset, count, winding.Capacity);
    }

    /// <summary>Allocates a winding and fills it with the given points.</summary>
    /// <param name="points">The points, in order.</param>
    /// <returns>The new winding.</returns>
    /// <remarks>
    /// No stock equivalent — stock's callers allocate and then write through
    /// the pointer. It exists so tests and map readers can state a polygon as a
    /// literal rather than as four statements.
    /// </remarks>
    public Winding Create(ReadOnlySpan<Vec3> points)
    {
        Winding w = Alloc(points.Length);
        points.CopyTo(Storage(w));
        return SetCount(w, points.Length);
    }

    /// <summary>A copy of a winding, with its own storage.</summary>
    /// <param name="winding">The winding to copy.</param>
    /// <returns>The copy.</returns>
    /// <remarks>
    /// <c>CopyWinding</c>. The copy's capacity is the
    /// SOURCE'S POINT COUNT, not the source's capacity: a 68-point reservation
    /// holding 5 points copies to a 5-point winding. That matters because the
    /// free list is keyed on capacity, so copying is also how stock compacts.
    /// </remarks>
    public Winding Copy(Winding winding)
    {
        Winding c = Alloc(winding.Count);
        Points(winding).CopyTo(Storage(c));
        return SetCount(c, winding.Count);
    }

    /// <summary>
    /// A copy, in this arena, of a winding that lives in another one, with the
    /// same points and the same capacity.
    /// </summary>
    /// <param name="source">The arena the winding lives in.</param>
    /// <param name="winding">The winding.</param>
    /// <returns>The copy, or <see cref="Winding.Null"/> for a null winding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <remarks>
    /// Not <see cref="Copy"/>: that one compacts the capacity to the point
    /// count, as stock's <c>CopyWinding</c> does, and so is an operation of the
    /// algorithm. This one moves a winding between arenas without the
    /// algorithm seeing a difference, which is what the parallel tree build
    /// needs when a subtree built in a fork's arena comes home.
    /// </remarks>
    internal Winding Adopt(WindingArena source, Winding winding)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (winding.IsNull)
        {
            return Winding.Null;
        }

        Winding c = Alloc(winding.Capacity);
        source.Points(winding).CopyTo(Storage(c));
        return SetCount(c, winding.Count);
    }

    /// <summary>A copy of a winding with its points in the opposite order.</summary>
    /// <param name="winding">The winding to reverse.</param>
    /// <returns>The reversed copy.</returns>
    /// <remarks>
    /// <c>ReverseWinding</c>. Point <c>i</c> of the
    /// result is point <c>n-1-i</c> of the source, so the first point MOVES: it
    /// becomes the last. A reversal that kept point 0 fixed and reversed the
    /// rest would describe the same polygon with the same winding order and
    /// would still pass an area or a plane check, but every consumer that pairs
    /// a portal with its opposite by index would then be off by one.
    /// </remarks>
    public Winding Reverse(Winding winding)
    {
        int n = winding.Count;
        Winding c = Alloc(n);
        Span<Vec3> src = Points(winding);
        Span<Vec3> dst = Storage(c);
        for (int i = 0; i < n; i++)
        {
            dst[i] = src[n - 1 - i];
        }

        return SetCount(c, n);
    }

    /// <summary>
    /// A huge quad lying on the given plane, the starting point for clipping a
    /// brush face out of a plane.
    /// </summary>
    /// <param name="normal">The plane's normal.</param>
    /// <param name="dist">The plane's distance along that normal.</param>
    /// <returns>A four-point winding on the plane.</returns>
    /// <exception cref="InvalidWindingException">
    /// No major axis could be found, which means the normal was not a number.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <c>BaseWindingForPlane</c>. Three details are
    /// load-bearing:
    /// </para>
    /// <para>
    /// The major axis search uses a strict <c>&gt;</c> from a starting maximum
    /// of -1, so on a tie the LOWEST axis wins. The up vector chosen from it
    /// is +Z for an X-major or Y-major plane and +X for a Z-major one
    /// — which means the resulting quad's vertex
    /// ORDER depends on the plane's orientation, and portal windings inherit
    /// that.
    /// </para>
    /// <para>
    /// The quad is pushed out to <c>MAX_COORD_INTEGER*4</c> = 65536 units, four
    /// times the largest legal coordinate, so that clipping it against the
    /// brush's other planes cannot miss a corner. Everything downstream inherits
    /// the rounding of numbers that large: a float has about 7 decimal digits,
    /// so at 65536 the spacing between representable values is about 0.0078,
    /// which is not far below <c>ON_EPSILON</c> at 0.1.
    /// </para>
    /// <para>
    /// The winding is wound p0, p1, p2, p3 = (-right +up), (+right +up),
    /// (+right -up), (-right -up), with <c>vright = vup x normal</c>. That is
    /// the order stock produces and the one every face that comes out of a
    /// brush is built from.
    /// </para>
    /// </remarks>
    public Winding BaseWindingForPlane(Vec3 normal, float dist)
    {
        float max = -1f;
        int x = -1;
        for (int i = 0; i < 3; i++)
        {
            float v = MathF.Abs(normal[i]);
            if (v > max)
            {
                x = i;
                max = v;
            }
        }

        if (x == -1)
        {
            throw new InvalidWindingException("BaseWindingForPlane: no axis found");
        }

        Vec3 vup = x switch
        {
            0 or 1 => new Vec3(0f, 0f, 1f),
            _ => new Vec3(1f, 0f, 0f),
        };

        float d = Vec3.Dot(vup, normal);
        vup += normal * -d;

        // StockQuirk.BaseWindingNormalise. calls
        // VectorNormalize, which is rsqrtss plus one Newton-Raphson step
        // And not a divide. The quad is then pushed out to
        // 65536 units and clipped with an epsilon of exactly zero, so the
        // estimate's last bits decide which slivers survive -- and, further
        // downstream, what AddBrushBevels computes an edge bevel's plane
        // distance from.
        vup = Compliance.Emulates(StockQuirk.BaseWindingNormalise)
            ? vup.NormaliseLikeStock().Normalised
            : vup.Normalise().Normalised;

        Vec3 org = normal * dist;
        Vec3 vright = Vec3.Cross(vup, normal);

        vup *= GeometryEpsilons.BaseWindingExtent;
        vright *= GeometryEpsilons.BaseWindingExtent;

        Winding w = Alloc(4);
        Span<Vec3> p = Storage(w);
        p[0] = (org - vright) + vup;
        p[1] = (org + vright) + vup;
        p[2] = (org + vright) - vup;
        p[3] = (org - vright) - vup;
        return SetCount(w, 4);
    }

    /// <summary>The winding's area.</summary>
    /// <param name="winding">The winding.</param>
    /// <returns>The area in square world units.</returns>
    /// <remarks>
    /// <c>WindingArea</c>. A fan triangulation from
    /// point 0, summing the lengths of the cross products and halving at the
    /// end — so it is correct for any convex polygon and quietly wrong for a
    /// non-convex one, which is why <see cref="Check"/> tests convexity
    /// separately. Summing the cross lengths and halving ONCE, rather than
    /// halving each term, is part of the float result.
    /// </remarks>
    public float Area(Winding winding)
    {
        Span<Vec3> p = Points(winding);
        float total = 0f;
        for (int i = 2; i < p.Length; i++)
        {
            Vec3 d1 = p[i - 1] - p[0];
            Vec3 d2 = p[i] - p[0];
            total += Vec3.Cross(d1, d2).Length();
        }

        return total * 0.5f;
    }

    /// <summary>The winding's axis-aligned bounds.</summary>
    /// <param name="winding">The winding.</param>
    /// <param name="mins">The lowest coordinate on each axis.</param>
    /// <param name="maxs">The highest coordinate on each axis.</param>
    /// <remarks>
    /// <c>WindingBounds</c>. The seeds are +99999 and
    /// -99999, NOT infinities and not
    /// <see cref="GeometryEpsilons.MaxCoordInteger"/>. An empty winding
    /// therefore comes back inside out with mins above maxs, which is stock's
    /// behaviour and is what a bounds-union loop relies on as its identity
    /// element. A vertex beyond 99999 units — impossible in a legal map, whose
    /// limit is 16384 — would be clamped away, which is stock's bug and is
    /// reproduced rather than fixed, since fixing it would change bounds only
    /// for maps that cannot be compiled anyway.
    /// </remarks>
    public void Bounds(Winding winding, out Vec3 mins, out Vec3 maxs)
    {
        float minX = 99999f, minY = 99999f, minZ = 99999f;
        float maxX = -99999f, maxY = -99999f, maxZ = -99999f;

        foreach (Vec3 v in Points(winding))
        {
            if (v.X < minX) { minX = v.X; }
            if (v.X > maxX) { maxX = v.X; }
            if (v.Y < minY) { minY = v.Y; }
            if (v.Y > maxY) { maxY = v.Y; }
            if (v.Z < minZ) { minZ = v.Z; }
            if (v.Z > maxZ) { maxZ = v.Z; }
        }

        mins = new Vec3(minX, minY, minZ);
        maxs = new Vec3(maxX, maxY, maxZ);
    }

    /// <summary>The average of the winding's points.</summary>
    /// <param name="winding">The winding.</param>
    /// <returns>The centre.</returns>
    /// <remarks>
    /// <c>WindingCenter</c>. The vertex average, not
    /// the area centroid — for that see
    /// <see cref="AreaAndBalancePoint"/>, and the two differ for any winding
    /// whose vertices are unevenly spaced.
    /// The scale is computed as <c>1.0 / numpoints</c> in DOUBLE and then
    /// narrowed to <c>float</c>, because stock declares <c>float scale</c> and
    /// assigns a double expression to it (and
    ///). <c>1f / n</c> is not always the same number.
    /// </remarks>
    public Vec3 Center(Winding winding)
    {
        Vec3 center = Vec3.Zero;
        foreach (Vec3 v in Points(winding))
        {
            center = v + center;
        }

        float scale = (float)(1.0 / winding.Count);
        return center * scale;
    }

    /// <summary>The winding's area and its area-weighted centroid.</summary>
    /// <param name="winding">The winding.</param>
    /// <param name="center">The centroid.</param>
    /// <returns>The area.</returns>
    /// <remarks>
    /// <c>WindingAreaAndBalancePoint</c>. Each fan
    /// triangle contributes its own centroid weighted by its area, accumulated
    /// as three separate <c>VectorMA</c> calls with a scale of
    /// <c>area / 3.0</c> — a DOUBLE divide narrowed to the <c>float</c>
    /// parameter, and likewise the final <c>1.0 / total</c>. A zero-area
    /// winding leaves the centre at the origin rather than dividing by zero,
    /// which stock guards with <c>if (total)</c>.
    /// </remarks>
    public float AreaAndBalancePoint(Winding winding, out Vec3 center)
    {
        center = Vec3.Zero;
        Span<Vec3> p = Points(winding);
        float total = 0f;

        for (int i = 2; i < p.Length; i++)
        {
            Vec3 d1 = p[i - 1] - p[0];
            Vec3 d2 = p[i] - p[0];
            float area = Vec3.Cross(d1, d2).Length();
            total += area;

            float third = (float)(area / 3.0);
            center += p[i - 1] * third;
            center += p[i] * third;
            center += p[0] * third;
        }

        if (total != 0f)
        {
            center *= (float)(1.0 / total);
        }

        return total * 0.5f;
    }

    /// <summary>The plane the winding lies in.</summary>
    /// <param name="winding">The winding.</param>
    /// <returns>The plane, with a normal on the winding's front face.</returns>
    /// <remarks>
    /// <para>
    /// <c>WindingPlane</c>. Two things here are not what
    /// a fresh implementation would do.
    /// </para>
    /// <para>
    /// The second edge is taken to point <b>3</b>, not point 2, whenever the
    /// winding has more than three points — stock's own comment calls it
    /// "HACKHACK: Avoid potentially collinear verts".
    /// So a five-point winding's plane is decided by points 0, 1 and 3, and
    /// moving point 2 does not change the answer at all.
    /// </para>
    /// <para>
    /// The cross product is <c>v2 x v1</c> and not <c>v1 x v2</c>
    /// Taking it the other way round returns the
    /// plane facing backwards, and since portals are matched to their opposite
    /// by plane sign that would silently invert visibility.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidWindingException">
    /// The winding has fewer than three points.
    /// </exception>
    public Plane WindingPlane(Winding winding)
    {
        Span<Vec3> p = Points(winding);
        if (p.Length < 3)
        {
            throw new InvalidWindingException(
                $"WindingPlane: {p.Length} points");
        }

        Vec3 v1 = p[1] - p[0];
        Vec3 v2 = p.Length > 3 ? p[3] - p[0] : p[2] - p[0];
        (Vec3 normal, _) = Vec3.Cross(v2, v1).Normalise();
        return new Plane(normal, Vec3.Dot(p[0], normal));
    }

    /// <summary>Drops points whose two edges run in nearly the same direction.</summary>
    /// <param name="winding">The winding to thin.</param>
    /// <returns>The winding, with the same storage and possibly fewer points.</returns>
    /// <remarks>
    /// <para>
    /// <c>RemoveColinearPoints</c>. A point is KEPT when
    /// the dot of its two normalised edge directions is below 0.999, so a point
    /// is dropped when the turn at it is less than about 2.56 degrees. The
    /// literal is a <c>double</c> compared against a <c>float</c> dot, so the
    /// comparison happens in double against
    /// 0.99899999999999999911 and not against <c>0.999f</c>.
    /// </para>
    /// <para>
    /// The edges are <c>p[i+1] - p[i]</c> and <c>p[i] - p[i-1]</c>, both
    /// pointing FORWARD through the vertex, which is why a straight run gives a
    /// dot near +1 and not near -1.
    /// </para>
    /// <para>
    /// Stock writes the survivors to a 64-entry stack array
    /// While iterating up to <c>numpoints</c>, which a
    /// clip can have left at 68; that is a latent overflow and the port does
    /// not have it.
    /// </para>
    /// </remarks>
    public Winding RemoveColinearPoints(Winding winding)
    {
        int n = winding.Count;
        Span<Vec3> p = Points(winding);
        Span<Vec3> kept = stackalloc Vec3[SideBufferLength];
        int nump = 0;

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            int k = (i + n - 1) % n;
            (Vec3 v1, _) = (p[j] - p[i]).Normalise();
            (Vec3 v2, _) = (p[i] - p[k]).Normalise();
            if (Vec3.Dot(v1, v2) < GeometryEpsilons.ColinearDotThreshold)
            {
                kept[nump] = p[i];
                nump++;
            }
        }

        if (nump == n)
        {
            return winding;
        }

        kept[..nump].CopyTo(p);
        return SetCount(winding, nump);
    }

    /// <summary>Checks that a winding is something the compilers can use.</summary>
    /// <param name="winding">The winding to check.</param>
    /// <exception cref="InvalidWindingException">It is not.</exception>
    /// <remarks>
    /// <para>
    /// <c>CheckWinding</c>. Five conditions: at least
    /// three points, an area of at least 1, every coordinate inside
    /// <c>MIN_COORD_INTEGER</c>..<c>MAX_COORD_INTEGER</c>, every point within
    /// <c>ON_EPSILON</c> of the winding's own plane, and no degenerate edge —
    /// then convexity, by checking that every other point is behind each edge's
    /// outward normal.
    /// </para>
    /// <para>
    /// <b>The comparisons here are DOUBLE ones.</b> <c>ON_EPSILON</c> is the
    /// bare macro, a <c>double</c> literal of 0.1, and the <c>float</c>
    /// distances are promoted to meet it. That is a different number from the
    /// <c>0.1f</c> the clipper uses, which receives the same macro through a
    /// <c>vec_t epsilon</c> parameter and narrows it. A winding can therefore be
    /// accepted by the clipper and rejected here, and the port keeps that.
    /// </para>
    /// <para>
    /// The area threshold of 1 square unit is absolute, not relative, so it
    /// rejects a legitimately tiny face as readily as a degenerate one.
    /// </para>
    /// </remarks>
    public void Check(Winding winding)
    {
        Span<Vec3> p = Points(winding);
        if (p.Length < 3)
        {
            throw new InvalidWindingException($"CheckWinding: {p.Length} points");
        }

        float area = Area(winding);
        if (area < 1f)
        {
            throw new InvalidWindingException($"CheckWinding: {area} area");
        }

        Plane face = WindingPlane(winding);

        for (int i = 0; i < p.Length; i++)
        {
            Vec3 p1 = p[i];

            for (int axis = 0; axis < 3; axis++)
            {
                if (p1[axis] > GeometryEpsilons.MaxCoordInteger
                    || p1[axis] < GeometryEpsilons.MinCoordInteger)
                {
                    throw new InvalidWindingException($"CheckFace: out of range: {p1[axis]}");
                }
            }

            int next = i + 1 == p.Length ? 0 : i + 1;

            double d = face.DistanceTo(p1);
            if (d < -GeometryEpsilons.OnEpsilon || d > GeometryEpsilons.OnEpsilon)
            {
                throw new InvalidWindingException("CheckWinding: point off plane");
            }

            Vec3 dir = p[next] - p1;
            if (dir.Length() < GeometryEpsilons.OnEpsilon)
            {
                throw new InvalidWindingException("CheckWinding: degenerate edge");
            }

            (Vec3 edgeNormal, _) = Vec3.Cross(face.Normal, dir).Normalise();

            // `edgedist += ON_EPSILON` on a vec_t: the float is promoted, 0.1
            // added in double, and the sum narrowed straight back to float.
            float edgeDist = (float)(Vec3.Dot(p1, edgeNormal) + GeometryEpsilons.OnEpsilon);

            for (int j = 0; j < p.Length; j++)
            {
                if (j == i)
                {
                    continue;
                }

                if (Vec3.Dot(p[j], edgeNormal) > edgeDist)
                {
                    throw new InvalidWindingException("CheckWinding: non-convex");
                }
            }
        }
    }

    /// <summary>Which side of a plane the whole winding is on.</summary>
    /// <param name="winding">The winding.</param>
    /// <param name="normal">The plane's normal.</param>
    /// <param name="dist">The plane's distance.</param>
    /// <returns>
    /// <see cref="PlaneSide.Front"/>, <see cref="PlaneSide.Back"/>,
    /// <see cref="PlaneSide.On"/> or <see cref="PlaneSide.Cross"/>.
    /// </returns>
    /// <remarks>
    /// <c>WindingOnPlaneSide</c>. It returns
    /// <see cref="PlaneSide.Cross"/> as soon as it has seen a point on each
    /// side, so it does not visit every point when the answer is already known.
    /// Both comparisons are against the bare <c>ON_EPSILON</c> macro and
    /// therefore happen in <c>double</c>. A winding entirely inside the epsilon
    /// slab reports <see cref="PlaneSide.On"/>.
    /// </remarks>
    public PlaneSide OnPlaneSide(Winding winding, Vec3 normal, float dist)
    {
        bool front = false;
        bool back = false;

        foreach (Vec3 v in Points(winding))
        {
            double d = Vec3.Dot(v, normal) - dist;
            if (d < -GeometryEpsilons.OnEpsilon)
            {
                if (front)
                {
                    return PlaneSide.Cross;
                }

                back = true;
                continue;
            }

            if (d > GeometryEpsilons.OnEpsilon)
            {
                if (back)
                {
                    return PlaneSide.Cross;
                }

                front = true;
                continue;
            }
        }

        if (back)
        {
            return PlaneSide.Back;
        }

        return front ? PlaneSide.Front : PlaneSide.On;
    }

    /// <summary>Splits a winding by a plane.</summary>
    /// <param name="winding">The winding to split. It is NOT freed.</param>
    /// <param name="normal">The plane's normal.</param>
    /// <param name="dist">The plane's distance.</param>
    /// <param name="epsilon">How close to the plane counts as on it.</param>
    /// <param name="front">The part in front, or <see cref="Winding.Null"/>.</param>
    /// <param name="back">The part behind, or <see cref="Winding.Null"/>.</param>
    /// <remarks>
    /// <para>
    /// <c>ClipWindingEpsilon</c>. The two-way split
    /// every other clipping function is built from. It does NOT free its input;
    /// <see cref="Chop"/> is the variant that does.
    /// </para>
    /// <para>
    /// <b>A winding entirely on the plane comes back as BACK, not front.</b>
    /// The first early-out is <c>if (!counts[0])</c> where <c>counts[0]</c> is
    /// the FRONT count, so a winding whose every point
    /// is within the epsilon has no front points, takes that branch, and is
    /// copied to <paramref name="back"/> with <paramref name="front"/> left
    /// null. Any caller that reads only the front result silently drops
    /// coplanar geometry, and vbsp's face merging depends on exactly this.
    /// </para>
    /// <para>
    /// <b>The split point is snapped on axial planes.</b> For each axis, if the
    /// plane's normal component is exactly 1 or exactly -1 the new vertex takes
    /// <c>dist</c> or <c>-dist</c> on that axis outright rather than being
    /// interpolated. Stock's comment is "avoid
    /// round off error when possible". It is the reason a grid-aligned map
    /// produces exactly-integer vertices, and it is why the epsilons behave
    /// differently on axial and non-axial planes.
    /// </para>
    /// <para>
    /// <b>The reservation is <c>numpoints + 4</c>.</b> Not <c>counts[0] + 2</c>,
    /// which is the geometrically correct bound — stock's comment says it "cant
    /// use counts[0]+2 because of fp grouping errors",
    /// meaning it does not trust its own side classification to be consistent
    /// with the interpolation that follows.
    /// </para>
    /// <para>
    /// <b>The wrap-around slot.</b> After classifying, stock copies side 0 and
    /// distance 0 into slot <c>numpoints</c> so the
    /// loop can read <c>sides[i+1]</c> without a modulo. The port keeps the same
    /// shape; unlike stock it sizes the buffer for it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidWindingException">
    /// A result exceeded <see cref="MaxPointsOnWinding"/> or its reservation.
    /// </exception>
    public void ClipEpsilon(
        Winding winding,
        Vec3 normal,
        float dist,
        float epsilon,
        out Winding front,
        out Winding back)
    {
        Span<float> dists = stackalloc float[SideBufferLength];
        Span<PlaneSide> sides = stackalloc PlaneSide[SideBufferLength];
        Span<int> counts = stackalloc int[3];

        int n = Classify(winding, normal, dist, epsilon, dists, sides, counts);

        front = Winding.Null;
        back = Winding.Null;

        if (counts[(int)PlaneSide.Front] == 0)
        {
            back = Copy(winding);
            return;
        }

        if (counts[(int)PlaneSide.Back] == 0)
        {
            front = Copy(winding);
            return;
        }

        int maxpts = n + 4;
        front = Alloc(maxpts);
        back = Alloc(maxpts);

        // Spans taken only now: the two allocations above may have grown the
        // slab and invalidated anything taken earlier.
        Span<Vec3> inPts = Points(winding);
        Span<Vec3> f = Storage(front);
        Span<Vec3> b = Storage(back);
        int fn = 0;
        int bn = 0;

        for (int i = 0; i < n; i++)
        {
            Vec3 p1 = inPts[i];

            if (sides[i] == PlaneSide.On)
            {
                f[fn++] = p1;
                b[bn++] = p1;
                continue;
            }

            if (sides[i] == PlaneSide.Front)
            {
                f[fn++] = p1;
            }

            if (sides[i] == PlaneSide.Back)
            {
                b[bn++] = p1;
            }

            if (sides[i + 1] == PlaneSide.On || sides[i + 1] == sides[i])
            {
                continue;
            }

            Vec3 mid = SplitPoint(inPts, i, n, dists, normal, dist);
            f[fn++] = mid;
            b[bn++] = mid;
        }

        CheckClipResult(fn, maxpts);
        CheckClipResult(bn, maxpts);
        front = SetCount(front, fn);
        back = SetCount(back, bn);
    }

    /// <summary>Splits a winding by a plane, three ways.</summary>
    /// <param name="winding">The winding to split. It is NOT freed.</param>
    /// <param name="normal">The plane's normal.</param>
    /// <param name="dist">The plane's distance.</param>
    /// <param name="epsilon">How close to the plane counts as on it.</param>
    /// <param name="front">The part in front, or <see cref="Winding.Null"/>.</param>
    /// <param name="back">The part behind, or <see cref="Winding.Null"/>.</param>
    /// <param name="on">
    /// The whole winding when every point lies within the epsilon, otherwise
    /// <see cref="Winding.Null"/>.
    /// </param>
    /// <remarks>
    /// <c>ClassifyWindingEpsilon</c>. Byte for byte the
    /// same as <see cref="ClipEpsilon"/> apart from one extra early-out
    /// When there is neither a front nor a back point
    /// the winding is returned as <paramref name="on"/>. That is the case
    /// <see cref="ClipEpsilon"/> reports as <paramref name="back"/>, so the two
    /// functions genuinely disagree about a coplanar winding and a caller has
    /// to pick the one whose answer it wants.
    /// </remarks>
    /// <exception cref="InvalidWindingException">
    /// A result exceeded <see cref="MaxPointsOnWinding"/> or its reservation.
    /// </exception>
    public void ClassifyEpsilon(
        Winding winding,
        Vec3 normal,
        float dist,
        float epsilon,
        out Winding front,
        out Winding back,
        out Winding on)
    {
        Span<float> dists = stackalloc float[SideBufferLength];
        Span<PlaneSide> sides = stackalloc PlaneSide[SideBufferLength];
        Span<int> counts = stackalloc int[3];

        int n = Classify(winding, normal, dist, epsilon, dists, sides, counts);

        front = Winding.Null;
        back = Winding.Null;
        on = Winding.Null;

        if (counts[(int)PlaneSide.Front] == 0 && counts[(int)PlaneSide.Back] == 0)
        {
            on = Copy(winding);
            return;
        }

        if (counts[(int)PlaneSide.Front] == 0)
        {
            back = Copy(winding);
            return;
        }

        if (counts[(int)PlaneSide.Back] == 0)
        {
            front = Copy(winding);
            return;
        }

        int maxpts = n + 4;
        front = Alloc(maxpts);
        back = Alloc(maxpts);

        Span<Vec3> inPts = Points(winding);
        Span<Vec3> f = Storage(front);
        Span<Vec3> b = Storage(back);
        int fn = 0;
        int bn = 0;

        for (int i = 0; i < n; i++)
        {
            Vec3 p1 = inPts[i];

            if (sides[i] == PlaneSide.On)
            {
                f[fn++] = p1;
                b[bn++] = p1;
                continue;
            }

            if (sides[i] == PlaneSide.Front)
            {
                f[fn++] = p1;
            }

            if (sides[i] == PlaneSide.Back)
            {
                b[bn++] = p1;
            }

            if (sides[i + 1] == PlaneSide.On || sides[i + 1] == sides[i])
            {
                continue;
            }

            Vec3 mid = SplitPoint(inPts, i, n, dists, normal, dist);
            f[fn++] = mid;
            b[bn++] = mid;
        }

        CheckClipResult(fn, maxpts);
        CheckClipResult(bn, maxpts);
        front = SetCount(front, fn);
        back = SetCount(back, bn);
    }

    /// <summary>Keeps only the front of a winding, freeing the original.</summary>
    /// <param name="winding">The winding to chop. It IS freed.</param>
    /// <param name="normal">The plane's normal.</param>
    /// <param name="dist">The plane's distance.</param>
    /// <returns>
    /// The front fragment, or <see cref="Winding.Null"/> when nothing was in
    /// front.
    /// </returns>
    /// <remarks>
    /// <c>ChopWinding</c>. It passes
    /// <c>ON_EPSILON</c> through <c>ClipWindingEpsilon</c>'s <c>vec_t</c>
    /// parameter, so this is the FLOAT 0.1f and not the double 0.1 that
    /// <see cref="Check"/> uses. The input and the discarded back fragment are
    /// both freed, so the caller's handle is dead on return.
    /// </remarks>
    public Winding Chop(Winding winding, Vec3 normal, float dist)
    {
        ClipEpsilon(winding, normal, dist, GeometryEpsilons.OnEpsilonFloat,
            out Winding f, out Winding b);
        Free(winding);
        Free(b);
        return f;
    }

    /// <summary>Keeps only the front of a winding, replacing it.</summary>
    /// <param name="winding">The winding, freed and replaced by the result.</param>
    /// <param name="normal">The plane's normal.</param>
    /// <param name="dist">The plane's distance.</param>
    /// <param name="epsilon">How close to the plane counts as on it.</param>
    /// <returns>
    /// The chopped winding, or <see cref="Winding.Null"/> when nothing was in
    /// front.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <c>ChopWindingInPlace</c>. The same clip again,
    /// but building only the front side, and with one behaviour the two-sided
    /// version does not have: when there is nothing BEHIND the plane it returns
    /// the winding <b>unchanged and unfreed</b> (
    /// "inout stays the same"), where <see cref="ClipEpsilon"/> would have made
    /// a copy. So the returned handle is sometimes the one that was passed in
    /// and sometimes a fresh one, and the old handle is dead in the second case
    /// but not the first. That is why this returns the handle rather than
    /// taking a <c>ref</c>: the caller cannot then keep a stale copy.
    /// </para>
    /// <para>
    /// A winding entirely in front of the plane therefore also keeps its
    /// original capacity, which a caller that free-lists by capacity can
    /// notice.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidWindingException">
    /// The result exceeded <see cref="MaxPointsOnWinding"/> or its reservation.
    /// </exception>
    public Winding ChopInPlace(Winding winding, Vec3 normal, float dist, float epsilon)
    {
        Span<float> dists = stackalloc float[SideBufferLength];
        Span<PlaneSide> sides = stackalloc PlaneSide[SideBufferLength];
        Span<int> counts = stackalloc int[3];

        int n = Classify(winding, normal, dist, epsilon, dists, sides, counts);

        if (counts[(int)PlaneSide.Front] == 0)
        {
            Free(winding);
            return Winding.Null;
        }

        if (counts[(int)PlaneSide.Back] == 0)
        {
            return winding;
        }

        int maxpts = n + 4;
        Winding result = Alloc(maxpts);

        Span<Vec3> inPts = Points(winding);
        Span<Vec3> f = Storage(result);
        int fn = 0;

        for (int i = 0; i < n; i++)
        {
            Vec3 p1 = inPts[i];

            if (sides[i] == PlaneSide.On)
            {
                f[fn++] = p1;
                continue;
            }

            if (sides[i] == PlaneSide.Front)
            {
                f[fn++] = p1;
            }

            if (sides[i + 1] == PlaneSide.On || sides[i + 1] == sides[i])
            {
                continue;
            }

            f[fn++] = SplitPoint(inPts, i, n, dists, normal, dist);
        }

        CheckClipResult(fn, maxpts);
        Free(winding);
        return SetCount(result, fn);
    }

    /// <summary>Moves every point of a winding by an offset.</summary>
    /// <param name="winding">The winding to move, in place.</param>
    /// <param name="offset">How far to move it.</param>
    /// <remarks>
    /// <c>TranslateWinding</c>.
    /// </remarks>
    public void Translate(Winding winding, Vec3 offset)
    {
        Span<Vec3> p = Points(winding);
        for (int i = 0; i < p.Length; i++)
        {
            p[i] += offset;
        }
    }

    /// <summary>
    /// Splits a winding by a plane, doing the arithmetic near the origin.
    /// </summary>
    /// <param name="winding">The winding to split. It is NOT freed, and it is restored.</param>
    /// <param name="normal">The plane's normal.</param>
    /// <param name="dist">The plane's distance.</param>
    /// <param name="epsilon">How close to the plane counts as on it.</param>
    /// <param name="offset">
    /// The translation applied before clipping and undone afterwards.
    /// </param>
    /// <param name="front">The part in front, or <see cref="Winding.Null"/>.</param>
    /// <param name="back">The part behind, or <see cref="Winding.Null"/>.</param>
    /// <remarks>
    /// <c>ClipWindingEpsilon_Offset</c>. Identical to
    /// <see cref="ClipEpsilon"/> except that everything is moved by
    /// <paramref name="offset"/> first and moved back after, which buys
    /// precision when the geometry is far from the origin: a float's spacing at
    /// 16384 units is about 0.002, and clipping with an epsilon of 0.1 at that
    /// magnitude is measurably coarser than clipping the same shape near zero.
    /// The plane's distance is corrected by <c>Dot(offset, normal)</c> so it
    /// travels with the geometry. The input winding is translated and then
    /// translated back, so it ends where it started — but it is MUTATED in
    /// between, which matters if anything else is holding its points.
    /// </remarks>
    public void ClipEpsilonOffset(
        Winding winding,
        Vec3 normal,
        float dist,
        float epsilon,
        Vec3 offset,
        out Winding front,
        out Winding back)
    {
        Translate(winding, offset);
        ClipEpsilon(winding, normal, dist + Vec3.Dot(offset, normal), epsilon, out front, out back);
        Translate(winding, -offset);
        if (!front.IsNull)
        {
            Translate(front, -offset);
        }

        if (!back.IsNull)
        {
            Translate(back, -offset);
        }
    }

    /// <summary>
    /// Splits a winding three ways, doing the arithmetic near the origin.
    /// </summary>
    /// <param name="winding">The winding to split. It is NOT freed, and it is restored.</param>
    /// <param name="normal">The plane's normal.</param>
    /// <param name="dist">The plane's distance.</param>
    /// <param name="epsilon">How close to the plane counts as on it.</param>
    /// <param name="offset">
    /// The translation applied before clipping and undone afterwards.
    /// </param>
    /// <param name="front">The part in front, or <see cref="Winding.Null"/>.</param>
    /// <param name="back">The part behind, or <see cref="Winding.Null"/>.</param>
    /// <param name="on">The whole winding when coplanar, otherwise null.</param>
    /// <remarks>
    /// <c>ClassifyWindingEpsilon_Offset</c>.
    /// </remarks>
    public void ClassifyEpsilonOffset(
        Winding winding,
        Vec3 normal,
        float dist,
        float epsilon,
        Vec3 offset,
        out Winding front,
        out Winding back,
        out Winding on)
    {
        Translate(winding, offset);
        ClassifyEpsilon(winding, normal, dist + Vec3.Dot(offset, normal), epsilon,
            out front, out back, out on);
        Translate(winding, -offset);
        if (!front.IsNull)
        {
            Translate(front, -offset);
        }

        if (!back.IsNull)
        {
            Translate(back, -offset);
        }

        if (!on.IsNull)
        {
            Translate(on, -offset);
        }
    }

    /// <summary>Whether a point lies inside a winding.</summary>
    /// <param name="winding">The winding.</param>
    /// <param name="point">The point, assumed to be in the winding's plane.</param>
    /// <returns>True when the point is inside.</returns>
    /// <remarks>
    /// <para>
    /// <c>PointInWinding</c>. It takes the cross of each
    /// edge with the vector to the point and asks whether all of them face the
    /// same way as the first one's. There is no epsilon at all: the test is
    /// <c>&lt; 0.0f</c>, so a point exactly on an edge is inside and a point a
    /// float's width outside it is not.
    /// </para>
    /// <para>
    /// The point is ASSUMED to be in the plane — stock says so in its comment —
    /// and nothing checks it. A point well off the plane can pass, because the
    /// crosses still agree in sign.
    /// </para>
    /// <para>
    /// The stock file also carries a shorter version of this in an
    /// <c>#if 0</c> block that tests the angle at
    /// the point instead. It is not equivalent, and it is not what shipped.
    /// </para>
    /// </remarks>
    public bool PointInWinding(Winding winding, Vec3 point)
    {
        if (winding.IsNull)
        {
            return false;
        }

        Span<Vec3> p = Points(winding);
        Vec3 toPt = point - p[0];
        Vec3 edge = p[1] - p[0];
        (Vec3 testCross, _) = Vec3.Cross(edge, toPt).Normalise();

        for (int i = 1; i < p.Length; i++)
        {
            toPt = point - p[i];
            edge = p[(i + 1) % p.Length] - p[i];
            (Vec3 cross, _) = Vec3.Cross(edge, toPt).Normalise();

            if (Vec3.Dot(cross, testCross) < 0.0f)
            {
                return false;
            }
        }

        return true;
    }

    private int Classify(
        Winding winding,
        Vec3 normal,
        float dist,
        float epsilon,
        Span<float> dists,
        Span<PlaneSide> sides,
        Span<int> counts)
    {
        counts.Clear();

        int n = winding.Count;
        if (n + 1 > dists.Length)
        {
            throw new InvalidWindingException(
                $"ClipWinding: {n} points exceeds the {SideBufferLength - 1} the side buffer holds");
        }

        Span<Vec3> p = Points(winding);
        for (int i = 0; i < n; i++)
        {
            float dot = Vec3.Dot(p[i], normal);
            dot -= dist;
            dists[i] = dot;
            sides[i] = dot > epsilon ? PlaneSide.Front
                : dot < -epsilon ? PlaneSide.Back
                : PlaneSide.On;
            counts[(int)sides[i]]++;
        }

        // -- the wrap-around slot the loop reads as sides[i+1].
        sides[n] = sides[0];
        dists[n] = dists[0];
        return n;
    }

    private static Vec3 SplitPoint(
        ReadOnlySpan<Vec3> points,
        int i,
        int n,
        ReadOnlySpan<float> dists,
        Vec3 normal,
        float dist)
    {
        Vec3 p1 = points[i];
        Vec3 p2 = points[(i + 1) % n];

        float dot = dists[i] / (dists[i] - dists[i + 1]);

        // -- "avoid round off error when possible". Written
        // out per axis rather than as stock's `for (j=0; j<3; j++)` loop so the
        // method stays inlineable; the three branches and their order are the
        // same.
        return new Vec3(
            Axis(normal.X, p1.X, p2.X, dot, dist),
            Axis(normal.Y, p1.Y, p2.Y, dot, dist),
            Axis(normal.Z, p1.Z, p2.Z, dot, dist));
    }

    private static float Axis(float normal, float a, float b, float dot, float dist)
    {
        if (normal == 1f)
        {
            return dist;
        }

        if (normal == -1f)
        {
            return -dist;
        }

        return a + (dot * (b - a));
    }

    private static void CheckClipResult(int count, int maxpts)
    {
        // Stock checks both of these after the fact,
        // having already written past the end of its reservation if the first
        // one is going to fire. Here the span's own bounds check fires first,
        // so this arm is unreachable and is kept only so the two files read the
        // same; the MAX_POINTS_ON_WINDING arm below is the live one, since a
        // reservation of numpoints+4 can legitimately be 68.
        if (count > maxpts)
        {
            throw new InvalidWindingException("ClipWinding: points exceeded estimate");
        }

        if (count > MaxPointsOnWinding)
        {
            throw new InvalidWindingException("ClipWinding: MAX_POINTS_ON_WINDING");
        }
    }

    private ref bool Live(int offset) => ref _live[offset >> SegmentShift][offset & SegmentMask];

    // Where a new reservation of `capacity` points goes: after the last one
    // when it fits in that segment, else at the start of the next segment.
    // Grows the first segment by doubling, or adds a whole segment.
    private int EnsureRoom(int capacity)
    {
        int segment = _used >> SegmentShift;
        int local = _used & SegmentMask;
        if (local + capacity > SegmentLength)
        {
            segment++;
            local = 0;
        }

        if (segment == 0)
        {
            Vec3[] first = _slab[0];
            if (local + capacity > first.Length)
            {
                int want = Math.Min(
                    Math.Max(first.Length == 0 ? 4096 : first.Length * 2, local + capacity),
                    SegmentLength);
                Array.Resize(ref _slab[0], want);
                Array.Resize(ref _live[0], want);
            }
        }
        else
        {
            if (segment == _slab.Length)
            {
                Array.Resize(ref _slab, _slab.Length * 2);
                Array.Resize(ref _live, _live.Length * 2);
            }

            if (_slab[segment] is null)
            {
                _slab[segment] = new Vec3[SegmentLength];
                _live[segment] = new bool[SegmentLength];
            }
        }

        return (segment << SegmentShift) | local;
    }
}
