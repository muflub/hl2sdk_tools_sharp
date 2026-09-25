using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>Bump basis, the solvers and the small numeric kernels of the light path.</summary>
public sealed class BumpBasisTests
{
    [Fact]
    public void TheLocalBasisIsTheHeadersLiterals()
    {
        //, spelled as the header spells them.
        Assert.Equal(new Vec3(0.81649661064147949f, 0.0f, 0.57735025882720947f), BumpBasis.Local[0]);
        Assert.Equal(new Vec3(-0.40824821591377258f, 0.70710676908493042f, 0.57735025882720947f), BumpBasis.Local[1]);
        Assert.Equal(new Vec3(-0.40824821591377258f, -0.70710676908493042f, 0.57735025882720947f), BumpBasis.Local[2]);
    }

    [Fact]
    public void ARightHandedMappingReproducesTheLocalBasis()
    {
        // GetBumpNormals: row1 = normalise(n x s) = +y,
        // row0 = normalise(row1 x n) = +x, row2 = n = +z, and (s x t). n =
        // (+x x +y). +z = 1 is right-handed, so VectorIRotate hands the local
        // basis back unchanged.
        Span<Vec3> b = stackalloc Vec3[3];
        BumpBasis.Build(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(0, 0, 1), b, stockNormalise: false);
        Assert.Equal(BumpBasis.Local[0], b[0]);
        Assert.Equal(BumpBasis.Local[1], b[1]);
        Assert.Equal(BumpBasis.Local[2], b[2]);
    }

    [Fact]
    public void ALeftHandedMappingNegatesTheSecondRow()
    {
        // t = -y: (s x t). n = -1 < 0, row1 becomes -y, so
        // every basis vector's y flips -- which swaps the second and third.
        Span<Vec3> b = stackalloc Vec3[3];
        BumpBasis.Build(new(1, 0, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, 1), b, stockNormalise: false);
        Assert.Equal(BumpBasis.Local[0], b[0]);
        Assert.Equal(BumpBasis.Local[2], b[1]);
        Assert.Equal(BumpBasis.Local[1], b[2]);
    }

    [Fact]
    public void OnlyTheSignOfTheFlatNormalMatters()
    {
        Span<Vec3> a = stackalloc Vec3[3];
        Span<Vec3> b = stackalloc Vec3[3];
        BumpBasis.Build(new(1, 0, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, 1), a, false);
        BumpBasis.Build(new(1, 0, 0), new(0, -1, 0), new(0.3f, 0.1f, 5), new(0, 0, 1), b, false);
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Fact]
    public void TheThirdRowIsThePhongNormalUnnormalised()
    {
        // The third row copies the phong normal through untouched: a z of 2 doubles the
        // basis vectors' z.
        Span<Vec3> b = stackalloc Vec3[3];
        BumpBasis.Build(new(1, 0, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, 2), b, false);
        Assert.Equal(BumpBasis.Local[0].Z * 2, b[0].Z);
    }

    [Fact]
    public void AShortOutputSpanIsRefused() =>
        Assert.Throws<ArgumentException>(() =>
        {
            Span<Vec3> b = stackalloc Vec3[2];
            BumpBasis.Build(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(0, 0, 1), b, false);
        });

    [Fact]
    public void NormaliseTakesTheStockEstimateOnlyWhenAsked()
    {
        Vec3 v = new(3, 4, 12);
        Assert.Equal(v.Normalise().Normalised, BumpBasis.Normalise(v, false));
        Assert.Equal(v.NormaliseLikeStock().Normalised, BumpBasis.Normalise(v, true));
    }
}

public sealed class MathSolverTests
{
    [Fact]
    public void AQuadraticThroughThreePointsIsExact()
    {
        float a = 0, b = 0, c = 0;
        Assert.True(MathSolvers.SolveInverseQuadratic(0, 1, 1, 2, 2, 5, ref a, ref b, ref c));
        Assert.Equal((1f, 0f, 1f), (a, b, c));
    }

    [Fact]
    public void CoincidentXLeavesTheCoefficientsUntouched()
    {
        // returns before any write; SetLightFalloffParams
        // relies on its 0,1,0 seed surviving.
        float a = 7, b = 8, c = 9;
        Assert.False(MathSolvers.SolveInverseQuadratic(1, 1, 1, 2, 3, 4, ref a, ref b, ref c));
        Assert.Equal((7f, 8f, 9f), (a, b, c));
    }

