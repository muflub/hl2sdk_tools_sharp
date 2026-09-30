//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.MultiLibraryHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A level that takes its rooms from two libraries, linked and flattened
/// (the rooms design, PR 17): two rooms of one name kept apart, link equal
/// to flatten at every turn, the same map as one library holding both room
/// sets, the same map at any thread count, the first library's singletons,
/// worldspawn and skybox, and the lighting rule across libraries.
/// </summary>
public sealed class LevelLinkerMultiLibraryTests
{
    public static TheoryData<int> Rotations => [0, 1, 2, 3];

    /// <summary>
    /// Both hubs and base's other, each at the same turn: the link and the
    /// flattened compile hold the same open space, the same markers (each
    /// hub its own library's) and the same props, at every turn; neither
    /// warns, since the two libraries agree.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ATwoLibraryLevelLinksAsItFlattensAtEveryTurn(int rotation)
    {
        int degrees = rotation * 90;
        LevelGrid level = Level($"base.hub@{degrees}, caves.hub@{degrees}, other@{degrees}");
        VmfDocument baseVmf = Base(), cavesVmf = Caves();
        (LinkedLevel linked, IReadOnlyList<string> warnings) = await LinkAsync(
            level, 1, await RoomPropHarness.CompileAsync(baseVmf), await RoomPropHarness.CompileAsync(cavesVmf));
        (BspData flat, IReadOnlyList<string> flatWarnings) = await CompileFlatAsync(level, baseVmf, cavesVmf);

        Assert.Empty(warnings);
        Assert.Empty(flatWarnings);
        RoomAreaPortalHarness.SamePartition(linked.Bsp, flat, level);
        Assert.Equal(Points(flat, "info_target"), Points(linked.Bsp, "info_target"));
        Assert.Equal(2, Points(linked.Bsp, "info_target").Count);
        Assert.Equal(RoomPropHarness.Observed(flat), RoomPropHarness.Observed(linked.Bsp));
        Assert.Equal(2, RoomPropHarness.Props(linked.Bsp).Props.Count);
        Assert.Equal(["base.hub", "caves.hub", "base.other"], linked.Plan.Layout.Rooms.Select(r => r.Placement.Room));
    }

    /// <summary>
    /// The level linked from two libraries is the level linked from one
    /// library holding both room sets under other names, byte for byte:
    /// nothing the link writes depends on which library a room came from,
    /// and nothing it keeps per room confuses two rooms of one name.
    /// </summary>
    [Fact]
    public async Task TwoLibrariesLinkAsOneLibraryHoldingBoth()
    {
        RoomLibrary baseRooms = await RoomPropHarness.CompileAsync(Base());
        RoomLibrary caves = await RoomPropHarness.CompileAsync(Caves());
        (LinkedLevel multi, _) = await LinkAsync(Level("base.hub, caves.hub, hall"), 1, baseRooms, caves);

        RoomLibrary one = new(baseRooms.Kit, baseRooms.CellSize) { LibraryEntities = baseRooms.LibraryEntities, Options = baseRooms.Options };
        one.Add(baseRooms.Get("hub"));
        one.Add("hub2", caves.Get("hub"), 0);
        one.Add("hall2", caves.Get("hall"), 0);
        LevelGrid single = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub, hub2, hall2"), "multi");
        LinkedLevel alone = await MultiLibraryHarness.LinkAsync(single, one);

        Assert.Equal(await BytesAsync(alone.Bsp), await BytesAsync(multi.Bsp));
    }

    /// <summary>A level of two libraries links to the same bytes at one thread and at four.</summary>
    [Fact]
    public async Task ATwoLibraryLevelIsTheSameAtAnyThreadCount()
    {
        RoomLibrary baseRooms = await RoomPropHarness.CompileAsync(Base());
        RoomLibrary caves = await RoomPropHarness.CompileAsync(Caves());
        LevelGrid level = Level("caves.hub@90, base.hub@180, hall", "base.other, caves.hub@270, ~");
        byte[] one = await BytesAsync((await LinkAsync(level, 1, baseRooms, caves)).Linked.Bsp);
        byte[] four = await BytesAsync((await LinkAsync(level, 4, baseRooms, caves)).Linked.Bsp);
        Assert.Equal(one, four);
    }

