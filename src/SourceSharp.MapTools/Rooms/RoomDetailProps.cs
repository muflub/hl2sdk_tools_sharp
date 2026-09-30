//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Detail;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A room's detail props as the link carries them: every detail prop of the
/// room's compile, its origin and angles turned for each quarter turn (the
/// rooms design, 4.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>What a turn changes.</b> vbsp writes the <c>dprp</c> game lump: a
/// model dictionary, a sprite dictionary and one record per prop (origin,
/// angles, dictionary entry, leaf, lighting, style run, sway, shape,
/// orientation, type, scale), sorted by leaf. A placement moves a record's
/// origin (a point: turned, then the cell added) and turns its angles
/// (yaw plus 90 degrees a turn, which is exact for the rotation it stands
/// for, since yaw is the outermost of the three). Its leaf is the linked
/// tree's (the link puts every origin through the linked tree, as vbsp
/// puts each through its own); its dictionary entry is renumbered into the
/// level's merged dictionaries; its lighting and style run are the bake's
/// (<see cref="RoomDetailLighting"/>). Everything else, the dictionaries
/// included, is the room's lump byte for byte, which stays in the room's
/// container.
/// </para>
/// <para>
/// <b>The same turn as an entity's.</b> A prop is turned as the flatten
/// turns an entity's keys (<see cref="RoomStaticProps.Turn"/>): the origin
/// turned exactly, the yaw turned by the flatten's formula and kept in
/// [0, 360), pitch and roll without a negative zero, turn 0 left alone. So
/// a <c>prop_detail</c> entity's record in the linked map is the one the
/// flattened level's vbsp writes for the moved entity, bit for bit. A prop
/// vbsp scattered over a face has no such twin: the flattened compile seeds
/// its faces by their renumbered ids and cuts them differently, which is
/// another random draw (the design's statistical equivalence), so the exact
/// test for those is the room's own compile, prop for prop.
/// </para>
/// <para>
/// <b>Per turn.</b> The origins and angles are stored for all four quarter
/// turns (the rooms design's pack rule, 1.1), so the link only adds the
/// placement's translation. The section records how many turns it holds, 1
/// or 4; a pack with one holds turn 0 and the link turns it, to the same
/// bytes (<see cref="Origins"/>, <see cref="Angles"/>).
/// </para>
/// <para>
/// <b>Required, not only a shortcut.</b> A room whose lump has detail props
/// and none of this bound to its compile (a pack written before detail props
/// were carried) is refused at link, naming the room, as a room with
/// displacements and no displacement data is: the pack is what says the
/// room was held to the detail prop rules when it was compiled.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>DPRP</c>) follows the
/// link sections' framing (<see cref="RoomLinkSections"/>): a codec byte,
/// the payload's decoded length (<c>int64</c>, big-endian), and a payload
/// that starts with an <c>int32</c> revision (<see cref="Revision"/>).
/// After the revision, big-endian <c>int32</c>s: the prop count (the room's
/// lump's), then the turn count, 1 or 4, and per turn every prop's origin
/// and then every prop's angles, three little-endian floats each. A section
/// of a revision this build does not read is absent; one that does not fit
/// the room's lump is refused as damaged. The tag is one an older build
/// skips, and such a build refuses a room with detail props by its lump, so
/// the pack's format version is unchanged.
/// </para>
/// <para>
/// <b>Binding.</b> It describes one compile's lumps: read from a pack or
/// built by the room compile, it remembers the BSP, and the link uses it
/// only for that very BSP (<see cref="IsFor"/>).
/// </para>
/// </remarks>
internal sealed class RoomDetailProps
{
    /// <summary>The tag of a room's detail prop section.</summary>
    public const string SectionTag = "DPRP";

    /// <summary>The revision this build writes and reads: the link sections' own (<see cref="RoomLinkSections.RevisionFor"/>).</summary>
    public const int Revision = RoomLinkSections.Revision;

