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
/// the library, and its own VMF, moved so its box is <c>[0, cell]² × [0, height]</c>
/// (the cube <c>[0, cell]³</c> for a room without a <c>room_height</c>).
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

    /// <summary>
    /// The room's water sockets by socket name, from its <c>info_room</c>'s
    /// <c>water_&lt;wall&gt;</c> keys (<see cref="RoomLibraryVmf.WaterKeyPrefix"/>):
    /// the sockets its water may reach, at the level each declares. Empty
    /// without the keys.
    /// </summary>
    public IReadOnlyDictionary<string, RoomWaterSocket> WaterSockets { get; init; } = new Dictionary<string, RoomWaterSocket>();

    /// <summary>
    /// The room's display label on the level map, from its <c>info_room</c>'s
    /// <c>map_label</c> key (<see cref="SourceSharp.RoomContracts.LevelMap.LabelKey"/>);
    /// empty without the key.
    /// </summary>
    public string MapLabel { get; init; } = string.Empty;

    /// <summary>
    /// The namespace the room is compiled into, for a room of a pack with
    /// namespaces (<see cref="RoomPackCombiner"/>), or null for a room of a
    /// plain pack, which every library split gives.
    /// </summary>
    /// <remarks>
    /// It carries the room's own library's name keys: a combined pack
    /// compiles every library's rooms in one run, and each library's name
    /// keys stay with its rooms (the rooms design, 17.4), so the run's own
    /// (<see cref="RoomLibraryCompileSettings.NameKeys"/>) cannot serve them
    /// all. The compile and the room cache's key read the room's through
    /// <see cref="NameKeysOr"/>.
    /// </remarks>
    public RoomNamespace? Namespace { get; init; }

    /// <summary>
    /// The name keys the room compiles with: its namespace's when it has
    /// one, else the run's.
    /// </summary>
    /// <param name="run">The run's name keys (<see cref="RoomLibraryCompileSettings.NameKeys"/>).</param>
    /// <returns>The keys, or null when neither adds any.</returns>
    public IReadOnlySet<string>? NameKeysOr(IReadOnlySet<string>? run) => Namespace is { } space ? space.NameKeys : run;
}

