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
using SourceSharp.MapTools.Materials;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>A point a player stands at, room-local: its position (the feet) and its facing.</summary>
/// <param name="Origin">Where it stands, room-local.</param>
/// <param name="Yaw">Its facing in degrees, 0 to 360; 0 for a point authored without angles.</param>
internal readonly record struct TransitPoint(Vec3 Origin, float Yaw);

/// <summary>
/// What a room brings to its level's transitions and spawn (the rooms
/// design, section 11): its role, its transition volume, the hallway
/// trigger the stock fallback folds into it, its arrival point and its
/// spawn points, all room-local.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the room's VMF, at pack time.</b> The points of interest
/// never reach the compile (<see cref="RoomPois.Extract"/>), and which
/// trigger holds which is a question about the authored brushes, so the
/// data is made from the room's VMF by <see cref="FromVmf"/>: at
/// <c>ssmap room</c>, which stores it in the room's <c>TRAN</c> section for
/// the link, and at <c>ssmap link --flatten</c>, which reads the same
/// library. One function on one document gives both maps the same numbers,
/// which is what lets them write the same text.
/// </para>
/// <para>
/// <b>Which rooms have it.</b> An up or down room (<c>room_role</c>), and
/// an ordinary room with <c>spawn</c> points, which a level with
/// <c>up: none</c> may start the player in (open point O21). Any other room
/// has none and its pack entry is as it was, so a library without roles or
/// spawn points packs to the same bytes.
/// </para>
/// <para>
/// <b>Refused</b> (the rooms design, 11.3, with the 15.4 messages): a role
/// room without exactly one <c>trigger_room_transition</c> named
/// <c>cxry_transition</c>, without exactly one arrival point, or with
/// nothing firing <c>Transition</c> at its volume; and, with messages of
/// their own, a transition volume in a room without a role, one named
/// otherwise, and one with no brushes. The arrival's clearance needs the
/// compile and is checked by <see cref="CheckClearance"/>.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>TRAN</c>) has the link
/// sections' framing (<see cref="RoomLinkSections"/>: a codec byte, the
/// decoded length, an <c>int32</c> revision). After it, big-endian
/// <c>int32</c>s and little-endian floats: the role byte; the volume's
/// Hammer id (-1 for none) and centre (three floats); the fold trigger's
/// Hammer id (-1 for none) and centre; an arrival byte (0 or 1) and the
/// arrival's origin and yaw; the spawn count and each spawn's origin and
/// yaw. Stored once, not per turn: a handful of points, which the link turns
/// as it turns every point entity (<see cref="RoomTransform.Apply"/>, yaw
/// plus 90 per turn), in less time than reading four copies would take.
/// The tag is one an older build skips; such a build refuses nothing it
/// should link, since it never applies the level rule, and a room packed
/// before this section existed has no transition data, which the link
/// refuses for a role room by name.
/// </para>
/// </remarks>
internal sealed class RoomTransit
{
    /// <summary>The tag of a room's transition section.</summary>
    public const string SectionTag = "TRAN";

    /// <summary>The revision this build writes and reads.</summary>
    public const int Revision = 1;

    /// <summary>The compile-only class of a transition volume.</summary>
    public const string VolumeClass = RoomLinkerNames.TransitionClass;

    /// <summary>The volume's authored name.</summary>
    public const string VolumeName = RoomNameGrammar.Placeholder + RoomLinkerNames.Transition;

    /// <summary>The type of a spawn point.</summary>
    public const string SpawnType = "spawn";

    /// <summary>The input the author fires at the volume.</summary>
    public const string Input = LevelTransition.TransitionInput;

    private readonly BspData? _bsp;

    internal RoomTransit(
        RoomRole role, int volumeId, Vec3 volumeCentre, int foldId, Vec3 foldCentre, TransitPoint? arrival, IReadOnlyList<TransitPoint> spawns,
        BspData? bsp = null)
    {
        Role = role;
        VolumeId = volumeId;
        VolumeCentre = volumeCentre;
        FoldId = foldId;
        FoldCentre = foldCentre;
        Arrival = arrival;
        Spawns = spawns;
        _bsp = bsp;
    }

