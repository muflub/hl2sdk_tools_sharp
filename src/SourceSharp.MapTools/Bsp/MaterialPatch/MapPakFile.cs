//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Zip;

namespace SourceSharp.MapTools.Bsp.MaterialPatch;

/// <summary>
/// The BSP's embedded pak while vbsp is building it: <c>GetPakFile()</c>'s
/// <c>IZip</c>, with the three operations the compiler performs on it.
/// </summary>
/// <remarks>
/// <para>
/// Every name is LOWER-CASED on the way in and on every lookup
/// (in <c>AddBufferToZip</c>,
/// In <c>FileExistsInZip</c>), so two spellings of one path
/// are one entry. Adding a name that is already there REPLACES its bytes
/// ("Adds a new lump, or overwrites existing one",), which this
/// does in place so the entry keeps its position.
/// </para>
/// <para>
/// Text mode is <c>CopyTextData</c>: every LF
/// becomes CR LF on the way, and <c>ReadTextData</c>
/// turns CR LF back into LF on the way out. The patched VMTs are written in
/// Text mode; the cubemap VTFs are not
/// </para>
/// <para>
/// ORDER: entries are kept in first-insertion order. Stock's order is not
/// reproducible (see <see cref="ZipArchiveWriter"/>'s remarks), so the gate on
/// a pak is its name LIST and each entry's bytes, not the byte stream.
/// </para>
/// </remarks>
public sealed class MapPakFile
{
    private readonly List<ZipEntry> _entries = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    /// <summary>The entries, in first-insertion order.</summary>
    public IReadOnlyList<ZipEntry> Entries => _entries;

    /// <summary>How many entries the pak holds.</summary>
    public int Count => _entries.Count;

    /// <summary>A pak holding what an existing archive holds, in its order.</summary>
    /// <param name="archive">The archive, typically an existing BSP's pak lump.</param>
    /// <returns>The pak.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="archive"/> is null.</exception>
    public static MapPakFile FromArchive(ZipArchiveReader archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        MapPakFile pak = new();
        foreach (ZipEntry entry in archive.Entries)
        {
            pak.Put(entry);
        }

        return pak;
    }

    /// <summary><c>AddBufferToPak</c>.</summary>
    /// <param name="name">The pak-relative path. Lower-cased.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="textMode">
    /// True to store it as text: each LF becomes CR LF
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public void Add(string name, ReadOnlySpan<byte> data, bool textMode)
    {
        ArgumentNullException.ThrowIfNull(name);

        byte[] bytes = textMode ? ToDiskText(data) : data.ToArray();
        Put(new ZipEntry(name, bytes));
    }

    /// <summary><c>FileExistsInPak</c>: case-insensitive.</summary>
    /// <param name="name">The pak-relative path.</param>
    /// <returns>True when an entry of that name is present.</returns>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _index.ContainsKey(name.ToLowerInvariant());
    }

    /// <summary><c>ReadFileFromPak</c>.</summary>
    /// <param name="name">The pak-relative path.</param>
    /// <param name="textMode">True to turn CR LF back into LF.</param>
    /// <returns>The bytes, or null when there is no such entry.</returns>
    /// <remarks>
    /// Stock's text read also appends a NUL; that terminator is
    /// a C-string convenience and is not returned here.
    /// </remarks>
    public byte[]? Read(string name, bool textMode)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_index.TryGetValue(name.ToLowerInvariant(), out int at))
        {
            return null;
        }

        byte[] data = _entries[at].Data;
        return textMode ? FromDiskText(data) : (byte[])data.Clone();
    }

    /// <summary>A writer holding every entry, in this pak's order.</summary>
    /// <returns>The writer; its bytes are the PAKFILE lump.</returns>
    public ZipArchiveWriter ToWriter()
    {
        ZipArchiveWriter writer = new();
        foreach (ZipEntry entry in _entries)
        {
            writer.Add(entry);
        }

        return writer;
    }

    private void Put(ZipEntry entry)
    {
        if (_index.TryGetValue(entry.Name, out int at))
        {
            _entries[at] = entry;
            return;
        }

        _index.Add(entry.Name, _entries.Count);
        _entries.Add(entry);
    }

    /// <summary><c>CopyTextData</c>: LF to CR LF, nothing else touched.</summary>
    internal static byte[] ToDiskText(ReadOnlySpan<byte> data)
    {
        int newlines = data.Count((byte)'\n');
        byte[] output = new byte[data.Length + newlines];
        int o = 0;

        foreach (byte b in data)
        {
            if (b == (byte)'\n')
            {
                output[o++] = (byte)'\r';
            }

            output[o++] = b;
        }

        return output;
    }

    /// <summary><c>ReadTextData</c>: a CR immediately before an LF is dropped.</summary>
    internal static byte[] FromDiskText(ReadOnlySpan<byte> data)
    {
        List<byte> output = new(data.Length);

        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\r' && i + 1 < data.Length && data[i + 1] == (byte)'\n')
            {
                continue;
            }

            output.Add(data[i]);
        }

        return [.. output];
    }
}
