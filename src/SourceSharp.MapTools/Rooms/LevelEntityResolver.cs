//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>One placement as the naming resolver sees it.</summary>
internal sealed class ResolverRoom
{
    /// <summary>The library room's name.</summary>
    public required string Room { get; init; }

    /// <summary>The level column, from the west, 0-based.</summary>
    public required int Column { get; init; }

    /// <summary>The level row, from the south, 0-based.</summary>
    public required int Row { get; init; }

    /// <summary>The placement's quarter turns, 0 to 3.</summary>
    public required int Turns { get; init; }

    /// <summary>The room's names for this placement's turn.</summary>
    public required RoomNameTurn Names { get; init; }

    /// <summary>The room's entity list, in the order the names index it; edited in place.</summary>
    public required List<LevelEntity> Entities { get; init; }

    /// <summary>The placement's joined sockets, by authored side.</summary>
    public required JoinedMask Joined { get; init; }

    /// <summary>The cell centre, written as an <c>origin</c> value: where an entity the linker writes stands.</summary>
    public required string CellCentre { get; init; }

    /// <summary>The section tag the names came from, for messages about a stored table that does not fit.</summary>
    public string NamesTag => RoomNameTurn.Tag(Turns);
}

/// <summary>What the resolver is asked to do beyond resolving names.</summary>
/// <param name="ModEntities">Emit the mod's classes (<c>-mod-entities</c>) rather than their stock fallbacks.</param>
/// <param name="FoldLogic">Fold stateless logic away (the library's <c>rooms_fold_logic</c>, on by default).</param>
/// <param name="Columns">The level's columns, or null when unknown (only a negative column is then off the grid).</param>
/// <param name="Rows">The level's rows, or null when unknown.</param>
internal sealed record LevelNamingOptions(bool ModEntities, bool FoldLogic, int? Columns, int? Rows);

/// <summary>What the resolver made of a level's entities.</summary>
internal sealed class LevelResolution
{
    /// <summary>Every entity of every placement that is still there, in level order: placement by placement, the room's in its order, then what the linker wrote for it.</summary>
    public required List<LevelEntity> Entities { get; init; }

    /// <summary>The warnings, each a whole sentence: references to empty cells, global names repeated across placements.</summary>
    public required List<string> Warnings { get; init; }

    /// <summary>What only verbose output reports: references to entities <c>room_needs</c> dropped on purpose.</summary>
    public required List<string> Verbose { get; init; }

    /// <summary>The worldspawn keys the level carries: the emission mode's, with <c>-mod-entities</c> only.</summary>
    public required List<(string Key, string Value)> WorldKeys { get; init; }
}

/// <summary>
/// The one resolver the link and the flatten share: room-local names
/// resolved to their cells, the three missing-neighbour mechanisms, the
/// <c>logic_room</c> hub or its stock fallback, and the fold.
/// </summary>
/// <remarks>
/// <para>
/// <b>One resolver, two paths.</b> The link runs it on the compiled rooms'
/// entity lumps, the flatten on the rooms' VMF entities before vbsp compiles
/// the level whole; both hand it a list of keys per entity and write back
/// what it leaves, so the two maps carry the same entities with the same
/// resolved keys, byte for byte. Nothing here reads a position: names are
/// never positions, and an entity the linker writes stands at its cell's
/// centre, which the caller supplies as text.
/// </para>
/// <para>
/// <b>The order of the steps</b> is the design's. (c) first:
/// <c>room_needs</c> keeps or drops each entity in its placement, and the
/// key never reaches the map. Then the names: each placeholder becomes
/// <c>c&lt;column&gt;r&lt;row&gt;_</c> for the cell its turned offset reaches.
/// A reference to an empty cell or off the grid is (a): warned of, and the
/// output removed or the key cleared; a reference to an entity that
/// <c>room_needs</c> dropped goes the same way, reported only under verbose
/// output because the drop was meant. Then (b): every neighbour and joined
/// flag a placement names (or places) gets its <c>InitialValue</c>, and
/// one nobody placed is written as a bare <c>logic_branch</c>, so a room
/// pays for no flag it does not use. Then the placement's
/// <c>logic_room</c>: filled in with <c>-mod-entities</c>, or expanded into
/// its stock fallback without (a branch per direction it tests, a relay per
/// channel it uses). Then the fold, to a fixed point
/// (<see cref="LevelLogicFolder"/>); with <c>-mod-entities</c>, the flags and
/// relays that did not fold merge into the placement's <c>logic_room</c>, and
/// the fold runs once more.
/// </para>
/// <para>
/// <b>What the linker writes, and where.</b> A placement's written entities
/// (bare flags, a <c>logic_room</c>, the stock fallback's branches and
/// relays) follow its own, in a fixed order: flags by kind then direction,
/// then the hub, then the fallback's relays by channel. Nothing is written
/// that a room does not ask for (decision D7).
/// </para>
/// <para>
/// <b>Nothing is kept.</b> The resolver edits the lists it is given and
/// returns; it holds nothing between calls, so levels resolved at once in
/// one process share nothing.
/// </para>
/// </remarks>
internal static class LevelEntityResolver
{
    /// <summary>The longest value the engine is believed to read, in bytes (1024 with its terminator; uncertain).</summary>
    public const int MaxValueBytes = 1023;