    /// <summary>The room's role.</summary>
    public RoomRole Role { get; }

    /// <summary>The transition volume's Hammer id, or -1 for a room without one.</summary>
    public int VolumeId { get; }

    /// <summary>The centre of the volume's brushes' box, room-local.</summary>
    public Vec3 VolumeCentre { get; }

    /// <summary>
    /// The Hammer id of the hallway <c>trigger_once</c> the stock fallback
    /// turns into the <c>trigger_changelevel</c> in the volume's place, or -1
    /// when the room's wiring does not allow the fold.
    /// </summary>
    public int FoldId { get; }

    /// <summary>The centre of the fold trigger's box, room-local: where the down room's landmark stands when it folds.</summary>
    public Vec3 FoldCentre { get; }

    /// <summary>The arrival point, or null for a room without a role.</summary>
    public TransitPoint? Arrival { get; }

    /// <summary>The <c>spawn</c> points, in document order.</summary>
    public IReadOnlyList<TransitPoint> Spawns { get; }

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The same data bound to a compile.</summary>
    internal RoomTransit For(BspData bsp) => new(Role, VolumeId, VolumeCentre, FoldId, FoldCentre, Arrival, Spawns, bsp);

    /// <summary>The direction a role room's transition leads.</summary>
    public TransitionDirection Direction => Role == RoomRole.Up ? TransitionDirection.Up : TransitionDirection.Down;

    /// <summary>
    /// A room's transition data from its VMF, or null for a room that has
    /// neither a role nor spawn points.
    /// </summary>
    /// <param name="definition">The room.</param>
    /// <param name="role">Its role, from its <c>info_room</c>.</param>
    /// <param name="room">Its VMF, room-local, points of interest included.</param>
    /// <returns>The data, bound to no compile.</returns>
    /// <exception cref="RoomLintException">A rule of 11.3 is broken, or a point of interest is malformed.</exception>
    public static RoomTransit? FromVmf(RoomDefinition definition, RoomRole role, VmfDocument room)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(room);
        string name = definition.Name;
        IReadOnlyList<AuthoredPoi> pois = RoomPois.Extract(room).Pois;
        List<TransitPoint> spawns = [.. pois.Where(p => p.Type == SpawnType).Select(p => new TransitPoint(p.Origin, p.Yaw))];
        List<VmfChunk> entities = [.. room.GetChunks(MapFileLoader.EntityChunk).Where(e => !RoomPois.IsPoi(e))];
        List<VmfChunk> volumes = [.. entities.Where(e => string.Equals(e.GetValue("classname"), VolumeClass, StringComparison.Ordinal))];

        if (role == RoomRole.None)
        {
            if (volumes.Count > 0)
            {
                throw new RoomLintException(
                    $"room {name}: {VolumeClass} {Id(volumes[0])} is in a room without a {RoomPois.RoleKey}; only an up or down room has a transition volume.");
            }

            return spawns.Count == 0 ? null : new RoomTransit(role, -1, default, -1, default, null, spawns);
        }

        string roleName = role == RoomRole.Up ? "an up" : "a down";
        foreach (VmfChunk volume in volumes)
        {
            if (!string.Equals(volume.GetValue("targetname"), VolumeName, StringComparison.Ordinal))
            {
                throw new RoomLintException(
                    $"room {name}: {VolumeClass} {Id(volume)} is named \"{volume.GetValue("targetname")}\"; the transition volume is named {VolumeName}.");
            }
        }

        if (volumes.Count != 1)
        {
            throw new RoomLintException(string.Create(CultureInfo.InvariantCulture,
                $"room {name}: {roleName} room needs exactly one {VolumeClass} named {VolumeName}; it has {volumes.Count}."));
        }

