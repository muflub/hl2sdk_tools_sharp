//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;

using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// One vertex another vertex needs in order to exist:
/// <c>CVertDependency</c>.
/// </summary>
/// <param name="Vert">
/// The dependency's index, in the SAME power as the displacement holding it,
/// and wrapped — so a dependency that reaches past an edge carries the index
/// it would have on the far side rather than an out-of-range one.
/// </param>
/// <param name="Neighbor">
/// -1 when the dependency is inside this displacement, otherwise the
/// <see cref="DispEdge"/> it reaches across.
/// </param>
public readonly record struct VertDependency(VertIndex Vert, int Neighbor)
{
    /// <summary>An empty slot: <c>(-1,-1)</c> with no neighbour.</summary>
    public static VertDependency Empty => new(VertIndex.Invalid, -1);

    /// <summary>Whether this slot holds a dependency at all.</summary>
    /// <remarks>
    /// <c>CVertDependency::IsValid</c> tests
    /// <b>only x</b>, which is reproduced exactly: the y component of an empty
    /// slot is never examined.
    /// </remarks>
    public bool IsValid => Vert.X != -1;
}

/// <summary>
/// The precalculated facts about one vertex of a displacement of a given
/// Power: <c>CVertInfo</c>.
/// </summary>
public sealed class VertInfo
{
    /// <summary>How many reverse-dependency slots a vertex has.</summary>
    /// <remarks><c>CVertInfo::NUM_REVERSE_DEPENDENCIES</c>, and four because a
    /// node has four side verts.</remarks>
    public const int ReverseDependencyCount = 4;

    /// <summary>The vertices this one needs in order to exist.</summary>
    public VertDependency[] Dependencies { get; } =
        [VertDependency.Empty, VertDependency.Empty];

    /// <summary>The vertices that name this one in their dependencies.</summary>
    public VertDependency[] ReverseDependencies { get; } =
    [
        VertDependency.Empty,
        VertDependency.Empty,
        VertDependency.Empty,
        VertDependency.Empty,
    ];

    /// <summary>
    /// The quad-tree level this vertex is a node of, root being 1, or -1 if it
    /// is not a node.
    /// </summary>
    public int NodeLevel { get; internal set; } = -1;

    /// <summary>
    /// The node above this one, or <see cref="VertIndex.Invalid"/> for the root
    /// and for non-nodes.
    /// </summary>
    public VertIndex Parent { get; internal set; } = VertIndex.Invalid;
}

/// <summary>Four vertex indices: <c>CFourVerts</c>.</summary>
public sealed class FourVerts
{
    /// <summary>The four indices.</summary>
    public VertIndex[] Verts { get; } = new VertIndex[4];
}

/// <summary>
/// One fully-tessellated triangle by vertex index: <c>CTriInfo</c>.
/// </summary>
/// <param name="A">The first vertex.</param>
/// <param name="B">The second.</param>
/// <param name="C">The third.</param>
public readonly record struct TriInfo(ushort A, ushort B, ushort C)
{
    /// <summary>The vertex at <paramref name="i"/>.</summary>
    /// <param name="i">0, 1 or 2.</param>
    /// <returns>That vertex index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="i"/> is not 0..2.
    /// </exception>
    public ushort this[int i] => i switch
    {
        0 => A,
        1 => B,
        2 => C,
        _ => throw new ArgumentOutOfRangeException(
            nameof(i), i, "a triangle has three vertices."),
    };
}

/// <summary>
/// One step of the winding a node walks while tessellating:
/// <c>CTesselateVert</c>.
/// </summary>
/// <param name="Index">The offset from the node, in units of the node's span.</param>
/// <param name="Node">
/// Which child node this step sits on, or -1 for the four side verts that
/// belong to no child.
/// </param>
public readonly record struct TesselateVert(VertIndex Index, int Node);

/// <summary>
/// Everything about displacements of one power that does not depend on the
/// Map: <c>CPowerInfo</c>.
/// </summary>
/// <remarks>
/// <para>
/// Stock builds these once into file-scope arrays sized <c>5x5</c>,
/// <c>9x9</c> and <c>17x17</c> and initialises them from a static
/// constructor object (<c>CPowerInfoInitializer</c>,
///). This does the same with a static
/// constructor, and the tables are immutable once built.
/// </para>
/// <para>
/// WHAT THIS CLASS REALLY IS is a dependency graph: which vertices must be
/// active for a given vertex to exist. The displacement's quad tree is not a
/// spatial index here — it is the order in which vertices are introduced, and
/// <see cref="ChildVerts"/> plus <see cref="SideVerts"/> is that order written
/// down. <c>SetupAllowedVerts</c> walks it to knock out the vertices a
/// lower-powered neighbour cannot match.
/// </para>
/// </remarks>
public sealed class PowerInfo
{
    private static readonly ImmutableArray<PowerInfo?> ByPower;

