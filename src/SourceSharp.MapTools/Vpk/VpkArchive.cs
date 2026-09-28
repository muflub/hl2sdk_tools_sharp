//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Vpk;

/// <summary>
/// A read-only reader for VPK archives, version 1 and version 2.
/// </summary>
/// <remarks>
/// <para>
/// OURS, per the plan's ruling that this assembly takes no packages. The format
/// is described by the engine drop's own <c>vpklib/fileformat.txt</c> and
/// Which is where the layout below comes from;
/// the format was cross-checked against public documentation on the corners
/// (the <c>0x7fff</c> embedded-chunk index and the multi-part descriptor list)
/// and no code from it is here. The reader is about three hundred lines because
/// the format is three loops and a struct.
/// </para>
/// <para>
/// READ-ONLY, deliberately. Nothing in a map compile writes a VPK, and a writer
/// would have to reproduce the shipping tools' chunking and signing to be worth anything.
/// </para>
/// <para>
/// The directory lives in <c>&lt;name&gt;_dir.vpk</c> and the bytes in
/// <c>&lt;name&gt;_000.vpk</c>, <c>_001</c> and so on — except for the small
/// files the shipping tools store in the directory file itself, which is what archive index
/// <see cref="EmbeddedArchiveIndex"/> means. <c>gameinfo.txt</c> names the
/// archive WITHOUT the <c>_dir</c>, so <see cref="OpenAsync"/> accepts either
/// spelling.
/// </para>
/// <para>
/// Everything is read through <see cref="IFileSystem"/>, including the archive
/// parts, so a VPK fixture can live entirely in memory and a real one on disk is
/// recorded by the dependency recorder like anything else.
/// </para>
/// </remarks>
public sealed class VpkArchive : IPackedArchive
{
    /// <summary>The four bytes that begin every VPK: <c>0x55aa1234</c>.</summary>
    public const uint Marker = 0x55aa1234;

    /// <summary>
    /// The archive index meaning "in the directory file itself", not in a
    /// numbered part.
    /// </summary>
    public const int EmbeddedArchiveIndex = 0x7fff;

    private const ushort PartListTerminator = 0xffff;
    private const string DirectorySuffix = "_dir.vpk";

    private readonly Dictionary<int, ArchivePart> _parts;
    private readonly Dictionary<VPath, VpkEntry> _entries;
    private readonly IFileSystem _fileSystem;
    private bool _disposed;

    private VpkArchive(
        string name,
        IFileSystem fileSystem,
        Dictionary<VPath, VpkEntry> entries,
        Dictionary<int, ArchivePart> parts,
        int version)
    {
        Name = name;
        Version = version;
        _fileSystem = fileSystem;
        _entries = entries;
        _parts = parts;
    }

    /// <summary>A description of the archive, for diagnostics. Not a key.</summary>
    public string Name { get; }

    /// <summary>The format version the directory declared: 1 or 2.</summary>
    public int Version { get; }

    /// <summary>Every path the archive holds, in its own spelling.</summary>
    public IReadOnlyCollection<VPath> Paths => _entries.Keys;

    /// <summary>How many files the archive holds.</summary>
    public int Count => _entries.Count;

    /// <summary>Opens a VPK by its directory file.</summary>
    /// <param name="fileSystem">Where the directory and its parts are read from.</param>
    /// <param name="path">
    /// The directory file, with or without the <c>_dir</c>:
    /// <c>hl2/hl2_misc_dir.vpk</c> and <c>hl2/hl2_misc.vpk</c> both open the
    /// same archive, because <c>gameinfo.txt</c> uses the second spelling and
    /// the disk uses the first.
    /// </param>
    /// <param name="cancellationToken">Cancels opening and reading the directory.</param>
    /// <returns>The archive; the caller disposes it.</returns>
    /// <exception cref="FileNotFoundException">There is no such archive.</exception>
    /// <exception cref="InvalidVpkException">The directory is not a readable VPK.</exception>
    public static async ValueTask<VpkArchive> OpenAsync(
        IFileSystem fileSystem,
        VPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        VPath directoryPath = await ResolveDirectoryPathAsync(fileSystem, path, cancellationToken)
            .ConfigureAwait(false);

        using IMemoryOwner<byte> owner = await fileSystem
            .ReadAllAsync(directoryPath, cancellationToken)
            .ConfigureAwait(false);

        return Parse(fileSystem, directoryPath, owner.Memory.Span);
    }

