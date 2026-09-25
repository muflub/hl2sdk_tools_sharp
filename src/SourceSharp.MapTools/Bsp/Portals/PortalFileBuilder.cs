using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// Which forced vis cluster a leaf falls in, if any
/// (<c>GetVisCluster</c>, <c>prtfile.cpp:154</c>).
/// </summary>
/// <remarks>
/// A <c>func_viscluster</c> brush entity merges every leaf that overlaps it
/// into one cluster. Answering that needs brush intersection and volume, which
/// belong to the CSG stage, so it arrives here as an interface: the portal file
/// stage owns the NUMBERING and not the geometry.
/// </remarks>
public interface IVisClusterResolver
{
    /// <summary>The index of the forced cluster covering this leaf.</summary>
    /// <param name="leaf">The leaf to place.</param>
    /// <returns>
    /// An index into the map's <c>func_viscluster</c> list, or -1 when the leaf
    /// is not inside one. Stock returns the first match found and warns that
    /// two overlapping volumes will not merge with each other.
    /// </returns>
    int GetVisCluster(IBspNode leaf);
}

/// <summary>
/// The result of numbering a tree's leaves and collecting its vis portals.
/// </summary>
/// <param name="ClusterCount">The number of vis clusters (<c>num_visclusters</c>).</param>
/// <param name="PortalCount">The number of vis portals (<c>num_visportals</c>).</param>
/// <param name="LeafClusters">
/// The cluster of every leaf in tree order, which is the order
/// <c>SaveClusters_r</c> writes them into <c>dleafs</c> starting at index 1.
/// Solid leaves carry -1.
/// </param>
public readonly record struct PortalFileResult(
    int ClusterCount,
    int PortalCount,
    IReadOnlyList<int> LeafClusters);

/// <summary>
/// The <c>.prt</c> file: the portal graph vvis reads
/// (<c>src/utils/vbsp/prtfile.cpp</c>).
/// </summary>
/// <remarks>
/// <para>
/// The first thing this does is throw away every portal the tree already has
/// and build them again (<c>prtfile.cpp:336-339</c>). That is not a tidy-up:
/// the tree has been pruned and merged since <c>MakeTreePortals</c> ran, so the
/// portals in the <c>.prt</c> are a portalisation of the FINAL tree, the one
/// that was just written to the BSP, and not of the tree the entity flood
/// walked.
/// </para>
/// <para>
/// Only portals between two different clusters, that vis can see through, and
/// that are not the head node's box portals, are written — and each is written
/// once, from the leaf on its front side.
/// </para>
/// </remarks>
public sealed class PortalFileBuilder
{
    private readonly TreePortals _portals;
    private readonly WindingArena _arena;
    private readonly List<int> _leafClusters = [];
    private readonly List<IBspNode> _leaves = [];

    /// <summary>Creates the builder over one compile's portaliser.</summary>
    /// <param name="portals">The portaliser, which owns the winding arena.</param>
    /// <param name="windings">That arena.</param>
    public PortalFileBuilder(TreePortals portals, WindingArena windings)
    {
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(windings);
        _portals = portals;
        _arena = windings;
    }

    /// <summary>
    /// The <c>func_viscluster</c> resolver. Left unset, no leaf is forced into
    /// a shared cluster, which is what a map with no <c>func_viscluster</c>
    /// entities does anyway.
    /// </summary>
    public IVisClusterResolver? VisClusters { get; init; }

    /// <summary>
    /// The areas a <c>sky_camera</c> was found in
    /// (<c>g_SkyAreas</c>, <c>vbsp.cpp:339</c>).
    /// </summary>
    public IReadOnlyCollection<int> SkyAreas { get; init; } = [];

    /// <summary>
    /// <c>-skyvis</c> (<c>g_bSkyVis</c>). When false, every leaf in a 3D
    /// skybox area shares ONE cluster, because nothing in there needs to be
    /// vised against anything else.
    /// </summary>
    public bool SkyVis { get; init; }

    /// <summary>The cluster given to every 3D skybox leaf, or -1 if there was none.</summary>
    public int SkyCluster { get; private set; } = -1;

    /// <summary>
    /// Re-portalises the tree, numbers its clusters, and collects the portals
    /// between them (<c>WritePortalFile</c>, <c>prtfile.cpp:323</c>, up to the
    /// point where it writes).
    /// </summary>
    /// <param name="tree">The final tree, as written to the BSP.</param>
    /// <returns>The cluster and portal counts and the per-leaf cluster list.</returns>
    public PortalFile Build(IBspTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        IBspNode headNode = tree.HeadNode;

        _portals.FreeTreePortals(headNode);
        _portals.MakeHeadnodePortals(tree);
        _portals.CreateVisPortals(headNode);

        // set the cluster field in every leaf and count the total number of portals
        _leaves.Clear();
        BuildVisLeafList(headNode);

        int clusterCount = NumberLeafs(_leaves);
        List<List<Portal>> byCluster = new(clusterCount);

        for (int i = 0; i < clusterCount; i++)
        {
            byCluster.Add([]);
        }

        BuildPortalList(byCluster, _leaves);

        _leafClusters.Clear();
        SaveClusters(headNode);

        PortalFile file = new() { ClusterCount = clusterCount };

        foreach (List<Portal> cluster in byCluster)
        {
            foreach (Portal p in cluster)
            {
                file.Portals.Add(ToFilePortal(p));
            }
        }

        LastResult = new PortalFileResult(clusterCount, file.Portals.Count, _leafClusters.AsReadOnly());
        return file;
    }

