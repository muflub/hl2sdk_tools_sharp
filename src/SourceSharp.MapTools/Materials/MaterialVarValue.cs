using System.Globalization;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Materials;

/// <summary>
/// Turning a VMT's string value into the types <c>IMaterialVar</c> hands back.
/// </summary>
/// <remarks>
/// A VMT holds nothing but strings; the material system decides a var's type
/// when it parses the file, from the first character of the value. Only the
/// conversions <c>utilmatlib</c>'s callers need are here.
/// </remarks>
public static class MaterialVarValue
{
    /// <summary>
    /// <c>IMaterialVar::GetVecValue(out, 3)</c> for a value written in a VMT.
    /// </summary>
    /// <param name="value">The raw value, or null.</param>
    /// <param name="result">The three components.</param>
    /// <returns>True when the value parsed as a vector.</returns>
    /// <remarks>
    /// <para>
    /// Two bracketings, and they do not mean the same thing.
    /// <c>[1 .5 .25]</c> is three floats as written; <c>{255 128 64}</c> is
    /// three 0..255 values, each divided by 255 — the material system's own
    /// rule for a colour written in integer form. Getting that wrong turns a
    /// reflectivity of 1 into 255.
    /// </para>
    /// <para>
    /// A bare number with no brackets replicates across all three components,
    /// which is what <c>GetVecValue</c> does for a float var.
    /// </para>
    /// </remarks>
    public static bool TryParseVector(string? value, out Vec3 result)
    {
        result = Vec3.Zero;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> text = value.AsSpan().Trim();
        bool scaleByBytes = false;

        if (text.Length >= 2 && (text[0] == '[' || text[0] == '{'))
        {
            scaleByBytes = text[0] == '{';
            char close = scaleByBytes ? '}' : ']';

            text = text[1..];
            int end = text.IndexOf(close);
            if (end >= 0)
            {
                text = text[..end];
            }
        }

        Span<float> components = [0f, 0f, 0f];
        int count = 0;

        foreach (Range range in text.SplitAny(" \t"))
        {
            ReadOnlySpan<char> part = text[range];
            if (part.IsEmpty)
            {
                continue;
            }

            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float component))
            {
                return false;
            }

            if (count < 3)
            {
                components[count] = component;
            }

            count++;
        }

        if (count == 0)
        {
            return false;
        }

        if (count == 1)
        {
            // A float var read as a vector: the same value in all three.
            components[1] = components[0];
            components[2] = components[0];
        }

        if (scaleByBytes)
        {
            components[0] /= 255f;
            components[1] /= 255f;
            components[2] /= 255f;
        }

        result = new Vec3(components[0], components[1], components[2]);
        return true;
    }

    /// <summary>
    /// Whether a <c>MaterialVarFlags_t</c> flag written in a VMT is set.
    /// </summary>
    /// <param name="value">The raw value, or null.</param>
    /// <returns>True when the material system would set the flag.</returns>
    /// <remarks>
    /// <c>CMaterial::ParseMaterialFlag</c> reads the value as an INT and tests
    /// it against zero. This is NOT <c>StringIsTrue</c>: see
    /// <see cref="ToInt"/>.
    /// </remarks>
    public static bool IsFlagSet(string? value) => ToInt(value) != 0;

    /// <summary>
    /// <c>KeyValues::GetInt</c> on a VMT value, which is <c>atoi</c>.
    /// </summary>
    /// <param name="value">The raw value, or null.</param>
    /// <returns>The leading integer, or 0.</returns>
    /// <remarks>
    /// <c>atoi</c>, not a parse: it reads the leading integer and stops, so
    /// <c>"0.5"</c> is 0, <c>"1abc"</c> is 1, and <c>"true"</c> is 0 — which is
    /// why <c>"$translucent" "true"</c> does NOT make a material translucent
    /// while <c>"%compileSky" "true"</c> does make it a sky. The two are read
    /// through different code: flags through
    /// <c>CMaterial::ParseMaterialFlag</c>, compile vars through vbsp's own
    /// <c>StringIsTrue</c>.
    /// </remarks>
    public static int ToInt(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        ReadOnlySpan<char> text = value.AsSpan().TrimStart();
        int index = 0;

        if (index < text.Length && (text[index] == '-' || text[index] == '+'))
        {
            index++;
        }

        int start = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        return index > start &&
               int.TryParse(text[..index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    /// <summary>
    /// <c>atof</c> on a VMT value, as does it.
    /// </summary>
    /// <param name="value">The raw value, or null.</param>
    /// <param name="fallback">What an absent or unparseable value gives.</param>
    /// <returns>The number.</returns>
    /// <remarks>
    /// <c>atof</c> returns 0 for a string that is not a number rather than
    /// failing, so <paramref name="fallback"/> should be 0 to match it.
    /// </remarks>
    public static float ToFloat(string? value, float fallback = 0f) =>
        value is not null &&
        float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : fallback;
}
