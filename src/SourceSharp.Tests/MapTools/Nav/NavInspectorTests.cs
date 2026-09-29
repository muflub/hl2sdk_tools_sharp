//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.Tests.MapFormats.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>What <c>ssmap nav</c> prints and exports.</summary>
public sealed class NavInspectorTests
{
    private static Nav3dReader Sample() => Nav3dReader.Open(Nav3dWriter.Write(Nav3dFileTests.Sample()));

    [Fact]
    public void TheStatsCountLeavesFloorsVolumeAndComponentsPerPreset()
    {
        NavAgentStats stats = NavInspector.Stats(Sample(), 0);
        // Standing, 8 x 20: a voxel's origin range plus 20 must stay under the 40 ceiling, so
        // one voxel of each floor leaf; the voxel under the overhang is too low.
        Assert.Equal(new NavAgentStats("standing", 4, 4, 4 * 16.0 * 16 * 16, 1, 4, 2), stats);
        NavAgentStats crawler = NavInspector.Stats(Sample(), 1);
        // The crawler, 4 x 4 NPC: not in the player-only water ladder, but under the
        // overhang (where it floats, so it stands in three) and in both voxels of each tall leaf.
        Assert.Equal(new NavAgentStats("crawler", 4, 3, 6 * 16.0 * 16 * 16, 1, 4, 2), crawler);
    }

    [Fact]
    public void TheReportNamesTheGridTheIdsTheTraversalTheSpawnAndEachPreset()
    {
        string report = NavInspector.Describe(Sample());
        Assert.Equal(
            """
            grid 2 x 1 cells of 32 units, 2 placed; voxel 16 (2 per cell edge)
            level id 0f1e2d3c-4b5a-8978-8685-f4e3d2c1b0a9, pack id 01234567-89ab-8def-8123-456789abcdef, codec None
            doors 2 (2 joined, 0 capped); points of interest 2
            leaves 5 (4 on a player floor, 1 water, 1 ladder); jump links 1; dynamic obstacles 1
            step 18, jump 56 up and 100 across
            spawn at (8 8 0) facing 90
            agent 0 "standing" 8 x 20 player
              fits in 4 leaves (stands in 4), free volume 16384 cubic units
              1 components; the largest has 4 leaves in 2 of 2 rooms
            agent 1 "crawler" 4 x 4 npc
              fits in 4 leaves (stands in 3), free volume 24576 cubic units
              1 components; the largest has 4 leaves in 2 of 2 rooms

            """.ReplaceLineEndings("\n"),
            report);

        // The header's spawn slot (offset 68 of the image, after the envelope) set to none.
        byte[] noSpawn = Nav3dWriter.Write(Nav3dFileTests.Sample());
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(noSpawn.AsSpan(Nav3dFormat.EnvelopeBytes + 68), -1);
        Assert.Contains("spawn: none", NavInspector.Describe(Nav3dReader.Open(noSpawn)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheObjHasABoxPerLeafOrASquarePerFloorLeaf()
    {
        string all = NavInspector.Obj(Sample(), -1, NavObjMode.Boxes);
        string boxes = NavInspector.Obj(Sample(), 0, NavObjMode.Boxes);
        string floors = NavInspector.Obj(Sample(), -1, NavObjMode.Floor);
        string crawler = NavInspector.Obj(Sample(), 1, NavObjMode.Floor);
        static int Count(string obj, string prefix) => obj.Split('\n').Count(l => l.StartsWith(prefix, StringComparison.Ordinal));

        // Every leaf: five boxes, full height (leaf 0 two voxels tall).
        Assert.Equal((40, 30), (Count(all, "v "), Count(all, "f ")));
        Assert.StartsWith("# ssmap nav: every leaf, leaves\nv 0 0 0\n", all, StringComparison.Ordinal);
        Assert.Contains("v 16 16 32\n", all, StringComparison.Ordinal);

        // Standing: the four leaves it fits, each cut to its fit (leaf 0 to one voxel).
        Assert.Equal((32, 24), (Count(boxes, "v "), Count(boxes, "f ")));
        Assert.DoesNotContain(" 32\n", boxes, StringComparison.Ordinal);

        // Floors: every walkable player floor at its height (the water ladder's at 2.5, cell 1's at 18),
        // and the crawler's three, not the player-only one.
        Assert.Equal((16, 4), (Count(floors, "v "), Count(floors, "f ")));
        Assert.Contains("v 16 16 2.5\n", floors, StringComparison.Ordinal);
        Assert.Contains("v 32 0 18\n", floors, StringComparison.Ordinal);
        Assert.Equal((12, 3), (Count(crawler, "v "), Count(crawler, "f ")));
        Assert.StartsWith("# ssmap nav: agent \"crawler\", floors\n", crawler, StringComparison.Ordinal);
        Assert.DoesNotContain("2.5", crawler, StringComparison.Ordinal);
    }
}
