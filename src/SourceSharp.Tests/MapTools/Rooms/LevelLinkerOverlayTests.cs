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
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomOverlayHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Overlays through the link (section 4.9 of the rooms design): every
/// placed room's <c>info_overlay</c> records are carried, moved and turned
/// with the room at every quarter turn, their ids, texinfos and faces
/// rebased and a named one's accessor renumbered, and the level's overlays
/// agree with the flattened level's vbsp compile; the pack carries what
/// the link needs, the output is a function of the level, an overlay on a
/// socket's plug is refused, and a named overlay costs the level its one
/// accessor entity.
/// </summary>
public sealed class LevelLinkerOverlayTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    private static readonly Vec3 FloorAt = new(80, 200, 16);
    private static readonly Vec3 MatAt = new(72, 144, 24);

    /// <summary>Off the half-unit grid, so the link's float sums are held to the flatten's.</summary>
    private static readonly Vec3 OtherAt = new(100.1f, 60.3f, 16);

    /// <summary>The hub's overlays: one on the floor, unnamed; one on the mat, named, faded, drawn second.</summary>
    private static (int, VmfChunk)[] HubOverlays =>
    [
        (0, MatEntity()),
        (0, Overlay(600, FloorAt, FloorSide.ToString(CultureInfo.InvariantCulture))),
        (0, Overlay(601, MatAt, MatSide.ToString(CultureInfo.InvariantCulture),
            ("targetname", "mark"), ("fademindist", "300"), ("fademaxdist", "600"), ("RenderOrder", "1"))),
    ];

    /// <summary>The other room's overlay: on its floor, at a point off the grid, with a turned basis.</summary>
    private static (int, VmfChunk) OtherOverlay =>
        (1, OverlayWithBasis(610, OtherAt, FloorSide.ToString(CultureInfo.InvariantCulture), "0 1 0", "-1 0 0", "0 0 1"));

    // ---- placement, turned -------------------------------------------------------------------

    /// <summary>
    /// A level of a hub with two overlays and another room with one, at
    /// every quarter turn: the linked lump holds the three in link order,
    /// numbered 0 to 2; each origin is its room-local <c>BasisOrigin</c>
    /// through the placement, its <c>BasisU</c> and normal turned, its UV
    /// points, extents, render order and fades the room's; its faces cover
    /// its whole square; the named one's accessor names it and carries its
    /// keys moved; and the flattened level's compile holds the same
    /// overlays, bit for bit but the face lists, and the same accessor but
    /// its <c>sides</c> (the room's side ids against the flattened map's).
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelHoldsItsRoomsOverlaysMovedAtEveryRotation(int rotation)
    {
        VmfDocument library = Library([.. HubOverlays, OtherOverlay]);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{rotation}");
        LinkedLevel linked = await LinkAsync(rooms, level);
        int turns = rotation / 90;
        RoomTransform hub = new(new RoomPlacement("hub", 0, 0, turns), RoomHarness.Cell);
        RoomTransform other = new(new RoomPlacement("other", 1, 0, turns), RoomHarness.Cell);

        DOverlay[] overlays = Overlays(linked.Bsp);
        Assert.Equal(3, overlays.Length);
        Assert.Equal([0, 1, 2], overlays.Select(o => o.Id));
        Assert.Equal(hub.Apply(FloorAt), overlays[0].Origin);
        Assert.Equal(hub.Apply(MatAt), overlays[1].Origin);
        Assert.Equal(Turned(new Vec3(1, 0, 0), turns), U(overlays[0]));
        Assert.Equal(Turned(new Vec3(0, 1, 0), turns), U(overlays[2]));
        Assert.All(overlays, o => Assert.Equal(new Vec3(0, 0, 1), o.BasisNormal));
        Assert.All(overlays, o => Assert.Equal(0f, o.UvPoints[3].Z));
        Assert.Equal(1, overlays[1].GetRenderOrder());
        Assert.Equal(new DOverlayFade { FadeDistMinSq = 90000, FadeDistMaxSq = 360000 }, Fades(linked.Bsp)[1]);
        Assert.All(overlays, o => Assert.Equal("unit/overlay", RoomBrushHarness.Material(linked.Bsp, o.TexInfo)));
        Assert.Single(overlays.Select(o => o.TexInfo).Distinct());
        Assert.All(overlays, o => Assert.Equal(1024.0, Covered(linked.Bsp, o), 2));

        // The other room's overlay, off the grid, moved as the flatten moves
        // its key: the room's turned point plus the translation.
        Vec3 local = Overlays(rooms.Get("other").Bsp)[0].Origin;
        Assert.Equal(RoomStaticProps.Unsigned(RoomTransform.Rotate(local, turns) + other.Apply(Vec3.Zero)), overlays[2].Origin);

        BspData flat = await CompileFlatAsync(library, level);
        Assert.Equal(Observed(flat), Observed(linked.Bsp));

        BspEntity accessor = Assert.Single(OfClass(linked.Bsp, RoomOverlays.AccessorClass));
        Assert.Equal("1", accessor.Get(RoomOverlays.IdKey));
        Assert.Equal("mark", accessor.Get("targetname"));
        Assert.Equal(VmfPlacement.Format(hub.Apply(MatAt)), accessor.Get(RoomOverlays.OriginKey));
        Assert.Equal(VmfPlacement.Format(Turned(new Vec3(1, 0, 0), turns)), accessor.Get("BasisU"));
        BspEntity flatAccessor = Assert.Single(OfClass(flat, RoomOverlays.AccessorClass));
        Assert.Equal(Keys(flatAccessor), Keys(accessor));
        Assert.NotEqual(flatAccessor.Get("sides"), accessor.Get("sides"));
    }

    /// <summary>
    /// A room placed twice: each placement's overlays take their own ids, the
    /// second's after the first's, and each named overlay's accessor names
    /// its own placement's overlay, as in the flattened level's compile.
    /// </summary>
    [Fact]
    public async Task EachPlacementsOverlaysTakeTheirOwnIds()
    {
        VmfDocument library = Library(HubOverlays);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub, hub@90");
        LinkedLevel linked = await LinkAsync(rooms, level);

        Assert.Equal([0, 1, 2, 3], Overlays(linked.Bsp).Select(o => o.Id));
        Assert.Equal(["1", "3"], OfClass(linked.Bsp, RoomOverlays.AccessorClass).Select(e => e.Get(RoomOverlays.IdKey)!));
        BspData flat = await CompileFlatAsync(library, level);
        Assert.Equal(Observed(flat), Observed(linked.Bsp));
        Assert.Equal(
            OfClass(flat, RoomOverlays.AccessorClass).Select(e => e.Get(RoomOverlays.IdKey)),
            OfClass(linked.Bsp, RoomOverlays.AccessorClass).Select(e => e.Get(RoomOverlays.IdKey)));
    }

    /// <summary>
    /// A level whose rooms have no overlays carries neither overlay lump, as
    /// before overlays were carried.
    /// </summary>
    [Fact]
    public async Task ALevelWithoutOverlaysCarriesNoOverlayLumps()
    {
        LinkedLevel linked = await LinkAsync(await CompileAsync(Library()), RoomPropHarness.Level("hub, other@90"));
        Assert.Equal(0, linked.Bsp[BspLump.Overlays].Length);
        Assert.Equal(0, linked.Bsp[BspLump.OverlayFades].Length);
    }

    // ---- faces the level does not draw ---------------------------------------------------------

    /// <summary>
    /// An overlay on a brush entity that <c>room_needs</c> drops loses that
    /// entity's faces, and keeps them where the entity is kept, as in the
    /// flattened level's compile, whose map has no such side.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOverlayOnAnOmittedBrushEntityLosesItsFaces(bool neighbour)
    {
        VmfChunk door = RoomBrushHarness.Door(6000, new Vec3(180, 40, 16), new Vec3(220, 80, 48), (RoomNeeds.Key, "north"));
        VmfDocument library = Library(
            (0, door),
            (0, Overlay(602, new Vec3(200, 60, 48), (6000 * 10 * 8).ToString(CultureInfo.InvariantCulture))));
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = neighbour ? RoomPropHarness.Level("other", "hub") : RoomPropHarness.Level("hub");
        LinkedLevel linked = await LinkAsync(rooms, level);

        DOverlay overlay = Assert.Single(Overlays(linked.Bsp));
        Assert.Equal(neighbour, overlay.GetFaceCount() > 0);
        Assert.Equal(neighbour ? 1024.0 : 0.0, Covered(linked.Bsp, overlay), 2);
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), Observed(linked.Bsp));
    }

    /// <summary>
    /// A face of a jointed socket's plug is left out of an overlay's list
    /// (the link draws it nodraw, the flatten has no plug there) and kept at
    /// a capped socket, where the plug is a wall. The pack refuses an
    /// overlay naming a plug, so the room here is edited after its compile.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APlugFaceLeavesAnOverlayWhereTheSocketIsJointed(bool jointed)
    {
        RoomLibrary rooms = await CompileAsync(Library((0, Overlay(600, FloorAt, FloorSide.ToString(CultureInfo.InvariantCulture)))));
        RoomObject hub = rooms.Get("hub");
        int plugFace = PlugFace(hub, "east");
        int floorFaces = Overlays(hub.Bsp)[0].GetFaceCount();
        RoomObject edited = RoomHarness.WithLumps(hub, bsp =>
        {
            DOverlay[] records = Overlays(bsp);
            records[0].Faces[floorFaces] = plugFace;
            records[0].FaceCountAndRenderOrder = (ushort)(floorFaces + 1);
            bsp.SetLump(BspLump.Overlays, System.Runtime.InteropServices.MemoryMarshal.AsBytes(records.AsSpan()).ToArray());
        });
        edited = edited with { Overlays = RoomOverlays.Build("hub", edited.Bsp) };
        RoomLibrary library = RoomPropHarness.RoomsOf(edited, rooms.Get("other"));

        LinkedLevel linked = await LinkAsync(library, jointed ? RoomPropHarness.Level("hub, other") : RoomPropHarness.Level("hub"));
        Assert.Equal(jointed ? floorFaces : floorFaces + 1, Assert.Single(Overlays(linked.Bsp)).GetFaceCount());
    }

    /// <summary>An overlay naming a face its room does not have is refused as damaged.</summary>
    [Fact]
    public async Task AnOverlayOnAFaceTheRoomLacksIsRefused()
    {
        RoomLibrary rooms = await CompileAsync(Library((0, Overlay(600, FloorAt, FloorSide.ToString(CultureInfo.InvariantCulture)))));
        RoomObject hub = rooms.Get("hub");
        int faces = BspStructView.Count<DFace>(hub.Bsp[BspLump.Faces]);
        RoomObject edited = RoomHarness.WithLumps(hub, bsp =>
        {
            DOverlay[] records = Overlays(bsp);
            records[0].Faces[0] = faces;
            bsp.SetLump(BspLump.Overlays, System.Runtime.InteropServices.MemoryMarshal.AsBytes(records.AsSpan()).ToArray());
        });
        edited = edited with { Overlays = RoomOverlays.Build("hub", edited.Bsp) };

        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => LinkAsync(RoomPropHarness.RoomsOf(edited), RoomPropHarness.Level("hub")));
        Assert.Equal($"room hub has an overlay on face {faces}; the room has {faces} faces.", refused.Message);
    }

    // ---- refusals ----------------------------------------------------------------------------

    /// <summary>
    /// An overlay naming a side of a socket's plug is refused with the rooms
    /// design's text (15.4) by the split, so by the pack and the flatten, and
    /// by a room compile given the room's VMF; one naming the floor is not.
    /// </summary>
    [Fact]
    public async Task AnOverlayOnAPlugIsRefusedByTheSplitTheFlattenAndTheRoomCompile()
    {
        LibraryRoom hub = RoomLibraryVmf.SplitLibrary(Library()).Rooms[0];
        RoomSocket east = hub.Definition.Sockets.Single(s => s.Name == "east");
        Box plugBox = RoomLinter.SealBox(hub.Definition, east, hub.Definition.CellSize);
        VmfChunk plug = hub.Document.GetChunk(MapFileLoader.WorldChunk)!.GetChunks(MapFileLoader.SolidChunk)
            .Single(s => RoomLibraryVmf.Same(VmfPlacement.Bounds(s), plugBox));
        string side = plug.GetChunks(MapFileLoader.SideChunk).First().GetValue("id")!;
        string message = $"room hub: info_overlay 603 names brush side {side}, which is socket \"east\"'s plug.";

        VmfDocument library = Library((0, Overlay(603, new Vec3(240, 128, 100), $"{FloorSide} {side}")));
        Assert.Equal(message, Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library)).Message);
        Assert.Equal(message, Assert.Throws<RoomLibraryException>(() => LevelFlattener.Flatten(RoomPropHarness.Level("hub"), library)).Message);

        VmfDocument room = RoomLibraryVmf.SplitLibrary(Library()).Rooms[0].Document;
        room.Chunks.Add(Overlay(603, new Vec3(240, 128, 100), side));
        VbspContext context = await ContextAsync("hub");
        RoomLintException compile = await Assert.ThrowsAsync<RoomLintException>(
            () => RoomCompiler.CompileAsync(room, hub.Definition, context));
        Assert.Equal(message, compile.Message);

        Assert.Null(RoomOverlays.PlugProblem(hub.Definition, hub.Document));
        VmfDocument floor = RoomLibraryVmf.SplitLibrary(Library()).Rooms[0].Document;
        floor.Chunks.Add(Overlay(604, FloorAt, $"{FloorSide} not-a-side"));
        Assert.Null(RoomOverlays.PlugProblem(hub.Definition, floor));
    }

    /// <summary>
    /// <c>room_needs</c> on an overlay is refused by the room compile and the
    /// flatten alike: the overlay is a record of the room's compile, and the
    /// flatten would drop what the link keeps.
    /// </summary>
    [Fact]
    public async Task RoomNeedsOnAnOverlayIsRefused()
    {
        VmfDocument library = Library((0, Overlay(605, FloorAt, FloorSide.ToString(CultureInfo.InvariantCulture), (RoomNeeds.Key, "east"))));
        const string Message = "room hub: entity 605 (info_overlay) has room_needs, but an overlay is built into its room's compile and cannot be dropped.";
        Assert.Equal(Message, (await Assert.ThrowsAsync<RoomLintException>(() => CompileAsync(library))).Message);
        Assert.Equal(Message, Assert.Throws<RoomLintException>(() => LevelFlattener.Flatten(RoomPropHarness.Level("hub"), library)).Message);
    }

    /// <summary>
    /// A room whose lump has overlays but that carries no overlay data from
    /// its compile (a pack written before overlays were carried) is refused
    /// by name at link.
    /// </summary>
    [Fact]
    public async Task ARoomWithOverlaysButNoOverlayDataIsRefused()
    {
        RoomLibrary rooms = await CompileAsync(Library(HubOverlays));
        RoomObject bare = rooms.Get("hub") with { Overlays = null };
        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => LinkAsync(RoomPropHarness.RoomsOf(bare), RoomPropHarness.Level("hub")));
        Assert.Equal(
            "room hub has 2 overlays but no overlay data from its compile (a pack written before the link carried overlays,"
            + " or a room built without ssmap room); recompile the library with ssmap room.",
            refused.Message);
    }

    /// <summary>
    /// Overlay lumps that are not the ones vbsp writes are refused naming the
    /// room: a record whose id is not its place, a fade lump of another
    /// length.
    /// </summary>
    [Fact]
    public async Task OverlayLumpsVbspWouldNotWriteAreRefused()
    {
        RoomObject hub = (await CompileAsync(Library(HubOverlays))).Get("hub");
        RoomObject renumbered = RoomHarness.WithLumps(hub, bsp =>
        {
            DOverlay[] records = Overlays(bsp);
            records[1].Id = 7;
            bsp.SetLump(BspLump.Overlays, System.Runtime.InteropServices.MemoryMarshal.AsBytes(records.AsSpan()).ToArray());
        });
        Assert.Equal(
            "room hub's overlay 1 has id 7; vbsp numbers a map's overlays from 0 in order.",
            Assert.Throws<LinkException>(() => RoomOverlays.Build("hub", renumbered.Bsp)).Message);

        RoomObject faded = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.OverlayFades, new byte[8]));
        Assert.Equal(
            "room hub has 2 overlays and 1 overlay fades; vbsp writes one fade per overlay.",
            Assert.Throws<LinkException>(() => RoomOverlays.Build("hub", faded.Bsp)).Message);
    }

    /// <summary>A level past the overlays a map holds is refused naming the room and cell that crossed it.</summary>
    [Fact]
    public void ALevelPastTheOverlayCapIsRefused()
    {
        LevelLinker.LinkTotals totals = new();
        totals.Add(new LevelLinker.LinkCounts { Overlays = 300 }, "a", 0, 0);
        LinkException refused = Assert.Throws<LinkException>(() => totals.Add(new LevelLinker.LinkCounts { Overlays = 213 }, "b", 1, 0));
        Assert.Equal("room b at cell (1, 0) pushes the link to 513 overlays; a map holds at most 512 (MAX_MAP_OVERLAYS).", refused.Message);

        LevelLinker.LinkTotals full = new();
        full.Add(new LevelLinker.LinkCounts { Overlays = 512 }, "a", 0, 0);
    }

    // ---- the pack, determinism, the budget, the loader ---------------------------------------

    /// <summary>
    /// Rooms with overlays through a pack: the pack stores a room's overlays
    /// (four turns), and none for a room without, the rooms it loads carry
    /// them, and the level links to the same bytes as from the rooms in
    /// memory; a pack holding only turn 0 (the rotation count of 1, the link
    /// turning them) links to the same bytes too, so storing once or four
    /// times is a choice of speed alone (the rooms design, 15.9).
    /// </summary>
    [Fact]
    public async Task OverlaysRoundTripThroughAPack()
    {
        RoomLibrary rooms = await CompileAsync(Library(HubOverlays));
        LevelGrid level = RoomPropHarness.Level("hub@90, other@180", "other, hub@270");
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
        Assert.NotNull(index.Find("hub")!.Find(RoomOverlays.SectionTag));
        Assert.Null(index.Find("other")!.Find(RoomOverlays.SectionTag));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            pack, index, [new RoomPackRequest("hub", [1, 3]), new RoomPackRequest("other", [0, 2])]);
        Assert.Equal(4, loaded[0].OverlaysOfCompile!.TurnCount);
        Assert.Null(loaded[1].OverlaysOfCompile);
        Assert.Equal(expected, await BytesAsync(await LinkAsync(RoomPropHarness.RoomsOf([.. loaded]), level)));

        RoomLibrary once = RoomPropHarness.RoomsOf(
            [.. rooms.Rooms.Select(r => r.Overlays is { } o ? r with { Overlays = o.WithTurnZeroOnly() } : r)]);
        Assert.Equal(1, once.Get("hub").OverlaysOfCompile!.TurnCount);
        Assert.Equal(expected, await BytesAsync(await LinkAsync(once, level)));
    }

    /// <summary>
    /// A pack of rooms with overlays is the same bytes whether its rooms
    /// compiled on one thread or on four, run after run (the rooms design,
    /// 15.5): the overlay section is a function of the room's compile.
    /// </summary>
    [Fact]
    public async Task APackOfRoomsWithOverlaysIsTheSameBytesAtAnyThreadCount()
    {
        async Task<byte[]> PackAsync(int degree)
        {
            RoomLibrary rooms = await CompileAsync(Library([.. HubOverlays, OtherOverlay]), degree);
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

    /// <summary>A level with overlays links to the same bytes at one thread and at many, run after run.</summary>
    [Fact]
    public async Task ALevelWithOverlaysIsTheSameBytesAtAnyThreadCount()
    {
        RoomLibrary rooms = await CompileAsync(Library([.. HubOverlays, OtherOverlay]));
        LevelGrid level = RoomPropHarness.Level("hub@90, other, hub@180", "other@270, hub, other");
        byte[] serial = await BytesAsync(await LinkAsync(rooms, level, 1));
        Assert.Equal(serial, await BytesAsync(await LinkAsync(rooms, level, 8)));
        Assert.Equal(serial, await BytesAsync(await LinkAsync(rooms, level, 1)));
        Assert.Equal(serial, await BytesAsync(await LinkAsync(rooms, level, 8)));
    }

    /// <summary>
    /// An overlay costs the level one entity when it is named (its
    /// <c>info_overlay_accessor</c>) and none otherwise (the rooms design,
    /// 15.6), and the link's budget counts exactly the entities its lump
    /// holds.
    /// </summary>
    [Fact]
    public async Task ANamedOverlayCostsOneEntityAndAnUnnamedOneNone()
    {
        LevelGrid level = RoomPropHarness.Level("hub, other");
        LinkedLevel bare = await LinkAsync(await CompileAsync(Library()), level);
        LinkedLevel unnamed = await LinkAsync(
            await CompileAsync(Library((0, Overlay(600, FloorAt, FloorSide.ToString(CultureInfo.InvariantCulture))))), level);
        LinkedLevel named = await LinkAsync(await CompileAsync(Library(HubOverlays)), level);

        Assert.Equal(bare.Bsp[BspLump.Entities].Data.ToArray(), unnamed.Bsp[BspLump.Entities].Data.ToArray());
        Assert.Equal(bare.EntityBudget!.Edicts, unnamed.EntityBudget!.Edicts);
        Assert.Equal(bare.EntityBudget.Edicts + 1, named.EntityBudget!.Edicts);
        Assert.Equal(bare.EntityBudget.Listed + 1, named.EntityBudget.Listed);
        Assert.Equal(EntityLump.Parse(named.Bsp[BspLump.Entities]).Count, named.EntityBudget.Listed);
    }

    /// <summary>A linked level with overlays passes the loader checks <c>ssmap check</c> makes (face counts and indices).</summary>
    [Fact]
    public async Task ALinkedLevelWithOverlaysPassesTheLoaderChecks()
    {
        RoomLibrary rooms = await CompileAsync(Library([.. HubOverlays, OtherOverlay]));
        LinkedLevel linked = await LinkAsync(rooms, RoomPropHarness.Level("hub@90, other", "other@180, hub@270"));
        Assert.Equal(6, Overlays(linked.Bsp).Length);
        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static Vec3 Turned(Vec3 direction, int turns) => RoomStaticProps.Unsigned(RoomTransform.Rotate(direction, turns));

    private static Vec3 U(DOverlay overlay) => new(overlay.UvPoints[0].Z, overlay.UvPoints[1].Z, overlay.UvPoints[2].Z);

    /// <summary>An entity's keys, less the two the link and the flattened compile cannot share (<c>sides</c>, <c>hammerid</c>), sorted.</summary>
    private static List<string> Keys(BspEntity entity) =>
        [.. entity.Pairs.Where(p => p.Key is not ("sides" or "hammerid")).Select(p => $"{p.Key}={p.Value}").Order(StringComparer.Ordinal)];

    /// <summary>A drawn face of a socket's plug in a compiled room: one of plug material whose centre lies in the plug's box.</summary>
    private static int PlugFace(RoomObject room, string socket)
    {
        Box plug = RoomLinter.SealBox(room.Definition, room.Definition.Sockets.Single(s => s.Name == socket), room.Definition.CellSize);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(room.Bsp[BspLump.Faces]);
        for (int f = 0; f < faces.Length; f++)
        {
            if (RoomBrushHarness.Material(room.Bsp, faces[f].TexInfo) != RoomHarness.Trigger)
            {
                continue;
            }

            List<Vec3> corners = RoomHarness.FaceVertices(room.Bsp, faces[f]);
            Vec3 centre = corners.Aggregate(Vec3.Zero, (a, b) => a + b) * (1f / corners.Count);
            if (centre.X >= plug.Mins.X && centre.X <= plug.Maxs.X && centre.Y >= plug.Mins.Y && centre.Y <= plug.Maxs.Y
                && centre.Z >= plug.Mins.Z && centre.Z <= plug.Maxs.Z)
            {
                return f;
            }
        }

        throw new InvalidOperationException($"room {room.Definition.Name} has no drawn face on socket {socket}'s plug");
    }
}