    /// <summary>The counts and leaf clusters from the most recent <see cref="Build"/>.</summary>
    public PortalFileResult LastResult { get; private set; }

    /// <summary>
    /// Turns one portal into a file record, flipping the leaf order when the
    /// winding disagrees with the plane (<c>prtfile.cpp:66-74</c>).
    /// </summary>
    /// <param name="portal">The portal to write.</param>
    /// <returns>Its file record.</returns>
    /// <remarks>
    /// <para>
    /// Stock's comment: "sometimes planes get turned around when they are very
    /// near the changeover point between different axis. Interpret the plane
    /// the same way vis will, and flip the side orders if needed." The test is
    /// <c>dot &lt; 0.99</c> against the plane recomputed from the winding, and
    /// the threshold being 0.99 rather than 0 means a portal whose winding is
    /// merely a little off its plane is ALSO flipped, not just one that is
    /// genuinely reversed.
    /// </para>
    /// <para>
    /// No catalogue map reaches the flip: mutating the threshold from 0.99 to
    /// 0 leaves all 17 <c>.prt</c> files byte-identical to stock's. It is
    /// covered by a unit test on a hand-made portal instead, which is the only
    /// place it IS covered.
    /// </para>
    /// </remarks>
    public FilePortal ToFilePortal(Portal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);

        Plane fromWinding = _arena.WindingPlane(portal.Winding);
        bool backwards = Vec3.Dot(portal.Plane.Normal, fromWinding.Normal) < 0.99f;

        int cluster0 = backwards ? portal.BackNode!.Cluster : portal.FrontNode!.Cluster;
        int cluster1 = backwards ? portal.FrontNode!.Cluster : portal.BackNode!.Cluster;

        return new FilePortal(cluster0, cluster1, _arena.Points(portal.Winding).ToArray());
    }

    /// <summary>
    /// Collects the non-solid leaves and marks the rest
    /// (<c>BuildVisLeafList_r</c>, <c>prtfile.cpp:175</c>).
    /// </summary>
    /// <param name="node">The root of the subtree.</param>
    public void BuildVisLeafList(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.IsLeaf())
        {
            // decision node
            node.Cluster = -99;
            BuildVisLeafList(node.Front!);
            BuildVisLeafList(node.Back!);
            return;
        }

        if ((node.Contents & PortalContents.Solid) != 0)
        {
            // solid block, viewpoint never inside
            node.Cluster = -1;
            return;
        }

        _leaves.Add(node);
    }

    /// <summary>
    /// Gives each empty leaf a cluster number
    /// (<c>NumberLeafs</c>, <c>prtfile.cpp:196</c>).
    /// </summary>
    /// <param name="leaves">The empty leaves, in tree order.</param>
    /// <returns>How many clusters were allocated.</returns>
    public int NumberLeafs(IReadOnlyList<IBspNode> leaves)
    {
        ArgumentNullException.ThrowIfNull(leaves);

        int clusterCount = 0;
        SkyCluster = -1;
        Dictionary<int, int> forced = [];

        foreach (IBspNode node in leaves)
        {
            int visCluster = VisClusters?.GetVisCluster(node) ?? -1;

            if (visCluster >= 0)
            {
                if (!forced.TryGetValue(visCluster, out int index))
                {
                    index = clusterCount++;
                    forced[visCluster] = index;
                }

                node.Cluster = index;
                continue;
            }

            if (!SkyVis && SkyAreas.Contains(node.Area))
            {
                if (SkyCluster < 0)
                {
                    // allocate a cluster for the sky
                    SkyCluster = clusterCount++;
                }

                node.Cluster = SkyCluster;
                continue;
            }

            node.Cluster = clusterCount++;
        }

        return clusterCount;
    }

    /// <summary>
    /// Collects every portal that joins two different clusters
    /// (<c>BuildPortalList</c>, <c>prtfile.cpp:259</c>).
    /// </summary>
    /// <param name="byCluster">One list per cluster, filled in place.</param>
    /// <param name="leaves">The empty leaves, in tree order.</param>
    /// <returns>The total number of portals collected.</returns>
    /// <remarks>
    /// A portal is written once, from the leaf on its FRONT side, which is what
    /// "only write out from first leaf" means — and it is filed under that
    /// leaf's cluster, so the file's portal order is cluster order and then
    /// leaf order within it.
    /// </remarks>
    public static int BuildPortalList(IReadOnlyList<List<Portal>> byCluster, IReadOnlyList<IBspNode> leaves)
    {
        ArgumentNullException.ThrowIfNull(byCluster);
        ArgumentNullException.ThrowIfNull(leaves);

        int portalCount = 0;

        foreach (IBspNode node in leaves)
        {
            for (Portal? p = node.Portals; p is not null;)
            {
                if (ReferenceEquals(p.FrontNode, node))
                {
                    // only write out from first leaf
                    if (p.FrontNode.Cluster != p.BackNode!.Cluster && PortalContents.VisFlood(p))
                    {
                        portalCount++;
                        byCluster[node.Cluster].Add(p);
                    }

                    p = p.NextFront;
                }
                else
                {
                    p = p.NextBack;
                }
            }
        }

        return portalCount;
    }

    /// <summary>
    /// Records every leaf's cluster in tree order
    /// (<c>SaveClusters_r</c>, <c>prtfile.cpp:307</c>).
    /// </summary>
    /// <param name="node">The root of the subtree.</param>
    public void SaveClusters(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsLeaf())
        {
            _leafClusters.Add(node.Cluster);
            return;
        }

        SaveClusters(node.Front!);
        SaveClusters(node.Back!);
    }
}
