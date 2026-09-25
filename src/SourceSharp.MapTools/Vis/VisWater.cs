using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// The two passes vvis runs over the finished PVS: which leaves have to test
/// for a fog volume at render time, and how far each leaf is from water
/// </summary>
/// <remarks>
/// <para>
/// Both are O(leaves x clusters x leaves-in-cluster) and both are strictly
/// serial in stock. They are kept serial here: parallelising them is on
/// the vvis scaling list, and that work comes after these gates are green.
/// </para>
/// <para>
/// They are in vvis rather than vrad because they are consumers of the PVS and
/// nothing else, which is also why they run from the COMPRESSED lump in stock
/// -- it is the only copy it keeps by then. Here they read the rows directly,
/// which is the same bits: the coder is lossless and the padding past the
/// cluster count is never examined.
/// </para>
/// </remarks>
internal static class VisWater
{
    /// <summary>
    /// The distance stock uses to mean "no water anywhere in sight"
    /// (where the comment asks for a define and there is
    /// none).
    /// </summary>
    internal const float NoWater = 65535f;

    /// <summary>
    /// <c>BuildClusterTable</c>: which
    /// leaves belong to each cluster.
    /// </summary>
    /// <param name="leaves">The map's leaves.</param>
    /// <param name="clusterCount">How many clusters the PVS has.</param>
    /// <returns>Leaf indices per cluster, ascending.</returns>
    internal static int[][] BuildClusterTable(VisLeaves leaves, int clusterCount)
    {
        int[] counts = new int[clusterCount];
        for (int leaf = 0; leaf < leaves.Count; leaf++)
        {
            short cluster = leaves.Cluster(leaf);
            if (cluster >= 0 && cluster < clusterCount)
            {
                counts[cluster]++;
            }
        }

        int[][] table = new int[clusterCount][];
        for (int c = 0; c < clusterCount; c++)
        {
            table[c] = new int[counts[c]];
        }

        int[] fill = new int[clusterCount];
        for (int leaf = 0; leaf < leaves.Count; leaf++)
        {
            short cluster = leaves.Cluster(leaf);
            if (cluster >= 0 && cluster < clusterCount)
            {
                table[cluster][fill[cluster]++] = leaf;
            }
        }

        return table;
    }

    /// <summary>
    /// <c>CalcVisibleFogVolumes</c>: mark every leaf that
    /// a water leaf can see, so the renderer knows it has to work out which fog
    /// volume the viewer is in.
    /// </summary>
    /// <param name="leaves">The map's leaves; contents are modified.</param>
    /// <param name="pvs">Every cluster's PVS row, end to end.</param>
    /// <param name="rowBytes">One row's length.</param>
    /// <param name="clusterLeaves">The cluster-to-leaf table.</param>
    /// <param name="minDistanceToWater">
    /// The LEAFMINDISTTOWATER array, reset to
    /// <see cref="NoWater"/> here as stock does.
    /// </param>
    internal static void CalcVisibleFogVolumes(
        VisLeaves leaves,
        byte[] pvs,
        int rowBytes,
        int[][] clusterLeaves,
        ushort[] minDistanceToWater)
    {
        int contentsTestFogVolume = (int)BrushContents.TestFogVolume;
        int contentsSolid = (int)BrushContents.Solid;
        int contentsSlime = (int)BrushContents.Slime;

        for (int i = 0; i < leaves.Count; i++)
        {
            leaves.SetContents(i, leaves.Contents(i) & ~contentsTestFogVolume);
            minDistanceToWater[i] = (ushort)NoWater;
        }

        for (int i = 0; i < leaves.Count; i++)
        {
            int contents = leaves.Contents(i);

            // -- a leaf discovered by an earlier looker is skipped
            // as a LOOKER. Reproduced; it is a real asymmetry and not a
            // shortcut, because such a leaf is by definition not a water leaf.
            if ((contents & contentsTestFogVolume) != 0)
            {
                continue;
            }

            if ((contents & contentsSolid) != 0)
            {
                continue;
            }

            if (leaves.LeafWaterDataId(i) == -1)
            {
                continue;
            }

            if ((contents & contentsSlime) != 0)
            {
                continue;
            }

            int cluster = leaves.Cluster(i);
            if (cluster < 0)
            {
                continue;
            }

            ReadOnlySpan<byte> row = pvs.AsSpan(cluster * rowBytes, rowBytes);

            for (int j = 0; j < clusterLeaves.Length; j++)
            {
                if (j == cluster)
                {
                    continue;
                }

                if ((row[j >> 3] & (1 << (j & 7))) == 0)
                {
                    continue;
                }

                foreach (int clusterLeaf in clusterLeaves[j])
                {
                    int other = leaves.Contents(clusterLeaf);
                    if ((other & contentsSolid) != 0)
                    {
                        continue;
                    }

                    if (leaves.LeafWaterDataId(clusterLeaf) != -1)
                    {
                        continue;
                    }

                    leaves.SetContents(clusterLeaf, other | contentsTestFogVolume);
                }
            }
        }
    }

