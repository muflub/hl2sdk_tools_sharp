using System.Text;

namespace SourceSharp.MapFormats.Bsp.Structs;

/// <summary>
/// One key/value pair of an entity (<c>bspfile.h:1131</c>,
/// <c>struct epair_t</c>).
/// </summary>
/// <param name="Key">The key, with trailing whitespace stripped.</param>
/// <param name="Value">The value, with trailing whitespace stripped.</param>
/// <remarks>
/// A pair rather than a dictionary entry because keys REPEAT: an entity's
/// outputs are all spelled <c>OnTrigger</c>, and several brush entities carry
/// more than one <c>solid</c>. A dictionary would silently drop all but one.
/// </remarks>
public readonly record struct BspKeyValue(string Key, string Value);

/// <summary>
/// One entity from the entity lump.
/// </summary>
public sealed class BspEntity
{
    /// <summary>The entity's key/value pairs, in the order the file holds them.</summary>
    public List<BspKeyValue> Pairs { get; } = [];

    /// <summary>The first value for <paramref name="key"/>, or null.</summary>
    /// <param name="key">The key to look for, compared case-sensitively as the tools do.</param>
    /// <returns>The value, or null when the entity has no such key.</returns>
    public string? Get(string key)
    {
        foreach (BspKeyValue pair in Pairs)
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>The entity's <c>classname</c>, or an empty string.</summary>
    public string ClassName => Get("classname") ?? string.Empty;
}

/// <summary>
/// Reads and writes LUMP_ENTITIES, the NUL-terminated keyvalue text every
/// compiler stage starts from.
/// </summary>
public static class EntityLump
{
    /// <summary>Parses the entity text.</summary>
    /// <param name="lump">The lump's bytes.</param>
    /// <returns>The entities, in file order, each with its pairs in file order.</returns>
    /// <exception cref="InvalidBspException">The text ends inside an entity or a pair.</exception>
    /// <remarks>
    /// <para>
    /// Pairs come back in FILE order. Stock <c>bsplib</c> does not do that:
    /// <c>ParseEntity</c> builds the list by prepending
    /// (<c>bsplib.cpp:3056</c>, <c>e-&gt;next = mapent-&gt;epairs</c>) and
    /// <c>UnparseEntities</c> walks it forwards, so every vbsp/vrad round trip
    /// REVERSES the keys inside each entity. That is an artefact of a singly
    /// linked list, not a property of the format -- nothing reads the order --
    /// so this port keeps the order and does not reproduce the shuffle.
    /// </para>
    /// <para>
    /// The tokeniser follows <c>scriplib.cpp:604</c>'s <c>GetToken</c>:
    /// whitespace is anything <c>&lt;= 32</c>, a line comment starts at
    /// <c>;</c>, <c>#</c> or <c>//</c>, <c>/* */</c> nests nothing, a quoted
    /// token has NO escape sequences, and a bare token runs until whitespace
    /// or a semicolon.
    /// </para>
    /// </remarks>
    public static List<BspEntity> Parse(BspLumpData lump)
    {
        // The lump is NUL-terminated; the terminator is not part of the text.
        ReadOnlySpan<byte> bytes = lump.Data.Span;
        if (bytes.Length > 0 && bytes[^1] == 0)
        {
            bytes = bytes[..^1];
        }

        // Latin-1, not UTF-8: the tools treat the lump as bytes, and a
        // material or targetname with a high byte in it must survive a round
        // trip rather than becoming U+FFFD.
        string text = Encoding.Latin1.GetString(bytes);
        return ParseText(text);
    }

    /// <summary>Parses entity text that is already a string.</summary>
    /// <param name="text">The text, without a trailing NUL.</param>
    /// <returns>The entities, in file order.</returns>
    /// <exception cref="InvalidBspException">The text ends inside an entity or a pair.</exception>
    public static List<BspEntity> ParseText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<BspEntity> entities = [];
        int position = 0;

