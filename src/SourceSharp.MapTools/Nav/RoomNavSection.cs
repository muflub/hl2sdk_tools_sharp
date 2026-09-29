//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// A room's navigation as a room pack section: the <c>NVR0</c> section
/// (turn 0, always written) and optionally <c>NVR1</c> to <c>NVR3</c> (the
/// same room at one to three quarter turns).
/// </summary>
/// <remarks>
/// <para>
/// <b>Framing.</b> Big-endian, as every integer of a room pack is, and
/// framed as the pack's link sections are, so every per-room section of a
/// pack reads the same way: a codec byte (<see cref="NavCodec"/>: 0 none,
/// 1 Deflate, 2 Brotli), the payload's decoded length as an <c>int64</c>,
/// and the payload, stored by the codec. The decoded payload starts with an
/// <c>int32</c> revision (<see cref="Revision"/>) and a <c>uint8</c> turn,
/// 0 to 3, which must match the tag. As with the link sections, a section of
/// a revision this build does not know reads as absent (the link goes on
/// without navigation and says so); a codec it does not know, or a payload
/// that does not decode to its recorded length, is refused.
/// </para>
/// <para>
/// <b>Revision 2</b> holds the shared clearance grid (revision 1 held an
/// octree per agent, so a revision 1 section reads as absent). After the
/// revision and turn: <c>float32</c> cell size and voxel size, <c>int32</c>
/// voxels per cell edge, <c>float32</c> floor normal z, step height, jump
/// height, jump distance, water cost and ladder cost, <c>uint8</c> role; the
/// presets (<c>uint8</c> count, each a string name, <c>float32</c> width and
/// height, <c>int32</c> contents mask); the sockets (<c>uint8</c> count, each
/// <c>uint8</c> turn-0 facing, a string name and the door point's
/// <c>float32</c> x, y, z); the points of interest (<c>int32</c> count, each
/// <c>float32</c> x, y, z, yaw, <c>uint8</c> has-facing, <c>float32</c>
/// radius, string type, string tags, <c>uint8</c> has-name then the name,
/// <c>uint32</c> preset mask, string entity id); the records (<c>int32</c>
/// count, each <c>uint16</c> static, dynamic and brush counts, then the
/// static corners as <c>float32</c> width and top, the dynamic ones as
/// <c>uint32</c> obstacle and two <c>float32</c>, the brushes as
/// <c>uint32</c>); the columns (<c>int32</c> run count, then per column,
/// x fastest, a <c>uint16</c> count, then the runs, each <c>uint8</c> low
/// voxel and height and a key); per socket its capped keys (<c>int32</c>
/// count, each <c>int32</c> voxel and a key); the obstacles (<c>int32</c>
/// count, each string classname, <c>uint8</c> has-name then the name,
/// <c>int32</c> hammer id, <c>uint8</c> kind, six <c>float32</c> bounds); and
/// the overhanging brushes (<c>int32</c> count, each <c>int32</c> plane count
/// and four <c>float32</c> per plane). A key is <c>int32</c> player and NPC
/// record, <c>uint16</c> flags and cost, <c>float32</c> player and NPC floor.
/// A string is a <c>uint16</c> byte count and UTF-8.
/// </para>
/// <para>
/// <b>Why one section per turn.</b> The link reads only the section for the
/// turn a placement uses, when the pack has it, and turns the <c>NVR0</c>
/// copy itself otherwise. Whether the pack carries the turned copies is the
/// writer's choice (<see cref="RoomNavPackOptions.StoreAllTurns"/>): the link
/// gives the same file either way.
/// </para>
/// </remarks>
public static class RoomNavSection
{
    /// <summary>The payload revision this build writes and reads; a section of another reads as absent.</summary>
    public const int Revision = 2;

    /// <summary>The codec byte and the <c>int64</c> decoded length every section starts with.</summary>
    private const int HeaderBytes = 1 + 8;

    /// <summary>The largest payload a section may claim: a lying length fails before it allocates.</summary>
    private const int MaxPayloadBytes = 1 << 30;

