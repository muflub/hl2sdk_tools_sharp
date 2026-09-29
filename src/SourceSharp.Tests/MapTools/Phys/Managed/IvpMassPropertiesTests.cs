//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;

using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Phys.Managed.Qhull;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The surface compile's rotation inertia, made in one pass over the triangles for all three
/// axes, is bit for bit what the reference's one pass per axis makes.
/// </summary>
/// <remarks>
/// <para>
/// The solver shares each triangle's normal and mass-centre-frame corners between the axes
/// (<c>IvpLedgeSolver.AxisMoments</c> says why no bit can move). These facts hold it to that
/// against <see cref="ThreePass{T, TP}"/>, a copy of the per-axis reference kept here as the
/// oracle, at both precisions, on ledges chosen so every branch of the integrand runs: axis
/// normals (both of the integrand's two slope modes, and the mode whose slope coefficient
/// stays 0), slanted normals (a non-zero coefficient), and coincident points (a zero-length
/// edge, skipped under the correct-mode fix and divided through, to a NaN, without it).
/// </para>
/// <para>
/// The comparison is on the stored bits, so a NaN the reference makes must come out as the
/// same NaN.
/// </para>
/// </remarks>
public sealed class IvpMassPropertiesTests
{
    /// <summary>The two precisions and whether zero-length edges are skipped.</summary>
    public static TheoryData<bool, bool> Modes => new()
    {
        { false, false },
        { false, true },
        { true, false },
        { true, true },
    };

    /// <summary>A box, whose faces' normals lie on the axes.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void ABoxMatchesThePerAxisPasses(bool stock, bool skipZeroLengthEdges) =>
        Check(stock, skipZeroLengthEdges, [Box(-16, -8, 0, 48, 24, 96)]);

    /// <summary>Slanted convexes, whose normals put weight on every axis.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void SlantedConvexesMatchThePerAxisPasses(bool stock, bool skipZeroLengthEdges) =>
        Check(stock, skipZeroLengthEdges, [Wedge(0), Wedge(1), Cloud(7), Cloud(11), Box(100, 100, 100, 132, 164, 110)]);

    /// <summary>
    /// A ledge with corners moved onto their neighbours: the edges between them have no length,
    /// so the fix skips them and the reference divides 0 by 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void CoincidentPointsMatchThePerAxisPasses(bool stock, bool skipZeroLengthEdges)
    {
        List<IvpCompactLedge> ledges = [Cloud(3), Box(-40, -40, -40, -8, 8, 24)];
        Squash(ledges[0], 0);
        Squash(ledges[0], 5);
        Check(stock, skipZeroLengthEdges, ledges);
    }

    /// <summary>The oracle itself tells the precisions and the fix apart, so the facts above can fail.</summary>
    [Fact]
    public void TheOracleSeesTheZeroLengthEdgeFix()
    {
        List<IvpCompactLedge> ledges = [Cloud(3)];
        Squash(ledges[0], 0);
        IvpLedgeSolver<double, CorrectPrecision>.MassProperties(ledges, out (double X, double Y, double Z) mc, out _);
        (double X, double Y, double Z) divided = ThreePass<double, CorrectPrecision>.Inertia(ledges, mc, false);
        (double X, double Y, double Z) skipped = ThreePass<double, CorrectPrecision>.Inertia(ledges, mc, true);
        Assert.True(double.IsNaN(divided.X) || double.IsNaN(divided.Y) || double.IsNaN(divided.Z));
        Assert.False(double.IsNaN(skipped.X) || double.IsNaN(skipped.Y) || double.IsNaN(skipped.Z));
    }

    private static void Check(bool stock, bool skipZeroLengthEdges, List<IvpCompactLedge> ledges)
    {
        if (stock)
        {
            Check<float, StockPrecision>(skipZeroLengthEdges, ledges);
        }
        else
        {
            Check<double, CorrectPrecision>(skipZeroLengthEdges, ledges);
        }
    }

    private static void Check<T, TP>(bool skipZeroLengthEdges, List<IvpCompactLedge> ledges)
        where T : unmanaged, IBinaryFloatingPointIeee754<T>
        where TP : struct, IIvpPrecision<T>
    {
        IvpLedgeSolver<T, TP>.MassProperties(ledges, out (T X, T Y, T Z) mc, out (T X, T Y, T Z) inertia, skipZeroLengthEdges);
        (T X, T Y, T Z) expected = ThreePass<T, TP>.Inertia(ledges, mc, skipZeroLengthEdges);
        Assert.Equal(Bits(expected.X), Bits(inertia.X));
        Assert.Equal(Bits(expected.Y), Bits(inertia.Y));
        Assert.Equal(Bits(expected.Z), Bits(inertia.Z));
    }

