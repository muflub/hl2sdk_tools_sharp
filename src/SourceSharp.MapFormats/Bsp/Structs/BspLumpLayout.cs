//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Bsp.Structs;

/// <summary>
/// Which struct each lump is an array of, and how big one element is.
/// </summary>
/// <remarks>
/// <para>
/// This table is the cheap, total check on every struct in this namespace: a
/// real map's lump length must be a whole number of its element size, and a
/// struct whose size is off by the four bytes a forgotten pad would cost
/// almost never divides an 11 MB map's lump cleanly. So the table is not a
/// convenience, it is the thing the layout gate is written against.
/// </para>
/// <para>
/// Lumps that are NOT arrays -- visibility, occlusion, the game lump, the
/// embedded zip, the cooked physics blobs -- return null rather than a size of
/// one, so that "this lump has no fixed element" is a distinct answer from
/// "this lump is bytes".
/// </para>
/// </remarks>
public static class BspLumpLayout
{
    /// <summary>
    /// The size in bytes of one element of <paramref name="lump"/>.
    /// </summary>
    /// <param name="lump">Which lump.</param>
    /// <param name="lumpVersion">
    /// The LUMP's version from the file header, not the file's version. Only
    /// <see cref="BspLump.Leafs"/> reads it, and that is the entire reason this
    /// parameter exists: <c>dleaf_t</c> is 56 bytes at version 0 and 32 at
    /// version 1, and nothing else in the file says which.
    /// </param>
    /// <returns>
    /// The element size, or null when the lump is not an array of a fixed
    /// struct.
    /// </returns>
    public static int? ElementSize(BspLump lump, int lumpVersion) => lump switch
    {
        // Text, NUL-terminated. One byte per element by construction.
        BspLump.Entities => 1,
        BspLump.Planes => Unsafe.SizeOf<DPlane>(),
        BspLump.TexData => Unsafe.SizeOf<DTexData>(),

        // A dvertex_t is a bare Vector, so Vec3 IS the struct.
        BspLump.Vertexes => Unsafe.SizeOf<Vec3>(),

        // dvis_t is a header plus a ragged bit-vector region.
        BspLump.Visibility => null,
        BspLump.Nodes => Unsafe.SizeOf<DNode>(),
        BspLump.TexInfo => Unsafe.SizeOf<TexInfo>(),
        BspLump.Faces => Unsafe.SizeOf<DFace>(),
        BspLump.Lighting => Unsafe.SizeOf<ColorRgbExp32>(),

        // Three counted runs of three different structs; see OcclusionLump.
        BspLump.Occlusion => null,
        BspLump.Leafs => lumpVersion == 0
            ? Unsafe.SizeOf<DLeafVersion0>()
            : Unsafe.SizeOf<DLeaf>(),
        BspLump.FaceIds => Unsafe.SizeOf<DFaceId>(),
        BspLump.Edges => Unsafe.SizeOf<DEdge>(),

        // Signed edge indices: bare ints.
        BspLump.SurfEdges => sizeof(int),
        BspLump.Models => Unsafe.SizeOf<DModel>(),
        BspLump.WorldLights => Unsafe.SizeOf<DWorldLight>(),
        BspLump.LeafFaces => sizeof(ushort),
        BspLump.LeafBrushes => sizeof(ushort),
        BspLump.Brushes => Unsafe.SizeOf<DBrush>(),
        BspLump.BrushSides => Unsafe.SizeOf<DBrushSide>(),
        BspLump.Areas => Unsafe.SizeOf<DArea>(),
        BspLump.AreaPortals => Unsafe.SizeOf<DAreaPortal>(),

        // The reference layout never assigns them, so nothing is known about them.
        BspLump.Unused0 or BspLump.Unused1 or BspLump.Unused2 or BspLump.Unused3 => null,
        BspLump.DispInfo => Unsafe.SizeOf<DispInfo>(),
        BspLump.OriginalFaces => Unsafe.SizeOf<DFace>(),

        // A dphysdisp_t header then two ragged runs.
        BspLump.PhysDisp => null,

        // dphysmodel_t records, each followed by a vphysics blob of its own size.
        BspLump.PhysCollide => null,
        BspLump.VertNormals => Unsafe.SizeOf<Vec3>(),
        BspLump.VertNormalIndices => sizeof(ushort),
        BspLump.DispLightmapAlphas => 1,
        BspLump.DispVerts => Unsafe.SizeOf<DispVert>(),

        // Run-length coded bytes, per the format.
        BspLump.DispLightmapSamplePositions => 1,

        // A nested directory with absolute offsets.
        BspLump.GameLump => null,
        BspLump.LeafWaterData => Unsafe.SizeOf<DLeafWaterData>(),
        BspLump.Primitives => Unsafe.SizeOf<DPrimitive>(),
        BspLump.PrimVerts => Unsafe.SizeOf<Vec3>(),
        BspLump.PrimIndices => sizeof(ushort),

        // A zip archive.
        BspLump.PakFile => null,
        BspLump.ClipPortalVerts => Unsafe.SizeOf<Vec3>(),
        BspLump.Cubemaps => Unsafe.SizeOf<DCubemapSample>(),
        BspLump.TexDataStringData => 1,
        BspLump.TexDataStringTable => sizeof(int),
        BspLump.Overlays => Unsafe.SizeOf<DOverlay>(),
        BspLump.LeafMinDistToWater => sizeof(ushort),
        BspLump.FaceMacroTextureInfo => Unsafe.SizeOf<FaceMacroTextureInfo>(),
        BspLump.DispTris => Unsafe.SizeOf<DispTri>(),

        // Deprecated win32 Havok terrain compression. Nothing in this tree
        // writes it and no struct for it survives, but maps older than its
        // removal (dm_lockdown.bsp among them) still carry it, so it is opaque
        // bytes to preserve rather than an array to read.
        BspLump.PhysCollideSurface => null,
        BspLump.WaterOverlays => Unsafe.SizeOf<DWaterOverlay>(),
        BspLump.LeafAmbientIndexHdr => Unsafe.SizeOf<DLeafAmbientIndex>(),
        BspLump.LeafAmbientIndex => Unsafe.SizeOf<DLeafAmbientIndex>(),
        BspLump.LightingHdr => Unsafe.SizeOf<ColorRgbExp32>(),
        BspLump.WorldLightsHdr => Unsafe.SizeOf<DWorldLight>(),
        // Version-dependent, exactly like Leafs above, and for a sharper
        // reason: at any version but 1 the reference loader casts the SAME LUMP
        // to CompressedLightCube (24 bytes) rather than dleafambientlighting_t
        // (28), and asserts the length divides by THAT. Answering 28
        // unconditionally makes a legacy map's lump look misaligned when it is
        // correct.
        //
        // One caveat this signature cannot express: the reference loader takes
        // that legacy branch when the version is not 1 *OR* when the matching
        // ambient INDEX lump is empty. A caller that knows whether the index
        // is empty should decide from that as well; the version alone is the
        // best this function can see.
        BspLump.LeafAmbientLightingHdr => lumpVersion == 1
            ? Unsafe.SizeOf<DLeafAmbientLighting>()
            : Unsafe.SizeOf<CompressedLightCube>(),
        BspLump.LeafAmbientLighting => lumpVersion == 1
            ? Unsafe.SizeOf<DLeafAmbientLighting>()
            : Unsafe.SizeOf<CompressedLightCube>(),

        // Deprecated Xbox 1 xzip pak.
        BspLump.XZipPakFile => null,
        BspLump.FacesHdr => Unsafe.SizeOf<DFace>(),
        BspLump.MapFlags => Unsafe.SizeOf<DFlagsLump>(),
        BspLump.OverlayFades => Unsafe.SizeOf<DOverlayFade>(),
        _ => null,
    };

