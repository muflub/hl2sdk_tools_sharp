//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Displacement;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// The displacement radial's arithmetic(<c>, 1108-1172</c>)
/// and <c>PreGetBumpNormalsForDisp</c>.
/// </summary>
public sealed class DispRadialTests
{
    private static readonly Vec3 Up = new(0, 0, 1);

    private static LightingValue[] Light(float v) =>
        [new(new Vec3(v, v, v), 0), new(new Vec3(1, 1, 1), 0), new(new Vec3(2, 2, 2), 0), new(new Vec3(3, 3, 3), 0)];

    [Fact]
    public void ASampleFacingAwayContributesNothing()
    {
        DispRadialMap r = new(0, 1, 1);
        Vec3 tilted = new(0, 0.99f, 0.14f);
        DispRadial.AddSampleLightToRadial(Vec3.Zero, tilted, Light(10), 100, Vec3.Zero, Up, r, 0, false, false);
        Assert.Equal(0.0f, r.Weight[0]);
    }

    [Fact]
    public void ASampleOutsideTheRadiusContributesNothing()
    {
        DispRadialMap r = new(0, 1, 1);
        DispRadial.AddSampleLightToRadial(new Vec3(10, 0, 0), Up, Light(10), 100, Vec3.Zero, Up, r, 0, false, false);
        Assert.Equal(0.0f, r.Weight[0]);
    }

    [Fact]
    public void TheInfluenceIsOneMinusDistanceSquaredOverRadiusSquaredTimesTheDot()
    {
        DispRadialMap r = new(0, 1, 1);
        DispRadial.AddSampleLightToRadial(new Vec3(5, 0, 0), Up, Light(10), 100, Vec3.Zero, Up, r, 0, false, false);
        Assert.Equal(0.75f, r.Weight[0]);
        Assert.Equal(7.5f, r.Light[0][0].Lighting.X);
    }

    [Fact]
    public void AnUnbumpedNeighbourGivesABumpedLuxelAFiftiethOfItsFlatLight()
    {
        DispRadialMap r = new(0, 1, 1);
        DispRadial.AddSampleLightToRadial(Vec3.Zero, Up, Light(10), 100, Vec3.Zero, Up, r, 0, true, false);
        Assert.Equal(0.05f, r.Weight[0]);
        Assert.Equal(0.5f, r.Light[3][0].Lighting.X);
    }

    [Fact]
    public void ABumpedNeighbourGivesEachBumpNormalItsOwnLight()
    {
        DispRadialMap r = new(0, 1, 1);
        DispRadial.AddSampleLightToRadial(Vec3.Zero, Up, Light(10), 100, Vec3.Zero, Up, r, 0, true, true);
        Assert.Equal(1.0f, r.Weight[0]);
        Assert.Equal(3.0f, r.Light[3][0].Lighting.X);
    }

    [Fact]
    public void SampleRadialDividesByTheWeight()
    {
        DispRadialMap r = new(0, 1, 1);
        r.Weight[0] = 4;
        r.Light[0][0] = new LightingValue(new Vec3(8, 4, 2), 0);
        Span<LightingValue> lb = stackalloc LightingValue[1];
        Assert.True(DispRadial.SampleRadial(r, 0, lb, 1, patch: false));
        Assert.Equal(new Vec3(2, 1, 0.5f), lb[0].Lighting);
    }

    [Fact]
    public void AnUnweightedLuxelIsABadSample()
    {
        Span<LightingValue> lb = stackalloc LightingValue[1];
        Assert.False(DispRadial.SampleRadial(new DispRadialMap(0, 1, 1), 0, lb, 1, patch: false));
        Assert.Equal(Vec3.Zero, lb[0].Lighting);
    }

    [Fact]
    public void AnUnweightedLuxelIsNotAnErrorForThePatchRadial()
    {
        Span<LightingValue> lb = stackalloc LightingValue[1];
        Assert.True(DispRadial.SampleRadial(new DispRadialMap(0, 1, 1), 0, lb, 1, patch: true));
    }

    [Fact]
    public void APatchBehindTheLuxelWeighsNothing()
    {
        DispRadialMap r = new(0, 1, 1);
        Vec3[] light = [new(9, 9, 9)];
        DispRadial.AddPatchLightToRadial(
            Vec3.Zero, new Vec3(0, 0, -1), light, 100, Vec3.Zero, Up, r, 0, false, false, default, false);
        Assert.Equal(0.0f, r.Weight[0]);
    }

    [Fact]
    public void APatchWeighsByDistanceAndFacing()
    {
        DispRadialMap r = new(0, 1, 1);
        Vec3[] light = [new(8, 8, 8)];
        Vec3 halfUp = new(0, 0.8660254f, 0.5f);
        DispRadial.AddPatchLightToRadial(
            new Vec3(5, 0, 0), halfUp, light, 100, Vec3.Zero, Up, r, 0, false, false, default, false);
        Assert.Equal(0.375f, r.Weight[0], 6);
        Assert.Equal(3.0f, r.Light[0][0].Lighting.X, 5);
    }

