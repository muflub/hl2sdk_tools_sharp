//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;

using SourceSharp.MapFormats.Numerics;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Numerics;

/// <summary>
/// The exact tier's building blocks: interval arithmetic, the stored
/// constants, rounding, and the exact powers that the Ziv loop could never
/// decide on its own.
/// </summary>
public class ExactMathTests
{
    // ------------------------------------------------------------------
    // FixedInterval
    // ------------------------------------------------------------------

    [Fact]
    public void BigIntegerRightShiftIsAFloorForNegativeValues()
    {
        // FloorShift relies on this; CeilShift is built from it.
        Assert.Equal(new BigInteger(-3), FixedInterval.FloorShift(-5, 1));
        Assert.Equal(new BigInteger(-2), FixedInterval.CeilShift(-5, 1));
        Assert.Equal(new BigInteger(2), FixedInterval.FloorShift(5, 1));
        Assert.Equal(new BigInteger(3), FixedInterval.CeilShift(5, 1));
        Assert.Equal(new BigInteger(-1), FixedInterval.FloorShift(-1, 40));
        Assert.Equal(BigInteger.Zero, FixedInterval.CeilShift(-1, 40));
    }

    [Theory]
    [InlineData(7, 2, 3, 4)]
    [InlineData(-7, 2, -4, -3)]
    [InlineData(6, 3, 2, 2)]
    [InlineData(-6, 3, -2, -2)]
    public void FloorAndCeilingDivisionRoundOutward(int a, int b, int floor, int ceil)
    {
        Assert.Equal(new BigInteger(floor), FixedInterval.FloorDiv(a, b));
        Assert.Equal(new BigInteger(ceil), FixedInterval.CeilDiv(a, b));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(15, 3, 4)]
    [InlineData(16, 4, 4)]
    [InlineData(17, 4, 5)]
    public void IntegerSquareRootsRoundBothWays(long n, long floor, long ceil)
    {
        Assert.Equal(new BigInteger(floor), FixedInterval.FloorSqrt(n));
        Assert.Equal(new BigInteger(ceil), FixedInterval.CeilSqrt(n));
    }

    [Fact]
    public void ALargeSquareRootIsExact()
    {
        BigInteger root = (BigInteger.One << 300) + 12345;
        Assert.Equal(root, FixedInterval.FloorSqrt(root * root));
        Assert.Equal(root, FixedInterval.FloorSqrt((root * root) + (2 * root)));
        Assert.Equal(root + 1, FixedInterval.CeilSqrt((root * root) + 1));
    }

    [Fact]
    public void ProductsOfMixedSignIntervalsTakeTheExtremes()
    {
        const int p = 4;
        FixedInterval a = new(-2 << p, 3 << p);
        FixedInterval b = new(-5 << p, 1 << p);
        FixedInterval c = a.Mul(b, p);
        Assert.Equal(new BigInteger(-15 << p), c.Lo);
        Assert.Equal(new BigInteger(10 << p), c.Hi);

        FixedInterval d = new FixedInterval(1 << p, 2 << p).Mul(new FixedInterval(3 << p, 4 << p), p);
        Assert.Equal(new BigInteger(3 << p), d.Lo);
        Assert.Equal(new BigInteger(8 << p), d.Hi);
    }

    [Fact]
    public void ASquareOfAnIntervalThroughZeroStartsAtZero()
    {
        const int p = 4;
        FixedInterval s = new FixedInterval(-3 << p, 2 << p).Square(p);
        Assert.Equal(BigInteger.Zero, s.Lo);
        Assert.Equal(new BigInteger(9 << p), s.Hi);

        FixedInterval t = new FixedInterval(-3 << p, -2 << p).Square(p);
        Assert.Equal(new BigInteger(4 << p), t.Lo);
        Assert.Equal(new BigInteger(9 << p), t.Hi);
    }

    [Fact]
    public void DivisionByANegativeIntervalFlipsTheEnds()
    {
        const int p = 8;
        FixedInterval q = new FixedInterval(1 << p, 2 << p).Div(new FixedInterval(-4 << p, -2 << p), p);
        Assert.Equal(new BigInteger(-1 << p), q.Lo);
        Assert.Equal(new BigInteger(-(1 << p) / 4), q.Hi);
    }

