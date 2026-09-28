//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Props;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// The traces the vbsp path asks of a collide, answered geometrically: a ray against the solid
/// (<c>TraceBox</c> with zero extents, which <c>ComputeOrthographicAreas</c> fires on a grid) and
/// a zero-length <c>TraceCollide</c> (a static prop's hull against a leaf polytope).
/// </summary>
/// <remarks>
/// NOT a port of <c>CPhysicsTrace</c> (IVP's own trace solver is not reproduced
/// Here): these are exact tests in double on the very polytopes IVP cooked. They can
/// disagree with vphysics only on grazing contact, and the driver gates measure how often.
/// </remarks>
internal static class ManagedTrace
{
    /// <summary>One leaf convex as HL-space points and outward planes (double).</summary>
    /// <remarks>
    /// <para>
    /// The planes are worked out the first time something asks for them, not when the convex is
    /// made. Only the ray tests (<see cref="Clip"/>, and <see cref="Flat"/> which picks between
    /// the plane clip and GJK) read them; the static-prop queries that dominate a compile
    /// (<c>TraceCollide</c>'s overlap test, <c>CollideGetAABB</c>, <c>CollideGetExtent</c>)
    /// look at the points alone. Building the planes eagerly cost one qhull hull per leaf per
    /// query that nothing read, and on a prop-heavy map that was more qhull work than the
    /// cooking itself. The planes are a pure function of the points and the ledge, so asking
    /// later gives the same bits as asking at once.
    /// </para>
    /// <para>
    /// A convex lives only inside the one query (or the one <see cref="LedgeTree"/>) that made
    /// it, on the thread that made it, so the qhull storage it builds its hull with later is
    /// still that thread's and still idle between builds.
    /// </para>
    /// </remarks>
    internal sealed class Convex
    {
        private readonly IvpCompactLedge _ledge;
        private readonly Qhull.QhullSession _hulls;
        private (double[] N, double D)[]? _planes;
        private bool _flat;

        /// <summary>A leaf's convex over its (placed) points; the planes follow on demand.</summary>
        /// <param name="points">The ledge's points in HL space, placed.</param>
        /// <param name="ledge">The ledge, for its triangles when the points have no hull.</param>
        /// <param name="hulls">The owning thread's qhull storage.</param>
        public Convex(double[][] points, IvpCompactLedge ledge, Qhull.QhullSession hulls)
        {
            Points = points;
            _ledge = ledge;
            _hulls = hulls;
        }

        /// <summary>The ledge's points in HL space.</summary>
        public double[][] Points { get; }

        /// <summary>
        /// The outward planes as (unit normal, offset along it): the facets of the points' hull,
        /// or the ledge's own triangles when the points have no hull (<see cref="Flat"/>).
        /// </summary>
        public (double[] N, double D)[] Planes
        {
            get
            {
                EnsurePlanes();
                return _planes!;
            }
        }

        /// <summary>No volume (a two-sided triangle, or coplanar points): tested by GJK, not planes.</summary>
        public bool Flat
        {
            get
            {
                EnsurePlanes();
                return _flat;
            }
        }

        /// <summary>Whether the planes have been worked out yet (for the facts that check they are not built unasked).</summary>
        internal bool PlanesBuilt => _planes is not null;

        private void EnsurePlanes()
        {
            if (_planes is not null)
            {
                return;
            }

            (double[] N, double D)[]? hull = HullPlanes(Points, _hulls);
            _flat = hull is null;
            _planes = hull ?? TrianglePlanes(Points, _ledge);
        }
    }

    /// <summary>The leaf convexes of a surface, placed by a transform (null for identity).</summary>
    /// <param name="surface">The compact surface.</param>
    /// <param name="placement">Where the collide sits, or null.</param>
    /// <param name="hulls">
    /// The calling thread's qhull storage (its cook context's), which the hull of every leaf is
    /// built with when its planes are asked for. Without it every leaf hull allocated a fresh
    /// qhull object graph.
    /// </param>
    /// <returns>The convexes.</returns>
    public static List<Convex> Convexes(ReadOnlySpan<byte> surface, InstanceTransform? placement, Qhull.QhullSession hulls)
    {
        var list = new List<Convex>();
        foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(surface))
        {
            list.Add(ConvexOf(ledge, placement, hulls));
        }