        while (NextToken(text, ref position) is { } open)
        {
            if (open != "{")
            {
                throw new InvalidBspException(
                    $"the entity lump has \"{open}\" where an entity's opening brace should be; "
                    + "bsplib.cpp:3041 calls this \"ParseEntity: { not found\"");
            }

            BspEntity entity = new();
            while (true)
            {
                string? key = NextToken(text, ref position)
                    ?? throw new InvalidBspException(
                        "the entity lump ends without a closing brace (bsplib.cpp:3052)");

                if (key == "}")
                {
                    break;
                }

                string value = NextToken(text, ref position)
                    ?? throw new InvalidBspException(
                        $"the entity lump ends after the key \"{key}\" with no value");

                entity.Pairs.Add(new BspKeyValue(StripTrailing(key), StripTrailing(value)));
            }

            entities.Add(entity);
        }

        return entities;
    }

    /// <summary>Writes entities back to lump bytes.</summary>
    /// <param name="entities">The entities to write.</param>
    /// <returns>The lump, NUL-terminated.</returns>
    /// <remarks>
    /// Reproduces <c>UnparseEntities</c> (<c>bsplib.cpp:3088</c>) exactly:
    /// <c>{\n</c>, then <c>"key" "value"\n</c> per pair with both ends
    /// stripped of trailing whitespace, then <c>}\n</c>, then a single
    /// trailing NUL over the whole lump. An entity with NO pairs is SKIPPED --
    /// <c>bsplib.cpp:3102</c> calls it "ent got removed" -- which is how vbsp
    /// deletes an entity without renumbering anything.
    /// </remarks>
    public static BspLumpData Write(IReadOnlyList<BspEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        StringBuilder builder = new();
        foreach (BspEntity entity in entities)
        {
            if (entity.Pairs.Count == 0)
            {
                continue;
            }

            builder.Append("{\n");
            foreach (BspKeyValue pair in entity.Pairs)
            {
                builder.Append('"').Append(StripTrailing(pair.Key)).Append("\" \"")
                    .Append(StripTrailing(pair.Value)).Append("\"\n");
            }

            builder.Append("}\n");
        }

        byte[] text = Encoding.Latin1.GetBytes(builder.ToString());
        byte[] bytes = new byte[text.Length + 1];
        text.CopyTo(bytes, 0);

        // bsplib.cpp:3122 -- the lump length INCLUDES the terminator.
        bytes[^1] = 0;
        return new BspLumpData(bytes, 0, 0);
    }

    /// <summary>
    /// Trailing whitespace removed, as <c>StripTrailing</c> does it.
    /// </summary>
    /// <param name="value">The text to strip.</param>
    /// <returns>The text without trailing characters of code 32 or below.</returns>
    /// <remarks>
    /// <c>bsplib.cpp:2986</c> walks back while <c>*s &lt;= 32</c>, so it strips
    /// every control character and not just spaces and tabs. Leading
    /// whitespace is NOT stripped, and a value that is entirely spaces becomes
    /// empty.
    /// </remarks>
    public static string StripTrailing(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int end = value.Length;
        while (end > 0 && value[end - 1] <= (char)32)
        {
            end--;
        }

        return end == value.Length ? value : value[..end];
    }

    private static string? NextToken(string text, ref int position)
    {
        while (true)
        {
            // scriplib.cpp:605 -- "skip space, ctrl chars": anything <= 32.
            while (position < text.Length && text[position] <= (char)32)
            {
                position++;
            }

            if (position >= text.Length)
            {
                return null;
            }

            char c = text[position];

            // scriplib.cpp:627 -- ';' and '#' are comments too, not just '//'.
            if (c == ';' || c == '#' ||
                (c == '/' && position + 1 < text.Length && text[position + 1] == '/'))
            {
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }

                continue;
            }

            // scriplib.cpp:643 -- block comments.
            if (c == '/' && position + 1 < text.Length && text[position + 1] == '*')
            {
                position += 2;
                while (position + 1 < text.Length &&
                       !(text[position] == '*' && text[position + 1] == '/'))
                {
                    position++;
                }

                position = Math.Min(position + 2, text.Length);
                continue;
            }

            if (c == '"')
            {
                // scriplib.cpp:664 -- a quoted token runs to the next quote,
                // with no escape handling whatsoever. A backslash in a
                // targetname is a literal backslash.
                position++;
                int start = position;
                while (position < text.Length && text[position] != '"')
                {
                    position++;
                }

                string quoted = text[start..position];
                if (position < text.Length)
                {
                    position++;
                }

                return quoted;
            }

            int bareStart = position;
            while (position < text.Length && text[position] > (char)32 && text[position] != ';')
            {
                position++;
            }

            return text[bareStart..position];
        }
    }
}
