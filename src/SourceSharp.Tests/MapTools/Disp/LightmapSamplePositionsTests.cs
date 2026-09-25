using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// LUMP_DISP_LIGHTMAP_SAMPLE_POSITIONS: <c>CalculateLightmapSamplePositions</c>
/// and its helpers, <c>utils/vbsp/disp_vbsp.cpp:47-137</c>.
/// </summary>
public sealed class LightmapSamplePositionsTests
{
    private static readonly Vec3[] Floor = DispFixtures.UnitFloor();

    /// <summary>A counter-clockwise triangle has positive doubled area: <c>TriArea2DTimesTwo</c>.</summary>
    [Fact]
    public void ACounterClockwiseTriangleHasPositiveArea()
    {
        Assert.Equal(2.0f, LightmapSamplePositions.TriArea2DTimesTwo(new(0, 0), new(1, 0), new(0, 2)));
    }

    /// <summary>A clockwise one has negative area.</summary>
    [Fact]
    public void AClockwiseTriangleHasNegativeArea()
    {
        Assert.True(LightmapSamplePositions.TriArea2DTimesTwo(new(0, 0), new(0, 2), new(1, 0)) < 0);
    }

    /// <summary>
    /// The barycentric weights of a vertex are one for it and zero for the
    /// others: <c>GetBarycentricCoords2D</c>.
    /// </summary>
    [Fact]
    public void AVertexHasWeightOneForItself()
    {
        Barycentric b = LightmapSamplePositions.BarycentricCoords2D(new(0, 0), new(4, 0), new(0, 4), new(4, 0));

        Assert.Equal(new Barycentric(0, 1, 0), b);
    }

    /// <summary>
    /// A sample outside every triangle is not found:
    /// <c>FindTriIndexMapByUV</c>, <c>disp_vbsp.cpp:58-91</c>.
    /// </summary>
    [Fact]
    public void ASampleOutsideTheDisplacementIsNotFound()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);

        Assert.False(LightmapSamplePositions.FindTriangleByUv(core, new DispUv(-3, -3), out int tri, out _));
        Assert.Equal(-1, tri);
    }

    /// <summary>
    /// The first luxel centre (0.5, 0.5) is the first vertex, and some
    /// triangle of <c>CPowerInfo</c>'s list contains it:
    /// <c>disp_vbsp.cpp:66-88</c>.
    /// </summary>
    [Fact]
    public void TheFirstLuxelIsFound()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);

        Assert.True(LightmapSamplePositions.FindTriangleByUv(core, new DispUv(0.5f, 0.5f), out int tri, out _));
        Assert.InRange(tri, 0, core.TriCount - 1);
    }

    /// <summary>
    /// A found sample is one index byte and three weights, each
    /// <c>(byte)(w * 255.9f)</c>: <c>disp_vbsp.cpp:112-125</c>.
    /// </summary>
    [Fact]
    public void AFoundSampleIsAnIndexAndThreeTruncatedWeights()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);
        List<byte> output = [];

        LightmapSamplePositions.Append(core, 0, 0, output);

        // A 1x1 grid of samples at (0.5, 0.5), which is vertex 0: one weight
        // is exactly 1 and truncates to 255.
        Assert.Equal(4, output.Count);
        Assert.Equal(255, output.Skip(1).Max());
    }

    /// <summary>
    /// There are (U + 1) x (V + 1) samples: <c>disp_vbsp.cpp:95-96</c>.
    /// </summary>
    [Fact]
    public void ThereIsOneSamplePerLuxelCornerPlusOne()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);
        List<byte> output = [];

        LightmapSamplePositions.Append(core, 3, 2, output);

        Assert.Equal(4 * 3 * 4, output.Count);
    }

    /// <summary>
    /// A triangle index of 255 or more takes two bytes, 255 then the rest:
    /// <c>disp_vbsp.cpp:114-122</c>.
    /// </summary>
    [Fact]
    public void AHighTriangleIndexTakesTwoBytes()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(4, Floor[0]), Floor);
        List<byte> output = [];

        LightmapSamplePositions.Append(core, core.Surface.LuxelU, core.Surface.LuxelV, output);

        int samples = (core.Surface.LuxelU + 1) * (core.Surface.LuxelV + 1);
        Assert.True(output.Count > samples * 4, $"{output.Count} bytes for {samples} samples");
    }
}
