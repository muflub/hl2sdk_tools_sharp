//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//


using SourceSharp.MapFormats.Assets;
using SourceSharp.MapGen.Content;

using Xunit;

namespace SourceSharp.Tests.MapGen.Content;

/// <summary>
/// <see cref="VtfWriter"/>: what it writes reads back through
/// <see cref="VtfFile"/> with the size, format, flags, mips, texels and
/// reflectivity it was given.
/// </summary>
public sealed class VtfWriterTests
{
    private static TexelSource Solid(byte r, byte g, byte b, byte a = 255) => (_, _) => (r, g, b, a);

    [Fact]
    public void AHeaderReadsBackAsWritten()
    {
        VtfFile vtf = VtfFile.Parse(VtfWriter.Write(64, 32, ImageFormat.Bgr888, 0x4, Solid(10, 20, 30)));

        Assert.Equal(64, vtf.Header.Width);
        Assert.Equal(32, vtf.Header.Height);
        Assert.Equal((int)ImageFormat.Bgr888, vtf.Header.ImageFormat);
        Assert.Equal(0x4u, vtf.Header.Flags);
        Assert.Equal(7, vtf.Header.NumMipLevels);
    }

    [Fact]
    public void NoMipWritesOneLevel()
    {
        VtfFile vtf = VtfFile.Parse(VtfWriter.Write(16, 16, ImageFormat.Bgr888, VtfWriter.SkyboxFlags, Solid(1, 2, 3)));

        Assert.Equal(1, vtf.Header.NumMipLevels);
        Assert.Equal(VtfWriter.SkyboxFlags, vtf.Header.Flags);
    }

    [Fact]
    public void ReflectivityIsTheLinearMeanOfTheTexels()
    {
        VtfFile white = VtfFile.Parse(VtfWriter.Write(4, 4, ImageFormat.Bgr888, 0, Solid(255, 255, 255)));
        VtfFile grey = VtfFile.Parse(VtfWriter.Write(4, 4, ImageFormat.Bgr888, 0, Solid(128, 0, 255)));

        Assert.Equal(1f, white.Reflectivity.X, 5);
        Assert.Equal((float)Math.Pow(128 / 255.0, 2.2), grey.Reflectivity.X, 5);
        Assert.Equal(0f, grey.Reflectivity.Y, 5);
        Assert.Equal(1f, grey.Reflectivity.Z, 5);
    }

    [Fact]
    public void TexelsDecodeWithTheirAlpha()
    {
        TexelSource checker = (x, y) => x == y ? ((byte)200, (byte)100, (byte)50, (byte)0) : ((byte)1, (byte)2, (byte)3, (byte)255);
        VtfFile vtf = VtfFile.Parse(VtfWriter.Write(2, 2, ImageFormat.Bgra8888, 0, checker));

        byte[] rgba = vtf.DecodeToRgba8888();

        Assert.Equal([200, 100, 50, 0, 1, 2, 3, 255], rgba[..8]);
    }

    [Fact]
    public void AnOpaqueFormatDropsTheSourceAlpha()
    {
        VtfFile vtf = VtfFile.Parse(VtfWriter.Write(2, 2, ImageFormat.Bgr888, 0, Solid(9, 9, 9, 0)));
        byte[] rgba = vtf.DecodeToRgba8888();

        Assert.Equal(255, rgba[3]);
    }

    [Fact]
    public void SmallerMipsAreTheBoxFilteredLargerOnes()
    {
        TexelSource stripes = (x, _) => x % 2 == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)200, (byte)200, (byte)200, (byte)255);
        VtfFile vtf = VtfFile.Parse(VtfWriter.Write(2, 2, ImageFormat.Bgr888, 0, stripes));

        Assert.Equal(2, vtf.Header.NumMipLevels);
        Assert.Equal(100, vtf.MipData(1).Span[0]);
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 0)]
    [InlineData(8192, 4)]
    public void ASizeThatIsNotAPowerOfTwoIsRefused(int w, int h)
    {
        Assert.Throws<ArgumentException>(() => VtfWriter.Write(w, h, ImageFormat.Bgr888, 0, Solid(0, 0, 0)));
    }

    [Fact]
    public void ACompressedFormatIsRefused()
    {
        Assert.Throws<ArgumentException>(() => VtfWriter.Write(4, 4, ImageFormat.Dxt1, 0, Solid(0, 0, 0)));
    }
}
