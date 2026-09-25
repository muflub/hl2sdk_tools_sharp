using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// An axis-aligned box round a displacement's base quad: <c>CDispBox</c>,
/// </summary>
/// <param name="Min">The low corner, already puffed out.</param>
/// <param name="Max">The high corner.</param>
public readonly record struct DispBox(Vec3 Min, Vec3 Max);

/// <summary>
/// Works out which displacements touch which:
/// <c>FindNeighboringDispSurfs</c> and <c>SetupAllowedVerts</c>,
/// </summary>
/// <remarks>
/// <para>
/// This is an O(n²) sweep over every pair of displacements in the map, with a
/// bounding-box reject in front of it. It stays that way here: the map with
/// the most displacements this port has compiled would have to grow by two
/// orders of magnitude before the sweep cost as much as one map's face
/// merging, and a spatial index would change the ORDER in which pairs are
/// visited — which decides which of two competing neighbours wins a
/// sub-neighbour slot when a map is malformed, and therefore decides output.
/// </para>
/// <para>
/// The two halves must run in this order and stock's comment says why
/// Corner neighbours are only recorded between
/// displacements that are NOT already edge neighbours, so the edge pass has to
/// have finished for the pair.
/// </para>
/// </remarks>
public static class DispNeighbourFinder
{
    /// <summary>
    /// A displacement edge was claimed by two neighbours at once.
    /// </summary>
    /// <remarks>
    /// Stock prints this through <c>ExecuteOnce</c>, so it appears at most once
    /// per compile however many edges are involved. This port emits one per
    /// occurrence, because a list is not a console and suppressing repeats
    /// would lose the map locations.
    /// </remarks>
    public const string MultipleEdgesCode = "VBSP0501";

    /// <summary>A corner already had four displacements recorded on it.</summary>
    public const string CornerOverflowCode = "VBSP0502";

    /// <summary>
    /// A neighbour connection did not map a vertex back to itself and was
    /// dropped.
    /// </summary>
    public const string BadConnectionCode = "VBSP0503";

    /// <summary>
    /// How far a displacement's bounding box is grown before the touch test:
    /// <c>flPuff</c>.
    /// </summary>
    public const float BoxPuff = 0.1f;

    /// <summary>
    /// How close two base-quad edge points must be to count as the same:
    /// <c>FindEdge</c>.
    /// </summary>
    public const float EdgeTolerance = 0.01f;

    /// <summary>
    /// How close two DISPLACED corners must be to count as the same:
    /// <c>SetupCornerNeighbors</c>.
    /// </summary>
    /// <remarks>
    /// A tenth of <see cref="EdgeTolerance"/>, and applied to a different
    /// quantity — see <see cref="CoreDispInfo.CornerPoint"/>.
    /// </remarks>
    public const float CornerTolerance = 0.001f;

    /// <summary>
    /// Fills every displacement's edge and corner neighbour records.
    /// </summary>
    /// <param name="displacements">
    /// Every displacement in the map, in LUMP_DISPINFO order. Each must already
    /// have its <see cref="CoreDispInfo.ListIndex"/> and its list base set.
    /// </param>
    /// <param name="diagnostics">Where warnings go, or null for none.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="displacements"/> is null.
    /// </exception>
    public static void FindNeighbouringDispSurfs(
        IReadOnlyList<CoreDispInfo> displacements, ICollection<CompileDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(displacements);

        foreach (CoreDispInfo disp in displacements)
        {
            for (int i = 0; i < 4; i++)
            {
                disp.EdgeNeighbor(i).SetInvalid();
                disp.CornerNeighbors(i).SetInvalid();
            }
        }

        DispBox[] boxes = new DispBox[displacements.Count];
        for (int i = 0; i < displacements.Count; i++)
        {
            boxes[i] = GetDispBox(displacements[i]);
        }

        int cornerOverflows = 0;

        for (int i = 0; i < displacements.Count; i++)
        {
            CoreDispInfo main = displacements[i];

            for (int j = i + 1; j < displacements.Count; j++)
            {
                if (!BoxesTouch(boxes[i], boxes[j]))
                {
                    continue;
                }

                CoreDispInfo other = displacements[j];

                SetupEdgeNeighbors(main, other, diagnostics);
                SetupCornerNeighbors(main, other, ref cornerOverflows);
            }
        }

        if (cornerOverflows != 0)
        {
            diagnostics?.Add(new CompileDiagnostic(
                CornerOverflowCode,
                DiagnosticSeverity.Warning,
                $"overflowed {cornerOverflows} displacement corner-neighbor lists."));
        }

        VerifyNeighborConnections(displacements, diagnostics);
    }

