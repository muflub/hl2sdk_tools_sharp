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
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Tracing;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The skybox parallax's parts (the rooms design, D36): the skybox's sun map
/// (<see cref="RoomSunMap"/>), a sky room's sun layer
/// (<see cref="RoomSunLayer"/>), their sections and the pack, and the link's
/// correction and far-cell warning.
/// </summary>
public sealed class RoomSkyboxParallaxTests(SkyboxLitFixture fixture, ITestOutputHelper output) : IClassFixture<SkyboxLitFixture>
{
    // ---- which rooms carry what -------------------------------------------------------------------

    /// <summary>
    /// The lit skybox carries the sun map and no layer; the sky room a layer
    /// of four turns under the map's sun and no map; the hub, which no sky
    /// reaches, neither; and rooms lit sealed, without the skybox, neither.
    /// </summary>
    [Fact]
    public async Task TheSkyboxHasTheSunMapAndOnlyTheSkyRoomASunLayer()
    {
        RoomLighting sky = fixture.Lit.Get("sky").LightingOfCompile!;
        RoomLighting other = fixture.Lit.Get("other").LightingOfCompile!;
        RoomLighting hub = fixture.Lit.Get("hub").LightingOfCompile!;
        Assert.NotNull(sky.SunMap);
        Assert.Null(sky.SunLayer);
        Assert.NotNull(other.SunLayer);
        Assert.Null(other.SunMap);
        Assert.Equal(4, other.SunLayer!.Turns.Length);
        Assert.True(LevelLinker.SameSun(other.SunLayer.Toward, sky.SunMap!.Toward));
        Assert.Null(hub.SunLayer);
        Assert.Null(hub.SunMap);
        Assert.Empty(hub.ParallaxSections());

        RoomLibrary sealedBake = await fixture.DoorLitSealedAsync();
        Assert.Null(sealedBake.Get("other").LightingOfCompile!.SunLayer);
        Assert.Null(sealedBake.Get("sky").LightingOfCompile!.SunMap);

        RoomSunRange ldr = other.SunLayer.For(0).Ldr!;
        output.WriteLine($"sun layer turn 0: {ldr.Faces.Length} faces, {ldr.Faces.Sum(f => f.Luxels.Length / 3)} luxels, {ldr.Props.Length} props;"
            + $" map {sky.SunMap.Width} x {sky.SunMap.Height}, {sky.SunMap.Toggles.Length} toggles; sections {string.Join(", ", other.ParallaxSections().Concat(sky.ParallaxSections()).Select(s => $"{s.Tag} {s.Bytes.Length} B"))}");
        Assert.NotEmpty(ldr.Faces);
    }

    /// <summary>
    /// The fixture's sun map: a recast start under the overhang, below its
    /// underside, is in its shadow; the same start above the overhang, and
    /// one past the grid, see the sun; the map's direction is the sun's.
    /// </summary>
    [Fact]
    public void TheSunMapSeesTheOverhang()
    {
        RoomSunMap map = fixture.Lit.Get("sky").LightingOfCompile!.SunMap!;
        Assert.Equal(RoomSkyboxHarness.Camera, map.Camera);
        Assert.Equal(16f, map.Scale);
        Assert.True(map.Toward.Z > 0);

        // The overhang: x 16 to 118, y 16 to 240, z 160 to 176; the sun leans
        // towards -x, -y, so a start below its middle is under it.
        Assert.Equal(0f, map.Visibility(new Vec3(80, 128, 140)));
        Assert.Equal(1f, map.Visibility(new Vec3(80, 128, 190)));
        Assert.Equal(1f, map.Visibility(new Vec3(100_000, 128, 140)));
        Assert.Equal(new Vec3(128 + 1, 128 + 2, 128 + 3), map.Recast(new Vec3(16, 32, 48)));
    }

    // ---- the map's arithmetic ---------------------------------------------------------------------

