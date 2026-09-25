using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// Reads a pakfile: the managed counterpart of the reference
/// implementation's <c>CZipFile::ParseFromBuffer</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reads the CENTRAL DIRECTORY and nothing else, which is what stock does and
/// is worth stating because it is how the format is really defined here. The
/// data offset for each entry is computed arithmetically from its central
/// header -- <c>relativeOffsetOfLocalHeader + 30 + fileNameLength +
/// extraFieldLength</c> -- and the LOCAL HEADER IS NEVER READ. Its signature is
/// not checked and its extra-field length is ASSUMED to equal the central one.
/// A pak whose two headers disagree reads as garbage in the reference build,
/// so this reader does the same arithmetic and then validates the local header
/// separately, reporting a mismatch rather than silently returning the wrong
/// bytes.
/// </para>
/// <para>
/// The directory is found by scanning BACKWARDS one byte at a time for the end
/// record's signature, starting 22 bytes from the end. There is no 64 KB cap
/// on how far back it looks and no offset is trusted to find it.
/// </para>
/// <para>
/// ORDER IS PRESERVED. Stock loses it -- entries are parsed in directory order
/// and inserted into a red-black tree that re-sorts them, so a load-then-save
/// through <c>CZipFile</c> generally REORDERS a pak. This reader keeps a list,
/// which is what makes a byte-exact round trip possible at all.
/// </para>
/// </remarks>
public sealed class ZipArchiveReader
{
    private ZipArchiveReader(IReadOnlyList<ZipEntry> entries, byte[] comment)
    {
        Entries = entries;
        Comment = comment;
    }

    /// <summary>The entries, in central-directory order.</summary>
    public IReadOnlyList<ZipEntry> Entries { get; }

    /// <summary>
    /// The bytes that followed the end-of-central-directory record.
    /// </summary>
    /// <remarks>
    /// Kept, rather than parsed and discarded, because it is not always the
    /// 32-byte <c>"XZP1 0"</c> string today's writer emits: a stock HL2DM map
    /// such as <c>dm_lockdown.bsp</c> has no comment at all. Carrying it is
    /// what makes a byte-exact round trip of shipped content possible. Stock
    /// reads it only to recover an alignment value
    /// (<c>ParseXZipCommentString</c>).
    /// </remarks>
    public byte[] Comment { get; }

    /// <summary>Reads a pak from a stream.</summary>
    /// <param name="stream">The bytes of the pak, read to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed pak.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidZipException">The pak is malformed.</exception>
    public static async Task<ZipArchiveReader> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return await ParseAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a pak already in memory.</summary>
    /// <param name="bytes">The whole pak -- exactly the contents of lump 40.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed pak.</returns>
    /// <exception cref="InvalidZipException">The pak is malformed.</exception>
    public static ValueTask<ZipArchiveReader> ParseAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ValueTask.FromResult(Parse(bytes.Span, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromCanceled<ZipArchiveReader>(cancellationToken);
        }
    }

