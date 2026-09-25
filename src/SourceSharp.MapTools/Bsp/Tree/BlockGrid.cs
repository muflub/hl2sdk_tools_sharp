using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// The 1024-unit block grid the world model is compiled on:
/// <c>BlockTree</c>, <c>ProcessBlock_Thread</c> and the block clamping at the
/// Top of <c>ProcessWorldModel</c>.
/// </summary>
/// <remarks>
/// <para>
/// The world is carved and BSP'd one 1024×1024 column at a time, full height,
/// and the per-block trees are then stitched together by a tree of axial
/// splitting planes on the block boundaries. Stock's reason is in its own
/// comment: "oversizing the blocks guarantees that all the boundaries will also
/// Get nodes".
/// </para>
/// <para>
/// <b>It is also why vbsp's only threaded call site exists</b>
/// (<c>RunThreadsOnIndividual</c> over the blocks) — and
/// why that call site runs on one thread in every shipped build, because
/// <c>numthreads</c> is forced to 1. Blocks are
/// genuinely independent apart from the plane table and the material tables
/// they all append to, which is exactly the serialisation Phase 3p has to
/// solve rather than the parallelism it has to invent.
/// </para>
/// </remarks>
public static class BlockGrid
{
    /// <summary>
    /// <c>BLOCKS_SIZE</c>: a block is 1024 units square.
    /// </summary>
    public const int BlockSize = 1024;

    /// <summary>
    /// <c>BLOCKS_SPACE</c>: how many blocks span the world.
    /// </summary>
    /// <remarks>
    /// <c>COORD_EXTENT / BLOCKS_SIZE</c> = 32768 / 1024 = 32.
    /// </remarks>
    public const int BlockSpace = 32;

    /// <summary><c>BLOCKS_MIN</c>: -16.</summary>
    public const int BlockMin = -(BlockSpace / 2);

    /// <summary><c>BLOCKS_MAX</c>: 15.</summary>
    public const int BlockMax = (BlockSpace / 2) - 1;

    /// <summary>
    /// Narrows the grid to the part of the world the map actually occupies:
    /// The clamping.
    /// </summary>
    /// <param name="requested">The grid the switches asked for.</param>
    /// <param name="mapMins">The map's bounds.</param>
    /// <param name="mapMaxs">The map's bounds.</param>
    /// <returns>The grid to compile.</returns>
    /// <remarks>
    /// <para>
    /// Four asymmetric tests, and the asymmetry is stock's. The high edges
    /// shrink to <c>floor(mapMax / 1024)</c> when the grid reaches past the
    /// map; the low edges to <c>floor(mapMin / 1024)</c> when the grid's
    /// SECOND boundary — <c>(xl+1) * 1024</c>, not <c>xl * 1024</c> — is still
    /// below the map. So a map starting at x = 100 gets <c>block_xl</c> left at
    /// -16 rather than moved to 0, because the test asks whether block -16's
    /// far edge is below 100, and it is not.
    /// </para>
    /// <para>
    /// Both floors are C's <c>floor()</c> on a double and not truncation, so a
    /// map minimum of -1 lands in block -1 and not block 0. Only after all four
    /// does the range get clamped back inside ±16.
    /// </para>
    /// <para>
    /// <b>Z is not part of the grid at all.</b> Blocks span
    /// <c>MIN_COORD_INTEGER</c> to <c>MAX_COORD_INTEGER</c> vertically, which
    /// is why <see cref="BrushCsg.ClipBrushToBox"/> only clips X and Y.
    /// </para>
    /// </remarks>
    public static BspBlockGrid Clamp(BspBlockGrid requested, Vec3 mapMins, Vec3 mapMaxs)
    {
        int xl = requested.MinX;
        int yl = requested.MinY;
        int xh = requested.MaxX;
        int yh = requested.MaxY;

        if (xh * BlockSize > mapMaxs[0])
        {
            xh = (int)MathF.Floor(mapMaxs[0] / BlockSize);
        }

        if ((xl + 1) * BlockSize < mapMins[0])
        {
            xl = (int)MathF.Floor(mapMins[0] / BlockSize);
        }

        if (yh * BlockSize > mapMaxs[1])
        {
            yh = (int)MathF.Floor(mapMaxs[1] / BlockSize);
        }

        if ((yl + 1) * BlockSize < mapMins[1])
        {
            yl = (int)MathF.Floor(mapMins[1] / BlockSize);
        }

        if (xl < BlockMin) { xl = BlockMin; }
        if (yl < BlockMin) { yl = BlockMin; }
        if (xh > BlockMax) { xh = BlockMax; }
        if (yh > BlockMax) { yh = BlockMax; }

        return new BspBlockGrid(xl, yl, xh, yh);
    }

