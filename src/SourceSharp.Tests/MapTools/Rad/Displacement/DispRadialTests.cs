using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Displacement;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// The displacement radial's arithmetic (<c>vraddisps.cpp:855-1087, 1108-1172</c>)
/// and <c>PreGetBumpNormalsForDisp</c> (<c>vrad.cpp:1494</c>).
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
}
