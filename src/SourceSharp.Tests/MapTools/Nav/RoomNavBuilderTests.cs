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
/// A room's navigation from its compile: a turned room gives the turned
/// answer bit for bit, doorways let through exactly the agents that fit, caps
/// change what the plug changes and compose, and points of interest are
/// checked against the presets they apply to.
/// </summary>
public sealed class RoomNavBuilderTests(NavRoomsFixture fixture) : IClassFixture<NavRoomsFixture>
{
    private static AuthoredPoi Poi(Vec3 at, string type = "cover", IReadOnlyList<string>? agents = null, bool facing = true, string? name = null) =>
        new("7", at, 30f, facing, 64f, type, "a,b", name, agents ?? []);

    /// <summary>A room navigation's open grid, as dense voxels.</summary>
    internal static NavGrid Grid(RoomNav nav, IEnumerable<NavCapChange>? changes = null)
    {
        NavRecordTable table = new();
        for (int r = 1; r < nav.Records.Count; r++)
        {
            Assert.Equal(r, table.Add(nav.Records[r]));
        }

        NavVoxelKey[] keys = nav.Columns.Expand(nav.CellVoxels);
        foreach (NavCapChange change in changes ?? [])
        {
            keys[change.Voxel] = change.Key;
        }

        int n = nav.CellVoxels;
        return new NavGrid(new NavRegion(0, 0, 0, n, n, n, nav.VoxelSize), table, keys);
    }

    /// <summary>A run or a capped voxel with its records as their bytes: what two builds agree on whatever order their tables are in.</summary>
    private static string Bytes(RoomNav nav, int at, int height, NavVoxelKey key) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{at}+{height} {Convert.ToHexString(nav.Records[key.PlayerRecord])} {Convert.ToHexString(nav.Records[key.NpcRecord])} {key.Flags} {key.Cost} {key.PlayerFloorZ:R} {key.NpcFloorZ:R}");

    private static bool Fits(NavGrid grid, int x, int y, int z, NavAgentSpec agent) => RoomNavBuilder.Fits(grid, [], x, y, z, agent);

    /// <summary>
    /// Compiling the corner room turned, and building its navigation, gives
    /// exactly the stored turn-0 navigation turned: every record, every
    /// column's runs, every capped key, every door point and every point of
    /// interest, bit for bit, at all four turns.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ComputingOnATurnedRoomEqualsTurningTheStoredNavigation(int turns)
    {
        AuthoredPoi poi = Poi(new Vec3(120, 100, 16), agents: ["standing"]);
        RoomNav stored = RoomNavBuilder.Build(
            fixture.Definition("corner"), fixture.Room("corner").Bsp, [poi], RoomRole.None, NavRoomsFixture.Settings);

        (var vmf, RoomDefinition definition) = NavRoomsFixture.Turned(fixture.Vmf("corner"), fixture.Definition("corner"), turns);
        RoomObject compiled = await NavRoomsFixture.CompileAsync(vmf, definition);
        RoomTransform transform = new(new RoomPlacement("corner", 0, 0, turns), 256);
        AuthoredPoi turnedPoi = poi with { Origin = transform.Apply(poi.Origin), Yaw = RoomNav.TurnYaw(poi.Yaw, turns) };
        RoomNav direct = RoomNavBuilder.Build(definition, compiled.Bsp, [turnedPoi], RoomRole.None, NavRoomsFixture.Settings);
        RoomNav turned = stored.Turned(turns);

        // The record tables hold the same records; each numbers them in the
        // order its build met them, so keys are compared by record bytes.
        Assert.Equal(direct.Records.Count, turned.Records.Count);
        Assert.Equal(direct.Records.Select(Convert.ToHexString).Order(StringComparer.Ordinal),
            turned.Records.Select(Convert.ToHexString).Order(StringComparer.Ordinal));
        Assert.Equal(turns, turned.Turn);
        Assert.Equal(direct.Columns.ColumnStarts, turned.Columns.ColumnStarts);
        Assert.Equal(direct.Columns.Runs.Select(r => Bytes(direct, r.ZLo, r.Height, r.Key)), turned.Columns.Runs.Select(r => Bytes(turned, r.ZLo, r.Height, r.Key)));
        for (int s = 0; s < definition.Sockets.Count; s++)
        {
            Assert.Equal(direct.SocketData[s].Capped.Select(c => Bytes(direct, c.Voxel, 0, c.Key)), turned.SocketData[s].Capped.Select(c => Bytes(turned, c.Voxel, 0, c.Key)));
            Assert.Equal(direct.SocketData[s].DoorPoint, turned.SocketData[s].DoorPoint);
        }

        Assert.Equal(direct.Pois.Single().Position, turned.Pois.Single().Position);
        Assert.Equal(direct.Pois.Single().Yaw, turned.Pois.Single().Yaw);
        Assert.Equal(direct.Obstacles, turned.Obstacles);
        Assert.Empty(turned.Brushes);
    }

