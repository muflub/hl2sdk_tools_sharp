//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The settings a room library makes for every level linked from it, read
/// from the library's worldspawn keys, and the room pack's library section
/// that keeps them.
/// </summary>
/// <param name="EntityReserve">
/// The edicts the library leaves to the game at runtime
/// (<see cref="EntityReserveKey"/>), or null when it sets none and the
/// link's default applies (<see cref="EntityClassTable.DefaultReserve"/>).
/// </param>
/// <remarks>
/// <para>
/// <b>Why in the pack.</b> A link reads the pack and nothing else of the
/// library (decision D1: the link needs no game files and no library VMF),
/// so a setting the library makes for its levels has to travel in the pack.
/// <c>ssmap room</c> reads the keys off the library's worldspawn when it
/// splits the library and writes them as the library section
/// <see cref="SectionTag"/> (<c>LOPT</c>); <c>ssmap link</c> and
/// <c>ssmap layout</c> read them back.
/// </para>
/// <para>
/// <b>Library keys stay out of the rooms.</b> Every room's worldspawn is a
/// copy of the library's, and the flattened level's is too, so a key the
/// library sets for the link would otherwise be compiled into every room
/// and carried into the linked map. The split and the flatten both leave
/// out every key <see cref="IsLibraryKey"/> names, so the linked map and
/// the flattened one agree, and a library that sets none compiles exactly
/// as before.
/// </para>
/// <para>
/// <b>The section</b> follows the pack's convention for new sections: a
/// codec byte (0, none: the only one this build writes or reads, since a
/// few keys gain nothing from compression), the payload's length as a
/// big-endian <c>int64</c>, then the payload: an <c>int32</c> revision
/// (<see cref="Revision"/>), the key count, and per key, in ordinal order,
/// the key and its value (each an <c>int32</c> byte length and UTF-8). A
/// key this build does not know is skipped, so a later build can add keys
/// without raising the revision; a section of another revision reads as no
/// settings. It is written only when the library sets something, so a
/// library that sets nothing writes the pack it always did.
/// </para>
/// </remarks>
public sealed record RoomLibraryOptions(int? EntityReserve = null)
{
    /// <summary>The tag of the pack's library section that holds the settings.</summary>
    public const string SectionTag = "LOPT";

    /// <summary>The revision of the section's payload this build writes and reads.</summary>
    public const int Revision = 1;

    /// <summary>
    /// The library worldspawn key for <see cref="EntityReserve"/>: a whole
    /// number of edicts from 0 to <see cref="EntityClassTable.EdictCap"/>.
    /// </summary>
    public const string EntityReserveKey = "rooms_entity_reserve";

    /// <summary>
    /// The library worldspawn key for <see cref="FoldLogic"/>: <c>0</c> turns
    /// the link-time fold of stateless logic off, <c>1</c> leaves it on.
    /// </summary>
    public const string FoldLogicKey = "rooms_fold_logic";

    /// <summary>
    /// The library worldspawn key for <see cref="NameKeys"/>: the keys, comma
    /// separated, that the library's classes use to name entities, beyond the
    /// built-in table the naming rule reads.
    /// </summary>
    public const string NameKeysKey = "rooms_name_keys";

    /// <summary>
    /// Whether the link folds stateless logic away (relays, constant
    /// branches, <c>logic_auto</c> merges, identical filters); null when the
    /// library does not say, which is on.
    /// </summary>
    /// <remarks>
    /// On by default (the rooms design's recommendation, accepted): folding
    /// saves entities at no runtime cost, but within one tick it can change
    /// the order of events relative to other entities', which a library that
    /// depends on that order turns off here.
    /// </remarks>
    public bool? FoldLogic { get; init; }

    /// <summary>
    /// The name-valued keys the library adds to the built-in table, comma
    /// separated as the key is written (entries trimmed, empty ones left out),
    /// or null. The table is game-dependent; the library extends it for its
    /// game's classes.
    /// </summary>
    public string? NameKeys { get; init; }

    /// <summary>Whether the link folds: <see cref="FoldLogic"/>, on when unset.</summary>
    public bool Folds => FoldLogic ?? true;