        List<AuthoredPoi> arrivals = [.. pois.Where(p => p.Type == RoomPois.ArrivalType)];
        if (arrivals.Count != 1)
        {
            throw new RoomLintException(string.Create(CultureInfo.InvariantCulture,
                $"room {name}: {roleName} room needs exactly one arrival point; it has {arrivals.Count}."));
        }

        VmfChunk transition = volumes[0];
        Box volumeBox = BoxOf(transition)
            ?? throw new RoomLintException($"room {name}: {VolumeClass} {Id(transition)} has no brushes; the transition volume is a brush entity.");

        List<(VmfChunk Entity, RoomOutput Output)> firing = [];
        foreach (VmfChunk entity in entities)
        {
            foreach (RoomOutput output in Outputs(entity))
            {
                if (FiresTransition(output))
                {
                    firing.Add((entity, output));
                }
            }
        }

        if (firing.Count == 0)
        {
            throw new RoomLintException($"room {name}: nothing fires {Input} at {VolumeName}.");
        }

        (int foldId, Vec3 foldCentre) = Fold(firing, volumeBox);
        return new RoomTransit(
            role, IdOf(transition), Centre(volumeBox), foldId, foldCentre, new TransitPoint(arrivals[0].Origin, arrivals[0].Yaw), spawns);
    }

    /// <summary>
    /// The arrival point has room for a standing player: the player's box
    /// (<see cref="PlayerHull"/>, 32 × 32 × 72 from the feet) stands inside
    /// the room's cell and crosses no brush of the room's world that blocks
    /// a player.
    /// </summary>
    /// <param name="definition">The room.</param>
    /// <param name="transit">The room's transition data.</param>
    /// <param name="room">The room's compile, with its brush models (<see cref="RoomObject.BrushModelsOfCompile"/>).</param>
    /// <exception cref="RoomLintException">The box is outside the cell or crosses a blocking brush, with the 15.4 message.</exception>
    /// <remarks>
    /// <para>
    /// The brushes are the compile's, the planes vbsp built them from (so a
    /// wedge is a wedge, not its box), and only the world's: a brush entity
    /// (a door, the transition volume itself) moves or does not block. A
    /// brush blocks when its contents are one a player collides with (solid,
    /// window, grate, moveable, player clip, monster). The box touches a
    /// brush without crossing it when it stops at one of its planes, so a
    /// player standing on a floor is clear; the test allows a hundredth of a
    /// unit, the precision a point is authored to.
    /// </para>
    /// <para>
    /// The navigation build checks the same point against its own grid when
    /// the library builds navigation (and requires a floor under it); this
    /// check runs for every library, since the stock fallback's
    /// <c>info_player_start</c> and landmark stand there whether or not
    /// navigation is built.
    /// </para>
    /// </remarks>
    public static void CheckClearance(RoomDefinition definition, RoomTransit transit, RoomObject room)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(transit);
        ArgumentNullException.ThrowIfNull(room);
        if (transit.Arrival is not { } arrival)
        {
            return;
        }

        const float Slack = 0.01f;
        float half = PlayerHull.Width / 2;
        Vec3 at = arrival.Origin;
        Box hull = new(new Vec3(at.X - half, at.Y - half, at.Z), new Vec3(at.X + half, at.Y + half, at.Z + PlayerHull.Height));
        float cell = definition.CellSize;
        bool inCell = hull.Mins.X >= -Slack && hull.Mins.Y >= -Slack && hull.Mins.Z >= -Slack
            && hull.Maxs.X <= cell + Slack && hull.Maxs.Y <= cell + Slack && hull.Maxs.Z <= definition.Height + Slack;
        if (!inCell || Blocked(room, hull, Slack))
        {
            throw new RoomLintException(string.Create(CultureInfo.InvariantCulture,
                $"room {definition.Name}: the arrival point at ({Num(at.X)}, {Num(at.Y)}, {Num(at.Z)}) has no room for a standing player"
                + $" ({Num(PlayerHull.Width)} x {Num(PlayerHull.Width)} x {Num(PlayerHull.Height)})."));
        }
    }

    /// <summary>The pack section holding this data.</summary>
    internal RoomPackSectionData ToSection()
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Byte((byte)Role);
        w.Int(VolumeId);
        w.Structs<Vec3>([VolumeCentre], counted: false);
        w.Int(FoldId);
        w.Structs<Vec3>([FoldCentre], counted: false);
        w.Byte(Arrival is null ? (byte)0 : (byte)1);
        Point(w, Arrival ?? default);
        w.Int(Spawns.Count);
        foreach (TransitPoint spawn in Spawns)
        {
            Point(w, spawn);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));

        static void Point(RoomLinkSections.Writer w, TransitPoint point)
        {
            w.Structs<Vec3>([point.Origin], counted: false);
            w.Structs<float>([point.Yaw], counted: false);
        }
    }

    /// <summary>A room's transition data from its section, bound to its compile; null when absent or of another revision.</summary>
    /// <param name="section">The section's bytes, or null.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compile.</param>
    /// <returns>The data, or null.</returns>
    /// <exception cref="LinkException">The section is damaged: a role out of range, a flag byte other than 0 or 1, a count its bytes cannot hold, bytes after its end.</exception>
    internal static RoomTransit? Read(ArraySegment<byte>? section, string room, BspData bsp)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        RoomRole role = (RoomRole)r.Small((int)RoomRole.Down, "a role of");
        int volumeId = r.Int();
        Vec3 volumeCentre = r.Structs<Vec3>("centre", 1, counted: false)[0];
        int foldId = r.Int();
        Vec3 foldCentre = r.Structs<Vec3>("centre", 1, counted: false)[0];
        bool hasArrival = r.Flag();
        TransitPoint arrival = Point(r);
        int count = r.Count("spawn points");
        List<TransitPoint> spawns = new(count);
        for (int i = 0; i < count; i++)
        {
            spawns.Add(Point(r));
        }

        r.End();
        return new RoomTransit(role, volumeId, volumeCentre, foldId, foldCentre, hasArrival ? arrival : null, spawns, bsp);

        static TransitPoint Point(RoomLinkSections.Reader r) =>
            new(r.Structs<Vec3>("point", 1, counted: false)[0], r.Structs<float>("yaw", 1, counted: false)[0]);
    }

    /// <summary>Whether an output fires the transition input at the room's volume, by its authored name.</summary>
    internal static bool FiresTransition(RoomOutput output) =>
        string.Equals(output.Target, VolumeName, StringComparison.Ordinal)
        && string.Equals(output.Input, Input, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The hallway fold (the rooms design, 11.4): the one <c>trigger_once</c>
    /// whose only output is the transition, with no filter, whose box holds
    /// the volume's, when it is also the only thing that fires the transition.
    /// </summary>
    /// <remarks>
    /// The last condition is this build's: the fold drops the volume, and an
    /// output of another entity still naming it (a button beside the
    /// hallway) would then fire at nothing. So the fold is taken only when
    /// the trigger is the one caller, and otherwise both stay, the trigger
    /// firing <c>ChangeLevel</c> at the changelevel like any caller.
    /// </remarks>
    private static (int Id, Vec3 Centre) Fold(List<(VmfChunk Entity, RoomOutput Output)> firing, Box volume)
    {
        if (firing is not [(VmfChunk trigger, _)]
            || !string.Equals(trigger.GetValue("classname"), "trigger_once", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(trigger.GetValue("filtername"))
            || Outputs(trigger).Count() != 1
            || BoxOf(trigger) is not { } box
            || !Contains(box, volume))
        {
            return (-1, default);
        }

        return (IdOf(trigger), Centre(box));
    }

    private static IEnumerable<RoomOutput> Outputs(VmfChunk entity)
    {
        foreach (VmfChunk connections in entity.GetChunks(MapFileLoader.ConnectionsChunk))
        {
            foreach (VmfKey key in connections.Keys)
            {
                if (RoomOutput.TryParse(key.Value, out RoomOutput output))
                {
                    yield return output;
                }
            }
        }
    }

    /// <summary>The box of an entity's brushes, room-local, or null when it has none.</summary>
    internal static Box? BoxOf(VmfChunk entity)
    {
        Box? box = null;
        foreach (VmfChunk solid in entity.GetChunks(MapFileLoader.SolidChunk))
        {
            Box b = VmfPlacement.Bounds(solid);
            box = box is { } sofar
                ? new Box(
                    new Vec3(Math.Min(sofar.Mins.X, b.Mins.X), Math.Min(sofar.Mins.Y, b.Mins.Y), Math.Min(sofar.Mins.Z, b.Mins.Z)),
                    new Vec3(Math.Max(sofar.Maxs.X, b.Maxs.X), Math.Max(sofar.Maxs.Y, b.Maxs.Y), Math.Max(sofar.Maxs.Z, b.Maxs.Z)))
                : b;
        }

        return box;
    }

    /// <summary>The centre of a box, as the link and the flatten both compute it.</summary>
    internal static Vec3 Centre(Box box) => new(
        (box.Mins.X + box.Maxs.X) / 2, (box.Mins.Y + box.Maxs.Y) / 2, (box.Mins.Z + box.Maxs.Z) / 2);

    private static bool Contains(Box outer, Box inner) =>
        outer.Mins.X <= inner.Mins.X && outer.Mins.Y <= inner.Mins.Y && outer.Mins.Z <= inner.Mins.Z
        && outer.Maxs.X >= inner.Maxs.X && outer.Maxs.Y >= inner.Maxs.Y && outer.Maxs.Z >= inner.Maxs.Z;

    private static int IdOf(VmfChunk entity) =>
        int.TryParse(entity.GetValue("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1;

    private static string Id(VmfChunk entity) => entity.GetValue("id") ?? "?";

    private static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a box crosses a player-blocking brush of the world: for every
    /// plane of the brush, some corner of the box is behind it by more than
    /// the slack (the separating-plane test on a convex brush, whose planes
    /// include its box's). A brush entity's brushes (the runs its model owns,
    /// <see cref="RoomBrushModels"/>) are not the world's and are skipped.
    /// </summary>
    private static bool Blocked(RoomObject room, Box hull, float slack)
    {
        const int Blocking = (int)(BrushContents.Solid | BrushContents.Window | BrushContents.Grate
            | BrushContents.Moveable | BrushContents.PlayerClip | BrushContents.Monster);
        BspData bsp = room.Bsp;
        ReadOnlySpan<DBrush> brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]);
        ReadOnlySpan<DBrushSide> sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        bool[] entityBrush = new bool[brushes.Length];
        foreach (RoomBrushModel model in room.BrushModelsOfCompile?.Models ?? [])
        {
            for (int b = model.Brushes.First; b < model.Brushes.First + model.Brushes.Count && b < entityBrush.Length; b++)
            {
                entityBrush[b] = true;
            }
        }

        for (int b = 0; b < brushes.Length; b++)
        {
            DBrush brush = brushes[b];
            if (entityBrush[b] || (brush.Contents & Blocking) == 0 || brush.NumSides == 0)
            {
                continue;
            }

            bool crosses = true;
            for (int s = brush.FirstSide; s < brush.FirstSide + brush.NumSides && crosses; s++)
            {
                DPlane plane = planes[sides[s].PlaneNum];
                float least = float.MaxValue;
                for (int c = 0; c < 8; c++)
                {
                    Vec3 corner = new(
                        (c & 1) == 0 ? hull.Mins.X : hull.Maxs.X,
                        (c & 2) == 0 ? hull.Mins.Y : hull.Maxs.Y,
                        (c & 4) == 0 ? hull.Mins.Z : hull.Maxs.Z);
                    least = Math.Min(least, Vec3.Dot(plane.Normal, corner) - plane.Dist);
                }

                crosses = least < -slack;
            }

            if (crosses)
            {
                return true;
            }
        }

        return false;
    }
}
