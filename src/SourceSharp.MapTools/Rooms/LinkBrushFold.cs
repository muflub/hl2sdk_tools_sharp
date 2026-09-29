//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Rooms;

/// <summary>What <see cref="LinkBrushFold.Fold"/> made of a map's brushes.</summary>
/// <param name="Brushes">The brushes after the fold, in the order of each one's first constituent.</param>
/// <param name="Sides">Their sides, one run per brush in brush order.</param>
/// <param name="Map">Per brush before the fold, the index of the brush it is now (itself or the box it joined).</param>
/// <param name="Groups">
/// Per brush after the fold, the brushes before it that it is made of,
/// ascending; a brush the fold left alone is a group of one.
/// </param>
internal sealed record BrushFoldResult(DBrush[] Brushes, DBrushSide[] Sides, int[] Map, int[][] Groups)
{
    /// <summary>How many brushes the fold removed.</summary>
    public int Removed => Map.Length - Brushes.Length;
}

/// <summary>
/// Merges touching axis-aligned box brushes of a linked level into larger
/// boxes, so a level of many rooms stays under the loader's brush cap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A linked level is every room's brushes side by side. The
/// rooms' shells meet cell to cell: two floors across a shared boundary, two
/// walls back to back, a jamb against the next room's jamb. Each pair is two
/// brushes where one box would do, and <c>MAX_MAP_BRUSHES</c> (8192) is what
/// bounds how many rooms a level can hold. Folding the pairs, and the rows
/// and sheets they make, lets a level grow until a different limit binds.
/// </para>
/// <para>
/// <b>When two boxes merge.</b> Both are world brushes that are exactly
/// axis-aligned boxes: six sides, one on each axial plane, none a bevel and
/// none a displacement. Their extents are identical (to the bit) on two
/// axes and they touch on the third (one's high face is the other's low
/// face), so their union is exactly a box and nothing is added or removed
/// but the face between them. Their contents are identical. And every side
/// that coalesces (the four around the axis of the merge, each made of one
/// side from each box) names the same material (texdata) with the same
/// surface flags in both boxes, so whatever a trace hits there reports the
/// same surface. The two faces that meet inside the new box are dropped;
/// they are what a trace could once stop on and now cannot (see below).
/// </para>
/// <para>
/// <b>Which texinfo.</b> A coalesced side takes the texinfo of the
/// constituent that comes first in brush order; the texinfos can differ only
/// in texture offsets and axes, which no trace or physics reads (a brush
/// side's texinfo is not drawn: the faces are). An end cap is one box's own
/// side and keeps its texinfo. The merged box's sides are written in the
/// order its first constituent's were, with bevel 0 and no displacement,
/// on the constituents' own planes: the fold never makes a plane.
/// </para>
/// <para>
/// <b>Order.</b> Greedy maximal chains, deterministically. First the
/// horizontal slabs (a box thinner in z than in x and y: floors and
/// ceilings): chains along x make rows, then chains of identical rows along
/// y make sheets. Then every other box, to a fixed point: chains along each
/// box's thin axis (walls and jambs back to back), then chains along x, y
/// and z (pieces in line). Within a chain the boxes are taken in order along
/// the axis; groups and chains are visited in the order of their first
/// brush, so the result is a pure function of the input brushes, whatever
/// the thread count.
/// </para>
/// <para>
/// <b>What changes for a trace.</b> The union is the same solid, so a
/// sweep that starts outside it meets it at the same point, on the same
/// plane, with the same contents and surface: on random point and box
/// sweeps over linked levels the answers are bit-identical. Physics collides
/// with the rooms' own convexes, which the fold does not touch (their client
/// data names the merged box, of the same contents). What goes is the seam
/// between constituents, which only a sweep that starts inside the solid can
/// see: one that starts in one constituent and ends in the next used to
/// leave the first at the seam, so it started solid but was not all solid;
/// now it never leaves the box and is all solid. A sweep grazing the union
/// within the 1/32-unit trace epsilon could in principle have stopped on the
/// seam face itself; the trace facts allow that case and have not met it.
/// </para>
/// </remarks>
internal static class LinkBrushFold
{
    /// <summary>Folds <paramref name="brushes"/>.</summary>
    /// <param name="brushes">The brushes.</param>
    /// <param name="sides">Their sides.</param>
    /// <param name="planes">The plane table the sides name.</param>
    /// <param name="texInfos">The texinfo table the sides name.</param>
    /// <param name="foldable">
    /// Per brush, whether it may merge: a world brush. A brush entity's
    /// brushes belong to their own model and are never merged.
    /// </param>
    /// <returns>The folded brushes and the map from the old numbering to the new.</returns>
    public static BrushFoldResult Fold(
        IReadOnlyList<DBrush> brushes,
        IReadOnlyList<DBrushSide> sides,
        IReadOnlyList<DPlane> planes,
        IReadOnlyList<TexInfo> texInfos,
        IReadOnlyList<bool> foldable)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        ArgumentNullException.ThrowIfNull(sides);
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(texInfos);
        ArgumentNullException.ThrowIfNull(foldable);

