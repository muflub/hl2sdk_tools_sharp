//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Numerics;

namespace SourceSharp.MapFormats.Numerics;

/// <summary>
/// The format a real result is rounded to, with round-to-nearest-even.
/// </summary>
internal enum RoundingTarget
{
    /// <summary>IEEE binary32.</summary>
    Single,

    /// <summary>IEEE binary64.</summary>
    Double,

    /// <summary>
    /// Rounded to binary64 first and that double then to binary32: the bits of
    /// <c>(float)f(x)</c> for a correctly rounded double <c>f</c>. Differs from
    /// <see cref="Single"/> only when the real result lies within half a double
    /// ulp of a float rounding boundary.
    /// </summary>
    DoubleThenSingle,
}

/// <summary>
/// The exact tier of <see cref="DetMath"/> and <see cref="DetMathF"/>:
/// correctly rounded results by arbitrary-precision interval arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Method.</b> Each function evaluates its real result as a
/// <see cref="FixedInterval"/> at some working precision, rounds both ends of
/// the interval to the target format, and returns the rounding if the two
/// ends agree. Rounding is monotone, so when both ends round to the same value
/// so does everything between them, including the true result. When they
/// disagree the result lies too close to a rounding boundary to decide at that
/// precision, and the evaluation is repeated with twice as many bits (Ziv's
/// strategy). A result that IS a boundary (a representable value or a midpoint
/// between two) would never be decided that way, so the functions whose
/// results can be exact dyadic numbers detect those inputs first and round the
/// exact value: <c>pow</c> with an exact power, and the trivial arguments the
/// public entry points take out beforehand.
/// </para>
/// <para>
/// <b>Speed.</b> Tens of microseconds per call. The public functions only come
/// here when their double-precision fast path cannot decide the rounding (about
/// one call in a million for a float result), for a double result, or for an
/// argument the fast path does not cover.
/// </para>
/// <para>
/// <b>Algorithms.</b> Textbook series, chosen for bounds that are easy to state
/// rather than for speed: <c>exp</c> by its Taylor series after reduction by
/// <c>ln 2</c>; <c>log</c> by the <c>atanh</c> series of
/// <c>(m - 1)/(m + 1)</c>; <c>sin</c> and <c>cos</c> by their Taylor series
/// after exact reduction by <c>pi/2</c> (the argument is a dyadic rational, so
/// the reduction only needs enough bits of pi); <c>atan</c> by its Taylor
/// series after two half-angle steps. <c>pi</c> and <c>ln 2</c> come from
/// stored 2048-bit expansions, and from Machin's formula and the
/// <c>atanh(1/3)</c> series past that; a fact recomputes the stored bits.
/// </para>
/// <para>
/// Nothing here is shared between calls: no cache of constants, no pooled
/// buffers. Two compiles can call it concurrently.
/// </para>
/// </remarks>
internal static class ExactMath
{
    /// <summary>The first working precision, in fractional bits.</summary>
    private const int FirstPrecision = 128;

    /// <summary>
    /// The precision at which the Ziv loop gives up. The hardest-to-round
    /// double arguments known for these functions need on the order of a
    /// hundred bits past the result's own 53; 32768 bits is two orders of
    /// magnitude of headroom, reached only by a bug.
    /// </summary>
    private const int LastPrecision = 1 << 15;

    /// <summary>The number of fractional bits in <see cref="PiBits"/>.</summary>
    private const int PiBitsScale = 2046;

    /// <summary>The number of fractional bits in <see cref="Ln2Bits"/>.</summary>
    private const int Ln2BitsScale = 2048;

    /// <summary>Below this magnitude an argument takes the short bracket of its first series terms.</summary>
    private const double TinyArgument = 1.0 / (1 << 20);

    // ------------------------------------------------------------------
    // Rounding
    // ------------------------------------------------------------------

    /// <summary>
    /// Rounds the dyadic rational <c>n * 2^e</c> to <paramref name="target"/>
    /// with round-to-nearest-even, including subnormal results and overflow to
    /// infinity.
    /// </summary>
    /// <param name="n">The integer part.</param>
    /// <param name="e">The power of two.</param>
    /// <param name="target">The format.</param>
    /// <returns>
    /// The rounded value as a double (a float target's value is exactly
    /// representable as one). Zero rounds to <c>+0</c>.
    /// </returns>
    internal static double RoundDyadic(BigInteger n, long e, RoundingTarget target)
    {
        if (target == RoundingTarget.DoubleThenSingle)
        {
            return (float)RoundDyadic(n, e, RoundingTarget.Double);
        }

        if (n.IsZero)
        {
            return 0.0;
        }

        bool negative = n.Sign < 0;
        BigInteger a = BigInteger.Abs(n);
        (int precision, int minExponent, int maxExponent) = target == RoundingTarget.Single
            ? (24, -126, 127)
            : (53, -1022, 1023);

        long top = (long)a.GetBitLength() - 1 + e;
        double magnitude;
        if (top > maxExponent)
        {
            // At or above 2^(emax+1): past the largest finite value and past
            // the halfway point to the next power, so infinity.
            magnitude = double.PositiveInfinity;
        }
        else if (top < minExponent - precision)
        {
            // Below 2^(emin-p+1)... strictly below half the least subnormal.
            magnitude = 0.0;
        }
        else
        {
            long quantum = Math.Max(top - (precision - 1), minExponent - (precision - 1));
            long shift = quantum - e;
            BigInteger m;
            if (shift <= 0)
            {
                m = a << (int)-shift;
            }
            else
            {
                m = a >> (int)shift;
                BigInteger rest = a - (m << (int)shift);
                int c = rest.CompareTo(BigInteger.One << (int)(shift - 1));
                if (c > 0 || (c == 0 && !m.IsEven))
                {
                    m += BigInteger.One;
                }
            }

            // m <= 2^precision, so the conversion is exact, and ScaleB of an
            // exactly representable result is exact.
            magnitude = Math.ScaleB((double)m, (int)quantum);
            if (target == RoundingTarget.Single && magnitude > float.MaxValue)
            {
                magnitude = double.PositiveInfinity;
            }
        }

        return negative ? -magnitude : magnitude;
    }

