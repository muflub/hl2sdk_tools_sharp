//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap room</c>, <c>ssmap link</c> and <c>ssmap layout</c> end to end on
/// an in-memory disk: a game with the harness materials, a room library VMF,
/// the <c>.room</c> files the first verb writes, the level files, and the
/// map or flattened VMF the second writes from them.
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

    private static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    // ---- the whole pipeline -------------------------------------------------

    /// <summary>
    /// A library room compiled by <c>ssmap room</c> with its default cooker
    /// links: the room carries world collision, the link carries it too, and
    /// the map it writes passes the loader validation. This is the
    /// pipeline's default road, which the linker used to refuse at the
    /// collision lump.
    /// </summary>
    [Fact]
    public async Task DefaultCookedRoomsLinkIntoAMapWithWorldCollision()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["/game/maps/rooms.vmf", "-out", "/rooms"], output));
        Assert.Contains("collision: managed", output.ToString(), StringComparison.Ordinal);

        AddLevel(fs, "/levels/level.yaml", "hub, hub");
        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms", "-out", "/out/level.bsp"], output);
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
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms"], output));

        AddLevel(fs, "/levels/level.yaml", "hub, hub");
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms", "-out", "/out/level.bsp"], output));
        Assert.Equal(0, (await LoadMapAsync(fs, "/out/level.bsp"))[BspLump.PhysCollide].Length);
    }

    /// <summary>
    /// The defaults line up: <c>ssmap room</c> writes beside the library,
    /// and <c>ssmap link</c> looks for rooms beside the library its level
    /// names and writes the map beside the level.
    /// </summary>
    [Fact]
    public async Task ByDefaultRoomsGoBesideTheLibraryAndTheMapBesideTheLevel()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf"], output));
        Assert.NotNull(fs.GetBytes(VPath.Create(Rooted("/game/maps/hub.room"))));

        AddLevel(fs, "/game/levels/pair.yaml", "hub, hub", library: "../maps/rooms.vmf");
        int exit = await RoomCommands.RunLinkAsync(fs, ["/game/levels/pair.yaml"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.NotNull(fs.GetBytes(VPath.Create(Rooted("/game/levels/pair.bsp"))));
    }

    // ---- ssmap room: its inputs ---------------------------------------------

    /// <summary>Every room of the library becomes its own <c>.room</c>, named for its <c>info_room</c>.</summary>
    [Fact]
    public async Task EveryRoomOfTheLibraryIsWrittenAsItsOwnFile()
    {
        RoomDefinition end = RoomHarness.WalkableRoom("end", RoomFacing.PositiveX);
        InMemoryFileSystem fs = Game(Hub, end);
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        RoomObject hub = await LoadRoomAsync(fs, "/rooms/hub.room");
        RoomObject endRoom = await LoadRoomAsync(fs, "/rooms/end.room");
        Assert.Equal(["east", "west", "north", "south"], hub.Definition.Sockets.Select(s => s.Name));
        Assert.Equal([new RoomSocket(RoomFacing.PositiveX, "east")], endRoom.Definition.Sockets);
    }

    /// <summary>
    /// A room name that is a path is refused before anything is compiled or
    /// written: <c>../escape</c> would have written <c>escape.room</c> beside
    /// <c>-out</c> instead of inside it.
    /// </summary>
    [Fact]
    public async Task ARoomNameThatLeavesTheOutputDirectoryIsRefused()
    {
        InMemoryFileSystem fs = Game(RoomHarness.WalkableRoom("../escape", RoomFacing.PositiveX));
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms"], output);

        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("the room name \"../escape\" starts with '.'", output.ToString(), StringComparison.Ordinal);
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
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "relative-rooms"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.NotNull(fs.GetBytes(VPath.Create(Rooted("relative-rooms/hub.room"))));
    }

    /// <summary>
    /// The room's name is read as UTF-8, as an editor writes it: an
    /// <c>info_room</c> named <c>salle-é</c> in a UTF-8 file is written as
    /// <c>salle-é.room</c> and reads back under that name, where reading the
    /// VMF's bytes as they are would have made it <c>salle-Ã©</c>.
    /// </summary>
    [Fact]
    public async Task ARoomNameIsReadAsUtf8()
    {
        InMemoryFileSystem fs = Game();
        byte[] latin1 = RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("salle-é", RoomFacing.PositiveX)).ToBytes();
        byte[] utf8 = Encoding.UTF8.GetBytes(Encoding.Latin1.GetString(latin1));
        Assert.NotEqual(latin1.Length, utf8.Length);
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), utf8);
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Equal("salle-é", (await LoadRoomAsync(fs, "/rooms/salle-é.room")).Definition.Name);
    }

    /// <summary>
    /// A name whose bytes are not UTF-8 is kept as read: a Latin-1 file's
    /// single-byte <c>é</c> is still <c>é</c>.
    /// </summary>
    [Fact]
    public async Task ARoomNameThatIsNotUtf8IsReadAsLatin1()
    {
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("salle-é", RoomFacing.PositiveX)).ToBytes());
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Equal("salle-é", (await LoadRoomAsync(fs, "/rooms/salle-é.room")).Definition.Name);
    }

    /// <summary>
    /// An <c>info_room</c> whose values are not numbers is reported naming
    /// the key, and nothing is compiled, instead of escaping the command.
    /// </summary>
    [Fact]
    public async Task ABadInfoRoomIsReportedNotThrown()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        library.Chunks.Single(c => c.GetValue("classname") == RoomLibraryVmf.RoomEntity)
            .Keys.Single(k => k.Name == RoomLibraryVmf.CellSizeKey).Value = "big";
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf"], output));
        Assert.Contains("room \"hub\": \"cell_size\" is \"big\", not a number", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fs.Paths, p => p.Value.EndsWith(".room", StringComparison.Ordinal));
    }

    /// <summary>A VMF with no <c>info_room</c> is not a library, and says what it lacks.</summary>
    [Fact]
    public async Task AVmfWithoutAnInfoRoomIsReported()
    {
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/bare.vmf"), RoomHarness.BuildRoomModel(Hub).ToBytes());
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomAsync(fs, [], ["/game/maps/bare.vmf"], output));
        Assert.Contains("the library has no info_room entity", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A library that is not there is reported, not thrown.</summary>
    [Fact]
    public async Task AMissingLibraryIsReported()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomAsync(fs, [], ["/game/maps/none.vmf"], output));
        Assert.Contains("ssmap room: ", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// One room that is not linkable (its plug is not trigger-surfaced, so
    /// the census finds no plug) fails the run, but the other rooms are
    /// still compiled and written.
    /// </summary>
    [Fact]
    public async Task ARoomThatFailsDoesNotStopTheOthers()
    {
        RoomDefinition bad = RoomHarness.WalkableRoom("bad", RoomFacing.PositiveX);
        VmfDocument library = RoomHarness.LibraryVmf(Hub, bad);
        foreach (VmfChunk solid in library.GetChunk("world")!.Chunks)
        {
            // Only the second room's plug: its cell starts at x = 320, so its
            // east plug is the one trigger brush reaching x = 576.
            List<VmfChunk> sides = [.. solid.Chunks];
            if (sides.Any(s => s.GetValue("material") == RoomHarness.Trigger)
                && sides.Any(s => s.GetValue("plane")!.Contains("(576", StringComparison.Ordinal)))
            {
                foreach (VmfKey key in sides.SelectMany(s => s.Keys).Where(k => k.Name == "material"))
                {
                    key.Value = RoomHarness.Plain;
                }
            }
        }

        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms"], output);

        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("room \"bad\" is not linkable: rule 4", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 of 2 room(s) failed", output.ToString(), StringComparison.Ordinal);
        Assert.NotNull(fs.GetBytes(VPath.Create(Rooted("/rooms/hub.room"))));
        Assert.Null(fs.GetBytes(VPath.Create(Rooted("/rooms/bad.room"))));
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

    /// <summary>Anything but one level on the line, or <c>-rooms</c> with <c>--flatten</c>, is a usage error.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task ALinkNeedsOneLevel(int levels, bool flattenWithRooms)
    {
        List<string> args = [.. Enumerable.Range(0, levels).Select(i => $"l{i}.yaml")];
        if (flattenWithRooms)
        {
            args.AddRange(["--flatten", "-rooms", "/rooms"]);
        }

        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLinkAsync(new InMemoryFileSystem(), args, output));
        Assert.Contains("usage: ssmap link", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreadable level, a level that is not YAML, a level that names a
    /// room with no compiled file, and a level of no rooms are each reported,
    /// the file's own problems with their line and column.
    /// </summary>
    [Theory]
    [InlineData("missing level", "cannot read")]
    [InlineData("bad yaml", "line 2, column 1: not YAML")]
    [InlineData("unknown key", "line 1, column 1: unknown key \"size\"")]
    [InlineData("no room file", "line 5, column 6: room \"hub\" has no compiled room")]
    [InlineData("no rooms", "the level places no room")]
    public async Task ABrokenLinkInputIsReported(string fault, string expected)
    {
        InMemoryFileSystem fs = new();
        switch (fault)
        {
            case "bad yaml":
                fs.AddText(Rooted("/levels/level.yaml"), "rows: [1\n");
                break;
            case "unknown key":
                fs.AddText(Rooted("/levels/level.yaml"), "size: 1\n");
                break;
            case "no room file":
                AddLevel(fs, "/levels/level.yaml", "hub");
                break;
            case "no rooms":
                AddLevel(fs, "/levels/level.yaml", "~");
                break;
        }

        using StringWriter output = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms"], output));
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A level whose library path the host cannot hold is reported, not thrown.</summary>
    [Fact]
    public async Task ALibraryPathTheHostCannotHoldIsReported()
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/levels/level.yaml"), "library: \"a\\0b.vmf\"\nrows: 1\ncolumns: 1\ngrid:\n  - [hub]\n");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "--flatten"], output));
        Assert.Contains("is not a usable path", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control of the reachability rule: two rooms with an
    /// empty cell between them are an island each, and a player could never
    /// walk from one to the other. <c>link</c> refuses it naming the room it
    /// cannot reach, and so does <c>--flatten</c>, so no reference is ever
    /// made of a level that could not be linked.
    /// </summary>
    [Fact]
    public async Task ALevelWithAnIslandIsNotLinkable()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms"], output));
        AddLevel(fs, "/levels/island.yaml", "hub, ~, hub", library: "../game/maps/rooms.vmf");

        using StringWriter link = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/island.yaml", "-rooms", "/rooms"], link));
        Assert.Contains(
            "the level is not linkable: rule 6 (EveryRoomReachable): a player cannot reach every room: room \"hub\" at cell (2, 0) is not joined",
            link.ToString(),
            StringComparison.Ordinal);
        Assert.Null(fs.GetBytes(VPath.Create(Rooted("/levels/island.bsp"))));

        using StringWriter flatten = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/island.yaml", "--flatten"], flatten));
        Assert.Contains("rule 6 (EveryRoomReachable)", flatten.ToString(), StringComparison.Ordinal);
        Assert.Null(fs.GetBytes(VPath.Create(Rooted("/levels/island.vmf"))));
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
        await AddRoomFileAsync(fs, "big", new RoomDefinition("big", 512, RoomHarness.Kit, [new RoomSocket(RoomFacing.NegativeX, "west")]));
        AddLevel(fs, "/levels/level.yaml", "hub, big");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms"], output));
        Assert.Contains("was built for cell", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A room file that is not a room container is reported by the room directory.</summary>
    [Fact]
    public async Task ABadRoomFileIsReported()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Rooted("/rooms/junk.room"), [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        AddLevel(fs, "/levels/level.yaml", "junk");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms"], output));
        Assert.Contains("not a room container", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The link reads exactly the rooms its level places: a broken
    /// <c>.room</c> file the level does not name is never opened.
    /// </summary>
    [Fact]
    public async Task ARoomFileTheLevelDoesNotPlaceIsNeverRead()
    {
        InMemoryFileSystem fs = new();
        await AddRoomFileAsync(fs, "hub", RoomHarness.Hub());
        fs.AddFile(Rooted("/rooms/junk.room"), [1, 2, 3]);
        AddLevel(fs, "/levels/level.yaml", "hub, hub");
        using StringWriter output = new();

        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
    }

    /// <summary>A room file renamed to another room's name is refused, not linked as the wrong room.</summary>
    [Fact]
    public async Task ARoomFileMustHoldTheRoomItIsNamedFor()
    {
        InMemoryFileSystem fs = new();
        await AddRoomFileAsync(fs, "other", RoomHarness.Hub());
        AddLevel(fs, "/levels/level.yaml", "other");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms"], output));
        Assert.Contains("holds room \"hub\", not \"other\"", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--flatten</c> writes the level as one VMF beside the level file:
    /// both rooms' brushes and entities, the joined plugs left out and the
    /// capped ones kept, which vbsp then compiles.
    /// </summary>
    [Fact]
    public async Task FlattenWritesTheLevelAsOneVmf()
    {
        InMemoryFileSystem fs = Game(Hub);
        AddLevel(fs, "/game/levels/pair.yaml", "hub, hub", library: "../maps/rooms.vmf");
        using StringWriter output = new();

        int exit = await RoomCommands.RunLinkAsync(fs, ["/game/levels/pair.yaml", "-flatten"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        VmfDocument flat = await VmfDocument.ParseAsync(fs.GetBytes(VPath.Create(Rooted("/game/levels/pair.vmf")))!);
        List<VmfChunk> solids = [.. flat.GetChunk("world")!.GetChunks("solid")];
        int plugs = solids.Count(s => s.Chunks.Any(side => side.GetValue("material") == RoomHarness.Trigger));

        // Two hubs of four sockets each: the one shared wall's two plugs are gone.
        Assert.Equal(6, plugs);
        Assert.Equal(2, flat.GetChunks("entity").Count(e => e.GetValue("classname") == "info_player_start"));
        Assert.DoesNotContain(flat.GetChunks("entity"), e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity);
    }

    /// <summary>A flatten whose library is not there is reported.</summary>
    [Fact]
    public async Task FlattenReportsAMissingLibrary()
    {
        InMemoryFileSystem fs = new();
        AddLevel(fs, "/levels/level.yaml", "hub");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "--flatten"], output));
        Assert.Contains("rooms.vmf", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A flatten of a level that places a room the library lacks is reported with its position.</summary>
    [Fact]
    public async Task FlattenReportsARoomTheLibraryLacks()
    {
        InMemoryFileSystem fs = Game(Hub);
        AddLevel(fs, "/game/levels/level.yaml", "hub, attic", library: "../maps/rooms.vmf");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/game/levels/level.yaml", "--flatten"], output));
        Assert.Contains("line 5, column 11: the level places room \"attic\", which is not in the room library", output.ToString(), StringComparison.Ordinal);

        AddLevel(fs, "/game/levels/empty.yaml", "~", library: "../maps/rooms.vmf");
        using StringWriter empty = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/game/levels/empty.yaml", "--flatten"], empty));
        Assert.Contains("A level places at least one room", empty.ToString(), StringComparison.Ordinal);
    }

    // ---- ssmap layout ------------------------------------------------------------

    /// <summary>
    /// <c>ssmap layout</c> writes a level file that reads back as a level of
    /// the library's rooms, names the library relative to itself, and is the
    /// same file every time for the same seed; the options take two dashes as
    /// well as one.
    /// </summary>
    [Fact]
    public async Task LayoutWritesTheSameValidLevelForTheSameSeed()
    {
        InMemoryFileSystem fs = Game(Hub, RoomHarness.WalkableRoom("end", RoomFacing.PositiveX));
        using StringWriter output = new();

        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(
            fs, ["/game/maps/rooms.vmf", "-rows", "2", "-columns", "3", "-seed", "7", "-out", "/game/levels/a.yaml"], output));
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(
            fs, ["/game/maps/rooms.vmf", "--rows", "2", "--columns", "3", "--seed", "7", "--out", "/game/levels/b.yaml"], output));

        byte[] a = fs.GetBytes(VPath.Create(Rooted("/game/levels/a.yaml")))!;
        Assert.Equal(a, fs.GetBytes(VPath.Create(Rooted("/game/levels/b.yaml"))));
        LevelGrid level = LevelYaml.Parse(Encoding.UTF8.GetString(a), "a");
        Assert.Equal("../maps/rooms.vmf", level.Library);
        Assert.Equal((2, 3), (level.Rows, level.Columns));
        Assert.StartsWith("# generated by ssmap layout: seed 7, 2 rows x 3 columns, 0 empty cell(s)\n", Encoding.UTF8.GetString(a), StringComparison.Ordinal);

        // And it links: the flattened level is the proof a player reaches every room.
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLinkAsync(fs, ["/game/levels/a.yaml", "--flatten"], output));
    }

    /// <summary>With no <c>-out</c>, the level is printed, naming the library from the current folder; <c>-empty</c> leaves cells out.</summary>
    [Fact]
    public async Task LayoutPrintsTheLevelWithoutAnOutFile()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();

        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(
            fs, ["/game/maps/rooms.vmf", "-rows", "3", "-columns", "3", "-seed", "1", "-empty", "0.5"], output));

        LevelGrid level = LevelYaml.Parse(output.ToString(), "printed");
        Assert.Equal(4, level.Cells.Count(c => c is null));
        Assert.EndsWith("rooms.vmf", level.Library, StringComparison.Ordinal);
    }

    /// <summary>Missing or malformed options are usage errors.</summary>
    [Theory]
    [InlineData(new[] { "/lib.vmf", "-rows", "3", "-columns", "3" }, "usage: ssmap layout")]
    [InlineData(new[] { "-rows", "3", "-columns", "3", "-seed", "1" }, "usage: ssmap layout")]
    [InlineData(new[] { "/lib.vmf", "-rows", "0", "-columns", "3", "-seed", "1" }, "-rows and -columns are whole numbers")]
    [InlineData(new[] { "/lib.vmf", "-rows", "3", "-columns", "x", "-seed", "1" }, "-rows and -columns are whole numbers")]
    [InlineData(new[] { "/lib.vmf", "-rows", "3", "-columns", "3", "-seed", "-1" }, "-rows and -columns are whole numbers")]
    [InlineData(new[] { "/lib.vmf", "-rows", "3", "-columns", "3", "-seed", "1", "-empty", "1" }, "-empty is a share of the cells")]
    [InlineData(new[] { "/lib.vmf", "-rows", "3", "-columns", "3", "-seed", "1", "-empty", "some" }, "-empty is a share of the cells")]
    public async Task LayoutRefusesBadOptions(string[] args, string expected)
    {
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLayoutAsync(new InMemoryFileSystem(), args, output));
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A library that cannot make the level asked for is reported: rooms of
    /// one socket cannot join three rooms in a row, since the middle one
    /// needs two doors.
    /// </summary>
    [Fact]
    public async Task LayoutReportsALevelTheLibraryCannotMake()
    {
        InMemoryFileSystem fs = Game(RoomHarness.WalkableRoom("end", RoomFacing.PositiveX));
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(
            fs, ["/game/maps/rooms.vmf", "-rows", "1", "-columns", "3", "-seed", "1"], output));
        Assert.Contains("no level of 1x3 cells with every room reachable was found", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A layout from a library that is not there is reported.</summary>
    [Fact]
    public async Task LayoutReportsAMissingLibrary()
    {
        using StringWriter output = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(
            new InMemoryFileSystem(), ["/nowhere/rooms.vmf", "-rows", "1", "-columns", "1", "-seed", "1"], output));
        Assert.Contains("ssmap layout: ", output.ToString(), StringComparison.Ordinal);
    }

    // ---- ssmap rooms -------------------------------------------------------

    /// <summary>
    /// The checked-in sample library lists as its five rooms, each with its
    /// cell corner and size and every door's plug box and size, in library
    /// order. Pinned as text: this is what an author reads.
    /// </summary>
    [RepoSourceFact("samples/rooms-3x3/rooms.vmf")]
    public async Task TheSampleLibraryListsItsRoomsAndDoors()
    {
        string library = RepoSourceFactAttribute.Find("samples/rooms-3x3/rooms.vmf")!;
        using StringWriter output = new();

        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(new PhysicalFileSystem("/"), [library], output));

        Assert.Equal(
            """
            5 room(s)
            cross: cell at (0, 0, 0), 256 x 256 x 256, 4 door(s)
              east: (240, 80, 16) to (256, 176, 240), 96 wide x 224 high x 16 deep
              west: (0, 80, 16) to (16, 176, 240), 96 wide x 224 high x 16 deep
              north: (80, 240, 16) to (176, 256, 240), 96 wide x 224 high x 16 deep
              south: (80, 0, 16) to (176, 16, 240), 96 wide x 224 high x 16 deep
            tee: cell at (384, 0, 0), 256 x 256 x 256, 3 door(s)
              east: (624, 80, 16) to (640, 176, 240), 96 wide x 224 high x 16 deep
              west: (384, 80, 16) to (400, 176, 240), 96 wide x 224 high x 16 deep
              north: (464, 240, 16) to (560, 256, 240), 96 wide x 224 high x 16 deep
            corner: cell at (768, 0, 0), 256 x 256 x 256, 2 door(s)
              east: (1008, 80, 16) to (1024, 176, 240), 96 wide x 224 high x 16 deep
              north: (848, 240, 16) to (944, 256, 240), 96 wide x 224 high x 16 deep
            hall: cell at (1152, 0, 0), 256 x 256 x 256, 2 door(s)
              east: (1392, 80, 16) to (1408, 176, 240), 96 wide x 224 high x 16 deep
              west: (1152, 80, 16) to (1168, 176, 240), 96 wide x 224 high x 16 deep
            end: cell at (1536, 0, 0), 256 x 256 x 256, 1 door(s)
              east: (1776, 80, 16) to (1792, 176, 240), 96 wide x 224 high x 16 deep

            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// Each room's doors sit in its own cell: the second room's boxes are the
    /// first's moved by the cell pitch, and a renamed socket shows its name
    /// beside its wall.
    /// </summary>
    [Fact]
    public async Task EachRoomsDoorsAreListedInItsOwnCell()
    {
        RoomDefinition end = new("end", RoomHarness.Cell, RoomHarness.WalkableKit, [new RoomSocket(RoomFacing.PositiveX, "front")]);
        InMemoryFileSystem fs = Game(Hub, end);
        using StringWriter output = new();

        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/game/maps/rooms.vmf"], output));

        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("2 room(s)", lines[0]);
        Assert.Equal("hub: cell at (0, 0, 0), 256 x 256 x 256, 4 door(s)", lines[1]);
        Assert.Equal("  east: (240, 80, 16) to (256, 176, 240), 96 wide x 224 high x 16 deep", lines[2]);
        float pitch = RoomHarness.Cell + RoomHarness.LibraryGap;
        Assert.Equal($"end: cell at ({pitch}, 0, 0), 256 x 256 x 256, 1 door(s)", lines[6]);
        Assert.Equal($"  east \"front\": ({pitch + 240}, 80, 16) to ({pitch + 256}, 176, 240), 96 wide x 224 high x 16 deep", lines[7]);
    }

    /// <summary>Anything but one library path is a usage error.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("a.vmf b.vmf")]
    [InlineData("-out")]
    public async Task RoomsNeedsOneLibrary(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunRoomsAsync(new InMemoryFileSystem(), args, output));
        Assert.Contains("usage: ssmap rooms <library.vmf>", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A missing library, and a VMF with no rooms in it, are reported and fail.</summary>
    [Fact]
    public async Task AMissingOrRoomlessLibraryIsReported()
    {
        InMemoryFileSystem fs = new();
        using StringWriter missing = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomsAsync(fs, ["/nowhere/rooms.vmf"], missing));
        Assert.StartsWith("ssmap rooms: ", missing.ToString(), StringComparison.Ordinal);

        fs.AddText(Rooted("/lib/empty.vmf"), "world\n{\n\t\"id\" \"1\"\n\t\"classname\" \"worldspawn\"\n}\n");
        using StringWriter empty = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomsAsync(fs, ["/lib/empty.vmf"], empty));
        Assert.Contains(RoomLibraryVmf.RoomEntity, empty.ToString(), StringComparison.Ordinal);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>A game with the harness materials, and a library of the given rooms at <c>/game/maps/rooms.vmf</c>.</summary>
    private static InMemoryFileSystem Game(params RoomDefinition[] rooms)
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Plain}.vmt"),
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Trigger}.vmt"),
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        if (rooms.Length > 0)
        {
            fs.AddFile(Rooted("/game/maps/rooms.vmf"), RoomHarness.LibraryVmf(rooms).ToBytes());
        }

        return fs;
    }

    /// <summary>A one-row level file.</summary>
    private static void AddLevel(InMemoryFileSystem fs, string path, string row, string library = "rooms.vmf") =>
        fs.AddText(Rooted(path), RoomHarness.LevelText(library, row));

    /// <summary>A compiled room written straight into <c>/rooms</c>.</summary>
    private static async Task AddRoomFileAsync(InMemoryFileSystem fs, string file, RoomDefinition definition)
    {
        RoomObject room = await RoomCompiler.CompileAsync(
            RoomHarness.BuildRoomModel(definition), definition, await RoomHarness.ContextAsync());
        using MemoryStream stream = new();
        await RoomObjectStore.SaveAsync(room, stream);
        fs.AddFile(Rooted($"/rooms/{file}.room"), stream.ToArray());
    }

    private static async Task<RoomObject> LoadRoomAsync(InMemoryFileSystem fs, string path)
    {
        byte[]? bytes = fs.GetBytes(VPath.Create(Rooted(path)));
        Assert.True(bytes is not null, $"{path} was not written");
        using MemoryStream stream = new(bytes!);
        return await RoomObjectStore.LoadAsync(stream);
    }

    private static async Task<BspData> LoadMapAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted(path)))!);
        return await BspFile.LoadAsync(stream);
    }
}
