//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Numerics;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// <c>IVP_SurfaceBuilder_Pointsoup</c>: the convex hull of a point soup, via qhull, as a compact
/// ledge.
/// </summary>
/// <remarks>
/// Mirrors the point-soup builder, checked against the TF2 build: <c>convert_pointsoup_to_compact_ledge</c>,
/// The qhull driver with its retry loop, facets to
/// Polygons with the polygon area and perimeter,
/// And the template-polygon builder.
/// </remarks>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
/// <typeparam name="TP">The precision policy.</typeparam>
internal static class IvpPointSoup<T, TP>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
    where TP : struct, IIvpPrecision<T>
{
    /// <summary>The first qhull command IVP runs (both builds).</summary>
    public const string QhullCommand = "qhull Qs Pp C-0 W1e-14 E1.0e-6";

    /// <summary>
    /// fewer than three points is no ledge, three is a flat two-sided triangle, more go
    /// through qhull.
    /// </summary>
    /// <param name="points">The point soup.</param>
    /// <param name="context">Per-thread scratch and the qhull runner.</param>
    /// <returns>The compact ledge, or null when no hull could be built.</returns>
    public static IvpCompactLedge? ToCompactLedge(List<IvpPoint<T>> points, IvpCookContext context)
    {
        if (points.Count < 3)
        {
            return null;
        }

        if (points.Count == 3)
        {
            return IvpTriangleLedge<T, TP>.Build(points[0], points[1], points[2]);
        }

        return ConvertViaQhull(points, context);
    }

    /// <summary>
    /// dedupe, run qhull, drop points that make sliver facets and retry, and on failure
    /// retry with joggle (<c>QJ</c>) growing from 1e-12 by x1.2 up to 0.02.
    /// </summary>
    /// <param name="input">The points; their fourth component is zeroed, as IVP does.</param>
    /// <param name="context">Scratch.</param>
    /// <returns>The ledge or null.</returns>
    private static IvpCompactLedge? ConvertViaQhull(List<IvpPoint<T>> input, IvpCookContext context)
    {
        // Bit-exact de-duplication (the IVP point hash compares the coordinate bytes, so +0 and -0
        // are different points here).
        var unique = new List<IvpPoint<T>>(input.Count);
        var seen = new HashSet<(T, T, T)>(new BitwiseTripleComparer());
        foreach (IvpPoint<T> p in input)
        {
            p.W = T.Zero;
            if (seen.Add((p.X, p.Y, p.Z)))
            {
                unique.Add(p);
            }
        }

        int n = unique.Count;
        double[] coords = new double[n * 3];
        for (int i = 0; i < n; i++)
        {
            coords[(3 * i) + 0] = double.CreateTruncating(unique[i].X);
            coords[(3 * i) + 1] = double.CreateTruncating(unique[i].Y);
            coords[(3 * i) + 2] = double.CreateTruncating(unique[i].Z);
        }

        byte[] removed = new byte[n];
        byte[] used = new byte[n];
        bool plain = true;
        const double JoggleStart = 9.999999960041972e-13;
        const double JoggleGrowth = 1.2000000476837158;
        const double JoggleLimit = 0.019999999552965164;
        double joggle = JoggleStart;
        IQhullRunner qhull = context.Qhull;

        while (true)
        {
            bool built = false;
            if (plain)
            {
                built = qhull.Run(coords.AsSpan(0, n * 3), n, QhullCommand) == 0;
            }

            if (!built)
            {
                // "*** Qhull failed. Retrying with different parameters."
                string cmd = "qhull Qs QJ" + FormatG(joggle) + " C-0 Pp W1e-14 E1.0e-18";
                built = qhull.Run(coords.AsSpan(0, n * 3), n, cmd) == 0;
                if (!built)
                {
                    plain = false;
                    joggle = (JoggleStart + joggle) * JoggleGrowth;
                    if (JoggleLimit <= joggle)
                    {
                        return null;
                    }

                    continue;
                }
            }

            Array.Clear(used, 0, n);
            Array.Clear(removed, 0, n);
            IvpCompactLedge? ledge = FacetsToLedge(unique, qhull, removed, used, context, out bool retry);
            if (ledge is not null || !retry)
            {
                // A null ledge without a retry request (the polygon builder failed): IVP falls
                // through to the compaction below as well, but with nothing removed and every
                // point used it only changes "plain"; reproduce that.
                if (ledge is not null)
                {
                    return ledge;
                }
            }

            if (n == 0)
            {
                return null;
            }

            // Keep the points that were on the hull and were not chosen for removal.
            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                if (used[i] != 0 && removed[i] == 0)
                {
                    coords[(3 * kept) + 0] = coords[(3 * i) + 0];
                    coords[(3 * kept) + 1] = coords[(3 * i) + 1];
                    coords[(3 * kept) + 2] = coords[(3 * i) + 2];
                    unique[kept] = unique[i];
                    kept++;
                }
            }

            unique.RemoveRange(kept, unique.Count - kept);
            if (kept == n)
            {
                plain = false;
                joggle = (JoggleStart + joggle) * JoggleGrowth;
            }

            if (kept == 3)
            {
                return IvpTriangleLedge<T, TP>.Build(unique[0], unique[1], unique[2]);
            }

            n = kept;
            if (kept < 4)
            {
                return null;
            }

            if (JoggleLimit <= joggle)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// C's <c>printf("%G")</c>: six significant digits, exponent form below 1e-4. qhull parses the
    /// option back with strtod, so only the rounded value matters.
    /// </summary>
    /// <param name="value">A positive joggle.</param>
    /// <returns>The text.</returns>
    internal static string FormatG(double value)
    {
        // "E" form with 6 significant digits, then trim trailing zeros as %G does; the parse of
        // either form gives the same double, which is all qhull sees.
        return value.ToString("0.#####E+00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// one polygon per qhull facet; a sliver facet (area &lt; 0.005 * perimeter) marks one
    /// of its middle points for removal and asks for a retry instead of building.
    /// </summary>
    private static IvpCompactLedge? FacetsToLedge(
        List<IvpPoint<T>> unique,
        IQhullRunner qhull,
        byte[] removed,
        byte[] used,
        IvpCookContext context,
        out bool retry)
    {
        retry = false;
        var polygons = new List<IvpFacetPolygon<T>>(qhull.FacetCount);
        for (int f = 0; f < qhull.FacetCount; f++)
        {
            (double nx, double ny, double nz) = qhull.Normal(f);
            var poly = new IvpFacetPolygon<T>(T.CreateTruncating(nx), T.CreateTruncating(ny), T.CreateTruncating(nz));
            polygons.Add(poly);
            ReadOnlySpan<int> ids = qhull.Vertices(f);
            context.Trace?.Invoke("facet " + f + " ids " + string.Join(",", ids.ToArray()));
            foreach (int id in ids)
            {
                poly.Points.Add(unique[id]);
                poly.Ids.Add(id);
                used[id]++;
            }

            T area = Area(poly);
            T perimeter = Perimeter(poly);
            if (area < T.Zero)
            {
                area = -area;
                poly.NX = -poly.NX;
                poly.NY = -poly.NY;
                poly.NZ = -poly.NZ;
            }

            int count = poly.Points.Count;
            if (!(double.CreateTruncating(area) < double.CreateTruncating(perimeter) * 0.004999999888241291) || count == 0)
            {
                continue;
            }

            // Sliver. Skip it if one of its points is already marked.
            bool skip = false;
            foreach (int id in poly.Ids)
            {
                if (removed[id] != 0)
                {
                    skip = true;
                    break;
                }
            }

            if (skip)
            {
                continue;
            }

            int far = 0;
            T best = -T.One;
            for (int k = 0; k < count; k++)
            {
                IvpPoint<T> a = poly.Points[0], b = poly.Points[k];
                T dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                T d = T.Sqrt((dz * dz) + ((dx * dx) + (dy * dy)));
                if (best < d)
                {
                    far = k;
                }

                best = MaxSse(best, d);
            }

            int far2 = 0;
            best = -T.One;
            for (int k = 0; k < count; k++)
            {
                retry = true;
                IvpPoint<T> a = poly.Points[far], b = poly.Points[k];
                T dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                T d = T.Sqrt((dz * dz) + ((dy * dy) + (dx * dx)));
                if (best < d)
                {
                    far2 = k;
                }

                best = MaxSse(best, d);
            }

            int victim;
            if (far2 == 0 || far == 0)
            {
                victim = -1;
                for (int k = 1; k < count; k++)
                {
                    if (k != far2 && k != far)
                    {
                        victim = k;
                        break;
                    }
                }

                if (victim < 0)
                {
                    continue;
                }
            }
            else
            {
                victim = 0;
            }

            removed[poly.Ids[victim]]++;
        }

        if (retry)
        {
            return null;
        }

        IvpTemplatePolygon<T> template = IvpTemplatePolygon<T>.Build(unique, polygons);
        return IvpPolygonTetra<T, TP>.ToCompactLedge(template, context);
    }

    /// <summary><c>maxss acc, d</c>: <c>acc &gt; d ? acc : d</c>.</summary>
    private static T MaxSse(T acc, T d) => acc > d ? acc : d;

    /// <summary>: the fan sum of <c>cross(p[i+1]-p[i], p[i]-p[0]). normal</c>.</summary>
    private static T Area(IvpFacetPolygon<T> poly)
    {
        int n = poly.Points.Count;
        if (n == 0)
        {
            return T.Zero;
        }

        IvpPoint<T> p0 = poly.Points[0];
        T acc = T.Zero;
        for (int i = 0; i < n; i++)
        {
            IvpPoint<T> pi = poly.Points[i];
            IvpPoint<T> pn = poly.Points[(i + 1) % n];
            T ax = pn.X - pi.X, ay = pn.Y - pi.Y, az = pn.Z - pi.Z;
            T bx = pi.X - p0.X, by = pi.Y - p0.Y, bz = pi.Z - p0.Z;
            IvpVector.Cross(ax, ay, az, bx, by, bz, out T cx, out T cy, out T cz);
            acc = ((cx * poly.NX) + (cy * poly.NY)) + ((cz * poly.NZ) + acc);
        }

        return acc;
    }

    /// <summary>: the sum of edge lengths.</summary>
    private static T Perimeter(IvpFacetPolygon<T> poly)
    {
        int n = poly.Points.Count;
        T acc = T.Zero;
        for (int i = 0; i < n; i++)
        {
            IvpPoint<T> a = poly.Points[i];
            IvpPoint<T> b = poly.Points[(i + 1) % n];
            T dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            acc += T.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz));
        }

        return acc;
    }

    /// <summary>Bitwise equality of coordinate triples, as the IVP point hash's memcmp.</summary>
    private sealed class BitwiseTripleComparer : IEqualityComparer<(T, T, T)>
    {
        public bool Equals((T, T, T) a, (T, T, T) b) =>
            Bits(a.Item1) == Bits(b.Item1) && Bits(a.Item2) == Bits(b.Item2) && Bits(a.Item3) == Bits(b.Item3);

        public int GetHashCode((T, T, T) v) => HashCode.Combine(Bits(v.Item1), Bits(v.Item2), Bits(v.Item3));

        private static long Bits(T v) =>
            typeof(T) == typeof(float)
                ? BitConverter.SingleToInt32Bits(float.CreateTruncating(v))
                : BitConverter.DoubleToInt64Bits(double.CreateTruncating(v));
    }
}

/// <summary>A qhull facet as IVP holds it before the template: normal and point list.</summary>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
internal sealed class IvpFacetPolygon<T>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
{
    /// <summary>Normal X.</summary>
    public T NX;

    /// <summary>Normal Y.</summary>
    public T NY;

    /// <summary>Normal Z.</summary>
    public T NZ;

    /// <summary>The facet's points in qh_facet3vertex order.</summary>
    public readonly List<IvpPoint<T>> Points = [];

    /// <summary>Their indices in the unique point list.</summary>
    public readonly List<int> Ids = [];

    /// <summary>Creates a polygon.</summary>
    /// <param name="nx">Normal X.</param>
    /// <param name="ny">Normal Y.</param>
    /// <param name="nz">Normal Z.</param>
    public IvpFacetPolygon(T nx, T ny, T nz)
    {
        NX = nx;
        NY = ny;
        NZ = nz;
    }
}
