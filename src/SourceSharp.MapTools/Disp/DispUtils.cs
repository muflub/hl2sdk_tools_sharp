using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// The neighbour traversal: how a vertex on one displacement's edge maps onto
/// the vertex it shares with the displacement next to it.
/// </summary>
/// <remarks>
/// <para>
/// The half of it that is not the finder. This
/// is what makes two displacements of different powers join without a seam:
/// every function here answers some form of "which of the neighbour's vertices
/// is this one of mine?", and <c>SetupAllowedVerts</c> then deletes the
/// vertices for which the answer is "none".
/// </para>
/// <para>
/// THE ONE IDEA worth holding while reading it is that a displacement's edge
/// can be covered by one neighbour or by two halves of one, and that the same
/// relation seen from the other side may be either. A power-4 whose right edge
/// meets two power-3s sees <c>CORNER_TO_MIDPOINT</c> and
/// <c>MIDPOINT_TO_CORNER</c> in its own two sub-neighbour slots; each power-3
/// sees a single <c>CORNER_TO_CORNER</c> whose <c>m_NeighborSpan</c> is the
/// half it occupies. Everything else is bookkeeping over that.
/// </para>
/// </remarks>
public static class DispUtils
{
    /// <summary>
    /// The power a neighbour effectively has across one connection:
    /// <c>GetNeighborEdgePower</c>.
    /// </summary>
    /// <param name="disp">The displacement asking.</param>
    /// <param name="edge">Which of its edges.</param>
    /// <param name="sub">Which of the up-to-two neighbours on it.</param>
    /// <returns>
    /// The neighbour's power adjusted by the span shift, or -1 if that slot is
    /// empty.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    public static int NeighborEdgePower(IDispUtils disp, int edge, int sub)
    {
        ArgumentNullException.ThrowIfNull(disp);

        ref DispNeighbor edgeNeighbor = ref disp.EdgeNeighbor(edge);
        DispSubNeighbor subNeighbor = edgeNeighbor.SubNeighbors[sub];

        if (!subNeighbor.IsValid())
        {
            return -1;
        }

        IDispUtils neighbor = disp.ByIndex(subNeighbor.Neighbor)!;
        ShiftInfo shift = DispTables.Shift(
            (NeighborSpan)subNeighbor.Span, (NeighborSpan)subNeighbor.NeighborSpan);

        return neighbor.PowerInfo.Power + shift.PowerShiftAdd;
    }

    /// <summary>
    /// The part of an edge a span covers: <c>SetupSpan</c>,
    /// </summary>
    /// <param name="power">The displacement's power.</param>
    /// <param name="edge">A <see cref="DispEdge"/>.</param>
    /// <param name="span">The span.</param>
    /// <returns>The first and last grid index of the covered run.</returns>
    /// <remarks>
    /// The start and end are <c>GetCornerPointIndex(edge)</c> and
    /// <c>GetCornerPointIndex(edge + 1)</c>, which walk the corners
    /// CLOCKWISE — so on the right and bottom edges the run goes down and
    /// left, and the two half-spans have to be applied to the opposite end.
    /// That reversal is the same fact <see cref="DispTables.EdgeNeighborFlip"/>
    /// records, spelled out a second time because the two are applied at
    /// different moments and one without the other is a silent quarter-turn.
    /// </remarks>
    public static (VertIndex Start, VertIndex End) SetupSpan(
        int power, int edge, NeighborSpan span)
    {
        PowerInfo info = PowerInfo.Get(power);
        int freeDim = DispTables.EdgeDims[edge] == 0 ? 1 : 0;

        VertIndex start = info.CornerPointIndex(edge);
        VertIndex end = info.CornerPointIndex((edge + 1) & 3);

        if (edge == (int)DispEdge.Right || edge == (int)DispEdge.Bottom)
        {
            if (span == NeighborSpan.CornerToMidpoint)
            {
                start = start.With(freeDim, info.MidPoint);
            }
            else if (span == NeighborSpan.MidpointToCorner)
            {
                end = end.With(freeDim, info.MidPoint);
            }
        }
        else
        {
            if (span == NeighborSpan.CornerToMidpoint)
            {
                end = end.With(freeDim, info.MidPoint);
            }
            else if (span == NeighborSpan.MidpointToCorner)
            {
                start = start.With(freeDim, info.MidPoint);
            }
        }

        return (start, end);
    }

