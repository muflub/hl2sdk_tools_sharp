//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>What the level writes for one transition room, every position already in world text.</summary>
/// <param name="Direction">Which way the room leads.</param>
/// <param name="Map">The destination map: the level's <c>up_map</c> or <c>down_map</c>.</param>
/// <param name="VolumeId">The transition volume's Hammer id.</param>
/// <param name="FoldId">
/// The Hammer id of the hallway <c>trigger_once</c> that becomes the
/// <c>trigger_changelevel</c> in the stock fallback, or -1: always -1 with
/// <c>-mod-entities</c>, which folds nothing.
/// </param>
/// <param name="Centre">The volume's centre, as an <c>origin</c> value: where <c>logic_level_transition</c> stands.</param>
/// <param name="Landmark">The <c>info_landmark</c>'s name in the stock fallback, or null with <c>-mod-entities</c>.</param>
/// <param name="LandmarkOrigin">Where the landmark stands, as an <c>origin</c> value, or null.</param>
internal sealed record PlacementTransition(
    TransitionDirection Direction, string Map, int VolumeId, int FoldId, string Centre, string? Landmark, string? LandmarkOrigin)
{
    /// <summary>Whether the level omits the volume's model: the mod's class stands in its place, or the fold replaces it.</summary>
    public bool OmitsVolume(bool modEntities) => modEntities || FoldId >= 0;
}

/// <summary>A spawn point the stock fallback writes as an <c>info_player_start</c>, in world text.</summary>
/// <param name="Placement">The placement it belongs to, whose written entities it follows.</param>
/// <param name="Origin">Its <c>origin</c> value.</param>
/// <param name="Angles">Its <c>angles</c> value: pitch 0, the point's yaw turned with the room, roll 0.</param>
internal sealed record LevelSpawnPoint(int Placement, string Origin, string Angles);

/// <summary>
/// A level's transitions and spawn, decided once from the level and its
/// rooms' transition data, for the link and the flatten alike (the rooms
/// design, section 11).
/// </summary>
/// <remarks>
/// <para>
/// <b>When it applies.</b> A level whose file has a transition key, or that
/// places a room with a role, is a level of a run: the level rule holds, the
/// transitions are written and the rooms' own <c>info_player_start</c>s are
/// stripped. Any other level (every level of a library without roles, and a
/// level that places none of a library's role rooms and says nothing of
/// transitions) is a standalone map: <see cref="Make"/> returns null and the
/// link and the flatten write exactly what they wrote before transitions.
/// </para>
/// <para>
/// <b>The level rule</b> (11.1, the layout half of <see cref="RoomLinter"/>):
/// exactly one up room and one down room unless the level switches the role
/// off with <c>up: none</c> or <c>down: none</c>, in which case none; a map
/// key for each role present and none for a role switched off. Then the
/// spawn (11.5): the up room's arrival and spawn points; with
/// <c>up: none</c>, the spawn points of the <c>spawn</c> cell's room or, by
/// default, of the room farthest in doors from the down room (ties to the
/// earlier in link order), refused when no room has one; and
/// <c>spawn_count</c> checked against what that room has. Each refusal is a
/// <see cref="RoomLintException"/> with the 15.4 message.
/// </para>
/// <para>
/// <b>Positions are text here.</b> Every position the level writes (the
/// volume's centre, the landmark, the spawn points) is put through the
/// placement's <see cref="RoomTransform.Apply"/> and formatted once, by
/// <see cref="LevelLinker.FormatVec"/>, and the yaw turned by
/// <see cref="LevelLinker.TurnYaw"/>: the resolver never reads a position,
/// and the two maps carry the same text.
/// </para>
/// <para>
/// <b>Nothing is kept</b>: a plan is made per link or flatten and dropped
/// with it.
/// </para>
/// </remarks>
internal sealed class LevelTransitionPlan
{
    private LevelTransitionPlan(bool modEntities, PlacementTransition?[] placements, List<LevelSpawnPoint> spawns)
    {
        ModEntities = modEntities;
        Placements = placements;
        Spawns = spawns;
    }

