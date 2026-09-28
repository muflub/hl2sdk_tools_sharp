//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One room of a library VMF: what it claims to be, where its cell was in
/// the library, and its own VMF, moved so its cell is <c>[0, cell]³</c>.
/// </summary>
/// <param name="Definition">The room's name, grid, kit and sockets.</param>
/// <param name="Corner">The cell's low corner in the library: where its <c>info_room</c> stands.</param>
/// <param name="Document">
/// The room alone, room-local: the library's <c>versioninfo</c> and
/// worldspawn keys, the world brushes inside the cell, and the entities
/// inside it, all moved by <c>-Corner</c>. The <c>info_room</c> itself is
/// left out; it describes the room and is not part of it.
/// </param>
public sealed record LibraryRoom(RoomDefinition Definition, Vec3 Corner, VmfDocument Document)
{
    /// <summary>
    /// The room's transition role, from its <c>info_room</c>'s
    /// <c>room_role</c> key (<see cref="RoomPois.RoleKey"/>): whether it moves
    /// the player up or down between levels. <see cref="RoomRole.None"/>
    /// without the key.
    /// </summary>
    public RoomRole Role { get; init; }
}

/// <summary>A room library split: its rooms, and what the whole library shares.</summary>
/// <param name="Rooms">The rooms, in the order their <c>info_room</c> entities appear.</param>
/// <param name="LibraryEntities">
/// The library-wide entities (<see cref="RoomLibraryEntities.IsLibraryWide"/>)
/// that stand in the gaps between cells, in library order and library
/// coordinates: what <c>ssmap room</c> keeps in the pack's library section
/// (<see cref="RoomLibraryEntities.SectionTag"/>). Empty when there are none.
/// </param>
public sealed record RoomLibrarySplit(IReadOnlyList<LibraryRoom> Rooms, IReadOnlyList<VmfChunk> LibraryEntities)
{
    /// <summary>
    /// What the library sets for every level linked from it, from its
    /// worldspawn keys (<see cref="RoomLibraryOptions.FromWorld"/>): what
    /// <c>ssmap room</c> keeps in the pack's library section
    /// (<see cref="RoomLibraryOptions.SectionTag"/>).
    /// </summary>
    public RoomLibraryOptions Options { get; init; } = RoomLibraryOptions.None;
}

/// <summary>
/// A room library that cannot be split into rooms: one problem, named.
/// </summary>
public sealed class RoomLibraryException : Exception
{
    /// <summary>Names the problem and where it is.</summary>
    /// <param name="message">What is wrong with the library.</param>
    public RoomLibraryException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The room library: every room of a set in one VMF, each in its own cell,
/// marked by an <c>info_room</c> entity, with gaps between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The format.</b> A room is a cell-sized box of brushes built anywhere
/// in the library map. An <c>info_room</c> point entity stands at the cell's
/// low corner (least x, y and z) and carries the room's description:
/// </para>
/// <list type="table">
/// <listheader><term>key</term><description>meaning</description></listheader>
/// <item><term><c>name</c></term><description>The room's name (<see cref="RoomNames"/>): its entry in the room pack and what a level calls it.</description></item>
/// <item><term><c>cell_size</c></term><description>The cell's edge, in units; the cell is a cube.</description></item>
/// <item><term><c>door_width</c></term><description>The door opening's width along its wall.</description></item>
/// <item><term><c>door_height</c></term><description>The door opening's height.</description></item>
/// <item><term><c>wall_depth</c></term><description>The shell's thickness, which is also how deep a door plug reaches in from the cell face.</description></item>
/// <item><term><c>socket_east</c>, <c>socket_west</c>, <c>socket_north</c>, <c>socket_south</c></term><description>Optional: a name for the socket on that wall, where the default is the wall's own name. East is +x, north is +y.</description></item>
/// <item><term><c>room_role</c></term><description>Optional: <c>up</c> or <c>down</c> for a room that moves the player between levels (<see cref="LibraryRoom.Role"/>).</description></item>
/// </list>
/// <para>
/// Every world brush and every brush entity inside a cell's box belongs to
/// that room, and so does every point entity whose origin is inside it.
/// Point entities in the gaps belong to no room and are ignored (a note for
/// the author, a camera, a light to see by in the editor), except the
/// classes the whole library shares, such as the sun, which are collected
/// for the pack's library section (<see cref="RoomLibraryEntities"/>,
/// <see cref="SplitLibrary"/>); a brush in the
/// gaps, or one that crosses a cell's edge, is an error, because it is
/// geometry some room would silently lose. Two cells may touch but not
/// overlap. Every room of a library shares one grid and one door kit, and
/// the kit must let a standing player through (<see cref="PlayerHull"/>).
/// </para>
/// <para>
/// <b>Sockets</b> are found, not declared: a world brush that exactly fills
/// the kit's plug box on one of the four walls (<see cref="RoomLinter.SealBox"/>)
/// is that wall's door plug, and the wall is a socket. Found by geometry
/// alone, so a library splits, and a level is generated from it, without a
/// game to read materials from; the room's compile then holds every plug to
/// being a <c>%compileTrigger</c> brush, and refuses any other trigger brush
/// that is not a plug (<see cref="RoomLinter.CheckModel"/>), which is where
/// a plug cut to the wrong size is reported.
/// </para>
/// <para>
/// <b>Names are read as UTF-8.</b> A VMF is read byte for byte (Latin-1),
/// which is what vbsp needs; a room name, though, is typed by an author whose
/// editor writes UTF-8. So the name's bytes are decoded as UTF-8 when they
/// are valid UTF-8, and kept as read when they are not (a Latin-1 file's
/// <c>é</c> is a single byte that is not UTF-8).
/// </para>
/// </remarks>
public static class RoomLibraryVmf
{
    /// <summary>The classname of the entity that marks a room.</summary>
    public const string RoomEntity = "info_room";

