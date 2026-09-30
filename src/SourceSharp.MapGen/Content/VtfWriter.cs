//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;

namespace SourceSharp.MapGen.Content;

/// <summary>
/// A texel source for <see cref="VtfWriter"/>: the colour at one pixel of
/// the largest mip, as bytes in RGBA order.
/// </summary>
/// <param name="x">The column, 0 at the left.</param>
/// <param name="y">The row, 0 at the top.</param>
/// <returns>Red, green, blue, alpha.</returns>
public delegate (byte R, byte G, byte B, byte A) TexelSource(int x, int y);

/// <summary>
/// Writes a VTF 7.2 (header, no thumbnail, the mip chain smallest first) with
/// its reflectivity computed from the pixels, as vtex computes it.
/// </summary>
/// <remarks>
/// <para>
/// 7.2 is the newest version with no resource table, so the image sits right
/// after the 80-byte header; the readers find it the same way for every
/// version below 7.3. Only uncompressed formats are written
/// (<see cref="ImageFormat.Bgr888"/>, <see cref="ImageFormat.Bgra8888"/>):
/// the compile reads a texture's header, its reflectivity, and for alpha
/// shadows its decoded texels, and none of that depends on compression.
/// </para>
/// <para>
/// The reflectivity is the mean of the largest mip's texels in linear space
/// (each channel over 255, raised to 2.2), which is what the texture tool
/// stores and what vbsp copies into the texdata lump.
/// </para>
/// </remarks>
public static class VtfWriter
{
    /// <summary>The flags a skybox face carries: clamp S and T, sRGB, no mips, no LOD.</summary>
    public const uint SkyboxFlags = 0x034c;

    /// <summary>Writes a texture.</summary>
    /// <param name="width">The largest mip's width, a power of two.</param>
    /// <param name="height">The largest mip's height, a power of two.</param>
    /// <param name="format"><see cref="ImageFormat.Bgr888"/> or <see cref="ImageFormat.Bgra8888"/>.</param>
    /// <param name="flags">The header's flags; <c>NoMip</c> (0x100) writes one mip only.</param>
    /// <param name="texels">The pixels of the largest mip.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">A size is not a power of two, or the format is not supported.</exception>
    public static byte[] Write(int width, int height, ImageFormat format, uint flags, TexelSource texels)
    {
        ArgumentNullException.ThrowIfNull(texels);
        if (width <= 0 || height <= 0 || (width & (width - 1)) != 0 || (height & (height - 1)) != 0 || width > 4096 || height > 4096)
        {
            throw new ArgumentException($"{width}x{height} is not a power-of-two size up to 4096");
        }

        int bpp = format switch
        {
            ImageFormat.Bgr888 => 3,
            ImageFormat.Bgra8888 => 4,
            _ => throw new ArgumentException($"{format} is not a format this writer produces", nameof(format)),
        };

        // The top mip, RGBA, and the reflectivity from it. A channel is one
        // of 256 bytes, so its linear value comes from a table of the 256
        // powers: DetMath, not Math.Pow, because the reflectivity reaches
        // vrad's bounce and must not depend on the platform's pow, and a
        // table because a correctly rounded pow a channel of every texel
        // would cost minutes over the synthetic content.
        double[] linear = new double[256];
        for (int i = 0; i < linear.Length; i++)
        {
            linear[i] = DetMath.Pow(i / 255.0, 2.2);
        }

        byte[] top = new byte[width * height * 4];
        double r = 0, g = 0, b = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte cr, byte cg, byte cb, byte ca) = texels(x, y);
                int at = ((y * width) + x) * 4;
                top[at] = cr;
                top[at + 1] = cg;
                top[at + 2] = cb;
                top[at + 3] = format == ImageFormat.Bgra8888 ? ca : (byte)255;
                r += linear[cr];
                g += linear[cg];
                b += linear[cb];
            }
        }

        double n = width * height;
        Vec3 reflectivity = new((float)(r / n), (float)(g / n), (float)(b / n));

        bool noMip = (flags & 0x100) != 0;
        List<(int W, int H, byte[] Rgba)> mips = [(width, height, top)];
        while (!noMip && (mips[^1].W > 1 || mips[^1].H > 1))
        {
            mips.Add(Halve(mips[^1]));
        }

        int headerSize = Unsafe.SizeOf<VtfHeader>();
        int imageSize = mips.Sum(m => m.W * m.H * bpp);
        byte[] bytes = new byte[headerSize + imageSize];

        VtfHeader header = default;
        header.FileTypeString[0] = (byte)'V';
        header.FileTypeString[1] = (byte)'T';
        header.FileTypeString[2] = (byte)'F';
        header.Version[0] = VtfFile.MajorVersion;
        header.Version[1] = 2;
        header.HeaderSize = headerSize;
        header.Width = (ushort)width;
        header.Height = (ushort)height;
        header.Flags = flags;
        header.NumFrames = 1;
        header.Reflectivity = reflectivity;
        header.BumpScale = 1.0f;
        header.ImageFormat = (int)format;
        header.NumMipLevels = (byte)mips.Count;
        header.LowResImageFormat = (int)ImageFormat.Unknown;
        header.Depth = 1;
        MemoryMarshal.Write(bytes, in header);

        // Smallest mip first, as the readers walk it.
        int offset = headerSize;
        for (int level = mips.Count - 1; level >= 0; level--)
        {
            (int w, int h, byte[] rgba) = mips[level];
            for (int i = 0; i < w * h; i++)
            {
                bytes[offset++] = rgba[(i * 4) + 2];
                bytes[offset++] = rgba[(i * 4) + 1];
                bytes[offset++] = rgba[i * 4];
                if (bpp == 4)
                {
                    bytes[offset++] = rgba[(i * 4) + 3];
                }
            }
        }

        return bytes;
    }

    // A 2x2 box filter; a side already at 1 stays 1.
    private static (int W, int H, byte[] Rgba) Halve((int W, int H, byte[] Rgba) mip)
    {
        int w = Math.Max(1, mip.W / 2);
        int h = Math.Max(1, mip.H / 2);
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(mip.W - 1, (x * 2) + dx);
                            int sy = Math.Min(mip.H - 1, (y * 2) + dy);
                            sum += mip.Rgba[(((sy * mip.W) + sx) * 4) + c];
                        }
                    }

                    rgba[(((y * w) + x) * 4) + c] = (byte)((sum + 2) / 4);
                }
            }
        }

        return (w, h, rgba);
    }
}
