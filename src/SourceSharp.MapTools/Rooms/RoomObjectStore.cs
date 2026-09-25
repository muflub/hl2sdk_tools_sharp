using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The <c>.room</c> file format: one compiled <see cref="RoomObject"/> written
/// to (and read back from) a stream, so <c>ssmap room</c> can produce rooms in
/// one process and <c>ssmap link</c> consume them in another.
/// </summary>
/// <remarks>
/// <para>
/// The container is <c>SSROOM01</c>: the eight magic bytes, an
/// <c>int32</c> big-endian version (currently 1), then three
/// length-prefixed (<c>int32</c> big-endian) sections — a UTF-8 JSON
/// manifest, the room's BSP lump bytes, and its vis blob. Every integer is
/// big-endian so the file's bytes do not depend on the writer's host byte
/// order; every section is length-prefixed so a reader can refuse a
/// truncated file by name ("the vis blob stops at 41 of 96 bytes") instead
/// of misparsing it as structure.
/// </para>
/// <para>
/// The manifest carries what the linker reads that is not a lump or a vis
/// row: the definition (name, cell size, kit, sockets), the socket seal
/// clusters in socket order, and the two provenance strings the compile
/// recorded — <c>sourceHash</c>, the SHA-256 hex of the room's VMF text, and
/// <c>toolIdentity</c>, the informational version of the MapTools assembly
/// that compiled it. A room object is exactly as trustworthy as the tool
/// that made it, so the file says which one that was.
/// </para>
/// <para>
/// Round-trip contract, and the reason the format exists at all: a room
/// loaded back is what <see cref="LevelLinker.LinkAsync"/> consumes, and the
/// linker reads the room's BSP lumps byte-for-byte (its relocation refuses
/// any lump it does not understand rather than dropping it), its vis rows,
/// its cluster count, the definition, and the seal clusters. Every one of
/// those survives the round-trip byte-identically — the lump bytes and the
/// PVS/PAS rows are copied through, never re-encoded — so the linked map
/// from a reloaded room is byte-identical to the linked map from the room
/// still in memory.
/// </para>
/// <para>
/// Two fields are not persisted and lose nothing the linker reads:
/// <see cref="RoomObject.InteriorClusters"/> is empty for every room
/// <see cref="RoomCompiler"/> produces (the linker keeps a room's interior
/// inside itself with the room's own vvis rows, not a cluster list), and
/// <see cref="VisResult.Trace"/> is the vvis debug replay, which a room
/// compile never requests. <see cref="RoomObject.InputKeys"/> is rebuilt
/// from the manifest in <see cref="RoomCompiler"/>'s own key format, so the
/// §10a promise "same keys ⇒ same room bytes" holds across files, and the
/// two strings that compose it are the persisted ones.
/// </para>
/// </remarks>
public static class RoomObjectStore
{
    /// <summary>The container's eight magic bytes, as they read in the file.</summary>
    public const string ContainerMagic = "SSROOM01";

    /// <summary>The only container version this build reads and writes.</summary>
    public const int ContainerVersion = 2;

    // Manifest keys — consts, not a table, so nothing mutable is ever static.
    private const string NameKey = "name";
    private const string CellSizeKey = "cellSize";
    private const string KitKey = "kit";
    private const string WidthKey = "width";
    private const string HeightKey = "height";
    private const string DepthKey = "depth";
    private const string SocketsKey = "sockets";
    private const string FacingKey = "facing";
    private const string SocketNameKey = "name";
    private const string SocketClustersKey = "socketClusters";
    private const string SourceHashKey = "sourceHash";
    private const string ToolIdentityKey = "toolIdentity";

    /// <summary>
    /// Writes one room object: magic, version, manifest, BSP blob, vis blob.
    /// </summary>
    /// <param name="room">The compiled room.</param>
    /// <param name="w">The stream to write to; the caller owns it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once every byte is in the stream.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task SaveAsync(RoomObject room, Stream w, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(w);

        byte[] manifest = BuildManifest(room);
        byte[] bspBlob = BuildBspBlob(room.Bsp);
        byte[] visBlob = BuildVisBlob(room.Vis);

