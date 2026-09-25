using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Tracing;

/// <summary>
/// Stock vrad's OTHER ray tracer: a front-to-back walk of the map's own BSP
/// tree, testing lit surfaces in lightmap space.
/// </summary>
/// <remarks>
/// <para>
/// THIS, NOT THE KD-TREE, IS WHAT LEAF AMBIENT USES, and leaf ambient is
/// 51.6 % of stock vrad's wall clock. <c>CalcRayAmbientLighting</c>
/// Constructs a <c>CLightSurface</c> and calls
/// <c>FindIntersection</c>; it never touches <c>g_RtEnv</c>. A port that
/// replaced this with a BVH would be replacing the single largest stage in the
/// tool with a structure that is worse at its shape, and the measurement says
/// so: on <c>dm_lockdown</c>'s leaf-ambient rays a prototype managed BVH did
/// 0.95 Mray/s against stock's 4.35.
/// </para>
/// <para>
/// Why the BSP walk wins, stated so the port keeps the properties rather than
/// the shape:
/// </para>
/// <list type="number">
/// <item><description>
/// THE TREE IS ALREADY IN THE FILE. There is no build, no SAH pass, no
/// per-map preprocessing beyond copying planes into nodes.
/// </description></item>
/// <item><description>
/// A NODE TEST IS ONE DOT PRODUCT against a plane, not six slab compares
/// against a box -- and it is EXACT: a BSP split has no straddling region, so
/// a ray that starts in front and ends in front never descends the back child.
/// A BVH's siblings overlap, so it often descends both.
/// </description></item>
/// <item><description>
/// THE WALK IS FRONT TO BACK AND STOPS AT THE FIRST HIT. Leaf ambient is a
/// closest-hit stage, so "first hit in front-to-back order" IS the answer and
/// everything beyond it is never visited.
/// </description></item>
/// </list>
/// <para>
/// WHAT IS MISSING, said plainly. Stock ends every leaf with
/// <c>StaticDispMgr()-&gt;ClipRayToDispInLeaf</c>, and this class does not,
/// because the displacement collision tree belongs to lane 4b.
/// <see cref="BspTraceGeometry.SkippedDisplacementFaces"/> says how much of a
/// given map that leaves out, and a throughput comparison against a stock
/// binary that DOES test displacements is not a comparison of the same work --
/// which is why the gate for this lane compares against stock with the same
/// hook stubbed out.
/// </para>
/// <para>
/// THREAD SAFETY: an instance is immutable and every trace keeps its state on
/// the stack, so one tracer serves every worker. Stock cannot: its
/// <c>CLightSurface</c> carries <c>m_iThread</c> purely to index
/// <c>s_DispTested[]</c>, a global whose per-thread slots §8 already lists as
/// needing to become per-work-item state.
/// </para>
/// </remarks>
public sealed class BspSurfaceTracer : IRayTracer
{
    /// <summary>
    /// <c>TEST_EPSILON</c>. One thirty-second of a
    /// unit, and exactly representable, so it is the same number here as
    /// there.
    /// </summary>
    private const float TestEpsilon = 0.03125f;

    /// <summary>
    /// How deep the explicit traversal stack goes.
    /// </summary>
    /// <remarks>
    /// Stock recurses, so its limit is the C stack and it has none written
    /// down. A BSP tree from vbsp is built by median splits over a bounded
    /// world and does not come close to this; the number is a guard against a
    /// malformed tree turning into an unbounded loop, and it throws rather than
    /// silently truncating the walk, because a truncated walk returns "no hit"
    /// and no hit is a perfectly ordinary answer.
    /// </remarks>
    private const int MaxStack = 256;

    private readonly BspTraceGeometry _geometry;
    private readonly TraceNode[] _nodes;
    private readonly TraceSurface[] _surfaces;
    private readonly int[] _nodeFaces;
    private readonly int[] _leafFaces;
    private readonly int[] _leafFaceStart;
    private readonly int[] _leafFaceCount;
    private readonly Vec3[] _skyPoints;
    private readonly int[] _skyStart;
    private readonly int[] _skyCount;

