//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.Tests.MapFormats.Text.Legacy;

/// <summary>
/// The number readers the VMF load used before the load-time performance
/// work -- <c>atof</c>, <c>atoi</c>, the bracketed vector scan and the side's
/// <c>plane</c> and texture-axis readers -- frozen, so facts can demand the
/// rewritten ones give the same bits for the same text.
/// </summary>
/// <remarks>
/// Verbatim apart from being gathered into one class and losing their doc
/// comments. Do not change them: their value is that they do not.
/// </remarks>
internal static class LegacyNumbers
{
    public static double Atof(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int end = ScanNumberEnd(text, allowFraction: true);
        return end == 0
            ? 0.0
            : double.Parse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static int Atoi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int end = ScanNumberEnd(text, allowFraction: false);
        if (end == 0)
        {
            return 0;
        }

        // C's atoi on overflow is undefined; glibc saturates through strtol.
        return long.TryParse(text[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? (int)Math.Clamp(value, int.MinValue, int.MaxValue)
            : 0;
    }

    private static int ScanNumberEnd(string text, bool allowFraction)
    {
        int index = 0;

        while (index < text.Length && (text[index] == ' ' || text[index] == '\t' ||
                                        text[index] == '\n' || text[index] == '\r' ||
                                        text[index] == '\v' || text[index] == '\f'))
        {
            index++;
        }

        int start = index;
        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            index++;
        }

        int integerDigits = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
            integerDigits++;
        }

        int fractionDigits = 0;
        if (allowFraction && index < text.Length && text[index] == '.')
        {
            int afterDot = index + 1;
            while (afterDot < text.Length && char.IsAsciiDigit(text[afterDot]))
            {
                afterDot++;
                fractionDigits++;
            }

            // A lone '.' is part of the number only when digits sit on one side
            // of it: "1." and ".5" convert, "." does not.
            if (integerDigits > 0 || fractionDigits > 0)
            {
                index = afterDot;
            }
        }

        if (integerDigits == 0 && fractionDigits == 0)
        {
            // No digits at all: no conversion is performed, and the result is
            // zero. The leading sign does not count as progress.
            _ = start;
            return 0;
        }

        if (allowFraction && index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            int afterExponent = index + 1;
            if (afterExponent < text.Length &&
                (text[afterExponent] == '+' || text[afterExponent] == '-'))
            {
                afterExponent++;
            }

            int exponentDigitStart = afterExponent;
            while (afterExponent < text.Length && char.IsAsciiDigit(text[afterExponent]))
            {
                afterExponent++;
            }

            // An 'e' with no digits after it is not part of the number.
            if (afterExponent > exponentDigitStart)
            {
                index = afterExponent;
            }
        }

        return index;
    }

    public static float ParseFloat(string? value) => (float)Atof(value ?? string.Empty);

    public static int ParseInt(string? value) => Atoi(value ?? string.Empty);

    public static bool TryParseVector4(
        string? value,
        out (float X, float Y, float Z, float W) vector)
    {
        vector = default;

        if (value is null || !TryScanNumbers(value, '[', ']', 4, out double[] parts))
        {
            return false;
        }

        vector = ((float)parts[0], (float)parts[1], (float)parts[2], (float)parts[3]);
        return true;
    }

    private static bool TryScanNumbers(
        string text,
        char open,
        char close,
        int count,
        out double[] values)
    {
        values = new double[count];
        int index = 0;

        if (open != '\0')
        {
            index = SkipWhitespace(text, index);
            if (index >= text.Length || text[index] != open)
            {
                return false;
            }

            index++;
        }

        for (int i = 0; i < count; i++)
        {
            index = SkipWhitespace(text, index);

            int start = index;
            int end = ScanFloat(text, index);
            if (end == start)
            {
                return false;
            }

            values[i] = double.Parse(
                text[start..end], NumberStyles.Float, CultureInfo.InvariantCulture);
            index = end;
        }

        // The closing literal is deliberately NOT required: see the remarks.
        _ = close;
        return true;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int ScanFloat(string text, int index)
    {
        int start = index;

        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            index++;
        }

        int digits = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
            digits++;
        }

        if (index < text.Length && text[index] == '.')
        {
            int afterDot = index + 1;
            int fractionDigits = 0;
            while (afterDot < text.Length && char.IsAsciiDigit(text[afterDot]))
            {
                afterDot++;
                fractionDigits++;
            }

            if (digits > 0 || fractionDigits > 0)
            {
                index = afterDot;
                digits += fractionDigits;
            }
        }

        if (digits == 0)
        {
            return start;
        }

        if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            int afterExponent = index + 1;
            if (afterExponent < text.Length &&
                (text[afterExponent] == '+' || text[afterExponent] == '-'))
            {
                afterExponent++;
            }

            int exponentStart = afterExponent;
            while (afterExponent < text.Length && char.IsAsciiDigit(text[afterExponent]))
            {
                afterExponent++;
            }

            if (afterExponent > exponentStart)
            {
                index = afterExponent;
            }
        }

        return index;
    }

    public static bool TryParsePlanePoints(string value, Vec3[] points)
    {
        int position = 0;

        for (int i = 0; i < 3; i++)
        {
            int open = value.IndexOf('(', position);
            int close = open < 0 ? -1 : value.IndexOf(')', open);

            if (open < 0 || close < 0)
            {
                return false;
            }

            string[] parts = value[(open + 1)..close]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 3)
            {
                return false;
            }

            points[i] = new Vec3(
                ParseFloat(parts[0]),
                ParseFloat(parts[1]),
                ParseFloat(parts[2]));

            position = close + 1;
        }

        return true;
    }

    public static bool TryParseAxis(string value, out Vec3 axis, out float shift, out float scale)
    {
        axis = default;
        shift = 0f;
        scale = 0f;

        if (!TryParseVector4(value, out (float X, float Y, float Z, float W) vector))
        {
            return false;
        }

        int close = value.LastIndexOf(']');
        if (close < 0)
        {
            return false;
        }

        string tail = value[(close + 1)..].Trim();
        if (tail.Length == 0)
        {
            return false;
        }

        axis = new Vec3(vector.X, vector.Y, vector.Z);
        shift = vector.W;
        scale = ParseFloat(tail);
        return true;
    }
}