    /// <summary>The room's name.</summary>
    public const string NameKey = "name";

    /// <summary>The cell's edge.</summary>
    public const string CellSizeKey = "cell_size";

    /// <summary>The door opening's width.</summary>
    public const string DoorWidthKey = "door_width";

    /// <summary>The door opening's height.</summary>
    public const string DoorHeightKey = "door_height";

    /// <summary>The shell's thickness and the plug's depth.</summary>
    public const string WallDepthKey = "wall_depth";

    /// <summary>The prefix of the optional socket-name keys: <c>socket_east</c> and so on.</summary>
    public const string SocketKeyPrefix = "socket_";

    /// <summary>The room's walls as the socket-name keys and default socket names spell them.</summary>
    /// <param name="facing">The room-local wall.</param>
    /// <returns><c>east</c>, <c>west</c>, <c>north</c> or <c>south</c>.</returns>
    public static string WallName(RoomFacing facing) => facing switch
    {
        RoomFacing.PositiveX => "east",
        RoomFacing.NegativeX => "west",
        RoomFacing.PositiveY => "north",
        RoomFacing.NegativeY => "south",
        _ => throw new ArgumentOutOfRangeException(nameof(facing), facing, "A room has four walls."),
    };

    /// <summary>Splits a library into its rooms.</summary>
    /// <param name="library">The library VMF.</param>
    /// <returns>The rooms, in the order their <c>info_room</c> entities appear.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="library"/> is null.</exception>
    /// <exception cref="RoomLibraryException">The library breaks a rule; the message names it.</exception>
    /// <remarks>The rooms of <see cref="SplitLibrary"/>, for a caller that has no use for the library-wide entities.</remarks>
    public static IReadOnlyList<LibraryRoom> Split(VmfDocument library) => SplitLibrary(library).Rooms;

    /// <summary>Splits a library into its rooms, and collects the entities the whole library shares.</summary>
    /// <param name="library">The library VMF.</param>
    /// <returns>
    /// The rooms, in the order their <c>info_room</c> entities appear, and
    /// the library-wide entities (<see cref="RoomLibraryEntities.IsLibraryWide"/>)
    /// found in the gaps between cells, in library order.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="library"/> is null.</exception>
    /// <exception cref="RoomLibraryException">The library breaks a rule; the message names it.</exception>
    public static RoomLibrarySplit SplitLibrary(VmfDocument library)
    {
        ArgumentNullException.ThrowIfNull(library);

        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)
            ?? throw new RoomLibraryException("the library has no world chunk.");
        RoomLibraryOptions options = RoomLibraryOptions.FromWorld(world);

        List<VmfChunk> entities = [.. library.GetChunks(MapFileLoader.EntityChunk)];
        List<Marker> markers = [.. entities.Where(IsRoomMarker).Select(ReadMarker)];
        if (markers.Count == 0)
        {
            throw new RoomLibraryException(
                $"the library has no {RoomEntity} entity; each room is marked by one at its cell's low corner.");
        }

        CheckMarkers(markers);

