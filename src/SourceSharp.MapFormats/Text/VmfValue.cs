//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The conversions a VMF value goes through: the <c>ReadKeyValueXxx</c> and
/// <c>WriteKeyValueXxx</c> static helpers of the reference chunk-file
/// serialiser.
/// </summary>
/// <remarks>
/// <para>
/// These are the whole type system of the format. A VMF stores only strings;
/// what makes <c>"origin" "0 0 64"</c> a point and
/// <c>"uaxis" "[1 0 0 0] 0.25"</c> a texture axis is which of these the
/// handler happened to call.
/// </para>
/// <para>
/// The two bracketings are NOT interchangeable and only the reference
/// serialiser says which is which: a POINT is parenthesised,
/// <c>(%f %f %f)</c> -- both read and written that way; a VECTOR is
/// bracketed, <c>[%f %f %f]</c>. The
/// cordon bounds in a <c>.vmm</c> use the POINT form while a displacement's
/// start position uses it too and a texture axis uses the vector form.
/// </para>
/// </remarks>
public static class VmfValue
{
    /// <summary>
    /// <c>ReadKeyValueBool</c>: <c>atoi(value) &gt; 0</c>.
    /// </summary>
    /// <param name="value">The value text.</param>
    /// <returns>True when the value parses to a positive integer.</returns>
    /// <remarks>
    /// STRICTLY greater than zero, so <c>"-1"</c> is FALSE -- which is not what
    /// a C programmer expects from a flag, and is why a VMF that stores
    /// <c>-1</c> for "on" reads as off.
    /// </remarks>
    public static bool ParseBool(string? value) => CFormat.Atoi(value ?? string.Empty) > 0;

    /// <summary>
    /// <c>ReadKeyValueInt</c>: <c>atoi</c>.
    /// </summary>
    /// <param name="value">The value text.</param>
    /// <returns>The value, or zero when it does not parse.</returns>
    /// <remarks>
    /// Cannot fail: <c>atoi</c> reports nothing and the reference reader
    /// returns <c>true</c> unconditionally, so <c>"banana"</c> is zero.
    /// </remarks>
    public static int ParseInt(string? value) => CFormat.Atoi(value ?? string.Empty);

    /// <summary>
    /// <c>ReadKeyValueFloat</c>: <c>(float)atof</c>.
    /// </summary>
    /// <param name="value">The value text.</param>
    /// <returns>The value, or zero when it does not parse.</returns>
    /// <remarks>
    /// Parsed at DOUBLE precision and then narrowed, exactly as written. It
    /// matters: <c>0.1</c> narrowed from the correctly rounded double is not
    /// always the same float as <c>0.1</c> parsed directly at single precision.
    /// </remarks>
    public static float ParseFloat(string? value) => (float)CFormat.Atof(value ?? string.Empty);

    /// <summary>
    /// <see cref="ParseFloat(string)"/> over a slice of a larger string: the
    /// same <c>atof</c>, narrowed to float the same way, with no substring.
    /// </summary>
    /// <param name="value">The characters to parse.</param>
    /// <returns>The value, or zero.</returns>
    public static float ParseFloat(ReadOnlySpan<char> value) => (float)CFormat.Atof(value);

    /// <summary>
    /// <c>ReadKeyValueColor</c>: three integers separated by whitespace.
    /// </summary>
    /// <param name="value">The value text.</param>
    /// <param name="colour">Receives the red, green and blue bytes.</param>
    /// <returns>True when all three parsed.</returns>
    /// <remarks>
    /// The reference scans into <c>int</c> and assigns to <c>unsigned char</c>,
    /// so a component outside 0..255 WRAPS rather than clamping. Reproduced.
    /// </remarks>
    public static bool TryParseColour(string? value, out (byte Red, byte Green, byte Blue) colour)
    {
        colour = default;

        Span<double> parts = stackalloc double[3];
        if (value is null || !TryScanNumbers(value, '\0', '\0', parts))
        {
            return false;
        }

        colour = ((byte)(int)parts[0], (byte)(int)parts[1], (byte)(int)parts[2]);
        return true;
    }

    /// <summary>
    /// <c>ReadKeyValuePoint</c>: <c>(%f %f %f)</c>, PARENTHESISED.
    /// </summary>
    /// <param name="value">The value text.</param>
    /// <param name="point">Receives the point.</param>
    /// <returns>True when all three parsed.</returns>
    public static bool TryParsePoint(string? value, out Vec3 point)
    {
        point = default;

        Span<double> parts = stackalloc double[3];
        if (value is null || !TryScanNumbers(value, '(', ')', parts))
        {
            return false;
        }

        point = new Vec3((float)parts[0], (float)parts[1], (float)parts[2]);
        return true;
    }

    /// <summary>
    /// <c>ReadKeyValueVector3</c>: <c>[%f %f %f]</c>, BRACKETED.
    /// </summary>
    /// <param name="value">The value text.</param>
    /// <param name="vector">Receives the vector.</param>
    /// <returns>True when all three parsed.</returns>
    public static bool TryParseVector3(string? value, out Vec3 vector)
    {
        vector = default;

        Span<double> parts = stackalloc double[3];
        if (value is null || !TryScanNumbers(value, '[', ']', parts))
        {
            return false;
        }

        vector = new Vec3((float)parts[0], (float)parts[1], (float)parts[2]);
        return true;
    }

