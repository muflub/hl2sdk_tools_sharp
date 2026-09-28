//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

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
    /// <remarks>
    /// <para>
    /// Every static prop's leaf test and every ray against a flat ledge comes
    /// through here, and it used to allocate a three-element array for every
    /// vector operation, some thirty per iteration: on 2fort that was the
    /// busiest allocation site of the prop leaf traces. The vectors are now
    /// values (<see cref="D3"/>) and the simplex a fixed four slots.
    /// </para>
    /// <para>
    /// <b>The arithmetic is unchanged, operation for operation.</b> Each
    /// helper evaluates the same expression, operands and grouping included,
    /// that the array version did (<c>0 - x</c> stays a subtraction, not a
    /// negation, where the array code subtracted from a zero vector), and the
    /// search visits the simplex's points, edges and faces in the same order
    /// with the same strict comparisons. A double is a double whether it sits
    /// in an array or a struct, so every intermediate has the same bits and
    /// every branch goes the same way. A test-only copy of the array version
    /// is compared against this one over random, degenerate and touching
    /// point sets.
    /// </para>
    /// </remarks>
    public static bool Intersect(double[][] a, double[][] b)
    {
        D3 direction = Sub(Centre(a), Centre(b));
        if (Dot(direction, direction) < 1e-18)
        {
            direction = new D3(1, 0, 0);
        }

        Simplex simplex = default;
        simplex.Add(Support(a, b, direction));
        direction = Neg(simplex[0]);

        for (int iteration = 0; iteration < 128; iteration++)
        {
            if (Dot(direction, direction) < 1e-24)
            {
                return true;
            }

            D3 point = Support(a, b, direction);
            if (Dot(point, direction) < -1e-9)
            {
                return false;
            }

            simplex.Add(point);
            if (DoSimplex(ref simplex, ref direction))
            {
                return true;
            }
        }

        return true;
    }

    // Distance subalgorithm by brute force over the simplex's faces:
    // replace the simplex with the closest feature to the origin.
    private static bool DoSimplex(ref Simplex s, ref D3 direction)
    {
        (D3 closest, Simplex feature) = ClosestToOrigin(s);
        if (Dot(closest, closest) < 1e-18)
        {
            return true;
        }

        s = feature;
        direction = Neg(closest);
        return false;
    }

    private static (D3 Point, Simplex Feature) ClosestToOrigin(in Simplex s)
    {
        if (s.Count == 4 && OriginInTetrahedron(s[0], s[1], s[2], s[3]))
        {
            return (new D3(0, 0, 0), s);
        }

        D3 best = s[0];
        Simplex bestFeature = Simplex.Of(s[0]);
        double bestDistance = Dot(s[0], s[0]);

        for (int i = 0; i < s.Count; i++)
        {
            Consider(s[i], Simplex.Of(s[i]), ref best, ref bestFeature, ref bestDistance);
            for (int j = i + 1; j < s.Count; j++)
            {
                Consider(ClosestOnSegment(s[i], s[j]), Simplex.Of(s[i], s[j]), ref best, ref bestFeature, ref bestDistance);
                for (int k = j + 1; k < s.Count; k++)
                {
                    Consider(
                        ClosestOnTriangle(s[i], s[j], s[k]),
                        Simplex.Of(s[i], s[j], s[k]),
                        ref best,
                        ref bestFeature,
                        ref bestDistance);
                }
            }
        }

        return (best, bestFeature);
    }

    private static void Consider(D3 point, Simplex feature, ref D3 best, ref Simplex bestFeature, ref double bestDistance)
    {
        double distance = Dot(point, point);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            best = point;
            bestFeature = feature;
        }
    }

    private static bool OriginInTetrahedron(D3 a, D3 b, D3 c, D3 d)
    {
        static bool SameSide(D3 p, D3 q, D3 r, D3 s)
        {
            D3 n = Cross(Sub(q, p), Sub(r, p));
            double ds = Dot(n, Sub(s, p));
            double d0 = Dot(n, Neg(p));
            return ds * d0 >= 0;
        }

        return SameSide(a, b, c, d) && SameSide(b, c, d, a) && SameSide(c, d, a, b) && SameSide(d, a, b, c);
    }

    private static D3 ClosestOnSegment(D3 a, D3 b)
    {
        D3 ab = Sub(b, a);
        double length = Dot(ab, ab);
        if (length < 1e-30)
        {
            return a;
        }

        double t = Math.Clamp(-Dot(a, ab) / length, 0, 1);
        return Add(a, Scale(ab, t));
    }

    // Ericson, Real-Time Collision Detection 5.1.5, for the point (origin).
    private static D3 ClosestOnTriangle(D3 a, D3 b, D3 c)
    {
        D3 p = new(0, 0, 0);
        D3 ab = Sub(b, a), ac = Sub(c, a), ap = Sub(p, a);
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
        {
            return a;
        }

        D3 bp = Sub(p, b);
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

        D3 cp = Sub(p, c);
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

    private static D3 Support(double[][] a, double[][] b, D3 d) =>
        Sub(Farthest(a, d), Farthest(b, Neg(d)));

    private static D3 Farthest(double[][] points, D3 d)
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

        return new D3(best[0], best[1], best[2]);
    }

    private static D3 Centre(double[][] points)
    {
        D3 c = new(0, 0, 0);
        foreach (double[] p in points)
        {
            c = new D3(c.X + p[0], c.Y + p[1], c.Z + p[2]);
        }

        return Scale(c, 1.0 / points.Length);
    }

    private static double Dot(D3 a, D3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    private static double Dot(double[] a, D3 b) => (a[0] * b.X) + (a[1] * b.Y) + (a[2] * b.Z);

    private static D3 Add(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static D3 Sub(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static D3 Neg(D3 a) => new(-a.X, -a.Y, -a.Z);

    private static D3 Scale(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    private static D3 Cross(D3 a, D3 b) =>
        new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    /// <summary>A vector of three doubles, by value.</summary>
    private readonly record struct D3(double X, double Y, double Z);

    /// <summary>Up to four points of a GJK simplex, in the order they were added.</summary>
    private struct Simplex
    {
        private D3 _p0;
        private D3 _p1;
        private D3 _p2;
        private D3 _p3;

        public int Count { get; private set; }

        public readonly D3 this[int index] => index switch
        {
            0 => _p0,
            1 => _p1,
            2 => _p2,
            3 => _p3,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public static Simplex Of(D3 a)
        {
            Simplex s = default;
            s.Add(a);
            return s;
        }

        public static Simplex Of(D3 a, D3 b)
        {
            Simplex s = Of(a);
            s.Add(b);
            return s;
        }

        public static Simplex Of(D3 a, D3 b, D3 c)
        {
            Simplex s = Of(a, b);
            s.Add(c);
            return s;
        }

        public void Add(D3 point)
        {
            switch (Count)
            {
                case 0: _p0 = point; break;
                case 1: _p1 = point; break;
                case 2: _p2 = point; break;
                case 3: _p3 = point; break;
                default: throw new InvalidOperationException("a GJK simplex has at most four points");
            }

            Count++;
        }
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

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Scale(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double[] Norm(double[] a) => Scale(a, 1.0 / Math.Sqrt(Dot(a, a)));
}
