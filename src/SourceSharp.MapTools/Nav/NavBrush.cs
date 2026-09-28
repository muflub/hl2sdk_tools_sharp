//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// One convex brush as the voxeliser tests it: its bounding planes, its
/// corners, its edge directions and its contents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why brushes.</b> The engine's movement code sweeps an axis-aligned box
/// against the world's brushes (with their bevel planes), not against the
/// drawn faces, so the free space an agent has is exactly the space its box
/// can occupy without overlapping a brush of a contents it collides with.
/// The voxeliser works on the compiled BSP's brushes for that reason: they
/// are what a trace in the game sees, clips and grates included, which the
/// faces are not.
/// </para>
/// <para>
/// <b>An exact overlap test.</b> A box overlaps a convex brush exactly when
/// no separating axis exists among the box's three axes, the brush's plane
/// normals, and the cross products of the brush's edges with the box's
/// axes (the separating axis theorem for two convex polyhedra). All three
/// families are tested, so a slope or a wedge is neither too fat nor too
/// thin: the facts on analytic shapes depend on it. Touching is not
/// overlapping: an agent standing on a floor touches it, and a separation
/// of less than <see cref="Epsilon"/> counts as touching, which absorbs the
/// float rounding of a compiled plane.
/// </para>
/// <para>
/// Arithmetic is in double and uses only IEEE-exact operations (add,
/// multiply, divide, square root), in a fixed order, so the answer is the
/// same on every machine.
/// </para>
/// </remarks>
public sealed class NavBrush
{
    /// <summary>How close two solids may come and still count as touching, not overlapping, in units.</summary>
    public const double Epsilon = 1e-3;

    // Where the corners' coordinates are rounded to find duplicates.
    private const double VertexWeld = 1e-4;

    // Half the side of the square each plane's winding starts from: beyond
    // any map's extent.
    private const double Huge = 1 << 20;

    private readonly double[] _planes;
    private readonly double[] _vertices;
    private readonly double[] _edges;

    private NavBrush(double[] planes, double[] vertices, double[] edges, int contents)
    {
        _planes = planes;
        _vertices = vertices;
        _edges = edges;
        Contents = contents;
        MinX = MinY = MinZ = double.MaxValue;
        MaxX = MaxY = MaxZ = double.MinValue;
        for (int i = 0; i < vertices.Length; i += 3)
        {
            MinX = Math.Min(MinX, vertices[i]);
            MinY = Math.Min(MinY, vertices[i + 1]);
            MinZ = Math.Min(MinZ, vertices[i + 2]);
            MaxX = Math.Max(MaxX, vertices[i]);
            MaxY = Math.Max(MaxY, vertices[i + 1]);
            MaxZ = Math.Max(MaxZ, vertices[i + 2]);
        }
    }

    /// <summary>The brush's <c>CONTENTS_*</c> bits.</summary>
    public int Contents { get; }

    /// <summary>How many bounding planes the brush has.</summary>
    public int PlaneCount => _planes.Length / 4;

    /// <summary>How many corners the brush has.</summary>
    public int VertexCount => _vertices.Length / 3;

    /// <summary>The bounding box's low x.</summary>
    public double MinX { get; }

    /// <summary>The bounding box's low y.</summary>
    public double MinY { get; }

    /// <summary>The bounding box's low z.</summary>
    public double MinZ { get; }

    /// <summary>The bounding box's high x.</summary>
    public double MaxX { get; }

    /// <summary>The bounding box's high y.</summary>
    public double MaxY { get; }

    /// <summary>The bounding box's high z.</summary>
    public double MaxZ { get; }

    /// <summary>One bounding plane: outward normal and distance, the inside being <c>n·x ≤ d</c>.</summary>
    /// <param name="index">The plane.</param>
    /// <returns>Its normal and distance.</returns>
    public (double X, double Y, double Z, double D) Plane(int index) =>
        (_planes[index * 4], _planes[(index * 4) + 1], _planes[(index * 4) + 2], _planes[(index * 4) + 3]);

