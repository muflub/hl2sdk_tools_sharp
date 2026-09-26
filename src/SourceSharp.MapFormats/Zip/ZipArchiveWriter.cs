//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// Writes a STORE-only pakfile, byte for byte as
/// <c>CZipFile::SaveDirectory</c> does in the reference implementation.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than <see cref="System.IO.Compression"/>: see
/// <see cref="ZipCompressionMethod"/> for the reason, which is that the format
/// accepts no other method and the framework can only write one it rejects.
/// </para>
/// <para>
/// Three things a correct general-purpose zip writer would get wrong here:
/// </para>
/// <list type="number">
/// <item><description>
/// The 32-byte <c>"XZP1 0"</c> comment is mandatory in the reference
/// writer. Without it the file is a valid zip and is
/// not a byte-identical pak.
/// </description></item>
/// <item><description>
/// Offsets are relative to the START OF THE PAK, not to the start of the
/// stream, because the pak is
/// embedded in a BSP at a non-zero file offset.
/// </description></item>
/// <item><description>
/// Zero-length entries are silently DROPPED -- both write loops of the
/// reference writer skip an entry
/// whose compressed size is not positive, and
/// the directory count counts only what was written. An empty
/// file put into a pak does not come out of it.
/// </description></item>
/// </list>
/// <para>
/// Entry ORDER is the caller's. The reference writer's order is not
/// reproducible and is not
/// worth reproducing: its entries live in a <c>CUtlRBTree</c> keyed on a
/// <c>CUtlSymbol</c>, and <c>CUtlSymbol</c> has no
/// <c>operator&lt;</c> -- it converts to its <c>UtlSymId_t</c>, so the
/// comparison orders by the id each name was assigned when it
/// was first interned ANYWHERE in the process, a global counter shared with
/// every other symbol user in the writer. That is neither alphabetical nor
/// insertion
/// order and cannot be derived from the file list. So this writer emits what it
/// is given, in that order, and the round-trip gate is that a pak read and
/// written back is byte-identical -- which holds for any order, the
/// reference writer's
/// included.
/// </para>
/// </remarks>
public sealed class ZipArchiveWriter
{
    private readonly List<ZipEntry> _entries = [];
    private byte[] _comment = BuildComment();

    /// <summary>The entries added so far, in the order they will be written.</summary>
    public IReadOnlyList<ZipEntry> Entries => _entries;

