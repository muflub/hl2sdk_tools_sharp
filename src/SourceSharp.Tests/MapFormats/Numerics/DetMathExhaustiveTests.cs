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
/// Every float in whole binades, for every function: the fast tier's error
/// bound measured, and every result checked to be correctly rounded.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bound.</b> The one claim in the library that is argued rather than
/// constructed is that each double-precision kernel stays within
/// <see cref="FastKernels.MaxRelativeError"/> (2^-48) of the true value. Here
/// it is measured against the platform's double-precision function, which is
/// within a couple of ulps (2^-51) of the truth: a kernel within 2^-49 of that
/// reference is within 2^-48 of the truth. The platform's double is fine as a
/// reference for a bound this loose even though its last bit is exactly what
/// the library exists to stop depending on.
/// </para>
/// <para>
/// <b>Correct rounding.</b> Every result is also checked to be the correctly
/// rounded float. The same double reference decides that wherever it lies
/// further from a float rounding boundary than its own error, which is all but
/// a few dozen floats in a binade; the exact tier decides those. The exact tier
/// itself is checked against an independent table in
/// <see cref="DetMathGoldenTests"/>. On glibc 2.39 the platform's own float
/// functions misround between 0.05% (powf) and 19% (atan2f) of these
/// arguments, which is the difference this library removes.
/// </para>
/// <para>
/// A binade is 2^23 floats. The rows are chosen where each function's
/// arithmetic changes: small and large reductions, both sides of the
/// breakpoints, and the exponents the compile actually uses for pow.
/// </para>
/// </remarks>
public class DetMathExhaustiveTests
{
    private const double ReferenceSlack = 1.0 / (1L << 49);

    public static TheoryData<string, float, float> Rows => new()
    {
        { "Sin", 0.5f, 0f }, { "Sin", 1f, 0f }, { "Sin", 2f, 0f }, { "Sin", 1024f, 0f }, { "Sin", 4194304f, 0f },
        { "Sin", 1.0f / 4096, 0f },
        { "Cos", 0.5f, 0f }, { "Cos", 1f, 0f }, { "Cos", 2f, 0f }, { "Cos", 1024f, 0f }, { "Cos", 4194304f, 0f },
        { "Cos", 1.0f / 4096, 0f },
        { "Tan", 0.5f, 0f }, { "Tan", 1f, 0f }, { "Tan", 2f, 0f }, { "Tan", 1024f, 0f },
        { "Asin", 0.5f, 0f }, { "Asin", 1.0f / 64, 0f }, { "Asin", 0.125f, 0f },
        { "Acos", 0.5f, 0f }, { "Acos", -0.5f, 0f }, { "Acos", 1.0f / 64, 0f },
        { "Atan2", 1f, 1f }, { "Atan2", 1f, 1.7f }, { "Atan2", 1f, -3.3f }, { "Atan2", 1f, 0.001f },
        { "Pow", 0.5f, 2.2f }, { "Pow", 0.5f, 1 / 2.2f }, { "Pow", 0.5f, 3.7f }, { "Pow", 0.5f, 50f },
        { "Pow", 0.5f, -1.3f }, { "Pow", 1f, 60f }, { "Pow", 64f, 0.37f },
        { "PowToSingle", 0.5f, 0f }, { "PowToSingle", 4f, 0f },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryFloatInTheBinadeIsCorrectlyRoundedAndTheKernelHoldsItsBound(string function, float binade, float other)
    {
        const int Chunks = 64;
        const int PerChunk = (1 << 23) / Chunks;
        int start = BitConverter.SingleToInt32Bits(binade);
        int step = binade < 0 ? -1 : 1;
        (double Worst, float At)[] worst = new (double, float)[Chunks];
        string?[] wrong = new string?[Chunks];

        Parallel.For(0, Chunks, chunk =>
        {
            for (int i = chunk * PerChunk; i < (chunk + 1) * PerChunk; i++)
            {
                float x = BitConverter.Int32BitsToSingle(start + (step * i));
                (double approx, double reference, float ours) = Evaluate(function, x, other);

                if (double.IsFinite(reference) && reference != 0 && double.IsFinite(approx))
                {
                    double error = Math.Abs(approx - reference) / Math.Abs(reference);
                    if (error > worst[chunk].Worst)
                    {
                        worst[chunk] = (error, x);
                    }
                }

                // The reference decides the correct rounding whenever it is
                // clear of a float rounding boundary by more than its own
                // error; the exact tier decides the rest.
                float expected = Decided(reference, out float fromReference)
                    ? fromReference
                    : Exact(function, x, other);
                if (BitConverter.SingleToInt32Bits(ours) != BitConverter.SingleToInt32Bits(expected))
                {
                    wrong[chunk] ??= string.Create(
                        CultureInfo.InvariantCulture, $"{function}({x:R}, {other:R}) = {ours:R}, correctly rounded {expected:R}");
                }
            }
        });

        string[] failures = [.. wrong.OfType<string>()];
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));

