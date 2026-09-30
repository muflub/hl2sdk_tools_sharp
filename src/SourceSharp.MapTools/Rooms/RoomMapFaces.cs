//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rad;

namespace SourceSharp.MapTools.Rooms;

/// <summary>A walkable face of a compile, in the compile's frame, in doubles.</summary>
/// <param name="Points">
/// Its points, x, y and z, in the face's order; for a displacement, its flat
/// base face's four, which only say where it is (the cell that owns it).
/// </param>
/// <param name="World">Whether it is the world model's (else a brush entity's).</param>
/// <param name="Displacement">
/// The displaced surface standing on the face, or null for a plain face.
/// When set, the face itself is not floor: the surface's walkable triangles
/// are (<see cref="RoomMapFaces.Place"/>).
/// </param>
internal sealed record MapFaceSource(IReadOnlyList<(double X, double Y, double Z)> Points, bool World, MapDisplacementSource? Displacement = null);

/// <summary>
/// What a displaced surface is built from, as a compile holds it, in the
/// compile's frame: the recipe rather than the triangles, so that a placed
/// room's surface is rebuilt in the room's own frame (see
/// <see cref="RoomMapFaces"/>).
/// </summary>
/// <param name="Corners">The base face's four points in winding order (floats, as the compile stores them).</param>
/// <param name="Start">The displacement's start position.</param>
/// <param name="Power">Its power.</param>
/// <param name="Verts">Its vertices' vectors, distances and alphas, row by row.</param>
/// <param name="Removed">Per triangle, in the surface's triangle order, whether the author removed it (its tag's remove bit).</param>
/// <param name="Front">The base face's plane normal: the side the surface faces.</param>
internal sealed record MapDisplacementSource(
    IReadOnlyList<(double X, double Y, double Z)> Corners,
    (double X, double Y, double Z) Start,
    int Power,
    IReadOnlyList<DispVert> Verts,
    IReadOnlyList<bool> Removed,
    (double X, double Y, double Z) Front);

/// <summary>
/// Where one placement's faces are cut from a compile and how they are taken
/// into the room's own frame: the placement's cell, its turn, and the boxes
/// whose floor is a door's rather than the room's.
/// </summary>
/// <param name="Cell">The placement's box in the compile's frame (x, y, z low, then high), or null for the whole compile.</param>
/// <param name="Placement">The placement, to take a point into the room's frame; null when the compile is in that frame already.</param>
/// <param name="CellSize">The level's cell size, for the turn.</param>
/// <param name="Plugs">The room's plug boxes, room-local: floor inside one is its door's.</param>
/// <param name="Furniture">The boxes of the room's socket furniture brushes, room-local: a brush entity's face inside one is door hardware.</param>
internal sealed record MapCut(
    (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)? Cell,
    RoomPlacement? Placement,
    float CellSize,
    IReadOnlyList<Box> Plugs,
    IReadOnlyList<Box> Furniture);

