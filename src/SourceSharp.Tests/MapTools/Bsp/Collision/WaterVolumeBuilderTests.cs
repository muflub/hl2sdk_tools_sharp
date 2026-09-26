//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// <c>EmitWaterVolumesForBSP</c> over hand-built trees.
/// </summary>
public class WaterVolumeBuilderTests
{
    private const int Water = CollisionContents.Water;

    private sealed class Tree
    {
        private readonly Dictionary<IBspNode, int> _disk = [];
        private int _next;

        public WindingArena Arena { get; } = new();

        public List<Plane> Planes { get; } = [new Plane(new Vec3(0, 0, 1), 0f), new Plane(new Vec3(0, 0, -1), 0f)];

        public List<int> Depths { get; } = [];

        public BspNode Leaf(int contents, float minZ, float maxZ, float x = 0)
        {
            BspNode leaf = new(_next++) { Contents = contents, Mins = new Vec3(x - 64, -64, minZ), Maxs = new Vec3(x + 64, 64, maxZ) };
            _disk[leaf] = _disk.Count;
            return leaf;
        }

        public BspNode Node(IBspNode front, IBspNode back) => new(_next++) { PlaneNumber = 0, Front = front, Back = back };

        /// <summary>A portal between two leaves with a quad winding at height z.</summary>
        public void Portal(IBspNode front, IBspNode back, float z, bool visible = true, int texInfo = 9)
        {
            Portal p = new(_next++)
            {
                Winding = Arena.Create([new Vec3(-10, -10, z), new Vec3(10, -10, z), new Vec3(10, 10, z), new Vec3(-10, 10, z)]),
                Side = visible ? new MapBrushSide { PlaneNumber = 0, TexInfo = texInfo } : null,
            };
            TreePortals.AddPortalToNodes(p, front, back);
        }

        public WaterVolumeBuilder Builder(ComplianceOptions? compliance = null) =>
            new(Planes, Arena, n => _disk.TryGetValue(n, out int d) ? d : -1, (_, _) => 77, new Sink(Depths), compliance);

        private sealed class Sink(List<int> depths) : IWaterTexInfoSink
        {
            public int FindOrCreateWaterTexInfo(int baseTexInfo, float depth)
            {
                depths.Add((int)depth);
                return 100 + baseTexInfo;
            }
        }
    }

    private static (WaterVolumeBuilder Builder, Tree Tree) Pool()
    {
        Tree t = new();
        BspNode air = t.Leaf(0, 0, 64);
        BspNode water = t.Leaf(Water, -48, 0);
        t.Portal(water, air, 0);
        WaterVolumeBuilder b = t.Builder();
        b.EmitWaterVolumesForModel(0, t.Node(air, water));
        return (b, t);
    }

    [Fact]
    public void AWaterLeafUnderAnAirPortalHasASurface()
    {
        // BuildWaterLeaf.
        (WaterVolumeBuilder b, _) = Pool();

        WaterModel model = Assert.Single(b.WaterModels);
        Assert.True(model.HasSurface);
        Assert.Equal(new Vec3(0, 0, 1), model.SurfaceNormal);
        Assert.Equal(0f, model.SurfaceDist);
    }

    [Fact]
    public void TheLeafWaterDataHoldsTheSurfaceAndTheLowestLeaf()
    {
        // FindOrCreateLeafWaterData(surfaceDist, minZ, surfaceTexInfo).
        (WaterVolumeBuilder b, _) = Pool();

        var data = Assert.Single(b.LeafWaterData);
        Assert.Equal(0f, data.SurfaceZ);
        Assert.Equal(-48f, data.MinZ);
        Assert.Equal(9, data.SurfaceTexInfoId);
    }

    [Fact]
    public void TheDepthTexInfoIsAskedForAtSurfaceMinusFloor()
    {
        (_, Tree t) = Pool();

        Assert.Equal([48], t.Depths);
    }

    [Fact]
    public void AnInvisiblePortalGivesNoSurface()
    {
        // "not visible, can't be the portals we're looking for...".
        Tree t = new();
        BspNode air = t.Leaf(0, 0, 64);
        BspNode water = t.Leaf(Water, -48, 0);
        t.Portal(water, air, 0, visible: false);
        WaterVolumeBuilder b = t.Builder();
        b.EmitWaterVolumesForModel(0, t.Node(air, water));

        Assert.False(Assert.Single(b.WaterModels).HasSurface);
        Assert.Equal(77, b.LeafWaterData[0].SurfaceTexInfoId);
    }

    [Fact]
    public void TwoSeparatePoolsWithTheSameSurfaceShareOneFogVolume()
    {
        // FindOrCreateLeafWaterData matches exactly: two
        // connected groups, two water models, one dleafwaterdata.
        Tree t = new();
        BspNode air = t.Leaf(0, 0, 64);
        BspNode left = t.Leaf(Water, -48, 0, x: -500);
        BspNode right = t.Leaf(Water, -48, 0, x: 500);
        t.Portal(left, air, 0);
        t.Portal(right, air, 0);
        WaterVolumeBuilder b = t.Builder();
        b.EmitWaterVolumesForModel(0, t.Node(air, t.Node(left, right)));

        Assert.Equal(2, b.WaterModels.Count);
        Assert.Single(b.LeafWaterData);
        Assert.All(b.WaterModels, m => Assert.Equal(0, m.FogVolumeIndex));
    }

