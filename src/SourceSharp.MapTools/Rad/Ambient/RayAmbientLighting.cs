//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <c>CalcRayAmbientLighting</c>: what colour
/// one ray brings back.
/// </summary>
/// <remarks>
/// <para>
/// The stage's inner loop, run 162 times per sample and up to 128 samples per
/// leaf. It is a CLOSEST-HIT query followed by a lightmap read: the ray is
/// traced against the map's own BSP, and the surface it landed on is sampled --
/// either at the exact luxel, or as the face's precomputed average, or blended
/// between them.
/// </para>
/// <para>
/// WHY THE BLEND EXISTS, in stock's own words: the ray is really a cone
/// (<see cref="VertexNormals.ConeInnerAngleRadians"/> wide), and sampling every
/// luxel the cone covers would need surface-neighbour information vrad does not
/// compute. So it approximates -- point sample up close, face average far away,
/// linear between 20 and 40 units of cone radius. That is an approximation, not
/// a defect: there is no "correct" version to branch to, so it is reproduced
/// unconditionally.
/// </para>
/// <para>
/// A NOTE ON THE SKY PATH that matters for anyone optimising this. When the ray
/// ends on a sky face the tracer reports fraction 1 rather than the distance to
/// The sky (never touches
/// <c>m_HitFrac</c>), which would make the cone enormous -- but the same path
/// forces <c>scaleAvg</c> to 1, so the cone radius is never read. Changing
/// either half alone changes the map.
/// </para>
/// </remarks>
public static class RayAmbientLighting
{
    /// <summary>How many lightstyle slots a face can carry (<c>MAXLIGHTMAPS</c>).</summary>
    public const int MaxLightmaps = 4;

    /// <summary>How many lightstyles exist (<c>MAX_LIGHTSTYLES</c>).</summary>
    public const int MaxLightStyles = 64;

    /// <summary><c>SURF_SKY</c>.</summary>
    public const int SurfSky = 0x0004;

    /// <summary><c>SURF_NOLIGHT</c>.</summary>
    public const int SurfNoLight = 0x0400;

    /// <summary>The style value meaning "this slot is unused".</summary>
    public const byte StyleUnused = 255;

    /// <summary>
    /// Adds one ray's contribution to a per-lightstyle accumulator.
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <param name="start">Where the ray starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="tanTheta">The tangent of the cone's inner half-angle.</param>
    /// <param name="skyAmbient">
    /// What a sky hit contributes, or null when the map has no
    /// <c>emit_skyambient</c> light. See <see cref="FindSkyAmbient"/>.
    /// </param>
    /// <param name="color">
    /// Accumulates, per lightstyle. Not cleared: stock's caller clears it once
    /// and then fires every ray into the same array.
    /// </param>
    /// <param name="scratch">The work item's displacement scratch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> is null.</exception>
    /// <remarks>
    /// The ray is traced with a MINIMUM DISTANCE OF ZERO, which is stock's
    /// behaviour and is worth stating because it is measurably load-bearing: a
    /// sample position is only guaranteed 1/32 of a unit clear of the leaf's own
    /// planes, so it can be standing on the wall it traces from, and the
    /// x-leafambient prototype measured 1,769 differing hit bits in 6.3 M rays
    /// at epsilon 0 against 19 at 1e-3. Raising it here would be a quieter map
    /// and a different one.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Accumulate(
        AmbientScene scene,
        Vec3 start,
        Vec3 end,
        float tanTheta,
        Vec3? skyAmbient,
        Span<Vec3> color,
        DispTestedScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // Ray_t::Init( vStart, vEnd ): m_Delta = end - start.
        Vec3 delta = end - start;

        AmbientHit hit = scene.Tracer.Trace(start, delta, scratch);
        if (!hit.IsHit)
        {
            return;
        }

        int face = hit.Surface;

        // The cone's radius where it meets the surface.
        float dist = delta.Length() * tanTheta * hit.Fraction;

        // RemapValClamped(dist, 20, 40, 0, 1).
        float scaleAvg = RemapValClamped(dist, 20.0f, 40.0f, 0.0f, 1.0f);

        // M_bHasLuxel is false only on the sky path.
        if (!hit.HasLuxel)
        {
            scaleAvg = 1.0f;
        }

        float scaleSample = 1.0f - scaleAvg;

        if (scaleAvg != 0)
        {
            AddFaceAverage(scene, face, skyAmbient, scaleAvg, color);
        }

