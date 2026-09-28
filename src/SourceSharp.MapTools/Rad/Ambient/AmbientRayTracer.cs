//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// What <c>CLightSurface::FindIntersection</c> leaves behind: the surface, the
/// fraction, and the luxel coordinate when there is one.
/// </summary>
/// <param name="Surface">The face hit, or -1.</param>
/// <param name="Fraction"><c>m_HitFrac</c>: 1 for a sky hit, as stock leaves it.</param>
/// <param name="HasLuxel"><c>m_bHasLuxel</c>.</param>
/// <param name="LuxelS"><c>m_LuxelCoord.x</c>, relative to the face's lightmap mins.</param>
/// <param name="LuxelT"><c>m_LuxelCoord.y</c>.</param>
public readonly record struct AmbientHit(int Surface, float Fraction, bool HasLuxel, float LuxelS, float LuxelT)
{
    /// <summary>Whether the ray found any surface.</summary>
    public bool IsHit => Surface >= 0;
}

/// <summary>
/// <c>CLightSurface</c> over
/// <c>EnumerateNodesAlongRay</c>, WITH the
/// displacement clip that ends every leaf.
/// </summary>
/// <remarks>
/// <para>
/// The walk is 4a2's <see cref="BspSurfaceTracer"/> line for line, over the
/// same <see cref="BspTraceGeometry"/>. It is a separate type because leaf
/// ambient needs two things the generic closest-hit seam does not carry: the
/// hit's LUXEL COORDINATE (a displacement's comes from barycentric
/// interpolation of its vertices', not from the face's lightmap vectors), and
/// the per-work-item <see cref="DispTestedScratch"/> stock keeps per thread.
/// </para>
/// <para>
/// Two behaviours kept from stock that look like mistakes: a displacement hit
/// is accepted whenever it is nearer than the best so far, even when it lies
/// beyond the leaf being enumerated (the clip has no range test); and a sky
/// hit leaves <c>m_HitFrac</c> at 1.
/// </para>
/// </remarks>
public sealed class AmbientRayTracer
{
    private const float TestEpsilon = 0.03125f;
    private const int MaxStack = 256;

    private readonly BspTraceGeometry _geometry;
    private readonly DispCollisionSet _disps;
    private readonly TraceNode[] _nodes;
    private readonly TraceSurface[] _surfaces;
    private readonly int[] _nodeFaces;
    private readonly int[] _leafFaces;
    private readonly int[] _leafFaceStart;
    private readonly int[] _leafFaceCount;
    private readonly Vec3[] _skyPoints;
    private readonly int[] _skyStart;
    private readonly int[] _skyCount;

    /// <summary><see cref="BspTraceGeometry.StockSkyNormalise"/>, copied like the arrays.</summary>
    private readonly bool _stockSkyNormalise;

