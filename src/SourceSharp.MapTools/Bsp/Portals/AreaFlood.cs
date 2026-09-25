using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// The two portals that led into the two areas an areaportal separates
/// (<c>entity_t::m_pPortalsLeadingIntoAreas</c>).
/// </summary>
/// <param name="Into0">The portal through which area <c>portalareas[0]</c> reached it.</param>
/// <param name="Into1">The portal through which area <c>portalareas[1]</c> reached it.</param>
public readonly record struct AreaPortalLink(Portal? Into0, Portal? Into1);

/// <summary>
/// Dividing the map into areas, bounded by areaportal brushes
/// (<c>src/utils/vbsp/portals.cpp:819-1381</c>).
/// </summary>
/// <remarks>
/// <para>
/// An area is a connected set of leaves you can walk between without crossing
/// an areaportal. The flood is the entity flood again with one extra rule:
/// areaportal leaves are flooded INTO and never out of, so reaching one just
/// records "area N is on this side of you" on the areaportal's entity and
/// stops.
/// </para>
/// <para>
/// The count this produces is <see cref="AreaCount"/> and it is NOT what ends
/// up in <c>LUMP_AREAS</c>: <c>EmitAreaPortals</c> writes
/// <c>numareas = c_areas + 1</c>, leaving index 0 as a placeholder. So a plain
/// sealed map with one area writes two entries, and a map with one areaportal
/// writes three. Any reasoning that starts "a sealed room is one area" gets the
/// lump wrong by one.
/// </para>
/// </remarks>
public sealed class AreaFlood
{
    /// <summary>An areaportal brush was found touching more than two areas.</summary>
    public const string AreaportalTouchesTooManyAreas = "VBSP0305";

    /// <summary>An areaportal brush does not have an area on both sides.</summary>
    public const string AreaportalTouchesOneArea = "VBSP0306";

    private readonly IReadOnlyList<MapEntity> _entities;
    private readonly Dictionary<MapEntity, AreaPortalLink> _links = [];

    /// <summary>Creates an area flooder over the map's entity list.</summary>
    /// <param name="entities">The map's entities; areaportal brushes name one by index.</param>
    public AreaFlood(IReadOnlyList<MapEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        _entities = entities;
    }

    /// <summary>Where warnings about areaportals are collected.</summary>
    public IList<CompileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// <c>c_areas</c>: how many areas were found. <c>LUMP_AREAS</c> gets one
    /// more than this.
    /// </summary>
    public int AreaCount { get; private set; }

    /// <summary>
    /// For each areaportal entity, the portals the two areas reached it
    /// through. <c>EmitAreaPortals</c> needs these to pick the portal that
    /// actually divides the two areas.
    /// </summary>
    public IReadOnlyDictionary<MapEntity, AreaPortalLink> Links => _links;

    /// <summary>
    /// The leak line an areaportal produced, if one did.
    /// </summary>
    public LeakReport? AreaportalLeak { get; private set; }

    /// <summary>
    /// The first brush in a leaf whose original is an areaportal
    /// (<c>AreaportalBrushForNode</c>, <c>portals.cpp:260</c>).
    /// </summary>
    /// <param name="node">The areaportal leaf.</param>
    /// <returns>That brush.</returns>
    /// <exception cref="MapCompileException">The leaf has no areaportal brush.</exception>
    /// <remarks>
    /// "Because of water areaportals support, the areaportal may not be the
    /// only brush on this node" — stock's own comment, and the reason this
    /// searches rather than taking the head of the list. Stock then
    /// dereferences the result without a null check, so a leaf carrying
    /// <c>CONTENTS_AREAPORTAL</c> with no areaportal brush in it crashes vbsp;
    /// here it throws.
    /// </remarks>
    public static MapBrush AreaportalBrushForNode(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        foreach (MapBrush brush in node.LeafBrushes)
        {
            if ((brush.Contents & PortalContents.AreaPortal) != 0)
            {
                return brush;
            }
        }

        throw new MapCompileException("AreaportalBrushForNode: no areaportal brush in an areaportal leaf");
    }

    /// <summary>
    /// Gives every node the area its children agree on, or -1
    /// (<c>SetNodeAreaIndices_R</c>, <c>portals.cpp:1273</c>).
    /// </summary>
    /// <param name="node">The root of the subtree.</param>
    /// <remarks>Leaf areas must already be set; this only fills in the nodes.</remarks>
    public static void SetNodeAreaIndices(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsLeaf())
        {
            return;
        }

        SetNodeAreaIndices(node.Front!);
        SetNodeAreaIndices(node.Back!);

