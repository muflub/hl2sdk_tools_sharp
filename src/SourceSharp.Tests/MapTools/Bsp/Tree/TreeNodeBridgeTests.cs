//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Portals;

using Xunit;

using TreeNode = SourceSharp.MapTools.Bsp.Tree.BspNode;
using TreeTree = SourceSharp.MapTools.Bsp.Tree.BspTree;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// The tree built by <c>BrushBSP</c> IS the tree the portal, face and write
/// stages walk: stock has one <c>node_t</c>, and so does
/// the port now.
/// </summary>
public sealed class TreeNodeBridgeTests
{
    private static (TreeNode Head, TreeNode Front, TreeNode Back) Split()
    {
        TreeNode head = new() { PlaneNumber = 4 };
        TreeNode front = new() { PlaneNumber = TreeNode.Leaf, Parent = head };
        TreeNode back = new() { PlaneNumber = TreeNode.Leaf, Parent = head };
        head.Children[0] = front;
        head.Children[1] = back;
        return (head, front, back);
    }

    [Fact]
    public void FrontIsChildrenZeroByReference()
    {
        (TreeNode head, TreeNode front, _) = Split();
        Assert.Same(front, ((IBspNode)head).Front);
    }

    [Fact]
    public void BackIsChildrenOneByReference()
    {
        (TreeNode head, _, TreeNode back) = Split();
        Assert.Same(back, ((IBspNode)head).Back);
    }

    [Fact]
    public void ParentIsTheTreeParent()
    {
        (TreeNode head, TreeNode front, _) = Split();
        Assert.Same(head, ((IBspNode)front).Parent);
    }

    [Fact]
    public void WritesThroughTheInterfaceLandOnTheNode()
    {
        (_, TreeNode front, _) = Split();
        IBspNode view = front;
        view.Cluster = 7;
        view.Area = 3;
        view.Contents = 1;
        Assert.Equal((7, 3, 1), (front.Cluster, front.Area, front.Contents));
    }

    [Fact]
    public void LeafBrushesAreTheOriginalsInBrushListOrderWithDuplicates()
    {
        MapBrush a = new() { Id = 1 };
        MapBrush b = new() { Id = 2 };
        TreeNode leaf = new() { PlaneNumber = TreeNode.Leaf };
        BspBrush f0 = new(0) { Original = a };
        BspBrush f1 = new(0) { Original = b };
        BspBrush f2 = new(0) { Original = a };
        f0.Next = f1;
        f1.Next = f2;
        leaf.BrushList = f0;

        Assert.Equal([a, b, a], ((IBspNode)leaf).LeafBrushes);
    }

    [Fact]
    public void LeafBrushesSeeAnUnlinkThatBypassesTheSetter()
    {
        // RemoveAreaPortalBrushes_R unlinks through prev->next, so
        // the projection must not be a cache keyed on the list head.
        MapBrush a = new() { Id = 1 };
        MapBrush b = new() { Id = 2 };
        TreeNode leaf = new() { PlaneNumber = TreeNode.Leaf };
        BspBrush f0 = new(0) { Original = a };
        BspBrush f1 = new(0) { Original = b };
        f0.Next = f1;
        leaf.BrushList = f0;
        _ = ((IBspNode)leaf).LeafBrushes;

        f0.Next = null;

        Assert.Equal([a], ((IBspNode)leaf).LeafBrushes);
    }

    [Fact]
    public void TreeHeadNodeIsTheBuiltHead()
    {
        (TreeNode head, _, _) = Split();
        TreeTree tree = new() { HeadNode = head };
        Assert.Same(head, ((IBspTree)tree).HeadNode);
    }

    [Fact]
    public void TreeOutsideNodeIsTheEmbeddedLeaf()
    {
        TreeTree tree = new();
        Assert.Same(tree.OutsideNode, ((IBspTree)tree).OutsideNode);
    }

    [Fact]
    public void TheOutsideNodeIsALeaf()
    {
        // The bridge marks the tree's outside node as a leaf (PLANENUM_LEAF),
        // so Portal_EntityFlood errors on any portal whose far
        // side is not a leaf, which every leaked map's flood crosses.
        IBspTree tree = new TreeTree();
        Assert.True(tree.OutsideNode.IsLeaf());
    }

    [Fact]
    public void AnUnbuiltTreeRefusesToHandOutAHead()
    {
        IBspTree tree = new TreeTree();
        Assert.Throws<InvalidOperationException>(() => tree.HeadNode);
    }
}
