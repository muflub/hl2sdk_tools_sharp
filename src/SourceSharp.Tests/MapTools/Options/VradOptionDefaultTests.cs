using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// Every default on <see cref="VradOptions"/> is stock's default, checked
/// against the line of <c>src/utils/vrad/vrad.cpp</c> it was read from.
/// </summary>
public class VradOptionDefaultTests
{
    public static TheoryData<string> BooleanOptions => OptionAssert.BooleanProperties<VradOptions>();

    [Theory]
    [MemberData(nameof(BooleanOptions))]
    public void EveryBooleanOptionExceptSupersampleDefaultsToFalse(string property)
    {
        // vrad's boolean globals are all false at vrad.cpp:57-121 except two:
        // `do_extra`, true at vrad.cpp:100 (Supersample), and `texscale`, true
        // at vrad.cpp:108 (TexScale, cleared by the debug-build -notexscale).
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
        // vrad.cpp:108 -- `qboolean texscale = true;`.
        Assert.True(VradOptions.Default.TexScale);
    }

    [Fact]
    public void SupersampleDefaultsOn()
    {
        // vrad.cpp:100 -- `qboolean do_extra = true;`. Stock has only
        // -noextra to turn it off.
        Assert.True(VradOptions.Default.Supersample);
    }

    [Fact]
    public void RangeDefaultsToLdr()
    {
        // vrad.cpp:2387 -- ParseCommandLine opens with SetHDRMode(false),
        // above its own comment "default to LDR".
        Assert.Equal(VradLightingRange.Ldr, VradOptions.Default.Range);
    }

    [Fact]
    public void BouncesDefaultsToStockHundred()
    {
        // vrad.cpp:51 -- `unsigned numbounce = 100;`
        Assert.Equal(100, VradOptions.Default.Bounces);
    }

    [Fact]
    public void SkySampleScaleDefaultsToStockOne()
    {
        // vrad.cpp:89 -- `float g_flSkySampleScale = 1.0;`
        Assert.Equal(1.0f, VradOptions.Default.SkySampleScale);
    }

    [Fact]
    public void SmoothingAngleDefaultsTo45Degrees()
    {
        // vrad.cpp:105 stores the cosine, not the angle. The angle is 45.
        Assert.Equal(45.0f, VradOptions.Default.SmoothingAngleDegrees);
    }

    [Fact]
    public void TheDefaultSmoothingAngleCosinesToStocksStoredThreshold()
    {
        // vrad.cpp:105 -- `float smoothing_threshold = 0.7071067;` with the
        // comment `cos(45.0*(M_PI/180))`. Holding degrees instead of a cosine
        // is only safe if the two agree; this is that check.
        double stockThreshold = 0.7071067;
        double fromDegrees = Math.Cos(VradOptions.Default.SmoothingAngleDegrees * (Math.PI / 180.0));

        Assert.Equal(stockThreshold, fromDegrees, 6);
    }

    [Fact]
    public void LuxelDensityDefaultsToStockOne()
    {
        // vrad.cpp:111 -- `float luxeldensity = 1.0;`
        Assert.Equal(1.0f, VradOptions.Default.LuxelDensity);
    }

    [Fact]
    public void SunAngularExtentDefaultsToStockZero()
    {
        // vrad.cpp:87 -- `float g_SunAngularExtent=0.0;`, a point sun.
        Assert.Equal(0.0f, VradOptions.Default.SunAngularExtentDegrees);
    }

    [Fact]
    public void MaxChopDefaultsToStockFour()
    {
        // vrad.cpp:53 -- `float maxchop = 4;`
        Assert.Equal(4.0f, VradOptions.Default.MaxChop);
    }

    [Fact]
    public void MinChopDefaultsToStockFour()
    {
        // vrad.cpp:54 -- `float minchop = 4;`, which is what -chop sets.
        Assert.Equal(4.0f, VradOptions.Default.MinChop);
    }

    [Fact]
    public void DispChopDefaultsToStockEight()
    {
        // vrad.cpp:55 -- `float dispchop = 8.0f;`
        Assert.Equal(8.0f, VradOptions.Default.DispChop);
    }

    [Fact]
    public void MaxDispPatchRadiusDefaultsToStockFifteenHundred()
    {
        // vrad.cpp:56 -- `float g_MaxDispPatchRadius = 1500.0f;`
        Assert.Equal(1500.0f, VradOptions.Default.MaxDispPatchRadius);
    }

    [Fact]
    public void MaxDispSampleSizeDefaultsToStockFiveTwelve()
    {
        // vrad_dispcoll.cpp:18 -- `float g_flMaxDispSampleSize = 512.0f;`
        Assert.Equal(512.0f, VradOptions.Default.MaxDispSampleSize);
    }

    [Fact]
    public void LightsFileDefaultsToNothing()
    {
        // vrad.cpp:78 -- `char designer_lights[MAX_PATH] = "";`
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
        // vrad.cpp:2514 -- -final sets g_flSkySampleScale = 16.0 and nothing
        // else.
        Assert.Equal(16.0f, VradOptions.FinalDefault.SkySampleScale);
    }
}
