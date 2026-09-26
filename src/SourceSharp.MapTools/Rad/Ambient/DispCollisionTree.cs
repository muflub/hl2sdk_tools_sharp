//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// What a ray found on one displacement (<c>RayDispOutput_t</c>, plus what
/// <c>DispRayDistance_EnumerateElement</c> derives from it).
/// </summary>
/// <param name="Distance">The hit, as a fraction of the ray's delta.</param>
/// <param name="Face">The displacement's base face (<c>GetParentIndex</c>).</param>
/// <param name="LuxelS">The luxel coordinate at the hit, s.</param>
/// <param name="LuxelT">The luxel coordinate at the hit, t.</param>
/// <param name="Normal">The hit triangle's unit normal.</param>
public readonly record struct DispRayHit(float Distance, int Face, float LuxelS, float LuxelT, Vec3 Normal);

/// <summary>
/// The ray half of one displacement's collision tree: <c>CDispCollTree</c>
/// Plus the luxel coordinates
/// <c>CVRADDispColl::Create</c> adds.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>AABBTree_Ray( ray, RayDispOutput_t&amp; )</c> is ported: that is
/// the query <c>ClipRayToDispInLeaf</c> makes for both leaf ambient callers
/// The quad tree is kept -- four child boxes per
/// node, two triangles per leaf, leaves in Morton order -- because it decides
/// WHICH triangles are tested, and the closest-hit comparison is strict, so on
/// an exact tie the earlier-listed triangle keeps the hit.
/// </para>
/// <para>
/// IMMUTABLE after construction, so one instance serves every worker; the
/// per-ray state stock keeps in <c>s_DispTested[iThread]</c> lives in the
/// caller's <see cref="DispTestedScratch"/> instead.
/// </para>
/// </remarks>
public sealed class DispCollisionTree
{
    /// <summary><c>DISPCOLL_DIST_EPSILON</c>.</summary>
    public const float DistEpsilon = 0.03125f;

    /// <summary><c>CCoreDispInfo::SURF_NORAY_COLL</c>.</summary>
    public const int SurfNoRayColl = 0x8;

    /// <summary><c>MASK_OPAQUE</c>.</summary>
    public const int MaskOpaque = 0x1 | 0x4000 | 0x80;

    private readonly Vec3[] _verts;
    private readonly int[] _tris;
    private readonly float[] _luxelS;
    private readonly float[] _luxelT;

    // Per inner node, the four child boxes.
    private readonly Vec3[] _nodeMins;
    private readonly Vec3[] _nodeMaxs;

    // Per leaf (Morton index), its two triangles.
    private readonly int[] _leafTris;

    // Per inner node, its four child boxes again, as six four-lane rows
    // (min x, y, z, max x, y, z): IntersectRayWithFourBoxes' own layout, so the
    // four boxes are tested in one pass. The same floats as _nodeMins/_nodeMaxs.
    private readonly float[] _childBoxes;

    // Per triangle, what the barycentric test reads, in its (0, 2, 1) order:
    // the first vertex, then the two edges from it. The same subtractions the
    // test made per ray, made once.
    private readonly Vec3[] _triGeometry;

    private readonly int _nodeCount;

