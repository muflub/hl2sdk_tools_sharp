//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Final;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>
/// the reference implementation's accumulation and sampling, on the light box's floor:
/// 256 units square, 16 units a luxel, so a 17 x 17 grid whose luxel (s, t)
/// sits at world (16 s, -16 (t - 16)) -- t runs along -y from mins -16.
/// </summary>
public sealed class LuxelRadialTests
{
    private static LuxelRadial FloorRadial()
    {
        RadWorld world = LightBox.Build(LightBox.Map());
        FaceLightInfo info = FaceLightInfo.Build(
            world.Geometry, world.Neighbours, 0, Vec3.Zero, world.Settings.SmoothingThreshold);
        LuxelRadial radial = new();
        radial.Reset(info);
        return radial;
    }

    private static Vec3 Luxel(LuxelRadial r, float s, float t) => r.Info.LuxelToWorld(s, t);

    private static LightingValue[] One(float v) => [new LightingValue(new Vec3(v, v, v), 0f)];

    [Fact]
    public void TheFloorsGridIsSeventeenLuxelsSquare()
    {
        LuxelRadial r = FloorRadial();
        Assert.Equal((17, 17), (r.Width, r.Height));
    }

    [Fact]
    public void AWholeSampleCentredHalfALuxelAwayWeighsTwoOnEachLuxelItCovers()
    {
        //: overlap area 1 over Chebyshev distance 0.5.
        LuxelRadial r = FloorRadial();
        r.AddDirect(Luxel(r, 4.5f, 4.5f), 4, 4, 5, 5, One(10), false, false);

        Assert.Equal(2f, r.Weight(4 + (4 * 17)));
        Assert.Equal(2f, r.Weight(5 + (5 * 17)));
    }

    [Fact]
    public void ASampleOnALuxelCentreIsWeightedByAreaOverATenthInDouble()
    {
        // r < 0.1 gives area / 0.1, a double division narrowed.
        LuxelRadial r = FloorRadial();
        r.AddDirect(Luxel(r, 4f, 4f), 3.5f, 3.5f, 4.5f, 4.5f, One(1), false, false);

        Assert.Equal((float)(1.0f / 0.1), r.Weight(4 + (4 * 17)));
    }

    [Fact]
    public void ALuxelTheSampleOverlapsByLessThanEqualEpsilonGetsNothing()
    {
        // Gate: `area > EQUAL_EPSILON`, the double 0.001. A sliver 0.0005 of a
        // luxel wide overlaps each luxel it touches by 0.0005.
        LuxelRadial r = FloorRadial();
        r.AddDirect(Luxel(r, 4f, 4.5f), 4, 4, 4.0005f, 5, One(10), false, false);

        Assert.Equal(0f, r.Weight(4 + (4 * 17)));
        Assert.Equal(0f, r.Weight(5 + (4 * 17)));
    }

    [Fact]
    public void ASampleHangingOffTheGridOnlyTouchesLuxelsOnIt()
    {
        // The window is clamped to [0, w) and [0, h).
        LuxelRadial r = FloorRadial();
        r.AddDirect(Luxel(r, 16.5f, 16.5f), 16, 16, 17, 17, One(10), false, false);

        Assert.Equal(2f, r.Weight(16 + (16 * 17)));
    }

    [Fact]
    public void AnUnbumpedSampleOnABumpedFaceFeedsEachBumpDirectionAtOneOverRootThree()
    {
        LuxelRadial r = FloorRadial();
        r.AddDirect(Luxel(r, 4.5f, 4.5f), 4, 4, 5, 5, One(3), hasBumpmap: true, neighbourHasBumpmap: false);

        int i = 4 + (4 * 17);
        Assert.Equal(3f * 2f, r.Light(0, i).Lighting.X);
        Assert.Equal(3f * (2f * LuxelRadial.OneOverSqrt3), r.Light(2, i).Lighting.X);
    }

    [Fact]
    public void ABouncedPatchWeighsTwoMinusItsSquaredDistanceInPatchUnits()
    {
        // Patch extent 1 luxel: a luxel 0.5 away in s and t is
        // 2 - (0.25 + 0.25) = 1.5.
        LuxelRadial r = FloorRadial();
        r.AddBounced(Luxel(r, 4.5f, 4.5f), 4, 4, 5, 5, [new Vec3(1, 1, 1)], false, false);

        Assert.Equal(1.5f, r.Weight(4 + (4 * 17)));
    }

