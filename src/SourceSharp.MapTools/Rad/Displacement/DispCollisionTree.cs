//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// The quad-tree of axis-aligned boxes over one displacement's triangles:
/// <c>CDispCollTree</c>'s <c>AABBTree_CreateLeafs</c>,
/// <c>AABBTree_GenerateBoxes_r</c> and <c>AABBTree_CalcBounds</c>
/// </summary>
/// <remarks>
/// <para>
/// Each node holds the boxes of its FOUR children (<c>CDispCollNode</c> is two
/// <c>FourVectors</c>); node <c>i</c>'s children are <c>4i + 1 .. 4i + 4</c>
/// (<c>Nodes_GetChild</c>), and an index at or past the node count is a leaf.
/// A leaf is one grid cell -- its two triangles -- and leaves are numbered by
/// interleaving the cell's column and row bits (<c>Nodes_GetIndexFromComponents</c>),
/// so each node's four children are the four quadrants of its cell block.
/// </para>
/// <para>
/// The tree's bounds are the root box grown by one unit on every side
/// (<c>AABBTree_CalcBounds</c>'s "bloat"); <c>INCLUDE_SURFACE_IN_BOUNDS</c> is
/// not defined in this drop, so the base quad does not contribute.
/// </para>
/// </remarks>
public sealed class DispCollisionTree
{
    private readonly Vec3[] _childMins;
    private readonly Vec3[] _childMaxs;
    private readonly short[] _leafTris;

    private DispCollisionTree(int nodeCount, Vec3[] childMins, Vec3[] childMaxs, short[] leafTris, Vec3 mins, Vec3 maxs)
    {
        NodeCount = nodeCount;
        _childMins = childMins;
        _childMaxs = childMaxs;
        _leafTris = leafTris;
        Mins = mins;
        Maxs = maxs;
    }

    /// <summary><c>m_nodes.Count()</c>: interior nodes only.</summary>
    public int NodeCount { get; }

    /// <summary>The leaf count: one per grid cell.</summary>
    public int LeafCount => _leafTris.Length / 2;

    /// <summary><c>m_mins</c>: the bounds, bloated by one unit.</summary>
    public Vec3 Mins { get; }

    /// <summary><c>m_maxs</c>.</summary>
    public Vec3 Maxs { get; }

    /// <summary><c>Nodes_CalcCount</c>: interior nodes plus leaves for a power.</summary>
    /// <param name="power">2..4 (1 is legal too).</param>
    /// <returns><c>(1 &lt;&lt; ((power + 1) &lt;&lt; 1)) / 3</c>.</returns>
    public static int CalcCount(int power) => (1 << ((power + 1) << 1)) / 3;

    /// <summary><c>Nodes_GetIndexFromComponents</c>: interleave x's bits (even) with y's (odd).</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <returns>The Morton index.</returns>
    public static int IndexFromComponents(int x, int y)
    {
        int index = 0;
        for (int shift = 0; x != 0; shift += 2, x >>= 1)
        {
            index |= (x & 1) << shift;
        }

        for (int shift = 1; y != 0; shift += 2, y >>= 1)
        {
            index |= (y & 1) << shift;
        }

        return index;
    }

    /// <summary>One child box of an interior node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="child">0..3.</param>
    /// <returns>Its mins and maxs.</returns>
    public (Vec3 Mins, Vec3 Maxs) ChildBox(int node, int child) =>
        (_childMins[(node * 4) + child], _childMaxs[(node * 4) + child]);

    /// <summary>A leaf's two triangles.</summary>
    /// <param name="leaf">The leaf (tree index minus <see cref="NodeCount"/>).</param>
    /// <returns>The triangle indices.</returns>
    public (int Tri0, int Tri1) LeafTris(int leaf) => (_leafTris[leaf * 2], _leafTris[(leaf * 2) + 1]);

