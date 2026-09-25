using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Displacement;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

using static SourceSharp.Tests.MapTools.Rad.Displacement.DispTestSurfaces;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// <c>BuildDispSamples</c>, <c>BuildDispLuxels</c> and
/// <c>BuildDispSamplesAndLuxels_DoFast</c>,
/// and the two <c>-fast</c> stock quirks.
/// </summary>
public sealed class DispSampleBuilderTests
{
    // A 256-unit floor at 16 units per luxel: a 17 x 17 lightmap.
    private const int W = 17;

    private static FaceLight Full(VradDispSurface s)
    {
        FaceLight fl = new(0, 1);
        DispSampleBuilder.BuildSamples(s, W, W, fl);
        DispSampleBuilder.BuildLuxels(s, W, W, fl);
        return fl;
    }

    private static FaceLight Fast(VradDispSurface s, ComplianceOptions compliance)
    {
        FaceLight fl = new(0, 1);
        DispSampleBuilder.BuildSamplesAndLuxelsFast(s, W, W, Tex(), fl, compliance);
        return fl;
    }

    [Fact]
    public void ThereIsOneSamplePerLuxel()
    {
        FaceLight fl = Full(Surface());
        Assert.Equal(W * W, fl.Samples.Length);
        Assert.Equal(W * W, fl.Luxels.Length);
    }

    [Fact]
    public void ASampleSitsAtItsCellCentre()
    {
        LightSample s = Full(Surface()).Samples[(3 * W) + 5];
        Assert.Equal(5, s.S);
        Assert.Equal(3, s.T);
        Assert.Equal((5 * (1.0f / W)) + ((1.0f / W) * 0.5f), s.CoordS);
        Assert.Equal((3 * (1.0f / W)) + ((1.0f / W) * 0.5f), s.CoordT);
    }

    [Fact]
    public void ASampleIsPushedOneUnitOffTheSurface()
    {
        LightSample s = Full(Surface()).Samples[40];
        Assert.Equal(1.0f, s.Position.Z, 5);
    }

    [Fact]
    public void ASamplesAreaIsItsCellsWindingArea()
    {
        // 256 / 17 units a side on a flat floor.
        float side = 256.0f / W;
        Assert.Equal(side * side, Full(Surface()).Samples[40].Area, 1);
    }

    [Fact]
    public void TheSampleAreasSumToTheSurface()
    {
        double total = Full(Surface((x, y) => x * 16.0f)).Samples.Sum(s => (double)s.Area);
        double expected = 256.0 * Math.Sqrt((256.0 * 256.0) + (64.0 * 64.0));
        Assert.Equal(expected, total, 0);
    }

    [Fact]
    public void TheFirstLuxelIsTheFirstVertexPushedUp()
    {
        VradDispSurface surface = Surface();
        Assert.True(Near(Full(surface).Luxels[0], surface.Verts[0] + new Vec3(0, 0, 1), 1e-5f));
    }

    [Fact]
    public void TheLastLuxelIsOnTheLastVertex()
    {
        VradDispSurface surface = Surface();
        Vec3 last = Full(surface).Luxels[(W * W) - 1];
        Assert.True(Near(last, surface.Verts[surface.Size - 1] + new Vec3(0, 0, 1), 1e-3f), last.ToString());
    }

    [Fact]
    public void LuxelNormalsParallelTheLuxels()
    {
        FaceLight fl = Full(Surface());
        Assert.Equal(fl.Luxels.Length, fl.LuxelNormals.Length);
        Assert.True(Near(fl.LuxelNormals[100], new Vec3(0, 0, 1), 1e-6f));
    }

    [Fact]
    public void UnderStockFastTheLastColumnIsLeftAtTheOrigin()
    {
        // StockQuirk.DispFastSamplesPastEdge: u = 1 + half a step is off the surface.
        LightSample s = Fast(Surface((_, _) => 10.0f), ComplianceOptions.Stock).Samples[(4 * W) + W - 1];
        Assert.Equal(Vec3.Zero, s.Position);
        Assert.Equal(Vec3.Zero, s.Normal);
    }

    [Fact]
    public void UnderStockFastTheLastRowIsLeftAtTheOrigin()
    {
        LightSample s = Fast(Surface((_, _) => 10.0f), ComplianceOptions.Stock).Samples[((W - 1) * W) + 4];
        Assert.Equal(Vec3.Zero, s.Normal);
    }

    [Fact]
    public void UnderStockFastAnInteriorSampleIsHalfALuxelOff()
    {
        float step = 1.0f / (W - 1);
        LightSample s = Fast(Surface(), ComplianceOptions.Stock).Samples[(2 * W) + 3];
        Assert.Equal((3 * step) + (step * 0.5f), s.CoordS);
    }

    [Fact]
    public void UnderCorrectFastEverySampleIsOnItsLuxel()
    {
        VradDispSurface surface = Surface((x, y) => x * y * 3.0f);
        FaceLight fast = Fast(surface, ComplianceOptions.Stock.Flipping(StockQuirk.DispFastSamplesPastEdge));
        FaceLight full = Full(surface);
        for (int i = 0; i < W * W; i++)
        {
            Assert.Equal(full.Luxels[i], fast.Samples[i].Position);
        }
    }

    [Fact]
    public void FastLuxelsAreTheSamples()
    {
        FaceLight fl = Fast(Surface(), ComplianceOptions.Correct);
        Assert.Equal(fl.Samples[77].Position, fl.Luxels[77]);
        Assert.Equal(fl.Samples[77].Normal, fl.LuxelNormals[77]);
    }

    [Fact]
    public void UnderStockFastSamplesHaveNoArea()
    {
        // StockQuirk.DispFastSampleAreaZero.
        Assert.Equal(0.0f, Fast(Surface(), ComplianceOptions.Stock).Samples[5].Area);
    }

    [Fact]
    public void UnderCorrectFastSamplesHaveTheWorldAreaPerLuxel()
    {
        FaceLight fl = Fast(Surface(), ComplianceOptions.Stock.Flipping(StockQuirk.DispFastSampleAreaZero));
        Assert.Equal(FaceSampleBuilder.WorldAreaPerLuxel(Tex()), fl.Samples[5].Area);
    }

    [Fact]
    public void QuadAreaIsTheFanArea() =>
        Assert.Equal(12.0f, DispSampleBuilder.QuadArea(new(0, 0, 0), new(0, 3, 0), new(4, 3, 0), new(4, 0, 0)));
}
