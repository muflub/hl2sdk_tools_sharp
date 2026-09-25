using System.Collections.Immutable;

using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// How a span maps onto a neighbour's span: <c>CShiftInfo</c>,
/// <c>disp_common.h:134</c>.
/// </summary>
/// <param name="MidPointScale">
/// <c>m_MidPointScale</c>. Stock declares and fills it and then never reads it
/// — no translation unit in the tree mentions it outside
/// <c>g_ShiftInfos</c>'s initialiser — so it is carried here for fidelity to
/// the table and nothing consumes it.
/// </param>
/// <param name="PowerShiftAdd">
/// How much to add to the neighbour's power to get its effective power across
/// this pair of spans: +1 when we cover half of it, -1 when it covers half of
/// us.
/// </param>
/// <param name="IsValid">
/// Whether the pair of spans can occur at all. A half-edge cannot abut another
/// half-edge, because the finder only ever pairs a half-span against a whole
/// one.
/// </param>
public readonly record struct ShiftInfo(int MidPointScale, int PowerShiftAdd, bool IsValid);

/// <summary>
/// The fixed tables the displacement neighbour rules are written in terms of.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is <b>arbitrary but consistent</b> in the sense
/// <see cref="Options.StockQuirk"/> means: the numbering of edges and corners,
/// which dimension each edge locks, and the rotation map are conventions, not
/// choices that can be right or wrong. They are reproduced unconditionally and
/// none of them is a compliance switch.
/// </para>
/// <para>
/// They are gathered in one file because they are read from four others, and
/// because their MUTUAL consistency is the thing that makes the neighbour
/// system work — <see cref="EdgeDims"/> and <see cref="EdgeSideLenMul"/> in
/// particular are two halves of one statement about the edge numbering, and
/// changing either alone silently rotates every displacement's neighbour data.
/// </para>
/// </remarks>
public static class DispTables
{
    /// <summary>
    /// Which dimension each edge locks: <c>g_EdgeDims</c>,
    /// <c>disp_common.cpp:57</c>.
    /// </summary>
    /// <remarks>
    /// Indexed by <see cref="DispEdge"/>. Left and right lock x (0), top and
    /// bottom lock y (1). The OTHER dimension is the one an edge walk varies,
    /// which stock spells <c>iFreeDim = !iEdgeDim</c>.
    /// </remarks>
    public static ReadOnlySpan<int> EdgeDims => [0, 1, 0, 1];

    /// <summary>
    /// Which end of the locked dimension each edge sits at:
    /// <c>g_EdgeSideLenMul</c>, <c>disp_common.cpp:86</c>.
    /// </summary>
    /// <remarks>
    /// Multiplied by <c>sideLength - 1</c>. Left and bottom are at 0; top and
    /// right are at the far end.
    /// </remarks>
    public static ReadOnlySpan<int> EdgeSideLenMul => [0, 1, 1, 0];

    /// <summary>
    /// The two edges meeting at each corner: <c>g_CornerEdges</c>,
    /// <c>disp_common.cpp:49</c>.
    /// </summary>
    /// <param name="corner">A <see cref="DispCorner"/>.</param>
    /// <returns>Two <see cref="DispEdge"/> values.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="corner"/> is not 0..3.
    /// </exception>
    public static (DispEdge First, DispEdge Second) CornerEdges(int corner) => corner switch
    {
        0 => (DispEdge.Bottom, DispEdge.Left),
        1 => (DispEdge.Top, DispEdge.Left),
        2 => (DispEdge.Top, DispEdge.Right),
        3 => (DispEdge.Bottom, DispEdge.Right),
        _ => throw new ArgumentOutOfRangeException(
            nameof(corner), corner, "a displacement has four corners."),
    };

    /// <summary>
    /// Whether an edge's sense of corner-to-midpoint is reversed:
    /// <c>g_bEdgeNeighborFlip</c>, <c>disp_common.cpp:745</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True for right and bottom. <c>CORNER_TO_MIDPOINT</c> is defined in the
    /// direction the index increases, but <c>SetupEdgeNeighbors</c> walks the
    /// base surface's four points in winding order 0-1, 1-2, 2-3, 3-0, which on
    /// the right and bottom edges runs down and left. This table is what undoes
    /// that, in <c>NeighborSpanFlip</c>.
    /// </para>
    /// <para>
    /// It is also used AS AN INTEGER for a sub-neighbour slot
    /// (<c>disp_common.cpp:943</c>: <c>AddNeighbor( ..., g_bEdgeNeighborFlip[iEdge], ... )</c>),
    /// so the false/true here means slot 0/slot 1 there. That double duty is
    /// deliberate in stock and is reproduced by
    /// <see cref="EdgeNeighborFlipSlot"/>.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<bool> EdgeNeighborFlip => [false, false, true, true];

