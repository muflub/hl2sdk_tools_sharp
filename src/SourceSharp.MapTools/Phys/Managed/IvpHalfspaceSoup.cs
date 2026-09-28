//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// An <c>IVP_U_Point</c> / <c>IVP_U_Hesse</c>: three coordinates and a fourth value (the hesse
/// distance for a plane, unused for a point), all <c>IVP_DOUBLE</c>.
/// </summary>
/// <remarks>
/// A class rather than a struct on purpose: IVP identifies points by address (point hashes,
/// <c>IVP_U_Vector</c> removal by pointer), and the port reproduces that with reference identity.
/// </remarks>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
internal sealed class IvpPoint<T>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
{
    /// <summary>k[0].</summary>
    public T X;

    /// <summary>k[1].</summary>
    public T Y;

    /// <summary>k[2].</summary>
    public T Z;

    /// <summary>hesse_val, or the point's spare fourth component.</summary>
    public T W;

    /// <summary>Creates a point.</summary>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="z">Z.</param>
    /// <param name="w">Fourth component.</param>
    public IvpPoint(T x, T y, T z, T w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }
}

/// <summary>
/// <c>IVP_SurfaceBuilder_Halfspacesoup</c>: a convex solid given as planes, turned into the point
/// soup of its corners.
/// </summary>
/// <remarks>
/// The behaviour follows the reference IVP builders: <c>ConvexFromPlanes</c> and
/// <c>IVP_Halfspacesoup::add_halfspace</c>, the plane-triple loop
/// And <c>IVP_U_Matrix3::real_invert</c>, with the TF2 build checked for
/// Every floating-point expression. Every expression here is
/// grouped as GCC emitted it (-ffast-math reassociates, so the source grouping is not what ran);
/// both reference builds were found to group these identically.
/// </remarks>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
/// <typeparam name="TP">The precision policy.</typeparam>
internal static class IvpHalfspaceSoup<T, TP>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
    where TP : struct, IIvpPrecision<T>
{
    /// <summary><c>g_PhysicsUnits.unitScaleMeters</c>: HL inches to IVP metres.</summary>
    public const float HlToIvp = 0.0254f;

    /// <summary>
    /// <c>CPhysicsCollision::ConvexFromPlanes</c>: outward HL planes to IVP inward halfspaces.
    /// </summary>
    /// <param name="planes">Planes as (nx, ny, nz, dist), outward-facing, HL units.</param>
    /// <param name="mergeDistance">Point merge distance in HL units.</param>
    /// <param name="mergeIvp">The merge distance converted to IVP units.</param>
    /// <returns>The halfspace soup.</returns>
    public static List<IvpPoint<T>> FromHlPlanes(
        ReadOnlySpan<(float X, float Y, float Z, float Distance)> planes,
        float mergeDistance,
        out T mergeIvp)
    {
        // mergeDistance = mergeDistance * 0.0254f (float), widened to IVP_DOUBLE.
        mergeIvp = T.CreateTruncating(mergeDistance * HlToIvp);
        var soup = new List<IvpPoint<T>>(planes.Length);
        foreach ((float nx, float ny, float nz, float d) in planes)
        {
            // ConvertPlaneToIVP(-normal, -dist): k = (-nx, nz, -ny), hesse = 0.0254f * dist.
            var h = new IvpPoint<T>(
                T.CreateTruncating(-nx),
                T.CreateTruncating(nz),
                T.CreateTruncating(-ny),
                T.CreateTruncating(HlToIvp * d));
            AddHalfspace(soup, h);
        }

        return soup;
    }

    /// <summary>
    /// <c>IVP_Halfspacesoup::add_halfspace</c>: adds a halfspace unless a parallel one is already
    /// tighter; a looser or equal parallel one is replaced.
    /// </summary>
    /// <param name="soup">The soup.</param>
    /// <param name="halfspace">The new halfspace (the list takes a copy, as IVP does).</param>
    public static void AddHalfspace(List<IvpPoint<T>> soup, IvpPoint<T> halfspace)
    {
        List<IvpPoint<T>>? parallel = null;
        T threshold = T.CreateTruncating(0.9999f);
        for (int i = 0; i < soup.Count; i++)
        {
            IvpPoint<T> old = soup[i];
            T dot = ((old.X * halfspace.X) + (old.Y * halfspace.Y)) + (old.Z * halfspace.Z);
            if (threshold < dot)
            {
                if (old.W < halfspace.W)
                {
                    return; // an existing parallel plane is tighter: drop the new one
                }

                (parallel ??= []).Add(old);
            }
        }

        soup.Add(new IvpPoint<T>(halfspace.X, halfspace.Y, halfspace.Z, halfspace.W));
        if (parallel is null)
        {
            return;
        }

        // Remove the superseded planes last-collected first, each found from the end (IVP_U_Vector
        // remove by pointer), preserving the order of the rest.
        for (int k = parallel.Count - 1; k >= 0; k--)
        {
            int at = soup.LastIndexOf(parallel[k]);
            soup.RemoveAt(at);
        }
    }

    /// <summary>
    /// Intersect every plane triple, keep the points inside every halfspace, and merge
    /// points closer than the merge distance.
    /// </summary>
    /// <param name="soup">The halfspaces.</param>
    /// <param name="merge">Merge distance, IVP units.</param>
    /// <returns>The corner points, in discovery order.</returns>
    /// <remarks>
    /// <para>
    /// <b>What it computes.</b> The reference visits every triple <c>i &lt; j &lt; k</c> in
    /// lexicographic order, solves the three planes for their meeting point, keeps the point when
    /// no halfspace has it more than 1e-4 outside, and appends it unless an earlier kept point is
    /// within the merge distance; a second pass then drops later points near earlier ones. That is
    /// C(n, 3) solves, each followed by a scan of the soup, so O(n^4) in the plane count. A prop's
    /// hull is rebuilt from its own triangle planes, so a detailed prop hands this a few hundred
    /// planes: 400 planes are ten million triples, and the scan ran 14 to 27 planes deep on
    /// average before a plane rejected the point. Those two terms were almost all of a prop cook.
    /// </para>
    /// <para>
    /// <b>Why this is the same answer, bit for bit.</b> The kept points, their order and every
    /// bit of every coordinate are unchanged, because nothing that decides them is changed; only
    /// work that cannot affect them is saved:
    /// </para>
    /// <list type="bullet">
    /// <item>The matrix cofactors that depend on only two of the three planes are computed once
    /// per pair (per <c>i</c> for the <c>(i, k)</c> terms, per <c>(i, j)</c> for the
    /// <c>(i, j)</c> terms) instead of once per triple, with the very expressions, operands in the
    /// same order, that <see cref="Intersect"/> evaluates. A floating-point expression of the same
    /// inputs has the same result wherever it is evaluated, so the solved point is
    /// <see cref="Intersect"/>'s to the bit (NaN payloads included, as no operand is swapped).
    /// </item>
    /// <item>The inside test is a conjunction: the point is kept when <em>no</em> halfspace rejects
    /// it, and each halfspace's value is computed from that halfspace and the point alone. The
    /// order the halfspaces are tried in therefore cannot change the answer, only how soon a
    /// rejection is found. The eight halfspaces that most recently rejected a point are tried
    /// first: consecutive triples share two planes, so their points lie on one line, and the
    /// planes that cut that line off on either side reject again. On prop hulls of 300 to 600
    /// planes they reject all but about one triple in a thousand, and those go on to the full
    /// scan in soup order, which decides them as the reference does.</item>
    /// <item>The third planes are taken <see cref="Vector{T}.Count"/> at a time, one per vector
    /// lane. A vector add, subtract, multiply or divide rounds every lane exactly as the scalar
    /// operation does, and the JIT fuses no multiply-add it is not asked to, so each lane holds
    /// the scalar result. Lanes the recent rejecters leave standing are finished one at a time
    /// in <c>k</c> order, so kept points are merged in the reference's order.</item>
    /// <item>The merge test on insertion likewise only asks whether <em>any</em> kept point is
    /// within the merge distance; it searches newest first, because the triples that meet at one
    /// corner of a detailed hull arrive close together.</item>
    /// </list>
    /// <para>
    /// Together these make a 435-plane prop hull about six times faster; the solve itself, one
    /// division and some forty multiplies per triple, is what is left, and it stays O(n^3). A
    /// test-only copy of the reference loop, and facts comparing the two bit for bit over random,
    /// degenerate, near-coplanar, brush and prop-hull soups at both precisions, pin this.
    /// </para>
    /// </remarks>
    public static List<IvpPoint<T>> CornerPoints(List<IvpPoint<T>> soup, T merge)
    {
        T merge2 = merge * merge;
        var rows = new RowScratch(soup);
        var points = new List<IvpPoint<T>>();
        for (int i = 0; i + 1 < soup.Count; i++)
        {
            FindRow(rows, i, points, merge2);
        }

        // Second pass: drop later points within the merge distance of an earlier one. The inner
        // index runs from the list end as it stood when the outer point was taken.
        for (int i = 0; i < points.Count; i++)
        {
            IvpPoint<T> p = points[i];
            for (int j = points.Count - 1; j > i; j--)
            {
                if (j >= points.Count)
                {
                    continue;
                }

                IvpPoint<T> q = points[j];
                T dx = p.X - q.X;
                T dy = p.Y - q.Y;
                T dz = p.Z - q.Z;
                if (((dx * dx) + (dy * dy)) + (dz * dz) < merge2)
                {
                    points.RemoveAt(points.LastIndexOf(q));
                }
            }
        }

        return points;
    }

    /// <summary>
    /// Every triple whose first plane is <paramref name="i"/>, in <c>(j, k)</c> order: the points
    /// inside every halfspace, each merged into <paramref name="points"/> as it is found.
    /// </summary>
    /// <param name="s">The soup as columns, with this row's scratch.</param>
    /// <param name="i">The first plane.</param>
    /// <param name="points">The kept points so far.</param>
    /// <param name="merge2">Merge distance squared.</param>
    private static void FindRow(RowScratch s, int i, List<IvpPoint<T>> points, T merge2)
    {
        int n = s.Count;
        T[] xs = s.X, ys = s.Y, zs = s.Z, ws = s.W;
        T[] r01 = s.R01, r11 = s.R11, r21 = s.R21;
        T ax = xs[i], ay = ys[i], az = zs[i];
        T ha = -ws[i];

        // The cofactors of the (i, k) pair, as Intersect writes them with a = i and c = k.
        for (int k = i + 1; k < n; k++)
        {
            T cx = xs[k], cy = ys[k], cz = zs[k];
            r01[k] = (az * cy) - (ay * cz);
            r11[k] = (cz * ax) - (az * cx);
            r21[k] = (cx * ay) - (cy * ax);
        }

        int lanes = Vector<T>.Count;
        bool wide = Vector.IsHardwareAccelerated && lanes > 1;
        for (int j = i + 1; j + 1 < n; j++)
        {
            var row = new Row(ax, ay, az, ha, xs[j], ys[j], zs[j], -ws[j]);
            int k = j + 1;
            if (wide)
            {
                for (; k + lanes <= n; k += lanes)
                {
                    FindBlock(s, row, k, points, merge2);
                }
            }

            for (; k < n; k++)
            {
                FindOne(s, row, k, points, merge2);
            }
        }
    }

    /// <summary>
    /// <see cref="Vector{T}.Count"/> consecutive third planes at once: the same arithmetic as
    /// <see cref="FindOne"/>, lane for lane, with the lanes the recent rejecters leave standing
    /// finished one at a time, in <c>k</c> order.
    /// </summary>
    /// <remarks>
    /// A vector add, subtract, multiply or divide rounds each lane exactly as the scalar
    /// operation does (IEEE 754 on every instruction set .NET targets, and the JIT never fuses a
    /// multiply and an add it was not asked to), so the lanes hold <see cref="FindOne"/>'s values
    /// to the bit. Which lanes survive the recent rejecters, and so which reach the full scan,
    /// depends on the rejecters tried, but the full scan decides every survivor on its own.
    /// </remarks>
    private static void FindBlock(RowScratch s, in Row r, int k, List<IvpPoint<T>> points, T merge2)
    {
        var cx = new Vector<T>(s.X, k);
        var cy = new Vector<T>(s.Y, k);
        var cz = new Vector<T>(s.Z, k);
        var bx = new Vector<T>(r.Bx);
        var by = new Vector<T>(r.By);
        var bz = new Vector<T>(r.Bz);

        Vector<T> c0 = (by * cz) - (cy * bz);
        Vector<T> c1 = (bz * cx) - (cz * bx);
        Vector<T> c2 = (cy * bx) - (by * cx);
        Vector<T> det = ((new Vector<T>(r.Ax) * c0) + (new Vector<T>(r.Ay) * c1)) + (new Vector<T>(r.Az) * c2);

        // Intersect's test is |det| < eps; a NaN determinant passes it, and passes here too.
        Vector<T> live = ~Vector.LessThan(Vector.Abs(det), new Vector<T>(Epsilon));
        if (live == Vector<T>.Zero)
        {
            return;
        }

        Vector<T> inv = Vector<T>.One / det;
        Vector<T> i00 = c0 * inv;
        Vector<T> i01 = new Vector<T>(s.R01, k) * inv;
        Vector<T> i02 = new Vector<T>(r.N02) * inv;
        Vector<T> i10 = c1 * inv;
        Vector<T> i11 = new Vector<T>(s.R11, k) * inv;
        Vector<T> i12 = new Vector<T>(r.N12) * inv;
        Vector<T> i20 = c2 * inv;
        Vector<T> i21 = new Vector<T>(s.R21, k) * inv;
        Vector<T> i22 = new Vector<T>(r.N22) * inv;
        var ha = new Vector<T>(r.Ha);
        var hb = new Vector<T>(r.Hb);
        Vector<T> hc = -new Vector<T>(s.W, k);
        Vector<T> px = ((hc * i02) + (i01 * hb)) + (ha * i00);
        Vector<T> py = (i10 * ha) + ((i11 * hb) + (i12 * hc));
        Vector<T> pz = (i20 * ha) + ((i21 * hb) + (i22 * hc));

        var outside = new Vector<T>(Outside);
        foreach (int h in s.Recent)
        {
            Vector<T> v = ((new Vector<T>(s.Z[h]) * pz) + new Vector<T>(s.W[h])) +
                ((new Vector<T>(s.Y[h]) * py) + (new Vector<T>(s.X[h]) * px));
            live = Vector.AndNot(live, Vector.GreaterThan(outside, v));
            if (live == Vector<T>.Zero)
            {
                return;
            }
        }

        for (int lane = 0; lane < Vector<T>.Count; lane++)
        {
            if (live[lane] != T.Zero)
            {
                Finish(s, px[lane], py[lane], pz[lane], points, merge2);
            }
        }
    }

    /// <summary>One triple <c>(i, j, k)</c>: <see cref="Intersect"/>'s arithmetic, expression for
    /// expression, with the pair cofactors taken from where they were computed once.</summary>
    private static void FindOne(RowScratch s, in Row r, int k, List<IvpPoint<T>> points, T merge2)
    {
        T cx = s.X[k], cy = s.Y[k], cz = s.Z[k];
        T c0 = (r.By * cz) - (cy * r.Bz);
        T c1 = (r.Bz * cx) - (cz * r.Bx);
        T c2 = (cy * r.Bx) - (r.By * cx);
        T det = ((r.Ax * c0) + (r.Ay * c1)) + (r.Az * c2);
        if (T.Abs(det) < Epsilon)
        {
            return;
        }

        T inv = T.One / det;
        T i00 = c0 * inv;
        T i01 = s.R01[k] * inv;
        T i02 = r.N02 * inv;
        T i10 = c1 * inv;
        T i11 = s.R11[k] * inv;
        T i12 = r.N12 * inv;
        T i20 = c2 * inv;
        T i21 = s.R21[k] * inv;
        T i22 = r.N22 * inv;
        T ha = r.Ha, hb = r.Hb, hc = -s.W[k];
        T px = ((hc * i02) + (i01 * hb)) + (ha * i00);
        T py = (i10 * ha) + ((i11 * hb) + (i12 * hc));
        T pz = (i20 * ha) + ((i21 * hb) + (i22 * hc));

        foreach (int h in s.Recent)
        {
            if (Outside > s.Value(h, px, py, pz))
            {
                return;
            }
        }

        Finish(s, px, py, pz, points, merge2);
    }

    /// <summary>
    /// The inside test for a point the recent rejecters let through: every halfspace in order,
    /// the first to reject it becoming the most recent rejecter; a point none rejects is merged.
    /// </summary>
    private static void Finish(RowScratch s, T px, T py, T pz, List<IvpPoint<T>> points, T merge2)
    {
        for (int h = 0; h < s.Count; h++)
        {
            if (Outside > s.Value(h, px, py, pz))
            {
                int[] recent = s.Recent;
                Array.Copy(recent, 0, recent, 1, recent.Length - 1);
                recent[0] = h;
                return;
            }
        }

        InsertMerged(new IvpPoint<T>(Canonical(px), Canonical(py), Canonical(pz), T.Zero), points, merge2);
    }

    /// <summary>
    /// A NaN coordinate as the one NaN every CPU agrees on, <c>T.NaN</c>; any other value as is.
    /// </summary>
    /// <remarks>
    /// A soup with a NaN or infinite plane (a zero-area triangle's normal, say) yields corners
    /// with NaN coordinates, and they reach the output: a NaN passes every inside test and is
    /// never merged. IEEE 754 fixes no NaN's sign or payload. x86 generates one NaN,
    /// sign set (<c>FFF8…</c> in double, the reference build's value too), so its corners were
    /// always that; arm64 generates a positive one (<c>7FF8…</c>) but propagates an input NaN's
    /// own sign, so its corner bits depended on which operation first made the NaN, and moved
    /// with the order of the arithmetic. Every NaN is written as <c>T.NaN</c> instead, which
    /// is x86's value: nothing changes there, and arm64 now writes the same bytes.
    /// </remarks>
    private static T Canonical(T value) => T.IsNaN(value) ? T.NaN : value;

    /// <summary>
    /// Append a point unless one is already within the merge distance. Whether any is within the
    /// distance does not depend on the order the kept points are compared in, so they are compared
    /// newest first, where a corner's other triples usually landed.
    /// </summary>
    /// <param name="p">The point.</param>
    /// <param name="points">The list.</param>
    /// <param name="merge2">Merge distance squared.</param>
    private static void InsertMerged(IvpPoint<T> p, List<IvpPoint<T>> points, T merge2)
    {
        for (int i = points.Count - 1; i >= 0; i--)
        {
            IvpPoint<T> q = points[i];
            T dx = p.X - q.X;
            T dy = p.Y - q.Y;
            T dz = p.Z - q.Z;
            if (((dx * dx) + (dy * dy)) + (dz * dz) < merge2)
            {
                return;
            }
        }

        points.Add(p);
    }

    /// <summary>The determinant magnitude below which three planes are taken not to meet.</summary>
    private static T Epsilon => TP.IsDouble ? T.CreateTruncating(1e-19) : T.CreateTruncating(1.0e-10f);

    /// <summary>How far outside a halfspace a point may be and still count as inside.</summary>
    private static T Outside => T.CreateTruncating(-1.0e-4f);

    /// <summary>How many recent rejecting halfspaces are tried before the full scan.</summary>
    private const int RecentRejecters = 8;

    /// <summary>
    /// The first two planes of the triples being solved, and the cofactors that depend on them
    /// alone, as <see cref="Intersect"/> writes them with <c>a = i</c> and <c>b = j</c>.
    /// </summary>
    private readonly struct Row
    {
        public readonly T Ax, Ay, Az, Ha, Bx, By, Bz, Hb, N02, N12, N22;

        public Row(T ax, T ay, T az, T ha, T bx, T by, T bz, T hb)
        {
            Ax = ax;
            Ay = ay;
            Az = az;
            Ha = ha;
            Bx = bx;
            By = by;
            Bz = bz;
            Hb = hb;
            N02 = (ay * bz) - (az * by);
            N12 = (az * bx) - (bz * ax);
            N22 = (ax * by) - (ay * bx);
        }
    }

    /// <summary>
    /// One call's working set: the soup as columns (the soup's points are classes, and the loop
    /// reads each plane millions of times), the <c>(i, k)</c> cofactors of the current first plane,
    /// and the recent rejecting halfspaces, most recent first. The rejecters start as plane 0;
    /// they only choose which halfspace is tried first, never the answer.
    /// </summary>
    private sealed class RowScratch
    {
        public RowScratch(List<IvpPoint<T>> soup)
        {
            Count = soup.Count;
            X = new T[Count];
            Y = new T[Count];
            Z = new T[Count];
            W = new T[Count];
            for (int i = 0; i < Count; i++)
            {
                IvpPoint<T> h = soup[i];
                X[i] = h.X;
                Y[i] = h.Y;
                Z[i] = h.Z;
                W[i] = h.W;
            }

            R01 = new T[Count];
            R11 = new T[Count];
            R21 = new T[Count];
        }

        public int Count { get; }

        public T[] X { get; }

        public T[] Y { get; }

        public T[] Z { get; }

        public T[] W { get; }

        public T[] R01 { get; }

        public T[] R11 { get; }

        public T[] R21 { get; }

        public int[] Recent { get; } = new int[RecentRejecters];

        /// <summary>A halfspace's value at a point, grouped as the reference inside test groups it.</summary>
        public T Value(int h, T px, T py, T pz) => ((Z[h] * pz) + W[h]) + ((Y[h] * py) + (X[h] * px));
    }

    /// <summary>
    /// The point where three planes meet, by inverting the matrix of their normals
    /// (<c>IVP_U_Matrix3::real_invert</c>).
    /// </summary>
    /// <param name="a">First plane.</param>
    /// <param name="b">Second plane.</param>
    /// <param name="c">Third plane.</param>
    /// <param name="x">Result X.</param>
    /// <param name="y">Result Y.</param>
    /// <param name="z">Result Z.</param>
    /// <returns>False when the determinant's magnitude is below the build's epsilon.</returns>
    public static bool Intersect(IvpPoint<T> a, IvpPoint<T> b, IvpPoint<T> c, out T x, out T y, out T z)
    {
        // Rows are the three normals.
        T m00 = a.X, m01 = a.Y, m02 = a.Z;
        T m10 = b.X, m11 = b.Y, m12 = b.Z;
        T m20 = c.X, m21 = c.Y, m22 = c.Z;

        T c0 = (m11 * m22) - (m21 * m12);
        T c1 = (m12 * m20) - (m22 * m10);
        T c2 = (m21 * m10) - (m11 * m20);
        T det = ((m00 * c0) + (m01 * c1)) + (m02 * c2);

        // comiss eps, |det| ; jbe ok: fails only when |det| < eps (NaN passes, as stock does).
        T eps = TP.IsDouble ? T.CreateTruncating(1e-19) : T.CreateTruncating(1.0e-10f);
        if (T.Abs(det) < eps)
        {
            x = y = z = T.Zero;
            return false;
        }

        T inv = T.One / det;
        T i00 = c0 * inv;
        T i01 = ((m02 * m21) - (m01 * m22)) * inv;
        T i02 = ((m01 * m12) - (m02 * m11)) * inv;
        T i10 = c1 * inv;
        T i11 = ((m22 * m00) - (m02 * m20)) * inv;
        T i12 = ((m02 * m10) - (m12 * m00)) * inv;
        T i20 = c2 * inv;
        T i21 = ((m20 * m01) - (m21 * m00)) * inv;
        T i22 = ((m00 * m11) - (m01 * m10)) * inv;

        T ha = -a.W, hb = -b.W, hc = -c.W;
        x = ((hc * i02) + (i01 * hb)) + (ha * i00);
        y = (i10 * ha) + ((i11 * hb) + (i12 * hc));
        z = (i20 * ha) + ((i21 * hb) + (i22 * hc));
        return true;
    }
}
