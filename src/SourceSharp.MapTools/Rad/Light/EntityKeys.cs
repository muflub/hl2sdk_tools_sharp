using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>ValueForKey</c> and its numeric siblings, as defines
/// them for the compiled-map entity list vrad reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the same functions as <c>MapEntity</c>'s.</b> That class models
/// vbsp's <c>.map</c> entity and matches keys CASE-INSENSITIVELY, because
/// <c>map_shared</c> does. the reference implementation's <c>ValueForKey</c> uses
/// <c>strcmp</c> and is case-SENSITIVE, and vrad reads only through that one.
/// Two different functions with the same name in two files is a trap worth one
/// separate type.
/// </para>
/// <para>
/// <b>The pair list is searched BACKWARDS.</b> <c>ParseEntity</c>
/// PREPENDS each pair, so stock's list is in
/// reverse file order and <c>ValueForKey</c>'s "first match" is the LAST
/// occurrence in the file. <see cref="BspEntity.Pairs"/> is in file order, so
/// matching stock means walking it from the end. That only shows on an entity
/// with a duplicate key -- which Hammer does emit, and which is exactly the
/// case where getting it wrong is invisible until a light lands in the wrong
/// place.
/// </para>
/// </remarks>
public static class EntityKeys
{
    /// <summary>
    /// <c>ValueForKey</c>.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="key">The key, matched case-sensitively.</param>
    /// <returns>The value, or the empty string when absent.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static string ValueForKey(BspEntity entity, string key) =>
        ValueForKeyOrNull(entity, key) ?? string.Empty;

    /// <summary>
    /// <c>ValueForKeyWithDefault</c>, which returns
    /// null rather than the empty string so that "present but empty" is
    /// distinguishable.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null when the key is absent.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// The distinction is load-bearing exactly once: <c>SunSpreadAngle</c> on
    /// a <c>light_environment</c>. A present-but-empty key sets
    /// <c>g_SunAngularExtent</c> to <c>atof("")</c> = 0 and PRINTS, where an
    /// absent one leaves the previous value; the two are otherwise the same
    /// number.
    /// </remarks>
    public static string? ValueForKeyWithDefault(BspEntity entity, string key) =>
        ValueForKeyOrNull(entity, key);

    /// <summary>
    /// <c>FloatForKey</c>.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value as <c>atof</c> reads it, or zero.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static float FloatForKey(BspEntity entity, string key) =>
        VmfValue.ParseFloat(ValueForKey(entity, key));

    /// <summary>
    /// <c>FloatForKeyWithDefault</c>.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="key">The key.</param>
    /// <param name="fallback">What to return when the key is absent.</param>
    /// <returns>The parsed value, or <paramref name="fallback"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="key"/> is null.
    /// </exception>
    public static float FloatForKeyWithDefault(BspEntity entity, string key, float fallback)
    {
        string? value = ValueForKeyOrNull(entity, key);
        return value is null ? fallback : VmfValue.ParseFloat(value);
    }

    /// <summary>
    /// <c>IntForKey</c>.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value as <c>atoi</c> reads it, or zero.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static int IntForKey(BspEntity entity, string key) =>
        VmfValue.ParseInt(ValueForKey(entity, key));

    /// <summary>
    /// <c>GetVectorForKey</c>.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="key">The key.</param>
    /// <returns>Three numbers, zero-filled where the value ran out.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <c>sscanf(k, "%lf %lf %lf", &amp;v1, &amp;v2, &amp;v3)</c> over
    /// pre-zeroed doubles, narrowed to float on assignment. Three consequences
    /// follow and all three are reproduced: no brackets are accepted; a short
    /// value keeps zeroes; and the scan STOPS at the first unconvertible field,
    /// so <c>"10 x 30"</c> is <c>(10, 0, 0)</c>.
    /// </remarks>
    public static Vec3 GetVectorForKey(BspEntity entity, string key)
    {
        string value = ValueForKey(entity, key);
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

    private static string? ValueForKeyOrNull(BspEntity entity, string key)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(key);

        List<BspKeyValue> pairs = entity.Pairs;
        for (int i = pairs.Count - 1; i >= 0; i--)
        {
            if (string.Equals(pairs[i].Key, key, StringComparison.Ordinal))
            {
                return pairs[i].Value;
            }
        }

        return null;
    }

    // strtod's prefix scan, which is what sscanf("%lf") is built on: skip
    // leading whitespace, take the longest prefix that parses as a C double,
    // and leave `position` where it stopped so the next field starts there.
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

            int exponentDigits = 0;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
                exponentDigits++;
            }

            if (exponentDigits == 0)
            {
                position = beforeExponent;
            }
        }

        return double.TryParse(
            text.AsSpan(start, position - start),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }
}
