using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Assets;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Assets;

/// <summary>
/// The sizes and offsets of the studio model structs.
/// </summary>
/// <remarks>
/// <para>
/// Two hazards run through the reference layout, and every
/// fact here is about one of them.
/// </para>
/// <para>
/// First, the reference headers are 64-bit ported, so several structs have
/// <c>#ifdef PLATFORM_64BITS</c> branches that shorten a reserved array to pay
/// for a wider pointer. The FILE is always the 32-bit branch, and taking the
/// 64-bit one would read every model in the game wrongly on this very machine.
/// </para>
/// <para>
/// Second, the reference implementation wraps the whole VTX block in
/// <c>#pragma pack(1)</c>, which makes five of its structs odd sizes that
/// natural alignment would round up.
/// </para>
/// </remarks>
public class StudioStructTests
{
    private static int OffsetOf<TStruct, TField>(ref TStruct value, ref TField field)
        where TStruct : unmanaged =>
        (int)Unsafe.ByteOffset(
            ref Unsafe.As<TStruct, byte>(ref value), ref Unsafe.As<TField, byte>(ref field));

    [Fact]
    public void StudioHeaderIsFourHundredEightBytes()
    {
        // The reference layout, with the four pointer members at four bytes each.
        Assert.Equal(408, Unsafe.SizeOf<StudioHeader>());
    }

    [Fact]
    public void StudioHeaderTwoIsTwoHundredFiftySixBytes()
    {
        // Layout: seven ints and a float (32 bytes) then
        // int reserved[56] (224). The 64-bit branch carves four pointers out
        // of the FRONT of that array and keeps the same total,
        // which is why the size alone cannot tell the branches apart and the
        // 32-bit shape has to be chosen deliberately.
        Assert.Equal(256, Unsafe.SizeOf<StudioHeader2>());
    }

    [Fact]
    public void StudioHeaderNameStartsAtTwelve()
    {
        StudioHeader header = default;
        Assert.Equal(12, OffsetOf(ref header, ref header.Name));
    }

    [Fact]
    public void StudioHeaderHullMinIsAtOneHundredFour()
    {
        // ident, version, checksum, name[64], length, then two Vectors. vbsp
        // reads the hull to size a static prop, so this offset is directly
        // load bearing for a compile.
        StudioHeader header = default;
        Assert.Equal(104, OffsetOf(ref header, ref header.HullMin));
    }

    [Fact]
    public void StudioHeaderBoneIndexIsAtOneHundredSixty()
    {
        StudioHeader header = default;
        Assert.Equal(160, OffsetOf(ref header, ref header.BoneIndex));
    }

    [Fact]
    public void StudioHeaderStudioHdr2IndexIsAtFourHundred()
    {
        // The last real field, four bytes before the end.
        StudioHeader header = default;
        Assert.Equal(400, OffsetOf(ref header, ref header.StudioHdr2Index));
    }

    [Fact]
    public void StudioBoneIsTwoHundredSixteenBytes()
    {
        Assert.Equal(216, Unsafe.SizeOf<StudioBone>());
    }

    [Fact]
    public void StudioBboxIsSixtyEightBytes()
    {
        Assert.Equal(68, Unsafe.SizeOf<StudioBbox>());
    }

    [Fact]
    public void StudioHitboxSetIsTwelveBytes()
    {
        Assert.Equal(12, Unsafe.SizeOf<StudioHitboxSet>());
    }

    [Fact]
    public void StudioBodyPartsIsSixteenBytes()
    {
        Assert.Equal(16, Unsafe.SizeOf<StudioBodyParts>());
    }

    [Fact]
    public void StudioModelIsOneHundredFortyEightBytes()
    {
        // Layout: name[64] + 9 ints + 8 bytes of run-time pointers +
        // int unused[8]. The 64-bit branch shortens that array to six
        // would give 140.
        Assert.Equal(148, Unsafe.SizeOf<StudioModel>());
    }

    [Fact]
    public void StudioMeshIsOneHundredSixteenBytes()
    {
        // 9 ints, a Vector, a run-time pointer, the real
        // numLODVertexes[8], and int unused[8].
        Assert.Equal(116, Unsafe.SizeOf<StudioMesh>());
    }

    [Fact]
    public void StudioMeshLodVertexCountsAreRealOnDiskDataAtOffsetFiftyTwo()
    {
        // The one genuinely on-disk member of mstudio_meshvertexdata_t
        // : studiomdl writes it and the LOD culling reads it.
        // The pointer that precedes it is not.
        StudioMesh mesh = default;
        Assert.Equal(48, OffsetOf(ref mesh, ref mesh.ModelVertexDataPointer));
        Assert.Equal(52, OffsetOf(ref mesh, ref mesh.NumLodVertexes));
    }

    [Fact]
    public void StudioTextureIsSixtyFourBytes()
    {
        // Layout: four ints, two run-time pointers, int unused[10].
        // The 64-bit branch has unused[8] and would give 64 as well but with
        // the fields in different places, which is why the offsets matter.
        Assert.Equal(64, Unsafe.SizeOf<StudioTexture>());
    }

    [Fact]
    public void StudioBoneWeightIsExactlySixteenBytes()
    {
        // Says so in a comment, and the struct has no padding:
        // three floats, three chars, one byte.
        Assert.Equal(16, Unsafe.SizeOf<StudioBoneWeight>());
    }