    /// <summary>
    /// A line's visibility from its hits: with none it sees the sun
    /// everywhere; a sky hit alone changes nothing; a solid hit alone shades
    /// below it; a solid then a sky hit turn it on at the solid; a sky then a
    /// solid hit see below the sky, turn off at it and on again past the
    /// solid.
    /// </summary>
    [Fact]
    public void ALineIsAStepFunctionOfHeight()
    {
        Same(true, [], RoomSunMap.StepFunction([]));
        Same(true, [], RoomSunMap.StepFunction([(10f, true)]));
        Same(false, [10f], RoomSunMap.StepFunction([(10f, false)]));
        Same(false, [5f], RoomSunMap.StepFunction([(5f, false), (10f, true)]));
        Same(true, [5f, 10f], RoomSunMap.StepFunction([(5f, true), (10f, false)]));

        static void Same(bool below, float[] toggles, (bool Below, List<float> Toggles) actual)
        {
            Assert.Equal(below, actual.Below);
            Assert.Equal(toggles, actual.Toggles);
        }
    }

    /// <summary>
    /// A made map, straight up: a texel shaded below height 5 reads 0 below
    /// and 1 from 5; one that sees everywhere reads 1; off the grid reads 1;
    /// halfway between the two texels' centres reads the mean; a map whose
    /// heads and toggles disagree is refused.
    /// </summary>
    [Fact]
    public void AMapReadsItsTexelsAndBlendsThem()
    {
        RoomSunMap map = new(new Vec3(0, 0, 1), Vec3.Zero, 16, 0, 10, 20, 2, 1, [2, 1], [5f]);
        Assert.Equal(0f, map.Line(0, 0, 4));
        Assert.Equal(1f, map.Line(0, 0, 5));
        Assert.Equal(1f, map.Line(1, 0, -100));
        Assert.Equal(1f, map.Line(-1, 0, 0));
        Assert.Equal(1f, map.Line(0, 1, 0));
        Assert.Equal(0f, map.Visibility(new Vec3(10.5f, 20.5f, 0)));
        Assert.Equal(0.5f, map.Visibility(new Vec3(11f, 20.5f, 0)));
        Assert.Equal(1f, map.Visibility(new Vec3(10.5f, 20.5f, 6)));
        Assert.Throws<ArgumentException>(() => new RoomSunMap(new Vec3(0, 0, 1), Vec3.Zero, 16, 0, 0, 0, 2, 1, [2, 1], []));
    }

    /// <summary>
    /// The grid: empty for no casters; the casters' projection along the
    /// sun, a texel of margin round it, and a unit below and above them;
    /// none past <see cref="RoomSunMap.MaxTexels"/>.
    /// </summary>
    [Fact]
    public void TheGridCoversTheCastersProjection()
    {
        Assert.Equal((0, 0, 0, 0, 0f, 0f), RoomSunMap.Grid([], new Vec3(0, 0, 1), 0));
        TracedTriangle t = new(0, new Vec3(0, 0, 10), new Vec3(4, 0, 10), new Vec3(0, 2, 20), 0);
        Assert.Equal((-1, -1, 6, 4, 9f, 21f), RoomSunMap.Grid([t], new Vec3(0, 0, 1), 0));
        TracedTriangle wide = new(0, new Vec3(0, 0, 0), new Vec3(4000, 0, 0), new Vec3(0, 4000, 0), 0);
        Assert.Null(RoomSunMap.Grid([wide], new Vec3(0, 0, 1), 0));
    }

    /// <summary>
    /// No map is made without a sun, for a room without a camera, or for a
    /// sun so low over the skybox that its grid would pass the cap; a sun
    /// map's build is the same bytes at one thread and four.
    /// </summary>
    [Fact]
    public async Task NoMapIsMadeWithoutASunACameraOrRoom()
    {
        VbspContext context = await RoomLightHarness.ContextAsync("sky");
        RoomObject sky = fixture.Lit.Get("sky");
        DWorldLight[] sun = fixture.Lit.Get("sky").LightingOfCompile!.SkyLdr!;
        CompileParallelism one = new() { MaxDegree = 1 }, four = new() { MaxDegree = 4 };
        Assert.Null(await RoomSunMap.BuildAsync(sky, null, SkyboxLitFixture.Options, context.Content!, one, CancellationToken.None));
        Assert.Null(await RoomSunMap.BuildAsync(fixture.Lit.Get("other"), sun, SkyboxLitFixture.Options, context.Content!, one, CancellationToken.None));
        DWorldLight low = new() { Type = (int)EmitType.SkyLight, Normal = new Vec3(-0.99996f, 0, -0.00873f) };
        Assert.Null(await RoomSunMap.BuildAsync(sky, [low], SkyboxLitFixture.Options, context.Content!, one, CancellationToken.None));

        RoomSunMap a = (await RoomSunMap.BuildAsync(sky, sun, SkyboxLitFixture.Options, context.Content!, one, CancellationToken.None))!;
        RoomSunMap b = (await RoomSunMap.BuildAsync(sky, sun, SkyboxLitFixture.Options, context.Content!, four, CancellationToken.None))!;
        Assert.Equal(a.ToSection().Bytes.ToArray(), b.ToSection().Bytes.ToArray());
        Assert.Equal(sky.LightingOfCompile!.SunMap!.ToSection().Bytes.ToArray(), a.ToSection().Bytes.ToArray());
    }

