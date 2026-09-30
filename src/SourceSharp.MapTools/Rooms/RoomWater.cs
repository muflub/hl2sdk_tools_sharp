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
/// float bits, contents, surface normal and distance), its water overlay
/// count, then the turn count, 1 or 4, and per turn every fluid's convexes
/// and every water overlay's pose. The tag is one an older build skips, and
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

    private RoomWater(int dataCount, RoomWaterFluid[] fluids, RoomLinkSolid[][] fluidLedges, RoomOverlayPose[][] overlays, BspData? bsp)
    {
        DataCount = dataCount;
        Fluids = fluids;
        _fluidLedges = fluidLedges;
        _overlays = overlays;
        _bsp = bsp;
    }

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
    internal RoomWater WithTurnZeroOnly() => new(DataCount, [.. Fluids], [_fluidLedges[0]], [_overlays[0]], _bsp);

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
    /// fluids read from its collision, and the convexes and water overlays
    /// turned four ways.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The water, bound to <paramref name="bsp"/>; or null.</returns>
    /// <exception cref="LinkException">The lumps are not ones vbsp writes (<see cref="Check"/>, <see cref="WaterOverlays"/>), or the collision does not read.</exception>
    public static RoomWater? Build(string room, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bsp);
        if (!HasWater(bsp))
        {
            return null;
        }

        int dataCount = Check(room, bsp);
        List<(RoomWaterFluid Fluid, byte[] Blob)> fluids = bsp[BspLump.PhysCollide].Length == 0
            ? []
            : LevelLinker.ReadRoomFluids(bsp, room);
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

        return new RoomWater(dataCount, [.. fluids.Select(f => f.Fluid)], ledges, poses, bsp);
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
        return new RoomWater(dataCount, fluids, ledges, poses, bsp);
    }

    /// <summary>
    /// What is wrong with a room's water for the link, as the rooms design's
    /// refusal says it, or null: a water brush of the world that reaches a
    /// socket's plug box (the rooms design, 4.6 and open point O7).
    /// </summary>
    /// <param name="definition">The room: its name, cell and sockets.</param>
    /// <param name="map">The room's map as the loader read it, which knows each brush's contents from its materials.</param>
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
    public static string? PlugProblem(RoomDefinition definition, MapFile map)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(map);
        const int waterContents = (int)(BrushContents.Water | BrushContents.Slime);
        foreach (RoomSocket socket in definition.Sockets)
        {
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
