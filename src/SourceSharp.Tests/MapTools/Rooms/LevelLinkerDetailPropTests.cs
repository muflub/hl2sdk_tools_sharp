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
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;
using Xunit.Abstractions;

using static SourceSharp.Tests.MapTools.Rooms.RoomDetailHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Detail props through the link (section 4.4 of the rooms design): every
/// placed room's detail props are carried, moved and turned with the room
/// at every quarter turn, their dictionaries merged, their leaves the linked
/// tree's, re-sorted by leaf; exactly the room's own props, prop for prop,
/// and against the flattened level's compile (another random draw) the same
/// props in distribution.
/// </summary>
public sealed class LevelLinkerDetailPropTests(ITestOutputHelper output)
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// The exact test (the rooms design, 4.4): a room placed alone, at every
    /// quarter turn, links to its own compile's detail props prop for prop,
    /// in the room's order, each moved and turned as the flatten moves an
    /// entity (its origin turned then moved, a zero unsigned; its yaw turned
    /// and kept in [0, 360), its pitch and roll unsigned), its dictionary
    /// entry the same model or sprite, every other field the room's; its
    /// leaf the one the linked tree puts its origin in, a leaf of the room's
    /// own cluster there; and <c>ssmap check</c> finds no error.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ARoomAloneLinksToItsOwnDetailPropsAtEveryRotation(int rotation)
    {
        RoomLibrary rooms = await CompileAsync(Library());
        RoomObject hub = rooms.Get("hub");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level($"hub@{rotation}"));

        DetailPropLump own = Lump(hub.Bsp);
        DetailPropLump lump = Lump(linked.Bsp);
        Assert.True(own.Props.Count > 100, $"the hub has {own.Props.Count} detail props");
        Assert.Equal(own.ModelNames, lump.ModelNames);
        Assert.Equal(own.Sprites.Select(Sprite), lump.Sprites.Select(Sprite));

        int turns = rotation / 90;
        RoomTransform transform = new(RoomPropHarness.Layout(rooms, RoomPropHarness.Level($"hub@{rotation}")).Rooms[0].Placement, RoomHarness.Cell);
        List<string> expected = [];
        foreach (DetailObjectLump p in own.Props)
        {
            DetailObjectLump moved = p;
            moved.Origin = RoomStaticProps.Unsigned(transform.Translate(RoomTransform.Rotate(p.Origin, turns)));
            moved.Angles = turns == 0 ? p.Angles : new Vec3(RoomStaticProps.Unsigned(p.Angles.X), LevelLinker.TurnYaw(p.Angles.Y, turns), RoomStaticProps.Unsigned(p.Angles.Z));
            expected.Add(Line(own, moved, leaf: false));
        }

        Assert.Equal(expected, lump.Props.Select(p => Line(lump, p, leaf: false)));

        BspTreeView tree = BspTreeView.FromBsp(linked.Bsp);
        DLeaf[] leaves = BspStructView.As<DLeaf>(linked.Bsp[BspLump.Leafs]).ToArray();
        DLeaf[] roomLeaves = BspStructView.As<DLeaf>(hub.Bsp[BspLump.Leafs]).ToArray();
        for (int i = 0; i < lump.Props.Count; i++)
        {
            Assert.Equal(tree.LeafOf(lump.Props[i].Origin), lump.Props[i].Leaf);
            Assert.Equal(roomLeaves[own.Props[i].Leaf].Cluster, leaves[lump.Props[i].Leaf].Cluster);
        }

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// A level of both rooms, each placed several times at mixed turns: the
    /// level's props are every placement's own, moved (the multiset of
    /// each placement's lines is its room's, moved), sorted by leaf with the
    /// placements' order kept among equal leaves; the dictionaries merged
    /// with each model and sprite once, in the order the placements bring
    /// them; each prop's leaf the linked tree's.
    /// </summary>
    [Fact]
    public async Task EveryPlacementBringsItsOwnDetailProps()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        LevelGrid level = RoomPropHarness.Level("hub@90, other, hub@180", "other@270, hub, other@90");
        LevelLayout layout = RoomPropHarness.Layout(rooms, level);
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        DetailPropLump lump = Lump(linked.Bsp);

        List<string> expected = [];
        List<string> models = [];
        List<string> sprites = [];
        foreach (RoomInstance instance in layout.Rooms)
        {
            RoomObject room = rooms.Get(instance.Placement.Room);
            DetailPropLump own = Lump(room.Bsp);
            int turns = instance.Placement.NormalizedRotation;
            RoomTransform transform = new(instance.Placement, RoomHarness.Cell);
            foreach (DetailObjectLump p in own.Props)
            {
                DetailObjectLump moved = p;
                moved.Origin = RoomStaticProps.Unsigned(transform.Translate(RoomTransform.Rotate(p.Origin, turns)));
                moved.Angles = turns == 0 ? p.Angles : new Vec3(RoomStaticProps.Unsigned(p.Angles.X), LevelLinker.TurnYaw(p.Angles.Y, turns), RoomStaticProps.Unsigned(p.Angles.Z));
                expected.Add(Line(own, moved, leaf: false));
            }

            models.AddRange(own.ModelNames.Where(m => !models.Contains(m)));
            sprites.AddRange(own.Sprites.Select(Sprite).Where(s => !sprites.Contains(s)));
        }

        Assert.Equal(models, lump.ModelNames);
        Assert.Equal(sprites, lump.Sprites.Select(Sprite));
        Assert.Equal(expected.Order(StringComparer.Ordinal), lump.Props.Select(p => Line(lump, p, leaf: false)).Order(StringComparer.Ordinal));

        BspTreeView tree = BspTreeView.FromBsp(linked.Bsp);
        Assert.All(lump.Props, p => Assert.Equal(tree.LeafOf(p.Origin), p.Leaf));
        Assert.True(lump.Props.Zip(lump.Props.Skip(1)).All(pair => pair.First.Leaf <= pair.Second.Leaf), "the props are sorted by leaf");

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// Against the flattened level's compile (the rooms design, 4.4 and
    /// 15.2: per room and dictionary entry, the random draw differs because
    /// the flatten renumbers face ids and a whole-map compile cuts faces
    /// differently): the same dictionary; per room and entry, the same count
    /// within the draw's noise; every prop on the surface it grows on, in the
    /// flattened map as in the link; and the props spread over their
    /// surface alike (each room's props, per quadrant of its grass, within
    /// the draw's noise), at every quarter turn. The detail entities are no
    /// draw: their records are the same bits in both maps. The noise bound
    /// is four standard deviations of the difference of two counts plus
    /// four (a face's triangles each lose a fraction of a prop to rounding);
    /// the whole comparison is deterministic, since both compiles are seeded.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelMatchesItsFlattenedCompileInDistribution(int rotation)
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{(rotation + 90) % 360}");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        DetailPropLump a = Lump(linked.Bsp);
        DetailPropLump b = Lump(flat);
        Assert.Equal(a.ModelNames.Order(StringComparer.Ordinal), b.ModelNames.Order(StringComparer.Ordinal));
        Assert.Equal(a.Sprites.Select(Sprite).Order(StringComparer.Ordinal), b.Sprites.Select(Sprite).Order(StringComparer.Ordinal));

        Dictionary<string, int> linkedCounts = Census(a);
        Dictionary<string, int> flatCounts = Census(b);
        Assert.Equal(linkedCounts.Keys.Order(StringComparer.Ordinal), flatCounts.Keys.Order(StringComparer.Ordinal));
        foreach ((string key, int count) in linkedCounts)
        {
            int other = flatCounts[key];
            double noise = 4 * Math.Sqrt(count + other) + 4;
            output.WriteLine($"turn {rotation} {key}: link {count}, flat {other}");
            Assert.True(Math.Abs(count - other) <= noise, $"{key}: {count} linked against {other} flattened");
        }

        // Every prop stands on its surface, in both maps, and each surface's
        // props spread over it alike: per quadrant of each grass box (in its
        // room's frame), the same count within the draw's noise.
        LevelLayout layout = RoomPropHarness.Layout(rooms, level);
        Dictionary<string, int>[] quadrants = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
        foreach ((DetailPropLump map, Dictionary<string, int> counts) in new[] { (a, quadrants[0]), (b, quadrants[1]) })
        {
            foreach (DetailObjectLump p in map.Props)
            {
                string? where = Where(layout, p.Origin);
                Assert.True(where is not null, $"a prop at {p.Origin} off the grass");
                counts[where!] = counts.GetValueOrDefault(where!) + 1;
            }
        }

        Assert.Equal(quadrants[0].Keys.Order(StringComparer.Ordinal), quadrants[1].Keys.Order(StringComparer.Ordinal));

        // A detail entity is not a draw: the flattened compile writes the
        // moved entity's record, and the link the same record bit for bit
        // (its pose turned as the flatten turns the entity's keys).
        string[] EntityProps(DetailPropLump map) =>
            [.. map.Props.Where(p => Where(layout, p.Origin)!.Contains("entity", StringComparison.Ordinal)).Select(p => Line(map, p, leaf: false, lighting: false)).Order(StringComparer.Ordinal)];
        Assert.Equal(3, EntityProps(a).Length);
        Assert.Equal(EntityProps(b), EntityProps(a));
        foreach ((string key, int count) in quadrants[0])
        {
            int other = quadrants[1][key];
            output.WriteLine($"turn {rotation} {key}: link {count}, flat {other}");
            Assert.True(Math.Abs(count - other) <= (4 * Math.Sqrt(count + other)) + 4, $"{key}: {count} linked against {other} flattened");
        }
    }

    /// <summary>
    /// A room whose detail prop lump has content but that carries no detail
    /// prop data from its compile (a pack written before detail props were
    /// carried) is refused by name at link; the old refusal by game lump id
    /// is gone.
    /// </summary>
    [Fact]
    public async Task ARoomWithDetailPropsButNoDetailDataIsRefused()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        RoomObject bare = RoomHarness.WithLumps(rooms.Get("hub"), _ => { });
        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf(bare), RoomPropHarness.Level("hub")));
        Assert.Equal(
            "room hub has detail props but no detail prop data from its compile (a pack written before the link carried detail props,"
            + " or a room built without ssmap room); recompile the library with ssmap room.",
            refused.Message);
        Assert.DoesNotContain("game lump", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A detail prop entity with <c>room_needs</c> is refused when its room is
    /// compiled: vbsp consumes the entity into the detail prop lump, where no
    /// condition survives, so the link would keep what the flattened compile
    /// drops.
    /// </summary>
    [Theory]
    [InlineData("prop_detail")]
    [InlineData("detail_prop")]
    [InlineData("prop_detail_sprite")]
    public async Task ADetailPropWithRoomNeedsIsRefused(string className)
    {
        VmfChunk entity = className == "prop_detail_sprite" ? DetailSprite(9200, new Vec3(120, 120, 16)) : DetailProp(9200, new Vec3(120, 120, 16));
        entity.Keys.Single(k => k.Name == "classname").Value = className;
        entity.AddKey("room_needs", "east");
        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => CompileAsync(Library(entities: [(0, entity)])));
        Assert.Equal(
            $"room hub: entity 9200 ({className}) has room_needs, but a detail prop is built into its room's compile and cannot be dropped.",
            refused.Message);
    }

    /// <summary>
    /// Rooms with detail props through a pack: the pack stores a room's
    /// detail props (four turns) in their own section, the rooms it loads
    /// carry them, and the level links to the same bytes as from the rooms
    /// in memory; a pack holding only turn 0 (the rotation count of 1, the
    /// link turning them) links to the same bytes too, so storing once or
    /// four times is a choice of speed alone. A room without detail props
    /// gets no section.
    /// </summary>
    [Fact]
    public async Task DetailPropsRoundTripThroughAPack()
    {
        RoomLibrary rooms = await CompileAsync(Library(entities: []));
        LevelGrid level = RoomPropHarness.Level("hub@90, other@180", "other, hub@270");
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
        Assert.NotNull(index.Find("hub")!.Find(RoomDetailProps.SectionTag));
        Assert.Null(index.Find("hub")!.Find(RoomDetailLighting.SectionTag));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            pack, index, [new RoomPackRequest("hub", [1, 3]), new RoomPackRequest("other", [0, 2])]);
        Assert.Equal(4, loaded[0].DetailPropsOfCompile!.TurnCount);
        Assert.Equal(expected, await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf([.. loaded]), level)));

        RoomLibrary once = RoomPropHarness.RoomsOf(
            [.. rooms.Rooms.Select(r => r.DetailProps is { } d ? r with { DetailProps = d.WithTurnZeroOnly() } : r)]);
        Assert.Equal(1, once.Get("hub").DetailPropsOfCompile!.TurnCount);
        Assert.Equal(expected, await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(once, level)));

        RoomObject bare = (await CompileAsync(Library([], []))).Get("hub");
        Assert.Null(bare.DetailPropsOfCompile);
        Assert.DoesNotContain((await RoomPackItem.CreateAsync(bare)).Extra, e => e.Tag == RoomDetailProps.SectionTag);
    }

    /// <summary>
    /// A level linked from rooms with detail props is the same bytes at one
    /// thread and at many, run after run (the rooms design, 15.5).
    /// </summary>
    [Fact]
    public async Task LevelsWithDetailPropsAreTheSameBytesAtAnyThreadCount()
    {
        RoomLibrary library = await CompileAsync(Library());
        LevelGrid level = RoomPropHarness.Level("hub@90, other, hub@180", "other@270, hub, other");
        byte[] linked = await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(library, level, 1));
        Assert.Equal(linked, await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(library, level, 8)));
        Assert.Equal(linked, await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(library, level, 1)));
    }

    /// <summary>
    /// Detail props cost the level no entity (the rooms design, 15.6): a
    /// level with them has the entity lump and budget of the same level
    /// without them (<c>prop_detail</c> and <c>prop_detail_sprite</c> are
    /// consumed by vbsp).
    /// </summary>
    [Fact]
    public async Task DetailPropsCostNoEntity()
    {
        LevelGrid level = RoomPropHarness.Level("hub@90, other");
        LinkedLevel bare = await RoomPropHarness.LinkAsync(await CompileAsync(Library([], [])), level);
        LinkedLevel grown = await RoomPropHarness.LinkAsync(await CompileAsync(Library()), level);
        Assert.NotEmpty(Lump(grown.Bsp).Props);
        Assert.Empty(Lump(bare.Bsp).Props);

        // A level whose rooms have none keeps its first room's game lumps
        // byte for byte, as before detail props were carried.
        RoomObject first = (await CompileAsync(Library([], []))).Get("hub");
        Assert.Equal(first.Bsp.GameLumps.Select(g => g.Data.ToArray()), bare.Bsp.GameLumps.Select(g => g.Data.ToArray()));
        Assert.Equal(bare.Bsp[BspLump.Entities].Data.ToArray(), grown.Bsp[BspLump.Entities].Data.ToArray());
        Assert.Equal(bare.EntityBudget!.Edicts, grown.EntityBudget!.Edicts);
        Assert.Equal(bare.EntityBudget.Listed, grown.EntityBudget.Listed);
    }

    /// <summary>
    /// Each prop by the room cell it stands in and its dictionary entry (by
    /// content), counted.
    /// </summary>
    private static Dictionary<string, int> Census(DetailPropLump lump)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (DetailObjectLump p in lump.Props)
        {
            int x = (int)Math.Floor(p.Origin.X / RoomHarness.Cell);
            int y = (int)Math.Floor(p.Origin.Y / RoomHarness.Cell);
            string entry = p.Type == 0 ? lump.ModelNames[p.DetailModel] : Sprite(lump.Sprites[p.DetailModel]);
            string key = $"({x}, {y}) {entry}";
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    /// <summary>
    /// Where a point stands, wherever the layout placed its room: the point
    /// turned back into its room's frame, on a grass box's top (anywhere
    /// over the displacement's brush) and in which quadrant of the box, or
    /// at one of the room's detail entities; null for a point on none.
    /// </summary>
    private static string? Where(LevelLayout layout, Vec3 point)
    {
        for (int i = 0; i < layout.Rooms.Count; i++)
        {
            RoomInstance instance = layout.Rooms[i];
            RoomTransform transform = new(instance.Placement, RoomHarness.Cell);
            Vec3 local = transform.Unapply(point);
            bool hub = instance.Placement.Room == "hub";
            foreach ((string name, Box box, bool displaced) in hub ? new[] { ("slab", HubSlab, false), ("patch", HubPatch, true) } : new[] { ("slab", OtherSlab, false) })
            {
                bool inside = local.X >= box.Mins.X - 0.01f && local.X <= box.Maxs.X + 0.01f && local.Y >= box.Mins.Y - 0.01f && local.Y <= box.Maxs.Y + 0.01f;
                if (inside && (displaced ? local.Z >= box.Maxs.Z - 0.01f : Math.Abs(local.Z - box.Maxs.Z) < 0.01f))
                {
                    Vec3 centre = (box.Mins + box.Maxs) * 0.5f;
                    return $"{i} {name} {(local.X < centre.X ? "w" : "e")}{(local.Y < centre.Y ? "s" : "n")}";
                }
            }

            foreach ((int room, VmfChunk entity) in Entities)
            {
                Vec3 origin = VmfPlacement.Origin(entity)!.Value;
                if ((room == 0) == hub && (local - origin).Length() < 0.01f)
                {
                    return $"{i} entity {entity.GetValue("id")}";
                }
            }
        }

        return null;
    }
}
