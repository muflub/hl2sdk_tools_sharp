//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary><c>ssmap vrad</c>, on an in-memory file system.</summary>
public sealed class VradCommandTests
{
    /// <summary>
    /// Where the command looks for a rooted path it is given: it resolves the
    /// map against the host (<c>/maps/box</c> is <c>D:\maps\box</c> on
    /// Windows), so the in-memory file goes where that lands.
    /// </summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    private static async Task<InMemoryFileSystem> MapAsync()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200")));

        // The CLI builds its own tracer from the map's brushes, which walks the
        // tree: one node whose both sides are the one leaf, and no brushes.
        SourceSharp.MapFormats.Bsp.Structs.DNode node = new() { PlaneNum = 0 };
        node.Children[0] = -1;
        node.Children[1] = -1;
        map.Nodes = [node];
        using MemoryStream stream = new();
        await BspFile.SaveAsync(map.Build(), stream, BspWriteMode.Canonical);
        InMemoryFileSystem fs = new();
        fs.AddFile(Rooted("/maps/box.bsp"), stream.ToArray());
        return fs;
    }

    [Fact]
    public async Task ListComplianceNamesTheVradQuirksAndCompilesNothing()
    {
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(new InMemoryFileSystem(), ["-listcompliance"], output);

        Assert.Equal((Program.ExitSuccess, true), (exit, output.ToString().Contains(nameof(StockQuirk.FastFinalLightStyleZero), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NoMapIsAUsageError()
    {
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await VradCommand.RunAsync(new InMemoryFileSystem(), ["-bounce", "0"], output));
    }

    [Fact]
    public async Task AMissingMapFails()
    {
        using StringWriter output = new();
        Assert.Equal(VradCommand.ExitFailed, await VradCommand.RunAsync(new InMemoryFileSystem(), ["/maps/nothere"], output));
    }

    [Fact]
    public async Task TheMapIsLitAndWrittenBack()
    {
        InMemoryFileSystem fs = await MapAsync();
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(fs, [VradCommand.NoGameContentSwitch, "-bounce", "0", "-threads", "2", "/maps/box"], output);

        BspData lit = await BspFile.LoadAsync(new MemoryStream(fs.GetBytes(VPath.Create(Rooted("/maps/box.bsp")))!));
        Assert.Equal((Program.ExitSuccess, false), (exit, lit[BspLump.Lighting].IsEmpty));
    }

    private sealed class FixedSteam(string directory) : ISteamAppLocator
    {
        public List<int> Asked { get; } = [];

        public ValueTask<VPath?> FindInstallDirectoryAsync(int appId, CancellationToken cancellationToken = default)
        {
            Asked.Add(appId);
            return ValueTask.FromResult<VPath?>(VPath.Create(directory));
        }
    }

    private const string SteamGameInfo = """
        "GameInfo"
        {
            game "steam game"
            FileSystem
            {
                SteamAppId 243750
                SearchPaths
                {
                    game+mod |gameinfo_path|.
                    game |appid_243750|hl2/hl2_misc.vpk
                }
            }
        }
        """;

    // The box under game/maps, beside a gameinfo that mounts a Steam app.
    private static async Task<InMemoryFileSystem> SteamMapAsync()
    {
        InMemoryFileSystem fs = await MapAsync();
        fs.AddFile(Rooted("/game/maps/box.bsp"), fs.GetBytes(VPath.Create(Rooted("/maps/box.bsp")))!);
        fs.AddFile(Rooted("/game/gameinfo.txt"), System.Text.Encoding.UTF8.GetBytes(SteamGameInfo));
        fs.AddFile(Rooted("/steam/common/Half-Life 2/hl2/readme.txt"), [1]);
        return fs;
    }

    [Fact]
    public async Task AGameThatMountsASteamAppIsFoundThroughTheLocator()
    {
        InMemoryFileSystem fs = await SteamMapAsync();
        FixedSteam steam = new(Rooted("/steam/common/Half-Life 2"));
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(
            fs, ["-bounce", "0", "-threads", "2", "-game", "/game", "/game/maps/box"], steam, output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains(243750, steam.Asked);
        Assert.DoesNotContain("cannot mount", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASteamLibraryTheMapIsLitWithoutGameContent()
    {
        // Only under the switch: the mount's failure is printed, and the
        // compile carries on with a note instead of failing.
        InMemoryFileSystem fs = await SteamMapAsync();
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(
            fs, [VradCommand.NoGameContentSwitch, "-bounce", "0", "-threads", "2", "-game", "/game", "/game/maps/box"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains($"ssmap vrad: cannot mount {Path.GetFullPath("/game")}:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ssmap vrad: no game content; lighting without it", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASteamLibraryAGameThatNamesAnAppFailsTheCompile()
    {
        // Stock stops on a game it cannot mount, and so does vbsp: a .bsp lit
        // without the game's materials and lights.rad is not the compile
        // that was asked for, so it must not come back with a success code.
        InMemoryFileSystem fs = await SteamMapAsync();
        byte[] before = fs.GetBytes(VPath.Create(Rooted("/game/maps/box.bsp")))!;
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(
            fs, ["-bounce", "0", "-threads", "2", "-game", "/game", "/game/maps/box"], output);

        Assert.True(exit == VradCommand.ExitFailed, output.ToString());
        Assert.Contains($"ssmap vrad: cannot mount {Path.GetFullPath("/game")}:", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, fs.GetBytes(VPath.Create(Rooted("/game/maps/box.bsp"))));
    }

    [Fact]
    public async Task AGameDirectoryWithNoGameInfoFailsTheCompile()
    {
        // -game names a directory with no gameinfo.txt in it.
        InMemoryFileSystem fs = await MapAsync();
        byte[] before = fs.GetBytes(VPath.Create(Rooted("/maps/box.bsp")))!;
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(fs, ["-bounce", "0", "-game", "/nogame", "/maps/box"], output);

        Assert.True(exit == VradCommand.ExitFailed, output.ToString());
        Assert.Contains(
            $"ssmap vrad: cannot mount {Path.GetFullPath("/nogame")}: no gameinfo.txt there",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(before, fs.GetBytes(VPath.Create(Rooted("/maps/box.bsp"))));
    }

    [Fact]
    public async Task NoGameInfoAboveTheMapFailsTheCompileToo()
    {
        // No -game: the directory above maps/ is used, as vbsp uses it, and
        // it has no gameinfo.txt either.
        InMemoryFileSystem fs = await MapAsync();
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(fs, ["-bounce", "0", "/maps/box"], output);

        Assert.True(exit == VradCommand.ExitFailed, output.ToString());
        Assert.Contains(VradCommand.NoGameContentSwitch, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoGameInfoUnderTheSwitchIsANote()
    {
        InMemoryFileSystem fs = await MapAsync();
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(
            fs, [VradCommand.NoGameContentSwitch, "-bounce", "0", "-game", "/nogame", "/maps/box"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains(
            $"ssmap vrad: no game content (no gameinfo.txt in {Path.GetFullPath("/nogame")})",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BenchPrintsEveryStageAndTheWorkCounters()
    {
        InMemoryFileSystem fs = await MapAsync();
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(
            fs, [VradCommand.NoGameContentSwitch, "-bounce", "0", "-threads", "2", VradCommand.BenchSwitch, "/maps/box"], output);

        string text = output.ToString();
        Assert.Equal(Program.ExitSuccess, exit);
        foreach (string stage in (string[])[SourceSharp.MapTools.Rad.Vrad.LoadStage, SourceSharp.MapTools.Rad.Vrad.StartStage, SourceSharp.MapTools.Rad.Vrad.FacelightsStage, SourceSharp.MapTools.Rad.Vrad.FinalStage, SourceSharp.MapTools.Rad.Vrad.OtherStage])
        {
            Assert.Contains($"bench {stage} ", text, StringComparison.Ordinal);
        }

        Assert.Contains("bench total ", text, StringComparison.Ordinal);
        Assert.Contains("bench work ldr samples=", text, StringComparison.Ordinal);

        // How many (group, light) records were gathered and how many the
        // dead-record cull left out.
        Assert.Matches(@"bench work ldr .* lightrecords=\d+ culledlightrecords=\d+", text);
    }

    [Fact]
    public async Task WithoutBenchNothingIsTimed()
    {
        InMemoryFileSystem fs = await MapAsync();
        using StringWriter output = new();
        await VradCommand.RunAsync(fs, [VradCommand.NoGameContentSwitch, "-bounce", "0", "-threads", "2", "/maps/box"], output);

        Assert.DoesNotContain("bench ", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMapWithNoCastersStillGetsItsLeafAmbient()
    {
        // The box has no brushes, so the tracer is the empty scene's. Leaf
        // ambient used to need the KD tracer itself and was reported as not
        // ported (VRAD0701) here; through the tracer seam, an empty scene
        // answers its segments (nothing blocks) and the stage runs. The floor
        // is a dim texlight (the level's .rad): dim enough at 512 units to be
        // baked into the cubes, and surface lights baked into the cubes are
        // the ones whose visibility the stage asks the tracer. Without one the
        // tracer is never consulted and the cubes would match whatever it
        // answered.
        InMemoryFileSystem fs = await MapAsync();
        fs.AddText(Rooted("/maps/box.rad"), "concrete/floor 255 255 255 1\n");
        using StringWriter output = new();
        int exit = await VradCommand.RunAsync(fs, [VradCommand.NoGameContentSwitch, "-bounce", "0", "/maps/box.bsp"], output);

        BspData lit = await BspFile.LoadAsync(new MemoryStream(fs.GetBytes(VPath.Create(Rooted("/maps/box.bsp")))!));
        Assert.Equal(Program.ExitSuccess, exit);
        Assert.DoesNotContain("VRAD0701", output.ToString(), StringComparison.Ordinal);
        Assert.False(lit[BspLump.LeafAmbientIndex].IsEmpty);

        // The cubes are the ones the builder gives over a KD tree that blocks
        // nothing: one triangle far outside the box, so the answer comes from
        // real traversal rather than from the empty scene's "never blocked".
        // Built from the lit map, whose lightmaps and world lights are what
        // the stage read, and with the options the command's parse gives.
        KdRayTracer nothingBlocks = KdRayTracer.Build(
        [
            new TracedTriangle(
                SourceSharp.MapTools.Rad.TraceId.Opaque,
                new Vec3(-100000, -100000, -100000), new Vec3(-99990, -100000, -100000), new Vec3(-100000, -99990, -100000), 0),
        ]);
        VradOptions options = StockArgs.ParseVrad(["-bounce", "0", "/maps/box.bsp"]).Options;
        SourceSharp.MapTools.Rad.Ambient.AmbientScene scene =
            SourceSharp.MapTools.Rad.Ambient.AmbientScene.Create(lit, SourceSharp.MapTools.Rad.Ambient.LightingMode.Ldr);
        SourceSharp.MapTools.Rad.Ambient.LeafAmbientResult expected = await SourceSharp.MapTools.Rad.Ambient.LeafAmbientBuilder.BuildAsync(
            scene,
            scene.WorldLights.ToArray(),
            new SourceSharp.MapTools.Rad.Ambient.LeafAmbientOptions { Compliance = options.Compliance, FastAmbient = options.FastAmbient },
            new SourceSharp.MapTools.Rad.Ambient.TracerLineVisibility(nothingBlocks, options.Compliance),
            CancellationToken.None);

        Assert.True(expected.LightsInAmbientCube > 0, $"{expected.LightsInAmbientCube} lights in the cubes");
        DLeafAmbientLighting[] cubes = SourceSharp.MapFormats.Bsp.Structs.BspStructView.As<DLeafAmbientLighting>(lit[BspLump.LeafAmbientLighting]).ToArray();
        Assert.Equal(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected.Lighting.AsSpan()).ToArray(),
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(cubes.AsSpan()).ToArray());

        // And the entity light reached them: a cube of zeros everywhere would
        // match a builder that saw no light at all.
        Assert.Contains(
            cubes,
            c => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new[] { c.Cube }.AsSpan()).ToArray().Any(b => b != 0));
    }

    [Fact]
    public async Task TheLevelRadBesideTheMapIsRead()
    {
        InMemoryFileSystem fs = await MapAsync();
        fs.AddText(Rooted("/maps/box.rad"), "concrete/floor 255 255 255 200\n");
        using StringWriter output = new();
        _ = await VradCommand.RunAsync(fs, [VradCommand.NoGameContentSwitch, "-bounce", "0", "/maps/box.bsp"], output);

        // Without it the box has exactly its one entity light; the texlight
        // floor adds its patches' lights.
        Assert.DoesNotContain("[LDR] 1 direct lights", output.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>The vrad switches p4f added to <see cref="VradOptions"/> and their parse.</summary>
public sealed class VradOptionAdditionsTests
{
    private const string Map = "map";

    [Fact]
    public void ScaleSetsLightScale()
    {
        Assert.Equal(2.5f, StockArgs.ParseVrad(["-scale", "2.5", Map]).Options.LightScale);
    }

    [Fact]
    public void DlightSetsTheTexlightThreshold()
    {
        Assert.Equal(0.5f, StockArgs.ParseVrad(["-dlight", "0.5", Map]).Options.DLightThreshold);
    }

    [Fact]
    public void AmbientIsTakenTimes128()
    {
        Assert.Equal(new Vec3(128, 64, 0), StockArgs.ParseVrad(["-ambient", "1", "0.5", "0", Map]).Options.Ambient);
    }

    [Fact]
    public void NoTexScaleClearsTexScale()
    {
        Assert.False(StockArgs.ParseVrad(["-notexscale", Map]).Options.TexScale);
    }

    [Fact]
    public void TheDefaultsAreStocksGlobals()
    {
        //, 108.
        VradOptions o = VradOptions.Default;
        Assert.Equal((1.0f, 0.1f, Vec3.Zero, true), (o.LightScale, o.DLightThreshold, o.Ambient, o.TexScale));
    }

    [Fact]
    public void TheNewSwitchesReachTheLightingSettings()
    {
        DirectLightingSettings s = DirectLightingSettings.FromVrad(
            VradOptions.Default with { LightScale = 2, DLightThreshold = 0.3f, Ambient = new Vec3(1, 2, 3), TexScale = false },
            hdr: false);
        Assert.Equal((2f, 0.3f, new Vec3(1, 2, 3), false), (s.LightScale, s.DLightThreshold, s.Ambient, s.TexScale));
    }

    [Fact]
    public void ALuxelDensityAboveOneIsRefusedByTheSettingsLikeOneBelow()
    {
        //: "-luxeldensity 2" is a density of 0.5.
        Assert.Throws<NotSupportedException>(
            () => DirectLightingSettings.FromVrad(VradOptions.Default with { LuxelDensity = 2 }, hdr: false));
    }
}

/// <summary>Rays as stock's <c>TestLine</c> builds them, and the tracer's reading of MaxDistance.</summary>
public sealed class StockRayTests
{
    private static KdRayTracer Wall()
    {
        // A wall across x = 10.
        Vec3 a = new(10, -50, -50), b = new(10, 50, -50), c = new(10, 50, 50), d = new(10, -50, 50);
        return KdRayTracer.Build([new TracedTriangle(7, a, b, c, 0), new TracedTriangle(7, a, c, d, 0)]);
    }

    [Fact]
    public void AStockRaysLengthIsItsFarLimit()
    {
        Ray ray = LightRayLog.MakeStockRay(new Vec3(0, 0, 0), new Vec3(30, 40, 0));
        Assert.Equal(50f, ray.MaxDistance);
    }

    [Fact]
    public void AStockRaysDirectionIsTheSegmentTimesTheReciprocalEstimateOfItsLength()
    {
        Ray ray = LightRayLog.MakeStockRay(new Vec3(0, 0, 0), new Vec3(30, 40, 0));
        Assert.Equal(30f * StockSimd.Reciprocal(50f, estimate: true), ray.DirectionX);
    }

    [Fact]
    public void AUnitDirectionTracedToItsLengthReportsTheFractionOfThatLength()
    {
        HitId[] hits = new HitId[1];
        Wall().TraceClosest([new Ray(0, 0, 0, 1, 0, 0, 20)], hits, RayTraceOptions.StockExact);

        Assert.Equal(0.5f, hits[0].Fraction);
    }

    [Fact]
    public void TheWholeSegmentAsTheDirectionReportsTheSameFraction()
    {
        HitId[] hits = new HitId[1];
        Wall().TraceClosest([new Ray(0, 0, 0, 20, 0, 0, 1)], hits, RayTraceOptions.StockExact);

        Assert.Equal(0.5f, hits[0].Fraction);
    }

    [Fact]
    public void AHitBeyondTheLengthIsNotBlockingForTheGather()
    {
        HitId[] hits = new HitId[1];
        Wall().TraceClosest([new Ray(0, 0, 0, 1, 0, 0, 5)], hits, RayTraceOptions.StockExact);

        Assert.False(LightRayLog.IsBlocking(hits[0]));
    }

    [Fact]
    public void StockConvertsDegreesWithTheFloatReciprocalOf180()
    {
        // StockQuirk.DegreesToRadiansByReciprocal: measured on ss_sandbox's sun.
        Assert.Equal((300f * (1.0f / 180)) * Math.PI, LightNormals.Radians(300f, reciprocal: true));
    }

    [Fact]
    public void TheCorrectConversionDividesAsTheSourceSays()
    {
        Assert.Equal((300f / 180) * Math.PI, LightNormals.Radians(300f, reciprocal: false));
    }

    [Fact]
    public void TheTwoConversionsDisagreeAtThreeHundredDegrees()
    {
        Assert.NotEqual(LightNormals.Radians(300f, reciprocal: true), LightNormals.Radians(300f, reciprocal: false));
    }
}
