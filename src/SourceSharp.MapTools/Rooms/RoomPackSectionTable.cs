//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Nav;

namespace SourceSharp.MapTools.Rooms;

/// <summary>One section of a pack as the section table lists it.</summary>
/// <param name="Room">The room it belongs to, or null for a library section.</param>
/// <param name="Tag">The section's tag.</param>
/// <param name="Offset">Where its bytes start, from the start of the pack.</param>
/// <param name="Length">How many bytes the pack stores for it.</param>
/// <param name="Codec">
/// The framing codec byte (0 none, 1 Deflate, 2 Brotli), or null for a
/// section that is not framed (<c>ROOM</c>, <c>CMPL</c>) or whose frame does
/// not read.
/// </param>
/// <param name="DecodedLength">The payload's decoded length from the frame, or null as for <paramref name="Codec"/>.</param>
/// <param name="Revision">
/// The payload's leading revision, or null for a section without one
/// (<c>LENT</c> carries VMF text) or whose payload does not decode.
/// </param>
/// <param name="Sha256">The first sixteen hex characters of the stored bytes' SHA-256: equal sections, equal prefixes.</param>
public sealed record RoomPackSectionInfo(
    string? Room, string Tag, long Offset, long Length, int? Codec, long? DecodedLength, int? Revision, string Sha256);

/// <summary>
/// The table of every section a pack holds: what <c>ssmap rooms -rooms</c>
/// prints, and what an incremental run is checked with.
/// </summary>
/// <remarks>
/// <para>
/// Every section after <c>ROOM</c> and <c>CMPL</c> is framed the same way: a
/// codec byte, the payload's decoded length as a big-endian <c>int64</c>,
/// then the payload as the codec stores it, whose decoded bytes open with
/// a big-endian <c>int32</c> revision (except <c>LENT</c>, whose payload is
/// VMF text). The table reads the frame and the revision of each, so a
/// reader can see at a glance which sections a build wrote and how; the
/// hash prefix lets two packs, or two rooms, be compared section by section.
/// </para>
/// <para>
/// A diagnostic, not a loader: a section whose frame or payload does not
/// read is listed with its size and hash and without the fields it lacks,
/// never refused, so the table also serves to look at a damaged pack.
/// </para>
/// </remarks>
public static class RoomPackSectionTable
{
    // codec byte + int64 decoded length
    private const int FrameBytes = 9;

    /// <summary>The largest payload the table decodes to read a revision; larger ones are listed without it.</summary>
    private const long MaxDecodeBytes = 256L * 1024 * 1024;

    /// <summary>Reads every section of a pack.</summary>
    /// <param name="r">The pack, positioned where it starts; read to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The library sections, then each room's, in pack order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="r"/> is null.</exception>
    /// <exception cref="LinkException">The pack's index does not read, or its sections run past its end.</exception>
    public static async Task<IReadOnlyList<RoomPackSectionInfo>> ReadAsync(Stream r, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(r, cancellationToken).ConfigureAwait(false);
        using MemoryStream rest = new();
        await r.CopyToAsync(rest, cancellationToken).ConfigureAwait(false);
        byte[] data = rest.GetBuffer();
        long held = rest.Length;

        List<RoomPackSectionInfo> table = [];
        void Add(string? room, RoomPackSection section)
        {
            long start = section.Offset - index.IndexEnd;
            if (start < 0 || section.Length < 0 || start + section.Length > held)
            {
                throw new LinkException(
                    $"the room pack's {section.Tag} section{(room is null ? string.Empty : $" of room \"{room}\"")} runs past the pack's end.");
            }

            table.Add(Describe(room, section, data.AsSpan((int)start, (int)section.Length)));
        }

        foreach (RoomPackSection section in index.LibrarySections)
        {
            Add(null, section);
        }

        foreach (RoomPackEntry entry in index.Entries)
        {
            foreach (RoomPackSection section in entry.Sections)
            {
                Add(entry.Name, section);
            }
        }

        return table;
    }

    /// <summary>The table as <c>ssmap rooms</c> prints it: one header line, then one line per section.</summary>
    /// <param name="table">The sections.</param>
    /// <returns>The text, LF line ends.</returns>
    public static string Format(IReadOnlyList<RoomPackSectionInfo> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        StringBuilder text = new();
        text.Append("owner tag offset length codec decoded revision sha256\n");
        foreach (RoomPackSectionInfo s in table)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{s.Room ?? "(library)"} {s.Tag} {s.Offset} {s.Length} {CodecName(s.Codec)} {Dash(s.DecodedLength)} {Dash(s.Revision)} {s.Sha256}\n");
        }

        return text.ToString();
    }

    internal static RoomPackSectionInfo Describe(string? room, RoomPackSection section, ReadOnlySpan<byte> bytes)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        if (section.Tag is RoomPack.RoomSection or RoomCompileIds.PackSection || bytes.Length < FrameBytes || bytes[0] > (byte)NavCodec.Brotli)
        {
            return new RoomPackSectionInfo(room, section.Tag, section.Offset, section.Length, null, null, null, hash);
        }

        int codec = bytes[0];
        long decoded = BinaryPrimitives.ReadInt64BigEndian(bytes[1..]);
        int? revision = null;
        if (section.Tag != RoomLibraryEntities.SectionTag && decoded is >= 4 and <= MaxDecodeBytes)
        {
            try
            {
                byte[] payload = NavCompression.Decompress((NavCodec)codec, bytes[FrameBytes..], (int)decoded);
                revision = BinaryPrimitives.ReadInt32BigEndian(payload);
            }
            catch (InvalidDataException)
            {
                // A payload that does not decode is listed without its revision.
            }
        }

        return new RoomPackSectionInfo(room, section.Tag, section.Offset, section.Length, codec, decoded, revision, hash);
    }

    private static string CodecName(int? codec) => codec switch
    {
        null => "-",
        0 => "none",
        1 => "deflate",
        _ => "brotli",
    };

    private static string Dash(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
