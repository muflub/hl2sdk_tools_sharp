using System.Numerics;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// <c>IVP_Compact_Ledge_Solver</c>: bounding boxes, volume integrals, mass centre, rotation
/// inertia, radius and surface deviation of compact ledges.
/// </summary>
/// <remarks>
/// Decompiled from SDK 2013 (TF2): calc_bounding_box 001c0980 (001c5650), the triangle normal
/// 001c13a0 (001c61d0), the mass-centre integrand 001a3e90 (001a89a0) and its loop 001a4090
/// (001a8c90), the inertia integrand 001a3a00 (001a84c0) and loop 001a4110 (001a8d20), the
/// finaliser 001a4280 (001a8e80), the flat-ledge fallback 001a3880 (001a8310), and the radius /
/// deviation pass 001c1650/001c1480 (001c64c0/001c62d0). The two builds group every expression
/// here identically except where noted; the note says what the difference is.
/// </remarks>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
/// <typeparam name="TP">The precision policy.</typeparam>
internal static class IvpLedgeSolver<T, TP>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
    where TP : struct, IIvpPrecision<T>
{
    /// <summary>The build's <c>P_DOUBLE_EPS</c> (1e-10f / 1e-19).</summary>
    public static T Eps => TP.IsDouble ? T.CreateTruncating(1e-19) : T.CreateTruncating(1.0e-10f);

    /// <summary>The solver's resolution epsilon (1e-6f in SDK, 1e-12 in TF2).</summary>
    public static T SolverEps => TP.IsDouble ? T.CreateTruncating(1e-12) : T.CreateTruncating(1.0e-6f);

    /// <summary>001c0980: min and max over every triangle corner of a ledge.</summary>
    /// <param name="ledge">The ledge.</param>
    /// <param name="min">Minimum.</param>
    /// <param name="max">Maximum.</param>
    public static void BoundingBox(IvpCompactLedge ledge, out (T X, T Y, T Z) min, out (T X, T Y, T Z) max)
    {
        (float fx, float fy, float fz) = ledge.Point(ledge.EdgeStart(0, 0));
        T x0 = T.CreateTruncating(fx), y0 = T.CreateTruncating(fy), z0 = T.CreateTruncating(fz);
        T x1 = x0, y1 = y0, z1 = z0;
        int n = ledge.TriangleCount;
        for (int t = 0; t < n; t++)
        {
            for (int e = 0; e < 3; e++)
            {
                (float px, float py, float pz) = ledge.Point(ledge.EdgeStart(t, e));
                T x = T.CreateTruncating(px), y = T.CreateTruncating(py), z = T.CreateTruncating(pz);
                if (x < x0)
                {
                    x0 = x;
                }

                if (x1 <= x)
                {
                    x1 = x;
                }

                if (y < y0)
                {
                    y0 = y;
                }

                if (y1 <= y)
                {
                    y1 = y;
                }

                if (z < z0)
                {
                    z0 = z;
                }

                if (z1 <= z)
                {
                    z1 = z;
                }
            }
        }

        min = (x0, y0, z0);
        max = (x1, y1, z1);
    }

    /// <summary>
    /// 001c13a0: a triangle's (unnormalised) normal (next - base) x (prev - base), from the
    /// ledge's float points widened to IVP_DOUBLE.
    /// </summary>
    /// <param name="ledge">The ledge.</param>
    /// <param name="tri">Triangle.</param>
    /// <param name="edge">The base edge.</param>
    /// <returns>The normal.</returns>
    public static (T X, T Y, T Z) TriangleNormal(IvpCompactLedge ledge, int tri, int edge)
    {
        (float bx, float by, float bz) = ledge.Point(ledge.EdgeStart(tri, edge));
        (float nx, float ny, float nz) = ledge.Point(ledge.NextStart(tri, edge));
        (float px, float py, float pz) = ledge.Point(ledge.PrevStart(tri, edge));
        T py_ = T.CreateTruncating(py) - T.CreateTruncating(by);
        T px_ = T.CreateTruncating(px) - T.CreateTruncating(bx);
        T nx_ = T.CreateTruncating(nx) - T.CreateTruncating(bx);
        T nz_ = T.CreateTruncating(nz) - T.CreateTruncating(bz);
        T pz_ = T.CreateTruncating(pz) - T.CreateTruncating(bz);
        T ny_ = T.CreateTruncating(ny) - T.CreateTruncating(by);
        T oy = (nz_ * px_) - (pz_ * nx_);
        T ox = (pz_ * ny_) - (py_ * nz_);
        T oz = (py_ * nx_) - (px_ * ny_);
        return (ox, oy, oz);
    }

    /// <summary>
    /// 001a4280 via 001a4090 / 001a4110: the mass centre and rotation inertia of a set of ledges,
    /// or the flat fallback 001a3880 when the volume is negligible.
    /// </summary>
    /// <param name="ledges">The ledges (the ledge tree's leaves, left first).</param>
    /// <param name="massCenter">The mass centre.</param>
    /// <param name="inertia">The rotation inertia.</param>
    /// <param name="skipZeroLengthEdges">Skip zero-length edges in the inertia integral (the correct-mode fix).</param>
    public static void MassProperties(List<IvpCompactLedge> ledges, out (T X, T Y, T Z) massCenter, out (T X, T Y, T Z) inertia, bool skipZeroLengthEdges = false)
    {
        // 001a4090: ledges from the last, triangles in order.
        float sx = 0f, sy = 0f, sz = 0f;
        T area = T.Zero, volume = T.Zero;
        for (int l = ledges.Count - 1; l >= 0; l--)
        {
            IvpCompactLedge ledge = ledges[l];
            for (int t = 0; t < ledge.TriangleCount; t++)
            {
                VolumeIntegrand(ledge, t, ref sx, ref sy, ref sz, ref area, ref volume);
            }
        }

        // 001a4280: flat when vol <= (area * 1e-9f) * sqrt(area).
        T flatLimit = (area * T.CreateTruncating(1.0e-9f)) * T.Sqrt(area);
        if (volume <= flatLimit)
        {
            FlatFallback(ledges, out massCenter, out inertia);
            return;
        }

        T inv = T.One / volume;
        massCenter = (
            T.CreateTruncating(sx) * inv,
            T.CreateTruncating(sy) * inv,
            inv * T.CreateTruncating(sz));

        T b0 = AxisMoment(ledges, massCenter, 0, 1, 2, skipZeroLengthEdges);
        T b1 = AxisMoment(ledges, massCenter, 1, 2, 0, skipZeroLengthEdges);
        T b2 = AxisMoment(ledges, massCenter, 2, 0, 1, skipZeroLengthEdges);
        inertia = (
            T.Sqrt((b1 * b1) + (b2 * b2)),
            T.Sqrt((b2 * b2) + (b0 * b0)),
            T.Sqrt((b0 * b0) + (b1 * b1)));
    }

    /// <summary>
    /// 001a3e90: one triangle's contribution to the volume, area and first-moment sums. The
    /// triple product and the moment sums are float in both builds; SDK folds the area sum as
    /// <c>(cy^2 + cx^2) + (cz^2 + acc)</c> in float, TF2 adds <c>(float)((cx^2 + cy^2) + cz^2)</c>
    /// to a double accumulator.
    /// </summary>
    private static void VolumeIntegrand(IvpCompactLedge ledge, int tri, ref float sx, ref float sy, ref float sz, ref T area, ref T volume)
    {
        (float px, float py, float pz) = ledge.Point(ledge.EdgeStart(tri, 0));
        (float nx, float ny, float nz) = ledge.Point(ledge.NextStart(tri, 0));
        (float vx, float vy, float vz) = ledge.Point(ledge.PrevStart(tri, 0));
        T nzp = T.CreateTruncating(nz) - T.CreateTruncating(pz);
        T vyp = T.CreateTruncating(vy) - T.CreateTruncating(py);
        T vxp = T.CreateTruncating(vx) - T.CreateTruncating(px);
        T vzp = T.CreateTruncating(vz) - T.CreateTruncating(pz);
        T nxp = T.CreateTruncating(nx) - T.CreateTruncating(px);
        T nyp = T.CreateTruncating(ny) - T.CreateTruncating(py);
        float cy = float.CreateTruncating((nzp * vxp) - (vzp * nxp));
        float cx = float.CreateTruncating((vzp * nyp) - (vyp * nzp));
        float cz = float.CreateTruncating((vyp * nxp) - (vxp * nyp));
        float triple = ((py * cy) + (px * cx)) + (pz * cz);
        if (TP.IsDouble)
        {
            area += T.CreateTruncating(((cx * cx) + (cy * cy)) + (cz * cz));
        }
        else
        {
            float a = float.CreateTruncating(area);
            area = T.CreateTruncating(((cy * cy) + (cx * cx)) + ((cz * cz) + a));
        }

        volume += T.CreateTruncating(triple);
        T fac = T.CreateTruncating(triple) * T.CreateTruncating(0.25f);
        sx = float.CreateTruncating(T.CreateTruncating(sx) + (T.CreateTruncating(px) * fac));
        sy = float.CreateTruncating(T.CreateTruncating(sy) + (T.CreateTruncating(py) * fac));
        sz = float.CreateTruncating(T.CreateTruncating(sz) + (T.CreateTruncating(pz) * fac));
        sx = float.CreateTruncating(T.CreateTruncating(sx) + (T.CreateTruncating(nx) * fac));
        sy = float.CreateTruncating(T.CreateTruncating(sy) + (T.CreateTruncating(ny) * fac));
        sz = float.CreateTruncating(T.CreateTruncating(sz) + (T.CreateTruncating(nz) * fac));
        sx = float.CreateTruncating(T.CreateTruncating(sx) + (T.CreateTruncating(vx) * fac));
        sy = float.CreateTruncating(T.CreateTruncating(sy) + (T.CreateTruncating(vy) * fac));
        sz = float.CreateTruncating(T.CreateTruncating(sz) + (T.CreateTruncating(vz) * fac));
    }

    /// <summary>
    /// 001a4110: the second moment about one axis, as <c>acc3/acc1</c> of the per-edge integrals,
    /// or 1 when the first integral is below <c>P_DOUBLE_EPS</c>.
    /// </summary>
    private static T AxisMoment(List<IvpCompactLedge> ledges, (T X, T Y, T Z) mc, int a, int b, int c, bool skipZeroLengthEdges)
    {
        double acc1 = 0.0, acc2 = 0.0, acc3 = 0.0;
        for (int l = ledges.Count - 1; l >= 0; l--)
        {
            IvpCompactLedge ledge = ledges[l];
            for (int t = 0; t < ledge.TriangleCount; t++)
            {
                InertiaIntegrand(ledge, t, mc, a, b, c, skipZeroLengthEdges, ref acc1, ref acc2, ref acc3);
            }
        }

        // comisd acc1, eps ; ja fallback -- a NaN acc1 takes the divide, as the binary does.
        if (double.CreateTruncating(Eps) > acc1)
        {
            return T.One;
        }

        return T.CreateTruncating(acc3 / acc1);
    }

    /// <summary>The ledge point moved into the mass-centre frame (00202e40 with an identity rotation).</summary>
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

    private static float Axis((float X, float Y, float Z) v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    private static T Axis((T X, T Y, T Z) v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    /// <summary>
    /// 001a3a00: one triangle's contribution to the three moment integrals about axis
    /// <paramref name="a"/>, integrating along each edge's projection. The per-edge polynomial is
    /// double in both builds; the normal, slopes and threshold are IVP_DOUBLE.
    /// </summary>
    private static void InertiaIntegrand(
        IvpCompactLedge ledge, int tri, (T X, T Y, T Z) mc, int a, int b, int c, bool skipZeroLengthEdges,
        ref double acc1, ref double acc2, ref double acc3)
    {
        (T X, T Y, T Z) n = TriangleNormal(ledge, tri, 0);
        T nx = n.X, ny = n.Y, nz = n.Z;
        NormizePoint(ref nx, ref ny, ref nz);
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
            if (SolverEps < T.Abs(nc))
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
            if (len * SolverEps > T.Abs(Axis(d, a)))
            {
                continue;
            }

            // Stock divides 0 by 0 here when a ledge has two coincident points (the double build
            // de-duplicates in double, then rounds the survivors to float), and the NaN reaches
            // rotation_inertia. A zero-length edge contributes nothing to the integral.
            if (skipZeroLengthEdges && len == T.Zero)
            {
                continue;
            }

            double s = double.CreateTruncating(Axis(d, b) / Axis(d, a));
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
                double sc = double.CreateTruncating(Axis(d, c) / Axis(d, a));
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

    /// <summary>
    /// <c>IVP_U_Point::real_length</c> of the edge vector: SDK 001ff650 in float, TF2 00208930 in
    /// double over the widened float difference.
    /// </summary>
    private static T RealLength(T dx, T dy, T dz, float fx, float fy, float fz)
    {
        if (TP.IsDouble)
        {
            return T.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz));
        }

        return T.CreateTruncating(MathF.Sqrt(((fx * fx) + (fy * fy)) + (fz * fz)));
    }

    /// <summary>
    /// <c>IVP_U_Point::normize</c> for IVP_DOUBLE: SDK 001ff790 (rsqrtss + one Newton step, the
    /// same code as the float point), TF2 00208ed0 (bit-hack isqrt with five double Newton steps).
    /// </summary>
    public static void NormizePoint(ref T x, ref T y, ref T z)
    {
        if (TP.IsDouble)
        {
            double dx = double.CreateTruncating(x), dy = double.CreateTruncating(y), dz = double.CreateTruncating(z);
            double s = ((dx * dx) + (dy * dy)) + (dz * dz);
            if (!(1e-19 <= s))
            {
                return;
            }

            double half = s * 0.5;
            int hi = (int)(BitConverter.DoubleToInt64Bits(s) >> 32);
            int guess = ((0x7ff00000 - hi) >> 1) + 0x1ff00000;
            double r = BitConverter.Int64BitsToDouble((long)(uint)guess << 32);
            r *= 1.5 - ((r * r) * half);
            r *= 1.5 - ((r * r) * half);
            r *= 1.5 - ((r * r) * half);
            r *= 1.5 - ((r * r) * half);
            r *= 1.5 - (half * (r * r));
            x = T.CreateTruncating(dx * r);
            y = T.CreateTruncating(dy * r);
            z = T.CreateTruncating(r * dz);
            return;
        }

        float fx = float.CreateTruncating(x), fy = float.CreateTruncating(y), fz = float.CreateTruncating(z);
        TP.NormizeFloatPoint(ref fx, ref fy, ref fz);
        x = T.CreateTruncating(fx);
        y = T.CreateTruncating(fy);
        z = T.CreateTruncating(fz);
    }

    /// <summary>
    /// 001a3880: a flat set of ledges gets the bounding-box centre as its mass centre and
    /// <c>((|max-min|/2)^2)/2</c> on every axis as its inertia.
    /// </summary>
    private static void FlatFallback(List<IvpCompactLedge> ledges, out (T X, T Y, T Z) massCenter, out (T X, T Y, T Z) inertia)
    {
        BoundingBox(ledges[0], out (T X, T Y, T Z) min, out (T X, T Y, T Z) max);
        for (int l = ledges.Count - 1; l >= 1; l--)
        {
            BoundingBox(ledges[l], out (T X, T Y, T Z) lo, out (T X, T Y, T Z) hi);
            min = (lo.X < min.X ? lo.X : min.X, lo.Y < min.Y ? lo.Y : min.Y, lo.Z < min.Z ? lo.Z : min.Z);
            max = (max.X < hi.X ? hi.X : max.X, max.Y < hi.Y ? hi.Y : max.Y, max.Z < hi.Z ? hi.Z : max.Z);
        }

        T half = T.CreateTruncating(0.5f);
        massCenter = Interpolate(half, min, max);
        T dx = min.X - max.X, dy = min.Y - max.Y, dz = min.Z - max.Z;
        T r = T.Sqrt((dz * dz) + ((dx * dx) + (dy * dy))) * half;
        T i = (r * r) * half;
        inertia = (i, i, i);
    }

    /// <summary>
    /// 001ff5f0 <c>set_interpolate(a, b, t)</c> as grouped by GCC: x = t*b.x + a.x*(1-t),
    /// y = b.y*t + a.y*(1-t), z = (1-t)*a.z + b.z*t.
    /// </summary>
    /// <param name="t">Weight of b.</param>
    /// <param name="a">First.</param>
    /// <param name="b">Second.</param>
    /// <returns>The blend.</returns>
    public static (T X, T Y, T Z) Interpolate(T t, (T X, T Y, T Z) a, (T X, T Y, T Z) b)
    {
        T u = T.One - t;
        return ((t * b.X) + (a.X * u), (b.Y * t) + (a.Y * u), (u * a.Z) + (b.Z * t));
    }

    /// <summary>
    /// 001c1650/001c1480: the largest distance of any corner from the mass centre, and the
    /// largest distance of any corner from the line through the mass centre along its triangle's
    /// normal (the surface deviation).
    /// </summary>
    /// <param name="ledges">The leaves.</param>
    /// <param name="mc">Mass centre.</param>
    /// <param name="radius">Radius.</param>
    /// <param name="deviation">Maximum deviation.</param>
    public static void RadiusAndDeviation(List<IvpCompactLedge> ledges, (T X, T Y, T Z) mc, out T radius, out T deviation)
    {
        radius = T.Zero;
        deviation = T.Zero;
        for (int l = ledges.Count - 1; l >= 0; l--)
        {
            IvpCompactLedge ledge = ledges[l];
            T r2max = T.Zero, dev2max = T.Zero;
            int n = ledge.TriangleCount;
            if (n < 1)
            {
                continue;
            }

            for (int t = 0; t < n; t++)
            {
                (T nx, T ny, T nz) = TriangleNormal(ledge, t, 0);
                T inv = T.One / (((ny * ny) + (nx * nx)) + (nz * nz));
                for (int k = 0; k < 3; k++)
                {
                    (float px, float py, float pz) = ledge.Point(ledge.EdgeStart(t, k));
                    T dx = T.CreateTruncating(px) - mc.X;
                    T dy = T.CreateTruncating(py) - mc.Y;
                    T dz = T.CreateTruncating(pz) - mc.Z;
                    T r2 = ((dx * dx) + (dy * dy)) + (dz * dz);
                    r2max = r2max > r2 ? r2max : r2;
                    T cx = (nz * dy) - (dz * ny);
                    T cy = (nx * dz) - (dx * nz);
                    T cz = (ny * dx) - (dy * nx);
                    T dev2 = (((cx * cx) + (cy * cy)) + (cz * cz)) * inv;
                    dev2max = dev2max > dev2 ? dev2max : dev2;
                }
            }

            T r = T.Sqrt(r2max);
            if (radius < r)
            {
                radius = r;
            }

            T dv = T.Sqrt(dev2max);
            if (deviation < dv)
            {
                deviation = dv;
            }
        }
    }
}
