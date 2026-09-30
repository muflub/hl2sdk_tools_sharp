//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The skybox under a sky room's bakes (<see cref="RoomSkybox"/>): where it
/// is placed at each turn, which rooms it lights, what it adds to the
/// lighting's description and keys, the library compile's order, the door
/// light's recast, and the refusals.
/// </summary>
public sealed class RoomSkyboxBakeTests(SkyboxLitFixture fixture) : IClassFixture<SkyboxLitFixture>
{
    /// <summary>The four quarter turns.</summary>
    public static TheoryData<int> Turns => new() { 0, 1, 2, 3 };

    /// <summary>
    /// At every turn the skybox and its camera stand in the room's frame
    /// where a level of the room alone at cell (0, 0) puts them, moved by
    /// the inverse of the room's placement, and a sample at <c>q</c> in the
    /// room recasts to the move of where the level's sample at
    /// <c>R q + T</c> recasts: <c>camera' + q / scale</c> is
    /// <c>M(camera + (R q + T) / scale)</c>, exactly, for whole-unit points.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public void TheSkyboxStandsWhereTheLevelPutsItAndTheRecastLandsThere(int turn)
    {
        RoomSkybox skybox = RoomSkybox.Of(fixture.Lit.Get("sky"));
        RoomObject other = fixture.Lit.Get("other");
        const float cell = RoomHarness.Cell;
        RoomTransform room = new(new RoomPlacement("other", 0, 0, turn), cell);
        RoomTransform sky = new(new RoomPlacement("sky", 0, 0, 0) { Level = -1 }, cell);

        (Vec3 origin, float scale) = Assert.Single(skybox.LocalCameras);
        Assert.Equal(RoomSkyboxHarness.Camera, origin);
        Assert.Equal(16f, scale);

        // M: the level's point back into the room's frame.
        Vec3 M(Vec3 world) => RoomTransform.Rotate(world - room.Apply(Vec3.Zero), (4 - turn) % 4);

        foreach (Vec3 s in (Vec3[])[new(40, 40, 16), new(88, 200, 72), Vec3.Zero])
        {
            Assert.Equal(M(sky.Apply(s)), skybox.Move(s, turn, cell));
        }

        SkyCamera camera = Assert.Single(skybox.Cameras(turn, cell));
        Assert.Equal(-1, camera.Area);
        Vec3 cameraWorld = sky.Apply(origin);
        foreach (Vec3 q in (Vec3[])[new(16, 16, 16), new(100, 60, 120), new(240, 128, 200)])
        {
            Vec3 level = cameraWorld + (room.Apply(q) * camera.WorldToSky);
            Assert.Equal(M(level), camera.Origin + (q * camera.WorldToSky));
        }

        VradSkybox forVrad = skybox.For(other, turn);
        Assert.Same(skybox.Room.Bsp, forVrad.Map);
        Assert.Equal(skybox.Cameras(turn, cell), forVrad.Cameras);
        Assert.Equal(skybox.Move(new Vec3(1, 2, 3), turn, cell), forVrad.Move(new Vec3(1, 2, 3)));
        Assert.Equal(0, forVrad.PropIndexBase);
    }

    /// <summary>
    /// The skybox lights a room's bake only when the room has a sky face
    /// under a library sun, and never the skybox's own.
    /// </summary>
    [Fact]
    public void TheSkyboxLightsOnlySkyRoomsUnderASun()
    {
        RoomSkybox skybox = RoomSkybox.Of(fixture.Lit.Get("sky"));
        RoomLightingSettings sunlit = new(RoomLightHarness.Options) { Sun = RoomLightHarness.Sun() };
        Assert.True(skybox.Lights(fixture.Lit.Get("other"), sunlit));
        Assert.False(skybox.Lights(fixture.Lit.Get("hub"), sunlit));
        Assert.False(skybox.Lights(fixture.Lit.Get("sky"), sunlit));
        Assert.False(skybox.Lights(fixture.Lit.Get("other"), new RoomLightingSettings(RoomLightHarness.Options)));
    }

