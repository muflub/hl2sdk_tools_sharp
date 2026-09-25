using System.Buffers.Binary;

namespace SourceSharp.MapFormats.Assets;

/// <summary>
/// Decodes the VTF pixel formats the map tools need into 8-bit RGBA.
/// </summary>
/// <remarks>
/// <para>
/// The two consumers are <c>vrad -textureshadows</c>, which samples a
/// texture's alpha to decide how much light a surface blocks, and cubemap
/// generation, which reads and writes whole faces. Between them they need
/// DXT1, DXT5 and the uncompressed 8-bit-per-channel layouts; the rarer
/// formats decode too, and the float formats deliberately do not.
/// </para>
/// <para>
/// Output is always RGBA8888, row-major from the top-left, four bytes per
/// pixel, because a single output layout is what makes the sampling code
/// above it format-blind.
/// </para>
/// </remarks>
public static class ImageDecoder
{
    /// <summary>Whether this port can decode <paramref name="format"/>.</summary>
    /// <param name="format">The format.</param>
    /// <returns>True when <see cref="Decode"/> will succeed.</returns>
    public static bool CanDecode(ImageFormat format) => format switch
    {
        ImageFormat.Rgba8888 or ImageFormat.Abgr8888 or ImageFormat.Argb8888
            or ImageFormat.Bgra8888 or ImageFormat.Bgrx8888
            or ImageFormat.Rgb888 or ImageFormat.Bgr888
            or ImageFormat.Rgb888Bluescreen or ImageFormat.Bgr888Bluescreen
            or ImageFormat.Rgb565 or ImageFormat.Bgr565
            or ImageFormat.Bgrx5551 or ImageFormat.Bgra5551 or ImageFormat.Bgra4444
            or ImageFormat.I8 or ImageFormat.Ia88 or ImageFormat.A8
            or ImageFormat.Dxt1 or ImageFormat.Dxt1OneBitAlpha
            or ImageFormat.Dxt3 or ImageFormat.Dxt5 => true,
        _ => false,
    };

    /// <summary>
    /// Decodes one image into <paramref name="destination"/> as RGBA8888.
    /// </summary>
    /// <param name="format">The source format.</param>
    /// <param name="source">The source pixels.</param>
    /// <param name="width">The image's width in pixels.</param>
    /// <param name="height">The image's height in pixels.</param>
    /// <param name="destination">A buffer of at least <c>width * height * 4</c> bytes.</param>
    /// <exception cref="NotSupportedException">The format has no decoder here.</exception>
    /// <exception cref="ArgumentException">Either buffer is the wrong size.</exception>
    public static void Decode(
        ImageFormat format,
        ReadOnlySpan<byte> source,
        int width,
        int height,
        Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        int needed = width * height * 4;
        if (destination.Length < needed)
        {
            throw new ArgumentException(
                $"decoding {width}x{height} needs {needed} bytes and the buffer is "
                + $"{destination.Length}",
                nameof(destination));
        }

        int sourceNeeded = ImageFormatInfo.SizeInBytes(format, width, height);
        if (source.Length < sourceNeeded)
        {
            throw new ArgumentException(
                $"{width}x{height} of {format} is {sourceNeeded} bytes and the source is "
                + $"{source.Length}",
                nameof(source));
        }

        switch (format)
        {
            case ImageFormat.Dxt1:
            case ImageFormat.Dxt1OneBitAlpha:
                DecodeDxt1(source, width, height, destination);
                return;

            case ImageFormat.Dxt3:
                DecodeDxt3(source, width, height, destination);
                return;

            case ImageFormat.Dxt5:
                DecodeDxt5(source, width, height, destination);
                return;

            default:
                DecodeUncompressed(format, source, width * height, destination);
                return;
        }
    }