    /// <summary>
    /// Maps one of our edge vertices onto a named sub-neighbour:
    /// <c>TransformIntoSubNeighbor</c>.
    /// </summary>
    /// <param name="disp">The displacement the vertex belongs to.</param>
    /// <param name="edge">Which of its edges.</param>
    /// <param name="sub">Which sub-neighbour.</param>
    /// <param name="nodeIndex">The vertex.</param>
    /// <param name="result">The matching vertex in the neighbour.</param>
    /// <returns>The neighbour.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The mapping is a 16.16 FIXED-POINT percentage along the span, and that
    /// is not incidental: <c>(delta * 65536) / span</c> then
    /// <c>(nbSpan * percent) / 65536</c> in integers truncates toward zero at
    /// both ends, and a float percentage would round differently at the
    /// midpoints of an odd-length run. Every vertex a connection can name has
    /// an exact integer answer, so the truncation never actually bites — but
    /// reproducing it costs nothing and removes the question.
    /// </para>
    /// <para>
    /// Note the DELIBERATELY REVERSED out-parameters in stock's second
    /// <c>SetupSpan</c> call (: <c>viDestEnd</c> then
    /// <c>viDestStart</c>). Two displacements that share an edge traverse it in
    /// opposite directions, so the neighbour's start is our end. Reading that
    /// line as a typo and "fixing" it mirrors every joined vertex.
    /// </para>
    /// </remarks>
    public static IDispUtils TransformIntoSubNeighbor(
        IDispUtils disp, int edge, int sub, VertIndex nodeIndex, out VertIndex result)
    {
        ArgumentNullException.ThrowIfNull(disp);

        ref DispNeighbor edgeNeighbor = ref disp.EdgeNeighbor(edge);
        DispSubNeighbor subNeighbor = edgeNeighbor.SubNeighbors[sub];

        (VertIndex srcStart, VertIndex srcEnd) =
            SetupSpan(disp.PowerInfo.Power, edge, (NeighborSpan)subNeighbor.Span);

        IDispUtils neighbor = disp.ByIndex(subNeighbor.Neighbor)!;
        int nbEdge = (edge + 2 + subNeighbor.NeighborOrientation) & 3;

        (VertIndex destEnd, VertIndex destStart) = SetupSpan(
            neighbor.PowerInfo.Power, nbEdge, (NeighborSpan)subNeighbor.NeighborSpan);

        int freeDim = DispTables.EdgeDims[edge] == 0 ? 1 : 0;
        int fixedPercent =
            ((nodeIndex[freeDim] - srcStart[freeDim]) * (1 << 16))
            / (srcEnd[freeDim] - srcStart[freeDim]);

        int nbDim = DispTables.EdgeDims[nbEdge];
        int nbFree = nbDim == 0 ? 1 : 0;

        VertIndex outIndex = default;
        outIndex = outIndex.With(nbDim, destStart[nbDim]);
        outIndex = outIndex.With(
            nbFree,
            // A DIVIDE and not a shift. The span difference is negative
            // whenever the neighbour traverses the shared edge the other way,
            // which is most of the time, and C's division truncates toward zero
            // where an arithmetic shift floors. The two differ by one for every
            // negative non-multiple.
            destStart[nbFree]
                + (((destEnd[nbFree] - destStart[nbFree]) * fixedPercent) / (1 << 16)));

        result = outIndex;
        return neighbor;
    }

