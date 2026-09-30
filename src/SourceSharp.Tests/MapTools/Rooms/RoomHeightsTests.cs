//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomHeightHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Room heights (the rooms design, 17.6, PR 19): a room's
/// <c>room_height</c> makes its box <c>[0, c]² × [0, h]</c> in the split,
/// the lint, the prop rule and the link's cell centre, while its doors stay
/// a cube room's; the top tree and the shared solid leaf are bounded by the
/// rooms' heights; a tall room links as its level flattens, at every turn
/// and beside rooms of other heights; the level's extent is held to the
/// engine's coordinates; and a pack holding a shaped room is version 5.
/// </summary>
public sealed class RoomHeightsTests
{
    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    // ---- the split -----------------------------------------------------------------------------

    /// <summary>
    /// A room's <c>room_height</c> is its height; without the key, or with
    /// the cell size, it is the cube it always was; and the tall room owns
    /// the brushes of its shell above its cell's top.
    /// </summary>
    [Fact]
    public void ARoomsHeightIsReadFromItsInfoRoom()
    {
        VmfDocument library = Library();
        VmfChunk cubeMarker = library.GetChunks("entity").First(e => e.GetValue("name") == "hub");
        cubeMarker.AddKey(RoomLibraryVmf.RoomHeightKey, "256");

        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);

