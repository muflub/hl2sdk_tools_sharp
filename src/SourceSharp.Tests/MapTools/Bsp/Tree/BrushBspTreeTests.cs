using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Materials;

using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// The tree-building half of <c>brushbsp.cpp</c>.
/// </summary>
public class BrushBspTreeTests
{
    [Fact]
    public void AnAxialBoxStraddlingThePlaneIsOnBothSides()
    {
        Plane plane = new(new Vec3(0, 0, 1), 0f);

        int side = BrushBspTree.BoxOnPlaneSide(
            new Vec3(-8, -8, -8), new Vec3(8, 8, 8), plane, PlaneType.Z);

        Assert.Equal(PlaneSideFlags.Both, side);
    }

    /// <summary>
    /// <b>The axial and non-axial paths disagree for a flat box.</b> The axial
    /// test is symmetric around the plane, so a box with zero thickness there
    /// is on NEITHER side and the answer is 0. The general test is
    /// <c>dist1 &gt;= +eps</c> for front and <c>dist2 &lt; +eps</c> for back —
    /// both against <c>+eps</c>, one inclusive and one exclusive — so the same
    /// box comes back BACK. That asymmetry is stock's, and it is why the
    /// plane's STORED type has to be used rather than one recomputed from the
    /// normal: recomputing would move this box between two different answers.
    /// </summary>
    [Fact]
    public void AFlatBoxIsOnNoSideOfAnAxialPlaneAndBehindAGeneralOne()
    {
        Plane plane = new(new Vec3(0, 0, 1), 0f);
        Vec3 mins = new(-8, -8, 0);
        Vec3 maxs = new(8, 8, 0);

        Assert.Equal(0, BrushBspTree.BoxOnPlaneSide(mins, maxs, plane, PlaneType.Z));
        Assert.Equal(
            PlaneSideFlags.Back, BrushBspTree.BoxOnPlaneSide(mins, maxs, plane, PlaneType.AnyZ));
    }

    /// <summary>
    /// The same asymmetry the other way: a box sitting exactly ON the plane by
    /// its minimum face is FRONT on the axial path and BOTH on the general one,
    /// because the general path's back test is exclusive against <c>+eps</c>
    /// and a distance of exactly zero passes it.
    /// </summary>
    [Fact]
    public void ABoxRestingOnThePlaneIsFrontAxiallyAndBothGenerally()
    {
        Plane plane = new(new Vec3(0, 0, 1), 0f);
        Vec3 mins = new(-8, -8, 0);
        Vec3 maxs = new(8, 8, 16);

        Assert.Equal(
            PlaneSideFlags.Front, BrushBspTree.BoxOnPlaneSide(mins, maxs, plane, PlaneType.Z));
        Assert.Equal(
            PlaneSideFlags.Both, BrushBspTree.BoxOnPlaneSide(mins, maxs, plane, PlaneType.AnyZ));
    }

    [Fact]
    public void ABoxWhollyInFrontIsFrontOnly()
    {
        Plane plane = new(new Vec3(0, 0, 1), 0f);

        Assert.Equal(
            PlaneSideFlags.Front,
            BrushBspTree.BoxOnPlaneSide(new Vec3(-8, -8, 4), new Vec3(8, 8, 16), plane, PlaneType.Z));
    }

    [Fact]
    public void ABoxWhollyBehindIsBackOnly()
    {
        Plane plane = new(new Vec3(0, 0, 1), 0f);

        Assert.Equal(
            PlaneSideFlags.Back,
            BrushBspTree.BoxOnPlaneSide(
                new Vec3(-8, -8, -16), new Vec3(8, 8, -4), plane, PlaneType.Z));
    }

    /// <summary>
    /// A brush that HAS the plane reports facing immediately, with no split
    /// count — which is what makes shared faces free in the score.
    /// </summary>
    [Fact]
    public async Task ABrushThatOwnsThePlaneIsFacingAndCountsNoSplits()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        int plane = box.Sides[0].PlaneNumber;
        int epsilon = 0;