    /// <summary>
    /// Builds the three tables once, after every static field above has its
    /// value.
    /// </summary>
    /// <remarks>
    /// An explicit static constructor rather than an initialiser on
    /// <c>ByPower</c>: the build reads <see cref="TesselateWinding"/> and the
    /// child-node tables, and a field initialiser would run before theirs and
    /// see nulls. C# runs every static field initialiser in textual order and
    /// THEN the static constructor, so this is the one place the build is safe.
    /// </remarks>
    static PowerInfo() => ByPower = BuildAll();

    private PowerInfo(int power)
    {
        int sideLength = (1 << power) + 1;

        Power = power;
        SideLength = sideLength;
        SideLengthMinus1 = sideLength - 1;
        MidPoint = sideLength / 2;
        MaxVerts = sideLength * sideLength;
        RootNode = new VertIndex(sideLength / 2, sideLength / 2);

        VertInfos = new VertInfo[MaxVerts];
        for (int i = 0; i < MaxVerts; i++)
        {
            VertInfos[i] = new VertInfo();
        }

        SideVerts = NewFourVerts(MaxVerts);
        ChildVerts = NewFourVerts(MaxVerts);
        SideVertCorners = NewFourVerts(MaxVerts);
        ErrorEdges = new ushort[MaxVerts * 2];

        TriInfos = new TriInfo[(1 << power) * (1 << power) * 2];

        CornerPointIndices =
        [
            new VertIndex(0, 0),
            new VertIndex(0, sideLength - 1),
            new VertIndex(sideLength - 1, sideLength - 1),
            new VertIndex(sideLength - 1, 0),
        ];

        NodeIndexIncrements = new int[MaxMapDispPower];
        EdgeStartVerts = new VertIndex[4];
        EdgeIncrements = new VertIndex[4];
        NeighborStartVerts = new VertIndex[16];
        NeighborIncrements = new VertIndex[16];
    }

    /// <summary>The largest power the format allows.</summary>
    /// <remarks><c>MAX_MAP_DISP_POWER</c>.</remarks>
    public const int MaxMapDispPower = 4;

    /// <summary>The smallest power the format allows.</summary>
    /// <remarks><c>MIN_MAP_DISP_POWER</c>.</remarks>
    public const int MinMapDispPower = 2;

    /// <summary>The displacement power: 2, 3 or 4.</summary>
    public int Power { get; }

    /// <summary>Vertices along one side: <c>2^power + 1</c>.</summary>
    public int SideLength { get; }

    /// <summary>The last valid index along a side.</summary>
    public int SideLengthMinus1 { get; }

    /// <summary>The index of the middle vertex of a side.</summary>
    public int MidPoint { get; }

    /// <summary>The total vertex count, <see cref="SideLength"/> squared.</summary>
    public int MaxVerts { get; }

    /// <summary>The quad tree's root, which is the grid's centre vertex.</summary>
    public VertIndex RootNode { get; }

    /// <summary>The number of quad-tree nodes, children included.</summary>
    /// <remarks>5, 21 and 85 for powers 2, 3 and 4.</remarks>
    public int NodeCount { get; private set; }

    /// <summary>
    /// The per-vertex dependency graph, indexed by
    /// <see cref="VertIndexToInt"/>.
    /// </summary>
    public VertInfo[] VertInfos { get; }

    /// <summary>The four side vertices of each node.</summary>
    public FourVerts[] SideVerts { get; }

    /// <summary>The four child nodes of each node.</summary>
    public FourVerts[] ChildVerts { get; }

    /// <summary>
    /// The first corner beside each of a node's four side vertices.
    /// </summary>
    /// <remarks>
    /// Stock fills this with <c>m_Corner1</c> only — <c>m_Corner2</c> goes
    /// into <see cref="ErrorEdges"/> and nowhere else
    /// — so the name is broader than the
    /// contents.
    /// </remarks>
    public FourVerts[] SideVertCorners { get; }

