//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Assets;

// The parts of the studio model format a MAP COMPILER needs, and no more. What
// is deliberately absent, and why, is listed on StudioHeader.
//
// Two layout rules run through every struct here.
//
// 1. The reference layout never packs (its only #pragma pack(push, 4) covers a
//    single template, and the other sits inside a #ifdef PLATFORM_64BITS
//    branch). Nothing in the 32-bit view needs more than 4-byte alignment, so
//    natural alignment and Pack = 1 with explicit pads agree; the pads are
//    written out anyway, for the same reason as in the BSP structs.
//
// 2. The reference layout declares 64-bit-port branches, so several structs
//    carry #ifdef PLATFORM_64BITS variants. The FILE is always the 32-bit
//    branch: every native pointer is four bytes on disk and the reserved
//    arrays are the longer, 32-bit ones. Each pointer below is an int
//    placeholder named for the field the reference layout calls it.

/// <summary>
/// The four-byte idents and versions of the studio model files.
/// </summary>
public static class StudioIdents
{
    /// <summary><c>IDST</c>, the MDL ident (the reference writer compares the literal).</summary>
    public const int Mdl = ('T' << 24) | ('S' << 16) | ('D' << 8) | 'I';

    /// <summary><c>IDAG</c>, an included-animation MDL's ident.</summary>
    public const int AnimationGroup = ('G' << 24) | ('A' << 16) | ('D' << 8) | 'I';

    /// <summary><c>IDSV</c>, the VVD ident.</summary>
    public const int Vvd = ('V' << 24) | ('S' << 16) | ('D' << 8) | 'I';

    /// <summary><c>IDCV</c>, a thin/compressed VVD's ident.</summary>
    public const int VvdThin = ('V' << 24) | ('C' << 16) | ('D' << 8) | 'I';

    /// <summary>The MDL version this branch reads.</summary>
    public const int MdlVersion = 48;

    /// <summary>The VVD version.</summary>
    public const int VvdVersion = 4;

    /// <summary>The VTX version.</summary>
    public const int VtxVersion = 7;

    /// <summary>The most LOD levels a model can have.</summary>
    public const int MaxLods = 8;

    /// <summary>
    /// How many bones can influence one vertex.
    /// </summary>
    /// <remarks>
    /// The reference layout warns that changing this number also changes the
    /// vtx file format -- it sizes both <c>mstudioboneweight_t</c> and
    /// <c>OptimizedModel::Vertex_t</c>, which is why the two files must agree.
    /// </remarks>
    public const int MaxBonesPerVert = 3;
}

/// <summary>Eight signed 32-bit values, as C's <c>int x[8]</c>.</summary>
[InlineArray(8)]
public struct IntArray8
{
    private int _element0;
}

/// <summary>Six signed 32-bit values, as C's <c>int x[6]</c>.</summary>
[InlineArray(6)]
public struct IntArray6
{
    private int _element0;
}

/// <summary>Ten signed 32-bit values, as C's <c>int x[10]</c>.</summary>
[InlineArray(10)]
public struct IntArray10
{
    private int _element0;
}

/// <summary>Fifty-six signed 32-bit values, as C's <c>int reserved[56]</c>.</summary>
[InlineArray(56)]
public struct IntArray56
{
    private int _element0;
}

/// <summary>Sixty-four bytes, as C's <c>char name[64]</c>.</summary>
[InlineArray(64)]
public struct ByteArray64
{
    private byte _element0;
}

/// <summary>Three 32-bit floats, as C's <c>float x[3]</c>.</summary>
[InlineArray(3)]
public struct FloatArray3
{
    private float _element0;
}

/// <summary>Twelve 32-bit floats: one <c>matrix3x4_t</c>.</summary>
[InlineArray(12)]
public struct FloatArray12
{
    private float _element0;
}

/// <summary>Four 32-bit floats: one <c>Quaternion</c> or <c>Vector4D</c>.</summary>
[InlineArray(4)]
public struct FloatArray4
{
    private float _element0;
}

