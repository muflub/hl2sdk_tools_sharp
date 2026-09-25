using System.Numerics;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// <c>IVP_Template_Polygon</c>: points, undirected lines, and surfaces made of lines with a
/// Per-line direction flag. Built from qhull's facets by the reference implementation.
/// </summary>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
internal sealed class IvpTemplatePolygon<T>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
{
    /// <summary>The template's points (copies).</summary>
    public readonly List<IvpPoint<T>> Points = [];

    /// <summary>Lines as point-index pairs, in first-appearance orientation.</summary>
    public readonly List<(ushort P0, ushort P1)> Lines = [];

    /// <summary>Surfaces.</summary>
    public readonly List<Surface> Surfaces = [];

    /// <summary>A surface: normal, line indices and direction flags.</summary>
    public sealed class Surface
    {
        /// <summary>Normal X.</summary>
        public T NX;

        /// <summary>Normal Y.</summary>
        public T NY;

        /// <summary>Normal Z.</summary>
        public T NZ;

        /// <summary>Line index per polygon edge.</summary>
        public ushort[] LineIndices = [];

        /// <summary>1 where the line runs the same way as the polygon edge, else 0.</summary>
        public byte[] Revert = [];
    }

    /// <summary>
    /// Points are copied from the unique list with an equality search whose "found at
    /// index 0" result is taken as "not found" (so a point equal, by <c>==</c>, to point 0 is
    /// appended again; only +0/-0 twins of point 0 can trigger it, and the copy is never
    /// referenced, so the ledge is unaffected). Lines are the undirected polygon edges in first
    /// appearance order.
    /// </summary>
    /// <param name="unique">The point list qhull ran on.</param>
    /// <param name="polygons">The facet polygons.</param>
    /// <returns>The template.</returns>
    public static IvpTemplatePolygon<T> Build(List<IvpPoint<T>> unique, List<IvpFacetPolygon<T>> polygons)
    {
        var t = new IvpTemplatePolygon<T>();
        foreach (IvpPoint<T> p in unique)
        {
            int found = -1;
            for (int i = 0; i < t.Points.Count; i++)
            {
                IvpPoint<T> q = t.Points[i];
                if (p.X == q.X && p.Y == q.Y && p.Z == q.Z)
                {
                    found = i;
                    break;
                }
            }

            if (found > 0)
            {
                continue;
            }

            t.Points.Add(new IvpPoint<T>(p.X, p.Y, p.Z, T.Zero));
        }

        var lineKeys = new HashSet<uint>();
        foreach (IvpFacetPolygon<T> poly in polygons)
        {
            int n = poly.Points.Count;
            for (int k = 0; k < n; k++)
            {
                ushort a = (ushort)t.IndexOf(poly.Points[k]);
                ushort b = (ushort)t.IndexOf(poly.Points[k + 1 == n ? 0 : k + 1]);
                ushort lo = a, hi = b;
                if (hi < lo)
                {
                    (lo, hi) = (hi, lo);
                }

                if (lineKeys.Add(hi | ((uint)lo << 16)))
                {
                    t.Lines.Add((a, b));
                }
            }
        }

        foreach (IvpFacetPolygon<T> poly in polygons)
        {
            int n = poly.Points.Count;
            var s = new Surface
            {
                NX = poly.NX,
                NY = poly.NY,
                NZ = poly.NZ,
                LineIndices = new ushort[n],
                Revert = new byte[n],
            };
            ushort[] idx = new ushort[n + 1];
            for (int k = 0; k < n; k++)
            {
                idx[k] = (ushort)t.IndexOf(poly.Points[k]);
            }

            idx[n] = idx[0];
            for (int k = 0; k < n; k++)
            {
                ushort line = 0;
                byte revert = 1;
                for (int l = 0; l < t.Lines.Count; l++)
                {
                    (ushort p0, ushort p1) = t.Lines[l];
                    if (idx[k] == p0 && idx[k + 1] == p1)
                    {
                        line = (ushort)l;
                        revert = 1;
                        break;
                    }

                    if (idx[k + 1] == p0 && idx[k] == p1)
                    {
                        line = (ushort)l;
                        revert = 0;
                        break;
                    }
                }

                s.LineIndices[k] = line;
                s.Revert[k] = revert;
            }

            t.Surfaces.Add(s);
        }

        return t;
    }

    /// <summary>The reference point lookup: first <c>==</c> match, or 0 when there is none.</summary>
    /// <param name="p">The point.</param>
    /// <returns>Its index.</returns>
    private int IndexOf(IvpPoint<T> p)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            IvpPoint<T> q = Points[i];
            if (p.X == q.X && p.Y == q.Y && p.Z == q.Z)
            {
                return i;
            }
        }

        return 0;
    }
}
