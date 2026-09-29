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
    internal static long DiagStates, DiagMarks, DiagTries;

    /// <summary>Composes the level's PVS and PAS.</summary>
    /// <param name="rooms">The placed rooms, in the level's order, cluster bases ascending.</param>
    /// <param name="clusterCount">The level's clusters.</param>
    /// <param name="parallelism">How many threads the flows may use.</param>
    /// <param name="stateCap">The most rooms one flow may enter (<see cref="DefaultStateCap"/>).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rows.</returns>
    public static async Task<LevelVisibility> ComposeAsync(
        IReadOnlyList<LevelDoorRoom> rooms,
        int clusterCount,
        CompileParallelism parallelism,
        int stateCap,
        CancellationToken cancellationToken)
    {
        var swT = System.Diagnostics.Stopwatch.StartNew();
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
                _ => new FlowScratch(rooms.Count, clusterCount),
                options: null,
                cancellationToken).ConfigureAwait(false);
        }

        System.Console.Error.WriteLine($"TIMING flows {swT.ElapsedMilliseconds} states {System.Threading.Interlocked.Read(ref DiagStates)} marks {DiagMarks} tries {DiagTries}"); swT.Restart();
        // Rows: the room's own (with its doorways), then every cross-room
        // pair both directions keep, then the cluster itself. A pair in two
        // rooms that share a cell face must also pass the one doorway
        // between them (Adjacent), and a pair in two that share a face and
        // no doorway cannot see each other at all.
        int rowBytes = (clusterCount + 7) >> 3;
        int[] roomOf = new int[clusterCount];
        Dictionary<(int X, int Y), int> byCell = new(rooms.Count);
        for (int r = 0; r < rooms.Count; r++)
        {
            Array.Fill(roomOf, r, rooms[r].ClusterBase, rooms[r].Doors.ClusterCount);
            byCell[(rooms[r].Transform.Placement.CellX, rooms[r].Transform.Placement.CellY)] = r;
        }

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
                        if ((y < lo || y >= hi) && Has(forward[y], x) && Adjacent(rooms, prepared, roomOf, r, x, y))
                        {
                            Set(row, y);
                        }
                    }
                }

                Set(row, x);
                pvs[x] = row;
            }
        }

        System.Console.Error.WriteLine($"TIMING rows {swT.ElapsedMilliseconds}"); swT.Restart();
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

        System.Console.Error.WriteLine($"TIMING pas {swT.ElapsedMilliseconds}"); swT.Restart();
        byte[] pvsBytes = new byte[clusterCount * rowBytes];
        byte[] pasBytes = new byte[clusterCount * rowBytes];
        int visible = 0, audible = 0;
        for (int x = 0; x < clusterCount; x++)
        {
            visible += ToBytes(pvs[x], pvsBytes.AsSpan(x * rowBytes, rowBytes));
            audible += ToBytes(pas[x], pasBytes.AsSpan(x * rowBytes, rowBytes));
        }

        System.Console.Error.WriteLine($"TIMING bytes {swT.ElapsedMilliseconds}");
        return new LevelVisibility(pvsBytes, pasBytes, rowBytes, visible, audible);
    }

    /// <summary>
    /// Whether clusters <paramref name="x"/> (of room <paramref name="r"/>)
    /// and <paramref name="y"/> can see each other as far as the cell face
    /// between their rooms goes: always when the rooms do not share a face;
    /// never when they share one and no doorway; and when they share a
    /// doorway, only if some segment from one cluster's bounds to the
    /// other's crosses the doorway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two neighbouring cells make one convex box, so a sight line between
    /// them stays inside the two and crosses their shared face, which is
    /// wall but for the doorway: the doorway is the only way, and whether a
    /// line can get through it is a question of the two boxes alone. The
    /// flows cannot ask it (a flow starts from the whole doorway, and the
    /// room beyond it has no cone to test yet), which made neighbouring
    /// rooms the loosest part of the composition.
    /// </para>
    /// <para>
    /// The points where segments between two boxes on either side of a plane
    /// cross it make a convex set: the plane's cut through the hull of the
    /// two boxes, which is the hull of where the segments between their
    /// corners cross. Its bounds, widened by <see cref="BoxMargin"/>, must
    /// meet the doorway's rectangle. The test runs in each room's frame and
    /// passes if either passes, so it is symmetric and, like the flows, the
    /// same numbers in a level turned as a whole.
    /// </para>
    /// </remarks>
    private static bool Adjacent(IReadOnlyList<LevelDoorRoom> rooms, Prepared[] prepared, int[] roomOf, int r, int x, int y)
    {
        int other = roomOf[y];
        RoomPlacement a = rooms[r].Transform.Placement, b = rooms[other].Transform.Placement;
        if (Math.Abs(a.CellX - b.CellX) + Math.Abs(a.CellY - b.CellY) != 1)
        {
            return true;
        }

        LevelDoor? door = null;
        foreach (LevelDoor joint in rooms[r].Joints)
        {
            if (joint.Neighbor == other)
            {
                door = joint;
            }
        }

        if (door is not { } shared)
        {
            return false;
        }

        int cx = x - rooms[r].ClusterBase, cy = y - rooms[other].ClusterBase;
        Prepared px = prepared[r], py = prepared[other];
        if (!px.HasBox[cx] || !py.HasBox[cy])
        {
            return true;
        }

        return Through(rooms[r].Transform, px.Boxes[cx], py.Boxes[cy], shared.Opening)
            || Through(rooms[other].Transform, px.Boxes[cx], py.Boxes[cy], shared.Opening);
    }

    /// <summary>Whether a segment from box <paramref name="near"/> to box <paramref name="far"/> can cross the doorway, in one room's frame.</summary>
    internal static bool Through(RoomTransform frame, Box nearWorld, Box farWorld, Box openingWorld)
    {
        Box near = ToFrame(frame, nearWorld), far = ToFrame(frame, farWorld), opening = ToFrame(frame, openingWorld);
        int axis = opening.Mins.X == opening.Maxs.X ? 0 : opening.Mins.Y == opening.Maxs.Y ? 1 : 2;
        int u = axis == 0 ? 1 : 0, v = axis == 2 ? 1 : 2;
        float plane = Component(opening.Mins, axis);

        // Which side the far box is on, and each box cut to its own side.
        float sign = Component(far.Mins, axis) + Component(far.Maxs, axis) >= Component(near.Mins, axis) + Component(near.Maxs, axis) ? 1f : -1f;
        (float nearLo, float nearHi) = (Component(near.Mins, axis), Component(near.Maxs, axis));
        (float farLo, float farHi) = (Component(far.Mins, axis), Component(far.Maxs, axis));
        if (sign > 0)
        {
            nearHi = Math.Min(nearHi, plane);
            farLo = Math.Max(farLo, plane);
        }
        else
        {
            nearLo = Math.Max(nearLo, plane);
            farHi = Math.Min(farHi, plane);
        }

        if (nearLo > nearHi || farLo > farHi)
        {
            return true; // a box wholly past the doorway's plane: nothing to decide by
        }

        float uLo = float.MaxValue, uHi = float.MinValue, vLo = float.MaxValue, vHi = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            Vec3 p = Corner(near, i, axis, nearLo, nearHi);
            float fp = sign * (Component(p, axis) - plane);
            for (int j = 0; j < 8; j++)
            {
                Vec3 q = Corner(far, j, axis, farLo, farHi);
                float fq = sign * (Component(q, axis) - plane);
                float t = fp == fq ? 0f : fp / (fp - fq);
                float pu = Component(p, u), pv = Component(p, v);
                float cu = pu + (t * (Component(q, u) - pu));
                float cv = pv + (t * (Component(q, v) - pv));
                uLo = Math.Min(uLo, cu);
                uHi = Math.Max(uHi, cu);
                vLo = Math.Min(vLo, cv);
                vHi = Math.Max(vHi, cv);
                if (fp == fq)
                {
                    // Both on the plane: the whole segment lies in it.
                    float qu = Component(q, u), qv = Component(q, v);
                    uLo = Math.Min(uLo, qu);
                    uHi = Math.Max(uHi, qu);
                    vLo = Math.Min(vLo, qv);
                    vHi = Math.Max(vHi, qv);
                }
            }
        }

        return uLo - BoxMargin <= Component(opening.Maxs, u) && Component(opening.Mins, u) <= uHi + BoxMargin
            && vLo - BoxMargin <= Component(opening.Maxs, v) && Component(opening.Mins, v) <= vHi + BoxMargin;
    }

    /// <summary>Corner <paramref name="i"/> of a box, its extent along <paramref name="axis"/> replaced by the cut one.</summary>
    private static Vec3 Corner(Box box, int i, int axis, float lo, float hi)
    {
        float x = axis == 0 ? ((i & 1) == 0 ? lo : hi) : (i & 1) == 0 ? box.Mins.X : box.Maxs.X;
        float y = axis == 1 ? ((i & 2) == 0 ? lo : hi) : (i & 2) == 0 ? box.Mins.Y : box.Maxs.Y;
        float z = axis == 2 ? ((i & 4) == 0 ? lo : hi) : (i & 4) == 0 ? box.Mins.Z : box.Maxs.Z;
        return new Vec3(x, y, z);
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
                boxes[c] = room.Transform.TranslateBox(doors.TurnedBox(c, rotation));
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
    private sealed class FlowScratch(int rooms, int clusters)
    {
        public bool[] Visited { get; } = new bool[rooms];

        public ulong[] Marked { get; } = new ulong[(clusters + 63) >> 6];

        /// <summary>Marks every cluster of the level, and no bit past the last.</summary>
        public void MarkAll()
        {
            Array.Fill(Marked, ulong.MaxValue);
            if ((clusters & 63) != 0)
            {
                Marked[^1] = (1UL << (clusters & 63)) - 1;
            }
        }

        public List<Frame> Stack { get; } = [];

        /// <summary>Per depth, the windings and separating planes of the frame at that depth.</summary>
        public List<Level> Levels { get; } = [];

        public Vec3[] Chop { get; } = new Vec3[WindingRoom];

        public Vec3[] Clip { get; } = new Vec3[WindingRoom];

        public Vec3[] Door { get; } = new Vec3[4];

        public Level At(int depth)
        {
            while (Levels.Count <= depth)
            {
                Levels.Add(new Level());
            }

            return Levels[depth];
        }
    }

    /// <summary>
    /// One depth of a flow: the source and pass windings of the frame there
    /// and, worked out once when first wanted, the planes separating them
    /// both ways (<see cref="VisClip.BuildSeparators"/>).
    /// </summary>
    /// <remarks>
    /// The same planes serve twice: to test the bounds of the room's
    /// clusters when the flow enters it, and to clip every doorway out of
    /// the room whose source the doorway's plane leaves whole, which is
    /// nearly every one (the source is the first doorway, far behind). vvis
    /// keeps the same memo per frame; the clip by stored planes is the same
    /// function as the clip that derives them (<see cref="VisClip.ClipToSeparatorPlanes"/>).
    /// </remarks>
    private sealed class Level
    {
        /// <summary>Enough for every edge-vertex pair of two windings a chop leaves (at most twelve points each).</summary>
        private const int Planes = VisClip.MaxPointsOnFixedWinding * VisClip.MaxPointsOnFixedWinding;

        public Vec3[] Source { get; } = new Vec3[WindingRoom];

        public Vec3[] Pass { get; } = new Vec3[WindingRoom];

        public Vec3[] ForwardNormals { get; } = new Vec3[Planes];

        public float[] ForwardDistances { get; } = new float[Planes];

        public Vec3[] BackwardNormals { get; } = new Vec3[Planes];

        public float[] BackwardDistances { get; } = new float[Planes];

        public int SourceCount { get; set; }

        public int PassCount { get; set; }

        /// <summary>-1 until worked out; then the forward planes' count, or -2 when they did not fit.</summary>
        public int Forward { get; set; } = -1;

        public int Backward { get; set; }

        public ReadOnlySpan<Vec3> SourceSpan => Source.AsSpan(0, SourceCount);

        public ReadOnlySpan<Vec3> PassSpan => Pass.AsSpan(0, PassCount);

        public void Reset(int sourceCount, int passCount)
        {
            SourceCount = sourceCount;
            PassCount = passCount;
            Forward = -1;
        }

        /// <summary>Works out the separating planes, once; false when there is no pass yet or they did not fit.</summary>
        public bool Separators()
        {
            if (PassCount == 0)
            {
                return false;
            }

            if (Forward == -1)
            {
                System.Threading.Interlocked.Increment(ref DiagMarks);
                int forward = VisClip.BuildSeparators(SourceSpan, PassSpan, ForwardNormals, ForwardDistances);
                int backward = VisClip.BuildSeparators(PassSpan, SourceSpan, BackwardNormals, BackwardDistances);
                Forward = forward < 0 || backward < 0 ? -2 : forward;
                Backward = backward;
            }

            return Forward >= 0;
        }
    }

    /// <summary>One room a flow is in: how it got there, and which of its doorways it tries next.</summary>
    private struct Frame
    {
        public int Room;
        public int Entry;
        public int Cursor;
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

        Level top = scratch.At(0);
        scratch.Door.CopyTo(top.Source, 0);
        top.Reset(4, 0);
        Mark(rooms, prepared, frame, first.Neighbor, first.NeighborSocket, top, baseNormal, baseDistance, scratch);
        stack.Add(new Frame { Room = first.Neighbor, Entry = first.NeighborSocket });

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
                scratch.MarkAll();
                stack.Clear();
                return;
            }

            if ((states & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            System.Threading.Interlocked.Increment(ref DiagTries);
            (Vec3 normal, float distance) = DoorPlane(frame, next.Opening, rooms[next.Neighbor].Transform);
            if (normal == baseNormal && distance == baseDistance)
            {
                continue; // a line crosses the first doorway's plane once
            }

            if (normal == -baseNormal && distance == -baseDistance)
            {
                continue;
            }

            Level here = scratch.At(depth);
            Level there = scratch.At(depth + 1);
            Winding(ToFrame(frame, next.Opening), scratch.Door);

            // The next doorway, cut to what lies in front of the first.
            VisChopResult chopped = VisClip.ChopWinding(scratch.Door, baseNormal, baseDistance, scratch.Chop, out int passCount);
            if (chopped == VisChopResult.Empty)
            {
                continue;
            }

            ReadOnlySpan<Vec3> candidate = chopped == VisChopResult.Clipped ? scratch.Chop.AsSpan(0, passCount) : scratch.Door;

            // The first doorway, cut to what lies behind the next.
            chopped = VisClip.ChopWinding(here.SourceSpan, -normal, -distance, there.Source, out int sourceCount);
            if (chopped == VisChopResult.Empty)
            {
                continue;
            }

            bool sourceWhole = chopped == VisChopResult.Unchanged;
            if (sourceWhole)
            {
                here.SourceSpan.CopyTo(there.Source);
                sourceCount = here.SourceCount;
            }

            ReadOnlySpan<Vec3> newSource = there.Source.AsSpan(0, sourceCount);
            int newPassCount;
            if (here.PassCount == 0)
            {
                candidate.CopyTo(there.Pass);
                newPassCount = candidate.Length;
            }
            else if (sourceWhole && here.Separators())
            {
                // The frame's own source and pass: its stored planes.
                if (!VisClip.ClipToSeparatorPlanes(
                    here.ForwardNormals.AsSpan(0, here.Forward), here.ForwardDistances.AsSpan(0, here.Forward), candidate, false, scratch.Clip, out int firstCount)
                    || !VisClip.ClipToSeparatorPlanes(
                    here.BackwardNormals.AsSpan(0, here.Backward), here.BackwardDistances.AsSpan(0, here.Backward), scratch.Clip.AsSpan(0, firstCount), true, there.Pass, out newPassCount))
                {
                    continue;
                }
            }
            else
            {
                ReadOnlySpan<Vec3> prevPass = here.PassSpan;
                if (!VisClip.ClipToSeparators(newSource, prevPass, candidate, false, scratch.Clip, out int firstCount)
                    || !VisClip.ClipToSeparators(prevPass, newSource, scratch.Clip.AsSpan(0, firstCount), true, there.Pass, out newPassCount))
                {
                    continue;
                }
            }

            System.Threading.Interlocked.Increment(ref DiagStates);
            there.Reset(sourceCount, newPassCount);
            Mark(rooms, prepared, frame, next.Neighbor, next.NeighborSocket, there, baseNormal, baseDistance, scratch);
            visited[next.Neighbor] = true;
            stack.Add(new Frame { Room = next.Neighbor, Entry = next.NeighborSocket });
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
        Level level,
        Vec3 baseNormal,
        float baseDistance,
        FlowScratch scratch)
    {
        LevelDoorRoom room = rooms[roomIndex];
        Prepared p = prepared[roomIndex];
        ulong[] sees = p.Sees[entry];
        ulong[] marked = scratch.Marked;
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

                // No pass yet (the room beyond the first doorway), or planes
                // too many to hold: nothing more to cull by.
                if (level.Separators()
                    && (AnyBehind(box, level.ForwardNormals, level.ForwardDistances, level.Forward, flip: false)
                    || AnyBehind(box, level.BackwardNormals, level.BackwardDistances, level.Backward, flip: true)))
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

    /// <summary>ORs one row into another, a vector at a time: the PAS is a few million of these on a large level.</summary>
    private static void Or(ulong[] into, ulong[] from)
    {
        Span<Vector<ulong>> target = System.Runtime.InteropServices.MemoryMarshal.Cast<ulong, Vector<ulong>>(into.AsSpan());
        ReadOnlySpan<Vector<ulong>> source = System.Runtime.InteropServices.MemoryMarshal.Cast<ulong, Vector<ulong>>(from.AsSpan());
        for (int v = 0; v < target.Length; v++)
        {
            target[v] |= source[v];
        }

        for (int w = target.Length * Vector<ulong>.Count; w < into.Length; w++)
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