    [Fact]
    public void IntervalBasicsHoldTheirEnds()
    {
        FixedInterval a = new(-3, 5);
        Assert.True(a.ContainsZero);
        Assert.False(new FixedInterval(1, 2).ContainsZero);
        Assert.Equal(new BigInteger(5), a.MaxAbs);
        Assert.Equal((new BigInteger(-5), new BigInteger(3)), ((-a).Lo, (-a).Hi));
        Assert.Equal((new BigInteger(-10), new BigInteger(6)), (a.Times(-2).Lo, a.Times(-2).Hi));
        Assert.Equal((new BigInteger(-2), new BigInteger(3)), (a.DivideBy(2).Lo, a.DivideBy(2).Hi));
        Assert.Equal((new BigInteger(-12), new BigInteger(20)), (a.ShiftDown(-2).Lo, a.ShiftDown(-2).Hi));
        Assert.Equal((new BigInteger(-4), new BigInteger(6)), (a.Widen(1).Lo, a.Widen(1).Hi));
        FixedInterval r = new FixedInterval(-1, 4).Sqrt(0);
        Assert.Equal((BigInteger.Zero, new BigInteger(2)), (r.Lo, r.Hi));
    }

    // ------------------------------------------------------------------
    // Constants
    // ------------------------------------------------------------------

    [Fact]
    public void TheStoredPiIsMachinsPi()
    {
        FixedInterval stored = ExactMath.Pi(2046);
        FixedInterval series = ExactMath.PiBySeries(2046);
        Assert.True(stored.Lo >= series.Lo - 1 && stored.Hi <= series.Hi + 1);
        Assert.True(series.Lo <= stored.Hi && stored.Lo <= series.Hi);
    }

    [Fact]
    public void TheStoredLn2IsTheSeriesLn2()
    {
        FixedInterval stored = ExactMath.Ln2(2048);
        FixedInterval series = ExactMath.Ln2BySeries(2048);
        Assert.True(series.Lo <= stored.Hi && stored.Lo <= series.Hi);
    }

    [Fact]
    public void PastTheStoredBitsTheConstantsComeFromTheirSeries()
    {
        FixedInterval pi = ExactMath.Pi(2100);
        Assert.Equal(ExactMath.Pi(2046).Lo, FixedInterval.FloorShift(pi.Lo, 54));
        FixedInterval ln2 = ExactMath.Ln2(2100);
        Assert.Equal(ExactMath.Ln2(2048).Lo, FixedInterval.FloorShift(ln2.Lo, 52));
    }

    [Fact]
    public void TheFastKernelsBreakpointsAreTheExactArctangents()
    {
        ReadOnlySpan<double> table = FastKernels.AtanBreakpoints;
        const int p = 200;
        for (int j = 1; j <= 8; j++)
        {
            FixedInterval t = FixedInterval.Exact(new BigInteger(j) << (p - 3));
            FixedInterval atan = ExactMath.AtanUnit(t, p);
            double hi = table[(2 * j) - 2];
            double lo = table[(2 * j) - 1];
            Assert.True(ExactMath.TryRound(atan.Lo, atan.Hi, -p, RoundingTarget.Double, out double head));
            Assert.Equal(head, hi);

            // The tail is the double nearest atan(j/8) - head.
            (BigInteger mh, int eh) = ExactMath.Decompose(hi);
            BigInteger headScaled = eh + p >= 0 ? mh << (eh + p) : mh >> -(eh + p);
            Assert.True(ExactMath.TryRound(atan.Lo - headScaled, atan.Hi - headScaled, -p, RoundingTarget.Double, out double tail));
            Assert.Equal(tail, lo);
        }
    }

    // ------------------------------------------------------------------
    // Rounding
    // ------------------------------------------------------------------

    [Fact]
    public void RoundingTiesGoToEven()
    {
        // 2^24 + 1 is the midpoint of 2^24 and 2^24 + 2; 2^24 + 3 of 2^24 + 2 and 2^24 + 4.
        Assert.Equal(16777216.0, ExactMath.RoundDyadic((1 << 24) + 1, 0, RoundingTarget.Single));
        Assert.Equal(16777220.0, ExactMath.RoundDyadic((1 << 24) + 3, 0, RoundingTarget.Single));
        Assert.Equal(9007199254740992.0, ExactMath.RoundDyadic((BigInteger.One << 53) + 1, 0, RoundingTarget.Double));
        Assert.Equal(-9007199254740996.0, ExactMath.RoundDyadic(-((BigInteger.One << 53) + 3), 0, RoundingTarget.Double));
    }

