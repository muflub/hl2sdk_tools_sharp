//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Map2d;

/// <summary>
/// The constants of the <c>.map2d</c> level map file: its magic, version,
/// header and record sizes, section tags and flag bits.
/// </summary>
/// <remarks>
/// <para>
/// The file is specified in <c>docs/map2d-format.md</c>, written so a reader
/// in another language can be built from it alone; this class is the C#
/// spelling of the same numbers. It follows the <c>.nav3d</c>'s container
/// conventions (a magic, a version, a header that records its own size, a
/// directory of tagged sections, every section on a four-byte boundary,
/// every multi-byte value little-endian) for the same reason: the game reads
/// it at run time, on little-endian machines, in one pass, and a reader may
/// view a section as an array of its records without copying.
/// </para>
/// <para>
/// <b>No codec.</b> Unlike the <c>.nav3d</c> the file has no envelope and is
/// never compressed: a level's map is a few kilobytes to a few hundred
/// (vector data), less than the time a decoder would take to start.
/// </para>
/// <para>
/// <b>Versions.</b> <see cref="Version"/> changes only for a change an older
/// reader must not read around (a record that changes size or meaning). A
/// new section is added under a new tag without a version change; readers
/// skip tags they do not know.
/// </para>
/// </remarks>
public static class Map2dFormat
{
    /// <summary>The file's eight magic bytes: <c>SSMAP2D</c> and a NUL.</summary>
    public static ReadOnlySpan<byte> Magic => "SSMAP2D\0"u8;

    /// <summary>The version this build writes and reads.</summary>
    public const int Version = 1;

    /// <summary>The file extension <c>ssmap link</c> writes beside the map.</summary>
    public const string Extension = ".map2d";

    /// <summary>The header's size in this version; the directory starts here.</summary>
    public const int HeaderBytes = 96;

    /// <summary>One entry of the section directory: tag, offset, length.</summary>
    public const int DirectoryEntryBytes = 12;

    /// <summary>The most sections a file may list: a bound a damaged count cannot pass.</summary>
    public const int MaxSections = 64;

    /// <summary>A placement index that names no placement: a map made without a level file.</summary>
    public const int NoPlacement = -1;

    /// <summary>The string table: NUL-terminated UTF-8, offset 0 the empty string.</summary>
    public const string StringsTag = "STRS";

    /// <summary>The placements (<see cref="RoomBytes"/> each).</summary>
    public const string RoomsTag = "ROOM";

    /// <summary>The rings (<see cref="RingBytes"/> each).</summary>
    public const string RingsTag = "POLY";

    /// <summary>The rings' points: <c>int16</c> or <c>int32</c> x, y pairs (<see cref="ShortPoints"/>).</summary>
    public const string PointsTag = "PNTS";

    /// <summary>The doors (<see cref="DoorBytes"/> each).</summary>
    public const string DoorsTag = "DOOR";

    /// <summary>The markers (<see cref="MarkerBytes"/> each).</summary>
    public const string MarkersTag = "MARK";

    /// <summary>A <c>ROOM</c> record: cell x, cell y, rotation, height, name, label.</summary>
    public const int RoomBytes = 24;

    /// <summary>A <c>POLY</c> record: placement, z low, z high, flags, first point, point count.</summary>
    public const int RingBytes = 24;

    /// <summary>A <c>DOOR</c> record: placement, socket, flags, neighbour, two ends, z band.</summary>
    public const int DoorBytes = 40;

    /// <summary>A <c>MARK</c> record: kind, label, placement, position, yaw.</summary>
    public const int MarkerBytes = 28;

    /// <summary>Header flag: every point is an <c>int16</c> pair (else an <c>int32</c> pair).</summary>
    public const uint ShortPoints = 1;

    /// <summary>Ring flag: the ring is a hole of the outer ring before it.</summary>
    public const uint Hole = 1;

    /// <summary>Door flag: the door is open (its socket is joined).</summary>
    public const uint Open = 1;
}
