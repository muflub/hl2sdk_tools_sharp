using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The <c>.room</c> container: what <c>ssmap room</c> writes and <c>ssmap link</c>
/// reads. The round-trip claim is the format's whole reason to exist — a
/// reloaded room must be what the linker consumes — so the facts compare every
/// byte the linker reads, not the container's own plumbing.
/// </summary>
public sealed class RoomObjectStoreTests
{
    /// <summary>
    /// Save then load a compiled room: every field the linker reads survives
    /// byte-identically — all 64 lump payloads with their header versions and
    /// uncompressed sizes, the game-lump directory, both vis row sets, the
    /// definition, and the seal clusters in socket order.
    /// </summary>
    [Fact]
    public async Task RoundTripPreservesEveryFieldTheLinkerReads()
    {
        RoomObject room = await CompileHubAsync();
        RoomObject loaded = await RoundTripAsync(room);

        // The definition the linker matches sockets and placements against.
        Assert.Equal(room.Definition.Name, loaded.Definition.Name);
        Assert.Equal(room.Definition.CellSize, loaded.Definition.CellSize);
        Assert.Equal(room.Definition.Kit, loaded.Definition.Kit);
        Assert.True(
            room.Definition.Sockets.SequenceEqual(loaded.Definition.Sockets),
            "the sockets, in order, are the socket identity the joints name");

        // The compile, lump for lump.
        Assert.Equal(room.Bsp.FileVersion, loaded.Bsp.FileVersion);
        Assert.Equal(room.Bsp.MapRevision, loaded.Bsp.MapRevision);
        for (int lump = 0; lump < BspData.HeaderLumps; lump++)
        {
            Assert.Equal(room.Bsp[lump].Version, loaded.Bsp[lump].Version);
            Assert.Equal(room.Bsp[lump].UncompressedSize, loaded.Bsp[lump].UncompressedSize);
            Assert.True(
                room.Bsp[lump].Data.Span.SequenceEqual(loaded.Bsp[lump].Data.Span),
                $"lump {lump} does not survive byte-identically");
        }
        Assert.Equal(room.Bsp.GameLumps.Count, loaded.Bsp.GameLumps.Count);
        for (int entry = 0; entry < room.Bsp.GameLumps.Count; entry++)
        {
            // GameLumpEntry is a record struct whose Data is a ReadOnlyMemory:
            // the generated equality compares the memories by reference, so
            // the payload has to be checked as bytes for the round-trip to
            // mean anything.
            Assert.Equal(room.Bsp.GameLumps[entry].Id, loaded.Bsp.GameLumps[entry].Id);
            Assert.Equal(room.Bsp.GameLumps[entry].Flags, loaded.Bsp.GameLumps[entry].Flags);
            Assert.Equal(room.Bsp.GameLumps[entry].Version, loaded.Bsp.GameLumps[entry].Version);
            Assert.True(
                room.Bsp.GameLumps[entry].Data.Span.SequenceEqual(loaded.Bsp.GameLumps[entry].Data.Span),
                $"game lump {entry} does not survive byte-identically");
        }

        // The vis rows, which the linker ORs into the level's PVS.
        Assert.Equal(room.Vis.ClusterCount, loaded.Vis.ClusterCount);
        Assert.Equal(room.Vis.PortalCount, loaded.Vis.PortalCount);
        Assert.Equal(room.Vis.RowBytes, loaded.Vis.RowBytes);
        Assert.Equal(room.Vis.VisDataSize, loaded.Vis.VisDataSize);
        Assert.Equal(room.Vis.TotalVisibleClusters, loaded.Vis.TotalVisibleClusters);
        Assert.Equal(room.Vis.DeepestFlow, loaded.Vis.DeepestFlow);
        Assert.Equal(room.Vis.Work, loaded.Vis.Work);
        for (int cluster = 0; cluster < room.Vis.ClusterCount; cluster++)
        {
            Assert.True(
                room.Vis.Pvs(cluster).ToArray().SequenceEqual(loaded.Vis.Pvs(cluster).ToArray()),
                $"PVS row {cluster} does not survive");
            Assert.True(
                room.Vis.Pas(cluster).ToArray().SequenceEqual(loaded.Vis.Pas(cluster).ToArray()),
                $"PAS row {cluster} does not survive");
        }

        // The door graph's raw material: each socket's plug cluster.
        Assert.True(
            room.SealClusters.SequenceEqual(loaded.SealClusters),
            "the seal clusters do not survive in socket order");

        // §10a: the cache keys rebuild in the compiler's own format.
        Assert.True(
            room.InputKeys.SequenceEqual(loaded.InputKeys),
            "the cache keys do not rebuild identically");
    }

