//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Several libraries split for one pack (the rooms design, 17.10, D26):
/// every room qualified and carrying its library's name keys, every later
/// library's room given the first library's worldspawn, the pack's
/// singletons the first library's; and the pieces that serve it (the
/// worldspawn a room carries, the name keys a room compiles with, the
/// cache key, the pack and level ids, the names a library holds rooms by).
/// </summary>
public sealed class RoomPackCombinerTests
{
    // ---- the plan -----------------------------------------------------------------

    /// <summary>
    /// Two libraries of rooms named alike: each room qualified, grouped by
    /// library; the first library's skybox packed last among its rooms and
    /// named by the pack; the later library's skybox dropped with its line;
    /// the later library's rooms carrying the first library's worldspawn
    /// keys and their own brushes; the first library's rooms their own
    /// documents; each room its own library's name keys.
    /// </summary>
    [Fact]
    public void EveryRoomIsQualifiedAndCompiledUnderTheFirstWorldspawn()
    {
        VmfDocument first = RoomSkyboxHarness.Library();
        first.GetChunk("world")!.AddKey("comment", "first");
        VmfDocument second = RoomSkyboxHarness.Library();
        VmfChunk world = second.GetChunk("world")!;
        world.AddKey("comment", "second");
        world.AddKey(RoomLibraryOptions.NameKeysKey, "friend");

        RoomPackPlan plan = RoomPackCombiner.Plan([Source("base", first), Source("caves", second)]);

        Assert.Equal(["base", "caves"], plan.Spaces.Select(s => s.Key));
        Assert.Equal(["base.hub", "base.other", "base.sky", "caves.hub", "caves.other"], plan.Rooms.Select(r => r.Definition.Name));
        Assert.Equal("base.sky", plan.SkyboxRoom);
        Assert.Equal(["library caves: its skybox room \"sky\" is dropped; the level's skybox is library base's, \"sky\"."], plan.Warnings);
        Assert.Null(plan.Spaces[0].NameKeys);
        Assert.Equal("friend", plan.Spaces[1].NameKeys);
        Assert.All(plan.Spaces[0].Rooms, r => Assert.Equal(new RoomNamespace("base", null), r.Namespace));
        Assert.All(plan.Spaces[1].Rooms, r => Assert.Equal(["friend"], r.Namespace!.NameKeys!));

        RoomLibrarySplit own = RoomLibraryVmf.SplitLibrary(second);
        LibraryRoom caves = plan.Spaces[1].Rooms[0];
        VmfChunk cavesWorld = caves.Document.GetChunk("world")!;
        Assert.Equal("first", cavesWorld.GetValue("comment"));
        Assert.Equal(RoomLibraryVmf.RoomWorldKeys(first.GetChunk("world")!), [.. cavesWorld.Keys.Select(k => new KeyValuePair<string, string>(k.Name, k.Value))]);
        Assert.Equal(own.Rooms[0].Document.GetChunk("world")!.Chunks.Count(), cavesWorld.Chunks.Count());
        Assert.Equal(own.Rooms[0].Corner, caves.Corner);
        Assert.Equal(own.Rooms[0].Definition.Sockets, caves.Definition.Sockets);

        RoomLibrarySplit firstSplit = RoomLibraryVmf.SplitLibrary(first);
        Assert.Equal(firstSplit.Rooms[0].Document.ToBytes(), plan.Spaces[0].Rooms[0].Document.ToBytes());
        Assert.Equal(firstSplit.LibraryEntities.Count, plan.LibraryEntities.Count);
        Assert.Equal(RoomPackNamespaces.SingletonDigest(RoomLibraryVmf.RoomWorldKeys(first.GetChunk("world")!), firstSplit.LibraryEntities), plan.SingletonsSha256);
    }