    /// <summary>Resolves a level's entities.</summary>
    /// <param name="rooms">The placements, in link order.</param>
    /// <param name="options">The emission mode, the fold switch, the grid.</param>
    /// <returns>The resolved entities and what was warned of.</returns>
    /// <exception cref="LinkException">A resolved value is too long, a resolved name equals a global one, or a stored table does not fit its room.</exception>
    public static LevelResolution Resolve(IReadOnlyList<ResolverRoom> rooms, LevelNamingOptions options)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(options);
        Dictionary<(int, int), int> cells = new(rooms.Count);
        for (int i = 0; i < rooms.Count; i++)
        {
            cells[(rooms[i].Column, rooms[i].Row)] = i;
            if (rooms[i].Names.EntityCount != rooms[i].Entities.Count)
            {
                throw Mismatch(rooms[i], $"a table for {rooms[i].Names.EntityCount} entities; the room has {rooms[i].Entities.Count}");
            }
        }

        Level level = new(rooms, options, cells);
        foreach (RoomState room in level.States)
        {
            level.ApplyNeeds(room);
        }

        foreach (RoomState room in level.States)
        {
            level.CollectDefinitions(room);
        }

        foreach (RoomState room in level.States)
        {
            level.ResolveNames(room);
        }

        level.CheckGlobalNames();
        foreach (RoomState room in level.States)
        {
            level.WriteFlags(room);
        }

        foreach (RoomState room in level.States)
        {
            if (options.ModEntities)
            {
                level.FillLogicRoom(room);
            }
            else
            {
                level.ExpandLogicRoom(room);
            }
        }

        List<LevelEntity> all = level.Ordered();
        if (options.FoldLogic)
        {
            LevelLogicFolder.Fold(all, level.Candidates, foldRooms: false);
        }

        if (options.ModEntities)
        {
            if (level.MergeIntoLogicRooms(all))
            {
                all = level.Ordered();
            }

            if (options.FoldLogic)
            {
                LevelLogicFolder.Fold(all, level.Candidates, foldRooms: true);
            }
        }

        // Removed keys go, and every entity's keys come before its outputs,
        // in order within each: the order vbsp writes a VMF entity in (its
        // keys, then its connections), so a key the resolver added after an
        // output sits where the flattened level's compile puts it too.
        foreach (LevelEntity entity in all)
        {
            entity.Pairs.RemoveAll(p => p.Deleted);
            List<LevelPair> outputs = [.. entity.Pairs.Where(p => RoomOutput.TryParse(p.Value, out _))];
            if (outputs.Count > 0)
            {
                entity.Pairs.RemoveAll(p => RoomOutput.TryParse(p.Value, out _));
                entity.Pairs.AddRange(outputs);
            }
        }