    /// <summary>Whether the level writes the mod's classes.</summary>
    public bool ModEntities { get; }

    /// <summary>Per placement, in link order, what it writes as a transition room, or null.</summary>
    public IReadOnlyList<PlacementTransition?> Placements { get; }

    /// <summary>The spawn points the stock fallback writes; none with <c>-mod-entities</c>, whose spawn is read from the navigation.</summary>
    public IReadOnlyList<LevelSpawnPoint> Spawns { get; }

    /// <summary>The class of the stock fallback's changelevel.</summary>
    public const string ChangeLevelClass = "trigger_changelevel";

    /// <summary>The class of the stock fallback's landmark.</summary>
    public const string LandmarkClass = "info_landmark";

    /// <summary>The class of a player start, written for the spawn and stripped from the rooms.</summary>
    public const string PlayerStartClass = "info_player_start";

    /// <summary>The stock input the author's <c>Transition</c> outputs become.</summary>
    public const string ChangeLevelInput = "ChangeLevel";

    /// <summary><c>trigger_changelevel</c>'s "disable touch" spawn flag.</summary>
    public const int DisableTouch = 2;

    /// <summary>
    /// Plans a level's transitions and spawn, or returns null for a level
    /// that has none (it says nothing of transitions and places no role room).
    /// </summary>
    /// <param name="layout">The level, its joints derived; its name is the level's map name.</param>
    /// <param name="transits">Per placement, in link order, its room's transition data, or null.</param>
    /// <param name="definitions">Each placed room's definition, by name.</param>
    /// <param name="modEntities">Whether the level writes the mod's classes.</param>
    /// <returns>The plan, or null.</returns>
    /// <exception cref="RoomLintException">The level breaks the level rule or the spawn rule, with the 15.4 message.</exception>
    public static LevelTransitionPlan? Make(
        LevelLayout layout, IReadOnlyList<RoomTransit?> transits, Func<string, RoomDefinition> definitions, bool modEntities)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(transits);
        ArgumentNullException.ThrowIfNull(definitions);
        IReadOnlyList<RoomInstance> rooms = layout.Rooms;
        if (layout.Transitions is null && !transits.Any(t => t is { Role: not RoomRole.None }))
        {
            return null;
        }

        LevelTransitions settings = layout.Transitions ?? new LevelTransitions();
        string level = layout.Name;
        int[] role = new int[2];
        role[0] = RoleRoom(layout, transits, settings, TransitionDirection.Up);
        role[1] = RoleRoom(layout, transits, settings, TransitionDirection.Down);

        PlacementTransition?[] placements = new PlacementTransition?[rooms.Count];
        foreach (TransitionDirection direction in (ReadOnlySpan<TransitionDirection>)[TransitionDirection.Up, TransitionDirection.Down])
        {
            int p = role[(int)direction];
            if (p < 0)
            {
                continue;
            }

            RoomTransit transit = transits[p]!;
            RoomTransform transform = new(rooms[p].Placement, layout.CellSize);
            string map = settings.MapOf(direction)!;
            int fold = modEntities ? -1 : transit.FoldId;
            string? landmark = null, landmarkOrigin = null;
            if (!modEntities)
            {
                // One landmark per transition room, named for the pair of
                // levels it joins, upper first: in the down room at the
                // changelevel's centre, in the level below's up room at its
                // arrival, so the engine carries the player's offset from one
                // to the other.
                (landmark, Vec3 at) = direction == TransitionDirection.Down
                    ? (Landmark(level, map), fold >= 0 ? transit.FoldCentre : transit.VolumeCentre)
                    : (Landmark(map, level), transit.Arrival!.Value.Origin);
                landmarkOrigin = LevelLinker.FormatVec(transform.Apply(at));
            }

            placements[p] = new PlacementTransition(
                direction, map, transit.VolumeId, fold, LevelLinker.FormatVec(transform.Apply(transit.VolumeCentre)), landmark, landmarkOrigin);
        }

