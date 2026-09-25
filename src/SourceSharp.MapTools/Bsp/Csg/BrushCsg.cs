using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// Brush subtraction, intersection, clipping to a
/// block, and <c>ChopBrushes</c>.
/// </summary>
/// <remarks>
/// The stage's whole job is stated in stock's opening comment: "there will be
/// no brush overlap after csg phase". Everything below is
/// in service of that and of doing it without fragmenting the world more than
/// it has to.
/// </remarks>
public static class BrushCsg
{
    /// <summary>
    /// <c>TRANSPARENT_CONTENTS</c>.
    /// </summary>
    public const int TransparentContents = (int)(BrushContents.Grate | BrushContents.Window);

    /// <summary>
    /// <c>MASK_SPLITAREAPORTAL</c>: the
    /// contents an areaportal is allowed to bite.
    /// </summary>
    public const int SplitAreaPortalMask = (int)(BrushContents.Water | BrushContents.Slime);

    /// <summary>
    /// Everything of <c>a</c> that is not inside <c>b</c>:
    /// <c>SubtractBrush</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="a">The brush being bitten. Undisturbed.</param>
    /// <param name="b">The brush doing the biting. Undisturbed.</param>
    /// <returns>
    /// A list of fragments, null when <paramref name="a"/> is entirely inside
    /// <paramref name="b"/>, or <paramref name="a"/> ITSELF when the two did
    /// not really intersect.
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <b>Three different kinds of answer come back through one return
    /// value</b>, and every caller has to tell them apart by reference: the
    /// identity check <c>if (sub == b1)</c> in <c>ChopBrushes</c>
    /// Is how "no intersection" is distinguished from "one
    /// fragment". Getting that wrong frees the input.
    /// </remarks>
    public static BspBrush? SubtractBrush(BspBuildContext context, BspBrush a, BspBrush b)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        BspBrush? inBrush = a;
        BspBrush? outList = null;

        for (int i = 0; i < b.SideCount && inBrush is not null; i++)
        {
            BrushGeometry.SplitBrush(
                context, inBrush, b.Sides[i].PlaneNumber,
                out BspBrush? front, out BspBrush? back);

            if (!ReferenceEquals(inBrush, a))
            {
                context.FreeBrush(inBrush);
            }

            if (front is not null)
            {
                front.Next = outList;
                outList = front;
            }

            inBrush = back;
        }

        if (inBrush is not null)
        {
            context.FreeBrush(inBrush);
        }
        else
        {
            context.FreeBrushList(outList);
            return a;
        }

