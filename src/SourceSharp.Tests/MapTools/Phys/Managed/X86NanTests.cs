//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Phys.Managed;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// <see cref="X86Nan"/>: a divide that makes a NaN makes x86's, on any CPU.
/// </summary>
public class X86NanTests
{
    [Fact]
    public void ZeroOverZeroIsX86sNegativeDefaultNaNInFloat()
    {
        Assert.Equal(0xFFC00000u, BitConverter.SingleToUInt32Bits(X86Nan.Divide(0f, 0f)));
    }

    [Fact]
    public void ZeroOverZeroIsX86sNegativeDefaultNaNInDouble()
    {
        Assert.Equal(0xFFF8000000000000ul, BitConverter.DoubleToUInt64Bits(X86Nan.Divide(0.0, 0.0)));
    }

    [Fact]
    public void InfinityOverInfinityIsTheSameNaN()
    {
        Assert.Equal(0xFFF8000000000000ul, BitConverter.DoubleToUInt64Bits(X86Nan.Divide(double.PositiveInfinity, double.NegativeInfinity)));
    }

    [Fact]
    public void AnOrdinaryQuotientIsUntouched()
    {
        Assert.Equal(0.5, X86Nan.Divide(1.0, 2.0));
        Assert.Equal(float.PositiveInfinity, X86Nan.Divide(1f, 0f));
    }

    [Fact]
    public void ANaNOperandPassesThroughRatherThanBeingReplaced()
    {
        // A positive NaN in: both instruction sets return it, sign clear.
        double positive = BitConverter.UInt64BitsToDouble(0x7FF8000000000001ul);

        ulong bits = BitConverter.DoubleToUInt64Bits(X86Nan.Divide(positive, 2.0));

        Assert.True(double.IsNaN(BitConverter.UInt64BitsToDouble(bits)));
        Assert.Equal(0ul, bits >> 63);
    }

    [Fact]
    public void OnlyFloatAndDoubleHaveADefault()
    {
        Assert.Throws<NotSupportedException>(() => X86Nan.Default<Half>());
    }
}