    /// <summary>
    /// Wraps a flattened tree.
    /// </summary>
    /// <param name="geometry">The tree, from <see cref="BspTraceGeometry.Build"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="geometry"/> is null.</exception>
    /// <remarks>
    /// Every array is copied into a field of this object rather than reached
    /// through <paramref name="geometry"/> per access. Two loads to reach an
    /// array is one more than one, and the walk does it per node.
    /// </remarks>
    public BspSurfaceTracer(BspTraceGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        _geometry = geometry;
        _nodes = geometry.Nodes;
        _surfaces = geometry.Surfaces;
        _nodeFaces = geometry.NodeFaces;
        _leafFaces = geometry.LeafFaces;
        _leafFaceStart = geometry.LeafFaceStart;
        _leafFaceCount = geometry.LeafFaceCount;
        _skyPoints = geometry.SkyPoints;
        _skyStart = geometry.SkyStart;
        _skyCount = geometry.SkyCount;
    }

    /// <inheritdoc />
    public string TracerIdentity => "cpu-bsp-surface-1";

    /// <summary>The tree this tracer walks.</summary>
    public BspTraceGeometry Geometry => _geometry;

    /// <inheritdoc />
    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TraceVisibility(rays.Span, hitBits.Span, options);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<HitId> hits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TraceClosest(rays.Span, hits.Span, options);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The synchronous closest-hit batch, for a caller already on a worker.
    /// </summary>
    /// <param name="rays">The rays to trace.</param>
    /// <param name="hits">Receives one result per ray.</param>
    /// <param name="options">The ray epsilon.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="hits"/> is shorter than <paramref name="rays"/>.
    /// </exception>
    /// <remarks>
    /// Spans, so the per-ray loop is not reached through a
    /// <see cref="Memory{T}"/> indirection, and no <c>async</c>, so there is no
    /// state machine on the path that runs six million times per map. The
    /// interface still carries the async pair, because the GPU backend needs
    /// it.
    /// </remarks>
    public void TraceClosest(
        ReadOnlySpan<Ray> rays, Span<HitId> hits, RayTraceOptions options)
    {
        if (hits.Length < rays.Length)
        {
            throw new ArgumentException(
                $"{rays.Length} rays need {rays.Length} hit slots, and {hits.Length} were given",
                nameof(hits));
        }

        Span<StackEntry> stack = stackalloc StackEntry[MaxStack];
        for (int i = 0; i < rays.Length; i++)
        {
            hits[i] = TraceOne(rays[i], options.MinDistance, stack);
        }
    }

    /// <summary>
    /// The synchronous visibility batch.
    /// </summary>
    /// <param name="rays">The rays to trace.</param>
    /// <param name="hitBits">Receives one bit per ray, least-significant first.</param>
    /// <param name="options">The ray epsilon.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="hitBits"/> is too short to hold one bit per ray.
    /// </exception>
    public void TraceVisibility(
        ReadOnlySpan<Ray> rays, Span<ulong> hitBits, RayTraceOptions options)
    {
        int words = (rays.Length + 63) / 64;
        if (hitBits.Length < words)
        {
            throw new ArgumentException(
                $"{rays.Length} rays need {words} words of hit bits, and {hitBits.Length} "
                + "were given",
                nameof(hitBits));
        }

        hitBits[..words].Clear();
        Span<StackEntry> stack = stackalloc StackEntry[MaxStack];
        for (int i = 0; i < rays.Length; i++)
        {
            // The visibility answer comes off the same walk rather than a
            // cheaper any-hit one, and that is not laziness: this walk ALREADY
            // stops at the first hit in front-to-back order, so an any-hit
            // variant would do the same work and return less.
            if (TraceOne(rays[i], options.MinDistance, stack).IsHit)
            {
                hitBits[i >> 6] |= 1UL << (i & 63);
            }
        }
    }

    /// <summary>
    /// One ray, closest hit.
    /// </summary>
    /// <param name="ray">The ray.</param>
    /// <param name="minDistance">The ray epsilon, in units of the ray's length.</param>
    /// <returns>The surface hit and where, or <see cref="HitId.Missed"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The traversal stack overflowed, which means a malformed tree.
    /// </exception>
    /// <remarks>
    /// Stock's <c>CLightSurface::FindIntersection</c>
    /// Plus
    /// <c>EnumerateNodesAlongRay_R</c>, fused: the
    /// enumerator interface exists in stock so that three different callers can
    /// share one walk, and there is exactly one caller of this shape. Keeping
    /// the virtual call would cost an indirect branch per node and per leaf on
    /// the hottest path in vrad.
    /// </remarks>
    public HitId TraceOne(Ray ray, float minDistance)
    {
        Span<StackEntry> stack = stackalloc StackEntry[MaxStack];
        return TraceOne(ray, minDistance, stack);
    }

