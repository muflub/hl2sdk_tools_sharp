//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// One world pass with its blocks built on several threads at once, producing
/// the serial pass's tree: node for node, brush for brush, plane for plane,
/// with the same counters and the same diagnostics in the same order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The world is cut into 1024-unit blocks and each gets its own
/// tree. The serial pass builds them one after another, and the parallel
/// tree build (<see cref="BspTreeParallelism"/>) could only fork inside a
/// block, below its root, so each block's brush list, CSG chop and root
/// plane choice stayed serial: on 2fort, a 71-block world built twice, the
/// tree stage ran 1.25 s on one thread and still 1.08 s on four. Blocks share
/// nothing that decides their trees except the plane table and the map, so
/// they can be built side by side.
/// </para>
/// <para>
/// <b>What stays serial, and why that is enough.</b>
/// </para>
/// <list type="bullet">
/// <item><b>Planes.</b> A block appends planes in two places only: its four
/// bounding planes, found at the top of its brush list, and its head volume's
/// six (<see cref="BrushGeometry.BrushFromBounds"/>), found before its tree
/// recursion; splitting and chopping never append. Both depend only on the
/// block's coordinates. The bounding planes are found first, block by block
/// in block order; the head volume's x and y planes are the bounding planes
/// again, and its z planes are the same for every block, so only the first
/// block that has any brushes can append them, and it is listed here, on
/// this thread, to learn which block that is. After that the table is frozen
/// (<see cref="PlaneTable.Freeze"/>) and every block only looks planes up. A
/// plane appended anywhere else would fail the pass loudly.</item>
/// <item><b>Ids and counters.</b> Each block is built in a fork of the build
/// context (<see cref="BspBuildContext.ForkForBlock"/>), numbering its nodes
/// and brushes from zero, and forks are joined back in block order
/// (<see cref="BspBuildContext.JoinBlock"/>), each rebased onto the counts the
/// serial pass had when it began that block. A block is joined as soon as
/// it and every block before it are done, by whichever thread finished last,
/// so the joins overlap the blocks still being built and a joined block's
/// arena goes back to the pool at once.</item>
/// <item><b>Diagnostics</b> go to the fork and are appended at its join, so
/// they come out in block order.</item>
/// <item><b>The map.</b> Building a block reads map brushes and sides and
/// writes none of them, with one exception:
/// <see cref="AreaportalWaterFixup"/> widens an areaportal brush's contents
/// and texinfos when a water brush crosses it, and a later block reads what
/// an earlier one wrote. A world where an areaportal brush and a water brush
/// can touch is therefore built serially (<see cref="Applies"/>).</item>
/// </list>
/// <para>
/// Every block's subtrees may still fork below it, on the same scheduler,
/// which is what keeps a few large blocks from finishing alone.
/// </para>
/// </remarks>
internal static class ParallelWorldPass
{
    /// <summary>
    /// Whether a world pass over <paramref name="count"/> blocks is built in
    /// parallel: the context must be the compile's root, with a tree
    /// parallelism that has a scheduler and more than one thread, the grid
    /// must have more than one block, and no areaportal brush may be able to
    /// meet a water brush.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="count">The blocks in the grid.</param>
    /// <returns>True to build the blocks in parallel.</returns>
    internal static bool Applies(BspBuildContext context, int count) =>
        count > 1
        && !context.IsFork
        && context.TreeParallelism is { Scheduler: not null, MaxDegree: > 1 }
        && !AreaportalMayMeetWater(context);