    [Fact]
    public void RoundingReachesSubnormalsZeroAndInfinity()
    {
        Assert.Equal(double.Epsilon, ExactMath.RoundDyadic(1, -1074, RoundingTarget.Double));
        Assert.Equal(0.0, ExactMath.RoundDyadic(1, -1075, RoundingTarget.Double));
        Assert.Equal(double.Epsilon, ExactMath.RoundDyadic(3, -1076, RoundingTarget.Double));
        Assert.Equal(0.0, ExactMath.RoundDyadic(1, -2000, RoundingTarget.Double));
        Assert.Equal((double)float.Epsilon, ExactMath.RoundDyadic(1, -149, RoundingTarget.Single));
        Assert.Equal(0.0, ExactMath.RoundDyadic(1, -150, RoundingTarget.Single));
        Assert.Equal(double.PositiveInfinity, ExactMath.RoundDyadic(1, 1024, RoundingTarget.Double));
        Assert.Equal(double.NegativeInfinity, ExactMath.RoundDyadic(-1, 128, RoundingTarget.Single));
        Assert.Equal(double.MaxValue, ExactMath.RoundDyadic((BigInteger.One << 53) - 1, 971, RoundingTarget.Double));

        // Rounding up past the largest finite value.
        Assert.Equal(double.PositiveInfinity, ExactMath.RoundDyadic((BigInteger.One << 54) - 1, 970, RoundingTarget.Double));
        Assert.Equal(double.PositiveInfinity, ExactMath.RoundDyadic((1 << 25) - 1, 103, RoundingTarget.Single));
        Assert.Equal(0.0, ExactMath.RoundDyadic(BigInteger.Zero, 5, RoundingTarget.Double));
    }

    [Fact]
    public void DoubleThenSingleCanDifferFromSingle()
    {
        // 1 + 2^-24 + 2^-60: just above a float midpoint, but within half a
        // double ulp of it, so the double rounds onto the midpoint and the
        // float of that ties to even -- down -- where the direct float
        // rounding goes up.
        BigInteger v = (BigInteger.One << 60) + (BigInteger.One << 36) + 1;
        Assert.Equal(1.0 + (1.0 / (1 << 23)), ExactMath.RoundDyadic(v, -60, RoundingTarget.Single));
        Assert.Equal(1.0, ExactMath.RoundDyadic(v, -60, RoundingTarget.DoubleThenSingle));
    }

    [Fact]
    public void AnIntervalAcrossZeroIsNotDecided()
    {
        Assert.False(ExactMath.TryRound(-1, 1, -2000, RoundingTarget.Double, out _));
        Assert.True(ExactMath.TryRound(0, 1, -2000, RoundingTarget.Double, out double zero));
        Assert.Equal(0.0, zero);
        Assert.False(ExactMath.TryRound(3, 5, -1, RoundingTarget.Single, out _));
    }

    [Fact]
    public void RationalsRoundWithAStickyBit()
    {
        Assert.Equal(1.0 / 3, ExactMath.RoundRational(1, 3, 0, RoundingTarget.Double));
        Assert.Equal((double)(1.0f / 3), ExactMath.RoundRational(1, 3, 0, RoundingTarget.Single));
        Assert.Equal(0.25, ExactMath.RoundRational(1, 4, 0, RoundingTarget.Double));
        Assert.Equal(1.0 / 343, ExactMath.RoundRational(1, 343, 0, RoundingTarget.Double));
        Assert.Equal(Math.ScaleB(1.0 / 3, -100), ExactMath.RoundRational(1, 3, -100, RoundingTarget.Double));
        Assert.Equal(Math.ScaleB(10.0 / 7, 0), ExactMath.RoundRational(new BigInteger(10) << 200, new BigInteger(7) << 200, 0, RoundingTarget.Double));
    }

    [Fact]
    public void DecomposeGivesAnOddIntegerAndAPowerOfTwo()
    {
        Assert.Equal((BigInteger.One, 0), ExactMath.Decompose(1.0));
        Assert.Equal((new BigInteger(-3), -1), ExactMath.Decompose(-1.5));
        Assert.Equal((BigInteger.One, -1074), ExactMath.Decompose(double.Epsilon));
        Assert.Equal((BigInteger.One, 10), ExactMath.Decompose(1024.0));
    }

    // ------------------------------------------------------------------
    // Exact powers
    // ------------------------------------------------------------------

    [Fact]
    public void AnIntegerPowerOnAFloatMidpointTiesToEven()
    {
        // 4097^2 = 16785409 needs 25 bits: exactly halfway between the floats
        // 16785408 and 16785410, whose even neighbour is 16785408.
        Assert.Equal(16785408f, DetMathF.Pow(4097f, 2f));

        // 4099^2 = 16801801, between 16801800 and 16801802: even is down again.
        Assert.Equal(16801800f, DetMathF.Pow(4099f, 2f));
        Assert.Equal(16785409.0, DetMath.Pow(4097.0, 2.0));
    }

