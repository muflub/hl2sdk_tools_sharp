//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Bsp.SurfaceContent;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The link carries the rooms' packed files (the rooms design, 4.13): rooms
/// compiled against a sky that resolves pack their default cubemaps, and a
/// level placing them gets one pak holding every placed room's files,
/// merged by name, the default cubemaps under the level's map name. Each
/// fact reads the linked pak lump directly.
/// </summary>
public sealed class LevelLinkerPackedFilesTests : IClassFixture<LevelLinkerPackedFilesTests.SkyRooms>
{
    private const string Sky = "sky_unit";
    private const string CubeName = "materials/maps/level/cubemapdefault.vtf";
    private const string HdrCubeName = "materials/maps/level/cubemapdefault.hdr.vtf";

    private readonly SkyRooms _rooms;

    /// <summary>Takes the rooms compiled once for the class.</summary>
    public LevelLinkerPackedFilesTests(SkyRooms rooms)
    {
        _rooms = rooms;
    }

    /// <summary>
    /// Two rooms, <c>hub</c> and <c>hall</c>, compiled against a sky that
    /// resolves, as a real game's does: vbsp packs each one's default
    /// cubemaps under its own name.
    /// </summary>
    public sealed class SkyRooms : IAsyncLifetime
    {
        /// <summary>The hub room.</summary>
        public RoomObject Hub { get; private set; } = null!;

        /// <summary>The hall room: the hub's shape under another name.</summary>
        public RoomObject Hall { get; private set; } = null!;

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            Hub = await CompileAsync(RoomHarness.Hub());
            Hall = await CompileAsync(RoomHarness.Room(
                "hall", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY));
        }

        /// <inheritdoc/>
        public Task DisposeAsync() => Task.CompletedTask;