        Assert.Equal(["hub", "tall", "mid"], rooms.Select(r => r.Definition.Name));
        Assert.Equal([256f, Tall, Mid], rooms.Select(r => r.Definition.Height));
        Assert.Equal([false, true, true], rooms.Select(r => r.Definition.IsShaped));
        Assert.Equal(new Box(Vec3.Zero, new Vec3(256, 256, 512)), rooms[1].Definition.Bounds);
        Assert.Equal(new Box(Vec3.Zero, new Vec3(256, 256, 256)), rooms[0].Definition.Bounds);
        Assert.Equal(Hub.Bounds, rooms[0].Definition.Bounds);
        float top = rooms[1].Document.GetChunk("world")!.GetChunks("solid").Max(s => VmfPlacement.Bounds(s).Maxs.Z);
        Assert.Equal(Tall, top);
    }

    /// <summary>A height that is not one is refused with the rooms design's 17.3 texts.</summary>
    [Theory]
    [InlineData("250", "room tall: room_height 250 leaves no room for the door; a room is at least 256 tall (door_height + 2 x wall_depth).")]
    [InlineData("300.5", "room tall: room_height \"300.5\" is not a whole number of units.")]
    [InlineData("high", "room tall: room_height \"high\" is not a whole number of units.")]
    [InlineData("nan", "room tall: room_height \"nan\" is not a whole number of units.")]
    [InlineData("20000", "room tall: room_height 20000 is taller than 16384, the most the engine's coordinates allow.")]
    public void AHeightThatIsNotOneIsRefused(string height, string message)
    {
        VmfDocument library = Library();
        library.GetChunks("entity").First(e => e.GetValue("name") == "tall").Keys.First(k => k.Name == RoomLibraryVmf.RoomHeightKey).Value = height;
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library));
        Assert.Equal(message, refused.Message);
    }

    /// <summary>
    /// A room lower than its cell is allowed when its door fits (a low
    /// hallway): on the harness's 96-high kit, 128 is the least.
    /// </summary>
    [Fact]
    public void ALowRoomIsAllowedWhenItsDoorFits()
    {
        Assert.Null(RoomDefinition.HeightProblem(128, RoomHarness.Kit));
        Assert.Equal(
            "127 leaves no room for the door; a room is at least 128 tall (door_height + 2 x wall_depth).",
            RoomDefinition.HeightProblem(127, RoomHarness.Kit));
        Assert.Null(RoomDefinition.HeightProblem(16384, RoomHarness.Kit));
        Assert.Equal("16385 is taller than 16384, the most the engine's coordinates allow.", RoomDefinition.HeightProblem(16385, RoomHarness.Kit));
        Assert.Equal("128.5 is not a whole number of units.", RoomDefinition.HeightProblem(128.5f, RoomHarness.Kit));

        RoomDefinition low = RoomHarness.Room("low", RoomFacing.PositiveX) with { Height = 128 };
        low.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (low with { Height = 100 }).Validate());
    }

    /// <summary>
    /// A tall room's box reaches as high as the room, so a room built above
    /// it in the library, inside that box, overlaps it; the same room as a
    /// cube leaves the space above its cell free.
    /// </summary>
    [Fact]
    public void ATallRoomsBoxHoldsTheRoomBuiltAboveIt()
    {
        VmfDocument tall = RoomHarness.LibraryVmf(Shaped("tall", Tall));
        tall.Chunks.Add(RoomHarness.InfoRoom(Hub with { Name = "attic" }, new Vec3(0, 0, 320)));
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(tall));
        Assert.Equal("the cells of rooms \"tall\" and \"attic\" overlap; rooms stand in separate cells, with gaps between them.", refused.Message);

        VmfDocument cube = RoomHarness.LibraryVmf(Hub with { Name = "tall" });
        cube.Chunks.Add(RoomHarness.InfoRoom(Hub with { Name = "attic" }, new Vec3(0, 0, 320)));
        Assert.Equal(2, RoomLibraryVmf.Split(cube).Count);
    }

    // ---- the door and the model ---------------------------------------------------------------

    /// <summary>
    /// The door stays on the floor (the kit's sill rule): a tall room's plug
    /// boxes are a cube room's, bit for bit, on every wall; its model's
    /// ceiling is at its height, and the lintel above each door reaches it.
    /// </summary>
    [Fact]
    public void ATallRoomsDoorsAreACubeRoomsDoors()
    {
        RoomDefinition tall = Shaped("tall", Tall);
        foreach (RoomSocket socket in tall.Sockets)
        {
            Box plug = RoomLinter.SealBox(tall, socket, tall.CellSize);
            Assert.Equal(RoomLinter.SealBox(Hub, socket, Hub.CellSize), plug);
            Assert.Equal(RoomHarness.WalkableKit.Depth, plug.Mins.Z);
        }

        Assert.Equal(RoomCompiler.SealBoxes(Hub), RoomCompiler.SealBoxes(tall));
        List<Box> solids = [.. RoomModel.Build(tall, tall.Kit.Depth).GetChunk("world")!.GetChunks("solid").Select(VmfPlacement.Bounds)];
        Assert.Equal(Tall, solids.Max(b => b.Maxs.Z));
        Assert.Contains(solids, b => b.Mins.Z == Tall - tall.Kit.Depth && b.Maxs.Z == Tall && b.Maxs.X - b.Mins.X == 256);
        Assert.Contains(solids, b => b.Mins.Z == 240 && b.Maxs.Z == Tall && b.Mins.X == 256 - 16);
    }

    /// <summary>
    /// The lint holds a tall room to its own box: its shell up to its
    /// ceiling passes, and a brush past it is refused naming the height; a
    /// cube's message is today's.
    /// </summary>
    [Fact]
    public async Task TheLintHoldsATallRoomToItsBox()
    {
        RoomDefinition tall = Shaped("tall", Tall);
        VbspContext context = await RoomHarness.ContextAsync();
        MapFile model = await MapFileLoader.LoadAsync(context, RoomHarness.BuildRoomModel(tall), CancellationToken.None);
        RoomLinter.CheckModel(tall, model);

        VmfDocument high = RoomHarness.BuildRoomModel(tall);
        high.GetChunk("world")!.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(100, 100, 480), new Vec3(120, 120, 520), 5000));
        MapFile over = await MapFileLoader.LoadAsync(context, high, CancellationToken.None);
        RoomLintException refused = Assert.Throws<RoomLintException>(() => RoomLinter.CheckModel(tall, over));
        Assert.EndsWith("against the cell 0..256, 0..512 tall.", refused.Message, StringComparison.Ordinal);

        MapFile cubeOver = await MapFileLoader.LoadAsync(context, high, CancellationToken.None);
        RoomLintException cube = Assert.Throws<RoomLintException>(() => RoomLinter.CheckModel(Hub, cubeOver));
        Assert.EndsWith("against the cell 0..256.", cube.Message, StringComparison.Ordinal);
    }

    // ---- the link against the flatten ------------------------------------------------------------

    /// <summary>
    /// A tall room beside a cube hub, at every quarter turn: the link and
    /// the flattened level's compile put every sample of both cells, up to
    /// above the tall room's ceiling, in the same space (the tall room open
    /// above the hub's top, the hub solid there); a line through the door
    /// and one up the tall room are clear in both; the engine's culling
    /// bounds hold every open leaf; the map passes the loader checks; and
    /// the link is the same bytes at one thread and four.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ATallRoomLinksAsItFlattensAtEveryTurn(int rotation)
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid level = Level($"hub, tall@{rotation}");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.True(SameSpace(linked.Bsp, flat, level) > 0);
        Vec3 high = new(256 + 128, 128, 400);
        foreach (BspData map in new[] { linked.Bsp, flat })
        {
            LevelProbe probe = new(map);
            Assert.Equal(0, probe.Contents(high) & (int)BrushContents.Solid);
            Assert.NotEqual(0, probe.Contents(new Vec3(128, 128, 400)) & (int)BrushContents.Solid);
            Assert.Equal(1f, probe.Trace(new Vec3(128, 128, 60), new Vec3(256 + 128, 128, 60), (int)BrushContents.Solid));
            Assert.Equal(1f, probe.Trace(new Vec3(256 + 128, 128, 30), new Vec3(256 + 128, 128, 490), (int)BrushContents.Solid));
            Assert.True(probe.Trace(new Vec3(256 + 128, 128, 30), new Vec3(256 + 128, 128, 530), (int)BrushContents.Solid) < 1f);
        }

        Assert.Null(BoundsProblem(linked.Bsp));
        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
        LinkedLevel four = await RoomPropHarness.LinkAsync(rooms, level, degree: 4);
        Assert.Equal(await MultiLibraryHarness.BytesAsync(linked.Bsp), await MultiLibraryHarness.BytesAsync(four.Bsp));
    }

    /// <summary>
    /// Rooms of three heights in one level (a cube, a tall room and a room
    /// a cell and a half tall), turned: each top-tree node is bounded by the
    /// tallest room in its region, the root and the shared solid leaf by
    /// the tallest of the level; every open leaf is inside its nodes'
    /// bounds; the link and the flatten agree on every sample; and the map
    /// passes the loader checks with no error.
    /// </summary>
    [Fact]
    public async Task RoomsOfSeveralHeightsLinkInOneLevel()
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid level = Level("hub, tall@90", "mid@180, hub@270");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.True(SameSpace(linked.Bsp, flat, level) > 0);
        Assert.Null(BoundsProblem(linked.Bsp));
        DNode[] nodes = BspStructView.As<DNode>(linked.Bsp[BspLump.Nodes]).ToArray();
        Assert.Equal((short)Tall, nodes[0].Maxs[2]);
        Assert.Equal((short)Tall, BspStructView.As<DLeaf>(linked.Bsp[BspLump.Leafs])[0].Maxs[2]);

        // The top tree's single-cell nodes, one per cell: each as tall as its room.
        int top = LevelLinker.TopPlanes(linked.Plan.Layout, RoomHarness.Cell).Count;
        Dictionary<(int, int), short> single = [];
        for (int n = 0; n < top; n++)
        {
            if (nodes[n].Maxs[0] - nodes[n].Mins[0] == 256 && nodes[n].Maxs[1] - nodes[n].Mins[1] == 256)
            {
                single[(nodes[n].Mins[0] / 256, nodes[n].Mins[1] / 256)] = nodes[n].Maxs[2];
            }
        }

        Assert.Equal((short)256, single[(0, 1)]);
        Assert.Equal((short)Tall, single[(1, 1)]);
        Assert.Equal((short)Mid, single[(0, 0)]);
        Assert.Equal((short)256, single[(1, 0)]);

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// The top tree's bounds, alone: a level of cubes has no heights and
    /// today's tree; given every cell's height as the cube's, the nodes are
    /// the same; a tall cell raises the nodes over it and no other; an empty
    /// region keeps the cell's top; the level's top is its tallest room.
    /// </summary>
    [Fact]
    public void TheTopTreeIsBoundedByTheRoomsInEachRegion()
    {
        LevelLayout layout = new("t", 256, RoomHarness.WalkableKit,
        [
            new RoomInstance(new RoomPlacement("a", 0, 0, 0), [], []),
            new RoomInstance(new RoomPlacement("b", 2, 0, 0), [], []),
        ]);
        Assert.Null(LevelLinker.CellHeights([(new RoomPlacement("a", 0, 0, 0), Hub), (new RoomPlacement("b", 2, 0, 0), Hub)]));
        List<DNode> cubes = LevelLinker.BuildTopNodes(layout, 256, []);
        Assert.Equal(
            cubes.Select(n => (n.Mins[2], n.Maxs[2])),
            LevelLinker.BuildTopNodes(layout, 256, [], new Dictionary<(int X, int Y), float> { [(0, 0)] = 256, [(2, 0)] = 256 }).Select(n => (n.Mins[2], n.Maxs[2])));

        Dictionary<(int X, int Y), float> heights = LevelLinker.CellHeights(
            [(new RoomPlacement("a", 0, 0, 0), Hub), (new RoomPlacement("b", 2, 0, 0), Shaped("b", Tall))])!;
        List<DNode> shaped = LevelLinker.BuildTopNodes(layout, 256, [], heights);
        Assert.Equal(cubes.Count, shaped.Count);
        foreach ((DNode cube, DNode node) in cubes.Zip(shaped))
        {
            bool overTall = node.Maxs[0] > 512;
            bool empty = node.Mins[0] == 256 && node.Maxs[0] == 512;
            Assert.Equal(overTall ? (short)Tall : (short)256, node.Maxs[2]);
            Assert.Equal(cube.Mins[0], node.Mins[0]);
            Assert.Equal(cube.PlaneNum, node.PlaneNum);
            Assert.True(!empty || node.Maxs[2] == 256);
        }

        Assert.Equal(Tall, LevelLinker.TallestRoom(heights, 256));
        Assert.Equal(256f, LevelLinker.TallestRoom(null, 256));
    }

    // ---- the extent -------------------------------------------------------------------------------

    /// <summary>
    /// A level past the engine's coordinates is refused naming the axis, the
    /// coordinate and the cell, by the link and by the flatten alike;
    /// touching the limit is allowed; and a room's height is held to it in z.
    /// </summary>
    [Fact]
    public async Task ALevelPastTheEnginesCoordinatesIsRefused()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid wide = Level(string.Join(", ", ["hub", .. Enumerable.Repeat("~", 63), "hub"]));
        const string Message = "level heights: reaches x = 16640 at cell (64, 0); the engine's coordinates stop at 16384.";
        LinkException linked = await Assert.ThrowsAsync<LinkException>(() => RoomPropHarness.LinkAsync(rooms, wide));
        Assert.Equal(Message, linked.Message);
        LinkException flattened = Assert.Throws<LinkException>(() => LevelFlattener.Flatten(wide, library));
        Assert.Equal(Message, flattened.Message);

        LevelLinker.CheckExtent("edge", [(new RoomPlacement("hub", 63, 63, 0), Hub)]);
        LinkException south = Assert.Throws<LinkException>(() => LevelLinker.CheckExtent("s", [(new RoomPlacement("hub", 0, -65, 0), Hub)]));
        Assert.Equal("level s: reaches y = -16640 at cell (0, -65); the engine's coordinates stop at 16384.", south.Message);
        LinkException up = Assert.Throws<LinkException>(() => LevelLinker.CheckExtent("u", [(new RoomPlacement("hub", 1, 2, 0), Hub with { Height = 16400 })]));
        Assert.Equal("level u: reaches z = 16400 at cell (1, 2); the engine's coordinates stop at 16384.", up.Message);
        LevelLinker.CheckExtent("sky", [(new RoomPlacement("sky", 0, 0, 0) { Level = -1 }, Hub)]);
    }

    // ---- the linker's own entities and props --------------------------------------------------------

    /// <summary>
    /// The linker's entities stand at the centre of the room's box: half the
    /// room's height up, turned and moved with it; a cube's is its cell's
    /// centre, as before.
    /// </summary>
    [Fact]
    public void TheCellCentreIsHalfTheRoomUp()
    {
        RoomTransform turned = new(new RoomPlacement("tall", 2, 1, 1), 256);
        Assert.Equal("640 384 256", LevelLinker.CellCentre(turned, Shaped("tall", Tall)));
        Assert.Equal("640 384 128", LevelLinker.CellCentre(turned, Hub));
        Assert.Equal("640 384 192", LevelLinker.CellCentre(turned, Shaped("mid", Mid)));
    }

    /// <summary>
    /// With the mod's entities, each placement's <c>logic_room</c> stands at
    /// its room's centre, half the room's height up, and the link and the
    /// flatten write the same origins. The linker writes a room's
    /// <c>logic_room</c> only when something in the room names it, so each
    /// room carries a <c>logic_auto</c> that fires its hub.
    /// </summary>
    [Fact]
    public async Task TheLogicRoomStandsHalfTheRoomUpInLinkAndFlatten()
    {
        VmfDocument library = Library((0, FiresItsRoom(1)), (1, FiresItsRoom(2)), (2, FiresItsRoom(3)));
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid level = Level("hub, tall@90, mid@270");
        LinkedLevel linked = await LevelLinker.LinkAsync(
            RoomPropHarness.Layout(rooms, level), rooms, await RoomHarness.ContextAsync(), new LevelLinkOptions { ModEntities = true });
        FlattenedLevel flat = LevelFlattener.FlattenLevel(level, library, new LevelFlattenOptions { ModEntities = true });

        List<string> ours = [.. EntityLump.Parse(linked.Bsp[BspLump.Entities])
            .Where(e => e.ClassName == SourceSharp.RoomContracts.LogicRoom.ClassName).Select(e => e.Get("origin")!)];
        List<string> theirs = [.. flat.Vmf.GetChunks("entity")
            .Where(e => e.GetValue("classname") == SourceSharp.RoomContracts.LogicRoom.ClassName).Select(e => e.GetValue("origin")!)];
        Assert.Equal(["128 128 128", "384 128 256", "640 128 192"], ours);
        Assert.Equal(ours, theirs);
    }

    /// <summary>A <c>logic_auto</c> at the room's floor that fires the room's own <c>logic_room</c> on spawn.</summary>
    private static VmfChunk FiresItsRoom(int id)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", "logic_auto");
        entity.AddKey("origin", "40 40 40");
        entity.AddKey("spawnflags", "1");
        entity.AddChunk("connections").AddKey("OnMapSpawn", "cxry_room,Trigger1,,0,-1");
        return entity;
    }

    /// <summary>
    /// The prop rule's cell box is the room's: a box prop standing above the
    /// cube's top is refused in the hub and carried in the tall room, and
    /// one past the tall room's ceiling is refused there.
    /// </summary>
    [Theory]
    [InlineData(0, 230f, "room hub: prop_static 700 (models/props_test/box.mdl) reaches 6 units outside the cell; props stay in their cell except as socket furniture.")]
    [InlineData(1, 230f, null)]
    [InlineData(1, 470f, null)]
    [InlineData(1, 490f, "room tall: prop_static 700 (models/props_test/box.mdl) reaches 10 units outside the cell; props stay in their cell except as socket furniture.")]
    public async Task ThePropRuleHoldsAPropToItsRoomsBox(int room, float z, string? message)
    {
        VmfDocument library = Library((room, RoomPropHarness.Prop(700, RoomPropHarness.BoxModel, new Vec3(128, 128, z))));
        if (message is null)
        {
            RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
            Assert.Single(rooms.Get("tall").StaticProps!.Props);
            LevelGrid level = Level("hub, tall");
            LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
            Assert.Equal(RoomPropHarness.Observed(await CompileFlatAsync(library, level)), RoomPropHarness.Observed(linked.Bsp));
        }
        else
        {
            RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => RoomPropHarness.CompileAsync(library));
            Assert.Equal(message, refused.Message);
        }
    }

    /// <summary>
    /// The arrival is held to the room's box: one high in a tall up room,
    /// above the cube's top, is the room's and compiles; in a cube room the
    /// same point is in no room (the split's box), and one whose standing
    /// hull passes the cube's top is refused with the 15.4 text.
    /// </summary>
    [Fact]
    public async Task AnArrivalIsHeldToItsRoomsBox()
    {
        static VmfChunk[] Up(float z) => [.. TransitHarness.UpEntities.Skip(1), TransitHarness.Poi(100, "arrival", new Vec3(64, 192, z), 0)];
        RoomLibrary tall = await TransitHarness.CompileAsync(TransitHarness.Library(up: Up(300), upHeight: Tall));
        Assert.Equal(Tall, tall.Get("up").Definition.Height);
        _ = await TransitHarness.CompileAsync(TransitHarness.Library(up: Up(200), upHeight: Tall));

        Exception outside = await TransitHarness.CompileErrorAsync(TransitHarness.Library(up: Up(300)));
        Assert.Equal("room up: an up room needs exactly one arrival point; it has 0.", outside.Message);
        Exception refused = await TransitHarness.CompileErrorAsync(TransitHarness.Library(up: Up(200)));
        Assert.IsType<RoomLintException>(refused);
        Assert.Equal("room up: the arrival point at (64, 192, 200) has no room for a standing player (32 x 32 x 72).", refused.Message);
    }

    // ---- the pack -----------------------------------------------------------------------------------

    /// <summary>
    /// A pack holding a shaped room is version 5 and its shaped rooms carry
    /// <c>SHAP</c>; one of cube rooms is version 4 with none; read back, the
    /// tall room is tall again and links to the same bytes as in memory.
    /// </summary>
    [Fact]
    public async Task APackWithAShapedRoomIsVersionFive()
    {
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(Library());
        byte[] shaped = await PackAsync(rooms.Get("hub"), rooms.Get("tall"));
        byte[] cubes = await PackAsync(rooms.Get("hub"));
        Assert.Equal(5, BinaryPrimitives.ReadInt32BigEndian(shaped.AsSpan(8)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(cubes.AsSpan(8)));

        using MemoryStream stream = new(shaped);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        Assert.Equal(5, index.Version);
        Assert.Null(index.Find("hub")!.Find(RoomShape.SectionTag));
        Assert.NotNull(index.Find("tall")!.Find(RoomShape.SectionTag));
        using MemoryStream cubeStream = new(cubes);
        Assert.DoesNotContain((await RoomPack.ReadIndexAsync(cubeStream)).Entries, e => e.Find(RoomShape.SectionTag) is not null);

        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(stream, index, [new RoomPackRequest("hub", [0]), new RoomPackRequest("tall", [0, 1])]);
        Assert.False(loaded[0].Definition.IsShaped);
        Assert.Equal(Tall, loaded[1].Definition.Height);
        Assert.Equal(rooms.Get("tall").Definition.Bounds, loaded[1].Definition.Bounds);

        RoomLibrary fromPack = RoomPropHarness.RoomsOf([.. loaded]);
        LevelGrid level = Level("hub, tall@90");
        Assert.Equal(
            await MultiLibraryHarness.BytesAsync((await RoomPropHarness.LinkAsync(rooms, level)).Bsp),
            await MultiLibraryHarness.BytesAsync((await RoomPropHarness.LinkAsync(fromPack, level)).Bsp));
    }

    /// <summary>
    /// A build that reads only version 4 refuses a version 5 pack by its
    /// version check, with the text every build's check gives, so it never
    /// links a tall room as a cube; this build refuses version 6 the same way.
    /// </summary>
    [Fact]
    public async Task AVersionFourReaderRefusesAVersionFivePack()
    {
        LinkException old = Assert.Throws<LinkException>(() => RoomPack.CheckVersion(5, 4));
        Assert.Equal("room pack version 5; this build reads version 4.", old.Message);
        RoomPack.CheckVersion(4, 4);
        RoomPack.CheckVersion(3, 5);

        RoomLibrary rooms = await RoomPropHarness.CompileAsync(RoomHarness.LibraryVmf(Shaped("tall", Tall)));
        byte[] pack = await PackAsync(rooms.Get("tall"));
        BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(8), 6);
        using MemoryStream stream = new(pack);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomPack.ReadIndexAsync(stream));
        Assert.Equal("room pack version 6; this build reads version 5.", refused.Message);
    }

    /// <summary>
    /// A shape section that is damaged, or says what this build cannot link,
    /// is refused naming the room and the section; and one in a version 4
    /// pack, which no build wrote, is refused as damage.
    /// </summary>
    [Fact]
    public async Task ADamagedShapeIsRefused()
    {
        RoomDefinition tall = Shaped("tall", Tall);
        byte[] section = RoomShape.ToSection(tall)!.Value.Bytes.ToArray();
        Assert.Null(RoomShape.ToSection(Hub));
        Assert.Equal(Tall, RoomShape.Apply(RoomShape.Read(section, "tall"), Hub).Height);
        RoomDefinition hub = Hub;
        Assert.Same(hub, RoomShape.Apply(null, hub));

        // payload: revision @9, rotations @13, length @17, height @21, width @25, depth @29, sockets @33, offsets @37
        Assert.Equal(
            "room pack entry \"tall\": its \"SHAP\" section is of a revision this build does not read; its shape would be lost, so recompile the library with ssmap room.",
            Refused(section, 9, 2));
        Assert.Equal("room pack entry \"tall\": its \"SHAP\" section holds 4 rotations; a room's shape is stored once.", Refused(section, 13, 4));
        Assert.Equal("room pack entry \"tall\": its \"SHAP\" section holds a footprint of 2 x 1 cells; this build links rooms of one cell.", Refused(section, 25, 2));
        Assert.Equal("room pack entry \"tall\": its \"SHAP\" section holds socket 1 at cell (1, 0) of a one-cell footprint.", Refused(section, 45, 1));
        Assert.Equal("room pack entry \"tall\": its \"SHAP\" section holds 9 sockets; a room of one cell has 0 to 4.", Refused(section, 33, 9));

        LinkException sockets = Assert.Throws<LinkException>(() => RoomShape.Apply(RoomShape.Read(section, "tall"), RoomHarness.WalkableRoom("tall", RoomFacing.PositiveX)));
        Assert.Equal("its \"SHAP\" section holds 4 sockets; the room has 1.", sockets.Message);
        LinkException cube = Assert.Throws<LinkException>(() => RoomShape.Apply(new RoomShapeData(256, 4), Hub));
        Assert.Equal("its \"SHAP\" section holds the cell size as its height; a cube room has no shape section.", cube.Message);
        LinkException low = Assert.Throws<LinkException>(() => RoomShape.Apply(new RoomShapeData(200, 4), Hub));
        Assert.Equal("its \"SHAP\" section holds room_height 200 leaves no room for the door; a room is at least 256 tall (door_height + 2 x wall_depth).", low.Message);

        RoomLibrary rooms = await RoomPropHarness.CompileAsync(RoomHarness.LibraryVmf(Shaped("tall", Tall)));
        byte[] pack = await PackAsync(rooms.Get("tall"));
        BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(8), 4);
        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        LinkException damaged = await Assert.ThrowsAsync<LinkException>(() => RoomPack.LoadRoomsAsync(stream, index, [new RoomPackRequest("tall", [0])]));
        Assert.Equal(
            "room pack entry \"tall\" has a \"SHAP\" section in a version 4 pack; only a version 5 pack holds shaped rooms, so the pack is damaged; recompile the library with ssmap room.",
            damaged.Message);

        static string Refused(byte[] section, int at, int value)
        {
            byte[] copy = [.. section];
            BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(at), value);
            return Assert.Throws<LinkException>(() => RoomShape.Read(copy, "tall")).Message;
        }
    }

    /// <summary>
    /// A room's height is in its cache key and its compile's input keys
    /// only when it is shaped: a cube keeps the keys it had.
    /// </summary>
    [Fact]
    public void AShapedRoomsHeightIsInItsKeys()
    {
        LibraryRoom room = RoomLibraryVmf.Split(Library())[1];
        LibraryRoom cube = room with { Definition = room.Definition with { Height = room.Definition.CellSize } };
        LibraryRoom taller = room with { Definition = room.Definition with { Height = 640 } };
        Assert.NotEqual(RoomCacheKey.RoomDigest(cube), RoomCacheKey.RoomDigest(room));
        Assert.NotEqual(RoomCacheKey.RoomDigest(taller), RoomCacheKey.RoomDigest(room));
        LibraryRoom plain = room with { Definition = new RoomDefinition(room.Definition.Name, 256, room.Definition.Kit, room.Definition.Sockets) };
        Assert.Equal(RoomCacheKey.RoomDigest(plain), RoomCacheKey.RoomDigest(cube));
    }

    /// <summary>A pack of the given rooms, as <c>ssmap room</c> writes one.</summary>
    private static async Task<byte[]> PackAsync(params RoomObject[] rooms)
    {
        List<RoomPackItem> items = [];
        foreach (RoomObject room in rooms)
        {
            items.Add(await RoomPackItem.CreateAsync(room));
        }

        using MemoryStream stream = new();
        await RoomPack.SaveAsync(items, stream);
        return stream.ToArray();
    }
}