    /// <summary>
    /// A displacement's puffed base-quad box: <c>GetDispBox</c>,
    /// </summary>
    /// <param name="disp">The displacement.</param>
    /// <returns>Its box.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    /// <remarks>
    /// Over the BASE QUAD's four points and not over the displaced vertices, so
    /// a displacement that pushes a long way out of its own footprint still
    /// gets the small box. That is correct for finding neighbours — two
    /// displacements are neighbours because their base surfaces abut — and it
    /// is the reason this box must not be reused as a bounding volume for
    /// anything else.
    /// </remarks>
    public static DispBox GetDispBox(CoreDispInfo disp)
    {
        ArgumentNullException.ThrowIfNull(disp);

        Vec3 min = new(1e24f, 1e24f, 1e24f);
        Vec3 max = new(-1e24f, -1e24f, -1e24f);

        ReadOnlySpan<Vec3> points = disp.Surface.Points;
        for (int i = 0; i < 4; i++)
        {
            min = new Vec3(
                MathF.Min(min.X, points[i].X),
                MathF.Min(min.Y, points[i].Y),
                MathF.Min(min.Z, points[i].Z));
            max = new Vec3(
                MathF.Max(max.X, points[i].X),
                MathF.Max(max.Y, points[i].Y),
                MathF.Max(max.Z, points[i].Z));
        }

        Vec3 puff = new(BoxPuff, BoxPuff, BoxPuff);
        return new DispBox(min - puff, max + puff);
    }

    /// <summary><c>DoBBoxesTouch</c>.</summary>
    /// <param name="a">One box.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when they overlap or abut.</returns>
    public static bool BoxesTouch(DispBox a, DispBox b) =>
        a.Max.X >= b.Min.X && a.Min.X <= b.Max.X &&
        a.Max.Y >= b.Min.Y && a.Min.Y <= b.Max.Y &&
        a.Max.Z >= b.Min.Z && a.Min.Z <= b.Max.Z;

    /// <summary>
    /// Finds the edge of a displacement running from one point to another:
    /// <c>FindEdge</c>.
    /// </summary>
    /// <param name="disp">The displacement to search.</param>
    /// <param name="point1">The edge's first point.</param>
    /// <param name="point2">Its second.</param>
    /// <returns>A <see cref="DispEdge"/>, or -1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    /// <remarks>
    /// DIRECTED: edge <c>e</c> runs from point <c>e</c> to point
    /// <c>(e+1) &amp; 3</c>, so the caller passes the points in the order the
    /// NEIGHBOUR would see them, which is the reverse of its own. That is why
    /// every call site in <see cref="SetupEdgeNeighbors"/> passes
    /// <c>pt[1]</c> before <c>pt[0]</c>.
    /// </remarks>
    public static int FindEdge(CoreDispInfo disp, Vec3 point1, Vec3 point2)
    {
        ArgumentNullException.ThrowIfNull(disp);

        ReadOnlySpan<Vec3> points = disp.Surface.Points;

        for (int edge = 0; edge < 4; edge++)
        {
            if (VectorsAreEqual(point1, points[edge], EdgeTolerance) &&
                VectorsAreEqual(point2, points[(edge + 1) & 3], EdgeTolerance))
            {
                return edge;
            }
        }

        return -1;
    }

    /// <summary>
    /// <c>VectorsAreEqual</c>: every component within a
    /// tolerance, inclusive.
    /// </summary>
    /// <param name="a">One vector.</param>
    /// <param name="b">The other.</param>
    /// <param name="tolerance">The per-component tolerance.</param>
    /// <returns>True when all three components agree.</returns>
    public static bool VectorsAreEqual(Vec3 a, Vec3 b, float tolerance) =>
        MathF.Abs(a.X - b.X) <= tolerance &&
        MathF.Abs(a.Y - b.Y) <= tolerance &&
        MathF.Abs(a.Z - b.Z) <= tolerance;

