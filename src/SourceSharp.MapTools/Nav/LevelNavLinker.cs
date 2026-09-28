//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// Stitches the placed rooms' precomputed navigation into one level
/// navigation file: no voxelisation, only copying, index remapping and
/// graph work.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the link does per room.</b> It takes the room's navigation at the
/// placement's turn (read from the pack when the pack stores that turn,
/// turned from turn 0 otherwise), applies the cap changes of every socket
/// the level caps and rebuilds that room's octree when there were any, and
/// offsets every voxel coordinate by the cell. It then finds each room's
/// face adjacency from its leaf map (every pair of neighbouring voxels in
/// different leaves), joins leaves across every joined door where the two
/// rooms' portal voxels meet face to face, and labels connected components
/// over the whole level.
/// </para>
/// <para>
/// <b>A capped door does not connect</b>: only a joint in the layout makes a
/// door link, and a capped socket's changes close its opening. <b>A door
/// too small for an agent</b> has no portal voxels for that agent, so it
/// makes no link for it.
/// </para>
/// <para>
/// <b>Deterministic.</b> Rooms are visited in the layout's order, leaves in
/// each room's octree order, edges sorted, components numbered by their
/// first leaf; nothing depends on a hash order or a thread.
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
    /// <exception cref="LinkException">The rooms were built with different navigation settings, or a placement is outside the grid.</exception>
    public static Nav3dLevel Link(
        LevelLayout layout,
        int columns,
        int rows,
        Func<string, int, RoomNav> navOf,
        Guid? packId,
        Guid levelId,
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

        RoomNav first = placed[0].Nav;
        foreach (Placed p in placed)
        {
            CheckSame(first, p.Nav, p.Instance.Placement.Room);
        }

        int n = first.CellVoxels;
        if ((long)columns * n > Nav3dFormat.MaxVoxelCoordinate || (long)rows * n > Nav3dFormat.MaxVoxelCoordinate)
        {
            throw new LinkException(
                $"a {columns} x {rows} grid of {n}-voxel cells is wider than a .nav3d leaf can address ({Nav3dFormat.MaxVoxelCoordinate} voxels).");
        }

        Doors doors = FindDoors(placed);
        List<Nav3dPoi> pois = [];
        List<int[]> poiLeafPerAgent = [];
        List<Nav3dAgent> agents = [];
        List<(int[][] Maps, int[] Bases)> leafMaps = [];
        for (int a = 0; a < first.Agents.Count; a++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (Nav3dAgent linked, int[][] maps, int[] bases) = LinkAgent(placed, doors, a, columns, rows, first, cancellationToken);
            agents.Add(linked);
            leafMaps.Add((maps, bases));
        }

        // The level leaf of a placed room's voxel, per agent.
        int LeafOfVoxel(int placement, int agent, NavVoxel v)
        {
            int local = leafMaps[agent].Maps[placement][RoomNav.Index(v, n)];
            return local < 0 ? -1 : leafMaps[agent].Bases[placement] + local;
        }

        int LeafAt(int placement, int agent, Vec3 point)
        {
            double s = placed[placement].Nav.VoxelSize;
            return LeafOfVoxel(placement, agent, new NavVoxel(
                (byte)RoomNavBuilder.VoxelOf(point.X, s, n),
                (byte)RoomNavBuilder.VoxelOf(point.Y, s, n),
                (byte)RoomNavBuilder.VoxelOf(point.Z, s, n)));
        }

        // The points: each room's authored ones, then its doors', room by room.
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
                poiLeafPerAgent.Add([.. Enumerable.Range(0, agents.Count).Select(a =>
                    (poi.AgentMask & (1u << a)) == 0 ? -1 : LeafAt(i, a, poi.Position))]);
            }

            for (int s = 0; s < p.Nav.Sockets.Count; s++)
            {
                int door = doors.Index[i][s];
                for (int a = 0; a < agents.Count; a++)
                {
                    IReadOnlyList<NavVoxel> portal = p.Nav.AgentData[a].Sockets[s].Portal;
                    if (portal.Count == 0)
                    {
                        continue;
                    }

                    (Vec3 local, NavVoxel at) = DoorPoint(portal, doors.Records[door].Direction, p.Nav);
                    int leaf = LeafOfVoxel(i, a, at);
                    pois.Add(new Nav3dPoi(
                        local + offset, DirectionYaw(doors.Records[door].Direction), 0, RoomPois.DoorType, string.Empty,
                        doors.Records[door].Name, (uint)p.Cell, 1u << a, door,
                        Nav3dPoiFlags.Door | Nav3dPoiFlags.HasFacing | (doors.Records[door].Joined ? Nav3dPoiFlags.Joined : 0),
                        ToNav(p.Nav.Role)));
                    int[] leaves = new int[agents.Count];
                    Array.Fill(leaves, -1);
                    leaves[a] = leaf;
                    poiLeafPerAgent.Add(leaves);
                }
            }
        }

        int up = pois.FindIndex(p => (p.Flags & Nav3dPoiFlags.Arrival) != 0 && p.Role == Nav3dRoomRole.Up);
        int down = pois.FindIndex(p => (p.Flags & Nav3dPoiFlags.Arrival) != 0 && p.Role == Nav3dRoomRole.Down);
        List<Nav3dAgent> finished = [];
        for (int a = 0; a < agents.Count; a++)
        {
            finished.Add(agents[a] with { PoiLeaves = [.. poiLeafPerAgent.Select(l => l[a])] });
        }

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

        return new Nav3dLevel
        {
            CellSize = first.CellSize,
            VoxelSize = first.VoxelSize,
            CellVoxels = n,
            Columns = columns,
            Rows = rows,
            Origin = new Vec3(0, 0, 0),
            FloorNormalZ = first.FloorNormalZ,
            Cells = cells,
            Doors = doors.Records,
            Pois = pois,
            Agents = finished,
            SpawnPoi = up,
            UpArrivalPoi = up,
            DownArrivalPoi = down,
            LevelId = levelId,
            PackId = packId ?? Guid.Empty,
        };
    }

    /// <summary>A world direction's yaw: east 0, north 90, west 180, south 270.</summary>
    private static float DirectionYaw(byte direction) => direction * 90f;

    private static Nav3dRoomRole ToNav(RoomRole role) => role switch
    {
        RoomRole.Up => Nav3dRoomRole.Up,
        RoomRole.Down => Nav3dRoomRole.Down,
        _ => Nav3dRoomRole.None,
    };

    private static void CheckSame(RoomNav first, RoomNav other, string room)
    {
        bool same = first.CellSize == other.CellSize && first.VoxelSize == other.VoxelSize && first.CellVoxels == other.CellVoxels
            && first.FloorNormalZ == other.FloorNormalZ && first.Agents.Count == other.Agents.Count;
        for (int a = 0; same && a < first.Agents.Count; a++)
        {
            same = first.Agents[a] == other.Agents[a];
        }

        if (!same)
        {
            throw new LinkException(
                $"room \"{room}\"'s navigation was built with other settings (voxel, slope or agents) than the level's first room's;"
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

    private static (Nav3dAgent Agent, int[][] LeafMaps, int[] LeafBase) LinkAgent(
        Placed[] placed, Doors doors, int agent, int columns, int rows, RoomNav first, CancellationToken cancellationToken)
    {
        int n = first.CellVoxels;
        int n3 = n * n * n;
        List<uint> nodes = [];
        List<Nav3dLeaf> leaves = [];
        int[] roots = new int[columns * rows];
        Array.Fill(roots, -1);
        int[] leafBase = new int[placed.Length];
        int[][] leafMaps = new int[placed.Length][];
        List<(int A, int B, bool Door, int DoorIndex)> edges = [];

        for (int i = 0; i < placed.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Placed p = placed[i];
            RoomNavAgent data = p.Nav.AgentData[agent];
            uint[] roomNodes = data.Nodes;
            RoomNavLeaf[] roomLeaves = data.Leaves;
            List<NavCapChange> changes = [];
            for (int s = 0; s < p.Nav.Sockets.Count; s++)
            {
                if (!doors.Records[doors.Index[i][s]].Joined)
                {
                    changes.AddRange(data.Sockets[s].Capped);
                }
            }

            if (changes.Count > 0)
            {
                ushort[] dense = NavOctree.Expand(roomNodes, roomLeaves, n);
                foreach (NavCapChange change in changes)
                {
                    int at = RoomNav.Index(change.Voxel, n);
                    if (change.Blocks)
                    {
                        dense[at] = NavVoxelGrid.Blocked;
                    }
                    else if ((dense[at] & NavVoxelGrid.FreeBit) != 0)
                    {
                        dense[at] |= (ushort)change.AddFlags;
                    }
                }

                (roomNodes, roomLeaves) = NavOctree.Build(dense, n);
            }

            int[] map = NavOctree.LeafMap(roomNodes, roomLeaves.Length, n);
            leafMaps[i] = map;
            int nodeBase = nodes.Count;
            leafBase[i] = leaves.Count;
            roots[p.Cell] = nodeBase;
            foreach (uint word in roomNodes)
            {
                nodes.Add(Nav3dFormat.KindOf(word) switch
                {
                    Nav3dNodeKind.Inner => Nav3dFormat.Node(Nav3dNodeKind.Inner, Nav3dFormat.PayloadOf(word) + (uint)nodeBase),
                    Nav3dNodeKind.Free => Nav3dFormat.Node(Nav3dNodeKind.Free, Nav3dFormat.PayloadOf(word) + (uint)leafBase[i]),
                    _ => word,
                });
            }

            RoomPlacement placement = p.Instance.Placement;
            int ox = placement.CellX * n;
            int oy = placement.CellY * n;
            foreach (RoomNavLeaf leaf in roomLeaves)
            {
                leaves.Add(new Nav3dLeaf(
                    (ushort)(leaf.X + ox), (ushort)(leaf.Y + oy), leaf.Z, leaf.SizeLog2, leaf.Flags, 0, (uint)p.Cell));
            }

            // Face adjacency inside the room: every voxel pair across a face
            // whose voxels lie in two different leaves.
            List<long> pairs = [];
            for (int z = 0; z < n; z++)
            {
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        int here = map[(((z * n) + y) * n) + x];
                        if (here < 0)
                        {
                            continue;
                        }

                        if (x + 1 < n)
                        {
                            AddPair(pairs, here, map[(((z * n) + y) * n) + x + 1]);
                        }

                        if (y + 1 < n)
                        {
                            AddPair(pairs, here, map[(((z * n) + y + 1) * n) + x]);
                        }

                        if (z + 1 < n)
                        {
                            AddPair(pairs, here, map[((((z + 1) * n) + y) * n) + x]);
                        }
                    }
                }
            }

            pairs.Sort();
            long previous = -1;
            foreach (long pair in pairs)
            {
                if (pair != previous)
                {
                    edges.Add((leafBase[i] + (int)(pair >> 32), leafBase[i] + (int)(pair & 0xFFFFFFFF), false, -1));
                    previous = pair;
                }
            }
        }

        // Door links: portal voxels that meet face to face across a joined door.
        List<Nav3dDoorLink> links = [];
        for (int i = 0; i < placed.Length; i++)
        {
            for (int s = 0; s < placed[i].Nav.Sockets.Count; s++)
            {
                int door = doors.Index[i][s];
                Nav3dDoor record = doors.Records[door];
                if (!record.Joined || record.Other < door)
                {
                    continue;
                }

                (int j, int t) = Owner(doors, record.Other);
                (int dx, int dy) = record.Direction switch
                {
                    0 => (1, 0),
                    1 => (0, 1),
                    2 => (-1, 0),
                    _ => (0, -1),
                };
                RoomPlacement pi = placed[i].Instance.Placement;
                RoomPlacement pj = placed[j].Instance.Placement;
                Dictionary<long, int> far = [];
                foreach (NavVoxel v in placed[j].Nav.AgentData[agent].Sockets[t].Portal)
                {
                    far[Key((pj.CellX * n) + v.X, (pj.CellY * n) + v.Y, v.Z)] = leafMaps[j][RoomNav.Index(v, n)];
                }

                SortedSet<(int, int)> joinedLeaves = [];
                foreach (NavVoxel v in placed[i].Nav.AgentData[agent].Sockets[s].Portal)
                {
                    int here = leafMaps[i][RoomNav.Index(v, n)];
                    if (here >= 0 && far.TryGetValue(Key((pi.CellX * n) + v.X + dx, (pi.CellY * n) + v.Y + dy, v.Z), out int there)
                        && there >= 0)
                    {
                        joinedLeaves.Add((leafBase[i] + here, leafBase[j] + there));
                    }
                }

                foreach ((int a, int b) in joinedLeaves)
                {
                    links.Add(new Nav3dDoorLink((uint)a, (uint)b, (uint)door));
                    edges.Add((a, b, true, door));
                }
            }
        }

        Nav3dLeaf[] leafArray = [.. leaves];
        foreach (Nav3dDoorLink link in links)
        {
            leafArray[link.LeafA] = leafArray[link.LeafA] with { Flags = leafArray[link.LeafA].Flags | Nav3dLeafFlags.Door };
            leafArray[link.LeafB] = leafArray[link.LeafB] with { Flags = leafArray[link.LeafB].Flags | Nav3dLeafFlags.Door };
        }

        // Components, numbered by their first leaf.
        int[] parent = new int[leafArray.Length];
        for (int l = 0; l < parent.Length; l++)
        {
            parent[l] = l;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        foreach ((int a, int b, _, _) in edges)
        {
            int ra = Find(a);
            int rb = Find(b);
            if (ra != rb)
            {
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        int[] componentOf = new int[leafArray.Length];
        Dictionary<int, int> number = [];
        List<(uint Leaves, ulong Voxels)> components = [];
        for (int l = 0; l < leafArray.Length; l++)
        {
            int root = Find(l);
            if (!number.TryGetValue(root, out int c))
            {
                c = components.Count;
                number[root] = c;
                components.Add((0, 0));
            }

            componentOf[l] = c;
            components[c] = (components[c].Leaves + 1, components[c].Voxels + (ulong)leafArray[l].Voxels);
            leafArray[l] = leafArray[l] with { Component = (uint)c };
        }

        // The neighbour lists, each in ascending leaf order.
        int[] degree = new int[leafArray.Length];
        foreach ((int a, int b, _, _) in edges)
        {
            degree[a]++;
            degree[b]++;
        }

        uint[] start = new uint[leafArray.Length + 1];
        for (int l = 0; l < leafArray.Length; l++)
        {
            start[l + 1] = start[l] + (uint)degree[l];
        }

        uint[] adjacency = new uint[start[^1]];
        int[] fill = new int[leafArray.Length];
        foreach ((int a, int b, bool throughDoor, _) in edges)
        {
            uint bit = throughDoor ? Nav3dFormat.ThroughDoorBit : 0;
            adjacency[start[a] + fill[a]++] = (uint)b | bit;
            adjacency[start[b] + fill[b]++] = (uint)a | bit;
        }

        for (int l = 0; l < leafArray.Length; l++)
        {
            Array.Sort(adjacency, (int)start[l], degree[l], LeafOrder.Instance);
        }

        Nav3dAgent linked = new(first.Agents[agent].Name, first.Agents[agent].Mins, first.Agents[agent].Maxs, first.Agents[agent].ContentsMask)
        {
            Roots = roots,
            Nodes = [.. nodes],
            Leaves = leafArray,
            AdjacencyStart = start,
            Adjacency = adjacency,
            Links = [.. links],
            Components = [.. components.Select(c => new Nav3dComponent(c.Leaves, (uint)Math.Min(c.Voxels, uint.MaxValue)))],
            PoiLeaves = [],
        };
        return (linked, leafMaps, leafBase);
    }

    /// <summary>The door point of a portal: on the cell face at the portal's middle, on its lowest voxels, facing out.</summary>
    private static (Vec3 Local, NavVoxel At) DoorPoint(IReadOnlyList<NavVoxel> portal, byte direction, RoomNav nav)
    {
        double s = nav.VoxelSize;
        bool alongX = direction is 1 or 3;
        int lo = int.MaxValue;
        int hi = int.MinValue;
        int bottom = int.MaxValue;
        foreach (NavVoxel v in portal)
        {
            int w = alongX ? v.X : v.Y;
            lo = Math.Min(lo, w);
            hi = Math.Max(hi, w);
            bottom = Math.Min(bottom, v.Z);
        }

        double middle = (lo + hi + 1) * s / 2;
        int wantW = (lo + hi) / 2;
        NavVoxel at = portal[0];
        foreach (NavVoxel v in portal)
        {
            if (v.Z == bottom && (alongX ? v.X : v.Y) == wantW)
            {
                at = v;
                break;
            }
        }

        float face = direction is 0 or 1 ? nav.CellSize : 0f;
        Vec3 local = alongX
            ? new Vec3((float)middle, face, (float)(bottom * s))
            : new Vec3(face, (float)middle, (float)(bottom * s));
        return (local, at);
    }

    private static (int Placement, int Socket) Owner(Doors doors, int door)
    {
        for (int i = 0; i < doors.Index.Length; i++)
        {
            int s = Array.IndexOf(doors.Index[i], door);
            if (s >= 0)
            {
                return (i, s);
            }
        }

        throw new InvalidOperationException($"door {door} belongs to no placement.");
    }

    private static void AddPair(List<long> pairs, int a, int b)
    {
        if (b < 0 || a == b)
        {
            return;
        }

        (int lo, int hi) = a < b ? (a, b) : (b, a);
        pairs.Add(((long)lo << 32) | (uint)hi);
    }

    private static long Key(int x, int y, int z) => ((long)x << 40) | ((long)y << 20) | (uint)z;

    private sealed record Placed(RoomInstance Instance, RoomNav Nav, int Cell, RoomTransform Transform);

    private sealed record Doors(List<Nav3dDoor> Records, int[][] Index);

    /// <summary>Orders adjacency entries by leaf, ignoring the door bit.</summary>
    private sealed class LeafOrder : IComparer<uint>
    {
        public static LeafOrder Instance { get; } = new();

        public int Compare(uint x, uint y) => (x & ~Nav3dFormat.ThroughDoorBit).CompareTo(y & ~Nav3dFormat.ThroughDoorBit);
    }
}