    /// <summary>
    /// Two vertex indices per vertex, flattened: the pair whose midpoint this
    /// vertex's screen-space error is measured against.
    /// </summary>
    /// <remarks>
    /// Read as <c>ErrorEdges[v * 2 + 0]</c> and <c>+ 1</c>. Only the renderer's
    /// LOD uses it; it is carried because it is part of the table and because a
    /// gate over it is a cheap check that the recursion visited every node.
    /// </remarks>
    public ushort[] ErrorEdges { get; }

    /// <summary>
    /// Every triangle of the fully-tessellated displacement, in the quad
    /// tree's depth-first order.
    /// </summary>
    /// <remarks>
    /// <b>NOT the same order as the BSP's LUMP_DISP_TRIS.</b> This is the
    /// tessellator's order, which walks the quad tree; the lump's order is the
    /// row-major scan of <c>GenerateCollisionSurface</c>. The two produce the
    /// same set of triangles for a fully-active displacement and in a
    /// completely different sequence, and <c>FindTriIndexMapByUV</c>
    /// Writes THIS index into the lightmap sample
    /// positions — so a lightmap sample's triangle number indexes the quad-tree
    /// order and not the lump.
    /// </remarks>
    public TriInfo[] TriInfos { get; }

    /// <summary>The number of triangles, which is <see cref="TriInfos"/>'s length.</summary>
    public int NumTriInfos => TriInfos.Length;

    /// <summary>
    /// The gap between sibling nodes at each level in a preorder node array.
    /// </summary>
    public int[] NodeIndexIncrements { get; }

    /// <summary>The first vertex of each edge, indexed by <see cref="DispEdge"/>.</summary>
    public VertIndex[] EdgeStartVerts { get; }

    /// <summary>The step along each edge.</summary>
    public VertIndex[] EdgeIncrements { get; }

    /// <summary>
    /// The neighbour's first vertex on each edge under each orientation,
    /// flattened as <c>edge * 4 + orientation</c>.
    /// </summary>
    public VertIndex[] NeighborStartVerts { get; }

    /// <summary>The matching steps, flattened the same way.</summary>
    public VertIndex[] NeighborIncrements { get; }

    /// <summary>
    /// The winding a node walks while tessellating:
    /// <c>g_TesselateVerts</c>.
    /// </summary>
    /// <remarks>
    /// Nine entries for eight triangles — the first is repeated at the end to
    /// close the fan. The entries alternate between corners of the node, which
    /// carry a child index, and the four side verts, which carry -1; the
    /// tessellator uses that to decide whether to recurse or to emit.
    /// </remarks>
    public static ReadOnlySpan<TesselateVert> TesselateWinding => TesselateWindingStore.AsSpan();

    private static readonly ImmutableArray<TesselateVert> TesselateWindingStore =
    [
        new(new VertIndex(1, -1), ChildNodeLowerRight),
        new(new VertIndex(0, -1), -1),
        new(new VertIndex(-1, -1), ChildNodeLowerLeft),
        new(new VertIndex(-1, 0), -1),
        new(new VertIndex(-1, 1), ChildNodeUpperLeft),
        new(new VertIndex(0, 1), -1),
        new(new VertIndex(1, 1), ChildNodeUpperRight),
        new(new VertIndex(1, 0), -1),
        new(new VertIndex(1, -1), ChildNodeLowerRight),
    ];

    /// <summary><c>CHILDNODE_UPPER_RIGHT</c>.</summary>
    public const int ChildNodeUpperRight = 0;

    /// <summary><c>CHILDNODE_UPPER_LEFT</c>.</summary>
    public const int ChildNodeUpperLeft = 1;

    /// <summary><c>CHILDNODE_LOWER_LEFT</c>.</summary>
    public const int ChildNodeLowerLeft = 2;

    /// <summary><c>CHILDNODE_LOWER_RIGHT</c>.</summary>
    public const int ChildNodeLowerRight = 3;

    /// <summary>The four corners' vertex indices, by <see cref="DispCorner"/>.</summary>
    public VertIndex[] CornerPointIndices { get; }

    /// <summary>The table for one power.</summary>
    /// <param name="power">2, 3 or 4.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The power is outside 2..4. Stock asserts and then dereferences a null
    /// <c>g_PowerInfos[power]</c>, which is not a behaviour to reproduce.
    /// </exception>
    public static PowerInfo Get(int power)
    {
        if (power < MinMapDispPower || power > MaxMapDispPower)
        {
            throw new ArgumentOutOfRangeException(
                nameof(power),
                power,
                $"a displacement's power is {MinMapDispPower} to {MaxMapDispPower}.");
        }

        return ByPower[power]!;
    }

