//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>Which part of a key a name occupies.</summary>
internal enum NameField : byte
{
    /// <summary>The whole value of a name-valued key.</summary>
    Whole = 0,

    /// <summary>An output's target field.</summary>
    Target = 1,

    /// <summary>An output's parameter field, when the whole parameter is a name.</summary>
    Parameter = 2,
}

/// <summary>
/// A piece of a key's value: text copied as it is, or a local name the link
/// fills in with its cell (<see cref="RoomNameTurn"/>).
/// </summary>
/// <param name="Literal">The text, or null for a name.</param>
/// <param name="Dx">The name's column offset in the level's frame (already turned).</param>
/// <param name="Dy">The name's row offset in the level's frame (already turned).</param>
/// <param name="Rest">What follows the name's placeholder.</param>
/// <param name="Field">Which part of the key the name is.</param>
internal readonly record struct NameSegment(string? Literal, int Dx, int Dy, string Rest, NameField Field)
{
    /// <summary>A piece of text.</summary>
    public static NameSegment Text(string text) => new(text, 0, 0, string.Empty, NameField.Whole);
}

/// <summary>A key whose value holds at least one local name, as pieces.</summary>
/// <param name="Entity">The entity's index in the room's entity list.</param>
/// <param name="Pair">The key's index in the entity.</param>
/// <param name="IsOutput">Whether the key is an output.</param>
/// <param name="Segments">The value in pieces, text and names alternating as they fall.</param>
/// <param name="IsTargetName">Whether the key is the entity's <c>targetname</c>: a name the room defines rather than one it references.</param>
internal sealed record NamePairRef(int Entity, int Pair, bool IsOutput, ImmutableArray<NameSegment> Segments, bool IsTargetName = false);

/// <summary>One condition of a <c>room_needs</c> key, with the level offset it looks at for one turn.</summary>
/// <param name="Need">The condition as authored.</param>
/// <param name="Dx">The neighbour's column offset in the level's frame (0 for a joined condition).</param>
/// <param name="Dy">The neighbour's row offset in the level's frame.</param>
internal readonly record struct TurnedNeed(RoomNeed Need, int Dx, int Dy);

/// <summary>An entity's <c>room_needs</c> key, its conditions turned.</summary>
/// <param name="Entity">The entity's index.</param>
/// <param name="Pair">The key's index.</param>
/// <param name="Conditions">Every condition, all of which must hold.</param>
internal sealed record NeedsRef(int Entity, int Pair, ImmutableArray<TurnedNeed> Conditions);

/// <summary>What a fold could make of an entity, judged from its own keys alone.</summary>
internal enum FoldKind : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>A stateless <c>logic_relay</c> with a local name.</summary>
    Relay = 1,

    /// <summary>A <c>logic_branch</c> with a local name whose outputs a constant value can stand in for.</summary>
    Branch = 2,

    /// <summary>An unnamed <c>logic_auto</c> without a global state, which merges with its kind.</summary>
    Auto = 3,

    /// <summary>A <c>filter_*</c> with a local name, which dedupes with its equals.</summary>
    Filter = 4,
}

/// <summary>An entity a fold could remove or merge.</summary>
/// <param name="Entity">The entity's index.</param>
/// <param name="Kind">What the fold could do with it.</param>
internal readonly record struct FoldCandidate(int Entity, FoldKind Kind);

