//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Materials;

using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
///: the tree walks, <c>PruneNodes</c> and the release path.
/// </summary>
public class TreeOperationsTests
{
    [Fact]
    public async Task NodeForPointAndPointInLeafAgreeOnEveryCornerOfABuiltTree()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        BspTree tree = BrushBspTree.BrushBsp(
            build,
            CsgFixture.AllBrushes(build),
            new Vec3(-512, -512, -512),
            new Vec3(512, 512, 512));

        foreach (Vec3 point in new[]
        {
            Vec3.Zero,
            new Vec3(-32, -32, -32),
            new Vec3(32, 32, 32),
            new Vec3(-256, 100, 3),
            new Vec3(31.999f, -31.999f, 0f),
        })
        {
            Assert.Same(
                BrushBspTree.PointInLeaf(build, tree.HeadNode!, point),
                TreeOperations.NodeForPoint(build, tree.HeadNode!, point));
        }

        _ = map;
    }

    /// <summary>
    /// A node whose two children are both solid becomes a solid leaf, and the
    /// count is recorded.
    /// </summary>
    [Fact]
    public async Task PruneNodesCollapsesASolidPair()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        BspNode root = Node(build, 4);
        root.Children[0] = SolidLeaf(build);
        root.Children[1] = SolidLeaf(build);

        TreeOperations.PruneNodes(build, root);

        Assert.True(root.IsLeaf);
        Assert.Equal((int)BrushContents.Solid, root.Contents);
        Assert.Equal(1, build.PrunedNodes);
    }

    [Fact]
    public async Task PruneNodesLeavesAMixedPairAlone()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        BspNode root = Node(build, 4);
        root.Children[0] = SolidLeaf(build);
        root.Children[1] = EmptyLeaf(build);

        TreeOperations.PruneNodes(build, root);

        Assert.False(root.IsLeaf);
        Assert.Equal(0, build.PrunedNodes);
    }

    /// <summary>
    /// Bottom up, so a chain collapses all the way in one call: the inner node
    /// becomes solid first and the outer one then sees two solid children.
    /// </summary>
    [Fact]
    public async Task PruneNodesCollapsesAChainInOnePass()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        BspNode inner = Node(build, 6);
        inner.Children[0] = SolidLeaf(build);
        inner.Children[1] = SolidLeaf(build);

        BspNode root = Node(build, 4);
        root.Children[0] = SolidLeaf(build);
        root.Children[1] = inner;

        TreeOperations.PruneNodes(build, root);

        Assert.True(root.IsLeaf);
        Assert.Equal(2, build.PrunedNodes);
    }

    /// <summary>
    /// The collapsed node takes child 1's brush list as-is and then pushes
    /// child 0's brushes onto its head one at a time — so child 0's order is
    /// REVERSED and child 1's is not.
    /// </summary>
    [Fact]
    public async Task PruneNodesReversesTheFrontChildsBrushListAndNotTheBacks()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (16, 16, 16)),
                (UnitMap.Plain, (32, 0, 0), (48, 16, 16)),
                (UnitMap.Plain, (64, 0, 0), (80, 16, 16)),
                (UnitMap.Plain, (96, 0, 0), (112, 16, 16))));

        foreach (MapBrush brush in map.Brushes)
        {
            brush.Contents = (int)BrushContents.Solid;
        }

        // The carved list is [4, 3, 2, 1]; split it into [4, 3] and [2, 1].
        List<BspBrush> brushes = CsgFixture.ToList(CsgFixture.AllBrushes(build));
        brushes[1].Next = null;
        brushes[3].Next = null;

        BspNode front = SolidLeaf(build);
        front.BrushList = brushes[0];
        BspNode back = SolidLeaf(build);
        back.BrushList = brushes[2];

        BspNode root = Node(build, 4);
        root.Children[0] = front;
        root.Children[1] = back;

        TreeOperations.PruneNodes(build, root);

        // Child 1's [2, 1] kept, then child 0's 4 and 3 pushed in that order.
        Assert.Equal([3, 4, 2, 1], CsgFixture.OriginalIds(root.BrushList));
    }

    /// <summary>
    /// <b>The collapsed node's children stay reachable.</b> Stock's comment is
    /// "FIXME: free stuff": nothing detaches them, and every walk stops at the
    /// plane number instead. Reproducing that is what keeps the node ids and
    /// the arena's allocation pattern the same as stock's.
    /// </summary>
    [Fact]
    public async Task PruneNodesLeavesTheCollapsedChildrenAttached()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        BspNode root = Node(build, 4);
        BspNode front = SolidLeaf(build);
        BspNode back = SolidLeaf(build);
        root.Children[0] = front;
        root.Children[1] = back;

        TreeOperations.PruneNodes(build, root);

        Assert.Same(front, root.Children[0]);
        Assert.Same(back, root.Children[1]);
    }

    [Fact]
    public async Task PruneNodesRefusesToCollapseANodeThatHasFaces()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        BspNode root = Node(build, 4);
        root.Children[0] = SolidLeaf(build);
        root.Children[1] = SolidLeaf(build);
        root.Faces = new StubFace();

        Assert.Throws<InvalidOperationException>(() => TreeOperations.PruneNodes(build, root));
    }

    [Fact]
    public async Task FreeingATreeReturnsEveryWindingToTheArena()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        BspTree tree = BrushBspTree.BrushBsp(
            build,
            CsgFixture.AllBrushes(build),
            new Vec3(-512, -512, -512),
            new Vec3(512, 512, 512));

        int before = build.Windings.ActiveWindings;
        TreeOperations.FreeTreeBrushes(build, tree.HeadNode!);

        Assert.True(build.Windings.ActiveWindings < before);
        Assert.Equal(0, build.Nodes);
    }

    [Fact]
    public async Task DescribingATreeGivesOneLinePerNodeAndLeaf()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        BspTree tree = BrushBspTree.BrushBsp(
            build,
            CsgFixture.AllBrushes(build),
            new Vec3(-512, -512, -512),
            new Vec3(512, 512, 512));

        IReadOnlyList<string> lines = TreeOperations.DescribeTree(build, tree.HeadNode!);

        // Six internal nodes and seven leaves.
        Assert.Equal(13, lines.Count);
        Assert.StartsWith("#", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Trim() == "NULL");
    }

    private static BspNode Node(BspBuildContext build, int planeNumber)
    {
        BspNode node = build.AllocNode();
        node.PlaneNumber = planeNumber;
        return node;
    }

    private static BspNode SolidLeaf(BspBuildContext build)
    {
        BspNode node = build.AllocNode();
        node.PlaneNumber = BspNode.Leaf;
        node.Contents = (int)BrushContents.Solid;
        return node;
    }

    private static BspNode EmptyLeaf(BspBuildContext build)
    {
        BspNode node = build.AllocNode();
        node.PlaneNumber = BspNode.Leaf;
        node.Contents = 0;
        return node;
    }

    private sealed class StubFace : IBspFace
    {
        public IBspFace? Next { get; set; }
    }
}
