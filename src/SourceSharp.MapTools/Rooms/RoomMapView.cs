//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>A room's door on its map, room-local: the segment across a socket's opening on the cell face.</summary>
/// <param name="Socket">The socket's index in the room's definition.</param>
/// <param name="X0">One end, x.</param>
/// <param name="Y0">One end, y.</param>
/// <param name="X1">The other end, x.</param>
/// <param name="Y1">The other end, y.</param>
/// <param name="ZLow">The opening's bottom.</param>
/// <param name="ZHigh">The opening's top.</param>
internal readonly record struct RoomMapDoor(int Socket, float X0, float Y0, float X1, float Y1, float ZLow, float ZHigh);

/// <summary>An author's marker, room-local: an <c>info_poi</c> with a <c>map_marker</c>.</summary>
/// <param name="Kind">Its kind (<see cref="LevelMap.IsKind"/>).</param>
/// <param name="Label">Its <c>map_label</c>, or empty.</param>
/// <param name="Origin">Where it stands, room-local.</param>
/// <param name="Yaw">Its facing, 0 to 360; 0 without angles.</param>
internal readonly record struct RoomMapMarker(string Kind, string Label, Vec3 Origin, float Yaw);

/// <summary>
/// A room's part of its level's map (the rooms design, 18.3): its floors as
/// polygons, its doors by socket and its authors' markers, all room-local and
/// unturned, and its label; the <c>MAPV</c> pack section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Made at pack time</b> (<see cref="Build"/>, decision D1): the walkable
/// faces of the room's compile (<see cref="RoomMapFaces"/>) cut to its cell,
/// its plug boxes' floor left to the doors, snapped and unioned
/// (<see cref="MapPolygonUnion"/>). The doors are the kit's openings, a
/// segment across each on the cell face; the markers are the room's
/// <c>info_poi</c> entities with a <c>map_marker</c>, read from its VMF
/// before the compile takes the points out.
/// </para>
/// <para>
/// <b>Stored once, not per turn.</b> A quarter turn and a whole-cell move are
/// exact on whole units, and the link turns a few hundred points per room in
/// less time than reading three more copies would take (D16's measure; the
/// PR's landed note has the numbers), so the section holds the room's own
/// frame and the link turns it (<see cref="LevelMapBuilder"/>).
/// </para>
/// <para>
/// <b>The bytes</b> have the link sections' framing
/// (<see cref="RoomLinkSections"/>: codec byte, always none; decoded length;
/// <c>int32</c> revision <see cref="Revision"/>), then, as the other room
/// sections write them (big-endian <c>int32</c> counts and scalars,
/// little-endian points and floats): the rotation count, always 1; the
/// payload's byte length; and the payload: the label (length and UTF-8);
/// the polygon count, and per polygon its band (two <c>int32</c>s), its ring
/// count (the outer ring first) and per ring its point count and points
/// (<c>int32</c> x and y); the door count, which is the socket count, and per
/// door its socket index and six floats (the two ends' x and y, then the
/// opening's bottom and top); the marker count, and per marker its kind, its
/// label and four floats (x, y, z, yaw).
/// </para>
/// <para>
/// <b>Optional, as the other link sections are.</b> A pack without it
/// (packed before the map) links without a <c>.map2d</c> and says so once; a
/// section of another revision reads as absent the same way. A room with no
/// walkable face stores an empty section and is warned about at pack time
/// (<see cref="EmptyWarning"/>), since a player cannot stand in it. A room
/// whose cell size is not a whole number of units stores none: its turned
/// polygons would not be whole.
/// </para>
/// </remarks>
internal sealed class RoomMapView
{
    /// <summary>The tag of the section.</summary>
    public const string SectionTag = "MAPV";

    /// <summary>The revision this build writes and reads.</summary>
    public const int Revision = RoomLinkSections.Revision;

    private readonly BspData? _bsp;

