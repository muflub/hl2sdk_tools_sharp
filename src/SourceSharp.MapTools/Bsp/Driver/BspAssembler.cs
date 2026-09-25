using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Bsp.Write;

namespace SourceSharp.MapTools.Bsp.Driver;

/// <summary>
/// The lump half of <c>WriteBSPFile</c>: the
/// <c>d*</c> arrays into a <see cref="BspData"/>. The byte layout — lump order,
/// padding, game-lump directory — is <see cref="BspFile"/>'s.
/// </summary>
internal static class BspAssembler
{
    /// <summary><c>GAMELUMP_STATIC_PROPS_VERSION</c>.</summary>
    internal const int StaticPropsVersion = 10;

    /// <summary><c>GAMELUMP_DETAIL_PROPS_VERSION</c>.</summary>
    internal const int DetailPropsVersion = 4;

    /// <summary>Places every lump the write stage owns.</summary>
    /// <param name="state">The finished, compacted arrays.</param>
    /// <param name="bsp">The file to fill.</param>
    /// <param name="mapRevision">The VMF's <c>mapversion</c>.</param>
    internal static void Assemble(BspWriteState state, BspData bsp, int mapRevision)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bsp);

        bsp.MapRevision = mapRevision;

        Put(bsp, BspLump.Planes, state.Planes, 0);
        Put(bsp, BspLump.Leafs, state.Leafs, 1);
        Put(bsp, BspLump.Vertexes, state.Vertices.Vertexes, 0);
        Put(bsp, BspLump.Nodes, state.Nodes, 0);
        Put(bsp, BspLump.TexInfo, state.CompactedTexInfo!, 0);
        Put(bsp, BspLump.TexData, state.CompactedTexData!, 0);
        Put(bsp, BspLump.FaceMacroTextureInfo, state.FaceMacroTextureInfos, 0);

        PrimitiveTable prims = state.Faces.Primitives;
        Put(bsp, BspLump.Primitives, prims.Primitives, 0);
        Put(bsp, BspLump.PrimVerts, prims.Vertices, 0);
        Put(bsp, BspLump.PrimIndices, prims.Indices, 0);

        Put(bsp, BspLump.Faces, state.DrawFaces, 1);
        Put(bsp, BspLump.FaceIds, state.FaceIds, 0);
        Put(bsp, BspLump.OriginalFaces, state.OrigFaces, 0);
        Put(bsp, BspLump.Brushes, state.Brushes, 0);
        Put(bsp, BspLump.BrushSides, state.BrushSides, 0);
        Put(bsp, BspLump.LeafFaces, state.LeafFaces, 0);
        Put(bsp, BspLump.LeafBrushes, state.LeafBrushes, 0);
        Put(bsp, BspLump.SurfEdges, state.SurfEdges, 0);
        Put(bsp, BspLump.Edges, state.Edges.Edges, 0);
        Put(bsp, BspLump.Models, state.Models, 0);
        Put(bsp, BspLump.Areas, state.Areas, 0);
        Put(bsp, BspLump.AreaPortals, state.AreaPortals, 0);

        bsp.SetLump(BspLump.Entities, state.EntityData ?? [0]);
        Put(bsp, BspLump.LeafWaterData, state.LeafWaterData, 0);

        OcclusionLump occlusion = new();
        occlusion.Occluders.AddRange(state.Occluders);
        occlusion.Polys.AddRange(state.OccluderPolys);
        occlusion.VertexIndices.AddRange(state.OccluderVertexIndices);
        bsp[BspLump.Occlusion] = occlusion.Write();

        // dflagslump_t: g_LevelFlags, which vbsp never sets.
        bsp.SetLump(BspLump.MapFlags, new byte[4]);

        Put(bsp, BspLump.ClipPortalVerts, state.ClipPortalVerts, 0);

        TexDataStringTable strings = state.CompactedStrings!;
        bsp.SetLump(BspLump.TexDataStringData, strings.ToData());
        Put(bsp, BspLump.TexDataStringTable, strings.Offsets, 0);

        Put(bsp, BspLump.VertNormals, state.VertNormals, 0);
        // g_vertnormalindices is unsigned short, not int.
        ushort[] normalIndices = new ushort[state.VertNormalIndices.Count];
        for (int i = 0; i < normalIndices.Length; i++)
        {
            normalIndices[i] = (ushort)state.VertNormalIndices[i];
        }

        Put(bsp, BspLump.VertNormalIndices, normalIndices, 0);

        // ClearDistToClosestWater: zero for every leaf until vvis.
        bsp.SetLump(BspLump.LeafMinDistToWater, new byte[state.Leafs.Count * sizeof(ushort)]);

        // Minimal-but-valid defaults for the lumps other lanes own; an
        // extension at VbspExtensionPoint.WriteFile replaces them.
        bsp.GameLumps.Clear();
        bsp.GameLumps.Add(new GameLumpEntry(
            GameLumpEntry.MakeId("sprp"), 0, StaticPropsVersion, new byte[12]));
        bsp.GameLumps.Add(new GameLumpEntry(
            GameLumpEntry.MakeId("dprp"), 0, DetailPropsVersion, new byte[12]));

        bsp.SetLump(BspLump.PakFile, new ZipArchiveWriter().ToBytes());
    }

    private static void Put<T>(BspData bsp, BspLump lump, IReadOnlyList<T> items, int version)
        where T : unmanaged
    {
        T[] array = items is List<T> list ? [.. CollectionsMarshal.AsSpan(list)] : [.. items];
        bsp[lump] = BspStructView.ToLump<T>(array, version);
    }
}
