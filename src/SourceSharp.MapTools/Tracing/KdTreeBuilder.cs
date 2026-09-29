//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Tracing;

/// <summary>
/// Builds stock's KD-tree: the reference implementation's
/// <c>SetupAccelerationStructure</c>, <c>RefineNode</c> and
/// <c>CalculateCostsOfSplit</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every constant and every tie-break is stock's, because the tree this
/// produces is compared against stock's NODE FOR NODE and not merely by the
/// answers it gives. A build that reached the same hits through a differently
/// shaped tree would pass a ray comparison and fail the moment anything
/// downstream cached a tree.
/// </para>
/// <para>
/// The surface area heuristic: the chance of a ray that hits a box also
/// hitting a sub-box is the ratio of their surface areas, so splitting costs
/// <c>Ct + Ci * (SA(L)/SA(V) * Nl + SA(R)/SA(V) * Nr)</c> and not splitting
/// costs <c>Ci * N</c>. Stock's constants are 75 and 167 "approximate
/// #operations", and their RATIO is what decides
/// every split, so they are reproduced rather than re-derived.
/// </para>
/// </remarks>
public static class KdTreeBuilder
{
    /// <summary><c>COST_OF_TRAVERSAL</c>.</summary>
    public const float CostOfTraversal = 75.0f;

    /// <summary><c>COST_OF_INTERSECTION</c>.</summary>
    public const float CostOfIntersection = 167.0f;

    /// <summary><c>MAX_TREE_DEPTH</c>.</summary>
    public const int MaxTreeDepth = 21;

    /// <summary><c>PLANECHECK_POSITIVE</c>.</summary>
    internal const sbyte PlaneCheckPositive = 1;

    /// <summary><c>PLANECHECK_NEGATIVE</c>.</summary>
    internal const sbyte PlaneCheckNegative = -1;

    /// <summary><c>PLANECHECK_STRADDLING</c>.</summary>
    internal const sbyte PlaneCheckStraddling = 0;

    /// <summary>
    /// The 1.0e23 stock initialises bounds and costs with.
    /// </summary>
    /// <remarks>
    /// Not <see cref="float.MaxValue"/> and not an infinity, because the
    /// comparisons that follow are against this exact value and a build that
    /// started from a different one could take a different branch on a
    /// degenerate scene.
    /// </remarks>
    private const float Huge = 1.0e23f;


    /// <summary>A subtree is only a job when it is at least this big.</summary>
    internal const int ParallelMinimumTriangles = 512;

    /// <summary>
    /// How many frontier jobs a parallel build aims to give each worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frontier is where the top of the tree stops being refined level by
    /// level and each remaining subtree becomes one job. A subtree's cost is
    /// not its triangle count -- a dense cluster of prop triangles refines
    /// many levels deeper than the same number of wall triangles -- so the
    /// jobs are handed out longest-first by count and the only real defence
    /// against one job outlasting the rest is to make every job small. With
    /// eight jobs per worker, which is what this was while the top ran on one
    /// thread, the busiest worker of a four-worker 2fort build with prop
    /// polys did twice the work of the quietest.
    /// </para>
    /// <para>
    /// A finer frontier means more levels at the top, and those used to be
    /// serial. They are not any more (<see cref="TopLevels"/>), so the top
    /// can go deeper for next to nothing and the frontier can be fine.
    /// </para>
    /// </remarks>
    private const int JobsPerWorker = 32;

    /// <summary>
    /// How many triangles one item of a parallel pass over the whole scene
    /// (the copy into build form, the conversion to intersection format)
    /// takes: big enough that an item is worth handing out, small enough that
    /// 1.45 million triangles are ninety items.
    /// </summary>
    private const int SceneChunk = 16384;

    /// <summary>
    /// The most trials one node tries on one axis: the midpoint and at most
    /// three vertices of each of at most eleven sampled triangles.
    /// </summary>
    /// <remarks>
    /// The sampled triangles are those at <c>-1 + k * triSkip</c> with
    /// <c>triSkip = 1 + count / 10</c>, and there are at most
    /// <c>count / triSkip + 1</c> of them. For <c>count = 10m + r</c> with
    /// <c>r &lt;= 9</c>, <c>count / (m + 1)</c> is at most 10, so eleven
    /// triangles and 34 trials. The bound is only a buffer size: a trial
    /// beyond it would be an index out of range, not a silently different
    /// tree.
    /// </remarks>
    private const int MaxTrialsPerAxis = 34;

    /// <summary>
    /// Builds the tree.
    /// </summary>
    /// <param name="triangles">The scene. Order matters: it is the tree's index order.</param>
    /// <param name="stockNormalise">
    /// Whether the triangles' plane normals are normalised as stock does, with
    /// the reciprocal-square-root estimate
    /// (<see cref="Options.StockQuirk.KdTracerReciprocalEstimate"/>'s Stock
    /// side), rather than with a divide. The caller decides it; the nodes are
    /// the same either way.
    /// </param>
    /// <returns>The nodes, the index list, the intersection triangles, and the bounds.</returns>
    /// <exception cref="ArgumentException"><paramref name="triangles"/> is empty.</exception>
    internal static KdBuildResult Build(ReadOnlySpan<TracedTriangle> triangles, bool stockNormalise)
    {
        KdBuildTriangle[] build = Prepare(triangles, out int[] rootList, out Vec3 min, out Vec3 max);

        List<KdNode> nodes = [new KdNode()];
        List<int> indices = [];
        RefineNode(nodes, indices, build, 0, rootList, 0, rootList.Length, min, max, 0, new SplitScratch());

        KdTriangle[] intersect = new KdTriangle[build.Length];
        ConvertRange(build, intersect, 0, build.Length, stockNormalise);
        return new KdBuildResult(nodes.ToArray(), indices.ToArray(), intersect, min, max);
    }

    /// <summary>
    /// The same tree as <see cref="Build"/>, node for node, built on
    /// <paramref name="queue"/>'s workers.
    /// </summary>
    /// <param name="triangles">The scene. Order matters: it is the tree's index order.</param>
    /// <param name="stockNormalise">As for <see cref="Build"/>.</param>
    /// <param name="queue">The workers.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The same result as <see cref="Build"/>.</returns>
    internal static Task<KdBuildResult> BuildAsync(
        ReadOnlyMemory<TracedTriangle> triangles, bool stockNormalise, WorkQueue queue, CancellationToken cancellationToken) =>
        BuildAsync(triangles, stockNormalise, queue, ParallelMinimumTriangles, cancellationToken);

