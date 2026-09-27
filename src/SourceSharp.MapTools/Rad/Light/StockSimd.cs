//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The SSE reciprocal and reciprocal-square-root routines stock's lighting
/// code calls, either as stock's binary computes them or exactly.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReciprocalSIMD</c> is <c>rcpps</c> plus one
/// Newton step, <c>2y - a*y*y</c>; <c>ReciprocalSqrtSIMD</c> is
/// <c>rsqrtps</c> plus one, <c>0.5 * y * (3 - a*y*y)</c>; a
/// <c>FourVectors::VectorNormalize</c> multiplies by the latter
/// with NO epsilon, unlike the scalar <c>VectorNormalize</c>. The estimate
/// instructions are architecturally allowed to differ between CPU models, so
/// the stock form is machine-dependent
/// (<see cref="Options.StockQuirk.GatherReciprocalEstimate"/>); the exact form
/// is <c>1/x</c> and <c>1/sqrt(x)</c> in IEEE float. On arm64 the estimate
/// is ARM's own (see <see cref="FloatEstimate"/>).
/// </para>
/// <para>
/// <c>rcpps(0)</c> is +infinity, and the Newton step then gives
/// <c>inf - 0*inf = NaN</c>. Stock's callers clamp that away with
/// <c>maxps</c>; the exact form gives +infinity, which the same clamps treat the
/// same way wherever it matters.
/// </para>
/// </remarks>
public static class StockSimd
{
    /// <summary><c>ReciprocalSIMD</c>: <c>1 / a</c>.</summary>
    /// <param name="a">The value.</param>
    /// <param name="estimate">True for <c>rcpss</c> plus one Newton step.</param>
    /// <returns>The reciprocal.</returns>
    /// <exception cref="PlatformNotSupportedException">The estimate was asked for on a CPU with neither SSE nor AdvSimd.</exception>
    public static float Reciprocal(float a, bool estimate)
    {
        if (!estimate)
        {
            return 1.0f / a;
        }

        RequireEstimate();
        float ret = FloatEstimate.Reciprocal(a);
        return (ret + ret) - (a * (ret * ret));
    }

    /// <summary><c>ReciprocalSqrtSIMD</c>: <c>1 / sqrt(a)</c>.</summary>
    /// <param name="a">The value.</param>
    /// <param name="estimate">True for <c>rsqrtss</c> plus one Newton step.</param>
    /// <returns>The reciprocal square root.</returns>
    /// <exception cref="PlatformNotSupportedException">The estimate was asked for on a CPU with neither SSE nor AdvSimd.</exception>
    public static float ReciprocalSqrt(float a, bool estimate)
    {
        if (!estimate)
        {
            return 1.0f / MathF.Sqrt(a);
        }

        RequireEstimate();
        float guess = FloatEstimate.ReciprocalSqrt(a);
        guess *= 3.0f - (a * (guess * guess));
        return 0.5f * guess;
    }

    /// <summary>
    /// <c>FourVectors::VectorNormalize</c> for one lane: <c>v * ReciprocalSqrtSIMD(v.v)</c>.
    /// </summary>
    /// <param name="v">The vector.</param>
    /// <param name="estimate">True for stock's estimate.</param>
    /// <returns>The scaled vector.</returns>
    /// <remarks>
    /// The exact form is the ordinary exact normalise, so a zero vector stays
    /// zero rather than becoming NaN.
    /// </remarks>
    public static Vec3 NormaliseFour(Vec3 v, bool estimate)
    {
        if (!estimate)
        {
            return v.Normalise().Normalised;
        }

        float magSq = (v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z);
        return v * ReciprocalSqrt(magSq, estimate: true);
    }

    /// <summary>
    /// <c>PowSIMD</c>: <c>x</c> to
    /// an exponent held in fixed point with TWO fractional bits.
    /// </summary>
    /// <param name="x">The base.</param>
    /// <param name="exponent">The exponent, truncated to quarters: 1.3 is 1.25.</param>
    /// <returns>The power.</returns>
    /// <remarks>
    /// Square roots for the quarter and half, then square-and-multiply for the
    /// integer part; a negative exponent takes <c>ReciprocalEstSaturateSIMD</c>,
    /// which is an ESTIMATE with no Newton step -- the one estimate the spot
    /// cone can reach, and only for a negative <c>_exponent</c>.
    /// </remarks>
    public static float FixedPointPow(float x, float exponent)
    {
        int fixedExponent = (int)(4.0 * exponent);
        float result = 1.0f;
        int xp = Math.Abs(fixedExponent);
        if ((xp & 3) != 0)
        {
            float sqrt = MathF.Sqrt(x);
            if ((xp & 1) != 0)
            {
                result = MathF.Sqrt(sqrt);
            }

            if ((xp & 2) != 0)
            {
                result *= sqrt;
            }
        }

        xp >>= 2;
        float current = x;
        while (true)
        {
            if ((xp & 1) != 0)
            {
                result *= current;
            }

            xp >>= 1;
            if (xp == 0)
            {
                break;
            }

            current *= current;
        }

        if (fixedExponent < 0)
        {
            RequireEstimate();
            // Four_Epsilons is FLT_EPSILON, OR-ed into a zero.
            float saturated = result == 0.0f ? 1.1920929e-7f : result;
            return FloatEstimate.Reciprocal(saturated);
        }

        return result;
    }

    private static void RequireEstimate()
    {
        if (!FloatEstimate.IsSupported)
        {
            throw FloatEstimate.Unsupported();
        }
    }
}
