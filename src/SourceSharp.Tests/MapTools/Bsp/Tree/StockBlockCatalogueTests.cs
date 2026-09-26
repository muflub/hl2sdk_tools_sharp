//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// <b>The I2 gate for this lane.</b> Every block of every catalogue map, plus
/// the corpus map, carved and BSP'd by this port and compared against what
/// stock's own <c>-v</c> log says it decided.
/// </summary>
/// <remarks>
/// <para>
/// Eight numbers per block: <c>ChopBrushes</c>'s input and output brush counts,
/// and <c>BrushBSP</c>'s brushes, visible faces, nonvisible faces, visible
/// nodes, nonvis nodes and leaves. Exact, not thresholded — none of these is a
/// float and none of them has any freedom.
/// </para>
/// <para>
/// The recipe that produces the reference is in
/// <see cref="SourceSharp.Tests.MapTools.Bsp.StockLoad"/>; the one extra thing
/// these facts need beyond the other stock gates is <c>-v</c> on the vbsp run,
/// because <c>ChopBrushes</c> and <c>BrushBSP</c> report through
/// <c>qprintf</c>.
/// </para>
/// </remarks>
public class StockBlockCatalogueTests
{
    /// <summary>
    /// Every block of every map: the chop counts and the tree counts, exactly.
    /// </summary>
    /// <param name="name">The catalogue entry.</param>
    /// <returns>The running fact.</returns>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task EveryBlockAgreesWithStocksFirstWorldPass(string name)
    {
        IReadOnlyList<BlockBuildStatistics> stock = StockBlockLog.FirstWorldPass(name);
        Assert.NotEmpty(stock);

        IReadOnlyList<BlockBuildStatistics> managed = await BuildAsync(name);

        Assert.Equal(stock.Count, managed.Count);

        for (int i = 0; i < stock.Count; i++)
        {
            Assert.Equal(stock[i], managed[i]);
        }
    }

    /// <summary>
    /// The same maps, one assertion: the grid this port compiles is the grid
    /// stock compiled, block coordinate for block coordinate.
    /// </summary>
    /// <param name="name">The catalogue entry.</param>
    /// <returns>The running fact.</returns>
    [StockLoadTheory]
    [MemberData(nameof(Entries))]
    public async Task TheBlockGridIsTheGridStockClampedTo(string name)
    {
        IReadOnlyList<BlockBuildStatistics> stock = StockBlockLog.FirstWorldPass(name);
        Assert.NotEmpty(stock);

        IReadOnlyList<BlockBuildStatistics> managed = await BuildAsync(name);

        Assert.Equal(
            stock.Select(b => (b.BlockX, b.BlockY)).ToArray(),
            managed.Select(b => (b.BlockX, b.BlockY)).ToArray());
    }

    /// <summary>
    /// <b>The check that cannot pass for the wrong reason.</b> A parser that
    /// silently returned nothing, or a build that silently compiled no blocks,
    /// would make every comparison above vacuously true. This fact fails unless
    /// the whole corpus is actually being compared, and it names the totals so
    /// that a shrinking reference is visible rather than quiet.
    /// </summary>
    [StockLoadFact]
    public async Task TheGateActuallyCoversTheWholeCatalogue()
    {
        int maps = 0;
        int blocks = 0;
        int treesCompared = 0;

        foreach (string name in StockLoad.EntryNames)
        {
            IReadOnlyList<BlockBuildStatistics> stock = StockBlockLog.FirstWorldPass(name);
            if (stock.Count == 0)
            {
                Assert.Fail($"{name}: no block records parsed out of its -v log");
            }

            maps++;
            blocks += stock.Count;
            treesCompared += stock.Count(b => b.Tree is not null);

            IReadOnlyList<BlockBuildStatistics> managed = await BuildAsync(name);
            Assert.Equal(stock.Count, managed.Count);
        }

        Assert.True(maps >= 30, $"only {maps} maps in the reference");
        Assert.True(blocks >= 234, $"only {blocks} blocks compared");
        Assert.True(treesCompared >= 232, $"only {treesCompared} trees compared");
    }

