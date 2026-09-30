//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// Water through the CLI as a user runs it (the rooms design, 4.6):
/// <c>ssmap room</c> on a library with pools and water overlays,
/// <c>ssmap link</c> of a level of them from the pack alone,
/// <c>ssmap check</c> on the linked map, and <c>ssmap link --flatten</c>,
/// whose whole compile holds the same water.
/// </summary>
public sealed class RoomsWaterCommandsTests
{
    private const string GameInfoText = """
        "GameInfo"
        {
        	game	"Rooms"
        	FileSystem
        	{
        		SearchPaths
        		{
        			game	|gameinfo_path|.
        		}
        	}
        }
        """;

    /// <summary>
    /// A level of the pools' rooms, turned, links from the pack alone,
    /// passes <c>ssmap check</c> with no error, and agrees with its
    /// flattened level's compile at every point of the level, in its water
    /// records and fluids, and in its water overlays.
    /// </summary>
    [Fact]
    public async Task ALevelWithWaterLinksChecksAndMatchesItsFlatten()
    {
        InMemoryFileSystem fs = Game(RoomWaterHarness.ShowcaseLibrary());
        fs.AddText(Rooted("/levels/pools.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "hub@90, other", "other@180, hub@270"));

        using StringWriter room = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-nolight", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], room);
        Assert.True(exit == Program.ExitSuccess, room.ToString());

        using StringWriter link = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/pools.yaml", "-rooms", "/rooms.roompack", "-out", "/out/pools.bsp", "-no-nav"], link);
        Assert.True(exit == Program.ExitSuccess, link.ToString());
        BspData linked = await LoadAsync(fs, "/out/pools.bsp");

        using StringWriter check = new();
        await CheckCommand.RunAsync(fs, [Rooted("/out/pools.bsp")], check);
        Assert.Contains("pools.bsp: 0 error(s)", check.ToString(), StringComparison.Ordinal);

        using StringWriter flatten = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/pools.yaml", "--flatten", "-out", "/out/pools.vmf"], flatten);
        Assert.True(exit == Program.ExitSuccess, flatten.ToString());
        VmfDocument vmf = await VmfDocument.ParseAsync(fs.GetBytes(VPath.Create(Rooted("/out/pools.vmf")))!);
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        VbspResult whole = await RoomHarness.CompileAsync(vmf, await RoomWaterHarness.ContextAsync("pools", cooker: cooker));
        BspData flat = whole.Bsp!;

        Assert.Equal(LevelLinkerWaterTests.Points(flat, 2, 2), LevelLinkerWaterTests.Points(linked, 2, 2));
        Assert.Equal(LevelLinkerWaterTests.Records(flat), LevelLinkerWaterTests.Records(linked));
        Assert.Equal(LevelLinkerWaterTests.Fluids(flat), LevelLinkerWaterTests.Fluids(linked));
        Assert.Equal(RoomWaterHarness.ObservedWaterOverlays(flat), RoomWaterHarness.ObservedWaterOverlays(linked));
        Assert.Equal(4, RoomWaterHarness.WaterOverlays(linked).Length);
    }

    /// <summary>
    /// Water through a door through the CLI: <c>ssmap room</c> reads the
    /// library's <c>water_&lt;wall&gt;</c> keys and holds each room to them,
    /// <c>ssmap link</c> joins the two rooms' water through the door from
    /// the pack alone, <c>ssmap check</c> finds no error, and the flattened
    /// level's compile holds the same water at every point, in one record.
    /// </summary>
    [Fact]
    public async Task ALevelWithAWaterDoorLinksChecksAndMatchesItsFlatten()
    {
        InMemoryFileSystem fs = Game(RoomWaterHarness.SocketLibrary());
        fs.AddText(Rooted("/levels/door.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "other@90", "hub@90"));

        using StringWriter room = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-nolight", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], room);
        Assert.True(exit == Program.ExitSuccess, room.ToString());

        using StringWriter link = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/door.yaml", "-rooms", "/rooms.roompack", "-out", "/out/door.bsp", "-no-nav"], link);
        Assert.True(exit == Program.ExitSuccess, link.ToString());
        BspData linked = await LoadAsync(fs, "/out/door.bsp");

        using StringWriter check = new();
        await CheckCommand.RunAsync(fs, [Rooted("/out/door.bsp")], check);
        Assert.Contains("door.bsp: 0 error(s)", check.ToString(), StringComparison.Ordinal);

        using StringWriter flatten = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/door.yaml", "--flatten", "-out", "/out/door.vmf"], flatten);
        Assert.True(exit == Program.ExitSuccess, flatten.ToString());
        VmfDocument vmf = await VmfDocument.ParseAsync(fs.GetBytes(VPath.Create(Rooted("/out/door.vmf")))!);
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        BspData flat = (await RoomHarness.CompileAsync(vmf, await RoomWaterHarness.ContextAsync("door", cooker: cooker))).Bsp!;

        Assert.Equal(LevelLinkerWaterTests.Points(flat, 1, 2), LevelLinkerWaterTests.Points(linked, 1, 2));
        Assert.Equal(["64 16 unit/water_cheap"], LevelLinkerWaterTests.Records(linked));
        Assert.Equal(LevelLinkerWaterTests.Records(flat), LevelLinkerWaterTests.Records(linked));
        Assert.Equal("water at 64 of unit/water_cheap", RoomWaterHarness.At(linked, new Vec3(128, 256, 40)));
    }

    /// <summary>A game holding the harness's materials, the waters and the overlay's, and the library.</summary>
    private static InMemoryFileSystem Game(VmfDocument library)
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        foreach ((string path, string text) in new[]
        {
            (RoomHarness.Plain, "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n"),
            (RoomHarness.Trigger, "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n"),
        })
        {
            fs.AddText(Rooted($"/game/materials/{path}.vmt"), text);
        }

        foreach ((string path, byte[] bytes) in RoomWaterHarness.Files().Concat(RoomOverlayHarness.Files()))
        {
            fs.AddFile(Rooted($"/game/{path}"), bytes);
        }

        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        return fs;
    }

    // The CLI hands its commands full host paths (Program resolves them
    // first), so the fact does the same: on Windows a bare "/x" names no file.
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    private static async Task<BspData> LoadAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted(path)))!);
        return await BspFile.LoadAsync(stream);
    }
}