    [Fact]
    public void TheMonotonicSolveFailsOnCoincidentXAndKeepsTheSeed()
    {
        float a = 0, b = 1, c = 0;
        Assert.False(MathSolvers.SolveInverseQuadraticMonotonic(0, 1, 100, 2, 100, 256, ref a, ref b, ref c));
        Assert.Equal((0f, 1f, 0f), (a, b, c));
    }

    [Fact]
    public void TheMonotonicSolveSortsItsPointsByX()
    {
        float a1 = 0, b1 = 1, c1 = 0, a2 = 0, b2 = 1, c2 = 0;
        MathSolvers.SolveInverseQuadraticMonotonic(0, 1, 128, 2, 400, 256, ref a1, ref b1, ref c1);
        MathSolvers.SolveInverseQuadraticMonotonic(400, 256, 0, 1, 128, 2, ref a2, ref b2, ref c2);
        Assert.Equal((a1, b1, c1), (a2, b2, c2));
    }

    [Fact]
    public void TheReciprocalFormIsTheStockBinarysAnswer()
    {
        // StockQuirk.InverseQuadraticReciprocal: d50 96 / d0 256 after the
        // SetLightFalloffParams rescale, as stock's LUMP_WORLDLIGHTS holds it
        // (p4c_texlights light 2): 0.00250157341 and 0.000185510085.
        LightFalloffTests.Solve(96, 256, reciprocal: true, out float a, out float b, out float c);
        Assert.Equal(0.0501881987f, c);
        Assert.Equal(0.00250157341f, b);
        Assert.Equal(0.000185510085f, a);
    }

    [Fact]
    public void TheDivideFormDiffersInTheLastBits()
    {
        LightFalloffTests.Solve(96, 256, reciprocal: false, out float a, out float b, out _);
        Assert.Equal(0.00250157318f, b);
        Assert.Equal(0.000185510071f, a);
    }
}