    /// <summary>The world-space box one block covers.</summary>
    /// <param name="blockX">The block's X coordinate.</param>
    /// <param name="blockY">The block's Y coordinate.</param>
    /// <param name="mins">The box's minimum.</param>
    /// <param name="maxs">The box's maximum.</param>
    /// <remarks>
    /// Full height in Z, so a brush is never clipped
    /// vertically by the grid.
    /// </remarks>
    public static void BlockBounds(int blockX, int blockY, out Vec3 mins, out Vec3 maxs)
    {
        mins = new Vec3(
            blockX * BlockSize, blockY * BlockSize, GeometryEpsilons.MinCoordInteger);
        maxs = new Vec3(
            (blockX + 1) * BlockSize, (blockY + 1) * BlockSize, GeometryEpsilons.MaxCoordInteger);
    }

    /// <summary>
    /// Compiles one block: <c>ProcessBlock_Thread</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="blockX">The block's X coordinate.</param>
    /// <param name="blockY">The block's Y coordinate.</param>
    /// <param name="statistics">What the block produced.</param>
    /// <returns>The block's head node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// A block with no brushes in it becomes a leaf of
    /// <c>CONTENTS_SOLID</c> and not an empty one — which
    /// is what stops the flood fill from escaping through a part of the world
    /// nobody built anything in. The DIFFERENT empty leaf that
    /// <see cref="BuildBlockTree"/> makes for a block outside the compiled
    /// range has contents 0, with stock's <c>//CONTENTS_SOLID</c> beside it
    /// recording that it used to be solid too.
    /// </para>
    /// <para>
    /// The brush list is built with
    /// <see cref="DetailScreen.NoDetail"/>: detail brushes are not in the world
    /// tree at all and are grafted back in by <c>MergeDetailTree</c>, Phase 3d.
    /// </para>
    /// <para>
    /// <see cref="AreaportalWaterFixup"/> runs BEFORE
    /// <see cref="BrushCsg.ChopBrushes"/> and unconditionally —
    /// <c>-nocsg</c> skips the chop, not the fixup.
    /// </para>
    /// </remarks>
    public static BspNode ProcessBlock(
        BspBuildContext context,
        int blockX,
        int blockY,
        out BlockBuildStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(context);

        BlockBounds(blockX, blockY, out Vec3 mins, out Vec3 maxs);

        BspBrush? brushes = BrushCsg.MakeBspBrushList(
            context, context.BrushStart, context.BrushEnd, mins, maxs, DetailScreen.NoDetail);

        if (brushes is null)
        {
            BspNode empty = context.AllocNode();
            empty.PlaneNumber = BspNode.Leaf;
            empty.Contents = (int)BrushContents.Solid;
            statistics = new BlockBuildStatistics(blockX, blockY, null, null);
            return empty;
        }

        AreaportalWaterFixup.FixupAreaportalWaterBrushes(context, brushes);

        ChopStatistics? chop = null;
        if (!context.Options.NoCsg)
        {
            int input = BrushCsg.CountBrushList(brushes);
            brushes = BrushCsg.ChopBrushes(context, brushes);
            chop = new ChopStatistics(input, BrushCsg.CountBrushList(brushes));
        }

        BspTree tree = BrushBspTree.BrushBsp(context, brushes, mins, maxs);
        statistics = new BlockBuildStatistics(blockX, blockY, chop, tree.Statistics);

        return tree.HeadNode!;
    }

