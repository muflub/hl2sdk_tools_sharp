//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Rooms;

/// <summary>One jointed socket of a placed room, as the level's visibility sees it.</summary>
/// <param name="Socket">The socket's index in the room's definition.</param>
/// <param name="Neighbor">The index of the room on the other side, in the level's placement order.</param>
/// <param name="NeighborSocket">The socket of that room this one meets.</param>
/// <param name="Opening">
/// The doorway where the two rooms meet: the plug's face on the shared cell
/// face, in world coordinates, a box flat along the axis the socket faces.
/// </param>
/// <param name="Plug">This room's plug box, in world coordinates: the doorway the link carves on this side.</param>
/// <param name="Facing">The room-local clusters facing the plug; the first is the one the carved doorway joins.</param>
internal readonly record struct LevelDoor(int Socket, int Neighbor, int NeighborSocket, Box Opening, Box Plug, int[] Facing);

/// <summary>One placed room, as the level's visibility sees it.</summary>
internal sealed class LevelDoorRoom
{
    /// <summary>The room's first cluster in the level's numbering.</summary>
    public required int ClusterBase { get; init; }

    /// <summary>The room's door visibility (<see cref="RoomDoorVisibility"/>).</summary>
    public required RoomDoorVisibility Doors { get; init; }

    /// <summary>The room's own vvis rows, room-local.</summary>
    public required byte[][] OwnRows { get; init; }

    /// <summary>Where the room is placed.</summary>
    public required RoomTransform Transform { get; init; }

    /// <summary>Its jointed sockets, in the order the level lists them.</summary>
    public required LevelDoor[] Joints { get; init; }
}

/// <summary>The level's composed visibility: every row of the PVS and the PAS.</summary>
/// <param name="Pvs">The PVS rows, <paramref name="RowBytes"/> each, back to back.</param>
/// <param name="Pas">The PAS rows, likewise.</param>
/// <param name="RowBytes">Bytes per row.</param>
/// <param name="Visible">The set bits of the PVS.</param>
/// <param name="Audible">The set bits of the PAS.</param>
internal sealed record LevelVisibility(byte[] Pvs, byte[] Pas, int RowBytes, int Visible, int Audible);

