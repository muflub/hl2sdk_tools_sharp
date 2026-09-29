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

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomSkyboxHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The 3D skybox (section 4.12 of the rooms design, open point O11): a
/// library's <c>info_room_skybox</c> room, compiled once like a socket-less
/// room, is placed by the link and the flatten below every level's grid,
/// never turned and never joined, as its own area, with its
/// <c>sky_camera</c>; the level's world bounds leave it out.
/// </summary>
public sealed class LevelLinkerSkyboxTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// A level of the hub and the other room, at every quarter turn, with
    /// the library's skybox: the linked map holds the skybox one cell below
    /// the south-west cell, unturned, its space an area of its own after
    /// the rooms' one, and its <c>sky_camera</c> moved there; the flattened
    /// level's compile makes the same partition of the open space (skybox
    /// included), carries the same camera, and the same world bounds, which
    /// leave the skybox out; the skybox's clusters see only one another; and
    /// the map passes the loader checks.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelCarriesTheSkyboxBelowItsGrid(int rotation)
    {
        VmfDocument library = Library();
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{rotation}");
        RoomLibrary rooms = await CompileAsync(library);
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(3, RoomAreaPortalHarness.Areas(linked.Bsp).Length);
        Assert.Equal(RoomAreaPortalHarness.Areas(flat).Length, RoomAreaPortalHarness.Areas(linked.Bsp).Length);
        Assert.Single(RoomAreaPortalHarness.Listings(linked.Bsp));

        Vec3 inside = new(128 + 100, 128, -128);
        DLeaf skyLeaf = RoomHarness.LeafAt(linked.Bsp, inside);
        Assert.Equal(0, skyLeaf.Contents & (int)BrushContents.Solid);
        Assert.Equal(2, skyLeaf.GetArea());
        Assert.Equal(1, RoomHarness.LeafAt(linked.Bsp, new Vec3(128, 128, 60)).GetArea());
        Dictionary<int, int> names = Partition(linked.Bsp, flat, level);
        Assert.Equal(2, names.Count);

        Assert.Equal(["angles=0 0 0 | scale=16 | origin=128 128 -128 | classname=sky_camera"], OfClass(linked.Bsp, "sky_camera"));
        Assert.Equal(OfClass(flat, "sky_camera"), OfClass(linked.Bsp, "sky_camera"));
        Assert.Equal(World(flat, "world_mins"), World(linked.Bsp, "world_mins"));
        Assert.Equal(World(flat, "world_maxs"), World(linked.Bsp, "world_maxs"));
        Assert.DoesNotContain("-", World(linked.Bsp, "world_mins")!, StringComparison.Ordinal);

        // The skybox's clusters see one another and nothing of the rooms'.
        short cluster = skyLeaf.Cluster;
        short room = RoomHarness.LeafAt(linked.Bsp, new Vec3(128, 128, 60)).Cluster;
        ReadOnlySpan<byte> row = linked.Vis.Pvs(cluster);
        Assert.True((row[cluster >> 3] & (1 << (cluster & 7))) != 0);
        Assert.Equal(0, row[room >> 3] & (1 << (room & 7)));

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// The linked and the flattened maps put every sample point of the
    /// rooms' cells and of the skybox's in open space alike, and in areas
    /// that name each other one to one.
    /// </summary>
    private static Dictionary<int, int> Partition(BspData linked, BspData flat, LevelGrid level)
    {
        Dictionary<int, int> names = RoomAreaPortalHarness.SamePartition(linked, flat, level);
        foreach (Vec3 point in SkyboxSamples(0, 0))
        {
            DLeaf a = RoomHarness.LeafAt(linked, point);
            DLeaf b = RoomHarness.LeafAt(flat, point);
            bool solidA = (a.Contents & (int)BrushContents.Solid) != 0;
            Assert.True(solidA == ((b.Contents & (int)BrushContents.Solid) != 0), $"at {point} the maps disagree on solid");
            if (!solidA)
            {
                Assert.True(!names.TryGetValue(a.GetArea(), out int seen) || seen == b.GetArea(), $"at {point} the areas disagree");
                names[a.GetArea()] = b.GetArea();
            }
        }

        Assert.Equal(names.Count, names.Values.Distinct().Count());
        return names;
    }
}