/// <summary>
/// The MDL file header (<c>struct studiohdr_t</c>).
/// 408 bytes in the 32-bit, on-disk layout.
/// </summary>
/// <remarks>
/// <para>
/// DELIBERATELY NOT PORTED, and these are index/count pairs whose targets have
/// no struct in this port: animations and sequences
/// (<c>mstudioanimdesc_t</c>, <c>mstudioseqdesc_t</c>, anim blocks), flexes
/// (descriptors, controllers, rules, UI), IK chains and autoplay locks,
/// eyeballs, mouths, pose parameters, attachments, bone controllers, local
/// nodes and transitions, include-model groups, linear bones, source bone
/// transforms, and the "thin" vertex representation. A map compiler never
/// evaluates an animation or a flex: vbsp wants a static prop's bounding box
/// and its collision, vrad wants its triangles. The COUNTS and OFFSETS are all
/// here, so nothing about the file is hidden and adding a struct later needs
/// no change to this one.
/// </para>
/// <para>
/// The four pointer members are <c>int</c> placeholders. In the shipped 32-bit
/// format they are four bytes of garbage the loader overwrites; the reference
/// layout's 64-bit port replaces them with <c>unused_</c> ints in exactly the
/// same slots, so the on-disk layout is unchanged either way.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioHeader
{
    /// <summary><c>IDST</c> or <c>IDAG</c>.</summary>
    public int Id;

    /// <summary>The format version; 48 in this branch.</summary>
    public int Version;

    /// <summary>
    /// A checksum that must match the VVD's and the PHY's, which is how the
    /// three files of one model are proved to belong together.
    /// </summary>
    public int Checksum;

    /// <summary>The model's own name, NUL-padded to 64 bytes.</summary>
    public ByteArray64 Name;

    /// <summary>The file's total length in bytes.</summary>
    public int Length;

    /// <summary>The ideal eye position.</summary>
    public Vec3 EyePosition;

    /// <summary>The point lighting is sampled at.</summary>
    public Vec3 IllumPosition;

    /// <summary>The movement hull's minimum.</summary>
    public Vec3 HullMin;

    /// <summary>The movement hull's maximum.</summary>
    public Vec3 HullMax;

    /// <summary>The render bounding box's minimum.</summary>
    public Vec3 ViewBbMin;

    /// <summary>The render bounding box's maximum.</summary>
    public Vec3 ViewBbMax;

    /// <summary><c>STUDIOHDR_FLAGS_</c> bits.</summary>
    public int Flags;

    /// <summary>How many bones the model has.</summary>
    public int NumBones;

    /// <summary>Byte offset from the header to the bone array.</summary>
    public int BoneIndex;

    /// <summary>How many bone controllers. Not ported.</summary>
    public int NumBoneControllers;

    /// <summary>Byte offset to the bone controller array. Not ported.</summary>
    public int BoneControllerIndex;

    /// <summary>How many hitbox sets.</summary>
    public int NumHitboxSets;

    /// <summary>Byte offset to the hitbox set array.</summary>
    public int HitboxSetIndex;

    /// <summary>How many local animations. Not ported.</summary>
    public int NumLocalAnim;

    /// <summary>Byte offset to the animation descriptions. Not ported.</summary>
    public int LocalAnimIndex;

    /// <summary>How many local sequences. Not ported.</summary>
    public int NumLocalSeq;

    /// <summary>Byte offset to the sequence descriptions. Not ported.</summary>
    public int LocalSeqIndex;

    /// <summary>A run-time flag the loader stamps; meaningless on disk.</summary>
    public int ActivityListVersion;

    /// <summary>A run-time flag the loader stamps; meaningless on disk.</summary>
    public int EventsIndexed;

    /// <summary>How many textures the model references.</summary>
    public int NumTextures;

    /// <summary>Byte offset to the texture array.</summary>
    public int TextureIndex;

    /// <summary>How many material search paths.</summary>
    public int NumCdTextures;

    /// <summary>Byte offset to an array of ints, each a string offset.</summary>
    public int CdTextureIndex;

    /// <summary>How many texture slots one skin family has.</summary>
    public int NumSkinRef;

    /// <summary>How many skin families.</summary>
    public int NumSkinFamilies;

    /// <summary>Byte offset to the skin table, <c>numskinfamilies * numskinref</c> shorts.</summary>
    public int SkinIndex;

    /// <summary>How many body parts.</summary>
    public int NumBodyParts;

    /// <summary>Byte offset to the body part array.</summary>
    public int BodyPartIndex;

    /// <summary>How many attachments. Not ported.</summary>
    public int NumLocalAttachments;

    /// <summary>Byte offset to the attachment array. Not ported.</summary>
    public int LocalAttachmentIndex;

    /// <summary>How many local nodes. Not ported.</summary>
    public int NumLocalNodes;

    /// <summary>Byte offset to the node transition table. Not ported.</summary>
    public int LocalNodeIndex;

    /// <summary>Byte offset to the node name offsets. Not ported.</summary>
    public int LocalNodeNameIndex;

    /// <summary>How many flex descriptors. Not ported.</summary>
    public int NumFlexDesc;

    /// <summary>Byte offset to the flex descriptors. Not ported.</summary>
    public int FlexDescIndex;

    /// <summary>How many flex controllers. Not ported.</summary>
    public int NumFlexControllers;

    /// <summary>Byte offset to the flex controllers. Not ported.</summary>
    public int FlexControllerIndex;

    /// <summary>How many flex rules. Not ported.</summary>
    public int NumFlexRules;

    /// <summary>Byte offset to the flex rules. Not ported.</summary>
    public int FlexRuleIndex;

    /// <summary>How many IK chains. Not ported.</summary>
    public int NumIkChains;

    /// <summary>Byte offset to the IK chains. Not ported.</summary>
    public int IkChainIndex;

    /// <summary>How many mouths. Not ported.</summary>
    public int NumMouths;

    /// <summary>Byte offset to the mouths. Not ported.</summary>
    public int MouthIndex;

    /// <summary>How many pose parameters. Not ported.</summary>
    public int NumLocalPoseParameters;

    /// <summary>Byte offset to the pose parameters. Not ported.</summary>
    public int LocalPoseParamIndex;

    /// <summary>Byte offset to the model's default surface property name.</summary>
    public int SurfacePropIndex;

    /// <summary>
    /// Byte offset to the model's keyvalue text, which is where
    /// <c>prop_data</c> and a prop's collision hints live.
    /// </summary>
    public int KeyValueIndex;

    /// <summary>How many bytes of keyvalue text.</summary>
    public int KeyValueSize;

    /// <summary>How many IK autoplay locks. Not ported.</summary>
    public int NumLocalIkAutoplayLocks;

    /// <summary>Byte offset to the IK autoplay locks. Not ported.</summary>
    public int LocalIkAutoplayLockIndex;

    /// <summary>The collision model's mass, as vphysics computed it.</summary>
    public float Mass;

    /// <summary>The model's contents flags.</summary>
    public int Contents;

    /// <summary>How many included models. Not ported.</summary>
    public int NumIncludeModels;

    /// <summary>Byte offset to the include-model groups. Not ported.</summary>
    public int IncludeModelIndex;

    /// <summary>A run-time pointer slot; four bytes of nothing on disk.</summary>
    public int VirtualModel;

    /// <summary>Byte offset to the animation block file name. Not ported.</summary>
    public int AnimBlockNameIndex;

    /// <summary>How many animation blocks. Not ported.</summary>
    public int NumAnimBlocks;

    /// <summary>Byte offset to the animation blocks. Not ported.</summary>
    public int AnimBlockIndex;

    /// <summary>A run-time pointer slot.</summary>
    public int AnimBlockModel;

    /// <summary>Byte offset to the bone table sorted by name: <c>numbones</c> bytes.</summary>
    public int BoneTableByNameIndex;

    /// <summary>A run-time pointer slot.</summary>
    public int VertexBase;

    /// <summary>A run-time pointer slot.</summary>
    public int IndexBase;

    /// <summary>The constant directional light's dot product, quantised.</summary>
    public byte ConstDirectionalLightDot;

    /// <summary>The LOD the model is currently at, for the run time.</summary>
    public byte RootLod;

    /// <summary>How many root LODs the model allows.</summary>
    public byte NumAllowedRootLods;

    /// <summary>One unused byte, named <c>unused[1]</c> in the reference layout.</summary>
    public byte Unused;

    /// <summary>Unused; the reference layout says to zero it below version 47.</summary>
    public int Unused4;

    /// <summary>How many flex controller UI entries. Not ported.</summary>
    public int NumFlexControllerUi;

    /// <summary>Byte offset to the flex controller UI entries. Not ported.</summary>
    public int FlexControllerUiIndex;

    /// <summary>The fixed-point scale vertex animations are stored at.</summary>
    public float VertAnimFixedPointScale;

    /// <summary>Unused, <c>int unused3[1]</c>.</summary>
    public int Unused3;

    /// <summary>
    /// Byte offset to a <see cref="StudioHeader2"/>, or zero when there is
    /// none.
    /// </summary>
    public int StudioHdr2Index;

    /// <summary>Unused, <c>int unused2[1]</c>.</summary>
    public int Unused2;
}