/// <summary>
/// Composes a linked level's visibility from its rooms' own vvis and door
/// visibility (<see cref="RoomDoorVisibility"/>) and the level's doorways,
/// without flooding the level and without running vvis on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it replaces.</b> The link used to join every cluster facing a
/// joint to every cluster facing it from the other side and close the rows
/// transitively. Every room of a level is reachable, so that made every
/// cluster see every other: correct, and as loose as a PVS can be. This
/// keeps the rule that nothing the flattened level's vvis would see is
/// dropped, and asks of every pair of rooms whether a straight line can get
/// from one to the other through the doorways between them.
/// </para>
/// <para>
/// <b>The door flow.</b> Rooms are sealed but for their sockets and lie
/// inside their cells, which are convex, so a sight line between two rooms
/// crosses a chain of doorway rectangles on the cell faces, one room at a
/// time, never entering a room twice. For each doorway, looked through from
/// one side, the flow walks those chains the way vvis walks portals (the
/// doorways are the portals, the rooms the leaves): the next doorway is cut
/// to the part in front of the first, the first is cut to the part behind
/// the next, and from the third doorway on the next is clipped by the
/// planes separating the first from the last (<see cref="VisClip"/>, the
/// very predicates vvis uses). A chain whose rectangle is cut away is
/// abandoned. Each room the walk enters marks the clusters that see the
/// doorway it came in by (<see cref="RoomDoorVisibility.Sees"/>) whose
/// bounds reach past every separating plane of the current cone; the walk
/// only turns from one doorway of a room to another where the room's own
/// vvis says a line can cross it (<see cref="RoomDoorVisibility.Through"/>).
/// Treating each room as its empty cell only adds lines, and each room's
/// relations come from its own vvis, which keeps every line, so the result
/// keeps every line too.
/// </para>
/// <para>
/// <b>Both ends.</b> A cluster sees what the flows out of the doorways it
/// sees mark. That takes the whole doorway as where the line starts, so a
/// cluster in a far corner inherits what the doorway's centre sees; the
/// flow the other way round tests this cluster's own bounds instead. A pair
/// is kept only when both directions keep it: a real sight line is found
/// both ways, and vvis itself makes its rows symmetric the same way (a bit
/// survives only when the transposed bit is set).
/// </para>
/// <para>
/// <b>Inside a room</b> the rows are the room's own vvis, with one addition:
/// the doorway the link carves at a jointed socket joins the socket's first
/// facing cluster, so that cluster sees, and is seen by, every cluster that
/// sees the doorway, and its bounds grow by the plug box.
/// </para>
/// <para>
/// <b>Exact and rotation-free.</b> Each flow runs in the frame of the room
/// it starts from (<see cref="RoomTransform.Unapply"/>), and every doorway
/// and cluster box it reads is moved into that frame by quarter turns and
/// whole cells, which round nothing for the integer geometry rooms are
/// built of. A level turned as a whole therefore runs every flow on the same
/// numbers in the same order and composes the same rows, cluster for
/// cluster; and since each flow writes only its own room's rows and the
/// merge is by index, the result is the same at any thread count.
/// </para>
/// <para>
/// <b>PAS.</b> What vvis writes: each cluster hears what the clusters it
/// sees can see (a radius of two, not a closure).
/// </para>
/// </remarks>
internal static class LevelDoorVisibility
{
    /// <summary>
    /// How far outside a separating plane a cluster's bounds may lie and
    /// still be marked: generous, so rounding in the clipped windings never
    /// decides a box that grazes the cone.
    /// </summary>
    internal const float BoxMargin = 1f;

    /// <summary>
    /// The most rooms one flow may enter before it stops walking and marks
    /// every room instead: far above what a straight line crosses in any
    /// level the format holds, so it only guards against a pathological
    /// layout turning one flow into an unbounded walk.
    /// </summary>
    internal const int DefaultStateCap = 1 << 22;

    private const int WindingRoom = VisClip.MaxPointsOnWinding;