    /// <summary>
    /// Which sub-neighbour a vertex falls in: <c>GetSubNeighborIndex</c>,
    /// </summary>
    /// <param name="disp">The displacement.</param>
    /// <param name="edge">Which of its edges.</param>
    /// <param name="nodeIndex">The vertex.</param>
    /// <returns>0, 1, or -1 for no usable neighbour.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The MIDPOINT case is the one with a reason worth keeping: a vertex
    /// exactly on the middle of our edge is only interesting when a SINGLE
    /// neighbour spans the whole edge, because then our midpoint is some
    /// interior vertex of theirs and may be switched off. If two neighbours
    /// meet at our midpoint, our midpoint is one of THEIR corners, and corner
    /// vertices are never disabled — so stock returns -1 and asks nothing.
    /// </para>
    /// <para>
    /// The fallback below it then covers the case where the vertex is past the
    /// midpoint but the far half has no neighbour of its own: if slot 0 spans
    /// the whole edge, it is the answer after all.
    /// </para>
    /// </remarks>
    public static int SubNeighborIndex(IDispUtils disp, int edge, VertIndex nodeIndex)
    {
        ArgumentNullException.ThrowIfNull(disp);

        PowerInfo info = disp.PowerInfo;
        ref DispNeighbor side = ref disp.EdgeNeighbor(edge);

        int edgeDim = DispTables.EdgeDims[edge];
        int freeDim = edgeDim == 0 ? 1 : 0;
        int freeIndex = nodeIndex[freeDim];

        int sub = 0;
        if (freeIndex == info.MidPoint)
        {
            if ((NeighborSpan)side.SubNeighbors[0].Span != NeighborSpan.CornerToCorner)
            {
                return -1;
            }
        }
        else if (freeIndex > info.MidPoint)
        {
            sub = 1;
        }

        if (side.SubNeighbors[sub].IsValid())
        {
            return sub;
        }

        if (sub == 1 &&
            side.SubNeighbors[0].IsValid() &&
            (NeighborSpan)side.SubNeighbors[0].Span == NeighborSpan.CornerToCorner)
        {
            return 0;
        }

        return -1;
    }

    /// <summary>
    /// Maps one of our vertices into whichever neighbour touches it:
    /// <c>TransformIntoNeighbor</c>.
    /// </summary>
    /// <param name="disp">The displacement.</param>
    /// <param name="edge">
    /// Which edge to go through, or -1 to let
    /// <see cref="DispTables.EdgeIndexFromPoint"/> choose.
    /// </param>
    /// <param name="nodeIndex">The vertex.</param>
    /// <param name="result">The matching vertex in the neighbour, if any.</param>
    /// <returns>The neighbour, or null when the vertex touches nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    /// <remarks>
    /// Passing -1 for a CORNER vertex picks whichever edge
    /// <see cref="DispTables.EdgeIndexFromPoint"/> tests first, which is rarely
    /// the one the caller meant. Stock's own callers that care pass the edge
    /// explicitly; the one that does not, <see cref="DoesPointHaveAnyNeighbors"/>,
    /// follows up by asking both of the corner's edges by name.
    /// </remarks>
    public static IDispUtils? TransformIntoNeighbor(
        IDispUtils disp, int edge, VertIndex nodeIndex, out VertIndex result)
    {
        ArgumentNullException.ThrowIfNull(disp);

        if (edge == -1)
        {
            edge = DispTables.EdgeIndexFromPoint(nodeIndex, disp.PowerInfo.Power);
        }

        result = default;

        if (edge == -1)
        {
            return null;
        }

        int sub = SubNeighborIndex(disp, edge, nodeIndex);
        if (sub == -1)
        {
            return null;
        }

        return TransformIntoSubNeighbor(disp, edge, sub, nodeIndex, out result);
    }

    /// <summary>
    /// Whether anything at all touches a vertex:
    /// <c>DoesPointHaveAnyNeighbors</c>.
    /// </summary>
    /// <param name="disp">The displacement.</param>
    /// <param name="index">The vertex.</param>
    /// <returns>True if some neighbour shares it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    public static bool DoesPointHaveAnyNeighbors(IDispUtils disp, VertIndex index)
    {
        ArgumentNullException.ThrowIfNull(disp);

        if (TransformIntoNeighbor(disp, -1, index, out _) is not null)
        {
            return true;
        }

        int corner = DispTables.CornerIndexFromPoint(index, disp.PowerInfo.Power);
        if (corner == -1)
        {
            return false;
        }

        if (disp.CornerNeighbors(corner).NumNeighbors > 0)
        {
            return true;
        }

        (DispEdge first, DispEdge second) = DispTables.CornerEdges(corner);

        return TransformIntoNeighbor(disp, (int)first, index, out _) is not null
            || TransformIntoNeighbor(disp, (int)second, index, out _) is not null;
    }

