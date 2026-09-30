//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// A room's navigation at a water socket (the rooms design, PR 14's water
/// sockets): the room is built alone, so what fills its open doorway is the
/// kit's assumption, and a water socket's doorway holds water up to the
/// declared level on both sides of the joint, as the link carves it and the
/// flattened level's compile fills it.
/// </summary>
public sealed class RoomNavWaterSocketTests
{
    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// The hub's east water socket and the other room's west one, jointed at
    /// every quarter turn: the stitched navigation is the flattened level's
    /// grid run for run, so the doorway's voxels below the level are water
    /// (flagged and costed as water) and the ones above it air, as they are
    /// in the level compiled whole.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task AJointedWaterDoorwayIsWaterInTheNavigation(int rotation)
    {
        VmfDocument library = RoomWaterHarness.SocketLibrary();
        RoomLibrary rooms = await RoomWaterHarness.CompileSocketsAsync(library);
        Dictionary<string, RoomNav> navs = Navs(library, rooms);
        LevelGrid level = rotation switch
        {
            0 => RoomPropHarness.Level("hub, other"),
            90 => RoomPropHarness.Level("other@90", "hub@90"),
            180 => RoomPropHarness.Level("other@180, hub@180"),
            _ => RoomPropHarness.Level("hub@270", "other@270"),
        };

        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(LevelNavLinker.Link(
            RoomPropHarness.Layout(rooms, level), level.Columns, level.Rows, (room, turn) => navs[room].Turned(turn), null, Guid.Empty)));
        BspData flat = await RoomWaterHarness.CompileFlatAsync(library, level);
        RoomNavHeightsTests.AssertSameAsFlattened(flat, level, nav);
    }

    /// <summary>
    /// The hub alone: with its east water socket open, the voxels of its
    /// doorway from the floor to the level are water and cost water's
    /// price, and the ones above are dry; a room built without its water
    /// sockets (as before this) has a dry doorway, and its own pool inside
    /// is the same either way. (A capped water socket keeps its plug, so
    /// its doorway is solid either way: the features sample's capped water
    /// sockets hold that against the flattened compile.)
    /// </summary>
    [Fact]
    public async Task AnOpenWaterSocketsDoorwayIsWaterAndACappedOneIsSolid()
    {
        VmfDocument library = RoomWaterHarness.SocketLibrary();
        RoomLibrary rooms = await RoomWaterHarness.CompileSocketsAsync(library);
        LibraryRoom hub = RoomLibraryVmf.Split(library).Single(r => r.Definition.Name == "hub");
        RoomObject compiled = rooms.Get("hub");
        RoomNav wet = RoomNavBuilder.Build(hub.Definition, compiled.Bsp, [], RoomRole.None, NavSettings.Default, waterSockets: hub.WaterSockets);
        RoomNav dry = RoomNavBuilder.Build(hub.Definition, compiled.Bsp, [], RoomRole.None, NavSettings.Default);

        // The east doorway's voxel column: x 240 to 256 (voxel 15), y 128 (voxel 8).
        Assert.Equal(["water 1..3", "dry 4..14"], Column(wet, 15, 8));
        Assert.Equal(["dry 1..14"], Column(dry, 15, 8));
        // Inside the room, beside the doorway, the room's own pool: the same either way.
        Assert.Equal(Column(dry, 12, 8), Column(wet, 12, 8));
    }

    /// <summary>Every room of the library built as <c>ssmap room</c> builds it: with its declared water sockets.</summary>
    private static Dictionary<string, RoomNav> Navs(VmfDocument library, RoomLibrary rooms)
    {
        Dictionary<string, RoomNav> navs = new(StringComparer.Ordinal);
        foreach (LibraryRoom room in RoomLibraryVmf.Split(library))
        {
            navs[room.Definition.Name] = RoomNavBuilder.Build(
                room.Definition, rooms.Get(room.Definition.Name).Bsp, [], RoomRole.None, NavSettings.Default, waterSockets: room.WaterSockets);
        }

        return navs;
    }

    /// <summary>A room's voxel column as runs of water and dry open voxels, bottom up.</summary>
    private static List<string> Column(RoomNav nav, int x, int y)
    {
        List<string> runs = [];
        foreach (NavRun run in nav.Columns.Column(x, y))
        {
            string kind = (run.Key.Flags & Nav3dLeafFlags.Water) != 0 ? "water" : "dry";
            runs.Add($"{kind} {run.ZLo}..{run.ZLo + run.Height - 1}");
        }

        return runs;
    }
}
