//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The compatibility check and the singleton rule over a level's libraries
/// (the rooms design, 17.4 and 17.5; D20, D21, D24 to D26, D29): every
/// refusal and warning of 17.3 by its exact text, on what the link and the
/// flatten read of each library.
/// </summary>
public sealed class LevelLibrariesCheckTests
{
    private static RoomDefinition Grid(float cell = 256, SocketKit? kit = null) =>
        new("room", cell, kit ?? new SocketKit(96, 224, 16), []);

    private static LevelLibraries.LibraryFacts Facts(
        string key,
        RoomDefinition? placed = null,
        VmfChunk[]? entities = null,
        RoomLibraryOptions? options = null,
        string? skybox = null,
        (string Key, string Value)[]? world = null) =>
        new(
            key,
            $"../{key}.vmf",
            placed,
            entities ?? [],
            options ?? RoomLibraryOptions.None,
            skybox,
            placed is null ? null : [new("classname", "worldspawn"), .. (world ?? []).Select(k => new KeyValuePair<string, string>(k.Key, k.Value))]);

    private static VmfChunk Sun(string angles = "0 30 0", int id = 10) =>
        RoomPropHarness.Entity("light_environment", id, new Vec3(-64, 0, 0), ("angles", angles), ("_light", "255 255 255 200"));

    private static VmfChunk Fog(string? name = null, string colour = "1 2 3", int id = 11) =>
        name is null
            ? RoomPropHarness.Entity("env_fog_controller", id, new Vec3(-64, 64, 0), ("fogcolor", colour))
            : RoomPropHarness.Entity("env_fog_controller", id, new Vec3(-64, 64, 0), ("targetname", name), ("fogcolor", colour));

    private static List<string> Check(params LevelLibraries.LibraryFacts[] facts) => LevelLibraries.Check(facts);

    private static string Refused(params LevelLibraries.LibraryFacts[] facts) => Assert.Throws<LinkException>(() => Check(facts)).Message;

    /// <summary>Libraries that agree on everything check clean.</summary>
    [Fact]
    public void AgreeingLibrariesCheckClean()
    {
        Assert.Empty(Check(Facts("base", Grid(), [Sun()]), Facts("caves", Grid())));
    }

    /// <summary>17.3, cell: two placed libraries on different grids.</summary>
    [Fact]
    public void DifferentCellSizesAreRefused()
    {
        Assert.Equal(
            "libraries base (../base.vmf) and caves (../caves.vmf) are built for different grids: cell_size 256 against 512;"
            + " the rooms of a level share one cell size.",
            Refused(Facts("base", Grid()), Facts("caves", Grid(512))));
    }

    /// <summary>17.3, kit: the first key of the kit that differs, in the library's order.</summary>
    [Theory]
    [InlineData(64f, 224f, 16f, "door_width 96 against 64")]
    [InlineData(96f, 200f, 16f, "door_height 224 against 200")]
    [InlineData(96f, 224f, 8f, "wall_depth 16 against 8")]
    [InlineData(64f, 200f, 8f, "door_width 96 against 64")]
    public void DifferentKitsAreRefused(float width, float height, float depth, string difference)
    {
        Assert.Equal(
            $"libraries base (../base.vmf) and caves (../caves.vmf) have different door kits: {difference}; the rooms of a level join through one kit.",
            Refused(Facts("base", Grid()), Facts("caves", Grid(kit: new SocketKit(width, height, depth)))));
    }

    /// <summary>
    /// The check runs over the libraries the level places: one it only names
    /// joins nothing, and the placed ones are held to the earliest placed,
    /// which is then named as the level's.
    /// </summary>
    [Fact]
    public void OnlyPlacedLibrariesAreCompared()
    {
        Assert.Empty(Check(Facts("base", Grid()), Facts("caves"), Facts("halls", Grid())));
        Assert.Empty(Check(Facts("base"), Facts("caves", Grid(512))));
        Assert.StartsWith(
            "libraries caves (../caves.vmf) and halls (../halls.vmf) are built for different grids",
            Refused(Facts("base"), Facts("caves", Grid()), Facts("halls", Grid(512))),
            StringComparison.Ordinal);
    }

