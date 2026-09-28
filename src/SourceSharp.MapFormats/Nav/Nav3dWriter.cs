//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.MapFormats.Nav;

/// <summary>Writes a <see cref="Nav3dLevel"/> as a <c>.nav3d</c> file.</summary>
/// <remarks>
/// <para>
/// <b>One layout per level.</b> The envelope (magic, version, codec,
/// lengths), then the image, stored raw or compressed: the image is the
/// header, then the section directory,
/// then the sections in a fixed order (the level's, then each agent's), each
/// padded with zeros to a four-byte boundary. Strings are interned in the
/// order they are first met, walking agents, cells, doors and points in that
/// order. Nothing depends on a clock, a hash seed or a thread, so a level
/// writes the same bytes on every run and machine, which is what lets the
/// link's determinism facts compare files byte for byte.
/// </para>
/// <para>
/// The writer checks the level's own consistency (every array the length the
/// others imply, every index in range) before it writes a byte, so a bug in
/// whatever built the level surfaces here as an exception naming the field
/// rather than as a file a game misreads.
/// </para>
/// </remarks>
public static class Nav3dWriter
{
    /// <summary>The file's bytes, uncompressed.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The <c>.nav3d</c> bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> is null.</exception>
    /// <exception cref="ArgumentException">The level is inconsistent; the message names the field.</exception>
    public static byte[] Write(Nav3dLevel level) => Write(level, NavCompression.None);

