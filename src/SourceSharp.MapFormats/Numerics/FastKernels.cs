//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Numerics;

/// <summary>
/// The fast tier of <see cref="DetMath"/> and <see cref="DetMathF"/>:
/// double-precision approximations with a known relative error, from which a
/// float result can almost always be rounded correctly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a float result, and why this is enough.</b> Every kernel here returns
/// a double within <see cref="MaxRelativeError"/> of the true value. A float
/// has 24 significant bits against the double's 53, so the true value lies
/// in a band about 2^-44 wide around the approximation, and
/// <see cref="TryNarrow"/> rounds both edges of that band to float. When they
/// agree, which is all but about one float result in a million, that IS the
/// correctly rounded result; otherwise the caller asks <see cref="ExactMath"/>.
/// The kernels therefore do not have to be correctly rounded, or even
/// especially tight: only honest about their bound.
/// </para>
/// <para>
/// <b>Determinism.</b> Only IEEE-754 double operations that every conforming
/// platform performs identically: add, subtract, multiply, divide, square root
/// and round-to-integer, each rounded to nearest even. No libm, no hardware
/// estimates, and no fused multiply-add: the exact products the reduction
/// needs come from Dekker's splitting (<see cref="TwoProduct"/>) rather than
/// <see cref="Math.FusedMultiplyAdd(double, double, double)"/>. .NET's JIT
/// does not contract <c>a * b + c</c> into a fused operation or reassociate
/// floating-point expressions, and evaluates doubles in SSE2 or NEON
/// registers rather than x87's extended precision, so the source below is the
/// arithmetic performed on x64 and arm64 alike.
/// </para>
/// <para>
/// <b>Error budget.</b> Each kernel's derivation is beside it and comes to at
/// most 2^-49.5 relative. <see cref="MaxRelativeError"/> claims 2^-48 and
/// <see cref="TryNarrow"/> allows 2^-44, sixteen times that again, so a slip
/// in a derivation costs speed (a fallback) before it could cost a wrong
/// result. The exhaustive facts measure every kernel against a reference over
/// whole binades and require the measured error under
/// <see cref="MaxRelativeError"/>.
/// </para>
/// <para>
/// Polynomial coefficients are plain Taylor coefficients, <c>1.0 / n!</c> and
/// <c>1.0 / n</c> as constant expressions (every factorial used is exact in
/// a double, so each is the correctly rounded reciprocal), rather than
/// minimax fits: a few more terms, in exchange for coefficients anyone can
/// check by reading them.
/// </para>
/// </remarks>
internal static class FastKernels
{
    /// <summary>The relative error every kernel stays under.</summary>
    internal const double MaxRelativeError = 1.0 / (1L << 48);

    /// <summary>The half-width of the band <see cref="TryNarrow"/> rounds.</summary>
    internal const double NarrowMargin = 1.0 / (1L << 44);

    /// <summary>Below this magnitude a float result is zero whatever the approximation's error.</summary>
    private const double BelowFloat = 1.0 / (1L << 62) / (1L << 62) / (1L << 62);

    /// <summary>
    /// <see cref="ReduceHalfPi"/> handles arguments below this; larger ones go
    /// to the exact tier. <c>k</c> then has at most 25 bits, which with the
    /// 28-bit <see cref="Pio2Part1"/> and <see cref="Pio2Part2"/> keeps
    /// <c>k * part</c> exact.
    /// </summary>
    internal const double ReductionLimit = 1 << 25;

    /// <summary>
    /// A reduced argument below this goes to the exact tier: the reduction's
    /// absolute error, about 2^-104, is no longer small beside it.
    /// </summary>
    private const double ReducedFloor = 1.0 / (1L << 40);

    // pi/2 in four pieces: the first two 28 bits wide so k times each is
    // exact, the last two each the double nearest the remainder. Their sum
    // is within 2^-168 of pi/2.
    private const double Pio2Part1 = 1.570796325802803;
    private const double Pio2Part2 = 9.920935739593517e-10;
    private const double Pio2Part3 = 5.721188726109832e-18;
    private const double Pio2Part4 = 4.3359050650618903e-35;
    private const double TwoOverPi = 0.6366197723675814;