/// <summary>
/// The playable area's source (the rooms design, 18.2): the walkable faces
/// of a compile, and one placement's of them cut, taken into the room's own
/// frame and snapped, ready for <see cref="MapPolygonUnion"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The face rule</b> (<see cref="Walkable"/>), one function for the pack
/// and for <c>ssmap map2d</c>, so both read the same faces from the same
/// compile. A face counts when its plane's normal points up at least as
/// steeply as the player's walkable slope (normal z of at least
/// <see cref="WalkableNormalZ"/>, the bound the movement code stands a
/// player on), and it is drawn: sky, 2D sky, nodraw, water (warp),
/// trigger (the door plugs and caps), hint and skip faces are not floors.
/// Faces a solid brush sits on are never faces of a compile at all: the
/// compiler emits a face only where a brush side meets open space, so a
/// floor under a wall or a crate is already gone.
/// </para>
/// <para>
/// <b>Displacements</b> are floor by their displaced surface, never by
/// their flat base face (which is not where a player stands): the surface is
/// built from the compile's lumps as vrad and the engine build it
/// (<see cref="DisplacementSurface"/>: the base face's corners from the start
/// corner, each vertex its flat point plus its vector times its distance,
/// two triangles a grid square with the diagonal alternating), and each
/// triangle whose normal, taken on the side the base face faces, has z of
/// at least <see cref="WalkableNormalZ"/> is a face of its own through the
/// rest of the rule. A steep triangle (a cliff, the side of a mound) is not
/// floor, as a steep brush face is not: it is left out, so the map shows the
/// walkable ground around it and the slope as a gap or an edge, exactly as a
/// wall. A triangle the author removed (its tag's remove bit) is no surface
/// at all and is left out too. The base face's own flags decide as a plain
/// face's do (a nodraw or skip displacement is not floor), but not its
/// slope: a displacement on a wall face may be sculpted into ground.
/// </para>
/// <para>
/// A placed room's surface is rebuilt in the room's own frame: its base
/// corners and start taken back through the placement (a quarter turn and a
/// whole-cell move, exact) and its vectors turned back, then tessellated.
/// The pack builds the room compile's surface in that frame from the same
/// numbers, so the two sides tessellate the same floats with the same
/// arithmetic and agree bit for bit; tessellating in the level's frame and
/// then moving the triangles would round every vertex in a different place.
/// For the same reason the triangles are cut to the cell in the room's
/// frame, and a displacement belongs to one placement only, the one whose
/// cell holds its base face's centre (<see cref="OwnerCell"/>): a
/// displacement is never merged with another room's, and a surface reaching
/// a hair over its cell's edge (as the pack's cell rule allows) is cut there
/// on both sides rather than drawn into the neighbour's.
/// </para>
/// <para>
/// <b>Brush entities</b> count when a player can stand on them: the classes
/// in <see cref="PlayerSolidClasses"/> (a <c>func_brush</c> unless its
/// <c>Solidity</c> is 1, never solid), their faces moved by the entity's
/// <c>origin</c>. <c>func_detail</c> compiles into the world and needs no
/// rule. Socket furniture (a brush entity with <c>room_socket</c>) is door
/// hardware, not floor: a brush entity's face inside a furniture brush's box
/// is left out (<see cref="MapCut.Furniture"/>), by its geometry rather than
/// its key, because the flattened level's compile, which <c>ssmap map2d</c>
/// reads, carries the furniture without the key.
/// </para>
/// <para>
/// <b>The cut</b> (<see cref="Place"/>). With a cell, a face is clipped to
/// the cell's x and y (the flattened compile may merge a floor across two
/// rooms' doorway) and kept only when its z lies within the cell's; then
/// taken into the room's frame (a quarter turn and a whole-cell move,
/// exact in doubles). Floor inside a plug box is the door's, not the
/// room's: a joined doorway's floor, which only the flattened compile has
/// (the room compiles with its plugs in), is cut away on both sides, and the
/// door is drawn as its segment instead. The snap is last, in the room's
/// frame: whole units, halves up. Snapping in the room's own frame, on the
/// pack's side and on the flattened compile's alike, is what keeps the two
/// maps equal: a half unit rounds the same way whichever turn the room is
/// placed at, because it is never rounded turned.
/// </para>
/// </remarks>
internal static class RoomMapFaces
{
    /// <summary>The steepest floor a player stands on: a face's normal z at least this.</summary>
    public const float WalkableNormalZ = 0.7f;

    /// <summary>How far outside a box a point may be and still count as in it, in units.</summary>
    public const double BoxEpsilon = 0.01;

