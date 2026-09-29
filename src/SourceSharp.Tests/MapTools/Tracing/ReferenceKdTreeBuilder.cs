//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The KD-tree build as it was before the build learned to take each axis's
/// extents once per node, count sides with vectors and spread the top of the
/// tree over the workers: a straight recursive refinement that classifies
/// every triangle from its three vertices for every trial. A reference for
/// the facts that pin the fast build.
/// </summary>
/// <remarks>
/// <para>
/// WHY A COPY. The fast serial build and the parallel one share their
/// classification, counting and partition code, so comparing the two passes
/// any mistake they share. The committed stock trees
/// (<see cref="StockKdScene"/>) are the real authority but are two scenes of
/// a few thousand triangles; this reference is what lets the facts compare
/// any scene they can generate or load, the golden map's casters included.
/// </para>
/// <para>
/// Written from the rules, not from the fast code: bounds and per-trial
/// extents folded with <see cref="MathF.Min(float, float)"/> and
/// <see cref="MathF.Max(float, float)"/> vertex by vertex in triangle order;
/// a triangle classified from its own extent (positive when its lowest
/// coordinate is at or above the plane, negative when its highest is at or
/// below, positive when flat, straddling otherwise); an empty side grown out
/// to the node's extent, which moves the split value but not the labels; the
/// surface areas as float products doubled in double; the cost in float with
/// the reciprocal taken in double; the strictly-less scan over the midpoint
/// and the vertices of every <c>1 + n/10</c>-th triangle inside the box, axis
/// by axis; a leaf below three triangles, beyond depth 21, or when not
/// splitting costs no more; the partition left run forwards, right run
/// backwards from the end, straddlers after the left run; a small one-sided
/// split pushed a hundred levels down; the children appended as a pair, then
/// the left subtree, then the right.
/// </para>
/// </remarks>
internal static class ReferenceKdTreeBuilder
{
    private const float Huge = 1.0e23f;

    /// <summary>Builds the tree.</summary>
    /// <param name="triangles">The scene.</param>
    /// <returns>The nodes as (children, split bits) and the index list.</returns>
    public static (int[] Children, int[] SplitBits, int[] Indices) Build(ReadOnlySpan<TracedTriangle> triangles)
    {
        float[] v = new float[triangles.Length * 9];
        for (int i = 0; i < triangles.Length; i++)
        {
            Put(v, i, 0, triangles[i].V0);
            Put(v, i, 1, triangles[i].V1);
            Put(v, i, 2, triangles[i].V2);
        }

        int[] root = new int[triangles.Length];
        float[] min = [Huge, Huge, Huge];
        float[] max = [-Huge, -Huge, -Huge];
        for (int i = 0; i < triangles.Length; i++)
        {
            root[i] = i;
            for (int vert = 0; vert < 3; vert++)
            {
                for (int c = 0; c < 3; c++)
                {
                    min[c] = MathF.Min(min[c], v[(i * 9) + (vert * 3) + c]);
                    max[c] = MathF.Max(max[c], v[(i * 9) + (vert * 3) + c]);
                }
            }
        }

        List<(int Children, int SplitBits)> nodes = [(0, 0)];
        List<int> indices = [];
        Refine(v, nodes, indices, 0, root, min, max, 0);
        return (
            [.. nodes.Select(n => n.Children)],
            [.. nodes.Select(n => n.SplitBits)],
            [.. indices]);
    }

    private static void Put(float[] v, int tri, int vertex, Vec3 p)
    {
        v[(tri * 9) + (vertex * 3) + 0] = p.X;
        v[(tri * 9) + (vertex * 3) + 1] = p.Y;
        v[(tri * 9) + (vertex * 3) + 2] = p.Z;
    }

    private static float Coord(float[] v, int tri, int vertex, int axis) => v[(tri * 9) + (vertex * 3) + axis];

    private static float Area(float[] min, float[] max)
    {
        float dx = max[0] - min[0];
        float dy = max[1] - min[1];
        float dz = max[2] - min[2];
        return (float)(2.0 * ((dx * dz) + (dx * dy) + (dy * dz)));
    }

    private static sbyte Classify(float[] v, int tri, int axis, float split)
    {
        float minc = Coord(v, tri, 0, axis);
        float maxc = minc;
        for (int vert = 1; vert < 3; vert++)
        {
            minc = MathF.Min(minc, Coord(v, tri, vert, axis));
            maxc = MathF.Max(maxc, Coord(v, tri, vert, axis));
        }

        if (minc >= split)
        {
            return 1;
        }

        if (maxc <= split)
        {
            return -1;
        }

        return minc == maxc ? (sbyte)1 : (sbyte)0;
    }