    /// <summary>pi/2 as a double-double.</summary>
    internal const double HalfPiHi = 1.5707963267948966;

    /// <summary>The low part of <see cref="HalfPiHi"/>.</summary>
    internal const double HalfPiLo = 6.123233995736766e-17;

    /// <summary>pi as a double-double.</summary>
    internal const double PiHi = 3.141592653589793;

    /// <summary>The low part of <see cref="PiHi"/>.</summary>
    internal const double PiLo = 1.2246467991473532e-16;

    // ln 2 with a 40-bit head, so k * head is exact for |k| < 2^13, and the
    // double nearest the rest. Their sum is within 2^-102 of ln 2.
    private const double Ln2Hi = 0.6931471805592082;
    private const double Ln2Lo = 7.371002565167799e-13;
    private const double InvLn2 = 1.4426950408889634;
    private const double Sqrt2 = 1.4142135623730951;

    /// <summary>Veltkamp's splitter for a double: 2^27 + 1.</summary>
    private const double Splitter = 134217729.0;

    /// <summary>
    /// <c>atan(j/8)</c> for <c>j = 1..8</c> as double-doubles, head then tail:
    /// the breakpoints of <see cref="Atan"/>. Each head is the nearest double
    /// and each tail the nearest double to the remainder; a fact recomputes
    /// both with the exact tier.
    /// </summary>
    internal static ReadOnlySpan<double> AtanBreakpoints =>
    [
        0.12435499454676144, -3.1253241424539383e-18,
        0.24497866312686414, 1.0698755618734451e-17,
        0.35877067027057225, -2.4623815582638635e-17,
        0.4636476090008061, 2.2698777452961687e-17,
        0.5585993153435624, -5.4556305485916264e-18,
        0.6435011087932844, 1.5834785051444286e-17,
        0.7188299996216245, -2.1478388444456983e-17,
        0.7853981633974483, 3.061616997868383e-17,
    ];

    // ------------------------------------------------------------------
    // Error-free transformations
    // ------------------------------------------------------------------

    /// <summary>Knuth's two-sum: <c>a + b = s + e</c> exactly.</summary>
    /// <param name="a">An addend.</param>
    /// <param name="b">An addend.</param>
    /// <returns>The rounded sum and its exact error.</returns>
    internal static (double S, double E) TwoSum(double a, double b)
    {
        double s = a + b;
        double bb = s - a;
        double e = (a - (s - bb)) + (b - bb);
        return (s, e);
    }

    /// <summary>Dekker's fast two-sum, for <c>|a| &gt;= |b|</c>.</summary>
    /// <param name="a">The larger addend.</param>
    /// <param name="b">The smaller addend.</param>
    /// <returns>The rounded sum and its exact error.</returns>
    internal static (double S, double E) FastTwoSum(double a, double b)
    {
        double s = a + b;
        return (s, b - (s - a));
    }

    /// <summary>
    /// Dekker's two-product: <c>a * b = p + e</c> exactly, by Veltkamp
    /// splitting, for products well inside the double range.
    /// </summary>
    /// <param name="a">A factor.</param>
    /// <param name="b">A factor.</param>
    /// <returns>The rounded product and its exact error.</returns>
    internal static (double P, double E) TwoProduct(double a, double b)
    {
        double p = a * b;
        (double ah, double al) = Split(a);
        (double bh, double bl) = Split(b);
        double e = (((ah * bh) - p) + (ah * bl) + (al * bh)) + (al * bl);
        return (p, e);
    }

    private static (double Hi, double Lo) Split(double a)
    {
        double t = Splitter * a;
        double hi = t - (t - a);
        return (hi, a - hi);
    }

    // ------------------------------------------------------------------
    // Rounding to float
    // ------------------------------------------------------------------

