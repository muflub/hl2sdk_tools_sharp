//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomWaterHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Water through a door (the rooms design, 4.6, "water sockets"; PR 14's
/// second stage): a room declares the water at a socket on its
/// <c>info_room</c>, the compile is held to it, a joint joins two sockets of
/// one level, and the link carves the doorway's water (a water leaf below
/// the level, a surface on a node at the level, a brush and a fluid convex)
/// where the flattened level's compile has a doorway brush of water; the
/// two agree at every quarter turn.
/// </summary>
public sealed class LevelLinkerWaterSocketTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// The hub and the other room jointed through the hub's east socket and
    /// the other's west, both turned by <paramref name="rotation"/>: rows
    /// north first, and the grid's size.
    /// </summary>
    private static (LevelGrid Level, int Columns, int Rows) Pair(int rotation) => rotation switch
    {
        0 => (RoomPropHarness.Level("hub, other"), 2, 1),
        90 => (RoomPropHarness.Level("other@90", "hub@90"), 1, 2),
        180 => (RoomPropHarness.Level("other@180, hub@180"), 2, 1),
        _ => (RoomPropHarness.Level("hub@270", "other@270"), 1, 2),
    };

    // ---- link and flatten agree ------------------------------------------------------------------

    /// <summary>
    /// Water through a door at every quarter turn: every point of a lattice
    /// over the level holds the same in the link and the flattened level's
    /// compile (the doorway water to the level, open air above it), the two
    /// carry the same water records (one: the doorway joins the two rooms'
    /// water into one body), the same fluid (the link's two rooms' fluids,
    /// the doorway's convexes among them, cover what the flattened level's
    /// one does, volume and extent), the doorway's surface wholly covered
    /// from above and from below by faces of the water's materials, and the
    /// linked map passes the loader's checks.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task WaterThroughADoorLinksAsItFlattensAtEveryRotation(int rotation)
    {
        VmfDocument library = SocketLibrary();
        RoomLibrary rooms = await CompileSocketsAsync(library);
        (LevelGrid level, int columns, int rows) = Pair(rotation);
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(LevelLinkerWaterTests.Points(flat, columns, rows), LevelLinkerWaterTests.Points(linked.Bsp, columns, rows));
        Assert.Equal(["64 16 unit/water_cheap"], LevelLinkerWaterTests.Records(linked.Bsp));
        Assert.Equal(LevelLinkerWaterTests.Records(flat), LevelLinkerWaterTests.Records(linked.Bsp));
        Assert.Equal(FluidSummary(flat), FluidSummary(linked.Bsp));

        Box doorway = Doorway(linked.Bsp);
        Assert.Equal("water at 64 of unit/water_cheap", At(linked.Bsp, doorway.Mins + new Vec3(3, 3, 3)));
        Assert.Equal("air", At(linked.Bsp, new Vec3(doorway.Mins.X + 3, doorway.Mins.Y + 3, 100)));
        double area = (doorway.Maxs.X - doorway.Mins.X) * (doorway.Maxs.Y - doorway.Mins.Y);
        Assert.Equal((area, area), SurfaceCover(linked.Bsp, doorway, SocketLevel));
        Assert.Equal((area, area), SurfaceCover(flat, doorway, SocketLevel));

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// A doorway's water leaf lists one water brush, as vbsp gives a water
    /// leaf the brushes in it: traces meet the doorway's water as they meet
    /// the flattened level's doorway brush.
    /// </summary>
    [Fact]
    public async Task ADoorwaysWaterLeafHoldsAWaterBrush()
    {
        RoomLibrary rooms = await CompileSocketsAsync(SocketLibrary());
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub, other"));
        DLeaf leaf = BspStructView.As<DLeaf>(linked.Bsp[BspLump.Leafs])[RoomWater.LeafAt(linked.Bsp, new Vec3(250, 128, 40))];
        Assert.Equal(1, leaf.NumLeafBrushes);
        int brush = BspStructView.As<ushort>(linked.Bsp[BspLump.LeafBrushes])[leaf.FirstLeafBrush];
        DBrush water = BspStructView.As<DBrush>(linked.Bsp[BspLump.Brushes])[brush];
        Assert.Equal((int)BrushContents.Water, water.Contents & (int)BrushContents.Water);
        Assert.Equal(6, water.NumSides);
        Assert.Equal(0, water.Contents & (int)BrushContents.TestFogVolume);
    }

    /// <summary>
    /// A chain of water doors is one body of water: three rooms jointed by
    /// two water doors link to one record, as the flattened level's compile
    /// finds one volume, where each room alone has its own.
    /// </summary>
    [Fact]
    public async Task AChainOfWaterDoorsIsOneRecord()
    {
        VmfDocument library = Library([
            (0, Water(EastPool(), WaterBrush)),
            (1, Water(new Box(new Vec3(16, 64, 16), new Vec3(240, 192, SocketLevel)), WaterBrush + 1))]);
        Declare(library, "hub", "east", "64 unit/water_cheap");
        Declare(library, "other", "west", "64 unit/water_cheap");
        Declare(library, "other", "east", "64 unit/water_cheap");
        RoomLibrary rooms = await CompileSocketsAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub, other, hub@180");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Single(WaterData(linked.Bsp));
        Assert.Equal(LevelLinkerWaterTests.Records(flat), LevelLinkerWaterTests.Records(linked.Bsp));
        Assert.Equal(LevelLinkerWaterTests.Points(flat, 3, 1), LevelLinkerWaterTests.Points(linked.Bsp, 3, 1));
    }

    /// <summary>
    /// A ring of water doors is one body of water too: four placements of a
    /// room whose one pool reaches its east and north doors, turned so every
    /// pair of neighbours meets at water, link to one record (the ring's
    /// last joint finding its two sides already joined), as the flattened
    /// level's compile finds one volume.
    /// </summary>
    [Fact]
    public async Task ARingOfWaterDoorsIsOneRecord()
    {
        VmfDocument library = Library([(0, Water(new Box(new Vec3(64, 64, 16), new Vec3(240, 240, SocketLevel))))]);
        Declare(library, "hub", "east", "64 unit/water_cheap");
        Declare(library, "hub", "north", "64 unit/water_cheap");
        RoomLibrary rooms = await CompileSocketsAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub@270, hub@180", "hub, hub@90");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Single(WaterData(linked.Bsp));
        Assert.Equal(LevelLinkerWaterTests.Records(flat), LevelLinkerWaterTests.Records(linked.Bsp));
        Assert.Equal(LevelLinkerWaterTests.Points(flat, 2, 2), LevelLinkerWaterTests.Points(linked.Bsp, 2, 2));
    }

    /// <summary>
    /// A water socket at a cap keeps its plug: the room's water stays in the
    /// room, the doorway is a wall, and nothing is carved.
    /// </summary>
    [Fact]
    public async Task ACappedWaterSocketKeepsItsPlug()
    {
        VmfDocument library = SocketLibrary();
        RoomLibrary rooms = await CompileSocketsAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub", "other");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);

        Assert.Equal("solid", At(linked.Bsp, new Vec3(250, 128, 40)));
        Assert.Equal(LevelLinkerWaterTests.Points(await CompileFlatAsync(library, level), 1, 2), LevelLinkerWaterTests.Points(linked.Bsp, 1, 2));
    }

    /// <summary>
    /// A doorway wholly under water (the level at the door's top): the whole
    /// doorway is water, with no surface in it, in the link as in the
    /// flattened level's compile.
    /// </summary>
    [Fact]
    public async Task AFloodedDoorwayIsWaterThroughout()
    {
        const float top = RoomHarness.Cell - 16;
        VmfDocument library = SocketLibrary(top, top);
        RoomLibrary rooms = await CompileSocketsAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub, other");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);

        Assert.Equal(LevelLinkerWaterTests.Points(await CompileFlatAsync(library, level), 2, 1), LevelLinkerWaterTests.Points(linked.Bsp, 2, 1));
        // A room filled to its ceiling has no surface: vbsp records its
        // largest coordinate as the water's height.
        Assert.Equal("water at 16384 of unit/water_cheap", At(linked.Bsp, new Vec3(250, 128, 230)));
        Assert.Equal((0.0, 0.0), SurfaceCover(linked.Bsp, Doorway(linked.Bsp), top));
    }

    // ---- the joint rule --------------------------------------------------------------------------

    /// <summary>
    /// The two sides of a joint are at one level: water at two levels, or
    /// water against a dry socket, is refused by the link and the flatten
    /// alike, naming both rooms, cells, sockets and levels.
    /// </summary>
    [Theory]
    [InlineData(80f, "64", "80")]
    [InlineData(-1f, "64", "none")]
    public async Task AJointsWaterIsAtOneLevel(float otherLevel, string a, string b)
    {
        VmfDocument library = otherLevel < 0
            ? Library([(0, Water(EastPool(), WaterBrush))])
            : SocketLibrary(otherLevel: otherLevel);
        if (otherLevel < 0)
        {
            Declare(library, "hub", "east", "64 unit/water_cheap");
        }

        RoomLibrary rooms = await CompileSocketsAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub, other");
        string expected = $"room hub at cell (0, 0) and room other at cell (1, 0) meet with water at {a} at socket \"east\" and {b} at socket \"west\";"
            + " the water on the two sides of a joint is at one level.";
        Assert.Equal(expected, (await Assert.ThrowsAsync<LinkException>(() => RoomPropHarness.LinkAsync(rooms, level))).Message);
        Assert.Equal(expected, Assert.Throws<LinkException>(() => LevelFlattener.Flatten(level, library)).Message);
    }

    // ---- the carve -----------------------------------------------------------------------------

    /// <summary>
    /// The carve of a water doorway's piece, by where the level stands in
    /// it: across it, a node at the level whose front is the piece's open
    /// air (the surface seen from above listed there) and whose back a new
    /// water leaf of the doorway's record (the surface seen from below);
    /// topping it, a water piece under a node whose front is a leaf as thin
    /// as the plane; below it, the whole piece water with no node; at or
    /// above it, the piece open air as a dry doorway's; and without an
    /// underside face, one face.
    /// </summary>
    [Theory]
    [InlineData(50f, 5, 6, "crosses")]
    [InlineData(100f, 5, 6, "tops")]
    [InlineData(150f, -1, -1, "below")]
    [InlineData(0f, 5, 6, "above")]
    [InlineData(50f, 5, -1, "crosses, no underside")]
    public void AWaterDoorwayPieceIsCarvedByItsLevel(float level, int top, int bottom, string what)
    {
        DLeaf solid = new()
        {
            Contents = (int)BrushContents.Solid,
            Cluster = -1,
            Mins = LevelLinker.Short3(Vec3.Zero),
            Maxs = LevelLinker.Short3(new Vec3(100, 100, 100)),
            LeafWaterDataId = -1,
        };
        DLeaf open = new() { Contents = 0, Cluster = 0, LeafWaterDataId = -1 };
        open.SetAreaFlags(1, LeafFlags.None);
        List<DLeaf> leafs = [solid];
        List<DNode> nodes = [];
        LevelLinker.LinkPlanes planes = new();
        List<ushort> leafMinDist = [9];
        List<(int, int, int)> doorways = [];
        LevelLinker.WaterDoorways sink = new() { FaceBase = 100 };
        RoomWaterDoor door = new(level, 0, top, bottom, 0x10000020, -1, CheapWater);
        LevelLinker.DoorwayWater water = new(null!, door, level, 3);

        int head = LevelLinker.CarveLeaf(
            5, [open], 0, [(new Box(new Vec3(10, 10, 0), new Vec3(20, 20, 100)), 0)], nodes, leafs, planes, leafMinDist, doorways, 2, null, [water], sink);

        Assert.Equal(leafs.Count, leafMinDist.Count);
        DLeaf low = leafs[Walk(new Vec3(15, 15, 20))];
        DLeaf high = leafs[Walk(new Vec3(15, 15, 80))];
        switch (what)
        {
            case "above":
                Assert.Equal((0, -1), (low.Contents, (int)low.LeafWaterDataId));
                Assert.Empty(sink.Faces);
                Assert.Empty(sink.Pieces);
                break;
            case "below":
                Assert.Equal((0x10000020, 3), (high.Contents, (int)high.LeafWaterDataId));
                Assert.Empty(sink.Faces);
                Assert.Single(sink.Pieces);
                Assert.DoesNotContain(nodes, n => planes.Planes[n.PlaneNum].Normal.Z != 0 && n.NumFaces > 0);
                break;
            default:
                Assert.Equal((0x10000020, 3), (low.Contents, (int)low.LeafWaterDataId));
                Assert.Equal(what == "tops" ? (0x10000020, 3) : (0, -1), (high.Contents, (int)high.LeafWaterDataId));
                Assert.Equal(5, low.Cluster);
                Assert.Equal(5, high.Cluster);
                DNode surface = Assert.Single(nodes, n => n.NumFaces > 0);
                Assert.Equal(new Plane(new Vec3(0, 0, 1), level), new Plane(planes.Planes[surface.PlaneNum].Normal, planes.Planes[surface.PlaneNum].Dist));
                Assert.Equal(100, surface.FirstFace);
                Assert.Equal(bottom >= 0 ? 2 : 1, surface.NumFaces);
                Assert.Equal(bottom >= 0 ? 2 : 1, sink.FaceCount);
                Assert.Equal(new Box(new Vec3(10, 10, level), new Vec3(20, 20, level)), sink.Faces[0].Rect);
                Assert.Equal(top, sink.Faces[0].TemplateFace);
                Assert.Single(sink.Pieces);
                Assert.Equal(new Box(new Vec3(10, 10, 0), new Vec3(20, 20, level)), sink.Pieces[0].Box);
                if (what == "tops")
                {
                    // The piece a solid leaf gave was 0 to 100 and the level is
                    // its top: the air in front is as thin as the plane.
                    DLeaf thin = leafs[sink.Faces[0].Leaf];
                    Assert.Equal(thin.Mins[2], thin.Maxs[2]);
                }

                break;
        }

        int Walk(Vec3 point)
        {
            int index = head;
            while (index >= 0)
            {
                DPlane plane = planes.Planes[nodes[index].PlaneNum];
                index = Vec3.Dot(point, plane.Normal) - plane.Dist < 0 ? nodes[index].Children[1] : nodes[index].Children[0];
            }

            return ~index;
        }
    }

    // ---- declarations -------------------------------------------------------------------------

    /// <summary>A room's water sockets are read from its <c>info_room</c>'s <c>water_&lt;wall&gt;</c> keys, by socket name.</summary>
    [Fact]
    public void TheSplitReadsAWaterSocket()
    {
        VmfDocument library = SocketLibrary(otherMaterial: ExpensiveWater, otherLevel: 72.5f);
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        Assert.Equal(new RoomWaterSocket(64, CheapWater), rooms[0].WaterSockets["east"]);
        Assert.Equal(new RoomWaterSocket(72.5f, ExpensiveWater), Assert.Single(rooms[1].WaterSockets).Value);
    }

    /// <summary>A water socket the split cannot read, or one no water can use, is refused naming the room and key.</summary>
    [Theory]
    [InlineData("water_up", "64 unit/water_cheap", "has a key \"water_up\"; a water socket is declared by water_east, water_west, water_north or water_south.")]
    [InlineData("water_east", "deep unit/water_cheap", "\"water_east\" is \"deep unit/water_cheap\"; a water socket is a height and a water material, such as \"48 nature/water_canals_cheap001\".")]
    [InlineData("water_east", "64", "\"water_east\" is \"64\"; a water socket is a height and a water material")]
    [InlineData("water_east", "16 unit/water_cheap", "room \"hub\" declares water at 16 on its east wall, at or below the door's sill (16); water that does not reach the doorway needs no water socket.")]
    public void AWaterSocketTheSplitCannotReadIsRefused(string key, string value, string expected)
    {
        VmfDocument library = Library([]);
        library.GetChunks(MapFileLoader.EntityChunk)
            .First(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity && e.GetValue(RoomLibraryVmf.NameKey) == "hub")
            .AddKey(key, value);
        Assert.Contains(expected, Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library)).Message, StringComparison.Ordinal);
    }

    /// <summary>A water socket on a wall without a door plug is refused naming the wall.</summary>
    [Fact]
    public void AWaterSocketOnAWallWithoutAPlugIsRefused()
    {
        VmfDocument library = RoomHarness.LibraryVmf(
            RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX));
        Declare(library, "hall", "north", "64 unit/water_cheap");
        Assert.Equal(
            "room \"hall\" declares water on its north wall (water_north), but its north wall has no door plug.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library)).Message);
    }

    /// <summary>
    /// The room compile holds a declared water socket to what it compiled:
    /// the doorway filled with water to the level and no higher, one body of
    /// water, its surface at the level, of the declared material and unlit.
    /// </summary>
    [Theory]
    [InlineData("dry", "room hub: socket \"east\" declares water at 64, but (239.5 80.5 16.5) against its plug holds air; the water must fill the doorway to that height and no higher.")]
    [InlineData("higher", "room hub: socket \"east\" declares water at 64, but (239.5 80.5 64.5) against its plug holds water; the water must fill the doorway to that height and no higher.")]
    [InlineData("material", "room hub: socket \"east\" declares unit/water_expensive, but the water against its plug is unit/water_cheap.")]
    [InlineData("lit", "room hub: socket \"east\"'s water unit/water_lit is lit (%compileKeepLight); the surface the link adds in a doorway has no lightmap, so a water socket's water is unlit.")]
    [InlineData("two", "room hub: socket \"east\" meets two bodies of water; a water socket's doorway meets one.")]
    public async Task ARoomCompileHoldsAWaterSocketToItsWater(string what, string expected)
    {
        (VmfChunk[] water, string declared) = what switch
        {
            "dry" => ((VmfChunk[])[], "64 unit/water_cheap"),
            "higher" => ([Water(EastPool(80))], "64 unit/water_cheap"),
            "material" => ([Water(EastPool())], "64 unit/water_expensive"),
            "lit" => ([Water(EastPool(), material: LitWater)], "64 unit/water_lit"),
            _ => ([Water(new Box(new Vec3(144, 64, 16), new Vec3(240, 128, 64)), WaterBrush), Water(new Box(new Vec3(144, 128, 16), new Vec3(240, 192, 64)), WaterBrush + 1, Slime)],
                "64 unit/water_cheap"),
        };

        VmfDocument library = Library([.. water.Select(w => (0, w))]);
        Declare(library, "hub", "east", declared);
        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => CompileSocketsAsync(library));
        Assert.Equal(expected, refused.Message);
    }

    /// <summary>
    /// Water may reach a declared socket's plug and no other: the room
    /// compile refuses water at an undeclared plug as before, even when the
    /// room declares another socket.
    /// </summary>
    [Fact]
    public async Task WaterAtAnUndeclaredPlugIsStillRefused()
    {
        VmfDocument library = Library([(0, Water(EastPool())), (0, Water(new Box(new Vec3(16, 64, 16), new Vec3(64, 192, 40)), WaterBrush + 1))]);
        Declare(library, "hub", "east", "64 unit/water_cheap");
        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => CompileSocketsAsync(library));
        Assert.Equal("room hub: water reaches socket \"west\"; water may not touch a door plug.", refused.Message);
    }

    // ---- the pack and determinism ---------------------------------------------------------------

    /// <summary>
    /// The water sockets round-trip through a pack (the loaded rooms link to
    /// the same bytes), and a section naming a record the room lacks, or
    /// holding another socket count than the room's, is refused as damaged.
    /// </summary>
    [Fact]
    public async Task WaterSocketsRoundTripThroughAPack()
    {
        RoomLibrary rooms = await CompileSocketsAsync(SocketLibrary());
        LevelGrid level = RoomPropHarness.Level("hub, other");
        byte[] expected = await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(rooms, level));

        using MemoryStream pack = new();
        List<RoomPackItem> items = [];
        foreach (RoomObject room in rooms.Rooms)
        {
            items.Add(await RoomPackItem.CreateAsync(room));
        }

        await RoomPack.SaveAsync(items, pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(pack, index, [new RoomPackRequest("hub", [0]), new RoomPackRequest("other", [0])]);
        Assert.Equal(64f, loaded[0].WaterOfCompile!.LevelAt(0));
        Assert.Null(loaded[0].WaterOfCompile!.LevelAt(1));
        Assert.Equal(expected, await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf([.. loaded]), level)));

        RoomObject hub = rooms.Get("hub");
        byte[] payload = hub.WaterOfCompile!.ToSection().Bytes.ToArray()[9..];
        int doors = DoorsAt(payload);
        payload[doors + 4 + 1 + 4 + 3] = 9; // socket 0's record
        LinkException damaged = Assert.Throws<LinkException>(() => RoomWater.Read(RoomLinkSections.Encode(payload, RoomLinkCodec.None), "hub", hub.Bsp));
        Assert.Equal("room pack entry \"hub\": its \"WATR\" section holds socket 0's water naming what the room does not have.", damaged.Message);

        RoomObject fewer = hub with { Definition = hub.Definition with { Sockets = [.. hub.Definition.Sockets.Take(3)] } };
        LinkException count = Assert.Throws<LinkException>(() => LevelLinker.RoomWaterOf(fewer));
        Assert.Equal("room pack entry \"hub\": its \"WATR\" section holds 4 sockets; the room has 3.", count.Message);
    }

    /// <summary>A level with water through its doors links to the same bytes at one thread and four, and its rooms pack so too.</summary>
    [Fact]
    public async Task WaterThroughDoorsIsTheSameBytesAtAnyThreadCount()
    {
        LevelGrid level = RoomPropHarness.Level("hub@90, hub@90", "other@90, other@90");
        async Task<(byte[] Pack, byte[] Map)> RunAsync(int degree)
        {
            VmfDocument library = SocketLibrary();
            RoomLibrary rooms = await CompileSocketsAsync(library, degree);
            using MemoryStream pack = new();
            List<RoomPackItem> items = [];
            foreach (RoomObject room in rooms.Rooms)
            {
                items.Add(await RoomPackItem.CreateAsync(room));
            }

            await RoomPack.SaveAsync(items, pack);
            return (pack.ToArray(), await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(rooms, level, degree)));
        }

        (byte[] pack, byte[] map) = await RunAsync(1);
        (byte[] pack4, byte[] map4) = await RunAsync(4);
        Assert.Equal(pack, pack4);
        Assert.Equal(map, map4);
    }

    // ---- lighting -------------------------------------------------------------------------------

    /// <summary>
    /// A lit level with water through a door links: the doorway's surfaces
    /// are unlit, as the room's own are, the level passes the loader's
    /// checks, and its lightmaps are each room's own bake (a water socket
    /// adds no luxel: its surfaces follow unlit faces).
    /// </summary>
    [Fact]
    public async Task ALitLevelWithAWaterDoorLinks()
    {
        VmfDocument library = SocketLibrary();
        library.Chunks.Add(VmfPlacement.MoveEntity(RoomLightHarness.Light(930, new Vec3(128, 128, 150)), QuarterTurn.Translation(Vec3.Zero)));
        RoomLightHarness.WorldAlign(library);
        RoomLibrary rooms = await CompileLitSocketsAsync(library);
        LinkedLevel linked = await RoomLightHarness.LinkAsync(rooms, RoomPropHarness.Level("hub, other"));

        Box doorway = Doorway(linked.Bsp);
        List<DFace> surfaces = [.. BspStructView.As<DFace>(linked.Bsp[BspLump.Faces]).ToArray()
            .Where(f => RoomHarness.FaceVertices(linked.Bsp, f).All(v => v.Z == SocketLevel && v.X >= doorway.Mins.X && v.X <= doorway.Maxs.X))];
        Assert.NotEmpty(surfaces);
        Assert.All(surfaces, f => Assert.NotEqual(0, BspStructView.As<TexInfo>(linked.Bsp[BspLump.TexInfo])[f.TexInfo].Flags & (int)SurfaceFlags.NoLight));
        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>The library's rooms compiled and lit, with their declared water sockets.</summary>
    private static async Task<RoomLibrary> CompileLitSocketsAsync(VmfDocument library)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        RoomLightingSettings settings = RoomLightHarness.Settings(split);
        foreach (LibraryRoom room in split.Rooms)
        {
            var context = await ContextAsync(room.Definition.Name);
            RoomObject compiledRoom = await RoomCompiler.CompileAsync(room.Document, room.Definition, context, null, room.WaterSockets, CancellationToken.None);
            compiled.Add(compiledRoom with
            {
                Lighting = await RoomLighting.BakeAsync(compiledRoom, settings, context.Content!, context.Parallelism, CancellationToken.None),
            });
        }

        return compiled;
    }

    /// <summary>
    /// The doorway of a two-room level's water joint, flat on its sill: the
    /// two plug boxes either side of the cell face between the grid's first
    /// two cells (x = 256 for a row, y = 256 for a column), where the
    /// doorway's water is.
    /// </summary>
    private static Box Doorway(BspData bsp)
    {
        const float face = RoomHarness.Cell;
        return At(bsp, new Vec3(face, 128, 30)).StartsWith("water", StringComparison.Ordinal)
            ? new Box(new Vec3(face - 16, 80, 16), new Vec3(face + 16, 176, 16))
            : new Box(new Vec3(80, face - 16, 16), new Vec3(176, face + 16, 16));
    }

    /// <summary>
    /// How much of a doorway's rectangle at a level faces of the water's
    /// surface cover, seen from above and from below: each face at that
    /// height naming a fog volume, clipped to the rectangle, its area summed
    /// by the way its winding faces.
    /// </summary>
    internal static (double Above, double Below) SurfaceCover(BspData bsp, Box doorway, float level)
    {
        double above = 0, below = 0;
        foreach (DFace face in BspStructView.As<DFace>(bsp[BspLump.Faces]))
        {
            if (face.SurfaceFogVolumeId < 0)
            {
                continue;
            }

            List<Vec3> corners = RoomHarness.FaceVertices(bsp, face);
            if (corners.Count < 3 || corners.Any(c => c.Z != level))
            {
                continue;
            }

            double minX = Math.Max(corners.Min(c => c.X), doorway.Mins.X), maxX = Math.Min(corners.Max(c => c.X), doorway.Maxs.X);
            double minY = Math.Max(corners.Min(c => c.Y), doorway.Mins.Y), maxY = Math.Min(corners.Max(c => c.Y), doorway.Maxs.Y);
            if (maxX <= minX || maxY <= minY)
            {
                continue;
            }

            double area = (maxX - minX) * (maxY - minY);
            if (Vec3.Cross(corners[1] - corners[0], corners[2] - corners[0]).Z < 0)
            {
                above += area;
            }
            else
            {
                below += area;
            }
        }

        return (above, below);
    }

    /// <summary>
    /// A map's fluids summed by surface: per plane and contents, the total
    /// volume and the extent of every convex, since the link keeps each
    /// room's fluid (the doorway's convexes joining one) where the flattened
    /// level's compile makes one of the body of water.
    /// </summary>
    private static List<string> FluidSummary(BspData bsp)
    {
        PhysCollideModel world = PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span)[0];
        List<(int Index, RoomWaterFluid Fluid)> fluids = [];
        _ = LevelLinker.ParseKeyData(world.KeyText, "map", fluids);
        Dictionary<string, (double Volume, Vec3 Min, Vec3 Max)> byPlane = new(StringComparer.Ordinal);
        foreach ((int index, RoomWaterFluid fluid) in fluids)
        {
            string key = string.Create(CultureInfo.InvariantCulture, $"{fluid.SurfaceProp} {fluid.Contents} {fluid.Normal} {fluid.Dist}");
            (double volume, Vec3 min, Vec3 max) = byPlane.TryGetValue(key, out var held)
                ? held
                : (0, new Vec3(float.MaxValue, float.MaxValue, float.MaxValue), new Vec3(float.MinValue, float.MinValue, float.MinValue));
            byte[] blob = world.Solids[index];
            volume += IvpCollideQueries.CollideVolume(blob);
            foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(blob)))
            {
                for (int p = 0; p < ledge.PointCount; p++)
                {
                    (float x, float y, float z) = IvpCollideQueries.HlPoint(ledge, p);
                    min = new Vec3(Math.Min(min.X, x), Math.Min(min.Y, y), Math.Min(min.Z, z));
                    max = new Vec3(Math.Max(max.X, x), Math.Max(max.Y, y), Math.Max(max.Z, z));
                }
            }

            byPlane[key] = (volume, min, max);
        }

        return [.. byPlane.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => string.Create(
            CultureInfo.InvariantCulture,
            $"{kv.Key} vol {kv.Value.Volume / 1000:0} ({Math.Round(kv.Value.Min.X, 1)} {Math.Round(kv.Value.Min.Y, 1)} {Math.Round(kv.Value.Min.Z, 1)}) ({Math.Round(kv.Value.Max.X, 1)} {Math.Round(kv.Value.Max.Y, 1)} {Math.Round(kv.Value.Max.Z, 1)})"))];
    }

    /// <summary>Where a water payload's sockets start: past the revision, the record count and the fluids.</summary>
    private static int DoorsAt(byte[] payload)
    {
        int at = 8;
        int fluids = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(at));
        at += 4;
        for (int f = 0; f < fluids; f++)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(at));
            at += 4 + length + 4 + 4 + 12 + 4;
        }

        return at;
    }
}
