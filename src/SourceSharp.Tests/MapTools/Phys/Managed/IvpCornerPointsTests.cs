//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
using System.Numerics;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Phys.Managed.Qhull;
using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The optimised <see cref="IvpHalfspaceSoup{T, TP}.CornerPoints"/> against the reference loop
/// (<see cref="IvpCornerPointsReference{T, TP}"/>): the same points, in the same order, to the
/// bit, at both precisions, over a deterministic corpus of soups that reaches every branch of the
/// optimised loop: random, degenerate, near-coplanar, and prop hulls rebuilt from their own
/// triangle planes, which is the case that made the loop worth optimising.
/// </summary>
public class IvpCornerPointsTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- the equality facts

    [Fact]
    public void RandomSoupsGiveTheReferenceCornersAtBothPrecisions()
    {
        // Random inward halfspaces around the origin: most triples meet outside the solid, some
        // soups are unbounded on a side, and a few planes cut nothing at all.
        for (int seed = 0; seed < 60; seed++)
        {
            var rng = new Random(seed);
            int n = rng.Next(0, 48);
            var planes = new List<(double X, double Y, double Z, double W)>(n);
            for (int i = 0; i < n; i++)
            {
                (double x, double y, double z) = Direction(rng);
                planes.Add((x, y, z, (rng.NextDouble() * 1.2) - 0.1));
            }

            AssertSame(planes, 0.01, $"random seed {seed}");
            AssertSame(planes, 0.0, $"random seed {seed}, no merge");
        }
    }

    [Fact]
    public void DegenerateSoupsGiveTheReferenceCornersAtBothPrecisions()
    {
        // Every plane of a cone through one apex: C(n, 3) triples all meet at the same point.
        var cone = new List<(double, double, double, double)>();
        for (int i = 0; i < 24; i++)
        {
            (double x, double y) = Circle(i, 24);
            double len = Math.Sqrt((x * x) + (y * y) + 1);
            cone.Add((-x / len, -y / len, 1 / len, 0.0)); // through the origin
        }

        cone.Add((0, 0, -1, 1)); // the base
        AssertSame(cone, 0.01, "cone");
        AssertSame(cone, 0.0, "cone, no merge");

        // Exactly repeated and exactly opposite planes (the soup is built directly, not through
        // AddHalfspace, so duplicates survive): determinants of exactly zero.
        var repeated = new List<(double, double, double, double)>();
        foreach ((double, double, double, double) p in Box(1.0))
        {
            repeated.Add(p);
            repeated.Add(p);
        }

        AssertSame(repeated, 0.01, "repeated box");
        AssertSame(repeated, 0.0, "repeated box, no merge");

        // Parallel slabs only: no triple meets at all.
        AssertSame([(1, 0, 0, 1), (-1, 0, 0, 1), (1, 0, 0, 2), (-1, 0, 0, 3)], 0.01, "slabs");

        // Fewer than three planes, and three that meet in a point.
        AssertSame(new List<(double, double, double, double)>(), 0.01, "empty");
        AssertSame([(1, 0, 0, 1)], 0.01, "one plane");
        AssertSame([(1, 0, 0, 1), (0, 1, 0, 1)], 0.01, "two planes");
        AssertSame([(1, 0, 0, 1), (0, 1, 0, 1), (0, 0, 1, 1)], 0.01, "three planes");

        // Normals whose determinant straddles the build's epsilon (1e-19 in double, 1e-10 in
        // float): tilting one of three nearly coplanar normals by e gives a determinant near e.
        foreach (double e in new[] { 1e-21, 1e-19, 3e-19, 1e-12, 1e-10, 3e-10, 1e-8 })
        {
            AssertSame(
                [(0, 0, 1, 1), (e, 0, 1, 1), (0, e, 1, 1), (1, 0, 0, 1), (-1, 0, 0, 1), (0, 1, 0, 1), (0, -1, 0, 1), (0, 0, -1, 1)],
                0.01,
                $"determinant near {e}");
        }

        // Non-finite input: a NaN determinant passes the epsilon test, and a NaN point passes
        // every inside test and is never merged; both loops keep it.
        AssertSame([.. Box(1.0), (double.NaN, 0, 0, 1), (0, double.PositiveInfinity, 0, 1)], 0.01, "non-finite");
    }

    [Fact]
    public void NearCoplanarSoupsGiveTheReferenceCornersAtBothPrecisions()
    {
        // A box whose every face is repeated with its normal jittered by a few ulps to a few
        // millimetres: many near-duplicate corners, merged or not depending on the distance.
        for (int seed = 0; seed < 12; seed++)
        {
            var rng = new Random(100 + seed);
            double jitter = Math.Pow(10, -rng.Next(3, 15));
            var planes = new List<(double, double, double, double)>();
            foreach ((double x, double y, double z, double w) in Box(0.5 + rng.NextDouble()))
            {
                for (int copy = 0; copy < 4; copy++)
                {
                    double jx = x + (jitter * ((rng.NextDouble() * 2) - 1));
                    double jy = y + (jitter * ((rng.NextDouble() * 2) - 1));
                    double jz = z + (jitter * ((rng.NextDouble() * 2) - 1));
                    double len = Math.Sqrt((jx * jx) + (jy * jy) + (jz * jz));
                    planes.Add((jx / len, jy / len, jz / len, w + (jitter * rng.NextDouble())));
                }
            }

            AssertSame(planes, 0.01, $"near-coplanar seed {seed}, jitter {jitter}");
            AssertSame(planes, 1e-6, $"near-coplanar seed {seed}, jitter {jitter}, tight merge");
        }
    }

    [Fact]
    public void BrushSoupsFromHammerPlanesGiveTheReferenceCorners()
    {
        // What a brush or a leaf hands ConvexFromPlanes: axial and angled faces in HL units,
        // converted and folded exactly as the cooker does.
        for (int seed = 0; seed < 20; seed++)
        {
            var rng = new Random(200 + seed);
            var hl = new List<(float, float, float, float)>
            {
                (1, 0, 0, 64), (-1, 0, 0, 64), (0, 1, 0, 32), (0, -1, 0, 32), (0, 0, 1, 128), (0, 0, -1, 0),
            };
            int bevels = rng.Next(0, 24);
            for (int i = 0; i < bevels; i++)
            {
                (double x, double y, double z) = Direction(rng);
                hl.Add(((float)x, (float)y, (float)z, (float)(40 + (rng.NextDouble() * 40))));
            }

            foreach (float merge in new[] { 0f, 0.5f })
            {
                List<IvpPoint<float>> fs = IvpHalfspaceSoup<float, StockPrecision>.FromHlPlanes([.. hl], merge, out float fm);
                AssertSame(fs, fm, $"brush {seed} float");
                List<IvpPoint<double>> ds = IvpHalfspaceSoup<double, CorrectPrecision>.FromHlPlanes([.. hl], merge, out double dm);
                AssertSame(ds, dm, $"brush {seed} double");
            }
        }
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(40, 2)]
    [InlineData(90, 3)]
    [InlineData(160, 4)]
    public void PropHullSoupsGiveTheReferenceCorners(int pointCount, int seed)
    {
        // The prop case: a point cloud's hull, rebuilt from its own triangle planes with the
        // cooker's 0.01 m merge, as every static prop hull is.
        foreach (Shape shape in Enum.GetValues<Shape>())
        {
            (float X, float Y, float Z)[] cloud = PropCloud(shape, pointCount, seed);
            AssertSame(PropSoup<double, CorrectPrecision>(cloud), 0.01, $"{shape} {pointCount} double");
            AssertSame(PropSoup<float, StockPrecision>(cloud), 0.01f, $"{shape} {pointCount} float");
        }
    }

    [Fact]
    public void AHeavyPropHullGivesTheReferenceCornersAndIsTimed()
    {
        // A soup shaped like the two long poles of a warm 2fort vbsp: a detailed rounded prop
        // whose hull rebuilds from about 440 triangle planes. The reference loop is kept to check
        // the answer against; the timings are reported, not asserted, since test machines are
        // shared and noisy.
        (float X, float Y, float Z)[] cloud = PropCloud(Shape.Rock, 400, 1);
        List<IvpPoint<double>> soup = PropSoup<double, CorrectPrecision>(cloud);
        Assert.InRange(soup.Count, 380, 500);

        // Warm both loops past tiering on a small soup first.
        List<IvpPoint<double>> small = PropSoup<double, CorrectPrecision>(PropCloud(Shape.Rock, 40, 9));
        for (int w = 0; w < 40; w++)
        {
            _ = IvpHalfspaceSoup<double, CorrectPrecision>.CornerPoints(small, 0.01);
            _ = IvpCornerPointsReference<double, CorrectPrecision>.CornerPoints(small, 0.01);
        }

        var clock = Stopwatch.StartNew();
        List<IvpPoint<double>> narrow = IvpHalfspaceSoup<double, CorrectPrecision>.CornerPoints(soup, 0.01, CornerLanes.Vector);
        double narrowMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        List<IvpPoint<double>> fast = IvpHalfspaceSoup<double, CorrectPrecision>.CornerPoints(soup, 0.01);
        double fastMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        List<IvpPoint<double>> reference = IvpCornerPointsReference<double, CorrectPrecision>.CornerPoints(soup, 0.01);
        double referenceMs = clock.Elapsed.TotalMilliseconds;

        output.WriteLine(
            $"{soup.Count} planes, {fast.Count} corners: optimised {fastMs:F1} ms, {Vector<double>.Count} lanes {narrowMs:F1} ms, reference {referenceMs:F1} ms " +
            $"(accelerated: {Vector.IsHardwareAccelerated}, 512-bit: {System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated})");
        AssertBits(reference, fast, "heavy prop");
        AssertBits(reference, narrow, "heavy prop, narrow");
    }

    // ---------------------------------------------------------------- helpers

    public enum Shape
    {
        /// <summary>Points on a lumpy ellipsoid: almost every point is a hull vertex.</summary>
        Rock,

        /// <summary>A bevelled box with a cylinder on top: large flat faces and a ring of facets.</summary>
        Building,

        /// <summary>Points on a grid on a sphere's faces, many exactly coplanar.</summary>
        Faceted,
    }

    private static void AssertSame(List<(double X, double Y, double Z, double W)> planes, double merge, string what)
    {
        AssertSame(Soup<double>(planes), merge, what + " (double)");
        AssertSame(Soup<float>(planes), (float)merge, what + " (float)");
    }

    // Every lane width the loop can take (one at a time, Vector<T> at a time, 512 bits at a
    // time where the CPU has them), each against the reference loop, so a machine with
    // AVX-512 checks the narrower paths too.
    private static readonly CornerLanes[] Widths = [CornerLanes.Scalar, CornerLanes.Vector, CornerLanes.Wide512];

    private static void AssertSame(List<IvpPoint<double>> soup, double merge, string what)
    {
        List<IvpPoint<double>> reference = Canonical(IvpCornerPointsReference<double, CorrectPrecision>.CornerPoints(soup, merge));
        AssertBits(reference, IvpHalfspaceSoup<double, CorrectPrecision>.CornerPoints(soup, merge), what);
        foreach (CornerLanes width in Widths)
        {
            AssertBits(reference, IvpHalfspaceSoup<double, CorrectPrecision>.CornerPoints(soup, merge, width), what + " " + width);
        }
    }

    private static void AssertSame(List<IvpPoint<float>> soup, float merge, string what)
    {
        List<IvpPoint<float>> reference = Canonical(IvpCornerPointsReference<float, StockPrecision>.CornerPoints(soup, merge));
        AssertBits(reference, IvpHalfspaceSoup<float, StockPrecision>.CornerPoints(soup, merge), what);
        foreach (CornerLanes width in Widths)
        {
            AssertBits(reference, IvpHalfspaceSoup<float, StockPrecision>.CornerPoints(soup, merge, width), what + " " + width);
        }
    }

    /// <summary>
    /// The reference's corners with every NaN coordinate written as <c>T.NaN</c>, the one
    /// difference the loop makes on purpose (see <c>IvpHalfspaceSoup.Canonical</c>). On x86 this
    /// changes nothing, because x86's NaN already is <c>T.NaN</c>; on arm64 the reference's NaN
    /// bits depend on which operation made the NaN, and are not something to match.
    /// </summary>
    private static List<IvpPoint<T>> Canonical<T>(List<IvpPoint<T>> points)
        where T : unmanaged, IBinaryFloatingPointIeee754<T> =>
        [.. points.Select(static p => new IvpPoint<T>(
            T.IsNaN(p.X) ? T.NaN : p.X, T.IsNaN(p.Y) ? T.NaN : p.Y, T.IsNaN(p.Z) ? T.NaN : p.Z, p.W))];

    /// <summary>
    /// A NaN corner is written as <c>T.NaN</c>, whatever NaN the arithmetic made, so the
    /// bytes are the same on x86 and arm64.
    /// </summary>
    [Fact]
    public void ANaNCornerIsTheCanonicalNaN()
    {
        List<(double, double, double, double)> planes = [.. Box(1.0), (double.NaN, 0, 0, 1), (0, double.PositiveInfinity, 0, 1)];
        foreach (IvpPoint<double> p in IvpHalfspaceSoup<double, CorrectPrecision>.CornerPoints(Soup<double>(planes), 0.01))
        {
            foreach (double c in new[] { p.X, p.Y, p.Z })
            {
                Assert.True(!double.IsNaN(c) || BitConverter.DoubleToInt64Bits(c) == BitConverter.DoubleToInt64Bits(double.NaN));
            }
        }

        foreach (IvpPoint<float> p in IvpHalfspaceSoup<float, StockPrecision>.CornerPoints(Soup<float>(planes), 0.01f))
        {
            foreach (float c in new[] { p.X, p.Y, p.Z })
            {
                Assert.True(!float.IsNaN(c) || BitConverter.SingleToInt32Bits(c) == BitConverter.SingleToInt32Bits(float.NaN));
            }
        }
    }

    private static void AssertBits<T>(List<IvpPoint<T>> expected, List<IvpPoint<T>> actual, string what)
        where T : unmanaged, IBinaryFloatingPointIeee754<T>
    {
        Assert.True(expected.Count == actual.Count, $"{what}: {actual.Count} corners, the reference has {expected.Count}");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(
                Bits(expected[i]) == Bits(actual[i]),
                $"{what}: corner {i} is {Bits(actual[i])}, the reference's is {Bits(expected[i])}");
        }
    }

    private static string Bits<T>(IvpPoint<T> p)
        where T : unmanaged, IBinaryFloatingPointIeee754<T> =>
        typeof(T) == typeof(double)
            ? $"{BitConverter.DoubleToInt64Bits(double.CreateTruncating(p.X)):x16} {BitConverter.DoubleToInt64Bits(double.CreateTruncating(p.Y)):x16} {BitConverter.DoubleToInt64Bits(double.CreateTruncating(p.Z)):x16} {BitConverter.DoubleToInt64Bits(double.CreateTruncating(p.W)):x16}"
            : $"{BitConverter.SingleToInt32Bits(float.CreateTruncating(p.X)):x8} {BitConverter.SingleToInt32Bits(float.CreateTruncating(p.Y)):x8} {BitConverter.SingleToInt32Bits(float.CreateTruncating(p.Z)):x8} {BitConverter.SingleToInt32Bits(float.CreateTruncating(p.W)):x8}";

    private static List<IvpPoint<T>> Soup<T>(List<(double X, double Y, double Z, double W)> planes)
        where T : unmanaged, IBinaryFloatingPointIeee754<T> =>
        [.. planes.Select(p => new IvpPoint<T>(T.CreateTruncating(p.X), T.CreateTruncating(p.Y), T.CreateTruncating(p.Z), T.CreateTruncating(p.W)))];

    /// <summary>The soup <c>ConvexFromVerts</c> hands the corner loop for a point cloud.</summary>
    internal static List<IvpPoint<T>> PropSoup<T, TP>((float X, float Y, float Z)[] cloud)
        where T : unmanaged, IBinaryFloatingPointIeee754<T>
        where TP : struct, IIvpPrecision<T>
    {
        IvpCompactLedge? ledge = IvpCooker<T, TP>.ConvexFromVertsFast(cloud, new IvpCookContext(new QhullRunner()));
        Assert.NotNull(ledge);
        return IvpCooker<T, TP>.RebuildSoup(ledge);
    }

    /// <summary>A deterministic prop-like point cloud, in HL units; no platform math.</summary>
    internal static (float X, float Y, float Z)[] PropCloud(Shape shape, int count, int seed)
    {
        var rng = new Random(seed);
        var points = new (float X, float Y, float Z)[count];
        for (int i = 0; i < count; i++)
        {
            (double x, double y, double z) = Direction(rng);
            points[i] = shape switch
            {
                Shape.Rock => Scale(x, y, z, 120, 80, 200, 1 + (0.05 * rng.NextDouble())),
                Shape.Building => i % 3 == 0
                    ? ((float)(64 * x / Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), 1e-3)), (float)(64 * y / Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), 1e-3)), (float)(128 + (32 * z)))
                    : Scale(Snap(x), Snap(y), Snap(z), 96, 96, 96, 1),
                _ => Scale(Snap(x), Snap(y), Snap(z), 50, 50, 50, 1),
            };
        }

        return points;

        static (float, float, float) Scale(double x, double y, double z, double sx, double sy, double sz, double s) =>
            ((float)(sx * s * x), (float)(sy * s * y), (float)(sz * s * z));

        static double Snap(double v) => Math.Round(v * 4) / 4;
    }

    /// <summary>A uniform random unit direction, by rejection from the cube: no platform math.</summary>
    private static (double X, double Y, double Z) Direction(Random rng)
    {
        while (true)
        {
            double x = (rng.NextDouble() * 2) - 1, y = (rng.NextDouble() * 2) - 1, z = (rng.NextDouble() * 2) - 1;
            double r2 = (x * x) + (y * y) + (z * z);
            if (r2 is > 0.01 and <= 1)
            {
                double r = Math.Sqrt(r2);
                return (x / r, y / r, z / r);
            }
        }
    }

    /// <summary>A point on the unit circle by a rational parametrisation: no platform math.</summary>
    private static (double X, double Y) Circle(int i, int n)
    {
        double t = ((2.0 * i) / n) - 1; // (-1, 1): half the circle, mirrored below for the rest
        double d = 1 + (t * t);
        return i % 2 == 0 ? ((1 - (t * t)) / d, 2 * t / d) : (-(1 - (t * t)) / d, -2 * t / d);
    }

    private static List<(double X, double Y, double Z, double W)> Box(double half) =>
    [
        (1, 0, 0, half), (-1, 0, 0, half), (0, 1, 0, half), (0, -1, 0, half), (0, 0, 1, half), (0, 0, -1, half),
    ];
}
