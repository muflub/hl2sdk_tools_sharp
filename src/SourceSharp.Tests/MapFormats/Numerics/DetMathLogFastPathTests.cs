//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Numerics;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapFormats.Numerics;

/// <summary>
/// <see cref="DetMath.Log"/>'s fast path: the double-double kernel and the
/// rounding test give the exact tier's double on every argument they decide,
/// and decide nearly all of them.
/// </summary>
/// <remarks>
/// <para>
/// The kernel's error bound is derived beside it
/// (<see cref="FastKernels.LogAccurate"/>); these facts are the measurement
/// that backs the derivation. Every argument in a deterministic sample is
/// run through the kernel and <see cref="FastKernels.TryRoundDouble"/> and,
/// separately, through the exact tier, and the two doubles must agree
/// wherever the fast path claims a result.
/// </para>
/// <para>
/// The sample covers every binade (random bit patterns, subnormals
/// included), the floats in (0, 1] the Gaussian takes the log of, the
/// neighbourhood of 1 where the result is tiny, and the edges of the
/// reduction (powers of two, <c>sqrt 2</c> and <c>sqrt(1/2)</c>, where k and
/// m change). It is 262,144 arguments in the suite, a few seconds of the
/// exact tier: enough that dropping the smallest double-double correction
/// from the kernel (the <c>zl</c> term of <c>z/3</c>, about 2^-62 relative)
/// fails it, which 24,576 arguments did not. Set
/// <c>SS_DETMATH_LOG_SAMPLE</c> to a larger count to run the same sample
/// scaled up. Measured when the fast path went in: 16,777,216 arguments
/// (and, separately, 4,194,304), 99.2 % of them decided by the fast path and
/// every decided one equal to the exact tier; the refusals are almost all in
/// the few-ulps-from-1 family, 0.05 % in each of the others.
/// </para>
/// </remarks>
public sealed class DetMathLogFastPathTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Takes the runner's output sink.</summary>
    /// <param name="output">Where the counts are written.</param>
    public DetMathLogFastPathTests(ITestOutputHelper output) => _output = output;

    private static int SampleSize =>
        int.TryParse(Environment.GetEnvironmentVariable("SS_DETMATH_LOG_SAMPLE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : 262144;

    /// <summary>
    /// The i-th argument of the sample: eight families in turn, each drawn
    /// from its own deterministic stream so the sample is the same on every
    /// run and every machine.
    /// </summary>
    internal static double Argument(int i)
    {
        Random random = new(unchecked((i * 1103515245) + 12345));
        switch (i % 8)
        {
            case 0:
            {
                // Any finite positive double: uniform bits, subnormals included.
                long bits = random.NextInt64(1, 0x7FF0000000000000L);
                return BitConverter.Int64BitsToDouble(bits);
            }

            case 1:
            case 2:
            {
                // A float in (0, 1], as the Gaussian's rsq.
                int bits = random.Next(1, 0x3F800001);
                return BitConverter.Int32BitsToSingle(bits);
            }

            case 3:
                // Within 2^-20 of 1, where ln x is tiny and relative error matters most.
                return 1 + (((random.NextDouble() * 2) - 1) * (1.0 / (1 << 20)));

            case 4:
                // A few ulps from 1.
                return BitConverter.Int64BitsToDouble(
                    BitConverter.DoubleToInt64Bits(1.0) + random.Next(-64, 65));

            case 5:
            {
                // A few ulps from a power of two, anywhere in the range.
                double p = Math.ScaleB(1.0, random.Next(-1074, 1024));
                long bits = BitConverter.DoubleToInt64Bits(p) + random.Next(-8, 9);
                return bits <= 0 ? double.Epsilon : BitConverter.Int64BitsToDouble(Math.Min(bits, 0x7FEFFFFFFFFFFFFFL));
            }

            case 6:
            {
                // A few ulps from sqrt 2 or sqrt(1/2) times a power of two: m's switch.
                double edge = (random.Next(2) == 0 ? 1.4142135623730951 : 0.7071067811865476)
                    * Math.ScaleB(1.0, random.Next(-1000, 1000));
                return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(edge) + random.Next(-8, 9));
            }

            default:
                // Typical magnitudes: 1e-6 to 1e6, log-uniform.
                return Math.ScaleB(1 + random.NextDouble(), random.Next(-20, 20));
        }
    }

    [Fact]
    public void EveryDecidedArgumentIsTheExactTiersDouble()
    {
        int n = SampleSize;
        bool[] decided = new bool[n];
        string?[] wrong = new string?[n];
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
        {
            double x = Argument(i);
            (double hi, double lo) = FastKernels.LogAccurate(x);
            double exact = ExactMath.Log(x, RoundingTarget.Double);
            if (FastKernels.TryRoundDouble(hi, lo, FastKernels.LogAccurateError, out double fast))
            {
                decided[i] = true;
                if (BitConverter.DoubleToInt64Bits(fast) != BitConverter.DoubleToInt64Bits(exact))
                {
                    wrong[i] = string.Create(CultureInfo.InvariantCulture, $"ln({x:R}) fast {fast:R}, exact {exact:R}");
                }
            }

            if (BitConverter.DoubleToInt64Bits(DetMath.Log(x)) != BitConverter.DoubleToInt64Bits(exact))
            {
                wrong[i] ??= string.Create(CultureInfo.InvariantCulture, $"DetMath.Log({x:R}) is not the exact tier's {exact:R}");
            }
        });

        int count = decided.Count(d => d);
        _output.WriteLine($"{n} arguments, {count} decided by the fast path ({n - count} to the exact tier)");
        string[] failures = [.. wrong.Where(w => w is not null).Take(20)!];
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The fast path decides all but a sliver of the sample, so it is what
    /// runs. A kernel whose bound had to be loosened to pass would show here
    /// before it showed in a profile.
    /// </summary>
    [Fact]
    public void TheFastPathDecidesAlmostEveryArgument()
    {
        int decided = 0;
        int[] refusedByFamily = new int[8];
        const int n = 65536;
        for (int i = 0; i < n; i++)
        {
            (double hi, double lo) = FastKernels.LogAccurate(Argument(i));
            if (FastKernels.TryRoundDouble(hi, lo, FastKernels.LogAccurateError, out _))
            {
                decided++;
            }
            else
            {
                refusedByFamily[i % 8]++;
            }
        }

        _output.WriteLine($"{decided} of {n} decided; refused by family: {string.Join(", ", refusedByFamily)} of {n / 8} each");
        Assert.True(decided >= n - (n / 100), $"only {decided} of {n} decided");
    }

    /// <summary>
    /// The rounding test accepts a band inside the half-ulps and refuses one
    /// that reaches a midpoint on either side, including the narrower one
    /// below a power of two.
    /// </summary>
    [Fact]
    public void TheRoundingTestRefusesABandThatReachesAMidpoint()
    {
        const double Err = 1.0 / (1L << 62) / 4;
        double halfUlpAbove1 = Math.ScaleB(1.0, -53);
        double halfUlpBelow1 = Math.ScaleB(1.0, -54);

        Assert.True(FastKernels.TryRoundDouble(1.0, 0.0, Err, out double r));
        Assert.Equal(1.0, r);

        // Above 1 the half-ulp is 2^-53; below it, 2^-54.
        Assert.True(FastKernels.TryRoundDouble(1.0, halfUlpAbove1 * 0.99, Err, out _));
        Assert.False(FastKernels.TryRoundDouble(1.0, halfUlpAbove1 - Math.ScaleB(1.0, -66), Err, out _));
        Assert.True(FastKernels.TryRoundDouble(1.0, -halfUlpBelow1 * 0.99, Err, out _));
        Assert.False(FastKernels.TryRoundDouble(1.0, -halfUlpBelow1 + Math.ScaleB(1.0, -67), Err, out _));

        // The same, mirrored, for a negative head.
        Assert.True(FastKernels.TryRoundDouble(-1.0, -halfUlpAbove1 * 0.99, Err, out r));
        Assert.Equal(-1.0, r);
        Assert.False(FastKernels.TryRoundDouble(-1.0, -halfUlpAbove1 + Math.ScaleB(1.0, -66), Err, out _));
        Assert.False(FastKernels.TryRoundDouble(-1.0, halfUlpBelow1 - Math.ScaleB(1.0, -67), Err, out _));

        // No relative ulp to reason in: refused.
        Assert.False(FastKernels.TryRoundDouble(double.Epsilon, 0, Err, out _));
        Assert.False(FastKernels.TryRoundDouble(0, 0, Err, out _));
        Assert.False(FastKernels.TryRoundDouble(double.PositiveInfinity, 0, Err, out _));
        Assert.False(FastKernels.TryRoundDouble(double.NaN, 0, Err, out _));
    }

    /// <summary>
    /// ln of a power of two is <c>k ln 2</c>, whose correctly rounded doubles
    /// are known: the kernel's reduction and its <c>ln 2</c> split reach them.
    /// </summary>
    [Theory]
    [InlineData(1, 0.6931471805599453)]
    [InlineData(-1, -0.6931471805599453)]
    [InlineData(10, 6.931471805599453)]
    [InlineData(-1074, -744.4400719213812)]
    [InlineData(1023, 709.0895657128241)]
    public void APowerOfTwoIsKTimesLn2(int k, double expected)
    {
        Assert.Equal(expected, ExactMath.Log(Math.ScaleB(1.0, k), RoundingTarget.Double));
        Assert.Equal(expected, DetMath.Log(Math.ScaleB(1.0, k)));
    }
}
