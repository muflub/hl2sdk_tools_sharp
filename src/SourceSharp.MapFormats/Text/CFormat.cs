//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// C <c>printf</c> conversions, reproduced exactly, because several of the
/// map text formats are byte-defined by them.
/// </summary>
/// <remarks>
/// <para>
/// .NET's format strings are close to C's and nowhere identical. <c>"G6"</c>
/// is not <c>%g</c>: it spells the exponent <c>E+06</c> where C spells it
/// <c>e+06</c>, and it keeps a digit C would drop. <c>"F6"</c> is not <c>%f</c>
/// for infinities or for the negative zero that a chopped winding produces. So
/// the two conversions the map formats actually depend on are written out here
/// once, and a fact pins each against known C output.
/// </para>
/// <para>
/// Static methods only, and no state: the no-mutable-statics rule holds for the
/// whole assembly.
/// </para>
/// </remarks>
internal static class CFormat
{
    /// <summary>
    /// C's <c>%g</c> with the default precision of 6, as
    /// <c>CChunkFile::WriteKeyValueFloat</c> uses.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <param name="precision">
    /// The <c>%g</c> precision. Six is <c>printf</c>'s default and the only
    /// value any call site uses.
    /// </param>
    /// <returns>The formatted text.</returns>
    /// <remarks>
    /// C99 7.19.6.1: with precision P (6 here, and 1 if 0 were given), let X be
    /// the exponent the <c>%e</c> conversion would produce. If
    /// <c>P &gt; X &gt;= -4</c> the value is converted as <c>%f</c> with
    /// precision <c>P - 1 - X</c>; otherwise as <c>%e</c> with precision
    /// <c>P - 1</c>. Then, with no <c>#</c> flag, trailing zeros are removed
    /// from the fractional part and the decimal point goes with them if nothing
    /// follows it.
    /// </remarks>
    public static string FormatG(double value, int precision = 6)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        if (precision == 0)
        {
            precision = 1;
        }

        // The exponent %e would produce. Formatting once and reading it back is
        // the only way to get the exponent AFTER rounding to P significant
        // digits: 9.9999995 has exponent 0 before rounding and 1 after.
        string scientific = value.ToString(
            "E" + (precision - 1).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
        int exponentIndex = scientific.IndexOf('E', StringComparison.Ordinal);
        int exponent = int.Parse(
            scientific[(exponentIndex + 1)..],
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture);

        if (precision > exponent && exponent >= -4)
        {
            string fixedForm = value.ToString(
                "F" + Math.Max(0, precision - 1 - exponent).ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);
            return TrimTrailingZeros(fixedForm);
        }

        // %e form: mantissa, 'e', sign, at least two exponent digits.
        string mantissa = TrimTrailingZeros(scientific[..exponentIndex]);
        string sign = exponent < 0 ? "-" : "+";
        string digits = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        if (digits.Length < 2)
        {
            digits = "0" + digits;
        }

        return mantissa + "e" + sign + digits;
    }