    /// <summary>
    /// Which directory file a <c>gameinfo.txt</c> spelling names, preferring the
    /// <c>_dir</c> form the disk actually uses.
    /// </summary>
    /// <param name="fileSystem">Where to look.</param>
    /// <param name="path">The path as it was written.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>The path of the directory file.</returns>
    /// <exception cref="FileNotFoundException">Neither spelling is there.</exception>
    public static async ValueTask<VPath> ResolveDirectoryPathAsync(
        IFileSystem fileSystem,
        VPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (path.Value.EndsWith(DirectorySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        VPath withDir = VPath.Create(path.Value[..^path.Extension.Length] + DirectorySuffix);
        if (await fileSystem.ExistsAsync(withDir, cancellationToken).ConfigureAwait(false))
        {
            return withDir;
        }

        if (await fileSystem.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return path;
        }

        throw new FileNotFoundException(
            $"no VPK at {path} or {withDir}",
            withDir.Value);
    }

    /// <summary>The entry for a path, in the archive's own spelling.</summary>
    /// <param name="path">The path to look up.</param>
    /// <returns>Its entry, or null when the archive does not hold it.</returns>
    public VpkEntry? Find(VPath path) =>
        _entries.TryGetValue(path, out VpkEntry? entry) ? entry : null;

    /// <inheritdoc />
    public async ValueTask<IMemoryOwner<byte>?> ReadAsync(
        VPath path,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_entries.TryGetValue(path, out VpkEntry? entry))
        {
            return null;
        }

        long total = entry.Length;
        if (total > int.MaxValue)
        {
            throw new InvalidVpkException(
                $"{Name}: {path} is {total} bytes, which does not fit one buffer");
        }

        PooledMemoryOwner owner = PooledMemoryOwner.Rent((int)total);
        try
        {
            Memory<byte> destination = owner.Memory;
            entry.Preload.CopyTo(destination);
            destination = destination[entry.Preload.Length..];

            foreach (VpkFilePart part in entry.Parts)
            {
                await ReadPartAsync(path, part, destination[..(int)part.Length], cancellationToken)
                    .ConfigureAwait(false);

                destination = destination[(int)part.Length..];
            }

            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A file is its preload bytes (kept in the directory, already in memory)
    /// followed by its parts in order, and the range is cut out of that
    /// concatenation: the preload is copied from memory and only the slice of
    /// each archive part that overlaps the range is read from disk. A texture
    /// header that lies entirely in the preload -- which is what the preload
    /// is for -- touches no archive part at all.
    /// </para>
    /// <para>
    /// A part that comes up short fails with the same
    /// <see cref="InvalidVpkException"/> a whole read gives, but only when the
    /// shortfall is inside the range: bytes past the range are not read, so
    /// they cannot be found missing.
    /// </para>
    /// </remarks>
    public async ValueTask<FileRange?> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // Checked here and not left to the part reads: a range inside the
        // preload reads no part, and a cancelled compile should still stop.
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(path, out VpkEntry? entry))
        {
            return null;
        }

        long fileLength = entry.Length;
        FileRange range = FileRange.Rent(FileRange.Available(offset, length, fileLength), offset, fileLength);
        try
        {
            Memory<byte> destination = range.Memory;

            // Where the next piece of the file starts, in file coordinates.
            long pieceStart = 0;

            ReadOnlyMemory<byte> preload = entry.Preload;
            if (!destination.IsEmpty && offset < preload.Length)
            {
                int take = (int)Math.Min(destination.Length, preload.Length - offset);
                preload.Slice((int)offset, take).CopyTo(destination);
                destination = destination[take..];
            }

            pieceStart += preload.Length;

            foreach (VpkFilePart part in entry.Parts)
            {
                if (destination.IsEmpty)
                {
                    break;
                }

                long pieceEnd = pieceStart + part.Length;
                long wantFrom = offset + (range.Memory.Length - destination.Length);
                if (wantFrom < pieceEnd)
                {
                    long skip = wantFrom - pieceStart;
                    int take = (int)Math.Min(destination.Length, part.Length - skip);
                    await ReadPartAsync(
                        path,
                        part with { Offset = part.Offset + skip, Length = take },
                        destination[..take],
                        cancellationToken).ConfigureAwait(false);

                    destination = destination[take..];
                }

                pieceStart = pieceEnd;
            }

            return range;
        }
        catch
        {
            range.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (ArchivePart part in _parts.Values)
        {
            await part.DisposeAsync().ConfigureAwait(false);
        }

        _parts.Clear();
    }

    private static VpkArchive Parse(IFileSystem fileSystem, VPath directoryPath, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            throw new InvalidVpkException($"{directoryPath} is {bytes.Length} bytes, too short for a VPK header");
        }

        uint marker = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (marker != Marker)
        {
            throw new InvalidVpkException(
                $"{directoryPath}: expected the marker 0x{Marker:x8}, found 0x{marker:x8}");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        int directorySize = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);

        int headerSize = version switch
        {
            1 => 12,
            2 => 28,
            _ => throw new InvalidVpkException($"{directoryPath}: version {version} is not a VPK version"),
        };

        if (bytes.Length < headerSize)
        {
            throw new InvalidVpkException(
                $"{directoryPath} is {bytes.Length} bytes, too short for a version {version} header");
        }

        if (directorySize < 0 || headerSize + (long)directorySize > bytes.Length)
        {
            throw new InvalidVpkException(
                $"{directoryPath}: the directory claims {directorySize} bytes, which runs past the end of a {bytes.Length}-byte file");
        }

        Dictionary<VPath, VpkEntry> entries = [];
        HashSet<int> archiveIndices = [];
        ReadDirectory(directoryPath, bytes.Slice(headerSize, directorySize), entries, archiveIndices);

        string baseName = directoryPath.Value.EndsWith(DirectorySuffix, StringComparison.OrdinalIgnoreCase)
            ? directoryPath.Value[..^DirectorySuffix.Length]
            : directoryPath.Value[..^directoryPath.Extension.Length];

        // The embedded chunk starts where the directory ends; a numbered part
        // is a whole file, so its data section starts at zero.
        long embeddedBase = headerSize + (long)directorySize;

        Dictionary<int, ArchivePart> parts = [];
        foreach (int index in archiveIndices)
        {
            parts[index] = index == EmbeddedArchiveIndex
                ? new ArchivePart(directoryPath, embeddedBase)
                : new ArchivePart(
                    VPath.Create($"{baseName}_{index.ToString("D3", CultureInfo.InvariantCulture)}.vpk"),
                    0);
        }

        return new VpkArchive(directoryPath.FileName, fileSystem, entries, parts, version);
    }

    private static void ReadDirectory(
        VPath directoryPath,
        ReadOnlySpan<byte> directory,
        Dictionary<VPath, VpkEntry> entries,
        HashSet<int> archiveIndices)
    {
        // extension \0 { directory \0 { name \0 <fixed> } } -- each of the
        // three levels terminated by an empty string. The path is
        // "<directory>/<name>.<extension>", with the directory spelled " " for
        // the archive's root.
        int offset = 0;

        while (true)
        {
            string extension = ReadString(directoryPath, directory, ref offset);
            if (extension.Length == 0)
            {
                break;
            }

            while (true)
            {
                string folder = ReadString(directoryPath, directory, ref offset);
                if (folder.Length == 0)
                {
                    break;
                }

                while (true)
                {
                    string name = ReadString(directoryPath, directory, ref offset);
                    if (name.Length == 0)
                    {
                        break;
                    }

                    VpkEntry entry = ReadEntry(
                        directoryPath,
                        directory,
                        ref offset,
                        BuildPath(folder, name, extension),
                        archiveIndices);

                    // Last spelling wins, matching the engine's own behaviour of
                    // overwriting a duplicate rather than refusing the archive.
                    entries[entry.Path] = entry;
                }
            }
        }
    }

    private static VPath BuildPath(string folder, string name, string extension)
    {
        string leaf = extension is " " ? name : name + "." + extension;
        return folder is " " ? VPath.Create(leaf) : VPath.Create(folder + "/" + leaf);
    }

    private static VpkEntry ReadEntry(
        VPath directoryPath,
        ReadOnlySpan<byte> directory,
        ref int offset,
        VPath path,
        HashSet<int> archiveIndices)
    {
        Need(directoryPath, directory, offset, 6);
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(directory[offset..]);
        int preloadSize = BinaryPrimitives.ReadUInt16LittleEndian(directory[(offset + 4)..]);
        offset += 6;

        List<VpkFilePart> parts = [];

        while (true)
        {
            Need(directoryPath, directory, offset, 2);
            ushort archiveIndex = BinaryPrimitives.ReadUInt16LittleEndian(directory[offset..]);

            if (archiveIndex == PartListTerminator)
            {
                offset += 2;
                break;
            }

            Need(directoryPath, directory, offset, 10);
            uint partOffset = BinaryPrimitives.ReadUInt32LittleEndian(directory[(offset + 2)..]);
            uint partLength = BinaryPrimitives.ReadUInt32LittleEndian(directory[(offset + 6)..]);
            offset += 10;

            archiveIndices.Add(archiveIndex);
            parts.Add(new VpkFilePart(archiveIndex, partOffset, partLength));
        }

        Need(directoryPath, directory, offset, preloadSize);
        byte[] preload = directory.Slice(offset, preloadSize).ToArray();
        offset += preloadSize;

        return new VpkEntry(path, crc, preload, parts);
    }

    private static string ReadString(VPath directoryPath, ReadOnlySpan<byte> directory, ref int offset)
    {
        int end = directory[offset..].IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidVpkException(
                $"{directoryPath}: a name at offset {offset} runs to the end of the directory with no terminator");
        }

        // Latin-1 rather than UTF-8: these are bytes the engine compares with
        // strcmp, and decoding a non-UTF-8 byte to U+FFFD would silently merge
        // two different names.
        string value = Encoding.Latin1.GetString(directory.Slice(offset, end));
        offset += end + 1;
        return value;
    }