        List<Piece> slabs = [];
        List<Piece> others = [];
        Piece?[] single = new Piece?[brushes.Count];
        for (int b = 0; b < brushes.Count; b++)
        {
            if (foldable[b] && AsBox(b, brushes[b], sides, planes, texInfos) is { } piece)
            {
                single[b] = piece;
                (piece.IsSlab ? slabs : others).Add(piece);
            }
        }

        // Floors and ceilings: rows along x, then sheets of rows along y.
        slabs = Chain(slabs, 0, _ => true);
        slabs = Chain(slabs, 1, _ => true);

        // Shell pieces and everything else: back to back, then in line, to
        // a fixed point (a back-to-back pair may make a row possible and
        // the other way round).
        int before;
        do
        {
            before = others.Count;
            for (int axis = 0; axis < 3; axis++)
            {
                others = Chain(others, axis, p => p.ThinAxis == axis);
            }

            for (int axis = 0; axis < 3; axis++)
            {
                others = Chain(others, axis, _ => true);
            }
        }
        while (others.Count < before);

        // Emit in the order of each piece's first brush; a brush that took
        // no part in a merge is copied as it was, sides and all.
        SortedDictionary<int, Piece> byFirst = [];
        foreach (Piece piece in slabs.Concat(others))
        {
            if (piece.Members.Count > 1)
            {
                byFirst[piece.First] = piece;
            }
        }

        List<DBrush> outBrushes = new(brushes.Count);
        List<DBrushSide> outSides = new(sides.Count);
        List<int[]> groups = new(brushes.Count);
        int[] map = new int[brushes.Count];
        Array.Fill(map, -1);
        for (int b = 0; b < brushes.Count; b++)
        {
            if (map[b] >= 0)
            {
                continue;
            }

            int index = outBrushes.Count;
            if (byFirst.TryGetValue(b, out Piece? merged))
            {
                outBrushes.Add(new DBrush { FirstSide = outSides.Count, NumSides = 6, Contents = merged.Contents });
                foreach (int slot in merged.SlotOrder)
                {
                    outSides.Add(new DBrushSide { PlaneNum = merged.Plane[slot], TexInfo = merged.TexInfo[slot], DispInfo = 0, Bevel = 0 });
                }

                int[] members = [.. merged.Members.Order()];
                foreach (int member in members)
                {
                    map[member] = index;
                }

                groups.Add(members);
                continue;
            }

            DBrush copy = brushes[b];
            copy.FirstSide = outSides.Count;
            for (int s = 0; s < brushes[b].NumSides; s++)
            {
                outSides.Add(sides[brushes[b].FirstSide + s]);
            }

            outBrushes.Add(copy);
            map[b] = index;
            groups.Add([b]);
        }