/// <summary>
/// The MDL's extension header (<c>struct studiohdr2_t</c>). 256 bytes in the
/// 32-bit layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioHeader2
{
    /// <summary>How many source bone transforms. Not ported.</summary>
    public int NumSrcBoneTransform;

    /// <summary>Byte offset to them. Not ported.</summary>
    public int SrcBoneTransformIndex;

    /// <summary>Which attachment the illumination position comes from.</summary>
    public int IllumPositionAttachmentIndex;

    /// <summary>
    /// The maximum eye deflection cosine. Zero means unset, and the reference
    /// implementation substitutes <c>cos(30)</c>.
    /// </summary>
    public float MaxEyeDeflection;

    /// <summary>Byte offset to the linear bone table. Not ported.</summary>
    public int LinearBoneIndex;

    /// <summary>Byte offset to the model's name, when it is too long for the base header.</summary>
    public int NameIndex;

    /// <summary>How many bone flex drivers. Not ported.</summary>
    public int BoneFlexDriverCount;

    /// <summary>Byte offset to them. Not ported.</summary>
    public int BoneFlexDriverIndex;

    /// <summary>
    /// <c>int reserved[56]</c>. The reference layout's 64-bit port carves four
    /// pointers out of the front of this array but the FILE holds 56 ints, so
    /// that is what is here.
    /// </summary>
    public IntArray56 Reserved;
}