    /// <summary>Builds the tree from a created displacement.</summary>
    /// <param name="disp">The displacement, after <see cref="CoreDispInfo.Create"/>.</param>
    /// <param name="face">Its base face (<c>pSurf-&gt;GetHandle()</c>).</param>
    /// <remarks>
    /// <c>AABBTree_Create</c>: copy, leaves,
    /// then bounds.
    /// </remarks>
    public DispCollisionTree(CoreDispInfo disp, int face)
    {
        ArgumentNullException.ThrowIfNull(disp);

        Face = face;
        Power = disp.Power;
        Flags = disp.Surface.Flags;
        Contents = disp.Surface.Contents;

        // AABBTree_CopyDispData.
        int size = disp.Size;
        _verts = new Vec3[size];
        _luxelS = new float[size];
        _luxelT = new float[size];
        for (int i = 0; i < size; i++)
        {
            _verts[i] = disp.Vert(i);

            // GetLuxelCoord(0, iVert).
            DispUv luxel = disp.LuxelCoord(0, i);
            _luxelS[i] = luxel.X;
            _luxelT[i] = luxel.Y;
        }

        int triCount = disp.TriCount;
        _tris = new int[triCount * 3];
        ReadOnlySpan<ushort> indices = disp.TriIndices;
        for (int i = 0; i < triCount * 3; i++)
        {
            _tris[i] = indices[i];
        }

        int width = (1 << Power) + 1;
        int leafCount = (width - 1) * (width - 1);
        _nodeCount = NodesCalcCount(Power) - leafCount;
        _nodeMins = new Vec3[_nodeCount * 4];
        _nodeMaxs = new Vec3[_nodeCount * 4];
        _leafTris = new int[leafCount * 2];
        _childBoxes = new float[_nodeCount * 24];
        _triGeometry = new Vec3[triCount * 3];
        for (int tri = 0; tri < triCount; tri++)
        {
            Vec3 a = _verts[_tris[tri * 3]];
            _triGeometry[tri * 3] = a;
            _triGeometry[(tri * 3) + 1] = _verts[_tris[(tri * 3) + 2]] - a;
            _triGeometry[(tri * 3) + 2] = _verts[_tris[(tri * 3) + 1]] - a;
        }

        // AABBTree_CreateLeafs.
        for (int hgt = 0; hgt < width - 1; hgt++)
        {
            for (int wid = 0; wid < width - 1; wid++)
            {
                int leaf = IndexFromComponents(wid, hgt);
                int tri = ((hgt * (width - 1)) + wid) * 2;
                _leafTris[leaf * 2] = tri;
                _leafTris[(leaf * 2) + 1] = tri + 1;
            }
        }

        // AABBTree_CalcBounds.
        if (size == 0 || _nodeCount == 0)
        {
            Mins = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            Maxs = new Vec3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
            return;
        }

        GenerateBoxes(0, out Vec3 mins, out Vec3 maxs);
        for (int node = 0; node < _nodeCount; node++)
        {
            for (int i = 0; i < 4; i++)
            {
                Vec3 lo = _nodeMins[(node * 4) + i];
                Vec3 hi = _nodeMaxs[(node * 4) + i];
                int row = node * 24;
                _childBoxes[row + i] = lo.X;
                _childBoxes[row + 4 + i] = lo.Y;
                _childBoxes[row + 8 + i] = lo.Z;
                _childBoxes[row + 12 + i] = hi.X;
                _childBoxes[row + 16 + i] = hi.Y;
                _childBoxes[row + 20 + i] = hi.Z;
            }
        }

        // "Bloat a little." INCLUDE_SURFACE_IN_BOUNDS is 0.
        Mins = new Vec3(mins.X - 1.0f, mins.Y - 1.0f, mins.Z - 1.0f);
        Maxs = new Vec3(maxs.X + 1.0f, maxs.Y + 1.0f, maxs.Z + 1.0f);
    }

    /// <summary>The displacement's base face.</summary>
    public int Face { get; }

    /// <summary>The displacement power.</summary>
    public int Power { get; }

    /// <summary>The surface flags (<c>SURF_*</c> from <c>minTess</c>).</summary>
    public int Flags { get; }

    /// <summary><c>ddispinfo_t::contents</c>.</summary>
    public int Contents { get; }

    /// <summary>The bloated bounds (<c>GetBounds</c>).</summary>
    public Vec3 Mins { get; }

    /// <summary>The bloated bounds (<c>GetBounds</c>).</summary>
    public Vec3 Maxs { get; }

    /// <summary>How many vertices.</summary>
    public int VertexCount => _verts.Length;

    /// <summary>
    /// <c>Nodes_CalcCount</c>: nodes including leaves.
    /// </summary>
    /// <param name="power">The power.</param>
    /// <returns>The count.</returns>
    public static int NodesCalcCount(int power) => (1 << ((power + 1) << 1)) / 3;

    /// <summary>
    /// <c>Nodes_GetIndexFromComponents</c>: bit
    /// interleave, x in the even bits.
    /// </summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>The Morton index.</returns>
    public static int IndexFromComponents(int x, int y)
    {
        int index = 0;
        for (int shift = 0; x != 0; shift += 2, x >>= 1)
        {
            index |= (x & 1) << shift;
        }

        for (int shift = 1; y != 0; shift += 2, y >>= 1)
        {
            index |= (y & 1) << shift;
        }

        return index;
    }

