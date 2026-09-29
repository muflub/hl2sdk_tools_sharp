//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Globalization;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Diagnostics;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Where one static prop stands for one quarter turn of its room: its
/// origin, angles and lighting origin turned, the placement's translation
/// not yet added.
/// </summary>
/// <param name="Origin">The prop's origin, turned about the room's own origin.</param>
/// <param name="Angles">Its pitch, yaw and roll, the yaw turned by the quarter turns.</param>
/// <param name="LightingOrigin">
/// Its lighting origin turned like the origin when the prop has one
/// (<see cref="StaticPropFlags.UseLightingOrigin"/>); otherwise the record's
/// own value, which is not a position and is never moved.
/// </param>
/// <param name="Bounds">
/// Its hull's box at the room-local pose, turned: what the link holds
/// against the cell and the jointed doorways to know whether the prop's
/// leaves are its room's own (<see cref="LevelLinker.WritePropsAsync"/>).
/// </param>
internal readonly record struct RoomPropPose(Vec3 Origin, Vec3 Angles, Vec3 LightingOrigin, Box Bounds);

/// <summary>
/// What the link needs to know of one static prop that its compiled record
/// does not hold: the keys vbsp read and dropped with the entity.
/// </summary>
/// <param name="Id">The <c>prop_static</c>'s Hammer id, for messages; -1 when it had none.</param>
/// <param name="Needs">Its <c>room_needs</c> conditions in the room's authored frame; empty when it has none.</param>
/// <param name="Socket">The index of the socket its <c>room_socket</c> names, or -1 when it is not socket furniture.</param>
/// <param name="Priority">Its <c>socket_priority</c>, 0 when it has none; read only for socket furniture.</param>
internal sealed record RoomProp(int Id, ImmutableArray<RoomNeed> Needs, int Socket, int Priority);

/// <summary>
/// One <c>prop_static</c> of a room as the map loader read it, before vbsp
/// turned it into a record and dropped the entity: what the room compile
/// matches each compiled record back to.
/// </summary>
/// <param name="Id">The entity's Hammer id, or -1.</param>
/// <param name="Build">Its keys as the static prop emitter reads them.</param>
/// <param name="Needs">Its <c>room_needs</c> value, or null.</param>
/// <param name="Socket">Its <c>room_socket</c> value, or null.</param>
/// <param name="Priority">Its <c>socket_priority</c> value, or null.</param>
internal sealed record RoomPropSource(int Id, StaticPropBuild Build, string? Needs, string? Socket, string? Priority)
{
    /// <summary>
    /// Whether the prop asks for texel lighting (<c>generatelightmaps</c>):
    /// the emitter leaves <see cref="StaticPropFlags.NoPerTexelLighting"/>
    /// off exactly then.
    /// </summary>
    public bool Texel => (Build.Flags & StaticPropFlags.NoPerTexelLighting) == 0;
}

