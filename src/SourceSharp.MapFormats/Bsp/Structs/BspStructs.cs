using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Bsp.Structs;

// The lump structs of src/public/bspfile.h, one C# struct per C struct, all
// blittable so a lump's bytes can be REINTERPRETED rather than copied field by
// field (see BspStructView).
//
// Every struct here is [StructLayout(Sequential, Pack = 1)] with any padding
// the C++ compiler would insert written out as an explicit field. Pack = 1
// alone would be WRONG -- C pads dleaf_t from 30 bytes to 32 and dnode_t from
// 30 to 32 -- so the pads are named, documented, and pinned by facts rather
// than left to the runtime's own packing rules.

/// <summary>
/// One entry of the 64-slot lump directory (<c>bspfile.h:374</c>,
/// <c>struct lump_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LumpHeader
{
    /// <summary>Byte offset of the lump's payload from the start of the file.</summary>
    public int FileOfs;

    /// <summary>The payload's length in bytes.</summary>
    public int FileLen;

    /// <summary>The lump's version. Zero unless listed in <c>bspfile.h:360</c>.</summary>
    public int Version;

    /// <summary>
    /// Zero when the lump is stored uncompressed. Non-zero means LZMA and is
    /// the decompressed size.
    /// </summary>
    /// <remarks>
    /// This field used to be <c>char fourCC[4]</c> and was repurposed
    /// (<c>bspfile.h:379</c>), so a very old map can carry junk here.
    /// </remarks>
    public int UncompressedSize;
}

/// <summary>
/// The header of a standalone lump file, <c>.lmp</c> (<c>bspfile.h:404</c>,
/// <c>struct lumpfileheader_t</c>).
/// </summary>
/// <remarks>
/// Note this is NOT <see cref="LumpHeader"/> plus a field: the order differs
/// and it carries the map revision so the engine can reject a lump file
/// written against a different compile.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LumpFileHeader
{
    /// <summary>Byte offset of the payload within the lump file.</summary>
    public int LumpOffset;

    /// <summary>Which of the 64 lumps this file replaces.</summary>
    public int LumpId;

    /// <summary>The lump's version.</summary>
    public int LumpVersion;

    /// <summary>The payload's length in bytes.</summary>
    public int LumpLength;

    /// <summary>The map revision the lump file was written against.</summary>
    public int MapRevision;
}

/// <summary>
/// The whole-level feature flags of LUMP_MAP_FLAGS (<c>bspfile.h:398</c>,
/// <c>struct dflagslump_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DFlagsLump
{
    /// <summary>A combination of <see cref="LevelFlags"/>.</summary>
    public uint LevelFlags;
}

/// <summary>
/// The values <see cref="DFlagsLump.LevelFlags"/> takes
/// (<c>bspfile.h:395</c>).
/// </summary>
[Flags]
public enum LevelFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>
    /// vrad ran with <c>-staticproplighting</c> and there is no HDR data.
    /// </summary>
    BakedStaticPropLightingNonHdr = 0x00000001,

    /// <summary>
    /// vrad ran with <c>-staticproplighting</c> and the data is HDR.
    /// </summary>
    BakedStaticPropLightingHdr = 0x00000002,
}

/// <summary>
/// One entry of the game lump's nested directory (<c>bspfile.h:429</c>,
/// <c>struct dgamelump_t</c>).
/// </summary>
/// <remarks>
/// <see cref="FileOfs"/> is an offset from the start of the FILE, not from the
/// start of the game lump, which is why the game lump cannot be relocated
/// without rewriting this table. <see cref="GameLumpEntry"/> is the decoded
/// form the rest of the port uses; this struct exists so the on-disk table can
/// be read and written as bytes.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DGameLump
{
    /// <summary>The four-character code, packed as <see cref="GameLumpEntry.MakeId"/> packs it.</summary>
    public int Id;

    /// <summary>1 when the payload is LZMA compressed (<c>GAMELUMPFLAG_COMPRESSED</c>).</summary>
    public ushort Flags;

    /// <summary>The nested lump's own version.</summary>
    public ushort Version;

    /// <summary>Byte offset of the payload from the start of the FILE.</summary>
    public int FileOfs;

    /// <summary>The payload's length in bytes.</summary>
    public int FileLen;
}

