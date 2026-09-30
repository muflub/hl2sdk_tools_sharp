//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;

using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.TransitHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The level's map (the rooms design, 18.3): the link's, from the rooms'
/// map sections, is the flattened level's compile's by the same face rule,
/// byte for byte, at every turn; its doors are open where the joints are,
/// its markers are the authors' turned with their rooms and the linker's
/// own, its rooms carry their labels; a pack without the section links
/// without a map and says why; a map made without a level file is its
/// whole floor with its entities' markers.
/// </summary>
public sealed class LevelMapBuilderTests
{
    private const string Keys = "up_map: above\ndown_map: below\n";

    /// <summary>
    /// The transition library, with a marker in the up room, a label on the
    /// plain room, and a player-solid platform (a <c>func_brush</c>) in it.
    /// </summary>
    private static VmfDocument Library()
    {
        VmfDocument library = TransitHarness.Library(
            up: [.. UpEntities, Point("info_poi", 110, new Vec3(40, 40, 16), (LevelMap.MarkerKey, "shop"), (LevelMap.LabelKey, "Shop"), ("angles", "0 90 0"))],
            plain: [.. PlainEntities, RoomBrushHarness.Brush("func_brush", 310, new Vec3(40, 150, 16), new Vec3(90, 200, 48))]);
        Marker(library, "plain").AddKey(LevelMap.LabelKey, "Plain hall");
        return library;
    }

    private static LevelGrid Square(int turns)
    {
        int r = 90 * turns;
        return Level("mid", Keys, $"up@{r}, plain@{r}", $"plain@{r}, down@{r}");
    }

    /// <summary>
    /// The key equivalence: the level's map the link writes from the rooms'
    /// map sections is, byte for byte, the map <c>ssmap map2d</c> makes from
    /// the flattened level's compile cut by the level file, at every turn
    /// (the checksum set alike, since it binds each file to its own map).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheLinkedMapIsTheFlattenedCompilesAtEveryTurn(int turns)
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = Square(turns);
        LevelLayout layout = level.ToLayout(name => rooms.Find(name)?.Definition, rooms.CellSize, rooms.Kit);
        LevelMapPlan plan = LevelMapBuilder.Plan(layout, level.Columns, level.Rows, rooms.Get);
        Assert.Null(plan.Warning);
        Assert.True(plan.WritesMap);
        Map2dLevel linked = plan.Build(42);

        BspData flat = await CompileFlatAsync(library, level, mod: false);
        Map2dLevel compiled = LevelMapBuilder.FromCompile(flat, 42, level, [library]);
        Assert.Equal(Map2dWriter.Write(linked), Map2dWriter.Write(compiled));