    /// <summary>An axis-aligned box brush.</summary>
    /// <param name="mins">The low corner.</param>
    /// <param name="maxs">The high corner.</param>
    /// <param name="contents">Its contents.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">The box is empty.</exception>
    public static NavBrush Box(Vec3 mins, Vec3 maxs, int contents)
    {
        if (!(mins.X < maxs.X && mins.Y < maxs.Y && mins.Z < maxs.Z))
        {
            throw new ArgumentException("a box brush has positive extent on every axis.", nameof(maxs));
        }

        double[] planes =
        [
            1, 0, 0, maxs.X, -1, 0, 0, -mins.X,
            0, 1, 0, maxs.Y, 0, -1, 0, -mins.Y,
            0, 0, 1, maxs.Z, 0, 0, -1, -mins.Z,
        ];
        double[] vertices = new double[24];
        for (int i = 0; i < 8; i++)
        {
            vertices[i * 3] = (i & 1) == 0 ? mins.X : maxs.X;
            vertices[(i * 3) + 1] = (i & 2) == 0 ? mins.Y : maxs.Y;
            vertices[(i * 3) + 2] = (i & 4) == 0 ? mins.Z : maxs.Z;
        }

        return new NavBrush(planes, vertices, [1, 0, 0, 0, 1, 0, 0, 0, 1], contents);
    }

    /// <summary>A brush bounded by planes, its corners and edges found by clipping each plane by the rest.</summary>
    /// <param name="planes">The outward normals (unit length) and distances.</param>
    /// <param name="contents">Its contents.</param>
    /// <returns>The brush, or null when the planes bound nothing with volume.</returns>
    public static NavBrush? FromPlanes(IReadOnlyList<(Vec3 Normal, float Dist)> planes, int contents)
    {
        ArgumentNullException.ThrowIfNull(planes);
        double[] p = new double[planes.Count * 4];
        for (int i = 0; i < planes.Count; i++)
        {
            p[i * 4] = planes[i].Normal.X;
            p[(i * 4) + 1] = planes[i].Normal.Y;
            p[(i * 4) + 2] = planes[i].Normal.Z;
            p[(i * 4) + 3] = planes[i].Dist;
        }

        return FromPlanes(p, contents);
    }

