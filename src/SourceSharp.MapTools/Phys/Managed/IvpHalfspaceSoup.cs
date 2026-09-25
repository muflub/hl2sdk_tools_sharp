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
    public static List<IvpPoint<T>> CornerPoints(List<IvpPoint<T>> soup, T merge)
    {
        T merge2 = merge * merge;
        T outside = T.CreateTruncating(-1.0e-4f);
        var points = new List<IvpPoint<T>>();
        int n = soup.Count;
        for (int i = 0; i + 1 < n; i++)
        {
            for (int j = i + 1; j + 1 < n; j++)
            {
                for (int k = j + 1; k < n; k++)
                {
                    if (!Intersect(soup[i], soup[j], soup[k], out T px, out T py, out T pz))
                    {
                        continue;
                    }

                    bool inside = true;
                    foreach (IvpPoint<T> h in soup)
                    {
                        T v = ((h.Z * pz) + h.W) + ((h.Y * py) + (h.X * px));
                        if (outside > v)
                        {
                            inside = false;
                            break;
                        }
                    }

                    if (inside)
                    {
                        InsertMerged(new IvpPoint<T>(px, py, pz, T.Zero), points, merge2);
                    }
                }
            }
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

    /// <summary>: append a point unless one is already within the merge distance.</summary>
    /// <param name="p">The point.</param>
    /// <param name="points">The list.</param>
    /// <param name="merge2">Merge distance squared.</param>
    private static void InsertMerged(IvpPoint<T> p, List<IvpPoint<T>> points, T merge2)
    {
        foreach (IvpPoint<T> q in points)
        {
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