    /// <summary>The direction towards the sun: none without a sky light or below the horizon; else the sky light's normal reversed.</summary>
    [Fact]
    public void TheSunsDirectionIsItsSkyLightsReversed()
    {
        Assert.Null(RoomSunMap.TowardSun(null));
        Assert.Null(RoomSunMap.TowardSun([new DWorldLight { Type = (int)EmitType.Point, Normal = new Vec3(0, 0, -1) }]));
        Assert.Null(RoomSunMap.TowardSun([new DWorldLight { Type = (int)EmitType.SkyLight, Normal = new Vec3(0, 0, 1) }]));
        Assert.Equal(new Vec3(0.6f, 0, 0.8f), RoomSunMap.TowardSun([new DWorldLight { Type = (int)EmitType.SkyLight, Normal = new Vec3(-0.6f, 0, -0.8f) }]));
    }

    // ---- the sections -----------------------------------------------------------------------------

    /// <summary>
    /// The map's section reads back to the same bytes; an absent section or
    /// another revision is no map; a damaged one is refused.
    /// </summary>
    [Fact]
    public void TheSunMapSectionRoundTrips()
    {
        RoomSunMap map = fixture.Lit.Get("sky").LightingOfCompile!.SunMap!;
        byte[] bytes = map.ToSection().Bytes.ToArray();
        Assert.Equal(bytes, RoomSunMap.Read(bytes, "sky")!.ToSection().Bytes.ToArray());
        Assert.Null(RoomSunMap.Read(null, "sky"));
        Assert.Null(RoomSunMap.Read(Payload(99, _ => { }), "sky"));

        Assert.Contains("texels", Refused(w => Header(w, new Vec3(0, 0, 1), 16, -1, 1)), StringComparison.Ordinal);
        Assert.Contains("out of range", Refused(w => Header(w, new Vec3(0, 0, -1), 16, 1, 1)), StringComparison.Ordinal);
        Assert.Contains("out of range", Refused(w => Header(w, new Vec3(0, 0, 1), 0, 1, 1)), StringComparison.Ordinal);
        Assert.Contains("toggles where", Refused(w =>
        {
            Header(w, new Vec3(0, 0, 1), 16, 1, 1);
            w.Raw([2]);
            w.Structs<float>([]);
        }), StringComparison.Ordinal);

        static string Refused(Action<RoomLinkSections.Writer> write) =>
            Assert.Throws<LinkException>(() => RoomSunMap.Read(Payload(RoomLinkSections.Revision, write), "sky")).Message;

        static void Header(RoomLinkSections.Writer w, Vec3 toward, float scale, int width, int height)
        {
            w.Structs<Vec3>([toward, Vec3.Zero], counted: false);
            w.Structs<float>([scale, 0f], counted: false);
            w.Int(0);
            w.Int(0);
            w.Int(width);
            w.Int(height);
        }
    }