    /// <summary>Composes the level's PVS and PAS.</summary>
    /// <param name="rooms">The placed rooms, in the level's order, cluster bases ascending.</param>
    /// <param name="clusterCount">The level's clusters.</param>
    /// <param name="parallelism">How many threads the flows may use.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <param name="stateCap">The most rooms one flow may enter (<see cref="DefaultStateCap"/>).</param>
    /// <returns>The rows.</returns>
    public static async Task<LevelVisibility> ComposeAsync(
        IReadOnlyList<LevelDoorRoom> rooms,
        int clusterCount,
        CompileParallelism parallelism,
        CancellationToken cancellationToken,
        int stateCap = DefaultStateCap)
    {
        int words = (clusterCount + 63) >> 6;
        Prepared[] prepared = [.. rooms.Select(Prepare)];

        // Per cluster, what the flows out of the doorways it sees mark. Each
        // room's work item writes its own clusters' rows only.
        ulong[][] forward = new ulong[clusterCount][];
        using (WorkQueue queue = new(parallelism))
        {
            await queue.RunAsync<FlowScratch, bool>(
                rooms.Count,
                (index, scratch, worker) =>
                {
                    FlowRoom(rooms, prepared, index, words, forward, scratch, stateCap, cancellationToken);
                    return true;
                },
                _ => new FlowScratch(rooms.Count, words),
                options: null,
                cancellationToken).ConfigureAwait(false);
        }

        // Rows: the room's own (with its doorways), then every cross-room
        // pair both directions keep, then the cluster itself.
        int rowBytes = (clusterCount + 7) >> 3;
        ulong[][] pvs = new ulong[clusterCount][];
        for (int r = 0; r < rooms.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LevelDoorRoom room = rooms[r];
            Prepared p = prepared[r];
            int lo = room.ClusterBase;
            int hi = lo + room.Doors.ClusterCount;
            for (int c = 0; c < room.Doors.ClusterCount; c++)
            {
                int x = lo + c;
                ulong[] row = new ulong[words];
                ulong[] own = p.Own[c];
                for (int z = 0; z < room.Doors.ClusterCount; z++)
                {
                    if ((own[z >> 6] & (1UL << (z & 63))) != 0)
                    {
                        Set(row, lo + z);
                    }
                }

                ulong[] a = forward[x];
                for (int w = 0; w < words; w++)
                {
                    for (ulong bits = a[w]; bits != 0; bits &= bits - 1)
                    {
                        int y = (w << 6) + BitOperations.TrailingZeroCount(bits);
                        if ((y < lo || y >= hi) && Has(forward[y], x))
                        {
                            Set(row, y);
                        }
                    }
                }

                Set(row, x);
                pvs[x] = row;
            }
        }

        // The PAS: what vvis writes, the union of the rows of every cluster
        // a cluster sees (radius two).
        ulong[][] pas = new ulong[clusterCount][];
        using (WorkQueue queue = new(parallelism))
        {
            const int Chunk = 64;
            await queue.RunAsync(
                (clusterCount + Chunk - 1) / Chunk,
                (chunk, _) =>
                {
                    for (int x = chunk * Chunk; x < Math.Min(clusterCount, (chunk + 1) * Chunk); x++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ulong[] scan = pvs[x];
                        ulong[] row = (ulong[])scan.Clone();
                        for (int w = 0; w < words; w++)
                        {
                            for (ulong bits = scan[w]; bits != 0; bits &= bits - 1)
                            {
                                Or(row, pvs[(w << 6) + BitOperations.TrailingZeroCount(bits)]);
                            }
                        }

                        pas[x] = row;
                    }
                },
                options: null,
                cancellationToken).ConfigureAwait(false);
        }

        byte[] pvsBytes = new byte[clusterCount * rowBytes];
        byte[] pasBytes = new byte[clusterCount * rowBytes];
        int visible = 0, audible = 0;
        for (int x = 0; x < clusterCount; x++)
        {
            visible += ToBytes(pvs[x], pvsBytes.AsSpan(x * rowBytes, rowBytes));
            audible += ToBytes(pas[x], pasBytes.AsSpan(x * rowBytes, rowBytes));
        }

        return new LevelVisibility(pvsBytes, pasBytes, rowBytes, visible, audible);
    }

    /// <summary>A room's link-time view: its own rows and what sees each doorway, with its carved doorways joined in.</summary>
    private sealed class Prepared
    {
        public required ulong[][] Own { get; init; }

        /// <summary>Per socket, the clusters that see its doorway, carved doorways included.</summary>
        public required ulong[][] Sees { get; init; }

        /// <summary>Per cluster, its bounds in world coordinates, the carved doorway's plug box included.</summary>
        public required Box[] Boxes { get; init; }

        public required bool[] HasBox { get; init; }
    }

