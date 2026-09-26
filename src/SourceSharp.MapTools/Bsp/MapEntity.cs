//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// One key and one value on an entity: stock's <c>epair_t</c>,
/// </summary>
/// <remarks>
/// Mutable, because <c>SetKeyValue</c> replaces a value in place and leaves the
/// pair where it is in the list, which is what keeps the entity lump's key
/// Order stable across a rewrite.
/// </remarks>
public sealed class MapKeyValue
{
    /// <summary>Creates a pair.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public MapKeyValue(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Key = key;
        Value = value;
    }

    /// <summary>The key.</summary>
    public string Key { get; set; }

    /// <summary>The value.</summary>
    public string Value { get; set; }
}

/// <summary>
/// One entity as loaded from the VMF: <c>entity_t</c>,
/// </summary>
/// <remarks>
/// <para>
/// <b>Key order is output.</b> The entity lump is written by walking the pair
/// list, so where a key lands in it is in the BSP. Stock's list is
/// singly-linked and the two ways into it disagree on purpose:
/// <c>SetKeyValue</c> PREPENDS a key that is not already there
/// While the <c>connections</c> chunk APPENDS to
/// the tail and allows duplicates because an entity
/// may have many outputs with the same name. <see cref="Pairs"/> is that list
/// with index 0 as the head, so both spellings stay visible in the code that
/// uses them.
/// </para>
/// <para>
/// Blanking an entity is <c>numbrushes = 0; epairs = NULL;</c> in stock and
/// <see cref="Clear"/> here. The entity SLOT survives — nothing is ever removed
/// from <see cref="MapFile.Entities"/> — because entity numbers are referenced
/// by <see cref="MapBrush.EntityNumber"/> and by the areaportal and instance
/// bookkeeping.
/// </para>
/// </remarks>
public sealed class MapEntity
{
    /// <summary>
    /// The entity's key/value pairs, head first.
    /// </summary>
    public List<MapKeyValue> Pairs { get; } = [];

    /// <summary>The entity's origin, parsed from its <c>origin</c> key.</summary>
    public Vec3 Origin { get; set; }

    /// <summary>
    /// The point the entity flood actually started from, or null if this
    /// entity never got placed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the <c>origin</c> key.</b> <c>FloodEntities</c>
    /// Raises every origin by one unit "so objects on
    /// floor are ok", and an <c>info_player_start</c> that lands in solid is
    /// retried on a 3x3 grid of 16-unit offsets with the successful offset left
    /// applied. This is that point.
    /// </para>
    /// <para>
    /// Stock has no such field and re-reads the key
    /// instead, so its <c>.lin</c> ends at a point the flood never used. See
    /// <see cref="StockQuirk.LeakFileUnnudgedOrigin"/>.
    /// </para>
    /// </remarks>
    public Vec3? FloodOrigin { get; set; }

    /// <summary>
    /// The index of the entity's first brush in <see cref="MapFile.Brushes"/>.
    /// </summary>
    public int FirstBrush { get; set; }

    /// <summary>How many brushes the entity has.</summary>
    public int BrushCount { get; set; }

    /// <summary>
    /// The areaportal number stock assigns to a <c>func_areaportal*</c>, or
    /// zero.
    /// </summary>
    public int AreaPortalNumber { get; set; }

    /// <summary>The two areas an areaportal joins, or -1.</summary>
    /// <remarks>
    /// Stock leaves these zero after <c>memset</c> and the two lines that would
    /// have set them to -1 are commented out. The
    /// array is here so the portal lane has somewhere to put its answer; the
    /// zeroes are stock's.
    /// </remarks>
    public int[] PortalAreas { get; } = [0, 0];

