//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// Area portals through the CLI as a user runs it (the rooms design, 4.11):
/// <c>ssmap room</c> on a library whose split room has a portal,
/// <c>ssmap link</c> of a level through it and of a ring around it, and
/// <c>ssmap check</c> on the linked map.
/// </summary>
public sealed class RoomsAreaPortalCommandsTests
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
    /// A level through the split room links from the pack alone, carries
    /// its two areas and the portal between them, and passes
    /// <c>ssmap check</c> with no error (its one warning is every such
    /// level's: no cubemap sample); a ring around the portal links too,
    /// with the portal's warning printed before the headroom line and no
    /// portal listed.
    /// </summary>
    [Fact]
    public async Task ALevelWithAnAreaPortalLinksAndChecks()
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

        foreach ((string path, byte[] bytes) in RoomAreaPortalHarness.Files())
        {
            fs.AddFile(Rooted($"/game/{path}"), bytes);
        }

        fs.AddFile(Rooted("/game/maps/rooms.vmf"), RoomAreaPortalHarness.Library().ToBytes());
        fs.AddText(Rooted("/levels/line.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "hub, split, hub"));
        fs.AddText(Rooted("/levels/ring.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "hub, hub, hub", "hub, split, hub"));

        using StringWriter room = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], room);
        Assert.True(exit == Program.ExitSuccess, room.ToString());

        using StringWriter line = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/line.yaml", "-rooms", "/rooms.roompack", "-out", "/out/line.bsp", "-no-nav"], line);
        Assert.True(exit == Program.ExitSuccess, line.ToString());
        Assert.DoesNotContain("area portal", line.ToString(), StringComparison.Ordinal);
        BspData linked = await LoadAsync(fs, "/out/line.bsp");
        Assert.Equal(3, BspStructView.Count<DArea>(linked[BspLump.Areas]));
        Assert.Equal(3, BspStructView.Count<DAreaPortal>(linked[BspLump.AreaPortals]));

        using StringWriter check = new();
        // Warnings make check's exit 1 (the level has no cubemap sample, as
        // every level of these rooms); what matters is that no error fired.
        await CheckCommand.RunAsync(fs, [Rooted("/out/line.bsp")], check);
        Assert.Contains("line.bsp: 0 error(s)", check.ToString(), StringComparison.Ordinal);

        using StringWriter ring = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/ring.yaml", "-rooms", "/rooms.roompack", "-out", "/out/ring.bsp", "-no-nav"], ring);
        Assert.True(exit == Program.ExitSuccess, ring.ToString());
        string[] lines = ring.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        int warning = Array.IndexOf(lines,
            "ssmap link: warning: room split at cell (1, 0): area portal 1 has one area on both sides once the level joins the rooms around it;"
            + " the level keeps its entity but lists no portal for it.");
        Assert.True(warning >= 0, ring.ToString());
        Assert.StartsWith("ssmap link: map entities", lines[warning + 1], StringComparison.Ordinal);
        Assert.Equal(1, BspStructView.Count<DAreaPortal>((await LoadAsync(fs, "/out/ring.bsp"))[BspLump.AreaPortals]));
    }

    /// <summary>
    /// A library with a skybox room through the CLI: <c>ssmap room</c> packs
    /// the skybox with the rooms and names it in the pack; <c>ssmap link</c>,
    /// reading the pack alone, places it below the level with its camera;
    /// <c>ssmap check</c> finds no error; <c>ssmap layout -entity-budget</c>
    /// counts its camera once per level with the worldspawn.
    /// </summary>
    [Fact]
    public async Task ALevelWithASkyboxLinksAndChecks()
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        foreach ((string path, string text) in new[]
        {
            (RoomHarness.Plain, "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n"),
            (RoomHarness.Trigger, "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n"),
            (RoomLightHarness.Sky, "\"UnlitGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileSky\" \"1\"\n}\n"),
        })
        {
            fs.AddText(Rooted($"/game/materials/{path}.vmt"), text);
        }

        fs.AddFile(Rooted("/game/maps/rooms.vmf"), RoomSkyboxHarness.Library().ToBytes());
        fs.AddText(Rooted("/levels/pair.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "hub, other"));

        using StringWriter room = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "-nolight", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], room);
        Assert.True(exit == Program.ExitSuccess, room.ToString());
        Assert.Contains("ssmap room: compiled sky (", room.ToString(), StringComparison.Ordinal);

        using StringWriter link = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/pair.yaml", "-rooms", "/rooms.roompack", "-out", "/out/pair.bsp", "-no-nav"], link);
        Assert.True(exit == Program.ExitSuccess, link.ToString());
        BspData linked = await LoadAsync(fs, "/out/pair.bsp");
        Assert.Contains(EntityLump.Parse(linked[BspLump.Entities]), e => e.ClassName == "sky_camera" && e.Get("origin") == "128 128 -128");
        Assert.Equal(3, BspStructView.Count<DArea>(linked[BspLump.Areas]));

        using StringWriter check = new();
        await CheckCommand.RunAsync(fs, [Rooted("/out/pair.bsp")], check);
        Assert.Contains("pair.bsp: 0 error(s)", check.ToString(), StringComparison.Ordinal);

        // Two rooms of one entity each (their player starts), the worldspawn
        // and the skybox's camera: four edicts at least, so three is refused.
        using StringWriter over = new();
        Assert.Equal(
            RoomCommands.ExitFailed,
            await RoomCommands.RunLayoutAsync(
                fs, ["/game/maps/rooms.vmf", "-rows", "1", "-columns", "2", "-seed", "1", "-rooms", "/rooms.roompack", "-entity-budget", "3"], over));
        Assert.Contains("its 2 room(s) bring at least 4, the worldspawn and the library's own entities included.", over.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The rooms design's D29 through the CLI: a level of two libraries
    /// whose first has no skybox takes the second's. <c>ssmap link</c>
    /// loads that library's skybox room from its pack (it loaded only the
    /// first library's before) and places it below the level with its
    /// camera, without a line; the flatten writes the same camera.
    /// </summary>
    [Fact]
    public async Task ALaterLibrarysSkyboxIsTheLevelsThroughTheCli()
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        foreach ((string path, string text) in new[]
        {
            (RoomHarness.Plain, "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n"),
            (RoomHarness.Trigger, "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n"),
            (RoomLightHarness.Sky, "\"UnlitGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileSky\" \"1\"\n}\n"),
        })
        {
            fs.AddText(Rooted($"/game/materials/{path}.vmt"), text);
        }

        fs.AddFile(Rooted("/game/maps/plain.vmf"), RoomPropHarness.Library().ToBytes());
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), RoomSkyboxHarness.Library().ToBytes());
        fs.AddText(
            Rooted("/levels/two.yaml"),
            "libraries:\n  base: ../game/maps/plain.vmf\n  sky: ../game/maps/rooms.vmf\nrows: 1\ncolumns: 2\ngrid:\n  - [base.hub, sky.other]\n");
        foreach (string library in new[] { "plain", "rooms" })
        {
            using StringWriter room = new();
            int packed = await RoomCommands.RunRoomAsync(
                fs, [], ["-cooker", "none", "-nolight", $"/game/maps/{library}.vmf", "-out", $"/game/maps/{library}.roompack"], room);
            Assert.True(packed == Program.ExitSuccess, room.ToString());
        }

        using StringWriter link = new();
        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/two.yaml", "-out", "/out/two.bsp", "-no-nav"], link);
        Assert.True(exit == Program.ExitSuccess, link.ToString());
        Assert.DoesNotContain("warning", link.ToString(), StringComparison.Ordinal);
        BspData linked = await LoadAsync(fs, "/out/two.bsp");
        Assert.Single(EntityLump.Parse(linked[BspLump.Entities]), e => e.ClassName == "sky_camera" && e.Get("origin") == "128 128 -128");

        using StringWriter flatten = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/two.yaml", "--flatten", "-out", "/out/two.vmf"], flatten);
        Assert.True(exit == Program.ExitSuccess, flatten.ToString());
        Assert.DoesNotContain("warning", flatten.ToString(), StringComparison.Ordinal);
        VmfDocument flat = await VmfDocument.ParseAsync(fs.GetBytes(VPath.Create(Rooted("/out/two.vmf")))!);
        VmfChunk camera = Assert.Single(flat.GetChunks("entity"), e => e.GetValue("classname") == "sky_camera");
        Assert.Equal("128 128 -128", camera.GetValue("origin"));
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
