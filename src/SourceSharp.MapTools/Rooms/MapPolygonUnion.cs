//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>A point in whole units on the map's plane.</summary>
/// <param name="X">East.</param>
/// <param name="Y">North.</param>
internal readonly record struct MapPoint(int X, int Y) : IComparable<MapPoint>
{
    /// <inheritdoc/>
    public int CompareTo(MapPoint other) => X != other.X ? X.CompareTo(other.X) : Y.CompareTo(other.Y);
}

/// <summary>A walkable face projected onto the plane and snapped: the union's input.</summary>
/// <param name="Points">Its points in whole units, counter-clockwise seen from above (as it was before the snap).</param>
/// <param name="ZLow">Its lowest z, rounded to a whole unit.</param>
/// <param name="ZHigh">Its highest z, rounded to a whole unit.</param>
internal sealed record MapFacePolygon(IReadOnlyList<MapPoint> Points, int ZLow, int ZHigh);

/// <summary>One polygon of the map: an outer ring and its holes, with the band of floor it draws.</summary>
/// <param name="ZLow">The band's bottom.</param>
/// <param name="ZHigh">The band's top.</param>
/// <param name="Outer">The outer ring, counter-clockwise, starting at its least point (x, then y).</param>
/// <param name="Holes">The holes, each clockwise and starting at its least point, in order of that point.</param>
internal sealed record MapPolygon(int ZLow, int ZHigh, IReadOnlyList<MapPoint> Outer, IReadOnlyList<IReadOnlyList<MapPoint>> Holes);

/// <summary>
/// The union of a room's walkable faces into simple polygons with holes, one
/// set per height band, exactly and whatever order the faces come in (the
/// rooms design, 18.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Bands.</b> Faces are grouped first: two faces are in one group when
/// their z bands overlap (closed intervals, in whole units) and they touch on
/// the plane (their areas share a point). Groups are the connected components
/// of that relation, so they do not depend on the order faces are met, and a
/// group's band is the lowest to the highest z of its faces. Each group is
/// unioned on its own: a gallery over a floor is two groups whose polygons
/// overlap on the plane, each with its band, which is what lets a game show
/// the floor the player is on.
/// </para>
/// <para>
/// <b>Exact.</b> The input is on whole units (the faces are snapped before),
/// and every step after is integer or rational arithmetic in
/// <see cref="Int128"/>, with no rounding until the last: faces are split
/// into triangles (a fan, each triangle signed, so a face the snap made a
/// little concave still counts once inside), every triangle edge is split at
/// every point where another edge meets it (touching, T-junctions, overlaps
/// and crossings, the last at rational points), and each piece of edge is a
/// boundary of the union when the triangles covering its left side and its
/// right side differ in whether they cover at all (the sum of their signs
/// above zero). The pieces are then chained into rings, keeping the covered
/// side on the left and taking the leftmost turn at a vertex where several
/// rings meet, so two regions touching at a point stay two simple rings.
/// Only then are points rounded to whole units (a crossing's rational point
/// is the only one that is not already whole), and points on a straight
/// line dropped, which is exact on integers. Every choice is a function of
/// the set of faces, so the result is too: the same bytes whatever order the
/// faces came in and whichever thread computed them.
/// </para>
/// <para>
/// <b>Bounds.</b> Coordinates must lie within ±<see cref="MaxCoordinate"/>;
/// that keeps every product below 2^106 and inside <see cref="Int128"/>.
/// The engine's coordinates stop at ±16384, and a room's own frame at its
/// cell, so a real map never comes near.
/// </para>
/// </remarks>
internal static class MapPolygonUnion
{
    /// <summary>The largest coordinate magnitude the arithmetic is sized for.</summary>
    public const int MaxCoordinate = 1 << 16;

    /// <summary>The side of a bucket of the triangle index, in units.</summary>
    private const int BucketSize = 64;

