//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Nav;

/// <summary>The codec byte's three codecs: spelled, compressed, decompressed, and refused when the bytes lie.</summary>
public sealed class NavCompressionTests
{
    [Theory]
    [InlineData("none", NavCodec.None, 0)]
    [InlineData("NONE", NavCodec.None, 0)]
    [InlineData("deflate", NavCodec.Deflate, 6)]
    [InlineData("deflate:0", NavCodec.Deflate, 0)]
    [InlineData("deflate:9", NavCodec.Deflate, 9)]
    [InlineData("brotli", NavCodec.Brotli, 9)]
    [InlineData(" brotli:11 ", NavCodec.Brotli, 11)]
    public void ACodecIsSpelledByNameAndLevel(string text, NavCodec codec, int level)
    {
        Assert.True(NavCompression.TryParse(text, out NavCompression compression));
        Assert.Equal(new NavCompression(codec, level), compression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zip")]
    [InlineData("deflate:10")]
    [InlineData("brotli:12")]
    [InlineData("brotli:-1")]
    [InlineData("none:1")]
    [InlineData("deflate:1:2")]
    [InlineData("deflate:x")]
    public void AnythingElseIsNotACodec(string? text) => Assert.False(NavCompression.TryParse(text, out _));

    [Theory]
    [InlineData(NavCodec.None, 0)]
    [InlineData(NavCodec.Deflate, 1)]
    [InlineData(NavCodec.Deflate, 9)]
    [InlineData(NavCodec.Brotli, 0)]
    [InlineData(NavCodec.Brotli, 11)]
    public void BytesRoundTripAndCompressTheSameEveryTime(NavCodec codec, int level)
    {
        byte[] raw = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i % 7 == 0 ? i : 3))];
        NavCompression compression = new(codec, level);
        byte[] stored = compression.Compress(raw);
        Assert.Equal(stored, compression.Compress(raw));
        Assert.Equal(raw, NavCompression.Decompress(codec, stored, raw.Length));
        if (codec != NavCodec.None)
        {
            Assert.True(stored.Length < raw.Length);
        }
    }

    [Theory]
    [InlineData(NavCodec.None)]
    [InlineData(NavCodec.Deflate)]
    [InlineData(NavCodec.Brotli)]
    public void AStoredStreamOfTheWrongLengthIsRefused(NavCodec codec)
    {
        byte[] raw = [.. Enumerable.Range(0, 300).Select(i => (byte)i)];
        byte[] stored = new NavCompression(codec, codec == NavCodec.None ? 0 : 5).Compress(raw);
        Assert.Throws<InvalidDataException>(() => NavCompression.Decompress(codec, stored, raw.Length + 1));
        Assert.Throws<InvalidDataException>(() => NavCompression.Decompress(codec, stored, raw.Length - 1));
        Assert.Throws<InvalidDataException>(() => NavCompression.Decompress(codec, stored, -1));
    }

    [Fact]
    public void GarbageAndUnknownCodecsAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => NavCompression.Decompress(NavCodec.Deflate, [0xFF, 0xFF, 0xFF, 0xFF], 10));
        Assert.Throws<InvalidDataException>(() => NavCompression.Decompress(NavCodec.Brotli, [0xFF, 0xFF, 0xFF, 0xFF], 10));
        Assert.Throws<InvalidDataException>(() => NavCompression.Decompress((NavCodec)7, [1], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavCompression((NavCodec)7, 0).Compress([1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavCompression(NavCodec.Deflate, 10).Compress([1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavCompression(NavCodec.Brotli, -1).Compress([1]));
    }
}