    /// <summary>
    /// Whether any areaportal brush in the context's brush range could be
    /// met by a water or slime brush in it, the pair
    /// <see cref="AreaportalWaterFixup"/> rewrites map brushes for.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <returns>True when some such pair's boxes overlap, or come within a unit.</returns>
    /// <remarks>
    /// Conservative on purpose: it tests the map brushes' boxes, grown by a
    /// unit, where the fixup tests the block's clipped brushes, which lie
    /// inside them, and it ignores the detail screen. A world it wrongly
    /// sends to the serial pass is only slower; one it wrongly let through
    /// would be a different compile.
    /// </remarks>
    internal static bool AreaportalMayMeetWater(BspBuildContext context)
    {
        IReadOnlyList<MapBrush> brushes = context.Map.Brushes;
        List<MapBrush> portals = [];
        List<MapBrush> waters = [];
        for (int i = context.BrushStart; i < context.BrushEnd; i++)
        {
            MapBrush brush = brushes[i];
            if ((brush.Contents & (int)BrushContents.AreaPortal) != 0)
            {
                portals.Add(brush);
            }
            else if ((brush.Contents & BrushCsg.SplitAreaPortalMask) != 0)
            {
                waters.Add(brush);
            }
        }

        foreach (MapBrush portal in portals)
        {
            foreach (MapBrush water in waters)
            {
                if (portal.Mins.X < water.Maxs.X + 1 && water.Mins.X < portal.Maxs.X + 1
                    && portal.Mins.Y < water.Maxs.Y + 1 && water.Mins.Y < portal.Maxs.Y + 1
                    && portal.Mins.Z < water.Maxs.Z + 1 && water.Mins.Z < portal.Maxs.Z + 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Builds every block of the grid and puts each head node in
    /// <paramref name="blocks"/>, as the serial loop does.
    /// </summary>
    /// <param name="context">The compile's root build context.</param>
    /// <param name="grid">The clamped grid.</param>
    /// <param name="blocks">The head node array, indexed as the serial loop indexes it.</param>
    /// <returns>One record per block, in block order.</returns>
    /// <remarks>
    /// Whatever happens, every block's fork has given its arena back and the
    /// plane table is writable again when this returns or throws.
    /// </remarks>
    internal static BlockBuildStatistics[] BuildBlocks(BspBuildContext context, BspBlockGrid grid, BspNode?[,] blocks)
    {
        BspTreeParallelism parallel = context.TreeParallelism!;
        TaskScheduler scheduler = parallel.Scheduler!;
        int width = grid.MaxX - grid.MinX + 1;
        int count = width * (grid.MaxY - grid.MinY + 1);
        Block[] jobs = new Block[count];
        BlockBuildStatistics[] records = new BlockBuildStatistics[count];

        try
        {
            // Every fork is made here, on this thread, so that none reads the
            // root while a join writes it; each rents its arena only when its
            // block first allocates.
            bool headPlanesFound = false;
            Span<int> headPlanes = stackalloc int[6];
            for (int b = 0; b < count; b++)
            {
                int blockY = grid.MinY + (b / width);
                int blockX = grid.MinX + (b % width);
                BlockGrid.BlockBounds(blockX, blockY, out Vec3 mins, out Vec3 maxs);

                BspBuildContext fork = context.ForkForBlock();
                fork.TreeParallelism = parallel;
                Block job = jobs[b] = new Block(blockX, blockY, mins, maxs, fork);

                if (headPlanesFound)
                {
                    // Only the bounding planes' finds, in this block's turn:
                    // the block's own list finds them again, and only reads.
                    context.ComputeBoundingPlanes(mins, maxs);
                    continue;
                }

                // Until some block has brushes, list each one here, so the
                // first to have any appends its head volume's planes at the
                // point in the sequence the serial pass would.
                job.Brushes = BrushCsg.MakeBspBrushList(
                    fork, fork.BrushStart, fork.BrushEnd, mins, maxs, DetailScreen.NoDetail);
                job.Listed = true;
                if (job.Brushes is not null)
                {
                    BrushGeometry.FindBoundsPlanes(context.Planes, mins, maxs, headPlanes);
                    headPlanesFound = true;
                }
            }

            Lock gate = new();
            int next = 0;
            context.Planes.Freeze();
            try
            {
                CallerParallelFor.For(
                    count,
                    parallel.MaxDegree,
                    scheduler,
                    static () => 0,
                    (b, _) =>
                    {
                        Build(jobs[b]);
                        lock (gate)
                        {
                            jobs[b].Done = true;
                            while (next < count && jobs[next].Done)
                            {
                                Join(context, jobs[next], blocks, records, next);
                                next++;
                            }
                        }
                    },
                    parallel.CancellationToken);
            }
            finally
            {
                context.Planes.Thaw();
            }
        }
        finally
        {
            foreach (Block? job in jobs)
            {
                job?.Fork.ReleaseForkWindings();
            }
        }

        return records;
    }

    // One block, in its fork: ProcessBlock without the empty block's leaf,
    // which is allocated at the join, in the root, where the serial pass
    // allocates it.
    private static void Build(Block job)
    {
        BspBuildContext fork = job.Fork;
        BspBrush? brushes = job.Listed
            ? job.Brushes
            : BrushCsg.MakeBspBrushList(fork, fork.BrushStart, fork.BrushEnd, job.Mins, job.Maxs, DetailScreen.NoDetail);
        job.Brushes = null;

        if (brushes is null)
        {
            job.Record = new BlockBuildStatistics(job.X, job.Y, null, null);
            return;
        }

        job.Head = BlockGrid.CarveBlock(fork, job.X, job.Y, brushes, job.Mins, job.Maxs, out BlockBuildStatistics record);
        job.Record = record;
    }

    // Folds one finished block into the root, in block order.
    private static void Join(BspBuildContext context, Block job, BspNode?[,] blocks, BlockBuildStatistics[] records, int index)
    {
        context.JoinBlock(job.Fork, job.Head);
        BspNode head = job.Head ?? BlockGrid.SolidBlock(context);
        job.Fork.ReleaseForkWindings();

        blocks[job.X - BlockGrid.BlockMin + 1, job.Y - BlockGrid.BlockMin + 1] = head;
        records[index] = job.Record;
    }

    /// <summary>One block's work and result.</summary>
    private sealed class Block(int x, int y, Vec3 mins, Vec3 maxs, BspBuildContext fork)
    {
        public int X { get; } = x;

        public int Y { get; } = y;

        public Vec3 Mins { get; } = mins;

        public Vec3 Maxs { get; } = maxs;

        public BspBuildContext Fork { get; } = fork;

        /// <summary>The brush list, when it was made before the parallel part.</summary>
        public BspBrush? Brushes { get; set; }

        /// <summary>Whether <see cref="Brushes"/> was made before the parallel part.</summary>
        public bool Listed { get; set; }

        /// <summary>The block tree's head, or null for a block with no brushes.</summary>
        public BspNode? Head { get; set; }

        public BlockBuildStatistics Record { get; set; }

        /// <summary>Built and waiting to be joined; read and written under the join lock.</summary>
        public bool Done { get; set; }
    }
}