    /// <summary>Unions faces into polygons, grouped by band.</summary>
    /// <param name="faces">The snapped faces, in any order.</param>
    /// <returns>The polygons, sorted by band and then by their outer rings.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A point lies outside ±<see cref="MaxCoordinate"/>.</exception>
    public static IReadOnlyList<MapPolygon> Union(IReadOnlyList<MapFacePolygon> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        List<Face> cleaned = [];
        foreach (MapFacePolygon face in faces)
        {
            if (Clean(face) is { } good)
            {
                cleaned.Add(good);
            }
        }

        List<MapPolygon> result = [];
        foreach (List<Face> group in Groups(cleaned))
        {
            int zLow = group.Min(f => f.ZLow);
            int zHigh = group.Max(f => f.ZHigh);
            foreach ((List<MapPoint> outer, List<List<MapPoint>> holes) in UnionOf(group))
            {
                result.Add(new MapPolygon(zLow, zHigh, outer, holes));
            }
        }

        result.Sort(ComparePolygons);
        return result;
    }

    /// <summary>A face as the union uses it: its signed triangles and its box.</summary>
    private sealed record Face(Triangle[] Triangles, int ZLow, int ZHigh, int MinX, int MinY, int MaxX, int MaxY);

    /// <summary>A triangle, counter-clockwise, and the sign it counts with (-1 when the snap turned it over).</summary>
    private readonly record struct Triangle(MapPoint A, MapPoint B, MapPoint C, int Sign)
    {
        public int MinX => Math.Min(A.X, Math.Min(B.X, C.X));

        public int MaxX => Math.Max(A.X, Math.Max(B.X, C.X));

        public int MinY => Math.Min(A.Y, Math.Min(B.Y, C.Y));

        public int MaxY => Math.Max(A.Y, Math.Max(B.Y, C.Y));
    }

    /// <summary>A face without repeated points, fanned into triangles; null when it covers nothing.</summary>
    private static Face? Clean(MapFacePolygon face)
    {
        List<MapPoint> points = [];
        foreach (MapPoint p in face.Points)
        {
            if (Math.Abs((long)p.X) > MaxCoordinate || Math.Abs((long)p.Y) > MaxCoordinate)
            {
                throw new ArgumentOutOfRangeException(nameof(face), string.Create(CultureInfo.InvariantCulture,
                    $"the point ({p.X}, {p.Y}) is outside the ±{MaxCoordinate} the map's union handles."));
            }

            if (points.Count == 0 || points[^1] != p)
            {
                points.Add(p);
            }
        }

        while (points.Count > 1 && points[^1] == points[0])
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count < 3)
        {
            return null;
        }

        // The fan from the first point: the signed triangles sum to the
        // polygon's winding at every point off their edges, for any simple
        // polygon and for one the snap bent a little.
        List<Triangle> triangles = [];
        long area = 0;
        for (int i = 1; i + 1 < points.Count; i++)
        {
            long cross = Cross(points[0], points[i], points[i + 1]);
            area += cross;
            if (cross > 0)
            {
                triangles.Add(new Triangle(points[0], points[i], points[i + 1], 1));
            }
            else if (cross < 0)
            {
                triangles.Add(new Triangle(points[0], points[i + 1], points[i], -1));
            }
        }

        if (area <= 0 || triangles.Count == 0)
        {
            // Snapped to nothing, or turned over whole: a sliver thinner than a unit.
            return null;
        }

