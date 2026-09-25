using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Assets;

/// <summary>
/// A VTF file's header, in the on-disk layout
/// (<c>src/public/vtf/vtf.h:458</c> onwards).
/// </summary>
/// <remarks>
/// <para>
/// <c>vtf.h:439</c> puts the whole block under <c>#pragma pack(1)</c>, and the
/// C++ then builds the header as a chain of four inheriting structs with
/// hand-written padding under POSIX. Flattened here, because a derived struct
/// is not a thing a file format has.
/// </para>
/// <para>
/// The header's own comment (<c>vtf.h:445</c>) warns at length that the
/// structure sizes "ARE NOT what they appear, regardless of Pack(1)" because
/// <c>VectorAligned reflectivity</c> makes the PC compiler pad where the 360's
/// does not. The POSIX branch spells that padding out as
/// <c>char pad1[4]</c> / <c>char pad2[4]</c> (<c>vtf.h:478</c>) and
/// <c>char pad5[8]</c> (<c>vtf.h:550</c>), and those are the bytes that are
/// actually in the file. So this struct is the POSIX branch, and it is 80
/// bytes with the resource table starting at 0x50.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtfHeader
{
    /// <summary>The four bytes <c>VTF\0</c>.</summary>
    public ByteArray4 FileTypeString;

    /// <summary>Major then minor version, as two <c>int</c>.</summary>
    public IntArray2 Version;

    /// <summary>
    /// Where the resource table ends and the low-res image begins. Includes the
    /// resource entries, so it is 0x50 for a 7.3+ file with none.
    /// </summary>
    public int HeaderSize;

    /// <summary>The largest mip's width.</summary>
    public ushort Width;

    /// <summary>The largest mip's height.</summary>
    public ushort Height;

    /// <summary><see cref="VtfFlags"/>.</summary>
    public uint Flags;

    /// <summary>How many animation frames the texture holds.</summary>
    public ushort NumFrames;

    /// <summary>Which frame to start on.</summary>
    public ushort StartFrame;

    /// <summary>
    /// Four bytes of padding before the reflectivity. In the PC build these
    /// come from <c>VectorAligned</c>'s 16-byte alignment; the POSIX build
    /// writes them by hand so the two agree on disk (<c>vtf.h:478</c>).
    /// </summary>
    public IntArray1 Pad1;

    /// <summary>
    /// The texture's average colour, which vbsp copies into LUMP_TEXDATA.
    /// </summary>
    /// <remarks>
    /// Byte offset 32. That is the field this whole struct exists to place
    /// correctly: get the padding wrong and every reflectivity in every
    /// compiled map's texdata lump is a different number.
    /// </remarks>
    public Vec3 Reflectivity;

    /// <summary>Four more bytes of <c>VectorAligned</c> padding (<c>vtf.h:481</c>).</summary>
    public IntArray1 Pad2;

    /// <summary>The scale a normal map's bumpiness is multiplied by.</summary>
    public float BumpScale;

    /// <summary>The main image's <see cref="ImageFormat"/>.</summary>
    public int ImageFormat;

    /// <summary>How many mip levels the main image has.</summary>
    public byte NumMipLevels;

    /// <summary>The thumbnail's <see cref="ImageFormat"/>, or -1 when absent.</summary>
    public int LowResImageFormat;

    /// <summary>The thumbnail's width, at most 16.</summary>
    public byte LowResImageWidth;

    /// <summary>The thumbnail's height.</summary>
    public byte LowResImageHeight;

    /// <summary>The depth of a volume texture. One for an ordinary texture. Added at 7.2.</summary>
    public ushort Depth;

    /// <summary>Three bytes of padding before the resource count (<c>vtf.h:544</c>).</summary>
    public ByteArray3 Pad4;

    /// <summary>How many resource entries follow the header. Added at 7.3.</summary>
    public uint NumResources;

    /// <summary>Eight more bytes of alignment padding (<c>vtf.h:550</c>).</summary>
    public IntArray2 Pad5;
}