    /// <summary>
    /// One ray, closest hit, reusing a caller-owned traversal stack.
    /// </summary>
    /// <param name="ray">The ray.</param>
    /// <param name="minDistance">The ray epsilon, in units of the ray's length.</param>
    /// <param name="stack">Scratch, at least <see cref="MaxStack"/> long.</param>
    /// <returns>The surface hit and where, or <see cref="HitId.Missed"/>.</returns>
    /// <remarks>
    /// The stack is a parameter because a <c>stackalloc</c> inside the per-ray
    /// call is four kilobytes of frame set up six million times per map, and
    /// the JIT zero-initialises it unless told otherwise. One per batch is
    /// enough: a trace leaves nothing behind in it.
    /// </remarks>
    [SkipLocalsInit]
    private HitId TraceOne(Ray ray, float minDistance, Span<StackEntry> stack)
    {
        TraceContext c = default;

        // Every array is taken as a bare reference to its first element and
        // then addressed with Unsafe.Add. That is not gratuitous: the walk
        // indexes six arrays per node and per candidate, and a bounds check on
        // each is a compare and a branch on a path that runs six million times
        // per map. The indices are the BSP's own -- a node's children, a leaf's
        // face run -- and they are validated ONCE, at load, by
        // BspTraceGeometry.Build, which throws on any index the map does not
        // have. Checking them again per ray re-answers a question already
        // settled.
        c.Nodes = ref MemoryMarshal.GetArrayDataReference(_nodes);
        c.Surfaces = ref MemoryMarshal.GetArrayDataReference(_surfaces);
        c.NodeFaces = ref MemoryMarshal.GetArrayDataReference(_nodeFaces);
        c.LeafFaces = ref MemoryMarshal.GetArrayDataReference(_leafFaces);
        c.LeafStart = ref MemoryMarshal.GetArrayDataReference(_leafFaceStart);
        c.LeafCount = ref MemoryMarshal.GetArrayDataReference(_leafFaceCount);
        c.LeafTotal = _leafFaceStart.Length;

        // Stock's Ray_t is start + delta, and a fraction runs 0..1 along
        // delta. Ray is origin + direction with MaxDistance in lengths of
        // direction, so delta = direction * MaxDistance and the two fraction
        // spaces coincide.
        c.Ox = ray.OriginX;
        c.Oy = ray.OriginY;
        c.Oz = ray.OriginZ;
        c.Dx = ray.DirectionX * ray.MaxDistance;
        c.Dy = ray.DirectionY * ray.MaxDistance;
        c.Dz = ray.DirectionZ * ray.MaxDistance;
        c.HitFrac = 1.0f;
        c.MinFrac = minDistance;
        c.Surface = HitId.Miss;

        ref StackEntry stack0 = ref MemoryMarshal.GetReference(stack);
        int sp = 0;
        int node = 0;
        float start = 0.0f;
        float end = 1.0f;

        while (true)
        {
            // The `while (node >= 0)` that walks down through
            // every node the ray passes wholly on one side of. These push
            // nothing, and they are the common case.
            while (node >= 0)
            {
                ref TraceNode n = ref Unsafe.Add(ref c.Nodes, node);

                // Stock reads the component directly for
                // an axial plane, and that shortcut is worth 1.13x here -- see
                // TraceNode's remarks, which used to argue for dropping it.
                float startDotN;
                float deltaDotN;
                int axis = (int)((uint)n.Packed >> 30);
                if (axis == 3)
                {
                    startDotN = (c.Ox * n.Nx) + (c.Oy * n.Ny) + (c.Oz * n.Nz);
                    deltaDotN = (c.Dx * n.Nx) + (c.Dy * n.Ny) + (c.Dz * n.Nz);
                }
                else if (axis == 0)
                {
                    startDotN = c.Ox;
                    deltaDotN = c.Dx;
                }
                else if (axis == 1)
                {
                    startDotN = c.Oy;
                    deltaDotN = c.Dy;
                }
                else
                {
                    startDotN = c.Oz;
                    deltaDotN = c.Dz;
                }

                float front = startDotN + (start * deltaDotN) - n.Dist;
                float back = startDotN + (end * deltaDotN) - n.Dist;

                if (front <= -TestEpsilon && back <= -TestEpsilon)
                {
                    node = n.Child1;
                    continue;
                }

                if (front >= TestEpsilon && back >= TestEpsilon)
                {
                    node = n.Child0;
                    continue;
                }

                bool side = front < 0;

                float splitfrac;
                if (deltaDotN == 0.0f)
                {
                    splitfrac = 1.0f;
                }
                else
                {
                    splitfrac = (n.Dist - startDotN) / deltaDotN;
                    if (splitfrac < 0.0f)
                    {
                        splitfrac = 0.0f;
                    }
                    else if (splitfrac > 1.0f)
                    {
                        splitfrac = 1.0f;
                    }
                }

                if (sp >= MaxStack)
                {
                    throw new InvalidOperationException(
                        $"the BSP walk went deeper than {MaxStack} pending splits at node "
                        + $"{node}, which a tree built by vbsp cannot do; the tree is malformed");
                }

                // ONE entry, not two. Stock's split has two things pending
                // while the near subtree runs -- visiting the node itself, then
                // descending the far subtree -- and they always come together
                // in that order, so they are one record rather than two stack
                // slots and two dispatches.
                ref StackEntry e = ref Unsafe.Add(ref stack0, sp++);
                e.VisitNode = node;
                e.FarChild = side ? n.Child0 : n.Child1;
                e.Split = splitfrac;
                e.End = end;

                node = side ? n.Child1 : n.Child0;
                end = splitfrac;
            }

            if (!EnumerateLeaf(ref c, -node - 1, start, end))
            {
                break;
            }

            if (sp == 0)
            {
                break;
            }

            ref StackEntry popped = ref Unsafe.Add(ref stack0, --sp);
            if (!EnumerateNode(ref c, popped.VisitNode, popped.Split))
            {
                break;
            }

            node = popped.FarChild;
            start = popped.Split;
            end = popped.End;
        }

        return c.Surface == HitId.Miss ? HitId.Missed : new HitId(c.Surface, c.HitFrac);
    }

