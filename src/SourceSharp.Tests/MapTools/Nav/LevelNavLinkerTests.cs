//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// Stitching placed rooms: joined doors connect, capped doors and doors too
/// small for a preset do not, points of interest land where they should (and
/// one in a capped doorway is refused), obstacles take the level's names, and
/// the file is the same every time.
/// </summary>
public sealed class LevelNavLinkerTests(NavRoomsFixture fixture) : IClassFixture<NavRoomsFixture>
{
    private static Nav3dReader Read(Nav3dLevel level) => Nav3dReader.Open(Nav3dWriter.Write(level));

    /// <summary>The components holding the standing floor leaves of two cells, for a preset.</summary>
    private static (int A, int B) Components(Nav3dReader nav, int preset, int cellA, int cellB)
    {
        int Of(int cell)
        {
            for (int l = 0; l < nav.LeafCount; l++)
            {
                if (nav.LeafColumn(l).Cell == cell && nav.Component(preset, l) >= 0 && nav.Leaf(l).IsGrounded(Nav3dClipClass.Player))
                {
                    return nav.Component(preset, l);
                }
            }

            throw new InvalidOperationException($"cell {cell} has no floor leaf for preset {preset}");
        }

        return (Of(cellA), Of(cellB));
    }

    [Fact]
    public void TwoRoomsJoinedThroughADoorAreOneComponentPerPreset()
    {
        Nav3dReader nav = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0)), 2, 1));
        foreach (int preset in new[] { NavRoomsFixture.StandingAgent, NavRoomsFixture.FlyerAgent })
        {
            (int a, int b) = Components(nav, preset, 0, 1);
            Assert.Equal(a, b);
            Assert.Equal(1, nav.ComponentCount(preset));
        }

        // The doorway's boundary leaves neighbour each other across the cell face, through the door.
        int here = nav.FindLeaf(0, 15, 7, 1);
        int there = nav.FindLeaf(1, 0, 7, 1);
        Assert.True(here >= 0 && there >= 0);
        bool through = false;
        foreach (Nav3dNeighbour neighbour in nav.Neighbours(here))
        {
            through |= neighbour.Leaf == there && neighbour.ThroughDoor && neighbour.Direction == Nav3dDirection.East;
        }

        Assert.True(through);
        Assert.True(nav.Door(0).Joined && nav.Door(1).Joined);
        Assert.Equal((1, 0), (nav.Door(0).Other, nav.Door(1).Other));
    }

    [Fact]
    public void TwoRoomsThroughACappedDoorAreTwoComponents()
    {
        // The second room's door faces the grid's edge, not the first room's.
        Nav3dReader nav = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("east", 1, 0, 0)), 2, 1));
        foreach (int preset in new[] { NavRoomsFixture.StandingAgent, NavRoomsFixture.FlyerAgent })
        {
            (int a, int b) = Components(nav, preset, 0, 1);
            Assert.NotEqual(a, b);
        }

        // Capped: the doorway is solid, so no leaf reaches the cell face there.
        Assert.Equal(-1, nav.FindLeaf(0, 15, 7, 1));
        Assert.False(nav.Door(0).Joined);
        for (int l = 0; l < nav.LeafCount; l++)
        {
            foreach (Nav3dNeighbour neighbour in nav.Neighbours(l))
            {
                Assert.False(neighbour.ThroughDoor);
            }
        }
    }

    [Fact]
    public void ADoorNarrowerThanAPresetDoesNotConnectForIt()
    {
        Nav3dReader nav = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0)), 2, 1));
        (int a, int b) = Components(nav, NavRoomsFixture.WideAgent, 0, 1);
        Assert.NotEqual(a, b);
        (a, b) = Components(nav, NavRoomsFixture.StandingAgent, 0, 1);
        Assert.Equal(a, b);

        // The door's leaves are there; the wide preset fits in none of them.
        int doorway = nav.FindLeaf(0, 15, 7, 1);
        Assert.Equal(-1, nav.FitTop(doorway, 112, 32, Nav3dClipClass.Npc));
        Assert.True(nav.FitTop(doorway, 32, 72, Nav3dClipClass.Player) >= 1);
    }

    [Fact]
    public void TurnedRoomsJoinWhereTheirTurnedDoorsMeet()
    {
        // The corner room's +x and +y doors, turned 90°, face +y and -x: the
        // east room west of it and the hall turned 90° north of it join it.
        LevelLayout layout = fixture.Layout(("east", 0, 1, 0), ("corner", 1, 1, 1), ("hall", 1, 2, 1));
        Nav3dReader nav = Read(fixture.Link(layout, 2, 3));
        Assert.Equal(3, NavInspector.Stats(nav, NavRoomsFixture.StandingAgent).RoomsInLargestComponent);
    }

    [Fact]
    public void ADoorPointIsMadePerDoorAndMarkedJoinedOrCapped()
    {
        Nav3dReader nav = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0), ("east", 1, 1, 0)), 2, 2));
        List<Nav3dPoi> doors = [.. Enumerable.Range(0, nav.PoiCount).Select(nav.Poi).Where(p => (p.Flags & Nav3dPoiFlags.Door) != 0)];

        // Three doors, one point each, for every preset (fit is the clearance's answer).
        Assert.Equal(3, doors.Count);
        Assert.All(doors, p => Assert.Equal(RoomPois.DoorType, p.Type));
        Assert.All(doors, p => Assert.Equal(0b111u, p.AgentMask));

        Nav3dPoi joined = doors.First(p => p.Cell == 0);
        Assert.True((joined.Flags & Nav3dPoiFlags.Joined) != 0);
        Assert.Equal(new Vec3(256, 128, 16), joined.Position);
        Assert.Equal(0f, joined.Yaw);
        Assert.Equal(0, joined.Door);
        Nav3dPoi capped = doors.First(p => p.Cell == 3);
        Assert.True((capped.Flags & Nav3dPoiFlags.Joined) == 0);
        Assert.Equal(new Vec3(512, 384, 16), capped.Position);

        // A joined door's point is in the doorway's leaf; a capped doorway is
        // solid, so its point is in none.
        int Index(Nav3dPoi p) => Enumerable.Range(0, nav.PoiCount).Single(i => nav.Poi(i) == p);
        Assert.True(nav.PoiLeaf(Index(joined)) >= 0);
        Assert.Equal(-1, nav.PoiLeaf(Index(capped)));
    }

    [Fact]
    public void AuthoredPointsMoveWithTheirRoomAndTheirNamesResolve()
    {
        AuthoredPoi guard = new("9", new Vec3(128, 64, 16), 0f, true, 0f, "patrol", "", "cx+1ry_guard", ["standing"]);
        RoomNav hall = RoomNavBuilder.Build(fixture.Definition("hall"), fixture.Room("hall").Bsp, [guard], RoomRole.None, NavRoomsFixture.Settings);
        LevelLayout layout = fixture.Layout(("east", 0, 1, 0), ("corner", 1, 1, 1), ("hall", 1, 2, 1));
        Nav3dReader nav = Read(fixture.Link(layout, 2, 3, room => room == "hall" ? hall : fixture.Nav(room)));
        Nav3dPoi placed = Enumerable.Range(0, nav.PoiCount).Select(nav.Poi).Single(p => p.Type == "patrol");

        // Turned 90° in cell (1, 2): (128, 64) goes to (256 − 64, 128), plus the cell.
        Assert.Equal(new Vec3(256 + 192, 512 + 128, 16), placed.Position);
        Assert.Equal(90f, placed.Yaw);

        // East in the room's frame is north in the level's at 90°.
        Assert.Equal("c1r3_guard", placed.Name);
        Assert.Equal((uint)((2 * 2) + 1), placed.Cell);
        int poi = Enumerable.Range(0, nav.PoiCount).Single(p => nav.Poi(p).Type == "patrol");
        Assert.True(nav.PoiLeaf(poi) >= 0);
    }

    /// <summary>
    /// A point of interest in a doorway the level caps stands inside the
    /// plug: the link refuses it, naming the point and the door. The same
    /// point in a joined doorway is fine.
    /// </summary>
    [Fact]
    public void APointInACappedDoorwayIsRefusedNamingThePointAndTheDoor()
    {
        AuthoredPoi sentry = new("42", new Vec3(248, 128, 16), 0f, true, 0f, "vantage", "", "cxry_sentry", ["standing"]);
        RoomNav east = RoomNavBuilder.Build(fixture.Definition("east"), fixture.Room("east").Bsp, [sentry], RoomRole.None, NavRoomsFixture.Settings);

        // Joined: the doorway is open, the point stands in it.
        Nav3dReader joined = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0)), 2, 1, room => room == "east" ? east : fixture.Nav(room)));
        int point = Enumerable.Range(0, joined.PoiCount).Single(p => joined.Poi(p).Type == "vantage");
        Assert.True(joined.PoiLeaf(point) >= 0);

        // Capped, at any turn: refused, naming both.
        foreach (int turn in new[] { 0, 1, 2, 3 })
        {
            LinkException refused = Assert.Throws<LinkException>(() =>
                fixture.Link(fixture.Layout(("east", 0, 0, turn)), 1, 1, room => east));
            Assert.Contains("room \"east\" at cell (0, 0): info_poi 42 \"cxry_sentry\" (vantage)", refused.Message, StringComparison.Ordinal);
            Assert.Contains("doorway of socket \"east\", which the level caps", refused.Message, StringComparison.Ordinal);
            Assert.Throws<LinkException>(() => LevelNavLinker.CheckCappedDoorways(fixture.Layout(("east", 0, 0, turn)), [east.Turned(turn)]));
        }

        // Just inside the room, in front of the plug, it is not in the doorway.
        AuthoredPoi inside = sentry with { Origin = new Vec3(232, 128, 16) };
        RoomNav clear = RoomNavBuilder.Build(fixture.Definition("east"), fixture.Room("east").Bsp, [inside], RoomRole.None, NavRoomsFixture.Settings);
        _ = fixture.Link(fixture.Layout(("east", 0, 0, 0)), 1, 1, room => clear);
        LevelNavLinker.CheckCappedDoorways(fixture.Layout(("east", 0, 0, 0)), [clear]);
    }

    [Fact]
    public void TheUpRoomsArrivalIsTheSpawnAndTheDownRoomsIsIndexedToo()
    {
        AuthoredPoi arrival = new("3", new Vec3(128, 128, 16), 180f, true, 0f, RoomPois.ArrivalType, "", null, []);
        RoomNav up = RoomNavBuilder.Build(fixture.Definition("east"), fixture.Room("east").Bsp, [arrival], RoomRole.Up, NavRoomsFixture.Settings);
        RoomNav down = RoomNavBuilder.Build(fixture.Definition("west"), fixture.Room("west").Bsp, [arrival], RoomRole.Down, NavRoomsFixture.Settings);
        Nav3dLevel level = fixture.Link(
            fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0)), 2, 1, room => room == "east" ? up : down);
        Nav3dReader nav = Read(level);
        Assert.Equal(0, nav.UpArrivalPoi);
        Assert.Equal(nav.UpArrivalPoi, nav.SpawnPoi);
        Assert.True(nav.DownArrivalPoi > 0);
        Assert.Equal(Nav3dRoomRole.Down, nav.Poi(nav.DownArrivalPoi).Role);
        Assert.True(nav.TryGetSpawn(out Vec3 position, out float yaw));
        Assert.Equal((new Vec3(128, 128, 16), 180f), (position, yaw));
        Assert.Equal((Nav3dRoomRole.Up, Nav3dRoomRole.Down), (nav.Cell(0).Role, nav.Cell(1).Role));

        Nav3dReader plain = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0)), 2, 1));
        Assert.Equal((-1, -1, -1), (plain.SpawnPoi, plain.UpArrivalPoi, plain.DownArrivalPoi));
        Assert.False(plain.TryGetSpawn(out _, out _));
    }

    [Fact]
    public void TheLevelsBytesAreTheSameEveryTimeAndWhetherTurnsAreStoredOrTurned()
    {
        LevelLayout layout = fixture.Layout(("east", 0, 1, 0), ("corner", 1, 1, 1), ("hall", 1, 2, 1), ("east", 0, 0, 1));
        byte[] first = Nav3dWriter.Write(fixture.Link(layout, 2, 3));
        byte[] again = Nav3dWriter.Write(fixture.Link(layout, 2, 3));
        Assert.Equal(first, again);

        // Turning at link, or reading a stored turn (built and written ahead),
        // gives one file.
        Dictionary<(string, int), RoomNav> stored = [];
        foreach (string room in new[] { "west", "corner", "hall", "east" })
        {
            for (int t = 0; t < 4; t++)
            {
                stored[(room, t)] = RoomNavSection.Read(RoomNavSection.Write(fixture.Nav(room).Turned(t), NavCompression.None))!;
            }
        }

        byte[] fromStored = Nav3dWriter.Write(LevelNavLinker.Link(layout, 2, 3, (room, turn) => stored[(room, turn)], null, Guid.Empty));
        Assert.Equal(first, fromStored);
    }

    [Fact]
    public void RoomsBuiltWithOtherSettingsOrOutsideTheGridAreRefused()
    {
        LevelLayout layout = fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0));
        RoomNav coarse = RoomNavBuilder.Build(fixture.Definition("west"), fixture.Room("west").Bsp, [], RoomRole.None,
            NavRoomsFixture.Settings with { FloorNormalZ = 0.5f });
        Assert.Contains("other settings", Assert.Throws<LinkException>(() =>
            fixture.Link(layout, 2, 1, room => room == "west" ? coarse : fixture.Nav(room))).Message, StringComparison.Ordinal);
        RoomNav steps = fixture.Nav("west") with { StepHeight = 12 };
        Assert.Contains("traversal limits", Assert.Throws<LinkException>(() =>
            fixture.Link(layout, 2, 1, room => room == "west" ? steps : fixture.Nav(room))).Message, StringComparison.Ordinal);
        Assert.Contains("outside the 1 x 1 grid", Assert.Throws<LinkException>(() => fixture.Link(layout, 1, 1)).Message, StringComparison.Ordinal);
        Assert.Throws<LinkException>(() =>
            LevelNavLinker.Link(layout with { Rooms = [] }, 2, 1, (room, turn) => fixture.Nav(room), null, Guid.Empty));
    }

    [Fact]
    public void ARoomHandedAtTheWrongTurnIsRefused()
    {
        LevelLayout layout = fixture.Layout(("east", 0, 0, 1));
        Assert.Contains("turn 0", Assert.Throws<LinkException>(() =>
            LevelNavLinker.Link(layout, 1, 1, (room, turn) => fixture.Nav(room), null, Guid.Empty)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdsCellsPresetsAndLimitsAreCarried()
    {
        Guid pack = Guid.Parse("11111111-2222-8333-8444-555555555555");
        Guid levelId = Guid.Parse("aaaaaaaa-bbbb-8ccc-8ddd-eeeeeeeeeeee");
        LevelLayout layout = fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0));
        Nav3dReader nav = Read(LevelNavLinker.Link(layout, 3, 1, (room, turn) => fixture.Nav(room).Turned(turn), pack, levelId));
        Assert.Equal((pack, levelId), (nav.PackId, nav.LevelId));
        Assert.True(nav.MatchesMap(levelId.ToString()));
        Assert.False(nav.MatchesMap(pack.ToString()));
        Assert.False(nav.MatchesMap(null));
        Assert.Equal(new Nav3dCell("east", 0, Nav3dRoomRole.None, 0b0001, 0), nav.Cell(0));
        Assert.Equal(new Nav3dCell("west", 0, Nav3dRoomRole.None, 0b0100, 0), nav.Cell(1));
        Assert.Null(nav.Cell(2).Room);
        Assert.Equal(-1, nav.CellRoot(2));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(700, 100, 50)));
        Assert.Equal(new Nav3dPreset("wide", 112, 32, Nav3dClipClass.Npc), nav.Preset(NavRoomsFixture.WideAgent));
        Assert.Equal((18f, 56f, 100f), (nav.StepHeight, nav.JumpHeight, nav.JumpDistance));
    }

    /// <summary>
    /// A room's dynamic obstacles take the level's names (a room-local name
    /// resolved for the cell and turn, as the link's naming does), move to the
    /// cell, and the records of a second room name the level's index of its
    /// own obstacles.
    /// </summary>
    [Fact]
    public void ObstaclesTakeTheLevelsNamesAndIndexes()
    {
        RoomDefinition east = RoomHarness_East;
        RoomDefinition west = RoomHarness_West;
        NavObstacleSource Door(string name) => new("func_door", name, 5, Nav3dObstacleKind.Door, [NavBrush.Box(new Vec3(100, 100, 16), new Vec3(116, 140, 120), 1)]);
        RoomNav a = NavTestRooms.Nav(east, NavTestRooms.Geometry(east, obstacles: [Door("cxry_gate")]));
        RoomNav b = NavTestRooms.Nav(west, NavTestRooms.Geometry(west, obstacles: [Door("gate_global"), Door("cx-1ry_back")]));
        Nav3dReader nav = NavTestRooms.Link((east, a, 0, 0, 0), (west, b, 1, 0, 1));
        Assert.Equal(3, nav.ObstacleCount);
        Assert.Equal(new Nav3dObstacle("c0r0_gate", "func_door", 0, 5, Nav3dObstacleKind.Door, new Vec3(100, 100, 16), new Vec3(116, 140, 120)), nav.Obstacle(0));
        Assert.Equal("gate_global", nav.Obstacle(1).Name);

        // The west room is turned 90° in cell (1, 0): its -x neighbour is the level's -y one.
        Assert.Equal("c1r-1_back", nav.Obstacle(2).Name);
        Assert.Equal(1u, nav.Obstacle(2).Cell);
        Assert.Equal(new Vec3(256 + 256 - 140, 100, 16), nav.Obstacle(2).Mins);

        // Each obstacle names only leaves of its own cell.
        for (int o = 0; o < 3; o++)
        {
            Assert.NotEmpty(nav.ObstacleLeaves(o).ToArray());
            Assert.All(nav.ObstacleLeaves(o).ToArray(), l => Assert.Equal((int)nav.Obstacle(o).Cell, nav.LeafColumn(l).Cell));
        }
    }

    private static RoomDefinition RoomHarness_East => Rooms.RoomHarness.WalkableRoom("east", RoomFacing.PositiveX);

    private static RoomDefinition RoomHarness_West => Rooms.RoomHarness.WalkableRoom("west", RoomFacing.NegativeX);
}
