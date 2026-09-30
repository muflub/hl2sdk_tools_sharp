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
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Text;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// What a link will write as its level's map: the map, once the linked
/// <c>.bsp</c>'s checksum is known, or why there is none.
/// </summary>
/// <remarks>
/// Planned before the map is written (the rooms' map sections are read with
/// the rooms), built after, since the file binds itself to the written
/// map's checksum. Nothing is kept: a plan is made per link and dropped with it.
/// </remarks>
public sealed class LevelMapPlan
{
    private readonly Func<uint, Map2dLevel>? _build;

    internal LevelMapPlan(string? warning, Func<uint, Map2dLevel>? build)
    {
        Warning = warning;
        _build = build;
    }

    /// <summary>Why the level links without a map (a room packed before the map), or null.</summary>
    public string? Warning { get; }

    /// <summary>Whether the link writes a map.</summary>
    public bool WritesMap => _build is not null;

    /// <summary>The level's map, bound to the written map.</summary>
    /// <param name="mapChecksum">The written <c>.bsp</c>'s checksum (<see cref="BspMapChecksum"/>).</param>
    /// <returns>The map.</returns>
    /// <exception cref="InvalidOperationException">The plan writes no map (<see cref="Warning"/> says why).</exception>
    public Map2dLevel Build(uint mapChecksum) =>
        (_build ?? throw new InvalidOperationException("the level links without a map: " + Warning))(mapChecksum);
}

