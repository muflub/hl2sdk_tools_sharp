//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The 3D skybox in each room's bake (the rooms design, 4.12's lighting
/// line): a sky room's sky rays are recast into the library's skybox, as
/// vrad recasts them in the linked and the flattened level, so a skybox
/// whose geometry shadows the sky shadows the room's bake too.
/// </summary>
public sealed class LevelLinkerSkyboxLightingTests(SkyboxLitFixture fixture, ITestOutputHelper output) : IClassFixture<SkyboxLitFixture>
{
    /// <summary>The four quarter turns.</summary>
    public static TheoryData<int> Turns => new() { 0, 90, 180, 270 };

    /// <summary>
    /// The sky room alone, every socket capped, with the skybox below it,
    /// links to vrad of that very linked map at every quarter turn: the same
    /// styles and the same luxels byte for byte on every face more than one
    /// luxel across (thin faces as PR 9's capped facts hold them: exact at
    /// turn 0), the same world lights, vertex normals and map flags. Red
    /// before the skybox joined the bake: the overhang's shadow on the sky
    /// was missing from the room's own lightmaps.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public async Task ASkyRoomLinksToVradOfItsOwnLinkWithTheSkyboxAtEveryTurn(int rotation)
    {
        string row = $"other@{rotation}";
        BspData linked = await fixture.LinkedAsync(row);
        BspData relit = await fixture.RelitAsync(row);
        (bool styles, int luxels, int differ, int thin) = LitCompare.SameFaces(linked, relit);
        output.WriteLine($"{row}: {luxels} luxels, {differ} differ, {thin} on thin faces differ");
        Assert.True(styles);
        Assert.True(luxels > 0);
        Assert.Equal(0, differ);
        if (rotation == 0)
        {
            Assert.Equal(0, thin);
        }

        foreach (BspLump lump in (ReadOnlySpan<BspLump>)[BspLump.WorldLights, BspLump.VertNormals, BspLump.VertNormalIndices, BspLump.MapFlags])
        {
            Assert.True(linked[lump].Data.Span.SequenceEqual(relit[lump].Data.Span), $"{row}: {lump}");
        }
    }

    /// <summary>
    /// The sky room and the skybox against the full compile of the flattened
    /// level (vbsp, vvis and vrad, the rooms' switches) within PR 9's
    /// capped-room tolerances: at turn 0 every luxel both maps hold is the
    /// same bytes; at a turn, where vbsp's grids meet, at least 98% exact,
    /// p99 within 0.05 and none past 0.25.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public async Task ASkyRoomAgreesWithItsFlattenedFullCompileWithTheSkybox(int rotation)
    {
        string row = $"other@{rotation}";
        (int both, int exact, List<double> relative) = Compare(await fixture.LinkedAsync(row), await fixture.FlatAsync(row));
        double p99 = LitCompare.Quantile(relative, 0.99), max = LitCompare.Quantile(relative, 1);
        output.WriteLine($"{row}: {both} luxels in both, {exact} exact, p95 {LitCompare.Quantile(relative, 0.95):G3} p99 {p99:G3} max {max:G3}");
        Assert.True(both > 0);
        if (rotation == 0)
        {
            Assert.Equal(both, exact);
        }

        Assert.True(p99 <= 0.05 && max <= 0.25, $"{row}: p99 {p99}, max {max}");
        Assert.True(exact >= both * 0.98, $"{row}: {exact} of {both} exact");
    }