        (int spawnRoom, List<TransitPoint> points) = SpawnRoom(layout, transits, definitions, settings, role[0], role[1]);
        if (settings.SpawnCount is int wanted && points.Count < wanted)
        {
            throw new RoomLintException(string.Create(CultureInfo.InvariantCulture,
                $"level {level}: {LevelYaml.SpawnCountKey} {wanted}, but {(role[0] >= 0 ? "up" : "spawn")} room {rooms[spawnRoom].Placement.Room}"
                + $" has {points.Count} spawn points."));
        }

        List<LevelSpawnPoint> spawns = [];
        if (!modEntities)
        {
            RoomTransform transform = new(rooms[spawnRoom].Placement, layout.CellSize);
            foreach (TransitPoint point in points)
            {
                float yaw = LevelLinker.TurnYaw(point.Yaw, rooms[spawnRoom].Placement.NormalizedRotation);
                spawns.Add(new LevelSpawnPoint(
                    spawnRoom, LevelLinker.FormatVec(transform.Apply(point.Origin)), $"0 {LevelLinker.Format(yaw)} 0"));
            }
        }

        return new LevelTransitionPlan(modEntities, placements, spawns);
    }

    /// <summary>A landmark's name: the upper level's map name, two underscores, the lower's.</summary>
    /// <param name="upper">The upper level's map name.</param>
    /// <param name="lower">The lower level's map name.</param>
    /// <returns>The name both levels give the landmark.</returns>
    public static string Landmark(string upper, string lower) => upper + "__" + lower;

    /// <summary>
    /// The placement holding a role's room, or -1 when the level switches the
    /// role off; refuses a level with none or several of a role it keeps, a
    /// role room it switched off, and a missing or superfluous map key.
    /// </summary>
    private static int RoleRoom(LevelLayout layout, IReadOnlyList<RoomTransit?> transits, LevelTransitions settings, TransitionDirection direction)
    {
        string level = layout.Name;
        string name = LevelTransition.Spell(direction);
        RoomRole wanted = direction == TransitionDirection.Up ? RoomRole.Up : RoomRole.Down;
        List<int> placed = [];
        for (int p = 0; p < layout.Rooms.Count; p++)
        {
            if (transits[p]?.Role == wanted)
            {
                placed.Add(p);
            }
        }

        string mapKey = direction == TransitionDirection.Up ? LevelYaml.UpMapKey : LevelYaml.DownMapKey;
        if (settings.IsOff(direction))
        {
            if (placed.Count > 0)
            {
                RoomPlacement first = layout.Rooms[placed[0]].Placement;
                throw new RoomLintException(string.Create(CultureInfo.InvariantCulture,
                    $"level {level}: says \"{name}: {LevelYaml.None}\" but places {name} room {first.Room} at cell ({first.CellX}, {first.CellY})."));
            }

            if (settings.MapOf(direction) is not null)
            {
                throw new RoomLintException($"level {level}: says \"{name}: {LevelYaml.None}\" and also names {mapKey}.");
            }

            return -1;
        }

        if (placed.Count != 1)
        {
            string cells = placed.Count == 0
                ? "none"
                : string.Join(", ", placed.Select(p => string.Create(CultureInfo.InvariantCulture,
                    $"({layout.Rooms[p].Placement.CellX}, {layout.Rooms[p].Placement.CellY})")));
            throw new RoomLintException(string.Create(CultureInfo.InvariantCulture,
                $"level {level}: {placed.Count} {name} rooms ({cells}); a level has exactly one unless it says \"{name}: {LevelYaml.None}\"."));
        }

        if (settings.MapOf(direction) is null)
        {
            throw new RoomLintException($"level {level}: has {(direction == TransitionDirection.Up ? "an" : "a")} {name} room but no {mapKey}.");
        }

        return placed[0];
    }

    /// <summary>
    /// The placement a fresh start spawns in, and its points in the order
    /// the stock fallback writes them: the up room's arrival then its spawn
    /// points; or, with <c>up: none</c>, the spawn points of the
    /// <c>spawn</c> cell's room or of the room farthest from the down room.
    /// </summary>
    private static (int Placement, List<TransitPoint> Points) SpawnRoom(
        LevelLayout layout, IReadOnlyList<RoomTransit?> transits, Func<string, RoomDefinition> definitions, LevelTransitions settings, int up, int down)
    {
        string level = layout.Name;
        if (up >= 0)
        {
            if (settings.SpawnCell is (int sx, int sy))
            {
                throw new RoomLintException(string.Create(CultureInfo.InvariantCulture,
                    $"level {level}: names {LevelYaml.SpawnKey} cell ({sx}, {sy}), but a level with an up room spawns at its arrival point."));
            }

            RoomTransit transit = transits[up]!;
            return (up, [transit.Arrival!.Value, .. transit.Spawns]);
        }

        if (settings.SpawnCell is (int x, int y))
        {
            for (int p = 0; p < layout.Rooms.Count; p++)
            {
                RoomPlacement placement = layout.Rooms[p].Placement;
                if (placement.CellX == x && placement.CellY == y && transits[p] is { Spawns.Count: > 0 } transit)
                {
                    return (p, [.. transit.Spawns]);
                }
            }

            throw new RoomLintException(string.Create(CultureInfo.InvariantCulture,
                $"level {level}: {LevelYaml.SpawnKey} names cell ({x}, {y}), which holds no room with a spawn point."));
        }

        int[] doors = down >= 0 ? DoorDistances(layout, definitions, down) : new int[layout.Rooms.Count];
        int best = -1;
        for (int p = 0; p < layout.Rooms.Count; p++)
        {
            if (transits[p] is { Spawns.Count: > 0 } && (best < 0 || doors[p] > doors[best]))
            {
                best = p;
            }
        }

        if (best < 0)
        {
            throw new RoomLintException(
                $"level {level}: says \"up: {LevelYaml.None}\" and no room has a spawn point; add an {RoomPois.Entity} of type spawn or a spawn cell.");
        }

        return (best, [.. transits[best]!.Spawns]);
    }

    /// <summary>
    /// Every placement's distance in doors from one placement: the shortest
    /// path through joined sockets, breadth first over the level's joints;
    /// -1 for a placement it cannot reach (none, in a level the linter passed).
    /// </summary>
    internal static int[] DoorDistances(LevelLayout layout, Func<string, RoomDefinition> definitions, int from)
    {
        IReadOnlyList<RoomInstance> rooms = layout.Rooms;
        Dictionary<(int, int), int> byCell = [];
        for (int i = 0; i < rooms.Count; i++)
        {
            byCell[(rooms[i].Placement.CellX, rooms[i].Placement.CellY)] = i;
        }

        List<int>[] next = [.. rooms.Select(_ => new List<int>())];
        for (int i = 0; i < rooms.Count; i++)
        {
            RoomPlacement placement = rooms[i].Placement;
            RoomTransform transform = new(placement, layout.CellSize);
            IReadOnlyList<RoomSocket> sockets = definitions(placement.Room).Sockets;
            foreach ((string socket, _) in rooms[i].Joints)
            {
                if (sockets.FirstOrDefault(s => s.Name == socket) is not { Name: not null } found)
                {
                    continue;
                }

                (int axis, int sign) = transform.WorldNormal(found.Facing);
                if (byCell.TryGetValue((placement.CellX + (axis == 0 ? sign : 0), placement.CellY + (axis == 1 ? sign : 0)), out int j))
                {
                    next[i].Add(j);
                }
            }
        }

        int[] distance = new int[rooms.Count];
        Array.Fill(distance, -1);
        distance[from] = 0;
        Queue<int> queue = new([from]);
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            foreach (int j in next[at])
            {
                if (distance[j] < 0)
                {
                    distance[j] = distance[at] + 1;
                    queue.Enqueue(j);
                }
            }
        }

        return distance;
    }
}