    [Fact]
    public void ABouncedPatchSmallerThanALuxelIsFilteredAsIfItWereOne()
    {
        // Extents clamped to at least 1.
        LuxelRadial small = FloorRadial();
        small.AddBounced(Luxel(small, 4.5f, 4.5f), 4.4f, 4.4f, 4.6f, 4.6f, [new Vec3(1, 1, 1)], false, false);
        LuxelRadial whole = FloorRadial();
        whole.AddBounced(Luxel(whole, 4.5f, 4.5f), 4, 4, 5, 5, [new Vec3(1, 1, 1)], false, false);

        Assert.Equal(whole.Weight(4 + (4 * 17)), small.Weight(4 + (4 * 17)));
    }

    [Fact]
    public void BouncedLightLeavesTheSunAmountAlone()
    {
        // The bounce path calls AddWeighted(Vector, float), which has no sun term.
        LuxelRadial r = FloorRadial();
        r.AddBounced(Luxel(r, 4.5f, 4.5f), 4, 4, 5, 5, [new Vec3(1, 1, 1)], false, false);

        Assert.Equal(0f, r.Light(0, 4 + (4 * 17)).DirectSunAmount);
    }

    [Fact]
    public void SampleDividesByTheWeightAsADoubleReciprocal()
    {
        //: Scale(1.0 / weight).
        LuxelRadial r = FloorRadial();
        r.AddDirect(Luxel(r, 4.5f, 4.5f), 4, 4, 5, 5, One(7), false, false);
        r.AddDirect(Luxel(r, 3.5f, 3.5f), 3, 3, 4, 4, One(3), false, false);

        LightingValue[] light = new LightingValue[1];
        Assert.True(r.Sample(Luxel(r, 4, 4), light, false, ComplianceOptions.Correct));

        float weight = r.Weight(4 + (4 * 17));
        float expected = r.Light(0, 4 + (4 * 17)).Lighting.X * (float)(1.0 / weight);
        Assert.Equal(expected, light[0].Lighting.X);
    }

    [Fact]
    public void ALuxelNothingReachedIsBlackAndNotABaseSample()
    {
        // The red-to-black default applies.
        LuxelRadial r = FloorRadial();
        LightingValue[] light = new LightingValue[1];

        Assert.False(r.Sample(Luxel(r, 8, 8), light, false, ComplianceOptions.Correct));
        Assert.Equal(Vec3.Zero, light[0].Lighting);
    }

    [Fact]
    public void RedErrorsPaintsALuxelNothingReachedRed()
    {
        LuxelRadial r = FloorRadial();
        LightingValue[] light = new LightingValue[1];
        _ = r.Sample(Luxel(r, 8, 8), light, redErrors: true, ComplianceOptions.Correct);

        Assert.Equal(LuxelRadial.ErrorRed, light[0].Lighting);
    }

    [Fact]
    public void APointOffTheGridIsRedWhateverRedErrorsSays()
    {
        LuxelRadial r = FloorRadial();
        LightingValue[] light = new LightingValue[1];

        Assert.False(r.Sample(Luxel(r, 40, 8), light, redErrors: false, ComplianceOptions.Correct));
        Assert.Equal(LuxelRadial.ErrorRed, light[0].Lighting);
        Assert.Equal(1, r.OffGrid);
    }

    [Fact]
    public void StocksEdgeTestLetsAPointOnePastTheLastColumnThrough()
    {
        LuxelRadial r = FloorRadial();
        Assert.False(r.IsOffGrid(17, 3, ComplianceOptions.Stock));
    }

    [Fact]
    public void TheCorrectEdgeTestPutsAPointOnePastTheLastColumnOffTheGrid()
    {
        LuxelRadial r = FloorRadial();
        Assert.True(r.IsOffGrid(17, 3, ComplianceOptions.Correct));
    }

    [Fact]
    public void ResetClearsEverythingTheLastFaceAccumulated()
    {
        LuxelRadial r = FloorRadial();
        r.AddDirect(Luxel(r, 4.5f, 4.5f), 4, 4, 5, 5, One(7), false, false);
        r.Reset(r.Info);

        Assert.Equal(0f, r.Weight(4 + (4 * 17)));
        Assert.Equal(Vec3.Zero, r.Light(0, 4 + (4 * 17)).Lighting);
    }
}