    /// <summary>
    /// The zip comment, written after the end-of-central-directory record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to the 32-byte <c>"XZP1 0"</c> string the reference writer
    /// emits unconditionally (its <c>MakeXZipCommentString</c> equivalent) --
    /// so a pak this library creates looks like one the reference writer builds.
    /// </para>
    /// <para>
    /// It is settable, and that is not a convenience: real shipped maps do not
    /// all have it. <c>game/mod_sharp/maps/dm_lockdown.bsp</c> -- a stock HL2DM
    /// map -- carries 595 stored entries and a comment length of ZERO, because
    /// the writer that built it predates the XZIP work. A writer that always
    /// emitted the comment could not round-trip that map, and round-tripping
    /// real shipped content is the point.
    /// </para>
    /// </remarks>
    public byte[] Comment
    {
        get => _comment;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value.Length,
                    "the comment length field is 16 bits");
            }

            _comment = value;
        }
    }

    /// <summary>Appends an entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    public void Add(ZipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
    }

    /// <summary>Appends a stored file.</summary>
    /// <param name="name">The path inside the pak.</param>
    /// <param name="data">The file's bytes.</param>
    /// <returns>The entry that was added.</returns>
    public ZipEntry Add(string name, byte[] data)
    {
        ZipEntry entry = new(name, data);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Writes the pak to a stream.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when everything is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidZipException">
    /// An entry uses a method this writer cannot emit.
    /// </exception>
    public async Task WriteAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] bytes = ToBytes();
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pak's bytes, exactly as <see cref="WriteAsync"/> would write them.
    /// </summary>
    /// <returns>The encoded pak.</returns>
    /// <exception cref="InvalidZipException">
    /// An entry uses a method this writer cannot emit.
    /// </exception>
    public byte[] ToBytes()
    {
        using MemoryStream output = new();

        // Local headers and payloads, in the caller's order.
        List<(ZipEntry Entry, uint Offset)> written = [];

        foreach (ZipEntry entry in _entries)
        {
            // An entry with nothing in it is skipped entirely, and
            // does not appear in the directory or the count either.
            if (entry.Data.Length <= 0)
            {
                continue;
            }

            uint offset = (uint)output.Position;
            written.Add((entry, offset));

            byte[] name = Encoding.Latin1.GetBytes(entry.Name);
            byte[] header = new byte[ZipFormat.LocalFileHeaderSize];

            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(0), ZipFormat.LocalFileHeaderSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(
                header.AsSpan(4), VersionNeeded(entry));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0);   // flags
            BinaryPrimitives.WriteUInt16LittleEndian(
                header.AsSpan(8), (ushort)entry.CompressionMethod);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 0);  // time
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 0);  // date
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(18), (uint)entry.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), entry.UncompressedSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)name.Length);

            // The padding field: the reference writer's alignment calc returns
            // 0 whenever the alignment size is 0, and it always is on the PC.
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0);

            output.Write(header);

            // The name is written WITHOUT a terminator; the length
            // field excludes it.
            output.Write(name);
            output.Write(entry.Data);
        }

        // The central directory.
        uint centralDirectoryStart = (uint)output.Position;

        foreach ((ZipEntry entry, uint offset) in written)
        {
            byte[] name = Encoding.Latin1.GetBytes(entry.Name);
            byte[] header = new byte[ZipFormat.CentralDirectoryHeaderSize];

            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(0), ZipFormat.CentralDirectoryHeaderSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(
                header.AsSpan(4), ZipFormat.VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(
                header.AsSpan(6), VersionNeeded(entry));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 0);   // flags
            BinaryPrimitives.WriteUInt16LittleEndian(
                header.AsSpan(10), (ushort)entry.CompressionMethod);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 0);  // time
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 0);  // date
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(20), (uint)entry.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), entry.UncompressedSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), 0);  // extra field
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 0);  // comment
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 0);  // disk
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(36), 0);  // internal
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(38), 0);  // external
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42), offset);

            output.Write(header);
            output.Write(name);
        }

        uint centralDirectoryEnd = (uint)output.Position;

        // The end record and the comment.
        byte[] record = new byte[ZipFormat.EndOfCentralDirectorySize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(0), ZipFormat.EndOfCentralDirectorySignature);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), (ushort)written.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), (ushort)written.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(12), centralDirectoryEnd - centralDirectoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), centralDirectoryStart);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), (ushort)_comment.Length);

        output.Write(record);
        output.Write(_comment);

        return output.ToArray();
    }

    /// <summary>
    /// The 32-byte XZIP comment a PC pak ends with, as the reference
    /// writer's <c>MakeXZipCommentString</c> builds it.
    /// </summary>
    /// <returns>The comment bytes.</returns>
    public static byte[] BuildComment()
    {
        byte[] comment = new byte[ZipFormat.CommentLength];
        Encoding.Latin1.GetBytes(ZipFormat.PcComment).CopyTo(comment, 0);
        return comment;
    }

    private static ushort VersionNeeded(ZipEntry entry) => entry.CompressionMethod switch
    {
        ZipCompressionMethod.Store => ZipFormat.VersionNeededToExtractStore,
        ZipCompressionMethod.Lzma => ZipFormat.VersionNeededToExtractLzma,
        _ => throw new InvalidZipException(
            $"calling AddBufferToZip with unknown compression type {(int)entry.CompressionMethod}"),
    };
}
