using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The per-power tables: the reference implementation's recursion, checked
/// against the shapes it is supposed to produce.
/// </summary>
/// <remarks>
/// These tables have no stock reference to compare against — they never reach
/// a file — so each fact here states a property the recursion must have rather
/// than a value someone recorded. A table that is wrong in a way all of these
/// tolerate would still be caught downstream, because
/// <c>SetupAllowedVerts</c> and the lightmap sample positions both read it and
/// both are gated against stock.
/// </remarks>
public sealed class PowerInfoTests
{
    /// <summary>The three powers the format allows.</summary>
    public static TheoryData<int> Powers => [2, 3, 4];

    [Theory]
    [MemberData(nameof(Powers))]
    public void TheSideLengthIsTwoToThePowerPlusOne(int power)
    {
        Assert.Equal((1 << power) + 1, PowerInfo.Get(power).SideLength);
    }

    [Theory]
    [MemberData(nameof(Powers))]
    public void TheRootNodeIsTheCentreVertex(int power)
    {
        PowerInfo info = PowerInfo.Get(power);
        int mid = info.SideLength / 2;

        Assert.Equal(new VertIndex(mid, mid), info.RootNode);
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(3, 21)]
    [InlineData(4, 85)]
    public void TheNodeCountIsTheSumOfThePowersOfFour(int power, int expected)
    {
        Assert.Equal(expected, PowerInfo.Get(power).NodeCount);
    }

    /// <summary>
    /// <c>CCoreDispInfo::MAX_NODE_COUNT</c> is 85, which is the power-4 count
    /// and therefore the largest.
    /// </summary>
    [Fact]
    public void TheLargestNodeCountIsTheOneBuilddispReserves()
    {
        Assert.Equal(85, PowerInfo.Get(4).NodeCount);
    }

    [Theory]
    [MemberData(nameof(Powers))]
    public void ThereAreTwoTrianglesPerGridSquare(int power)
    {
        Assert.Equal((1 << power) * (1 << power) * 2, PowerInfo.Get(power).NumTriInfos);
    }

    /// <summary>
    /// The tessellated triangles cover every grid square exactly twice over —
    /// once per triangle — so every vertex that is not on the boundary appears
    /// six times.
    /// </summary>
    /// <remarks>
    /// The property that catches a recursion which visited a node twice or
    /// skipped one: a missing leaf loses eight triangles and the counts of its
    /// nine vertices all drop together.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Powers))]
    public void EveryVertexIsUsedByTheTriangleList(int power)
    {
        PowerInfo info = PowerInfo.Get(power);
        int[] uses = new int[info.MaxVerts];

        foreach (TriInfo tri in info.TriInfos)
        {
            uses[tri.A]++;
            uses[tri.B]++;
            uses[tri.C]++;
        }

        for (int v = 0; v < info.MaxVerts; v++)
        {
            Assert.True(uses[v] > 0, $"power {power}: vertex {v} is in no triangle");
        }
    }

    /// <summary>
    /// The triangle list's total area, counted in grid squares, is the whole
    /// grid.
    /// </summary>
    /// <remarks>
    /// A stronger statement than the use count: it fails for a list that
    /// covers some squares twice and others not at all, which a per-vertex
    /// count cannot see.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Powers))]
    public void TheTrianglesCoverTheGridExactlyOnce(int power)
    {
        PowerInfo info = PowerInfo.Get(power);
        int side = info.SideLength;

        double area = 0;

        foreach (TriInfo tri in info.TriInfos)
        {
            (int ax, int ay) = (tri.A % side, tri.A / side);
            (int bx, int by) = (tri.B % side, tri.B / side);
            (int cx, int cy) = (tri.C % side, tri.C / side);

            area += Math.Abs((((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax))) / 2.0);
        }

        Assert.Equal((1 << power) * (1 << power), area, 6);
    }

    [Theory]
    [MemberData(nameof(Powers))]
    public void TheCornerIndicesAreTheGridsFourCorners(int power)
    {
        PowerInfo info = PowerInfo.Get(power);
        int last = info.SideLength - 1;

        Assert.Equal(new VertIndex(0, 0), info.CornerPointIndex((int)DispCorner.LowerLeft));
        Assert.Equal(new VertIndex(0, last), info.CornerPointIndex((int)DispCorner.UpperLeft));
        Assert.Equal(new VertIndex(last, last), info.CornerPointIndex((int)DispCorner.UpperRight));
        Assert.Equal(new VertIndex(last, 0), info.CornerPointIndex((int)DispCorner.LowerRight));
    }

    [Theory]
    [MemberData(nameof(Powers))]
    public void TheRootNodesFourChildrenAreTheQuadrantCentres(int power)
    {
        PowerInfo info = PowerInfo.Get(power);
        int mid = info.SideLength / 2;
        int quarter = mid / 2;

        if (quarter == 0)
        {
            // Power 2's root has children one step away, which the general
            // expression below also gives; stated so the case is not silently
            // skipped.
            quarter = 1;
        }

        VertIndex[] children = info.ChildVerts[info.VertIndexToInt(info.RootNode)].Verts;

        Assert.Equal(new VertIndex(mid + quarter, mid + quarter), children[0]);
        Assert.Equal(new VertIndex(mid - quarter, mid + quarter), children[1]);
        Assert.Equal(new VertIndex(mid - quarter, mid - quarter), children[2]);
        Assert.Equal(new VertIndex(mid + quarter, mid - quarter), children[3]);
    }