    /// <summary>
    /// The singleton rule at link and flatten: the level writes the first
    /// library's sun and fog, once; the second library's are dropped with
    /// the same warnings from both.
    /// </summary>
    [Fact]
    public async Task TheFirstLibrarysSingletonsAreTheLevels()
    {
        VmfChunk sun = RoomLightHarness.Sun();
        VmfChunk otherSun = RoomLightHarness.Sun(angles: "0 90 0");
        VmfChunk fog = RoomPropHarness.Entity("env_fog_controller", 960, new(-64, 0, 0), ("fogcolor", "1 2 3"));
        VmfDocument baseVmf = Base(sun), cavesVmf = Caves(otherSun, fog);
        LevelGrid level = Level("base.hub, caves.hub");
        (LinkedLevel linked, IReadOnlyList<string> warnings) = await LinkAsync(
            level, 1, await RoomPropHarness.CompileAsync(baseVmf), await RoomPropHarness.CompileAsync(cavesVmf));
        (BspData flat, IReadOnlyList<string> flatWarnings) = await CompileFlatAsync(level, baseVmf, cavesVmf);

        string[] expected =
        [
            "library caves: its light_environment differs from library base's (angles: \"0 90 0\" against \"0 30 0\"); the level takes library base's, the first listed, and drops it.",
            "library caves: its env_fog_controller is dropped; the level's singletons come from library base, which has none.",
        ];
        Assert.Equal(expected, warnings);
        Assert.Equal(expected, flatWarnings);
        List<string> suns = RoomSkyboxHarness.OfClass(linked.Bsp, "light_environment");
        Assert.Single(suns);
        Assert.Contains("angles=0 30 0", suns[0], StringComparison.Ordinal);
        Assert.Empty(RoomSkyboxHarness.OfClass(linked.Bsp, "env_fog_controller"));
        Assert.Single(RoomSkyboxHarness.OfClass(flat, "light_environment"));
        Assert.Empty(RoomSkyboxHarness.OfClass(flat, "env_fog_controller"));
    }

    /// <summary>
    /// The worldspawn is the first placed room's of the earliest listed
    /// library the level places, even when another library's room is placed
    /// first; the other library's differing key warns; a level of the
    /// second library alone takes its worldspawn.
    /// </summary>
    [Fact]
    public async Task TheWorldspawnIsTheEarliestPlacedLibrarys()
    {
        VmfDocument baseVmf = Base(), cavesVmf = Caves();
        baseVmf.GetChunk(MapFileLoader.WorldChunk)!.AddKey("skyname", "sky_day");
        cavesVmf.GetChunk(MapFileLoader.WorldChunk)!.AddKey("skyname", "sky_night");
        RoomLibrary baseRooms = await RoomPropHarness.CompileAsync(baseVmf), caves = await RoomPropHarness.CompileAsync(cavesVmf);

        LevelGrid level = Level("caves.hub, base.hub");
        (LinkedLevel linked, IReadOnlyList<string> warnings) = await LinkAsync(level, 1, baseRooms, caves);
        string line = "library caves: its rooms were compiled with worldspawn skyname \"sky_night\"; the level's is \"sky_day\" (library base)."
            + " They link as compiled; build the libraries into one pack with ssmap roompack to compile them with the level's.";
        Assert.Equal([line], warnings);
        Assert.Equal("sky_day", World(linked.Bsp, "skyname"));
        (BspData flat, IReadOnlyList<string> flatWarnings) = await CompileFlatAsync(level, baseVmf, cavesVmf);
        Assert.Equal([line], flatWarnings);
        Assert.Equal("sky_day", World(flat, "skyname"));

        (LinkedLevel cavesOnly, IReadOnlyList<string> none) = await LinkAsync(Level("caves.hub, hall"), 1, baseRooms, caves);
        Assert.Empty(none);
        Assert.Equal("sky_night", World(cavesOnly.Bsp, "skyname"));
        Assert.Equal("sky_night", World((await CompileFlatAsync(Level("caves.hub, hall"), baseVmf, cavesVmf)).Bsp, "skyname"));
    }

