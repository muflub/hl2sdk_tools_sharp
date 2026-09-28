//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The room container: what <c>ssmap room</c> packs for each room and <c>ssmap link</c>
/// reads. The round-trip claim is the format's whole reason to exist — a
/// reloaded room must be what the linker consumes — so the facts compare every
/// byte the linker reads, not the container's own plumbing.
/// </summary>
public sealed class RoomObjectStoreTests
{
    /// <summary>
    /// The vis blob's fixed fields ahead of the rows: seven big-endian ints,
    /// the radius flag byte and the radius as eight bytes.
    /// </summary>
    private const int VisFixedBytes = (7 * 4) + 1 + 8;

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
        Assert.Equal(room.Vis.OptimizedClusters, loaded.Vis.OptimizedClusters);
        Assert.Equal(room.Vis.TotalAudibleClusters, loaded.Vis.TotalAudibleClusters);
        Assert.Equal(room.Vis.UsedRadius, loaded.Vis.UsedRadius);
        Assert.Equal(room.Vis.VisRadiusSquared, loaded.Vis.VisRadiusSquared);

        // How the flow got there is not in the file (it depends on the
        // schedule; see TheVisBlobCarriesNothingTheScheduleDecides): a loaded
        // room reports no flow, as a room that ran none.
        Assert.Equal(0, loaded.Vis.DeepestFlow);
        Assert.Equal(VisWorkCounters.Zero, loaded.Vis.Work);
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
    /// The file is a function of the room, not of the schedule that compiled
    /// it: two rooms that differ ONLY in the flow's work counters and deepest
    /// flow -- what two compiles of one room at more than one worker produce
    /// (RoomReproducibilityTests forces that schedule) -- write the same
    /// bytes, and the vis blob is exactly the fixed fields and the two row
    /// sets, with no room left for a counter.
    /// </summary>
    /// <remarks>
    /// Container version 2 wrote the deepest flow and the four counters, and
    /// the 3x3 sample's rooms came out with different bytes on every compile.
    /// </remarks>
    [Fact]
    public async Task TheVisBlobCarriesNothingTheScheduleDecides()
    {
        RoomObject room = await CompileHubAsync();
        VisResult vis = room.Vis;
        VisResult otherRun = new(
            vis.ClusterCount, vis.PortalCount, vis.RowBytes, vis.PvsBytes.ToArray(), vis.PasBytes.ToArray(),
            vis.VisDataSize, vis.TotalVisibleClusters, vis.OptimizedClusters, vis.TotalAudibleClusters,
            vis.UsedRadius, vis.VisRadiusSquared,
            deepestFlow: vis.DeepestFlow + 7,
            vis.Work + new VisWorkCounters(Chains: 11, Candidates: 13, SeparatorClips: 17, BaseRays: 19),
            trace: null);

        byte[] bytes = await SaveAsync(room);
        Assert.Equal(bytes, await SaveAsync(room with { Vis = otherRun }));

        // Seven ints, the radius flag and the radius, then the two rows.
        int rowSpan = vis.ClusterCount * vis.RowBytes;
        (_, _, byte[] blob) = Split(bytes);
        Assert.Equal(VisFixedBytes + 4 + rowSpan + 4 + rowSpan, blob.Length);
    }