    /// <summary>The tag of the section holding a turn: <c>NVR0</c> to <c>NVR3</c>.</summary>
    /// <param name="turn">The quarter turns, 0 to 3.</param>
    /// <returns>The tag.</returns>
    public static string Tag(int turn) => turn switch
    {
        0 => "NVR0",
        1 => "NVR1",
        2 => "NVR2",
        3 => "NVR3",
        _ => throw new ArgumentOutOfRangeException(nameof(turn), turn, "a turn is 0 to 3."),
    };

    /// <summary>The section's bytes.</summary>
    /// <param name="nav">The room's navigation at some turn.</param>
    /// <param name="compression">How the payload is stored.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Write(RoomNav nav, NavCompression compression)
    {
        ArgumentNullException.ThrowIfNull(nav);
        Writer w = new();
        w.I32(Revision);
        w.U8((byte)nav.Turn);
        w.F32(nav.CellSize);
        w.F32(nav.VoxelSize);
        w.I32(nav.CellVoxels);
        w.F32(nav.FloorNormalZ);
        w.F32(nav.StepHeight);
        w.F32(nav.JumpHeight);
        w.F32(nav.JumpDistance);
        w.F32(nav.WaterCost);
        w.F32(nav.LadderCost);
        w.U8((byte)nav.Role);
        w.U8((byte)nav.Agents.Count);
        foreach (NavAgentSpec agent in nav.Agents)
        {
            w.Str(agent.Name);
            w.F32(agent.Width);
            w.F32(agent.Height);
            w.I32(agent.ContentsMask);
        }

        w.U8((byte)nav.Sockets.Count);
        for (int s = 0; s < nav.Sockets.Count; s++)
        {
            w.U8((byte)nav.Sockets[s].Facing);
            w.Str(nav.Sockets[s].Name);
            w.Vec(nav.SocketData[s].DoorPoint);
        }

        w.I32(nav.Pois.Count);
        foreach (RoomNavPoi poi in nav.Pois)
        {
            w.Vec(poi.Position);
            w.F32(poi.Yaw);
            w.U8(poi.HasFacing ? (byte)1 : (byte)0);
            w.F32(poi.Radius);
            w.Str(poi.Type);
            w.Str(poi.Tags);
            w.OptStr(poi.Name);
            w.U32(poi.AgentMask);
            w.Str(poi.EntityId);
        }

        w.I32(nav.Records.Count);
        foreach (byte[] bytes in nav.Records)
        {
            NavRecord record = NavRecord.Decode(bytes);
            w.U16((ushort)record.Corners.Count);
            w.U16((ushort)record.Dynamics.Count);
            w.U16((ushort)record.Brushes.Count);
            foreach (Nav3dCorner corner in record.Corners)
            {
                w.F32(corner.Width);
                w.F32(corner.Top);
            }

            foreach (Nav3dDynamicCorner dynamic in record.Dynamics)
            {
                w.U32((uint)dynamic.Obstacle);
                w.F32(dynamic.Corner.Width);
                w.F32(dynamic.Corner.Top);
            }

            foreach (int brush in record.Brushes)
            {
                w.U32((uint)brush);
            }
        }

        NavColumns columns = nav.Columns;
        w.I32(columns.Runs.Length);
        for (int c = 0; c < columns.SizeX * columns.SizeY; c++)
        {
            w.U16((ushort)(columns.ColumnStarts[c + 1] - columns.ColumnStarts[c]));
        }

        foreach (NavRun run in columns.Runs)
        {
            w.U8(run.ZLo);
            w.U8(run.Height);
            w.Key(run.Key);
        }

        foreach (RoomNavSocket socket in nav.SocketData)
        {
            w.I32(socket.Capped.Count);
            foreach (NavCapChange change in socket.Capped)
            {
                w.I32(change.Voxel);
                w.Key(change.Key);
            }
        }

        w.I32(nav.Obstacles.Count);
        foreach (RoomNavObstacle obstacle in nav.Obstacles)
        {
            w.Str(obstacle.ClassName);
            w.OptStr(obstacle.TargetName);
            w.I32(obstacle.HammerId);
            w.U8((byte)obstacle.Kind);
            w.Vec(obstacle.Bounds.Mins);
            w.Vec(obstacle.Bounds.Maxs);
        }

        w.I32(nav.Brushes.Count);
        foreach (float[] planes in nav.Brushes)
        {
            w.I32(planes.Length / 4);
            foreach (float value in planes)
            {
                w.F32(value);
            }
        }

        byte[] raw = w.ToArray();
        byte[] stored = compression.Compress(raw);
        byte[] section = new byte[HeaderBytes + stored.Length];
        section[0] = (byte)compression.Codec;
        BinaryPrimitives.WriteInt64BigEndian(section.AsSpan(1), raw.Length);
        stored.CopyTo(section, HeaderBytes);
        return section;
    }

