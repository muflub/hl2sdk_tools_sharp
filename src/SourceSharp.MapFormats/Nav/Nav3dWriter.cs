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
/// header, then the section directory, then the sections in a fixed order,
/// each padded with zeros to a four-byte boundary. Strings are interned in
/// the order they are first met, walking presets, cells, doors, points and
/// obstacles in that order. Nothing depends on a clock, a hash seed or a
/// thread, so a level writes the same bytes on every run and machine, which
/// is what lets the link's determinism facts compare files byte for byte.
/// </para>
/// <para>
/// <b>What is not written.</b> Version 2 writes only what a reader cannot
/// cheaply work out: the leaves, their clearance, floors and costs, the jump
/// links and the level's records. Which leaves neighbour which, the
/// connected components of each preset, each point's leaf, each obstacle's
/// leaves: the reader derives them once, at load (<see cref="Nav3dReader"/>),
/// from what is here, the same way every time.
/// </para>
/// <para>
/// The writer checks the level's own consistency (every array the length the
/// others imply, every index in range, every clearance record well formed)
/// before it writes a byte, so a bug in whatever built the level surfaces
/// here as an exception naming the field rather than as a file a game
/// misreads.
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
    /// <param name="compression">How the image is stored.</param>
    /// <returns>The <c>.nav3d</c> bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> is null.</exception>
    /// <exception cref="ArgumentException">The level is inconsistent; the message names the field.</exception>
    public static byte[] Write(Nav3dLevel level, NavCompression compression)
    {
        ArgumentNullException.ThrowIfNull(level);
        Check(level);

        Strings strings = new();
        foreach (Nav3dPreset preset in level.Presets)
        {
            strings.Add(preset.Name);
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

        foreach (Nav3dObstacle obstacle in level.Obstacles)
        {
            strings.Add(obstacle.Name);
            strings.Add(obstacle.ClassName);
        }

        uint[] brushStarts = new uint[level.Brushes.Count + 1];
        for (int b = 0; b < level.Brushes.Count; b++)
        {
            brushStarts[b + 1] = brushStarts[b] + (uint)(level.Brushes[b].Length / 4);
        }

        List<(string Tag, byte[] Bytes)> sections =
        [
            (Nav3dFormat.StringsTag, strings.Bytes()),
            (Nav3dFormat.PresetsTag, PresetRecords(level, strings)),
            (Nav3dFormat.CellsTag, CellRecords(level, strings)),
            (Nav3dFormat.DoorsTag, DoorRecords(level, strings)),
            (Nav3dFormat.PoisTag, PoiRecords(level, strings)),
            (Nav3dFormat.RootsTag, Int32s(level.Roots)),
            (Nav3dFormat.ColumnsTag, UInt32s(level.ColumnStarts)),
            (Nav3dFormat.LeavesTag, LeafRecords(level.Leaves)),
            (Nav3dFormat.ClearanceTag, level.Clearance),
            (Nav3dFormat.ObstaclesTag, ObstacleRecords(level, strings)),
            (Nav3dFormat.BrushIndexTag, UInt32s(brushStarts)),
            (Nav3dFormat.BrushPlanesTag, Floats(level.Brushes)),
            (Nav3dFormat.JumpsTag, JumpRecords(level.Jumps)),
        ];

        // Version 3's one addition, last so every section before it is where
        // version 2 puts it: each cell's own height, for a level whose cells
        // differ from the cube (the rooms design, 17.11).
        if (level.CellHeights is { } heights)
        {
            sections.Add((Nav3dFormat.CellHeightsTag, Int32s([.. heights])));
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
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], level.Presets.Count);
        BinaryPrimitives.WriteSingleLittleEndian(h[12..], level.CellSize);
        BinaryPrimitives.WriteSingleLittleEndian(h[16..], level.VoxelSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], level.CellVoxels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], level.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], level.Rows);
        BinaryPrimitives.WriteSingleLittleEndian(h[32..], level.Origin.X);
        BinaryPrimitives.WriteSingleLittleEndian(h[36..], level.Origin.Y);
        BinaryPrimitives.WriteSingleLittleEndian(h[40..], level.Origin.Z);
        BinaryPrimitives.WriteSingleLittleEndian(h[44..], level.FloorNormalZ);
        BinaryPrimitives.WriteSingleLittleEndian(h[48..], level.StepHeight);
        BinaryPrimitives.WriteSingleLittleEndian(h[52..], level.JumpHeight);
        BinaryPrimitives.WriteSingleLittleEndian(h[56..], level.JumpDistance);
        BinaryPrimitives.WriteInt32LittleEndian(h[60..], level.Pois.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[64..], level.Doors.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[68..], level.SpawnPoi);
        BinaryPrimitives.WriteInt32LittleEndian(h[72..], level.UpArrivalPoi);
        BinaryPrimitives.WriteInt32LittleEndian(h[76..], level.DownArrivalPoi);
        BinaryPrimitives.WriteInt32LittleEndian(h[80..], level.ColumnStarts.Length - 1);
        BinaryPrimitives.WriteInt32LittleEndian(h[84..], level.Leaves.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h[88..], level.Obstacles.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[92..], level.Brushes.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[96..], level.Jumps.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[100..], level.Clearance.Length);
        _ = level.LevelId.TryWriteBytes(h.Slice(104, 16), bigEndian: true, out _);
        _ = level.PackId.TryWriteBytes(h.Slice(120, 16), bigEndian: true, out _);

        for (int i = 0; i < sections.Count; i++)
        {
            Span<byte> entry = h.Slice(Nav3dFormat.HeaderBytes + (i * Nav3dFormat.DirectoryEntryBytes), Nav3dFormat.DirectoryEntryBytes);
            Encoding.ASCII.GetBytes(sections[i].Tag, entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], Nav3dFormat.LevelIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)sections[i].Bytes.Length);
            sections[i].Bytes.CopyTo(image, offsets[i]);
        }

        byte[] stored = compression.Compress(image);
        byte[] file = new byte[Nav3dFormat.EnvelopeBytes + stored.Length];
        Span<byte> e = file;
        Nav3dFormat.Magic.CopyTo(e);
        BinaryPrimitives.WriteInt32LittleEndian(e[8..], level.CellHeights is null ? Nav3dFormat.CubeVersion : Nav3dFormat.Version);
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

        if (level.CellVoxels is < 1 or > Nav3dFormat.MaxCellVoxels || level.Columns < 1 || level.Rows < 1)
        {
            throw new ArgumentException(
                $"the level has at least one cell of 1 to {Nav3dFormat.MaxCellVoxels} voxels a side.", nameof(level));
        }

        int cells = checked(level.Columns * level.Rows);
        if (level.Cells.Count != cells || level.Roots.Length != cells)
        {
            throw new ArgumentException($"the level has {cells} cells but {level.Cells.Count} cell records and {level.Roots.Length} roots.", nameof(level));
        }

        if (level.Presets.Count > Nav3dFormat.MaxPresets)
        {
            throw new ArgumentException($"the level has {level.Presets.Count} presets; a file holds at most {Nav3dFormat.MaxPresets}.", nameof(level));
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

        foreach (Nav3dObstacle obstacle in level.Obstacles)
        {
            if (obstacle.Cell >= cells)
            {
                throw new ArgumentException($"obstacle \"{obstacle.ClassName}\" names cell {obstacle.Cell} of {cells}.", nameof(level));
            }
        }

        int block = level.CellVoxels * level.CellVoxels;
        int columnCount = level.ColumnStarts.Length - 1;
        if (columnCount < 0 || level.ColumnStarts[0] != 0 || level.ColumnStarts[^1] != level.Leaves.Length)
        {
            throw new ArgumentException($"the column starts do not frame the {level.Leaves.Length} leaves.", nameof(level));
        }

        for (int j = 0; j < columnCount; j++)
        {
            if (level.ColumnStarts[j + 1] < level.ColumnStarts[j])
            {
                throw new ArgumentException($"column {j}'s leaves end before they start.", nameof(level));
            }
        }

        bool[] used = new bool[columnCount / Math.Max(1, block) + 1];
        int placed = 0;
        foreach (int root in level.Roots)
        {
            if (root == -1)
            {
                continue;
            }

            if (root < 0 || root % block != 0 || root + block > columnCount || used[root / block])
            {
                throw new ArgumentException($"a root {root} is not the start of its own block of {block} columns within {columnCount}.", nameof(level));
            }

            used[root / block] = true;
            placed++;
        }

        if ((long)placed * block != columnCount)
        {
            throw new ArgumentException($"{placed} placed cells own {(long)placed * block} columns, not {columnCount}.", nameof(level));
        }

        // Each block's height: the cube's, or its cell's own (version 3),
        // which is 1 to 255 for a placed cell and 0 for an empty one.
        int[] blockHeight = new int[Math.Max(1, columnCount / Math.Max(1, block))];
        Array.Fill(blockHeight, level.CellVoxels);
        if (level.CellHeights is { } heights)
        {
            if (heights.Count != cells)
            {
                throw new ArgumentException($"the level has {cells} cells but {heights.Count} cell heights.", nameof(level));
            }

            for (int c = 0; c < cells; c++)
            {
                bool empty = level.Roots[c] == -1;
                if (empty ? heights[c] != 0 : heights[c] is < 1 or > Nav3dFormat.MaxColumnVoxels)
                {
                    throw new ArgumentException(
                        $"cell {c} is {heights[c]} voxels tall; a placed cell is 1 to {Nav3dFormat.MaxColumnVoxels}, an empty one 0.", nameof(level));
                }

                if (!empty)
                {
                    blockHeight[level.Roots[c] / block] = heights[c];
                }
            }
        }

        if (level.Clearance.Length % 4 != 0)
        {
            throw new ArgumentException("the clearance section is not a whole number of four-byte words.", nameof(level));
        }

        for (int l = 0, column = 0; l < level.Leaves.Length; l++)
        {
            while (level.ColumnStarts[column + 1] <= l)
            {
                column++;
            }

            Nav3dLeaf leaf = level.Leaves[l];
            if (leaf.Height < 1 || leaf.ZLo + leaf.Height > blockHeight[column / block])
            {
                throw new ArgumentException($"a leaf from voxel {leaf.ZLo}, {leaf.Height} high, leaves its cell.", nameof(level));
            }

            foreach (uint offset in new[] { leaf.PlayerClearance, leaf.NpcClearance })
            {
                if (offset % 4 != 0 || offset >= level.Clearance.Length
                    || Nav3dClearance.Problem(level.Clearance.AsSpan((int)offset), level.Obstacles.Count, level.Brushes.Count) is not null)
                {
                    throw new ArgumentException($"a leaf's clearance record at {offset} is out of range or malformed.", nameof(level));
                }
            }
        }

        foreach (float[] brush in level.Brushes)
        {
            if (brush.Length < 16 || brush.Length % 4 != 0)
            {
                throw new ArgumentException("a brush has at least four planes of four floats.", nameof(level));
            }
        }

        foreach (Nav3dJump jump in level.Jumps)
        {
            if (jump.LeafA >= level.Leaves.Length || jump.LeafB >= level.Leaves.Length || jump.LeafA >= jump.LeafB
                || (byte)jump.Direction > 3 || jump.ClassMask is 0 or > 3 || jump.Columns < 1)
            {
                throw new ArgumentException($"a jump link ({jump.LeafA}, {jump.LeafB}) is out of range or out of order.", nameof(level));
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

    private static byte[] PresetRecords(Nav3dLevel level, Strings strings)
    {
        byte[] bytes = new byte[level.Presets.Count * Nav3dFormat.PresetRecordBytes];
        for (int i = 0; i < level.Presets.Count; i++)
        {
            Nav3dPreset preset = level.Presets[i];
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.PresetRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, strings.Offset(preset.Name));
            BinaryPrimitives.WriteSingleLittleEndian(r[4..], preset.Width);
            BinaryPrimitives.WriteSingleLittleEndian(r[8..], preset.Height);
            r[12] = (byte)preset.ClipClass;
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
            r[0] = leaf.ZLo;
            r[1] = leaf.Height;
            BinaryPrimitives.WriteUInt16LittleEndian(r[2..], (ushort)leaf.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(r[4..], leaf.Cost);
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], leaf.PlayerClearance);
            BinaryPrimitives.WriteUInt32LittleEndian(r[12..], leaf.NpcClearance);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], leaf.PlayerFloorZ);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], leaf.NpcFloorZ);
        }

        return bytes;
    }

    private static byte[] ObstacleRecords(Nav3dLevel level, Strings strings)
    {
        byte[] bytes = new byte[level.Obstacles.Count * Nav3dFormat.ObstacleRecordBytes];
        for (int i = 0; i < level.Obstacles.Count; i++)
        {
            Nav3dObstacle o = level.Obstacles[i];
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.ObstacleRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, strings.Offset(o.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], strings.Offset(o.ClassName));
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], o.Cell);
            BinaryPrimitives.WriteInt32LittleEndian(r[12..], o.HammerId);
            r[16] = (byte)o.Kind;
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], o.Mins.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], o.Mins.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[28..], o.Mins.Z);
            BinaryPrimitives.WriteSingleLittleEndian(r[32..], o.Maxs.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[36..], o.Maxs.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[40..], o.Maxs.Z);
        }

        return bytes;
    }

    private static byte[] JumpRecords(IReadOnlyList<Nav3dJump> jumps)
    {
        byte[] bytes = new byte[jumps.Count * Nav3dFormat.JumpRecordBytes];
        for (int i = 0; i < jumps.Count; i++)
        {
            Nav3dJump j = jumps[i];
            Span<byte> r = bytes.AsSpan(i * Nav3dFormat.JumpRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, j.LeafA);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], j.LeafB);
            BinaryPrimitives.WriteSingleLittleEndian(r[8..], j.Rise);
            r[12] = j.ClassMask;
            r[13] = (byte)j.Direction;
            r[14] = j.Columns;
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

    private static byte[] Floats(IReadOnlyList<float[]> brushes)
    {
        // Loops rather than a lambda: a non-capturing lambda is cached in a
        // static field, which the no-mutable-statics rule counts.
        int count = 0;
        foreach (float[] brush in brushes)
        {
            count += brush.Length;
        }

        byte[] bytes = new byte[count * 4];
        int at = 0;
        foreach (float[] brush in brushes)
        {
            foreach (float value in brush)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
                at += 4;
            }
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
