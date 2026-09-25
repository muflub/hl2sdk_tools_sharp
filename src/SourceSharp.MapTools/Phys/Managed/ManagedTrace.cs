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
    internal sealed class Convex
    {
        public required double[][] Points { get; init; }

        public required (double[] N, double D)[] Planes { get; init; }

        /// <summary>No volume (a two-sided triangle, or coplanar points): tested by GJK, not planes.</summary>
        public bool Flat { get; init; }
    }

    /// <summary>The leaf convexes of a surface, placed by a transform (null for identity).</summary>
    /// <param name="surface">The compact surface.</param>
    /// <param name="placement">Where the collide sits, or null.</param>
    /// <returns>The convexes.</returns>
    public static List<Convex> Convexes(ReadOnlySpan<byte> surface, InstanceTransform? placement)
    {
        var list = new List<Convex>();
        foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(surface))
        {
            list.Add(ConvexOf(ledge, placement));
        }

        return list;
    }

    private static Convex ConvexOf(IvpCompactLedge ledge, InstanceTransform? placement)
    {
        int n = ledge.PointCount;
        double[][] points = new double[n][];
        double cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < n; i++)
        {
            (float x, float y, float z) = IvpCollideQueries.HlPoint(ledge, i);
            Vec3 p = new(x, y, z);
            if (placement is { } t)
            {
                p = t.TransformPoint(p);
            }

            points[i] = [p.X, p.Y, p.Z];
            cx += p.X;
            cy += p.Y;
            cz += p.Z;
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

        (double[] N, double D)[]? hull = HullPlanes(points);
        return new Convex { Points = points, Planes = hull ?? planes, Flat = hull is null };
    }

    /// <summary>
    /// The facets of the points' convex hull (qhull), outward, as (normal, offset along it).
    /// IVP ledges are not exactly the intersection of their triangles' planes (a point may sit a
    /// little outside a face), and the native trace works on the points, so the hull is the solid.
    /// </summary>
    private static (double[] N, double D)[]? HullPlanes(double[][] points)
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

        Qhull.QhullResult hull = Qhull.QhullBuilder.Build(xyz, "qhull Pp");
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
    public static CollisionTrace Ray(ReadOnlySpan<byte> surface, InstanceTransform? placement, Vec3 start, Vec3 end)
    {
        double best = 1;
        double[]? bestNormal = null;
        bool startSolid = false, allSolid = false;
        double[][] segment = [[start.X, start.Y, start.Z], [end.X, end.Y, end.Z]];
        foreach (Convex c in Convexes(surface, placement))
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
    public static (float X, float Y, float Z) OrthographicAreas(ReadOnlySpan<byte> surface, float epsilon, bool doublePrecision)
    {
        ((float X, float Y, float Z) mn, (float X, float Y, float Z) mx) = IvpCollideQueries.SurfaceAabb(surface);
        float[] mins = [mn.X, mn.Y, mn.Z];
        float[] maxs = [mx.X, mx.Y, mx.Z];
        float side = MathF.Sqrt(epsilon);
        if (side < 1e-4f)
        {
            side = 1e-4f;
        }

        var tree = new LedgeTree(surface, doublePrecision);
        float[] areas = [1f, 1f, 1f];
        float halfSide = (float)(side * 0.5);
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
                    float[] s = new float[3];
                    float[] e = new float[3];
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

        public LedgeTree(ReadOnlySpan<byte> surface, bool doublePrecision)
        {
            _surface = surface.ToArray();
            _double = doublePrecision;
            foreach (int at in IvpCollideQueries.LeafOffsets(_surface))
            {
                _leaves[at] = ConvexOf(IvpCollideQueries.LedgeAt(_surface, at), null);
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