        node.Area = node.Front!.Area == node.Back!.Area ? node.Front.Area : -1;
    }

    /// <summary>
    /// Marks each leaf with an area, bounded by areaportals
    /// (<c>FloodAreas</c>, <c>portals.cpp:1372</c>).
    /// </summary>
    /// <param name="tree">The flooded, filled tree.</param>
    /// <param name="windings">The arena holding the portal windings.</param>
    /// <returns><see cref="AreaCount"/>, stock's <c>c_areas</c>.</returns>
    public int FloodAreas(IBspTree tree, WindingArena windings)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(windings);

        FindAreas(tree.HeadNode);
        SetAreaPortalAreas(tree, windings, tree.HeadNode);

        return AreaCount;
    }

    /// <summary>
    /// Descends the tree, starting a new area at every reachable leaf that does
    /// not have one (<c>FindAreas_r</c>, <c>portals.cpp:896</c>).
    /// </summary>
    /// <param name="node">The root of the subtree.</param>
    public void FindAreas(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.IsLeaf())
        {
            FindAreas(node.Front!);
            FindAreas(node.Back!);
            return;
        }

        if (node.Area != 0)
        {
            return;     // allready got it
        }

        if ((node.Contents & PortalContents.Solid) != 0)
        {
            return;
        }

        if (node.Occupied == 0)
        {
            return;     // not reachable by entities
        }

        // area portals are allways only flooded into, never out of
        if (node.IsAreaportal())
        {
            return;
        }

        AreaCount++;
        FloodArea(node);
    }

    /// <summary>
    /// Floods one area outward from a leaf
    /// (<c>FloodAreas_r</c>, <c>portals.cpp:831</c>).
    /// </summary>
    /// <param name="start">The leaf to start from.</param>
    /// <remarks>
    /// Walked on an explicit stack rather than by recursion, for the same
    /// reason as <see cref="EntityFlood.FloodPortals"/>: one frame per leaf is
    /// too many on a real map. The order is stock's — neighbours pushed in
    /// reverse so the first portal is taken first — and the "already has an
    /// area" test sits where stock's does, at the top of the visit.
    /// </remarks>
    public void FloodArea(IBspNode start)
    {
        ArgumentNullException.ThrowIfNull(start);

        Stack<(IBspNode Node, Portal? SeeThrough)> pending = new();
        List<Portal> neighbours = [];

        pending.Push((start, null));

        while (pending.TryPop(out (IBspNode Node, Portal? SeeThrough) item))
        {
            IBspNode node = item.Node;

            if (node.IsAreaportal())
            {
                RecordAreaportalTouch(node, item.SeeThrough);
                continue;
            }

            if (node.Area != 0)
            {
                continue;   // allready got it
            }

            node.Area = AreaCount;

            neighbours.Clear();

            for (Portal? p = node.Portals; p is not null;)
            {
                int side = p.SideOf(node);

                if (PortalContents.EntityFlood(p))
                {
                    neighbours.Add(p);
                }

                p = p.NextAt(side);
            }

            for (int i = neighbours.Count - 1; i >= 0; i--)
            {
                Portal p = neighbours[i];
                pending.Push((p.NodeAt(1 - p.SideOf(node))!, p));
            }
        }
    }

    /// <summary>
    /// Gives each areaportal leaf the area recorded on its entity, and reports
    /// the ones that only ever touched one
    /// (<c>SetAreaPortalAreas_r</c>, <c>portals.cpp:982</c>).
    /// </summary>
    /// <param name="tree">The tree, for the leak trace.</param>
    /// <param name="windings">The arena holding the portal windings.</param>
    /// <param name="node">The root of the subtree.</param>
    public void SetAreaPortalAreas(IBspTree tree, WindingArena windings, IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(node);

        if (!node.IsLeaf())
        {
            SetAreaPortalAreas(tree, windings, node.Front!);
            SetAreaPortalAreas(tree, windings, node.Back!);
            return;
        }

        if (!node.IsAreaportal())
        {
            return;
        }

        if (node.Area != 0)
        {
            return;     // already set
        }

        MapBrush brush = AreaportalBrushForNode(node);
        MapEntity entity = _entities[brush.EntityNumber];
        node.Area = entity.PortalAreas[0];

        if (entity.PortalAreas[1] == 0)
        {
            ReportAreaportalLeak(tree, windings, node);
            Diagnostics.Add(new CompileDiagnostic(
                AreaportalTouchesOneArea,
                DiagnosticSeverity.Warning,
                $"Brush {brush.Id}: areaportal brush doesn't touch two areas",
                new MapLocation(EntityId: brush.EntityNumber, BrushId: brush.Id)));
        }
    }

    /// <summary>
    /// Builds the leak line around an areaportal that did not seal
    /// (<c>ReportAreaportalLeak</c>, <c>portals.cpp:924</c>).
    /// </summary>
    /// <param name="tree">The tree.</param>
    /// <param name="windings">The arena holding the portal windings.</param>
    /// <param name="node">The areaportal leaf.</param>
    /// <remarks>
    /// <b>The second loop here advances along the wrong link and that is
    /// stock's code, not a porting slip.</b> At <c>portals.cpp:952</c> the loop
    /// increment is <c>p = p-&gt;next[s]</c>, but on the iteration that skips
    /// <c>pStart</c> the <c>continue</c> jumps over the line that would have
    /// set <c>s</c> for this portal, so the step uses whatever <c>s</c> was
    /// left over — for the very first iteration, the
    /// <c>s = (pStart-&gt;nodes[0] == node)</c> assigned just above the loop,
    /// which is the COMPLEMENT of the convention every other loop in the file
    /// uses. The walk then continues along the other node's portal list. It is
    /// reproduced exactly, because the only thing downstream of it is which
    /// <c>.lin</c> a broken areaportal draws.
    /// </remarks>
    public void ReportAreaportalLeak(IBspTree tree, WindingArena windings, IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(node);

        Portal? start = null;
        int s = 0;

        // Find a portal out of this areaportal into empty space
        for (Portal? p = node.Portals; p is not null;)
        {
            s = p.SideOf(node);

            if (!PortalContents.EntityFlood(p))
            {
                p = p.NextAt(s);
                continue;
            }

            if ((p.NodeAt(1 - s)!.Contents & PortalContents.AreaPortal) != 0)
            {
                p = p.NextAt(s);
                continue;
            }

            start = p;
            break;
        }

        if (start is null)
        {
            return;
        }

        s = ReferenceEquals(start.FrontNode, node) ? 1 : 0;
        IBspNode firstSide = start.NodeAt(s)!;

        // flood fill the area outside this areaportal brush
        EntityFlood.FloodAreaLeakFrom(tree.HeadNode, firstSide);

        // find the portal into the longest path around the portal
        Portal? best = null;
        int bestDist = 0;

        for (Portal? p = node.Portals; p is not null;)
        {
            if (ReferenceEquals(p, start))
            {
                // StockQuirk.AreaportalLeakWalk, portals.cpp:952. Stock's
                // `continue` jumps over the `s = ...` below, so the step uses
                // the value left from above the loop -- the COMPLEMENT of the
                // convention every other loop in the file uses -- and the walk
                // proceeds into the other node's portal list.
                if (!windings.Compliance.Emulates(StockQuirk.AreaportalLeakWalk))
                {
                    s = p.SideOf(node);
                }

                p = p.NextAt(s);
                continue;
            }

            s = p.SideOf(node);

            if (p.NodeAt(1 - s)!.Occupied > bestDist)
            {
                best = p;
                bestDist = p.NodeAt(1 - s)!.Occupied;
            }

            p = p.NextAt(s);
        }

        if (best is null)
        {
            return;
        }

        int bestSide = ReferenceEquals(best.FrontNode, node) ? 1 : 0;

        // write the linefile that goes from pBest to pStart
        // Only the first report is a file: TraceAreaportal returns null once
        // the tree is marked leaked (leakfile.cpp:104), and that null must not
        // replace the report already made.
        AreaportalLeak ??= LeakTrace.TraceAreaportal(tree, windings, start, best, best.NodeAt(bestSide)!);
    }

    private void RecordAreaportalTouch(IBspNode node, Portal? seeThrough)
    {
        // this node is part of an area portal
        MapBrush brush = AreaportalBrushForNode(node);
        MapEntity entity = _entities[brush.EntityNumber];

        // if the current area has allready touched this portal, we are done
        if (entity.PortalAreas[0] == AreaCount || entity.PortalAreas[1] == AreaCount)
        {
            return;
        }

        // note the current area as bounding the portal
        if (entity.PortalAreas[1] != 0)
        {
            Diagnostics.Add(new CompileDiagnostic(
                AreaportalTouchesTooManyAreas,
                DiagnosticSeverity.Warning,
                $"areaportal entity {brush.EntityNumber} (brush {brush.Id}) touches > 2 areas",
                new MapLocation(EntityId: brush.EntityNumber, BrushId: brush.Id)));
            return;
        }

        _links.TryGetValue(entity, out AreaPortalLink link);

        if (entity.PortalAreas[0] != 0)
        {
            entity.PortalAreas[1] = AreaCount;
            _links[entity] = link with { Into1 = seeThrough };
        }
        else
        {
            entity.PortalAreas[0] = AreaCount;
            _links[entity] = link with { Into0 = seeThrough };
        }
    }
}
