namespace SourceSharp.MapFormats.Assets;

/// <summary>
/// The pixel formats a VTF can store (<c>src/public/bitmap/imageformat.h:33</c>,
/// <c>enum ImageFormat</c>).
/// </summary>
/// <remarks>
/// The numbering is the file format: a VTF stores this enum's value as an
/// <c>int</c>, so the order is fixed forever and the entries this port cannot
/// decode are still named rather than omitted. A format with no name reads as a
/// corrupt file.
/// </remarks>
public enum ImageFormat
{
    /// <summary>No format; also what a VTF stores for "no low-res image".</summary>
    Unknown = -1,

    /// <summary>Eight bits each of red, green, blue and alpha, in that order.</summary>
    Rgba8888 = 0,

    /// <summary>Eight bits each of alpha, blue, green and red.</summary>
    Abgr8888 = 1,

    /// <summary>Twenty-four bits: red, green, blue.</summary>
    Rgb888 = 2,

    /// <summary>Twenty-four bits: blue, green, red.</summary>
    Bgr888 = 3,

    /// <summary>Sixteen bits: five red, six green, five blue.</summary>
    Rgb565 = 4,

    /// <summary>Eight bits of intensity, replicated to all three channels.</summary>
    I8 = 5,

    /// <summary>Eight bits of intensity and eight of alpha.</summary>
    Ia88 = 6,

    /// <summary>Eight-bit palettised. Never written by vtex.</summary>
    P8 = 7,

    /// <summary>Eight bits of alpha only.</summary>
    A8 = 8,

    /// <summary>RGB888 with a magenta key colour treated as transparent.</summary>
    Rgb888Bluescreen = 9,

    /// <summary>BGR888 with a magenta key colour treated as transparent.</summary>
    Bgr888Bluescreen = 10,

    /// <summary>Eight bits each of alpha, red, green and blue.</summary>
    Argb8888 = 11,

    /// <summary>Eight bits each of blue, green, red and alpha.</summary>
    Bgra8888 = 12,

    /// <summary>Block compression 1: four bits per pixel, one-bit alpha.</summary>
    Dxt1 = 13,

    /// <summary>Block compression 2: eight bits per pixel, explicit four-bit alpha.</summary>
    Dxt3 = 14,

    /// <summary>Block compression 3: eight bits per pixel, interpolated alpha.</summary>
    Dxt5 = 15,

    /// <summary>BGRA8888 whose alpha byte is ignored.</summary>
    Bgrx8888 = 16,

    /// <summary>Sixteen bits: five blue, six green, five red.</summary>
    Bgr565 = 17,

    /// <summary>Sixteen bits: five each of blue, green and red, one unused.</summary>
    Bgrx5551 = 18,

    /// <summary>Sixteen bits: four each of blue, green, red and alpha.</summary>
    Bgra4444 = 19,

    /// <summary>DXT1 whose one-bit alpha is actually used.</summary>
    Dxt1OneBitAlpha = 20,

    /// <summary>Sixteen bits: five each of blue, green and red, one of alpha.</summary>
    Bgra5551 = 21,

    /// <summary>Two eight-bit signed channels, for normal maps.</summary>
    Uv88 = 22,

    /// <summary>Four eight-bit signed channels.</summary>
    Uvwq8888 = 23,

    /// <summary>Four 16-bit floats.</summary>
    Rgba16161616F = 24,

    /// <summary>Four 16-bit integers.</summary>
    Rgba16161616 = 25,

    /// <summary>Two signed channels, one unsigned, one unused.</summary>
    Uvlx8888 = 26,

    /// <summary>One 32-bit float.</summary>
    R32F = 27,

    /// <summary>Three 32-bit floats.</summary>
    Rgb323232F = 28,

    /// <summary>Four 32-bit floats.</summary>
    Rgba32323232F = 29,
}

