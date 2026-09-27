//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Numerics;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Numerics;

/// <summary>
/// NaN, infinities, signed zeros, subnormals, the rules of pow and atan2, and
/// the arguments each public function hands to the exact tier.
/// </summary>
/// <remarks>
/// For the special values themselves the platform functions are the
/// reference: .NET documents IEEE 754 / C Annex F results for them on every
/// platform, and they are exact (a zero, an infinity, one, NaN), so there is
/// no last bit to disagree about. Every other result is checked against the
/// exact tier.
/// </remarks>
public class DetMathSpecialValueTests
{
    private static readonly float[] Specials =
    [
        0f, -0f, float.PositiveInfinity, float.NegativeInfinity, float.NaN, 1f, -1f, 0.5f, -0.5f, 2f, -2f, -3f, 3f,
        float.Epsilon, -float.Epsilon, float.MaxValue, 1e30f, -1e30f, 16777217f, 2.5f, -2.5f, 1.5f,
    ];

    [Fact]
    public void PowFollowsTheIeeeTableOverEveryPairOfSpecials()
    {
        List<string> wrong = [];
        foreach (float x in Specials)
        {
            foreach (float y in Specials)
            {
                float ours = DetMathF.Pow(x, y);
                float expected = IsSpecial(x) || IsSpecial(y) || Math.Abs(x) == 1
                    ? MathF.Pow(x, y)
                    : ExactPow(x, y);
                Check(wrong, $"Pow({x:R}, {y:R})", ours, expected);
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Atan2FollowsTheIeeeTableOverEveryPairOfSpecials()
    {
        List<string> wrong = [];
        foreach (float y in Specials)
        {
            foreach (float x in Specials)
            {
                float ours = DetMathF.Atan2(y, x);
                float expected = IsSpecial(x) || IsSpecial(y)
                    ? MathF.Atan2(y, x)
                    : (float)ExactMath.Atan2(y, x, RoundingTarget.Single);
                Check(wrong, $"Atan2({y:R}, {x:R})", ours, expected);
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void UnaryFunctionsFollowTheIeeeTable()
    {
        List<string> wrong = [];
        foreach (float x in Specials)
        {
            if (IsSpecial(x))
            {
                Check(wrong, $"Sin({x:R})", DetMathF.Sin(x), MathF.Sin(x));
                Check(wrong, $"Cos({x:R})", DetMathF.Cos(x), MathF.Cos(x));
                Check(wrong, $"Tan({x:R})", DetMathF.Tan(x), MathF.Tan(x));
                Check(wrong, $"SinCos({x:R}).Sin", DetMathF.SinCos(x).Sin, MathF.Sin(x));
                Check(wrong, $"SinCos({x:R}).Cos", DetMathF.SinCos(x).Cos, MathF.Cos(x));
                Check(wrong, $"SinToSingle({x:R})", DetMath.SinToSingle(x), MathF.Sin(x));
                Check(wrong, $"CosToSingle({x:R})", DetMath.CosToSingle(x), MathF.Cos(x));
            }

            if (IsSpecial(x) || Math.Abs(x) >= 1)
            {
                Check(wrong, $"Asin({x:R})", DetMathF.Asin(x), Math.Abs(x) == 1 ? (float)ExactMath.Asin(x, RoundingTarget.Single) : MathF.Asin(x));
                Check(wrong, $"Acos({x:R})", DetMathF.Acos(x), x == -1 ? (float)ExactMath.Acos(x, RoundingTarget.Single) : MathF.Acos(x));
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void DoubleFunctionsFollowTheIeeeTable()
    {
        double[] specials = [0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 1.0, -1.0];
        foreach (double x in specials)
        {
            Assert.Equal(Bits(Math.Sin(x)), Bits(DetMath.Sin(x)), NaNAware);
            Assert.Equal(Bits(Math.Cos(x)), Bits(DetMath.Cos(x)), NaNAware);
            Assert.Equal(Bits(Math.Log(x)), Bits(DetMath.Log(x)), NaNAware);
            foreach (double y in specials)
            {
                Assert.Equal(Bits(Math.Pow(x, y)), Bits(DetMath.Pow(x, y)), NaNAware);
                Assert.Equal(Bits((float)Math.Pow(x, y)), Bits((double)DetMath.PowToSingle(x, y)), NaNAware);
            }
        }
    }

    [Fact]
    public void ANaNArgumentIsReturnedAsItIs()
    {
        // A payload, and a sign, that a freshly made NaN would not have.
        float nan = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC01234));
        double dnan = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000001234));
        int bits = BitConverter.SingleToInt32Bits(nan);
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(DetMathF.Sin(nan)));
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(DetMathF.Cos(nan)));
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(DetMathF.Tan(nan)));
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(DetMathF.Asin(nan)));
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(DetMathF.Acos(nan)));
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(DetMathF.Atan2(nan, float.NaN)));
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(DetMathF.Atan2(1f, nan)));
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(DetMathF.Pow(nan, 2f)));
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(DetMathF.Pow(2f, nan)));
        Assert.Equal(1f, DetMathF.Pow(nan, 0f));
        Assert.Equal(1f, DetMathF.Pow(1f, nan));
        Assert.Equal(Bits(dnan), Bits(DetMath.Sin(dnan)));
        Assert.Equal(Bits(dnan), Bits(DetMath.Cos(dnan)));
        Assert.Equal(Bits(dnan), Bits(DetMath.Log(dnan)));
        Assert.Equal(Bits(dnan), Bits(DetMath.Pow(dnan, 3.0)));
    }

    [Fact]
    public void DomainErrorsAreNaN()
    {
        Assert.True(float.IsNaN(DetMathF.Asin(1.0000001f)));
        Assert.True(float.IsNaN(DetMathF.Acos(-1.0000001f)));
        Assert.True(float.IsNaN(DetMathF.Pow(-2f, 0.5f)));
        Assert.True(double.IsNaN(DetMath.Pow(-2.0, 0.5)));
        Assert.True(float.IsNaN(DetMath.PowToSingle(-2.0, 0.5)));
        Assert.True(double.IsNaN(DetMath.Log(-1e-300)));
        Assert.True(float.IsNaN(DetMathF.Sin(float.PositiveInfinity)));
    }

    [Fact]
    public void SignedZerosSurvive()
    {
        Assert.True(float.IsNegative(DetMathF.Sin(-0f)));
        Assert.True(float.IsNegative(DetMathF.Tan(-0f)));
        Assert.True(float.IsNegative(DetMathF.Asin(-0f)));
        Assert.True(float.IsNegative(DetMathF.Atan2(-0f, 1f)));
        Assert.True(float.IsNegative(DetMathF.Atan2(-1f, float.PositiveInfinity)));
        Assert.True(float.IsNegative(DetMathF.Pow(-0f, 3f)));
        Assert.False(float.IsNegative(DetMathF.Pow(-0f, 2f)));
        Assert.True(double.IsNegative(DetMath.Sin(-0.0)));
        Assert.True(float.IsNegative(DetMath.SinToSingle(-0.0)));
        Assert.Equal(0f, DetMathF.Acos(1f));
        Assert.False(float.IsNegative(DetMathF.Acos(1f)));
    }

    [Theory]
    [InlineData(-2f, 3f)]
    [InlineData(-2f, -3f)]
    [InlineData(-0.5f, 11f)]
    [InlineData(-3f, 2f)]
    [InlineData(-1.1f, 1e30f)]
    [InlineData(-0.9f, 1e30f)]
    [InlineData(-4097f, 2f)]
    [InlineData(-3.3f, 5f)]
    public void ANegativeBaseTakesTheSignOfAnOddIntegerPower(float x, float y)
    {
        float magnitude = DetMathF.Pow(-x, y);
        bool odd = MathF.Floor(y) == y && Math.Abs(y) < 16777216f && ((long)y & 1) != 0;
        Assert.Equal(odd ? -magnitude : magnitude, DetMathF.Pow(x, y));
        Assert.Equal(odd ? -(double)DetMath.PowToSingle(-x, y) : DetMath.PowToSingle(-x, y), DetMath.PowToSingle(x, y));
    }

    [Fact]
    public void PowSaturatesPastTheFloatRange()
    {
        Assert.Equal(float.PositiveInfinity, DetMathF.Pow(10f, 39f));
        Assert.Equal(float.PositiveInfinity, DetMathF.Pow(10f, 1000f));
        Assert.Equal(0f, DetMathF.Pow(10f, -46f));
        Assert.Equal(0f, DetMathF.Pow(10f, -1000f));
        Assert.Equal(float.NegativeInfinity, DetMathF.Pow(-10f, 1001f));
        Assert.Equal(float.PositiveInfinity, DetMath.PowToSingle(10.0, 39.0));
        Assert.Equal(0f, DetMath.PowToSingle(10.0, -46.0));
        Assert.Equal(double.PositiveInfinity, DetMath.Pow(10.0, 400.0));
        Assert.Equal(0.0, DetMath.Pow(10.0, -400.0));
        Assert.Equal(double.PositiveInfinity, DetMath.Pow(1.0000001, 1e300));

        // Just inside: 2^127.99 and 2^-149 are finite and nonzero.
        Assert.True(float.IsFinite(DetMathF.Pow(2f, 127.99f)));
        Assert.Equal(float.Epsilon, DetMathF.Pow(2f, -149f));
        Assert.Equal(0f, DetMathF.Pow(2f, -150f));
    }

    [Theory]
    [InlineData(33554432f)]
    [InlineData(-1e10f)]
    [InlineData(3.4e38f)]
    [InlineData(1e20f)]
    public void ArgumentsPastTheReductionLimitUseTheExactTier(float x)
    {
        Assert.False(FastKernels.ReduceHalfPi(x, out _, out _, out _));
        Assert.Equal((float)ExactMath.Sin(x, RoundingTarget.Single), DetMathF.Sin(x));
        Assert.Equal((float)ExactMath.Cos(x, RoundingTarget.Single), DetMathF.Cos(x));
        Assert.Equal((float)ExactMath.Tan(x, RoundingTarget.Single), DetMathF.Tan(x));
        Assert.Equal((DetMathF.Sin(x), DetMathF.Cos(x)), DetMathF.SinCos(x));
        Assert.Equal((float)ExactMath.Sin(x, RoundingTarget.DoubleThenSingle), DetMath.SinToSingle(x));
        Assert.Equal((float)ExactMath.Cos(x, RoundingTarget.DoubleThenSingle), DetMath.CosToSingle(x));
    }

    [Fact]
    public void AReducedArgumentTooSmallForTheFastTierUsesTheExactTier()
    {
        // The double nearest pi/2 reduces to 6.1e-17, under the fast tier's
        // 2^-40 floor; cos there is that remainder.
        Assert.False(FastKernels.ReduceHalfPi(Math.PI / 2, out _, out _, out _));
        Assert.Equal(6.123234e-17f, DetMath.CosToSingle(Math.PI / 2));
        Assert.Equal(1f, DetMath.SinToSingle(Math.PI / 2));
        Assert.Equal(6.123233995736766e-17, DetMath.Cos(Math.PI / 2));
    }

    [Fact]
    public void SinCosIsSinAndCos()
    {
        Random random = new(7);
        for (int i = 0; i < 20000; i++)
        {
            float x = (float)((random.NextDouble() - 0.5) * 2000);
            Assert.Equal((DetMathF.Sin(x), DetMathF.Cos(x)), DetMathF.SinCos(x));
        }
    }

    [Fact]
    public void SubnormalArgumentsAndResults()
    {
        Assert.Equal(float.Epsilon, DetMathF.Sin(float.Epsilon));
        Assert.Equal(float.Epsilon, DetMathF.Tan(float.Epsilon));
        Assert.Equal(float.Epsilon, DetMathF.Asin(float.Epsilon));
        Assert.Equal(1f, DetMathF.Cos(float.Epsilon));
        Assert.Equal(float.Epsilon, DetMathF.Atan2(float.Epsilon, 1f));
        Assert.Equal(0f, DetMathF.Atan2(float.Epsilon, 3f));
        Assert.Equal(double.Epsilon, DetMath.Sin(double.Epsilon));
        Assert.Equal(0f, DetMath.SinToSingle(double.Epsilon));
        Assert.Equal(-744.44007192138122, DetMath.Log(double.Epsilon));
        Assert.Equal(1.1754942e-38f, DetMathF.Pow(1.1754942e-38f, 1f));
    }

    [Theory]
    [InlineData(1.5707964f)]
    [InlineData(-1.5707964f)]
    [InlineData(4.712389f)]
    [InlineData(1.5707963f)]
    public void TangentNearAPoleIsCorrectlyRounded(float x)
    {
        Assert.Equal((float)ExactMath.Tan(x, RoundingTarget.Single), DetMathF.Tan(x));
    }

    private static bool IsSpecial(float x) => x == 0 || !float.IsFinite(x);

    private static float ExactPow(float x, float y)
    {
        if (x < 0 && MathF.Floor(y) != y)
        {
            return float.NaN;
        }

        float magnitude = (float)ExactMath.Pow(Math.Abs(x), y, RoundingTarget.Single);
        bool odd = x < 0 && Math.Abs(y) < 16777216f && ((long)y & 1) != 0;
        return odd ? -magnitude : magnitude;
    }

    private static void Check(List<string> wrong, FormattableString what, float actual, float expected)
    {
        bool same = float.IsNaN(expected)
            ? float.IsNaN(actual)
            : BitConverter.SingleToInt32Bits(actual) == BitConverter.SingleToInt32Bits(expected);
        if (!same)
        {
            wrong.Add(string.Create(CultureInfo.InvariantCulture, $"{what.ToString(CultureInfo.InvariantCulture)} = {actual:R}, expected {expected:R}"));
        }
    }

    private static ulong Bits(double d) => (ulong)BitConverter.DoubleToInt64Bits(d);

    // NaNs compare by NaN-ness: the platform's own NaN results differ in sign
    // and payload between runtimes, which is not what these facts are about.
    private static readonly IEqualityComparer<ulong> NaNAware = EqualityComparer<ulong>.Create(
        (a, b) => double.IsNaN(BitConverter.Int64BitsToDouble((long)a))
            ? double.IsNaN(BitConverter.Int64BitsToDouble((long)b))
            : a == b,
        a => 0);
}
