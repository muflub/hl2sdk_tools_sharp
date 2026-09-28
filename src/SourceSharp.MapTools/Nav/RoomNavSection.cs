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
/// <b>Layout, revision 1.</b> Big-endian, as every integer of a room pack is,
/// and framed as the pack's link sections are (<c>RoomLinkSections</c>), so
/// every per-room section of a pack reads the same way: a codec byte
/// (<see cref="NavCodec"/>: 0 none, 1 Deflate, 2 Brotli, the same values
/// as the link sections' codec), the payload's decoded length as an
/// <c>int64</c>, and the payload, stored by the codec. The decoded payload
/// starts with an <c>int32</c> revision (<see cref="Revision"/>) and a
/// <c>uint8</c> turn, 0 to 3, which must match the tag. As with the link
/// sections, a section of a revision this build does not know reads as
/// absent (the link goes on without navigation and says so), a codec it does
/// not know, or a payload that does not decode to its recorded length, is
/// refused. The codec byte and revision replace the earlier nav-only
/// version and codec header, so there is one convention in the pack.
/// </para>
/// <para>
/// The payload after the revision and turn: <c>float32</c> cell size, voxel size, <c>int32</c> voxels
/// per cell edge, <c>float32</c> floor normal z, <c>uint8</c> role; the
/// agents (<c>uint8</c> count, each a string name, <c>float32</c> width and
/// height, <c>int32</c> contents mask); the sockets (<c>uint8</c> count, each
/// <c>uint8</c> turn-0 facing and a string name); the points of interest
/// (<c>int32</c> count, each <c>float32</c> x y z yaw, <c>uint8</c> has-facing,
/// <c>float32</c> radius, string type, string tags, <c>uint8</c> has-name
/// then the name, <c>uint32</c> agent mask); then per agent: <c>int32</c>
/// node count and the node words, <c>int32</c> leaf count and the leaves (x,
/// y, z, size log2, flags: five bytes each), and per socket <c>int32</c>
/// portal voxel count and the voxels (x, y, z), <c>int32</c> cap change
/// count and the changes (x, y, z, 1 when it blocks else 0, the flags it
/// adds). A string is a <c>uint16</c> byte count and UTF-8.
/// </para>
/// <para>
/// <b>Why one section per turn.</b> The link reads only the section for the
/// turn a placement uses, when the pack has it, and turns the <c>NVR0</c>
/// copy itself otherwise. Whether the pack carries the turned copies is the
/// writer's choice (<see cref="RoomNavPackOptions.StoreAllTurns"/>, on by
/// default because it links faster): the link gives the same file either
/// way, and the measured trade is in <c>docs/nav3d-format.md</c>.
/// </para>
/// </remarks>
public static class RoomNavSection
{
    /// <summary>The payload revision this build writes and reads; a section of another reads as absent.</summary>
    public const int Revision = 1;

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
        foreach (RoomSocket socket in nav.Sockets)
        {
            w.U8((byte)socket.Facing);
            w.Str(socket.Name);
        }

        w.I32(nav.Pois.Count);
        foreach (RoomNavPoi poi in nav.Pois)
        {
            w.F32(poi.Position.X);
            w.F32(poi.Position.Y);
            w.F32(poi.Position.Z);
            w.F32(poi.Yaw);
            w.U8(poi.HasFacing ? (byte)1 : (byte)0);
            w.F32(poi.Radius);
            w.Str(poi.Type);
            w.Str(poi.Tags);
            w.U8(poi.Name is null ? (byte)0 : (byte)1);
            if (poi.Name is not null)
            {
                w.Str(poi.Name);
            }

            w.U32(poi.AgentMask);
        }