        await w.WriteAsync(Encoding.ASCII.GetBytes(ContainerMagic), cancellationToken).ConfigureAwait(false);
        await WriteInt32BEAsync(w, ContainerVersion, cancellationToken).ConfigureAwait(false);
        await WriteBlobAsync(w, manifest, cancellationToken).ConfigureAwait(false);
        await WriteBlobAsync(w, bspBlob, cancellationToken).ConfigureAwait(false);
        await WriteBlobAsync(w, visBlob, cancellationToken).ConfigureAwait(false);
        await w.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one room object written by <see cref="SaveAsync"/>.
    /// </summary>
    /// <param name="r">The stream to read from; the caller owns it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The room, ready for <see cref="LevelLinker.LinkAsync"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">
    /// The container is not one this build reads: wrong magic, unknown
    /// version, a truncated section, or a manifest with a missing, unknown or
    /// mistyped field — each message names the observed bytes or field.
    /// </exception>
    public static async Task<RoomObject> LoadAsync(Stream r, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);

        byte[] magic = await ReadFixedAsync(r, 8, "magic", cancellationToken).ConfigureAwait(false);
        if (!MatchesMagic(magic))
        {
            throw new LinkException(
                $"not a room container: the file opens with bytes {FormatBytes(magic)}"
                + $" (text \"{AsVisibleText(magic)}\"), expected the {ContainerMagic} magic.");
        }

        int version = await ReadInt32BEAsync(r, "version", cancellationToken).ConfigureAwait(false);
        if (version != ContainerVersion)
        {
            throw new LinkException(
                $"room container version {version} (bytes "
                + $"{FormatBytes([(byte)(version >> 24), (byte)(version >> 16), (byte)(version >> 8), (byte)version])});"
                + $" this build reads version {ContainerVersion}.");
        }

        byte[] manifestBytes = await ReadBlobAsync(r, "manifest", cancellationToken).ConfigureAwait(false);
        byte[] bspBlob = await ReadBlobAsync(r, "BSP", cancellationToken).ConfigureAwait(false);
        byte[] visBlob = await ReadBlobAsync(r, "vis", cancellationToken).ConfigureAwait(false);

        string? roomName = null;
        float? cellSize = null;
        (float Width, float Height, float Depth)? kit = null;
        RoomSocket[]? sockets = null;
        int[] socketClusters = [];
        string? sourceHash = null;
        foreach ((string field, JsonElement value) in EnumerateProperties(manifestBytes, "manifest"))
        {
            switch (field)
            {
                case NameKey:
                    roomName = ReadString(value, NameKey);
                    break;
                case CellSizeKey:
                    cellSize = ReadSingle(value, CellSizeKey);
                    break;
                case KitKey:
                    kit = ReadKit(value);
                    break;
                case SocketsKey:
                    sockets = ReadSockets(value);
                    break;
                case SocketClustersKey:
                    socketClusters = ReadInt32Array(value, SocketClustersKey);
                    break;
                case SourceHashKey:
                    sourceHash = ReadString(value, SourceHashKey);
                    break;
                case ToolIdentityKey:
                    break; // provenance: recorded on write, read for nothing but the record itself.
                default:
                    throw new LinkException(
                        $"room manifest has an unknown field \"{field}\""
                        + $" ({FormatBytes(Encoding.UTF8.GetBytes(field))}).");
            }
        }

        if (roomName is null)
        {
            throw new LinkException($"room manifest is missing {NameKey}.");
        }

        if (cellSize is null)
        {
            throw new LinkException($"room manifest is missing {CellSizeKey}.");
        }

        if (kit is null)
        {
            throw new LinkException($"room manifest is missing {KitKey}.");
        }

        if (sockets is null)
        {
            throw new LinkException($"room manifest is missing {SocketsKey}.");
        }

        if (socketClusters.Length != sockets.Length)
        {
            throw new LinkException(
                $"room manifest lists {socketClusters.Length} socket clusters for"
                + $" {sockets.Length} sockets.");
        }

        RoomDefinition definition = new(
            roomName, cellSize.Value, new SocketKit(kit.Value.Width, kit.Value.Height, kit.Value.Depth), sockets);

        definition.Validate();

        BspData bsp = ParseBspBlob(bspBlob);
        VisResult vis = ParseVisBlob(visBlob);

        // The lint verdict is derived, not persisted: every claim the loaded
        // definition makes is checked against the loaded compile, which is
        // exactly what the compile-time lint checked — re-running it here is
        // the refusal for a file whose blobs and manifest were edited apart.
        RoomLintReport lint = RoomLinter.CheckCompiled(
            definition, bsp, RoomCompiler.SealBoxes(definition), leaked: false);

