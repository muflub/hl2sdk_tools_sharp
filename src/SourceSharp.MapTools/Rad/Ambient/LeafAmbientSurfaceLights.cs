using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// Which <c>emit_surface</c> lights get folded into the ambient cubes instead of
/// being shipped as lights (<c>IsLeafAmbientSurfaceLight</c>.
/// </summary>
/// <remarks>
/// <para>
/// A texture light is one <c>emit_surface</c> entry per emitting patch, so a map
/// can carry thousands of them and most are dim. The engine's
/// <c>r_worldlightmin</c> would throw the dim ones away at runtime, leaving the
/// room lit by nothing; baking them into the leaf ambient cubes keeps the light
/// and costs no per-frame work.
/// </para>
/// <para>
/// <b>This is a side effect on the world-light lump, not just a filter.</b>
/// <c>ComputePerLeafAmbientLighting</c> sets or clears
/// <c>DWL_FLAGS_INAMBIENTCUBE</c> on every light before it samples anything,
/// and those flags are written back to <c>LUMP_WORLDLIGHTS</c>. So a compiled
/// map records this decision, which makes it independently checkable against
/// stock without computing a single ambient cube.
/// </para>
/// </remarks>
public static class LeafAmbientSurfaceLights
{
    /// <summary>
    /// The intensity below which a surface light is baked rather than shipped.
    /// </summary>
    /// <remarks>
    /// <c>g_flWorldLightMinEmitSurface</c>, 0.005. It is compared against the
    /// light's brightness AT 512 UNITS -- the threshold is intensity times
    /// <see cref="MinEmitSurfaceDistanceRatio"/> -- so it asks "would this light
    /// still be visible half a room away?".
    /// </remarks>
    public const float MinEmitSurface = 0.005f;

    /// <summary>
    /// The falloff at 512 units: <c>InvRSquared(Vector(0, 0, 512))</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A function-local <c>static const float</c> in the reference build, so it is computed
    /// once at first call and then fixed. 512 units is roughly a large room.
    /// </para>
    /// <para>
    /// It goes through <see cref="AmbientCube.InvRSquared"/> rather than
    /// <c>1 / 512^2</c>, and on x86 that is the <c>rcpss</c> ESTIMATE plus a
    /// <c>1e-10f</c> addend, not an exact reciprocal. The difference is about
    /// one part in 4,096, which only matters for a light sitting within that of
    /// the threshold -- but "only matters rarely" is how a parity gate fails on
    /// one map in ten, so the compliance switch decides it rather than a
    /// comment.
    /// </para>
    /// </remarks>
    public static float MinEmitSurfaceDistanceRatio(bool stockEstimate) =>
        stockEstimate
            ? AmbientCube.InvRSquaredStock(new Vec3(0, 0, 512))
            : AmbientCube.InvRSquared(new Vec3(0, 0, 512));

    /// <summary>
    /// Whether one light belongs in the ambient cubes.
    /// </summary>
    /// <param name="light">The light.</param>
    /// <param name="stockEstimate">
    /// Whether to use stock's <c>rcpss</c> estimate for the 512-unit falloff.
    /// </param>
    /// <returns>True when it should be baked.</returns>
    /// <remarks>
    /// Three conditions, all required: it must be an <c>emit_surface</c> light,
    /// it must be on lightstyle 0 (an animated light cannot be baked into a
    /// static cube), and it must be dim at 512 units. The intensity used is the
    /// largest of the three channels, computed as two nested maxima in stock's
    /// own order -- which matters only for NaN, and a NaN intensity is a
    /// malformed map either way.
    /// </remarks>
    public static bool IsAmbientCubeLight(ref readonly DWorldLight light, bool stockEstimate)
    {
        if (light.Type != (int)EmitType.Surface)
        {
            return false;
        }

        if (light.Style != 0)
        {
            return false;
        }

        float intensity = MathF.Max(light.Intensity.X, light.Intensity.Y);
        intensity = MathF.Max(intensity, light.Intensity.Z);

        return intensity * MinEmitSurfaceDistanceRatio(stockEstimate) < MinEmitSurface;
    }

    /// <summary>
    /// Sets or clears <c>DWL_FLAGS_INAMBIENTCUBE</c> across a light list.
    /// </summary>
    /// <param name="lights">The lights, updated in place.</param>
    /// <param name="stockEstimate">
    /// Whether to use stock's <c>rcpss</c> estimate for the 512-unit falloff.
    /// </param>
    /// <returns>
    /// How many lights were flagged, and how many <c>emit_surface</c> lights
    /// there were -- the two numbers stock prints.
    /// </returns>
    /// <remarks>
    /// IDEMPOTENT, and that is what makes re-running this stage on an
    /// already-lit map meaningful: the decision reads only the type, style and
    /// intensity, never the existing flag, so the flag is recomputed rather than
    /// accumulated. Stock relies on the same property -- its loop has an
    /// explicit <c>else</c> that clears.
    /// </remarks>
    public static (int Flagged, int SurfaceLights) Classify(
        Span<DWorldLight> lights, bool stockEstimate)
    {
        int flagged = 0;
        int surfaceLights = 0;

        for (int i = 0; i < lights.Length; i++)
        {
            ref DWorldLight wl = ref lights[i];

            if (IsAmbientCubeLight(in wl, stockEstimate))
            {
                wl.Flags |= (int)WorldLightFlags.InAmbientCube;
            }
            else
            {
                wl.Flags &= ~(int)WorldLightFlags.InAmbientCube;
            }

            if (wl.Type == (int)EmitType.Surface)
            {
                surfaceLights++;
            }

            if ((wl.Flags & (int)WorldLightFlags.InAmbientCube) != 0)
            {
                flagged++;
            }
        }

        return (flagged, surfaceLights);
    }
}
