using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// The leaf-ambient lumps are read as two different structs depending on their
/// version, like <see cref="BspLump.Leafs"/> and for a sharper reason.
/// </summary>
public class LeafAmbientSizeTests
{
    [Theory]
    [InlineData(BspLump.LeafAmbientLighting)]
    [InlineData(BspLump.LeafAmbientLightingHdr)]
    public void AtVersionOneTheLumpIsAmbientLightingRecords(BspLump lump)
    {
        Assert.Equal(28, BspLumpLayout.ElementSize(lump, 1));
    }

    [Theory]
    [InlineData(BspLump.LeafAmbientLighting)]
    [InlineData(BspLump.LeafAmbientLightingHdr)]
    public void AtAnyOtherVersionTheSameLumpIsCompressedLightCubes(BspLump lump)
    {
        // On the legacy branch the reference implementation casts the
        // SAME lump to CompressedLightCube* and asserts its length divides by
        // 24, not 28. Answering 28 unconditionally made a legacy map's lump
        // look misaligned when it was correct -- found by the validator lane,
        // which could not fix it because this file belongs to MapFormats.
        Assert.Equal(24, BspLumpLayout.ElementSize(lump, 0));
    }

    [Fact]
    public void TheTwoStructsReallyAreDifferentSizes()
    {
        // If these were equal the fix above would be untestable and the bug
        // would have been invisible, which is most of why it survived.
        Assert.NotEqual(
            BspLumpLayout.ElementSize(BspLump.LeafAmbientLighting, 1),
            BspLumpLayout.ElementSize(BspLump.LeafAmbientLighting, 0));
    }
}