    /// <summary>
    /// The sky room and the hub jointed, the sky room at its bakes' cell
    /// (the grid's south-west cell, above the skybox), lit with their door
    /// light: against vrad of the linked level within PR 10's tolerances
    /// (near and elsewhere p95 under 0.08, energy within 2%, under 3% of
    /// luxels more than 5% brighter), where the rooms baked sealed, as
    /// before the skybox joined the bake, are far off near the joint (the
    /// hub's floor lit by the sun the overhang shades) and too bright.
    /// Measured: near p95 0.008 to 0.009, elsewhere 0.035 to 0.056, energy
    /// 0.995 to 0.997; sealed near p95 0.64 to 0.76, energy 1.12 to 1.13.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task AJointedLevelWithTheSkyboxAgreesWithVradOfTheLinkWithinTheTolerances(int rotation)
    {
        string row = $"other@{rotation}, hub@{rotation}";
        LinkedLevel level = await fixture.DoorLinkedAsync(row);
        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(level);
        Assert.Equal(2, joints.Count);
        BspData relit = await fixture.RelitAsync(row);
        DoorLightCompare.Metric door = DoorLightCompare.Measure(level.Bsp, relit, joints, allStyles: true);
        DoorLightCompare.Metric sealedBake = DoorLightCompare.Measure((await fixture.DoorLinkedAsync(row, sealedBake: true)).Bsp, relit, joints, allStyles: true);
        output.WriteLine($"{row} with the skybox: {door}");
        output.WriteLine($"{row} sealed:          {sealedBake}");
        AssertWithinDoorLightTolerances(door);
        Assert.True(sealedBake.NearP95 > 0.5, $"sealed near p95 {sealedBake.NearP95}");
        Assert.True(sealedBake.Energy > 1.1, $"sealed energy {sealedBake.Energy}");
    }

    /// <summary>
    /// The same level against the full compile of its flattened map (vbsp,
    /// vvis, vrad), held to the same tolerances. Measured: near p95 0.009
    /// to 0.013, elsewhere 0.036 to 0.054, energy 0.992 to 0.994; sealed
    /// near p95 0.51 to 0.64, energy 1.11 to 1.13.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task AJointedLevelWithTheSkyboxAgreesWithItsFlattenedFullCompile(int rotation)
    {
        string row = $"other@{rotation}, hub@{rotation}";
        LinkedLevel level = await fixture.DoorLinkedAsync(row);
        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(level);
        BspData flat = await fixture.FlatAsync(row);
        DoorLightCompare.Metric door = DoorLightCompare.Measure(level.Bsp, flat, joints, allStyles: true);
        DoorLightCompare.Metric sealedBake = DoorLightCompare.Measure((await fixture.DoorLinkedAsync(row, sealedBake: true)).Bsp, flat, joints, allStyles: true);
        output.WriteLine($"{row} with the skybox: {door}");
        output.WriteLine($"{row} sealed:          {sealedBake}");
        AssertWithinDoorLightTolerances(door);
        Assert.True(sealedBake.NearP95 > 0.5, $"sealed near p95 {sealedBake.NearP95}");
    }

    /// <summary>
    /// The known difference the bake cannot hold: a sky room one cell east
    /// of its bakes' cell. vrad recasts a sample at <c>p</c> to
    /// <c>camera + p / scale</c>, so the room's recast starts lie 16 units
    /// further east in the skybox (its 256-unit cell over the scale of 16),
    /// past the overhang's edge, and vrad of the level shades the room far
    /// less than its bake does: measured, elsewhere p95 0.42 to 0.46, energy
    /// 0.90, the sealed bake nearer (p95 0.02). Held here so the parallax
    /// stays what the design note says it is: the linked room's own light is
    /// the bake's, cell for cell, and only darker than vrad's (the overhang
    /// the bake saw), never brighter.
    /// </summary>
    [Fact]
    public async Task ASkyRoomAwayFromItsBakesCellSeesTheSkyboxFromThere()
    {
        const string row = "hub@0, other@0";
        LinkedLevel level = await fixture.DoorLinkedAsync(row);
        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(level);
        DoorLightCompare.Metric door = DoorLightCompare.Measure(level.Bsp, await fixture.RelitAsync(row), joints, allStyles: true);
        output.WriteLine($"{row}: {door}");
        Assert.True(door.FarP95 > 0.3, $"far p95 {door.FarP95}");
        Assert.InRange(door.Energy, 0.85, 0.95);
        Assert.True(door.ExcessP99 <= 0.05, $"excess p99 {door.ExcessP99}");
    }