        return outList;
    }

    /// <summary>
    /// The overlap of two brushes, or null when they are disjoint:
    /// <c>IntersectBrush</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="a">The first brush. Undisturbed.</param>
    /// <param name="b">The second brush. Undisturbed.</param>
    /// <returns>A single brush, or null.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// The only caller(<c>FixupAreaportalWaterBrushes</c>)
    /// frees the result immediately and uses only whether it was null, so this
    /// is an intersection TEST with an allocation in it. It is kept as stock
    /// wrote it rather than shortened, because <c>BrushesDisjoint</c> is the
    /// cheap test and this is deliberately the exact one.
    /// </remarks>
    public static BspBrush? IntersectBrush(BspBuildContext context, BspBrush a, BspBrush b)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        BspBrush? inBrush = a;

        for (int i = 0; i < b.SideCount && inBrush is not null; i++)
        {
            BrushGeometry.SplitBrush(
                context, inBrush, b.Sides[i].PlaneNumber,
                out BspBrush? front, out BspBrush? back);

            if (!ReferenceEquals(inBrush, a))
            {
                context.FreeBrush(inBrush);
            }

            if (front is not null)
            {
                context.FreeBrush(front);
            }

            inBrush = back;
        }

        if (ReferenceEquals(inBrush, a) || inBrush is null)
        {
            return null;
        }

        inBrush.Next = null;
        return inBrush;
    }

    /// <summary>
    /// Whether two brushes definitely do not touch: <c>BrushesDisjoint</c>,
    /// </summary>
    /// <param name="a">The first brush.</param>
    /// <param name="b">The second brush.</param>
    /// <returns>True when they cannot intersect.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <para>
    /// Conservative in one direction only, as stock's comment says: "There will
    /// be false negatives for some non-axial combinations." A true answer means
    /// disjoint; a false answer means "might intersect".
    /// </para>
    /// <para>
    /// The box test uses <c>&gt;=</c> and <c>&lt;=</c>, so brushes that touch
    /// exactly face to face are disjoint — which is what keeps two stacked
    /// blocks from biting each other. The second test, for a plane of
    /// <paramref name="a"/> that is the mirror of a plane of
    /// <paramref name="b"/>, is what makes that work for non-axial contact too,
    /// and it is exact because the plane table hands out mirrored pairs at
    /// <c>n</c> and <c>n^1</c>.
    /// </para>
    /// </remarks>
    public static bool BrushesDisjoint(BspBrush a, BspBrush b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return new BrushBox(a.Mins, a.Maxs).Disjoint(new BrushBox(b.Mins, b.Maxs)) || MirroredPlanes(a, b);
    }

    // The second half of BrushesDisjoint: a plane of a that
    // is the mirror of a plane of b.
    private static bool MirroredPlanes(BspBrush a, BspBrush b)
    {
        Span<BspBrushSide> sidesA = a.Sides;
        Span<BspBrushSide> sidesB = b.Sides;
        for (int i = 0; i < sidesA.Length; i++)
        {
            int mirror = sidesA[i].PlaneNumber ^ 1;
            for (int j = 0; j < sidesB.Length; j++)
            {
                if (sidesB[j].PlaneNumber == mirror)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A brush's box, flattened, and the first half of BrushesDisjoint
    // >= and <= in stock's operand order, axis by axis.
    private readonly struct BrushBox(Vec3 mins, Vec3 maxs)
    {
        private readonly float _minX = mins.X, _minY = mins.Y, _minZ = mins.Z;
        private readonly float _maxX = maxs.X, _maxY = maxs.Y, _maxZ = maxs.Z;

        public bool Disjoint(in BrushBox b) =>
            _minX >= b._maxX || _maxX <= b._minX
            || _minY >= b._maxY || _maxY <= b._minY
            || _minZ >= b._maxZ || _maxZ <= b._minZ;
    }

    /// <summary>
    /// Carves a brush down to a block: <c>ClipBrushToBox</c>,
    /// </summary>
    /// <param name="context">The build context, whose bounding planes are read.</param>
    /// <param name="brush">The brush, consumed.</param>
    /// <param name="clipMins">The block's minimum.</param>
    /// <param name="clipMaxs">The block's maximum.</param>
    /// <returns>The clipped brush, or null when nothing is left.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="brush"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Only X and Y are clipped</b> — stock's loop is
    /// <c>for (j=0; j&lt;2; j++)</c> — because the
    /// blocks span the whole legal Z range and a Z clip would be a no-op that
    /// cost two splits per brush.
    /// </para>
    /// <para>
    /// The second half is what stock's summary line calls it: "Any planes shared
    /// with the box edge will be set to no texinfo". A side lying in a block
    /// boundary is marked <see cref="BspBrushSide.TexInfoNode"/> and made
    /// invisible so it never becomes a face and never becomes a splitter — the
    /// seam between two blocks must not turn into geometry. The test is on
    /// <c>planenum &amp; ~1</c>, so BOTH orientations of the boundary plane
    /// count.
    /// </para>
    /// </remarks>
    public static BspBrush? ClipBrushToBox(
        BspBuildContext context,
        BspBrush brush,
        Vec3 clipMins,
        Vec3 clipMaxs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        BspBrush? current = brush;

        for (int j = 0; j < 2; j++)
        {
            if (current!.Maxs[j] > clipMaxs[j])
            {
                BrushGeometry.SplitBrush(
                    context, current, context.MaxPlaneNumbers[j],
                    out BspBrush? front, out BspBrush? back);

                if (front is not null)
                {
                    context.FreeBrush(front);
                }

                current = back;
                if (current is null)
                {
                    return null;
                }
            }

            if (current.Mins[j] < clipMins[j])
            {
                BrushGeometry.SplitBrush(
                    context, current, context.MinPlaneNumbers[j],
                    out BspBrush? front, out BspBrush? back);

                if (back is not null)
                {
                    context.FreeBrush(back);
                }

                current = front;
                if (current is null)
                {
                    return null;
                }
            }
        }

        for (int i = 0; i < current!.SideCount; i++)
        {
            int p = current.Sides[i].PlaneNumber & ~1;
            if (p == context.MaxPlaneNumbers[0]
                || p == context.MaxPlaneNumbers[1]
                || p == context.MinPlaneNumbers[0]
                || p == context.MinPlaneNumbers[1])
            {
                current.Sides[i].TexInfo = BspBrushSide.TexInfoNode;
                current.Sides[i].Visible = false;
            }
        }

        return current;
    }

    /// <summary>
    /// Copies a map brush and clips it to a block:
    /// <c>CreateClippedBrush</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="mapBrush">The map brush.</param>
    /// <param name="clipMins">The block's minimum.</param>
    /// <param name="clipMaxs">The block's maximum.</param>
    /// <returns>The clipped brush, or null.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="mapBrush"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A brush with zero sides is skipped: that is how the loader marks a brush
    /// it decided not to keep (see <see cref="MapBrush.SideCount"/>), and it is
    /// the first thing this function tests.
    /// </para>
    /// <para>
    /// <b>Hint sides are forced visible here</b> ("hints
    /// are always visible") — so a hint brush gets to be a BSP splitter even
    /// where the loader would have called its sides invisible. That single line
    /// is what makes <c>tools/toolshint</c> do anything at all.
    /// </para>
    /// <para>
    /// The block rejection test uses the MAP brush's own bounds, before any
    /// clipping, with <c>&gt;=</c> and <c>&lt;=</c> on all three axes — so a
    /// brush whose face lies exactly on a block boundary belongs to the block
    /// on the far side of it and not to both.
    /// </para>
    /// </remarks>
    public static BspBrush? CreateClippedBrush(
        BspBuildContext context,
        MapBrush mapBrush,
        Vec3 clipMins,
        Vec3 clipMaxs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mapBrush);

        int sideCount = mapBrush.SideCount;
        if (sideCount == 0)
        {
            return null;
        }

        for (int j = 0; j < 3; j++)
        {
            if (mapBrush.Mins[j] >= clipMaxs[j] || mapBrush.Maxs[j] <= clipMins[j])
            {
                return null;
            }
        }

        BspBrush newBrush = context.AllocBrush(sideCount);
        newBrush.Original = mapBrush;
        newBrush.CopySidesFrom(context.Map, mapBrush);

        for (int j = 0; j < sideCount; j++)
        {
            if (!newBrush.Sides[j].Winding.IsNull)
            {
                newBrush.Sides[j].Winding = context.Windings.Copy(newBrush.Sides[j].Winding);
            }

            if ((newBrush.Sides[j].Surface & (int)SurfaceFlags.Hint) != 0)
            {
                newBrush.Sides[j].Visible = true;
            }
        }

        newBrush.Mins = mapBrush.Mins;
        newBrush.Maxs = mapBrush.Maxs;

        return ClipBrushToBox(context, newBrush, clipMins, clipMaxs);
    }

    /// <summary>
    /// Builds the brush list for a block out of a range of map brushes:
    /// <c>MakeBspBrushList</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="startBrush">The first map brush index.</param>
    /// <param name="endBrush">One past the last map brush index.</param>
    /// <param name="clipMins">The block's minimum.</param>
    /// <param name="clipMaxs">The block's maximum.</param>
    /// <param name="detail">Which brushes to take.</param>
    /// <returns>The head of the list, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// <b>The list comes out in REVERSE map order</b>, because each brush is
    /// pushed onto the head. That is not incidental: it is
    /// the order <c>ChopBrushes</c> then visits pairs in and the order
    /// <c>SelectSplitSide</c> scores candidate planes in, and both of those
    /// decide output. Building it forwards would be a different compile.
    /// </remarks>
    public static BspBrush? MakeBspBrushList(
        BspBuildContext context,
        int startBrush,
        int endBrush,
        Vec3 clipMins,
        Vec3 clipMaxs,
        DetailScreen detail)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.ComputeBoundingPlanes(clipMins, clipMaxs);

        BspBrush? list = null;

        for (int i = startBrush; i < endBrush; i++)
        {
            MapBrush mb = context.Map.Brushes[i];

            if (detail != DetailScreen.FullDetail)
            {
                bool onlyDetail = detail == DetailScreen.OnlyDetail;
                bool isDetail = (mb.Contents & (int)BrushContents.Detail) != 0;
                if (onlyDetail ^ isDetail)
                {
                    continue;
                }
            }

            BspBrush? newBrush = CreateClippedBrush(context, mb, clipMins, clipMaxs);
            if (newBrush is not null)
            {
                newBrush.Next = list;
                list = newBrush;
            }
        }

        return list;
    }

    /// <summary>
    /// The same, from an explicit set of brushes: <c>MakeBspBrushList</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brushes">The map brushes, in the order they are taken.</param>
    /// <param name="clipMins">The block's minimum.</param>
    /// <param name="clipMaxs">The block's maximum.</param>
    /// <returns>The head of the list, or null.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// The overload that takes no detail screen, because its caller
    /// (<c>MergeDetailTree</c>, Phase 3d) has already selected the brushes. It
    /// reverses the given order for the same reason the range overload does.
    /// </remarks>
    public static BspBrush? MakeBspBrushList(
        BspBuildContext context,
        IReadOnlyList<MapBrush> brushes,
        Vec3 clipMins,
        Vec3 clipMaxs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brushes);

        context.ComputeBoundingPlanes(clipMins, clipMaxs);

        BspBrush? list = null;

        for (int i = 0; i < brushes.Count; i++)
        {
            BspBrush? newBrush = CreateClippedBrush(context, brushes[i], clipMins, clipMaxs);
            if (newBrush is not null)
            {
                newBrush.Next = list;
                list = newBrush;
            }
        }

        return list;
    }

    /// <summary>
    /// Appends one list to another and returns the new tail:
    /// <c>AddBrushListToTail</c>.
    /// </summary>
    /// <param name="list">The list to append, or null.</param>
    /// <param name="tail">The last element of the list being appended to.</param>
    /// <returns>The new last element.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tail"/> is null.</exception>
    /// <remarks>
    /// It relinks one element at a time rather than joining the two chains, so
    /// the appended list keeps its order. Stock's own name for the function
    /// in the comment above it is <c>AddBspBrushListToTail</c>, which is not
    /// what it is called.
    /// </remarks>
    public static BspBrush AddBrushListToTail(BspBrush? list, BspBrush tail)
    {
        ArgumentNullException.ThrowIfNull(tail);

        BspBrush current = tail;

        for (BspBrush? walk = list; walk is not null;)
        {
            BspBrush? next = walk.Next;
            walk.Next = null;
            current.Next = walk;
            current = walk;
            walk = next;
        }

        return current;
    }

    /// <summary>
    /// Rebuilds a list without one brush, REVERSED: <c>CullList</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="list">The head of the list to rebuild.</param>
    /// <param name="skip">The brush to leave out, which is freed.</param>
    /// <returns>The head of the new list.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="skip"/> is null.
    /// </exception>
    /// <remarks>
    /// <b>The reversal is the behaviour, not a detail of the implementation.</b>
    /// Every element is pushed onto the head of a new list, so the order comes
    /// out backwards — and <c>ChopBrushes</c> calls this on every successful
    /// bite and then rescans from the top. The order brushes are compared in
    /// therefore flips on each bite, and which of two equally good subtractions
    /// is taken depends on it.
    /// </remarks>
    public static BspBrush? CullList(BspBuildContext context, BspBrush? list, BspBrush skip)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(skip);

        BspBrush? newList = null;

        for (BspBrush? current = list; current is not null;)
        {
            BspBrush? next = current.Next;

            if (ReferenceEquals(current, skip))
            {
                context.FreeBrush(current);
                current = next;
                continue;
            }

            current.Next = newList;
            newList = current;
            current = next;
        }

        return newList;
    }

    /// <summary>How many brushes are in a list: <c>CountBrushList</c>,
    /// </summary>
    /// <param name="brushes">The head of the list, or null.</param>
    /// <returns>The count.</returns>
    public static int CountBrushList(BspBrush? brushes)
    {
        int c = 0;
        for (BspBrush? b = brushes; b is not null; b = b.Next)
        {
            c++;
        }

        return c;
    }

    /// <summary>
    /// Whether <paramref name="b1"/> is allowed to bite <paramref name="b2"/>:
    /// <c>BrushGE</c>.
    /// </summary>
    /// <param name="b1">The biter.</param>
    /// <param name="b2">The bitten.</param>
    /// <returns>True when the bite is allowed.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <para>
    /// Four rules, in order, and the order matters because the first one wins:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// An areaportal may bite water or slime, whatever else is true — the
    /// exception <c>FixupAreaportalWaterBrushes</c> exists to prepare.
    /// </description></item>
    /// <item><description>
    /// A detail brush never bites a structural one.
    /// </description></item>
    /// <item><description>
    /// A solid brush bites anything.
    /// </description></item>
    /// <item><description>
    /// Two transparent brushes (grate or window) bite each other, with stock's
    /// note that they "are not marked as detail anymore".
    /// </description></item>
    /// </list>
    /// <para>
    /// It reads <c>original-&gt;contents</c>, the WHOLE map brush's contents,
    /// never the carved fragment's sides — so a fragment's own materials do not
    /// enter into it.
    /// </para>
    /// </remarks>
    public static bool BrushGreaterOrEqual(BspBrush b1, BspBrush b2)
    {
        ArgumentNullException.ThrowIfNull(b1);
        ArgumentNullException.ThrowIfNull(b2);

        int c1 = b1.Original!.Contents;
        int c2 = b2.Original!.Contents;

        if ((c2 & SplitAreaPortalMask) != 0 && (c1 & (int)BrushContents.AreaPortal) != 0)
        {
            return true;
        }

        if ((c1 & (int)BrushContents.Detail) != 0 && (c2 & (int)BrushContents.Detail) == 0)
        {
            return false;
        }

        if ((c1 & (int)BrushContents.Solid) != 0)
        {
            return true;
        }

        return (c1 & TransparentContents) != 0 && (c2 & TransparentContents) != 0;
    }

    /// <summary>
    /// Carves intersecting brushes apart: <c>ChopBrushes</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="head">The head of the list, consumed.</param>
    /// <returns>The head of the non-overlapping list, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>The <c>goto newlist</c> is kept, and so is the visit order it
    /// produces.</b> Every successful bite restarts the whole O(n²) scan from a
    /// list that <see cref="CullList"/> has just reversed and
    /// <see cref="AddBrushListToTail"/> has just extended. Which brush is
    /// compared against which, and therefore which of several possible
    /// carvings the map ends up with, is a function of that sequence. A tidier
    /// loop — a queue, a worklist, a sort — is a different compiler.
    /// </para>
    /// <para>
    /// The fragmentation rule is the other half of the
    /// answer: when BOTH directions would produce more than one fragment,
    /// neither bite is taken and the brushes are left overlapping, unless they
    /// are both detail or either is an areaportal. Stock's comment
    /// ("commening this out allows full fragmentation") says what the
    /// alternative would be.
    /// </para>
    /// <para>
    /// <b>The early <c>return NULL</c> at the top discards everything already
    /// kept.</b> It is stock's, and it is only
    /// reachable on the first pass: after a bite, <see cref="CullList"/> is
    /// called either from <c>b1</c> skipping <c>b1</c> — and a bite requires a
    /// <c>b2</c> after <c>b1</c>, so at least that survives — or from
    /// <c>b1</c> skipping <c>b2</c>, which leaves <c>b1</c>. So the list is
    /// never empty at the label except when the caller passed an empty one,
    /// and the discarded <c>keep</c> is then empty too.
    /// </para>
    /// </remarks>
    public static BspBrush? ChopBrushes(BspBuildContext context, BspBrush? head)
    {
        ArgumentNullException.ThrowIfNull(context);

        BspBrush? keep = null;

        // The list from b1 to the tail does not change between two restarts
        // (every change is followed by goto newlist), so each restart lays it
        // out once, in list order, as the brushes and their boxes side by side.
        // The scan then visits exactly stock's pairs in stock's order; the
        // array only makes the box half of BrushesDisjoint -- the test that
        // rejects almost every pair -- a walk over contiguous floats instead of
        // a pointer chase (plan 3p: it was 6% of a goldrush compile).
        List<BspBrush> order = [];
        List<BrushBox> boxes = [];

    newlist:
        if (head is null)
        {
            return null;
        }

        order.Clear();
        boxes.Clear();
        BspBrush tail = head;
        for (BspBrush? b = head; b is not null; b = b.Next)
        {
            order.Add(b);
            boxes.Add(new BrushBox(b.Mins, b.Maxs));
            tail = b;
        }

        for (int i = 0; i < order.Count; i++)
        {
            BspBrush b1 = order[i];
            BrushBox box1 = boxes[i];

            for (int j = i + 1; j < order.Count; j++)
            {
                if (box1.Disjoint(boxes[j]))
                {
                    continue;
                }

                BspBrush b2 = order[j];
                if (MirroredPlanes(b1, b2))
                {
                    continue;
                }

                BspBrush? sub = null;
                BspBrush? sub2 = null;
                int c1 = 999999;
                int c2 = 999999;

                if (BrushGreaterOrEqual(b2, b1))
                {
                    sub = SubtractBrush(context, b1, b2);
                    if (ReferenceEquals(sub, b1))
                    {
                        continue;
                    }

                    if (sub is null)
                    {
                        head = CullList(context, b1, b1);
                        goto newlist;
                    }

                    c1 = CountBrushList(sub);
                }

                if (BrushGreaterOrEqual(b1, b2))
                {
                    sub2 = SubtractBrush(context, b2, b1);
                    if (ReferenceEquals(sub2, b2))
                    {
                        // Stock leaks `sub` here. Nothing reads it
                        // again, and freeing it would change which arena slots
                        // later windings are recycled into.
                        continue;
                    }

                    if (sub2 is null)
                    {
                        context.FreeBrushList(sub);
                        head = CullList(context, b1, b2);
                        goto newlist;
                    }

                    c2 = CountBrushList(sub2);
                }

                if (sub is null && sub2 is null)
                {
                    continue;
                }

                if (c1 > 1 && c2 > 1)
                {
                    int contents1 = b1.Original!.Contents;
                    int contents2 = b2.Original!.Contents;

                    if ((contents1 & contents2 & (int)BrushContents.Detail) == 0
                        && ((contents1 | contents2) & (int)BrushContents.AreaPortal) == 0)
                    {
                        context.FreeBrushList(sub2);
                        context.FreeBrushList(sub);
                        continue;
                    }
                }

                if (c1 < c2)
                {
                    context.FreeBrushList(sub2);
                    tail = AddBrushListToTail(sub, tail);
                    head = CullList(context, b1, b1);
                }
                else
                {
                    context.FreeBrushList(sub);
                    tail = AddBrushListToTail(sub2, tail);
                    head = CullList(context, b1, b2);
                }

                goto newlist;
            }

            // b1 is no longer intersecting anything, so keep it.
            b1.Next = keep;
            keep = b1;
        }

        return keep;
    }
}
