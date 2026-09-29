//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Validation;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// A room's areas and area portals as the link carries them, or null when
    /// its compile has none (one area, the reserved listing, no clip vertex,
    /// no portal number).
    /// </summary>
    /// <param name="room">The room.</param>
    /// <returns>The data bound to the room's compile; null for a room without area portals.</returns>
    /// <exception cref="LinkException">
    /// The room's lumps have area portals but the room carries no area portal
    /// data from its compile.
    /// </exception>
    /// <remarks>
    /// The data is what says the room was held to the pack's area portal
    /// rules (<see cref="RoomAreaPortals.Problem"/>, no <c>room_needs</c> on a
    /// portal) when it was compiled, so a room without it (a pack written
    /// before area portals were carried, or a room built without
    /// <c>ssmap room</c>) is refused rather than linked unchecked, as a room
    /// with overlays and no overlay data is. Without the data the link reads
    /// only the lump lengths, so a room whose one portal sealed nothing (it
    /// has a <c>portalnumber</c> but one area and no listing) is taken as
    /// having none, which is how the link read it before portals were
    /// carried.
    /// </remarks>
    internal static RoomAreaPortals? RoomAreaPortalsOf(RoomObject room)
    {
        if (room.AreaPortalsOfCompile is { } portals)
        {
            return portals;
        }

        int areas = BspStructView.Count<DArea>(room.Bsp[BspLump.Areas]);
        ReadOnlySpan<DAreaPortal> listings = BspStructView.As<DAreaPortal>(room.Bsp[BspLump.AreaPortals]);
        if (areas <= 2 && listings.Length <= 1 && room.Bsp[BspLump.ClipPortalVerts].Length == 0)
        {
            return null;
        }

        HashSet<int> keys = [];
        for (int i = 1; i < listings.Length; i++)
        {
            keys.Add(listings[i].PortalKey);
        }

        throw new LinkException(
            $"room {room.Definition.Name} has {keys.Count} area portals but no area portal data from its compile"
            + " (a pack written before the link carried area portals, or a room built without ssmap room);"
            + " recompile the library with ssmap room.");
    }

    /// <summary>
    /// The level's areas: which of the level's areas each placement's own
    /// areas become, with the portals that divide them, or null when no
    /// placed room has an area portal, so a level without them links exactly
    /// as before they were carried (one open area, the first room's lumps).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The union.</b> Every placement's own areas (1 and up; area 0 is the
    /// void) start apart. Two areas meet wherever a joint opens a doorway
    /// between them: at a joint, every area the facing clusters of one
    /// side's socket lie in joins every area the other side's facing clusters
    /// lie in, since the link strips both plugs and the doorway is one open
    /// space. Nothing else joins areas: a room is sealed but at its sockets,
    /// and a capped socket keeps its plug. This generalises the collapse the
    /// link made before portals were carried: rooms without a portal are one
    /// area each, and a level of them, every room reachable, is one area.
    /// </para>
    /// <para>
    /// <b>Numbering.</b> The level's areas are numbered from 1 in the order
    /// their first member appears, placement by placement in link order and
    /// each room's areas in its own order. The flattened compile numbers its
    /// areas in the order its flood meets them, which need not be this; the
    /// two maps hold the same partition of the level's open space, up to the
    /// names (the rooms design, 4.11).
    /// </para>
    /// <para>
    /// <b>A portal that seals nothing.</b> When the rooms around a portal
    /// join its two sides into one level area (a ring of rooms around it),
    /// the portal divides nothing. vbsp, meeting that in the flattened
    /// level, warns that the portal does not touch two areas and writes no
    /// listing for it, keeping its entity and number; the link does the
    /// same, and reports it (<see cref="LinkedLevel.AreaWarnings"/>).
    /// </para>
    /// <para>
    /// <b>Limits.</b> The level's areas are held to the loader's cap on the
    /// lump (<c>MAX_MAP_AREAS</c>, 256 entries, area 0 included), refused
    /// naming the placement whose area crossed it; the listings, the clip
    /// vertices and the portal numbers are held where the lumps are written
    /// (<see cref="WriteAreas"/>).
    /// </para>
    /// </remarks>
    /// <param name="resolved">The placements.</param>
    /// <param name="plans">The placements' plans, their censuses applied.</param>
    /// <returns>The level's areas, or null.</returns>
    /// <exception cref="LinkException">The level has more areas than a map holds.</exception>
    private static LevelAreas? PlanAreas(ResolvedPlacement[] resolved, RoomPlan[] plans)
    {
        if (!plans.Any(p => p.AreaPortals is not null))
        {
            return null;
        }

        // One union-find node per placement area 1 and up.
        int[] first = new int[plans.Length];
        int nodes = 0;
        for (int p = 0; p < plans.Length; p++)
        {
            first[p] = nodes;
            nodes += RoomAreaCount(plans[p]) - 1;
        }

        int[] parent = new int[nodes];
        for (int i = 0; i < nodes; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            if (ra != rb)
            {
                // The lower node is the root, so a union's result does not
                // depend on the order the joints are met in.
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        Dictionary<(int X, int Y), ResolvedPlacement> byCell = new(resolved.Length);
        foreach (ResolvedPlacement placement in resolved)
        {
            byCell[(placement.Instance.Placement.CellX, placement.Instance.Placement.CellY)] = placement;
        }

        for (int p = 0; p < plans.Length; p++)
        {
            RoomPlan plan = plans[p];
            foreach ((string socket, string neighborSocket) in plan.Placement.Instance.Joints)
            {
                RoomSocket mine = Socket(plan.Placement.Room, socket);
                (RoomPlan other, RoomSocket theirs, int q) = Neighbor(plan.Placement, mine, neighborSocket, byCell, plans);
                foreach (int a in FacingAreas(plan, socket))
                {
                    foreach (int b in FacingAreas(other, theirs.Name))
                    {
                        Union(first[p] + a - 1, first[q] + b - 1);
                    }
                }
            }
        }

        // Numbered in the order the roots first appear.
        int[][] map = new int[plans.Length][];
        Dictionary<int, int> number = [];
        for (int p = 0; p < plans.Length; p++)
        {
            RoomPlan plan = plans[p];
            map[p] = new int[RoomAreaCount(plan)];
            for (int a = 1; a < map[p].Length; a++)
            {
                int root = Find(first[p] + a - 1);
                if (!number.TryGetValue(root, out int level))
                {
                    number[root] = level = number.Count + 1;
                    RoomPlacement where = plan.Placement.Instance.Placement;
                    LoaderLimit(
                        plan.Placement.Room.Definition.Name,
                        where.CellX,
                        where.CellY,
                        "areas",
                        level + 1,
                        BspLimits.Caps.First(c => c.Lump == BspLump.Areas).Max,
                        "MAX_MAP_AREAS");
                }

                map[p][a] = level;
            }
        }

        int portalBase = 0;
        foreach (RoomPlan plan in plans)
        {
            plan.PortalBase = portalBase;
            portalBase += plan.AreaPortals?.PortalNumbers ?? 0;
            RoomPlacement where = plan.Placement.Instance.Placement;
            AreaPortalLimits(plan.Placement.Room.Definition.Name, where.CellX, where.CellY, portalBase, listings: 1, clipVerts: 0);
        }

        for (int p = 0; p < plans.Length; p++)
        {
            plans[p].AreaMap = map[p];
        }

        return new LevelAreas(number.Count);
    }

    /// <summary>How many entries a placement's room has in its <c>Areas</c> lump, area 0 included.</summary>
    private static int RoomAreaCount(RoomPlan plan) =>
        plan.AreaPortals?.AreaCount ?? Math.Max(BspStructView.Count<DArea>(plan.Bsp[BspLump.Areas]), 2);

    /// <summary>
    /// The room areas the open clusters facing a jointed socket lie in: the
    /// areas the doorway opens onto from this side (a doorway leaf takes the
    /// first's, <see cref="OpenArea"/>).
    /// </summary>
    private static IEnumerable<int> FacingAreas(RoomPlan plan, string socket)
    {
        int count = RoomAreaCount(plan);
        foreach (int cluster in plan.JointFacing[socket])
        {
            int area = OpenArea(plan.Leafs, cluster);
            if (area >= 1 && area < count)
            {
                yield return area;
            }
        }
    }

    /// <summary>A room-local area through a placement's area map; the map's identity for a level without area portals.</summary>
    private static int LevelArea(RoomPlan plan, int roomArea) =>
        plan.AreaMap is { } map && roomArea > 0 && roomArea < map.Length ? map[roomArea] : roomArea;

    /// <summary>
    /// Writes the level's <c>Areas</c>, <c>AreaPortals</c> and
    /// <c>ClipPortalVerts</c> lumps from the planned areas: every level area
    /// in order, and under it every portal listed from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order.</b> vbsp lists a map's portals area by area, and under each
    /// area its portal entities in entity order, each once, from the side
    /// that area is on. The flattened level writes the placements' entities
    /// in link order and each room's in its own order, and its loader
    /// numbers the portal entities in that order; the link numbers placement
    /// <i>p</i>'s portal <i>k</i> as <see cref="RoomPlan.PortalBase"/> +
    /// <i>k</i> (the entity's <c>portalnumber</c> rebased the same way,
    /// <see cref="TranslateEntity"/>), so under each level area the listings
    /// go in the order of their linked numbers.
    /// </para>
    /// <para>
    /// <b>A listing</b> is its room's: the key rebased, the other side's area
    /// through the placement's map, the plane the room's (oriented into the
    /// listing area, as vbsp orients it) as the shared table holds it
    /// (<see cref="RoomPlan.PlaneRef(int)"/>), and a copy of its clip
    /// vertices turned for the placement (<see cref="RoomAreaPortals.ClipVerts"/>)
    /// and moved (<see cref="RoomTransform.Translate"/>); vbsp writes each
    /// listing its own run of vertices, and so does the link. A listing
    /// whose two areas the level joined is left out with its partner
    /// (<see cref="PlanAreas"/>), and reported once.
    /// </para>
    /// <para>
    /// <b>Limits.</b> <see cref="AreaPortalLimits"/>, after every listing,
    /// naming the placement whose listing crossed one.
    /// </para>
    /// </remarks>
    private static void WriteAreas(BspData linked, RoomPlan[] plans, LevelAreas areas, List<string> warnings)
    {
        List<(int Key, RoomPlan Plan, int Listing)>[] byArea = new List<(int, RoomPlan, int)>[areas.Count + 1];
        for (int a = 0; a < byArea.Length; a++)
        {
            byArea[a] = [];
        }

        foreach (RoomPlan plan in plans)
        {
            if (plan.AreaLumps is not { } lumps)
            {
                continue;
            }

            HashSet<int> sealedNothing = [];
            for (int l = 1; l < lumps.Portals.Length; l++)
            {
                DAreaPortal listing = lumps.Portals[l];
                int from = LevelArea(plan, lumps.SourceOf(l));
                int to = LevelArea(plan, listing.OtherArea);
                if (from == to)
                {
                    if (sealedNothing.Add(listing.PortalKey))
                    {
                        RoomPlacement where = plan.Placement.Instance.Placement;
                        warnings.Add(
                            $"room {plan.Placement.Room.Definition.Name} at cell ({where.CellX}, {where.CellY}): area portal {listing.PortalKey + plan.PortalBase}"
                            + " has one area on both sides once the level joins the rooms around it; the level keeps its entity but lists no portal for it.");
                    }

                    continue;
                }

                byArea[from].Add((listing.PortalKey + plan.PortalBase, plan, l));
            }
        }

        List<DArea> areaLump = [default];
        List<DAreaPortal> portalLump = [default];
        List<Vec3> verts = [];
        for (int a = 1; a < byArea.Length; a++)
        {
            areaLump.Add(new DArea { FirstAreaPortal = portalLump.Count, NumAreaPortals = byArea[a].Count });
            foreach ((int key, RoomPlan plan, int l) in byArea[a].OrderBy(x => x.Key))
            {
                DAreaPortal room = plan.AreaLumps!.Portals[l];
                string name = plan.Placement.Room.Definition.Name;
                RoomPlacement where = plan.Placement.Instance.Placement;
                Vec3[] turned = plan.AreaPortals!.ClipVerts(where.NormalizedRotation);
                int start = verts.Count;
                for (int v = room.FirstClipPortalVert; v < room.FirstClipPortalVert + room.ClipPortalVerts; v++)
                {
                    verts.Add(plan.Transform.Translate(turned[v]));
                }

                portalLump.Add(new DAreaPortal
                {
                    PortalKey = (ushort)key,
                    OtherArea = (ushort)LevelArea(plan, room.OtherArea),
                    FirstClipPortalVert = (ushort)start,
                    ClipPortalVerts = room.ClipPortalVerts,
                    PlaneNum = plan.PlaneRef(room.PlaneNum),
                });
                AreaPortalLimits(name, where.CellX, where.CellY, portalNumbers: 0, portalLump.Count, verts.Count);
            }
        }

        linked.SetLump(BspLump.Areas, Bytes(areaLump));
        linked.SetLump(BspLump.AreaPortals, Bytes(portalLump));
        linked.SetLump(BspLump.ClipPortalVerts, Bytes(verts));
    }

    /// <summary>
    /// Refuses area portal totals past what the format carries: the portal
    /// numbers past the <c>ushort</c> a listing's key is, the listings (the
    /// reserved one included) past the loader's <c>MAX_MAP_AREAPORTALS</c>
    /// (1024: each portal is listed twice), and the clip vertices past the
    /// <c>ushort</c> that starts a listing's run. vbsp's own cap on the
    /// vertices (<c>MAX_MAP_PORTALVERTS</c>, 128,000) is wider than the
    /// field, so the field is the one that binds.
    /// </summary>
    /// <param name="room">The room whose placement brought the totals there, for the message.</param>
    /// <param name="cellX">Its cell's column.</param>
    /// <param name="cellY">Its cell's row.</param>
    /// <param name="portalNumbers">The portal numbers so far.</param>
    /// <param name="listings">The listings so far, the reserved one included.</param>
    /// <param name="clipVerts">The clip vertices so far.</param>
    /// <exception cref="LinkException">A total is past its limit.</exception>
    internal static void AreaPortalLimits(string room, int cellX, int cellY, long portalNumbers, long listings, long clipVerts)
    {
        Limit(room, cellX, cellY, "area portal numbers", portalNumbers, ushort.MaxValue);
        LoaderLimit(room, cellX, cellY, "area portal listings", listings, BspLimits.Caps.First(c => c.Lump == BspLump.AreaPortals).Max, "MAX_MAP_AREAPORTALS");
        Limit(room, cellX, cellY, "clip portal vertices", clipVerts, ushort.MaxValue + 1);
    }

    /// <summary>The level's areas, once planned: how many there are besides area 0.</summary>
    /// <param name="Count">The level's areas, area 0 not counted.</param>
    private sealed record LevelAreas(int Count);
}
