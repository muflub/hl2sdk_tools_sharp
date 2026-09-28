//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// How <see cref="BrushBspTree.BrushBsp"/> may build a tree's subtrees in
/// parallel, and the token it observes while it builds.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is parallel.</b> Once a node has chosen its plane and split its
/// brush list, the front and back lists share no brush, and building the two
/// subtrees shares only what is read: the plane table (splitting never adds a
/// plane) and the map. So the back subtree can be built on another thread in
/// a <see cref="Csg.BspBuildContext.Fork"/> while this thread builds the
/// front, and the fork is joined afterwards in the order the serial build
/// would have visited it. The output is the serial build's, node for node,
/// whatever the degree and whichever thread took which half.
/// </para>
/// <para>
/// <b>When.</b> Only where both halves hold at least
/// <see cref="MinBrushes"/> brushes, and only <see cref="MaxForkDepth"/>
/// forks deep. Choosing a plane costs roughly the square of the list, so a
/// small subtree is cheaper to build than to hand over: the back list's
/// windings are copied into the fork's arena on the way out and the
/// finished subtree's are copied back on the way in. The depth cap stops a
/// deep, balanced tree from queueing far more helpers than there are
/// threads to run them.
/// </para>
/// <para>
/// A helper is only queued, never waited for until it has started (see
/// <see cref="Parallel.CallerParallelFor"/>), so a build on a busy pool, or
/// on a pool thread whose siblings are all building subtrees too, degrades
/// to the serial recursion rather than deadlocking.
/// </para>
/// </remarks>
internal sealed record BspTreeParallelism
{
    /// <summary>
    /// The smallest brush list, on each side of a split, worth building on
    /// another thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chosen by measuring 2fort, whose world is 71 blocks of at most about
    /// 340 brushes each after the CSG chop, so a threshold in the hundreds
    /// never forks at all. With every node's own time recorded (plane choice
    /// and list split, warm, one thread) the two world passes took 1130 ms
    /// serially. The longest chain of work left when every pair at or above
    /// the threshold forks, five forks deep, was 600 ms at 8 brushes, 623 ms
    /// at 16, 717 ms at 32 and 840 ms at 64; forking every node without a
    /// depth cap would still leave 504 ms, because each block's root choice
    /// (178 ms over both passes) and the choices on the way down are serial.
    /// </para>
    /// <para>
    /// 16 keeps nearly all of 8's gain at a bit over half the forks (224 a
    /// compile rather than 390): each fork copies its back list's windings
    /// out and its finished subtree's back in, and queues a helper, which is
    /// cheap next to a 16-brush subtree but not free. The facts lower it to
    /// force forks on maps too small to reach it.
    /// </para>
    /// </remarks>
    public const int DefaultMinBrushes = 16;

    /// <summary>Where the helpers are queued, or null to build serially.</summary>
    public TaskScheduler? Scheduler { get; init; }

    /// <summary>How many forks deep a build may go; zero never forks.</summary>
    public int MaxForkDepth { get; init; }

    /// <summary>
    /// How many threads the world pass may build blocks on at once, the
    /// calling thread included (<see cref="BlockGrid.BuildWorldPass"/>).
    /// </summary>
    /// <remarks>
    /// The compile's degree. A subtree fork needs no such number, as it only
    /// ever splits in two; the world pass hands out tens of blocks, and
    /// queueing a helper per block on a pool of a few threads would only
    /// queue helpers that find nothing left to do. Two, when a caller does
    /// not say, which is what a subtree fork uses.
    /// </remarks>
    public int MaxDegree { get; init; } = 2;

    /// <summary>See <see cref="DefaultMinBrushes"/>.</summary>
    public int MinBrushes { get; init; } = DefaultMinBrushes;

    /// <summary>Observed at every node, so a cancelled compile stops mid-tree.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// The fork depth for a degree: enough levels that every thread can have a
    /// subtree, and two more, so that an uneven split still leaves work for
    /// the threads its small side finishes early on.
    /// </summary>
    /// <param name="degree">How many threads the build may use.</param>
    /// <returns>Zero for one thread; otherwise <c>ceil(log2(degree)) + 2</c>.</returns>
    public static int ForkDepthFor(int degree)
    {
        if (degree <= 1)
        {
            return 0;
        }

        // Capped well below where 1 << levels would overflow: no machine has
        // 65536 threads, and a tree is never that many forks deep anyway.
        int levels = 0;
        while (levels < 16 && (1 << levels) < degree)
        {
            levels++;
        }

        return levels + 2;
    }
}
