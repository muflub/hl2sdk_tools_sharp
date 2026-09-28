//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomNamingFacts;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The resolver's view of an entity from each path (a compiled lump, a
/// turned link entity, a VMF chunk), its key lookups, and the names summary
/// <c>ssmap rooms</c> prints.
/// </summary>
public class LevelEntityTests
{
    /// <summary>A VMF entity reads in the order vbsp compiles it: keys reversed (a repeat only changes the first), then its outputs in order.</summary>
    [Fact]
    public void AVmfEntityReadsInCompiledOrder()
    {
        VmfChunk chunk = new(MapFileLoader.EntityChunk);
        chunk.AddKey("id", "4");
        chunk.AddKey("classname", "logic_relay");
        chunk.AddKey("targetname", "a");
        chunk.AddKey("TargetName", "b");
        VmfChunk connections = chunk.AddChunk(MapFileLoader.ConnectionsChunk);
        connections.AddKey("OnTrigger", Out("x", "Use"));
        connections.AddKey("OnTrigger", Out("y", "Use"));
        LevelEntity entity = LevelEntity.FromVmf(chunk, 2, 3);
        Assert.Equal([("targetname", "b"), ("classname", "logic_relay"), ("id", "4"), ("OnTrigger", Out("x", "Use")), ("OnTrigger", Out("y", "Use"))], Keys(entity));
        Assert.Equal([false, false, false, true, true], entity.Pairs.Select(p => p.IsConnection));
        Assert.Same(chunk, entity.Payload);
        Assert.Equal((2, 3, "4"), (entity.Placement, entity.Source, entity.Id));
    }

    /// <summary>A compiled entity and a turned one keep their keys in order; a position key carries its turned position, not text.</summary>
    [Fact]
    public void CompiledAndTurnedEntitiesKeepTheirKeys()
    {
        BspEntity bsp = new();
        bsp.Pairs.Add(new BspKeyValue("classname", "info_target"));
        bsp.Pairs.Add(new BspKeyValue("hammerid", "9"));
        LevelEntity fromBsp = LevelEntity.FromBsp(bsp, 0, 1);
        Assert.Equal([("classname", "info_target"), ("hammerid", "9")], Keys(fromBsp));
        Assert.Equal("9", fromBsp.Id);

        RoomLinkPair origin = new("origin", null, new Vec3(1, 2, 3));
        LevelEntity turned = LevelEntity.FromLink(new RoomLinkEntity(false, [new RoomLinkPair("classname", "x", default), origin], null, null), 1, 2);
        Assert.Equal(origin, turned.Pairs[1].Position);
        Assert.Null(turned.Pairs[1].Value);
        Assert.Equal("?", turned.Id);
        Assert.Equal("x", turned.ClassName);
    }

    /// <summary>Keys are found ignoring case, the class spelt exactly; setting a key changes its first pair or appends one.</summary>
    [Fact]
    public void KeysAreFoundAndSet()
    {
        LevelEntity entity = Ent(0, 0, "ClassName", "wrong", "classname", "logic_branch", "InitialValue", "1");
        Assert.Equal("logic_branch", entity.ClassName);
        Assert.Equal("1", entity.Get("initialvalue"));
        Assert.Null(entity.TargetName);
        entity.Set("INITIALVALUE", "0");
        entity.Set("targetname", "t");
        Assert.Equal([("ClassName", "wrong"), ("classname", "logic_branch"), ("InitialValue", "0"), ("targetname", "t")], Keys(entity));
        Assert.Equal(string.Empty, Ent(0, 0, "origin", "0 0 0").ClassName);
    }

    /// <summary>The summary of a room of diagonal references and warnings; a room of no names summarises to nothing.</summary>
    [Fact]
    public void TheSummaryListsDiagonalsAndWarnings()
    {
        RoomNameTurn turn = RoomNameAnalysis.Analyse("hub", Room(
            ["classname", "logic_auto", "hammerid", "3", "OnMapSpawn", Out("cx+1ry-1_door", "Use"), "OnMapSpawn", Out("cxry_gone", "Use")])(-1), null)[0];
        RoomNameSummary summary = RoomNameSummary.Of(turn);
        Assert.Equal(new Dictionary<RoomDirection, int> { [RoomDirection.SouthEast] = 1 }, summary.NeighbourReferences);
        Assert.Equal(
            "  neighbour references: southeast (1); dropped with a warning where the level leaves that cell empty\n"
            + "  warning: room hub: entity 3 (logic_auto) key \"OnMapSpawn\" names cxry_gone, which no entity of the room defines.\n",
            summary.Describe());
        Assert.Equal("cx-1ry+1_", RoomNameSummary.Placeholder(-1, 1));
        Assert.Equal("cxry_", RoomNameSummary.Placeholder(0, 0));

        RoomNameSummary empty = RoomNameSummary.Of(RoomNameAnalysis.Analyse("hub", Room(["classname", "light"])(-1), null)[0]);
        Assert.True(empty.IsEmpty);
        Assert.Equal(string.Empty, empty.Describe());
        Assert.Equal(0, empty.WrittenEdictsBound(false));
    }

    /// <summary>
    /// Without <c>-mod-entities</c>, a hub test on a direction the room also
    /// names as a flag reuses that flag's branch: one branch, both callers.
    /// </summary>
    [Fact]
    public void TheStockHubReusesAFlagBranch()
    {
        LevelResolution level = Resolve(false,
        [
            new Placed("hub", 0, 0, 0, Room(
                ["classname", "logic_branch_listener", "Branch01", "cxry_has_east"],
                ["classname", "func_button", "targetname", "b", "OnPressed", Out("cxry_room", "TestEast")])),
        ]);
        Assert.Single(level.Entities, e => e.TargetName == "c0r0_has_east");
        Assert.Equal("c0r0_has_east", Outputs(Named(level, "b")!, "OnPressed").Single().Target);
    }
}
