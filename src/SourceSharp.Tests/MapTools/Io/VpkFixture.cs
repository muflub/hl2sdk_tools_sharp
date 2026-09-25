using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Vpk;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// Builds a VPK in memory, so the reader's facts need no installed game.
/// </summary>
/// <remarks>
/// <para>
/// A WRITER, in the test assembly, because <see cref="VpkArchive"/> is
/// deliberately read-only: nothing in a map compile writes a VPK, and shipping a
/// writer in the library to make the tests convenient would be shipping
/// untested surface for the sake of test convenience.
/// </para>
/// <para>
/// This is a stand-in for something native, which this project has been bitten
/// by before, so <see cref="VpkArchiveTests"/> also runs the reader against a
/// REAL shipped archive: if the fixture drifts from the format, the paired facts
/// disagree.
/// </para>
/// </remarks>
internal sealed class VpkFixture
{
    private readonly List<(string Path, byte[] Contents, int PreloadBytes, int ArchiveIndex)> _files = [];

    /// <summary>Adds a file whose bytes live in an archive part.</summary>
    /// <param name="path">Its path inside the archive.</param>
    /// <param name="contents">Its bytes.</param>
    /// <param name="archiveIndex">Which <c>_NNN.vpk</c> holds them.</param>
    /// <returns>This fixture, so adders chain.</returns>
    public VpkFixture Add(string path, byte[] contents, int archiveIndex = 0)
    {
        _files.Add((path, contents, 0, archiveIndex));
        return this;
    }

    /// <summary>Adds a file whose bytes live in the directory file itself.</summary>
    /// <param name="path">Its path inside the archive.</param>
    /// <param name="contents">Its bytes.</param>
    /// <returns>This fixture, so adders chain.</returns>
    public VpkFixture AddEmbedded(string path, byte[] contents)
    {
        _files.Add((path, contents, 0, VpkArchive.EmbeddedArchiveIndex));
        return this;
    }

    /// <summary>Adds a file whose first bytes are preloaded into the directory.</summary>
    /// <param name="path">Its path inside the archive.</param>
    /// <param name="contents">Its bytes.</param>
    /// <param name="preloadBytes">How many of them go in the directory.</param>
    /// <returns>This fixture, so adders chain.</returns>
    public VpkFixture AddWithPreload(string path, byte[] contents, int preloadBytes)
    {
        _files.Add((path, contents, preloadBytes, 0));
        return this;
    }