    private static long Bits<T>(T value)
        where T : unmanaged, IBinaryFloatingPointIeee754<T> =>
        typeof(T) == typeof(float)
            ? BitConverter.SingleToInt32Bits(float.CreateTruncating(value))
            : BitConverter.DoubleToInt64Bits(double.CreateTruncating(value));

    /// <summary>Moves the end of a triangle's first edge onto its start, so that edge has no length.</summary>
    private static void Squash(IvpCompactLedge ledge, int tri)
    {
        (float x, float y, float z) = ledge.Point(ledge.EdgeStart(tri, 0));
        ledge.SetPoint(ledge.EdgeStart(tri, 1), x, y, z);
    }

    private static IvpCompactLedge Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        Verts([(x0, y0, z0), (x1, y0, z0), (x0, y1, z0), (x1, y1, z0), (x0, y0, z1), (x1, y0, z1), (x0, y1, z1), (x1, y1, z1)]);

    /// <summary>A wedge: a box with its top edge sheared, so two faces slant.</summary>
    private static IvpCompactLedge Wedge(int variant)
    {
        float s = 13 + (variant * 7);
        return Verts([(0, 0, 0), (64, 0, 0), (0, 32, 0), (64, 32, 0), (s, 0, 48), (64 - s, 0, 40), (s, 32, 48), (64 - s, 32, 40)]);
    }

    /// <summary>The hull of a seeded cloud of points: triangles facing every way.</summary>
    private static IvpCompactLedge Cloud(int seed)
    {
        Random random = new(seed);
        (float X, float Y, float Z)[] points = new (float X, float Y, float Z)[24];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = ((float)(random.NextDouble() * 90) - 30, (float)(random.NextDouble() * 70) + 5, (float)(random.NextDouble() * 50) - 60);
        }

