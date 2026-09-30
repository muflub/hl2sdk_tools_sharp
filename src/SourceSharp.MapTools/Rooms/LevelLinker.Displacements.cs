//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// A room's displacements as the link carries them, or null when its
    /// compile wrote none.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <returns>The displacement data bound to the room's compile; null for a room without displacements.</returns>
    /// <exception cref="LinkException">
    /// The room's lumps have displacements but the room carries no
    /// displacement data from its compile, or the lumps are not the ones
    /// vbsp writes (<see cref="RoomDisplacements.Records"/>): among them a
    /// collision lump for displacements the room does not have.
    /// </exception>
    /// <remarks>
    /// The data is what says the room was held to the pack's displacement
    /// rules (<see cref="RoomDisplacements.Problem"/>) when it was compiled,
    /// so a room without it (a pack written before displacements were
    /// carried, or a room built without <c>ssmap room</c>) is refused rather
    /// than linked unchecked, as a room with overlays and no overlay data is.
    /// </remarks>
    internal static RoomDisplacements? RoomDisplacementsOf(RoomObject room)
    {
        string name = room.Definition.Name;
        int count = RoomDisplacements.Records(name, room.Bsp).Length;
        if (count == 0)
        {
            int collision = PhysDispCount(room.Bsp);
            if (collision != 0)
            {
                throw new LinkException($"room {name} carries displacement collision for {collision} displacements but has none.");
            }

            return null;
        }

        return room.DisplacementsOfCompile ?? throw new LinkException(
            $"room {name} has {count} displacements but no displacement data from its compile"
            + " (a pack written before the link carried displacements, or a room built without ssmap room);"
            + " recompile the library with ssmap room.");
    }

    /// <summary>How many displacements a room's collision lump holds entries for.</summary>
    private static int PhysDispCount(BspData bsp) =>
        Bsp.Collision.PhysDispLump.ReadSizes(bsp[BspLump.PhysDisp].Data.Span).Count;

    /// <summary>
    /// Refuses a level with more displacements than a map holds
    /// (<see cref="RoomDisplacements.MaxMapDispInfo"/>), naming the room and
    /// cell that crossed it: the SDK's vbsp refuses a map past it, so the
    /// flattened level would not compile with the stock tools. The fields
    /// that index displacements (a face's <c>short</c>, a neighbour's
    /// <c>ushort</c>) are wider, so this is the first to fill.
    /// </summary>
    internal static void DisplacementLimit(string room, int cellX, int cellY, long displacements)
    {
        if (displacements > RoomDisplacements.MaxMapDispInfo)
        {
            throw new LinkException(
                $"room {room} at cell ({cellX}, {cellY}) pushes the link to {displacements} displacements;"
                + $" a map holds at most {RoomDisplacements.MaxMapDispInfo} (MAX_MAP_DISPINFO).");
        }
    }

    /// <summary>The level's five displacement lumps, each with the version the rooms wrote it at.</summary>
    internal sealed record LinkedDisplacements(
        (byte[] Bytes, int Version) Infos,
        (byte[] Bytes, int Version) Verts,
        (byte[] Bytes, int Version) Tris,
        (byte[] Bytes, int Version) Alphas,
        (byte[] Bytes, int Version) SamplePositions);

    /// <summary>
    /// The level's displacement lumps: every placement's displacements in
    /// link order, each record rebased and its start moved, each vertex's
    /// vector turned (<see cref="LinkDisplacement"/>), the triangles, alphas
    /// and sample positions the room's bytes; or null when no placed room has
    /// a displacement, so a level without them carries none of the lumps, as
    /// it did before displacements were carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order.</b> vbsp numbers a map's displacements in the order their
    /// sides load, and the flattened level writes the placements' brushes in
    /// link order, each room's in its own order, so its compile numbers them
    /// as the placements' displacements one after another, which is the
    /// order here: placement <i>p</i>'s displacement <i>k</i> is
    /// <see cref="RoomPlan.DispBase"/> + <i>k</i>.
    /// </para>
    /// <para>
    /// <b>No stitching.</b> A room's displacements are its compile's, with
    /// the neighbours, allowed vertices and smoothed normals it found among
    /// them; none is added across a joint (the pack refuses a displacement
    /// that reaches into a doorway, so the flattened level finds none there
    /// either).
    /// </para>
    /// </remarks>
    private static LinkedDisplacements? LinkDisplacements(RoomPlan[] plans)
    {
        RoomPlan? first = plans.FirstOrDefault(p => p.Displacements is not null);
        if (first is null)
        {
            return null;
        }

        List<DispInfo> infos = [];
        List<DispVert> verts = [];
        List<byte> tris = [], alphas = [], samples = [];
        foreach (RoomPlan plan in plans)
        {
            if (plan.Displacements is not { } data)
            {
                continue;
            }

            string room = plan.Placement.Room.Definition.Name;
            int rotation = plan.Transform.Placement.NormalizedRotation;
            ReadOnlySpan<DispInfo> records = RoomDisplacements.Records(room, plan.Bsp);
            Vec3[] starts = data.Starts(rotation);
            Vec3 offset = plan.Transform.Apply(Vec3.Zero);
            for (int i = 0; i < records.Length; i++)
            {
                infos.Add(LinkDisplacement(records[i], starts[i], offset, plan, room));
            }

            Vec3[] vectors = data.Vectors(rotation);
            ReadOnlySpan<DispVert> roomVerts = BspStructView.As<DispVert>(plan.Bsp[BspLump.DispVerts]);
            for (int v = 0; v < roomVerts.Length; v++)
            {
                verts.Add(roomVerts[v] with { Vector = vectors[v] });
            }

            tris.AddRange(plan.Bsp[BspLump.DispTris].Data.Span);
            alphas.AddRange(plan.Bsp[BspLump.DispLightmapAlphas].Data.Span);
            samples.AddRange(plan.Bsp[BspLump.DispLightmapSamplePositions].Data.Span);
        }

        BspData bsp = first.Bsp;
        return new LinkedDisplacements(
            (Bytes(infos), bsp[BspLump.DispInfo].Version),
            (Bytes(verts), bsp[BspLump.DispVerts].Version),
            ([.. tris], bsp[BspLump.DispTris].Version),
            ([.. alphas], bsp[BspLump.DispLightmapAlphas].Version),
            ([.. samples], bsp[BspLump.DispLightmapSamplePositions].Version));
    }

    /// <summary>
    /// One displacement record as its placement links it: the start moved,
    /// every run and index rebased.
    /// </summary>
    /// <param name="record">The room's record.</param>
    /// <param name="start">Its start position at the placement's turn (<see cref="RoomDisplacements.Starts"/>).</param>
    /// <param name="offset">The placement's translation, <see cref="RoomTransform.Apply"/> of the room's origin.</param>
    /// <param name="plan">The placement, for its bases and linked faces.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <returns>The linked record.</returns>
    /// <exception cref="LinkException">The record's base face is one the level does not draw.</exception>
    /// <remarks>
    /// <para>
    /// <b>The start</b> is the turned start plus the placement's translation,
    /// added as one vector, which is how the flatten moves the
    /// <c>dispinfo</c>'s <c>startposition</c> (<see cref="QuarterTurn.Apply"/>),
    /// and vbsp writes the start it read: the linked record holds the float
    /// the flattened level's compile writes.
    /// </para>
    /// <para>
    /// <b>Rebased:</b> the vertex and triangle runs, the lightmap alpha and
    /// sample-position starts (byte offsets into their lumps), the base face
    /// (the face the link wrote for it), and every neighbour, edge and
    /// corner, by the placement's first displacement. An edge's missing
    /// neighbour (<see cref="DispSubNeighbor.NoNeighbor"/>) stays missing,
    /// and a corner's slots past its neighbour count are the room's.
    /// Everything else (power, flags, smoothing angle, contents, allowed
    /// vertices, the neighbours' orientations and spans) is the room's.
    /// </para>
    /// </remarks>
    internal static DispInfo LinkDisplacement(DispInfo record, Vec3 start, Vec3 offset, RoomPlan plan, string room)
    {
        DispInfo linked = record;
        linked.StartPosition = RoomStaticProps.Unsigned(start + offset);
        linked.DispVertStart = record.DispVertStart + plan.DispVertBase;
        linked.DispTriStart = record.DispTriStart + plan.DispTriBase;
        linked.LightmapAlphaStart = record.LightmapAlphaStart + plan.DispAlphaBase;
        linked.LightmapSamplePositionStart = record.LightmapSamplePositionStart + plan.DispSampleBase;
        if (plan.StrippedFaces.Contains(record.MapFace))
        {
            throw new LinkException($"room {room}'s displacement stands on face {record.MapFace}, a jointed socket's plug.");
        }

        linked.MapFace = (ushort)plan.LinkedFace(record.MapFace);
        for (int e = 0; e < 4; e++)
        {
            for (int s = 0; s < 2; s++)
            {
                ref DispSubNeighbor sub = ref linked.EdgeNeighbors[e].SubNeighbors[s];
                if (sub.IsValid())
                {
                    sub.Neighbor = (ushort)(sub.Neighbor + plan.DispBase);
                }
            }

            ref DispCornerNeighbors corner = ref linked.CornerNeighbors[e];
            for (int n = 0; n < corner.NumNeighbors; n++)
            {
                corner.Neighbors[n] = (ushort)(corner.Neighbors[n] + plan.DispBase);
            }
        }

        return linked;
    }

    /// <summary>
    /// The level's <c>PhysDisp</c> lump: every placement's displacement
    /// collision, one entry per displacement in link order; or the empty
    /// lump (no displacement) when a room wrote one and none has a
    /// displacement, as the link wrote before displacements were carried;
    /// or null when no room wrote one.
    /// </summary>
    /// <remarks>
    /// Each entry is the packed bounding hull of the displacement's
    /// collision mesh, which names the displacement's own vertices by index
    /// and holds no position, so a placement's entries are its room's bytes
    /// whatever its turn and cell; the engine builds the mesh itself from the
    /// linked vertices. A level mixing cooked and uncooked rooms is refused
    /// by the world collision's merge (<see cref="MergeCollision"/>).
    /// </remarks>
    internal static byte[]? LinkDisplacementCollision(RoomPlan[] plans)
    {
        if (!plans.Any(p => p.Bsp[BspLump.PhysDisp].Length > 0))
        {
            return null;
        }

        List<byte[]?> blobs = [];
        foreach (RoomPlan plan in plans)
        {
            if (plan.Displacements is not null)
            {
                blobs.AddRange(RoomDisplacements.CollisionBlobs(plan.Bsp));
            }
        }

        return Bsp.Collision.PhysDispLump.Write(blobs);
    }
}
