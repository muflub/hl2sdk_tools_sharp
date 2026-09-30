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

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys.Managed;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One connected water volume's fluid record of a room's world collision:
/// the <c>fluid</c> block's keys, the surface plane in the room's own frame.
/// </summary>
/// <param name="SurfaceProp">The surface property the block names.</param>
/// <param name="Damping">The damping it names (vbsp always writes 0.01).</param>
/// <param name="Contents">The contents the volume was collected with.</param>
/// <param name="Normal">The surface plane's normal, room-local.</param>
/// <param name="Dist">The surface plane's distance, room-local.</param>
internal sealed record RoomWaterFluid(string SurfaceProp, float Damping, int Contents, Vec3 Normal, float Dist);

/// <summary>
/// A socket's water as its room declares it: the <c>water_&lt;wall&gt;</c>
/// key of its <c>info_room</c> (the rooms design, 4.6: the kit's knowing the
/// water level at a socket).
/// </summary>
/// <param name="Level">
/// The height of the water's surface above the cell's floor, room-local:
/// the same in every room a doorway joins, so the water continues through
/// it. Above the door's sill; at or above the door's top the doorway is
/// wholly under water.
/// </param>
/// <param name="Material">
/// The water's material at the socket, as the author named it (the surface
/// material of the room's water there, and the material the flattened
/// level fills the doorway with).
/// </param>
public sealed record RoomWaterSocket(float Level, string Material);

/// <summary>
/// What a water socket's room compile holds at its doorway, room-local: what
/// the link carves the doorway's water from.
/// </summary>
/// <param name="Level">The water's surface height, as declared and found.</param>
/// <param name="Record">The room's water record (<c>LeafWaterData</c>) the water against the plug names.</param>
/// <param name="TopFace">A face of the room's surface at the level seen from above, whose texinfo and plane the doorway's surface takes; -1 when the doorway is wholly under water.</param>
/// <param name="BottomFace">A face of the surface seen from below (the <c>$bottommaterial</c>), or -1 when the room has none.</param>
/// <param name="Contents">The contents of the room's water leaf against the plug, which the doorway's water leaf takes.</param>
/// <param name="Fluid">The room's fluid the water against the plug is part of, which the doorway's convex joins; -1 without collision.</param>
/// <param name="Material">The declared material.</param>
internal sealed record RoomWaterDoor(float Level, int Record, int TopFace, int BottomFace, int Contents, int Fluid, string Material);

/// <summary>
/// A room's water as the link carries it: the room's leaf water data
/// counted, its fluids' convexes and its water overlays turned for each
/// quarter turn (the rooms design, 4.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>What vbsp writes for water.</b> Every connected water volume of the
/// world is one <c>LeafWaterData</c> record (surface height, lowest point,
/// surface texinfo), named by each of its leaves' <c>LeafWaterDataId</c>
/// and by each warped face's <c>SurfaceFogVolumeId</c>; one <c>fluid</c>
/// solid of the world's collision (<see cref="PhysFluidEntry"/>); a
/// patched material per depth in the pak, named after the room
/// (<c>maps/&lt;room&gt;/&lt;material&gt;_depth_&lt;n&gt;</c>), which the
/// pak merge carries as it carries every room-named file; and a default
/// <c>water_lod_control</c>, which the level keeps once (the singleton
/// rule). vvis then marks every leaf a water leaf sees
/// (<c>CONTENTS_TESTFOGVOLUME</c>) and writes each leaf's distance to the
/// water it sees (<c>LeafMinDistToWater</c>), which the link works out
/// again over the level's visibility.
/// </para>
/// <para>
/// <b>What a turn changes.</b> Nothing of a water record: a turn is about
/// +z and a placement moves by whole cells, so the surface height and the
/// lowest point stay, and only the texinfo is the level's. The fluids'
/// convexes turn as the world's do (stored per turn, the rooms design's
/// pack rule, 1.1, as every convex is), their surface planes turn with
/// them (vertical, so unchanged but for the sign of a zero). A water
/// overlay (<c>overlaytransition</c>) moves as an <c>info_overlay</c> does
/// (<see cref="RoomOverlays"/>): origin, <c>BasisU</c> and normal, stored
/// per turn.
/// </para>
/// <para>
/// <b>Water sockets.</b> Water may reach a socket's plug only where the
/// room declares the socket's water (<see cref="RoomWaterSocket"/>); the
/// compile is held to the declaration and what the link needs to carve the
/// doorway's water is kept (<see cref="Doors"/>, the rooms design's "water
/// sockets").
/// </para>
/// <para>
/// <b>Required, not only a shortcut.</b> A room is held to the water rules
/// of the pack when <c>ssmap room</c> compiles it (<see cref="PlugProblem"/>),
/// and this section is what says it was. So a room whose compile has water
/// and none of this bound to it (a pack written before water was carried)
/// is refused at link, naming the room.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>WATR</c>) follows the
/// link sections' framing (<see cref="RoomLinkSections"/>): a codec byte,
/// the payload's decoded length, then the payload: the revision, the
/// room's water data count, its fluids (surface property, damping as
/// float bits, contents, surface normal and distance), its sockets (per
/// socket a flag, and for a water socket its <see cref="RoomWaterDoor"/>:
/// level, record, surface faces, contents, fluid and material), its water
/// overlay count, then the turn count, 1 or 4, and per turn every fluid's
/// convexes and every water overlay's pose. The tag is one an older build skips, and
/// such a build refuses a room with water by its lumps, so the pack's
/// format version is unchanged.
/// </para>
/// <para>
/// <b>Binding.</b> It describes one compile's lumps: read from a pack or
/// built by the room compile, it remembers the BSP and is used only for
/// that very BSP (<see cref="IsFor"/>).
/// </para>
/// </remarks>
internal sealed class RoomWater
{
    /// <summary>The tag of a room's water section.</summary>
    public const string SectionTag = "WATR";