        (double max, float at) = worst.MaxBy(w => w.Worst);
        Assert.True(
            max <= ReferenceSlack,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{function} kernel error 2^{Math.Log2(max):F2} relative at {at:R}; allowed 2^-49"));
    }

    /// <summary>
    /// The float rounding of a double reference, when the reference is further
    /// than 2^-50 from every float rounding boundary (its own error is under
    /// 2^-51), so the true value rounds the same way.
    /// </summary>
    private static bool Decided(double reference, out float result)
    {
        double m = Math.Abs(reference) * (1.0 / (1L << 50));
        result = (float)reference;
        return double.IsNaN(reference)
            || BitConverter.SingleToInt32Bits((float)(reference - m)) == BitConverter.SingleToInt32Bits((float)(reference + m));
    }

    private static (double Approx, double Reference, float Ours) Evaluate(string function, float x, float other)
    {
        switch (function)
        {
            case "Sin":
            case "Cos":
            case "Tan":
            {
                double approx = double.NaN;
                if (FastKernels.ReduceHalfPi(x, out double rh, out double rl, out int q))
                {
                    approx = function switch
                    {
                        "Sin" => DetMathF.SinOfReduced(rh, rl, q),
                        "Cos" => DetMathF.CosOfReduced(rh, rl, q),
                        _ => (q & 1) == 0 ? FastKernels.Sin(rh, rl) / FastKernels.Cos(rh, rl) : -FastKernels.Cos(rh, rl) / FastKernels.Sin(rh, rl),
                    };
                }

                return function switch
                {
                    "Sin" => (approx, Math.Sin(x), DetMathF.Sin(x)),
                    "Cos" => (approx, Math.Cos(x), DetMathF.Cos(x)),
                    _ => (approx, Math.Tan(x), DetMathF.Tan(x)),
                };
            }

            case "Asin":
            {
                double a = x;
                double approx = FastKernels.Atan2(a, Math.Sqrt((1 - a) * (1 + a)));
                return (approx, Math.Asin(x), DetMathF.Asin(x));
            }

            case "Acos":
            {
                double a = x;
                double approx = FastKernels.Atan2(Math.Sqrt((1 - a) * (1 + a)), a);
                return (approx, Math.Acos(x), DetMathF.Acos(x));
            }

            case "Atan2":
                return (FastKernels.Atan2(x, other), Math.Atan2(x, other), DetMathF.Atan2(x, other));

            case "Pow":
            {
                (double zh, double zl) = FastKernels.LogTimes(x, other);
                return (FastKernels.Exp(zh, zl), Math.Pow(x, other), DetMathF.Pow(x, other));
            }

            default:
            {
                // FaceLightJob's shape: a float intensity over 256, to 1/2.2 in double.
                double b = x / 256.0;
                (double zh, double zl) = FastKernels.LogTimes(b, 1.0 / 2.2);
                return (FastKernels.Exp(zh, zl), Math.Pow(b, 1.0 / 2.2), DetMath.PowToSingle(b, 1.0 / 2.2));
            }
        }
    }

    private static float Exact(string function, float x, float other) => function switch
    {
        "Sin" => (float)ExactMath.Sin(x, RoundingTarget.Single),
        "Cos" => (float)ExactMath.Cos(x, RoundingTarget.Single),
        "Tan" => (float)ExactMath.Tan(x, RoundingTarget.Single),
        "Asin" => (float)ExactMath.Asin(x, RoundingTarget.Single),
        "Acos" => (float)ExactMath.Acos(x, RoundingTarget.Single),
        "Atan2" => (float)ExactMath.Atan2(x, other, RoundingTarget.Single),
        "Pow" => (float)ExactMath.Pow(x, other, RoundingTarget.Single),
        _ => (float)ExactMath.Pow(x / 256.0, 1.0 / 2.2, RoundingTarget.DoubleThenSingle),
    };
}
