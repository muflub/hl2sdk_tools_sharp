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
        return room.WaterOfCompile ?? throw new LinkException(
            $"room {name} has water but no water data from its compile"
            + " (a pack written before the link carried water, or a room built without ssmap room);"
            + " recompile the library with ssmap room.");
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
    private static List<DLeafWaterData>? PlanWaterData(RoomPlan[] plans)
    {
        if (!plans.Any(p => p.Water is not null))
        {
            return null;
        }

        List<DLeafWaterData> table = [];
        foreach (RoomPlan plan in plans)
        {
            if (plan.Water is null)
            {
                continue;
            }

            ReadOnlySpan<DLeafWaterData> records = BspStructView.As<DLeafWaterData>(plan.Bsp[BspLump.LeafWaterData]);
            float lift = plan.Transform.Apply(Vec3.Zero).Z;
            plan.WaterMap = new int[records.Length];
            for (int r = 0; r < records.Length; r++)
            {
                DLeafWaterData linked = records[r];
                linked.SurfaceZ += lift;
                linked.MinZ += lift;
                linked.SurfaceTexInfoId = records[r].SurfaceTexInfoId < 0 ? records[r].SurfaceTexInfoId : (short)plan.TexInfoRef(records[r].SurfaceTexInfoId);
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
    /// vbsp writes a map's fluids after its static solids, one per
    /// connected water volume, so the linked world record lists them after
    /// its contents classes, placement by placement, as the flattened
    /// level's compile lists its volumes found in its tree's order (the
    /// order differs; the volumes, planes and keys are the same).
    /// </remarks>
    private static List<(byte[] Blob, PhysFluidEntry Entry)> LinkFluids(
        RoomPlan[] plans, ManagedCollisionCooker cooker, List<string> materials, int[]? brushMap, CancellationToken cancellationToken)
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
                    records[i].TexInfo < 0 ? records[i].TexInfo : plan.TexInfoRef(records[i].TexInfo),
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
}