/// <summary>
/// One brush model: the world (index 0) or a brush entity
/// (<c>bspfile.h:441</c>, <c>struct dmodel_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DModel
{
    /// <summary>The model's bounding box minimum.</summary>
    public Vec3 Mins;

    /// <summary>The model's bounding box maximum.</summary>
    public Vec3 Maxs;

    /// <summary>The origin used for sound and light placement.</summary>
    public Vec3 Origin;

    /// <summary>The index of this model's root node in LUMP_NODES.</summary>
    public int HeadNode;

    /// <summary>The first face of this model's face run.</summary>
    public int FirstFace;

    /// <summary>
    /// How many faces the run holds. Submodels draw these directly rather than
    /// walking the tree (<c>bspfile.h:447</c>).
    /// </summary>
    public int NumFaces;
}

/// <summary>
/// One entry of LUMP_PHYSCOLLIDE: the framing around one model's cooked
/// collision blob (<c>bspfile.h:450</c>, <c>struct dphysmodel_t</c>).
/// </summary>
/// <remarks>
/// The blob that follows is vphysics' own format and is not decoded here, for
/// the same reason vrad does not decode it: it belongs to the physics library.
/// A terminator record has <see cref="ModelIndex"/> of -1 and zeroes elsewhere.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DPhysModel
{
    /// <summary>Which <see cref="DModel"/> this collision data belongs to, or -1 to terminate.</summary>
    public int ModelIndex;

    /// <summary>The length in bytes of the solids that follow the header.</summary>
    public int DataSize;

    /// <summary>The length in bytes of the trailing text key data.</summary>
    public int KeydataSize;

    /// <summary>How many solids <see cref="DataSize"/> covers.</summary>
    public int SolidCount;
}

/// <summary>
/// The header of LUMP_PHYSDISP (<c>bspfile.h:460</c>,
/// <c>struct dphysdisp_t</c>).
/// </summary>
/// <remarks>
/// Followed immediately by <c>unsigned short dataSize[numDisplacements]</c>
/// and then the blobs themselves, which is why the C++ declares the array as a
/// comment: the lump is a header plus two variable-length runs, not an array of
/// this struct.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DPhysDisp
{
    /// <summary>How many per-displacement size entries follow.</summary>
    public ushort NumDisplacements;
}

/// <summary>
/// One world plane (<c>bspfile.h:475</c>, <c>struct dplane_t</c>).
/// </summary>
/// <remarks>
/// Planes come in opposite pairs: <c>(x&amp;~1)</c> and <c>(x&amp;~1)+1</c> are
/// always each other's flip (<c>bspfile.h:473</c>), so the lump always holds an
/// even count.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DPlane
{
    /// <summary>The plane's unit normal.</summary>
    public Vec3 Normal;

    /// <summary>The plane's distance along its normal from the origin.</summary>
    public float Dist;

    /// <summary>
    /// The axial classification, <c>PLANE_X</c> through <c>PLANE_ANYZ</c>.
    /// Trivially regenerable, and the header says so.
    /// </summary>
    public int Type;
}

/// <summary>
/// One interior node of the BSP tree (<c>bspfile.h:486</c>,
/// <c>struct dnode_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DNode
{
    /// <summary>The splitting plane's index in LUMP_PLANES.</summary>
    public int PlaneNum;

    /// <summary>
    /// The two children. A NEGATIVE value is a leaf, encoded as
    /// <c>-(leafIndex + 1)</c> (<c>bspfile.h:490</c>) -- so -1 is leaf 0 and
    /// there is no way to spell "no child".
    /// </summary>
    public IntArray2 Children;

    /// <summary>The node's integer bounding box minimum, for frustum culling.</summary>
    public ShortArray3 Mins;

    /// <summary>The node's integer bounding box maximum.</summary>
    public ShortArray3 Maxs;

    /// <summary>The first face of this node's face run.</summary>
    public ushort FirstFace;

    /// <summary>How many faces, counting both sides of the plane.</summary>
    public ushort NumFaces;

    /// <summary>
    /// The area index when every leaf below this node shares one area, and -1
    /// when they do not (<c>bspfile.h:495</c>).
    /// </summary>
    public short Area;

    /// <summary>
    /// Trailing padding. The fields above total 30 bytes; <c>int planenum</c>
    /// gives the C struct 4-byte alignment, so the compiler rounds it to 32.
    /// Written out because <c>Pack = 1</c> would otherwise produce a 30-byte
    /// struct and misalign every node after the first.
    /// </summary>
    public short Padding;
}