/// <summary>
/// One bone (<c>struct mstudiobone_t</c>). 216 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioBone
{
    /// <summary>Byte offset from this struct to the bone's name.</summary>
    public int NameIndex;

    /// <summary>The parent bone's index, or -1 for a root.</summary>
    public int Parent;

    /// <summary>One bone controller index per degree of freedom, -1 for none.</summary>
    public IntArray6 BoneController;

    /// <summary>The bone's default position relative to its parent.</summary>
    public Vec3 Pos;

    /// <summary>The bone's default rotation as a quaternion.</summary>
    public FloatArray4 Quat;

    /// <summary>The bone's default rotation as radian Euler angles.</summary>
    public Vec3 Rot;

    /// <summary>The compression scale for the position channels.</summary>
    public Vec3 PosScale;

    /// <summary>The compression scale for the rotation channels.</summary>
    public Vec3 RotScale;

    /// <summary>The bone's pose-to-bone matrix, a <c>matrix3x4_t</c>.</summary>
    public FloatArray12 PoseToBone;

    /// <summary>An alignment quaternion.</summary>
    public FloatArray4 QAlignment;

    /// <summary><c>BONE_</c> flags.</summary>
    public int Flags;

    /// <summary>The procedural rule's type, or zero.</summary>
    public int ProcType;

    /// <summary>Byte offset to the procedural rule. Not ported.</summary>
    public int ProcIndex;

    /// <summary>The physics bone this maps to.</summary>
    public int PhysicsBone;

    /// <summary>Byte offset to the bone's surface property name.</summary>
    public int SurfacePropIdx;

    /// <summary>The bone's contents flags.</summary>
    public int Contents;

    /// <summary>Eight unused ints the reference layout marks "remove as appropriate".</summary>
    public IntArray8 Unused;
}

/// <summary>
/// One hitbox (<c>struct mstudiobbox_t</c>). 68 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioBbox
{
    /// <summary>The bone the box is attached to.</summary>
    public int Bone;

    /// <summary>The hit group.</summary>
    public int Group;

    /// <summary>The box's minimum, in the bone's space.</summary>
    public Vec3 BbMin;

    /// <summary>The box's maximum.</summary>
    public Vec3 BbMax;

    /// <summary>Byte offset to the hitbox's name, or zero.</summary>
    public int HitboxNameIndex;

    /// <summary>Eight unused ints.</summary>
    public IntArray8 Unused;
}

