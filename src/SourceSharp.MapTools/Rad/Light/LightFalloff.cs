using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>SetLightFalloffParams</c>: the three
/// attenuation coefficients and the hard-falloff window.
/// </summary>
/// <remarks>
/// <para>
/// Two completely separate paths, chosen by whether the entity has a non-zero
/// <c>_fifty_percent_distance</c>:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Solve for the curve.</b> Given the distances at which the light should
/// be at 50% and 0%, fit <c>a x^2 + b x + c</c> through (0, 1), (d50, 2) and
/// (d0, 256) -- the RECIPROCALS of the wanted brightnesses, since the falloff
/// divides by this polynomial -- then rescale so the 50% point is exact.
/// </description></item>
/// <item><description>
/// <b>Take the coefficients literally</b> from <c>_constant_attn</c>,
/// <c>_linear_attn</c> and <c>_quadratic_attn</c>, then scale the intensity so
/// that a light at 100 units is unchanged by the choice.
/// </description></item>
/// </list>
/// <para>
/// <b>Path 2 rescales the light's INTENSITY, not just its falloff</b>
/// The factor is the falloff denominator evaluated at 100
/// units, so a pure-quadratic light is multiplied by 10,000 and a pure-linear
/// one by 100. Miss it and every point light in the map is four orders of
/// magnitude too dim.
/// </para>
/// </remarks>
public static class LightFalloff
{
    /// <summary>
    /// The distance the literal-coefficient path normalises intensity at: 100
    /// Units.
    /// </summary>
    public const float NormalisationDistance = 100f;

    /// <summary>
    /// Applies the falloff keys to a light.
    /// </summary>
    /// <param name="entity">The light entity.</param>
    /// <param name="light">The light, whose attenuation and fade window are set.</param>
    /// <param name="warnings">
    /// Receives the diagnostics stock prints as <c>Warning</c>. May be null.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <param name="reciprocalSolve">
    /// Solve the falloff curve as stock's binary does
    /// (<see cref="Options.StockQuirk.InverseQuadraticReciprocal"/>).
    /// </param>
    /// <param name="derivativeAtOne">
    /// Test the fit's slope where stock does
    /// (<see cref="Options.StockQuirk.MonotonicDerivativeAtOne"/>).
    /// </param>
    /// <paramref name="entity"/> or <paramref name="light"/> is null.
    /// </exception>
    public static void Apply(
        BspEntity entity,
        DirectLight light,
        IList<string>? warnings = null,
        bool reciprocalSolve = false,
        bool derivativeAtOne = false)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(light);

        float d50 = EntityKeys.FloatForKey(entity, "_fifty_percent_distance");

        // Reset before either path, so a light that
        // had these from a previous parse does not inherit them.
        light.StartFadeDistance = 0;
        light.EndFadeDistance = -1;
        light.CapDist = 1.0e22f;

        if (d50 == 0f)
        {
            ApplyLiteralCoefficients(entity, light);
            return;
        }

        ApplySolvedCurve(entity, light, d50, warnings, reciprocalSolve, derivativeAtOne);
    }

    private static void ApplyLiteralCoefficients(BspEntity entity, DirectLight light)
    {
        light.ConstantAttn = EntityKeys.FloatForKey(entity, "_constant_attn");
        light.LinearAttn = EntityKeys.FloatForKey(entity, "_linear_attn");
        light.QuadraticAttn = EntityKeys.FloatForKey(entity, "_quadratic_attn");
        light.Radius = EntityKeys.FloatForKey(entity, "_distance");

        // Clamped to zero BELOW EQUAL_EPSILON, not below zero --
        // so a deliberate 0.0005 quadratic term is discarded.
        if (light.ConstantAttn < LightConstants.EqualEpsilon)
        {
            light.ConstantAttn = 0;
        }

        if (light.LinearAttn < LightConstants.EqualEpsilon)
        {
            light.LinearAttn = 0;
        }

        if (light.QuadraticAttn < LightConstants.EqualEpsilon)
        {
            light.QuadraticAttn = 0;
        }

        // All three gone means a light with no falloff at all,
        // which would be infinite; constant 1 makes it flat instead.
        if (light.ConstantAttn < LightConstants.EqualEpsilon
            && light.LinearAttn < LightConstants.EqualEpsilon
            && light.QuadraticAttn < LightConstants.EqualEpsilon)
        {
            light.ConstantAttn = 1;
        }

        // See the type remarks: this is the four-orders-of-
        // magnitude one.
        float ratio = light.ConstantAttn
            + (NormalisationDistance * light.LinearAttn)
            + (NormalisationDistance * NormalisationDistance * light.QuadraticAttn);
        if (ratio > 0)
        {
            light.Intensity *= ratio;
        }
    }

    private static void ApplySolvedCurve(
        BspEntity entity,
        DirectLight light,
        float d50,
        IList<string>? warnings,
        bool reciprocalSolve,
        bool derivativeAtOne)
    {
        float d0 = EntityKeys.FloatForKey(entity, "_zero_percent_distance");

        if (d0 < d50)
        {
            warnings?.Add(
                $"light has _fifty_percent_distance of {d50} but _zero_percent_distance of {d0}");
            d0 = (float)(2.0 * d50);
        }

        // The y values are RECIPROCAL brightnesses: 1 at distance zero,
        // 2 at the half point, 256 at the zero point -- 256 rather than
        // infinity because the curve has to stay finite.
        // Seeded 0, 1, 0: a solve that fails leaves these in place.
        float a = 0f, b = 1f, c = 0f;
        if (!MathSolvers.SolveInverseQuadraticMonotonic(
                0f, 1.0f, d50, 2.0f, d0, 256.0f, ref a, ref b, ref c, reciprocalSolve, derivativeAtOne))
        {
            warnings?.Add($"can't solve quadratic for light {d50} {d0}");
        }

        // Monotonicity enforcement in the solver can move the
        // midpoint, so the 50% value is re-normalised afterwards.
        float v50 = c + (d50 * (b + (d50 * a)));
        // `2.0 / v50`, `0.75 * d0`, `b / (-2.0 * a)`: every constant in this
        // function is a double literal, so each expression is double arithmetic
        // narrowed into its float destination.
        float scale = (float)(2.0 / v50);
        a *= scale;
        b *= scale;
        c *= scale;

        light.QuadraticAttn = a;
        light.LinearAttn = b;
        light.ConstantAttn = c;

        if (EntityKeys.IntForKey(entity, "_hardfalloff") != 0)
        {
            // Fade starts three quarters of the way from the half
            // point to the zero point.
            light.EndFadeDistance = d0;
            light.StartFadeDistance = (float)((0.75 * d0) + (0.25 * d50));
            return;
        }

        // An extreme falloff gives the quadratic a positive
        // leading coefficient, so past its minimum the light would brighten
        // with distance. Freeze the falloff there and fade out over ten times
        // that distance.
        if (Math.Abs(a) > 0.0)
        {
            float max = (float)(b / (-2.0 * a));
            if (max > 0.0f)
            {
                light.CapDist = max;
                light.StartFadeDistance = max;
                light.EndFadeDistance = (float)(10.0 * max);
            }
        }
    }
}