/// <summary>
/// One face's texture mapping (<c>bspfile.h:499</c>, <c>struct texinfo_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TexInfo
{
    /// <summary>
    /// The texture-space axes, <c>float[2][4]</c> flattened: index
    /// <c>(row * 4) + column</c>, row 0 the s axis and row 1 the t axis,
    /// columns 0..2 the axis xyz and column 3 its offset.
    /// </summary>
    public FloatArray8 TextureVecsTexelsPerWorldUnits;

    /// <summary>The lightmap-space axes, in the same flattened shape.</summary>
    public FloatArray8 LightmapVecsLuxelsPerWorldUnits;

    /// <summary>Surface flags: <c>SURF_*</c> from <c>bspflags.h</c>.</summary>
    public int Flags;

    /// <summary>The index into LUMP_TEXDATA naming the material.</summary>
    public int TexData;
}

/// <summary>
/// One material as the map uses it (<c>bspfile.h:510</c>,
/// <c>struct dtexdata_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DTexData
{
    /// <summary>
    /// The material's average colour, copied by vbsp out of the VTF header.
    /// </summary>
    /// <remarks>
    /// This is what makes the VTF header's reflectivity field directly
    /// checkable: vbsp reads it from the texture and writes it here verbatim,
    /// so a wrong VTF header layout shows up as a wrong number in this lump.
    /// </remarks>
    public Vec3 Reflectivity;

    /// <summary>Index into LUMP_TEXDATA_STRING_TABLE for the material's name.</summary>
    public int NameStringTableId;

    /// <summary>The source image's width in pixels.</summary>
    public int Width;

    /// <summary>The source image's height in pixels.</summary>
    public int Height;

    /// <summary>The width actually used for texture mapping.</summary>
    public int ViewWidth;

    /// <summary>The height actually used for texture mapping.</summary>
    public int ViewHeight;
}

/// <summary>
/// One occluder (<c>bspfile.h:529</c>, <c>struct doccluderdata_t</c>), as
/// LUMP_OCCLUSION version 2 stores it.
/// </summary>
/// <remarks>
/// This struct is NOT the whole lump. The occlusion lump is three counted runs
/// rather than an array; see <see cref="OcclusionLump"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DOccluderData
{
    /// <summary><see cref="OccluderFlags"/>.</summary>
    public int Flags;

    /// <summary>The first polygon of this occluder's run.</summary>
    public int FirstPoly;

    /// <summary>How many polygons the run holds.</summary>
    public int PolyCount;

    /// <summary>The occluder's bounding box minimum.</summary>
    public Vec3 Mins;

    /// <summary>The occluder's bounding box maximum.</summary>
    public Vec3 Maxs;

    /// <summary>
    /// The visibility area the occluder sits in. Present only at lump version
    /// 2; version 1 stops after <see cref="Maxs"/>
    /// (<c>bspfile.h:540</c>, <c>doccluderdataV1_t</c>).
    /// </summary>
    public int Area;
}

/// <summary>
/// One occluder as LUMP_OCCLUSION version 1 stored it
/// (<c>bspfile.h:540</c>, <c>struct doccluderdataV1_t</c>): the same fields
/// without <see cref="DOccluderData.Area"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DOccluderDataV1
{
    /// <summary><see cref="OccluderFlags"/>.</summary>
    public int Flags;

    /// <summary>The first polygon of this occluder's run.</summary>
    public int FirstPoly;

    /// <summary>How many polygons the run holds.</summary>
    public int PolyCount;

    /// <summary>The occluder's bounding box minimum.</summary>
    public Vec3 Mins;

    /// <summary>The occluder's bounding box maximum.</summary>
    public Vec3 Maxs;
}

/// <summary>The values <see cref="DOccluderData.Flags"/> takes (<c>bspfile.h:524</c>).</summary>
[Flags]
public enum OccluderFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The occluder starts switched off.</summary>
    Inactive = 0x1,
}

/// <summary>
/// One occluder polygon (<c>bspfile.h:549</c>,
/// <c>struct doccluderpolydata_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DOccluderPolyData
{
    /// <summary>The first index of this polygon's run in the vertex-index array.</summary>
    public int FirstVertexIndex;

    /// <summary>How many vertices the polygon has.</summary>
    public int VertexCount;

    /// <summary>The polygon's plane, as an index into LUMP_PLANES.</summary>
    public int PlaneNum;
}

