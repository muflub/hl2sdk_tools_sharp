using System.Globalization;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>
/// Looks up <c>$macro_texture</c> on a material, for
/// <c>DiscoverMacroTextures</c> (<c>writebsp.cpp:1164</c>).
/// </summary>
internal interface IMacroTextureResolver
{
    /// <summary>The material's <c>$macro_texture</c>, or null when it has none.</summary>
    /// <param name="materialName">The texdata's material name.</param>
    /// <returns>The macro texture name.</returns>
    string? MacroTextureOf(string materialName);
}

/// <summary>
/// The whole-lump passes <c>EndBSPFile</c> runs over the emitted faces:
/// vertex normals, lightmap extents and macro textures.
/// </summary>
internal static class FaceLumpStages
{
    /// <summary>
    /// <c>SaveVertexNormals</c> (<c>normals.cpp:12</c>): one flat normal per
    /// face, indexed once per surfedge.
    /// </summary>
    /// <param name="state">The emitted lumps.</param>
    internal static void SaveVertexNormals(BspWriteState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        (IReadOnlyList<Vec3> normals, IReadOnlyList<int> indices) =
            VertexNormals.Save(state.DrawFaces, state.Planes);

        state.VertNormals.Clear();
        state.VertNormals.AddRange(normals);
        state.VertNormalIndices.Clear();
        state.VertNormalIndices.AddRange(indices);
    }

    /// <summary>
    /// <c>UpdateAllFaceLightmapExtents</c> (<c>bsplib.cpp:3383</c>): the
    /// lightmap mins and size of every lit face.
    /// </summary>
    /// <param name="state">The emitted lumps.</param>
    /// <param name="texInfos">The (uncompacted) texinfo table.</param>
    /// <param name="materialNameOf">The material name of a texinfo, for the error.</param>
    /// <exception cref="MapCompileException">A face is too big to have a lightmap.</exception>
    internal static void UpdateAllFaceLightmapExtents(
        BspWriteState state, TexInfoTable texInfos, Func<int, string> materialNameOf)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(texInfos);
        ArgumentNullException.ThrowIfNull(materialNameOf);

        for (int i = 0; i < state.DrawFaces.Count; i++)
        {
            DFace face = state.DrawFaces[i];

            // non-lit texture
            if ((texInfos[face.TexInfo].Flags & (int)(SurfaceFlags.Sky | SurfaceFlags.NoLight)) != 0)
            {
                continue;
            }

            CalcFaceExtents(state, texInfos, ref face, materialNameOf);
            state.DrawFaces[i] = face;
        }
    }

    /// <summary><c>CalcFaceExtents</c> (<c>bsplib.cpp:3319</c>).</summary>
    internal static void CalcFaceExtents(
        BspWriteState state, TexInfoTable texInfos, ref DFace s, Func<int, string> materialNameOf)
    {
        Span<float> mins = [1e24f, 1e24f];
        Span<float> maxs = [-1e24f, -1e24f];

        TexInfo tex = texInfos[s.TexInfo];

        for (int i = 0; i < s.NumEdges; i++)
        {
            Vec3 v = FaceVertex(state, s, i);

            for (int j = 0; j < 2; j++)
            {
                // Four float products summed left to right, as the C does.
                float val = (v.X * tex.LightmapVecsLuxelsPerWorldUnits[(j * 4) + 0])
                    + (v.Y * tex.LightmapVecsLuxelsPerWorldUnits[(j * 4) + 1])
                    + (v.Z * tex.LightmapVecsLuxelsPerWorldUnits[(j * 4) + 2])
                    + tex.LightmapVecsLuxelsPerWorldUnits[(j * 4) + 3];

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

        int maxDim = s.DispInfo == -1
            ? WriteLimits.MaxLightmapDimWithoutBorder
            : WriteLimits.MaxDispLightmapDimWithoutBorder;

        for (int i = 0; i < 2; i++)
        {
            mins[i] = (float)Math.Floor(mins[i]);
            maxs[i] = (float)Math.Ceiling(maxs[i]);

            s.LightmapTextureMinsInLuxels[i] = (int)mins[i];
            s.LightmapTextureSizeInLuxels[i] = (int)(maxs[i] - mins[i]);

            if (s.LightmapTextureSizeInLuxels[i] > maxDim + 1)
            {
                Vec3 point = Vec3.Zero;
                for (int j = 0; j < s.NumEdges; j++)
                {
                    point += FaceVertex(state, s, j);
                }

                point *= 1.0f / s.NumEdges;
                throw new MapCompileException(WriteCodes.BadSurfaceExtents, string.Create(
                    CultureInfo.InvariantCulture,
                    $"Bad surface extents - surface is too big to have a lightmap\n\tmaterial "
                    + $"{materialNameOf(s.TexInfo)} around point ({point.X:F1} {point.Y:F1} {point.Z:F1})\n"
                    + $"\t(dimension: {i}, {s.LightmapTextureSizeInLuxels[i]}>{maxDim + 1})"));
            }
        }
    }

    /// <summary>
    /// <c>DiscoverMacroTextures</c> (<c>writebsp.cpp:1164</c>): one entry per
    /// face, the string-table index of its material's <c>$macro_texture</c> or
    /// 0xFFFF. Runs AFTER compaction and appends to the compacted string table.
    /// </summary>
    /// <param name="state">The emitted, compacted lumps.</param>
    /// <param name="resolver">The material lookup; null means no material has one.</param>
    internal static void DiscoverMacroTextures(BspWriteState state, IMacroTextureResolver? resolver)
    {
        ArgumentNullException.ThrowIfNull(state);

        List<TexInfo> texInfo = state.CompactedTexInfo
            ?? throw new InvalidOperationException("DiscoverMacroTextures runs after CompactTexinfos");
        List<DTexData> texData = state.CompactedTexData!;
        TexDataStringTable strings = state.CompactedStrings!;

        state.FaceMacroTextureInfos.Clear();
        foreach (DFace face in state.DrawFaces)
        {
            TexInfo t = texInfo[face.TexInfo];
            if (t.TexData < 0)
            {
                // CUtlVector::SetSize leaves the slot value-initialised.
                state.FaceMacroTextureInfos.Add(0);
                continue;
            }

            string material = strings.GetString(texData[t.TexData].NameStringTableId);
            string? macro = resolver?.MacroTextureOf(material);

            state.FaceMacroTextureInfos.Add(
                macro is null ? (ushort)0xFFFF : (ushort)strings.AddOrFind(macro));
        }
    }

    // A face's i'th vertex, through its surfedge (bsplib.cpp:3336-3339).
    private static Vec3 FaceVertex(BspWriteState state, DFace s, int i)
    {
        int e = state.SurfEdges[s.FirstEdge + i];
        DEdge edge = state.Edges.Edges[e >= 0 ? e : -e];
        return state.Vertices[e >= 0 ? edge.V[0] : edge.V[1]];
    }
}
