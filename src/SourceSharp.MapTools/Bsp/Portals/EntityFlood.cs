using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// What the entity flood found: whether the map is sealed, and how much of it
/// was filled away.
/// </summary>
/// <param name="Inside">At least one entity was placed in a non-solid leaf.</param>
/// <param name="ReachedOutside">The flood got out of the map.</param>
/// <param name="SolidLeaves">Leaves already solid that no entity reached (<c>c_solid</c>).</param>
/// <param name="FilledLeaves">Leaves turned solid by <c>FillOutside</c> (<c>c_outside</c>).</param>
/// <param name="InsideLeaves">Leaves an entity reached (<c>c_inside</c>).</param>
/// <remarks>
/// <see cref="Sealed"/> is stock's return from <c>FloodEntities</c> and is the
/// whole decision: a map with no entities at all is not leaked, it is just not
/// filled, and stock says "no entities in open -- no filling" and carries on.
/// </remarks>
public readonly record struct FloodResult(
    bool Inside,
    bool ReachedOutside,
    int SolidLeaves = 0,
    int FilledLeaves = 0,
    int InsideLeaves = 0)
{
    /// <summary>Whether <c>FillOutside</c> should run: an entity is inside and none got out.</summary>
    public bool Sealed => Inside && !ReachedOutside;
}

/// <summary>
/// The entity flood and the fill that follows it
/// </summary>
/// <remarks>
/// The flood is the definition of "sealed": start in the leaf under every point
/// entity's origin and walk through every portal that does not cross into
/// solid, numbering leaves with their hop count. If the leaf on the far side of
/// the head node's box portals is ever numbered, the map leaks; if it is not,
/// every leaf that was NOT numbered is outside the map and can be turned solid.
/// </remarks>
public static class EntityFlood
{
    /// <summary>
    /// Floods outward from a leaf, numbering each leaf with its hop count
    /// (<c>FloodPortals_r</c>).
    /// </summary>
    /// <param name="start">The leaf to start in.</param>
    /// <param name="dist">The number to give <paramref name="start"/>.</param>
    /// <remarks>
    /// Stock recurses, one C stack frame per leaf, which on a large map is tens
    /// of thousands deep. This walks the same depth-first order on an explicit
    /// stack: neighbours are pushed in reverse so the first portal is popped
    /// first, and the "already numbered" test moves from before the recursive
    /// call to just after the pop — which sees exactly the same state, because
    /// a depth-first pop order finishes each subtree before the next sibling is
    /// considered either way.
    /// </remarks>
    public static void FloodPortals(IBspNode start, int dist)
    {
        ArgumentNullException.ThrowIfNull(start);

        Stack<(IBspNode Node, int Dist, bool Unconditional)> pending = new();
        List<IBspNode> neighbours = [];

        // The first node is numbered unconditionally: stock assigns
        // node->occupied on entry, before any test, so a second entity in an
        // already-flooded leaf renumbers it to 1.
        pending.Push((start, dist, true));

        while (pending.TryPop(out (IBspNode Node, int Dist, bool Unconditional) item))
        {
            if (!item.Unconditional && item.Node.Occupied != 0)
            {
                continue;
            }

            item.Node.Occupied = item.Dist;

            neighbours.Clear();

            for (Portal? p = item.Node.Portals; p is not null;)
            {
                int side = p.SideOf(item.Node);
                IBspNode other = p.NodeAt(1 - side)!;

                if (other.Occupied == 0 && PortalContents.EntityFlood(p))
                {
                    neighbours.Add(other);
                }

                p = p.NextAt(side);
            }

            for (int i = neighbours.Count - 1; i >= 0; i--)
            {
                pending.Push((neighbours[i], item.Dist + 1, false));
            }
        }
    }