        return new Face(
            [.. triangles], Math.Min(face.ZLow, face.ZHigh), Math.Max(face.ZLow, face.ZHigh),
            points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    /// <summary>The faces grouped: connected components of "bands overlap and areas touch".</summary>
    private static List<List<Face>> Groups(List<Face> faces)
    {
        int[] parent = [.. Enumerable.Range(0, faces.Count)];
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        int[] byMinX = [.. Enumerable.Range(0, faces.Count).OrderBy(i => faces[i].MinX)];
        for (int a = 0; a < byMinX.Length; a++)
        {
            Face f = faces[byMinX[a]];
            for (int b = a + 1; b < byMinX.Length && faces[byMinX[b]].MinX <= f.MaxX; b++)
            {
                Face g = faces[byMinX[b]];
                if (g.MinY > f.MaxY || g.MaxY < f.MinY || g.ZLow > f.ZHigh || g.ZHigh < f.ZLow)
                {
                    continue;
                }

                int ra = Find(byMinX[a]);
                int rb = Find(byMinX[b]);
                if (ra != rb && Touch(f, g))
                {
                    parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                }
            }
        }

        Dictionary<int, List<Face>> groups = [];
        List<List<Face>> ordered = [];
        for (int i = 0; i < faces.Count; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out List<Face>? group))
            {
                groups[root] = group = [];
                ordered.Add(group);
            }

            group.Add(faces[i]);
        }

