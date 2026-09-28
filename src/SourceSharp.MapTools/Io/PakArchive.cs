//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// The zip in a BSP's <c>PAKFILE</c> lump, read so its contents can be mounted
/// as content.
/// </summary>
/// <remarks>
/// <para>
/// READ side only, and only what mounting needs: the end-of-central-directory
/// record, the central directory, and one local header per entry read. Phase
/// 1b owns the pak WRITER — the alignment rule, PAKFILE-last, the STORE-only
/// round trip that <c>bspzip -dir</c> has to agree with — and if it wants this
/// reader replaced by its own it can implement <see cref="IPackedArchive"/> and
/// every mount keeps working unchanged. That is what the interface is for.
/// </para>
/// <para>
/// STORE and deflate both, though vbsp writes STORE: a pak that a modder built
/// with a normal zip tool still has to mount, and <c>DeflateStream</c> is in the
/// BCL, so supporting it costs four lines and removes a confusing failure.
/// </para>
/// <para>
/// Over a buffer rather than a stream because the lump is already in memory by
/// the time anything wants to mount it — the BSP reader has just handed it over.
/// </para>
/// </remarks>
public sealed class PakArchive : IPackedArchive
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint CentralDirectorySignature = 0x02014b50;
    private const uint LocalHeaderSignature = 0x04034b50;
    private const int EndOfCentralDirectoryLength = 22;
    private const ushort MethodStore = 0;
    private const ushort MethodDeflate = 8;

    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly Dictionary<VPath, Entry> _entries;
    private readonly IDisposable? _owner;

    private PakArchive(
        string name,
        ReadOnlyMemory<byte> bytes,
        Dictionary<VPath, Entry> entries,
        IDisposable? owner)
    {
        Name = name;
        _bytes = bytes;
        _entries = entries;
        _owner = owner;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<VPath> Paths => _entries.Keys;

    /// <summary>How many files the pak holds.</summary>
    public int Count => _entries.Count;

    /// <summary>Reads the central directory of a pak already in memory.</summary>
    /// <param name="bytes">The lump's bytes. Not copied; they must outlive the archive.</param>
    /// <param name="name">A description of where it came from, for diagnostics.</param>
    /// <returns>The archive.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a readable zip.</exception>
    public static PakArchive Open(ReadOnlyMemory<byte> bytes, string name) =>
        Open(bytes, name, owner: null);

    /// <summary>Reads the central directory of a pak, taking ownership of its storage.</summary>
    /// <param name="owner">The storage the bytes live in; disposed with the archive.</param>
    /// <param name="name">A description of where it came from, for diagnostics.</param>
    /// <returns>The archive.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a readable zip.</exception>
    public static PakArchive Open(IMemoryOwner<byte> owner, string name)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Open(owner.Memory, name, owner);
    }

    /// <inheritdoc />
    public ValueTask<IMemoryOwner<byte>?> ReadAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(path, out Entry entry))
        {
            return ValueTask.FromResult<IMemoryOwner<byte>?>(null);
        }

        return ValueTask.FromResult<IMemoryOwner<byte>?>(ReadEntry(path, entry));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A STORE entry -- which is every entry vbsp writes -- is sliced straight
    /// out of the lump, so the range costs only its own bytes.
    /// </para>
    /// <para>
    /// A deflated entry is inflated whole and the range copied out of it. A
    /// deflate stream cannot seek, so the bytes before the range have to be
    /// inflated anyway, and inflating the rest too keeps the one check a
    /// whole read makes -- that the entry inflates to exactly the length its
    /// directory entry declares -- rather than a ranged read accepting an
    /// entry a whole read rejects. So does a STORE entry whose two lengths
    /// disagree, for the same reason: it is malformed, and it fails or
    /// succeeds exactly as a whole read of it would.
    /// </para>
    /// </remarks>
    public ValueTask<FileRange?> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(path, out Entry entry))
        {
            return ValueTask.FromResult<FileRange?>(null);
        }

        if (entry.Method == MethodStore && entry.CompressedLength == entry.Length)
        {
            ReadOnlySpan<byte> stored = DataOf(path, entry);
            return ValueTask.FromResult<FileRange?>(FileRange.Copy(stored, offset, length));
        }

        using PooledMemoryOwner whole = ReadEntry(path, entry);
        return ValueTask.FromResult<FileRange?>(FileRange.Copy(whole.Memory.Span, offset, length));
    }

    private PooledMemoryOwner ReadEntry(VPath path, Entry entry)
    {
        ReadOnlySpan<byte> compressed = DataOf(path, entry);
        PooledMemoryOwner result = PooledMemoryOwner.Rent(entry.Length);

        try
        {
            if (entry.Method == MethodStore)
            {
                compressed.CopyTo(result.Memory.Span);
            }
            else
            {
                Inflate(compressed, result.Memory.Span, path);
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// An entry's stored bytes, after its local header, with every bound a
    /// read depends on checked.
    /// </summary>
    private ReadOnlySpan<byte> DataOf(VPath path, Entry entry)
    {
        ReadOnlySpan<byte> all = _bytes.Span;

        if (entry.LocalHeaderOffset + 30 > all.Length)
        {
            throw new InvalidDataException(
                $"{Name}: the local header for {path} is past the end of the pak");
        }

        ReadOnlySpan<byte> local = all[entry.LocalHeaderOffset..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalHeaderSignature)
        {
            throw new InvalidDataException(
                $"{Name}: {path} does not begin with a local file header");
        }

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(local[26..]);
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(local[28..]);
        int dataOffset = entry.LocalHeaderOffset + 30 + nameLength + extraLength;

        if (dataOffset + entry.CompressedLength > all.Length)
        {
            throw new InvalidDataException(
                $"{Name}: {path} claims {entry.CompressedLength} bytes at offset {dataOffset}, past the end of a {all.Length}-byte pak");
        }

        return all.Slice(dataOffset, entry.CompressedLength);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _owner?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static PakArchive Open(ReadOnlyMemory<byte> bytes, string name, IDisposable? owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        try
        {
            return new PakArchive(name, bytes, ReadCentralDirectory(bytes.Span, name), owner);
        }
        catch
        {
            owner?.Dispose();
            throw;
        }
    }

    private static Dictionary<VPath, Entry> ReadCentralDirectory(ReadOnlySpan<byte> all, string name)
    {
        Dictionary<VPath, Entry> entries = [];

        if (all.Length == 0)
        {
            // An empty PAKFILE lump is a map that packs nothing, which is
            // normal and not an error.
            return entries;
        }

        int end = FindEndOfCentralDirectory(all, name);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(all[(end + 10)..]);
        int directorySize = BinaryPrimitives.ReadInt32LittleEndian(all[(end + 12)..]);
        int directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(all[(end + 16)..]);

        if (directoryOffset < 0 || directorySize < 0 || directoryOffset + directorySize > all.Length)
        {
            throw new InvalidDataException(
                $"{name}: the central directory claims {directorySize} bytes at offset {directoryOffset}, past the end of a {all.Length}-byte pak");
        }

        int offset = directoryOffset;

        for (int i = 0; i < count; i++)
        {
            if (offset + 46 > all.Length)
            {
                throw new InvalidDataException(
                    $"{name}: central directory entry {i} of {count} runs past the end of the pak");
            }

            ReadOnlySpan<byte> record = all[offset..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != CentralDirectorySignature)
            {
                throw new InvalidDataException(
                    $"{name}: central directory entry {i} has no signature at offset {offset}");
            }

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            uint compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            uint localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[42..]);

            if (offset + 46 + nameLength > all.Length)
            {
                throw new InvalidDataException(
                    $"{name}: the name of central directory entry {i} runs past the end of the pak");
            }

            if (compressedLength == uint.MaxValue || length == uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"{name}: entry {i} uses Zip64, which a BSP pak never does and this reader does not handle");
            }

            if (method is not (MethodStore or MethodDeflate))
            {
                throw new InvalidDataException(
                    $"{name}: entry {i} uses compression method {method}, which is neither STORE nor deflate");
            }

            // Latin-1 rather than UTF-8 for the same reason as the VPK reader:
            // these are bytes the engine compares with strcmp, and decoding a
            // stray byte to U+FFFD would merge two different names.
            string entryName = Encoding.Latin1.GetString(all.Slice(offset + 46, nameLength));
            offset += 46 + nameLength + extraLength + commentLength;

            // A directory entry, which a zip records as a zero-length name
            // ending in a slash. Not content.
            if (entryName.EndsWith('/'))
            {
                continue;
            }

            if (VPath.TryCreate(entryName, out VPath path) && !path.IsEmpty)
            {
                entries[path] = new Entry(method, (int)length, (int)compressedLength, (int)localHeaderOffset);
            }
        }

        return entries;
    }

    private static int FindEndOfCentralDirectory(ReadOnlySpan<byte> all, string name)
    {
        if (all.Length < EndOfCentralDirectoryLength)
        {
            throw new InvalidDataException(
                $"{name} is {all.Length} bytes, too short to be a pak");
        }

        // The record is last but for a variable-length comment, so it is found
        // by scanning backwards. A zip comment is at most 64 KB.
        int lowest = Math.Max(0, all.Length - EndOfCentralDirectoryLength - ushort.MaxValue);

        for (int i = all.Length - EndOfCentralDirectoryLength; i >= lowest; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(all[i..]) == EndOfCentralDirectorySignature)
            {
                return i;
            }
        }

        throw new InvalidDataException($"{name}: no end-of-central-directory record");
    }

    private static void Inflate(ReadOnlySpan<byte> compressed, Span<byte> destination, VPath path)
    {
        byte[] input = compressed.ToArray();
        using MemoryStream source = new(input, writable: false);
        using DeflateStream inflate = new(source, CompressionMode.Decompress);

        byte[] output = new byte[destination.Length];
        inflate.ReadExactly(output);
        output.CopyTo(destination);

        if (inflate.ReadByte() >= 0)
        {
            throw new InvalidDataException(
                $"{path} inflated to more than the {destination.Length} bytes its directory entry declares");
        }
    }

    private readonly record struct Entry(
        ushort Method,
        int Length,
        int CompressedLength,
        int LocalHeaderOffset);
}