/// <summary>
/// The level map overlay (the rooms design, section 18): a level's
/// <c>.map2d</c>, from its rooms' map sections at link, or from any
/// compiled map by the same face rule (<c>ssmap map2d</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The link</b> (<see cref="Plan"/>) does no geometry: each placement's
/// polygons, doors and markers come from its room's <c>MAPV</c> section
/// (<see cref="RoomMapView"/>), room-local, and are turned and moved to its
/// cell on whole units, exactly; the doors take their state from the
/// joints; the automatic markers (the level spawn, the arrivals, the
/// transition exits) come from the rooms' transition data by the link's own
/// rule (<see cref="LevelTransitionPlan"/>). The cost is a copy and a turn.
/// </para>
/// <para>
/// <b>A compiled map</b> (<see cref="FromCompile(BspData, uint, LevelGrid, IReadOnlyList{VmfDocument})"/>)
/// gives the same map by the same steps: its walkable faces cut into the
/// level's cells, each placement's taken into its room's frame and unioned
/// there, as the pack unions a room's; the doors and markers from the
/// level's libraries, as the pack reads them. The flattened level's compile
/// therefore gives the linked map's file byte for byte but for the checksum,
/// which binds each file to its own <c>.bsp</c> (a fact holds the two
/// equal). Without a level file (<see cref="FromCompile(BspData, uint)"/>) a
/// hand-built map gets its floors unioned whole, in its own frame, with no
/// placements or doors, and markers from its entities.
/// </para>
/// <para>
/// <b>Order.</b> Placements in link order; per placement its polygons as
/// its section orders them (each outer ring followed by its holes), its
/// doors in socket order; the automatic markers first (the spawn, then per
/// transition room its arrival and its exit), then each placement's own
/// markers in document order. Every part is a function of the level and its
/// rooms, so the file is too.
/// </para>
/// </remarks>
public static class LevelMapBuilder
{
    /// <summary>The map a link writes for a level of loaded rooms, or why it writes none.</summary>
    /// <param name="layout">The level, its joints derived.</param>
    /// <param name="columns">The level grid's columns.</param>
    /// <param name="rows">The level grid's rows.</param>
    /// <param name="rooms">Each placed room, by name, as the pack loaded it.</param>
    /// <returns>The plan: a map to build once the <c>.bsp</c> is written, or a warning naming the rooms without a map section.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static LevelMapPlan Plan(LevelLayout layout, int columns, int rows, Func<string, RoomObject> rooms)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rooms);
        string[] missing = [.. layout.Rooms.Select(r => r.Placement.Room).Distinct(StringComparer.Ordinal)
            .Where(room => rooms(room).MapViewOfCompile is null).Order(StringComparer.Ordinal)];
        if (missing.Length > 0)
        {
            return new LevelMapPlan(
                $"the room pack holds no level map for {string.Join(", ", missing.Select(m => $"\"{m}\""))};"
                + " the level is linked without a .map2d (compile the library with a build that writes the map)",
                null);
        }

        RoomObject[] placed = [.. layout.Rooms.Select(r => rooms(r.Placement.Room))];
        return new LevelMapPlan(null, checksum => Assemble(
            layout, columns, rows,
            [.. placed.Select(r => r.Definition)],
            [.. placed.Select(r => r.MapViewOfCompile!)],
            [.. placed.Select(r => r.TransitOfCompile)],
            checksum));
    }

    /// <summary>
    /// The map of a compiled map without a level file: its walkable faces
    /// unioned whole in its own frame, and markers from its entities.
    /// </summary>
    /// <param name="bsp">The compiled map.</param>
    /// <param name="mapChecksum">Its checksum (<see cref="BspMapChecksum"/>), which the file records.</param>
    /// <returns>The map: no placements, no doors.</returns>
    /// <remarks>
    /// The markers: an <c>info_player_start</c> is a <c>spawn</c>, and an
    /// entity with a <c>map_marker</c> that is a kind is a marker of that kind
    /// with its <c>map_label</c> when that is a label; anything else is not
    /// on the map. A hand-built map has no pack to be refused by, so an
    /// unreadable marker is left out rather than failing the map.
    /// </remarks>
    public static Map2dLevel FromCompile(BspData bsp, uint mapChecksum)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        MapCut whole = new(null, null, 0, [], []);
        ImmutableArray<Map2dRing>.Builder rings = ImmutableArray.CreateBuilder<Map2dRing>();
        foreach (MapPolygon polygon in MapPolygonUnion.Union(RoomMapFaces.Place(RoomMapFaces.Walkable(bsp), whole)))
        {
            AddRings(rings, Map2dFormat.NoPlacement, polygon, p => new Map2dPoint(p.X, p.Y));
        }

        ImmutableArray<Map2dMarker>.Builder markers = ImmutableArray.CreateBuilder<Map2dMarker>();
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            string? kind = entity.ClassName == LevelTransitionPlan.PlayerStartClass
                ? LevelMap.SpawnKind
                : entity.Get(LevelMap.MarkerKey) is { } k && LevelMap.IsKind(k) ? k : null;
            if (kind is null || Vector(entity.Get("origin")) is not { } origin)
            {
                continue;
            }

            string label = entity.Get(LevelMap.LabelKey) is { } l && LevelMap.IsLabel(l) ? l : string.Empty;
            float yaw = Vector(entity.Get("angles")) is { } angles ? LevelLinker.TurnYaw(angles.Y, 0) : 0;
            markers.Add(new Map2dMarker(kind, label, Map2dFormat.NoPlacement, origin.X, origin.Y, origin.Z, yaw));
        }

        return new Map2dLevel { MapChecksum = mapChecksum, Rings = rings.ToImmutable(), Markers = markers.ToImmutable() };
    }

    /// <summary>
    /// The map of a compiled level, cut into the level's cells: what the link
    /// writes for the same level, from the flattened level's compile.
    /// </summary>
    /// <param name="bsp">The compiled map (the flattened level's, or any map of the level's layout).</param>
    /// <param name="mapChecksum">Its checksum, which the file records.</param>
    /// <param name="level">The level file, as read.</param>
    /// <param name="libraries">Its library VMFs, in the level's order.</param>
    /// <returns>The map.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="RoomLibraryException">A library cannot be split into rooms.</exception>
    /// <exception cref="LevelFileException">A cell or alias does not resolve.</exception>
    /// <exception cref="RoomLintException">A marker is malformed, or the level breaks the transition rule.</exception>
    /// <exception cref="ArgumentException">The level places no room, or two rooms in one cell.</exception>
    public static Map2dLevel FromCompile(BspData bsp, uint mapChecksum, LevelGrid level, IReadOnlyList<VmfDocument> libraries)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(libraries);
        (level, _, Dictionary<string, LibraryRoom> byName, _) = LevelFlattener.RoomsOf(level, libraries);
        RoomDefinition first = level.Placed.Select(p => byName.GetValueOrDefault(p.Cell.Room)?.Definition).FirstOrDefault(d => d is not null)
            ?? throw new ArgumentException($"level {level.Name} places no room of its libraries.", nameof(level));
        LevelLayout layout = level.ToLayout(name => byName.TryGetValue(name, out LibraryRoom? room) ? room.Definition : null, first.CellSize, first.Kit);
        layout.Validate();

        // The faces by the cells their boxes reach, so a placement reads its
        // own; a displacement by the one cell that owns it (a displacement
        // is never merged across rooms, and is its room's alone).
        List<MapFaceSource> faces = RoomMapFaces.Walkable(bsp);
        double c = layout.CellSize;
        Dictionary<(long, long), List<MapFaceSource>> byCell = [];
        foreach (MapFaceSource face in faces)
        {
            if (face.Displacement is not null)
            {
                (long, long) owner = RoomMapFaces.OwnerCell(face, c);
                if (!byCell.TryGetValue(owner, out List<MapFaceSource>? own))
                {
                    byCell[owner] = own = [];
                }

                own.Add(face);
                continue;
            }

            long x0 = (long)Math.Floor(face.Points.Min(p => p.X) / c), x1 = (long)Math.Floor(face.Points.Max(p => p.X) / c);
            long y0 = (long)Math.Floor(face.Points.Min(p => p.Y) / c), y1 = (long)Math.Floor(face.Points.Max(p => p.Y) / c);
            for (long x = x0; x <= x1 && x - x0 < 4096; x++)
            {
                for (long y = y0; y <= y1 && y - y0 < 4096; y++)
                {
                    if (!byCell.TryGetValue((x, y), out List<MapFaceSource>? list))
                    {
                        byCell[(x, y)] = list = [];
                    }

                    list.Add(face);
                }
            }
        }

        List<RoomDefinition> definitions = [];
        List<RoomMapView> views = [];
        List<RoomTransit?> transits = [];
        Dictionary<string, (IReadOnlyList<RoomMapMarker> Markers, RoomTransit? Transit)> perRoom = new(StringComparer.Ordinal);
        foreach (RoomInstance instance in layout.Rooms)
        {
            LibraryRoom room = byName[instance.Placement.Room];
            if (!perRoom.TryGetValue(instance.Placement.Room, out (IReadOnlyList<RoomMapMarker> Markers, RoomTransit? Transit) read))
            {
                perRoom[instance.Placement.Room] = read =
                    (RoomMapView.MarkersOf(room.Document), RoomTransit.FromVmf(room.Definition, room.Role, room.Document));
            }

            MapCut cut = RoomMapView.CutOf(room.Definition, room.Document, instance.Placement);
            IReadOnlyList<MapPolygon> polygons = MapPolygonUnion.Union(RoomMapFaces.Place(
                byCell.GetValueOrDefault((instance.Placement.CellX, instance.Placement.CellY)) ?? [], cut));
            definitions.Add(room.Definition);
            views.Add(new RoomMapView(room.MapLabel, polygons, RoomMapView.DoorsOf(room.Definition), read.Markers));
            transits.Add(read.Transit);
        }

        return Assemble(layout, level.Columns, level.Rows, definitions, views, transits, mapChecksum);
    }

    /// <summary>The level's map from each placement's room map, turned and moved to its cell.</summary>
    internal static Map2dLevel Assemble(
        LevelLayout layout,
        int columns,
        int rows,
        IReadOnlyList<RoomDefinition> definitions,
        IReadOnlyList<RoomMapView> views,
        IReadOnlyList<RoomTransit?> transits,
        uint mapChecksum)
    {
        float cellSize = layout.CellSize;
        if (!RoomMapView.IsWhole(cellSize))
        {
            throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                $"level {layout.Name}: the cell size {cellSize} is not a whole number of units, which the level map's polygons need."));
        }

        Dictionary<(int, int), int> byCell = [];
        for (int p = 0; p < layout.Rooms.Count; p++)
        {
            byCell[(layout.Rooms[p].Placement.CellX, layout.Rooms[p].Placement.CellY)] = p;
        }

        ImmutableArray<Map2dRoom>.Builder placements = ImmutableArray.CreateBuilder<Map2dRoom>(layout.Rooms.Count);
        ImmutableArray<Map2dRing>.Builder rings = ImmutableArray.CreateBuilder<Map2dRing>();
        ImmutableArray<Map2dDoor>.Builder doors = ImmutableArray.CreateBuilder<Map2dDoor>();
        ImmutableArray<Map2dMarker>.Builder markers = ImmutableArray.CreateBuilder<Map2dMarker>();

        // The automatic markers first: the spawn, then each transition room's arrival and exit.
        if (LevelTransitionPlan.Make(layout, transits, name => definitions[Index(layout, name)], modEntities: true) is { } plan)
        {
            RoomPlacement spawnAt = layout.Rooms[plan.SpawnPlacement].Placement;
            if (plan.SpawnPoints.Count > 0)
            {
                markers.Add(Marker(LevelMap.SpawnKind, string.Empty, plan.SpawnPlacement, spawnAt, cellSize, plan.SpawnPoints[0].Origin, plan.SpawnPoints[0].Yaw));
            }

            for (int p = 0; p < layout.Rooms.Count; p++)
            {
                if (plan.Placements[p] is not { } transition || transits[p] is not { } transit)
                {
                    continue;
                }

                RoomPlacement at = layout.Rooms[p].Placement;
                if (transit.Arrival is { } arrival)
                {
                    markers.Add(Marker(LevelMap.ArrivalKind, transition.Map, p, at, cellSize, arrival.Origin, arrival.Yaw));
                }

                // An exit is a place, not a facing: yaw 0 whatever the turn.
                string exit = transition.Direction == TransitionDirection.Up ? LevelMap.ExitUpKind : LevelMap.ExitDownKind;
                Vec3 centre = new RoomTransform(at, cellSize).Apply(transit.VolumeCentre);
                markers.Add(new Map2dMarker(exit, transition.Map, p, centre.X, centre.Y, centre.Z, 0));
            }
        }

        for (int p = 0; p < layout.Rooms.Count; p++)
        {
            RoomInstance instance = layout.Rooms[p];
            RoomPlacement placement = instance.Placement;
            RoomDefinition definition = definitions[p];
            RoomMapView view = views[p];
            placements.Add(new Map2dRoom(placement.CellX, placement.CellY, placement.NormalizedRotation, definition.Height, placement.Room, view.Label));

            foreach (MapPolygon polygon in view.Polygons)
            {
                AddRings(rings, p, polygon, point => Turn(point, placement, (long)cellSize));
            }

            RoomTransform transform = new(placement, cellSize);
            foreach (RoomMapDoor door in view.Doors)
            {
                RoomSocket socket = definition.Sockets[door.Socket];
                int neighbour = Map2dFormat.NoPlacement;
                bool open = instance.Joints.Any(j => string.Equals(j.Socket, socket.Name, StringComparison.Ordinal));
                if (open)
                {
                    (int axis, int sign) = transform.WorldNormal(socket.Facing);
                    neighbour = byCell.GetValueOrDefault(
                        (placement.CellX + (axis == 0 ? sign : 0), placement.CellY + (axis == 1 ? sign : 0)), Map2dFormat.NoPlacement);
                }

                Vec3 a = transform.Apply(new Vec3(door.X0, door.Y0, door.ZLow));
                Vec3 b = transform.Apply(new Vec3(door.X1, door.Y1, door.ZHigh));
                doors.Add(new Map2dDoor(p, socket.Name, open, neighbour, a.X, a.Y, b.X, b.Y, a.Z, b.Z));
            }
        }

        for (int p = 0; p < layout.Rooms.Count; p++)
        {
            foreach (RoomMapMarker marker in views[p].Markers)
            {
                markers.Add(Marker(marker.Kind, marker.Label, p, layout.Rooms[p].Placement, cellSize, marker.Origin, marker.Yaw));
            }
        }

        return new Map2dLevel
        {
            MapChecksum = mapChecksum,
            CellSize = cellSize,
            Columns = columns,
            Rows = rows,
            Rooms = placements.MoveToImmutable(),
            Rings = rings.ToImmutable(),
            Doors = doors.ToImmutable(),
            Markers = markers.ToImmutable(),
        };
    }

    /// <summary>A room-local point turned and moved to its placement's cell, on whole units: <see cref="RoomTransform.Apply"/> in integers.</summary>
    internal static Map2dPoint Turn(MapPoint p, RoomPlacement placement, long cell)
    {
        long tx = placement.CellX * cell, ty = placement.CellY * cell;
        (long x, long y) = placement.NormalizedRotation switch
        {
            0 => (p.X + tx, p.Y + ty),
            1 => (-p.Y + tx + cell, p.X + ty),
            2 => (-p.X + tx + cell, -p.Y + ty + cell),
            _ => (p.Y + tx, -p.X + ty + cell),
        };
        return new Map2dPoint(checked((int)x), checked((int)y));
    }

    private static void AddRings(ImmutableArray<Map2dRing>.Builder into, int placement, MapPolygon polygon, Func<MapPoint, Map2dPoint> turn)
    {
        into.Add(new Map2dRing(placement, polygon.ZLow, polygon.ZHigh, false, [.. polygon.Outer.Select(turn)]));
        foreach (IReadOnlyList<MapPoint> hole in polygon.Holes)
        {
            into.Add(new Map2dRing(placement, polygon.ZLow, polygon.ZHigh, true, [.. hole.Select(turn)]));
        }
    }

    private static Map2dMarker Marker(string kind, string label, int p, RoomPlacement placement, float cellSize, Vec3 origin, float yaw)
    {
        Vec3 at = new RoomTransform(placement, cellSize).Apply(origin);
        return new Map2dMarker(kind, label, p, at.X, at.Y, at.Z, LevelLinker.TurnYaw(yaw, placement.NormalizedRotation));
    }

    private static int Index(LevelLayout layout, string room)
    {
        for (int p = 0; p < layout.Rooms.Count; p++)
        {
            if (layout.Rooms[p].Placement.Room == room)
            {
                return p;
            }
        }

        throw new ArgumentException($"the level places no room \"{room}\".", nameof(room));
    }

    private static Vec3? Vector(string? text)
    {
        string[] parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && float.IsFinite(x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && float.IsFinite(y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) && float.IsFinite(z)
            ? new Vec3(x, y, z)
            : null;
    }
}
