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
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomPropHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Static props through the link (section 4.3 of the rooms design): a room's
/// props are moved with its placement at every quarter turn, their leaves
/// listed in the linked tree, kept or dropped by <c>room_needs</c> and the
/// socket furniture rule, their dictionaries merged, and the level's lump
/// agrees with the flattened level's vbsp compile; the pack carries what the
/// link needs, the output is a function of the level, and props cost the
/// level no entity.
/// </summary>
public sealed class LevelLinkerStaticPropsTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    private static readonly Vec3 BoxAt = new(64, 64, 16);
    private static readonly Vec3 PostAt = new(180, 60, 16);
    private static readonly Vec3 LampAt = new(100, 100, 100);

    /// <summary>The hub's everyday props: a turned box, and a post lit from a room-local <c>info_lighting</c>.</summary>
    private static (int, VmfChunk)[] HubProps =>
    [
        (0, Prop(700, BoxModel, BoxAt, "0 30 0", ("skin", "1"), ("solid", "6"), ("fademindist", "400"), ("fademaxdist", "800"))),
        (0, Prop(701, PostModel, PostAt, "0 0 0", ("lightingorigin", "cxry_lamp"), ("disableshadows", "1"))),
        (0, Entity("info_lighting", 702, LampAt, ("targetname", "cxry_lamp"))),
    ];

    // ---- placement, turned -------------------------------------------------------------------

    /// <summary>
    /// A level of a hub with two props and another room with one: the linked
    /// lump holds exactly those three props, in link order, each moved with
    /// its placement (origin and lighting origin through the placement's
    /// transform, the yaw turned by 90 degrees a quarter turn, the rest of
    /// the record as compiled), the dictionary merged with each model once in
    /// the order a prop first names it; and the flattened level's compile
    /// holds the same props.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelHoldsExactlyItsRoomsPropsMovedAtEveryRotation(int rotation)
    {
        VmfDocument library = Library([.. HubProps, (1, Prop(710, BoxModel, new Vec3(128, 40, 16), "0 45 0"))]);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = Level($"hub@{rotation}, other@{rotation}");
        LinkedLevel linked = await LinkAsync(rooms, level);

        StaticPropLump lump = Props(linked.Bsp);
        Assert.Equal([BoxModel, PostModel], lump.ModelNames);
        Assert.Equal(3, lump.Props.Count);
        int turns = rotation / 90;
        RoomTransform hub = new(new RoomPlacement("hub", 0, 0, turns), RoomHarness.Cell);
        RoomTransform other = new(new RoomPlacement("other", 1, 0, turns), RoomHarness.Cell);

        StaticProp box = lump.Props[0];
        Assert.Equal(hub.Apply(BoxAt), box.Origin);
        Assert.Equal(new Vec3(0, (30 + rotation) % 360, 0), box.Angles);
        Assert.Equal(0, box.PropType);
        Assert.Equal(1, box.Skin);
        Assert.Equal(6, box.Solid);
        Assert.Equal(400, box.FadeMinDist);
        Assert.Equal(800, box.FadeMaxDist);
        Assert.False(RoomStaticProps.HasLightingOrigin(box));

        StaticProp post = lump.Props[1];
        Assert.Equal(hub.Apply(PostAt), post.Origin);
        Assert.Equal(new Vec3(0, rotation, 0), post.Angles);
        Assert.Equal(1, post.PropType);
        Assert.True(RoomStaticProps.HasLightingOrigin(post));
        Assert.Equal(hub.Apply(LampAt), post.LightingOrigin);

        StaticProp third = lump.Props[2];
        Assert.Equal(other.Apply(new Vec3(128, 40, 16)), third.Origin);
        Assert.Equal(new Vec3(0, (45 + rotation) % 360, 0), third.Angles);
        Assert.Equal(0, third.PropType);

        Assert.All(lump.Props, p => Assert.True(p.LeafCount > 0));
        Assert.Equal(lump.Props.Sum(p => p.LeafCount), lump.LeafEntries.Count);
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), Observed(linked.Bsp));
    }

    // ---- leaves -------------------------------------------------------------------------------

    /// <summary>
    /// A prop's leaves are the leaves its hull touches in the linked tree
    /// (the rooms design's hull leaf walk): the list equals a walk of the
    /// linked tree with the hull built from the model in the game's content
    /// (not the pack's copy); an interior prop's list is its room's own
    /// list rebased; and a prop reaching into a plug box lists the doorway
    /// leaf the carve made where the socket is jointed and nothing of the
    /// solid plug where it is capped.
    /// </summary>
    [Fact]
    public async Task APropsLeavesAreItsHullsLeavesInTheLinkedTree()
    {
        Vec3 byDoor = new(236, 128, 100);
        VmfDocument library = Library(
            (0, Prop(700, BoxModel, BoxAt, "0 30 0")),
            (0, Prop(701, BoxModel, byDoor)));
        RoomLibrary rooms = await CompileAsync(library);
        LinkedLevel linked = await LinkAsync(rooms, Level("hub, hub"));
        StaticPropLump lump = Props(linked.Bsp);
        Assert.Equal(4, lump.Props.Count);

        // The walk, with the hull from the content.
        BspTreeView tree = BspTreeView.FromBsp(linked.Bsp);
        List<Vec3[]> meshes = (await StaticPropEmitter.LoadMeshesAsync(await ContextAsync(), BoxModel, [], CancellationToken.None))!;
        IStaticPropHull hull = (await new ManagedStaticPropCollision().BuildHullAsync(meshes))!;
        foreach (StaticProp prop in lump.Props)
        {
            List<ushort> walked = await StaticPropLeaves.ComputeAsync(tree, hull, prop.Origin, prop.Angles);
            Assert.Equal([.. walked.Select(l => (int)l)], LeavesOf(lump, prop));
        }

        // The interior prop of the first placement: its room's own list,
        // shifted past the shared solid leaf.
        RoomObject hub = rooms.Get("hub");
        StaticPropLump own = Props(hub.Bsp);
        Assert.Equal([.. LeavesOf(own, own.Props[0]).Select(l => l + 1)], LeavesOf(lump, lump.Props[0]));

        // By the east door: jointed in the first placement, capped in the second.
        RoomDefinition definition = hub.Definition;
        int jointed = LevelLinker.PointInLeaf(linked.Bsp, RoomHarness.PlugCentre(definition, new RoomPlacement("hub", 0, 0, 0), "east"));
        Assert.Equal(0, BspStructView.As<DLeaf>(linked.Bsp[BspLump.Leafs])[jointed].Contents);
        Assert.Contains(jointed, LeavesOf(lump, lump.Props[1]));
        int capped = LevelLinker.PointInLeaf(linked.Bsp, RoomHarness.PlugCentre(definition, new RoomPlacement("hub", 1, 0, 0), "east"));
        Assert.NotEqual(0, BspStructView.As<DLeaf>(linked.Bsp[BspLump.Leafs])[capped].Contents & (int)BrushContents.Solid);
        Assert.DoesNotContain(capped, LeavesOf(lump, lump.Props[3]));
        int leafBase = 1 + BspStructView.Count<DLeaf>(hub.Bsp[BspLump.Leafs]);
        Assert.Equal([.. LeavesOf(own, own.Props[1]).Select(l => l + leafBase)], LeavesOf(lump, lump.Props[3]));
    }

    // ---- socket furniture and room_needs -------------------------------------------------------

    /// <summary>
    /// Socket furniture (open point O5): a door frame reaching through the
    /// hub's east doorway is kept at the joint, where the other room's frame
    /// on the same doorway is dropped (the earlier room in link order wins a
    /// tie), and lists both rooms' doorway leaves; a higher
    /// <c>socket_priority</c> on the other side wins instead; at a cap the
    /// furniture is dropped. The flattened level keeps the same pieces.
    /// </summary>
    [Fact]
    public async Task SocketFurnitureIsKeptOnOneSideOfAJointAndDroppedAtACap()
    {
        Vec3 frame = new(236, 128, 100);
        VmfChunk hubFrame = Prop(700, BarModel, frame, "0 0 0", (RoomStaticProps.SocketKey, "east"));
        VmfChunk otherFrame = Prop(710, BarModel, new Vec3(20, 128, 100), "0 0 0", (RoomStaticProps.SocketKey, "west"));

        VmfDocument library = Library((0, hubFrame), (1, otherFrame));
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = Level("hub, other");
        LinkedLevel linked = await LinkAsync(rooms, level);
        StaticPropLump lump = Props(linked.Bsp);
        StaticProp kept = Assert.Single(lump.Props);
        Assert.Equal(frame, kept.Origin);
        int mine = LevelLinker.PointInLeaf(linked.Bsp, new Vec3(250, 128, 116));
        int theirs = LevelLinker.PointInLeaf(linked.Bsp, new Vec3(258, 128, 116));
        Assert.NotEqual(mine, theirs);
        Assert.Contains(mine, LeavesOf(lump, kept));
        Assert.Contains(theirs, LeavesOf(lump, kept));
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), Observed(linked.Bsp));

        // The other side's higher priority takes the doorway.
        otherFrame.AddKey(RoomStaticProps.PriorityKey, "5");
        library = Library((0, hubFrame), (1, otherFrame));
        rooms = await CompileAsync(library);
        linked = await LinkAsync(rooms, level);
        Assert.Equal(new Vec3(RoomHarness.Cell + 20, 128, 100), Assert.Single(Props(linked.Bsp).Props).Origin);
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), Observed(linked.Bsp));

        // Alone, every socket is capped: nothing is kept, and the lump is empty.
        linked = await LinkAsync(rooms, Level("hub"));
        Assert.Empty(Props(linked.Bsp).Props);
        Assert.Empty(Props(linked.Bsp).ModelNames);
        Assert.Empty(Props(linked.Bsp).LeafEntries);
    }

    /// <summary>
    /// <c>room_needs</c> on a static prop (the (c) mechanism, 5.8): the prop
    /// is kept where its condition holds for the placement, in the room's
    /// authored frame turned with it, and dropped elsewhere, in the link and
    /// the flatten alike.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task RoomNeedsKeepsOrDropsAPropAtEveryRotation(int rotation)
    {
        VmfDocument library = Library(
            (0, Prop(700, BoxModel, BoxAt)),
            (0, Prop(701, PostModel, PostAt, "0 0 0", (RoomNeeds.Key, "east"), ("disableshadows", "1"))),
            (0, Prop(702, PostModel, new Vec3(128, 180, 16), "0 0 0", (RoomNeeds.Key, "!joined_east"), ("disableshadows", "1"))));
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = Level($"hub@{rotation}, other");
        LinkedLevel linked = await LinkAsync(rooms, level);

        // The hub's authored east is world east only unturned, and only then
        // does a room stand there and a door join it.
        StaticPropLump lump = Props(linked.Bsp);
        Assert.Equal(2, lump.Props.Count);
        Assert.Equal(PostModel, lump.ModelNames[lump.Props[1].PropType]);
        RoomTransform hub = new(new RoomPlacement("hub", 0, 0, rotation / 90), RoomHarness.Cell);
        Assert.Equal(hub.Apply(rotation == 0 ? PostAt : new Vec3(128, 180, 16)), lump.Props[1].Origin);
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), Observed(linked.Bsp));
    }

    /// <summary>
    /// Every kind of prop at once, at every rotation of both rooms: the link
    /// and the flattened compile hold the same props (the rooms design, 5.9:
    /// link and flatten agree).
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task LinkAndFlattenHoldTheSamePropsAtEveryRotation(int rotation)
    {
        VmfDocument library = Library(
        [
            .. HubProps,
            (0, Prop(703, BarModel, new Vec3(236, 128, 100), "0 0 0", (RoomStaticProps.SocketKey, "east"))),
            (0, Prop(704, PostModel, new Vec3(40, 200, 16), "0 0 0", (RoomNeeds.Key, "west"), ("disableshadows", "1"))),
            (1, Prop(710, BoxModel, new Vec3(128, 40, 16), "-10 45 5")),
            (1, Prop(711, BarModel, new Vec3(20, 128, 100), "0 180 0", (RoomStaticProps.SocketKey, "west"))),
        ]);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = Level($"hub@{rotation}, other@{(rotation + 180) % 360}", $"other@{rotation}, hub");
        LinkedLevel linked = await LinkAsync(rooms, level);
        List<string> props = Observed(linked.Bsp);
        Assert.InRange(props.Count, 6, 12);
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), props);
    }

    // ---- the pack, determinism, the budget --------------------------------------------------

    /// <summary>
    /// Rooms with props through a pack: the pack stores each room's props
    /// (four turns of poses), the rooms it loads carry them, and the level
    /// links to the same bytes as from the rooms in memory; a pack holding
    /// only turn 0 of the poses (the rotation count of 1, the link turning
    /// them) links to the same bytes too, so storing once or four times is a
    /// choice of speed alone (the rooms design, 15.9).
    /// </summary>
    [Fact]
    public async Task PropsRoundTripThroughAPack()
    {
        RoomLibrary rooms = await CompileAsync(Library([.. HubProps, (1, Prop(710, BoxModel, new Vec3(128, 40, 16), "0 45 0"))]));
        LevelGrid level = Level("hub@90, other@180", "other, hub@270");
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
        Assert.NotNull(index.Find("hub")!.Find(RoomStaticProps.SectionTag));
        Assert.NotNull(index.Find("other")!.Find(RoomStaticProps.SectionTag));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            pack, index, [new RoomPackRequest("hub", [1, 3]), new RoomPackRequest("other", [0, 2])]);
        RoomLibrary fromPack = RoomsOf([.. loaded]);
        Assert.All(loaded, room => Assert.Equal(4, room.StaticProps!.TurnCount));
        Assert.Equal(expected, await BytesAsync(await LinkAsync(fromPack, level)));

        RoomLibrary once = RoomsOf([.. rooms.Rooms.Select(r => r with { Props = r.Props!.WithTurnZeroOnly() })]);
        Assert.All(once.Rooms, room => Assert.Equal(1, room.StaticProps!.TurnCount));
        Assert.Equal(expected, await BytesAsync(await LinkAsync(once, level)));
    }

    /// <summary>A level with props links to the same bytes at one thread and at many, run after run.</summary>
    [Fact]
    public async Task ALevelWithPropsIsTheSameBytesAtAnyThreadCount()
    {
        RoomLibrary rooms = await CompileAsync(Library([.. HubProps, (0, Prop(703, BarModel, new Vec3(236, 128, 100), "0 0 0", (RoomStaticProps.SocketKey, "east")))]));
        LevelGrid level = Level("hub@90, other, hub@180", "other@270, hub, other");
        byte[] serial = await BytesAsync(await LinkAsync(rooms, level, 1));
        Assert.Equal(serial, await BytesAsync(await LinkAsync(rooms, level, 8)));
        Assert.Equal(serial, await BytesAsync(await LinkAsync(rooms, level, 1)));
        Assert.Equal(serial, await BytesAsync(await LinkAsync(rooms, level, 8)));
    }

    /// <summary>
    /// Static props cost a level no entity (the rooms design, 6: a static
    /// prop is a record): a level of rooms with props has the same entity
    /// lump and the same budget as the level of the same rooms without them,
    /// and every entity of its lump is budgeted.
    /// </summary>
    [Fact]
    public async Task PropsLeaveTheEntityBudgetUnchanged()
    {
        LevelGrid level = Level("hub, other", "other@90, hub@270");
        LinkedLevel bare = await LinkAsync(await CompileAsync(Library()), level);
        LinkedLevel furnished = await LinkAsync(await CompileAsync(Library([.. HubProps, (1, Prop(710, BoxModel, new Vec3(128, 40, 16)))])), level);

        Assert.NotEmpty(Props(furnished.Bsp).Props);
        Assert.Equal(bare.Bsp[BspLump.Entities].Data.ToArray(), furnished.Bsp[BspLump.Entities].Data.ToArray());
        Assert.Equal(bare.EntityBudget!.Edicts, furnished.EntityBudget!.Edicts);
        Assert.Equal(bare.EntityBudget.Listed, furnished.EntityBudget.Listed);
        Assert.Equal(EntityLump.Parse(furnished.Bsp[BspLump.Entities]).Count, furnished.EntityBudget.Listed);
    }

    /// <summary>A linked level with props passes the loader checks <c>ssmap check</c> makes (dictionary indices, leaf runs).</summary>
    [Fact]
    public async Task ALinkedLevelWithPropsPassesTheLoaderChecks()
    {
        RoomLibrary rooms = await CompileAsync(Library([.. HubProps, (1, Prop(710, BoxModel, new Vec3(128, 40, 16)))]));
        LinkedLevel linked = await LinkAsync(rooms, Level("hub@90, other", "other, hub@180"));
        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    // ---- the pak ---------------------------------------------------------------------------

    /// <summary>
    /// A room's static prop lighting files (vrad's <c>sp_N.vhv</c> and
    /// <c>sp_hdr_N.vhv</c>) are written under each kept placement's linked
    /// prop index, the room's bytes unchanged, and a dropped prop's file is
    /// left out; any other file keeps its name.
    /// </summary>
    [Fact]
    public async Task PropLightingFilesTakeTheLinkedPropIndices()
    {
        RoomLibrary compiled = await CompileAsync(Library(
            (0, Prop(700, BoxModel, BoxAt)),
            (0, Prop(701, PostModel, PostAt, "0 0 0", (RoomNeeds.Key, "!east"), ("disableshadows", "1")))));
        RoomObject hub = compiled.Get("hub");
        ZipArchiveWriter zip = new();
        zip.Add("sp_0.vhv", [1]);
        zip.Add("sp_hdr_0.vhv", [2]);
        zip.Add("sp_1.vhv", [3]);
        zip.Add("materials/unit/kept.vmt", [4]);
        BspData bsp = new() { FileVersion = hub.Bsp.FileVersion, MapRevision = hub.Bsp.MapRevision };
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            bsp[i] = hub.Bsp[i];
        }

        bsp.GameLumps.AddRange(hub.Bsp.GameLumps);
        bsp.SetLump(BspLump.PakFile, zip.ToBytes());
        RoomObject packed = hub with { Bsp = bsp, Compiled = bsp, Props = hub.Props!.For(bsp) };

        // hub at (0,0) has a room east of it, so its post is dropped; the
        // hub at (1,0) keeps both: linked props 0 (box), 1 (box), 2 (post).
        LinkedLevel linked = await LinkAsync(RoomsOf(packed), Level("hub, hub"));
        Assert.Equal(3, Props(linked.Bsp).Props.Count);
        ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(linked.Bsp[BspLump.PakFile].Data);
        Assert.Equal(
            ["materials/unit/kept.vmt", "sp_0.vhv", "sp_1.vhv", "sp_2.vhv", "sp_hdr_0.vhv", "sp_hdr_1.vhv"],
            pak.Entries.Select(e => e.Name));
        Assert.Equal([1], pak.Entries.Single(e => e.Name == "sp_1.vhv").Data);
        Assert.Equal([3], pak.Entries.Single(e => e.Name == "sp_2.vhv").Data);
        Assert.Equal([2], pak.Entries.Single(e => e.Name == "sp_hdr_1.vhv").Data);
    }

    // ---- refusals at pack time -------------------------------------------------------------

    /// <summary>
    /// The pack-time refusals, each with its exact text (the rooms design,
    /// 15.4): a prop whose hull leaves the cell (O6), socket furniture that
    /// reaches past its doorway's depth or beside its opening, a prop that
    /// asks for texel lighting (O13), and a furniture key that names no
    /// socket or no number.
    /// </summary>
    [Theory]
    [InlineData(BarModel, 236f, 128f, 100f, null, null, "room hub: prop_static 700 (models/props_test/bar.mdl) reaches 4 units outside the cell; props stay in their cell except as socket furniture.")]
    [InlineData(BeamModel, 236f, 128f, 100f, "east", null, "room hub: prop_static 700 (models/props_test/beam.mdl) reaches 20 units outside the cell; props stay in their cell except as socket furniture.")]
    [InlineData(BarModel, 236f, 60f, 100f, "east", null, "room hub: prop_static 700 (models/props_test/bar.mdl) reaches 4 units outside the cell; props stay in their cell except as socket furniture.")]
    [InlineData(BoxModel, 128f, 128f, 230f, null, null, "room hub: prop_static 700 (models/props_test/box.mdl) reaches 6 units outside the cell; props stay in their cell except as socket furniture.")]
    [InlineData(BarModel, 20f, 128f, 100f, "east", null, "room hub: prop_static 700 (models/props_test/bar.mdl) reaches 4 units outside the cell; props stay in their cell except as socket furniture.")]
    [InlineData(BoxModel, 128f, 128f, 16f, null, "generatelightmaps", "room hub: prop_static 700 asks for texel lighting, which this vrad does not bake.")]
    [InlineData(BoxModel, 128f, 128f, 16f, "up", null, "room hub: prop_static 700 has room_socket \"up\", which is not a socket of the room.")]
    [InlineData(BoxModel, 128f, 128f, 16f, null, "priority", "room hub: prop_static 700 has socket_priority \"high\", which is not a whole number.")]
    public async Task APropTheLinkCannotCarryIsRefusedAtPackTime(string model, float x, float y, float z, string? socket, string? extra, string message)
    {
        List<(string, string)> keys = [];
        if (socket is not null)
        {
            keys.Add((RoomStaticProps.SocketKey, socket));
        }

        if (extra == "generatelightmaps")
        {
            keys.Add(("generatelightmaps", "1"));
        }
        else if (extra == "priority")
        {
            keys.Add((RoomStaticProps.SocketKey, "east"));
            keys.Add((RoomStaticProps.PriorityKey, "high"));
        }

        VmfDocument library = Library((0, Prop(700, model, new Vec3(x, y, z), "0 0 0", [.. keys])));
        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => CompileAsync(library));
        Assert.Equal(message, refused.Message);
    }

    /// <summary>
    /// Socket furniture whose part outside the cell lies in the doorway
    /// beyond its socket is accepted, however it is turned, and so is a
    /// prop touching the cell's face from inside.
    /// </summary>
    [Theory]
    [InlineData(BarModel, 236f, 128f, 100f, "0 0 0", "east")]
    [InlineData(BarModel, 236f, 128f, 100f, "0 180 0", "east")]
    [InlineData(BarModel, 128f, 236f, 100f, "0 90 0", "north")]
    [InlineData(BarModel, 20f, 128f, 100f, "0 0 0", "west")]
    [InlineData(BoxModel, 232f, 128f, 100f, "0 0 0", null)]
    public async Task FurnitureInItsDoorwayAndPropsInTheirCellAreAccepted(string model, float x, float y, float z, string angles, string? socket)
    {
        VmfDocument library = Library((0, Prop(700, model, new Vec3(x, y, z), angles,
            socket is null ? [] : [(RoomStaticProps.SocketKey, socket)])));
        RoomObject hub = (await CompileAsync(library)).Get("hub");
        RoomStaticProps props = hub.StaticProps!;
        RoomProp prop = Assert.Single(props.Props);
        Assert.Equal(700, prop.Id);
        Assert.Equal(socket is null ? -1 : hub.Definition.Sockets.ToList().FindIndex(s => s.Name == socket), prop.Socket);
    }

    /// <summary>A room whose props vbsp dropped (a model that does not load) carries no prop data, and links.</summary>
    [Fact]
    public async Task ARoomWhosePropsVbspDroppedCarriesNone()
    {
        RoomLibrary rooms = await CompileAsync(Library((0, Prop(700, "models/props_test/missing.mdl", BoxAt))));
        RoomObject hub = rooms.Get("hub");
        Assert.Null(hub.Props);
        Assert.Empty(Props(hub.Bsp).Props);
        LinkedLevel linked = await LinkAsync(rooms, Level("hub"));
        Assert.Equal(hub.Bsp.GameLumps.Select(g => g.Data.ToArray()), linked.Bsp.GameLumps.Select(g => g.Data.ToArray()));
    }

    private static async Task<byte[]> BytesAsync(LinkedLevel linked)
    {
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }
}
