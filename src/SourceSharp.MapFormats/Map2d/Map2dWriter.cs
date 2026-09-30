//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.MapFormats.Map2d;

/// <summary>Writes a <see cref="Map2dLevel"/> as a <c>.map2d</c> file (<see cref="Map2dFormat"/>).</summary>
/// <remarks>
/// <para>
/// The bytes are a function of the level alone: sections in a fixed order
/// (<c>STRS</c>, <c>ROOM</c>, <c>POLY</c>, <c>PNTS</c>, <c>DOOR</c>,
/// <c>MARK</c>), each padded with zeros to a four-byte boundary; strings
/// interned in the order they are first met (rooms' names and labels, then
/// doors' sockets, then markers' kinds and labels), so one level always
/// writes one file, whatever machine or thread count made it.
/// </para>
/// <para>
/// <b>Refused</b> (<see cref="ArgumentException"/>): a ring of fewer than
/// three points or with its band upside down, a placement index outside the
/// placements, a rotation outside 0 to 3, a string holding a NUL, and a
/// room, door or marker float that is not finite: none of them is a map a
/// reader could draw.
/// </para>
/// </remarks>
public static class Map2dWriter
{
    /// <summary>The file for a level.</summary>
    /// <param name="level">The level's map.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> is null.</exception>
    /// <exception cref="ArgumentException">The level holds something no reader could draw (see the remarks).</exception>
    public static byte[] Write(Map2dLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        Check(level);

        Strings strings = new();
        foreach (Map2dRoom room in level.Rooms)
        {
            strings.Add(room.Name);
            strings.Add(room.Label);
        }

        foreach (Map2dDoor door in level.Doors)
        {
            strings.Add(door.Socket);
        }

        foreach (Map2dMarker marker in level.Markers)
        {
            strings.Add(marker.Kind);
            strings.Add(marker.Label);
        }

        bool shortPoints = level.ShortPoints;
        int points = level.Rings.Sum(r => r.Points.Length);

        byte[] rooms = new byte[level.Rooms.Length * Map2dFormat.RoomBytes];
        for (int i = 0; i < level.Rooms.Length; i++)
        {
            Map2dRoom room = level.Rooms[i];
            Span<byte> r = rooms.AsSpan(i * Map2dFormat.RoomBytes);
            BinaryPrimitives.WriteInt32LittleEndian(r, room.CellX);
            BinaryPrimitives.WriteInt32LittleEndian(r[4..], room.CellY);
            BinaryPrimitives.WriteInt32LittleEndian(r[8..], room.Rotation);
            BinaryPrimitives.WriteSingleLittleEndian(r[12..], room.Height);
            BinaryPrimitives.WriteUInt32LittleEndian(r[16..], strings.Offset(room.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(r[20..], strings.Offset(room.Label));
        }

        byte[] rings = new byte[level.Rings.Length * Map2dFormat.RingBytes];
        byte[] pointBytes = new byte[points * (shortPoints ? 4 : 8)];
        int first = 0;
        for (int i = 0; i < level.Rings.Length; i++)
        {
            Map2dRing ring = level.Rings[i];
            Span<byte> r = rings.AsSpan(i * Map2dFormat.RingBytes);
            BinaryPrimitives.WriteInt32LittleEndian(r, ring.Placement);
            BinaryPrimitives.WriteInt32LittleEndian(r[4..], ring.ZLow);
            BinaryPrimitives.WriteInt32LittleEndian(r[8..], ring.ZHigh);
            BinaryPrimitives.WriteUInt32LittleEndian(r[12..], ring.IsHole ? Map2dFormat.Hole : 0u);
            BinaryPrimitives.WriteInt32LittleEndian(r[16..], first);
            BinaryPrimitives.WriteInt32LittleEndian(r[20..], ring.Points.Length);
            foreach (Map2dPoint p in ring.Points)
            {
                if (shortPoints)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(pointBytes.AsSpan(first * 4), (short)p.X);
                    BinaryPrimitives.WriteInt16LittleEndian(pointBytes.AsSpan((first * 4) + 2), (short)p.Y);
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(pointBytes.AsSpan(first * 8), p.X);
                    BinaryPrimitives.WriteInt32LittleEndian(pointBytes.AsSpan((first * 8) + 4), p.Y);
                }

                first++;
            }
        }

        byte[] doors = new byte[level.Doors.Length * Map2dFormat.DoorBytes];
        for (int i = 0; i < level.Doors.Length; i++)
        {
            Map2dDoor door = level.Doors[i];
            Span<byte> r = doors.AsSpan(i * Map2dFormat.DoorBytes);
            BinaryPrimitives.WriteInt32LittleEndian(r, door.Placement);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], strings.Offset(door.Socket));
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], door.Open ? Map2dFormat.Open : 0u);
            BinaryPrimitives.WriteInt32LittleEndian(r[12..], door.Neighbour);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], door.X0);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], door.Y0);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], door.X1);
            BinaryPrimitives.WriteSingleLittleEndian(r[28..], door.Y1);
            BinaryPrimitives.WriteSingleLittleEndian(r[32..], door.ZLow);
            BinaryPrimitives.WriteSingleLittleEndian(r[36..], door.ZHigh);
        }

        byte[] markers = new byte[level.Markers.Length * Map2dFormat.MarkerBytes];
        for (int i = 0; i < level.Markers.Length; i++)
        {
            Map2dMarker marker = level.Markers[i];
            Span<byte> r = markers.AsSpan(i * Map2dFormat.MarkerBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, strings.Offset(marker.Kind));
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], strings.Offset(marker.Label));
            BinaryPrimitives.WriteInt32LittleEndian(r[8..], marker.Placement);
            BinaryPrimitives.WriteSingleLittleEndian(r[12..], marker.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], marker.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], marker.Z);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], marker.Yaw);
        }

        (string Tag, byte[] Bytes)[] sections =
        [
            (Map2dFormat.StringsTag, strings.ToArray()),
            (Map2dFormat.RoomsTag, rooms),
            (Map2dFormat.RingsTag, rings),
            (Map2dFormat.PointsTag, pointBytes),
            (Map2dFormat.DoorsTag, doors),
            (Map2dFormat.MarkersTag, markers),
        ];

        int at = Map2dFormat.HeaderBytes + (sections.Length * Map2dFormat.DirectoryEntryBytes);
        int[] offsets = new int[sections.Length];
        for (int s = 0; s < sections.Length; s++)
        {
            at = Align(at);
            offsets[s] = at;
            at += sections[s].Bytes.Length;
        }

        byte[] file = new byte[Align(at)];
        Span<byte> h = file;
        Map2dFormat.Magic.CopyTo(h);
        Map2dExtent extent = level.Extent;
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], Map2dFormat.Version);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], Map2dFormat.HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], sections.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], level.MapChecksum);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], shortPoints ? Map2dFormat.ShortPoints : 0u);
        BinaryPrimitives.WriteSingleLittleEndian(h[28..], level.CellSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[32..], level.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(h[36..], level.Rows);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], extent.MinX);
        BinaryPrimitives.WriteInt32LittleEndian(h[44..], extent.MinY);
        BinaryPrimitives.WriteInt32LittleEndian(h[48..], extent.MaxX);
        BinaryPrimitives.WriteInt32LittleEndian(h[52..], extent.MaxY);
        BinaryPrimitives.WriteInt32LittleEndian(h[56..], extent.MinZ);
        BinaryPrimitives.WriteInt32LittleEndian(h[60..], extent.MaxZ);
        BinaryPrimitives.WriteInt32LittleEndian(h[64..], level.Rooms.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h[68..], level.Rings.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h[72..], points);
        BinaryPrimitives.WriteInt32LittleEndian(h[76..], level.Doors.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h[80..], level.Markers.Length);

        for (int s = 0; s < sections.Length; s++)
        {
            Span<byte> entry = h[(Map2dFormat.HeaderBytes + (s * Map2dFormat.DirectoryEntryBytes))..];
            Encoding.ASCII.GetBytes(sections[s].Tag, entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)offsets[s]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)sections[s].Bytes.Length);
            sections[s].Bytes.CopyTo(h[offsets[s]..]);
        }

        return file;
    }

    private static int Align(int at) => (at + 3) & ~3;

    private static void Check(Map2dLevel level)
    {
        int placements = level.Rooms.Length;
        void Placement(int index, string what, bool allowNone)
        {
            if (index >= placements || index < (allowNone ? Map2dFormat.NoPlacement : 0))
            {
                throw new ArgumentException($"{what} names placement {index}; the map has {placements}.", nameof(level));
            }
        }

        static void Text(string? value, string what)
        {
            if (value is null || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"{what} is null or holds a NUL.", nameof(level));
            }
        }

        static void Finite(string what, params ReadOnlySpan<float> values)
        {
            foreach (float value in values)
            {
                if (!float.IsFinite(value))
                {
                    throw new ArgumentException($"{what} has a coordinate that is not a finite number.", nameof(level));
                }
            }
        }

        foreach (Map2dRoom room in level.Rooms)
        {
            Text(room.Name, "a room's name");
            Text(room.Label, "a room's label");
            Finite("a room", room.Height);
            if ((uint)room.Rotation > 3)
            {
                throw new ArgumentException($"a room has rotation {room.Rotation}; a placement turns 0 to 3 quarter turns.", nameof(level));
            }
        }

        foreach (Map2dRing ring in level.Rings)
        {
            Placement(ring.Placement, "a ring", allowNone: true);
            if (ring.Points.IsDefault || ring.Points.Length < 3 || ring.ZLow > ring.ZHigh)
            {
                throw new ArgumentException("a ring has fewer than three points or its band upside down.", nameof(level));
            }
        }

        foreach (Map2dDoor door in level.Doors)
        {
            Placement(door.Placement, "a door", allowNone: false);
            Placement(door.Neighbour, "a door's neighbour", allowNone: true);
            Text(door.Socket, "a door's socket");
            Finite("a door", door.X0, door.Y0, door.X1, door.Y1, door.ZLow, door.ZHigh);
        }

        foreach (Map2dMarker marker in level.Markers)
        {
            Placement(marker.Placement, "a marker", allowNone: true);
            Text(marker.Kind, "a marker's kind");
            Text(marker.Label, "a marker's label");
            Finite("a marker", marker.X, marker.Y, marker.Z, marker.Yaw);
        }
    }

    /// <summary>The string table: NUL-terminated UTF-8, the empty string at offset 0, each string once.</summary>
    private sealed class Strings
    {
        private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal) { [string.Empty] = 0 };
        private readonly MemoryStream _bytes = new();

        public Strings() => _bytes.WriteByte(0);

        public void Add(string value)
        {
            if (!_offsets.ContainsKey(value))
            {
                _offsets[value] = (uint)_bytes.Length;
                _bytes.Write(Encoding.UTF8.GetBytes(value));
                _bytes.WriteByte(0);
            }
        }

        public uint Offset(string value) => _offsets[value];

        public byte[] ToArray() => _bytes.ToArray();
    }
}