    /// <summary><see cref="NameKeys"/> as a set, compared ignoring case as entity keys are; null when there are none.</summary>
    public IReadOnlySet<string>? NameKeySet =>
        NameKeys is { Length: > 0 } keys ? keys.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

    /// <summary>The codec byte for an uncompressed payload, the only one this build writes and reads.</summary>
    public const byte CodecNone = 0;

    // codec byte + int64 payload length
    private const int HeaderBytes = 1 + 8;

    /// <summary>A library that sets nothing.</summary>
    public static RoomLibraryOptions None { get => new(); }

    /// <summary>Whether a worldspawn key is a library setting rather than a map key; compared ignoring case, as entity keys are.</summary>
    /// <param name="key">The key.</param>
    /// <returns>True for <see cref="EntityReserveKey"/>, <see cref="FoldLogicKey"/> and <see cref="NameKeysKey"/>.</returns>
    public static bool IsLibraryKey(string key) =>
        string.Equals(key, EntityReserveKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, FoldLogicKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, NameKeysKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the settings off a library's world chunk.</summary>
    /// <param name="world">The library's <c>world</c> chunk.</param>
    /// <returns>The settings; the first of a repeated key wins, as for any other key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    /// <exception cref="RoomLibraryException">A setting's value is out of range.</exception>
    public static RoomLibraryOptions FromWorld(VmfChunk world)
    {
        ArgumentNullException.ThrowIfNull(world);
        int? reserve = null;
        bool? fold = null;
        string? nameKeys = null;
        bool seenReserve = false, seenFold = false, seenNames = false;
        foreach (VmfKey key in world.Keys)
        {
            if (!seenReserve && string.Equals(key.Name, EntityReserveKey, StringComparison.OrdinalIgnoreCase))
            {
                seenReserve = true;
                reserve = TryParseReserve(key.Value, out int parsed)
                    ? parsed
                    : throw new RoomLibraryException(
                        $"the library's {EntityReserveKey} \"{key.Value}\" is not a whole number of edicts from 0 to {EntityClassTable.EdictCap}.");
            }
            else if (!seenFold && string.Equals(key.Name, FoldLogicKey, StringComparison.OrdinalIgnoreCase))
            {
                seenFold = true;
                fold = TryParseFold(key.Value, out bool parsed)
                    ? parsed
                    : throw new RoomLibraryException($"the library's {FoldLogicKey} \"{key.Value}\" is not 0 or 1.");
            }
            else if (!seenNames && string.Equals(key.Name, NameKeysKey, StringComparison.OrdinalIgnoreCase))
            {
                seenNames = true;
                nameKeys = NormalizeNameKeys(key.Value);
            }
        }

        return new RoomLibraryOptions(reserve) { FoldLogic = fold, NameKeys = nameKeys };
    }

    /// <summary>Parses the fold switch: <c>0</c> or <c>1</c>, nothing else.</summary>
    /// <param name="text">The text.</param>
    /// <param name="fold">Whether it turns the fold on.</param>
    /// <returns>Whether the text is a switch.</returns>
    public static bool TryParseFold(string? text, out bool fold)
    {
        fold = text == "1";
        return text is "0" or "1";
    }

    /// <summary>A name-key list as the section keeps it: entries trimmed, empty ones left out, joined by commas; null when none remain.</summary>
    private static string? NormalizeNameKeys(string value)
    {
        string joined = string.Join(',', value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Parses an entity reserve: a whole number from 0 to <see cref="EntityClassTable.EdictCap"/>, digits only.</summary>
    /// <param name="text">The text.</param>
    /// <param name="reserve">The reserve, or 0 when the text is not one.</param>
    /// <returns>Whether the text is a reserve.</returns>
    public static bool TryParseReserve(string? text, out int reserve)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out reserve)
            && reserve <= EntityClassTable.EdictCap)
        {
            return true;
        }

        reserve = 0;
        return false;
    }