/// <summary>One byte-aligned 32-bit value, as C's <c>char pad[4]</c>.</summary>
[InlineArray(1)]
public struct IntArray1
{
    private int _element0;
}

/// <summary>
/// One entry of a 7.3+ VTF's resource table (<c>vtf.h:530</c>,
/// <c>struct ResourceEntryInfo</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VtfResourceEntry
{
    /// <summary>
    /// The resource type in the low three bytes and
    /// <see cref="VtfResourceFlags"/> in the high byte.
    /// </summary>
    public uint Type;

    /// <summary>
    /// A file offset, or the resource's DATA when
    /// <see cref="VtfResourceFlags.HasNoDataChunk"/> is set
    /// (<c>vtf.h:518</c>).
    /// </summary>
    public uint ResData;
}

/// <summary>
/// The stock resource types a VTF's table can name (<c>vtf.h:521</c>).
/// </summary>
/// <remarks>
/// The ids are packed by <c>MK_VTF_RSRC_ID</c> (<c>vtf.h:501</c>), which puts
/// the first byte in the LOW byte -- the opposite of the game lump codes.
/// </remarks>
public static class VtfResourceType
{
    /// <summary>The thumbnail. A legacy type with no length word.</summary>
    public const uint LowResImage = 0x01;

    /// <summary>The main image. A legacy type with no length word.</summary>
    public const uint Image = 0x30;

    /// <summary>Sprite sheet data.</summary>
    public const uint Sheet = 0x10;

    /// <summary>The mask that separates the type from its flags (<c>vtf.h:512</c>).</summary>
    public const uint TypeMask = 0x00FFFFFF;
}

/// <summary>
/// The flag byte of a resource entry's type (<c>vtf.h:509</c>).
/// </summary>
[Flags]
public enum VtfResourceFlags : uint
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The entry's <see cref="VtfResourceEntry.ResData"/> IS the data.</summary>
    HasNoDataChunk = 0x02000000,
}

/// <summary>
/// The texture flags of <see cref="VtfHeader.Flags"/>
/// (<c>vtf.h:31</c>, <c>enum CompiledVtfFlags</c>).
/// </summary>
[Flags]
public enum VtfFlags : uint
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Point sampling.</summary>
    PointSample = 0x00000001,

    /// <summary>Trilinear filtering.</summary>
    Trilinear = 0x00000002,

    /// <summary>Clamp the s coordinate.</summary>
    ClampS = 0x00000004,

    /// <summary>Clamp the t coordinate.</summary>
    ClampT = 0x00000008,

    /// <summary>Anisotropic filtering.</summary>
    Anisotropic = 0x00000010,

    /// <summary>Prefer DXT5 when compressing.</summary>
    HintDxt5 = 0x00000020,

    /// <summary>The texture is in sRGB space.</summary>
    Srgb = 0x00000040,

    /// <summary>The texture is a normal map.</summary>
    Normal = 0x00000080,

    /// <summary>No mip chain.</summary>
    NoMip = 0x00000100,

    /// <summary>Never drop this texture's detail.</summary>
    NoLod = 0x00000200,

    /// <summary>Keep every mip, including the ones below 32x32.</summary>
    AllMips = 0x00000400,

    /// <summary>The texture is generated at run time.</summary>
    Procedural = 0x00000800,

    /// <summary>vtex found exactly one alpha value that is not opaque.</summary>
    OneBitAlpha = 0x00001000,

    /// <summary>vtex found a full alpha channel.</summary>
    EightBitAlpha = 0x00002000,

    /// <summary>
    /// The texture is a cubemap: SIX faces per frame instead of one. This flag
    /// is what makes the mip chain six times as large, so a reader that
    /// ignores it computes every offset after the first mip wrongly.
    /// </summary>
    EnvMap = 0x00004000,

    /// <summary>The texture is a render target.</summary>
    RenderTarget = 0x00008000,

    /// <summary>The texture is a depth render target.</summary>
    DepthRenderTarget = 0x00010000,

    /// <summary>Ignore debug overrides.</summary>
    NoDebugOverride = 0x00020000,

    /// <summary>Do not share this texture's memory.</summary>
    SingleCopy = 0x00040000,

    /// <summary>Ignore the picmip convar.</summary>
    IgnorePicmip = 0x00200000,

    /// <summary>The render target has no depth buffer.</summary>
    NoDepthBuffer = 0x00800000,

    /// <summary>Clamp the u coordinate of a volume texture.</summary>
    ClampU = 0x02000000,

    /// <summary>The texture can be sampled by a vertex shader.</summary>
    VertexTexture = 0x04000000,

    /// <summary>The texture is a self-shadowing bump map.</summary>
    SsBump = 0x08000000,

    /// <summary>Clamp to the border colour on every coordinate.</summary>
    Border = 0x20000000,
}

