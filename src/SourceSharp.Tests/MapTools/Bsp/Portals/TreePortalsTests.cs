using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>
/// Portalisation on a tree small enough to know the right answer for.
/// </summary>
public class TreePortalsTests
{
    [Fact]
    public void AddPortalToNodesPutsThePortalOnBothNodesLists()
    {
        BspNode front = new(0);
        BspNode back = new(1);
        Portal p = new(2);

        TreePortals.AddPortalToNodes(p, front, back);

        Assert.Same(p, front.Portals);
        Assert.Same(p, back.Portals);
        Assert.Same(front, p.FrontNode);
        Assert.Same(back, p.BackNode);
    }

    [Fact]
    public void AddingAPortalTwiceThrows()
    {
        BspNode front = new(0);
        BspNode back = new(1);
        Portal p = new(2);

        TreePortals.AddPortalToNodes(p, front, back);

        Assert.Throws<MapCompileException>(() => TreePortals.AddPortalToNodes(p, front, back));
    }

    [Fact]
    public void RemovingAPortalUnlinksItFromOneSideAndLeavesTheOther()
    {
        BspNode front = new(0);
        BspNode back = new(1);
        Portal p = new(2);
        TreePortals.AddPortalToNodes(p, front, back);

        TreePortals.RemovePortalFromNode(p, front);

        Assert.Null(front.Portals);
        Assert.Null(p.FrontNode);
        Assert.Same(p, back.Portals);
        Assert.Same(back, p.BackNode);
    }

    [Fact]
    public void RemovingTheMiddleOfAThreePortalListRelinksTheOnesEitherSide()
    {
        BspNode node = new(0);
        Portal a = new(1);
        Portal b = new(2);
        Portal c = new(3);

        // Each push goes to the front, so the list ends up c, b, a.
        TreePortals.AddPortalToNodes(a, node, new BspNode(4));
        TreePortals.AddPortalToNodes(b, node, new BspNode(5));
        TreePortals.AddPortalToNodes(c, node, new BspNode(6));

        TreePortals.RemovePortalFromNode(b, node);

        Assert.Same(c, node.Portals);
        Assert.Same(a, c.NextFront);
    }

    [Fact]
    public void RemovingAPortalThatIsNotOnTheListThrows()
    {
        BspNode node = new(0);
        Portal stranger = new(1);

        Assert.Throws<MapCompileException>(() => TreePortals.RemovePortalFromNode(stranger, node));
    }

    [Fact]
    public void TheHeadNodeGetsSixPortalsToTheOutsideLeaf()
    {
        PortalFixture f = PortalFixture.SealedRoom();

        f.Portals.MakeHeadnodePortals(f.Tree);

        Assert.Equal(6, Count(f.Tree.HeadNode.Portals, f.Tree.HeadNode));
        Assert.Equal(6, Count(f.Tree.OutsideNode.Portals, f.Tree.OutsideNode));
    }

    [Fact]
    public void TheBoxIsTheTreeBoundsPaddedBySideSpace()
    {
        PortalFixture f = PortalFixture.SealedRoom();

        f.Portals.MakeHeadnodePortals(f.Tree);
        f.Portals.CalcNodeBounds(f.Tree.HeadNode);

        Assert.Equal(new Vec3(-72f, -72f, -72f), f.Tree.HeadNode.Mins);
        Assert.Equal(new Vec3(72f, 72f, 72f), f.Tree.HeadNode.Maxs);
    }

    [Fact]
    public void EveryBoxPortalLeadsToTheOutsideLeafAndHasNoOnNode()
    {
        PortalFixture f = PortalFixture.SealedRoom();

        f.Portals.MakeHeadnodePortals(f.Tree);

        for (Portal? p = f.Tree.HeadNode.Portals; p is not null; p = p.NextAt(p.SideOf(f.Tree.HeadNode)))
        {
            Assert.Null(p.OnNode);
            Assert.Same(f.Tree.OutsideNode, p.BackNode);
        }
    }

    [Fact]
    public void TheSealedRoomIsBoundedBySixPortals()
    {
        PortalFixture f = PortalFixture.SealedRoom();

        f.Portalise();

        Assert.Equal(6, Count(f.Room.Portals, f.Room));
    }

    [Fact]
    public void TheSealedRoomsBoundsAreTheSixtyFourUnitCube()
    {
        PortalFixture f = PortalFixture.SealedRoom();

        f.Portalise();

        Assert.Equal(new Vec3(-32f, -32f, -32f), f.Room.Mins);
        Assert.Equal(new Vec3(32f, 32f, 32f), f.Room.Maxs);
    }

    [Fact]
    public void EveryPortalOfTheSealedRoomIsASixtyFourUnitSquare()
    {
        PortalFixture f = PortalFixture.SealedRoom();

        f.Portalise();

        for (Portal? p = f.Room.Portals; p is not null; p = p.NextAt(p.SideOf(f.Room)))
        {
            Assert.Equal(4, f.Arena.Points(p.Winding).Length);
            Assert.Equal(64f * 64f, f.Arena.Area(p.Winding), 3);
        }
    }

    [Fact]
    public void PortalisingATreeLeavesNoPortalsOnTheSplittingNodes()
    {
        PortalFixture f = PortalFixture.SealedRoom();

        f.Portalise();

        // SplitNodePortals hands everything down and clears the node.
        Assert.Null(f.Tree.HeadNode.Portals);
    }

    [Fact]
    public void FreeingTheTreesPortalsAlsoEmptiesTheOutsideLeaf()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Portalise();

        f.Portals.FreeTreePortals(f.Tree.HeadNode);

