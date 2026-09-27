//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Numerics;

/// <summary>
/// Correctly rounded single-precision elementary functions, computed in
/// managed code so the same argument gives the same bits on every OS and CPU.
/// Use these, not <see cref="MathF"/>, anywhere the result can reach a
/// compiled map.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="MathF"/>.</b> .NET forwards <c>MathF.Sin</c>,
/// <c>MathF.Pow</c> and the rest to the platform C library: glibc on Linux,
/// the UCRT on Windows, libSystem on macOS. None of them promises a
/// correctly rounded result, and they disagree with each other in the last bit
/// for a small fraction of arguments, so a lightmap or a prop angle computed
/// through them depends on the machine that compiled the map. A correctly
/// rounded result is unique: it is the float nearest the true value (ties to
/// even), so it is the same everywhere, and it matches any platform's library
/// wherever that library happens to be right.
/// </para>
/// <para>
/// <b>How.</b> Each function first evaluates in double precision with a
/// bounded error (<see cref="FastKernels"/>), then rounds to float only when
/// the whole error band rounds to one value. For about one argument in a
/// million the true result is too close to a float rounding boundary to tell,
/// and the function falls back to arbitrary-precision interval arithmetic
/// (<see cref="ExactMath"/>), which always decides. Arguments outside the fast
/// path's range (a trigonometric argument of 2^25 or more) go there directly.
/// Typical cost is a small multiple of <see cref="MathF"/>'s.
/// </para>
/// <para>
/// <b>Special values</b> follow IEEE 754 and C's Annex F, which is also what
/// <see cref="MathF"/> documents: a NaN argument is returned as it is, a
/// domain error returns <see cref="float.NaN"/>, and signed zeros and
/// infinities are handled as those rules say (see <see cref="Pow"/> and
/// <see cref="Atan2"/> for the full lists).
/// </para>
/// </remarks>
public static class DetMathF
{
    /// <summary>The correctly rounded sine.</summary>
    /// <param name="x">The angle, in radians.</param>
    /// <returns><c>sin x</c>; NaN for an infinite or NaN argument; x itself for a zero.</returns>
    public static float Sin(float x)
    {
        if (x == 0 || !float.IsFinite(x))
        {
            return x == 0 ? x : float.IsNaN(x) ? x : float.NaN;
        }

        if (FastKernels.ReduceHalfPi(x, out double rh, out double rl, out int quadrant)
            && FastKernels.TryNarrow(SinOfReduced(rh, rl, quadrant), out float result))
        {
            return result;
        }

        return (float)ExactMath.Sin(x, RoundingTarget.Single);
    }

    /// <summary>The correctly rounded cosine.</summary>
    /// <param name="x">The angle, in radians.</param>
    /// <returns><c>cos x</c>; NaN for an infinite or NaN argument; 1 for a zero.</returns>
    public static float Cos(float x)
    {
        if (x == 0 || !float.IsFinite(x))
        {
            return x == 0 ? 1f : float.IsNaN(x) ? x : float.NaN;
        }

        if (FastKernels.ReduceHalfPi(x, out double rh, out double rl, out int quadrant)
            && FastKernels.TryNarrow(CosOfReduced(rh, rl, quadrant), out float result))
        {
            return result;
        }

        return (float)ExactMath.Cos(x, RoundingTarget.Single);
    }

    /// <summary>The correctly rounded sine and cosine, from one reduction.</summary>
    /// <param name="x">The angle, in radians.</param>
    /// <returns>Exactly <c>(Sin(x), Cos(x))</c>.</returns>
    public static (float Sin, float Cos) SinCos(float x)
    {
        if (x == 0 || !float.IsFinite(x))
        {
            return (Sin(x), Cos(x));
        }

        if (FastKernels.ReduceHalfPi(x, out double rh, out double rl, out int quadrant))
        {
            float s = FastKernels.TryNarrow(SinOfReduced(rh, rl, quadrant), out float fs)
                ? fs
                : (float)ExactMath.Sin(x, RoundingTarget.Single);
            float c = FastKernels.TryNarrow(CosOfReduced(rh, rl, quadrant), out float fc)
                ? fc
                : (float)ExactMath.Cos(x, RoundingTarget.Single);
            return (s, c);
        }

        return ((float)ExactMath.Sin(x, RoundingTarget.Single), (float)ExactMath.Cos(x, RoundingTarget.Single));
    }