    /// <summary>Reads a section.</summary>
    /// <param name="section">The section's bytes.</param>
    /// <returns>The room's navigation at the section's turn, or null for a section of a revision this build does not read.</returns>
    /// <exception cref="InvalidDataException">An unknown codec, or a payload cut short, of the wrong length, or inconsistent; the message says which.</exception>
    public static RoomNav? Read(ReadOnlySpan<byte> section)
    {
        if (section.Length < HeaderBytes)
        {
            throw new InvalidDataException($"a room nav section is at least {HeaderBytes} bytes, not {section.Length}.");
        }

        long length = BinaryPrimitives.ReadInt64BigEndian(section[1..]);
        if (length is < 0 or > MaxPayloadBytes)
        {
            throw new InvalidDataException($"a room nav section claims a payload of {length} bytes.");
        }

        byte[] raw = NavCompression.Decompress((NavCodec)section[0], section[HeaderBytes..], (int)length);
        Reader r = new(raw);
        if (r.I32() != Revision)
        {
            return null;
        }

        int turn = r.U8();
        if (turn > 3)
        {
            throw new InvalidDataException($"a room nav section at turn {turn}; a turn is 0 to 3.");
        }

        float cell = r.F32();
        float voxel = r.F32();
        int n = r.I32();
        float floor = r.F32();
        float step = r.F32();
        float jumpHeight = r.F32();
        float jumpDistance = r.F32();
        float waterCost = r.F32();
        float ladderCost = r.F32();
        if (n is < 1 or > NavSettings.MaxCellVoxels || !(cell > 0) || !(voxel > 0))
        {
            throw new InvalidDataException($"a room nav section of {n} voxels a side; a cell has 1 to {NavSettings.MaxCellVoxels}.");
        }

        RoomRole role = (RoomRole)r.U8();
        if (role is not (RoomRole.None or RoomRole.Up or RoomRole.Down))
        {
            throw new InvalidDataException($"a room nav section with role {(int)role}.");
        }

        List<NavAgentSpec> agents = [];
        int agentCount = r.U8();
        if (agentCount > Nav3dFormat.MaxPresets)
        {
            throw new InvalidDataException($"a room nav section with {agentCount} presets.");
        }

        for (int a = 0; a < agentCount; a++)
        {
            agents.Add(new NavAgentSpec(r.Str(), r.F32(), r.F32(), r.I32()));
        }

        List<RoomSocket> sockets = [];
        List<Vec3> doorPoints = [];
        int socketCount = r.U8();
        for (int s = 0; s < socketCount; s++)
        {
            byte facing = r.U8();
            if (facing > 3)
            {
                throw new InvalidDataException($"a room nav socket facing {facing}.");
            }

            sockets.Add(new RoomSocket((RoomFacing)facing, r.Str()));
            doorPoints.Add(r.Vec());
        }

        List<RoomNavPoi> pois = [];
        int poiCount = r.Count(22);
        for (int p = 0; p < poiCount; p++)
        {
            Vec3 position = r.Vec();
            float yaw = r.F32();
            bool hasFacing = r.U8() != 0;
            float radius = r.F32();
            string type = r.Str();
            string tags = r.Str();
            string? name = r.OptStr();
            uint mask = r.U32();
            pois.Add(new RoomNavPoi(position, yaw, hasFacing, radius, type, tags, name, mask, r.Str()));
        }

        int recordCount = r.Count(6);
        List<byte[]> records = new(recordCount);
        for (int i = 0; i < recordCount; i++)
        {
            int statics = r.U16();
            int dynamics = r.U16();
            int brushCount = r.U16();
            Nav3dCorner[] corners = new Nav3dCorner[statics];
            for (int k = 0; k < statics; k++)
            {
                corners[k] = new Nav3dCorner(r.F32(), r.F32());
            }

            Nav3dDynamicCorner[] dynamic = new Nav3dDynamicCorner[dynamics];
            for (int k = 0; k < dynamics; k++)
            {
                dynamic[k] = new Nav3dDynamicCorner((int)r.U32(), new Nav3dCorner(r.F32(), r.F32()));
            }

            int[] brushes = new int[brushCount];
            for (int k = 0; k < brushCount; k++)
            {
                brushes[k] = (int)r.U32();
            }

            try
            {
                records.Add(Nav3dClearance.Encode(corners, dynamic, brushes));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"a room nav section's record {i}: {exception.Message}");
            }
        }