/// <summary>
/// One edge: two vertex indices (<c>bspfile.h:673</c>,
/// <c>struct dedge_t</c>).
/// </summary>
/// <remarks>
/// Edge 0 is never used, because LUMP_SURFEDGES encodes an edge's direction in
/// the SIGN of its index and zero has no sign (<c>bspfile.h:671</c>).
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DEdge
{
    /// <summary>The edge's two endpoints, as indices into LUMP_VERTEXES.</summary>
    public UShortArray2 V;
}

/// <summary>
/// One non-polygon primitive: a triangle list or strip
/// (<c>bspfile.h:687</c>, <c>struct dprimitive_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DPrimitive
{
    /// <summary><see cref="PrimitiveType"/>.</summary>
    public byte Type;

    /// <summary>
    /// Padding. <c>unsigned short firstIndex</c> follows a single byte, so the
    /// C compiler inserts one byte here and the struct is 10 bytes rather than
    /// the 9 its fields total.
    /// </summary>
    public byte Padding;

    /// <summary>The first index of this primitive's run in LUMP_PRIMINDICES.</summary>
    public ushort FirstIndex;

    /// <summary>How many indices the run holds.</summary>
    public ushort IndexCount;

    /// <summary>The first vertex of this primitive's run in LUMP_PRIMVERTS.</summary>
    public ushort FirstVert;

    /// <summary>How many vertices the run holds.</summary>
    public ushort VertCount;
}

/// <summary>The values <see cref="DPrimitive.Type"/> takes (<c>bspfile.h:681</c>).</summary>
public enum PrimitiveType
{
    /// <summary>An independent triangle list.</summary>
    TriList = 0,

    /// <summary>A triangle strip.</summary>
    TriStrip = 1,
}

/// <summary>
/// One face (<c>bspfile.h:703</c>, <c>struct dface_t</c>). The same struct
/// backs LUMP_FACES, LUMP_ORIGINALFACES and LUMP_FACES_HDR.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DFace
{
    /// <summary>The face's plane, as an index into LUMP_PLANES.</summary>
    public ushort PlaneNum;

    /// <summary>Non-zero when the face faces opposite to the node's plane.</summary>
    public byte Side;

    /// <summary>1 when the face is on a node, 0 when it is in a leaf.</summary>
    public byte OnNode;

    /// <summary>
    /// The first surfedge of this face's run. An <c>int</c> because a map can
    /// hold more than 64k edges (<c>bspfile.h:710</c>).
    /// </summary>
    public int FirstEdge;

    /// <summary>How many surfedges the run holds.</summary>
    public short NumEdges;

    /// <summary>The face's <see cref="TexInfo"/> index.</summary>
    public short TexInfo;

    /// <summary>
    /// The face's displacement, or -1. Shares its meaning with
    /// <see cref="SurfaceFogVolumeId"/> only by convention: the C++ has these
    /// as two fields with a comment saying they SHOULD be a union
    /// (<c>bspfile.h:713</c>), so both are separate fields here too.
    /// </summary>
    public short DispInfo;

    /// <summary>The fog volume id, for faces that bound water.</summary>
    public short SurfaceFogVolumeId;

    /// <summary>The four lightstyles this face's lightmap holds.</summary>
    public ByteArray4 Styles;

    /// <summary>Byte offset of this face's samples into the lighting lump, or -1.</summary>
    public int LightOfs;

    /// <summary>The face's surface area in world units.</summary>
    public float Area;

    /// <summary>The lightmap's origin in luxels.</summary>
    public IntArray2 LightmapTextureMinsInLuxels;

    /// <summary>The lightmap's size in luxels, minus one in each axis.</summary>
    public IntArray2 LightmapTextureSizeInLuxels;

    /// <summary>The LUMP_ORIGINALFACES index this face was cut from.</summary>
    public int OrigFace;

    /// <summary>
    /// The primitive count in the low 15 bits and "dynamic shadows disabled"
    /// in the top bit. Use <see cref="GetNumPrims"/> and
    /// <see cref="AreDynamicShadowsEnabled"/> rather than reading it raw --
    /// that top bit is exactly why <c>dface_t</c> has accessors
    /// (<c>bspfile.h:748</c>).
    /// </summary>
    public ushort NumPrimsAndFlags;

    /// <summary>The first primitive of this face's run in LUMP_PRIMITIVES.</summary>
    public ushort FirstPrimId;

    /// <summary>The face's smoothing group mask, as vrad uses it for phong.</summary>
    public uint SmoothingGroups;

    /// <summary>The primitive count, without the shadow bit.</summary>
    /// <returns>The low 15 bits of <see cref="NumPrimsAndFlags"/>.</returns>
    /// <remarks><c>bspfile.h:757</c>, <c>dface_t::GetNumPrims</c>.</remarks>
    public readonly ushort GetNumPrims() => (ushort)(NumPrimsAndFlags & 0x7FFF);

    /// <summary>Sets the primitive count, leaving the shadow bit alone.</summary>
    /// <param name="count">A count below 0x8000.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> would collide with the shadow bit. The C++
    /// asserts the same thing (<c>bspfile.h:764</c>); an assert compiled out of
    /// a release build is how a silent corruption gets shipped, so this throws.
    /// </exception>
    public void SetNumPrims(ushort count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)0x7FFF);

        // bspfile.h:765 clears with ~0x7FFF, which leaves the top bit set in
        // the mask and so PRESERVES it. Reproduced rather than "cleaned up".
        NumPrimsAndFlags &= unchecked((ushort)~0x7FFF);
        NumPrimsAndFlags |= (ushort)(count & 0x7FFF);
    }

    /// <summary>Whether the engine casts dynamic shadows onto this face.</summary>
    /// <returns>True when the top bit of <see cref="NumPrimsAndFlags"/> is clear.</returns>
    /// <remarks><c>bspfile.h:769</c>, and note the sense is INVERTED: the bit means "disabled".</remarks>
    public readonly bool AreDynamicShadowsEnabled() => (NumPrimsAndFlags & 0x8000) == 0;

    /// <summary>Enables or disables dynamic shadows on this face.</summary>
    /// <param name="enabled">True to enable.</param>
    public void SetDynamicShadowsEnabled(bool enabled)
    {
        if (enabled)
        {
            NumPrimsAndFlags &= unchecked((ushort)~0x8000);
        }
        else
        {
            NumPrimsAndFlags |= 0x8000;
        }
    }
}

