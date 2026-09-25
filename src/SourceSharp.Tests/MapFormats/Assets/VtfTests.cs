using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Assets;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Assets;

/// <summary>
/// The VTF header layout and the mip chain's arithmetic.
/// </summary>
public class VtfTests : IClassFixture<TfLogoFixture>
{
    private readonly TfLogoFixture _fixture;

    /// <summary>Takes the committed golden texture, read once.</summary>
    /// <param name="fixture">The fixture xUnit constructs once.</param>
    public VtfTests(TfLogoFixture fixture) => _fixture = fixture;

    private static int OffsetOf<TField>(ref VtfHeader value, ref TField field) =>
        (int)Unsafe.ByteOffset(
            ref Unsafe.As<VtfHeader, byte>(ref value), ref Unsafe.As<TField, byte>(ref field));

    [Fact]
    public void TheHeaderStructIsEightyBytes()
    {
        // The reference implementation's own warning is that the 7.3 struct "ends at 0x48 as
        // you would expect by counting structure bytes. But, the Infos start
        // at 0x50!" -- the POSIX branch writes that difference out as
        // char pad5[8], so the struct on disk is 0x50 = 80.
        Assert.Equal(80, Unsafe.SizeOf<VtfHeader>());
    }

    [Fact]
    public void ReflectivitySitsAtByteThirtyTwo()
    {
        // The field the whole padding exercise is for: vbsp copies it into
        // dtexdata_t::reflectivity, so a wrong offset here changes every
        // compiled map's TEXDATA lump.
        VtfHeader header = default;
        Assert.Equal(32, OffsetOf(ref header, ref header.Reflectivity));
    }

    [Fact]
    public void BumpScaleFollowsTheSecondPad()
    {
        VtfHeader header = default;
        Assert.Equal(48, OffsetOf(ref header, ref header.BumpScale));
    }

    [Fact]
    public void ImageFormatAndMipCountSitWhereTheKnownLayoutPutsThem()
    {
        VtfHeader header = default;
        Assert.Equal(52, OffsetOf(ref header, ref header.ImageFormat));
        Assert.Equal(56, OffsetOf(ref header, ref header.NumMipLevels));
    }

    [Fact]
    public void TheLowResImageFormatIsAFullIntNotAByte()
    {
        // An easy mistake next to two byte-sized dimensions. ImageFormat is an
        // int everywhere, so lowResImageWidth lands at 61 and not at 58.
        VtfHeader header = default;
        Assert.Equal(57, OffsetOf(ref header, ref header.LowResImageFormat));
        Assert.Equal(61, OffsetOf(ref header, ref header.LowResImageWidth));
        Assert.Equal(62, OffsetOf(ref header, ref header.LowResImageHeight));
    }

    [Fact]
    public void DepthSitsAtSixtyThreeAndNumResourcesAtSixtyEight()
    {
        VtfHeader header = default;
        Assert.Equal(63, OffsetOf(ref header, ref header.Depth));
        Assert.Equal(68, OffsetOf(ref header, ref header.NumResources));
    }

    [Fact]
    public void AResourceEntryIsEightBytes()
    {
        // A union of uint and char[4], plus a uint.
        Assert.Equal(8, Unsafe.SizeOf<VtfResourceEntry>());
    }

    [Fact]
    public void TheGoldenTextureIsVersionSevenPointFour()
    {
        Assert.Equal(7, _fixture.Vtf.Header.Version[0]);
        Assert.Equal(4, _fixture.Vtf.Header.Version[1]);
    }

    [Fact]
    public void TheGoldenTextureIsTwoThousandFortyEightByFiveHundredTwelve()
    {
        Assert.Equal(2048, _fixture.Vtf.Width);
        Assert.Equal(512, _fixture.Vtf.Height);
    }

    [Fact]
    public void TheGoldenTextureIsDxt5()
    {
        Assert.Equal(ImageFormat.Dxt5, _fixture.Vtf.Format);
    }

    [Fact]
    public void TheGoldenTextureHasTwelveMipLevels()
    {
        // log2(2048) + 1. A twelfth level is 1x1, which is where a decoder
        // that forgets to round a compressed mip up to a whole block breaks.
        Assert.Equal(12, _fixture.Vtf.MipCount);
    }

    [Fact]
    public void TheGoldenTexturesHeaderSizeIsEightyPlusTwoResources()
    {
        // 80 + 2 * 8. This is the number that proves both the struct size and
        // the resource entry size against a real file at once.
        Assert.Equal(96, _fixture.Vtf.Header.HeaderSize);
        Assert.Equal(2, _fixture.Vtf.Resources.Count);
    }