    /// <summary>17.3, navigation grid (D25): two libraries building navigation on different voxels per cell.</summary>
    [Fact]
    public void DifferentNavigationGridsAreRefused()
    {
        Assert.Equal(
            "libraries base and caves build navigation on different grids: 16 against 8 voxels per cell; a level's navigation is one grid.",
            Refused(Facts("base", Grid()), Facts("caves", Grid(), world: [(NavSettings.VoxelKey, "32")])));
    }

    /// <summary>A library that builds no navigation is not compared on it, and gets no navigation line.</summary>
    [Fact]
    public void ALibraryWithoutNavigationIsNotCompared()
    {
        Assert.Empty(Check(Facts("base", Grid()), Facts("caves", Grid(), world: [(NavSettings.EnabledKey, "0"), (NavSettings.VoxelKey, "32")])));
        Assert.Empty(Check(Facts("base", Grid(), world: [(NavSettings.EnabledKey, "0")]), Facts("caves", Grid(), world: [(NavSettings.StepKey, "24")])));
    }

    /// <summary>
    /// 17.3, navigation (D25): the first other setting that differs warns,
    /// with the value as written or the default, and the level's.
    /// </summary>
    [Theory]
    [InlineData("nav_step_height", "24", "library caves: its rooms' navigation was built with nav_step_height 24; the level's is 18 (library base).")]
    [InlineData("nav_max_slope", "30", "library caves: its rooms' navigation was built with nav_max_slope 30; the level's is 45.57 (library base).")]
    [InlineData("nav_jump_height", "40", "library caves: its rooms' navigation was built with nav_jump_height 40; the level's is 56 (library base).")]
    [InlineData("nav_jump_distance", "80", "library caves: its rooms' navigation was built with nav_jump_distance 80; the level's is 100 (library base).")]
    [InlineData("nav_cost_water", "3", "library caves: its rooms' navigation was built with nav_cost_water 3; the level's is 2 (library base).")]
    [InlineData("nav_cost_ladder", "2", "library caves: its rooms' navigation was built with nav_cost_ladder 2; the level's is 1.5 (library base).")]
    [InlineData("nav_agents", "standing 32 72", "library caves: its rooms' navigation was built with nav_agents standing 32 72; the level's is standing 32 72 player; flyer 32 32 npc (library base).")]
    public void OtherNavigationSettingsWarn(string key, string value, string line)
    {
        Assert.Equal([line], Check(Facts("base", Grid()), Facts("caves", Grid(), world: [(key, value)])));
    }

    /// <summary>A setting spelt differently but equal (the default written out) is no difference.</summary>
    [Fact]
    public void AnEqualNavigationSettingDoesNotWarn()
    {
        Assert.Empty(Check(Facts("base", Grid()), Facts("caves", Grid(), world: [(NavSettings.StepKey, "18.0")])));
    }

    /// <summary>17.3, differs: a later library's singleton unlike the first's, the class and the name, and the first differing key.</summary>
    [Fact]
    public void ADifferentSingletonWarns()
    {
        Assert.Equal(
            [
                "library caves: its light_environment differs from library base's (angles: \"0 90 0\" against \"0 30 0\"); the level takes library base's, the first listed, and drops it.",
                "library caves: its env_fog_controller \"mist\" differs from library base's (fogcolor: \"9 9 9\" against \"1 2 3\"); the level takes library base's, the first listed, and drops it.",
            ],
            Check(Facts("base", Grid(), [Sun(), Fog("mist")]), Facts("caves", Grid(), [Sun("0 90 0"), Fog("mist", "9 9 9")])));
    }

    /// <summary>17.3, equal (D24): equal copies summed into one line per library, listed in its order; the id and position do not count.</summary>
    [Fact]
    public void EqualSingletonsWarnOnceALibrary()
    {
        VmfChunk otherSun = RoomPropHarness.Entity("light_environment", 90, new Vec3(512, 0, 0), ("angles", "0 30 0"), ("_light", "255 255 255 200"));
        Assert.Equal(
            ["library caves: 2 singleton(s) equal to library base's dropped (light_environment, env_fog_controller \"mist\")."],
            Check(Facts("base", Grid(), [Sun(), Fog("mist")]), Facts("caves", Grid(), [otherSun, Fog("mist", id: 91)])));
    }

