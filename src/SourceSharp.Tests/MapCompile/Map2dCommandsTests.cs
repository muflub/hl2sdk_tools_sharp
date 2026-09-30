//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// The level map through the commands (the rooms design, 18.3):
/// <c>ssmap link</c> writes <c>&lt;map&gt;.map2d</c> beside the map, bound
/// to it, and the SVG preview when asked, or none with <c>-no-map2d</c> or
/// from a pack without the map (saying so); <c>ssmap room</c> warns of a
/// room with no floor; <c>ssmap map2d</c> makes the file from any compiled
/// map, with or without the level file, and refuses what it cannot read.
/// </summary>
public sealed class Map2dCommandsTests
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

    private const string NoDraw = "unit/nodraw";

    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    private static VPath At(string path) => VPath.Create(Rooted(path));

    private static string Shown(string path) => HostPaths.Display(At(path));

    /// <summary>
    /// A game and a library of two walkable rooms, the first labelled and
    /// holding a marker, and a third whose shell is all nodraw (no floor to
    /// stand on); a level of the first two.
    /// </summary>
    private static InMemoryFileSystem Game()
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Plain}.vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{NoDraw}.vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileNoDraw\" \"1\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Trigger}.vmt"),
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        VmfDocument library = RoomHarness.LibraryVmf(
            RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX),
            RoomHarness.WalkableRoom("end", RoomFacing.NegativeX),
            RoomHarness.WalkableRoom("void", RoomFacing.NegativeX));
        float step = RoomHarness.Cell + RoomHarness.LibraryGap;
        foreach (VmfChunk solid in library.GetChunk(MapFileLoader.WorldChunk)!.GetChunks(MapFileLoader.SolidChunk))
        {
            if (VmfPlacement.Bounds(solid).Mins.X >= 2 * step - 1)
            {
                foreach (VmfChunk side in solid.GetChunks(MapFileLoader.SideChunk))
                {
                    if (side.GetValue("material") == RoomHarness.Plain)
                    {
                        side.Keys.First(k => k.Name == "material").Value = NoDraw;
                    }
                }
            }
        }

        library.GetChunks(MapFileLoader.EntityChunk).First(e => e.GetValue(RoomLibraryVmf.NameKey) == "hall").AddKey(LevelMap.LabelKey, "Hall");
        library.Chunks.Add(RoomPoiTests.PoiEntity(new Vec3(40, 50, 16), (LevelMap.MarkerKey, "chest"), (LevelMap.LabelKey, "Loot")));
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        fs.AddText(Rooted("/levels/level.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "hall, end"));
        return fs;
    }

    private static async Task<(int Exit, string Log)> RoomAsync(InMemoryFileSystem fs)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "-nolight", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);
        return (exit, output.ToString());
    }

    private static async Task<(int Exit, string Log)> LinkAsync(IFileSystem fs, params string[] more)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-no-nav", "-out", "/out/level.bsp", .. more], output);
        return (exit, output.ToString());
    }

    private static async Task<(int Exit, string Log)> Map2dAsync(IFileSystem fs, params string[] args)
    {
        using StringWriter output = new();
        int exit = await Map2dCommand.RunAsync(fs, args, output);
        return (exit, output.ToString());
    }

    private static string Line(string verb, string path, Map2dLevel map, int bytes) =>
        $"{verb}: wrote {Shown(path)} ({map.Rooms.Length} rooms, {map.Rings.Length} rings, {map.Doors.Length} doors, {map.Markers.Length} markers, {bytes} bytes)";

    /// <summary>
    /// <c>ssmap room</c> warns of a room with no walkable floor, which a
    /// player cannot stand in, and packs it with an empty map section.
    /// </summary>
    [Fact]
    public async Task RoomWarnsOfARoomWithNoFloor()
    {
        InMemoryFileSystem fs = Game();
        (int exit, string log) = await RoomAsync(fs);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Contains(
            "ssmap room: warning: room \"void\" has no walkable floor (no drawn face whose normal points up at least 0.7);"
            + " a player cannot stand in it, and the level map shows it empty.",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain("room \"hall\" has no walkable floor", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ssmap link</c> writes the level map beside the map, bound to it by
    /// its checksum, and reports it; the placements carry their labels and
    /// the marker its room's turn.
    /// </summary>
    [Fact]
    public async Task LinkWritesTheMapBesideTheBspBoundToIt()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        (int exit, string log) = await LinkAsync(fs);
        Assert.True(exit == Program.ExitSuccess, log);

        byte[] file = fs.GetBytes(At("/out/level.map2d"))!;
        Map2dLevel map = Map2dReader.Read(file, BspMapChecksum.Compute(fs.GetBytes(At("/out/level.bsp"))!));
        Assert.Contains(Line("ssmap link", "/out/level.map2d", map, file.Length) + Environment.NewLine, log, StringComparison.Ordinal);
        Assert.Equal(["Hall", string.Empty], map.Rooms.Select(r => r.Label));
        Assert.Equal([true, true], map.Doors.Select(d => d.Open));
        Assert.Equal(new Map2dMarker("chest", "Loot", 0, 40, 50, 16, 0), Assert.Single(map.Markers));
        Assert.Null(fs.GetBytes(At("/out/level.svg")));
    }

    /// <summary>
    /// The map of the linked map, cut by the level file, is the link's own
    /// file byte for byte: the link copies the rooms' faces, and the face
    /// rule reads them back to the same polygons.
    /// </summary>
    [Fact]
    public async Task Map2dOfTheLinkedMapIsTheLinksFile()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs)).Exit);
        byte[] linked = fs.GetBytes(At("/out/level.map2d"))!;

        (int exit, string log) = await Map2dAsync(fs, "/out/level.bsp", "-level", "/levels/level.yaml", "-out", "/out/again.map2d");
        Assert.True(exit == Program.ExitSuccess, log);
        byte[] again = fs.GetBytes(At("/out/again.map2d"))!;
        Assert.Equal(linked, again);
        Assert.Equal(Line("ssmap map2d", "/out/again.map2d", Map2dReader.Read(again), again.Length) + Environment.NewLine, log);
    }

    /// <summary><c>-map2d-svg</c> writes the preview beside the map too; <c>-no-map2d</c> writes neither and says nothing of it.</summary>
    [Fact]
    public async Task TheSvgIsWrittenWhenAskedAndNothingWithNoMap2d()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        (int exit, string log) = await LinkAsync(fs, "-map2d-svg");
        Assert.True(exit == Program.ExitSuccess, log);
        byte[] svg = fs.GetBytes(At("/out/level.svg"))!;
        Map2dLevel map = Map2dReader.Read(fs.GetBytes(At("/out/level.map2d"))!);
        Assert.Equal(Map2dSvg.Write(map), Encoding.UTF8.GetString(svg));
        Assert.Contains($"ssmap link: wrote {Shown("/out/level.svg")} ({svg.Length} bytes)", log, StringComparison.Ordinal);

        InMemoryFileSystem bare = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(bare)).Exit);
        (exit, log) = await LinkAsync(bare, "-no-map2d");
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Null(bare.GetBytes(At("/out/level.map2d")));
        Assert.Null(bare.GetBytes(At("/out/level.svg")));
        Assert.DoesNotContain(".map2d", log, StringComparison.Ordinal);
    }

    /// <summary><c>-no-map2d</c> with <c>-map2d-svg</c>, or either with <c>--flatten</c>, is a usage error.</summary>
    [Theory]
    [InlineData("-no-map2d", "-map2d-svg")]
    [InlineData("--flatten", "-no-map2d")]
    [InlineData("--flatten", "-map2d-svg")]
    public async Task ContradictoryMapSwitchesAreAUsageError(string first, string second)
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", first, second], output));
        Assert.StartsWith("usage: ssmap link", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A pack whose rooms have no map section (packed before the map) links
    /// the map as always, without a <c>.map2d</c>, and says so once, naming
    /// the rooms.
    /// </summary>
    [Fact]
    public async Task APackWithoutTheMapLinksWithoutOneAndSaysSo()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        using (MemoryStream stream = new(fs.GetBytes(At("/rooms.roompack"))!))
        {
            RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
            List<RoomPackItem> items = [];
            foreach (RoomObject room in await RoomPack.LoadRoomsAsync(stream, index, ["hall", "end"]))
            {
                items.Add(await RoomPackItem.CreateAsync(room with { MapView = null }));
            }

            using MemoryStream rewritten = new();
            await RoomPack.SaveAsync(items, rewritten);
            fs.AddFile(Rooted("/rooms.roompack"), rewritten.ToArray());
        }

        (int exit, string log) = await LinkAsync(fs);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.NotNull(fs.GetBytes(At("/out/level.bsp")));
        Assert.Null(fs.GetBytes(At("/out/level.map2d")));
        string warning = "ssmap link: warning: the room pack holds no level map for \"end\", \"hall\";"
            + " the level is linked without a .map2d (compile the library with a build that writes the map)";
        Assert.Single(log.Split(Environment.NewLine), l => l == warning);
    }

    /// <summary>
    /// <c>ssmap map2d</c> without a level file: the map's floors, unioned whole, beside
    /// it (or at <c>-out</c>), bound to it, and the preview with <c>-svg</c>.
    /// </summary>
    [Fact]
    public async Task Map2dWithoutALevelWritesTheWholeFloor()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs, "-no-map2d")).Exit);

        (int exit, string log) = await Map2dAsync(fs, "/out/level.bsp", "-svg");
        Assert.True(exit == Program.ExitSuccess, log);
        byte[] file = fs.GetBytes(At("/out/level.map2d"))!;
        Map2dLevel map = Map2dReader.Read(file, BspMapChecksum.Compute(fs.GetBytes(At("/out/level.bsp"))!));
        Assert.Empty(map.Rooms);
        Assert.All(map.Rings, r => Assert.Equal(Map2dFormat.NoPlacement, r.Placement));
        // The two rooms' floors, apart: the linked map has no floor in the
        // joined doorway (the plugs' floor was the doors'), and without the
        // level file nothing says where one room ends.
        Assert.Equal(
            [
                new Map2dRing(-1, 16, 16, false, [new(16, 16), new(240, 16), new(240, 240), new(16, 240)]),
                new Map2dRing(-1, 16, 16, false, [new(272, 16), new(496, 16), new(496, 240), new(272, 240)]),
            ],
            map.Rings.AsEnumerable());
        Assert.Equal(
            Line("ssmap map2d", "/out/level.map2d", map, file.Length) + Environment.NewLine
            + $"ssmap map2d: wrote {Shown("/out/level.svg")} ({fs.GetBytes(At("/out/level.svg"))!.Length} bytes)" + Environment.NewLine,
            log);
    }

    /// <summary>
    /// <c>ssmap map2d</c> refuses what it cannot read: no map, a file that is
    /// not a map, a level whose library is missing, and a bad command line.
    /// </summary>
    [Fact]
    public async Task Map2dRefusesWhatItCannotRead()
    {
        InMemoryFileSystem fs = Game();
        (int exit, string log) = await Map2dAsync(fs, "/out/none.bsp");
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.StartsWith($"ssmap map2d: {Shown("/out/none.bsp")}: ", log, StringComparison.Ordinal);

        fs.AddText(Rooted("/out/text.bsp"), "not a map");
        (exit, log) = await Map2dAsync(fs, "/out/text.bsp");
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Equal($"ssmap map2d: {Shown("/out/text.bsp")}: not a BSP: the header is missing or cut short" + Environment.NewLine, log);

        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs, "-no-map2d")).Exit);
        fs.AddText(Rooted("/levels/lost.yaml"), RoomHarness.LevelText("../nowhere/rooms.vmf", "hall, end"));
        (exit, log) = await Map2dAsync(fs, "/out/level.bsp", "-level", "/levels/lost.yaml");
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.StartsWith($"ssmap map2d: {Path.GetFullPath("/levels/lost.yaml")}: ", log, StringComparison.Ordinal);

        foreach (string[] args in new[] { Array.Empty<string>(), ["/out/level.bsp", "-bogus"], ["/out/level.bsp", "/out/other.bsp"], ["-level"] })
        {
            (exit, log) = await Map2dAsync(fs, args);
            Assert.Equal(Program.ExitUsage, exit);
            Assert.Equal("usage: ssmap map2d <map.bsp> [-level <level.yaml>] [-out <file.map2d>] [-svg]" + Environment.NewLine, log);
        }
    }
}