        foreach (RoomNavAgent agent in nav.AgentData)
        {
            w.I32(agent.Nodes.Length);
            foreach (uint node in agent.Nodes)
            {
                w.U32(node);
            }

            w.I32(agent.Leaves.Length);
            foreach (RoomNavLeaf leaf in agent.Leaves)
            {
                w.U8(leaf.X);
                w.U8(leaf.Y);
                w.U8(leaf.Z);
                w.U8(leaf.SizeLog2);
                w.U8((byte)leaf.Flags);
            }

            foreach (RoomNavSocket socket in agent.Sockets)
            {
                w.I32(socket.Portal.Count);
                foreach (NavVoxel v in socket.Portal)
                {
                    w.Voxel(v);
                }

                w.I32(socket.Capped.Count);
                foreach (NavCapChange change in socket.Capped)
                {
                    w.Voxel(change.Voxel);
                    w.U8(change.Blocks ? (byte)1 : (byte)0);
                    w.U8((byte)change.AddFlags);
                }
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
        if (agentCount is < 1 or > Nav3dFormat.MaxAgents)
        {
            throw new InvalidDataException($"a room nav section with {agentCount} agents.");
        }

        for (int a = 0; a < agentCount; a++)
        {
            agents.Add(new NavAgentSpec(r.Str(), r.F32(), r.F32(), r.I32()));
        }

        List<RoomSocket> sockets = [];
        int socketCount = r.U8();
        for (int s = 0; s < socketCount; s++)
        {
            byte facing = r.U8();
            if (facing > 3)
            {
                throw new InvalidDataException($"a room nav socket facing {facing}.");
            }

            sockets.Add(new RoomSocket((RoomFacing)facing, r.Str()));
        }

        List<RoomNavPoi> pois = [];
        int poiCount = r.Count(22);
        for (int p = 0; p < poiCount; p++)
        {
            Vec3 position = new(r.F32(), r.F32(), r.F32());
            float yaw = r.F32();
            bool hasFacing = r.U8() != 0;
            float radius = r.F32();
            string type = r.Str();
            string tags = r.Str();
            string? name = r.U8() != 0 ? r.Str() : null;
            pois.Add(new RoomNavPoi(position, yaw, hasFacing, radius, type, tags, name, r.U32()));
        }

        List<RoomNavAgent> data = [];
        for (int a = 0; a < agentCount; a++)
        {
            uint[] nodes = new uint[r.Count(4)];
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i] = r.U32();
            }

            RoomNavLeaf[] leaves = new RoomNavLeaf[r.Count(5)];
            for (int i = 0; i < leaves.Length; i++)
            {
                leaves[i] = new RoomNavLeaf(r.U8(), r.U8(), r.U8(), r.U8(), (Nav3dLeafFlags)r.U8());
            }

            List<RoomNavSocket> socketData = [];
            for (int s = 0; s < socketCount; s++)
            {
                NavVoxel[] portal = new NavVoxel[r.Count(3)];
                for (int i = 0; i < portal.Length; i++)
                {
                    portal[i] = r.Voxel(n);
                }

                NavCapChange[] capped = new NavCapChange[r.Count(5)];
                for (int i = 0; i < capped.Length; i++)
                {
                    capped[i] = new NavCapChange(r.Voxel(n), r.U8() != 0, (Nav3dLeafFlags)r.U8());
                }

                socketData.Add(new RoomNavSocket(portal, capped));
            }

            // Checked here so the link can trust the tree's shape.
            _ = NavOctree.LeafMap(nodes, leaves.Length, n);
            data.Add(new RoomNavAgent(nodes, leaves, socketData));
        }

        if (!r.AtEnd)
        {
            throw new InvalidDataException("a room nav section has bytes after its last agent.");
        }

        return new RoomNav
        {
            CellSize = cell,
            VoxelSize = voxel,
            CellVoxels = n,
            FloorNormalZ = floor,
            Turn = turn,
            Role = role,
            Agents = agents,
            Sockets = sockets,
            Pois = pois,
            AgentData = data,
        };
    }

    private sealed class Writer
    {
        private readonly MemoryStream _bytes = new();

        public void U8(byte value) => _bytes.WriteByte(value);

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

        public void Str(string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            if (utf8.Length > ushort.MaxValue)
            {
                throw new ArgumentException($"a string of {utf8.Length} bytes; a room nav string is at most {ushort.MaxValue}.", nameof(value));
            }

            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)utf8.Length);
            _bytes.Write(b);
            _bytes.Write(utf8);
        }

        public void Voxel(NavVoxel v)
        {
            U8(v.X);
            U8(v.Y);
            U8(v.Z);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _at;

        public readonly bool AtEnd => _at == _bytes.Length;

        public byte U8() => Take(1)[0];

        public int I32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        public float F32() => BinaryPrimitives.ReadSingleBigEndian(Take(4));

        public string Str() => Encoding.UTF8.GetString(Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))));

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

        public NavVoxel Voxel(int n)
        {
            NavVoxel v = new(U8(), U8(), U8());
            if (v.X >= n || v.Y >= n || v.Z >= n)
            {
                throw new InvalidDataException($"a room nav voxel ({v.X}, {v.Y}, {v.Z}) outside a cell of {n}.");
            }

            return v;
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