        if (records.Count == 0 || !records[0].AsSpan().SequenceEqual(Nav3dClearance.BlockedRecord))
        {
            throw new InvalidDataException("a room nav section's first record is not the one that blocks everything.");
        }

        int runCount = r.Count(22);
        int[] starts = new int[(n * n) + 1];
        for (int c = 0; c < n * n; c++)
        {
            starts[c + 1] = starts[c] + r.U16();
        }

        if (starts[^1] != runCount)
        {
            throw new InvalidDataException($"a room nav section's columns hold {starts[^1]} runs, not {runCount}.");
        }

        NavRun[] runs = new NavRun[runCount];
        for (int c = 0; c < n * n; c++)
        {
            int top = -1;
            for (int i = starts[c]; i < starts[c + 1]; i++)
            {
                byte zLo = r.U8();
                byte height = r.U8();
                if (height < 1 || zLo <= top || zLo + height > n)
                {
                    throw new InvalidDataException($"a room nav run from voxel {zLo} for {height} overlaps its column's last or leaves the cell.");
                }

                top = zLo + height - 1;
                runs[i] = new NavRun(zLo, height, r.Key(records.Count));
            }
        }

        List<List<NavCapChange>> caps = [];
        for (int s = 0; s < socketCount; s++)
        {
            int count = r.Count(24);
            List<NavCapChange> capped = new(count);
            for (int i = 0; i < count; i++)
            {
                int voxelIndex = r.I32();
                if (voxelIndex < 0 || voxelIndex >= n * n * n)
                {
                    throw new InvalidDataException($"a room nav cap change at voxel {voxelIndex}, outside a cell of {n}.");
                }

                capped.Add(new NavCapChange(voxelIndex, r.Key(records.Count)));
            }

            caps.Add(capped);
        }

        List<RoomNavObstacle> obstacles = [];
        int obstacleCount = r.Count(32);
        for (int o = 0; o < obstacleCount; o++)
        {
            string className = r.Str();
            string? targetName = r.OptStr();
            int hammerId = r.I32();
            Nav3dObstacleKind kind = (Nav3dObstacleKind)r.U8();
            obstacles.Add(new RoomNavObstacle(className, targetName, hammerId, kind, new Box(r.Vec(), r.Vec())));
        }

        List<float[]> brushPlanes = [];
        int brushTotal = r.Count(4);
        for (int b = 0; b < brushTotal; b++)
        {
            int planes = r.Count(16);
            float[] floats = new float[planes * 4];
            for (int i = 0; i < floats.Length; i++)
            {
                floats[i] = r.F32();
            }

            if (NavBrush.FromPlaneFloats(floats, 1) is null)
            {
                throw new InvalidDataException($"a room nav section's brush {b} bounds no volume.");
            }

            brushPlanes.Add(floats);
        }

