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
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapTools.Io;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap room</c>, <c>ssmap link</c> and <c>ssmap layout</c> end to end on
/// an in-memory disk: a game with the harness materials, a room library VMF,
/// the room pack the first verb writes, the level files, and the map or
/// flattened VMF the second writes from them.
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
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));
        Assert.Contains("collision: managed", output.ToString(), StringComparison.Ordinal);

        AddLevel(fs, "/levels/level.yaml", "hub, hub");
        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-out", "/out/level.bsp"], output);
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
            await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));

        AddLevel(fs, "/levels/level.yaml", "hub, hub");
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-out", "/out/level.bsp"], output));
        Assert.Equal(0, (await LoadMapAsync(fs, "/out/level.bsp"))[BspLump.PhysCollide].Length);
    }

    /// <summary>
    /// The defaults line up: <c>ssmap room</c> writes <c>rooms.roompack</c>
    /// beside <c>rooms.vmf</c>, and <c>ssmap link</c> looks for that pack
    /// beside the library its level names and writes the map beside the level.
    /// </summary>
    [Fact]
    public async Task ByDefaultRoomsGoBesideTheLibraryAndTheMapBesideTheLevel()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf"], output));
        Assert.NotNull(fs.GetBytes(VPath.Create(Rooted("/game/maps/rooms.roompack"))));
        Assert.DoesNotContain(fs.Paths, p => p.Value.EndsWith(".room", StringComparison.Ordinal));

        AddLevel(fs, "/game/levels/pair.yaml", "hub, hub", library: "../maps/rooms.vmf");
        int exit = await RoomCommands.RunLinkAsync(fs, ["/game/levels/pair.yaml"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.NotNull(fs.GetBytes(VPath.Create(Rooted("/game/levels/pair.bsp"))));
    }

    // ---- library-wide entities ------------------------------------------------

    /// <summary>
    /// A library with a <c>light_environment</c> in the gap between its
    /// rooms' cells, the natural place for the one sun every room shares:
    /// <c>ssmap room</c> keeps it in the pack's library section, in library
    /// coordinates, rather than dropping it with the gap's editor clutter (a
    /// plain <c>light</c> there is still ignored). Before the fix the split
    /// dropped it silently and the pack had no library section.
    /// </summary>
    [Fact]
    public async Task ASunInTheGapsIsKeptInThePacksLibrarySection()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        library.Chunks.Add(GapEntity(900101, "light_environment", "-64 -64 128", ("angles", "-45 30 0"), ("_light", "255 255 255 200")));
        library.Chunks.Add(GapEntity(900102, "light", "-64 -64 64", ("_light", "255 255 255 200")));
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());

        byte[] pack = fs.GetBytes(VPath.Create(Rooted("/rooms.roompack")))!;
        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        RoomPackSection section = Assert.Single(index.LibrarySections);
        Assert.Equal("LENT", section.Tag);
        string text = Encoding.UTF8.GetString(pack, (int)section.Offset, (int)section.Length);
        Assert.Contains("\"light_environment\"", text, StringComparison.Ordinal);
        Assert.Contains("\"-64 -64 128\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"light\"", text, StringComparison.Ordinal);

        IReadOnlyList<VmfChunk> kept = await RoomLibraryEntities.ReadAsync(pack.AsMemory((int)section.Offset, (int)section.Length));
        Assert.Equal("light_environment", Assert.Single(kept).GetValue("classname"));
        Assert.Equal("-45 30 0", kept[0].GetValue("angles"));
    }

    /// <summary>
    /// A library with nothing library-wide in its gaps writes a pack with
    /// no library section, byte for byte the pack it wrote before library
    /// sections were written at all.
    /// </summary>
    [Fact]
    public async Task ALibraryWithoutLibraryWideEntitiesWritesNoLibrarySection()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        library.Chunks.Add(GapEntity(900102, "light", "-64 -64 64", ("_light", "255 255 255 200")));
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());

        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted("/rooms.roompack")))!);
        Assert.Empty((await RoomPack.ReadIndexAsync(stream)).LibrarySections);
    }

    // ---- real game content ------------------------------------------------------

    /// <summary>
    /// A game whose sky textures resolve, as any real game's do: a room
    /// compile must not pack the default cubemaps vbsp writes for a map
    /// (named after the room, which no linked level is), so the rooms still
    /// link. Before the fix every room's pak held
    /// <c>materials/maps/&lt;room&gt;/cubemapdefault.vtf</c> and the link
    /// refused every room for it.
    /// </summary>
    [Fact]
    public async Task RoomsOfAGameWithASkyStillLink()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        library.GetChunk("world")!.AddKey("skyname", SurfaceUnitSky);
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        foreach (string face in new[] { "rt", "lf", "bk", "ft", "up", "dn" })
        {
            fs.AddText(Rooted($"/game/materials/skybox/{SurfaceUnitSky}{face}.vmt"),
                $"\"UnlitGeneric\"\n{{\n\t\"$basetexture\" \"skybox/{SurfaceUnitSky}{face}\"\n}}\n");
            fs.AddFile(Rooted($"/game/materials/skybox/{SurfaceUnitSky}{face}.vtf"),
                SourceSharp.Tests.MapTools.Bsp.SurfaceContent.SurfaceUnit.Vtf(512, 512, (int)SourceSharp.MapFormats.Assets.ImageFormat.Bgr888, 0x0304));
        }

        using StringWriter output = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));
        Assert.DoesNotContain("default cubemap", output.ToString(), StringComparison.OrdinalIgnoreCase);

        AddLevel(fs, "/levels/level.yaml", "hub, hub");
        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-out", "/out/level.bsp"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());
    }

    private const string SurfaceUnitSky = "sky_unit";

    private static VmfChunk GapEntity(int id, string classname, string origin, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new("entity");
        entity.AddKey("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        entity.AddKey("classname", classname);
        entity.AddKey("origin", origin);
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    // ---- what they say they wrote -------------------------------------------

    /// <summary>
    /// <c>ssmap room</c> names the pack it wrote as the host spells it, where
    /// it used to print the path with its root cut off.
    /// </summary>
    [Fact]
    public async Task ARoomNamesTheFileItWroteByItsHostPath()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains(
            $"ssmap room: wrote {Path.GetFullPath("/rooms.roompack")} (1 of 1 room(s))",
            output.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary><c>ssmap layout</c> names the level file it wrote as the host spells it.</summary>
    [Fact]
    public async Task ALayoutNamesTheFileItWroteByItsHostPath()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();

        int exit = await RoomCommands.RunLayoutAsync(
            fs, ["/game/maps/rooms.vmf", "-rows", "1", "-columns", "2", "-seed", "1", "-out", "/levels/l.yaml"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains($"ssmap layout: wrote {Path.GetFullPath("/levels/l.yaml")}", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary><c>ssmap link --flatten</c> names the VMF it wrote as the host spells it.</summary>
    [Fact]
    public async Task AFlattenNamesTheVmfItWroteByItsHostPath()
    {
        InMemoryFileSystem fs = Game(Hub);
        AddLevel(fs, "/levels/level.yaml", "hub, hub", library: "../game/maps/rooms.vmf");
        using StringWriter output = new();

        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "--flatten", "-out", "/out/level.vmf"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains($"ssmap link: wrote {Path.GetFullPath("/out/level.vmf")} (", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary><c>ssmap link</c> names the map it wrote as the host spells it.</summary>
    [Fact]
    public async Task ALinkNamesTheMapItWroteByItsHostPath()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));
        AddLevel(fs, "/levels/level.yaml", "hub, hub");

        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-out", "/out/level.bsp"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains(
            $"ssmap link: wrote {Path.GetFullPath("/out/level.bsp")} (2 rooms, ",
            output.ToString(),
            StringComparison.Ordinal);
    }

    // ---- ssmap room: its inputs ---------------------------------------------

    /// <summary>
    /// Every room of the library is in the one pack, named for its
    /// <c>info_room</c>, in library order, and a line per room says so in
    /// that order.
    /// </summary>
    [Fact]
    public async Task EveryRoomOfTheLibraryIsInThePackInLibraryOrder()
    {
        RoomDefinition end = RoomHarness.WalkableRoom("end", RoomFacing.PositiveX);
        InMemoryFileSystem fs = Game(Hub, end);
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Equal(["hub", "end"], (await ReadIndexAsync(fs, "/rooms.roompack")).Entries.Select(e => e.Name));
        RoomObject hub = await LoadRoomAsync(fs, "/rooms.roompack", "hub");
        RoomObject endRoom = await LoadRoomAsync(fs, "/rooms.roompack", "end");
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(
            [
                "ssmap room: compiled hub (1 clusters, 4 sockets)",
                "ssmap room: compiled end (1 clusters, 1 sockets)",
                $"ssmap room: wrote {Path.GetFullPath("/rooms.roompack")} (2 of 2 room(s))",
            ],
            lines[^3..]);
        Assert.Equal(["east", "west", "north", "south"], hub.Definition.Sockets.Select(s => s.Name));
        Assert.Equal([new RoomSocket(RoomFacing.PositiveX, "east")], endRoom.Definition.Sockets);
    }

    /// <summary>
    /// A room name that is a path is refused before anything is compiled or
    /// written: a name is one path segment, whatever it names.
    /// </summary>
    [Fact]
    public async Task ARoomNameThatLeavesTheOutputDirectoryIsRefused()
    {
        InMemoryFileSystem fs = Game(RoomHarness.WalkableRoom("../escape", RoomFacing.PositiveX));
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);

        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("the room name \"../escape\" starts with '.'", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fs.Paths, p => p.Value.EndsWith(".roompack", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>-out</c> resolves against the current directory like every other
    /// path on the line: a relative <c>-out rooms.roompack</c> writes beside
    /// where the command ran, where it used to write under the disk root (and
    /// on Windows a rooted <c>-out</c> lost its drive the same way).
    /// </summary>
    [Fact]
    public async Task ARelativeOutDirectoryResolvesAgainstTheCurrentDirectory()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "relative-rooms.roompack"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.NotNull(fs.GetBytes(VPath.Create(Rooted("relative-rooms.roompack"))));
    }

    /// <summary>
    /// The room's name is read as UTF-8, as an editor writes it: an
    /// <c>info_room</c> named <c>salle-é</c> in a UTF-8 file is packed as
    /// <c>salle-é</c> and reads back under that name, where reading the
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

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Equal("salle-é", (await LoadRoomAsync(fs, "/rooms.roompack", "salle-é")).Definition.Name);
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

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Equal("salle-é", (await LoadRoomAsync(fs, "/rooms.roompack", "salle-é")).Definition.Name);
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
        Assert.DoesNotContain(fs.Paths, p => p.Value.EndsWith(".roompack", StringComparison.Ordinal));
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
    /// still compiled and packed: the pack holds the rest, the failure is
    /// reported in its place in library order, and the exit code is failed.
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

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);

        Assert.Equal(RoomCommands.ExitFailed, exit);
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.StartsWith("ssmap room: compiled hub (", lines[^4], StringComparison.Ordinal);
        Assert.StartsWith("ssmap room: room \"bad\" is not linkable: rule 4", lines[^3], StringComparison.Ordinal);
        Assert.Equal($"ssmap room: wrote {Path.GetFullPath("/rooms.roompack")} (1 of 2 room(s))", lines[^2]);
        Assert.Equal("ssmap room: 1 of 2 room(s) failed", lines[^1]);
        Assert.Equal(["hub"], (await ReadIndexAsync(fs, "/rooms.roompack")).Entries.Select(e => e.Name));
    }

    // ---- ssmap room: rooms side by side --------------------------------------

    /// <summary>
    /// The pack and the log are the same at one thread and at four, run after
    /// run: the rooms compile side by side, but the pack holds them in library
    /// order and the log reports them in it.
    /// </summary>
    [Fact]
    public async Task ThePackAndTheLogAreTheSameAtAnyThreadCountRunAfterRun()
    {
        InMemoryFileSystem fs = Game(Library());
        List<(byte[] Pack, string Log)> runs = [];
        foreach (string threads in new[] { "1", "4", "1", "4" })
        {
            using StringWriter output = new();
            int exit = await RoomCommands.RunRoomAsync(
                fs, [], ["-threads", threads, "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);
            Assert.True(exit == Program.ExitSuccess, output.ToString());
            runs.Add((fs.GetBytes(VPath.Create(Rooted("/rooms.roompack")))!, output.ToString()));
        }

        Assert.All(runs, run => Assert.Equal(runs[0].Pack, run.Pack));
        Assert.All(runs, run => Assert.Equal(runs[0].Log, run.Log));
        Assert.Equal(
            Library().Select(d => $"ssmap room: compiled {d.Name} ("),
            runs[0].Log.Split('\n').Where(l => l.StartsWith("ssmap room: compiled", StringComparison.Ordinal))
                .Select(l => l[..(l.IndexOf('(', StringComparison.Ordinal) + 1)]));
    }

    /// <summary>
    /// Each room in the pack is byte for byte the container a serial compile
    /// of that room alone writes (its own context, one thread, the same
    /// cooker): packing and compiling side by side change no room's bytes.
    /// </summary>
    [Fact]
    public async Task EachPackedRoomIsTheSerialCompileOfThatRoomAlone()
    {
        InMemoryFileSystem fs = Game(Library());
        using StringWriter output = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(fs, [], ["-threads", "4", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));

        VmfDocument library = await VmfDocument.ParseAsync(fs.GetBytes(VPath.Create(Rooted("/game/maps/rooms.vmf")))!);
        await using ContentFileSystem content = new([await DirectoryContentMount.MountAsync(fs, VPath.Create(Rooted("/game")))]);
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using MemoryStream pack = new(fs.GetBytes(VPath.Create(Rooted("/rooms.roompack")))!);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        IReadOnlyList<byte[]> packed = await RoomPack.ReadRoomBytesAsync(pack, index, [.. rooms.Select(r => r.Definition.Name)]);

        for (int i = 0; i < rooms.Count; i++)
        {
            VbspContext alone = new(VbspOptions.Default, content)
            {
                MapBase = rooms[i].Definition.Name.ToLowerInvariant(),
                CollisionCooker = cooker,
                Parallelism = CompileParallelism.Serial,
            };
            RoomObject room = await RoomCompiler.CompileAsync(rooms[i].Document, rooms[i].Definition, alone);
            using MemoryStream serial = new();
            await RoomObjectStore.SaveAsync(room, serial);
            Assert.True(serial.ToArray().AsSpan().SequenceEqual(packed[i]), $"{rooms[i].Definition.Name} differs from its serial compile");
        }
    }

    /// <summary>
    /// A run cancelled while its rooms compile writes no pack, and leaves the
    /// previous one as it was; nor is any temporary left beside it. On the
    /// host's disk, where a temporary would be a real file.
    /// </summary>
    [Fact]
    public async Task ARunCancelledWhileCompilingLeavesThePreviousPackAndNoTemporary()
    {
        using TempTree tree = new();
        string root = tree.Root;
        WriteGame(tree, Library());
        tree.Write("game/maps/rooms.roompack", [7, 7, 7]);
        using CancellationTokenSource cancel = new();
        TapFileSystem fs = new(new PhysicalFileSystem("/"))
        {
            OnRead = path =>
            {
                if (path.Value.EndsWith(".vmt", StringComparison.Ordinal))
                {
                    cancel.Cancel();
                }
            },
        };
        using StringWriter output = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RoomCommands.RunRoomAsync(
            fs, [], ["-cooker", "none", Path.Combine(root, "game/maps/rooms.vmf")], output, cancel.Token));

        Assert.Equal([7, 7, 7], tree.Read("game/maps/rooms.roompack"));
        Assert.Equal(["gameinfo.txt", "maps", "materials"], Directory.EnumerateFileSystemEntries(Path.Combine(root, "game")).Select(Path.GetFileName).Order());
        Assert.Equal(["rooms.roompack", "rooms.vmf"], Directory.EnumerateFiles(Path.Combine(root, "game/maps")).Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// A run cancelled after the pack's bytes are written but before the
    /// file is replaced leaves the previous pack, and no temporary.
    /// </summary>
    [Fact]
    public async Task ARunCancelledWhileWritingThePackLeavesThePreviousPackAndNoTemporary()
    {
        using TempTree tree = new();
        string root = tree.Root;
        WriteGame(tree, Library());
        tree.Write("game/maps/rooms.roompack", [7, 7, 7]);
        using CancellationTokenSource cancel = new();
        TapFileSystem fs = new(new PhysicalFileSystem("/")) { AfterWrite = _ => cancel.Cancel() };
        using StringWriter output = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RoomCommands.RunRoomAsync(
            fs, [], ["-cooker", "none", Path.Combine(root, "game/maps/rooms.vmf")], output, cancel.Token));

        Assert.Equal([7, 7, 7], tree.Read("game/maps/rooms.roompack"));
        Assert.Equal(["rooms.roompack", "rooms.vmf"], Directory.EnumerateFiles(Path.Combine(root, "game/maps")).Select(Path.GetFileName).Order());
        Assert.DoesNotContain("wrote", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A room whose content cannot be read fails with the read's message,
    /// reported against the room in library order, as a room that does not
    /// lint is; the run fails.
    /// </summary>
    [Fact]
    public async Task AContentReadErrorIsReportedAgainstEachRoomInOrder()
    {
        TapFileSystem fs = new(Game(Hub, RoomHarness.WalkableRoom("end", RoomFacing.PositiveX)))
        {
            OnRead = path =>
            {
                if (path.Value.EndsWith(".vmt", StringComparison.Ordinal))
                {
                    throw new IOException("the kit is unreadable");
                }
            },
        };
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);

        Assert.Equal(RoomCommands.ExitFailed, exit);
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(
            [
                "ssmap room: room \"hub\": the kit is unreadable",
                "ssmap room: room \"end\": the kit is unreadable",
                $"ssmap room: wrote {Path.GetFullPath("/rooms.roompack")} (0 of 2 room(s))",
                "ssmap room: 2 of 2 room(s) failed",
            ],
            lines[^4..]);
    }

    /// <summary>A pack that cannot be written is reported, and the run fails.</summary>
    [Fact]
    public async Task APackThatCannotBeWrittenIsReported()
    {
        InMemoryFileSystem inner = Game(Hub);
        TapFileSystem fs = new(inner) { AfterWrite = _ => throw new IOException("the disk is full") };
        using StringWriter output = new();

        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);

        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains($"ssmap room: cannot write {Path.GetFullPath("/rooms.roompack")}: the disk is full", output.ToString(), StringComparison.Ordinal);
        Assert.Null(inner.GetBytes(VPath.Create(Rooted("/rooms.roompack"))));
    }

    /// <summary>
    /// A library in which every room fails still writes its pack, empty, so
    /// the pack always says what the last run compiled; the run fails.
    /// </summary>
    [Fact]
    public async Task ALibraryOfOnlyFailingRoomsWritesAnEmptyPackAndFails()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        foreach (VmfKey key in library.GetChunk("world")!.Chunks.SelectMany(s => s.Chunks).SelectMany(s => s.Keys)
            .Where(k => k.Name == "material" && k.Value == RoomHarness.Trigger))
        {
            key.Value = RoomHarness.Plain;
        }

        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf"], output));
        Assert.Empty((await ReadIndexAsync(fs, "/game/maps/rooms.roompack")).Entries);
        Assert.Contains("(0 of 1 room(s))", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 of 1 room(s) failed", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A <c>-out</c> the host cannot hold as a path is a usage error.</summary>
    [Fact]
    public async Task AnOutPathTheHostCannotHoldIsAUsageError()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();

        Assert.Equal(Program.ExitUsage, await RoomCommands.RunRoomAsync(fs, [], ["/game/maps/rooms.vmf", "-out", "/a\0b"], output));
        Assert.Contains("-out \"/a\0b\" is not a usable path", output.ToString(), StringComparison.Ordinal);
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
            args.AddRange(["--flatten", "-rooms", "/rooms.roompack"]);
        }

        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLinkAsync(new InMemoryFileSystem(), args, output));
        Assert.Contains("usage: ssmap link", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreadable level, a level that is not YAML, a level whose rooms
    /// were never packed, and a level of no rooms are each reported, the
    /// file's own problems with their line and column.
    /// </summary>
    [Theory]
    [InlineData("missing level", "cannot read")]
    [InlineData("bad yaml", "line 2, column 1: not YAML")]
    [InlineData("unknown key", "line 1, column 1: unknown key \"size\"")]
    [InlineData("no room pack", "there is no room pack")]
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
            case "no room pack":
                AddLevel(fs, "/levels/level.yaml", "hub");
                break;
            case "no rooms":
                AddLevel(fs, "/levels/level.yaml", "~");
                break;
        }

        using StringWriter output = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack"], output));
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
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));
        AddLevel(fs, "/levels/island.yaml", "hub, ~, hub", library: "../game/maps/rooms.vmf");

        using StringWriter link = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/island.yaml", "-rooms", "/rooms.roompack"], link));
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
    /// A level that places a room its pack does not hold is refused naming
    /// the room, where the level places it, and the pack.
    /// </summary>
    [Fact]
    public async Task ARoomThePackDoesNotHoldIsNamedWithThePack()
    {
        InMemoryFileSystem fs = new();
        await AddPackAsync(fs, ("hub", RoomHarness.Hub()));
        AddLevel(fs, "/levels/level.yaml", "hub, attic");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack"], output));
        Assert.Contains(
            $"line 5, column 11: room \"attic\" is not in the room pack {Path.GetFullPath("/rooms.roompack")}",
            output.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A link reads the pack's index and the rooms its level places, and
    /// not one byte of the pack's other rooms.
    /// </summary>
    [Fact]
    public async Task ALinkReadsOnlyTheIndexAndTheRoomsItPlaces()
    {
        InMemoryFileSystem inner = new();
        byte[] hub = await AddPackAsync(inner, ("hub", RoomHarness.Hub()), ("big", RoomHarness.Room("big", RoomFacing.PositiveX)));
        byte[] pack = inner.GetBytes(VPath.Create(Rooted("/rooms.roompack")))!;
        AddLevel(inner, "/levels/level.yaml", "hub, hub");
        TapFileSystem fs = new(inner);
        using StringWriter output = new();

        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        Assert.Equal(index.IndexEnd + hub.Length, fs.BytesReadFrom(VPath.Create(Rooted("/rooms.roompack"))));
        Assert.True(index.IndexEnd + hub.Length < pack.Length);
    }

    /// <summary>
    /// Room files built for two different grids cannot be one library; the
    /// refusal is reported, instead of escaping as an ArgumentException.
    /// </summary>
    [Fact]
    public async Task RoomsOfTwoGridsAreReportedNotThrown()
    {
        InMemoryFileSystem fs = new();
        await AddPackAsync(
            fs,
            ("hub", RoomHarness.Hub()),
            ("big", new RoomDefinition("big", 512, RoomHarness.Kit, [new RoomSocket(RoomFacing.NegativeX, "west")])));
        AddLevel(fs, "/levels/level.yaml", "hub, big");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack"], output));
        Assert.Contains("was built for cell", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A file that is not a room pack, and a pack whose room is not a room
    /// container, are each reported naming the pack.
    /// </summary>
    [Theory]
    [InlineData(false, "not a room pack")]
    [InlineData(true, "room pack entry \"junk\": not a room container")]
    public async Task ABadPackOrABadRoomInItIsReported(bool packed, string expected)
    {
        InMemoryFileSystem fs = new();
        if (packed)
        {
            await AddRawPackAsync(fs, new RoomPackItem("junk", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        }
        else
        {
            fs.AddFile(Rooted("/rooms.roompack"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);
        }

        AddLevel(fs, "/levels/level.yaml", "junk");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack"], output));
        Assert.Contains($"ssmap link: {Path.GetFullPath("/rooms.roompack")}: {expected}", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A pack that cannot be read is reported, not thrown.</summary>
    [Fact]
    public async Task APackThatCannotBeReadIsReported()
    {
        InMemoryFileSystem inner = new();
        await AddPackAsync(inner, ("hub", RoomHarness.Hub()));
        AddLevel(inner, "/levels/level.yaml", "hub");
        TapFileSystem fs = new(inner)
        {
            OnRead = path =>
            {
                if (path.Value.EndsWith(".roompack", StringComparison.Ordinal))
                {
                    throw new IOException("the disk is gone");
                }
            },
        };
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack"], output));
        Assert.Contains($"cannot read the room pack {Path.GetFullPath("/rooms.roompack")}: the disk is gone", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A <c>-rooms</c> the host cannot hold as a path is a usage error.</summary>
    [Fact]
    public async Task ARoomsPathTheHostCannotHoldIsAUsageError()
    {
        InMemoryFileSystem fs = new();
        AddLevel(fs, "/levels/level.yaml", "hub");
        using StringWriter output = new();

        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/a\0b"], output));
        Assert.Contains("-rooms \"/a\0b\" is not a usable path", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The link reads exactly the rooms its level places: a broken room in
    /// the pack that the level does not name is never parsed.
    /// </summary>
    [Fact]
    public async Task ARoomTheLevelDoesNotPlaceIsNeverRead()
    {
        InMemoryFileSystem fs = new();
        RoomDefinition definition = RoomHarness.Hub();
        await AddRawPackAsync(
            fs,
            new RoomPackItem("hub", await ContainerAsync(definition)),
            new RoomPackItem("junk", new byte[] { 1, 2, 3 }));
        AddLevel(fs, "/levels/level.yaml", "hub, hub");
        using StringWriter output = new();

        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
    }

    /// <summary>A pack entry that holds another room than it is named for is refused, not linked as the wrong room.</summary>
    [Fact]
    public async Task APackEntryMustHoldTheRoomItIsNamedFor()
    {
        InMemoryFileSystem fs = new();
        await AddRawPackAsync(fs, new RoomPackItem("other", await ContainerAsync(RoomHarness.Hub())));
        AddLevel(fs, "/levels/level.yaml", "other");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack"], output));
        Assert.Contains("room pack entry \"other\" holds room \"hub\"", output.ToString(), StringComparison.Ordinal);
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

    /// <summary>
    /// A grid past the cap is refused before the library is read: the
    /// library here does not exist, and the refusal is still the grid's,
    /// worded as the generator words it, where it used to be the missing
    /// file's (and, for a real library, came only after reading all of it).
    /// </summary>
    [Fact]
    public async Task LayoutRefusesAGridPastTheCapBeforeReadingTheLibrary()
    {
        using StringWriter output = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(
            new InMemoryFileSystem(), ["/nowhere/rooms.vmf", "-rows", "512", "-columns", "512", "-seed", "1"], output));
        Assert.Equal(
            $"ssmap layout: {Path.GetFullPath("/nowhere/rooms.vmf")}: a 512x512 grid has more than 4096 cells (Parameter 'options')"
                + Environment.NewLine,
            output.ToString());
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

    // ---- entity budget -------------------------------------------------------

    /// <summary>
    /// <c>ssmap link</c> always prints the headroom line: the level's edicts
    /// (the worldspawn and each hub's player start), the budget with the
    /// default reserve, and the entity list, which is exactly the linked
    /// entity lump.
    /// </summary>
    [Fact]
    public async Task LinkPrintsTheHeadroomLine()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));
        AddLevel(fs, "/levels/level.yaml", "hub, hub");

        using StringWriter link = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-out", "/out/level.bsp"], link));

        string[] lines = link.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("ssmap link: map entities 3 / budget 1536 (reserve 512, cap 2048); 3 entities in the entity list", lines[0]);
        Assert.StartsWith("ssmap link: wrote ", lines[1], StringComparison.Ordinal);
        Assert.Equal(2, lines.Length);
        Assert.Equal(3, EntityLump.Parse((await LoadMapAsync(fs, "/out/level.bsp"))[BspLump.Entities]).Count);
    }

    /// <summary>
    /// The library's reserve (its worldspawn key) travels in the pack's
    /// library section: <c>ssmap room</c> writes it, and <c>ssmap link</c>
    /// budgets with it, here warning that the level eats into it with the
    /// design's message, before the headroom line. <c>-entity-reserve</c>
    /// overrides it. The key reaches neither the rooms nor the map.
    /// </summary>
    [Fact]
    public async Task TheLibrarysReserveTravelsInThePackAndTheLinkOverridesIt()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        library.GetChunk(MapFileLoader.WorldChunk)!.AddKey(RoomLibraryOptions.EntityReserveKey, "2046");
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));
        Assert.Equal([RoomLibraryOptions.SectionTag], (await ReadIndexAsync(fs, "/rooms.roompack")).LibrarySections.Select(s => s.Tag));

        AddLevel(fs, "/levels/level.yaml", "hub, hub");
        using StringWriter warned = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-out", "/out/a.bsp"], warned));
        string[] lines = warned.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "ssmap link: warning: map entities 3 / budget 2 (reserve 2046, cap 2048): the level uses 1 of the reserve; most expensive rooms: hub x2 = 2",
            lines[0]);
        Assert.Equal("ssmap link: map entities 3 / budget 2 (reserve 2046, cap 2048); 3 entities in the entity list", lines[1]);

        using StringWriter overridden = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLinkAsync(
            fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-entity-reserve", "100", "-out", "/out/b.bsp"], overridden));
        Assert.StartsWith("ssmap link: map entities 3 / budget 1948 (reserve 100, cap 2048);", overridden.ToString(), StringComparison.Ordinal);

        Assert.Equal(fs.GetBytes(VPath.Create(Rooted("/out/a.bsp"))), fs.GetBytes(VPath.Create(Rooted("/out/b.bsp"))));
        BspEntity world = EntityLump.Parse((await LoadMapAsync(fs, "/out/a.bsp"))[BspLump.Entities])[0];
        Assert.DoesNotContain(world.Pairs, p => RoomLibraryOptions.IsLibraryKey(p.Key));
    }

    /// <summary>A library that sets nothing writes no settings section, as before.</summary>
    [Fact]
    public async Task ALibraryThatSetsNothingWritesNoSettings()
    {
        InMemoryFileSystem fs = Game(Hub);
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));
        Assert.Empty((await ReadIndexAsync(fs, "/rooms.roompack")).LibrarySections);
    }

    /// <summary>
    /// A level over the 2048-edict cap is refused with the design's message,
    /// naming the room that costs the most, and no map is written.
    /// </summary>
    [Fact]
    public async Task ALevelOverTheCapIsRefused()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        for (int i = 0; i < 1100; i++)
        {
            library.Chunks.Add(GapEntity(700000 + i, "info_target", "128 128 64"));
        }

        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output));
        AddLevel(fs, "/levels/level.yaml", "hub, hub");

        using StringWriter refused = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-out", "/out/level.bsp"], refused));
        Assert.Equal(
            $"ssmap link: {Path.GetFullPath("/levels/level.yaml")}: map entities 2203 exceed the cap of 2048 edicts; most expensive rooms: hub x2 = 2202"
            + Environment.NewLine,
            refused.ToString());
        Assert.Null(fs.GetBytes(VPath.Create(Rooted("/out/level.bsp"))));
    }

    /// <summary><c>-entity-reserve</c> takes a whole number from 0 to the cap, and <c>--flatten</c> takes none.</summary>
    [Theory]
    [InlineData("-1", "ssmap link: -entity-reserve is a whole number of edicts from 0 to 2048")]
    [InlineData("2049", "ssmap link: -entity-reserve is a whole number of edicts from 0 to 2048")]
    [InlineData("half", "ssmap link: -entity-reserve is a whole number of edicts from 0 to 2048")]
    [InlineData("flatten", "usage: ssmap link <level.yaml> [-rooms <pack.roompack>] [-entity-reserve <n>] [-out <map.bsp>]")]
    public async Task ABadEntityReserveIsAUsageError(string value, string expected)
    {
        string[] args = value == "flatten"
            ? ["/levels/level.yaml", "--flatten", "-entity-reserve", "10"]
            : ["/levels/level.yaml", "-entity-reserve", value];
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLinkAsync(new InMemoryFileSystem(), args, output));
        Assert.StartsWith(expected, output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// With the library's pack beside it, <c>ssmap rooms</c> opens with the
    /// entity budget and lists each room's entities, edicts and server-only
    /// ones; <c>-rooms</c> names another pack.
    /// </summary>
    [Fact]
    public async Task RoomsListsEachRoomsEntitiesFromThePack()
    {
        InMemoryFileSystem fs = Game(Hub, RoomHarness.WalkableRoom("end", RoomFacing.PositiveX));
        using StringWriter compile = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf"], compile));

        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/game/maps/rooms.vmf"], output));
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("2 room(s)", lines[0]);
        Assert.Equal("entity budget 1536 (reserve 512, cap 2048)", lines[1]);
        Assert.StartsWith("hub: cell at (0, 0, 0)", lines[2], StringComparison.Ordinal);
        Assert.Equal("  entities: 1 (1 edicts, 0 server-only)", lines[3]);
        Assert.StartsWith("end: cell at", lines[8], StringComparison.Ordinal);
        Assert.Equal("  entities: 1 (1 edicts, 0 server-only)", lines[9]);

        fs.AddFile(Rooted("/elsewhere/other.roompack"), fs.GetBytes(VPath.Create(Rooted("/game/maps/rooms.roompack")))!);
        await fs.DeleteAsync(VPath.Create(Rooted("/game/maps/rooms.roompack")));
        using StringWriter named = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/game/maps/rooms.vmf", "-rooms", "/elsewhere/other.roompack"], named));
        Assert.Equal(output.ToString(), named.ToString());
    }

    /// <summary>
    /// A room the pack lacks, a room packed before the counts existed, and a
    /// room whose counts include compile-only entities each read as such;
    /// the library's reserve opens the listing.
    /// </summary>
    [Fact]
    public async Task RoomsSaysWhatThePackLacks()
    {
        InMemoryFileSystem fs = Game(Hub, RoomHarness.WalkableRoom("end", RoomFacing.PositiveX), RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX));
        byte[] hub = await ContainerAsync(Hub);
        RoomEntityCounts counts = RoomEntityCounts.Of(RoomEntityCountsTests.Bsp(["worldspawn"], ["light"], ["func_detail"], ["func_detail"]));
        using (MemoryStream stream = new())
        {
            await RoomPack.SaveAsync(
                [new RoomLibraryOptions(1000).ToSection()!.Value],
                [
                    new RoomPackItem("hub", hub) { Extra = [counts.ToSection()] },
                    new RoomPackItem("end", await ContainerAsync(RoomHarness.WalkableRoom("end", RoomFacing.PositiveX))),
                ],
                stream,
                CancellationToken.None);
            fs.AddFile(Rooted("/game/maps/rooms.roompack"), stream.ToArray());
        }

        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/game/maps/rooms.vmf"], output));
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("entity budget 1048 (reserve 1000, cap 2048)", lines[1]);
        Assert.Equal("  entities: 1 (1 edicts, 0 server-only, 2 compile-only stripped at link)", lines[3]);
        Assert.Equal("  entities: not counted; recompile with ssmap room", lines[9]);
        Assert.Equal("  entities: not in the room pack", lines[12]);
    }

    /// <summary>A pack that is not one is reported, and the listing fails.</summary>
    [Fact]
    public async Task RoomsReportsABrokenPack()
    {
        InMemoryFileSystem fs = Game(Hub);
        fs.AddText(Rooted("/game/maps/rooms.roompack"), "not a pack at all, not even close");
        using StringWriter output = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomsAsync(fs, ["/game/maps/rooms.vmf"], output));
        Assert.StartsWith("ssmap rooms: ", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("not a room pack", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// With the pack beside the library, <c>ssmap layout</c> keeps within
    /// <c>cap − reserve</c> by default, which for rooms of a few entities
    /// changes nothing: the level is the one made without a pack.
    /// </summary>
    [Fact]
    public async Task LayoutsDefaultBudgetChangesNothingForSmallRooms()
    {
        InMemoryFileSystem fs = Game(Library());
        using StringWriter output = new();
        string[] args = ["/game/maps/rooms.vmf", "-rows", "3", "-columns", "3", "-seed", "4"];
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(fs, [.. args, "-out", "/levels/before.yaml"], output));
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf"], output));
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(fs, [.. args, "-out", "/levels/after.yaml"], output));
        Assert.Equal(fs.GetBytes(VPath.Create(Rooted("/levels/before.yaml"))), fs.GetBytes(VPath.Create(Rooted("/levels/after.yaml"))));
    }

    /// <summary>
    /// <c>-entity-budget</c> holds the level to it: three rooms of one
    /// entity each are four edicts with the worldspawn, so a budget of four
    /// makes the level and three is refused with the generator's message.
    /// </summary>
    [Fact]
    public async Task LayoutHoldsTheLevelToAnEntityBudget()
    {
        InMemoryFileSystem fs = Game(Library());
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/rooms.vmf"], output));
        string[] args = ["/game/maps/rooms.vmf", "-rows", "1", "-columns", "3", "-seed", "2"];

        using StringWriter fits = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(fs, [.. args, "-entity-budget", "4", "-out", "/levels/four.yaml"], fits));

        using StringWriter over = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, [.. args, "-entity-budget", "3"], over));
        Assert.Equal(
            $"ssmap layout: {Path.GetFullPath("/game/maps/rooms.vmf")}: no level of 1x3 cells keeps within the entity budget of 3 edicts:"
            + " its 3 room(s) bring at least 4, the worldspawn included." + Environment.NewLine,
            over.ToString());
    }

    /// <summary>
    /// <c>-entity-budget</c> needs the rooms' counts: with no pack, or a pack
    /// without them, it is refused naming the pack; without the option, the
    /// layout goes ahead unbudgeted.
    /// </summary>
    [Fact]
    public async Task LayoutsEntityBudgetNeedsTheCounts()
    {
        InMemoryFileSystem fs = Game(Hub);
        string pack = HostPaths.Display(VPath.Create(Rooted("/game/maps/rooms.roompack")));
        string[] args = ["/game/maps/rooms.vmf", "-rows", "1", "-columns", "2", "-seed", "1", "-entity-budget", "100"];

        using StringWriter none = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, args, none));
        Assert.Contains($"-entity-budget counts the rooms' entities, and there is no room pack {pack};", none.ToString(), StringComparison.Ordinal);

        using (MemoryStream stream = new())
        {
            await RoomPack.SaveAsync([new RoomPackItem("hub", await ContainerAsync(Hub))], stream);
            fs.AddFile(Rooted("/game/maps/rooms.roompack"), stream.ToArray());
        }

        using StringWriter uncounted = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, args, uncounted));
        Assert.Contains($"the room pack {pack} has no counts for room \"hub\"; recompile the library with ssmap room.", uncounted.ToString(), StringComparison.Ordinal);

        using StringWriter plain = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(fs, args[..^2], plain));
    }

    /// <summary><c>-entity-budget</c> takes a whole number from 0 to the cap.</summary>
    [Theory]
    [InlineData("-5")]
    [InlineData("2049")]
    [InlineData("lots")]
    public async Task ABadEntityBudgetIsAUsageError(string value)
    {
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLayoutAsync(
            new InMemoryFileSystem(), ["/lib.vmf", "-rows", "1", "-columns", "1", "-seed", "1", "-entity-budget", value], output));
        Assert.Equal("ssmap layout: -entity-budget is a whole number of edicts from 0 to 2048" + Environment.NewLine, output.ToString());
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

    /// <summary>Five walkable rooms of different shapes: enough for rooms to overlap on four threads.</summary>
    private static RoomDefinition[] Library() =>
    [
        Hub,
        RoomHarness.WalkableRoom("end", RoomFacing.PositiveX),
        RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        RoomHarness.WalkableRoom("tee", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY),
        RoomHarness.WalkableRoom("corner", RoomFacing.PositiveX, RoomFacing.PositiveY),
    ];

    /// <summary>The same game as <see cref="Game"/>, on the host's disk under a temporary tree.</summary>
    private static void WriteGame(TempTree tree, params RoomDefinition[] rooms)
    {
        tree.Write("game/gameinfo.txt", Encoding.UTF8.GetBytes(GameInfoText));
        tree.Write($"game/materials/{RoomHarness.Plain}.vmt", Encoding.UTF8.GetBytes(
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n"));
        tree.Write($"game/materials/{RoomHarness.Trigger}.vmt", Encoding.UTF8.GetBytes(
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n"));
        tree.Write("game/maps/rooms.vmf", RoomHarness.LibraryVmf(rooms).ToBytes());
    }

    /// <summary>A one-row level file.</summary>
    private static void AddLevel(InMemoryFileSystem fs, string path, string row, string library = "rooms.vmf") =>
        fs.AddText(Rooted(path), RoomHarness.LevelText(library, row));

    /// <summary>A compiled room's container.</summary>
    private static async Task<byte[]> ContainerAsync(RoomDefinition definition)
    {
        RoomObject room = await RoomCompiler.CompileAsync(
            RoomHarness.BuildRoomModel(definition), definition, await RoomHarness.ContextAsync());
        using MemoryStream stream = new();
        await RoomObjectStore.SaveAsync(room, stream);
        return stream.ToArray();
    }

    /// <summary>Compiled rooms packed straight into <c>/rooms.roompack</c>; returns the first room's container.</summary>
    private static async Task<byte[]> AddPackAsync(InMemoryFileSystem fs, params (string Name, RoomDefinition Definition)[] rooms)
    {
        List<RoomPackItem> items = [];
        foreach ((string name, RoomDefinition definition) in rooms)
        {
            items.Add(new RoomPackItem(name, await ContainerAsync(definition)));
        }

        await AddRawPackAsync(fs, [.. items]);
        return items[0].Room.ToArray();
    }

    /// <summary>A pack of the given containers at <c>/rooms.roompack</c>, whatever they hold.</summary>
    private static async Task AddRawPackAsync(InMemoryFileSystem fs, params RoomPackItem[] items)
    {
        using MemoryStream stream = new();
        await RoomPack.SaveAsync(items, stream);
        fs.AddFile(Rooted("/rooms.roompack"), stream.ToArray());
    }

    private static async Task<RoomPackIndex> ReadIndexAsync(InMemoryFileSystem fs, string path)
    {
        byte[]? bytes = fs.GetBytes(VPath.Create(Rooted(path)));
        Assert.True(bytes is not null, $"{path} was not written");
        using MemoryStream stream = new(bytes!);
        return await RoomPack.ReadIndexAsync(stream);
    }

    private static async Task<RoomObject> LoadRoomAsync(InMemoryFileSystem fs, string path, string room)
    {
        byte[]? bytes = fs.GetBytes(VPath.Create(Rooted(path)));
        Assert.True(bytes is not null, $"{path} was not written");
        using MemoryStream stream = new(bytes!);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        return (await RoomPack.LoadRoomsAsync(stream, index, [room]))[0];
    }

    private static async Task<BspData> LoadMapAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted(path)))!);
        return await BspFile.LoadAsync(stream);
    }
}