/// <summary>
/// A room's static props as the link carries them: the model hulls, the
/// keys vbsp consumed, and every prop's pose at each quarter turn. What
/// <c>ssmap room</c> works out once per room so that a level's static props
/// are placed, filtered and given their leaves without the game's files.
/// </summary>
/// <remarks>
/// <para>
/// <b>What vbsp leaves, and what it does not.</b> A room's compile turns
/// every <c>prop_static</c> into a record of the <c>sprp</c> game lump
/// (<see cref="StaticPropEmitter"/>), with its model in the lump's
/// dictionary and its leaves in the lump's leaf list, and drops the entity.
/// The records hold everything the linked lump needs except three things:
/// where the prop stands once its room is turned and moved (a quarter turn
/// of the origin and the lighting origin, and 90 degrees a turn added to
/// the yaw, all exact), which leaves of the <em>linked</em> tree its hull
/// touches (which needs the model's hull, and the model is a game file
/// the link does not read, decision D1), and the keys the rooms feature
/// gives a prop and vbsp ignores: <c>room_needs</c> (the (c) mechanism of
/// the naming design) and <c>room_socket</c> with <c>socket_priority</c>
/// (socket furniture, O5). This holds those three, next to the room's own
/// compiled lump, which stays in the room's container byte for byte.
/// </para>
/// <para>
/// <b>Hulls, per model, in model space.</b> Each dictionary entry's hull is
/// the model's meshes, one point set per mesh, exactly the positions vbsp's
/// hull build read (<see cref="StaticPropEmitter.LoadMeshesAsync"/>), with
/// repeated points left out. The link rebuilds the managed hull from them
/// (<see cref="ManagedStaticPropCollision"/>, the default cooker's) and
/// walks the linked tree with the prop's linked pose
/// (<see cref="StaticPropLeaves"/>) wherever the linked tree differs from
/// the room's own, so a prop's leaf list is recomputed against the tree it
/// will be drawn from: the doorway leaves the plug carve makes, and for
/// socket furniture the neighbour's (a prop clear of both keeps its room's
/// own list, rebased, which is what the walk would find). A hull is the
/// same at every turn (the pose turns, not the model), so it is stored
/// once; its box at the prop's pose is stored with each turn's pose.
/// </para>
/// <para>
/// <b>Poses, per turn.</b> The poses are stored for all four quarter turns
/// (the rooms design's pack rule, 1.1: data the link would otherwise turn
/// element by element is turned at pack time), so the link only adds the
/// placement's translation. The section records how many turns it holds,
/// 1 or 4, as every per-turn section of that design does; a pack with one
/// holds turn 0 and the link turns it, to the same bytes
/// (<see cref="Poses"/>), so the choice can change on measurement alone.
/// Measured on a 16 x 16 grid of hubs with four props each, the two link in
/// the same time within the run-to-run noise (a turn is three negations
/// and a yaw addition a prop, next to reading the poses at all), so the
/// writer keeps the design's default of four; a pose is 60 bytes.
/// </para>
/// <para>
/// <b>Refused at pack time.</b> A prop whose hull reaches outside its cell
/// (open point O6: its leaves and its lighting would belong to a room the
/// pack does not know), unless it is socket furniture whose part outside
/// the cell lies in the doorway beyond its socket; a prop that asks for
/// texel lighting (open point O13: the port's vrad does not bake it); and a
/// <c>room_socket</c> that names no socket of the room or a
/// <c>socket_priority</c> that is not a whole number.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>PROP</c>) follows the
/// link sections' framing (<see cref="RoomLinkSections"/>): a codec byte,
/// the payload's decoded length (<c>int64</c>, big-endian), and a payload
/// that starts with an <c>int32</c> revision (<see cref="Revision"/>). After
/// the revision, big-endian <c>int32</c>s except where said: the dictionary
/// count (the room's lump's), and per entry its mesh count and per mesh a
/// point count and the points as little-endian floats, three each; the prop
/// count (the lump's), and per prop its Hammer id, its socket (-1 for none),
/// its priority, its condition count and per condition a direction byte and
/// a flags byte (1 joined, 2 negated); then the turn count, 1 or 4, and per
/// turn per prop fifteen little-endian floats: origin, angles, lighting
/// origin, and the hull's box (mins, maxs).
/// A section of a revision this build does not read is absent (and a room
/// with props but no section is refused at link, naming the room); a
/// section that does not fit the room's lump is refused as damaged.
/// </para>
/// <para>
/// <b>Binding.</b> It describes one compile's lump: read from a pack or
/// built by the room compile, it remembers the BSP, and the link uses it only
/// for that very BSP (<see cref="IsFor"/>), as the other stored work is used.
/// </para>
/// </remarks>
internal sealed class RoomStaticProps
{
    /// <summary>The tag of a room's static prop section.</summary>
    public const string SectionTag = "PROP";

    /// <summary>The revision this build writes and reads.</summary>
    public const int Revision = 1;

