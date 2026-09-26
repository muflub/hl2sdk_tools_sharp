//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The fixed tables the neighbour rules are written in terms of.
/// </summary>
/// <remarks>
/// Every value here is arbitrary-but-consistent rather than right or wrong, so
/// these facts pin the CONSISTENCY between tables that must agree, and the
/// closed forms the tables happen to have. A table that drifts alone is what
/// they are for.
/// </remarks>
public sealed class DispTablesTests
{
    [Fact]
    public void LeftAndRightLockXAndTopAndBottomLockY()
    {
        Assert.Equal(0, DispTables.EdgeDims[(int)DispEdge.Left]);
        Assert.Equal(0, DispTables.EdgeDims[(int)DispEdge.Right]);
        Assert.Equal(1, DispTables.EdgeDims[(int)DispEdge.Top]);
        Assert.Equal(1, DispTables.EdgeDims[(int)DispEdge.Bottom]);
    }

    [Fact]
    public void LeftAndBottomSitAtZeroAndTopAndRightAtTheFarEnd()
    {
        Assert.Equal(0, DispTables.EdgeSideLenMul[(int)DispEdge.Left]);
        Assert.Equal(0, DispTables.EdgeSideLenMul[(int)DispEdge.Bottom]);
        Assert.Equal(1, DispTables.EdgeSideLenMul[(int)DispEdge.Top]);
        Assert.Equal(1, DispTables.EdgeSideLenMul[(int)DispEdge.Right]);
    }

    /// <summary>
    /// The orientation table is a rotation by the difference of the two edge
    /// indices, plus a half turn.
    /// </summary>
    /// <remarks>
    /// The closed form is not written into the table on purpose: if the edge
    /// numbering ever moves, the table is the statement and this fact is what
    /// notices they have stopped agreeing.
    /// </remarks>
    [Fact]
    public void TheOrientationTableIsTheEdgeDifferencePlusAHalfTurn()
    {
        for (int edge = 0; edge < 4; edge++)
        {
            for (int nbEdge = 0; nbEdge < 4; nbEdge++)
            {
                Assert.Equal(
                    (NeighborOrientation)((nbEdge - edge + 2) & 3),
                    DispTables.NeighborOrientationFor(edge, nbEdge));
            }
        }
    }

    /// <summary>
    /// Two facing edges with no rotation between them give no rotation.
    /// </summary>
    [Fact]
    public void FacingEdgesGiveNoRotation()
    {
        Assert.Equal(
            NeighborOrientation.Ccw0,
            DispTables.NeighborOrientationFor((int)DispEdge.Right, (int)DispEdge.Left));
        Assert.Equal(
            NeighborOrientation.Ccw0,
            DispTables.NeighborOrientationFor((int)DispEdge.Top, (int)DispEdge.Bottom));
    }

    /// <summary>
    /// The orientation is antisymmetric: what they are to us is the inverse of
    /// what we are to them.
    /// </summary>
    [Fact]
    public void TheOrientationsOfTheTwoSidesInvertEachOther()
    {
        for (int edge = 0; edge < 4; edge++)
        {
            for (int nbEdge = 0; nbEdge < 4; nbEdge++)
            {
                int ours = (int)DispTables.NeighborOrientationFor(edge, nbEdge);
                int theirs = (int)DispTables.NeighborOrientationFor(nbEdge, edge);

                Assert.Equal(0, (ours + theirs) & 3);
            }
        }
    }

    [Fact]
    public void TheSpanFlipSwapsTheTwoHalvesAndLeavesTheWholeAlone()
    {
        Assert.Equal(
            NeighborSpan.CornerToCorner,
            DispTables.SpanFlip(NeighborSpan.CornerToCorner));
        Assert.Equal(
            NeighborSpan.MidpointToCorner,
            DispTables.SpanFlip(NeighborSpan.CornerToMidpoint));
        Assert.Equal(
            NeighborSpan.CornerToMidpoint,
            DispTables.SpanFlip(NeighborSpan.MidpointToCorner));
    }

    [Fact]
    public void OnlyTheRightAndBottomEdgesFlipTheirSpans()
    {
        Assert.False(DispTables.EdgeNeighborFlip[(int)DispEdge.Left]);
        Assert.False(DispTables.EdgeNeighborFlip[(int)DispEdge.Top]);
        Assert.True(DispTables.EdgeNeighborFlip[(int)DispEdge.Right]);
        Assert.True(DispTables.EdgeNeighborFlip[(int)DispEdge.Bottom]);
    }

    /// <summary>
    /// A half-span can only meet a whole one, so only the first row and first
    /// column of the shift table are valid.
    /// </summary>
    [Fact]
    public void OnlyAWholeSpanCanMeetAHalfSpan()
    {
        foreach (NeighborSpan span in Enum.GetValues<NeighborSpan>())
        {
            foreach (NeighborSpan nbSpan in Enum.GetValues<NeighborSpan>())
            {
                bool eitherIsWhole =
                    span == NeighborSpan.CornerToCorner ||
                    nbSpan == NeighborSpan.CornerToCorner;

                Assert.Equal(eitherIsWhole, DispTables.Shift(span, nbSpan).IsValid);
            }
        }
    }