    private static void DecodeUncompressed(
        ImageFormat format,
        ReadOnlySpan<byte> source,
        int pixels,
        Span<byte> destination)
    {
        int stride = ImageFormatInfo.BitsPerPixel(format) / 8;
        if (stride == 0 || !CanDecode(format))
        {
            throw new NotSupportedException(
                $"image format {format} has no decoder here; the format is known "
                + "but nothing in the map tools reads it");
        }

        for (int i = 0; i < pixels; i++)
        {
            ReadOnlySpan<byte> pixel = source.Slice(i * stride, stride);
            Span<byte> output = destination.Slice(i * 4, 4);

            switch (format)
            {
                case ImageFormat.Rgba8888:
                    pixel.CopyTo(output);
                    break;

                case ImageFormat.Abgr8888:
                    output[0] = pixel[3];
                    output[1] = pixel[2];
                    output[2] = pixel[1];
                    output[3] = pixel[0];
                    break;

                case ImageFormat.Argb8888:
                    output[0] = pixel[1];
                    output[1] = pixel[2];
                    output[2] = pixel[3];
                    output[3] = pixel[0];
                    break;

                case ImageFormat.Bgra8888:
                    // The reference format declares BGRA8888_t as b, g, r, a in
                    // that declaration order, so the byte at offset 0 is BLUE.
                    output[0] = pixel[2];
                    output[1] = pixel[1];
                    output[2] = pixel[0];
                    output[3] = pixel[3];
                    break;

                case ImageFormat.Bgrx8888:
                    output[0] = pixel[2];
                    output[1] = pixel[1];
                    output[2] = pixel[0];
                    output[3] = 255;
                    break;

                case ImageFormat.Rgb888:
                    output[0] = pixel[0];
                    output[1] = pixel[1];
                    output[2] = pixel[2];
                    output[3] = 255;
                    break;

                case ImageFormat.Bgr888:
                    output[0] = pixel[2];
                    output[1] = pixel[1];
                    output[2] = pixel[0];
                    output[3] = 255;
                    break;

                case ImageFormat.Rgb888Bluescreen:
                    output[0] = pixel[0];
                    output[1] = pixel[1];
                    output[2] = pixel[2];

                    // The key colour is pure magenta (0, 0, 255) in BGR terms,
                    // which is r=0 g=0 b=255. A pixel that matches is fully
                    // transparent; nothing else is.
                    output[3] = (byte)(pixel[0] == 0 && pixel[1] == 0 && pixel[2] == 255 ? 0 : 255);
                    break;

                case ImageFormat.Bgr888Bluescreen:
                    output[0] = pixel[2];
                    output[1] = pixel[1];
                    output[2] = pixel[0];
                    output[3] = (byte)(pixel[2] == 0 && pixel[1] == 0 && pixel[0] == 255 ? 0 : 255);
                    break;

                case ImageFormat.Rgb565:
                {
                    ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(pixel);
                    output[0] = Expand5((packed >> 11) & 0x1F);
                    output[1] = Expand6((packed >> 5) & 0x3F);
                    output[2] = Expand5(packed & 0x1F);
                    output[3] = 255;
                    break;
                }

                case ImageFormat.Bgr565:
                {
                    ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(pixel);
                    output[0] = Expand5(packed & 0x1F);
                    output[1] = Expand6((packed >> 5) & 0x3F);
                    output[2] = Expand5((packed >> 11) & 0x1F);
                    output[3] = 255;
                    break;
                }

                case ImageFormat.Bgrx5551:
                case ImageFormat.Bgra5551:
                {
                    ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(pixel);
                    output[0] = Expand5((packed >> 10) & 0x1F);
                    output[1] = Expand5((packed >> 5) & 0x1F);
                    output[2] = Expand5(packed & 0x1F);
                    output[3] = format == ImageFormat.Bgra5551
                        ? (byte)((packed & 0x8000) != 0 ? 255 : 0)
                        : (byte)255;
                    break;
                }

                case ImageFormat.Bgra4444:
                {
                    ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(pixel);
                    output[0] = Expand4((packed >> 8) & 0xF);
                    output[1] = Expand4((packed >> 4) & 0xF);
                    output[2] = Expand4(packed & 0xF);
                    output[3] = Expand4((packed >> 12) & 0xF);
                    break;
                }

                case ImageFormat.I8:
                    output[0] = pixel[0];
                    output[1] = pixel[0];
                    output[2] = pixel[0];
                    output[3] = 255;
                    break;

                case ImageFormat.Ia88:
                    output[0] = pixel[0];
                    output[1] = pixel[0];
                    output[2] = pixel[0];
                    output[3] = pixel[1];
                    break;

                case ImageFormat.A8:
                    // An alpha-only texture has NO colour. Black rather than
                    // white, because vrad's texture shadows multiply by it.
                    output[0] = 0;
                    output[1] = 0;
                    output[2] = 0;
                    output[3] = pixel[0];
                    break;

                default:
                    throw new NotSupportedException(
                        $"image format {format} has no decoder in this port");
            }
        }
    }

