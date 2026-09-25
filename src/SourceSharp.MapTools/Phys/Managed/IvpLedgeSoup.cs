using System.Buffers.Binary;
using System.Numerics;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// <c>IVP_SurfaceBuilder_Ledge_Soup</c>: compiles convex ledges into one
/// <c>IVP_Compact_Surface</c> with its ledge tree, optional outer convex hull, and mass properties.
/// </summary>
/// <remarks>
/// <para>
/// Decompiled from SDK 2013 (TF2): ctor 00181fe0 (00186a00), insert_ledge 0017f840 (00184080),
/// compile 00182d20 (001877a0) with its stages ledges-to-spheres 001800b0 (00184960), sphere
/// clustering 00182c40/00182020 (001876c0/00186a40) and its box 001810e0 (00185a80), the root
/// hull 0017fb50/0017f8c0/0019d8b0/0019dae0 (00184390/00184100/001a2340/001a2590), surface
/// allocation 001814b0/00181390/00181200 (00185e90/00185d70/00185be0), the node writer 00181720
/// (00186100), and the mass/inertia/radius writer 00181ac0 (001864b0).
/// </para>
/// <para>
/// Compact surface layout (48-byte header, then the ledges, then 28-byte tree nodes):
/// <c>float mass_center[3], rotation_inertia[3], upper_limit_radius; uint
/// max_factor_surface_deviation:8, byte_size:24; int offset_ledgetree_root; int dummy[3]</c>; node:
/// <c>int offset_right_node, offset_compact_ledge; float center[3], radius; byte box_sizes[3],
/// free_0</c>.
/// </para>
/// </remarks>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
/// <typeparam name="TP">The precision policy.</typeparam>
internal static class IvpLedgeSoup<T, TP>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
    where TP : struct, IIvpPrecision<T>
{
    /// <summary>A sphere around a ledge or a cluster (the compile's temporary node).</summary>
    private sealed class Sphere
    {
        public T CX, CY, CZ, Radius;
        public byte B0, B1, B2;
        public IvpCompactLedge? Ledge;
        public Sphere? Left, Right;
    }

    /// <summary>The soup's float bounding box scratch (+0x50 / +0x60 in the binary).</summary>
    private struct Box
    {
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    private static T BoxScale => TP.IsDouble ? T.CreateTruncating(249.99998812563774) : T.CreateTruncating(249.99998474121094f);

    /// <summary>Compiles ledges into a compact surface.</summary>
    /// <param name="ledges">The convex ledges, in insertion order.</param>
    /// <param name="buildRootConvexHull">Build an outer hull when there is more than one ledge.</param>
    /// <param name="context">Scratch.</param>
    /// <returns>The compact surface bytes (IVP layout, byte_size long), or null.</returns>
    public static byte[]? Compile(List<IvpCompactLedge> ledges, bool buildRootConvexHull, IvpCookContext context)
    {
        if (ledges.Count == 0)
        {
            return null;
        }

        // 001800b0: one sphere per ledge, and the soup's float box of all spheres.
        var box = new Box { MinX = 1e6f, MinY = 1e6f, MinZ = 1e6f, MaxX = -1e6f, MaxY = -1e6f, MaxZ = -1e6f };
        var leaves = new List<Sphere>(ledges.Count);
        foreach (IvpCompactLedge ledge in ledges)
        {
            IvpLedgeSolver<T, TP>.BoundingBox(ledge, out (T X, T Y, T Z) min, out (T X, T Y, T Z) max);
            Sphere s = SphereOf(min, max);
            s.Ledge = ledge;
            GrowBox(ref box, s.CX - s.Radius, s.CY - s.Radius, s.CZ - s.Radius, s.CX + s.Radius, s.CY + s.Radius, s.CZ + s.Radius);
            leaves.Add(s);
        }

        // 00182c40: cluster.
        Sphere root = Cluster(leaves, ref box);

        // 0017fb50: the outer hull goes on the root, and its ledge after all the leaves.
        IvpCompactLedge? hull = null;
        if (buildRootConvexHull && ledges.Count > 1)
        {
            var dfs = new List<IvpCompactLedge>();
            CollectLeaves(root, dfs);
            hull = RootHull(dfs, context);
            if (hull is null)
            {
                return null;
            }

            root.Ledge = hull;
        }

        // 001814b0: layout.
        int ledgeBytes = 0;
        foreach (IvpCompactLedge l in ledges)
        {
            ledgeBytes += l.Size;
        }

        if (hull is not null)
        {
            ledgeBytes += hull.Size;
        }

        int nodes = CountNodes(root);
        int treeOffset = 0x30 + ledgeBytes;
        int byteSize = treeOffset + (nodes * 28);
        byte[] surface = new byte[byteSize];

        // 00181390: copy the leaves (in insertion order), then the hull.
        int cursor = 0x30;
        var placed = new Dictionary<IvpCompactLedge, int>(ReferenceEqualityComparer.Instance);
        foreach (Sphere s in leaves)
        {
            IvpCompactLedge l = s.Ledge!;
            l.Bytes.AsSpan(0, l.Size).CopyTo(surface.AsSpan(cursor));
            placed[l] = cursor;
            cursor += l.Size;
        }

        if (hull is not null)
        {
            hull.Bytes.AsSpan(0, hull.Size).CopyTo(surface.AsSpan(cursor));
            placed[hull] = cursor;
            cursor += hull.Size;
        }

        // 00181720: nodes depth first, left subtree immediately after its parent.
        int nodeCursor = treeOffset;
        WriteNode(root, surface, placed, ref nodeCursor);

        BinaryPrimitives.WriteUInt32LittleEndian(surface.AsSpan(0x1c), (uint)byteSize << 8);
        BinaryPrimitives.WriteInt32LittleEndian(surface.AsSpan(0x20), treeOffset);

        // 00181ac0: mass properties over the tree's leaves, left first.
        var leafLedges = new List<IvpCompactLedge>();
        CollectLeafCopies(root, surface, placed, leafLedges);
        IvpLedgeSolver<T, TP>.MassProperties(leafLedges, out (T X, T Y, T Z) mc, out (T X, T Y, T Z) inertia, context.SkipZeroLengthInertiaEdges);
        IvpLedgeSolver<T, TP>.RadiusAndDeviation(leafLedges, mc, out T radius, out T deviation);
        WriteF(surface, 0x0, mc.X);
        WriteF(surface, 0x4, mc.Y);
        WriteF(surface, 0x8, mc.Z);
        WriteF(surface, 0xc, inertia.X);
        WriteF(surface, 0x10, inertia.Y);
        WriteF(surface, 0x14, inertia.Z);
        WriteF(surface, 0x18, radius);
        T factor = ((BoxScale * deviation) / radius) + T.One;
        surface[0x1c] = (byte)IvpVector.CvttToInt32(double.CreateTruncating(factor));
        return surface;
    }

    private static void WriteF(byte[] b, int offset, T v) =>
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset), float.CreateTruncating(v));

    /// <summary>
    /// The sphere of a box: centre = interpolate(0.5, max, min), radius = |max - centre|,
    /// box sizes = (int)((max - centre) * (C/radius)) + 1 (001800b0, 00182020).
    /// </summary>
    private static Sphere SphereOf((T X, T Y, T Z) min, (T X, T Y, T Z) max)
    {
        (T X, T Y, T Z) c = IvpLedgeSolver<T, TP>.Interpolate(T.CreateTruncating(0.5f), max, min);
        T dx = max.X - c.X, dy = max.Y - c.Y, dz = max.Z - c.Z;
        T r = T.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz));
        T k = BoxScale / r;
        return new Sphere
        {
            CX = c.X,
            CY = c.Y,
            CZ = c.Z,
            Radius = r,
            B0 = (byte)(IvpVector.CvttToInt32(double.CreateTruncating((max.X - c.X) * k)) + 1),
            B1 = (byte)(IvpVector.CvttToInt32(double.CreateTruncating((max.Y - c.Y) * k)) + 1),
            B2 = (byte)(IvpVector.CvttToInt32(double.CreateTruncating((max.Z - c.Z) * k)) + 1),
        };
    }

    /// <summary>The soup's float box grows (compared at IVP_DOUBLE, stored as float).</summary>
    private static void GrowBox(ref Box box, T x0, T y0, T z0, T x1, T y1, T z1)
    {
        if (x0 < T.CreateTruncating(box.MinX))
        {
            box.MinX = float.CreateTruncating(x0);
        }

        if (T.CreateTruncating(box.MaxX) < x1)
        {
            box.MaxX = float.CreateTruncating(x1);
        }

        if (y0 < T.CreateTruncating(box.MinY))
        {
            box.MinY = float.CreateTruncating(y0);
        }

        if (T.CreateTruncating(box.MaxY) < y1)
        {
            box.MaxY = float.CreateTruncating(y1);
        }

        if (z0 < T.CreateTruncating(box.MinZ))
        {
            box.MinZ = float.CreateTruncating(z0);
        }

        if (T.CreateTruncating(box.MaxZ) < z1)
        {
            box.MaxZ = float.CreateTruncating(z1);
        }
    }

    /// <summary>001810e0: the box of a set of spheres from their quantised box sizes.</summary>
    private static Box BoxOf(List<Sphere> set)
    {
        var box = new Box { MinX = 1e6f, MinY = 1e6f, MinZ = 1e6f, MaxX = -1e6f, MaxY = -1e6f, MaxZ = -1e6f };
        T scale = T.CreateTruncating(0.004f);
        foreach (Sphere s in set)
        {
            T f = s.Radius * scale;
            T ex = T.CreateTruncating(s.B0) * f;
            T ey = T.CreateTruncating(s.B1) * f;
            T ez = T.CreateTruncating(s.B2) * f;
            GrowBox(ref box, s.CX - ex, s.CY - ey, s.CZ - ez, s.CX + ex, s.CY + ey, s.CZ + ez);
        }

        return box;
    }

    /// <summary>
    /// 00182020: a single sphere is its own node; otherwise a parent around the set's box, split on
    /// whichever axis gives the smallest sum of the halves' box volumes.
    /// </summary>
    private static Sphere Cluster(List<Sphere> set, ref Box soupBox)
    {
        if (set.Count == 1)
        {
            return set[0];
        }

        Box b = BoxOf(set);
        soupBox = b;
        (T X, T Y, T Z) max = (T.CreateTruncating(b.MaxX), T.CreateTruncating(b.MaxY), T.CreateTruncating(b.MaxZ));
        (T X, T Y, T Z) min = (T.CreateTruncating(b.MinX), T.CreateTruncating(b.MinY), T.CreateTruncating(b.MinZ));
        Sphere node = SphereOf(min, max);

        if (set.Count == 2)
        {
            node.Left = Cluster([set[0]], ref soupBox);
            node.Right = Cluster([set[1]], ref soupBox);
            return node;
        }

        var lefts = new List<Sphere>[3];
        var rights = new List<Sphere>[3];
        float[] cost = new float[3];
        T eps = T.CreateTruncating(1.0e-6f);
        int n = set.Count;
        for (int axis = 0; axis < 3; axis++)
        {
            var left = new List<Sphere>(n);
            var right = new List<Sphere>(n);
            T sum = T.Zero;
            foreach (Sphere s in set)
            {
                sum += Center(s, axis);
            }

            T mean = sum / T.CreateTruncating(n);
            T hi = mean + eps;
            T lo = mean - eps;
            bool toLeft = true;
            for (int i = 0; i < n; i++)
            {
                Sphere s = set[i];
                T v = Center(s, axis);
                if (v < lo)
                {
                    left.Add(s);
                }
                else if (hi < v)
                {
                    right.Add(s);
                }
                else
                {
                    Sphere neighbour = i == n - 1 ? set[i - 1] : set[i + 1];
                    T nv = Center(neighbour, axis);
                    if (nv < lo)
                    {
                        right.Add(s);
                    }
                    else if (nv <= hi)
                    {
                        if (left.Count == 0)
                        {
                            left.Add(s);
                        }
                        else if (right.Count == 0)
                        {
                            right.Add(s);
                        }
                        else if (toLeft)
                        {
                            left.Add(s);
                            toLeft = false;
                        }
                        else
                        {
                            right.Add(s);
                            toLeft = true;
                        }
                    }
                    else
                    {
                        left.Add(s);
                    }
                }
            }

            Box bl = BoxOf(left);
            Box br = BoxOf(right);
            soupBox = br;
            cost[axis] = Volume(bl) + Volume(br);
            lefts[axis] = left;
            rights[axis] = right;
        }

        // The binary lowers the best cost even when that axis left a side empty, and only moves
        // the selection when both sides have members.
        float best = 1.0e20f;
        int chosen = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            if (cost[axis] < best)
            {
                best = cost[axis];
                if (lefts[axis].Count != 0 && rights[axis].Count != 0)
                {
                    chosen = axis;
                }
            }
        }

        node.Left = Cluster(lefts[chosen], ref soupBox);
        node.Right = Cluster(rights[chosen], ref soupBox);
        return node;
    }

    /// <summary>A box volume as the builds group it (SDK (|dy|*|dz|)*|dx|, TF2 (|dx|*|dy|)*|dz|).</summary>
    private static float Volume(Box b)
    {
        float dx = MathF.Abs(b.MaxX - b.MinX), dy = MathF.Abs(b.MaxY - b.MinY), dz = MathF.Abs(b.MaxZ - b.MinZ);
        return TP.IsDouble ? (dx * dy) * dz : (dy * dz) * dx;
    }

    private static T Center(Sphere s, int axis) => axis switch
    {
        0 => s.CX,
        1 => s.CY,
        _ => s.CZ,
    };

    private static int CountNodes(Sphere s) => s.Left is null ? 1 : 1 + CountNodes(s.Left) + CountNodes(s.Right!);

    /// <summary>The leaves depth first, left first (0017fb50 / 001c0ac0 order).</summary>
    private static void CollectLeaves(Sphere s, List<IvpCompactLedge> into)
    {
        if (s.Left is null)
        {
            into.Add(s.Ledge!);
            return;
        }

        CollectLeaves(s.Left, into);
        CollectLeaves(s.Right!, into);
    }

    /// <summary>The leaf ledges as they now sit inside the surface (what the solver walks).</summary>
    private static void CollectLeafCopies(Sphere s, byte[] surface, Dictionary<IvpCompactLedge, int> placed, List<IvpCompactLedge> into)
    {
        if (s.Left is null)
        {
            IvpCompactLedge l = s.Ledge!;
            into.Add(new IvpCompactLedge(surface.AsSpan(placed[l], l.Size).ToArray()));
            return;
        }

        CollectLeafCopies(s.Left, surface, placed, into);
        CollectLeafCopies(s.Right!, surface, placed, into);
    }

    /// <summary>00181720.</summary>
    private static int WriteNode(Sphere s, byte[] surface, Dictionary<IvpCompactLedge, int> placed, ref int cursor)
    {
        int at = cursor;
        cursor += 28;
        Span<byte> node = surface.AsSpan(at, 28);
        BinaryPrimitives.WriteSingleLittleEndian(node[8..], float.CreateTruncating(s.CX));
        BinaryPrimitives.WriteSingleLittleEndian(node[12..], float.CreateTruncating(s.CY));
        BinaryPrimitives.WriteSingleLittleEndian(node[16..], float.CreateTruncating(s.CZ));
        BinaryPrimitives.WriteSingleLittleEndian(node[20..], float.CreateTruncating(s.Radius));
        node[24] = s.B0;
        node[25] = s.B1;
        node[26] = s.B2;
        node[27] = 0;
        if (s.Left is not null)
        {
            if (s.Ledge is null)
            {
                BinaryPrimitives.WriteInt32LittleEndian(node[4..], 0);
            }
            else
            {
                int ledgeAt = placed[s.Ledge];
                BinaryPrimitives.WriteInt32LittleEndian(node[4..], ledgeAt - at);
                BinaryPrimitives.WriteInt32LittleEndian(surface.AsSpan(ledgeAt + 4), at - ledgeAt);
                surface[ledgeAt + 8] = (byte)((surface[ledgeAt + 8] & 0xfc) | 1);
            }

            WriteNode(s.Left, surface, placed, ref cursor);
            int right = WriteNode(s.Right!, surface, placed, ref cursor);
            BinaryPrimitives.WriteInt32LittleEndian(node[0..], right - at);
            return at;
        }

        int leafAt = placed[s.Ledge!];
        surface[leafAt + 8] = (byte)(surface[leafAt + 8] & 0xfc);
        BinaryPrimitives.WriteInt32LittleEndian(node[0..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(node[4..], leafAt - at);
        return at;
    }

    /// <summary>
    /// 0019d8b0 + 0019dae0: the convex hull of every leaf's points (leaves last first, points
    /// deduplicated on all 16 bytes), with the triangles and edges that no leaf has flagged
    /// virtual.
    /// </summary>
    private static IvpCompactLedge? RootHull(List<IvpCompactLedge> leaves, IvpCookContext context)
    {
        var seen = new HashSet<(uint, uint, uint, uint)>();
        var points = new List<IvpPoint<T>>();
        for (int l = leaves.Count - 1; l >= 0; l--)
        {
            IvpCompactLedge ledge = leaves[l];
            for (int t = 0; t < ledge.TriangleCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int pi = ledge.EdgeStart(t, e);
                    (uint, uint, uint, uint) key = PointKey(ledge, pi);
                    if (seen.Add(key))
                    {
                        (float x, float y, float z) = ledge.Point(pi);
                        points.Add(new IvpPoint<T>(T.CreateTruncating(x), T.CreateTruncating(y), T.CreateTruncating(z), T.Zero));
                    }
                }
            }
        }

        IvpCompactLedge? hull = IvpPointSoup<T, TP>.ToCompactLedge(points, context);
        if (hull is null)
        {
            return null;
        }

        // 0019dae0: ids by first appearance over the leaves, triangles and directed edges keyed on ids.
        var ids = new Dictionary<(uint, uint, uint, uint), int>();
        var tris = new HashSet<(int, int, int)>();
        var edges = new HashSet<(int, int)>();
        for (int l = leaves.Count - 1; l >= 0; l--)
        {
            IvpCompactLedge ledge = leaves[l];
            for (int t = 0; t < ledge.TriangleCount; t++)
            {
                int[] id = new int[3];
                for (int e = 0; e < 3; e++)
                {
                    (uint, uint, uint, uint) key = PointKey(ledge, ledge.EdgeStart(t, e));
                    if (!ids.TryGetValue(key, out int v))
                    {
                        v = ids.Count;
                        ids.Add(key, v);
                    }

                    id[e] = v;
                }

                tris.Add((id[0], id[1], id[2]));
                edges.Add((id[0], id[1]));
                edges.Add((id[1], id[2]));
                edges.Add((id[2], id[0]));
            }
        }

        for (int t = 0; t < hull.TriangleCount; t++)
        {
            int[] id = new int[3];
            for (int e = 0; e < 3; e++)
            {
                id[e] = ids.TryGetValue(PointKey(hull, hull.EdgeStart(t, e)), out int v) ? v : -1;
            }

            if (!tris.Contains((id[0], id[1], id[2])))
            {
                hull.SetTriangleWord(t, hull.TriangleWord(t) | 0x80000000u);
            }

            for (int e = 0; e < 3; e++)
            {
                if (!edges.Contains((id[e], id[(e + 1) % 3])))
                {
                    hull.SetEdgeWord(t, e, hull.EdgeWord(t, e) | 0x80000000u);
                }
            }
        }

        return hull;
    }

    private static (uint, uint, uint, uint) PointKey(IvpCompactLedge ledge, int point)
    {
        int o = ledge.PointOffset + (16 * point);
        ReadOnlySpan<byte> b = ledge.Bytes;
        return (
            BinaryPrimitives.ReadUInt32LittleEndian(b[o..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 4)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 8)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 12)..]));
    }
}
