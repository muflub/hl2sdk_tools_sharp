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
/// small for an agent do not, the points of interest land where they
/// should, and the file is the same every time.
/// </summary>
public sealed class LevelNavLinkerTests(NavRoomsFixture fixture) : IClassFixture<NavRoomsFixture>
{
    private static Nav3dReader Read(Nav3dLevel level) => Nav3dReader.Open(Nav3dWriter.Write(level));

    /// <summary>The components holding leaves of the two cells, for an agent.</summary>
    private static (uint A, uint B) Components(Nav3dReader nav, int agent, int cellA, int cellB)
    {
        uint Of(int cell)
        {
            for (int l = 0; l < nav.LeafCount(agent); l++)
            {
                Nav3dLeaf leaf = nav.Leaf(agent, l);
                if (leaf.Cell == cell && (leaf.Flags & Nav3dLeafFlags.Floor) != 0)
                {
                    return leaf.Component;
                }
            }

            throw new InvalidOperationException($"cell {cell} has no floor leaf for agent {agent}");
        }

        return (Of(cellA), Of(cellB));
    }

    [Fact]
    public void TwoRoomsJoinedThroughADoorAreOneComponentPerAgent()
    {
        Nav3dReader nav = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0)), 2, 1));
        foreach (int agent in new[] { NavRoomsFixture.StandingAgent, NavRoomsFixture.FlyerAgent })
        {
            (uint a, uint b) = Components(nav, agent, 0, 1);
            Assert.Equal(a, b);
            Assert.Equal(1, nav.ComponentCount(agent));
            Assert.True(nav.LinkCount(agent) > 0);
            Nav3dDoorLink link = nav.Link(agent, 0);
            Assert.True((nav.Leaf(agent, (int)link.LeafA).Flags & Nav3dLeafFlags.Door) != 0);
            bool through = false;
            foreach (Nav3dNeighbour neighbour in nav.Neighbours(agent, (int)link.LeafA))
            {
                through |= neighbour.Leaf == (int)link.LeafB && neighbour.ThroughDoor;
            }

            Assert.True(through);
        }

        Assert.True(nav.Door(0).Joined && nav.Door(1).Joined);
        Assert.Equal((1, 0), (nav.Door(0).Other, nav.Door(1).Other));
    }

    [Fact]
    public void TwoRoomsThroughACappedDoorAreTwoComponents()
    {
        // The second room's door faces the grid's edge, not the first room's.
        Nav3dReader nav = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("east", 1, 0, 0)), 2, 1));
        foreach (int agent in new[] { NavRoomsFixture.StandingAgent, NavRoomsFixture.FlyerAgent })
        {
            (uint a, uint b) = Components(nav, agent, 0, 1);
            Assert.NotEqual(a, b);
            Assert.Equal(0, nav.LinkCount(agent));
        }

        // Capped: the doorway is solid, so no leaf reaches the cell face there.
        Assert.Equal(-1, nav.FindLeaf(NavRoomsFixture.StandingAgent, 0, 15, 7, 1));
        Assert.False(nav.Door(0).Joined);
    }

    [Fact]
    public void ADoorNarrowerThanAnAgentDoesNotConnectForIt()
    {
        Nav3dReader nav = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0)), 2, 1));
        (uint a, uint b) = Components(nav, NavRoomsFixture.WideAgent, 0, 1);
        Assert.NotEqual(a, b);
        Assert.Equal(0, nav.LinkCount(NavRoomsFixture.WideAgent));
        (a, b) = Components(nav, NavRoomsFixture.StandingAgent, 0, 1);
        Assert.Equal(a, b);
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
    public void DoorPointsAreMadePerAgentThatFitsAndMarkedJoinedOrCapped()
    {
        Nav3dReader nav = Read(fixture.Link(fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0), ("east", 1, 1, 0)), 2, 2));
        List<Nav3dPoi> doors = [.. Enumerable.Range(0, nav.PoiCount).Select(nav.Poi).Where(p => (p.Flags & Nav3dPoiFlags.Door) != 0)];

        // Three doors, two agents each fit (the wide one fits none).
        Assert.Equal(6, doors.Count);
        Assert.All(doors, p => Assert.Equal(RoomPois.DoorType, p.Type));
        Assert.DoesNotContain(doors, p => p.AgentMask == 1u << NavRoomsFixture.WideAgent);

        Nav3dPoi joined = doors.First(p => p.Cell == 0);
        Assert.True((joined.Flags & Nav3dPoiFlags.Joined) != 0);
        Assert.Equal(new Vec3(256, 128, 16), joined.Position);
        Assert.Equal(0f, joined.Yaw);
        Nav3dPoi capped = doors.First(p => p.Cell == 3);
        Assert.True((capped.Flags & Nav3dPoiFlags.Joined) == 0);
        Assert.Equal(new Vec3(512, 384, 16), capped.Position);

        // A joined door's point is in the doorway's leaf; a capped doorway is
        // solid, so its point is in none.
        int Index(Nav3dPoi p) => Enumerable.Range(0, nav.PoiCount).Single(i => nav.Poi(i) == p);
        Assert.True(nav.PoiLeaf(NavRoomsFixture.StandingAgent, Index(joined)) >= 0);
        Assert.Equal(-1, nav.PoiLeaf(NavRoomsFixture.StandingAgent, Index(capped)));
        Assert.Equal(-1, nav.PoiLeaf(NavRoomsFixture.FlyerAgent, Index(joined)));
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
        Assert.True(nav.PoiLeaf(NavRoomsFixture.StandingAgent, poi) >= 0);
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
                stored[(room, t)] = RoomNavSection.Read(RoomNavSection.Write(fixture.Nav(room).Turned(t), NavCompression.None));
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
    public void TheIdsAndCellsAreCarried()
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
        Assert.Equal(-1, nav.CellRoot(0, 2));
        Assert.Equal(-1, nav.FindLeaf(0, new Vec3(700, 100, 50)));
    }
}