    /// <summary>Every brush of a BSP's brush lump, with its contents.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <returns>The brushes, in lump order; a brush whose planes bound no volume is left out.</returns>
    /// <remarks>
    /// Bevel planes are kept: they are supporting planes of the brush, so
    /// they change neither its shape nor the overlap test's answer, and a
    /// box sliding along a bevelled edge meets the bevel in the game too.
    /// </remarks>
    public static List<NavBrush> FromBsp(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ReadOnlySpan<DBrush> brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]);
        ReadOnlySpan<DBrushSide> sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        List<NavBrush> result = new(brushes.Length);
        foreach (DBrush brush in brushes)
        {
            double[] p = new double[brush.NumSides * 4];
            for (int s = 0; s < brush.NumSides; s++)
            {
                DPlane plane = planes[sides[brush.FirstSide + s].PlaneNum];
                p[s * 4] = plane.Normal.X;
                p[(s * 4) + 1] = plane.Normal.Y;
                p[(s * 4) + 2] = plane.Normal.Z;
                p[(s * 4) + 3] = plane.Dist;
            }

            if (FromPlanes(p, brush.Contents) is { } built)
            {
                result.Add(built);
            }
        }

        return result;
    }

    /// <summary>Whether a box's interior overlaps the brush's, by more than <see cref="Epsilon"/>.</summary>
    /// <param name="box">The box.</param>
    /// <returns>True when no separating axis exists.</returns>
    public bool Overlaps(in NavBox box)
    {
        const double e = Epsilon;
        if (MaxX <= box.MinX + e || MinX >= box.MaxX - e
            || MaxY <= box.MinY + e || MinY >= box.MaxY - e
            || MaxZ <= box.MinZ + e || MinZ >= box.MaxZ - e)
        {
            return false;
        }

        for (int i = 0; i < PlaneCount; i++)
        {
            if (FaceSeparates(i, box))
            {
                return false;
            }
        }

        double cx = (box.MinX + box.MaxX) * 0.5;
        double cy = (box.MinY + box.MaxY) * 0.5;
        double cz = (box.MinZ + box.MaxZ) * 0.5;
        double hx = (box.MaxX - box.MinX) * 0.5;
        double hy = (box.MaxY - box.MinY) * 0.5;
        double hz = (box.MaxZ - box.MinZ) * 0.5;
        for (int i = 0; i < _edges.Length; i += 3)
        {
            double ex = _edges[i];
            double ey = _edges[i + 1];
            double ez = _edges[i + 2];
            for (int axis = 0; axis < 3; axis++)
            {
                // edge × box axis
                (double ax, double ay, double az) = axis switch
                {
                    0 => (0.0, ez, -ey),
                    1 => (-ez, 0.0, ex),
                    _ => (ey, -ex, 0.0),
                };
                double length = Math.Sqrt((ax * ax) + (ay * ay) + (az * az));
                if (length < 1e-9)
                {
                    continue;
                }

                ax /= length;
                ay /= length;
                az /= length;
                double centre = (ax * cx) + (ay * cy) + (az * cz);
                double radius = (Math.Abs(ax) * hx) + (Math.Abs(ay) * hy) + (Math.Abs(az) * hz);
                (double lo, double hi) = Project(ax, ay, az);
                if (hi <= centre - radius + e || lo >= centre + radius - e)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether one of the brush's planes has the whole box on its outside (touching allowed).</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="box">The box.</param>
    /// <returns>True when the plane separates the box from the brush.</returns>
    public bool FaceSeparates(int plane, in NavBox box)
    {
        double nx = _planes[plane * 4];
        double ny = _planes[(plane * 4) + 1];
        double nz = _planes[(plane * 4) + 2];
        double d = _planes[(plane * 4) + 3];

        // The box's least projection on the normal: its corner furthest
        // against the normal.
        double least = (nx >= 0 ? nx * box.MinX : nx * box.MaxX)
            + (ny >= 0 ? ny * box.MinY : ny * box.MaxY)
            + (nz >= 0 ? nz * box.MinZ : nz * box.MaxZ);
        return least >= d - Epsilon;
    }

    /// <summary>The brush turned a quarter turn about +z, some number of times, about a centre line.</summary>
    /// <param name="quarterTurns">Counter-clockwise quarter turns, 0 to 3.</param>
    /// <param name="size">The cell's edge: the turn maps the cell <c>[0, size]²</c> onto itself.</param>
    /// <returns>The turned brush: its planes permuted and negated exactly, its corners and edges likewise.</returns>
    /// <remarks>
    /// The same exact permutation <see cref="Rooms.RoomTransform"/> applies
    /// to a room at a turned placement, so a fact can voxelise a room turned
    /// and compare it with the room's stored grid turned.
    /// </remarks>
    public NavBrush Turned(int quarterTurns, double size)
    {
        int r = ((quarterTurns % 4) + 4) % 4;
        double[] planes = (double[])_planes.Clone();
        double[] vertices = (double[])_vertices.Clone();
        double[] edges = (double[])_edges.Clone();
        for (int t = 0; t < r; t++)
        {
            for (int i = 0; i < planes.Length; i += 4)
            {
                // n·x ≤ d with x' = (size - y, x): n' = (-ny, nx), d' = d - ny·size
                double nx = planes[i];
                double ny = planes[i + 1];
                planes[i] = -ny;
                planes[i + 1] = nx;
                planes[i + 3] -= ny * size;
            }

            for (int i = 0; i < vertices.Length; i += 3)
            {
                double x = vertices[i];
                vertices[i] = size - vertices[i + 1];
                vertices[i + 1] = x;
            }

            for (int i = 0; i < edges.Length; i += 3)
            {
                double x = edges[i];
                edges[i] = -edges[i + 1];
                edges[i + 1] = x;
            }
        }

        return new NavBrush(planes, vertices, edges, Contents);
    }

    private (double Lo, double Hi) Project(double ax, double ay, double az)
    {
        double lo = double.MaxValue;
        double hi = double.MinValue;
        for (int v = 0; v < _vertices.Length; v += 3)
        {
            double d = (ax * _vertices[v]) + (ay * _vertices[v + 1]) + (az * _vertices[v + 2]);
            lo = Math.Min(lo, d);
            hi = Math.Max(hi, d);
        }

        return (lo, hi);
    }

    private static NavBrush? FromPlanes(double[] planes, int contents)
    {
        int count = planes.Length / 4;
        List<double> vertices = [];
        List<double> edges = [];
        List<double> polygon = [];
        List<double> clipped = [];
        for (int i = 0; i < count; i++)
        {
            BaseWinding(planes, i, polygon);
            for (int j = 0; j < count && polygon.Count >= 9; j++)
            {
                if (j != i)
                {
                    Clip(polygon, planes, j, clipped);
                    (polygon, clipped) = (clipped, polygon);
                }
            }

            if (polygon.Count < 9)
            {
                continue;
            }

            int points = polygon.Count / 3;
            for (int k = 0; k < points; k++)
            {
                AddVertex(vertices, polygon[k * 3], polygon[(k * 3) + 1], polygon[(k * 3) + 2]);
                int next = (k + 1) % points;
                AddEdge(
                    edges,
                    polygon[next * 3] - polygon[k * 3],
                    polygon[(next * 3) + 1] - polygon[(k * 3) + 1],
                    polygon[(next * 3) + 2] - polygon[(k * 3) + 2]);
            }
        }

        if (vertices.Count < 12)
        {
            return null;
        }

        return new NavBrush(planes, [.. vertices], [.. edges], contents);
    }

    /// <summary>A square far larger than any map, on one plane, wound so its normal is the plane's.</summary>
    private static void BaseWinding(double[] planes, int index, List<double> polygon)
    {
        double nx = planes[index * 4];
        double ny = planes[(index * 4) + 1];
        double nz = planes[(index * 4) + 2];
        double d = planes[(index * 4) + 3];

        // A direction in the plane: the normal crossed with whichever of up
        // or +x it is least parallel to.
        (double ux, double uy, double uz) = Math.Abs(nz) < 0.9 ? (-ny, nx, 0.0) : (0.0, nz, -ny);
        double ul = Math.Sqrt((ux * ux) + (uy * uy) + (uz * uz));
        ux /= ul;
        uy /= ul;
        uz /= ul;

        // v = n × u
        double vx = (ny * uz) - (nz * uy);
        double vy = (nz * ux) - (nx * uz);
        double vz = (nx * uy) - (ny * ux);
        double ox = nx * d;
        double oy = ny * d;
        double oz = nz * d;

        polygon.Clear();
        ReadOnlySpan<(double U, double V)> corners = [(-1, -1), (1, -1), (1, 1), (-1, 1)];
        foreach ((double cu, double cv) in corners)
        {
            polygon.Add(ox + (Huge * ((cu * ux) + (cv * vx))));
            polygon.Add(oy + (Huge * ((cu * uy) + (cv * vy))));
            polygon.Add(oz + (Huge * ((cu * uz) + (cv * vz))));
        }
    }

    /// <summary>Keeps the part of a convex polygon inside one plane (<c>n·x ≤ d</c>, with a sliver of slack).</summary>
    private static void Clip(List<double> polygon, double[] planes, int index, List<double> output)
    {
        double nx = planes[index * 4];
        double ny = planes[(index * 4) + 1];
        double nz = planes[(index * 4) + 2];
        double d = planes[(index * 4) + 3];
        output.Clear();
        int points = polygon.Count / 3;
        for (int k = 0; k < points; k++)
        {
            int next = (k + 1) % points;
            double ax = polygon[k * 3];
            double ay = polygon[(k * 3) + 1];
            double az = polygon[(k * 3) + 2];
            double bx = polygon[next * 3];
            double by = polygon[(next * 3) + 1];
            double bz = polygon[(next * 3) + 2];
            double da = (nx * ax) + (ny * ay) + (nz * az) - d;
            double db = (nx * bx) + (ny * by) + (nz * bz) - d;
            if (da <= VertexWeld)
            {
                output.Add(ax);
                output.Add(ay);
                output.Add(az);
            }

            if ((da < -VertexWeld && db > VertexWeld) || (da > VertexWeld && db < -VertexWeld))
            {
                double t = da / (da - db);
                output.Add(ax + ((bx - ax) * t));
                output.Add(ay + ((by - ay) * t));
                output.Add(az + ((bz - az) * t));
            }
        }
    }

    private static void AddVertex(List<double> vertices, double x, double y, double z)
    {
        for (int i = 0; i < vertices.Count; i += 3)
        {
            if (Math.Abs(vertices[i] - x) <= VertexWeld && Math.Abs(vertices[i + 1] - y) <= VertexWeld
                && Math.Abs(vertices[i + 2] - z) <= VertexWeld)
            {
                return;
            }
        }

        vertices.Add(x);
        vertices.Add(y);
        vertices.Add(z);
    }

    private static void AddEdge(List<double> edges, double x, double y, double z)
    {
        double length = Math.Sqrt((x * x) + (y * y) + (z * z));
        if (length < VertexWeld)
        {
            return;
        }

        x /= length;
        y /= length;
        z /= length;
        for (int i = 0; i < edges.Count; i += 3)
        {
            double dot = (edges[i] * x) + (edges[i + 1] * y) + (edges[i + 2] * z);
            if (Math.Abs(dot) >= 1 - 1e-9)
            {
                return;
            }
        }

        edges.Add(x);
        edges.Add(y);
        edges.Add(z);
    }
}

/// <summary>An axis-aligned box in double precision: an agent's box swept over a voxel.</summary>
/// <param name="MinX">Low x.</param>
/// <param name="MinY">Low y.</param>
/// <param name="MinZ">Low z.</param>
/// <param name="MaxX">High x.</param>
/// <param name="MaxY">High y.</param>
/// <param name="MaxZ">High z.</param>
public readonly record struct NavBox(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);