    /// <summary>
    /// The singleton rule's lines are the link's, but never the worldspawn
    /// or navigation line: no room of the pack is compiled under another
    /// library's worldspawn. A library alone is one namespace and warns of
    /// nothing. The navigation is the first library's.
    /// </summary>
    [Fact]
    public void TheSingletonLinesAreTheLinksWithoutTheWorldspawns()
    {
        VmfDocument first = RoomPropHarness.Library();
        VmfDocument second = RoomPropHarness.Library();
        second.GetChunk("world")!.AddKey("skyname", "sky_other");
        second.GetChunk("world")!.AddKey(NavSettings.StepKey, "24");
        second.Chunks.Add(RoomLightHarness.Sun());

        RoomPackPlan plan = RoomPackCombiner.Plan([Source("base", first), Source("caves", second)]);
        Assert.Equal(["library caves: its light_environment is dropped; the level's singletons come from library base, which has none."], plan.Warnings);
        Assert.Empty(plan.LibraryEntities);
        Assert.Empty(RoomPackCombiner.Plan([Source("caves", second)]).Warnings);
        Assert.Equal(NavSettings.DefaultStepHeight, RoomPackCombiner.NavOf([Source("base", first), Source("caves", second)])!.StepHeight);
        Assert.Equal(24, RoomPackCombiner.NavOf([Source("caves", second), Source("base", first)])!.StepHeight);
        Assert.Null(RoomPackCombiner.NavOf([]));
    }

    /// <summary>
    /// What the plan refuses: no library, a key that is not a key, a key
    /// given twice ignoring case, a library that does not split (named by
    /// its place), and libraries that do not fit together (17.5's text).
    /// </summary>
    [Fact]
    public void ThePlanRefusesWhatCannotBePacked()
    {
        VmfDocument library = RoomPropHarness.Library();
        Assert.Throws<ArgumentException>(() => RoomPackCombiner.Plan([]));
        Assert.Throws<ArgumentException>(() => RoomPackCombiner.Plan([Source("3x3", library)]));
        ArgumentException twice = Assert.Throws<ArgumentException>(() => RoomPackCombiner.Plan([Source("base", library), Source("BASE", library)]));
        Assert.StartsWith("the key BASE is given twice.", twice.Message, StringComparison.Ordinal);

        VmfDocument broken = new();
        broken.Chunks.Add(new VmfChunk("world"));
        RoomPackSplitException split = Assert.Throws<RoomPackSplitException>(() => RoomPackCombiner.Plan([Source("base", library), Source("caves", broken)]));
        Assert.Equal(1, split.Library);
        Assert.IsType<RoomLibraryException>(split.InnerException);
        Assert.StartsWith("the library has no info_room entity", split.Message, StringComparison.Ordinal);

        VmfDocument half = RoomHarness.LibraryVmf(new RoomDefinition("cross", 128, new SocketKit(64, 96, 16), [new RoomSocket(RoomFacing.PositiveX, "east")]));
        LinkException grid = Assert.Throws<LinkException>(() => RoomPackCombiner.Plan([Source("base", library), Source("caves", half)]));
        Assert.Equal(
            "libraries base (base.vmf) and caves (caves.vmf) are built for different grids: cell_size 256 against 128; the rooms of a level share one cell size.",
            grid.Message);
    }

    // ---- the worldspawn a room carries -------------------------------------------------

