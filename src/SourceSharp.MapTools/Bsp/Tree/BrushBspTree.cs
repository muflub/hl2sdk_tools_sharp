//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// The tree-building half of the reference implementation: the split
/// heuristic, the recursive build and the leaf classification.
/// </summary>
public static class BrushBspTree
{
    /// <summary>
    /// Which side of a plane a box is on: <c>BrushBspBoxOnPlaneSide</c>,
    /// </summary>
    /// <param name="mins">The box's minimum.</param>
    /// <param name="maxs">The box's maximum.</param>
    /// <param name="plane">The plane.</param>
    /// <param name="type">The plane's STORED type, from the plane table.</param>
    /// <returns>A <see cref="PlaneSideFlags"/> combination.</returns>
    /// <remarks>
    /// <para>
    /// <b>The axial and non-axial paths do not agree, and stock knows.</b> The
    /// axial path is symmetric: front if <c>maxs &gt; dist + eps</c>, back if
    /// <c>mins &lt; dist - eps</c>. The general path is not: it is
    /// <c>dist1 &gt;= eps</c> for front and <c>dist2 &lt; eps</c> for back —
    /// one inclusive, one exclusive, both against <c>+eps</c>. So a box lying
    /// exactly on a non-axial plane comes back <see cref="PlaneSideFlags.Both"/>
    /// where the same box on an axial plane comes back 0. This is stock's
    /// asymmetry, not a transcription slip, and it is why the plane's stored
    /// type has to be the one the table recorded rather than one recomputed
    /// from the normal.
    /// </para>
    /// <para>
    /// A result of 0 — neither front nor back — is possible, and
    /// <c>SelectSplitSide</c> counts it as neither.
    /// </para>
    /// </remarks>
    public static int BoxOnPlaneSide(Vec3 mins, Vec3 maxs, Plane plane, PlaneType type)
    {
        if (type < PlaneType.AnyX)
        {
            int axial = 0;
            int axis = (int)type;

            if (maxs[axis] > plane.Dist + BrushGeometry.PlaneSideEpsilon)
            {
                axial |= PlaneSideFlags.Front;
            }

            if (mins[axis] < plane.Dist - BrushGeometry.PlaneSideEpsilon)
            {
                axial |= PlaneSideFlags.Back;
            }

            return axial;
        }

        Span<float> leading = stackalloc float[3];
        Span<float> trailing = stackalloc float[3];

        for (int i = 0; i < 3; i++)
        {
            if (plane.Normal[i] < 0)
            {
                leading[i] = mins[i];
                trailing[i] = maxs[i];
            }
            else
            {
                trailing[i] = mins[i];
                leading[i] = maxs[i];
            }
        }

        Vec3 corner0 = new(leading[0], leading[1], leading[2]);
        Vec3 corner1 = new(trailing[0], trailing[1], trailing[2]);

        float dist1 = Vec3.Dot(plane.Normal, corner0) - plane.Dist;
        float dist2 = Vec3.Dot(plane.Normal, corner1) - plane.Dist;

        int side = 0;
        if (dist1 >= BrushGeometry.PlaneSideEpsilon)
        {
            side = PlaneSideFlags.Front;
        }

        if (dist2 < BrushGeometry.PlaneSideEpsilon)
        {
            side |= PlaneSideFlags.Back;
        }

        return side;
    }