    /// <summary>
    /// Covering half of a neighbour's edge makes it one power finer than it
    /// is; having a neighbour cover half of ours makes it one coarser.
    /// </summary>
    [Fact]
    public void TheShiftIsPlusOneWhenWeCoverHalfOfThem()
    {
        Assert.Equal(
            1,
            DispTables.Shift(NeighborSpan.CornerToMidpoint, NeighborSpan.CornerToCorner)
                .PowerShiftAdd);
        Assert.Equal(
            1,
            DispTables.Shift(NeighborSpan.MidpointToCorner, NeighborSpan.CornerToCorner)
                .PowerShiftAdd);
        Assert.Equal(
            -1,
            DispTables.Shift(NeighborSpan.CornerToCorner, NeighborSpan.CornerToMidpoint)
                .PowerShiftAdd);
        Assert.Equal(
            -1,
            DispTables.Shift(NeighborSpan.CornerToCorner, NeighborSpan.MidpointToCorner)
                .PowerShiftAdd);
        Assert.Equal(
            0,
            DispTables.Shift(NeighborSpan.CornerToCorner, NeighborSpan.CornerToCorner)
                .PowerShiftAdd);
    }

    /// <summary>
    /// A corner vertex reads as the FIRST edge that matches, in left, top,
    /// right, bottom order.
    /// </summary>
    /// <remarks>
    /// The reason <c>TransformIntoNeighbor</c> with an edge of -1 is not
    /// usable on a corner, and the reason
    /// <c>DoesPointHaveAnyNeighbors</c> asks for both of a corner's edges by
    /// name afterwards.
    /// </remarks>
    [Fact]
    public void ACornerReadsAsTheFirstEdgeThatMatches()
    {
        Assert.Equal(
            (int)DispEdge.Left, DispTables.EdgeIndexFromPoint(new VertIndex(0, 0), 2));
        Assert.Equal(
            (int)DispEdge.Left, DispTables.EdgeIndexFromPoint(new VertIndex(0, 4), 2));
        Assert.Equal(
            (int)DispEdge.Top, DispTables.EdgeIndexFromPoint(new VertIndex(4, 4), 2));
        Assert.Equal(
            (int)DispEdge.Right, DispTables.EdgeIndexFromPoint(new VertIndex(4, 0), 2));
    }

    [Fact]
    public void AnInteriorVertexIsOnNoEdgeAndNoCorner()
    {
        Assert.Equal(-1, DispTables.EdgeIndexFromPoint(new VertIndex(2, 2), 2));
        Assert.Equal(-1, DispTables.CornerIndexFromPoint(new VertIndex(2, 2), 2));
    }

    [Fact]
    public void TheFourCornersAreRecognised()
    {
        Assert.Equal(
            (int)DispCorner.LowerLeft, DispTables.CornerIndexFromPoint(new VertIndex(0, 0), 2));
        Assert.Equal(
            (int)DispCorner.UpperLeft, DispTables.CornerIndexFromPoint(new VertIndex(0, 4), 2));
        Assert.Equal(
            (int)DispCorner.UpperRight, DispTables.CornerIndexFromPoint(new VertIndex(4, 4), 2));
        Assert.Equal(
            (int)DispCorner.LowerRight, DispTables.CornerIndexFromPoint(new VertIndex(4, 0), 2));
    }

    [Fact]
    public void EachCornerNamesTheTwoEdgesThatMeetThere()
    {
        Assert.Equal(
            (DispEdge.Bottom, DispEdge.Left),
            DispTables.CornerEdges((int)DispCorner.LowerLeft));
        Assert.Equal(
            (DispEdge.Top, DispEdge.Left),
            DispTables.CornerEdges((int)DispCorner.UpperLeft));
        Assert.Equal(
            (DispEdge.Top, DispEdge.Right),
            DispTables.CornerEdges((int)DispCorner.UpperRight));
        Assert.Equal(
            (DispEdge.Bottom, DispEdge.Right),
            DispTables.CornerEdges((int)DispCorner.LowerRight));
    }

    /// <summary>
    /// Rotating a position four times by ninety degrees returns it.
    /// </summary>
    [Fact]
    public void FourQuarterTurnsOfAPositionIsTheIdentity()
    {
        VertIndex start = new(1, 3);
        VertIndex point = start;

        for (int i = 0; i < 4; i++)
        {
            point = DispTables.RotateVertIndex(NeighborOrientation.Ccw90, 4, point);
        }

        Assert.Equal(start, point);
    }

    /// <summary>
    /// A position rotates about the grid's centre and an increment about the
    /// origin, and the two are NOT the same transform.
    /// </summary>
    /// <remarks>
    /// Using the increment form on a position is how a rotated neighbour's
    /// vertices end up negative on two of the four orientations.
    /// </remarks>
    [Fact]
    public void RotatingAPositionAndAnIncrementAreDifferentTransforms()
    {
        VertIndex v = new(1, 0);

        Assert.Equal(
            new VertIndex(0, 3), DispTables.RotateVertIndex(NeighborOrientation.Ccw90, 4, v));
        Assert.Equal(
            new VertIndex(0, -1), DispTables.RotateVertIncrement(NeighborOrientation.Ccw90, v));
    }

    [Fact]
    public void ACornerIsACornerAtEveryPower()
    {
        foreach (int power in (int[])[2, 3, 4])
        {
            int side = (1 << power) + 1;

            Assert.True(DispTables.IsCorner(new VertIndex(0, 0), side));
            Assert.True(DispTables.IsCorner(new VertIndex(side - 1, side - 1), side));
            Assert.False(DispTables.IsCorner(new VertIndex(1, 0), side));
            Assert.False(DispTables.IsCorner(new VertIndex(0, 1), side));
        }
    }
}