    private static Prepared Prepare(LevelDoorRoom room)
    {
        RoomDoorVisibility doors = room.Doors;
        int clusters = doors.ClusterCount;
        int localWords = doors.Words;
        ulong[][] own = new ulong[clusters][];
        for (int c = 0; c < clusters; c++)
        {
            ulong[] row = new ulong[localWords];
            byte[] bytes = room.OwnRows[c];
            for (int z = 0; z < clusters; z++)
            {
                if ((bytes[z >> 3] & (1 << (z & 7))) != 0)
                {
                    row[z >> 6] |= 1UL << (z & 63);
                }
            }

            own[c] = row;
        }

        ulong[][] sees = [.. doors.Sees.Select(s => (ulong[])s.Clone())];
        int rotation = room.Transform.Placement.NormalizedRotation;
        Box[] boxes = new Box[clusters];
        bool[] hasBox = (bool[])doors.HasBox.Clone();
        for (int c = 0; c < clusters; c++)
        {
            if (hasBox[c])
            {
                Box local = doors.ClusterBoxes[c];
                boxes[c] = room.Transform.TranslateBox(LevelLinker.RotateBox(local.Mins, local.Maxs, rotation));
            }
        }

        // A carved doorway is open space in its socket's first facing
        // cluster: that cluster now sees, and is seen by, whatever sees the
        // doorway, sees every doorway a line from this one can reach through
        // the room, and holds the plug box.
        foreach (LevelDoor joint in room.Joints)
        {
            int f = joint.Facing[0];
            ulong[] seesDoor = doors.Sees[joint.Socket];
            for (int z = 0; z < clusters; z++)
            {
                if ((seesDoor[z >> 6] & (1UL << (z & 63))) != 0)
                {
                    own[f][z >> 6] |= 1UL << (z & 63);
                    own[z][f >> 6] |= 1UL << (f & 63);
                }
            }

            for (int t = 0; t < doors.SocketCount; t++)
            {
                if (t == joint.Socket || doors.IsThrough(joint.Socket, t))
                {
                    sees[t][f >> 6] |= 1UL << (f & 63);
                }
            }

            boxes[f] = hasBox[f] ? RoomDoorVisibility.Union(boxes[f], joint.Plug) : joint.Plug;
            hasBox[f] = true;
        }

        return new Prepared { Own = own, Sees = sees, Boxes = boxes, HasBox = hasBox };
    }

    /// <summary>What one worker reuses from flow to flow.</summary>
    private sealed class FlowScratch(int rooms, int words)
    {
        public bool[] Visited { get; } = new bool[rooms];

        public ulong[] Marked { get; } = new ulong[words];

        public List<Frame> Stack { get; } = [];

        /// <summary>Per depth, the source and pass windings of the frame at that depth.</summary>
        public List<(Vec3[] Source, Vec3[] Pass)> Windings { get; } = [];

        public Vec3[] Chop { get; } = new Vec3[WindingRoom];

        public Vec3[] Clip { get; } = new Vec3[WindingRoom];

        public Vec3[] Door { get; } = new Vec3[4];

        public Vec3[] Normals { get; } = new Vec3[WindingRoom * WindingRoom];

        public float[] Distances { get; } = new float[WindingRoom * WindingRoom];

        public Vec3[] Normals2 { get; } = new Vec3[WindingRoom * WindingRoom];

        public float[] Distances2 { get; } = new float[WindingRoom * WindingRoom];

        public (Vec3[] Source, Vec3[] Pass) At(int depth)
        {
            while (Windings.Count <= depth)
            {
                Windings.Add((new Vec3[WindingRoom], new Vec3[WindingRoom]));
            }

            return Windings[depth];
        }
    }

    /// <summary>One room a flow is in: how it got there, and which of its doorways it tries next.</summary>
    private struct Frame
    {
        public int Room;
        public int Entry;
        public int Cursor;
        public int SourceCount;
        public int PassCount; // 0: entered through the flow's first doorway, no pass yet
    }

    /// <summary>
    /// Every flow out of one room: per jointed socket, the flow through its
    /// doorway, OR-ed into the forward rows of the clusters that see it.
    /// </summary>
    private static void FlowRoom(
        IReadOnlyList<LevelDoorRoom> rooms,
        Prepared[] prepared,
        int index,
        int words,
        ulong[][] forward,
        FlowScratch scratch,
        int stateCap,
        CancellationToken cancellationToken)
    {
        LevelDoorRoom room = rooms[index];
        Prepared mine = prepared[index];
        for (int c = 0; c < room.Doors.ClusterCount; c++)
        {
            forward[room.ClusterBase + c] = new ulong[words];
        }

        foreach (LevelDoor joint in room.Joints)
        {
            Array.Clear(scratch.Marked);
            Flow(rooms, prepared, index, joint, scratch, stateCap, cancellationToken);
            ulong[] sees = mine.Sees[joint.Socket];
            for (int c = 0; c < room.Doors.ClusterCount; c++)
            {
                if ((sees[c >> 6] & (1UL << (c & 63))) != 0)
                {
                    Or(forward[room.ClusterBase + c], scratch.Marked);
                }
            }
        }
    }