    /// <summary>
    /// The door light's own sky rays: a recast with no skybox casters stops
    /// nothing; the fixture's overhang stops a ray recast from under it
    /// toward the sun and lets one from the room's far side through; and the
    /// sky room's capture at a turn is not what it is without the recast.
    /// </summary>
    [Fact]
    public async Task TheDoorLightsSkyRaysAreRecastIntoTheSkybox()
    {
        Assert.False(new SkyboxRecast(null, [new SkyCamera(Vec3.Zero, 16, 1f / 16, -1)]).Blocks(Vec3.Zero, new Vec3(0, 0, 1)));

        RoomObject other = fixture.Lit.Get("other");
        RoomSkybox skybox = RoomSkybox.Of(fixture.Lit.Get("sky"));
        VbspContext context = await RoomLightHarness.ContextAsync("other");
        SkyboxRecast recast = await skybox.RecastAsync(other, 0, SkyboxLitFixture.Options, context.Content, CancellationToken.None);
        Assert.NotNull(recast.Tracer);

        // A recast starts at x 128 to 144 of the skybox, east of the
        // overhang (x 16 to 118, some 24 to 40 units above the start): straight
        // up it meets the sky; leaning west it rises under the overhang and
        // meets it; leaning east it meets the skybox's sky walls.
        Vec3 up = new(0, 0, 1);
        Assert.False(recast.Blocks(new Vec3(128, 128, 120), up));
        Assert.True(recast.Blocks(new Vec3(16, 128, 120), new Vec3(-0.9578f, 0, 0.2873f)));
        Assert.False(recast.Blocks(new Vec3(16, 128, 120), new Vec3(0.9578f, 0, 0.2873f)));
    }

    /// <summary>
    /// The lighting's description names the skybox by its content: two
    /// skyboxes that differ give two descriptions, so two pack ids and two
    /// cache keys for every room; a library without one describes its
    /// lighting as before; <see cref="RoomLightingSettings.WithSkybox"/>
    /// keeps every other member.
    /// </summary>
    [Fact]
    public void TheSkyboxIsInTheDescriptionAndTheCacheKey()
    {
        LibraryRoom a = RoomLibraryVmf.SplitLibrary(SkyboxLitFixture.Library).Skybox!;
        LibraryRoom b = RoomLibraryVmf.SplitLibrary(SkyboxLitFixture.Make(new Box(new Vec3(16, 16, 160), new Vec3(150, 240, 176)))).Skybox!;
        LibraryRoom a2 = RoomLibraryVmf.SplitLibrary(SkyboxLitFixture.Make(SkyboxLitFixture.Overhang)).Skybox!;
        RoomLightingSettings none = new(VradOptions.Default) { Sun = RoomLightHarness.Sun() };
        RoomLightingSettings withA = new(VradOptions.Default) { Sun = RoomLightHarness.Sun(), Skybox = a };
        RoomLightingSettings withB = new(VradOptions.Default) { Sun = RoomLightHarness.Sun(), Skybox = b };

        Assert.DoesNotContain("skybox", none.Describe(), StringComparison.Ordinal);
        Assert.Equal(none.Describe() + "|skybox:" + RoomCacheKey.RoomDigest(a), withA.Describe());
        Assert.NotEqual(withA.Describe(), withB.Describe());
        Assert.Equal(withA.Describe(), new RoomLightingSettings(VradOptions.Default) { Sun = RoomLightHarness.Sun(), Skybox = a2 }.Describe());

        RoomCacheInputs inputs = new(VbspOptions.Default);
        string keyNone = RoomCacheKey.OptionsDigestOf(inputs with { Lighting = none });
        string keyA = RoomCacheKey.OptionsDigestOf(inputs with { Lighting = withA });
        string keyB = RoomCacheKey.OptionsDigestOf(inputs with { Lighting = withB });
        Assert.NotEqual(keyNone, keyA);
        Assert.NotEqual(keyA, keyB);

        RoomLightingSettings compiled = withA.WithSkybox(RoomSkybox.Of(fixture.Lit.Get("sky")));
        Assert.Equal(withA.Describe(), compiled.Describe());
        Assert.Same(withA.Options, compiled.Options);
        Assert.Same(withA.Sun, compiled.Sun);
        Assert.Same(a, compiled.Skybox);
        Assert.NotNull(compiled.SkyboxScene);
        Assert.Null(withA.SkyboxScene);
    }

    /// <summary>
    /// A sky room lit under a library skybox whose compile it was not given
    /// is refused, naming both, rather than lit as if the level had none; the
    /// hub, which no sky reaches, and the skybox itself are lit as always.
    /// </summary>
    [Fact]
    public async Task ASkyRoomWithoutItsSkyboxsCompileIsRefused()
    {
        LibraryRoom skybox = RoomLibraryVmf.SplitLibrary(SkyboxLitFixture.Library).Skybox!;
        RoomLightingSettings settings = new(SkyboxLitFixture.Options) { Sun = RoomLightHarness.Sun(), Skybox = skybox };
        VbspContext context = await RoomLightHarness.ContextAsync("other");
        MapCompileException refused = await Assert.ThrowsAsync<MapCompileException>(() =>
            RoomLighting.BakeAsync(fixture.Unlit.Get("other"), settings, context.Content, new CompileParallelism { MaxDegree = 1 }, CancellationToken.None));
        Assert.Equal(
            "room other is lit under the library's 3D skybox \"sky\", which did not compile; its sky rays are recast into the skybox, so the skybox must compile first.",
            refused.Message);
        _ = await RoomLighting.BakeAsync(fixture.Unlit.Get("hub"), settings, context.Content, new CompileParallelism { MaxDegree = 1 }, CancellationToken.None);
        _ = await RoomLighting.BakeAsync(fixture.Unlit.Get("sky"), settings, context.Content, new CompileParallelism { MaxDegree = 1 }, CancellationToken.None);
    }