/// <summary>
/// A room's names for one quarter turn: every key the link fills in, every
/// <c>room_needs</c> condition with the cell it looks at, the fold
/// candidates, and the warnings the room compile gave. What the room compile
/// stores per turn so the link substitutes cell numbers and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per turn.</b> A local name's offset is in the room's frame and
/// the link needs it in the level's; the turn is the only thing that
/// changes it, and it is known at room compile time. So each turn's section
/// holds the offsets already turned, and the link adds the cell and
/// concatenates. The owner's rule for the pack (link speed over disk size,
/// work ahead of time over work at link) makes four small sections the
/// right trade. The text around a name is kept as pieces, so a key is
/// rebuilt by concatenation, never re-parsed.
/// </para>
/// <para>
/// <b>The section</b> (<c>NAM</c><i>r</i>, one per turn, written after the
/// turn's other sections) follows the link sections' framing
/// (<see cref="RoomLinkSections"/>): a codec byte, the decoded payload's
/// length as a big-endian <c>int64</c>, and a payload that starts with an
/// <c>int32</c> revision (<see cref="Revision"/>). A reader skips a tag it
/// does not know; a section of a revision it does not read is absent, and
/// the link computes the same table from the room's entity lump. After the
/// revision: the room's entity count; the named keys (count, then per key
/// its entity, its index, a kind byte (0 a key, 1 an output, 2 the
/// entity's targetname), and its pieces: count, then per
/// piece a kind byte, and for text the string, for a name the two offsets
/// as signed bytes, the field byte and the rest); the <c>room_needs</c> keys
/// (count, then per key its entity, its index and its conditions: count,
/// then per condition the direction byte, a flags byte (1 joined, 2
/// negated) and the two turned offsets as signed bytes); the fold
/// candidates (count, then per candidate its entity and kind byte); and the
/// warnings (count, then strings). Every integer is big-endian
/// <c>int32</c>, every string an <c>int32</c> byte length and UTF-8.
/// </para>
/// </remarks>
internal sealed class RoomNameTurn
{
    /// <summary>The revision this build writes and reads.</summary>
    public const int Revision = 1;

    internal RoomNameTurn(
        int turn,
        int entityCount,
        ImmutableArray<NamePairRef> pairs,
        ImmutableArray<NeedsRef> needs,
        ImmutableArray<FoldCandidate> candidates,
        ImmutableArray<string> warnings)
    {
        Turn = turn;
        EntityCount = entityCount;
        Pairs = pairs;
        Needs = needs;
        Candidates = candidates;
        Warnings = warnings;
    }

    /// <summary>The quarter turn, 0 to 3.</summary>
    public int Turn { get; }

    /// <summary>How many entities the room's list has, worldspawn included: what the indices are checked against.</summary>
    public int EntityCount { get; }

    /// <summary>Every key holding a local name, in entity then key order.</summary>
    public ImmutableArray<NamePairRef> Pairs { get; }

    /// <summary>Every <c>room_needs</c> key, in entity order.</summary>
    public ImmutableArray<NeedsRef> Needs { get; }

    /// <summary>Every fold candidate, in entity order.</summary>
    public ImmutableArray<FoldCandidate> Candidates { get; }

    /// <summary>The warnings the room compile gave, each a whole sentence.</summary>
    public ImmutableArray<string> Warnings { get; }

    /// <summary>Whether the room uses nothing of the naming: no local name, no <c>room_needs</c>, nothing a fold could touch.</summary>
    public bool IsEmpty => Pairs.IsEmpty && Needs.IsEmpty && Candidates.IsEmpty;

    /// <summary>The section tag for a turn's names.</summary>
    public static string Tag(int turn) => turn switch
    {
        0 => "NAM0",
        1 => "NAM1",
        2 => "NAM2",
        3 => "NAM3",
        _ => throw new ArgumentOutOfRangeException(nameof(turn), turn, "a quarter-turn count is 0 to 3"),
    };

    /// <summary>The pack section for this turn.</summary>
    public RoomPackSectionData ToSection(RoomLinkCodec codec = RoomLinkCodec.None)
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(EntityCount);
        w.Int(Pairs.Length);
        foreach (NamePairRef pair in Pairs)
        {
            w.Int(pair.Entity);
            w.Int(pair.Pair);
            w.Byte(pair.IsOutput ? (byte)1 : pair.IsTargetName ? (byte)2 : (byte)0);
            w.Int(pair.Segments.Length);
            foreach (NameSegment segment in pair.Segments)
            {
                if (segment.Literal is { } text)
                {
                    w.Byte(0);
                    w.String(text);
                }
                else
                {
                    w.Byte(1);
                    w.Byte(unchecked((byte)(sbyte)segment.Dx));
                    w.Byte(unchecked((byte)(sbyte)segment.Dy));
                    w.Byte((byte)segment.Field);
                    w.String(segment.Rest);
                }
            }
        }

