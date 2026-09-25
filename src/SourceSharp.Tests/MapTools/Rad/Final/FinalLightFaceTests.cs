using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Final;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary><c>FinalLightFace</c> (<c>radial.cpp:642</c>) on the light box.</summary>
public sealed class FinalLightFaceTests
{
    private static readonly CompileParallelism One = new() { MaxDegree = 1 };

    internal static async Task<RadWorld> LitBoxAsync(
        LightTestMap? map = null, bool stock = false, bool light = true, int bounces = 100, bool fast = false)
    {
        map ??= LightBox.Map();
        if (light)
        {
            map.Entities.Add(LightTestMap.Entity(
                ("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200")));
        }

        IRayTracer tracer = map.Tracer();
        DirectLightingSettings settings = LightBox.Settings(stock) with { Bounces = bounces, Fast = fast };
        RadWorld world = await RadWorld.StartAsync(
            map.Build(), settings, new TextureLightTable(new(), "box"), tracer, One, CancellationToken.None);
        await world.LightFacesAsync(tracer, One, CancellationToken.None);
        return world;
    }

    private static async Task<(FinalLightContext Context, byte[] Data)> FinishAsync(RadWorld world)
    {
        FinalLightContext context = new(world, MacroTextures.None);
        FinalLightingResult result = await FinalLighting.RunAsync(context, One, CancellationToken.None);
        return (context, result.LightData);
    }

    private static ColorRgbExp32 Record(byte[] data, int offset) =>
        new() { R = data[offset], G = data[offset + 1], B = data[offset + 2], Exponent = (sbyte)data[offset + 3] };

    [Fact]
    public async Task EveryLuxelOfTheLitFloorIsWritten()
    {
        (FinalLightContext context, byte[] data) = await FinishAsync(await LitBoxAsync(bounces: 0));
        int ofs = context.Layout.LightOffsets[0];
        for (int j = 0; j < 17 * 17; j++)
        {
            Assert.NotEqual(0, data[ofs + (j * 4)]);
        }
    }

    [Fact]
    public async Task TheAverageColourSitsJustBeforeLightofs()
    {
        // radial.cpp:857, bsplib.h:398: lightofs - (style + 1) * 4. A dark
        // face held at _minlight 1 is 128 everywhere, so its median is 128.
        LightTestMap map = LightBox.Map();
        map.Entities[0] = LightTestMap.Entity(("classname", "worldspawn"), ("_minlight", "1"));
        (FinalLightContext context, byte[] data) = await FinishAsync(await LitBoxAsync(map, light: false, bounces: 0));

        ColorRgbExp32 average = Record(data, context.Layout.LightOffsets[0] - 4);
        Assert.Equal((128, 128, 128, 0), (average.R, average.G, average.B, (int)average.Exponent));
    }

    [Fact]
    public void TheMedianIsElementCountOverTwoOfTheSortedValues()
    {
        // radial.cpp:865-878: FirstInorder, then avgCount >>= 1 steps.
        Assert.Equal(3f, FinalLightFace.Median([4f, 1f, 3f, 2f]));
    }

    [Fact]
    public void TheMedianOfAnOddCountIsTheMiddleValue()
    {
        Assert.Equal(2f, FinalLightFace.Median([3f, 1f, 2f]));
    }

    [Fact]
    public async Task MinlightRaisesADarkFaceToTheEntitysValueTimes128()
    {
        // radial.cpp:676, :811-814: _minlight 1 -> 128 per channel, which
        // ColorRGBExp32 stores as mantissa 128, exponent 0.
        LightTestMap map = LightBox.Map();
        map.Entities[0] = LightTestMap.Entity(("classname", "worldspawn"), ("_minlight", "1"));
        (FinalLightContext context, byte[] data) = await FinishAsync(await LitBoxAsync(map, light: false, bounces: 0));

        ColorRgbExp32 c = Record(data, context.Layout.LightOffsets[0]);
        Assert.Equal((128, 128, 128, 0), (c.R, c.G, c.B, (int)c.Exponent));
    }