    /// <summary>
    /// The root node's four side vertices are the four edge midpoints.
    /// </summary>
    [Theory]
    [MemberData(nameof(Powers))]
    public void TheRootNodesSideVertsAreTheEdgeMidpoints(int power)
    {
        PowerInfo info = PowerInfo.Get(power);
        int mid = info.SideLength / 2;
        int last = info.SideLength - 1;

        VertIndex[] sides = info.SideVerts[info.VertIndexToInt(info.RootNode)].Verts;

        Assert.Contains(new VertIndex(last, mid), sides);
        Assert.Contains(new VertIndex(mid, last), sides);
        Assert.Contains(new VertIndex(0, mid), sides);
        Assert.Contains(new VertIndex(mid, 0), sides);
    }

    /// <summary>
    /// Every vertex that is a node names a parent, and only the root does not.
    /// </summary>
    [Theory]
    [MemberData(nameof(Powers))]
    public void OnlyTheRootNodeHasNoParent(int power)
    {
        PowerInfo info = PowerInfo.Get(power);

        int rootless = 0;

        for (int v = 0; v < info.MaxVerts; v++)
        {
            VertInfo vert = info.VertInfos[v];

            if (vert.NodeLevel == -1)
            {
                continue;
            }

            if (vert.Parent.X == -1)
            {
                rootless++;
                Assert.Equal(info.VertIndexToInt(info.RootNode), v);
            }
        }

        Assert.Equal(1, rootless);
    }

    /// <summary>
    /// A dependency that reaches past an edge is wrapped to the far side of
    /// the grid, one vertex in.
    /// </summary>
    /// <remarks>
    /// <c>WrapVertIndex</c>'s negative branch is
    /// <c>sideLength - 1 - ((-v) % sideLength)</c>, which for <c>v = -1</c> and
    /// a five-vertex side gives 3 and not 4. Off by one from a plain modulus,
    /// deliberately: the two grids SHARE their edge vertices, so the vertex one
    /// step outside ours is the neighbour's second row, not its first.
    /// </remarks>
    [Fact]
    public void AnEdgeVertexsNeighbourDependencyWrapsOneVertexIn()
    {
        PowerInfo info = PowerInfo.Get(2);

        // The middle of the left edge: a side vert of the root, on edge LEFT.
        VertIndex edgeVert = new(0, 2);
        VertInfo vert = info.VertInfos[info.VertIndexToInt(edgeVert)];

        VertDependency across = vert.Dependencies[1];

        Assert.True(across.IsValid);
        Assert.Equal((int)DispEdge.Left, across.Neighbor);

        // The dependency inside the grid is the root (2,2); reflecting (0,2)
        // through it gives (-2,2), which wraps to (5-1-2, 2) = (2,2).
        Assert.Equal(new VertIndex(2, 2), across.Vert);
    }

    /// <summary>
    /// A vertex that depends on another appears in that other's reverse
    /// dependencies.
    /// </summary>
    /// <remarks>
    /// The invariant <c>UnallowVerts_R</c> walks. Only the INTERNAL
    /// dependencies are checked, because those are the only ones the recursion
    /// adds a reverse edge for: the across-the-edge dependency in slot 1 is
    /// written directly and has no counterpart.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Powers))]
    public void EveryInternalDependencyHasItsReverse(int power)
    {
        PowerInfo info = PowerInfo.Get(power);

        for (int v = 0; v < info.MaxVerts; v++)
        {
            foreach (VertDependency dep in info.VertInfos[v].Dependencies)
            {
                if (!dep.IsValid || dep.Neighbor != -1)
                {
                    continue;
                }

                VertInfo target = info.VertInfos[info.VertIndexToInt(dep.Vert)];
                VertIndex self = new(v % info.SideLength, v / info.SideLength);

                Assert.Contains(
                    target.ReverseDependencies,
                    r => r.IsValid && r.Neighbor == -1 && r.Vert == self);
            }
        }
    }

    /// <summary>
    /// The tessellation winding is nine steps closing back on its first.
    /// </summary>
    [Fact]
    public void TheTesselateWindingClosesOnItself()
    {
        ReadOnlySpan<TesselateVert> winding = PowerInfo.TesselateWinding;

        Assert.Equal(9, winding.Length);
        Assert.Equal(winding[0], winding[^1]);
    }

    /// <summary>
    /// The winding alternates between corners, which carry a child index, and
    /// side vertices, which carry -1.
    /// </summary>
    [Fact]
    public void TheTesselateWindingAlternatesCornersAndSides()
    {
        ReadOnlySpan<TesselateVert> winding = PowerInfo.TesselateWinding;

        for (int i = 0; i < winding.Length; i++)
        {
            bool isCorner = i % 2 == 0;
            Assert.Equal(isCorner, winding[i].Node != -1);
        }
    }

    [Fact]
    public void APowerOutsideTwoToFourIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerInfo.Get(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerInfo.Get(5));
    }
}