    /// <summary>
    /// The same flood, but stopped by areaportals as well as by solid
    /// (<c>FloodAreaLeak_r</c>).
    /// </summary>
    /// <param name="start">The leaf to start in.</param>
    /// <param name="dist">The number to give <paramref name="start"/>.</param>
    public static void FloodAreaLeak(IBspNode start, int dist)
    {
        ArgumentNullException.ThrowIfNull(start);

        Stack<(IBspNode Node, int Dist, bool Unconditional)> pending = new();
        List<IBspNode> neighbours = [];

        pending.Push((start, dist, true));

        while (pending.TryPop(out (IBspNode Node, int Dist, bool Unconditional) item))
        {
            if (!item.Unconditional && item.Node.Occupied != 0)
            {
                continue;
            }

            item.Node.Occupied = item.Dist;

            neighbours.Clear();

            for (Portal? p = item.Node.Portals; p is not null;)
            {
                int side = p.SideOf(item.Node);
                IBspNode other = p.NodeAt(1 - side)!;

                if (other.Occupied == 0 && PortalContents.AreaLeakFlood(p))
                {
                    neighbours.Add(other);
                }

                p = p.NextAt(side);
            }

            for (int i = neighbours.Count - 1; i >= 0; i--)
            {
                pending.Push((neighbours[i], item.Dist + 1, false));
            }
        }
    }

    /// <summary>
    /// Clears every leaf's hop count(<c>ClearOccupied_r</c>).
    /// </summary>
    /// <param name="node">The root of the subtree to clear.</param>
    public static void ClearOccupied(IBspNode? node)
    {
        if (node is null)
        {
            return;
        }

        node.Occupied = 0;
        ClearOccupied(node.Front);
        ClearOccupied(node.Back);
    }

    /// <summary>
    /// The full areaportal-leak flood: clear the tree, then flood from one leaf
    /// starting at 2(<c>FloodAreaLeak</c>).
    /// </summary>
    /// <param name="headNode">The tree root, to clear.</param>
    /// <param name="firstSide">The leaf to flood from.</param>
    public static void FloodAreaLeakFrom(IBspNode headNode, IBspNode firstSide)
    {
        ClearOccupied(headNode);
        FloodAreaLeak(firstSide, 2);
    }

    /// <summary>
    /// The leaf a point falls in(<c>PlaceOccupant</c>'s descent).
    /// </summary>
    /// <param name="headNode">The tree root.</param>
    /// <param name="planes">The map's plane table.</param>
    /// <param name="point">The world position.</param>
    /// <returns>The leaf containing it.</returns>
    /// <remarks>
    /// Exactly on the plane counts as the front side (<c>d &gt;= 0</c>), which
    /// is why a probe sitting on a block-grid cut is not ambiguous here even
    /// though it is ambiguous to anything that tests with an epsilon.
    /// </remarks>
    public static IBspNode LeafForPoint(IBspNode headNode, PlaneTable planes, Vec3 point)
    {
        ArgumentNullException.ThrowIfNull(headNode);
        ArgumentNullException.ThrowIfNull(planes);

        IBspNode node = headNode;

        while (!node.IsLeaf())
        {
            Plane plane = planes[node.PlaneNumber];
            float d = Vec3.Dot(point, plane.Normal) - plane.Dist;
            node = d >= 0 ? node.Front! : node.Back!;
        }

        return node;
    }

    /// <summary>
    /// Puts an entity in the leaf under its origin and floods out from there
    /// (<c>PlaceOccupant</c>).
    /// </summary>
    /// <param name="headNode">The tree root.</param>
    /// <param name="planes">The map's plane table.</param>
    /// <param name="origin">Where the entity is.</param>
    /// <param name="occupant">The entity.</param>
    /// <returns><see langword="false"/> when the origin is in solid.</returns>
    /// <remarks>
    /// The solid test is <c>contents == CONTENTS_SOLID</c>, an equality and not
    /// a mask test, so a leaf that is solid AND something else is accepted
    /// here.
    /// </remarks>
    public static bool PlaceOccupant(IBspNode headNode, PlaneTable planes, Vec3 origin, MapEntity occupant)
    {
        IBspNode node = LeafForPoint(headNode, planes, origin);

        if (node.Contents == PortalContents.Solid)
        {
            return false;
        }

        node.Occupant = occupant;

        // The point the flood actually started from, which is NOT the entity's
        // origin key: it has had the +1z applied and may have been nudged on
        // the 16-unit grid as well. Stock never records it and re-reads the key
        // when it writes the .lin, which is StockQuirk.LeakFileUnnudgedOrigin.
        occupant.FloodOrigin = origin;

        // Flood outward from here to see if this entity leaks.
        FloodPortals(node, 1);

        return true;
    }

