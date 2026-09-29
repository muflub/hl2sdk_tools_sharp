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
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The door light through the link (the rooms design, 9.1 parts 2 to 4 and
/// 9.8, as PR 10 built it): a capped room links as it did without it; a
/// jointed level gains the light its neighbours send through their doors,
/// only adds light, carries the neighbour's switchable styles, and agrees
/// with vrad of the linked level itself within the tolerances the sweep
/// measured, near the joints and elsewhere, where the base alone does not;
/// its leaf ambient and prop lighting gain the neighbour's light too; and
/// it is the same bytes at any thread count.
/// </summary>
public sealed class LevelLinkerDoorLightTests(LitRoomsFixture fixture, ITestOutputHelper output) : IClassFixture<LitRoomsFixture>
{
    // ---- 9.8: a capped room is unchanged ---------------------------------------------------------

    /// <summary>
    /// A room alone, every socket capped, receives no door light: its level
    /// is the same bytes with the door light as without it, at every turn,
    /// so it still links exactly as vrad of its own link
    /// (<see cref="LevelLinkerLightingTests.ACappedRoomLinksToVradOfItsOwnLinkAtEveryTurn"/>).
    /// </summary>
    [Theory]
    [MemberData(nameof(LevelLinkerLightingTests.RoomsAndTurns), MemberType = typeof(LevelLinkerLightingTests))]
    public async Task ACappedRoomLinksTheSameBytesWithItsDoorLight(string room, int rotation)
    {
        string row = $"{room}@{rotation}";
        Assert.Equal(await Bytes(await fixture.LinkedAsync(row)), await Bytes(await fixture.DoorLinkedAsync(row)));
    }

    // ---- 9.8: a jointed level ----------------------------------------------------------------------

    /// <summary>
    /// Two rooms jointed, at two turns, against vrad of the linked level
    /// itself (the same faces, so every luxel has a counterpart; the
    /// flattened full compile cuts faces its own way at a turn, see
    /// <see cref="LevelLinkerLightingTests"/>): with the door light, within a
    /// door width of the joint the 95th percentile of the relative error is
    /// under 8% and elsewhere under 8%, the level's energy within 2%, and
    /// under 3% of the luxels brighter by more than 5% (the door light does
    /// not invent light); the base alone, PR 9's link, is further off near
    /// the joint and short of energy. Measured (PR 10): near p95 0.056 and
    /// 0.037, elsewhere 0.024 and 0.057, energy 0.998, 20 and 68 luxels of
    /// about 3,500 brighter; the base alone near p95 over 0.3.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task AJointedLevelAgreesWithVradOfTheLinkWithinTheTolerances(int rotation)
    {
        string row = $"hub@{rotation}, other@{rotation}";
        LinkedLevel level = await RoomLightHarness.LinkAsync(await fixture.DoorLitAsync(), RoomPropHarness.Level(row));
        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(level);
        Assert.Equal(2, joints.Count);
        Assert.Empty(level.LightingWarnings);
        BspData relit = await fixture.RelitAsync(row);

        DoorLightCompare.Metric door = DoorLightCompare.Measure(level.Bsp, relit, joints, allStyles: true);
        DoorLightCompare.Metric base0 = DoorLightCompare.Measure(await fixture.LinkedAsync(row), relit, joints, allStyles: true);
        output.WriteLine($"{row} door light: {door}");
        output.WriteLine($"{row} base only:  {base0}");
        Assert.True(door.NearP95 <= 0.08, $"near p95 {door.NearP95}");
        Assert.True(door.FarP95 <= 0.08, $"far p95 {door.FarP95}");
        Assert.InRange(door.Energy, 0.98, 1.02);
        Assert.True(door.Brighter <= door.Count * 3 / 100, $"{door.Brighter} of {door.Count} brighter");
        Assert.True(base0.NearP95 > door.NearP95 * 2, $"base near p95 {base0.NearP95}");
        Assert.True(base0.Energy < door.Energy);
    }

    /// <summary>
    /// The door light only adds: every luxel of every style of the jointed
    /// level is at least the base's (to the encoding's rounding), a style
    /// the base did not give a face is the neighbour's switchable light, and
    /// some luxels are brighter, on both sides of the joint.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    public async Task TheDoorLightOnlyAddsToTheBase(int rotation)
    {
        string row = $"hub@{rotation}, other@{rotation}";
        var door = LitCompare.Lattice(await fixture.DoorLinkedAsync(row));
        var base0 = LitCompare.Lattice(await fixture.LinkedAsync(row));
        int brighter = 0;
        bool[] sides = new bool[2];
        foreach ((var key, (List<ColorRgbExp32> colours, bool _)) in base0)
        {
            Vec3 b = colours[0].ToLinear();
            Assert.True(door.TryGetValue(key, out var other), $"{key}");
            Vec3 d = other.Colours[0].ToLinear();
            Assert.True(
                d.X >= (b.X * 0.99f) - 1e-3f && d.Y >= (b.Y * 0.99f) - 1e-3f && d.Z >= (b.Z * 0.99f) - 1e-3f,
                $"{key}: {d} under the base's {b}");
            if (d.X + d.Y + d.Z > ((b.X + b.Y + b.Z) * 1.05f) + 0.01f)
            {
                brighter++;
                sides[key.Item1 < RoomHarness.Cell * 100 ? 0 : 1] = true;
            }
        }

        // A style the base did not give a face: the neighbour's switchable
        // light through the door, all of it added.
        foreach ((var key, (List<ColorRgbExp32> colours, bool _)) in door)
        {
            Vec3 d = colours[0].ToLinear();
            if (!base0.ContainsKey(key) && d.X + d.Y + d.Z > 0.01f)
            {
                Assert.NotEqual(0, key.Item7);
                brighter++;
                sides[key.Item1 < RoomHarness.Cell * 100 ? 0 : 1] = true;
            }
        }

        output.WriteLine($"{row}: {brighter} of {base0.Count} luxels brighter");
        Assert.True(brighter > 50);
        Assert.Equal([true, true], sides);
    }

    /// <summary>
    /// The other room's switchable lamp reaches the hub through the door
    /// under its own style, renumbered for the level as the other room's own
    /// faces have it: faces of the hub that had only style 0 without the
    /// door light have that style too with it, and no face of the hub has a
    /// style the level does not give the other room.
    /// </summary>
    [Fact]
    public async Task ANeighboursSwitchableLampCrossesTheDoorUnderItsStyle()
    {
        const string row = "hub@0, other@0";
        BspData door = await fixture.DoorLinkedAsync(row);
        BspData base0 = await fixture.LinkedAsync(row);
        DFace[] doorFaces = BspStructView.As<DFace>(door[BspLump.Faces]).ToArray();
        DFace[] baseFaces = BspStructView.As<DFace>(base0[BspLump.Faces]).ToArray();
        Assert.Equal(baseFaces.Length, doorFaces.Length);
        HashSet<byte> otherStyles = [];
        HashSet<byte> gained = [];
        for (int f = 0; f < doorFaces.Length; f++)
        {
            bool hub = RoomHarness.FaceVertices(door, doorFaces[f]).Average(v => v.X) < RoomHarness.Cell;
            for (int k = 0; k < 4; k++)
            {
                byte style = doorFaces[f].Styles[k];
                if (style is 0 or 255)
                {
                    continue;
                }

                if (!hub)
                {
                    otherStyles.Add(style);
                }
                else if (!Enumerable.Range(0, 4).Any(j => baseFaces[f].Styles[j] == style))
                {
                    gained.Add(style);
                }
            }
        }

        output.WriteLine($"other's styles {string.Join(",", otherStyles)}; the hub gained {string.Join(",", gained)}");
        Assert.NotEmpty(gained);
        Assert.Subset(otherStyles, gained);
    }

    /// <summary>
    /// The hub's leaf ambient and its prop's lighting gain the other room's
    /// light through the door. The hub has no sky and its surfaces reflect
    /// nothing, so its base leaf ambient is black; with the door light, the
    /// cube of its room leaf gains the other room's sky seen through the
    /// door, every face at least the base's, the sum of its faces within a
    /// factor of three of vrad of the linked level (measured: 2.1 times; vrad
    /// takes the mean of seven samples spread over the leaf, the link keeps
    /// the room's one, and a few of vrad's 162 rays per sample pass the
    /// door). The prop's vertex colours change and end nearer vrad of the
    /// link than the base's were.
    /// </summary>
    [Fact]
    public async Task LeafAmbientAndPropsGainTheNeighboursLight()
    {
        const string row = "hub@0, other@0";
        BspData door = await fixture.DoorLinkedAsync(row);
        BspData base0 = await fixture.LinkedAsync(row);
        BspData relit = await fixture.RelitAsync(row);

        DLeaf[] leaves = BspStructView.As<DLeaf>(door[BspLump.Leafs]).ToArray();
        int room = Enumerable.Range(0, leaves.Length)
            .Where(l => leaves[l].Maxs[0] <= RoomHarness.Cell - RoomHarness.WalkableKit.Depth)
            .MaxBy(l => (long)(leaves[l].Maxs[0] - leaves[l].Mins[0]) * (leaves[l].Maxs[1] - leaves[l].Mins[1]) * (leaves[l].Maxs[2] - leaves[l].Mins[2]));
        Vec3[] withDoor = Cube(door, room), without = Cube(base0, room), vrad = Cube(relit, room);
        output.WriteLine($"leaf {room}: door light {string.Join(" ", withDoor)}; base {string.Join(" ", without)}; vrad of the link {string.Join(" ", vrad)}");
        Assert.All(without, c => Assert.Equal(Vec3.Zero, c));
        for (int side = 0; side < 6; side++)
        {
            Assert.True(withDoor[side].X >= without[side].X && withDoor[side].Y >= without[side].Y && withDoor[side].Z >= without[side].Z);
        }

        double ours = withDoor.Sum(c => c.X + c.Y + c.Z), theirs = vrad.Sum(c => c.X + c.Y + c.Z);
        Assert.True(theirs > 0);
        Assert.InRange(ours / theirs, 1 / 3.0, 3.0);

        (string Name, byte[] Data)[] doorProps = await LevelLinkerLightingTests.VhvFiles(door);
        (string Name, byte[] Data)[] baseProps = await LevelLinkerLightingTests.VhvFiles(base0);
        (string Name, byte[] Data)[] relitProps = await LevelLinkerLightingTests.VhvFiles(relit);
        Assert.Single(doorProps);
        Assert.Equal(relitProps.Select(p => p.Name), doorProps.Select(p => p.Name));
        Assert.False(doorProps[0].Data.AsSpan().SequenceEqual(baseProps[0].Data));
        long doorOff = ByteDistance(doorProps[0].Data, relitProps[0].Data), baseOff = ByteDistance(baseProps[0].Data, relitProps[0].Data);
        output.WriteLine($"prop colours off vrad of the link: door light {doorOff}, base {baseOff}");
        Assert.True(doorOff < baseOff);

        // A leaf's cube: per face, the mean of its samples.
        static Vec3[] Cube(BspData bsp, int leaf)
        {
            DLeafAmbientLighting[] samples = BspStructView.As<DLeafAmbientLighting>(bsp[BspLump.LeafAmbientLighting]).ToArray();
            DLeafAmbientIndex index = BspStructView.As<DLeafAmbientIndex>(bsp[BspLump.LeafAmbientIndex])[leaf];
            Assert.True(index.AmbientSampleCount > 0);
            Vec3[] cube = new Vec3[6];
            for (int side = 0; side < 6; side++)
            {
                for (int s = index.FirstAmbientSample; s < index.FirstAmbientSample + index.AmbientSampleCount; s++)
                {
                    cube[side] += samples[s].Cube.Color[side].ToLinear() * (1f / index.AmbientSampleCount);
                }
            }

            return cube;
        }

        static long ByteDistance(byte[] a, byte[] b)
        {
            Assert.Equal(b.Length, a.Length);
            long sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                sum += Math.Abs(a[i] - b[i]);
            }

            return sum;
        }
    }

    /// <summary>
    /// A face holds at most four styles: five switchable lamps in the other
    /// room, all seen through the door by the hub's far wall, would give its
    /// faces six (style 0 and five of the other room's); the link keeps four,
    /// dropping the weakest of the door's styles with a warning naming the
    /// room, its cell and the face, and the level passes the loader's checks.
    /// </summary>
    [Fact]
    public async Task AFaceThatWouldNeedMoreThanFourStylesDropsTheWeakestDoorStylesWithAWarning()
    {
        VmfDocument library = RoomLightHarness.Library(
            false,
            [],
            (0, RoomLightHarness.Light(800, LitRoomsFixture.HubLight)),
            (1, RoomLightHarness.Light(810, new Vec3(40, 100, 60), "lamp_a", "255 255 255 400")),
            (1, RoomLightHarness.Light(811, new Vec3(40, 150, 60), "lamp_b", "255 255 255 300")),
            (1, RoomLightHarness.Light(812, new Vec3(40, 100, 180), "lamp_c", "255 255 255 200")),
            (1, RoomLightHarness.Light(813, new Vec3(40, 150, 180), "lamp_d", "255 255 255 100")),
            (1, RoomLightHarness.Light(814, new Vec3(40, 128, 120), "lamp_e", "255 255 255 50")));
        RoomLibrary rooms = await RoomLightHarness.CompileAsync(library, doorLight: true);
        LinkedLevel level = await RoomLightHarness.LinkAsync(rooms, RoomPropHarness.Level("hub@0, other@0"));
        foreach (string warning in level.LightingWarnings)
        {
            output.WriteLine(warning);
        }

        Assert.NotEmpty(level.LightingWarnings);
        Assert.All(level.LightingWarnings, w => Assert.Matches(@"^room hub at cell \(0, 0\): face \d+ would need [56] light styles with its neighbours' door light; style \d+ was left out\.$", w));

        DFace[] faces = BspStructView.As<DFace>(level.Bsp[BspLump.Faces]).ToArray();
        Assert.Contains(faces, f => f.Styles[3] != 255 && RoomHarness.FaceVertices(level.Bsp, f).Average(v => v.X) < RoomHarness.Cell);
        foreach (DFace face in faces)
        {
            byte[] used = [.. Enumerable.Range(0, 4).Select(k => face.Styles[k]).Where(s => s != 255)];
            Assert.Equal(used.Length, used.Distinct().Count());
        }

        ValidationReport report = await BspValidator.CheckAsync(level.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("; ", report.Diagnostics));
    }

    /// <summary>A jointed level lit with its door light passes the loader's checks.</summary>
    [Fact]
    public async Task ADoorLitLevelPassesTheValidator()
    {
        ValidationReport report = await BspValidator.CheckAsync(await fixture.DoorLinkedAsync("hub@90, other@270"), CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("; ", report.Diagnostics));
    }

    // ---- determinism -------------------------------------------------------------------------------

    /// <summary>
    /// 15.5: the door light bake is the same bytes at any thread count (each
    /// room's section, one thread against four), and so is a level linked
    /// from it.
    /// </summary>
    [Fact]
    public async Task TheDoorLightBakeAndLinkAreTheSameBytesAtAnyThreadCount()
    {
        RoomLibrary one = await fixture.DoorLitAsync();
        RoomLibrary four = await RoomLightHarness.CompileAsync(LitRoomsFixture.Library, degree: 4, options: LitRoomsFixture.Options, doorLight: true);
        foreach (RoomObject room in one.Rooms)
        {
            Assert.Equal(
                room.DoorLight!.ToSection().Bytes.ToArray(),
                four.Get(room.Definition.Name).DoorLight!.ToSection().Bytes.ToArray());
        }

        LevelGrid level = RoomPropHarness.Level("hub@90, other@270", "other@0, hub@180");
        Assert.Equal(
            await Bytes((await RoomLightHarness.LinkAsync(one, level, 1)).Bsp),
            await Bytes((await RoomLightHarness.LinkAsync(four, level, 4)).Bsp));
    }

    private static async Task<byte[]> Bytes(BspData bsp)
    {
        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
        return stream.ToArray();
    }
}