    [Fact]
    public void ADoorwayLetsThroughTheAgentsThatFitAndNotOnesThatDoNot()
    {
        RoomNav nav = fixture.Nav("east");
        NavGrid grid = Grid(nav);
        NavAgentSpec standing = NavRoomsFixture.Settings.Agents[NavRoomsFixture.StandingAgent];
        NavAgentSpec wide = NavRoomsFixture.Settings.Agents[NavRoomsFixture.WideAgent];

        // Standing: origin y from 96 to 160 (voxels 6 to 9) and feet from 16
        // to 168 (voxels 1 to 9), in the boundary layer x = 15.
        List<(int Y, int Z)> fits = [];
        for (int z = 0; z < 16; z++)
        {
            for (int y = 0; y < 16; y++)
            {
                if (Fits(grid, 15, y, z, standing))
                {
                    fits.Add((y, z));
                }

                // A 112-wide agent cannot pass a 96-wide door.
                Assert.False(Fits(grid, 15, y, z, wide));
            }
        }

        Assert.Equal(36, fits.Count);
        Assert.All(fits, v => Assert.True(v.Y is >= 6 and <= 9 && v.Z is >= 1 and <= 9, v.ToString()));
    }

    [Fact]
    public void CappingADoorFillsItsDoorwayAndBringsTheWallCloserInFrontOfIt()
    {
        RoomNav nav = fixture.Nav("east");
        IReadOnlyList<NavCapChange> capped = nav.SocketData[0].Capped;
        NavGrid open = Grid(nav);
        NavGrid shut = Grid(nav, capped);
        NavAgentSpec standing = NavRoomsFixture.Settings.Agents[NavRoomsFixture.StandingAgent];
        Assert.Contains(capped, c => c.Voxel == (((1 * 16) + 7) * 16) + 15 && c.Key.IsSolid);
        Assert.True(Fits(open, 14, 7, 1, standing));
        Assert.False(Fits(shut, 14, 7, 1, standing));

        // Two voxels in, the plug is 16 away: exactly the standing agent's half-width, so it still fits.
        Assert.True(Fits(shut, 13, 7, 1, standing));
        Assert.Equal(capped.OrderBy(c => c.Voxel), capped);

        // Every change is near the door: within the opening's half-width (48)
        // and a voxel of the wall, where the jambs no longer dominate the plug.
        Assert.All(capped, c => Assert.True(c.Voxel % 16 >= 16 - 5, $"voxel {c.Voxel}"));
    }

