//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Cubemaps;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Where one overlay stands for one quarter turn of its room: the three
/// vectors of its record that a turn changes, the placement's translation
/// not yet added.
/// </summary>
/// <param name="Origin">The overlay's <c>BasisOrigin</c>, turned about the room's own origin.</param>
/// <param name="BasisU">
/// Its <c>BasisU</c>, turned: the record keeps it in the <c>z</c> of its
/// first three UV points, which is where the link writes it back.
/// </param>
/// <param name="BasisNormal">Its <c>BasisNormal</c>, turned.</param>
internal readonly record struct RoomOverlayPose(Vec3 Origin, Vec3 BasisU, Vec3 BasisNormal);

/// <summary>
/// A room's overlays as the link carries them: every <c>info_overlay</c>
/// record of the room's compile, turned for each quarter turn (the rooms
/// design, 4.9).
/// </summary>
/// <remarks>
/// <para>
/// <b>What a turn changes.</b> vbsp writes one record per
/// <c>info_overlay</c> into the <c>Overlays</c> lump
/// (<see cref="Bsp.Overlays.OverlaySet"/>) and its fade distances into
/// <c>OverlayFades</c>. Of a record, a placement moves the origin (a point:
/// turned, then the cell added) and turns the basis normal and
/// <c>BasisU</c> (directions), which the record packs into the <c>z</c> of
/// its first three UV points. The UV points' <c>x</c> and <c>y</c> are in
/// the overlay's own basis and do not move; the fourth point's <c>z</c>,
/// the flag that says the basis is left-handed, is unchanged, since a quarter
/// turn about +z is a proper rotation; the extents, render order and fades
/// are the room's. The rest is the level's: the face list, the texinfo and
/// the id are rebased at link (<c>LevelLinker.LinkOverlay</c>).
/// </para>
/// <para>
/// <b>Per turn.</b> The three vectors are stored for all four quarter turns
/// (the rooms design's pack rule, 1.1: data the link would otherwise turn
/// element by element is turned at pack time), so the link only adds the
/// placement's translation. Each turned component is written as the
/// flatten writes it, a negative zero unsigned (<see cref="Turn"/>), so the
/// linked record carries the floats vbsp reads back from the flattened
/// level's keys. The section records how many turns it holds, 1 or 4, as
/// every per-turn section of that design does; a pack with one holds turn
/// 0 and the link turns it, to the same bytes (<see cref="Poses"/>), so the
/// choice can change on measurement alone.
/// </para>
/// <para>
/// <b>Required, not only a shortcut.</b> Everything here could be worked out
/// from the compiled lump, but a room is held to the overlay rules of the
/// pack (<see cref="PlugProblem"/>, and no <c>room_needs</c> on an overlay)
/// when <c>ssmap room</c> compiles it, and this section is what says it
/// was. So a room whose lump has overlays and that has none of this bound to
/// its compile (a pack written before overlays were carried) is refused at
/// link, naming the room, as a room with static props and no prop data is.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>OVLY</c>) follows the
/// link sections' framing (<see cref="RoomLinkSections"/>): a codec byte,
/// the payload's decoded length (<c>int64</c>, big-endian), and a payload
/// that starts with an <c>int32</c> revision (<see cref="Revision"/>). After
/// the revision, big-endian <c>int32</c>s: the overlay count (the room's
/// lump's), then the turn count, 1 or 4, and per turn per overlay nine
/// little-endian floats: origin, <c>BasisU</c>, basis normal. A section of a
/// revision this build does not read is absent; a section that does not fit
/// the room's lump is refused as damaged. The tag is one an older build
/// skips, and such a build refuses a room with overlays by its lump, so the
/// pack's format version is unchanged.
/// </para>
/// <para>
/// <b>Water overlays</b> (<c>overlaytransition</c>, the <c>WaterOverlays</c>
/// lump) are carried with the room's water (<see cref="RoomWater"/>), and
/// held to the same plug rule (<see cref="PlugProblem"/>).
/// </para>
/// <para>
/// <b>Binding.</b> It describes one compile's lump: read from a pack or
/// built by the room compile, it remembers the BSP, and the link uses it
/// only for that very BSP (<see cref="IsFor"/>), as the other stored work is
/// used.
/// </para>
/// </remarks>
internal sealed class RoomOverlays
{
    /// <summary>The tag of a room's overlay section.</summary>
    public const string SectionTag = "OVLY";