    private static void Need(VPath directoryPath, ReadOnlySpan<byte> directory, int offset, int bytes)
    {
        if (offset + bytes > directory.Length)
        {
            throw new InvalidVpkException(
                $"{directoryPath}: an entry at offset {offset} needs {bytes} bytes and the directory has {directory.Length - offset} left");
        }
    }

    private async ValueTask ReadPartAsync(
        VPath path,
        VpkFilePart part,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (!_parts.TryGetValue(part.ArchiveIndex, out ArchivePart? archive))
        {
            throw new InvalidVpkException(
                $"{Name}: {path} names archive part {part.ArchiveIndex}, which the directory never declared");
        }

        await archive.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Stream stream = archive.Stream
                ??= await _fileSystem.OpenReadAsync(archive.Path, cancellationToken).ConfigureAwait(false);

            if (!stream.CanSeek)
            {
                throw new InvalidVpkException(
                    $"{Name}: {archive.Path} is not seekable, so {path} cannot be read out of it");
            }

            stream.Seek(archive.BaseOffset + part.Offset, SeekOrigin.Begin);

            try
            {
                await stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException error)
            {
                // A short read: the archive part is truncated, or the directory
                // describes bytes that are not there. A diagnostic naming the
                // entry, not a short buffer handed on to a VTF parser that will
                // fail somewhere unrelated.
                throw new InvalidVpkException(
                    $"{Name}: {path} claims {part.Length} bytes at offset {part.Offset} of {archive.Path}, which came up short",
                    error);
            }
        }
        finally
        {
            archive.Gate.Release();
        }
    }

    /// <summary>
    /// One <c>_NNN.vpk</c>, or the directory file when it carries the embedded
    /// chunk. The stream is opened on first use and held, because a compile
    /// reads thousands of entries out of the same handful of parts; the
    /// semaphore is there because a seek-then-read on a shared stream is not
    /// thread-safe and compile stages read in parallel.
    /// </summary>
    private sealed class ArchivePart(VPath path, long baseOffset) : IAsyncDisposable
    {
        public VPath Path { get; } = path;

        public long BaseOffset { get; } = baseOffset;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Stream? Stream { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Stream is not null)
            {
                await Stream.DisposeAsync().ConfigureAwait(false);
                Stream = null;
            }

            Gate.Dispose();
        }
    }
}
