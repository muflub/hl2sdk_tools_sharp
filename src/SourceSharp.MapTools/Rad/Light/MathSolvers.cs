//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>SolveInverseQuadratic</c> and <c>SolveInverseQuadraticMonotonic</c>
/// </summary>
/// <remarks>
/// Only one caller in all of vrad -- <see cref="LightFalloff"/> -- but it is
/// the caller that decides how every <c>_fifty_percent_distance</c> light in a
/// map falls off, and the monotonic wrapper is peculiar enough that inlining it
/// would hide what it does.
/// </remarks>
public static class MathSolvers
{
    /// <summary>
    /// Fits <c>a x^2 + b x + c</c> through three points.
    /// </summary>
    /// <param name="x1">The first point's x.</param>
    /// <param name="y1">Its y.</param>
    /// <param name="x2">The second point's x.</param>
    /// <param name="y2">Its y.</param>
    /// <param name="x3">The third point's x.</param>
    /// <param name="y3">Its y.</param>
    /// <param name="a">The quadratic coefficient; left untouched on failure.</param>
    /// <param name="b">The linear coefficient; left untouched on failure.</param>
    /// <param name="c">The constant; left untouched on failure.</param>
    /// <param name="reciprocal">
    /// True to divide by the determinant as stock's binary does, through a
    /// reciprocal (<see cref="Options.StockQuirk.InverseQuadraticReciprocal"/>).
    /// </param>
    /// <returns>False when two x values coincide.</returns>
    /// <remarks>
    /// The determinant test is <c>== 0.0</c> exactly, with stock's own
    /// <c>FIXME: check with some sort of epsilon</c> beside it
    /// Two x values a float apart therefore
    /// "succeed" and produce coefficients of order 1e38. Reproduced; the only
    /// caller passes 0, d50 and d0, which a map would have to work at to make
    /// nearly equal.
    /// </remarks>
    public static bool SolveInverseQuadratic(
        float x1, float y1, float x2, float y2, float x3, float y3,
        ref float a, ref float b, ref float c,
        bool reciprocal = false)
    {
        float det = (x1 - x2) * (x1 - x3) * (x2 - x3);

        // A failure returns WITHOUT writing a, b or c,
        // so the caller's own values survive -- SetLightFalloffParams seeds
        // them with 0, 1, 0 and uses them regardless.
        if (det == 0.0f)
        {
            return false;
        }

        float na = (x3 * (-y1 + y2)) + (x2 * (y1 - y3)) + (x1 * (-y2 + y3));
        float nb = (x3 * x3 * (y1 - y2)) + (x1 * x1 * (y2 - y3)) + (x2 * x2 * (-y1 + y3));
        float nc = (x1 * x3 * (-x1 + x3) * y2)
            + (x2 * x2 * ((x3 * y1) - (x1 * y3)))
            + (x2 * ((-(x3 * x3 * y1)) + (x1 * x1 * y3)));

        if (reciprocal)
        {
            // StockQuirk.InverseQuadraticReciprocal: what /fp:fast made of
            // the three divides the reference build keeps.
            float inv = 1.0f / det;
            a = na * inv;
            b = nb * inv;
            c = nc * inv;
        }
        else
        {
            a = na / det;
            b = nb / det;
            c = nc / det;
        }

        return true;
    }