/// <summary>
/// One hitbox set (<c>struct mstudiohitboxset_t</c>). 12 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioHitboxSet
{
    /// <summary>Byte offset to the set's name.</summary>
    public int NameIndex;

    /// <summary>How many hitboxes.</summary>
    public int NumHitboxes;

    /// <summary>Byte offset from this struct to the hitbox array.</summary>
    public int HitboxIndex;
}

/// <summary>
/// One body part (<c>struct mstudiobodyparts_t</c>). 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioBodyParts
{
    /// <summary>Byte offset to the body part's name.</summary>
    public int NameIndex;

    /// <summary>How many alternative models this part can show.</summary>
    public int NumModels;

    /// <summary>The multiplier this part contributes to the bodygroup number.</summary>
    public int Base;

    /// <summary>Byte offset from this struct to the model array.</summary>
    public int ModelIndex;
}

/// <summary>
/// One model within a body part (<c>struct mstudiomodel_t</c>). 148 bytes in
/// the 32-bit layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioModel
{
    /// <summary>The model's name, NUL-padded to 64 bytes.</summary>
    public ByteArray64 Name;

    /// <summary>The model's type.</summary>
    public int Type;

    /// <summary>The bounding sphere's radius.</summary>
    public float BoundingRadius;

    /// <summary>How many meshes this model has.</summary>
    public int NumMeshes;

    /// <summary>Byte offset from this struct to the mesh array.</summary>
    public int MeshIndex;

    /// <summary>
    /// How many vertices this model owns in the VVD.
    /// </summary>
    public int NumVertices;

    /// <summary>
    /// The model's first vertex, as a BYTE offset into the VVD's vertex block.
    /// </summary>
    /// <remarks>
    /// A byte offset and not an index: divide by <c>sizeof(mstudiovertex_t)</c>
    /// (48) to index the vertex array. Treating it as an index reads 48 times
    /// too far into the file.
    /// </remarks>
    public int VertexIndex;

    /// <summary>The model's first tangent, as a byte offset into the tangent block.</summary>
    public int TangentsIndex;

    /// <summary>How many attachments. Not ported.</summary>
    public int NumAttachments;

    /// <summary>Byte offset to the attachments. Not ported.</summary>
    public int AttachmentIndex;

    /// <summary>How many eyeballs. Not ported.</summary>
    public int NumEyeballs;

    /// <summary>Byte offset to the eyeballs. Not ported.</summary>
    public int EyeballIndex;

    /// <summary>
    /// <c>mstudio_modelvertexdata_t</c>: two run-time pointers. Eight bytes of
    /// nothing on disk.
    /// </summary>
    public IntArray2 VertexDataPointers;

    /// <summary>
    /// <c>int unused[8]</c>. The reference layout's 64-bit port shortens this
    /// to six to pay for the wider pointers above; the FILE has eight.
    /// </summary>
    public IntArray8 Unused;
}

/// <summary>
/// One mesh within a model (<c>struct mstudiomesh_t</c>). 116 bytes in the
/// 32-bit layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioMesh
{
    /// <summary>The index into the model's texture array.</summary>
    public int Material;

    /// <summary>The NEGATIVE byte offset back to the owning model.</summary>
    public int ModelIndex;

    /// <summary>How many vertices this mesh owns.</summary>
    public int NumVertices;

    /// <summary>
    /// This mesh's first vertex, as an INDEX relative to the model's own
    /// vertex run.
    /// </summary>
    /// <remarks>
    /// An index here, unlike <see cref="StudioModel.VertexIndex"/>, which is a
    /// byte offset. The two fields mean different things and the reference
    /// layout names them differently (<c>vertexoffset</c> against
    /// <c>vertexindex</c>) for exactly that reason.
    /// </remarks>
    public int VertexOffset;

    /// <summary>How many vertex-animation flexes. Not ported.</summary>
    public int NumFlexes;

    /// <summary>Byte offset to the flexes. Not ported.</summary>
    public int FlexIndex;

    /// <summary>A material operation code.</summary>
    public int MaterialType;

    /// <summary>A material operation parameter.</summary>
    public int MaterialParam;

    /// <summary>A unique ordinal for this mesh within the model.</summary>
    public int MeshId;

    /// <summary>The mesh's centre.</summary>
    public Vec3 Center;

    /// <summary>
    /// A run-time pointer slot from <c>mstudio_meshvertexdata_t</c>.
    /// </summary>
    public int ModelVertexDataPointer;

    /// <summary>
    /// How many vertices this mesh has at each of the eight LOD levels. REAL
    /// on-disk data, unlike the pointer above it: studiomdl writes it and the
    /// LOD culling reads it.
    /// </summary>
    public IntArray8 NumLodVertexes;

    /// <summary><c>int unused[8]</c> in the 32-bit layout.</summary>
    public IntArray8 Unused;
}

