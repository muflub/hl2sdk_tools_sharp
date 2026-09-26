//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// The <c>mathlib</c> colour conversions in <see cref="StockLightColor"/>, one
/// behaviour per fact.
/// </summary>
public sealed class StockLightColorTests
{
    [Fact]
    public void EncodingBlackGivesZeroMantissasAndExponent()
    {
        ColorRgbExp32 c = StockLightColor.Encode(Vec3.Zero);
        Assert.Equal((0, 0, 0, 0), (c.R, c.G, c.B, (int)c.Exponent));
    }

    [Fact]
    public void EncodingPutsTheLargestChannelOnTheTopHalfOfAByte()
    {
        // 1.0f has biased exponent 127, so the exponent is 127 - 134 = -7 and the scale 128.
        ColorRgbExp32 c = StockLightColor.Encode(new Vec3(0.5f, 1.0f, 0.25f));
        Assert.Equal((64, 128, 32, -7), (c.R, c.G, c.B, (int)c.Exponent));
    }

    [Fact]
    public void EncodingTruncatesRatherThanRounds()
    {
        // 1.99 * 128 = 254.72.
        ColorRgbExp32 c = StockLightColor.Encode(new Vec3(1.99f, 0, 0));
        Assert.Equal(254, c.R);
    }

    [Fact]
    public void DecodingIsMantissaTimesTwoToTheExponentOver255()
    {
        Assert.Equal(1.0f / 255.0f, StockLightColor.TexLightToLinear(128, -7), 7);
    }

    [Fact]
    public void ScreenGammaMapsZeroToZeroAndOneTo255()
    {
        Assert.Equal(0, StockLightColor.LinearToScreenGamma(0.0f));
        Assert.Equal(255, StockLightColor.LinearToScreenGamma(1.0f));
    }

    [Fact]
    public void ScreenGammaClampsOutOfRangeInput()
    {
        Assert.Equal(255, StockLightColor.LinearToScreenGamma(7.0f));
        Assert.Equal(0, StockLightColor.LinearToScreenGamma(-1.0f));
    }

    [Fact]
    public void VertexLightOfZeroIsZero()
    {
        Assert.Equal(0.0f, StockLightColor.LinearToVertexLight(0.0f));
    }

    [Fact]
    public void VertexLightOfOneIsHalvedByTheOverbrightFactor()
    {
        Assert.Equal(0.5f, StockLightColor.LinearToVertexLight(1.0f));
    }

    [Fact]
    public void VertexLightAboveFourUsesTheLastTableEntry()
    {
        Assert.Equal(StockLightColor.LinearToVertexLight(4095.0f / 1024.0f), StockLightColor.LinearToVertexLight(100.0f));
    }

    [Fact]
    public void VertexLightOfANegativeValueIsZero()
    {
        Assert.Equal(0.0f, StockLightColor.LinearToVertexLight(-3.0f));
    }

    [Theory]
    [InlineData(0.5f, 0)]
    [InlineData(1.5f, 2)]
    [InlineData(2.5f, 2)]
    [InlineData(-1.5f, -2)]
    public void RoundFloatToIntRoundsHalfToEven(float value, int expected)
    {
        Assert.Equal(expected, StockLightColor.RoundFloatToInt(value));
    }

    [Fact]
    public void RoundFloatToIntGivesTheIndefiniteIntegerForNaN()
    {
        Assert.Equal(int.MinValue, StockLightColor.RoundFloatToInt(float.NaN));
    }

    [Fact]
    public void Rgba8888OfBlackIsOpaqueBlack()
    {
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), StockLightColor.ToRgba8888(default));
    }

    [Fact]
    public void Rgba8888GoesThroughTheVertexGammaCurve()
    {
        // Linear 0.25: pow(256/1024, 1/2.2) * 0.5 * 255 = 67.9.
        ColorRgbExp32 quarter = new() { R = 255, G = 0, B = 0, Exponent = -2 };
        Assert.Equal(68, StockLightColor.ToRgba8888(quarter).R);
    }
}
