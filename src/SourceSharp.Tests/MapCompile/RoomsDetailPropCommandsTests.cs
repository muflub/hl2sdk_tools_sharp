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
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// Detail props through the CLI as a user runs it (the rooms design, 4.4):
/// <c>ssmap room</c> on a library with grass, lit as it lights by default,
/// <c>ssmap link</c> of a level of them from the pack alone, <c>ssmap
/// check</c> on the linked map, and <c>ssmap link --flatten</c>, whose whole
/// compile grows the same kinds of props over the same surfaces.
/// </summary>
public sealed class RoomsDetailPropCommandsTests
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
    /// A level of the grassed rooms, turned, links from the pack alone,
    /// passes <c>ssmap check</c> with no error, carries every placement's
    /// detail props lit (a style lump beside the props, and no prop left at
    /// vbsp's white), and its flattened level's compile holds the same
    /// dictionary and, per room, the same number of props within the draw's
    /// noise.
    /// </summary>
    [Fact]
    public async Task ALevelWithDetailPropsLinksChecksAndMatchesItsFlatten()
    {
        InMemoryFileSystem fs = Game(RoomDetailHarness.Library());
        fs.AddText(Rooted("/levels/grass.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "hub@90, other", "other@180, hub@270"));

        using StringWriter room = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["/game/maps/rooms.vmf", "-out", "/rooms.roompack"], room);
        Assert.True(exit == Program.ExitSuccess, room.ToString());

        using StringWriter link = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/grass.yaml", "-rooms", "/rooms.roompack", "-out", "/out/grass.bsp", "-no-nav"], link);
        Assert.True(exit == Program.ExitSuccess, link.ToString());
        BspData linked = await LoadAsync(fs, "/out/grass.bsp");
        DetailPropLump lump = RoomDetailHarness.Lump(linked);
        Assert.Equal(2 * (228 + 225), lump.Props.Count);
        Assert.Contains("dplt", RoomDetailHarness.GameLumpIds(linked));
        Assert.DoesNotContain(lump.Props, p => p.Lighting.Equals(new ColorRgbExp32 { R = 255, G = 255, B = 255, Exponent = 0 }));

        using StringWriter check = new();
        await CheckCommand.RunAsync(fs, [Rooted("/out/grass.bsp")], check);
        Assert.Contains("grass.bsp: 0 error(s)", check.ToString(), StringComparison.Ordinal);

        using StringWriter flatten = new();
        exit = await RoomCommands.RunLinkAsync(fs, ["/levels/grass.yaml", "--flatten", "-out", "/out/grass.vmf"], flatten);
        Assert.True(exit == Program.ExitSuccess, flatten.ToString());
        VmfDocument vmf = await VmfDocument.ParseAsync(fs.GetBytes(VPath.Create(Rooted("/out/grass.vmf")))!);
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        DetailPropLump flat = RoomDetailHarness.Lump((await RoomHarness.CompileAsync(vmf, await RoomDetailHarness.ContextAsync("grass", 1, cooker))).Bsp!);

        Assert.Equal(lump.ModelNames.Order(StringComparer.Ordinal), flat.ModelNames.Order(StringComparer.Ordinal));
        Assert.Equal(lump.Sprites.Select(RoomDetailHarness.Sprite).Order(StringComparer.Ordinal), flat.Sprites.Select(RoomDetailHarness.Sprite).Order(StringComparer.Ordinal));
        foreach ((int x, int y) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
        {
            int a = PropsIn(lump, x, y), b = PropsIn(flat, x, y);
            Assert.True(Math.Abs(a - b) <= a / 10, $"cell ({x}, {y}): {a} linked, {b} flattened");
        }
    }

    /// <summary>
    /// A library with a detail prop entity carrying <c>room_needs</c> is
    /// refused by <c>ssmap room</c> with its text (the room fails to compile,
    /// as a room refused by any other compile-time rule does).
    /// </summary>
    [Fact]
    public async Task ADetailEntityWithRoomNeedsIsRefusedByTheRoomCommand()
    {
        VmfChunk entity = RoomDetailHarness.DetailProp(9300, new Vec3(120, 120, 16));
        entity.AddKey("room_needs", "joined_east");
        InMemoryFileSystem fs = Game(RoomDetailHarness.Library(entities: [(0, entity)]));

        using StringWriter room = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-nolight", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], room);
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains(
            "room hub: entity 9300 (prop_detail) has room_needs, but a detail prop is built into its room's compile and cannot be dropped.",
            room.ToString(),
            StringComparison.Ordinal);
    }

    private static int PropsIn(DetailPropLump lump, int x, int y) =>
        lump.Props.Count(p => (int)Math.Floor(p.Origin.X / RoomHarness.Cell) == x && (int)Math.Floor(p.Origin.Y / RoomHarness.Cell) == y);

    /// <summary>A game holding the harness's materials, the detail props' files and the library.</summary>
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

        foreach ((string path, byte[] bytes) in RoomDetailHarness.Files())
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
