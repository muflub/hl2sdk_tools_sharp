using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;

using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>The whole 4c pipeline on the in-memory box.</summary>
public sealed class RadWorldTests
{
    private static readonly CompileParallelism One = new() { MaxDegree = 1 };

    private static async Task<RadWorld> LightAsync(
        LightTestMap map, DirectLightingSettings? settings = null, CompileParallelism? parallelism = null)
    {
        IRayTracer tracer = map.Tracer();
        RadWorld world = await RadWorld.StartAsync(
            map.Build(), settings ?? LightBox.Settings(), new TextureLightTable(new(), "box"),
            tracer, parallelism ?? One, CancellationToken.None);
        await world.LightFacesAsync(tracer, parallelism ?? One, CancellationToken.None);
        return world;
    }

    private static LightTestMap LitBox(string style = "0")
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200"), ("style", style)));
        return map;
    }

    [Fact]
    public async Task ALightInTheBoxLightsEveryFloorSample()
    {
        RadWorld world = await LightAsync(LitBox());
        FaceLight floor = world.FaceLights[0]!;
        Assert.All(floor.LightFor(0, 0)!, v => Assert.True(v.Lighting.X > 0.1f));
    }

    [Fact]
    public async Task AnOccluderShadowsTheFloorBelowIt()
    {
        LightTestMap map = LitBox();
        map.AddOccluder(new(96, 96, 64), new(160, 96, 64), new(160, 160, 64), new(96, 160, 64));
        RadWorld world = await LightAsync(map);
        FaceLight floor = world.FaceLights[0]!;
        LightingValue[] light = floor.LightFor(0, 0)!;
        int centre = Array.FindIndex(floor.Samples, s => s.S == 8 && s.T == 8);
        int corner = Array.FindIndex(floor.Samples, s => s.S == 0 && s.T == 0);

        // No vis -> only the 0.1 flat ambient reaches the shadowed centre.
        Assert.Equal(0.1f, light[centre].Lighting.X, 5);
        Assert.True(light[corner].Lighting.X > 1f);
    }

    [Fact]
    public async Task AMapWithNoVisIsDirectOnlyWithATenthAmbient()
    {
        // vrad.cpp:2245-2251.
        RadWorld world = await LightAsync(LightBox.Map());
        Assert.Equal(0, world.Settings.Bounces);
        Assert.Equal(new Vec3(0.1f, 0.1f, 0.1f), world.Settings.Ambient);
        Assert.All(world.FaceLights[0]!.LightFor(0, 0)!, v => Assert.Equal(new Vec3(0.1f, 0.1f, 0.1f), v.Lighting));
    }

    [Fact]
    public async Task EveryLitFaceHasStyleZeroFirst()
    {
        RadWorld world = await LightAsync(LitBox());
        for (int f = 0; f < 6; f++)
        {
            Assert.Equal(0, world.Layout!.Styles[f * 4]);
        }
    }

    [Fact]
    public async Task AStyledLightTakesTheSecondSlot()
    {
        RadWorld world = await LightAsync(LitBox(style: "5"));
        Assert.Equal(new byte[] { 0, 5, 255, 255 }, world.Layout!.Styles[..4]);
    }

    [Fact]
    public async Task AFifthStyleOnAFaceIsWarnedAndDropped()
    {
        LightTestMap map = LightBox.Map();
        for (int s = 1; s <= 4; s++)
        {
            map.Entities.Add(LightTestMap.Entity(
                ("classname", "light"), ("origin", $"{s * 40} 128 128"), ("_light", "255 255 255"), ("style", $"{s}")));
        }

        RadWorld world = await LightAsync(map);
        Assert.Contains(world.Warnings, w => w.Contains("Too many light styles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASkyFaceGetsNoLightmap()
    {
        LightTestMap map = LightBox.Map(ceiling: SurfaceFlags.Sky);
        RadWorld world = await LightAsync(map);
        Assert.Null(world.FaceLights[1]);
        Assert.Equal(-1, world.Layout!.LightOffsets[1]);
    }

    [Fact]
    public async Task TheSunLightsTheFloorThroughASkyCeiling()
    {
        LightTestMap map = LightBox.Map(ceiling: SurfaceFlags.Sky);
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_environment"), ("origin", "128 128 128"), ("pitch", "-90"), ("_light", "255 255 255 200")));
        RadWorld world = await LightAsync(map);
        Assert.All(world.FaceLights[0]!.LightFor(0, 0)!, v => Assert.True(v.Lighting.X > 50f));
    }

    [Fact]
    public async Task OneWorkerAndFourProduceTheSameLight()
    {
        LightTestMap map = LitBox();
        map.AddOccluder(new(96, 96, 64), new(160, 96, 64), new(160, 160, 64), new(96, 160, 64));
        RadWorld one = await LightAsync(map, parallelism: One);
        RadWorld four = await LightAsync(map, parallelism: new CompileParallelism { MaxDegree = 4 });
        for (int f = 0; f < 6; f++)
        {
            Assert.Equal(
                MemoryMarshal.AsBytes(one.FaceLights[f]!.LightFor(0, 0)!.AsSpan()).ToArray(),
                MemoryMarshal.AsBytes(four.FaceLights[f]!.LightFor(0, 0)!.AsSpan()).ToArray());
        }
    }

    [Fact]
    public async Task SupersamplingChangesTheLightAtAShadowEdge()
    {
        LightTestMap map = LitBox();
        map.AddOccluder(new(96, 96, 64), new(160, 96, 64), new(160, 160, 64), new(96, 160, 64));
        RadWorld with = await LightAsync(map);
        RadWorld without = await LightAsync(map, LightBox.Settings() with { Supersample = false });
        Assert.NotEqual(
            MemoryMarshal.AsBytes(with.FaceLights[0]!.LightFor(0, 0)!.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(without.FaceLights[0]!.LightFor(0, 0)!.AsSpan()).ToArray());
        Assert.True(with.Statistics.Batches > without.Statistics.Batches);
    }

    [Fact]
    public async Task DebugExtraPaintsTheSupersampledSamples()
    {
        LightTestMap map = LitBox();
        map.AddOccluder(new(96, 96, 64), new(160, 96, 64), new(160, 160, 64), new(96, 160, 64));
        RadWorld world = await LightAsync(map, LightBox.Settings() with { DebugExtra = true });
        // Pass 1 paints (255, 0, 0) (lightmap.cpp:2915-2918); the no-vis 0.1
        // ambient is added after, in BuildPatchLights, as in stock.
        Assert.Contains(world.FaceLights[0]!.LightFor(0, 0)!, v => v.Lighting == new Vec3(255.1f, 0.1f, 0.1f));
    }

    [Fact]
    public async Task FaceWindingsAreDroppedOnceLit()
    {
        RadWorld world = await LightAsync(LitBox());
        Assert.All(world.FaceLights, fl => Assert.Empty(fl!.SampleWindingPoints));
    }

    [Fact]
    public async Task APreCancelledTokenDoesNoWork()
    {
        LightTestMap map = LitBox();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RadWorld.StartAsync(
            map.Build(), LightBox.Settings(), new TextureLightTable(new(), "box"), map.Tracer(), One, cts.Token));
    }

    [Fact]
    public void TheWorldlightBytesAreTheExportedRecords()
    {
        RadWorld world = LightBox.Build(LitBox());
        Assert.Equal(88, world.WorldLightBytes().Length);
    }

    [Fact]
    public void TheSettingsTakeStocksSmoothingLiteralForFortyFiveDegrees()
    {
        Assert.Equal(0.7071067f, DirectLightingSettings.FromVrad(VradOptions.Default, false).SmoothingThreshold);
        Assert.Equal(
            (float)Math.Cos(30 * (Math.PI / 180.0)),
            DirectLightingSettings.FromVrad(VradOptions.Default with { SmoothingAngleDegrees = 30 }, false).SmoothingThreshold);
    }

    [Fact]
    public void SoftSunIsStoredAsASine() =>
        Assert.Equal(
            (float)Math.Sin(Math.PI / 180.0 * 5),
            DirectLightingSettings.FromVrad(VradOptions.Default with { SunAngularExtentDegrees = 5 }, false).SunAngularExtent);

    [Fact]
    public void LuxelDensityIsRefused() =>
        Assert.Throws<NotSupportedException>(() =>
            DirectLightingSettings.FromVrad(VradOptions.Default with { LuxelDensity = 0.5f }, false));
}

public sealed class QuirkEffectTests
{
    private static LightTestMap TwoSuns()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_environment"), ("origin", "1 1 1"), ("_light", "255 255 255"), ("SunSpreadAngle", "5")));
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_environment"), ("origin", "2 2 2"), ("_light", "255 255 255"), ("SunSpreadAngle", "20")));
        return map;
    }

    [Fact]
    public void StockLetsTheSecondSunsSpreadWin()
    {
        RadWorld world = LightBox.Build(TwoSuns(), LightBox.Settings(stock: true));
        Assert.Equal((float)Math.Sin(Math.PI / 180.0 * 20), world.Lights.SunAngularExtent);
    }

    [Fact]
    public void CorrectKeepsTheFirstSunsSpread()
    {
        RadWorld world = LightBox.Build(TwoSuns());
        Assert.Equal((float)Math.Sin(Math.PI / 180.0 * 5), world.Lights.SunAngularExtent);
    }

    private static int ProbeRays(bool stock)
    {
        RadWorld world = LightBox.Build(LightBox.Map(), LightBox.Settings(stock: stock));
        LightRayLog rays = new();
        world.CanLeafTraceToSky(0, rays);
        return rays.SkyCount;
    }

    [Fact]
    public void StockCastsTheTailDirectionThreeTimes() => Assert.Equal(164, ProbeRays(stock: true));

    [Fact]
    public void CorrectCastsEachOfTheHundredAndSixtyTwoOnce() => Assert.Equal(162, ProbeRays(stock: false));

    [Fact]
    public void AProbeThatSeesSkyAnswersTrue()
    {
        RadWorld world = LightBox.Build(LightBox.Map());
        LightRayLog rays = new();
        world.CanLeafTraceToSky(0, rays);
        HitId[] hits = new HitId[rays.SkyCount];
        Array.Fill(hits, HitId.Missed);
        hits[5] = new HitId(SourceSharp.MapTools.Rad.TraceId.Sky, 0.5f);
        rays.BeginReplay(new ulong[1], 0, hits, 0);
        Assert.True(world.CanLeafTraceToSky(0, rays));
    }
}

public sealed class LightVisibilityTests
{
    private static LightVisibility Load(bool[][] sees, int leafClusters = 1)
    {
        LightTestMap map = LightBox.Map();
        map.Visibility = LightTestMap.VisLump(sees);
        _ = leafClusters;
        LightGeometry g = Geometry.Load(map);
        return LightVisibility.Load(map.Build(), g.Leaves);
    }

    [Fact]
    public void ARowIsDecompressedFromTheLump()
    {
        LightVisibility v = Load([[true, false, true], [false, true, false], [true, false, true]]);
        byte[] row = new byte[v.RowBytes];
        v.GetVisCache(0, row);
        Assert.True(LightVisibility.PvsCheck(row, 0));
        Assert.False(LightVisibility.PvsCheck(row, 1));
        Assert.True(LightVisibility.PvsCheck(row, 2));
    }

    [Fact]
    public void ANegativeClusterSeesEverything()
    {
        LightVisibility v = Load([[false, false], [false, false]]);
        byte[] row = new byte[v.RowBytes];
        v.GetVisCache(-1, row);
        Assert.Equal(0xFF, row[0]);
        Assert.True(LightVisibility.PvsCheck(row, -1));
    }

    [Fact]
    public void AMergeIsTheUnionOfTwoRows()
    {
        LightVisibility v = Load([[true, false, false], [false, true, false], [false, false, true]]);
        DirectLight light = new();
        v.SetLightVis(light, 0);
        v.MergeLightVis(light, 2);
        Assert.True(LightVisibility.PvsCheck(light.Pvs, 0));
        Assert.False(LightVisibility.PvsCheck(light.Pvs, 1));
        Assert.True(LightVisibility.PvsCheck(light.Pvs, 2));
    }

    [Fact]
    public void ALightsRowIsOneByteLongerThanTheLumpsWhenTheCountIsAMultipleOfEight()
    {
        // lightmap.cpp:1016 (numclusters/8)+1 against GetVisCache's (n+7)/8.
        LightVisibility v = Load([.. Enumerable.Range(0, 8).Select(_ => new bool[8])]);
        Assert.Equal(2, v.RowBytes);
    }

    [Fact]
    public void WithoutVisTheClustersAreCountedFromTheLeaves()
    {
        // vrad.cpp:2250: CountClusters() is the largest leaf cluster plus one.
        LightInfoLeaves leaves = new();
        Assert.Equal(4, LightVisibility.CountClusters(leaves.WithClusters(0, 3, 1)));
        Assert.Equal(1, LightVisibility.CountClusters(leaves.WithClusters(-1)));
    }

    private sealed class LightInfoLeaves
    {
        public LeafInfo[] WithClusters(params int[] clusters) =>
            [.. clusters.Select(c => new LeafInfo(0, c, 0, 0, Vec3.Zero, Vec3.Zero, 0, 0))];
    }

    [Fact]
    public void ANoVisLightSeesItsOwnCluster()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "1 1 1"), ("_light", "255 255 255")));
        DirectLight light = Assert.Single(LightBox.Build(map).Lights.Active);
        Assert.True(LightVisibility.PvsCheck(light.Pvs, 0));
    }
}