        Assert.Null(f.Room.Portals);
        Assert.Null(f.Tree.OutsideNode.Portals);
    }

    [Fact]
    public void FreeingTheTreesPortalsReturnsEveryWindingToTheArena()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Portalise();

        f.Portals.FreeTreePortals(f.Tree.HeadNode);

        Assert.Equal(0, f.Portals.ActivePortals);
        Assert.Equal(0, f.Arena.ActiveWindings);
    }

    [Fact]
    public void SubtractFromOriginTurnsNegativeZeroIntoPositiveZero()
    {
        // VectorSubtract(vec3_origin, v) is not unary minus: 0 - -0.0 is +0.0
        // and so is 0 - 0.0, where -(0.0) would be -0.0. Axial plane normals
        // are full of zeroes and the sign of them reaches LUMP_PLANES.
        Vec3 negated = TreePortals.SubtractFromOrigin(new Vec3(0f, -0f, 1f));

        Assert.False(float.IsNegative(negated.X));
        Assert.False(float.IsNegative(negated.Y));
    }

    [Fact]
    public void AWindingWithOnlyTwoLongEdgesIsTiny()
    {
        WindingArena arena = new();
        Winding w = FromPoints(arena, [new(0f, 0f, 0f), new(10f, 0f, 0f), new(10f, 0.1f, 0f)]);

        Assert.True(TreePortals.IsTiny(arena, w));
    }

    [Fact]
    public void AWindingWithThreeLongEdgesIsNotTiny()
    {
        WindingArena arena = new();
        Winding w = FromPoints(arena, [new(0f, 0f, 0f), new(10f, 0f, 0f), new(10f, 10f, 0f)]);

        Assert.False(TreePortals.IsTiny(arena, w));
    }

    /// <summary>
    /// <b>Stock side of <see cref="StockQuirk.WindingIsTinyEdgePromotion"/>.</b>
    /// </summary>
    /// <remarks>
    /// <c>EDGE_LENGTH</c> is <c>0.2</c> with no suffix, so
    /// <c>len &gt; EDGE_LENGTH</c> promotes the float length to double and
    /// compares against 0.2000000000000000111 — while the float <c>0.2f</c> is
    /// 0.2000000029802322. A 0.2-unit edge is therefore LONG, and a four-edged
    /// 0.2 square is not tiny.
    /// </remarks>
    [Fact]
    public void AnEdgeOfNominallyTheThresholdCountsAsLongUnderStock()
    {
        WindingArena arena = new() { Compliance = ComplianceOptions.Stock };
        Winding w = ThresholdSquare(arena);

        Assert.False(TreePortals.IsTiny(arena, w));
    }

    /// <summary>
    /// <b>Correct side of the same quirk, and it differs.</b> Comparing in the
    /// float the surrounding types imply makes a 0.2f edge NOT long, so the
    /// same square is tiny.
    /// </summary>
    [Fact]
    public void AnEdgeOfNominallyTheThresholdCountsAsShortUnderCorrect()
    {
        WindingArena arena = new() { Compliance = ComplianceOptions.Correct };
        Winding w = ThresholdSquare(arena);

        Assert.True(TreePortals.IsTiny(arena, w));
    }

    /// <summary>
    /// The default arena takes the Correct side, so a caller that expresses no
    /// opinion gets the right answer rather than stock's.
    /// </summary>
    [Fact]
    public void TheDefaultArenaTakesTheCorrectSideOfTheEdgePromotion()
    {
        WindingArena arena = new();

        Assert.True(TreePortals.IsTiny(arena, ThresholdSquare(arena)));
    }

    /// <summary>
    /// The two spellings of <c>EDGE_LENGTH</c> are the same number in their own
    /// widths, and the double one is the SMALLER of the two as a real — which
    /// is the whole mechanism.
    /// </summary>
    [Fact]
    public void TheTwoEdgeLengthSpellingsDifferOnlyInWidth()
    {
        Assert.Equal(0.2, TreePortals.StockEdgeLength);
        Assert.Equal(0.2f, TreePortals.CorrectEdgeLength);
        Assert.True(TreePortals.CorrectEdgeLength > TreePortals.StockEdgeLength);
    }

    /// <summary>
    /// The OTHER port of <c>WindingIsTiny</c> uses the same pair of constants.
    /// </summary>
    /// <remarks>
    /// The two copies had drifted: <c>TreePortals.IsTiny</c> compared in double
    /// and <c>BrushGeometry.WindingIsTiny</c> in float, so the same winding
    /// could be tiny to one stage and not to the other. This holds them
    /// together.
    /// </remarks>
    [Fact]
    public void BothPortsOfWindingIsTinyShareTheSameThresholds()
    {
        Assert.Equal(TreePortals.StockEdgeLength, BrushGeometry.StockEdgeLength);
        Assert.Equal(TreePortals.CorrectEdgeLength, BrushGeometry.EdgeLength);
    }

    private static Winding ThresholdSquare(WindingArena arena) => FromPoints(
        arena,
        [new(0f, 0f, 0f), new(0.2f, 0f, 0f), new(0.2f, 0.2f, 0f), new(0f, 0.2f, 0f)]);

    [Fact]
    public void AFourEdgedSquareWellUnderTheThresholdIsTiny()
    {
        WindingArena arena = new();
        Winding w = FromPoints(
            arena,
            [new(0f, 0f, 0f), new(0.1f, 0f, 0f), new(0.1f, 0.1f, 0f), new(0f, 0.1f, 0f)]);

        Assert.True(TreePortals.IsTiny(arena, w));
    }

    private static Winding FromPoints(WindingArena arena, Vec3[] points) => arena.Create(points);

    private static int Count(Portal? head, IBspNode node)
    {
        int n = 0;

        for (Portal? p = head; p is not null; p = p.NextAt(p.SideOf(node)))
        {
            n++;
        }

        return n;
    }
}