    private static float Cost(
        float[] v, int axis, int[] list, float[] min, float[] max, ref float split, sbyte[] side,
        out int nl, out int nr, out int nb)
    {
        nl = nr = nb = 0;
        float minCoord = Huge;
        float maxCoord = -Huge;
        for (int t = 0; t < list.Length; t++)
        {
            for (int vert = 0; vert < 3; vert++)
            {
                minCoord = MathF.Min(minCoord, Coord(v, list[t], vert, axis));
                maxCoord = MathF.Max(maxCoord, Coord(v, list[t], vert, axis));
            }

            side[t] = Classify(v, list[t], axis, split);
            switch (side[t])
            {
                case -1: nl++; break;
                case 1: nr++; break;
                default: nb++; break;
            }
        }

        if (nl != 0 && nb == 0 && nr == 0)
        {
            split = maxCoord;
        }

        if (nr != 0 && nb == 0 && nl == 0)
        {
            split = minCoord;
        }

        float[] leftMax = [.. max];
        leftMax[axis] = split;
        float[] rightMin = [.. min];
        rightMin[axis] = split;
        float isa = (float)(1.0 / Area(min, max));
        return KdTreeBuilder.CostOfTraversal
            + (KdTreeBuilder.CostOfIntersection
                * (nb + (Area(min, leftMax) * isa * nl) + (Area(rightMin, max) * isa * nr)));
    }

    private static void Refine(
        float[] v, List<(int Children, int SplitBits)> nodes, List<int> indices, int node, int[] list,
        float[] min, float[] max, int depth)
    {
        int count = list.Length;
        bool leaf = count < 3;
        float bestCost = Huge;
        int bestAxis = 0;
        float bestSplit = 0;
        int bestL = 0;
        int bestR = 0;
        sbyte[] bestSide = new sbyte[count];
        if (!leaf)
        {
            sbyte[] side = new sbyte[count];
            int skip = 1 + (count / 10);
            for (int axis = 0; axis < 3; axis++)
            {
                for (int ts = -1; ts < count; ts += skip)
                {
                    for (int tv = 0; tv < 3; tv++)
                    {
                        float trial;
                        if (ts == -1)
                        {
                            trial = 0.5f * (min[axis] + max[axis]);
                        }
                        else
                        {
                            trial = Coord(v, list[ts], tv, axis);
                            if (trial > max[axis] || trial < min[axis])
                            {
                                continue;
                            }
                        }

                        float cost = Cost(v, axis, list, min, max, ref trial, side, out int nl, out int nr, out _);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            bestAxis = axis;
                            bestSplit = trial;
                            bestL = nl;
                            bestR = nr;
                            (side, bestSide) = (bestSide, side);
                        }

                        if (ts == -1)
                        {
                            break;
                        }
                    }
                }
            }

            leaf = KdTreeBuilder.CostOfIntersection * count <= bestCost || depth > KdTreeBuilder.MaxTreeDepth;
        }

        if (leaf)
        {
            nodes[node] = (KdNode.Leaf + (indices.Count << 2), count);
            indices.AddRange(list);
            return;
        }

        int[] parted = new int[count];
        int l = 0;
        int b = 0;
        int r = 0;
        for (int t = 0; t < count; t++)
        {
            switch (bestSide[t])
            {
                case -1: parted[l++] = list[t]; break;
                case 1: parted[count - ++r] = list[t]; break;
                default: parted[bestL + b++] = list[t]; break;
            }
        }

        if (count < 20 && (bestL == 0 || bestR == 0))
        {
            depth += 100;
        }

        int left = nodes.Count;
        nodes[node] = (bestAxis + (left << 2), BitConverter.SingleToInt32Bits(bestSplit));
        nodes.Add((0, 0));
        nodes.Add((0, 0));
        float[] leftMax = [.. max];
        leftMax[bestAxis] = bestSplit;
        float[] rightMin = [.. min];
        rightMin[bestAxis] = bestSplit;
        Refine(v, nodes, indices, left, parted[..(count - bestR)], min, leftMax, depth + 1);
        Refine(v, nodes, indices, left + 1, parted[bestL..], rightMin, max, depth + 1);
    }
}
