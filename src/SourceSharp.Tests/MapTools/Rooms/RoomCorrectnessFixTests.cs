//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

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

    // ---- 5. the sun is not turned ----------------------------------------------

    /// <summary>
    /// A room with a <c>light_environment</c>, linked and flattened at each
    /// rotation: the sun is library-wide (every room shares one sun, however
    /// it is placed), so the level has exactly one, with its <c>angles</c>,
    /// <c>angle</c> and <c>pitch</c> as authored in both maps and standing at
    /// the level's origin in both. Before the fix both the linker and the
    /// flatten turned its yaw with the room. Since the library-singletons
    /// rule (decision D3) a room may carry a sun only as an equal copy of the
    /// library's, which the split drops, so the sun here is the library's, in
    /// the gap, with the room's copy standing elsewhere in its cell.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ARoomsSunIsNotTurnedWithTheRoom(int rotation)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        (string, string)[] keys = [("angles", "-45 30 0"), ("angle", "30"), ("pitch", "-45"), ("_light", "255 255 255 200")];
        library.Chunks.Add(Entity("light_environment", 700008, [("origin", "-64 -64 128"), .. keys]));
        library.Chunks.Add(Entity("light_environment", 700009, [("origin", "64 64 128"), .. keys]));

        (BspData linked, BspData whole) = await LinkAndCompileFlatAsync(library, $"hub@{rotation}");

        foreach (BspData bsp in new[] { linked, whole })
        {
            BspEntity sun = EntityLump.Parse(bsp[BspLump.Entities]).Single(e => e.ClassName == "light_environment");
            Assert.Equal("-45 30 0", sun.Get("angles"));
            Assert.Equal("30", sun.Get("angle"));
            Assert.Equal("-45", sun.Get("pitch"));
            Assert.Equal(RoomLibraryEntities.LevelOrigin, sun.Get("origin"));
        }
    }

    /// <summary>
    /// Only the sun keeps its angles: any other entity's yaw still turns
    /// with the room, in the link and in the flatten, and the sun's class is
    /// matched as vbsp matches classes, exactly.
    /// </summary>
    [Fact]
    public void OnlyTheSunKeepsItsAngles()
    {
        RoomTransform linkTurn = new(new RoomPlacement("r", 0, 0, 1), 256);
        QuarterTurn flattenTurn = QuarterTurn.Of(linkTurn);
        foreach ((string classname, string expected) in new[] { ("light_environment", "-45 30 0"), ("light_spot", "-45 120 0") })
        {
            BspEntity entity = new();
            entity.Pairs.Add(new BspKeyValue("classname", classname));
            entity.Pairs.Add(new BspKeyValue("angles", "-45 30 0"));
            Assert.Equal(expected, LevelLinker.MoveEntity(entity, linkTurn, "r").Get("angles"));
            Assert.Equal(expected, VmfPlacement.MoveEntity(Entity(classname, 1, ("angles", "-45 30 0")), flattenTurn).GetValue("angles"));
        }
    }

    // ---- 6. library-wide entities in the gaps ----------------------------------

    /// <summary>
    /// The split collects the library-wide classes that stand in the gaps
    /// (and one with no origin, which stands in no cell), in library order
    /// and untouched; an equal copy of the sun inside a cell is dropped from
    /// its room (what else a room's copy meets is
    /// <c>RoomSingletonTests</c>'); any other class in the gaps is still
    /// ignored.
    /// </summary>
    [Fact]
    public void TheSplitCollectsLibraryWideEntitiesFromTheGaps()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        library.Chunks.Add(Entity("env_fog_controller", 700010, ("origin", "-64 0 0"), ("fogenable", "1")));
        library.Chunks.Add(Entity("light_environment", 700011, ("origin", "-64 -64 128"), ("angles", "-45 30 0")));
        library.Chunks.Add(Entity("shadow_control", 700012));
        library.Chunks.Add(Entity("light_environment", 700013, ("origin", "128 128 128"), ("angles", "-45 30 0")));
        library.Chunks.Add(Entity("info_target", 700014, ("origin", "-64 0 0")));
        library.Chunks.Add(Entity("Light_Environment", 700015, ("origin", "-64 0 0")));

        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);

        Assert.Equal(["700010", "700011", "700012"], split.LibraryEntities.Select(e => e.GetValue("id")));
        Assert.Equal("-64 -64 128", split.LibraryEntities[1].GetValue("origin"));
        // The room's copy equals the library's sun but for its origin, so the
        // room drops it (the library-singletons rule, decision D3).
        Assert.DoesNotContain(
            split.Rooms.Single().Document.GetChunks(MapFileLoader.EntityChunk),
            e => e.GetValue("id") == "700013");
        Assert.Equal(split.Rooms.Single().Document.ToBytes(), RoomLibraryVmf.Split(library).Single().Document.ToBytes());
        Assert.Empty(RoomLibraryVmf.SplitLibrary(RoomHarness.LibraryVmf(Hub)).LibraryEntities);

        Assert.True(RoomLibraryEntities.IsLibraryWide("env_tonemap_controller"));
        Assert.True(RoomLibraryEntities.IsLibraryWide("postprocess_controller"));
        Assert.False(RoomLibraryEntities.IsLibraryWide(null));
    }

    /// <summary>
    /// The library section reads back the entities it was written from, key
    /// for key, with codec 0 and the payload's length in its header.
    /// </summary>
    [Fact]
    public async Task TheLibrarySectionRoundTrips()
    {
        VmfChunk sun = Entity("light_environment", 7, ("origin", "-64 -64 128"), ("angles", "-45 30 0"));
        VmfChunk fog = Entity("env_fog_controller", 8, ("fogcolor", "1 2 3"));
        RoomPackSectionData section = RoomLibraryEntities.ToSection([sun, fog]);

        Assert.Equal(RoomLibraryEntities.SectionTag, section.Tag);
        Assert.Equal(RoomLibraryEntities.CodecNone, section.Bytes.Span[0]);
        Assert.Equal(section.Bytes.Length - 9, System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(section.Bytes.Span[1..]));

        IReadOnlyList<VmfChunk> read = await RoomLibraryEntities.ReadAsync(section.Bytes);
        Assert.Equal(2, read.Count);
        Assert.Equal(sun.Keys.Select(k => (k.Name, k.Value)), read[0].Keys.Select(k => (k.Name, k.Value)));
        Assert.Equal(fog.Keys.Select(k => (k.Name, k.Value)), read[1].Keys.Select(k => (k.Name, k.Value)));
        Assert.Empty(await RoomLibraryEntities.ReadAsync(RoomLibraryEntities.ToSection([]).Bytes));
    }

    /// <summary>A section the reader cannot trust is refused, each with its reason.</summary>
    [Fact]
    public async Task ABadLibrarySectionIsRefused()
    {
        byte[] good = RoomLibraryEntities.ToSection([Entity("light_environment", 7)]).Bytes.ToArray();

        LinkException shortOne = await Assert.ThrowsAsync<LinkException>(async () => await RoomLibraryEntities.ReadAsync(good.AsMemory(0, 5)));
        Assert.Equal("the room pack's LENT section is 5 bytes, shorter than its 9-byte header.", shortOne.Message);

        byte[] brotli = (byte[])good.Clone();
        brotli[0] = 2;
        LinkException codec = await Assert.ThrowsAsync<LinkException>(async () => await RoomLibraryEntities.ReadAsync(brotli));
        Assert.Equal("the room pack's LENT section has codec 2; this build reads codec 0 (none).", codec.Message);

        LinkException length = await Assert.ThrowsAsync<LinkException>(async () => await RoomLibraryEntities.ReadAsync(good.AsMemory(0, good.Length - 1)));
        Assert.Equal($"the room pack's LENT section says {good.Length - 9} bytes of entities but holds {good.Length - 10}.", length.Message);

        byte[] text = [0, 0, 0, 0, 0, 0, 0, 0, 1, (byte)'}'];
        LinkException notVmf = await Assert.ThrowsAsync<LinkException>(async () => await RoomLibraryEntities.ReadAsync(text));
        Assert.StartsWith("the room pack's LENT section is not VMF text: ", notVmf.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentNullException>(() => RoomLibraryEntities.ToSection(null!));
    }

    // ---- 7. default cubemaps and real content ------------------------------------

    /// <summary>
    /// With sky textures that resolve, an ordinary compile of a room's VMF
    /// packs the default cubemaps under its map name, as vbsp does, and the
    /// room compile of the same VMF packs nothing, turning the switch off on
    /// the context it was given.
    /// </summary>
    [Fact]
    public async Task ARoomCompilePacksNoDefaultCubemapsWhereAMapCompileDoes()
    {
        RoomDefinition hub = RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX);
        VmfDocument document = RoomHarness.BuildRoomModel(hub);
        document.GetChunk(MapFileLoader.WorldChunk)!.AddKey("skyname", "sky_unit");

        VbspContext map = await SkyContextAsync();
        Assert.True(map.WritesDefaultCubemaps);
        VbspResult whole = await RoomHarness.CompileAsync(document, map);
        Assert.Contains("materials/maps/hub/cubemapdefault.vtf"u8.ToArray(), Windows(whole.Bsp![BspLump.PakFile].Data.ToArray(), 37));

        VbspContext room = await SkyContextAsync();
        RoomObject compiled = await RoomCompiler.CompileAsync(document, hub, room);
        Assert.False(room.WritesDefaultCubemaps);
        Assert.DoesNotContain("cubemapdefault"u8.ToArray(), Windows(compiled.Bsp[BspLump.PakFile].Data.ToArray(), 14));

        static IEnumerable<byte[]> Windows(byte[] bytes, int width) =>
            Enumerable.Range(0, Math.Max(0, bytes.Length - width + 1)).Select(i => bytes[i..(i + width)]);
    }

    private static async Task<VbspContext> SkyContextAsync()
    {
        InMemoryFileSystem files = new();
        files.AddText($"materials/{RoomHarness.Plain}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        files.AddText(
            $"materials/{RoomHarness.Trigger}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        SurfaceUnit.AddSky(files, "sky_unit", (int)ImageFormat.Bgr888, 0x0304);
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        return new VbspContext(VbspOptions.Default, new ContentFileSystem([mount])) { MapBase = "hub" };
    }

    // ---- the fixes through stored link data ------------------------------------

    /// <summary>
    /// The ladder's two steps (turned and stored with the room, moved to the
    /// cell at link) give exactly the one-step <see cref="LevelLinker.MoveBox"/>
    /// result at every turn, off the grid and around zero too.
    /// </summary>
    [Fact]
    public void TheSplitLadderMoveIsTheOneStepMoveBox()
    {
        (Vec3 Mins, Vec3 Maxs)[] boxes =
        [
            (new Vec3(40, 60, 16), new Vec3(56, 72, 120)),
            (new Vec3(-0f, 0f, -3.25f), new Vec3(0.1f, 255.99f, 1.5f)),
            (new Vec3(-98765.43f, -1e-7f, 0), new Vec3(123456.78f, 1e-7f, 16777216f)),
        ];
        foreach ((Vec3 mins, Vec3 maxs) in boxes)
        {
            BspEntity ladder = new();
            string[] keys = ["mins.x", "mins.y", "mins.z", "maxs.x", "maxs.y", "maxs.z"];
            float[] values = [mins.X, mins.Y, mins.Z, maxs.X, maxs.Y, maxs.Z];
            for (int i = 0; i < 6; i++)
            {
                ladder.Pairs.Add(new BspKeyValue(keys[i], values[i].ToString("R", CultureInfo.InvariantCulture)));
            }

            for (int rotation = 0; rotation < 4; rotation++)
            {
                RoomTransform transform = new(new RoomPlacement("r", 3, -2, rotation), 256f);
                Box box = LevelLinker.MoveBox(transform, mins, maxs);
                float[] moved = [box.Mins.X, box.Mins.Y, box.Mins.Z, box.Maxs.X, box.Maxs.Y, box.Maxs.Z];
                BspEntity actual = LevelLinker.MoveEntity(ladder, transform, "r");
                Assert.Equal(moved.Select(v => v.ToString("F2", CultureInfo.InvariantCulture)), keys.Select(k => actual.Get(k)));
            }
        }
    }

    /// <summary>
    /// A room with a ladder, a sun and an occluder: what the room compile
    /// stores for each turn, read back from its pack sections, equals what
    /// the link computes then, and linking from the stored data gives the
    /// same entity lump as linking without it, with the ladder moved, the
    /// sun unturned and the occluder numbers rebased. An entities section of
    /// the revision before these fixes reads as absent, so stored data
    /// turned the old way is never linked.
    /// </summary>
    [Fact]
    public async Task StoredEntitiesCarryTheFixes()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk ladder = Entity("func_ladder", 700001);
        ladder.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(40, 60, 16), new Vec3(56, 72, 120), 70001));
        library.Chunks.Add(ladder);
        VmfChunk occluder = Entity("func_occluder", 700002, ("StartActive", "1"));
        occluder.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(140, 160, 16), new Vec3(156, 220, 120), 70002));
        library.Chunks.Add(occluder);
        LibraryRoom split = RoomLibraryVmf.Split(library).Single();

        // A sun of the room's own, as a host that packs rooms itself may give
        // one: ssmap room's split would take it out of the room, and it is
        // the link's turn of it that is checked here.
        split.Document.Chunks.Add(Entity("light_environment", 700009, ("origin", "64 64 128"), ("angles", "-45 30 0")));
        VbspContext context = await RoomHarness.ContextAsync();
        context.MapBase = "hub";
        RoomObject compiled = await RoomCompiler.CompileAsync(split.Document, split.Definition, context);

        RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(compiled, CancellationToken.None))!;
        const RoomLinkParts all = RoomLinkParts.Geometry | RoomLinkParts.Collision | RoomLinkParts.Entities;
        List<RoomPackSectionData> sections = [.. RoomLinkSections.Write(data, all)];
        RoomLinkData read = Read(compiled, sections)!;
        for (int rotation = 0; rotation < 4; rotation++)
        {
            RoomLinkEntities computed = LevelLinker.ComputeEntities(compiled, rotation);
            RoomLinkEntities stored = read.Rotation(rotation)!.Entities!;
            Assert.Equal(computed.Items.Count, stored.Items.Count);
            for (int e = 0; e < computed.Items.Count; e++)
            {
                Assert.Equal(computed.Items[e].Pairs, stored.Items[e].Pairs);
            }

            Assert.Contains(stored.Items.SelectMany(i => i.Pairs), p => p.Component == 2);
        }

        RoomLibrary withData = new(split.Definition.Kit, split.Definition.CellSize);
        withData.Add(compiled with { Link = read });
        RoomLibrary without = new(split.Definition.Kit, split.Definition.CellSize);
        without.Add(compiled with { Link = null });
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub@90, hub@270"), "stored");
        BspData a = (await LevelLinker.LinkAsync(level.ToLayout(n => withData.Find(n)?.Definition, withData.CellSize, withData.Kit), withData, await RoomHarness.ContextAsync())).Bsp;
        BspData b = (await LevelLinker.LinkAsync(level.ToLayout(n => without.Find(n)?.Definition, without.CellSize, without.Kit), without, await RoomHarness.ContextAsync())).Bsp;
        Assert.Equal(b[BspLump.Entities].Data.ToArray(), a[BspLump.Entities].Data.ToArray());

        List<BspEntity> entities = [.. EntityLump.Parse(a[BspLump.Entities])];
        Assert.All(entities.Where(e => e.ClassName == "light_environment"), e => Assert.Equal("-45 30 0", e.Get("angles")));
        Assert.Equal(["0", "1"], entities.Where(e => e.ClassName == "func_occluder").Select(e => e.Get("occludernumber")));
        Assert.Equal(["184.00", "316.00"], entities.Where(e => e.ClassName == "info_ladder").Select(e => e.Get("mins.x")));

        // An ENT section of revision 1 (entities turned before these fixes) is absent.
        RoomPackSectionData ent1 = sections.Single(s => s.Tag == "ENT1");
        byte[] old = ent1.Bytes.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(old.AsSpan(9), 1);
        RoomLinkData? stale = Read(compiled, [.. sections.Select(s => s.Tag == "ENT1" ? s with { Bytes = old } : s)]);
        Assert.Null(stale!.Rotation(1)!.Entities);
        Assert.NotNull(stale.Rotation(1)!.Geometry);
        Assert.Equal(2, RoomLinkSections.RevisionFor("ENT3"));
        Assert.Equal(RoomLinkSections.Revision, RoomLinkSections.RevisionFor("GEO3"));

        static RoomLinkData? Read(RoomObject room, IReadOnlyList<RoomPackSectionData> sections) =>
            RoomLinkSections.Read(room, tag => sections.FirstOrDefault(s => s.Tag == tag) is { Tag: not null } found
                ? new ArraySegment<byte>(found.Bytes.ToArray())
                : (ArraySegment<byte>?)null);
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
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        IReadOnlyList<LibraryRoom> rooms = split.Rooms;
        RoomLibrary compiled = new(rooms[0].Definition.Kit, rooms[0].Definition.CellSize) { LibraryEntities = split.LibraryEntities };
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