        w.Int(Needs.Length);
        foreach (NeedsRef needs in Needs)
        {
            w.Int(needs.Entity);
            w.Int(needs.Pair);
            w.Int(needs.Conditions.Length);
            foreach (TurnedNeed need in needs.Conditions)
            {
                w.Byte((byte)need.Need.Direction);
                w.Byte((byte)((need.Need.Joined ? 1 : 0) | (need.Need.Negated ? 2 : 0)));
                w.Byte(unchecked((byte)(sbyte)need.Dx));
                w.Byte(unchecked((byte)(sbyte)need.Dy));
            }
        }

        w.Int(Candidates.Length);
        foreach (FoldCandidate candidate in Candidates)
        {
            w.Int(candidate.Entity);
            w.Byte((byte)candidate.Kind);
        }

        w.Int(Warnings.Length);
        foreach (string warning in Warnings)
        {
            w.String(warning);
        }

        return new RoomPackSectionData(Tag(Turn), RoomLinkSections.Encode(w.ToArray(), codec));
    }

    /// <summary>A turn's names from its section, or null when absent or of a revision this build does not read.</summary>
    /// <exception cref="LinkException">A codec this build does not read, or a payload out of shape.</exception>
    public static RoomNameTurn? Read(ArraySegment<byte>? section, string room, int turn)
    {
        string tag = Tag(turn);
        if (RoomLinkSections.Open(section, room, tag) is not { } r)
        {
            return null;
        }

        int entities = r.Int();
        if (entities < 0)
        {
            throw r.Mismatch($"{entities} entities");
        }

        ImmutableArray<NamePairRef>.Builder pairs = ImmutableArray.CreateBuilder<NamePairRef>(r.Count("named keys"));
        for (int i = pairs.Capacity; i > 0; i--)
        {
            int entity = Index(r, entities, "a named key of entity");
            int pair = Index(r, int.MaxValue, "a named key");
            int kind = r.Small(2, "a named key kind of");
            ImmutableArray<NameSegment>.Builder segments = ImmutableArray.CreateBuilder<NameSegment>(r.Count("pieces"));
            for (int s = segments.Capacity; s > 0; s--)
            {
                if (!r.Flag())
                {
                    segments.Add(NameSegment.Text(r.String()));
                    continue;
                }

                int dx = Offset(r);
                int dy = Offset(r);
                NameField field = (NameField)r.Small(2, "a name field of");
                segments.Add(new NameSegment(null, dx, dy, r.String(), field));
            }

            pairs.Add(new NamePairRef(entity, pair, kind == 1, segments.MoveToImmutable(), kind == 2));
        }

        ImmutableArray<NeedsRef>.Builder needs = ImmutableArray.CreateBuilder<NeedsRef>(r.Count("room_needs keys"));
        for (int i = needs.Capacity; i > 0; i--)
        {
            int entity = Index(r, entities, "a room_needs key of entity");
            int pair = Index(r, int.MaxValue, "a room_needs key");
            ImmutableArray<TurnedNeed>.Builder conditions = ImmutableArray.CreateBuilder<TurnedNeed>(r.Count("conditions"));
            for (int c = conditions.Capacity; c > 0; c--)
            {
                RoomDirection direction = (RoomDirection)r.Small(RoomDirections.Count - 1, "a direction of");
                int flags = r.Small(3, "condition flags of");
                conditions.Add(new TurnedNeed(new RoomNeed(direction, (flags & 1) != 0, (flags & 2) != 0), Offset(r), Offset(r)));
            }

            needs.Add(new NeedsRef(entity, pair, conditions.MoveToImmutable()));
        }

        ImmutableArray<FoldCandidate>.Builder candidates = ImmutableArray.CreateBuilder<FoldCandidate>(r.Count("fold candidates"));
        for (int i = candidates.Capacity; i > 0; i--)
        {
            candidates.Add(new FoldCandidate(Index(r, entities, "a fold candidate of entity"), (FoldKind)r.Small(4, "a fold kind of")));
        }

        ImmutableArray<string>.Builder warnings = ImmutableArray.CreateBuilder<string>(r.Count("warnings"));
        for (int i = warnings.Capacity; i > 0; i--)
        {
            warnings.Add(r.String());
        }

        r.End();
        return new RoomNameTurn(turn, entities, pairs.MoveToImmutable(), needs.MoveToImmutable(), candidates.MoveToImmutable(), warnings.MoveToImmutable());
    }

    private static int Index(RoomLinkSections.Reader r, int limit, string what)
    {
        int value = r.Int();
        return value >= 0 && value < limit ? value : throw r.Mismatch($"{what} {value}");
    }

    private static int Offset(RoomLinkSections.Reader r)
    {
        int value = unchecked((sbyte)(byte)r.Small(255, "an offset of"));
        return value is >= -1 and <= 1 ? value : throw r.Mismatch($"a cell offset of {value}");
    }
}

