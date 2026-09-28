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

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Driver;

/// <summary>
/// <c>func_viscluster</c> end to end: from the VMF entity to the cluster
/// count in the portal file.
/// </summary>
/// <remarks>
/// <para>
/// Until these facts existed the loader noticed the entity and nothing used
/// it, so every leaf a viscluster covered stayed its own cluster. On
/// <c>ctf_2fort</c>, whose 14 visclusters cover about two hundred leaves,
/// that was 188 clusters and 393 portals more than the fixed compile, most
/// of the gap to the reference compiler's portal file.
/// </para>
/// <para>
/// The map is a corridor with two part-height walls in it, so the BSP has to
/// cut the open space into several leaves; the facts then compare it with
/// and without a viscluster over the whole interior.
/// </para>
/// </remarks>
public sealed class VisClusterCompileTests
{
    // A sealed 768 x 256 x 256 corridor with two baffles, and optionally a
    // func_viscluster over some of its inside, followed by a func_brush so a
    // later entity's planes can be compared with the viscluster's.
    private static VmfDocument Corridor(
        (float, float, float)? clusterMins = null,
        (float, float, float)? clusterMaxs = null,
        bool trailingBrushEntity = false)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");

        int id = 10;
        void Slab((float, float, float) mins, (float, float, float) maxs) =>
            world.Children.Add(UnitMap.Box(UnitMap.Plain, mins, maxs, id++));

        Slab((-16, -16, -16), (784, 272, 0));      // floor
        Slab((-16, -16, 256), (784, 272, 272));    // ceiling
        Slab((-16, -16, 0), (0, 272, 256));        // -x
        Slab((768, -16, 0), (784, 272, 256));      // +x
        Slab((0, -16, 0), (768, 0, 256));          // -y
        Slab((0, 256, 0), (768, 272, 256));        // +y
        Slab((256, 0, 0), (272, 128, 256));        // baffle from -y
        Slab((512, 128, 0), (528, 256, 256));      // baffle from +y
        document.Chunks.Add(world);

        VmfChunk start = new(MapFileLoader.EntityChunk);
        start.AddKey("id", "2");
        start.AddKey("classname", "info_player_start");
        start.AddKey("origin", "64 64 64");
        document.Chunks.Add(start);

        if (clusterMins is { } mins && clusterMaxs is { } maxs)
        {
            VmfChunk cluster = new(MapFileLoader.EntityChunk);
            cluster.AddKey("id", "3");
            cluster.AddKey("classname", "func_viscluster");
            cluster.Children.Add(UnitMap.Box(UnitMap.Plain, mins, maxs, id++));
            document.Chunks.Add(cluster);
        }

        if (trailingBrushEntity)
        {
            VmfChunk brush = new(MapFileLoader.EntityChunk);
            brush.AddKey("id", "4");
            brush.AddKey("classname", "func_brush");
            brush.Children.Add(UnitMap.Box(UnitMap.Plain, (333, 32, 32), (350, 64, 64), id++));
            document.Chunks.Add(brush);
        }

        return document;
    }

    private static async Task<VbspResult> CompileAsync(VmfDocument document)
    {
        VbspContext context = await UnitMap.ContextAsync();
        MapFile map = await MapFileLoader.LoadAsync(context, document);
        MapFileReader.TakeBounds(map);
        return await Vbsp.CompileAsync(map, context);
    }

    private static int PlaneIndex(MapFile map, Vec3 normal, float dist)
    {
        for (int i = 0; i < map.Planes.Count; i++)
        {
            Plane p = map.Planes[i];
            if (p.Normal == normal && p.Dist == dist)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public async Task TheCorridorIsSeveralClustersWithoutAVisCluster()
    {
        // The control for the facts below: without a viscluster the baffles
        // do split the open space into more than one cluster.
        VbspResult result = await CompileAsync(Corridor());

        Assert.True(result.Portals!.ClusterCount > 2, $"clusters: {result.Portals.ClusterCount}");
    }

    [Fact]
    public async Task AVisClusterOverTheWholeInsideMakesItOneCluster()
    {
        VbspResult result = await CompileAsync(Corridor((0, 0, 0), (768, 256, 256)));

        Assert.Equal(1, result.Portals!.ClusterCount);
    }

    [Fact]
    public async Task AVisClusterOverTheWholeInsideLeavesNoPortals()
    {
        // Every portal is now between two leaves of one cluster, and those
        // are not written.
        VbspResult result = await CompileAsync(Corridor((0, 0, 0), (768, 256, 256)));

        Assert.Empty(result.Portals!.Portals);
    }

    [Fact]
    public async Task EveryEmptyLeafOfTheWorldIsInClusterZero()
    {
        VbspResult result = await CompileAsync(Corridor((0, 0, 0), (768, 256, 256)));
        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(result.Bsp![BspLump.Leafs]);

        // Leaf 0 is the solid error leaf; solid leaves carry -1.
        HashSet<short> clusters = [];
        for (int i = 1; i < leafs.Length; i++)
        {
            if (leafs[i].Cluster >= 0)
            {
                clusters.Add(leafs[i].Cluster);
            }
        }

        Assert.Equal([(short)0], clusters);
    }

    [Fact]
    public async Task AVisClusterOverOneBayMergesOnlyThatBay()
    {
        // The first bay, up to the first baffle, becomes one cluster; the
        // rest keeps the clusters it had, so the count falls but stays above
        // one.
        int without = (await CompileAsync(Corridor())).Portals!.ClusterCount;
        int with = (await CompileAsync(Corridor((0, 0, 0), (256, 256, 256)))).Portals!.ClusterCount;

        Assert.InRange(with, 2, without - 1);
    }

    [Fact]
    public async Task AVisClusterIsNotWrittenToTheEntityLump()
    {
        VbspResult result = await CompileAsync(Corridor((0, 0, 0), (768, 256, 256)));

        Assert.DoesNotContain(
            EntityLump.Parse(result.Bsp![BspLump.Entities]),
            e => e.ClassName == "func_viscluster");
    }

    [Fact]
    public async Task AVisClusterGetsNoBrushModel()
    {
        VbspResult result = await CompileAsync(Corridor((0, 0, 0), (768, 256, 256)));

        Assert.Equal(1, BspStructView.As<DModel>(result.Bsp![BspLump.Models]).Length);
    }

    [Fact]
    public async Task AVisClusterIsRecordedAndClearedByTheLoader()
    {
        VbspContext context = await UnitMap.ContextAsync();
        MapFile map = await MapFileLoader.LoadAsync(context, Corridor((0, 0, 0), (768, 256, 256)));

        int entity = Assert.Single(map.VisClusterEntities);
        Assert.Equal(
            (1, 0, 0),
            (map.VisClusters.Count, map.Entities[entity].BrushCount, map.Entities[entity].Pairs.Count));
    }

    [Fact]
    public async Task TheCoordinateBoxPlanesAreAddedWhereTheVisClusterLoads()
    {
        // Clipping the viscluster to the coordinate box looks its planes up
        // while the entity loads, so they are numbered before the planes of
        // any brush entity that comes after it in the file.
        VbspContext context = await UnitMap.ContextAsync();
        MapFile map = await MapFileLoader.LoadAsync(
            context, Corridor((0, 0, 0), (768, 256, 256), trailingBrushEntity: true));

        int box = PlaneIndex(map, new Vec3(1f, 0f, 0f), 16384f);
        int later = PlaneIndex(map, new Vec3(1f, 0f, 0f), 350f);

        Assert.True(box >= 0 && later > box, $"box plane {box}, later brush plane {later}");
    }
}
