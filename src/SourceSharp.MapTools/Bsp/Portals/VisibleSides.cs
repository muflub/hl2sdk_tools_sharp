using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// Choosing a brush side to texture each portal, and marking the sides that
/// turned into visible faces
/// </summary>
/// <remarks>
/// This is what decides whether a brush side ever becomes a face. A portal
/// separates two leaves whose contents differ; the strongest bit of that
/// difference says what KIND of surface it is, and then the brush side nearest
/// the portal's own plane, among brushes in either leaf that carry that
/// content bit, is the one that gets to draw it.
/// </remarks>
public sealed class VisibleSides
{
    /// <summary>No brush side could be matched to a portal.</summary>
    public const string PortalSideNotFound = "VBSP0307";

    /// <summary>The best brush side for a portal was a long way from it.</summary>
    public const string PortalSideBadMatch = "VBSP0308";

    /// <summary>How many bad matches are reported before the rest are suppressed.</summary>
    public const int BadMatchWarningLimit = 8;

    private readonly WindingArena _arena;
    private readonly PlaneTable _planes;
    private readonly IReadOnlyList<MapBrushSide> _brushSides;
    private int _badMatchWarnings;

    /// <summary>Creates the side finder over one map's geometry.</summary>
    /// <param name="windings">The arena holding the portal windings.</param>
    /// <param name="planes">The map's plane table.</param>
    /// <param name="brushSides">The map's shared brush side list.</param>
    public VisibleSides(WindingArena windings, PlaneTable planes, IReadOnlyList<MapBrushSide> brushSides)
    {
        ArgumentNullException.ThrowIfNull(windings);
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(brushSides);
        _arena = windings;
        _planes = planes;
        _brushSides = brushSides;
    }

    /// <summary>Where warnings about unmatched portals are collected.</summary>
    public IList<CompileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// The total absolute distance of a winding's points from a plane, giving
    /// up once it passes a bound
    /// (<c>ComputeDistFromPlane</c>).
    /// </summary>
    /// <param name="arena">The arena holding the winding.</param>
    /// <param name="winding">The winding to measure.</param>
    /// <param name="plane">The plane to measure against.</param>
    /// <param name="maxDist">Stop and return as soon as the running total passes this.</param>
    /// <returns>The total, or the first running total that exceeded <paramref name="maxDist"/>.</returns>
    /// <remarks>
    /// The early return means the value is NOT the true total once it exceeds
    /// the bound — but every caller only compares it against that same bound,
    /// so the early value is always on the losing side of the comparison
    /// anyway.
    /// </remarks>
    public static float ComputeDistFromPlane(WindingArena arena, Winding winding, Plane plane, float maxDist)
    {
        ArgumentNullException.ThrowIfNull(arena);

        float total = 0f;

        foreach (Vec3 point in arena.Points(winding))
        {
            total += MathF.Abs(Vec3.Dot(plane.Normal, point) - plane.Dist);

            if (total > maxDist)
            {
                return total;
            }
        }

        return total;
    }

    /// <summary>
    /// Finds the brush side that should texture a portal
    /// (<c>FindPortalSide</c>).
    /// </summary>
    /// <param name="portal">The portal to match.</param>
    /// <remarks>
    /// <para>
    /// An exact plane match wins immediately, and the comparison is
    /// <c>(side.PlaneNumber &amp; ~1) == portal.OnNode.PlaneNumber</c> — the
    /// side's plane is normalised to the even half of its pair and the node's
    /// is not, so a node splitting on an ODD plane index can never take the
    /// exact-match path and always falls through to the distance search. That
    /// is stock's comparison, written exactly as stock writes it.
    /// </para>
    /// <para>
    /// Otherwise the winner is the side with the smallest summed distance from
    /// the portal's points, ties going to the first side visited: front leaf
    /// before back leaf, brush list order, then side order within the brush.
    /// </para>
    /// </remarks>
    public void FindPortalSide(Portal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);

        // decide which content change is strongest: solid > lava > water, etc
        int visContents = PortalContents.VisibleContents(
            portal.FrontNode!.Contents ^ portal.BackNode!.Contents);

        if (visContents == 0)
        {
            return;
        }

        // Compares `side->planenum & ~1` against the node's
        // RAW planenum. That is not a quirk worth a compliance switch: a node
        // is always on the even half of its plane pair -- BuildTree_r stores
        // `bestside->planenum & ~1` and BlockTree's axial
        // splits take FindFloatPlane's positive-normal half, which is stored
        // first -- so masking
        // the node too is identical on every tree stock builds, and it stays
        // right if a tree ever does carry an odd node.
        // ComplianceQuirkEffectTests.StockNeverPutsANodeOnAnOddPlane reads
        // that off every node of the stock catalogue.
        int planeNumber = portal.OnNode!.PlaneNumber & ~1;
        MapBrushSide? bestSide = null;
        float bestDist = 1000000f;

        // Stock leaves the loops with `goto gotit` on an exact plane match. A
        // distance of zero is NOT the same condition: a side can score zero on
        // the distance search and stock keeps looking, because a later exact
        // match would still replace it.
        bool exactMatch = false;