    /// <summary>
    /// Rounds an approximation to float when the whole band it may be wrong by
    /// rounds to one value.
    /// </summary>
    /// <param name="approx">A double within <see cref="MaxRelativeError"/> of the true value.</param>
    /// <param name="result">The float, when decided.</param>
    /// <returns>Whether the rounding is decided.</returns>
    /// <remarks>
    /// The same answer serves both float semantics <see cref="RoundingTarget"/>
    /// names. A double band <c>[lo, hi]</c> that contains the true value also
    /// contains its correctly rounded double, and rounding to float is
    /// monotone, so equal ends mean the correctly rounded float and the
    /// float of the correctly rounded double are that same value.
    /// </remarks>
    internal static bool TryNarrow(double approx, out float result)
    {
        double a = Math.Abs(approx);
        if (a < BelowFloat)
        {
            // Under 2^-186, and so under half the least float subnormal, for
            // any error short of the approximation being off by a factor.
            result = (float)approx;
            return true;
        }

        double m = a * NarrowMargin;
        float lo = (float)(approx - m);
        float hi = (float)(approx + m);
        result = lo;
        return BitConverter.SingleToInt32Bits(lo) == BitConverter.SingleToInt32Bits(hi);
    }

    // ------------------------------------------------------------------
    // Trigonometric
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>x = k pi/2 + r</c> with <c>|r| &lt;= pi/4</c> (and a little), r as a
    /// double-double, for <c>|x| &lt; <see cref="ReductionLimit"/></c>.
    /// </summary>
    /// <param name="x">The argument.</param>
    /// <param name="rh">The head of r.</param>
    /// <param name="rl">The tail of r.</param>
    /// <param name="quadrant"><c>k mod 4</c>.</param>
    /// <returns>
    /// False when x is out of range or r is too small for the reduction's
    /// absolute error, and the exact tier must decide.
    /// </returns>
    /// <remarks>
    /// Cody and Waite's reduction, carried in double-double. <c>k P1</c> and
    /// <c>k P2</c> are exact (25 + 28 bits), and <c>x - k P1</c> is exact by
    /// Sterbenz's lemma because <c>k P1</c> is within a factor of two of x.
    /// The next two steps are error-free sums and products, so the only
    /// rounding is in collecting the tiny low-order terms, about 2^-104
    /// absolute, plus <c>k</c> times the 2^-168 error of the four-part pi/2.
    /// Against the 2^-40 floor that is under 2^-60 relative.
    /// </remarks>
    internal static bool ReduceHalfPi(double x, out double rh, out double rl, out int quadrant)
    {
        rh = 0;
        rl = 0;
        quadrant = 0;
        if (!(Math.Abs(x) < ReductionLimit))
        {
            return false;
        }

        double k = Math.Round(x * TwoOverPi);
        if (k == 0)
        {
            rh = x;
            return true;
        }

        double t = x - (k * Pio2Part1);
        (double s, double e1) = TwoSum(t, -(k * Pio2Part2));
        (double ph, double pl) = TwoProduct(k, Pio2Part3);
        (double s2, double e2) = TwoSum(s, -ph);
        double low = ((e1 + e2) - pl) - (k * Pio2Part4);
        (rh, rl) = FastTwoSum(s2, low);
        quadrant = (int)((long)k & 3);
        return Math.Abs(rh) >= ReducedFloor;
    }