        private static async Task<RoomObject> CompileAsync(RoomDefinition definition)
        {
            VmfDocument document = RoomHarness.BuildRoomModel(definition);
            document.GetChunk(MapFileLoader.WorldChunk)!.AddKey("skyname", Sky);
            await using ContentFileSystem content = await SkyContentAsync();
            VbspContext context = new(VbspOptions.Default, content) { MapBase = definition.Name };
            return await RoomCompiler.CompileAsync(document, definition, context);
        }
    }

    // ---- what a real room packs, and where it lands ---------------------------

    /// <summary>
    /// The room compile packs vbsp's default cubemaps under the room's name,
    /// and the linked level holds them once, under the level's map name, the
    /// same bytes, however many rooms and placements brought them.
    /// </summary>
    [Fact]
    public async Task TheDefaultCubemapsLandOnceUnderTheLevelsName()
    {
        ZipArchiveReader hubPak = await PakOf(_rooms.Hub.Bsp);
        Assert.Equal(
            ["materials/maps/hub/cubemapdefault.vtf", "materials/maps/hub/cubemapdefault.hdr.vtf"],
            hubPak.Entries.Select(e => e.Name));

        LinkedLevel linked = await LinkAsync(_rooms.Hub, _rooms.Hall, "level");
        ZipArchiveReader pak = await PakOf(linked.Bsp);
        Assert.Equal([HdrCubeName, CubeName], pak.Entries.Select(e => e.Name));
        Assert.Equal(hubPak.Entries[0].Data, pak.Find(CubeName)!.Data);
        Assert.Equal(hubPak.Entries[1].Data, pak.Find(HdrCubeName)!.Data);
        Assert.Equal(2, linked.PackedFiles);
    }

    /// <summary>
    /// A room with packed content (its default cubemaps, a patched material
    /// named after it, a file its author embedded) and a second room with
    /// its own, linked into a level placing the first twice: the pak holds
    /// exactly the expected entries, each with its room's bytes. The shared
    /// file is written once, each room's patch keeps the room's name, and
    /// the default cubemaps are the level's.
    /// </summary>
    [Fact]
    public async Task ALevelsPakHoldsExactlyTheExpectedEntries()
    {
        byte[] hum = [1, 2, 3, 4, 5];
        byte[] hubPatch = "\"patch\" { \"include\" \"materials/unit/plain.vmt\" }\r\n"u8.ToArray();
        byte[] hallPatch = "\"patch\" { \"include\" \"materials/unit/other.vmt\" }\r\n"u8.ToArray();
        RoomObject hub = await WithFilesAsync(_rooms.Hub, ("materials/maps/hub/unit/plain_wvt_patch.vmt", hubPatch), ("sound/rooms/hum.wav", hum));
        RoomObject hall = await WithFilesAsync(_rooms.Hall, ("materials/maps/hall/unit/plain_wvt_patch.vmt", hallPatch), ("sound/rooms/hum.wav", hum));

        LinkedLevel linked = await LinkAsync(hub, hall, "level");
        ZipArchiveReader pak = await PakOf(linked.Bsp);
        ZipArchiveReader hubPak = await PakOf(_rooms.Hub.Bsp);

        Dictionary<string, byte[]> expected = new(StringComparer.Ordinal)
        {
            ["materials/maps/hall/unit/plain_wvt_patch.vmt"] = hallPatch,
            ["materials/maps/hub/unit/plain_wvt_patch.vmt"] = hubPatch,
            [HdrCubeName] = hubPak.Entries[1].Data,
            [CubeName] = hubPak.Entries[0].Data,
            ["sound/rooms/hum.wav"] = hum,
        };
        Assert.Equal(expected.Keys, pak.Entries.Select(e => e.Name));
        foreach (ZipEntry entry in pak.Entries)
        {
            Assert.Equal(expected[entry.Name], entry.Data);
        }

        Assert.Equal(5, linked.PackedFiles);
    }

    /// <summary>Two rooms of a level packing one file with different bytes are refused with the design's message (15.4).</summary>
    [Fact]
    public async Task TwoRoomsPackingOneFileDifferentlyAreRefused()
    {
        RoomObject hub = await WithFilesAsync(_rooms.Hub, ("sound/rooms/hum.wav", [1]));
        RoomObject hall = await WithFilesAsync(_rooms.Hall, ("sound/rooms/hum.wav", [2]));
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(hub, hall, "level"));
        Assert.Equal("rooms hub and hall both pack sound/rooms/hum.wav with different bytes.", refused.Message);
    }

    /// <summary>
    /// A link given no map name cannot rename the default cubemaps, and says
    /// so, rather than carrying them where nothing reads them.
    /// </summary>
    [Fact]
    public async Task WithoutAMapNameTheDefaultCubemapsAreRefused()
    {
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(_rooms.Hub, _rooms.Hall, string.Empty));
        Assert.StartsWith("room hub packs materials/maps/hub/cubemapdefault.vtf, which is named after its map;", refused.Message, StringComparison.Ordinal);
    }

    // ---- through the pack ----------------------------------------------------------

    /// <summary>
    /// A room with packed files round-trips through a pack: its pak lump
    /// comes back byte for byte inside its container, it gets link data at
    /// pack time (a room with a packed file used to get none, being one the
    /// link refused), and a level linked from the loaded rooms is the same
    /// bytes as one linked from the rooms in memory.
    /// </summary>
    [Fact]
    public async Task ARoomWithPackedFilesRoundTripsThroughAPack()
    {
        RoomObject hub = await WithFilesAsync(_rooms.Hub, ("sound/rooms/hum.wav", [1, 2, 3]));
        Assert.NotNull(await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None));

        using MemoryStream pack = new();
        await RoomPack.SaveAsync([await RoomPackItem.CreateAsync(hub), await RoomPackItem.CreateAsync(_rooms.Hall)], pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(pack, index, ["hub", "hall"]);

        Assert.True(loaded[0].Bsp[BspLump.PakFile].Data.Span.SequenceEqual(hub.Bsp[BspLump.PakFile].Data.Span));
        Assert.True(loaded[1].Bsp[BspLump.PakFile].Data.Span.SequenceEqual(_rooms.Hall.Bsp[BspLump.PakFile].Data.Span));
        Assert.NotNull(loaded[0].Link);
        Assert.True(loaded[0].Link!.IsFor(loaded[0]));

        Assert.Equal(await LinkBytesAsync(hub, _rooms.Hall, 1), await LinkBytesAsync(loaded[0], loaded[1], 1));
    }

    // ---- nothing else moves --------------------------------------------------------

    /// <summary>
    /// Packed files cost no entity (15.6): the linked entity lump and the
    /// budget are those of the same rooms with empty paks, and every lump but
    /// the pak is the same bytes.
    /// </summary>
    [Fact]
    public async Task PackedFilesMoveOnlyThePak()
    {
        LinkedLevel packed = await LinkAsync(_rooms.Hub, _rooms.Hall, "level");
        LinkedLevel bare = await LinkAsync(await WithoutFilesAsync(_rooms.Hub), await WithoutFilesAsync(_rooms.Hall), "level");

        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            if ((BspLump)i != BspLump.PakFile)
            {
                Assert.True(packed.Bsp[i].Data.Span.SequenceEqual(bare.Bsp[i].Data.Span), $"lump {(BspLump)i} moved");
            }
        }

        Assert.Equal(bare.EntityBudget!.Edicts, packed.EntityBudget!.Edicts);
        Assert.Equal(bare.EntityBudget.Listed, packed.EntityBudget.Listed);
        Assert.Equal(0, bare.PackedFiles);
        Assert.Empty((await PakOf(bare.Bsp)).Entries);
    }

    /// <summary>
    /// A level none of whose rooms packs a file carries its first room's pak
    /// lump byte for byte, as the link always has, so a level without packed
    /// files links to the bytes it always did.
    /// </summary>
    [Fact]
    public async Task NothingPackedKeepsTheFirstRoomsLump()
    {
        RoomObject hub = await WithoutFilesAsync(_rooms.Hub);
        LinkedLevel bare = await LinkAsync(hub, await WithoutFilesAsync(_rooms.Hall), "level");
        Assert.True(bare.Bsp[BspLump.PakFile].Data.Span.SequenceEqual(hub.Bsp[BspLump.PakFile].Data.Span));
    }

    /// <summary>
    /// The same bytes at one thread and at four, twice each (15.5): the merge
    /// is sequential and in name order, so the pak and the map do not depend
    /// on the degree or the run.
    /// </summary>
    [Fact]
    public async Task TheLinkIsTheSameBytesAtAnyDegree()
    {
        RoomObject hub = await WithFilesAsync(_rooms.Hub, ("sound/rooms/hum.wav", [1, 2]), ("a/first.txt", [3]));
        RoomObject hall = await WithFilesAsync(_rooms.Hall, ("z/last.txt", [4]));
        byte[] first = await LinkBytesAsync(hub, hall, 1);
        Assert.Equal(first, await LinkBytesAsync(hub, hall, 1));
        Assert.Equal(first, await LinkBytesAsync(hub, hall, 4));
        Assert.Equal(first, await LinkBytesAsync(hub, hall, 4));
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>The harness materials and a sky whose six faces resolve.</summary>
    internal static async Task<ContentFileSystem> SkyContentAsync()
    {
        InMemoryFileSystem files = new();
        files.AddText($"materials/{RoomHarness.Plain}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        files.AddText(
            $"materials/{RoomHarness.Trigger}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        SurfaceUnit.AddSky(files, Sky, (int)ImageFormat.Bgr888, 0x0304);
        return new ContentFileSystem([await DirectoryContentMount.MountAsync(files, VPath.Empty)]);
    }

    private static async Task<ZipArchiveReader> PakOf(BspData bsp) =>
        await ZipArchiveReader.ParseAsync(bsp[BspLump.PakFile].Data);

    /// <summary>A copy of a room whose pak holds its own files and the given ones after them.</summary>
    private static async Task<RoomObject> WithFilesAsync(RoomObject room, params (string Name, byte[] Data)[] files)
    {
        ZipArchiveWriter writer = (await PakOf(room.Bsp)).ToWriter();
        foreach ((string name, byte[] data) in files)
        {
            writer.Add(name, data);
        }

        byte[] pak = writer.ToBytes();
        return RoomHarness.WithLumps(room, bsp => bsp.SetLump(BspLump.PakFile, pak));
    }

    /// <summary>A copy of a room whose pak is empty, as a room compiled without a sky packs.</summary>
    private static async Task<RoomObject> WithoutFilesAsync(RoomObject room)
    {
        ZipArchiveReader pak = await PakOf(room.Bsp);
        byte[] empty = new ZipArchiveWriter { Comment = pak.Comment }.ToBytes();
        return RoomHarness.WithLumps(room, bsp => bsp.SetLump(BspLump.PakFile, empty));
    }

    /// <summary>Links hub at (0,0), hall at (1,0) and hub again at (2,0), under the given map name.</summary>
    private static async Task<LinkedLevel> LinkAsync(RoomObject hub, RoomObject hall, string mapBase, int degree = 1)
    {
        RoomLibrary library = RoomHarness.Library(hub, hall);
        LevelLayout layout = RoomHarness.AutoLayout("level", library, ("hub", 0, 0, 0), ("hall", 1, 0, 1), ("hub", 2, 0, 2));
        await using ContentFileSystem content = new([]);
        VbspContext context = new(VbspOptions.Default, content)
        {
            MapBase = mapBase,
            Parallelism = new CompileParallelism { MaxDegree = degree },
        };
        return await LevelLinker.LinkAsync(layout, library, context);
    }

    private static async Task<byte[]> LinkBytesAsync(RoomObject hub, RoomObject hall, int degree)
    {
        LinkedLevel linked = await LinkAsync(hub, hall, "level", degree);
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }
}