    /// <summary>
    /// <c>CalcDistanceFromLeavesToWater</c>: for each
    /// leaf, the closest visible water surface.
    /// </summary>
    /// <param name="bsp">The map, for its faces, edges and vertices.</param>
    /// <param name="leaves">The map's leaves.</param>
    /// <param name="pvs">Every cluster's PVS row, end to end.</param>
    /// <param name="rowBytes">One row's length.</param>
    /// <param name="clusterLeaves">The cluster-to-leaf table.</param>
    /// <param name="minDistanceToWater">The array to fill.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    internal static void CalcDistanceFromLeavesToWater(
        BspData bsp,
        VisLeaves leaves,
        byte[] pvs,
        int rowBytes,
        int[][] clusterLeaves,
        ushort[] minDistanceToWater,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);
        ReadOnlySpan<Vec3> vertices = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        ReadOnlySpan<TexInfo> texInfo = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        ReadOnlySpan<ushort> leafFaces = BspStructView.As<ushort>(bsp[BspLump.LeafFaces]);

        int contentsTestFogVolume = (int)BrushContents.TestFogVolume;
        int surfaceWarp = (int)SurfaceFlags.Warp;

        for (int leaf = 0; leaf < leaves.Count; leaf++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // -- a leaf that neither sees a water boundary nor is
            // in water is left at the sentinel without any work.
            if ((leaves.Contents(leaf) & contentsTestFogVolume) == 0 &&
                leaves.LeafWaterDataId(leaf) == -1)
            {
                minDistanceToWater[leaf] = (ushort)NoWater;
                continue;
            }

            int cluster = leaves.Cluster(leaf);
            if (cluster < 0)
            {
                minDistanceToWater[leaf] = (ushort)NoWater;
                continue;
            }

            ReadOnlySpan<byte> row = pvs.AsSpan(cluster * rowBytes, rowBytes);

            leaves.Bounds(leaf, out var mins, out var maxs);
            Vec3 leafMin = new(mins.X, mins.Y, mins.Z);
            Vec3 leafMax = new(maxs.X, maxs.Y, maxs.Z);

            float minDistance = NoWater;

            for (int j = 0; j < clusterLeaves.Length; j++)
            {
                if (j == cluster)
                {
                    continue;
                }

                if ((row[j >> 3] & (1 << (j & 7))) == 0)
                {
                    continue;
                }

                foreach (int clusterLeaf in clusterLeaves[j])
                {
                    if ((leaves.Contents(clusterLeaf) & contentsTestFogVolume) == 0 &&
                        leaves.LeafWaterDataId(clusterLeaf) == -1)
                    {
                        continue;
                    }

                    int first = leaves.FirstLeafFace(clusterLeaf);
                    int howMany = leaves.NumLeafFaces(clusterLeaf);
                    for (int f = 0; f < howMany; f++)
                    {
                        int faceId = leafFaces[first + f];
                        DFace face = faces[faceId];
                        if (face.TexInfo == -1)
                        {
                            continue;
                        }

                        if ((texInfo[face.TexInfo].Flags & surfaceWarp) == 0)
                        {
                            continue;
                        }

                        FaceBounds(face, edges, surfEdges, vertices, out Vec3 faceMin, out Vec3 faceMax);
                        float distance = MinDistanceBetweenBoxes(leafMin, leafMax, faceMin, faceMax);
                        if (distance < minDistance)
                        {
                            minDistance = distance;
                        }
                    }
                }
            }

            // -- a C cast from float to unsigned short, which
            // truncates towards zero.
            minDistanceToWater[leaf] = (ushort)minDistance;
        }
    }

    /// <summary>
    /// <c>GetBoundsForFace</c>.
    /// </summary>
    /// <param name="face">The face.</param>
    /// <param name="edges">LUMP_EDGES.</param>
    /// <param name="surfEdges">LUMP_SURFEDGES.</param>
    /// <param name="vertices">LUMP_VERTEXES.</param>
    /// <param name="min">The minimum corner.</param>
    /// <param name="max">The maximum corner.</param>
    /// <remarks>
    /// Both endpoints of every edge are added, so each vertex is added twice.
    /// Faithful, and free: adding a point to a bounding box is idempotent.
    /// </remarks>
    internal static void FaceBounds(
        DFace face,
        ReadOnlySpan<DEdge> edges,
        ReadOnlySpan<int> surfEdges,
        ReadOnlySpan<Vec3> vertices,
        out Vec3 min,
        out Vec3 max)
    {
        // ClearBounds. Not float.MaxValue: 99999, which
        // is smaller than a Source map's own coordinate limits allow in theory
        // and is what every bounds computation in the tools starts from.
        float minX = 99999f;
        float minY = 99999f;
        float minZ = 99999f;
        float maxX = -99999f;
        float maxY = -99999f;
        float maxZ = -99999f;

        for (int i = face.FirstEdge; i < face.FirstEdge + face.NumEdges; i++)
        {
            int edgeId = surfEdges[i];
            if (edgeId < 0)
            {
                edgeId = -edgeId;
            }

            DEdge edge = edges[edgeId];
            Add(vertices[edge.V[0]]);
            Add(vertices[edge.V[1]]);
        }

        min = new Vec3(minX, minY, minZ);
        max = new Vec3(maxX, maxY, maxZ);

        void Add(Vec3 v)
        {
            if (v.X < minX)
            {
                minX = v.X;
            }

            if (v.X > maxX)
            {
                maxX = v.X;
            }

            if (v.Y < minY)
            {
                minY = v.Y;
            }

            if (v.Y > maxY)
            {
                maxY = v.Y;
            }

            if (v.Z < minZ)
            {
                minZ = v.Z;
            }

            if (v.Z > maxZ)
            {
                maxZ = v.Z;
            }
        }
    }

    /// <summary>
    /// <c>GetMinDistanceBetweenBoundingBoxes</c>.
    /// </summary>
    /// <param name="min1">The first box's minimum.</param>
    /// <param name="max1">The first box's maximum.</param>
    /// <param name="min2">The second box's minimum.</param>
    /// <param name="max2">The second box's maximum.</param>
    /// <returns>Zero when they touch, otherwise the gap between them.</returns>
    internal static float MinDistanceBetweenBoxes(Vec3 min1, Vec3 max1, Vec3 min2, Vec3 max2)
    {
        if (BoxesIntersect(min1, max1, min2, max2))
        {
            return 0f;
        }

        float x = AxisGap(min1.X, max1.X, min2.X, max2.X);
        float y = AxisGap(min1.Y, max1.Y, min2.Y, max2.Y);
        float z = AxisGap(min1.Z, max1.Z, min2.Z, max2.Z);

        return new Vec3(x, y, z).Length();
    }

    private static bool BoxesIntersect(Vec3 min1, Vec3 max1, Vec3 min2, Vec3 max2)
    {
        // IsBoxIntersectingBox.
        if (min1.X > max2.X || max1.X < min2.X)
        {
            return false;
        }

        if (min1.Y > max2.Y || max1.Y < min2.Y)
        {
            return false;
        }

        return !(min1.Z > max2.Z || max1.Z < min2.Z);
    }

    private static float AxisGap(float min1, float max1, float min2, float max2)
    {
        if (min1 <= max2 && max1 >= min2)
        {
            return 0f;
        }

        float a = min1 - max2;
        float b = min2 - max1;
        return a > b ? a : b;
    }
}
