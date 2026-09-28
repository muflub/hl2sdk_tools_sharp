//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap room</c> and <c>ssmap link</c> end to end on an in-memory disk: a
/// game with the harness materials, room VMFs with their definition
/// sidecars, the <c>.room</c> files the first verb writes and the map the
/// second writes from them.
/// </summary>
public sealed class RoomCommandsTests
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
    /// Where the commands look for a rooted path they are given: they resolve
    /// against the host (<c>/game</c> is <c>D:\game</c> on Windows), so the
    /// in-memory files go where that lands.
    /// </summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    // ---- the whole pipeline -------------------------------------------------

    /// <summary>
    /// Two rooms compiled by <c>ssmap room</c> with its default cooker link:
    /// the rooms carry world collision, the link carries it too, and the map
    /// it writes passes the loader validation. This is the pipeline's
    /// default road, which the linker used to refuse at the collision lump.
    /// </summary>
    [Fact]
    public async Task DefaultCookedRoomsLinkIntoAMapWithWorldCollision()
    {
        InMemoryFileSystem fs = Game();
        AddRoom(fs, RoomHarness.Hub());
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["/game/maps/hub.vmf", "-out", "/rooms"], output));
        Assert.Contains("collision: managed", output.ToString(), StringComparison.Ordinal);

        AddPairLayout(fs);
        int exit = await RoomCommands.RunLinkAsync(fs, ["/rooms/level.json", "-out", "/out/level.bsp"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());

        BspData map = await LoadMapAsync(fs, "/out/level.bsp");
        Assert.NotEqual(0, map[BspLump.PhysCollide].Length);
        ValidationReport report = await BspValidator.CheckAsync(map, CancellationToken.None);
        Assert.Equal(0, report.ErrorCount);
    }

    /// <summary>Rooms compiled with <c>-cooker none</c> still link, into a map without world collision.</summary>
    [Fact]
    public async Task UncookedRoomsStillLink()
    {
        InMemoryFileSystem fs = Game();
        AddRoom(fs, RoomHarness.Hub());
        using StringWriter output = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/hub.vmf", "-out", "/rooms"], output));

        AddPairLayout(fs);
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLinkAsync(fs, ["/rooms/level.json", "-out", "/out/level.bsp"], output));
        Assert.Equal(0, (await LoadMapAsync(fs, "/out/level.bsp"))[BspLump.PhysCollide].Length);
    }

    // ---- what they say they wrote -------------------------------------------

    /// <summary>
    /// <c>ssmap room</c> names the file it wrote as the host spells it, where
    /// it used to print the path with its root cut off
    /// (<c>tmp/x/rooms/hub.room</c>).
    /// </summary>
    [Fact]
    public async Task ARoomNamesTheFileItWroteByItsHostPath()
    {
        InMemoryFileSystem fs = Game();
        AddRoom(fs, RoomHarness.Hub());
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/hub.vmf", "-out", "/rooms"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains(
            $"ssmap room: wrote {Path.GetFullPath("/rooms/hub.room")} (",
            output.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary><c>ssmap link</c> names the map it wrote as the host spells it.</summary>
    [Fact]
    public async Task ALinkNamesTheMapItWroteByItsHostPath()
    {
        InMemoryFileSystem fs = Game();
        AddRoom(fs, RoomHarness.Hub());
        using StringWriter output = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/hub.vmf", "-out", "/rooms"], output));
        AddPairLayout(fs);

        int exit = await RoomCommands.RunLinkAsync(fs, ["/rooms/level.json", "-out", "/out/level.bsp"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains(
            $"ssmap link: wrote {Path.GetFullPath("/out/level.bsp")} (2 rooms, ",
            output.ToString(),
            StringComparison.Ordinal);
    }

    // ---- ssmap room: its inputs ---------------------------------------------

    /// <summary>
    /// A room name that is a path is refused before anything is compiled or
    /// written: <c>../escape</c> would have written <c>escape.room</c> beside
    /// <c>-out</c> instead of inside it.
    /// </summary>
    [Fact]
    public async Task ARoomNameThatLeavesTheOutputDirectoryIsRefused()
    {
        InMemoryFileSystem fs = Game();
        AddRoom(fs, RoomHarness.Room("../escape", RoomFacing.PositiveX), "escape");
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/escape.vmf", "-out", "/rooms"], output);

        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("cannot name a file in -out: it contains a path separator", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fs.Paths, p => p.Value.EndsWith(".room", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>-out</c> resolves against the current directory like every other
    /// path on the line: a relative <c>-out rooms</c> writes beside where the
    /// command ran, where it used to write to a root-level <c>/rooms</c> (and on
    /// Windows a rooted <c>-out</c> lost its drive the same way).
    /// </summary>
    [Fact]
    public async Task ARelativeOutDirectoryResolvesAgainstTheCurrentDirectory()
    {
        InMemoryFileSystem fs = Game();
        AddRoom(fs, RoomHarness.Hub());
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/hub.vmf", "-out", "relative-rooms"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.NotNull(fs.GetBytes(VPath.Create(Rooted("relative-rooms/hub.room"))));
    }

    /// <summary>A room name is one path segment on every host.</summary>
    [Theory]
    [InlineData("hub", null)]
    [InlineData("salle-é", null)]
    [InlineData("a/b", "it contains a path separator")]
    [InlineData("a\\b", "it contains a path separator")]
    [InlineData("..", "it is a relative directory name")]
    [InlineData(".", "it is a relative directory name")]
    [InlineData("c:x", "it contains a drive or stream colon")]
    [InlineData("a\tb", "it contains a control character")]
    public void ARoomNameIsOnePathSegment(string name, string? problem) =>
        Assert.Equal(problem, RoomCommands.RoomFileNameProblem(name));

    /// <summary>
    /// The definition is read as UTF-8, as the layout is: a room named
    /// <c>salle-é</c> is written as <c>salle-é.room</c> and reads back under
    /// that name, where Latin-1 decoding made it <c>salle-Ã©</c>.
    /// </summary>
    [Fact]
    public async Task ARoomDefinitionIsReadAsUtf8()
    {
        InMemoryFileSystem fs = Game();
        AddRoom(fs, RoomHarness.Room("salle-é", RoomFacing.PositiveX), "salle");

        // The JSON writer escapes non-ASCII (é), which any decoding
        // reads alike; an author's editor writes the character as UTF-8.
        string json = RoomDefinitionJson.Write(RoomHarness.Room("salle-é", RoomFacing.PositiveX))
            .Replace("\\u00E9", "é", StringComparison.Ordinal);
        Assert.Contains("salle-é", json, StringComparison.Ordinal);
        fs.AddFile(Rooted("/game/maps/salle.vmf.roomdef.json"), Encoding.UTF8.GetBytes(json));
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/salle.vmf", "-out", "/rooms"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        byte[]? bytes = fs.GetBytes(VPath.Create(Rooted("/rooms/salle-é.room")));
        Assert.NotNull(bytes);
        using MemoryStream stream = new(bytes!);
        Assert.Equal("salle-é", (await RoomObjectStore.LoadAsync(stream)).Definition.Name);
    }

    /// <summary>
    /// A definition that parses but does not validate (two sockets on one
    /// face) is reported and exits failed, instead of escaping the command
    /// as an ArgumentException.
    /// </summary>
    [Fact]
    public async Task AnInvalidDefinitionIsReportedNotThrown()
    {
        InMemoryFileSystem fs = Game();
        RoomDefinition twice = new("twice", RoomHarness.Cell, RoomHarness.Kit,
            [new RoomSocket(RoomFacing.PositiveX, "a"), new RoomSocket(RoomFacing.PositiveX, "b")]);
        AddRoom(fs, twice, "twice", model: RoomHarness.Room("twice", RoomFacing.PositiveX));
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/twice.vmf"], output);

        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("cannot read the room definition", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Two sockets on the same face", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A missing definition sidecar is reported, not thrown.</summary>
    [Fact]
    public async Task AMissingDefinitionIsReported()
    {
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/bare.vmf"), RoomHarness.BuildRoomModel(RoomHarness.Hub()).ToBytes());
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomAsync(fs, [], ["/game/maps/bare.vmf"], output));
        Assert.Contains("cannot read the room definition", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>No map on the line is a usage error.</summary>
    [Fact]
    public async Task ARoomWithoutAMapIsAUsageError()
    {
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunRoomAsync(new InMemoryFileSystem(), [], [], output));
        Assert.Contains("usage: ssmap room", output.ToString(), StringComparison.Ordinal);
    }

    // ---- ssmap link: its inputs ---------------------------------------------

    /// <summary>Anything but one layout on the line is a usage error.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ALinkNeedsOneLayout(int layouts)
    {
        string[] args = [.. Enumerable.Range(0, layouts).Select(i => $"l{i}.json")];
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLinkAsync(new InMemoryFileSystem(), args, output));
        Assert.Contains("usage: ssmap link", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>An unreadable layout, an empty room directory and a malformed layout are each reported.</summary>
    [Theory]
    [InlineData("missing layout", "cannot read")]
    [InlineData("no rooms", "no .room files in")]
    [InlineData("bad json", "layout.json is not JSON")]
    public async Task ABrokenLinkInputIsReported(string fault, string expected)
    {
        InMemoryFileSystem fs = new();
        if (fault != "missing layout")
        {
            fs.AddText(Rooted("/rooms/level.json"), fault == "bad json" ? "{" : "{}");
        }

        if (fault == "bad json")
        {
            await AddRoomFileAsync(fs, "hub", RoomHarness.Hub());
        }

        using StringWriter output = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/rooms/level.json"], output));
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A layout whose own shape is wrong (two rooms in one cell) is reported
    /// and exits failed, instead of escaping as an ArgumentException.
    /// </summary>
    [Fact]
    public async Task ALayoutWithASharedCellIsReportedNotThrown()
    {
        InMemoryFileSystem fs = new();
        await AddRoomFileAsync(fs, "hub", RoomHarness.Hub());
        fs.AddText(Rooted("/rooms/level.json"), """
            { "name": "l", "rooms": [ { "room": "hub", "cellX": 0, "cellY": 0 }, { "room": "hub", "cellX": 0, "cellY": 0 } ] }
            """);
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/rooms/level.json"], output));
        Assert.Contains("two rooms are placed in cell (0, 0)", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A layout restating another grid than the rooms' is not linkable, by name.</summary>
    [Fact]
    public async Task ALayoutOnAnotherGridIsNotLinkable()
    {
        InMemoryFileSystem fs = new();
        await AddRoomFileAsync(fs, "hub", RoomHarness.Hub());
        fs.AddText(Rooted("/rooms/level.json"), """
            { "name": "l", "cellSize": 512, "kit": { "width": 96, "height": 96, "depth": 16 },
              "rooms": [ { "room": "hub", "cellX": 0, "cellY": 0, "capped": [ "PositiveX", "NegativeX", "PositiveY", "NegativeY" ] } ] }
            """);
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/rooms/level.json"], output));
        Assert.Contains("the layout is not linkable: rule 5", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Room files built for two different grids cannot be one library; the
    /// refusal is reported, instead of escaping as an ArgumentException.
    /// </summary>
    [Fact]
    public async Task RoomsOfTwoGridsAreReportedNotThrown()
    {
        InMemoryFileSystem fs = new();
        await AddRoomFileAsync(fs, "hub", RoomHarness.Hub());
        await AddRoomFileAsync(fs, "big", new RoomDefinition("big", 512, RoomHarness.Kit, [new RoomSocket(RoomFacing.PositiveX, "PositiveX")]));
        fs.AddText(Rooted("/rooms/level.json"), """{ "name": "l", "rooms": [ { "room": "hub", "cellX": 0, "cellY": 0 } ] }""");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/rooms/level.json"], output));
        Assert.Contains("was built for cell", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A room file that is not a room container is reported by the room directory.</summary>
    [Fact]
    public async Task ABadRoomFileIsReported()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Rooted("/rooms/junk.room"), [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        fs.AddText(Rooted("/rooms/level.json"), "{}");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/rooms/level.json"], output));
        Assert.Contains("not a room container", output.ToString(), StringComparison.Ordinal);
    }

    // ---- helpers -----------------------------------------------------------

    private static InMemoryFileSystem Game()
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Plain}.vmt"),
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Trigger}.vmt"),
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        return fs;
    }

    /// <summary>A room's VMF and its definition sidecar under <c>/game/maps</c>.</summary>
    private static void AddRoom(InMemoryFileSystem fs, RoomDefinition definition, string file = "hub", RoomDefinition? model = null)
    {
        fs.AddFile(Rooted($"/game/maps/{file}.vmf"), RoomHarness.BuildRoomModel(model ?? definition).ToBytes());
        fs.AddFile(Rooted($"/game/maps/{file}.vmf.roomdef.json"), Encoding.UTF8.GetBytes(RoomDefinitionJson.Write(definition)));
    }

    /// <summary>A compiled room written straight into <c>/rooms</c>.</summary>
    private static async Task AddRoomFileAsync(InMemoryFileSystem fs, string name, RoomDefinition definition)
    {
        RoomLibrary library = new(definition.Kit, definition.CellSize);
        RoomObject room = await RoomCompiler.CompileAsync(
            RoomHarness.BuildRoomModel(definition), definition, await RoomHarness.ContextAsync());
        library.Add(room);
        using MemoryStream stream = new();
        await RoomObjectStore.SaveAsync(room, stream);
        fs.AddFile(Rooted($"/rooms/{name}.room"), stream.ToArray());
    }

    /// <summary>Two hubs side by side, joined at the shared wall, everything else capped.</summary>
    private static void AddPairLayout(InMemoryFileSystem fs) =>
        fs.AddText(Rooted("/rooms/level.json"), """
            { "name": "level", "rooms": [
              { "room": "hub", "cellX": 0, "cellY": 0,
                "joints": [ { "socket": "PositiveX", "neighborSocket": "NegativeX" } ],
                "capped": [ "NegativeX", "PositiveY", "NegativeY" ] },
              { "room": "hub", "cellX": 1, "cellY": 0,
                "joints": [ { "socket": "NegativeX", "neighborSocket": "PositiveX" } ],
                "capped": [ "PositiveX", "PositiveY", "NegativeY" ] } ] }
            """);

    private static async Task<BspData> LoadMapAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted(path)))!);
        return await BspFile.LoadAsync(stream);
    }
}