/// <summary>
/// Reads a VTF: the header, the resource table, and the pixels.
/// </summary>
public sealed class VtfFile
{
    private readonly ReadOnlyMemory<byte> _bytes;

    private VtfFile(
        ReadOnlyMemory<byte> bytes,
        VtfHeader header,
        IReadOnlyList<VtfResourceEntry> resources,
        int imageDataOffset)
    {
        _bytes = bytes;
        Header = header;
        Resources = resources;
        ImageDataOffset = imageDataOffset;
    }

    /// <summary>The magic bytes every VTF starts with.</summary>
    public static ReadOnlySpan<byte> Signature => "VTF\0"u8;

    /// <summary>The newest version this branch writes (<c>vtf.h:441</c>).</summary>
    public const int MajorVersion = 7;

    /// <summary>The newest minor version (<c>vtf.h:442</c>).</summary>
    public const int MinorVersion = 4;

    /// <summary>The header, as read.</summary>
    public VtfHeader Header { get; }

    /// <summary>The resource table, empty before version 7.3.</summary>
    public IReadOnlyList<VtfResourceEntry> Resources { get; }

    /// <summary>Where the main image's mip chain starts in the file.</summary>
    public int ImageDataOffset { get; }

    /// <summary>The main image's format.</summary>
    public ImageFormat Format => (ImageFormat)Header.ImageFormat;

    /// <summary>The largest mip's width.</summary>
    public int Width => Header.Width;

    /// <summary>The largest mip's height.</summary>
    public int Height => Header.Height;

    /// <summary>How many mip levels the main image has.</summary>
    public int MipCount => Header.NumMipLevels;

    /// <summary>How many animation frames the texture holds.</summary>
    public int FrameCount => Math.Max(1, (int)Header.NumFrames);

    /// <summary>The depth of a volume texture, at least one.</summary>
    public int Depth => Math.Max(1, (int)Header.Depth);

    /// <summary>
    /// The texture's average colour, straight out of the header.
    /// </summary>
    /// <remarks>
    /// vbsp copies this into <c>dtexdata_t::reflectivity</c>, so every map's
    /// TEXDATA lump is a cross-check on this field's offset.
    /// </remarks>
    public Vec3 Reflectivity => Header.Reflectivity;

    /// <summary>
    /// Six when the texture is a cubemap, one otherwise.
    /// </summary>
    /// <remarks>
    /// Faces multiply into every mip's size, so this is not cosmetic. Versions
    /// 7.0 and 7.1 stored a SEVENTH "spheremap" face; those are older than
    /// anything this branch ships, and this port reads six and says so rather
    /// than silently mis-slicing them.
    /// </remarks>
    public int FaceCount => ((VtfFlags)Header.Flags).HasFlag(VtfFlags.EnvMap) ? 6 : 1;

