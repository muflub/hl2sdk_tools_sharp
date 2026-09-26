//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

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
    private const sbyte PlaneCheckPositive = 1;

    /// <summary><c>PLANECHECK_NEGATIVE</c>.</summary>
    private const sbyte PlaneCheckNegative = -1;

    /// <summary><c>PLANECHECK_STRADDLING</c>.</summary>
    private const sbyte PlaneCheckStraddling = 0;

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

    /// <summary>
    /// Builds the tree.
    /// </summary>
    /// <param name="triangles">The scene. Order matters: it is the tree's index order.</param>
    /// <returns>The nodes, the index list, the intersection triangles, and the bounds.</returns>
    /// <exception cref="ArgumentException"><paramref name="triangles"/> is empty.</exception>
    internal static KdBuildResult Build(ReadOnlySpan<TracedTriangle> triangles)
    {
        KdBuildTriangle[] build = Prepare(triangles, out int[] rootList, out Vec3 min, out Vec3 max);

        List<KdNode> nodes = [new KdNode()];
        List<int> indices = [];
        RefineNode(nodes, indices, build, 0, rootList, 0, rootList.Length, min, max, 0);

        return Finish(build, nodes, indices, min, max);
    }

    /// <summary>
    /// The same tree as <see cref="Build"/>, node for node, with its subtrees
    /// built on <paramref name="queue"/>'s workers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three steps. The top of the tree is refined serially, exactly as
    /// <see cref="RefineNode"/> would, down to a frontier of subtrees small
    /// enough to be jobs. Each frontier subtree is then built by the serial
    /// <see cref="RefineNode"/> into its own node and index lists, rooted at
    /// local node 0, in parallel. Finally the pieces are laid out in the order
    /// the serial build would have appended them: a split's two children next,
    /// then all of the left subtree, then all of the right.
    /// </para>
    /// <para>
    /// That order is what makes a relocation enough. A serial subtree rooted at
    /// node <c>g</c> appends its descendants starting wherever the list ends
    /// when <c>g</c> splits, and a local build of the same subtree does the
    /// same starting at 1, so local node <c>k &gt;= 1</c> lands at
    /// <c>base + k - 1</c> and a leaf's index run moves by the length of the
    /// index list before it.
    /// </para>
    /// <para>
    /// The per-triangle scratch the serial build kept ON the triangles
    /// (stock's <c>m_Data.m_GeometryData.m_nTmpData0/1</c>) is per call
    /// instead: a triangle straddling a split sits in both halves, and two
    /// halves built at once would otherwise overwrite each other's labels.
    /// </para>
    /// </remarks>
    /// <param name="triangles">The scene. Order matters: it is the tree's index order.</param>
    /// <param name="queue">The workers.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The same result as <see cref="Build"/>.</returns>
    internal static async Task<KdBuildResult> BuildAsync(
        ReadOnlyMemory<TracedTriangle> triangles, WorkQueue queue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);

        // The serial top runs on a worker too, never on the caller's thread.
        KdBuildTriangle[] build = [];
        Vec3 min = default;
        Vec3 max = default;
        Skeleton root = null!;
        List<FrontierJob> jobs = [];
        await queue.RunAsync(
                1,
                (_, _) =>
                {
                    build = Prepare(triangles.Span, out int[] rootList, out min, out max);

                    // Frontier subtrees of at most this share of the scene: enough
                    // jobs to balance, few enough that the serial top stays small.
                    int jobSize = Math.Max(ParallelMinimumTriangles, rootList.Length / (queue.Degree * 8));
                    root = Expand(build, rootList, 0, rootList.Length, min, max, 0, jobSize, jobs, queue.Degree > 1);
                },
                new WorkQueueOptions { Stage = "kd tree" },
                cancellationToken)
            .ConfigureAwait(false);

        SubTree[] built = await queue.RunAsync(
                jobs.Count,
                (i, _, _) =>
                {
                    FrontierJob job = jobs[i];
                    SubTree sub = new();
                    RefineNode(sub.Nodes, sub.Indices, build, 0, job.List, job.Offset, job.Count, job.Min, job.Max, job.Depth);
                    return sub;
                },
                static _ => (object?)null,
                new WorkQueueOptions { Stage = "kd tree", ItemCost = i => jobs[i].Count },
                cancellationToken)
            .ConfigureAwait(false);

        KdBuildResult result = default;
        await queue.RunAsync(
                1,
                (_, _) =>
                {
                    List<KdNode> nodes = [new KdNode()];
                    List<int> indices = [];
                    Emit(root, 0, nodes, indices, built);
                    result = Finish(build, nodes, indices, min, max);
                },
                new WorkQueueOptions { Stage = "kd tree" },
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>A subtree is only a job when it is at least this big.</summary>
    private const int ParallelMinimumTriangles = 512;

    private static KdBuildTriangle[] Prepare(
        ReadOnlySpan<TracedTriangle> triangles, out int[] rootList, out Vec3 min, out Vec3 max)
    {
        if (triangles.Length == 0)
        {
            throw new ArgumentException(
                "a KD-tree over no triangles has no bounds to start from; stock would read "
                + "1.0e23 back out of CalculateTriangleListBounds and build a tree around it",
                nameof(triangles));
        }

        KdBuildTriangle[] build = new KdBuildTriangle[triangles.Length];
        for (int i = 0; i < triangles.Length; i++)
        {
            ref KdBuildTriangle t = ref build[i];
            t.Id = triangles[i].Id;
            t.Flags = triangles[i].Flags;
            Store(ref t, 0, triangles[i].V0);
            Store(ref t, 1, triangles[i].V1);
            Store(ref t, 2, triangles[i].V2);
        }

        rootList = new int[triangles.Length];
        for (int i = 0; i < rootList.Length; i++)
        {
            rootList[i] = i;
        }

        (min, max) = CalculateTriangleListBounds(build, rootList, 0, rootList.Length);
        return build;
    }

    private static KdBuildResult Finish(
        KdBuildTriangle[] build, List<KdNode> nodes, List<int> indices, Vec3 min, Vec3 max)
    {
        KdTriangle[] intersect = new KdTriangle[build.Length];
        for (int i = 0; i < build.Length; i++)
        {
            intersect[i] = ToIntersectionFormat(build[i]);
        }

        return new KdBuildResult(nodes.ToArray(), indices.ToArray(), intersect, min, max);
    }

    /// <summary>
    /// The serial top of a parallel build: refines while a node is bigger than
    /// a job, and leaves a <see cref="FrontierJob"/> where it stops.
    /// </summary>
    private static Skeleton Expand(
        KdBuildTriangle[] tris, int[] list, int offset, int count, Vec3 minBound, Vec3 maxBound,
        int depth, int jobSize, List<FrontierJob> jobs, bool parallel)
    {
        if (!parallel || count <= jobSize)
        {
            jobs.Add(new FrontierJob(list, offset, count, minBound, maxBound, depth));
            return new Skeleton(Job: jobs.Count - 1);
        }

        SplitChoice c = ChooseSplit(tris, list, offset, count, minBound, maxBound, depth);
        if (c.IsLeaf)
        {
            return new Skeleton(Leaf: (list, offset, count));
        }

        Skeleton left = Expand(
            tris, c.Partitioned, 0, c.NLeft + c.NBoth, minBound, c.LeftMax, c.ChildDepth, jobSize, jobs, parallel);
        Skeleton right = Expand(
            tris, c.Partitioned, c.NLeft, c.NRight + c.NBoth, c.RightMin, maxBound, c.ChildDepth, jobSize, jobs, parallel);
        return new Skeleton(Split: (c.Plane, c.Value, left, right));
    }

    /// <summary>Lays a skeleton node and everything under it out at node <paramref name="at"/>.</summary>
    private static void Emit(Skeleton s, int at, List<KdNode> nodes, List<int> indices, SubTree[] built)
    {
        if (s.Split is { } split)
        {
            int leftChild = nodes.Count;
            nodes[at] = new KdNode { Children = split.Plane + (leftChild << 2), Split = split.Value };
            nodes.Add(default);
            nodes.Add(default);
            Emit(split.Left, leftChild, nodes, indices, built);
            Emit(split.Right, leftChild + 1, nodes, indices, built);
            return;
        }

        if (s.Leaf is { } leaf)
        {
            MakeLeaf(nodes, indices, at, leaf.List, leaf.Offset, leaf.Count);
            return;
        }

        SubTree sub = built[s.Job];
        int nodeBase = nodes.Count - 1;
        int indexBase = indices.Count;
        for (int k = 0; k < sub.Nodes.Count; k++)
        {
            KdNode n = sub.Nodes[k];
            n.Children = (n.Children & 3) == KdNode.Leaf
                ? KdNode.Leaf + (((n.Children >> 2) + indexBase) << 2)
                : (n.Children & 3) + (((n.Children >> 2) + nodeBase) << 2);
            if (k == 0)
            {
                nodes[at] = n;
            }
            else
            {
                nodes.Add(n);
            }
        }

        indices.AddRange(sub.Indices);
    }

    /// <summary>One frontier subtree: its triangles, box and depth.</summary>
    private sealed record FrontierJob(int[] List, int Offset, int Count, Vec3 Min, Vec3 Max, int Depth);

    /// <summary>A frontier subtree built on its own, rooted at local node 0.</summary>
    private sealed class SubTree
    {
        public List<KdNode> Nodes { get; } = [new KdNode()];

        public List<int> Indices { get; } = [];
    }

    /// <summary>The serial top of a parallel build: a split, a leaf, or a job.</summary>
    private sealed record Skeleton(
        (int Plane, float Value, Skeleton Left, Skeleton Right)? Split = null,
        (int[] List, int Offset, int Count)? Leaf = null,
        int Job = -1);

    private static void Store(ref KdBuildTriangle t, int vertex, Vec3 v)
    {
        t.V[(vertex * 3) + 0] = v.X;
        t.V[(vertex * 3) + 1] = v.Y;
        t.V[(vertex * 3) + 2] = v.Z;
    }

    /// <summary>
    /// <c>RayTracingEnvironment::CalculateTriangleListBounds</c>,
    /// </summary>
    private static (Vec3 Min, Vec3 Max) CalculateTriangleListBounds(
        KdBuildTriangle[] tris, int[] list, int offset, int count)
    {
        float minX = Huge, minY = Huge, minZ = Huge;
        float maxX = -Huge, maxY = -Huge, maxZ = -Huge;

        for (int i = 0; i < count; i++)
        {
            ref KdBuildTriangle t = ref tris[list[offset + i]];
            for (int v = 0; v < 3; v++)
            {
                minX = MathF.Min(minX, t.Get(v, 0));
                maxX = MathF.Max(maxX, t.Get(v, 0));
                minY = MathF.Min(minY, t.Get(v, 1));
                maxY = MathF.Max(maxY, t.Get(v, 1));
                minZ = MathF.Min(minZ, t.Get(v, 2));
                maxZ = MathF.Max(maxZ, t.Get(v, 2));
            }
        }

        return (new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
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
    /// <c>CacheOptimizedTriangle::ClassifyAgainstAxisSplit</c>,
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
    private static sbyte ClassifyAgainstAxisSplit(
        in KdBuildTriangle tri, int axis, float splitValue)
    {
        float minc = tri.Get(0, axis);
        float maxc = minc;
        for (int v = 1; v < 3; v++)
        {
            minc = MathF.Min(minc, tri.Get(v, axis));
            maxc = MathF.Max(maxc, tri.Get(v, axis));
        }

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
    /// <c>RayTracingEnvironment::CalculateCostsOfSplit</c>,
    /// </summary>
    /// <remarks>
    /// <paramref name="splitValue"/> is <c>ref</c> because stock's is
    /// <c>float&amp;</c> and this function WRITES it: when one side comes out
    /// empty the split is moved out to the extreme coordinate -- "growing" the
    /// empty node -- and the caller keeps the grown value, not the one it
    /// passed in. A by-value port would build a visibly different tree.
    /// </remarks>
    private static float CalculateCostsOfSplit(
        KdBuildTriangle[] tris,
        int axis,
        int[] list,
        int offset,
        int count,
        Vec3 minBound,
        Vec3 maxBound,
        ref float splitValue,
        Span<sbyte> side,
        out int nleft,
        out int nright,
        out int nboth)
    {
        nleft = 0;
        nright = 0;
        nboth = 0;

        float minCoord = Huge;
        float maxCoord = -Huge;

        for (int t = 0; t < count; t++)
        {
            ref KdBuildTriangle tri = ref tris[list[offset + t]];
            for (int v = 0; v < 3; v++)
            {
                minCoord = MathF.Min(minCoord, tri.Get(v, axis));
                maxCoord = MathF.Max(maxCoord, tri.Get(v, axis));
            }

            sbyte label = ClassifyAgainstAxisSplit(in tri, axis, splitValue);
            side[t] = label;
            switch (label)
            {
                case PlaneCheckNegative:
                    nleft++;
                    break;
                case PlaneCheckPositive:
                    nright++;
                    break;
                default:
                    nboth++;
                    break;
            }
        }

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
        int depth)
    {
        SplitChoice c = ChooseSplit(tris, list, offset, count, minBound, maxBound, depth);
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
            minBound, c.LeftMax, c.ChildDepth);
        RefineNode(
            nodes, indices, tris, leftChild + 1, c.Partitioned, c.NLeft,
            c.NRight + c.NBoth, c.RightMin, maxBound, c.ChildDepth);
    }

    /// <summary>
    /// <c>RefineNode</c>'s decision: leaf, or
    /// which plane, and the partitioned list its two children take.
    /// </summary>
    private static SplitChoice ChooseSplit(
        KdBuildTriangle[] tris,
        int[] list,
        int offset,
        int count,
        Vec3 minBound,
        Vec3 maxBound,
        int depth)
    {
        if (count < 3)
        {
            return SplitChoice.Leaf;
        }

        float bestCost = Huge;
        int bestNleft = 0;
        int bestNright = 0;
        int bestNboth = 0;
        float bestSplit = 0.0f;
        int splitPlane = 0;

        // Stock's m_nTmpData0 / m_nTmpData1, per call rather than on the
        // triangles: the trial's labels, and the best trial's.
        // Swapped rather than copied when a trial becomes the best: the old
        // best's buffer is the next trial's scratch.
        sbyte[] side = ArrayPool<sbyte>.Shared.Rent(count);
        sbyte[] bestSide = ArrayPool<sbyte>.Shared.Rent(count);

        // Strided, so a big list does not cost O(n^2) split
        // trials: one candidate in every 1 + n/10, which is at most eleven
        // triangles' worth of vertices per axis however large the list is.
        int triSkip = 1 + (count / 10);

        for (int axis = 0; axis < 3; axis++)
        {
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

                    float trialCost = CalculateCostsOfSplit(
                        tris, axis, list, offset, count, minBound, maxBound,
                        ref trialSplit, side, out int tl, out int tr, out int tb);

                    if (trialCost < bestCost)
                    {
                        splitPlane = axis;
                        bestCost = trialCost;
                        bestNleft = tl;
                        bestNright = tr;
                        bestNboth = tb;
                        bestSplit = trialSplit;

                        // The classification is saved, so the
                        // partition below uses the BEST split's labelling and
                        // not the last one tried.
                        (side, bestSide) = (bestSide, side);
                    }

                    if (ts == -1)
                    {
                        break;
                    }
                }
            }
        }

        ArrayPool<sbyte>.Shared.Return(side);
        float costOfNoSplit = CostOfIntersection * count;
        if (costOfNoSplit <= bestCost || depth > MaxTreeDepth)
        {
            ArrayPool<sbyte>.Shared.Return(bestSide);
            return SplitChoice.Leaf;
        }

        int[] partitioned = new int[count];

        int nLeftOut = 0;
        int nBothOut = 0;
        int nRightOut = 0;
        for (int t = 0; t < count; t++)
        {
            int index = list[offset + t];
            switch (bestSide[t])
            {
                case PlaneCheckNegative:
                    partitioned[nLeftOut++] = index;
                    break;
                case PlaneCheckPositive:
                    nRightOut++;
                    partitioned[count - nRightOut] = index;
                    break;
                default:
                    partitioned[bestNleft + nBothOut] = index;
                    nBothOut++;
                    break;
            }
        }

        // A small list that split entirely to one side is
        // pushed 100 levels down, which is stock's way of saying "stop":
        // the depth test then makes both children leaves.
        if (count < 20 && (bestNleft == 0 || bestNright == 0))
        {
            depth += 100;
        }

        ArrayPool<sbyte>.Shared.Return(bestSide);
        return new SplitChoice(
            false,
            splitPlane,
            bestSplit,
            partitioned,
            bestNleft,
            bestNright,
            bestNboth,
            WithAxis(maxBound, splitPlane, bestSplit),
            WithAxis(minBound, splitPlane, bestSplit),
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
    /// <c>CacheOptimizedTriangle::ChangeIntoIntersectionFormat</c>,
    /// </summary>
    private static KdTriangle ToIntersectionFormat(in KdBuildTriangle src)
    {
        Vec3 p1 = new(src.Get(0, 0), src.Get(0, 1), src.Get(0, 2));
        Vec3 p2 = new(src.Get(1, 0), src.Get(1, 1), src.Get(1, 2));
        Vec3 p3 = new(src.Get(2, 0), src.Get(2, 1), src.Get(2, 2));

        Vec3 e1 = p2 - p1;
        Vec3 e2 = p3 - p1;
        (Vec3 n, _) = Vec3.Cross(e1, e2).NormaliseLikeStock();

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