    /// <summary>
    /// The library compile (what <c>ssmap room</c> runs) compiles the skybox
    /// before the rooms and lights each sky room over it: the skybox starts
    /// first, the rooms come out in library order, the sky room's lighting
    /// the bytes of the harness's skybox-first bake; with the skybox not
    /// listed (the cache serves it), its geometry alone is compiled for the
    /// bakes, the same lighting comes out, and the skybox is not delivered;
    /// with a skybox that does not compile, the sky room fails naming it and
    /// the hub is lit as always.
    /// </summary>
    [Fact]
    public async Task TheLibraryCompileCompilesTheSkyboxFirst()
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(SkyboxLitFixture.Library);
        VbspContext context = await RoomLightHarness.ContextAsync();
        RoomLightingSettings lighting = new(SkyboxLitFixture.Options)
        {
            Sun = RoomLightingSettings.SunOf(split.LibraryEntities),
            DoorLight = false,
            Skybox = split.Skybox,
        };
        List<int> started = [];
        RoomLibraryCompileSettings settings = new(context.Options, context.Content)
        {
            Lighting = lighting,
            Parallelism = new CompileParallelism { MaxDegree = 2 },
            BeforeRoomProbe = (index, _) =>
            {
                lock (started)
                {
                    started.Add(index);
                }

                return ValueTask.CompletedTask;
            },
        };
        byte[] expected = fixture.Lit.Get("other").Lighting!.ToSection().Bytes.ToArray();

        List<RoomCompileOutcome> listed = await CompileAsync([.. split.Rooms, split.Skybox!], settings);
        Assert.Equal(2, started[0]);
        Assert.Equal(3, started.Count);
        Assert.Equal(["hub", "other", "sky"], listed.Select(o => o.Room.Definition.Name));
        Assert.All(listed, o => Assert.Null(o.Error));
        Assert.Equal(expected, listed[1].Compiled!.Lighting!.ToSection().Bytes.ToArray());

        List<RoomCompileOutcome> unlisted = await CompileAsync(split.Rooms, settings);
        Assert.Equal(["hub", "other"], unlisted.Select(o => o.Room.Definition.Name));
        Assert.Equal(expected, unlisted[1].Compiled!.Lighting!.ToSection().Bytes.ToArray());

        // A skybox without its floor: it leaks, so it does not compile.
        LibraryRoom broken = split.Skybox! with { Document = await EmptyAsync(split.Skybox!.Document) };
        List<RoomCompileOutcome> failing = await CompileAsync(
            [.. split.Rooms, broken], new RoomLibraryCompileSettings(context.Options, context.Content) { Lighting = new RoomLightingSettings(lighting.Options) { Sun = lighting.Sun, DoorLight = false, Skybox = broken } });
        Assert.Null(failing[0].Error);
        Assert.NotNull(failing[2].Error);
        Assert.Contains("which did not compile", failing[1].Error!.Message, StringComparison.Ordinal);
    }

    private static async Task<List<RoomCompileOutcome>> CompileAsync(IReadOnlyList<LibraryRoom> rooms, RoomLibraryCompileSettings settings)
    {
        List<RoomCompileOutcome> outcomes = [];
        await RoomLibraryCompiler.CompileAsync(rooms, settings, (outcome, _) =>
        {
            outcomes.Add(outcome);
            return ValueTask.CompletedTask;
        });
        return outcomes;
    }

    /// <summary>A room document with its world's first brush (the shell's floor) taken out.</summary>
    private static async Task<VmfDocument> EmptyAsync(VmfDocument document)
    {
        VmfDocument copy = await VmfDocument.ParseAsync(document.ToBytes(), CancellationToken.None);
        VmfChunk world = copy.GetChunk(MapFileLoader.WorldChunk)!;
        world.Children.Remove(world.GetChunks(MapFileLoader.SolidChunk).First());

        return copy;
    }
}
