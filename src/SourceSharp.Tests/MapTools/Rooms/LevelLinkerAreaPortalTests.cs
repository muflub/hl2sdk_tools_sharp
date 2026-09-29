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
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomAreaPortalHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Area portals and areas through the link (section 4.11 of the rooms
/// design): a room's own areas are joined to its neighbours' at every
/// joint, its portals are listed for the level with their numbers rebased,
/// and the level's areas and portals agree with the flattened level's vbsp
/// compile at every quarter turn.
/// </summary>
public sealed class LevelLinkerAreaPortalTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// A line of three rooms, the split room between two hubs, along the
    /// split room's sockets at its turn: a row for 0 and 180 degrees, a
    /// column for 90 and 270.
    /// </summary>
    private static LevelGrid Line(int rotation) => rotation % 180 == 0
        ? RoomPropHarness.Level($"hub, split@{rotation}, hub")
        : RoomPropHarness.Level("hub", $"split@{rotation}", "hub");

    // ---- placement, turned -------------------------------------------------------------------

    /// <summary>
    /// The split room between two hubs, at every quarter turn: the level
    /// has two areas (each hub joined to the half of the split room its
    /// doorway opens onto) and the one portal between them, listed from
    /// both sides with its key 1; the linked and the flattened maps make the
    /// same partition of the open space into areas, list the same portals
    /// (outline and normal) between the same areas, each map's portal on a
    /// face of the portal's brush (which face follows each compile's flood
    /// order), and number the portal's entity alike; and every node the
    /// link says is in one area holds only that area's leaves.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelHoldsItsRoomsAreasAndPortalsAtEveryRotation(int rotation)
    {
        VmfDocument library = Library();
        LevelGrid level = Line(rotation);
        LinkedLevel linked = await LinkAsync(await CompileAsync(library), level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(3, Areas(linked.Bsp).Length);
        Assert.Equal(3, Listings(linked.Bsp).Length);
        Assert.Equal(Areas(flat).Length, Areas(linked.Bsp).Length);
        Dictionary<int, int> names = SamePartition(linked.Bsp, flat, level);
        Assert.Equal(2, names.Count);
        Assert.Equal(Portals(flat), Portals(linked.Bsp, names));
        Box[] brushes = [.. linked.Plan.Layout.Rooms.Where(r => r.Placement.Room == "split").Select(r => Placed(Doorway, r.Placement))];
        OnPortalFace(linked.Bsp, brushes);
        OnPortalFace(flat, brushes);
        Assert.Equal([1], PortalNumbers(linked.Bsp));
        Assert.Equal(PortalNumbers(flat), PortalNumbers(linked.Bsp));
        NodeAreasHold(linked.Bsp);
        Assert.Empty(linked.AreaWarnings);
    }

    /// <summary>
    /// Two split rooms in a row between hubs, the second turned half round,
    /// and the same with window portals: three areas and two portals; the
    /// second placement's portal is numbered 2 in its entity and its
    /// listings; and link and flatten agree as above.
    /// </summary>
    [Theory]
    [InlineData("func_areaportal")]
    [InlineData("func_areaportalwindow")]
    public async Task EachPlacementsPortalsAreNumberedAfterTheLastOnes(string classname)
    {
        VmfDocument library = Library(portal: false);
        library.Chunks.Add(VmfPlacement.MoveEntity(Portal(PortalId, Doorway, classname), QuarterTurn.Translation(Corner(1))));
        LevelGrid level = RoomPropHarness.Level("hub, split, split@180, hub");
        LinkedLevel linked = await LinkAsync(await CompileAsync(library), level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(4, Areas(linked.Bsp).Length);
        Assert.Equal(5, Listings(linked.Bsp).Length);
        Assert.Equal([1, 2], PortalNumbers(linked.Bsp));
        Assert.Equal(PortalNumbers(flat), PortalNumbers(linked.Bsp));
        Assert.Equal([1, 1, 2, 2], Listings(linked.Bsp).Skip(1).Select(l => (int)l.PortalKey).Order());
        Dictionary<int, int> names = SamePartition(linked.Bsp, flat, level);
        Assert.Equal(3, names.Count);
        Assert.Equal(Portals(flat), Portals(linked.Bsp, names));
        NodeAreasHold(linked.Bsp);
    }

    /// <summary>
    /// A level without an area portal keeps the one open area it always
    /// had: the first room's two area lumps byte for byte, no clip vertex
    /// lump, nothing to warn of; the split room's inner wall without its
    /// portal is only a wall.
    /// </summary>
    [Fact]
    public async Task ALevelWithoutAreaPortalsKeepsItsOneArea()
    {
        RoomLibrary rooms = await CompileAsync(Library(portal: false));
        Assert.Null(rooms.Get("split").AreaPortals);
        LinkedLevel linked = await LinkAsync(rooms, Line(0));
        Assert.Equal(rooms.Get("hub").Bsp[BspLump.Areas].Data.ToArray(), linked.Bsp[BspLump.Areas].Data.ToArray());
        Assert.Equal(rooms.Get("hub").Bsp[BspLump.AreaPortals].Data.ToArray(), linked.Bsp[BspLump.AreaPortals].Data.ToArray());
        Assert.Equal(0, linked.Bsp[BspLump.ClipPortalVerts].Length);
        Assert.Empty(linked.AreaWarnings);
        Assert.All(BspStructView.As<DLeaf>(linked.Bsp[BspLump.Leafs]).ToArray(), leaf => Assert.True(leaf.GetArea() <= 1));
    }

    /// <summary>
    /// A ring of rooms around the split room joins its portal's two sides
    /// into one area: the level has one area and lists no portal, keeps the
    /// portal's entity and number, and warns naming the room, its cell and
    /// the portal, as the flattened level's vbsp keeps the entity, lists no
    /// portal and makes the same one area.
    /// </summary>
    [Fact]
    public async Task APortalTheLevelJoinsAroundSealsNothingAndIsReported()
    {
        VmfDocument library = Library();
        LevelGrid level = RoomPropHarness.Level("hub, hub, hub", "hub, split, hub");
        LinkedLevel linked = await LinkAsync(await CompileAsync(library), level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(2, Areas(linked.Bsp).Length);
        Assert.Single(Listings(linked.Bsp));
        Assert.Equal(0, linked.Bsp[BspLump.ClipPortalVerts].Length);
        Assert.Equal([1], PortalNumbers(linked.Bsp));
        Assert.Equal(PortalNumbers(flat), PortalNumbers(linked.Bsp));
        Assert.Equal(Areas(flat).Length, Areas(linked.Bsp).Length);
        Assert.Equal(Listings(flat).Length, Listings(linked.Bsp).Length);
        Assert.Single(SamePartition(linked.Bsp, flat, level));
        Assert.Equal(
            ["room split at cell (1, 0): area portal 1 has one area on both sides once the level joins the rooms around it; the level keeps its entity but lists no portal for it."],
            linked.AreaWarnings);
    }

    /// <summary>
    /// An occluder in one half of the split room is in that half's level
    /// area, the one the flattened compile puts it in (through the
    /// partition's renaming).
    /// </summary>
    [Fact]
    public async Task AnOccludersAreaIsItsLevelArea()
    {
        VmfChunk occluder = new(MapFileLoader.EntityChunk);
        occluder.AddKey("id", "700300");
        occluder.AddKey("classname", "func_occluder");
        occluder.AddKey("StartActive", "1");
        occluder.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(180, 40, 16), new Vec3(196, 100, 120), 70300));
        VmfDocument library = Library(true, (1, occluder));
        LevelGrid level = Line(0);
        LinkedLevel linked = await LinkAsync(await CompileAsync(library), level);
        BspData flat = await CompileFlatAsync(library, level);
        Dictionary<int, int> names = SamePartition(linked.Bsp, flat, level);

        DOccluderData mine = Assert.Single(OcclusionLump.Read(linked.Bsp[BspLump.Occlusion]).Occluders);
        DOccluderData theirs = Assert.Single(OcclusionLump.Read(flat[BspLump.Occlusion]).Occluders);
        int east = RoomHarness.LeafAt(linked.Bsp, new Vec3(256 + 220, 128, 60)).GetArea();
        Assert.Equal(east, mine.Area);
        Assert.Equal(theirs.Area, names[mine.Area]);
    }

    /// <summary>
    /// A portal does not cut the linked PVS (the relation to door
    /// visibility, 4.11 and Q3): vvis sees through an area portal, the
    /// engine closing it at runtime, so a line from the west hub through
    /// both doorways into the split room's east half is kept by the link,
    /// as vvis on the flattened level keeps it.
    /// </summary>
    [Fact]
    public async Task APortalDoesNotCutThePvs()
    {
        VmfDocument library = Library();
        LevelGrid level = Line(0);
        LinkedLevel linked = await LinkAsync(await CompileAsync(library), level);
        Vec3 west = new(128, 128, 60);
        Vec3 east = new(256 + 220, 128, 60);
        short from = RoomHarness.LeafAt(linked.Bsp, west).Cluster;
        short to = RoomHarness.LeafAt(linked.Bsp, east).Cluster;
        Assert.NotEqual(from, to);
        Assert.True(Sees(linked.Vis, from, to));
        Assert.True(Sees(linked.Vis, to, from));

        static bool Sees(SourceSharp.MapTools.Vis.VisResult vis, int a, int b) => (vis.Pvs(a)[b >> 3] & (1 << (b & 7))) != 0;
    }

    // ---- refusals ----------------------------------------------------------------------------

    /// <summary>
    /// An area portal in a socket's plug box is refused with the rooms
    /// design's text (15.4, the 4.11 socket row) by the split, so by the pack
    /// and the flatten, and by a room compile given the room's VMF.
    /// </summary>
    [Fact]
    public async Task APortalInAPlugBoxIsRefusedEverywhere()
    {
        Box inPlug = new(new Vec3(236, 100, 16), new Vec3(248, 156, 120));
        VmfDocument library = Library(true, (0, Portal(700200, inPlug)));
        const string Message = "room hub: func_areaportal 700200 lies in socket \"east\"'s plug box.";
        Assert.Equal(Message, Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library)).Message);
        Assert.Equal(Message, Assert.Throws<RoomLibraryException>(() => LevelFlattener.Flatten(Line(0), library)).Message);

        VmfDocument room = RoomHarness.BuildRoomModel(RoomPropHarness.Hub);
        room.Chunks.Add(Portal(700200, inPlug));
        Assert.Equal(
            Message,
            (await Assert.ThrowsAsync<RoomLintException>(
                async () => await RoomCompiler.CompileAsync(room, RoomPropHarness.Hub, await ContextAsync("hub")))).Message);
    }

    /// <summary>An area portal named as socket furniture is refused by the split and the flatten.</summary>
    [Fact]
    public void APortalAsSocketFurnitureIsRefused()
    {
        VmfDocument library = Library(portal: false);
        library.Chunks.Add(VmfPlacement.MoveEntity(Portal(PortalId, Doorway, keys: ("room_socket", "east")), QuarterTurn.Translation(Corner(1))));
        const string Message = "room split: func_areaportal 700100 has room_socket; an area portal is built into its room's world and cannot be socket furniture.";
        Assert.Equal(Message, Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library)).Message);
        Assert.Equal(Message, Assert.Throws<RoomLibraryException>(() => LevelFlattener.Flatten(Line(0), library)).Message);
    }

    /// <summary>
    /// <c>room_needs</c> on an area portal is refused by the room compile
    /// and the flatten with a message of its own: its brush is the room's
    /// world, and dropping it would renumber every later portal.
    /// </summary>
    [Fact]
    public async Task RoomNeedsOnAPortalIsRefused()
    {
        VmfDocument library = Library(portal: false);
        library.Chunks.Add(VmfPlacement.MoveEntity(Portal(PortalId, Doorway, keys: ("room_needs", "east")), QuarterTurn.Translation(Corner(1))));
        const string Message = "room split: entity 700100 (func_areaportal) has room_needs, but an area portal is built into its room's compile and cannot be dropped.";
        Assert.Equal(Message, (await Assert.ThrowsAsync<RoomLintException>(() => CompileAsync(library))).Message);
        Assert.Equal(Message, Assert.Throws<RoomLintException>(() => LevelFlattener.Flatten(Line(0), library)).Message);
    }

    /// <summary>
    /// A room whose lumps have area portals but that carries no area portal
    /// data from its compile (a pack written before the link carried them)
    /// is refused naming the room and what to do; the old refusal of every
    /// area portal is gone.
    /// </summary>
    [Fact]
    public async Task ARoomWithPortalsButNoPortalDataIsRefused()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        RoomLibrary stale = RoomPropHarness.RoomsOf(rooms.Get("hub"), rooms.Get("split") with { AreaPortals = null });
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(stale, Line(0)));
        Assert.Equal(
            "room split has 1 area portals but no area portal data from its compile (a pack written before the link carried"
            + " area portals, or a room built without ssmap room); recompile the library with ssmap room.",
            refused.Message);
        Assert.DoesNotContain("a linkable room has no area portal", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A level with more areas than a map holds is refused naming the room
    /// and cell whose area crossed <c>MAX_MAP_AREAS</c>: seventeen rows of a
    /// hub and fifteen split rooms, the hubs joined in a column, bring
    /// 1 + 15 areas a row, and the last split room of the last row brings
    /// the 256th.
    /// </summary>
    [Fact]
    public async Task ALevelPastTheAreaCapIsRefused()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        string row = "hub" + string.Concat(Enumerable.Repeat(", split", 15));
        LevelGrid level = RoomPropHarness.Level([.. Enumerable.Repeat(row, 17)]);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkAsync(rooms, level));
        Assert.Equal("room split at cell (15, 16) pushes the link to 257 areas; the engine loads at most 256 (MAX_MAP_AREAS).", refused.Message);

        LevelGrid fits = RoomPropHarness.Level([.. Enumerable.Repeat(row, 16)]);
        Assert.Equal(242, Areas((await LinkAsync(rooms, fits)).Bsp).Length);
    }

    // ---- the pack, determinism, the budget, the loader ---------------------------------------

    /// <summary>
    /// Rooms with area portals through a pack: the pack stores a room's area
    /// portal data (four turns), and none for a room without, the rooms it
    /// loads carry it, and the level links to the same bytes as from the
    /// rooms in memory; a pack holding only turn 0 (the link turning the
    /// vertices) links to the same bytes too (the rooms design, 15.9).
    /// </summary>
    [Fact]
    public async Task AreaPortalsRoundTripThroughAPack()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        LevelGrid level = RoomPropHarness.Level("hub", "split@90", "hub@180", "split@270", "hub");
        byte[] expected = await BytesAsync(await LinkAsync(rooms, level));

        using MemoryStream pack = new();
        List<RoomPackItem> items = [];
        foreach (RoomObject room in rooms.Rooms)
        {
            items.Add(await RoomPackItem.CreateAsync(room));
        }

        await RoomPack.SaveAsync(items, pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        Assert.NotNull(index.Find("split")!.Find(RoomAreaPortals.SectionTag));
        Assert.Null(index.Find("hub")!.Find(RoomAreaPortals.SectionTag));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            pack, index, [new RoomPackRequest("hub", [0, 2]), new RoomPackRequest("split", [1, 3])]);
        Assert.Null(loaded[0].AreaPortalsOfCompile);
        Assert.Equal(4, loaded[1].AreaPortalsOfCompile!.TurnCount);
        Assert.Equal(expected, await BytesAsync(await LinkAsync(RoomPropHarness.RoomsOf([.. loaded]), level)));

        RoomLibrary once = RoomPropHarness.RoomsOf(
            [.. rooms.Rooms.Select(r => r.AreaPortals is { } a ? r with { AreaPortals = a.WithTurnZeroOnly() } : r)]);
        Assert.Equal(1, once.Get("split").AreaPortalsOfCompile!.TurnCount);
        Assert.Equal(expected, await BytesAsync(await LinkAsync(once, level)));
    }

    /// <summary>
    /// A pack of rooms with area portals is the same bytes whether its rooms
    /// compiled on one thread or on four, and a level with them links to the
    /// same bytes at one thread and at many, run after run (15.5).
    /// </summary>
    [Fact]
    public async Task AreaPortalsAreTheSameBytesAtAnyThreadCount()
    {
        async Task<byte[]> PackAsync(int degree)
        {
            RoomLibrary rooms = await CompileAsync(Library(), degree);
            List<RoomPackItem> items = [];
            foreach (string name in new[] { "hub", "split" })
            {
                items.Add(await RoomPackItem.CreateAsync(rooms.Find(name)!));
            }

            using MemoryStream pack = new();
            await RoomPack.SaveAsync(items, pack);
            return pack.ToArray();
        }

        byte[] serial = await PackAsync(1);
        Assert.Equal(serial, await PackAsync(4));

        RoomLibrary library = await CompileAsync(Library());
        LevelGrid level = RoomPropHarness.Level("hub, split, split@180, hub", "hub, hub, hub, hub");
        byte[] one = await BytesAsync(await LinkAsync(library, level, 1));
        Assert.Equal(one, await BytesAsync(await LinkAsync(library, level, 8)));
        Assert.Equal(one, await BytesAsync(await LinkAsync(library, level, 1)));
    }

    /// <summary>
    /// An area portal costs the level its one entity per placement (the
    /// rooms design, 6.9 and 15.6: <c>func_areaportal</c> is kept by vbsp),
    /// and the link's budget counts exactly the entities its lump holds.
    /// </summary>
    [Fact]
    public async Task AnAreaPortalCostsOneEntityPerPlacement()
    {
        LevelGrid level = RoomPropHarness.Level("hub, split, split@180, hub");
        LinkedLevel bare = await LinkAsync(await CompileAsync(Library(portal: false)), level);
        LinkedLevel portals = await LinkAsync(await CompileAsync(Library()), level);
        Assert.Equal(bare.EntityBudget!.Edicts + 2, portals.EntityBudget!.Edicts);
        Assert.Equal(bare.EntityBudget.Listed + 2, portals.EntityBudget.Listed);
        Assert.Equal(EntityLump.Parse(portals.Bsp[BspLump.Entities]).Count, portals.EntityBudget.Listed);
    }

    /// <summary>
    /// A lit library (the base bake, PR 9) with an area portal: each room
    /// baked as <c>ssmap room</c> bakes it (vrad reads through the portal,
    /// which only the engine closes), and the level links with the areas
    /// and portals of the same level unlit, its leaves' flags set by the
    /// link's sky pass on top of their areas, and passes the loader checks.
    /// </summary>
    [Fact]
    public async Task ALitLevelWithAnAreaPortalLinks()
    {
        VmfDocument library = Library(
            true,
            (0, RoomLightHarness.Light(700400, new Vec3(128, 128, 200))),
            (1, RoomLightHarness.Light(700401, new Vec3(200, 128, 200))));
        RoomLightHarness.WorldAlign(library);
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary lit = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize) { LibraryEntities = split.LibraryEntities, Options = split.Options };
        RoomLibrary unlit = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize) { LibraryEntities = split.LibraryEntities, Options = split.Options };
        foreach (LibraryRoom room in split.Rooms)
        {
            SourceSharp.MapTools.Bsp.VbspContext context = await ContextAsync(room.Definition.Name);
            RoomObject compiled = await RoomCompiler.CompileAsync(room.Document, room.Definition, context);
            unlit.Add(compiled);
            lit.Add(compiled with
            {
                Lighting = await RoomLighting.BakeAsync(
                    compiled, RoomLightHarness.Settings(split), context.Content!, context.Parallelism, CancellationToken.None),
            });
        }

        Assert.NotNull(lit.Get("split").LightingOfCompile);
        LevelGrid level = Line(0);
        LinkedLevel linkedLit = await LinkAsync(lit, level);
        LinkedLevel linkedUnlit = await LinkAsync(unlit, level);
        Assert.Equal(linkedUnlit.Bsp[BspLump.Areas].Data.ToArray(), linkedLit.Bsp[BspLump.Areas].Data.ToArray());
        Assert.Equal(linkedUnlit.Bsp[BspLump.AreaPortals].Data.ToArray(), linkedLit.Bsp[BspLump.AreaPortals].Data.ToArray());
        Assert.Equal(linkedUnlit.Bsp[BspLump.ClipPortalVerts].Data.ToArray(), linkedLit.Bsp[BspLump.ClipPortalVerts].Data.ToArray());
        DLeaf[] a = BspStructView.As<DLeaf>(linkedLit.Bsp[BspLump.Leafs]).ToArray();
        DLeaf[] b = BspStructView.As<DLeaf>(linkedUnlit.Bsp[BspLump.Leafs]).ToArray();
        Assert.Equal(b.Select(l => l.GetArea()), a.Select(l => l.GetArea()));
        Assert.True(linkedLit.Bsp[BspLump.Lighting].Length > 0);
        ValidationReport report = await BspValidator.CheckAsync(linkedLit.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>A linked level with area portals passes the loader checks <c>ssmap check</c> makes, with no error.</summary>
    [Fact]
    public async Task ALinkedLevelWithAreaPortalsPassesTheLoaderChecks()
    {
        LinkedLevel linked = await LinkAsync(await CompileAsync(Library()), RoomPropHarness.Level("hub, split, split@180, hub", "hub, hub, hub, hub"));
        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }
}