    [Fact]
    public void StudioVertexIsExactlyFortyEightBytes()
    {
        // Says so in a comment. It is also how
        // mstudiomodel_t::vertexindex converts from a byte offset to an index.
        Assert.Equal(48, Unsafe.SizeOf<StudioVertex>());
    }

    [Fact]
    public void StudioVertexPutsTheBoneWeightsFirstAndTheTexcoordLast()
    {
        StudioVertex vertex = default;
        Assert.Equal(0, OffsetOf(ref vertex, ref vertex.BoneWeights));
        Assert.Equal(16, OffsetOf(ref vertex, ref vertex.Position));
        Assert.Equal(28, OffsetOf(ref vertex, ref vertex.Normal));
        Assert.Equal(40, OffsetOf(ref vertex, ref vertex.TexCoord));
    }

    [Fact]
    public void VertexFileHeaderIsSixtyFourBytes()
    {
        // Layout: four ints, int[8], four ints.
        Assert.Equal(64, Unsafe.SizeOf<VertexFileHeader>());
    }

    [Fact]
    public void VertexFileFixupIsTwelveBytes()
    {
        Assert.Equal(12, Unsafe.SizeOf<VertexFileFixup>());
    }

    [Fact]
    public void VtxFileHeaderIsThirtySixBytes()
    {
        // Under pack(1): two ints, two ushorts, five ints.
        Assert.Equal(36, Unsafe.SizeOf<VtxFileHeader>());
    }

    [Fact]
    public void VtxBodyPartIsEightBytes()
    {
        Assert.Equal(8, Unsafe.SizeOf<VtxBodyPartHeader>());
    }

    [Fact]
    public void VtxModelIsEightBytes()
    {
        Assert.Equal(8, Unsafe.SizeOf<VtxModelHeader>());
    }

    [Fact]
    public void VtxModelLodIsTwelveBytes()
    {
        Assert.Equal(12, Unsafe.SizeOf<VtxModelLodHeader>());
    }

    [Fact]
    public void VtxMeshIsNineBytesNotTwelve()
    {
        // Under pack(1): two ints and a byte. Natural alignment
        // would round this to 12.
        Assert.Equal(9, Unsafe.SizeOf<VtxMeshHeader>());
    }

    [Fact]
    public void VtxStripGroupIsTwentyFiveBytesNotTwentyEight()
    {
        // Under pack(1): six ints and a byte.
        Assert.Equal(25, Unsafe.SizeOf<VtxStripGroupHeader>());
    }

    [Fact]
    public void VtxStripIsTwentySevenBytesNotThirtyTwo()
    {
        // Under pack(1).
        Assert.Equal(27, Unsafe.SizeOf<VtxStripHeader>());
    }

    [Fact]
    public void VtxVertexIsNineBytesNotTen()
    {
        // Under pack(1): uchar[3], uchar, ushort, char[3]. The
        // single most consequential packing in this port -- a 10-byte stride
        // reads every vertex after the first one byte too far and produces
        // geometry that is wrong rather than obviously broken.
        Assert.Equal(9, Unsafe.SizeOf<VtxVertex>());
    }

    [Fact]
    public void VtxVertexOrigMeshVertIdIsAtOffsetFour()
    {
        VtxVertex vertex = default;
        Assert.Equal(0, OffsetOf(ref vertex, ref vertex.BoneWeightIndex));
        Assert.Equal(3, OffsetOf(ref vertex, ref vertex.NumBones));
        Assert.Equal(4, OffsetOf(ref vertex, ref vertex.OrigMeshVertId));
        Assert.Equal(6, OffsetOf(ref vertex, ref vertex.BoneId));
    }

    [Fact]
    public void VtxBoneStateChangeIsEightBytes()
    {
        Assert.Equal(8, Unsafe.SizeOf<VtxBoneStateChangeHeader>());
    }

    [Fact]
    public void VtxMaterialReplacementIsSixBytesNotEight()
    {
        // Under pack(1): a short then an int.
        Assert.Equal(6, Unsafe.SizeOf<VtxMaterialReplacementHeader>());
    }

    [Fact]
    public void VtxMaterialReplacementListIsEightBytes()
    {
        Assert.Equal(8, Unsafe.SizeOf<VtxMaterialReplacementListHeader>());
    }

    [Fact]
    public void TheMdlIdentSpellsIdst()
    {
        Assert.Equal("IDST", Fourcc(StudioIdents.Mdl));
    }

    [Fact]
    public void TheVvdIdentSpellsIdsv()
    {
        // Builds it as ('V'<<24)+('S'<<16)+('D'<<8)+'I', the
        // hand-reversed form, so 'I' is the LOW byte and the file reads IDSV.
        Assert.Equal("IDSV", Fourcc(StudioIdents.Vvd));
    }

    [Fact]
    public void TheThinVvdIdentSpellsIdcv()
    {
        Assert.Equal("IDCV", Fourcc(StudioIdents.VvdThin));
    }

    [Fact]
    public void MaxBonesPerVertIsThree()
    {
        // The reference fixes it at three, and it sizes both mstudioboneweight_t and Vertex_t.
        Assert.Equal(3, StudioIdents.MaxBonesPerVert);
    }

    private static string Fourcc(int id) => new(
    [
        (char)(id & 0xFF),
        (char)((id >> 8) & 0xFF),
        (char)((id >> 16) & 0xFF),
        (char)((id >> 24) & 0xFF),
    ]);
}