    /// <summary>A vertex index flattened to its position in a vertex array.</summary>
    /// <param name="index">The grid index.</param>
    /// <returns><c>y * sideLength + x</c>.</returns>
    public int VertIndexToInt(VertIndex index) => (index.Y * SideLength) + index.X;

    /// <summary>The corner at <paramref name="corner"/>.</summary>
    /// <param name="corner">A <see cref="DispCorner"/>.</param>
    /// <returns>Its grid index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not 0..3.</exception>
    public VertIndex CornerPointIndex(int corner)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(corner, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(corner, 3);
        return CornerPointIndices[corner];
    }

    /// <summary>The midpoint of one edge.</summary>
    /// <param name="edge">A <see cref="DispEdge"/>.</param>
    /// <returns>The grid index of that edge's middle vertex.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not 0..3.</exception>
    /// <remarks><c>CDispUtilsHelper::GetEdgeMidPoint</c>.</remarks>
    public VertIndex EdgeMidPoint(int edge)
    {
        int end = SideLength - 1;

        return edge switch
        {
            (int)DispEdge.Left => new VertIndex(0, MidPoint),
            (int)DispEdge.Top => new VertIndex(MidPoint, end),
            (int)DispEdge.Right => new VertIndex(end, MidPoint),
            (int)DispEdge.Bottom => new VertIndex(MidPoint, 0),
            _ => throw new ArgumentOutOfRangeException(
                nameof(edge), edge, "a displacement has four edges."),
        };
    }

    private static FourVerts[] NewFourVerts(int count)
    {
        FourVerts[] verts = new FourVerts[count];
        for (int i = 0; i < count; i++)
        {
            verts[i] = new FourVerts();
        }

        return verts;
    }

    private static ImmutableArray<PowerInfo?> BuildAll()
    {
        PowerInfo?[] infos = new PowerInfo?[MaxMapDispPower + 1];

        for (int power = MinMapDispPower; power <= MaxMapDispPower; power++)
        {
            PowerInfo info = new(power);
            info.Build();
            infos[power] = info;
        }

        return [.. infos];
    }

    /// <summary><c>InitPowerInfo</c>.</summary>
    private void Build()
    {
        int sideLength = SideLength;

        BuildRecursive(
            RootNode,
            new VertIndex(sideLength - 1, sideLength - 1),
            new VertIndex(0, 0),
            new VertIndex(0, 0),
            new VertIndex(sideLength - 1, sideLength - 1),
            VertIndex.Invalid,
            0);

        int triCursor = 0;
        BuildTriInfos(RootNode, ref triCursor, 0);

        for (int edge = 0; edge < 4; edge++)
        {
            EdgeStartVerts[edge] = EdgeVertIndex(sideLength, edge, 0);
            EdgeIncrements[edge] = EdgeVertIndex(sideLength, edge, 1) - EdgeStartVerts[edge];

            VertIndex nbStart = EdgeVertIndex(sideLength, (edge + 2) & 3, 0);
            VertIndex nbDelta = EdgeVertIndex(sideLength, (edge + 2) & 3, 1) - nbStart;

            for (int orient = 0; orient < 4; orient++)
            {
                NeighborStartVerts[(edge * 4) + orient] = Transform2D(
                    orient, nbStart, new VertIndex(sideLength / 2, sideLength / 2));

                NeighborIncrements[(edge * 4) + orient] = Transform2D(
                    orient, nbDelta, new VertIndex(0, 0));
            }
        }

        int curPowerOf4 = 1;
        int curTotal = 0;
        for (int i = 0; i < Power - 1; i++)
        {
            curTotal += curPowerOf4;
            NodeIndexIncrements[Power - i - 2] = curTotal;
            curPowerOf4 *= 4;
        }

        NodeCount = curTotal + curPowerOf4;
    }

