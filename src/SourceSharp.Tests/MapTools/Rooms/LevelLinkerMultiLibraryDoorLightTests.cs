//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;
using Xunit.Abstractions;

using static SourceSharp.Tests.MapTools.Rooms.MultiLibraryHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The door light (PR 10) across a joint between rooms of two libraries
/// (PR 17): each library door-lit on its own, as its own pack would be, under
/// one sun; the light each room sends through the door reaches the other
/// library's room as it reaches a room of its own library.
/// </summary>
public sealed class LevelLinkerMultiLibraryDoorLightTests(LitRoomsFixture fixture, ITestOutputHelper output) : IClassFixture<LitRoomsFixture>
{
    /// <summary>
    /// base's hub jointed to caves' other, at two turns: the level is the
    /// same bytes as the one-library level of the same rooms (so every door
    /// term crossed the libraries' joint), and it agrees with vrad of the
    /// linked level itself within the door light's tolerances (9.8, as PR
    /// 10 measured them), where the base bake alone does not.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task LightCrossesADoorBetweenTwoLibrariesWithinTheTolerances(int rotation)
    {
        RoomLibrary baseRooms = await fixture.DoorLitAsync();
        RoomLibrary caves = await RoomLightHarness.CompileAsync(LitRoomsFixture.Library, options: LitRoomsFixture.Options, doorLight: true);
        LevelGrid level = Level($"base.hub@{rotation}, caves.other@{rotation}");
        (LinkedLevel linked, IReadOnlyList<string> warnings) = await LinkAsync(level, 1, baseRooms, caves);
        Assert.Equal(["library caves: 1 singleton(s) equal to library base's dropped (light_environment)."], warnings);
        Assert.Empty(linked.LightingWarnings);

        string row = $"hub@{rotation}, other@{rotation}";
        Assert.Equal(await BytesAsync(await fixture.DoorLinkedAsync(row)), await BytesAsync(linked.Bsp));

        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(linked);
        Assert.Equal(2, joints.Count);
        (LinkedLevel unlit, _) = await LinkAsync(level, 1, fixture.Unlit, await RoomLightHarness.CompileAsync(LitRoomsFixture.Library, light: false));
        BspData relit = await RoomLightHarness.RelightAsync(unlit.Bsp, LitRoomsFixture.Options);
        DoorLightCompare.Metric door = DoorLightCompare.Measure(linked.Bsp, relit, joints, allStyles: true);
        DoorLightCompare.Metric base0 = DoorLightCompare.Measure(await fixture.LinkedAsync(row), relit, joints, allStyles: true);
        output.WriteLine($"{level.Name} {rotation}: door light {door}; base only {base0}");
        Assert.True(door.NearP95 <= 0.08, $"near p95 {door.NearP95}");
        Assert.True(door.FarP95 <= 0.08, $"far p95 {door.FarP95}");
        Assert.InRange(door.Energy, 0.98, 1.02);
        Assert.True(door.Brighter <= door.Count * 3 / 100, $"{door.Brighter} of {door.Count} brighter");
        Assert.True(base0.NearP95 > door.NearP95 * 2, $"base near p95 {base0.NearP95}");
    }
}