    /// <summary>
    /// The layer's section reads back to the same bytes; an absent section
    /// or another revision is no layer; one for a room without lighting, of
    /// another turn count, with a range the lighting lacks, or a face past
    /// the lighting, is refused.
    /// </summary>
    [Fact]
    public void TheSunLayerSectionRoundTrips()
    {
        RoomLighting lighting = fixture.Lit.Get("other").LightingOfCompile!;
        RoomLighting hub = fixture.Lit.Get("hub").LightingOfCompile!;
        byte[] bytes = lighting.SunLayer!.ToSection().Bytes.ToArray();
        Assert.Equal(bytes, RoomSunLayer.Read(bytes, "other", lighting)!.ToSection().Bytes.ToArray());
        Assert.Null(RoomSunLayer.Read(null, "other", lighting));
        Assert.Null(RoomSunLayer.Read(Payload(99, _ => { }), "other", lighting));

        Assert.Contains("without lighting", Assert.Throws<LinkException>(() => RoomSunLayer.Read(bytes, "other", null)).Message, StringComparison.Ordinal);
        Assert.Contains("4 turns", Assert.Throws<LinkException>(() => RoomSunLayer.Read(bytes, "hub", hub)).Message, StringComparison.Ordinal);
        Assert.Contains("does not have", Refused(w =>
        {
            w.Structs<Vec3>([new Vec3(0, 0, 1)], counted: false);
            w.Int(4);
            w.Byte(2);
        }), StringComparison.Ordinal);
        Assert.Contains("outside the room's lighting", Refused(w =>
        {
            w.Structs<Vec3>([new Vec3(0, 0, 1)], counted: false);
            w.Int(4);
            w.Byte(1);
            w.Int(1);
            w.Int(lighting.FaceCount);
            w.Int(0);
            w.Int(0);
        }), StringComparison.Ordinal);

        string Refused(Action<RoomLinkSections.Writer> write) =>
            Assert.Throws<LinkException>(() => RoomSunLayer.Read(Payload(RoomLinkSections.Revision, write), "other", lighting)).Message;
    }