    /// <summary>
    /// <b>The waiver that no longer exists, kept as a fact so it cannot come
    /// back unnoticed.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seam octahedron used to be excluded by name from
    /// <see cref="EveryBlockAgreesWithStocksFirstWorldPass"/>: its vertices sit
    /// on the block boundary planes to within <c>BRUSH_CLIP_EPSILON</c>, so the
    /// block clip turns four of its eight faces into zero-area slivers, and how
    /// many survive was decided by the last bits of a coordinate. Two of its
    /// four blocks disagreed with stock on the visible-face count.
    /// </para>
    /// <para>
    /// Wiring <see cref="StockQuirk.BaseWindingNormalise"/> and
    /// <see cref="StockQuirk.EdgeBevelNormalise"/> and pinning this gate at
    /// <see cref="ComplianceOptions.Stock"/> closed both: the coordinates whose
    /// last bits were deciding it now come out of the same <c>rsqrtss</c>
    /// sequence stock used. This fact says the disagreement count is ZERO, so a
    /// change that reopens it is red rather than a quietly widened waiver.
    /// </para>
    /// </remarks>
    [StockLoadFact]
    public async Task TheSeamOctahedronNowAgreesWithStockOnEveryBlock()
    {
        string name = SourceSharp.Tests.MapTools.Bsp.Csg.EdgeBevelShapes.OctahedronOnTheSeam;

        IReadOnlyList<BlockBuildStatistics> stock = StockBlockLog.FirstWorldPass(name);
        Assert.NotEmpty(stock);

        IReadOnlyList<BlockBuildStatistics> managed = await BuildAsync(name);
        Assert.Equal(stock.Count, managed.Count);

        List<string> differences = [];

        for (int i = 0; i < stock.Count; i++)
        {
            Assert.Equal((stock[i].BlockX, stock[i].BlockY), (managed[i].BlockX, managed[i].BlockY));
            Assert.Equal(stock[i].Chop, managed[i].Chop);

            BspTreeStatistics s = stock[i].Tree!.Value;
            BspTreeStatistics m = managed[i].Tree!.Value;

            if (s != m)
            {
                differences.Add($"block {stock[i].BlockX},{stock[i].BlockY}: {s} vs {m}");
            }
        }

        Assert.Empty(differences);
    }

    /// <summary>
    /// The sliver-face disagreement was REAL, and the compliance switch is what
    /// closes it -- not a corpus that stopped exercising the shape.
    /// </summary>
    /// <remarks>
    /// Under <see cref="ComplianceOptions.Correct"/> this map's blocks differ
    /// from stock's again. Without this fact the one above would also pass on a
    /// corpus whose seam octahedron had become trivial, and the pin would be
    /// decoration.
    /// </remarks>
    [StockLoadFact]
    public async Task TheSeamOctahedronDisagreesWithStockUnderCorrect()
    {
        string name = SourceSharp.Tests.MapTools.Bsp.Csg.EdgeBevelShapes.OctahedronOnTheSeam;

        IReadOnlyList<BlockBuildStatistics> stock = StockBlockLog.FirstWorldPass(name);
        IReadOnlyList<BlockBuildStatistics> correct =
            await BuildAsync(name, ComplianceOptions.Correct);

        Assert.Equal(stock.Count, correct.Count);

        int differing = 0;
        for (int i = 0; i < stock.Count; i++)
        {
            if (stock[i].Tree!.Value != correct[i].Tree!.Value)
            {
                differing++;
            }
        }

        Assert.NotEqual(0, differing);
    }

    /// <summary>The catalogue entries with a verbose log.</summary>
    public static TheoryData<string> Entries => StockLoad.Entries;

    /// <summary>
    /// Loads a map and runs the first world pass exactly as
    /// <c>ProcessWorldModel</c> does before it portalizes.
    /// </summary>
    private static async Task<IReadOnlyList<BlockBuildStatistics>> BuildAsync(
        string name,
        ComplianceOptions? compliance = null)
    {
        (VbspContext context, MapFile map, _) =
            await StockLoad.LoadAsync(name, compliance ?? ComplianceOptions.Stock);

        MapEntity world = map.Entities[0];

        BspBuildContext build = new(context, map)
        {
            BrushStart = world.FirstBrush,
            BrushEnd = world.FirstBrush + world.BrushCount,
        };

        BspBlockGrid grid = BlockGrid.Clamp(BspBlockGrid.Full, map.Mins, map.Maxs);

        BlockGrid.BuildWorldPass(
            build, grid, map.Mins, map.Maxs, out IReadOnlyList<BlockBuildStatistics> blocks);

        return blocks;
    }
}
