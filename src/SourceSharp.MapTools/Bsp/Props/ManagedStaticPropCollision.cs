using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Props;

/// <summary>
/// <see cref="IStaticPropCollision"/> from exact convex geometry, with no
/// physics library.
/// </summary>
/// <remarks>
/// <para>
/// A mesh's convex hull is its point set; the collision model is the union of
/// those hulls, as <c>ConvertConvexToCollide</c> makes it. The box is the
/// placed points' extent, and a leaf holds the prop when some mesh hull and
/// the leaf's polytope overlap, decided by GJK on the two point sets (the
/// leaf's corners come from clipping a large box by its planes).
/// </para>
/// <para>
/// NOT vphysics. IVP builds its own hull (and may shrink or pad it), so a prop
/// whose hull merely grazes a leaf boundary can be listed differently. The
/// stock gate measures how often; lane 3h's native cooker replaces this where
/// stock bytes are the goal.
/// </para>
/// </remarks>
public sealed class ManagedStaticPropCollision : IStaticPropCollision
{
    /// <inheritdoc />
    public ValueTask<IStaticPropHull?> BuildHullAsync(
        IReadOnlyList<Vec3[]> meshes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        cancellationToken.ThrowIfCancellationRequested();

        // ConvexFromVerts refuses fewer than four points; a model with no
        // usable mesh at all is "Bad geometry".
        List<Vec3[]> usable = [.. meshes.Where(m => m.Length >= 4)];
        return ValueTask.FromResult<IStaticPropHull?>(usable.Count == 0 ? null : new Hull(usable));
    }

    private sealed class Hull(List<Vec3[]> meshes) : IStaticPropHull
    {
        public ValueTask<(Vec3 Mins, Vec3 Maxs)> GetAabbAsync(Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            InstanceTransform transform = InstanceTransform.FromAngles(angles, origin);
            Vec3 mins = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vec3 maxs = new(float.MinValue, float.MinValue, float.MinValue);

            foreach (Vec3[] mesh in meshes)
            {
                foreach (Vec3 p in mesh)
                {
                    Vec3 w = transform.TransformPoint(p);
                    mins = new Vec3(Math.Min(mins.X, w.X), Math.Min(mins.Y, w.Y), Math.Min(mins.Z, w.Z));
                    maxs = new Vec3(Math.Max(maxs.X, w.X), Math.Max(maxs.Y, w.Y), Math.Max(maxs.Z, w.Z));
                }
            }

            return ValueTask.FromResult((mins, maxs));
        }

        public ValueTask<bool> IntersectsAsync(
            ReadOnlyMemory<(Vec3 Normal, float Dist)> planes,
            Vec3 origin,
            Vec3 angles,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double[][] leaf = ConvexGeometry.PolytopeCorners(planes.Span);
            if (leaf.Length == 0)
            {
                return ValueTask.FromResult(false);
            }

            InstanceTransform transform = InstanceTransform.FromAngles(angles, origin);
            foreach (Vec3[] mesh in meshes)
            {
                double[][] placed = new double[mesh.Length][];
                for (int i = 0; i < mesh.Length; i++)
                {
                    Vec3 w = transform.TransformPoint(mesh[i]);
                    placed[i] = [w.X, w.Y, w.Z];
                }

                if (ConvexGeometry.Intersect(placed, leaf))
                {
                    return ValueTask.FromResult(true);
                }
            }

            return ValueTask.FromResult(false);
        }
    }
}

/// <summary>Exact convex-set helpers for the managed collision seam.</summary>
internal static class ConvexGeometry
{
    private const double Extent = 65536.0;