/// <summary>
/// One texture reference (<c>struct mstudiotexture_t</c>). 64 bytes in the
/// 32-bit layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioTexture
{
    /// <summary>Byte offset from this struct to the material's name.</summary>
    public int NameIndex;

    /// <summary>Texture flags.</summary>
    public int Flags;

    /// <summary>A run-time "is this used" marker.</summary>
    public int Used;

    /// <summary>Unused.</summary>
    public int Unused1;

    /// <summary>A run-time <c>IMaterial*</c> slot.</summary>
    public int Material;

    /// <summary>A run-time client material slot.</summary>
    public int ClientMaterial;

    /// <summary><c>int unused[10]</c> in the 32-bit layout.</summary>
    public IntArray10 Unused;
}

/// <summary>
/// One vertex's bone weighting (<c>struct mstudioboneweight_t</c>). Exactly
/// 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioBoneWeight
{
    /// <summary>Up to three weights, summing to one.</summary>
    public FloatArray3 Weight;

    /// <summary>
    /// Up to three bone indices. Declared <c>char bone[3]</c>, and read here
    /// as unsigned bytes because <c>MAXSTUDIOBONES</c> is 128, so no valid
    /// index ever sets the sign bit.
    /// </summary>
    public ByteArray3 Bone;

    /// <summary>How many of the three are in use.</summary>
    public byte NumBones;
}

/// <summary>
/// One vertex (<c>struct mstudiovertex_t</c>). Exactly 48 bytes.
/// </summary>
/// <remarks>
/// The tangent is NOT here. It lives in a parallel <c>Vector4D</c> array in
/// the VVD, which is why <see cref="VertexFileHeader.TangentDataStart"/>
/// exists.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StudioVertex
{
    /// <summary>The vertex's bone weighting.</summary>
    public StudioBoneWeight BoneWeights;

    /// <summary>The vertex's position.</summary>
    public Vec3 Position;

    /// <summary>The vertex's normal.</summary>
    public Vec3 Normal;

    /// <summary>The vertex's texture coordinate, a <c>Vector2D</c>.</summary>
    public FloatArray2 TexCoord;
}

/// <summary>
/// The VVD file header (<c>struct vertexFileHeader_t</c>). 64 bytes, all on
/// disk.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexFileHeader
{
    /// <summary><c>IDSV</c>, or <c>IDCV</c> for a thin file.</summary>
    public int Id;

    /// <summary>The version; 4 in this branch.</summary>
    public int Version;

    /// <summary>Must equal the MDL's checksum.</summary>
    public int Checksum;

    /// <summary>How many of <see cref="NumLodVertexes"/> are valid.</summary>
    public int NumLods;

    /// <summary>How many vertices remain at each LOD level.</summary>
    public IntArray8 NumLodVertexes;

    /// <summary>How many <see cref="VertexFileFixup"/> entries follow.</summary>
    public int NumFixups;

    /// <summary>Byte offset from the header to the fixup table.</summary>
    public int FixupTableStart;

    /// <summary>Byte offset from the header to the vertex block.</summary>
    public int VertexDataStart;

    /// <summary>Byte offset from the header to the tangent block.</summary>
    public int TangentDataStart;
}

