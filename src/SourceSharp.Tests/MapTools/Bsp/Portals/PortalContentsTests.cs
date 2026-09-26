//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>
/// The content tests that decide what a portal lets through.
/// </summary>
public class PortalContentsTests
{
    [Fact]
    public void VisibleContentsTakesTheLOWESTSetBitAndNotTheHighest()
    {
        // "the strongest visible content" is the comment; the code scans up
        // from bit 0, so solid (1) beats water (0x20) and not the other way.
        int both = PortalContents.Solid | (int)BrushContents.Water;

        Assert.Equal(PortalContents.Solid, PortalContents.VisibleContents(both));
    }

    [Fact]
    public void VisibleContentsIncludesBitEightyBecauseTheLoopBoundIsInclusive()
    {
        Assert.Equal(0x80, PortalContents.VisibleContents(0x80));
    }

    [Fact]
    public void VisibleContentsIgnoresEverythingAboveTheLastVisibleBit()
    {
        Assert.Equal(0, PortalContents.VisibleContents(0x100));
    }

    [Fact]
    public void VisibleContentsOfNothingIsZero()
    {
        Assert.Equal(0, PortalContents.VisibleContents(0));
    }

    [Fact]
    public void ClusterContentsOfALeafIsJustItsContents()
    {
        BspNode leaf = new(0) { Contents = PortalContents.Solid };

        Assert.Equal(PortalContents.Solid, PortalContents.ClusterContents(leaf));
    }

    [Fact]
    public void ClusterContentsDropsSolidWhenEitherChildIsNotEntirelySolid()
    {
        BspNode solid = new(0) { Contents = PortalContents.Solid };
        BspNode empty = new(1);
        BspNode node = new(2);
        node.SplitOn(0, solid, empty);

        Assert.Equal(0, PortalContents.ClusterContents(node));
    }

    [Fact]
    public void ClusterContentsKeepsSolidWhenBothChildrenAreSolid()
    {
        BspNode a = new(0) { Contents = PortalContents.Solid };
        BspNode b = new(1) { Contents = PortalContents.Solid };
        BspNode node = new(2);
        node.SplitOn(0, a, b);

        Assert.Equal(PortalContents.Solid, PortalContents.ClusterContents(node));
    }

    [Fact]
    public void ClusterContentsKeepsEveryOtherBitFromBothSides()
    {
        BspNode a = new(0) { Contents = (int)BrushContents.Water };
        BspNode b = new(1) { Contents = (int)BrushContents.Window };
        BspNode node = new(2);
        node.SplitOn(0, a, b);

        Assert.Equal((int)(BrushContents.Water | BrushContents.Window), PortalContents.ClusterContents(node));
    }

    [Fact]
    public void ABoxPortalNeverFloodsVisBecauseItLeadsToTheOutsideLeaf()
    {
        Portal p = new(0) { OnNode = null, FrontNode = new BspNode(1), BackNode = new BspNode(2) };

        Assert.False(PortalContents.VisFlood(p));
    }

    [Fact]
    public void VisFloodsThroughAPortalWithTheSameContentsOnBothSides()
    {
        Portal p = Between(0, 0);

        Assert.True(PortalContents.VisFlood(p));
    }

    [Fact]
    public void VisDoesNotFloodThroughSolid()
    {
        Portal p = Between(0, PortalContents.Solid);

        Assert.False(PortalContents.VisFlood(p));
    }

    [Fact]
    public void VisFloodsThroughASolidSideThatIsAlsoDetailBecauseDetailMasksItAway()
    {
        // c2 is cleared entirely when it carries DETAIL, so the solid test
        // never sees the solid bit.
        Portal p = Between(0, PortalContents.Solid | PortalContents.Detail);

        Assert.True(PortalContents.VisFlood(p));
    }

    [Fact]
    public void EntityFloodRefusesToCrossIntoSolid()
    {
        Assert.False(PortalContents.EntityFlood(Between(0, PortalContents.Solid)));
    }

    [Fact]
    public void EntityFloodCrossesAnAreaportal()
    {
        Assert.True(PortalContents.EntityFlood(Between(0, PortalContents.AreaPortal)));
    }

    [Fact]
    public void AreaLeakFloodDoesNotCrossAnAreaportal()
    {
        Assert.False(PortalContents.AreaLeakFlood(Between(0, PortalContents.AreaPortal)));
    }

    [Fact]
    public void EntityFloodThrowsWhenASideIsNotALeaf()
    {
        BspNode leafA = new(0);
        BspNode leafB = new(1);
        BspNode node = new(2);
        node.SplitOn(0, leafA, leafB);

        Portal p = new(3) { OnNode = node, FrontNode = node, BackNode = leafA };

        Assert.Throws<MapCompileException>(() => PortalContents.EntityFlood(p));
    }

    private static Portal Between(int frontContents, int backContents)
    {
        BspNode onNode = new(100);
        BspNode front = new(101) { Contents = frontContents };
        BspNode back = new(102) { Contents = backContents };

        return new Portal(103) { OnNode = onNode, FrontNode = front, BackNode = back };
    }
}