        return Verts(points);
    }

    private static IvpCompactLedge Verts(ReadOnlySpan<(float X, float Y, float Z)> points) =>
        IvpCooker<double, CorrectPrecision>.ConvexFromVerts(points, new IvpCookContext(new QhullRunner()))
        ?? throw new InvalidOperationException("the points make no convex");

    /// <summary>
    /// The reference's rotation inertia: one pass over every triangle per axis, each making
    /// the triangle's normal and mass-centre-frame corners afresh, with all three integrals.
    /// </summary>
    private static class ThreePass<T, TP>
        where T : unmanaged, IBinaryFloatingPointIeee754<T>
        where TP : struct, IIvpPrecision<T>
    {
        public static (T X, T Y, T Z) Inertia(List<IvpCompactLedge> ledges, (T X, T Y, T Z) mc, bool skip)
        {
            T b0 = AxisMoment(ledges, mc, 0, 1, 2, skip);
            T b1 = AxisMoment(ledges, mc, 1, 2, 0, skip);
            T b2 = AxisMoment(ledges, mc, 2, 0, 1, skip);
            return (T.Sqrt((b1 * b1) + (b2 * b2)), T.Sqrt((b2 * b2) + (b0 * b0)), T.Sqrt((b0 * b0) + (b1 * b1)));
        }

        private static T AxisMoment(List<IvpCompactLedge> ledges, (T X, T Y, T Z) mc, int a, int b, int c, bool skip)
        {
            double acc1 = 0.0, acc2 = 0.0, acc3 = 0.0;
            for (int l = ledges.Count - 1; l >= 0; l--)
            {
                for (int t = 0; t < ledges[l].TriangleCount; t++)
                {
                    Integrand(ledges[l], t, mc, a, b, c, skip, ref acc1, ref acc2, ref acc3);
                }
            }

            return double.CreateTruncating(IvpLedgeSolver<T, TP>.Eps) > acc1 ? T.One : T.CreateTruncating(acc3 / acc1);
        }

        private static (float X, float Y, float Z) ToMassFrame(IvpCompactLedge ledge, int point, (T X, T Y, T Z) mc)
        {
            (float px, float py, float pz) = ledge.Point(point);
            T dx = T.CreateTruncating(px) - mc.X;
            T dy = T.CreateTruncating(py) - mc.Y;
            T dz = T.CreateTruncating(pz) - mc.Z;
            T one = T.One, zero = T.Zero;
            T qx = ((one * dx) + (zero * dy)) + (zero * dz);
            T qy = ((zero * dx) + (one * dy)) + (zero * dz);
            T qz = ((zero * dy) + (zero * dx)) + (one * dz);
            return (float.CreateTruncating(qx), float.CreateTruncating(qy), float.CreateTruncating(qz));
        }

        private static float Axis((float X, float Y, float Z) v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

        private static T Axis((T X, T Y, T Z) v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

        private static T RealLength(T dx, T dy, T dz, float fx, float fy, float fz) =>
            TP.IsDouble
                ? T.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz))
                : T.CreateTruncating(MathF.Sqrt(((fx * fx) + (fy * fy)) + (fz * fz)));

        private static void Integrand(
            IvpCompactLedge ledge, int tri, (T X, T Y, T Z) mc, int a, int b, int c, bool skip,
            ref double acc1, ref double acc2, ref double acc3)
        {
            (T X, T Y, T Z) n = IvpLedgeSolver<T, TP>.TriangleNormal(ledge, tri, 0);
            T nx = n.X, ny = n.Y, nz = n.Z;
            IvpLedgeSolver<T, TP>.NormizePoint(ref nx, ref ny, ref nz);
            n = (nx, ny, nz);
            T nb = Axis(n, b), nc = Axis(n, c);
            bool mode1;
            double e = 0.0, dcoef = 0.0;
            if (T.Abs(nc) < T.Abs(nb))
            {
                mode1 = false;
                e = double.CreateTruncating((nc * T.CreateTruncating(0.5f)) / nb);
            }
            else
            {
                mode1 = true;
                if (IvpLedgeSolver<T, TP>.SolverEps < T.Abs(nc))
                {
                    dcoef = double.CreateTruncating((nb * T.CreateTruncating(-0.5f)) / nc);
                }
            }

            double l1 = 0.0, l2 = 0.0, l3 = 0.0;
            for (int k = 0; k < 3; k++)
            {
                (float X, float Y, float Z) q0 = ToMassFrame(ledge, ledge.EdgeStart(tri, k), mc);
                (float X, float Y, float Z) q1 = ToMassFrame(ledge, ledge.NextStart(tri, k), mc);
                float dfx = q1.X - q0.X, dfy = q1.Y - q0.Y, dfz = q1.Z - q0.Z;
                T dx = T.CreateTruncating(dfx), dy = T.CreateTruncating(dfy), dz = T.CreateTruncating(dfz);
                (T X, T Y, T Z) d = (dx, dy, dz);
                T len = RealLength(dx, dy, dz, dfx, dfy, dfz);
                if (len * IvpLedgeSolver<T, TP>.SolverEps > T.Abs(Axis(d, a)))
                {
                    continue;
                }

                if (skip && len == T.Zero)
                {
                    continue;
                }

                double s = double.CreateTruncating(X86Nan.Divide(Axis(d, b), Axis(d, a)));
                double q0a = Axis(q0, a);
                double ib = Axis(q0, b) - (q0a * s);
                double p, q, r;
                if (mode1)
                {
                    double t = dcoef * ib;
                    p = ib * t;
                    q = (s * s) * dcoef;
                    r = (s + s) * t;
                }
                else
                {
                    double sc = double.CreateTruncating(X86Nan.Divide(Axis(d, c), Axis(d, a)));
                    double ic = Axis(q0, c) - (q0a * sc);
                    double u = e * ic;
                    p = (u + ib) * ic;
                    r = ((ib + (u + u)) * sc) + (ic * s);
                    q = (s + (e * sc)) * sc;
                }

                double q1a = Axis(q1, a);
                double q0a2 = q0a * q0a;
                double q1a2 = q1a * q1a;
                double q1a3 = q1a2 * q1a;
                double h2 = (q1a2 - q0a2) * 0.5;
                double q0a3 = q0a2 * q0a;
                double q1a4 = q1a3 * q1a;
                double h3 = (q1a3 - q0a3) * 0.3333333432674408;
                double q0a4 = q0a3 * q0a;
                double q1a5 = q1a4 * q1a;
                double q0a5 = q0a4 * q0a;
                double h1 = q1a - q0a;
                double h4 = (q1a4 - q0a4) * 0.25;
                double h5 = (q1a5 - q0a5) * 0.20000000298023224;
                l1 = ((h1 * p) + (r * h2)) + ((q * h3) + l1);
                l2 = ((h2 * p) + (r * h3)) + ((q * h4) + l2);
                l3 = ((p * h3) + (r * h4)) + (l3 + (h5 * q));
            }

            acc3 = l3 + acc3;
            acc1 = l1 + acc1;
            acc2 = l2 + acc2;
        }
    }
}