/// <summary>
/// One VVD fixup (<c>struct vertexFileFixup_t</c>). 12 bytes.
/// </summary>
/// <remarks>
/// The fixup table is how a VVD stores vertices grouped by LOD while the MDL
/// still indexes them in authoring order. A reader that ignores it gets the
/// right COUNT of vertices and the wrong ones.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexFileFixup
{
    /// <summary>The LOD this run belongs to; runs above the wanted LOD are skipped.</summary>
    public int Lod;

    /// <summary>The run's first vertex, absolute in the vertex block.</summary>
    public int SourceVertexId;

    /// <summary>How many vertices the run holds.</summary>
    public int NumVertexes;
}

// ---------------------------------------------------------------------------
// The VTX file. EVERY struct the format defines is here.
//
// The reference layout declares the whole block under #pragma pack(1), so
// these sizes are NOT the naturally aligned ones: Vertex_t is 9 bytes,
// StripHeader_t 27, StripGroupHeader_t 25, MeshHeader_t 9 and
// MaterialReplacementHeader_t 6. Every one of those would be larger without
// the pragma, and every array index past element 0 would be wrong.
// ---------------------------------------------------------------------------

/// <summary>
/// The VTX file header (<c>struct FileHeader_t</c>). 36 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxFileHeader
{
    /// <summary>The version; 7 in this branch.</summary>
    public int Version;

    /// <summary>The vertex cache size the model was optimised for.</summary>
    public int VertCacheSize;

    /// <summary>The most bones one strip may reference.</summary>
    public ushort MaxBonesPerStrip;

    /// <summary>The most bones one triangle may reference.</summary>
    public ushort MaxBonesPerTri;

    /// <summary>The most bones one vertex may reference.</summary>
    public int MaxBonesPerVert;

    /// <summary>Must equal the MDL's checksum.</summary>
    public int CheckSum;

    /// <summary>How many LOD levels, matching the MDL's.</summary>
    public int NumLods;

    /// <summary>Byte offset to one material replacement list per LOD.</summary>
    public int MaterialReplacementListOffset;

    /// <summary>How many body parts, matching the MDL's.</summary>
    public int NumBodyParts;

    /// <summary>Byte offset from the header to the body part array.</summary>
    public int BodyPartOffset;
}

/// <summary>
/// One VTX body part (<c>struct BodyPartHeader_t</c>). 8 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxBodyPartHeader
{
    /// <summary>How many models.</summary>
    public int NumModels;

    /// <summary>Byte offset FROM THIS STRUCT to the model array.</summary>
    public int ModelOffset;
}

/// <summary>
/// One VTX model (<c>struct ModelHeader_t</c>). 8 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxModelHeader
{
    /// <summary>How many LOD levels this model has.</summary>
    public int NumLods;

    /// <summary>Byte offset from this struct to the LOD array.</summary>
    public int LodOffset;
}

/// <summary>
/// One VTX LOD (<c>struct ModelLODHeader_t</c>). 12 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxModelLodHeader
{
    /// <summary>How many meshes.</summary>
    public int NumMeshes;

    /// <summary>Byte offset from this struct to the mesh array.</summary>
    public int MeshOffset;

    /// <summary>The distance at which this LOD takes over.</summary>
    public float SwitchPoint;
}

/// <summary>
/// One VTX mesh (<c>struct MeshHeader_t</c>). 9 bytes under <c>pack(1)</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxMeshHeader
{
    /// <summary>How many strip groups.</summary>
    public int NumStripGroups;

    /// <summary>Byte offset from this struct to the strip group array.</summary>
    public int StripGroupHeaderOffset;

    /// <summary><see cref="VtxMeshFlags"/>.</summary>
    public byte Flags;
}

/// <summary>
/// One VTX strip group (<c>struct StripGroupHeader_t</c>). 25 bytes under
/// <c>pack(1)</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxStripGroupHeader
{
    /// <summary>How many vertices the group owns.</summary>
    public int NumVerts;

    /// <summary>Byte offset from this struct to the vertex array.</summary>
    public int VertOffset;

    /// <summary>How many indices the group owns.</summary>
    public int NumIndices;

    /// <summary>Byte offset from this struct to the index array, of <c>ushort</c>.</summary>
    public int IndexOffset;

    /// <summary>How many strips.</summary>
    public int NumStrips;

    /// <summary>Byte offset from this struct to the strip array.</summary>
    public int StripOffset;

    /// <summary><see cref="VtxStripGroupFlags"/>.</summary>
    public byte Flags;
}

