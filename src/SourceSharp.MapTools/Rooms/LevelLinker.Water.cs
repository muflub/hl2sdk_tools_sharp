//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Overlays;
using SourceSharp.MapTools.Bsp.Write;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// A room's water as the link carries it, or null when its compile has
    /// none (<see cref="RoomWater.HasWater"/>).
    /// </summary>
    /// <param name="room">The room.</param>
    /// <returns>The water bound to the room's compile; null for a room without water.</returns>
    /// <exception cref="LinkException">
    /// The room's compile has water but the room carries no water data from
    /// its compile, or the lumps are not the ones vbsp writes.
    /// </exception>
    /// <remarks>
    /// The data is what says the room was held to the pack's water rules
    /// (<see cref="RoomWater.PlugProblem"/>) when it was compiled, so a room
    /// without it (a pack written before water was carried) is refused
    /// rather than linked unchecked, as a room with overlays and no overlay
    /// data is.
    /// </remarks>
    internal static RoomWater? RoomWaterOf(RoomObject room)
    {
        if (!RoomWater.HasWater(room.Bsp))
        {
            return null;
        }

        string name = room.Definition.Name;
        _ = RoomWater.Check(name, room.Bsp);
        _ = RoomWater.WaterOverlays(name, room.Bsp);
        RoomWater water = room.WaterOfCompile ?? throw new LinkException(
            $"room {name} has water but no water data from its compile"
            + " (a pack written before the link carried water, or a room built without ssmap room);"
            + " recompile the library with ssmap room.");
        if (water.Doors.Count != room.Definition.Sockets.Count)
        {
            throw new LinkException(
                $"room pack entry \"{name}\": its \"{RoomWater.SectionTag}\" section holds {water.Doors.Count} sockets; the room has {room.Definition.Sockets.Count}.");
        }

        return water;
    }

    /// <summary>
    /// The level's leaf water data: every placement's records in link order,
    /// each with its surface texinfo the shared table's, merged as vbsp
    /// merges a map's (one record per distinct surface height, lowest point
    /// and surface texinfo, the first kept); each placement's map from its
    /// room's records to the level's is left on its plan
    /// (<see cref="RoomPlan.WaterMap"/>). Null when no placed room has water,
    /// so a level without it carries no lump, as before water was carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why merge.</b> vbsp finds a volume's record by an exact match of its
    /// three fields before it adds one, so the flattened level's compile
    /// holds one record for the pools of every placement of a room at one
    /// height; the link writes the same table, and a level of many pools
    /// stays under the loader's cap as long as the flattened level would.
    /// </para>
    /// <para>
    /// <b>The height</b> moves with the placement's height (whole cells, the
    /// skybox's below the grid) and not with its turn: a turn is about +z.
    /// </para>
    /// </remarks>
    /// <exception cref="LinkException">The level's records pass <c>MAX_MAP_LEAFWATERDATA</c>, naming the placement that crossed it.</exception>
    private static List<DLeafWaterData>? PlanWaterData(RoomPlan[] plans, LevelLayout layout)
    {
        if (!plans.Any(p => p.Water is not null))
        {
            return null;
        }

        // Each placement's records moved for the level; then the records a
        // water joint joins made one (JoinedWater).
        DLeafWaterData[][] moved = new DLeafWaterData[plans.Length][];
        for (int p = 0; p < plans.Length; p++)
        {
            RoomPlan plan = plans[p];
            if (plan.Water is null)
            {
                continue;
            }

            ReadOnlySpan<DLeafWaterData> records = BspStructView.As<DLeafWaterData>(plan.Bsp[BspLump.LeafWaterData]);
            float lift = plan.Transform.Apply(Vec3.Zero).Z;
            moved[p] = new DLeafWaterData[records.Length];
            for (int r = 0; r < records.Length; r++)
            {
                DLeafWaterData linked = records[r];
                linked.SurfaceZ += lift;
                linked.MinZ += lift;
                linked.SurfaceTexInfoId = (short)plan.TexInfoRef(records[r].SurfaceTexInfoId);
                moved[p][r] = linked;
            }
        }

        JoinedWater(plans, layout, moved);

        List<DLeafWaterData> table = [];
        for (int p = 0; p < plans.Length; p++)
        {
            RoomPlan plan = plans[p];
            if (plan.Water is null)
            {
                continue;
            }

            plan.WaterMap = new int[moved[p].Length];
            for (int r = 0; r < moved[p].Length; r++)
            {
                DLeafWaterData linked = moved[p][r];
                int found = table.FindIndex(d =>
                    d.SurfaceZ == linked.SurfaceZ && d.MinZ == linked.MinZ && d.SurfaceTexInfoId == linked.SurfaceTexInfoId);
                if (found < 0)
                {
                    found = table.Count;
                    table.Add(linked);
                    RoomPlacement at = plan.Placement.Instance.Placement;
                    WaterDataLimit(plan.Placement.Room.Definition.Name, at.CellX, at.CellY, table.Count);
                }

                plan.WaterMap[r] = found;
            }
        }

        return table;
    }

    /// <summary>
    /// Makes one record of the records a level's water joints join: the
    /// water on the two sides of a jointed water socket, and through it
    /// whatever else it joins, is one body of water in the level, as vbsp
    /// finds one volume through the flattened level's doorway. Each joined
    /// record takes the group's lowest point (vbsp's volume reaches down to
    /// the lowest leaf it holds) and the surface texinfo of its first member
    /// in link order; the heights are one by the joint rule
    /// (<see cref="LevelWaterJoints"/>).
    /// </summary>
    /// <remarks>
    /// Which member's texinfo vbsp's volume takes follows the order its flood
    /// sorts the volume's leaves in, which the link cannot know; the first in
    /// link order is a fixed rule, and differs from the flattened compile's
    /// only in the texture alignment the record names, and only when the
    /// joined rooms' surfaces are aligned differently.
    /// </remarks>
    private static void JoinedWater(RoomPlan[] plans, LevelLayout layout, DLeafWaterData[][] moved)
    {
        Dictionary<(int Placement, int Record), (int, int)> parent = [];
        (int, int) Find((int, int) key)
        {
            while (parent.TryGetValue(key, out (int, int) up) && up != key)
            {
                key = up;
            }

            return key;
        }

        Dictionary<string, RoomDefinition> definitions = new(StringComparer.Ordinal);
        foreach (RoomPlan plan in plans)
        {
            definitions.TryAdd(plan.Placement.Instance.Placement.Room, plan.Placement.Room.Definition);
        }

        bool any = false;
        foreach ((int a, string socketA, float? levelA, int b, string socketB, float? levelB) in LevelWaterJoints.Joints(
            layout, name => definitions[name], (p, socket) => DoorAt(plans[p], socket)?.Level))
        {
            if (levelA is null || levelB is null)
            {
                continue;
            }

            (int, int) ra = Find((a, DoorAt(plans[a], socketA)!.Record));
            (int, int) rb = Find((b, DoorAt(plans[b], socketB)!.Record));
            if (ra != rb)
            {
                // The lower (earlier) root wins, so the result does not
                // depend on the order joints are met.
                (int, int) low = ra.CompareTo(rb) < 0 ? ra : rb;
                parent[ra] = low;
                parent[rb] = low;
                parent.TryAdd(low, low);
                any = true;
            }
        }

        if (!any)
        {
            return;
        }

        Dictionary<(int, int), DLeafWaterData> joined = [];
        for (int p = 0; p < plans.Length; p++)
        {
            for (int r = 0; moved[p] is not null && r < moved[p].Length; r++)
            {
                (int, int) root = Find((p, r));
                DLeafWaterData record = moved[p][r];
                if (joined.TryGetValue(root, out DLeafWaterData group))
                {
                    group.MinZ = Math.Min(group.MinZ, record.MinZ);
                    joined[root] = group;
                }
                else
                {
                    joined[root] = record;
                }
            }
        }

        for (int p = 0; p < plans.Length; p++)
        {
            for (int r = 0; moved[p] is not null && r < moved[p].Length; r++)
            {
                moved[p][r] = joined[Find((p, r))];
            }
        }
    }

    /// <summary>A placement's water at a socket (<see cref="RoomWater.Doors"/>), or null for a dry one.</summary>
    private static RoomWaterDoor? DoorAt(RoomPlan plan, string socket) =>
        plan.Water?.Doors[SocketIndex(plan.Placement.Room.Definition, socket)];

    /// <summary>
    /// Refuses a level whose leaf water data passes what vbsp writes
    /// (<c>MAX_MAP_LEAFWATERDATA</c>), naming the placement that crossed it:
    /// vbsp refuses a map past it, so the flattened level would not compile.
    /// </summary>
    internal static void WaterDataLimit(string room, int cellX, int cellY, long count)
    {
        if (count > WriteLimits.MaxMapLeafWaterData)
        {
            throw new LinkException(
                $"room {room} at cell ({cellX}, {cellY}) pushes the link to {count} leaf water data records;"
                + $" vbsp writes at most {WriteLimits.MaxMapLeafWaterData} (MAX_MAP_LEAFWATERDATA).");
        }
    }

    /// <summary>
    /// Refuses a level with more water overlays than a map holds
    /// (<c>MAX_MAP_WATEROVERLAYS</c>), naming the placement that crossed it:
    /// vbsp refuses a map past it.
    /// </summary>
    internal static void WaterOverlayLimit(string room, int cellX, int cellY, long count)
    {
        if (count > MapOverlay.MaxMapWaterOverlays)
        {
            throw new LinkException(
                $"room {room} at cell ({cellX}, {cellY}) pushes the link to {count} water overlays;"
                + $" a map holds at most {MapOverlay.MaxMapWaterOverlays} (MAX_MAP_WATEROVERLAYS).");
        }
    }

    /// <summary>A room's water data id as the level numbers it: -1 stays -1.</summary>
    private static short LinkedWaterData(RoomPlan plan, short roomId) =>
        roomId < 0 || plan.WaterMap is not { } map ? roomId : (short)map[roomId];

    /// <summary>
    /// The level's fluids: every placement's in link order, each one's
    /// convexes (stored turned) moved by the placement and their brushes and
    /// materials renumbered for the level, rebuilt into one surface as the
    /// room's compile built it, with its <c>fluid</c> block's plane moved.
    /// </summary>
    /// <remarks>
    /// A water doorway's convex joins the fluid of its room's water against
    /// the plug (<see cref="DoorwayConvex"/>). vbsp writes a map's fluids
    /// after its static solids, one per
    /// connected water volume, so the linked world record lists them after
    /// its contents classes, placement by placement, as the flattened
    /// level's compile lists its volumes found in its tree's order (the
    /// order differs; the volumes, planes and keys are the same).
    /// </remarks>
    private static List<(byte[] Blob, PhysFluidEntry Entry)> LinkFluids(
        RoomPlan[] plans,
        ManagedCollisionCooker cooker,
        List<string> materials,
        int[]? brushMap,
        List<DoorwayWaterPiece> doorways,
        CancellationToken cancellationToken)
    {
        List<(byte[], PhysFluidEntry)> fluids = [];
        foreach (RoomPlan plan in plans)
        {
            if (plan.Water is not { } water || water.Fluids.Count == 0)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            int rotation = plan.Transform.Placement.NormalizedRotation;
            RoomLinkCollision collide = CollisionFor(plan.Placement.Room, rotation)
                ?? throw new LinkException($"room {plan.Placement.Room.Definition.Name} has fluids but no world collision");
            int[] remap = new int[collide.Materials.Count + 1];
            for (int m = 0; m < collide.Materials.Count; m++)
            {
                remap[m + 1] = MaterialIndex(materials, collide.Materials[m]);
            }

            RoomLinkSolid[] turned = water.FluidLedges(rotation);
            Vec3 translation = plan.Transform.Apply(Vec3.Zero);
            for (int f = 0; f < turned.Length; f++)
            {
                List<IvpCompactLedge> ledges = new(turned[f].Starts.Length);
                for (int l = 0; l < turned[f].Starts.Length; l++)
                {
                    IvpCompactLedge ledge = new(turned[f].Ledge(l).ToArray());
                    TranslateLedge(ledge, plan.Transform);
                    if ((uint)ledge.ClientData < (uint)plan.BrushMap.Length && plan.BrushMap[ledge.ClientData] >= 0)
                    {
                        int linkedBrush = plan.LinkedBrush(ledge.ClientData);
                        ledge.ClientData = brushMap is null ? linkedBrush : brushMap[linkedBrush];
                    }

                    RemapMaterials(ledge, remap, plan);
                    ledges.Add(ledge);
                }

                // The water doorways this fluid runs into: their convexes
                // join it, as the doorway's water joins the room's.
                foreach (DoorwayWaterPiece piece in doorways)
                {
                    if (ReferenceEquals(piece.Plan, plan) && piece.Door.Fluid == f)
                    {
                        ledges.Add(DoorwayConvex(cooker, piece));
                    }
                }

                byte[] blob = cooker.CompileLedges(ledges)
                    ?? throw new LinkException($"room {plan.Placement.Room.Definition.Name}'s fluid {f} builds no surface");
                RoomWaterFluid fluid = water.Fluids[f];
                (Vec3 normal, float dist) = RoomWater.SurfaceAt(fluid, rotation, translation);
                fluids.Add((blob, new PhysFluidEntry(blob, fluid.SurfaceProp, fluid.Damping, normal, dist, fluid.Contents)));
            }
        }

        return fluids;
    }

    /// <summary>
    /// The level's <c>WaterOverlays</c> lump: every placement's water
    /// overlays in link order, each rebased and moved as an overlay is
    /// (<see cref="LinkOverlay"/>'s rule); or null when no placed room has
    /// one, so a level without them carries no lump.
    /// </summary>
    /// <remarks>
    /// vbsp numbers a map's water overlays from one past the last ordinary
    /// overlay a map may hold, in the order it reads their
    /// <c>overlaytransition</c> chunks; the flatten writes the placements'
    /// chunks in link order, so the link gives placement <i>p</i>'s water
    /// overlay <i>k</i> the id <see cref="RoomWater.FirstWaterOverlayId"/> +
    /// <see cref="RoomPlan.WaterOverlayBase"/> + <i>k</i>. Nothing names a
    /// water overlay by id, so no entity is rebased.
    /// </remarks>
    /// <exception cref="LinkException">The level's water overlays pass what a map holds.</exception>
    private static (byte[] Lump, int Version)? LinkWaterOverlays(RoomPlan[] plans)
    {
        RoomPlan? first = plans.FirstOrDefault(p => p.Water is { OverlayCount: > 0 });
        if (first is null)
        {
            return null;
        }

        List<DWaterOverlay> linked = [];
        int count = 0;
        foreach (RoomPlan plan in plans)
        {
            if (plan.Water is not { OverlayCount: > 0 } water)
            {
                continue;
            }

            plan.WaterOverlayBase = count;
            count += water.OverlayCount;
            RoomPlacement at = plan.Placement.Instance.Placement;
            WaterOverlayLimit(plan.Placement.Room.Definition.Name, at.CellX, at.CellY, count);

            ReadOnlySpan<DWaterOverlay> records = RoomWater.WaterOverlays(plan.Placement.Room.Definition.Name, plan.Bsp);
            RoomOverlayPose[] poses = water.OverlayPoses(plan.Transform.Placement.NormalizedRotation);
            Vec3 offset = plan.Transform.Apply(Vec3.Zero);
            for (int i = 0; i < records.Length; i++)
            {
                linked.Add(LinkWaterOverlay(
                    records[i],
                    poses[i],
                    offset,
                    plan.WaterOverlayBase,
                    plan.TexInfoRef(records[i].TexInfo),
                    face => LinkedOverlayFace(plan, face)));
            }
        }

        return (MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(linked)).ToArray(), first.Bsp[BspLump.WaterOverlays].Version);
    }

    /// <summary>
    /// One water overlay record as its placement links it: as
    /// <see cref="LinkOverlay"/> links an overlay, with the water overlays'
    /// longer face list.
    /// </summary>
    internal static DWaterOverlay LinkWaterOverlay(
        DWaterOverlay record, RoomOverlayPose pose, Vec3 offset, int idBase, int texInfo, Func<int, int> face)
    {
        DWaterOverlay linked = record;
        linked.Id = record.Id + idBase;
        linked.TexInfo = (short)texInfo;
        linked.Origin = RoomStaticProps.Unsigned(pose.Origin + offset);
        linked.BasisNormal = pose.BasisNormal;
        linked.UvPoints[0] = new Vec3(record.UvPoints[0].X, record.UvPoints[0].Y, pose.BasisU.X);
        linked.UvPoints[1] = new Vec3(record.UvPoints[1].X, record.UvPoints[1].Y, pose.BasisU.Y);
        linked.UvPoints[2] = new Vec3(record.UvPoints[2].X, record.UvPoints[2].Y, pose.BasisU.Z);

        int count = record.GetFaceCount();
        int kept = 0;
        linked.Faces = default;
        for (int f = 0; f < count; f++)
        {
            int linkedFace = face(record.Faces[f]);
            if (linkedFace >= 0)
            {
                linked.Faces[kept++] = linkedFace;
            }
        }

        linked.FaceCountAndRenderOrder = (ushort)((record.FaceCountAndRenderOrder & DOverlay.RenderOrderMask) | kept);
        return linked;
    }

    /// <summary>
    /// vvis's two water passes run again over the linked level: which leaves
    /// see a water leaf (<c>CONTENTS_TESTFOGVOLUME</c>) and how far each leaf
    /// is from the water it sees (<c>LeafMinDistToWater</c>), over the
    /// level's visibility; only for a level with a water leaf.
    /// </summary>
    /// <remarks>
    /// Each room's compile worked both out over its own rows alone, blind to
    /// a neighbour's water through a doorway (the rooms design, 4.6); the
    /// passes need nothing but the leaves, the rows and the water faces, so
    /// the link runs vvis's own (<see cref="VisWater"/>) over what it wrote.
    /// A level without water keeps its rooms' bytes: every leaf 65535 and no
    /// leaf marked, which is what the passes would give it.
    /// </remarks>
    private static void RecomputeWaterSight(BspData linked, byte[] pvs, int rowBytes, int clusterCount, CancellationToken cancellationToken)
    {
        VisLeaves leaves = VisLeaves.From(linked);
        bool any = false;
        for (int l = 0; l < leaves.Count && !any; l++)
        {
            any = leaves.LeafWaterDataId(l) != -1;
        }

        if (!any)
        {
            return;
        }

        ushort[] distances = new ushort[leaves.Count];
        int[][] clusterLeaves = VisWater.BuildClusterTable(leaves, clusterCount);
        VisWater.CalcVisibleFogVolumes(leaves, pvs, rowBytes, clusterLeaves, distances);
        VisWater.CalcDistanceFromLeavesToWater(linked, leaves, pvs, rowBytes, clusterLeaves, distances, cancellationToken);
        linked[BspLump.Leafs] = new BspLumpData(leaves.Bytes, leaves.Version, 0);
        linked.SetLump(BspLump.LeafMinDistToWater, MemoryMarshal.AsBytes(distances.AsSpan()).ToArray());
    }

    /// <summary>
    /// A jointed water socket's doorway, as its carve needs it: the
    /// placement, the room's description of the water at the socket, the
    /// level in world space and the level's record for it.
    /// </summary>
    /// <param name="Plan">The placement.</param>
    /// <param name="Door">What the room's compile holds at the socket (<see cref="RoomWater.Doors"/>).</param>
    /// <param name="Level">The water's surface height, moved with the placement (whole cells; the grid's are unmoved).</param>
    /// <param name="Record">The level's water record the doorway's water names (<see cref="RoomPlan.WaterMap"/>); 0 while only counting.</param>
    internal sealed record DoorwayWater(RoomPlan Plan, RoomWaterDoor Door, float Level, short Record);

    /// <summary>A doorway's water leaf: the placement, the socket's water, the linked leaf and its box.</summary>
    internal sealed record DoorwayWaterPiece(RoomPlan Plan, RoomWaterDoor Door, int Leaf, Box Box)
    {
        /// <summary>The water brush the level adds for the piece (<see cref="AddDoorwayBrushes"/>); -1 until added.</summary>
        public int Brush { get; set; } = -1;
    }

    /// <summary>
    /// A doorway's surface face: the placement, the room face it follows
    /// (the room's own surface there, seen from above or below), the
    /// rectangle it covers at the level, and the leaf that lists it.
    /// </summary>
    internal sealed record DoorwayWaterFace(RoomPlan Plan, int TemplateFace, Box Rect, int Leaf);

    /// <summary>
    /// What the carve of the level's water doorways made, for the assembly
    /// to finish: the water leaves (their brushes and fluid convexes) and
    /// the surface faces (their edges, vertices and leaf lists), in the
    /// order the carve made them.
    /// </summary>
    /// <remarks>
    /// A doorway's faces sit in the linked face list right after every
    /// placement's world faces and before the brush models' (model 0's
    /// faces are a map's first), so their count must be known before the
    /// bases are assigned: <see cref="CountWaterDoorwayFaces"/> carves every
    /// water doorway on scratch lists first (<see cref="Counting"/>) and
    /// counts what the carve will make.
    /// </remarks>
    internal sealed class WaterDoorways
    {
        /// <summary>Whether this only counts: no piece or face is kept, only how many faces there would be.</summary>
        public bool Counting { get; init; }

        /// <summary>The linked index of the first doorway face: every placement's world faces before it.</summary>
        public int FaceBase { get; init; }

        /// <summary>The doorways' water leaves, in carve order.</summary>
        public List<DoorwayWaterPiece> Pieces { get; } = [];

        /// <summary>The doorways' surface faces, in linked order.</summary>
        public List<DoorwayWaterFace> Faces { get; } = [];

        /// <summary>How many faces the carve made (or would make).</summary>
        public int FaceCount { get; private set; }

        /// <summary>Records a face and returns its linked index.</summary>
        public int AddFace(RoomPlan plan, int template, Box rect, int leaf)
        {
            if (!Counting)
            {
                Faces.Add(new DoorwayWaterFace(plan, template, rect, leaf));
            }

            return FaceBase + FaceCount++;
        }
    }

    /// <summary>The doorway water of a placement's socket, or null for a dry one.</summary>
    private static DoorwayWater? DoorwayWaterOf(RoomPlan plan, int socket)
    {
        if (plan.Water?.Doors[socket] is not { } door)
        {
            return null;
        }

        float lift = plan.Transform.Apply(Vec3.Zero).Z;
        short record = plan.WaterMap is { } map ? (short)map[door.Record] : (short)0;
        return new DoorwayWater(plan, door, door.Level + lift, record);
    }

    /// <summary>
    /// How many surface faces the level's water doorways get: every
    /// placement's carves with water made on scratch lists, in the carve's
    /// own order (<see cref="CarvesByLeaf"/>), counted.
    /// </summary>
    /// <remarks>
    /// The carve cuts a room's solid leaves as their boxes say, and the
    /// boxes, the plugs and the levels are all known before the level is
    /// assembled, so the count is the carve's own, made once more on copies
    /// that are then dropped: nothing of the level is touched.
    /// </remarks>
    private static int CountWaterDoorwayFaces(RoomPlan[] plans)
    {
        WaterDoorways counter = new() { Counting = true };
        foreach (RoomPlan plan in plans)
        {
            if (plan.Water is null || plan.Carves.Count == 0)
            {
                continue;
            }

            foreach ((int roomLeaf, List<(Box, int)> plugs, List<DoorwayWater?> water) in CarvesByLeaf(plan))
            {
                if (water.All(w => w is null))
                {
                    continue;
                }

                DLeaf template = plan.Leafs[roomLeaf];
                Box box = plan.Transform.TranslateBox(plan.Geometry.LeafBoxes[roomLeaf]);
                template.Mins = Short3(box.Mins);
                template.Maxs = Short3(box.Maxs);
                _ = CarveLeaf(0, plan.Leafs, 0, plugs, [], [template], new LinkPlanes(), null, null, -1, null, water, counter);
            }
        }

        return counter.FaceCount;
    }

    /// <summary>
    /// A water doorway's piece of the carve: below the level a water leaf of
    /// the socket's record, and where the level crosses the piece, or tops it
    /// with open doorway above, a node at the level whose faces are the
    /// water's surface seen from above (listed in the leaf above it) and from
    /// below (listed in the water leaf), each following the room's own
    /// surface face there (<see cref="RoomWaterDoor.TopFace"/>,
    /// <see cref="RoomWaterDoor.BottomFace"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where the node goes.</b> At the end of the piece's chain: the
    /// doorway itself (the index the chain's last node names) is what it
    /// splits, so nothing above it changes. A piece the level crosses keeps
    /// its index for the part above (open air, as every doorway is) and
    /// gives the part below a new water leaf. A piece whose top is the level
    /// (the room's compile split the plug there) is wholly water, and its
    /// surface still needs a node on its plane: the node's front is then a
    /// new leaf as thin as the plane, open air of the doorway's cluster,
    /// which only lists the face. A piece at or above the level is open air.
    /// </para>
    /// <para>
    /// <b>Why a node of its own.</b> The engine draws a face from the node it
    /// is listed on, deciding which side of the face it is on by the node's
    /// plane; a face listed on a node of another plane would be culled
    /// wrongly, so the surface lies on a node of the level's plane.
    /// </para>
    /// </remarks>
    private static int CarveWater(
        int linkedLeaf,
        Box door,
        DoorwayWater water,
        int head,
        int pendingNode,
        int pendingSide,
        List<DNode> nodes,
        List<DLeaf> leafs,
        LinkPlanes planes,
        List<ushort>? leafMinDist,
        List<(int Leaf, int Placement, int Cluster)>? doorways,
        int placement,
        int cluster,
        WaterDoorways sink)
    {
        const float Epsilon = RoomLinter.CellEpsilon;
        float level = water.Level;
        if (door.Mins.Z >= level - Epsilon)
        {
            return head;
        }

        // The surface follows the room's own: a face seen from above where
        // the room has one, and from below likewise (a water whose top is
        // nodraw has neither, and its doorway none).
        bool faces = water.Door.TopFace >= 0 || water.Door.BottomFace >= 0;
        bool crosses = door.Maxs.Z > level + Epsilon;
        bool topped = !crosses && faces && Math.Abs(door.Maxs.Z - level) <= Epsilon;
        DLeaf air = leafs[linkedLeaf];
        DLeaf wet = air;
        wet.Contents = water.Door.Contents;
        wet.LeafWaterDataId = water.Record;
        if (!crosses && !topped)
        {
            leafs[linkedLeaf] = wet;
            RecordPiece(sink, water, linkedLeaf, door);
            return head;
        }

        // Two leaves under a node at the level: the part above (open air)
        // in front, the water behind.
        int other = leafs.Count;
        int airLeaf, waterLeaf;
        if (crosses)
        {
            air.Mins = Short3(new Vec3(door.Mins.X, door.Mins.Y, level));
            wet.Maxs = Short3(new Vec3(door.Maxs.X, door.Maxs.Y, level));
            leafs[linkedLeaf] = air;
            leafs.Add(wet);
            (airLeaf, waterLeaf) = (linkedLeaf, other);
            RecordPiece(sink, water, other, new Box(door.Mins, new Vec3(door.Maxs.X, door.Maxs.Y, level)));
        }
        else
        {
            air.Mins = Short3(new Vec3(door.Mins.X, door.Mins.Y, level));
            air.Maxs = Short3(new Vec3(door.Maxs.X, door.Maxs.Y, level));
            leafs[linkedLeaf] = wet;
            leafs.Add(air);
            (airLeaf, waterLeaf) = (other, linkedLeaf);
            RecordPiece(sink, water, linkedLeaf, door);
        }

        leafMinDist?.Add(leafMinDist[linkedLeaf]);
        doorways?.Add((other, placement, cluster));

        Box rect = new(new Vec3(door.Mins.X, door.Mins.Y, level), new Vec3(door.Maxs.X, door.Maxs.Y, level));
        int first = sink.FaceBase + sink.FaceCount;
        int count = 0;
        foreach ((int template, int leaf) in (ReadOnlySpan<(int, int)>)[(water.Door.TopFace, airLeaf), (water.Door.BottomFace, waterLeaf)])
        {
            if (template >= 0)
            {
                _ = sink.AddFace(water.Plan, template, rect, leaf);
                count++;
            }
        }

        IntArray2 children = default;
        children[0] = -(airLeaf + 1);
        children[1] = -(waterLeaf + 1);
        (int plane, bool flipped) = planes.Intern(new Vec3(0, 0, 1), level);
        int node = nodes.Count;
        nodes.Add(new DNode
        {
            PlaneNum = plane,
            Children = Orient(children, flipped),
            Mins = Short3(door.Mins),
            Maxs = Short3(door.Maxs),
            FirstFace = (ushort)(count == 0 ? 0 : first),
            NumFaces = (ushort)count,
            Area = -1,
        });

        if (pendingNode < 0)
        {
            return node;
        }

        DNode parent = nodes[pendingNode];
        IntArray2 parentChildren = parent.Children;
        parentChildren[pendingSide] = node;
        parent.Children = parentChildren;
        nodes[pendingNode] = parent;
        return head;
    }

    private static void RecordPiece(WaterDoorways sink, DoorwayWater water, int leaf, Box box)
    {
        if (!sink.Counting)
        {
            sink.Pieces.Add(new DoorwayWaterPiece(water.Plan, water.Door, leaf, box));
        }
    }

    /// <summary>
    /// The water doorways' surface faces as the linked face lump holds them,
    /// and their vertices: each the room face it follows (its plane, side,
    /// texinfo, fog volume, styles and flags, moved as the room's faces are,
    /// <see cref="ShiftFace"/>), over the doorway's rectangle at the level,
    /// its four corners wound as that face is wound, with its own edges,
    /// surfedges and original face after every room's.
    /// </summary>
    /// <param name="faces">The doorways' faces, in linked order.</param>
    /// <param name="surfEdgeBase">The linked index of the first doorway surfedge.</param>
    /// <param name="origFaceBase">The linked index of the first doorway original face.</param>
    /// <param name="noDraw">The assembly's nodraw copy of a texinfo (never used: a doorway face is not a plug's).</param>
    /// <returns>The faces and their vertices, four a face.</returns>
    /// <remarks>
    /// A water socket's water is unlit (the room compile refuses a lit one,
    /// <see cref="RoomWater"/>), so the face's lightmap fields are its
    /// template's and nothing reads them; it has no primitives and no
    /// displacement.
    /// </remarks>
    private static (List<DFace> Faces, List<Vec3> Vertices) DoorwayFaces(
        List<DoorwayWaterFace> faces, int surfEdgeBase, int origFaceBase, Func<RoomPlan, int, int> noDraw)
    {
        List<DFace> linked = new(faces.Count);
        List<Vec3> vertices = new(4 * faces.Count);
        for (int f = 0; f < faces.Count; f++)
        {
            DoorwayWaterFace door = faces[f];
            DFace template = BspStructView.As<DFace>(door.Plan.Bsp[BspLump.Faces])[door.TemplateFace];
            DFace face = ShiftFace(door.Plan, template, stripped: false, noDraw, original: false);
            face.FirstEdge = surfEdgeBase + (4 * f);
            face.NumEdges = 4;
            face.OrigFace = origFaceBase + f;
            face.DispInfo = -1;
            face.FirstPrimId = 0;
            face.NumPrimsAndFlags = (ushort)(template.NumPrimsAndFlags & 0x8000);
            face.Area = (door.Rect.Maxs.X - door.Rect.Mins.X) * (door.Rect.Maxs.Y - door.Rect.Mins.Y);
            linked.Add(face);

            // Wound as the template is: seen from its side, the same turn.
            Vec3[] corners = RoomWater.FaceCorners(door.Plan.Bsp, template);
            bool clockwiseFromAbove = Vec3.Cross(corners[1] - corners[0], corners[2] - corners[0]).Z < 0;
            float z = door.Rect.Mins.Z;
            Vec3 a = new(door.Rect.Mins.X, door.Rect.Mins.Y, z), b = new(door.Rect.Mins.X, door.Rect.Maxs.Y, z);
            Vec3 c = new(door.Rect.Maxs.X, door.Rect.Maxs.Y, z), d = new(door.Rect.Maxs.X, door.Rect.Mins.Y, z);
            vertices.AddRange(clockwiseFromAbove ? [a, b, c, d] : [a, d, c, b]);
        }

        return (linked, vertices);
    }

    /// <summary>
    /// A water brush for every water doorway leaf, after the level's own
    /// brushes (never folded): the leaf's box, six axial sides of the
    /// socket's water surface texinfo, the room's water contents; each
    /// listed in its leaf's brush run, which is otherwise empty.
    /// </summary>
    /// <remarks>
    /// vbsp gives a water leaf the water brushes in it, and the engine's
    /// traces meet water by its brushes (point contents read the leaf), so
    /// the doorway's water is a brush as the flattened level's doorway brush
    /// is. The contents are the room's water leaf's but for the flag vvis
    /// sets on leaves, which a brush never carries.
    /// </remarks>
    private static void AddDoorwayBrushes(
        List<DoorwayWaterPiece> pieces, List<DBrush> brushes, List<DBrushSide> brushSides, List<DLeaf> leafs, List<int> leafBrushes, LinkPlanes planes)
    {
        foreach (DoorwayWaterPiece piece in pieces)
        {
            RoomPlan plan = piece.Plan;
            short texInfo = (short)plan.TexInfoRef(BspStructView.As<DLeafWaterData>(plan.Bsp[BspLump.LeafWaterData])[piece.Door.Record].SurfaceTexInfoId);
            piece.Brush = brushes.Count;
            brushes.Add(new DBrush
            {
                FirstSide = brushSides.Count,
                NumSides = 6,
                Contents = piece.Door.Contents & ~(int)BrushContents.TestFogVolume,
            });
            for (int axis = 0; axis < 3; axis++)
            {
                foreach ((float at, bool outward) in (ReadOnlySpan<(float, bool)>)[(Component(piece.Box.Maxs, axis), true), (Component(piece.Box.Mins, axis), false)])
                {
                    (int even, bool flipped) = planes.Intern(Axis(axis), at);
                    brushSides.Add(new DBrushSide
                    {
                        PlaneNum = (ushort)(even + (outward == flipped ? 1 : 0)),
                        TexInfo = texInfo,
                        DispInfo = 0,
                        Bevel = 0,
                    });
                }
            }

            DLeaf leaf = leafs[piece.Leaf];
            leaf.FirstLeafBrush = (ushort)leafBrushes.Count;
            leaf.NumLeafBrushes = 1;
            leafs[piece.Leaf] = leaf;
            leafBrushes.Add(piece.Brush);
            Limit(plan, "leaf brushes", leafBrushes.Count, ushort.MaxValue + 1);
        }
    }

    /// <summary>
    /// A water doorway leaf's fluid convex: its box, in IVP's own axes, its
    /// client data the leaf's brush, to join the fluid of its room's water
    /// against the plug (<see cref="RoomWaterDoor.Fluid"/>).
    /// </summary>
    private static IvpCompactLedge DoorwayConvex(ManagedCollisionCooker cooker, DoorwayWaterPiece piece)
    {
        Box box = piece.Box;
        byte[] blob = cooker.CookPlanes(
            [
                (1, 0, 0, box.Maxs.X), (-1, 0, 0, -box.Mins.X),
                (0, 1, 0, box.Maxs.Y), (0, -1, 0, -box.Mins.Y),
                (0, 0, 1, box.Maxs.Z), (0, 0, -1, -box.Mins.Z),
            ],
            PhysCollisionEmitter.VPhysicsMerge) ?? throw new LinkException($"room {piece.Plan.Placement.Room.Definition.Name}'s doorway water builds no convex");
        IvpCompactLedge ledge = IvpCollideQueries.Leaves(IvpCollideQueries.Surface(blob))[0];
        ledge.ClientData = piece.Brush;
        return ledge;
    }
}
