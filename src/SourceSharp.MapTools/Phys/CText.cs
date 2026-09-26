//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// The two C text conversions the collision code needs: <c>atof</c> for the
/// surface-property scripts and <c>printf("%f")</c> / <c>"%d"</c> for the
/// keydata. (MapFormats' <c>CFormat</c> has the same pair but is internal to
/// that assembly.)
/// </summary>
internal static class CText
{
    /// <summary><c>printf("%f")</c>: six decimals of the value promoted to double, correctly rounded.</summary>
    /// <param name="value">The float, promoted as a C variadic argument is.</param>
    /// <returns>The text.</returns>
    public static string F6(float value)
    {
        double d = value;
        if (double.IsNaN(d))
        {
            return "nan";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "inf" : "-inf";
        }

        return d.ToString("F6", CultureInfo.InvariantCulture);
    }

    /// <summary><c>printf("%d")</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string D(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>atof</c>: the longest leading number, or zero.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    public static double Atof(string text)
    {
        int index = 0;
        while (index < text.Length && text[index] is ' ' or '\t' or '\n' or '\r' or '\v' or '\f')
        {
            index++;
        }

        int start = index;
        if (index < text.Length && text[index] is '+' or '-')
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
        if (index < text.Length && text[index] == '.')
        {
            int afterDot = index + 1;
            while (afterDot < text.Length && char.IsAsciiDigit(text[afterDot]))
            {
                afterDot++;
                fractionDigits++;
            }

            if (integerDigits > 0 || fractionDigits > 0)
            {
                index = afterDot;
            }
        }

        if (integerDigits == 0 && fractionDigits == 0)
        {
            return 0.0;
        }

        if (index < text.Length && text[index] is 'e' or 'E')
        {
            int after = index + 1;
            if (after < text.Length && text[after] is '+' or '-')
            {
                after++;
            }

            int digits = after;
            while (after < text.Length && char.IsAsciiDigit(text[after]))
            {
                after++;
            }

            if (after > digits)
            {
                index = after;
            }
        }

        return double.Parse(text[start..index], NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