    /// <summary>
    /// The pack carries both sections with the lighting they belong to: the
    /// sky room's layer and the skybox's map read back to the same bytes, and
    /// a level linked from the read-back rooms is the same map.
    /// </summary>
    [Fact]
    public async Task ThePackCarriesTheParallax()
    {
        RoomPackItem other = await RoomPackItem.CreateAsync(fixture.Lit.Get("other"));
        RoomPackItem sky = await RoomPackItem.CreateAsync(fixture.Lit.Get("sky"));
        Assert.Contains(other.Extra, s => s.Tag == RoomSunLayer.SectionTag);
        Assert.Contains(sky.Extra, s => s.Tag == RoomSunMap.SectionTag);

        using MemoryStream pack = new();
        await RoomPack.SaveAsync([other, sky], pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(pack, index, ["other", "sky"]);
        Assert.Equal(
            fixture.Lit.Get("other").LightingOfCompile!.SunLayer!.ToSection().Bytes.ToArray(),
            loaded[0].LightingOfCompile!.SunLayer!.ToSection().Bytes.ToArray());
        Assert.Equal(
            fixture.Lit.Get("sky").LightingOfCompile!.SunMap!.ToSection().Bytes.ToArray(),
            loaded[1].LightingOfCompile!.SunMap!.ToSection().Bytes.ToArray());

        RoomLibrary read = new(fixture.Lit.Kit, fixture.Lit.CellSize) { LibraryEntities = fixture.Lit.LibraryEntities, Options = fixture.Lit.Options, SkyboxRoom = "sky" };
        read.Add(loaded[0]);
        read.Add(loaded[1]);
        LevelGrid level = RoomPropHarness.Level(SkyboxLitFixture.Rows(("other@90", 2, 3)));
        Assert.Equal(
            (await RoomLightHarness.LinkAsync(fixture.Lit, level)).Bsp[BspLump.Lighting].Data.ToArray(),
            (await RoomLightHarness.LinkAsync(read, level)).Bsp[BspLump.Lighting].Data.ToArray());
    }

    // ---- the bake's pieces ------------------------------------------------------------------------

    /// <summary>The sun the layer is baked with: its ambient zeroed, LDR and HDR, and added when it had none; every other key kept.</summary>
    [Fact]
    public void TheLayersSunHasNoAmbient()
    {
        VmfChunk sun = new("entity");
        sun.AddKey("classname", "light_environment");
        sun.AddKey("_ambient", "1 2 3 4");
        sun.AddKey("_AmbientHDR", "5 6 7 8");
        VmfChunk dark = RoomSunLayer.Darkened(sun);
        Assert.Equal("light_environment", dark.GetValue("classname"));
        Assert.Equal("0 0 0 0", dark.GetValue("_ambient"));
        Assert.Equal("0 0 0 0", dark.GetValue("_AmbientHDR"));

        VmfChunk bare = new("entity");
        bare.AddKey("classname", "light_environment");
        Assert.Equal("0 0 0 0", RoomSunLayer.Darkened(bare).GetValue("_ambient"));
    }

    /// <summary>The style slot holding style 0: each of the four, or none.</summary>
    [Fact]
    public void TheStyleSlotIsTheOneHoldingStyleZero()
    {
        Assert.Equal(0, RoomSunLayer.StyleSlot(0, 255, 255, 255));
        Assert.Equal(1, RoomSunLayer.StyleSlot(32, 0, 255, 255));
        Assert.Equal(2, RoomSunLayer.StyleSlot(32, 33, 0, 255));
        Assert.Equal(3, RoomSunLayer.StyleSlot(32, 33, 34, 0));
        Assert.Equal(-1, RoomSunLayer.StyleSlot(32, 33, 34, 255));
    }

    // ---- the link ---------------------------------------------------------------------------------

    /// <summary>
    /// A placement's payload: none at its bakes' cell, whatever its turn;
    /// its own, differing from the stored turn, one cell east; none under a
    /// map that sees the sun everywhere (no luxel moves); two suns are the
    /// same only bit for bit.
    /// </summary>
    [Fact]
    public void APlacementTakesItsOwnPayloadOnlyAwayFromItsBakesCell()
    {
        RoomObject other = fixture.Lit.Get("other");
        RoomLighting lighting = other.LightingOfCompile!;
        RoomSunLayer layer = lighting.SunLayer!;
        RoomSunMap map = fixture.Lit.Get("sky").LightingOfCompile!.SunMap!;
        DoorFaceCells?[] cells = RoomDoorLight.FaceCellsOf(other.Bsp);
        const float cell = RoomHarness.Cell;
        for (int turn = 0; turn < 4; turn++)
        {
            Assert.Null(LevelLinker.ParallaxPayload(cell, lighting, layer, map, new RoomPlacement("other", 0, 0, turn), cells));
        }

        RoomLightingPayload moved = LevelLinker.ParallaxPayload(cell, lighting, layer, map, new RoomPlacement("other", 1, 0, 0), cells)!;
        Assert.NotEqual(lighting.For(0).Ldr!.Luxels, moved.Ldr!.Luxels);
        Assert.Equal(lighting.For(0).Ldr!.AmbientCubes, moved.Ldr.AmbientCubes);

        RoomSunMap open = new(map.Toward, map.Camera, map.Scale, map.PlaneZ, 0, 0, 0, 0, [], []);
        Assert.Null(LevelLinker.ParallaxPayload(cell, lighting, layer, open, new RoomPlacement("other", 1, 0, 0), cells));

        Assert.True(LevelLinker.SameSun(new Vec3(0, 0, 1), new Vec3(0, 0, 1)));
        Assert.False(LevelLinker.SameSun(new Vec3(-0f, 0, 1), new Vec3(0, 0, 1)));
        Assert.False(LevelLinker.SameSun(new Vec3(0, 1e-7f, 1), new Vec3(0, 0, 1)));
    }

    /// <summary>
    /// A prop's vertices move with its origin's change, as a luxel moves
    /// with its own: under a layer whose one prop the sun reaches, a map
    /// shading that prop's recast start at the bakes' cell and not one cell
    /// east adds the prop's sun to its stored colours; a prop the stored
    /// turn does not light, or of another vertex count, is left alone.
    /// </summary>
    [Fact]
    public void APropsSunMovesWithItsOrigin()
    {
        RoomObject other = fixture.Lit.Get("other");
        RoomLighting lighting = other.LightingOfCompile!;
        RoomLightRange stored = lighting.For(0).Ldr!;
        RoomPropColors lit = new(0, 1, [0], [2], [(Half)1, (Half)1, (Half)1, (Half)2, (Half)2, (Half)2]);
        RoomPropColors other1 = new(1, 1, [0], [1], [(Half)1, (Half)1, (Half)1]);
        RoomLightRange withProp = stored with { Props = [lit, other1] };
        RoomLighting propLit = new(
            lighting.FaceCount, lighting.LeafCount, lighting.VertNormals, lighting.VertNormalIndices, true, lighting.SkyLeaves, lighting.MapFlags,
            [new RoomLightingPayload(withProp, null)], null, null, other.Bsp);
        Half one = (Half)1;
        RoomSunLayer layer = new(new Vec3(0, 0, 1), [new RoomSunTurn(new RoomSunRange([], [
            new RoomSunProp(0, new Vec3(8.5f, 8.5f, 8), [one, one, one, one, one, one]),
            new RoomSunProp(1, new Vec3(8.5f, 8.5f, 8), [one, one, one, one, one, one]),
            new RoomSunProp(5, new Vec3(8.5f, 8.5f, 8), [one, one, one]),
        ]), null)]);

        // Straight up, a texel whose centre is the bakes' cell's recast of the
        // prop's origin (camera 0, scale 1: the recast is the world point),
        // shaded everywhere below 1000; one cell east (x 264.5) is off the grid.
        RoomSunMap map = new(new Vec3(0, 0, 1), Vec3.Zero, 1, 0, 8, 8, 1, 1, [2], [1000f]);
        RoomLightingPayload moved = LevelLinker.ParallaxPayload(RoomHarness.Cell, propLit, layer, map, new RoomPlacement("other", 1, 0, 0), [])!;
        Assert.Equal([(Half)2, (Half)2, (Half)2, (Half)3, (Half)3, (Half)3], moved.Ldr!.Props[0].Colors);
        Assert.Same(other1.Colors, moved.Ldr.Props[1].Colors);
        Assert.Same(stored.Luxels, moved.Ldr.Luxels);
    }

    /// <summary>
    /// The far-cell warning: a sky room whose cell recasts from outside the
    /// skybox (the fixture's 256-unit skybox at scale 16, its camera in the
    /// middle, holds cells 0 to 7) links, lit or unlit, with one warning
    /// naming every such cell; a level within it has none; the hub, which
    /// has no sky, never counts.
    /// </summary>
    [Fact]
    public async Task ALevelWhoseSkyRoomsRecastFromOutsideTheSkyboxIsWarnedOnce()
    {
        LinkedLevel far = await RoomLightHarness.LinkAsync(fixture.Lit, RoomPropHarness.Level(SkyboxLitFixture.Rows(("hub@0", 7, 0), ("other@0", 8, 0), ("other@90", 9, 0))));
        string warning = Assert.Single(far.LightingWarnings);
        output.WriteLine(warning);
        Assert.StartsWith("the sky rooms at cells (8, 0), (9, 0) recast their sky from outside the 3D skybox \"sky\"", warning, StringComparison.Ordinal);

        LinkedLevel unlit = await RoomLightHarness.LinkAsync(fixture.Unlit, RoomPropHarness.Level(SkyboxLitFixture.Rows(("other@0", 8, 0))));
        Assert.StartsWith("the sky rooms at cell (8, 0) recast", Assert.Single(unlit.LightingWarnings), StringComparison.Ordinal);

        LinkedLevel within = await RoomLightHarness.LinkAsync(fixture.Lit, RoomPropHarness.Level(SkyboxLitFixture.Rows(("other@0", 7, 7), ("hub@0", 8, 7))));
        Assert.Empty(within.LightingWarnings);
    }

    /// <summary>
    /// A level of sky rooms away from their bakes' cell links to the same
    /// bytes at one thread and at four: the correction reads the map, which
    /// no worker order changes.
    /// </summary>
    [Fact]
    public async Task TheCorrectedLinkIsTheSameAtAnyThreadCount()
    {
        LevelGrid level = RoomPropHarness.Level(SkyboxLitFixture.Rows(("other@90", 2, 3), ("other@180", 3, 3), ("hub@0", 1, 3)));
        BspData one = (await RoomLightHarness.LinkAsync(fixture.Lit, level, degree: 1)).Bsp;
        BspData four = (await RoomLightHarness.LinkAsync(fixture.Lit, level, degree: 4)).Bsp;
        for (int lump = 0; lump < BspData.HeaderLumps; lump++)
        {
            Assert.True(one[lump].Data.Span.SequenceEqual(four[lump].Data.Span), $"lump {lump}");
        }
    }

    /// <summary>A section of the link sections' framing, codec none, whose payload is the revision and what <paramref name="write"/> adds.</summary>
    private static byte[] Payload(int revision, Action<RoomLinkSections.Writer> write)
    {
        RoomLinkSections.Writer w = new();
        w.Int(revision);
        write(w);
        return RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None);
    }
}