    private static WaterVolumeBuilder TwoEqualPools(ComplianceOptions compliance)
    {
        Tree t = new();
        BspNode air = t.Leaf(0, 0, 64);
        BspNode left = t.Leaf(Water, -48, 0, x: -500);
        BspNode right = t.Leaf(Water, -48, 0, x: 500);
        t.Portal(left, air, 0);
        t.Portal(right, air, 0);
        WaterVolumeBuilder b = t.Builder(compliance);
        b.EmitWaterVolumesForModel(0, t.Node(air, t.Node(left, right)));
        return b;
    }

    [Fact]
    public void StockVisitsEqualSurfaceLeavesInReverseTreeOrder()
    {
        // WaterLeafSortTie: IsLowerLeaf returns true for an equal leaf, so each
        // one is inserted before those already listed.
        Assert.Equal([[2], [1]], TwoEqualPools(ComplianceOptions.Stock).WaterModels.Select(m => m.Leaves.ToArray()));
    }

    [Fact]
    public void CorrectVisitsEqualSurfaceLeavesInTreeOrder()
    {
        Assert.Equal([[1], [2]], TwoEqualPools(ComplianceOptions.Correct).WaterModels.Select(m => m.Leaves.ToArray()));
    }

    [Fact]
    public void ConnectedWaterLeavesAreOneModel()
    {
        Tree t = new();
        BspNode air = t.Leaf(0, 0, 64);
        BspNode upper = t.Leaf(Water, -48, 0);
        BspNode lower = t.Leaf(Water, -200, -48);
        t.Portal(upper, air, 0);
        t.Portal(lower, upper, -48, visible: false);
        WaterVolumeBuilder b = t.Builder();
        b.EmitWaterVolumesForModel(0, t.Node(air, t.Node(upper, lower)));

        WaterModel model = Assert.Single(b.WaterModels);
        Assert.Equal([1, 2], model.Leaves.Order());
        Assert.Equal(-200f, b.LeafWaterData[0].MinZ);
    }

    [Fact]
    public void ALeafWithAPortalAboveTheSurfaceIsNotFlooded()
    {
        // PortalCrossesWater: FRONT of the surface stops the flood.
        Tree t = new();
        BspNode air = t.Leaf(0, 0, 64);
        BspNode pool = t.Leaf(Water, -48, 0);
        BspNode tall = t.Leaf(Water, -48, 32, x: 128);
        t.Portal(pool, air, 0);
        t.Portal(tall, pool, -10, visible: false);
        t.Portal(tall, air, 16);
        WaterVolumeBuilder b = t.Builder();
        b.EmitWaterVolumesForModel(0, t.Node(air, t.Node(pool, tall)));

        // tall is refused by pool's flood, and by its own: its portal at z=16
        // is in front of the surface either way.
        Assert.Equal([1], Assert.Single(b.WaterModels).Leaves);
    }

    [Theory]
    [InlineData(true, 1f, 0f, true, 0.5f, 0f, true)]    // current points less up: new first
    [InlineData(true, 0.5f, 0f, true, 1f, 0f, false)]   // current points more up: after
    [InlineData(true, 1f, 5f, true, 1f, 0f, true)]      // equal, new FARTHER: still first (the dead branch)
    [InlineData(true, 1f, 0f, true, 1f, 5f, true)]      // equal, new nearer: first
    [InlineData(true, 1f, 0f, false, 0f, 0f, true)]     // a surface beats none
    [InlineData(false, 0f, 0f, true, 1f, 0f, false)]
    [InlineData(false, 0f, 0f, false, 0f, 0f, false)]
    public void StockIsLowerLeafOrder(bool newSurface, float newZ, float newDist, bool curSurface, float curZ, float curDist, bool expected)
    {
        // WaterLeafSortTie, reproduced.
        Assert.Equal(expected, WaterVolumeBuilder.IsLowerLeaf(newSurface, newZ, newDist, curSurface, curZ, curDist, ComplianceOptions.Stock));
    }

    [Theory]
    [InlineData(true, 1f, 5f, true, 1f, 0f, false)]     // equal, new farther: after
    [InlineData(true, 1f, 0f, true, 1f, 5f, true)]      // equal, new nearer: first
    [InlineData(true, 1f, 0f, true, 1f, 0f, false)]     // identical: after, so discovery order is kept
    [InlineData(true, 1f, 0f, true, 0.5f, 0f, true)]
    [InlineData(true, 0.5f, 0f, true, 1f, 0f, false)]
    public void CorrectIsLowerLeafSortsEqualSurfacesByDistance(bool newSurface, float newZ, float newDist, bool curSurface, float curZ, float curDist, bool expected)
    {
        Assert.Equal(expected, WaterVolumeBuilder.IsLowerLeaf(newSurface, newZ, newDist, curSurface, curZ, curDist, ComplianceOptions.Correct));
    }

    [Fact]
    public void TheWaterTextureNameIsMangledAndLowerCased()
    {
        // GetWaterTextureName.
        Assert.Equal("maps/l1_pool/liquids/water_depth_48", WaterVolumeBuilder.WaterTextureName("L1_Pool", "Liquids/Water", 48));
    }
}
