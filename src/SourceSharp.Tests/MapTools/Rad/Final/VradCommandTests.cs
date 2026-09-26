using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
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
        int exit = await VradCommand.RunAsync(fs, ["-bounce", "0", "-threads", "2", "/maps/box"], output);

        BspData lit = await BspFile.LoadAsync(new MemoryStream(fs.GetBytes(VPath.Create(Rooted("/maps/box.bsp")))!));
        Assert.Equal((Program.ExitSuccess, false), (exit, lit[BspLump.Lighting].IsEmpty));
    }

    [Fact]
    public async Task TheCommandReportsStagesThatAreNotPortedYet()
    {
        InMemoryFileSystem fs = await MapAsync();
        using StringWriter output = new();
        _ = await VradCommand.RunAsync(fs, ["-bounce", "0", "/maps/box.bsp"], output);

        Assert.Contains("VRAD0701", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLevelRadBesideTheMapIsRead()
    {
        InMemoryFileSystem fs = await MapAsync();
        fs.AddText(Rooted("/maps/box.rad"), "concrete/floor 255 255 255 200\n");
        using StringWriter output = new();
        _ = await VradCommand.RunAsync(fs, ["-bounce", "0", "/maps/box.bsp"], output);

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