        foreach (byte[] record in records)
        {
            if (Nav3dClearance.Problem(record, obstacles.Count, brushPlanes.Count) is { } problem)
            {
                throw new InvalidDataException($"a room nav section's record {problem}.");
            }
        }

        if (!r.AtEnd)
        {
            throw new InvalidDataException("a room nav section has bytes after its last brush.");
        }

        return new RoomNav
        {
            CellSize = cell,
            VoxelSize = voxel,
            CellVoxels = n,
            FloorNormalZ = floor,
            StepHeight = step,
            JumpHeight = jumpHeight,
            JumpDistance = jumpDistance,
            WaterCost = waterCost,
            LadderCost = ladderCost,
            Turn = turn,
            Role = role,
            Agents = agents,
            Sockets = sockets,
            Pois = pois,
            Records = records,
            Columns = new NavColumns(n, n, starts, runs),
            SocketData = [.. caps.Select((c, s) => new RoomNavSocket(c, doorPoints[s]))],
            Obstacles = obstacles,
            Brushes = brushPlanes,
        };
    }

    private sealed class Writer
    {
        private readonly MemoryStream _bytes = new();

        public void U8(byte value) => _bytes.WriteByte(value);

        public void U16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
            _bytes.Write(b);
        }

        public void I32(int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, value);
            _bytes.Write(b);
        }

        public void U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            _bytes.Write(b);
        }

        public void F32(float value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteSingleBigEndian(b, value);
            _bytes.Write(b);
        }

        public void Vec(Vec3 value)
        {
            F32(value.X);
            F32(value.Y);
            F32(value.Z);
        }

        public void Str(string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            if (utf8.Length > ushort.MaxValue)
            {
                throw new ArgumentException($"a string of {utf8.Length} bytes; a room nav string is at most {ushort.MaxValue}.", nameof(value));
            }

            U16((ushort)utf8.Length);
            _bytes.Write(utf8);
        }

        public void OptStr(string? value)
        {
            U8(value is null ? (byte)0 : (byte)1);
            if (value is not null)
            {
                Str(value);
            }
        }

        public void Key(NavVoxelKey key)
        {
            I32(key.PlayerRecord);
            I32(key.NpcRecord);
            U16((ushort)key.Flags);
            U16(key.Cost);
            F32(key.PlayerFloorZ);
            F32(key.NpcFloorZ);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _at;

        public readonly bool AtEnd => _at == _bytes.Length;

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

        public int I32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        public float F32() => BinaryPrimitives.ReadSingleBigEndian(Take(4));

        public Vec3 Vec() => new(F32(), F32(), F32());

        public string Str() => Encoding.UTF8.GetString(Take(U16()));

        public string? OptStr() => U8() != 0 ? Str() : null;

        public NavVoxelKey Key(int records)
        {
            int player = I32();
            int npc = I32();
            if (player < 0 || player >= records || npc < 0 || npc >= records)
            {
                throw new InvalidDataException($"a room nav key names records {player} and {npc} of {records}.");
            }

            return new NavVoxelKey(player, npc, (Nav3dLeafFlags)U16(), U16(), F32(), F32());
        }

        /// <summary>A count of records of at least <paramref name="each"/> bytes, refused when the bytes left cannot hold them.</summary>
        public int Count(int each)
        {
            int count = I32();
            if (count < 0 || (long)count * each > _bytes.Length - _at)
            {
                throw new InvalidDataException($"a room nav section claims {count} records in {_bytes.Length - _at} bytes.");
            }

            return count;
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > _bytes.Length - _at)
            {
                throw new InvalidDataException("a room nav section is cut short.");
            }

            ReadOnlySpan<byte> taken = _bytes.Slice(_at, count);
            _at += count;
            return taken;
        }
    }
}
