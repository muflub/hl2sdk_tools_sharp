//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The link carries <c>env_cubemap</c> samples (the rooms design, 4.10):
/// every placement's samples at their linked positions, and every name the
/// room's compile made after a sample (its patched specular materials, as
/// texdata strings and as packed files with their text, and the sample's
/// default cubemap copies) renamed to the level's map name and the linked
/// position, as a vbsp compile of the flattened level names them.
/// </summary>
/// <remarks>
/// The library is the harness hub on the walkable kit, with a sky that
/// resolves (so vbsp writes the default cubemaps and their per-sample
/// copies) and two specular slab tops: one named by the first sample's
/// <c>sides</c>, one left to the nearest sample. The specular material has
/// a dependent (<c>$bottommaterial</c>) that is specular too, so its patch
/// names a second patch. The samples stand off the whole units, so a turn
/// that negates an origin would truncate it differently from the room's
/// own integers.
/// </remarks>
public sealed class LevelLinkerCubemapTests : IClassFixture<LevelLinkerCubemapTests.CubemapRooms>
{
    /// <summary>The level's map name, in the link and in the flattened compile.</summary>
    private const string Level = "level";

    private const string Sky = "sky_unit";
    private const string Shiny = "unit/shiny";
    private const string ShinyBottom = "unit/shinybottom";

    private readonly CubemapRooms _rooms;

    /// <summary>Takes the rooms compiled once for the class.</summary>
    public LevelLinkerCubemapTests(CubemapRooms rooms)
    {
        _rooms = rooms;
    }

    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    private static RoomDefinition HubDefinition => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    /// <summary>
    /// The harness rooms compiled once: the hub with its samples, and a hall
    /// of the same shape with none.
    /// </summary>
    public sealed class CubemapRooms : IAsyncLifetime
    {
        /// <summary>The hub, with two samples and specular slabs.</summary>
        public RoomObject Hub { get; private set; } = null!;

        /// <summary>A room with no sample.</summary>
        public RoomObject Hall { get; private set; } = null!;

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            LibraryRoom hub = RoomLibraryVmf.Split(LibraryVmf()).Single();
            VbspContext context = await ContextAsync(hub.Definition.Name);
            Hub = await RoomCompiler.CompileAsync(hub.Document, hub.Definition, context);

            RoomDefinition hall = RoomHarness.WalkableRoom(
                "hall", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
            VmfDocument document = RoomHarness.BuildRoomModel(hall);
            document.GetChunk(MapFileLoader.WorldChunk)!.AddKey("skyname", Sky);
            Hall = await RoomCompiler.CompileAsync(document, hall, await ContextAsync(hall.Name));
        }

        /// <inheritdoc/>
        public Task DisposeAsync() => Task.CompletedTask;
    }

    // ---- link and flatten agree -----------------------------------------------------