/// <summary>
/// One Hammer face id (<c>bspfile.h:782</c>, <c>struct dfaceid_t</c>),
/// parallel to LUMP_FACES.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DFaceId
{
    /// <summary>The id Hammer gave the brush face this face came from.</summary>
    public ushort HammerFaceId;
}

/// <summary>
/// One leaf as LUMP_LEAFS version 1 stores it: 32 bytes
/// (<c>bspfile.h:826</c>, <c>struct dleaf_t</c>).
/// </summary>
/// <remarks>
/// <para>
/// The ambient cube that <see cref="DLeafVersion0"/> carries inline was moved
/// out to LUMP_LEAF_AMBIENT_LIGHTING at version 1 (<c>bspfile.h:848</c>), which
/// is the whole difference and the reason there are two structs.
/// </para>
/// <para>
/// Which one a file uses is decided by the LUMP's version field, not by the
/// file version -- <c>dm_lockdown.bsp</c> in this tree is BSP version 19 with
/// LEAFS at version 0, so it uses <see cref="DLeafVersion0"/>.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DLeaf
{
    /// <summary>The OR of every brush's contents in this leaf.</summary>
    public int Contents;

    /// <summary>The vis cluster this leaf belongs to, or -1 for solid.</summary>
    public short Cluster;

    /// <summary>
    /// The C++ bitfield <c>short area:9; short flags:7;</c>
    /// (<c>bspfile.h:833</c>), stored whole. Read it with
    /// <see cref="GetArea"/> and <see cref="GetFlags"/>.
    /// </summary>
    public ushort AreaFlags;

    /// <summary>The leaf's integer bounding box minimum.</summary>
    public ShortArray3 Mins;

    /// <summary>The leaf's integer bounding box maximum.</summary>
    public ShortArray3 Maxs;

    /// <summary>The first entry of this leaf's run in LUMP_LEAFFACES.</summary>
    public ushort FirstLeafFace;

    /// <summary>How many entries the run holds.</summary>
    public ushort NumLeafFaces;

    /// <summary>The first entry of this leaf's run in LUMP_LEAFBRUSHES.</summary>
    public ushort FirstLeafBrush;

    /// <summary>How many entries the run holds.</summary>
    public ushort NumLeafBrushes;

    /// <summary>The LUMP_LEAFWATERDATA index, or -1 when not in water.</summary>
    public short LeafWaterDataId;

    /// <summary>
    /// Trailing padding. The fields total 30 bytes and <c>int contents</c>
    /// gives the C struct 4-byte alignment, so it is 32 -- the size the plan
    /// calls out and the one a real map's lump length divides by.
    /// </summary>
    public short Padding;

    /// <summary>The leaf's visibility area.</summary>
    /// <returns>The low 9 bits of <see cref="AreaFlags"/>.</returns>
    public readonly int GetArea() => AreaFlags & 0x01FF;

    /// <summary>The leaf's per-leaf flags.</summary>
    /// <returns>The top 7 bits of <see cref="AreaFlags"/>, as <see cref="LeafFlags"/>.</returns>
    /// <remarks>
    /// The C++ declares them <c>area:9</c> then <c>flags:7</c>, and both GCC
    /// and MSVC allocate little-endian bitfields from the LOW bit up, so area
    /// is bits 0..8 and flags bits 9..15. The header underlines that only 7
    /// bits are stored (<c>bspfile.h:790</c>).
    /// </remarks>
    public readonly LeafFlags GetFlags() => (LeafFlags)((AreaFlags >> 9) & 0x7F);

    /// <summary>Replaces the area and flags together.</summary>
    /// <param name="area">A value in 0..511.</param>
    /// <param name="flags">A combination of <see cref="LeafFlags"/>, below 0x80.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either value does not fit its field.</exception>
    public void SetAreaFlags(int area, LeafFlags flags)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(area);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(area, 511);
        ArgumentOutOfRangeException.ThrowIfNegative((int)flags);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)flags, 127);
        AreaFlags = (ushort)(area | ((int)flags << 9));
    }
}

