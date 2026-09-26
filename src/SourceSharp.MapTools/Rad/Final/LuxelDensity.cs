//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad.Final;

/// <summary>
/// <c>-luxeldensity</c>: the head of <c>RadWorld_Start</c>
/// Which caps every texinfo's luxels-per-unit and
/// recomputes every face's lightmap extents to match.
/// </summary>
/// <remarks>
/// <para>
/// It edits the map itself, and the edit is written out with the lighting --
/// stock's own FIXME says "since this writes out to the BSP file every run,
/// once it's set high it can't be reset to a lower value". So it runs here as
/// a pass over the map's bytes before anything is loaded from them.
/// </para>
/// <para>
/// Only the three direction components of each lightmap axis are rescaled; the
/// offset (column 3) is left alone, and the extents recomputation re-anchors
/// the grid, so that is not a defect. <c>oldLightmapVecs</c> is filled and
/// never read (dead code, nothing to reproduce).
/// </para>
/// </remarks>
public static class LuxelDensity
{
    /// <summary>
    /// <c>MAX_LIGHTMAP_DIM_WITHOUT_BORDER</c>, which this
    /// SDK defines as the displacement limit.
    /// </summary>
    public const int MaxLightmapDimWithoutBorder = 125;

    /// <summary><c>MAX_DISP_LIGHTMAP_DIM_WITHOUT_BORDER</c>.</summary>
    public const int MaxDispLightmapDimWithoutBorder = 125;

    /// <summary>
    /// The density stock compares against: <c>-luxeldensity n</c> stores
    /// <c>1/n</c> when <c>n &gt; 1</c>.
    /// </summary>
    /// <param name="typed">What was typed (<see cref="VradOptions.LuxelDensity"/>).</param>
    /// <returns>The effective density; below 1 means the map is rewritten.</returns>
    public static float Effective(float typed) => typed > 1.0 ? (float)(1.0 / typed) : typed;

    /// <summary>Rescales the map's lightmap axes and face extents.</summary>
    /// <param name="bsp">The map, changed in place.</param>
    /// <param name="density">The effective density, below 1.</param>
    /// <param name="hdr">Whether the pass about to run lights the HDR faces.</param>
    /// <param name="compliance">
    /// Selects <see cref="StockQuirk.VradVectorNormalise"/> for the axis length,
    /// and <see cref="StockQuirk.LuxelDensityLeavesHdrFacesStale"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="MapCompileException">A face's new extents are too big for a lightmap.</exception>
    public static void Apply(BspData bsp, float density, bool hdr, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(compliance);

        bool stockNormalise = compliance.Emulates(StockQuirk.VradVectorNormalise);

        byte[] texBytes = bsp[BspLump.TexInfo].Data.ToArray();
        Span<TexInfo> texinfo = MemoryMarshal.Cast<byte, TexInfo>(texBytes.AsSpan());

        // An HDR pass lights a copy of the LDR faces taken BEFORE this runs
        // (precedes RadWorld_Start); see the quirk.
        if (hdr && bsp[BspLump.FacesHdr].IsEmpty && StaleHdrFaces(compliance))
        {
            bsp.SetLump(BspLump.FacesHdr, bsp[BspLump.Faces].Data.ToArray(), bsp[BspLump.Faces].Version);
        }

        for (int i = 0; i < texinfo.Length; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                int b = j * 4;
                Vec3 tmp = new(
                    texinfo[i].LightmapVecsLuxelsPerWorldUnits[b],
                    texinfo[i].LightmapVecsLuxelsPerWorldUnits[b + 1],
                    texinfo[i].LightmapVecsLuxelsPerWorldUnits[b + 2]);

                (Vec3 unit, float scale) = stockNormalise ? tmp.NormaliseLikeStock() : tmp.Normalise();

                if (MathF.Abs(scale) > density)
                {
                    scale = scale < 0 ? -density : density;
                    Vec3 scaled = unit * scale;
                    texinfo[i].LightmapVecsLuxelsPerWorldUnits[b] = scaled.X;
                    texinfo[i].LightmapVecsLuxelsPerWorldUnits[b + 1] = scaled.Y;
                    texinfo[i].LightmapVecsLuxelsPerWorldUnits[b + 2] = scaled.Z;
                }
            }
        }