    /// <summary>
    /// The fourth work counter is persisted, not re-derived: the vis blob's
    /// BaseRays field is the last 8 bytes before the row payloads, and a
    /// flipped byte there must reach the loaded counters. (p15 added BaseRays
    /// to VisWorkCounters; a store whose reader ignored the new field would
    /// round-trip every OTHER field and still report zero base rays.)
    /// </summary>
    [Fact]
    public async Task TheBaseRaysCounterIsTheBlobNotARecompute()
    {
        RoomObject room = await CompileHubAsync();
        // The hub room is small enough that its base pass casts nothing on
        // some builds, so the fact cannot premise a non-zero stored value.
        // it works byte-wise: flipping the field's low bit must change what
        // the reader reports for THAT counter and only that one, whether the
        // stored value was 0 or not. A reader that ignored BaseRays would
        // report 0 for the flipped file and 0 for the clean one: equal, RED.
        byte[] good = await SaveAsync(room);
        int rowSpan = room.Vis.ClusterCount * room.Vis.RowBytes;

        // The vis blob is the container's last section, so its counter block
        // sits 8 + 2*rowSpan + 8 bytes from the end: the PVS and PAS blobs
        // (4-byte length + payload each) trail four 8-byte counters.
        int baseRaysStart = good.Length - (8 + 2 * rowSpan) - 8;
        byte[] flipped = (byte[])good.Clone();
        flipped[baseRaysStart] ^= 0x01;
        RoomObject altered = await LoadAsync(flipped);
        Assert.NotEqual(room.Vis.Work.BaseRays, altered.Vis.Work.BaseRays);

        // The other three counters still read from their own bytes — the flip
        // was surgical, so the field boundaries are where they were computed.
        Assert.Equal(room.Vis.Work.Chains, altered.Vis.Work.Chains);
        Assert.Equal(room.Vis.Work.Candidates, altered.Vis.Work.Candidates);
        Assert.Equal(room.Vis.Work.SeparatorClips, altered.Vis.Work.SeparatorClips);
    }

