using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// Each displacement quirk reaches its site: <see cref="ComplianceOptions.Stock"/>
/// against <c>Stock.Flipping(quirk)</c>, so exactly one quirk moves.
/// </summary>
public sealed class DispComplianceEffectTests
{
    private static readonly Vec3[] Floor = DispFixtures.UnitFloor();

    /// <summary>A 256 x 64 rectangle whose lightmap U runs along its short side: the swap case.</summary>
    private static readonly Vec3[] Strip = DispFixtures.FloorQuad(Vec3.Zero, 256, 64);

    /// <summary>
    /// Stock: the swapped face keeps its texinfo and the table is untouched
    ///(repoints only the map face).
    /// </summary>
    [Fact]
    public void UnderStockTheSwappedFaceKeepsItsTexInfo()
    {
        (int[] assigned, List<TexInfo> table) = SwapCase(ComplianceOptions.Stock);

        Assert.Equal([0], assigned);
        Assert.Single(table);
    }

    /// <summary>Flipped: the face carries a new, swapped copy.</summary>
    [Fact]
    public void CorrectingTheSwapRepointsTheFaceAtASwappedCopy()
    {
        (int[] assigned, List<TexInfo> table) =
            SwapCase(ComplianceOptions.Stock.Flipping(StockQuirk.DispLightmapSwapDropped));

        Assert.Equal([1], assigned);
        Assert.Equal(2, table.Count);
        Assert.Equal(DispFixtures.LuxelsPerUnit, table[1].LightmapVecsLuxelsPerWorldUnits[0]);
    }

    /// <summary>
    /// A face whose axes already agree is not swapped under correct either.
    /// </summary>
    [Fact]
    public void CorrectLeavesAnUnswappedFaceAlone()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build([(DispFixtures.Heightfield(2, Floor[0]), Floor)]);
        List<TexInfo> table = [default];

        int[] assigned = DispVbspHooks.FaceTexInfos(r, [0], table, ComplianceOptions.Correct);

        Assert.Equal([0], assigned);
        Assert.Single(table);
    }

    /// <summary>Stock world bounds stop at the base quad.</summary>
    [Fact]
    public void UnderStockWorldBoundsStopAtTheBaseQuad()
    {
        Assert.Equal(0.1f, RaisedBounds(ComplianceOptions.Stock).Max.Z);
    }

    /// <summary>Flipped, they reach the raised surface.</summary>
    [Fact]
    public void CorrectingTheBoundsReachesTheRaisedSurface()
    {
        Assert.Equal(300.0f, RaisedBounds(ComplianceOptions.Stock.Flipping(StockQuirk.DispWorldBoundsBaseQuad)).Max.Z);
    }

    /// <summary>Stock crease normals built by the lump builder are short.</summary>
    [Fact]
    public void UnderStockTheBuildersCreaseNormalIsShort()
    {
        Assert.True(CreaseNormalLength(ComplianceOptions.Stock) < 0.99f);
    }

    /// <summary>Flipped, the builder's crease normal is unit length.</summary>
    [Fact]
    public void CorrectingTheMeanGivesTheBuilderAUnitCreaseNormal()
    {
        Assert.Equal(
            1.0f,
            CreaseNormalLength(ComplianceOptions.Stock.Flipping(StockQuirk.DispVertexNormalMeanUnnormalised)),
            1e-6f);
    }

    /// <summary>The normal quirk moves nothing vbsp writes: DISP_VERTS is identical either way.</summary>
    [Fact]
    public void TheNormalQuirkLeavesTheVertexLumpAlone()
    {
        MapDisplacement disp = DispFixtures.Heightfield(2, Floor[0], (x, _) => MathF.Abs(x - 2) * 64);
        (_, DisplacementLumps a) = DispFixtures.Build([(disp, Floor)], ComplianceOptions.Stock);
        (_, DisplacementLumps b) = DispFixtures.Build(
            [(disp, Floor)], ComplianceOptions.Stock.Flipping(StockQuirk.DispVertexNormalMeanUnnormalised));

        Assert.Equal(a.Verts, b.Verts);
    }

    private static (int[] Assigned, List<TexInfo> Table) SwapCase(ComplianceOptions compliance)
    {
        DisplacementLumps lumps = new();
        IReadOnlyList<DisplacementResult> r = DisplacementLumpBuilder.Build(
            [DispFixtures.Heightfield(2, Strip[0])],
            [DispFixtures.Face(Strip, lightmapU: new Vec3(0, DispFixtures.LuxelsPerUnit, 0),
                lightmapV: new Vec3(DispFixtures.LuxelsPerUnit, 0, 0))],
            new VbspOptions { Compliance = compliance },
            lumps);
        Assert.True(r[0].NeedsSwappedTexInfo);

        TexInfo t = default;
        t.LightmapVecsLuxelsPerWorldUnits[1] = DispFixtures.LuxelsPerUnit;
        t.LightmapVecsLuxelsPerWorldUnits[4] = DispFixtures.LuxelsPerUnit;
        List<TexInfo> table = [t];

        return (DispVbspHooks.FaceTexInfos(r, [0], table, compliance), table);
    }

    private static DispBox RaisedBounds(ComplianceOptions compliance) =>
        DisplacementLumpBuilder.ComputeDispInfoBounds(
            DispFixtures.Heightfield(2, Floor[0], (_, _) => 300), DispFixtures.Face(Floor), compliance);

    private static float CreaseNormalLength(ComplianceOptions compliance)
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, Floor[0], (x, _) => MathF.Abs(x - 2) * 64), Floor)], compliance);
        return r[0].Core.Normal(DispFixtures.Index(r[0].Core, 2, 2)).Length();
    }
}
