//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// The geometric half of the reference implementation: bounds, volumes,
/// windings and the brush splitter everything else is built out of.
/// </summary>
/// <remarks>
/// It lives beside the CSG rather than beside the tree because
/// <see cref="SplitBrush"/> is what <c>SubtractBrush</c>,
/// <c>IntersectBrush</c> and <c>ClipBrushToBox</c> are made of
/// — CSG depends on it and the
/// tree depends on CSG, so putting it here is the arrangement with no cycle in
/// it. Stock has the cycle: and each call
/// into the other.
/// </remarks>
public static class BrushGeometry
{
    /// <summary>
    /// <c>EDGE_LENGTH</c>: how long an edge has to be
    /// to count.
    /// </summary>
    public const float EdgeLength = 0.2f;

    /// <summary>
    /// The same threshold spelled as stock spells it: <c>0.2</c> with no
    /// <c>f</c>, so a DOUBLE, which is what makes <c>0.2f</c> a long edge.
    /// </summary>
    /// <remarks>
    /// See <see cref="StockQuirk.WindingIsTinyEdgePromotion"/>. The twin of
    /// <c>TreePortals.StockEdgeLength</c>; a fact holds the two pairs equal so
    /// the two copies of <c>WindingIsTiny</c> cannot drift apart again, which
    /// they had.
    /// </remarks>
    public const double StockEdgeLength = 0.2;

    /// <summary>
    /// <c>PLANESIDE_EPSILON</c>: how far a brush may
    /// poke past a plane before the box test calls it a crossing.
    /// </summary>
    /// <remarks>
    /// <b>A double in stock, and the value matters more than the type.</b> The
    /// comment above it says "if a brush just barely pokes onto the other side,
    /// let it slide by without chopping", and the line under it,
    /// <c>//0.1</c>, records that it used to be 100 times larger. It is used
    /// only by <c>BrushBspBoxOnPlaneSide</c>, where it is compared against
    /// <c>float</c> distances, so the comparison is done in double in stock and
    /// in float here; at 0.001 against coordinates below 16384 the two agree,
    /// because every value on both sides of the comparison is exactly
    /// representable after the widening.
    /// </remarks>
    public const float PlaneSideEpsilon = 0.001f;

    /// <summary>
    /// Sets a brush's bounds from its side windings: <c>BoundBrush</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brush">The brush to bound.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// A brush with no windings at all comes back inside out — mins 99999,
    /// maxs -99999 — which is <c>ClearBounds</c>'s seed
    /// And is what <c>SplitBrush</c>'s
    /// out-of-range check then rejects.
    /// </remarks>
    public static void BoundBrush(BspBuildContext context, BspBrush brush)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        float minX = 99999f, minY = 99999f, minZ = 99999f;
        float maxX = -99999f, maxY = -99999f, maxZ = -99999f;

        for (int i = 0; i < brush.SideCount; i++)
        {
            Winding w = brush.Sides[i].Winding;
            if (w.IsNull)
            {
                continue;
            }

            foreach (Vec3 p in context.Windings.Points(w))
            {
                if (p.X < minX) { minX = p.X; }
                if (p.X > maxX) { maxX = p.X; }
                if (p.Y < minY) { minY = p.Y; }
                if (p.Y > maxY) { maxY = p.Y; }
                if (p.Z < minZ) { minZ = p.Z; }
                if (p.Z > maxZ) { maxZ = p.Z; }
            }
        }