    /// <summary>
    /// <c>CLightSurface::EnumerateNode</c>.
    /// </summary>
    /// <param name="c">The ray and the best hit so far.</param>
    /// <param name="node">Which node.</param>
    /// <param name="f">The fraction at which the ray crosses the node's plane.</param>
    /// <returns>False to stop the whole walk.</returns>
    /// <remarks>
    /// Note what this does NOT do on the sky path: <c>m_HitFrac</c> is left at
    /// 1.0 even when a sky face is accepted (
    /// sets only <c>pSkySurface</c>). So a sky hit reports the full ray length
    /// rather than the distance to the sky surface, and that is reproduced
    /// rather than corrected: <c>CalcRayAmbientLighting</c> uses
    /// <c>m_HitFrac</c> to size the cone it samples with, and a sky hit's
    /// <c>scaleAvg</c> is forced to 1 anyway.
    /// </remarks>
    private bool EnumerateNode(ref TraceContext c, int node, float f)
    {
        ref TraceNode n = ref Unsafe.Add(ref c.Nodes, node);
        int packed = n.Packed;
        int solid = packed & 0x7FFF;
        int sky = (packed >> 15) & 0x7FFF;
        if ((solid | sky) == 0)
        {
            return true;
        }

        float px = c.Ox + (f * c.Dx);
        float py = c.Oy + (f * c.Dy);
        float pz = c.Oz + (f * c.Dz);

        ref int first = ref Unsafe.Add(ref c.NodeFaces, n.FirstFace);
        for (int i = 0; i < solid; i++)
        {
            int face = Unsafe.Add(ref first, i);
            if (TestPointAgainstSurface(in Unsafe.Add(ref c.Surfaces, face), px, py, pz))
            {
                c.HitFrac = f;
                c.Surface = face;
                return false;
            }
        }

        int skyHit = HitId.Miss;
        for (int i = 0; i < sky; i++)
        {
            int face = Unsafe.Add(ref first, solid + i);
            if (TestPointAgainstSkySurface(face, px, py, pz))
            {
                skyHit = face;
            }
        }

        // The assignment is unconditional in stock,
        // including the case where no sky face passed and m_pSurface is set
        // back to null -- which is safe only because nothing can have set it
        // earlier: any earlier hit returned false and ended the walk.
        c.Surface = skyHit;
        return skyHit == HitId.Miss;
    }