    /// <summary><c>InitPowerInfo_R</c>.</summary>
    private void BuildRecursive(
        VertIndex nodeIndex,
        VertIndex dependency1,
        VertIndex dependency2,
        VertIndex nodeEdge1,
        VertIndex nodeEdge2,
        VertIndex parent,
        int level)
    {
        int iNode = VertIndexToInt(nodeIndex);

        VertInfos[iNode].Parent = parent;
        VertInfos[iNode].NodeLevel = level + 1;

        ErrorEdges[(iNode * 2) + 0] = (ushort)VertIndexToInt(nodeEdge1);
        ErrorEdges[(iNode * 2) + 1] = (ushort)VertIndexToInt(nodeEdge2);

        AddDependency(nodeIndex, dependency1, checkNeighborDependency: false);
        AddDependency(nodeIndex, dependency2, checkNeighborDependency: false);

        int vertInc = 1 << (Power - level - 1);

        for (int side = 0; side < 4; side++)
        {
            VertIndex sideVert = new(
                nodeIndex.X + (SideVertMul[side * 2] * vertInc),
                nodeIndex.Y + (SideVertMul[(side * 2) + 1] * vertInc));

            int iSideVert = VertIndexToInt(sideVert);

            SideVerts[iNode].Verts[side] = sideVert;

            VertIndex corner0 = new(
                nodeIndex.X + (SideVertCornerMul[side * 4] * vertInc),
                nodeIndex.Y + (SideVertCornerMul[(side * 4) + 1] * vertInc));
            VertIndex corner1 = new(
                nodeIndex.X + (SideVertCornerMul[(side * 4) + 2] * vertInc),
                nodeIndex.Y + (SideVertCornerMul[(side * 4) + 3] * vertInc));

            SideVertCorners[iNode].Verts[side] = corner0;

            ErrorEdges[(iSideVert * 2) + 0] = (ushort)VertIndexToInt(corner0);
            ErrorEdges[(iSideVert * 2) + 1] = (ushort)VertIndexToInt(corner1);

            AddDependency(sideVert, nodeIndex, checkNeighborDependency: true);
        }

        int nodeInc = vertInc >> 1;
        if (nodeInc == 0)
        {
            return;
        }

        for (int child = 0; child < 4; child++)
        {
            VertIndex mul = ChildNodeIndexMul[child];
            VertIndex childVert = new(
                nodeIndex.X + (mul.X * nodeInc), nodeIndex.Y + (mul.Y * nodeInc));

            ChildVerts[iNode].Verts[child] = childVert;

            VertIndex dep0 = ChildNodeDependencies[child * 2];
            VertIndex dep1 = ChildNodeDependencies[(child * 2) + 1];

            BuildRecursive(
                childVert,
                new VertIndex(
                    nodeIndex.X + (dep0.X * vertInc), nodeIndex.Y + (dep0.Y * vertInc)),
                new VertIndex(
                    nodeIndex.X + (dep1.X * vertInc), nodeIndex.Y + (dep1.Y * vertInc)),
                nodeIndex,
                new VertIndex(
                    nodeIndex.X + (mul.X * vertInc), nodeIndex.Y + (mul.Y * vertInc)),
                nodeIndex,
                level + 1);
        }
    }

    /// <summary><c>AddDependency</c>.</summary>
    /// <remarks>
    /// Both of stock's callers pass <c>bAddReverseDependency = true</c>, so the
    /// parameter is dropped and the reverse edge is always added.
    /// </remarks>
    private void AddDependency(
        VertIndex nodeIndex, VertIndex dependency, bool checkNeighborDependency)
    {
        int iNode = VertIndexToInt(nodeIndex);
        VertInfo node = VertInfos[iNode];

        node.Dependencies[FreeSlot(node.Dependencies)] = new VertDependency(dependency, -1);

        VertInfo dep = VertInfos[VertIndexToInt(dependency)];
        dep.ReverseDependencies[FreeSlot(dep.ReverseDependencies)] =
            new VertDependency(nodeIndex, -1);

        if (!checkNeighborDependency)
        {
            return;
        }

        int connection = DispTables.EdgeIndexFromPoint(nodeIndex, Power);
        if (connection == -1)
        {
            return;
        }

        // The reflection of the dependency through the node, wrapped around the
        // grid so it names the vertex it WOULD be if the neighbour's grid were
        // laid out in ours. Slot 1 unconditionally: stock asserts it is free
        // and then writes it regardless.
        VertIndex delta = nodeIndex - dependency;
        VertIndex reflected = nodeIndex + delta;

        node.Dependencies[1] = new VertDependency(
            WrapVertIndex(reflected, SideLength), connection);
    }

