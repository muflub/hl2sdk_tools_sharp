//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// What the navigation reads from a compiled room: the world's brushes are
/// solid, a door's are its obstacle, a trigger's are nothing, a prop is its
/// model's hull, and ladders come from entities too.
/// </summary>
public sealed class NavGeometryTests
{
    private static VmfChunk Entity(string className, int id, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", className);
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    /// <summary>
    /// A room with a door, a trigger, two props (one whose model the content
    /// has, one it lacks) and both ladder entities, compiled; its geometry
    /// splits them as the navigation needs.
    /// </summary>
    [Fact]
    public async Task ACompiledRoomsGeometrySplitsTheWorldFromItsEntities()
    {
        RoomDefinition definition = RoomHarness.WalkableRoom("props", RoomFacing.PositiveX);
        VmfDocument vmf = RoomHarness.BuildRoomModel(definition);
        VmfChunk door = Entity("func_door", 800001, ("targetname", "cxry_gate"));
        door.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(96, 64, 16), new Vec3(112, 192, 176), 800002));
        VmfChunk trigger = Entity("trigger_multiple", 800003);
        trigger.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(160, 64, 16), new Vec3(192, 96, 64), 800004));
        vmf.Chunks.Add(door);
        vmf.Chunks.Add(trigger);
        vmf.Chunks.Add(Entity("prop_physics", 800005, ("model", "models/crate.mdl"), ("origin", "64 64 16"), ("angles", "0 90 0")));
        vmf.Chunks.Add(Entity("prop_physics", 800006, ("model", "models/missing.mdl"), ("origin", "64 192 16")));
        vmf.Chunks.Add(Entity("info_ladder", 800007, ("mins.x", "200"), ("mins.y", "100"), ("mins.z", "16"), ("maxs.x", "216"), ("maxs.y", "140"), ("maxs.z", "200")));
        vmf.Chunks.Add(Entity("func_useableladder", 800008, ("point0", "40 200 20"), ("point1", "40 200 180")));
        RoomObject room = await NavRoomsFixture.CompileAsync(vmf, definition);

        NavGeometry geometry = NavGeometry.FromBsp(room.Bsp, m => m == "models/crate.mdl" ? (new Vec3(-8, -4, 0), new Vec3(8, 4, 20)) : null);

        // The door: its brushes are its obstacle, never world.
        NavObstacleSource gate = geometry.Obstacles.Single(o => o.ClassName == "func_door");
        Assert.Equal(("cxry_gate", Nav3dObstacleKind.Door), (gate.TargetName, gate.Kind));
        Assert.Equal(new Box(new Vec3(96, 64, 16), new Vec3(112, 192, 176)), gate.Bounds);
        Assert.DoesNotContain(geometry.Brushes, b => b.MinX == 96 && b.MaxX == 112 && b.MinY == 64);
        Assert.DoesNotContain(geometry.Brushes, b => b.MinX == 160 && b.MaxX == 192);
        Assert.True(gate.HammerId > 0);

        // The prop turned 90°: its 16 × 8 hull is 8 × 16 at its origin.
        NavObstacleSource crate = geometry.Obstacles.Single(o => o.ClassName == "prop_physics");
        Assert.Equal((Nav3dObstacleKind.Physics, new Box(new Vec3(60, 56, 16), new Vec3(68, 72, 36))), (crate.Kind, crate.Bounds));
        Assert.Contains(geometry.Warnings, w => w.Contains("models/missing.mdl", StringComparison.Ordinal));

        // The ladders: info_ladder's box, and the useable ladder's climb widened by a player's half-width.
        Assert.Contains(new Box(new Vec3(200, 100, 16), new Vec3(216, 140, 200)), geometry.Ladders);
        Assert.Contains(new Box(new Vec3(24, 184, 20), new Vec3(56, 216, 180)), geometry.Ladders);

        // Without a hull source, props are left out, each with a warning naming its model.
        NavGeometry plain = NavGeometry.FromBsp(room.Bsp);
        Assert.Single(plain.Obstacles);
        Assert.Contains(plain.Warnings, w => w.Contains("models/crate.mdl", StringComparison.Ordinal));

        // The world's brushes: the room's own, less the door's and the trigger's.
        Assert.Equal(NavBrush.FromBsp(room.Bsp).Count - 2, geometry.Brushes.Count);
    }

    [Theory]
    [InlineData("func_door", Nav3dObstacleKind.Door)]
    [InlineData("FUNC_DOOR_ROTATING", Nav3dObstacleKind.Door)]
    [InlineData("prop_door_rotating", Nav3dObstacleKind.Door)]
    [InlineData("func_movelinear", Nav3dObstacleKind.Mover)]
    [InlineData("func_tracktrain", Nav3dObstacleKind.Mover)]
    [InlineData("func_brush", Nav3dObstacleKind.Toggle)]
    [InlineData("func_wall_toggle", Nav3dObstacleKind.Toggle)]
    [InlineData("func_breakable", Nav3dObstacleKind.Breakable)]
    [InlineData("func_breakable_surf", Nav3dObstacleKind.Breakable)]
    [InlineData("func_physbox", Nav3dObstacleKind.Physics)]
    [InlineData("prop_physics_multiplayer", Nav3dObstacleKind.Physics)]
    [InlineData("prop_dynamic_override", Nav3dObstacleKind.Prop)]
    public void DynamicClassesAreKnownByKind(string className, Nav3dObstacleKind kind) => Assert.Equal(kind, NavGeometry.KindOf(className));

    [Theory]
    [InlineData("func_detail")]
    [InlineData("trigger_multiple")]
    [InlineData("prop_static")]
    [InlineData("info_player_start")]
    public void OtherClassesAreNotObstacles(string className) => Assert.Null(NavGeometry.KindOf(className));

    [Fact]
    public void LadderEntitiesReadEitherSpellingAndAMalformedOneIsNone()
    {
        BspEntity vectors = new();
        vectors.Pairs.Add(new BspKeyValue("mins", "10 20 30"));
        vectors.Pairs.Add(new BspKeyValue("maxs", "0 40 50"));
        Assert.Equal(new Box(new Vec3(0, 20, 30), new Vec3(10, 40, 50)), NavGeometry.LadderBox(vectors));

        BspEntity half = new();
        half.Pairs.Add(new BspKeyValue("mins.x", "1"));
        Assert.Null(NavGeometry.LadderBox(half));
        Assert.Null(NavGeometry.UseableLadder(half));
    }

    [Theory]
    [InlineData("1 -2.5 3e1", true, 1f, -2.5f, 30f)]
    [InlineData("  4   5 6 ", true, 4f, 5f, 6f)]
    [InlineData("[1 2 3]", false, 0f, 0f, 0f)]
    [InlineData("1 2", false, 0f, 0f, 0f)]
    [InlineData("1 2 3 4", false, 0f, 0f, 0f)]
    [InlineData("1 x 3", false, 0f, 0f, 0f)]
    [InlineData(null, false, 0f, 0f, 0f)]
    public void AnEntityKeysVectorIsExactlyThreeNumbers(string? text, bool parses, float x, float y, float z)
    {
        Assert.Equal(parses, NavGeometry.TryVector(text, out Vec3 v));
        Assert.Equal(new Vec3(x, y, z), v);
    }

    [Fact]
    public void APropsBoxTurnsExactlyAtQuarterTurnsAndBoundsItsCornersOtherwise()
    {
        (Vec3, Vec3) hull = (new Vec3(-10, -2, 0), new Vec3(10, 2, 30));
        BspEntity prop = new();
        prop.Pairs.Add(new BspKeyValue("origin", "100 100 0"));
        prop.Pairs.Add(new BspKeyValue("angles", "0 180 0"));
        Assert.Equal(new Box(new Vec3(90, 98, 0), new Vec3(110, 102, 30)), NavGeometry.PropBox(prop, hull));
        prop.Pairs[1] = new BspKeyValue("angles", "0 45 0");
        Box turned = NavGeometry.PropBox(prop, hull)!.Value;
        Assert.Equal(12 / MathF.Sqrt(2), turned.Maxs.X - 100, 3);
        prop.Pairs[1] = new BspKeyValue("angles", "90 0 0");
        Assert.Equal(new Box(new Vec3(100, 98, -10), new Vec3(130, 102, 10)), NavGeometry.PropBox(prop, hull));
        BspEntity nowhere = new();
        Assert.Null(NavGeometry.PropBox(nowhere, hull));
    }

    [Fact]
    public void GeometryTakesMoreBrushesAndNamesItsOverhangsInOrder()
    {
        float r = MathF.Sqrt(0.5f);
        NavBrush overhang = NavBrush.FromPlanes([
            (new Vec3(1, 0, 0), 20f), (new Vec3(-1, 0, 0), 0f), (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f),
            (new Vec3(0, 0, 1), 30f), (new Vec3(r, 0, -r), -10f * r)], 1)!;
        NavBrush water = NavBrush.FromPlanes([
            (new Vec3(1, 0, 0), 20f), (new Vec3(-1, 0, 0), 0f), (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f),
            (new Vec3(0, 0, 1), 30f), (new Vec3(r, 0, -r), -10f * r)], 0x20)!;
        NavGeometry geometry = new NavGeometry { Brushes = [water, overhang] }.With([NavBrush.Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1), 1)]);
        Assert.Equal(3, geometry.Brushes.Count);
        Assert.Equal([overhang], NavClearanceBuilder.OverhangBrushes(geometry));
    }
}
