using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The stock gate's second half: the swapped-texinfo decision, and one fact
/// per branch proving the catalogue actually reaches it — so that an
/// element-for-element match is not a match of defaults against defaults
/// (memory: the-check-that-cannot-fail).
/// </summary>
public sealed class DispStockCoverageTests
{
    /// <summary>The catalogue entries, for xUnit's theory data.</summary>
    public static TheoryData<string> Entries => DispStockCatalogue.Entries;

    /// <summary>
    /// STOCK QUIRK, pinned: the swap is decided (<c>CalcLuxelCoords</c>
    /// returns true, <c>builddisp.cpp:489-500</c>) but never reaches the BSP.
    /// <c>DispMapToCoreDispInfo</c> repoints <c>mapdispinfo_t::face.texinfo</c>
    /// (<c>disp_vbsp.cpp:230</c>), the MAP face — the <c>dface_t</c> was
    /// already emitted by <c>WriteBSP</c> (<c>writebsp.cpp:933</c>) from the
    /// same struct before <c>EndBSPFile</c> ran this, so it keeps the original
    /// texinfo, and <c>CompactTexinfos</c> (<c>writebsp.cpp:771</c>) then drops
    /// the unreferenced copy. The d2_prison_08 fix its comment describes is
    /// dead in every BSP. Measured on <c>p3f_swap</c>: our decision is swap,
    /// stock's face lightmap U is the side's <c>uaxis</c>.
    /// </summary>
    [DispStockFact]
    public void TheSwappedTexInfoNeverReachesTheFaceLump()
    {
        DispStockMap map = DispStockMap.Load("p3f_swap");
        IReadOnlyList<DisplacementResult> results = DispStockLumpTests.BuildResults(map, ComplianceOptions.Stock);
        TexInfo t = map.StockFaceTexInfos[0];
        Vec3 lmU = new(t.LightmapVecsLuxelsPerWorldUnits[0], t.LightmapVecsLuxelsPerWorldUnits[1], t.LightmapVecsLuxelsPerWorldUnits[2]);

        Assert.True(results[0].NeedsSwappedTexInfo);
        Assert.Equal(1.0f, Vec3.Dot(lmU.Normalise().Normalised, map.SideUAxes[0].Normalise().Normalised), 1e-6f);
    }

    /// <summary>
    /// ...and the swapped copy is not in LUMP_TEXINFO either: no entry is
    /// <see cref="DisplacementLumpBuilder.SwapLightmapAxes"/> of the face's.
    /// </summary>
    [DispStockFact]
    public void TheSwappedCopyIsCompactedAway()
    {
        DispStockMap map = DispStockMap.Load("p3f_swap");
        TexInfo swapped = DisplacementLumpBuilder.SwapLightmapAxes(map.StockFaceTexInfos[0]);

        Assert.DoesNotContain(map.StockTexInfos, t =>
            Enumerable.Range(0, 8).All(k => DispFixtures.BitEqual(
                t.LightmapVecsLuxelsPerWorldUnits[k], swapped.LightmapVecsLuxelsPerWorldUnits[k])));
    }

    /// <summary>Some stock edge neighbour spans corner to midpoint.</summary>
    [DispStockFact]
    public void SomeCatalogueMapHasAHalfSpan() =>
        Assert.Contains(SubNeighbours(), s => s.Span == (byte)NeighborSpan.CornerToMidpoint);

    /// <summary>Some stock edge neighbour's own span is midpoint to corner.</summary>
    [DispStockFact]
    public void SomeCatalogueMapHasAMidpointNeighbourSpan() =>
        Assert.Contains(SubNeighbours(), s => s.NeighborSpan == (byte)NeighborSpan.MidpointToCorner);

    /// <summary>Some stock neighbour is rotated, orientation other than <c>CCW_0</c>.</summary>
    [DispStockFact]
    public void SomeCatalogueMapHasARotatedNeighbour() =>
        Assert.Contains(SubNeighbours(), s => s.NeighborOrientation != (byte)NeighborOrientation.Ccw0);

    /// <summary>Some stock displacement has a corner neighbour.</summary>
    [DispStockFact]
    public void SomeCatalogueMapHasACornerNeighbour() =>
        Assert.Contains(
            Maps().SelectMany(m => m.StockInfos),
            i => Enumerable.Range(0, 4).Any(c => i.CornerNeighbors[c].NumNeighbors > 0));

    /// <summary>
    /// Some stock vertex was snapped (<c>SnapRemainingVertsToSurface</c>): its
    /// distance is exactly one and its vector is not unit.
    /// </summary>
    [DispStockFact]
    public void SomeCatalogueMapHasASnappedVertex() =>
        Assert.Contains(
            Maps().SelectMany(m => m.StockVerts),
            v => v.Dist == 1.0f && MathF.Abs(v.Vector.Length() - 1.0f) > 1e-3f);

    /// <summary>
    /// Some stock displacement's lightmap is clamped at
    /// <c>MAX_DISP_LIGHTMAP_DIM_WITHOUT_BORDER</c> (<c>builddisp.cpp:476</c>).
    /// </summary>
    [DispStockFact]
    public void SomeCatalogueMapClampsALightmapAt125() =>
        Assert.Contains(Maps().SelectMany(m => m.StockLightmapSizes), s => s.U == 125 || s.V == 125);

    /// <summary>Every power is present.</summary>
    [DispStockFact]
    public void EveryPowerIsPresent() =>
        Assert.Equal([2, 3, 4], Maps().SelectMany(m => m.StockInfos).Select(i => i.Power).Distinct().Order());

    /// <summary>The collapsed tags reach all four walkable/buildable combinations (<c>map.cpp:1186-1210</c>).</summary>
    [DispStockFact]
    public void SomeCatalogueMapHasEveryTagCombination() =>
        Assert.Equal(
            [0, (int)DispTriTags.Walkable, (int)DispTriTags.Buildable, (int)(DispTriTags.Walkable | DispTriTags.Buildable)],
            Maps().SelectMany(m => m.StockTris).Select(t => (int)t.Tags).Distinct().Order());

    /// <summary>Some stock displacement has a non-zero alpha.</summary>
    [DispStockFact]
    public void SomeCatalogueMapHasAlphas() =>
        Assert.Contains(Maps().SelectMany(m => m.StockVerts), v => v.Alpha > 0);

    /// <summary>Some stock displacement is not on a horizontal face.</summary>
    [DispStockFact]
    public void SomeCatalogueMapHasANonHorizontalDisplacement() =>
        Assert.Contains(Maps().SelectMany(m => m.Faces), f =>
            Vec3.Cross(f.Winding[3] - f.Winding[0], f.Winding[1] - f.Winding[0]).Normalise().Normalised.Z is > -0.99f and < 0.99f);

    private static IEnumerable<DispStockMap> Maps() =>
        DispStockCatalogue.EntryNames.Select(DispStockMap.Load);

    private static IEnumerable<DispSubNeighbor> SubNeighbours() =>
        Maps().SelectMany(m => m.StockInfos).SelectMany(i =>
            Enumerable.Range(0, 8).Select(k => i.EdgeNeighbors[k / 2].SubNeighbors[k % 2]))
            .Where(s => s.IsValid());
}