    /// <summary>Wraps a flattened tree and the map's displacements.</summary>
    /// <param name="geometry">The tree.</param>
    /// <param name="displacements">The displacements, or an empty set.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AmbientRayTracer(BspTraceGeometry geometry, DispCollisionSet displacements)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(displacements);
        _geometry = geometry;
        _disps = displacements;
        _nodes = geometry.Nodes;
        _surfaces = geometry.Surfaces;
        _nodeFaces = geometry.NodeFaces;
        _leafFaces = geometry.LeafFaces;
        _leafFaceStart = geometry.LeafFaceStart;
        _leafFaceCount = geometry.LeafFaceCount;
        _skyPoints = geometry.SkyPoints;
        _skyStart = geometry.SkyStart;
        _skyCount = geometry.SkyCount;
        _stockSkyNormalise = geometry.StockSkyNormalise;
    }

    /// <summary>The tree.</summary>
    public BspTraceGeometry Geometry => _geometry;

    /// <summary>The displacements.</summary>
    public DispCollisionSet Displacements => _disps;

    /// <summary>
    /// <c>FindIntersection</c>.
    /// </summary>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray delta (end - start).</param>
    /// <param name="scratch">This work item's displacement scratch.</param>
    /// <returns>The hit.</returns>
    public AmbientHit Trace(Vec3 start, Vec3 delta, DispTestedScratch scratch)
    {
        AmbientHit state = new(-1, 1.0f, false, 0, 0);
        _ = TraceFrom(start, delta, scratch, ref state);

        // From a fresh enumerator, a walk that did not stop leaves m_pSurface null.
        return state;
    }

    /// <summary>
    /// <c>FindIntersection</c> on an enumerator that has ALREADY been used: the
    /// walk starts from <paramref name="state"/>'s <c>m_HitFrac</c>,
    /// <c>m_pSurface</c> and <c>m_bHasLuxel</c> rather than from a fresh
    /// <c>CLightSurface</c>.
    /// </summary>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray delta.</param>
    /// <param name="scratch">This work item's displacement scratch.</param>
    /// <param name="state">The enumerator's state, updated in place.</param>
    /// <returns>Whether the walk stopped on a surface (<c>FindIntersection</c>'s result).</returns>
    /// <remarks>
    /// <c>ComputeIndirectLightingAtPoint</c>
    /// constructs ONE <c>CLightSurface</c> and calls <c>FindIntersection</c>
    /// for every sample direction, and <c>FindIntersection</c> resets nothing
    /// but the displacement counter -- so each ray only accepts leaf and
    /// displacement hits nearer than the previous ray's. See
    /// <see cref="Options.StockQuirk.IndirectSurfaceEnumeratorReused"/>.
    /// <para>
    /// AggressiveOptimization here and on the walk's other hot methods
    /// (EnumerateNode, EnumerateLeaf, DispCollisionSet.ClipRayInLeaf,
    /// DispCollisionTree.RayInsideBox, RayAmbientLighting.Accumulate): detail
    /// prop lighting is the first stage to trace through this walk, and on
    /// 2fort it is short enough (0.15 s in stock) that it ran mostly in tier-0
    /// code while the JIT promoted these (441 compilations inside its 0.25 s
    /// window at 32 threads, p5-trace's EventPipe split). Compiling them fully
    /// optimised up front made that stage 12-33 % faster and left leaf
    /// ambient's steady state unchanged (p5-trace-findings.md).
    /// </para>
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TraceFrom(Vec3 start, Vec3 delta, DispTestedScratch scratch, ref AmbientHit state)
    {
        ArgumentNullException.ThrowIfNull(scratch);

        // FindIntersection: StartRayTest, then the walk.
        scratch.StartRayTest();

        Span<Entry> stack = stackalloc Entry[MaxStack];
        Ctx c = default;
        c.Start = start;
        c.Delta = delta;
        c.HitFrac = state.Fraction;
        c.Surface = state.Surface;
        c.HasLuxel = state.HasLuxel;
        c.LuxelS = state.LuxelS;
        c.LuxelT = state.LuxelT;
        c.Scratch = scratch;
        bool stopped = false;

        ref TraceNode nodes0 = ref MemoryMarshal.GetArrayDataReference(_nodes);
        int sp = 0;
        int node = 0;
        float fs = 0.0f;
        float fe = 1.0f;

        while (true)
        {
            while (node >= 0)
            {
                ref TraceNode n = ref Unsafe.Add(ref nodes0, node);
                float startDotN;
                float deltaDotN;
                int axis = (int)((uint)n.Packed >> 30);
                if (axis == 3)
                {
                    startDotN = (start.X * n.Nx) + (start.Y * n.Ny) + (start.Z * n.Nz);
                    deltaDotN = (delta.X * n.Nx) + (delta.Y * n.Ny) + (delta.Z * n.Nz);
                }
                else if (axis == 0)
                {
                    startDotN = start.X;
                    deltaDotN = delta.X;
                }
                else if (axis == 1)
                {
                    startDotN = start.Y;
                    deltaDotN = delta.Y;
                }
                else
                {
                    startDotN = start.Z;
                    deltaDotN = delta.Z;
                }

                float front = startDotN + (fs * deltaDotN) - n.Dist;
                float back = startDotN + (fe * deltaDotN) - n.Dist;

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
                        $"the BSP walk went deeper than {MaxStack} pending splits; the tree is malformed");
                }

                ref Entry e = ref stack[sp++];
                e.VisitNode = node;
                e.FarChild = side ? n.Child0 : n.Child1;
                e.Split = splitfrac;
                e.End = fe;

                node = side ? n.Child1 : n.Child0;
                fe = splitfrac;
            }

            if (!EnumerateLeaf(ref c, -node - 1, fs, fe))
            {
                stopped = true;
                break;
            }

            if (sp == 0)
            {
                break;
            }

            ref Entry popped = ref stack[--sp];
            if (!EnumerateNode(ref c, popped.VisitNode, popped.Split))
            {
                stopped = true;
                break;
            }

            node = popped.FarChild;
            fs = popped.Split;
            fe = popped.End;
        }

        state = new AmbientHit(c.Surface, c.HitFrac, c.HasLuxel, c.LuxelS, c.LuxelT);
        return stopped;
    }

    /// <summary><c>CLightSurface::EnumerateNode</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool EnumerateNode(ref Ctx c, int node, float f)
    {
        ref TraceNode n = ref At(_nodes, node);
        int packed = n.Packed;
        int solid = packed & 0x7FFF;
        int sky = (packed >> 15) & 0x7FFF;
        if ((solid | sky) == 0)
        {
            // m_pSurface = pSkySurface (null); nothing earlier can have set it.
            c.Surface = -1;
            return true;
        }

        // VectorMA( ray.m_Start, f, ray.m_Delta, pt ).
        float px = c.Start.X + (f * c.Delta.X);
        float py = c.Start.Y + (f * c.Delta.Y);
        float pz = c.Start.Z + (f * c.Delta.Z);

        for (int i = 0; i < solid; i++)
        {
            int face = At(_nodeFaces, n.FirstFace + i);
            if (TestPointAgainstSurface(in At(_surfaces, face), px, py, pz, out float ds, out float dt))
            {
                c.HitFrac = f;
                c.Surface = face;
                c.HasLuxel = true;
                c.LuxelS = ds;
                c.LuxelT = dt;
                return false;
            }
        }

        int skyHit = -1;
        for (int i = 0; i < sky; i++)
        {
            int face = At(_nodeFaces, n.FirstFace + solid + i);
            if (TestPointAgainstSkySurface(face, px, py, pz))
            {
                skyHit = face;
            }
        }

        c.Surface = skyHit;
        return skyHit == -1;
    }

    /// <summary><c>CLightSurface::EnumerateLeaf</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool EnumerateLeaf(ref Ctx c, int leaf, float start, float end)
    {
        bool hit = false;
        if ((uint)leaf < (uint)_leafFaceStart.Length)
        {
            int count = At(_leafFaceCount, leaf);
            int first = At(_leafFaceStart, leaf);
            for (int i = 0; i < count; i++)
            {
                int face = At(_leafFaces, first + i);
                ref TraceSurface s = ref At(_surfaces, face);

                float deltaDotN = (c.Delta.X * s.Nx) + (c.Delta.Y * s.Ny) + (c.Delta.Z * s.Nz);
                if (deltaDotN > 0)
                {
                    continue;
                }

                float startDotN = (c.Start.X * s.Nx) + (c.Start.Y * s.Ny) + (c.Start.Z * s.Nz);
                float front = startDotN + (start * deltaDotN) - s.Dist;
                float back = startDotN + (end * deltaDotN) - s.Dist;

                bool side = front < 0;
                if ((back < 0) == side)
                {
                    continue;
                }

                float f = front / (front - back);
                float mid = (start * (1.0f - f)) + (end * f);
                if (mid >= c.HitFrac)
                {
                    continue;
                }

                float px = c.Start.X + (mid * c.Delta.X);
                float py = c.Start.Y + (mid * c.Delta.Y);
                float pz = c.Start.Z + (mid * c.Delta.Z);

                if (TestPointAgainstSurface(in s, px, py, pz, out float ds, out float dt))
                {
                    c.HitFrac = mid;
                    c.Surface = face;
                    c.HasLuxel = true;
                    c.LuxelS = ds;
                    c.LuxelT = dt;
                    hit = true;
                }
            }
        }

        // Every leaf ends with the displacements.
        if (_disps.Count > 0)
        {
            _disps.ClipRayInLeaf(c.Scratch!, c.Start, c.Delta, leaf, out DispRayHit disp);
            if (disp.Distance < c.HitFrac)
            {
                c.HitFrac = disp.Distance;
                c.Surface = disp.Face;
                c.LuxelS = disp.LuxelS;
                c.LuxelT = disp.LuxelT;
                c.HasLuxel = true;
                hit = true;
            }
        }

        return !hit;
    }

    /// <summary>
    /// <c>TestPointAgainstSurface</c>, with the
    /// luxel coordinate it records.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TestPointAgainstSurface(
        ref readonly TraceSurface s, float px, float py, float pz, out float ds, out float dt)
    {
        float sc = (px * s.Sx) + (py * s.Sy) + (pz * s.Sz) + s.So;
        float tc = (px * s.Tx) + (py * s.Ty) + (pz * s.Tz) + s.To;
        ds = 0;
        dt = 0;

        if (sc < s.MinS || tc < s.MinT)
        {
            return false;
        }

        ds = sc - s.MinS;
        dt = tc - s.MinT;
        return ds <= s.SizeS && dt <= s.SizeT;
    }

    /// <summary>
    /// <c>TestPointAgainstSkySurface</c>:
    /// <c>PointInWinding</c>.
    /// </summary>
    /// <remarks>
    /// As <see cref="BspSurfaceTracer"/>'s: the crosses are normalised with
    /// stock's estimate only under <see cref="Options.StockQuirk.SkyWindingNormalise"/>.
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
        (Vec3 testCross, _) = BspTraceGeometry.Normalise(Vec3.Cross(edge, toPt), _stockSkyNormalise);

        for (int i = 1; i < count; i++)
        {
            toPt = pt - p[i];
            edge = p[(i + 1) % count] - p[i];
            (Vec3 cross, _) = BspTraceGeometry.Normalise(Vec3.Cross(edge, toPt), _stockSkyNormalise);
            if (Vec3.Dot(cross, testCross) < 0.0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// An unchecked element reference. Every index the walk uses comes from the
    /// BSP itself and was validated once by <see cref="BspTraceGeometry.Build(SourceSharp.MapFormats.Bsp.BspData, Options.ComplianceOptions)"/>
    /// (which throws on any index the map does not have), so the per-access
    /// bounds check re-answers a settled question on the hottest path in vrad.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref T At<T>(T[] array, int index) =>
        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), index);

    private ref struct Ctx
    {
        public Vec3 Start;
        public Vec3 Delta;
        public float HitFrac;
        public int Surface;
        public bool HasLuxel;
        public float LuxelS;
        public float LuxelT;
        public DispTestedScratch? Scratch;
    }

    private struct Entry
    {
        public int VisitNode;
        public int FarChild;
        public float Split;
        public float End;
    }
}