    [Fact]
    public void AnIntegerPowerOnADoubleMidpointTiesToEven()
    {
        // 94906267^2 has 54 significant bits and is odd, so it is halfway
        // between two doubles.
        BigInteger exact = BigInteger.Pow(94906267, 2);
        Assert.Equal(54, (int)exact.GetBitLength());
        double expected = ExactMath.RoundDyadic(exact, 0, RoundingTarget.Double);
        Assert.Equal(0, (long)expected % 4);
        Assert.Equal(expected, DetMath.Pow(94906267.0, 2.0));
    }

    [Theory]
    [InlineData(0.25, 0.5, 0.5)]
    [InlineData(16.0, 0.25, 2.0)]
    [InlineData(9.0, 0.5, 3.0)]
    [InlineData(9.0, 1.5, 27.0)]
    [InlineData(1.0 / 64, -0.5, 8.0)]
    [InlineData(27.0, -1.0, 1.0 / 27.0)]
    [InlineData(2.0, -1074.0, 4.9406564584124654E-324)]
    [InlineData(2.0, -1075.0, 0.0)]
    [InlineData(2.0, 1024.0, double.PositiveInfinity)]
    [InlineData(4.0, 0.5, 2.0)]
    [InlineData(65536.0, 0.0625, 2.0)]
    [InlineData(3.0, 64.0, 3.4336838202925124E+30)]
    [InlineData(7.0, -3.0, 1.0 / 343.0)]
    [InlineData(0.5, 1e10, 0.0)]
    [InlineData(2.0, 1e10, double.PositiveInfinity)]
    public void ExactPowersAreRoundedExactly(double x, double y, double expected)
    {
        Assert.Equal(expected, DetMath.Pow(x, y));
    }

    [Theory]
    [InlineData(3.0, 65.0)]
    [InlineData(3.0, 0.5)]
    [InlineData(5.0, 0.03125)]
    [InlineData(2.0, 0.5)]
    [InlineData(10.0, 1e-9)]
    public void InexactPowersAreLeftToTheGeneralPath(double x, double y)
    {
        (BigInteger mx, int ex) = ExactMath.Decompose(x);
        (BigInteger my, int ey) = ExactMath.Decompose(y);
        Assert.False(ExactMath.TryExactPow(mx, ex, my, ey, false, RoundingTarget.Double, out _));
    }

    [Fact]
    public void ExactPowerDetectionCoversEveryRoot()
    {
        // mx = 1 with a fractional y that does not divide the exponent.
        (BigInteger m, int e) = ExactMath.Decompose(8.0);
        Assert.False(ExactMath.TryExactPow(m, e, 1, -1, false, RoundingTarget.Double, out _));

        // 2^40 to the 2^-31: past the largest root the detection tries.
        (m, e) = ExactMath.Decompose(Math.ScaleB(1.0, 40));
        Assert.False(ExactMath.TryExactPow(m, e, 1, -31, false, RoundingTarget.Double, out _));

        // 3^64 is a perfect 64th power but 64th roots of odd numbers are past
        // what a 53-bit odd part can be.
        (m, e) = ExactMath.Decompose(9.0);
        Assert.False(ExactMath.TryExactPow(m, e, 1, -6, false, RoundingTarget.Double, out _));

        // 4^(1/2) passes the exponent test, and 2^(3/2) does not.
        (m, e) = ExactMath.Decompose(4.0);
        Assert.True(ExactMath.TryExactPow(m, e, 1, -1, false, RoundingTarget.Double, out double two));
        Assert.Equal(2.0, two);
        (m, e) = ExactMath.Decompose(2.0);
        Assert.False(ExactMath.TryExactPow(m, e, 3, -1, false, RoundingTarget.Double, out _));

        // A negative integer power of an odd base is a rational.
        (m, e) = ExactMath.Decompose(3.0);
        Assert.True(ExactMath.TryExactPow(m, e, 1, 1, true, RoundingTarget.Double, out double ninth));
        Assert.Equal(1.0 / 9, ninth);
    }