    /// <summary>
    /// The most detail props a map holds: vbsp's own cap
    /// (<see cref="DetailPropEmitter.MaxDetailProps"/>, 65,535), past which
    /// it stops writing props (a model) or fails (a sprite), so a linked
    /// level past it would be one the flattened level's compile cannot
    /// write.
    /// </summary>
    public const int MaxDetailProps = DetailPropEmitter.MaxDetailProps;

    private readonly BspData? _bsp;
    private readonly Vec3[][] _origins;
    private readonly Vec3[][] _angles;

    private RoomDetailProps(DetailPropLump lump, Vec3[][] origins, Vec3[][] angles, BspData? bsp)
    {
        Lump = lump;
        _origins = origins;
        _angles = angles;
        _bsp = bsp;
    }

    /// <summary>The room's detail prop lump as its compile wrote it: the dictionaries and the records, room-local.</summary>
    public DetailPropLump Lump { get; }

    /// <summary>How many detail props the room's compile wrote.</summary>
    public int Count => _origins[0].Length;

    /// <summary>How many turns the poses are stored for: 4 when built, 1 or 4 when read.</summary>
    public int TurnCount => _origins.Length;

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>Every prop's origin at one quarter turn, not yet moved: the stored turn, or turn 0 turned.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public Vec3[] Origins(int rotation) => _origins.Length == 4 ? _origins[rotation] : TurnOrigins(_origins[0], rotation);

    /// <summary>Every prop's angles at one quarter turn: the stored turn, or turn 0 turned.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public Vec3[] Angles(int rotation) => _angles.Length == 4 ? _angles[rotation] : TurnAngles(_angles[0], rotation);

    /// <summary>
    /// The same props stored with only turn 0 (the link turns them): what a
    /// pack written with a rotation count of 1 reads as, for the facts that
    /// hold the two storages to the same linked bytes.
    /// </summary>
    internal RoomDetailProps WithTurnZeroOnly() => new(Lump, [_origins[0]], [_angles[0]], _bsp);

    /// <summary>
    /// A compiled room's detail props for the link, or null when its compile
    /// wrote none: every origin and angle turned four ways.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The props, bound to <paramref name="bsp"/>; or null.</returns>
    /// <exception cref="LinkException">The lump is not one vbsp writes (<see cref="ReadLump"/>).</exception>
    public static RoomDetailProps? Build(string room, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bsp);
        if (ReadLump(room, bsp) is not { Props.Count: > 0 } lump)
        {
            return null;
        }

        Vec3[] origins = [.. lump.Props.Select(p => p.Origin)];
        Vec3[] angles = [.. lump.Props.Select(p => p.Angles)];
        Vec3[][] turnedOrigins = new Vec3[4][];
        Vec3[][] turnedAngles = new Vec3[4][];
        for (int turn = 0; turn < 4; turn++)
        {
            turnedOrigins[turn] = TurnOrigins(origins, turn);
            turnedAngles[turn] = TurnAngles(angles, turn);
        }

