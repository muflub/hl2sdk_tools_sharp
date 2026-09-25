using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// A displacement face's light samples and luxels: <c>BuildDispSamples</c>,
/// <c>BuildDispLuxels</c> and <c>BuildDispSamplesAndLuxels_DoFast</c>
/// Which <c>CalcPoints</c>
/// Calls in place of the flat-face builders.
/// </summary>
/// <remarks>
/// <para>
/// A displacement's lightmap is its own grid of <c>w x h</c> luxels laid over
/// the surface's (u, v) square, not a projection of the base face: the luxel
/// at column <c>s</c> is the surface point at <c>u = s / (w - 1)</c>. Samples
/// are the CENTRES of <c>w</c> equal cells: the sample at <c>s</c> is at
/// <c>u = (s + 0.5) / w</c>, and its area is that of the quad of surface
/// points around it.
/// </para>
/// <para>
/// Every sample and luxel is taken on the surface and pushed one unit off it
/// along the triangle's normal (<c>DispUVToSurfPoint(..., 1.0f)</c>); the
/// winding corners are on the surface (push 0).
/// </para>
/// </remarks>
public static class DispSampleBuilder
{
    /// <summary>
    /// <c>CalcPoints</c> for a displacement face: the samples and luxels, fast
    /// or full.
    /// </summary>
    /// <param name="surface">The displacement.</param>
    /// <param name="face">Its base face (for the lightmap size).</param>
    /// <param name="tex">The face's texinfo (for the fast path's sample area).</param>
    /// <param name="faceLight">Receives samples, luxels and luxel normals.</param>
    /// <param name="fast"><c>-fast</c>.</param>
    /// <param name="compliance">The compile's compliance.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public static void CalcPoints(
        VradDispSurface surface, in DFace face, in TexInfo tex, FaceLight faceLight, bool fast, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(faceLight);
        ArgumentNullException.ThrowIfNull(compliance);

        int width = face.LightmapTextureSizeInLuxels[0] + 1;
        int height = face.LightmapTextureSizeInLuxels[1] + 1;
        if (fast)
        {
            BuildSamplesAndLuxelsFast(surface, width, height, tex, faceLight, compliance);
            return;
        }