    /// <summary>
    /// Which side of a plane a box is on, counting a side only when the box
    /// reaches <see cref="SplitOnPlaneEpsilon"/> or more across the plane:
    /// the side test <see cref="TestBrushToPlaneNumber"/> uses under
    /// <see cref="CompliancePolicy.Correct"/>.
    /// </summary>
    /// <param name="mins">The box's minimum.</param>
    /// <param name="maxs">The box's maximum.</param>
    /// <param name="plane">The plane.</param>
    /// <param name="type">The plane's stored type, from the plane table.</param>
    /// <returns>
    /// <see cref="PlaneSideFlags.Front"/>, <see cref="PlaneSideFlags.Back"/>
    /// or <see cref="PlaneSideFlags.Both"/>; never 0.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>What it replaces.</b> <see cref="BoxOnPlaneSide"/> calls a box in
    /// front when its leading corner is
    /// <see cref="BrushGeometry.PlaneSideEpsilon"/> (0.001) or more in front,
    /// and behind when its trailing corner is under +0.001 (the general path)
    /// or more than 0.001 behind (the axial one). A brush that only TOUCHES a
    /// candidate plane, with a vertex at the corner of its own box, has that
    /// corner a rounding residual away from the plane, and a residual is as
    /// likely to be 0.0016 as 0.0004: on 2fort, a slanted plane
    /// (0, -0.8437, -0.5369) at -1884.74 moved by about 0.0016 at a brush
    /// corner 2000 units out when <see cref="StockQuirk.PlaneFromPointsNormalise"/>
    /// was flipped, and the brush went from behind to both. The split
    /// heuristic counts a both-sided brush on each side, so the plane's
    /// <c>abs(front - back)</c> term moved by one, another plane won the node
    /// and the tree below it changed. The epsilon-brush penalty sees the same
    /// flip: a brush the box calls both-sided goes on to the vertex loop and
    /// can cost the plane 1000 points, where one the box calls one-sided
    /// costs nothing.
    /// </para>
    /// <para>
    /// <b>Why 0.1.</b> What the side test predicts is what
    /// <see cref="SplitBrushList"/> will do with the brush, and a both-sided
    /// brush is handed to <see cref="BrushGeometry.SplitBrush"/>, which does
    /// not cut a brush whose furthest vertex is under 0.1 across: it copies
    /// the brush whole to the other side, to the back when neither side
    /// reaches 0.1. A box reaching under 0.1 across the plane has every
    /// vertex under 0.1 across, so calling it both-sided predicts a cut that
    /// will not happen. This test uses SplitBrush's own line, so it calls a
    /// side only where SplitBrush would put a piece, and the list the node
    /// is split into is the same brush for brush. 0.1 is also over ten times
    /// the rounding a winding vertex carries (about 0.007 at worst), where
    /// 0.001 is inside it.
    /// </para>
    /// <para>
    /// <b>Only the band is changed.</b> A box that reaches 0.1 or more across
    /// on both sides is both-sided, as stock has it, even when the brush's
    /// vertices do not (a wedge whose box corner overhangs a slanted plane):
    /// that is the box test's approximation, not a rounding cliff, and it is
    /// left alone. Front is <c>d &gt;= 0.1</c> and back <c>d &lt;= -0.1</c>
    /// on both paths, the complements of SplitBrush's "not in front" (under
    /// 0.1) and "not behind" (over -0.1). A box inside the band on both sides
    /// is behind, which is where SplitBrush sends such a brush, so unlike
    /// <see cref="BoxOnPlaneSide"/> this never answers 0 and never drops a
    /// brush from the split.
    /// </para>
    /// </remarks>
    public static int BoxOnPlaneSideBeyondBand(Vec3 mins, Vec3 maxs, Plane plane, PlaneType type)
    {
        float dFront;
        float dBack;

        if (type < PlaneType.AnyX)
        {
            int axis = (int)type;
            dFront = maxs[axis] - plane.Dist;
            dBack = mins[axis] - plane.Dist;
        }
        else
        {
            // The same corners, picked the same way, as BoxOnPlaneSide.
            Span<float> leading = stackalloc float[3];
            Span<float> trailing = stackalloc float[3];

            for (int i = 0; i < 3; i++)
            {
                if (plane.Normal[i] < 0)
                {
                    leading[i] = mins[i];
                    trailing[i] = maxs[i];
                }
                else
                {
                    leading[i] = maxs[i];
                    trailing[i] = mins[i];
                }
            }

            dFront = Vec3.Dot(plane.Normal, new Vec3(leading[0], leading[1], leading[2])) - plane.Dist;
            dBack = Vec3.Dot(plane.Normal, new Vec3(trailing[0], trailing[1], trailing[2])) - plane.Dist;
        }

        int side = 0;
        if (dFront >= SplitOnPlaneEpsilon)
        {
            side = PlaneSideFlags.Front;
        }

        if (dBack <= -SplitOnPlaneEpsilon)
        {
            side |= PlaneSideFlags.Back;
        }

        return side == 0 ? PlaneSideFlags.Back : side;
    }

    /// <summary>
    /// The cheap version of the split test, without counting real splits:
    /// <c>QuickTestBrushToPlanenum</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brush">The brush to test.</param>
    /// <param name="planeNumber">The plane.</param>
    /// <param name="splits">Three when the box straddles the plane, else zero.</param>
    /// <returns>A <see cref="PlaneSideFlags"/> combination.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="brush"/> is null.
    /// </exception>
    /// <remarks>
    /// <b>Nothing in the shipped compiler calls it.</b> It is the estimate
    /// <c>SelectSplitSide</c> would use if the exact count were too expensive,
    /// and its "three splits" guess for a straddling brush is a constant. It is
    /// ported because it is in the file and because Phase 3p's parallel scorer
    /// is the obvious place it would come back — with a fact pinning what it
    /// answers, so that reviving it is a measurement and not a rewrite.
    /// </remarks>
    public static int QuickTestBrushToPlaneNumber(
        BspBuildContext context,
        BspBrush brush,
        int planeNumber,
        out int splits)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        splits = 0;

        for (int i = 0; i < brush.SideCount; i++)
        {
            int num = brush.Sides[i].PlaneNumber;
            if (num == planeNumber)
            {
                return PlaneSideFlags.Back | PlaneSideFlags.Facing;
            }

            if (num == (planeNumber ^ 1))
            {
                return PlaneSideFlags.Front | PlaneSideFlags.Facing;
            }
        }

        int s = BoxOnPlaneSide(
            brush.Mins, brush.Maxs, context.Planes[planeNumber], context.Planes.TypeOf(planeNumber));

        if (s == PlaneSideFlags.Both)
        {
            splits += 3;
        }