    /// <summary>
    /// <c>AABBTree_Ray( ray, RayDispOutput_t&amp; )</c>
    /// then what
    /// <c>DispRayDistance_EnumerateElement</c> derives from a hit
    /// </summary>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray delta.</param>
    /// <param name="hit">The hit, when there is one.</param>
    /// <returns>Whether the ray hit.</returns>
    /// <remarks>
    /// The flag test runs before the box test here, where stock runs it after:
    /// both only return false, so the order cannot change an answer.
    /// </remarks>
    [SkipLocalsInit]
    public bool Ray(Vec3 start, Vec3 delta, out DispRayHit hit)
    {
        hit = default;

        if ((Flags & SurfNoRayColl) != 0 || (Contents & MaskOpaque) == 0)
        {
            return false;
        }

        if (!IsBoxIntersectingRay(Mins, Maxs, start, delta, DistEpsilon))
        {
            return false;
        }

        return RayInsideBox(start, delta, out hit);
    }

    /// <summary>
    /// <see cref="Ray"/> for a ray already known to pass
    /// <see cref="IsBoxIntersectingRay"/> against <see cref="Mins"/> and
    /// <see cref="Maxs"/>.
    /// </summary>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray delta.</param>
    /// <param name="hit">The hit, when there is one.</param>
    /// <returns>Whether the ray hit.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal bool RayInsideBox(Vec3 start, Vec3 delta, out DispRayHit hit)
    {
        hit = default;

        if ((Flags & SurfNoRayColl) != 0 || (Contents & MaskOpaque) == 0)
        {
            return false;
        }

        // Ray_t::InvDelta.
        Vec3 invDelta = new(
            delta.X != 0.0f ? 1.0f / delta.X : float.MaxValue,
            delta.Y != 0.0f ? 1.0f / delta.Y : float.MaxValue,
            delta.Z != 0.0f ? 1.0f / delta.Z : float.MaxValue);

        float bestDist = float.MaxValue;
        float bestU = -1.0f;
        float bestV = -1.0f;
        int bestTri = -1;

        // AABBTree_TreeTrisRayBarycentricTest.
        Span<int> list = stackalloc int[MaxAabbList];
        int maxIndex = 0;
        int listIndex = BuildRayLeafList(list, ref maxIndex, start, invDelta);

        for (; listIndex <= maxIndex; listIndex++)
        {
            int leaf = list[listIndex] - _nodeCount;
            for (int k = 0; k < 2; k++)
            {
                int tri = _leafTris[(leaf * 2) + k];

                // The triangle is passed as (0, 2, 1): the stored edges are
                // v2 - v0 and v1 - v0.
                if (IntersectBarycentricEdges(
                        start, delta, _triGeometry[tri * 3], _triGeometry[(tri * 3) + 1], _triGeometry[(tri * 3) + 2],
                        out float u, out float v, out float t)
                    && u >= 0.0f && v >= 0.0f && (u + v) <= 1.0f
                    && t > 0.0f && t < bestDist)
                {
                    bestTri = tri;
                    bestU = u;
                    bestV = v;
                    bestDist = t;
                }
            }
        }

        if (bestTri < 0)
        {
            return false;
        }

        // NdxVerts = tri verts 0, 2, 1.
        int n0 = _tris[bestTri * 3];
        int n1 = _tris[(bestTri * 3) + 2];
        int n2 = _tris[(bestTri * 3) + 1];

        // ComputePointFromBarycentric:
        // pt = v0 + u*(v1-v0); pt = pt + v*(v2-v0).
        float eus = _luxelS[n1] - _luxelS[n0];
        float eut = _luxelT[n1] - _luxelT[n0];
        float evs = _luxelS[n2] - _luxelS[n0];
        float evt = _luxelT[n2] - _luxelT[n0];
        float s = _luxelS[n0] + (bestU * eus);
        float tt = _luxelT[n0] + (bestU * eut);
        s += bestV * evs;
        tt += bestV * evt;

        Vec3 e0 = _verts[n1] - _verts[n0];
        Vec3 e1 = _verts[n2] - _verts[n0];
        (Vec3 normal, _) = Vec3.Cross(e0, e1).Normalise();

        hit = new DispRayHit(bestDist, Face, s, tt, normal);
        return true;
    }

