//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="SseLanes"/>: the x86 lane rules the KD tracer relies on, in a
/// form that gives x86's answer on any CPU.
/// </summary>
public class SseLanesTests
{
    /// <summary>The awkward operands: signed zeros, NaN, infinities, and ordinary values.</summary>
    private static readonly float[] Awkward =
        [0f, -0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1f, -1f, 3.5f];

    [Fact]
    public void MaxKeepsTheSecondOperandUnlessTheFirstIsGreater()
    {
        Vector128<float> a = Vector128.Create(float.NaN, 1f, 0f, -0f);
        Vector128<float> b = Vector128.Create(1f, float.NaN, -0f, 0f);

        Assert.Equal(Lanes(Vector128.Create(1f, float.NaN, -0f, 0f)), Lanes(SseLanes.MaxPortable(a, b)));
    }

    [Fact]
    public void MinKeepsTheSecondOperandUnlessTheFirstIsLess()
    {
        Vector128<float> a = Vector128.Create(float.NaN, 1f, 0f, -0f);
        Vector128<float> b = Vector128.Create(1f, float.NaN, -0f, 0f);

        Assert.Equal(Lanes(Vector128.Create(1f, float.NaN, -0f, 0f)), Lanes(SseLanes.MinPortable(a, b)));
    }

    [Fact]
    public void ThePortableMaxAndMinMatchSseOnEveryAwkwardPair()
    {
        if (!Sse.IsSupported)
        {
            return;
        }

        foreach (float x in Awkward)
        {
            foreach (float y in Awkward)
            {
                Vector128<float> a = Vector128.Create(x);
                Vector128<float> b = Vector128.Create(y);
                Assert.Equal(Lanes(Sse.Max(a, b)), Lanes(SseLanes.MaxPortable(a, b)));
                Assert.Equal(Lanes(Sse.Min(a, b)), Lanes(SseLanes.MinPortable(a, b)));
            }
        }
    }

    [Fact]
    public void AndNotInvertsTheFirstOperandAsSseDoes()
    {
        Vector128<float> mask = Vector128.Create(-1, 0, -1, 0).AsSingle();
        Vector128<float> b = Vector128.Create(1f, 2f, 3f, 4f);

        Assert.Equal(Lanes(Vector128.Create(0f, 2f, 0f, 4f)), Lanes(SseLanes.AndNot(mask, b)));
        if (Sse.IsSupported)
        {
            Assert.Equal(Lanes(Sse.AndNot(mask, b)), Lanes(SseLanes.AndNot(mask, b)));
        }
    }

    [Fact]
    public void MoveMaskReadsTheSignBitsLaneZeroLowest()
    {
        Assert.Equal(0b1010, SseLanes.MoveMask(Vector128.Create(1f, -1f, 0f, -0f)));
    }

    [Fact]
    public void TransposeTurnsRowsIntoColumns()
    {
        Vector128<float> r0 = Vector128.Create(0f, 1f, 2f, 3f);
        Vector128<float> r1 = Vector128.Create(4f, 5f, 6f, 7f);
        Vector128<float> r2 = Vector128.Create(8f, 9f, 10f, 11f);
        Vector128<float> r3 = Vector128.Create(12f, 13f, 14f, 15f);

        SseLanes.TransposePortable(r0, r1, r2, r3, out var c0, out var c1, out var c2, out var c3);
        Assert.Equal(Lanes([0f, 4f, 8f, 12f]), Lanes(c0));
        Assert.Equal(Lanes([1f, 5f, 9f, 13f]), Lanes(c1));
        Assert.Equal(Lanes([2f, 6f, 10f, 14f]), Lanes(c2));
        Assert.Equal(Lanes([3f, 7f, 11f, 15f]), Lanes(c3));

        SseLanes.Transpose(r0, r1, r2, r3, out var d0, out var d1, out var d2, out var d3);
        Assert.Equal(Lanes(c0), Lanes(d0));
        Assert.Equal(Lanes(c1), Lanes(d1));
        Assert.Equal(Lanes(c2), Lanes(d2));
        Assert.Equal(Lanes(c3), Lanes(d3));
    }

    /// <summary>A vector's lanes as bit patterns, so NaN and -0 compare exactly.</summary>
    private static uint[] Lanes(Vector128<float> v) =>
        [.. Enumerable.Range(0, 4).Select(i => BitConverter.SingleToUInt32Bits(v.GetElement(i)))];

    private static uint[] Lanes(float[] values) =>
        [.. values.Select(BitConverter.SingleToUInt32Bits)];
}