    /// <summary>
    /// The corners of the region every plane keeps (<c>n·x &lt;= d</c>),
    /// clipped to a 65536-unit box; empty when the region is.
    /// </summary>
    public static double[][] PolytopeCorners(ReadOnlySpan<(Vec3 Normal, float Dist)> planes)
    {
        // Each face of the box as a polygon, clipped by every plane; the
        // surviving vertices, plus each plane's own face polygon clipped by
        // the others, bound the region. Simpler and sufficient: clip the six
        // box faces AND one polygon per plane, collecting all vertices.
        List<double[]> corners = [];
        List<(double[] N, double D)> all = [];
        foreach ((Vec3 n, float d) in planes)
        {
            all.Add(([n.X, n.Y, n.Z], d));
        }

        for (int axis = 0; axis < 3; axis++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                double[] n = [0, 0, 0];
                n[axis] = sign;
                all.Add((n, Extent));
            }
        }

        for (int i = 0; i < all.Count; i++)
        {
            List<double[]> polygon = BasePolygon(all[i].N, all[i].D);
            for (int j = 0; j < all.Count && polygon.Count > 0; j++)
            {
                if (j != i)
                {
                    polygon = Clip(polygon, all[j].N, all[j].D);
                }
            }

            corners.AddRange(polygon);
        }

