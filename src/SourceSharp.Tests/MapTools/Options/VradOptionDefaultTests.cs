//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// Pins each default on <see cref="VradOptions"/> to the reference behaviour.
/// </summary>
public class VradOptionDefaultTests
{
    public static TheoryData<string> BooleanOptions => OptionAssert.BooleanProperties<VradOptions>();

    [Theory]
    [MemberData(nameof(BooleanOptions))]
    public void EveryBooleanOptionExceptSupersampleDefaultsToFalse(string property)
    {
        // The reference tool's boolean flags are all off by default except
        // two: supersampling and texture scaling (cleared by the debug-build
        // -notexscale).
        // Each has its own fact below.
        if (property is nameof(VradOptions.Supersample) or nameof(VradOptions.TexScale))
        {
            return;
        }

        Assert.False(OptionAssert.BooleanValue(VradOptions.Default, property));
    }

    [Fact]
    public void TexScaleDefaultsOn()
    {
        // Texture scaling is on by default.
        Assert.True(VradOptions.Default.TexScale);
    }

    [Fact]
    public void SupersampleDefaultsOn()
    {
        // Supersampling is on by default; stock has only
        // -noextra to turn it off.
        Assert.True(VradOptions.Default.Supersample);
    }

    [Fact]
    public void RangeDefaultsToLdr()
    {
        // The reference parser opens by selecting LDR —
        // "default to LDR".
        Assert.Equal(VradLightingRange.Ldr, VradOptions.Default.Range);
    }

    [Fact]
    public void BouncesDefaultsToStockHundred()
    {
        // The default bounce count is 100.
        Assert.Equal(100, VradOptions.Default.Bounces);
    }

    [Fact]
    public void SkySampleScaleDefaultsToStockOne()
    {
        // The sky sample scale starts at 1.0.
        Assert.Equal(1.0f, VradOptions.Default.SkySampleScale);
    }

    [Fact]
    public void SmoothingAngleDefaultsTo45Degrees()
    {
        // The reference default stores the cosine, not the angle. The angle is 45.
        Assert.Equal(45.0f, VradOptions.Default.SmoothingAngleDegrees);
    }

    [Fact]
    public void TheDefaultSmoothingAngleCosinesToStocksStoredThreshold()
    {
        // The reference default smoothing threshold is 0.7071067, documented
        // as `cos(45.0*(M_PI/180))`. Holding degrees instead of a cosine
        // is only safe if the two agree; this is that check.
        double stockThreshold = 0.7071067;
        double fromDegrees = Math.Cos(VradOptions.Default.SmoothingAngleDegrees * (Math.PI / 180.0));

        Assert.Equal(stockThreshold, fromDegrees, 6);
    }

    [Fact]
    public void LuxelDensityDefaultsToStockOne()
    {
        // The default luxel density is 1.0.
        Assert.Equal(1.0f, VradOptions.Default.LuxelDensity);
    }

    [Fact]
    public void SunAngularExtentDefaultsToStockZero()
    {
        // The sun starts as a point sun: angular extent zero.
        Assert.Equal(0.0f, VradOptions.Default.SunAngularExtentDegrees);
    }

    [Fact]
    public void MaxChopDefaultsToStockFour()
    {
        // The default maxchop is 4.
        Assert.Equal(4.0f, VradOptions.Default.MaxChop);
    }

    [Fact]
    public void MinChopDefaultsToStockFour()
    {
        // The default minchop is 4, which is what -chop sets.
        Assert.Equal(4.0f, VradOptions.Default.MinChop);
    }

    [Fact]
    public void DispChopDefaultsToStockEight()
    {
        // The default dispchop is 8.
        Assert.Equal(8.0f, VradOptions.Default.DispChop);
    }

    [Fact]
    public void MaxDispPatchRadiusDefaultsToStockFifteenHundred()
    {
        // The default maximum displacement patch radius is 1500.
        Assert.Equal(1500.0f, VradOptions.Default.MaxDispPatchRadius);
    }

    [Fact]
    public void MaxDispSampleSizeDefaultsToStockFiveTwelve()
    {
        // The default maximum displacement sample size is 512.
        Assert.Equal(512.0f, VradOptions.Default.MaxDispSampleSize);
    }

    [Fact]
    public void LightsFileDefaultsToNothing()
    {
        // No lights file is requested by default.
        Assert.Null(VradOptions.Default.LightsFile);
    }

    [Fact]
    public void DefaultIsTheSameAsAFreshRecord()
    {
        Assert.Equal(new VradOptions(), VradOptions.Default);
    }

    [Fact]
    public void FastDefaultChangesOnlyFast()
    {
        OptionAssert.OnlyChanged(VradOptions.FastDefault, VradOptions.Default, nameof(VradOptions.Fast), true);
    }

    [Fact]
    public void FinalDefaultAsksForBothRanges()
    {
        Assert.Equal(VradLightingRange.Both, VradOptions.FinalDefault.Range);
    }

    [Fact]
    public void FinalDefaultUsesTheSkySamplingOfStocksFinalFlag()
    {
        // -final sets the sky sample scale to 16.0 and nothing
        // else.
        Assert.Equal(16.0f, VradOptions.FinalDefault.SkySampleScale);
    }
}