/// <summary>
/// One VTX strip (<c>struct StripHeader_t</c>). 27 bytes under
/// <c>pack(1)</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxStripHeader
{
    /// <summary>How many indices this strip uses.</summary>
    public int NumIndices;

    /// <summary>Where they start in the GROUP's index array.</summary>
    public int IndexOffset;

    /// <summary>How many vertices this strip uses.</summary>
    public int NumVerts;

    /// <summary>Where they start in the GROUP's vertex array.</summary>
    public int VertOffset;

    /// <summary>How many bones the strip references.</summary>
    public short NumBones;

    /// <summary><see cref="VtxStripFlags"/>.</summary>
    public byte Flags;

    /// <summary>How many hardware bone state changes.</summary>
    public int NumBoneStateChanges;

    /// <summary>Byte offset from this struct to the bone state changes.</summary>
    public int BoneStateChangeOffset;
}

/// <summary>
/// One VTX vertex (<c>struct Vertex_t</c>).
/// 9 bytes under <c>pack(1)</c>, and 10 without it.
/// </summary>
/// <remarks>
/// The single most consequential <c>pack(1)</c> in this port: a 10-byte stride
/// reads every vertex after the first from one byte too far, and the result is
/// geometry that is subtly, silently wrong rather than obviously broken.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxVertex
{
    /// <summary>Which of the mesh vertex's bone weights each index uses.</summary>
    public ByteArray3 BoneWeightIndex;

    /// <summary>How many bones influence this vertex.</summary>
    public byte NumBones;

    /// <summary>
    /// The index into the MESH's vertex run -- not the model's and not the
    /// file's.
    /// </summary>
    public ushort OrigMeshVertId;

    /// <summary>
    /// Up to three bone ids: global bone indices for software skinning,
    /// hardware bone slots for hardware skinning.
    /// </summary>
    public ByteArray3 BoneId;
}

/// <summary>
/// One hardware bone state change (<c>struct BoneStateChangeHeader_t</c>).
/// 8 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxBoneStateChangeHeader
{
    /// <summary>The hardware bone slot.</summary>
    public int HardwareId;

    /// <summary>The model bone to load into it.</summary>
    public int NewBoneId;
}

/// <summary>
/// One material replacement (<c>struct MaterialReplacementHeader_t</c>). 6
/// bytes under <c>pack(1)</c>, and 8 without.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxMaterialReplacementHeader
{
    /// <summary>The material slot being replaced.</summary>
    public short MaterialId;

    /// <summary>Byte offset from this struct to the replacement's name.</summary>
    public int ReplacementMaterialNameOffset;
}

/// <summary>
/// One LOD's material replacement list
/// (<c>struct MaterialReplacementListHeader_t</c>). 8 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtxMaterialReplacementListHeader
{
    /// <summary>How many replacements.</summary>
    public int NumReplacements;

    /// <summary>Byte offset from this struct to them.</summary>
    public int ReplacementOffset;
}

/// <summary>
/// The flags of <see cref="VtxStripHeader.Flags"/>
/// (<c>enum StripHeaderFlags_t</c>).
/// </summary>
[Flags]
public enum VtxStripFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The strip is an independent triangle list.</summary>
    IsTriList = 0x01,

    /// <summary>The strip is a triangle strip.</summary>
    IsTriStrip = 0x02,
}

/// <summary>
/// The flags of <see cref="VtxStripGroupHeader.Flags"/>
/// (<c>enum StripGroupFlags_t</c>).
/// </summary>
[Flags]
public enum VtxStripGroupFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The group is flexed.</summary>
    IsFlexed = 0x01,

    /// <summary>The group is hardware skinned.</summary>
    IsHwSkinned = 0x02,

    /// <summary>The group is delta flexed.</summary>
    IsDeltaFlexed = 0x04,

    /// <summary>A run-time flag.</summary>
    SuppressHwMorph = 0x08,
}

/// <summary>
/// The flags of <see cref="VtxMeshHeader.Flags"/>
/// (<c>enum MeshFlags_t</c>).
/// </summary>
[Flags]
public enum VtxMeshFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The mesh is teeth.</summary>
    IsTeeth = 0x01,

    /// <summary>The mesh is eyes.</summary>
    IsEyes = 0x02,
}
