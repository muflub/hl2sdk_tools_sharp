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
/// <para>
/// Nothing here asks the platform's math library what the answer is. A fact
/// that compared against <see cref="Math"/> or <see cref="MathF"/> would pass
/// or fail with the C library underneath it, which is the very dependence
/// <see cref="DetMath"/> exists to remove; <c>Math.Sin(1.0)</c> in particular
/// is not exact and is not guaranteed to be correctly rounded anywhere.
/// </para>
/// <para>
/// So every expected value is written out: the IEEE 754 / C Annex F tables
/// for the special cases (<see cref="AnnexFPow"/>, <see cref="AnnexFUnary"/>),
/// whose results are exact -- a zero, an infinity, one, NaN -- or a
/// correctly rounded multiple of pi given as its bit pattern; hard-coded bit
/// patterns for the few finite results that are not exact (sin 1 and cos 1,
/// checked against mpmath at 300 bits); and the exact tier for everything
/// else. atan2's special results are multiples of pi/4 and platforms round
/// them differently (Apple's atan2f(+0, -1) is the float just below pi), which
/// is why its table was already written out here.
/// </para>
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
                    ? (float)AnnexFPow(x, y)
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
                    ? IeeeAtan2(y, x)
                    : (float)ExactMath.Atan2(y, x, RoundingTarget.Single);
                Check(wrong, $"Atan2({y:R}, {x:R})", ours, expected);
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    // pi, pi/2, pi/4 and 3pi/4 rounded to the nearest float.
    private static readonly float Pi = BitConverter.Int32BitsToSingle(0x40490FDB);
    private static readonly float HalfPi = BitConverter.Int32BitsToSingle(0x3FC90FDB);
    private static readonly float QuarterPi = BitConverter.Int32BitsToSingle(0x3F490FDB);
    private static readonly float ThreeQuarterPi = BitConverter.Int32BitsToSingle(0x4016CBE4);

    /// <summary>
    /// atan2 where either argument is a zero, an infinity or NaN: IEEE 754's
    /// table (C Annex F.10.1.4), with each multiple of pi correctly rounded.
    /// </summary>
    private static float IeeeAtan2(float y, float x)
    {
        if (float.IsNaN(y) || float.IsNaN(x))
        {
            return float.NaN;
        }

        float sign = float.IsNegative(y) ? -1f : 1f;
        if (y == 0)
        {
            // atan2(+-0, x): +-0 for x > 0 or +0; +-pi for x < 0 or -0.
            return float.IsNegative(x) ? sign * Pi : sign * 0f;
        }

        if (float.IsInfinity(y))
        {
            return float.IsPositiveInfinity(x) ? sign * QuarterPi
                : float.IsNegativeInfinity(x) ? sign * ThreeQuarterPi
                : sign * HalfPi;
        }

        // y finite and non-zero, so x is a zero or an infinity.
        return x == 0 ? sign * HalfPi
            : float.IsPositiveInfinity(x) ? sign * 0f
            : sign * Pi;
    }

    [Fact]
    public void TheAtan2ConstantsAreTheCorrectlyRoundedMultiplesOfPi()
    {
        Assert.Equal(Pi, (float)ExactMath.Atan2(1e-30f, -1f, RoundingTarget.Single));
        Assert.Equal(HalfPi, (float)ExactMath.Atan2(1f, 1e-30f, RoundingTarget.Single));
        Assert.Equal(QuarterPi, (float)ExactMath.Atan2(1f, 1f, RoundingTarget.Single));
        Assert.Equal(ThreeQuarterPi, (float)ExactMath.Atan2(1f, -1f, RoundingTarget.Single));
    }

    [Fact]
    public void UnaryFunctionsFollowTheIeeeTable()
    {
        List<string> wrong = [];
        foreach (float x in Specials)
        {
            if (IsSpecial(x))
            {
                Check(wrong, $"Sin({x:R})", DetMathF.Sin(x), (float)AnnexFUnary("sin", x));
                Check(wrong, $"Cos({x:R})", DetMathF.Cos(x), (float)AnnexFUnary("cos", x));
                Check(wrong, $"Tan({x:R})", DetMathF.Tan(x), (float)AnnexFUnary("tan", x));
                Check(wrong, $"SinCos({x:R}).Sin", DetMathF.SinCos(x).Sin, (float)AnnexFUnary("sin", x));
                Check(wrong, $"SinCos({x:R}).Cos", DetMathF.SinCos(x).Cos, (float)AnnexFUnary("cos", x));
                Check(wrong, $"SinToSingle({x:R})", DetMath.SinToSingle(x), (float)AnnexFUnary("sin", x));
                Check(wrong, $"CosToSingle({x:R})", DetMath.CosToSingle(x), (float)AnnexFUnary("cos", x));
            }

            if (IsSpecial(x) || Math.Abs(x) >= 1)
            {
                Check(wrong, $"Asin({x:R})", DetMathF.Asin(x), AnnexFAsin(x));
                Check(wrong, $"Acos({x:R})", DetMathF.Acos(x), AnnexFAcos(x));
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>sin 1 and cos 1, the double nearest each: the finite, inexact entries of the double table.</summary>
    private const ulong SinOne = 0x3FEAED548F090CEE;
    private const ulong CosOne = 0x3FE14A280FB5068C;

    [Fact]
    public void DoubleFunctionsFollowTheIeeeTable()
    {
        double[] specials = [0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 1.0, -1.0];
        foreach (double x in specials)
        {
            Assert.Equal(Bits(Math.Abs(x) == 1 ? Math.CopySign(D(SinOne), x) : AnnexFUnary("sin", x)), Bits(DetMath.Sin(x)), NaNAware);
            Assert.Equal(Bits(Math.Abs(x) == 1 ? D(CosOne) : AnnexFUnary("cos", x)), Bits(DetMath.Cos(x)), NaNAware);
            Assert.Equal(Bits(AnnexFUnary("log", x)), Bits(DetMath.Log(x)), NaNAware);
            foreach (double y in specials)
            {
                Assert.Equal(Bits(AnnexFPow(x, y)), Bits(DetMath.Pow(x, y)), NaNAware);
                Assert.Equal(Bits((float)AnnexFPow(x, y)), Bits((double)DetMath.PowToSingle(x, y)), NaNAware);
            }
        }
    }

    /// <summary>
    /// The written-out tables agree with what they are meant to encode, so a
    /// slip in one cannot make a wrong result look right. The finite, inexact
    /// constants are checked against the exact tier, which computes them
    /// independently of the fast tier and of any C library.
    /// </summary>
    [Fact]
    public void TheWrittenOutConstantsAreTheCorrectlyRoundedValues()
    {
        Assert.Equal(SinOne, Bits(ExactMath.Sin(1.0, RoundingTarget.Double)));
        Assert.Equal(CosOne, Bits(ExactMath.Cos(1.0, RoundingTarget.Double)));
        Assert.Equal(HalfPi, (float)ExactMath.Asin(1f, RoundingTarget.Single));
        Assert.Equal(Pi, (float)ExactMath.Acos(-1f, RoundingTarget.Single));

        // A few entries of the pow table that are easy to get backwards.
        Assert.Equal(double.PositiveInfinity, AnnexFPow(-0.0, -2.0));
        Assert.Equal(double.NegativeInfinity, AnnexFPow(-0.0, -1.0));
        Assert.Equal(-0.0, AnnexFPow(double.NegativeInfinity, -1.0));
        Assert.True(double.IsNegative(AnnexFPow(double.NegativeInfinity, -1.0)));
        Assert.Equal(1.0, AnnexFPow(-1.0, double.NegativeInfinity));
        Assert.Equal(1.0, AnnexFPow(double.NaN, 0.0));
        Assert.Equal(1.0, AnnexFPow(1.0, double.NaN));
        Assert.True(double.IsNaN(AnnexFPow(-1.0, 0.5)));
        Assert.Equal(-1.0, AnnexFPow(-1.0, 3.0));
    }

    private static double D(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    /// <summary>
    /// <c>pow(x, y)</c> where x or y is a zero, an infinity or NaN, or
    /// <c>|x|</c> is 1: C Annex F's table (F.10.4.4), every result exact.
    /// </summary>
    private static double AnnexFPow(double x, double y)
    {
        if (y == 0 || x == 1)
        {
            return 1;
        }

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return double.NaN;
        }

        bool integer = Math.Floor(y) == y;
        bool oddInteger = integer && Math.Abs(y) < 9007199254740992.0 && Math.Abs(y % 2) == 1;

        if (double.IsInfinity(y))
        {
            double ax = Math.Abs(x);
            return ax == 1 ? 1
                : (ax < 1) == (y < 0) ? double.PositiveInfinity
                : 0.0;
        }

        if (x == 0)
        {
            return y < 0
                ? (oddInteger ? Math.CopySign(double.PositiveInfinity, x) : double.PositiveInfinity)
                : (oddInteger ? x : 0.0);
        }

        if (double.IsNegativeInfinity(x))
        {
            return y < 0
                ? (oddInteger ? -0.0 : 0.0)
                : (oddInteger ? double.NegativeInfinity : double.PositiveInfinity);
        }

        if (double.IsPositiveInfinity(x))
        {
            return y < 0 ? 0.0 : double.PositiveInfinity;
        }

        if (x == -1)
        {
            return !integer ? double.NaN : oddInteger ? -1.0 : 1.0;
        }

        throw new ArgumentException($"pow({x}, {y}) is not in the special table");
    }

    /// <summary>
    /// sin, cos, tan and log at a zero, an infinity or NaN, and log at 1 and
    /// at a negative: C Annex F's tables, every result exact.
    /// </summary>
    private static double AnnexFUnary(string function, double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        return function switch
        {
            "sin" or "tan" when x == 0 => x,
            "cos" when x == 0 => 1.0,
            "sin" or "cos" or "tan" when double.IsInfinity(x) => double.NaN,
            "log" when x == 0 => double.NegativeInfinity,
            "log" when x < 0 => double.NaN,
            "log" when x == 1 => 0.0,
            "log" when double.IsPositiveInfinity(x) => double.PositiveInfinity,
            _ => throw new ArgumentException($"{function}({x}) is not in the special table"),
        };
    }

    /// <summary>asin where <c>|x| &gt;= 1</c> or x is a zero, an infinity or NaN.</summary>
    private static float AnnexFAsin(float x) =>
        float.IsNaN(x) || Math.Abs(x) > 1 ? float.NaN
        : x == 0 ? x
        : x == 1 ? HalfPi
        : -HalfPi;

    /// <summary>acos where <c>|x| &gt;= 1</c> or x is a zero, an infinity or NaN.</summary>
    private static float AnnexFAcos(float x) =>
        float.IsNaN(x) || Math.Abs(x) > 1 ? float.NaN
        : x == 0 ? HalfPi
        : x == 1 ? 0f
        : Pi;

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