    /// <summary>
    /// The mutation proofs for the round-trip fact: edits the reader has no
    /// grounds to refuse still reach what it returns, so the blobs (not a
    /// recompute) are the source. A lump's recorded version is opaque to the
    /// store, and a PAS row's own cluster bit is a legal row either way; both
    /// load, and both differ from the compile they were written from.
    /// </summary>
    [Fact]
    public async Task TheBlobsAreTheSourceNotARecompute()
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
        // last rowSpan bytes. Flip row 0's own bit (bit 0 of its first byte,
        // which is never padding) and the loaded visibility differs.
        Assert.True(room.Vis.ClusterCount > 0 && room.Vis.RowBytes > 0, "the fixture room must have vis rows");
        int rowSpan = room.Vis.ClusterCount * room.Vis.RowBytes;
        byte[] flipped = (byte[])good.Clone();
        flipped[flipped.Length - rowSpan] ^= 0x01;
        RoomObject corrupted = await LoadAsync(flipped);
        Assert.False(
            room.Vis.Pas(0).SequenceEqual(corrupted.Vis.Pas(0)),
            "a flipped PAS bit did not reach the loaded rows: the blob is not the source");
    }

    /// <summary>
    /// A set bit past the last cluster in a row is refused: it names a
    /// cluster the room does not have, which the linker would shift into the
    /// next room's range. Both row sets are checked.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APaddingBitInARowIsRefused(bool pas)
    {
        RoomObject room = await CompileHubAsync();
        Assert.True(room.Vis.ClusterCount % 8 != 0, "the fixture room's rows must have padding bits");
        byte[] bytes = await SaveAsync(room);
        int rowSpan = room.Vis.ClusterCount * room.Vis.RowBytes;

        // The PAS payload is the file's last rowSpan bytes; the PVS payload
        // sits before the PAS blob's four-byte length.
        int rowStart = pas ? bytes.Length - rowSpan : bytes.Length - rowSpan - 4 - rowSpan;
        bytes[rowStart + room.Vis.RowBytes - 1] |= 0x80;

        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LoadAsync(bytes));
        Assert.Contains($"{(pas ? "PAS" : "PVS")} row 0 sets a padding bit", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leaf in a cluster past the vis's count is refused: once linked it
    /// would alias into the next room's clusters.
    /// </summary>
    [Fact]
    public async Task ALeafClusterPastTheVisIsRefused()
    {
        RoomObject room = await CompileHubAsync();
        RoomObject aliased = RoomHarness.WithLumps(room, bsp =>
        {
            SourceSharp.MapFormats.Bsp.Structs.DLeaf[] leafs =
                SourceSharp.MapFormats.Bsp.Structs.BspStructView.As<SourceSharp.MapFormats.Bsp.Structs.DLeaf>(bsp[BspLump.Leafs]).ToArray();
            leafs[^1].Cluster = (short)(room.ClusterCount + 3);
            bsp.SetLump(BspLump.Leafs,
                SourceSharp.MapFormats.Bsp.Structs.BspStructView.ToLump<SourceSharp.MapFormats.Bsp.Structs.DLeaf>(leafs, bsp[BspLump.Leafs].Version).Data,
                bsp[BspLump.Leafs].Version);
        });

        LinkException refused = await Assert.ThrowsAsync<LinkException>(async () => await LoadAsync(await SaveAsync(aliased)));
        Assert.Contains($"is in cluster {room.ClusterCount + 3}", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A vis blob whose row size does not fit its cluster count, or whose
    /// rows are not one per cluster, is refused before any row is sliced.
    /// </summary>
    [Theory]
    [InlineData("row bytes")]
    [InlineData("row count")]
    [InlineData("no clusters")]
    public async Task AVisBlobThatDoesNotFitItsClustersIsRefused(string fault)
    {
        RoomObject room = await CompileHubAsync();
        (byte[] manifest, byte[] bsp, byte[] vis) = Split(await SaveAsync(room));
        int rowSpan = room.Vis.ClusterCount * room.Vis.RowBytes;
        byte[] edited = fault switch
        {
            "row bytes" => WithInt(vis, 8, room.Vis.RowBytes + 1),
            "no clusters" => WithInt(vis, 0, 0),
            _ =>
            [
                .. vis[..VisFixedBytes], .. BE(rowSpan + 1),
                .. vis[(VisFixedBytes + 4)..(VisFixedBytes + 4 + rowSpan)], 0,
                .. vis[(VisFixedBytes + 4 + rowSpan)..],
            ],
        };

        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LoadAsync(Join(manifest, bsp, edited)));
        Assert.Contains(
            fault switch
            {
                "row bytes" => $"vis rows are {room.Vis.RowBytes + 1} bytes",
                "no clusters" => "has 0 clusters",
                _ => $"PVS rows hold {rowSpan + 1} bytes",
            },
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Content the store's own parsers refuse in their own vocabulary — a
    /// definition that does not validate, a lump that is not whole structs,
    /// a compile whose plug census cannot be walked — reaches the caller as
    /// the one <see cref="LinkException"/> a bad file is.
    /// </summary>
    [Theory]
    [InlineData("definition")]
    [InlineData("lump")]
    [InlineData("census")]
    public async Task EveryBadFileIsALinkException(string fault)
    {
        RoomObject room = await CompileHubAsync();
        byte[] bytes;
        if (fault == "definition")
        {
            (byte[] manifest, byte[] bsp, byte[] vis) = Split(await SaveAsync(room));
            string text = System.Text.Encoding.UTF8.GetString(manifest).Replace("\"cellSize\":256", "\"cellSize\":-256", StringComparison.Ordinal);
            bytes = Join(System.Text.Encoding.UTF8.GetBytes(text), bsp, vis);
        }
        else
        {
            bytes = await SaveAsync(RoomHarness.WithLumps(room, bsp =>
            {
                if (fault == "lump")
                {
                    bsp.SetLump(BspLump.Leafs, new byte[33], bsp[BspLump.Leafs].Version);
                }
                else
                {
                    ushort[] leafBrushes = SourceSharp.MapFormats.Bsp.Structs.BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]).ToArray();
                    Array.Fill(leafBrushes, (ushort)60000);
                    bsp.SetLump(BspLump.LeafBrushes, System.Runtime.InteropServices.MemoryMarshal.AsBytes(leafBrushes.AsSpan()).ToArray());
                }
            }));
        }

        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LoadAsync(bytes));
        Assert.StartsWith("room container content is not a linkable room:", refused.Message, StringComparison.Ordinal);
        if (fault == "census")
        {
            Assert.Contains("names brush 60000", refused.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A manifest field of the wrong JSON kind — a string where a number
    /// goes — is refused naming the field; the JSON reader's own
    /// InvalidOperationException for it never reaches the caller.
    /// </summary>
    [Theory]
    [InlineData("\"cellSize\":256", "\"cellSize\":\"256\"", "field \"cellSize\" is not a number")]
    [InlineData("\"facing\":0", "\"facing\":\"PositiveX\"", "socket facing PositiveX is not a facing")]
    [InlineData("\"socketClusters\":[0", "\"socketClusters\":[\"0\"", "has a non-integer element 0")]
    public async Task AManifestFieldOfTheWrongKindIsRefused(string from, string to, string expected)
    {
        (byte[] manifest, byte[] bsp, byte[] vis) = Split(await SaveAsync(await CompileHubAsync()));
        string text = System.Text.Encoding.UTF8.GetString(manifest);
        Assert.Contains(from, text, StringComparison.Ordinal);
        byte[] edited = System.Text.Encoding.UTF8.GetBytes(text.Replace(from, to, StringComparison.Ordinal));

        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LoadAsync(Join(edited, bsp, vis)));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A section length is four bytes of an untrusted file: a claim past
    /// the format's limit is refused outright, and a claim past what the
    /// stream holds is a truncation — on a stream that cannot say its length
    /// too, where it is read in bounded chunks rather than allocated whole.
    /// </summary>
    [Theory]
    [InlineData(0x7FFFFFFF, true, "section claims 2147483647 bytes")]
    [InlineData(0x3FFFFFFF, true, "truncated in manifest: wanted 1073741823 bytes")]
    [InlineData(0x3FFFFFFF, false, "truncated in manifest: wanted 1073741823 bytes")]
    public async Task ASectionLengthIsNotTrustedToAllocate(int length, bool seekable, string expected)
    {
        byte[] bytes = [.. System.Text.Encoding.ASCII.GetBytes(RoomObjectStore.ContainerMagic), .. BE(RoomObjectStore.ContainerVersion), .. BE(length), 1, 2, 3];
        using Stream stream = seekable ? new MemoryStream(bytes) : new ForwardOnly(bytes);

        // Both streams complete every read synchronously, so the whole load
        // runs on this thread and its allocations are this thread's: a
        // reader that allocated the claimed gigabyte shows here even though
        // it then fails the same way.
        long before = GC.GetAllocatedBytesForCurrentThread();
        Task<RoomObject> load = RoomObjectStore.LoadAsync(stream);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => load);
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.True(allocated < 16L << 20, $"a {length}-byte claim allocated {allocated} bytes before failing");
    }

    /// <summary>
    /// A room that did not come from a VMF has no source hash; the manifest
    /// leaves the field out, and the room loads back (it used to be written
    /// as JSON null, which the reader refused). An explicit null from such a
    /// file is read as "no hash" too.
    /// </summary>
    [Fact]
    public async Task ARoomWithoutASourceHashRoundTrips()
    {
        RoomObject room = (await CompileHubAsync()) with { InputKeys = [] };
        byte[] bytes = await SaveAsync(room);
        (byte[] manifest, byte[] bsp, byte[] vis) = Split(bytes);
        Assert.DoesNotContain("sourceHash", System.Text.Encoding.UTF8.GetString(manifest), StringComparison.Ordinal);

        RoomObject loaded = await LoadAsync(bytes);
        Assert.Single(loaded.InputKeys);
        Assert.StartsWith("room:hub|", loaded.InputKeys[0], StringComparison.Ordinal);

        string withNull = System.Text.Encoding.UTF8.GetString(manifest).Replace("\"toolIdentity\"", "\"sourceHash\":null,\"toolIdentity\"", StringComparison.Ordinal);
        RoomObject fromNull = await LoadAsync(Join(System.Text.Encoding.UTF8.GetBytes(withNull), bsp, vis));
        Assert.Equal(loaded.InputKeys, fromNull.InputKeys);
    }

    /// <summary>
    /// A manifest whose seal claim was edited apart from the compile's leaves
    /// is refused, naming both accounts. The lint derives every socket in
    /// order, so a file this store wrote always claims 0..n-1; the check is
    /// the container's consistency, and this is its refusal path.
    /// </summary>
    [Fact]
    public async Task AnEditedManifestSealClaimIsRefused()
    {
        RoomObject room = await CompileHubAsync();
        Assert.Equal(Enumerable.Range(0, room.Definition.Sockets.Count), room.SealClusters);
        byte[] good = await SaveAsync(room);
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
        // version is bumped whenever the vis blob's fixed fields change, and a
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
            Assert.Contains($"00 00 00 {future2:X2}", refusal.Message, StringComparison.Ordinal);
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

    /// <summary>The container's three sections: manifest, BSP blob, vis blob.</summary>
    private static (byte[] Manifest, byte[] Bsp, byte[] Vis) Split(byte[] container)
    {
        int at = 12;
        byte[] Next()
        {
            int length = ReadBE(container, at);
            byte[] section = container[(at + 4)..(at + 4 + length)];
            at += 4 + length;
            return section;
        }

        return (Next(), Next(), Next());
    }

    private static byte[] Join(byte[] manifest, byte[] bsp, byte[] vis) =>
        [.. System.Text.Encoding.ASCII.GetBytes(RoomObjectStore.ContainerMagic), .. BE(RoomObjectStore.ContainerVersion),
         .. BE(manifest.Length), .. manifest, .. BE(bsp.Length), .. bsp, .. BE(vis.Length), .. vis];

    private static byte[] BE(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] WithInt(byte[] bytes, int offset, int value)
    {
        byte[] copy = (byte[])bytes.Clone();
        BE(value).CopyTo(copy, offset);
        return copy;
    }

    /// <summary>A stream that reads forward and cannot say its length, like a pipe.</summary>
    private sealed class ForwardOnly(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
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