/// <summary>
/// The per-leaf flags of <see cref="DLeaf.GetFlags"/>
/// (<c>bspfile.h:791</c>). Only seven bits are stored.
/// </summary>
[Flags]
public enum LeafFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The leaf has 3D sky in its PVS.</summary>
    Sky = 0x01,

    /// <summary>The leaf culled away some portals because of radial vis.</summary>
    Radial = 0x02,

    /// <summary>The leaf has 2D sky in its PVS.</summary>
    Sky2D = 0x04,
}

/// <summary>
/// One leaf as LUMP_LEAFS version 0 stored it: 56 bytes, with the ambient
/// cube inline (<c>bspfile.h:799</c>, <c>struct dleaf_version_0_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DLeafVersion0
{
    /// <summary>The OR of every brush's contents in this leaf.</summary>
    public int Contents;

    /// <summary>The vis cluster this leaf belongs to, or -1 for solid.</summary>
    public short Cluster;

    /// <summary>The <c>area:9 / flags:7</c> bitfield, stored whole.</summary>
    public ushort AreaFlags;

    /// <summary>The leaf's integer bounding box minimum.</summary>
    public ShortArray3 Mins;

    /// <summary>The leaf's integer bounding box maximum.</summary>
    public ShortArray3 Maxs;

    /// <summary>The first entry of this leaf's run in LUMP_LEAFFACES.</summary>
    public ushort FirstLeafFace;

    /// <summary>How many entries the run holds.</summary>
    public ushort NumLeafFaces;

    /// <summary>The first entry of this leaf's run in LUMP_LEAFBRUSHES.</summary>
    public ushort FirstLeafBrush;

    /// <summary>How many entries the run holds.</summary>
    public ushort NumLeafBrushes;

    /// <summary>The LUMP_LEAFWATERDATA index, or -1 when not in water.</summary>
    public short LeafWaterDataId;

    /// <summary>
    /// The precalculated ambient cube entities light themselves from. This is
    /// the field version 1 removed.
    /// </summary>
    public CompressedLightCube AmbientLighting;

    /// <summary>
    /// Trailing padding: the fields total 54 bytes and the struct's alignment
    /// is 4, so it is 56.
    /// </summary>
    public short Padding;

    /// <summary>The leaf's visibility area.</summary>
    /// <returns>The low 9 bits of <see cref="AreaFlags"/>.</returns>
    public readonly int GetArea() => AreaFlags & 0x01FF;

    /// <summary>The leaf's per-leaf flags.</summary>
    /// <returns>The top 7 bits of <see cref="AreaFlags"/>.</returns>
    public readonly LeafFlags GetFlags() => (LeafFlags)((AreaFlags >> 9) & 0x7F);
}

