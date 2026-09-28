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
/// A room's navigation from its compile: turned rooms give the turned
/// answer, door portals and caps are where the kit says, and points of
/// interest are checked against the agents they apply to.
/// </summary>
public sealed class RoomNavBuilderTests(NavRoomsFixture fixture) : IClassFixture<NavRoomsFixture>
{
    private static AuthoredPoi Poi(Vec3 at, string type = "cover", IReadOnlyList<string>? agents = null, bool facing = true, string? name = null) =>
        new("7", at, 30f, facing, 64f, type, "a,b", name, agents ?? []);

    /// <summary>
    /// Compiling the corner room turned, and building its navigation, gives
    /// exactly the stored turn-0 navigation turned: the grids, every portal,
    /// every cap change and every point, at all four turns.
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

        Assert.Equal(turns, turned.Turn);
        for (int a = 0; a < NavRoomsFixture.Settings.Agents.Count; a++)
        {
            Assert.Equal(
                NavOctree.Expand(direct.AgentData[a].Nodes, direct.AgentData[a].Leaves, 16),
                NavOctree.Expand(turned.AgentData[a].Nodes, turned.AgentData[a].Leaves, 16));
            Assert.Equal(direct.AgentData[a].Nodes, turned.AgentData[a].Nodes);
            for (int s = 0; s < definition.Sockets.Count; s++)
            {
                Assert.Equal(direct.AgentData[a].Sockets[s].Portal, turned.AgentData[a].Sockets[s].Portal);
                Assert.Equal(direct.AgentData[a].Sockets[s].Capped, turned.AgentData[a].Sockets[s].Capped);
            }
        }

