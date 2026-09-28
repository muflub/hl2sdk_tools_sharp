//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Numerics;

/// <summary>
/// Correctly rounded double-precision elementary functions, computed in
/// managed code so the same argument gives the same bits on every OS and CPU.
/// Use these, not <see cref="Math"/>, anywhere the result can reach a
/// compiled map. <see cref="DetMathF"/> is the single-precision set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> <see cref="Math.Sin"/>, <see cref="Math.Pow"/> and the rest
/// call the platform C library, which is not correctly rounded and differs
/// from platform to platform in the last bit. A correctly rounded result is
/// the double nearest the true value (ties to even); it is unique, so it is
/// the same on every machine, and it equals any library's answer wherever that
/// library is right, which for glibc is nearly always.
/// </para>
/// <para>
/// <b>Cost.</b> A double result has too few spare bits for a plain
/// double-precision approximation to decide its rounding. <see cref="Log"/>,
/// which the detail-prop Gaussian takes once a sample, has a double-double
/// kernel good to 2^-64 (<see cref="FastKernels.LogAccurate"/>) and goes to
/// the exact tier only for the arguments within that of a rounding
/// boundary, about one in two thousand of a typical spread: a mean of about
/// 0.15 microseconds a call against the exact tier's 30, measured by
/// <c>DetMathThroughputTests</c>. <see cref="Sin"/>, <see cref="Cos"/> and
/// <see cref="Pow"/> go straight to the exact tier (<see cref="ExactMath"/>):
/// tens of microseconds a call, which is fine for the once-per-light and
/// once-per-entity uses the compile has, and wrong for a per-sample loop. A
/// caller that converts the result to float at once should use the
/// <c>...ToSingle</c> forms, which give the same bits as the cast for a small
/// multiple of <see cref="Math"/>'s cost.
/// </para>
/// <para>
/// <b>Special values</b> follow IEEE 754 and C's Annex F, as <see cref="Math"/>
/// documents them: a NaN argument is returned as it is, a domain error returns
/// <see cref="double.NaN"/>.
/// </para>
/// </remarks>
public static class DetMath
{
    /// <summary>The correctly rounded sine.</summary>
    /// <param name="x">The angle, in radians.</param>
    /// <returns><c>sin x</c>; NaN for an infinite or NaN argument; x itself for a zero.</returns>
    public static double Sin(double x)
    {
        if (x == 0 || !double.IsFinite(x))
        {
            return x == 0 ? x : double.IsNaN(x) ? x : double.NaN;
        }

        return ExactMath.Sin(x, RoundingTarget.Double);
    }

    /// <summary>The correctly rounded cosine.</summary>
    /// <param name="x">The angle, in radians.</param>
    /// <returns><c>cos x</c>; NaN for an infinite or NaN argument; 1 for a zero.</returns>
    public static double Cos(double x)
    {
        if (x == 0 || !double.IsFinite(x))
        {
            return x == 0 ? 1.0 : double.IsNaN(x) ? x : double.NaN;
        }

        return ExactMath.Cos(x, RoundingTarget.Double);
    }

    /// <summary>The correctly rounded natural logarithm.</summary>
    /// <param name="x">The argument.</param>
    /// <returns>
    /// <c>ln x</c>; <c>-inf</c> for a zero, NaN for a negative or NaN argument,
    /// <c>+inf</c> for <c>+inf</c>, and <c>+0</c> for 1.
    /// </returns>
    public static double Log(double x)
    {
        if (double.IsNaN(x))
        {
            return x;
        }

        if (x == 0)
        {
            return double.NegativeInfinity;
        }

        if (x < 0)
        {
            return double.NaN;
        }

        if (double.IsPositiveInfinity(x) || x == 1)
        {
            return x == 1 ? 0.0 : x;
        }

        // The fast tier first: a double-double within 2^-64, rounded when
        // its whole error band rounds to one double, which is all but about
        // one argument in two thousand. The rest, those within 2^-64 of a
        // rounding boundary, go to the exact tier, so the result is the same
        // correctly rounded double either way.
        (double hi, double lo) = FastKernels.LogAccurate(x);
        if (FastKernels.TryRoundDouble(hi, lo, FastKernels.LogAccurateError, out double result))
        {
            return result;
        }

        return ExactMath.Log(x, RoundingTarget.Double);
    }

    /// <summary>The correctly rounded power.</summary>
    /// <param name="x">The base.</param>
    /// <param name="y">The exponent.</param>
    /// <returns><c>x^y</c>, with the special cases <see cref="DetMathF.Pow"/> lists.</returns>
    public static double Pow(double x, double y)
    {
        if (PowSpecialCase(x, y, out double special, out double magnitude, out bool negate))
        {
            return special;
        }

        // Decide the far ends from the fast head of y ln x, so a huge y does
        // not send the exact tier after thousands of bits of ln x first.
        // e^711 overflows and e^-746.5 is under half the least subnormal.
        (double zh, _) = FastKernels.LogTimes(magnitude, y);
        double result = zh > 712 ? double.PositiveInfinity
            : zh < -748 ? 0.0
            : ExactMath.Pow(magnitude, y, RoundingTarget.Double);
        return negate ? -result : result;
    }

