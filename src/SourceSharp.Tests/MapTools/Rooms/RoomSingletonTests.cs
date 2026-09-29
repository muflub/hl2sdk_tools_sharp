//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The level-wide singletons (section 8 of the rooms design): the agreement
/// rules a room's copy of the sun, a controller or a <c>sky_camera</c> meets
/// when the pack is built (<see cref="RoomLibraryEntities.KeepInRoom"/>,
/// decision D3), the one-of-each rule the link and the flatten both run
/// (<see cref="LevelSingletons"/>), how a library entity is written into a
/// level, and the level that comes out: one of each, never turned, the same
/// in the linked and the flattened map, and counted once in the budget.
/// </summary>
/// <remarks>
/// The linker facts use small harness rooms on the walkable kit, as
/// <see cref="RoomCorrectnessFixTests"/> does, and go through the same split,
/// room compile, link and flatten <c>ssmap room</c> and <c>ssmap link</c> run.
/// A room carrying a singleton of its own reaches the link only from a host
/// that packs rooms itself; those facts add the entity to the room after the
/// split, which is exactly that road.
/// </remarks>
public sealed class RoomSingletonTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    private static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    private static RoomDefinition Other => RoomHarness.WalkableRoom(
        "other", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    private static VmfChunk LibrarySun => Entity(
        "light_environment", 900001, ("origin", "-64 -64 128"), ("angles", "-45 30 0"), ("_light", "255 255 255 200"), ("_ambient", "1 2 3 4"));

    private static VmfChunk LibraryFog => Entity("env_fog_controller", 900002, ("origin", "-64 0 0"), ("fogenable", "1"), ("fogcolor", "1 2 3"));

    // ---- agreement at pack time (D3) -------------------------------------------

    /// <summary>
    /// A room's sun that equals the library's is dropped from the room: its
    /// editor id and its origin do not count (a map-wide entity's position
    /// means nothing, and a copy in a cell cannot stand where the gap's does).
    /// </summary>
    [Fact]
    public void AnEqualSunInARoomIsDropped()
    {
        VmfChunk copy = Entity(
            "light_environment", 17, ("origin", "64 64 128"), ("angles", "-45 30 0"), ("_light", "255 255 255 200"), ("_ambient", "1 2 3 4"));
        Assert.False(RoomLibraryEntities.KeepInRoom("hub", copy, [LibrarySun]));
    }

    /// <summary>
    /// A room's sun that differs from the library's is refused with the
    /// design's message, naming the room and the first key that differs; a
    /// key only one copy has reads as empty on the other, either way round.
    /// </summary>
    [Fact]
    public void ARoomsSunThatDiffersIsRefusedNamingTheRoomAndKey()
    {
        VmfChunk brighter = Entity(
            "light_environment", 17, ("angles", "-45 30 0"), ("_light", "255 255 255 400"), ("_ambient", "1 2 3 4"));
        Assert.Equal(
            "room hub: its light_environment differs from the library's (_light: \"255 255 255 400\" against \"255 255 255 200\"); the sun is library-wide.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryEntities.KeepInRoom("hub", brighter, [LibrarySun])).Message);

        VmfChunk noAmbient = Entity("light_environment", 17, ("angles", "-45 30 0"), ("_light", "255 255 255 200"));
        Assert.Equal(
            "room hub: its light_environment differs from the library's (_ambient: \"\" against \"1 2 3 4\"); the sun is library-wide.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryEntities.KeepInRoom("hub", noAmbient, [LibrarySun])).Message);

        VmfChunk extra = Entity(
            "light_environment", 17, ("angles", "-45 30 0"), ("_light", "255 255 255 200"), ("_ambient", "1 2 3 4"), ("SunSpreadAngle", "5"));
        Assert.Equal(
            "room hub: its light_environment differs from the library's (SunSpreadAngle: \"5\" against \"\"); the sun is library-wide.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryEntities.KeepInRoom("hub", extra, [LibrarySun])).Message);
    }

    /// <summary>A room's sun in a library with none in its gaps is refused: the sun belongs to the library.</summary>
    [Fact]
    public void ARoomsSunWithoutALibrarySunIsRefused()
    {
        Assert.Equal(
            "room hub: it has a light_environment, and the library has none in the gaps between rooms; the sun is library-wide, so it belongs there.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryEntities.KeepInRoom("hub", LibrarySun, [])).Message);
    }

    /// <summary>
    /// A <c>sky_camera</c> is refused in a room with the design's message,
    /// and in the gaps too (the library's skybox room that may hold one is a
    /// later part of the rooms work; until then a camera that silently
    /// vanished would be the library's 3D sky).
    /// </summary>
    [Fact]
    public void ASkyCameraIsRefusedInARoomAndInTheGaps()
    {
        Assert.Equal(
            "room hub: sky_camera is allowed only in the library's skybox room.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryEntities.KeepInRoom("hub", Entity("sky_camera", 3, ("scale", "16")), [])).Message);

        VmfDocument inRoom = RoomHarness.LibraryVmf(Hub);
        inRoom.Chunks.Add(Entity("sky_camera", 900010, ("origin", "64 64 64")));
        Assert.Equal(
            "room hub: sky_camera is allowed only in the library's skybox room.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(inRoom)).Message);

        VmfDocument inGap = RoomHarness.LibraryVmf(Hub);
        inGap.Chunks.Add(Entity("sky_camera", 900011, ("origin", "-64 0 0")));
        Assert.Equal(
            "the library has a sky_camera (entity 900011) in the gaps between rooms; sky_camera is allowed only in the library's skybox room.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(inGap)).Message);
    }

    /// <summary>
    /// The controllers follow the sun, but a named one the library does not
    /// hold under that name is the room's own (per-room fog is a trigger and
    /// a named controller) and stays; one named as the library's copy, or
    /// unnamed, is library-wide: dropped when equal, refused when not, and an
    /// unnamed one with no library copy is refused. Every other class,
    /// <c>water_lod_control</c> included, stays with the room.
    /// </summary>
    [Fact]
    public void AControllerFollowsTheSunUnlessItIsNamedAsTheRoomsOwn()
    {
        VmfChunk named = Entity("env_fog_controller", 5, ("targetname", "cxry_fog"), ("fogcolor", "9 9 9"));
        Assert.True(RoomLibraryEntities.KeepInRoom("hub", named, [LibraryFog]));

        VmfChunk namedLibrary = Entity("env_fog_controller", 900003, ("origin", "-64 64 0"), ("targetname", "level_fog"), ("fogcolor", "4 5 6"));
        VmfChunk sameName = Entity("env_fog_controller", 6, ("targetname", "level_fog"), ("fogcolor", "4 5 6"));
        Assert.False(RoomLibraryEntities.KeepInRoom("hub", sameName, [LibraryFog, namedLibrary]));

        VmfChunk equal = Entity("env_fog_controller", 7, ("fogenable", "1"), ("fogcolor", "1 2 3"));
        Assert.False(RoomLibraryEntities.KeepInRoom("hub", equal, [LibraryFog]));

        VmfChunk differs = Entity("env_fog_controller", 7, ("fogenable", "1"), ("fogcolor", "3 2 1"));
        Assert.Equal(
            "room hub: its env_fog_controller differs from the library's (fogcolor: \"3 2 1\" against \"1 2 3\"); env_fog_controller is library-wide.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryEntities.KeepInRoom("hub", differs, [LibraryFog])).Message);

        Assert.Equal(
            "room hub: it has an unnamed shadow_control, and the library has none in the gaps between rooms; an unnamed shadow_control is library-wide, so it belongs there (name it to keep it with the room).",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryEntities.KeepInRoom("hub", Entity("shadow_control", 8), [LibraryFog])).Message);

        Assert.True(RoomLibraryEntities.KeepInRoom("hub", Entity("water_lod_control", 9), [LibraryFog]));
        Assert.True(RoomLibraryEntities.KeepInRoom("hub", Entity("light", 10), [LibrarySun]));
    }

    /// <summary>
    /// What "equal" means: keys by name ignoring case with the last of a
    /// repeated key winning (as vbsp keeps it), the editor's <c>id</c> and
    /// <c>hammerid</c> and the <c>origin</c> ignored, a missing key as an
    /// empty one, and the outputs as a list in order (the order they fire in).
    /// </summary>
    [Fact]
    public void EqualIsEveryKeyButTheIdsAndOriginAndTheOutputsInOrder()
    {
        static List<KeyValuePair<string, string>> Pairs(params (string Key, string Value)[] pairs) =>
            [.. pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))];

        const string First = "a,Enable,,0,-1";
        const string Second = "b,Disable,,1,-1";

        Assert.Null(RoomLibraryEntities.Difference(
            Pairs(("id", "1"), ("hammerid", "4"), ("origin", "1 2 3"), ("Angles", "0 1 0"), ("OnTrigger", First)),
            Pairs(("id", "2"), ("origin", "9 9 9"), ("angles", "0 0 0"), ("angles", "0 1 0"), ("OnTrigger", First))));
        Assert.Null(RoomLibraryEntities.Difference(Pairs(("spawnflags", "")), Pairs()));

        Assert.Equal(
            ("angles", "0 0 0", "0 1 0"),
            RoomLibraryEntities.Difference(Pairs(("angles", "0 1 0"), ("angles", "0 0 0")), Pairs(("angles", "0 1 0"))));
        Assert.Equal(
            ("OnTrigger", Second, First),
            RoomLibraryEntities.Difference(Pairs(("OnTrigger", Second), ("OnTrigger", First)), Pairs(("OnTrigger", First), ("OnTrigger", Second))));
        Assert.Equal(
            ("OnTrigger", string.Empty, Second),
            RoomLibraryEntities.Difference(Pairs(("OnTrigger", First)), Pairs(("OnTrigger", First), ("OnTrigger", Second))));
        Assert.Equal(
            ("OnUser1", First, string.Empty),
            RoomLibraryEntities.Difference(Pairs(("OnUser1", First)), Pairs()));
    }

    /// <summary>
    /// The gaps hold one of each: two suns (whatever their names), or two
    /// controllers of one class under one name (both unnamed is one name), are
    /// refused naming the entities; two controllers of different names are two
    /// entities.
    /// </summary>
    [Fact]
    public void TheGapsHoldOneOfEach()
    {
        RoomLibraryEntities.CheckGaps([LibrarySun, LibraryFog, Entity("env_fog_controller", 4, ("targetname", "other"))]);

        Assert.Equal(
            "the library has 2 light_environment entities in the gaps between rooms (entities 900001, 5); the sun is library-wide, so there is one.",
            Assert.Throws<RoomLibraryException>(
                () => RoomLibraryEntities.CheckGaps([LibrarySun, Entity("light_environment", 5, ("targetname", "sun2"))])).Message);
        Assert.Equal(
            "the library has 2 unnamed env_fog_controller entities in the gaps between rooms (entities 900002, 6); a level has one of each.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryEntities.CheckGaps([LibraryFog, Entity("env_fog_controller", 6)])).Message);
        Assert.Equal(
            "the library has 2 \"f\" shadow_control entities in the gaps between rooms (entities 7, 8); a level has one of each.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryEntities.CheckGaps(
                [Entity("shadow_control", 7, ("targetname", "f")), Entity("shadow_control", 8, ("targetname", "f"))])).Message);
    }

    /// <summary>
    /// The split applies the rules to every room: an equal copy leaves the
    /// room's document, a different one refuses the library naming the room
    /// and key, and a library without a sun but a room with one is refused.
    /// </summary>
    [Fact]
    public void TheSplitHoldsEveryRoomToTheLibrarysSun()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, Other);
        library.Chunks.Add(LibrarySun);
        VmfChunk copy = Entity(
            "light_environment", 900020, ("origin", "400 64 128"), ("angles", "-45 30 0"), ("_light", "255 255 255 200"), ("_ambient", "1 2 3 4"));
        library.Chunks.Add(copy);
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        Assert.Equal(["900001"], split.LibraryEntities.Select(e => e.GetValue("id")));
        Assert.All(split.Rooms, r => Assert.DoesNotContain(
            r.Document.GetChunks(MapFileLoader.EntityChunk), e => e.GetValue("classname") == "light_environment"));

        copy.Keys.Single(k => k.Name == "angles").Value = "-45 120 0";
        Assert.Equal(
            "room other: its light_environment differs from the library's (angles: \"-45 120 0\" against \"-45 30 0\"); the sun is library-wide.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library)).Message);

        VmfDocument sunless = RoomHarness.LibraryVmf(Hub);
        sunless.Chunks.Add(Entity("light_environment", 900021, ("origin", "64 64 128")));
        Assert.StartsWith(
            "room hub: it has a light_environment, and the library has none",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(sunless)).Message,
            StringComparison.Ordinal);
    }

    // ---- one of each in a level ---------------------------------------------------

    /// <summary>
    /// The link's and the flatten's rule: other classes pass; the library's
    /// copies come first; a room's equal later copy is dropped and recorded
    /// against its placement; a different one is refused against whichever
    /// copy came first, the library's or another room's.
    /// </summary>
    [Fact]
    public void ALevelKeepsTheFirstCopyDropsEqualOnesAndRefusesDifferentOnes()
    {
        LevelSingletons singletons = new([LibrarySun]);
        Assert.Same(singletons.Library[0], singletons.Library.Single());
        Assert.True(singletons.Keep("a", 0, "light", Pairs(("classname", "light"))));

        Assert.False(singletons.Keep("a", 0, "light_environment", Pairs(
            ("classname", "light_environment"), ("hammerid", "3"), ("origin", "5 5 5"), ("angles", "-45 30 0"), ("_light", "255 255 255 200"),
            ("_ambient", "1 2 3 4"), ("id", "900001"))));
        Assert.Equal([(0, "light_environment")], singletons.Dropped);
        Assert.Equal(
            "room b: its light_environment differs from the library's (angles: \"0 0 0\" against \"-45 30 0\"); the sun is library-wide.",
            Assert.Throws<LinkException>(() => singletons.Keep("b", 1, "light_environment", Pairs(
                ("classname", "light_environment"), ("angles", "0 0 0"), ("_light", "255 255 255 200"), ("_ambient", "1 2 3 4")))).Message);

        Assert.True(singletons.Keep("a", 0, "water_lod_control", Pairs(("classname", "water_lod_control"), ("cheapwaterstartdistance", "1"))));
        Assert.False(singletons.Keep("b", 1, "water_lod_control", Pairs(("classname", "water_lod_control"), ("cheapwaterstartdistance", "1"))));
        Assert.Equal(
            "room c: its water_lod_control differs from room a's (cheapwaterstartdistance: \"2\" against \"1\"); the level has one water_lod_control.",
            Assert.Throws<LinkException>(() => singletons.Keep("c", 2, "water_lod_control", Pairs(
                ("classname", "water_lod_control"), ("cheapwaterstartdistance", "2")))).Message);

        // A controller's identity is its class and name: c0r0_fog and c1r0_fog are two.
        Assert.True(singletons.Keep("a", 0, "env_fog_controller", Pairs(("classname", "env_fog_controller"), ("targetname", "c0r0_fog"))));
        Assert.True(singletons.Keep("a", 1, "env_fog_controller", Pairs(("classname", "env_fog_controller"), ("targetname", "c1r0_fog"), ("fogcolor", "1 1 1"))));
        Assert.Equal([(0, "light_environment"), (1, "water_lod_control")], singletons.Dropped);
    }

    /// <summary>
    /// A library entity in a level: at the level's origin, never turned; in
    /// the flatten a copy of the VMF entity (an entity without an origin is
    /// copied as it is); in the link the keys vbsp's compile of that copy
    /// leaves (each new key in front, a repeated key keeping its place with
    /// the later value, <c>id</c> as <c>hammerid</c>) and then the outputs.
    /// </summary>
    [Fact]
    public void ALibraryEntityStandsAtTheLevelsOriginUnturned()
    {
        VmfChunk sun = Entity("light_environment", 900001, ("origin", "-64 -64 128"), ("angles", "-45 30 0"), ("Angles", "-45 60 0"));
        VmfChunk connections = new(MapFileLoader.ConnectionsChunk);
        connections.AddKey("OnUser1", "x,Kill,,0,-1");
        sun.Children.Add(connections);

        VmfChunk flat = RoomLibraryEntities.ForFlatten(sun);
        Assert.NotSame(sun, flat);
        Assert.Equal("0 0 0", flat.GetValue("origin"));
        Assert.Equal("-64 -64 128", sun.GetValue("origin"));
        Assert.Equal("-45 30 0", flat.GetValue("angles"));

        BspEntity linked = RoomLibraryEntities.ToLinked(sun);
        Assert.Equal(
            [("angles", "-45 60 0"), ("origin", "0 0 0"), ("classname", "light_environment"), ("hammerid", "900001"), ("OnUser1", "x,Kill,,0,-1")],
            linked.Pairs.Select(p => (p.Key, p.Value)));

        VmfChunk unplaced = Entity("shadow_control", 3, ("color", "1 1 1"));
        Assert.Null(RoomLibraryEntities.ForFlatten(unplaced).GetValue("origin"));

        Assert.Throws<ArgumentNullException>(() => RoomLibraryEntities.ForFlatten(null!));
        Assert.Throws<ArgumentNullException>(() => RoomLibraryEntities.ToLinked(null!));
        Assert.Throws<ArgumentNullException>(() => RoomLibraryEntities.Count(null!));
    }

    /// <summary>
    /// Which classes are singletons: the library-wide ones and
    /// <c>water_lod_control</c>, matched exactly; the library's are counted
    /// by class, bound to no room.
    /// </summary>
    [Fact]
    public void TheSingletonClassesAndTheirCounts()
    {
        foreach (string classname in new[] { "light_environment", "env_fog_controller", "env_tonemap_controller", "shadow_control", "postprocess_controller", "water_lod_control" })
        {
            Assert.True(RoomLibraryEntities.IsLevelSingleton(classname), classname);
        }

        Assert.False(RoomLibraryEntities.IsLevelSingleton("Water_LOD_Control"));
        Assert.False(RoomLibraryEntities.IsLevelSingleton("sky_camera"));
        Assert.False(RoomLibraryEntities.IsLevelSingleton(null));

        RoomEntityCounts counts = RoomLibraryEntities.Count([LibrarySun, LibraryFog]);
        Assert.Equal(new EntityTally(2, 0, 0), counts.Tally(EntityClassTable.Default));
        Assert.Equal(new EntityTally(0, 0, 0), RoomLibraryEntities.Count([]).Tally(EntityClassTable.Default));
    }

    // ---- the budget ---------------------------------------------------------------

    /// <summary>
    /// The library's entities are the level's: counted once with the
    /// worldspawn however many rooms are placed, reported apart, and never
    /// among the rooms; without them a level budgets as it did.
    /// </summary>
    [Fact]
    public void TheLibrarysEntitiesAreCountedOncePerLevel()
    {
        RoomEntityCounts room = RoomEntityCounts.FromClasses(["light", "light", "func_detail"]);
        RoomEntityCounts library = RoomLibraryEntities.Count([LibrarySun, LibraryFog]);

        LevelEntityReport plain = LevelEntityBudget.Check([("a", room), ("a", room)], 512, EntityClassTable.Default);
        LevelEntityReport with = LevelEntityBudget.Check([("a", room), ("a", room)], 512, EntityClassTable.Default, library);
        LevelEntityReport three = LevelEntityBudget.Check([("a", room), ("a", room), ("a", room)], 512, EntityClassTable.Default, library);

        Assert.Equal(1 + 4, plain.Edicts);
        Assert.Equal(default, plain.Library);
        Assert.Equal(plain.Edicts + 2, with.Edicts);
        Assert.Equal(plain.Listed + 2, with.Listed);
        Assert.Equal(new EntityTally(2, 0, 0), with.Library);
        Assert.Equal(with.Edicts + 2, three.Edicts);
        Assert.Equal(plain.Rooms.Select(r => r.Room), with.Rooms.Select(r => r.Room));
        Assert.Equal(plain.Rooms, LevelEntityBudget.Check([("a", room), ("a", room)], 512, EntityClassTable.Default, null).Rooms);
    }

    /// <summary>
    /// A layout's budget leaves room for the library's entities: the least a
    /// level can bring counts them, the refusal says so, every level made
    /// within the budget keeps within it with them (the same level as a
    /// budget that much smaller without them), and a negative count is
    /// refused.
    /// </summary>
    [Fact]
    public void ALayoutBudgetLeavesRoomForTheLibrarysEntities()
    {
        RoomDefinition[] kinds =
        [
            RoomHarness.WalkableRoom("cross", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY),
            RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
            RoomHarness.WalkableRoom("end", RoomFacing.PositiveX),
        ];

        LinkException refused = Assert.Throws<LinkException>(() => LevelGenerator.Generate(
            kinds, new LevelGeneratorOptions(3, 3, 1), "l", "x", new LayoutEntityBudget(10, [1, 1, 1]) { LevelEdicts = 1 }));
        Assert.Equal(
            "no level of 3x3 cells keeps within the entity budget of 10 edicts: its 9 room(s) bring at least 11, the worldspawn and the library's own entities included.",
            refused.Message);

        int[] costs = [9, 3, 1];
        for (ulong seed = 0; seed < 10; seed++)
        {
            LevelGrid level = LevelGenerator.Generate(kinds, new LevelGeneratorOptions(3, 3, seed), "l", "x", new LayoutEntityBudget(56, costs) { LevelEdicts = 5 });
            int edicts = 1 + 5 + level.Cells.OfType<LevelCell>().Sum(c => costs[Array.FindIndex(kinds, k => k.Name == c.Room)]);
            Assert.True(edicts <= 56, $"seed {seed}: {edicts} edicts");

            // The library's five edicts take five from what the rooms may spend, no more and no less.
            LevelGrid tighter = LevelGenerator.Generate(kinds, new LevelGeneratorOptions(3, 3, seed), "l", "x", new LayoutEntityBudget(51, costs));
            Assert.Equal(LevelYaml.Write(tighter, []), LevelYaml.Write(level, []));
        }

        Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(
            kinds, new LevelGeneratorOptions(3, 3, 1), "l", "x", new LayoutEntityBudget(40, costs) { LevelEdicts = -1 }));
    }

    // ---- the pack -------------------------------------------------------------------

    /// <summary>
    /// The library section round-trips through a pack: every entity key for
    /// key and in library order, found by its tag whatever the section's
    /// place in the table; a pack without one reads as no entities.
    /// </summary>
    [Fact]
    public async Task TheLibrarySectionRoundTripsThroughAPack()
    {
        RoomPackSectionData options = new RoomLibraryOptions(333).ToSection()!.Value;
        foreach (RoomPackSectionData[] sections in new[]
        {
            new[] { RoomLibraryEntities.ToSection([LibrarySun, LibraryFog]), options },
            new[] { options, RoomLibraryEntities.ToSection([LibrarySun, LibraryFog]) },
        })
        {
            using MemoryStream pack = new();
            await RoomPack.SaveAsync(sections, [], pack, CancellationToken.None);
            pack.Position = 0;
            RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
            IReadOnlyList<VmfChunk> read = await RoomPack.ReadLibraryEntitiesAsync(pack, index);
            Assert.Equal(
                new[] { LibrarySun, LibraryFog }.Select(e => e.Keys.Select(k => (k.Name, k.Value)).ToList()),
                read.Select(e => e.Keys.Select(k => (k.Name, k.Value)).ToList()));
            Assert.Equal(new RoomLibraryOptions(333), await RoomPack.ReadLibraryOptionsAsync(pack, index));
        }

        using MemoryStream bare = new();
        await RoomPack.SaveAsync([options], [], bare, CancellationToken.None);
        bare.Position = 0;
        Assert.Empty(await RoomPack.ReadLibraryEntitiesAsync(bare, await RoomPack.ReadIndexAsync(bare)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomPack.ReadLibraryEntitiesAsync(null!, null!));
    }

    // ---- link and flatten -------------------------------------------------------------

    /// <summary>
    /// A library with a sun and a fog controller in its gaps, two rooms
    /// placed at each rotation: the linked map and the flattened map's
    /// compile each carry exactly one of each, the sun's angles as authored
    /// at every rotation, the two maps' copies key for key the same (but the
    /// editor number the flatten renumbers), and the link's budget counts
    /// them once: its edicts are the linked lump's entities.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelHasOneOfEachSingletonAtEveryRotation(int rotation)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        library.Chunks.Add(LibrarySun);
        library.Chunks.Add(LibraryFog);

        (LinkedLevel linked, BspData whole) = await LinkAndCompileFlatAsync(library, null, $"hub@{rotation}, hub@{(rotation + 90) % 360}");

        foreach (BspData bsp in new[] { linked.Bsp, whole })
        {
            List<BspEntity> entities = [.. EntityLump.Parse(bsp[BspLump.Entities])];
            BspEntity sun = Assert.Single(entities, e => e.ClassName == "light_environment");
            Assert.Single(entities, e => e.ClassName == "env_fog_controller");
            Assert.Equal("-45 30 0", sun.Get("angles"));
            Assert.Equal("0 0 0", sun.Get("origin"));
        }

        Assert.Equal(Singletons(whole), Singletons(linked.Bsp));

        List<BspEntity> lump = [.. EntityLump.Parse(linked.Bsp[BspLump.Entities])];
        Assert.Equal(["worldspawn", "light_environment", "env_fog_controller"], lump.Take(3).Select(e => e.ClassName));
        LevelEntityReport budget = linked.EntityBudget!;
        Assert.Equal(new EntityTally(2, 0, 0), budget.Library);
        Assert.Equal(lump.Count, budget.Listed);
        Assert.Equal(lump.Count, budget.Edicts);
    }

    /// <summary>
    /// Rooms a host packed itself, each still carrying the sun: the link and
    /// the flatten keep the library's and drop every equal copy, and the
    /// budget is what the lump holds; a room whose sun differs is refused at
    /// link with the room and the key.
    /// </summary>
    [Fact]
    public async Task EqualCopiesInHostPackedRoomsAreDroppedAndADifferentOneRefused()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, Other);
        library.Chunks.Add(LibrarySun);
        VmfChunk copy = Entity(
            "light_environment", 900030, ("origin", "64 64 128"), ("angles", "-45 30 0"), ("_light", "255 255 255 200"), ("_ambient", "1 2 3 4"));

        (LinkedLevel linked, _) = await LinkAndCompileFlatAsync(library, (_, document) => document.Chunks.Add(VmfPlacement.Clone(copy)), "hub, other, hub");
        List<BspEntity> lump = [.. EntityLump.Parse(linked.Bsp[BspLump.Entities])];
        Assert.Single(lump, e => e.ClassName == "light_environment");
        Assert.Equal(lump.Count, linked.EntityBudget!.Edicts);

        copy.Keys.Single(k => k.Name == "_light").Value = "1 1 1 1";
        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => LinkAndCompileFlatAsync(library, (room, document) =>
            {
                if (room == "other")
                {
                    document.Chunks.Add(VmfPlacement.Clone(copy));
                }
            }, "hub, other"));
        Assert.Equal(
            "room other: its light_environment differs from the library's (_light: \"1 1 1 1\" against \"255 255 255 200\"); the sun is library-wide.",
            refused.Message);
    }

    /// <summary>
    /// <c>water_lod_control</c>, which vbsp adds to a room compile with
    /// water: the link keeps the first placement's, drops the equal copies of
    /// the others and refuses a different one, naming both rooms.
    /// </summary>
    [Fact]
    public async Task WaterLodControlIsKeptOnce()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, Other);
        static void Add(VmfDocument document, string distance) =>
            document.Chunks.Add(Entity("water_lod_control", 900040, ("origin", "32 32 32"), ("cheapwaterstartdistance", distance), ("cheapwaterenddistance", "2000")));

        (LinkedLevel linked, _) = await LinkAndCompileFlatAsync(library, (_, document) => Add(document, "1000"), "hub, other, hub");
        Assert.Single(EntityLump.Parse(linked.Bsp[BspLump.Entities]), e => e.ClassName == "water_lod_control");
        Assert.Equal(EntityLump.Parse(linked.Bsp[BspLump.Entities]).Count, linked.EntityBudget!.Edicts);

        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => LinkAndCompileFlatAsync(library, (room, document) => Add(document, room == "hub" ? "1000" : "500"), "hub, other"));
        Assert.Equal(
            "room other: its water_lod_control differs from room hub's (cheapwaterstartdistance: \"500\" against \"1000\"); the level has one water_lod_control.",
            refused.Message);
    }

    /// <summary>
    /// A level with library singletons links to the same bytes at one
    /// thread and at many, run after run, and flattens to the same VMF.
    /// </summary>
    [Fact]
    public async Task ALevelWithSingletonsIsTheSameBytesAtAnyThreadCount()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, Other);
        library.Chunks.Add(LibrarySun);
        library.Chunks.Add(LibraryFog);
        RoomLibrary compiled = await CompileAsync(library, null);
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub@90, other, hub@180", "other@270, hub, other"), "same");

        byte[] serial = await LinkBytesAsync(level, compiled, 1);
        Assert.Equal(serial, await LinkBytesAsync(level, compiled, 8));
        Assert.Equal(serial, await LinkBytesAsync(level, compiled, 1));
        Assert.Equal(serial, await LinkBytesAsync(level, compiled, 8));
        Assert.Equal(LevelFlattener.Flatten(level, library).ToBytes(), LevelFlattener.Flatten(level, library).ToBytes());
    }

    // ---- helpers ------------------------------------------------------------------------

    private static List<KeyValuePair<string, string>> Pairs(params (string Key, string Value)[] pairs) =>
        [.. pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))];

    /// <summary>A point entity chunk with an id and a class.</summary>
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

    /// <summary>The singletons of a map, every key but the editor number, in lump order.</summary>
    private static List<string> Singletons(BspData bsp) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities])
            .Where(e => RoomLibraryEntities.IsLevelSingleton(e.ClassName))
            .Select(e => string.Join(" | ", e.Pairs.Where(p => p.Key != "hammerid").Select(p => $"{p.Key}={p.Value}")))];

    /// <summary>
    /// The library's rooms compiled, each room's document first handed to
    /// <paramref name="addToRoom"/> (a host packing rooms itself), with the
    /// library's own entities as the pack carries them.
    /// </summary>
    private static async Task<RoomLibrary> CompileAsync(VmfDocument library, Action<string, VmfDocument>? addToRoom)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize) { LibraryEntities = split.LibraryEntities };
        foreach (LibraryRoom room in split.Rooms)
        {
            addToRoom?.Invoke(room.Definition.Name, room.Document);
            VbspContext context = await RoomHarness.ContextAsync();
            context.MapBase = room.Definition.Name;
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, context));
        }

        return compiled;
    }

    /// <summary>
    /// The level of <paramref name="rows"/> linked from the library's rooms,
    /// and the same level flattened and compiled whole.
    /// </summary>
    private static async Task<(LinkedLevel Linked, BspData Whole)> LinkAndCompileFlatAsync(
        VmfDocument library, Action<string, VmfDocument>? addToRoom, params string[] rows)
    {
        RoomLibrary compiled = await CompileAsync(library, addToRoom);
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", rows), "singletons");
        LevelLayout layout = level.ToLayout(name => compiled.Find(name)?.Definition, compiled.CellSize, compiled.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, compiled, await RoomHarness.ContextAsync());

        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await RoomHarness.ContextAsync());
        Assert.NotNull(whole.Bsp);
        return (linked, whole.Bsp!);
    }

    private static async Task<byte[]> LinkBytesAsync(LevelGrid level, RoomLibrary library, int degree)
    {
        LevelLayout layout = level.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
        VbspContext context = await RoomHarness.ContextAsync();
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, library, context);
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }
}
