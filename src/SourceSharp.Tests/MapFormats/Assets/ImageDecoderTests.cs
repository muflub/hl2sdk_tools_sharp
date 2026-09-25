using SourceSharp.MapFormats.Assets;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Assets;

/// <summary>
/// The pixel decoders, against blocks whose answer is arithmetic rather than
/// a golden image.
/// </summary>
public class ImageDecoderTests
{
    [Fact]
    public void Dxt1IsFourBitsPerPixel()
    {
        // An 8-byte block over 4x4 pixels.
        Assert.Equal(8, ImageFormatInfo.SizeInBytes(ImageFormat.Dxt1, 4, 4));
    }

    [Fact]
    public void Dxt5IsEightBitsPerPixel()
    {
        Assert.Equal(16, ImageFormatInfo.SizeInBytes(ImageFormat.Dxt5, 4, 4));
    }

    [Fact]
    public void ACompressedImageIsRoundedUpToWholeBlocks()
    {
        // 5x5 needs 2x2 blocks, not 1.25 squared.
        Assert.Equal(32, ImageFormatInfo.SizeInBytes(ImageFormat.Dxt1, 5, 5));
    }

    [Fact]
    public void ATwoByTwoCompressedMipStillCostsOneBlock()
    {
        Assert.Equal(8, ImageFormatInfo.SizeInBytes(ImageFormat.Dxt1, 2, 2));
    }

    [Fact]
    public void AnUncompressedImageIsWidthTimesHeightTimesItsStride()
    {
        Assert.Equal(64, ImageFormatInfo.SizeInBytes(ImageFormat.Bgra8888, 4, 4));
    }

    [Fact]
    public void Bgr888IsThreeBytesPerPixel()
    {
        Assert.Equal(48, ImageFormatInfo.SizeInBytes(ImageFormat.Bgr888, 4, 4));
    }

    [Fact]
    public void AFormatWithNoSizeEntryThrowsRatherThanReturningZero()
    {
        // A zero would silently produce an empty mip chain whose offsets all
        // collapse onto each other.
        Assert.Throws<NotSupportedException>(
            () => ImageFormatInfo.SizeInBytes(ImageFormat.Unknown, 4, 4));
    }

    [Fact]
    public void Bgra8888PutsBlueFirstOnDisk()
    {
        // Declares BGRA8888_t as b, g, r, a. A decoder that
        // reads it as RGBA swaps every texture's red and blue, which is subtle
        // enough to ship.
        byte[] source = [10, 20, 30, 40];
        byte[] output = new byte[4];

        ImageDecoder.Decode(ImageFormat.Bgra8888, source, 1, 1, output);

        Assert.Equal<byte[]>([30, 20, 10, 40], output);
    }

    [Fact]
    public void Rgba8888PassesThrough()
    {
        byte[] source = [10, 20, 30, 40];
        byte[] output = new byte[4];

        ImageDecoder.Decode(ImageFormat.Rgba8888, source, 1, 1, output);

        Assert.Equal<byte[]>([10, 20, 30, 40], output);
    }

    [Fact]
    public void Bgr888IsOpaque()
    {
        byte[] source = [10, 20, 30];
        byte[] output = new byte[4];

        ImageDecoder.Decode(ImageFormat.Bgr888, source, 1, 1, output);

        Assert.Equal<byte[]>([30, 20, 10, 255], output);
    }

    [Fact]
    public void FiveBitChannelsExpandByReplicationNotByShifting()
    {
        // 0x1F must become 255 and not 248: a shift alone can never produce
        // full white, which shows up as a texture that is very slightly grey.
        byte[] source = [0xFF, 0xFF];
        byte[] output = new byte[4];

        ImageDecoder.Decode(ImageFormat.Rgb565, source, 1, 1, output);

        Assert.Equal<byte[]>([255, 255, 255, 255], output);
    }

    [Fact]
    public void Rgb565PutsRedInTheTopFiveBits()
    {
        byte[] source = [0x00, 0xF8];
        byte[] output = new byte[4];

        ImageDecoder.Decode(ImageFormat.Rgb565, source, 1, 1, output);

        Assert.Equal<byte[]>([255, 0, 0, 255], output);
    }