    /// <summary>
    /// How to walk the vertices two displacements share along one
    /// sub-neighbour: <c>SetupEdgeIncrements</c>.
    /// </summary>
    /// <param name="disp">The displacement.</param>
    /// <param name="edge">Which of its edges.</param>
    /// <param name="sub">Which sub-neighbour.</param>
    /// <param name="walk">The starting indices, steps and end, if there is one.</param>
    /// <returns>The neighbour, or null when that slot is empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    /// <remarks>
    /// The two increments are asymmetric on purpose: whichever side has the
    /// HIGHER effective power steps by one and the other steps by the power
    /// difference, so the walk visits exactly the vertices that exist on both.
    /// </remarks>
    public static IDispUtils? SetupEdgeIncrements(
        IDispUtils disp, int edge, int sub, out EdgeWalk walk)
    {
        ArgumentNullException.ThrowIfNull(disp);

        walk = default;

        int edgeDim = DispTables.EdgeDims[edge];
        int freeDim = edgeDim == 0 ? 1 : 0;

        ref DispNeighbor side = ref disp.EdgeNeighbor(edge);
        DispSubNeighbor subNeighbor = side.SubNeighbors[sub];

        if (!subNeighbor.IsValid())
        {
            return null;
        }

        IDispUtils neighbor = disp.ByIndex(subNeighbor.Neighbor)!;
        ShiftInfo shift = DispTables.Shift(
            (NeighborSpan)subNeighbor.Span, (NeighborSpan)subNeighbor.NeighborSpan);

        PowerInfo info = disp.PowerInfo;

        VertIndex myIndex = default;
        myIndex = myIndex.With(edgeDim, DispTables.EdgeSideLenMul[edge] * info.SideLengthMinus1);
        myIndex = myIndex.With(freeDim, info.MidPoint * sub);

        TransformIntoSubNeighbor(disp, edge, sub, myIndex, out VertIndex nbIndex);

        int myPower = info.Power;
        int nbPower = neighbor.PowerInfo.Power + shift.PowerShiftAdd;

        VertIndex myInc = default;
        VertIndex tempInc = default;

        if (nbPower > myPower)
        {
            myInc = myInc.With(freeDim, 1);
            tempInc = tempInc.With(freeDim, 1 << (nbPower - myPower));
        }
        else
        {
            myInc = myInc.With(freeDim, 1 << (myPower - nbPower));
            tempInc = tempInc.With(freeDim, 1);
        }

        VertIndex nbInc = DispTables.RotateVertIncrement(
            (NeighborOrientation)subNeighbor.NeighborOrientation, tempInc);

        int end = (NeighborSpan)subNeighbor.Span == NeighborSpan.CornerToMidpoint
            ? info.SideLength >> 1
            : info.SideLength - 1;

        walk = new EdgeWalk(myIndex, myInc, nbIndex, nbInc, end, freeDim);
        return neighbor;
    }
}

/// <summary>
/// The state of a walk along the vertices two displacements share.
/// </summary>
/// <param name="Index">Our current vertex.</param>
/// <param name="Increment">Our step.</param>
/// <param name="NeighborIndex">The neighbour's current vertex.</param>
/// <param name="NeighborIncrement">The neighbour's step.</param>
/// <param name="End">
/// The value of our free dimension at which the walk stops, exclusive.
/// </param>
/// <param name="FreeDim">Which of our dimensions the walk varies.</param>
public readonly record struct EdgeWalk(
    VertIndex Index,
    VertIndex Increment,
    VertIndex NeighborIndex,
    VertIndex NeighborIncrement,
    int End,
    int FreeDim);