    /// <summary>
    /// Combining puts every room under its qualified name with its library's
    /// index and name keys, and carries the first library's options,
    /// entities and skybox; a library the level places no room of still
    /// speaks (its option line), and adds no room.
    /// </summary>
    [Fact]
    public async Task CombiningKeepsEachRoomsLibrary()
    {
        VmfDocument baseVmf = Base(), cavesVmf = Caves();
        baseVmf.GetChunk(MapFileLoader.WorldChunk)!.AddKey(RoomLibraryOptions.NameKeysKey, "base_key");
        cavesVmf.GetChunk(MapFileLoader.WorldChunk)!.AddKey(RoomLibraryOptions.NameKeysKey, "caves_key");
        cavesVmf.GetChunk(MapFileLoader.WorldChunk)!.AddKey(RoomLibraryOptions.EntityReserveKey, "100");
        RoomLibrary baseRooms = await RoomPropHarness.CompileAsync(baseVmf), caves = await RoomPropHarness.CompileAsync(cavesVmf);
        (_, LevelLibrarySet set) = Combine(Level("base.hub, caves.hub"), baseRooms, caves);

        RoomLibrary rooms = set.Rooms;
        Assert.Same(baseRooms.Get("hub"), rooms.Get("base.hub"));
        Assert.Same(caves.Get("hub"), rooms.Get("caves.hub"));
        Assert.Null(rooms.Find("hub"));
        Assert.Equal((0, 1), (rooms.SourceOf("base.other"), rooms.SourceOf("caves.hall")));
        Assert.Equal(["base_key"], rooms.NameKeysOf("base.hub")!);
        Assert.Equal(["caves_key"], rooms.NameKeysOf("caves.hall")!);
        Assert.Same(baseRooms.Options, rooms.Options);
        Assert.Same(baseRooms.LibraryEntities, rooms.LibraryEntities);
        Assert.Equal(["library caves: rooms_entity_reserve 100 is ignored; the level takes library base's, 512."], set.Warnings);

        // A library the level only names: its lines, no compatibility check.
        (_, LevelLibrarySet named) = Combine(Level("base.hub, base.other"), baseRooms, caves);
        Assert.Equal(set.Warnings, named.Warnings);

        // A library outside a combination answers for itself.
        Assert.Equal(0, baseRooms.SourceOf("hub"));
        Assert.Equal(["base_key"], baseRooms.NameKeysOf("hub")!);
        Assert.Throws<ArgumentOutOfRangeException>(() => baseRooms.Add("x", caves.Get("hall"), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rooms.Add("x", caves.Get("hall"), 2));
        Assert.Throws<ArgumentException>(() => LevelLibraries.Combine(RoomPropHarness.Level("hub"), [baseRooms]));
        Assert.Throws<ArgumentException>(() => LevelLibraries.Combine(Level("base.hub"), [baseRooms]));
    }

    /// <summary>
    /// The skybox is the first library's, under its qualified name; the
    /// second library's warns and is never placed; a level that places any
    /// library's skybox room is refused as the link refuses one library's.
    /// </summary>
    [Fact]
    public async Task TheSkyboxIsTheFirstLibrarys()
    {
        RoomLibrary first = await RoomSkyboxHarness.CompileAsync(RoomSkyboxHarness.Library());
        RoomLibrary second = await RoomSkyboxHarness.CompileAsync(RoomSkyboxHarness.Library());
        LevelGrid level = LevelYaml.Parse(
            "libraries:\n  base: a.vmf\n  caves: b.vmf\nrows: 1\ncolumns: 2\ngrid:\n  - [base.hub, caves.other]\n", "sky2");
        (LinkedLevel linked, IReadOnlyList<string> warnings) = await LinkAsync(level, 1, first, second);

        Assert.Equal(["library caves: its skybox room \"sky\" is dropped; the level's skybox is library base's, \"sky\"."], warnings);
        Assert.Single(RoomSkyboxHarness.OfClass(linked.Bsp, "sky_camera"));
        Assert.Equal("base.sky", linked.Plan.Rooms[^1].Instance.Placement.Room);

        foreach (string placed in new[] { "base.sky", "caves.sky" })
        {
            LevelGrid bad = LevelYaml.Parse(
                $"libraries:\n  base: a.vmf\n  caves: b.vmf\nrows: 1\ncolumns: 2\ngrid:\n  - [base.hub, {placed}]\n", "sky2");
            LinkException refused = Assert.Throws<LinkException>(() => Combine(bad, first, second));
            Assert.Equal($"level sky2 places the skybox room {placed} at cell (1, 0); the link places the skybox below the grid itself.", refused.Message);
        }
    }

    /// <summary>
    /// Two lit libraries (PR 9) link as one lit library holding both room
    /// sets, byte for byte, the lightmaps of the two hubs kept apart; with
    /// one sun they do not warn.
    /// </summary>
    [Fact]
    public async Task TwoLitLibrariesLinkAsOneLitLibraryHoldingBoth()
    {
        RoomLibrary baseRooms = await RoomLightHarness.CompileAsync(RoomLightHarness.Library(true, [0]));
        VmfDocument cavesVmf = RoomLightHarness.Library(true, [0], (0, RoomLightHarness.Light(961, new(128, 64, 160))));
        RoomLibrary caves = await RoomLightHarness.CompileAsync(cavesVmf);
        (LinkedLevel multi, IReadOnlyList<string> warnings) = await LinkAsync(Level("base.hub, caves.hub, caves.other"), 1, baseRooms, caves);
        Assert.Equal(["library caves: 1 singleton(s) equal to library base's dropped (light_environment)."], warnings);
        Assert.NotEmpty(multi.Bsp[BspLump.Lighting].Data.ToArray());

        RoomLibrary one = new(baseRooms.Kit, baseRooms.CellSize) { LibraryEntities = baseRooms.LibraryEntities, Options = baseRooms.Options };
        one.Add(baseRooms.Get("hub"));
        one.Add("hub2", caves.Get("hub"), 0);
        one.Add("other2", caves.Get("other"), 0);
        LinkedLevel alone = await MultiLibraryHarness.LinkAsync(LevelYaml.Parse(RoomHarness.LevelText("r.vmf", "hub, hub2, other2"), "multi"), one);
        Assert.Equal(await BytesAsync(alone.Bsp), await BytesAsync(multi.Bsp));
    }

    /// <summary>
    /// D26 until the door light: a sunlit room of a library whose sun the
    /// level drops links as baked, with one line naming the library's first
    /// such room; a room of it no sun reaches is not named.
    /// </summary>
    [Fact]
    public async Task ASunlitRoomUnderADroppedSunWarns()
    {
        RoomLibrary baseRooms = await RoomLightHarness.CompileAsync(RoomLightHarness.Library(true, [0]));
        VmfDocument cavesVmf = RoomPropHarness.Library();
        cavesVmf.Chunks.Add(RoomLightHarness.Sun(angles: "0 120 0"));
        RoomLightHarness.SkyCeiling(cavesVmf, 1);
        RoomLightHarness.WorldAlign(cavesVmf);
        RoomLibrary caves = await RoomLightHarness.CompileAsync(cavesVmf);

        (LinkedLevel linked, IReadOnlyList<string> warnings) = await LinkAsync(Level("base.hub, caves.hub, caves.other, caves.other"), 1, baseRooms, caves);
        Assert.Equal(
            [
                "library caves: its light_environment differs from library base's (angles: \"0 120 0\" against \"0 30 0\"); the level takes library base's, the first listed, and drops it.",
                "library caves: room caves.other was baked under library caves's sun, and the level takes library base's; it links as baked. Build the libraries with one sun.",
            ],
            warnings);
        Assert.NotEmpty(linked.Bsp[BspLump.Lighting].Data.ToArray());
    }

    /// <summary>PR 9's rule across libraries: a lit library and an unlit one never share a level.</summary>
    [Fact]
    public async Task ALitAndAnUnlitLibraryAreRefused()
    {
        RoomLibrary lit = await RoomLightHarness.CompileAsync(RoomLightHarness.Library(false, []));
        RoomLibrary unlit = await RoomLightHarness.CompileAsync(RoomLightHarness.Library(false, []), light: false);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(Level("base.hub, caves.hub"), 1, lit, unlit));
        Assert.Equal(
            "room caves.hub has no baked lighting, but room base.hub of the same level has; a level's rooms are lit alike. Recompile the library with ssmap room.",
            refused.Message);
    }

