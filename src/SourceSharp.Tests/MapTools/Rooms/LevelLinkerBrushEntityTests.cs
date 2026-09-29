//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomBrushHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Brush entities through the link (section 4.1 of the rooms design): each
/// placed room's brush entities linked as their own models, world-coordinate
/// and origin-relative, with their trees, faces, brushes and collision
/// records moved with the placement at every quarter turn, their
/// <c>model</c> keys renumbered, and the level agreeing with the flattened
/// level's vbsp compile.
/// </summary>
public sealed class LevelLinkerBrushEntityTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>The hub's brush entities: a door, a hinged door, a trigger and a wall brush.</summary>
    private static (int, VmfChunk)[] HubBrushes =>
    [
        (0, Door(700, new Vec3(100, 100, 16), new Vec3(132, 132, 64), ("targetname", "hub_door"))),
        (0, Rotating(701, new Vec3(40, 40, 16), new Vec3(56, 90, 100), new Vec3(44, 44, 20), ("targetname", "hub_hinged"))),
        (0, Trigger(702, new Vec3(150, 40, 16), new Vec3(200, 90, 100), ("targetname", "hub_trigger"))),
        (0, Brush("func_brush", 703, new Vec3(60, 160, 16), new Vec3(80, 200, 48), keys: ("targetname", "hub_wall"))),
    ];

    /// <summary>
    /// A level of a hub with four brush entities of several classes and
    /// another room with one, both turned: the linked map holds every one
    /// as its own model, and agrees with the flattened level's compile in
    /// what a game observes of each (class, keys, world bounds, faces per
    /// material), in traces against each model at its origin, and in each
    /// model's collision convexes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task LinkAndFlattenAgreeOnBrushEntitiesAtEveryRotation(int rotation)
    {
        VmfDocument library = RoomPropHarness.Library(
        [
            .. HubBrushes,
            (1, Rotating(710, new Vec3(150, 150, 16), new Vec3(200, 166, 90), new Vec3(196, 156, 20), ("targetname", "other_hinged"))),
        ]);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{(rotation + 90) % 360}");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(6, BspStructView.Count<DModel>(linked.Bsp[BspLump.Models]));
        Assert.Equal(Observed(flat), Observed(linked.Bsp));
        foreach (string name in new[] { "hub_door", "hub_hinged", "hub_trigger", "hub_wall", "other_hinged" })
        {
            Placed ours = Named(linked.Bsp, "targetname", name);
            Placed theirs = Named(flat, "targetname", name);
            Assert.Equal(Traces(flat, theirs), Traces(linked.Bsp, ours));
            List<Box> a = Convexes(flat, theirs);
            List<Box> b = Convexes(linked.Bsp, ours);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.True(LinkedBrushProbe.Near(a[i], b[i], 0.05f), $"{name}: convex {i} {a[i]} against {b[i]}");
            }
        }
    }
}