    [Fact]
    public void TheGoldenTexturesResourceTableNamesTheThumbnailAndTheImage()
    {
        Assert.Equal(
            VtfResourceType.LowResImage,
            _fixture.Vtf.Resources[0].Type & VtfResourceType.TypeMask);
        Assert.Equal(
            VtfResourceType.Image,
            _fixture.Vtf.Resources[1].Type & VtfResourceType.TypeMask);
    }

    [Fact]
    public void TheGoldenTexturesImageStartsAfterItsThumbnail()
    {
        // 96 header + a 16x4 DXT1 thumbnail (32 bytes) = 128. The resource
        // table says 128 and the arithmetic agrees, which cross-checks the
        // compressed size rule independently of the mip chain.
        Assert.Equal(128, _fixture.Vtf.ImageDataOffset);
        Assert.Equal(
            32, ImageFormatInfo.SizeInBytes(ImageFormat.Dxt1, 16, 4));
    }

    [Fact]
    public void TheGoldenTexturesFileLengthIsExactlyItsHeaderThumbnailAndMipChain()
    {
        // The single strongest fact in this file: 96 + 32 + 1,398,160 =
        // 1,398,288 bytes on disk. Nothing in the header arithmetic can be
        // wrong and still land on the file's real length.
        long computed = _fixture.Vtf.ImageDataOffset + _fixture.Vtf.ImageDataSize();

        Assert.Equal(_fixture.Bytes.Length, computed);
    }

    [Fact]
    public void TheGoldenTexturesReflectivityIsAPlausibleColour()
    {
        // Roughly (0.32, 0.39, 0.26). Read at the wrong offset this is a
        // pixel count or a denormal.
        Assert.InRange(_fixture.Vtf.Reflectivity.X, 0.0f, 1.0f);
        Assert.InRange(_fixture.Vtf.Reflectivity.Y, 0.0f, 1.0f);
        Assert.InRange(_fixture.Vtf.Reflectivity.Z, 0.0f, 1.0f);
        Assert.True(_fixture.Vtf.Reflectivity.LengthSquared() > 0.0f);
    }

    [Fact]
    public void TheGoldenTextureIsNotACubemap()
    {
        Assert.Equal(1, _fixture.Vtf.FaceCount);
    }

    [Fact]
    public void ACubemapFlagMultipliesTheImageDataBySix()
    {
        // Built rather than found: no cubemap VTF is committed, and the flag's
        // effect on the chain is too important to leave unasserted.
        VtfFile flat = VtfFile.Parse(Synthetic(ImageFormat.Bgra8888, 4, 4, mips: 1, envMap: false));
        VtfFile cube = VtfFile.Parse(Synthetic(ImageFormat.Bgra8888, 4, 4, mips: 1, envMap: true));

        Assert.Equal(6, cube.FaceCount);
        Assert.Equal(flat.ImageDataSize() * 6, cube.ImageDataSize());
    }

    [Fact]
    public void TheSmallestMipOfTheGoldenTextureIsOneByOne()
    {
        Assert.Equal(1, _fixture.Vtf.MipWidth(11));
        Assert.Equal(1, _fixture.Vtf.MipHeight(11));
    }

    [Fact]
    public void AOneByOneCompressedMipStillCostsAWholeBlock()
    {
        // The rounding rule. Without it a DXT5 1x1 mip would be sized at zero
        // bytes and the chain's offsets would collapse.
        Assert.Equal(16, _fixture.Vtf.MipSize(11));
    }

    [Fact]
    public void MipsAreStoredSmallestFirst()
    {
        // The last mip is the FIRST thing in the image block, which is what
        // lets the game stream a coarse level without reading the file.
        Assert.Equal(
            _fixture.Vtf.ImageDataOffset,
            IndexOf(_fixture.Bytes, _fixture.Vtf.MipData(_fixture.Vtf.MipCount - 1)));
    }

    [Fact]
    public void TheLargestMipSitsAtTheEndOfTheFile()
    {
        int at = IndexOf(_fixture.Bytes, _fixture.Vtf.MipData(0));

        Assert.Equal(_fixture.Bytes.Length, at + _fixture.Vtf.MipSize(0));
    }

    [Fact]
    public void DecodingTheGoldenTexturesTopMipProducesFourBytesPerPixel()
    {
        byte[] pixels = _fixture.Vtf.DecodeToRgba8888(0);

        Assert.Equal(2048 * 512 * 4, pixels.Length);
    }