public sealed class CompiledBspTreeTests
{
    private static CompiledBspTree Split(out LightGeometry g)
    {
        LightTestMap map = LightBox.Map();
        int plane = map.AddPlane(new Vec3(1, 0, 0), 100);
        DNode node = new() { PlaneNum = plane };
        node.Children[0] = -2;
        node.Children[1] = -1;
        map.Nodes = [node];
        map.Leaves = [new DLeaf { Cluster = 0 }, new DLeaf { Cluster = 1 }];
        g = Geometry.Load(map);
        return new CompiledBspTree(g);
    }

    [Fact]
    public void APointInFrontOfTheSplitIsInTheFrontChild()
    {
        CompiledBspTree tree = Split(out _);
        Assert.Equal(1, tree.ClusterFromPoint(new Vec3(200, 0, 0)));
        Assert.Equal(0, tree.ClusterFromPoint(new Vec3(0, 0, 0)));
    }

    [Fact]
    public void APointOnThePlanePrefersAFrontLeafWithACluster()
    {
        // vismat.cpp:112-118: within TEST_EPSILON, front first unless it is -1.
        CompiledBspTree tree = Split(out _);
        Assert.Equal(1, tree.ClusterFromPoint(new Vec3(100.05f, 0, 0)));
    }

    [Fact]
    public void PointLeafnumSplitsOnTheSignWithNoEpsilon()
    {
        // trace.cpp:450: dist < 0 goes back, anything else front.
        CompiledBspTree tree = Split(out _);
        Assert.Equal(1, tree.LeafFromPoint(new Vec3(100, 0, 0)));
        Assert.Equal(0, tree.LeafFromPoint(new Vec3(99.99f, 0, 0)));
    }