    /// <summary>Adds a text file, encoded as UTF-8.</summary>
    /// <param name="path">Its path inside the archive.</param>
    /// <param name="text">Its contents.</param>
    /// <returns>This fixture, so adders chain.</returns>
    public VpkFixture AddText(string path, string text) => Add(path, Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Writes the archive into a file system, as <c>&lt;baseName&gt;_dir.vpk</c>
    /// and its numbered parts.
    /// </summary>
    /// <param name="fileSystem">Where the files go.</param>
    /// <param name="baseName">
    /// The archive's base path, without <c>_dir</c> or an extension, for example
    /// <c>hl2/hl2_misc</c>.
    /// </param>
    /// <param name="version">1 or 2.</param>
    /// <returns>The path of the directory file.</returns>
    public VPath Write(InMemoryFileSystem fileSystem, string baseName, int version = 2)
    {
        Dictionary<int, MemoryStream> archives = [];

        foreach ((string path, byte[] contents, int preloadBytes, int archiveIndex) in _files)
        {
            if (archiveIndex == VpkArchive.EmbeddedArchiveIndex)
            {
                continue;
            }

            if (!archives.TryGetValue(archiveIndex, out MemoryStream? archive))
            {
                archives[archiveIndex] = archive = new MemoryStream();
            }

            archive.Write(contents.AsSpan(preloadBytes));
        }

        // Laid out extension -> directory -> name, which is the order the
        // format nests them in, so that a shared extension or folder is written
        // once.
        var tree = _files
            .GroupBy(f => Extension(f.Path), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        MemoryStream embedded = new();
        MemoryStream directory = new();
        Dictionary<int, long> written = [];

        // One scratch buffer, outside the loops: a stackalloc per entry is a
        // stack overflow waiting for a large fixture (CA2014).
        byte[] scratch = new byte[10];

        foreach (var extensionGroup in tree)
        {
            WriteString(directory, extensionGroup.Key);

            foreach (var folderGroup in extensionGroup
                .GroupBy(f => Folder(f.Path), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                WriteString(directory, folderGroup.Key);

                foreach ((string path, byte[] contents, int preloadBytes, int archiveIndex) in folderGroup)
                {
                    WriteString(directory, Name(path));

                    Span<byte> fixedPart = scratch.AsSpan(0, 6);
                    BinaryPrimitives.WriteUInt32LittleEndian(fixedPart, Crc32(contents));
                    BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[4..], (ushort)preloadBytes);
                    directory.Write(fixedPart);

                    int bodyLength = contents.Length - preloadBytes;
                    if (bodyLength > 0)
                    {
                        long offset;
                        if (archiveIndex == VpkArchive.EmbeddedArchiveIndex)
                        {
                            offset = embedded.Position;
                            embedded.Write(contents.AsSpan(preloadBytes));
                        }
                        else
                        {
                            offset = written.GetValueOrDefault(archiveIndex);
                            written[archiveIndex] = offset + bodyLength;
                        }

                        Span<byte> part = scratch.AsSpan(0, 10);
                        BinaryPrimitives.WriteUInt16LittleEndian(part, (ushort)archiveIndex);
                        BinaryPrimitives.WriteUInt32LittleEndian(part[2..], (uint)offset);
                        BinaryPrimitives.WriteUInt32LittleEndian(part[6..], (uint)bodyLength);
                        directory.Write(part);
                    }

                    Span<byte> terminator = scratch.AsSpan(0, 2);
                    BinaryPrimitives.WriteUInt16LittleEndian(terminator, 0xffff);
                    directory.Write(terminator);

                    directory.Write(contents.AsSpan(0, preloadBytes));
                }

                directory.WriteByte(0);
            }

            directory.WriteByte(0);
        }

        directory.WriteByte(0);

        byte[] directoryBytes = directory.ToArray();
        byte[] embeddedBytes = embedded.ToArray();

        MemoryStream file = new();
        Span<byte> header = stackalloc byte[version == 1 ? 12 : 28];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, VpkArchive.Marker);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], directoryBytes.Length);

        if (version == 2)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], embeddedBytes.Length);
        }

        file.Write(header);
        file.Write(directoryBytes);
        file.Write(embeddedBytes);

        VPath directoryPath = VPath.Create(baseName + "_dir.vpk");
        fileSystem.AddFile(directoryPath, file.ToArray());

        foreach ((int index, MemoryStream archive) in archives)
        {
            fileSystem.AddFile(VPath.Create($"{baseName}_{index:D3}.vpk"), archive.ToArray());
        }

        return directoryPath;
    }

    /// <summary>
    /// The CRC-32 a VPK directory records, computed here rather than pulled in
    /// as a package: <c>System.IO.Hashing</c> would be a PackageReference, and
    /// the plan rules this tree package-free.
    /// </summary>
    /// <param name="data">The bytes to hash.</param>
    /// <returns>Their CRC-32, the reflected IEEE polynomial zip and VPK use.</returns>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;

        foreach (byte b in data)
        {
            crc ^= b;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static string Extension(string path)
    {
        int slash = path.LastIndexOf('/');
        string leaf = slash < 0 ? path : path[(slash + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot < 0 ? " " : leaf[(dot + 1)..];
    }

    private static string Folder(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? " " : path[..slash];
    }

    private static string Name(string path)
    {
        int slash = path.LastIndexOf('/');
        string leaf = slash < 0 ? path : path[(slash + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot < 0 ? leaf : leaf[..dot];
    }

    private static void WriteString(Stream stream, string value)
    {
        stream.Write(Encoding.Latin1.GetBytes(value));
        stream.WriteByte(0);
    }
}
