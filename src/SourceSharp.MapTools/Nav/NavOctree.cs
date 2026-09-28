//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Nav;

namespace SourceSharp.MapTools.Nav;

/// <summary>A free leaf of one room's octree: its low corner in the cell's voxels, its size and its flags.</summary>
/// <param name="X">The low corner along x, 0 to 127.</param>
/// <param name="Y">Along y.</param>
/// <param name="Z">Along z.</param>
/// <param name="SizeLog2">Its edge is <c>2^SizeLog2</c> voxels.</param>
/// <param name="Flags">What it touches.</param>
public readonly record struct RoomNavLeaf(byte X, byte Y, byte Z, byte SizeLog2, Nav3dLeafFlags Flags);

/// <summary>
/// The sparse voxel octree of one cell: built from a dense grid of voxel
/// codes, expanded back into one, and turned by quarter turns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> The node words are the <c>.nav3d</c> file's own
/// (<see cref="Nav3dFormat.Node"/>): an inner node's eight children sit back
/// to back from its payload, in octant order (bit 0 the high half along x,
/// bit 1 along y, bit 2 along z); a free leaf's payload is its leaf index. The
/// root is node 0 and covers <c>2^D</c> voxels a side from the cell's low
/// corner, D the least depth whose cube holds the cell; voxels past the
/// cell, when its voxel count is not a power of two, are
/// <see cref="Nav3dNodeKind.Outside"/>.
/// </para>
/// <para>
/// <b>Merging.</b> Eight siblings merge into their parent when all are
/// blocked, all outside, or all free with the same flags. Open space away
/// from every surface has no flags, so it merges into large leaves; space
/// next to a surface stays at the voxel size, where its flags differ. The
/// build is one bottom-up pass for the merge codes and one top-down pass
/// that writes the nodes depth first, children allocated before they are
/// filled, so the same grid gives the same words every time.
/// </para>
/// </remarks>
public static class NavOctree
{
    /// <summary>A pyramid code meaning "not uniform": the node has children.</summary>
    private const int Mixed = -1;

    /// <summary>A pyramid code for a voxel beyond the cell.</summary>
    private const int OutsideCode = 0xFFFF;