    /// <summary>
    /// <see cref="BuildAsync(ReadOnlyMemory{TracedTriangle}, bool, WorkQueue, CancellationToken)"/>
    /// with the smallest frontier job chosen by the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three steps, and none of them runs on the caller's thread.
    /// </para>
    /// <para>
    /// <b>The top.</b> Every node bigger than a job is refined level by level
    /// (<see cref="TopLevels"/>), each level spread over the workers: the
    /// per-axis extents of every node, then every split trial of every node,
    /// then every node's partition. The decisions between those steps are
    /// taken serially, in the same order the serial build takes them, from
    /// numbers each computed exactly as the serial build computes them. Nothing
    /// is summed across items, so no float depends on how the work was cut up
    /// or who did it. Nodes that are small enough become frontier jobs.
    /// </para>
    /// <para>
    /// <b>The frontier.</b> Each job is built by the serial
    /// <see cref="RefineNode"/> into its own node and index lists, rooted at
    /// local node 0, in parallel, longest first.
    /// </para>
    /// <para>
    /// <b>The assembly.</b> The pieces are laid out in the order the serial
    /// build would have appended them: a split's two children next, then all
    /// of the left subtree, then all of the right. That order is what makes a
    /// relocation enough. A serial subtree rooted at node <c>g</c> appends its
    /// descendants starting wherever the list ends when <c>g</c> splits, and a
    /// local build of the same subtree does the same starting at 1, so local
    /// node <c>k &gt;= 1</c> lands at <c>base + k - 1</c> and a leaf's index
    /// run moves by the length of the index list before it. The triangles'
    /// conversion to intersection format runs beside the assembly, in chunks,
    /// since each triangle's conversion reads only that triangle.
    /// </para>
    /// <para>
    /// With one worker there is no top: the whole scene is one job, which is
    /// <see cref="Build"/> run on the worker.
    /// </para>
    /// </remarks>
    /// <param name="triangles">The scene. Order matters: it is the tree's index order.</param>
    /// <param name="stockNormalise">As for <see cref="Build"/>.</param>
    /// <param name="queue">The workers.</param>
    /// <param name="minimumJob">
    /// The smallest subtree that is a job of its own; the facts lower it to
    /// drive the level-by-level top deep into small scenes.
    /// </param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The same result as <see cref="Build"/>.</returns>
    internal static async Task<KdBuildResult> BuildAsync(
        ReadOnlyMemory<TracedTriangle> triangles,
        bool stockNormalise,
        WorkQueue queue,
        int minimumJob,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumJob, 1);

        // The preparation: the scene box, one item per axis, and the copy
        // into build form in chunks.
        RefuseEmpty(triangles.Length);
        KdBuildTriangle[] build = new KdBuildTriangle[triangles.Length];
        int[] rootList = new int[triangles.Length];
        float[] extent = new float[6];
        int copies = Chunks(triangles.Length);
        await queue.RunAsync(
                3 + copies,
                (i, _) =>
                {
                    if (i < 3)
                    {
                        (extent[i], extent[3 + i]) = SceneExtent(triangles.Span, i);
                        return;
                    }

                    (int start, int count) = Chunk(i - 3, triangles.Length);
                    PrepareRange(triangles.Span, build, rootList, start, count);
                },
                new WorkQueueOptions { Stage = Stage, ChunkSize = 1 },
                cancellationToken)
            .ConfigureAwait(false);
        Vec3 min = new(extent[0], extent[1], extent[2]);
        Vec3 max = new(extent[3], extent[4], extent[5]);

        int jobSize = queue.Degree > 1
            ? Math.Max(minimumJob, rootList.Length / (queue.Degree * JobsPerWorker))
            : int.MaxValue;
        TopLevels top = new(build, jobSize);
        Skeleton root = top.Add(rootList, 0, rootList.Length, min, max, 0);
        while (top.HasLevel)
        {
            await top.RefineLevelAsync(queue, cancellationToken).ConfigureAwait(false);
        }

        List<FrontierJob> jobs = top.Jobs;
        SubTree[] built = await queue.RunAsync(
                jobs.Count,
                (i, scratch, _) =>
                {
                    FrontierJob job = jobs[i];
                    SubTree sub = new();
                    RefineNode(
                        sub.Nodes, sub.Indices, build, 0, job.List, job.Offset, job.Count, job.Min, job.Max,
                        job.Depth, scratch);
                    return sub;
                },
                static _ => new SplitScratch(),
                new WorkQueueOptions { Stage = Stage, ItemCost = i => jobs[i].Count },
                cancellationToken)
            .ConfigureAwait(false);

