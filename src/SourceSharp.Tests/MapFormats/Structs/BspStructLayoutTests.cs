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
/// Every number here was computed from <c>src/public/bspfile.h</c> by hand and
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
        // bspfile.h:374 -- four ints.
        Assert.Equal(16, Unsafe.SizeOf<LumpHeader>());
    }

    [Fact]
    public void LumpFileHeaderIsTwentyBytes()
    {
        // bspfile.h:404 -- five ints.
        Assert.Equal(20, Unsafe.SizeOf<LumpFileHeader>());
    }

    [Fact]
    public void GameLumpDirectoryEntryIsSixteenBytes()
    {
        // bspfile.h:429 -- int, two ushorts, two ints.
        Assert.Equal(16, Unsafe.SizeOf<DGameLump>());
    }

    [Fact]
    public void ModelIsFortyEightBytes()
    {
        // bspfile.h:441 -- three Vectors and three ints.
        Assert.Equal(48, Unsafe.SizeOf<DModel>());
    }

    [Fact]
    public void PhysModelIsSixteenBytes()
    {
        // bspfile.h:450 -- four ints.
        Assert.Equal(16, Unsafe.SizeOf<DPhysModel>());
    }

    [Fact]
    public void VertexIsAVec3()
    {
        // bspfile.h:467 -- dvertex_t is a bare Vector, which is why the vertex
        // lump has no struct of its own in this port.
        Assert.Equal(12, Unsafe.SizeOf<Vec3>());
    }

    [Fact]
    public void PlaneIsTwentyBytes()
    {
        // bspfile.h:475 -- Vector, float, int.
        Assert.Equal(20, Unsafe.SizeOf<DPlane>());
    }

    [Fact]
    public void NodeIsThirtyTwoBytes()
    {
        // bspfile.h:486 -- the fields total 30 and C rounds to 32.
        Assert.Equal(32, Unsafe.SizeOf<DNode>());
    }

    [Fact]
    public void TexInfoIsSeventyTwoBytes()
    {
        // bspfile.h:499 -- two float[2][4] matrices and two ints.
        Assert.Equal(72, Unsafe.SizeOf<TexInfo>());
    }

    [Fact]
    public void TexDataIsThirtyTwoBytes()
    {
        // bspfile.h:510 -- Vector and five ints.
        Assert.Equal(32, Unsafe.SizeOf<DTexData>());
    }

    [Fact]
    public void OccluderDataIsFortyBytes()
    {
        // bspfile.h:529 -- three ints, two Vectors, one int.
        Assert.Equal(40, Unsafe.SizeOf<DOccluderData>());
    }

    [Fact]
    public void OccluderDataVersionOneIsThirtySixBytes()
    {
        // bspfile.h:540 -- the same without the area, which is the whole
        // difference between occlusion lump versions 1 and 2.
        Assert.Equal(36, Unsafe.SizeOf<DOccluderDataV1>());
    }

    [Fact]
    public void OccluderPolyDataIsTwelveBytes()
    {
        // bspfile.h:549 -- three ints.
        Assert.Equal(12, Unsafe.SizeOf<DOccluderPolyData>());
    }

    [Fact]
    public void DispSubNeighborIsSixBytes()
    {
        // bspfile.h:559 -- ushort plus three bytes is 5, and the ushort forces
        // a trailing pad byte. Get this one wrong by a byte and ddispinfo_t is
        // out by eight.
        Assert.Equal(6, Unsafe.SizeOf<DispSubNeighbor>());
    }

    [Fact]
    public void DispNeighborIsTwelveBytes()
    {
        // bspfile.h:585 -- two sub-neighbours.
        Assert.Equal(12, Unsafe.SizeOf<DispNeighbor>());
    }

    [Fact]
    public void DispCornerNeighborsIsTenBytes()
    {
        // bspfile.h:602 -- ushort[4] plus a byte is 9, padded to 10.
        Assert.Equal(10, Unsafe.SizeOf<DispCornerNeighbors>());
    }

    [Fact]
    public void DispVertIsTwentyBytes()
    {
        // bspfile.h:615 -- Vector and two floats.
        Assert.Equal(20, Unsafe.SizeOf<DispVert>());
    }

    [Fact]
    public void DispTriIsTwoBytes()
    {
        // bspfile.h:631 -- one ushort.
        Assert.Equal(2, Unsafe.SizeOf<DispTri>());
    }

    [Fact]
    public void DispInfoIsOneHundredSeventySixBytes()
    {
        // bspfile.h:638. Arrived at through three separate paddings and a
        // 10-word allowed-verts array, so it is the single most fragile size
        // in the format.
        Assert.Equal(176, Unsafe.SizeOf<DispInfo>());
    }

    [Fact]
    public void DispInfoAllowedVertsIsTenWords()
    {
        // bspfile.h:665 -- PAD_NUMBER(289, 32) / 32. The lane notes flag this
        // as a genuine 64-bit-Linux layout fix: an "unsigned long" array here
        // would be 80 bytes on LP64 and ddispinfo_t would read 216.
        Assert.Equal(40, Unsafe.SizeOf<UIntArray10>());
    }

    [Fact]
    public void EdgeIsFourBytes()
    {
        // bspfile.h:673 -- two ushorts.
        Assert.Equal(4, Unsafe.SizeOf<DEdge>());
    }

    [Fact]
    public void PrimitiveIsTenBytes()
    {
        // bspfile.h:687 -- a byte then four ushorts, so C inserts one pad byte
        // after the type and the struct is 10, not 9.
        Assert.Equal(10, Unsafe.SizeOf<DPrimitive>());
    }

    [Fact]
    public void FaceIsFiftySixBytes()
    {
        // bspfile.h:703.
        Assert.Equal(56, Unsafe.SizeOf<DFace>());
    }

    [Fact]
    public void FaceIdIsTwoBytes()
    {
        // bspfile.h:782 -- one ushort.
        Assert.Equal(2, Unsafe.SizeOf<DFaceId>());
    }

    [Fact]
    public void LeafVersionZeroIsFiftySixBytes()
    {
        // bspfile.h:799 -- with the ambient cube inline: 54 bytes of fields
        // padded to 56. dm_lockdown.bsp records LEAFS at version 0 and its
        // lump divides by exactly this.
        Assert.Equal(56, Unsafe.SizeOf<DLeafVersion0>());
    }

    [Fact]
    public void LeafVersionOneIsThirtyTwoBytes()
    {
        // bspfile.h:826 -- the ambient cube moved out at version 1, leaving 30
        // bytes of fields padded to 32.
        Assert.Equal(32, Unsafe.SizeOf<DLeaf>());
    }

    [Fact]
    public void LeafAmbientLightingIsTwentyEightBytes()
    {
        // bspfile.h:860 -- a light cube and four bytes.
        Assert.Equal(28, Unsafe.SizeOf<DLeafAmbientLighting>());
    }

    [Fact]
    public void LeafAmbientIndexIsFourBytes()
    {
        // bspfile.h:870 -- two ushorts.
        Assert.Equal(4, Unsafe.SizeOf<DLeafAmbientIndex>());
    }

    [Fact]
    public void BrushSideIsEightBytes()
    {
        // bspfile.h:878 -- four 16-bit fields.
        Assert.Equal(8, Unsafe.SizeOf<DBrushSide>());
    }

    [Fact]
    public void BrushIsTwelveBytes()
    {
        // bspfile.h:887 -- three ints.
        Assert.Equal(12, Unsafe.SizeOf<DBrush>());
    }

    [Fact]
    public void AreaPortalIsTwelveBytes()
    {
        // bspfile.h:913 -- four ushorts and an int.
        Assert.Equal(12, Unsafe.SizeOf<DAreaPortal>());
    }

    [Fact]
    public void AreaIsEightBytes()
    {
        // bspfile.h:929 -- two ints.
        Assert.Equal(8, Unsafe.SizeOf<DArea>());
    }

    [Fact]
    public void LeafWaterDataIsTwelveBytes()
    {
        // bspfile.h:936 -- two floats and a short, padded from 10 to 12.
        Assert.Equal(12, Unsafe.SizeOf<DLeafWaterData>());
    }

    [Fact]
    public void FaceMacroTextureInfoIsTwoBytes()
    {
        // bspfile.h:944 -- one ushort.
        Assert.Equal(2, Unsafe.SizeOf<FaceMacroTextureInfo>());
    }

    [Fact]
    public void WorldLightIsEightyEightBytes()
    {
        // bspfile.h:969 -- three Vectors and sixteen 4-byte fields.
        Assert.Equal(88, Unsafe.SizeOf<DWorldLight>());
    }

    [Fact]
    public void CubemapSampleIsSixteenBytes()
    {
        // bspfile.h:992 -- int[3] plus a byte, padded from 13 to 16.
        Assert.Equal(16, Unsafe.SizeOf<DCubemapSample>());
    }

    [Fact]
    public void OverlayIsThreeHundredFiftyTwoBytes()
    {
        // bspfile.h:1007 -- the 64-entry face array dominates it.
        Assert.Equal(352, Unsafe.SizeOf<DOverlay>());
    }

    [Fact]
    public void OverlayFadeIsEightBytes()
    {
        // bspfile.h:1056 -- two floats.
        Assert.Equal(8, Unsafe.SizeOf<DOverlayFade>());
    }

    [Fact]
    public void WaterOverlayIsOneThousandOneHundredTwentyBytes()
    {
        // bspfile.h:1069 -- the same as doverlay_t with a 256-entry face array.
        Assert.Equal(1120, Unsafe.SizeOf<DWaterOverlay>());
    }

    [Fact]
    public void ColorRgbExp32IsFourBytes()
    {
        // mathlib.h:990 -- three bytes and a signed char.
        Assert.Equal(4, Unsafe.SizeOf<ColorRgbExp32>());
    }

    [Fact]
    public void CompressedLightCubeIsTwentyFourBytes()
    {
        // compressed_light_cube.h:17 -- six samples.
        Assert.Equal(24, Unsafe.SizeOf<CompressedLightCube>());
    }

    [Fact]
    public void MapFlagsLumpIsFourBytes()
    {
        // bspfile.h:398 -- one uint32.
        Assert.Equal(4, Unsafe.SizeOf<DFlagsLump>());
    }

    [Fact]
    public void PhysDispHeaderIsTwoBytes()
    {
        // bspfile.h:460 -- one ushort, then two ragged runs the struct only
        // describes in a comment.
        Assert.Equal(2, Unsafe.SizeOf<DPhysDisp>());
    }
}