    [Fact]
    public void ATreeWithNoNodesIsLeafZero()
    {
        CompiledBspTree tree = new(Geometry.Load(LightBox.Map()));
        Assert.Equal(0, tree.LeafFromPoint(new Vec3(5, 5, 5)));
        Assert.Equal(0, tree.ClusterFromPoint(new Vec3(5, 5, 5)));
    }
}

public sealed class SkyLeafVisibilityTests
{
    private static (SkyLeafVisibility Sky, LightGeometry G) Build(bool radialSecondLeaf)
    {
        // Leaf 0 holds every face, including a sky ceiling; leaf 1 holds none
        // and does not see cluster 0.
        LightTestMap map = LightBox.Map(ceiling: SurfaceFlags.Sky);
        DLeaf a = new() { Cluster = 0, FirstLeafFace = 0, NumLeafFaces = 6 };
        DLeaf b = new() { Cluster = 1, AreaFlags = (ushort)(radialSecondLeaf ? ((int)LeafFlags.Radial << 9) : 0) };
        b.Mins[0] = b.Mins[1] = b.Mins[2] = 10;
        b.Maxs[0] = b.Maxs[1] = b.Maxs[2] = 20;
        map.Leaves = [a, b];
        map.Visibility = LightTestMap.VisLump([[true, false], [false, true]]);
        LightGeometry g = Geometry.Load(map);
        LightVisibility vis = LightVisibility.Load(map.Build(), g.Leaves);
        SkyLeafVisibility sky = new();
        sky.Build(g, vis, new DirectLight(), new DirectLight());
        return (sky, g);
    }

