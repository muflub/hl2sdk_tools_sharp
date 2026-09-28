//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Text;
using SourceSharp.Tests.MapFormats.Text.Legacy;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// The VMF number readers after the load-time work -- span overloads that read
/// in place, and a direct route for short decimals -- against the frozen
/// copies in <see cref="LegacyNumbers"/> and against
/// <see cref="double.Parse(string, NumberStyles, IFormatProvider)"/>. Every
/// answer is compared as bits, so a negative zero or a last-place difference
/// fails.
/// </summary>
public class NumberParsingEquivalenceTests
{
    /// <summary>Texts <c>atof</c> and <c>atoi</c> treat differently, one way or another.</summary>
    public static TheoryData<string> AwkwardNumbers() =>
    [
        "", " ", "0", "-0", "+0", "0.0", "-0.0", "1", "-1", "+1", "1.", ".5", "-.5", "+.5", ".", "-", "+", "-.",
        "1.5", "-1.5", "0.25", "128", "-1024", "16", "0.1", "0.3", "3.14159265358979", "2.718281828459045",
        "123456789012345", "1234567890123456", "12345678901234567890", "0.000000000000001",
        "0.0000000000000001", "999999999999999", "-999999999999999", "9007199254740993",
        "1e5", "1E5", "1e", "1e+", "1e-3", "-2.5e-3", "1.5e308", "1e309", "-1e309", "4.9e-324", "1e-400",
        "   42", "\t42", "\n42", "\r42", "\v42", "\f42", " \t\r\n\v\f-7.25",
        " 42", "42abc", "42 7", "7.5.3", "0x10", "inf", "nan", "--1", "+-1",
        "2147483647", "2147483648", "-2147483648", "-2147483649", "99999999999999999999",
        "00000000000000000001", "000.000", "1.0000000000000000000001",
        "0.1234567890123", "0.12345678901234", "0.123456789012345", "0.1234567890123456",
    ];

    [Theory]
    [MemberData(nameof(AwkwardNumbers))]
    public void AtofOverASpanMatchesTheOldAtof(string text)
    {
        double expected = LegacyNumbers.Atof(text);

        AssertSameBits(expected, CFormat.Atof(text));
        AssertSameBits(expected, CFormat.Atof(text.AsSpan()));
    }

    [Theory]
    [MemberData(nameof(AwkwardNumbers))]
    public void AtoiOverASpanMatchesTheOldAtoi(string text)
    {
        int expected = LegacyNumbers.Atoi(text);

        Assert.Equal(expected, CFormat.Atoi(text));
        Assert.Equal(expected, CFormat.Atoi(text.AsSpan()));
    }

