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
using SourceSharp.MapTools.Rooms;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms whose walls reflect light, lit with their door light, compiled
/// once for the class: the hub lit by an inverse-square lamp, the other
/// room by a dimmer lamp of its own, and a level linked from them at a turn
/// and vrad of that level.
/// </summary>
public sealed class ReflectiveRoomsFixture : IAsyncLifetime
{
    /// <summary>The shell's reflectivity.</summary>
    public const string Reflectivity = ".6 .55 .5";

    /// <summary>The library.</summary>
    public static VmfDocument Library { get; } = RoomLightHarness.Library(
        false,
        [],
        (0, QuadraticLight(800, new Vec3(150, 100, 120), "255 240 220 800")),
        (1, RoomLightHarness.Light(820, new Vec3(90, 160, 60), colour: "200 220 255 60")));

    /// <summary>The rooms, lit with their door light.</summary>
    public RoomLibrary Rooms { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync() =>
        Rooms = await RoomLightHarness.CompileAsync(Library, doorLight: true, reflectivity: Reflectivity);

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    private static VmfChunk QuadraticLight(int id, Vec3 origin, string colour)
    {
        VmfChunk light = RoomLightHarness.Light(id, origin, colour: colour);
        light.AddKey("_quadratic_attn", "1");
        return light;
    }
}

/// <summary>
/// The bounced half of the door light (the rooms design, 9.1 part 3, as PR
/// 10 built it): a room that reflects light records its answer to sixteen
/// point emitters behind each opening, and the link shares each neighbour
/// source's light over them, so the light that enters by a door bounces
/// around the room it enters; measured against vrad of the linked level
/// within the tolerances the sweep found for reflecting rooms.
/// </summary>
public sealed class LevelLinkerDoorLightBounceTests(ReflectiveRoomsFixture fixture, ITestOutputHelper output) : IClassFixture<ReflectiveRoomsFixture>
{
    /// <summary>
    /// A reflecting room answers every socket with sixteen emitters, each
    /// of which bounces light onto its faces and lights its leaf ambient.
    /// </summary>
    [Fact]
    public void AReflectingRoomRecordsItsResponseAtEverySocket()
    {
        foreach (RoomObject room in fixture.Rooms.Rooms)
        {
            DoorLightRange range = room.DoorLight!.Ldr!;
            Assert.Equal(room.Definition.Sockets.Count, range.Responses.Length);
            Assert.All(range.Responses, socket =>
            {
                Assert.Equal(DoorLightMath.EmitterCount, socket.Length);
                Assert.All(socket, e => Assert.NotEmpty(e.Faces));
                Assert.Contains(socket, e => e.Ambient.Length > 0);
            });
        }
    }

