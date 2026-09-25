using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// The closest hit of a ray on one displacement: stock's
/// <c>RayDispOutput_t</c> (<c>dispcoll_common.h</c>).
/// </summary>
/// <param name="Dist">The hit's fraction along the ray's delta.</param>
/// <param name="U">Barycentric u along <c>verts[Vert1] - verts[Vert0]</c>.</param>
/// <param name="V">Barycentric v along <c>verts[Vert2] - verts[Vert0]</c>.</param>
/// <param name="Vert0"><c>ndxVerts[0]</c>: the triangle's vertex 0.</param>
/// <param name="Vert1"><c>ndxVerts[1]</c>: its vertex 2.</param>
/// <param name="Vert2"><c>ndxVerts[2]</c>: its vertex 1.</param>
public readonly record struct DispRayHit(float Dist, float U, float V, int Vert0, int Vert1, int Vert2);

/// <summary>
/// Ray tests against displacements: <c>CDispCollTree::AABBTree_Ray</c> with a
/// <c>RayDispOutput_t</c> (<c>dispcoll_common.cpp:555-647</c>) and the helpers
/// it calls from <c>collisionutils.cpp</c>.
/// </summary>
/// <remarks>
/// <para>
/// vrad reaches these only through <c>ClipRayToDispInLeaf</c>
/// (<see cref="DispLeafIndex"/>), which is how leaf ambient
/// (<c>CastRayInLeaf</c>) and detail-prop lighting (<c>CLightSurface</c>) see
/// displacements -- the KD-tree tracer (<c>g_RtEnv</c>) gets the triangles
/// separately (<c>AddPolysForRayTrace</c>, <see cref="DisplacementShadowCasters"/>).
/// </para>
/// <para>
/// Not ported, because nothing in vrad calls it: the <c>CBaseTrace</c> overload
/// of <c>AABBTree_Ray</c> reached only from <c>ClipRayToDisp</c> and the
/// three-argument <c>ClipRayToDispInLeaf</c>, neither of which has a caller
/// under <c>src/utils/vrad</c>.
/// </para>
/// </remarks>
public static class DispCollision
{
    /// <summary><c>DISPCOLL_DIST_EPSILON</c>.</summary>
    public const float DistEpsilon = 0.03125f;

    /// <summary><c>MASK_OPAQUE</c> (<c>bspflags.h:114</c>): SOLID | MOVEABLE | OPAQUE.</summary>
    public const int MaskOpaque = 0x1 | 0x4000 | 0x80;

    /// <summary><c>CCoreDispInfo::SURF_NORAY_COLL</c>.</summary>
    public const int SurfNoRayColl = 0x8;

    /// <summary><c>FLT_EPSILON</c>.</summary>
    public const float FltEpsilon = 1.1920929e-07f;

    /// <summary><c>ComputeBoxOffset</c> for a point ray: <c>1e-3f</c>.</summary>
    public const float RayBoxOffset = 1e-3f;

    /// <summary>
    /// <c>AABBTree_Ray( ray, output )</c>, the "lower perf helper" with the
    /// bounds check (<c>dispcoll_common.cpp:555</c>): the closest triangle hit
    /// nearer than <paramref name="hit"/>'s distance.
    /// </summary>
    /// <param name="surface">The displacement.</param>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray's full extent.</param>
    /// <param name="hit">In: the distance to beat (stock starts at <c>FLT_MAX</c>). Out: the hit, when one is found.</param>
    /// <returns>True when a closer hit was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
    public static bool Ray(VradDispSurface surface, Vec3 start, Vec3 delta, ref DispRayHit hit)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (!IsBoxIntersectingRay(surface.Tree.Mins, surface.Tree.Maxs, start, delta, DistEpsilon))
        {
            return false;
        }