    /// <summary>
    /// Records the relationship between two displacements' edges, both ways
    /// round: <c>SetupEdgeNeighbors</c>.
    /// </summary>
    /// <param name="main">One displacement.</param>
    /// <param name="other">The other.</param>
    /// <param name="diagnostics">Where warnings go, or null.</param>
    /// <exception cref="ArgumentNullException">Either displacement is null.</exception>
    /// <remarks>
    /// <para>
    /// Four cases per edge, tried in order and NOT exclusive of each other in
    /// the last one:
    /// </para>
    /// <list type="number">
    /// <item>the neighbour's edge is exactly ours: one whole-to-whole link;</item>
    /// <item>the neighbour's edge is twice ours and we are its first half;</item>
    /// <item>the neighbour's edge is twice ours and we are its second half;</item>
    /// <item>
    /// one or TWO neighbours each cover half of ours — both are tested, and a
    /// map can have one without the other.
    /// </item>
    /// </list>
    /// <para>
    /// Cases 2 and 3 use the reflected point <c>pt[0]*2 - pt[1]</c>: the
    /// neighbour's edge continues past our corner by our own length, so its far
    /// end is our near end reflected through our far end.
    /// </para>
    /// </remarks>
    public static void SetupEdgeNeighbors(
        CoreDispInfo main, CoreDispInfo other, ICollection<CompileDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(other);

        for (int edge = 0; edge < 4; edge++)
        {
            Vec3 p0 = main.Surface.Points[edge];
            Vec3 p1 = main.Surface.Points[(edge + 1) & 3];
            Vec3 mid = (p0 + p1) * 0.5f;

            int nbEdge = FindEdge(other, p1, p0);
            if (nbEdge != -1)
            {
                AddNeighbor(
                    main, edge, 0, NeighborSpan.CornerToCorner,
                    other, nbEdge, NeighborSpan.CornerToCorner, diagnostics);
                continue;
            }

            nbEdge = FindEdge(other, p1, (p0 * 2.0f) - p1);
            if (nbEdge != -1)
            {
                AddNeighbor(
                    main, edge, 0, NeighborSpan.CornerToCorner,
                    other, nbEdge, NeighborSpan.CornerToMidpoint, diagnostics);
                continue;
            }

            nbEdge = FindEdge(other, (p1 * 2.0f) - p0, p0);
            if (nbEdge != -1)
            {
                AddNeighbor(
                    main, edge, 0, NeighborSpan.CornerToCorner,
                    other, nbEdge, NeighborSpan.MidpointToCorner, diagnostics);
                continue;
            }

            nbEdge = FindEdge(other, mid, p0);
            if (nbEdge != -1)
            {
                AddNeighbor(
                    main, edge, DispTables.EdgeNeighborFlipSlot(edge),
                    NeighborSpan.CornerToMidpoint,
                    other, nbEdge, NeighborSpan.CornerToCorner, diagnostics);
            }

            nbEdge = FindEdge(other, p1, mid);
            if (nbEdge != -1)
            {
                AddNeighbor(
                    main, edge, 1 - DispTables.EdgeNeighborFlipSlot(edge),
                    NeighborSpan.MidpointToCorner,
                    other, nbEdge, NeighborSpan.CornerToCorner, diagnostics);
            }
        }
    }

