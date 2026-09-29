//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The pack's library section naming the library's skybox room (the rooms
/// design, 4.12 and open point O11: the 3D skybox is a library section).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a section.</b> The skybox room is compiled and packed like any
/// room (its container, link sections and lighting under its own name), but
/// no level places it: the link adds it below the grid by itself, and has
/// to know which entry it is without the library VMF (decision D1). This
/// section says so; it is written only for a library with a skybox, so a
/// library without one packs as before.
/// </para>
/// <para>
/// <b>The bytes</b> follow the library sections' convention: a codec byte
/// (0, none: a name gains nothing from compression), the payload's length as
/// a big-endian <c>int64</c>, then the payload: an <c>int32</c> revision
/// (<see cref="Revision"/>), and the room's name as an <c>int32</c> byte
/// length and UTF-8. A revision this build does not read is no skybox; an
/// older build skips the tag and links levels without the skybox, as it
/// always did. The pack's format version is unchanged.
/// </para>
/// </remarks>
public static class RoomLibrarySkybox
{
    /// <summary>The tag of the pack's library section naming the skybox room.</summary>
    public const string SectionTag = "SKYB";

    /// <summary>The revision of the section's payload this build writes and reads.</summary>
    public const int Revision = 1;

    // codec byte + int64 payload length
    private const int HeaderBytes = 1 + 8;

    /// <summary>The section naming a skybox room.</summary>
    /// <param name="room">The skybox room's name.</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="room"/> is null.</exception>
    public static RoomPackSectionData ToSection(string room)
    {
        ArgumentNullException.ThrowIfNull(room);
        byte[] name = Encoding.UTF8.GetBytes(room);
        byte[] bytes = new byte[HeaderBytes + 8 + name.Length];
        bytes[0] = RoomLibraryOptions.CodecNone;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), 8 + name.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(HeaderBytes), Revision);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(HeaderBytes + 4), name.Length);
        name.CopyTo(bytes, HeaderBytes + 8);
        return new RoomPackSectionData(SectionTag, bytes);
    }

    /// <summary>Reads the skybox room's name back from its section's bytes.</summary>
    /// <param name="section">The section's bytes, as the pack holds them.</param>
    /// <returns>The name; null for a revision this build does not read.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a length that disagrees with the
    /// bytes, a payload cut short or with bytes after its end, or a name that
    /// is not a room name.
    /// </exception>
    public static string? Read(ReadOnlySpan<byte> section)
    {
        if (section.Length < HeaderBytes + 4)
        {
            throw Bad($"is {section.Length} bytes, shorter than its header");
        }

        if (section[0] != RoomLibraryOptions.CodecNone)
        {
            throw Bad($"has codec {section[0]}; this build reads codec {RoomLibraryOptions.CodecNone} (none)");
        }

        ReadOnlySpan<byte> payload = section[HeaderBytes..];
        long length = BinaryPrimitives.ReadInt64BigEndian(section[1..]);
        if (length != payload.Length)
        {
            throw Bad($"says {length} bytes but holds {payload.Length}");
        }

        if (BinaryPrimitives.ReadInt32BigEndian(payload) != Revision)
        {
            return null;
        }

        if (payload.Length < 8)
        {
            throw Bad("is cut short");
        }

        int size = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
        if (size < 0 || size != payload.Length - 8)
        {
            throw Bad(size < 0 || size > payload.Length - 8 ? "is cut short" : $"has {payload.Length - 8 - size} bytes after its name");
        }

        string name = Encoding.UTF8.GetString(payload[8..]);
        return RoomNames.Problem(name) is { } problem ? throw Bad($"names \"{name}\", which {problem}") : name;
    }

    private static LinkException Bad(string what) => new($"the room pack's {SectionTag} section {what}.");
}