        return new RoomDetailProps(lump, turnedOrigins, turnedAngles, bsp);
    }

    /// <summary>
    /// A room's detail prop lump, checked against what vbsp writes: version
    /// 4, every record naming an entry of the dictionary its type reads (a
    /// model's the model names, a sprite's or a shape's the sprites) and a
    /// leaf of the room; or null when the room has no such lump.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The lump, or null.</returns>
    /// <exception cref="LinkException">A lump out of the shape vbsp writes.</exception>
    /// <remarks>
    /// The link renumbers every record's dictionary entry into the level's
    /// merged dictionaries and reads the entry to do it, so a record naming
    /// an entry its room does not have would name another room's in the
    /// level; refusing it here keeps a damaged room from linking into a map
    /// the engine misreads.
    /// </remarks>
    internal static DetailPropLump? ReadLump(string room, BspData bsp)
    {
        int id = GameLumpId.MakeId(GameLumpId.DetailProps);
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            if (entry.Id != id)
            {
                continue;
            }

            DetailPropLump lump;
            try
            {
                lump = DetailPropLump.Read(entry);
            }
            catch (InvalidBspException exception)
            {
                throw new LinkException($"room {room}'s detail prop lump cannot be read: {exception.Message}");
            }

            int leaves = BspStructView.Count<DLeaf>(bsp[BspLump.Leafs]);
            for (int i = 0; i < lump.Props.Count; i++)
            {
                DetailObjectLump prop = lump.Props[i];
                bool model = prop.Type == (byte)MapFormats.Bsp.Structs.DetailPropType.Model;
                int entries = model ? lump.ModelNames.Count : lump.Sprites.Count;
                if (prop.Type > (byte)MapFormats.Bsp.Structs.DetailPropType.ShapeTri)
                {
                    throw new LinkException($"room {room}'s detail prop {i} is of type {prop.Type}; vbsp writes 0 to 3.");
                }

                if (prop.DetailModel >= entries)
                {
                    throw new LinkException(
                        $"room {room}'s detail prop {i} names {(model ? "model" : "sprite")} {prop.DetailModel}; its dictionary holds {entries}.");
                }

                if (prop.Leaf >= leaves)
                {
                    throw new LinkException($"room {room}'s detail prop {i} is in leaf {prop.Leaf}; the room has {leaves}.");
                }
            }

            return lump;
        }

        return null;
    }

    /// <summary>
    /// What is wrong with a room's plugs for its detail props, as the
    /// refusal says it, or null: a side of a socket's plug whose material
    /// grows detail props (<c>%detailtype</c>).
    /// </summary>
    /// <param name="definition">The room: its name, cell and sockets.</param>
    /// <param name="room">The room's VMF, room-local.</param>
    /// <param name="materials">The compile's materials, to read each side's <c>%detailtype</c>.</param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>The refusal's text, or null.</returns>
    /// <remarks>
    /// A plug is a wall only while its socket is capped: a joint strips its
    /// faces and the flattened level leaves the brush out, so props vbsp grew
    /// on a plug's side would stand in the joined doorway in the link and be
    /// gone from the flattened compile. The kit's plug is a trigger brush,
    /// whose faces vbsp does not draw; a plug given a detail material on a
    /// side is refused, before the compile, as a displacement on one is. A
    /// plug is the world brush whose box is its socket's plug box, the rule
    /// the flatten leaves joined plugs out by (<see cref="RoomLibraryVmf.Same"/>).
    /// </remarks>
    public static async Task<string?> PlugProblemAsync(
        RoomDefinition definition, VmfDocument room, MaterialFactsCache materials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(materials);
        foreach (VmfChunk world in room.GetChunks(MapFileLoader.WorldChunk))
        {
            foreach (VmfChunk solid in world.GetChunks(MapFileLoader.SolidChunk))
            {
                Box brush = VmfPlacement.Bounds(solid);
                foreach (RoomSocket socket in definition.Sockets)
                {
                    if (!RoomLibraryVmf.Same(brush, RoomLinter.SealBox(definition, socket, definition.CellSize)))
                    {
                        continue;
                    }

                    foreach (VmfChunk side in solid.GetChunks(MapFileLoader.SideChunk))
                    {
                        string material = side.GetValue("material") ?? string.Empty;
                        MaterialFacts facts = await materials.GetAsync(material, cancellationToken).ConfigureAwait(false);
                        if (facts.Found && facts.Material()?.GetString("%detailtype") is { } type)
                        {
                            return string.Create(
                                CultureInfo.InvariantCulture,
                                $"room {definition.Name}: socket \"{socket.Name}\"'s plug has side {side.GetValue("id") ?? "?"} of material {material}, which grows detail props (%detailtype {type}); a joint removes the plug.");
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// How many detail props a compiled room's lump holds, read from the
    /// lump's three counts alone, for the link's capacity check: 0 for a
    /// room without the lump or with one too short to say.
    /// </summary>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The prop count.</returns>
    public static int CountOf(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        int id = GameLumpId.MakeId(GameLumpId.DetailProps);
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            if (entry.Id != id)
            {
                continue;
            }

            ReadOnlySpan<byte> bytes = entry.Data.Span;
            long at = 0;
            if (!Skip(bytes, ref at, Unsafe.SizeOf<DetailObjectDictLump>())
                || !Skip(bytes, ref at, Unsafe.SizeOf<DetailSpriteDictLump>())
                || at + 4 > bytes.Length)
            {
                return 0;
            }

            return Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(bytes[(int)at..]));
        }

        return 0;

        static bool Skip(ReadOnlySpan<byte> bytes, ref long at, int size)
        {
            if (at + 4 > bytes.Length)
            {
                return false;
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[(int)at..]);
            at += 4 + ((long)Math.Max(0, count) * size);
            return true;
        }
    }

    /// <summary>
    /// Whether a room's detail prop lump has content: any non-zero byte (an
    /// empty lump is three zero counts). What the link refuses in a room
    /// without detail prop data, read without parsing, so a damaged lump is
    /// refused by the same text.
    /// </summary>
    internal static bool HasContent(BspData bsp)
    {
        int id = GameLumpId.MakeId(GameLumpId.DetailProps);
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            if (entry.Id == id && entry.Data.Span.ContainsAnyExcept((byte)0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Origins turned by quarter turns, exactly (a turn permutes and negates components).</summary>
    private static Vec3[] TurnOrigins(Vec3[] origins, int rotation) =>
        rotation == 0 ? origins : [.. origins.Select(o => RoomTransform.Rotate(o, rotation))];

    /// <summary>Angles turned as the flatten turns an entity's (<see cref="TurnAngle"/>).</summary>
    private static Vec3[] TurnAngles(Vec3[] angles, int rotation) =>
        rotation == 0 ? angles : [.. angles.Select(a => TurnAngle(a, rotation))];

    /// <summary>
    /// A prop's angles turned by quarter turns, as the flatten turns an
    /// entity's <c>angles</c> key and vbsp reads it back: pitch and roll
    /// without a negative zero, the yaw turned and kept in [0, 360); turn 0
    /// left alone.
    /// </summary>
    /// <param name="angles">Pitch, yaw, roll in the room's frame.</param>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    /// <returns>The turned angles.</returns>
    public static Vec3 TurnAngle(Vec3 angles, int rotation) =>
        rotation == 0
            ? angles
            : new Vec3(RoomStaticProps.Unsigned(angles.X), LevelLinker.TurnYaw(angles.Y, rotation), RoomStaticProps.Unsigned(angles.Z));

    /// <summary>The pack section holding these props.</summary>
    /// <param name="codec">How to store the payload; none by default, as every link section.</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    internal RoomPackSectionData ToSection(RoomLinkCodec codec = RoomLinkCodec.None)
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(Count);
        w.Int(_origins.Length);
        for (int t = 0; t < _origins.Length; t++)
        {
            w.Structs<Vec3>(_origins[t], counted: false);
            w.Structs<Vec3>(_angles[t], counted: false);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), codec));
    }

    /// <summary>
    /// A room's detail props from its section, bound to <paramref name="bsp"/>;
    /// or null when the section is absent or of a revision this build does not read.
    /// </summary>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP, whose lump the section must fit.</param>
    /// <returns>The props, or null.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload that decodes to another
    /// length than recorded, or a payload that does not fit the room's lump:
    /// cut short, another prop count, a turn count other than 1 or 4, bytes
    /// after its end; or a room lump out of the shape vbsp writes.
    /// </exception>
    internal static RoomDetailProps? Read(ArraySegment<byte>? section, string room, BspData bsp)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        DetailPropLump? lump = ReadLump(room, bsp);
        int props = lump?.Props.Count ?? 0;
        int count = r.Int();
        if (count != props || count == 0)
        {
            throw r.Mismatch($"{count} detail props; the room has {props}");
        }

        int turns = r.Int();
        if (turns is not (1 or 4))
        {
            throw r.Mismatch($"{turns} turns of detail props; a section holds 1 or 4");
        }

        Vec3[][] origins = new Vec3[turns][];
        Vec3[][] angles = new Vec3[turns][];
        for (int t = 0; t < turns; t++)
        {
            origins[t] = r.Structs<Vec3>("detail prop origins", count, counted: false);
            angles[t] = r.Structs<Vec3>("detail prop angles", count, counted: false);
        }

        r.End();
        return new RoomDetailProps(lump!, origins, angles, bsp);
    }
}
