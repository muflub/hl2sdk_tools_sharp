//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// <see cref="FloatEstimate"/>: the hardware estimate stock's arithmetic starts
/// from, SSE's on x86 and ARM's on arm64.
/// </summary>
public class FloatEstimateTests
{
    /// <summary>x86's documented bound on <c>rcpss</c> and <c>rsqrtss</c>: 1.5 * 2^-12.</summary>
    private const double X86Bound = 1.5 / 4096;

    [Fact]
    public void TheFamilyNamesThisCpusInstructionSet()
    {
        string expected = Sse.IsSupported ? "sse" : AdvSimd.IsSupported ? "arm64" : "none";

        Assert.Equal(expected, FloatEstimate.Family);
        Assert.Equal(expected != "none", FloatEstimate.IsSupported);
    }

    [Fact]
    public void OnX86TheEstimatesAreSsesOwn()
    {
        if (!Sse.IsSupported)
        {
            return;
        }

        foreach (float a in Samples())
        {
            Vector128<float> v = Vector128.CreateScalar(a);
            Assert.Equal(Bits(Sse.ReciprocalScalar(v).ToScalar()), Bits(FloatEstimate.Reciprocal(a)));
            Assert.Equal(Bits(Sse.ReciprocalSqrtScalar(v).ToScalar()), Bits(FloatEstimate.ReciprocalSqrt(a)));
        }
    }

    [Fact]
    public void OnArm64TheEstimatesAreArmsRefinedOnce()
    {
        if (!AdvSimd.IsSupported)
        {
            return;
        }

        foreach (float a in Samples())
        {
            Vector128<float> v = Vector128.Create(a);
            Vector128<float> re = AdvSimd.ReciprocalEstimate(v);
            Vector128<float> rs = AdvSimd.ReciprocalSquareRootEstimate(v);
            float reciprocal = AdvSimd.Multiply(re, AdvSimd.ReciprocalStep(v, re)).ToScalar();
            float rsqrt = AdvSimd.Multiply(rs, AdvSimd.ReciprocalSquareRootStep(AdvSimd.Multiply(v, rs), rs)).ToScalar();

            Assert.Equal(Bits(reciprocal), Bits(FloatEstimate.Reciprocal(a)));
            Assert.Equal(Bits(rsqrt), Bits(FloatEstimate.ReciprocalSqrt(a)));
        }
    }

    [Fact]
    public void TheReciprocalIsAtLeastAsGoodAsX86Promises()
    {
        if (!FloatEstimate.IsSupported)
        {
            return;
        }

        foreach (float a in Samples())
        {
            double error = Math.Abs((FloatEstimate.Reciprocal(a) * (double)a) - 1.0);
            Assert.True(error <= X86Bound, $"1/{a}: relative error {error}");
        }
    }

    [Fact]
    public void TheReciprocalSqrtIsAtLeastAsGoodAsX86Promises()
    {
        if (!FloatEstimate.IsSupported)
        {
            return;
        }

        foreach (float a in Samples())
        {
            double error = Math.Abs((FloatEstimate.ReciprocalSqrt(a) * Math.Sqrt(a)) - 1.0);
            Assert.True(error <= X86Bound, $"1/sqrt({a}): relative error {error}");
        }
    }

    [Fact]
    public void ZerosAndInfinitiesGiveX86sAnswers()
    {
        if (!FloatEstimate.IsSupported)
        {
            return;
        }

        Assert.Equal(float.PositiveInfinity, FloatEstimate.Reciprocal(0f));
        Assert.Equal(float.NegativeInfinity, FloatEstimate.Reciprocal(-0f));
        Assert.Equal(Bits(0f), Bits(FloatEstimate.Reciprocal(float.PositiveInfinity)));
        Assert.Equal(float.PositiveInfinity, FloatEstimate.ReciprocalSqrt(0f));
        Assert.Equal(Bits(0f), Bits(FloatEstimate.ReciprocalSqrt(float.PositiveInfinity)));
    }

    [Fact]
    public void TheScalarAndFourLaneFormsAgree()
    {
        if (!FloatEstimate.IsSupported)
        {
            return;
        }

        float[] a = [0.3f, 1f, 7.5f, 1.0e6f];
        Vector128<float> v = Vector128.Create(a);
        Vector128<float> reciprocal = FloatEstimate.Reciprocal(v);
        Vector128<float> rsqrt = FloatEstimate.ReciprocalSqrt(v);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(Bits(FloatEstimate.Reciprocal(a[i])), Bits(reciprocal.GetElement(i)));
            Assert.Equal(Bits(FloatEstimate.ReciprocalSqrt(a[i])), Bits(rsqrt.GetElement(i)));
        }
    }

    [Fact]
    public void TheRefusalSaysWhatIsMissing()
    {
        string message = FloatEstimate.Unsupported().Message;

        Assert.Contains("SSE", message, StringComparison.Ordinal);
        Assert.Contains("AdvSimd", message, StringComparison.Ordinal);
    }

    /// <summary>Powers of two, their neighbours, and a seeded spread over many decades.</summary>
    private static IEnumerable<float> Samples()
    {
        for (int e = -60; e <= 60; e += 3)
        {
            float p = MathF.Pow(2f, e);
            yield return p;
            yield return BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(p) + 1);
            yield return BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(p) - 1);
        }

        Random random = new(20260926);
        for (int i = 0; i < 4000; i++)
        {
            yield return (float)Math.Pow(10, (random.NextDouble() * 24) - 12);
        }
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
