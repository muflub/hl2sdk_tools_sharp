//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// LUMP_DISP_LIGHTMAP_SAMPLE_POSITIONS: <c>CalculateLightmapSamplePositions</c>
/// and its helpers.
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
    /// <c>FindTriIndexMapByUV</c>.
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
    /// <c>(byte)(w * 255.9f)</c>:.
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
    /// There are (U + 1) x (V + 1) samples:.
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

    /// <summary>
    /// Append searches triangles worked out once per displacement; its bytes
    /// are those of searching every sample with <see cref="LightmapSamplePositions.FindTriangleByUv"/>,
    /// the search it replaced, over displacements of every power, on floors
    /// whose luxel coordinates are not whole numbers, with sample grids that
    /// reach past the displacement.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AppendWritesWhatASearchPerSampleWrites(int power)
    {
        foreach ((Vec3 min, float sx, float sy) in (ReadOnlySpan<(Vec3, float, float)>)[
            (Vec3.Zero, 256, 256), (new Vec3(13.7f, -5.3f, 0), 200, 333), (new Vec3(-1000.25f, 77.5f, 12), 97, 61)])
        {
            Vec3[] floor = DispFixtures.FloorQuad(min, sx, sy);
            CoreDispInfo core = DispFixtures.Core(
                DispFixtures.Heightfield(power, floor[0], (x, y) => ((x * 7) + (y * 3)) % 11), floor);
            foreach ((int u, int v) in (ReadOnlySpan<(int, int)>)[
                (core.Surface.LuxelU, core.Surface.LuxelV), (core.Surface.LuxelU + 2, core.Surface.LuxelV + 3), (1, 0)])
            {
                List<byte> fast = [];
                LightmapSamplePositions.Append(core, u, v, fast);
                Assert.Equal(PerSample(core, u, v), fast);
            }
        }
    }

    // The encoding Append writes, one FindTriangleByUv per sample.
    private static List<byte> PerSample(CoreDispInfo core, int u, int v)
    {
        List<byte> output = [];
        for (int y = 0; y <= v; y++)
        {
            for (int x = 0; x <= u; x++)
            {
                if (!LightmapSamplePositions.FindTriangleByUv(core, new DispUv(x + 0.5f, y + 0.5f), out int tri, out Barycentric bary))
                {
                    output.AddRange([0, 0, 0, 0]);
                    continue;
                }

                if (tri < LightmapSamplePositions.LongFormMarker)
                {
                    output.Add((byte)tri);
                }
                else
                {
                    output.Add(LightmapSamplePositions.LongFormMarker);
                    output.Add((byte)(tri - LightmapSamplePositions.LongFormMarker));
                }

                output.Add((byte)(bary.A * 255.9f));
                output.Add((byte)(bary.B * 255.9f));
                output.Add((byte)(bary.C * 255.9f));
            }
        }

        return output;
    }
}
