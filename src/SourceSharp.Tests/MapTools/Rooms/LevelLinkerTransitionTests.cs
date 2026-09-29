//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.TransitHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Transition rooms and the level spawn through the link and the flatten
/// (the rooms design, section 11 and 11.7): what each mode writes for an
/// up room left by a button and a down room left down a hallway, at every
/// quarter turn; the hallway fold and its failures; the landmarks, maps and
/// spawn points; the rooms' own player starts stripped; the link agreeing
/// with the flattened level's compile; the budget; and a level without
/// transitions linking as before.
/// </summary>
public sealed class LevelLinkerTransitionTests
{
    /// <summary>Every quarter turn in degrees, with and without <c>-mod-entities</c>.</summary>
    public static TheoryData<int, bool> TurnsAndModes
    {
        get
        {
            TheoryData<int, bool> data = [];
            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                data.Add(rotation, false);
                data.Add(rotation, true);
            }

            return data;
        }
    }

    /// <summary>Both modes.</summary>
    public static TheoryData<bool> Modes => [false, true];

    private const string Keys = "up_map: above\ndown_map: below\n";

    private static LevelGrid MiddleLevel(int rotation) =>
        Level("mid", Keys, $"up@{rotation}, plain, down@{rotation}");

    /// <summary>
    /// The stock fallback, at every turn: the up room's volume is the
    /// touch-disabled <c>trigger_changelevel</c> to the map above, which the
    /// button now fires with <c>ChangeLevel</c>; the down room's hallway
    /// <c>trigger_once</c> became the touch-enabled changelevel to the map
    /// below (its "disable touch" bit, which it held as its NPC flag,
    /// cleared, its transition output gone) and its volume is dropped with
    /// its model; each has an <c>info_landmark</c> named for the pair of
    /// levels, at the up arrival and at the hallway's centre; the spawn is
    /// the up room's arrival and spawn point, turned; and the plain room's
    /// player start is stripped.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task TheStockFallbackWritesChangelevelsLandmarksAndTheSpawn(int rotation)
    {
        RoomLibrary rooms = await CompileAsync(Library());
        LinkedLevel linked = await LinkAsync(rooms, MiddleLevel(rotation), mod: false);
        BspData bsp = linked.Bsp;

        List<BspEntity> changeLevels = OfClass(bsp, "trigger_changelevel");
        Assert.Equal(2, changeLevels.Count);
        BspEntity up = changeLevels.Single(e => e.Get("map") == "above");
        Assert.Equal(("c0r0_transition", "2", "above__mid"), (up.Get("targetname"), up.Get("spawnflags"), up.Get("landmark")));
        BspEntity down = changeLevels.Single(e => e.Get("map") == "below");
        Assert.Equal(("1", "mid__below", null), (down.Get("spawnflags"), down.Get("landmark"), down.Get("targetname")));
        Assert.DoesNotContain(down.Pairs, p => p.Key == "OnStartTouch");

        BspEntity button = Assert.Single(OfClass(bsp, "func_button"));
        Assert.Equal("c0r0_transition\u001bChangeLevel\u001b\u001b0\u001b-1", button.Get("OnPressed"));

        List<BspEntity> landmarks = OfClass(bsp, "info_landmark");
        Assert.Equal(
            [("above__mid", At(0, 0, rotation, UpArrival)), ("mid__below", At(2, 0, rotation, RoomTransit.Centre(Hallway)))],
            landmarks.Select(e => (e.Get("targetname"), e.Get("origin"))));

        List<BspEntity> starts = OfClass(bsp, "info_player_start");
        Assert.Equal(
            [(At(0, 0, rotation, UpArrival), $"0 {rotation} 0"), (At(0, 0, rotation, UpSpawn), $"0 {(270 + rotation) % 360} 0")],
            starts.Select(e => (e.Get("origin"), e.Get("angles"))));

        Assert.Empty(OfClass(bsp, RoomTransit.VolumeClass));
        Assert.Empty(OfClass(bsp, "trigger_once"));
        Assert.Equal(4, Models(bsp)); // the world, the button, the up volume, the hallway
    }

    /// <summary>
    /// With <c>-mod-entities</c>, at every turn: each volume is gone with its
    /// model and a <c>logic_level_transition</c> stands at its centre, named
    /// as the volume was, with its direction and map; the author's button
    /// and hallway still fire <c>Transition</c> at it; no changelevel,
    /// landmark or player start is written (the mod reads the arrival and
    /// spawn points from the navigation), and the rooms' starts are stripped.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task TheModEntitiesStandAtTheVolumes(int rotation)
    {
        RoomLibrary rooms = await CompileAsync(Library());
        BspData bsp = (await LinkAsync(rooms, MiddleLevel(rotation), mod: true)).Bsp;

        List<BspEntity> transitions = OfClass(bsp, LevelTransition.ClassName);
        Vec3 centre = RoomTransit.Centre(Volume);
        Assert.Equal(
            [
                ("c0r0_transition", "up", "above", At(0, 0, rotation, centre)),
                ("c2r0_transition", "down", "below", At(2, 0, rotation, centre)),
            ],
            transitions.Select(e => (e.Get("targetname"), e.Get("direction"), e.Get("map"), e.Get("origin"))));
        Assert.Equal("c0r0_transition\u001bTransition\u001b\u001b0\u001b-1", Assert.Single(OfClass(bsp, "func_button")).Get("OnPressed"));
        Assert.Equal("c2r0_transition\u001bTransition\u001b\u001b0\u001b-1", Assert.Single(OfClass(bsp, "trigger_once")).Get("OnStartTouch"));
        Assert.Empty(OfClass(bsp, "trigger_changelevel"));
        Assert.Empty(OfClass(bsp, "info_landmark"));
        Assert.Empty(OfClass(bsp, "info_player_start"));
        Assert.Empty(OfClass(bsp, RoomTransit.VolumeClass));
        Assert.Equal(3, Models(bsp)); // the world, the button, the hallway
    }

    /// <summary>
    /// The link and the flattened level's whole-map compile carry the same
    /// entities, key for key and in order, in both modes at every turn: the
    /// one resolver writes the transitions and the spawn on both paths.
    /// </summary>
    [Theory]
    [MemberData(nameof(TurnsAndModes))]
    public async Task TheLinkAndTheFlattenAgree(int rotation, bool mod)
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = Level("mid", Keys, $"up@{rotation}, plain@90", $"plain, down@{(rotation + 90) % 360}");
        LinkedLevel linked = await LinkAsync(rooms, level, mod);
        BspData whole = await CompileFlatAsync(library, level, mod);
        Assert.Equal(Comparable(whole), Comparable(linked.Bsp));
        Assert.Equal(Models(whole), Models(linked.Bsp));
    }

    /// <summary>
    /// A hallway that cannot fold (it has a filter) leaves both: the volume is
    /// the touch-disabled changelevel, the trigger fires <c>ChangeLevel</c>
    /// at it, and the landmark stands at the volume's centre; the flatten
    /// agrees.
    /// </summary>
    [Fact]
    public async Task AHallwayThatCannotFoldFiresTheChangelevel()
    {
        VmfChunk[] down =
        [
            Poi(200, "arrival", DownArrival, 90),
            Brush("trigger_once", 201, Hallway, [("filtername", "players")], ("OnStartTouch", Out(RoomTransit.VolumeName, "Transition"))),
            VolumeEntity(202),
        ];
        VmfDocument library = Library(down: down);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = MiddleLevel(90);
        BspData bsp = (await LinkAsync(rooms, level, mod: false)).Bsp;

        BspEntity changeLevel = OfClass(bsp, "trigger_changelevel").Single(e => e.Get("map") == "below");
        Assert.Equal(("c2r0_transition", "2"), (changeLevel.Get("targetname"), changeLevel.Get("spawnflags")));
        Assert.Equal("c2r0_transition\u001bChangeLevel\u001b\u001b0\u001b-1", Assert.Single(OfClass(bsp, "trigger_once")).Get("OnStartTouch"));
        Assert.Equal(At(2, 0, 90, RoomTransit.Centre(Volume)), OfClass(bsp, "info_landmark").Single(e => e.Get("targetname") == "mid__below").Get("origin"));
        Assert.Equal(5, Models(bsp));
        Assert.Equal(Comparable(await CompileFlatAsync(library, level, mod: false)), Comparable(bsp));
    }

    /// <summary>
    /// The top and bottom levels of a run: <c>up: none</c> spawns at the
    /// plain room's spawn point (the only room with one, as far from the
    /// down room as any), the down room leads below; <c>down: none</c> has
    /// the up room alone. The flatten agrees in both.
    /// </summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public async Task TheTopAndBottomLevels(bool mod)
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await CompileAsync(library);

        LevelGrid top = Level("top", "up: none\ndown_map: next\n", "plain@180, down");
        BspData first = (await LinkAsync(rooms, top, mod)).Bsp;
        Assert.Equal(mod ? [] : [(At(0, 0, 180, PlainSpawn), "0 225 0")], OfClass(first, "info_player_start").Select(e => (e.Get("origin"), e.Get("angles"))));
        Assert.Equal(mod ? 1 : 0, OfClass(first, LevelTransition.ClassName).Count);
        Assert.Equal(mod ? [] : ["top__next"], OfClass(first, "info_landmark").Select(e => e.Get("targetname")));
        Assert.Equal(Comparable(await CompileFlatAsync(library, top, mod)), Comparable(first));

        LevelGrid bottom = Level("last", "down: none\nup_map: prev\n", "up, plain");
        BspData last = (await LinkAsync(rooms, bottom, mod)).Bsp;
        Assert.Equal(mod ? 0 : 2, OfClass(last, "info_player_start").Count);
        Assert.Equal(mod ? [] : ["prev__last"], OfClass(last, "info_landmark").Select(e => e.Get("targetname")));
        Assert.Equal(Comparable(await CompileFlatAsync(library, bottom, mod)), Comparable(last));
    }

    /// <summary>
    /// The budget counts what the level holds (the rooms design, 15.6): with
    /// <c>-mod-entities</c> the two transitions cost no edict; in the stock
    /// fallback an unfolded room costs its changelevel and landmark, a folded
    /// one its landmark only (the trigger was the author's), and the spawn
    /// one start per spawn point (two here), the rooms' own starts stripped.
    /// </summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public async Task TheBudgetCountsTheTransitionsAndTheSpawn(bool mod)
    {
        RoomLibrary rooms = await CompileAsync(Library());
        LinkedLevel linked = await LinkAsync(rooms, MiddleLevel(0), mod);
        List<BspEntity> lump = EntityLump.Parse(linked.Bsp[BspLump.Entities]);
        LevelEntityReport budget = linked.EntityBudget!;
        Assert.Equal(lump.Count, budget.Listed);

        // world + button + hallway, and in the stock fallback the up room's
        // changelevel (its volume), two landmarks and two starts, the
        // hallway having become the down room's changelevel.
        Assert.Equal(mod ? 3 : 8, budget.Edicts);
        Assert.Equal(mod ? 5 : 8, lump.Count);
    }

    /// <summary>A level with transitions links to the same bytes at one thread and at many, run after run, in both modes.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ALevelWithTransitionsIsTheSameBytesAtAnyThreadCount(bool mod)
    {
        RoomLibrary rooms = await CompileAsync(Library(), degree: 4);
        LevelGrid level = Level("mid", Keys, "up@90, plain, down@270", "plain@180, plain, plain");
        byte[] serial = await BytesAsync(await LinkAsync(rooms, level, mod, 1));
        Assert.Equal(serial, await BytesAsync(await LinkAsync(rooms, level, mod, 8)));
        Assert.Equal(serial, await BytesAsync(await LinkAsync(rooms, level, mod, 1)));
        RoomLibrary again = await CompileAsync(Library(), degree: 1);
        Assert.Equal(serial, await BytesAsync(await LinkAsync(again, level, mod, 8)));
    }

    /// <summary>
    /// A pack of transition rooms is the same bytes whether its rooms compiled
    /// on one thread or on four, run after run (the rooms design, 15.5): the
    /// transition data is a function of the room's VMF.
    /// </summary>
    [Fact]
    public async Task APackOfTransitionRoomsIsTheSameBytesAtAnyThreadCount()
    {
        async Task<byte[]> PackAsync(int degree)
        {
            RoomLibrary rooms = await CompileAsync(Library(), degree);
            List<RoomPackItem> items = [];
            foreach (string name in new[] { "up", "down", "plain" })
            {
                items.Add(await RoomPackItem.CreateAsync(rooms.Find(name)!));
            }

            using MemoryStream pack = new();
            await RoomPack.SaveAsync(items, pack);
            return pack.ToArray();
        }

        byte[] serial = await PackAsync(1);
        Assert.Equal(serial, await PackAsync(4));
        Assert.Equal(serial, await PackAsync(1));
    }

    /// <summary>
    /// The transition data rides in the pack (<c>TRAN</c>, for the role rooms
    /// and the room with a spawn point), and a link of the packed rooms is the
    /// same bytes as a link of the rooms in memory.
    /// </summary>
    [Fact]
    public async Task TransitionsRoundTripThroughAPack()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        LevelGrid level = MiddleLevel(180);
        byte[] expected = await BytesAsync(await LinkAsync(rooms, level, mod: false));

        using MemoryStream pack = new();
        List<RoomPackItem> items = [];
        foreach (RoomObject room in rooms.Rooms)
        {
            items.Add(await RoomPackItem.CreateAsync(room));
        }

        await RoomPack.SaveAsync(items, pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        Assert.All(["up", "down", "plain"], name => Assert.NotNull(index.Find(name)!.Find(RoomTransit.SectionTag)));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            pack, index, [new RoomPackRequest("up", [2]), new RoomPackRequest("plain", [0]), new RoomPackRequest("down", [2])]);
        Assert.All(loaded, room => Assert.NotNull(room.TransitOfCompile));
        Assert.Equal(expected, await BytesAsync(await LinkAsync(RoomPropHarness.RoomsOf([.. loaded]), level, mod: false)));
    }

    /// <summary>
    /// A library's role rooms change nothing for a level that places none of
    /// them and says nothing of transitions: it links to the bytes the same
    /// level links to from a library without transition rooms, the plain
    /// room's player start kept.
    /// </summary>
    [Fact]
    public async Task ALevelWithoutTransitionsLinksAsBefore()
    {
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "plain, plain@90"), "solo");
        RoomLibrary withRoles = await CompileAsync(Library());
        RoomLibrary without = await CompileAsync(Library(up: [], down: [], plain: [PlainEntities[0]], roles: false));
        byte[] a = await BytesAsync(await LinkAsync(withRoles, level, mod: false));
        Assert.Equal(a, await BytesAsync(await LinkAsync(without, level, mod: false)));
        Assert.Equal(4, OfClass((await LinkAsync(withRoles, level, mod: false)).Bsp, "info_player_start").Count); // the harness room's and the plain room's, twice
        Assert.Null(without.Find("plain")!.TransitOfCompile);
        Assert.NotNull(withRoles.Find("plain")!.TransitOfCompile);
    }

    /// <summary>
    /// A room whose compile has a transition volume but no transition data
    /// (a pack written before transitions, a room compiled on its own) is
    /// refused by name rather than linked as an ordinary brush entity.
    /// </summary>
    [Fact]
    public async Task ARoomWithAVolumeButNoTransitionDataIsRefused()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        RoomObject up = rooms.Find("up")!;
        RoomLibrary stale = RoomPropHarness.RoomsOf(up with { Transit = null }, rooms.Find("plain")!, rooms.Find("down")!);
        LinkException refusal = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(stale, MiddleLevel(0), mod: false));
        Assert.Equal(
            "room up has a trigger_room_transition but no transition data from its compile (a pack written before the link carried"
            + " transitions, or a room built without ssmap room); recompile the library with ssmap room.",
            refusal.Message);
    }

    /// <summary>The level rule refuses through the link and the flatten alike, with the 15.4 text.</summary>
    [Fact]
    public async Task TheLevelRuleRefusesInTheLinkAndTheFlatten()
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = Level("two", Keys, "up, up@90, down");
        const string Expected = "level two: 2 up rooms ((0, 0), (1, 0)); a level has exactly one unless it says \"up: none\".";
        Assert.Equal(Expected, (await Assert.ThrowsAsync<RoomLintException>(() => LinkAsync(rooms, level, mod: false))).Message);
        Assert.Equal(Expected, Assert.Throws<RoomLintException>(() => LevelFlattener.FlattenLevel(level, library, new LevelFlattenOptions())).Message);
    }

    /// <summary>A role room's pack-time refusals come out of the library compile as the room's error, with the 15.4 text.</summary>
    [Fact]
    public async Task ARoleRoomWithoutWiringFailsItsCompile()
    {
        VmfChunk[] up = [Poi(100, "arrival", UpArrival, 0), VolumeEntity(103)];
        Exception error = await CompileErrorAsync(Library(up: up));
        Assert.IsType<RoomLintException>(error);
        Assert.Equal("room up: nothing fires Transition at cxry_transition.", error.Message);
    }

    /// <summary>
    /// The arrival's clearance is checked against the compiled room at pack
    /// time: an arrival inside a wall is refused, with the 15.4 text; one
    /// standing on the floor passes (every other fact's).
    /// </summary>
    [Fact]
    public async Task AnArrivalInAWallIsRefused()
    {
        VmfChunk[] up = [.. UpEntities.Skip(1), Poi(100, "arrival", new Vec3(8, 128, 16), 0)];
        Exception error = await CompileErrorAsync(Library(up: up));
        Assert.Equal("room up: the arrival point at (8, 128, 16) has no room for a standing player (32 x 32 x 72).", error.Message);
    }

    /// <summary>
    /// A brush entity does not block an arrival (it moves, or it is the
    /// volume itself): an arrival inside the button's pedestal compiles.
    /// </summary>
    [Fact]
    public async Task ABrushEntityDoesNotBlockTheArrival()
    {
        VmfChunk[] up = [.. UpEntities.Skip(1), Poi(100, "arrival", new Vec3(208, 48, 16), 0)];
        RoomLibrary rooms = await CompileAsync(Library(up: up));
        Assert.Equal(new Vec3(208, 48, 16), rooms.Find("up")!.TransitOfCompile!.Arrival!.Value.Origin);
    }

    /// <summary>The transition entities take their classes' costs: <c>logic_level_transition</c> is server-only, the stock classes take an edict.</summary>
    [Fact]
    public void TheClassTableKnowsTheTransitionClasses()
    {
        Assert.Equal(EntityCost.ServerOnly, EntityClassTable.Default.Classify(LevelTransition.ClassName));
        Assert.Equal(EntityCost.Edict, EntityClassTable.Default.Classify("trigger_changelevel"));
        Assert.Equal(EntityCost.Edict, EntityClassTable.Default.Classify("info_landmark"));
    }
}