    internal RoomMapView(
        string label, IReadOnlyList<MapPolygon> polygons, IReadOnlyList<RoomMapDoor> doors, IReadOnlyList<RoomMapMarker> markers, BspData? bsp = null)
    {
        Label = label;
        Polygons = polygons;
        Doors = doors;
        Markers = markers;
        _bsp = bsp;
    }

    /// <summary>The room's <c>map_label</c>, or empty.</summary>
    public string Label { get; }

    /// <summary>The floors, room-local, as <see cref="MapPolygonUnion.Union"/> orders them.</summary>
    public IReadOnlyList<MapPolygon> Polygons { get; }

    /// <summary>The doors, one per socket in the definition's order.</summary>
    public IReadOnlyList<RoomMapDoor> Doors { get; }

    /// <summary>The authors' markers, in document order.</summary>
    public IReadOnlyList<RoomMapMarker> Markers { get; }

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The same data bound to a compile.</summary>
    internal RoomMapView For(BspData bsp) => new(Label, Polygons, Doors, Markers, bsp);

    /// <summary>The same floors and doors, still bound to their compile, with the library's markers and label.</summary>
    internal RoomMapView With(IReadOnlyList<RoomMapMarker> markers, string label) => new(label, Polygons, Doors, markers, _bsp);

    /// <summary>The pack-time warning for a room with no floor, or null.</summary>
    /// <param name="room">The room's name.</param>
    /// <returns>The warning's text, or null when the room has floor.</returns>
    public string? EmptyWarning(string room) => Polygons.Count > 0
        ? null
        : $"room \"{room}\" has no walkable floor (no drawn face whose normal points up at least {RoomMapFaces.WalkableNormalZ.ToString(CultureInfo.InvariantCulture)});"
            + " a player cannot stand in it, and the level map shows it empty.";

    /// <summary>
    /// A room's map, from its compile; null for a room whose cell size is not
    /// a whole number of units.
    /// </summary>
    /// <param name="definition">The room.</param>
    /// <param name="bsp">Its compile.</param>
    /// <param name="document">Its VMF, room-local: where its socket furniture brushes are read from.</param>
    /// <param name="markers">Its markers (<see cref="MarkersOf"/>, read before the compile).</param>
    /// <param name="label">Its <c>info_room</c>'s <c>map_label</c>, or empty.</param>
    /// <returns>The map, bound to the compile.</returns>
    public static RoomMapView? Build(RoomDefinition definition, BspData bsp, VmfDocument document, IReadOnlyList<RoomMapMarker> markers, string label)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(markers);
        if (!IsWhole(definition.CellSize))
        {
            return null;
        }