/// <summary>
/// A room's names for all four turns, bound to the compile they describe:
/// what the room compile makes (and <c>ssmap room</c> stores) and the link
/// reads in place of analysing the room's entities again.
/// </summary>
internal sealed class RoomNameTables
{
    private readonly RoomNameTurn?[] _turns;
    private readonly BspData? _bsp;

    internal RoomNameTables(RoomNameTurn?[] turns, BspData? bsp)
    {
        _turns = turns;
        _bsp = bsp;
    }

    /// <summary>A turn's names, or null when not held.</summary>
    public RoomNameTurn? Turn(int turn) => _turns[turn];

    /// <summary>Whether all four turns are held, as the room compile makes them (a pack load holds only the turns it read).</summary>
    public bool IsComplete => _turns.All(t => t is not null);

    /// <summary>Whether these names describe exactly this room's compile.</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The same names bound to another BSP: what the pack reader does once it has the room.</summary>
    public RoomNameTables For(BspData bsp) => new(_turns, bsp);

    /// <summary>The four sections, in turn order.</summary>
    public IEnumerable<RoomPackSectionData> Sections() => _turns.Select(t => t!.ToSection());
}

/// <summary>
/// Reads a room's entities for the naming grammar: the pack-time rule
/// (<see cref="RoomRule.LocalNamesWellFormed"/>) and the per-turn tables
/// the link fills in (<see cref="RoomNameTurn"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is read.</b> Every key of every entity but the worldspawn:
/// a key of the built-in table of name-valued keys (<see cref="IsNameKey"/>)
/// is a name whatever it holds; an output's target is a name, and so is its
/// parameter; any other key is a name only when its whole value is a local
/// one (the placeholder is explicit, so it cannot be mistaken for prose,
/// which removes most of the need for an FGD). A name that matches the
/// local pattern becomes a placeholder piece; one that begins like a
/// resolved name is refused as reserved, and a near miss of a placeholder as
/// malformed (<see cref="RoomNameGrammar"/>). A placeholder anywhere but at
/// the start of a value is a warning: it is a global name, or text such as
/// an <c>AddOutput</c> parameter, which the link does not look inside.
/// </para>
/// <para>
/// <b>Linker-owned names</b> (<see cref="RoomLinkerNames"/>): an entity
/// whose <c>targetname</c> is one must be of the class the linker expects,
/// and a flag must name a direction; an output that sends an input to the
/// room's <c>logic_room</c> must send one of the class's inputs, since the
/// stock fallback has nothing else to turn it into.
/// </para>
/// <para>
/// <b>room_needs</b> (<see cref="RoomNeeds"/>) must parse, and may not sit on
/// a baked light (its light is in the room's lighting and cannot be dropped),
/// on an <c>info_overlay</c> (a record of the room's compile, which the
/// accessors number), on an area portal (its brush is the room's world and
/// its number one of the room's portals) or on a <c>prop_static</c> that casts shadows (its
/// shadow would stay).
/// </para>
/// <para>
/// <b>Warnings</b> go with the room (the pack stores them, <c>ssmap room</c>
/// and <c>ssmap rooms</c> print them): a misplaced placeholder, and a local
/// name of the room's own that no entity of the room defines (a typo).
/// </para>
/// </remarks>
internal static class RoomNameAnalysis
{
    /// <summary>The classes whose light is baked: <c>room_needs</c> cannot drop them.</summary>
    private static readonly ImmutableHashSet<string> BakedLights =
        ImmutableHashSet.Create(StringComparer.Ordinal, "light", "light_spot", "light_environment", "light_directional");