    /// <summary>
    /// The version the reference writer records for this lump in a freshly
    /// compiled map.
    /// </summary>
    /// <param name="lump">Which lump.</param>
    /// <returns>The version, which is zero for every lump not in that enum.</returns>
    /// <remarks>
    /// <para>
    /// The value is part of the format rather than a running counter: a map
    /// whose LEAFS version is 0 is not an out-of-date map, it is a map with
    /// 56-byte leaves.
    /// </para>
    /// <para>
    /// LUMP_ORIGINALFACES is deliberately NOT here even though it holds
    /// <see cref="DFace"/>. The reference writer passes
    /// <c>LUMP_FACES_VERSION</c> for LUMP_FACES and LUMP_FACES_HDR and lets
    /// LUMP_ORIGINALFACES default to zero, and <c>dm_lockdown.bsp</c> records
    /// exactly that. Versioning it "consistently" would be inventing a format.
    /// </para>
    /// </remarks>
    public static int CurrentVersion(BspLump lump) => lump switch
    {
        BspLump.Lighting => 1,
        BspLump.LightingHdr => 1,
        BspLump.Faces => 1,
        BspLump.FacesHdr => 1,
        BspLump.Occlusion => 2,
        BspLump.Leafs => 1,
        BspLump.LeafAmbientLighting => 1,
        BspLump.LeafAmbientLightingHdr => 1,
        _ => 0,
    };
}
