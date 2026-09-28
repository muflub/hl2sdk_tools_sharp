//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>The cell octree: built, expanded, merged, padded, turned, and a malformed tree refused.</summary>
public sealed class NavOctreeTests
{
    private const ushort Free = NavVoxelGrid.FreeBit;

    private static ushort[] Grid(int n, Func<int, int, int, ushort> code)
    {
        ushort[] dense = new ushort[n * n * n];
        for (int z = 0; z < n; z++)
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    dense[(((z * n) + y) * n) + x] = code(x, y, z);
                }
            }
        }

        return dense;
    }

    [Fact]
    public void AUniformCellIsOneNode()
    {
        (uint[] nodes, RoomNavLeaf[] leaves) = NavOctree.Build(Grid(16, (_, _, _) => Free), 16);
        Assert.Equal([Nav3dFormat.Node(Nav3dNodeKind.Free, 0)], nodes);
        Assert.Equal([new RoomNavLeaf(0, 0, 0, 4, Nav3dLeafFlags.None)], leaves);

        (nodes, leaves) = NavOctree.Build(Grid(16, (_, _, _) => NavVoxelGrid.Blocked), 16);
        Assert.Equal([Nav3dFormat.Node(Nav3dNodeKind.Blocked, 0)], nodes);
        Assert.Empty(leaves);
    }

    [Fact]
    public void OpenSpaceMergesIntoLargeLeavesAndFlaggedVoxelsStaySmall()
    {
        // A floor layer at z = 0 with flags, open above.
        ushort[] dense = Grid(16, (_, _, z) => z == 0 ? (ushort)(Free | (ushort)Nav3dLeafFlags.Floor) : Free);
        (uint[] nodes, RoomNavLeaf[] leaves) = NavOctree.Build(dense, 16);
        Assert.Contains(leaves, l => l.SizeLog2 == 3 && l.Z == 8);
        Assert.Contains(leaves, l => l.SizeLog2 == 0 && l.Z == 1);
        Assert.All(leaves.Where(l => l.Flags == Nav3dLeafFlags.Floor), l => Assert.True(l.Z == 0 && l.SizeLog2 == 0));
        Assert.Equal(dense, NavOctree.Expand(nodes, leaves, 16));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(16)]
    public void AnyGridRoundTripsAndCellsThatAreNotAPowerOfTwoArePadded(int n)
    {
        ushort[] dense = Grid(n, (x, y, z) => (x * 7 + y * 3 + z) % 5 == 0 ? NavVoxelGrid.Blocked : (ushort)(Free | ((x + z) % 3)));
        (uint[] nodes, RoomNavLeaf[] leaves) = NavOctree.Build(dense, n);
        Assert.Equal(dense, NavOctree.Expand(nodes, leaves, n));
        int[] map = NavOctree.LeafMap(nodes, leaves.Length, n);
        Assert.Equal(dense.Select(c => (c & Free) != 0), map.Select(l => l >= 0));
        Assert.Equal((n & (n - 1)) != 0, nodes.Any(w => Nav3dFormat.KindOf(w) == Nav3dNodeKind.Outside));
    }

    [Fact]
    public void TurningMovesVoxelsAndSideBitsAQuarterAtATime()
    {
        ushort[] dense = Grid(4, (x, y, z) => x == 3 && y == 0 && z == 1 ? (ushort)(Free | (ushort)(Nav3dLeafFlags.SidePositiveX | Nav3dLeafFlags.Floor)) : NavVoxelGrid.Blocked);
        ushort[] turned = NavOctree.Turn(dense, 4, 1);

        // (3, 0) turns to (4 − 1 − 0, 3) = (3, 3); east becomes north.
        Assert.Equal(Free | (ushort)(Nav3dLeafFlags.SidePositiveY | Nav3dLeafFlags.Floor), turned[(((1 * 4) + 3) * 4) + 3]);
        Assert.Equal(dense, NavOctree.Turn(NavOctree.Turn(turned, 4, 2), 4, 1));
        Assert.Equal(dense, NavOctree.Turn(dense, 4, -4));
        Assert.Equal(Nav3dLeafFlags.SideNegativeY | Nav3dLeafFlags.Door, NavOctree.TurnFlags(Nav3dLeafFlags.SidePositiveX | Nav3dLeafFlags.Door, 3));
        Assert.Equal(Nav3dLeafFlags.SidePositiveX, NavOctree.TurnFlags(Nav3dLeafFlags.SideNegativeY, 1));
        Assert.Equal((3, 3), NavOctree.TurnVoxel(3, 0, 4, 1));
        Assert.Equal((0, 3), NavOctree.TurnVoxel(3, 0, 4, 2));
    }

    [Fact]
    public void TurningAVoxelMatchesTheRoomTransform()
    {
        RoomTransform transform = new(new RoomPlacement("r", 0, 0, 1), 64);
        for (int x = 0; x < 4; x++)
        {
            for (int y = 0; y < 4; y++)
            {
                Vec3 centre = transform.Apply(new Vec3((x * 16) + 8, (y * 16) + 8, 0));
                Assert.Equal(((int)(centre.X / 16), (int)(centre.Y / 16)), NavOctree.TurnVoxel(x, y, 4, 1));
            }
        }
    }

    [Fact]
    public void AMalformedTreeIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => NavOctree.LeafMap([], 0, 4));
        Assert.Throws<InvalidDataException>(() => NavOctree.LeafMap([Nav3dFormat.Node(Nav3dNodeKind.Inner, 0)], 0, 4));
        Assert.Throws<InvalidDataException>(() => NavOctree.LeafMap([Nav3dFormat.Node(Nav3dNodeKind.Inner, 5)], 0, 4));
        Assert.Throws<InvalidDataException>(() => NavOctree.LeafMap([Nav3dFormat.Node(Nav3dNodeKind.Free, 3)], 1, 4));
        Assert.Throws<InvalidDataException>(() => NavOctree.LeafMap([Nav3dFormat.Node(Nav3dNodeKind.Inner, 1), .. Enumerable.Repeat(Nav3dFormat.Node(Nav3dNodeKind.Inner, 1), 8)], 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavOctree.Build(new ushort[7], 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavOctree.Build(new ushort[0], 0));
    }
}