    /// <summary>The pack section holding these settings, or null when the library sets nothing.</summary>
    /// <returns>The section, tagged <see cref="SectionTag"/>, or null.</returns>
    public RoomPackSectionData? ToSection()
    {
        // In ordinal order of the key, as the section's layout says.
        List<(string Key, string Value)> keys = [];
        if (EntityReserve is int reserve)
        {
            keys.Add((EntityReserveKey, reserve.ToString(CultureInfo.InvariantCulture)));
        }

        if (FoldLogic is bool fold)
        {
            keys.Add((FoldLogicKey, fold ? "1" : "0"));
        }

        if (NameKeys is { } nameKeys)
        {
            keys.Add((NameKeysKey, nameKeys));
        }

        if (keys.Count == 0)
        {
            return null;
        }

        using MemoryStream payload = new();
        Int(payload, Revision);
        Int(payload, keys.Count);
        foreach ((string key, string value) in keys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Text(payload, key);
            Text(payload, value);
        }

        byte[] body = payload.ToArray();
        byte[] bytes = new byte[HeaderBytes + body.Length];
        bytes[0] = CodecNone;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), body.Length);
        body.CopyTo(bytes, HeaderBytes);
        return new RoomPackSectionData(SectionTag, bytes);
    }

    /// <summary>Reads the settings back from their section's bytes.</summary>
    /// <param name="section">The section's bytes, as the pack holds them.</param>
    /// <returns>The settings; <see cref="None"/> for a revision this build does not read.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a length that disagrees with the
    /// bytes, a payload cut short or with bytes after its end, or a known key
    /// whose value is out of range.
    /// </exception>
    public static RoomLibraryOptions Read(ReadOnlySpan<byte> section)
    {
        if (section.Length < HeaderBytes)
        {
            throw Bad($"is {section.Length} bytes, shorter than its {HeaderBytes}-byte header");
        }

        if (section[0] != CodecNone)
        {
            throw Bad($"has codec {section[0]}; this build reads codec {CodecNone} (none)");
        }

        long length = BinaryPrimitives.ReadInt64BigEndian(section[1..]);
        ReadOnlySpan<byte> payload = section[HeaderBytes..];
        if (length != payload.Length)
        {
            throw Bad($"says {length} bytes of settings but holds {payload.Length}");
        }

        int at = 0;
        if (ReadInt(payload, ref at) != Revision)
        {
            return None;
        }

        int count = ReadInt(payload, ref at);
        if (count < 0)
        {
            throw Bad($"claims {count} settings");
        }

        int? reserve = null;
        bool? fold = null;
        string? nameKeys = null;
        for (int i = 0; i < count; i++)
        {
            string key = ReadText(payload, ref at);
            string value = ReadText(payload, ref at);
            if (string.Equals(key, EntityReserveKey, StringComparison.Ordinal))
            {
                reserve = TryParseReserve(value, out int parsed)
                    ? parsed
                    : throw Bad($"sets {EntityReserveKey} to \"{value}\", not a whole number from 0 to {EntityClassTable.EdictCap}");
            }
            else if (string.Equals(key, FoldLogicKey, StringComparison.Ordinal))
            {
                fold = TryParseFold(value, out bool parsed) ? parsed : throw Bad($"sets {FoldLogicKey} to \"{value}\", not 0 or 1");
            }
            else if (string.Equals(key, NameKeysKey, StringComparison.Ordinal))
            {
                nameKeys = NormalizeNameKeys(value);
            }
        }

        if (at != payload.Length)
        {
            throw Bad($"has {payload.Length - at} bytes after its last setting");
        }

        return new RoomLibraryOptions(reserve) { FoldLogic = fold, NameKeys = nameKeys };
    }

    private static LinkException Bad(string what) => new($"the room pack's {SectionTag} section {what}.");

    private static void Int(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        s.Write(b);
    }

    private static void Text(Stream s, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Int(s, bytes.Length);
        s.Write(bytes);
    }

    private static int ReadInt(ReadOnlySpan<byte> payload, ref int at)
    {
        if (payload.Length - at < 4)
        {
            throw Bad("is cut short");
        }

        int value = BinaryPrimitives.ReadInt32BigEndian(payload[at..]);
        at += 4;
        return value;
    }

    private static string ReadText(ReadOnlySpan<byte> payload, ref int at)
    {
        int length = ReadInt(payload, ref at);
        if (length < 0 || length > payload.Length - at)
        {
            throw Bad("is cut short");
        }

        string text = Encoding.UTF8.GetString(payload.Slice(at, length));
        at += length;
        return text;
    }
}
