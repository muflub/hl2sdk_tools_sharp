//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace SourceSharp.MapFormats.Map2d;

/// <summary>
/// Reads a <c>.map2d</c> file (<see cref="Map2dFormat"/>): the reference
/// reader of <c>docs/map2d-format.md</c>, which the facts hold to the writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validated once, at load.</b> Everything a game would index by is
/// checked before a record is handed out: the magic, the version, the
/// header's size and counts, every section inside the file and on a
/// four-byte boundary and sized to its count, the string table and every
/// offset into it, every ring's points inside <c>PNTS</c> and its band the
/// right way up, every placement index, and the header's extent against
/// the contents. A file that fails any of it is refused with an
/// <see cref="InvalidDataException"/> that names what is wrong, never drawn
/// half right.
/// </para>
/// <para>
/// <b>The binding.</b> <see cref="Read(ReadOnlySpan{byte}, uint)"/> also
/// holds the file to the map the game loaded: a file made for another
/// compile of the map (a stale sidecar left beside a relinked map) is
/// refused, since its floors and doors would be drawn where the map no
/// longer has them.
/// </para>
/// <para>Tags the reader does not know are skipped: a later build may add sections.</para>
/// </remarks>
public static class Map2dReader
{
    /// <summary>Reads a file, whatever map it was made for.</summary>
    /// <param name="file">The file's bytes.</param>
    /// <returns>The level's map.</returns>
    /// <exception cref="InvalidDataException">The file is not a <c>.map2d</c> this build reads, or it is damaged; the message says how.</exception>
    public static Map2dLevel Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 8 || !file[..8].SequenceEqual(Map2dFormat.Magic))
        {
            throw new InvalidDataException("not a .map2d file: the magic is missing.");
        }

        if (file.Length < Map2dFormat.HeaderBytes)
        {
            throw new InvalidDataException($"the .map2d file is {file.Length} bytes, shorter than its {Map2dFormat.HeaderBytes}-byte header.");
        }

        int version = I32(file, 8);
        if (version != Map2dFormat.Version)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $".map2d version {version}; this build reads version {Map2dFormat.Version}."));
        }

        int headerBytes = I32(file, 12);
        int sectionCount = I32(file, 16);
        if (headerBytes < Map2dFormat.HeaderBytes || headerBytes % 4 != 0 || sectionCount < 0 || sectionCount > Map2dFormat.MaxSections
            || (long)headerBytes + ((long)sectionCount * Map2dFormat.DirectoryEntryBytes) > file.Length)
        {
            throw new InvalidDataException("the .map2d header's size or section count is out of range.");
        }

        uint flags = U32(file, 24);
        if ((flags & ~Map2dFormat.ShortPoints) != 0)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"the .map2d header has unknown flags 0x{flags:x8}."));
        }

        bool shortPoints = (flags & Map2dFormat.ShortPoints) != 0;
        float cellSize = F32(file, 28);
        int columns = I32(file, 32);
        int rows = I32(file, 36);
        Map2dExtent extent = new(I32(file, 40), I32(file, 44), I32(file, 48), I32(file, 52), I32(file, 56), I32(file, 60));
        int roomCount = I32(file, 64);
        int ringCount = I32(file, 68);
        int pointCount = I32(file, 72);
        int doorCount = I32(file, 76);
        int markerCount = I32(file, 80);
        if (!float.IsFinite(cellSize) || cellSize < 0 || columns < 0 || rows < 0 || (cellSize == 0) != (columns == 0 && rows == 0)
            || roomCount < 0 || ringCount < 0 || pointCount < 0 || doorCount < 0 || markerCount < 0)
        {
            throw new InvalidDataException("the .map2d header's grid or counts are out of range.");
        }

        Dictionary<string, (int Offset, int Length)> sections = new(StringComparer.Ordinal);
        for (int s = 0; s < sectionCount; s++)
        {
            int at = headerBytes + (s * Map2dFormat.DirectoryEntryBytes);
            string tag = Encoding.ASCII.GetString(file.Slice(at, 4));
            uint offset = U32(file, at + 4);
            uint length = U32(file, at + 8);
            if (offset % 4 != 0 || offset < headerBytes + (sectionCount * Map2dFormat.DirectoryEntryBytes) || (long)offset + length > file.Length)
            {
                throw new InvalidDataException($"the .map2d section \"{tag}\" lies outside the file or off a four-byte boundary.");
            }

            if (!sections.TryAdd(tag, ((int)offset, (int)length)))
            {
                throw new InvalidDataException($"the .map2d file has two \"{tag}\" sections.");
            }
        }

        ReadOnlySpan<byte> Section(ReadOnlySpan<byte> bytes, string tag, long expected)
        {
            if (!sections.TryGetValue(tag, out (int Offset, int Length) found))
            {
                throw new InvalidDataException($"the .map2d file has no \"{tag}\" section.");
            }

            if (expected >= 0 && found.Length != expected)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"the .map2d \"{tag}\" section is {found.Length} bytes, which its counts do not allow."));
            }

            return bytes.Slice(found.Offset, found.Length);
        }

        byte[] table = Section(file, Map2dFormat.StringsTag, -1).ToArray();
        if (table.Length == 0 || table[0] != 0 || table[^1] != 0)
        {
            throw new InvalidDataException("the .map2d string table must start with the empty string and end with a NUL.");
        }

        Dictionary<uint, string> strings = [];
        string Text(uint offset)
        {
            if (strings.TryGetValue(offset, out string? known))
            {
                return known;
            }

            if (offset >= table.Length || (offset > 0 && table[(int)offset - 1] != 0))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"string offset {offset} is not the start of a string in the {table.Length}-byte string table."));
            }

            ReadOnlySpan<byte> rest = table.AsSpan((int)offset);
            string value;
            try
            {
                value = new UTF8Encoding(false, true).GetString(rest[..rest.IndexOf((byte)0)]);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"the string at offset {offset} is not UTF-8."));
            }

            strings[offset] = value;
            return value;
        }

        void Placement(int index, string what, bool allowNone)
        {
            if (index >= roomCount || index < (allowNone ? Map2dFormat.NoPlacement : 0))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"{what} names placement {index}; the file has {roomCount}."));
            }
        }

        static void Finite(string what, params ReadOnlySpan<float> values)
        {
            foreach (float value in values)
            {
                if (!float.IsFinite(value))
                {
                    throw new InvalidDataException($"{what} has a coordinate that is not a finite number.");
                }
            }
        }

        ReadOnlySpan<byte> roomBytes = Section(file, Map2dFormat.RoomsTag, (long)roomCount * Map2dFormat.RoomBytes);
        ImmutableArray<Map2dRoom>.Builder rooms = ImmutableArray.CreateBuilder<Map2dRoom>(roomCount);
        for (int i = 0; i < roomCount; i++)
        {
            ReadOnlySpan<byte> r = roomBytes.Slice(i * Map2dFormat.RoomBytes, Map2dFormat.RoomBytes);
            int rotation = I32(r, 8);
            float height = F32(r, 12);
            if ((uint)rotation > 3)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"room {i} has rotation {rotation}, not 0 to 3."));
            }

            Finite(string.Create(CultureInfo.InvariantCulture, $"room {i}"), height);
            rooms.Add(new Map2dRoom(I32(r, 0), I32(r, 4), rotation, height, Text(U32(r, 16)), Text(U32(r, 20))));
        }

        int pointSize = shortPoints ? 4 : 8;
        ReadOnlySpan<byte> pointBytes = Section(file, Map2dFormat.PointsTag, (long)pointCount * pointSize);
        ReadOnlySpan<byte> ringBytes = Section(file, Map2dFormat.RingsTag, (long)ringCount * Map2dFormat.RingBytes);
        ImmutableArray<Map2dRing>.Builder rings = ImmutableArray.CreateBuilder<Map2dRing>(ringCount);
        for (int i = 0; i < ringCount; i++)
        {
            ReadOnlySpan<byte> r = ringBytes.Slice(i * Map2dFormat.RingBytes, Map2dFormat.RingBytes);
            int placement = I32(r, 0);
            int zLow = I32(r, 4);
            int zHigh = I32(r, 8);
            uint ringFlags = U32(r, 12);
            int first = I32(r, 16);
            int count = I32(r, 20);
            string what = string.Create(CultureInfo.InvariantCulture, $"ring {i}");
            Placement(placement, what, allowNone: true);
            if ((ringFlags & ~Map2dFormat.Hole) != 0 || zLow > zHigh || count < 3 || first < 0 || (long)first + count > pointCount)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"{what} has flags 0x{ringFlags:x8}, band {zLow} to {zHigh} and points {first} + {count} of {pointCount}; one is out of range."));
            }

            ImmutableArray<Map2dPoint>.Builder points = ImmutableArray.CreateBuilder<Map2dPoint>(count);
            for (int p = first; p < first + count; p++)
            {
                points.Add(shortPoints
                    ? new Map2dPoint(BinaryPrimitives.ReadInt16LittleEndian(pointBytes[(p * 4)..]), BinaryPrimitives.ReadInt16LittleEndian(pointBytes[((p * 4) + 2)..]))
                    : new Map2dPoint(I32(pointBytes, p * 8), I32(pointBytes, (p * 8) + 4)));
            }

            rings.Add(new Map2dRing(placement, zLow, zHigh, (ringFlags & Map2dFormat.Hole) != 0, points.MoveToImmutable()));
        }

        ReadOnlySpan<byte> doorBytes = Section(file, Map2dFormat.DoorsTag, (long)doorCount * Map2dFormat.DoorBytes);
        ImmutableArray<Map2dDoor>.Builder doors = ImmutableArray.CreateBuilder<Map2dDoor>(doorCount);
        for (int i = 0; i < doorCount; i++)
        {
            ReadOnlySpan<byte> r = doorBytes.Slice(i * Map2dFormat.DoorBytes, Map2dFormat.DoorBytes);
            string what = string.Create(CultureInfo.InvariantCulture, $"door {i}");
            uint doorFlags = U32(r, 8);
            if ((doorFlags & ~Map2dFormat.Open) != 0)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"{what} has unknown flags 0x{doorFlags:x8}."));
            }

            Placement(I32(r, 0), what, allowNone: false);
            Placement(I32(r, 12), what + "'s neighbour", allowNone: true);
            Map2dDoor door = new(
                I32(r, 0), Text(U32(r, 4)), (doorFlags & Map2dFormat.Open) != 0, I32(r, 12),
                F32(r, 16), F32(r, 20), F32(r, 24), F32(r, 28), F32(r, 32), F32(r, 36));
            Finite(what, door.X0, door.Y0, door.X1, door.Y1, door.ZLow, door.ZHigh);
            doors.Add(door);
        }

        ReadOnlySpan<byte> markerBytes = Section(file, Map2dFormat.MarkersTag, (long)markerCount * Map2dFormat.MarkerBytes);
        ImmutableArray<Map2dMarker>.Builder markers = ImmutableArray.CreateBuilder<Map2dMarker>(markerCount);
        for (int i = 0; i < markerCount; i++)
        {
            ReadOnlySpan<byte> r = markerBytes.Slice(i * Map2dFormat.MarkerBytes, Map2dFormat.MarkerBytes);
            string what = string.Create(CultureInfo.InvariantCulture, $"marker {i}");
            Placement(I32(r, 8), what, allowNone: true);
            Map2dMarker marker = new(Text(U32(r, 0)), Text(U32(r, 4)), I32(r, 8), F32(r, 12), F32(r, 16), F32(r, 20), F32(r, 24));
            Finite(what, marker.X, marker.Y, marker.Z, marker.Yaw);
            markers.Add(marker);
        }

        Map2dLevel level = new()
        {
            MapChecksum = U32(file, 20),
            CellSize = cellSize,
            Columns = columns,
            Rows = rows,
            Rooms = rooms.MoveToImmutable(),
            Rings = rings.MoveToImmutable(),
            Doors = doors.MoveToImmutable(),
            Markers = markers.MoveToImmutable(),
        };

        if (level.Extent != extent)
        {
            throw new InvalidDataException("the .map2d header's extent is not the box its contents fill.");
        }

        return level;
    }

    /// <summary>Reads a file and holds it to the map it is drawn over.</summary>
    /// <param name="file">The file's bytes.</param>
    /// <param name="mapChecksum">The checksum of the map the game loaded (<c>BspMapChecksum</c>).</param>
    /// <returns>The level's map.</returns>
    /// <exception cref="InvalidDataException">
    /// As <see cref="Read(ReadOnlySpan{byte})"/>, or the file was made for
    /// another map: it is stale, and the message says so with both checksums.
    /// </exception>
    public static Map2dLevel Read(ReadOnlySpan<byte> file, uint mapChecksum)
    {
        Map2dLevel level = Read(file);
        return level.MapChecksum == mapChecksum
            ? level
            : throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"the .map2d was made for the map with checksum {level.MapChecksum:x8}, not this one ({mapChecksum:x8}): it is stale; link the level again."));
    }

    private static int I32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);

    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);

    private static float F32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]);
}