    /// <summary><c>sin r</c> for a reduced <c>r = rh + rl</c>.</summary>
    /// <param name="rh">The head of r.</param>
    /// <param name="rl">The tail of r.</param>
    /// <returns>The approximation.</returns>
    /// <remarks>
    /// Taylor to <c>r^19</c>; the first omitted term is under 2^-80 of r at
    /// <c>|r| = pi/4</c>. The correction <c>rh z S(z) + rl (1 - z/2)</c> is at most
    /// 0.11 of the result and carries a few ulps of its own error, and the
    /// final addition one half ulp; with <c>sin r &gt;= 0.9 r</c> the total is
    /// under 2^-52 relative.
    /// </remarks>
    internal static double Sin(double rh, double rl)
    {
        double z = rh * rh;
        double s = (-1.0 / 121645100408832000);
        s = (s * z) + (1.0 / 355687428096000);
        s = (s * z) + (-1.0 / 1307674368000);
        s = (s * z) + (1.0 / 6227020800);
        s = (s * z) + (-1.0 / 39916800);
        s = (s * z) + (1.0 / 362880);
        s = (s * z) + (-1.0 / 5040);
        s = (s * z) + (1.0 / 120);
        s = (s * z) + (-1.0 / 6);
        return rh + ((rh * z * s) + (rl * (1 - (0.5 * z))));
    }

    /// <summary><c>cos r</c> for a reduced <c>r = rh + rl</c>.</summary>
    /// <param name="rh">The head of r.</param>
    /// <param name="rl">The tail of r.</param>
    /// <returns>The approximation.</returns>
    /// <remarks>
    /// Taylor to <c>r^20</c>. <c>w = 1 - z/2</c> is rounded once and its
    /// rounding error recovered exactly (<c>(1 - w) - z/2</c>, both terms within
    /// a factor of two of each other); the rounding of <c>z = rh^2</c> itself
    /// costs 2^-54.7 absolute and the final addition half an ulp. With
    /// <c>cos r &gt;= 0.7</c> that is under 2^-51.5 relative.
    /// </remarks>
    internal static double Cos(double rh, double rl)
    {
        double z = rh * rh;
        double c = (1.0 / 2432902008176640000);
        c = (c * z) + (-1.0 / 6402373705728000);
        c = (c * z) + (1.0 / 20922789888000);
        c = (c * z) + (-1.0 / 87178291200);
        c = (c * z) + (1.0 / 479001600);
        c = (c * z) + (-1.0 / 3628800);
        c = (c * z) + (1.0 / 40320);
        c = (c * z) + (-1.0 / 720);
        c = (c * z) + (1.0 / 24);
        double hz = 0.5 * z;
        double w = 1 - hz;
        return w + (((1 - w) - hz) + ((z * z * c) - (rh * rl)));
    }

    /// <summary>
    /// <c>atan t</c> for <c>0 &lt;= t &lt;= 1</c>, by the nearest breakpoint
    /// <c>c = j/8</c>: <c>atan t = atan c + atan((t - c)/(1 + t c))</c>.
    /// </summary>
    /// <param name="t">The argument.</param>
    /// <returns>The approximation.</returns>
    /// <remarks>
    /// <c>t - c</c> is exact (Sterbenz: t is within 1/16 of c and c is at least
    /// 1/8), and the divisor carries two roundings, so u is within 2^-51.4
    /// relative. <c>|u| &lt;= 1/16</c> and the Taylor series to <c>u^15</c>
    /// leaves 2^-64 out. The sum with the double-double <c>atan c</c>, at least
    /// 0.124 against a correction of at most 0.0625, lands under 2^-52
    /// relative; an error in t itself passes through at most unchanged, since
    /// <c>t atan'(t) / atan(t) &lt;= 1</c>.
    /// </remarks>
    internal static double Atan(double t)
    {
        int j = (int)Math.Round(t * 8);
        if (j == 0)
        {
            return AtanSmall(t);
        }

        double c = j * 0.125;
        double u = (t - c) / (1 + (t * c));
        ReadOnlySpan<double> table = AtanBreakpoints;
        return table[(2 * j) - 2] + (table[(2 * j) - 1] + AtanSmall(u));
    }

    private static double AtanSmall(double u)
    {
        double z = u * u;
        double p = (-1.0 / 15);
        p = (p * z) + (1.0 / 13);
        p = (p * z) + (-1.0 / 11);
        p = (p * z) + (1.0 / 9);
        p = (p * z) + (-1.0 / 7);
        p = (p * z) + (1.0 / 5);
        p = (p * z) + (-1.0 / 3);
        return u + (u * z * p);
    }

