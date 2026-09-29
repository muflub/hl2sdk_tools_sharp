//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Overlays;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// A room's overlays as the link carries them, or null when its compile
    /// wrote none.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <returns>The overlay data bound to the room's compile; null for a room without overlays.</returns>
    /// <exception cref="LinkException">
    /// The room's lump has overlays but the room carries no overlay data from
    /// its compile, or the lumps are not the ones vbsp writes.
    /// </exception>
    /// <remarks>
    /// The data is what says the room was held to the pack's overlay rules
    /// (<see cref="RoomOverlays.PlugProblem"/>, no <c>room_needs</c> on an
    /// overlay) when it was compiled, so a room without it (a pack written
    /// before overlays were carried, or a room built without
    /// <c>ssmap room</c>) is refused rather than linked unchecked, as a room
    /// with static props and no prop data is.
    /// </remarks>
    internal static RoomOverlays? RoomOverlaysOf(RoomObject room)
    {
        string name = room.Definition.Name;
        int count = RoomOverlays.Records(name, room.Bsp).Length;
        if (count == 0)
        {
            return null;
        }

        return room.OverlaysOfCompile ?? throw new LinkException(
            $"room {name} has {count} overlays but no overlay data from its compile"
            + " (a pack written before the link carried overlays, or a room built without ssmap room);"
            + " recompile the library with ssmap room.");
    }

    /// <summary>
    /// Refuses a level with more overlays than a map holds
    /// (<see cref="MapOverlay.MaxMapOverlays"/>), naming the room and cell
    /// that crossed it: vbsp refuses a map past it, so the flattened level
    /// would not compile, and the engine's overlay ids are numbered below it
    /// (a water overlay's start just past it).
    /// </summary>
    internal static void OverlayLimit(string room, int cellX, int cellY, long overlays)
    {
        if (overlays > MapOverlay.MaxMapOverlays)
        {
            throw new LinkException(
                $"room {room} at cell ({cellX}, {cellY}) pushes the link to {overlays} overlays;"
                + $" a map holds at most {MapOverlay.MaxMapOverlays} (MAX_MAP_OVERLAYS).");
        }
    }

    /// <summary>
    /// The level's <c>Overlays</c> and <c>OverlayFades</c> lumps: every
    /// placement's overlays in link order, each rebased and moved
    /// (<see cref="LinkOverlay"/>), with its fade record as the room's; or
    /// null when no placed room has an overlay, so a level without them
    /// carries neither lump, as it did before overlays were carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order and ids.</b> vbsp numbers a map's overlays from 0 in entity
    /// order, and a named one's <c>info_overlay_accessor</c> names its id.
    /// The flattened level writes the placements' entities in link order, and
    /// each room's in its own order, so its compile numbers them as the
    /// placements' overlays one after another; the link gives placement
    /// <i>p</i>'s overlay <i>k</i> the id <see cref="RoomPlan.OverlayBase"/>
    /// + <i>k</i>, and the accessor's <c>OverlayID</c> is rebased by the same
    /// base (<see cref="TranslateEntity"/>).
    /// </para>
    /// <para>
    /// <b>Faces.</b> Each face the room's record lists becomes the face the
    /// link wrote for it (<see cref="LinkedOverlayFace"/>); one the level
    /// does not draw is left out.
    /// </para>
    /// <para>
    /// <b>Texinfo.</b> An overlay's texinfo is the one vbsp made for its
    /// material (no flags, zero axes, a -99999 offset), which the shared
    /// table holds like any of the room's texinfos; turning and moving it
    /// changes nothing, so every placement of a material names one entry.
    /// </para>
    /// </remarks>
    private static (byte[] Overlays, byte[] Fades, int Version)? LinkOverlays(RoomPlan[] plans)
    {
        RoomPlan? first = plans.FirstOrDefault(p => p.Overlays is not null);
        if (first is null)
        {
            return null;
        }

        List<DOverlay> overlays = [];
        List<DOverlayFade> fades = [];
        foreach (RoomPlan plan in plans)
        {
            if (plan.Overlays is not { } data)
            {
                continue;
            }

            string room = plan.Placement.Room.Definition.Name;
            ReadOnlySpan<DOverlay> records = RoomOverlays.Records(room, plan.Bsp);
            RoomOverlayPose[] poses = data.Poses(plan.Transform.Placement.NormalizedRotation);
            Vec3 offset = plan.Transform.Apply(Vec3.Zero);
            for (int i = 0; i < records.Length; i++)
            {
                DOverlay record = records[i];
                overlays.Add(LinkOverlay(
                    record,
                    poses[i],
                    offset,
                    plan.OverlayBase,
                    record.TexInfo < 0 ? record.TexInfo : plan.TexInfoRef(record.TexInfo),
                    face => LinkedOverlayFace(plan, face)));
            }

            fades.AddRange(BspStructView.As<DOverlayFade>(plan.Bsp[BspLump.OverlayFades]));
        }

        return (Bytes(overlays), Bytes(fades), first.Bsp[BspLump.Overlays].Version);
    }

    /// <summary>
    /// One overlay record as its placement links it: the id rebased, the
    /// texinfo the shared one, the origin moved, the basis turned, the face
    /// list the linked faces.
    /// </summary>
    /// <param name="record">The room's record.</param>
    /// <param name="pose">Its pose at the placement's turn (<see cref="RoomOverlays.Poses"/>).</param>
    /// <param name="offset">The placement's translation, <see cref="RoomTransform.Apply"/> of the room's origin.</param>
    /// <param name="idBase">The placement's first linked overlay id.</param>
    /// <param name="texInfo">The linked texinfo of the record's.</param>
    /// <param name="face">A room face's linked index, or -1 for one the level does not draw.</param>
    /// <returns>The linked record.</returns>
    /// <remarks>
    /// <para>
    /// <b>The origin</b> is the turned origin plus the placement's
    /// translation, added as one vector, which is how the flatten moves the
    /// entity's <c>BasisOrigin</c> (<see cref="QuarterTurn.Apply"/>); the
    /// sum's zeros are unsigned, as the flatten writes every number, so the
    /// linked record holds the float vbsp reads back from the flattened
    /// level's key.
    /// </para>
    /// <para>
    /// <b>The rest</b> of the record is the room's: the U and V extents, the
    /// UV points' <c>x</c> and <c>y</c> (in the overlay's basis) and the
    /// fourth point's <c>z</c> (the handedness flag, which a turn keeps), and
    /// the render order, kept in the top bits of the face count.
    /// </para>
    /// </remarks>
    internal static DOverlay LinkOverlay(
        DOverlay record, RoomOverlayPose pose, Vec3 offset, int idBase, int texInfo, Func<int, int> face)
    {
        DOverlay linked = record;
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
    /// The linked face an overlay of a placement is drawn on, for the room
    /// face its record lists, or -1 when the level does not draw that face.
    /// </summary>
    /// <param name="plan">The placement.</param>
    /// <param name="roomFace">The room face.</param>
    /// <returns>The linked face, or -1.</returns>
    /// <exception cref="LinkException">The record names a face the room does not have.</exception>
    /// <remarks>
    /// Two kinds of room face are not drawn in the level: a jointed plug's
    /// (kept in the face list, drawn nodraw), and a brush model's that the
    /// level omits (<c>room_needs</c>, socket furniture), which is not in the
    /// face list at all. The flattened level has neither side (a joined plug
    /// is left out, and so is an omitted entity's brush), so its compile
    /// finds no face for them; the link leaves them out the same way. The
    /// pack refuses an overlay that names a plug's side
    /// (<see cref="RoomOverlays.PlugProblem"/>), so the first kind reaches
    /// here only from a room compiled some other way.
    /// </remarks>
    internal static int LinkedOverlayFace(RoomPlan plan, int roomFace)
    {
        if (roomFace < 0 || roomFace >= plan.FaceCount)
        {
            throw new LinkException(
                $"room {plan.Placement.Room.Definition.Name} has an overlay on face {roomFace}; the room has {plan.FaceCount} faces.");
        }

        if (plan.StrippedFaces.Contains(roomFace))
        {
            return -1;
        }

        if (plan.Models is { } models && roomFace >= models.WorldFaces)
        {
            RoomBrushModel owner = models.Owner(m => m.Faces, roomFace)!;
            if (models.Linked[owner.Model - 1] < 0)
            {
                return -1;
            }

            return plan.LinkedFace(roomFace, owner);
        }

        return plan.LinkedFace(roomFace);
    }
}
