using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;

namespace SourceSharp.MapTools.Rad.Props;

/// <summary>
/// <c>ComputeIndirectLightingAtPoint</c>: the
/// bounced light a point sees, gathered from the final lightmaps.
/// </summary>
/// <remarks>
/// <para>
/// Hemispherical Halton directions (a quarter of 162 when forced fast, which
/// every static-prop caller does), each traced with <c>CLightSurface</c> --
/// the same BSP walk leaf ambient uses -- and the lightmap at the hit,
/// attenuated by the inverse square of the distance in 128-unit steps and
/// tinted by the surface's reflectivity. Sky hits and unlit faces contribute
/// nothing. The sum is divided by the total dot.
/// </para>
/// <para>
/// Under <see cref="StockQuirk.IndirectSurfaceEnumeratorReused"/> the one
/// enumerator stock creates carries its <c>m_HitFrac</c> from ray to ray.
/// </para>
/// </remarks>
public static class PropIndirectLighting
{
    /// <summary><c>(0.7071/2)</c>, a double narrowed to the float <c>dot</c>.</summary>
    private const float IgnoreNormalsDot = (float)(0.7071 / 2);

    /// <summary><c>EQUAL_EPSILON</c>, a double.</summary>
    private const double EqualEpsilon = 0.001;

    /// <summary>The indirect colour at a point.</summary>
    /// <param name="scene">The map, for this pass.</param>
    /// <param name="position">The point.</param>
    /// <param name="normal">Its normal.</param>
    /// <param name="forceFast"><c>force_fast</c> (or <c>do_fast</c>): a quarter of the samples.</param>
    /// <param name="ignoreNormals">Use a constant dot instead of the normal's.</param>
    /// <param name="scratch">The work item's displacement scratch.</param>
    /// <param name="compliance">Which defects to reproduce.</param>
    /// <param name="staticPropIndirectMode">The ++ <c>-StaticPropIndirectMode</c>: 0 stock, 1 inverse-square from the accumulated hit, 2 keep-reflection, other raw.</param>
    /// <returns>The colour, in vrad's 0..255 lightmap scale.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static Vec3 Compute(
        AmbientScene scene,
        Vec3 position,
        Vec3 normal,
        bool forceFast,
        bool ignoreNormals,
        DispTestedScratch scratch,
        ComplianceOptions compliance,
        int staticPropIndirectMode = 0)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(compliance);

        bool reuse = compliance.Emulates(StockQuirk.IndirectSurfaceEnumeratorReused);

        int nSamples = 162;
        if (forceFast)
        {
            nSamples /= 4;
        }

        Vec3 outColor = Vec3.Zero;
        float totalDot = 0;
        DirectionalSampler sampler = new();
        AmbientHit state = new(-1, 1.0f, false, 0, 0);

        for (int j = 0; j < nSamples; j++)
        {
            Vec3 samplingNormal = sampler.NextValue();
            float dot = ignoreNormals
                ? IgnoreNormalsDot
                : (normal.X * samplingNormal.X) + (normal.Y * samplingNormal.Y) + (normal.Z * samplingNormal.Z);

            if ((double)dot <= EqualEpsilon)
            {
                continue;
            }

            totalDot += dot;

            // VectorScale( samplingNormal, MAX_TRACE_LENGTH, vEnd ); VectorAdd( position, vEnd, vEnd ).
            Vec3 vEnd = new(
                samplingNormal.X * PropLightSampler.MaxTraceLength,
                samplingNormal.Y * PropLightSampler.MaxTraceLength,
                samplingNormal.Z * PropLightSampler.MaxTraceLength);
            vEnd = position + vEnd;

            if (!reuse)
            {
                state = new AmbientHit(-1, 1.0f, false, 0, 0);
            }

            if (!scene.Tracer.TraceFrom(position, vEnd - position, scratch, ref state))
            {
                continue;
            }

            ref readonly FaceShade shade = ref scene.Shade(state.Surface);
            if (shade.Sky)
            {
                continue;
            }

            if (shade.Style0 == RayAmbientLighting.StyleUnused || shade.LightOfs < 0)
            {
                continue;
            }

            Vec3 lightmapColor;
            if (!state.HasLuxel)
            {
                lightmapColor = ToVector(scene.AverageLightColor(in scene.Faces[state.Surface], 0));
            }
            else
            {
                int ds = Math.Clamp((int)state.LuxelS, 0, shade.Smax - 1);
                int dt = Math.Clamp((int)state.LuxelT, 0, shade.Tmax - 1);
                lightmapColor = ToVector(scene.LightSamples(shade.LightOfs + (((dt * shade.Smax) + ds) * 4), 1)[0]);
            }

            // The weighting is the ++ -StaticPropIndirectMode switch, one
            // consumer: ComputeIndirectLightingAtPoint
            // counterpart), branches at
 // all.c (== 0), 46478 (== 1), 46496 (== 2), fallthrough
            // The gate global is.
            //
            // ((vEnd - position) * m_HitFrac / 128.0).LengthSqr(): a Vector
            // times a float, then divided by 128 (VectorDivide: times 1/128).
            // Mode 1 replaces the traced-ray vector with the TRUE hit point
            // (hit - position), still scaled by the fraction over 128
            // (the offset scalar times the stored axis unit vector; hit = position +
            // (vEnd - position) * fraction), which for the port's tracer is
            // (vEnd - position) * fraction^2 / 128 -- a strictly smaller d,
            // hence a weight nearer 1.
            Vec3 d = vEnd - position;
            float fraction = state.Fraction;
            if (staticPropIndirectMode == 1)
            {
                fraction *= fraction;
            }

            d = new Vec3(d.X * fraction, d.Y * fraction, d.Z * fraction);
            const float Inv128 = 1.0f / 128.0f;
            d = new Vec3(d.X * Inv128, d.Y * Inv128, d.Z * Inv128);
            float invLengthSqr = staticPropIndirectMode switch
            {
        // Mode 2 drops the inverse-square entirely:
            // weight 1, reflectivity kept.
                2 => 1.0f,
                // Modes 0 and 1: 1/(1+|d|^2), the 1.0f baseline.
                _ => 1.0f / (1.0f + d.LengthSquared()),
            };

            if (staticPropIndirectMode is < 0 or > 2)
            {
                // Out of range takes NONE of the weighting branches
 // (all.c): the raw lightmap triple accumulates.
                // no weight, no reflectivity.
                outColor += lightmapColor;
                continue;
            }

            // VectorMultiply( lightmapColor, invLengthSqr * reflectivity, lightmapColor ).
            Vec3 r = shade.Reflectivity;
            Vec3 scale = new(r.X * invLengthSqr, r.Y * invLengthSqr, r.Z * invLengthSqr);
            lightmapColor = new Vec3(lightmapColor.X * scale.X, lightmapColor.Y * scale.Y, lightmapColor.Z * scale.Z);
            outColor += lightmapColor;
        }

        if (totalDot != 0)
        {
            float inv = 1.0f / totalDot;
            outColor = new Vec3(outColor.X * inv, outColor.Y * inv, outColor.Z * inv);
        }

        return outColor;
    }

    /// <summary><c>ColorRGBExp32ToVector</c>: 255 times the linear value.</summary>
    private static Vec3 ToVector(MapFormats.Bsp.Structs.ColorRgbExp32 c) => new(
        255.0f * StockLightColor.TexLightToLinear(c.R, c.Exponent),
        255.0f * StockLightColor.TexLightToLinear(c.G, c.Exponent),
        255.0f * StockLightColor.TexLightToLinear(c.B, c.Exponent));
}
