using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Structs;

/// <summary>
/// The sizes of the BSP lump structs.
/// </summary>
/// <remarks>
/// <para>
/// Every number here was computed from the reference implementation by hand and
/// each one is a separate fact, because a bulk "all sizes" assertion tells you
/// only that SOMETHING moved. The ones with explicit padding are the ones that
/// break silently: <c>Pack = 1</c> without the pad field gives a struct that
/// compiles, casts, and shifts every element after the first.
/// </para>
/// <para>
/// These facts need no game content, which is the point: they fail in a fresh
/// checkout with no compiled map in it.
/// </para>
/// </remarks>
public class BspStructLayoutTests
{
    [Fact]
    public void LumpHeaderIsSixteenBytes()
    {
        // Layout: four ints.
        Assert.Equal(16, Unsafe.SizeOf<LumpHeader>());
    }

    [Fact]
    public void LumpFileHeaderIsTwentyBytes()
    {
        // Layout: five ints.
        Assert.Equal(20, Unsafe.SizeOf<LumpFileHeader>());
    }

    [Fact]
    public void GameLumpDirectoryEntryIsSixteenBytes()
    {
        // Layout: int, two ushorts, two ints.
        Assert.Equal(16, Unsafe.SizeOf<DGameLump>());
    }

    [Fact]
    public void ModelIsFortyEightBytes()
    {
        // Layout: three Vectors and three ints.
        Assert.Equal(48, Unsafe.SizeOf<DModel>());
    }

    [Fact]
    public void PhysModelIsSixteenBytes()
    {
        // Layout: four ints.
        Assert.Equal(16, Unsafe.SizeOf<DPhysModel>());
    }

    [Fact]
    public void VertexIsAVec3()
    {
        // Dvertex_t is a bare Vector, which is why the vertex
        // lump has no struct of its own in this port.
        Assert.Equal(12, Unsafe.SizeOf<Vec3>());
    }

    [Fact]
    public void PlaneIsTwentyBytes()
    {
        // Layout: Vector, float, int.
        Assert.Equal(20, Unsafe.SizeOf<DPlane>());
    }

    [Fact]
    public void NodeIsThirtyTwoBytes()
    {
        // The fields total 30 and C rounds to 32.
        Assert.Equal(32, Unsafe.SizeOf<DNode>());
    }

    [Fact]
    public void TexInfoIsSeventyTwoBytes()
    {
        // Layout: two float[2][4] matrices and two ints.
        Assert.Equal(72, Unsafe.SizeOf<TexInfo>());
    }

    [Fact]
    public void TexDataIsThirtyTwoBytes()
    {
        // Layout: Vector and five ints.
        Assert.Equal(32, Unsafe.SizeOf<DTexData>());
    }

    [Fact]
    public void OccluderDataIsFortyBytes()
    {
        // Layout: three ints, two Vectors, one int.
        Assert.Equal(40, Unsafe.SizeOf<DOccluderData>());
    }

    [Fact]
    public void OccluderDataVersionOneIsThirtySixBytes()
    {
        // The same without the area, which is the whole
        // difference between occlusion lump versions 1 and 2.
        Assert.Equal(36, Unsafe.SizeOf<DOccluderDataV1>());
    }

    [Fact]
    public void OccluderPolyDataIsTwelveBytes()
    {
        // Layout: three ints.
        Assert.Equal(12, Unsafe.SizeOf<DOccluderPolyData>());
    }

    [Fact]
    public void DispSubNeighborIsSixBytes()
    {
        // Layout: ushort plus three bytes is 5, and the ushort forces
        // a trailing pad byte. Get this one wrong by a byte and ddispinfo_t is
        // out by eight.
        Assert.Equal(6, Unsafe.SizeOf<DispSubNeighbor>());
    }

    [Fact]
    public void DispNeighborIsTwelveBytes()
    {
        // Layout: two sub-neighbours.
        Assert.Equal(12, Unsafe.SizeOf<DispNeighbor>());
    }

    [Fact]
    public void DispCornerNeighborsIsTenBytes()
    {
        // Layout: ushort[4] plus a byte is 9, padded to 10.
        Assert.Equal(10, Unsafe.SizeOf<DispCornerNeighbors>());
    }

    [Fact]
    public void DispVertIsTwentyBytes()
    {
        // Layout: Vector and two floats.
        Assert.Equal(20, Unsafe.SizeOf<DispVert>());
    }

    [Fact]
    public void DispTriIsTwoBytes()
    {
        // Layout: one ushort.
        Assert.Equal(2, Unsafe.SizeOf<DispTri>());
    }

    [Fact]
    public void DispInfoIsOneHundredSeventySixBytes()
    {
        // The reference implementation. Arrived at through three separate paddings and a
        // 10-word allowed-verts array, so it is the single most fragile size
        // in the format.
        Assert.Equal(176, Unsafe.SizeOf<DispInfo>());
    }

    [Fact]
    public void DispInfoAllowedVertsIsTenWords()
    {
        // PAD_NUMBER(289, 32) / 32. An "unsigned long" array here
        // would be 80 bytes on LP64 and ddispinfo_t would read 216, so the
        // field's element type is pinned to a fixed width.
        Assert.Equal(40, Unsafe.SizeOf<UIntArray10>());
    }