        // Every brush of the world finds its room, or the library is refused.
        List<VmfChunk>[] solids = [.. markers.Select(_ => new List<VmfChunk>())];
        foreach (VmfChunk solid in world.GetChunks(MapFileLoader.SolidChunk))
        {
            Box box = VmfPlacement.Bounds(solid);
            int owner = Owner(markers, box);
            if (owner < 0)
            {
                throw new RoomLibraryException(
                    $"world brush {VmfPlacement.IdOf(solid)} at ({Fmt(box.Mins)})-({Fmt(box.Maxs)}) is not inside any"
                    + $" room's cell: brushes between rooms, or across a cell's edge, belong to no room.");
            }

            solids[owner].Add(solid);
        }

        List<VmfChunk>[] owned = [.. markers.Select(_ => new List<VmfChunk>())];
        List<VmfChunk> libraryWide = [];
        foreach (VmfChunk entity in entities)
        {
            if (IsRoomMarker(entity))
            {
                continue;
            }

            int owner = EntityOwner(markers, entity);
            if (owner >= 0)
            {
                owned[owner].Add(entity);
            }
            else if (RoomLibraryEntities.IsLibraryWide(entity.GetValue("classname")))
            {
                // In the gaps and shared by the whole library: kept as the
                // library wrote it, in library coordinates, not dropped.
                libraryWide.Add(VmfPlacement.Clone(entity));
            }
        }

        VmfChunk? version = library.GetChunk("versioninfo");
        List<LibraryRoom> rooms = [];
        for (int i = 0; i < markers.Count; i++)
        {
            Marker marker = markers[i];
            QuarterTurn home = QuarterTurn.Translation(-marker.Corner);

            VmfDocument document = new();
            if (version is not null)
            {
                document.Chunks.Add(VmfPlacement.Clone(version));
            }

            // The library's own settings stay out of the room: they are for
            // the link, which reads them from the pack, not for the map.
            VmfChunk roomWorld = new(world.Name);
            foreach (VmfKey key in world.Keys)
            {
                if (!RoomLibraryOptions.IsLibraryKey(key.Name))
                {
                    roomWorld.AddKey(key.Name, key.Value);
                }
            }

            List<Box> localSolids = [];
            foreach (VmfChunk solid in solids[i])
            {
                VmfChunk moved = VmfPlacement.MoveSolid(solid, home);
                localSolids.Add(VmfPlacement.Bounds(moved));
                roomWorld.Children.Add(moved);
            }

            document.Chunks.Add(roomWorld);
            foreach (VmfChunk entity in owned[i])
            {
                document.Chunks.Add(VmfPlacement.MoveEntity(entity, home));
            }

            RoomDefinition definition = new(marker.Name, marker.CellSize, marker.Kit, Sockets(marker, localSolids));
            definition.Validate();
            rooms.Add(new LibraryRoom(definition, marker.Corner, document) { Role = marker.Role });
        }

        return new RoomLibrarySplit(rooms, libraryWide) { Options = options };
    }

