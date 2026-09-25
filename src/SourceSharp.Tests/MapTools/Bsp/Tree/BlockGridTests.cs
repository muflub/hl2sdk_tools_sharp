using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// The 1024-unit block grid: clamping, per-block compilation and the stitched
/// tree.
/// </summary>
public class BlockGridTests
{
    [Fact]
    public void TheFullGridIsMinusSixteenToFifteenOnBothAxes()
    {
        Assert.Equal(-16, BlockGrid.BlockMin);
        Assert.Equal(15, BlockGrid.BlockMax);
        Assert.Equal(32, BlockGrid.BlockSpace);
        Assert.Equal(1024, BlockGrid.BlockSize);
    }

    [Fact]
    public void ABlockSpansTheWholeLegalHeight()
    {
        BlockGrid.BlockBounds(0, 0, out Vec3 mins, out Vec3 maxs);

        Assert.Equal(new Vec3(0, 0, GeometryEpsilons.MinCoordInteger), mins);
        Assert.Equal(new Vec3(1024, 1024, GeometryEpsilons.MaxCoordInteger), maxs);
    }

    /// <summary>
    /// A 64-unit box at the origin occupies blocks -1 and 0 on both axes, and
    /// the clamp narrows the grid to exactly those four. This is the grid the
    /// stock log for <c>l0_unit_cube</c> reports, block by block.
    /// </summary>
    [Fact]
    public void ABoxAtTheOriginNarrowsTheGridToFourBlocks()
    {
        BspBlockGrid grid = BlockGrid.Clamp(
            BspBlockGrid.Full, new Vec3(-32, -32, -32), new Vec3(32, 32, 32));

        Assert.Equal(new BspBlockGrid(-1, -1, 0, 0), grid);
    }

    /// <summary>
    /// <b>The low edge test is on the block's FAR boundary, not its near
    /// one.</b> <c>(xl+1) * 1024 &lt; mapMins.X</c> asks whether block
    /// <c>xl</c> ends below the map — so a map starting at x = 100 leaves
    /// <c>block_xl</c> at -16, because block -16 ends at -15360 and that is not
    /// below 100... which it is. The case that actually holds the low edge
    /// still is a map whose minimum is inside block -16 itself.
    /// </summary>
    [Fact]
    public void TheLowEdgeMovesToTheBlockTheMapStartsIn()
    {
        BspBlockGrid grid = BlockGrid.Clamp(
            BspBlockGrid.Full, new Vec3(3000, 3000, 0), new Vec3(4000, 4000, 100));

        // 3000/1024 = 2.93 -> block 2; 4000/1024 = 3.9 -> block 3.
        Assert.Equal(new BspBlockGrid(2, 2, 3, 3), grid);
    }

    /// <summary>
    /// Both narrowings use C's <c>floor</c> on a signed value and not
    /// truncation, so a map that reaches to -1 lands in block -1 rather than
    /// block 0.
    /// </summary>
    [Fact]
    public void TheEdgesFloorRatherThanTruncateOnTheNegativeSide()
    {
        BspBlockGrid grid = BlockGrid.Clamp(
            BspBlockGrid.Full, new Vec3(-3000, -3000, 0), new Vec3(-1, -1, 100));

        // -1/1024 = -0.00098 -> floor -1; -3000/1024 = -2.93 -> floor -3.
        Assert.Equal(new BspBlockGrid(-3, -3, -1, -1), grid);
    }

    [Fact]
    public void TheGridIsNeverWiderThanTheLegalWorld()
    {
        BspBlockGrid grid = BlockGrid.Clamp(
            BspBlockGrid.Full, new Vec3(-99999, -99999, 0), new Vec3(99999, 99999, 100));

        Assert.Equal(BspBlockGrid.Full, grid);
    }

    /// <summary>
    /// A block with no brushes in it becomes a SOLID leaf, which is what keeps
    /// the flood fill from escaping through unbuilt parts of the world.
    /// </summary>
    [Fact]
    public async Task AnEmptyBlockBecomesASolidLeaf()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        build.BrushStart = 0;
        build.BrushEnd = map.BrushCount;

        BspNode node = BlockGrid.ProcessBlock(build, 8, 8, out BlockBuildStatistics statistics);

