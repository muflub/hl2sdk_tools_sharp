using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SourceSharp.MapFormats.Assets;

/// <summary>
/// A <c>.phy</c> file's header (<c>src/public/phyfile.h:14</c>,
/// <c>struct phyheader_t</c>). 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PhyHeader
{
    /// <summary>
    /// The header's own size in bytes, so the solids can be found without
    /// knowing this struct's length.
    /// </summary>
    public int Size;

    /// <summary>The format id; zero in every file this branch writes.</summary>
    public int Id;

    /// <summary>How many solids follow the header.</summary>
    public int SolidCount;

    /// <summary>
    /// The source MDL's checksum. Must equal <see cref="MdlFile.Checksum"/>,
    /// which is how a stale PHY is caught.
    /// </summary>
    public int CheckSum;
}

/// <summary>
/// Where one solid's bytes sit inside a <c>.phy</c>.
/// </summary>
/// <param name="Offset">The byte offset of the solid's PAYLOAD, past its size word.</param>
/// <param name="Length">The payload's length in bytes.</param>
public readonly record struct PhySolid(int Offset, int Length);

/// <summary>
/// A <c>.phy</c> file, framed but not decoded.
/// </summary>
/// <remarks>
/// <para>
/// FRAMING ONLY, deliberately. A solid's payload is an IVP compact ledge tree
/// in Havok's own format, and stock vrad does not decode it either: it hands
/// the bytes to vphysics' <c>CreatePolySoup</c> / <c>CreateVirtualMesh</c> and
/// asks the library for the triangles. Reimplementing the ledge decoder here
/// would be a second, divergent implementation of a format this project does
/// not own.
/// </para>
/// <para>
/// So what this class provides is exactly what the compilers need in managed
/// code: the header, the per-solid byte ranges, and the trailing text key
/// data. The solids go to the library.
/// </para>
/// <para>
/// The layout is: <see cref="PhyHeader"/>, then for each solid an
/// <c>int</c> size followed by that many bytes, then the key data as text to
/// the end of the file. The size word is INSIDE the solid's own accounting in
/// vphysics but is stripped here, so <see cref="PhySolid.Offset"/> points at
/// the payload.
/// </para>
/// </remarks>
public sealed class PhyFile
{
    private readonly ReadOnlyMemory<byte> _bytes;

    private PhyFile(
        ReadOnlyMemory<byte> bytes,
        PhyHeader header,
        IReadOnlyList<PhySolid> solids,
        int keyDataOffset)
    {
        _bytes = bytes;
        Header = header;
        Solids = solids;
        KeyDataOffset = keyDataOffset;
    }

    /// <summary>The file header.</summary>
    public PhyHeader Header { get; }

    /// <summary>Where each solid's payload sits, in file order.</summary>
    public IReadOnlyList<PhySolid> Solids { get; }

    /// <summary>Where the trailing text key data begins.</summary>
    public int KeyDataOffset { get; }

    /// <summary>The MDL checksum this file was built against.</summary>
    public int Checksum => Header.CheckSum;

    /// <summary>Reads a PHY from a stream.</summary>
    /// <param name="stream">The stream, read from its current position to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The framed file.</returns>
    /// <exception cref="InvalidStudioException">The bytes are not a PHY this port can frame.</exception>
    public static async Task<PhyFile> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Parse(buffer.ToArray());
    }

    /// <summary>Frames a PHY already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <returns>The framed file.</returns>
    /// <exception cref="InvalidStudioException">The bytes are not a PHY this port can frame.</exception>
    public static PhyFile Parse(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlySpan<byte> span = bytes.Span;
        int headerSize = Unsafe.SizeOf<PhyHeader>();
        if (span.Length < headerSize)
        {
            throw new InvalidStudioException(
                $"a PHY is at least {headerSize} bytes of header; this is {span.Length}");
        }

        PhyHeader header = MemoryMarshal.Read<PhyHeader>(span);

        // phyheader_t::size is the header's OWN length, which is how the
        // format would have grown without breaking old files. Trust it rather
        // than sizeof(), but refuse a value that cannot be one.
        if (header.Size < headerSize || header.Size > span.Length)
        {
            throw new InvalidStudioException(
                $"the PHY header declares itself {header.Size} bytes; the struct is "
                + $"{headerSize} and the file is {span.Length}");
        }

        if (header.SolidCount < 0)
        {
            throw new InvalidStudioException(
                $"the PHY declares {header.SolidCount} solids");
        }

        List<PhySolid> solids = new(header.SolidCount);
        int at = header.Size;
        for (int i = 0; i < header.SolidCount; i++)
        {
            if (at + sizeof(int) > span.Length)
            {
                throw new InvalidStudioException(
                    $"solid {i} of {header.SolidCount} has no size word: offset {at} of "
                    + $"{span.Length}");
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(span[at..]);
            at += sizeof(int);
            if (size < 0 || at + size > span.Length)
            {
                throw new InvalidStudioException(
                    $"solid {i} declares {size} bytes at offset {at} of {span.Length}");
            }

            solids.Add(new PhySolid(at, size));
            at += size;
        }

        return new PhyFile(bytes, header, solids, at);
    }

    /// <summary>One solid's undecoded bytes.</summary>
    /// <param name="index">The solid's index.</param>
    /// <returns>A slice of the file, never a copy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    /// <remarks>
    /// These are handed to vphysics. Nothing in this port interprets them.
    /// </remarks>
    public ReadOnlyMemory<byte> SolidData(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Solids.Count);

        PhySolid solid = Solids[index];
        return _bytes.Slice(solid.Offset, solid.Length);
    }

    /// <summary>
    /// The trailing key data text: the surface properties and mass the model's
    /// solids carry.
    /// </summary>
    /// <returns>The text, up to the first NUL or the end of the file.</returns>
    public string KeyData()
    {
        if (KeyDataOffset >= _bytes.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> tail = _bytes.Span[KeyDataOffset..];
        int end = tail.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? tail : tail[..end]);
    }

    /// <summary>
    /// Frames the solids of one LUMP_PHYSCOLLIDE record, which uses the same
    /// per-solid size words as a <c>.phy</c>.
    /// </summary>
    /// <param name="bytes">The record's solid bytes, starting at the first size word.</param>
    /// <param name="solidCount">How many solids <c>dphysmodel_t::solidCount</c> declares.</param>
    /// <returns>Where each solid's payload sits, relative to <paramref name="bytes"/>.</returns>
    /// <exception cref="InvalidStudioException">The framing runs off the end.</exception>
    /// <remarks>
    /// The BSP's collision lump is a <c>dphysmodel_t</c> followed by exactly
    /// this framing and then the same text key data, which makes a compiled
    /// map a real specimen for this code without a single <c>.phy</c> on disk.
    /// </remarks>
    public static IReadOnlyList<PhySolid> FrameSolids(ReadOnlySpan<byte> bytes, int solidCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(solidCount);

        List<PhySolid> solids = new(solidCount);
        int at = 0;
        for (int i = 0; i < solidCount; i++)
        {
            if (at + sizeof(int) > bytes.Length)
            {
                throw new InvalidStudioException(
                    $"solid {i} of {solidCount} has no size word: offset {at} of {bytes.Length}");
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);
            at += sizeof(int);
            if (size < 0 || at + size > bytes.Length)
            {
                throw new InvalidStudioException(
                    $"solid {i} declares {size} bytes at offset {at} of {bytes.Length}");
            }

            solids.Add(new PhySolid(at, size));
            at += size;
        }

        return solids;
    }
}