    /// <summary>The revision this build writes and reads: the link sections' own (<see cref="RoomLinkSections.RevisionFor"/>).</summary>
    public const int Revision = RoomLinkSections.Revision;

    /// <summary>The class vbsp gives a named overlay's entity, which keeps the overlay's keys and gains its id.</summary>
    public const string AccessorClass = "info_overlay_accessor";

    /// <summary>The key the accessor names its overlay by: the record's id.</summary>
    public const string IdKey = "OverlayID";

    /// <summary>The key an overlay is placed by: the point its basis stands on.</summary>
    public const string OriginKey = "BasisOrigin";

    /// <summary>The overlay's class in a VMF.</summary>
    private const string OverlayClass = "info_overlay";

    private readonly BspData? _bsp;
    private readonly RoomOverlayPose[][] _poses;

    private RoomOverlays(RoomOverlayPose[][] poses, BspData? bsp)
    {
        _poses = poses;
        _bsp = bsp;
    }

    /// <summary>How many overlays the room's compile wrote.</summary>
    public int Count => _poses[0].Length;

    /// <summary>How many turns the poses are stored for: 4 when built, 1 or 4 when read.</summary>
    public int TurnCount => _poses.Length;

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The overlays' poses at one quarter turn: the stored turn, or turn 0 turned when only it is stored.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public RoomOverlayPose[] Poses(int rotation)
    {
        if (_poses.Length == 4)
        {
            return _poses[rotation];
        }

        RoomOverlayPose[] turned = new RoomOverlayPose[Count];
        for (int i = 0; i < turned.Length; i++)
        {
            turned[i] = Turn(_poses[0][i], rotation);
        }

        return turned;
    }

    /// <summary>
    /// The same overlays stored with only turn 0 (the link turns it): what a
    /// pack written with a rotation count of 1 reads as, for the facts that
    /// hold the two storages to the same linked bytes.
    /// </summary>
    internal RoomOverlays WithTurnZeroOnly() => new([_poses[0]], _bsp);

    /// <summary>
    /// An overlay's pose turned by quarter turns, as the flatten turns its
    /// entity's keys (<see cref="VmfPlacement.MoveEntity"/>).
    /// </summary>
    /// <param name="pose">The pose in the room's own frame.</param>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    /// <returns>The turned pose; turn 0 is the pose itself.</returns>
    /// <remarks>
    /// The flatten turns <c>BasisU</c>, <c>BasisV</c> and
    /// <c>BasisNormal</c> only for a turned placement, and writes each
    /// number back in its shortest round-trip spelling with a negative zero
    /// written as zero, which vbsp then reads; so a turned direction loses a
    /// negative zero too and a turn-0 pose is left alone. The origin is
    /// turned and nothing more: the flatten writes it moved, so the link
    /// unsigns it once the cell is added (<c>LevelLinker.LinkOverlay</c>).
    /// </remarks>
    public static RoomOverlayPose Turn(RoomOverlayPose pose, int rotation)
    {
        if (rotation == 0)
        {
            return pose;
        }

        return new RoomOverlayPose(
            RoomTransform.Rotate(pose.Origin, rotation),
            RoomStaticProps.Unsigned(RoomTransform.Rotate(pose.BasisU, rotation)),
            RoomStaticProps.Unsigned(RoomTransform.Rotate(pose.BasisNormal, rotation)));
    }

    /// <summary>A record's pose in the room's own frame: its origin, the <c>BasisU</c> its UV points carry, its basis normal.</summary>
    public static RoomOverlayPose PoseOf(in DOverlay overlay) => new(
        overlay.Origin,
        new Vec3(overlay.UvPoints[0].Z, overlay.UvPoints[1].Z, overlay.UvPoints[2].Z),
        overlay.BasisNormal);