    /// <summary>
    /// Stitches the per-block head nodes into one tree: <c>BlockTree</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="blocks">
    /// The block head nodes, indexed <c>[x - <see cref="BlockMin"/> + 1,
    /// y - <see cref="BlockMin"/> + 1]</c> as stock's
    /// <c>BLOCKX_OFFSET</c> does.
    /// </param>
    /// <param name="xl">The low X block, one BELOW the compiled range.</param>
    /// <param name="yl">The low Y block, one BELOW the compiled range.</param>
    /// <param name="xh">The high X block, one ABOVE the compiled range.</param>
    /// <param name="yh">The high Y block, one ABOVE the compiled range.</param>
    /// <returns>The head node of the stitched tree.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="blocks"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The caller passes a range one block wider than it compiled</b>, on
    /// all four sides (
    /// <c>BlockTree (block_xl-1, block_yl-1, block_xh+1, block_yh+1)</c>). The
    /// extra ring has no block nodes in it and becomes empty leaves, and its
    /// only job is to force a splitting plane onto the outer boundary of every
    /// real block. That is stock's "oversizing the blocks guarantees that all
    /// the boundaries will also get nodes", and it is why the grid array is
    /// <c>BLOCKS_SPACE+2</c> wide.
    /// </para>
    /// <para>
    /// The split axis is whichever range is LONGER, with ties going to Y
    /// (<c>xh - xl &gt; yh - yl</c> is strict), and the split point is
    /// <c>lo + (hi-lo)/2 + 1</c> — the <c>+1</c> putting the boundary above the
    /// midpoint, so the FRONT child gets the upper half and the back child the
    /// lower. Children are assigned in that order: <c>children[0]</c> is the
    /// half on the positive side of the plane, which is the same convention
    /// <c>BuildTree_r</c> uses.
    /// </para>
    /// <para>
    /// Each internal node here appends an axial plane at a multiple of 1024 to
    /// the plane table and has no <see cref="BspNode.Volume"/>, no
    /// <see cref="BspNode.Parent"/> and no <see cref="BspNode.Side"/> — the
    /// parent pointers in a stitched tree start only at each block's own head
    /// node, so a walk upwards from a leaf stops at the block boundary. Nothing
    /// in 3b walks upwards outside <c>SelectSplitSide</c>, which never sees
    /// these nodes.
    /// </para>
    /// </remarks>
    public static BspNode BuildBlockTree(
        BspBuildContext context,
        BspNode?[,] blocks,
        int xl,
        int yl,
        int xh,
        int yh)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(blocks);

        if (xl == xh && yl == yh)
        {
            BspNode? block = blocks[xl - BlockMin + 1, yl - BlockMin + 1];
            if (block is not null)
            {
                return block;
            }

            BspNode empty = context.AllocNode();
            empty.PlaneNumber = BspNode.Leaf;
            empty.Contents = 0;
            return empty;
        }

        BspNode node = context.AllocNode();

        if (xh - xl > yh - yl)
        {
            int mid = xl + ((xh - xl) / 2) + 1;
            node.PlaneNumber = context.Planes.Find(new Vec3(1f, 0f, 0f), mid * BlockSize);
            node.Children[0] = BuildBlockTree(context, blocks, mid, yl, xh, yh);
            node.Children[1] = BuildBlockTree(context, blocks, xl, yl, mid - 1, yh);
        }
        else
        {
            int mid = yl + ((yh - yl) / 2) + 1;
            node.PlaneNumber = context.Planes.Find(new Vec3(0f, 1f, 0f), mid * BlockSize);
            node.Children[0] = BuildBlockTree(context, blocks, xl, mid, xh, yh);
            node.Children[1] = BuildBlockTree(context, blocks, xl, yl, xh, mid - 1);
        }