    /// <summary>
    /// Finds an entry by name, case-insensitively as the reference build looks
    /// it up.
    /// </summary>
    /// <param name="name">The path inside the pak.</param>
    /// <returns>The entry, or null.</returns>
    /// <remarks>
    /// <c>ReadFileFromZip</c> lower-cases the name before looking it up, and so
    /// does <c>FileExistsInZip</c>, against names that were themselves
    /// lower-cased on the way in.
    /// </remarks>
    public ZipEntry? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string folded = name.ToLowerInvariant();
        return Entries.FirstOrDefault(e => e.Name == folded);
    }

    /// <summary>
    /// Re-encodes this pak, preserving entry order and metadata.
    /// </summary>
    /// <returns>A writer loaded with the same entries.</returns>
    /// <remarks>
    /// The round-trip gate: read a pak, call this, write it, and the bytes
    /// match. An LZMA entry survives it -- its payload is carried through
    /// verbatim -- because re-encoding does not require decoding.
    /// </remarks>
    public ZipArchiveWriter ToWriter()
    {
        ZipArchiveWriter writer = new() { Comment = Comment };
        foreach (ZipEntry entry in Entries)
        {
            writer.Add(entry);
        }

        return writer;
    }

    private static ZipArchiveReader Parse(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        // The one size sanity check either reference parser has.
        if (bytes.Length < ZipFormat.EndOfCentralDirectorySize)
        {
            throw new InvalidZipException(
                $"pak is shorter than an end-of-central-directory record ({bytes.Length} bytes)");
        }

        int recordOffset = FindEndOfCentralDirectory(bytes);
        if (recordOffset < 0)
        {
            // The reference build asserts here in debug and, in release, falls
            // through with a zeroed record and returns silently. A library that
            // returned an empty pak for a corrupt one would hide the corruption
            // from every caller, so this reports it.
            throw new InvalidZipException("no end-of-central-directory record found");
        }

        ReadOnlySpan<byte> record = bytes[recordOffset..];
        ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        ushort zipCommentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);

        int commentStart = recordOffset + ZipFormat.EndOfCentralDirectorySize;
        int commentBytes = Math.Min(zipCommentLength, bytes.Length - commentStart);
        byte[] comment = commentBytes > 0
            ? bytes.Slice(commentStart, commentBytes).ToArray()
            : [];

        // Zero entries is not an error, just an empty pak. Every BSP without
        // embedded content has one.
        if (entryCount == 0)
        {
            return new ZipArchiveReader([], comment);
        }

        if (directoryOffset > (uint)bytes.Length)
        {
            throw new InvalidZipException(
                $"central directory offset {directoryOffset} is past the end of the pak");
        }

        List<ZipEntry> entries = new(entryCount);
        int cursor = (int)directoryOffset;

        for (int i = 0; i < entryCount; i++)
        {
            if ((i & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (cursor + ZipFormat.CentralDirectoryHeaderSize > bytes.Length)
            {
                throw new InvalidZipException(
                    $"central directory entry {i} runs past the end of the pak");
            }

            ReadOnlySpan<byte> header = bytes.Slice(cursor, ZipFormat.CentralDirectoryHeaderSize);

            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (signature != ZipFormat.CentralDirectoryHeaderSignature)
            {
                // The reference build asserts here in debug only, but its
                // from-disk parser rejects the whole file. The stricter of the
                // two is the right behaviour for a library.
                throw new InvalidZipException(
                    $"central directory entry {i} has signature 0x{signature:X8}, expected 0x{ZipFormat.CentralDirectoryHeaderSignature:X8}");
            }

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            uint localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);

            if (method != (ushort)ZipCompressionMethod.Store &&
                method != (ushort)ZipCompressionMethod.Lzma)
            {
                // The reference build's from-disk parser rejects the whole file.
                // Its buffer parser only warns, but then hands the bogus method
                // to a read that calls Error() and aborts, so continuing buys
                // nothing.
                throw new InvalidZipException(
                    $"entry {i} uses unsupported compression method {method}; a Source pak may only use 0 (store) or 14 (LZMA)");
            }

            cursor += ZipFormat.CentralDirectoryHeaderSize;
            if (cursor + nameLength > bytes.Length)
            {
                throw new InvalidZipException($"central directory entry {i} has a truncated name");
            }

            string name = Encoding.Latin1.GetString(bytes.Slice(cursor, nameLength));
            cursor += nameLength + extraLength + commentLength;

            // The reference arithmetic, verbatim. The local header's own
            // extraFieldLength is not consulted.
            long dataOffset = localOffset + ZipFormat.LocalFileHeaderSize + nameLength + extraLength;

            if (dataOffset < 0 || dataOffset + compressedSize > bytes.Length)
            {
                throw new InvalidZipException(
                    $"entry '{name}' claims {compressedSize} bytes at offset {dataOffset}, past the end of the pak");
            }

            // Not in stock, and deliberately so: stock never looks at the local
            // header, which means a pak whose two headers disagree silently
            // yields wrong bytes. Checking is cheap and the diagnostic is worth
            // far more than the bug-for-bug fidelity would be.
            if (localOffset + 4 <= (uint)bytes.Length)
            {
                uint localSignature = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes[(int)localOffset..]);
                if (localSignature != ZipFormat.LocalFileHeaderSignature)
                {
                    throw new InvalidZipException(
                        $"entry '{name}' points at 0x{localSignature:X8} where a local file header should be");
                }
            }

            byte[] data = bytes.Slice((int)dataOffset, (int)compressedSize).ToArray();

            entries.Add(new ZipEntry(
                name,
                data,
                (ZipCompressionMethod)method,
                crc,
                uncompressedSize));
        }

        return new ZipArchiveReader(entries, comment);
    }

    /// <summary>
    /// The backward scan the reference build uses to find the end record.
    /// </summary>
    private static int FindEndOfCentralDirectory(ReadOnlySpan<byte> bytes)
    {
        for (int offset = bytes.Length - ZipFormat.EndOfCentralDirectorySize; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) ==
                ZipFormat.EndOfCentralDirectorySignature)
            {
                return offset;
            }
        }

        return -1;
    }
}
