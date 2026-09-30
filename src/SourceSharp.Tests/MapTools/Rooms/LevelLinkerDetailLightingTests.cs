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

using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Rooms;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Detail prop lighting through the link (the rooms design, 4.4 and 9.4):
/// each prop's colour and style run are its room's bake, per stored turn,
/// replayed over the level's props as vrad of the linked map writes them,
/// with the level's switchable styles.
/// </summary>
public sealed class LevelLinkerDetailLightingTests(LitDetailFixture fixture, ITestOutputHelper output) : IClassFixture<LitDetailFixture>
{
    /// <summary>Both rooms at the four quarter turns.</summary>
    public static TheoryData<string, int> RoomsAndTurns => LevelLinkerLightingTests.RoomsAndTurns;

    /// <summary>
    /// A room with detail props alone, every socket capped, links to vrad of
    /// its own link (both ranges): the same game lumps in the same order
    /// (<c>sprp</c>, <c>dprp</c>, <c>dplh</c>, <c>dplt</c>, as vrad adds
    /// them), and every prop's colour, style count and run start, and both
    /// style lumps, the same bytes, at turn 0 and at every turn of the
    /// sunlit room, whose bake is stored per turn in the frame of the turned
    /// room (the detail props' ambient rays and sun jitter turned into it,
    /// <c>BakeFrame</c>; without that turn the sunlit room's props at 90 and
    /// 270 were off by up to 40%, p95 0.29). The hub, which no sun reaches,
    /// is stored once and turned at link: there vrad of the turned map is
    /// not quarter-turn invariant to the last bit, and a prop on the
    /// displacement whose centre sits at a shadow's edge can flip. Measured:
    /// every prop exact at every turn but one of 228 in the hub at 90 and at
    /// 270 (lit on one side, black on the other); the facts hold 99%.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoomsAndTurns))]
    public async Task ACappedRoomsDetailPropsLinkToVradOfItsLink(string room, int rotation)
    {
        string row = $"{room}@{rotation}";
        BspData linked = await fixture.LinkedAsync(row);
        BspData relit = await fixture.RelitAsync(row);
        Assert.Equal(["sprp", "dprp", "dplh", "dplt"], RoomDetailHarness.GameLumpIds(relit));
        Assert.Equal(RoomDetailHarness.GameLumpIds(relit), RoomDetailHarness.GameLumpIds(linked));

        DetailPropLump a = RoomDetailHarness.Lump(linked);
        DetailPropLump b = RoomDetailHarness.Lump(relit);
        Assert.Equal(b.Props.Select(p => RoomDetailHarness.Line(b, p, lighting: false)), a.Props.Select(p => RoomDetailHarness.Line(a, p, lighting: false)));
        // A prop is lit exactly when its colour and count are vrad's and its
        // run (in dplh: after -both the start and count are the HDR pass's)
        // holds vrad's styles and colours; a start can differ only because an
        // earlier prop's run did.
        DetailPropLightstylesLump[] runsA = StyleLump(linked, hdr: true), runsB = StyleLump(relit, hdr: true);
        int exact = a.Props.Zip(b.Props).Count(pair =>
            pair.First.Lighting.Equals(pair.Second.Lighting) && Run(runsA, pair.First).SequenceEqual(Run(runsB, pair.Second)));
        output.WriteLine($"{row}: {a.Props.Count} props, {exact} lit exactly as vrad of the link lights them");
        bool storedPerTurn = room == "other";
        if (rotation == 0 || storedPerTurn)
        {
            Assert.Equal(a.Props.Count, exact);
            Assert.Equal(b.Props.Select(p => p.LightStyles), a.Props.Select(p => p.LightStyles));
            Assert.Equal(StyleLump(relit, hdr: false), StyleLump(linked, hdr: false));
            Assert.Equal(StyleLump(relit, hdr: true), StyleLump(linked, hdr: true));
        }
        else
        {
            Assert.True(exact >= a.Props.Count * 0.99, $"{exact} of {a.Props.Count} exact");
        }

        // The runs hold the lamps' styles: the hub's named lamp, and the
        // other room's, each its room's first switchable style.
        Assert.Contains(StyleLump(linked, hdr: false), s => s.Style == LevelLightStyles.FirstSwitchable);
    }