    [Theory]
    [MemberData(nameof(AwkwardNumbers))]
    public void ParseFloatOverASpanMatchesTheOldParseFloat(string text)
    {
        float expected = LegacyNumbers.ParseFloat(text);

        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(VmfValue.ParseFloat(text)));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(expected),
            BitConverter.SingleToInt32Bits(VmfValue.ParseFloat(text.AsSpan())));
    }

    [Fact]
    public void ParseFloatOfASliceReadsOnlyTheSlice()
    {
        // The point of the span overload: "12" out of "[12 34]" without the
        // rest leaking in.
        string text = "[12 34]";

        Assert.Equal(12f, VmfValue.ParseFloat(text.AsSpan(1, 2)));
        Assert.Equal(34f, VmfValue.ParseFloat(text.AsSpan(4, 2)));
    }

    /// <summary>Vector texts the bracketed scan treats differently.</summary>
    public static TheoryData<string> AwkwardVectors() =>
    [
        "[1 0 0 0] 0.25", "[0 -1 0 -128] 0.25", "[1 2 3 4]", "[1 2 3", "[1 2 3 4", "[ 1 2 3 4 ]",
        "[1\t2\t3\t4]", " [1 2 3 4]", "[1 2 3]", "1 2 3 4", "[]", "[", "", "[1e2 -2.5e-1 .5 5.] 1",
        "[0.70710678118654 0.70710678118654 0 12.5] 0.5", "[a b c d]", "[1 2 3 x]", "[-0 -0 -0 -0] -0",
        "[ 1 2 3 4]", "[1 2 3 4] ", "[123456789012345678 1 1 1] 1",
    ];

    [Theory]
    [MemberData(nameof(AwkwardVectors))]
    public void TryParseVector4OverASpanMatchesTheOldScan(string text)
    {
        bool expected = LegacyNumbers.TryParseVector4(text, out (float X, float Y, float Z, float W) old);

        Assert.Equal(expected, VmfValue.TryParseVector4(text, out (float X, float Y, float Z, float W) byString));
        Assert.Equal(expected, VmfValue.TryParseVector4(text.AsSpan(), out (float X, float Y, float Z, float W) bySpan));
        AssertSameVector(old, byString);
        AssertSameVector(old, bySpan);
    }

    [Fact]
    public void TryParseVector4OfNullIsFalse()
    {
        Assert.False(VmfValue.TryParseVector4((string?)null, out (float X, float Y, float Z, float W) vector));
        Assert.Equal(default, vector);
    }

    [Fact]
    public void TheOtherVectorReadersStillAgreeWithTheOldScan()
    {
        // They share the rewritten scan: a point, a three-vector, a two-vector
        // and a colour, each against what the old scan made of the same text.
        Assert.True(VmfValue.TryParsePoint("(1.5 -2 3)", out var point));
        Assert.Equal(new SourceSharp.MapFormats.Geometry.Vec3(1.5f, -2f, 3f), point);
        Assert.False(VmfValue.TryParsePoint("[1 2 3]", out _));

        Assert.True(VmfValue.TryParseVector3("[0.25 0.5 1e1]", out var vector));
        Assert.Equal(new SourceSharp.MapFormats.Geometry.Vec3(0.25f, 0.5f, 10f), vector);

        Assert.True(VmfValue.TryParseVector2("[7 -8]", out var two));
        Assert.Equal((7f, -8f), two);
        Assert.False(VmfValue.TryParseVector2("[7]", out _));

        Assert.True(VmfValue.TryParseColour("255 128 0", out var colour));
        Assert.Equal(((byte)255, (byte)128, (byte)0), colour);
        Assert.False(VmfValue.TryParseColour("255 128", out _));
    }

    [Fact]
    public void TheShortDecimalRouteAgreesWithDoubleParseOnRandomDecimals()
    {
        // Every shape the direct route accepts -- up to fifteen digits, the
        // point anywhere or nowhere, either sign or none -- a few hundred
        // thousand times, against the general parser.
        Random random = new(0x5eed);
        StringBuilder text = new();

        for (int i = 0; i < 300_000; i++)
        {
            text.Clear();
            switch (random.Next(3))
            {
                case 0:
                    text.Append('-');
                    break;
                case 1:
                    text.Append('+');
                    break;
            }

            int digits = random.Next(1, 16);
            int point = random.Next(-1, digits + 1);
            for (int d = 0; d < digits; d++)
            {
                if (d == point)
                {
                    text.Append('.');
                }

                text.Append((char)('0' + random.Next(10)));
            }

            if (point == digits)
            {
                text.Append('.');
            }

            string number = text.ToString();
            Assert.True(CFormat.TryParseShortDecimal(number, out double fast), number);
            AssertSameBits(double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture), fast, number);
        }
    }

    [Fact]
    public void TheShortDecimalRouteAgreesWithDoubleParseOnTheValuesMapsUse()
    {
        // Coordinates on and off the grid, texture scales and shifts, and
        // normals written to many places: the numbers a VMF is made of.
        for (int whole = -4096; whole <= 4096; whole += 7)
        {
            for (int fraction = 0; fraction < 1000; fraction += 37)
            {
                string number = string.Create(CultureInfo.InvariantCulture, $"{whole}.{fraction:D3}");
                AssertShortRouteMatches(number);
            }
        }

        foreach (double value in new[] { 0.25, 0.5, 0.125, 0.0625, 0.707107, 0.70710678118654, 12345.678901 })
        {
            AssertShortRouteMatches(value.ToString("R", CultureInfo.InvariantCulture));
            AssertShortRouteMatches((-value).ToString("R", CultureInfo.InvariantCulture));
        }

        // Round-trip spellings with an exponent take the general route and
        // still agree.
        foreach (string number in new[] { "1E-06", "-2.5E-07", "1.7976931348623157E+308", "5E-324" })
        {
            Assert.False(CFormat.TryParseShortDecimal(number, out _));
            AssertSameBits(double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture), CFormat.ParseScannedNumber(number), number);
        }
    }

    [Theory]
    [InlineData("1e5")]
    [InlineData("1E5")]
    [InlineData("1234567890123456")]
    [InlineData("0.0000000000000001")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1.2.3")]
    [InlineData("12a")]
    [InlineData(" 1")]
    public void TheShortDecimalRouteDeclinesWhatItCannotDoExactly(string number)
    {
        // Sixteen digits may not fit 53 bits, an exponent is not a short
        // decimal, and anything else the scan would not have produced goes
        // to the general parser rather than being guessed at.
        Assert.False(CFormat.TryParseShortDecimal(number, out _));
    }

    [Fact]
    public void TheShortDecimalRouteSkipsCWhitespaceAndKeepsNegativeZero()
    {
        Assert.True(CFormat.TryParseShortDecimal(" \t\r\n\v\f-0", out double zero));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(zero));

        Assert.True(CFormat.TryParseShortDecimal("+.5", out double half));
        Assert.Equal(0.5, half);
    }

    [Fact]
    public void ParseScannedNumberFallsBackForAnExponent()
    {
        AssertSameBits(1.5e-3, CFormat.ParseScannedNumber("1.5e-3"));
        AssertSameBits(12345678901234567, CFormat.ParseScannedNumber("12345678901234567"));
    }

    private static void AssertShortRouteMatches(string number)
    {
        Assert.True(CFormat.TryParseShortDecimal(number, out double fast), number);
        AssertSameBits(double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture), fast, number);
    }

    private static void AssertSameBits(double expected, double actual, string? text = null) =>
        Assert.True(
            BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"\"{text}\": expected {expected:R} got {actual:R}");

    private static void AssertSameVector((float X, float Y, float Z, float W) expected, (float X, float Y, float Z, float W) actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.W), BitConverter.SingleToInt32Bits(actual.W));
    }
}