    /// <summary>
    /// <c>atan2(y, x)</c> for finite arguments, not both zero, whose ratio
    /// stays well inside the double range.
    /// </summary>
    /// <param name="y">The ordinate.</param>
    /// <param name="x">The abscissa.</param>
    /// <returns>The approximation.</returns>
    /// <remarks>
    /// The smaller magnitude over the larger is one rounding (2^-53), then
    /// <see cref="Atan"/>. Folding by <c>pi/2 - theta</c> or <c>pi - theta</c>
    /// never cancels, since theta is at most pi/4 or pi/2 respectively, and
    /// the double-double constants keep those steps to a rounding each. Under
    /// 2^-50.5 relative in all.
    /// </remarks>
    internal static double Atan2(double y, double x)
    {
        double a = Math.Abs(y);
        double b = Math.Abs(x);
        double theta = a <= b
            ? Atan(a / b)
            : HalfPiHi + (HalfPiLo - Atan(b / a));

        if (x < 0)
        {
            theta = PiHi + (PiLo - theta);
        }

        return y < 0 ? -theta : theta;
    }

    // ------------------------------------------------------------------
    // Power
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>ln x</c> as a double-double, for finite positive x.
    /// </summary>
    /// <param name="x">The argument.</param>
    /// <returns>Head and tail, within about 2^-57 relative.</returns>
    /// <remarks>
    /// <para>
    /// <c>x = 2^k m</c> with m in <c>[sqrt(1/2), sqrt 2)</c>, and
    /// <c>ln m = 2 atanh s</c> for <c>s = (m - 1)/(m + 1)</c>, <c>|s| &lt;= 0.1716</c>.
    /// </para>
    /// <para>
    /// <c>m - 1</c> is exact (Sterbenz) and <c>m + 1</c> is kept exactly as a
    /// two-sum, so s is formed as a double-double with one Newton correction:
    /// under 2^-104 relative. <c>ln m = 2s + 2s T</c> with
    /// <c>T = s^2/3 + s^4/5 + ...</c> to <c>s^22</c> (the omitted rest is under
    /// 2^-60 of 1): T is at most 0.0108 and is evaluated in plain double from
    /// the head of s, a few ulps of error that reach ln m scaled by 0.0108,
    /// about 2^-58 relative. <c>k ln 2</c> adds an exact head (40-bit
    /// <c>Ln2Hi</c>) and a tail within 2^-82 absolute; for <c>k != 0</c> there
    /// is no cancellation, since <c>|ln m| &lt;= 0.347 &lt; ln 2</c>.
    /// </para>
    /// </remarks>
    internal static (double Hi, double Lo) Log(double x)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int k = (int)(bits >> 52) - 1023;
        if (k == -1023)
        {
            // Subnormal: scale by 2^54 exactly and account for it.
            bits = BitConverter.DoubleToInt64Bits(x * 18014398509481984.0);
            k = (int)(bits >> 52) - 1023 - 54;
        }

        double m = BitConverter.Int64BitsToDouble((bits & 0xFFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > Sqrt2)
        {
            m *= 0.5;
            k++;
        }

        double f = m - 1;
        (double dh, double dl) = TwoSum(m, 1.0);
        double sh = f / dh;
        (double ph, double pl) = TwoProduct(sh, dh);
        double sl = (((f - ph) - pl) - (sh * dl)) / dh;

        double z = sh * sh;
        double t = (1.0 / 23);
        t = (t * z) + (1.0 / 21);
        t = (t * z) + (1.0 / 19);
        t = (t * z) + (1.0 / 17);
        t = (t * z) + (1.0 / 15);
        t = (t * z) + (1.0 / 13);
        t = (t * z) + (1.0 / 11);
        t = (t * z) + (1.0 / 9);
        t = (t * z) + (1.0 / 7);
        t = (t * z) + (1.0 / 5);
        t = (t * z) + (1.0 / 3);
        t *= z;

        double lnHi = 2 * sh;
        double lnLo = (2 * sl) + (2 * sh * t);
        if (k == 0)
        {
            return FastTwoSum(lnHi, lnLo);
        }

        (double hi, double e) = TwoSum(k * Ln2Hi, lnHi);
        return FastTwoSum(hi, e + ((k * Ln2Lo) + lnLo));
    }

