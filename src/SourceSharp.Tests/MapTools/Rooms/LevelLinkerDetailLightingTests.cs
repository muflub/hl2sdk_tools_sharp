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
    /// In a level of both rooms (jointed, lit without their door light, so
    /// the bake alone), each placement's detail props carry their room's
    /// bake as the room alone links it: the same colours, counts and runs,
    /// the other room's lamp renumbered from its compile's style 32 to the
    /// level's 33 (its name comes second in the linked entity lump, as vbsp
    /// numbers the flattened map's), the hub's keeping 32.
    /// </summary>
    [Fact]
    public async Task EveryPlacementKeepsItsRoomsDetailLightingWithTheLevelsStyles()
    {
        BspData level = await fixture.BaseLinkedAsync("hub, other");
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
            BspData alone = await fixture.BaseLinkedAsync(room);
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
    /// Two rooms jointed, at two turns, against vrad of the linked level
    /// itself (the rooms design, 9.8, as PR 10 holds a jointed level's
    /// luxels): each detail prop's colour, near a joint (within a door width)
    /// and elsewhere, by <see cref="LitCompare.Relative"/>. With the door
    /// light (the neighbour's lights evaluated at each prop's one sample
    /// point through the cells of the opening it sees, as a face's receivers
    /// take them), the props are within a twentieth (p95, near and
    /// elsewhere) and 1% of the energy, the door light only adds to each
    /// prop's base colour and runs, 95% of the props list the styles vrad
    /// lists for them, and the base alone is far off; a capped room is exact
    /// with its door light (the capped facts above run on rooms lit with it).
    /// Measured: near p95 0.006 and 0.006, elsewhere 0.005 and 0.006, energy
    /// 1.000 and 1.002, 443 and 438 of 453 props with vrad's styles; the base
    /// alone near p95 1.0, energy 0.76 and 0.70.
    /// </summary>
    [Theory]
    [InlineData("hub, other")]
    [InlineData("hub@90, other@180")]
    public async Task AJointedLevelsDetailPropsTakeTheDoorsLight(string row)
    {
        // The rooms' surfaces reflect nothing and they hold no static prop,
        // so their responses are there for their detail props alone.
        RoomDoorLight hubDoor = fixture.Lit.Get("hub").DoorLightOfCompile!;
        Assert.All(hubDoor.Ldr!.Responses, socket => Assert.Equal(DoorLightMath.EmitterCount, socket.Length));
        Assert.Equal(RoomDetailHarness.Lump(fixture.Lit.Get("hub").Bsp).Props.Count, hubDoor.Details!.Centres.Length);

        LevelGrid level = RoomPropHarness.Level(row);
        LinkedLevel shape = await RoomLightHarness.LinkAsync(fixture.Lit, level);
        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(shape);
        BspData relit = await fixture.RelitAsync(row);
        BspData linked = await fixture.LinkedAsync(row);
        BspData baseOnly = await fixture.BaseLinkedAsync(row);
        (double nearP95, double farP95, double energy) door = Measure(linked, relit, joints);
        (double nearP95, double farP95, double energy) bare = Measure(baseOnly, relit, joints);
        output.WriteLine($"{row}: with the door light near p95 {door.nearP95:F3} elsewhere p95 {door.farP95:F3} energy {door.energy:F3};"
            + $" base alone near p95 {bare.nearP95:F3} elsewhere p95 {bare.farP95:F3} energy {bare.energy:F3}");
        Assert.True(door.nearP95 < 0.05 && door.farP95 < 0.05, $"p95 {door.nearP95} near, {door.farP95} elsewhere");
        Assert.InRange(door.energy, 0.99, 1.01);
        Assert.True(bare.nearP95 >= 2 * door.nearP95 && bare.energy < door.energy, "the door light brings the props nearer vrad's");

        // The door light only adds: every prop's colour, and every style its
        // base run holds, at least the base's; the styles it adds are the
        // neighbour's lamps', renumbered, and each prop's run lists the
        // styles vrad of the level lists for it.
        DetailPropLump withDoor = RoomDetailHarness.Lump(linked), without = RoomDetailHarness.Lump(baseOnly), reference = RoomDetailHarness.Lump(relit);
        DetailPropLightstylesLump[] doorRuns = StyleLump(linked, hdr: true), baseRuns = StyleLump(baseOnly, hdr: true), relitRuns = StyleLump(relit, hdr: true);
        Dictionary<string, DetailObjectLump> relitByOrigin = reference.Props.ToDictionary(p => RoomDetailHarness.V(p.Origin), StringComparer.Ordinal);
        int sameStyles = 0;
        Assert.All(withDoor.Props.Zip(without.Props), pair =>
        {
            Vec3 a = pair.First.Lighting.ToLinear(), b = pair.Second.Lighting.ToLinear();
            Assert.True(a.X >= b.X && a.Y >= b.Y && a.Z >= b.Z, $"{a} darker than the base {b}");
            DetailPropLightstylesLump[] run = Run(doorRuns, pair.First);
            foreach (DetailPropLightstylesLump entry in Run(baseRuns, pair.Second))
            {
                DetailPropLightstylesLump added = Assert.Single(run, e => e.Style == entry.Style);
                Vec3 x = added.Lighting.ToLinear(), y = entry.Lighting.ToLinear();
                Assert.True(x.X >= y.X && x.Y >= y.Y && x.Z >= y.Z, $"style {entry.Style}: {x} darker than the base {y}");
            }

            Assert.True(run.Zip(run.Skip(1)).All(e => e.First.Style < e.Second.Style), "a run lists its styles in ascending order");
            sameStyles += run.Select(e => e.Style).SequenceEqual(Run(relitRuns, relitByOrigin[RoomDetailHarness.V(pair.First.Origin)]).Select(e => e.Style)) ? 1 : 0;
        });
        output.WriteLine($"{row}: {sameStyles} of {withDoor.Props.Count} props list vrad's styles");
        Assert.True(sameStyles >= withDoor.Props.Count * 0.95, $"{sameStyles} of {withDoor.Props.Count} props list vrad's styles");
    }

    /// <summary>
    /// Reflecting rooms (the shell's reflectivity 0.6/0.55/0.5, as PR 10's
    /// bounce facts use): a room with detail props stores responses, each
    /// emitter's detail props' ambient light (what its surfaces reflect onto
    /// them; the direct light is the link's), and a jointed level's props
    /// are within PR 10's tolerances for reflecting rooms (p95 under 0.2),
    /// here held to 0.15 near and elsewhere and 2% of the energy. Measured
    /// against vrad of the linked level: near p95 0.074, elsewhere 0.040,
    /// energy 1.000; with the rooms' detail responses taken out (the direct
    /// light alone), p95 0.14 to 0.20 and energy 0.978.
    /// </summary>
    [Fact]
    public async Task ReflectingRoomsDetailPropsTakeTheBouncedDoorLight()
    {
        const string Reflectivity = ".6 .55 .5";
        RoomLibrary rooms = await RoomLightHarness.CompileAsync(LitDetailFixture.Library, options: LitDetailFixture.Options, doorLight: true, reflectivity: Reflectivity);
        DoorResponseEmitter[][] responses = rooms.Get("hub").DoorLightOfCompile!.Ldr!.Responses;
        Assert.All(responses, socket => Assert.Equal(DoorLightMath.EmitterCount, socket.Length));
        Assert.Contains(responses.SelectMany(s => s), e => e.Details.Length > 0);
        Assert.NotNull(rooms.Get("hub").DoorLightOfCompile!.Details);

        LevelGrid level = RoomPropHarness.Level("hub, other@90");
        LinkedLevel linked = await RoomLightHarness.LinkAsync(rooms, level);
        // vrad of the linked level itself: its texdata carry the shell's
        // reflectivity, which the rooms' compiles read from the material.
        BspData relit = await RoomLightHarness.RelightAsync(linked.Bsp, LitDetailFixture.Options, Reflectivity);
        (double nearP95, double farP95, double energy) = Measure(linked.Bsp, relit, DoorLightCompare.Joints(linked));
        output.WriteLine($"reflecting: near p95 {nearP95:F3} elsewhere p95 {farP95:F3} energy {energy:F3}");
        Assert.True(nearP95 < 0.15 && farP95 < 0.15, $"p95 {nearP95} near, {farP95} elsewhere");
        Assert.InRange(energy, 0.98, 1.02);
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
    /// section, no detail receivers, and no responses a room of surfaces that
    /// reflect nothing would store only for them), and their level keeps each prop's lighting as vbsp wrote it
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
        Assert.All(dark.Rooms, r => Assert.Null(r.DoorLightOfCompile!.Details));
        Assert.All(dark.Get("hub").DoorLightOfCompile!.Ldr!.Responses, socket => Assert.Empty(socket));
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

    /// <summary>Each detail prop's colour against the reference's at the same origin: p95 near a joint and elsewhere, and the energy.</summary>
    private static (double NearP95, double FarP95, double Energy) Measure(BspData level, BspData reference, List<DoorLightCompare.Joint> joints)
    {
        DetailPropLump a = RoomDetailHarness.Lump(level), b = RoomDetailHarness.Lump(reference);
        Dictionary<string, DetailObjectLump> byOrigin = b.Props.ToDictionary(p => RoomDetailHarness.V(p.Origin), StringComparer.Ordinal);
        List<double> near = [], far = [];
        double sumA = 0, sumB = 0;
        foreach (DetailObjectLump p in a.Props)
        {
            Vec3 x = p.Lighting.ToLinear(), y = byOrigin[RoomDetailHarness.V(p.Origin)].Lighting.ToLinear();
            (joints.Any(j => (p.Origin - j.Centre).Length() <= j.Width + (j.Height / 2)) ? near : far).Add(LitCompare.Relative(x, y));
            sumA += x.X + x.Y + x.Z;
            sumB += y.X + y.Y + y.Z;
        }

        near.Sort();
        far.Sort();
        return (LitCompare.Quantile(near, .95), LitCompare.Quantile(far, .95), sumA / sumB);
    }

    private static DetailPropLightstylesLump[] Run(DetailPropLightstylesLump[] runs, DetailObjectLump p) =>
        p.LightStyleCount == 0 ? [] : runs[(int)p.LightStyles..((int)p.LightStyles + p.LightStyleCount)];

    private static DetailPropLightstylesLump[] StyleLump(BspData bsp, bool hdr)
    {
        string id = hdr ? GameLumpId.DetailPropLightingHdr : GameLumpId.DetailPropLighting;
        return DetailPropLighting.ReadStyleLump(bsp.GameLumps.Single(g => g.IdString() == id));
    }
}
