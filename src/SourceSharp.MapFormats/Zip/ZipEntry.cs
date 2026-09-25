namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// One file in a pakfile.
/// </summary>
/// <remarks>
/// <para>
/// Names are lower case, forward-slashed, relative, and carry no leading slash.
/// The writer lower-cases unconditionally
/// (<c>src/public/zip_utils.cpp:995-998</c>) and so do both readers
/// (<c>:709</c>, <c>:875</c>) and every lookup (<c>:1144-1147</c>,
/// <c>:1227-1230</c>) -- so a pak is a case-insensitive namespace stored in
/// lower case. The forward slashes come from the callers rather than from the
/// zip code: <c>AddDirToPak</c> composes <c>"%s/%s"</c>
/// (<c>src/utils/common/bsplib.cpp:870-880</c>).
/// </para>
/// <para>
/// One asymmetry NOT reproduced: <c>RemoveFileFromZip</c>
/// (<c>zip_utils.cpp:1270-1274</c>) does not lower-case its argument, so
/// removing an entry by a mixed-case name silently fails in stock. That is a
/// bug in an operation this port does not need to have, and reproducing it
/// would only propagate it.
/// </para>
/// </remarks>
public sealed class ZipEntry
{
    /// <summary>Creates a stored entry.</summary>
    /// <param name="name">The path inside the pak. Lower-cased on construction.</param>
    /// <param name="data">The file's bytes.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="data"/> is null.
    /// </exception>
    public ZipEntry(string name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);

        Name = name.ToLowerInvariant();
        Data = data;
        CompressionMethod = ZipCompressionMethod.Store;
        UncompressedSize = (uint)data.Length;
        Crc = Crc32.Compute(data);
    }

    /// <summary>Creates an entry read from a pak, with its stored metadata.</summary>
    /// <param name="name">The path inside the pak.</param>
    /// <param name="data">
    /// The STORED bytes -- still compressed when
    /// <paramref name="compressionMethod"/> is not
    /// <see cref="ZipCompressionMethod.Store"/>.
    /// </param>
    /// <param name="compressionMethod">The method the directory declared.</param>
    /// <param name="crc">The CRC the directory declared.</param>
    /// <param name="uncompressedSize">The uncompressed size the directory declared.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="data"/> is null.
    /// </exception>
    public ZipEntry(
        string name,
        byte[] data,
        ZipCompressionMethod compressionMethod,
        uint crc,
        uint uncompressedSize)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);

        Name = name.ToLowerInvariant();
        Data = data;
        CompressionMethod = compressionMethod;
        Crc = crc;
        UncompressedSize = uncompressedSize;
    }

    /// <summary>The path inside the pak, lower case with forward slashes.</summary>
    public string Name { get; }

    /// <summary>
    /// The bytes as stored. For <see cref="ZipCompressionMethod.Store"/> this
    /// is the file; for <see cref="ZipCompressionMethod.Lzma"/> it is the
    /// compressed payload in its ZIP-5.8.8 framing, which this port does not
    /// decode.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>The compression method.</summary>
    public ZipCompressionMethod CompressionMethod { get; }

    /// <summary>
    /// The CRC-32 of the UNCOMPRESSED bytes. Computed before compression
    /// (<c>src/public/zip_utils.cpp:1017-1021</c>), so it is a checksum of the
    /// file rather than of the payload.
    /// </summary>
    /// <remarks>
    /// Nothing in Source ever verifies it. It is stored at
    /// <c>zip_utils.cpp:740</c> and <c>:882</c> and re-emitted on save, and
    /// there is no comparison against the data anywhere.
    /// </remarks>
    public uint Crc { get; }

    /// <summary>The uncompressed length.</summary>
    public uint UncompressedSize { get; }

    /// <summary>
    /// Whether this entry's bytes can be handed back as the file's contents.
    /// </summary>
    /// <remarks>
    /// False only for LZMA. See <see cref="ZipArchiveReader"/> for why that is
    /// surfaced rather than decoded.
    /// </remarks>
    public bool IsStored => CompressionMethod == ZipCompressionMethod.Store;
}