        if (scaleSample != 0)
        {
            AddFacePointSample(scene, face, skyAmbient, hit.LuxelS, hit.LuxelT, scaleSample, color);
        }
    }

    /// <summary>
    /// What a sky hit contributes, read off the map's own world lights.
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <returns>
    /// The first <c>emit_skyambient</c> light's intensity, or null when there is
    /// none.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Stock's <c>FindAmbientSkyLight</c> walks
    /// the <c>activelights</c> list -- vrad's own parsed lights, which this port
    /// does not have at this stage -- and returns the first
    /// <c>emit_skyambient</c>, caching it in a function-local static for the
    /// life of the process.
    /// </para>
    /// <para>
    /// <b>The world-light lump is the same list, in the same order.</b>
    /// <c>ExportDirectLightsToWorldLights</c> walks
    /// <c>activelights</c> head to tail and appends one <c>dworldlight_t</c> per
    /// entry, every type included, so "first <c>emit_skyambient</c> in
    /// <c>activelights</c>" and "first <c>SkyAmbient</c> in
    /// <c>LUMP_WORLDLIGHTS</c>" name the same light.
    /// </para>
    /// <para>
    /// <b>And it is already divided by 255.</b> The export scales
    /// <c>dl-&gt;light.intensity</c> by <c>1/255</c>, which is exactly the
    /// division <c>ComputeLightmapColorFromAverage</c> applies to the value it
    /// reads off the <c>directlight_t</c>. So this returns the lump's intensity
    /// unmodified and the caller must not divide again -- doing so was the first
    /// version of this function and it made every sky-lit ambient cube 255 times
    /// too dark.
    /// </para>
    /// </remarks>
    public static Vec3? FindSkyAmbient(AmbientScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        ReadOnlySpan<DWorldLight> lights = scene.WorldLights;
        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i].Type == (int)EmitType.SkyAmbient)
            {
                return lights[i].Intensity;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>ComputeLightmapColorFromAverage</c>.
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <param name="face">The face hit.</param>
    /// <param name="skyAmbient">The sky ambient intensity, or null.</param>
    /// <param name="scale">How much of this sample to take.</param>
    /// <param name="color">Accumulates, per lightstyle.</param>
    /// <remarks>
    /// A SKY face short-circuits to the sky ambient and returns, so it
    /// contributes to style 0 only and never reads a lightmap. Every other face
    /// walks its style slots until one reads 255; each slot's decoded, tinted
    /// average is precomputed per face by <see cref="AmbientScene"/>.
    /// </remarks>
    private static void AddFaceAverage(
        AmbientScene scene,
        int face,
        Vec3? skyAmbient,
        float scale,
        Span<Vec3> color)
    {
        ref readonly FaceShade s = ref scene.Shade(face);
        if (s.Sky)
        {
            if (skyAmbient is { } amb)
            {
                color[0] += amb * scale;
            }

            return;
        }

        if (s.BadAverages)
        {
            throw new InvalidOperationException(
                $"face {face} has lightstyles but its averages lie outside the lightmap lump "
                + "(lightofs " + s.LightOfs + "); stock would read memory before the lump");
        }

        for (int maps = 0; maps < s.StyleCount; maps++)
        {
            color[s.Style(maps)] += scene.AverageTinted(face, maps) * scale;
        }
    }

    /// <summary>
    /// <c>ComputeLightmapColorPointSample</c>.
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <param name="face">The face hit.</param>
    /// <param name="skyAmbient">The sky ambient intensity, or null.</param>
    /// <param name="s">The luxel coordinate, s.</param>
    /// <param name="t">The luxel coordinate, t.</param>
    /// <param name="scale">How much of this sample to take.</param>
    /// <param name="color">Accumulates, per lightstyle.</param>
    /// <remarks>
    /// <para>
    /// The style slots are strided: slot <c>n</c>'s samples begin
    /// <c>n * smax * tmax</c> after slot 0's, times four again on a bumped
    /// Surface.
    /// </para>
    /// <para>
    /// A face with <c>lightofs == -1</c> returns immediately.
    /// Otherwise each slot goes through <c>ComputeAmbientFromSurface</c>
    /// Whose SKY branch REPLACES the colour with the sky ambient
    /// (or leaves it untinted when there is none) -- reachable only for a lit
    /// sky face in a leaf, and reproduced so it is not a trap.
    /// </para>
    /// </remarks>
    private static void AddFacePointSample(
        AmbientScene scene,
        int face,
        Vec3? skyAmbient,
        float s,
        float t,
        float scale,
        Span<Vec3> color)
    {
        ref readonly FaceShade shade = ref scene.Shade(face);
        if (shade.LightOfs == -1)
        {
            return;
        }

        int ds = Math.Clamp((int)s, 0, shade.Smax - 1);
        int dt = Math.Clamp((int)t, 0, shade.Tmax - 1);
        int index = (dt * shade.Smax) + ds;

        ReadOnlySpan<byte> light = scene.LightData;
        for (int maps = 0; maps < shade.StyleCount; maps++)
        {
            int offset = shade.LightOfs + ((index + (maps * shade.Stride)) * 4);
            if ((uint)offset > (uint)(light.Length - 4))
            {
                throw new InvalidOperationException(
                    $"face {face}'s luxel ({ds},{dt}) slot {maps} lies outside the lightmap lump");
            }

            ColorRgbExp32 sample = BspStructView.As<ColorRgbExp32>(light.Slice(offset, 4))[0];
            Vec3 c = StockLightColor.TexLightToLinear(sample);

            if (shade.Sky)
            {
                if (skyAmbient is { } amb)
                {
                    c = amb;
                }
            }
            else
            {
                Vec3 r = shade.Reflectivity;
                c = new Vec3(c.X * r.X, c.Y * r.Y, c.Z * r.Z);
            }

            color[shade.Style(maps)] += c * scale;
        }
    }

    /// <summary>
    /// <c>RemapValClamped</c>.
    /// </summary>
    /// <param name="val">The value.</param>
    /// <param name="a">The input range's start.</param>
    /// <param name="b">The input range's end.</param>
    /// <param name="c">The output range's start.</param>
    /// <param name="d">The output range's end.</param>
    /// <returns>The remapped, clamped value.</returns>
    /// <remarks>
    /// A DEGENERATE input range returns <paramref name="d"/>, not
    /// <paramref name="c"/> and not a midpoint. Neither caller in this lane can
    /// reach it -- both pass literal ranges -- but a reader checking the port
    /// against the header should not have to guess.
    /// </remarks>
    internal static float RemapValClamped(float val, float a, float b, float c, float d)
    {
        if (a == b)
        {
            return d;
        }

        float cVal = (val - a) / (b - a);
        cVal = Math.Clamp(cVal, 0.0f, 1.0f);

        return c + ((d - c) * cVal);
    }
}
