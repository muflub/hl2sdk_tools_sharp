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
using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>The worldspawn keys that describe one room's extent rather than the map.</summary>
    private const string WorldMinsKey = "world_mins";

    /// <summary>The worldspawn's upper extent key.</summary>
    private const string WorldMaxsKey = "world_maxs";

    /// <summary>
    /// The linked entity lump: one worldspawn, then every room's other
    /// entities in layout order, each moved to its room's placement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every room's lump is a whole entity list ending in NUL, with its own
    /// worldspawn first; the engine reads entity text up to the first NUL, so
    /// lumps appended together are read as room 0's alone. Parsing each one
    /// and writing the list once is the fix.
    /// </para>
    /// <para>
    /// The first room's worldspawn is the map's. Its <c>world_mins</c> and
    /// <c>world_maxs</c> are replaced by the union of every room's extents as
    /// placed; every other key must agree across rooms (sky, detail
    /// settings, and so on are per map, and quietly keeping one room's would
    /// be a silent choice), and <c>hammerid</c> is per room and not compared.
    /// </para>
    /// <para>
    /// A moved entity has its <c>origin</c> put through the placement and its
    /// yaw (<c>angles</c>' second value, or <c>angle</c> unless it is the -1
    /// "up" or -2 "down" code) turned by the placement's quarter turns. Those
    /// are the keys every point entity reads its placement from; a key that
    /// holds a world position under another name is carried as written.
    /// </para>
    /// <para>
    /// An entity of a class the class table calls compile-only
    /// (<see cref="EntityCost.CompileOnly"/>: one the tools consume, such as
    /// <c>func_detail</c> or <c>prop_static</c>) is left out. vbsp already
    /// clears every one of them, so a room it compiled has none and its
    /// lump links exactly as before; a room built some other way that
    /// still carries one would otherwise put an entity in the level that
    /// the flattened level's compile drops, and that the entity budget
    /// does not count.
    /// </para>
    /// <para>
    /// <b>Singletons.</b> The library's own entities (the sun, fog and the
    /// other library-wide controllers the pack's library section holds) are
    /// written once, right after the worldspawn, never turned and standing
    /// at the level's origin (<see cref="RoomLibraryEntities.ToLinked"/>).
    /// A placement's copy of a level-wide singleton that equals the first
    /// copy is dropped and a different one refused (<see cref="LevelSingletons"/>),
    /// after names are resolved, as the flatten does.
    /// </para>
    /// <para>
    /// <b>Names.</b> When a placed room uses room-local names (or the link
    /// writes the mod's classes), every placement's entities go through the
    /// one naming resolver the flatten also runs (<see cref="LevelEntityResolver"/>)
    /// before they are moved: names filled in from the room's stored tables
    /// for its turn, <c>room_needs</c> applied, flags and the hub written,
    /// the logic folded. A level that uses none of it skips the resolver
    /// entirely and links to the bytes it did before names existed.
    /// </para>
    /// </remarks>
    internal static BspLumpData MergeEntities(
        RoomPlan[] plans, EntityClassTable classes, LevelNaming? naming = null, string? mapVersion = null, LevelSingletons? singletons = null)
    {
        singletons ??= new LevelSingletons([]);
        List<BspEntity> merged = [];
        BspEntity? world = null;
        string? worldOwner = null;
        Box? extent = null;

        // The naming resolver runs only when some placed room uses names
        // (or the mod's classes are asked for): a level that uses nothing of
        // it links exactly as it did before names existed.
        bool resolving = naming is not null && naming.IsActive(plans);
        List<ResolverRoom> resolverRooms = [];
        for (int index = 0; index < plans.Length; index++)
        {
            RoomPlan plan = plans[index];
            string name = plan.Placement.Room.Definition.Name;
            List<LevelEntity> entities = [];

            // Parsed and turned at room compile time (or now, for a room
            // without stored link data); a key that could not be read is
            // reported here, where the walk reaches its entity.
            IReadOnlyList<RoomLinkEntity> items = EntitiesFor(plan.Placement.Room, plan.Transform.Placement.NormalizedRotation).Items;
            for (int i = 0; i < items.Count; i++)
            {
                RoomLinkEntity item = items[i];
                if (!item.IsWorld)
                {
                    bool compileOnly = classes.Classify(ClassOf(item)) == EntityCost.CompileOnly;
                    if (!compileOnly && item.Error is not null)
                    {
                        throw new LinkException(item.Error);
                    }

                    if (resolving)
                    {
                        // Every entity keeps its place, so the room's
                        // stored name tables index the list as they index
                        // the room's lump; a compile-only one is removed.
                        entities.Add(compileOnly ? Removed(LevelEntity.FromLink(item, index, i)) : LevelEntity.FromLink(item, index, i));
                    }
                    else if (!compileOnly)
                    {
                        AddUnlessDuplicate(merged, singletons, TranslateEntity(item, plan.Transform, name, plan.OccluderBase), name, index);
                    }

                    continue;
                }

                if (resolving)
                {
                    entities.Add(LevelEntity.FromLink(item, index, i));
                }

                BspEntity entity = new();
                foreach (RoomLinkPair pair in item.Pairs)
                {
                    entity.Pairs.Add(new BspKeyValue(pair.Key, pair.Value!));
                }

                if (world is null)
                {
                    world = entity;
                    worldOwner = name;
                }
                else
                {
                    RequireSameWorld(world, worldOwner!, entity, name);
                }

                if (item.Error is { } error)
                {
                    throw new LinkException(error);
                }

                if (item.Extent is { } turned)
                {
                    Box moved = plan.Transform.TranslateBox(turned);
                    extent = extent is { } sofar ? Union(sofar, moved) : moved;
                }
            }

            if (resolving)
            {
                resolverRooms.Add(naming!.RoomFor(plan, index, entities));
            }
        }

        LevelResolution? resolution = null;
        if (resolving)
        {
            // One resolver for the whole level (a room's logic may reach its
            // neighbours'), then every entity it leaves moved to its cell.
            resolution = LevelEntityResolver.Resolve(resolverRooms, naming!.Options);
            naming.Result = resolution;
            foreach (LevelEntity entity in resolution.Entities)
            {
                RoomPlan plan = plans[entity.Placement];
                List<RoomLinkPair> pairs = [.. entity.Pairs.Select(p => p.Position ?? new RoomLinkPair(p.Key, p.Value!, default))];
                string room = plan.Placement.Room.Definition.Name;
                AddUnlessDuplicate(
                    merged,
                    singletons,
                    TranslateEntity(new RoomLinkEntity(false, pairs, null, null), plan.Transform, room, plan.OccluderBase),
                    room,
                    entity.Placement);
            }
        }

        List<BspEntity> lump = [];
        if (world is not null)
        {
            BspEntity linkedWorld = new();
            foreach (BspKeyValue pair in world.Pairs)
            {
                string value = pair.Value;
                if (extent is { } box && IsKey(pair.Key, WorldMinsKey))
                {
                    value = FormatVec(box.Mins);
                }
                else if (extent is { } box2 && IsKey(pair.Key, WorldMaxsKey))
                {
                    value = FormatVec(box2.Maxs);
                }
                else if (mapVersion is not null && IsKey(pair.Key, RoomLibraryOptions.MapVersionKey))
                {
                    // The rooms carry a fixed save counter; the level carries
                    // the library's (RoomLibraryOptions.MapVersionKey).
                    value = mapVersion;
                }

                linkedWorld.Pairs.Add(new BspKeyValue(pair.Key, value));
            }

            // The emission mode, with -mod-entities only (the contract's
            // worldspawn keys; the flatten writes the same).
            foreach ((string key, string value) in resolution?.WorldKeys ?? [])
            {
                linkedWorld.Pairs.Add(new BspKeyValue(key, value));
            }

            lump.Add(linkedWorld);
        }

        // The library's entities once, straight after the worldspawn, as the
        // flatten writes them (RoomLibraryEntities.ToLinked).
        lump.AddRange(singletons.Library.Select(RoomLibraryEntities.ToLinked));
        lump.AddRange(merged);
        return EntityLump.Write(lump);
    }

    /// <summary>
    /// Adds a moved entity to the level unless it is an equal later copy of
    /// a level-wide singleton (<see cref="LevelSingletons"/>), which is
    /// dropped; a different copy is refused there.
    /// </summary>
    private static void AddUnlessDuplicate(List<BspEntity> merged, LevelSingletons singletons, BspEntity entity, string room, int placement)
    {
        if (singletons.Keep(room, placement, entity.ClassName, [.. entity.Pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))]))
        {
            merged.Add(entity);
        }
    }

    private static LevelEntity Removed(LevelEntity entity)
    {
        entity.Removed = true;
        return entity;
    }

    /// <summary>
    /// What the link hands the naming resolver, and what it got back: the
    /// options, the library's name keys, and the resolution, which the link
    /// reads for its warnings and the entity budget.
    /// </summary>
    /// <param name="options">The resolver's options.</param>
    /// <param name="nameKeys">The library's name keys, for rooms whose names are read at link.</param>
    internal sealed class LevelNaming(LevelNamingOptions options, IReadOnlySet<string>? nameKeys)
    {
        private readonly Dictionary<int, RoomNameTurn> _names = [];

        /// <summary>The resolver's options.</summary>
        public LevelNamingOptions Options { get; } = options;

        /// <summary>The resolution, once the entities are merged; null when the resolver did not run.</summary>
        public LevelResolution? Result { get; set; }

        /// <summary>Whether the resolver runs: some placed room uses names, or the mod's classes are asked for.</summary>
        public bool IsActive(RoomPlan[] plans)
        {
            bool active = Options.ModEntities;
            for (int i = 0; i < plans.Length; i++)
            {
                active |= !NamesFor(plans[i], i).IsEmpty;
            }

            return active;
        }

        /// <summary>A placement as the resolver takes it.</summary>
        public ResolverRoom RoomFor(RoomPlan plan, int index, List<LevelEntity> entities)
        {
            ResolvedPlacement placement = plan.Placement;
            RoomPlacement where = placement.Instance.Placement;
            return new ResolverRoom
            {
                Room = placement.Room.Definition.Name,
                Column = where.CellX,
                Row = where.CellY,
                Turns = where.NormalizedRotation,
                Names = NamesFor(plan, index),
                Entities = entities,
                Joined = JoinedSides(placement.Room.Definition, placement.Instance),
                CellCentre = CellCentre(plan.Transform, placement.Room.Definition.CellSize),
            };
        }

        private RoomNameTurn NamesFor(RoomPlan plan, int index)
        {
            if (!_names.TryGetValue(index, out RoomNameTurn? names))
            {
                _names[index] = names = plan.Placement.Room.NamesFor(plan.Transform.Placement.NormalizedRotation, nameKeys);
            }

            return names;
        }
    }

    /// <summary>
    /// Where an entity the linker writes for a placement stands, as an
    /// <c>origin</c> value: the cell's centre. One spelling for the link and
    /// the flatten, so the two maps carry the same text.
    /// </summary>
    internal static string CellCentre(RoomTransform transform, float cellSize)
    {
        float half = cellSize / 2;
        return FormatVec(transform.Apply(new Vec3(half, half, half)));
    }

    /// <summary>A placement's joined sockets by the side of the room they are on, in its authored frame.</summary>
    internal static JoinedMask JoinedSides(RoomDefinition definition, RoomInstance instance)
    {
        JoinedMask joined = JoinedMask.None;
        foreach ((string socket, _) in instance.Joints)
        {
            if (definition.Sockets.FirstOrDefault(s => s.Name == socket) is { Name: not null } found)
            {
                joined |= RoomDirections.JoinedBit(SideOf(found.Facing));
            }
        }

        return joined;
    }

    /// <summary>The authored side a socket faces.</summary>
    internal static RoomDirection SideOf(RoomFacing facing) => facing switch
    {
        RoomFacing.PositiveX => RoomDirection.East,
        RoomFacing.PositiveY => RoomDirection.North,
        RoomFacing.NegativeX => RoomDirection.West,
        _ => RoomDirection.South,
    };

    /// <summary>
    /// The six keys vbsp writes on an <c>info_ladder</c> (what a
    /// <c>func_ladder</c> becomes once its brushes join the world): the
    /// ladder's bounds, one component per key, in mins-then-maxs order.
    /// </summary>
    /// <remarks>A property, not a static array: an array's elements are writable, and the libraries hold no mutable statics.</remarks>
    private static string[] LadderKeys => ["mins.x", "mins.y", "mins.z", "maxs.x", "maxs.y", "maxs.z"];

    /// <summary>One entity with its placement keys moved.</summary>
    /// <remarks>
    /// The turn (<see cref="TurnEntity"/>, which the room compile stores)
    /// and then the cell (<see cref="TranslateEntity"/>, the link's share).
    /// </remarks>
    internal static BspEntity MoveEntity(BspEntity entity, RoomTransform transform, string room, int occluderBase = 0) =>
        TranslateEntity(
            new RoomLinkEntity(false, TurnEntity(entity, transform.Placement.NormalizedRotation, room), null, null),
            transform,
            room,
            occluderBase);

    /// <summary>
    /// One entity's keys turned by a quarter turn: the part of moving it
    /// that depends only on the room and its turn, which the room compile
    /// can store per rotation (<see cref="RoomLinkEntities"/>).
    /// </summary>
    /// <param name="entity">The room's entity, as its compile wrote it.</param>
    /// <param name="turns">The quarter turns, 0 to 3.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <returns>Its keys in order, turned (<see cref="RoomLinkPair"/> says which kind each is).</returns>
    /// <exception cref="LinkException">A placement key does not hold numbers.</exception>
    /// <remarks>
    /// <para>
    /// <b>The yaw</b> (<c>angles</c>' second value, or <c>angle</c> unless it
    /// is the -1 "up" or -2 "down" code) turns with the room, except the
    /// sun's: all rooms of a library share one sun, fixed in the world
    /// (<see cref="VmfPlacement.KeepsWorldAngles"/>), so a
    /// <c>light_environment</c>'s angles are carried as written while its
    /// origin still turns.
    /// </para>
    /// <para>
    /// <b>An <c>info_ladder</c>'s bounds</b> are a world-space box written as
    /// six separate keys (<see cref="LadderKeys"/>), room-local in the room
    /// compile; a whole-map compile of the level measures them from the
    /// moved brushes. When all six are present they are read as one box and
    /// turned the way an occluder's box is (<see cref="RotateBox"/>: every
    /// corner turned, then the least and greatest taken again, because a
    /// quarter turn swaps which corner is the least). Each key then carries
    /// the turned corner it reads a component of, and the link adds the cell
    /// and writes the component with two decimals, the format vbsp writes
    /// them in; <see cref="RoomTransform.TranslateBox"/> makes the two steps
    /// bit for bit <see cref="MoveBox"/>. An entity with only some of the
    /// six keys is not a ladder vbsp made, and they are carried as written.
    /// The values are read back from the two-decimal text the room compile
    /// wrote, so a bound off the 0.01 grid is moved from its rounded value;
    /// on the grid (every bound a room kit produces) the result equals the
    /// whole-map compile's to the digit.
    /// </para>
    /// </remarks>
    internal static List<RoomLinkPair> TurnEntity(BspEntity entity, int turns, string room)
    {
        string[] ladderKeys = LadderKeys;
        Box? ladder = LadderBounds(entity, ladderKeys, turns, room);
        int yawTurns = VmfPlacement.KeepsWorldAngles(entity.ClassName) ? 0 : turns;
        List<RoomLinkPair> pairs = new(entity.Pairs.Count);
        foreach (BspKeyValue pair in entity.Pairs)
        {
            int ladderKey = ladder is null ? -1 : Array.FindIndex(ladderKeys, k => IsKey(pair.Key, k));
            if (ladderKey >= 0)
            {
                Box box = ladder!.Value;
                pairs.Add(new RoomLinkPair(pair.Key, null, ladderKey < 3 ? box.Mins : box.Maxs, ladderKey % 3));
                continue;
            }

            pairs.Add(TurnPair(pair, turns, yawTurns, room));
        }

        return pairs;
    }

    /// <summary>
    /// A turned entity moved to the placement's cell: its origin through
    /// <see cref="RoomTransform.Translate"/>, a ladder bound through the same
    /// translation, its <c>occludernumber</c> shifted by the room's occluder
    /// base, every other key as the turn left it.
    /// </summary>
    /// <remarks>
    /// A <c>func_occluder</c>'s <c>occludernumber</c> is an index into the
    /// occlusion lump, which every room compile numbers from 0. The linker
    /// appends the rooms' occluders in layout order
    /// (<see cref="RoomPlan.OccluderBase"/>), so the key is shifted by the
    /// same base, or the second room's occluder entity would name the first
    /// room's occluder, and an input toggling it would reach the wrong one.
    /// The base depends on the level, not the room, so this is the link's
    /// step, never stored; the first room's key (base 0) is carried as
    /// written.
    /// </remarks>
    private static BspEntity TranslateEntity(RoomLinkEntity entity, RoomTransform transform, string room, int occluderBase)
    {
        BspEntity moved = new();
        foreach (RoomLinkPair pair in entity.Pairs)
        {
            string value;
            if (pair.Value is null)
            {
                Vec3 at = transform.Translate(pair.Origin);
                value = pair.Component switch
                {
                    0 => F2(at.X),
                    1 => F2(at.Y),
                    2 => F2(at.Z),
                    _ => FormatVec(at),
                };
            }
            else if (occluderBase != 0 && IsKey(pair.Key, "occludernumber"))
            {
                value = int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int occluder)
                    ? (occluder + occluderBase).ToString(CultureInfo.InvariantCulture)
                    : throw new LinkException($"room {room} has an entity whose \"occludernumber\" holds \"{pair.Value}\", not an occluder index");
            }
            else
            {
                value = pair.Value;
            }

            moved.Pairs.Add(new BspKeyValue(pair.Key, value));
        }

        return moved;

        static string F2(float value) => value.ToString("F2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// An <c>info_ladder</c>'s bounds read from its six keys (in
    /// <paramref name="keys"/> order) and turned, or null when the entity
    /// does not carry all six.
    /// </summary>
    private static Box? LadderBounds(BspEntity entity, string[] keys, int turns, string room)
    {
        float[] bounds = new float[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            if (entity.Get(keys[i]) is not { } text)
            {
                return null;
            }

            bounds[i] = ParseFloat(text, keys[i], room);
        }

        return RotateBox(new Vec3(bounds[0], bounds[1], bounds[2]), new Vec3(bounds[3], bounds[4], bounds[5]), turns);
    }

    private static void RequireSameWorld(BspEntity world, string owner, BspEntity other, string name)
    {
        static IEnumerable<BspKeyValue> Compared(BspEntity e) =>
            e.Pairs.Where(p => !IsKey(p.Key, WorldMinsKey) && !IsKey(p.Key, WorldMaxsKey) && !IsKey(p.Key, "hammerid"));

        List<BspKeyValue> a = [.. Compared(world)];
        List<BspKeyValue> b = [.. Compared(other)];
        if (!a.SequenceEqual(b))
        {
            throw new LinkException(
                $"room {name}'s worldspawn keys disagree with room {owner}'s; the linked map has one worldspawn,"
                + " so every room must be compiled with the same map-wide settings");
        }
    }

    /// <summary>A yaw turned by quarter turns, kept in [0, 360).</summary>
    private static float TurnYaw(float yaw, int turns)
    {
        float turned = (yaw + (90f * turns)) % 360f;
        return turned < 0 ? turned + 360f : turned;
    }

    /// <summary>
    /// A turned entity's class: its first <c>classname</c> key, matched
    /// exactly, or an empty string; what <see cref="BspEntity.ClassName"/>
    /// reads, so the link strips exactly what the entity counts call
    /// compile-only.
    /// </summary>
    private static string ClassOf(RoomLinkEntity entity)
    {
        foreach (RoomLinkPair pair in entity.Pairs)
        {
            if (string.Equals(pair.Key, "classname", StringComparison.Ordinal))
            {
                return pair.Value ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool IsKey(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    private static Vec3 ParseVec(string text, string key, string room)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            throw new LinkException($"room {room} has an entity whose \"{key}\" is \"{text}\", not three numbers");
        }

        return new Vec3(ParseFloat(parts[0], key, room), ParseFloat(parts[1], key, room), ParseFloat(parts[2], key, room));
    }

    private static float ParseFloat(string text, string key, string room) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new LinkException($"room {room} has an entity whose \"{key}\" holds \"{text}\", not a number");

    private static string FormatVec(Vec3 v) => $"{Format(v.X)} {Format(v.Y)} {Format(v.Z)}";

    private static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);
}
