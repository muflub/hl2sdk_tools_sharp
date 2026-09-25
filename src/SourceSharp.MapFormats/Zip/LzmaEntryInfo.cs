using System.Buffers.Binary;

namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// What can be said about an LZMA pak entry without decoding it.
/// </summary>
/// <param name="SdkVersionMajor">
/// The LZMA SDK major version the compressor wrote
/// (<c>src/public/zip_utils.cpp:1046</c>).
/// </param>
/// <param name="SdkVersionMinor">
/// The minor version (<c>zip_utils.cpp:1047</c>).
/// </param>
/// <param name="PropertiesSize">
/// The declared properties size. Always 5 when the entry is well-formed; the
/// engine's own decoder rejects anything else
/// (<c>src/tier1/lzmaDecoder.cpp:376-382</c>).
/// </param>
/// <param name="UncompressedSize">
/// The uncompressed length, taken from the ZIP header rather than from the LZMA
/// stream -- the ZIP framing carries no size of its own.
/// </param>
/// <remarks>
/// <para>
/// The single most useful fact about this framing: a pak's LZMA entry is NOT
/// Valve's own <c>lzma_header_t</c>. That structure -- the <c>'LZMA'</c> magic,
/// an <c>actualSize</c> and an <c>lzmaSize</c>, then five properties bytes,
/// <c>src/public/tier1/lzmaDecoder.h:21-34</c> -- is what Valve uses
/// everywhere ELSE. The zip writer strips it off and substitutes the ZIP
/// specification's own 5.8.8 framing
/// (<c>src/public/zip_utils.cpp:1034-1061</c>):
/// </para>
/// <code>
/// byte 0    LZMA SDK major version
/// byte 1    LZMA SDK minor version
/// bytes 2-3 uint16 little-endian properties size, always 5
/// bytes 4-8 the five raw LZMA properties bytes
/// bytes 9+  the raw LZMA stream, with no end marker and no size field
/// </code>
/// <para>
/// So there is no <c>LZMA</c> magic to look for, and a reader that expects
/// <c>lzma_header_t</c> misreads the first four bytes as a version number.
/// </para>
/// </remarks>
public sealed record LzmaEntryInfo(
    byte SdkVersionMajor,
    byte SdkVersionMinor,
    ushort PropertiesSize,
    uint UncompressedSize)
{
    /// <summary>
    /// The size of the ZIP-5.8.8 preamble: two version bytes, a two-byte size,
    /// and five properties bytes (<c>src/public/zip_utils.cpp:1039</c>).
    /// </summary>
    public const int HeaderSize = 2 + 2 + 5;

    /// <summary>
    /// <c>LZMA_PROPS_SIZE</c>: the only properties size the engine's decoder
    /// accepts (<c>src/tier1/lzmaDecoder.cpp:376-382</c>).
    /// </summary>
    public const int ExpectedPropertiesSize = 5;

    /// <summary>
    /// Reads what the framing says about an LZMA entry.
    /// </summary>
    /// <param name="entry">The entry, which must be LZMA-compressed.</param>
    /// <returns>The framing's fields.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    /// <exception cref="InvalidZipException">
    /// The entry is not LZMA-compressed, or its payload is too short to carry
    /// the framing.
    /// </exception>
    public static LzmaEntryInfo From(ZipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.CompressionMethod != ZipCompressionMethod.Lzma)
        {
            throw new InvalidZipException(
                $"entry '{entry.Name}' is not LZMA-compressed");
        }

        if (entry.Data.Length < HeaderSize)
        {
            throw new InvalidZipException(
                $"entry '{entry.Name}' is LZMA but its payload is {entry.Data.Length} bytes, too short for the {HeaderSize}-byte ZIP 5.8.8 header");
        }

        return new LzmaEntryInfo(
            entry.Data[0],
            entry.Data[1],
            BinaryPrimitives.ReadUInt16LittleEndian(entry.Data.AsSpan(2)),
            entry.UncompressedSize);
    }

    /// <summary>
    /// Whether the properties size is the one the engine's decoder requires.
    /// </summary>
    public bool IsWellFormed => PropertiesSize == ExpectedPropertiesSize;

    /// <summary>
    /// A diagnostic a caller can show instead of mis-reading the entry.
    /// </summary>
    /// <param name="name">The entry's name.</param>
    /// <returns>The message.</returns>
    /// <remarks>
    /// Deliberately a message and not a decode. A hand-written LZMA decoder is
    /// a large, fiddly, correctness-critical piece of code that this port does
    /// not need: the map compilers never WRITE a compressed entry -- without
    /// <c>ZIP_SUPPORT_LZMA_ENCODE</c>, <c>AddBufferToZip</c> calls
    /// <c>Error()</c> for anything but method 0
    /// (<c>src/public/zip_utils.cpp:1066-1070</c>) -- so reading one is needed
    /// only for a pak some other tool produced.
    /// </remarks>
    public string Describe(string name) =>
        IsWellFormed
            ? $"entry '{name}' is LZMA-compressed (ZIP spec 5.8.8 framing, SDK {SdkVersionMajor}.{SdkVersionMinor}, {UncompressedSize} bytes uncompressed); this reader stores only, so its contents are not available"
            : $"entry '{name}' claims LZMA with a properties size of {PropertiesSize}, but the engine's own decoder accepts only {ExpectedPropertiesSize}, so this entry would not load in the game either";
}