    /// <summary>The correctly rounded tangent.</summary>
    /// <param name="x">The angle, in radians.</param>
    /// <returns><c>tan x</c>; NaN for an infinite or NaN argument; x itself for a zero.</returns>
    public static float Tan(float x)
    {
        if (x == 0 || !float.IsFinite(x))
        {
            return x == 0 ? x : float.IsNaN(x) ? x : float.NaN;
        }

        // sin/cos for an even quadrant and -cos/sin for an odd one, each
        // kernel within 2^-51.5, so the quotient is within 2^-50.
        if (FastKernels.ReduceHalfPi(x, out double rh, out double rl, out int quadrant)
            && FastKernels.TryNarrow(
                (quadrant & 1) == 0
                    ? FastKernels.Sin(rh, rl) / FastKernels.Cos(rh, rl)
                    : -FastKernels.Cos(rh, rl) / FastKernels.Sin(rh, rl),
                out float result))
        {
            return result;
        }

        return (float)ExactMath.Tan(x, RoundingTarget.Single);
    }

    /// <summary>The correctly rounded arcsine.</summary>
    /// <param name="x">The sine, in <c>[-1, 1]</c>.</param>
    /// <returns>
    /// <c>asin x</c> in <c>[-pi/2, pi/2]</c>; NaN outside the domain or for a
    /// NaN argument; x itself for a zero.
    /// </returns>
    public static float Asin(float x)
    {
        if (x == 0 || float.IsNaN(x))
        {
            return x;
        }

        if (!(Math.Abs(x) <= 1))
        {
            return float.NaN;
        }

        // asin x = atan2(x, sqrt((1 - x)(1 + x))). The factored form keeps
        // 1 - x^2 accurate near |x| = 1: each factor and the product carry
        // one rounding, the square root halves their sum, so the cosine is
        // within 2^-52 before the atan2 kernel.
        double a = x;
        if (FastKernels.TryNarrow(FastKernels.Atan2(a, Math.Sqrt((1 - a) * (1 + a))), out float result))
        {
            return result;
        }

        return (float)ExactMath.Asin(x, RoundingTarget.Single);
    }

    /// <summary>The correctly rounded arccosine.</summary>
    /// <param name="x">The cosine, in <c>[-1, 1]</c>.</param>
    /// <returns>
    /// <c>acos x</c> in <c>[0, pi]</c>; NaN outside the domain or for a NaN
    /// argument; <c>+0</c> for 1.
    /// </returns>
    public static float Acos(float x)
    {
        if (float.IsNaN(x))
        {
            return x;
        }

        if (!(Math.Abs(x) <= 1))
        {
            return float.NaN;
        }

        if (x == 1)
        {
            return 0f;
        }

        double a = x;
        if (FastKernels.TryNarrow(FastKernels.Atan2(Math.Sqrt((1 - a) * (1 + a)), a), out float result))
        {
            return result;
        }

        return (float)ExactMath.Acos(x, RoundingTarget.Single);
    }

