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

using SourceSharp.MapTools.Rooms;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Lighting of a tall room (the rooms design, 17.6 and section 9): its base
/// bake, its door light to a cube neighbour and the level's sky pass work
/// as they do for cubes. The tall room has a lamp high up, above the hub's
/// top, and a sky ceiling the library's sun lights; the hub has a lamp.
/// </summary>
public sealed class LevelLinkerHeightsLightTests(LevelLinkerHeightsLightTests.Fixture fixture, ITestOutputHelper output)
    : IClassFixture<LevelLinkerHeightsLightTests.Fixture>
{
    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// A tall sunlit room alone, every socket capped, links to vrad of its
    /// own linked map at every turn, from its bake for that turn: the same
    /// styles and luxels on every face (its walls above the cube's top
    /// included), the same world lights, leaves (their sky flags among
    /// them) and map flags.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ACappedTallRoomLinksToVradOfItsOwnLinkAtEveryTurn(int rotation)
    {
        string row = $"tall@{rotation}";
        Assert.Equal(4, fixture.Lit.Get("tall").LightingOfCompile!.RotationCount);
        BspData linked = (await RoomLightHarness.LinkAsync(fixture.Lit, RoomHeightHarness.Level(row))).Bsp;
        BspData relit = await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(fixture.Unlit, RoomHeightHarness.Level(row))).Bsp);

        (bool styles, int luxels, int differ, int thin) = LitCompare.SameFaces(linked, relit);
        output.WriteLine($"{row}: {luxels} luxels, {differ} differ, {thin} on thin faces differ");
        Assert.True(styles);
        Assert.True(luxels > 0);
        Assert.Equal(0, differ);
        Assert.True(rotation != 0 || thin == 0);
        foreach (BspLump lump in (ReadOnlySpan<BspLump>)[BspLump.WorldLights, BspLump.Leafs, BspLump.MapFlags])
        {
            Assert.True(linked[lump].Data.Span.SequenceEqual(relit[lump].Data.Span), $"{row}: {lump}");
        }

        // The upper walls are baked: the walls reaching above the cube's top have lightmaps.
        Assert.True(UpperLight(linked) >= 4);
    }

    /// <summary>
    /// The tall room jointed to the cube hub, at two turns, against vrad of
    /// the linked level: PR 10's tolerances hold (near and elsewhere p95
    /// under 0.08, energy within 2%, under 3% of luxels more than 5%
    /// brighter), and the base alone is at least twice as far off near the
    /// joint: the tall room's high lamp reaches the hub through the door,
    /// and the hub's lamp the tall room.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task ATallRoomsDoorLightMeetsTheTolerances(int rotation)
    {
        LevelGrid level = RoomHeightHarness.Level($"hub@{rotation}, tall@{rotation}");
        LinkedLevel door = await RoomLightHarness.LinkAsync(fixture.DoorLit, level);
        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(door);
        Assert.Equal(2, joints.Count);
        BspData relit = await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(fixture.Unlit, level)).Bsp);
        BspData base0 = (await RoomLightHarness.LinkAsync(fixture.Lit, level)).Bsp;

        DoorLightCompare.Metric withDoor = DoorLightCompare.Measure(door.Bsp, relit, joints, allStyles: true);
        DoorLightCompare.Metric alone = DoorLightCompare.Measure(base0, relit, joints, allStyles: true);
        output.WriteLine($"door light: {withDoor}");
        output.WriteLine($"base only:  {alone}");
        Assert.True(withDoor.NearP95 <= 0.08, $"near p95 {withDoor.NearP95}");
        Assert.True(withDoor.FarP95 <= 0.08, $"far p95 {withDoor.FarP95}");
        Assert.InRange(withDoor.Energy, 0.98, 1.02);
        Assert.True(withDoor.Brighter <= withDoor.Count * 3 / 100, $"{withDoor.Brighter} of {withDoor.Count} brighter");
        Assert.True(alone.NearP95 > withDoor.NearP95 * 2, $"base near p95 {alone.NearP95}");
    }

    /// <summary>
    /// The sky pass over a level of the tall sky room and the hub: every
    /// point of both cells, high in the tall room too, has the sky flags the
    /// flattened level's full compile gives it, the hub's seen through the
    /// doorway included.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(270)]
    public async Task SkyFlagsOfATallRoomAreTheFullCompiles(int rotation)
    {
        LevelGrid level = RoomHeightHarness.Level($"hub@{rotation}, tall@{rotation}");
        BspData linked = (await RoomLightHarness.LinkAsync(fixture.Lit, level)).Bsp;
        BspData flat = await RoomLightHarness.CompileFlatLitAsync(fixture.Library, level);
        int flagged = 0;
        for (float x = 24; x < 2 * RoomHarness.Cell; x += 40)
        {
            for (float y = 24; y < RoomHarness.Cell; y += 40)
            {
                foreach (float z in new[] { 100f, 400f })
                {
                    Vec3 point = new(x, y, z);
                    DLeaf a = RoomHarness.LeafAt(linked, point);
                    DLeaf b = RoomHarness.LeafAt(flat, point);
                    if ((a.Contents & 1) == 0 && (b.Contents & 1) == 0)
                    {
                        LeafFlags ours = a.GetFlags() & (LeafFlags.Sky | LeafFlags.Sky2D);
                        LeafFlags theirs = b.GetFlags() & (LeafFlags.Sky | LeafFlags.Sky2D);
                        Assert.True(ours == theirs, $"{point}: {ours} against {theirs}");
                        flagged += ours == LeafFlags.Sky ? 1 : 0;
                    }
                }
            }
        }

        Assert.True(flagged > 0);
    }

    /// <summary>How many faces reaching above the cube's top have a lightmap.</summary>
    private static int UpperLight(BspData bsp)
    {
        int lit = 0;
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        foreach (DFace face in faces)
        {
            if (face.LightOfs < 0 || RoomHarness.FaceVertices(bsp, face).All(v => v.Z < RoomHarness.Cell + 1))
            {
                continue;
            }

            lit++;
        }

        return lit;
    }

    /// <summary>The library, compiled lit, lit with its door light, and unlit, once for the class.</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        /// <summary>The library: the hub with a lamp; the tall room with a lamp high up and a sky ceiling; the sun in the gap.</summary>
        public VmfDocument Library { get; } = MakeLibrary();

        /// <summary>The rooms, lit.</summary>
        public RoomLibrary Lit { get; private set; } = null!;

        /// <summary>The rooms, lit with their door light.</summary>
        public RoomLibrary DoorLit { get; private set; } = null!;

        /// <summary>The rooms, unlit.</summary>
        public RoomLibrary Unlit { get; private set; } = null!;

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            Lit = await RoomLightHarness.CompileAsync(Library);
            DoorLit = await RoomLightHarness.CompileAsync(Library, doorLight: true);
            Unlit = await RoomLightHarness.CompileAsync(Library, light: false);
        }

        /// <inheritdoc/>
        public Task DisposeAsync() => Task.CompletedTask;

        private static VmfDocument MakeLibrary()
        {
            VmfDocument library = RoomHeightHarness.Library(
                (0, RoomLightHarness.Light(800, new Vec3(128, 128, 150))),
                (1, RoomLightHarness.Light(810, new Vec3(100, 150, 400))));
            library.Chunks.Add(RoomLightHarness.Sun());
            RoomLightHarness.SkyCeiling(library, 1, RoomLightHarness.Sky, RoomHeightHarness.Tall);
            RoomLightHarness.WorldAlign(library);
            return library;
        }
    }
}