    /// <summary>
    /// Fits a quadratic that does not double back.
    /// </summary>
    /// <param name="x1">The first point's x.</param>
    /// <param name="y1">Its y.</param>
    /// <param name="x2">The second point's x.</param>
    /// <param name="y2">Its y.</param>
    /// <param name="x3">The third point's x.</param>
    /// <param name="y3">Its y.</param>
    /// <param name="a">The quadratic coefficient; left untouched on failure.</param>
    /// <param name="b">The linear coefficient; left untouched on failure.</param>
    /// <param name="c">The constant; left untouched on failure.</param>
    /// <param name="reciprocal">
    /// True to divide by the determinant as stock's binary does, through a
    /// reciprocal (<see cref="Options.StockQuirk.InverseQuadraticReciprocal"/>).
    /// </param>
    /// <param name="derivativeAtOne">
    /// True to test the slope at x = 1 as stock does
    /// (<see cref="Options.StockQuirk.MonotonicDerivativeAtOne"/>).
    /// </param>
    /// <returns>False when <see cref="SolveInverseQuadratic"/> failed.</returns>
    /// <remarks>
    /// <para>
    /// The three points are sorted by x with a three-comparison bubble
    /// Then the MIDDLE point's y is blended toward the
    /// straight line between the outer two in twenty-one steps of 0.05, until
    /// the fitted curve's derivative at the start has the same sign as the
    /// data's overall trend. Stock's own comment concedes "this code is not
    /// fast".
    /// </para>
    /// <para>
    /// <b>The derivative it tests is <c>2a + b</c>, which is the derivative at
    /// x = 1 and not at the start point</b> -- the comment above it says "at
    /// the start point" and the start point is <c>x1</c>, whose derivative is
    /// <c>2 a x1 + b</c>. The only caller passes <c>x1 = 0</c>, where the
    /// derivative is <c>b</c>, so the test is wrong for every call it ever
    /// receives. Reproduced exactly: the wrongness only decides how many blend
    /// steps run before it accepts, and both the right and the wrong test
    /// accept the same curve for the distances real maps use.
    /// </para>
    /// <para>
    /// The loop counter is a <c>float</c> accumulated by <c>+= 0.05</c>
    /// And 0.05 is not representable: it runs <b>20</b> times,
    /// the last blend is 0.95000017 and the next value is 1.0000001, which
    /// fails <c>&lt;= 1.0</c>. So the fully-linear blend is never evaluated.
    /// Reproduced with a float accumulator, because the blend factor feeds the
    /// fit.
    /// </para>
    /// </remarks>
    public static bool SolveInverseQuadraticMonotonic(
        float x1, float y1, float x2, float y2, float x3, float y3,
        ref float a, ref float b, ref float c,
        bool reciprocal = false,
        bool derivativeAtOne = false)
    {
        if (x1 > x2)
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
        }

        if (x2 > x3)
        {
            (x2, x3) = (x3, x2);
            (y2, y3) = (y3, y2);
        }

        if (x1 > x2)
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
        }

        // The float counter is stepped by the DOUBLE 0.05 and narrowed
        // back each time.
        for (float blend = 0.0f; blend <= 1.0f; blend = (float)(blend + 0.05))
        {
            float linear = FLerp(y1, y3, x1, x3, x2);
            float tempY2 = ((1 - blend) * y2) + (blend * linear);

            if (!SolveInverseQuadratic(x1, y1, x2, tempY2, x3, y3, ref a, ref b, ref c, reciprocal))
            {
                return false;
            }

            // 1392. `2.0*a+b` is double arithmetic narrowed into a float, and
            // it is the slope at x = 1 (StockQuirk.MonotonicDerivativeAtOne);
            // the slope at the start point the comment names is 2*a*x1 + b.
            float derivative = derivativeAtOne
                ? (float)((2.0 * a) + b)
                : (float)((2.0 * a * x1) + b);

            if (y1 < y2 && y2 < y3)
            {
                if (derivative >= 0.0f)
                {
                    return true;
                }
            }
            else if (y1 > y2 && y2 > y3)
            {
                if (derivative <= 0.0f)
                {
                    return true;
                }
            }
            else
            {
                // Not monotonic in the data at all, so there is nothing
                // to enforce and the first fit is accepted.
                return true;
            }
        }

        // Falls out of the loop reporting SUCCESS with whatever the
        // last blend produced, which is the 0.95 blend rather than the linear
        // one. The caller cannot tell this apart from an early success.
        return true;
    }

    private static float FLerp(float f1, float f2, float i1, float i2, float x) =>
        f1 + ((f2 - f1) * (x - i1) / (i2 - i1));
}