    /// <summary>The correctly rounded two-argument arctangent.</summary>
    /// <param name="y">The ordinate.</param>
    /// <param name="x">The abscissa.</param>
    /// <returns>
    /// The angle of <c>(x, y)</c> in <c>[-pi, pi]</c>. A NaN argument is
    /// returned (y's if both are NaN). Otherwise, with <c>+-</c> the sign of y:
    /// <c>(+-0, +0 or x &gt; 0)</c> is <c>+-0</c>; <c>(+-0, -0 or x &lt; 0)</c>
    /// is <c>+-pi</c>; <c>(y != 0, +-0)</c> is <c>+-pi/2</c>;
    /// <c>(finite, +inf)</c> is <c>+-0</c> and <c>(finite, -inf)</c>
    /// <c>+-pi</c>; <c>(+-inf, finite)</c> is <c>+-pi/2</c>;
    /// <c>(+-inf, +inf)</c> is <c>+-pi/4</c> and <c>(+-inf, -inf)</c>
    /// <c>+-3pi/4</c>.
    /// </returns>
    public static float Atan2(float y, float x)
    {
        if (float.IsNaN(y) || float.IsNaN(x))
        {
            // Chosen explicitly rather than by an arithmetic operation, whose
            // choice between two NaN operands differs between instruction sets.
            return float.IsNaN(y) ? y : x;
        }

        if (y == 0)
        {
            if (float.IsNegative(x))
            {
                return float.IsNegative(y) ? -Pi : Pi;
            }

            return y;
        }

        if (float.IsInfinity(y) || float.IsInfinity(x))
        {
            float angle = float.IsInfinity(y)
                ? (float.IsInfinity(x) ? (x > 0 ? QuarterPi : ThreeQuarterPi) : HalfPi)
                : (x > 0 ? 0f : Pi);
            return y < 0 ? -angle : angle;
        }

        if (x == 0)
        {
            return y < 0 ? -HalfPi : HalfPi;
        }

        // Float arguments: the ratio of the magnitudes is between 2^-277 and
        // 2^277, well inside the double range the kernel needs.
        if (FastKernels.TryNarrow(FastKernels.Atan2(y, x), out float result))
        {
            return result;
        }

        return (float)ExactMath.Atan2(y, x, RoundingTarget.Single);
    }

    /// <summary>The correctly rounded power.</summary>
    /// <param name="x">The base.</param>
    /// <param name="y">The exponent.</param>
    /// <returns>
    /// <c>x^y</c>. Special cases as IEEE 754's <c>pow</c> and C's Annex F:
    /// <c>pow(x, +-0) = 1</c> and <c>pow(1, y) = 1</c> for any x or y, even
    /// NaN; otherwise a NaN argument is returned; <c>pow(-1, +-inf) = 1</c>;
    /// an infinite y gives <c>+0</c> or <c>+inf</c> by whether <c>|x|</c> is
    /// below or above 1; a zero or infinite x gives a zero or infinity whose
    /// sign is x's only for an odd integer y; a negative finite x gives NaN
    /// for a non-integer y and <c>+-|x|^y</c> for an integer one.
    /// </returns>
    public static float Pow(float x, float y)
    {
        if (DetMath.PowSpecialCase(x, y, out double special, out double magnitude, out bool negate))
        {
            return (float)special;
        }

        (double zh, double zl) = FastKernels.LogTimes(magnitude, y);

        // Decisive range limits: e^89.5 is past 2^129 and e^-104.5 under
        // 2^-150.7, below half the least subnormal, and z's error is a
        // few ulps.
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
            result = (float)ExactMath.Pow(magnitude, y, RoundingTarget.Single);
        }

        return negate ? -result : result;
    }

    private const float Pi = 3.14159274f;
    private const float HalfPi = 1.57079637f;
    private const float QuarterPi = 0.785398185f;
    private const float ThreeQuarterPi = 2.3561945f;

    /// <summary>sin of <c>r + quadrant pi/2</c>.</summary>
    internal static double SinOfReduced(double rh, double rl, int quadrant) => quadrant switch
    {
        0 => FastKernels.Sin(rh, rl),
        1 => FastKernels.Cos(rh, rl),
        2 => -FastKernels.Sin(rh, rl),
        _ => -FastKernels.Cos(rh, rl),
    };

    /// <summary>cos of <c>r + quadrant pi/2</c>.</summary>
    internal static double CosOfReduced(double rh, double rl, int quadrant) => quadrant switch
    {
        0 => FastKernels.Cos(rh, rl),
        1 => -FastKernels.Sin(rh, rl),
        2 => -FastKernels.Cos(rh, rl),
        _ => FastKernels.Sin(rh, rl),
    };
}