    /// <summary>
    /// The bits of <c>(float)Sin(x)</c>, without the exact tier's cost.
    /// </summary>
    /// <param name="x">The angle, in radians.</param>
    /// <returns>The sine, rounded to double and then to float.</returns>
    /// <remarks>
    /// For a caller that takes a double sine and stores it as a float. This
    /// is the float of the correctly rounded double, not the correctly rounded
    /// float: the two differ only for a result within half a double ulp of a
    /// float rounding boundary, and matching the cast keeps such a caller's
    /// output the same as <c>(float)Math.Sin(x)</c> wherever the platform's
    /// double sine is right.
    /// </remarks>
    public static float SinToSingle(double x)
    {
        if (x == 0 || !double.IsFinite(x))
        {
            return (float)Sin(x);
        }

        if (FastKernels.ReduceHalfPi(x, out double rh, out double rl, out int quadrant)
            && FastKernels.TryNarrow(DetMathF.SinOfReduced(rh, rl, quadrant), out float result))
        {
            return result;
        }

        return (float)ExactMath.Sin(x, RoundingTarget.DoubleThenSingle);
    }

    /// <summary>
    /// The bits of <c>(float)Cos(x)</c>, without the exact tier's cost.
    /// </summary>
    /// <param name="x">The angle, in radians.</param>
    /// <returns>The cosine, rounded to double and then to float.</returns>
    /// <remarks>As <see cref="SinToSingle"/>.</remarks>
    public static float CosToSingle(double x)
    {
        if (x == 0 || !double.IsFinite(x))
        {
            return (float)Cos(x);
        }

        if (FastKernels.ReduceHalfPi(x, out double rh, out double rl, out int quadrant)
            && FastKernels.TryNarrow(DetMathF.CosOfReduced(rh, rl, quadrant), out float result))
        {
            return result;
        }

        return (float)ExactMath.Cos(x, RoundingTarget.DoubleThenSingle);
    }

    /// <summary>
    /// The bits of <c>(float)Pow(x, y)</c>, without the exact tier's cost.
    /// </summary>
    /// <param name="x">The base.</param>
    /// <param name="y">The exponent.</param>
    /// <returns>The power, rounded to double and then to float.</returns>
    /// <remarks>As <see cref="SinToSingle"/>.</remarks>
    public static float PowToSingle(double x, double y)
    {
        if (PowSpecialCase(x, y, out double special, out double magnitude, out bool negate))
        {
            return (float)special;
        }

        (double zh, double zl) = FastKernels.LogTimes(magnitude, y);

        // As DetMathF.Pow: past e^89.5 the double is past the float range,
        // and under e^-104.5 it is under half the least float subnormal.
        float result;
        if (zh > 89.5)
        {
            result = float.PositiveInfinity;
        }
        else if (zh < -104.5)
        {
            result = 0f;
        }
        else if (!FastKernels.TryNarrow(FastKernels.Exp(zh, zl), out result))
        {
            result = (float)ExactMath.Pow(magnitude, y, RoundingTarget.DoubleThenSingle);
        }

        return negate ? -result : result;
    }

    /// <summary>
    /// The special cases of <c>pow</c>, shared by the float and double forms.
    /// </summary>
    /// <param name="x">The base.</param>
    /// <param name="y">The exponent.</param>
    /// <param name="result">The result, when this is a special case.</param>
    /// <param name="magnitude">Otherwise <c>|x|</c>: finite, positive and not 1.</param>
    /// <param name="negate">
    /// Otherwise whether the result is <c>-(|x|^y)</c>: a negative x with an
    /// odd integer y.
    /// </param>
    /// <returns>Whether the result is decided.</returns>
    internal static bool PowSpecialCase(double x, double y, out double result, out double magnitude, out bool negate)
    {
        magnitude = Math.Abs(x);
        negate = false;
        result = 0;

        if (y == 0 || x == 1)
        {
            result = 1;
            return true;
        }

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            result = double.IsNaN(x) ? x : y;
            return true;
        }

        if (double.IsInfinity(y))
        {
            result = magnitude == 1 ? 1
                : (magnitude < 1) == (y > 0) ? 0.0
                : double.PositiveInfinity;
            return true;
        }

        bool integer = Math.Floor(y) == y;
        bool odd = integer && Math.Abs(y) < 9007199254740992.0 && ((long)y & 1) != 0;

        if (x == 0 || double.IsInfinity(x))
        {
            // A zero base and an infinite one are reciprocals: 0^y and
            // inf^-y agree. The sign survives only an odd integer power.
            bool large = (x == 0) == (y < 0);
            double value = large ? double.PositiveInfinity : 0.0;
            result = odd && double.IsNegative(x) ? -value : value;
            return true;
        }

        if (x < 0)
        {
            if (!integer)
            {
                result = double.NaN;
                return true;
            }

            negate = odd;
        }

        if (magnitude == 1)
        {
            result = negate ? -1.0 : 1.0;
            return true;
        }

        return false;
    }
}