    /// <summary>
    /// In a level of both rooms (jointed, lit with their door light), each
    /// placement's detail props carry their room's bake as the room alone
    /// links it: the same colours, counts and runs, the other room's lamp
    /// renumbered from its compile's style 32 to the level's 33 (its name
    /// comes second in the linked entity lump, as vbsp numbers the flattened
    /// map's), the hub's keeping 32. The door light reaches no detail prop:
    /// a prop is one sample point, which the design's door response would
    /// serve, and this PR leaves it at the base bake (the landed note).
    /// </summary>
    [Fact]
    public async Task EveryPlacementKeepsItsRoomsDetailLightingWithTheLevelsStyles()
    {
        BspData level = await fixture.LinkedAsync("hub, other");
        DetailPropLump lump = RoomDetailHarness.Lump(level);
        // After -both a prop's count and run start are the HDR pass's (vrad's
        // last), so its run is in dplh.
        DetailPropLightstylesLump[] runs = StyleLump(level, hdr: true);
        Dictionary<string, (DetailObjectLump Prop, DetailPropLightstylesLump[] Run)> linked = [];
        foreach (DetailObjectLump p in lump.Props)
        {
            linked[RoomDetailHarness.V(p.Origin)] = (p, Run(runs, p));
        }

        foreach ((string room, int cellX, byte renumbered) in new[] { ("hub", 0, (byte)32), ("other", 1, (byte)33) })
        {
            BspData alone = await fixture.LinkedAsync(room);
            DetailPropLump own = RoomDetailHarness.Lump(alone);
            DetailPropLightstylesLump[] ownRuns = StyleLump(alone, hdr: true);
            int withStyles = 0;
            foreach (DetailObjectLump p in own.Props)
            {
                Vec3 moved = new(p.Origin.X + (cellX * RoomHarness.Cell), p.Origin.Y, p.Origin.Z);
                (DetailObjectLump prop, DetailPropLightstylesLump[] run) = linked[RoomDetailHarness.V(RoomStaticProps.Unsigned(moved))];
                Assert.Equal(p.Lighting, prop.Lighting);
                Assert.Equal(p.LightStyleCount, prop.LightStyleCount);
                DetailPropLightstylesLump[] expected = [.. Run(ownRuns, p).Select(s => s.Style == 32 ? s with { Style = renumbered } : s)];
                Assert.Equal(expected, run);
                withStyles += run.Length > 0 ? 1 : 0;
            }

            output.WriteLine($"{room}: {own.Props.Count} props, {withStyles} with a style run");
            Assert.True(withStyles > 0);
        }
    }

    /// <summary>
    /// Lit rooms with detail props through a pack: the pack stores each lit
    /// room's detail lighting in its own section (<c>DPLT</c>, beside
    /// <c>LITE</c>, whose bytes it leaves as they were), the rooms it loads
    /// carry it, and the level links to the same bytes as from the rooms in
    /// memory.
    /// </summary>
    [Fact]
    public async Task DetailLightingRoundTripsThroughAPack()
    {
        LevelGrid level = RoomPropHarness.Level("hub@90, other@180");
        byte[] expected = await RoomOverlayHarness.BytesAsync(await RoomLightHarness.LinkAsync(fixture.Lit, level));

        using MemoryStream pack = new();
        List<RoomPackItem> items = [];
        foreach (RoomObject room in fixture.Lit.Rooms)
        {
            items.Add(await RoomPackItem.CreateAsync(room));
        }

        await RoomPack.SaveAsync(items, pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        Assert.NotNull(index.Find("hub")!.Find(RoomDetailLighting.SectionTag));
        Assert.NotNull(index.Find("other")!.Find(RoomDetailProps.SectionTag));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            pack, index, [new RoomPackRequest("hub", [1]), new RoomPackRequest("other", [2])]);
        Assert.NotNull(loaded[0].LightingOfCompile!.Payloads[0].Ldr!.Detail);
        Assert.Equal(4, loaded[1].LightingOfCompile!.Payloads.Length);
        Assert.All(loaded[1].LightingOfCompile!.Payloads, p => Assert.NotNull(p.Hdr!.Detail));
        RoomLibrary rooms = new(fixture.Lit.Kit, fixture.Lit.CellSize) { LibraryEntities = fixture.Lit.LibraryEntities, Options = fixture.Lit.Options };
        foreach (RoomObject room in loaded)
        {
            rooms.Add(room);
        }

        Assert.Equal(expected, await RoomOverlayHarness.BytesAsync(await RoomLightHarness.LinkAsync(rooms, level)));
    }