        Assert.Equal(direct.Pois.Single().Position, turned.Pois.Single().Position);
        Assert.Equal(direct.Pois.Single().Yaw, turned.Pois.Single().Yaw);
    }

    [Fact]
    public void ADoorPortalIsTheDoorwaysFreeVoxelsForAnAgentThatFitsAndEmptyForOneThatDoesNot()
    {
        RoomNav nav = fixture.Nav("east");

        // Standing: origin y from 96 to 160 (voxels 6 to 9) and feet from 16
        // to 168 (voxels 1 to 9), in the boundary layer x = 15.
        IReadOnlyList<NavVoxel> standing = nav.AgentData[NavRoomsFixture.StandingAgent].Sockets[0].Portal;
        Assert.Equal(36, standing.Count);
        Assert.All(standing, v => Assert.True(v.X == 15 && v.Y is >= 6 and <= 9 && v.Z is >= 1 and <= 9, v.ToString()));

        // A 112-wide agent cannot pass a 96-wide door.
        Assert.Empty(nav.AgentData[NavRoomsFixture.WideAgent].Sockets[0].Portal);
    }

    [Fact]
    public void CappingADoorBlocksItsDoorwayAndWallsTheVoxelsInFrontOfIt()
    {
        RoomNav nav = fixture.Nav("east");
        IReadOnlyList<NavCapChange> capped = nav.AgentData[NavRoomsFixture.StandingAgent].Sockets[0].Capped;
        Assert.Contains(capped, c => c.Voxel == new NavVoxel(15, 7, 1) && c.Blocks);
        Assert.Contains(capped, c => c.Voxel == new NavVoxel(14, 7, 1) && c.Blocks);
        Assert.Contains(capped, c => c.Voxel == new NavVoxel(13, 7, 1) && !c.Blocks
            && c.AddFlags == (Nav3dLeafFlags.Wall | Nav3dLeafFlags.SidePositiveX));
        Assert.Equal(capped.OrderBy(c => RoomNav.Index(c.Voxel, 16)), capped);
    }

    /// <summary>
    /// A cap is classified over the slab of voxels near its wall only; that
    /// gives exactly the changes classifying the whole cell capped gives, for
    /// every room, socket and agent (the wide one included, whose reach is
    /// the largest).
    /// </summary>
    [Theory]
    [InlineData("east")]
    [InlineData("hall")]
    [InlineData("corner")]
    public void TheCapsSlabGivesTheWholeCellsChanges(string name)
    {
        RoomDefinition definition = fixture.Definition(name);
        RoomNav nav = fixture.Nav(name);
        List<NavBrush> all = NavBrush.FromBsp(fixture.Room(name).Bsp);
        bool IsPlug(NavBrush b, int socket)
        {
            Box plug = RoomLinter.SealBox(definition, definition.Sockets[socket], 256);
            return Math.Abs(b.MinX - plug.Mins.X) < 0.01 && Math.Abs(b.MaxX - plug.Maxs.X) < 0.01
                && Math.Abs(b.MinY - plug.Mins.Y) < 0.01 && Math.Abs(b.MaxY - plug.Maxs.Y) < 0.01
                && Math.Abs(b.MinZ - plug.Mins.Z) < 0.01 && Math.Abs(b.MaxZ - plug.Maxs.Z) < 0.01;
        }

        NavRegion cell = new(0, 0, 0, 16, 16, 16, 16);
        for (int a = 0; a < NavRoomsFixture.Settings.Agents.Count; a++)
        {
            NavAgentSpec agent = NavRoomsFixture.Settings.Agents[a];
            List<NavBrush> fixedBrushes = [.. all.Where(b => !Enumerable.Range(0, definition.Sockets.Count).Any(s => IsPlug(b, s)))];
            NavVoxelGrid open = NavVoxeliser.Classify([.. fixedBrushes, .. RoomNavBuilder.Outside(definition, -1)], cell, agent, 0.7f);
            for (int s = 0; s < definition.Sockets.Count; s++)
            {
                NavVoxelGrid closed = NavVoxeliser.Classify(
                    [.. fixedBrushes, .. all.Where(b => IsPlug(b, s)), .. RoomNavBuilder.Outside(definition, s)], cell, agent, 0.7f);
                List<NavCapChange> whole = [];
                for (int i = 0; i < 4096; i++)
                {
                    ushort before = open.Cells[i];
                    ushort after = closed.Cells[i];
                    if (before != after)
                    {
                        NavVoxel v = new((byte)(i % 16), (byte)(i / 16 % 16), (byte)(i / 256));
                        whole.Add((after & NavVoxelGrid.FreeBit) == 0
                            ? new NavCapChange(v, true, Nav3dLeafFlags.None)
                            : new NavCapChange(v, false, (Nav3dLeafFlags)(after & ~before & 0xFF)));
                    }
                }

                Assert.True(whole.Count > 0 || nav.AgentData[a].Sockets[s].Portal.Count == 0);
                Assert.Equal(whole, nav.AgentData[a].Sockets[s].Capped);
            }
        }
    }

    [Fact]
    public void TheCapSlabIsTheLayersAnAgentCanReachFromTheWall()
    {
        NavRegion cell = new(0, 0, 0, 16, 16, 16, 16);
        NavAgentSpec standing = NavRoomsFixture.Settings.Agents[NavRoomsFixture.StandingAgent];
        Assert.Equal(cell with { OriginX = 192, SizeX = 4 }, RoomNavBuilder.CapRegion(cell, RoomFacing.PositiveX, 16, standing));
        Assert.Equal(cell with { SizeX = 4 }, RoomNavBuilder.CapRegion(cell, RoomFacing.NegativeX, 16, standing));
        Assert.Equal(cell with { OriginY = 192, SizeY = 4 }, RoomNavBuilder.CapRegion(cell, RoomFacing.PositiveY, 16, standing));
        Assert.Equal(cell with { SizeY = 4 }, RoomNavBuilder.CapRegion(cell, RoomFacing.NegativeY, 16, standing));
        Assert.Equal(cell with { SizeX = 16 }, RoomNavBuilder.CapRegion(cell, RoomFacing.NegativeX, 16, new NavAgentSpec("huge", 900, 10, 1)));
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
        Assert.Equal(nav.AgentData[0].Nodes, nav.Turned(4).AgentData[0].Nodes);
        Assert.Same(nav, nav.Turned(0));
        Assert.Equal(nav.Turned(1).Turned(3).AgentData[0].Nodes, nav.AgentData[0].Nodes);
    }
}
