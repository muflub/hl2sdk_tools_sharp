using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Structs;

/// <summary>
/// Where the fields of the BSP lump structs actually sit, and what their
/// accessors mean.
/// </summary>
/// <remarks>
/// A size fact catches a struct that is the wrong length. It does NOT catch two
/// fields that are the same width and swapped, or a pad byte put at the front
/// instead of the back. These facts do.
/// </remarks>
public class BspStructFieldTests
{
    private static int OffsetOf<TStruct, TField>(ref TStruct value, ref TField field)
        where TStruct : unmanaged =>
        (int)Unsafe.ByteOffset(ref Unsafe.As<TStruct, byte>(ref value), ref Unsafe.As<TField, byte>(ref field));

    [Fact]
    public void TexDataStartsWithReflectivity()
    {
        // bspfile.h:513 -- reflectivity is the FIRST field. vbsp copies it out
        // of the VTF header, so this offset is where a wrong VTF layout would
        // surface.
        DTexData value = default;
        Assert.Equal(0, OffsetOf(ref value, ref value.Reflectivity));
    }

    [Fact]
    public void TexDataNameStringTableIdFollowsTheReflectivity()
    {
        DTexData value = default;
        Assert.Equal(12, OffsetOf(ref value, ref value.NameStringTableId));
    }

    [Fact]
    public void DispInfoAllowedVertsIsTheLastField()
    {
        // bspfile.h:666 -- 40 bytes of bit vector ending the struct at 176.
        DispInfo value = default;
        Assert.Equal(136, OffsetOf(ref value, ref value.AllowedVerts));
    }

    [Fact]
    public void DispInfoPaddingSitsBetweenMapFaceAndLightmapAlphaStart()
    {
        // The pad is at offset 38, NOT at the end: an int follows and it needs
        // 4-byte alignment. Putting the two bytes anywhere else keeps the size
        // at 176 and still reads every later field wrong.
        DispInfo value = default;
        Assert.Equal(36, OffsetOf(ref value, ref value.MapFace));
        Assert.Equal(38, OffsetOf(ref value, ref value.Padding));
        Assert.Equal(40, OffsetOf(ref value, ref value.LightmapAlphaStart));
    }

    [Fact]
    public void LeafPaddingIsTrailing()
    {
        // bspfile.h:846 -- leafWaterDataID is the last real field, at 28.
        DLeaf value = default;
        Assert.Equal(28, OffsetOf(ref value, ref value.LeafWaterDataId));
        Assert.Equal(30, OffsetOf(ref value, ref value.Padding));
    }

    [Fact]
    public void LeafVersionZeroPutsTheAmbientCubeAfterTheWaterDataId()
    {
        // bspfile.h:822 -- the cube is the field version 1 removed, and it sits
        // at the END, so a version 0 leaf is a version 1 leaf's first 30 bytes
        // followed by 24 bytes of light.
        DLeafVersion0 value = default;
        Assert.Equal(28, OffsetOf(ref value, ref value.LeafWaterDataId));
        Assert.Equal(30, OffsetOf(ref value, ref value.AmbientLighting));
        Assert.Equal(54, OffsetOf(ref value, ref value.Padding));
    }

    [Fact]
    public void PrimitivePaddingSitsAfterTheTypeByte()
    {
        DPrimitive value = default;
        Assert.Equal(0, OffsetOf(ref value, ref value.Type));
        Assert.Equal(1, OffsetOf(ref value, ref value.Padding));
        Assert.Equal(2, OffsetOf(ref value, ref value.FirstIndex));
    }

    [Fact]
    public void FaceSmoothingGroupsAreTheLastFourBytes()
    {
        DFace value = default;
        Assert.Equal(52, OffsetOf(ref value, ref value.SmoothingGroups));
    }

    [Fact]
    public void LeafAreaIsTheLowNineBitsAndFlagsTheTopSeven()
    {
        // bspfile.h:833 -- short area:9; short flags:7. Little-endian bitfields
        // allocate from the low bit, so area is 0..8 and flags 9..15.
        DLeaf leaf = default;
        leaf.SetAreaFlags(511, LeafFlags.Sky | LeafFlags.Sky2D);

        Assert.Equal(511, leaf.GetArea());
    }

    [Fact]
    public void LeafFlagsSurviveAMaximumArea()
    {
        DLeaf leaf = default;
        leaf.SetAreaFlags(511, LeafFlags.Sky | LeafFlags.Sky2D);

        Assert.Equal(LeafFlags.Sky | LeafFlags.Sky2D, leaf.GetFlags());
    }

    [Fact]
    public void LeafAreaFlagsPackIntoOneSpecificWord()
    {
        // A round trip through the accessors would pass with the fields the
        // wrong way round. The raw word is the actual claim.
        DLeaf leaf = default;
        leaf.SetAreaFlags(3, LeafFlags.Radial);

        Assert.Equal((ushort)(3 | (0x02 << 9)), leaf.AreaFlags);
    }

    [Fact]
    public void LeafRejectsAnAreaThatWouldOverflowIntoTheFlags()
    {
        DLeaf leaf = default;
        Assert.Throws<ArgumentOutOfRangeException>(() => leaf.SetAreaFlags(512, LeafFlags.None));
    }

