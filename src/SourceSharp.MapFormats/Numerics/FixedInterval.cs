//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;

namespace SourceSharp.MapFormats.Numerics;

/// <summary>
/// A closed interval of fixed-point reals, <c>[Lo, Hi] * 2^-p</c>, where the
/// scale <c>p</c> is carried by the caller rather than by the value.
/// </summary>
/// <remarks>
/// <para>
/// The exact tier of <see cref="DetMath"/> is built on this so that its error
/// bounds are not an argument written in a comment but a property of the
/// arithmetic: every operation rounds its lower end down and its upper end
/// up, so an interval that contains the true operands always contains the
/// true result. A series is summed the same way and its truncated tail is
/// added as an explicit widening, which is the only place a bound has to be
/// derived by hand.
/// </para>
/// <para>
/// The scale is not stored because every interval in one evaluation shares
/// it; passing it to the operations that need it (multiply, divide, square
/// root) keeps the struct two <see cref="BigInteger"/>s wide and makes a mixed
/// scale a visible argument rather than a silent mismatch.
/// </para>
/// </remarks>
/// <param name="lo">The lower end, in units of <c>2^-p</c>.</param>
/// <param name="hi">The upper end, in units of <c>2^-p</c>. Must not be below <paramref name="lo"/>.</param>
internal readonly struct FixedInterval(BigInteger lo, BigInteger hi)
{
    /// <summary>The lower end.</summary>
    public BigInteger Lo { get; } = lo;

    /// <summary>The upper end.</summary>
    public BigInteger Hi { get; } = hi;

    /// <summary>The larger of <c>|Lo|</c> and <c>|Hi|</c>: a bound on the magnitude of anything inside.</summary>
    public BigInteger MaxAbs => BigInteger.Max(BigInteger.Abs(Lo), BigInteger.Abs(Hi));

    /// <summary>Whether zero lies inside, so the interval cannot be a divisor.</summary>
    public bool ContainsZero => Lo.Sign <= 0 && Hi.Sign >= 0;

    /// <summary>A single exact value.</summary>
    /// <param name="value">The value, in units of <c>2^-p</c>.</param>
    /// <returns>The degenerate interval.</returns>
    public static FixedInterval Exact(BigInteger value) => new(value, value);

    /// <summary>Exact sum.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns>The sum.</returns>
    public static FixedInterval operator +(FixedInterval a, FixedInterval b) => new(a.Lo + b.Lo, a.Hi + b.Hi);

    /// <summary>Exact difference.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns>The difference.</returns>
    public static FixedInterval operator -(FixedInterval a, FixedInterval b) => new(a.Lo - b.Hi, a.Hi - b.Lo);

    /// <summary>Exact negation.</summary>
    /// <param name="a">The operand.</param>
    /// <returns>The negation.</returns>
    public static FixedInterval operator -(FixedInterval a) => new(-a.Hi, -a.Lo);

    /// <summary>Floor division of any integer by a positive one.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor; must be positive.</param>
    /// <returns><c>floor(a / b)</c>.</returns>
    public static BigInteger FloorDiv(BigInteger a, BigInteger b)
    {
        BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
        return r.Sign < 0 ? q - 1 : q;
    }

    /// <summary>Ceiling division of any integer by a positive one.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor; must be positive.</param>
    /// <returns><c>ceil(a / b)</c>.</returns>
    public static BigInteger CeilDiv(BigInteger a, BigInteger b)
    {
        BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
        return r.Sign > 0 ? q + 1 : q;
    }

    /// <summary><c>floor(a / 2^s)</c> for any sign of <paramref name="a"/>.</summary>
    /// <param name="a">The value.</param>
    /// <param name="s">The shift; must not be negative.</param>
    /// <returns>The shifted value, rounded down.</returns>
    /// <remarks>
    /// <see cref="BigInteger"/>'s right shift is arithmetic on the two's
    /// complement form, which is already a floor for negative values; the
    /// helper exists so the direction is named where it is relied on, and a
    /// fact pins the runtime's behaviour.
    /// </remarks>
    public static BigInteger FloorShift(BigInteger a, int s) => a >> s;

    /// <summary><c>ceil(a / 2^s)</c> for any sign of <paramref name="a"/>.</summary>
    /// <param name="a">The value.</param>
    /// <param name="s">The shift; must not be negative.</param>
    /// <returns>The shifted value, rounded up.</returns>
    public static BigInteger CeilShift(BigInteger a, int s) => -((-a) >> s);

    /// <summary><c>floor(sqrt(n))</c> for a non-negative integer.</summary>
    /// <param name="n">The radicand.</param>
    /// <returns>The integer square root.</returns>
    public static BigInteger FloorSqrt(BigInteger n)
    {
        if (n.Sign <= 0)
        {
            return BigInteger.Zero;
        }

        // Newton's iteration from above: starting at or over the root, each
        // step stays at or over it and strictly decreases until it reaches
        // floor(sqrt(n)), where the next step would not decrease.
        BigInteger x = BigInteger.One << (int)((n.GetBitLength() + 1) / 2);
        while (true)
        {
            BigInteger y = (x + (n / x)) >> 1;
            if (y >= x)
            {
                return x;
            }

            x = y;
        }
    }

    /// <summary><c>ceil(sqrt(n))</c> for a non-negative integer.</summary>
    /// <param name="n">The radicand.</param>
    /// <returns>The integer square root, rounded up.</returns>
    public static BigInteger CeilSqrt(BigInteger n)
    {
        BigInteger f = FloorSqrt(n);
        return f * f == n ? f : f + 1;
    }

    /// <summary>Widens both ends by <paramref name="t"/> units.</summary>
    /// <param name="t">The widening; must not be negative.</param>
    /// <returns>The wider interval.</returns>
    public FixedInterval Widen(BigInteger t) => new(Lo - t, Hi + t);

    /// <summary>Exact product with an integer.</summary>
    /// <param name="k">The integer.</param>
    /// <returns>The product.</returns>
    public FixedInterval Times(BigInteger k) => k.Sign >= 0 ? new(Lo * k, Hi * k) : new(Hi * k, Lo * k);

    /// <summary>Quotient by a positive integer, rounded outward.</summary>
    /// <param name="n">The divisor; must be positive.</param>
    /// <returns>The quotient.</returns>
    public FixedInterval DivideBy(BigInteger n) => new(FloorDiv(Lo, n), CeilDiv(Hi, n));

    /// <summary>Division by <c>2^s</c>, rounded outward: a change to a coarser scale.</summary>
    /// <param name="s">The number of fractional bits to drop.</param>
    /// <returns>The interval at scale <c>p - s</c>.</returns>
    public FixedInterval ShiftDown(int s) => s <= 0 ? new(Lo << -s, Hi << -s) : new(FloorShift(Lo, s), CeilShift(Hi, s));

    /// <summary>Product at scale <paramref name="p"/>, rounded outward.</summary>
    /// <param name="b">The other factor.</param>
    /// <param name="p">The shared scale.</param>
    /// <returns>The product.</returns>
    public FixedInterval Mul(FixedInterval b, int p)
    {
        BigInteger lo;
        BigInteger hi;
        if (Lo.Sign >= 0 && b.Lo.Sign >= 0)
        {
            // Both non-negative, the common case in every series here: the
            // extremes are the matching ends.
            lo = Lo * b.Lo;
            hi = Hi * b.Hi;
        }
        else
        {
            BigInteger p1 = Lo * b.Lo;
            BigInteger p2 = Lo * b.Hi;
            BigInteger p3 = Hi * b.Lo;
            BigInteger p4 = Hi * b.Hi;
            lo = BigInteger.Min(BigInteger.Min(p1, p2), BigInteger.Min(p3, p4));
            hi = BigInteger.Max(BigInteger.Max(p1, p2), BigInteger.Max(p3, p4));
        }

        return new(FloorShift(lo, p), CeilShift(hi, p));
    }

    /// <summary>Square at scale <paramref name="p"/>, rounded outward.</summary>
    /// <param name="p">The scale.</param>
    /// <returns>The square, which is never negative.</returns>
    /// <remarks>
    /// Not <c>Mul(this)</c>: a product of an interval with itself treats the
    /// two factors as independent, and an interval straddling zero would get a
    /// negative lower end that no square has.
    /// </remarks>
    public FixedInterval Square(int p)
    {
        BigInteger a = BigInteger.Abs(Lo);
        BigInteger b = BigInteger.Abs(Hi);
        BigInteger max = BigInteger.Max(a, b);
        BigInteger min = ContainsZero ? BigInteger.Zero : BigInteger.Min(a, b);
        return new(FloorShift(min * min, p), CeilShift(max * max, p));
    }

    /// <summary>Quotient at scale <paramref name="p"/>, rounded outward.</summary>
    /// <param name="b">The divisor; must not contain zero.</param>
    /// <param name="p">The shared scale.</param>
    /// <returns>The quotient.</returns>
    public FixedInterval Div(FixedInterval b, int p)
    {
        BigInteger lo = BigInteger.Zero;
        BigInteger hi = BigInteger.Zero;
        bool first = true;

        // Division by a negative is division of the negation by a positive.
        foreach (BigInteger n in (ReadOnlySpan<BigInteger>)[Lo << p, Hi << p])
        {
            foreach (BigInteger d in (ReadOnlySpan<BigInteger>)[b.Lo, b.Hi])
            {
                BigInteger num = d.Sign < 0 ? -n : n;
                BigInteger den = BigInteger.Abs(d);
                BigInteger f = FloorDiv(num, den);
                BigInteger c = CeilDiv(num, den);
                lo = first ? f : BigInteger.Min(lo, f);
                hi = first ? c : BigInteger.Max(hi, c);
                first = false;
            }
        }

        return new(lo, hi);
    }

    /// <summary>Square root at scale <paramref name="p"/>, rounded outward.</summary>
    /// <param name="p">The scale.</param>
    /// <returns>The root. A negative lower end is treated as zero.</returns>
    public FixedInterval Sqrt(int p)
    {
        BigInteger lo = Lo.Sign <= 0 ? BigInteger.Zero : FloorSqrt(Lo << p);
        return new(lo, CeilSqrt(BigInteger.Max(Hi, BigInteger.Zero) << p));
    }
}