    [Fact]
    public async Task ASkyFaceIsNotFinished()
    {
        // radial.cpp:658: TEX_SPECIAL.
        RadWorld world = await LitBoxAsync(LightBox.Map(ceiling: SurfaceFlags.Sky), bounces: 0);
        FinalLightContext context = new(world, MacroTextures.None);

        Assert.Equal(
            FinalFaceOutcome.NotLit,
            FinalLightFace.Run(context, 1, new byte[context.Layout.LightDataSize], new FinalLightScratch()));
    }

    [Fact]
    public async Task ALitFaceIsWritten()
    {
        RadWorld world = await LitBoxAsync(bounces: 0);
        FinalLightContext context = new(world, MacroTextures.None);

        Assert.Equal(
            FinalFaceOutcome.Written,
            FinalLightFace.Run(context, 0, new byte[context.Layout.LightDataSize], new FinalLightScratch()));
    }

    [Fact]
    public async Task StocksFastBranchReadsStyleSlotZeroForEveryStyle()
    {
        // radial.cpp:766.
        RadWorld world = await LitBoxAsync(stock: true, bounces: 0);
        FinalLightContext context = new(world, MacroTextures.None);
        FaceLight fl = TwoStyles(world);
        LightingValue[] lb = new LightingValue[1];

        FinalLightFace.FastLuxel(context, fl, 1, 0, lb);
        Assert.Equal(10f, lb[0].Lighting.X);
    }

    [Fact]
    public async Task TheCorrectFastBranchReadsTheStyleBeingWritten()
    {
        RadWorld world = await LitBoxAsync(stock: false, bounces: 0);
        FinalLightContext context = new(world, MacroTextures.None);
        FaceLight fl = TwoStyles(world);
        LightingValue[] lb = new LightingValue[1];

        FinalLightFace.FastLuxel(context, fl, 1, 0, lb);
        Assert.Equal(20f, lb[0].Lighting.X);
    }

    [Fact]
    public async Task StocksBounceFilterTakesANeighboursBumpinessFromTheFaceItself()
    {
        // radial.cpp:364, 375-381: a bumped ceiling next to unbumped walls.
        RadWorld world = await LitBoxAsync(LightBox.Map(ceiling: SurfaceFlags.BumpLight), stock: true, bounces: 0);
        FinalLightContext context = new(world, MacroTextures.None);

        Assert.True(FinalLightFace.NeighbourPatchBumpmap(context, 1, 2));
    }

    [Fact]
    public async Task TheCorrectBounceFilterAsksTheNeighbour()
    {
        RadWorld world = await LitBoxAsync(LightBox.Map(ceiling: SurfaceFlags.BumpLight), stock: false, bounces: 0);
        FinalLightContext context = new(world, MacroTextures.None);

        Assert.False(FinalLightFace.NeighbourPatchBumpmap(context, 1, 2));
    }

    [Fact]
    public async Task FinishingIsTheSameAtOneAndEightWorkers()
    {
        RadWorld world = await LitBoxAsync(bounces: 0);
        FinalLightContext context = new(world, MacroTextures.None);
        FinalLightingResult one = await FinalLighting.RunAsync(context, One, CancellationToken.None);
        FinalLightingResult eight = await FinalLighting.RunAsync(
            context, new CompileParallelism { MaxDegree = 8 }, CancellationToken.None);

        Assert.Equal(one.LightData, eight.LightData);
    }

    [Fact]
    public async Task APreCancelledTokenFinishesNothing()
    {
        RadWorld world = await LitBoxAsync(bounces: 0);
        FinalLightContext context = new(world, MacroTextures.None);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FinalLighting.RunAsync(context, One, cancelled.Token));
    }

    [Fact]
    public void ContextNeedsALaidOutWorld()
    {
        RadWorld world = LightBox.Build(LightBox.Map());
        Assert.Throws<ArgumentException>(() => new FinalLightContext(world, MacroTextures.None));
    }

    /// <summary>A one-sample facelight with style 0 at 10 and style 1 at 20.</summary>
    private static FaceLight TwoStyles(RadWorld world)
    {
        _ = world;
        FaceLight fl = new(0, 1, [new LightSample()]);
        fl.AllocateStyle(0);
        fl.AllocateStyle(1);
        fl.LightFor(0, 0)![0] = new LightingValue(new Vec3(10, 10, 10), 0);
        fl.LightFor(1, 0)![0] = new LightingValue(new Vec3(20, 20, 20), 0);
        return fl;
    }
}
