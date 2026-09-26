//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>CalcPoints</c> for brush faces: where a face's
/// light samples and luxels are.
/// </summary>
/// <remarks>
/// <para>
/// Two different grids come out of this, and confusing them is the classic
/// vrad misreading. <b>Luxels</b> are the regular lightmap grid, one per
/// texel of the lightmap page, at integer lightmap coordinates. <b>Samples</b>
/// are where light is actually gathered: each luxel's cell clipped to the face
/// polygon, so a luxel on the face's edge gets a sample at the centroid of the
/// part of its cell that is on the face, and a luxel entirely off the face gets
/// no sample at all. The radial filter (4f) maps samples back onto luxels.
/// </para>
/// <para>
/// Displacement faces are NOT handled here: stock routes them to
/// <c>StaticDispMgr()-&gt;BuildDispSamples</c>, which belongs to 4e. The caller
/// skips them.
/// </para>
/// </remarks>
public static class FaceSampleBuilder
{
    /// <summary>
    /// The capacity stock allocates for one face's samples before copying out:
    /// <c>SINGLE_BRUSH_MAP * 2</c>.
    /// </summary>
    public const int SampleCapacity = LightConstants.SingleBrushMap * 2;

    /// <summary>
    /// <c>worldAreaPerLuxel</c>: the world area one
    /// full luxel covers.
    /// </summary>
    /// <param name="tex">The face's texinfo.</param>
    /// <returns>
    /// <c>1.0 / (sqrt(s.s) * sqrt(t.t))</c>: two float square roots of float dot
    /// products, a float product, and a DOUBLE reciprocal narrowed into the
    /// float field.
    /// </returns>
    public static float WorldAreaPerLuxel(in TexInfo tex)
    {
        FloatArray8 l = tex.LightmapVecsLuxelsPerWorldUnits;
        float ss = (l[0] * l[0]) + (l[1] * l[1]) + (l[2] * l[2]);
        float tt = (l[4] * l[4]) + (l[5] * l[5]) + (l[6] * l[6]);
        float product = MathF.Sqrt(ss) * MathF.Sqrt(tt);
        return (float)(1.0 / product);
    }

    /// <summary>
    /// <c>CalcPoints</c>: fills a facelight's samples and luxels.
    /// </summary>
    /// <param name="geometry">The map.</param>
    /// <param name="info">The face's lighting frame.</param>
    /// <param name="faceLight">Receives the samples and luxels.</param>
    /// <param name="arena">Scratch windings; left as it was found.</param>
    /// <param name="fast">Stock's <c>-fast</c>: one full-luxel sample per luxel.</param>
    /// <param name="centerSamples">Stock's <c>-centersamples</c>.</param>
    /// <param name="keepPartialWindings">
    /// Stock's <c>do_extra</c> (not <c>-noextra</c>): keep the world-space
    /// winding of every partial sample for supersampling.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The face produced more than <see cref="SampleCapacity"/> samples, which
    /// stock would have written past the end of its buffer.
    /// </exception>
    public static void CalcPoints(
        LightGeometry geometry,
        FaceLightInfo info,
        FaceLight faceLight,
        WindingArena arena,
        bool fast,
        bool centerSamples,
        bool keepPartialWindings)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(faceLight);
        ArgumentNullException.ThrowIfNull(arena);

        // -fast builds samples and luxels together, on the
        // luxel grid, with no clipping at all.
        if (fast)
        {
            BuildFacesamplesAndLuxelsFast(geometry, info, faceLight);
            return;
        }