    [Fact]
    public void ALeafWithASkyFaceIsASkyLeaf()
    {
        (SkyLeafVisibility sky, _) = Build(false);
        Assert.True((sky.Flags[0] & LeafFlags.Sky) != 0);
        Assert.Equal(1, sky.LeavesWithSkyFaces);
    }

    [Fact]
    public void ALeafThatCannotSeeSkyIsNot()
    {
        (SkyLeafVisibility sky, _) = Build(false);
        Assert.Equal((LeafFlags)0, sky.Flags[1] & LeafFlags.Sky);
        Assert.Empty(sky.RadialCandidates);
    }

    [Fact]
    public void ARadialLeafIsLeftForTheProbe()
    {
        (SkyLeafVisibility sky, _) = Build(true);
        Assert.Equal([1], sky.RadialCandidates);
    }

    [Fact]
    public void MarkingSetsTheFlagOnce()
    {
        (SkyLeafVisibility sky, _) = Build(true);
        int before = sky.SkyLeaves;
        sky.MarkSky(1);
        sky.MarkSky(1);
        Assert.Equal(before + 1, sky.SkyLeaves);
    }
}

public sealed class MacroTextureTests
{
    private static byte[] Vtf(int w, int h, byte[] rgba)
    {
        byte[] bytes = MapFormats.Assets.VtfTests.Synthetic(ImageFormat.Rgba8888, w, h, 1, false);
        rgba.CopyTo(bytes, bytes.Length - rgba.Length);
        return bytes;
    }