    /// <summary>The revision this build writes and reads: the link sections' own.</summary>
    public const int Revision = RoomLinkSections.Revision;

    /// <summary>The first id vbsp gives a water overlay: one past the last ordinary overlay a map may hold.</summary>
    public const int FirstWaterOverlayId = Bsp.Overlays.MapOverlay.MaxMapOverlays + 1;

    private readonly BspData? _bsp;
    private readonly RoomLinkSolid[][] _fluidLedges;
    private readonly RoomOverlayPose[][] _overlays;

    private RoomWater(int dataCount, RoomWaterFluid[] fluids, RoomLinkSolid[][] fluidLedges, RoomOverlayPose[][] overlays, RoomWaterDoor?[] doors, BspData? bsp)
    {
        DataCount = dataCount;
        Fluids = fluids;
        _fluidLedges = fluidLedges;
        _overlays = overlays;
        Doors = doors;
        _bsp = bsp;
    }

    /// <summary>
    /// Per socket of the room, in the definition's order, its water
    /// (<see cref="RoomWaterDoor"/>), or null for a dry socket: what the
    /// link carves a jointed water socket's doorway from.
    /// </summary>
    public IReadOnlyList<RoomWaterDoor?> Doors { get; }

    /// <summary>The water level of a socket of the room, or null when the socket is dry.</summary>
    public float? LevelAt(int socket) => Doors[socket]?.Level;

    /// <summary>How many leaf water data records the room's compile wrote.</summary>
    public int DataCount { get; }

    /// <summary>The room's fluids, in the order its collision lists them.</summary>
    public IReadOnlyList<RoomWaterFluid> Fluids { get; }

    /// <summary>How many water overlays the room's compile wrote.</summary>
    public int OverlayCount => _overlays[0].Length;

    /// <summary>How many turns are stored: 4 when built, 1 or 4 when read.</summary>
    public int TurnCount => _overlays.Length;

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The fluids' convexes at one quarter turn: the stored turn, or turn 0 turned when only it is stored.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public RoomLinkSolid[] FluidLedges(int rotation)
    {
        if (_fluidLedges.Length == 4)
        {
            return _fluidLedges[rotation];
        }

        return [.. _fluidLedges[0].Select(solid => TurnSolid(solid, rotation))];
    }

    /// <summary>The water overlays' poses at one quarter turn.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public RoomOverlayPose[] OverlayPoses(int rotation) =>
        _overlays.Length == 4 ? _overlays[rotation] : [.. _overlays[0].Select(p => RoomOverlays.Turn(p, rotation))];

