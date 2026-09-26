//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// <c>g_ClusterLeaves</c> and <c>g_ClusterDispFaces</c>: per vis cluster, its
/// leaves and the displacement faces that have a patch in it.
/// </summary>
/// <remarks>
/// Both are compressed rows -- one index array and a start per cluster --
/// rather than stock's <c>CUtlVector</c> per cluster.
/// </remarks>
public sealed class ClusterTables
{
    private readonly int[] _leafStart;
    private readonly int[] _leaves;
    private readonly int[] _dispStart;
    private readonly int[] _dispFaces;

    private ClusterTables(int[] leafStart, int[] leaves, int[] dispStart, int[] dispFaces)
    {
        _leafStart = leafStart;
        _leaves = leaves;
        _dispStart = dispStart;
        _dispFaces = dispFaces;
    }

    /// <summary>How many clusters.</summary>
    public int ClusterCount => _leafStart.Length - 1;

    /// <summary>One cluster's leaves, in leaf-index order.</summary>
    /// <param name="cluster">The cluster.</param>
    /// <returns>Its leaves.</returns>
    public ReadOnlySpan<int> Leaves(int cluster) =>
        _leaves.AsSpan(_leafStart[cluster], _leafStart[cluster + 1] - _leafStart[cluster]);

    /// <summary>One cluster's displacement faces, in the order they were found.</summary>
    /// <param name="cluster">The cluster.</param>
    /// <returns>Face indices.</returns>
    public ReadOnlySpan<int> DispFaces(int cluster) =>
        _dispFaces.AsSpan(_dispStart[cluster], _dispStart[cluster + 1] - _dispStart[cluster]);

    /// <summary>Builds both tables.</summary>
    /// <param name="geometry">The map.</param>
    /// <param name="patches">The patches, after subdivision.</param>
    /// <param name="clusterCount"><c>dvis-&gt;numclusters</c>.</param>
    /// <returns>The tables.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// <c>BuildClusterTable</c>: every leaf whose
    /// cluster is <c>i</c>, in leaf order.
    /// </para>
    /// <para>
    /// <c>AddDispsToClusterTable</c>: for each
    /// displacement face in face order, walk its patch list and add the face
    /// to the cluster of every patch that has one, once per cluster
    /// (stock's <c>Find</c> before <c>AddToTail</c>). A displacement's leaf
    /// faces are not in <c>dleaffaces</c>, which is why <c>BuildVisRow</c>
    /// needs this second list at all.
    /// </para>
    /// </remarks>
    public static ClusterTables Build(LightGeometry geometry, PatchSet patches, int clusterCount)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(patches);

        int[] leafStart = new int[clusterCount + 1];
        foreach (LeafInfo leaf in geometry.Leaves)
        {
            if (leaf.Cluster >= 0 && leaf.Cluster < clusterCount)
            {
                leafStart[leaf.Cluster + 1]++;
            }
        }

        for (int i = 0; i < clusterCount; i++)
        {
            leafStart[i + 1] += leafStart[i];
        }

        int[] leaves = new int[leafStart[clusterCount]];
        int[] fill = (int[])leafStart.Clone();
        for (int j = 0; j < geometry.Leaves.Length; j++)
        {
            int c = geometry.Leaves[j].Cluster;
            if (c >= 0 && c < clusterCount)
            {
                leaves[fill[c]++] = j;
            }
        }

        // AddDispsToClusterTable, as (cluster, face) pairs in discovery order.
        List<(int Cluster, int Face)> pairs = [];
        HashSet<(int Cluster, int Face)> seen = [];
        for (int face = 0; face < geometry.Faces.Length; face++)
        {
            if (geometry.Faces[face].DispInfo == -1)
            {
                continue;
            }

            for (int p = patches.FacePatches[face]; p != Patch.Invalid; p = patches.At(p).Next)
            {
                int cluster = patches.At(p).ClusterNumber;
                if (cluster == Patch.Invalid || !seen.Add((cluster, face)))
                {
                    continue;
                }

                pairs.Add((cluster, face));
            }
        }

        int[] dispStart = new int[clusterCount + 1];
        foreach ((int cluster, _) in pairs)
        {
            dispStart[cluster + 1]++;
        }

        for (int i = 0; i < clusterCount; i++)
        {
            dispStart[i + 1] += dispStart[i];
        }

        int[] dispFaces = new int[pairs.Count];
        int[] dispFill = (int[])dispStart.Clone();
        foreach ((int cluster, int face) in pairs)
        {
            dispFaces[dispFill[cluster]++] = face;
        }

        return new ClusterTables(leafStart, leaves, dispStart, dispFaces);
    }
}