    /// <summary>
    /// The built-in name-valued keys: keys whose value names an entity in
    /// the stock classes, whatever that value is. Compared ignoring case.
    /// </summary>
    private static readonly ImmutableHashSet<string> BuiltInNameKeys = BuildNameKeys();

    /// <summary>Whether a key is a built-in name-valued key or one the library adds.</summary>
    public static bool IsNameKey(string key, IReadOnlySet<string>? extra) =>
        BuiltInNameKeys.Contains(key) || (extra?.Contains(key) ?? false);

    /// <summary>The built-in name-valued keys, for listings and facts.</summary>
    public static IReadOnlySet<string> NameKeys => BuiltInNameKeys;

    /// <summary>
    /// Checks a room's entities and builds its four turns' tables.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="entities">The room's entity list, worldspawn included, in order.</param>
    /// <param name="nameKeys">Name-valued keys the library adds to the built-in table, or null.</param>
    /// <returns>The four turns' tables.</returns>
    /// <exception cref="RoomLintException">A name or a <c>room_needs</c> key breaks the rule; the message is the design's.</exception>
    public static RoomNameTurn[] Analyse(string room, IReadOnlyList<LevelEntity> entities, IReadOnlySet<string>? nameKeys)
    {
        ArgumentNullException.ThrowIfNull(entities);
        List<(int Entity, int Pair, int Kind, List<(string? Text, LocalName Name, NameField Field)> Pieces)> named = [];
        List<(int Entity, int Pair, List<RoomNeed> Needs)> needs = [];
        List<FoldCandidate> candidates = [];
        List<string> warnings = [];
        HashSet<string> defined = new(StringComparer.Ordinal);
        List<(LevelEntity Entity, string Key, string Value, string Name)> ownReferences = [];

        for (int e = 0; e < entities.Count; e++)
        {
            LevelEntity entity = entities[e];
            string className = entity.ClassName;
            if (string.Equals(className, "worldspawn", StringComparison.Ordinal))
            {
                continue;
            }

            string who = $"room {room}: entity {entity.Id} ({className})";
            for (int p = 0; p < entity.Pairs.Count; p++)
            {
                LevelPair pair = entity.Pairs[p];
                if (pair.Value is not { } value)
                {
                    continue;
                }

                if (RoomNeeds.IsKey(pair.Key))
                {
                    needs.Add((e, p, CheckNeeds(room, entity, className, value)));
                    continue;
                }

                if (RoomOutput.TryParse(value, out RoomOutput output))
                {
                    List<(string?, LocalName, NameField)> pieces = [];
                    bool local = false;
                    local |= Field(who, pair.Key, output.Target, NameField.Target, entity, ownReferences, warnings, out LocalName target);
                    local |= Field(who, pair.Key, output.Parameter, NameField.Parameter, entity, ownReferences, warnings, out LocalName parameter);
                    CheckLogicRoomInput(room, entity, className, pair.Key, output, target);
                    if (local)
                    {
                        string separator = output.Separator.ToString();
                        pieces.Add((target.Rest is null ? output.Target : null, target, NameField.Target));
                        pieces.Add((separator + output.Input + separator, default, NameField.Whole));
                        pieces.Add((parameter.Rest is null ? output.Parameter : null, parameter, NameField.Parameter));
                        pieces.Add((separator + output.Delay + separator + output.Times, default, NameField.Whole));
                        named.Add((e, p, 1, pieces));
                    }

                    continue;
                }

                bool nameKey = IsNameKey(pair.Key, nameKeys);
                if (!nameKey && !RoomNameGrammar.TryParseLocal(value, out _))
                {
                    if (RoomNameGrammar.HasPlaceholderAfterStart(value))
                    {
                        warnings.Add($"{who} key \"{pair.Key}\": \"{value}\" contains cxry after its start; it is a global name, since the placeholder is only read at the start of a name.");
                    }

                    continue;
                }

                if (Field(who, pair.Key, value, NameField.Whole, entity, ownReferences, warnings, out LocalName name))
                {
                    bool definesName = string.Equals(pair.Key, "targetname", StringComparison.OrdinalIgnoreCase);
                    named.Add((e, p, definesName ? 2 : 0, [(null, name, NameField.Whole)]));
                    if (string.Equals(pair.Key, "targetname", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckOwnedClass(room, entity, className, value, name);
                        if (name.Dx == 0 && name.Dy == 0)
                        {
                            defined.Add(name.Rest);
                        }
                    }
                }
            }

            if (FoldCandidateOf(entity, className) is FoldKind kind and not FoldKind.None)
            {
                candidates.Add(new FoldCandidate(e, kind));
            }
        }

        // A local name of the room's own that nothing defines: a typo, most
        // likely. Linker-owned names are the linker's to define, and a
        // wildcard need not match anything.
        foreach ((LevelEntity entity, string key, string value, string rest) in ownReferences)
        {
            if (!defined.Contains(rest) && RoomLinkerNames.Parse(rest).Kind == LinkerNameKind.None && !rest.EndsWith('*'))
            {
                warnings.Add($"room {room}: entity {entity.Id} ({entity.ClassName}) key \"{key}\" names {value}, which no entity of the room defines.");
            }
        }

        RoomNameTurn[] turns = new RoomNameTurn[4];
        for (int turn = 0; turn < 4; turn++)
        {
            turns[turn] = new RoomNameTurn(
                turn,
                entities.Count,
                [.. named.Select(n => new NamePairRef(n.Entity, n.Pair, n.Kind == 1, Pieces(n.Pieces, turn), n.Kind == 2))],
                [.. needs.Select(n => new NeedsRef(n.Entity, n.Pair, [.. n.Needs.Select(need => Turned(need, turn))]))],
                [.. candidates],
                [.. warnings]);
        }

        return turns;
    }

    /// <summary>
    /// The rule run on the room's VMF entities, before any compile time is
    /// spent: the compile consumes some classes (a <c>prop_static</c> becomes
    /// a static prop record), so only the VMF still shows a <c>room_needs</c>
    /// on one. The tables the link uses are built from the compile, whose
    /// entities are the VMF's with the same keys.
    /// </summary>
    /// <returns>The rule's warnings.</returns>
    /// <exception cref="RoomLintException">A name or a <c>room_needs</c> key breaks the rule.</exception>
    public static IReadOnlyList<string> CheckVmf(string room, IReadOnlyList<LevelEntity> entities, IReadOnlySet<string>? nameKeys) =>
        Analyse(room, entities, nameKeys)[0].Warnings;

    /// <summary>A level offset: the authored one turned by the placement's quarter turns, as geometry turns.</summary>
    public static (int Dx, int Dy) Turn(int dx, int dy, int turns)
    {
        Vec3 turned = RoomTransform.Rotate(new Vec3(dx, dy, 0), ((turns % 4) + 4) % 4);
        return ((int)turned.X, (int)turned.Y);
    }

    private static TurnedNeed Turned(RoomNeed need, int turn)
    {
        if (need.Joined)
        {
            return new TurnedNeed(need, 0, 0);
        }

        (int dx, int dy) = RoomDirections.Offset(need.Direction);
        (int tx, int ty) = Turn(dx, dy, turn);
        return new TurnedNeed(need, tx, ty);
    }

    private static ImmutableArray<NameSegment> Pieces(List<(string? Text, LocalName Name, NameField Field)> pieces, int turn)
    {
        ImmutableArray<NameSegment>.Builder segments = ImmutableArray.CreateBuilder<NameSegment>();
        foreach ((string? text, LocalName name, NameField field) in pieces)
        {
            if (text is not null)
            {
                // Adjacent text pieces merge, so an output with a local
                // target and a plain parameter is three pieces, not five.
                if (segments.Count > 0 && segments[^1].Literal is { } before)
                {
                    segments[^1] = NameSegment.Text(before + text);
                }
                else if (text.Length > 0)
                {
                    segments.Add(NameSegment.Text(text));
                }

                continue;
            }

            (int dx, int dy) = Turn(name.Dx, name.Dy, turn);
            segments.Add(new NameSegment(null, dx, dy, name.Rest, field));
        }

        return segments.ToImmutable();
    }

    /// <summary>
    /// One name read by the rules: true for a local name (returned taken
    /// apart); false for a global one; a reserved or malformed one is refused.
    /// </summary>
    private static bool Field(
        string who, string key, string value, NameField field, LevelEntity entity,
        List<(LevelEntity, string, string, string)> ownReferences, List<string> warnings, out LocalName name)
    {
        name = default;
        switch (RoomNameGrammar.Classify(value))
        {
            case RoomNameKind.Local:
                RoomNameGrammar.TryParseLocal(value, out name);
                if (RoomLinkerNames.Parse(name.Rest).Kind == LinkerNameKind.UnknownDirection)
                {
                    throw new RoomLintException(
                        $"{who} key \"{key}\": \"{value}\" names an unknown direction; has_ takes east, north, west, south, northeast,"
                        + " northwest, southwest or southeast, and joined_ takes east, north, west or south.");
                }

                if (field != NameField.Whole || !string.Equals(key, "targetname", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Dx == 0 && name.Dy == 0)
                    {
                        ownReferences.Add((entity, key, value, name.Rest));
                    }
                }

                return true;
            case RoomNameKind.Reserved:
                throw new RoomLintException(
                    $"{who} key \"{key}\": the global name \"{value}\" begins like a room-local or resolved name (c<column>r<row>_); rename it.");
            case RoomNameKind.Malformed:
                throw new RoomLintException(
                    $"{who} key \"{key}\": \"{value}\" is a malformed room-local name; a local name starts with cxry_, cx+1ry_, cx-1ry_,"
                    + " cxry+1_, cxry-1_ or a diagonal such as cx+1ry-1_, in lower case, followed by the name.");
            default:
                if (RoomNameGrammar.HasPlaceholderAfterStart(value))
                {
                    warnings.Add($"{who} key \"{key}\": \"{value}\" contains cxry after its start; it is a global name, since the placeholder is only read at the start of a name.");
                }

                // The default's rest is null: a global name, whose piece is its text.
                return false;
        }
    }

    /// <summary>An entity named with a linker-owned rest must be the class the linker expects.</summary>
    private static void CheckOwnedClass(string room, LevelEntity entity, string className, string value, LocalName name)
    {
        // A channel relay's name is the linker's alone: it writes one per
        // logic_room channel in the stock fallback, and an author's entity of
        // the same name would collide with it whatever its class.
        if (RoomLinkerNames.Parse(name.Rest).Kind == LinkerNameKind.Channel)
        {
            throw new RoomLintException(
                $"room {room}: entity {entity.Id} is a {className} named {value}; that name belongs to the logic_relay the linker writes for a logic_room channel.");
        }

        if (RoomLinkerNames.ExpectedClass(RoomLinkerNames.Parse(name.Rest)) is { } expected
            && !string.Equals(className, expected, StringComparison.Ordinal))
        {
            throw new RoomLintException($"room {room}: entity {entity.Id} is a {className} named {value}; that name belongs to a {expected}.");
        }
    }

    /// <summary>An output to the room's <c>logic_room</c> must send one of its inputs.</summary>
    private static void CheckLogicRoomInput(string room, LevelEntity entity, string className, string key, RoomOutput output, LocalName target)
    {
        if (target.Rest is not null
            && RoomLinkerNames.Parse(target.Rest).Kind == LinkerNameKind.Room
            && !LogicRoom.TryParseInput(output.Input, out _))
        {
            throw new RoomLintException(
                $"room {room}: entity {entity.Id} ({className}) output \"{key}\" sends {output.Input} to {output.Target},"
                + " which is not an input of logic_room.");
        }
    }

    /// <summary>A <c>room_needs</c> key checked: it parses, and does not sit on a baked light, an overlay or a shadow-casting static prop.</summary>
    private static List<RoomNeed> CheckNeeds(string room, LevelEntity entity, string className, string value)
    {
        if (!RoomNeeds.TryParse(value, out List<RoomNeed> needs, out string? unknown))
        {
            throw new RoomLintException(
                $"room {room}: entity {entity.Id} ({className}) room_needs \"{value}\": unknown direction \"{unknown}\";"
                + " use east, west, north, south, a diagonal, or joined_ with a side, optionally negated with !.");
        }

        if (BakedLights.Contains(className))
        {
            throw new RoomLintException(
                $"room {room}: entity {entity.Id} ({className}) has room_needs, but a light's contribution is in the room's baked lighting and cannot be dropped.");
        }

        // An overlay is a record of its room's compile, not an entity the
        // resolver can leave out (an unnamed one has no entity at all once
        // compiled), and dropping it would renumber every later overlay the
        // accessors name; the flattened compile would drop it, the link
        // would keep it. Refused, like a baked light.
        if (string.Equals(className, "info_overlay", StringComparison.Ordinal))
        {
            throw new RoomLintException(
                $"room {room}: entity {entity.Id} (info_overlay) has room_needs, but an overlay is built into its room's compile and cannot be dropped.");
        }

        // An area portal's brush is moved into its room's world by vbsp and
        // its number is one of the room's portals, so the link cannot leave
        // it out while the flattened compile would (and would renumber every
        // later portal). Refused, like an overlay.
        if (MapFileLoader.IsAreaPortal(className))
        {
            throw new RoomLintException(
                $"room {room}: entity {entity.Id} ({className}) has room_needs, but an area portal is built into its room's compile and cannot be dropped.");
        }

        // A detail prop entity is consumed by vbsp into its room's detail
        // prop lump, where no key survives, so the link cannot leave it out
        // while the flattened compile would. Refused, like an overlay.
        if (className is "prop_detail" or "prop_detail_sprite" or "detail_prop")
        {
            throw new RoomLintException(
                $"room {room}: entity {entity.Id} ({className}) has room_needs, but a detail prop is built into its room's compile and cannot be dropped.");
        }

        if (string.Equals(className, "prop_static", StringComparison.Ordinal)
            && !string.Equals(entity.Get("disableshadows")?.Trim(), "1", StringComparison.Ordinal))
        {
            throw new RoomLintException($"room {room}: prop_static {entity.Id} has room_needs and casts shadows; set disableshadows or remove room_needs.");
        }

        return needs;
    }

    /// <summary>
    /// What a fold could make of an entity, from its own keys: the half of
    /// the fold analysis that does not depend on the level. The rules are
    /// <see cref="LevelLogicFolder"/>'s.
    /// </summary>
    internal static FoldKind FoldCandidateOf(LevelEntity entity, string className)
    {
        string? name = entity.TargetName;
        bool local = RoomNameGrammar.TryParseLocal(name, out _);
        bool parented = !string.IsNullOrEmpty(entity.Get("parentname"));
        switch (className)
        {
            case "logic_relay" when local && !parented:
                return RoomLogicRules.RelayCanFold(entity) ? FoldKind.Relay : FoldKind.None;
            case "logic_branch" when local && !parented:
                return RoomLogicRules.BranchCanFold(entity) ? FoldKind.Branch : FoldKind.None;
            case "logic_auto" when string.IsNullOrEmpty(name) && !parented && string.IsNullOrEmpty(entity.Get("globalstate")):
                return FoldKind.Auto;
            default:
                return className.StartsWith("filter_", StringComparison.Ordinal) && local && !parented ? FoldKind.Filter : FoldKind.None;
        }
    }

    /// <summary>A room's tables from its compiled entity lump.</summary>
    public static RoomNameTurn[] Analyse(string room, BspData bsp, IReadOnlySet<string>? nameKeys)
    {
        List<BspEntity> parsed = EntityLump.Parse(bsp[BspLump.Entities]);
        return Analyse(room, [.. parsed.Select((e, i) => LevelEntity.FromBsp(e, -1, i))], nameKeys);
    }

    private static ImmutableHashSet<string> BuildNameKeys()
    {
        List<string> keys =
        [
            "targetname", "parentname", "target", "filtername", "damagefilter", "lightingorigin",
            "LightingOriginHack", "NextKey", "LaserTarget", "LightningStart", "LightningEnd", "altpath",
            "SourceEntityName", "EntityTemplate", "master", "MeasureTarget", "MeasureReference",
            "TargetReference", "glow", "spawntarget",
        ];

        for (int i = 1; i <= 16; i++)
        {
            keys.Add(string.Create(CultureInfo.InvariantCulture, $"Template{i:00}"));
        }

        for (int i = 1; i <= 10; i++)
        {
            keys.Add(string.Create(CultureInfo.InvariantCulture, $"Filter{i:00}"));
        }

        return keys.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