        // And what it holds: four rooms, the plain rooms labelled; every room's
        // floor, the platform over the plain rooms' floor at its own band;
        // sixteen doors, eight of them open, each naming the room it opens into.
        Assert.Equal(["plain", "down", "up", "plain"], linked.Rooms.Select(r => r.Name));
        Assert.Equal(["Plain hall", string.Empty, string.Empty, "Plain hall"], linked.Rooms.Select(r => r.Label));
        Assert.Equal(4, linked.Rings.Count(r => (r.ZLow, r.ZHigh) == (16, 16) && !r.IsHole));
        Assert.Equal(2, linked.Rings.Count(r => (r.ZLow, r.ZHigh) == (48, 48)));
        Assert.Equal(16, linked.Doors.Length);
        Assert.Equal(8, linked.Doors.Count(d => d.Open));
        Assert.All(linked.Doors, d => Assert.Equal(d.Open, d.Neighbour >= 0));
        Assert.All(linked.Doors.Where(d => d.Open), d => Assert.Contains(linked.Doors, o => o.Placement == d.Neighbour && o.Neighbour == d.Placement
            && Math.Min(o.X0, o.X1) == Math.Min(d.X0, d.X1) && Math.Min(o.Y0, o.Y1) == Math.Min(d.Y0, d.Y1)));
    }

    /// <summary>
    /// The markers: the linker's first (the spawn at the up room's arrival,
    /// then per transition room its arrival and its exit, labelled with the
    /// map it leads to), then the authors', each turned with its room.
    /// </summary>
    [Fact]
    public async Task TheMarkersAreTheLinkersThenTheAuthorsTurnedWithTheirRooms()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        LevelGrid level = Level("mid", Keys, "up@90, plain", "plain, down@270");
        LevelLayout layout = level.ToLayout(name => rooms.Find(name)?.Definition, rooms.CellSize, rooms.Kit);
        Map2dLevel map = LevelMapBuilder.Plan(layout, level.Columns, level.Rows, rooms.Get).Build(0);

        // Placement 2 is the up room at cell (0, 1), turned a quarter: local
        // (x, y) is world (256 - y, 256 + x); placement 1 the down room at
        // (1, 0), turned three quarters: local (x, y) is world (256 + y, 256 - x).
        Assert.Equal(
            [
                new Map2dMarker(LevelMap.SpawnKind, string.Empty, 2, 256 - UpArrival.Y, 256 + UpArrival.X, 16, 90),
                new Map2dMarker(LevelMap.ArrivalKind, "below", 1, 256 + DownArrival.Y, 256 - DownArrival.X, 16, 0),
                new Map2dMarker(LevelMap.ExitDownKind, "below", 1, 256 + 128, 256 - 128, 48, 0),
                new Map2dMarker(LevelMap.ArrivalKind, "above", 2, 256 - UpArrival.Y, 256 + UpArrival.X, 16, 90),
                new Map2dMarker(LevelMap.ExitUpKind, "above", 2, 256 - 128, 256 + 128, 48, 0),
                new Map2dMarker("shop", "Shop", 2, 256 - 40, 256 + 40, 16, 180),
            ],
            map.Markers.AsEnumerable());
    }

    /// <summary>
    /// A level with <c>up: none</c> spawns in the spawn room, at its first
    /// spawn point: the map marks it there and has no up arrival or exit.
    /// </summary>
    [Fact]
    public async Task WithoutAnUpRoomTheSpawnIsTheSpawnRooms()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        LevelGrid level = Level("top", "up: none\ndown_map: next\n", "plain@180, down");
        LevelLayout layout = level.ToLayout(name => rooms.Find(name)?.Definition, rooms.CellSize, rooms.Kit);
        Map2dLevel map = LevelMapBuilder.Plan(layout, level.Columns, level.Rows, rooms.Get).Build(0);
        Assert.Equal(
            [LevelMap.SpawnKind, LevelMap.ArrivalKind, LevelMap.ExitDownKind],
            map.Markers.Select(m => m.Kind));
        Map2dMarker spawn = map.Markers[0];
        Assert.Equal((0, 256 - PlainSpawn.X, 256 - PlainSpawn.Y, 225f), (spawn.Placement, spawn.X, spawn.Y, spawn.Yaw));
    }

    /// <summary>A level without transitions (it places no role room and says nothing of them) has no linker markers, only the authors'.</summary>
    [Fact]
    public async Task ALevelWithoutTransitionsHasOnlyTheAuthorsMarkers()
    {
        RoomLibrary rooms = await CompileAsync(TransitHarness.Library(
            plain: [.. PlainEntities, Point("info_poi", 320, new Vec3(10, 20, 16), (LevelMap.MarkerKey, "chest"))]));
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "plain, plain"), "flat");
        LevelLayout layout = level.ToLayout(name => rooms.Find(name)?.Definition, rooms.CellSize, rooms.Kit);
        Map2dLevel map = LevelMapBuilder.Plan(layout, level.Columns, level.Rows, rooms.Get).Build(0);
        Assert.Equal(
            [new Map2dMarker("chest", string.Empty, 0, 10, 20, 16, 0), new Map2dMarker("chest", string.Empty, 1, 266, 20, 16, 0)],
            map.Markers.AsEnumerable());
    }

    /// <summary>
    /// A pack whose rooms have no map section (packed before the map) links
    /// without a map, and the plan says which rooms, once, in name order.
    /// </summary>
    [Fact]
    public async Task APackWithoutTheMapLinksWithoutOneAndSaysWhy()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        LevelGrid level = Square(0);
        LevelLayout layout = level.ToLayout(name => rooms.Find(name)?.Definition, rooms.CellSize, rooms.Kit);
        LevelMapPlan plan = LevelMapBuilder.Plan(
            layout, level.Columns, level.Rows, name => name == "up" ? rooms.Get(name) : rooms.Get(name) with { MapView = null });
        Assert.False(plan.WritesMap);
        Assert.Equal(
            "the room pack holds no level map for \"down\", \"plain\"; the level is linked without a .map2d (compile the library with a build that writes the map)",
            plan.Warning);
        Assert.Equal(
            "the level links without a map: " + plan.Warning,
            Assert.Throws<InvalidOperationException>(() => plan.Build(0)).Message);
    }

    /// <summary>
    /// A map made without a level file: its whole floor in its own frame, no
    /// placements, no doors; its <c>info_player_start</c> a spawn marker, and
    /// an entity with a <c>map_marker</c> a marker; an entity whose kind is
    /// not one, or without an origin, is not on the map.
    /// </summary>
    [Fact]
    public void AMapWithoutALevelFileIsItsWholeFloor()
    {
        Vec3 up = new(0, 0, 1);
        BspData bsp = new RoomMapFacesTests.FaceBsp()
            .Face(up, SurfaceFlags.None, RoomMapFacesTests.Floor(0, 0, 100, 100, 0))
            .Face(up, SurfaceFlags.None, RoomMapFacesTests.Floor(100, 0, 200, 100, 0))
            .Model()
            .Entity(("classname", "info_player_start"), ("origin", "10 20 1"), ("angles", "0 -90 0"))
            .Entity(("classname", "info_target"), ("origin", "50 60 0"), (LevelMap.MarkerKey, "exit_hatch"), (LevelMap.LabelKey, "Hatch"))
            .Entity(("classname", "info_target"), ("origin", "1 1 0"), (LevelMap.MarkerKey, "Bad"))
            .Entity(("classname", "info_target"), (LevelMap.MarkerKey, "nowhere"))
            .Build();
        Map2dLevel map = LevelMapBuilder.FromCompile(bsp, 7);
        Assert.Equal(7u, map.MapChecksum);
        Assert.Equal(0, map.CellSize);
        Assert.Empty(map.Rooms);
        Assert.Empty(map.Doors);
        Map2dRing ring = Assert.Single(map.Rings);
        Assert.Equal(new Map2dRing(-1, 0, 0, false, [new(0, 0), new(200, 0), new(200, 100), new(0, 100)]), ring);
        Assert.Equal(
            [new Map2dMarker(LevelMap.SpawnKind, string.Empty, -1, 10, 20, 1, 270), new Map2dMarker("exit_hatch", "Hatch", -1, 50, 60, 0, 0)],
            map.Markers.AsEnumerable());
    }

    /// <summary>A room-local point turned and moved on whole units is <see cref="RoomTransform.Apply"/>'s point, at every turn.</summary>
    [Fact]
    public void TheIntegerTurnIsTheTransforms()
    {
        foreach (int rotation in (ReadOnlySpan<int>)[0, 1, 2, 3])
        {
            RoomPlacement placement = new("r", 2, 5, rotation);
            RoomTransform transform = new(placement, 256);
            foreach (MapPoint p in (ReadOnlySpan<MapPoint>)[new(0, 0), new(16, 240), new(256, 7), new(-3, 300)])
            {
                Vec3 world = transform.Apply(new Vec3(p.X, p.Y, 0));
                Assert.Equal(new Map2dPoint((int)world.X, (int)world.Y), LevelMapBuilder.Turn(p, placement, 256));
            }
        }
    }

    /// <summary>A level whose cell size is not a whole number of units has no map: its turned polygons would not be whole.</summary>
    [Fact]
    public void AFractionalCellSizeHasNoMap()
    {
        LevelLayout layout = new("odd", 256.5f, RoomHarness.Kit, []);
        Assert.Equal(
            "level odd: the cell size 256.5 is not a whole number of units, which the level map's polygons need.",
            Assert.Throws<LinkException>(() => LevelMapBuilder.Assemble(layout, 0, 0, [], [], [], 0)).Message);
    }

    /// <summary>
    /// <c>ssmap map2d</c>'s level reading: a level whose libraries place no
    /// room of theirs is refused, as the link refuses it.
    /// </summary>
    [Fact]
    public void ALevelOfNoKnownRoomIsRefused()
    {
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "ghost, ~"), "empty");
        Assert.Equal(
            "level empty places no room of its libraries. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => LevelMapBuilder.FromCompile(new BspData(), 0, level, [Library()])).Message);
    }
}