    /// <summary>
    /// The mutation proofs for the fact above. Corrupt the BSP blob's first
    /// lump header version and the loaded room disagrees with the compile it
    /// came from; corrupt one PAS byte and a row changes — proof the blobs
    /// (not a recompute) are the source. Then flip a manifest seal cluster and
    /// the load is refused, because the claim no longer matches the leaves.
    /// </summary>
    [Fact]
    public async Task CorruptedBlobsAndEditedManifestAreCaught()
    {
        RoomObject room = await CompileHubAsync();
        byte[] good = await SaveAsync(room);
        // The BSP blob begins after magic+version+manifest-length+manifest;
        // its bytes 8..12 are the first lump's recorded version.
        int manifestLength = ReadBE(good, 12);
        int bspStart = 20 + manifestLength;
        byte[] shiftedLump = (byte[])good.Clone();
        shiftedLump[bspStart + 8] ^= 0x1F;
        RoomObject shifted = await LoadAsync(shiftedLump);
        Assert.NotEqual(room.Bsp[0].Version, shifted.Bsp[0].Version);

        // The vis rows end the container — the PAS payload is the file's
        // last rowSpan bytes. Flip one bit in a row and the loaded room's
        // visibility differs from the compile it claims to be. A store that
        // dropped the blob and re-derived rows from the lumps would pass the
        // round-trip fact; this one notices.
        Assert.True(room.Vis.ClusterCount > 0 && room.Vis.RowBytes > 0, "the fixture room must have vis rows");
        int rowSpan = room.Vis.ClusterCount * room.Vis.RowBytes;
        byte[] lastByte = (byte[])good.Clone();
        lastByte[lastByte.Length - rowSpan + rowSpan / 2] ^= 0x08; // mid-PAS-payload
        RoomObject corrupted = await LoadAsync(lastByte);
        bool anyRowDiffers = false;
        for (int cluster = 0; cluster < room.Vis.ClusterCount; cluster++)
        {
            anyRowDiffers |= room.Vis.Pas(cluster).ToArray()
                .SequenceEqual(corrupted.Vis.Pas(cluster).ToArray()) is false;
        }

        Assert.True(anyRowDiffers, "a flipped PAS byte did not reach the loaded rows: the blob is not the source");

        // A manifest whose seal clusters were edited apart from the compile's
        // leaves is refused, naming both accounts.
        byte[] edited = (byte[])good.Clone();
        byte[] marker = System.Text.Encoding.ASCII.GetBytes("\"socketClusters\":[");
        int at = IndexOf(edited, marker);
        Assert.True(at > 0, "the manifest must record the socket clusters");
        byte digit = edited[at + marker.Length];
        edited[at + marker.Length] = digit == (byte)'9' ? (byte)'7' : (byte)'9';

        using MemoryStream stream = new(edited);
        LinkException refusal = await Assert.ThrowsAsync<LinkException>(() => RoomObjectStore.LoadAsync(stream));
        Assert.Contains("seals cluster", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file that is not a room container is refused with the observed bytes
    /// in its message, and so is a container from a version this build cannot
    /// read — a silent misread of either would link garbage.
    /// </summary>
    [Fact]
    public async Task BadMagicAndUnknownVersionAreRefusedNamingTheObservedBytes()
    {
        RoomObject room = await CompileHubAsync();
        byte[] good = await SaveAsync(room);

        // Wrong magic: an.bsp's id, which is what a user will feed it first.
        byte[] notRoom = (byte[])good.Clone();
        byte[] bspId = [0x56, 0x53, 0x42, 0x50, 0, 0, 0, 0]; // "VSPB" version 20
        Array.Copy(bspId, notRoom, 8);
        using (MemoryStream stream = new(notRoom))
        {
            LinkException refusal = await Assert.ThrowsAsync<LinkException>(() => RoomObjectStore.LoadAsync(stream));
            Assert.Contains("56 53 42 50", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("SSROOM01", refusal.Message, StringComparison.Ordinal);
        }

        // Unknown version: one above what this build reads, big-endian at
        // offset 8. Derived from the constant, not a literal — the container
        // version is bumped when the vis blob's counter list grows, and a
        // hard-coded "future" version would quietly become the current one.
        int future2 = RoomObjectStore.ContainerVersion + 1;
        byte[] future = (byte[])good.Clone();
        future[8] = 0;
        future[9] = 0;
        future[10] = 0;
        future[11] = (byte)future2;
        using (MemoryStream stream = new(future))
        {
            LinkException refusal = await Assert.ThrowsAsync<LinkException>(() => RoomObjectStore.LoadAsync(stream));
            Assert.Contains($"version {future2}", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("00 00 00 03", refusal.Message, StringComparison.Ordinal);
        }

        // Truncation names the section it died in.
        using (MemoryStream stream = new(good, 0, good.Length / 2))
        {
            LinkException refusal = await Assert.ThrowsAsync<LinkException>(() => RoomObjectStore.LoadAsync(stream));
            Assert.Contains("truncated in", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>The container's stable bytes: magic, version, then length-prefixed sections.</summary>
    [Fact]
    public async Task TheHeaderIsBigEndianLengthPrefixedBytes()
    {
        RoomObject room = await CompileHubAsync();
        byte[] bytes = await SaveAsync(room);

        Assert.Equal("SSROOM01", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal(0, bytes[8]);
        Assert.Equal(0, bytes[9]);
        Assert.Equal(0, bytes[10]);
        Assert.Equal(RoomObjectStore.ContainerVersion, bytes[11]);

        int manifestLength = ReadBE(bytes, 12);
        Assert.InRange(manifestLength, 64, bytes.Length - 24);
        string manifest = System.Text.Encoding.UTF8.GetString(bytes, 16, manifestLength);
        Assert.Contains("\"sourceHash\":\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"toolIdentity\":\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"socketClusters\":[", manifest, StringComparison.Ordinal);
    }

    private static async Task<RoomObject> RoundTripAsync(RoomObject room)
    {
        byte[] bytes = await SaveAsync(room);
        return await LoadAsync(bytes);
    }

    private static async Task<byte[]> SaveAsync(RoomObject room)
    {
        using MemoryStream file = new();
        await RoomObjectStore.SaveAsync(room, file);
        return file.ToArray();
    }

    private static async Task<RoomObject> LoadAsync(byte[] bytes)
    {
        using MemoryStream file = new(bytes);
        return await RoomObjectStore.LoadAsync(file);
    }

    private static int ReadBE(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static async Task<RoomObject> CompileHubAsync()
    {
        RoomDefinition hub = RoomHarness.Room("hub",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        VbspContext context = await RoomHarness.ContextAsync();
        return await RoomCompiler
            .CompileAsync(RoomHarness.BuildRoomModel(hub), hub, context);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
            {
                hit &= haystack[i + j] == needle[j];
            }

            if (hit)
            {
                return i;
            }
        }

        return -1;
    }
}