    // ------------------------------------------------------------------
    // Brackets and the Ziv loop
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1e-10)]
    [InlineData(-3e-9)]
    [InlineData(1e-300)]
    [InlineData(4.9406564584124654E-324)]
    public void TinyArgumentsAreDecidedByTheirFirstTerms(double x)
    {
        Assert.Equal(x, ExactMath.Sin(x, RoundingTarget.Double));
        Assert.Equal(x, ExactMath.Tan(x, RoundingTarget.Double));
        Assert.Equal(x, ExactMath.Asin(x, RoundingTarget.Double));
        Assert.Equal(1.0, ExactMath.Cos(x, RoundingTarget.Double));
    }

    [Fact]
    public void ATinyArgumentTooCoarseForItsBracketTakesTheSeries()
    {
        // 2^-21: sin x is x (1 - 2^-43.6), a few ulps under x, which the
        // bracket [x - x^3/4, x] cannot place within one ulp.
        double x = Math.ScaleB(1.0, -21);
        double expected = x - (x * x * x / 6);
        Assert.Equal(expected, ExactMath.Sin(x, RoundingTarget.Double));
        Assert.Equal(1 - (x * x / 2), ExactMath.Cos(x, RoundingTarget.Double));
    }

    [Fact]
    public void LogarithmsNearOneUseTheirBracket()
    {
        // ln(1 + 2^-52) = 2^-52 - 2^-105 + 2^-158/3: just above the double
        // 2^-52 - 2^-105, and nearer it than 2^-52.
        double x = 1 + Math.ScaleB(1.0, -52);
        Assert.Equal(Math.BitDecrement(Math.ScaleB(1.0, -52)), ExactMath.Log(x, RoundingTarget.Double));
        double below = 1 - Math.ScaleB(1.0, -53);
        Assert.Equal(-Math.ScaleB(1.0, -53), ExactMath.Log(below, RoundingTarget.Double));

        // 1 + 2^-21: the bracket is too wide at double precision, and the
        // series decides.
        double wide = 1 + Math.ScaleB(1.0, -21);
        Assert.Equal(4.7683704451632344e-07, ExactMath.Log(wide, RoundingTarget.Double));
    }

    [Fact]
    public void ATangentNearAPoleNeedsMorePrecisionThanTheFirstPass()
    {
        // The double nearest pi/2 is 6.1e-17 short of it, so tan is 1.6e16;
        // the reduced argument's first 56 bits are zero.
        Assert.Equal(16331239353195370.0, ExactMath.Tan(Math.PI / 2, RoundingTarget.Double));

        // And pi's double is 1.2e-16 short of pi, so tan there is minus that.
        Assert.Equal(-1.2246467991473532e-16, ExactMath.Tan(Math.PI, RoundingTarget.Double));
    }

    [Fact]
    public void HugeArgumentsReduceExactly()
    {
        // sin(2^1000): its reduction needs more than a thousand bits of pi.
        double x = Math.ScaleB(1.0, 1000);
        double s = ExactMath.Sin(x, RoundingTarget.Double);
        double c = ExactMath.Cos(x, RoundingTarget.Double);
        Assert.InRange((s * s) + (c * c), 1 - 1e-15, 1 + 1e-15);
        Assert.Equal(s, DetMath.Sin(x));
        Assert.Equal((float)s, DetMath.SinToSingle(x));
    }

    [Fact]
    public void AcosOfZeroIsHalfPi()
    {
        Assert.Equal(Math.PI / 2, ExactMath.Acos(0, RoundingTarget.Double));
        Assert.Equal(Math.PI, ExactMath.Acos(-1, RoundingTarget.Double));
        Assert.Equal((double)(float)(Math.PI / 2), ExactMath.Acos(0, RoundingTarget.Single));
    }

    [Fact]
    public void Atan2HandlesAZeroOnEitherSide()
    {
        Assert.Equal(Math.PI, ExactMath.Atan2(0, -1, RoundingTarget.Double));
        Assert.Equal(Math.PI / 2, ExactMath.Atan2(1, 0, RoundingTarget.Double));
        Assert.Equal(-Math.PI / 2, ExactMath.Atan2(-1e-300, 0, RoundingTarget.Double));
        Assert.Equal(1e-300 / 3, ExactMath.Atan2(1e-300, 3, RoundingTarget.Double));
    }

    [Fact]
    public void ThePowRangeLimitsSaturate()
    {
        Assert.Equal(double.PositiveInfinity, ExactMath.Pow(10, 400, RoundingTarget.Double));
        Assert.Equal(0.0, ExactMath.Pow(10, -400, RoundingTarget.Double));
        Assert.Equal(double.PositiveInfinity, ExactMath.Pow(10, 39, RoundingTarget.Single));
        Assert.Equal(0.0, ExactMath.Pow(10, -46, RoundingTarget.Single));
    }

    [Fact]
    public void NonConvergenceNamesTheFunction()
    {
        InvalidOperationException e = ExactMath.NotConverged("Sin", 1.5);
        Assert.Contains("Sin(1.5)", e.Message, StringComparison.Ordinal);
    }
}