    /// <summary>
    /// D29 (was O25, whose facts said "dropped"): a singleton only a later
    /// library has is the level's, without a line; an unnamed and a named
    /// copy are different entities, so the named fog fills a gap beside the
    /// first library's unnamed one. The level's list is the first library's,
    /// then the fillers in library order; the sun's source is the library
    /// that supplied it.
    /// </summary>
    [Fact]
    public void ASingletonOnlyALaterLibraryHasIsTheLevels()
    {
        VmfChunk fog = Fog(), sun = Sun(), mist = Fog("mist");
        LevelLibraries.LibraryFacts[] facts = [Facts("base", Grid(), [fog]), Facts("caves", Grid(), [sun, mist])];
        Assert.Empty(Check(facts));
        LevelLibraries.LevelSingletonChoice level = LevelLibraries.Singletons(facts);
        Assert.Equal([fog, sun, mist], level.Entities);
        Assert.Equal(1, level.SunSource);
    }

    /// <summary>
    /// The first library still wins where it has a singleton (D20): its copy
    /// is the level's, the later one warns, and the level's list is the
    /// first library's own, so a level whose first library has every
    /// singleton writes what it wrote before D29.
    /// </summary>
    [Fact]
    public void TheFirstLibraryStillWinsWhereItHasOne()
    {
        LevelLibraries.LibraryFacts first = Facts("base", Grid(), [Sun(), Fog("mist")]);
        LevelLibraries.LibraryFacts[] facts = [first, Facts("caves", Grid(), [Sun("0 90 0"), Fog("mist", "9 9 9")])];
        LevelLibraries.LevelSingletonChoice level = LevelLibraries.Singletons(facts);
        Assert.Same(first.Entities, level.Entities);
        Assert.Same(first.Options, level.Options);
        Assert.Equal((null, 0), (level.SkyboxSource, level.SunSource));
        Assert.Equal(2, Check(facts).Count);

        // No library with a sun: the source reads as the first library.
        Assert.Equal(0, LevelLibraries.Singletons([Facts("base"), Facts("caves", entities: [Fog()])]).SunSource);
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelLibraries.Singletons([]));
    }

    /// <summary>
    /// Every listed library speaks and fills, placed or not: an unplaced
    /// library's fog is the level's, and a third library's copy is held to
    /// it, differing (named as the earliest listed with one) or equal.
    /// </summary>
    [Fact]
    public void AGapIsFilledFromTheEarliestLibraryThatHasIt()
    {
        Assert.Equal(
            ["library halls: its env_fog_controller differs from library caves's (fogcolor: \"9 9 9\" against \"1 2 3\"); the level takes library caves's, the earliest listed with one, and drops it."],
            Check(Facts("base"), Facts("caves", entities: [Fog()]), Facts("halls", Grid(), [Fog(colour: "9 9 9")])));
        Assert.Equal(
            ["library halls: 1 singleton(s) equal to library caves's dropped (env_fog_controller)."],
            Check(Facts("base"), Facts("caves", entities: [Fog()]), Facts("halls", Grid(), [Fog(id: 40)])));
    }

    /// <summary>
    /// D24 with D29: equal copies are still one line per library; when the
    /// level's copies came from several libraries (the first's sun, a later
    /// library's fog), the line names each copy's library.
    /// </summary>
    [Fact]
    public void EqualCopiesOfSeveralSourcesWarnOnceALibrary()
    {
        Assert.Equal(
            ["library halls: 2 singleton(s) equal to the level's dropped (light_environment of library base, env_fog_controller \"mist\" of library caves)."],
            Check(Facts("base", Grid(), [Sun()]), Facts("caves", Grid(), [Fog("mist")]), Facts("halls", Grid(), [Sun(id: 30), Fog("mist", id: 31)])));
    }

    /// <summary>
    /// 17.3, option, with D29: an option the first library sets stands and a
    /// later library's other value warns; one the first does not set is
    /// taken from the earliest library that sets it, without a line, and a
    /// later library's other value warns against that library's. (Before
    /// D29 an unset option was the default and the later value warned.)
    /// </summary>
    [Fact]
    public void ADifferentOptionWarns()
    {
        RoomLibraryOptions theirs = new(300) { FoldLogic = false, DoorPortals = true, MapVersion = "7" };
        LevelLibraries.LibraryFacts[] filled = [Facts("base", Grid(), options: new RoomLibraryOptions { MapVersion = "3", NameKeys = "k" }), Facts("caves", Grid(), options: theirs)];
        Assert.Empty(Check(filled));
        Assert.Equal(new RoomLibraryOptions(300) { FoldLogic = false, DoorPortals = true, MapVersion = "3", NameKeys = "k" }, LevelLibraries.Singletons(filled).Options);

        Assert.Equal(
            [
                "library halls: rooms_entity_reserve 200 is ignored; the level takes library caves's, 300.",
                "library halls: rooms_fold_logic 1 is ignored; the level takes library caves's, 0.",
                "library halls: rooms_door_portals 0 is ignored; the level takes library caves's, 1.",
            ],
            Check([.. filled, Facts("halls", Grid(), options: new RoomLibraryOptions(200) { FoldLogic = true, DoorPortals = false })]));

        LevelLibraries.LibraryFacts first = Facts("base", Grid(), options: new RoomLibraryOptions(400) { FoldLogic = true, DoorPortals = false });
        Assert.Equal(
            [
                "library caves: rooms_entity_reserve 300 is ignored; the level takes library base's, 400.",
                "library caves: rooms_fold_logic 0 is ignored; the level takes library base's, 1.",
                "library caves: rooms_door_portals 1 is ignored; the level takes library base's, 0.",
            ],
            Check(first, Facts("caves", Grid(), options: theirs)));
        Assert.Same(first.Options, LevelLibraries.Singletons([first, Facts("caves", Grid(), options: theirs)]).Options);
        Assert.Empty(Check(Facts("base", Grid(), options: new RoomLibraryOptions(512) { FoldLogic = true }), Facts("caves", Grid(), options: new RoomLibraryOptions(512))));
    }

    /// <summary>
    /// 17.3, skybox, with D29: another library's skybox is dropped against
    /// the first's; when the first has none, the earliest library's with one
    /// is the level's (before D29 it was dropped), and a third library's
    /// warns against it.
    /// </summary>
    [Fact]
    public void AnotherLibrarysSkyboxWarns()
    {
        Assert.Equal(
            ["library caves: its skybox room \"sky2\" is dropped; the level's skybox is library base's, \"sky\"."],
            Check(Facts("base", Grid(), skybox: "sky"), Facts("caves", Grid(), skybox: "sky2")));
        LevelLibraries.LibraryFacts[] filled = [Facts("base", Grid()), Facts("caves", Grid(), skybox: "sky2")];
        Assert.Empty(Check(filled));
        Assert.Equal(1, LevelLibraries.Singletons(filled).SkyboxSource);
        Assert.Equal(
            ["library halls: its skybox room \"sky3\" is dropped; the level's skybox is library caves's, \"sky2\"."],
            Check([.. filled, Facts("halls", Grid(), skybox: "sky3")]));
    }

    /// <summary>The level's skybox library: the first with one, or none.</summary>
    [Fact]
    public void TheSkyboxSourceIsTheEarliestLibraryWithOne()
    {
        Assert.Equal(0, LevelLibraries.SkyboxSource(["a", "b"]));
        Assert.Equal(1, LevelLibraries.SkyboxSource([null, "b", "c"]));
        Assert.Null(LevelLibraries.SkyboxSource([null, null]));
        Assert.Throws<ArgumentNullException>(() => LevelLibraries.SkyboxSource(null!));
    }

    /// <summary>
    /// 17.3, worldspawn: the first key a placed library's rooms were
    /// compiled with differently, in its order; the keys that describe one
    /// room or compile, the save counter, the library keys and the
    /// navigation keys are not compared.
    /// </summary>
    [Fact]
    public void ADifferentWorldspawnWarns()
    {
        Assert.Equal(
            ["library caves: its rooms were compiled with worldspawn skyname \"sky_night\"; the level's is \"sky_day\" (library base)."
                + " They link as compiled; build the libraries into one pack with ssmap roompack to compile them with the level's."],
            Check(
                Facts("base", Grid(), world: [("skyname", "sky_day"), ("detailvbsp", "a.vbsp")]),
                Facts("caves", Grid(), world: [("skyname", "sky_night"), ("detailvbsp", "b.vbsp")])));
        Assert.Equal(
            ["library caves: its rooms were compiled with worldspawn detailvbsp \"\"; the level's is \"a.vbsp\" (library base)."
                + " They link as compiled; build the libraries into one pack with ssmap roompack to compile them with the level's."],
            Check(Facts("base", Grid(), world: [("detailvbsp", "a.vbsp")]), Facts("caves", Grid())));
        Assert.Empty(Check(
            Facts("base", Grid(), world: [("world_mins", "0 0 0"), ("hammerid", "1"), ("id", "1"), ("mapversion", "3"), ("rooms_entity_reserve", "9"), ("nav_cost_water", "2"), ("ss_level_id", "x")]),
            Facts("caves", Grid(), world: [("world_mins", "9 9 9"), ("hammerid", "2"), ("id", "2"), ("mapversion", "0"), ("ss_pack_id", "y")])));
    }

    /// <summary>
    /// Each later library's lines come in one order (singletons, options,
    /// skybox, navigation, worldspawn), library by library.
    /// </summary>
    [Fact]
    public void TheLinesComeLibraryByLibrary()
    {
        List<string> lines = Check(
            Facts("base", Grid(), [Sun()], new RoomLibraryOptions(512), "sky", [("skyname", "a")]),
            Facts("caves", Grid(), [Sun("0 90 0")], new RoomLibraryOptions(1), "sky2", [("skyname", "b"), (NavSettings.StepKey, "20")]),
            Facts("halls", Grid(), [Fog()], world: [("skyname", "c")]));
        string[] starts =
        [
            "library caves: its light_environment differs",
            "library caves: rooms_entity_reserve 1 is ignored",
            "library caves: its skybox room",
            "library caves: its rooms' navigation",
            "library caves: its rooms were compiled",
            "library halls: its rooms were compiled",
        ];
        Assert.Equal(starts.Length, lines.Count);
        for (int i = 0; i < starts.Length; i++)
        {
            Assert.StartsWith(starts[i], lines[i], StringComparison.Ordinal);
        }
    }

    /// <summary>D26: the refusal now the door light has landed (the switch on), and the warning a build without it gives.</summary>
    [Fact]
    public void ASunlitRoomUnderADroppedSunIsRefusedWithTheDoorLight()
    {
        Assert.True(LevelLibraries.RefusesDroppedSun);
        Assert.Equal(
            "library caves: room caves.hub was baked under library caves's sun, and the level takes library base's; it links as baked. Build the libraries with one sun.",
            LevelLibraries.SunLine(false, "caves", "caves.hub", "base"));
        Assert.Equal(
            "library caves: room caves.hub was baked under library caves's sun, but the level takes library base's;"
            + " a sunlit room links only under the sun it was baked with. Build the libraries with one sun.",
            Assert.Throws<LinkException>(() => LevelLibraries.SunLine(true, "caves", "caves.hub", "base")).Message);
    }

    /// <summary>
    /// The level's pack id: one pack's own id, so a level of one library
    /// keeps its ids; several packs' an id over every key and id in order;
    /// none when no pack has one.
    /// </summary>
    [Fact]
    public void TheLevelsPackIdCoversEveryPack()
    {
        Guid a = Guid.Parse("11111111-1111-8111-8111-111111111111");
        Guid b = Guid.Parse("22222222-2222-8222-8222-222222222222");
        Assert.Equal(a, RoomCompileIds.LevelPackId([("base", a)]));
        Assert.Null(RoomCompileIds.LevelPackId([("base", null)]));
        Assert.Null(RoomCompileIds.LevelPackId([("base", null), ("caves", null)]));
        Assert.Null(RoomCompileIds.LevelPackId([]));
        Guid both = RoomCompileIds.LevelPackId([("base", a), ("caves", b)])!.Value;
        Assert.Equal(both, RoomCompileIds.LevelPackId([("base", a), ("caves", b)]));
        Assert.NotEqual(both, RoomCompileIds.LevelPackId([("caves", b), ("base", a)]));
        Assert.NotEqual(both, RoomCompileIds.LevelPackId([("base", a), ("halls", b)]));
        Assert.NotEqual(both, RoomCompileIds.LevelPackId([("base", a), ("caves", null)]));
        Assert.NotEqual(a, both);
        Assert.NotEqual(b, both);
    }
}
