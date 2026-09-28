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
    public void TheStatsCountLeavesFloorsVolumeComponentsAndDoorLinks()
    {
        NavAgentStats stats = NavInspector.Stats(Sample(), 0);
        Assert.Equal(new NavAgentStats("standing", 10, 3, 3, 10 * 16.0 * 16 * 16, 1, 3, 1, 2), stats);
    }

    [Fact]
    public void TheReportNamesTheGridTheIdsTheSpawnAndEachAgent()
    {
        string report = NavInspector.Describe(Sample());
        Assert.Contains("grid 2 x 1 cells of 32 units, 2 placed; voxel 16 (2 per cell edge)", report, StringComparison.Ordinal);
        Assert.Contains("level id 0f1e2d3c-4b5a-8978-8685-f4e3d2c1b0a9", report, StringComparison.Ordinal);
        Assert.Contains("doors 2 (2 joined, 0 capped); points of interest 2", report, StringComparison.Ordinal);
        Assert.Contains("spawn at (8 8 0) facing 90", report, StringComparison.Ordinal);
        Assert.Contains("agent 0 \"standing\" box (-16 -16 0)-(16 16 72) mask 0x201400b", report, StringComparison.Ordinal);
        Assert.Contains("1 components; the largest has 3 leaves in 2 of 2 rooms; 1 door links", report, StringComparison.Ordinal);

        Nav3dLevel noSpawn = Nav3dFileTests.Sample();
        noSpawn = new Nav3dLevel
        {
            CellSize = noSpawn.CellSize, VoxelSize = noSpawn.VoxelSize, CellVoxels = noSpawn.CellVoxels, Columns = noSpawn.Columns,
            Rows = noSpawn.Rows, Cells = noSpawn.Cells, Doors = noSpawn.Doors, Pois = noSpawn.Pois, Agents = noSpawn.Agents,
        };
        Assert.Contains("spawn: none", NavInspector.Describe(Nav3dReader.Open(Nav3dWriter.Write(noSpawn))), StringComparison.Ordinal);
    }

    [Fact]
    public void TheObjHasABoxPerLeafOrASquarePerFloorLeaf()
    {
        string boxes = NavInspector.Obj(Sample(), 0, NavObjMode.Boxes);
        Assert.Equal(24, boxes.Split('\n').Count(l => l.StartsWith("v ", StringComparison.Ordinal)));
        Assert.Equal(18, boxes.Split('\n').Count(l => l.StartsWith("f ", StringComparison.Ordinal)));
        Assert.Contains("v 32 32 32", boxes, StringComparison.Ordinal);

        string floors = NavInspector.Obj(Sample(), 0, NavObjMode.Floor);
        Assert.Equal(12, floors.Split('\n').Count(l => l.StartsWith("v ", StringComparison.Ordinal)));
        Assert.Contains("f 9 10 11 12", floors, StringComparison.Ordinal);
    }
}