        BuildFacesamples(geometry, info, faceLight, arena, centerSamples, keepPartialWindings);
        BuildFaceLuxels(info, faceLight);
    }

    /// <summary>
    /// <c>BuildFacesamples</c>: the face's
    /// lightmap-space polygon cut into one piece per luxel cell.
    /// </summary>
    /// <param name="geometry">The map.</param>
    /// <param name="info">The face's lighting frame.</param>
    /// <param name="faceLight">Receives the samples.</param>
    /// <param name="arena">Scratch windings; left as it was found.</param>
    /// <param name="centerSamples">Stock's <c>-centersamples</c>.</param>
    /// <param name="keepPartialWindings">Stock's <c>do_extra</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">Too many samples.</exception>
    /// <remarks>
    /// <para>
    /// The cut is a sweep: row by row, the remaining polygon is clipped at
    /// <c>t + offset</c> and the BACK piece (smaller t) is then swept along s
    /// the same way; each BACK piece of that inner sweep is one sample. The
    /// offset is 1.0 -- so cell <c>t</c> spans (t, t+1] of what remains --
    /// unless <c>-centersamples</c> makes it 0.5.
    /// </para>
    /// <para>
    /// The clip epsilon is <c>ON_EPSILON / 16</c>, stock's lightmap-space
    /// "hack", and the planes are exactly axial, so
    /// <see cref="WindingArena.ClipEpsilon"/> snaps every new vertex onto the
    /// cut. Both loops stop as soon as nothing is left to cut.
    /// </para>
    /// <para>
    /// A sample's winding is kept, converted to world space, only when
    /// <paramref name="keepPartialWindings"/> is set and the sample is short of
    /// a full luxel by more than <c>EQUAL_EPSILON</c> -- a DOUBLE comparison
    /// </para>
    /// </remarks>
    public static void BuildFacesamples(
        LightGeometry geometry,
        FaceLightInfo info,
        FaceLight faceLight,
        WindingArena arena,
        bool centerSamples,
        bool keepPartialWindings)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(faceLight);
        ArgumentNullException.ThrowIfNull(arena);

        ref readonly DFace face = ref geometry.Faces[info.FaceNum];
        int width = info.Width;
        int height = info.Height;

        float worldAreaPerLuxel = WorldAreaPerLuxel(geometry.TexInfos[face.TexInfo]);
        faceLight.WorldAreaPerLuxel = worldAreaPerLuxel;

        List<LightSample> samples = new(Math.Min(width * height, SampleCapacity));
        List<Vec3> windingPoints = [];

        Winding lightmapWinding = info.LightmapCoordWinding(arena, geometry);

        Vec3 sNorm = new(1.0f, 0.0f, 0.0f);
        Vec3 tNorm = new(0.0f, 1.0f, 0.0f);

        // A double literal narrowed into a float.
        float sampleOffset = centerSamples ? 0.5f : 1.0f;

        for (int t = 0; t < height && !lightmapWinding.IsNull; t++)
        {
            float dist = t + sampleOffset;

            // Front is the rest of the face, back is this row.
            arena.ClipEpsilon(
                lightmapWinding, tNorm, dist, LightConstants.LightmapOnEpsilon,
                out Winding windingT1, out Winding windingT2);

            for (int s = 0; s < width && !windingT2.IsNull; s++)
            {
                dist = s + sampleOffset;

                arena.ClipEpsilon(
                    windingT2, sNorm, dist, LightConstants.LightmapOnEpsilon,
                    out Winding windingS1, out Winding windingS2);

                if (!windingS2.IsNull)
                {
                    if (samples.Count >= SampleCapacity)
                    {
                        throw new InvalidOperationException(
                            $"face {info.FaceNum} produced more than {SampleCapacity} light samples; "
                            + "stock writes past its SINGLE_BRUSH_MAP * 2 buffer here.");
                    }

                    samples.Add(MakeSample(
                        info, arena, windingS2, s, t, worldAreaPerLuxel, keepPartialWindings,
                        windingPoints));
                    arena.Free(windingS2);
                }

                // The row's remainder becomes the next s-cut's input.
                arena.Free(windingT2);
                windingT2 = windingS1;
            }

            arena.Free(lightmapWinding);
            if (!windingT2.IsNull)
            {
                arena.Free(windingT2);
            }

            lightmapWinding = windingT1;
        }

        if (!lightmapWinding.IsNull)
        {
            arena.Free(lightmapWinding);
        }

        // Every sample starts with the flat face normal; a smoothed
        // face's are replaced by BuildFacelights once the phong normals exist.
        LightSample[] result = [.. samples];
        for (int i = 0; i < result.Length; i++)
        {
            result[i].Normal = info.FaceNormal;
        }

        faceLight.Samples = result;
        faceLight.SampleWindingPoints = [.. windingPoints];
    }

    /// <summary>
    /// <c>BuildFaceLuxels</c>: one world position per
    /// lightmap texel, at integer lightmap coordinates.
    /// </summary>
    /// <param name="info">The face's lighting frame.</param>
    /// <param name="faceLight">Receives the luxels.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void BuildFaceLuxels(FaceLightInfo info, FaceLight faceLight)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(faceLight);

        int width = info.Width;
        int height = info.Height;
        Vec3[] luxels = new Vec3[width * height];
        for (int t = 0; t < height; t++)
        {
            for (int s = 0; s < width; s++)
            {
                luxels[s + (t * width)] = info.LuxelToWorld(s, t);
            }
        }

        faceLight.Luxels = luxels;
    }

    /// <summary>
    /// <c>BuildFacesamplesAndLuxels_DoFast</c>.
    /// </summary>
    /// <param name="geometry">The map.</param>
    /// <param name="info">The face's lighting frame.</param>
    /// <param name="faceLight">Receives samples and luxels.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// Every luxel becomes a sample of exactly one luxel's area, sitting ON the
    /// luxel -- including luxels whose centre is off the face, which is the
    /// price <c>-fast</c> pays. The bounds are <c>s +/- 0.5</c> ("unused but
    /// initialized anyway"), computed in double and narrowed.
    /// </remarks>
    public static void BuildFacesamplesAndLuxelsFast(
        LightGeometry geometry,
        FaceLightInfo info,
        FaceLight faceLight)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(faceLight);

        int width = info.Width;
        int height = info.Height;
        float worldAreaPerLuxel = WorldAreaPerLuxel(geometry.TexInfos[geometry.Faces[info.FaceNum].TexInfo]);
        faceLight.WorldAreaPerLuxel = worldAreaPerLuxel;

        LightSample[] samples = new LightSample[width * height];
        Vec3[] luxels = new Vec3[width * height];
        int k = 0;
        for (int t = 0; t < height; t++)
        {
            for (int s = 0; s < width; s++)
            {
                Vec3 pos = info.LuxelToWorld(s, t);
                samples[k] = new LightSample
                {
                    S = s,
                    T = t,
                    CoordS = s,
                    CoordT = t,
                    MinS = (float)(s - 0.5),
                    MinT = (float)(t - 0.5),
                    MaxS = (float)(s + 0.5),
                    MaxT = (float)(t + 0.5),
                    Area = worldAreaPerLuxel,
                    Position = pos,

                    // calloc'd in stock and never set on this path: the
                    // normal is ZERO until BuildFacelights fills it for a
                    // smoothed face. A flat face keeps the zero; nothing reads
                    // sample normals on a flat brush face.
                    Normal = Vec3.Zero,
                };
                luxels[k] = pos;
                k++;
            }
        }

        faceLight.Samples = samples;
        faceLight.Luxels = luxels;
        faceLight.SampleWindingPoints = [];
    }

    private static LightSample MakeSample(
        FaceLightInfo info,
        WindingArena arena,
        Winding cell,
        int s,
        int t,
        float worldAreaPerLuxel,
        bool keepPartialWindings,
        List<Vec3> windingPoints)
    {
        float area = arena.AreaAndBalancePoint(cell, out Vec3 center) * worldAreaPerLuxel;
        arena.Bounds(cell, out Vec3 mins, out Vec3 maxs);

        LightSample sample = new()
        {
            S = s,
            T = t,
            Area = area,
            CoordS = center.X,
            CoordT = center.Y,
            MinS = mins.X,
            MinT = mins.Y,
            MaxS = maxs.X,
            MaxT = maxs.Y,
            Position = info.LuxelToWorld(center.X, center.Y),
        };

        // Double arithmetic: EQUAL_EPSILON is a double literal.
        if (keepPartialWindings
            && area < worldAreaPerLuxel - LightConstants.EqualEpsilonDouble)
        {
            sample.WindingOffset = windingPoints.Count;
            foreach (Vec3 p in arena.Points(cell))
            {
                windingPoints.Add(info.LuxelToWorld(p.X, p.Y));
            }

            sample.WindingCount = windingPoints.Count - sample.WindingOffset;
        }

        return sample;
    }
}