    /// <summary>
    /// <c>ReadKeyValueVector2</c>: <c>[%f %f]</c>.
    /// </summary>
    /// <param name="value">The value text.</param>
    /// <param name="vector">Receives the two components.</param>
    /// <returns>True when both parsed.</returns>
    public static bool TryParseVector2(string? value, out (float X, float Y) vector)
    {
        vector = default;

        Span<double> parts = stackalloc double[2];
        if (value is null || !TryScanNumbers(value, '[', ']', parts))
        {
            return false;
        }

        vector = ((float)parts[0], (float)parts[1]);
        return true;
    }

    /// <summary>
    /// <c>ReadKeyValueVector4</c>: <c>[%f %f %f %f]</c>.
    /// </summary>
    /// <param name="value">The value text.</param>
    /// <param name="vector">Receives the four components.</param>
    /// <returns>True when all four parsed.</returns>
    public static bool TryParseVector4(
        string? value,
        out (float X, float Y, float Z, float W) vector)
    {
        vector = default;
        return value is not null && TryParseVector4(value.AsSpan(), out vector);
    }

    /// <summary>
    /// <see cref="TryParseVector4(string, out ValueTuple{float, float, float, float})"/>
    /// over a slice of a larger string.
    /// </summary>
    /// <param name="value">The characters to parse.</param>
    /// <param name="vector">The four components, when they parse.</param>
    /// <returns>True when all four components were read.</returns>
    /// <remarks>
    /// The loader reads both texture axes of every side through this. The
    /// string overload used to allocate the four-element result array and a
    /// substring per component; this one scans into a stack buffer and
    /// converts each component in place, with the same scan and the same
    /// rounding, so the two overloads agree on every input.
    /// </remarks>
    public static bool TryParseVector4(
        ReadOnlySpan<char> value,
        out (float X, float Y, float Z, float W) vector)
    {
        vector = default;

        Span<double> parts = stackalloc double[4];
        if (!TryScanNumbers(value, '[', ']', parts))
        {
            return false;
        }

        vector = ((float)parts[0], (float)parts[1], (float)parts[2], (float)parts[3]);
        return true;
    }

    /// <summary>
    /// <c>WriteKeyValueInt</c>'s <c>"%d"</c>.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The formatted text.</returns>
    public static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>WriteKeyValueFloat</c>'s <c>"%g"</c>.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The formatted text.</returns>
    /// <remarks>
    /// SIX significant digits, and the reference widens the value to
    /// <c>double</c> before the conversion. A
    /// float that needs nine digits to round-trip does NOT survive a Hammer
    /// save, which is a property of the format and not a defect of this port.
    /// </remarks>
    public static string FormatFloat(float value) => CFormat.FormatG(value);

    /// <summary>
    /// <c>WriteKeyValuePoint</c>'s <c>"(%g %g %g)"</c>.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns>The formatted text.</returns>
    public static string FormatPoint(Vec3 point) =>
        $"({CFormat.FormatG(point.X)} {CFormat.FormatG(point.Y)} {CFormat.FormatG(point.Z)})";

    /// <summary>
    /// <c>WriteKeyValueVector3</c>'s <c>"[%g %g %g]"</c>.
    /// </summary>
    /// <param name="vector">The vector.</param>
    /// <returns>The formatted text.</returns>
    public static string FormatVector3(Vec3 vector) =>
        $"[{CFormat.FormatG(vector.X)} {CFormat.FormatG(vector.Y)} {CFormat.FormatG(vector.Z)}]";

    /// <summary>
    /// <c>WriteKeyValueColor</c>'s <c>"%d %d %d"</c>.
    /// </summary>
    /// <param name="red">The red component.</param>
    /// <param name="green">The green component.</param>
    /// <param name="blue">The blue component.</param>
    /// <returns>The formatted text.</returns>
    public static string FormatColour(byte red, byte green, byte blue) =>
        string.Create(CultureInfo.InvariantCulture, $"{red} {green} {blue}");

    /// <summary>
    /// The scanning half of <c>sscanf(value, "&lt;open&gt;%f %f ...&lt;close&gt;")</c>.
    /// </summary>
    /// <remarks>
    /// <c>sscanf</c> rules that matter and are reproduced: whitespace in the
    /// format matches any run of whitespace INCLUDING NONE; a literal in the
    /// format must match exactly; and the return value counts assignments, so
    /// trailing literals that fail to match do NOT reduce it. That last one is
    /// why <c>"[1 2 3"</c> with no closing bracket still parses as a vector:
    /// three assignments happened before the <c>]</c> failed to match.
    /// </remarks>
    private static bool TryScanNumbers(
        ReadOnlySpan<char> text,
        char open,
        char close,
        Span<double> values)
    {
        int count = values.Length;
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

            values[i] = CFormat.ParseScannedNumber(text[start..end]);
            index = end;
        }

        // The closing literal is deliberately NOT required: see the remarks.
        _ = close;
        return true;
    }

    private static int SkipWhitespace(ReadOnlySpan<char> text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int ScanFloat(ReadOnlySpan<char> text, int index)
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
}
