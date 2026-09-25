using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Bsp.Props;

/// <summary>
/// <c>ComputeStaticPropLeaves</c> and <c>ComputeConvexHullLeaves_R</c>
/// (<c>utils/vbsp/staticprop.cpp:357-448</c>): the leaves a placed prop hull
/// touches, in the order stock lists them.
/// </summary>
public static class StaticPropLeaves
{
    /// <summary>The node list's capacity, <c>tempNodeList[1024]</c> (<c>:445</c>).</summary>
    public const int MaxDepth = 1024;

    /// <summary>The leaves, in walk order.</summary>
    /// <param name="tree">The written tree.</param>
    /// <param name="hull">The prop's hull.</param>
    /// <param name="origin">The prop origin.</param>
    /// <param name="angles">The prop angles.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The leaf indices.</returns>
    /// <remarks>
    /// <para>
    /// The box is classified against each node plane by its two corners
    /// (<c>:371-383</c>): wholly behind (<c>&lt;= dist</c>) descends BACK and
    /// records the node, wholly in front (<c>&gt;= dist</c>) descends FRONT and
    /// records <c>-node - 1</c>; a straddle recurses back first, then front.
    /// A box exactly on the plane is "behind".
    /// </para>
    /// <para>
    /// At a leaf, solid leaves are skipped and every other leaf is tested
    /// against the hull with the recorded planes, deepest first
    /// (<c>:318-331</c>): a positive entry keeps <c>n·x &lt;= d</c>, a negative
    /// one the flipped plane.
    /// </para>
    /// </remarks>
    public static async Task<List<ushort>> ComputeAsync(
        BspTreeView tree,
        IStaticPropHull hull,
        Vec3 origin,
        Vec3 angles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(hull);

        (Vec3 mins, Vec3 maxs) = await hull.GetAabbAsync(origin, angles, cancellationToken).ConfigureAwait(false);

        List<ushort> leaves = [];
        int[] nodeList = new int[MaxDepth];
        await WalkAsync(tree, 0, 0, nodeList, mins, maxs, hull, origin, angles, leaves, cancellationToken).ConfigureAwait(false);
        return leaves;
    }

    private static async Task WalkAsync(
        BspTreeView tree,
        int node,
        int depth,
        int[] nodeList,
        Vec3 mins,
        Vec3 maxs,
        IStaticPropHull hull,
        Vec3 origin,
        Vec3 angles,
        List<ushort> leaves,
        CancellationToken cancellationToken)
    {
        while (node >= 0)
        {
            DNode n = tree.Nodes[node];
            DPlane plane = tree.Planes[n.PlaneNum];

            Vec3 cornerMin = new(
                plane.Normal.X >= 0 ? mins.X : maxs.X,
                plane.Normal.Y >= 0 ? mins.Y : maxs.Y,
                plane.Normal.Z >= 0 ? mins.Z : maxs.Z);
            Vec3 cornerMax = new(
                plane.Normal.X >= 0 ? maxs.X : mins.X,
                plane.Normal.Y >= 0 ? maxs.Y : mins.Y,
                plane.Normal.Z >= 0 ? maxs.Z : mins.Z);

            if (depth >= MaxDepth)
            {
                throw new MapCompileException($"static prop leaf walk deeper than {MaxDepth} nodes");
            }

            if (Vec3.Dot(plane.Normal, cornerMax) <= plane.Dist)
            {
                nodeList[depth++] = node;
                node = n.Children[1];
            }
            else if (Vec3.Dot(plane.Normal, cornerMin) >= plane.Dist)
            {
                nodeList[depth++] = -node - 1;
                node = n.Children[0];
            }
            else
            {
                nodeList[depth++] = node;
                await WalkAsync(tree, n.Children[1], depth, nodeList, mins, maxs, hull, origin, angles, leaves, cancellationToken)
                    .ConfigureAwait(false);

                nodeList[depth - 1] = -node - 1;
                await WalkAsync(tree, n.Children[0], depth, nodeList, mins, maxs, hull, origin, angles, leaves, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        int leaf = -node - 1;
        if ((tree.LeafContents[leaf] & BspTreeView.ContentsSolid) != 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        (Vec3 Normal, float Dist)[] planes = new (Vec3, float)[depth];
        int idx = 0;
        for (int i = depth; --i >= 0; ++idx)
        {
            int entry = nodeList[i];
            int sign = entry < 0 ? -1 : 1;
            int index = sign < 0 ? -entry - 1 : entry;
            DPlane p = tree.Planes[tree.Nodes[index].PlaneNum];
            planes[idx] = (new Vec3(sign * p.Normal.X, sign * p.Normal.Y, sign * p.Normal.Z), sign * p.Dist);
        }

        if (await hull.IntersectsAsync(planes, origin, angles, cancellationToken).ConfigureAwait(false))
        {
            leaves.Add((ushort)leaf);
        }
    }
}