    [Fact]
    public void EdgeIsFourBytes()
    {
        // Layout: two ushorts.
        Assert.Equal(4, Unsafe.SizeOf<DEdge>());
    }

    [Fact]
    public void PrimitiveIsTenBytes()
    {
        // Layout: a byte then four ushorts, so C inserts one pad byte
        // after the type and the struct is 10, not 9.
        Assert.Equal(10, Unsafe.SizeOf<DPrimitive>());
    }

    [Fact]
    public void FaceIsFiftySixBytes()
    {
        Assert.Equal(56, Unsafe.SizeOf<DFace>());
    }

    [Fact]
    public void FaceIdIsTwoBytes()
    {
        // Layout: one ushort.
        Assert.Equal(2, Unsafe.SizeOf<DFaceId>());
    }

    [Fact]
    public void LeafVersionZeroIsFiftySixBytes()
    {
        // With the ambient cube inline: 54 bytes of fields
        // padded to 56. dm_lockdown.bsp records LEAFS at version 0 and its
        // lump divides by exactly this.
        Assert.Equal(56, Unsafe.SizeOf<DLeafVersion0>());
    }

    [Fact]
    public void LeafVersionOneIsThirtyTwoBytes()
    {
        // The ambient cube moved out at version 1, leaving 30
        // bytes of fields padded to 32.
        Assert.Equal(32, Unsafe.SizeOf<DLeaf>());
    }

    [Fact]
    public void LeafAmbientLightingIsTwentyEightBytes()
    {
        // A light cube and four bytes.
        Assert.Equal(28, Unsafe.SizeOf<DLeafAmbientLighting>());
    }

    [Fact]
    public void LeafAmbientIndexIsFourBytes()
    {
        // Layout: two ushorts.
        Assert.Equal(4, Unsafe.SizeOf<DLeafAmbientIndex>());
    }

    [Fact]
    public void BrushSideIsEightBytes()
    {
        // Layout: four 16-bit fields.
        Assert.Equal(8, Unsafe.SizeOf<DBrushSide>());
    }

    [Fact]
    public void BrushIsTwelveBytes()
    {
        // Layout: three ints.
        Assert.Equal(12, Unsafe.SizeOf<DBrush>());
    }

    [Fact]
    public void AreaPortalIsTwelveBytes()
    {
        // Layout: four ushorts and an int.
        Assert.Equal(12, Unsafe.SizeOf<DAreaPortal>());
    }

    [Fact]
    public void AreaIsEightBytes()
    {
        // Layout: two ints.
        Assert.Equal(8, Unsafe.SizeOf<DArea>());
    }

    [Fact]
    public void LeafWaterDataIsTwelveBytes()
    {
        // Layout: two floats and a short, padded from 10 to 12.
        Assert.Equal(12, Unsafe.SizeOf<DLeafWaterData>());
    }

    [Fact]
    public void FaceMacroTextureInfoIsTwoBytes()
    {
        // Layout: one ushort.
        Assert.Equal(2, Unsafe.SizeOf<FaceMacroTextureInfo>());
    }

    [Fact]
    public void WorldLightIsEightyEightBytes()
    {
        // Layout: three Vectors and sixteen 4-byte fields.
        Assert.Equal(88, Unsafe.SizeOf<DWorldLight>());
    }

    [Fact]
    public void CubemapSampleIsSixteenBytes()
    {
        // Layout: int[3] plus a byte, padded from 13 to 16.
        Assert.Equal(16, Unsafe.SizeOf<DCubemapSample>());
    }

    [Fact]
    public void OverlayIsThreeHundredFiftyTwoBytes()
    {
        // The 64-entry face array dominates it.
        Assert.Equal(352, Unsafe.SizeOf<DOverlay>());
    }

    [Fact]
    public void OverlayFadeIsEightBytes()
    {
        // Layout: two floats.
        Assert.Equal(8, Unsafe.SizeOf<DOverlayFade>());
    }

    [Fact]
    public void WaterOverlayIsOneThousandOneHundredTwentyBytes()
    {
        // The same as doverlay_t with a 256-entry face array.
        Assert.Equal(1120, Unsafe.SizeOf<DWaterOverlay>());
    }

    [Fact]
    public void ColorRgbExp32IsFourBytes()
    {
        // Layout: three bytes and a signed char.
        Assert.Equal(4, Unsafe.SizeOf<ColorRgbExp32>());
    }

    [Fact]
    public void CompressedLightCubeIsTwentyFourBytes()
    {
        // Layout: six samples.
        Assert.Equal(24, Unsafe.SizeOf<CompressedLightCube>());
    }

    [Fact]
    public void MapFlagsLumpIsFourBytes()
    {
        // Layout: one uint32.
        Assert.Equal(4, Unsafe.SizeOf<DFlagsLump>());
    }

    [Fact]
    public void PhysDispHeaderIsTwoBytes()
    {
        // Layout: one ushort, then two ragged runs the struct only
        // describes in a comment.
        Assert.Equal(2, Unsafe.SizeOf<DPhysDisp>());
    }
}
