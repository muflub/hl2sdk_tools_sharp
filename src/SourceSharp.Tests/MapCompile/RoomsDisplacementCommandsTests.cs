//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// Displacements through the CLI as a user runs it (the rooms design, 4.5):
/// <c>ssmap room</c> on a library with patches of terrain, lit as it lights
/// by default, <c>ssmap link</c> of a level of them from the pack alone,
/// <c>ssmap check</c> on the linked map, and <c>ssmap link --flatten</c>,
/// whose whole compile holds the same displacements.
/// </summary>
public sealed class RoomsDisplacementCommandsTests
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
    /// A level of the patched rooms, turned, links from the pack alone,
    /// passes <c>ssmap check</c> with no error, carries every placement's
    /// displacements, and agrees with its flattened level's compile on them
    /// (<see cref="RoomDisplacementHarness.Observed"/>, the start positions,
    /// the surfaces' vertices).
    /// </summary>
    [Fact]
    public async Task ALevelWithDisplacementsLinksChecksAndMatchesItsFlatten()
    {
        InMemoryFileSystem fs = Game(RoomDisplacementHarness.Library(RoomDisplacementHarness.Patches));
        fs.AddText(Rooted("/levels/terrain.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "hub@90, other", "other@180, hub@270"));

        using StringWriter room = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["/game/maps/rooms.vmf", "-out", "/rooms.roompack"], room);
        Assert.True(exit == Program.ExitSuccess, room.ToString());

        using StringWriter link = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/terrain.yaml", "-rooms", "/rooms.roompack", "-out", "/out/terrain.bsp", "-no-nav"], link);
        Assert.True(exit == Program.ExitSuccess, link.ToString());
        BspData linked = await LoadAsync(fs, "/out/terrain.bsp");
        Assert.Equal(6, RoomDisplacementHarness.Infos(linked).Length);

        using StringWriter check = new();
        await CheckCommand.RunAsync(fs, [Rooted("/out/terrain.bsp")], check);
        Assert.Contains("terrain.bsp: 0 error(s)", check.ToString(), StringComparison.Ordinal);

        using StringWriter flatten = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/terrain.yaml", "--flatten", "-out", "/out/terrain.vmf"], flatten);
        Assert.True(exit == Program.ExitSuccess, flatten.ToString());
        VmfDocument vmf = await VmfDocument.ParseAsync(fs.GetBytes(VPath.Create(Rooted("/out/terrain.vmf")))!);
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        BspData flat = (await RoomHarness.CompileAsync(vmf, await RoomDisplacementHarness.ContextAsync(cooker, "terrain"))).Bsp!;

        Assert.Equal(RoomDisplacementHarness.Observed(flat), RoomDisplacementHarness.Observed(linked));
        Assert.Equal(RoomDisplacementHarness.Infos(flat).Select(d => d.StartPosition), RoomDisplacementHarness.Infos(linked).Select(d => d.StartPosition));
        Assert.True(RoomDisplacementHarness.VertexGap(linked, flat) < 1e-3f);
    }

    /// <summary>
    /// A library with a displacement reaching into a doorway is refused by
    /// <c>ssmap room</c> with the rooms design's text (15.4), before any
    /// room compiles.
    /// </summary>
    [Fact]
    public async Task ADisplacementInADoorwayIsRefusedByTheRoomCommand()
    {
        InMemoryFileSystem fs = Game(RoomDisplacementHarness.Library(
            [(0, RoomDisplacementHarness.Patch(RoomDisplacementHarness.PatchBrush, new Box(new Vec3(176, 96, 16), new Vec3(250, 160, 24)), offsets: false))]));

        using StringWriter room = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-nolight", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], room);
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains(
            "room hub: the displacement on brush side 48000 has an edge on socket \"east\"'s plug box; displacements may not meet at a joint.",
            room.ToString(),
            StringComparison.Ordinal);
        Assert.Null(fs.GetBytes(VPath.Create(Rooted("/rooms.roompack"))));
    }

    /// <summary>A game holding the harness's materials and the library.</summary>
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
