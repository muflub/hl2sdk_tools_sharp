//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Bsp.Props;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Props;

/// <summary>
/// <see cref="ConvexGeometry.Intersect"/> without its per-operation arrays
/// answers exactly as the array version it replaced, kept here as
/// <see cref="ArrayGjk"/>: over random clouds that overlap, touch and miss,
/// and over the degenerate sets (single points, repeated points, segments,
/// flat polygons, shared faces) where a different rounding would change a
/// branch.
/// </summary>
public sealed class ConvexGeometryIntersectTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RandomCloudsAnswerAsTheArrayVersionDoes(int seed)
    {
        Random random = new(seed);
        int hits = 0, misses = 0;
        for (int trial = 0; trial < 4000; trial++)
        {
            double[][] a = Cloud(random, random.Next(1, 24), random.NextDouble() * 64 - 32);
            double[][] b = Cloud(random, random.Next(1, 24), random.NextDouble() * 64 - 32);

            bool expected = ArrayGjk.Intersect(a, b);
            Assert.Equal(expected, ConvexGeometry.Intersect(a, b));
            if (expected)
            {
                hits++;
            }
            else
            {
                misses++;
            }
        }

        // Both answers must be common, or the comparison proves little.
        Assert.True(hits > 400 && misses > 400, $"{hits} hits, {misses} misses");
    }

    [Fact]
    public void DegenerateSetsAnswerAsTheArrayVersionDoes()
    {
        double[][] unitCube = Box(0, 0, 0, 1, 1, 1);
        List<(double[][] A, double[][] B)> cases =
        [
            ([[0, 0, 0]], [[0, 0, 0]]),
            ([[0, 0, 0]], [[1e-12, 0, 0]]),
            ([[0, 0, 0]], [[1, 2, 3]]),
            ([[0, 0, 0], [0, 0, 0], [0, 0, 0]], [[0, 0, 0]]),
            ([[0, 0, 0], [2, 0, 0]], [[1, 0, 0]]),
            ([[0, 0, 0], [2, 0, 0]], [[1, 1e-10, 0]]),
            ([[-1, 0, 0], [1, 0, 0]], [[0, -1, 0], [0, 1, 0]]),
            ([[0, 0, 0], [1, 0, 0], [0, 1, 0]], [[0.25, 0.25, 0]]),
            ([[0, 0, 0], [1, 0, 0], [0, 1, 0]], [[0.25, 0.25, 1e-9]]),
            (unitCube, Box(1, 0, 0, 2, 1, 1)),
            (unitCube, Box(1 + 1e-9, 0, 0, 2, 1, 1)),
            (unitCube, Box(0.5, 0.5, 0.5, 3, 3, 3)),
            (unitCube, Box(-0.0, -0.0, -0.0, 1, 1, 1)),
            (unitCube, [[0.5, 0.5, 0.5]]),
            (unitCube, [[0.5, 0.5, 1.5], [0.5, 0.5, 2.5]]),
            (unitCube, [[0.5, 0.5, -1.5], [0.5, 0.5, 2.5]]),
            (Box(-0.0, 0, 0, 0, 1, 1), Box(0, 0, 0, 1e-300, 1, 1)),
        ];

        foreach ((double[][] a, double[][] b) in cases)
        {
            Assert.Equal(ArrayGjk.Intersect(a, b), ConvexGeometry.Intersect(a, b));
            Assert.Equal(ArrayGjk.Intersect(b, a), ConvexGeometry.Intersect(b, a));
        }
    }

    [Fact]
    public void GridAlignedPropsAnswerAsTheArrayVersionDoes()
    {
        // Prop hulls and leaf corners are mostly on a coarse grid, so exact
        // ties between support points are common; ties decide which point
        // Farthest keeps.
        Random random = new(77);
        for (int trial = 0; trial < 3000; trial++)
        {
            double[][] a = GridCloud(random, random.Next(4, 16));
            double[][] b = GridCloud(random, random.Next(4, 16));
            Assert.Equal(ArrayGjk.Intersect(a, b), ConvexGeometry.Intersect(a, b));
        }
    }

    private static double[][] Cloud(Random random, int count, double offset)
    {
        double[][] points = new double[count][];
        double scale = random.NextDouble() * 32;
        for (int i = 0; i < count; i++)
        {
            points[i] = [offset + (random.NextDouble() * scale), random.NextDouble() * scale, random.NextDouble() * scale];
        }

        return points;
    }

    private static double[][] GridCloud(Random random, int count)
    {
        double[][] points = new double[count][];
        for (int i = 0; i < count; i++)
        {
            points[i] = [random.Next(-4, 5) * 8.0, random.Next(-4, 5) * 8.0, random.Next(-4, 5) * 8.0];
        }

        return points;
    }

    private static double[][] Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
    [
        [x0, y0, z0], [x1, y0, z0], [x0, y1, z0], [x1, y1, z0],
        [x0, y0, z1], [x1, y0, z1], [x0, y1, z1], [x1, y1, z1],
    ];

    /// <summary>
    /// The GJK test as it was before its vectors became values, verbatim:
    /// the reference the facts above compare against.
    /// </summary>
    private static class ArrayGjk
    {
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
    }
}