        return new LevelResolution
        {
            Entities = [.. all.Where(e => !e.Removed)],
            Warnings = level.Warnings,
            Verbose = level.Verbose,
            WorldKeys = options.ModEntities
                ? [(ModEntityContract.EntitiesKey, ModEntityContract.Mod),
                   (ModEntityContract.VersionKey, ModEntityContract.Version.ToString(CultureInfo.InvariantCulture))]
                : [],
        };
    }

    /// <summary>Whether a name is <c>worldspawn</c>'s class: the resolver never touches the world.</summary>
    internal static bool IsWorld(LevelEntity entity) => string.Equals(entity.ClassName, "worldspawn", StringComparison.Ordinal);

    private static LinkException Mismatch(ResolverRoom room, string what) =>
        new($"room pack entry \"{room.Room}\": its \"{room.NamesTag}\" section holds {what}.");

    /// <summary>A cell's name prefix, negative numbers allowed: what a message shows for a cell off the grid.</summary>
    private static string Prefix(int x, int y) => string.Create(CultureInfo.InvariantCulture, $"c{x}r{y}_");

    /// <summary>One placement's working state.</summary>
    private sealed class RoomState(ResolverRoom room, int index)
    {
        public ResolverRoom Room { get; } = room;

        public int Index { get; } = index;

        /// <summary>Local names (rests) of the room's own that a kept entity defines.</summary>
        public HashSet<string> Kept { get; } = new(StringComparer.Ordinal);

        /// <summary>Local names that only entities <c>room_needs</c> dropped define.</summary>
        public HashSet<string> Dropped { get; } = new(StringComparer.Ordinal);

        /// <summary>The flags (rests) something names in this placement.</summary>
        public SortedSet<string> FlagRequests { get; } = new(StringComparer.Ordinal);

        /// <summary>Whether something names this placement's <c>logic_room</c>.</summary>
        public bool RoomRequested { get; set; }

        /// <summary>What the linker writes for the placement, in order.</summary>
        public List<LevelEntity> Written { get; } = [];

        /// <summary>The placement's <c>logic_room</c>, authored or written, once there is one.</summary>
        public LevelEntity? Hub { get; set; }

        public string Cell => string.Create(CultureInfo.InvariantCulture, $"({Room.Column}, {Room.Row})");

        public string Name(string rest) => RoomNameGrammar.Resolve(Room.Column, Room.Row, rest);
    }

    /// <summary>The level being resolved: the placements, the grid, and what has been warned of.</summary>
    private sealed class Level(IReadOnlyList<ResolverRoom> rooms, LevelNamingOptions options, Dictionary<(int, int), int> cells)
    {
        public RoomState[] States { get; } = [.. rooms.Select((r, i) => new RoomState(r, i))];

        public List<string> Warnings { get; } = [];

        public List<string> Verbose { get; } = [];

        /// <summary>Every entity the fold may consider, with what it may do: the rooms' stored candidates and the entities the linker wrote.</summary>
        public Dictionary<LevelEntity, FoldKind> Candidates { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every entity in level order: each placement's own, then what the linker wrote for it.</summary>
        public List<LevelEntity> Ordered()
        {
            List<LevelEntity> all = [];
            foreach (RoomState room in States)
            {
                foreach (LevelEntity entity in room.Room.Entities)
                {
                    if (!entity.Removed && !IsWorld(entity))
                    {
                        all.Add(entity);
                    }
                }

                all.AddRange(room.Written.Where(e => !e.Removed));
            }

            return all;
        }

        /// <summary>(c): keeps or drops each entity with a <c>room_needs</c> key, and takes the key off every kept one.</summary>
        public void ApplyNeeds(RoomState state)
        {
            ResolverRoom room = state.Room;
            foreach (FoldCandidate candidate in room.Names.Candidates)
            {
                Candidates[room.Entities[candidate.Entity]] = candidate.Kind;
            }

            foreach (NeedsRef needs in room.Names.Needs)
            {
                LevelEntity entity = room.Entities[needs.Entity];
                LevelPair pair = PairOf(state, needs.Entity, needs.Pair);
                bool holds = true;
                foreach (TurnedNeed condition in needs.Conditions)
                {
                    bool value = condition.Need.Joined
                        ? (room.Joined & RoomDirections.JoinedBit(condition.Need.Direction)) != 0
                        : cells.ContainsKey((room.Column + condition.Dx, room.Row + condition.Dy));
                    holds &= value != condition.Need.Negated;
                }

                if (holds)
                {
                    pair.Deleted = true;
                }
                else
                {
                    entity.Removed = true;
                }
            }
        }

        /// <summary>The room's own local names, split by whether a kept or only a dropped entity defines them.</summary>
        public void CollectDefinitions(RoomState state)
        {
            foreach (NamePairRef named in state.Room.Names.Pairs)
            {
                LevelEntity entity = state.Room.Entities[named.Entity];
                _ = PairOf(state, named.Entity, named.Pair);
                if (!named.IsTargetName
                    || named.Segments is not [{ Literal: null, Dx: 0, Dy: 0 } segment])
                {
                    continue;
                }

                if (!entity.Removed)
                {
                    state.Kept.Add(segment.Rest);
                }
                else
                {
                    state.Dropped.Add(segment.Rest);
                }
            }

            state.Dropped.ExceptWith(state.Kept);
        }

        /// <summary>
        /// Fills in every local name of a placement, removing what names an
        /// empty cell (a) or a dropped entity, and records the flags and hubs
        /// it names for the placements they belong to.
        /// </summary>
        public void ResolveNames(RoomState state)
        {
            ResolverRoom room = state.Room;
            StringBuilder text = new();
            foreach (NamePairRef named in room.Names.Pairs)
            {
                LevelEntity entity = room.Entities[named.Entity];
                LevelPair pair = PairOf(state, named.Entity, named.Pair);
                if (entity.Removed || pair.Deleted)
                {
                    continue;
                }

                text.Clear();
                string? missing = null;
                foreach (NameSegment segment in named.Segments)
                {
                    if (segment.Literal is { } literal)
                    {
                        text.Append(literal);
                        continue;
                    }

                    int x = room.Column + segment.Dx;
                    int y = room.Row + segment.Dy;
                    string resolved = Prefix(x, y) + segment.Rest;
                    RoomState? target = segment.Dx == 0 && segment.Dy == 0
                        ? state
                        : cells.TryGetValue((x, y), out int index) ? States[index] : null;
                    if (target is null)
                    {
                        bool offGrid = x < 0 || y < 0 || x >= (options.Columns ?? int.MaxValue) || y >= (options.Rows ?? int.MaxValue);
                        missing = string.Create(CultureInfo.InvariantCulture,
                            $"room {room.Room} at cell ({room.Column}, {room.Row}): entity {DisplayName(state, entity)} ({entity.ClassName}) key \"{pair.Key}\""
                            + $" names {resolved}, but cell ({x}, {y}) {(offGrid ? "is off the grid" : "holds no room")};"
                            + $" the {(named.IsOutput ? "output was removed" : "key was cleared")}.");
                        Warnings.Add(missing);
                        break;
                    }

                    LinkerName owned = RoomLinkerNames.Parse(segment.Rest);
                    if (owned.Kind is LinkerNameKind.Has or LinkerNameKind.Joined)
                    {
                        target.FlagRequests.Add(segment.Rest);
                    }
                    else if (owned.Kind == LinkerNameKind.Room)
                    {
                        target.RoomRequested = true;
                    }
                    else if (!segment.Rest.EndsWith('*') && target.Dropped.Contains(segment.Rest))
                    {
                        missing = string.Create(CultureInfo.InvariantCulture,
                            $"room {room.Room} at cell ({room.Column}, {room.Row}): entity {DisplayName(state, entity)} ({entity.ClassName}) key \"{pair.Key}\""
                            + $" names {resolved}, which room_needs dropped; the {(named.IsOutput ? "output was removed" : "key was cleared")}.");
                        Verbose.Add(missing);
                        break;
                    }

                    text.Append(resolved);
                }

                if (missing is not null)
                {
                    pair.Deleted = true;
                    continue;
                }

                // The entity lump is Latin-1, one byte a character, so the
                // length is the byte count the engine reads.
                if (text.Length > MaxValueBytes)
                {
                    throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                        $"room {room.Room} at cell ({room.Column}, {room.Row}): entity {DisplayName(state, entity)} ({entity.ClassName}) key \"{pair.Key}\""
                        + $" resolves to {text.Length} bytes; the engine reads at most {MaxValueBytes}."));
                }

                pair.Value = text.ToString();
            }

            // A flag or hub the room places itself is used, named or not.
            foreach (LevelEntity entity in room.Entities)
            {
                if (entity.Removed || IsWorld(entity)
                    || !RoomNameGrammar.TryParseResolved(entity.TargetName, out int column, out int row, out string rest)
                    || column != room.Column || row != room.Row)
                {
                    continue;
                }

                LinkerName owned = RoomLinkerNames.Parse(rest);
                if (owned.Kind is LinkerNameKind.Has or LinkerNameKind.Joined)
                {
                    state.FlagRequests.Add(rest);
                }
                else if (owned.Kind == LinkerNameKind.Room)
                {
                    state.RoomRequested = true;
                    state.Hub ??= entity;
                }
            }
        }

        /// <summary>
        /// The duplicate-global warning (a room placed more than once defines
        /// its global names more than once), and the assertion that no
        /// resolved name equals a global one.
        /// </summary>
        public void CheckGlobalNames()
        {
            Dictionary<string, List<RoomState>> global = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<(string Room, string Name), List<RoomState>> byRoom = [];
            List<(string Name, RoomState Owner)> resolved = [];
            foreach (RoomState state in States)
            {
                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (LevelEntity entity in state.Room.Entities)
                {
                    if (entity.Removed || IsWorld(entity) || entity.Find("targetname") is not int at
                        || entity.Pairs[at].Deleted || entity.Pairs[at].Value is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    if (IsResolvedName(state, entity, at))
                    {
                        resolved.Add((name, state));
                        continue;
                    }

                    if (!global.TryGetValue(name, out List<RoomState>? owners))
                    {
                        global[name] = owners = [];
                    }

                    owners.Add(state);
                    if (seen.Add(name))
                    {
                        (string, string) key = (state.Room.Room, name);
                        if (!byRoom.TryGetValue(key, out List<RoomState>? placements))
                        {
                            byRoom[key] = placements = [];
                        }

                        placements.Add(state);
                    }
                }
            }

            foreach (((string room, string name), List<RoomState> placements) in byRoom)
            {
                if (placements.Count > 1)
                {
                    Warnings.Add(string.Create(CultureInfo.InvariantCulture,
                        $"the global name \"{name}\" is defined by {placements.Count} placements of room {room}, at cells {string.Join(", ", placements.Select(p => p.Cell))}."));
                }
            }

            foreach ((string name, RoomState owner) in resolved)
            {
                if (global.TryGetValue(name, out List<RoomState>? owners))
                {
                    throw new LinkException(
                        $"room {owner.Room.Room} at cell {owner.Cell}: its resolved name {name} equals the global name of room {owners[0].Room.Room}"
                        + $" at cell {owners[0].Cell}; a global name may not begin like a resolved one.");
                }
            }
        }

        /// <summary>(b): the flags a placement uses get their value; one it did not place is written bare.</summary>
        public void WriteFlags(RoomState state)
        {
            ResolverRoom room = state.Room;
            foreach (string rest in state.FlagRequests)
            {
                LinkerName flag = RoomLinkerNames.Parse(rest);
                string value = FlagValue(state, flag) ? "1" : "0";
                string name = state.Name(rest);
                if (Authored(state, name) is { } branch)
                {
                    branch.Set("InitialValue", value);
                    continue;
                }

                LevelEntity written = Write(state,
                    ("classname", RoomLinkerNames.FlagClass), ("targetname", name), ("InitialValue", value), ("origin", room.CellCentre));
                Candidates[written] = FoldKind.Branch;
            }
        }

        /// <summary>With <c>-mod-entities</c>: the placement's <c>logic_room</c>, authored or written, with its keys filled in.</summary>
        public void FillLogicRoom(RoomState state)
        {
            if (!state.RoomRequested)
            {
                return;
            }

            state.Hub ??= Write(state, ("classname", LogicRoom.ClassName), ("targetname", state.Name(RoomLinkerNames.Room)), ("origin", state.Room.CellCentre));
            FillHub(state, state.Hub);
        }

        /// <summary>
        /// Without <c>-mod-entities</c>: the placement's <c>logic_room</c>
        /// expanded into stock entities. A branch per direction the room tests
        /// or has outputs for (the flag's own, authored or written), a relay
        /// per channel it uses; every output to the hub rewritten to the
        /// stock entity and input; the hub removed.
        /// </summary>
        public void ExpandLogicRoom(RoomState state)
        {
            if (!state.RoomRequested)
            {
                return;
            }

            ResolverRoom room = state.Room;
            string hubName = state.Name(RoomLinkerNames.Room);
            LevelEntity? hub = state.Hub;

            // What the hub is asked and what it answers: by direction and
            // side (a flag rest each) and by channel.
            SortedDictionary<string, List<LevelPair>> flagOutputs = new(StringComparer.Ordinal);
            SortedDictionary<int, List<LevelPair>> channelOutputs = [];
            SortedDictionary<int, RelayFlags> channelFlags = [];
            List<(LevelPair Pair, RoomOutput Output, LogicRoomInput Input)> callers = [];
            if (hub is not null)
            {
                hub.Removed = true;
                foreach (LevelPair pair in hub.Pairs)
                {
                    if (pair.Deleted || pair.Value is not { } value)
                    {
                        continue;
                    }

                    if (RoomOutput.TryParse(value, out _) && LogicRoom.TryParseOutput(pair.Key, out LogicRoomOutput output))
                    {
                        if (output.Kind == LogicRoomOutputKind.Trigger)
                        {
                            Add(channelOutputs, output.Channel, pair);
                        }
                        else
                        {
                            string rest = output.Kind is LogicRoomOutputKind.JoinedTrue or LogicRoomOutputKind.JoinedFalse
                                ? RoomLinkerNames.JoinedRest(output.Direction)
                                : RoomLinkerNames.HasRest(output.Direction);
                            Add(flagOutputs, rest, pair);
                        }
                    }
                    else if (TryChannelFlags(pair, out int channel, out RelayFlags flags))
                    {
                        channelFlags[channel] = flags;
                        channelOutputs.TryAdd(channel, []);
                    }
                }
            }

            foreach (LevelEntity entity in Ordered())
            {
                foreach (LevelPair pair in entity.Pairs)
                {
                    if (!pair.Deleted && RoomOutput.TryParse(pair.Value, out RoomOutput output)
                        && string.Equals(output.Target, hubName, StringComparison.OrdinalIgnoreCase)
                        && LogicRoom.TryParseInput(output.Input, out LogicRoomInput input))
                    {
                        callers.Add((pair, output, input));
                        if (input.Kind == LogicRoomInputKind.Test)
                        {
                            flagOutputs.TryAdd(RoomLinkerNames.HasRest(input.Direction), []);
                        }
                        else if (input.Kind == LogicRoomInputKind.TestJoined)
                        {
                            flagOutputs.TryAdd(RoomLinkerNames.JoinedRest(input.Direction), []);
                        }
                        else
                        {
                            channelOutputs.TryAdd(input.Channel, []);
                        }
                    }
                }
            }

            // A branch per flag, reusing one the room placed or (b) wrote.
            foreach ((string rest, List<LevelPair> outputs) in flagOutputs)
            {
                string name = state.Name(rest);
                LevelEntity branch = Authored(state, name) ?? state.Written.FirstOrDefault(e => Named(e, name))
                    ?? WriteFlag(state, name, rest);
                foreach (LevelPair pair in outputs)
                {
                    LogicRoom.TryParseOutput(pair.Key, out LogicRoomOutput output);
                    bool value = output.Kind is LogicRoomOutputKind.NeighbourTrue or LogicRoomOutputKind.JoinedTrue;
                    branch.Pairs.Add(new LevelPair(value ? "OnTrue" : "OnFalse", pair.Value!, isConnection: true));
                }
            }

            // A relay per channel, after the flags.
            foreach ((int channel, List<LevelPair> outputs) in channelOutputs)
            {
                RelayFlags flags = channelFlags.GetValueOrDefault(channel);
                int spawnflags = ((flags & RelayFlags.FireOnce) != 0 ? 1 : 0) | ((flags & RelayFlags.FastRetrigger) != 0 ? 2 : 0);
                List<(string, string)> keys =
                [
                    ("classname", RoomLinkerNames.ChannelClass),
                    ("targetname", state.Name(RoomLinkerNames.ChannelRest(channel))),
                    ("spawnflags", spawnflags.ToString(CultureInfo.InvariantCulture)),
                ];
                if ((flags & RelayFlags.StartDisabled) != 0)
                {
                    keys.Add(("StartDisabled", "1"));
                }

                keys.Add(("origin", room.CellCentre));
                LevelEntity relay = Write(state, [.. keys]);
                foreach (LevelPair pair in outputs)
                {
                    relay.Pairs.Add(new LevelPair("OnTrigger", pair.Value!, isConnection: true));
                }

                if (RoomLogicRules.RelayCanFold(relay))
                {
                    Candidates[relay] = FoldKind.Relay;
                }
            }

            foreach ((LevelPair pair, RoomOutput output, LogicRoomInput input) in callers)
            {
                (string target, string stockInput) = input.Kind switch
                {
                    LogicRoomInputKind.Test => (state.Name(RoomLinkerNames.HasRest(input.Direction)), "Test"),
                    LogicRoomInputKind.TestJoined => (state.Name(RoomLinkerNames.JoinedRest(input.Direction)), "Test"),
                    LogicRoomInputKind.Trigger => (state.Name(RoomLinkerNames.ChannelRest(input.Channel)), "Trigger"),
                    LogicRoomInputKind.Enable => (state.Name(RoomLinkerNames.ChannelRest(input.Channel)), "Enable"),
                    LogicRoomInputKind.Disable => (state.Name(RoomLinkerNames.ChannelRest(input.Channel)), "Disable"),
                    _ => (state.Name(RoomLinkerNames.ChannelRest(input.Channel)), "Toggle"),
                };
                pair.Value = (output with { Target = target, Input = stockInput }).Format();
            }
        }

        /// <summary>
        /// With <c>-mod-entities</c>, after the first fold: every flag branch
        /// and local relay of a placement that did not fold and can be merged
        /// goes into the placement's <c>logic_room</c> (written if the room has
        /// none), its callers rewritten to the hub's inputs and its outputs
        /// moved to the hub's. At most eight relays; the rest stay relays.
        /// </summary>
        /// <returns>Whether anything was written or merged.</returns>
        public bool MergeIntoLogicRooms(List<LevelEntity> all)
        {
            LevelReferences references = LevelReferences.Of(all);
            bool changed = false;
            foreach (RoomState state in States)
            {
                List<(LevelEntity Branch, LinkerName Flag)> flags = [];
                List<LevelEntity> relays = [];
                foreach (LevelEntity entity in state.Room.Entities.Concat(state.Written))
                {
                    if (entity.Removed || IsWorld(entity)
                        || !RoomNameGrammar.TryParseResolved(entity.TargetName, out int column, out int row, out string rest)
                        || column != state.Room.Column || row != state.Room.Row)
                    {
                        continue;
                    }

                    LinkerName owned = RoomLinkerNames.Parse(rest);
                    if (owned.Kind is LinkerNameKind.Has or LinkerNameKind.Joined
                        && RoomLogicRules.CanMergeFlag(entity, references))
                    {
                        flags.Add((entity, owned));
                    }
                    else if (owned.Kind == LinkerNameKind.None
                        && string.Equals(entity.ClassName, "logic_relay", StringComparison.Ordinal)
                        && RoomLogicRules.CanMergeRelay(entity, references))
                    {
                        relays.Add(entity);
                    }
                }

                if (flags.Count == 0 && relays.Count == 0)
                {
                    continue;
                }

                if (state.Hub is null)
                {
                    state.Hub = Write(state, ("classname", LogicRoom.ClassName), ("targetname", state.Name(RoomLinkerNames.Room)), ("origin", state.Room.CellCentre));
                    FillHub(state, state.Hub);
                    all.Add(state.Hub);
                }

                LevelEntity hub = state.Hub;
                string hubName = hub.TargetName!;
                foreach ((LevelEntity branch, LinkerName flag) in flags)
                {
                    string input = flag.Kind == LinkerNameKind.Has ? LogicRoom.TestInput(flag.Direction) : LogicRoom.TestJoinedInput(flag.Direction);
                    foreach (LevelPair caller in references.Callers(branch))
                    {
                        RoomOutput.TryParse(caller.Value, out RoomOutput output);
                        caller.Value = (output with { Target = hubName, Input = input }).Format();
                    }

                    foreach (LevelPair pair in branch.Pairs.Where(p => !p.Deleted && RoomOutput.TryParse(p.Value, out _)))
                    {
                        bool value = string.Equals(pair.Key, "OnTrue", StringComparison.OrdinalIgnoreCase);
                        string key = flag.Kind == LinkerNameKind.Has
                            ? LogicRoom.NeighbourOutput(flag.Direction, value)
                            : LogicRoom.JoinedOutput(flag.Direction, value);
                        hub.Pairs.Add(new LevelPair(key, pair.Value!, isConnection: true));
                    }

                    branch.Removed = true;
                    changed = true;
                }

                SortedSet<int> used = RoomLogicRules.UsedChannels(hub, references);
                foreach (LevelEntity relay in relays)
                {
                    int channel = Enumerable.Range(1, LogicRoom.Channels).FirstOrDefault(c => !used.Contains(c));
                    if (channel == 0)
                    {
                        break;
                    }

                    used.Add(channel);
                    foreach (LevelPair caller in references.Callers(relay))
                    {
                        RoomOutput.TryParse(caller.Value, out RoomOutput output);
                        LogicRoomInputKind kind = RoomLogicRules.RelayInput(output.Input);
                        caller.Value = (output with { Target = hubName, Input = LogicRoom.ChannelInput(kind, channel) }).Format();
                    }

                    foreach (LevelPair pair in relay.Pairs.Where(p => !p.Deleted && RoomOutput.TryParse(p.Value, out _)))
                    {
                        hub.Pairs.Add(new LevelPair(LogicRoom.TriggerOutput(channel), pair.Value!, isConnection: true));
                    }

                    RelayFlags flags2 = RoomLogicRules.RelayFlagsOf(relay);
                    if (flags2 != RelayFlags.None)
                    {
                        hub.Set(LogicRoom.RelayFlagsKey(channel), ((int)flags2).ToString(CultureInfo.InvariantCulture));
                    }

                    relay.Removed = true;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>The <c>logic_room</c> keys the linker owns, from the placement.</summary>
        private void FillHub(RoomState state, LevelEntity hub)
        {
            ResolverRoom room = state.Room;
            NeighbourMask neighbours = NeighbourMask.None;
            for (int d = 0; d < RoomDirections.Count; d++)
            {
                if (FlagValue(state, new LinkerName(LinkerNameKind.Has, (RoomDirection)d, 0)))
                {
                    neighbours |= RoomDirections.Bit((RoomDirection)d);
                }
            }

            hub.Set(LogicRoom.NeighboursKey, ((int)neighbours).ToString(CultureInfo.InvariantCulture));
            hub.Set(LogicRoom.JoinedKey, ((int)room.Joined).ToString(CultureInfo.InvariantCulture));
            hub.Set(LogicRoom.RotationKey, room.Turns.ToString(CultureInfo.InvariantCulture));
            hub.Set(LogicRoom.ColumnKey, room.Column.ToString(CultureInfo.InvariantCulture));
            hub.Set(LogicRoom.RowKey, room.Row.ToString(CultureInfo.InvariantCulture));
            hub.Set(LogicRoom.RoomKey, room.Room);
        }

        /// <summary>A flag's value in its placement: whether the cell in its (turned) direction holds a room, or its side's socket is joined.</summary>
        private bool FlagValue(RoomState state, LinkerName flag)
        {
            ResolverRoom room = state.Room;
            if (flag.Kind == LinkerNameKind.Joined)
            {
                return (room.Joined & RoomDirections.JoinedBit(flag.Direction)) != 0;
            }

            (int dx, int dy) = RoomDirections.Offset(flag.Direction);
            (int tx, int ty) = RoomNameAnalysis.Turn(dx, dy, room.Turns);
            return cells.ContainsKey((room.Column + tx, room.Row + ty));
        }

        /// <summary>A kept entity of the room's own with this resolved name, or null.</summary>
        private static LevelEntity? Authored(RoomState state, string name) =>
            state.Room.Entities.FirstOrDefault(e => !e.Removed && !IsWorld(e) && Named(e, name));

        private static bool Named(LevelEntity entity, string name) =>
            entity.Find("targetname") is int at && !entity.Pairs[at].Deleted
            && string.Equals(entity.Pairs[at].Value, name, StringComparison.Ordinal);

        private LevelEntity WriteFlag(RoomState state, string name, string rest)
        {
            LevelEntity branch = Write(state,
                ("classname", RoomLinkerNames.FlagClass), ("targetname", name),
                ("InitialValue", FlagValue(state, RoomLinkerNames.Parse(rest)) ? "1" : "0"), ("origin", state.Room.CellCentre));
            Candidates[branch] = FoldKind.Branch;
            return branch;
        }

        private static LevelEntity Write(RoomState state, params (string Key, string Value)[] keys)
        {
            LevelEntity entity = new(state.Index, -1, [.. keys.Select(k => new LevelPair(k.Key, k.Value))]);
            state.Written.Add(entity);
            return entity;
        }

        private static bool TryChannelFlags(LevelPair pair, out int channel, out RelayFlags flags)
        {
            channel = 0;
            flags = RelayFlags.None;
            for (int k = 1; k <= LogicRoom.Channels; k++)
            {
                if (string.Equals(pair.Key, LogicRoom.RelayFlagsKey(k), StringComparison.OrdinalIgnoreCase))
                {
                    channel = k;
                    flags = (RelayFlags)(Bsp.Write.EntityStage.Atoi(pair.Value ?? string.Empty) & 7);
                    return true;
                }
            }

            return false;
        }

        private static void Add<TKey>(SortedDictionary<TKey, List<LevelPair>> map, TKey key, LevelPair pair)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out List<LevelPair>? list))
            {
                map[key] = list = [];
            }

            list.Add(pair);
        }

        /// <summary>Whether an entity's name was a local one the resolver filled in (so it may not be compared with global names).</summary>
        private static bool IsResolvedName(RoomState state, LevelEntity entity, int pair) =>
            state.Room.Names.Pairs.Any(n => !n.IsOutput && ReferenceEquals(state.Room.Entities[n.Entity], entity) && n.Pair == pair);

        private static string DisplayName(RoomState state, LevelEntity entity)
        {
            if (entity.Find("targetname") is int at && entity.Pairs[at].Value is { Length: > 0 } name)
            {
                NamePairRef? named = state.Room.Names.Pairs.FirstOrDefault(n =>
                    !n.IsOutput && n.Pair == at && ReferenceEquals(state.Room.Entities[n.Entity], entity));
                if (named?.Segments is [{ Literal: null } segment])
                {
                    return Prefix(state.Room.Column + segment.Dx, state.Room.Row + segment.Dy) + segment.Rest;
                }

                return name;
            }

            return entity.Id;
        }

        private static LevelPair PairOf(RoomState state, int entity, int pair)
        {
            List<LevelPair> pairs = state.Room.Entities[entity].Pairs;
            return pair < pairs.Count && pairs[pair].Value is not null
                ? pairs[pair]
                : throw Mismatch(state.Room, $"a key {pair} of entity {entity}, which has {pairs.Count} keys or holds a position there");
        }
    }
}