    /// <summary>
    /// A compiled room's overlays for the link, or null when its compile
    /// wrote none: every record's pose turned four ways.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The overlays, bound to <paramref name="bsp"/>; or null.</returns>
    /// <exception cref="LinkException">
    /// The lumps are not the ones vbsp writes: a record whose id is not its
    /// place in the lump, or a fade lump of another length.
    /// </exception>
    public static RoomOverlays? Build(string room, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bsp);
        ReadOnlySpan<DOverlay> overlays = Records(room, bsp);
        if (overlays.Length == 0)
        {
            return null;
        }

        RoomOverlayPose[][] poses = [new RoomOverlayPose[overlays.Length], new RoomOverlayPose[overlays.Length],
            new RoomOverlayPose[overlays.Length], new RoomOverlayPose[overlays.Length]];
        for (int i = 0; i < overlays.Length; i++)
        {
            RoomOverlayPose pose = PoseOf(overlays[i]);
            for (int turn = 0; turn < 4; turn++)
            {
                poses[turn][i] = Turn(pose, turn);
            }
        }

        return new RoomOverlays(poses, bsp);
    }

    /// <summary>
    /// A room's overlay records, checked against what vbsp writes: each id is
    /// the record's place in the lump (<see cref="Bsp.Overlays.OverlaySet.AddFromEntity"/>
    /// numbers them so, and the link rebases the id by the same base as the
    /// place), and there is one fade record per overlay.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The records; empty when the room has none.</returns>
    /// <exception cref="LinkException">An id out of place, or a fade lump of another length.</exception>
    internal static ReadOnlySpan<DOverlay> Records(string room, BspData bsp)
    {
        ReadOnlySpan<DOverlay> overlays = BspStructView.As<DOverlay>(bsp[BspLump.Overlays]);
        int fades = BspStructView.Count<DOverlayFade>(bsp[BspLump.OverlayFades]);
        if (fades != overlays.Length)
        {
            throw new LinkException($"room {room} has {overlays.Length} overlays and {fades} overlay fades; vbsp writes one fade per overlay.");
        }

        for (int i = 0; i < overlays.Length; i++)
        {
            if (overlays[i].Id != i)
            {
                throw new LinkException($"room {room}'s overlay {i} has id {overlays[i].Id}; vbsp numbers a map's overlays from 0 in order.");
            }
        }

        return overlays;
    }

    /// <summary>The pack section holding these overlays.</summary>
    /// <param name="codec">How to store the payload; none by default, as every link section.</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    internal RoomPackSectionData ToSection(RoomLinkCodec codec = RoomLinkCodec.None)
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(Count);
        w.Int(_poses.Length);
        foreach (RoomOverlayPose[] turn in _poses)
        {
            w.Structs<RoomOverlayPose>(turn, counted: false);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), codec));
    }

    /// <summary>
    /// A room's overlays from its section, bound to <paramref name="bsp"/>;
    /// or null when the section is absent or of a revision this build does not read.
    /// </summary>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP, whose lump the section must fit.</param>
    /// <returns>The overlays, or null.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload that decodes to another
    /// length than recorded, or a payload that does not fit the room's lump:
    /// cut short, another overlay count, a turn count other than 1 or 4,
    /// bytes after its end.
    /// </exception>
    internal static RoomOverlays? Read(ArraySegment<byte>? section, string room, BspData bsp)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        int lump = BspStructView.Count<DOverlay>(bsp[BspLump.Overlays]);
        int count = r.Int();
        if (count != lump || count == 0)
        {
            throw r.Mismatch($"{count} overlays; the room has {lump}");
        }

        int turns = r.Int();
        if (turns is not (1 or 4))
        {
            throw r.Mismatch($"{turns} turns of overlays; a section holds 1 or 4");
        }

        RoomOverlayPose[][] poses = new RoomOverlayPose[turns][];
        for (int t = 0; t < turns; t++)
        {
            poses[t] = r.Structs<RoomOverlayPose>("overlays", count, counted: false);
        }

        r.End();
        return new RoomOverlays(poses, bsp);
    }

    /// <summary>
    /// What is wrong with a room's overlays for the link, as the rooms
    /// design's refusal says it, or null: an <c>info_overlay</c> or a water
    /// overlay whose <c>sides</c> names a side of one of the room's socket
    /// plugs.
    /// </summary>
    /// <param name="definition">The room: its name, cell and sockets.</param>
    /// <param name="room">The room's VMF, room-local.</param>
    /// <returns>The refusal's text, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> An overlay is drawn on the faces its sides make. A plug is
    /// a wall only where its socket is capped; at a joint the link strips
    /// it and draws its faces nodraw, and the flatten leaves it out, so an
    /// overlay on it would hang in an open doorway in one map and be gone in
    /// the other. An overlay names sides of its own room only, so a doorway
    /// that wants one on each side gets one in each room (the rooms design,
    /// 4.9).
    /// </para>
    /// <para>
    /// <b>How.</b> A plug is the world brush whose box is its socket's plug
    /// box, the rule the flatten leaves joined plugs out by
    /// (<see cref="RoomLibraryVmf.Same"/>). The <c>sides</c> list is read as
    /// vbsp reads it (<see cref="CubemapFixups.ParseSideList"/>). A side id
    /// the room does not have is not refused: vbsp ignores it, in the room's
    /// compile and in the flattened level's alike. The split refuses a
    /// library with such an overlay, so the pack and the flatten both do,
    /// and a room compile refuses one too.
    /// </para>
    /// </remarks>
    public static string? PlugProblem(RoomDefinition definition, VmfDocument room)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(room);
        List<VmfChunk> overlays = [.. room.GetChunks(MapFileLoader.EntityChunk)
            .Where(e => string.Equals(e.GetValue("classname"), OverlayClass, StringComparison.Ordinal))];

        // Water overlays too, which name sides the same way: the
        // overlaydata of every overlaytransition, the world's and any
        // entity's.
        List<VmfChunk> waterOverlays = [.. room.Chunks
            .SelectMany(c => c.GetChunks(MapFileLoader.OverlayTransitionChunk))
            .SelectMany(t => t.GetChunks(MapFileLoader.OverlayDataChunk))];
        if (overlays.Count == 0 && waterOverlays.Count == 0)
        {
            return null;
        }

        Dictionary<int, string> plugSides = [];
        List<(Box Box, string Socket)> plugs = [.. definition.Sockets.Select(
            s => (RoomLinter.SealBox(definition, s, definition.CellSize), s.Name))];
        foreach (VmfChunk world in room.GetChunks(MapFileLoader.WorldChunk))
        {
            foreach (VmfChunk solid in world.GetChunks(MapFileLoader.SolidChunk))
            {
                Box box = VmfPlacement.Bounds(solid);
                foreach ((Box plug, string socket) in plugs)
                {
                    if (!RoomLibraryVmf.Same(box, plug))
                    {
                        continue;
                    }

                    foreach (VmfChunk side in solid.GetChunks(MapFileLoader.SideChunk))
                    {
                        if (int.TryParse(side.GetValue("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                        {
                            plugSides.TryAdd(id, socket);
                        }
                    }
                }
            }
        }

        foreach (VmfChunk overlay in overlays)
        {
            foreach (int side in CubemapFixups.ParseSideList(overlay.GetValue("sides") ?? string.Empty))
            {
                if (plugSides.TryGetValue(side, out string? socket))
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"room {definition.Name}: info_overlay {VmfPlacement.IdOf(overlay)} names brush side {side}, which is socket \"{socket}\"'s plug.");
                }
            }
        }

        // A water overlay keeps its last "sides" key, as vbsp reads it.
        foreach (VmfChunk water in waterOverlays)
        {
            string? sides = water.Keys.LastOrDefault(k => string.Equals(k.Name, "sides", StringComparison.OrdinalIgnoreCase))?.Value;
            foreach (int side in CubemapFixups.ParseSideList(sides ?? string.Empty))
            {
                if (plugSides.TryGetValue(side, out string? socket))
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"room {definition.Name}: a water overlay names brush side {side}, which is socket \"{socket}\"'s plug.");
                }
            }
        }

        return null;
    }
}