    /// <summary><c>MAX_AABB_LIST</c>.</summary>
    private const int MaxAabbList = 344;

    /// <summary>
    /// <c>BuildRayLeafList</c>: breadth-first
    /// over the quad tree, children in order 0..3.
    /// </summary>
    private int BuildRayLeafList(Span<int> list, ref int maxIndex, Vec3 start, Vec3 invDelta)
    {
        list[0] = 0;
        int listIndex = 0;
        maxIndex = 0;
        while (listIndex <= maxIndex)
        {
            int node = list[listIndex];
            if (node >= _nodeCount)
            {
                return listIndex;
            }

            listIndex++;
            int child = (node << 2) + 1;
            int hits = Sse.IsSupported
                ? RayHitsFourBoxes(start, invDelta, node)
                : RayHitsFourBoxesScalar(start, invDelta, node);
            for (int i = 0; i < 4; i++)
            {
                if ((hits & (1 << i)) != 0)
                {
                    list[++maxIndex] = child + i;
                }
            }
        }

        return listIndex;
    }

    /// <summary>
    /// One lane of <c>IntersectRayWithFourBoxes</c>,
    /// with the ray extents at <see cref="DistEpsilon"/> (a point ray,
    /// </summary>
    private static bool RayHitsBox(Vec3 start, Vec3 invDelta, Vec3 mins, Vec3 maxs)
    {
        float lo0 = ((mins.X - start.X) - DistEpsilon) * invDelta.X;
        float hi0 = ((maxs.X - start.X) + DistEpsilon) * invDelta.X;
        float lo1 = ((mins.Y - start.Y) - DistEpsilon) * invDelta.Y;
        float hi1 = ((maxs.Y - start.Y) + DistEpsilon) * invDelta.Y;
        float lo2 = ((mins.Z - start.Z) - DistEpsilon) * invDelta.Z;
        float hi2 = ((maxs.Z - start.Z) + DistEpsilon) * invDelta.Z;

        // maximum/minimum are maxps/minps: a > b ? a : b.
        float exit0 = Max(lo0, hi0), entry0 = Min(lo0, hi0);
        float exit1 = Max(lo1, hi1), entry1 = Min(lo1, hi1);
        float exit2 = Max(lo2, hi2), entry2 = Min(lo2, hi2);

        float boxEntry = Max(Max(entry0, entry1), entry2);
        float boxExit = Min(Min(exit0, exit1), exit2);

        boxEntry = Max(boxEntry, 0.0f);
        boxExit = Min(boxExit, 1.0f);
        return boxEntry <= boxExit;
    }

    private int RayHitsFourBoxesScalar(Vec3 start, Vec3 invDelta, int node)
    {
        int hits = 0;
        for (int i = 0; i < 4; i++)
        {
            if (RayHitsBox(start, invDelta, _nodeMins[(node * 4) + i], _nodeMaxs[(node * 4) + i]))
            {
                hits |= 1 << i;
            }
        }

        return hits;
    }