        return list;
    }

    private static Convex ConvexOf(IvpCompactLedge ledge, InstanceTransform? placement, Qhull.QhullSession hulls)
    {
        int n = ledge.PointCount;
        double[][] points = new double[n][];
        for (int i = 0; i < n; i++)
        {
            (float x, float y, float z) = IvpCollideQueries.HlPoint(ledge, i);
            Vec3 p = new(x, y, z);
            if (placement is { } t)
            {
                p = t.TransformPoint(p);
            }

            points[i] = [p.X, p.Y, p.Z];
        }

        return new Convex(points, ledge, hulls);
    }

    /// <summary>
    /// The ledge's triangles as planes, each turned to face away from the points' centroid: the
    /// fallback solid for a ledge whose points have no hull.
    /// </summary>
    private static (double[] N, double D)[] TrianglePlanes(double[][] points, IvpCompactLedge ledge)
    {
        int n = points.Length;
        double cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < n; i++)
        {
            cx += points[i][0];
            cy += points[i][1];
            cz += points[i][2];
        }

        cx /= n;
        cy /= n;
        cz /= n;

        var planes = new (double[] N, double D)[ledge.TriangleCount];
        for (int t = 0; t < ledge.TriangleCount; t++)
        {
            double[] a = points[ledge.EdgeStart(t, 0)];
            double[] b = points[ledge.EdgeStart(t, 1)];
            double[] c = points[ledge.EdgeStart(t, 2)];
            double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            double vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            double[] nn = [(uy * vz) - (uz * vy), (uz * vx) - (ux * vz), (ux * vy) - (uy * vx)];
            double len = Math.Sqrt((nn[0] * nn[0]) + (nn[1] * nn[1]) + (nn[2] * nn[2]));
            if (len > 0)
            {
                nn[0] /= len;
                nn[1] /= len;
                nn[2] /= len;
            }

            double d = (nn[0] * a[0]) + (nn[1] * a[1]) + (nn[2] * a[2]);
            if ((nn[0] * cx) + (nn[1] * cy) + (nn[2] * cz) > d)
            {
                nn = [-nn[0], -nn[1], -nn[2]];
                d = -d;
            }

            planes[t] = (nn, d);
        }

        return planes;
    }

    /// <summary>
    /// The facets of the points' convex hull (qhull), outward, as (normal, offset along it).
    /// IVP ledges are not exactly the intersection of their triangles' planes (a point may sit a
    /// little outside a face), and the native trace works on the points, so the hull is the solid.
    /// </summary>
    private static (double[] N, double D)[]? HullPlanes(double[][] points, Qhull.QhullSession hulls)
    {
        if (points.Length < 4)
        {
            return null;
        }

        double[] xyz = new double[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            xyz[(3 * i) + 0] = points[i][0];
            xyz[(3 * i) + 1] = points[i][1];
            xyz[(3 * i) + 2] = points[i][2];
        }

        Qhull.QhullResult hull = hulls.Build(xyz, "qhull Pp");
        if (hull.ExitCode != 0 || hull.Facets.Count < 4)
        {
            return null;
        }

        var planes = new (double[] N, double D)[hull.Facets.Count];
        for (int i = 0; i < planes.Length; i++)
        {
            Qhull.QhullFacet f = hull.Facets[i];
            planes[i] = ([f.NormalX, f.NormalY, f.NormalZ], -f.Offset);
        }

        return planes;
    }

    /// <summary>
    /// Clips the segment start→end by a convex: the entry fraction and plane, or null for a miss.
    /// Grazing contact (the segment on a face's plane) counts as a hit.
    /// </summary>
    public static (double Enter, double Exit, double[]? Normal)? Clip(Convex convex, Vec3 start, Vec3 end)
    {
        double sx = start.X, sy = start.Y, sz = start.Z;
        double dx = end.X - sx, dy = end.Y - sy, dz = end.Z - sz;
        double t0 = 0, t1 = 1;
        double[]? normal = null;
        foreach ((double[] n, double d) in convex.Planes)
        {
            double dist = (n[0] * sx) + (n[1] * sy) + (n[2] * sz) - d;
            double denom = (n[0] * dx) + (n[1] * dy) + (n[2] * dz);
            if (denom == 0)
            {
                if (dist > 0)
                {
                    return null;
                }

                continue;
            }

            double t = -dist / denom;
            if (denom < 0)
            {
                if (t > t0)
                {
                    t0 = t;
                    normal = n;
                }
            }
            else if (t < t1)
            {
                t1 = t;
            }

            if (t0 > t1)
            {
                return null;
            }
        }

        return (t0, t1, normal);
    }

    /// <summary>A ray (zero-extent <c>TraceBox</c>) against a surface's leaves.</summary>
    public static CollisionTrace Ray(ReadOnlySpan<byte> surface, InstanceTransform? placement, Vec3 start, Vec3 end, Qhull.QhullSession hulls)
    {
        double best = 1;
        double[]? bestNormal = null;
        bool startSolid = false, allSolid = false;
        double[][] segment = [[start.X, start.Y, start.Z], [end.X, end.Y, end.Z]];
        foreach (Convex c in Convexes(surface, placement, hulls))
        {
            if (c.Flat ? !ConvexGeometry.Intersect(segment, c.Points) : Clip(c, start, end) is null)
            {
                continue;
            }

            // A flat ledge has no volume for the clip to find; its plane gives the entry.
            if (Clip(c, start, end) is not { } hit)
            {
                continue;
            }

            if (hit.Normal is null)
            {
                startSolid = true;
                allSolid |= hit.Exit >= 1;
                best = 0;
                continue;
            }

            if (hit.Enter < best)
            {
                best = hit.Enter;
                bestNormal = hit.Normal;
            }
        }

        float f = (float)best;
        Vec3 at = new(start.X + ((end.X - start.X) * f), start.Y + ((end.Y - start.Y) * f), start.Z + ((end.Z - start.Z) * f));
        Vec3 normal = bestNormal is null ? default : new Vec3((float)bestNormal[0], (float)bestNormal[1], (float)bestNormal[2]);
        return new CollisionTrace(start, at, normal, f, allSolid, startSolid);
    }

    /// <summary>Whether a ray segment meets any leaf of a surface (<c>trace_t::DidHit</c>).</summary>
    public static bool RayHits(List<Convex> convexes, Vec3 start, Vec3 end)
    {
        double[][] segment = [[start.X, start.Y, start.Z], [end.X, end.Y, end.Z]];
        foreach (Convex c in convexes)
        {
            // The segment against the convex hull of the ledge's points: flat two-sided ledges
            // (a triangle's two faces bound no edges) are handled like any other.
            if (c.Flat ? ConvexGeometry.Intersect(segment, c.Points) : Clip(c, start, end) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether two placed surfaces overlap (a zero-length <c>TraceCollide</c>'s startsolid).</summary>
    public static bool Overlaps(List<Convex> a, List<Convex> b)
    {
        foreach (Convex ca in a)
        {
            foreach (Convex cb in b)
            {
                if (ConvexGeometry.Intersect(ca.Points, cb.Points))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// <c>CPhysCollideCompactSurface::ComputeOrthographicAreas</c>: the
    /// fraction of a grid of axis rays, <c>sqrt(epsilon)</c> apart, that hit the solid.
    /// </summary>
    public static (float X, float Y, float Z) OrthographicAreas(ReadOnlySpan<byte> surface, float epsilon, bool doublePrecision, Qhull.QhullSession hulls)
    {
        ((float X, float Y, float Z) mn, (float X, float Y, float Z) mx) = IvpCollideQueries.SurfaceAabb(surface);
        float[] mins = [mn.X, mn.Y, mn.Z];
        float[] maxs = [mx.X, mx.Y, mx.Z];
        float side = MathF.Sqrt(epsilon);
        if (side < 1e-4f)
        {
            side = 1e-4f;
        }

        var tree = new LedgeTree(surface, doublePrecision, hulls);
        float[] areas = [1f, 1f, 1f];
        float halfSide = (float)(side * 0.5);

        // The ray's two ends, reused for every ray of the grid: each ray sets
        // all three of their components (axis, u and v are a permutation of
        // 0, 1, 2), so nothing carries over from the ray before. Two arrays
        // per ray was the largest allocation site in a 2fort vbsp, about
        // 65 MB of garbage over the drag areas of all its collides.
        Span<float> s = stackalloc float[3];
        Span<float> e = stackalloc float[3];
        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;
            int hits = 0;
            int total = 0;
            for (float u0 = mins[u] + halfSide; u0 < maxs[u]; u0 += side)
            {
                for (float v0 = mins[v] + halfSide; v0 < maxs[v]; v0 += side)
                {
                    s[axis] = mins[axis] - 1;
                    e[axis] = maxs[axis] + 1;
                    s[u] = u0;
                    e[u] = u0;
                    s[v] = v0;
                    e[v] = v0;
                    if (tree.RayHits(new Vec3(s[0], s[1], s[2]), new Vec3(e[0], e[1], e[2])))
                    {
                        hits++;
                    }

                    total++;
                }
            }

            if (total <= 0)
            {
                total = 1;
            }

            areas[axis] = hits / (float)total;
        }

        return (areas[0], areas[1], areas[2]);
    }

    /// <summary>
    /// The native ray walk over a compact surface's ledge tree, with the ray set up
    /// as the reference implementation sets it up: a node is entered only when the ray's LINE passes strictly inside its
    /// sphere (radius plus the ray's 1e-8 m radius), in float, IVP space. IVP's cluster spheres are
    /// approximations that need not contain their children, so this cull decides some hits: a ray
    /// through a leaf whose ancestor sphere misses the line is a miss, as it is in vphysics.
    /// </summary>
    internal sealed class LedgeTree
    {
        private const float HlToIvp = 0.0254f;
        private const float RayRadius = 1e-8f;
        private readonly byte[] _surface;
        private readonly bool _double;
        private readonly Dictionary<int, Convex> _leaves = [];

        public LedgeTree(ReadOnlySpan<byte> surface, bool doublePrecision, Qhull.QhullSession hulls)
        {
            _surface = surface.ToArray();
            _double = doublePrecision;
            foreach (int at in IvpCollideQueries.LeafOffsets(_surface))
            {
                _leaves[at] = ConvexOf(IvpCollideQueries.LedgeAt(_surface, at), null, hulls);
            }
        }

        private float F(int at) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(_surface.AsSpan(at));

        private int I(int at) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(_surface.AsSpan(at));

        /// <summary><c>trace_t::DidHit</c> for a ray from start to end (HL).</summary>
        public bool RayHits(Vec3 start, Vec3 end)
        {
            // ConvertPositionToIVP: (x, -z, y) * 0.0254f; the identity placement leaves it exact.
            float sx = start.X * HlToIvp, sy = -(start.Z * HlToIvp), sz = start.Y * HlToIvp;
            float dx = (end.X - start.X) * HlToIvp, dy = -((end.Z - start.Z) * HlToIvp), dz = (end.Y - start.Y) * HlToIvp;
            float nx = dx, ny = dy, nz = dz;
            if (_double)
            {
                CorrectPrecision.NormizeFloatPoint(ref nx, ref ny, ref nz);
            }
            else
            {
                StockPrecision.NormizeFloatPoint(ref nx, ref ny, ref nz);
            }

            float cx = (dx * 0.5f) + sx, cy = (dy * 0.5f) + sy, cz = (dz * 0.5f) + sz;
            var ray = (C: (cx, cy, cz), N: (nx, ny, nz));
            int root = I(0x20);
            return Visit(root, ray, start, end);
        }

        private bool Inside(int node, ((float X, float Y, float Z) C, (float X, float Y, float Z) N) ray)
        {
            float r = F(node + 20) + RayRadius;
            float ex = F(node + 8) - ray.C.X, ey = F(node + 12) - ray.C.Y, ez = F(node + 16) - ray.C.Z;
            float a = (ez * ray.N.Y) - (ey * ray.N.Z);
            float b = (ex * ray.N.Z) - (ez * ray.N.X);
            float c = (ey * ray.N.X) - (ex * ray.N.Y);
            return ((a * a) + (b * b)) + (c * c) < r * r;
        }

        private bool Visit(int node, ((float X, float Y, float Z) C, (float X, float Y, float Z) N) ray, Vec3 start, Vec3 end)
        {
            if (!Inside(node, ray))
            {
                return false;
            }

            int right = I(node);
            if (right == 0)
            {
                Convex leaf = _leaves[node + I(node + 4)];
                return leaf.Flat
                    ? ConvexGeometry.Intersect([[start.X, start.Y, start.Z], [end.X, end.Y, end.Z]], leaf.Points)
                    : Clip(leaf, start, end) is not null;
            }

            return Visit(node + 28, ray, start, end) || Visit(node + right, ray, start, end);
        }
    }
}
