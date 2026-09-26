//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// Walking the finished tree, collapsing solid
/// nodes, and releasing it.
/// </summary>
public static class TreeOperations
{
    /// <summary>
    /// The leaf a point falls in, without the axial shortcut:
    /// <c>NodeForPoint</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="node">The subtree root.</param>
    /// <param name="origin">The point.</param>
    /// <returns>The leaf.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="node"/> is null.
    /// </exception>
    /// <remarks>
    /// <b>The same walk as <see cref="BrushBspTree.PointInLeaf"/>, and not the
    /// same function.</b> <c>PointInLeaf</c> takes the axial shortcut
    /// <c>d = point[type] - dist</c> when the plane's stored type allows it;
    /// this one always computes the dot product. For an axial plane the two
    /// differ only in that the dot product adds two exact zero terms, so they
    /// agree bit for bit — but the duplication is stock's, the two are called
    /// from different places, and collapsing them would hide that if either
    /// ever drifts.
    /// </remarks>
    public static BspNode NodeForPoint(BspBuildContext context, BspNode node, Vec3 origin)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        BspNode current = node;

        while (!current.IsLeaf)
        {
            Plane plane = context.Planes[current.PlaneNumber];
            float d = Vec3.Dot(origin, plane.Normal) - plane.Dist;
            current = d >= 0 ? current.Children[0]! : current.Children[1]!;
        }

        return current;
    }

    /// <summary>
    /// Collapses nodes that separate solid from solid: <c>PruneNodes</c>,
    /// </summary>
    /// <param name="context">The build context, whose pruned count is updated.</param>
    /// <param name="node">The subtree root.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A node being collapsed has faces or a brush list. Both are stock
    /// <c>Error()</c> calls and both are assertions about the compiler rather
    /// than reports about the map.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Bottom up: both children are pruned first, so a chain of solid nodes
    /// collapses one level per pass of the recursion rather than needing
    /// several. The test is <c>&amp;</c> against <c>CONTENTS_SOLID</c> on both
    /// children, so a leaf that is solid AND something else still counts as
    /// solid here.
    /// </para>
    /// <para>
    /// <b>The children are not detached.</b> Stock's comment is
    /// "FIXME: free stuff": the node becomes a leaf by
    /// having its plane number overwritten, and its two children stay reachable
    /// through <c>children[]</c> forever. Anything that walks the tree by
    /// checking <c>planenum</c> first — everything does — never sees them
    /// again, and <c>FreeTree_r</c> does not either, so they leak. Detaching
    /// them here would be a fix, and a fix is a difference.
    /// </para>
    /// <para>
    /// The collapsed node inherits both children's brush lists, the SECOND
    /// child's first and then the first child's brushes pushed on in front of
    /// it one at a time — which reverses child 0's list and leaves child 1's in
    /// Order.
    /// </para>
    /// <para>
    /// Skipped entirely under <c>-noprune</c>, which is
    /// the caller's business and not this function's.
    /// </para>
    /// </remarks>
    public static void PruneNodes(BspBuildContext context, BspNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsLeaf)
        {
            return;
        }

        PruneNodes(context, node.Children[0]!);
        PruneNodes(context, node.Children[1]!);

        BspNode child0 = node.Children[0]!;
        BspNode child1 = node.Children[1]!;

        if ((child0.Contents & (int)BrushContents.Solid) == 0
            || (child1.Contents & (int)BrushContents.Solid) == 0)
        {
            return;
        }

        if (node.Faces is not null)
        {
            throw new InvalidOperationException(
                "PruneNodes: node->faces seperating CONTENTS_SOLID");
        }

        if (child0.Faces is not null || child1.Faces is not null)
        {
            throw new InvalidOperationException("PruneNodes: !node->faces with children");
        }

        node.PlaneNumber = BspNode.Leaf;
        node.Contents = (int)BrushContents.Solid;

        if (node.BrushList is not null)
        {
            throw new InvalidOperationException("PruneNodes: node->brushlist");
        }

        node.BrushList = child1.BrushList;

        for (BspBrush? b = child0.BrushList; b is not null;)
        {
            BspBrush? next = b.Next;
            b.Next = node.BrushList;
            node.BrushList = b;
            b = next;
        }

        context.PrunedNodes++;
    }

    /// <summary>
    /// Releases the brushes and volumes a tree holds: the part of
    /// <c>FreeTree_r</c> that Phase 3b owns.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="node">The subtree root.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <para>
    /// Stock's <c>FreeTree_r</c> also frees the node's face chain and, through
    /// <c>FreeTreePortals_r</c> before it, every portal. Those are Phase 3d's
    /// and Phase 3c's types (<see cref="IBspFace"/>,
    /// <see cref="Portals.Portal"/>), so releasing them is theirs; a
    /// <c>FreeTree</c> that covers all three belongs wherever the last of the
    /// three lands. What is here is what this lane allocated: the brush list of
    /// every leaf, the volume brush of every node, and the node counter.
    /// </para>
    /// <para>
    /// It recurses on <c>planenum != PLANENUM_LEAF</c>, like stock, so a node
    /// that <see cref="PruneNodes"/> collapsed is not descended into and the
    /// two children it still points at are not freed. That leak is stock's and
    /// is noted on <see cref="PruneNodes"/>.
    /// </para>
    /// </remarks>
    public static void FreeTreeBrushes(BspBuildContext context, BspNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        if (!node.IsLeaf)
        {
            FreeTreeBrushes(context, node.Children[0]!);
            FreeTreeBrushes(context, node.Children[1]!);
        }

        context.FreeBrushList(node.BrushList);
        node.BrushList = null;

        if (node.Volume is not null)
        {
            context.FreeBrush(node.Volume);
            node.Volume = null;
        }

        context.Nodes--;
    }

    /// <summary>
    /// Walks the tree, leaves first, as <c>PrintTree_r</c> renders it:
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="node">The subtree root.</param>
    /// <param name="depth">The indentation depth to start at.</param>
    /// <returns>One line per node, in stock's walk order.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// Stock prints this straight to the console and it is the only textual
    /// description of a tree anywhere in vbsp. Returning the lines instead of
    /// printing them is what lets a fact compare two trees by shape — which is
    /// what the tree-shape gate does. The walk order and the content of each
    /// line are stock's; the field widths are not, because
    /// <c>%5.2f</c>'s space padding says nothing about a tree.
    /// </remarks>
    public static IReadOnlyList<string> DescribeTree(
        BspBuildContext context,
        BspNode node,
        int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        List<string> lines = [];
        Describe(context, node, depth, lines);
        return lines;
    }

    private static void Describe(
        BspBuildContext context,
        BspNode node,
        int depth,
        List<string> lines)
    {
        string indent = new(' ', depth * 2);

        if (node.IsLeaf)
        {
            if (node.BrushList is null)
            {
                lines.Add(indent + "NULL");
                return;
            }

            System.Text.StringBuilder text = new(indent);
            for (BspBrush? bb = node.BrushList; bb is not null; bb = bb.Next)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"{bb.Original!.BrushNumber} ");
            }

            lines.Add(text.ToString());
            return;
        }

        Plane plane = context.Planes[node.PlaneNumber];
        lines.Add(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{indent}#{node.PlaneNumber} ({plane.Normal.X:F2} {plane.Normal.Y:F2} "
            + $"{plane.Normal.Z:F2}):{plane.Dist:F2}"));

        Describe(context, node.Children[0]!, depth + 1, lines);
        Describe(context, node.Children[1]!, depth + 1, lines);
    }
}