        return Ray(surface, start, delta, InvDelta(delta), ref hit);
    }

    /// <summary>
    /// <c>AABBTree_Ray( ray, invDelta, output )</c> (<c>dispcoll_common.cpp:564</c>).
    /// </summary>
    /// <param name="surface">The displacement.</param>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray's full extent.</param>
    /// <param name="invDelta"><c>Ray_t::InvDelta</c>.</param>
    /// <param name="hit">In: the distance to beat. Out: the hit, when one is found.</param>
    /// <returns>True when a closer hit was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
    /// <remarks>
    /// Non-opaque displacements (contents without <c>MASK_OPAQUE</c>) are never
    /// hit. The triangles are intersected with their vertices in the order
    /// 0, 2, 1, and the hit's vertex list is reported in that order too.
    /// </remarks>
    public static bool Ray(VradDispSurface surface, Vec3 start, Vec3 delta, Vec3 invDelta, ref DispRayHit hit)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if ((surface.Flags & SurfNoRayColl) != 0)
        {
            return false;
        }

        if ((surface.Contents & MaskOpaque) == 0)
        {
            return false;
        }

        DispCollisionTree tree = surface.Tree;
        ReadOnlySpan<Vec3> verts = surface.Verts;
        Span<int> list = stackalloc int[344];
        int listIndex = BuildRayLeafList(tree, start, delta, invDelta, list, out int maxIndex);

        int impact = -1;
        float dist = hit.Dist;
        float bestU = 0f;
        float bestV = 0f;
        for (; listIndex <= maxIndex; listIndex++)
        {
            (int tri0, int tri1) = tree.LeafTris(list[listIndex] - tree.NodeCount);
            foreach (int t in (ReadOnlySpan<int>)[tri0, tri1])
            {
                DispCollTri tri = surface.Tris[t];
                if (IntersectBarycentric(start, delta, verts[tri.V0], verts[tri.V2], verts[tri.V1], out float u, out float v, out float time)
                    && u >= 0.0f && v >= 0.0f && (u + v) <= 1.0f
                    && time > 0.0f && time < dist)
                {
                    impact = t;
                    bestU = u;
                    bestV = v;
                    dist = time;
                }
            }
        }

        if (impact < 0)
        {
            return false;
        }

        DispCollTri hitTri = surface.Tris[impact];
        hit = new DispRayHit(dist, bestU, bestV, hitTri.V0, hitTri.V2, hitTri.V1);
        return true;
    }

    /// <summary>
    /// <c>BuildRayLeafList</c> (<c>dispcoll_common.cpp:247</c>): a breadth-first
    /// walk that keeps every child box the ray crosses, leaving the leaves at
    /// the end of the list.
    /// </summary>
    /// <param name="tree">The tree.</param>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray delta (unused but for symmetry with stock).</param>
    /// <param name="invDelta">Its inverse.</param>
    /// <param name="list">At least 344 entries (<c>MAX_AABB_LIST</c>).</param>
    /// <param name="maxIndex">The last filled entry.</param>
    /// <returns>The first leaf's position in the list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree"/> is null.</exception>
    public static int BuildRayLeafList(DispCollisionTree tree, Vec3 start, Vec3 delta, Vec3 invDelta, Span<int> list, out int maxIndex)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _ = delta;

        // :611-613. A point ray's extents are zero, plus DISPCOLL_DIST_EPSILON.
        const float ext = DistEpsilon;
        list[0] = 0;
        int listIndex = 0;
        maxIndex = 0;
        while (listIndex <= maxIndex)
        {
            int node = list[listIndex];
            if (node >= tree.NodeCount)
            {
                return listIndex;
            }

            listIndex++;
            int mask = IntersectRayWithFourBoxes(tree, node, start, invDelta, ext);
            int child = (node << 2) + 1;
            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    list[++maxIndex] = child + i;
                }
            }
        }

        return listIndex;
    }

    private static int IntersectRayWithFourBoxes(DispCollisionTree tree, int node, Vec3 start, Vec3 invDelta, float ext)
    {
        // :150. Per box: slab entry/exit, entry clamped to >= 0, exit to <= 1.
        int mask = 0;
        for (int i = 0; i < 4; i++)
        {
            (Vec3 mins, Vec3 maxs) = tree.ChildBox(node, i);
            float entry = 0f;
            float exit = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                float lo = ((mins[axis] - start[axis]) - ext) * invDelta[axis];
                float hi = ((maxs[axis] - start[axis]) + ext) * invDelta[axis];
                float axisExit = Max(lo, hi);
                float axisEntry = Min(lo, hi);
                entry = axis == 0 ? axisEntry : Max(entry, axisEntry);
                exit = axis == 0 ? axisExit : Min(exit, axisExit);
            }

            entry = Max(entry, 0.0f);
            exit = Min(exit, 1.0f);
            if (entry <= exit)
            {
                mask |= 1 << i;
            }
        }

        return mask;
    }

    // _mm_max_ps / _mm_min_ps: the second operand unless the first compares greater (smaller).
    private static float Max(float a, float b) => a > b ? a : b;

    private static float Min(float a, float b) => a < b ? a : b;

    /// <summary><c>Ray_t::InvDelta</c>: 1/d per axis, <c>FLT_MAX</c> for a zero axis.</summary>
    /// <param name="delta">The ray delta.</param>
    /// <returns>The inverse.</returns>
    public static Vec3 InvDelta(Vec3 delta) => new(
        delta.X != 0.0f ? 1.0f / delta.X : float.MaxValue,
        delta.Y != 0.0f ? 1.0f / delta.Y : float.MaxValue,
        delta.Z != 0.0f ? 1.0f / delta.Z : float.MaxValue);

    /// <summary>
    /// <c>ComputeIntersectionBarycentricCoordinates</c> (<c>collisionutils.cpp:140</c>)
    /// for a point ray.
    /// </summary>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray delta.</param>
    /// <param name="v1">Triangle vertex 1.</param>
    /// <param name="v2">Vertex 2.</param>
    /// <param name="v3">Vertex 3.</param>
    /// <param name="u">Along <c>v2 - v1</c>.</param>
    /// <param name="v">Along <c>v3 - v1</c>.</param>
    /// <param name="t">The fraction along the ray.</param>
    /// <returns>False when the ray is parallel (|det| &lt; 1e-6) or t is outside [-1e-3, 1 + 1e-3].</returns>
    /// <remarks>
    /// u and v are NOT range-checked here; the caller does it.
    /// </remarks>
    public static bool IntersectBarycentric(Vec3 start, Vec3 delta, Vec3 v1, Vec3 v2, Vec3 v3, out float u, out float v, out float t)
    {
        Vec3 edge1 = v2 - v1;
        Vec3 edge2 = v3 - v1;
        Vec3 dirCrossEdge2 = Vec3.Cross(delta, edge2);

        float denom = Vec3.Dot(dirCrossEdge2, edge1);
        u = 0f;
        v = 0f;
        t = 0f;

        // :157. The 1e-6 is a double literal: the float is widened.
        if (Math.Abs(denom) < 1e-6)
        {
            return false;
        }

        denom = 1.0f / denom;
        Vec3 org = start - v1;
        u = Vec3.Dot(dirCrossEdge2, org) * denom;
        Vec3 orgCrossEdge1 = Vec3.Cross(org, edge1);
        v = Vec3.Dot(orgCrossEdge1, delta) * denom;

        t = Vec3.Dot(orgCrossEdge1, edge2) * denom;
        return !(t < -RayBoxOffset || t > 1.0f + RayBoxOffset);
    }

    /// <summary>
    /// <c>IsBoxIntersectingRay</c> with a tolerance (<c>collisionutils.cpp:642</c>,
    /// the <c>USE_SIMD_RAY_CHECKS</c> body).
    /// </summary>
    /// <param name="boxMin">Box mins.</param>
    /// <param name="boxMax">Box maxs.</param>
    /// <param name="origin">Ray start.</param>
    /// <param name="delta">Ray delta.</param>
    /// <param name="tolerance">How much to grow the box.</param>
    /// <returns>True when the segment touches the grown box.</returns>
    /// <remarks>
    /// Stock takes the reciprocal with <c>rcpps</c> plus a Newton step
    /// (<c>ReciprocalSaturateSIMD</c>); this divides exactly. The test is a
    /// conservative cull in front of an exact triangle test on a box grown by
    /// a unit, so a last-bit difference in t cannot change which triangle is
    /// hit.
    /// </remarks>
    public static bool IsBoxIntersectingRay(Vec3 boxMin, Vec3 boxMax, Vec3 origin, Vec3 delta, float tolerance)
    {
        float lastIn = float.MinValue;
        float firstOut = float.MaxValue;
        bool first = true;
        for (int axis = 0; axis < 3; axis++)
        {
            float offMin = (boxMin[axis] - origin[axis]) - tolerance;
            float offMax = (boxMax[axis] - origin[axis]) + tolerance;
            float d = delta[axis];

            bool startOutMins = 0.0f < offMin;
            bool endOutMins = d < offMin;
            bool startOutMaxs = 0.0f > offMax;
            bool endOutMaxs = d > offMax;
            if ((startOutMins && endOutMins) || (startOutMaxs && endOutMaxs))
            {
                return false;
            }

            // ReciprocalSaturateSIMD: a zero becomes FLT_EPSILON first.
            float inv = 1.0f / (d == 0.0f ? FltEpsilon : d);
            float tmin = offMin * inv;
            float tmax = offMax * inv;
            bool cross = (startOutMins ^ endOutMins) || (startOutMaxs ^ endOutMaxs);
            if (!cross)
            {
                tmin = -float.MaxValue;
                tmax = float.MaxValue;
            }

            float mint = Min(tmin, tmax);
            float maxt = Max(tmin, tmax);
            if (first)
            {
                lastIn = mint;
                firstOut = maxt;
                first = false;
            }
            else
            {
                lastIn = Math.Max(lastIn, mint);
                firstOut = Math.Min(firstOut, maxt);
            }
        }

        firstOut = Min(firstOut, 1.0f);
        lastIn = Max(lastIn, 0.0f);
        return !(lastIn > firstOut);
    }

    /// <summary>
    /// <c>ComputePointFromBarycentric</c> for 2-D points: <c>v0 + (v1 - v0) u + (v2 - v0) v</c>.
    /// </summary>
    /// <param name="v0">Point 0.</param>
    /// <param name="v1">Point 1.</param>
    /// <param name="v2">Point 2.</param>
    /// <param name="u">U.</param>
    /// <param name="v">V.</param>
    /// <returns>The point.</returns>
    public static DispUv PointFromBarycentric(DispUv v0, DispUv v1, DispUv v2, float u, float v)
    {
        // collisionutils.h: edgeU = v1 - v0; edgeV = v2 - v0; pt = v0 + edgeU*u + edgeV*v.
        DispUv edgeU = v1 - v0;
        DispUv edgeV = v2 - v0;
        return new DispUv(v0.X + (edgeU.X * u) + (edgeV.X * v), v0.Y + (edgeU.Y * u) + (edgeV.Y * v));
    }
}