    /// <summary>The brush entity classes a player stands on (a <c>func_brush</c> as its <c>Solidity</c> says).</summary>
    public static readonly ImmutableHashSet<string> PlayerSolidClasses = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "func_brush", "func_door", "func_door_rotating", "func_movelinear", "func_platrot", "func_tracktrain", "func_train",
        "func_breakable", "func_physbox", "func_wall", "func_wall_toggle", "func_button", "func_rot_button");

    private const int NotFloor = (int)(SurfaceFlags.Sky | SurfaceFlags.Sky2D | SurfaceFlags.NoDraw | SurfaceFlags.Warp
        | SurfaceFlags.Trigger | SurfaceFlags.Hint | SurfaceFlags.Skip);

    /// <summary>The walkable faces of a compile: the world's and the player-solid brush entities'.</summary>
    /// <param name="bsp">The compile.</param>
    /// <returns>The faces, in face order, world first then each entity's in entity order.</returns>
    public static List<MapFaceSource> Walkable(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        List<MapFaceSource> faces = [];
        if (models.Length == 0)
        {
            return faces;
        }

        Add(bsp, models[0], (0, 0, 0), world: true, faces);
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            if (!IsPlayerSolid(entity)
                || entity.Get("model") is not { Length: > 1 } model || model[0] != '*'
                || !int.TryParse(model.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index <= 0 || index >= models.Length)
            {
                continue;
            }

            Add(bsp, models[index], OriginOf(entity), world: false, faces);
        }

        return faces;
    }

    /// <summary>Whether a brush entity is one a player stands on.</summary>
    /// <param name="entity">The entity, as the compile's entity lump holds it.</param>
    /// <returns>True for a class of <see cref="PlayerSolidClasses"/>, and a <c>func_brush</c> that is not never-solid.</returns>
    public static bool IsPlayerSolid(BspEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return PlayerSolidClasses.Contains(entity.ClassName)
            && !(entity.ClassName == "func_brush" && entity.Get("Solidity")?.Trim() == "1");
    }

    /// <summary>One placement's faces, cut, in the room's frame, snapped.</summary>
    /// <param name="faces">The compile's walkable faces (<see cref="Walkable"/>).</param>
    /// <param name="cut">Where the placement is and what of it is a door's.</param>
    /// <returns>The pieces, ready for the union.</returns>
    public static List<MapFacePolygon> Place(IEnumerable<MapFaceSource> faces, MapCut cut)
    {
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(cut);
        List<MapFacePolygon> placed = [];
        foreach (MapFaceSource face in faces)
        {
            if (face.Displacement is { } displacement)
            {
                foreach (List<(double X, double Y, double Z)> triangle in Triangles(displacement, cut))
                {
                    AddPieces(triangle, cut, placed);
                }

                continue;
            }

            List<(double X, double Y, double Z)> polygon = [.. face.Points];
            if (cut.Cell is { } cell)
            {
                if (polygon.Any(p => p.Z < cell.MinZ - BoxEpsilon || p.Z > cell.MaxZ + BoxEpsilon))
                {
                    continue;
                }

                polygon = Clip(polygon, 0, cell.MinX, keepAbove: true);
                polygon = Clip(polygon, 0, cell.MaxX, keepAbove: false);
                polygon = Clip(polygon, 1, cell.MinY, keepAbove: true);
                polygon = Clip(polygon, 1, cell.MaxY, keepAbove: false);
                if (polygon.Count < 3)
                {
                    continue;
                }
            }

            if (cut.Placement is { } placement)
            {
                polygon = [.. polygon.Select(p => ToLocal(p, placement, cut.CellSize))];
            }

            if (!face.World && cut.Furniture.Any(box => polygon.All(p => Inside(p, box))))
            {
                continue;
            }

            AddPieces(polygon, cut, placed);
        }

        return placed;
    }

    /// <summary>
    /// The placement that owns a displacement: the cell holding its base
    /// face's centre, in the compile's frame.
    /// </summary>
    /// <param name="face">The displacement's source (its <see cref="MapFaceSource.Points"/> are the base face's).</param>
    /// <param name="cellSize">The level's cell size.</param>
    /// <returns>The cell's column and row.</returns>
    public static (long X, long Y) OwnerCell(MapFaceSource face, double cellSize)
    {
        ArgumentNullException.ThrowIfNull(face);
        double x = 0, y = 0;
        foreach ((double px, double py, _) in face.Points)
        {
            x += px;
            y += py;
        }

        return ((long)Math.Floor(x / face.Points.Count / cellSize), (long)Math.Floor(y / face.Points.Count / cellSize));
    }

    /// <summary>
    /// A displacement's walkable triangles, in the room's frame (the
    /// placement's, or the compile's when there is none) and cut to the
    /// cell there: each kept only when all of it lies within the cell's
    /// height, then clipped to the cell's x and y.
    /// </summary>
    private static List<List<(double X, double Y, double Z)>> Triangles(MapDisplacementSource displacement, MapCut cut)
    {
        List<List<(double X, double Y, double Z)>> triangles = [];
        RoomPlacement? placement = cut.Placement;
        Vec3[] corners = [.. displacement.Corners.Select(p => Float(Local(p)))];
        DispVert[] verts = [.. displacement.Verts];
        for (int v = 0; v < verts.Length; v++)
        {
            verts[v].Vector = Turn(verts[v].Vector);
        }

        DispInfo info = default;
        info.StartPosition = Float(Local(displacement.Start));
        info.Power = displacement.Power;
        DisplacementSurface surface = DisplacementSurface.Create(0, 0, info, corners, verts);

        // The side the surface faces: every triangle of the tessellation is
        // wound as the base quad's corners 0, 1 and 3 are (the grid's first
        // index runs from corner 0 to 1, its second from 0 to 3, and each of
        // the two triangles a square is cut into turns the same way in
        // those), so a triangle's normal on the front is its cross product
        // times the sign that takes the corners' cross product to the front.
        Vec3 turnedFront = Turn(Float(displacement.Front));
        ReadOnlySpan<Vec3> c = surface.CornerPoints;
        double facing = Dot(Cross(Minus(c[1], c[0]), Minus(c[3], c[0])), turnedFront);
        if (facing == 0)
        {
            return triangles;
        }

        double sign = facing > 0 ? 1 : -1;
        (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)? box = cut.Cell is { } cell && placement is not null
            ? (0, 0, cell.MinZ, cut.CellSize, cut.CellSize, cell.MaxZ)
            : cut.Cell;
        ReadOnlySpan<Vec3> vertices = surface.Vertices;
        ReadOnlySpan<int> indices = surface.TriangleIndices;
        for (int t = 0; t < surface.TriangleCount; t++)
        {
            if (t < displacement.Removed.Count && displacement.Removed[t])
            {
                continue;
            }

            Vec3 a = vertices[indices[3 * t]], b = vertices[indices[(3 * t) + 1]], d = vertices[indices[(3 * t) + 2]];
            (double X, double Y, double Z) normal = Cross(Minus(b, a), Minus(d, a));
            double length = Math.Sqrt(Dot(normal, normal));
            if (length == 0 || sign * normal.Z < WalkableNormalZ * length)
            {
                continue;
            }

            List<(double X, double Y, double Z)> triangle = [(a.X, a.Y, a.Z), (b.X, b.Y, b.Z), (d.X, d.Y, d.Z)];
            if (box is { } cellBox)
            {
                if (triangle.Any(p => p.Z < cellBox.MinZ - BoxEpsilon || p.Z > cellBox.MaxZ + BoxEpsilon))
                {
                    continue;
                }

                triangle = Clip(triangle, 0, cellBox.MinX, keepAbove: true);
                triangle = Clip(triangle, 0, cellBox.MaxX, keepAbove: false);
                triangle = Clip(triangle, 1, cellBox.MinY, keepAbove: true);
                triangle = Clip(triangle, 1, cellBox.MaxY, keepAbove: false);
                if (triangle.Count < 3)
                {
                    continue;
                }
            }

            triangles.Add(triangle);
        }

        return triangles;

        (double X, double Y, double Z) Local((double X, double Y, double Z) p) => placement is { } at ? ToLocal(p, at, cut.CellSize) : p;

        // A direction taken back through the placement's turn: the turn of
        // ToLocal without its move, exact (a swap and a sign).
        Vec3 Turn(Vec3 v) => placement is not { } at ? v : at.NormalizedRotation switch
        {
            0 => v,
            1 => new Vec3(v.Y, -v.X, v.Z),
            2 => new Vec3(-v.X, -v.Y, v.Z),
            _ => new Vec3(-v.Y, v.X, v.Z),
        };
    }

    private static Vec3 Float((double X, double Y, double Z) p) => new((float)p.X, (float)p.Y, (float)p.Z);

    private static (double X, double Y, double Z) Minus(Vec3 a, Vec3 b) => ((double)a.X - b.X, (double)a.Y - b.Y, (double)a.Z - b.Z);

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        ((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    private static double Dot((double X, double Y, double Z) a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    /// <summary>A polygon in the room's frame through the plugs and the snap, into the pieces for the union.</summary>
    private static void AddPieces(List<(double X, double Y, double Z)> polygon, MapCut cut, List<MapFacePolygon> placed)
    {
        List<List<(double X, double Y, double Z)>> pieces = [polygon];
        foreach (Box plug in cut.Plugs)
        {
            pieces = [.. pieces.SelectMany(piece => Outside(piece, plug))];
        }

        foreach (List<(double X, double Y, double Z)> piece in pieces)
        {
            if (Snap(piece) is { } snapped)
            {
                placed.Add(snapped);
            }
        }
    }

    /// <summary>A world point in its placement's room frame: the placement's turn and move undone, exactly.</summary>
    /// <param name="p">The world point.</param>
    /// <param name="placement">The placement.</param>
    /// <param name="cellSize">The level's cell size.</param>
    /// <returns>The room-local point.</returns>
    public static (double X, double Y, double Z) ToLocal((double X, double Y, double Z) p, RoomPlacement placement, float cellSize)
    {
        double c = cellSize;
        double tx = (double)placement.CellX * c;
        double ty = (double)placement.CellY * c;
        return placement.NormalizedRotation switch
        {
            0 => (p.X - tx, p.Y - ty, p.Z),
            1 => (p.Y - ty, tx + c - p.X, p.Z),
            2 => (tx + c - p.X, ty + c - p.Y, p.Z),
            _ => (ty + c - p.Y, p.X - tx, p.Z),
        };
    }

    private static void Add(BspData bsp, DModel model, (double X, double Y, double Z) origin, bool world, List<MapFaceSource> into)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<Vec3> vertices = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        int end = Math.Min(faces.Length, model.FirstFace + model.NumFaces);
        for (int f = Math.Max(0, model.FirstFace); f < end; f++)
        {
            DFace face = faces[f];
            if (face.TexInfo < 0 || face.TexInfo >= texInfos.Length || (texInfos[face.TexInfo].Flags & NotFloor) != 0
                || face.PlaneNum >= planes.Length || face.NumEdges < 3)
            {
                continue;
            }

            // A face's plane is its own: the compiler writes the face on the
            // plane of its side's orientation (its side byte only says which
            // of the plane's pair that is), so the normal is the plane's. A
            // displacement's base face is judged by its surface instead.
            Vec3 normal = planes[face.PlaneNum].Normal;
            if (face.DispInfo < 0 && normal.Z < WalkableNormalZ)
            {
                continue;
            }

            List<(double, double, double)> points = new(face.NumEdges);
            for (int e = 0; e < face.NumEdges; e++)
            {
                int se = surfEdges[face.FirstEdge + e];
                Vec3 v = vertices[se >= 0 ? edges[se].V[0] : edges[-se].V[1]];
                points.Add((v.X + origin.X, v.Y + origin.Y, v.Z + origin.Z));
            }

            if (face.DispInfo < 0)
            {
                into.Add(new MapFaceSource(points, world));
            }
            else if (DisplacementOf(bsp, face.DispInfo, points, origin, normal) is { } displacement)
            {
                into.Add(new MapFaceSource(points, world, displacement));
            }
        }
    }

    /// <summary>
    /// The recipe of a face's displacement, or null when the compile does not
    /// hold a whole one (no record, a base face that is not four points, a
    /// power outside 2 to 4, a vertex run past its lump): such a face is
    /// neither floor nor a surface the map can build.
    /// </summary>
    private static MapDisplacementSource? DisplacementOf(
        BspData bsp, int index, List<(double X, double Y, double Z)> corners, (double X, double Y, double Z) origin, Vec3 front)
    {
        ReadOnlySpan<DispInfo> infos = BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]);
        if (index >= infos.Length || corners.Count != 4)
        {
            return null;
        }

        DispInfo info = infos[index];
        ReadOnlySpan<DispVert> verts = BspStructView.As<DispVert>(bsp[BspLump.DispVerts]);
        if (info.Power is < 2 or > 4 || info.DispVertStart < 0 || info.DispVertStart + info.NumVerts() > verts.Length)
        {
            return null;
        }

        // The tags say which triangles the author removed; a compile without
        // the lump (none that vbsp writes) removes none.
        ReadOnlySpan<DispTri> tris = BspStructView.As<DispTri>(bsp[BspLump.DispTris]);
        bool[] removed = new bool[info.NumTris()];
        if (info.DispTriStart >= 0 && info.DispTriStart + removed.Length <= tris.Length)
        {
            for (int t = 0; t < removed.Length; t++)
            {
                removed[t] = (tris[info.DispTriStart + t].Tags & (ushort)DispTriTags.Remove) != 0;
            }
        }

        Vec3 start = info.StartPosition;
        return new MapDisplacementSource(
            corners,
            (start.X + origin.X, start.Y + origin.Y, start.Z + origin.Z),
            info.Power,
            verts.Slice(info.DispVertStart, info.NumVerts()).ToArray(),
            removed,
            (front.X, front.Y, front.Z));
    }

    private static (double X, double Y, double Z) OriginOf(BspEntity entity)
    {
        string[] parts = (entity.Get("origin") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)
            ? ((float)x, (float)y, (float)z)
            : (0, 0, 0);
    }

    private static bool Inside((double X, double Y, double Z) p, Box box) =>
        p.X >= box.Mins.X - BoxEpsilon && p.X <= box.Maxs.X + BoxEpsilon
        && p.Y >= box.Mins.Y - BoxEpsilon && p.Y <= box.Maxs.Y + BoxEpsilon
        && p.Z >= box.Mins.Z - BoxEpsilon && p.Z <= box.Maxs.Z + BoxEpsilon;

    /// <summary>The parts of a convex polygon outside a box's x-y rectangle, when its z range meets the box's.</summary>
    private static IEnumerable<List<(double X, double Y, double Z)>> Outside(List<(double X, double Y, double Z)> polygon, Box box)
    {
        double low = polygon.Min(p => p.Z), high = polygon.Max(p => p.Z);
        if (high < box.Mins.Z - BoxEpsilon || low > box.Maxs.Z + BoxEpsilon)
        {
            return [polygon];
        }

        List<List<(double, double, double)>> outside = [];
        List<(double X, double Y, double Z)> rest = polygon;
        (int Axis, double Value, bool Above)[] walls =
        [
            (0, box.Mins.X, false), (0, box.Maxs.X, true), (1, box.Mins.Y, false), (1, box.Maxs.Y, true),
        ];
        foreach ((int axis, double value, bool above) in walls)
        {
            outside.Add(Clip(rest, axis, value, above));
            rest = Clip(rest, axis, value, !above);
        }

        return outside.Where(p => p.Count >= 3);
    }

    /// <summary>A convex polygon clipped to one side of an axis line (Sutherland and Hodgman), in doubles.</summary>
    private static List<(double X, double Y, double Z)> Clip(List<(double X, double Y, double Z)> polygon, int axis, double value, bool keepAbove)
    {
        List<(double X, double Y, double Z)> kept = [];
        for (int i = 0; i < polygon.Count; i++)
        {
            (double X, double Y, double Z) a = polygon[i];
            (double X, double Y, double Z) b = polygon[(i + 1) % polygon.Count];
            double da = (axis == 0 ? a.X : a.Y) - value;
            double db = (axis == 0 ? b.X : b.Y) - value;
            if (!keepAbove)
            {
                (da, db) = (-da, -db);
            }

            if (da >= 0)
            {
                kept.Add(a);
            }

            if ((da > 0 && db < 0) || (da < 0 && db > 0))
            {
                double t = da / (da - db);
                (double X, double Y, double Z) at = (a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));
                kept.Add(axis == 0 ? (value, at.Y, at.Z) : (at.X, value, at.Z));
            }
        }

        return kept;
    }

    /// <summary>A piece counter-clockwise from above, snapped to whole units; null when it covers no area.</summary>
    private static MapFacePolygon? Snap(List<(double X, double Y, double Z)> piece)
    {
        if (piece.Count < 3)
        {
            return null;
        }

        double area = 0;
        for (int i = 0, j = piece.Count - 1; i < piece.Count; j = i++)
        {
            area += (piece[j].X * piece[i].Y) - (piece[i].X * piece[j].Y);
        }

        if (Math.Abs(area) < 1e-6)
        {
            return null;
        }

        IEnumerable<(double X, double Y, double Z)> ordered = area > 0 ? piece : Enumerable.Reverse(piece);
        List<MapPoint> points = [.. ordered.Select(p => new MapPoint(Whole(p.X), Whole(p.Y)))];
        return new MapFacePolygon(points, Whole(piece.Min(p => p.Z)), Whole(piece.Max(p => p.Z)));
    }

    /// <summary>The nearest whole unit, halves up: <c>floor(v + 0.5)</c>, exact.</summary>
    private static int Whole(double value) => (int)Math.Clamp(Math.Floor(value + 0.5), int.MinValue, int.MaxValue);
}