        return ordered;
    }

    /// <summary>Whether two faces' areas share a point.</summary>
    private static bool Touch(Face f, Face g)
    {
        foreach (Triangle s in f.Triangles)
        {
            foreach (Triangle t in g.Triangles)
            {
                if (s.Sign > 0 && t.Sign > 0 && TrianglesMeet(s, t))
                {
                    return true;
                }
            }
        }

        // A face whose every triangle the snap turned over has none that
        // counts; its area is then another triangle's, which a group finds
        // through the faces around it.
        return false;
    }

    /// <summary>Whether two closed counter-clockwise triangles share a point: no edge of either separates them strictly.</summary>
    private static bool TrianglesMeet(Triangle s, Triangle t)
    {
        return !Separates(s, t) && !Separates(t, s);

        static bool Separates(Triangle by, Triangle other)
        {
            ReadOnlySpan<MapPoint> e = [by.A, by.B, by.C];
            for (int i = 0; i < 3; i++)
            {
                MapPoint p = e[i];
                MapPoint q = e[(i + 1) % 3];
                if (Cross(p, q, other.A) < 0 && Cross(p, q, other.B) < 0 && Cross(p, q, other.C) < 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The union of one group's triangles, as polygons with holes.</summary>
    private static List<(List<MapPoint> Outer, List<List<MapPoint>> Holes)> UnionOf(List<Face> group)
    {
        List<Triangle> triangles = [.. group.SelectMany(f => f.Triangles)];

        // Every triangle edge once, as an undirected segment.
        HashSet<(MapPoint, MapPoint)> seen = [];
        List<(MapPoint P, MapPoint Q)> segments = [];
        foreach (Triangle t in triangles)
        {
            ReadOnlySpan<MapPoint> v = [t.A, t.B, t.C];
            for (int i = 0; i < 3; i++)
            {
                MapPoint p = v[i];
                MapPoint q = v[(i + 1) % 3];
                (MapPoint, MapPoint) key = p.CompareTo(q) < 0 ? (p, q) : (q, p);
                if (seen.Add(key))
                {
                    segments.Add(key);
                }
            }
        }

        // Where every segment is met by another: its split points.
        List<Rational>[] splits = [.. segments.Select(s => new List<Rational> { Rational.Of(s.P), Rational.Of(s.Q) })];
        int[] order = [.. Enumerable.Range(0, segments.Count).OrderBy(i => segments[i].P.X)];
        for (int a = 0; a < order.Length; a++)
        {
            (MapPoint p1, MapPoint p2) = segments[order[a]];
            int maxX = Math.Max(p1.X, p2.X);
            int minY = Math.Min(p1.Y, p2.Y), maxY = Math.Max(p1.Y, p2.Y);
            for (int b = a + 1; b < order.Length && segments[order[b]].P.X <= maxX; b++)
            {
                (MapPoint p3, MapPoint p4) = segments[order[b]];
                if (Math.Max(p3.Y, p4.Y) < minY || Math.Min(p3.Y, p4.Y) > maxY)
                {
                    continue;
                }

                Meet(p1, p2, p3, p4, splits[order[a]], splits[order[b]]);
            }
        }

        // The pieces, each once, with the direction of the line it lies on.
        Dictionary<(Rational, Rational), (long Dx, long Dy)> pieces = [];
        for (int s = 0; s < segments.Count; s++)
        {
            (MapPoint p, MapPoint q) = segments[s];
            long dx = q.X - p.X, dy = q.Y - p.Y;
            long g = (long)Gcd((ulong)Math.Abs(dx), (ulong)Math.Abs(dy));
            (dx, dy) = (dx / g, dy / g);
            List<Rational> along = splits[s];
            along.Sort((u, w) => Rational.CompareAlong(u, w, p, dx, dy));
            for (int i = 0; i + 1 < along.Count; i++)
            {
                if (along[i] != along[i + 1])
                {
                    // p is the segment's least point, so along the line (dx, dy) is from lesser to greater.
                    pieces.TryAdd((along[i], along[i + 1]), (dx, dy));
                }
            }
        }

        // The triangle index, by bucket of the plane.
        Dictionary<(long, long), List<int>> buckets = [];
        for (int i = 0; i < triangles.Count; i++)
        {
            Triangle t = triangles[i];
            for (long bx = Bucket(t.MinX); bx <= Bucket(t.MaxX); bx++)
            {
                for (long by = Bucket(t.MinY); by <= Bucket(t.MaxY); by++)
                {
                    if (!buckets.TryGetValue((bx, by), out List<int>? list))
                    {
                        buckets[(bx, by)] = list = [];
                    }

                    list.Add(i);
                }
            }
        }

        // The boundary: pieces whose two sides differ in being covered,
        // directed with the covered side on the left.
        List<(Rational From, Rational To, long Dx, long Dy)> boundary = [];
        foreach (((Rational from, Rational to), (long dx, long dy)) in pieces)
        {
            // The midpoint: (from + to) / 2, as a rational.
            Rational mid = Rational.Midpoint(from, to);
            (long bx, long by) = (Rational.FloorDiv(mid.X, mid.W * BucketSize), Rational.FloorDiv(mid.Y, mid.W * BucketSize));
            int left = 0, right = 0;
            if (buckets.TryGetValue((bx, by), out List<int>? candidates))
            {
                foreach (int i in candidates)
                {
                    Triangle t = triangles[i];
                    (bool l, bool r) = Sides(t, mid, dx, dy);
                    left += l ? t.Sign : 0;
                    right += r ? t.Sign : 0;
                }
            }

            if ((left > 0) != (right > 0))
            {
                boundary.Add(left > 0 ? (from, to, dx, dy) : (to, from, -dx, -dy));
            }
        }

        List<List<MapPoint>> rings = Chain(boundary);
        return Nest(rings);
    }

    /// <summary>Adds where two segments meet to both segments' split points.</summary>
    private static void Meet(MapPoint p1, MapPoint p2, MapPoint p3, MapPoint p4, List<Rational> first, List<Rational> second)
    {
        long rx = p2.X - p1.X, ry = p2.Y - p1.Y;
        long sx = p4.X - p3.X, sy = p4.Y - p3.Y;
        long qx = p3.X - p1.X, qy = p3.Y - p1.Y;
        long denominator = (rx * sy) - (ry * sx);
        if (denominator == 0)
        {
            if ((qx * ry) - (qy * rx) != 0)
            {
                return; // parallel, apart
            }

            // Collinear: each end of one inside the other splits it.
            AddIfWithin(p3, p1, p2, first);
            AddIfWithin(p4, p1, p2, first);
            AddIfWithin(p1, p3, p4, second);
            AddIfWithin(p2, p3, p4, second);
            return;
        }

        long t = (qx * sy) - (qy * sx);
        long u = (qx * ry) - (qy * rx);
        if (denominator < 0)
        {
            (denominator, t, u) = (-denominator, -t, -u);
        }

        if (t < 0 || t > denominator || u < 0 || u > denominator)
        {
            return;
        }

        Rational at = Rational.Make(
            ((Int128)p1.X * denominator) + ((Int128)rx * t),
            ((Int128)p1.Y * denominator) + ((Int128)ry * t),
            denominator);
        first.Add(at);
        second.Add(at);

        static void AddIfWithin(MapPoint point, MapPoint a, MapPoint b, List<Rational> into)
        {
            long dot = ((long)(point.X - a.X) * (b.X - a.X)) + ((long)(point.Y - a.Y) * (b.Y - a.Y));
            long length = ((long)(b.X - a.X) * (b.X - a.X)) + ((long)(b.Y - a.Y) * (b.Y - a.Y));
            if (dot > 0 && dot < length)
            {
                into.Add(Rational.Of(point));
            }
        }
    }

    /// <summary>
    /// Whether the region just left, and just right, of a piece with this
    /// midpoint and direction is inside a triangle. A piece never crosses an
    /// edge (it was split where one meets it), so its midpoint is strictly
    /// inside an edge's half plane, strictly outside it, or on the edge's
    /// line; on the line it lies along the edge, and the side it faces is
    /// inside exactly when the directions agree (left) or oppose (right).
    /// </summary>
    private static (bool Left, bool Right) Sides(Triangle t, Rational m, long dx, long dy)
    {
        bool left = true, right = true;
        ReadOnlySpan<MapPoint> v = [t.A, t.B, t.C];
        for (int i = 0; i < 3 && (left || right); i++)
        {
            MapPoint a = v[i];
            MapPoint b = v[(i + 1) % 3];
            long ex = b.X - a.X, ey = b.Y - a.Y;

            // cross(e, m - a), scaled by m's denominator.
            Int128 side = ((Int128)ex * (m.Y - ((Int128)a.Y * m.W))) - ((Int128)ey * (m.X - ((Int128)a.X * m.W)));
            if (side > 0)
            {
                continue;
            }

            if (side < 0 || (ex * dy) - (ey * dx) != 0)
            {
                // Outside the edge, or on its line beyond it (a piece is
                // split at every triangle vertex on it, so its midpoint on a
                // line it crosses is outside the triangle).
                return (false, false);
            }

            long dot = (ex * dx) + (ey * dy);
            left &= dot > 0;
            right &= dot < 0;
        }

        return (left, right);
    }

    /// <summary>
    /// Chains directed boundary pieces into rings: from each unused piece in
    /// sorted order, at every vertex the leftmost turn, until the piece it
    /// started with is next.
    /// </summary>
    private static List<List<MapPoint>> Chain(List<(Rational From, Rational To, long Dx, long Dy)> boundary)
    {
        boundary.Sort((a, b) =>
        {
            int c = a.From.CompareTo(b.From);
            return c != 0 ? c : a.To.CompareTo(b.To);
        });

        Dictionary<Rational, List<int>> outgoing = [];
        for (int i = 0; i < boundary.Count; i++)
        {
            if (!outgoing.TryGetValue(boundary[i].From, out List<int>? list))
            {
                outgoing[boundary[i].From] = list = [];
            }

            list.Add(i);
        }

        bool[] used = new bool[boundary.Count];
        List<List<MapPoint>> rings = [];
        for (int start = 0; start < boundary.Count; start++)
        {
            if (used[start])
            {
                continue;
            }

            List<Rational> ring = [];
            int current = start;
            while (true)
            {
                used[current] = true;
                ring.Add(boundary[current].From);
                (_, Rational at, long dx, long dy) = boundary[current];
                int next = -1;
                foreach (int candidate in outgoing.GetValueOrDefault(at) ?? [])
                {
                    if ((used[candidate] && candidate != start)
                        || (next >= 0 && !MoreLeft(dx, dy, boundary[candidate].Dx, boundary[candidate].Dy, boundary[next].Dx, boundary[next].Dy)))
                    {
                        continue;
                    }

                    next = candidate;
                }

                if (next < 0 || next == start)
                {
                    break;
                }

                current = next;
            }

            if (Round(ring) is { } rounded)
            {
                rings.Add(rounded);
            }
        }

        return rings;
    }

    /// <summary>Whether, arriving along (dx, dy), leaving along u turns further left than leaving along w.</summary>
    private static bool MoreLeft(long dx, long dy, long ux, long uy, long wx, long wy)
    {
        int hu = Half(dx, dy, ux, uy);
        int hw = Half(dx, dy, wx, wy);
        if (hu != hw)
        {
            return hu > hw;
        }

        // In one half plane, the one counter-clockwise of the other turns further left.
        return (ux * wy) - (uy * wx) < 0;

        // -1 right turn, 0 straight on, 1 left turn (a piece never leaves straight back).
        static int Half(long dx, long dy, long x, long y)
        {
            long cross = (dx * y) - (dy * x);
            return cross > 0 ? 1 : cross < 0 ? -1 : 0;
        }
    }

    /// <summary>
    /// A ring's points rounded to whole units (only a crossing's point is not
    /// whole already), repeated points and points on a straight line
    /// dropped, starting at its least point; null when nothing is left of it.
    /// </summary>
    private static List<MapPoint>? Round(List<Rational> ring)
    {
        List<MapPoint> points = [];
        foreach (Rational r in ring)
        {
            MapPoint p = r.Rounded();
            if (points.Count == 0 || points[^1] != p)
            {
                points.Add(p);
            }
        }

        bool changed = true;
        while (changed && points.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < points.Count && points.Count >= 3; i++)
            {
                MapPoint previous = points[(i + points.Count - 1) % points.Count];
                MapPoint next = points[(i + 1) % points.Count];
                if (previous == points[i] || Cross(previous, points[i], next) == 0)
                {
                    points.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }

        if (points.Count < 3 || Area2(points) == 0)
        {
            return null;
        }

        int least = 0;
        for (int i = 1; i < points.Count; i++)
        {
            least = points[i].CompareTo(points[least]) < 0 ? i : least;
        }

        return [.. points.Skip(least), .. points.Take(least)];
    }

    /// <summary>Outer rings (counter-clockwise) with the holes each holds: a hole goes to the smallest outer ring around it.</summary>
    private static List<(List<MapPoint> Outer, List<List<MapPoint>> Holes)> Nest(List<List<MapPoint>> rings)
    {
        List<List<MapPoint>> outers = [.. rings.Where(r => Area2(r) > 0)];
        outers.Sort(CompareRings);
        List<List<List<MapPoint>>> holes = [.. outers.Select(_ => new List<List<MapPoint>>())];
        foreach (List<MapPoint> hole in rings.Where(r => Area2(r) < 0))
        {
            int parent = -1;
            for (int o = 0; o < outers.Count; o++)
            {
                if (Contains(outers[o], hole) && (parent < 0 || Area2(outers[o]) < Area2(outers[parent])))
                {
                    parent = o;
                }
            }

            // A hole around nothing covers nothing: only the rounding of a
            // crossing could leave one, and it is dropped.
            if (parent >= 0)
            {
                holes[parent].Add(hole);
            }
        }

        List<(List<MapPoint>, List<List<MapPoint>>)> polygons = [];
        for (int o = 0; o < outers.Count; o++)
        {
            holes[o].Sort(CompareRings);
            polygons.Add((outers[o], holes[o]));
        }

        return polygons;
    }

    /// <summary>Whether a hole lies inside an outer ring: its first point not on the ring's boundary decides.</summary>
    private static bool Contains(List<MapPoint> outer, List<MapPoint> hole)
    {
        foreach (MapPoint p in hole)
        {
            switch (PointIn(outer, p))
            {
                case > 0:
                    return true;
                case < 0:
                    return false;
            }
        }

        return false;
    }

    /// <summary>1 inside, -1 outside, 0 on the boundary: the crossing count, in integers.</summary>
    private static int PointIn(List<MapPoint> ring, MapPoint p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            MapPoint a = ring[j];
            MapPoint b = ring[i];
            if (Cross(a, b, p) == 0 && Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X)
                && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y))
            {
                return 0;
            }

            if ((a.Y > p.Y) != (b.Y > p.Y))
            {
                // Is p left of the edge going up (or right going down)? Integer form of the crossing's x.
                long cross = Cross(a, b, p);
                if ((b.Y > a.Y) ? cross > 0 : cross < 0)
                {
                    inside = !inside;
                }
            }
        }

        return inside ? 1 : -1;
    }

    private static int ComparePolygons(MapPolygon a, MapPolygon b)
    {
        int c = a.ZLow.CompareTo(b.ZLow);
        c = c != 0 ? c : a.ZHigh.CompareTo(b.ZHigh);
        return c != 0 ? c : CompareRings(a.Outer, b.Outer);
    }

    /// <summary>Rings in order of their points, point by point, then by length.</summary>
    internal static int CompareRings(IReadOnlyList<MapPoint> a, IReadOnlyList<MapPoint> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return a.Count.CompareTo(b.Count);
    }

    /// <summary>Twice the ring's signed area: positive counter-clockwise.</summary>
    internal static long Area2(IReadOnlyList<MapPoint> ring)
    {
        long area = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            area += ((long)ring[j].X * ring[i].Y) - ((long)ring[i].X * ring[j].Y);
        }

        return area;
    }

    private static long Cross(MapPoint o, MapPoint a, MapPoint b) =>
        ((long)(a.X - o.X) * (b.Y - o.Y)) - ((long)(a.Y - o.Y) * (b.X - o.X));

    private static long Bucket(int value) => Rational.FloorDiv(value, BucketSize);

    private static UInt128 Gcd(UInt128 a, UInt128 b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }

    /// <summary>A point with rational coordinates X / W, Y / W, W positive, in lowest terms: equal points, equal values.</summary>
    private readonly record struct Rational(Int128 X, Int128 Y, Int128 W) : IComparable<Rational>
    {
        public static Rational Of(MapPoint p) => new(p.X, p.Y, 1);

        public static Rational Make(Int128 x, Int128 y, Int128 w)
        {
            if (w < 0)
            {
                (x, y, w) = (-x, -y, -w);
            }

            UInt128 g = Gcd(Gcd(Magnitude(x), Magnitude(y)), (UInt128)w);
            return g == 1 ? new Rational(x, y, w) : new Rational(x / (Int128)g, y / (Int128)g, w / (Int128)g);

            static UInt128 Magnitude(Int128 v) => v < 0 ? (UInt128)(-v) : (UInt128)v;
        }

        public static Rational Midpoint(Rational a, Rational b) =>
            Make((a.X * b.W) + (b.X * a.W), (a.Y * b.W) + (b.Y * a.W), 2 * a.W * b.W);

        /// <summary>Orders two points on the line through <paramref name="origin"/> along (dx, dy).</summary>
        public static int CompareAlong(Rational a, Rational b, MapPoint origin, long dx, long dy)
        {
            Int128 ka = ((a.X - ((Int128)origin.X * a.W)) * dx) + ((a.Y - ((Int128)origin.Y * a.W)) * dy);
            Int128 kb = ((b.X - ((Int128)origin.X * b.W)) * dx) + ((b.Y - ((Int128)origin.Y * b.W)) * dy);
            return (ka * b.W).CompareTo(kb * a.W);
        }

        public static long FloorDiv(Int128 value, Int128 divisor)
        {
            Int128 q = Int128.DivRem(value, divisor).Quotient;
            if ((value % divisor != 0) && ((value < 0) != (divisor < 0)))
            {
                q--;
            }

            return (long)q;
        }

        /// <summary>The nearest whole point, halves rounded up.</summary>
        public MapPoint Rounded() =>
            W == 1 ? new MapPoint((int)X, (int)Y) : new MapPoint((int)FloorDiv((2 * X) + W, 2 * W), (int)FloorDiv((2 * Y) + W, 2 * W));

        public int CompareTo(Rational other)
        {
            int c = (X * other.W).CompareTo(other.X * W);
            return c != 0 ? c : (Y * other.W).CompareTo(other.Y * W);
        }
    }
}