    private static void AssertWithinDoorLightTolerances(DoorLightCompare.Metric metric)
    {
        Assert.True(metric.NearP95 <= 0.08, $"near p95 {metric.NearP95}");
        Assert.True(metric.FarP95 <= 0.08, $"far p95 {metric.FarP95}");
        Assert.InRange(metric.Energy, 0.98, 1.02);
        Assert.True(metric.Brighter <= metric.Count * 3 / 100, $"{metric.Brighter} of {metric.Count} brighter");
    }

    /// <summary>
    /// Only a sky room's bake changes: the hub, which no sky reaches, bakes
    /// and records its door light to the same bytes with the skybox as
    /// sealed and alone; the sky room's base and door light both change
    /// (its lightmaps shaded by the overhang, and the sun and sky it sends
    /// through its doors recast into the skybox).
    /// </summary>
    [Fact]
    public async Task OnlyTheSkyRoomsBakeChanges()
    {
        RoomLibrary recast = await fixture.DoorLitAsync();
        RoomLibrary sealedBake = await fixture.DoorLitSealedAsync();
        Assert.Equal(Section(sealedBake.Get("hub").Lighting!.ToSection()), Section(recast.Get("hub").Lighting!.ToSection()));
        Assert.Equal(Section(sealedBake.Get("hub").DoorLight!.ToSection()), Section(recast.Get("hub").DoorLight!.ToSection()));
        Assert.NotEqual(Section(sealedBake.Get("other").Lighting!.ToSection()), Section(recast.Get("other").Lighting!.ToSection()));
        Assert.NotEqual(Section(sealedBake.Get("other").DoorLight!.ToSection()), Section(recast.Get("other").DoorLight!.ToSection()));
        Assert.Equal(Section(sealedBake.Get("sky").Lighting!.ToSection()), Section(recast.Get("sky").Lighting!.ToSection()));
    }

    /// <summary>
    /// A sky room's bake and door light with the skybox are the same bytes at
    /// one thread and at four.
    /// </summary>
    [Fact]
    public async Task TheBakeWithTheSkyboxIsTheSameAtAnyThreadCount()
    {
        RoomLibrary one = await fixture.DoorLitAsync();
        RoomLibrary four = await SkyboxLitFixture.CompileAsync(SkyboxLitFixture.Library, light: true, degree: 4, doorLight: true);
        foreach (string room in (string[])["hub", "other", "sky"])
        {
            Assert.Equal(Section(one.Get(room).Lighting!.ToSection()), Section(four.Get(room).Lighting!.ToSection()));
            if (one.Get(room).DoorLight is { } door)
            {
                Assert.Equal(Section(door.ToSection()), Section(four.Get(room).DoorLight!.ToSection()));
            }
        }
    }

    private static byte[] Section(RoomPackSectionData section) => section.Bytes.ToArray();

    /// <summary>The luxels two maps hold at the same points (thin faces left out): how many, how many exact, and each one's relative difference, sorted.</summary>
    internal static (int Both, int Exact, List<double> Relative) Compare(BspData a, BspData b)
    {
        var la = LitCompare.Lattice(a);
        var lb = LitCompare.Lattice(b);
        List<double> relative = [];
        int exact = 0;
        foreach ((var key, (List<ColorRgbExp32> colours, bool thin)) in la)
        {
            if (!lb.TryGetValue(key, out var other) || thin || other.Thin)
            {
                continue;
            }

            exact += colours.Any(c => other.Colours.Any(o => LitCompare.Same(c, o))) ? 1 : 0;
            relative.Add(colours.Min(c => other.Colours.Min(o => LitCompare.Relative(c.ToLinear(), o.ToLinear()))));
        }

        relative.Sort();
        return (relative.Count, exact, relative);
    }
}