        bsp.SetLump(BspLump.TexInfo, texBytes, bsp[BspLump.TexInfo].Version);

        // UpdateAllFaceLightmapExtents: dfaces ONLY.
        UpdateExtents(bsp, BspLump.Faces, texinfo);
        if (!bsp[BspLump.FacesHdr].IsEmpty && !StaleHdrFaces(compliance))
        {
            UpdateExtents(bsp, BspLump.FacesHdr, texinfo);
        }
    }

    /// <summary>
    /// Whether the HDR faces keep their old extents.
    /// </summary>
    /// <param name="compliance">The policy.</param>
    /// <returns>True under stock.</returns>
    /// <remarks>
    /// <c>UpdateAllFaceLightmapExtents</c> walks <c>dfaces</c>, but an HDR pass
    /// lights <c>dfaces_hdr</c>, copied from <c>dfaces</c> before
    /// <c>RadWorld_Start</c>. So a <c>-hdr</c> run with <c>-luxeldensity</c>
    /// lights faces whose extents belong to the OLD lightmap axes: every
    /// sample lands in the wrong place and the luxel counts disagree with the
    /// LDR faces. <c>-both</c> hides it, because its HDR pass reloads a map
    /// whose axes the LDR pass already capped. Correct updates both lumps.
    /// </remarks>
    public static bool StaleHdrFaces(ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);
        return compliance.Emulates(StockQuirk.LuxelDensityLeavesHdrFacesStale);
    }

    /// <summary><c>CalcFaceExtents</c> over one face lump.</summary>
    private static void UpdateExtents(BspData bsp, BspLump lump, ReadOnlySpan<TexInfo> texinfo)
    {
        byte[] faceBytes = bsp[lump].Data.ToArray();
        Span<DFace> faces = MemoryMarshal.Cast<byte, DFace>(faceBytes.AsSpan());
        ReadOnlySpan<Vec3> vertexes = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);
        const int texSpecial = (int)(SurfaceFlags.Sky | SurfaceFlags.NoLight);

        for (int f = 0; f < faces.Length; f++)
        {
            ref DFace s = ref faces[f];
            ref readonly TexInfo tex = ref texinfo[s.TexInfo];
            if ((tex.Flags & texSpecial) != 0)
            {
                continue;
            }

            Span<float> mins = [1e24f, 1e24f];
            Span<float> maxs = [-1e24f, -1e24f];

            for (int i = 0; i < s.NumEdges; i++)
            {
                int e = surfEdges[s.FirstEdge + i];
                Vec3 v = e >= 0 ? vertexes[edges[e].V[0]] : vertexes[edges[-e].V[1]];

                for (int j = 0; j < 2; j++)
                {
                    int b = j * 4;
                    float val = (v.X * tex.LightmapVecsLuxelsPerWorldUnits[b])
                        + (v.Y * tex.LightmapVecsLuxelsPerWorldUnits[b + 1])
                        + (v.Z * tex.LightmapVecsLuxelsPerWorldUnits[b + 2])
                        + tex.LightmapVecsLuxelsPerWorldUnits[b + 3];
                    if (val < mins[j])
                    {
                        mins[j] = val;
                    }

                    if (val > maxs[j])
                    {
                        maxs[j] = val;
                    }
                }
            }

            int maxDim = s.DispInfo == -1 ? MaxLightmapDimWithoutBorder : MaxDispLightmapDimWithoutBorder;
            for (int i = 0; i < 2; i++)
            {
                mins[i] = (float)Math.Floor(mins[i]);
                maxs[i] = (float)Math.Ceiling(maxs[i]);

                s.LightmapTextureMinsInLuxels[i] = (int)mins[i];
                s.LightmapTextureSizeInLuxels[i] = (int)(maxs[i] - mins[i]);
                if (s.LightmapTextureSizeInLuxels[i] > maxDim + 1)
                {
                    throw new MapCompileException(
                        $"Bad surface extents - surface {f} is too big to have a lightmap "
                        + $"(dimension: {i}, {s.LightmapTextureSizeInLuxels[i]}>{maxDim + 1})");
                }
            }
        }

        bsp.SetLump(lump, faceBytes, bsp[lump].Version);
    }
}