    [Fact]
    public void Bgr565PutsBlueInTheTopFiveBits()
    {
        byte[] source = [0x00, 0xF8];
        byte[] output = new byte[4];

        ImageDecoder.Decode(ImageFormat.Bgr565, source, 1, 1, output);

        Assert.Equal<byte[]>([0, 0, 255, 255], output);
    }

    [Fact]
    public void I8ReplicatesIntensityToAllThreeChannels()
    {
        byte[] output = new byte[4];

        ImageDecoder.Decode(ImageFormat.I8, [77], 1, 1, output);

        Assert.Equal<byte[]>([77, 77, 77, 255], output);
    }

    [Fact]
    public void A8CarriesAlphaAndNoColour()
    {
        byte[] output = new byte[4];

        ImageDecoder.Decode(ImageFormat.A8, [77], 1, 1, output);

        Assert.Equal<byte[]>([0, 0, 0, 77], output);
    }

    private static byte[] Dxt1Block(ushort c0, ushort c1, uint indices)
    {
        byte[] block = new byte[8];
        BitConverter.GetBytes(c0).CopyTo(block, 0);
        BitConverter.GetBytes(c1).CopyTo(block, 2);
        BitConverter.GetBytes(indices).CopyTo(block, 4);
        return block;
    }

    [Fact]
    public void Dxt1DecodesItsFirstEndpointVerbatim()
    {
        // Index 0 is colour 0. 0xF800 is pure red in 565.
        byte[] output = new byte[4 * 4 * 4];

        ImageDecoder.Decode(ImageFormat.Dxt1, Dxt1Block(0xF800, 0x0000, 0), 4, 4, output);

        Assert.Equal<byte[]>([255, 0, 0, 255], output[..4]);
    }

    [Fact]
    public void Dxt1WithC0AboveC1HasFourOpaqueColours()
    {
        // Index 3 is the 1/3-2/3 blend, NOT transparent, when c0 > c1.
        byte[] output = new byte[4 * 4 * 4];

        ImageDecoder.Decode(ImageFormat.Dxt1, Dxt1Block(0xFFFF, 0x0000, 0xFFFFFFFF), 4, 4, output);

        Assert.Equal(255, output[3]);
    }

    [Fact]
    public void Dxt1WithC0AtOrBelowC1MakesIndexThreeTransparent()
    {
        // The comparison is on the PACKED 16-bit endpoints. Reverse it and
        // every punch-through texture becomes fully opaque.
        byte[] output = new byte[4 * 4 * 4];

        ImageDecoder.Decode(ImageFormat.Dxt1, Dxt1Block(0x0000, 0xFFFF, 0xFFFFFFFF), 4, 4, output);

        Assert.Equal(0, output[3]);
    }

    [Fact]
    public void Dxt1MidpointBlendIsTheAverageInTheThreeColourMode()
    {
        // Index 2, c0 <= c1: (c0 + c1) / 2. White and black give 127.
        byte[] output = new byte[4 * 4 * 4];
        uint allTwos = 0xAAAAAAAA;

        ImageDecoder.Decode(ImageFormat.Dxt1, Dxt1Block(0x0000, 0xFFFF, allTwos), 4, 4, output);

        Assert.Equal(127, output[0]);
    }

    private static byte[] Dxt5Block(byte a0, byte a1, ulong alphaIndices, ushort c0, ushort c1)
    {
        byte[] block = new byte[16];
        block[0] = a0;
        block[1] = a1;
        for (int i = 0; i < 6; i++)
        {
            block[2 + i] = (byte)((alphaIndices >> (8 * i)) & 0xFF);
        }

        BitConverter.GetBytes(c0).CopyTo(block, 8);
        BitConverter.GetBytes(c1).CopyTo(block, 10);
        return block;
    }