    /// <summary>
    /// Writes one connection into both displacements: <c>AddNeighbor</c>,
    /// </summary>
    /// <param name="main">The displacement whose edge this is.</param>
    /// <param name="edge">Which of its edges.</param>
    /// <param name="sub">Which of its sub-neighbour slots.</param>
    /// <param name="span">What part of its edge the neighbour fills.</param>
    /// <param name="other">The neighbour.</param>
    /// <param name="neighborEdge">Which of the neighbour's edges.</param>
    /// <param name="neighborSpan">What part of the neighbour's edge we fill.</param>
    /// <param name="diagnostics">Where the contention warning goes, or null.</param>
    /// <exception cref="ArgumentNullException">Either displacement is null.</exception>
    /// <remarks>
    /// The NEIGHBOUR's slot is chosen from the span rather than passed in: a
    /// neighbour whose second half we fill goes in its slot 1, everything else
    /// in slot 0. That is what makes stock's invariant hold — a
    /// <c>CORNER_TO_CORNER</c> neighbour is always in slot 0
    /// </remarks>
    public static void AddNeighbor(
        CoreDispInfo main,
        int edge,
        int sub,
        NeighborSpan span,
        CoreDispInfo other,
        int neighborEdge,
        NeighborSpan neighborSpan,
        ICollection<CompileDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(other);

        span = DispTables.NeighborSpanFlip(edge, span);
        neighborSpan = DispTables.NeighborSpanFlip(neighborEdge, neighborSpan);

        int neighborSub = neighborSpan == NeighborSpan.MidpointToCorner ? 1 : 0;

        ref DispSubNeighbor mine = ref main.EdgeNeighbor(edge).SubNeighbors[sub];
        ref DispSubNeighbor theirs =
            ref other.EdgeNeighbor(neighborEdge).SubNeighbors[neighborSub];

        if (mine.IsValid() || theirs.IsValid())
        {
            // ExecuteOnce in stock, so it prints at most once per compile.
            diagnostics?.Add(new CompileDiagnostic(
                MultipleEdgesCode,
                DiagnosticSeverity.Warning,
                "Found a displacement edge abutting multiple other edges."));
            return;
        }

        mine.Neighbor = (ushort)other.ListIndex;
        mine.NeighborOrientation =
            (byte)DispTables.NeighborOrientationFor(edge, neighborEdge);
        mine.Span = (byte)span;
        mine.NeighborSpan = (byte)neighborSpan;

        theirs.Neighbor = (ushort)main.ListIndex;
        theirs.NeighborOrientation =
            (byte)DispTables.NeighborOrientationFor(neighborEdge, edge);
        theirs.Span = (byte)neighborSpan;
        theirs.NeighborSpan = (byte)span;
    }

