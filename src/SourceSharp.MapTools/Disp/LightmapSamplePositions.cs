namespace SourceSharp.MapTools.Disp;

/// <summary>
/// Where each of a displacement's lightmap samples lands on its surface:
/// <c>CalculateLightmapSamplePositions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This lump is vrad's entire reason for reading a displacement's
/// tessellation.</b> A displacement's lightmap is a flat rectangle of luxels,
/// and vrad has to know, for each luxel, which triangle of the curved surface
/// it sits on and where inside that triangle. vbsp works that out once, here,
/// and writes it down; vrad then reads a triangle index and three barycentric
/// bytes per sample and never has to tessellate anything.
/// </para>
/// <para>
/// The encoding is VARIABLE LENGTH, which is the detail that catches a reader
/// out. A sample is normally four bytes — triangle index, then three
/// barycentric bytes — but a triangle index of 255 or more is written as the
/// byte 255 followed by <c>index - 255</c>, so those samples take FIVE. A
/// power-4 displacement has 512 triangles, so this is not a corner case: on
/// <c>p3f_disp_grid_mixed</c> 162 of one displacement's 324 samples take the
/// long form. There is no count anywhere; the only way to find sample
/// <c>n</c> is to walk from the start of the displacement's run.
/// </para>
/// <para>
/// A sample that lands in NO triangle is written as four zero bytes, which is
/// indistinguishable from a real sample on triangle 0 at barycentric
/// <c>(0,0,0)</c> — a point no triangle contains. vrad does not check.
/// </para>
/// </remarks>
public static class LightmapSamplePositions
{
    /// <summary>
    /// The byte that introduces a long-form triangle index.
    /// </summary>
    public const byte LongFormMarker = 255;