        // The manifest's socket clusters are the writing tool's claim about
        // where each door's plug put its cluster; the lint above derives the
        // same list from the blobs. A file whose two accounts disagree was
        // edited, and the linker's door graph would walk the edited claim —
        // so the claim must match the derivation, element for element.
        for (int socket = 0; socket < socketClusters.Length; socket++)
        {
            if (socketClusters[socket] != lint.SealClusters[socket])
            {
                throw new LinkException(
                    $"room manifest says socket {socket} ({definition.Sockets[socket].Name}) seals cluster"
                    + $" {socketClusters[socket]}; the compile's leaves say {lint.SealClusters[socket]}.");
            }
        }

        // §10a: keys rebuild in RoomCompiler's format from the persisted strings,
        // so "same keys ⇒ same room bytes" keeps meaning the same thing here.
        string claimed = "room:" + roomName + "|" + CellText(definition.CellSize) + "|" + KitText(definition.Kit);
        IReadOnlyList<string> inputKeys = sourceHash is null
            ? [claimed]
            : ["vmf:" + sourceHash, claimed];

        return new RoomObject(definition, bsp, vis, lint, inputKeys);
    }

    /// <summary>The manifest section's JSON, key order fixed so the bytes are stable.</summary>
    private static byte[] BuildManifest(RoomObject room)
    {
        RoomDefinition definition = room.Definition;
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(NameKey, definition.Name);
            writer.WriteNumber(CellSizeKey, definition.CellSize);
            writer.WriteStartObject(KitKey);
            writer.WriteNumber(WidthKey, definition.Kit.Width);
            writer.WriteNumber(HeightKey, definition.Kit.Height);
            writer.WriteNumber(DepthKey, definition.Kit.Depth);
            writer.WriteEndObject();
            writer.WriteStartArray(SocketsKey);
            foreach (RoomSocket socket in definition.Sockets)
            {
                writer.WriteStartObject();
                writer.WriteNumber(FacingKey, (int)socket.Facing);
                writer.WriteString(SocketNameKey, socket.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray(SocketClustersKey);
            foreach (int cluster in room.SealClusters)
            {
                writer.WriteNumberValue(cluster);
            }

            writer.WriteEndArray();
            writer.WriteString(SourceHashKey, SourceHashOf(room));
            writer.WriteString(ToolIdentityKey, ToolIdentityOf());
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The room's VMF text hash: the compile recorded it as the <c>vmf:</c>
    /// cache key (<see cref="RoomCompiler"/>), so a room that never came from
    /// the room compiler has no hash to name and the field is omitted.
    /// </summary>
    private static string? SourceHashOf(RoomObject room)
    {
        foreach (string key in room.InputKeys)
        {
            if (key.StartsWith("vmf:", StringComparison.Ordinal) && key.Length > 4)
            {
                return key[4..];
            }
        }

        return null;
    }

    /// <summary>The writing tool's identity, per the assembly's own stamp.</summary>
    private static string ToolIdentityOf()
    {
        Assembly assembly = typeof(RoomObjectStore).Assembly;
        return
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    // ---- the BSP blob ------------------------------------------------------

    /// <summary>
    /// The room's BSP as its bytes: the file version and map revision, then
    /// all 64 header lumps with their header-recorded version and uncompressed
    /// size, then the game-lump entries. Every payload is copied, never
    /// rewritten — the linker's relocation reads the same bytes the compile
    /// wrote, so a reloaded room links byte-identically.
    /// </summary>
    private static byte[] BuildBspBlob(BspData bsp)
    {
        using MemoryStream buffer = new();
        WriteInt32BE(buffer, bsp.FileVersion);
        WriteInt32BE(buffer, bsp.MapRevision);
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            BspLumpData lump = bsp[i];
            WriteInt32BE(buffer, lump.Version);
            WriteInt32BE(buffer, lump.UncompressedSize);
            WriteBlob(buffer, lump.Data.Span);
        }

        WriteInt32BE(buffer, bsp.GameLumps.Count);
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            WriteInt32BE(buffer, entry.Id);
            WriteInt32BE(buffer, entry.Flags);
            WriteInt32BE(buffer, entry.Version);
            WriteBlob(buffer, entry.Data.Span);
        }

        return buffer.ToArray();
    }

    private static BspData ParseBspBlob(byte[] blob)
    {
        Cursor cursor = new(blob);
        BspData bsp = new()
        {
            FileVersion = cursor.ReadInt32("BSP blob file version"),
            MapRevision = cursor.ReadInt32("BSP blob map revision"),
        };

        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            int version = cursor.ReadInt32($"lump {i} version");
            int uncompressedSize = cursor.ReadInt32($"lump {i} uncompressed size");
            ReadOnlyMemory<byte> data = cursor.ReadBlob($"lump {i}");
            bsp[i] = new BspLumpData(data, version, uncompressedSize);
        }

        int gameLumpCount = cursor.ReadInt32("game-lump count");
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)gameLumpCount, (uint)4096);
        for (int i = 0; i < gameLumpCount; i++)
        {
            int id = cursor.ReadInt32($"game lump {i} id");
            int flags = cursor.ReadInt32($"game lump {i} flags");
            int version = cursor.ReadInt32($"game lump {i} version");
            ReadOnlyMemory<byte> data = cursor.ReadBlob($"game lump {i}");
            bsp.GameLumps.Add(new GameLumpEntry(id, (ushort)flags, (ushort)version, data));
        }

        cursor.ExpectEnd();
        return bsp;
    }

    // ---- the vis blob ------------------------------------------------------

    /// <summary>
    /// The room's vis: the counters the linker and the cache report read, then
    /// the raw PVS and PAS rows — the uncompressed halves, which is what the
    /// linker consumes today when it builds the linked map's visibility lump.
    /// </summary>
    private static byte[] BuildVisBlob(VisResult vis)
    {
        using MemoryStream buffer = new();
        WriteInt32BE(buffer, vis.ClusterCount);
        WriteInt32BE(buffer, vis.PortalCount);
        WriteInt32BE(buffer, vis.RowBytes);
        WriteInt32BE(buffer, vis.VisDataSize);
        WriteInt32BE(buffer, vis.TotalVisibleClusters);
        WriteInt32BE(buffer, vis.OptimizedClusters);
        WriteInt32BE(buffer, vis.TotalAudibleClusters);
        buffer.WriteByte(vis.UsedRadius ? (byte)1 : (byte)0);
        WriteInt64BE(buffer, BitConverter.DoubleToInt64Bits(vis.VisRadiusSquared));
        WriteInt32BE(buffer, vis.DeepestFlow);
        WriteInt64BE(buffer, vis.Work.Chains);
        WriteInt64BE(buffer, vis.Work.Candidates);
        WriteInt64BE(buffer, vis.Work.SeparatorClips);
        WriteInt64BE(buffer, vis.Work.BaseRays);

        int rowSpan = vis.ClusterCount * vis.RowBytes;
        byte[] rows = new byte[rowSpan];
        for (int cluster = 0; cluster < vis.ClusterCount; cluster++)
        {
            vis.Pvs(cluster).CopyTo(rows.AsSpan(cluster * vis.RowBytes, vis.RowBytes));
        }

        WriteBlob(buffer, rows);
        for (int cluster = 0; cluster < vis.ClusterCount; cluster++)
        {
            vis.Pas(cluster).CopyTo(rows.AsSpan(cluster * vis.RowBytes, vis.RowBytes));
        }

        WriteBlob(buffer, rows.AsSpan(0, rowSpan));
        return buffer.ToArray();
    }

    private static VisResult ParseVisBlob(byte[] blob)
    {
        Cursor cursor = new(blob);
        int clusterCount = cursor.ReadInt32("vis cluster count");
        int portalCount = cursor.ReadInt32("vis portal count");
        int rowBytes = cursor.ReadInt32("vis row bytes");
        int visDataSize = cursor.ReadInt32("vis data size");
        int totalVisible = cursor.ReadInt32("vis total visible clusters");
        int optimized = cursor.ReadInt32("vis optimized clusters");
        int totalAudible = cursor.ReadInt32("vis total audible clusters");
        byte usedRadius = cursor.ReadByte("vis used-radius");
        double visRadiusSquared = BitConverter.Int64BitsToDouble(cursor.ReadInt64("vis radius squared"));
        int deepestFlow = cursor.ReadInt32("vis deepest flow");
        long chains = cursor.ReadInt64("vis work chains");
        long candidates = cursor.ReadInt64("vis work candidates");
        long separatorClips = cursor.ReadInt64("vis work separator clips");
        long baseRays = cursor.ReadInt64("vis work base rays");

        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)clusterCount, (uint)1_000_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)rowBytes, (uint)1_000_000);
        byte[] pvs = cursor.ReadBlob("PVS rows").ToArray();
        byte[] pas = cursor.ReadBlob("PAS rows").ToArray();
        cursor.ExpectEnd();
        return new VisResult(
            clusterCount,
            portalCount,
            rowBytes,
            pvs,
            pas,
            visDataSize,
            totalVisible,
            optimized,
            totalAudible,
            usedRadius != 0,
            visRadiusSquared,
            deepestFlow,
            new VisWorkCounters(chains, candidates, separatorClips, baseRays),
            trace: null);
    }

    // ---- binary plumbing ---------------------------------------------------

    /// <summary>A read cursor over one blob: every read is bounds-checked by name.</summary>
    private sealed class Cursor(byte[] blob)
    {
        private int _offset;

        public int ReadInt32(string what)
        {
            Require(4, what);
            int value = (blob[_offset] << 24) | (blob[_offset + 1] << 16) | (blob[_offset + 2] << 8) | blob[_offset + 3];
            _offset += 4;
            return value;
        }

        public long ReadInt64(string what)
        {
            Require(8, what);
            long value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | blob[_offset + i];
            }

            _offset += 8;
            return value;
        }

        public byte ReadByte(string what)
        {
            Require(1, what);
            return blob[_offset++];
        }

        public ReadOnlyMemory<byte> ReadBlob(string what)
        {
            int length = ReadInt32($"{what} length");
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, (uint)blob.Length);
            Require(length, what);
            ReadOnlyMemory<byte> data = blob.AsMemory(_offset, length);
            _offset += length;
            return data;
        }

        public void ExpectEnd()
        {
            if (_offset != blob.Length)
            {
                throw new LinkException(
                    $"room container blob has {_offset} of {blob.Length} bytes consumed: trailing garbage.");
            }
        }

        private void Require(int count, string what)
        {
            if (_offset + count > blob.Length)
            {
                throw new LinkException(
                    $"room container blob is truncated at {blob.Length} bytes: {what}"
                    + $" needs {count} at offset {_offset}.");
            }
        }
    }

    private static async Task WriteInt32BEAsync(Stream w, int value, CancellationToken cancellationToken)
    {
        byte[] bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
        await w.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadInt32BEAsync(Stream r, string what, CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadFixedAsync(r, 4, what, cancellationToken).ConfigureAwait(false);
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }

    private static async Task WriteBlobAsync(Stream w, byte[] blob, CancellationToken cancellationToken)
    {
        await WriteInt32BEAsync(w, blob.Length, cancellationToken).ConfigureAwait(false);
        await w.WriteAsync(blob, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBlobAsync(Stream r, string name, CancellationToken cancellationToken)
    {
        int length = await ReadInt32BEAsync(r, $"{name} length", cancellationToken).ConfigureAwait(false);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, 1u << 30);
        return await ReadFixedAsync(r, length, name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFixedAsync(
        Stream r, int count, string what, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            int read = await r.ReadAsync(bytes.AsMemory(filled, count - filled), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new LinkException(
                    $"room container is truncated in {what}: wanted {count} bytes, got {filled}.");
            }

            filled += read;
        }

        return bytes;
    }

    private static void WriteInt32BE(Stream w, int value) =>
        WriteSpan(w, [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static void WriteInt64BE(Stream w, long value)
    {
        byte[] bytes = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            bytes[i] = (byte)value;
            value >>= 8;
        }

        WriteSpan(w, bytes);
    }

    private static void WriteBlob(Stream w, ReadOnlySpan<byte> blob)
    {
        WriteInt32BE(w, blob.Length);
        WriteSpan(w, blob);
    }

    private static void WriteSpan(Stream w, ReadOnlySpan<byte> bytes) => w.Write(bytes);

    private static bool MatchesMagic(ReadOnlySpan<byte> magic) =>
        magic.Length == 8
        && magic[0] == (byte)'S' && magic[1] == (byte)'S' && magic[2] == (byte)'R' && magic[3] == (byte)'O'
        && magic[4] == (byte)'O' && magic[5] == (byte)'M' && magic[6] == (byte)'0' && magic[7] == (byte)'1';

    private static string FormatBytes(ReadOnlySpan<byte> bytes)
    {
        StringBuilder text = new(bytes.Length * 3);
        foreach (byte b in bytes)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            _ = text.Append(b.ToString("X2"));
        }

        return text.ToString();
    }

    private static string AsVisibleText(ReadOnlySpan<byte> bytes)
    {
        StringBuilder text = new(bytes.Length);
        foreach (byte b in bytes)
        {
            _ = text.Append(b is >= (byte)' ' and <= (byte)'~' ? (char)b : '?');
        }

        return text.ToString();
    }

    // ---- manifest reading --------------------------------------------------

    private static IEnumerable<(string Name, JsonElement Value)> EnumerateProperties(byte[] json, string what)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new LinkException($"room container {what} is not JSON: {exception.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LinkException(
                    $"room container {what} is {document.RootElement.ValueKind}, expected an object.");
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                yield return (property.Name, property.Value.Clone());
            }
        }
    }

    private static string ReadString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new LinkException($"room manifest field \"{name}\" is not a string.");
        }

        return value.GetString() ?? throw new LinkException($"room manifest field \"{name}\" is null.");
    }

    private static float ReadSingle(JsonElement value, string name)
    {
        if (!value.TryGetSingle(out float number))
        {
            throw new LinkException($"room manifest field \"{name}\" is not a number.");
        }

        return number;
    }

    private static (float Width, float Height, float Depth) ReadKit(JsonElement kit)
    {
        if (kit.ValueKind != JsonValueKind.Object)
        {
            throw new LinkException("room manifest kit is not an object.");
        }

        float width = 0f, height = 0f, depth = 0f;
        bool seenWidth = false, seenHeight = false, seenDepth = false;
        foreach (JsonProperty member in kit.EnumerateObject())
        {
            switch (member.Name)
            {
                case WidthKey:
                    width = ReadSingle(member.Value, WidthKey);
                    seenWidth = true;
                    break;
                case HeightKey:
                    height = ReadSingle(member.Value, HeightKey);
                    seenHeight = true;
                    break;
                case DepthKey:
                    depth = ReadSingle(member.Value, DepthKey);
                    seenDepth = true;
                    break;
                default:
                    throw new LinkException($"room manifest kit has an unknown field \"{member.Name}\".");
            }
        }

        if (!seenWidth || !seenHeight || !seenDepth)
        {
            throw new LinkException("room manifest kit needs width, height and depth.");
        }

        return (width, height, depth);
    }

    private static RoomSocket[] ReadSockets(JsonElement sockets)
    {
        if (sockets.ValueKind != JsonValueKind.Array)
        {
            throw new LinkException("room manifest sockets is not an array.");
        }

        List<RoomSocket> list = [];
        foreach (JsonElement socket in sockets.EnumerateArray())
        {
            if (socket.ValueKind != JsonValueKind.Object)
            {
                throw new LinkException("a room manifest socket is not an object.");
            }

            int facing = -1;
            string? name = null;
            foreach (JsonProperty member in socket.EnumerateObject())
            {
                switch (member.Name)
                {
                    case FacingKey:
                        if (!member.Value.TryGetInt32(out int raw) || !Enum.IsDefined((RoomFacing)raw))
                        {
                            throw new LinkException(
                                $"room manifest socket facing {member.Value} is not a facing.");
                        }

                        facing = raw;
                        break;
                    case SocketNameKey:
                        name = ReadString(member.Value, SocketNameKey);
                        break;
                    default:
                        throw new LinkException($"a room manifest socket has an unknown field \"{member.Name}\".");
                }
            }

            if (facing < 0 || name is null)
            {
                throw new LinkException("a room manifest socket needs both facing and name.");
            }

            list.Add(new RoomSocket((RoomFacing)facing, name));
        }

        return [.. list];
    }

    private static int[] ReadInt32Array(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new LinkException($"room manifest field \"{name}\" is not an array.");
        }

        List<int> values = [];
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (!element.TryGetInt32(out int number))
            {
                throw new LinkException($"room manifest field \"{name}\" has a non-integer element {element}.");
            }

            values.Add(number);
        }

        return [.. values];
    }

    private static string CellText(float cellSize) =>
        cellSize.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static string KitText(SocketKit kit) =>
        kit.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
        + kit.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
        + kit.Depth.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