    /// <summary>
    /// The hub placed twice, the first turned, linked and flattened and
    /// compiled whole: the two maps carry the same samples in the same order
    /// (positions truncated from the turned floats, sizes kept), the same
    /// patched texdata names, and the same packed files byte for byte (the
    /// patched materials' text included); every patched face of the linked
    /// map names a sample of its own room; and <c>ssmap check</c> no longer
    /// warns that the map has no cubemap samples.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALinkedLevelsCubemapsAreTheFlattenedCompiles(int rotation)
    {
        (LinkedLevel linked, BspData whole) = await LinkAndCompileFlatAsync($"hub@{rotation}, hub");
        BspData map = linked.Bsp;

        Assert.Equal(4, BspStructView.Count<DCubemapSample>(map[BspLump.Cubemaps]));
        Assert.Equal(4, linked.CubemapSamples);
        Assert.True(whole[BspLump.Cubemaps].Data.Span.SequenceEqual(map[BspLump.Cubemaps].Data.Span), "the samples differ");
        Assert.Equal(6, BspStructView.As<DCubemapSample>(map[BspLump.Cubemaps])[0].Size);

        List<string> patches = Patches(map);
        Assert.Equal(Patches(whole), patches);
        Assert.Equal(4, patches.Count); // two slabs, two placements

        Dictionary<string, byte[]> files = await FilesAsync(map);
        Dictionary<string, byte[]> reference = await FilesAsync(whole);
        Assert.Equal(reference.Keys.Order(StringComparer.Ordinal), files.Keys.Order(StringComparer.Ordinal));
        foreach ((string name, byte[] bytes) in reference)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(files[name]), $"{name}: the link and the flattened compile pack different bytes");
        }

        // Four VTF pairs, the defaults, and per placement two patches and
        // the dependent's patch for the one slab each sample patched.
        Assert.Equal(8 + 2 + 8, files.Count);
        Assert.DoesNotContain(files.Keys, n => n.Contains("/hub/", StringComparison.Ordinal));

        AssertInRoomAssignment(map);

        ValidationReport report = await BspValidator.CheckAsync(map, CancellationToken.None);
        Assert.Equal(0, report.ErrorCount);
        Assert.True(report.ForCode(BspRuleCodes.NoCubemaps).IsEmpty);
    }

    /// <summary>
    /// A room with both a cubemap sample and an overlay (the overlay lying on
    /// a specular face a sample patches), placed twice with the first turned:
    /// at every turn the link and the flattened compile hold the same samples,
    /// patches and overlays (bit for bit but the overlays' face lists, each
    /// overlay's square wholly covered by its faces), the overlays' accessor
    /// names its own overlay, and the linked map passes <c>ssmap check</c>
    /// with no error and no cubemap warning.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ARoomWithACubemapAndAnOverlayLinksAsItFlattens(int rotation)
    {
        (LinkedLevel linked, BspData whole) = await LinkAndCompileFlatAsync(LibraryVmf(overlay: true), $"hub@{rotation}, hub");
        BspData map = linked.Bsp;

        Assert.Equal(4, BspStructView.Count<DCubemapSample>(map[BspLump.Cubemaps]));
        Assert.True(whole[BspLump.Cubemaps].Data.Span.SequenceEqual(map[BspLump.Cubemaps].Data.Span), "the samples differ");
        Assert.Equal(Patches(whole), Patches(map));

        Assert.Equal(2, RoomOverlayHarness.Overlays(map).Length);
        Assert.Equal(RoomOverlayHarness.Observed(whole), RoomOverlayHarness.Observed(map));
        Assert.All(RoomOverlayHarness.Overlays(map), o => Assert.Equal(1024.0, RoomOverlayHarness.Covered(map, o), 2));
        Assert.Equal(
            ["0", "1"],
            RoomOverlayHarness.OfClass(map, RoomOverlays.AccessorClass).Select(e => e.Get(RoomOverlays.IdKey)!));

        ValidationReport report = await BspValidator.CheckAsync(map, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
        Assert.True(report.ForCode(BspRuleCodes.NoCubemaps).IsEmpty);
    }

    /// <summary>
    /// A patched material's text in the linked pak: its <c>$envmap</c> names
    /// the linked sample's texture and its <c>$bottommaterial</c> the
    /// linked patch of the dependent, and nothing else of the room's file
    /// changed.
    /// </summary>
    [Fact]
    public async Task APatchedMaterialNamesItsLinkedSampleAndDependent()
    {
        LinkedLevel linked = await LinkAsync(_rooms.Hub, _rooms.Hall, Level, ("hub", 1, 0, 1));
        (int X, int Y, int Z) at = Position(BspStructView.As<DCubemapSample>(linked.Bsp[BspLump.Cubemaps])[0]);
        (int X, int Y, int Z) local = Position(BspStructView.As<DCubemapSample>(_rooms.Hub.Bsp[BspLump.Cubemaps])[0]);
        string suffix = $"_{at.X}_{at.Y}_{at.Z}";
        string roomSuffix = $"_{local.X}_{local.Y}_{local.Z}";

        Dictionary<string, byte[]> files = await FilesAsync(linked.Bsp);
        Dictionary<string, byte[]> room = await FilesAsync(_rooms.Hub.Bsp);
        string text = Encoding.Latin1.GetString(files[$"materials/maps/{Level}/{Shiny}{suffix}.vmt"]);
        string roomText = Encoding.Latin1.GetString(room[$"materials/maps/hub/{Shiny}{roomSuffix}.vmt"]);
        Assert.Contains($"\"$envmap\"\t\t\"maps/{Level}/c{at.X}_{at.Y}_{at.Z}\"", text, StringComparison.Ordinal);
        Assert.Contains($"\"$bottommaterial\"\t\t\"maps/{Level}/{ShinyBottom}{suffix}\"", text, StringComparison.Ordinal);
        Assert.Equal(
            roomText.Replace($"maps/hub/c{local.X}_{local.Y}_{local.Z}", $"maps/{Level}/c{at.X}_{at.Y}_{at.Z}", StringComparison.Ordinal)
                .Replace($"maps/hub/{ShinyBottom}{roomSuffix}", $"maps/{Level}/{ShinyBottom}{suffix}", StringComparison.Ordinal),
            text);
        Assert.True(files.ContainsKey($"materials/maps/{Level}/{ShinyBottom}{suffix}.vmt"));
        Assert.Equal(room[$"materials/maps/hub/c{local.X}_{local.Y}_{local.Z}.hdr.vtf"], files[$"materials/maps/{Level}/c{at.X}_{at.Y}_{at.Z}.hdr.vtf"]);
    }

    // ---- nothing else moves ------------------------------------------------------------

    /// <summary>
    /// Cubemaps cost no entity (15.6) and move nothing but their own: the
    /// linked level of the hub keeps the entity lump and budget of the same
    /// level whose hub has no sample, and a level of rooms without samples
    /// writes no cubemap lump, as the link always wrote it.
    /// </summary>
    [Fact]
    public async Task CubemapsAddNoEntityAndALevelWithoutThemWritesNoLump()
    {
        LinkedLevel linked = await LinkAsync(_rooms.Hub, _rooms.Hall, Level, ("hub", 0, 0, 0), ("hall", 1, 0, 0));
        LinkedLevel bare = await LinkAsync(_rooms.Hall, _rooms.Hall, Level, ("hall", 0, 0, 0), ("hall", 1, 0, 0));
        Assert.True(linked.Bsp[BspLump.Entities].Data.Span.SequenceEqual(bare.Bsp[BspLump.Entities].Data.Span));
        Assert.Equal(bare.EntityBudget!.Edicts, linked.EntityBudget!.Edicts);
        Assert.Equal(bare.EntityBudget.Listed, linked.EntityBudget.Listed);

        Assert.Equal(0, bare.Bsp[BspLump.Cubemaps].Length);
        Assert.Equal(0, bare.CubemapSamples);
    }

    /// <summary>
    /// The same bytes at one thread and at four, twice each (15.5): the
    /// renames are made sequentially per placement and the pak is written in
    /// name order.
    /// </summary>
    [Fact]
    public async Task TheLinkIsTheSameBytesAtAnyDegree()
    {
        byte[] first = await LinkBytesAsync(1);
        Assert.Equal(first, await LinkBytesAsync(1));
        Assert.Equal(first, await LinkBytesAsync(4));
        Assert.Equal(first, await LinkBytesAsync(4));
    }

    /// <summary>
    /// A room with samples round-trips through a pack: its <c>CUBE</c>
    /// section reads back bound to the loaded compile, it gets link data at
    /// pack time (a room with samples used to get none, being one the link
    /// refused), and a level linked from the loaded rooms is the same bytes
    /// as one linked from the rooms in memory.
    /// </summary>
    [Fact]
    public async Task ARoomWithCubemapsRoundTripsThroughAPack()
    {
        RoomObject hub = _rooms.Hub with { Link = await LevelLinker.TryPrecomputeAsync(_rooms.Hub, CancellationToken.None) };
        Assert.NotNull(hub.Link);

        using MemoryStream pack = new();
        await RoomPack.SaveAsync([await RoomPackItem.CreateAsync(hub), await RoomPackItem.CreateAsync(_rooms.Hall)], pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        Assert.NotNull(index.Entries.Single(e => e.Name == "hub").Find(RoomCubemaps.SectionTag));
        Assert.Null(index.Entries.Single(e => e.Name == "hall").Find(RoomCubemaps.SectionTag));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(pack, index, ["hub", "hall"]);

        Assert.NotNull(loaded[0].CubemapsOfCompile);
        Assert.Null(loaded[1].Cubemaps);
        Assert.Equal(await BytesAsync(await LinkAsync(hub, _rooms.Hall, Level)), await BytesAsync(await LinkAsync(loaded[0], loaded[1], Level)));
    }

    // ---- refusals -------------------------------------------------------------------------

    /// <summary>
    /// A room whose compile has samples but carries no cubemap data from it
    /// (a pack written before cubemaps were carried) is refused by name,
    /// before anything is counted; its lump is no longer refused as one the
    /// relocation does not understand.
    /// </summary>
    [Fact]
    public async Task ARoomWithSamplesAndNoCubemapDataIsRefused()
    {
        RoomObject stale = _rooms.Hub with { Cubemaps = null };
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(stale, _rooms.Hall, Level));
        Assert.Equal(
            "room hub has 2 cubemap samples but no cubemap data from its compile (a pack written before the link carried cubemaps,"
            + " or a room built without ssmap room); recompile the library with ssmap room.",
            refused.Message);

        // Bound to another compile is the same as none.
        RoomObject copied = RoomHarness.WithLumps(_rooms.Hub, _ => { });
        Assert.Null(copied.CubemapsOfCompile);
        await Assert.ThrowsAsync<LinkException>(() => LinkAsync(copied, _rooms.Hall, Level));
    }

    /// <summary>
    /// A link given no map name cannot rename a sample's files, and says so,
    /// rather than carrying them where nothing reads them.
    /// </summary>
    [Fact]
    public async Task WithoutAMapNameTheCubemapFilesAreRefused()
    {
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(_rooms.Hub, _rooms.Hall, string.Empty));
        Assert.Matches(
            "^room hub packs materials/maps/hub/[^ ]+, which is named after its map; the link renames it to the level's map name,"
            + " and was given none \\(the map name of the link's compile context\\)\\.$",
            refused.Message);
        Assert.DoesNotContain("cubemapdefault", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A level name long enough to push a renamed patch past what vbsp
    /// names is refused naming the room, the cell and the name.
    /// </summary>
    [Fact]
    public async Task APatchNameTooLongForVbspIsRefused()
    {
        string level = new('l', 110);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(_rooms.Hub, _rooms.Hall, level));
        Assert.Matches(
            $"^room hub at cell \\(0, 0\\): the cubemap patch maps/{level}/unit/shiny_-?\\d+_-?\\d+_-?\\d+ is \\d+ characters long;"
            + " vbsp names a patch in fewer than 127\\.$",
            refused.Message);

        // The longest patch (the dependent's, for the second sample) at 126
        // characters links; at 127 it is refused.
        string longest = new('l', 126 - $"maps//{ShinyBottom}_170_165_100".Length);
        Assert.NotNull(await LinkAsync(_rooms.Hub, _rooms.Hall, longest, ("hub", 0, 0, 0)));
        LinkException edge = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(_rooms.Hub, _rooms.Hall, longest + "l", ("hub", 0, 0, 0)));
        Assert.Contains($"{ShinyBottom}_170_165_100 is 127 characters long", edge.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A patched material packed compressed cannot have its text rewritten,
    /// and is refused naming the room and file.
    /// </summary>
    [Fact]
    public async Task ACompressedPatchedMaterialIsRefused()
    {
        ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(_rooms.Hub.Bsp[BspLump.PakFile].Data);
        ZipArchiveWriter writer = new() { Comment = pak.Comment };
        foreach (ZipEntry entry in pak.Entries)
        {
            bool compress = entry.Name.EndsWith(".vmt", StringComparison.Ordinal);
            writer.Add(compress
                ? new ZipEntry(entry.Name, entry.Data, ZipCompressionMethod.Lzma, entry.Crc, entry.UncompressedSize)
                : entry);
        }

        byte[] bytes = writer.ToBytes();
        RoomObject hub = Rebound(RoomHarness.WithLumps(_rooms.Hub, bsp => bsp.SetLump(BspLump.PakFile, bytes)));
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(hub, _rooms.Hall, Level));
        Assert.Matches(
            "^room hub packs materials/maps/hub/unit/shiny[^ ]*\\.vmt compressed; the link renames the cubemap names inside a patched"
            + " material and reads only stored files\\.$",
            refused.Message);
    }

    /// <summary>
    /// A level past <c>MAX_MAP_CUBEMAPSAMPLES</c> is refused naming the
    /// placement that crossed it, from the rooms' lumps alone; exactly the
    /// cap links. The rooms' samples are copies of the hub's, each room
    /// with its own data made from its copy.
    /// </summary>
    [Fact]
    public async Task ALevelPastTheSampleCapIsRefused()
    {
        RoomObject Copies(int count)
        {
            DCubemapSample one = BspStructView.As<DCubemapSample>(_rooms.Hub.Bsp[BspLump.Cubemaps])[0];
            byte[] lump = System.Runtime.InteropServices.MemoryMarshal.AsBytes(Enumerable.Repeat(one, count).ToArray().AsSpan()).ToArray();
            return RoomHarness.WithLumps(_rooms.Hall, bsp => bsp.SetLump(BspLump.Cubemaps, lump));
        }

        async Task<RoomObject> BoundAsync(int count)
        {
            RoomObject room = Copies(count);
            CubemapSample sample = new(_rooms.Hub.CubemapsOfCompile!.Turned(0)[0], 6, string.Empty);
            return room with { Cubemaps = await RoomCubemaps.BuildAsync(room.Bsp, [.. Enumerable.Repeat(sample, count)], "hall") };
        }

        RoomLibrary library = new(RoomHarness.WalkableKit, RoomHarness.Cell);
        library.Add(await BoundAsync(512));
        LevelLayout exact = Layout(library, ("hall", 0, 0, 0), ("hall", 1, 0, 0));
        LevelLinker.CheckCapacity(exact, library);

        RoomLibrary over = new(RoomHarness.WalkableKit, RoomHarness.Cell);
        over.Add(await BoundAsync(513));
        LevelLayout layout = Layout(over, ("hall", 0, 0, 0), ("hall", 1, 0, 0));
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.CheckCapacity(layout, over));
        Assert.Equal(
            "room hall at cell (1, 0) pushes the link to 1026 cubemap samples; vbsp writes at most 1024 (MAX_MAP_CUBEMAPSAMPLES).",
            refused.Message);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The library: the hub with two specular slab tops, two samples and a sky.</summary>
    private static VmfDocument LibraryVmf(bool overlay = false)
    {
        VmfDocument library = RoomHarness.LibraryVmf(HubDefinition);
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        world.AddKey("skyname", Sky);
        world.Children.Add(ShinyTop(new Vec3(60, 60, 16), new Vec3(92, 108, 40), 72000, 72001));
        world.Children.Add(ShinyTop(new Vec3(150, 140, 16), new Vec3(190, 190, 40), 72100, 72101));

        // Off the whole units, negative after a turn: truncation must be of
        // the moved float, not of the room's integer.
        library.Chunks.Add(Entity("env_cubemap", 700010, ("origin", "76.5 84.25 100.75"), ("sides", "72001"), ("cubemapsize", "6")));
        library.Chunks.Add(Entity("env_cubemap", 700011, ("origin", "170.75 165.5 100")));
        if (overlay)
        {
            // A named overlay on the second slab's specular top, which the
            // nearest sample patches: both features on one face.
            library.Chunks.Add(RoomOverlayHarness.Overlay(700012, new Vec3(170, 165, 40), "72101", ("targetname", "shine")));
        }

        return library;
    }

    /// <summary>A plain slab whose top face is specular, with its own side id.</summary>
    private static VmfChunk ShinyTop(Vec3 mins, Vec3 maxs, int id, int topId)
    {
        VmfChunk slab = RoomModel.Slab(RoomHarness.Plain, mins, maxs, id);
        VmfChunk top = slab.GetChunks("side").First();
        top.Keys.First(k => k.Name == "id").Value = topId.ToString(CultureInfo.InvariantCulture);
        top.Keys.First(k => k.Name == "material").Value = Shiny;
        return slab;
    }

    private static VmfChunk Entity(string classname, int id, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", classname);
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    /// <summary>The harness materials, the specular pair and a sky whose six faces resolve.</summary>
    private static async Task<VbspContext> ContextAsync(string mapBase, int degree = 1)
    {
        InMemoryFileSystem files = new();
        files.AddText($"materials/{RoomHarness.Plain}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        files.AddText(
            $"materials/{RoomHarness.Trigger}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        files.AddText(
            $"materials/{RoomHarness.PlayerClip}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%playerClip\" \"1\"\n}\n");
        files.AddText(
            $"materials/{Shiny}.vmt",
            $"\"LightmappedGeneric\"\n{{\n\t\"$basetexture\" \"unit/missing\"\n\t\"$envmap\" \"env_cubemap\"\n\t\"$bottommaterial\" \"{ShinyBottom}\"\n}}\n");
        files.AddText(
            $"materials/{ShinyBottom}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"$envmap\" \"env_cubemap\"\n}\n");
        foreach ((string path, byte[] bytes) in RoomOverlayHarness.Files())
        {
            files.AddFile(path, bytes);
        }

        SurfaceUnit.AddSky(files, Sky, (int)ImageFormat.Bgr888, 0x0304);
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        return new VbspContext(VbspOptions.Default, new ContentFileSystem([mount]))
        {
            MapBase = mapBase,
            Parallelism = new CompileParallelism { MaxDegree = degree },
        };
    }

    /// <summary>The library's rooms compiled and linked into the level of the given rows, and the same level flattened and compiled whole.</summary>
    private static Task<(LinkedLevel Linked, BspData Whole)> LinkAndCompileFlatAsync(params string[] rows) =>
        LinkAndCompileFlatAsync(LibraryVmf(), rows);

    /// <summary>A library's rooms compiled and linked into the level of the given rows, and the same level flattened and compiled whole.</summary>
    private static async Task<(LinkedLevel Linked, BspData Whole)> LinkAndCompileFlatAsync(VmfDocument library, params string[] rows)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        LibraryRoom room = split.Rooms.Single();
        RoomLibrary compiled = new(room.Definition.Kit, room.Definition.CellSize) { LibraryEntities = split.LibraryEntities };
        compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, await ContextAsync(room.Definition.Name)));

        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", rows), Level);
        LevelLayout layout = level.ToLayout(name => compiled.Find(name)?.Definition, compiled.CellSize, compiled.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, compiled, await ContextAsync(Level));

        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await ContextAsync(Level));
        Assert.NotNull(whole.Bsp);
        return (linked, whole.Bsp!);
    }

    /// <summary>Links the given placements of the hub and the hall (default: hub at (0,0) turned once, hall at (1,0), hub at (2,0)).</summary>
    private static async Task<LinkedLevel> LinkAsync(
        RoomObject hub, RoomObject hall, string mapBase, params (string Room, int X, int Y, int Rotation)[] cells) =>
        await LinkAsync(hub, hall, mapBase, 1, cells);

    private static async Task<LinkedLevel> LinkAsync(
        RoomObject hub, RoomObject hall, string mapBase, int degree, params (string Room, int X, int Y, int Rotation)[] cells)
    {
        RoomLibrary library = new(RoomHarness.WalkableKit, RoomHarness.Cell);
        library.Add(hub);
        if (!ReferenceEquals(hub, hall))
        {
            library.Add(hall);
        }

        cells = cells.Length > 0 ? cells : [("hub", 0, 0, 1), ("hall", 1, 0, 0), ("hub", 2, 0, 0)];
        return await LevelLinker.LinkAsync(Layout(library, cells), library, await ContextAsync(mapBase, degree));
    }

    /// <summary>A one-row level of the given cells, west to east, on the walkable kit.</summary>
    private static LevelLayout Layout(RoomLibrary library, params (string Room, int X, int Y, int Rotation)[] cells)
    {
        string row = string.Join(", ", cells.OrderBy(c => c.X).Select(c => $"{c.Room}@{c.Rotation * 90}"));
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", row), Level);
        return level.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
    }

    private async Task<byte[]> LinkBytesAsync(int degree) =>
        await BytesAsync(await LinkAsync(_rooms.Hub, _rooms.Hall, Level, degree));

    private static async Task<byte[]> BytesAsync(LinkedLevel linked)
    {
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }

    /// <summary>A room whose cubemap data is made again for its (edited) compile.</summary>
    private RoomObject Rebound(RoomObject room) => room with { Cubemaps = _rooms.Hub.Cubemaps!.For(room.Bsp) };

    /// <summary>A map's packed files by name.</summary>
    private static async Task<Dictionary<string, byte[]>> FilesAsync(BspData bsp)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (ZipEntry entry in (await ZipArchiveReader.ParseAsync(bsp[BspLump.PakFile].Data)).Entries)
        {
            files.Add(entry.Name, entry.Data);
        }

        return files;
    }

    /// <summary>The map's texdata names that are cubemap patches of the level's, in order.</summary>
    private static List<string> Patches(BspData bsp) =>
        [.. TexDataNames(bsp).Where(n => n.StartsWith($"maps/{Level}/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private static IEnumerable<string> TexDataNames(BspData bsp)
    {
        ReadOnlySpan<int> table = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]);
        byte[] data = bsp[BspLump.TexDataStringData].Data.ToArray();
        List<string> names = [];
        foreach (DTexData texData in BspStructView.As<DTexData>(bsp[BspLump.TexData]))
        {
            int start = table[texData.NameStringTableId];
            int end = Array.IndexOf(data, (byte)0, start);
            names.Add(Encoding.Latin1.GetString(data, start, end - start));
        }

        return names;
    }

    /// <summary>Every face whose material is a patch names a sample standing in the face's own cell.</summary>
    private static void AssertInRoomAssignment(BspData bsp)
    {
        List<string> names = [.. TexDataNames(bsp)];
        ReadOnlySpan<TexInfo> infos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        int patched = 0;
        foreach (DFace face in BspStructView.As<DFace>(bsp[BspLump.Faces]))
        {
            string name = names[infos[face.TexInfo].TexData];
            if (!name.StartsWith($"maps/{Level}/", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = name.Split('_');
            (float x, float y) = (float.Parse(parts[^3], CultureInfo.InvariantCulture), float.Parse(parts[^2], CultureInfo.InvariantCulture));
            List<Vec3> points = RoomHarness.FaceVertices(bsp, face);
            Vec3 centre = points.Aggregate(Vec3.Zero, (a, b) => a + b) * (1f / points.Count);
            Assert.Equal(MathF.Floor(centre.X / RoomHarness.Cell), MathF.Floor(x / RoomHarness.Cell));
            Assert.Equal(MathF.Floor(centre.Y / RoomHarness.Cell), MathF.Floor(y / RoomHarness.Cell));
            patched++;
        }

        Assert.True(patched >= 2, "no patched face");
    }

    private static (int X, int Y, int Z) Position(DCubemapSample sample) => (sample.Origin[0], sample.Origin[1], sample.Origin[2]);
}