    /// <summary>
    /// Whether one displacement already names another anywhere:
    /// <c>HasEdgeNeighbor</c>.
    /// </summary>
    /// <param name="main">The displacement to search.</param>
    /// <param name="neighbor">The index to look for.</param>
    /// <returns>True if it appears on any edge or corner.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="main"/> is null.</exception>
    /// <remarks>
    /// Despite the name it checks the CORNER lists too, and checks them first.
    /// </remarks>
    public static bool HasEdgeNeighbor(CoreDispInfo main, int neighbor)
    {
        ArgumentNullException.ThrowIfNull(main);

        for (int i = 0; i < 4; i++)
        {
            ref DispCornerNeighbors corner = ref main.CornerNeighbors(i);
            for (int n = 0; n < corner.NumNeighbors; n++)
            {
                if (corner.Neighbors[n] == neighbor)
                {
                    return true;
                }
            }

            ref DispNeighbor edge = ref main.EdgeNeighbor(i);
            if (edge.SubNeighbors[0].Neighbor == neighbor ||
                edge.SubNeighbors[1].Neighbor == neighbor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records two displacements that meet at exactly one point:
    /// <c>SetupCornerNeighbors</c>.
    /// </summary>
    /// <param name="main">One displacement.</param>
    /// <param name="other">The other.</param>
    /// <param name="overflows">Incremented when a corner list is already full.</param>
    /// <exception cref="ArgumentNullException">Either displacement is null.</exception>
    /// <remarks>
    /// <para>
    /// EXACTLY one: the 4x4 corner comparison counts every match and the pair
    /// is only recorded when the count is one. Two shared corners mean a shared
    /// edge, which the edge pass has already handled, and a degenerate
    /// displacement with three or four could otherwise be recorded at whichever
    /// corner happened to match last.
    /// </para>
    /// <para>
    /// The comparison is on DISPLACED corners. See
    /// <see cref="CoreDispInfo.CornerPoint"/>.
    /// </para>
    /// </remarks>
    public static void SetupCornerNeighbors(
        CoreDispInfo main, CoreDispInfo other, ref int overflows)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(other);

        if (HasEdgeNeighbor(main, other.ListIndex))
        {
            return;
        }

        int shared = 0;
        int mainCorner = -1;
        int otherCorner = -1;

        for (int i = 0; i < 4; i++)
        {
            Vec3 a = main.CornerPoint(i);

            for (int j = 0; j < 4; j++)
            {
                if (VectorsAreEqual(a, other.CornerPoint(j), CornerTolerance))
                {
                    mainCorner = i;
                    otherCorner = j;
                    shared++;
                }
            }
        }

        if (shared != 1)
        {
            return;
        }

        ref DispCornerNeighbors mine = ref main.CornerNeighbors(mainCorner);
        ref DispCornerNeighbors theirs = ref other.CornerNeighbors(otherCorner);

        if (mine.NumNeighbors < MaxDispCornerNeighbors &&
            theirs.NumNeighbors < MaxDispCornerNeighbors)
        {
            mine.Neighbors[mine.NumNeighbors++] = (ushort)other.ListIndex;
            theirs.Neighbors[theirs.NumNeighbors++] = (ushort)main.ListIndex;
        }
        else
        {
            overflows++;
        }
    }

    /// <summary>
    /// How many displacements may be recorded at one corner:
    /// <c>MAX_DISP_CORNER_NEIGHBORS</c>.
    /// </summary>
    public const int MaxDispCornerNeighbors = 4;

    /// <summary>
    /// Throws away any edge whose vertices do not map back to themselves:
    /// <c>VerifyNeighborConnections</c>.
    /// </summary>
    /// <param name="displacements">Every displacement in the map.</param>
    /// <param name="diagnostics">Where warnings go, or null.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="displacements"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The outer loop repeats until a whole pass changes nothing, because
    /// invalidating one edge can break a connection that verified a moment ago:
    /// the walk goes out through one displacement and back through another, and
    /// the return leg may be the edge just removed.
    /// </para>
    /// <para>
    /// The check is genuinely a ROUND TRIP and not a validity test. It maps a
    /// vertex into the neighbour, asks which edge it landed on, maps it back,
    /// and requires the same displacement and the same vertex. A connection
    /// that is merely asymmetric — one side says corner-to-midpoint and the
    /// other does not agree — fails here and is dropped rather than shipped.
    /// </para>
    /// </remarks>
    public static void VerifyNeighborConnections(
        IReadOnlyList<CoreDispInfo> displacements, ICollection<CompileDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(displacements);

        bool happy;

        do
        {
            happy = true;

            foreach (CoreDispInfo disp in displacements)
            {
                for (int edge = 0; edge < 4; edge++)
                {
                    DispEdgeIterator it = new(disp, edge);

                    while (it.Next())
                    {
                        if (VerifyVertConnection(
                                disp, it.VertIndex, it.CurrentNeighbor, it.NeighborVertIndex, edge))
                        {
                            continue;
                        }

                        disp.EdgeNeighbor(edge).SetInvalid();
                        Vec3 corner = disp.CornerPoint(0);
                        diagnostics?.Add(new CompileDiagnostic(
                            BadConnectionCode,
                            DiagnosticSeverity.Warning,
                            "invalid neighbor connection on displacement near "
                            + FormattableString.Invariant(
                                $"({corner.X:F2} {corner.Y:F2} {corner.Z:F2})"),
                            new MapLocation(Position: (corner.X, corner.Y, corner.Z))));
                        happy = false;
                    }
                }
            }
        }
        while (!happy);
    }

    /// <summary>
    /// <c>VerifyNeighborVertConnection</c>.
    /// </summary>
    private static bool VerifyVertConnection(
        IDispUtils disp,
        VertIndex nodeIndex,
        IDispUtils? testNeighbor,
        VertIndex testNeighborIndex,
        int side)
    {
        IDispUtils? neighbor =
            DispUtils.TransformIntoNeighbor(disp, side, nodeIndex, out VertIndex nbIndex);

        if (neighbor is null)
        {
            // Stock's `if` never enters, so the function returns true: a vertex
            // that maps nowhere is not a broken connection.
            return true;
        }

        if (!ReferenceEquals(testNeighbor, neighbor) || nbIndex != testNeighborIndex)
        {
            return false;
        }

        int nbSide = DispTables.EdgeIndexFromPoint(nbIndex, neighbor.PowerInfo.Power);
        if (nbSide == -1)
        {
            return false;
        }

        IDispUtils? test =
            DispUtils.TransformIntoNeighbor(neighbor, nbSide, nbIndex, out VertIndex testIndex);

        return ReferenceEquals(test, disp) && nodeIndex == testIndex;
    }

    /// <summary>
    /// Switches off the vertices a lower-powered neighbour cannot match:
    /// <c>SetupAllowedVerts</c>.
    /// </summary>
    /// <param name="displacements">Every displacement in the map.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="displacements"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS: a power-4 displacement next to a power-2 has four times
    /// as many vertices along their shared edge. Three out of four of its edge
    /// vertices have no counterpart, so the engine must not tessellate down to
    /// them or the seam cracks. Clearing the bit removes the vertex, and
    /// removing a vertex removes every vertex that DEPENDS on it —
    /// which is what <see cref="PowerInfo.VertInfos"/>'s reverse dependencies
    /// are for, and why the removal is recursive and inward.
    /// </para>
    /// <para>
    /// The outer loop repeats until a whole pass clears nothing, because a
    /// displacement that loses vertices becomes, in effect, lower-powered along
    /// that edge, and its OWN neighbours must then lose vertices in turn.
    /// Stock's comment says so.
    /// </para>
    /// </remarks>
    public static void SetupAllowedVerts(IReadOnlyList<CoreDispInfo> displacements)
    {
        ArgumentNullException.ThrowIfNull(displacements);

        foreach (CoreDispInfo disp in displacements)
        {
            disp.AllowedVertsSetAll();
        }

        bool again;

        do
        {
            again = false;

            foreach (CoreDispInfo disp in displacements)
            {
                int unallowed = 0;
                DisableUnallowedVerts(disp, disp.PowerInfo.RootNode, 0, ref unallowed);

                if (unallowed != 0)
                {
                    again = true;
                }
            }
        }
        while (again);
    }

    /// <summary><c>DisableUnallowedVerts_R</c>.</summary>
    private static void DisableUnallowedVerts(
        CoreDispInfo disp, VertIndex nodeIndex, int level, ref int unallowed)
    {
        PowerInfo info = disp.PowerInfo;
        int iNode = info.VertIndexToInt(nodeIndex);

        for (int side = 0; side < 4; side++)
        {
            VertIndex sideVert = info.SideVerts[iNode].Verts[side];

            if (!IsVertAllowed(disp, sideVert, level))
            {
                UnallowVerts(disp, sideVert, ref unallowed);
            }
        }

        if (level + 1 >= info.Power)
        {
            return;
        }

        for (int child = 0; child < 4; child++)
        {
            DisableUnallowedVerts(
                disp, info.ChildVerts[iNode].Verts[child], level + 1, ref unallowed);
        }
    }

    /// <summary><c>UnallowVerts_R</c>.</summary>
    private static void UnallowVerts(CoreDispInfo disp, VertIndex nodeIndex, ref int unallowed)
    {
        int iNode = disp.PowerInfo.VertIndexToInt(nodeIndex);

        if (!disp.AllowedVertsGet(iNode))
        {
            return;
        }

        unallowed++;
        disp.AllowedVertsClear(iNode);

        VertInfo info = disp.PowerInfo.VertInfos[iNode];

        for (int i = 0; i < VertInfo.ReverseDependencyCount; i++)
        {
            VertDependency dep = info.ReverseDependencies[i];

            if (dep.Vert.X != -1 && dep.Neighbor == -1)
            {
                UnallowVerts(disp, dep.Vert, ref unallowed);
            }
        }
    }

    /// <summary><c>IsVertAllowed</c>.</summary>
    /// <remarks>
    /// The four early-outs all mean "nothing can object": a corner is never
    /// removed, an interior vertex has no neighbour to disagree with it, and an
    /// edge vertex with no neighbour on that edge is ours alone. Only a vertex
    /// with a live connection can be refused, and then for two separate
    /// reasons — the neighbour is not fine enough to have it, or the neighbour
    /// has already had it taken away.
    /// </remarks>
    private static bool IsVertAllowed(CoreDispInfo disp, VertIndex sideVert, int level)
    {
        PowerInfo info = disp.PowerInfo;

        if (DispTables.IsCorner(sideVert, info.SideLength))
        {
            return true;
        }

        int side = DispTables.EdgeIndexFromPoint(sideVert, info.Power);
        if (side == -1)
        {
            return true;
        }

        int sub = DispUtils.SubNeighborIndex(disp, side, sideVert);
        if (sub == -1)
        {
            return true;
        }

        DispSubNeighbor subNeighbor = disp.EdgeNeighbor(side).SubNeighbors[sub];
        CoreDispInfo neighbor = (CoreDispInfo)disp.ByIndex(subNeighbor.Neighbor)!;

        ShiftInfo shift = DispTables.Shift(
            (NeighborSpan)subNeighbor.Span, (NeighborSpan)subNeighbor.NeighborSpan);

        if (neighbor.PowerInfo.Power + shift.PowerShiftAdd < level + 1)
        {
            return false;
        }

        DispUtils.TransformIntoSubNeighbor(disp, side, sub, sideVert, out VertIndex nbIndex);

        return neighbor.AllowedVertsGet(neighbor.PowerInfo.VertIndexToInt(nbIndex));
    }
}