    [Fact]
    public void TheGoldenTexturesTopMipHasBothTransparentAndOpaquePixels()
    {
        // It is a logo on transparency. If the DXT5 alpha palette's
        // six-versus-four branch were inverted, every pixel would come out
        // opaque; if the colour block were misread, nothing would be.
        byte[] pixels = _fixture.Vtf.DecodeToRgba8888(0);
        bool transparent = false;
        bool opaque = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            transparent |= pixels[i] == 0;
            opaque |= pixels[i] == 255;
        }

        Assert.True(transparent, "no fully transparent pixel");
        Assert.True(opaque, "no fully opaque pixel");
    }

    [Fact]
    public void RejectsBytesThatAreNotAVtf()
    {
        byte[] bytes = new byte[80];
        "NOPE"u8.CopyTo(bytes);

        Assert.Throws<InvalidVtfException>(() => VtfFile.Parse(bytes));
    }

    [Fact]
    public void RejectsAMajorVersionOtherThanSeven()
    {
        byte[] bytes = Synthetic(ImageFormat.Bgra8888, 4, 4, mips: 1, envMap: false);
        MemoryMarshal.Write(bytes.AsSpan(4), 8);

        Assert.Throws<InvalidVtfException>(() => VtfFile.Parse(bytes));
    }

    [Fact]
    public void RejectsAHeaderSizeBiggerThanTheFile()
    {
        byte[] bytes = Synthetic(ImageFormat.Bgra8888, 4, 4, mips: 1, envMap: false);
        MemoryMarshal.Write(bytes.AsSpan(12), 1 << 20);

        Assert.Throws<InvalidVtfException>(() => VtfFile.Parse(bytes));
    }

    [Fact]
    public void AVersionSevenPointTwoFileWithNoResourceTableUsesTheFixedLayout()
    {
        // Before 7.3 there is no table at all, so the image begins right after
        // the header and the thumbnail. headerSize is then the only thing that
        // says where that is.
        byte[] bytes = Synthetic(
            ImageFormat.Bgra8888, 4, 4, mips: 1, envMap: false, minor: 2, headerSize: 80);
        VtfFile vtf = VtfFile.Parse(bytes);

        Assert.Empty(vtf.Resources);
        Assert.Equal(80, vtf.ImageDataOffset);
    }

    private static int IndexOf(byte[] haystack, ReadOnlyMemory<byte> needle)
    {
        // The slice is a window into the same array, so its position is found
        // by identity rather than by searching for equal bytes.
        return MemoryMarshal.TryGetArray(needle, out ArraySegment<byte> segment)
            && ReferenceEquals(segment.Array, haystack)
            ? segment.Offset
            : -1;
    }

    /// <summary>A minimal, valid VTF built from the header layout.</summary>
    internal static byte[] Synthetic(
        ImageFormat format,
        int width,
        int height,
        int mips,
        bool envMap,
        int minor = 4,
        int headerSize = 96)
    {
        int imageSize = 0;
        for (int i = 0; i < mips; i++)
        {
            imageSize += ImageFormatInfo.SizeInBytes(
                format, Math.Max(1, width >> i), Math.Max(1, height >> i))
                * (envMap ? 6 : 1);
        }

        byte[] bytes = new byte[headerSize + imageSize];
        Span<byte> span = bytes;
        "VTF\0"u8.CopyTo(span);
        MemoryMarshal.Write(span[4..], 7);
        MemoryMarshal.Write(span[8..], minor);
        MemoryMarshal.Write(span[12..], headerSize);
        MemoryMarshal.Write(span[16..], (ushort)width);
        MemoryMarshal.Write(span[18..], (ushort)height);
        MemoryMarshal.Write(span[20..], envMap ? (uint)VtfFlags.EnvMap : 0u);
        MemoryMarshal.Write(span[24..], (ushort)1);
        MemoryMarshal.Write(span[52..], (int)format);
        span[56] = (byte)mips;
        MemoryMarshal.Write(span[57..], (int)ImageFormat.Unknown);
        MemoryMarshal.Write(span[63..], (ushort)1);

        if (minor >= 3 && headerSize >= 88)
        {
            MemoryMarshal.Write(span[68..], 1u);
            MemoryMarshal.Write(span[80..], VtfResourceType.Image);
            MemoryMarshal.Write(span[84..], (uint)headerSize);
        }

        return bytes;
    }
}

/// <summary>The committed TF2 logo VTF, read once for a whole test class.</summary>
public sealed class TfLogoFixture
{
    /// <summary>Reads the file and parses it.</summary>
    public TfLogoFixture()
    {
        Bytes = File.ReadAllBytes(GoldenAssets.TfLogoVtf());
        Vtf = VtfFile.Parse(Bytes);
    }

    /// <summary>The file's bytes.</summary>
    public byte[] Bytes { get; }

    /// <summary>The parsed texture.</summary>
    public VtfFile Vtf { get; }
}