    /// <summary>Builds the octree of a dense grid.</summary>
    /// <param name="dense">The voxel codes (<see cref="NavVoxelGrid.Blocked"/> or <see cref="NavVoxelGrid.FreeBit"/> with flags), x fastest; <c>n³</c> of them.</param>
    /// <param name="n">Voxels along the cell's edge, 1 to <see cref="NavSettings.MaxCellVoxels"/>.</param>
    /// <returns>The node words and the free leaves.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (uint[] Nodes, RoomNavLeaf[] Leaves) Build(ReadOnlySpan<ushort> dense, int n)
    {
        CheckSize(n, dense.Length);
        int depth = Nav3dFormat.DepthFor(n);

        // levels[l] holds the merge code of every node of edge 2^l voxels.
        int[][] levels = new int[depth + 1][];
        int side = 1 << depth;
        levels[0] = new int[side * side * side];
        for (int z = 0; z < side; z++)
        {
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    levels[0][(((z * side) + y) * side) + x] = x < n && y < n && z < n
                        ? dense[(((z * n) + y) * n) + x]
                        : OutsideCode;
                }
            }
        }

        for (int l = 1; l <= depth; l++)
        {
            int below = side >> (l - 1);
            int here = side >> l;
            int[] codes = new int[here * here * here];
            int[] lower = levels[l - 1];
            for (int z = 0; z < here; z++)
            {
                for (int y = 0; y < here; y++)
                {
                    for (int x = 0; x < here; x++)
                    {
                        int first = lower[((((2 * z) * below) + (2 * y)) * below) + (2 * x)];
                        int code = first;
                        for (int c = 1; c < 8 && code != Mixed; c++)
                        {
                            int cx = (2 * x) + (c & 1);
                            int cy = (2 * y) + ((c >> 1) & 1);
                            int cz = (2 * z) + ((c >> 2) & 1);
                            if (lower[(((cz * below) + cy) * below) + cx] != first)
                            {
                                code = Mixed;
                            }
                        }

                        codes[(((z * here) + y) * here) + x] = code;
                    }
                }
            }

            levels[l] = codes;
        }

        List<uint> nodes = [0];
        List<RoomNavLeaf> leaves = [];
        Emit(levels, depth, depth, 0, 0, 0, 0, nodes, leaves);
        return ([.. nodes], [.. leaves]);
    }

    /// <summary>Expands an octree back into the dense grid it was built from.</summary>
    /// <param name="nodes">The node words.</param>
    /// <param name="leaves">The free leaves.</param>
    /// <param name="n">Voxels along the cell's edge.</param>
    /// <returns>The voxel codes, x fastest.</returns>
    /// <exception cref="InvalidDataException">A node points outside the nodes or leaves, or the tree is deeper than the cell allows.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ushort[] Expand(ReadOnlySpan<uint> nodes, ReadOnlySpan<RoomNavLeaf> leaves, int n)
    {
        CheckSize(n, n * n * n);
        ushort[] dense = new ushort[n * n * n];
        int[] map = LeafMap(nodes, leaves.Length, n);
        for (int i = 0; i < dense.Length; i++)
        {
            dense[i] = map[i] < 0 ? NavVoxelGrid.Blocked : (ushort)(NavVoxelGrid.FreeBit | (ushort)leaves[map[i]].Flags);
        }

        return dense;
    }

    /// <summary>Which leaf holds each voxel of the cell.</summary>
    /// <param name="nodes">The node words.</param>
    /// <param name="leafCount">How many leaves the tree has.</param>
    /// <param name="n">Voxels along the cell's edge.</param>
    /// <returns>Each voxel's leaf index, or -1 where the voxel is blocked; x fastest.</returns>
    /// <exception cref="InvalidDataException">A node points outside the nodes or leaves, or the tree is deeper than the cell allows.</exception>
    public static int[] LeafMap(ReadOnlySpan<uint> nodes, int leafCount, int n)
    {
        CheckSize(n, n * n * n);
        if (nodes.Length == 0)
        {
            throw new InvalidDataException("an octree has at least its root.");
        }

        int[] map = new int[n * n * n];
        Fill(nodes, leafCount, n, 0, 1 << Nav3dFormat.DepthFor(n), 0, 0, 0, map);
        return map;
    }

    /// <summary>A dense grid turned by quarter turns counter-clockwise about +z, about the cell's centre line.</summary>
    /// <param name="dense">The codes, x fastest.</param>
    /// <param name="n">Voxels along the cell's edge.</param>
    /// <param name="quarterTurns">0 to 3.</param>
    /// <returns>The turned grid; its free voxels' side bits turned with it.</returns>
    /// <remarks>
    /// The cell turns onto itself: voxel <c>(x, y, z)</c> goes to
    /// <c>(n − 1 − y, x, z)</c> each quarter turn, the index form of
    /// <see cref="Rooms.RoomTransform"/>'s <c>(x, y) → (cell − y, x)</c>.
    /// Only the side bits change meaning: the east side becomes the north,
    /// and so on round.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ushort[] Turn(ReadOnlySpan<ushort> dense, int n, int quarterTurns)
    {
        CheckSize(n, dense.Length);
        int r = ((quarterTurns % 4) + 4) % 4;
        ushort[] turned = new ushort[dense.Length];
        for (int z = 0; z < n; z++)
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    (int tx, int ty) = TurnVoxel(x, y, n, r);
                    ushort code = dense[(((z * n) + y) * n) + x];
                    if ((code & NavVoxelGrid.FreeBit) != 0)
                    {
                        code = (ushort)(NavVoxelGrid.FreeBit | (ushort)TurnFlags((Nav3dLeafFlags)(code & 0xFF), r));
                    }

                    turned[(((z * n) + ty) * n) + tx] = code;
                }
            }
        }

        return turned;
    }

    /// <summary>Where a voxel column goes under quarter turns.</summary>
    /// <param name="x">The voxel along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="n">Voxels along the cell's edge.</param>
    /// <param name="quarterTurns">0 to 3.</param>
    /// <returns>The turned column.</returns>
    public static (int X, int Y) TurnVoxel(int x, int y, int n, int quarterTurns)
    {
        int r = ((quarterTurns % 4) + 4) % 4;
        for (int t = 0; t < r; t++)
        {
            (x, y) = (n - 1 - y, x);
        }

        return (x, y);
    }

    /// <summary>Flags turned by quarter turns: the side bits rotate east, north, west, south; the rest stay.</summary>
    /// <param name="flags">The flags.</param>
    /// <param name="quarterTurns">0 to 3.</param>
    /// <returns>The turned flags.</returns>
    public static Nav3dLeafFlags TurnFlags(Nav3dLeafFlags flags, int quarterTurns)
    {
        int r = ((quarterTurns % 4) + 4) % 4;
        int sides = ((int)flags >> 3) & 0xF;
        sides = ((sides << r) | (sides >> (4 - r))) & 0xF;
        return (Nav3dLeafFlags)(((int)flags & ~(0xF << 3)) | (sides << 3));
    }

    private static void Emit(
        int[][] levels, int depth, int level, int x, int y, int z, int slot, List<uint> nodes, List<RoomNavLeaf> leaves)
    {
        int side = (1 << depth) >> level;
        int code = levels[level][(((z * side) + y) * side) + x];
        if (code != Mixed)
        {
            nodes[slot] = code switch
            {
                OutsideCode => Nav3dFormat.Node(Nav3dNodeKind.Outside, 0),
                NavVoxelGrid.Blocked => Nav3dFormat.Node(Nav3dNodeKind.Blocked, 0),
                _ => Leaf(code, level, x, y, z, leaves),
            };
            return;
        }

        int first = nodes.Count;
        nodes[slot] = Nav3dFormat.Node(Nav3dNodeKind.Inner, (uint)first);
        for (int c = 0; c < 8; c++)
        {
            nodes.Add(0);
        }

        for (int c = 0; c < 8; c++)
        {
            Emit(levels, depth, level - 1, (2 * x) + (c & 1), (2 * y) + ((c >> 1) & 1), (2 * z) + ((c >> 2) & 1), first + c, nodes, leaves);
        }
    }

    private static uint Leaf(int code, int level, int x, int y, int z, List<RoomNavLeaf> leaves)
    {
        int index = leaves.Count;
        leaves.Add(new RoomNavLeaf(
            (byte)(x << level), (byte)(y << level), (byte)(z << level), (byte)level, (Nav3dLeafFlags)(code & 0xFF)));
        return Nav3dFormat.Node(Nav3dNodeKind.Free, (uint)index);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Fill(ReadOnlySpan<uint> nodes, int leafCount, int n, int node, int size, int x0, int y0, int z0, int[] map)
    {
        uint word = nodes[node];
        Nav3dNodeKind kind = Nav3dFormat.KindOf(word);
        if (kind == Nav3dNodeKind.Inner)
        {
            int first = (int)Nav3dFormat.PayloadOf(word);
            if (size == 1 || first + 8 > nodes.Length || first <= node)
            {
                throw new InvalidDataException($"octree node {node} has children outside the tree or below a voxel.");
            }

            int half = size >> 1;
            for (int c = 0; c < 8; c++)
            {
                Fill(nodes, leafCount, n, first + c, half,
                    x0 + ((c & 1) * half), y0 + (((c >> 1) & 1) * half), z0 + (((c >> 2) & 1) * half), map);
            }

            return;
        }

        int value = -1;
        if (kind == Nav3dNodeKind.Free)
        {
            value = (int)Nav3dFormat.PayloadOf(word);
            if (value >= leafCount)
            {
                throw new InvalidDataException($"octree node {node} names leaf {value} of {leafCount}.");
            }
        }

        for (int z = z0; z < Math.Min(n, z0 + size); z++)
        {
            for (int y = y0; y < Math.Min(n, y0 + size); y++)
            {
                for (int x = x0; x < Math.Min(n, x0 + size); x++)
                {
                    map[(((z * n) + y) * n) + x] = value;
                }
            }
        }
    }

    private static void CheckSize(int n, int length)
    {
        if (n < 1 || n > NavSettings.MaxCellVoxels || length != n * n * n)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, $"a cell of 1 to {NavSettings.MaxCellVoxels} voxels a side, with n³ codes.");
        }
    }
}