    /// <summary>
    /// <c>CLightSurface::EnumerateLeaf</c>,
    /// without the displacement clip.
    /// </summary>
    /// <param name="c">The ray and the best hit so far.</param>
    /// <param name="leaf">Which leaf.</param>
    /// <param name="start">The fraction the ray enters the leaf at.</param>
    /// <param name="end">The fraction it leaves at.</param>
    /// <returns>False to stop the whole walk.</returns>
    private static bool EnumerateLeaf(ref TraceContext c, int leaf, float start, float end)
    {
        if ((uint)leaf >= (uint)c.LeafTotal)
        {
            return true;
        }

        int count = Unsafe.Add(ref c.LeafCount, leaf);
        if (count == 0)
        {
            return true;
        }

        ref int first = ref Unsafe.Add(ref c.LeafFaces, Unsafe.Add(ref c.LeafStart, leaf));

        bool hit = false;
        for (int i = 0; i < count; i++)
        {
            int face = Unsafe.Add(ref first, i);
            ref TraceSurface s = ref Unsafe.Add(ref c.Surfaces, face);

            // Stock computes this dot product twice,
            // once for the backface cull and once as deltaDotN. Computing it
            // once is the same float: the operands and the order are identical,
            // so there is no rounding to preserve.
            float deltaDotN = (c.Dx * s.Nx) + (c.Dy * s.Ny) + (c.Dz * s.Nz);
            if (deltaDotN > 0)
            {
                continue;
            }

            float startDotN = (c.Ox * s.Nx) + (c.Oy * s.Ny) + (c.Oz * s.Nz);

            float front = startDotN + (start * deltaDotN) - s.Dist;
            float back = startDotN + (end * deltaDotN) - s.Dist;

            bool side = front < 0;
            if ((back < 0) == side)
            {
                continue;
            }

            float f = front / (front - back);
            float mid = (start * (1.0f - f)) + (end * f);
            if (mid >= c.HitFrac || mid < c.MinFrac)
            {
                continue;
            }

            float px = c.Ox + (mid * c.Dx);
            float py = c.Oy + (mid * c.Dy);
            float pz = c.Oz + (mid * c.Dz);

            if (TestPointAgainstSurface(in s, px, py, pz))
            {
                c.HitFrac = mid;
                c.Surface = face;
                hit = true;
            }
        }

        return !hit;
    }