        int side = BrushBspTree.TestBrushToPlaneNumber(
            build, box, plane, out int splits, out _, ref epsilon);

        Assert.Equal(PlaneSideFlags.Back | PlaneSideFlags.Facing, side);
        Assert.Equal(0, splits);
        Assert.Equal(0, epsilon);
    }

    [Fact]
    public async Task TheMirrorOfAnOwnedPlaneIsFacingTheOtherWay()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        int plane = box.Sides[0].PlaneNumber ^ 1;
        int epsilon = 0;

        int side = BrushBspTree.TestBrushToPlaneNumber(
            build, box, plane, out _, out _, ref epsilon);

        Assert.Equal(PlaneSideFlags.Front | PlaneSideFlags.Facing, side);
    }

    /// <summary>
    /// A plane through the middle of a box splits four of its six faces; the
    /// two parallel to the plane are not split.
    /// </summary>
    [Fact]
    public async Task APlaneThroughABoxSplitsItsFourSideFaces()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-64, -64, -64), (64, 64, 64))));

        BspBrush? list = CsgFixture.AllBrushes(build);
        int plane = build.Planes.Find(new Vec3(0, 0, 1), 0f);
        int epsilon = 0;

        int side = BrushBspTree.TestBrushToPlaneNumber(
            build, list!, plane, out int splits, out _, ref epsilon);

        Assert.Equal(PlaneSideFlags.Both, side);
        Assert.Equal(4, splits);
    }

    /// <summary>
    /// <c>epsilonbrush</c> is incremented, never assigned, and
    /// <c>SelectSplitSide</c> passes ONE variable to every brush in the list.
    /// So it counts brushes across the whole list, and each is worth -1000.
    /// </summary>
    [Fact]
    public async Task TheEpsilonBrushCounterAccumulatesAcrossCalls()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-64, -64, -64), (64, 64, 64))));

        BspBrush? list = CsgFixture.AllBrushes(build);

        // A plane half a unit inside the top face: the box crosses it, and
        // d_front comes out between 0 and 1.
        int plane = build.Planes.Find(new Vec3(0, 0, 1), 63.5f);
        int epsilon = 0;

        BrushBspTree.TestBrushToPlaneNumber(build, list!, plane, out _, out _, ref epsilon);
        Assert.Equal(1, epsilon);

        BrushBspTree.TestBrushToPlaneNumber(build, list!, plane, out _, out _, ref epsilon);
        Assert.Equal(2, epsilon);
    }

    [Fact]
    public async Task LeafNodeOrsEveryBrushsContents()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (128, 0, 0), (192, 64, 64))));

        map.Brushes[0].Contents = (int)BrushContents.Water;
        map.Brushes[1].Contents = (int)BrushContents.Grate;

        BspNode node = build.AllocNode();
        BrushBspTree.LeafNode(node, CsgFixture.AllBrushes(build));

        Assert.Equal((int)(BrushContents.Water | BrushContents.Grate), node.Contents);
    }

    /// <summary>
    /// <b>A solid brush whose every side is on a node eats everything.</b> The
    /// leaf becomes exactly <c>CONTENTS_SOLID</c> — an assignment, not an OR —
    /// and the loop breaks, so the water brush behind it in the list never
    /// contributes.
    /// </summary>
    [Fact]
    public async Task ASolidBrushWithEverySideOnANodeMakesTheLeafPurelySolid()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (128, 0, 0), (192, 64, 64))));

        map.Brushes[0].Contents = (int)BrushContents.Water;
        map.Brushes[1].Contents = (int)BrushContents.Solid;

        // The list is reversed, so brush 1 (solid) is at the head.
        BspBrush? list = CsgFixture.AllBrushes(build);
        for (int i = 0; i < list!.SideCount; i++)
        {
            list.Sides[i].TexInfo = BspBrushSide.TexInfoNode;
        }

        BspNode node = build.AllocNode();
        BrushBspTree.LeafNode(node, list);

        Assert.Equal((int)BrushContents.Solid, node.Contents);
    }

    [Fact]
    public async Task ALeafWithNoBrushesIsEmpty()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        BspNode node = build.AllocNode();
        BrushBspTree.LeafNode(node, null);

        Assert.Equal(0, node.Contents);
        Assert.True(node.IsLeaf);
        Assert.Null(node.BrushList);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RemoveAreaPortalBrushesDropsTheBrushAtAnyPosition(int position)
    {
        (BspBuildContext build, MapFile map) = await ThreeBrushes();

        for (int i = 0; i < map.BrushCount; i++)
        {
            map.Brushes[i].Contents = (int)BrushContents.Solid;
        }

        // The list is reversed, so map brush (2 - position) is at `position`.
        map.Brushes[2 - position].Contents = (int)BrushContents.AreaPortal;

        BspNode leaf = build.AllocNode();
        BrushBspTree.LeafNode(leaf, CsgFixture.AllBrushes(build));

        BrushBspTree.RemoveAreaPortalBrushes(leaf);

        Assert.Equal(2, BrushCsg.CountBrushList(leaf.BrushList));
        Assert.DoesNotContain(3 - position, CsgFixture.OriginalIds(leaf.BrushList));
    }

    [Fact]
    public async Task RemoveAreaPortalBrushesCanEmptyTheLeaf()
    {
        (BspBuildContext build, MapFile map) = await ThreeBrushes();

        for (int i = 0; i < map.BrushCount; i++)
        {
            map.Brushes[i].Contents = (int)BrushContents.AreaPortal;
        }

        BspNode leaf = build.AllocNode();
        BrushBspTree.LeafNode(leaf, CsgFixture.AllBrushes(build));

        BrushBspTree.RemoveAreaPortalBrushes(leaf);

        Assert.Null(leaf.BrushList);
    }

    /// <summary>
    /// The test is <c>==</c> and not <c>&amp;</c>, so a brush that
    /// <see cref="AreaportalWaterFixup"/> has just given water bits to stays in
    /// the leaf. If it did not, the water it sits in would stop working.
    /// </summary>
    [Fact]
    public async Task AnAreaportalThatAlsoCarriesWaterIsNotRemoved()
    {
        (BspBuildContext build, MapFile map) = await ThreeBrushes();

        map.Brushes[0].Contents = (int)BrushContents.Solid;
        map.Brushes[1].Contents = (int)(BrushContents.AreaPortal | BrushContents.Water);
        map.Brushes[2].Contents = (int)BrushContents.AreaPortal;

        BspNode leaf = build.AllocNode();
        BrushBspTree.LeafNode(leaf, CsgFixture.AllBrushes(build));

        BrushBspTree.RemoveAreaPortalBrushes(leaf);

        Assert.Equal([2, 1], CsgFixture.OriginalIds(leaf.BrushList));
    }

    [Fact]
    public async Task CheckPlaneAgainstParentsWalksTheWholeChain()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        BspNode root = build.AllocNode();
        root.PlaneNumber = 4;
        BspNode middle = build.AllocNode();
        middle.Parent = root;
        middle.PlaneNumber = 6;
        BspNode leaf = build.AllocNode();
        leaf.Parent = middle;

        Assert.False(BrushBspTree.CheckPlaneAgainstParents(4, leaf));
        Assert.False(BrushBspTree.CheckPlaneAgainstParents(6, leaf));
        Assert.True(BrushBspTree.CheckPlaneAgainstParents(8, leaf));
    }

    [Fact]
    public async Task CheckPlaneAgainstVolumeRejectsAPlaneOutsideTheVolume()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        BspNode node = build.AllocNode();
        node.Volume = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));

        Assert.True(BrushBspTree.CheckPlaneAgainstVolume(
            build, build.Planes.Find(new Vec3(0, 0, 1), 0f), node));
        Assert.False(BrushBspTree.CheckPlaneAgainstVolume(
            build, build.Planes.Find(new Vec3(0, 0, 1), 512f), node));
    }

    /// <summary>
    /// A water side scores <c>9999999</c>, an assignment that overrides
    /// everything else, so water always splits first.
    /// </summary>
    [Fact]
    public async Task SelectSplitSideTakesAWaterSideOverAnAxialSolidOne()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Water, (128, 0, 0), (192, 64, 64))));

        BspNode node = build.AllocNode();
        node.Volume = CsgFixture.Box(build, new Vec3(-512, -512, -512), new Vec3(512, 512, 512));

        // The water brush is last in the map, so it is FIRST in the carved list
        // -- but that is not why it wins: the fact below shows the order does
        // not matter.
        Assert.True(BrushBspTree.SelectSplitSide(
            build, CsgFixture.AllBrushes(build), node, out BspBrushSide best));

        Assert.NotEqual(0, best.Contents & (int)BrushContents.Water);
        _ = map;
    }

    [Fact]
    public async Task SelectSplitSideFindsNothingInAnEmptyList()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        BspNode node = build.AllocNode();
        node.Volume = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));

        Assert.False(BrushBspTree.SelectSplitSide(build, null, node, out _));
    }

    /// <summary>
    /// Pass 0 only considers visible sides and pass 1 only invisible ones, and
    /// the first pass that finds anything wins — so an all-invisible list
    /// still gets a splitter, and <c>c_nonvis</c> records that it took two
    /// passes.
    /// </summary>
    [Fact]
    public async Task AnAllInvisibleListIsSplitOnTheSecondPassAndCounted()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-64, -64, -64), (64, 64, 64))));

        foreach (MapBrushSide side in map.BrushSides)
        {
            side.Visible = false;
        }

        BspNode node = build.AllocNode();
        node.Volume = CsgFixture.Box(build, new Vec3(-512, -512, -512), new Vec3(512, 512, 512));

        Assert.True(BrushBspTree.SelectSplitSide(
            build, CsgFixture.AllBrushes(build), node, out _));
        Assert.Equal(1, build.NonVisibleNodes);
    }

    [Fact]
    public async Task SelectSplitSideClearsEveryTestedFlagBeforeItReturns()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (-64, -64, -64), (64, 64, 64)),
                (UnitMap.Plain, (0, 0, 0), (128, 128, 128))));

        BspNode node = build.AllocNode();
        node.Volume = CsgFixture.Box(build, new Vec3(-512, -512, -512), new Vec3(512, 512, 512));

        BspBrush? list = CsgFixture.AllBrushes(build);
        Assert.True(BrushBspTree.SelectSplitSide(build, list, node, out BspBrushSide best));

        foreach (BspBrush brush in CsgFixture.ToList(list))
        {
            for (int i = 0; i < brush.SideCount; i++)
            {
                Assert.False(brush.Sides[i].Tested);
            }
        }

        Assert.False(best.Tested);
    }

    [Fact]
    public async Task SplitBrushListPutsAFacingBrushsSharedSidesOnTheNode()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-64, -64, -64), (64, 64, 64))));

        BspBrush? list = CsgFixture.AllBrushes(build);
        int plane = list!.Sides[0].PlaneNumber & ~1;

        BspNode node = build.AllocNode();
        node.PlaneNumber = plane;
        list.Side = PlaneSideFlags.Facing | PlaneSideFlags.Back;

        BrushBspTree.SplitBrushList(build, list, node, out BspBrush? front, out BspBrush? back);

        Assert.Null(front);
        Assert.NotNull(back);

        int onNode = 0;
        for (int i = 0; i < back!.SideCount; i++)
        {
            if (back.Sides[i].TexInfo == BspBrushSide.TexInfoNode)
            {
                onNode++;
                Assert.Equal(plane, back.Sides[i].PlaneNumber & ~1);
            }
        }

        Assert.Equal(1, onNode);
    }

    /// <summary>
    /// A saved side of 0 — which <see cref="BrushBspTree.BoxOnPlaneSide"/> can
    /// produce for an axial plane a brush lies flat on — puts the brush on
    /// neither list. Stock drops it the same way.
    /// </summary>
    [Fact]
    public async Task SplitBrushListDropsABrushWithNoSavedSide()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-64, -64, -64), (64, 64, 64))));

        BspBrush? list = CsgFixture.AllBrushes(build);
        list!.Side = 0;

        BspNode node = build.AllocNode();
        node.PlaneNumber = build.Planes.Find(new Vec3(0, 0, 1), 0f);

        BrushBspTree.SplitBrushList(build, list, node, out BspBrush? front, out BspBrush? back);

        Assert.Null(front);
        Assert.Null(back);
    }

    /// <summary>
    /// An UNCLIPPED box needs all six of its planes, so the tree is six nodes
    /// and seven leaves. The same box compiled through the block grid needs
    /// only four, because the grid has already put two of its faces on block
    /// boundaries — which is the 4/5 stock's log reports for
    /// <c>l0_unit_cube</c> and what <c>BlockGridTests</c> checks.
    /// </summary>
    [Fact]
    public async Task BuildingATreeOverOneUnclippedBoxUsesAllSixOfItsPlanes()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        BspTree tree = BrushBspTree.BrushBsp(
            build,
            CsgFixture.AllBrushes(build),
            new Vec3(-512, -512, -512),
            new Vec3(512, 512, 512));

        Assert.Equal(1, tree.Statistics.Brushes);
        Assert.Equal(6, tree.Statistics.VisibleFaces);
        Assert.Equal(0, tree.Statistics.NonVisibleFaces);
        Assert.Equal(6, tree.Statistics.VisibleNodes);
        Assert.Equal(0, tree.Statistics.NonVisibleNodes);
        Assert.Equal(7, tree.Statistics.Leaves);
    }

    [Fact]
    public async Task EveryNodeOfABuiltTreeKnowsItsParent()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        BspTree tree = BrushBspTree.BrushBsp(
            build,
            CsgFixture.AllBrushes(build),
            new Vec3(-512, -512, -512),
            new Vec3(512, 512, 512));

        Assert.Null(tree.HeadNode!.Parent);
        Walk(tree.HeadNode);

        static void Walk(BspNode node)
        {
            if (node.IsLeaf)
            {
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                Assert.Same(node, node.Children[i]!.Parent);
                Walk(node.Children[i]!);
            }
        }
    }

    [Fact]
    public async Task EveryNodeOfABuiltTreeUsesTheEvenHalfOfItsPlanePair()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        BspTree tree = BrushBspTree.BrushBsp(
            build,
            CsgFixture.AllBrushes(build),
            new Vec3(-512, -512, -512),
            new Vec3(512, 512, 512));

        Walk(tree.HeadNode!);

        static void Walk(BspNode node)
        {
            if (node.IsLeaf)
            {
                return;
            }

            Assert.Equal(0, node.PlaneNumber & 1);
            Walk(node.Children[0]!);
            Walk(node.Children[1]!);
        }
    }

    /// <summary>
    /// The solid box's own leaf is the one whose contents carry
    /// <c>CONTENTS_SOLID</c>, and a point inside it lands there.
    /// </summary>
    [Fact]
    public async Task PointInLeafFindsTheSolidLeafInsideTheBox()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-32, -32, -32), (32, 32, 32))));

        BspTree tree = BrushBspTree.BrushBsp(
            build,
            CsgFixture.AllBrushes(build),
            new Vec3(-512, -512, -512),
            new Vec3(512, 512, 512));

        BspNode inside = BrushBspTree.PointInLeaf(build, tree.HeadNode!, Vec3.Zero);
        BspNode outside = BrushBspTree.PointInLeaf(build, tree.HeadNode!, new Vec3(256, 256, 256));

        Assert.NotEqual(0, inside.Contents & (int)BrushContents.Solid);
        Assert.Equal(0, outside.Contents);
    }

    private static Task<(BspBuildContext Build, MapFile Map)> ThreeBrushes() =>
        CsgFixture.LoadAsync(CsgFixture.World(
            (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
            (UnitMap.Plain, (128, 0, 0), (192, 64, 64)),
            (UnitMap.Plain, (256, 0, 0), (320, 64, 64))));
}