    // 5, 6 and 4 bit channels are expanded by REPLICATING the high bits into
    // the low ones, not by shifting and leaving zeros: that is what makes 31
    // become 255 rather than 248, so a fully white 565 pixel decodes white.
    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));

    private static byte Expand6(int value) => (byte)((value << 2) | (value >> 4));

    private static byte Expand4(int value) => (byte)((value << 4) | value);

    private static void DecodeDxt1(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        Span<byte> destination)
    {
        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);
        Span<byte> colors = stackalloc byte[16];

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                ReadOnlySpan<byte> block = source.Slice(((by * blocksWide) + bx) * 8, 8);
                DecodeColorBlock(block, colors, out bool hasPunchThrough);
                EmitBlock(block[4..], colors, bx, by, width, height, destination, hasPunchThrough);
            }
        }
    }

    private static void DecodeDxt3(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        Span<byte> destination)
    {
        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[16];

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                ReadOnlySpan<byte> block = source.Slice(((by * blocksWide) + bx) * 16, 16);

                // DXT3 alpha is four explicit bits per pixel, low nibble first.
                for (int i = 0; i < 16; i++)
                {
                    int nibble = (block[i / 2] >> ((i % 2) * 4)) & 0xF;
                    alpha[i] = Expand4(nibble);
                }

                DecodeColorBlock(block[8..], colors, out _);
                EmitBlock(block[12..], colors, bx, by, width, height, destination, false, alpha);
            }
        }
    }

    private static void DecodeDxt5(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        Span<byte> destination)
    {
        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[16];
        Span<byte> palette = stackalloc byte[8];

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                ReadOnlySpan<byte> block = source.Slice(((by * blocksWide) + bx) * 16, 16);

                palette[0] = block[0];
                palette[1] = block[1];
                if (palette[0] > palette[1])
                {
                    // Six interpolated values, no explicit transparent entry.
                    for (int i = 1; i <= 6; i++)
                    {
                        palette[i + 1] = (byte)((((7 - i) * palette[0]) + (i * palette[1])) / 7);
                    }
                }
                else
                {
                    // Four interpolated values, then hard 0 and 255. Getting
                    // this branch backwards makes every fully transparent
                    // texel opaque.
                    for (int i = 1; i <= 4; i++)
                    {
                        palette[i + 1] = (byte)((((5 - i) * palette[0]) + (i * palette[1])) / 5);
                    }

                    palette[6] = 0;
                    palette[7] = 255;
                }

                // Three-bit indices packed into six bytes, little-endian.
                ulong bits = 0;
                for (int i = 0; i < 6; i++)
                {
                    bits |= (ulong)block[2 + i] << (8 * i);
                }

                for (int i = 0; i < 16; i++)
                {
                    alpha[i] = palette[(int)((bits >> (3 * i)) & 0x7)];
                }

                DecodeColorBlock(block[8..], colors, out _);
                EmitBlock(block[12..], colors, bx, by, width, height, destination, false, alpha);
            }
        }
    }

    private static void DecodeColorBlock(
        ReadOnlySpan<byte> block,
        Span<byte> colors,
        out bool hasPunchThrough)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);

        Rgb565(c0, colors[..4]);
        Rgb565(c1, colors.Slice(4, 4));

        // The c0 > c1 comparison is on the PACKED 16-bit values, not on any
        // decoded channel. It selects between four opaque colours and three
        // plus a transparent entry -- which is the whole of DXT1's alpha.
        hasPunchThrough = c0 <= c1;
        if (!hasPunchThrough)
        {
            for (int i = 0; i < 3; i++)
            {
                colors[8 + i] = (byte)(((2 * colors[i]) + colors[4 + i]) / 3);
                colors[12 + i] = (byte)((colors[i] + (2 * colors[4 + i])) / 3);
            }

            colors[11] = 255;
            colors[15] = 255;
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                colors[8 + i] = (byte)((colors[i] + colors[4 + i]) / 2);
                colors[12 + i] = 0;
            }

            colors[11] = 255;
            colors[15] = 0;
        }
    }

    private static void Rgb565(ushort packed, Span<byte> output)
    {
        output[0] = Expand5((packed >> 11) & 0x1F);
        output[1] = Expand6((packed >> 5) & 0x3F);
        output[2] = Expand5(packed & 0x1F);
        output[3] = 255;
    }

    private static void EmitBlock(
        ReadOnlySpan<byte> indices,
        ReadOnlySpan<byte> colors,
        int blockX,
        int blockY,
        int width,
        int height,
        Span<byte> destination,
        bool punchThrough,
        ReadOnlySpan<byte> alpha = default)
    {
        for (int y = 0; y < 4; y++)
        {
            int py = (blockY * 4) + y;
            if (py >= height)
            {
                continue;
            }

            for (int x = 0; x < 4; x++)
            {
                int px = (blockX * 4) + x;
                if (px >= width)
                {
                    continue;
                }

                int index = (indices[y] >> (2 * x)) & 0x3;
                int at = ((py * width) + px) * 4;
                destination[at] = colors[(index * 4) + 0];
                destination[at + 1] = colors[(index * 4) + 1];
                destination[at + 2] = colors[(index * 4) + 2];

                if (!alpha.IsEmpty)
                {
                    destination[at + 3] = alpha[(y * 4) + x];
                }
                else
                {
                    destination[at + 3] = punchThrough && index == 3 ? (byte)0 : (byte)255;
                }
            }
        }
    }
}