    /// <summary>
    /// The flow through one doorway, looked through from room
    /// <paramref name="from"/>: marks in <see cref="FlowScratch.Marked"/>
    /// every cluster beyond it a line through it can reach.
    /// </summary>
    private static void Flow(
        IReadOnlyList<LevelDoorRoom> rooms,
        Prepared[] prepared,
        int from,
        LevelDoor first,
        FlowScratch scratch,
        int stateCap,
        CancellationToken cancellationToken)
    {
        RoomTransform frame = rooms[from].Transform;
        (Vec3 baseNormal, float baseDistance) = DoorPlane(frame, first.Opening, rooms[first.Neighbor].Transform);
        Winding(ToFrame(frame, first.Opening), scratch.Door);

        List<Frame> stack = scratch.Stack;
        stack.Clear();
        bool[] visited = scratch.Visited;
        Array.Clear(visited);
        visited[from] = true;
        visited[first.Neighbor] = true;

        (Vec3[] source0, _) = scratch.At(0);
        scratch.Door.CopyTo(source0, 0);
        Mark(rooms, prepared, frame, first.Neighbor, first.NeighborSocket, source0.AsSpan(0, 4), [], baseNormal, baseDistance, scratch);
        stack.Add(new Frame { Room = first.Neighbor, Entry = first.NeighborSocket, SourceCount = 4 });

        int states = 1;
        while (stack.Count > 0)
        {
            int depth = stack.Count - 1;
            Frame f = stack[depth];
            LevelDoorRoom room = rooms[f.Room];
            if (f.Cursor >= room.Joints.Length)
            {
                // Another chain may reach this room by another way, with another cone.
                visited[f.Room] = depth == 0;
                stack.RemoveAt(depth);
                continue;
            }

            LevelDoor next = room.Joints[f.Cursor];
            f.Cursor++;
            stack[depth] = f;
            if (next.Socket == f.Entry || visited[next.Neighbor] || !room.Doors.IsThrough(f.Entry, next.Socket))
            {
                continue;
            }

            if (++states > stateCap)
            {
                // A walk this long is no level a line can cross: give up on
                // it, and keep every cluster of the level rather than guess.
                Array.Fill(scratch.Marked, ulong.MaxValue);
                stack.Clear();
                return;
            }

            if ((states & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            (Vec3 normal, float distance) = DoorPlane(frame, next.Opening, rooms[next.Neighbor].Transform);
            if (normal == baseNormal && distance == baseDistance)
            {
                continue; // a line crosses the first doorway's plane once
            }

            if (normal == -baseNormal && distance == -baseDistance)
            {
                continue;
            }

            (Vec3[] source, Vec3[] pass) = scratch.At(depth);
            (Vec3[] nextSource, Vec3[] nextPass) = scratch.At(depth + 1);
            Winding(ToFrame(frame, next.Opening), scratch.Door);

            // The next doorway, cut to what lies in front of the first.
            VisChopResult chopped = VisClip.ChopWinding(scratch.Door, baseNormal, baseDistance, scratch.Chop, out int passCount);
            if (chopped == VisChopResult.Empty)
            {
                continue;
            }

            ReadOnlySpan<Vec3> candidate = chopped == VisChopResult.Clipped ? scratch.Chop.AsSpan(0, passCount) : scratch.Door;

            // The first doorway, cut to what lies behind the next.
            chopped = VisClip.ChopWinding(source.AsSpan(0, f.SourceCount), -normal, -distance, nextSource, out int sourceCount);
            if (chopped == VisChopResult.Empty)
            {
                continue;
            }

            if (chopped == VisChopResult.Unchanged)
            {
                source.AsSpan(0, f.SourceCount).CopyTo(nextSource);
                sourceCount = f.SourceCount;
            }

            ReadOnlySpan<Vec3> newSource = nextSource.AsSpan(0, sourceCount);
            int newPassCount;
            if (f.PassCount == 0)
            {
                candidate.CopyTo(nextPass);
                newPassCount = candidate.Length;
            }
            else
            {
                ReadOnlySpan<Vec3> prevPass = pass.AsSpan(0, f.PassCount);
                if (!VisClip.ClipToSeparators(newSource, prevPass, candidate, false, scratch.Clip, out int firstCount))
                {
                    continue;
                }

                if (!VisClip.ClipToSeparators(prevPass, newSource, scratch.Clip.AsSpan(0, firstCount), true, nextPass, out newPassCount))
                {
                    continue;
                }
            }

            Mark(rooms, prepared, frame, next.Neighbor, next.NeighborSocket, newSource, nextPass.AsSpan(0, newPassCount), baseNormal, baseDistance, scratch);
            visited[next.Neighbor] = true;
            stack.Add(new Frame { Room = next.Neighbor, Entry = next.NeighborSocket, SourceCount = sourceCount, PassCount = newPassCount });
        }
    }

    /// <summary>
    /// Marks the clusters of the room a flow just entered: those that see
    /// the doorway it came in by, and whose bounds are not wholly behind
    /// the first doorway's plane or any plane separating the source from
    /// the pass.
    /// </summary>
    private static void Mark(
        IReadOnlyList<LevelDoorRoom> rooms,
        Prepared[] prepared,
        RoomTransform frame,
        int roomIndex,
        int entry,
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        Vec3 baseNormal,
        float baseDistance,
        FlowScratch scratch)
    {
        LevelDoorRoom room = rooms[roomIndex];
        Prepared p = prepared[roomIndex];
        ulong[] sees = p.Sees[entry];
        ulong[] marked = scratch.Marked;
        int forward = 0, backward = 0;
        bool test = !pass.IsEmpty;
        if (test)
        {
            forward = VisClip.BuildSeparators(source, pass, scratch.Normals, scratch.Distances);
            backward = VisClip.BuildSeparators(pass, source, scratch.Normals2, scratch.Distances2);
            test = forward >= 0 && backward >= 0; // a list too long for the buffers culls nothing
        }

        for (int c = 0; c < room.Doors.ClusterCount; c++)
        {
            int x = room.ClusterBase + c;
            if ((sees[c >> 6] & (1UL << (c & 63))) == 0 || Has(marked, x))
            {
                continue;
            }

            if (p.HasBox[c])
            {
                Box box = ToFrame(frame, p.Boxes[c]);
                if (Behind(box, baseNormal, baseDistance))
                {
                    continue;
                }

                if (test && (AnyBehind(box, scratch.Normals, scratch.Distances, forward, flip: false)
                    || AnyBehind(box, scratch.Normals2, scratch.Distances2, backward, flip: true)))
                {
                    continue;
                }
            }

            Set(marked, x);
        }
    }

    private static bool AnyBehind(Box box, Vec3[] normals, float[] distances, int count, bool flip)
    {
        for (int i = 0; i < count; i++)
        {
            Vec3 n = flip ? -normals[i] : normals[i];
            float d = flip ? -distances[i] : distances[i];
            if (Behind(box, n, d))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether every point of the box is more than <see cref="BoxMargin"/> behind the plane.</summary>
    private static bool Behind(Box box, Vec3 normal, float distance)
    {
        // The corner farthest along the normal.
        Vec3 far = new(
            normal.X >= 0 ? box.Maxs.X : box.Mins.X,
            normal.Y >= 0 ? box.Maxs.Y : box.Mins.Y,
            normal.Z >= 0 ? box.Maxs.Z : box.Mins.Z);
        return Vec3.Dot(far, normal) - distance < -BoxMargin;
    }

    /// <summary>
    /// A doorway's plane in the flow's frame, its normal pointing into
    /// <paramref name="entering"/>'s cell, the room the doorway leads to.
    /// </summary>
    private static (Vec3 Normal, float Distance) DoorPlane(RoomTransform frame, Box opening, RoomTransform entering)
    {
        Box local = ToFrame(frame, opening);
        int axis = local.Mins.X == local.Maxs.X ? 0 : local.Mins.Y == local.Maxs.Y ? 1 : 2;
        Vec3 into = frame.Unapply(CellCentre(entering.Placement, entering.CellSize));
        float at = Component(local.Mins, axis);
        float sign = Component(into, axis) > at ? 1f : -1f;
        Vec3 normal = axis switch
        {
            0 => new Vec3(sign, 0, 0),
            1 => new Vec3(0, sign, 0),
            _ => new Vec3(0, 0, sign),
        };

        return (normal, sign * at);
    }

    private static Vec3 CellCentre(RoomPlacement placement, float cell) =>
        new((placement.CellX * cell) + (cell / 2), (placement.CellY * cell) + (cell / 2), 0);

    private static float Component(Vec3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    /// <summary>A world box in the frame of a room: its corners through <see cref="RoomTransform.Unapply"/>, exact for integer geometry.</summary>
    internal static Box ToFrame(RoomTransform frame, Box world)
    {
        Vec3 a = frame.Unapply(world.Mins);
        Vec3 b = frame.Unapply(world.Maxs);
        return new Box(
            new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
            new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }

    /// <summary>A flat box's rectangle as a four-point winding, in a fixed corner order.</summary>
    private static void Winding(Box flat, Vec3[] into)
    {
        Vec3 lo = flat.Mins, hi = flat.Maxs;
        if (lo.X == hi.X)
        {
            into[0] = new Vec3(lo.X, lo.Y, lo.Z);
            into[1] = new Vec3(lo.X, hi.Y, lo.Z);
            into[2] = new Vec3(lo.X, hi.Y, hi.Z);
            into[3] = new Vec3(lo.X, lo.Y, hi.Z);
        }
        else if (lo.Y == hi.Y)
        {
            into[0] = new Vec3(lo.X, lo.Y, lo.Z);
            into[1] = new Vec3(hi.X, lo.Y, lo.Z);
            into[2] = new Vec3(hi.X, lo.Y, hi.Z);
            into[3] = new Vec3(lo.X, lo.Y, hi.Z);
        }
        else
        {
            into[0] = new Vec3(lo.X, lo.Y, lo.Z);
            into[1] = new Vec3(hi.X, lo.Y, lo.Z);
            into[2] = new Vec3(hi.X, hi.Y, lo.Z);
            into[3] = new Vec3(lo.X, hi.Y, lo.Z);
        }
    }

    private static bool Has(ulong[] row, int bit) => (row[bit >> 6] & (1UL << (bit & 63))) != 0;

    private static void Set(ulong[] row, int bit) => row[bit >> 6] |= 1UL << (bit & 63);

    private static void Or(ulong[] into, ulong[] from)
    {
        for (int w = 0; w < into.Length; w++)
        {
            into[w] |= from[w];
        }
    }

    /// <summary>A row of words as bytes (no bit past the clusters is ever set); returns its set bits.</summary>
    private static int ToBytes(ulong[] row, Span<byte> into)
    {
        int count = 0;
        for (int b = 0; b < into.Length; b++)
        {
            into[b] = (byte)(row[b >> 3] >> (8 * (b & 7)));
            count += BitOperations.PopCount(into[b]);
        }

        return count;
    }
}