/// <summary>
/// One ambient sample inside a leaf (<c>bspfile.h:860</c>,
/// <c>struct dleafambientlighting_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DLeafAmbientLighting
{
    /// <summary>The sampled ambient cube.</summary>
    public CompressedLightCube Cube;

    /// <summary>The sample's x position as a 0.8 fraction of the leaf's bounds.</summary>
    public byte X;

    /// <summary>The sample's y position as a 0.8 fraction of the leaf's bounds.</summary>
    public byte Y;

    /// <summary>The sample's z position as a 0.8 fraction of the leaf's bounds.</summary>
    public byte Z;

    /// <summary>Unused, and named <c>pad</c> in the header too.</summary>
    public byte Pad;
}

/// <summary>
/// One leaf's slice of the ambient sample array (<c>bspfile.h:870</c>,
/// <c>struct dleafambientindex_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DLeafAmbientIndex
{
    /// <summary>How many samples this leaf has.</summary>
    public ushort AmbientSampleCount;

    /// <summary>The first sample's index.</summary>
    public ushort FirstAmbientSample;
}

/// <summary>
/// One brush side (<c>bspfile.h:878</c>, <c>struct dbrushside_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DBrushSide
{
    /// <summary>The side's plane, facing OUT of the leaf.</summary>
    public ushort PlaneNum;

    /// <summary>The side's <see cref="TexInfo"/> index.</summary>
    public short TexInfo;

    /// <summary>The side's displacement index, or -1. Added at BSP version 7.</summary>
    public short DispInfo;

    /// <summary>Non-zero when the side is a bevel plane. Added at BSP version 7.</summary>
    public short Bevel;
}

/// <summary>
/// One collision brush (<c>bspfile.h:887</c>, <c>struct dbrush_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DBrush
{
    /// <summary>The first side of this brush's run in LUMP_BRUSHSIDES.</summary>
    public int FirstSide;

    /// <summary>How many sides the run holds.</summary>
    public int NumSides;

    /// <summary>The brush's contents flags, <c>CONTENTS_*</c> from <c>bspflags.h</c>.</summary>
    public int Contents;
}

/// <summary>
/// One areaportal: the door between two visibility areas
/// (<c>bspfile.h:913</c>, <c>struct dareaportal_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DAreaPortal
{
    /// <summary>
    /// The value an areaportal entity's <c>portalnumber</c> key must match for
    /// the engine to bind the entity to this portal (<c>bspfile.h:916</c>).
    /// </summary>
    public ushort PortalKey;

    /// <summary>The area on the far side.</summary>
    public ushort OtherArea;

    /// <summary>The first vertex of the portal's polygon in LUMP_CLIPPORTALVERTS.</summary>
    public ushort FirstClipPortalVert;

    /// <summary>How many vertices the polygon has.</summary>
    public ushort ClipPortalVerts;

    /// <summary>The portal's plane, as an index into LUMP_PLANES.</summary>
    public int PlaneNum;
}

/// <summary>
/// One visibility area (<c>bspfile.h:929</c>, <c>struct darea_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DArea
{
    /// <summary>How many areaportals lead out of this area.</summary>
    public int NumAreaPortals;

    /// <summary>The first of them, as an index into LUMP_AREAPORTALS.</summary>
    public int FirstAreaPortal;
}

/// <summary>
/// One water volume (<c>bspfile.h:936</c>, <c>struct dleafwaterdata_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DLeafWaterData
{
    /// <summary>The world height of the water surface.</summary>
    public float SurfaceZ;

    /// <summary>The world height of the water's bottom.</summary>
    public float MinZ;

    /// <summary>The <see cref="TexInfo"/> of the surface, for fog parameters.</summary>
    public short SurfaceTexInfoId;

    /// <summary>
    /// Trailing padding: the fields total 10 bytes and the two floats give the
    /// struct 4-byte alignment, so it is 12.
    /// </summary>
    public short Padding;
}

