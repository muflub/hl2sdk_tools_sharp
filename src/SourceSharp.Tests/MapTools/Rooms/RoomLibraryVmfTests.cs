//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The room library VMF: every room of a set in one map, each marked by an
/// <c>info_room</c> at its cell's low corner, split into room-local rooms
/// with their definitions; and every way a library can be refused.
/// </summary>
public sealed class RoomLibraryVmfTests
{
    private static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    private static RoomDefinition End => RoomHarness.WalkableRoom("end", RoomFacing.PositiveX);

    // ---- splitting --------------------------------------------------------------------

    /// <summary>
    /// A library of two rooms splits into two, in <c>info_room</c> order:
    /// each with its name, grid and kit from the entity, its sockets found
    /// from its plugs and named for their walls, its corner, and its own VMF
    /// moved to the cell origin with the library's worldspawn keys and
    /// version, and without the <c>info_room</c>.
    /// </summary>
    [Fact]
    public void ALibrarySplitsIntoItsRooms()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, End);
        library.GetChunk("world")!.AddKey("skyname", "sky_day01_01");
        VmfChunk version = new("versioninfo");
        version.AddKey("formatversion", "100");
        library.Chunks.Insert(0, version);

        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);

        Assert.Equal(["hub", "end"], rooms.Select(r => r.Definition.Name));
        Assert.Equal(new Vec3(0, 0, 0), rooms[0].Corner);
        Assert.Equal(new Vec3(RoomHarness.Cell + RoomHarness.LibraryGap, 0, 0), rooms[1].Corner);
        Assert.Equal(RoomHarness.WalkableKit, rooms[1].Definition.Kit);
        Assert.Equal(RoomHarness.Cell, rooms[1].Definition.CellSize);
        Assert.Equal(["east", "west", "north", "south"], rooms[0].Definition.Sockets.Select(s => s.Name));
        Assert.Equal(
            [RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY],
            rooms[0].Definition.Sockets.Select(s => s.Facing));
        Assert.Equal([new RoomSocket(RoomFacing.PositiveX, "east")], rooms[1].Definition.Sockets);

        foreach (LibraryRoom room in rooms)
        {
            VmfChunk world = room.Document.GetChunk("world")!;
            Assert.Equal("sky_day01_01", world.GetValue("skyname"));
            Assert.Equal("100", room.Document.GetChunk("versioninfo")!.GetValue("formatversion"));
            Assert.Equal(
                RoomHarness.BuildRoomModel(room.Definition).GetChunk("world")!.GetChunks("solid").Count(),
                world.GetChunks("solid").Count());
            foreach (VmfChunk solid in world.GetChunks("solid"))
            {
                Assert.True(VmfPlacement.Bounds(solid).ContainsWithin(new Box(Vec3.Zero, new Vec3(256, 256, 256)), 0));
            }

            VmfChunk start = Assert.Single(room.Document.GetChunks("entity"));
            Assert.Equal("info_player_start", start.GetValue("classname"));
            Assert.Equal("128 128 129", start.GetValue("origin"));
        }
    }

    /// <summary>
    /// A library without a <c>versioninfo</c> splits into rooms without one;
    /// point entities in the gaps, or with no origin at all, belong to no
    /// room and are left out; a brush entity inside a cell belongs to it.
    /// </summary>
    [Fact]
    public void EntitiesBelongByWhereTheyAre()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, End);
        library.Chunks.Add(Entity("light", "300 128 128"));  // in the gap between the cells
        library.Chunks.Add(Entity("info_null", null));         // nowhere
        VmfChunk detail = Entity("func_detail", null);
        detail.Children.Add(Box(340, 20, 20, 360, 40, 40));   // inside the second cell
        library.Chunks.Add(detail);

        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);

        Assert.Null(rooms[0].Document.GetChunk("versioninfo"));
        Assert.Single(rooms[0].Document.GetChunks("entity"));
        List<VmfChunk> second = [.. rooms[1].Document.GetChunks("entity")];
        Assert.Equal(["info_player_start", "func_detail"], second.Select(e => e.GetValue("classname")));
        Assert.Equal(new Vec3(20, 20, 20), VmfPlacement.Bounds(second[1].GetChunk("solid")!).Mins);
    }

    /// <summary>A socket can be given a name of its own by its wall's <c>socket_</c> key.</summary>
    [Fact]
    public void ASocketCanBeNamed()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk marker = Marker(library, 0);
        marker.AddKey("socket_east", "front_door");
        marker.AddKey("SOCKET_North", "hatch");

        RoomDefinition hub = RoomLibraryVmf.Split(library)[0].Definition;

        Assert.Equal(["front_door", "west", "hatch", "south"], hub.Sockets.Select(s => s.Name));
    }

    [Fact]
    public void WallNamesAreTheCompassPoints()
    {
        Assert.Equal(["east", "west", "north", "south"], Enum.GetValues<RoomFacing>().Select(RoomLibraryVmf.WallName));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomLibraryVmf.WallName((RoomFacing)4));
        Assert.Throws<ArgumentNullException>(() => RoomLibraryVmf.Split(null!));
    }

    /// <summary>A name read from a file's bytes is UTF-8 when its bytes are, and Latin-1 when they are not; text built in memory is kept.</summary>
    [Theory]
    [InlineData("salle-Ã©", "salle-é")]
    [InlineData("salle-é", "salle-é")]
    [InlineData("hub", "hub")]
    [InlineData("中", "中")]
    public void ANameIsDecodedAsUtf8WhenItIsUtf8(string read, string name) =>
        Assert.Equal(name, RoomLibraryVmf.Utf8(read));

    // ---- refusals ---------------------------------------------------------------------

    /// <summary>Every way an <c>info_room</c> can fail to describe a room, named.</summary>
    [Theory]
    [InlineData("origin", null, "info_room 800000 has no origin")]
    [InlineData("name", null, "the info_room at (0 0 0) has no \"name\"")]
    [InlineData("name", "../x", "the info_room at (0 0 0): the room name \"../x\" starts with '.'")]
    [InlineData("door_width", null, "room \"hub\" has no \"door_width\"; the info_room keys are name, cell_size, door_width, door_height and wall_depth")]
    [InlineData("cell_size", "big", "room \"hub\": \"cell_size\" is \"big\", not a number")]
    [InlineData("wall_depth", "0", "room \"hub\": \"wall_depth\" is \"0\", not a positive finite number")]
    [InlineData("door_height", "-5", "room \"hub\": \"door_height\" is \"-5\", not a positive finite number")]
    [InlineData("door_width", "NaN", "room \"hub\": \"door_width\" is \"NaN\", not a positive finite number")]
    [InlineData("door_width", "3000000", "room \"hub\": the door kit")]
    [InlineData("door_width", "300", "does not fit in a 256-unit cell")]
    [InlineData("wall_depth", "128", "does not fit in a 256-unit cell")]
    [InlineData("socket_up", "x", "room \"hub\" has a key \"socket_up\"; a socket is named by socket_east, west, north or south")]
    [InlineData("socket_east", " ", "room \"hub\" names its east socket with an empty \"socket_east\"")]
    public void ABadInfoRoomIsRefused(string key, string? value, string expected)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk marker = Marker(library, 0);
        VmfKey? existing = marker.Keys.FirstOrDefault(k => k.Name == key);
        if (existing is not null)
        {
            marker.Children.Remove(existing);
        }

        if (value is not null)
        {
            marker.AddKey(key, value);
        }

        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>What is wrong between the rooms of a library, named.</summary>
    [Fact]
    public void TheRoomsOfALibraryMustAgreeAndStayApart()
    {
        // No world, no room at all.
        Assert.Contains("the library has no world chunk", Refused(new VmfDocument()), StringComparison.Ordinal);
        VmfDocument bare = RoomHarness.BuildRoomModel(Hub);
        Assert.Contains("the library has no info_room entity", Refused(bare), StringComparison.Ordinal);

        // Two rooms of one name, ignoring case.
        VmfDocument twins = RoomHarness.LibraryVmf(Hub, Hub with { Name = "HUB" });
        Assert.Contains("two rooms are named \"hub\" and \"HUB\"", Refused(twins), StringComparison.Ordinal);

        // Two grids.
        VmfDocument grids = RoomHarness.LibraryVmf(Hub, End);
        Marker(grids, 1).Keys.Single(k => k.Name == "door_width").Value = "64";
        Assert.Contains("room \"end\" is built for cell 256 and kit", Refused(grids), StringComparison.Ordinal);
        Assert.Contains("every room of a library shares one grid and one door kit", Refused(grids), StringComparison.Ordinal);

        // A kit a player cannot walk through.
        VmfDocument low = RoomHarness.LibraryVmf(RoomHarness.Room("low", RoomFacing.PositiveX));
        Assert.Contains(
            "the library's door kit (door_width 96, door_height 96, wall_depth 16, cell_size 256) puts the door's sill at z = 80",
            Refused(low),
            StringComparison.Ordinal);

        // Overlapping cells.
        VmfDocument overlap = RoomHarness.LibraryVmf(Hub, End);
        Marker(overlap, 1).Keys.Single(k => k.Name == "origin").Value = "128 0 0";
        Assert.Contains("the cells of rooms \"hub\" and \"end\" overlap", Refused(overlap), StringComparison.Ordinal);
    }

    /// <summary>
    /// Geometry belongs to one room or the library is refused: a world brush
    /// in the gaps, one across a cell's edge, or a brush entity across two
    /// cells, each named with where it is.
    /// </summary>
    [Theory]
    [InlineData("gap", "world brush 900 at (270 0 0)-(300 16 16) is not inside any room's cell")]
    [InlineData("edge", "world brush 900 at (240 0 0)-(300 16 16) is not inside any room's cell")]
    [InlineData("entity", "entity 901 (func_detail) has brush 900 at (270 0 0)-(300 16 16) outside its room's cell")]
    [InlineData("split entity", "entity 901 (func_detail) has brush 900 at (330 0 0)-(340 16 16) outside its room's cell")]
    public void GeometryOutsideTheRoomsIsRefused(string where, string expected)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, End);
        switch (where)
        {
            case "gap":
                library.GetChunk("world")!.Children.Add(Box(270, 0, 0, 300, 16, 16));
                break;
            case "edge":
                library.GetChunk("world")!.Children.Add(Box(240, 0, 0, 300, 16, 16));
                break;
            case "entity":
                VmfChunk gap = Entity("func_detail", null);
                gap.Children.Add(Box(270, 0, 0, 300, 16, 16));
                library.Chunks.Add(gap);
                break;
            case "split entity":
                VmfChunk split = Entity("func_detail", null);
                split.Children.Add(Box(10, 0, 0, 20, 16, 16));
                split.Children.Add(Box(330, 0, 0, 340, 16, 16));
                library.Chunks.Add(split);
                break;
        }

        Assert.Contains(expected, Refused(library), StringComparison.Ordinal);
    }

    /// <summary>What is wrong with a room's plugs, named.</summary>
    [Fact]
    public void ARoomsPlugsMustBeOnePerWallAndNamedOnlyWhereTheyAre()
    {
        // A name for a wall with no plug.
        VmfDocument unplugged = RoomHarness.LibraryVmf(End);
        Marker(unplugged, 0).AddKey("socket_west", "back");
        Assert.Contains(
            "room \"end\" names its west socket \"back\" (socket_west), but its west wall has no door plug: no world brush fills (0 80 16)-(16 176 240)",
            Refused(unplugged),
            StringComparison.Ordinal);

        // Two plugs on one wall.
        VmfDocument doubled = RoomHarness.LibraryVmf(End);
        doubled.GetChunk("world")!.Children.Add(Box(240, 80, 16, 256, 176, 240));
        Assert.Contains("room \"end\" has 2 door plugs on its east wall; a wall has one socket", Refused(doubled), StringComparison.Ordinal);

        // Two sockets given one name.
        VmfDocument same = RoomHarness.LibraryVmf(Hub);
        Marker(same, 0).AddKey("socket_east", "door");
        Marker(same, 0).AddKey("socket_west", "door");
        Assert.Contains("room \"hub\" has two sockets named \"door\"", Refused(same), StringComparison.Ordinal);

        // A name that collides with another wall's default.
        VmfDocument collide = RoomHarness.LibraryVmf(Hub);
        Marker(collide, 0).AddKey("socket_east", "west");
        Assert.Contains("room \"hub\" has two sockets named \"west\"", Refused(collide), StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------------------

    private static string Refused(VmfDocument library) =>
        Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library)).Message;

    private static VmfChunk Marker(VmfDocument library, int index) =>
        library.GetChunks("entity").Where(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity).ElementAt(index);

    private static VmfChunk Entity(string classname, string? origin)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", "901");
        entity.AddKey("classname", classname);
        if (origin is not null)
        {
            entity.AddKey("origin", origin);
        }

        return entity;
    }

    /// <summary>A box brush, id 900, from its corners.</summary>
    private static VmfChunk Box(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        VmfChunk solid = new(MapFileLoader.SolidChunk);
        solid.AddKey("id", "900");
        string P(float x, float y, float z) => $"({VmfPlacement.Format(new Vec3(x, y, z))})";
        foreach (string plane in new[]
        {
            $"{P(x0, y1, z1)} {P(x1, y1, z1)} {P(x1, y0, z1)}",
            $"{P(x0, y0, z0)} {P(x1, y0, z0)} {P(x1, y1, z0)}",
            $"{P(x0, y1, z0)} {P(x0, y0, z0)} {P(x0, y0, z1)}",
            $"{P(x1, y0, z1)} {P(x1, y0, z0)} {P(x1, y1, z0)}",
            $"{P(x1, y1, z1)} {P(x1, y1, z0)} {P(x0, y1, z0)}",
            $"{P(x0, y0, z1)} {P(x0, y0, z0)} {P(x1, y0, z0)}",
        })
        {
            VmfChunk side = solid.AddChunk(MapFileLoader.SideChunk);
            side.AddKey("plane", plane);
            side.AddKey("material", RoomHarness.Plain);
            side.AddKey("uaxis", "[1 0 0 0] 0.25");
            side.AddKey("vaxis", "[0 -1 0 0] 0.25");
        }

        return solid;
    }

    /// <summary>
    /// A room's label on the level map is its <c>info_room</c>'s
    /// <c>map_label</c>, empty without the key; a label longer than the
    /// contract's 64 bytes of UTF-8 is refused, naming the room.
    /// </summary>
    [Fact]
    public void ARoomsMapLabelIsReadAndHeldToTheContract()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, RoomHarness.WalkableRoom("side", RoomFacing.PositiveX));
        TransitHarness.Marker(library, "hub").AddKey(SourceSharp.RoomContracts.LevelMap.LabelKey, "Armoury");
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        Assert.Equal(["Armoury", string.Empty], rooms.Select(r => r.MapLabel));

        string tooLong = new('x', 65);
        TransitHarness.Marker(library, "side").AddKey(SourceSharp.RoomContracts.LevelMap.LabelKey, tooLong);
        Assert.Equal(
            $"room \"side\" has map_label \"{tooLong}\"; a label is at most 64 bytes of UTF-8, without a NUL.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library)).Message);
    }
}
