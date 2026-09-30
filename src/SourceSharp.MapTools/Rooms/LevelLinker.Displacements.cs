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
            DisplacementBases bases = new(plan.DispBase, plan.DispVertBase, plan.DispTriBase, plan.DispAlphaBase, plan.DispSampleBase);
            for (int i = 0; i < records.Length; i++)
            {
                infos.Add(LinkDisplacement(records[i], starts[i], offset, bases, face => LinkedDisplacementFace(plan, face, room)));
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

    /// <summary>Where a placement's displacement data starts in each of the level's lumps (<see cref="RoomPlan.DispBase"/> and the rest).</summary>
    /// <param name="Disp">The linked index of the placement's first displacement.</param>
    /// <param name="Vert">Its first vertex in <c>DispVerts</c>.</param>
    /// <param name="Tri">Its first triangle in <c>DispTris</c>.</param>
    /// <param name="Alpha">Its first byte in <c>DispLightmapAlphas</c>.</param>
    /// <param name="Sample">Its first byte in <c>DispLightmapSamplePositions</c>.</param>
    internal readonly record struct DisplacementBases(int Disp, int Vert, int Tri, int Alpha, int Sample);

    /// <summary>
    /// One displacement record as its placement links it: the start moved,
    /// every run and index rebased.
    /// </summary>
    /// <param name="record">The room's record.</param>
    /// <param name="start">Its start position at the placement's turn (<see cref="RoomDisplacements.Starts"/>).</param>
    /// <param name="offset">The placement's translation, <see cref="RoomTransform.Apply"/> of the room's origin.</param>
    /// <param name="bases">The placement's bases in the level's lumps.</param>
    /// <param name="face">A room face's linked index (<see cref="LinkedDisplacementFace"/>).</param>
    /// <returns>The linked record.</returns>
    /// <remarks>
    /// <para>
    /// <b>The start</b> is the turned start plus the placement's translation,
    /// added as one vector, which is how the flatten moves the
    /// <c>dispinfo</c>'s <c>startposition</c> (<see cref="QuarterTurn.Apply"/>),
    /// its zeros unsigned as the flatten writes every number; vbsp writes the
    /// start it read, so the linked record holds the float the flattened
    /// level's compile writes.
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
    internal static DispInfo LinkDisplacement(DispInfo record, Vec3 start, Vec3 offset, DisplacementBases bases, Func<int, int> face)
    {
        DispInfo linked = record;
        linked.StartPosition = RoomStaticProps.Unsigned(start + offset);
        linked.DispVertStart = record.DispVertStart + bases.Vert;
        linked.DispTriStart = record.DispTriStart + bases.Tri;
        linked.LightmapAlphaStart = record.LightmapAlphaStart + bases.Alpha;
        linked.LightmapSamplePositionStart = record.LightmapSamplePositionStart + bases.Sample;
        linked.MapFace = (ushort)face(record.MapFace);
        for (int e = 0; e < 4; e++)
        {
            for (int s = 0; s < 2; s++)
            {
                ref DispSubNeighbor sub = ref linked.EdgeNeighbors[e].SubNeighbors[s];
                if (sub.IsValid())
                {
                    sub.Neighbor = (ushort)(sub.Neighbor + bases.Disp);
                }
            }

            ref DispCornerNeighbors corner = ref linked.CornerNeighbors[e];
            for (int n = 0; n < corner.NumNeighbors; n++)
            {
                corner.Neighbors[n] = (ushort)(corner.Neighbors[n] + bases.Disp);
            }
        }

        return linked;
    }

    /// <summary>
    /// The linked face a placement's displacement stands on: its base face,
    /// a world face of the room, where the link wrote it.
    /// </summary>
    /// <exception cref="LinkException">
    /// The base face is a jointed socket's plug, which the level draws
    /// nodraw: the pack refuses a displacement in a doorway
    /// (<see cref="RoomDisplacements.Problem"/>), so only a room compiled
    /// some other way reaches this.
    /// </exception>
    internal static int LinkedDisplacementFace(RoomPlan plan, int roomFace, string room)
    {
        if (plan.StrippedFaces.Contains(roomFace))
        {
            throw new LinkException($"room {room}'s displacement stands on face {roomFace}, a jointed socket's plug.");
        }

        return plan.LinkedFace(roomFace);
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