    /// <summary>
    /// A fluid's surface plane at one quarter turn, moved by a placement's
    /// translation: the normal turned, a negative zero written as zero (as
    /// vbsp computes the flattened level's), the distance moved along it.
    /// </summary>
    public static (Vec3 Normal, float Dist) SurfaceAt(RoomWaterFluid fluid, int rotation, Vec3 translation)
    {
        Vec3 normal = RoomStaticProps.Unsigned(RoomTransform.Rotate(fluid.Normal, rotation));
        return (normal, fluid.Dist + Vec3.Dot(normal, translation));
    }

    /// <summary>
    /// The same water stored with only turn 0 (the link turns it): what a
    /// pack written with a rotation count of 1 reads as, for the facts that
    /// hold the two storages to the same linked bytes.
    /// </summary>
    internal RoomWater WithTurnZeroOnly() => new(DataCount, [.. Fluids], [_fluidLedges[0]], [_overlays[0]], [.. Doors], _bsp);

    /// <summary>Whether a room's compile has anything this describes: water data, a water leaf, a fluid or a water overlay.</summary>
    public static bool HasWater(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        if (bsp[BspLump.LeafWaterData].Length > 0 || bsp[BspLump.WaterOverlays].Length > 0)
        {
            return true;
        }

        foreach (DLeaf leaf in BspStructView.As<DLeaf>(bsp[BspLump.Leafs]))
        {
            if (leaf.LeafWaterDataId != -1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A compiled room's water for the link, or null when its compile has
    /// none (<see cref="HasWater"/>): the records counted and checked, the
    /// fluids read from its collision, the convexes and water overlays turned
    /// four ways, and each water socket the room declares held to what the
    /// compile holds against its plug (<see cref="Door"/>) and described for
    /// the link.
    /// </summary>
    /// <param name="definition">The room: its name, cell and sockets.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <param name="declared">The water sockets the room declares, by socket name.</param>
    /// <param name="originalName">A texdata name's material as the author named it (vbsp's patch chain undone).</param>
    /// <returns>The water, bound to <paramref name="bsp"/>; or null for a room without water.</returns>
    /// <exception cref="RoomLintException">A declared water socket does not hold the water it declares.</exception>
    /// <exception cref="LinkException">The lumps are not ones vbsp writes (<see cref="Check"/>, <see cref="WaterOverlays"/>), or the collision does not read.</exception>
    public static RoomWater? Build(
        RoomDefinition definition, BspData bsp, IReadOnlyDictionary<string, RoomWaterSocket> declared, Func<string, string> originalName)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(originalName);
        RoomWaterDoor?[] doors = new RoomWaterDoor?[definition.Sockets.Count];
        List<(RoomWaterFluid Fluid, byte[] Blob)>? fluids = null;
        for (int s = 0; s < doors.Length; s++)
        {
            if (declared.TryGetValue(definition.Sockets[s].Name, out RoomWaterSocket? water))
            {
                fluids ??= bsp[BspLump.PhysCollide].Length == 0 ? [] : LevelLinker.ReadRoomFluids(bsp, definition.Name);
                doors[s] = Door(definition, definition.Sockets[s], water, bsp, originalName, fluids);
            }
        }

        return Build(definition.Name, bsp, doors, fluids);
    }

    private static RoomWater? Build(string room, BspData bsp, RoomWaterDoor?[] doors, List<(RoomWaterFluid Fluid, byte[] Blob)>? read)
    {
        if (!HasWater(bsp))
        {
            return null;
        }

        int dataCount = Check(room, bsp);
        List<(RoomWaterFluid Fluid, byte[] Blob)> fluids = read ?? (bsp[BspLump.PhysCollide].Length == 0
            ? []
            : LevelLinker.ReadRoomFluids(bsp, room));
        ReadOnlySpan<DWaterOverlay> overlays = WaterOverlays(room, bsp);

        RoomLinkSolid[][] ledges = new RoomLinkSolid[4][];
        RoomOverlayPose[][] poses = new RoomOverlayPose[4][];
        for (int turn = 0; turn < 4; turn++)
        {
            ledges[turn] = new RoomLinkSolid[fluids.Count];
            for (int f = 0; f < fluids.Count; f++)
            {
                List<byte[]> turned = [];
                foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(fluids[f].Blob)))
                {
                    LevelLinker.RotateLedge(ledge, turn);
                    turned.Add(ledge.Bytes);
                }

                ledges[turn][f] = RoomLinkSolid.Of(fluids[f].Fluid.Contents, turned);
            }

            poses[turn] = new RoomOverlayPose[overlays.Length];
            for (int o = 0; o < overlays.Length; o++)
            {
                poses[turn][o] = RoomOverlays.Turn(PoseOf(overlays[o]), turn);
            }
        }

        return new RoomWater(dataCount, [.. fluids.Select(f => f.Fluid)], ledges, poses, doors, bsp);
    }

