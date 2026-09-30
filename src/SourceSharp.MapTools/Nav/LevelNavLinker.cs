//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// Stitches the placed rooms' precomputed navigation into one level
/// navigation file: no voxelisation, only copying, renumbering, merging the
/// caps' records, and the jump search.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the link does per room.</b> It takes the room's navigation at the
/// placement's turn (read from the pack when the pack stores that turn,
/// turned from turn 0 otherwise), applies the capped keys of every socket
/// the level caps (merging the records of a voxel two caps change) and
/// rebuilds that room's runs when there were any, renumbers the room's
/// records, obstacles and overhanging brushes into the level's tables, and
/// appends its columns. The file stores nothing about which leaves
/// neighbour which: the reader derives that from the columns, across cell
/// faces too, so two rooms joined through a door connect exactly where both
/// doorways are open, and a capped door (whose plug is solid) connects
/// nothing.
/// </para>
/// <para>
/// <b>Jump links</b> are the one piece of graph work the link does, because
/// they reach across columns and rooms with the library's limits: for each
/// class, every walkable floor is paired with the walkable floors up to
/// <see cref="NavSettings.JumpDistance"/> away in each direction whose height
/// differs by at most <see cref="NavSettings.JumpHeight"/> and that a walk
/// does not already join (a neighbour within the step height), when the
/// space between is open at the higher floor's standing voxel and holds no
/// floor a walker would use instead. A link is bidirectional: up by
/// jumping, down by dropping. Whether a given agent clears the arc is its
/// own clearance's answer at run time.
/// </para>
/// <para>
/// <b>Deterministic.</b> Rooms are visited in the layout's order, columns x
/// fastest, records numbered in the order first met, jumps sorted; nothing
/// depends on a hash order or a thread.
/// </para>
/// </remarks>
public static class LevelNavLinker
{
    /// <summary>Links a level's navigation.</summary>
    /// <param name="layout">The level, its joints and caps spelled out.</param>
    /// <param name="columns">The grid's columns, west to east.</param>
    /// <param name="rows">The grid's rows, south to north.</param>
    /// <param name="navOf">Each placed room's navigation at a turn: <c>(room, quarterTurns)</c>.</param>
    /// <param name="packId">The pack's id, or null when it has none.</param>
    /// <param name="levelId">The level's id.</param>
    /// <param name="cancellationToken">Cancels the link between rooms.</param>
    /// <returns>The level's navigation.</returns>
    /// <exception cref="LinkException">
    /// The rooms were built with different navigation settings, a placement
    /// is outside the grid, or a point of interest stands in a doorway the
    /// level caps.
    /// </exception>
    public static Nav3dLevel Link(
        LevelLayout layout,
        int columns,
        int rows,
        Func<string, int, RoomNav> navOf,
        Guid? packId,
        Guid levelId,
        CancellationToken cancellationToken = default) =>
        Link(layout, columns, rows, navOf, packId, levelId, libraryOf: null, cancellationToken);