    /// <summary>Builds the tree over a surface's triangles.</summary>
    /// <param name="surface">The displacement.</param>
    /// <returns>The tree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
    public static DispCollisionTree Build(VradDispSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        int cells = surface.Width - 1;
        int numLeaves = cells * cells;
        int numNodes = CalcCount(surface.Power) - numLeaves;

        // AABBTree_CreateLeafs.
        short[] leafTris = new short[numLeaves * 2];
        for (int h = 0; h < cells; h++)
        {
            for (int w = 0; w < cells; w++)
            {
                int leaf = IndexFromComponents(w, h);
                int tri = ((h * cells) + w) * 2;
                leafTris[leaf * 2] = (short)tri;
                leafTris[(leaf * 2) + 1] = (short)(tri + 1);
            }
        }

        Vec3[] childMins = new Vec3[numNodes * 4];
        Vec3[] childMaxs = new Vec3[numNodes * 4];

        // AABBTree_CalcBounds: nothing to do without vertices or nodes.
        if (surface.Size == 0 || numNodes == 0)
        {
            return new DispCollisionTree(
                numNodes, childMins, childMaxs, leafTris,
                new Vec3(float.MaxValue, float.MaxValue, float.MaxValue),
                new Vec3(-float.MaxValue, -float.MaxValue, -float.MaxValue));
        }

        GenerateBoxes(surface, numNodes, leafTris, childMins, childMaxs, 0, out Vec3 mins, out Vec3 maxs);

        // Bloat a little.
        mins = new Vec3(mins.X - 1.0f, mins.Y - 1.0f, mins.Z - 1.0f);
        maxs = new Vec3(maxs.X + 1.0f, maxs.Y + 1.0f, maxs.Z + 1.0f);
        return new DispCollisionTree(numNodes, childMins, childMaxs, leafTris, mins, maxs);
    }

    private static void GenerateBoxes(
        VradDispSurface surface,
        int numNodes,
        short[] leafTris,
        Vec3[] childMins,
        Vec3[] childMaxs,
        int node,
        out Vec3 mins,
        out Vec3 maxs)
    {
        // AABBTree_GenerateBoxes_r. ClearBounds is +-99999.
        Bounds b = Bounds.Cleared;
        if (node >= numNodes)
        {
            int leaf = node - numNodes;
            ReadOnlySpan<Vec3> verts = surface.Verts;
            for (int t = 0; t < 2; t++)
            {
                DispCollTri tri = surface.Tris[leafTris[(leaf * 2) + t]];
                b.Add(verts[tri.V0]);
                b.Add(verts[tri.V1]);
                b.Add(verts[tri.V2]);
            }
        }
        else
        {
            for (int i = 0; i < 4; i++)
            {
                int child = (node << 2) + (i + 1);
                GenerateBoxes(surface, numNodes, leafTris, childMins, childMaxs, child, out Vec3 cMin, out Vec3 cMax);
                childMins[(node * 4) + i] = cMin;
                childMaxs[(node * 4) + i] = cMax;
                b.Add(cMin);
                b.Add(cMax);
            }
        }

        mins = b.Mins;
        maxs = b.Maxs;
    }

    private struct Bounds
    {
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public static Bounds Cleared => new()
        {
            MinX = 99999f, MinY = 99999f, MinZ = 99999f,
            MaxX = -99999f, MaxY = -99999f, MaxZ = -99999f,
        };

        public readonly Vec3 Mins => new(MinX, MinY, MinZ);

        public readonly Vec3 Maxs => new(MaxX, MaxY, MaxZ);

        // AddPointToBounds: two independent ifs per axis.
        public void Add(Vec3 v)
        {
            if (v.X < MinX)
            {
                MinX = v.X;
            }

            if (v.X > MaxX)
            {
                MaxX = v.X;
            }

            if (v.Y < MinY)
            {
                MinY = v.Y;
            }

            if (v.Y > MaxY)
            {
                MaxY = v.Y;
            }

            if (v.Z < MinZ)
            {
                MinZ = v.Z;
            }

            if (v.Z > MaxZ)
            {
                MaxZ = v.Z;
            }
        }
    }
}