    private static async Task<MacroTextures> LoadAsync(LightTestMap map, params (string Path, byte[] Body)[] files)
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] body) in files)
        {
            disk.AddFile(path, body);
        }

        await using ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
        BspData bsp = map.Build();
        LightGeometry g = LightGeometry.Load(bsp, VradLightingRange.Ldr, ComplianceOptions.Correct);
        return await MacroTextures.LoadAsync(bsp, g, map.Entities, "box", content, [], CancellationToken.None);
    }

    [Fact]
    public async Task NoWorldspawnMeansNoTextures()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Clear();
        List<string> warnings = [];
        await using ContentFileSystem content = new([]);
        BspData bsp = map.Build();
        MacroTextures t = await MacroTextures.LoadAsync(
            bsp, LightGeometry.Load(bsp, VradLightingRange.Ldr, ComplianceOptions.Correct),
            map.Entities, "box", content, warnings, CancellationToken.None);
        Assert.Null(t.Global);
        Assert.Contains("can't find worldspawn", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGlobalTextureIsFoundUnderMacroSlashMap()
    {
        LightTestMap map = LightBox.Map();
        MacroTextures t = await LoadAsync(map, ("materials/macro/box/base.vtf", Vtf(1, 1, [10, 20, 30, 255])));
        Assert.NotNull(t.Global);
    }

    [Fact]
    public async Task ApplyMultipliesByTheTexel()
    {
        LightTestMap map = LightBox.Map();
        map.Entities[0].Pairs.Add(new BspKeyValue("world_mins", "0 0 0"));
        map.Entities[0].Pairs.Add(new BspKeyValue("world_maxs", "256 256 0"));
        MacroTextures t = await LoadAsync(map, ("materials/macro/box/base.vtf", Vtf(1, 1, [255, 51, 0, 255])));
        Vec3 luxel = new(10, 10, 10);
        t.Apply(0, new Vec3(1, 1, 0), ref luxel);
        Assert.Equal(new Vec3(10, 2, 0), luxel);
    }

    [Fact]
    public async Task YIsFlippedAcrossTheTexture()
    {
        // macro_texture.cpp:146: iy = height - 1 - clamp(iy).
        LightTestMap map = LightBox.Map();
        map.Entities[0].Pairs.Add(new BspKeyValue("world_mins", "0 0 0"));
        map.Entities[0].Pairs.Add(new BspKeyValue("world_maxs", "100 100 0"));
        MacroTextures t = await LoadAsync(map, ("materials/macro/box/base.vtf", Vtf(1, 2, [255, 0, 0, 255, 0, 255, 0, 255])));
        Assert.Equal(new Vec3(0, 1, 0), t.Sample(t.Global!, new Vec3(10, 10, 0)));
        Assert.Equal(new Vec3(1, 0, 0), t.Sample(t.Global!, new Vec3(10, 90, 0)));
    }

    [Fact]
    public async Task AFaceWithoutAMacroTextureIsUntouched()
    {
        MacroTextures t = await LoadAsync(LightBox.Map());
        Vec3 luxel = new(3, 3, 3);
        t.Apply(0, Vec3.Zero, ref luxel);
        Assert.Equal(new Vec3(3, 3, 3), luxel);
    }
}