    /// <summary>
    /// <c>GetFreeDependency</c>: the first empty
    /// slot, or slot 0 when full.
    /// </summary>
    /// <remarks>
    /// Stock asserts on a full array and returns 0, which in a release build
    /// means the oldest dependency is overwritten. That is the behaviour vbsp
    /// ships with and is reproduced rather than thrown on, because the tables
    /// built here are compared byte for byte against a stock compile's
    /// consequences.
    /// </remarks>
    private static int FreeSlot(VertDependency[] slots)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (!slots[i].IsValid)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary><c>WrapVertIndex</c>.</summary>
    /// <remarks>
    /// The negative branch is <c>sideLength - 1 - ((-v) % sideLength)</c>,
    /// which is NOT the usual positive modulus: for <c>v = -1</c> and a side
    /// length of 5 it gives 3, where a modulus would give 4. That off-by-one is
    /// the point — the reflected index has to land on the neighbour's vertex
    /// one in from its edge, because the two grids share their edge vertices.
    /// </remarks>
    private static VertIndex WrapVertIndex(VertIndex index, int sideLength)
    {
        return new VertIndex(Wrap(index.X), Wrap(index.Y));

        int Wrap(int v)
        {
            if (v < 0)
            {
                return sideLength - 1 - (-v % sideLength);
            }

            if (v >= sideLength)
            {
                return v % sideLength;
            }

            return v;
        }
    }

    /// <summary><c>InitPowerInfoTriInfos_R</c>.</summary>
    private void BuildTriInfos(VertIndex nodeIndex, ref int cursor, int level)
    {
        int iNode = VertIndexToInt(nodeIndex);

        if (level + 1 < Power)
        {
            for (int child = 0; child < 4; child++)
            {
                BuildTriInfos(ChildVerts[iNode].Verts[child], ref cursor, level + 1);
            }

            return;
        }

        int vertInc = 1 << (Power - level - 1);

        ushort first = 0;
        int curTriVert = 0;

        ReadOnlySpan<TesselateVert> winding = TesselateWinding;
        for (int i = 0; i < winding.Length; i++)
        {
            VertIndex sideVert = nodeIndex.Offset(winding[i].Index, vertInc);

            if (curTriVert == 1)
            {
                TriInfos[cursor++] = new TriInfo(
                    first, (ushort)VertIndexToInt(sideVert), (ushort)iNode);
            }

            first = (ushort)VertIndexToInt(sideVert);
            curTriVert = 1;
        }
    }

    /// <summary><c>GetEdgeVertIndex</c>.</summary>
    private static VertIndex EdgeVertIndex(int sideLength, int edge, int vert) => edge switch
    {
        (int)DispEdge.Right => new VertIndex(sideLength - 1, vert),
        (int)DispEdge.Top => new VertIndex(vert, sideLength - 1),
        (int)DispEdge.Left => new VertIndex(0, vert),

        // Stock's final `else`, which is the bottom edge.
        _ => new VertIndex(vert, 0),
    };

    /// <summary><c>Transform2D</c>.</summary>
    private static VertIndex Transform2D(int orientation, VertIndex vert, VertIndex centre)
    {
        VertIndex translated = vert - centre;

        ReadOnlySpan<int> m = OrientationRotations.Slice(orientation * 4, 4);

        VertIndex transformed = new(
            (translated.X * m[0]) + (translated.Y * m[1]),
            (translated.X * m[2]) + (translated.Y * m[3]));

        return transformed + centre;
    }

    /// <summary><c>g_SideVertMul</c>.</summary>
    private static ReadOnlySpan<short> SideVertMul => [1, 0, 0, 1, -1, 0, 0, -1];

    /// <summary><c>g_SideVertCorners</c>, flattened.</summary>
    private static ReadOnlySpan<short> SideVertCornerMul =>
    [
        1, -1, 1, 1,
        1, 1, -1, 1,
        -1, 1, -1, -1,
        -1, -1, 1, -1,
    ];

    /// <summary><c>g_OrientationRotations</c>, flattened.</summary>
    private static ReadOnlySpan<int> OrientationRotations =>
    [
        1, 0, 0, 1,
        0, 1, -1, 0,
        -1, 0, 0, -1,
        0, -1, 1, 0,
    ];

    /// <summary><c>g_ChildNodeIndexMul</c>.</summary>
    private static readonly ImmutableArray<VertIndex> ChildNodeIndexMul =
    [
        new(1, 1),
        new(-1, 1),
        new(-1, -1),
        new(1, -1),
    ];

    /// <summary><c>g_ChildNodeDependencies</c>, flattened.</summary>
    private static readonly ImmutableArray<VertIndex> ChildNodeDependencies =
    [
        new(1, 0), new(0, 1),
        new(0, 1), new(-1, 0),
        new(-1, 0), new(0, -1),
        new(0, -1), new(1, 0),
    ];
}