    /// <summary>
    /// Rooms lit with <c>-nodetaillight</c> carry no detail lighting (no
    /// section), and their level keeps each prop's lighting as vbsp wrote it
    /// and gets no style lump, as vrad of the level with that switch leaves
    /// it; a level of a room whose bake lit its detail props and one whose
    /// bake did not is refused.
    /// </summary>
    [Fact]
    public async Task DetailPropsLitWithNoDetailLightStayAsCompiled()
    {
        VradOptions options = LitDetailFixture.Options with { NoDetailLighting = true };
        RoomLibrary dark = await RoomLightHarness.CompileAsync(LitDetailFixture.Library, options: options, doorLight: true);
        Assert.All(dark.Rooms, r => Assert.False(RoomDetailLighting.Has(r.LightingOfCompile!)));
        Assert.Null(RoomDetailLighting.ToSection(dark.Get("hub").LightingOfCompile!));

        LevelGrid level = RoomPropHarness.Level("hub@90, other");
        BspData linked = (await RoomLightHarness.LinkAsync(dark, level)).Bsp;
        BspData relit = await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(fixture.Unlit, level)).Bsp, options);
        Assert.Equal(["sprp", "dprp"], RoomDetailHarness.GameLumpIds(linked));
        Assert.Equal(RoomDetailHarness.GameLumpIds(relit), RoomDetailHarness.GameLumpIds(linked));
        DetailPropLump lump = RoomDetailHarness.Lump(linked);
        Assert.All(lump.Props, p => Assert.Equal(new ColorRgbExp32 { R = 255, G = 255, B = 255, Exponent = 0 }, p.Lighting));
        Assert.Equal(
            RoomDetailHarness.Lump(relit).Props.Select(p => RoomDetailHarness.Line(RoomDetailHarness.Lump(relit), p)),
            lump.Props.Select(p => RoomDetailHarness.Line(lump, p)));

        RoomLibrary mixed = new(dark.Kit, dark.CellSize) { LibraryEntities = dark.LibraryEntities, Options = dark.Options };
        mixed.Add(fixture.Lit.Get("hub"));
        mixed.Add(dark.Get("other"));
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomLightHarness.LinkAsync(mixed, RoomPropHarness.Level("hub, other")));
        Assert.Equal(
            "rooms hub and other were lit with different settings (one bake lit its detail props, the other did not);"
            + " a level's rooms are lit alike. Recompile the library with ssmap room.",
            refused.Message);
    }

    /// <summary>
    /// A lit pack of rooms with detail props is the same bytes whether its
    /// rooms were compiled and lit on one thread or on four (the rooms
    /// design, 15.5): vrad commits the detail props' colours and runs in
    /// prop order whatever its degree.
    /// </summary>
    [Fact]
    public async Task ALitPackWithDetailPropsIsTheSameBytesAtAnyThreadCount()
    {
        async Task<byte[]> PackAsync(int degree)
        {
            RoomLibrary rooms = await RoomLightHarness.CompileAsync(LitDetailFixture.Library, degree, LitDetailFixture.Options);
            List<RoomPackItem> items = [];
            foreach (RoomObject room in rooms.Rooms)
            {
                items.Add(await RoomPackItem.CreateAsync(room));
            }

            using MemoryStream pack = new();
            await RoomPack.SaveAsync(items, pack);
            return pack.ToArray();
        }

        Assert.Equal(await PackAsync(1), await PackAsync(4));
    }

    private static DetailPropLightstylesLump[] Run(DetailPropLightstylesLump[] runs, DetailObjectLump p) =>
        p.LightStyleCount == 0 ? [] : runs[(int)p.LightStyles..((int)p.LightStyles + p.LightStyleCount)];

    private static DetailPropLightstylesLump[] StyleLump(BspData bsp, bool hdr)
    {
        string id = hdr ? GameLumpId.DetailPropLightingHdr : GameLumpId.DetailPropLighting;
        return DetailPropLighting.ReadStyleLump(bsp.GameLumps.Single(g => g.IdString() == id));
    }
}