        return s;
    }

    /// <summary>
    /// How close to a candidate plane a vertex has to be for the split
    /// heuristic's epsilon-brush test to call it ON the plane rather than in
    /// front of or behind it, under <see cref="CompliancePolicy.Correct"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it guards.</b> <see cref="TestBrushToPlaneNumber"/> charges a
    /// candidate plane 1000 points for every brush that "only just" crosses
    /// it: whose furthest vertex in front is under one unit in front, or
    /// whose furthest vertex behind is under one unit behind. Stock tests
    /// "in front" as <c>d &gt; 0</c>. A brush that merely TOUCHES the plane
    /// with a vertex or an edge therefore counts or not by the sign of that
    /// vertex's rounding residual, which is noise: a last-bit change anywhere
    /// upstream (a normal normalised with the <c>rsqrtss</c> estimate rather
    /// than a divide, or a different CPU's estimate) flips it, the plane gains
    /// or loses 1000 points, and a different plane splits the node. On 2fort
    /// that moves the cluster count by up to 24 depending on which of three
    /// normalise quirks is on (see <see cref="StockQuirk.SplitEpsilonBrushOnPlane"/>).
    /// </para>
    /// <para>
    /// <b>How big the noise is.</b> Brush windings are cut from a base winding
    /// whose corners are pushed out to <see cref="GeometryEpsilons.BaseWindingExtent"/>
    /// (65536) units, in single precision. A float between 65536 and 131072
    /// has an ulp of 1/128, so the first clips round each coordinate of a new
    /// corner by up to half of that, about 0.004, and a unit normal turns an
    /// error of <c>e</c> in each of three coordinates into at most
    /// <c>sqrt(3) * e</c>, about 0.007, of distance. The dot product against
    /// the plane adds a rounding of its own at the map's coordinates (an ulp
    /// of 1/512 at 16384). Measured on 2fort, a corner meant to be
    /// (452, 1911, 256) came out as (451.99756, 1911.0015, 256), a residual of
    /// 0.0029 against a plane through it, and exact corners read residuals
    /// like 6.1e-5 of either sign.
    /// </para>
    /// <para>
    /// <b>Why 0.1.</b> It is more than ten times the worst of that noise, and
    /// ten times under the one unit the test is about, so a real sliver (a
    /// brush crossing by a few tenths) is still charged. And it is not a new
    /// number: it is where <see cref="BrushGeometry.SplitBrush"/> already
    /// draws the line. A brush whose furthest vertex is under 0.1 across a
    /// plane is not split by it at all; SplitBrush hands the whole brush to
    /// the other side. So a brush inside the band is one that splitting on
    /// this plane would leave whole, and charging the plane for a sliver it
    /// would never cut was never the heuristic's intent. The split counter in
    /// the same loop uses the same 0.1 for the same reason.
    /// </para>
    /// <para>
    /// A vertex exactly 0.1 across counts as crossing, as it does in
    /// SplitBrush, whose test is <c>d_front &lt; 0.1</c> for "does not".
    /// </para>
    /// </remarks>
    public const float SplitOnPlaneEpsilon = 0.1f;

    /// <summary>
    /// How a plane would cut a brush: <c>TestBrushToPlanenum</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brush">The brush to test.</param>
    /// <param name="planeNumber">The plane.</param>
    /// <param name="splits">How many VISIBLE faces the plane would split.</param>
    /// <param name="hintSplit">Whether any of them is a hint face.</param>
    /// <param name="epsilonBrush">
    /// Incremented — not set — when the brush only just crosses the plane.
    /// </param>
    /// <returns>A <see cref="PlaneSideFlags"/> combination.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="brush"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A brush that HAS the plane returns immediately with
    /// <see cref="PlaneSideFlags.Facing"/> and no split count, which is what
    /// makes shared faces free.
    /// </para>
    /// <para>
    /// The split count is over sides that are visible, have a winding, and are
    /// not already on a node; a side crossing the plane counts unless it is
    /// <c>SURF_SKIP</c>. The per-point threshold is <b>0.1</b>, with the
    /// commented-out <c>PLANESIDE_EPSILON</c> beside it
    /// Recording that it is deliberately a
    /// hundred times looser than the box test's.
    /// </para>
    /// <para>
    /// <b><paramref name="epsilonBrush"/> accumulates across the whole brush
    /// list, and stock never resets it per brush.</b> <c>SelectSplitSide</c>
    /// zeroes it once per candidate plane and then
    /// passes the same variable to every brush, and <c>d_front</c>/<c>d_back</c>
    /// inside here also accumulate across every SIDE of one brush rather than
    /// being per-side. So "this plane only just clips something" is a property
    /// of the plane against the list, worth -1000 each in the score. Resetting
    /// either would be a different heuristic.
    /// </para>
    /// </remarks>
    public static int TestBrushToPlaneNumber(
        BspBuildContext context,
        BspBrush brush,
        int planeNumber,
        out int splits,
        out bool hintSplit,
        ref int epsilonBrush)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        splits = 0;
        hintSplit = false;

        for (int i = 0; i < brush.SideCount; i++)
        {
            int num = brush.Sides[i].PlaneNumber;
            if (num == planeNumber)
            {
                return PlaneSideFlags.Back | PlaneSideFlags.Facing;
            }

            if (num == (planeNumber ^ 1))
            {
                return PlaneSideFlags.Front | PlaneSideFlags.Facing;
            }
        }

        Plane plane = context.Planes[planeNumber];

        // StockQuirk.SplitSideTestBoxEpsilon. Stock calls a brush in front of
        // (or behind) the plane when its box reaches 0.001 across, so a brush
        // that only touches the plane is counted on both sides or one by the
        // rounding of its touching corner. Correct counts a side only when the
        // box reaches SplitOnPlaneEpsilon across, the band inside which
        // SplitBrush hands the brush over whole.
        int s = context.Windings.Compliance.Emulates(StockQuirk.SplitSideTestBoxEpsilon)
            ? BoxOnPlaneSide(brush.Mins, brush.Maxs, plane, context.Planes.TypeOf(planeNumber))
            : BoxOnPlaneSideBeyondBand(brush.Mins, brush.Maxs, plane, context.Planes.TypeOf(planeNumber));

        if (s != PlaneSideFlags.Both)
        {
            return s;
        }

        float dFront = 0f;
        float dBack = 0f;

        for (int i = 0; i < brush.SideCount; i++)
        {
            if (brush.Sides[i].TexInfo == BspBrushSide.TexInfoNode)
            {
                continue;
            }

            if (!brush.Sides[i].Visible)
            {
                continue;
            }

            Winding w = brush.Sides[i].Winding;
            if (w.IsNull)
            {
                continue;
            }

            bool front = false;
            bool back = false;

            foreach (Vec3 p in context.Windings.Points(w))
            {
                float d = Vec3.Dot(p, plane.Normal) - plane.Dist;

                if (d > dFront) { dFront = d; }
                if (d < dBack) { dBack = d; }

                if (d > 0.1f) { front = true; }
                if (d < -0.1f) { back = true; }
            }

            if (front && back && (brush.Sides[i].Surface & (int)SurfaceFlags.Skip) == 0)
            {
                splits++;
                if ((brush.Sides[i].Surface & (int)SurfaceFlags.Hint) != 0)
                {
                    hintSplit = true;
                }
            }
        }

        // StockQuirk.SplitEpsilonBrushOnPlane. Stock reads "only just crosses"
        // as any positive excursion, so a vertex lying on the plane counts or
        // not by the sign of its rounding residual. Correct calls anything
        // within SplitOnPlaneEpsilon on the plane, which is also where
        // SplitBrush stops calling it a crossing.
        bool stock = context.Windings.Compliance.Emulates(StockQuirk.SplitEpsilonBrushOnPlane);
        bool frontSliver = stock ? dFront > 0.0f : dFront >= SplitOnPlaneEpsilon;
        bool backSliver = stock ? dBack < 0.0f : dBack <= -SplitOnPlaneEpsilon;

        if ((frontSliver && dFront < 1.0f) || (backSliver && dBack > -1.0f))
        {
            epsilonBrush++;
        }

        return s;
    }

    /// <summary>
    /// Turns a node into a leaf and gives it its contents: <c>LeafNode</c>,
    /// </summary>
    /// <param name="node">The node to make a leaf.</param>
    /// <param name="brushes">The fragments that landed here.</param>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The contents is the OR of every fragment's ORIGINAL map brush contents —
    /// with one override. A solid brush all of whose sides carry
    /// <see cref="BspBrushSide.TexInfoNode"/> "eats everything": the leaf
    /// becomes exactly <c>CONTENTS_SOLID</c> and the loop breaks, discarding
    /// whatever was ORed in before it and never reaching what comes after. So
    /// a leaf that holds both a solid block and a water brush is solid, and
    /// which of them is first in the list decides whether the override fires
    /// before the water's bits are seen — but not the answer, because the break
    /// assigns rather than ORs.
    /// </para>
    /// <para>
    /// A leaf with no brushes at all keeps contents 0, which is empty space.
    /// </para>
    /// </remarks>
    public static void LeafNode(BspNode node, BspBrush? brushes)
    {
        ArgumentNullException.ThrowIfNull(node);

        node.PlaneNumber = BspNode.Leaf;
        node.Contents = 0;

        for (BspBrush? b = brushes; b is not null; b = b.Next)
        {
            if ((b.Original!.Contents & (int)BrushContents.Solid) != 0)
            {
                int i = 0;
                for (; i < b.SideCount; i++)
                {
                    if (b.Sides[i].TexInfo != BspBrushSide.TexInfoNode)
                    {
                        break;
                    }
                }

                if (i == b.SideCount)
                {
                    node.Contents = (int)BrushContents.Solid;
                    break;
                }
            }

            node.Contents |= b.Original.Contents;
        }

        node.BrushList = brushes;
    }

    /// <summary>
    /// Drops the areaportal fragments out of every leaf:
    /// <c>RemoveAreaPortalBrushes_R</c>.
    /// </summary>
    /// <param name="node">The subtree root.</param>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Stock's comment says what for: "We don't want them in the engine at
    /// runtime but we do want their flags in the leaves" — the leaf contents
    /// were already set by <see cref="LeafNode"/>, so removing the brushes now
    /// loses nothing.
    /// </para>
    /// <para>
    /// <b>The test is <c>==</c> and not <c>&amp;</c></b>
    /// A brush is removed only if areaportal is the
    /// WHOLE of its contents. That is what spares the water-areaportal combos
    /// <see cref="AreaportalWaterFixup"/> has just given extra bits to, which
    /// still have to be in the leaf for the water to work.
    /// </para>
    /// <para>
    /// The removal is a pointer walk here because it is one in stock: a
    /// <c>bspbrush_t **pPrev</c> that starts at <c>&amp;node-&gt;brushlist</c>,
    /// is written through on a removal and left where it is, and only advances
    /// to <c>&amp;b-&gt;next</c> on a keep. That is the correct idiom and it is
    /// checked rather than assumed — the facts remove the head, the tail, a run
    /// in the middle, and every brush.
    /// </para>
    /// <para>
    /// The removed brushes are NOT freed. Stock unlinks them and leaves them
    /// allocated, which is deliberate in stock's case only by accident — the
    /// tree is freed wholesale later — and is reproduced because freeing them
    /// would return arena slots to the free list at a different moment.
    /// </para>
    /// </remarks>
    public static void RemoveAreaPortalBrushes(BspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsLeaf)
        {
            BspBrush? prev = null;

            for (BspBrush? b = node.BrushList; b is not null; b = b.Next)
            {
                if (b.Original!.Contents == (int)BrushContents.AreaPortal)
                {
                    if (prev is null)
                    {
                        node.BrushList = b.Next;
                    }
                    else
                    {
                        prev.Next = b.Next;
                    }
                }
                else
                {
                    prev = b;
                }
            }

            return;
        }

        RemoveAreaPortalBrushes(node.Children[0]!);
        RemoveAreaPortalBrushes(node.Children[1]!);
    }

    /// <summary>
    /// Whether a plane already appears above this node:
    /// <c>CheckPlaneAgainstParents</c>.
    /// </summary>
    /// <param name="planeNumber">The candidate plane.</param>
    /// <param name="node">The node being split.</param>
    /// <returns>True when no ancestor uses it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    /// <remarks>
    /// Stock does not return anything: it calls <c>Error("Tried parent")</c>
    /// and exits the process. That is an assertion
    /// about the algorithm, not a report about the map — a plane reaching here
    /// twice means the <c>tested</c> flags or <c>TEXINFO_NODE</c> marking went
    /// wrong — so it comes back as a bool and
    /// <see cref="SelectSplitSide"/> throws, which is this port's line between
    /// "the map is bad" and "the compiler is".
    /// </remarks>
    public static bool CheckPlaneAgainstParents(int planeNumber, BspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        for (BspNode? p = node.Parent; p is not null; p = p.Parent)
        {
            if (p.PlaneNumber == planeNumber)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a plane actually divides the node's volume:
    /// <c>CheckPlaneAgainstVolume</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="planeNumber">The candidate plane.</param>
    /// <param name="node">The node being split.</param>
    /// <returns>True when both halves survive the split.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="node"/> is null.
    /// </exception>
    /// <remarks>
    /// It runs a whole <see cref="BrushGeometry.SplitBrush"/> and throws the
    /// result away, so every candidate plane costs a brush split of the node
    /// volume before it is even scored. Both of <c>SplitBrush</c>'s rejections
    /// — under three sides, and under one cubic unit of volume — count as
    /// "would produce a tiny volume" here.
    /// </remarks>
    public static bool CheckPlaneAgainstVolume(
        BspBuildContext context,
        int planeNumber,
        BspNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        BrushGeometry.SplitBrush(
            context, node.Volume!, planeNumber, out BspBrush? front, out BspBrush? back);

        bool good = front is not null && back is not null;

        if (front is not null)
        {
            context.FreeBrush(front);
        }

        if (back is not null)
        {
            context.FreeBrush(back);
        }

        return good;
    }

    /// <summary>
    /// Chooses the plane to split a node on: <c>SelectSplitSide</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brushes">The node's brush list.</param>
    /// <param name="node">The node being split.</param>
    /// <param name="bestSide">The winning side, when there is one.</param>
    /// <returns>True when a splitter was found; false makes the node a leaf.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="node"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A candidate plane is already used by an ancestor, or a facing brush
    /// reported splits. Both are stock <c>Error()</c> calls.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Two passes, and the first one that finds anything wins.</b> Pass 0
    /// considers only VISIBLE sides and pass 1 only non-visible ones — the test
    /// is <c>side-&gt;visible ^ (pass&lt;1)</c>,
    /// which reads backwards until you notice it is a skip. Structural geometry
    /// that a player can see therefore shapes the tree before geometry that
    /// cannot, and stock counts how often it had to fall through
    /// (<c>c_nonvis</c>).
    /// </para>
    /// <para>
    /// The score is
    /// <c>5*facing - 5*splits - abs(front-back)</c>, plus 5 for an axial plane,
    /// minus 1000 per epsilon brush, minus 500 for <c>SURF_TRANS</c>. Two
    /// overrides sit on top of it and are not adjustments but assignments:
    /// a plane that would split a hint face and is not itself a hint scores
    /// <c>-9999999</c>, and a water or slime side scores <c>+9999999</c>. So
    /// water always splits first and hints are never cut by anything but
    /// another hint, regardless of everything else.
    /// </para>
    /// <para>
    /// <b>The winner is the first plane to strictly EXCEED the running best</b>
    /// So ties go to whichever was scored first —
    /// which makes the order of the brush list, and the order of sides within a
    /// brush, part of the output. Phase 3p's parallel scorer has to reduce with
    /// that same tie-break.
    /// </para>
    /// <para>
    /// Four skips come before any scoring: bevels are never splitters, sides
    /// with no winding cannot split, sides already on a node are done, and
    /// <c>SURF_SKIP</c> sides are never chosen. A fifth, <c>tested</c>, is the
    /// memo — when a brush turns out to be FACING a candidate, every side of it
    /// sharing that plane is marked tested so the same plane is not scored
    /// again from another brush, and all the flags are cleared at the end.
    /// </para>
    /// </remarks>
    public static bool SelectSplitSide(
        BspBuildContext context,
        BspBrush? brushes,
        BspNode node,
        out BspBrushSide bestSide)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        bestSide = default;
        BspBrush? bestBrush = null;
        int bestIndex = -1;
        int bestValue = -99999;

        for (int pass = 0; pass < 2; pass++)
        {
            for (BspBrush? brush = brushes; brush is not null; brush = brush.Next)
            {
                for (int i = 0; i < brush.SideCount; i++)
                {
                    if (brush.Sides[i].Bevel
                        || brush.Sides[i].Winding.IsNull
                        || brush.Sides[i].TexInfo == BspBrushSide.TexInfoNode
                        || brush.Sides[i].Tested
                        || (brush.Sides[i].Surface & (int)SurfaceFlags.Skip) != 0
                        || brush.Sides[i].Visible != (pass < 1))
                    {
                        // The last test is stock's `side->visible ^ (pass<1)`
                        // On two ints that only ever hold 0
                        // or 1, which is a logical XOR: skip the side when its
                        // visibility does not match the pass. Pass 0 takes
                        // visible sides, pass 1 takes the rest.
                        continue;
                    }

                    int planeNumber = brush.Sides[i].PlaneNumber & ~1;

                    if (!CheckPlaneAgainstParents(planeNumber, node))
                    {
                        throw new InvalidOperationException(
                            $"SelectSplitSide: plane {planeNumber} is already used by a parent "
                            + "of this node (stock: Error \"Tried parent\")");
                    }

                    if (!CheckPlaneAgainstVolume(context, planeNumber, node))
                    {
                        continue;
                    }

                    int front = 0;
                    int back = 0;
                    int both = 0;
                    int facing = 0;
                    int splits = 0;
                    int epsilonBrush = 0;
                    bool hintSplit = false;

                    for (BspBrush? test = brushes; test is not null; test = test.Next)
                    {
                        int s = TestBrushToPlaneNumber(
                            context, test, planeNumber,
                            out int brushSplits, out hintSplit, ref epsilonBrush);

                        splits += brushSplits;
                        if (brushSplits != 0 && (s & PlaneSideFlags.Facing) != 0)
                        {
                            throw new InvalidOperationException(
                                "SelectSplitSide: PSIDE_FACING with splits");
                        }

                        test.TestSide = s;

                        if ((s & PlaneSideFlags.Facing) != 0)
                        {
                            facing++;
                            for (int j = 0; j < test.SideCount; j++)
                            {
                                if ((test.Sides[j].PlaneNumber & ~1) == planeNumber)
                                {
                                    test.Sides[j].Tested = true;
                                }
                            }
                        }

                        if ((s & PlaneSideFlags.Front) != 0) { front++; }
                        if ((s & PlaneSideFlags.Back) != 0) { back++; }
                        if (s == PlaneSideFlags.Both) { both++; }
                    }

                    // `both` is counted by stock and never read.
                    // It is kept so that the loop is the loop.
                    _ = both;

                    int value = (5 * facing) - (5 * splits) - Math.Abs(front - back);

                    if (context.Planes.TypeOf(planeNumber) < PlaneType.AnyX)
                    {
                        value += 5;
                    }

                    value -= epsilonBrush * 1000;

                    if ((brush.Sides[i].Surface & (int)SurfaceFlags.Trans) != 0)
                    {
                        value -= 500;
                    }

                    if (hintSplit && (brush.Sides[i].Surface & (int)SurfaceFlags.Hint) == 0)
                    {
                        value = -9999999;
                    }

                    if ((brush.Sides[i].Contents
                        & (int)(BrushContents.Water | BrushContents.Slime)) != 0)
                    {
                        value = 9999999;
                    }

                    if (value > bestValue)
                    {
                        bestValue = value;
                        bestBrush = brush;
                        bestIndex = i;

                        for (BspBrush? test = brushes; test is not null; test = test.Next)
                        {
                            test.Side = test.TestSide;
                        }
                    }
                }
            }

            if (bestBrush is not null)
            {
                if (pass > 0)
                {
                    context.NonVisibleNodes++;
                }

                break;
            }
        }

        for (BspBrush? brush = brushes; brush is not null; brush = brush.Next)
        {
            for (int i = 0; i < brush.SideCount; i++)
            {
                brush.Sides[i].Tested = false;
            }
        }

        // Taken AFTER the tested flags are cleared, because stock returns a
        // POINTER into the winning brush and the caller reads it afterwards.
        // Copying at the moment the winner was chosen would freeze a `tested`
        // that stock's pointer sees cleared.
        if (bestBrush is null)
        {
            return false;
        }

        bestSide = bestBrush.Sides[bestIndex];
        return true;
    }

    /// <summary>
    /// Divides a brush list by a node's plane: <c>SplitBrushList</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brushes">The list to divide. Not freed here.</param>
    /// <param name="node">The node, whose plane and saved per-brush sides are read.</param>
    /// <param name="front">The front list.</param>
    /// <param name="back">The back list.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="node"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// It does not re-test anything: it reads <see cref="BspBrush.Side"/>,
    /// which <see cref="SelectSplitSide"/> saved when the winning plane was
    /// scored. Stock's comment says so ("save off the side test so we don't
    /// need to recalculate it").
    /// </para>
    /// <para>
    /// <b>A brush FACING the plane has that plane's sides marked
    /// <see cref="BspBrushSide.TexInfoNode"/></b>,
    /// on the copy, so the face is now the node's and will never be considered
    /// as a splitter or emitted as geometry again. The test is on
    /// <c>planenum &amp; ~1</c>, so both orientations are consumed.
    /// </para>
    /// <para>
    /// A brush whose saved side is neither front, back nor both is silently
    /// dropped — the copy is made and then linked to nothing. That is stock's
    /// fall-through, and it is reachable, because
    /// <see cref="BoxOnPlaneSide"/> can return 0.
    /// </para>
    /// </remarks>
    public static void SplitBrushList(
        BspBuildContext context,
        BspBrush? brushes,
        BspNode node,
        out BspBrush? front,
        out BspBrush? back)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        front = null;
        back = null;

        for (BspBrush? brush = brushes; brush is not null; brush = brush.Next)
        {
            int sides = brush.Side;

            if (sides == PlaneSideFlags.Both)
            {
                BrushGeometry.SplitBrush(
                    context, brush, node.PlaneNumber,
                    out BspBrush? newFront, out BspBrush? newBack);

                if (newFront is not null)
                {
                    newFront.Next = front;
                    front = newFront;
                }

                if (newBack is not null)
                {
                    newBack.Next = back;
                    back = newBack;
                }

                continue;
            }

            BspBrush newBrush = BrushGeometry.CopyBrush(context, brush);

            if ((sides & PlaneSideFlags.Facing) != 0)
            {
                for (int i = 0; i < newBrush.SideCount; i++)
                {
                    if ((newBrush.Sides[i].PlaneNumber & ~1) == node.PlaneNumber)
                    {
                        newBrush.Sides[i].TexInfo = BspBrushSide.TexInfoNode;
                    }
                }
            }

            if ((sides & PlaneSideFlags.Front) != 0)
            {
                newBrush.Next = front;
                front = newBrush;
                continue;
            }

            if ((sides & PlaneSideFlags.Back) != 0)
            {
                newBrush.Next = back;
                back = newBrush;
            }
        }
    }

    /// <summary>
    /// Builds the tree under a node: <c>BuildTree_r</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="node">The node to fill in.</param>
    /// <param name="brushes">The brushes that reached it, consumed.</param>
    /// <returns>The same node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="node"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Recursion depth is bounded by the tree, which for a real map is a few
    /// dozen levels; stock recurses too. It is left recursive rather than
    /// turned into an explicit stack because Phase 3p's plan for this function
    /// is fork/join over the two children, which wants the recursive shape.
    /// </para>
    /// <para>
    /// <b>The order of the last four statements is the whole reason this
    /// works.</b> The brush list is split, then FREED; the two children are
    /// allocated (so their ids are consecutive and precede everything in either
    /// subtree); the node's volume is split into theirs; and only then does the
    /// recursion happen, front child first. Allocating the children before
    /// recursing is stock's own comment and it is
    /// what makes <c>node-&gt;id</c> a breadth-ish numbering rather than a
    /// depth-first one.
    /// </para>
    /// </remarks>
    public static BspNode BuildTree(BspBuildContext context, BspNode node, BspBrush? brushes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        return BuildTree(context, node, brushes, parallel: null, forkDepth: 0);
    }

    /// <summary>
    /// <see cref="BuildTree(BspBuildContext, BspNode, BspBrush?)"/>, building
    /// large enough subtree pairs on two threads.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="node">The node to fill in.</param>
    /// <param name="brushes">The brushes that reached it, consumed.</param>
    /// <param name="parallel">How to fork, or null for the serial recursion.</param>
    /// <param name="forkDepth">How many forks deep this node already is.</param>
    /// <returns>The same node.</returns>
    /// <remarks>
    /// <para>
    /// Up to the two recursive calls this is the serial function, statement
    /// for statement: the plane choice, the list split, the free, the two
    /// child allocations and the volume split all happen on this context
    /// before anything forks, so the children's ids and everything above them
    /// are the serial build's.
    /// </para>
    /// <para>
    /// <b>The fork.</b> The back list and the back child's volume move into a
    /// <see cref="BspBuildContext.Fork"/>; the front subtree is built on this
    /// context and the back on the fork, on whichever threads
    /// <see cref="CallerParallelFor"/> gives them; and only once both are done
    /// is the fork joined, which rebases its ids and appends its diagnostics
    /// as though the back subtree had been built here after the front one.
    /// That is the serial order, so the result does not depend on which half
    /// finished first. If either half throws, the front's failure wins, as it
    /// would serially, and nothing is joined.
    /// </para>
    /// </remarks>
    internal static BspNode BuildTree(
        BspBuildContext context,
        BspNode node,
        BspBrush? brushes,
        BspTreeParallelism? parallel,
        int forkDepth)
    {
        parallel?.CancellationToken.ThrowIfCancellationRequested();

        context.Nodes++;

        if (!SelectSplitSide(context, brushes, node, out BspBrushSide bestSide))
        {
            node.HasSide = false;
            node.Side = default;
            node.PlaneNumber = BspNode.Leaf;
            LeafNode(node, brushes);
            return node;
        }

        node.Side = bestSide;
        node.HasSide = true;
        node.PlaneNumber = bestSide.PlaneNumber & ~1;

        SplitBrushList(context, brushes, node, out BspBrush? frontList, out BspBrush? backList);
        context.FreeBrushList(brushes);

        for (int i = 0; i < 2; i++)
        {
            BspNode child = context.AllocNode();
            child.Parent = node;
            node.Children[i] = child;
        }

        BrushGeometry.SplitBrush(
            context, node.Volume!, node.PlaneNumber,
            out BspBrush? frontVolume, out BspBrush? backVolume);

        node.Children[0]!.Volume = frontVolume;
        node.Children[1]!.Volume = backVolume;

        BspNode front = node.Children[0]!;
        BspNode back = node.Children[1]!;

        if (parallel?.Scheduler is { } scheduler
            && forkDepth < parallel.MaxForkDepth
            && HasAtLeast(frontList, parallel.MinBrushes)
            && HasAtLeast(backList, parallel.MinBrushes))
        {
            BspBuildContext fork = context.Fork();
            try
            {
                fork.TakeWindings(context, backList, backVolume);

                CallerParallelFor.For(
                    2,
                    2,
                    scheduler,
                    static () => 0,
                    (half, _) =>
                    {
                        if (half == 0)
                        {
                            BuildTree(context, front, frontList, parallel, forkDepth + 1);
                        }
                        else
                        {
                            BuildTree(fork, back, backList, parallel, forkDepth + 1);
                        }
                    },
                    parallel.CancellationToken);

                context.Join(fork, back);
            }
            finally
            {
                // Joined, failed or cancelled, the fork's windings are read
                // no more (Join copied the live ones home; a failed subtree
                // is never joined) and no helper is still running, so its
                // arena goes back for the next fork.
                fork.ReleaseForkWindings();
            }

            return node;
        }

        node.Children[0] = BuildTree(context, front, frontList, parallel, forkDepth);
        node.Children[1] = BuildTree(context, back, backList, parallel, forkDepth);

        return node;
    }

    /// <summary>Whether a brush list is at least <paramref name="count"/> long, without walking all of it.</summary>
    /// <param name="brushes">The list, or null.</param>
    /// <param name="count">The length to reach; zero or less is always reached.</param>
    /// <returns>True when the list has that many brushes.</returns>
    internal static bool HasAtLeast(BspBrush? brushes, int count)
    {
        for (BspBrush? b = brushes; b is not null && count > 0; b = b.Next)
        {
            count--;
        }

        return count <= 0;
    }

    /// <summary>
    /// The leaf a point falls in: <c>PointInLeaf</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="node">The subtree root.</param>
    /// <param name="point">The point.</param>
    /// <returns>The leaf.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="node"/> is null.
    /// </exception>
    /// <remarks>
    /// Takes the axial shortcut when the plane's stored type allows it, and
    /// sends a point exactly on the plane to the FRONT child (<c>d &gt;= 0</c>).
    /// <c>NodeForPoint</c> in the reference implementation is the same walk without the
    /// shortcut, and the two can therefore disagree for a point within a
    /// rounding step of an axial plane — see
    /// <see cref="TreeOperations.NodeForPoint"/>.
    /// </remarks>
    public static BspNode PointInLeaf(BspBuildContext context, BspNode node, Vec3 point)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        BspNode current = node;

        while (!current.IsLeaf)
        {
            Plane plane = context.Planes[current.PlaneNumber];
            PlaneType type = context.Planes.TypeOf(current.PlaneNumber);

            float d = type < PlaneType.AnyX
                ? point[(int)type] - plane.Dist
                : Vec3.Dot(point, plane.Normal) - plane.Dist;

            current = d >= 0 ? current.Children[0]! : current.Children[1]!;
        }

        return current;
    }

    /// <summary>
    /// Builds a tree from a brush list: <c>BrushBSP</c>,
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brushList">The brush list, consumed.</param>
    /// <param name="mins">The volume the tree spans.</param>
    /// <param name="maxs">The volume the tree spans.</param>
    /// <returns>The tree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>The tree's bounds are the BRUSHES' bounds, not the box it was asked
    /// for.</b> <paramref name="mins"/> and <paramref name="maxs"/> size the
    /// head node's VOLUME, while <see cref="BspTree.Mins"/> is accumulated from
    /// every brush in the list. For a block that is
    /// the difference between "this 1024-unit column" and "the part of it that
    /// has anything in it" — and the caller then overwrites both with the block
    /// Grid's own numbers anyway, which is why the
    /// accumulation is invisible in a world compile and visible in a submodel.
    /// </para>
    /// <para>
    /// The micro-brush warning is measured here, against
    /// <see cref="SourceSharp.MapTools.Options.VbspOptions.MicroVolume"/>, and
    /// it is a real <c>Warning()</c> rather than a <c>qprintf</c> — so it is
    /// the one diagnostic from this stage that a stock user sees without
    /// <c>-v</c>.
    /// </para>
    /// <para>
    /// When the context carries a <see cref="BspBuildContext.TreeParallelism"/>
    /// (the vbsp driver sets one), large subtree pairs are built on two
    /// threads; the tree, the counters and the diagnostics are the serial
    /// build's either way. See <see cref="BspTreeParallelism"/>.
    /// </para>
    /// </remarks>
    public static BspTree BrushBsp(
        BspBuildContext context,
        BspBrush? brushList,
        Vec3 mins,
        Vec3 maxs)
    {
        ArgumentNullException.ThrowIfNull(context);

        BspTree tree = new();

        float minX = 99999f, minY = 99999f, minZ = 99999f;
        float maxX = -99999f, maxY = -99999f, maxZ = -99999f;

        int brushCount = 0;
        int visibleFaces = 0;
        int nonVisibleFaces = 0;

        for (BspBrush? b = brushList; b is not null; b = b.Next)
        {
            brushCount++;

            float volume = BrushGeometry.BrushVolume(context, b);
            if (volume < context.Options.MicroVolume)
            {
                context.Diagnostics.Add(new CompileDiagnostic(
                    BspBuildCodes.MicroBrush,
                    DiagnosticSeverity.Warning,
                    "microbrush",
                    new MapLocation(BrushId: b.Original?.Id)));
            }

            for (int i = 0; i < b.SideCount; i++)
            {
                if (b.Sides[i].Bevel
                    || b.Sides[i].Winding.IsNull
                    || b.Sides[i].TexInfo == BspBrushSide.TexInfoNode)
                {
                    continue;
                }

                if (b.Sides[i].Visible)
                {
                    visibleFaces++;
                }
                else
                {
                    nonVisibleFaces++;
                }
            }

            if (b.Mins.X < minX) { minX = b.Mins.X; }
            if (b.Mins.Y < minY) { minY = b.Mins.Y; }
            if (b.Mins.Z < minZ) { minZ = b.Mins.Z; }
            if (b.Mins.X > maxX) { maxX = b.Mins.X; }
            if (b.Mins.Y > maxY) { maxY = b.Mins.Y; }
            if (b.Mins.Z > maxZ) { maxZ = b.Mins.Z; }

            if (b.Maxs.X < minX) { minX = b.Maxs.X; }
            if (b.Maxs.Y < minY) { minY = b.Maxs.Y; }
            if (b.Maxs.Z < minZ) { minZ = b.Maxs.Z; }
            if (b.Maxs.X > maxX) { maxX = b.Maxs.X; }
            if (b.Maxs.Y > maxY) { maxY = b.Maxs.Y; }
            if (b.Maxs.Z > maxZ) { maxZ = b.Maxs.Z; }
        }

        tree.Mins = new Vec3(minX, minY, minZ);
        tree.Maxs = new Vec3(maxX, maxY, maxZ);

        context.Nodes = 0;
        context.NonVisibleNodes = 0;

        BspNode node = context.AllocNode();
        node.Volume = BrushGeometry.BrushFromBounds(context, mins, maxs);

        tree.HeadNode = node;
        tree.HeadNode = BuildTree(context, node, brushList, context.TreeParallelism, forkDepth: 0);

        tree.Statistics = new BspTreeStatistics(
            brushCount,
            visibleFaces,
            nonVisibleFaces,
            (context.Nodes / 2) - context.NonVisibleNodes,
            context.NonVisibleNodes,
            (context.Nodes + 1) / 2);

        return tree;
    }
}