    /// <summary>
    /// Caps compose: a room alone in a level has every socket capped, and the
    /// link merges the sockets' capped keys voxel by voxel; the result is
    /// exactly the room built with every door shut, run for run and record
    /// for record.
    /// </summary>
    [Theory]
    [InlineData("east")]
    [InlineData("hall")]
    [InlineData("corner")]
    public void EveryCapMergedIsTheRoomBuiltWithEveryDoorShut(string name)
    {
        RoomDefinition definition = fixture.Definition(name);
        Nav3dReader linked = Nav3dReader.Open(Nav3dWriter.Write(fixture.Link(fixture.Layout((name, 0, 0, 0)), 1, 1)));

        NavGeometry geometry = NavGeometry.FromBsp(fixture.Room(name).Bsp);
        List<NavBrush> outside = RoomNavBuilder.Outside(definition with { Sockets = [] }, -1);
        NavGrid whole = NavClearanceBuilder.Build(geometry.With(outside), new NavRegion(0, 0, 0, 16, 16, 16, 16), NavRoomsFixture.Settings);
        NavColumns columns = NavColumns.Of(whole);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                NavRun[] expected = columns.Column(x, y).ToArray();
                List<int> leaves = [.. Enumerable.Range(0, linked.LeafCount).Where(l => linked.LeafColumn(l) == (0, x, y))];
                Assert.Equal(expected.Length, leaves.Count);
                for (int i = 0; i < leaves.Count; i++)
                {
                    Nav3dLeaf leaf = linked.Leaf(leaves[i]);
                    NavRun run = expected[i];
                    Assert.Equal((run.ZLo, run.Height, run.Key.Flags, run.Key.Cost, run.Key.PlayerFloorZ, run.Key.NpcFloorZ),
                        (leaf.ZLo, leaf.Height, leaf.Flags, leaf.Cost, leaf.PlayerFloorZ, leaf.NpcFloorZ));
                    byte[] player = whole.Records[run.Key.PlayerRecord];
                    byte[] npc = whole.Records[run.Key.NpcRecord];
                    Assert.True(player.AsSpan().SequenceEqual(linked.ClearanceRecord(leaves[i], Nav3dClipClass.Player)[..player.Length]), $"column {x} {y}");
                    Assert.True(npc.AsSpan().SequenceEqual(linked.ClearanceRecord(leaves[i], Nav3dClipClass.Npc)[..npc.Length]), $"column {x} {y}");
                }
            }
        }
    }

    [Fact]
    public void ADoorPointIsOnTheFaceAtTheOpeningsMiddleOnItsFloor()
    {
        RoomNav nav = fixture.Nav("corner");
        Assert.Equal(new Vec3(256, 128, 16), nav.SocketData[0].DoorPoint);
        Assert.Equal(new Vec3(128, 256, 16), nav.SocketData[1].DoorPoint);
        Assert.Equal(new Vec3(128, 0, 16), RoomNavBuilder.DoorPoint(
            RoomHarnessDefinitions.South, new RoomSocket(RoomFacing.NegativeY, "south"), Grid(fixture.Nav("east"))));
    }

    [Fact]
    public void APointOfInterestIsStoredWithItsMaskAndFields()
    {
        RoomNav nav = RoomNavBuilder.Build(
            fixture.Definition("east"), fixture.Room("east").Bsp,
            [Poi(new Vec3(128, 128, 16), agents: ["flyer"], name: "cxry_guard"), Poi(new Vec3(128, 128, 60))],
            RoomRole.Up, NavRoomsFixture.Settings);
        Assert.Equal(RoomRole.Up, nav.Role);
        Assert.Equal(1u << NavRoomsFixture.FlyerAgent, nav.Pois[0].AgentMask);
        Assert.Equal("cxry_guard", nav.Pois[0].Name);
        Assert.Equal("7", nav.Pois[0].EntityId);
        Assert.Equal(0b111u, nav.Pois[1].AgentMask);
        Assert.Equal((30f, true, 64f, "cover", "a,b"), (nav.Pois[1].Yaw, nav.Pois[1].HasFacing, nav.Pois[1].Radius, nav.Pois[1].Type, nav.Pois[1].Tags));
    }

    [Fact]
    public void APointOfInterestInSolidIsRefusedNamingItsEntityAndAgent()
    {
        RoomLintException refused = Assert.Throws<RoomLintException>(() => RoomNavBuilder.Build(
            fixture.Definition("east"), fixture.Room("east").Bsp, [Poi(new Vec3(128, 128, 8))], RoomRole.None, NavRoomsFixture.Settings));
        Assert.Contains("info_poi 7", refused.Message, StringComparison.Ordinal);
        Assert.Contains("agent \"standing\" does not fit", refused.Message, StringComparison.Ordinal);

        // Near the ceiling the flyer fits and the standing agent does not.
        _ = RoomNavBuilder.Build(fixture.Definition("east"), fixture.Room("east").Bsp,
            [Poi(new Vec3(128, 128, 200), agents: ["flyer"])], RoomRole.None, NavRoomsFixture.Settings);
        Assert.Throws<RoomLintException>(() => RoomNavBuilder.Build(fixture.Definition("east"), fixture.Room("east").Bsp,
            [Poi(new Vec3(128, 128, 200), agents: ["standing"])], RoomRole.None, NavRoomsFixture.Settings));
    }

    [Fact]
    public void APointNamingAnUnknownAgentIsRefused()
    {
        RoomLintException refused = Assert.Throws<RoomLintException>(() => RoomNavBuilder.Build(
            fixture.Definition("east"), fixture.Room("east").Bsp, [Poi(new Vec3(128, 128, 16), agents: ["tank"])],
            RoomRole.None, NavRoomsFixture.Settings));
        Assert.Contains("agent \"tank\"", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArrivalIsThePlayersOnTheFloorAndNeedsAPlayerAgent()
    {
        RoomNav nav = RoomNavBuilder.Build(fixture.Definition("east"), fixture.Room("east").Bsp,
            [Poi(new Vec3(128, 128, 16), RoomPois.ArrivalType)], RoomRole.Up, NavRoomsFixture.Settings);
        Assert.True(nav.Pois[0].IsArrival);
        Assert.Equal(1u << NavRoomsFixture.StandingAgent, nav.Pois[0].AgentMask);

        Assert.Contains("off the floor", Assert.Throws<RoomLintException>(() => RoomNavBuilder.Build(
            fixture.Definition("east"), fixture.Room("east").Bsp, [Poi(new Vec3(128, 128, 64), RoomPois.ArrivalType)],
            RoomRole.Up, NavRoomsFixture.Settings)).Message, StringComparison.Ordinal);
        Assert.Contains("alone", Assert.Throws<RoomLintException>(() => RoomNavBuilder.Build(
            fixture.Definition("east"), fixture.Room("east").Bsp, [Poi(new Vec3(128, 128, 16), RoomPois.ArrivalType, ["flyer"])],
            RoomRole.Up, NavRoomsFixture.Settings)).Message, StringComparison.Ordinal);
        NavSettings noPlayer = NavRoomsFixture.Settings with { Agents = NavSettings.ParseAgents("flyer 32 32 npc") };
        Assert.Contains("no agent", Assert.Throws<RoomLintException>(() => RoomNavBuilder.Build(
            fixture.Definition("east"), fixture.Room("east").Bsp, [Poi(new Vec3(128, 128, 16), RoomPois.ArrivalType)],
            RoomRole.Up, noPlayer)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVoxelThatDoesNotDivideTheCellIsRefused()
    {
        Assert.Throws<RoomLibraryException>(() => RoomNavBuilder.Build(
            fixture.Definition("east"), fixture.Room("east").Bsp, [], RoomRole.None, NavRoomsFixture.Settings with { VoxelSize = 15 }));
    }

    [Fact]
    public void ACoarserVoxelBuildsAndTurnsToo()
    {
        RoomNav nav = RoomNavBuilder.Build(
            fixture.Definition("corner"), fixture.Room("corner").Bsp, [], RoomRole.None, NavSettings.Default with { VoxelSize = 32 });
        Assert.Equal(8, nav.CellVoxels);
        Assert.Equal(nav.Columns.Runs, nav.Turned(4).Columns.Runs);
        Assert.Same(nav, nav.Turned(0));
        Assert.Equal(nav.Turned(1).Turned(3).Columns.Runs, nav.Columns.Runs);
        Assert.Equal(nav.Turned(2).Columns.Runs, nav.Turned(1).Turned(1).Columns.Runs);
    }
}

/// <summary>Room definitions the builder facts need beyond the fixture's.</summary>
internal static class RoomHarnessDefinitions
{
    public static RoomDefinition South => Rooms.RoomHarness.WalkableRoom("south", RoomFacing.NegativeY);
}