    /// <summary>
    /// The level's sun world lights are the first library's bake even when
    /// another library's room is placed first and was baked under another
    /// sun; a level of one library keeps taking its first lit placement's.
    /// </summary>
    [Fact]
    public async Task TheSunsWorldLightsAreTheFirstLibrarys()
    {
        RoomLibrary baseRooms = await RoomLightHarness.CompileAsync(RoomLightHarness.Library(true, [0]));
        VmfDocument cavesVmf = RoomPropHarness.Library();
        cavesVmf.Chunks.Add(RoomLightHarness.Sun(angles: "0 120 0"));
        RoomLightHarness.SkyCeiling(cavesVmf, 0);
        RoomLightHarness.WorldAlign(cavesVmf);
        RoomLibrary caves = await RoomLightHarness.CompileAsync(cavesVmf);
        (LevelGrid resolved, LevelLibrarySet set) = Combine(Level("caves.hub, base.hub"), baseRooms, caves);
        LinkedLevel linked = await MultiLibraryHarness.LinkAsync(resolved, set.Rooms);
        ResolvedPlacement[] placed = [.. linked.Plan.Rooms];

        Assert.Same(baseRooms.Get("hub").LightingOfCompile!.SkyLdr, LevelLinker.PlanLighting(placed, set.Rooms)!.SkyLdr);
        Assert.Same(caves.Get("hub").LightingOfCompile!.SkyLdr, LevelLinker.PlanLighting(placed)!.SkyLdr);
        Assert.NotEqual(
            baseRooms.Get("hub").LightingOfCompile!.SkyLdr!.Select(l => l.Normal.X),
            caves.Get("hub").LightingOfCompile!.SkyLdr!.Select(l => l.Normal.X));
    }

