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
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomWaterHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Water contained in rooms, through the link (section 4.6 of the rooms
/// design, PR 14's first stage): every placed room's water data merged as
/// vbsp merges a map's, its leaves' and faces' references renumbered, its
/// fluids moved into the level's collision, its water overlays rebased and
/// moved, vvis's water passes run again over the level, and the level agreeing
/// with the flattened level's vbsp compile at every quarter turn; the pack
/// carries what the link needs, the output is a function of the level, and
/// water that reaches a door plug is refused.
/// </summary>
public sealed class LevelLinkerWaterTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>A library of the hub's cheap pool and the other room's expensive one.</summary>
    private static VmfDocument TwoPools() =>
        Library([(0, Water(Pool)), (1, Water(OtherPool, WaterBrush + 1, ExpensiveWater))]);

    // ---- placement, turned -------------------------------------------------------------------

    /// <summary>
    /// A level of the two pools' rooms, each turned: every point of the level
    /// holds the same thing in the linked map and the flattened level's
    /// compile (solid, air, or water of the same surface height and
    /// material), the two carry the same water records, and the same fluids
    /// (surface plane, contents, surface property, volume and extent).
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task PoolsLinkAsTheyFlattenAtEveryRotation(int rotation)
    {
        VmfDocument library = TwoPools();
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{(rotation + 90) % 360}");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(Points(flat, 2, 1), Points(linked.Bsp, 2, 1));
        Assert.Equal(Records(flat), Records(linked.Bsp));
        Assert.Equal(Fluids(flat), Fluids(linked.Bsp));
        Assert.Contains(Points(linked.Bsp, 2, 1), p => p.EndsWith("water at 56 of unit/water_cheap", StringComparison.Ordinal));
        Assert.Contains(Points(linked.Bsp, 2, 1), p => p.EndsWith("water at 88 of unit/water_expensive", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every leaf and warped face of the level names the record of its own
    /// placement's water: the linked leaves in the hub's pool read its
    /// height, the other room's its own, and no leaf or face names a record
    /// the level lacks.
    /// </summary>
    [Fact]
    public async Task LeavesAndFacesNameTheirOwnPlacementsRecord()
    {
        RoomLibrary rooms = await CompileAsync(TwoPools());
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub, other"));
        DLeafWaterData[] records = WaterData(linked.Bsp);

        Assert.Equal("water at 56 of unit/water_cheap", At(linked.Bsp, new Vec3(70, 70, 30)));
        Assert.Equal("water at 88 of unit/water_expensive", At(linked.Bsp, new Vec3(256 + 180, 180, 30)));
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(linked.Bsp[BspLump.Leafs]))
        {
            Assert.InRange(leaf.LeafWaterDataId, -1, records.Length - 1);
        }

        List<DFace> warped = [.. BspStructView.As<DFace>(linked.Bsp[BspLump.Faces]).ToArray().Where(f => f.SurfaceFogVolumeId >= 0)];
        Assert.NotEmpty(warped);
        foreach (DFace face in warped)
        {
            Assert.InRange(face.SurfaceFogVolumeId, 0, records.Length - 1);
            string material = RoomBrushHarness.Material(linked.Bsp, face.TexInfo);
            float z = RoomHarness.FaceVertices(linked.Bsp, face).Max(v => v.Z);
            Assert.Equal(z <= 56 ? 56f : 88f, records[face.SurfaceFogVolumeId].SurfaceZ);
            Assert.Contains("water", material, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Records merge as vbsp merges a map's, by an exact match of height,
    /// lowest point and surface texinfo: two placements of a pool whose
    /// texture axes are across the rows (so a move along a column leaves its
    /// texinfo as it was) share a record when stacked in a column, and do
    /// not side by side in a row (the move shifts the texture); the
    /// flattened level's compile holds the same count.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task EqualRecordsMergeAsVbspMergesThem(bool column, int expected)
    {
        VmfChunk pool = Water(Pool);
        foreach (VmfChunk side in pool.GetChunks(MapFileLoader.SideChunk))
        {
            side.Keys.First(k => k.Name == "vaxis").Value = "[0 0 -1 0] 0.25";
        }

        VmfDocument library = Library([(0, pool)]);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = column ? RoomPropHarness.Level("hub", "hub") : RoomPropHarness.Level("hub, hub");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);

        Assert.Equal(expected, WaterData(linked.Bsp).Length);
        Assert.Equal(WaterData(await CompileFlatAsync(library, level)).Length, WaterData(linked.Bsp).Length);
    }

    /// <summary>
    /// vvis's water passes run again over the level: a leaf of the other
    /// room that sees the hub's pool through the doorway is marked as
    /// seeing a fog volume and given a distance to it, which its own room's
    /// compile, blind to the neighbour, left at 65535; and the passes over
    /// the linked leaves and rows give exactly the lumps the link wrote.
    /// </summary>
    [Fact]
    public async Task VvisWaterPassesRunOverTheLevel()
    {
        RoomLibrary rooms = await CompileAsync(PoolLibrary());
        Assert.All(BspStructView.As<ushort>(rooms.Get("other").Bsp[BspLump.LeafMinDistToWater]).ToArray(), d => Assert.Equal(ushort.MaxValue, d));

        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub, other"));
        DLeaf[] leafs = BspStructView.As<DLeaf>(linked.Bsp[BspLump.Leafs]).ToArray();
        ushort[] distances = BspStructView.As<ushort>(linked.Bsp[BspLump.LeafMinDistToWater]).ToArray();
        Assert.Equal(leafs.Length, distances.Length);

        int otherSeesWater = 0;
        for (int l = 0; l < leafs.Length; l++)
        {
            Box box = new(new Vec3(leafs[l].Mins[0], leafs[l].Mins[1], leafs[l].Mins[2]), new Vec3(leafs[l].Maxs[0], leafs[l].Maxs[1], leafs[l].Maxs[2]));
            if (box.Mins.X >= 256 && (leafs[l].Contents & (int)BrushContents.TestFogVolume) != 0 && distances[l] < ushort.MaxValue)
            {
                otherSeesWater++;
            }
        }

        Assert.True(otherSeesWater > 0, "no leaf of the other room sees the hub's pool");
    }

    /// <summary>
    /// A level without water keeps its rooms' water bytes: no water lumps,
    /// every leaf 65535 from water and no leaf marked as seeing a fog volume.
    /// </summary>
    [Fact]
    public async Task ALevelWithoutWaterCarriesNoWater()
    {
        RoomLibrary rooms = await CompileAsync(Library([]));
        Assert.Null(rooms.Get("hub").Water);
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub, other"));

        Assert.Equal(0, linked.Bsp[BspLump.LeafWaterData].Length);
        Assert.Equal(0, linked.Bsp[BspLump.WaterOverlays].Length);
        Assert.All(BspStructView.As<ushort>(linked.Bsp[BspLump.LeafMinDistToWater]).ToArray(), d => Assert.Equal(ushort.MaxValue, d));
        Assert.All(BspStructView.As<DLeaf>(linked.Bsp[BspLump.Leafs]).ToArray(), l => Assert.Equal(0, l.Contents & (int)BrushContents.TestFogVolume));
    }

    /// <summary>
    /// The linked world collision lists every placement's fluid after its
    /// static solids, each with its <c>fluid</c> block and its surface plane
    /// moved (a turn leaves a vertical normal vertical, its zeros unsigned).
    /// </summary>
    [Fact]
    public async Task FluidsFollowTheStaticSolids()
    {
        RoomLibrary rooms = await CompileAsync(TwoPools());
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub@90, other@270"));
        PhysCollideModel world = PhysCollideLump.Read(linked.Bsp[BspLump.PhysCollide].Data.Span)[0];
        string text = world.KeyText;

        Assert.True(text.IndexOf("staticsolid", StringComparison.Ordinal) < text.IndexOf("fluid", StringComparison.Ordinal));
        Assert.Contains("\"surfaceplane\" \"0.000000 0.000000 1.000000 56.000000 \"", text, StringComparison.Ordinal);
        Assert.Contains("\"surfaceplane\" \"0.000000 0.000000 1.000000 88.000000 \"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-0.000000", text, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(text, "fluid {"));
        Assert.Equal(world.Solids.Count, CountOf(text, "\"index\""));
    }

    /// <summary>
    /// A fluid's surface plane at a turn and a translation: the normal
    /// turned with its zeros unsigned, the distance moved along it (a
    /// vertical normal keeps its distance on the grid and takes the height
    /// below it), and a sloped one's normal and distance moved as a plane is.
    /// </summary>
    [Fact]
    public void AFluidsPlaneTurnsAndMovesAsAPlane()
    {
        RoomWaterFluid level = new("water", 0.01f, 32, new Vec3(0, 0, 1), 56);
        Assert.Equal((new Vec3(0, 0, 1), 56f), RoomWater.SurfaceAt(level, 1, new Vec3(512, 256, 0)));
        Assert.Equal((new Vec3(0, 0, 1), -200f), RoomWater.SurfaceAt(level, 3, new Vec3(0, 0, -256)));
        Assert.True(float.IsPositive(RoomWater.SurfaceAt(level, 1, Vec3.Zero).Normal.X));

        RoomWaterFluid sloped = new("water", 0.01f, 32, new Vec3(0.5f, 0, 0.75f), 10);
        (Vec3 normal, float dist) = RoomWater.SurfaceAt(sloped, 1, new Vec3(100, 50, 0));
        Assert.Equal(new Vec3(0, 0.5f, 0.75f), normal);
        Assert.Equal(35f, dist);
    }

    /// <summary>
    /// The level keeps one <c>water_lod_control</c>, which vbsp adds to every
    /// room with water, as the flattened level's compile holds one; and it
    /// costs the level that one entity, counted as the budget counts it.
    /// </summary>
    [Fact]
    public async Task TheLevelKeepsOneWaterLodControl()
    {
        VmfDocument library = TwoPools();
        LevelGrid level = RoomPropHarness.Level("hub, other", "other@90, hub@180");
        RoomLibrary rooms = await CompileAsync(library);
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        LinkedLevel dry = await RoomPropHarness.LinkAsync(await CompileAsync(Library([])), level);

        Assert.Single(RoomOverlayHarness.OfClass(linked.Bsp, "water_lod_control"));
        Assert.Single(RoomOverlayHarness.OfClass(await CompileFlatAsync(library, level), "water_lod_control"));
        Assert.Equal(dry.EntityBudget!.Listed + 1, linked.EntityBudget!.Listed);
        Assert.Equal(EntityLump.Parse(linked.Bsp[BspLump.Entities]).Count, linked.EntityBudget.Listed);
    }

    /// <summary>
    /// Water is lit like any face (the rooms design, 4.6): a room with a
    /// pool of a water vrad lights, alone and capped, links to vrad of its
    /// own linked map at a turn and without one, the same styles on every
    /// face and the same luxels on every face more than one luxel across,
    /// its water surfaces among them.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task ALitPoolLinksToVradOfItsOwnLink(int rotation)
    {
        VmfDocument library = Library(
            [(0, Water(Pool, material: LitWater))],
            (0, RoomLightHarness.Light(930, new Vec3(160, 160, 150))));
        RoomLightHarness.WorldAlign(library);
        RoomLibrary rooms = await CompileLitAsync(library);
        LinkedLevel linked = await RoomLightHarness.LinkAsync(rooms, RoomPropHarness.Level($"hub@{rotation}"));
        BspData relit = await RelightAsync(linked.Bsp);

        (bool styles, int luxels, int differ, _) = LitCompare.SameFaces(linked.Bsp, relit);
        Assert.True(styles);
        Assert.True(luxels > 0);
        Assert.Equal(0, differ);
        Assert.Contains(
            BspStructView.As<DFace>(linked.Bsp[BspLump.Faces]).ToArray(),
            f => f.SurfaceFogVolumeId >= 0 && f.LightOfs >= 0 && RoomBrushHarness.Material(linked.Bsp, f.TexInfo) == LitWater);
        Assert.True(linked.Bsp[BspLump.LeafWaterData].Data.Span.SequenceEqual(relit[BspLump.LeafWaterData].Data.Span));
    }

    // ---- the pack and determinism ------------------------------------------------------------

    /// <summary>
    /// The water section round-trips through a pack: a room with water has
    /// one and a room without none, the loaded rooms link to the same bytes,
    /// and the water stored at one turn (the link turning it) links to the
    /// same bytes as the four turns stored.
    /// </summary>
    [Fact]
    public async Task WaterRoundTripsThroughAPack()
    {
        RoomLibrary rooms = await CompileAsync(PoolLibrary());
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
        Assert.NotNull(index.Find("hub")!.Find(RoomWater.SectionTag));
        Assert.Null(index.Find("other")!.Find(RoomWater.SectionTag));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            pack, index, [new RoomPackRequest("hub", [1, 3]), new RoomPackRequest("other", [0, 2])]);
        Assert.Equal(4, loaded[0].WaterOfCompile!.TurnCount);
        Assert.Null(loaded[1].WaterOfCompile);
        Assert.Equal(expected, await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf([.. loaded]), level)));

        RoomLibrary once = RoomPropHarness.RoomsOf(
            [.. rooms.Rooms.Select(r => r.Water is { } w ? r with { Water = w.WithTurnZeroOnly() } : r)]);
        Assert.Equal(1, once.Get("hub").WaterOfCompile!.TurnCount);
        Assert.Equal(expected, await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(once, level)));
    }

    /// <summary>A pack of rooms with water is the same bytes at one thread and four, run after run.</summary>
    [Fact]
    public async Task APackOfRoomsWithWaterIsTheSameBytesAtAnyThreadCount()
    {
        async Task<byte[]> PackAsync(int degree)
        {
            RoomLibrary rooms = await CompileAsync(TwoPools(), degree);
            List<RoomPackItem> items = [];
            foreach (string name in new[] { "hub", "other" })
            {
                items.Add(await RoomPackItem.CreateAsync(rooms.Find(name)!));
            }

            using MemoryStream pack = new();
            await RoomPack.SaveAsync(items, pack);
            return pack.ToArray();
        }

        byte[] serial = await PackAsync(1);
        Assert.Equal(serial, await PackAsync(4));
        Assert.Equal(serial, await PackAsync(1));
    }

    /// <summary>A level with water links to the same bytes at one thread and four.</summary>
    [Fact]
    public async Task ALevelWithWaterIsTheSameBytesAtAnyThreadCount()
    {
        RoomLibrary rooms = await CompileAsync(TwoPools());
        LevelGrid level = RoomPropHarness.Level("hub@90, other", "other@180, hub@270");
        byte[] serial = await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(rooms, level, degree: 1));
        Assert.Equal(serial, await RoomOverlayHarness.BytesAsync(await RoomPropHarness.LinkAsync(rooms, level, degree: 4)));
    }

    /// <summary>A linked level with water passes the loader's checks with no error.</summary>
    [Fact]
    public async Task ALinkedLevelWithWaterPassesTheLoaderChecks()
    {
        RoomLibrary rooms = await CompileAsync(TwoPools());
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub@90, other", "other@180, hub@270"));
        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    // ---- water overlays -------------------------------------------------------------------------

    /// <summary>
    /// The hub's pool with two water overlays on its surface: one from an
    /// <c>info_overlay_transition</c>, one from the library's world (which
    /// the split gives the hub as an entity of its own), at a point off the
    /// grid with a turned basis; and the other room's pool
    /// (<see cref="ShowcaseLibrary"/>).
    /// </summary>
    private static VmfDocument OverlayPool() => ShowcaseLibrary();

    /// <summary>
    /// Water overlays at every quarter turn: the linked lump holds the
    /// placements' in link order, numbered from 513, each moved and turned
    /// with its room, and the flattened level's compile holds the same
    /// records bit for bit but the face lists, whose faces cover the same
    /// area of each overlay.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task WaterOverlaysLinkAsTheyFlattenAtEveryRotation(int rotation)
    {
        VmfDocument library = OverlayPool();
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, hub@{(rotation + 180) % 360}");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal([513, 514, 515, 516], WaterOverlays(linked.Bsp).Select(o => o.Id));
        Assert.Equal(ObservedWaterOverlays(flat), ObservedWaterOverlays(linked.Bsp));
        Assert.All(WaterOverlays(linked.Bsp), o => Assert.True(o.GetFaceCount() > 0));
    }

    /// <summary>
    /// The split gives the world's water overlays to the room whose cell
    /// holds each, as an <c>info_overlay_transition</c> first among its
    /// entities, and moves every water overlay's vectors with the room.
    /// </summary>
    [Fact]
    public void TheSplitCarriesTheWorldsWaterOverlays()
    {
        LibraryRoom hub = RoomLibraryVmf.Split(OverlayPool())[0];
        VmfChunk first = hub.Document.GetChunks(MapFileLoader.EntityChunk).First();
        Assert.Equal("info_overlay_transition", first.GetValue("classname"));
        VmfChunk data = first.GetChunk(MapFileLoader.OverlayTransitionChunk)!.GetChunk(MapFileLoader.OverlayDataChunk)!;
        Assert.Equal("[80.3 71.1 56]", data.GetValue("BasisOrigin"));
        Assert.Null(hub.Document.GetChunk(MapFileLoader.WorldChunk)!.GetChunk(MapFileLoader.OverlayTransitionChunk));

        LibraryRoom other = RoomLibraryVmf.Split(Library([], (1, Transition(900, new Vec3(72, 72, 56), WaterOverlay(new Vec3(64, 64, 56), "1")))))[1];
        VmfChunk moved = other.Document.GetChunks(MapFileLoader.EntityChunk).Single(e => e.GetValue("classname") == "info_overlay_transition");
        Assert.Equal("[64 64 56]", moved.GetChunk(MapFileLoader.OverlayTransitionChunk)!.GetChunk(MapFileLoader.OverlayDataChunk)!.GetValue("BasisOrigin"));
    }

    /// <summary>
    /// A water overlay the split cannot give a room is refused: one of the
    /// world's in the gaps between rooms, and one an entity carries outside
    /// its room's cell.
    /// </summary>
    [Fact]
    public void AWaterOverlayOutsideItsRoomIsRefused()
    {
        VmfDocument gaps = Library([]);
        VmfChunk transition = new(MapFileLoader.OverlayTransitionChunk);
        transition.Children.Add(WaterOverlay(new Vec3(RoomHarness.Cell + 10, 64, 56), "1"));
        gaps.GetChunk(MapFileLoader.WorldChunk)!.Children.Add(transition);
        Assert.Equal(
            "the library has a water overlay at (266 64 56) in the gaps between rooms; a water overlay belongs to the room whose cell holds its BasisOrigin.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(gaps)).Message);

        VmfDocument astray = Library([], (0, Transition(900, new Vec3(72, 72, 56), WaterOverlay(new Vec3(300, 64, 56), "1"))));
        Assert.Equal(
            "room hub: entity 900 (info_overlay_transition) has a water overlay at (300 64 56) outside the room's cell; a water overlay belongs to the room whose cell holds its BasisOrigin.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(astray)).Message);
    }

    /// <summary>
    /// Moving a water overlay: its origin moved as a point and its basis
    /// turned as directions, written in brackets; at no turn the basis is
    /// left as written; a vector that is not three numbers in brackets is
    /// refused, naming the entity.
    /// </summary>
    [Fact]
    public void AWaterOverlayMovesItsVectorsInBrackets()
    {
        VmfChunk entity = Transition(900, new Vec3(1, 2, 3), WaterOverlay(new Vec3(10, 20, 56), "1", "1 0 0", "0 1 0"));
        VmfChunk turned = VmfPlacement.MoveEntity(entity, new QuarterTurn(1, new Vec3(256, 0, 0)));
        VmfChunk data = turned.GetChunk(MapFileLoader.OverlayTransitionChunk)!.GetChunk(MapFileLoader.OverlayDataChunk)!;
        Assert.Equal("[236 10 56]", data.GetValue("BasisOrigin"));
        Assert.Equal("[0 1 0]", data.GetValue("BasisU"));
        Assert.Equal("[-1 0 0]", data.GetValue("BasisV"));
        Assert.Equal("[0 0 1]", data.GetValue("BasisNormal"));
        Assert.Equal("[-16 -16 0]", data.GetValue("uv0"));

        VmfChunk moved = VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(new Vec3(0, 0, 8)));
        VmfChunk movedData = moved.GetChunk(MapFileLoader.OverlayTransitionChunk)!.GetChunk(MapFileLoader.OverlayDataChunk)!;
        Assert.Equal("[10 20 64]", movedData.GetValue("BasisOrigin"));
        Assert.Equal("[1 0 0]", movedData.GetValue("BasisU"));

        VmfChunk bad = Transition(900, new Vec3(1, 2, 3), WaterOverlay(new Vec3(10, 20, 56), "1"));
        bad.GetChunk(MapFileLoader.OverlayTransitionChunk)!.GetChunk(MapFileLoader.OverlayDataChunk)!.Keys.First(k => k.Name == "BasisOrigin").Value = "10 20 56";
        Assert.Equal(
            "entity 900 (info_overlay_transition): a water overlay's BasisOrigin \"10 20 56\" is not three numbers in brackets.",
            Assert.Throws<RoomLibraryException>(() => VmfPlacement.MoveEntity(bad, new QuarterTurn(1, Vec3.Zero))).Message);
    }

    /// <summary>
    /// The flatten renames a water overlay's <c>sides</c> list to the
    /// renumbered side ids, as it renames an entity's: the flattened
    /// overlay names the moved pool's surface.
    /// </summary>
    [Fact]
    public void TheFlattenRenamesAWaterOverlaysSides()
    {
        VmfDocument flat = LevelFlattener.Flatten(RoomPropHarness.Level("hub, other"), OverlayPool());
        List<VmfChunk> datas = [.. flat.GetChunks(MapFileLoader.EntityChunk)
            .SelectMany(e => e.GetChunks(MapFileLoader.OverlayTransitionChunk))
            .SelectMany(t => t.GetChunks(MapFileLoader.OverlayDataChunk))];
        Assert.Equal(2, datas.Count);
        HashSet<string> tops = [.. flat.GetChunk(MapFileLoader.WorldChunk)!.GetChunks(MapFileLoader.SolidChunk)
            .Select(s => s.GetChunks(MapFileLoader.SideChunk).First())
            .Where(side => side.GetValue("material") == CheapWater)
            .Select(side => side.GetValue("id")!)];
        Assert.All(datas, d => Assert.Contains(d.GetValue("sides")!, tops));
    }

    /// <summary>
    /// A water overlay on a socket's plug is refused by the split, as an
    /// <c>info_overlay</c> on one is, whether an entity or the world carries
    /// it; one on a side the room does not have is not.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AWaterOverlayOnAPlugIsRefused(bool entity, bool onPlug)
    {
        VmfDocument library = RoomOverlayHarness.Library();
        VmfChunk plug = library.GetChunk(MapFileLoader.WorldChunk)!.GetChunks(MapFileLoader.SolidChunk)
            .First(s => s.GetChunks(MapFileLoader.SideChunk).Any(side => side.GetValue("material") == RoomHarness.Trigger));
        string side = onPlug ? plug.GetChunks(MapFileLoader.SideChunk).First().GetValue("id")! : "123456789";
        VmfChunk overlay = WaterOverlay(new Vec3(64, 64, 56), side);
        if (entity)
        {
            library.Chunks.Add(VmfPlacement.MoveEntity(Transition(900, new Vec3(72, 72, 56), overlay), QuarterTurn.Translation(Vec3.Zero)));
        }
        else
        {
            VmfChunk transition = new(MapFileLoader.OverlayTransitionChunk);
            transition.Children.Add(overlay);
            library.GetChunk(MapFileLoader.WorldChunk)!.Children.Add(transition);
        }

        if (!onPlug)
        {
            Assert.NotEmpty(RoomLibraryVmf.Split(library));
            return;
        }

        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library));
        Assert.StartsWith("room hub: a water overlay names brush side ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("'s plug.", refused.Message, StringComparison.Ordinal);
    }

    // ---- refusals ------------------------------------------------------------------------------

    /// <summary>
    /// Water that reaches a socket's plug box is refused by the room compile
    /// with the rooms design's text (15.4, the 4.6 socket row), whether it
    /// touches the plug's face or reaches into the box; water that only
    /// meets the plug box along an edge is not.
    /// </summary>
    [Theory]
    [InlineData(16, 104, 128, 136, 56, true)]   // along the west wall, touching the west plug's face
    [InlineData(4, 100, 200, 140, 56, true)]    // reaching into the west plug box
    [InlineData(16, 40, 80, 80, 56, false)]     // against the west wall below the door's edge (80): meets the plug box at an edge only
    public async Task WaterReachingASocketIsRefused(float x0, float y0, float x1, float y1, float z1, bool refused)
    {
        RoomDefinition hub = RoomPropHarness.Hub;
        VmfDocument room = RoomHarness.BuildRoomModel(hub);
        room.GetChunk(MapFileLoader.WorldChunk)!.Children.Add(Water(new Box(new Vec3(x0, y0, 16), new Vec3(x1, y1, z1))));
        Task<RoomObject> compile = RoomCompiler.CompileAsync(room, hub, await ContextAsync("hub"));
        if (refused)
        {
            RoomLintException refusal = await Assert.ThrowsAsync<RoomLintException>(() => compile);
            Assert.Equal("room hub: water reaches socket \"west\"; water may not touch a door plug.", refusal.Message);
        }
        else
        {
            Assert.NotNull((await compile).WaterOfCompile);
        }
    }

    /// <summary>
    /// Whether two boxes meet over more than an edge: overlapping, or
    /// touching over a face, meets; touching along an edge or a corner, or
    /// apart, does not.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 10, 10, 10, true)]
    [InlineData(10, 0, 0, 20, 10, 10, true)]
    [InlineData(10, 10, 0, 20, 20, 10, false)]
    [InlineData(10, 10, 10, 20, 20, 20, false)]
    [InlineData(11, 0, 0, 20, 10, 10, false)]
    public void BoxesMeetOverAFace(float x0, float y0, float z0, float x1, float y1, float z1, bool meets)
    {
        Box a = new(new Vec3(0, 0, 0), new Vec3(10, 10, 10));
        Assert.Equal(meets, RoomWater.Meets(a, new Box(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1))));
    }

    /// <summary>
    /// A room whose compile has water and no water data bound to it (a pack
    /// written before water was carried) is refused, naming the room; the
    /// old refusal of a water leaf is gone.
    /// </summary>
    [Fact]
    public async Task ARoomWithWaterButNoWaterDataIsRefused()
    {
        RoomLibrary rooms = await CompileAsync(PoolLibrary());
        RoomLibrary without = RoomPropHarness.RoomsOf([.. rooms.Rooms.Select(r => r with { Water = null, Link = null })]);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => RoomPropHarness.LinkAsync(without, RoomPropHarness.Level("hub, other")));
        Assert.Equal(
            "room hub has water but no water data from its compile (a pack written before the link carried water,"
            + " or a room built without ssmap room); recompile the library with ssmap room.",
            refused.Message);
        Assert.DoesNotContain("water leaf", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lumps vbsp would not write are refused as damaged, naming the room: a
    /// leaf or a face naming a water record the room lacks, a water overlay
    /// whose id is not its place.
    /// </summary>
    [Theory]
    [InlineData("leaf", "room hub's leaf")]
    [InlineData("face", "room hub's face")]
    [InlineData("overlay", "room hub's water overlay 0 has id 7")]
    public void WaterLumpsVbspWouldNotWriteAreRefused(string what, string expected)
    {
        BspData bsp = new();
        bsp.SetLump(BspLump.LeafWaterData, new byte[12]);
        DLeaf leaf = new() { LeafWaterDataId = what == "leaf" ? (short)3 : (short)0 };
        DFace face = new() { SurfaceFogVolumeId = what == "face" ? (short)5 : (short)-1 };
        bsp.SetLump(BspLump.Leafs, Bytes([leaf]));
        bsp.SetLump(BspLump.Faces, Bytes([face]));
        if (what == "overlay")
        {
            DWaterOverlay overlay = new() { Id = 7 };
            bsp.SetLump(BspLump.WaterOverlays, Bytes([overlay]));
        }

        LinkException refused = Assert.Throws<LinkException>(() => RoomWater.Build("hub", bsp));
        Assert.StartsWith(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A water section that does not fit its room is refused as damaged,
    /// naming the room and the section: another record count, another water
    /// overlay count, a turn count other than 1 or 4, bytes after its end.
    /// </summary>
    [Theory]
    [InlineData(0, "1 water data records; the room has 2")]
    [InlineData(1, "2 water overlays; the room has 0")]
    [InlineData(2, "3 turns of water")]
    [InlineData(3, "1 bytes after its end")]
    public async Task ADamagedWaterSectionIsRefused(int damage, string expected)
    {
        RoomObject hub = (await CompileAsync(PoolLibrary())).Get("hub");
        byte[] section = hub.WaterOfCompile!.ToSection().Bytes.ToArray();
        byte[] payload = section[9..];
        int fluidsEnd = FluidsEnd(payload);
        switch (damage)
        {
            case 0:
                // The section records one; the room below has two.
                break;
            case 1:
                payload[fluidsEnd + 3] = 2;
                break;
            case 2:
                payload[fluidsEnd + 7] = 3;
                break;
            default:
                payload = [.. payload, 0];
                break;
        }

        byte[] damaged = RoomLinkSections.Encode(payload, RoomLinkCodec.None);
        BspData room = new();
        room.SetLump(BspLump.LeafWaterData, new byte[damage == 0 ? 24 : 12]);
        LinkException refused = Assert.Throws<LinkException>(() => RoomWater.Read(damaged, "hub", room));
        Assert.Contains($"its \"{RoomWater.SectionTag}\" section", refused.Message, StringComparison.Ordinal);
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    // ---- the collision keydata -----------------------------------------------------------------

    /// <summary>
    /// A world's <c>fluid</c> blocks are read beside its static solids: each
    /// one's index, surface property, damping, contents and plane; the
    /// static solids are what the two-argument read returns, as before.
    /// </summary>
    [Fact]
    public void TheFluidBlocksRead()
    {
        List<(int Index, RoomWaterFluid Fluid)> fluids = [];
        (List<(int Index, int Contents)> statics, _, _) = LevelLinker.ParseKeyData(
            "staticsolid {\n\"index\" \"0\"\n\"contents\" \"1\"\n}\n"
            + "fluid {\n\"index\" \"1\"\n\"surfaceprop\" \"slime\"\n\"damping\" \"0.010000\"\n\"contents\" \"268435488\"\n"
            + "\"surfaceplane\" \"0.000000 0.000000 1.000000 56.500000 \"\n\"currentvelocity\" \"0.000000 0.000000 0.000000 \"\n}\n\0",
            "r",
            fluids);

        Assert.Equal([(0, 1)], statics);
        (int index, RoomWaterFluid fluid) = Assert.Single(fluids);
        Assert.Equal(1, index);
        Assert.Equal(new RoomWaterFluid("slime", 0.01f, 268435488, new Vec3(0, 0, 1), 56.5f), fluid);
    }

    /// <summary>A <c>fluid</c> block missing a key, or with a plane that is not four numbers, is refused naming it.</summary>
    [Theory]
    [InlineData("fluid {\n\"index\" \"1\"\n}\n", "block \"fluid\" has no \"surfaceprop\"")]
    [InlineData("fluid {\n\"index\" \"1\"\n\"surfaceprop\" \"w\"\n\"damping\" \"x\"\n}\n", "\"damping\" is \"x\", not 1 number")]
    [InlineData("fluid {\n\"index\" \"1\"\n\"surfaceprop\" \"w\"\n\"damping\" \"0\"\n\"contents\" \"32\"\n\"surfaceplane\" \"0 0 1\"\n}\n", "\"surfaceplane\" is \"0 0 1\", not 4 numbers")]
    public void AFluidBlockThatDoesNotReadIsRefused(string text, string expected)
    {
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.ParseKeyData(text, "r", []));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The leaf water data and water overlay caps are refused naming the placement that crossed them, and not a record before.</summary>
    [Fact]
    public void TheWaterCapsAreRefusedNamingThePlacement()
    {
        LevelLinker.WaterDataLimit("hub", 3, 4, 32768);
        LinkException data = Assert.Throws<LinkException>(() => LevelLinker.WaterDataLimit("hub", 3, 4, 32769));
        Assert.Equal(
            "room hub at cell (3, 4) pushes the link to 32769 leaf water data records; vbsp writes at most 32768 (MAX_MAP_LEAFWATERDATA).",
            data.Message);
        LevelLinker.WaterOverlayLimit("hub", 3, 4, 16384);
        LinkException overlays = Assert.Throws<LinkException>(() => LevelLinker.WaterOverlayLimit("hub", 3, 4, 16385));
        Assert.Equal(
            "room hub at cell (3, 4) pushes the link to 16385 water overlays; a map holds at most 16384 (MAX_MAP_WATEROVERLAYS).",
            overlays.Message);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>What each point of a lattice over the level's cells holds (<see cref="At"/>), off every whole 8 units.</summary>
    internal static List<string> Points(BspData bsp, int columns, int rows)
    {
        List<string> points = [];
        for (float x = 4; x < columns * RoomHarness.Cell; x += 16)
        {
            for (float y = 4; y < rows * RoomHarness.Cell; y += 16)
            {
                foreach (float z in (ReadOnlySpan<float>)[20, 36, 52, 60, 84, 92, 140])
                {
                    points.Add(string.Create(CultureInfo.InvariantCulture, $"{x} {y} {z}: {At(bsp, new Vec3(x, y, z))}"));
                }
            }
        }

        return points;
    }

    /// <summary>A map's water records as surface height, lowest point and material, sorted.</summary>
    internal static List<string> Records(BspData bsp) =>
        [.. WaterData(bsp).Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.SurfaceZ} {d.MinZ} {RoomBrushHarness.Material(bsp, d.SurfaceTexInfoId)}"))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// A map's fluids as its world collision holds them: each block's plane,
    /// contents and surface property, its surface's volume and the extent of
    /// its convexes' points, sorted.
    /// </summary>
    internal static List<string> Fluids(BspData bsp)
    {
        PhysCollideModel world = PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span)[0];
        List<(int Index, RoomWaterFluid Fluid)> fluids = [];
        _ = LevelLinker.ParseKeyData(world.KeyText, "map", fluids);
        List<string> observed = [];
        foreach ((int index, RoomWaterFluid fluid) in fluids)
        {
            byte[] blob = world.Solids[index];
            Vec3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(blob)))
            {
                for (int p = 0; p < ledge.PointCount; p++)
                {
                    (float x, float y, float z) = IvpCollideQueries.HlPoint(ledge, p);
                    min = new Vec3(Math.Min(min.X, x), Math.Min(min.Y, y), Math.Min(min.Z, z));
                    max = new Vec3(Math.Max(max.X, x), Math.Max(max.Y, y), Math.Max(max.Z, z));
                }
            }

            observed.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{fluid.SurfaceProp} {fluid.Contents} {fluid.Normal} {fluid.Dist} vol {IvpCollideQueries.CollideVolume(blob):0} {R(min)} {R(max)}"));
        }

        return [.. observed.Order(StringComparer.Ordinal)];

        static string R(Vec3 v) => string.Create(CultureInfo.InvariantCulture, $"({Math.Round(v.X, 1)} {Math.Round(v.Y, 1)} {Math.Round(v.Z, 1)})");
    }

    private static int CountOf(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Where a water payload's fluids end: past the revision, the record count, the fluid count and each fluid.</summary>
    private static int FluidsEnd(byte[] payload)
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

    private static byte[] Bytes<T>(T[] items)
        where T : unmanaged =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(items.AsSpan()).ToArray();
}