    /// <summary>A water overlay record's pose in the room's own frame.</summary>
    public static RoomOverlayPose PoseOf(in DWaterOverlay overlay) => new(
        overlay.Origin,
        new Vec3(overlay.UvPoints[0].Z, overlay.UvPoints[1].Z, overlay.UvPoints[2].Z),
        overlay.BasisNormal);

    /// <summary>
    /// A room's leaf water data count, checked against what vbsp writes:
    /// every leaf and face naming a record names one the room has.
    /// </summary>
    /// <exception cref="LinkException">A leaf or face names a record the room does not have.</exception>
    internal static int Check(string room, BspData bsp)
    {
        int count = BspStructView.Count<DLeafWaterData>(bsp[BspLump.LeafWaterData]);
        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        for (int l = 0; l < leafs.Length; l++)
        {
            if (leafs[l].LeafWaterDataId < -1 || leafs[l].LeafWaterDataId >= count)
            {
                throw new LinkException(
                    $"room {room}'s leaf {l} names water data {leafs[l].LeafWaterDataId}; the room has {count}.");
            }
        }

        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f].SurfaceFogVolumeId < -1 || faces[f].SurfaceFogVolumeId >= count)
            {
                throw new LinkException(
                    $"room {room}'s face {f} names fog volume {faces[f].SurfaceFogVolumeId}; the room has {count}.");
            }
        }

        return count;
    }

    /// <summary>
    /// A room's water overlay records, checked against what vbsp writes:
    /// each id is <see cref="FirstWaterOverlayId"/> plus the record's place
    /// in the lump, which the link rebases.
    /// </summary>
    /// <exception cref="LinkException">An id out of place.</exception>
    internal static ReadOnlySpan<DWaterOverlay> WaterOverlays(string room, BspData bsp)
    {
        ReadOnlySpan<DWaterOverlay> overlays = BspStructView.As<DWaterOverlay>(bsp[BspLump.WaterOverlays]);
        for (int i = 0; i < overlays.Length; i++)
        {
            if (overlays[i].Id != FirstWaterOverlayId + i)
            {
                throw new LinkException(
                    $"room {room}'s water overlay {i} has id {overlays[i].Id}; vbsp numbers a map's water overlays from {FirstWaterOverlayId} in order.");
            }
        }

        return overlays;
    }

    /// <summary>A fluid's convexes turned from turn 0, for a section that stores one turn.</summary>
    private static RoomLinkSolid TurnSolid(RoomLinkSolid solid, int rotation)
    {
        List<byte[]> turned = new(solid.Starts.Length);
        for (int l = 0; l < solid.Starts.Length; l++)
        {
            IvpCompactLedge ledge = new(solid.Ledge(l).ToArray());
            LevelLinker.RotateLedge(ledge, rotation);
            turned.Add(ledge.Bytes);
        }

        return RoomLinkSolid.Of(solid.Contents, turned);
    }

    /// <summary>The pack section holding this water.</summary>
    /// <param name="codec">How to store the payload; none by default, as every link section.</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    internal RoomPackSectionData ToSection(RoomLinkCodec codec = RoomLinkCodec.None)
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(DataCount);
        w.Int(Fluids.Count);
        foreach (RoomWaterFluid fluid in Fluids)
        {
            w.String(fluid.SurfaceProp);
            w.Int(BitConverter.SingleToInt32Bits(fluid.Damping));
            w.Int(fluid.Contents);
            w.Structs<Vec3>([fluid.Normal], counted: false);
            w.Int(BitConverter.SingleToInt32Bits(fluid.Dist));
        }

        w.Int(Doors.Count);
        foreach (RoomWaterDoor? door in Doors)
        {
            w.Byte(door is null ? (byte)0 : (byte)1);
            if (door is not null)
            {
                w.Int(BitConverter.SingleToInt32Bits(door.Level));
                w.Int(door.Record);
                w.Int(door.TopFace);
                w.Int(door.BottomFace);
                w.Int(door.Contents);
                w.Int(door.Fluid);
                w.String(door.Material);
            }
        }

        w.Int(OverlayCount);
        w.Int(_overlays.Length);
        for (int t = 0; t < _overlays.Length; t++)
        {
            foreach (RoomLinkSolid solid in _fluidLedges[t])
            {
                w.Int(solid.Starts.Length);
                w.Int(solid.Ledges.Length);
                w.Raw(solid.Ledges);
            }

            w.Structs<RoomOverlayPose>(_overlays[t], counted: false);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), codec));
    }

    /// <summary>
    /// A room's water from its section, bound to <paramref name="bsp"/>; or
    /// null when the section is absent.
    /// </summary>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP, whose lumps the section must fit.</param>
    /// <returns>The water, or null.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload that decodes to another
    /// length than recorded, or a payload that does not fit the room's
    /// lumps: another water data or water overlay count, a turn count other
    /// than 1 or 4, convexes that do not read, bytes after its end.
    /// </exception>
    internal static RoomWater? Read(ArraySegment<byte>? section, string room, BspData bsp)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        return Read(r, bsp);
    }

    /// <summary>
    /// How many water volumes (water data records) a room's section holds,
    /// read without the room's compile (what <c>ssmap rooms</c> lists); null
    /// when the section is absent or of a revision this build does not read.
    /// </summary>
    /// <param name="section">The section's bytes, or null.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <returns>The count, or null.</returns>
    /// <exception cref="LinkException">The section is cut short, or its count is negative.</exception>
    internal static int? ReadVolumeCount(ArraySegment<byte>? section, string room)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        int count = r.Int();
        return count >= 0 ? count : throw r.Mismatch($"{count} water data records");
    }

    private static RoomWater Read(RoomLinkSections.Reader r, BspData bsp)
    {
        int lump = BspStructView.Count<DLeafWaterData>(bsp[BspLump.LeafWaterData]);
        int dataCount = r.Int();
        if (dataCount != lump)
        {
            throw r.Mismatch($"{dataCount} water data records; the room has {lump}");
        }

        RoomWaterFluid[] fluids = new RoomWaterFluid[r.Count("fluids")];
        for (int f = 0; f < fluids.Length; f++)
        {
            string prop = r.String();
            float damping = BitConverter.Int32BitsToSingle(r.Int());
            int contents = r.Int();
            Vec3 normal = r.Structs<Vec3>("normal", 1, counted: false)[0];
            float dist = BitConverter.Int32BitsToSingle(r.Int());
            fluids[f] = new RoomWaterFluid(prop, damping, contents, normal, dist);
        }

        int faces = BspStructView.Count<DFace>(bsp[BspLump.Faces]);
        RoomWaterDoor?[] doors = new RoomWaterDoor?[r.Count("sockets")];
        for (int s = 0; s < doors.Length; s++)
        {
            if (!r.Flag())
            {
                continue;
            }

            float level = BitConverter.Int32BitsToSingle(r.Int());
            int record = r.Int();
            int top = r.Int();
            int bottom = r.Int();
            int contents = r.Int();
            int fluid = r.Int();
            string material = r.String();
            if ((uint)record >= (uint)lump || top < -1 || top >= faces || bottom < -1 || bottom >= faces
                || fluid < -1 || fluid >= fluids.Length || !float.IsFinite(level))
            {
                throw r.Mismatch($"socket {s}'s water naming what the room does not have");
            }

            doors[s] = new RoomWaterDoor(level, record, top, bottom, contents, fluid, material);
        }

        int overlays = BspStructView.Count<DWaterOverlay>(bsp[BspLump.WaterOverlays]);
        int count = r.Int();
        if (count != overlays)
        {
            throw r.Mismatch($"{count} water overlays; the room has {overlays}");
        }

        int turns = r.Int();
        if (turns is not (1 or 4))
        {
            throw r.Mismatch($"{turns} turns of water; a section holds 1 or 4");
        }

        RoomLinkSolid[][] ledges = new RoomLinkSolid[turns][];
        RoomOverlayPose[][] poses = new RoomOverlayPose[turns][];
        for (int t = 0; t < turns; t++)
        {
            ledges[t] = new RoomLinkSolid[fluids.Length];
            for (int f = 0; f < fluids.Length; f++)
            {
                int ledgeCount = r.Count("fluid convexes");
                ledges[t][f] = r.Ledges(fluids[f].Contents, ledgeCount);
            }

            poses[t] = r.Structs<RoomOverlayPose>("water overlays", count, counted: false);
        }

        r.End();
        return new RoomWater(dataCount, fluids, ledges, poses, doors, bsp);
    }

    /// <summary>
    /// What is wrong with a room's water for the link, as the rooms design's
    /// refusal says it, or null: a water brush of the world that reaches a
    /// socket's plug box (the rooms design, 4.6 and open point O7).
    /// </summary>
    /// <param name="definition">The room: its name, cell and sockets.</param>
    /// <param name="map">The room's map as the loader read it, which knows each brush's contents from its materials.</param>
    /// <param name="declared">
    /// The water sockets the room declares (<see cref="RoomWaterSocket"/>),
    /// by socket name: water may reach those plugs, and is held to the
    /// declarations once compiled (<see cref="Door"/>).
    /// </param>
    /// <returns>The refusal's text, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> A plug is solid, so the room's water stops at it: after
    /// the link the carved doorway is an air gap as deep as two walls, no
    /// surface spans it and no fluid covers it, and the flattened level
    /// (which leaves the plug out) has the same gap. Water that meets a
    /// plug would show that gap in an open doorway, so it is refused
    /// until the socket says what water it carries.
    /// </para>
    /// <para>
    /// <b>How.</b> A water brush (<c>CONTENTS_WATER</c> or <c>CONTENTS_SLIME</c>,
    /// from its materials) of the world, <c>func_detail</c> included, whose
    /// box shares more than an edge with a plug box: overlapping it, or
    /// touching one of its faces over an area. Found on the loaded map,
    /// before any compile time is spent; the materials are what say a brush
    /// is water, so it is a room compile's check and not the split's.
    /// Brush entities (a <c>func_water_analog</c>) are their own models and
    /// the socket furniture rule's.
    /// </para>
    /// </remarks>
    public static string? PlugProblem(RoomDefinition definition, MapFile map, IReadOnlyDictionary<string, RoomWaterSocket> declared)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(declared);
        const int waterContents = (int)(BrushContents.Water | BrushContents.Slime);
        foreach (RoomSocket socket in definition.Sockets)
        {
            if (declared.ContainsKey(socket.Name))
            {
                continue;
            }

            Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);
            foreach (MapBrush brush in map.Brushes)
            {
                if (brush.EntityNumber != 0 || (brush.Contents & waterContents) == 0)
                {
                    continue;
                }

                if (Meets(new Box(brush.Mins, brush.Maxs), plug))
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"room {definition.Name}: water reaches socket \"{socket.Name}\"; water may not touch a door plug.");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// How far a water socket's doorway is sampled from its plug's inner
    /// face, into the room: half a unit, clear of any whole-unit plane.
    /// </summary>
    private const float SampleInset = 0.5f;

    /// <summary>
    /// A declared water socket held to what the room's compile holds against
    /// its plug, and described for the link: the room's world, sampled a
    /// half unit inside the plug's inner face at every half unit of the
    /// door's width and height (and a quarter unit either side of the
    /// level), is water of one record below the level and open air above it
    /// (the water fills the doorway to that height and no higher); the
    /// record's surface is at the level and of the declared material, which
    /// vbsp does not light (the doorway's surface the link adds has no
    /// lightmap). Below a level under the door's top, the room's faces of
    /// that surface at the level, seen from above and from below, are what
    /// the doorway's surface follows (a room whose water shows no surface
    /// there, a nodraw top, gives its doorway none either).
    /// </summary>
    /// <exception cref="RoomLintException">The compile does not hold what the socket declares.</exception>
    private static RoomWaterDoor Door(
        RoomDefinition definition,
        RoomSocket socket,
        RoomWaterSocket declared,
        BspData bsp,
        Func<string, string> originalName,
        List<(RoomWaterFluid Fluid, byte[] Blob)> fluids)
    {
        string room = definition.Name;
        Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);
        int axis = socket.Facing is RoomFacing.PositiveX or RoomFacing.NegativeX ? 0 : 1;
        int across = 1 - axis;
        bool positive = socket.Facing is RoomFacing.PositiveX or RoomFacing.PositiveY;
        float inside = positive ? Component(plug.Mins, axis) - SampleInset : Component(plug.Maxs, axis) + SampleInset;
        float level = declared.Level;
        string where = string.Create(CultureInfo.InvariantCulture, $"room {room}: socket \"{socket.Name}\" declares water at {level:0.###}");

        List<float> heights = [];
        for (float z = plug.Mins.Z + 0.5f; z < plug.Maxs.Z; z += 0.5f)
        {
            heights.Add(z);
        }

        foreach (float z in (ReadOnlySpan<float>)[level - 0.25f, level + 0.25f])
        {
            if (z > plug.Mins.Z && z < plug.Maxs.Z)
            {
                heights.Add(z);
            }
        }

        int record = -1;
        int contents = 0;
        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        for (float u = Component(plug.Mins, across) + 0.5f; u < Component(plug.Maxs, across); u += 0.5f)
        {
            foreach (float z in heights)
            {
                if (z == level)
                {
                    continue;
                }

                Vec3 point = axis == 0 ? new Vec3(inside, u, z) : new Vec3(u, inside, z);
                DLeaf leaf = leafs[LeafAt(bsp, point)];
                bool water = (leaf.Contents & (int)(BrushContents.Water | BrushContents.Slime)) != 0 && leaf.LeafWaterDataId >= 0;
                bool solid = (leaf.Contents & (int)BrushContents.Solid) != 0;
                if (water != (z < level) || solid)
                {
                    throw new RoomLintException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{where}, but ({point.X:0.###} {point.Y:0.###} {point.Z:0.###}) against its plug holds {(solid ? "solid" : water ? "water" : "air")};")
                        + " the water must fill the doorway to that height and no higher.");
                }

                if (!water)
                {
                    continue;
                }

                if (record >= 0 && leaf.LeafWaterDataId != record)
                {
                    throw new RoomLintException(
                        $"room {room}: socket \"{socket.Name}\" meets two bodies of water; a water socket's doorway meets one.");
                }

                record = leaf.LeafWaterDataId;
                contents = leaf.Contents;
            }
        }

        // A doorway wholly under water (the level at or above its top) has
        // no surface of its own: the room's water may stand higher, or fill
        // the room to its ceiling, where vbsp records no surface at all (its
        // largest coordinate).
        DLeafWaterData data = BspStructView.As<DLeafWaterData>(bsp[BspLump.LeafWaterData])[record];
        bool flooded = level >= plug.Maxs.Z;
        if (flooded ? data.SurfaceZ < plug.Maxs.Z : data.SurfaceZ != level)
        {
            throw new RoomLintException(string.Create(
                CultureInfo.InvariantCulture,
                $"{where}, but the water against its plug has its surface at {data.SurfaceZ:0.###}."));
        }

        string material = originalName(TexInfoMaterial(bsp, data.SurfaceTexInfoId));
        if (!string.Equals(material, declared.Material, StringComparison.OrdinalIgnoreCase))
        {
            throw new RoomLintException(
                $"room {room}: socket \"{socket.Name}\" declares {declared.Material}, but the water against its plug is {material}.");
        }

        TexInfo surface = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo])[data.SurfaceTexInfoId];
        if ((surface.Flags & (int)SurfaceFlags.NoLight) == 0)
        {
            throw new RoomLintException(
                $"room {room}: socket \"{socket.Name}\"'s water {material} is lit (%compileKeepLight);"
                + " the surface the link adds in a doorway has no lightmap, so a water socket's water is unlit.");
        }

        (int top, int bottom) = SurfaceFaces(bsp, record, level);

        int fluid = -1;
        for (int f = 0; f < fluids.Count && fluid < 0; f++)
        {
            if (Meets(FluidBox(fluids[f].Blob), plug))
            {
                fluid = f;
            }
        }

        return new RoomWaterDoor(level, record, level < plug.Maxs.Z ? top : -1, level < plug.Maxs.Z ? bottom : -1, contents, fluid, declared.Material);
    }

    /// <summary>
    /// The first face (in face order) of a water record's surface at a
    /// height seen from above, and the first seen from below (its
    /// underside, drawn with the <c>$bottommaterial</c>); -1 for none.
    /// </summary>
    /// <remarks>
    /// A face is of the surface when it names the record's fog volume and
    /// every vertex is at the height; which way it is seen from is the way
    /// its winding faces, which is what the engine draws it by.
    /// </remarks>
    internal static (int Top, int Bottom) SurfaceFaces(BspData bsp, int record, float level)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        int top = -1, bottom = -1;
        for (int f = 0; f < faces.Length && (top < 0 || bottom < 0); f++)
        {
            if (faces[f].SurfaceFogVolumeId != record)
            {
                continue;
            }

            Vec3[] corners = FaceCorners(bsp, faces[f]);
            if (corners.Length < 3 || corners.Any(c => c.Z != level))
            {
                continue;
            }

            float up = Vec3.Cross(corners[1] - corners[0], corners[2] - corners[0]).Z;
            if (up < 0 && top < 0)
            {
                top = f;
            }
            else if (up > 0 && bottom < 0)
            {
                bottom = f;
            }
        }

        return (top, bottom);
    }

    /// <summary>A face's vertices in its winding order.</summary>
    internal static Vec3[] FaceCorners(BspData bsp, DFace face)
    {
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<Vec3> vertices = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        Vec3[] corners = new Vec3[face.NumEdges];
        for (int e = 0; e < face.NumEdges; e++)
        {
            int edge = surfEdges[face.FirstEdge + e];
            corners[e] = vertices[edge >= 0 ? edges[edge].V[0] : edges[-edge].V[1]];
        }

        return corners;
    }

    /// <summary>The leaf of a map's world that holds a point: the tree walked from model 0's head.</summary>
    internal static int LeafAt(BspData bsp, Vec3 point)
    {
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        int node = BspStructView.As<DModel>(bsp[BspLump.Models])[0].HeadNode;
        while (node >= 0)
        {
            DNode n = nodes[node];
            DPlane plane = planes[n.PlaneNum];
            node = Vec3.Dot(plane.Normal, point) - plane.Dist >= 0 ? n.Children[0] : n.Children[1];
        }

        return -1 - node;
    }

    /// <summary>A texinfo's material name as its texdata string holds it.</summary>
    internal static string TexInfoMaterial(BspData bsp, int texInfo)
    {
        TexInfo info = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo])[texInfo];
        DTexData data = BspStructView.As<DTexData>(bsp[BspLump.TexData])[info.TexData];
        int at = BspStructView.As<int>(bsp[BspLump.TexDataStringTable])[data.NameStringTableId];
        ReadOnlySpan<byte> strings = bsp[BspLump.TexDataStringData].Data.Span[at..];
        int end = strings.IndexOf((byte)0);
        return System.Text.Encoding.Latin1.GetString(end < 0 ? strings : strings[..end]);
    }

    /// <summary>The box around a fluid's convexes' points, in map units, room-local.</summary>
    private static Box FluidBox(byte[] blob)
    {
        Vec3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(blob)))
        {
            for (int p = 0; p < ledge.PointCount; p++)
            {
                (float x, float y, float z) = IvpCollideQueries.HlPoint(ledge, p);
                min = new Vec3(Math.Min(min.X, x), Math.Min(min.Y, y), Math.Min(min.Z, z));
                max = new Vec3(Math.Max(max.X, x), Math.Max(max.Y, y), Math.Max(max.Z, z));
            }
        }

        return new Box(min, max);
    }

    /// <summary>
    /// Whether two boxes share more than an edge: they overlap or touch on
    /// every axis, and on at most one axis the shared extent is empty.
    /// </summary>
    internal static bool Meets(Box a, Box b)
    {
        int flat = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            float low = Math.Max(Component(a.Mins, axis), Component(b.Mins, axis));
            float high = Math.Min(Component(a.Maxs, axis), Component(b.Maxs, axis));
            if (high < low - RoomLinter.CellEpsilon)
            {
                return false;
            }

            if (high <= low + RoomLinter.CellEpsilon)
            {
                flat++;
            }
        }

        return flat <= 1;
    }

    private static float Component(Vec3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