    /// <summary>The key that makes an entity socket furniture, naming its socket (open point O5).</summary>
    public const string SocketKey = "room_socket";

    /// <summary>The key that decides which side's socket furniture a joint keeps; higher wins.</summary>
    public const string PriorityKey = "socket_priority";

    /// <summary>The most points a stored mesh may have: a lying count fails before it allocates.</summary>
    private const int MaxPoints = 1 << 20;

    private readonly BspData? _bsp;
    private readonly RoomPropPose[][] _poses;

    private RoomStaticProps(
        StaticPropLump lump, IReadOnlyList<IReadOnlyList<Vec3[]>> hulls, IReadOnlyList<RoomProp> props, RoomPropPose[][] poses, BspData? bsp)
    {
        Lump = lump;
        Hulls = hulls;
        Props = props;
        _poses = poses;
        _bsp = bsp;
    }

    /// <summary>The room's compiled static prop lump, as the room's BSP holds it.</summary>
    public StaticPropLump Lump { get; }

    /// <summary>Per dictionary entry, the model's meshes in model space, one point set each.</summary>
    public IReadOnlyList<IReadOnlyList<Vec3[]>> Hulls { get; }

    /// <summary>Per prop of <see cref="Lump"/>, in lump order, what the record does not hold.</summary>
    public IReadOnlyList<RoomProp> Props { get; }

    /// <summary>How many turns the poses are stored for: 4 when built, 1 or 4 when read.</summary>
    public int TurnCount => _poses.Length;

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The props' poses at one quarter turn: the stored turn, or turn 0 turned when only it is stored.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public RoomPropPose[] Poses(int rotation)
    {
        if (_poses.Length == 4)
        {
            return _poses[rotation];
        }

        RoomPropPose[] turned = new RoomPropPose[Props.Count];
        for (int i = 0; i < turned.Length; i++)
        {
            turned[i] = Turn(_poses[0][i], rotation, HasLightingOrigin(Lump.Props[i]));
        }

        return turned;
    }

    /// <summary>
    /// The same props stored with only turn 0 (the link turns it): what a
    /// pack written with a rotation count of 1 reads as, for the facts that
    /// hold the two storages to the same linked bytes.
    /// </summary>
    internal RoomStaticProps WithTurnZeroOnly() => new(Lump, Hulls, Props, [_poses[0]], _bsp);

    /// <summary>Whether a record's lighting origin is a position (the prop names an <c>info_lighting</c>).</summary>
    public static bool HasLightingOrigin(StaticProp prop) => (prop.Flags & StaticPropFlags.UseLightingOrigin) != 0;

    /// <summary>
    /// A prop's pose turned by quarter turns, exactly as the flatten turns
    /// its entity's keys (<see cref="VmfPlacement"/>), so a linked record and
    /// the flattened compile's carry the same floats.
    /// </summary>
    /// <param name="pose">The pose in the room's own frame.</param>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    /// <param name="lightingOrigin">Whether the lighting origin is a position to turn.</param>
    /// <returns>The turned pose; turn 0 is the pose itself.</returns>
    /// <remarks>
    /// The flatten turns only a turned entity's <c>angles</c>, and writes
    /// each number back in its shortest round-trip spelling with a negative
    /// zero written as zero, which vbsp then reads; so a turned pose's pitch
    /// and roll lose a negative zero too, its yaw is turned by the flatten's
    /// own formula, and a turn-0 pose is left alone.
    /// </remarks>
    public static RoomPropPose Turn(RoomPropPose pose, int rotation, bool lightingOrigin)
    {
        if (rotation == 0)
        {
            return pose;
        }

        Vec3 angles = new(Unsigned(pose.Angles.X), LevelLinker.TurnYaw(pose.Angles.Y, rotation), Unsigned(pose.Angles.Z));
        return new RoomPropPose(
            RoomTransform.Rotate(pose.Origin, rotation),
            angles,
            lightingOrigin ? RoomTransform.Rotate(pose.LightingOrigin, rotation) : pose.LightingOrigin,
            LevelLinker.RotateBox(pose.Bounds.Mins, pose.Bounds.Maxs, rotation));
    }

