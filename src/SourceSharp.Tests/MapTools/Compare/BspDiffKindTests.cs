using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Compare;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compare;

/// <summary>
/// The kind table, held to the list the comparison rules write down.
/// </summary>
/// <remarks>
/// The plan names each lump and the kind it is compared by. A table in code
/// that quietly drifted from that list would still pass every other fact here,
/// because every other fact takes the table as given.
/// </remarks>
public sealed class BspDiffKindTests
{
    /// <summary>
    /// The lumps the comparison rules list as having no float freedom.
    /// </summary>
    public static TheoryData<BspLump> ExactLumps =>
    [
        BspLump.Entities,
        BspLump.TexData,
        BspLump.TexDataStringData,
        BspLump.TexDataStringTable,
        BspLump.TexInfo,
        BspLump.Brushes,
        BspLump.BrushSides,
        BspLump.Models,
        BspLump.GameLump,
        BspLump.PakFile,
    ];

    /// <summary>The geometry lumps whose index order is vbsp's, not the map's.</summary>
    public static TheoryData<BspLump> CanonicalLumps =>
    [
        BspLump.Planes,
        BspLump.Faces,
        BspLump.FacesHdr,
        BspLump.OriginalFaces,
        BspLump.Leafs,
    ];

    /// <summary>The lumps that can only be described.</summary>
    public static TheoryData<BspLump> DistributionalLumps =>
    [
        BspLump.Visibility,
        BspLump.Lighting,
        BspLump.LightingHdr,
        BspLump.LeafAmbientLighting,
        BspLump.LeafAmbientLightingHdr,
        BspLump.LeafAmbientIndex,
        BspLump.LeafAmbientIndexHdr,
    ];

    [Theory]
    [MemberData(nameof(ExactLumps))]
    public void ThePlansExactLumpsAreComparedExactly(BspLump lump) =>
        Assert.Equal(DiffKind.Exact, BspDiff.KindOf(lump));

    [Theory]
    [MemberData(nameof(CanonicalLumps))]
    public void ThePlansGeometryLumpsAreComparedAsSets(BspLump lump) =>
        Assert.Equal(DiffKind.CanonicalSet, BspDiff.KindOf(lump));

    [Theory]
    [MemberData(nameof(DistributionalLumps))]
    public void ThePlansStatisticalLumpsAreComparedDistributionally(BspLump lump) =>
        Assert.Equal(DiffKind.Distributional, BspDiff.KindOf(lump));

    [Fact]
    public void EveryNamedLumpHasAKind()
    {
        // Total over the enum: KindOf answers for all 61 named slots, and the
        // fourth answer is a legible "not compared" rather than a gap.
        foreach (BspLump lump in Enum.GetValues<BspLump>())
        {
            DiffKind kind = BspDiff.KindOf(lump);
            Assert.True(Enum.IsDefined(kind), $"{lump} has no kind");
        }
    }

    [Fact]
    public void TheCollisionLumpsAreNotComparedAndTheTableSaysSoDeliberately()
    {
        // Named rather than left to fall through the default, so that a later
        // phase adding a VPHY reader has somewhere obvious to change.
        Assert.Equal(DiffKind.NotCompared, BspDiff.KindOf(BspLump.PhysCollide));
        Assert.Equal(DiffKind.NotCompared, BspDiff.KindOf(BspLump.PhysDisp));
    }
}