    /// <summary>
    /// 15.9 and O14: a room's response is stored once and applied at every
    /// turn, so a response run lit at each quarter turn in the bake frame
    /// agrees with the one at turn 0: every face's bounced light the same to
    /// half-float rounding, and the leaf ambient samples, which vrad gathers
    /// along world-fixed directions, within their sampling noise.
    /// </summary>
    [Fact]
    public async Task AResponseIsTheSameAtEveryTurn()
    {
        RoomObject room = fixture.Rooms.Get("other");
        BspData open = RoomDoorLight.Open(room, LevelLinker.ComputeShared(room));
        DoorFrame frame = DoorFrame.Of(room.Definition, room.Definition.Sockets[0]);
        RoomLightingSettings settings = new(RoomLightHarness.Options);
        SourceSharp.MapTools.Io.IContentFileSystem content = (await RoomLightHarness.ContextAsync("other", reflectivity: ReflectiveRoomsFixture.Reflectivity)).Content!;
        SourceSharp.MapTools.Parallel.CompileParallelism one = new() { MaxDegree = 1 };
        DoorResponseEmitter first = (await RoomDoorLight.RespondAsync(room, open, frame, 0, settings, content, one, null, 0, CancellationToken.None)).Ldr!;
        for (int turn = 1; turn < 4; turn++)
        {
            DoorResponseEmitter turned = (await RoomDoorLight.RespondAsync(room, open, frame, 0, settings, content, one, null, turn, CancellationToken.None)).Ldr!;
            Assert.Equal(first.Faces.Select(f => f.Face), turned.Faces.Select(f => f.Face));
            for (int f = 0; f < first.Faces.Length; f++)
            {
                for (int v = 0; v < first.Faces[f].Values.Length; v++)
                {
                    float a = (float)first.Faces[f].Values[v], b = (float)turned.Faces[f].Values[v];
                    Assert.True(MathF.Abs(a - b) <= MathF.Max(MathF.Abs(a), MathF.Abs(b)) * 2e-3f + 1e-9f, $"turn {turn} face {first.Faces[f].Face} value {v}: {a} against {b}");
                }
            }

            // Per leaf, the mean of its samples: which candidates vrad keeps
            // depends on their last bits.
            Dictionary<int, Vec3[]> ours = Means(first.Ambient), theirs = Means(turned.Ambient);
            Assert.Equal(ours.Keys.Order(), theirs.Keys.Order());
            List<double> relative = [];
            foreach ((int leaf, Vec3[] cube) in ours)
            {
                for (int side = 0; side < 6; side++)
                {
                    relative.Add(LitCompare.Relative(cube[side], theirs[leaf][side]));
                }
            }

            relative.Sort();
            output.WriteLine($"turn {turn}: {first.Faces.Length} faces agree; ambient p50 {LitCompare.Quantile(relative, .5):G3} p95 {LitCompare.Quantile(relative, .95):G3}");
            Assert.True(LitCompare.Quantile(relative, .95) <= 0.3, $"turn {turn}: ambient p95 {LitCompare.Quantile(relative, .95)}");
        }

        static Dictionary<int, Vec3[]> Means(DoorResponseSample[] samples)
        {
            Dictionary<int, Vec3[]> means = [];
            foreach (IGrouping<int, DoorResponseSample> leaf in samples.GroupBy(x => x.Leaf))
            {
                Vec3[] cube = new Vec3[6];
                foreach (DoorResponseSample sample in leaf)
                {
                    for (int side = 0; side < 6; side++)
                    {
                        cube[side] += new Vec3((float)sample.Cube[side * 3], (float)sample.Cube[(side * 3) + 1], (float)sample.Cube[(side * 3) + 2]) * (1f / leaf.Count());
                    }
                }

                means[leaf.Key] = cube;
            }

            return means;
        }
    }

    /// <summary>
    /// Two reflecting rooms jointed, against vrad of the linked level: within
    /// a door width of the joint and elsewhere the 95th percentile of the
    /// relative error under 20%, the energy within 3%, and the 99th
    /// percentile of what any luxel has over vrad's under 30%. Measured (PR
    /// 10): near p95 0.10, elsewhere 0.10 and 0.09, energy 1.013 and 1.014;
    /// the base alone is short of energy and far further off near the joint.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task ReflectingRoomsAgreeWithVradOfTheLinkWithinTheTolerances(int rotation)
    {
        LevelGrid grid = RoomPropHarness.Level($"hub@{rotation}, other@{rotation}");
        LinkedLevel level = await RoomLightHarness.LinkAsync(fixture.Rooms, grid);
        BspData relit = await RoomLightHarness.RelightAsync(level.Bsp, reflectivity: ReflectiveRoomsFixture.Reflectivity);
        RoomLibrary baseOnly = new(fixture.Rooms.Kit, fixture.Rooms.CellSize) { LibraryEntities = fixture.Rooms.LibraryEntities, Options = fixture.Rooms.Options };
        foreach (RoomObject room in fixture.Rooms.Rooms)
        {
            baseOnly.Add(room with { DoorLight = null });
        }

        BspData base0 = (await RoomLightHarness.LinkAsync(baseOnly, grid)).Bsp;
        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(level);
        DoorLightCompare.Metric door = DoorLightCompare.Measure(level.Bsp, relit, joints);
        DoorLightCompare.Metric without = DoorLightCompare.Measure(base0, relit, joints);
        output.WriteLine($"door light: {door}");
        output.WriteLine($"base only:  {without}");
        Assert.True(door.NearP95 <= 0.2, $"near p95 {door.NearP95}");
        Assert.True(door.FarP95 <= 0.2, $"far p95 {door.FarP95}");
        Assert.InRange(door.Energy, 0.97, 1.03);
        Assert.True(door.ExcessP99 <= 0.3, $"excess p99 {door.ExcessP99}");
        Assert.True(without.NearP95 > door.NearP95 * 2, $"base near p95 {without.NearP95}");
        Assert.True(without.Energy < 0.97);
    }
}