public sealed class LightFalloffTests
{
    internal static void Solve(float d50, float d0, bool reciprocal, out float a, out float b, out float c)
    {
        DirectLight light = new();
        SourceSharp.MapFormats.Bsp.Structs.BspEntity e = LightTestMap.Entity(
            ("_fifty_percent_distance", d50.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("_zero_percent_distance", d0.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        LightFalloff.Apply(e, light, null, reciprocal);
        (a, b, c) = (light.QuadraticAttn, light.LinearAttn, light.ConstantAttn);
    }

    [Fact]
    public void TheSolvedCurveIsExactlyTwiceTheFullBrightnessAtTheFiftyPercentDistance()
    {
        // rescales so 1/(c + d50 b + d50^2 a) = 0.5.
        Solve(128, 400, false, out float a, out float b, out float c);
        float v50 = c + (128 * (b + (128 * a)));
        Assert.Equal(2.0f, v50, 5);
    }

    [Fact]
    public void AZeroDistanceShorterThanTheFiftyPercentOneIsTwiceIt()
    {
        // Same curve as asking for 2*d50 outright.
        Solve(100, 50, false, out float a1, out float b1, out float c1);
        Solve(100, 200, false, out float a2, out float b2, out float c2);
        Assert.Equal((a2, b2, c2), (a1, b1, c1));
    }

    [Fact]
    public void HardFalloffFadesFromThreeQuartersOfTheWay()
    {
        DirectLight light = new();
        LightFalloff.Apply(
            LightTestMap.Entity(("_fifty_percent_distance", "100"), ("_zero_percent_distance", "300"), ("_hardfalloff", "1")),
            light);
        Assert.Equal(300f, light.EndFadeDistance);
        Assert.Equal((0.75f * 300) + (0.25f * 100), light.StartFadeDistance);
    }

    [Fact]
    public void LiteralCoefficientsBelowEqualEpsilonAreDropped()
    {
        // Small coefficients vanish; constant 1 returns when all do.
        DirectLight light = new() { Intensity = new Vec3(1, 1, 1) };
        LightFalloff.Apply(LightTestMap.Entity(("_quadratic_attn", "0.0005")), light);
        Assert.Equal((1f, 0f, 0f), (light.ConstantAttn, light.LinearAttn, light.QuadraticAttn));
    }

    [Fact]
    public void LiteralCoefficientsScaleIntensityToUnitAtOneHundredUnits()
    {
        // intensity *= c + 100 l + 100^2 q.
        DirectLight light = new() { Intensity = new Vec3(1, 1, 1) };
        LightFalloff.Apply(LightTestMap.Entity(("_linear_attn", "1")), light);
        Assert.Equal(new Vec3(100, 100, 100), light.Intensity);
    }

    [Fact]
    public void DistanceIsReadAsTheRadius()
    {
        DirectLight light = new();
        LightFalloff.Apply(LightTestMap.Entity(("_distance", "512")), light);
        Assert.Equal(512f, light.Radius);
    }

    [Fact]
    public void AnExtremeFalloffIsCappedWhereTheCurveTurnsUnderStocksSlopeTest()
    {
        // d50 10 / d0 20 with stock's 2a + b test (MonotonicDerivativeAtOne):
        // the blend stops at 0.45 with a = 0.696 and b = -1.165 (before the
        // rescale, which scales both alike), so the quadratic turns at 0.837
        // units, and the far cap fades there.
        DirectLight light = new();
        LightFalloff.Apply(
            LightTestMap.Entity(("_fifty_percent_distance", "10"), ("_zero_percent_distance", "20")),
            light, null, reciprocalSolve: false, derivativeAtOne: true);
        Assert.Equal(0.837f, light.CapDist, 3);
        Assert.Equal(light.CapDist, light.StartFadeDistance);
        Assert.Equal((float)(10.0 * light.CapDist), light.EndFadeDistance);
    }

    [Fact]
    public void TheStartPointSlopeTestLeavesTheSameCurveUncapped()
    {
        // Correct tests the slope at x1 = 0, i.e. b: the blend runs until b is
        // non-negative, and a curve rising from its start has no turning point
        // ahead of it to cap.
        DirectLight light = new();
        LightFalloff.Apply(
            LightTestMap.Entity(("_fifty_percent_distance", "10"), ("_zero_percent_distance", "20")),
            light, null, reciprocalSolve: false, derivativeAtOne: false);
        Assert.Equal(1.0e22f, light.CapDist);
        Assert.True(light.LinearAttn >= 0);
    }
}

public sealed class LightNormalTests
{
    [Fact]
    public void ZeroAngleFallsBackToTheAnglesYaw()
    {
        Vec3 n = LightNormals.FromProps(new Vec3(0, 180, 0), 0, 0);
        Assert.Equal(-1f, n.X);
    }

    [Fact]
    public void ZeroPitchFallsBackToTheAnglesPitch()
    {
        Vec3 n = LightNormals.FromProps(new Vec3(-90, 0, 0), 0, 0);
        Assert.Equal(-1f, n.Z);
    }

    [Fact]
    public void AngleUpIsOverwrittenByThePitch()
    {
        // The base vector starts at (0,0,1); the pitch then overwrites z.
        Vec3 n = LightNormals.FromProps(Vec3.Zero, LightNormals.AngleUp, 0);
        Assert.Equal(new Vec3(0, 0, 0), n);
    }

    [Fact]
    public void TheCorrectCosineOfARightAngleIsTheDoublesDistanceFromPi()
    {
        Assert.Equal(6.123234e-17f, (float)LightNormals.Cosine(Math.PI / 2, crtCosine: false));
    }

    [Fact]
    public void TheStockCosineOfARightAngleIsSinPi()
    {
        // StockQuirk.CrtCosineAtRightAngle, observed on p4c_texlights lights 4-5.
        Assert.Equal(1.2246469e-16f, (float)LightNormals.Cosine(Math.PI / 2, crtCosine: true));
        Assert.Equal(1.2246469e-16f, (float)LightNormals.Cosine(-Math.PI / 2, crtCosine: true));
    }

    [Fact]
    public void TheStockCosineIsOrdinaryAwayFromTheRightAngle()
    {
        Assert.Equal(Math.Cos(1.0), LightNormals.Cosine(1.0, crtCosine: true));
    }
}

public sealed class HaltonAndSamplerTests
{
    [Fact]
    public void TheFirstValueIsElementTwoNotOne()
    {
        // `GetElement(seed++)` reads the member AFTER the
        // increment, so base 2 starts 0.25, not 0.5.
        HaltonSequence h = new(2);
        Assert.Equal(0.25f, h.NextValue());
        Assert.Equal(0.75f, h.NextValue());
        Assert.Equal(0.125f, h.NextValue());
    }

    [Fact]
    public void BaseThreeStartsAtTwoThirds()
    {
        HaltonSequence h = new(3);
        Assert.Equal((float)(1.0 / 3) * 2, h.NextValue());
    }

    [Fact]
    public void ABaseBelowTwoIsRefused() => Assert.Throws<ArgumentOutOfRangeException>(() => new HaltonSequence(1));

    [Fact]
    public void TheSamplersDirectionsAreUnitVectors()
    {
        DirectionalSampler s = new();
        for (int i = 0; i < 200; i++)
        {
            Vec3 v = s.NextValue();
            Assert.InRange(v.Length(), 0.9999f, 1.0001f);
        }
    }

    [Fact]
    public void TheFirstDirectionUsesZFromTheBaseTwoValue()
    {
        // z = 2 * 0.25 - 1 = -0.5.
        DirectionalSampler s = new();
        Assert.Equal(-0.5f, s.NextValue().Z);
    }

    [Fact]
    public void TwoSamplersProduceTheSameSequence()
    {
        DirectionalSampler a = new();
        DirectionalSampler b = new();
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(a.NextValue(), b.NextValue());
        }
    }
}

public sealed class LightingValueTests
{
    [Fact]
    public void AddLightIsAMultiplyAddAndAddsTheSunUnscaled()
    {
        LightingValue v = new(new Vec3(1, 2, 3), 0.5f);
        v.AddLight(2, new Vec3(10, 20, 30), 7);
        Assert.Equal(new Vec3(21, 42, 63), v.Lighting);
        Assert.Equal(7.5f, v.DirectSunAmount);
    }

    [Fact]
    public void IntensityIsTheChannelSum() => Assert.Equal(6f, new LightingValue(new Vec3(1, 2, 3), 0).Intensity());

    [Fact]
    public void AddWeightedScalesBothParts()
    {
        LightingValue v = default;
        v.AddWeighted(new LightingValue(new Vec3(4, 4, 4), 8), 0.25f);
        Assert.Equal(new Vec3(1, 1, 1), v.Lighting);
        Assert.Equal(2f, v.DirectSunAmount);
    }

    [Fact]
    public void ScaleScalesBothParts()
    {
        LightingValue v = new(new Vec3(2, 2, 2), 2);
        v.Scale(0.5f);
        Assert.Equal((new Vec3(1, 1, 1), 1f), (v.Lighting, v.DirectSunAmount));
    }

    [Fact]
    public void AddLightOfAValueAddsBothParts()
    {
        LightingValue v = new(new Vec3(1, 1, 1), 1);
        v.AddLight(new LightingValue(new Vec3(1, 2, 3), 4));
        Assert.Equal((new Vec3(2, 3, 4), 5f), (v.Lighting, v.DirectSunAmount));
    }
}

public sealed class SsePrimitiveTests
{
    [Fact]
    public void MaxPsReturnsTheSecondOperandOnNaN()
    {
        // maxps: a NaN first operand yields the second (why a 0/0 sky-ambient
        // dot is clamped to 0 by MaxSIMD(dot, Four_Zeros)).
        Assert.Equal(0f, DirectLightGatherer.MaxPs(float.NaN, 0f));
        Assert.True(float.IsNaN(DirectLightGatherer.MaxPs(0f, float.NaN)));
    }

    [Fact]
    public void MinPsReturnsTheSecondOperandOnNaN()
    {
        Assert.Equal(1f, DirectLightGatherer.MinPs(float.NaN, 1f));
        Assert.True(float.IsNaN(DirectLightGatherer.MinPs(1f, float.NaN)));
    }

    [Fact]
    public void MaxPsPicksTheLarger() => Assert.Equal(3f, DirectLightGatherer.MaxPs(3f, 2f));

    [Fact]
    public void RemapValOfAnEmptyRangeIsAStep()
    {
        Assert.Equal(9f, MacroTextures.RemapVal(5, 5, 5, 1, 9));
        Assert.Equal(1f, MacroTextures.RemapVal(4, 5, 5, 1, 9));
    }

    [Fact]
    public void RemapValIsLinear() => Assert.Equal(5f, MacroTextures.RemapVal(50, 0, 100, 0, 10));
}