    /// <summary>
    /// Links a level of several libraries' rooms: as the overload for one,
    /// except that only rooms of one library must share every setting.
    /// </summary>
    /// <param name="layout">As for the overload for one library.</param>
    /// <param name="columns">As for the overload for one library.</param>
    /// <param name="rows">As for the overload for one library.</param>
    /// <param name="navOf">As for the overload for one library.</param>
    /// <param name="packId">As for the overload for one library.</param>
    /// <param name="levelId">As for the overload for one library.</param>
    /// <param name="libraryOf">
    /// Which of the level's libraries a placed room comes from, in level
    /// order, or null for a level of one. Rooms of different libraries must
    /// share the grid (the voxel and the voxels per cell, which a level of
    /// several refuses to differ before it links, the rooms design's D25);
    /// their other settings may differ, and the header takes the first
    /// placed library's, as the level takes its singletons.
    /// </param>
    /// <param name="cancellationToken">Cancels the link between rooms.</param>
    /// <returns>The level's navigation.</returns>
    /// <exception cref="LinkException">As for the overload for one library.</exception>
    public static Nav3dLevel Link(
        LevelLayout layout,
        int columns,
        int rows,
        Func<string, int, RoomNav> navOf,
        Guid? packId,
        Guid levelId,
        Func<string, int>? libraryOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(navOf);
        if (layout.Rooms.Count == 0)
        {
            throw new LinkException("a level's navigation needs at least one room.");
        }

        Placed[] placed = new Placed[layout.Rooms.Count];
        for (int i = 0; i < placed.Length; i++)
        {
            RoomPlacement p = layout.Rooms[i].Placement;
            if (p.CellX < 0 || p.CellY < 0 || p.CellX >= columns || p.CellY >= rows)
            {
                throw new LinkException($"room \"{p.Room}\" is placed at cell ({p.CellX}, {p.CellY}), outside the {columns} x {rows} grid.");
            }

            RoomNav nav = navOf(p.Room, p.NormalizedRotation);
            if (nav.Turn != p.NormalizedRotation)
            {
                throw new LinkException($"room \"{p.Room}\"'s navigation is at turn {nav.Turn}, not the placement's {p.NormalizedRotation}.");
            }

            placed[i] = new Placed(layout.Rooms[i], nav, (p.CellY * columns) + p.CellX, new RoomTransform(p, nav.CellSize));
        }

        // The header's settings: the first placed room's of the earliest
        // listed library the level places (placement 0's for one library).
        int Library(Placed p) => libraryOf?.Invoke(p.Instance.Placement.Room) ?? 0;
        int headerLibrary = placed.Min(Library);
        RoomNav first = placed.First(p => Library(p) == headerLibrary).Nav;
        Dictionary<int, RoomNav> firstOf = [];
        foreach (Placed p in placed)
        {
            RoomNav own = firstOf.TryGetValue(Library(p), out RoomNav? held) ? held : firstOf[Library(p)] = p.Nav;
            CheckSame(own, p.Nav, p.Instance.Placement.Room);
            CheckSameGrid(first, p.Nav, p.Instance.Placement.Room);
        }

        int n = first.CellVoxels;
        Doors doors = FindDoors(placed);
        CheckCappedDoorways(layout, placed, doors);

        NavRecordTable levelRecords = new();
        List<Nav3dObstacle> obstacles = [];
        List<float[]> brushes = [];
        int[] roots = new int[columns * rows];
        Array.Fill(roots, -1);
        List<uint> columnStarts = [];
        List<NavRun> runs = [];
        NavRegion cellRegion = new(0, 0, 0, n, n, n, first.VoxelSize);
        for (int i = 0; i < placed.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Placed p = placed[i];
            RoomPlacement placement = p.Instance.Placement;
            NavRecordTable local = new();
            for (int r = 1; r < p.Nav.Records.Count; r++)
            {
                _ = local.Add(p.Nav.Records[r]);
            }

            NavColumns roomColumns = p.Nav.Columns;
            bool anyCapped = false;
            for (int s = 0; s < p.Nav.Sockets.Count; s++)
            {
                anyCapped |= !doors.Records[doors.Index[i][s]].Joined;
            }

            if (anyCapped)
            {
                NavVoxelKey[] keys = roomColumns.Expand(n);
                bool[] touched = new bool[keys.Length];
                for (int s = 0; s < p.Nav.Sockets.Count; s++)
                {
                    if (doors.Records[doors.Index[i][s]].Joined)
                    {
                        continue;
                    }

                    foreach (NavCapChange change in p.Nav.SocketData[s].Capped)
                    {
                        keys[change.Voxel] = touched[change.Voxel] ? Merge(keys[change.Voxel], change.Key, local) : change.Key;
                        touched[change.Voxel] = true;
                    }
                }

                roomColumns = NavColumns.Of(cellRegion, keys, local);
            }

            int obstacleBase = obstacles.Count;
            int brushBase = brushes.Count;
            int[] map = new int[local.Count];
            Array.Fill(map, -1);
            int Level(int record)
            {
                if (map[record] < 0)
                {
                    map[record] = record == NavRecordTable.BlockedIndex
                        ? NavRecordTable.BlockedIndex
                        : levelRecords.Add(NavRecord.Decode(local[record]).Remap(obstacleBase, brushBase).Encode());
                }

                return map[record];
            }

            roots[p.Cell] = columnStarts.Count;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    columnStarts.Add((uint)runs.Count);
                    foreach (NavRun run in roomColumns.Column(x, y))
                    {
                        runs.Add(run with { Key = run.Key with { PlayerRecord = Level(run.Key.PlayerRecord), NpcRecord = Level(run.Key.NpcRecord) } });
                    }
                }
            }

