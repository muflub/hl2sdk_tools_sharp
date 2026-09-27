//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.MapTools.Vis;

using Xunit;

using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// Whole faces lit with and without the dead-record cull
/// (<see cref="DirectLightingSettings.KeepDeadLights"/>) come out the same to
/// the bit, direct gather and supersampling alike.
/// </summary>
public sealed class DeadLightCullMapTests
{
    private static void AssertSameLight(FaceLight?[] expected, FaceLight?[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int f = 0; f < expected.Length; f++)
        {
            AssertSameFace(expected[f], actual[f]);
        }
    }

    private static void AssertSameFace(FaceLight? expected, FaceLight? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Styles, actual.Styles);
        Assert.Equal(expected.Light.Length, actual.Light.Length);
        for (int k = 0; k < expected.Light.Length; k++)
        {
            LightingValue[]? a = expected.Light[k];
            LightingValue[]? b = actual.Light[k];
            Assert.Equal(a is null, b is null);
            for (int i = 0; a is not null && i < a.Length; i++)
            {
                // Bits, so a zero's sign counts.
                Assert.Equal(BitConverter.SingleToInt32Bits(a[i].Lighting.X), BitConverter.SingleToInt32Bits(b![i].Lighting.X));
                Assert.Equal(BitConverter.SingleToInt32Bits(a[i].Lighting.Y), BitConverter.SingleToInt32Bits(b[i].Lighting.Y));
                Assert.Equal(BitConverter.SingleToInt32Bits(a[i].Lighting.Z), BitConverter.SingleToInt32Bits(b[i].Lighting.Z));
                Assert.Equal(BitConverter.SingleToInt32Bits(a[i].DirectSunAmount), BitConverter.SingleToInt32Bits(b[i].DirectSunAmount));
            }
        }
    }

    // The lit box with bumped walls, an emissive ceiling, an occluder and a
    // seeded handful of point and spot lights, some with a hard fade.
    private static LightTestMap SeededBox(int seed)
    {
        Random r = new(seed);
        LightTestMap map = LightBox.Map(ceilingMaterial: "lights/white", walls: SurfaceFlags.BumpLight);
        map.AddOccluder(new(96, 96, 64), new(160, 96, 64), new(160, 160, 64), new(96, 160, 64));
        string V(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        string Inside() => $"{V(16 + (r.NextDouble() * 224))} {V(16 + (r.NextDouble() * 224))} {V(16 + (r.NextDouble() * 224))}";
        int lights = 4 + r.Next(6);
        for (int i = 0; i < lights; i++)
        {
            List<(string, string)> keys = [("origin", Inside()), ("_light", $"{r.Next(50, 256)} {r.Next(50, 256)} {r.Next(50, 256)} {r.Next(50, 400)}")];
            if (r.Next(3) != 0)
            {
                double inner = 5 + (r.NextDouble() * 50);
                keys.Add(("classname", "light_spot"));
                keys.Add(("angles", $"{V((r.NextDouble() * 180) - 90)} {V(r.NextDouble() * 360)} 0"));
                keys.Add(("_inner_cone", V(inner)));
                keys.Add(("_cone", V(inner + (r.NextDouble() * 30))));
                keys.Add(("_exponent", r.Next(3) switch { 0 => "0", 1 => "1", _ => V(r.NextDouble() * 4) }));
            }
            else
            {
                keys.Add(("classname", "light"));
            }

            if (r.Next(3) == 0)
            {
                double half = 30 + (r.NextDouble() * 100);
                keys.Add(("_fifty_percent_distance", V(half)));
                keys.Add(("_zero_percent_distance", V(half * (1.5 + r.NextDouble()))));
                keys.Add(("_hardfalloff", "1"));
            }

            map.Entities.Add(LightTestMap.Entity([.. keys]));
        }

        return map;
    }

    private static async Task<RadWorld> LightAsync(LightTestMap map, bool stock, bool keep, int threads)
    {
        IRayTracer tracer = map.Tracer();
        CompileParallelism parallelism = new() { MaxDegree = threads };
        DirectLightingSettings settings = LightBox.Settings(stock) with { KeepDeadLights = keep };
        RadWorld world = await RadWorld.StartAsync(
            map.Build(), settings, await LightBox.TexLightsAsync("lights/white 255 255 255 200"),
            tracer, parallelism, CancellationToken.None);
        await world.LightFacesAsync(tracer, parallelism, CancellationToken.None);
        return world;
    }

    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 3)]
    [InlineData(3, true, 1)]
    [InlineData(4, true, 3)]
    [InlineData(5, false, 2)]
    [InlineData(6, true, 2)]
    public async Task ASeededBoxIsLitTheSameWithAndWithoutTheCull(int seed, bool stock, int threads)
    {
        LightTestMap map = SeededBox(seed);
        RadWorld uncut = await LightAsync(map, stock, keep: true, threads);
        RadWorld culled = await LightAsync(map, stock, keep: false, threads);

        AssertSameLight(uncut.FaceLights, culled.FaceLights);
        Assert.Equal(uncut.Statistics.VisibilityRays, culled.Statistics.VisibilityRays);
        Assert.Equal(uncut.Statistics.SkyRays, culled.Statistics.SkyRays);

        // The cull did something, and every record it left out is one the
        // uncut run emitted.
        Assert.Equal(0, uncut.Statistics.CulledLightRecords);
        Assert.True(culled.Statistics.CulledLightRecords > 0);
        Assert.Equal(uncut.Statistics.LightRecords, culled.Statistics.LightRecords + culled.Statistics.CulledLightRecords);
    }

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public Task TheSandboxIsLitTheSameWithAndWithoutTheCullCorrect() => SandboxAsync(synthetic: false, stock: false);

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public Task TheSandboxIsLitTheSameWithAndWithoutTheCullStock() => SandboxAsync(synthetic: false, stock: true);

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public Task TheSyntheticSandboxIsLitTheSameWithAndWithoutTheCullCorrect() => SandboxAsync(synthetic: true, stock: false);

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public Task TheSyntheticSandboxIsLitTheSameWithAndWithoutTheCullStock() => SandboxAsync(synthetic: true, stock: true);

    // The sandbox through vbsp and vvis in memory -- bare, or with the
    // synthetic content, which gives it texlights, sky and bumped materials --
    // then every seventh face lit on its own with each gatherer. A face lit
    // alone lights as it does among the others (FaceLightPacketTests), and a
    // sample of faces keeps the fact's time reasonable.
    private static async Task SandboxAsync(bool synthetic, bool stock)
    {
        const int Stride = 7;
        (BspData bsp, TextureLightTable texLights) = await CompileSandboxAsync(synthetic);
        IRayTracer tracer = StockRadWorld.Tracer(bsp, hdr: false);
        DirectLightingSettings settings = DirectLightingSettings.FromVrad(
            VradOptions.Default with { Compliance = stock ? ComplianceOptions.Stock : ComplianceOptions.Correct }, hdr: false);
        CompileParallelism parallelism = new() { MaxDegree = 2 };
        RadWorld world = await RadWorld.StartAsync(bsp, settings, texLights, tracer, parallelism, CancellationToken.None);

        DirectLightGatherer keep = new(
            world.Lights.Active, world.Lights.SunAngularExtent, world.Settings with { KeepDeadLights = true },
            world.Tree, world.Geometry.Leaves, world.SkyCameras);
        FaceLightContext culledContext = new(
            world.Geometry, world.Neighbours, world.Patches, world.Tree, world.Settings, world.Gatherer, world.Displacements);
        FaceLightContext uncutContext = new(
            world.Geometry, world.Neighbours, world.Patches, world.Tree, world.Settings, keep, world.Displacements);

        int faces = world.Geometry.Faces.Length;
        long culledRecords = 0;
        long lit = 0;
        await System.Threading.Tasks.Parallel.ForAsync(0, (faces + Stride - 1) / Stride, async (i, _) =>
        {
            int f = i * Stride;
            FaceLightJob uncut = await LightFaceAsync(uncutContext, f, tracer, world.Settings.Compliance);
            FaceLightJob culled = await LightFaceAsync(culledContext, f, tracer, world.Settings.Compliance);
            AssertSameFace(uncut.Result, culled.Result);
            Assert.Equal(0, uncut.CulledLightRecords);
            Assert.Equal(uncut.LightRecords, culled.LightRecords + culled.CulledLightRecords);
            Interlocked.Add(ref culledRecords, culled.CulledLightRecords);
            Interlocked.Add(ref lit, culled.Result is null ? 0 : 1);
        });

        Assert.True(lit > 100, $"{lit} faces lit");
        Assert.True(culledRecords > 1000, $"{culledRecords} records culled");
    }

    private static async Task<FaceLightJob> LightFaceAsync(
        FaceLightContext context, int face, IRayTracer tracer, ComplianceOptions compliance)
    {
        FaceLightJob job = new(context, face);
        job.Prepare(new WindingArena { Compliance = compliance });
        while (!job.Done)
        {
            job.RunRound();
            LightRayLog rays = job.Rays;
            ulong[] bits = new ulong[(rays.VisibilityCount + 63) / 64];
            HitId[] hits = new HitId[rays.SkyCount];
            HitId[] hits2 = new HitId[rays.Sky2Count];
            await tracer.TraceVisibilityAsync(rays.VisibilityRays().ToArray(), bits, RayTraceOptions.StockExact);
            await tracer.TraceClosestAsync(rays.SkyRays().ToArray(), hits, RayTraceOptions.StockExact);

            // The sandbox has a 3D skybox: the recursion's rays need answers too.
            await tracer.TraceClosestAsync(rays.Sky2Memory, hits2, RayTraceOptions.StockExact);
            rays.BeginReplay(bits, 0, hits, 0, hits2);
            job.RunRound();
        }

        return job;
    }

    private static async Task<(BspData Bsp, TextureLightTable TexLights)> CompileSandboxAsync(bool synthetic)
    {
        byte[] vmf = await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf")!);
        InMemoryFileSystem disk = new();
        IReadOnlyDictionary<string, byte[]> files = synthetic ? SyntheticContent.Build() : new Dictionary<string, byte[]>();
        foreach ((string path, byte[] bytes) in files)
        {
            disk.AddFile(path, bytes);
        }

        disk.AddFile("maps/ss_sandbox.vmf", vmf);
        IContentFileSystem content = new ContentFileSystem([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);

        VbspContext context = new(VbspOptions.Default, content) { MapBase = "ss_sandbox" };
        MapFile map = await new MapFileReader(context, disk).LoadAsync(VPath.Create("maps/ss_sandbox.vmf"));
        VbspResult vbsp = await Vbsp.CompileAsync(map, context);
        BspData bsp = vbsp.Bsp!;
        PortalFile prt = await PortalFile.ParseAsync(vbsp.Portals!.ToBytes(PortalLineEnding.CrLf));
        _ = await Vvis.ComputeAsync(
            bsp, PortalSet.FromPortalFile(prt), new VisContext { Parallelism = new CompileParallelism { MaxDegree = 2 } });

        RadLightFile rad = files.TryGetValue("lights.rad", out byte[]? radBytes)
            ? await RadLightFile.ParseAsync(radBytes)
            : new RadLightFile();
        return (bsp, new TextureLightTable(rad, "ss_sandbox"));
    }
}