    [Fact]
    public void AlignedAxesPassTheTextureAxesAndTheNormalThrough()
    {
        TexInfo t = DispTestSurfaces.Tex();
        (Vec3 u, Vec3 v, Vec3 n) = DispBumpBasis.PreGetBumpNormals(t, new Vec3(0.1f, 0.2f, 0.97f), false);
        Assert.Equal(new Vec3(1, 0, 0), u);
        Assert.Equal(new Vec3(0, -1, 0), v);
        Assert.Equal(new Vec3(0.1f, 0.2f, 0.97f), n);
    }

    [Fact]
    public void SwappedLightmapAxesAreConcatenatedIntoTheTextureFrame()
    {
        // tex (x, y), lightmap (y, x): light * tex is the swap, so the axes
        // come back swapped and the z normal unchanged.
        TexInfo t = default;
        t.TextureVecsTexelsPerWorldUnits[0] = 1;
        t.TextureVecsTexelsPerWorldUnits[5] = 1;
        t.LightmapVecsLuxelsPerWorldUnits[1] = 0.0625f;
        t.LightmapVecsLuxelsPerWorldUnits[4] = 0.0625f;
        (Vec3 u, Vec3 v, Vec3 n) = DispBumpBasis.PreGetBumpNormals(t, Up, false);
        Assert.Equal(new Vec3(0, 1, 0), u);
        Assert.Equal(new Vec3(1, 0, 0), v);
        Assert.Equal(Up, n);
        }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFrameProductMatchesTheThreeByThreeMatrixConcatenationBitForBit(bool stockNormalise)
    {
        // The product used to be formed through 3x3 arrays; it is now written
        // out per column. Random frames, most of them disagreeing enough to
        // take the conversion branch, must come out bit-identical to the
        // matrix form, association order included.
        Random random = new(1234);
        int converted = 0;
        for (int n = 0; n < 2000; n++)
        {
            TexInfo t = default;
            for (int i = 0; i < 8; i++)
            {
                t.TextureVecsTexelsPerWorldUnits[i] = (float)((random.NextDouble() * 2) - 1);
                t.LightmapVecsLuxelsPerWorldUnits[i] = (float)((random.NextDouble() * 0.2) - 0.1);
            }

            Vec3 normal = new((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            (Vec3 u, Vec3 v, Vec3 nn) = DispBumpBasis.PreGetBumpNormals(t, normal, stockNormalise);
            (Vec3 eu, Vec3 ev, Vec3 en, bool didConvert) = MatrixForm(t, normal, stockNormalise);
            converted += didConvert ? 1 : 0;
            AssertBits(eu, u);
            AssertBits(ev, v);
            AssertBits(en, nn);
        }

        Assert.True(converted > 1000, $"only {converted} frames took the conversion branch");
    }

    [Fact]
    public void TheConversionBranchAllocatesNothing()
    {
        // Once per displacement sample: the 3x3 arrays were three heap
        // allocations per call.
        TexInfo t = default;
        t.TextureVecsTexelsPerWorldUnits[0] = 1;
        t.TextureVecsTexelsPerWorldUnits[5] = 1;
        t.LightmapVecsLuxelsPerWorldUnits[1] = 0.0625f;
        t.LightmapVecsLuxelsPerWorldUnits[4] = 0.0625f;
        Vec3 sum = DispBumpBasis.PreGetBumpNormals(t, Up, false).U;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            sum += DispBumpBasis.PreGetBumpNormals(t, Up, false).U;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(1001f, sum.Y);
    }

    private static void AssertBits(Vec3 expected, Vec3 actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
    }

    // The earlier formulation, kept as the reference: both frames as 3x3
    // matrices with the axes in columns, their product, its columns read back.
    private static (Vec3 U, Vec3 V, Vec3 N, bool Converted) MatrixForm(TexInfo tex, Vec3 normal, bool stock)
    {
        FloatArray8 t = tex.TextureVecsTexelsPerWorldUnits;
        FloatArray8 l = tex.LightmapVecsLuxelsPerWorldUnits;
        Vec3 texU = VradDispSurface.Normalise(new Vec3(t[0], t[1], t[2]), stock);
        Vec3 texV = VradDispSurface.Normalise(new Vec3(t[4], t[5], t[6]), stock);
        Vec3 lightU = VradDispSurface.Normalise(new Vec3(l[0], l[1], l[2]), stock);
        Vec3 lightV = VradDispSurface.Normalise(new Vec3(l[4], l[5], l[6]), stock);
        bool convert = Math.Abs(Vec3.Dot(texU, lightU)) < DispBumpBasis.AxisDotEpsilon
            || Math.Abs(Vec3.Dot(texV, lightV)) < DispBumpBasis.AxisDotEpsilon;
        if (!convert)
        {
            return (texU, texV, normal, false);
        }

        float[,] a = Columns(lightU, lightV, normal);
        float[,] b = Columns(texU, texV, normal);
        float[,] m = new float[3, 3];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                m[r, c] = (a[r, 0] * b[0, c]) + ((a[r, 1] * b[1, c]) + (a[r, 2] * b[2, c]));
            }
        }

        return (
            new Vec3(m[0, 0], m[1, 0], m[2, 0]),
            new Vec3(m[0, 1], m[1, 1], m[2, 1]),
            new Vec3(m[0, 2], m[1, 2], m[2, 2]),
            true);
    }

    private static float[,] Columns(Vec3 x, Vec3 y, Vec3 z) => new float[,]
    {
        { x.X, y.X, z.X },
        { x.Y, y.Y, z.Y },
        { x.Z, y.Z, z.Z },
    };
}