            Vec3 offset = new(placement.CellX * p.Nav.CellSize, placement.CellY * p.Nav.CellSize, 0);
            foreach (RoomNavObstacle o in p.Nav.Obstacles)
            {
                obstacles.Add(new Nav3dObstacle(
                    o.TargetName is null ? null : ResolveName(o.TargetName, placement),
                    o.ClassName, (uint)p.Cell, o.HammerId, o.Kind, o.Bounds.Mins + offset, o.Bounds.Maxs + offset));
            }

            foreach (float[] planes in p.Nav.Brushes)
            {
                brushes.Add(NavBrush.FromPlaneFloats(planes, 1)!.Translated(offset).PlaneFloats());
            }
        }

        columnStarts.Add((uint)runs.Count);

        // The clearance section: the level's records back to back, in index
        // order; every record is a whole number of words.
        uint[] offsets = new uint[levelRecords.Count];
        using MemoryStream clearance = new();
        for (int r = 0; r < levelRecords.Count; r++)
        {
            offsets[r] = (uint)clearance.Length;
            clearance.Write(levelRecords[r]);
        }

        Nav3dLeaf[] leaves = [.. runs.Select(run => new Nav3dLeaf(
            run.ZLo, run.Height, run.Key.Flags, run.Key.Cost, offsets[run.Key.PlayerRecord], offsets[run.Key.NpcRecord],
            run.Key.PlayerFloorZ, run.Key.NpcFloorZ))];
        uint[] starts = [.. columnStarts];
        cancellationToken.ThrowIfCancellationRequested();
        List<Nav3dJump> jumps = FindJumps(
            new Grid(columns, rows, n, first.VoxelSize, roots, starts, leaves, levelRecords, offsets),
            first.StepHeight, first.JumpHeight, first.JumpDistance, cancellationToken);

        (List<Nav3dPoi> pois, int up, int down) = Pois(placed, doors);
        return new Nav3dLevel
        {
            CellSize = first.CellSize,
            VoxelSize = first.VoxelSize,
            CellVoxels = n,
            Columns = columns,
            Rows = rows,
            Origin = new Vec3(0, 0, 0),
            FloorNormalZ = first.FloorNormalZ,
            StepHeight = first.StepHeight,
            JumpHeight = first.JumpHeight,
            JumpDistance = first.JumpDistance,
            Cells = Cells(placed, doors, columns, rows),
            Doors = doors.Records,
            Pois = pois,
            Presets = [.. first.Agents.Select(a => new Nav3dPreset(a.Name, a.Width, a.Height, a.ClipClass))],
            Roots = roots,
            ColumnStarts = starts,
            Leaves = leaves,
            Clearance = clearance.ToArray(),
            Obstacles = obstacles,
            Brushes = brushes,
            Jumps = jumps,
            SpawnPoi = up,
            UpArrivalPoi = up,
            DownArrivalPoi = down,
            LevelId = levelId,
            PackId = packId ?? Guid.Empty,
        };
    }

    /// <summary>
    /// Refuses a point of interest standing in a doorway the level caps: a
    /// capped doorway is filled by its plug, so a point there is inside a
    /// wall. The message names the point and the door.
    /// </summary>
    /// <param name="layout">The level.</param>
    /// <param name="rooms">Each placed room's navigation at its placement's turn, in layout order.</param>
    /// <exception cref="LinkException">A point stands in a capped doorway.</exception>
    public static void CheckCappedDoorways(LevelLayout layout, IReadOnlyList<RoomNav> rooms)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rooms);
        Placed[] placed = [.. layout.Rooms.Select((r, i) => new Placed(
            r, rooms[i], 0, new RoomTransform(r.Placement, rooms[i].CellSize)))];
        CheckCappedDoorways(layout, placed, FindDoors(placed));
    }

    private static void CheckCappedDoorways(LevelLayout layout, Placed[] placed, Doors doors)
    {
        for (int i = 0; i < placed.Length; i++)
        {
            Placed p = placed[i];
            RoomPlacement placement = p.Instance.Placement;
            RoomDefinition definition = new(placement.Room, p.Nav.CellSize, layout.Kit, p.Nav.Sockets);
            RoomTransform turn = new(new RoomPlacement("turn", 0, 0, placement.NormalizedRotation), p.Nav.CellSize);
            for (int s = 0; s < p.Nav.Sockets.Count; s++)
            {
                if (doors.Records[doors.Index[i][s]].Joined)
                {
                    continue;
                }

                Box plug = RoomLinter.SealBox(definition, p.Nav.Sockets[s], p.Nav.CellSize);
                Vec3 a = turn.Apply(plug.Mins);
                Vec3 b = turn.Apply(plug.Maxs);
                Vec3 lo = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
                Vec3 hi = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
                foreach (RoomNavPoi poi in p.Nav.Pois)
                {
                    Vec3 at = poi.Position;
                    if (at.X >= lo.X && at.X <= hi.X && at.Y >= lo.Y && at.Y <= hi.Y && at.Z >= lo.Z && at.Z <= hi.Z)
                    {
                        string named = poi.Name is null ? string.Empty : $" \"{poi.Name}\"";
                        string where = string.Create(CultureInfo.InvariantCulture, $"cell ({placement.CellX}, {placement.CellY})");
                        throw new LinkException(
                            $"room \"{placement.Room}\" at {where}: {RoomPois.Entity} {poi.EntityId}{named} ({poi.Type})"
                            + $" stands in the doorway of socket \"{p.Nav.Sockets[s].Name}\", which the level caps;"
                            + " a capped doorway is filled by its plug, so the point would be inside the wall.");
                    }
                }
            }
        }
    }

    /// <summary>Resolves a room-local name for a placement with the rooms' naming rule; a name the rule refuses is kept as written.</summary>
    private static string ResolveName(string name, RoomPlacement placement) =>
        RoomLocalNames.Problem(name) is null
            ? RoomLocalNames.Resolve(name, placement.CellX, placement.CellY, placement.NormalizedRotation)
            : name;

    /// <summary>The key of a voxel two capped doors both change: the merged records per class, the higher floor.</summary>
    private static NavVoxelKey Merge(NavVoxelKey a, NavVoxelKey b, NavRecordTable table)
    {
        int Record(int ra, int rb) => ra == rb
            ? ra
            : table.Add(NavRecord.Merge(NavRecord.Decode(table[ra]), NavRecord.Decode(table[rb])).Encode());

        int player = Record(a.PlayerRecord, b.PlayerRecord);
        int npc = Record(a.NpcRecord, b.NpcRecord);
        Nav3dLeafFlags flags = a.Flags & ~NavVoxelKey.FloorFlags;
        (float playerFloor, Nav3dLeafFlags playerFlags) = Floor(a, b, Nav3dClipClass.Player, player);
        (float npcFloor, Nav3dLeafFlags npcFlags) = Floor(a, b, Nav3dClipClass.Npc, npc);
        NavVoxelKey merged = new(player, npc, flags | playerFlags | npcFlags, a.Cost, playerFloor, npcFloor);
        return merged.IsSolid ? NavVoxelKey.Solid : merged;
    }

    private static (float Floor, Nav3dLeafFlags Flags) Floor(NavVoxelKey a, NavVoxelKey b, Nav3dClipClass clipClass, int record)
    {
        Nav3dLeafFlags grounded = clipClass == Nav3dClipClass.Npc ? Nav3dLeafFlags.GroundedNpc : Nav3dLeafFlags.GroundedPlayer;
        Nav3dLeafFlags walkable = clipClass == Nav3dClipClass.Npc ? Nav3dLeafFlags.WalkableNpc : Nav3dLeafFlags.WalkablePlayer;
        bool ga = (a.Flags & grounded) != 0;
        bool gb = (b.Flags & grounded) != 0;
        if (record == NavRecordTable.BlockedIndex || !(ga || gb))
        {
            return (0, Nav3dLeafFlags.None);
        }

        float fa = clipClass == Nav3dClipClass.Npc ? a.NpcFloorZ : a.PlayerFloorZ;
        float fb = clipClass == Nav3dClipClass.Npc ? b.NpcFloorZ : b.PlayerFloorZ;
        bool wa = (a.Flags & walkable) != 0;
        bool wb = (b.Flags & walkable) != 0;
        if (ga && (!gb || fa > fb))
        {
            return (fa, grounded | (wa ? walkable : 0));
        }

        if (gb && (!ga || fb > fa))
        {
            return (fb, grounded | (wb ? walkable : 0));
        }

        return (fa, grounded | (wa || wb ? walkable : 0));
    }

    private static Nav3dRoomRole ToNav(RoomRole role) => role switch
    {
        RoomRole.Up => Nav3dRoomRole.Up,
        RoomRole.Down => Nav3dRoomRole.Down,
        _ => Nav3dRoomRole.None,
    };

    /// <summary>Refuses a room on another voxel grid than the level's: the one setting rooms of several libraries must share.</summary>
    private static void CheckSameGrid(RoomNav first, RoomNav other, string room)
    {
        if (first.CellSize != other.CellSize || first.VoxelSize != other.VoxelSize || first.CellVoxels != other.CellVoxels)
        {
            throw new LinkException(
                $"room \"{room}\"'s navigation was built on another voxel grid than the level's; a level's navigation is one grid.");
        }
    }

    private static void CheckSame(RoomNav first, RoomNav other, string room)
    {
        bool same = first.CellSize == other.CellSize && first.VoxelSize == other.VoxelSize && first.CellVoxels == other.CellVoxels
            && first.FloorNormalZ == other.FloorNormalZ && first.StepHeight == other.StepHeight && first.JumpHeight == other.JumpHeight
            && first.JumpDistance == other.JumpDistance && first.WaterCost == other.WaterCost && first.LadderCost == other.LadderCost
            && first.Agents.Count == other.Agents.Count;
        for (int a = 0; same && a < first.Agents.Count; a++)
        {
            same = first.Agents[a] == other.Agents[a];
        }

        if (!same)
        {
            throw new LinkException(
                $"room \"{room}\"'s navigation was built with other settings (voxel, slope, traversal limits, costs or presets) than the level's first room's;"
                + " every room of a level shares one library's settings.");
        }
    }

    /// <summary>The world direction a socket of a placement faces: 0 east, 1 north, 2 west, 3 south.</summary>
    private static byte Direction(RoomTransform transform, RoomFacing facing)
    {
        (int axis, int sign) = transform.WorldNormal(facing);
        return (axis, sign) switch
        {
            (0, > 0) => 0,
            (1, > 0) => 1,
            (0, _) => 2,
            _ => 3,
        };
    }

    private static Doors FindDoors(Placed[] placed)
    {
        List<Nav3dDoor> records = [];
        int[][] index = new int[placed.Length][];
        Dictionary<(int, int), int> byCell = [];
        for (int i = 0; i < placed.Length; i++)
        {
            byCell[(placed[i].Instance.Placement.CellX, placed[i].Instance.Placement.CellY)] = i;
        }

        for (int i = 0; i < placed.Length; i++)
        {
            Placed p = placed[i];
            index[i] = new int[p.Nav.Sockets.Count];
            for (int s = 0; s < p.Nav.Sockets.Count; s++)
            {
                RoomSocket socket = p.Nav.Sockets[s];
                bool joined = p.Instance.Joints.Any(j => j.Socket == socket.Name);
                index[i][s] = records.Count;
                records.Add(new Nav3dDoor((uint)p.Cell, Direction(p.Transform, socket.Facing), joined, socket.Name, -1));
            }
        }

        // Each joined door faces the neighbour's door on the shared wall.
        for (int i = 0; i < placed.Length; i++)
        {
            RoomPlacement pi = placed[i].Instance.Placement;
            for (int s = 0; s < placed[i].Nav.Sockets.Count; s++)
            {
                Nav3dDoor door = records[index[i][s]];
                if (!door.Joined)
                {
                    continue;
                }

                (int dx, int dy) = door.Direction switch
                {
                    0 => (1, 0),
                    1 => (0, 1),
                    2 => (-1, 0),
                    _ => (0, -1),
                };
                int other = -1;
                if (byCell.TryGetValue((pi.CellX + dx, pi.CellY + dy), out int j))
                {
                    for (int t = 0; t < placed[j].Nav.Sockets.Count; t++)
                    {
                        if (records[index[j][t]].Direction == (door.Direction + 2) % 4 && records[index[j][t]].Joined)
                        {
                            other = index[j][t];
                        }
                    }
                }

                records[index[i][s]] = other >= 0
                    ? door with { Other = other }
                    : door with { Joined = false };
            }
        }

        return new Doors(records, index);
    }

    /// <summary>The points: each room's authored ones, then its doors', room by room; and the first up and down arrivals.</summary>
    private static (List<Nav3dPoi> Pois, int Up, int Down) Pois(Placed[] placed, Doors doors)
    {
        List<Nav3dPoi> pois = [];
        uint all = placed[0].Nav.Agents.Count >= 32 ? uint.MaxValue : (1u << placed[0].Nav.Agents.Count) - 1;
        for (int i = 0; i < placed.Length; i++)
        {
            Placed p = placed[i];
            RoomPlacement placement = p.Instance.Placement;
            Vec3 offset = new(placement.CellX * p.Nav.CellSize, placement.CellY * p.Nav.CellSize, 0);
            foreach (RoomNavPoi poi in p.Nav.Pois)
            {
                Nav3dPoiFlags flags = (poi.HasFacing ? Nav3dPoiFlags.HasFacing : 0) | (poi.IsArrival ? Nav3dPoiFlags.Arrival : 0);
                pois.Add(new Nav3dPoi(
                    poi.Position + offset, poi.Yaw, poi.Radius, poi.Type, poi.Tags,
                    poi.Name is null ? null : RoomLocalNames.Resolve(poi.Name, placement.CellX, placement.CellY, placement.NormalizedRotation),
                    (uint)p.Cell, poi.AgentMask, -1, flags, ToNav(p.Nav.Role)));
            }

            for (int s = 0; s < p.Nav.Sockets.Count; s++)
            {
                int door = doors.Index[i][s];
                Nav3dDoor record = doors.Records[door];
                pois.Add(new Nav3dPoi(
                    p.Nav.SocketData[s].DoorPoint + offset, record.Direction * 90f, 0, RoomPois.DoorType, string.Empty, record.Name,
                    (uint)p.Cell, all, door, Nav3dPoiFlags.Door | Nav3dPoiFlags.HasFacing | (record.Joined ? Nav3dPoiFlags.Joined : 0),
                    ToNav(p.Nav.Role)));
            }
        }

        int up = pois.FindIndex(p => (p.Flags & Nav3dPoiFlags.Arrival) != 0 && p.Role == Nav3dRoomRole.Up);
        int down = pois.FindIndex(p => (p.Flags & Nav3dPoiFlags.Arrival) != 0 && p.Role == Nav3dRoomRole.Down);
        return (pois, up, down);
    }

    private static Nav3dCell[] Cells(Placed[] placed, Doors doors, int columns, int rows)
    {
        Nav3dCell[] cells = new Nav3dCell[columns * rows];
        for (int c = 0; c < cells.Length; c++)
        {
            cells[c] = new Nav3dCell(null, 0, Nav3dRoomRole.None, 0, 0);
        }

        for (int i = 0; i < placed.Length; i++)
        {
            byte joined = 0;
            byte capped = 0;
            foreach (int door in doors.Index[i])
            {
                Nav3dDoor record = doors.Records[door];
                if (record.Joined)
                {
                    joined |= (byte)(1 << record.Direction);
                }
                else
                {
                    capped |= (byte)(1 << record.Direction);
                }
            }

            RoomPlacement placement = placed[i].Instance.Placement;
            cells[placed[i].Cell] = new Nav3dCell(
                placement.Room, (byte)placement.NormalizedRotation, ToNav(placed[i].Nav.Role), joined, capped);
        }

        return cells;
    }

    /// <summary>
    /// The jump links of a stitched grid, for both classes: see the type's
    /// remarks for the rule. Sorted by leaf pair, then rise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static List<Nav3dJump> FindJumps(Grid grid, float stepHeight, float jumpHeight, float jumpDistance, CancellationToken cancellationToken)
    {
        double s = grid.VoxelSize;
        int reach = (int)Math.Floor((jumpDistance / s) + 1e-9);
        SortedDictionary<(uint A, uint B, float Rise), (byte Mask, Nav3dDirection Direction, byte Columns)> found = [];
        for (int leaf = 0; leaf < grid.Leaves.Length; leaf++)
        {
            if ((leaf & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            (int gx, int gy) = grid.LevelColumn(leaf);
            for (int c = 0; c < 2; c++)
            {
                Nav3dClipClass clipClass = (Nav3dClipClass)c;
                Nav3dLeaf from = grid.Leaves[leaf];
                if (!from.IsWalkable(clipClass))
                {
                    continue;
                }

                float fa = from.FloorZ(clipClass);
                for (int d = 0; d < 4 && reach > 0; d++)
                {
                    (int dx, int dy) = d switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
                    for (int k = 1; k <= reach; k++)
                    {
                        int column = grid.Column(gx + (k * dx), gy + (k * dy));
                        if (column < 0)
                        {
                            break;
                        }

                        for (int other = (int)grid.ColumnStarts[column]; other < grid.ColumnStarts[column + 1]; other++)
                        {
                            Nav3dLeaf to = grid.Leaves[other];
                            if (!to.IsWalkable(clipClass))
                            {
                                continue;
                            }

                            float fb = to.FloorZ(clipClass);
                            float rise = fb - fa;
                            if (Math.Abs(rise) > jumpHeight || (k == 1 && Math.Abs(rise) <= stepHeight))
                            {
                                continue;
                            }

                            // The higher floor's standing voxel: the jumper
                            // passes there, so every column from the take-off
                            // to the landing must be open at it.
                            int top = fa >= fb ? from.ZLo : to.ZLo;
                            if (!grid.Open(grid.Column(gx, gy), from.ZLo, top, clipClass)
                                || !grid.Open(column, to.ZLo, top, clipClass))
                            {
                                continue;
                            }

                            bool gap = true;
                            for (int j = 1; j < k && gap; j++)
                            {
                                int between = grid.Column(gx + (j * dx), gy + (j * dy));
                                gap = between >= 0 && grid.Open(between, top, top, clipClass)
                                    && !grid.FloorNear(between, clipClass, Math.Min(fa, fb) - stepHeight, Math.Max(fa, fb) + stepHeight);
                            }

                            if (!gap)
                            {
                                continue;
                            }

                            (uint a, uint b, float r, Nav3dDirection direction) = leaf < other
                                ? ((uint)leaf, (uint)other, rise, (Nav3dDirection)d)
                                : ((uint)other, (uint)leaf, -rise, (Nav3dDirection)((d + 2) % 4));
                            byte bit = (byte)(1 << c);
                            found[(a, b, r)] = found.TryGetValue((a, b, r), out var seen)
                                ? seen with { Mask = (byte)(seen.Mask | bit) }
                                : (bit, direction, (byte)k);
                        }
                    }
                }
            }
        }

        return [.. found.Select(f => new Nav3dJump(f.Key.A, f.Key.B, f.Key.Rise, f.Value.Mask, f.Value.Direction, f.Value.Columns))];
    }

    /// <summary>The stitched grid as the jump search reads it.</summary>
    internal sealed record Grid(
        int Columns, int Rows, int CellVoxels, float VoxelSize, int[] Roots, uint[] ColumnStarts, Nav3dLeaf[] Leaves,
        NavRecordTable Records, uint[] Offsets)
    {
        private readonly int[] _leafColumn = LeafColumns(ColumnStarts, Leaves.Length);
        private readonly int[] _blockCell = BlockCells(Roots, CellVoxels);
        private readonly Dictionary<uint, int> _recordOf = Enumerable.Range(0, Offsets.Length).ToDictionary(r => Offsets[r], r => r);

        /// <summary>A level voxel column's index, or -1 outside the grid or in an empty cell.</summary>
        public int Column(int gx, int gy)
        {
            int n = CellVoxels;
            if (gx < 0 || gy < 0 || gx >= Columns * n || gy >= Rows * n)
            {
                return -1;
            }

            int root = Roots[((gy / n) * Columns) + (gx / n)];
            return root < 0 ? -1 : root + ((gy % n) * n) + (gx % n);
        }

        /// <summary>A leaf's level voxel column coordinates.</summary>
        public (int X, int Y) LevelColumn(int leaf)
        {
            int n = CellVoxels;
            int column = _leafColumn[leaf];
            int cell = _blockCell[column / (n * n)];
            int within = column % (n * n);
            return (((cell % Columns) * n) + (within % n), ((cell / Columns) * n) + (within / n));
        }

        /// <summary>Whether every voxel of a column from one height to another is free for a class.</summary>
        public bool Open(int column, int from, int to, Nav3dClipClass clipClass)
        {
            for (int z = Math.Min(from, to); z <= Math.Max(from, to); z++)
            {
                bool free = false;
                for (int l = (int)ColumnStarts[column]; l < ColumnStarts[column + 1] && !free; l++)
                {
                    Nav3dLeaf leaf = Leaves[l];
                    free = z >= leaf.ZLo && z <= leaf.ZHi && _recordOf[leaf.Clearance(clipClass)] != NavRecordTable.BlockedIndex;
                }

                if (!free)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether a column has a walkable floor for a class within a height range.</summary>
        public bool FloorNear(int column, Nav3dClipClass clipClass, float lo, float hi)
        {
            for (int l = (int)ColumnStarts[column]; l < ColumnStarts[column + 1]; l++)
            {
                Nav3dLeaf leaf = Leaves[l];
                if (leaf.IsWalkable(clipClass) && leaf.FloorZ(clipClass) >= lo && leaf.FloorZ(clipClass) <= hi)
                {
                    return true;
                }
            }

            return false;
        }

        private static int[] LeafColumns(uint[] starts, int leaves)
        {
            int[] column = new int[leaves];
            for (int j = 0; j + 1 < starts.Length; j++)
            {
                for (uint l = starts[j]; l < starts[j + 1]; l++)
                {
                    column[l] = j;
                }
            }

            return column;
        }

        private static int[] BlockCells(int[] roots, int n)
        {
            int blocks = roots.Count(r => r >= 0);
            int[] cells = new int[blocks];
            for (int c = 0; c < roots.Length; c++)
            {
                if (roots[c] >= 0)
                {
                    cells[roots[c] / (n * n)] = c;
                }
            }

            return cells;
        }
    }

    private sealed record Placed(RoomInstance Instance, RoomNav Nav, int Cell, RoomTransform Transform);

    private sealed record Doors(List<Nav3dDoor> Records, int[][] Index);
}
