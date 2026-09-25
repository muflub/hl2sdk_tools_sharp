using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// Every portal of a finished tree that <c>FindPortalsLeadingToArea_R</c>
/// (<c>portals.cpp:1158</c>) could ever return, grouped by the pair of areas
/// it joins, in the order that walk meets them.
/// </summary>
/// <remarks>
/// <para>
/// Stock walks the whole tree once per areaportal (<c>EmitClipPortalGeometry</c>,
/// <c>portals.cpp:1202</c>, from <c>EmitAreaPortals</c>). The walk's answer
/// depends only on the tree and on the two areas and the plane asked about,
/// and nothing changes the tree, its portals, their occupancy or their areas
/// while the areaportals are emitted. So this walks it once, keeps every
/// visit to a portal between two occupied leaves under its unordered area
/// pair, and answers each question from that pair's list with the same
/// plane test (plan 3p: the per-areaportal walk was 3% of a goldrush compile).
/// </para>
/// <para>
/// A visit is a (leaf, portal) step, exactly as the walk makes it, so a
/// portal between two occupied leaves is kept twice -- once from each leaf --
/// and is returned twice, as stock returns it.
/// </para>
/// </remarks>
public sealed class AreaPortalIndex
{
    private readonly Dictionary<(int Low, int High), List<Portal>> _byAreas = [];

    /// <summary>Indexes a tree.</summary>
    /// <param name="headNode">The tree root.</param>
    /// <exception cref="ArgumentNullException"><paramref name="headNode"/> is null.</exception>
    public AreaPortalIndex(IBspNode headNode)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        Stack<IBspNode> pending = new();
        pending.Push(headNode);
        while (pending.Count > 0)
        {
            IBspNode node = pending.Pop();
            if (!node.IsLeaf())
            {
                // children[0] first, as the recursion does.
                pending.Push(node.Back!);
                pending.Push(node.Front!);
                continue;
            }

            for (Portal? p = node.Portals; p is not null;)
            {
                int mine = ReferenceEquals(p.FrontNode, node) ? 0 : 1;
                Portal? next = p.NextAt(mine);

                if (p.FrontNode!.Occupied != 0 && p.BackNode!.Occupied != 0)
                {
                    int a = p.FrontNode.Area;
                    int b = p.BackNode.Area;
                    (int, int) key = a <= b ? (a, b) : (b, a);
                    if (!_byAreas.TryGetValue(key, out List<Portal>? list))
                    {
                        list = [];
                        _byAreas.Add(key, list);
                    }

                    list.Add(p);
                }

                p = next;
            }
        }
    }

    /// <summary>
    /// <see cref="AreaPortalGeometry.FindPortalsLeadingToArea"/>, answered
    /// from the index: the same portals in the same order.
    /// </summary>
    /// <param name="planes">The map's plane table.</param>
    /// <param name="sourceArea">One of the two areas.</param>
    /// <param name="destArea">The other.</param>
    /// <param name="plane">The plane the portals must lie in.</param>
    /// <param name="found">The portals, appended in tree order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planes"/> or <paramref name="found"/> is null.</exception>
    public void FindPortalsLeadingToArea(PlaneTable planes, int sourceArea, int destArea, Plane plane, List<Portal> found)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(found);

        // (back == dest && front == src) || (front == dest && back == src)
        // is "the two areas, in either order": the unordered pair.
        (int, int) key = sourceArea <= destArea ? (sourceArea, destArea) : (destArea, sourceArea);
        if (!_byAreas.TryGetValue(key, out List<Portal>? candidates))
        {
            return;
        }

        foreach (Portal p in candidates)
        {
            if (AreaPortalGeometry.LiesInPlane(planes, p, plane))
            {
                found.Add(p);
            }
        }
    }
}