    /// <summary>
    /// <see cref="RayHitsBox"/> for a node's four children at once, one per
    /// lane: <c>IntersectRayWithFourBoxes</c> as stock writes it. The same
    /// operations lane for lane (maxps/minps ARE <c>a &gt; b ? a : b</c> and
    /// <c>a &lt; b ? a : b</c>), so the same bits.
    /// </summary>
    /// <returns>Bit i set when child i's box is hit.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RayHitsFourBoxes(Vec3 start, Vec3 invDelta, int node)
    {
        ref float row = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_childBoxes), node * 24);
        Vector128<float> eps = Vector128.Create(DistEpsilon);
        Vector128<float> sx = Vector128.Create(start.X);
        Vector128<float> sy = Vector128.Create(start.Y);
        Vector128<float> sz = Vector128.Create(start.Z);
        Vector128<float> ix = Vector128.Create(invDelta.X);
        Vector128<float> iy = Vector128.Create(invDelta.Y);
        Vector128<float> iz = Vector128.Create(invDelta.Z);

        Vector128<float> lo0 = Sse.Multiply(Sse.Subtract(Sse.Subtract(Vector128.LoadUnsafe(ref row, 0), sx), eps), ix);
        Vector128<float> hi0 = Sse.Multiply(Sse.Add(Sse.Subtract(Vector128.LoadUnsafe(ref row, 12), sx), eps), ix);
        Vector128<float> lo1 = Sse.Multiply(Sse.Subtract(Sse.Subtract(Vector128.LoadUnsafe(ref row, 4), sy), eps), iy);
        Vector128<float> hi1 = Sse.Multiply(Sse.Add(Sse.Subtract(Vector128.LoadUnsafe(ref row, 16), sy), eps), iy);
        Vector128<float> lo2 = Sse.Multiply(Sse.Subtract(Sse.Subtract(Vector128.LoadUnsafe(ref row, 8), sz), eps), iz);
        Vector128<float> hi2 = Sse.Multiply(Sse.Add(Sse.Subtract(Vector128.LoadUnsafe(ref row, 20), sz), eps), iz);

        Vector128<float> exit0 = Sse.Max(lo0, hi0), entry0 = Sse.Min(lo0, hi0);
        Vector128<float> exit1 = Sse.Max(lo1, hi1), entry1 = Sse.Min(lo1, hi1);
        Vector128<float> exit2 = Sse.Max(lo2, hi2), entry2 = Sse.Min(lo2, hi2);

        Vector128<float> boxEntry = Sse.Max(Sse.Max(entry0, entry1), entry2);
        Vector128<float> boxExit = Sse.Min(Sse.Min(exit0, exit1), exit2);

        boxEntry = Sse.Max(boxEntry, Vector128<float>.Zero);
        boxExit = Sse.Min(boxExit, Vector128.Create(1.0f));
        return Sse.MoveMask(Sse.CompareLessThanOrEqual(boxEntry, boxExit));
    }

    private static float Max(float a, float b) => a > b ? a : b;

    private static float Min(float a, float b) => a < b ? a : b;

    /// <summary>
    /// <c>ComputeIntersectionBarycentricCoordinates</c>
    /// For a point ray (<c>boxt = 1e-3</c>).
    /// </summary>
    public static bool IntersectBarycentric(
        Vec3 start, Vec3 delta, Vec3 v1, Vec3 v2, Vec3 v3, out float u, out float v, out float t) =>
        IntersectBarycentricEdges(start, delta, v1, v2 - v1, v3 - v1, out u, out v, out t);

    /// <summary>
    /// <see cref="IntersectBarycentric"/> with its two edges already made:
    /// <paramref name="edge1"/> is <c>v2 - v1</c>, <paramref name="edge2"/> is
    /// <c>v3 - v1</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IntersectBarycentricEdges(
        Vec3 start, Vec3 delta, Vec3 v1, Vec3 edge1, Vec3 edge2, out float u, out float v, out float t)
    {
        u = 0;
        v = 0;
        t = 0;

        Vec3 dirCrossEdge2 = Vec3.Cross(delta, edge2);

        float denom = Vec3.Dot(dirCrossEdge2, edge1);

        // FloatMakePositive( denom ) < 1e-6: a float against a DOUBLE literal.
        if ((double)MathF.Abs(denom) < 1e-6)
        {
            return false;
        }

        denom = 1.0f / denom;

        Vec3 org = start - v1;
        u = Vec3.Dot(dirCrossEdge2, org) * denom;

        Vec3 orgCrossEdge1 = Vec3.Cross(org, edge1);
        v = Vec3.Dot(orgCrossEdge1, delta) * denom;

        // ComputeBoxOffset returns 1e-3f for a point ray.
        const float BoxT = 1e-3f;
        t = Vec3.Dot(orgCrossEdge1, edge2) * denom;
        return !(t < -BoxT || t > 1.0f + BoxT);
    }

    /// <summary>
    /// <c>IsBoxIntersectingRay( boxMin, boxMax, origin, delta, tolerance )</c>,
    /// The SIMD branch compiled (
    /// <c>USE_SIMD_RAY_CHECKS 1</c>).
    /// </summary>
    /// <remarks>
    /// <c>ReciprocalSaturateSIMD</c> is <c>rcpps</c> plus one Newton step; this
    /// takes the exact reciprocal. It is a conservative box FILTER in front of
    /// exact triangle tests, so the difference can only move a ray that grazes
    /// the box to within the estimate's error -- and the box is already
    /// bloated by a unit and a 1/32 tolerance.
    /// </remarks>
    public static bool IsBoxIntersectingRay(Vec3 boxMin, Vec3 boxMax, Vec3 origin, Vec3 delta, float tolerance)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            float offMin = (boxMin[axis] - origin[axis]) - tolerance;
            float offMax = (boxMax[axis] - origin[axis]) + tolerance;
            bool startOutMins = 0.0f < offMin;
            bool endOutMins = delta[axis] < offMin;
            bool startOutMaxs = 0.0f > offMax;
            bool endOutMaxs = delta[axis] > offMax;
            if ((startOutMins && endOutMins) || (startOutMaxs && endOutMaxs))
            {
                return false;
            }
        }

        float lastIn = -float.MaxValue;
        float firstOut = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float offMin = (boxMin[axis] - origin[axis]) - tolerance;
            float offMax = (boxMax[axis] - origin[axis]) + tolerance;
            bool startOutMins = 0.0f < offMin;
            bool endOutMins = delta[axis] < offMin;
            bool startOutMaxs = 0.0f > offMax;
            bool endOutMaxs = delta[axis] > offMax;
            bool crossPlane = (startOutMins ^ endOutMins) || (startOutMaxs ^ endOutMaxs);
            if (!crossPlane)
            {
                continue;
            }

            float inv = SaturatedReciprocal(delta[axis]);
            float tmin = offMin * inv;
            float tmax = offMax * inv;
            float mint = Min(tmin, tmax);
            float maxt = Max(tmin, tmax);
            firstOut = Min(firstOut, maxt);
            lastIn = Max(lastIn, mint);
        }

        firstOut = Min(firstOut, 1.0f);
        lastIn = Max(lastIn, 0.0f);
        return !(lastIn > firstOut);
    }

    /// <summary><c>AABBTree_GenerateBoxes_r</c>.</summary>
    /// <summary>
    /// <see cref="IsBoxIntersectingRay"/>'s reciprocal of one delta component.
    /// </summary>
    /// <remarks>
    /// ReciprocalSaturate: 1/0 becomes 1/FLT_EPSILON rather than infinity
    /// (Four_Epsilons). Unreachable for a zero delta, which
    /// never crosses a plane, and kept so the reason is written down.
    /// </remarks>
    internal static float SaturatedReciprocal(float delta)
    {
        const float FltEpsilon = 1.1920929e-07f;
        float d = delta == 0.0f ? FltEpsilon : delta;
        return 1.0f / d;
    }

    private void GenerateBoxes(int node, out Vec3 mins, out Vec3 maxs)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;

        if (node >= _nodeCount)
        {
            int leaf = node - _nodeCount;
            for (int k = 0; k < 2; k++)
            {
                int tri = _leafTris[(leaf * 2) + k];
                for (int j = 0; j < 3; j++)
                {
                    Add(_verts[_tris[(tri * 3) + j]]);
                }
            }
        }
        else
        {
            for (int i = 0; i < 4; i++)
            {
                int child = (node << 2) + i + 1;
                GenerateBoxes(child, out Vec3 cmin, out Vec3 cmax);
                _nodeMins[(node * 4) + i] = cmin;
                _nodeMaxs[(node * 4) + i] = cmax;
                Add(cmin);
                Add(cmax);
            }
        }

        mins = new Vec3(minX, minY, minZ);
        maxs = new Vec3(maxX, maxY, maxZ);

        // AddPointToBounds: per component, strict compares.
        void Add(Vec3 p)
        {
            if (p.X < minX) { minX = p.X; }
            if (p.X > maxX) { maxX = p.X; }
            if (p.Y < minY) { minY = p.Y; }
            if (p.Y > maxY) { maxY = p.Y; }
            if (p.Z < minZ) { minZ = p.Z; }
            if (p.Z > maxZ) { maxZ = p.Z; }
        }
    }
}
