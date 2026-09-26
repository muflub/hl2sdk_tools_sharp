//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// <c>MakeParents</c> (called as <c>MakeParents(0, -1)</c>) and <c>GetLeafBoundaryPlanes</c>.
/// </summary>
public sealed class BspParentsTests
{
    /// <summary>
    /// Node 0 splits on x=0 (plane 0): front = node 1, back = leaf 0.
    /// Node 1 splits on y=16 (plane 1): front = leaf 1, back = leaf 2.
    /// Node 2 is a submodel's head node the world tree never reaches:
    /// front = leaf 3, back = leaf 4.
    /// </summary>
    internal static DNode[] Tree()
    {
        DNode[] nodes = new DNode[3];
        nodes[0].PlaneNum = 0;
        nodes[0].Children[0] = 1;
        nodes[0].Children[1] = -(0 + 1);
        nodes[1].PlaneNum = 1;
        nodes[1].Children[0] = -(1 + 1);
        nodes[1].Children[1] = -(2 + 1);
        nodes[2].PlaneNum = 1;
        nodes[2].Children[0] = -(3 + 1);
        nodes[2].Children[1] = -(4 + 1);
        return nodes;
    }

    internal static DPlane[] Planes()
    {
        DPlane[] planes = new DPlane[2];
        planes[0].Normal = new Vec3(1, 0, 0);
        planes[0].Dist = 0;
        planes[1].Normal = new Vec3(0, 1, 0);
        planes[1].Dist = 16;
        return planes;
    }

    [Fact]
    public void TheRootsParentIsMinusOne()
    {
        // MakeParents(0, -1): the walk up must stop at the root. A zero here
        // made GetLeafBoundaryPlanes loop forever and grow its plane list
        // until the process died (37 GB before the machine's OOM killer).
        BspParents parents = new(Tree(), 5);

        Assert.Equal(-1, parents.NodeParent(0));
    }

    [Fact]
    public void AnInnerNodesParentIsTheNodeAboveIt()
    {
        BspParents parents = new(Tree(), 5);

        Assert.Equal(0, parents.NodeParent(1));
    }

    [Fact]
    public void ALeafsParentIsTheNodeThatNamesIt()
    {
        BspParents parents = new(Tree(), 5);

        Assert.Equal(1, parents.LeafParent(2));
    }

    [Fact]
    public void ANodeTheWorldTreeNeverReachesKeepsParentZero()
    {
        // the reference implementation's arrays are zero-initialised globals and MakeParents only
        // walks from node 0, so a submodel's nodes and leaves keep parent 0.
        BspParents parents = new(Tree(), 5);

        Assert.Equal((0, 0), (parents.NodeParent(2), parents.LeafParent(3)));
    }

    [Fact]
    public void BoundaryPlanesOfADeepLeafAreOnePerAncestor()
    {
        List<LeafPlane> planes = [];
        LeafBoundaryPlanes.Gather(2, Tree(), Planes(), new BspParents(Tree(), 5), planes);

        Assert.Equal(2, planes.Count);
    }

    [Fact]
    public void ABackChildGetsTheFlippedPlane()
    {
        // leaf 2 is node 1's BACK child: -normal, -dist.
        List<LeafPlane> planes = [];
        LeafBoundaryPlanes.Gather(2, Tree(), Planes(), new BspParents(Tree(), 5), planes);

        Assert.Equal(new LeafPlane(new Vec3(0, -1, 0), -16), planes[0]);
    }

    [Fact]
    public void AFrontChildGetsThePlaneAsIs()
    {
        // node 1 is node 0's FRONT child, so leaf 2's second plane is plane 0 unflipped.
        List<LeafPlane> planes = [];
        LeafBoundaryPlanes.Gather(2, Tree(), Planes(), new BspParents(Tree(), 5), planes);

        Assert.Equal(new LeafPlane(new Vec3(1, 0, 0), 0), planes[1]);
    }

    [Fact]
    public void GatheringBoundaryPlanesAllocatesBoundedMemory()
    {
        // The memory bound the 37 GB / 25 GB OOMs lacked: the walk is one plane
        // per ancestor, so gathering for every leaf of this tree must allocate
        // kilobytes. An unterminated walk allocates without bound (the list
        // doubles until OutOfMemory); run in a 4G cap it dies instead of passing.
        DNode[] nodes = Tree();
        DPlane[] planes = Planes();
        BspParents parents = new(nodes, 5);
        List<LeafPlane> into = new(capacity: 8);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int leaf = 0; leaf < 5; leaf++)
        {
            LeafBoundaryPlanes.Gather(leaf, nodes, planes, parents, into);
            Assert.InRange(into.Count, 1, 2);
        }

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
    }

    [Fact]
    public void ABoxQueryWalksTheFrontChildFirstAndCompletely()
    {
        //: a straddling box recurses children[0] before
        // children[1]. Node 0's front is node 1 (front leaf 1, back leaf 2),
        // its back is leaf 0.
        List<int> leaves = [];
        ToolBspTree.EnumerateLeavesInBox(Tree(), Planes(), new Vec3(-100, -100, -100), new Vec3(100, 100, 100), leaves);

        Assert.Equal([1, 2, 0], leaves);
    }

    [Fact]
    public void ABoxOnOneSideDescendsOneChild()
    {
        // Wholly behind x = 0 by more than TEST_EPSILON: only leaf 0.
        List<int> leaves = [];
        ToolBspTree.EnumerateLeavesInBox(Tree(), Planes(), new Vec3(-100, -100, -100), new Vec3(-1, 100, 100), leaves);

        Assert.Equal([0], leaves);
    }

    [Fact]
    public void AnUnreachedLeafWalksFromTheRootAndStops()
    {
        // Leaf 3's parent is the zero default, so stock walks node 0 -- whose
        // children do not name it, so the back-side plane -- then stops at -1.
        List<LeafPlane> planes = [];
        LeafBoundaryPlanes.Gather(3, Tree(), Planes(), new BspParents(Tree(), 5), planes);

        Assert.Equal([new LeafPlane(new Vec3(-1, 0, 0), 0)], planes);
    }
}