    /// <summary>
    /// Floods from every entity in the map
    /// (<c>FloodEntities</c>).
    /// </summary>
    /// <param name="tree">The portalised tree.</param>
    /// <param name="planes">The map's plane table.</param>
    /// <param name="entities">The map's entities; index 0 (worldspawn) is skipped.</param>
    /// <returns>Whether an entity is inside and whether the flood got out.</returns>
    /// <remarks>
    /// <para>
    /// Three details decide which entities take part. An entity whose origin is
    /// exactly (0,0,0) is skipped entirely — that is how brush entities and
    /// keyless entities stay out of it, and it also means a point entity
    /// deliberately placed at the world origin does not seal anything.
    /// </para>
    /// <para>
    /// Every origin is raised by one unit "so objects on floor are ok", and an
    /// <c>info_player_start</c> that lands in solid is retried on a 3×3 grid of
    /// ±16-unit offsets. The nudge that succeeds is left applied, so the offset
    /// leaf is the one that gets the occupant.
    /// </para>
    /// </remarks>
    public static FloodResult FloodEntities(IBspTree tree, PlaneTable planes, IReadOnlyList<MapEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entities);

        IBspNode headNode = tree.HeadNode;
        bool inside = false;
        tree.OutsideNode.Occupied = 0;

        for (int i = 1; i < entities.Count; i++)
        {
            MapEntity entity = entities[i];
            Vec3 origin = entity.GetVectorForKey("origin");

            if (origin == Vec3.Zero)
            {
                continue;
            }

            string className = entity.ValueForKey("classname");

            origin = new Vec3(origin.X, origin.Y, origin.Z + 1f);   // so objects on floor are ok

            if (className == "info_player_start")
            {
                // nudge playerstart around if needed so clipping hulls always
                // have a valid point
                bool placed = false;

                for (int x = -16; x <= 16 && !placed; x += 16)
                {
                    for (int y = -16; y <= 16 && !placed; y += 16)
                    {
                        Vec3 nudged = new(origin.X + x, origin.Y + y, origin.Z);

                        if (PlaceOccupant(headNode, planes, nudged, entity))
                        {
                            inside = true;
                            placed = true;
                        }
                    }
                }
            }
            else if (PlaceOccupant(headNode, planes, origin, entity))
            {
                inside = true;
            }
        }

        return new FloodResult(inside, tree.OutsideNode.Occupied != 0);
    }

    /// <summary>
    /// Turns every leaf no entity reached into solid
    /// (<c>FillOutside</c>).
    /// </summary>
    /// <param name="headNode">The tree root.</param>
    /// <returns>The three leaf counts, with <c>Inside</c> and <c>ReachedOutside</c> left false.</returns>
    public static FloodResult FillOutside(IBspNode headNode)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        int outside = 0;
        int insideCount = 0;
        int solid = 0;

        FillOutsideRecursive(headNode, ref outside, ref insideCount, ref solid);

        return new FloodResult(false, false, solid, outside, insideCount);
    }

    private static void FillOutsideRecursive(IBspNode node, ref int outside, ref int inside, ref int solid)
    {
        if (!node.IsLeaf())
        {
            FillOutsideRecursive(node.Front!, ref outside, ref inside, ref solid);
            FillOutsideRecursive(node.Back!, ref outside, ref inside, ref solid);
            return;
        }

        // anything not reachable by an entity can be filled away
        if (node.Occupied == 0)
        {
            if (node.Contents != PortalContents.Solid)
            {
                outside++;
                node.Contents = PortalContents.Solid;
            }
            else
            {
                solid++;
            }
        }
        else
        {
            inside++;
        }
    }
}