        IReadOnlyList<MapPolygon> polygons = MapPolygonUnion.Union(RoomMapFaces.Place(RoomMapFaces.Walkable(bsp), CutOf(definition, document, null)));
        return new RoomMapView(label, polygons, DoorsOf(definition), markers, bsp);
    }

    /// <summary>Whether a cell size is a whole number of units, which the map's turned polygons need.</summary>
    internal static bool IsWhole(float cellSize) => float.IsFinite(cellSize) && cellSize == MathF.Floor(cellSize) && cellSize <= MapPolygonUnion.MaxCoordinate;

    /// <summary>
    /// Where a room's faces are cut: its cell as tall as the room, its plug
    /// boxes and its furniture brushes' boxes; placed in a level's frame when
    /// a placement is given (<c>ssmap map2d</c> of a flattened compile), in
    /// the room's own otherwise (the pack).
    /// </summary>
    internal static MapCut CutOf(RoomDefinition definition, VmfDocument document, RoomPlacement? placement)
    {
        double c = definition.CellSize;
        double x = placement is { } p ? p.CellX * c : 0;
        double y = placement is { } q ? q.CellY * c : 0;
        return new MapCut(
            (x, y, 0, x + c, y + c, definition.Height),
            placement,
            definition.CellSize,
            RoomCompiler.SealBoxes(definition),
            FurnitureOf(definition, document));
    }

    /// <summary>The boxes of a room's socket furniture brushes (brush entities with <c>room_socket</c> naming a socket), room-local.</summary>
    internal static IReadOnlyList<Box> FurnitureOf(RoomDefinition definition, VmfDocument document)
    {
        List<Box> boxes = [];
        foreach (VmfChunk entity in document.GetChunks(MapFileLoader.EntityChunk))
        {
            if (!BrushEntityDirections.IsBrushEntity(entity) || BrushEntityDirections.IsConsumed(entity.GetValue("classname"))
                || entity.GetValue(RoomStaticProps.SocketKey) is not { } socket
                || !definition.Sockets.Any(s => string.Equals(s.Name, socket, StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (VmfChunk solid in entity.GetChunks(MapFileLoader.SolidChunk))
            {
                boxes.Add(VmfPlacement.Bounds(solid));
            }
        }

        return boxes;
    }

    /// <summary>A room's doors: per socket, the segment across its opening on the cell face, and the opening's z range.</summary>
    internal static IReadOnlyList<RoomMapDoor> DoorsOf(RoomDefinition definition)
    {
        List<RoomMapDoor> doors = [];
        for (int s = 0; s < definition.Sockets.Count; s++)
        {
            RoomSocket socket = definition.Sockets[s];
            Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);
            (float x0, float y0, float x1, float y1) = socket.Facing switch
            {
                RoomFacing.PositiveX => (plug.Maxs.X, plug.Mins.Y, plug.Maxs.X, plug.Maxs.Y),
                RoomFacing.NegativeX => (plug.Mins.X, plug.Mins.Y, plug.Mins.X, plug.Maxs.Y),
                RoomFacing.PositiveY => (plug.Mins.X, plug.Maxs.Y, plug.Maxs.X, plug.Maxs.Y),
                _ => (plug.Mins.X, plug.Mins.Y, plug.Maxs.X, plug.Mins.Y),
            };
            doors.Add(new RoomMapDoor(s, x0, y0, x1, y1, plug.Mins.Z, plug.Maxs.Z));
        }

        return doors;
    }

    /// <summary>A room's markers: its <c>info_poi</c> entities with a <c>map_marker</c>, in document order.</summary>
    /// <param name="document">The room's VMF, points of interest included.</param>
    /// <returns>The markers, room-local.</returns>
    /// <exception cref="RoomLintException">
    /// A kind that is not one (<see cref="LevelMap.IsKind"/>), one of the
    /// linker's own, a label too long or holding a NUL, or a label on a point
    /// that is not a marker; each message names the entity.
    /// </exception>
    internal static IReadOnlyList<RoomMapMarker> MarkersOf(VmfDocument document)
    {
        List<RoomMapMarker> markers = [];
        foreach (VmfChunk entity in document.GetChunks(MapFileLoader.EntityChunk))
        {
            if (!RoomPois.IsPoi(entity))
            {
                continue;
            }

            string who = $"{RoomPois.Entity} {entity.GetValue("id") ?? "?"}";
            string? kind = entity.GetValue(LevelMap.MarkerKey) is { } k ? RoomLibraryVmf.Utf8(k) : null;
            string? label = entity.GetValue(LevelMap.LabelKey) is { } l ? RoomLibraryVmf.Utf8(l) : null;
            if (kind is null)
            {
                if (label is not null)
                {
                    throw new RoomLintException(
                        $"{who} has {LevelMap.LabelKey} but no {LevelMap.MarkerKey}; only a marker is drawn on the level map.");
                }

                continue;
            }

            if (!LevelMap.IsKind(kind))
            {
                throw new RoomLintException(string.Create(CultureInfo.InvariantCulture,
                    $"{who} has {LevelMap.MarkerKey} \"{kind}\"; a marker kind is a lower-case letter, then lower-case letters, digits or underscores, {LevelMap.MaxKindLength} characters at most."));
            }

            if (LevelMap.LinkerKinds.Contains(kind))
            {
                throw new RoomLintException(
                    $"{who} has {LevelMap.MarkerKey} \"{kind}\", which the linker writes itself ({string.Join(", ", LevelMap.LinkerKinds)}).");
            }

            if (label is not null && !LevelMap.IsLabel(label))
            {
                throw new RoomLintException(LabelProblem(who, label));
            }

            AuthoredPoi point = RoomPois.ReadPoi(entity);
            markers.Add(new RoomMapMarker(kind, label ?? string.Empty, point.Origin, point.Yaw));
        }

        return markers;
    }

    /// <summary>The refusal of a label that is not one, for an <c>info_poi</c> or an <c>info_room</c>.</summary>
    internal static string LabelProblem(string who, string label) => string.Create(CultureInfo.InvariantCulture,
        $"{who} has {LevelMap.LabelKey} \"{label}\"; a label is at most {LevelMap.MaxLabelBytes} bytes of UTF-8, without a NUL.");

    /// <summary>The room's section.</summary>
    /// <returns>The section's tag and bytes.</returns>
    public RoomPackSectionData ToSection()
    {
        RoomLinkSections.Writer turn = new();
        turn.String(Label);
        turn.Int(Polygons.Count);
        foreach (MapPolygon polygon in Polygons)
        {
            turn.Int(polygon.ZLow);
            turn.Int(polygon.ZHigh);
            turn.Int(1 + polygon.Holes.Count);
            foreach (IReadOnlyList<MapPoint> ring in (IEnumerable<IReadOnlyList<MapPoint>>)[polygon.Outer, .. polygon.Holes])
            {
                turn.Structs<MapPoint>([.. ring]);
            }
        }

        turn.Int(Doors.Count);
        foreach (RoomMapDoor door in Doors)
        {
            turn.Int(door.Socket);
            turn.Structs<float>([door.X0, door.Y0, door.X1, door.Y1, door.ZLow, door.ZHigh], counted: false);
        }

        turn.Int(Markers.Count);
        foreach (RoomMapMarker marker in Markers)
        {
            turn.String(marker.Kind);
            turn.String(marker.Label);
            turn.Structs<float>([marker.Origin.X, marker.Origin.Y, marker.Origin.Z, marker.Yaw], counted: false);
        }

        byte[] payload = turn.ToArray();
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(1);
        w.Int(payload.Length);
        w.Raw(payload);
        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));
    }

    /// <summary>A room's map from its section; null when it has none, or one of a revision this build does not read.</summary>
    /// <param name="section">The section's bytes, or null.</param>
    /// <param name="definition">The room, whose sockets the doors must match.</param>
    /// <param name="bsp">The room's compile, which the map is bound to; null to read it bound to none.</param>
    /// <returns>The map, or null.</returns>
    /// <exception cref="LinkException">The section is cut short or out of shape; the message names the room and the section.</exception>
    public static RoomMapView? Read(ArraySegment<byte>? section, RoomDefinition definition, BspData? bsp)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Read(section, definition.Name, definition.Sockets.Count, bsp);
    }

    /// <summary>
    /// What <c>ssmap rooms</c> lists of a room's map, from its section, with
    /// no room definition to hold the doors to: null when the room has no
    /// section, or one of a revision this build does not read.
    /// </summary>
    /// <param name="section">The section's bytes, or null.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <returns>The summary, or null.</returns>
    /// <exception cref="LinkException">The section is cut short or out of shape.</exception>
    internal static RoomMapSummary? ReadSummary(ArraySegment<byte>? section, string room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return Read(section, room, null, null) is { } view
            ? new RoomMapSummary(view.Polygons.Count, view.Polygons.Sum(p => 1 + p.Holes.Count), view.Markers.Count, view.Label)
            : null;
    }

    /// <summary>The section read, its doors held to <paramref name="sockets"/> when that is given.</summary>
    private static RoomMapView? Read(ArraySegment<byte>? section, string room, int? sockets, BspData? bsp)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        int rotations = r.Int();
        if (rotations != 1)
        {
            throw r.Mismatch(string.Create(CultureInfo.InvariantCulture, $"{rotations} rotations; a room's map is stored once"));
        }

        int length = r.Count("map bytes");
        int start = r.Position;
        string label = r.String();
        if (!LevelMap.IsLabel(label))
        {
            throw r.Mismatch("a label that is not one");
        }

        int polygonCount = r.Count("polygons");
        List<MapPolygon> polygons = new(polygonCount);
        for (int i = 0; i < polygonCount; i++)
        {
            int zLow = r.Int();
            int zHigh = r.Int();
            int rings = r.Count("rings");
            if (zLow > zHigh || rings < 1)
            {
                throw r.Mismatch(string.Create(CultureInfo.InvariantCulture, $"polygon {i} with band {zLow} to {zHigh} and {rings} rings"));
            }

            List<IReadOnlyList<MapPoint>> read = [];
            for (int k = 0; k < rings; k++)
            {
                int points = r.Count("ring points");
                MapPoint[] ring = r.Structs<MapPoint>("ring points", points, counted: false);
                long area = MapPolygonUnion.Area2(ring);
                if (points < 3 || (k == 0 ? area <= 0 : area >= 0)
                    || ring.Any(p => Math.Abs((long)p.X) > MapPolygonUnion.MaxCoordinate || Math.Abs((long)p.Y) > MapPolygonUnion.MaxCoordinate))
                {
                    throw r.Mismatch(string.Create(CultureInfo.InvariantCulture,
                        $"polygon {i} whose ring {k} is not a {(k == 0 ? "counter-clockwise outer ring" : "clockwise hole")} of whole units"));
                }

                read.Add(ring);
            }

            polygons.Add(new MapPolygon(zLow, zHigh, read[0], read.GetRange(1, read.Count - 1)));
        }

        int doorCount = r.Int();
        if (sockets is { } expected ? doorCount != expected : doorCount < 0)
        {
            throw r.Mismatch(sockets is { } count
                ? string.Create(CultureInfo.InvariantCulture, $"{doorCount} doors; the room has {count} sockets")
                : string.Create(CultureInfo.InvariantCulture, $"{doorCount} doors"));
        }

        List<RoomMapDoor> doors = new(doorCount);
        for (int d = 0; d < doorCount; d++)
        {
            int socket = r.Int();
            float[] f = r.Structs<float>("door", 6, counted: false);
            if (socket != d || f.Any(v => !float.IsFinite(v)))
            {
                throw r.Mismatch(string.Create(CultureInfo.InvariantCulture, $"door {d} for socket {socket}, or with a coordinate that is not a number"));
            }

            doors.Add(new RoomMapDoor(socket, f[0], f[1], f[2], f[3], f[4], f[5]));
        }

        int markerCount = r.Count("markers");
        List<RoomMapMarker> markers = new(markerCount);
        for (int m = 0; m < markerCount; m++)
        {
            string kind = r.String();
            string markerLabel = r.String();
            float[] f = r.Structs<float>("marker", 4, counted: false);
            if (!LevelMap.IsKind(kind) || LevelMap.LinkerKinds.Contains(kind) || !LevelMap.IsLabel(markerLabel) || f.Any(v => !float.IsFinite(v)))
            {
                throw r.Mismatch(string.Create(CultureInfo.InvariantCulture, $"marker {m} of kind \"{kind}\", which is not an author's marker"));
            }

            markers.Add(new RoomMapMarker(kind, markerLabel, new Vec3(f[0], f[1], f[2]), f[3]));
        }

        if (r.Position - start != length)
        {
            throw r.Mismatch(string.Create(CultureInfo.InvariantCulture, $"a map of {r.Position - start} bytes where it records {length}"));
        }

        r.End();
        return new RoomMapView(label, polygons, doors, markers, bsp);
    }
}