        BuildSamples(surface, width, height, faceLight);
        BuildLuxels(surface, width, height, faceLight);
    }

    /// <summary>
    /// <c>BuildDispSamples</c>.
    /// </summary>
    /// <param name="surface">The displacement.</param>
    /// <param name="width">Luxels across (<c>m_LightmapTextureSizeInLuxels[0] + 1</c>).</param>
    /// <param name="height">Luxels down.</param>
    /// <param name="faceLight">Receives <see cref="FaceLight.Samples"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// A grid of <c>(w + 1) x (h + 1)</c> surface points at <c>u = i / w</c>
    /// gives each sample a four-point winding, whose
    /// <c>WindingArea</c> is the sample's area. The sample
    /// itself is at the cell centre, <c>u = i / w + 1 / (2w)</c>, pushed one
    /// unit off the surface, with the blended vertex normal.
    /// </para>
    /// <para>
    /// The winding is kept only for stock's <c>-dumppatches</c> output and for
    /// supersampling, which displacements never get,
    /// so it is not stored.
    /// </para>
    /// </remarks>
    public static void BuildSamples(VradDispSurface surface, int width, int height, FaceLight faceLight)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(faceLight);

        float stepU = 1.0f / width;
        float stepV = 1.0f / height;
        float halfStepU = stepU * 0.5f;
        float halfStepV = stepV * 0.5f;

        int pointsAcross = width + 1;
        Vec3[] world = new Vec3[pointsAcross * (height + 1)];
        for (int v = 0; v < height + 1; v++)
        {
            for (int u = 0; u < pointsAcross; u++)
            {
                surface.DispUVToSurfPoint(u * stepU, v * stepV, 0.0f, ref world[(v * pointsAcross) + u]);
            }
        }

        LightSample[] samples = new LightSample[width * height];
        for (int v = 0; v < height; v++)
        {
            for (int u = 0; u < width; u++)
            {
                // p0 (u, v), p1 (u, v+1), p2 (u+1, v+1), p3 (u+1, v).
                Vec3 p0 = world[(v * pointsAcross) + u];
                Vec3 p1 = world[((v + 1) * pointsAcross) + u];
                Vec3 p2 = world[((v + 1) * pointsAcross) + u + 1];
                Vec3 p3 = world[(v * pointsAcross) + u + 1];

                ref LightSample s = ref samples[(v * width) + u];
                s.Area = QuadArea(p0, p1, p2, p3);
                s.S = u;
                s.T = v;
                s.CoordS = (u * stepU) + halfStepU;
                s.CoordT = (v * stepV) + halfStepV;
                surface.DispUVToSurfPoint(s.CoordS, s.CoordT, 1.0f, ref s.Position);
                surface.DispUVToSurfNormal(s.CoordS, s.CoordT, ref s.Normal);
            }
        }

        faceLight.Samples = samples;
    }

    /// <summary>
    /// <c>BuildDispLuxels</c>: one luxel per
    /// lightmap texel, at <c>u = s / (w - 1)</c>, pushed one unit off the
    /// surface, with its blended normal.
    /// </summary>
    /// <param name="surface">The displacement.</param>
    /// <param name="width">Luxels across.</param>
    /// <param name="height">Luxels down.</param>
    /// <param name="faceLight">Receives <see cref="FaceLight.Luxels"/> and <see cref="FaceLight.LuxelNormals"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void BuildLuxels(VradDispSurface surface, int width, int height, FaceLight faceLight)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(faceLight);

        Vec3[] luxels = new Vec3[width * height];
        Vec3[] normals = new Vec3[width * height];
        float stepU = 1.0f / (width - 1);
        float stepV = 1.0f / (height - 1);
        for (int v = 0; v < height; v++)
        {
            for (int u = 0; u < width; u++)
            {
                int i = (v * width) + u;
                float cu = u * stepU;
                float cv = v * stepV;
                surface.DispUVToSurfPoint(cu, cv, 1.0f, ref luxels[i]);
                surface.DispUVToSurfNormal(cu, cv, ref normals[i]);
            }
        }

        faceLight.Luxels = luxels;
        faceLight.LuxelNormals = normals;
    }

    /// <summary>
    /// <c>BuildDispSamplesAndLuxels_DoFast</c>:
    /// one sample per luxel, and the luxel IS the sample.
    /// </summary>
    /// <param name="surface">The displacement.</param>
    /// <param name="width">Luxels across.</param>
    /// <param name="height">Luxels down.</param>
    /// <param name="tex">The face's texinfo.</param>
    /// <param name="faceLight">Receives samples, luxels and luxel normals.</param>
    /// <param name="compliance">The compile's compliance.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Two stock defects, both behind a switch.</b>
    /// </para>
    /// <para>
    /// <see cref="StockQuirk.DispFastSamplesPastEdge"/>: stock places sample
    /// <c>s</c> at <c>u = s / (w - 1) + 1 / (2 (w - 1))</c> -- the
    /// full path's half-step offset on the LUXEL grid. Every sample is half a
    /// luxel off its luxel, and the last column and row land at
    /// <c>u = 1 + half-step</c>, off the surface: <c>DispUVToSurfPoint</c> and
    /// <c>DispUVToSurfNormal</c> return without writing (<c>,
    /// 326</c>), the position and normal stay the <c>calloc</c> zeros, a zero
    /// normal takes no light, and every displacement's last row and column of
    /// luxels is black in a <c>-fast</c> compile -- the "black seams" defect the
    /// reference build's changelog mentions. Measured on all 18 p3f-t maps (stock <c>-fast</c> and
    /// <c>-fast -bounce 0</c>): the last row and column of every displacement
    /// lightmap is exactly zero, and the full-path compile of the same map has
    /// none. Correct samples each luxel at its own position, <c>u = s / (w - 1)</c>,
    /// which is where <see cref="BuildLuxels"/> puts it and where the flat
    /// <c>-fast</c> path samples.
    /// </para>
    /// <para>
    /// <see cref="StockQuirk.DispFastSampleAreaZero"/>: the samples come from
    /// <c>calloc</c> and the fast path never sets their area, so
    /// <c>AddSampleToPatch</c> credits nothing to a displacement's patches
    /// (add <c>area * light</c> and <c>area</c>)
    /// and displacements reflect no light in a <c>-fast</c> compile with
    /// bounces. The flat fast path sets <c>worldAreaPerLuxel</c>
    ///Correct does the same here.
    /// </para>
    /// </remarks>
    public static void BuildSamplesAndLuxelsFast(
        VradDispSurface surface, int width, int height, in TexInfo tex, FaceLight faceLight, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(faceLight);
        ArgumentNullException.ThrowIfNull(compliance);

        bool stockOffset = compliance.Emulates(StockQuirk.DispFastSamplesPastEdge);
        bool stockArea = compliance.Emulates(StockQuirk.DispFastSampleAreaZero);
        float area = stockArea ? 0.0f : FaceSampleBuilder.WorldAreaPerLuxel(tex);

        LightSample[] samples = new LightSample[width * height];
        Vec3[] luxels = new Vec3[width * height];
        Vec3[] normals = new Vec3[width * height];
        float stepU = 1.0f / (width - 1);
        float stepV = 1.0f / (height - 1);
        float halfStepU = stockOffset ? stepU * 0.5f : 0.0f;
        float halfStepV = stockOffset ? stepV * 0.5f : 0.0f;

        for (int v = 0; v < height; v++)
        {
            for (int u = 0; u < width; u++)
            {
                int i = (v * width) + u;
                ref LightSample s = ref samples[i];
                s.S = u;
                s.T = v;
                s.CoordS = (u * stepU) + halfStepU;
                s.CoordT = (v * stepV) + halfStepV;
                s.Area = area;

                // Off the surface, both calls leave the zeros (see remarks).
                surface.DispUVToSurfPoint(s.CoordS, s.CoordT, 1.0f, ref s.Position);
                surface.DispUVToSurfNormal(s.CoordS, s.CoordT, ref s.Normal);

                luxels[i] = s.Position;
                normals[i] = s.Normal;
            }
        }

        faceLight.Samples = samples;
        faceLight.Luxels = luxels;
        faceLight.LuxelNormals = normals;
    }

    /// <summary>
    /// <c>WindingArea</c> of a four-point winding: the
    /// fan from point 0, cross lengths summed, halved once.
    /// </summary>
    /// <param name="p0">Point 0.</param>
    /// <param name="p1">Point 1.</param>
    /// <param name="p2">Point 2.</param>
    /// <param name="p3">Point 3.</param>
    /// <returns>The area.</returns>
    public static float QuadArea(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3)
    {
        float total = 0f;
        total += Vec3.Cross(p1 - p0, p2 - p0).Length();
        total += Vec3.Cross(p2 - p0, p3 - p0).Length();
        return total * 0.5f;
    }
}