    /// <summary>
    /// The flatten's own checks: one VMF per library; any library's skybox
    /// is the link's to place; a library level's aliases are replaced by
    /// the rooms they name, the VMF the same as the level spelt out.
    /// </summary>
    [Fact]
    public void TheFlattenChecksItsLibraries()
    {
        VmfDocument baseVmf = Base(), cavesVmf = Caves();
        Assert.Throws<ArgumentException>(() => LevelFlattener.FlattenLevel(Level("base.hub"), [baseVmf], new LevelFlattenOptions()));
        Assert.Throws<ArgumentException>(() => LevelFlattener.FlattenLevel(RoomPropHarness.Level("hub"), [baseVmf, cavesVmf], new LevelFlattenOptions()));

        VmfDocument skyA = RoomSkyboxHarness.Library(), skyB = RoomSkyboxHarness.Library();
        LinkException refused = Assert.Throws<LinkException>(
            () => LevelFlattener.FlattenLevel(Level("base.hub, caves.sky"), [skyA, skyB], new LevelFlattenOptions()));
        Assert.Equal("level multi places the skybox room caves.sky at cell (1, 0); the link places the skybox below the grid itself.", refused.Message);

        LevelGrid aliased = LevelYaml.Parse("library: rooms.vmf\naliases:\n  H: hub\nrows: 1\ncolumns: 2\ngrid:\n  - [H, other@90]\n", "props");
        Assert.Equal(
            LevelFlattener.Flatten(RoomPropHarness.Level("hub, other@90"), baseVmf).ToBytes(),
            LevelFlattener.FlattenLevel(aliased, baseVmf, new LevelFlattenOptions()).Vmf.ToBytes());
    }

    /// <summary>
    /// A level placing only the second library's rooms takes that library's
    /// worldspawn but the first library's save counter, in the link and the
    /// flatten alike.
    /// </summary>
    [Fact]
    public async Task TheSaveCounterIsTheFirstLibrarysWhateverTheWorldspawn()
    {
        VmfDocument baseVmf = Base(), cavesVmf = Caves();
        baseVmf.GetChunk(MapFileLoader.WorldChunk)!.AddKey("mapversion", "5");
        cavesVmf.GetChunk(MapFileLoader.WorldChunk)!.AddKey("mapversion", "9");
        LevelGrid level = Level("caves.hub, hall");
        (LinkedLevel linked, _) = await LinkAsync(level, 1, await RoomPropHarness.CompileAsync(baseVmf), await RoomPropHarness.CompileAsync(cavesVmf));
        Assert.Equal("5", World(linked.Bsp, "mapversion"));
        VmfDocument flat = LevelFlattener.FlattenLevel(level, [baseVmf, cavesVmf], new LevelFlattenOptions()).Vmf;
        Assert.Equal("5", flat.GetChunk(MapFileLoader.WorldChunk)!.GetValue("mapversion"));
        VmfDocument first = LevelFlattener.FlattenLevel(Level("base.hub"), [baseVmf, cavesVmf], new LevelFlattenOptions()).Vmf;
        Assert.Equal("5", first.GetChunk(MapFileLoader.WorldChunk)!.GetValue("mapversion"));
    }
}