    /// <summary>A zero without its sign, as the flatten spells every number it writes.</summary>
    public static float Unsigned(float value) => value == 0f ? 0f : value;

    /// <summary>A point with each zero unsigned (<see cref="Unsigned(float)"/>).</summary>
    public static Vec3 Unsigned(Vec3 v) => new(Unsigned(v.X), Unsigned(v.Y), Unsigned(v.Z));

    /// <summary>The room's compiled static prop lump, or null when its BSP has none.</summary>
    /// <param name="bsp">The room's BSP.</param>
    /// <exception cref="InvalidBspException">The lump is not one this build reads.</exception>
    public static StaticPropLump? ReadLump(BspData bsp)
    {
        int id = GameLumpId.MakeId(GameLumpId.StaticProps);
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            if (entry.Id == id)
            {
                return StaticPropLump.Read(entry);
            }
        }

        return null;
    }

    /// <summary>
    /// Every <c>prop_static</c> of a loaded map, in entity order, before
    /// vbsp consumes them: what <see cref="BuildAsync"/> matches the
    /// compiled records to.
    /// </summary>
    /// <param name="entities">The map's entities as the loader left them.</param>
    public static IReadOnlyList<RoomPropSource> Sources(IReadOnlyList<MapEntity> entities)
    {
        List<RoomPropSource> sources = [];
        foreach (MapEntity entity in entities)
        {
            string className = entity.ValueForKey("classname");
            if (!string.Equals(className, "prop_static", StringComparison.Ordinal)
                && !string.Equals(className, "static_prop", StringComparison.Ordinal))
            {
                continue;
            }

            sources.Add(new RoomPropSource(
                int.TryParse(entity.ValueForKey("hammerid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1,
                StaticPropEmitter.ReadBuild(entity),
                Optional(entity, RoomNeeds.Key),
                Optional(entity, SocketKey),
                Optional(entity, PriorityKey)));
        }

        return sources;

        static string? Optional(MapEntity entity, string key) => entity.HasKey(key) ? entity.ValueForKey(key) : null;
    }

    /// <summary>
    /// Refuses a prop that asks for texel lighting (open point O13), before
    /// any compile time is spent on the room.
    /// </summary>
    /// <param name="room">The room's name.</param>
    /// <param name="sources">The room's props.</param>
    /// <exception cref="RoomLintException">A prop asks for texel lighting; the message is the rooms design's.</exception>
    /// <remarks>
    /// vrad here lights static props per vertex only (the texel path's
    /// <c>texelslighting_N.ppl</c> files are not ported), so a room whose
    /// prop asks for texel lighting would be baked, once the rooms carry
    /// lighting, with something other than what its author asked for. It is
    /// refused rather than quietly lit per vertex, until vrad bakes it.
    /// </remarks>
    public static void RefuseTexelLighting(string room, IReadOnlyList<RoomPropSource> sources)
    {
        foreach (RoomPropSource source in sources)
        {
            if (source.Texel)
            {
                throw new RoomLintException($"room {room}: prop_static {source.Id} asks for texel lighting, which this vrad does not bake.");
            }
        }
    }

    /// <summary>
    /// A compiled room's static props for the link, or null when its
    /// compile emitted none: each record matched to the entity it came
    /// from, the models' hulls read, the O6 rule checked, and every pose
    /// turned four ways.
    /// </summary>
    /// <param name="definition">The room.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <param name="sources">The room's <c>prop_static</c>s as loaded (<see cref="Sources"/>).</param>
    /// <param name="context">The room's compile context: the content the models are read from.</param>
    /// <param name="cancellationToken">Cancels the model reads.</param>
    /// <returns>The props, bound to <paramref name="bsp"/>; or null.</returns>
    /// <exception cref="RoomLintException">
    /// A prop reaches outside its cell and is not socket furniture inside its
    /// doorway, or a furniture key is wrong; the message names the room and
    /// the prop.
    /// </exception>
    /// <exception cref="MapCompileException">A prop's model no longer loads.</exception>
    /// <remarks>
    /// <para>
    /// <b>Matching.</b> vbsp emits the props in entity order and leaves out
    /// one whose model does not load or that touches no leaf (each with its
    /// warning), so the records are the sources in order with some left
    /// out. Each record is matched to the next source with its model,
    /// origin and angles, which vbsp copied from that entity's keys as
    /// read here.
    /// </para>
    /// <para>
    /// <b>The cell rule (O6).</b> The prop's extent is its hull's points
    /// placed at the prop's pose, which is exactly the hull's box. A prop
    /// inside the cell's box (within <see cref="RoomLinter.CellEpsilon"/>)
    /// is fine. One reaching further is refused with how far it reaches,
    /// unless it is socket furniture and every part of its hull outside the
    /// cell lies in the doorway beyond its socket: the neighbour's plug box
    /// of a joint there, the socket's plug box mirrored through the cell
    /// face. That is where a door frame hung in a doorway reaches, and at a
    /// joint that space is the neighbour's doorway leaf, which the link's
    /// tree walk finds; at a cap the furniture is dropped (O5).
    /// </para>
    /// </remarks>
    public static async Task<RoomStaticProps?> BuildAsync(
        RoomDefinition definition,
        BspData bsp,
        IReadOnlyList<RoomPropSource> sources,
        VbspContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(context);
        StaticPropLump? lump = ReadLump(bsp);
        if (lump is null || lump.Props.Count == 0)
        {
            return null;
        }

        string room = definition.Name;
        RoomPropSource[] matched = Match(room, lump, sources);

        // The hulls, per dictionary entry, read as the compile read them.
        List<IReadOnlyList<Vec3[]>> hulls = new(lump.ModelNames.Count);
        List<IStaticPropHull> built = new(lump.ModelNames.Count);
        ManagedStaticPropCollision collision = new();
        foreach (string model in lump.ModelNames)
        {
            List<CompileDiagnostic> ignored = [];
            List<Vec3[]> meshes = await StaticPropEmitter.LoadMeshesAsync(context, model, ignored, cancellationToken).ConfigureAwait(false)
                ?? throw new MapCompileException($"room {room}: the static prop model {model} loaded for the compile and not since");
            Vec3[][] distinct = [.. meshes.Where(m => m.Length >= 4).Select(m => m.Distinct().ToArray())];
            hulls.Add(distinct);
            built.Add(await collision.BuildHullAsync(distinct, cancellationToken).ConfigureAwait(false)
                ?? throw new MapCompileException($"room {room}: the static prop model {model} has no hull"));
        }

        List<RoomProp> props = new(lump.Props.Count);
        RoomPropPose[][] poses = [new RoomPropPose[lump.Props.Count], new RoomPropPose[lump.Props.Count],
            new RoomPropPose[lump.Props.Count], new RoomPropPose[lump.Props.Count]];
        for (int i = 0; i < lump.Props.Count; i++)
        {
            StaticProp record = lump.Props[i];
            RoomPropSource source = matched[i];
            RoomProp prop = Describe(definition, source);
            Box bounds = await CheckCellAsync(definition, prop, lump.ModelNames[record.PropType], built[record.PropType], record, cancellationToken)
                .ConfigureAwait(false);
            props.Add(prop);

            RoomPropPose pose = new(record.Origin, record.Angles, record.LightingOrigin, bounds);
            for (int turn = 0; turn < 4; turn++)
            {
                poses[turn][i] = Turn(pose, turn, HasLightingOrigin(record));
            }
        }

        return new RoomStaticProps(lump, hulls, props, poses, bsp);
    }

    /// <summary>The props bound to another BSP: what the pack reader does once it has loaded the room they sit with.</summary>
    internal RoomStaticProps For(BspData bsp) => new(Lump, Hulls, Props, _poses, bsp);

    /// <summary>The pack section holding these props.</summary>
    /// <param name="codec">How to store the payload; none by default, as every link section.</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    internal RoomPackSectionData ToSection(RoomLinkCodec codec = RoomLinkCodec.None)
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(Hulls.Count);
        foreach (IReadOnlyList<Vec3[]> meshes in Hulls)
        {
            w.Int(meshes.Count);
            foreach (Vec3[] mesh in meshes)
            {
                w.Structs<Vec3>(mesh);
            }
        }

        w.Int(Props.Count);
        foreach (RoomProp prop in Props)
        {
            w.Int(prop.Id);
            w.Int(prop.Socket);
            w.Int(prop.Priority);
            w.Int(prop.Needs.Length);
            foreach (RoomNeed need in prop.Needs)
            {
                w.Byte((byte)need.Direction);
                w.Byte((byte)((need.Joined ? 1 : 0) | (need.Negated ? 2 : 0)));
            }
        }

        w.Int(_poses.Length);
        foreach (RoomPropPose[] turn in _poses)
        {
            w.Structs<RoomPropPose>(turn, counted: false);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), codec));
    }

    /// <summary>
    /// A room's props from its section, bound to <paramref name="bsp"/>; or
    /// null when the section is absent or of a revision this build does not read.
    /// </summary>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP, whose lump the section must fit.</param>
    /// <returns>The props, or null.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload that decodes to another
    /// length than recorded, or a payload that does not fit the room's lump:
    /// cut short, another dictionary or prop count, a socket the room does
    /// not have, a direction or flag out of range, a turn count other than
    /// 1 or 4, bytes after its end.
    /// </exception>
    internal static RoomStaticProps? Read(ArraySegment<byte>? section, RoomDefinition room, BspData bsp)
    {
        if (RoomLinkSections.Open(section, room.Name, SectionTag) is not { } r)
        {
            return null;
        }

        StaticPropLump lump;
        try
        {
            lump = ReadLump(bsp) ?? new StaticPropLump();
        }
        catch (InvalidBspException exception)
        {
            throw new LinkException($"room {room.Name}'s static prop lump does not read: {exception.Message}");
        }

        int models = r.Int();
        if (models != lump.ModelNames.Count)
        {
            throw r.Mismatch($"{models} prop models; the room has {lump.ModelNames.Count}");
        }

        List<IReadOnlyList<Vec3[]>> hulls = new(models);
        for (int m = 0; m < models; m++)
        {
            Vec3[][] meshes = new Vec3[r.Count("meshes")][];
            for (int k = 0; k < meshes.Length; k++)
            {
                int points = r.Count("points");
                if (points is < 4 or > MaxPoints)
                {
                    throw r.Mismatch($"a mesh of {points} points");
                }

                meshes[k] = r.Structs<Vec3>("points", points, counted: false);
            }

            hulls.Add(meshes);
        }

        int count = r.Int();
        if (count != lump.Props.Count)
        {
            throw r.Mismatch($"{count} props; the room has {lump.Props.Count}");
        }

        List<RoomProp> props = new(count);
        for (int i = 0; i < count; i++)
        {
            int id = r.Int();
            int socket = r.Int();
            if (socket < -1 || socket >= room.Sockets.Count)
            {
                throw r.Mismatch($"socket {socket} for a prop; the room has {room.Sockets.Count}");
            }

            int priority = r.Int();
            ImmutableArray<RoomNeed>.Builder needs = ImmutableArray.CreateBuilder<RoomNeed>(r.Count("conditions"));
            for (int n = needs.Capacity; n > 0; n--)
            {
                RoomDirection direction = (RoomDirection)r.Small(7, "a direction");
                int flags = r.Small(3, "condition flags");
                bool joined = (flags & 1) != 0;
                if (joined && !RoomDirections.IsSide(direction))
                {
                    throw r.Mismatch($"a joined condition on the diagonal {RoomDirections.Name(direction)}");
                }

                needs.Add(new RoomNeed(direction, joined, (flags & 2) != 0));
            }

            props.Add(new RoomProp(id, needs.MoveToImmutable(), socket, priority));
        }

        int turns = r.Int();
        if (turns is not (1 or 4))
        {
            throw r.Mismatch($"{turns} turns of poses; a section holds 1 or 4");
        }

        RoomPropPose[][] poses = new RoomPropPose[turns][];
        for (int t = 0; t < turns; t++)
        {
            poses[t] = r.Structs<RoomPropPose>("poses", count, counted: false);
        }

        r.End();
        return new RoomStaticProps(lump, hulls, props, poses, bsp);
    }

    /// <summary>Each record's source: the next entity with its model, origin and angles.</summary>
    private static RoomPropSource[] Match(string room, StaticPropLump lump, IReadOnlyList<RoomPropSource> sources)
    {
        RoomPropSource[] matched = new RoomPropSource[lump.Props.Count];
        int next = 0;
        for (int i = 0; i < matched.Length; i++)
        {
            StaticProp record = lump.Props[i];
            string model = lump.ModelNames[record.PropType];
            while (next < sources.Count
                && !(string.Equals(sources[next].Build.ModelName, model, StringComparison.Ordinal)
                    && sources[next].Build.Origin == record.Origin
                    && sources[next].Build.Angles == record.Angles))
            {
                next++;
            }

            if (next == sources.Count)
            {
                throw new InvalidOperationException(
                    $"room {room}: static prop {i} ({model}) matches no prop_static the compile read");
            }

            matched[i] = sources[next++];
        }

        return matched;
    }

    /// <summary>A source's furniture and inclusion keys, checked.</summary>
    private static RoomProp Describe(RoomDefinition definition, RoomPropSource source)
    {
        string room = definition.Name;
        ImmutableArray<RoomNeed> needs = [];
        if (source.Needs is { } value)
        {
            // The naming rule checked the key on the VMF before the compile
            // (RoomNameAnalysis.CheckVmf), with its own message.
            if (!RoomNeeds.TryParse(value, out List<RoomNeed> parsed, out string? unknown))
            {
                throw new RoomLintException(
                    $"room {room}: entity {source.Id} (prop_static) room_needs \"{value}\": unknown direction \"{unknown}\";"
                    + " use east, west, north, south, a diagonal, or joined_ with a side, optionally negated with !.");
            }

            needs = [.. parsed];
        }

        int socket = -1;
        if (source.Socket is { } socketName)
        {
            for (int s = 0; s < definition.Sockets.Count && socket < 0; s++)
            {
                socket = string.Equals(definition.Sockets[s].Name, socketName, StringComparison.Ordinal) ? s : -1;
            }

            if (socket < 0)
            {
                throw new RoomLintException(
                    $"room {room}: prop_static {source.Id} has {SocketKey} \"{socketName}\", which is not a socket of the room.");
            }
        }

        int priority = 0;
        if (source.Priority is { } text
            && !int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out priority))
        {
            throw new RoomLintException(
                $"room {room}: prop_static {source.Id} has {PriorityKey} \"{text}\", which is not a whole number.");
        }

        return new RoomProp(source.Id, needs, socket, priority);
    }

    /// <summary>The cell rule of open point O6, on the prop's hull at its room-local pose; returns the hull's box there.</summary>
    private static async Task<Box> CheckCellAsync(
        RoomDefinition definition, RoomProp prop, string model, IStaticPropHull hull, StaticProp record, CancellationToken cancellationToken)
    {
        float cell = definition.CellSize;
        (Vec3 mins, Vec3 maxs) = await hull.GetAabbAsync(record.Origin, record.Angles, cancellationToken).ConfigureAwait(false);
        float reach = Math.Max(
            Math.Max(Math.Max(-mins.X, maxs.X - cell), Math.Max(-mins.Y, maxs.Y - cell)),
            Math.Max(-mins.Z, maxs.Z - cell));
        Box bounds = new(mins, maxs);
        if (reach <= RoomLinter.CellEpsilon)
        {
            return bounds;
        }

        if (prop.Socket >= 0)
        {
            bool outside = false;
            foreach ((Vec3 Normal, float Dist)[] region in OutsideDoorway(definition, definition.Sockets[prop.Socket]))
            {
                outside |= await hull.IntersectsAsync(region, record.Origin, record.Angles, cancellationToken).ConfigureAwait(false);
            }

            if (!outside)
            {
                return bounds;
            }
        }

        throw new RoomLintException(
            string.Create(CultureInfo.InvariantCulture,
                $"room {definition.Name}: prop_static {prop.Id} ({model}) reaches {reach:0.##} units outside the cell; props stay in their cell except as socket furniture."));
    }

    /// <summary>
    /// The space outside a room's cell that is not the doorway beyond one
    /// of its sockets, as convex regions (each a list of half-spaces keeping
    /// <c>n·x &lt;= d</c>), each pulled in by <see cref="RoomLinter.CellEpsilon"/>
    /// so that touching the cell or the doorway is not reaching past it.
    /// </summary>
    /// <remarks>
    /// The doorway beyond a socket is the socket's plug box mirrored through
    /// the cell face: at a joint, the neighbour's plug box. The regions are
    /// the five half-spaces beyond the cell's other faces, the space beyond
    /// the socket's face deeper than the doorway, and the four slabs beyond
    /// that face on either side of the doorway's opening; they overlap,
    /// which a test for "touches any" does not mind.
    /// </remarks>
    internal static IReadOnlyList<(Vec3 Normal, float Dist)[]> OutsideDoorway(RoomDefinition definition, RoomSocket socket)
    {
        float cell = definition.CellSize;
        float e = RoomLinter.CellEpsilon;
        Box plug = RoomLinter.SealBox(definition, socket, cell);
        float depth = definition.Kit.Depth;
        (int axis, int sign) = socket.Facing switch
        {
            RoomFacing.PositiveX => (0, 1),
            RoomFacing.NegativeX => (0, -1),
            RoomFacing.PositiveY => (1, 1),
            _ => (1, -1),
        };

        List<(Vec3 Normal, float Dist)[]> regions = [];
        for (int a = 0; a < 3; a++)
        {
            for (int s = -1; s <= 1; s += 2)
            {
                if (a == axis && s == sign)
                {
                    continue;
                }

                regions.Add([Beyond(a, s, s > 0 ? cell + e : -e)]);
            }
        }

        // Beyond the socket's face: past the doorway's depth, and beside the
        // opening in the two axes of the face.
        (Vec3 Normal, float Dist) past = Beyond(axis, sign, sign > 0 ? cell + e : -e);
        regions.Add([Beyond(axis, sign, sign > 0 ? cell + depth + e : -depth - e)]);
        for (int a = 0; a < 3; a++)
        {
            if (a == axis)
            {
                continue;
            }

            regions.Add([past, Beyond(a, -1, Component(plug.Mins, a) - e)]);
            regions.Add([past, Beyond(a, 1, Component(plug.Maxs, a) + e)]);
        }

        return regions;

        // The half-space beyond a coordinate on one axis: x_a > at for a
        // positive side, x_a < at for a negative one, as n·x <= d.
        static (Vec3 Normal, float Dist) Beyond(int a, int s, float at)
        {
            Vec3 n = a switch { 0 => new Vec3(-s, 0, 0), 1 => new Vec3(0, -s, 0), _ => new Vec3(0, 0, -s) };
            return (n, -s * at);
        }

        static float Component(Vec3 v, int a) => a switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    }
}