    /// <summary>
    /// Appends one displacement's samples to the LUMP_DISP_LIGHTMAP_SAMPLE_POSITIONS
    /// buffer.
    /// </summary>
    /// <param name="disp">The tessellated displacement.</param>
    /// <param name="lightmapSizeU">
    /// The base face's <c>m_LightmapTextureSizeInLuxels[0]</c>, which is
    /// <see cref="CoreDispSurface.LuxelU"/>.
    /// </param>
    /// <param name="lightmapSizeV">Its <c>[1]</c>.</param>
    /// <param name="output">The buffer to append to.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// The loop is <c>height + 1</c> by <c>width + 1</c> samples, because a
    /// lightmap of <c>n</c> luxels has <c>n + 1</c> sample positions along each
    /// axis, and each sample sits at the luxel's CENTRE — hence the
    /// <c>+ 0.5f</c>, which matches the <c>0.5</c> offsets
    /// <see cref="CoreDispSurface.CalcLuxelCoords"/> put on the corners.
    /// </remarks>
    public static void Append(
        CoreDispInfo disp, int lightmapSizeU, int lightmapSizeV, List<byte> output)
    {
        ArgumentNullException.ThrowIfNull(disp);
        ArgumentNullException.ThrowIfNull(output);

        int width = lightmapSizeU + 1;
        int height = lightmapSizeV + 1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                DispUv sample = new(x + 0.5f, y + 0.5f);

                if (!FindTriangleByUv(disp, sample, out int triangle, out Barycentric bary))
                {
                    output.Add(0);
                    output.Add(0);
                    output.Add(0);
                    output.Add(0);
                    continue;
                }

                if (triangle < LongFormMarker)
                {
                    output.Add((byte)triangle);
                }
                else
                {
                    output.Add(LongFormMarker);
                    output.Add((byte)(triangle - LongFormMarker));
                }

                // 255.9f and not 255.0f: stock scales so that a barycentric of
                // exactly 1 rounds to 255 rather than to 254, and the cast
                // truncates. A coordinate of 1.0 gives 255, and 0.996 gives 255
                // too -- the top of the range is deliberately coarse.
                output.Add((byte)(bary.A * 255.9f));
                output.Add((byte)(bary.B * 255.9f));
                output.Add((byte)(bary.C * 255.9f));
            }
        }
    }

    /// <summary>
    /// Finds the triangle whose luxel coordinates contain a sample:
    /// <c>FindTriIndexMapByUV</c>.
    /// </summary>
    /// <param name="disp">The displacement.</param>
    /// <param name="sample">The sample's luxel coordinate.</param>
    /// <param name="triangle">The triangle index, if one was found.</param>
    /// <param name="barycentric">Its barycentric coordinates in that triangle.</param>
    /// <returns>False when no triangle contains the sample.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>The triangles are the POWER INFO's, not the displacement's.</b>
    /// Stock's own <c>GetTriIndices</c> call is commented out at
    /// And replaced by a direct read of
    /// <c>pPowerInfo-&gt;m_pTriInfos[iTri]</c>, so the index written into this
    /// lump indexes <see cref="PowerInfo.TriInfos"/> — the quad tree's
    /// depth-first order — and NOT <see cref="CoreDispInfo.TriIndices"/>, which
    /// is the row-major order LUMP_DISP_TRIS is in. The two hold the same
    /// triangles in different sequences, so using the wrong one produces a
    /// lump of the right length with every sample on the wrong triangle.
    /// </para>
    /// <para>
    /// FIRST match wins and the search is linear from triangle 0. A sample on a
    /// shared edge belongs to whichever triangle the quad tree reached first,
    /// which is what makes the answer deterministic without a tie-break.
    /// </para>
    /// <para>
    /// The containment test is <c>0 &lt;= c &lt;= 1</c> on all three
    /// coordinates, which for coordinates that sum to one is equivalent to all
    /// three being non-negative — so a sample OUTSIDE every triangle fails
    /// because some coordinate is negative, never because one exceeds 1.
    /// </para>
    /// </remarks>
    public static bool FindTriangleByUv(
        CoreDispInfo disp, DispUv sample, out int triangle, out Barycentric barycentric)
    {
        ArgumentNullException.ThrowIfNull(disp);

        PowerInfo info = disp.PowerInfo;

        for (int i = 0; i < info.NumTriInfos; i++)
        {
            TriInfo tri = info.TriInfos[i];

            DispUv a = disp.LuxelCoord(0, tri.A);
            DispUv b = disp.LuxelCoord(0, tri.B);
            DispUv c = disp.LuxelCoord(0, tri.C);

            Barycentric bary = BarycentricCoords2D(a, b, c, sample);

            if (bary.A is >= 0.0f and <= 1.0f &&
                bary.B is >= 0.0f and <= 1.0f &&
                bary.C is >= 0.0f and <= 1.0f)
            {
                triangle = i;
                barycentric = bary;
                return true;
            }
        }

        triangle = -1;
        barycentric = default;
        return false;
    }

    /// <summary>
    /// <c>GetBarycentricCoords2D</c>.
    /// </summary>
    /// <param name="a">The triangle's first vertex.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <param name="point">The point to locate.</param>
    /// <returns>The three coordinates.</returns>
    /// <remarks>
    /// Each coordinate is a signed doubled area over the whole doubled area,
    /// so the factors of two cancel and the winding does not matter — a
    /// clockwise triangle negates both and gives the same answer. Stock's own
    /// comment claims the vertices are counter-clockwise; the arithmetic does
    /// not need them to be.
    /// </remarks>
    public static Barycentric BarycentricCoords2D(DispUv a, DispUv b, DispUv c, DispUv point)
    {
        float invTriArea = 1.0f / TriArea2DTimesTwo(a, b, c);

        return new Barycentric(
            TriArea2DTimesTwo(b, c, point) * invTriArea,
            TriArea2DTimesTwo(c, a, point) * invTriArea,
            TriArea2DTimesTwo(a, b, point) * invTriArea);
    }

    /// <summary>
    /// <c>TriArea2DTimesTwo</c>: the cross
    /// product of two edges, which is twice the signed area.
    /// </summary>
    /// <param name="a">The first vertex.</param>
    /// <param name="b">The second.</param>
    /// <param name="c">The third.</param>
    /// <returns>Twice the signed area.</returns>
    public static float TriArea2DTimesTwo(DispUv a, DispUv b, DispUv c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
}

/// <summary>A point's position inside a triangle, as three weights.</summary>
/// <param name="A">The weight of the triangle's first vertex.</param>
/// <param name="B">Its second.</param>
/// <param name="C">Its third.</param>
public readonly record struct Barycentric(float A, float B, float C);