        return [.. corners];
    }

    /// <summary>GJK on two point sets: whether their convex hulls meet.</summary>
    public static bool Intersect(double[][] a, double[][] b)
    {
        double[] direction = Sub(Centre(a), Centre(b));
        if (Dot(direction, direction) < 1e-18)
        {
            direction = [1, 0, 0];
        }

        List<double[]> simplex = [Support(a, b, direction)];
        direction = Neg(simplex[0]);

        for (int iteration = 0; iteration < 128; iteration++)
        {
            if (Dot(direction, direction) < 1e-24)
            {
                return true;
            }

            double[] point = Support(a, b, direction);
            if (Dot(point, direction) < -1e-9)
            {
                return false;
            }

            simplex.Add(point);
            if (DoSimplex(simplex, ref direction))
            {
                return true;
            }
        }

        return true;
    }

    private static List<double[]> BasePolygon(double[] n, double d)
    {
        // Two axes in the plane, then a square of side 4 * Extent.
        int major = Math.Abs(n[0]) >= Math.Abs(n[1]) && Math.Abs(n[0]) >= Math.Abs(n[2]) ? 0
                  : Math.Abs(n[1]) >= Math.Abs(n[2]) ? 1 : 2;
        double[] up = major == 2 ? [1, 0, 0] : [0, 0, 1];
        double k = Dot(up, n);
        up = Norm(Sub(up, Scale(n, k)));
        double[] right = Cross(up, n);
        double[] centre = Scale(n, d);
        double s = Extent * 4;

        return
        [
            Add(Add(centre, Scale(up, s)), Scale(right, -s)),
            Add(Add(centre, Scale(up, s)), Scale(right, s)),
            Add(Add(centre, Scale(up, -s)), Scale(right, s)),
            Add(Add(centre, Scale(up, -s)), Scale(right, -s)),
        ];
    }

    private static List<double[]> Clip(List<double[]> polygon, double[] n, double d)
    {
        const double epsilon = 1e-7;
        List<double[]> result = [];
        for (int i = 0; i < polygon.Count; i++)
        {
            double[] p = polygon[i];
            double[] q = polygon[(i + 1) % polygon.Count];
            double dp = Dot(n, p) - d;
            double dq = Dot(n, q) - d;

            if (dp <= epsilon)
            {
                result.Add(p);
            }

            if ((dp < -epsilon && dq > epsilon) || (dp > epsilon && dq < -epsilon))
            {
                double t = dp / (dp - dq);
                result.Add(Add(p, Scale(Sub(q, p), t)));
            }
        }

        return result;
    }

    private static bool DoSimplex(List<double[]> s, ref double[] direction)
    {
        // Distance subalgorithm by brute force over the simplex's faces:
        // replace the simplex with the closest feature to the origin.
        (double[] closest, List<double[]> feature) = ClosestToOrigin(s);
        if (Dot(closest, closest) < 1e-18)
        {
            return true;
        }

        s.Clear();
        s.AddRange(feature);
        direction = Neg(closest);
        return false;
    }

    private static (double[] Point, List<double[]> Feature) ClosestToOrigin(List<double[]> s)
    {
        if (s.Count == 4 && OriginInTetrahedron(s[0], s[1], s[2], s[3]))
        {
            return ([0, 0, 0], s);
        }

        double[] best = s[0];
        List<double[]> bestFeature = [s[0]];
        double bestDistance = Dot(s[0], s[0]);

        void Consider(double[] point, List<double[]> feature)
        {
            double distance = Dot(point, point);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = point;
                bestFeature = feature;
            }
        }

        for (int i = 0; i < s.Count; i++)
        {
            Consider(s[i], [s[i]]);
            for (int j = i + 1; j < s.Count; j++)
            {
                Consider(ClosestOnSegment(s[i], s[j]), [s[i], s[j]]);
                for (int k = j + 1; k < s.Count; k++)
                {
                    Consider(ClosestOnTriangle(s[i], s[j], s[k]), [s[i], s[j], s[k]]);
                }
            }
        }

        return (best, bestFeature);
    }

    private static bool OriginInTetrahedron(double[] a, double[] b, double[] c, double[] d)
    {
        static bool SameSide(double[] p, double[] q, double[] r, double[] s)
        {
            double[] n = Cross(Sub(q, p), Sub(r, p));
            double ds = Dot(n, Sub(s, p));
            double d0 = Dot(n, Neg(p));
            return ds * d0 >= 0;
        }

        return SameSide(a, b, c, d) && SameSide(b, c, d, a) && SameSide(c, d, a, b) && SameSide(d, a, b, c);
    }

    private static double[] ClosestOnSegment(double[] a, double[] b)
    {
        double[] ab = Sub(b, a);
        double length = Dot(ab, ab);
        if (length < 1e-30)
        {
            return a;
        }

        double t = Math.Clamp(-Dot(a, ab) / length, 0, 1);
        return Add(a, Scale(ab, t));
    }

    // Ericson, Real-Time Collision Detection 5.1.5, for the point (origin).
    private static double[] ClosestOnTriangle(double[] a, double[] b, double[] c)
    {
        double[] p = [0, 0, 0];
        double[] ab = Sub(b, a), ac = Sub(c, a), ap = Sub(p, a);
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
        {
            return a;
        }

        double[] bp = Sub(p, b);
        double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
        {
            return b;
        }

        double vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            return Add(a, Scale(ab, d1 / (d1 - d3)));
        }

        double[] cp = Sub(p, c);
        double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
        {
            return c;
        }

        double vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            return Add(a, Scale(ac, d2 / (d2 - d6)));
        }

        double va = (d3 * d6) - (d5 * d4);
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
        {
            return Add(b, Scale(Sub(c, b), (d4 - d3) / ((d4 - d3) + (d5 - d6))));
        }

        double denominator = 1 / (va + vb + vc);
        return Add(a, Add(Scale(ab, vb * denominator), Scale(ac, vc * denominator)));
    }

    private static double[] Support(double[][] a, double[][] b, double[] d) =>
        Sub(Farthest(a, d), Farthest(b, Neg(d)));

    private static double[] Farthest(double[][] points, double[] d)
    {
        double[] best = points[0];
        double bestDot = Dot(best, d);
        foreach (double[] p in points)
        {
            double dot = Dot(p, d);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = p;
            }
        }

        return best;
    }

    private static double[] Centre(double[][] points)
    {
        double[] c = [0, 0, 0];
        foreach (double[] p in points)
        {
            c = Add(c, p);
        }

        return Scale(c, 1.0 / points.Length);
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Neg(double[] a) => [-a[0], -a[1], -a[2]];

    private static double[] Scale(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double[] Norm(double[] a) => Scale(a, 1.0 / Math.Sqrt(Dot(a, a)));
}