    /// <summary>
    /// The keys a room's world carries are its library's without the
    /// library's settings, the save counter at its room value; a room given
    /// another's keeps its brushes and every other chunk, and one without a
    /// world is refused.
    /// </summary>
    [Fact]
    public void ARoomCarriesItsLibrarysWorldspawnKeysOrAnothers()
    {
        VmfDocument library = RoomPropHarness.Library();
        VmfChunk world = library.GetChunk("world")!;
        world.AddKey(RoomLibraryOptions.EntityReserveKey, "300");
        world.AddKey(RoomLibraryOptions.MapVersionKey, "42");
        IReadOnlyList<KeyValuePair<string, string>> keys = RoomLibraryVmf.RoomWorldKeys(world);
        LibraryRoom room = RoomLibraryVmf.Split(library)[0];
        Assert.Equal(keys, [.. room.Document.GetChunk("world")!.Keys.Select(k => new KeyValuePair<string, string>(k.Name, k.Value))]);
        Assert.DoesNotContain(keys, k => k.Key == RoomLibraryOptions.EntityReserveKey);
        Assert.Equal(RoomLibraryOptions.RoomMapVersion, keys.Single(k => k.Key == RoomLibraryOptions.MapVersionKey).Value);

        LibraryRoom moved = RoomLibraryVmf.WithWorld(room, [new("classname", "worldspawn"), new("comment", "other")]);
        VmfChunk movedWorld = moved.Document.GetChunk("world")!;
        Assert.Equal(["classname", "comment"], movedWorld.Keys.Select(k => k.Name));
        Assert.Equal(room.Document.GetChunk("world")!.Chunks.Count(), movedWorld.Chunks.Count());
        Assert.Equal(room.Document.Chunks.Count, moved.Document.Chunks.Count);
        Assert.Equal(room.Definition, moved.Definition);
        Assert.NotNull(room.Document.GetChunk("world")!.GetValue("id"));

        VmfDocument worldless = new();
        worldless.Chunks.Add(new VmfChunk("entity"));
        Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.WithWorld(room with { Document = worldless }, keys));
    }

    // ---- name keys --------------------------------------------------------------------

    /// <summary>
    /// A room of a namespace compiles with its namespace's name keys, the
    /// run's being for the others; its cache key folds its own. The keys
    /// decide what a value means: under a name key, a value that reads like
    /// a resolved name is refused (5.2), and elsewhere it is plain text. A
    /// room with a namespace naming the key is refused as the same room is
    /// when the run names it, and compiles when neither does.
    /// </summary>
    [Fact]
    public async Task ANamespacesRoomCompilesWithItsOwnNameKeys()
    {
        HashSet<string> friend = new(StringComparer.OrdinalIgnoreCase) { "friend" };
        LibraryRoom plain = RoomLibraryVmf.Split(RoomPropHarness.Library(
            (0, RoomPropHarness.Entity("info_target", 901, new Vec3(190, 60, 40), ("friend", "c0r0_lamp")))))[0];
        LibraryRoom spaced = plain with { Namespace = new RoomNamespace("caves", friend) };
        Assert.Same(friend, spaced.NameKeysOr(null));
        Assert.Null((plain with { Namespace = new RoomNamespace("caves", null) }).NameKeysOr(friend));
        Assert.Same(friend, plain.NameKeysOr(friend));
        Assert.Null(plain.NameKeysOr(null));

        RoomCacheInputs inputs = new(VbspOptions.Default);
        Assert.Equal(RoomCacheKey.Of(plain, inputs with { NameKeys = friend }).OptionsDigest, RoomCacheKey.Of(spaced, inputs).OptionsDigest);
        Assert.NotEqual(RoomCacheKey.Of(plain, inputs).OptionsDigest, RoomCacheKey.Of(spaced, inputs).OptionsDigest);
        Assert.Equal(RoomCacheKey.Of(plain, inputs).OptionsDigest, RoomCacheKey.Of(plain, inputs).OptionsDigest);

        const string Refusal = "key \"friend\": the global name \"c0r0_lamp\" begins like a room-local or resolved name (c<column>r<row>_); rename it.";
        Assert.Contains(Refusal, await CompileAsync(plain, friend), StringComparison.Ordinal);
        Assert.Contains(Refusal, await CompileAsync(spaced, null), StringComparison.Ordinal);
        Assert.Contains(Refusal, await CompileAsync(spaced with { Namespace = new RoomNamespace("caves", friend) }, new HashSet<string>()), StringComparison.Ordinal);
        Assert.Equal("compiled", await CompileAsync(plain, null));
        Assert.Equal("compiled", await CompileAsync(plain with { Namespace = new RoomNamespace("caves", null) }, friend));
    }

    // ---- ids ----------------------------------------------------------------------------

    /// <summary>
    /// A combined pack's id follows every key, VMF, option and the
    /// navigation, in order, and is never the plain pack's; a level whose
    /// keys all come from one pack records that pack's id.
    /// </summary>
    [Fact]
    public void ACombinedPacksIdFollowsItsLibrariesInOrder()
    {
        string a = RoomPackNamespaces.Digest("a"u8), b = RoomPackNamespaces.Digest("b"u8);
        Guid id = RoomCompileIds.CombinedPackId([("base", a), ("caves", b)], ["-micro", "2"], "nav");
        Assert.Equal(id, RoomCompileIds.CombinedPackId([("base", a), ("caves", b)], ["-micro", "2"], "nav"));
        Assert.NotEqual(id, RoomCompileIds.CombinedPackId([("caves", b), ("base", a)], ["-micro", "2"], "nav"));
        Assert.NotEqual(id, RoomCompileIds.CombinedPackId([("base", a), ("halls", b)], ["-micro", "2"], "nav"));
        Assert.NotEqual(id, RoomCompileIds.CombinedPackId([("base", a), ("caves", a)], ["-micro", "2"], "nav"));
        Assert.NotEqual(id, RoomCompileIds.CombinedPackId([("base", a), ("caves", b)], [], "nav"));
        Assert.NotEqual(id, RoomCompileIds.CombinedPackId([("base", a), ("caves", b)], ["-micro", "2"], null));
        Assert.NotEqual(RoomCompileIds.PackId("a"u8, [], null), RoomCompileIds.CombinedPackId([("base", a)], [], null));

        Guid other = Guid.NewGuid();
        Assert.Equal(id, RoomCompileIds.LevelPackId([("base", id), ("caves", id)]));
        Guid? mixed = RoomCompileIds.LevelPackId([("base", id), ("caves", other)]);
        Assert.NotEqual(id, mixed);
        Assert.NotEqual(other, mixed);
    }

    // ---- the names a library holds rooms by -------------------------------------------

    /// <summary>
    /// A library lists the names it holds its rooms by, in the order added:
    /// a room's own, or the one it was added by; combining a level's
    /// libraries qualifies those names, so a namespace's room held by its
    /// name within the library is placed as <c>key.room</c>, not doubled.
    /// </summary>
    [Fact]
    public async Task ALibraryHoldsRoomsByTheNamesTheyWereAddedBy()
    {
        RoomObject hub = await RoomCompiler.CompileAsync(RoomHarness.BuildRoomModel(RoomPropHarness.Hub), RoomPropHarness.Hub, await RoomHarness.ContextAsync());
        RoomObject qualified = hub with { Definition = hub.Definition with { Name = "caves.hub" } };
        RoomLibrary plain = new(hub.Definition.Kit, hub.Definition.CellSize);
        plain.Add(hub);
        RoomLibrary spaced = new(hub.Definition.Kit, hub.Definition.CellSize);
        spaced.Add("hub", qualified, 0);
        Assert.Equal(["hub"], plain.Names);
        Assert.Equal(["hub"], spaced.Names);

        LevelGrid level = MultiLibraryHarness.Level("base.hub, caves.hub@90");
        LevelGrid resolved = LevelLibraries.Resolve(level, [["hub"], ["hub"]]);
        LevelLibrarySet set = LevelLibraries.Combine(resolved, [plain, spaced]);
        Assert.Equal(["base.hub", "caves.hub"], set.Rooms.Names);
        Assert.Same(qualified, set.Rooms.Get("caves.hub"));
        Assert.Equal(1, set.Rooms.SourceOf("caves.hub"));
    }

    private static RoomPackSource Source(string key, VmfDocument vmf) =>
        new(key, key + ".vmf", vmf, RoomPackNamespaces.Digest(vmf.ToBytes()));

    /// <summary>One room compiled as a library compile compiles it, with the run's name keys: "compiled", or why it failed.</summary>
    private static async Task<string> CompileAsync(LibraryRoom room, IReadOnlySet<string>? nameKeys)
    {
        VbspContextFiles files = await VbspContextFiles.CreateAsync();
        RoomLibraryCompileSettings settings = new(VbspOptions.Default, files.Content) { NameKeys = nameKeys };
        string? result = null;
        await RoomLibraryCompiler.CompileAsync([room], settings, (outcome, _) =>
        {
            result = outcome.Compiled is not null ? "compiled" : outcome.Error!.Message;
            return ValueTask.CompletedTask;
        });
        return result!;
    }

    /// <summary>The harness's content for a library compile.</summary>
    private sealed record VbspContextFiles(SourceSharp.MapTools.Io.IContentFileSystem Content)
    {
        public static async Task<VbspContextFiles> CreateAsync() =>
            new((await RoomPropHarness.ContextAsync("unused")).Content);
    }
}
