using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Props;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// <c>GatherSampleLightSSE</c>'s one-point form.
/// <c>Pow_FixedPoint_Exponent_SIMD</c>.
/// <c>DirectionalSampler_t</c> and the world-light export
/// inversion, in the fixture room (a sealed 768 x 768 x 384 box).
/// </summary>
public sealed class PropLightSamplerTests : IClassFixture<DetailPropFixture>
{
    private readonly PropLightSampler _exact;

    /// <summary>Takes the shared fixture's ray environment.</summary>
    /// <param name="fixture">The loaded map.</param>
    public PropLightSamplerTests(DetailPropFixture fixture) =>
        _exact = new PropLightSampler(fixture.Environment, ComplianceOptions.Correct);

    private static PropLight Point(Vec3 origin, float c = 0, float l = 0, float q = 1) => new()
    {
        Type = EmitType.Point,
        Origin = origin,
        Intensity = new Vec3(1, 1, 1),
        ConstantAttn = c,
        LinearAttn = l,
        QuadraticAttn = q,
    };

    [Fact]
    public void APointLightFallsOffWithItsAttenuation()
    {
        // 1 / (q d^2 + l d + c) at d = 10: 1/100.
        PropLightSample s = _exact.Gather(Point(new Vec3(0, 0, 110)), new Vec3(0, 0, 100), new Vec3(0, 0, 1));

        Assert.Equal((0.01f, 1.0f), (s.Falloff, s.Dot));
    }

    [Fact]
    public void AShortDistanceIsFlooredAtOneUnit()
    {
        // dist = MaxSIMD(dist, Four_Ones):1864).
        PropLightSample s = _exact.Gather(Point(new Vec3(0, 0, 100.5f)), new Vec3(0, 0, 100), new Vec3(0, 0, 1));

        Assert.Equal(1.0f, s.Falloff);
    }

    [Fact]
    public void ALightBehindTheNormalHasNoDot()
    {
        PropLightSample s = _exact.Gather(Point(new Vec3(0, 0, 50)), new Vec3(0, 0, 100), new Vec3(0, 0, 1));

        Assert.Equal(0.0f, s.Dot);
    }

    [Fact]
    public void AWallBlocksTheLight()
    {
        // TestLine: the light is outside the sealed room.
        PropLightSample s = _exact.Gather(Point(new Vec3(1000, 0, 100)), new Vec3(0, 0, 100), new Vec3(1, 0, 0));

        Assert.Equal(0.0f, s.Dot);
    }

    [Fact]
    public void ASpotLightOutsideItsConeIsDark()
    {
        //:1911-1915: dot2 <= stopdot2 returns before any falloff.
        PropLight spot = Point(new Vec3(0, 0, 200)) with
        {
            Type = EmitType.Spotlight,
            Normal = new Vec3(1, 0, 0),
            StopDot = 0.9f,
            StopDot2 = 0.8f,
        };

        PropLightSample s = _exact.Gather(spot, new Vec3(0, 0, 100), new Vec3(0, 0, 1));

        Assert.Equal(default, s);
    }

    [Fact]
    public void ASpotLightInsideItsInnerConeHasFullStrength()
    {
        PropLight spot = Point(new Vec3(0, 0, 200)) with
        {
            Type = EmitType.Spotlight,
            Normal = new Vec3(0, 0, -1),
            StopDot = 0.9f,
            StopDot2 = 0.8f,
        };

        PropLightSample s = _exact.Gather(spot, new Vec3(0, 0, 100), new Vec3(0, 0, 1));

        // falloff 1/10000 * dot2 (1) * mult (1, not in the fringe).
        Assert.Equal(0.0001f, s.Falloff, 9);
    }

    [Fact]
    public void AFixedPointPowerOfTwoSquares()
    {
        // exponent 2 -> 4 * 2 = 8: no fraction, x^2.
        Assert.Equal(0.25f, PropLightSampler.PowFixed(0.5f, 8));
    }

    [Fact]
    public void AFixedPointQuarterPowerTakesTwoSquareRoots()
    {
        // exponent 0.25 -> 1: x^(1/4).
        Assert.Equal(0.5f, PropLightSampler.PowFixed(0.0625f, 1));
    }

    [Fact]
    public void TheHaltonSamplerStartsAtElementTwo()
    {
        // GetElement reads the post-incremented member seed: the
        // first z is halton_2(2) = 0.25, mapped to 2*0.25 - 1 = -0.5.
        DirectionalSampler s = new();

        Assert.Equal(-0.5f, s.NextValue().Z);
    }

    [Fact]
    public void AnExportedIntensityInvertsExactly()
    {
        // wl = fl(x * fl(1/255)) is injective away from the top of a binade.
        const float original = 150.0f;
        float exported = original * (float)(1.0 / 255.0);

        Assert.Equal((original, false), PropLights.Invert(exported));
    }
}