/// <summary>
/// One face's macro texture reference (<c>bspfile.h:944</c>,
/// <c>class CFaceMacroTextureInfo</c>), parallel to LUMP_FACES.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FaceMacroTextureInfo
{
    /// <summary>
    /// An index into LUMP_TEXDATA_STRING_TABLE, or 0xFFFF when the face has no
    /// macro texture.
    /// </summary>
    public ushort MacroTextureNameId;
}

/// <summary>
/// The kinds of light source vrad resolves (<c>bspfile.h:954</c>,
/// <c>enum emittype_t</c>).
/// </summary>
public enum EmitType
{
    /// <summary>A 90 degree spotlight emitted from a surface.</summary>
    Surface = 0,

    /// <summary>A simple point source.</summary>
    Point = 1,

    /// <summary>A spotlight with a penumbra.</summary>
    Spotlight = 2,

    /// <summary>A directional light with no falloff; the surface must see sky.</summary>
    SkyLight = 3,

    /// <summary>Linear falloff, non-lambertian.</summary>
    QuakeLight = 4,

    /// <summary>A spherical source with no falloff; the surface must see sky.</summary>
    SkyAmbient = 5,
}

/// <summary>
/// One light source as vrad resolved it (<c>bspfile.h:969</c>,
/// <c>struct dworldlight_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DWorldLight
{
    /// <summary>The light's world position.</summary>
    public Vec3 Origin;

    /// <summary>The light's colour and brightness, linear.</summary>
    public Vec3 Intensity;

    /// <summary>The light's direction, for surfaces and spotlights.</summary>
    public Vec3 Normal;

    /// <summary>The vis cluster the light sits in.</summary>
    public int Cluster;

    /// <summary><see cref="EmitType"/>.</summary>
    public int Type;

    /// <summary>The lightstyle this light animates on.</summary>
    public int Style;

    /// <summary>The cosine where a spotlight's penumbra starts.</summary>
    public float StopDot;

    /// <summary>The cosine where a spotlight's penumbra ends.</summary>
    public float StopDot2;

    /// <summary>The spotlight falloff exponent.</summary>
    public float Exponent;

    /// <summary>The cutoff distance.</summary>
    public float Radius;

    /// <summary>The constant term of the attenuation denominator.</summary>
    public float ConstantAttn;

    /// <summary>The linear term of the attenuation denominator.</summary>
    public float LinearAttn;

    /// <summary>The quadratic term of the attenuation denominator.</summary>
    public float QuadraticAttn;

    /// <summary><see cref="WorldLightFlags"/>.</summary>
    public int Flags;

    /// <summary>The <see cref="TexInfo"/> of the emitting surface.</summary>
    public int TexInfo;

    /// <summary>The entity this light is positioned relative to.</summary>
    public int Owner;
}

/// <summary>The values <see cref="DWorldLight.Flags"/> takes (<c>bspfile.h:966</c>).</summary>
[Flags]
public enum WorldLightFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>vrad folded this light into the per-leaf ambient cubes.</summary>
    InAmbientCube = 0x0001,
}

/// <summary>
/// One cubemap sample position (<c>bspfile.h:992</c>,
/// <c>struct dcubemapsample_t</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DCubemapSample
{
    /// <summary>
    /// The sample's position, snapped to integers. The VTF's filename is
    /// derived from these three numbers (<c>bspfile.h:996</c>), so rounding
    /// them differently renames every cubemap in the map.
    /// </summary>
    public IntArray3 Origin;

    /// <summary>
    /// The face size: 0 for the default, otherwise <c>1 &lt;&lt; (size - 1)</c>.
    /// </summary>
    public byte Size;

    /// <summary>
    /// Trailing padding: the fields total 13 bytes and the int array gives the
    /// struct 4-byte alignment, so it is 16.
    /// </summary>
    public ByteArray3 Padding;
}

/// <summary>Three bytes laid out end to end.</summary>
[System.Runtime.CompilerServices.InlineArray(3)]
public struct ByteArray3
{
    private byte _element0;
}

/// <summary>
/// One overlay's fade distances (<c>bspfile.h:1056</c>,
/// <c>struct doverlayfade_t</c>), parallel to LUMP_OVERLAYS.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DOverlayFade
{
    /// <summary>The squared distance at which the overlay starts to fade in.</summary>
    public float FadeDistMinSq;

    /// <summary>The squared distance at which it is gone.</summary>
    public float FadeDistMaxSq;
}
