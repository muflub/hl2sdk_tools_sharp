using System.Numerics;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// <c>IVP_SurfaceBuilder_Polygon_Convex</c> over <c>IVP_Object_Polygon_Tetra</c>: triangulates each
/// template surface in 2-D, stitches the triangles into a closed mesh, picks each triangle's
/// "pierce" partner, and writes the compact ledge.
/// </summary>
/// <remarks>
/// Decompiled (SDK 2013 / TF2): 00184a80/001849f0/00184890 driver (00189540/001894b0/00189350),
/// tetra ctor 001a02d0 (001a4d20), make_triangles 001a16d0 (001a6130), the 2-D line representation
/// 0019f290 (001a3d20), the baseline triangulation 0019f730 (001a41b0) with its segment tests
/// 0019f040/0019eda0/0019ef50/0019ee70 (001a3ac0/001a3840/001a39d0/001a38f0) and the
/// <c>IVP_U_Min_Hash</c> 002054b0..00205890, the triangle hesse 0019e8d0/002003f0/00200550, the
/// pierce pairing 001a37b0 (001a8230), and the compact-ledge generator 0019d1f0/0019d5b0
/// (001a1c70/001a2030).
/// </remarks>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
/// <typeparam name="TP">The precision policy.</typeparam>
internal static class IvpPolygonTetra<T, TP>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
    where TP : struct, IIvpPrecision<T>
{
    /// <summary>A mesh point (<c>IVP_Poly_Point</c>).</summary>
    private sealed class PolyPoint(T x, T y, T z, int index)
    {
        public readonly T X = x, Y = y, Z = z;
        public readonly int Index = index;
        public int CompactIndex = -1;
    }

    /// <summary>A mesh edge (<c>IVP_Tri_Edge</c>).</summary>
    private sealed class Edge
    {
        public PolyPoint Start = null!;
        public Triangle Tri = null!;
        public Edge Next = null!;
        public Edge Prev = null!;
        public Edge? Opposite;
        public int GlobalIndex;
    }

    /// <summary>A mesh triangle (<c>IVP_Triangle</c>).</summary>
    private sealed class Triangle
    {
        public readonly Edge E0 = new(), E1 = new(), E2 = new();
        public Triangle? Pierce;
        public int Index;
        public T HX, HY, HZ, HW;
    }

    /// <summary>A projected 2-D point.</summary>
    private sealed class Point2(int index)
    {
        public T X, Y;
        public readonly int Index = index;
    }

    /// <summary>A directed 2-D line.</summary>
    private sealed class Line2
    {
        public Point2 Start;
        public Point2 End;
        public T Dx, Dy;

        public Line2(Point2 start, Point2 end)
        {
            Start = start;
            End = end;
            Dx = end.X - start.X;
            Dy = end.Y - start.Y;
        }
    }

    /// <summary>Builds the ledge, or null when any stage fails.</summary>
    /// <param name="template">The template polygon.</param>
    /// <param name="context">Scratch.</param>
    /// <returns>The ledge.</returns>
    public static IvpCompactLedge? ToCompactLedge(IvpTemplatePolygon<T> template, IvpCookContext context)
    {
        _ = context;
        var points = new PolyPoint[template.Points.Count];
        for (int i = 0; i < points.Length; i++)
        {
            IvpPoint<T> p = template.Points[i];
            points[i] = new PolyPoint(p.X, p.Y, p.Z, i);
        }

        // Creation order; IVP's triangle list is head-inserted, so its order is the reverse.
        var created = new List<Triangle>();
        var edgeHash = new Dictionary<(int, int), Edge>();
        var tris2 = new List<(int I0, int I1, int I2)>();
        foreach (IvpTemplatePolygon<T>.Surface surface in template.Surfaces)
        {
            tris2.Clear();
            if (!Triangulate(template, surface, tris2, context.Trace))
            {
                return null;
            }

            // The 2-D triangle list is head-inserted too: newest first.
            for (int t = tris2.Count - 1; t >= 0; t--)
            {
                (int i0, int i1, int i2) = tris2[t];
                PolyPoint p17 = points[i1], p14 = points[i2], p18 = points[i0];
                T ax = p17.X - p14.X, ay = p17.Y - p14.Y, az = p17.Z - p14.Z;
                T bx = p18.X - p14.X, by = p18.Y - p14.Y, bz = p18.Z - p14.Z;
                IvpVector.Cross(ax, ay, az, bx, by, bz, out T cx, out T cy, out T cz);
                T dot = ((cx * surface.NX) + (cy * surface.NY)) + (cz * surface.NZ);
                PolyPoint p19 = p18;
                if (T.Zero > dot)
                {
                    p19 = p17;
                    p17 = p18;
                }

                var tri = new Triangle();
                Wire(tri.E0, tri, p14, tri.E1, tri.E2);
                Wire(tri.E1, tri, p17, tri.E2, tri.E0);
                Wire(tri.E2, tri, p19, tri.E0, tri.E1);
                Link(edgeHash, tri.E0, p14, p17);
                Link(edgeHash, tri.E1, p17, p19);
                Link(edgeHash, tri.E2, p19, p14);
                created.Add(tri);
            }
        }

        int n = created.Count;
        var list = new Triangle[n];
        for (int i = 0; i < n; i++)
        {
            list[i] = created[n - 1 - i];
        }

        // 0019e8d0: hesse of every triangle, normalised (the mirrors IVP also builds never reach the
        // ledge, so they are not modelled).
        foreach (Triangle t in list)
        {
            Hesse(t);
        }

        if (!PairPierce(list))
        {
            return null;
        }

        return Generate(list);
    }

    private static void Wire(Edge e, Triangle tri, PolyPoint start, Edge next, Edge prev)
    {
        e.Start = start;
        e.Tri = tri;
        e.Next = next;
        e.Prev = prev;
    }

    /// <summary>Joins an edge to the one already hashed under the same unordered point pair.</summary>
    private static void Link(Dictionary<(int, int), Edge> hash, Edge e, PolyPoint a, PolyPoint b)
    {
        (int, int) key = a.Index <= b.Index ? (a.Index, b.Index) : (b.Index, a.Index);
        if (hash.TryGetValue(key, out Edge? other))
        {
            e.Opposite = other;
            other.Opposite = e;
        }
        else
        {
            hash.Add(key, e);
        }
    }

    /// <summary>
    /// 0019e8d0 = 002003f0 then the hesse normalise: n = (next - p0) x (prev - p0) in the grouping
    /// the binary uses, w = -((p0.x*n.x + n.y*p0.y) + n.z*p0.z).
    /// </summary>
    private static void Hesse(Triangle t)
    {
        PolyPoint p1 = t.E0.Start, p2 = t.E0.Prev.Start, p3 = t.E0.Next.Start;
        T ay = p2.Y - p1.Y, az = p2.Z - p1.Z, ax = p2.X - p1.X;
        T bx = p3.X - p1.X, by = p3.Y - p1.Y, bz = p3.Z - p1.Z;
        T nx = (az * by) - (ay * bz);
        T ny = (bz * ax) - (az * bx);
        T nz = (ay * bx) - (ax * by);
        T w = -(((p1.X * nx) + (ny * p1.Y)) + (nz * p1.Z));
        TP.NormizeHesse(ref nx, ref ny, ref nz, ref w);
        t.HX = nx;
        t.HY = ny;
        t.HZ = nz;
        t.HW = w;
    }

    /// <summary>
    /// 001a37b0: each triangle without a partner takes the one whose normal is most opposite
    /// (first minimum below -1e-6), and that one takes it back.
    /// </summary>
    private static bool PairPierce(Triangle[] list)
    {
        T start = T.CreateTruncating(-1.0e-6f);
        foreach (Triangle t in list)
        {
            if (t.Pierce is not null)
            {
                continue;
            }

            Triangle? best = null;
            T bestDot = start;
            foreach (Triangle u in list)
            {
                T d = ((t.HX * u.HX) + (t.HY * u.HY)) + (t.HZ * u.HZ);
                if (d < bestDot)
                {
                    best = u;
                }

                bestDot = MinSse(bestDot, d);
            }

            if (best is null)
            {
                return false; // the binary dereferences null here
            }

            t.Pierce = best;
            best.Pierce = t;
        }

        return true;
    }

    /// <summary><c>minss acc, d</c>: <c>acc &lt; d ? acc : d</c>.</summary>
    private static T MinSse(T acc, T d) => acc < d ? acc : d;

    /// <summary>0019d1f0 + 0019d5b0: number the triangles and points and write the ledge.</summary>
    private static IvpCompactLedge Generate(Triangle[] list)
    {
        var order = new List<PolyPoint>();
        for (int i = 0; i < list.Length; i++)
        {
            Triangle t = list[i];
            t.Index = i;
            Edge e = t.E0;
            for (int k = 0; k < 3; k++)
            {
                if (e.Start.CompactIndex < 0)
                {
                    e.Start.CompactIndex = order.Count;
                    order.Add(e.Start);
                }

                e.GlobalIndex = (4 * i) + 1 + k;
                e = e.Next;
            }
        }

        var ledge = IvpCompactLedge.Create(list.Length, order.Count);
        for (int i = 0; i < list.Length; i++)
        {
            Triangle t = list[i];
            uint word = (uint)(i & 0xfff);
            if (t.Pierce is not null)
            {
                word |= (uint)(t.Pierce.Index & 0xfff) << 12;
            }

            ledge.SetTriangleWord(i, word);
            Edge e = t.E0;
            for (int k = 0; k < 3; k++)
            {
                int target = e.Opposite is null ? -1 : e.Opposite.GlobalIndex;
                int rel = (target - e.GlobalIndex) & 0x7fff;
                ledge.SetEdgeWord(i, k, (uint)(e.Start.CompactIndex & 0xffff) | ((uint)rel << 16));
                e = e.Next;
            }
        }

        for (int p = 0; p < order.Count; p++)
        {
            PolyPoint q = order[p];
            ledge.SetPoint(p, float.CreateTruncating(q.X), float.CreateTruncating(q.Y), float.CreateTruncating(q.Z));
        }

        return ledge;
    }

    /// <summary>
    /// 0019f290 + 0019f730: project a surface onto its dominant plane and triangulate it by
    /// advancing a baseline to the nearest admissible point.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <param name="s">The surface.</param>
    /// <param name="triangles">Receives the triangles in creation order (IVP's list is the reverse).</param>
    /// <param name="trace">Optional narration of the decisions.</param>
    /// <returns>False on either stage's failure ("No 2d representation" / "no 3d representation").</returns>
    private static bool Triangulate(IvpTemplatePolygon<T> template, IvpTemplatePolygon<T>.Surface s, List<(int, int, int)> triangles, Action<string>? trace)
    {
        // Dominant axis and projection (0019f290).
        T ax = T.Abs(s.NX), ay = T.Abs(s.NY), az = T.Abs(s.NZ);
        int u, v;
        bool flip;
        if (ax < ay && az <= ay)
        {
            u = 0;
            v = 2;
            flip = T.Zero <= s.NY;
        }
        else if (!(ax < ay) && az <= ax)
        {
            u = 1;
            v = 2;
            flip = s.NX < T.Zero;
        }
        else
        {
            u = 0;
            v = 1;
            flip = s.NZ < T.Zero;
        }

        var pts = new Dictionary<int, Point2>();
        var lines = new LinkedList<Line2>();
        int n = s.LineIndices.Length;
        for (int k = n - 1; k >= 0; k--)
        {
            (ushort l0, ushort l1) = template.Lines[s.LineIndices[k]];
            int revert = s.Revert[k];
            int ia = revert, ib = 1 - revert;
            if (flip)
            {
                (ia, ib) = (ib, ia);
            }

            int a = ia == 0 ? l0 : l1;
            int b = ib == 0 ? l0 : l1;
            Point2 pa = Project(template, pts, a, u, v);
            Point2 pb = Project(template, pts, b, u, v);
            lines.AddFirst(new Line2(pa, pb));
        }

        if (trace is not null)
        {
            var sb = new System.Text.StringBuilder("surface n=(" + s.NX + " " + s.NY + " " + s.NZ + ") u=" + u + " v=" + v + " flip=" + flip + " lines:");
            foreach (Line2 l in lines)
            {
                sb.Append(" " + l.Start.Index + "->" + l.End.Index);
            }

            trace(sb.ToString());
        }

        return Advance(lines, triangles, trace);
    }

    private static Point2 Project(IvpTemplatePolygon<T> template, Dictionary<int, Point2> pts, int index, int u, int v)
    {
        if (!pts.TryGetValue(index, out Point2? p))
        {
            p = new Point2(index);
            pts.Add(index, p);
        }

        IvpPoint<T> q = template.Points[index];
        p.X = Component(q, u);
        p.Y = Component(q, v);
        return p;
    }

    private static T Component(IvpPoint<T> q, int axis) => axis switch
    {
        0 => q.X,
        1 => q.Y,
        _ => q.Z,
    };

    /// <summary>The binary's left-of-line test: (q.y - s.y)*dx + (s.x - q.x)*dy.</summary>
    private static T Side(Line2 l, Point2 q) =>
        ((q.Y - l.Start.Y) * l.Dx) + ((l.Start.X - q.X) * l.Dy);

    /// <summary>The <c>P_DOUBLE_EPS</c> of the build: 1e-10f / 1e-19.</summary>
    private static T Eps => TP.IsDouble ? T.CreateTruncating(1e-19) : T.CreateTruncating(1.0e-10f);

    /// <summary>0019f730's main loop.</summary>
    private static bool Advance(LinkedList<Line2> lines, List<(int, int, int)> triangles, Action<string>? trace)
    {
        T eps = Eps;
        var candidates = new List<(T Value, int Seq, LinkedListNode<Line2> Node)>();
        int budget = 101;
        while (lines.First is not null)
        {
            Line2 baseline = lines.First.Value;
            Point2 s = baseline.Start, e = baseline.End;
            candidates.Clear();
            int seq = 0;
            for (LinkedListNode<Line2>? node = lines.First; node is not null; node = node.Next)
            {
                Point2 c = node.Value.Start;
                T ex = c.X - e.X, ey = c.Y - e.Y, ez = T.Zero;
                T sx = c.X - s.X, sy = c.Y - s.Y, sz = T.Zero;
                T value = (((ex * ex) + (sz * sz)) + ((sx * sx) + (sy * sy))) + ((ey * ey) + (ez * ez));
                candidates.Add((value, seq++, node));
            }

            // IVP_U_Min_Hash hands elements back by (value, insertion order).
            candidates.Sort(static (a, b) =>
            {
                int c = a.Value.CompareTo(b.Value);
                return c != 0 ? c : a.Seq.CompareTo(b.Seq);
            });

            bool accepted = false;
            if (trace is not null)
            {
                var sb = new System.Text.StringBuilder("  baseline " + s.Index + "->" + e.Index + " candidates:");
                foreach ((T val, int sq, LinkedListNode<Line2> nd) in candidates)
                {
                    sb.Append(" " + nd.Value.Start.Index + "=" + double.CreateTruncating(val).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "#" + sq);
                }

                trace(sb.ToString());
            }

            foreach ((T _, int _, LinkedListNode<Line2> node) in candidates)
            {
                Line2 cand = node.Value;
                Point2 pt = cand.Start;
                if (ReferenceEquals(cand, baseline) || ReferenceEquals(pt, e) || eps >= Side(baseline, pt))
                {
                    continue;
                }

                var new6 = new Line2(pt, s);
                var new7 = new Line2(e, pt);
                if (CrossesAny(lines, pt, s, e, new6, new7))
                {
                    continue;
                }

                bool inside = false;
                for (LinkedListNode<Line2>? l = lines.First; l is not null; l = l.Next)
                {
                    Point2 q = l.Value.Start;
                    if (eps < Side(baseline, q) && eps < Side(new7, q) && eps < Side(new6, q))
                    {
                        inside = true;
                        break;
                    }
                }

                if (inside)
                {
                    if (node.Next is not null)
                    {
                        continue;
                    }

                    // "Couldn't find a matching point to baseline!" then Error(): fatal in stock.
                    return false;
                }

                triangles.Add((s.Index, pt.Index, e.Index));
                lines.RemoveFirst();
                bool haveSp = false, havePe = false;
                for (LinkedListNode<Line2>? l = lines.First; l is not null;)
                {
                    LinkedListNode<Line2>? next = l.Next;
                    Line2 line = l.Value;
                    if ((ReferenceEquals(pt, line.Start) && ReferenceEquals(s, line.End))
                        || (ReferenceEquals(s, line.Start) && ReferenceEquals(pt, line.End)))
                    {
                        lines.Remove(l);
                        haveSp = true;
                    }
                    else if ((ReferenceEquals(pt, line.Start) && ReferenceEquals(e, line.End))
                        || (ReferenceEquals(e, line.Start) && ReferenceEquals(pt, line.End)))
                    {
                        lines.Remove(l);
                        havePe = true;
                    }

                    l = next;
                }

                if (!haveSp)
                {
                    lines.AddFirst(new Line2(s, pt));
                }

                if (!havePe)
                {
                    lines.AddFirst(new Line2(pt, e));
                }

                accepted = true;
                break;
            }

            if (!accepted)
            {
                return false; // the binary retries the unchanged baseline until its budget runs out
            }

            if (lines.First is null)
            {
                return true;
            }

            if (--budget == 0)
            {
                return false; // "Cannot convert"
            }
        }

        return true;
    }

    /// <summary>
    /// The intersection screen in 0019f730: does any boundary line cross the proposed new edges?
    /// Lines already joining the candidate to a baseline end are exempt from that edge's test.
    /// </summary>
    private static bool CrossesAny(LinkedList<Line2> lines, Point2 pt, Point2 s, Point2 e, Line2 new6, Line2 new7)
    {
        for (LinkedListNode<Line2>? node = lines.First; node is not null; node = node.Next)
        {
            Line2 l = node.Value;
            Point2 a = l.Start, b = l.End;
            bool joinsPtS = (ReferenceEquals(pt, a) && ReferenceEquals(b, s)) || (ReferenceEquals(pt, b) && ReferenceEquals(a, s));
            if (!joinsPtS && Intersects(l, new6))
            {
                return true;
            }

            bool joinsEPt = ReferenceEquals(a, e) && ReferenceEquals(b, pt);
            if (!joinsEPt && Intersects(l, new7))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>0019f040: do two segments cross (sharing an end point does not count)?</summary>
    private static bool Intersects(Line2 l1, Line2 l2)
    {
        T cross = (l2.Dx * l1.Dy) - (l1.Dx * l2.Dy);
        if (T.Abs(cross) < T.CreateTruncating(1.0e-9f))
        {
            T d = PointLineDistance(l1, l2.Start);
            return T.Abs(d) < T.CreateTruncating(BitConverter.Int32BitsToSingle(0x322bcc76)) && Overlap(l1, l2);
        }

        Point2 a = l1.Start, c = l2.Start;
        if (ReferenceEquals(a, l2.End) || ReferenceEquals(a, c) || ReferenceEquals(c, l1.End) || ReferenceEquals(l2.End, l1.End))
        {
            return false;
        }

        T hi = cross, lo = T.Zero;
        if (cross <= T.Zero)
        {
            hi = T.Zero;
            lo = cross;
        }

        T tx = c.X - a.X, ty = c.Y - a.Y;
        T u = (l2.Dx * ty) - (l2.Dy * tx);
        if (lo > u || u > hi)
        {
            return false;
        }

        T w = (l1.Dx * ty) - (l1.Dy * tx);
        return !(lo > w) && !(w > hi);
    }

    /// <summary>0019eda0: signed distance of a point from a line (SDK: rsqrt estimate; TF2: divide).</summary>
    private static T PointLineDistance(Line2 l, Point2 p)
    {
        T len2 = (l.Dy * l.Dy) + (l.Dx * l.Dx);
        T sx = l.Start.X - p.X;
        if (Eps > len2)
        {
            T sy = l.Start.Y - p.Y;
            return (sx * sx) + (sy * sy);
        }

        T num = ((p.Y - l.Start.Y) * l.Dx) + (sx * l.Dy);
        if (TP.IsDouble)
        {
            return num / T.Sqrt(len2);
        }

        float f = StockPrecision.RsqrtNewton(float.CreateTruncating(len2));
        return T.CreateTruncating(f) * num;
    }

    /// <summary>0019ef50: do two collinear segments overlap (beyond 1e-8)?</summary>
    private static bool Overlap(Line2 l1, Line2 l2)
    {
        T eps = T.CreateTruncating(1.0e-8f);
        T lo, hi, c, d;
        if (T.Abs(l1.Dx) <= eps)
        {
            if (T.Abs(l1.Dy) <= eps)
            {
                return PointInside(l2, l1.Start);
            }

            lo = l1.End.Y;
            hi = l1.Start.Y;
            if (T.Zero < l1.Dy)
            {
                lo = l1.Start.Y;
                hi = l1.End.Y;
            }

            c = l2.Start.Y;
            d = l2.End.Y;
        }
        else
        {
            lo = l1.End.X;
            hi = l1.Start.X;
            if (T.Zero < l1.Dx)
            {
                lo = l1.Start.X;
                hi = l1.End.X;
            }

            c = l2.Start.X;
            d = l2.End.X;
        }

        lo += eps;
        if (!(lo < c) && d <= lo)
        {
            return false;
        }

        return c < hi - eps || d < hi - eps;
    }

    /// <summary>0019ee70: is a point strictly inside a (degenerate-direction) segment?</summary>
    private static bool PointInside(Line2 l, Point2 p)
    {
        T eps = Eps;
        T margin = T.CreateTruncating(1.0e-4f);
        T adx = T.Abs(l.Dx);
        if (adx <= eps)
        {
            T ady = T.Abs(l.Dy);
            if (eps < ady)
            {
                T sign = l.Dy <= T.Zero ? -T.One : T.One;
                T t = (p.Y - l.Start.Y) * sign;
                if (margin < t)
                {
                    return t < ady - margin;
                }
            }

            return false;
        }

        T sgn = l.Dx <= T.Zero ? -T.One : T.One;
        T tt = (p.X - l.Start.X) * sgn;
        return margin < tt && tt < adx - margin;
    }
}