    [Fact]
    public void Dxt5AlphaIndexZeroIsTheFirstEndpoint()
    {
        byte[] output = new byte[4 * 4 * 4];

        ImageDecoder.Decode(ImageFormat.Dxt5, Dxt5Block(200, 10, 0, 0xFFFF, 0), 4, 4, output);

        Assert.Equal(200, output[3]);
    }

    [Fact]
    public void Dxt5WithA0AtOrBelowA1GivesIndexSixHardZero()
    {
        // The four-interpolant branch appends explicit 0 and 255. Taking the
        // six-interpolant branch instead makes fully transparent texels come
        // out at roughly a seventh of a0, which reads as "nearly opaque".
        ulong allSixes = 0;
        for (int i = 0; i < 16; i++)
        {
            allSixes |= 6UL << (3 * i);
        }

        byte[] output = new byte[4 * 4 * 4];
        ImageDecoder.Decode(ImageFormat.Dxt5, Dxt5Block(10, 200, allSixes, 0xFFFF, 0), 4, 4, output);

        Assert.Equal(0, output[3]);
    }

    [Fact]
    public void Dxt5WithA0AtOrBelowA1GivesIndexSevenHardTwoFiveFive()
    {
        ulong allSevens = 0;
        for (int i = 0; i < 16; i++)
        {
            allSevens |= 7UL << (3 * i);
        }

        byte[] output = new byte[4 * 4 * 4];
        ImageDecoder.Decode(ImageFormat.Dxt5, Dxt5Block(10, 200, allSevens, 0xFFFF, 0), 4, 4, output);

        Assert.Equal(255, output[3]);
    }

    [Fact]
    public void Dxt5WithA0AboveA1HasNoHardEndpointsAtSixOrSeven()
    {
        // The six-interpolant branch: index 7 is the last blend, close to a1.
        ulong allSevens = 0;
        for (int i = 0; i < 16; i++)
        {
            allSevens |= 7UL << (3 * i);
        }

        byte[] output = new byte[4 * 4 * 4];
        ImageDecoder.Decode(ImageFormat.Dxt5, Dxt5Block(200, 10, allSevens, 0xFFFF, 0), 4, 4, output);

        Assert.Equal((200 + (6 * 10)) / 7, output[3]);
    }

    [Fact]
    public void Dxt3AlphaIsFourExplicitBitsPerPixelLowNibbleFirst()
    {
        byte[] block = new byte[16];
        block[0] = 0xF0;
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(block, 8);

        byte[] output = new byte[4 * 4 * 4];
        ImageDecoder.Decode(ImageFormat.Dxt3, block, 4, 4, output);

        // Pixel 0 takes the LOW nibble (0) and pixel 1 the high one (0xF).
        Assert.Equal(0, output[3]);
        Assert.Equal(255, output[7]);
    }

    [Fact]
    public void DecodingRejectsATooSmallDestination()
    {
        Assert.Throws<ArgumentException>(
            () => ImageDecoder.Decode(ImageFormat.Bgra8888, new byte[64], 4, 4, new byte[8]));
    }

    [Fact]
    public void DecodingRejectsATruncatedSource()
    {
        Assert.Throws<ArgumentException>(
            () => ImageDecoder.Decode(ImageFormat.Bgra8888, new byte[8], 4, 4, new byte[64]));
    }

    [Fact]
    public void TheFloatFormatsAreNotDecodable()
    {
        // Named in the reference implementation, deliberately unsupported: nothing in the map
        // tools samples an HDR texture, and a wrong guess would be worse than
        // a refusal.
        Assert.False(ImageDecoder.CanDecode(ImageFormat.Rgba16161616F));
        Assert.False(ImageDecoder.CanDecode(ImageFormat.Rgba32323232F));
    }

    [Fact]
    public void TheFormatsTheMapToolsNeedAreAllDecodable()
    {
        Assert.True(ImageDecoder.CanDecode(ImageFormat.Dxt1));
        Assert.True(ImageDecoder.CanDecode(ImageFormat.Dxt5));
        Assert.True(ImageDecoder.CanDecode(ImageFormat.Bgra8888));
        Assert.True(ImageDecoder.CanDecode(ImageFormat.Bgr888));
    }
}
