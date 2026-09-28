//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomNamingFacts;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Names through the whole pipeline: rooms compiled from a library with
/// local names, flags, a hub, <c>room_needs</c> and foldable logic, linked,
/// and the same level flattened and compiled whole, in both emission modes
/// at every turn: the two maps carry the same entities with the same keys
/// (the design's one-resolver rule, section 5.9). Also the pack's name
/// sections, the budget of the level the resolver wrote, and the rule's
/// refusal at room compile time.
/// </summary>
public class LevelLinkerNamingTests
{
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

    private static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    /// <summary>The hub room's library with its names: every mechanism once, all point entities.</summary>
    private static VmfDocument NamingLibrary()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        int id = 900000;
        void Add(string classname, (string Key, string Value)[] keys, params (string Output, string Value)[] outputs)
        {
            VmfChunk entity = new(MapFileLoader.EntityChunk);
            entity.AddKey("id", (id++).ToString(CultureInfo.InvariantCulture));
            entity.AddKey("classname", classname);
            foreach ((string key, string value) in keys)
            {
                entity.AddKey(key, value);
            }

            if (outputs.Length > 0)
            {
                VmfChunk connections = entity.AddChunk(MapFileLoader.ConnectionsChunk);
                foreach ((string output, string value) in outputs)
                {
                    connections.AddKey(output, value);
                }
            }

            library.Chunks.Add(entity);
        }