    /// <summary>
    /// <c>CLightSurface::TestPointAgainstSurface</c>,
    /// Is the point inside the face's lightmap
    /// rectangle?
    /// </summary>
    /// <param name="s">The face's gathered data.</param>
    /// <param name="px">The point, x.</param>
    /// <param name="py">The point, y.</param>
    /// <param name="pz">The point, z.</param>
    /// <returns>True when the point lands on lit luxels.</returns>
    /// <remarks>
    /// This is a LIGHTMAP-space test, not a polygon one, and that is the
    /// surprise in this tracer rather than a detail: stock never clips the ray
    /// against the face's winding. A face is "hit" when the intersection with
    /// its PLANE falls inside the lightmap rectangle, which is a conservative
    /// superset of the face and can accept a point that is off the polygon.
    /// Stock's own comment concedes the approximation ("assuming a square
    /// lightmap (FIXME: which ain't always the case)"). Reproduced, because
    /// leaf ambient goes on to SAMPLE that rectangle and a tighter test would
    /// change every ambient cube in the map.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TestPointAgainstSurface(
        ref readonly TraceSurface s, float px, float py, float pz)
    {
        // The SURF_NOLIGHT test is folded into the
        // gathered data: a face with no lightmap gets a zero-size rectangle
        // there, which no point can be inside.
        float sc = (px * s.Sx) + (py * s.Sy) + (pz * s.Sz) + s.So;
        float tc = (px * s.Tx) + (py * s.Ty) + (pz * s.Tz) + s.To;

        if (sc < s.MinS || tc < s.MinT)
        {
            return false;
        }

        float ds = sc - s.MinS;
        float dt = tc - s.MinT;
        return ds <= s.SizeS && dt <= s.SizeT;
    }

    /// <summary>
    /// <c>CLightSurface::TestPointAgainstSkySurface</c>,
    /// <c>PointInWinding</c> against the
    /// face's polygon.
    /// </summary>
    /// <param name="face">Which face.</param>
    /// <param name="px">The point, x.</param>
    /// <param name="py">The point, y.</param>
    /// <param name="pz">The point, z.</param>
    /// <returns>True when the point is inside the sky face.</returns>
    /// <remarks>
    /// The <c>#else</c> branch, which is the one
    /// compiled: cross the first edge with the vector to the point, normalise,
    /// and require every other edge's cross to agree in sign with it. The
    /// normalisations go through <see cref="Vec3.NormaliseLikeStock"/> because
    /// the comparison is against zero and a near-edge point is decided by the
    /// estimate's last bits.
    /// </remarks>
    private bool TestPointAgainstSkySurface(int face, float px, float py, float pz)
    {
        int start = _skyStart[face];
        int count = _skyCount[face];
        if (count < 2)
        {
            return false;
        }

        ReadOnlySpan<Vec3> p = _skyPoints.AsSpan(start, count);
        Vec3 pt = new(px, py, pz);

        Vec3 toPt = pt - p[0];
        Vec3 edge = p[1] - p[0];
        (Vec3 testCross, _) = Vec3.Cross(edge, toPt).NormaliseLikeStock();

        for (int i = 1; i < count; i++)
        {
            toPt = pt - p[i];
            edge = p[(i + 1) % count] - p[i];
            (Vec3 cross, _) = Vec3.Cross(edge, toPt).NormaliseLikeStock();
            if (Vec3.Dot(cross, testCross) < 0.0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One ray's state and the arrays it walks, gathered so that the two
    /// callbacks reach them through a register rather than through
    /// <c>this</c>.
    /// </summary>
    /// <remarks>
    /// A <c>ref struct</c> with <c>ref</c> fields, so the array references are
    /// interior pointers the GC still tracks: nothing here is pinned and
    /// nothing is unmanaged. It exists because the alternative -- reloading
    /// six array fields off <c>this</c> inside every node and candidate loop --
    /// is what the JIT does otherwise, since a call to another method may
    /// change a field.
    /// </remarks>
    private ref struct TraceContext
    {
        /// <summary>The first node.</summary>
        public ref TraceNode Nodes;

        /// <summary>The first gathered surface record.</summary>
        public ref TraceSurface Surfaces;

        /// <summary>The first node-face candidate.</summary>
        public ref int NodeFaces;

        /// <summary>The first leaf-face candidate.</summary>
        public ref int LeafFaces;

        /// <summary>The first leaf's candidate run offset.</summary>
        public ref int LeafStart;

        /// <summary>The first leaf's candidate run length.</summary>
        public ref int LeafCount;

        /// <summary>How many leaves there are, for the one bounds test kept.</summary>
        public int LeafTotal;

        /// <summary>The ray's origin, x.</summary>
        public float Ox;

        /// <summary>The ray's origin, y.</summary>
        public float Oy;

        /// <summary>The ray's origin, z.</summary>
        public float Oz;

        /// <summary>The ray's delta, x.</summary>
        public float Dx;

        /// <summary>The ray's delta, y.</summary>
        public float Dy;

        /// <summary>The ray's delta, z.</summary>
        public float Dz;

        /// <summary>Stock's <c>m_HitFrac</c>: the closest hit so far.</summary>
        public float HitFrac;

        /// <summary>The ray epsilon.</summary>
        public float MinFrac;

        /// <summary>Stock's <c>m_pSurface</c>, as an index.</summary>
        public int Surface;
    }

    /// <summary>
    /// One suspended split: the node to visit when the near subtree is done,
    /// and the far subtree to descend after it.
    /// </summary>
    /// <remarks>
    /// Sixteen bytes and mutable, written through a <c>ref</c> rather than
    /// constructed and copied. Both halves of a split are in one record
    /// because stock always does them in this order and never one without the
    /// other.
    /// </remarks>
    private struct StackEntry
    {
        /// <summary>The node whose own faces are tested between the halves.</summary>
        public int VisitNode;

        /// <summary>The subtree on the far side of the splitting plane.</summary>
        public int FarChild;

        /// <summary>The fraction the ray crosses the splitting plane at.</summary>
        public float Split;

        /// <summary>The fraction the suspended span ends at.</summary>
        public float End;
    }
}