/// <summary>The namespace a room of a combined pack is compiled into.</summary>
/// <param name="Key">The namespace: its library's key, the front of the room's name.</param>
/// <param name="NameKeys">Its library's name-valued keys (<see cref="RoomLibraryOptions.NameKeySet"/>), or null when it adds none.</param>
public sealed record RoomNamespace(string Key, IReadOnlySet<string>? NameKeys);

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
    /// The library's skybox room (<see cref="RoomLibraryVmf.SkyboxEntity"/>),
    /// or null when it has none: its cell's brushes and entities, room-local,
    /// its <c>sky_camera</c> among them, with no sockets. It is not one of
    /// <see cref="Rooms"/>: no level places it; the link and the flatten put
    /// it below every level's grid (the rooms design, 4.12).
    /// </summary>
    public LibraryRoom? Skybox { get; init; }

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
/// <item><term><c>cell_size</c></term><description>The cell's edge, in units: the grid every room of the library stands on, and the cube a room fills unless it gives its own height.</description></item>
/// <item><term><c>room_height</c></term><description>Optional: the room's own height, in whole units, the cell size by default (<see cref="RoomDefinition.Height"/>). The room's box is then <c>[0, c] × [0, c] × [0, room_height]</c>; its doors stay where a cube room's are.</description></item>
/// <item><term><c>door_width</c></term><description>The door opening's width along its wall.</description></item>
/// <item><term><c>door_height</c></term><description>The door opening's height.</description></item>
/// <item><term><c>wall_depth</c></term><description>The shell's thickness, which is also how deep a door plug reaches in from the cell face.</description></item>
/// <item><term><c>socket_east</c>, <c>socket_west</c>, <c>socket_north</c>, <c>socket_south</c></term><description>Optional: a name for the socket on that wall, where the default is the wall's own name. East is +x, north is +y.</description></item>
/// <item><term><c>room_role</c></term><description>Optional: <c>up</c> or <c>down</c> for a room that moves the player between levels (<see cref="LibraryRoom.Role"/>).</description></item>
/// <item><term><c>water_east</c>, <c>water_west</c>, <c>water_north</c>, <c>water_south</c></term><description>Optional: a water socket, the height of the water's surface above the cell's floor and its material, such as <c>48 nature/water_canals_cheap001</c> (<see cref="LibraryRoom.WaterSockets"/>).</description></item>
/// </list>
/// <para>
/// Every world brush and every brush entity inside a cell's box belongs to
/// that room, and so does every point entity whose origin is inside it.
/// Point entities in the gaps belong to no room and are ignored (a note for
/// the author, a camera, a light to see by in the editor), except the
/// classes the whole library shares, such as the sun, which are collected
/// for the pack's library section (<see cref="RoomLibraryEntities"/>,
/// <see cref="SplitLibrary"/>), and a <c>sky_camera</c>, which is refused.
/// A room's own copy of a library-wide class is dropped when it equals the
/// library's and refused when it does not
/// (<see cref="RoomLibraryEntities.KeepInRoom"/>). A brush in the
/// gaps, or one that crosses a cell's edge, is an error, because it is
/// geometry some room would silently lose. Two cells may touch but not
/// overlap. Every room of a library shares one grid and one door kit, and
/// the kit must let a standing player through (<see cref="PlayerHull"/>).
/// </para>
/// <para>
/// <b>The skybox room.</b> One cell may be marked with an
/// <see cref="SkyboxEntity"/> (<c>info_room_skybox</c>) instead, with a
/// <c>name</c>: the library's 3D skybox, on the library's grid, owning what
/// stands in it as a room does, with exactly one <c>sky_camera</c>, no door
/// plug and no <c>room_needs</c> or <c>room_socket</c>. It is not one of the
/// rooms (<see cref="RoomLibrarySplit.Skybox"/>): no level places it, and the
/// link and the flatten put it below every level's grid.
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

    /// <summary>
    /// The classname of the entity that marks the library's skybox room: at
    /// its cell's low corner, with a <see cref="NameKey"/>, the cell the
    /// library's grid (the rooms design, 4.12 and open point O11).
    /// </summary>
    public const string SkyboxEntity = "info_room_skybox";

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

    /// <summary>
    /// The room's own height, optional: the cell size when absent (the rooms
    /// design, 17.6). Not compared between rooms or libraries: rooms of one
    /// library, and of the libraries of one level, may differ in height.
    /// </summary>
    public const string RoomHeightKey = "room_height";

    /// <summary>The prefix of the optional socket-name keys: <c>socket_east</c> and so on.</summary>
    public const string SocketKeyPrefix = "socket_";

    /// <summary>
    /// The prefix of the optional water socket keys: <c>water_east</c> and so
    /// on, each <c>"&lt;level&gt; &lt;material&gt;"</c> (<see cref="RoomWaterSocket"/>).
    /// </summary>
    public const string WaterKeyPrefix = "water_";

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

        // The skybox room: one at most, a cell of the library's grid, owning
        // what stands in it as a room does; it is split with the rooms and
        // set apart at the end.
        List<VmfChunk> skyboxes = [.. entities.Where(IsSkyboxMarker)];
        if (skyboxes.Count > 1)
        {
            throw new RoomLibraryException(
                $"the library has {skyboxes.Count} {SkyboxEntity} entities; a library has one skybox room at most.");
        }

        int skyboxIndex = -1;
        if (skyboxes.Count == 1)
        {
            skyboxIndex = markers.Count;
            markers.Add(ReadSkyboxMarker(skyboxes[0], markers[0]));
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
            if (IsRoomMarker(entity) || IsSkyboxMarker(entity))
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
            else if (string.Equals(entity.GetValue("classname"), RoomLibraryEntities.SkyCameraClass, StringComparison.Ordinal))
            {
                // Not ignored like the rest of the gaps' clutter: a 3D
                // skybox that silently vanished would be the library's sky.
                throw new RoomLibraryException(
                    $"the library has a sky_camera (entity {VmfPlacement.IdOf(entity)}) in the gaps between rooms;"
                    + " sky_camera is allowed only in the library's skybox room.");
            }
        }

        // Water overlays: those the world holds go to the room whose cell
        // holds each one's BasisOrigin, as an info_overlay_transition of
        // their own; every one an entity holds must stand in its room's cell.
        WaterOverlaysOf(world, markers, owned);

        // One of each singleton in the gaps, and every room's own copies
        // checked against them (decision D3): an equal copy is dropped from
        // the room, so no room compile, entity count or link ever sees it.
        RoomLibraryEntities.CheckGaps(libraryWide);
        for (int i = 0; i < markers.Count; i++)
        {
            string room = markers[i].Name;
            owned[i].RemoveAll(entity => !RoomLibraryEntities.KeepInRoom(room, entity, libraryWide, skybox: i == skyboxIndex));

            // A brush entity whose angles a turn could not treat right
            // (open point O15): refused here, so the pack and the flatten,
            // which both split the library, refuse it alike.
            foreach (VmfChunk entity in owned[i])
            {
                if (BrushEntityDirections.Problem(room, entity) is { } problem)
                {
                    throw new RoomLibraryException(problem);
                }
            }
        }

        VmfChunk? version = library.GetChunk("versioninfo");
        List<LibraryRoom> rooms = [];
        LibraryRoom? skybox = null;
        for (int i = 0; i < markers.Count; i++)
        {
            Marker marker = markers[i];
            QuarterTurn home = QuarterTurn.Translation(-marker.Corner);

            VmfDocument document = new();
            if (version is not null)
            {
                document.Chunks.Add(WithRoomMapVersion(VmfPlacement.Clone(version)));
            }

            // The library's own settings stay out of the room: they are for
            // the link, which reads them from the pack, not for the map. The
            // editor's save counter stays, at a fixed value, where it was
            // (RoomLibraryOptions.MapVersionKey says why).
            VmfChunk roomWorld = new(world.Name);
            foreach (KeyValuePair<string, string> key in RoomWorldKeys(world))
            {
                roomWorld.AddKey(key.Key, key.Value);
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

            RoomDefinition definition = new(marker.Name, marker.CellSize, marker.Kit, Sockets(marker, localSolids)) { Height = marker.Height };
            definition.Validate();
            IReadOnlyDictionary<string, RoomWaterSocket> waterSockets = WaterSockets(marker, definition);

            // An overlay on a socket's plug (the rooms design, 4.9): refused
            // here, where the plugs are known, so the pack and the flatten,
            // which both split the library, refuse it alike.
            if (RoomOverlays.PlugProblem(definition, document) is { } overlayProblem)
            {
                throw new RoomLibraryException(overlayProblem);
            }

            // An area portal in a socket's plug box, or one named as socket
            // furniture (the rooms design, 4.11): refused here too, so the
            // pack and the flatten refuse it alike.
            if (RoomAreaPortals.Problem(definition, document) is { } portalProblem)
            {
                throw new RoomLibraryException(portalProblem);
            }

            // A displacement the link cannot carry as the flattened level
            // compiles it (the rooms design, 4.5): power 4, out of the cell,
            // or reaching into a doorway. Refused here too, so the pack and
            // the flatten refuse it alike.
            if (RoomDisplacements.Problem(definition, document) is { } displacementProblem)
            {
                throw new RoomLibraryException(displacementProblem);
            }

            if (i == skyboxIndex)
            {
                CheckSkybox(definition, owned[i]);
                skybox = new LibraryRoom(definition, marker.Corner, document);
                continue;
            }

            rooms.Add(new LibraryRoom(definition, marker.Corner, document) { Role = marker.Role, WaterSockets = waterSockets, MapLabel = marker.MapLabel });
        }

        return new RoomLibrarySplit(rooms, libraryWide) { Options = options, Skybox = skybox };
    }

    /// <summary>
    /// Gives each room the water overlays the library's world holds in its
    /// cell, and holds every entity's water overlays to its room's cell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A water overlay is an <c>overlaydata</c> chunk inside an
    /// <c>overlaytransition</c> chunk, which vbsp reads from the world and
    /// from any entity (the editor puts them in an
    /// <c>info_overlay_transition</c>, which vbsp then clears). A room's
    /// world is its brushes and keys, so the world's are carried as one
    /// <c>info_overlay_transition</c> per room, written first among the
    /// room's entities (vbsp reads the world's before any entity's), each
    /// overlay going to the room whose cell holds its <c>BasisOrigin</c>.
    /// One in the gaps would be lost with no room to draw it, and one an
    /// entity carries into another room's cell would name sides its room
    /// does not have, so both are refused.
    /// </para>
    /// <para>
    /// The overlays' vectors move with the room (<see cref="VmfPlacement.MoveWaterOverlays"/>),
    /// and the flatten renames their <c>sides</c> lists as it renames an
    /// entity's.
    /// </para>
    /// </remarks>
    private static void WaterOverlaysOf(VmfChunk world, List<Marker> markers, List<VmfChunk>[] owned)
    {
        List<VmfChunk>?[] fromWorld = new List<VmfChunk>?[markers.Count];
        foreach (VmfChunk transition in world.GetChunks(MapFileLoader.OverlayTransitionChunk))
        {
            foreach (VmfChunk data in transition.GetChunks(MapFileLoader.OverlayDataChunk))
            {
                Vec3 at = WaterOverlayOrigin(data, world);
                int owner = markers.FindIndex(m => new Box(at, at).ContainsWithin(m.Cell, RoomLinter.CellEpsilon));
                if (owner < 0)
                {
                    throw new RoomLibraryException(
                        $"the library has a water overlay at ({Fmt(at)}) in the gaps between rooms;"
                        + " a water overlay belongs to the room whose cell holds its BasisOrigin.");
                }

                (fromWorld[owner] ??= []).Add(VmfPlacement.Clone(data));
            }
        }

        for (int i = 0; i < markers.Count; i++)
        {
            foreach (VmfChunk entity in owned[i])
            {
                foreach (VmfChunk transition in entity.GetChunks(MapFileLoader.OverlayTransitionChunk))
                {
                    foreach (VmfChunk data in transition.GetChunks(MapFileLoader.OverlayDataChunk))
                    {
                        Vec3 at = WaterOverlayOrigin(data, entity);
                        if (!new Box(at, at).ContainsWithin(markers[i].Cell, RoomLinter.CellEpsilon))
                        {
                            throw new RoomLibraryException(
                                $"room {markers[i].Name}: entity {VmfPlacement.IdOf(entity)} ({entity.GetValue("classname") ?? "no classname"})"
                                + $" has a water overlay at ({Fmt(at)}) outside the room's cell;"
                                + " a water overlay belongs to the room whose cell holds its BasisOrigin.");
                        }
                    }
                }
            }

            if (fromWorld[i] is { } datas)
            {
                VmfChunk carrier = new(MapFileLoader.EntityChunk);
                carrier.AddKey("classname", WaterOverlayCarrier);
                carrier.AddKey("origin", VmfPlacement.Format(WaterOverlayOrigin(datas[0], world)));
                VmfChunk block = new(MapFileLoader.OverlayTransitionChunk);
                foreach (VmfChunk data in datas)
                {
                    block.Children.Add(data);
                }

                carrier.Children.Add(block);
                owned[i].Insert(0, carrier);
            }
        }
    }

    /// <summary>The class of the entity a room's water overlays from the world are carried in, as the editor writes one.</summary>
    internal const string WaterOverlayCarrier = "info_overlay_transition";

    /// <summary>A water overlay's <c>BasisOrigin</c>, which places it.</summary>
    private static Vec3 WaterOverlayOrigin(VmfChunk data, VmfChunk owner) =>
        VmfPlacement.BracketedVector(
            data.GetValue(OverlayOriginKey) ?? throw new RoomLibraryException(
                $"{(owner.GetValue("classname") is { } c ? $"entity {VmfPlacement.IdOf(owner)} ({c})" : "the world")} has a water overlay without a {OverlayOriginKey}."),
            OverlayOriginKey,
            owner);

    private const string OverlayOriginKey = "BasisOrigin";

    /// <summary>
    /// A library's worldspawn keys as each of its rooms carries them: every
    /// key but the library's own settings (<see cref="RoomLibraryOptions.IsLibraryKey"/>),
    /// in the library's order, with the editor's save counter at its fixed
    /// room value (<see cref="RoomLibraryOptions.RoomMapVersion"/>).
    /// </summary>
    /// <param name="world">The library's world chunk.</param>
    /// <returns>The keys, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    /// <remarks>
    /// The split writes these into every room's world, and a combined pack
    /// (<see cref="RoomPackCombiner"/>) writes the first library's into every
    /// other library's rooms: one function, so a room of the first library
    /// and a room given its worldspawn carry the same keys.
    /// </remarks>
    public static IReadOnlyList<KeyValuePair<string, string>> RoomWorldKeys(VmfChunk world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return [.. world.Keys
            .Where(key => !RoomLibraryOptions.IsLibraryKey(key.Name))
            .Select(key => new KeyValuePair<string, string>(key.Name, IsMapVersion(key) ? RoomLibraryOptions.RoomMapVersion : key.Value))];
    }

    /// <summary>
    /// A room with another worldspawn: its world's keys replaced by the given
    /// ones, its brushes and every other chunk of its document as they were.
    /// </summary>
    /// <param name="room">The room, as a split gave it.</param>
    /// <param name="world">The keys its world takes instead (<see cref="RoomWorldKeys"/> of another library).</param>
    /// <returns>A room over a new document; the given room is not changed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="RoomLibraryException">The room's document has no world.</exception>
    /// <remarks>
    /// What a combined pack compiles a later library's room from (D26): the
    /// first library's worldspawn, so every room of the pack is compiled
    /// under the level's. The brushes keep their order after the keys, as
    /// the split writes them.
    /// </remarks>
    public static LibraryRoom WithWorld(LibraryRoom room, IReadOnlyList<KeyValuePair<string, string>> world)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(world);
        VmfDocument document = new();
        bool found = false;
        foreach (VmfChunk chunk in room.Document.Chunks)
        {
            if (!found && string.Equals(chunk.Name, MapFileLoader.WorldChunk, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                VmfChunk replaced = new(chunk.Name);
                foreach (KeyValuePair<string, string> key in world)
                {
                    replaced.AddKey(key.Key, key.Value);
                }

                foreach (VmfChunk child in chunk.Chunks)
                {
                    replaced.Children.Add(child);
                }

                document.Chunks.Add(replaced);
                continue;
            }

            document.Chunks.Add(chunk);
        }

        return found
            ? room with { Document = document }
            : throw new RoomLibraryException($"room \"{room.Definition.Name}\" has no world chunk.");
    }

    private static bool IsMapVersion(VmfKey key) =>
        string.Equals(key.Name, RoomLibraryOptions.MapVersionKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A room's copy of the library's <c>versioninfo</c> with its
    /// <c>mapversion</c> fixed too: no compile reads the chunk, but it is
    /// part of the room's document, which the room cache key folds, and a
    /// save must not change that either.
    /// </summary>
    private static VmfChunk WithRoomMapVersion(VmfChunk version)
    {
        foreach (VmfKey key in version.Keys)
        {
            if (IsMapVersion(key))
            {
                key.Value = RoomLibraryOptions.RoomMapVersion;
            }
        }

        return version;
    }

    /// <summary>
    /// A room's water sockets by socket name, from its <c>water_&lt;wall&gt;</c>
    /// keys: each on a wall with a socket, its level above the door's sill
    /// (water no higher does not reach the doorway, and needs no socket).
    /// </summary>
    private static Dictionary<string, RoomWaterSocket> WaterSockets(Marker marker, RoomDefinition definition)
    {
        Dictionary<string, RoomWaterSocket> sockets = new(StringComparer.Ordinal);
        foreach ((string wall, RoomWaterSocket water) in marker.WaterWalls)
        {
            List<RoomSocket> onWall = [.. definition.Sockets.Where(s => WallName(s.Facing) == wall)];
            if (onWall.Count == 0)
            {
                throw new RoomLibraryException(
                    $"room \"{marker.Name}\" declares water on its {wall} wall ({WaterKeyPrefix}{wall}), but its {wall} wall has no door plug.");
            }

            RoomSocket named = onWall[0];
            float sill = RoomLinter.SealBox(definition, named, definition.CellSize).Mins.Z;
            if (water.Level <= sill)
            {
                throw new RoomLibraryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"room \"{marker.Name}\" declares water at {water.Level:0.###} on its {wall} wall, at or below the door's sill ({sill:0.###});")
                    + " water that does not reach the doorway needs no water socket.");
            }

            sockets[named.Name] = water;
        }

        return sockets;
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

    private static bool IsSkyboxMarker(VmfChunk entity) =>
        string.Equals(entity.GetValue("classname"), SkyboxEntity, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The skybox room's marker: its name and corner, on the library's grid
    /// and kit (the first room's, which every room shares): a skybox is a
    /// cell like any room's, so the split owns its brushes the same way.
    /// </summary>
    private static Marker ReadSkyboxMarker(VmfChunk entity, Marker grid)
    {
        string where = VmfPlacement.Origin(entity) is { } at
            ? $"the {SkyboxEntity} at ({Fmt(at)})"
            : $"{SkyboxEntity} {VmfPlacement.IdOf(entity)}";
        Vec3 corner = VmfPlacement.Origin(entity)
            ?? throw new RoomLibraryException($"{where} has no origin; it stands at the skybox's cell's low corner.");
        string name = Utf8(entity.GetValue(NameKey)
            ?? throw new RoomLibraryException($"{where} has no \"{NameKey}\"; the skybox room is named like any room."));
        if (RoomNames.Problem(name) is { } problem)
        {
            throw new RoomLibraryException($"{where}: the room name \"{name}\" {problem}.");
        }

        return new Marker(name, corner, grid.CellSize, grid.Kit, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// The skybox room's own rules: no socket (it is never joined), exactly
    /// one <c>sky_camera</c> (the engine draws the skybox from it), and no
    /// entity that names a neighbour or a socket (<c>room_needs</c>,
    /// <c>room_socket</c>): it has neither.
    /// </summary>
    private static void CheckSkybox(RoomDefinition definition, List<VmfChunk> entities)
    {
        if (definition.Sockets.Count > 0)
        {
            throw new RoomLibraryException(
                $"the skybox room \"{definition.Name}\" has a door plug on its {WallName(definition.Sockets[0].Facing)} wall;"
                + " the skybox is never joined, so it has no sockets.");
        }

        int cameras = entities.Count(e => string.Equals(e.GetValue("classname"), RoomLibraryEntities.SkyCameraClass, StringComparison.Ordinal));
        if (cameras != 1)
        {
            throw new RoomLibraryException(
                $"the skybox room \"{definition.Name}\" has {cameras} sky_camera entities; the engine draws a skybox from exactly one.");
        }

        foreach (VmfChunk entity in entities)
        {
            foreach (string key in (ReadOnlySpan<string>)[RoomNeeds.Key, RoomStaticProps.SocketKey])
            {
                if (entity.GetValue(key) is not null)
                {
                    throw new RoomLibraryException(
                        $"room {definition.Name}: entity {VmfPlacement.IdOf(entity)} ({entity.GetValue("classname") ?? "no classname"}) has {key},"
                        + " but the skybox room has no neighbours and no sockets.");
                }
            }
        }
    }

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

        Dictionary<string, RoomWaterSocket> waterWalls = new(StringComparer.Ordinal);
        foreach (VmfKey key in entity.Keys)
        {
            if (!key.Name.StartsWith(WaterKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string wall = key.Name[WaterKeyPrefix.Length..].ToLowerInvariant();
            if (wall is not ("east" or "west" or "north" or "south"))
            {
                throw new RoomLibraryException(
                    $"{who} has a key \"{key.Name}\"; a water socket is declared by {WaterKeyPrefix}east, {WaterKeyPrefix}west, {WaterKeyPrefix}north or {WaterKeyPrefix}south.");
            }

            waterWalls[wall] = ParseWaterSocket(key, who);
        }

        // The room's label on the level map (the rooms design, 18.1): display
        // text, carried as written, held to the contract's length.
        string mapLabel = entity.GetValue(SourceSharp.RoomContracts.LevelMap.LabelKey) is { } labelText ? Utf8(labelText) : string.Empty;
        if (!SourceSharp.RoomContracts.LevelMap.IsLabel(mapLabel))
        {
            throw new RoomLibraryException(RoomMapView.LabelProblem(who, mapLabel));
        }

        return new Marker(name, corner, cell, kit, socketNames)
        {
            Role = role, Height = Height(entity, name, cell, kit), WaterWalls = waterWalls, MapLabel = mapLabel,
        };
    }

    /// <summary>A <c>water_&lt;wall&gt;</c> key's value: a finite height, then a material.</summary>
    private static RoomWaterSocket ParseWaterSocket(VmfKey key, string who)
    {
        string text = key.Value.Trim();
        int space = text.IndexOf(' ', StringComparison.Ordinal);
        string material = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        if (space < 0 || material.Length == 0 || material.Contains(' ', StringComparison.Ordinal)
            || !float.TryParse(text[..space], NumberStyles.Float, CultureInfo.InvariantCulture, out float level) || !float.IsFinite(level))
        {
            throw new RoomLibraryException(
                $"{who}: \"{key.Name}\" is \"{key.Value}\"; a water socket is a height and a water material, such as \"48 nature/water_canals_cheap001\".");
        }

        return new RoomWaterSocket(level, Utf8(material));
    }

    /// <summary>
    /// A room's <c>room_height</c>: the cell size without the key, else a
    /// whole number of units the door fits in and the engine's coordinates
    /// hold (<see cref="RoomDefinition.HeightProblem"/>), refused with the
    /// rooms design's 17.3 texts.
    /// </summary>
    /// <remarks>
    /// The text is read as a number first, so <c>"tall"</c> and <c>"300.5"</c>
    /// are both "not a whole number of units", quoting what the author wrote;
    /// the other two rules name the number. A key equal to the cell size is
    /// a cube, the same room as one without the key.
    /// </remarks>
    private static float Height(VmfChunk entity, string room, float cell, SocketKit kit)
    {
        if (entity.GetValue(RoomHeightKey) is not { } text)
        {
            return cell;
        }

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float height)
            || !float.IsFinite(height) || height != MathF.Floor(height))
        {
            throw new RoomLibraryException($"room {room}: {RoomHeightKey} \"{text}\" is not a whole number of units.");
        }

        return RoomDefinition.HeightProblem(height, kit) is { } problem
            ? throw new RoomLibraryException($"room {room}: {RoomHeightKey} {problem}")
            : height;
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
        /// <summary>
        /// The room's box in the library: the cell's footprint, as tall as the
        /// room (17.6), so a tall room may not overlap the room built above it,
        /// and a brush above a low room's ceiling belongs to no room.
        /// </summary>
        public Box Cell => new(Corner, Corner + new Vec3(CellSize, CellSize, Height));

        /// <summary>The room's height: the cell size unless its <c>room_height</c> says otherwise.</summary>
        public float Height { get; init; } = CellSize;

        public RoomRole Role { get; init; }

        public Dictionary<string, RoomWaterSocket> WaterWalls { get; init; } = new(StringComparer.Ordinal);

        public string MapLabel { get; init; } = string.Empty;
    }
}