    /// <summary>Reads a VTF from a stream.</summary>
    /// <param name="stream">The stream, read from its current position to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="InvalidVtfException">The bytes are not a VTF this port can read.</exception>
    public static async Task<VtfFile> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Parse(buffer.ToArray());
    }

    /// <summary>Parses a VTF already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="InvalidVtfException">The bytes are not a VTF this port can read.</exception>
    public static VtfFile Parse(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlySpan<byte> span = bytes.Span;
        int headerBytes = Unsafe.SizeOf<VtfHeader>();
        if (span.Length < 16)
        {
            throw new InvalidVtfException(
                $"a VTF is at least 16 bytes of base header; this is {span.Length}");
        }

        if (!span[..4].SequenceEqual(Signature))
        {
            throw new InvalidVtfException(
                $"the first four bytes are not \"VTF\\0\" but "
                + $"0x{Convert.ToHexString(span[..4])}");
        }

        int major = MemoryMarshal.Read<int>(span[4..]);
        int minor = MemoryMarshal.Read<int>(span[8..]);
        if (major != MajorVersion)
        {
            throw new InvalidVtfException(
                $"VTF major version {major} is not {MajorVersion}; vtf.h:441 knows only 7");
        }

        if (minor is < 0 or > 5)
        {
            throw new InvalidVtfException(
                $"VTF minor version {minor} is outside 7.0 to 7.5");
        }

        // Versions before 7.3 have no resource table and a shorter header, but
        // headerSize still says where the low-res image begins, so it is the
        // only field that has to be trusted.
        int headerSize = MemoryMarshal.Read<int>(span[12..]);
        if (headerSize < 16 || headerSize > span.Length)
        {
            throw new InvalidVtfException(
                $"the header claims to be {headerSize} bytes of a {span.Length}-byte file");
        }

        VtfHeader header = default;
        Span<byte> headerSpan = MemoryMarshal.AsBytes(new Span<VtfHeader>(ref header));
        span[..Math.Min(headerBytes, headerSize)].CopyTo(headerSpan);

        List<VtfResourceEntry> resources = [];
        if (minor >= 3 && header.NumResources > 0)
        {
            int table = headerBytes;
            int wanted = (int)header.NumResources * Unsafe.SizeOf<VtfResourceEntry>();
            if (header.NumResources > 32 || table + wanted > span.Length)
            {
                throw new InvalidVtfException(
                    $"the header declares {header.NumResources} resources, which do not fit "
                    + $"between offset {table} and the end of a {span.Length}-byte file "
                    + "(vtf.h:527 caps a dictionary at 32 entries)");
            }

            resources.AddRange(MemoryMarshal.Cast<byte, VtfResourceEntry>(span.Slice(table, wanted)));
        }

        int imageOffset = ImageOffset(header, resources, minor, headerSize);
        if (imageOffset > span.Length)
        {
            throw new InvalidVtfException(
                $"the image data starts at {imageOffset} of a {span.Length}-byte file");
        }

        return new VtfFile(bytes, header, resources, imageOffset);
    }

    private static int ImageOffset(
        VtfHeader header,
        List<VtfResourceEntry> resources,
        int minor,
        int headerSize)
    {
        // 7.3 and later: the image's location is in the resource table, which
        // is the whole reason the table exists. A file that declares resources
        // but not the image one is malformed rather than defaulted.
        foreach (VtfResourceEntry entry in resources)
        {
            if ((entry.Type & VtfResourceType.TypeMask) == VtfResourceType.Image)
            {
                return (int)entry.ResData;
            }
        }

        if (minor >= 3 && resources.Count > 0)
        {
            throw new InvalidVtfException(
                "the resource table names no image resource (VTF_LEGACY_RSRC_IMAGE, vtf.h:524)");
        }

        // Before 7.3, and for a 7.3+ file with an empty table, the layout is
        // fixed: header, then the low-res thumbnail, then the mip chain.
        ImageFormat lowFormat = (ImageFormat)header.LowResImageFormat;
        int lowSize = lowFormat == ImageFormat.Unknown
            ? 0
            : ImageFormatInfo.SizeInBytes(lowFormat, header.LowResImageWidth, header.LowResImageHeight);

        return headerSize + lowSize;
    }

    /// <summary>
    /// The size in bytes of one face of one frame at one mip level.
    /// </summary>
    /// <param name="mip">The mip level, 0 being the largest.</param>
    /// <returns>The size in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mip"/> is not a level of this texture.</exception>
    public int MipSize(int mip)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mip);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mip, Math.Max(1, MipCount));

        return ImageFormatInfo.SizeInBytes(Format, MipWidth(mip), MipHeight(mip));
    }

    /// <summary>The width of one mip level, never below one.</summary>
    /// <param name="mip">The mip level, 0 being the largest.</param>
    /// <returns>The width in pixels.</returns>
    public int MipWidth(int mip) => Math.Max(1, Width >> mip);

    /// <summary>The height of one mip level, never below one.</summary>
    /// <param name="mip">The mip level, 0 being the largest.</param>
    /// <returns>The height in pixels.</returns>
    public int MipHeight(int mip) => Math.Max(1, Height >> mip);

    /// <summary>
    /// The bytes of one face of one frame at one mip level.
    /// </summary>
    /// <param name="mip">The mip level, 0 being the largest.</param>
    /// <param name="frame">The animation frame.</param>
    /// <param name="face">The cubemap face, 0 for a flat texture.</param>
    /// <returns>A slice of the file, never a copy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Any index is out of range.</exception>
    /// <exception cref="InvalidVtfException">The slice runs off the end of the file.</exception>
    /// <remarks>
    /// The chain is stored SMALLEST mip first, and within a mip the order is
    /// frame, then face, then z slice. That ordering is why a texture's
    /// full-size image is at the END of the file and why streaming a coarse
    /// mip only needs the first few hundred bytes.
    /// </remarks>
    public ReadOnlyMemory<byte> MipData(int mip, int frame = 0, int face = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mip);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mip, Math.Max(1, MipCount));
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, FrameCount);
        ArgumentOutOfRangeException.ThrowIfNegative(face);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(face, FaceCount);

        int offset = ImageDataOffset;
        for (int level = MipCount - 1; level > mip; level--)
        {
            offset += ImageFormatInfo.SizeInBytes(Format, MipWidth(level), MipHeight(level))
                * FrameCount * FaceCount * Depth;
        }

        int sliceSize = ImageFormatInfo.SizeInBytes(Format, MipWidth(mip), MipHeight(mip));
        offset += ((frame * FaceCount * Depth) + (face * Depth)) * sliceSize;

        if (offset + sliceSize > _bytes.Length)
        {
            throw new InvalidVtfException(
                $"mip {mip} frame {frame} face {face} would span {offset}..{offset + sliceSize} "
                + $"of a {_bytes.Length}-byte file");
        }

        return _bytes.Slice(offset, sliceSize);
    }

    /// <summary>
    /// The total size of the main image: every mip, frame, face and slice.
    /// </summary>
    /// <returns>The size in bytes.</returns>
    public long ImageDataSize()
    {
        long total = 0;
        for (int level = 0; level < Math.Max(1, MipCount); level++)
        {
            total += (long)ImageFormatInfo.SizeInBytes(Format, MipWidth(level), MipHeight(level))
                * FrameCount * FaceCount * Depth;
        }

        return total;
    }

    /// <summary>
    /// One mip decoded to 8-bit RGBA, four bytes per pixel.
    /// </summary>
    /// <param name="mip">The mip level, 0 being the largest.</param>
    /// <param name="frame">The animation frame.</param>
    /// <param name="face">The cubemap face.</param>
    /// <returns>The pixels, row-major from the top-left.</returns>
    /// <exception cref="NotSupportedException">This port cannot decode the texture's format.</exception>
    public byte[] DecodeToRgba8888(int mip = 0, int frame = 0, int face = 0)
    {
        int width = MipWidth(mip);
        int height = MipHeight(mip);
        byte[] output = new byte[width * height * 4];
        ImageDecoder.Decode(Format, MipData(mip, frame, face).Span, width, height, output);
        return output;
    }
}

/// <summary>Thrown when a stream is not a VTF this port can read.</summary>
public sealed class InvalidVtfException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public InvalidVtfException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong with the file.</param>
    public InvalidVtfException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">What was wrong with the file.</param>
    /// <param name="innerException">The underlying failure.</param>
    public InvalidVtfException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