        brush.Mins = new Vec3(minX, minY, minZ);
        brush.Maxs = new Vec3(maxX, maxY, maxZ);
    }

    /// <summary>
    /// A point somewhere inside the brush: <c>PointInsideBrush</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brush">The brush.</param>
    /// <returns>A point, or the origin when the search did not converge.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <para>
    /// Four relaxation passes from the origin: each pass pushes the point back
    /// inside every plane it is outside of, and stops early once a whole pass
    /// finds nothing to fix. Its only purpose is to give
    /// <see cref="CreateBrushWindings"/> a translation that puts the CSG near
    /// zero, so an imperfect answer costs precision and not correctness.
    /// </para>
    /// <para>
    /// <b>It returns the origin unchanged for a brush that does not contain
    /// it</b> — if four passes are not enough the last pass's partial
    /// corrections are still in the point, and if the brush is empty the loop
    /// finds nothing to correct. Stock has no fallback either.
    /// </para>
    /// </remarks>
    public static Vec3 PointInsideBrush(BspBuildContext context, BspBrush brush)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        Vec3 insidePoint = Vec3.Zero;

        bool inside = false;
        for (int k = 0; k < 4 && !inside; k++)
        {
            inside = true;
            for (int i = 0; i < brush.SideCount; i++)
            {
                Plane plane = context.Planes[brush.Sides[i].PlaneNumber];
                float d = Vec3.Dot(plane.Normal, insidePoint) - plane.Dist;
                if (d < 0)
                {
                    inside = false;
                    insidePoint -= plane.Normal * d;
                }
            }
        }

        return insidePoint;
    }

    /// <summary>
    /// Builds every side's winding from the brush's planes:
    /// <c>CreateBrushWindings</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brush">The brush, whose side windings are replaced.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <para>
    /// Not the same function as <see cref="MapFile.MakeBrushWindings"/>, and the
    /// differences are the point. This one translates the whole problem so that
    /// a point inside the brush sits at the origin before clipping and
    /// translates the result back ("translate the CSG problem to improve
    /// precision"); it clips with epsilon <b>0</b>
    /// rather than <c>CLIP_EPSILON</c>, with the 0.1 left in the source as a
    /// commented-out argument; and it does not
    /// discard a side whose winding came out null, where the map loader reports
    /// the brush.
    /// </para>
    /// <para>
    /// It skips bevels as clip planes but still
    /// builds a winding FOR one, so a bevel side ends up with the winding its
    /// plane cuts out of the rest of the hull rather than with none. Only
    /// <see cref="BrushFromBounds"/> reaches this function in the shipped
    /// compile, and a six-plane box has no bevels, so that case is unexercised
    /// by any map — see the facts.
    /// </para>
    /// </remarks>
    public static void CreateBrushWindings(BspBuildContext context, BspBrush brush)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        Vec3 insidePoint = PointInsideBrush(context, brush);
        Vec3 offset = -insidePoint;

        for (int i = 0; i < brush.SideCount; i++)
        {
            Plane plane = context.Planes[brush.Sides[i].PlaneNumber];
            Winding w = context.Windings.BaseWindingForPlane(
                plane.Normal, plane.Dist + Vec3.Dot(plane.Normal, offset));

            for (int j = 0; j < brush.SideCount && !w.IsNull; j++)
            {
                if (i == j)
                {
                    continue;
                }

                if (brush.Sides[j].Bevel)
                {
                    continue;
                }

                Plane opposite = context.Planes[brush.Sides[j].PlaneNumber ^ 1];
                w = context.Windings.ChopInPlace(
                    w, opposite.Normal, opposite.Dist + Vec3.Dot(opposite.Normal, offset), 0f);
            }

            if (!w.IsNull)
            {
                context.Windings.Translate(w, -offset);
            }

            brush.Sides[i].Winding = w;
        }

        BoundBrush(context, brush);
    }

    /// <summary>
    /// An axial brush filling a box: <c>BrushFromBounds</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="mins">The box's minimum.</param>
    /// <param name="maxs">The box's maximum.</param>
    /// <returns>A six-sided brush with no <see cref="BspBrush.Original"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The side order is <c>+X +Y +Z -X -Y -Z</c> — three maxs then three
    /// mins, at <c>sides[i]</c> and <c>sides[3+i]</c> — which is NOT the
    /// canonical order <c>AddBrushBevels</c> puts a map brush's sides in
    /// (<c>-X +X -Y +Y -Z +Z</c>). Nothing here depends on
    /// either order, but a reader comparing the two will notice.
    /// </para>
    /// <para>
    /// <b>It appends up to six planes to the table</b>, through
    /// <c>FindFloatPlane</c>. The node volume of every tree, and so of every
    /// block, is built this way, which is one of the two reasons the plane
    /// table grows during the BSP build rather than only during loading.
    /// </para>
    /// </remarks>
    public static BspBrush BrushFromBounds(BspBuildContext context, Vec3 mins, Vec3 maxs)
    {
        ArgumentNullException.ThrowIfNull(context);

        BspBrush b = context.AllocBrush(6);
        for (int i = 0; i < 6; i++)
        {
            b.AddSide(default);
        }

        for (int i = 0; i < 3; i++)
        {
            Vec3 positive = AxisVector(i, 1);
            b.Sides[i].PlaneNumber = context.Planes.Find(positive, maxs[i]);

            Vec3 negative = AxisVector(i, -1);
            b.Sides[3 + i].PlaneNumber = context.Planes.Find(negative, -mins[i]);
        }

        CreateBrushWindings(context, b);
        return b;
    }

    /// <summary>
    /// The brush's volume: <c>BrushVolume</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brush">The brush, or null.</param>
    /// <returns>The volume in cubic world units, or zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Tetrahedra from the first point of the first winding to every face,
    /// summed as <c>distance * area</c> and divided by three at the end.
    /// </para>
    /// <para>
    /// <b>The face the corner was taken from is included in the sum, not
    /// skipped.</b> Stock's second loop continues from the index the first one
    /// stopped at (<c>for (; i&lt;...</c>) rather than
    /// restarting, so that face contributes <c>d * area</c> with
    /// <c>d</c> the distance from a point ON its own plane, which is zero up to
    /// rounding. Restarting the loop at zero would be the same answer in exact
    /// arithmetic and a different float, so it continues here too.
    /// </para>
    /// </remarks>
    public static float BrushVolume(BspBuildContext context, BspBrush? brush)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (brush is null)
        {
            return 0f;
        }

        int i = 0;
        Winding w = Winding.Null;
        for (; i < brush.SideCount; i++)
        {
            w = brush.Sides[i].Winding;
            if (!w.IsNull)
            {
                break;
            }
        }

        if (w.IsNull)
        {
            return 0f;
        }

        Vec3 corner = context.Windings.Points(w)[0];

        float volume = 0f;
        for (; i < brush.SideCount; i++)
        {
            w = brush.Sides[i].Winding;
            if (w.IsNull)
            {
                continue;
            }

            Plane plane = context.Planes[brush.Sides[i].PlaneNumber];
            float d = -(Vec3.Dot(corner, plane.Normal) - plane.Dist);
            float area = context.Windings.Area(w);
            volume += d * area;
        }

        return volume / 3f;
    }

    /// <summary>
    /// Whether vertex snapping would erase the winding: <c>WindingIsTiny</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="winding">The winding.</param>
    /// <returns>True when fewer than three edges are longer than 0.2 units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// Stock carries a better version of this beside it — an OBB fit with the
    /// note "UNDONE: Test &amp; use this instead"
    /// — inside <c>#if 0</c>. It is not what shipped and it is not what this
    /// is.
    /// </remarks>
    public static bool WindingIsTiny(BspBuildContext context, Winding winding)
    {
        ArgumentNullException.ThrowIfNull(context);

        // StockQuirk.WindingIsTinyEdgePromotion. spells
        // EDGE_LENGTH `0.2` with no `f`, so the compare happens in double and
        // an edge of exactly 0.2f counts as LONG. This copy of the function
        // compared in float and so was already doing the right thing while
        // TreePortals.IsTiny did stock's -- the two are now one decision.
        bool stock = context.Windings.Compliance
            .Emulates(StockQuirk.WindingIsTinyEdgePromotion);

        Span<Vec3> p = context.Windings.Points(winding);
        int edges = 0;

        for (int i = 0; i < p.Length; i++)
        {
            int j = i == p.Length - 1 ? 0 : i + 1;
            float len = (p[j] - p[i]).Length();

            bool longEdge = stock ? len > StockEdgeLength : len > EdgeLength;

            if (longEdge && ++edges == 3)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the winding still reaches past the legal world:
    /// <c>WindingIsHuge</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="winding">The winding.</param>
    /// <returns>True when any coordinate is outside ±16384.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// Stock's comment says it detects "one of the points from basewinding for
    /// plane", which is the case it was written for: a base winding reaches
    /// 65536 units and anything that survives unclipped still carries one.
    /// </remarks>
    public static bool WindingIsHuge(BspBuildContext context, Winding winding)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (Vec3 p in context.Windings.Points(winding))
        {
            for (int j = 0; j < 3; j++)
            {
                if (p[j] < GeometryEpsilons.MinCoordInteger || p[j] > GeometryEpsilons.MaxCoordInteger)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Which side of a plane the brush is mostly on:
    /// <c>BrushMostlyOnSide</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="plane">The plane.</param>
    /// <returns>
    /// <see cref="Tree.PlaneSideFlags.Front"/> or
    /// <see cref="Tree.PlaneSideFlags.Back"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="brush"/> is null.
    /// </exception>
    /// <remarks>
    /// The furthest vertex wins, and ties go to whichever was seen first —
    /// both comparisons are strict <c>&gt;</c> against the running maximum,
    /// which starts at zero. A brush exactly ON the plane therefore comes back
    /// FRONT, because that is the initial value and nothing beats it.
    /// </remarks>
    public static int BrushMostlyOnSide(BspBuildContext context, BspBrush brush, Plane plane)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        float max = 0f;
        int side = Tree.PlaneSideFlags.Front;

        for (int i = 0; i < brush.SideCount; i++)
        {
            Winding w = brush.Sides[i].Winding;
            if (w.IsNull)
            {
                continue;
            }

            foreach (Vec3 p in context.Windings.Points(w))
            {
                float d = Vec3.Dot(p, plane.Normal) - plane.Dist;
                if (d > max)
                {
                    max = d;
                    side = Tree.PlaneSideFlags.Front;
                }

                if (-d > max)
                {
                    max = -d;
                    side = Tree.PlaneSideFlags.Back;
                }
            }
        }

        return side;
    }

    /// <summary>
    /// Duplicates a brush, its sides and its windings: <c>CopyBrush</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brush">The brush to copy.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <b>The copy inherits the original's id and its <see cref="BspBrush.Next"/>
    /// pointer</b>, because stock copies the whole header with one
    /// <c>memcpy</c> over the id
    /// <c>AllocBrush</c> had just assigned. Most callers overwrite
    /// <c>next</c> on the following line; <c>SplitBrush</c>'s two early returns
    /// Do not, so a brush that was
    /// "only on one side" leaves the splitter still linked to whatever the
    /// input was linked to. Nothing reads it before it is overwritten, and
    /// reproducing it costs one line.
    /// </remarks>
    public static BspBrush CopyBrush(BspBuildContext context, BspBrush brush)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        BspBrush copy = context.AllocBrush(brush.SideCount);

        copy.Id = brush.Id;
        copy.IdScope = brush.IdScope;
        copy.Next = brush.Next;
        copy.Mins = brush.Mins;
        copy.Maxs = brush.Maxs;
        copy.Side = brush.Side;
        copy.TestSide = brush.TestSide;
        copy.Original = brush.Original;

        for (int i = 0; i < brush.SideCount; i++)
        {
            BspBrushSide side = brush.Sides[i];
            if (!side.Winding.IsNull)
            {
                side.Winding = context.Windings.Copy(side.Winding);
            }

            copy.AddSide(side);
        }

        return copy;
    }

    /// <summary>
    /// Splits a brush by a plane, leaving the original alone:
    /// <c>SplitBrush</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brush">The brush to split.</param>
    /// <param name="planeNumber">The splitting plane.</param>
    /// <param name="front">The part in front, or null.</param>
    /// <param name="back">The part behind, or null.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="brush"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Five things decide the answer and four of them are thresholds, so they
    /// are listed rather than left in the code:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The 0.1 in <c>d_front &lt; 0.1</c> and <c>d_back &gt; -0.1</c>
    /// — not
    /// <see cref="PlaneSideEpsilon"/>, which the commented-out text beside it
    /// says it once was. A brush within a tenth of a unit of the plane is
    /// "only on one side" and is copied whole.
    /// </description></item>
    /// <item><description>
    /// The midwinding is cut from a base winding by EVERY side, bevels
    /// Included — unlike
    /// <see cref="CreateBrushWindings"/>, which skips them — with epsilon 0.
    /// </description></item>
    /// <item><description>
    /// A tiny or absent midwinding sends the whole brush to
    /// <see cref="BrushMostlyOnSide"/>, so the split silently does not happen.
    /// </description></item>
    /// <item><description>
    /// A half with fewer than three sides, or any bound outside ±16384, is
    /// discarded; if exactly one half survives that, the OTHER half's copy of
    /// the whole brush is returned, not the survivor.
    /// </description></item>
    /// <item><description>
    /// A half whose volume is under 1.0 cubic unit is dropped, at the very end,
    /// after both halves are otherwise complete — so a split can legitimately
    /// return two nulls.
    /// </description></item>
    /// </list>
    /// <para>
    /// The arithmetic is done translated to the brush's own centre
    /// (<c>-0.5 * (mins + maxs)</c>) and translated
    /// back, which is why <c>ClipWindingEpsilon_Offset</c> exists at all.
    /// </para>
    /// <para>
    /// <b>Two windings are leaked on purpose.</b> The tiny-midwinding return
    /// And the one-sided return
    /// Both drop <c>w</c> without freeing it. Both
    /// are reproduced: freeing them would change which arena slots later
    /// windings are recycled into, and the whole point of this stage is that
    /// nothing downstream can tell this apart from stock.
    /// </para>
    /// </remarks>
    public static void SplitBrush(
        BspBuildContext context,
        BspBrush brush,
        int planeNumber,
        out BspBrush? front,
        out BspBrush? back)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        front = null;
        back = null;

        WindingArena arena = context.Windings;
        Plane plane = context.Planes[planeNumber];

        float dFront = 0f;
        float dBack = 0f;

        for (int i = 0; i < brush.SideCount; i++)
        {
            Winding sw = brush.Sides[i].Winding;
            if (sw.IsNull)
            {
                continue;
            }

            foreach (Vec3 p in arena.Points(sw))
            {
                float d = Vec3.Dot(p, plane.Normal) - plane.Dist;
                if (d > 0 && d > dFront) { dFront = d; }
                if (d < 0 && d < dBack) { dBack = d; }
            }
        }

        if (dFront < 0.1f)
        {
            back = CopyBrush(context, brush);
            return;
        }

        if (dBack > -0.1f)
        {
            front = CopyBrush(context, brush);
            return;
        }

        Vec3 offset = (brush.Mins + brush.Maxs) * -0.5f;

        Winding w = arena.BaseWindingForPlane(
            plane.Normal, plane.Dist + Vec3.Dot(plane.Normal, offset));

        for (int i = 0; i < brush.SideCount && !w.IsNull; i++)
        {
            Plane plane2 = context.Planes[brush.Sides[i].PlaneNumber ^ 1];
            w = arena.ChopInPlace(
                w, plane2.Normal, plane2.Dist + Vec3.Dot(plane2.Normal, offset), 0f);
        }

        if (w.IsNull || WindingIsTiny(context, w))
        {
            // The winding is deliberately not freed: see the remarks.
            int side = BrushMostlyOnSide(context, brush, plane);
            if (side == Tree.PlaneSideFlags.Front)
            {
                front = CopyBrush(context, brush);
            }

            if (side == Tree.PlaneSideFlags.Back)
            {
                back = CopyBrush(context, brush);
            }

            return;
        }

        if (WindingIsHuge(context, w))
        {
            context.Diagnostics.Add(new CompileDiagnostic(
                BspBuildCodes.HugeWinding, DiagnosticSeverity.Warning, "huge winding"));
        }

        arena.Translate(w, -offset);
        Winding midwinding = w;

        BspBrush?[] b = new BspBrush?[2];
        for (int i = 0; i < 2; i++)
        {
            b[i] = context.AllocBrush(brush.SideCount + 1);
            b[i]!.Original = brush.Original;
        }

        for (int i = 0; i < brush.SideCount; i++)
        {
            BspBrushSide s = brush.Sides[i];
            if (s.Winding.IsNull)
            {
                continue;
            }

            arena.ClipEpsilonOffset(
                s.Winding, plane.Normal, plane.Dist, 0f, offset,
                out Winding cwFront, out Winding cwBack);

            for (int j = 0; j < 2; j++)
            {
                Winding cw = j == 0 ? cwFront : cwBack;
                if (cw.IsNull)
                {
                    continue;
                }

                BspBrushSide cs = s;
                cs.Winding = cw;
                cs.Tested = false;
                b[j]!.AddSide(cs);
            }
        }

        for (int i = 0; i < 2; i++)
        {
            BoundBrush(context, b[i]!);

            int j = 0;
            for (; j < 3; j++)
            {
                if (b[i]!.Mins[j] < GeometryEpsilons.MinCoordInteger
                    || b[i]!.Maxs[j] > GeometryEpsilons.MaxCoordInteger)
                {
                    context.Diagnostics.Add(new CompileDiagnostic(
                        BspBuildCodes.BogusBrushAfterClip,
                        DiagnosticSeverity.Warning,
                        "bogus brush after clip"));
                    break;
                }
            }

            if (b[i]!.SideCount < 3 || j < 3)
            {
                context.FreeBrush(b[i]!);
                b[i] = null;
            }
        }

        if (b[0] is null || b[1] is null)
        {
            context.Diagnostics.Add(b[0] is null && b[1] is null
                ? new CompileDiagnostic(
                    BspBuildCodes.SplitRemovedBrush,
                    DiagnosticSeverity.Warning,
                    "split removed brush")
                : new CompileDiagnostic(
                    BspBuildCodes.SplitNotOnBothSides,
                    DiagnosticSeverity.Warning,
                    "split not on both sides"));

            if (b[0] is not null)
            {
                context.FreeBrush(b[0]!);
                front = CopyBrush(context, brush);
            }

            if (b[1] is not null)
            {
                context.FreeBrush(b[1]!);
                back = CopyBrush(context, brush);
            }

            // The midwinding is deliberately not freed: see the remarks.
            return;
        }

        for (int i = 0; i < 2; i++)
        {
            b[i]!.AddSide(new BspBrushSide
            {
                PlaneNumber = planeNumber ^ i ^ 1,
                TexInfo = BspBrushSide.TexInfoNode,
                Displacement = null,
                Visible = false,
                Tested = false,
                Winding = i == 0 ? arena.Copy(midwinding) : midwinding,
            });
        }

        for (int i = 0; i < 2; i++)
        {
            if (BrushVolume(context, b[i]) < 1.0f)
            {
                context.FreeBrush(b[i]!);
                b[i] = null;
            }
        }

        front = b[0];
        back = b[1];
    }

    /// <summary>The unit vector along one axis.</summary>
    /// <param name="axis">0, 1 or 2.</param>
    /// <param name="direction">-1 or 1.</param>
    /// <returns>The vector.</returns>
    internal static Vec3 AxisVector(int axis, int direction) => axis switch
    {
        0 => new Vec3(direction, 0f, 0f),
        1 => new Vec3(0f, direction, 0f),
        _ => new Vec3(0f, 0f, direction),
    };
}