    /// <summary>
    /// The value of a key, or the empty string: <c>ValueForKey</c>,
    /// </summary>
    /// <param name="key">The key to look for, matched case-insensitively.</param>
    /// <returns>The first matching value, or <see cref="string.Empty"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <remarks>
    /// The empty string rather than null is stock's, and callers depend on it:
    /// <c>*pMinDXLevelStr != '\0'</c> and
    /// <c>pInstanceFile[0]</c> are both reading index 0 of
    /// a string that may be the literal <c>""</c>.
    /// </remarks>
    public string ValueForKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach (MapKeyValue pair in Pairs)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return string.Empty;
    }

    /// <summary>Whether the entity has a key at all.</summary>
    /// <param name="key">The key to look for, matched case-insensitively.</param>
    /// <returns>True when a pair with that key exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <remarks>
    /// Distinct from a non-empty <see cref="ValueForKey"/>: a key present with
    /// an empty value is not the same as an absent key, and
    /// <c>FloatForKeyWithDefault</c> is the stock
    /// function that tells them apart.
    /// </remarks>
    public bool HasKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach (MapKeyValue pair in Pairs)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces a key's value, or prepends the pair: <c>SetKeyValue</c>,
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// Prepends, not appends. A key set here lands at the FRONT of the entity
    /// lump's pair list, so a run of <c>SetKeyValue</c> calls comes out in
    /// reverse. That is what stock does and the entity lump is compared byte
    /// for byte, so it is not a detail to tidy.
    /// </remarks>
    public void SetKeyValue(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        foreach (MapKeyValue pair in Pairs)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                pair.Value = value;
                return;
            }
        }

        Pairs.Insert(0, new MapKeyValue(key, value));
    }

    /// <summary>
    /// Appends a pair to the end of the list, duplicates allowed.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>The pair that was added.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// The <c>connections</c> chunk's insertion
    /// (<c>CMapFile::LoadConnectionsKeyCallback</c>). An
    /// entity's outputs are many pairs with the same key, so this must neither
    /// replace nor prepend.
    /// </remarks>
    public MapKeyValue AddKeyValue(string key, string value)
    {
        MapKeyValue pair = new(key, value);
        Pairs.Add(pair);
        return pair;
    }

    /// <summary>
    /// A key parsed as an integer: <c>IntForKey</c>.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value as C's <c>atol</c> reads it, or zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public int IntForKey(string key) => VmfValue.ParseInt(ValueForKey(key));

    /// <summary>
    /// A key parsed as a float: <c>FloatForKey</c>.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value as C's <c>atof</c> reads it, or zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public float FloatForKey(string key) => VmfValue.ParseFloat(ValueForKey(key));

    /// <summary>
    /// A key parsed as three numbers: <c>GetVectorForKey</c>,
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The vector, or the zero vector.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Stock reads into three <c>double</c>s with
    /// <c>sscanf(k, "%lf %lf %lf", ...)</c>, pre-zeroed, and assigns them to the
    /// <c>float</c> vector afterwards. Three consequences are reproduced here
    /// and are why this does not go through
    /// <see cref="VmfValue.TryParseVector3"/>: there are no brackets (that one
    /// is <c>ReadKeyValueVector3</c>'s <c>[%f %f %f]</c>, a different format);
    /// a value with fewer than three numbers keeps zeroes for the rest instead
    /// of failing; and <c>sscanf</c> stops at the FIRST field it cannot
    /// convert, so <c>"10 x 30"</c> is <c>(10, 0, 0)</c> and not
    /// <c>(10, 0, 30)</c>.
    /// </para>
    /// </remarks>
    public Vec3 GetVectorForKey(string key)
    {
        string value = ValueForKey(key);
        Span<double> parts = [0d, 0d, 0d];
        int position = 0;

        for (int i = 0; i < 3; i++)
        {
            if (!TryScanDouble(value, ref position, out parts[i]))
            {
                break;
            }
        }

        return new Vec3((float)parts[0], (float)parts[1], (float)parts[2]);
    }

    // strtod's prefix scan, which is what sscanf("%lf") is built on: skip
    // leading whitespace, take the longest prefix that looks like a C double,
    // and report where it stopped so the next field starts there.
    private static bool TryScanDouble(string text, ref int position, out double value)
    {
        value = 0d;

        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        int start = position;

        if (position < text.Length && (text[position] == '+' || text[position] == '-'))
        {
            position++;
        }

        int digits = 0;

        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            position++;
            digits++;
        }

        if (position < text.Length && text[position] == '.')
        {
            position++;

            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
                digits++;
            }
        }

        if (digits == 0)
        {
            position = start;
            return false;
        }

        int beforeExponent = position;

        if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
        {
            position++;

            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
            {
                position++;
            }

            if (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                while (position < text.Length && char.IsAsciiDigit(text[position]))
                {
                    position++;
                }
            }
            else
            {
                // "1e" is a double followed by junk, not a malformed double.
                position = beforeExponent;
            }
        }

        value = VmfValue.ParseFloat(text[start..position]);
        return true;
    }

    /// <summary>
    /// Blanks the entity: no brushes and no keys.
    /// </summary>
    /// <remarks>
    /// Stock's <c>mapent-&gt;numbrushes = 0; mapent-&gt;epairs = NULL;</c>. The
    /// entity is still counted and still occupies its number.
    /// </remarks>
    public void Clear()
    {
        BrushCount = 0;
        Pairs.Clear();
    }

    /// <summary>
    /// Sets a key to an integer, formatted the way stock's <c>sprintf</c> does.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public void SetKeyValue(string key, int value) =>
        SetKeyValue(key, value.ToString(CultureInfo.InvariantCulture));
}
