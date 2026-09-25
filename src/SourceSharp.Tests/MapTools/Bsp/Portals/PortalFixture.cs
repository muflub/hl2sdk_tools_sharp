using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>
/// Hand-built trees for the portal stages, small enough to reason about
/// completely.
/// </summary>
internal sealed class PortalFixture
{
    private int _nextId;

    private PortalFixture(ComplianceOptions compliance) =>
        Arena = new WindingArena { Compliance = compliance };

    internal PlaneTable Planes { get; } = new();

    /// <summary>
    /// The arena, carrying the compliance the fixture was built for.
    /// </summary>
    /// <remarks>
    /// Every quirk the portal stages switch on reads it from here, so a fact
    /// that wants stock's behaviour asks for it at the factory rather than
    /// inheriting a default.
    /// </remarks>
    internal WindingArena Arena { get; }

    internal List<MapEntity> Entities { get; } = [];

    internal List<BspNode> Leaves { get; } = [];

    internal BspTree Tree { get; private set; } = null!;

    internal TreePortals Portals { get; private set; } = null!;

    /// <summary>
    /// A 64-unit cube of empty space walled in on all six sides by solid
    /// leaves, inside a tree bounded at ±64 (so the head node's box is ±72).
    /// </summary>
    /// <remarks>
    /// Seven leaves, in tree order: +x, +y, +z, THE ROOM, -z, -y, -x. The room
    /// is the only one an entity can be in and the only one that is not solid,
    /// which is the whole point: every gate about sealing, filling and areas
    /// has exactly one right answer on it.
    /// </remarks>
    internal static PortalFixture SealedRoom(ComplianceOptions? compliance = null)
    {
        PortalFixture f = new(compliance ?? ComplianceOptions.Correct);

        BspNode room = f.Leaf(0);
        BspNode xHigh = f.Leaf(PortalContents.Solid);
        BspNode xLow = f.Leaf(PortalContents.Solid);
        BspNode yHigh = f.Leaf(PortalContents.Solid);
        BspNode yLow = f.Leaf(PortalContents.Solid);
        BspNode zHigh = f.Leaf(PortalContents.Solid);
        BspNode zLow = f.Leaf(PortalContents.Solid);

        BspNode n6 = f.Node(new Vec3(0f, 0f, 1f), -32f, room, zLow);
        BspNode n5 = f.Node(new Vec3(0f, 0f, 1f), 32f, zHigh, n6);
        BspNode n4 = f.Node(new Vec3(0f, 1f, 0f), -32f, n5, yLow);
        BspNode n3 = f.Node(new Vec3(0f, 1f, 0f), 32f, yHigh, n4);
        BspNode n2 = f.Node(new Vec3(1f, 0f, 0f), -32f, n3, xLow);
        BspNode n1 = f.Node(new Vec3(1f, 0f, 0f), 32f, xHigh, n2);

        f.Finish(n1, new Vec3(-64f, -64f, -64f), new Vec3(64f, 64f, 64f));

        // Tree order, which is what SaveClusters_r and FindAreas_r walk.
        f.Leaves.AddRange([xHigh, yHigh, zHigh, room, zLow, yLow, xLow]);

        f.Entities.Add(f.Entity("worldspawn", new Vec3(0f, 0f, 0f)));

        return f;
    }

    /// <summary>
    /// The same room, cut into an upper half, an 16-unit areaportal slab and a
    /// lower half.
    /// </summary>
    /// <remarks>
    /// Leaves in tree order: +x, +y, +z, UPPER, SLAB, LOWER, -z, -y, -x. One
    /// entity in the upper half reaches all three, because the entity flood
    /// crosses areaportals and only the AREA flood stops at them — which is
    /// the difference the two areas come from.
    /// </remarks>
    internal static PortalFixture SealedRoomWithAreaportal(ComplianceOptions? compliance = null)
    {
        PortalFixture f = new(compliance ?? ComplianceOptions.Correct);

        BspNode upper = f.Leaf(0);
        BspNode slab = f.Leaf(PortalContents.AreaPortal);
        BspNode lower = f.Leaf(0);
        BspNode xHigh = f.Leaf(PortalContents.Solid);
        BspNode xLow = f.Leaf(PortalContents.Solid);
        BspNode yHigh = f.Leaf(PortalContents.Solid);
        BspNode yLow = f.Leaf(PortalContents.Solid);
        BspNode zHigh = f.Leaf(PortalContents.Solid);
        BspNode zLow = f.Leaf(PortalContents.Solid);

        BspNode n8 = f.Node(new Vec3(0f, 0f, 1f), -8f, slab, lower);
        BspNode n7 = f.Node(new Vec3(0f, 0f, 1f), 8f, upper, n8);
        BspNode n6 = f.Node(new Vec3(0f, 0f, 1f), -32f, n7, zLow);
        BspNode n5 = f.Node(new Vec3(0f, 0f, 1f), 32f, zHigh, n6);
        BspNode n4 = f.Node(new Vec3(0f, 1f, 0f), -32f, n5, yLow);
        BspNode n3 = f.Node(new Vec3(0f, 1f, 0f), 32f, yHigh, n4);
        BspNode n2 = f.Node(new Vec3(1f, 0f, 0f), -32f, n3, xLow);
        BspNode n1 = f.Node(new Vec3(1f, 0f, 0f), 32f, xHigh, n2);

        f.Finish(n1, new Vec3(-64f, -64f, -64f), new Vec3(64f, 64f, 64f));

        f.Leaves.AddRange([xHigh, yHigh, zHigh, upper, slab, lower, zLow, yLow, xLow]);

        f.Entities.Add(f.Entity("worldspawn", new Vec3(0f, 0f, 0f)));

        MapEntity areaportal = f.Entity("func_areaportal", new Vec3(0f, 0f, 0f));
        areaportal.AreaPortalNumber = 1;
        f.Entities.Add(areaportal);

        slab.AddLeafBrush(new MapBrush
        {
            Contents = PortalContents.AreaPortal,
            EntityNumber = f.Entities.Count - 1,
            Id = 7,
        });

        return f;
    }