    /// <summary>
    /// The same table read as a sub-neighbour slot index.
    /// </summary>
    /// <param name="edge">A <see cref="DispEdge"/>.</param>
    /// <returns>0 or 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="edge"/> is not 0..3.
    /// </exception>
    public static int EdgeNeighborFlipSlot(int edge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(edge, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(edge, 3);
        return EdgeNeighborFlip[edge] ? 1 : 0;
    }

    /// <summary>
    /// <c>g_SpanFlip</c>, <c>disp_common.cpp:744</c>: swaps the two half-spans
    /// and leaves the whole one alone.
    /// </summary>
    /// <param name="span">The span to flip.</param>
    /// <returns>The flipped span.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="span"/> is not a <see cref="NeighborSpan"/>.
    /// </exception>
    public static NeighborSpan SpanFlip(NeighborSpan span) => span switch
    {
        NeighborSpan.CornerToCorner => NeighborSpan.CornerToCorner,
        NeighborSpan.CornerToMidpoint => NeighborSpan.MidpointToCorner,
        NeighborSpan.MidpointToCorner => NeighborSpan.CornerToMidpoint,
        _ => throw new ArgumentOutOfRangeException(
            nameof(span), span, "there are three neighbour spans."),
    };

    /// <summary>
    /// <c>NeighborSpanFlip</c>, <c>disp_common.cpp:840</c>: flip the span only
    /// on the edges whose winding order runs backwards.
    /// </summary>
    /// <param name="edge">The edge the span is on.</param>
    /// <param name="span">The span.</param>
    /// <returns>The span in the canonical sense.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="edge"/> is not 0..3.
    /// </exception>
    public static NeighborSpan NeighborSpanFlip(int edge, NeighborSpan span)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(edge, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(edge, 3);
        return EdgeNeighborFlip[edge] ? SpanFlip(span) : span;
    }

    /// <summary>
    /// How a span on one side maps onto the span it meets:
    /// <c>g_ShiftInfos</c>, <c>disp_common.cpp:65</c>.
    /// </summary>
    /// <param name="span">Our span on our edge.</param>
    /// <param name="neighborSpan">Our span on the NEIGHBOUR's edge.</param>
    /// <returns>The shift.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either span is not a <see cref="NeighborSpan"/>.
    /// </exception>
    /// <remarks>
    /// Only the first row and first column are valid, because a half-edge
    /// always meets a whole edge: two displacements cannot each cover half of
    /// the other. Stock asserts that and, in a release build, reads
    /// <c>{0, 0}</c> — which is a valid-looking no-shift rather than a
    /// failure, and is why the invalid entries are carried here rather than
    /// left out.
    /// </remarks>
    public static ShiftInfo Shift(NeighborSpan span, NeighborSpan neighborSpan)
    {
        int s = (int)span;
        int n = (int)neighborSpan;

        if (s is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(span), span, "there are three neighbour spans.");
        }

        if (n is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(neighborSpan), neighborSpan, "there are three neighbour spans.");
        }