    /// <summary>The file's bytes, with the image stored by a codec.</summary>
    /// <param name="level">The level.</param>
    /// <param name="compression">How the image is stored. <see cref="NavCompression.None"/> is the default <c>ssmap link</c> writes: the runtime load measured fastest raw (see <c>docs/nav3d-format.md</c>).</param>
    /// <returns>The <c>.nav3d</c> bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> is null.</exception>
    /// <exception cref="ArgumentException">The level is inconsistent; the message names the field.</exception>
    public static byte[] Write(Nav3dLevel level, NavCompression compression)
    {
        ArgumentNullException.ThrowIfNull(level);
        Check(level);

        Strings strings = new();
        foreach (Nav3dAgent agent in level.Agents)
        {
            strings.Add(agent.Name);
        }

        foreach (Nav3dCell cell in level.Cells)
        {
            strings.Add(cell.Room);
        }

        foreach (Nav3dDoor door in level.Doors)
        {
            strings.Add(door.Name);
        }

        foreach (Nav3dPoi poi in level.Pois)
        {
            strings.Add(poi.Type);
            strings.Add(poi.Tags);
            strings.Add(poi.Name);
        }

        List<(string Tag, uint Index, byte[] Bytes)> sections =
        [
            (Nav3dFormat.StringsTag, Nav3dFormat.LevelIndex, strings.Bytes()),
            (Nav3dFormat.AgentsTag, Nav3dFormat.LevelIndex, AgentRecords(level, strings)),
            (Nav3dFormat.CellsTag, Nav3dFormat.LevelIndex, CellRecords(level, strings)),
            (Nav3dFormat.DoorsTag, Nav3dFormat.LevelIndex, DoorRecords(level, strings)),
            (Nav3dFormat.PoisTag, Nav3dFormat.LevelIndex, PoiRecords(level, strings)),
        ];

        for (int a = 0; a < level.Agents.Count; a++)
        {
            Nav3dAgent agent = level.Agents[a];
            uint index = (uint)a;
            sections.Add((Nav3dFormat.RootsTag, index, Int32s(agent.Roots)));
            sections.Add((Nav3dFormat.NodesTag, index, UInt32s(agent.Nodes)));
            sections.Add((Nav3dFormat.LeavesTag, index, LeafRecords(agent.Leaves)));
            sections.Add((Nav3dFormat.AdjacencyStartTag, index, UInt32s(agent.AdjacencyStart)));
            sections.Add((Nav3dFormat.AdjacencyTag, index, UInt32s(agent.Adjacency)));
            sections.Add((Nav3dFormat.LinksTag, index, LinkRecords(agent.Links)));
            sections.Add((Nav3dFormat.ComponentsTag, index, ComponentRecords(agent.Components)));
            sections.Add((Nav3dFormat.PoiLeavesTag, index, Int32s(agent.PoiLeaves)));
        }

        long offset = Nav3dFormat.HeaderBytes + ((long)sections.Count * Nav3dFormat.DirectoryEntryBytes);
        long[] offsets = new long[sections.Count];
        for (int i = 0; i < sections.Count; i++)
        {
            offsets[i] = offset;
            offset = Align4(offset + sections[i].Bytes.Length);
        }

        if (offset > int.MaxValue - Nav3dFormat.EnvelopeBytes)
        {
            throw new ArgumentException($"the level's navigation is {offset} bytes; a .nav3d image holds at most 2 GiB.", nameof(level));
        }

        byte[] image = new byte[offset];
        Span<byte> h = image;
        BinaryPrimitives.WriteInt32LittleEndian(h, Nav3dFormat.HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], sections.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], level.Agents.Count);
        BinaryPrimitives.WriteSingleLittleEndian(h[12..], level.CellSize);
        BinaryPrimitives.WriteSingleLittleEndian(h[16..], level.VoxelSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], level.CellVoxels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], level.OctreeDepth);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], level.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(h[32..], level.Rows);
        BinaryPrimitives.WriteSingleLittleEndian(h[36..], level.Origin.X);
        BinaryPrimitives.WriteSingleLittleEndian(h[40..], level.Origin.Y);
        BinaryPrimitives.WriteSingleLittleEndian(h[44..], level.Origin.Z);
        BinaryPrimitives.WriteSingleLittleEndian(h[48..], level.FloorNormalZ);
        BinaryPrimitives.WriteInt32LittleEndian(h[52..], level.Pois.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[56..], level.Doors.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[60..], level.SpawnPoi);
        BinaryPrimitives.WriteInt32LittleEndian(h[64..], level.UpArrivalPoi);
        BinaryPrimitives.WriteInt32LittleEndian(h[68..], level.DownArrivalPoi);
        _ = level.LevelId.TryWriteBytes(h.Slice(72, 16), bigEndian: true, out _);
        _ = level.PackId.TryWriteBytes(h.Slice(88, 16), bigEndian: true, out _);

        for (int i = 0; i < sections.Count; i++)
        {
            Span<byte> entry = h.Slice(Nav3dFormat.HeaderBytes + (i * Nav3dFormat.DirectoryEntryBytes), Nav3dFormat.DirectoryEntryBytes);
            Encoding.ASCII.GetBytes(sections[i].Tag, entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], sections[i].Index);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)sections[i].Bytes.Length);
            sections[i].Bytes.CopyTo(image, offsets[i]);
        }

        byte[] stored = compression.Compress(image);
        byte[] file = new byte[Nav3dFormat.EnvelopeBytes + stored.Length];
        Span<byte> e = file;
        Nav3dFormat.Magic.CopyTo(e);
        BinaryPrimitives.WriteInt32LittleEndian(e[8..], Nav3dFormat.Version);
        e[12] = (byte)compression.Codec;
        BinaryPrimitives.WriteInt32LittleEndian(e[16..], image.Length);
        BinaryPrimitives.WriteInt32LittleEndian(e[20..], stored.Length);
        stored.CopyTo(file, Nav3dFormat.EnvelopeBytes);
        return file;
    }

    private static long Align4(long value) => (value + 3) & ~3L;

    private static void Check(Nav3dLevel level)
    {
        if (!float.IsFinite(level.CellSize) || level.CellSize <= 0 || !float.IsFinite(level.VoxelSize) || level.VoxelSize <= 0)
        {
            throw new ArgumentException("the cell and voxel sizes are positive finite numbers.", nameof(level));
        }

        if (level.CellVoxels < 1 || level.Columns < 1 || level.Rows < 1)
        {
            throw new ArgumentException("the level has at least one cell of at least one voxel.", nameof(level));
        }

        int cells = checked(level.Columns * level.Rows);
        if (level.Cells.Count != cells)
        {
            throw new ArgumentException($"the level has {cells} cells but {level.Cells.Count} cell records.", nameof(level));
        }

        if (level.Agents.Count > Nav3dFormat.MaxAgents)
        {
            throw new ArgumentException($"the level has {level.Agents.Count} agents; a file holds at most {Nav3dFormat.MaxAgents}.", nameof(level));
        }

        CheckPoi(level.SpawnPoi, level.Pois.Count, nameof(Nav3dLevel.SpawnPoi));
        CheckPoi(level.UpArrivalPoi, level.Pois.Count, nameof(Nav3dLevel.UpArrivalPoi));
        CheckPoi(level.DownArrivalPoi, level.Pois.Count, nameof(Nav3dLevel.DownArrivalPoi));
        foreach (Nav3dDoor door in level.Doors)
        {
            if (door.Cell >= cells || door.Direction > 3 || door.Other < -1 || door.Other >= level.Doors.Count)
            {
                throw new ArgumentException($"door \"{door.Name}\" names a cell, direction or facing door out of range.", nameof(level));
            }
        }

        foreach (Nav3dPoi poi in level.Pois)
        {
            if (poi.Cell >= cells || poi.Door < -1 || poi.Door >= level.Doors.Count)
            {
                throw new ArgumentException($"point of interest \"{poi.Type}\" names a cell or door out of range.", nameof(level));
            }
        }

        foreach (Nav3dAgent agent in level.Agents)
        {
            string who = $"agent \"{agent.Name}\"";
            if (agent.Roots.Length != cells)
            {
                throw new ArgumentException($"{who} has {agent.Roots.Length} roots for {cells} cells.", nameof(level));
            }

            if (agent.AdjacencyStart.Length != agent.Leaves.Length + 1
                || agent.AdjacencyStart[0] != 0
                || agent.AdjacencyStart[^1] != agent.Adjacency.Length)
            {
                throw new ArgumentException($"{who}'s adjacency starts do not frame its {agent.Adjacency.Length} entries.", nameof(level));
            }

            if (agent.PoiLeaves.Length != level.Pois.Count)
            {
                throw new ArgumentException($"{who} has {agent.PoiLeaves.Length} point leaves for {level.Pois.Count} points.", nameof(level));
            }

            foreach (int root in agent.Roots)
            {
                if (root < -1 || root >= agent.Nodes.Length)
                {
                    throw new ArgumentException($"{who} has a root {root} beyond its {agent.Nodes.Length} nodes.", nameof(level));
                }
            }

            foreach (Nav3dLeaf leaf in agent.Leaves)
            {
                if (leaf.Component >= agent.Components.Length || leaf.Cell >= cells)
                {
                    throw new ArgumentException($"{who} has a leaf naming a component or cell out of range.", nameof(level));
                }
            }
        }
    }

    private static void CheckPoi(int index, int count, string field)
    {
        if (index < -1 || index >= count)
        {
            throw new ArgumentException($"{field} is {index}; it names one of the {count} points or is -1.", field);
        }
    }

    private static byte[] AgentRecords(Nav3dLevel level, Strings strings)
    {
        byte[] bytes = new byte[level.Agents.Count * Nav3dFormat.AgentRecordBytes];
        for (int i = 0; i < level.Agents.Count; i++)
        {
            Nav3dAgent agent = level.Agents[i];
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.AgentRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, strings.Offset(agent.Name));
            BinaryPrimitives.WriteSingleLittleEndian(r[4..], agent.Mins.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[8..], agent.Mins.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[12..], agent.Mins.Z);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], agent.Maxs.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], agent.Maxs.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], agent.Maxs.Z);
            BinaryPrimitives.WriteInt32LittleEndian(r[28..], agent.ContentsMask);
        }

        return bytes;
    }

    private static byte[] CellRecords(Nav3dLevel level, Strings strings)
    {
        byte[] bytes = new byte[level.Cells.Count * Nav3dFormat.CellRecordBytes];
        for (int i = 0; i < level.Cells.Count; i++)
        {
            Nav3dCell cell = level.Cells[i];
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.CellRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, strings.Offset(cell.Room));
            r[4] = cell.Rotation;
            r[5] = (byte)cell.Role;
            r[6] = cell.Joined;
            r[7] = cell.Capped;
        }

        return bytes;
    }

    private static byte[] DoorRecords(Nav3dLevel level, Strings strings)
    {
        byte[] bytes = new byte[level.Doors.Count * Nav3dFormat.DoorRecordBytes];
        for (int i = 0; i < level.Doors.Count; i++)
        {
            Nav3dDoor door = level.Doors[i];
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.DoorRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, door.Cell);
            r[4] = door.Direction;
            r[5] = door.Joined ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], strings.Offset(door.Name));
            BinaryPrimitives.WriteInt32LittleEndian(r[12..], door.Other);
        }

        return bytes;
    }

    private static byte[] PoiRecords(Nav3dLevel level, Strings strings)
    {
        byte[] bytes = new byte[level.Pois.Count * Nav3dFormat.PoiRecordBytes];
        for (int i = 0; i < level.Pois.Count; i++)
        {
            Nav3dPoi poi = level.Pois[i];
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.PoiRecordBytes);
            BinaryPrimitives.WriteSingleLittleEndian(r, poi.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[4..], poi.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[8..], poi.Position.Z);
            BinaryPrimitives.WriteSingleLittleEndian(r[12..], poi.Yaw);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], poi.Radius);
            BinaryPrimitives.WriteUInt32LittleEndian(r[20..], strings.Offset(poi.Type));
            BinaryPrimitives.WriteUInt32LittleEndian(r[24..], strings.Offset(poi.Tags));
            BinaryPrimitives.WriteUInt32LittleEndian(r[28..], strings.Offset(poi.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(r[32..], poi.Cell);
            BinaryPrimitives.WriteUInt32LittleEndian(r[36..], poi.AgentMask);
            BinaryPrimitives.WriteInt32LittleEndian(r[40..], poi.Door);
            BinaryPrimitives.WriteUInt16LittleEndian(r[44..], (ushort)poi.Flags);
            r[46] = (byte)poi.Role;
        }

        return bytes;
    }

    private static byte[] LeafRecords(Nav3dLeaf[] leaves)
    {
        byte[] bytes = new byte[leaves.Length * Nav3dFormat.LeafRecordBytes];
        for (int i = 0; i < leaves.Length; i++)
        {
            Nav3dLeaf leaf = leaves[i];
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.LeafRecordBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(r, leaf.X);
            BinaryPrimitives.WriteUInt16LittleEndian(r[2..], leaf.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(r[4..], leaf.Z);
            r[6] = leaf.SizeLog2;
            r[7] = (byte)leaf.Flags;
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], leaf.Component);
            BinaryPrimitives.WriteUInt32LittleEndian(r[12..], leaf.Cell);
        }

        return bytes;
    }

    private static byte[] LinkRecords(Nav3dDoorLink[] links)
    {
        byte[] bytes = new byte[links.Length * Nav3dFormat.LinkRecordBytes];
        for (int i = 0; i < links.Length; i++)
        {
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.LinkRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, links[i].LeafA);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], links[i].LeafB);
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], links[i].Door);
        }

        return bytes;
    }

    private static byte[] ComponentRecords(Nav3dComponent[] components)
    {
        byte[] bytes = new byte[components.Length * Nav3dFormat.ComponentRecordBytes];
        for (int i = 0; i < components.Length; i++)
        {
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.ComponentRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, components[i].Leaves);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], components[i].Voxels);
        }

        return bytes;
    }

    private static byte[] Int32s(int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    private static byte[] UInt32s(uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    /// <summary>The string table: each distinct string once, NUL-terminated, in the order first met; offset 0 is "".</summary>
    private sealed class Strings
    {
        private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal) { [string.Empty] = 0 };
        private readonly List<byte> _bytes = [0];

        public void Add(string? value)
        {
            if (value is null || _offsets.ContainsKey(value))
            {
                return;
            }

            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            if (Array.IndexOf(utf8, (byte)0) >= 0)
            {
                throw new ArgumentException($"the string \"{value}\" holds a NUL, which the string table cannot.", nameof(value));
            }

            _offsets[value] = (uint)_bytes.Count;
            _bytes.AddRange(utf8);
            _bytes.Add(0);
        }

        public uint Offset(string? value) => value is null ? Nav3dFormat.NoString : _offsets[value];

        public byte[] Bytes() => [.. _bytes];
    }
}
