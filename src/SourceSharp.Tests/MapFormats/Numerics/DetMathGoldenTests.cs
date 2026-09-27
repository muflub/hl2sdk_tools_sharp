//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Numerics;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Numerics;

/// <summary>
/// DetMath and DetMathF against a table of correctly rounded results computed
/// by an independent implementation (mpmath, at 3000 bits).
/// </summary>
/// <remarks>
/// <para>
/// These are the determinism facts. A correctly rounded result is unique, so
/// the table holds on every OS and CPU or the library is wrong; CI runs them
/// on Linux, Windows and macOS, x64 and arm64, and every one of those runs
/// compares bit patterns, not tolerances.
/// </para>
/// <para>
/// Each row is checked through the public entry point (which takes the fast
/// path when it can decide) AND through the exact tier directly, so a row
/// that the fast path happens to decide still tests the exact tier's answer.
/// </para>
/// </remarks>
public class DetMathGoldenTests
{
    public static TheoryData<string> Functions =>
    [
        "F1 Sin", "F1 Cos", "F1 Tan", "F1 Asin", "F1 Acos", "F2 Atan2", "F2 Pow",
        "D1 Sin", "D1 Cos", "D1 Log", "D2 Pow", "S1 SinToSingle", "S1 CosToSingle", "S2 PowToSingle",
    ];

    [Theory]
    [MemberData(nameof(Functions))]
    public void ThePublicFunctionMatchesTheTable(string function)
    {
        List<string> wrong = [];
        int rows = 0;
        foreach ((string kind, string name, ulong a, ulong b, ulong expected) in Select(function))
        {
            rows++;
            ulong actual = Public(kind, name, a, b);
            if (actual != expected)
            {
                wrong.Add(Describe(kind, name, a, b, expected, actual));
            }
        }

        Assert.True(rows > 10, $"{function}: only {rows} rows");
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public void TheExactTierMatchesTheTable(string function)
    {
        List<string> wrong = [];
        foreach ((string kind, string name, ulong a, ulong b, ulong expected) in Select(function))
        {
            ulong actual = Exact(kind, name, a, b);
            if (actual != expected)
            {
                wrong.Add(Describe(kind, name, a, b, expected, actual));
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    private static IEnumerable<(string Kind, string Function, ulong A, ulong B, ulong Expected)> Select(string function)
    {
        string[] parts = function.Split(' ');
        return DetMathGoldens.Rows.Where(r => r.Kind == parts[0] && r.Function == parts[1]);
    }

    private static float F(ulong bits) => BitConverter.Int32BitsToSingle((int)(uint)bits);

    private static double D(ulong bits) => BitConverter.Int64BitsToDouble((long)bits);

    private static ulong Bits(float f) => (uint)BitConverter.SingleToInt32Bits(f);

    private static ulong Bits(double d) => (ulong)BitConverter.DoubleToInt64Bits(d);

    private static ulong Public(string kind, string name, ulong a, ulong b) => (kind, name) switch
    {
        ("F1", "Sin") => Bits(DetMathF.Sin(F(a))),
        ("F1", "Cos") => Bits(DetMathF.Cos(F(a))),
        ("F1", "Tan") => Bits(DetMathF.Tan(F(a))),
        ("F1", "Asin") => Bits(DetMathF.Asin(F(a))),
        ("F1", "Acos") => Bits(DetMathF.Acos(F(a))),
        ("F2", "Atan2") => Bits(DetMathF.Atan2(F(a), F(b))),
        ("F2", "Pow") => Bits(DetMathF.Pow(F(a), F(b))),
        ("D1", "Sin") => Bits(DetMath.Sin(D(a))),
        ("D1", "Cos") => Bits(DetMath.Cos(D(a))),
        ("D1", "Log") => Bits(DetMath.Log(D(a))),
        ("D2", "Pow") => Bits(DetMath.Pow(D(a), D(b))),
        ("S1", "SinToSingle") => Bits(DetMath.SinToSingle(D(a))),
        ("S1", "CosToSingle") => Bits(DetMath.CosToSingle(D(a))),
        ("S2", "PowToSingle") => Bits(DetMath.PowToSingle(D(a), D(b))),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name),
    };

    private static ulong Exact(string kind, string name, ulong a, ulong b)
    {
        RoundingTarget target = kind[0] switch
        {
            'F' => RoundingTarget.Single,
            'D' => RoundingTarget.Double,
            _ => RoundingTarget.DoubleThenSingle,
        };

        double x = kind[0] == 'F' ? F(a) : D(a);
        double y = kind[0] == 'F' ? F(b) : D(b);
        double r = name switch
        {
            "Sin" or "SinToSingle" => ExactMath.Sin(x, target),
            "Cos" or "CosToSingle" => ExactMath.Cos(x, target),
            "Tan" => ExactMath.Tan(x, target),
            "Asin" => ExactMath.Asin(x, target),
            "Acos" => ExactMath.Acos(x, target),
            "Atan2" => ExactMath.Atan2(x, y, target),
            "Log" => ExactMath.Log(x, target),
            _ => Math.Sign(x) < 0
                ? (((long)y & 1) != 0 ? -1 : 1) * ExactMath.Pow(-x, y, target)
                : ExactMath.Pow(x, y, target),
        };

        return target == RoundingTarget.Double ? Bits(r) : Bits((float)r);
    }

    private static string Describe(string kind, string name, ulong a, ulong b, ulong expected, ulong actual) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{kind} {name}(0x{a:X}, 0x{b:X}): expected 0x{expected:X}, got 0x{actual:X}");
}