        return ShiftInfos[(s * 3) + n];
    }

    /// <summary>
    /// A neighbour's orientation from the two edge indices that meet:
    /// <c>g_CoreDispNeighborOrientationMap</c>, <c>disp_common.cpp:749</c>.
    /// </summary>
    /// <param name="edge">Our edge.</param>
    /// <param name="neighborEdge">Theirs.</param>
    /// <returns>The rotation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either edge is not 0..3.</exception>
    /// <remarks>
    /// The whole table is <c>((neighborEdge - edge + 2) &amp; 3) * 90</c>
    /// degrees, and it is still written out as a table here, because that
    /// closed form is a fact about the numbering rather than a rule anyone
    /// stated: reproducing the expression instead would leave nothing to check
    /// the numbering against if it ever moved.
    /// </remarks>
    public static NeighborOrientation NeighborOrientationFor(int edge, int neighborEdge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(edge, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(edge, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(neighborEdge, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(neighborEdge, 3);

        return (NeighborOrientation)OrientationMap[(edge * 4) + neighborEdge];
    }

    /// <summary>
    /// <c>RotateVertIndex</c>, <c>disp_common.cpp:107</c>: rotate a POSITION
    /// about the grid's centre.
    /// </summary>
    /// <param name="orientation">The rotation.</param>
    /// <param name="sideLengthMinus1">The grid's last index.</param>
    /// <param name="index">The position.</param>
    /// <returns>The rotated position.</returns>
    /// <remarks>
    /// Unlike <see cref="RotateVertIncrement"/> this subtracts from
    /// <paramref name="sideLengthMinus1"/>, because a position has to land back
    /// inside the grid. Using the increment form here is the classic way to
    /// produce indices that are negative on two of the four orientations.
    /// </remarks>
    public static VertIndex RotateVertIndex(
        NeighborOrientation orientation, int sideLengthMinus1, VertIndex index) =>
        orientation switch
        {
            NeighborOrientation.Ccw0 => index,
            NeighborOrientation.Ccw90 => new VertIndex(index.Y, sideLengthMinus1 - index.X),
            NeighborOrientation.Ccw180 =>
                new VertIndex(sideLengthMinus1 - index.X, sideLengthMinus1 - index.Y),

            // Stock's final `else`, which is CCW_270 and also anything else.
            _ => new VertIndex(sideLengthMinus1 - index.Y, index.X),
        };

    /// <summary>
    /// <c>RotateVertIncrement</c>, <c>disp_common.cpp:134</c>: rotate a
    /// DIRECTION about the origin.
    /// </summary>
    /// <param name="orientation">The rotation.</param>
    /// <param name="increment">The direction.</param>
    /// <returns>The rotated direction.</returns>
    public static VertIndex RotateVertIncrement(
        NeighborOrientation orientation, VertIndex increment) =>
        orientation switch
        {
            NeighborOrientation.Ccw0 => increment,
            NeighborOrientation.Ccw90 => new VertIndex(increment.Y, -increment.X),
            NeighborOrientation.Ccw180 => new VertIndex(-increment.X, -increment.Y),
            _ => new VertIndex(-increment.Y, increment.X),
        };

    /// <summary>
    /// Which edge an index lies on: <c>GetEdgeIndexFromPoint</c>,
    /// <c>disp_common.cpp:165</c>.
    /// </summary>
    /// <param name="index">The vertex index.</param>
    /// <param name="power">The displacement's power.</param>
    /// <returns>A <see cref="DispEdge"/>, or -1 for an interior vertex.</returns>
    /// <remarks>
    /// A CORNER lies on two edges and this returns the FIRST that matches, in
    /// left, top, right, bottom order — so the lower-left corner reads as left
    /// and never as bottom. Callers that need both edges of a corner ask for
    /// them by name through <see cref="CornerEdges"/>; this function cannot
    /// give them.
    /// </remarks>
    public static int EdgeIndexFromPoint(VertIndex index, int power)
    {
        int sideLengthMinus1 = 1 << power;

        if (index.X == 0)
        {
            return (int)DispEdge.Left;
        }

        if (index.Y == sideLengthMinus1)
        {
            return (int)DispEdge.Top;
        }

        if (index.X == sideLengthMinus1)
        {
            return (int)DispEdge.Right;
        }

        if (index.Y == 0)
        {
            return (int)DispEdge.Bottom;
        }

        return -1;
    }

    /// <summary>
    /// Which corner an index is, if any: <c>GetCornerIndexFromPoint</c>,
    /// <c>disp_common.cpp:182</c>.
    /// </summary>
    /// <param name="index">The vertex index.</param>
    /// <param name="power">The displacement's power.</param>
    /// <returns>A <see cref="DispCorner"/>, or -1.</returns>
    public static int CornerIndexFromPoint(VertIndex index, int power)
    {
        int sideLengthMinus1 = 1 << power;

        if (index.X == 0)
        {
            if (index.Y == 0)
            {
                return (int)DispCorner.LowerLeft;
            }

            if (index.Y == sideLengthMinus1)
            {
                return (int)DispCorner.UpperLeft;
            }
        }
        else if (index.X == sideLengthMinus1)
        {
            if (index.Y == sideLengthMinus1)
            {
                return (int)DispCorner.UpperRight;
            }

            if (index.Y == 0)
            {
                return (int)DispCorner.LowerRight;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether an index is one of the four corners: <c>IsCorner</c>,
    /// <c>disp_common.cpp:1143</c>.
    /// </summary>
    /// <param name="index">The vertex index.</param>
    /// <param name="sideLength">The grid's side length.</param>
    /// <returns>True for a corner.</returns>
    public static bool IsCorner(VertIndex index, int sideLength)
    {
        if (index.X == 0)
        {
            return index.Y == 0 || index.Y == sideLength - 1;
        }

        if (index.X == sideLength - 1)
        {
            return index.Y == 0 || index.Y == sideLength - 1;
        }

        return false;
    }

    private static ReadOnlySpan<int> OrientationMap =>
    [
        2, 3, 0, 1,
        1, 2, 3, 0,
        0, 1, 2, 3,
        3, 0, 1, 2,
    ];

    private static readonly ImmutableArray<ShiftInfo> ShiftInfos =
    [
        new(0, 0, true),    // CORNER_TO_CORNER   -> CORNER_TO_CORNER
        new(0, -1, true),   // CORNER_TO_CORNER   -> CORNER_TO_MIDPOINT
        new(2, -1, true),   // CORNER_TO_CORNER   -> MIDPOINT_TO_CORNER

        new(0, 1, true),    // CORNER_TO_MIDPOINT -> CORNER_TO_CORNER
        new(0, 0, false),   // CORNER_TO_MIDPOINT -> CORNER_TO_MIDPOINT (invalid)
        new(0, 0, false),   // CORNER_TO_MIDPOINT -> MIDPOINT_TO_CORNER (invalid)

        new(-1, 1, true),   // MIDPOINT_TO_CORNER -> CORNER_TO_CORNER
        new(0, 0, false),   // MIDPOINT_TO_CORNER -> CORNER_TO_MIDPOINT (invalid)
        new(0, 0, false),   // MIDPOINT_TO_CORNER -> MIDPOINT_TO_CORNER (invalid)
    ];
}