    /// <summary>The plugs among a room's world brushes, as sockets in wall order.</summary>
    /// <param name="marker">The room.</param>
    /// <param name="localSolids">Its world brushes' boxes, room-local.</param>
    private static List<RoomSocket> Sockets(Marker marker, List<Box> localSolids)
    {
        RoomDefinition bare = new(marker.Name, marker.CellSize, marker.Kit, []);
        List<RoomSocket> sockets = [];
        foreach (RoomFacing facing in Enum.GetValues<RoomFacing>())
        {
            Box plug = RoomLinter.SealBox(bare, new RoomSocket(facing, "probe"), marker.CellSize);
            int found = localSolids.Count(box => Same(box, plug));
            string wall = WallName(facing);
            marker.SocketNames.TryGetValue(wall, out string? named);
            if (found == 0)
            {
                if (named is not null)
                {
                    throw new RoomLibraryException(
                        $"room \"{marker.Name}\" names its {wall} socket \"{named}\" ({SocketKeyPrefix}{wall}),"
                        + $" but its {wall} wall has no door plug: no world brush fills ({Fmt(plug.Mins)})-({Fmt(plug.Maxs)}).");
                }

                continue;
            }

            if (found > 1)
            {
                throw new RoomLibraryException(
                    $"room \"{marker.Name}\" has {found} door plugs on its {wall} wall; a wall has one socket.");
            }

            sockets.Add(new RoomSocket(facing, named ?? wall));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (RoomSocket socket in sockets)
        {
            if (!seen.Add(socket.Name))
            {
                throw new RoomLibraryException(
                    $"room \"{marker.Name}\" has two sockets named \"{socket.Name}\"; a socket's name is unique in its room.");
            }
        }

        return sockets;
    }

    internal static bool Same(Box a, Box b) =>
        Math.Abs(a.Mins.X - b.Mins.X) <= PlayerHull.SillTolerance
        && Math.Abs(a.Mins.Y - b.Mins.Y) <= PlayerHull.SillTolerance
        && Math.Abs(a.Mins.Z - b.Mins.Z) <= PlayerHull.SillTolerance
        && Math.Abs(a.Maxs.X - b.Maxs.X) <= PlayerHull.SillTolerance
        && Math.Abs(a.Maxs.Y - b.Maxs.Y) <= PlayerHull.SillTolerance
        && Math.Abs(a.Maxs.Z - b.Maxs.Z) <= PlayerHull.SillTolerance;

    /// <summary>The rules between rooms: names, one grid and kit, a kit a player fits, no overlaps.</summary>
    private static void CheckMarkers(List<Marker> markers)
    {
        Dictionary<string, Marker> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (Marker marker in markers)
        {
            if (byName.TryGetValue(marker.Name, out Marker? other))
            {
                throw new RoomLibraryException(
                    $"two rooms are named \"{other.Name}\" and \"{marker.Name}\"; room names are unique, ignoring case,"
                    + " because each names an entry of the room pack.");
            }

            byName[marker.Name] = marker;
        }

        Marker first = markers[0];
        foreach (Marker marker in markers)
        {
            if (marker.CellSize != first.CellSize || marker.Kit != first.Kit)
            {
                throw new RoomLibraryException(
                    $"room \"{marker.Name}\" is built for cell {Num(marker.CellSize)} and kit {marker.Kit},"
                    + $" but room \"{first.Name}\" for cell {Num(first.CellSize)} and kit {first.Kit};"
                    + " every room of a library shares one grid and one door kit.");
            }
        }

        if (PlayerHull.DoorProblem(first.Kit, first.CellSize) is { } problem)
        {
            throw new RoomLibraryException(
                $"the library's door kit ({DoorWidthKey} {Num(first.Kit.Width)}, {DoorHeightKey} {Num(first.Kit.Height)},"
                + $" {WallDepthKey} {Num(first.Kit.Depth)}, {CellSizeKey} {Num(first.CellSize)}) {problem}.");
        }

        for (int i = 0; i < markers.Count; i++)
        {
            for (int j = i + 1; j < markers.Count; j++)
            {
                if (markers[i].Cell.Overlaps(markers[j].Cell, RoomLinter.CellEpsilon))
                {
                    throw new RoomLibraryException(
                        $"the cells of rooms \"{markers[i].Name}\" and \"{markers[j].Name}\" overlap;"
                        + " rooms stand in separate cells, with gaps between them.");
                }
            }
        }
    }

    /// <summary>Which room's cell holds a box, or -1.</summary>
    private static int Owner(List<Marker> markers, Box box)
    {
        for (int i = 0; i < markers.Count; i++)
        {
            if (box.ContainsWithin(markers[i].Cell, RoomLinter.CellEpsilon))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Which room an entity belongs to: a brush entity by its brushes, all in
    /// one cell; a point entity by its origin; -1 for a point entity in the
    /// gaps, which is ignored.
    /// </summary>
    private static int EntityOwner(List<Marker> markers, VmfChunk entity)
    {
        List<VmfChunk> brushes = [.. entity.GetChunks(MapFileLoader.SolidChunk)];
        if (brushes.Count == 0)
        {
            return VmfPlacement.Origin(entity) is { } origin ? Owner(markers, new Box(origin, origin)) : -1;
        }

        int owner = -2;
        foreach (VmfChunk solid in brushes)
        {
            Box box = VmfPlacement.Bounds(solid);
            int cell = Owner(markers, box);
            if (cell < 0 || (owner >= 0 && cell != owner))
            {
                throw new RoomLibraryException(
                    $"entity {VmfPlacement.IdOf(entity)} ({entity.GetValue("classname") ?? "no classname"}) has brush"
                    + $" {VmfPlacement.IdOf(solid)} at ({Fmt(box.Mins)})-({Fmt(box.Maxs)}) outside its room's cell;"
                    + " a brush entity lies inside one room's cell.");
            }

            owner = cell;
        }

        return owner;
    }

    private static bool IsRoomMarker(VmfChunk entity) =>
        string.Equals(entity.GetValue("classname"), RoomEntity, StringComparison.OrdinalIgnoreCase);

    private static Marker ReadMarker(VmfChunk entity)
    {
        string where = VmfPlacement.Origin(entity) is { } at
            ? $"the {RoomEntity} at ({Fmt(at)})"
            : $"{RoomEntity} {VmfPlacement.IdOf(entity)}";
        Vec3 corner = VmfPlacement.Origin(entity)
            ?? throw new RoomLibraryException($"{where} has no origin; it stands at its room's cell's low corner.");

        string name = Utf8(entity.GetValue(NameKey)
            ?? throw new RoomLibraryException($"{where} has no \"{NameKey}\"; every room is named."));
        if (RoomNames.Problem(name) is { } problem)
        {
            throw new RoomLibraryException($"{where}: the room name \"{name}\" {problem}.");
        }

        string who = $"room \"{name}\"";
        float cell = Positive(entity, CellSizeKey, who);
        SocketKit kit = new(Positive(entity, DoorWidthKey, who), Positive(entity, DoorHeightKey, who), Positive(entity, WallDepthKey, who));
        try
        {
            kit.Validate();
            _ = kit.OpeningUnit(cell);
        }
        catch (ArgumentException exception)
        {
            throw new RoomLibraryException($"{who}: the door kit {kit} is not a kit: {exception.Message}");
        }

        (_, _, float u1, float v1) = kit.OpeningUnit(cell);
        if (u1 > 1f || v1 > 1f || kit.Depth * 2 >= cell)
        {
            throw new RoomLibraryException(string.Create(CultureInfo.InvariantCulture,
                $"{who}: the door kit {kit} does not fit in a {cell:0.###}-unit cell."));
        }

        Dictionary<string, string> socketNames = new(StringComparer.Ordinal);
        foreach (VmfKey key in entity.Keys)
        {
            if (!key.Name.StartsWith(SocketKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string wall = key.Name[SocketKeyPrefix.Length..].ToLowerInvariant();
            if (wall is not ("east" or "west" or "north" or "south"))
            {
                throw new RoomLibraryException(
                    $"{who} has a key \"{key.Name}\"; a socket is named by {SocketKeyPrefix}east, west, north or south.");
            }

            string socket = Utf8(key.Value);
            if (string.IsNullOrWhiteSpace(socket))
            {
                throw new RoomLibraryException($"{who} names its {wall} socket with an empty \"{key.Name}\".");
            }

            socketNames[wall] = socket;
        }

        RoomRole role;
        try
        {
            role = RoomPois.ParseRole(entity.GetValue(RoomPois.RoleKey));
        }
        catch (RoomLibraryException exception)
        {
            throw new RoomLibraryException($"{who}: {exception.Message}");
        }

        return new Marker(name, corner, cell, kit, socketNames) { Role = role };
    }

    private static float Positive(VmfChunk entity, string key, string who)
    {
        string text = entity.GetValue(key)
            ?? throw new RoomLibraryException($"{who} has no \"{key}\"; the {RoomEntity} keys are {NameKey}, {CellSizeKey},"
                + $" {DoorWidthKey}, {DoorHeightKey} and {WallDepthKey}.");
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            throw new RoomLibraryException($"{who}: \"{key}\" is \"{text}\", not a number.");
        }

        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new RoomLibraryException($"{who}: \"{key}\" is \"{text}\", not a positive finite number.");
        }

        return value;
    }

    /// <summary>A value read byte for byte, decoded as UTF-8 when its bytes are UTF-8.</summary>
    internal static string Utf8(string latin1)
    {
        foreach (char c in latin1)
        {
            if (c > 'ÿ')
            {
                // Built in memory rather than read from bytes: already text.
                return latin1;
            }
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Encoding.Latin1.GetBytes(latin1));
        }
        catch (DecoderFallbackException)
        {
            return latin1;
        }
    }

    private static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Fmt(Vec3 v) =>
        string.Create(CultureInfo.InvariantCulture, $"{v.X:0.###} {v.Y:0.###} {v.Z:0.###}");

    /// <summary>One <c>info_room</c>, read.</summary>
    private sealed record Marker(string Name, Vec3 Corner, float CellSize, SocketKit Kit, Dictionary<string, string> SocketNames)
    {
        public Box Cell => new(Corner, Corner + new Vec3(CellSize, CellSize, CellSize));

        public RoomRole Role { get; init; }
    }
}