        return new BrushFoldResult([.. outBrushes], [.. outSides], map, [.. groups]);
    }

    /// <summary>
    /// A brush as a box piece, or null when it is not exactly an axial box:
    /// six sides, one per axial plane (normal exactly ±1 on one axis), no
    /// bevel, no displacement, and a positive extent on every axis.
    /// </summary>
    internal static Piece? AsBox(
        int index, DBrush brush, IReadOnlyList<DBrushSide> sides, IReadOnlyList<DPlane> planes, IReadOnlyList<TexInfo> texInfos)
    {
        if (brush.NumSides != 6)
        {
            return null;
        }

        Piece piece = new(index, brush.Contents);
        bool[] seen = new bool[6];
        for (int s = 0; s < 6; s++)
        {
            DBrushSide side = sides[brush.FirstSide + s];
            if (side.Bevel != 0 || side.DispInfo != 0)
            {
                return null;
            }

            DPlane plane = planes[side.PlaneNum];
            int slot = Slot(plane);
            if (slot < 0 || seen[slot])
            {
                return null;
            }

            seen[slot] = true;
            int axis = slot >> 1;
            if ((slot & 1) == 1)
            {
                piece.Hi[axis] = plane.Dist;
            }
            else
            {
                piece.Lo[axis] = -plane.Dist;
            }

            piece.Plane[slot] = side.PlaneNum;
            piece.TexInfo[slot] = side.TexInfo;
            piece.Surface[slot] = side.TexInfo < 0
                ? (-1, 0)
                : (texInfos[side.TexInfo].TexData, texInfos[side.TexInfo].Flags);
            piece.SlotOrder[s] = slot;
        }

        for (int axis = 0; axis < 3; axis++)
        {
            if (!(piece.Lo[axis] < piece.Hi[axis]))
            {
                return null;
            }
        }

        return piece;
    }

    /// <summary>A side's slot: axis x 2, plus 1 for the high face (normal +axis), or -1 for a plane that is not axial.</summary>
    private static int Slot(DPlane plane)
    {
        float[] n = [plane.Normal.X, plane.Normal.Y, plane.Normal.Z];
        for (int axis = 0; axis < 3; axis++)
        {
            if (n[(axis + 1) % 3] == 0 && n[(axis + 2) % 3] == 0)
            {
                if (n[axis] == 1)
                {
                    return (axis * 2) + 1;
                }

                if (n[axis] == -1)
                {
                    return axis * 2;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// One greedy pass along <paramref name="axis"/>: the pieces that take
    /// part are grouped by everything a merge along the axis must share
    /// (the other two axes' extents, the contents, and the four sides
    /// around the axis' materials and flags); in each group the pieces are
    /// taken in order along the axis and every maximal run of touching ones
    /// becomes one piece.
    /// </summary>
    private static List<Piece> Chain(List<Piece> pieces, int axis, Func<Piece, bool> takesPart)
    {
        Dictionary<ChainKey, List<Piece>> groups = [];
        List<ChainKey> order = [];
        List<Piece> result = new(pieces.Count);
        foreach (Piece piece in pieces)
        {
            if (!takesPart(piece))
            {
                result.Add(piece);
                continue;
            }

            ChainKey key = ChainKey.Of(piece, axis);
            if (!groups.TryGetValue(key, out List<Piece>? group))
            {
                groups[key] = group = [];
                order.Add(key);
            }

            group.Add(piece);
        }

        foreach (ChainKey key in order)
        {
            List<Piece> group = groups[key];
            group.Sort((a, b) =>
            {
                int c = a.Lo[axis].CompareTo(b.Lo[axis]);
                return c != 0 ? c : a.First.CompareTo(b.First);
            });

            int start = 0;
            while (start < group.Count)
            {
                int end = start + 1;
                while (end < group.Count && group[end].Lo[axis] == group[end - 1].Hi[axis])
                {
                    end++;
                }

                result.Add(end - start == 1 ? group[start] : Merge(group.GetRange(start, end - start), axis));
                start = end;
            }
        }

        // The next pass visits groups in the order of their first brush.
        result.Sort((a, b) => a.First.CompareTo(b.First));
        return result;
    }

    /// <summary>
    /// A run of touching pieces along <paramref name="axis"/> as one box: the
    /// sides around the axis from the piece first in brush order, the low
    /// cap from the first piece along the axis and the high cap from the
    /// last, the side order of the piece first in brush order.
    /// </summary>
    private static Piece Merge(List<Piece> chain, int axis)
    {
        Piece first = chain.MinBy(p => p.First)!;
        Piece merged = new(first.First, first.Contents);
        for (int a = 0; a < 3; a++)
        {
            merged.Lo[a] = first.Lo[a];
            merged.Hi[a] = first.Hi[a];
        }

        merged.Lo[axis] = chain[0].Lo[axis];
        merged.Hi[axis] = chain[^1].Hi[axis];
        for (int slot = 0; slot < 6; slot++)
        {
            Piece from = (slot >> 1) != axis ? first : (slot & 1) == 0 ? chain[0] : chain[^1];
            merged.Plane[slot] = from.Plane[slot];
            merged.TexInfo[slot] = from.TexInfo[slot];
            merged.Surface[slot] = from.Surface[slot];
        }

        first.SlotOrder.CopyTo(merged.SlotOrder, 0);
        merged.Members.Clear();
        foreach (Piece piece in chain)
        {
            merged.Members.AddRange(piece.Members);
        }

        return merged;
    }

    /// <summary>A box on its way through the fold: one brush, or several merged.</summary>
    internal sealed class Piece(int first, int contents)
    {
        /// <summary>The lowest brush index among its members: its place in brush order.</summary>
        public int First { get; } = first;

        public int Contents { get; } = contents;

        public float[] Lo { get; } = new float[3];

        public float[] Hi { get; } = new float[3];

        /// <summary>Per slot (axis x 2, +1 for the high face), the side's plane.</summary>
        public ushort[] Plane { get; } = new ushort[6];

        /// <summary>Per slot, the side's texinfo.</summary>
        public short[] TexInfo { get; } = new short[6];

        /// <summary>Per slot, the side's texdata and surface flags: what must agree for sides to coalesce.</summary>
        public (int TexData, int Flags)[] Surface { get; } = new (int, int)[6];

        /// <summary>The slots in the order the sides are written.</summary>
        public int[] SlotOrder { get; } = new int[6];

        /// <summary>The brushes it is made of.</summary>
        public List<int> Members { get; } = [first];

        /// <summary>A horizontal slab: thinner in z than in x and in y (a floor or a ceiling).</summary>
        public bool IsSlab => Extent(2) < Extent(0) && Extent(2) < Extent(1);

        /// <summary>The axis it is thinnest along, the lowest on a tie.</summary>
        public int ThinAxis => Extent(0) <= Extent(1) && Extent(0) <= Extent(2) ? 0 : Extent(1) <= Extent(2) ? 1 : 2;

        private float Extent(int axis) => Hi[axis] - Lo[axis];
    }

    /// <summary>What pieces merging along one axis must share.</summary>
    private readonly record struct ChainKey(
        float LoA, float HiA, float LoB, float HiB, int Contents,
        (int, int) S0, (int, int) S1, (int, int) S2, (int, int) S3)
    {
        public static ChainKey Of(Piece piece, int axis)
        {
            int a = (axis + 1) % 3, b = (axis + 2) % 3;
            if (a > b)
            {
                (a, b) = (b, a);
            }

            return new ChainKey(
                piece.Lo[a], piece.Hi[a], piece.Lo[b], piece.Hi[b], piece.Contents,
                piece.Surface[a * 2], piece.Surface[(a * 2) + 1], piece.Surface[b * 2], piece.Surface[(b * 2) + 1]);
        }
    }
}