    /// <summary>
    /// C's <c>%f</c> with the default precision of 6, as the portal writer's
    /// <c>WriteFloat</c> uses.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The formatted text, always with six decimal places.</returns>
    public static string FormatF6(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        return value.ToString("F6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>atof</c>: parse as much of a leading number as possible, and yield
    /// zero when there is none.
    /// </summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The value, or zero.</returns>
    /// <remarks>
    /// Never throws, and never reports failure, because <c>atof</c> does
    /// neither -- the reference <c>CChunkFile::ReadKeyValueFloat</c> returns
    /// <c>true</c> unconditionally, so a
    /// VMF key whose value is not a number silently becomes 0.
    /// </remarks>
    public static double Atof(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Atof(text.AsSpan());
    }

    /// <summary>
    /// <c>atof</c> over a slice of a larger string: the same conversion as
    /// <see cref="Atof(string)"/>, without first cutting the slice out.
    /// </summary>
    /// <param name="text">The characters to parse.</param>
    /// <returns>The value, or zero.</returns>
    /// <remarks>
    /// The VMF loader reads nine coordinates out of every side's <c>plane</c>
    /// and eight numbers out of its two texture axes. Cutting each one out as
    /// a string before converting it was a substring allocation per number,
    /// hundreds of thousands per map, for text that is discarded the moment
    /// it is converted. The conversion itself is unchanged: the same prefix
    /// scan picks the same characters, and <see cref="double.Parse(ReadOnlySpan{char}, NumberStyles, IFormatProvider)"/>
    /// rounds a span exactly as it rounds the equal string.
    /// </remarks>
    public static double Atof(ReadOnlySpan<char> text)
    {
        int end = ScanNumberEnd(text, allowFraction: true);
        return end == 0 ? 0.0 : ParseScannedNumber(text[..end]);
    }

    /// <summary>
    /// Converts text a number scan has already accepted -- optional leading C
    /// whitespace, then a C floating-point literal -- to the nearest double,
    /// exactly as <see cref="double.Parse(ReadOnlySpan{char}, NumberStyles, IFormatProvider)"/>
    /// with <see cref="NumberStyles.Float"/> and the invariant culture does.
    /// </summary>
    /// <param name="number">The accepted text.</param>
    /// <returns>The correctly rounded value.</returns>
    /// <remarks>
    /// <para>
    /// A VMF is hundreds of thousands of short decimals -- <c>-1024</c>,
    /// <c>0.25</c>, <c>128.5</c> -- and the general parser's set-up per call
    /// (style checks, the format provider, its digit buffer) cost more than
    /// the conversion. The short ones take a direct route instead: at most
    /// fifteen digits, no exponent. Fifteen decimal digits always fit in a
    /// double's 53-bit significand exactly, and so does every power of ten up
    /// to 10^22, so the value is one exact integer divided by one exact power
    /// of ten -- a single IEEE division, which rounds correctly by
    /// definition. The general parser rounds correctly too, and the correctly
    /// rounded result is unique, so the two cannot differ. Anything else --
    /// more digits, an exponent, anything unexpected -- goes to the general
    /// parser as before.
    /// </para>
    /// <para>
    /// A negative zero stays negative, as the general parser keeps it.
    /// </para>
    /// </remarks>
    internal static double ParseScannedNumber(ReadOnlySpan<char> number)
    {
        return TryParseShortDecimal(number, out double value)
            ? value
            : double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // The direct route of ParseScannedNumber, or false to send the text to
    // the general parser.
    internal static bool TryParseShortDecimal(ReadOnlySpan<char> number, out double value)
    {
        value = 0.0;
        int index = 0;

        // The leading whitespace atoi/atof skip, all of which NumberStyles.Float
        // also allows.
        while (index < number.Length && number[index] is ' ' or '\t' or '\n' or '\r' or '\v' or '\f')
        {
            index++;
        }

        bool negative = false;
        if (index < number.Length && number[index] is '+' or '-')
        {
            negative = number[index] == '-';
            index++;
        }

        long mantissa = 0;
        int digits = 0;
        int fractionDigits = 0;
        bool seenPoint = false;

        for (; index < number.Length; index++)
        {
            char c = number[index];
            if (char.IsAsciiDigit(c))
            {
                if (++digits > MaxShortDecimalDigits)
                {
                    return false;
                }

                mantissa = (mantissa * 10) + (c - '0');
                if (seenPoint)
                {
                    fractionDigits++;
                }
            }
            else if (c == '.' && !seenPoint)
            {
                seenPoint = true;
            }
            else
            {
                // An exponent, or anything the scan should not have let in.
                return false;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        double magnitude = mantissa;
        if (fractionDigits > 0)
        {
            magnitude /= PowersOfTen[fractionDigits];
        }

        value = negative ? -magnitude : magnitude;
        return true;
    }

    // Fifteen digits: 10^15 - 1 is below 2^53, so every such integer is a
    // double exactly.
    private const int MaxShortDecimalDigits = 15;

    // 10^0 .. 10^15, each exactly representable (every power of ten to 10^22 is).
    private static ReadOnlySpan<double> PowersOfTen =>
    [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7,
        1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
    ];

    /// <summary>
    /// <c>atoi</c>: parse a leading base-10 integer, and yield zero when there
    /// is none.
    /// </summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The value, or zero.</returns>
    public static int Atoi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Atoi(text.AsSpan());
    }

    /// <summary>
    /// <c>atoi</c> over a slice of a larger string, for the same reason
    /// <see cref="Atof(ReadOnlySpan{char})"/> exists: no substring per number.
    /// </summary>
    /// <param name="text">The characters to parse.</param>
    /// <returns>The value, or zero.</returns>
    public static int Atoi(ReadOnlySpan<char> text)
    {
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

    private static int ScanNumberEnd(ReadOnlySpan<char> text, bool allowFraction)
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

    private static string TrimTrailingZeros(string text)
    {
        if (!text.Contains('.', StringComparison.Ordinal))
        {
            return text;
        }

        text = text.TrimEnd('0');
        return text.EndsWith('.') ? text[..^1] : text;
    }
}