    [Fact]
    public void FacePrimitiveCountIgnoresTheShadowBit()
    {
        // bspfile.h:757 -- GetNumPrims masks with 0x7FFF.
        DFace face = default;
        face.NumPrimsAndFlags = 0x8007;

        Assert.Equal(7, face.GetNumPrims());
    }

    [Fact]
    public void FaceDynamicShadowsAreDisabledByTheTopBitBeingSet()
    {
        // bspfile.h:769 -- the sense is inverted, which is the easy bug.
        DFace face = default;
        face.NumPrimsAndFlags = 0x8000;

        Assert.False(face.AreDynamicShadowsEnabled());
    }

    [Fact]
    public void FaceSettingPrimitiveCountPreservesTheShadowBit()
    {
        DFace face = default;
        face.SetDynamicShadowsEnabled(false);
        face.SetNumPrims(12);

        Assert.False(face.AreDynamicShadowsEnabled());
        Assert.Equal(12, face.GetNumPrims());
    }

    [Fact]
    public void FaceRejectsAPrimitiveCountThatWouldCollideWithTheShadowBit()
    {
        DFace face = default;
        Assert.Throws<ArgumentOutOfRangeException>(() => face.SetNumPrims(0x8000));
    }

    [Fact]
    public void OverlayFaceCountIsTheLowFourteenBits()
    {
        // bspfile.h:1039 -- masked with ~0xC000.
        DOverlay overlay = default;
        overlay.FaceCountAndRenderOrder = 0xC000 | 17;

        Assert.Equal(17, overlay.GetFaceCount());
    }

    [Fact]
    public void OverlayRenderOrderIsTheTopTwoBits()
    {
        // bspfile.h:1050 -- shifted down by 16 - OVERLAY_RENDER_ORDER_NUM_BITS.
        DOverlay overlay = default;
        overlay.FaceCountAndRenderOrder = 0xC000 | 17;

        Assert.Equal(3, overlay.GetRenderOrder());
    }

    [Fact]
    public void OverlaySettingFaceCountPreservesTheRenderOrder()
    {
        DOverlay overlay = default;
        overlay.SetRenderOrder(2);
        overlay.SetFaceCount(9);

        Assert.Equal(2, overlay.GetRenderOrder());
        Assert.Equal(9, overlay.GetFaceCount());
    }

    [Fact]
    public void WaterOverlayUsesTheSameRenderOrderMaskDespiteHavingFourTimesTheFaces()
    {
        // bspfile.h:1068 -- WATEROVERLAY_RENDER_ORDER_MASK is also 0xC000, so a
        // water overlay's face count still has only 14 bits for its 256 faces.
        DWaterOverlay overlay = default;
        overlay.FaceCountAndRenderOrder = 0x4000 | 256;

        Assert.Equal(256, overlay.GetFaceCount());
        Assert.Equal(1, overlay.GetRenderOrder());
    }

    [Fact]
    public void DispSubNeighborSentinelIsAllOnes()
    {
        // bspfile.h:568 -- SetInvalid writes 0xFFFF.
        Assert.Equal(0xFFFF, DispSubNeighbor.NoNeighbor);
    }

    [Fact]
    public void DispSubNeighborDefaultReadsAsNeighbourZeroNotAsAbsent()
    {
        // The sentinel is 0xFFFF, so a zeroed struct is a VALID reference to
        // displacement 0. This project has already had two lanes measure the
        // wrong path by assuming default(T) meant "none".
        DispSubNeighbor neighbor = default;

        Assert.True(neighbor.IsValid());
    }

    [Fact]
    public void DispInfoVertexCountFollowsThePowerFormula()
    {
        // bspfile.h:53 -- ((1 << power) + 1)^2. Power 4 is the maximum and
        // gives the 289 that sizes m_AllowedVerts.
        DispInfo info = default;
        info.Power = 4;

        Assert.Equal(289, info.NumVerts());
    }

    [Fact]
    public void DispInfoTriangleCountFollowsThePowerFormula()
    {
        // bspfile.h:54 -- 2 * (1 << power)^2.
        DispInfo info = default;
        info.Power = 4;

        Assert.Equal(512, info.NumTris());
    }

    [Fact]
    public void ColorRgbExp32DecodesWithASignedExponent()
    {
        // mathlib.h:993 -- a signed char. Read unsigned, an exponent of -1
        // becomes 255 and the sample is 2^255 times too bright.
        ColorRgbExp32 color = new() { R = 128, G = 64, B = 32, Exponent = -1 };
        Vec3 linear = color.ToLinear();

        Assert.Equal(64.0f, linear.X);
        Assert.Equal(32.0f, linear.Y);
        Assert.Equal(16.0f, linear.Z);
    }

    [Fact]
    public void TexInfoTextureVectorsAreTwoRowsOfFour()
    {
        // bspfile.h:502 -- float[2][4] is row-major, so the t axis starts at
        // element 4 and the s axis' offset is element 3.
        TexInfo info = default;
        info.TextureVecsTexelsPerWorldUnits[3] = 12.5f;
        info.TextureVecsTexelsPerWorldUnits[4] = 1.0f;

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<TexInfo>(in info));
        Assert.Equal(12.5f, BitConverter.ToSingle(bytes.Slice(3 * sizeof(float), sizeof(float))));
        Assert.Equal(1.0f, BitConverter.ToSingle(bytes.Slice(4 * sizeof(float), sizeof(float))));
    }
}