        for (int j = 0; j < 2 && !exactMatch; j++)
        {
            IBspNode n = portal.NodeAt(j)!;

            foreach (MapBrush brush in n.LeafBrushes)
            {
                if ((brush.Contents & visContents) == 0)
                {
                    continue;
                }

                for (int i = 0; i < brush.SideCount; i++)
                {
                    MapBrushSide side = _brushSides[brush.FirstSide + i];

                    if (side.Bevel)
                    {
                        continue;
                    }

                    if (side.TexInfo == TexInfoTable.TexInfoNode)
                    {
                        continue;   // non-visible
                    }

                    if ((side.PlaneNumber & ~1) == planeNumber)
                    {
                        // exact match
                        bestSide = side;
                        bestDist = 0f;
                        exactMatch = true;
                        break;
                    }

                    Plane p2 = _planes[side.PlaneNumber & ~1];
                    float dist = ComputeDistFromPlane(_arena, portal.Winding, p2, bestDist);

                    if (dist < bestDist)
                    {
                        bestSide = side;
                        bestDist = dist;
                    }
                }

                if (exactMatch)
                {
                    break;
                }
            }
        }

        if (bestSide is null)
        {
            Diagnostics.Add(new CompileDiagnostic(
                PortalSideNotFound,
                DiagnosticSeverity.Warning,
                "side not found for portal"));
        }

        // Compute average dist, check for problems...
        int pointCount = _arena.Points(portal.Winding).Length;

        if (pointCount > 0 && bestDist / pointCount > 2f && _badMatchWarnings < BadMatchWarningLimit)
        {
            _badMatchWarnings++;
            Vec3 center = _arena.Center(portal.Winding);

            Diagnostics.Add(new CompileDiagnostic(
                PortalSideBadMatch,
                DiagnosticSeverity.Warning,
                "FindPortalSide: Couldn't find a good match for which brush to assign to a portal. " +
                $"Leaf 0 contents: 0x{portal.FrontNode.Contents:x}, leaf 1 contents: 0x{portal.BackNode.Contents:x}, " +
                $"viscontents: 0x{visContents:x}",
                new MapLocation(Position: (center.X, center.Y, center.Z))));
        }

        portal.SideFound = true;
        portal.Side = bestSide;
    }

    /// <summary>
    /// Marks every brush side that a portal chose
    /// (<c>MarkVisibleSides_r</c>).
    /// </summary>
    /// <param name="node">The root of the subtree.</param>
    public void MarkVisibleSidesRecursive(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.IsLeaf())
        {
            MarkVisibleSidesRecursive(node.Front!);
            MarkVisibleSidesRecursive(node.Back!);
            return;
        }

        // empty leafs are never boundary leafs
        if (node.Contents == 0)
        {
            return;
        }

        // see if there is a visible face
        for (Portal? p = node.Portals; p is not null;)
        {
            int mine = ReferenceEquals(p.FrontNode, node) ? 0 : 1;

            if (p.OnNode is null)
            {
                p = p.NextAt(mine);
                continue;   // edge of world
            }

            if (!p.SideFound)
            {
                FindPortalSide(p);
            }

            if (p.Side is not null)
            {
                p.Side.Visible = true;
            }

            p = p.NextAt(mine);
        }
    }

    /// <summary>
    /// Clears the visible flag on a range of brushes and then sets it on every
    /// side a portal chose
    /// (<c>MarkVisibleSides</c>).
    /// </summary>
    /// <param name="tree">The portalised tree.</param>
    /// <param name="brushes">The map's brush list.</param>
    /// <param name="startBrush">The first brush index to clear.</param>
    /// <param name="endBrush">One past the last brush index to clear.</param>
    /// <param name="detailScreen">Which brushes in that range to clear.</param>
    public void MarkVisibleSides(
        IBspTree tree,
        IReadOnlyList<MapBrush> brushes,
        int startBrush,
        int endBrush,
        DetailScreen detailScreen)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(brushes);

        // clear all the visible flags
        for (int i = startBrush; i < endBrush; i++)
        {
            MapBrush brush = brushes[i];

            if (detailScreen != DetailScreen.FullDetail)
            {
                bool onlyDetail = detailScreen == DetailScreen.OnlyDetail;
                bool detail = (brush.Contents & PortalContents.Detail) != 0;

                if (onlyDetail ^ detail)
                {
                    // both of these must have the same value or we're not
                    // interested in this brush
                    continue;
                }
            }

            ClearVisible(brush);
        }

        // set visible flags on the sides that are used by portals
        MarkVisibleSidesRecursive(tree.HeadNode);
    }

    /// <summary>
    /// The occluder overload: clear a named set of brushes, then mark
    /// (<c>MarkVisibleSides</c>).
    /// </summary>
    /// <param name="tree">The portalised tree.</param>
    /// <param name="brushes">The brushes to clear.</param>
    public void MarkVisibleSides(IBspTree tree, IEnumerable<MapBrush> brushes)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(brushes);

        foreach (MapBrush brush in brushes)
        {
            ClearVisible(brush);
        }

        MarkVisibleSidesRecursive(tree.HeadNode);
    }

    private void ClearVisible(MapBrush brush)
    {
        for (int j = 0; j < brush.SideCount; j++)
        {
            _brushSides[brush.FirstSide + j].Visible = false;
        }
    }
}
