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

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The link and flatten transforms the rooms audit found silently wrong:
/// each fact here failed on the code before its fix, and holds the linked
/// map, the flattened VMF or the split to what a whole-map compile of the
/// same level gives.
/// </summary>
/// <remarks>
/// The rooms are small harness rooms on the walkable kit, built into a
/// library VMF with the feature under test added, so each fact goes through
/// the same split, room compile, link and flatten that <c>ssmap room</c> and
/// <c>ssmap link</c> run, in memory.
/// </remarks>
public sealed class RoomCorrectnessFixTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    private static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    // ---- 1. info_ladder bounds -------------------------------------------------

    /// <summary>
    /// A room with a <c>func_ladder</c>, linked at each rotation: the
    /// <c>info_ladder</c> vbsp makes of it carries its bounds in six keys,
    /// and the linked map's must be the flattened compile's, which vbsp
    /// measures from the moved brushes. Before the fix the linker moved only
    /// <c>origin</c> and <c>angles</c>, so the bounds stayed room-local.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALinkedLaddersBoundsAreTheFlattenedCompiles(int rotation)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk ladder = Entity("func_ladder", 700001);
        ladder.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(40, 60, 16), new Vec3(56, 72, 120), 70001));
        library.Chunks.Add(ladder);

        (BspData linked, BspData whole) = await LinkAndCompileFlatAsync(library, $"hub@{rotation}, hub");

        List<string> Bounds(BspData bsp) =>
            [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => e.ClassName == "info_ladder")
                .Select(e => string.Join(' ', new[] { "mins.x", "mins.y", "mins.z", "maxs.x", "maxs.y", "maxs.z" }.Select(k => e.Get(k))))
                .Order(StringComparer.Ordinal)];

        List<string> expected = Bounds(whole);
        Assert.Equal(2, expected.Count);
        Assert.Equal(expected, Bounds(linked));
    }

    /// <summary>
    /// The ladder keys move as one box, re-sorted after the turn; an entity
    /// with only some of them is not a ladder and keeps them as written; a
    /// bound that is not a number is refused naming the room and key.
    /// </summary>
    [Fact]
    public void LadderBoundsMoveAsOneBoxOnlyWhenAllSixArePresent()
    {
        RoomTransform turned = new(new RoomPlacement("r", 1, 0, 1), 256);
        BspEntity ladder = new();
        foreach ((string key, string value) in new[]
        {
            ("classname", "info_ladder"), ("maxs.z", "120.00"), ("maxs.y", "72.00"), ("maxs.x", "56.00"),
            ("mins.z", "16.00"), ("mins.y", "60.00"), ("mins.x", "40.00"),
        })
        {
            ladder.Pairs.Add(new BspKeyValue(key, value));
        }

        BspEntity moved = LevelLinker.MoveEntity(ladder, turned, "r");
        Assert.Equal(
            ["440.00", "40.00", "16.00", "452.00", "56.00", "120.00"],
            new[] { "mins.x", "mins.y", "mins.z", "maxs.x", "maxs.y", "maxs.z" }.Select(k => moved.Get(k)));
        Assert.Equal([.. ladder.Pairs.Select(p => p.Key)], moved.Pairs.Select(p => p.Key));

        BspEntity partial = new();
        partial.Pairs.Add(new BspKeyValue("mins.x", "40"));
        Assert.Equal("40", LevelLinker.MoveEntity(partial, turned, "r").Get("mins.x"));

        ladder.Pairs[1] = new BspKeyValue("maxs.z", "high");
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.MoveEntity(ladder, turned, "attic"));
        Assert.Equal("room attic has an entity whose \"maxs.z\" holds \"high\", not a number", refused.Message);
    }

    // ---- 2. occludernumber -----------------------------------------------------

    /// <summary>
    /// Two placements with a <c>func_occluder</c> each, at each rotation:
    /// every room compile numbers its occluders from 0, and the linker
    /// appends the rooms' occluders one after another, so the second room's
    /// entity must say 1 and name the occluder in its own cell, as the
    /// flattened compile does. Before the fix the key stayed 0 and named the
    /// first room's occluder.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task EachLinkedOccluderEntityNamesItsOwnRoomsOccluder(int rotation)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk occluder = Entity("func_occluder", 700002, ("StartActive", "1"));
        occluder.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(40, 60, 16), new Vec3(56, 120, 120), 70002));
        library.Chunks.Add(occluder);

        (BspData linked, BspData whole) = await LinkAndCompileFlatAsync(library, $"hub@{rotation}, hub");

        static List<int> Numbers(BspData bsp) =>
            [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => e.ClassName == "func_occluder")
                .Select(e => int.Parse(e.Get("occludernumber")!, CultureInfo.InvariantCulture))];

        Assert.Equal([0, 1], Numbers(whole));
        Assert.Equal([0, 1], Numbers(linked));

        // Entities come in layout order, so the k-th occluder entity is the
        // k-th placement's: west cell first, then east.
        OcclusionLump occlusion = OcclusionLump.Read(linked[BspLump.Occlusion]);
        OcclusionLump reference = OcclusionLump.Read(whole[BspLump.Occlusion]);
        for (int k = 0; k < 2; k++)
        {
            Box cell = new(new Vec3(k * RoomHarness.Cell, 0, 0), new Vec3((k + 1) * RoomHarness.Cell, RoomHarness.Cell, RoomHarness.Cell));
            int named = Numbers(linked)[k];
            Box box = new(occlusion.Occluders[named].Mins, occlusion.Occluders[named].Maxs);
            Assert.True(box.ContainsWithin(cell, 0), $"occluder {named} at {box.Mins}-{box.Maxs} is not in cell {k}");
            Assert.Equal(reference.Occluders[named].Mins, occlusion.Occluders[named].Mins);
            Assert.Equal(reference.Occluders[named].Maxs, occlusion.Occluders[named].Maxs);
        }
    }

    /// <summary>
    /// The occluder base shifts <c>occludernumber</c> and nothing else; the
    /// first room (base 0) keeps the key's text as written; a key that is
    /// not an index is refused naming the room.
    /// </summary>
    [Fact]
    public void OccluderNumbersShiftByTheRoomsBase()
    {
        RoomTransform moved = new(new RoomPlacement("r", 1, 0, 0), 256);
        BspEntity occluder = new();
        occluder.Pairs.Add(new BspKeyValue("classname", "func_occluder"));
        occluder.Pairs.Add(new BspKeyValue("occludernumber", "2"));
        occluder.Pairs.Add(new BspKeyValue("StartActive", "1"));

        Assert.Equal("5", LevelLinker.MoveEntity(occluder, moved, "r", 3).Get("occludernumber"));
        Assert.Equal("1", LevelLinker.MoveEntity(occluder, moved, "r", 3).Get("StartActive"));
        Assert.Equal("2", LevelLinker.MoveEntity(occluder, moved, "r").Get("occludernumber"));

        occluder.Pairs[1] = new BspKeyValue("occludernumber", "two");
        Assert.Equal("two", LevelLinker.MoveEntity(occluder, moved, "r").Get("occludernumber"));
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.MoveEntity(occluder, moved, "attic", 1));
        Assert.Equal("room attic has an entity whose \"occludernumber\" holds \"two\", not an occluder index", refused.Message);
    }

    // ---- 3. side-id references in the flatten ------------------------------------

    /// <summary>
    /// A flattened level whose room holds an <c>env_cubemap</c>, an
    /// <c>info_overlay</c> and an <c>info_no_dynamic_shadow</c> naming brush
    /// sides, placed twice at each rotation: the flatten renumbers every
    /// <c>id</c>, so every id in a <c>sides</c> list must be the new id of
    /// the same side of the same placement, and a side the flatten left out
    /// (a joined plug's) must be dropped rather than left to name whatever
    /// took its number. Before the fix the lists kept the library's ids.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public void FlattenedSideListsNameTheMovedSides(int rotation)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        VmfChunk pillar = RoomModel.Slab(RoomHarness.Plain, new Vec3(100, 100, 16), new Vec3(132, 132, 64), 71000);
        int next = 71001;
        foreach (VmfChunk side in pillar.GetChunks("side"))
        {
            side.Keys.First(k => k.Name == "id").Value = (next++).ToString(CultureInfo.InvariantCulture);
        }

        world.Children.Add(pillar);

        // The hub's east plug: the joint between the two placements leaves
        // it out when the west hub is unturned; its sides are all one id.
        string eastPlugSide = world.GetChunks(MapFileLoader.SolidChunk)
            .Where(s => s.GetChunks("side").Any(side => side.GetValue("material") == RoomHarness.Trigger))
            .Single(s => VmfPlacement.Bounds(s).Mins.X == RoomHarness.Cell - RoomHarness.WalkableKit.Depth)
            .GetChunks("side").First().GetValue("id")!;

        library.Chunks.Add(Entity("env_cubemap", 700003, ("origin", "120 120 100"), ("sides", "71001 71002")));
        library.Chunks.Add(Entity("info_overlay", 700004, ("origin", "116 116 64"), ("material", RoomHarness.Plain), ("sides", $"71001 {eastPlugSide}")));
        library.Chunks.Add(Entity("info_no_dynamic_shadow", 700005, ("origin", "110 110 20"), ("sides", "71003 71004 71005")));

        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", $"hub@{rotation}, hub"), "sides");
        VmfDocument flat = LevelFlattener.Flatten(level, library);

        Dictionary<string, VmfChunk> sidesById = [];
        void Collect(VmfChunk chunk)
        {
            if (string.Equals(chunk.Name, "side", StringComparison.Ordinal))
            {
                Assert.True(sidesById.TryAdd(chunk.GetValue("id")!, chunk), "the flatten repeats a side id");
            }

            foreach (VmfChunk child in chunk.Chunks)
            {
                Collect(child);
            }
        }

        foreach (VmfChunk chunk in flat.Chunks)
        {
            Collect(chunk);
        }

        // Each placement's plane of an original side, moved as the flatten moves it.
        Dictionary<string, string> planes = pillar.GetChunks("side").ToDictionary(s => s.GetValue("id")!, s => s.GetValue("plane")!);
        string Moved(string id, int placement)
        {
            RoomPlacement where = new("hub", placement, 0, placement == 0 ? rotation / 90 : 0);
            QuarterTurn turn = QuarterTurn.Of(new RoomTransform(where, RoomHarness.Cell));
            VmfChunk solid = new("solid");
            VmfChunk side = solid.AddChunk("side");
            side.AddKey("plane", planes[id]);
            side.AddKey("uaxis", "[1 0 0 0] 0.25");
            side.AddKey("vaxis", "[0 -1 0 0] 0.25");
            return VmfPlacement.MoveSolid(solid, turn).GetChunks("side").Single().GetValue("plane")!;
        }

        foreach ((string classname, string[] expected) in new[]
        {
            ("env_cubemap", new[] { "71001", "71002" }),
            ("info_overlay", new[] { "71001" }),
            ("info_no_dynamic_shadow", new[] { "71003", "71004", "71005" }),
        })
        {
            List<VmfChunk> placed = [.. flat.GetChunks(MapFileLoader.EntityChunk).Where(e => e.GetValue("classname") == classname)];
            Assert.Equal(2, placed.Count);
            for (int k = 0; k < 2; k++)
            {
                string[] ids = placed[k].GetValue("sides")!.Split(' ');
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.Equal(Moved(expected[i], k), sidesById[ids[i]].GetValue("plane"));
                }

                if (classname != "info_overlay")
                {
                    Assert.Equal(expected.Length, ids.Length);
                    continue;
                }

                // The west hub unturned joins through its east plug, which
                // the flatten leaves out; at any other turn, and in the east
                // hub, the east plug is capped and kept.
                bool plugLeftOut = k == 0 && rotation == 0;
                Assert.Equal(plugLeftOut ? 1 : 2, ids.Length);
                if (!plugLeftOut)
                {
                    Assert.Equal(RoomHarness.Trigger, sidesById[ids[1]].GetValue("material"));
                }
            }
        }
    }

    /// <summary>
    /// A <c>sides</c> list may name a brush entity's sides too, which the
    /// flatten renumbers like the world's; an id no side of the placement
    /// has is dropped; a token that is not a number is kept as written; an
    /// entity without a list is untouched.
    /// </summary>
    [Fact]
    public void FlattenedSideListsCoverBrushEntitiesAndDropUnknownIds()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk detail = Entity("func_detail", 700006);
        VmfChunk slab = RoomModel.Slab(RoomHarness.Plain, new Vec3(100, 100, 16), new Vec3(132, 132, 64), 72000);
        slab.GetChunks("side").First().Keys.First(k => k.Name == "id").Value = "72001";
        detail.Children.Add(slab);
        library.Chunks.Add(detail);
        library.Chunks.Add(Entity("info_no_dynamic_shadow", 700007, ("origin", "110 110 20"), ("SIDES", "x 72001 999999")));

        VmfDocument flat = LevelFlattener.Flatten(LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub"), "one"), library);

        VmfChunk movedDetail = flat.GetChunks(MapFileLoader.EntityChunk).Single(e => e.GetValue("classname") == "func_detail");
        string newId = movedDetail.GetChunks("solid").Single().GetChunks("side").First().GetValue("id")!;
        VmfChunk shadow = flat.GetChunks(MapFileLoader.EntityChunk).Single(e => e.GetValue("classname") == "info_no_dynamic_shadow");
        Assert.Equal($"x {newId}", shadow.GetValue("SIDES"));
        Assert.NotEqual("72001", newId);
    }

    // ---- 4. overlay basis ------------------------------------------------------

    /// <summary>
    /// A library room with an <c>info_overlay</c>, split and then flattened
    /// at each rotation: vbsp places an overlay by <c>BasisOrigin</c> and
    /// orients it by <c>BasisU</c>, <c>BasisV</c> and <c>BasisNormal</c>, not
    /// by <c>origin</c>, so the split must move the origin into the room and
    /// the flatten must move and turn all four. Before the fix only
    /// <c>origin</c> moved and the overlay was built in library coordinates.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public void OverlayBasesMoveWithTheSplitAndTheFlatten(int rotation)
    {
        // The hub second, so its cell's corner is not the library origin and
        // the split's move is not the identity.
        VmfDocument library = RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("end", RoomFacing.PositiveX), Hub);
        Vec3 corner = new(RoomHarness.Cell + RoomHarness.LibraryGap, 0, 0);
        Vec3 local = new(100, 60, 16);
        library.Chunks.Add(Entity(
            "info_overlay", 700008,
            ("origin", VmfPlacement.Format(corner + local)),
            ("BasisOrigin", VmfPlacement.Format(corner + local)),
            ("BasisU", "0 1 0"),
            ("BasisV", "1 0 0"),
            ("BasisNormal", "0 0 1"),
            ("uv0", "-8 -8 0"),
            ("material", RoomHarness.Plain)));

        VmfChunk split = RoomLibraryVmf.Split(library).Single(r => r.Definition.Name == "hub").Document
            .GetChunks(MapFileLoader.EntityChunk).Single(e => e.GetValue("classname") == "info_overlay");
        Assert.Equal("100 60 16", split.GetValue("BasisOrigin"));
        Assert.Equal("0 1 0", split.GetValue("BasisU"));

        VmfDocument flat = LevelFlattener.Flatten(LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", $"end, hub@{rotation}"), "overlay"), library);
        VmfChunk overlay = flat.GetChunks(MapFileLoader.EntityChunk).Single(e => e.GetValue("classname") == "info_overlay");
        QuarterTurn turn = QuarterTurn.Of(new RoomTransform(new RoomPlacement("hub", 1, 0, rotation / 90), RoomHarness.Cell));
        Assert.Equal(VmfPlacement.Format(turn.Apply(local)), overlay.GetValue("BasisOrigin"));
        Assert.Equal(overlay.GetValue("origin"), overlay.GetValue("BasisOrigin"));
        Assert.Equal(VmfPlacement.Format(turn.Rotate(new Vec3(0, 1, 0))), overlay.GetValue("BasisU"));
        Assert.Equal(VmfPlacement.Format(turn.Rotate(new Vec3(1, 0, 0))), overlay.GetValue("BasisV"));
        Assert.Equal("0 0 1", overlay.GetValue("BasisNormal"));
        Assert.Equal("-8 -8 0", overlay.GetValue("uv0"));
    }

    /// <summary>
    /// A wall overlay's normal turns with the room, and a basis key that is
    /// not three numbers is refused naming the entity and key, as a bad
    /// origin is.
    /// </summary>
    [Fact]
    public void AWallOverlaysNormalTurnsAndABadBasisIsRefused()
    {
        VmfChunk overlay = Entity("info_overlay", 5, ("BasisNormal", "1 0 0"), ("BasisU", "0 1 0"));
        VmfChunk moved = VmfPlacement.MoveEntity(overlay, new QuarterTurn(1, new Vec3(256, 0, 0)));
        Assert.Equal("0 1 0", moved.GetValue("BasisNormal"));
        Assert.Equal("-1 0 0", moved.GetValue("BasisU"));

        VmfChunk bad = Entity("info_overlay", 6, ("BasisV", "0 1"));
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(
            () => VmfPlacement.MoveEntity(bad, new QuarterTurn(1, Vec3.Zero)));
        Assert.Equal("entity 6 (info_overlay): BasisV \"0 1\" is not three numbers.", refused.Message);
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>A point or brush entity chunk with an id and a class.</summary>
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

    /// <summary>
    /// A library's rooms compiled and linked into the level of the given
    /// rows, and the same level flattened and compiled whole: the two maps
    /// a room feature must agree across.
    /// </summary>
    private static async Task<(BspData Linked, BspData Whole)> LinkAndCompileFlatAsync(VmfDocument library, params string[] rows)
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        RoomLibrary compiled = new(rooms[0].Definition.Kit, rooms[0].Definition.CellSize);
        foreach (LibraryRoom room in rooms)
        {
            VbspContext context = await RoomHarness.ContextAsync();
            context.MapBase = room.Definition.Name;
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, context));
        }

        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", rows), "fixes");
        LevelLayout layout = level.ToLayout(name => compiled.Find(name)?.Definition, compiled.CellSize, compiled.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, compiled, await RoomHarness.ContextAsync());

        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await RoomHarness.ContextAsync());
        Assert.NotNull(whole.Bsp);
        return (linked.Bsp, whole.Bsp!);
    }
}