    /// <summary>
    /// <c>e^(zh + zl)</c> for <c>|zh| &lt;= 707</c> and <c>|zl|</c> a few ulps of zh.
    /// </summary>
    /// <param name="zh">The head of the argument.</param>
    /// <param name="zl">The tail of the argument.</param>
    /// <returns>The approximation.</returns>
    /// <remarks>
    /// <c>z = K ln 2 + r</c>: <c>K ln2Hi</c> is exact (11 + 40 bits) and
    /// <c>zh - K ln2Hi</c> exact by Sterbenz, so r carries one rounding of
    /// 2^-54.5 absolute plus the 2^-83 of <c>K ln2Lo</c>. The Taylor series of
    /// <c>e^r</c> to <c>r^13</c> leaves 2^-57.5 out at <c>|r| = ln2/2</c>, and
    /// its Horner evaluation adds a few ulps; scaling by <c>2^K</c> is exact
    /// while the result is normal, which the float callers' range of
    /// <c>[-104.5, 89.5]</c> keeps it. Under 2^-51.5 relative, before whatever
    /// error the argument itself carries, which passes through as an absolute
    /// error in z and so a relative one in the result.
    /// </remarks>
    internal static double Exp(double zh, double zl)
    {
        double k = Math.Round(zh * InvLn2);
        double r = ((zh - (k * Ln2Hi)) - (k * Ln2Lo)) + zl;
        double p = (1.0 / 6227020800);
        p = (p * r) + (1.0 / 479001600);
        p = (p * r) + (1.0 / 39916800);
        p = (p * r) + (1.0 / 3628800);
        p = (p * r) + (1.0 / 362880);
        p = (p * r) + (1.0 / 40320);
        p = (p * r) + (1.0 / 5040);
        p = (p * r) + (1.0 / 720);
        p = (p * r) + (1.0 / 120);
        p = (p * r) + (1.0 / 24);
        p = (p * r) + (1.0 / 6);
        p = (p * r) + (1.0 / 2);
        p = (p * r) + 1.0;
        p = (p * r) + 1.0;

        // 2^k built from its bits: exact, and cheaper than ScaleB's range
        // handling. k stays within [-1021, 1023] for every caller's range,
        // so 2^k is a normal double.
        return p * BitConverter.Int64BitsToDouble((long)(k + 1023) << 52);
    }

    /// <summary>
    /// <c>y ln x</c> as a double-double, for finite positive x and finite y.
    /// </summary>
    /// <param name="x">The base.</param>
    /// <param name="y">The exponent.</param>
    /// <returns>
    /// Head and tail. The tail is meaningful only while the head is within the
    /// double range's logarithm; callers decide overflow and underflow from
    /// the head first.
    /// </returns>
    /// <remarks>
    /// <c>ln x</c> within 2^-57 relative and one exact two-product: the
    /// product's absolute error is <c>|y ln x| 2^-57</c>, which for the float
    /// range's <c>|y ln x| &lt;= 104.5</c> is 2^-50.3 and becomes that relative
    /// error in <c>x^y</c>.
    /// </remarks>
    internal static (double Hi, double Lo) LogTimes(double x, double y)
    {
        (double lh, double ll) = Log(x);
        double zh = y * lh;
        if (!(Math.Abs(zh) < 1e4))
        {
            // Far outside every range the callers round to, and |y| may be
            // past what Veltkamp's split can take without overflowing. Below
            // this, |ln x| >= 2^-53 for x != 1 keeps |y| under 2^67.
            return (zh, 0);
        }

        (double ph, double pl) = TwoProduct(y, lh);
        return (ph, pl + (y * ll));
    }
}