        return node;
    }

    /// <summary>
    /// Compiles every block of a grid and stitches the result: the body of
    /// one <c>optimize</c> pass of <c>ProcessWorldModel</c>,
    /// </summary>
    /// <param name="context">The build context, whose brush range is read.</param>
    /// <param name="grid">The clamped grid.</param>
    /// <param name="mapMins">The map's bounds, for the tree's Z extent.</param>
    /// <param name="mapMaxs">The map's bounds, for the tree's Z extent.</param>
    /// <param name="statistics">One record per compiled block, in block order.</param>
    /// <returns>The stitched tree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Stock runs this TWICE.</b> <c>ProcessWorldModel</c>'s
    /// <c>for (optimize = 0 ; optimize &lt;= 1 ; optimize++)</c> builds the
    /// whole world, portalizes it, floods it and runs <c>MarkVisibleSides</c>,
    /// then throws the tree away and does it all again — because
    /// <c>MarkVisibleSides</c> has by then written <c>visible</c> onto the MAP
    /// brush sides, and the second pass's <see cref="BrushBspTree.SelectSplitSide"/>
    /// therefore splits only with planes that turned out to carry renderable
    /// geometry. Stock's own comment says exactly that.
    /// The loop breaks after one pass under <c>-noopt</c> or if the map leaked.
    /// </para>
    /// <para>
    /// This function is one pass. The loop around it needs
    /// <c>MakeTreePortals</c>, <c>FloodEntities</c>, <c>FillOutside</c>,
    /// <c>LeakFile</c> and <c>MarkVisibleSides</c>, all of which are Phase 3c's,
    /// so the driver lives there and this is what it calls.
    /// </para>
    /// <para>
    /// The tree's bounds are the GRID's, not the brushes' — the block range
    /// times 1024 in X and Y, and the map's own Z padded by 8
    /// — overwriting whatever
    /// <see cref="BrushBspTree.BrushBsp"/> accumulated for the last block.
    /// </para>
    /// </remarks>
    public static BspTree BuildWorldPass(
        BspBuildContext context,
        BspBlockGrid grid,
        Vec3 mapMins,
        Vec3 mapMaxs,
        out IReadOnlyList<BlockBuildStatistics> statistics)
    {
        ArgumentNullException.ThrowIfNull(context);

        BspNode?[,] blocks = new BspNode?[BlockSpace + 2, BlockSpace + 2];
        List<BlockBuildStatistics> records = [];

        int count = (grid.MaxX - grid.MinX + 1) * (grid.MaxY - grid.MinY + 1);

        for (int blockNumber = 0; blockNumber < count; blockNumber++)
        {
            int blockY = grid.MinY + (blockNumber / (grid.MaxX - grid.MinX + 1));
            int blockX = grid.MinX + (blockNumber % (grid.MaxX - grid.MinX + 1));

            BspNode head = ProcessBlock(context, blockX, blockY, out BlockBuildStatistics record);
            blocks[blockX - BlockMin + 1, blockY - BlockMin + 1] = head;
            records.Add(record);
        }

        BspTree tree = new()
        {
            HeadNode = BuildBlockTree(
                context, blocks, grid.MinX - 1, grid.MinY - 1, grid.MaxX + 1, grid.MaxY + 1),
            Mins = new Vec3(grid.MinX * BlockSize, grid.MinY * BlockSize, mapMins[2] - 8f),
            Maxs = new Vec3(
                (grid.MaxX + 1) * BlockSize, (grid.MaxY + 1) * BlockSize, mapMaxs[2] + 8f),
        };

        statistics = records;
        return tree;
    }
}