        Add("logic_auto", [("origin", "40 40 40"), ("spawnflags", "1")],
            ("OnMapSpawn", Out("cxry_relay", "Trigger", "", "1")), ("OnMapSpawn", Out("cxry_room", "Trigger1")),
            ("OnMapSpawn", Out("cxry_room", "TestNorth")), ("OnMapSpawn", Out("cxry_state", "Trigger")));
        Add("logic_relay", [("origin", "50 40 40"), ("targetname", "cxry_relay"), ("spawnflags", "2")],
            ("OnTrigger", Out("cx+1ry_door", "Use", "", "0.5")), ("OnTrigger", Out("cxry_has_east", "Test")));
        Add("logic_relay", [("origin", "60 40 40"), ("targetname", "cxry_state"), ("spawnflags", "1")],
            ("OnTrigger", Out("cxry_door", "Use")));
        Add("logic_branch", [("origin", "70 40 40"), ("targetname", "cxry_has_east")],
            ("OnTrue", Out("cxry_door", "Enable")), ("OnFalse", Out("cxry_door", "Disable", "", "2")));
        Add("logic_room", [("origin", "80 40 40"), ("targetname", "cxry_room")],
            ("OnTrigger1", Out("cxry_door", "Toggle")), ("OnNorthTrue", Out("cxry_sign", "Use")));
        Add("info_target", [("origin", "90 40 40"), ("targetname", "cxry_door")]);
        Add("info_target", [("origin", "100 40 40"), ("targetname", "cxry_sign"), ("room_needs", "north")]);
        Add("info_target", [("origin", "110 40 40"), ("targetname", "cxry_arm"), ("parentname", "cx-1ry_door")]);
        return library;
    }

    /// <summary>
    /// The link and the flattened level's whole-map compile carry the same
    /// entities, key for key and in order (the entities' positions compared
    /// as numbers, the hammer ids left out), in both modes at every turn, and
    /// the mode is on the worldspawn only with the flag.
    /// </summary>
    [Theory]
    [MemberData(nameof(TurnsAndModes))]
    public async Task TheLinkAndTheFlattenAgree(int rotation, bool mod)
    {
        VmfDocument library = NamingLibrary();
        (LinkedLevel linked, BspData whole, FlattenedLevel flat) = await LinkAndFlattenAsync(library, mod, $"hub@{rotation}, hub");
        List<BspEntity> a = EntityLump.Parse(linked.Bsp[BspLump.Entities]);
        List<BspEntity> b = EntityLump.Parse(whole[BspLump.Entities]);
        Assert.Equal(Comparable(b.Skip(1)), Comparable(a.Skip(1)));
        Assert.Equal(mod ? "mod" : null, a[0].Get(ModEntityContract.EntitiesKey));
        Assert.Equal(mod ? "mod" : null, b[0].Get(ModEntityContract.EntitiesKey));
        Assert.Equal(mod ? "1" : null, a[0].Get(ModEntityContract.VersionKey));
        Assert.Equal(linked.NameWarnings, flat.Warnings);

        // The two placements' names are their cells; the hub is there only with the flag.
        Assert.Contains(a, e => e.Get("targetname") == "c0r0_door");
        Assert.Contains(a, e => e.Get("targetname") == "c1r0_door");
        Assert.Equal(mod ? 2 : 0, a.Count(e => e.ClassName == LogicRoom.ClassName));
        Assert.DoesNotContain(a, e => e.Get("room_needs") is not null);
    }

    /// <summary>
    /// A link is a function of its level in both modes: the same bytes on one
    /// thread and on four, run after run.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALinkWithNamesIsTheSameAtAnyThreadCount(bool mod)
    {
        RoomLibrary compiled = await CompileAsync(NamingLibrary());
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub@270, hub@90", "hub, hub@180"), "names");
        byte[] first = await LinkBytesAsync(level, compiled, mod, degree: 1);
        foreach (int degree in new[] { 4, 1, 4 })
        {
            Assert.Equal(first, await LinkBytesAsync(level, compiled, mod, degree));
        }
    }

    /// <summary>
    /// The design's correctness facts 7 and 8, their naming half: a room
    /// placed twice with a local name, and with a named switchable light,
    /// gives each placement its own name (before names, both copies carried
    /// one name and an output fired both).
    /// </summary>
    [Fact]
    public async Task ARoomPlacedTwiceNamesEachCopyApart()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk light = new(MapFileLoader.EntityChunk);
        light.AddKey("id", "5");
        light.AddKey("classname", "light");
        light.AddKey("origin", "40 40 40");
        light.AddKey("targetname", "cxry_lamp");
        light.AddKey("style", "0");
        library.Chunks.Add(light);
        (LinkedLevel linked, BspData whole, _) = await LinkAndFlattenAsync(library, false, "hub, hub");
        foreach (BspData map in new[] { linked.Bsp, whole })
        {
            Assert.Equal(
                ["c0r0_lamp", "c1r0_lamp"],
                EntityLump.Parse(map[BspLump.Entities]).Where(e => e.ClassName == "light").Select(e => e.Get("targetname")).Order(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// What the (a) warnings say for the level, and that the link's budget is
    /// the level it wrote: the entities in the linked lump are the report's
    /// entity list, and its edicts are what the class table makes of them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheBudgetIsTheLevelTheResolverWrote(bool mod)
    {
        (LinkedLevel linked, _, _) = await LinkAndFlattenAsync(NamingLibrary(), mod, "hub, hub");
        List<BspEntity> lump = EntityLump.Parse(linked.Bsp[BspLump.Entities]);
        LevelEntityReport budget = linked.EntityBudget!;
        Assert.Equal(lump.Count, budget.Listed);
        EntityClassTable table = EntityClassTable.Default;
        Assert.Equal(1 + lump.Skip(1).Count(e => table.Classify(e.ClassName) is EntityCost.Edict or EntityCost.SpawnTransient), budget.Edicts);

        // The east room's cx+1ry_door, and the west room's cx-1ry_door, name empty cells.
        Assert.Equal(
            [
                "room hub at cell (0, 0): entity c0r0_arm (info_target) key \"parentname\" names c-1r0_door, but cell (-1, 0) is off the grid; the key was cleared.",
                "room hub at cell (1, 0): entity c1r0_relay (logic_relay) key \"OnTrigger\" names c2r0_door, but cell (2, 0) is off the grid; the output was removed.",
            ],
            linked.NameWarnings);
    }

    /// <summary>
    /// The names ride in the pack, one section per turn, and a link from the
    /// pack writes the same bytes as a link of the rooms in memory; a pack
    /// whose name sections are of an unknown revision reads them as absent
    /// and links to the same bytes too.
    /// </summary>
    [Fact]
    public async Task APackedRoomsNamesLinkAsTheRoomsOwn()
    {
        RoomLibrary compiled = await CompileAsync(NamingLibrary());
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub@90, hub"), "names");
        byte[] direct = await LinkBytesAsync(level, compiled, true);

        RoomObject hub = compiled.Get("hub");
        RoomPackItem item = await RoomPackItem.CreateAsync(hub);
        Assert.Equal(["NAM0", "NAM1", "NAM2", "NAM3"], item.Extra.Select(s => s.Tag).Where(t => t.StartsWith("NAM", StringComparison.Ordinal)));
        Assert.Equal(await LinkBytesAsync(level, await LoadAsync(item), true), direct);

        // Revision 99: the names are read from the room's lump instead, to the same bytes.
        RoomPackItem stale = item with
        {
            Extra = [.. item.Extra.Select(s => s.Tag.StartsWith("NAM", StringComparison.Ordinal) ? Stale(s) : s)],
        };
        RoomLibrary reread = await LoadAsync(stale);
        Assert.Null(reread.Get("hub").Names);
        Assert.NotNull((await LoadAsync(item)).Get("hub").Names);
        Assert.Equal(await LinkBytesAsync(level, reread, true), direct);

        static RoomPackSectionData Stale(RoomPackSectionData section)
        {
            byte[] bytes = section.Bytes.ToArray();
            bytes[12] = 99;
            return new RoomPackSectionData(section.Tag, bytes);
        }
    }

    /// <summary>The names summary <c>ssmap rooms</c> lists, read from the pack.</summary>
    [Fact]
    public async Task ThePackListsARoomsNames()
    {
        RoomLibrary compiled = await CompileAsync(NamingLibrary());
        using MemoryStream pack = new();
        await RoomPack.SaveAsync([await RoomPackItem.CreateAsync(compiled.Get("hub"))], pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        RoomNameSummary summary = (await RoomPack.ReadNameSummariesAsync(pack, index))["hub"];
        Assert.Equal(["cxry_arm", "cxry_door", "cxry_relay", "cxry_sign", "cxry_state"], summary.LocalNames);
        Assert.Equal(["cxry_has_east", "cxry_room"], summary.LinkerNames);
        Assert.Equal(
            new Dictionary<RoomDirection, int> { [RoomDirection.East] = 1, [RoomDirection.West] = 1 },
            summary.NeighbourReferences);
        Assert.Equal(["north"], summary.Needs);
        Assert.Empty(summary.Warnings);
        Assert.False(summary.IsEmpty);
        Assert.Equal(
            "  local names: cxry_arm, cxry_door, cxry_relay, cxry_sign, cxry_state\n"
            + "  neighbour references: east (1), west (1); dropped with a warning where the level leaves that cell empty\n"
            + "  flags and hub: cxry_has_east, cxry_room\n"
            + "  room_needs: north\n",
            summary.Describe());
        Assert.Equal(1 + 20, summary.WrittenEdictsBound(modEntities: false));
        Assert.Equal(1, summary.WrittenEdictsBound(modEntities: true));
    }

    /// <summary>The rule runs at room compile time on the room's VMF: a malformed name stops the room with the design's message.</summary>
    [Fact]
    public async Task TheRoomCompileRefusesABadName()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", "5");
        entity.AddKey("classname", "info_target");
        entity.AddKey("origin", "40 40 40");
        entity.AddKey("targetname", "CXRY_door");
        library.Chunks.Add(entity);
        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => CompileAsync(library));
        Assert.StartsWith("room hub: entity 5 (info_target) key \"targetname\": \"CXRY_door\" is a malformed room-local name;", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With <c>room_needs</c> on a static prop that casts no shadow, the
    /// flatten leaves the prop out of the placement whose condition fails
    /// (the link refuses static props until they are linked).
    /// </summary>
    [Fact]
    public void TheFlattenDropsAConditionalStaticProp()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk prop = new(MapFileLoader.EntityChunk);
        prop.AddKey("id", "5");
        prop.AddKey("classname", "prop_static");
        prop.AddKey("origin", "40 40 40");
        prop.AddKey("model", "models/crate.mdl");
        prop.AddKey("disableshadows", "1");
        prop.AddKey("room_needs", "!west");
        library.Chunks.Add(prop);

        VmfDocument flat = LevelFlattener.Flatten(LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub, hub"), "props"), library);
        VmfChunk kept = Assert.Single(flat.GetChunks(MapFileLoader.EntityChunk), e => e.GetValue("classname") == "prop_static");
        Assert.Null(kept.GetValue("room_needs"));
        Assert.StartsWith("40 ", kept.GetValue("origin"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A level whose rooms use no names, flattened without the flag, is
    /// written as before names existed (the chunks untouched, no worldspawn
    /// key); with the flag the worldspawn records the mode.
    /// </summary>
    [Fact]
    public void AFlattenWithoutNamesIsUnchanged()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub, hub"), "plain");
        FlattenedLevel stock = LevelFlattener.FlattenLevel(level, library, new LevelFlattenOptions());
        Assert.Null(stock.Vmf.GetChunk(MapFileLoader.WorldChunk)!.GetValue(ModEntityContract.EntitiesKey));
        Assert.Equal(LevelFlattener.Flatten(level, library).ToBytes(), stock.Vmf.ToBytes());
        FlattenedLevel mod = LevelFlattener.FlattenLevel(level, library, new LevelFlattenOptions { ModEntities = true });
        Assert.Equal("mod", mod.Vmf.GetChunk(MapFileLoader.WorldChunk)!.GetValue(ModEntityContract.EntitiesKey));
        Assert.Empty(mod.Warnings);
    }

    /// <summary>
    /// The library's settings reach the resolver: <c>rooms_fold_logic 0</c>
    /// keeps the foldable relay in the link and in the flatten, and a library
    /// key the settings name is kept out of the flattened worldspawn.
    /// </summary>
    [Fact]
    public async Task TheLibraryCanTurnTheFoldOff()
    {
        VmfDocument library = NamingLibrary();
        library.GetChunk(MapFileLoader.WorldChunk)!.AddKey(RoomLibraryOptions.FoldLogicKey, "0");
        FlattenedLevel flat = LevelFlattener.FlattenLevel(LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub, hub"), "nofold"), library, new LevelFlattenOptions());
        Assert.Contains(flat.Vmf.GetChunks(MapFileLoader.EntityChunk), e => e.GetValue("targetname") == "c0r0_relay");
        Assert.Null(flat.Vmf.GetChunk(MapFileLoader.WorldChunk)!.GetValue(RoomLibraryOptions.FoldLogicKey));

        RoomLibrary compiled = await CompileAsync(NamingLibrary());
        compiled.Options = new RoomLibraryOptions { FoldLogic = false };
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub, hub"), "nofold");
        LevelLayout layout = level.ToLayout(name => compiled.Find(name)?.Definition, compiled.CellSize, compiled.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, compiled, await RoomHarness.ContextAsync());
        Assert.Contains(EntityLump.Parse(linked.Bsp[BspLump.Entities]), e => e.Get("targetname") == "c0r0_relay");
    }

    // ---- helpers ----------------------------------------------------------------

    /// <summary>The entities as comparable text: every key but the hammer id, in order, positions as numbers.</summary>
    private static List<string> Comparable(IEnumerable<BspEntity> entities) =>
        [.. entities.Select(e => string.Join(" | ", e.Pairs
            .Where(p => p.Key != "hammerid")
            .Select(p => p.Key == "origin" ? $"origin={Position(p.Value)}" : $"{p.Key}={p.Value}")))];

    private static string Position(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.Parse(v, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture)));

    private static async Task<RoomLibrary> CompileAsync(VmfDocument library)
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        RoomLibrary compiled = new(rooms[0].Definition.Kit, rooms[0].Definition.CellSize);
        foreach (LibraryRoom room in rooms)
        {
            VbspContext context = await RoomHarness.ContextAsync();
            context.MapBase = room.Definition.Name;
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, context));
        }

        return compiled;
    }

    private static async Task<(LinkedLevel Linked, BspData Whole, FlattenedLevel Flat)> LinkAndFlattenAsync(VmfDocument library, bool mod, params string[] rows)
    {
        RoomLibrary compiled = await CompileAsync(library);
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", rows), "names");
        LevelLayout layout = level.ToLayout(name => compiled.Find(name)?.Definition, compiled.CellSize, compiled.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(
            layout, compiled, await RoomHarness.ContextAsync(), new LevelLinkOptions { ModEntities = mod });
        FlattenedLevel flat = LevelFlattener.FlattenLevel(level, library, new LevelFlattenOptions { ModEntities = mod });
        VbspResult whole = await RoomHarness.CompileAsync(flat.Vmf, await RoomHarness.ContextAsync());
        Assert.NotNull(whole.Bsp);
        return (linked, whole.Bsp!, flat);
    }

    private static async Task<byte[]> LinkBytesAsync(LevelGrid level, RoomLibrary library, bool mod, int degree = 1)
    {
        LevelLayout layout = level.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
        VbspContext context = await RoomHarness.ContextAsync();
        context.Parallelism = new SourceSharp.MapTools.Parallel.CompileParallelism { MaxDegree = degree };
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, library, context, new LevelLinkOptions { ModEntities = mod });
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }

    private static async Task<RoomLibrary> LoadAsync(RoomPackItem item)
    {
        using MemoryStream pack = new();
        await RoomPack.SaveAsync([item], pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        RoomObject room = (await RoomPack.LoadRoomsAsync(pack, index, [new RoomPackRequest("hub", [0, 1])]))[0];
        RoomLibrary library = new(room.Definition.Kit, room.Definition.CellSize);
        library.Add(room);
        return library;
    }
}