/// <summary>
/// How much memory each <see cref="ImageFormat"/> takes.
/// </summary>
/// <remarks>
/// The size table lives in the closed <c>materialsystem</c> library in this
/// SDK -- <c>imageformat.h</c> declares <c>ImageLoader::GetMemRequired</c> and
/// defines only the colour structs -- so it is reproduced here from those
/// structs and from the DXT block sizes the formats are named after. It is
/// checkable: the committed <c>new_tf2_logo.vtf</c>'s file length must equal
/// its header plus the mip chain this table computes, and a fact says so.
/// </remarks>
public static class ImageFormatInfo
{
    /// <summary>The bits one pixel of <paramref name="format"/> occupies.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The bits per pixel, or zero for a format this port cannot size.</returns>
    /// <remarks>
    /// For the block-compressed formats this is the AVERAGE over a block: DXT1
    /// is an 8-byte block covering 4x4 pixels, so 4 bits per pixel. Never size
    /// a compressed image by multiplying this out -- use
    /// <see cref="SizeInBytes"/>, which rounds up to whole blocks.
    /// </remarks>
    public static int BitsPerPixel(ImageFormat format) => format switch
    {
        ImageFormat.Rgba8888 or ImageFormat.Abgr8888 or ImageFormat.Argb8888
            or ImageFormat.Bgra8888 or ImageFormat.Bgrx8888
            or ImageFormat.Uvwq8888 or ImageFormat.Uvlx8888 => 32,
        ImageFormat.Rgb888 or ImageFormat.Bgr888
            or ImageFormat.Rgb888Bluescreen or ImageFormat.Bgr888Bluescreen => 24,
        ImageFormat.Rgb565 or ImageFormat.Bgr565 or ImageFormat.Bgrx5551
            or ImageFormat.Bgra4444 or ImageFormat.Bgra5551
            or ImageFormat.Ia88 or ImageFormat.Uv88 => 16,
        ImageFormat.I8 or ImageFormat.P8 or ImageFormat.A8 => 8,
        ImageFormat.Dxt1 or ImageFormat.Dxt1OneBitAlpha => 4,
        ImageFormat.Dxt3 or ImageFormat.Dxt5 => 8,
        ImageFormat.Rgba16161616F or ImageFormat.Rgba16161616 => 64,
        ImageFormat.R32F => 32,
        ImageFormat.Rgb323232F => 96,
        ImageFormat.Rgba32323232F => 128,
        _ => 0,
    };

    /// <summary>Whether <paramref name="format"/> is DXT block compressed.</summary>
    /// <param name="format">The format.</param>
    /// <returns>True for the DXT formats.</returns>
    public static bool IsCompressed(ImageFormat format) => format switch
    {
        ImageFormat.Dxt1 or ImageFormat.Dxt1OneBitAlpha or ImageFormat.Dxt3
            or ImageFormat.Dxt5 => true,
        _ => false,
    };

    /// <summary>
    /// How many bytes one <paramref name="width"/> by <paramref name="height"/>
    /// image of <paramref name="format"/> occupies.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <returns>The size in bytes.</returns>
    /// <exception cref="NotSupportedException">The format has no known size.</exception>
    /// <remarks>
    /// A compressed image is rounded UP to whole 4x4 blocks, so a 2x2 DXT1 mip
    /// still costs a full 8-byte block. Dropping that rounding is the classic
    /// way to walk off the end of a mip chain at the 2x2 and 1x1 levels.
    /// </remarks>
    public static int SizeInBytes(ImageFormat format, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        if (IsCompressed(format))
        {
            int blocksWide = Math.Max(1, (width + 3) / 4);
            int blocksHigh = Math.Max(1, (height + 3) / 4);
            int blockBytes = format is ImageFormat.Dxt1 or ImageFormat.Dxt1OneBitAlpha ? 8 : 16;
            return blocksWide * blocksHigh * blockBytes;
        }

        int bits = BitsPerPixel(format);
        if (bits == 0)
        {
            throw new NotSupportedException(
                $"no size is known for image format {format}; imageformat.h names it but this "
                + "port has no entry for it");
        }

        return width * height * bits / 8;
    }
}
