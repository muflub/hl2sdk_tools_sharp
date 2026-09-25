using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// <c>ComputeAmbientForLeaf</c>'s sizing (<c>leaf_ambient_lighting.cpp:533-546</c>),
/// <c>Fixed8Fraction</c> (<c>:514</c>) and <c>AABBDistance</c> (<c>:450</c>).
/// </summary>
public sealed class LeafAmbientBuilderTests
{
    private static DLeaf Leaf(int dx, int dy, int dz)
    {
        DLeaf leaf = default;
        leaf.Maxs[0] = (short)dx;
        leaf.Maxs[1] = (short)dy;
        leaf.Maxs[2] = (short)dz;
        return leaf;
    }

    private static readonly LeafAmbientOptions Stock = LeafAmbientOptions.StockParity;

    private static readonly LeafAmbientOptions Correct = new() { Compliance = ComplianceOptions.Correct };

    [Fact]
    public void StockCubesTheXSizeWhateverTheLeafsShape()
    {
        // :536-538: ySize = max(xSize,1); zSize = max(xSize,1). A 96 x 32 x 64
        // leaf has xSize 3, so stock draws 3*3*3 = 27.
        DLeaf leaf = Leaf(96, 32, 64);

        Assert.Equal(27, LeafAmbientBuilder.CandidateSampleCount(in leaf, Stock));
    }

    [Fact]
    public void CorrectUsesEachAxisOwnSize()
    {
        // What the code evidently intended: 96/32 * 32/32 * 64/64 = 3.
        DLeaf leaf = Leaf(96, 32, 64);

        Assert.Equal(3, LeafAmbientBuilder.CandidateSampleCount(in leaf, Correct));
    }

    [Fact]
    public void TheDivisionsTruncate()
    {
        // Integer division of integer extents: 63 units is one 32-unit step.
        DLeaf leaf = Leaf(63, 63, 127);

        Assert.Equal(1, LeafAmbientBuilder.CandidateSampleCount(in leaf, Correct));
    }

    [Fact]
    public void AnyLeafDrawsAtLeastOne()
    {
        DLeaf leaf = Leaf(0, 0, 0);

        Assert.Equal(1, LeafAmbientBuilder.CandidateSampleCount(in leaf, Stock));
    }

    [Fact]
    public void TheCountIsClampedAt128()
    {
        // :546, clamp( volumeCount, 1, 128 ).
        DLeaf leaf = Leaf(1024, 1024, 1024);

        Assert.Equal(128, LeafAmbientBuilder.CandidateSampleCount(in leaf, Stock));
    }

    [Fact]
    public void FastAmbientDrawsOne()
    {
        // :541-545, g_bFastAmbient.
        DLeaf leaf = Leaf(512, 512, 512);

        Assert.Equal(1, LeafAmbientBuilder.CandidateSampleCount(in leaf, Stock with { FastAmbient = true }));
    }

    [Fact]
    public void Fixed8FractionRoundsHalfUp()
    {
        // byte(frac + 0.5f): 127.5 of 255 becomes 128.
        Assert.Equal(128, LeafAmbientBuilder.Fixed8Fraction(127.5f, 0f, 255f));
    }

    [Fact]
    public void Fixed8FractionSaturatesOutsideTheRange()
    {
        // RemapValClamped clamps before the rounding.
        Assert.Equal((0, 255), (LeafAmbientBuilder.Fixed8Fraction(-10f, 0f, 100f), LeafAmbientBuilder.Fixed8Fraction(110f, 0f, 100f)));
    }

    [Fact]
    public void Fixed8FractionOfAnEmptyRangeIsZero()
    {
        // :516, tMax <= tMin.
        Assert.Equal(0, LeafAmbientBuilder.Fixed8Fraction(5f, 5f, 5f));
    }

    [Fact]
    public void OverlappingBoxesAreZeroApart()
    {
        float d = LeafAmbientBuilder.AabbDistance(new Vec3(0, 0, 0), new Vec3(10, 10, 10), new Vec3(5, 5, 5), new Vec3(20, 20, 20));

        Assert.Equal(0f, d);
    }

    [Fact]
    public void SeparatedBoxesAreTheirGapApart()
    {
        // A 3-4-0 gap: the per-axis gaps are negative, the length is 5.
        float d = LeafAmbientBuilder.AabbDistance(new Vec3(0, 0, 0), new Vec3(1, 1, 1), new Vec3(4, 5, 0), new Vec3(6, 6, 1));

        Assert.Equal(5f, d);
    }
}