    /// <summary>
    /// Rounds a real known to lie in <c>[lo, hi] * 2^e</c>, if both ends round
    /// to the same value.
    /// </summary>
    /// <param name="lo">The lower end.</param>
    /// <param name="hi">The upper end.</param>
    /// <param name="e">The power of two.</param>
    /// <param name="target">The format.</param>
    /// <param name="result">The rounding, when decided.</param>
    /// <returns>Whether the rounding is decided.</returns>
    internal static bool TryRound(BigInteger lo, BigInteger hi, long e, RoundingTarget target, out double result)
    {
        double a = RoundDyadic(lo, e, target);
        double b = RoundDyadic(hi, e, target);
        result = a;

        // Compared as bits so an interval straddling zero, whose ends round
        // to -0 and +0, is not taken as decided.
        return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b)
            && !(lo.Sign < 0 && hi.Sign > 0);
    }

    /// <summary>Rounds the positive rational <c>num / den * 2^e</c> exactly.</summary>
    /// <param name="num">The numerator; positive.</param>
    /// <param name="den">The denominator; positive.</param>
    /// <param name="e">The power of two.</param>
    /// <param name="target">The format.</param>
    /// <returns>The correctly rounded value.</returns>
    internal static double RoundRational(BigInteger num, BigInteger den, long e, RoundingTarget target)
    {
        // Take the quotient to at least 57 bits, past both formats'
        // precision plus a rounding and a sticky bit, and fold a nonzero
        // remainder into a sticky last bit: the real value lies strictly
        // inside (q, q + 1), which contains no rounding boundary at this many
        // bits, and 2q + 1 is a point inside it.
        int k = 60 + (int)(den.GetBitLength() - num.GetBitLength());
        BigInteger n = k >= 0 ? num << k : num;
        BigInteger d = k >= 0 ? den : den << -k;
        BigInteger q = BigInteger.DivRem(n, d, out BigInteger rest);
        return RoundDyadic((q << 1) + (rest.IsZero ? 0 : 1), e - k - 1, target);
    }

    // ------------------------------------------------------------------
    // Functions
    // ------------------------------------------------------------------

    /// <summary>Correctly rounded <c>sin(x)</c>, for finite nonzero <paramref name="x"/>.</summary>
    /// <param name="x">The argument.</param>
    /// <param name="target">The format.</param>
    /// <returns>The rounded result.</returns>
    internal static double Sin(double x, RoundingTarget target)
    {
        if (Math.Abs(x) < TinyArgument)
        {
            // x - x^3/6 < sin x < x for x > 0; x^3/4 keeps the bracket dyadic.
            (BigInteger m, int e) = Decompose(x);
            BigInteger lin = m << (2 - (2 * e));
            BigInteger cube = m * m * m;
            (BigInteger lo, BigInteger hi) = m.Sign > 0 ? (lin - cube, lin) : (lin, lin - cube);
            if (TryRound(lo, hi, (3L * e) - 2, target, out double r))
            {
                return r;
            }
        }

        for (int p = FirstPrecision; p <= LastPrecision; p *= 2)
        {
            (FixedInterval r, int quadrant) = ReduceHalfPi(x, p);
            FixedInterval v = quadrant switch
            {
                0 => SinSmall(r, p),
                1 => CosSmall(r, p),
                2 => -SinSmall(r, p),
                _ => -CosSmall(r, p),
            };

            if (TryRound(v.Lo, v.Hi, -p, target, out double result))
            {
                return result;
            }
        }

        throw NotConverged(nameof(Sin), x);
    }

    /// <summary>Correctly rounded <c>cos(x)</c>, for finite nonzero <paramref name="x"/>.</summary>
    /// <param name="x">The argument.</param>
    /// <param name="target">The format.</param>
    /// <returns>The rounded result.</returns>
    internal static double Cos(double x, RoundingTarget target)
    {
        if (Math.Abs(x) < TinyArgument)
        {
            // 1 - x^2/2 <= cos x < 1.
            (BigInteger m, int e) = Decompose(x);
            BigInteger one = BigInteger.One << (1 - (2 * e));
            if (TryRound(one - (m * m), one, (2L * e) - 1, target, out double r))
            {
                return r;
            }
        }

        for (int p = FirstPrecision; p <= LastPrecision; p *= 2)
        {
            (FixedInterval r, int quadrant) = ReduceHalfPi(x, p);
            FixedInterval v = quadrant switch
            {
                0 => CosSmall(r, p),
                1 => -SinSmall(r, p),
                2 => -CosSmall(r, p),
                _ => SinSmall(r, p),
            };

            if (TryRound(v.Lo, v.Hi, -p, target, out double result))
            {
                return result;
            }
        }

        throw NotConverged(nameof(Cos), x);
    }

    /// <summary>Correctly rounded <c>tan(x)</c>, for finite nonzero <paramref name="x"/>.</summary>
    /// <param name="x">The argument.</param>
    /// <param name="target">The format.</param>
    /// <returns>The rounded result.</returns>
    internal static double Tan(double x, RoundingTarget target)
    {
        if (Math.Abs(x) < TinyArgument)
        {
            // x < tan x < x + x^3/2 for 0 < x < 2^-20.
            (BigInteger m, int e) = Decompose(x);
            BigInteger lin = m << (1 - (2 * e));
            BigInteger cube = m * m * m;
            (BigInteger lo, BigInteger hi) = m.Sign > 0 ? (lin, lin + cube) : (lin + cube, lin);
            if (TryRound(lo, hi, (3L * e) - 1, target, out double r))
            {
                return r;
            }
        }

        for (int p = FirstPrecision; p <= LastPrecision; p *= 2)
        {
            (FixedInterval r, int quadrant) = ReduceHalfPi(x, p);
            FixedInterval s = SinSmall(r, p);
            FixedInterval c = CosSmall(r, p);

            // tan(r + k pi/2) is tan r for even k and -cot r for odd k. A
            // divisor that still contains zero means r is too close to zero
            // to resolve at this precision; the next pass has twice the bits.
            FixedInterval divisor = (quadrant & 1) == 0 ? c : s;
            if (divisor.ContainsZero)
            {
                continue;
            }

            FixedInterval v = (quadrant & 1) == 0 ? s.Div(c, p) : -c.Div(s, p);
            if (TryRound(v.Lo, v.Hi, -p, target, out double result))
            {
                return result;
            }
        }

        throw NotConverged(nameof(Tan), x);
    }

    /// <summary>Correctly rounded <c>asin(x)</c>, for nonzero <c>|x| &lt;= 1</c>.</summary>
    /// <param name="x">The argument.</param>
    /// <param name="target">The format.</param>
    /// <returns>The rounded result.</returns>
    internal static double Asin(double x, RoundingTarget target)
    {
        (BigInteger m, int e) = Decompose(Math.Abs(x));
        if (Math.Abs(x) < TinyArgument)
        {
            // x < asin x < x + x^3/4 for 0 < x < 2^-20.
            BigInteger lin = m << (2 - (2 * e));
            BigInteger cube = m * m * m;
            (BigInteger lo, BigInteger hi) = x > 0 ? (lin, lin + cube) : (-(lin + cube), -lin);
            if (TryRound(lo, hi, (3L * e) - 2, target, out double r))
            {
                return r;
            }
        }

        for (int p = Math.Max(FirstPrecision, 64 - e); p <= LastPrecision; p *= 2)
        {
            (FixedInterval a, FixedInterval b) = SineAndCosineOfArcsine(m, e, p);
            FixedInterval? theta = Atan2NonNegative(a, b, p);
            if (theta is null)
            {
                continue;
            }

            FixedInterval v = x < 0 ? -theta.Value : theta.Value;
            if (TryRound(v.Lo, v.Hi, -p, target, out double result))
            {
                return result;
            }
        }

        throw NotConverged(nameof(Asin), x);
    }

    /// <summary>Correctly rounded <c>acos(x)</c>, for <c>|x| &lt;= 1</c> other than 1.</summary>
    /// <param name="x">The argument.</param>
    /// <param name="target">The format.</param>
    /// <returns>The rounded result.</returns>
    internal static double Acos(double x, RoundingTarget target)
    {
        if (x == 0)
        {
            return RoundHalfPi(target);
        }

        (BigInteger m, int e) = Decompose(Math.Abs(x));
        for (int p = Math.Max(FirstPrecision, 64 - e); p <= LastPrecision; p *= 2)
        {
            (FixedInterval a, FixedInterval b) = SineAndCosineOfArcsine(m, e, p);

            // acos |x| = atan2(sqrt(1 - x^2), |x|), and acos(-x) = pi - acos x.
            FixedInterval? theta = Atan2NonNegative(b, a, p);
            if (theta is null)
            {
                continue;
            }

            FixedInterval v = x < 0 ? Pi(p) - theta.Value : theta.Value;
            if (TryRound(v.Lo, v.Hi, -p, target, out double result))
            {
                return result;
            }
        }

        throw NotConverged(nameof(Acos), x);
    }

    /// <summary>
    /// Correctly rounded <c>atan2(y, x)</c>, for finite arguments with
    /// <paramref name="y"/> nonzero or <paramref name="x"/> negative.
    /// </summary>
    /// <param name="y">The ordinate.</param>
    /// <param name="x">The abscissa.</param>
    /// <param name="target">The format.</param>
    /// <returns>The rounded result, in <c>[-pi, pi]</c>.</returns>
    internal static double Atan2(double y, double x, RoundingTarget target)
    {
        // Both magnitudes as integers over a common power of two, so their
        // ratio is an exact rational.
        (BigInteger my, int ey) = y == 0 ? (BigInteger.Zero, 0) : Decompose(Math.Abs(y));
        (BigInteger mx, int ex) = x == 0 ? (BigInteger.Zero, 0) : Decompose(Math.Abs(x));
        int common = Math.Min(y == 0 ? ex : ey, x == 0 ? ey : ex);
        BigInteger a = my << ((y == 0 ? common : ey) - common);
        BigInteger b = mx << ((x == 0 ? common : ex) - common);

        // A tiny ratio gives a tiny angle, which needs that many more
        // fractional bits before its own significant bits start.
        int start = FirstPrecision;
        if (y != 0 && x != 0)
        {
            start += Math.Max(0, Math.ILogB(x) - Math.ILogB(y));
        }

        for (int p = start; p <= LastPrecision; p *= 2)
        {
            FixedInterval theta;
            if (a <= b)
            {
                FixedInterval t = new(FixedInterval.FloorDiv(a << p, b), FixedInterval.CeilDiv(a << p, b));
                theta = AtanUnit(t, p);
            }
            else
            {
                FixedInterval t = new(FixedInterval.FloorDiv(b << p, a), FixedInterval.CeilDiv(b << p, a));
                theta = HalfPi(p) - AtanUnit(t, p);
            }

            if (x < 0)
            {
                theta = Pi(p) - theta;
            }

            if (y < 0)
            {
                theta = -theta;
            }

            if (TryRound(theta.Lo, theta.Hi, -p, target, out double result))
            {
                return result;
            }
        }

        throw NotConverged(nameof(Atan2), y);
    }

    /// <summary>Correctly rounded <c>ln(x)</c>, for finite positive <paramref name="x"/> other than 1.</summary>
    /// <param name="x">The argument.</param>
    /// <param name="target">The format.</param>
    /// <returns>The rounded result.</returns>
    internal static double Log(double x, RoundingTarget target)
    {
        if (Math.Abs(x - 1) < TinyArgument)
        {
            // u = x - 1 is exact here. ln(1 + u) = u - u^2/2 + t with
            // |t| <= |u|^3/3 / (1 - |u|) < |u|^3.
            // Everything over 2^(3eu - 1): u = mu 2^eu = (mu << (1 - 2eu)) 2^(3eu-1),
            // u^2/2 = mu^2 2^(2eu - 1) = (mu^2 << -eu) 2^(3eu - 1),
            // |u|^3 = |mu|^3 2^(3eu) = (|mu|^3 << 1) 2^(3eu - 1).
            (BigInteger mu, int eu) = Decompose(x - 1);
            BigInteger u = mu << (1 - (2 * eu));
            BigInteger half = (mu * mu) << (-eu);
            BigInteger cube = BigInteger.Abs(mu * mu * mu) << 1;
            BigInteger centre = u - half;
            if (TryRound(centre - cube, centre + cube, (3L * eu) - 1, target, out double r))
            {
                return r;
            }
        }

        (BigInteger m, int e) = Decompose(x);
        for (int p = FirstPrecision; p <= LastPrecision; p *= 2)
        {
            FixedInterval v = Ln(m, e, p);
            if (TryRound(v.Lo, v.Hi, -p, target, out double result))
            {
                return result;
            }
        }

        throw NotConverged(nameof(Log), x);
    }

    /// <summary>
    /// Correctly rounded <c>x^y</c>, for finite positive <paramref name="x"/>
    /// other than 1 and finite nonzero <paramref name="y"/>.
    /// </summary>
    /// <param name="x">The base.</param>
    /// <param name="y">The exponent.</param>
    /// <param name="target">The format.</param>
    /// <returns>The rounded result, which may be <c>+0</c> or <c>+infinity</c>.</returns>
    internal static double Pow(double x, double y, RoundingTarget target)
    {
        (BigInteger mx, int ex) = Decompose(x);
        (BigInteger my, int ey) = Decompose(Math.Abs(y));
        bool negativeY = y < 0;

        if (TryExactPow(mx, ex, my, ey, negativeY, target, out double exact))
        {
            return exact;
        }

        // |y| < 2^yTop. ln x is computed with that many more bits so the
        // product with y still has p fractional bits.
        int yTop = ey + (int)my.GetBitLength();
        BigInteger signedMy = negativeY ? -my : my;

        for (int p = FirstPrecision; p <= LastPrecision; p *= 2)
        {
            int q = p + Math.Max(0, yTop) + 16;
            FixedInterval lnx = Ln(mx, ex, q);

            // z = y ln x = (lnx 2^-q) (my 2^ey), at scale p.
            FixedInterval z = lnx.Times(signedMy).ShiftDown(q - p - ey);

            // Past the ends of the double range the result is +infinity or
            // +0 whatever the target: 2^1025 overflows and 2^-1077 is under
            // half the least subnormal. ln 2 * 1025 = 710.5 and
            // ln 2 * 1077 = 746.5; the margins cover the approximation of z.
            double zLo = Approximate(z.Lo, p);
            double zHi = Approximate(z.Hi, p);
            if (zLo > 711)
            {
                return double.PositiveInfinity;
            }

            if (zHi < -747)
            {
                return 0.0;
            }

            // z = k ln2 + r with |r| <= ln2/2 (and a little), so e^r is near
            // 1 and exp's series converges quickly; x^y = 2^k e^r.
            long k = (long)Math.Round(((zLo + zHi) / 2) * 1.4426950408889634);
            FixedInterval kln2 = Ln2(p + 16).Times(k).ShiftDown(16);
            FixedInterval er = ExpSmall(z - kln2, p);
            if (TryRound(er.Lo, er.Hi, k - p, target, out double result))
            {
                return result;
            }
        }

        throw NotConverged(nameof(Pow), x);
    }

    // ------------------------------------------------------------------
    // Exact powers
    // ------------------------------------------------------------------

    /// <summary>
    /// Rounds <c>x^y</c> exactly when it is a dyadic rational (or a rational
    /// with a small power in its denominator), which are the only cases whose
    /// result can sit exactly on a rounding boundary.
    /// </summary>
    /// <param name="mx">The odd integer part of x.</param>
    /// <param name="ex">x's power of two.</param>
    /// <param name="my">The odd integer part of |y|.</param>
    /// <param name="ey">|y|'s power of two.</param>
    /// <param name="negativeY">Whether y is negative.</param>
    /// <param name="target">The format.</param>
    /// <param name="result">The rounded result, when handled.</param>
    /// <returns>
    /// Whether the power was exact and has been rounded. False means
    /// <c>x^y</c> is irrational, or a rational with at least 101 significant
    /// bits, and so lies strictly between rounding boundaries.
    /// </returns>
    /// <remarks>
    /// <para>
    /// With <c>x = mx 2^ex</c> (mx odd) and <c>y = my 2^ey</c> (my odd):
    /// </para>
    /// <list type="bullet">
    /// <item>A fractional y (<c>ey &lt; 0</c>, denominator <c>2^g</c>) gives a
    /// rational power only when x is a perfect <c>2^g</c>-th power: mx is,
    /// and ex is divisible by <c>2^g</c>. mx is at most 53 bits and an odd
    /// root above 1 is at least 3, so for <c>2^g &gt; 32</c> only
    /// <c>mx = 1</c> qualifies. The root replaces x and y becomes the integer
    /// my.</item>
    /// <item>A power of two <c>2^(ex y)</c> is exact when <c>ex y</c> is an
    /// integer.</item>
    /// <item>An integer power of an odd <c>mx &gt;= 3</c> is exact, and is
    /// computed when <c>|y| &lt;= 64</c>. Above that <c>mx^|y|</c> has at least
    /// 101 bits (so <c>x^y</c> is not within 54 bits of a boundary) or, for a
    /// negative y, is not dyadic at all; neither can be a boundary, and the
    /// general path decides it.</item>
    /// </list>
    /// </remarks>
    internal static bool TryExactPow(
        BigInteger mx, int ex, BigInteger my, int ey, bool negativeY, RoundingTarget target, out double result)
    {
        result = 0;
        if (ey < 0)
        {
            int g = -ey;
            if (g > 30)
            {
                // Only mx = 1 could qualify, and then ex (at most 1075 in
                // magnitude, and nonzero since x != 1) would have to be
                // divisible by 2^g.
                return false;
            }

            int root = 1 << g;
            if (ex % root != 0)
            {
                return false;
            }

            if (!mx.IsOne)
            {
                if (g > 5)
                {
                    return false;
                }

                BigInteger r = IntegerRoot(mx, root);
                if (BigInteger.Pow(r, root) != mx)
                {
                    return false;
                }

                mx = r;
            }

            ex /= root;
            ey = 0;
        }

        // y is now the integer my 2^ey (ey >= 0).
        BigInteger yInt = my << ey;
        if (negativeY)
        {
            yInt = -yInt;
        }

        if (mx.IsOne)
        {
            BigInteger exponent = yInt * ex;
            long clamped = exponent > 4000 ? 4000 : exponent < -4000 ? -4000 : (long)exponent;
            result = RoundDyadic(BigInteger.One, clamped, target);
            return true;
        }

        if (BigInteger.Abs(yInt) > 64)
        {
            return false;
        }

        int n = (int)yInt;
        BigInteger power = BigInteger.Pow(mx, Math.Abs(n));
        result = n > 0
            ? RoundDyadic(power, (long)ex * n, target)
            : RoundRational(BigInteger.One, power, (long)ex * n, target);
        return true;
    }

    /// <summary><c>floor(n^(1/k))</c> by bisection.</summary>
    /// <param name="n">A positive integer of at most 53 bits.</param>
    /// <param name="k">The root.</param>
    /// <returns>The integer root.</returns>
    private static BigInteger IntegerRoot(BigInteger n, int k)
    {
        BigInteger lo = BigInteger.One;
        BigInteger hi = (BigInteger.One << (int)((n.GetBitLength() / k) + 1)) + 1;
        while (hi - lo > 1)
        {
            BigInteger mid = (lo + hi) >> 1;
            if (BigInteger.Pow(mid, k) <= n)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // ------------------------------------------------------------------
    // Constants
    // ------------------------------------------------------------------

    /// <summary><c>pi</c> at scale <paramref name="p"/>.</summary>
    /// <param name="p">The scale.</param>
    /// <returns>An interval one unit wide.</returns>
    internal static FixedInterval Pi(int p)
    {
        if (p <= PiBitsScale)
        {
            BigInteger lo = FixedInterval.FloorShift(new BigInteger(PiBits, isUnsigned: true, isBigEndian: true), PiBitsScale - p);
            return new(lo, lo + 1);
        }

        return PiBySeries(p);
    }

    /// <summary><c>pi / 2</c> at scale <paramref name="p"/>.</summary>
    /// <param name="p">The scale.</param>
    /// <returns>The interval.</returns>
    internal static FixedInterval HalfPi(int p) => Pi(p - 1);

    /// <summary><c>ln 2</c> at scale <paramref name="p"/>.</summary>
    /// <param name="p">The scale.</param>
    /// <returns>An interval one unit wide.</returns>
    internal static FixedInterval Ln2(int p)
    {
        if (p <= Ln2BitsScale)
        {
            BigInteger lo = FixedInterval.FloorShift(new BigInteger(Ln2Bits, isUnsigned: true, isBigEndian: true), Ln2BitsScale - p);
            return new(lo, lo + 1);
        }

        return Ln2BySeries(p);
    }

    /// <summary><c>pi</c> by Machin's formula, <c>16 atan(1/5) - 4 atan(1/239)</c>.</summary>
    /// <param name="p">The scale.</param>
    /// <returns>The interval.</returns>
    internal static FixedInterval PiBySeries(int p)
    {
        const int Guard = 16;
        FixedInterval a = AtanOfReciprocal(5, p + Guard);
        FixedInterval b = AtanOfReciprocal(239, p + Guard);
        return (a.Times(16) - b.Times(4)).ShiftDown(Guard);
    }

    /// <summary><c>ln 2 = 2 atanh(1/3)</c> by its series.</summary>
    /// <param name="p">The scale.</param>
    /// <returns>The interval.</returns>
    internal static FixedInterval Ln2BySeries(int p)
    {
        const int Guard = 16;
        int q = p + Guard;
        BigInteger one = BigInteger.One << q;
        FixedInterval sum = FixedInterval.Exact(BigInteger.Zero);
        BigInteger power = 3;
        for (int k = 0; ; k++)
        {
            // 1 / ((2k + 1) 3^(2k + 1)): each term exact to a unit.
            BigInteger den = power * ((2 * k) + 1);
            FixedInterval term = new(FixedInterval.FloorDiv(one, den), FixedInterval.CeilDiv(one, den));
            if (term.Hi <= 1)
            {
                // The rest is under this term times 1/(1 - 1/9).
                sum = sum.Widen(2);
                break;
            }

            sum += term;
            power *= 9;
        }

        return sum.Times(2).ShiftDown(Guard);
    }

    private static FixedInterval AtanOfReciprocal(int k, int p)
    {
        BigInteger one = BigInteger.One << p;
        BigInteger kk = k * k;
        BigInteger power = k;
        FixedInterval sum = FixedInterval.Exact(BigInteger.Zero);
        for (int j = 0; ; j++)
        {
            BigInteger den = power * ((2 * j) + 1);
            FixedInterval term = new(FixedInterval.FloorDiv(one, den), FixedInterval.CeilDiv(one, den));
            if (term.Hi <= 1)
            {
                // Alternating and decreasing: the rest is under this term.
                sum = sum.Widen(1);
                break;
            }

            sum = (j & 1) == 0 ? sum + term : sum - term;
            power *= kk;
        }

        return sum;
    }

    private static double RoundHalfPi(RoundingTarget target)
    {
        FixedInterval h = HalfPi(FirstPrecision);
        _ = TryRound(h.Lo, h.Hi, -FirstPrecision, target, out double r);
        return r;
    }

    // ------------------------------------------------------------------
    // Series
    // ------------------------------------------------------------------

    /// <summary><c>e^r</c> for <c>|r| &lt;= 1</c>, by its Taylor series.</summary>
    /// <param name="r">The argument, at scale <paramref name="p"/>.</param>
    /// <param name="p">The scale.</param>
    /// <returns>The interval.</returns>
    internal static FixedInterval ExpSmall(FixedInterval r, int p)
    {
        BigInteger one = BigInteger.One << p;
        FixedInterval sum = FixedInterval.Exact(one);
        FixedInterval term = sum;
        for (int n = 1; ; n++)
        {
            term = term.Mul(r, p).DivideBy(n);
            if (term.MaxAbs <= 1)
            {
                // |t(n+i)| <= |t(n)| / 2^i for |r| <= 1, so the rest,
                // this term included, is under two units.
                return sum.Widen(2);
            }

            sum += term;
        }
    }

    /// <summary><c>sin r</c> for <c>|r| &lt;= 1</c>, by its Taylor series.</summary>
    /// <param name="r">The argument, at scale <paramref name="p"/>.</param>
    /// <param name="p">The scale.</param>
    /// <returns>The interval.</returns>
    internal static FixedInterval SinSmall(FixedInterval r, int p)
    {
        FixedInterval r2 = r.Square(p);
        FixedInterval sum = r;
        FixedInterval term = r;
        for (int n = 1; ; n++)
        {
            term = -term.Mul(r2, p).DivideBy(2 * n * ((2 * n) + 1));
            if (term.MaxAbs <= 1)
            {
                // Alternating with decreasing magnitude: the rest is under
                // this term.
                return sum.Widen(1);
            }

            sum += term;
        }
    }

    /// <summary><c>cos r</c> for <c>|r| &lt;= 1</c>, by its Taylor series.</summary>
    /// <param name="r">The argument, at scale <paramref name="p"/>.</param>
    /// <param name="p">The scale.</param>
    /// <returns>The interval.</returns>
    internal static FixedInterval CosSmall(FixedInterval r, int p)
    {
        FixedInterval r2 = r.Square(p);
        FixedInterval sum = FixedInterval.Exact(BigInteger.One << p);
        FixedInterval term = sum;
        for (int n = 1; ; n++)
        {
            term = -term.Mul(r2, p).DivideBy(((2 * n) - 1) * 2 * n);
            if (term.MaxAbs <= 1)
            {
                return sum.Widen(1);
            }

            sum += term;
        }
    }

    /// <summary>
    /// <c>atan t</c> for <c>0 &lt;= t &lt;= 2</c>: two half-angle steps
    /// <c>t / (1 + sqrt(1 + t^2))</c> bring t under <c>tan(pi/8)/2</c>, then
    /// the Taylor series.
    /// </summary>
    /// <param name="t">The argument, at scale <paramref name="p"/>.</param>
    /// <param name="p">The scale.</param>
    /// <returns>The interval.</returns>
    internal static FixedInterval AtanUnit(FixedInterval t, int p)
    {
        FixedInterval one = FixedInterval.Exact(BigInteger.One << p);
        for (int i = 0; i < 2; i++)
        {
            t = t.Div((t.Square(p) + one).Sqrt(p) + one, p);
        }

        FixedInterval t2 = t.Square(p);
        FixedInterval sum = t;
        FixedInterval power = t;
        for (int k = 1; ; k++)
        {
            power = power.Mul(t2, p);
            if (power.MaxAbs <= 1)
            {
                // Alternating with decreasing magnitude.
                sum = sum.Widen(1);
                break;
            }

            FixedInterval term = power.DivideBy((2 * k) + 1);
            sum = (k & 1) == 1 ? sum - term : sum + term;
        }

        return sum.Times(4);
    }

    /// <summary>
    /// The angle in <c>[0, pi/2]</c> whose tangent is <c>a / b</c>, for
    /// non-negative a and b, or null when the smaller of the two is too
    /// uncertain to divide by at this precision.
    /// </summary>
    private static FixedInterval? Atan2NonNegative(FixedInterval a, FixedInterval b, int p)
    {
        if (a.Lo + a.Hi <= b.Lo + b.Hi)
        {
            return b.ContainsZero ? null : AtanUnit(a.Div(b, p), p);
        }

        return a.ContainsZero ? null : HalfPi(p) - AtanUnit(b.Div(a, p), p);
    }

    /// <summary>
    /// For <c>|x| = m 2^e &lt;= 1</c>: <c>|x|</c> exactly and
    /// <c>sqrt(1 - x^2)</c>, both at scale <paramref name="p"/>.
    /// </summary>
    private static (FixedInterval A, FixedInterval B) SineAndCosineOfArcsine(BigInteger m, int e, int p)
    {
        // 1 - x^2 = (2^(-2e) - m^2) 2^(2e), exactly; e <= 0 because |x| <= 1.
        BigInteger n = (BigInteger.One << (-2 * e)) - (m * m);
        BigInteger radicand = n << ((2 * e) + (2 * p));
        FixedInterval b = new(FixedInterval.FloorSqrt(radicand), FixedInterval.CeilSqrt(radicand));
        return (FixedInterval.Exact(m << (e + p)), b);
    }

    /// <summary><c>ln x</c> for <c>x = m 2^e &gt; 0</c>, at scale <paramref name="p"/>.</summary>
    /// <param name="m">The integer part; positive.</param>
    /// <param name="e">The power of two.</param>
    /// <param name="p">The scale.</param>
    /// <returns>The interval.</returns>
    internal static FixedInterval Ln(BigInteger m, int e, int p)
    {
        // x = (m / 2^q) 2^k with the mantissa m / 2^q in [0.75, 1.5), so
        // s = (mant - 1)/(mant + 1) has |s| <= 1/5.
        int q = (int)m.GetBitLength() - 1;
        int k = e + q;
        if (m * 2 >= 3 * (BigInteger.One << q))
        {
            q++;
            k++;
        }

        FixedInterval lnm = LnMantissa(m, q, p);
        if (k == 0)
        {
            return lnm;
        }

        return lnm + Ln2(p + 12).Times(k).ShiftDown(12);
    }

    /// <summary>
    /// <c>ln(m / 2^q)</c> for a mantissa in <c>[0.75, 1.5)</c>, as
    /// <c>2 atanh(s)</c> with <c>s = (m - 2^q)/(m + 2^q)</c>.
    /// </summary>
    private static FixedInterval LnMantissa(BigInteger m, int q, int p)
    {
        BigInteger unit = BigInteger.One << q;
        BigInteger num = (m - unit) << p;
        BigInteger den = m + unit;
        FixedInterval s = new(FixedInterval.FloorDiv(num, den), FixedInterval.CeilDiv(num, den));
        FixedInterval s2 = s.Square(p);
        FixedInterval sum = s;
        FixedInterval power = s;
        for (int k = 1; ; k++)
        {
            power = power.Mul(s2, p);
            if (power.MaxAbs <= 1)
            {
                // The rest is under this power times 1/(3 (1 - s^2)) < 1.
                sum = sum.Widen(1);
                break;
            }

            sum += power.DivideBy((2 * k) + 1);
        }

        return sum.Times(2);
    }

    /// <summary>
    /// Reduces x by the nearest multiple of <c>pi/2</c>:
    /// <c>x = k pi/2 + r</c> with <c>|r| &lt;= pi/4</c> (and a little).
    /// </summary>
    /// <param name="x">A finite argument.</param>
    /// <param name="p">The scale wanted for r.</param>
    /// <returns>r at scale p, and <c>k mod 4</c>.</returns>
    internal static (FixedInterval R, int Quadrant) ReduceHalfPi(double x, int p)
    {
        (BigInteger m, int e) = Decompose(Math.Abs(x));
        if (x < 0)
        {
            m = -m;
        }

        // k has up to (e + 53) bits, and each costs a bit of pi's accuracy in
        // k pi/2, so pi is taken with that many more; x itself must be exact
        // at the working scale.
        int q = Math.Max(p + Math.Max(0, e + 53) + 8, -e);
        BigInteger xq = m << (e + q + 1);

        // pi at scale q is pi/2 at scale q + 1, the scale of xq.
        FixedInterval pi = Pi(q);
        BigInteger k = FixedInterval.FloorDiv((2 * xq) + pi.Lo, 2 * pi.Lo);
        FixedInterval r = k.Sign >= 0
            ? new(xq - (k * pi.Hi), xq - (k * pi.Lo))
            : new(xq - (k * pi.Lo), xq - (k * pi.Hi));

        int quadrant = (int)(((k % 4) + 4) % 4);
        return (r.ShiftDown(q + 1 - p), quadrant);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// A finite nonzero double as an odd integer times a power of two.
    /// </summary>
    /// <param name="x">The value.</param>
    /// <returns><c>(m, e)</c> with <c>x = m 2^e</c> and m odd, carrying x's sign.</returns>
    internal static (BigInteger M, int E) Decompose(double x)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int biased = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        long mantissa = biased == 0 ? fraction : fraction | (1L << 52);
        int exponent = (biased == 0 ? 1 : biased) - 1075;
        int zeros = BitOperations.TrailingZeroCount(mantissa);
        mantissa >>= zeros;
        exponent += zeros;
        return (x < 0 ? -mantissa : mantissa, exponent);
    }

    /// <summary>An approximate double of <c>n 2^-p</c>, for range decisions.</summary>
    private static double Approximate(BigInteger n, int p)
    {
        int excess = Math.Max(0, (int)n.GetBitLength() - 62);
        return Math.ScaleB((double)(long)(n >> excess), excess - p);
    }

    /// <summary>The exception for a Ziv loop that ran out of precision: a bug, never an input.</summary>
    internal static InvalidOperationException NotConverged(string function, double x) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"{function}({x:R}) did not round within {LastPrecision} bits; this is a bug in the exact tier."));

    /// <summary>
    /// <c>floor(pi * 2^2046)</c>: 2048 bits of pi, big-endian. A fact
    /// recomputes them by Machin's formula.
    /// </summary>
    private static ReadOnlySpan<byte> PiBits =>
    [
        0xC9, 0x0F, 0xDA, 0xA2, 0x21, 0x68, 0xC2, 0x34, 0xC4, 0xC6, 0x62, 0x8B, 0x80, 0xDC, 0x1C, 0xD1,
        0x29, 0x02, 0x4E, 0x08, 0x8A, 0x67, 0xCC, 0x74, 0x02, 0x0B, 0xBE, 0xA6, 0x3B, 0x13, 0x9B, 0x22,
        0x51, 0x4A, 0x08, 0x79, 0x8E, 0x34, 0x04, 0xDD, 0xEF, 0x95, 0x19, 0xB3, 0xCD, 0x3A, 0x43, 0x1B,
        0x30, 0x2B, 0x0A, 0x6D, 0xF2, 0x5F, 0x14, 0x37, 0x4F, 0xE1, 0x35, 0x6D, 0x6D, 0x51, 0xC2, 0x45,
        0xE4, 0x85, 0xB5, 0x76, 0x62, 0x5E, 0x7E, 0xC6, 0xF4, 0x4C, 0x42, 0xE9, 0xA6, 0x37, 0xED, 0x6B,
        0x0B, 0xFF, 0x5C, 0xB6, 0xF4, 0x06, 0xB7, 0xED, 0xEE, 0x38, 0x6B, 0xFB, 0x5A, 0x89, 0x9F, 0xA5,
        0xAE, 0x9F, 0x24, 0x11, 0x7C, 0x4B, 0x1F, 0xE6, 0x49, 0x28, 0x66, 0x51, 0xEC, 0xE4, 0x5B, 0x3D,
        0xC2, 0x00, 0x7C, 0xB8, 0xA1, 0x63, 0xBF, 0x05, 0x98, 0xDA, 0x48, 0x36, 0x1C, 0x55, 0xD3, 0x9A,
        0x69, 0x16, 0x3F, 0xA8, 0xFD, 0x24, 0xCF, 0x5F, 0x83, 0x65, 0x5D, 0x23, 0xDC, 0xA3, 0xAD, 0x96,
        0x1C, 0x62, 0xF3, 0x56, 0x20, 0x85, 0x52, 0xBB, 0x9E, 0xD5, 0x29, 0x07, 0x70, 0x96, 0x96, 0x6D,
        0x67, 0x0C, 0x35, 0x4E, 0x4A, 0xBC, 0x98, 0x04, 0xF1, 0x74, 0x6C, 0x08, 0xCA, 0x18, 0x21, 0x7C,
        0x32, 0x90, 0x5E, 0x46, 0x2E, 0x36, 0xCE, 0x3B, 0xE3, 0x9E, 0x77, 0x2C, 0x18, 0x0E, 0x86, 0x03,
        0x9B, 0x27, 0x83, 0xA2, 0xEC, 0x07, 0xA2, 0x8F, 0xB5, 0xC5, 0x5D, 0xF0, 0x6F, 0x4C, 0x52, 0xC9,
        0xDE, 0x2B, 0xCB, 0xF6, 0x95, 0x58, 0x17, 0x18, 0x39, 0x95, 0x49, 0x7C, 0xEA, 0x95, 0x6A, 0xE5,
        0x15, 0xD2, 0x26, 0x18, 0x98, 0xFA, 0x05, 0x10, 0x15, 0x72, 0x8E, 0x5A, 0x8A, 0xAA, 0xC4, 0x2D,
        0xAD, 0x33, 0x17, 0x0D, 0x04, 0x50, 0x7A, 0x33, 0xA8, 0x55, 0x21, 0xAB, 0xDF, 0x1C, 0xBA, 0x64,
    ];

    /// <summary>
    /// <c>floor(ln2 * 2^2048)</c>: 2048 bits of <c>ln 2</c>, big-endian. A fact
    /// recomputes them from the <c>atanh(1/3)</c> series.
    /// </summary>
    private static ReadOnlySpan<byte> Ln2Bits =>
    [
        0xB1, 0x72, 0x17, 0xF7, 0xD1, 0xCF, 0x79, 0xAB, 0xC9, 0xE3, 0xB3, 0x98, 0x03, 0xF2, 0xF6, 0xAF,
        0x40, 0xF3, 0x43, 0x26, 0x72, 0x98, 0xB6, 0x2D, 0x8A, 0x0D, 0x17, 0x5B, 0x8B, 0xAA, 0xFA, 0x2B,
        0xE7, 0xB8, 0x76, 0x20, 0x6D, 0xEB, 0xAC, 0x98, 0x55, 0x95, 0x52, 0xFB, 0x4A, 0xFA, 0x1B, 0x10,
        0xED, 0x2E, 0xAE, 0x35, 0xC1, 0x38, 0x21, 0x44, 0x27, 0x57, 0x3B, 0x29, 0x11, 0x69, 0xB8, 0x25,
        0x3E, 0x96, 0xCA, 0x16, 0x22, 0x4A, 0xE8, 0xC5, 0x1A, 0xCB, 0xDA, 0x11, 0x31, 0x7C, 0x38, 0x7E,
        0xB9, 0xEA, 0x9B, 0xC3, 0xB1, 0x36, 0x60, 0x3B, 0x25, 0x6F, 0xA0, 0xEC, 0x76, 0x57, 0xF7, 0x4B,
        0x72, 0xCE, 0x87, 0xB1, 0x9D, 0x65, 0x48, 0xCA, 0xF5, 0xDF, 0xA6, 0xBD, 0x38, 0x30, 0x32, 0x48,
        0x65, 0x5F, 0xA1, 0x87, 0x2F, 0x20, 0xE3, 0xA2, 0xDA, 0x2D, 0x97, 0xC5, 0x0F, 0x3F, 0xD5, 0xC6,
        0x07, 0xF4, 0xCA, 0x11, 0xFB, 0x5B, 0xFB, 0x90, 0x61, 0x0D, 0x30, 0xF8, 0x8F, 0xE5, 0x51, 0xA2,
        0xEE, 0x56, 0x9D, 0x6D, 0xFC, 0x1E, 0xFA, 0x15, 0x7D, 0x2E, 0x23, 0xDE, 0x14, 0x00, 0xB3, 0x96,
        0x17, 0x46, 0x07, 0x75, 0xDB, 0x89, 0x90, 0xE5, 0xC9, 0x43, 0xE7, 0x32, 0xB4, 0x79, 0xCD, 0x33,
        0xCC, 0xCC, 0x4E, 0x65, 0x93, 0x93, 0x51, 0x4C, 0x4C, 0x1A, 0x1E, 0x0B, 0xD1, 0xD6, 0x09, 0x5D,
        0x25, 0x66, 0x9B, 0x33, 0x35, 0x64, 0xA3, 0x37, 0x6A, 0x9C, 0x7F, 0x8A, 0x5E, 0x14, 0x8E, 0x82,
        0x07, 0x4D, 0xB6, 0x01, 0x5C, 0xFE, 0x7A, 0xA3, 0x0C, 0x48, 0x0A, 0x54, 0x17, 0x35, 0x0D, 0x2C,
        0x95, 0x5D, 0x51, 0x79, 0xB1, 0xE1, 0x7B, 0x9D, 0xAE, 0x31, 0x3C, 0xDB, 0x6C, 0x60, 0x6C, 0xB1,
        0x07, 0x8F, 0x73, 0x5D, 0x1B, 0x2D, 0xB3, 0x1B, 0x5F, 0x50, 0xB5, 0x18, 0x50, 0x64, 0xC1, 0x8B,
    ];
}