        // The assembly: the skeleton is laid out serially (it is small), which
        // places every subtree; the subtrees are then copied into place, and
        // the triangles converted to intersection format, in parallel.
        Layout layout = Layout.Place(root, built);
        KdTriangle[] intersect = new KdTriangle[build.Length];
        await queue.RunAsync(
                built.Length + Chunks(build.Length),
                (i, _) =>
                {
                    if (i < built.Length)
                    {
                        layout.CopySubTree(i, built[i]);
                        return;
                    }

                    (int start, int count) = Chunk(i - built.Length, build.Length);
                    ConvertRange(build, intersect, start, count, stockNormalise);
                },
                new WorkQueueOptions { Stage = Stage, ChunkSize = 1 },
                cancellationToken)
            .ConfigureAwait(false);
        return new KdBuildResult(layout.Nodes, layout.Indices, intersect, min, max);
    }

    /// <summary>The progress stage every run of a build reports under.</summary>
    private const string Stage = "kd tree";

    /// <summary>How many <see cref="SceneChunk"/> items cover a scene of <paramref name="length"/> triangles.</summary>
    private static int Chunks(int length) => (length + SceneChunk - 1) / SceneChunk;

    /// <summary>Where item <paramref name="chunk"/> of a scene pass starts, and how many triangles it takes.</summary>
    private static (int Start, int Count) Chunk(int chunk, int length)
    {
        int start = chunk * SceneChunk;
        return (start, Math.Min(SceneChunk, length - start));
    }

    /// <summary>Converts <paramref name="count"/> triangles from <paramref name="start"/> to intersection format.</summary>
    private static void ConvertRange(
        KdBuildTriangle[] build, KdTriangle[] intersect, int start, int count, bool stockNormalise)
    {
        for (int i = start; i < start + count; i++)
        {
            intersect[i] = ToIntersectionFormat(build[i], stockNormalise);
        }
    }

    /// <summary>
    /// The assembled tree: the skeleton laid out in the serial build's order,
    /// and a place for every frontier subtree in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The serial build appends a split's two children, then all of the left
    /// subtree, then all of the right, and a leaf's triangles to the end of
    /// the index list. <see cref="Place"/> walks the skeleton in that order
    /// with two cursors, writing the skeleton's own nodes and leaves as it
    /// goes; a frontier subtree it only reserves room for, since its size is
    /// already known. A serial subtree rooted at node <c>g</c> appends its
    /// descendants wherever the list ends when <c>g</c> splits, and a local
    /// build of the same subtree does the same starting at 1, so local node
    /// <c>k &gt;= 1</c> lands at <c>base + k - 1</c> and a leaf's index run
    /// moves by the length of the index list before it.
    /// </para>
    /// <para>
    /// With every place known, the subtrees are independent: each writes only
    /// its own range of both arrays, so <see cref="CopySubTree"/> runs one item
    /// per subtree in parallel. On 2fort with prop polys that copy is 766,889
    /// nodes and 3.8 million indices, which as one serial item was the
    /// longest single step left after the frontier.
    /// </para>
    /// </remarks>
    private sealed class Layout
    {
        private readonly (int At, int NodeBase, int IndexBase)[] _places;
        private int _nodeCursor = 1;
        private int _indexCursor;

        private Layout(int nodes, int indices, int subTrees)
        {
            Nodes = new KdNode[nodes];
            Indices = new int[indices];
            _places = new (int, int, int)[subTrees];
        }

        /// <summary>The whole tree's nodes; node 0 is the root.</summary>
        public KdNode[] Nodes { get; }

        /// <summary>The whole tree's index list.</summary>
        public int[] Indices { get; }

        /// <summary>Lays the skeleton out and places every subtree.</summary>
        public static Layout Place(Skeleton root, SubTree[] built)
        {
            (int nodes, int indices) = Size(root, built);
            Layout layout = new(1 + nodes, indices, built.Length);
            layout.Walk(root, 0, built);
            return layout;
        }

        /// <summary>Copies subtree <paramref name="job"/> into its place, relocating its links.</summary>
        public void CopySubTree(int job, SubTree sub)
        {
            (int at, int nodeBase, int indexBase) = _places[job];
            for (int k = 0; k < sub.Nodes.Count; k++)
            {
                KdNode n = sub.Nodes[k];
                n.Children = (n.Children & 3) == KdNode.Leaf
                    ? KdNode.Leaf + (((n.Children >> 2) + indexBase) << 2)
                    : (n.Children & 3) + (((n.Children >> 2) + nodeBase) << 2);
                Nodes[k == 0 ? at : nodeBase + k] = n;
            }

            sub.Indices.CopyTo(Indices, indexBase);
        }

        /// <summary>How many nodes below its own, and how many indices, a skeleton node lays out.</summary>
        private static (int Nodes, int Indices) Size(Skeleton s, SubTree[] built)
        {
            if (s.Left is { } left && s.Right is { } right)
            {
                (int ln, int li) = Size(left, built);
                (int rn, int ri) = Size(right, built);
                return (2 + ln + rn, li + ri);
            }

            if (s.Job < 0)
            {
                return (0, s.LeafCount);
            }

            return (built[s.Job].Nodes.Count - 1, built[s.Job].Indices.Count);
        }

        private void Walk(Skeleton s, int at, SubTree[] built)
        {
            if (s.Left is { } left && s.Right is { } right)
            {
                int leftChild = _nodeCursor;
                Nodes[at] = new KdNode { Children = s.Plane + (leftChild << 2), Split = s.Value };
                _nodeCursor += 2;
                Walk(left, leftChild, built);
                Walk(right, leftChild + 1, built);
                return;
            }

            if (s.Job < 0)
            {
                KdNode leaf = new() { Children = KdNode.Leaf + (_indexCursor << 2) };
                leaf.TriangleCount = s.LeafCount;
                Nodes[at] = leaf;
                s.LeafList.AsSpan(s.LeafOffset, s.LeafCount).CopyTo(Indices.AsSpan(_indexCursor));
                _indexCursor += s.LeafCount;
                return;
            }

            SubTree sub = built[s.Job];
            _places[s.Job] = (at, _nodeCursor - 1, _indexCursor);
            _nodeCursor += sub.Nodes.Count - 1;
            _indexCursor += sub.Indices.Count;
        }
    }

    /// <summary>One frontier subtree: its triangles, box and depth.</summary>
    private sealed record FrontierJob(int[] List, int Offset, int Count, Vec3 Min, Vec3 Max, int Depth);

    /// <summary>A frontier subtree built on its own, rooted at local node 0.</summary>
    private sealed class SubTree
    {
        public List<KdNode> Nodes { get; } = [new KdNode()];

        public List<int> Indices { get; } = [];
    }

    /// <summary>
    /// The top of a parallel build: a split (both children set), a frontier
    /// job (<see cref="Job"/> set), or otherwise a leaf. Filled in as the
    /// levels are decided.
    /// </summary>
    private sealed class Skeleton
    {
        public int Plane { get; set; }

        public float Value { get; set; }

        public Skeleton? Left { get; set; }

        public Skeleton? Right { get; set; }

        public int Job { get; set; } = -1;

        public int[] LeafList { get; set; } = [];

        public int LeafOffset { get; set; }

        public int LeafCount { get; set; }
    }

    /// <summary>
    /// The top of a parallel build, refined a level at a time with every step
    /// of a level spread over the workers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not a subtree per worker from the root.</b> Before this the top
    /// was <c>RefineNode</c>'s recursion run on one thread down to the
    /// frontier, and on 2fort with prop polys (1.45 million triangles) that
    /// thread spent about seven CPU-seconds while every other worker waited:
    /// the root alone costs a hundred passes over the whole scene. Splitting
    /// a node's subtrees across workers cannot start until the node itself is
    /// decided, so the only way to shorten that path is to spread the node.
    /// </para>
    /// <para>
    /// <b>What a node's work is.</b> Choosing a split tries up to
    /// <see cref="MaxTrialsPerAxis"/> planes on each axis, and each trial
    /// classifies every triangle. The trials are independent of each other --
    /// each starts from the node's box and its own plane, and the only thing
    /// one trial leaves behind in the serial build is its labels, which the
    /// partition re-derives for the winner -- so they are the parallel items.
    /// A hundred trials per node is plenty to spread over any worker count,
    /// even at the root.
    /// </para>
    /// <para>
    /// <b>Why it is the same tree.</b> Each trial's cost comes from the one
    /// function the serial build calls, over the same inputs, so its float is
    /// the same bit for bit whichever worker ran it; the choice is then the
    /// serial build's strictly-less scan over those costs in the serial
    /// build's order, on one thread; the partition is the serial build's
    /// partition. The counts are integers, and nothing else is combined
    /// across items. The level order is only the order the work is DONE in;
    /// the skeleton records the shape, and the assembly lays it out in the
    /// serial order regardless.
    /// </para>
    /// </remarks>
    private sealed class TopLevels(KdBuildTriangle[] tris, int jobSize)
    {
        private List<TopNode> _level = [];

        /// <summary>The frontier jobs found so far, in the order they were found.</summary>
        public List<FrontierJob> Jobs { get; } = [];

        /// <summary>Whether a level is waiting to be refined.</summary>
        public bool HasLevel => _level.Count > 0;

        /// <summary>
        /// Places a node: a frontier job when it is small enough, otherwise a
        /// node of the next level.
        /// </summary>
        public Skeleton Add(int[] list, int offset, int count, Vec3 minBound, Vec3 maxBound, int depth)
        {
            Skeleton shell = new();
            if (count <= jobSize)
            {
                Jobs.Add(new FrontierJob(list, offset, count, minBound, maxBound, depth));
                shell.Job = Jobs.Count - 1;
            }
            else
            {
                _level.Add(new TopNode(shell, list, offset, count, minBound, maxBound, depth));
            }

            return shell;
        }

        /// <summary>Decides every node of the current level and queues their children.</summary>
        public async Task RefineLevelAsync(WorkQueue queue, CancellationToken cancellationToken)
        {
            TopNode[] level = [.. _level];
            _level = [];
            WorkQueueOptions options = new() { Stage = Stage };

            // Extents: one item per node and axis.
            await queue.RunAsync(
                    level.Length * 3,
                    (i, _) => level[i / 3].Extents(tris, i % 3),
                    options,
                    cancellationToken)
                .ConfigureAwait(false);

            // Trials: listed serially (a few dozen per node, in the serial
            // build's order), costed in parallel.
            List<TrialRef> trials = [];
            for (int n = 0; n < level.Length; n++)
            {
                level[n].CollectTrials(tris, n, trials);
            }

            await queue.RunAsync(
                    trials.Count,
                    (i, _) =>
                    {
                        TrialRef t = trials[i];
                        level[t.Node].Cost(t.Axis, t.Trial);
                    },
                    options,
                    cancellationToken)
                .ConfigureAwait(false);

            // Partitions, each deciding its node first: one item per node.
            await queue.RunAsync(level.Length, (i, _) => level[i].Decide(), options, cancellationToken)
                .ConfigureAwait(false);

            foreach (TopNode node in level)
            {
                SplitChoice c = node.Choice;
                Skeleton shell = node.Shell;
                if (c.IsLeaf)
                {
                    shell.LeafList = node.List;
                    shell.LeafOffset = node.Offset;
                    shell.LeafCount = node.Count;
                    continue;
                }

                shell.Plane = c.Plane;
                shell.Value = c.Value;
                shell.Left = Add(c.Partitioned, 0, c.NLeft + c.NBoth, node.Min, c.LeftMax, c.ChildDepth);
                shell.Right = Add(c.Partitioned, c.NLeft, c.NRight + c.NBoth, c.RightMin, node.Max, c.ChildDepth);
            }
        }
    }

    /// <summary>One trial of one node of a level: which node, which axis, which of its trials.</summary>
    private readonly record struct TrialRef(int Node, int Axis, int Trial);

    /// <summary>One costed trial: what <see cref="CalculateCostsOfSplit"/> returned for it.</summary>
    private readonly record struct TrialResult(float Cost, float Value, int NLeft, int NRight, int NBoth);

    /// <summary>
    /// A node of the parallel top, with its per-axis extents and its trials'
    /// costs while its level is being refined.
    /// </summary>
    private sealed class TopNode(
        Skeleton shell, int[] list, int offset, int count, Vec3 minBound, Vec3 maxBound, int depth)
    {
        private readonly float[][] _mins = new float[3][];
        private readonly float[][] _maxs = new float[3][];
        private readonly float[] _minCoord = new float[3];
        private readonly float[] _maxCoord = new float[3];
        private readonly float[][] _splits = [[], [], []];
        private readonly TrialResult[][] _results = [[], [], []];

        public Skeleton Shell => shell;

        public int[] List => list;

        public int Offset => offset;

        public int Count => count;

        public Vec3 Min => minBound;

        public Vec3 Max => maxBound;

        /// <summary>The decision, once <see cref="Decide"/> has run.</summary>
        public SplitChoice Choice { get; private set; } = SplitChoice.Leaf;

        /// <summary>Whether the node is a leaf without trying anything.</summary>
        private bool IsLeafOutright => IsLeafWithoutTrials(count, depth);

        public void Extents(KdBuildTriangle[] tris, int axis)
        {
            if (IsLeafOutright)
            {
                return;
            }

            _mins[axis] = new float[count];
            _maxs[axis] = new float[count];
            (_minCoord[axis], _maxCoord[axis]) = AxisExtents(tris, list, offset, count, axis, _mins[axis], _maxs[axis]);
        }

        public void CollectTrials(KdBuildTriangle[] tris, int node, List<TrialRef> trials)
        {
            if (IsLeafOutright)
            {
                return;
            }

            Span<float> splits = stackalloc float[MaxTrialsPerAxis];
            for (int axis = 0; axis < 3; axis++)
            {
                int n = CollectTrialSplits(tris, list, offset, count, minBound, maxBound, axis, splits);
                _splits[axis] = splits[..n].ToArray();
                _results[axis] = new TrialResult[n];
                for (int k = 0; k < n; k++)
                {
                    trials.Add(new TrialRef(node, axis, k));
                }
            }
        }

        public void Cost(int axis, int trial)
        {
            float value = _splits[axis][trial];
            float cost = CalculateCostsOfSplit(
                _mins[axis], _maxs[axis], axis, _minCoord[axis], _maxCoord[axis], minBound, maxBound,
                ref value, out int nl, out int nr, out int nb);
            _results[axis][trial] = new TrialResult(cost, value, nl, nr, nb);
        }

        /// <summary>
        /// The serial build's choice over the costed trials, in its order, and
        /// the partition; then lets go of everything but the decision.
        /// </summary>
        public void Decide()
        {
            if (!IsLeafOutright)
            {
                BestSplit best = BestSplit.None;
                for (int axis = 0; axis < 3; axis++)
                {
                    for (int k = 0; k < _results[axis].Length; k++)
                    {
                        TrialResult r = _results[axis][k];
                        best.Offer(r.Cost, axis, _splits[axis][k], r.Value, r.NLeft, r.NRight, r.NBoth);
                    }
                }

                Choice = Finalise(
                    in best, list, offset, count, _mins[best.Axis], _maxs[best.Axis], minBound, maxBound, depth);
            }

            for (int axis = 0; axis < 3; axis++)
            {
                _mins[axis] = null!;
                _maxs[axis] = null!;
                _splits[axis] = [];
                _results[axis] = [];
            }
        }
    }

    /// <summary>Refuses an empty scene.</summary>
    /// <exception cref="ArgumentException">There are no triangles.</exception>
    private static void RefuseEmpty(int length)
    {
        if (length == 0)
        {
            throw new ArgumentException(
                "a KD-tree over no triangles has no bounds to start from; stock would read "
                + "1.0e23 back out of CalculateTriangleListBounds and build a tree around it",
                "triangles");
        }
    }

    /// <summary>The serial build's preparation: the build triangles, the root list and the scene box.</summary>
    private static KdBuildTriangle[] Prepare(
        ReadOnlySpan<TracedTriangle> triangles, out int[] rootList, out Vec3 min, out Vec3 max)
    {
        RefuseEmpty(triangles.Length);
        KdBuildTriangle[] build = new KdBuildTriangle[triangles.Length];
        rootList = new int[triangles.Length];
        PrepareRange(triangles, build, rootList, 0, triangles.Length);
        (float minX, float maxX) = SceneExtent(triangles, 0);
        (float minY, float maxY) = SceneExtent(triangles, 1);
        (float minZ, float maxZ) = SceneExtent(triangles, 2);
        min = new Vec3(minX, minY, minZ);
        max = new Vec3(maxX, maxY, maxZ);
        return build;
    }

    /// <summary>Copies a run of the scene into build form and numbers it in the root list.</summary>
    private static void PrepareRange(
        ReadOnlySpan<TracedTriangle> triangles, KdBuildTriangle[] build, int[] rootList, int start, int count)
    {
        for (int i = start; i < start + count; i++)
        {
            ref KdBuildTriangle t = ref build[i];
            t.Id = triangles[i].Id;
            t.Flags = triangles[i].Flags;
            Store(ref t, 0, triangles[i].V0);
            Store(ref t, 1, triangles[i].V1);
            Store(ref t, 2, triangles[i].V2);
            rootList[i] = i;
        }
    }

    private static void Store(ref KdBuildTriangle t, int vertex, Vec3 v)
    {
        t.V[(vertex * 3) + 0] = v.X;
        t.V[(vertex * 3) + 1] = v.Y;
        t.V[(vertex * 3) + 2] = v.Z;
    }

    /// <summary>
    /// One axis of <c>RayTracingEnvironment::CalculateTriangleListBounds</c>
    /// over the whole scene.
    /// </summary>
    /// <remarks>
    /// Stock folds all three axes in one loop, but each axis's running value
    /// only ever meets that axis's coordinates, in triangle order and then
    /// vertex order; so the three folds are independent, and one per axis
    /// gives the same bits and lets a parallel build take them at once.
    /// </remarks>
    private static (float Min, float Max) SceneExtent(ReadOnlySpan<TracedTriangle> triangles, int axis)
    {
        float min = Huge;
        float max = -Huge;
        foreach (TracedTriangle t in triangles)
        {
            float a = t.V0[axis];
            float b = t.V1[axis];
            float c = t.V2[axis];
            min = MathF.Min(MathF.Min(MathF.Min(min, a), b), c);
            max = MathF.Max(MathF.Max(MathF.Max(max, a), b), c);
        }

        return (min, max);
    }

    /// <summary><c>BoxSurfaceArea</c>.</summary>
    /// <remarks>
    /// Stock's expression is <c>2.0*((d0*d2)+(d0*d1)+(d1*d2))</c> over float
    /// components, so the three products and their two sums happen in FLOAT
    /// and only the doubling is promoted. That boundary decides splits: the
    /// cost comparison below is against a number this returns, and two splits
    /// are often within a bit of each other.
    /// </remarks>
    private static float BoxSurfaceArea(Vec3 min, Vec3 max)
    {
        float dx = max.X - min.X;
        float dy = max.Y - min.Y;
        float dz = max.Z - min.Z;

        // The products and their sum are FLOAT; only the final doubling is
        // double, because that is exactly where stock crosses over: the
        // literal is 2.0, which promotes the multiply and nothing before it.
        // Computing the whole expression in double instead built a tree with
        // TWO nodes too many on a 2,000-triangle scene -- one split that stock
        // declines -- which is what this comment is here to stop anyone
        // tidying back.
        return (float)(2.0 * ((dx * dz) + (dx * dy) + (dy * dz)));
    }

    /// <summary>
    /// One axis of a node: each triangle's lowest and highest coordinate on
    /// it, and the lowest and highest over the whole node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why computed once per axis.</b> Stock's
    /// <c>CalculateCostsOfSplit</c> recomputes both for every trial: a pass
    /// over the full 36-byte triangles, three min/max pairs per triangle, up
    /// to 34 times per axis. Neither depends on the trial's plane, so they
    /// are taken here once and every trial is then a pass over two packed
    /// float arrays (<see cref="CountSides"/>). That, not threads, is most of
    /// what made the build cheaper.
    /// </para>
    /// <para>
    /// <b>Why the same bits.</b> Both are computed with the operations, and in
    /// the order, stock uses. A triangle's extent is
    /// <c>min(min(v0, v1), v2)</c>, which is <c>ClassifyAgainstAxisSplit</c>'s
    /// loop; the node's extent folds every vertex into the running value in
    /// triangle order and then vertex order, which is
    /// <c>CalculateCostsOfSplit</c>'s loop. Min and max of non-NaN floats are
    /// exact and order-free anyway, but keeping the order keeps even a NaN's
    /// payload where stock would leave it.
    /// </para>
    /// </remarks>
    private static (float MinCoord, float MaxCoord) AxisExtents(
        KdBuildTriangle[] tris, int[] list, int offset, int count, int axis, Span<float> mins, Span<float> maxs)
    {
        float minCoord = Huge;
        float maxCoord = -Huge;
        for (int t = 0; t < count; t++)
        {
            ref KdBuildTriangle tri = ref tris[list[offset + t]];
            float a = tri.Get(0, axis);
            float b = tri.Get(1, axis);
            float c = tri.Get(2, axis);
            minCoord = MathF.Min(MathF.Min(MathF.Min(minCoord, a), b), c);
            maxCoord = MathF.Max(MathF.Max(MathF.Max(maxCoord, a), b), c);
            mins[t] = MathF.Min(MathF.Min(a, b), c);
            maxs[t] = MathF.Max(MathF.Max(a, b), c);
        }

        return (minCoord, maxCoord);
    }

    /// <summary>
    /// A node's trial planes on one axis, in stock's order: the box's
    /// midpoint, then the vertices of every <c>triSkip</c>-th triangle that
    /// lie inside the box.
    /// </summary>
    /// <remarks>
    /// Strided, so a big list does not cost O(n^2) split trials: one candidate
    /// triangle in every <c>1 + n/10</c>. The stride starts from -1, the
    /// midpoint, so the sampled triangles are <c>triSkip - 1</c>,
    /// <c>2 * triSkip - 1</c> and so on, not 0, <c>triSkip</c>, ...
    /// </remarks>
    /// <returns>How many planes were written to <paramref name="splits"/>.</returns>
    private static int CollectTrialSplits(
        KdBuildTriangle[] tris, int[] list, int offset, int count, Vec3 minBound, Vec3 maxBound, int axis,
        Span<float> splits)
    {
        int n = 0;
        int triSkip = 1 + (count / 10);
        for (int ts = -1; ts < count; ts += triSkip)
        {
            for (int tv = 0; tv < 3; tv++)
            {
                float trialSplit;
                if (ts == -1)
                {
                    trialSplit = 0.5f * (Axis(minBound, axis) + Axis(maxBound, axis));
                }
                else
                {
                    trialSplit = tris[list[offset + ts]].Get(tv, axis);
                    if (trialSplit > Axis(maxBound, axis) || trialSplit < Axis(minBound, axis))
                    {
                        continue;
                    }
                }

                splits[n++] = trialSplit;
                if (ts == -1)
                {
                    break;
                }
            }
        }

        return n;
    }

    /// <summary>
    /// <c>CacheOptimizedTriangle::ClassifyAgainstAxisSplit</c>, given the
    /// triangle's extent on the axis (<see cref="AxisExtents"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE THIRD TEST IS UNREACHABLE, in stock as well as here, and it is kept
    /// only because a port that quietly drops a line has to be able to say
    /// which line and why. If <c>minc == maxc == c</c> then either
    /// <c>c &gt;= splitValue</c>, and the first test already returned POSITIVE,
    /// or <c>c &lt; splitValue</c>, and since <c>maxc</c> is also <c>c</c> the
    /// second test already returned NEGATIVE. There is no third case.
    /// </para>
    /// <para>
    /// Mutation testing is how that was established rather than reasoned:
    /// flipping this return to NEGATIVE leaves every fact green on BOTH
    /// recorded scenes, including the axis-aligned one that exists precisely
    /// to make degenerate classifications happen.
    /// </para>
    /// </remarks>
    internal static sbyte ClassifyAgainstAxisSplit(float minc, float maxc, float splitValue)
    {
        if (minc >= splitValue)
        {
            return PlaneCheckPositive;
        }

        if (maxc <= splitValue)
        {
            return PlaneCheckNegative;
        }

        if (minc == maxc)
        {
            return PlaneCheckPositive;
        }

        return PlaneCheckStraddling;
    }

    /// <summary>
    /// How many triangles <see cref="ClassifyAgainstAxisSplit"/> puts on each
    /// side of <paramref name="splitValue"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the build's inner loop -- every trial of every node -- so it is
    /// vectorised: the same three comparisons, lane by lane, each lane's
    /// verdict an all-ones mask that is subtracted into an integer count.
    /// Comparisons are exact, NaN compares false in both forms, and the lane
    /// counts are summed as integers, so the vector and scalar paths agree on
    /// every input; the facts check that on NaNs, signed zeros and flat
    /// triangles. The tail below a full vector is the scalar function itself.
    /// </para>
    /// <para>
    /// The unreachable third test is carried into the vector form too, for
    /// the reason <see cref="ClassifyAgainstAxisSplit"/> gives.
    /// </para>
    /// </remarks>
    internal static void CountSides(
        ReadOnlySpan<float> mins, ReadOnlySpan<float> maxs, float splitValue,
        out int nleft, out int nright, out int nboth)
    {
        int count = mins.Length;
        int left = 0;
        int right = 0;
        int t = 0;
        if (Vector.IsHardwareAccelerated && count >= Vector<float>.Count)
        {
            Vector<float> split = new(splitValue);
            Vector<int> accLeft = Vector<int>.Zero;
            Vector<int> accRight = Vector<int>.Zero;
            for (; t <= count - Vector<float>.Count; t += Vector<float>.Count)
            {
                Vector<float> minc = new(mins[t..]);
                Vector<float> maxc = new(maxs[t..]);
                Vector<int> positive = Vector.GreaterThanOrEqual(minc, split);
                Vector<int> negative = Vector.AndNot(Vector.LessThanOrEqual(maxc, split), positive);
                Vector<int> flat = Vector.AndNot(Vector.AndNot(Vector.Equals(minc, maxc), positive), negative);
                accRight -= positive | flat;
                accLeft -= negative;
            }

            left = Vector.Sum(accLeft);
            right = Vector.Sum(accRight);
        }

        for (; t < count; t++)
        {
            switch (ClassifyAgainstAxisSplit(mins[t], maxs[t], splitValue))
            {
                case PlaneCheckNegative:
                    left++;
                    break;
                case PlaneCheckPositive:
                    right++;
                    break;
            }
        }

        nleft = left;
        nright = right;
        nboth = count - left - right;
    }

    /// <summary>
    /// <c>RayTracingEnvironment::CalculateCostsOfSplit</c>, over a node's
    /// extents on the axis (<see cref="AxisExtents"/>).
    /// </summary>
    /// <remarks>
    /// <paramref name="splitValue"/> is <c>ref</c> because stock's is
    /// <c>float&amp;</c> and this function WRITES it: when one side comes out
    /// empty the split is moved out to the extreme coordinate -- "growing" the
    /// empty node -- and the caller keeps the grown value, not the one it
    /// passed in. A by-value port would build a visibly different tree. The
    /// triangles are classified against the value passed IN, before any
    /// growing, and so is the partition that follows the winning trial.
    /// </remarks>
    private static float CalculateCostsOfSplit(
        ReadOnlySpan<float> mins,
        ReadOnlySpan<float> maxs,
        int axis,
        float minCoord,
        float maxCoord,
        Vec3 minBound,
        Vec3 maxBound,
        ref float splitValue,
        out int nleft,
        out int nright,
        out int nboth)
    {
        CountSides(mins, maxs, splitValue, out nleft, out nright, out nboth);

        // Grow whichever side came out empty.
        if (nleft != 0 && nboth == 0 && nright == 0)
        {
            splitValue = maxCoord;
        }

        if (nright != 0 && nboth == 0 && nleft == 0)
        {
            splitValue = minCoord;
        }

        Vec3 leftMax = WithAxis(maxBound, axis, splitValue);
        Vec3 rightMin = WithAxis(minBound, axis, splitValue);
        float saL = BoxSurfaceArea(minBound, leftMax);
        float saR = BoxSurfaceArea(rightMin, maxBound);

        // The reciprocal is taken in double (1.0/...)
        // and the cost expression is then a float one.
        float isa = (float)(1.0 / BoxSurfaceArea(minBound, maxBound));
        return CostOfTraversal
            + (CostOfIntersection * (nboth + (saL * isa * nleft) + (saR * isa * nright)));
    }

    private static Vec3 WithAxis(Vec3 v, int axis, float value) => axis switch
    {
        0 => new Vec3(value, v.Y, v.Z),
        1 => new Vec3(v.X, value, v.Z),
        _ => new Vec3(v.X, v.Y, value),
    };

    private static float Axis(Vec3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    /// <summary><c>RayTracingEnvironment::RefineNode</c>.</summary>
    private static void RefineNode(
        List<KdNode> nodes,
        List<int> indices,
        KdBuildTriangle[] tris,
        int nodeNumber,
        int[] list,
        int offset,
        int count,
        Vec3 minBound,
        Vec3 maxBound,
        int depth,
        SplitScratch scratch)
    {
        SplitChoice c = ChooseSplit(tris, list, offset, count, minBound, maxBound, depth, scratch);
        if (c.IsLeaf)
        {
            MakeLeaf(nodes, indices, nodeNumber, list, offset, count);
            return;
        }

        int leftChild = nodes.Count;
        KdNode split = nodes[nodeNumber];
        split.Children = c.Plane + (leftChild << 2);
        split.Split = c.Value;
        nodes[nodeNumber] = split;

        nodes.Add(default);
        nodes.Add(default);

        RefineNode(
            nodes, indices, tris, leftChild, c.Partitioned, 0, c.NLeft + c.NBoth,
            minBound, c.LeftMax, c.ChildDepth, scratch);
        RefineNode(
            nodes, indices, tris, leftChild + 1, c.Partitioned, c.NLeft,
            c.NRight + c.NBoth, c.RightMin, maxBound, c.ChildDepth, scratch);
    }

    /// <summary>
    /// Whether a node is a leaf before any trial is tried: fewer than three
    /// triangles, or deeper than <see cref="MaxTreeDepth"/>.
    /// </summary>
    /// <remarks>
    /// Stock tests the depth only AFTER trying every split, and then makes a
    /// leaf whatever the trials said. The trials have no effect but their
    /// result, so testing first builds the same tree without their cost; the
    /// saving is real, because a small list that split entirely to one side
    /// is pushed a hundred levels down precisely so that its children end
    /// here (<see cref="Finalise"/>).
    /// </remarks>
    private static bool IsLeafWithoutTrials(int count, int depth) => count < 3 || depth > MaxTreeDepth;

    /// <summary>
    /// <c>RefineNode</c>'s decision: leaf, or which plane, and the partitioned
    /// list its two children take.
    /// </summary>
    /// <remarks>
    /// Axis by axis: the node's extents on the axis, then every trial on it.
    /// When an axis produces a new best, its extents become the best
    /// extents by a swap of buffers, so the partition classifies against the
    /// winning axis without a further pass over the triangles.
    /// </remarks>
    private static SplitChoice ChooseSplit(
        KdBuildTriangle[] tris,
        int[] list,
        int offset,
        int count,
        Vec3 minBound,
        Vec3 maxBound,
        int depth,
        SplitScratch scratch)
    {
        if (IsLeafWithoutTrials(count, depth))
        {
            return SplitChoice.Leaf;
        }

        scratch.EnsureCapacity(count);
        BestSplit best = BestSplit.None;
        Span<float> splits = stackalloc float[MaxTrialsPerAxis];
        for (int axis = 0; axis < 3; axis++)
        {
            Span<float> mins = scratch.Mins.AsSpan(0, count);
            Span<float> maxs = scratch.Maxs.AsSpan(0, count);
            (float minCoord, float maxCoord) = AxisExtents(tris, list, offset, count, axis, mins, maxs);
            int n = CollectTrialSplits(tris, list, offset, count, minBound, maxBound, axis, splits);
            bool improved = false;
            for (int k = 0; k < n; k++)
            {
                float value = splits[k];
                float cost = CalculateCostsOfSplit(
                    mins, maxs, axis, minCoord, maxCoord, minBound, maxBound,
                    ref value, out int tl, out int tr, out int tb);
                improved |= best.Offer(cost, axis, splits[k], value, tl, tr, tb);
            }

            if (improved)
            {
                scratch.KeepAsBest();
            }
        }

        return Finalise(
            in best, list, offset, count, scratch.BestMins.AsSpan(0, count), scratch.BestMaxs.AsSpan(0, count),
            minBound, maxBound, depth);
    }

    /// <summary>
    /// The winning trial so far: stock's <c>bestCost</c>, <c>splitPlane</c>,
    /// <c>bestSplit</c> and counts, plus the plane the trial was CLASSIFIED
    /// against, which is not the node's split value when the trial grew it.
    /// </summary>
    private struct BestSplit
    {
        public float Cost;
        public int Axis;
        public float Trial;
        public float Value;
        public int NLeft;
        public int NRight;
        public int NBoth;

        /// <summary>Nothing tried yet: stock's 1.0e23 starting cost.</summary>
        public static BestSplit None => new() { Cost = Huge };

        /// <summary>
        /// Takes a trial if it is strictly cheaper, as stock does: on a tie
        /// the earlier trial stays, which is why trials must be offered in
        /// stock's order.
        /// </summary>
        /// <returns>Whether the trial was taken.</returns>
        public bool Offer(float cost, int axis, float trial, float value, int nleft, int nright, int nboth)
        {
            if (!(cost < Cost))
            {
                return false;
            }

            Cost = cost;
            Axis = axis;
            Trial = trial;
            Value = value;
            NLeft = nleft;
            NRight = nright;
            NBoth = nboth;
            return true;
        }
    }

    /// <summary>
    /// The end of stock's <c>RefineNode</c> decision, given the best trial:
    /// leaf when splitting costs no less than not splitting, otherwise the
    /// partition.
    /// </summary>
    /// <param name="best">The winning trial.</param>
    /// <param name="list">The node's triangle list.</param>
    /// <param name="offset">Where the node's run starts in it.</param>
    /// <param name="count">How many triangles the node has.</param>
    /// <param name="mins">The node's extents on the winning axis (lower).</param>
    /// <param name="maxs">The node's extents on the winning axis (upper).</param>
    /// <param name="minBound">The node's box.</param>
    /// <param name="maxBound">The node's box.</param>
    /// <param name="depth">The node's depth.</param>
    /// <returns>The decision.</returns>
    /// <remarks>
    /// Stock partitions by the labels its winning trial saved on the
    /// triangles. Those labels are a pure function of each triangle's extent
    /// and the plane the trial classified against, so they are taken again
    /// here from the same extents and <see cref="BestSplit.Trial"/>, which
    /// is what lets the trials run without a label buffer each.
    /// </remarks>
    private static SplitChoice Finalise(
        in BestSplit best,
        int[] list,
        int offset,
        int count,
        ReadOnlySpan<float> mins,
        ReadOnlySpan<float> maxs,
        Vec3 minBound,
        Vec3 maxBound,
        int depth)
    {
        float costOfNoSplit = CostOfIntersection * count;
        if (costOfNoSplit <= best.Cost || depth > MaxTreeDepth)
        {
            return SplitChoice.Leaf;
        }

        int[] partitioned = new int[count];

        int nLeftOut = 0;
        int nBothOut = 0;
        int nRightOut = 0;
        for (int t = 0; t < count; t++)
        {
            int index = list[offset + t];
            switch (ClassifyAgainstAxisSplit(mins[t], maxs[t], best.Trial))
            {
                case PlaneCheckNegative:
                    partitioned[nLeftOut++] = index;
                    break;
                case PlaneCheckPositive:
                    nRightOut++;
                    partitioned[count - nRightOut] = index;
                    break;
                default:
                    partitioned[best.NLeft + nBothOut] = index;
                    nBothOut++;
                    break;
            }
        }

        // A small list that split entirely to one side is
        // pushed 100 levels down, which is stock's way of saying "stop":
        // the depth test then makes both children leaves.
        if (count < 20 && (best.NLeft == 0 || best.NRight == 0))
        {
            depth += 100;
        }

        return new SplitChoice(
            false,
            best.Axis,
            best.Value,
            partitioned,
            best.NLeft,
            best.NRight,
            best.NBoth,
            WithAxis(maxBound, best.Axis, best.Value),
            WithAxis(minBound, best.Axis, best.Value),
            depth + 1);
    }

    /// <summary>What <see cref="ChooseSplit"/> decided.</summary>
    private readonly record struct SplitChoice(
        bool IsLeaf,
        int Plane,
        float Value,
        int[] Partitioned,
        int NLeft,
        int NRight,
        int NBoth,
        Vec3 LeftMax,
        Vec3 RightMin,
        int ChildDepth)
    {
        public static SplitChoice Leaf => new(true, 0, 0, [], 0, 0, 0, default, default, 0);
    }

    /// <summary>
    /// One serial build's extent buffers: the axis being tried, and the best
    /// axis so far.
    /// </summary>
    /// <remarks>
    /// One per serial build (per worker in a parallel one), reused by every
    /// node of every subtree it builds: <see cref="RefineNode"/> finishes with
    /// a node's buffers before it recurses, so the whole recursion needs one
    /// set, as big as its largest node. Plain arrays rather than pooled ones:
    /// they live exactly as long as the build and go with it.
    /// </remarks>
    private sealed class SplitScratch
    {
        public float[] Mins { get; private set; } = [];

        public float[] Maxs { get; private set; } = [];

        public float[] BestMins { get; private set; } = [];

        public float[] BestMaxs { get; private set; } = [];

        public void EnsureCapacity(int count)
        {
            if (Mins.Length >= count)
            {
                return;
            }

            Mins = new float[count];
            Maxs = new float[count];
            BestMins = new float[count];
            BestMaxs = new float[count];
        }

        /// <summary>Makes the current axis's extents the best ones; the old best become the next axis's buffers.</summary>
        public void KeepAsBest()
        {
            (Mins, BestMins) = (BestMins, Mins);
            (Maxs, BestMaxs) = (BestMaxs, Maxs);
        }
    }

    private static void MakeLeaf(
        List<KdNode> nodes, List<int> indices, int nodeNumber,
        int[] list, int offset, int count)
    {
        KdNode leaf = nodes[nodeNumber];
        leaf.Children = KdNode.Leaf + (indices.Count << 2);
        leaf.TriangleCount = count;
        nodes[nodeNumber] = leaf;

        for (int t = 0; t < count; t++)
        {
            indices.Add(list[offset + t]);
        }
    }

    /// <summary><c>GetEdgeEquation</c>.</summary>
    private static Vec3 GetEdgeEquation(
        Vec3 p1, Vec3 p2, int c1, int c2, Vec3 insidePoint)
    {
        float nx = p1[c2] - p2[c2];
        float ny = p2[c1] - p1[c1];
        float d = -((nx * p1[c1]) + (ny * p1[c2]));

        float trialDist = (insidePoint[c1] * nx) + (insidePoint[c2] * ny) + d;
        if (trialDist < 0)
        {
            nx = -nx;
            ny = -ny;
            d = -d;
            trialDist = -trialDist;
        }

        // The pre-scale that removes the barycentric divide from the ray test:
        // after this the equation reads 1 at the opposite vertex.
        nx /= trialDist;
        ny /= trialDist;
        d /= trialDist;

        return new Vec3(nx, ny, d);
    }

    /// <summary>
    /// <c>CacheOptimizedTriangle::ChangeIntoIntersectionFormat</c>.
    /// </summary>
    /// <remarks>
    /// The plane normal is normalised with stock's reciprocal-square-root
    /// estimate only when <paramref name="stockNormalise"/> says so
    /// (<see cref="Options.StockQuirk.KdTracerReciprocalEstimate"/>);
    /// otherwise with a divide. The normal and the plane distance taken from it
    /// decide every hit distance, and the largest component decides the
    /// projection axes, so an estimate's last bits would make both depend on
    /// the CPU.
    /// </remarks>
    private static KdTriangle ToIntersectionFormat(in KdBuildTriangle src, bool stockNormalise)
    {
        Vec3 p1 = new(src.Get(0, 0), src.Get(0, 1), src.Get(0, 2));
        Vec3 p2 = new(src.Get(1, 0), src.Get(1, 1), src.Get(1, 2));
        Vec3 p3 = new(src.Get(2, 0), src.Get(2, 1), src.Get(2, 2));

        Vec3 e1 = p2 - p1;
        Vec3 e2 = p3 - p1;
        Vec3 cross = Vec3.Cross(e1, e2);
        (Vec3 n, _) = stockNormalise ? cross.NormaliseLikeStock() : cross.Normalise();

        int dropAxis = 0;
        for (int c = 1; c < 3; c++)
        {
            if (MathF.Abs(n[c]) > MathF.Abs(n[dropAxis]))
            {
                dropAxis = c;
            }
        }

        int cs0 = (dropAxis + 1) % 3;
        int cs1 = (dropAxis + 2) % 3;

        Vec3 edge1 = GetEdgeEquation(p1, p2, cs0, cs1, p3);
        Vec3 edge2 = GetEdgeEquation(p2, p3, cs0, cs1, p1);

        return new KdTriangle
        {
            Nx = n.X,
            Ny = n.Y,
            Nz = n.Z,
            D = Vec3.Dot(n, p1),
            Id = src.Id,
            E0 = edge1.X,
            E1 = edge1.Y,
            E2 = edge1.Z,
            E3 = edge2.X,
            E4 = edge2.Y,
            E5 = edge2.Z,
            CoordSelect0 = (byte)cs0,
            CoordSelect1 = (byte)cs1,
            Flags = src.Flags,
            Unused = 0,
        };
    }
}

/// <summary>What a build produces.</summary>
/// <param name="Nodes">The packed tree; node 0 is the root.</param>
/// <param name="Indices">The triangle index list leaves point into.</param>
/// <param name="Triangles">The triangles in intersection format.</param>
/// <param name="Min">The scene's lower bound.</param>
/// <param name="Max">The scene's upper bound.</param>
internal readonly record struct KdBuildResult(
    KdNode[] Nodes, int[] Indices, KdTriangle[] Triangles, Vec3 Min, Vec3 Max);