    /// <summary>
    /// A room whose areaportal slab covers only its -x half, so the space above
    /// the slab reaches the space below it through the +x half. One area on
    /// both sides means the areaportal leaks.
    /// </summary>
    /// <param name="compliance">The compliance the fixture's arena carries.</param>
    /// <returns>The fixture.</returns>
    /// <remarks>
    /// Leaves in tree order: +x, +y, +z, RIGHT, UPPER, SLAB, LOWER, -z, -y,
    /// -x. <c>SetAreaPortalAreas</c> finds area 1 on both sides of the
    /// slab, and that is the path into <c>ReportAreaportalLeak</c>.
    /// </remarks>
    internal static PortalFixture LeakingAreaportal(ComplianceOptions? compliance = null)
    {
        PortalFixture f = new(compliance ?? ComplianceOptions.Correct);

        BspNode right = f.Leaf(0);
        BspNode upper = f.Leaf(0);
        BspNode slab = f.Leaf(PortalContents.AreaPortal);
        BspNode lower = f.Leaf(0);
        BspNode xHigh = f.Leaf(PortalContents.Solid);
        BspNode xLow = f.Leaf(PortalContents.Solid);
        BspNode yHigh = f.Leaf(PortalContents.Solid);
        BspNode yLow = f.Leaf(PortalContents.Solid);
        BspNode zHigh = f.Leaf(PortalContents.Solid);
        BspNode zLow = f.Leaf(PortalContents.Solid);

        BspNode n9 = f.Node(new Vec3(0f, 0f, 1f), -8f, slab, lower);
        BspNode n8 = f.Node(new Vec3(0f, 0f, 1f), 8f, upper, n9);
        BspNode n7 = f.Node(new Vec3(1f, 0f, 0f), 0f, right, n8);
        BspNode n6 = f.Node(new Vec3(0f, 0f, 1f), -32f, n7, zLow);
        BspNode n5 = f.Node(new Vec3(0f, 0f, 1f), 32f, zHigh, n6);
        BspNode n4 = f.Node(new Vec3(0f, 1f, 0f), -32f, n5, yLow);
        BspNode n3 = f.Node(new Vec3(0f, 1f, 0f), 32f, yHigh, n4);
        BspNode n2 = f.Node(new Vec3(1f, 0f, 0f), -32f, n3, xLow);
        BspNode n1 = f.Node(new Vec3(1f, 0f, 0f), 32f, xHigh, n2);

        f.Finish(n1, new Vec3(-64f, -64f, -64f), new Vec3(64f, 64f, 64f));

        f.Leaves.AddRange([xHigh, yHigh, zHigh, right, upper, slab, lower, zLow, yLow, xLow]);

        f.Entities.Add(f.Entity("worldspawn", new Vec3(0f, 0f, 0f)));
        f.Entities.Add(f.Entity("info_player_start", new Vec3(-16f, 0f, 16f)));

        MapEntity areaportal = f.Entity("func_areaportal", new Vec3(0f, 0f, 0f));
        areaportal.AreaPortalNumber = 1;
        f.Entities.Add(areaportal);

        slab.AddLeafBrush(new MapBrush
        {
            Contents = PortalContents.AreaPortal,
            EntityNumber = f.Entities.Count - 1,
            Id = 7,
        });

        return f;
    }

    /// <summary>The single empty leaf of <see cref="SealedRoom"/>.</summary>
    internal BspNode Room => Leaves[3];

    /// <summary>The areaportal leaf of <see cref="SealedRoomWithAreaportal"/>.</summary>
    internal BspNode Slab => Leaves[4];

    /// <summary>The leaf on the far side of the +x wall.</summary>
    internal BspNode BeyondXHigh => Leaves[0];

    /// <summary>Adds a point entity to the map.</summary>
    /// <param name="className">Its classname.</param>
    /// <param name="origin">Its origin.</param>
    /// <returns>The entity, already appended to <see cref="Entities"/>.</returns>
    internal MapEntity Add(string className, Vec3 origin)
    {
        MapEntity entity = Entity(className, origin);
        Entities.Add(entity);
        return entity;
    }

    /// <summary>Portalises the tree.</summary>
    internal void Portalise() => Portals.MakeTreePortals(Tree);

    private MapEntity Entity(string className, Vec3 origin)
    {
        MapEntity entity = new();
        entity.AddKeyValue("classname", className);
        entity.AddKeyValue("origin", string.Create(CultureInfo.InvariantCulture, $"{origin.X} {origin.Y} {origin.Z}"));
        entity.Origin = origin;
        return entity;
    }

    private BspNode Leaf(int contents) => new(_nextId++) { Contents = contents };

    private BspNode Node(Vec3 normal, float dist, BspNode front, BspNode back)
    {
        int planeNumber = Planes.Find(normal, dist);
        BspNode node = new(_nextId++);
        node.SplitOn(planeNumber, front, back);
        return node;
    }

    private void Finish(BspNode head, Vec3 mins, Vec3 maxs)
    {
        BspNode outside = new(_nextId++);
        Tree = new BspTree(head, outside, mins, maxs);
        Portals = new TreePortals(Arena, Planes);
    }
}