        Assert.True(node.IsLeaf);
        Assert.Equal((int)SourceSharp.MapTools.Materials.BrushContents.Solid, node.Contents);
        Assert.Null(statistics.Chop);
        Assert.Null(statistics.Tree);
    }

    /// <summary>
    /// <b>The stock numbers for <c>l0_unit_cube</c>, reproduced.</b> Its
    /// <c>-v</c> log reports, for each of the four blocks: 1 original brush,
    /// 1 output brush, 1 brush, 4 visible faces, 0 nonvisible faces, 4 visible
    /// nodes, 0 nonvis nodes, 5 leafs. Two of the cube's six faces lie on block
    /// boundaries and are marked <c>TEXINFO_NODE</c> by
    /// <see cref="BrushCsg.ClipBrushToBox"/>, which is why four planes and not
    /// six build the tree.
    /// </summary>
    [Fact]
    public async Task EachBlockOfAUnitCubeMatchesStocksFourNodesAndFiveLeaves()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        build.BrushStart = 0;
        build.BrushEnd = map.BrushCount;

        BspBlockGrid grid = BlockGrid.Clamp(BspBlockGrid.Full, map.Mins, map.Maxs);
        BlockGrid.BuildWorldPass(
            build, grid, map.Mins, map.Maxs, out IReadOnlyList<BlockBuildStatistics> blocks);

        Assert.Equal(4, blocks.Count);

        foreach (BlockBuildStatistics block in blocks)
        {
            Assert.Equal(new ChopStatistics(1, 1), block.Chop);
            Assert.Equal(new BspTreeStatistics(1, 4, 0, 4, 0, 5), block.Tree);
        }
    }

    /// <summary>
    /// The blocks are visited Y-outer, X-inner, which is the order stock's
    /// <c>############### block  x, y ###############</c> lines come out in.
    /// </summary>
    [Fact]
    public async Task BlocksAreVisitedWithYOutsideAndXInside()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        build.BrushStart = 0;
        build.BrushEnd = map.BrushCount;

        BspBlockGrid grid = BlockGrid.Clamp(BspBlockGrid.Full, map.Mins, map.Maxs);
        BlockGrid.BuildWorldPass(
            build, grid, map.Mins, map.Maxs, out IReadOnlyList<BlockBuildStatistics> blocks);

        Assert.Equal(
            [(-1, -1), (0, -1), (-1, 0), (0, 0)],
            blocks.Select(b => (b.BlockX, b.BlockY)).ToArray());
    }

    /// <summary>
    /// The stitching tree is built over a range one block wider on every side,
    /// so the outer boundary of every real block gets a node. For a 2x2 grid
    /// that means a 4x4 range, and every leaf of the stitch that is not one of
    /// the four block trees is an EMPTY leaf — contents 0, not solid.
    /// </summary>
    [Fact]
    public async Task TheStitchedTreeWrapsTheBlocksInEmptyLeaves()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        build.BrushStart = 0;
        build.BrushEnd = map.BrushCount;

        BspBlockGrid grid = BlockGrid.Clamp(BspBlockGrid.Full, map.Mins, map.Maxs);
        BspTree tree = BlockGrid.BuildWorldPass(build, grid, map.Mins, map.Maxs, out _);

        // A point far outside the compiled blocks is in the empty ring.
        BspNode outside = BrushBspTree.PointInLeaf(
            build, tree.HeadNode!, new Vec3(3000, 3000, 0));

        Assert.True(outside.IsLeaf);
        Assert.Equal(0, outside.Contents);
    }

    [Fact]
    public async Task TheStitchedTreesBoundsComeFromTheGridAndTheMapsHeight()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        build.BrushStart = 0;
        build.BrushEnd = map.BrushCount;

        BspBlockGrid grid = BlockGrid.Clamp(BspBlockGrid.Full, map.Mins, map.Maxs);
        BspTree tree = BlockGrid.BuildWorldPass(build, grid, map.Mins, map.Maxs, out _);

        Assert.Equal(new Vec3(-1024, -1024, -40), tree.Mins);
        Assert.Equal(new Vec3(1024, 1024, 40), tree.Maxs);
    }

    /// <summary>
    /// A single block needs no stitching plane at all beyond the ring, and a
    /// point inside the box still reaches a solid leaf through the stitch.
    /// </summary>
    [Fact]
    public async Task APointInsideTheBoxReachesASolidLeafThroughTheStitchedTree()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        build.BrushStart = 0;
        build.BrushEnd = map.BrushCount;

        BspBlockGrid grid = BlockGrid.Clamp(BspBlockGrid.Full, map.Mins, map.Maxs);
        BspTree tree = BlockGrid.BuildWorldPass(build, grid, map.Mins, map.Maxs, out _);

        BspNode inside = BrushBspTree.PointInLeaf(build, tree.HeadNode!, new Vec3(-8, -8, -8));

        Assert.NotEqual(
            0, inside.Contents & (int)SourceSharp.MapTools.Materials.BrushContents.Solid);
    }

    /// <summary>
    /// <c>-nocsg</c> skips the chop and nothing else: the areaportal fixup and
    /// the tree build still run.
    /// </summary>
    [Fact]
    public async Task NoCsgSkipsTheChopAndStillBuildsATree()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))),
            new VbspOptions { NoCsg = true });

        build.BrushStart = 0;
        build.BrushEnd = map.BrushCount;

        BlockGrid.ProcessBlock(build, -1, -1, out BlockBuildStatistics statistics);

        Assert.Null(statistics.Chop);
        Assert.NotNull(statistics.Tree);
    }
}
