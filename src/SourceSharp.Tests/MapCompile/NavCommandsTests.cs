//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Compile;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// Navigation through the commands: <c>ssmap room</c> packs each room's
/// navigation and its points (taken out of the map), <c>ssmap link</c> writes
/// the level's <c>.nav3d</c> beside the map with one id in both, and
/// <c>ssmap nav</c> reports on it.
/// </summary>
public sealed class NavCommandsTests
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

    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    private static VPath At(string path) => VPath.Create(Rooted(path));

    private static readonly RoomDefinition[] Rooms =
    [
        RoomHarness.WalkableRoom("up", RoomFacing.PositiveX),
        RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        RoomHarness.WalkableRoom("down", RoomFacing.NegativeX),
    ];

    /// <summary>
    /// The game and a library of the three rooms: the first an up room with
    /// an arrival, the last a down room with one, the hall with a named
    /// cover point; worldspawn keys added as given.
    /// </summary>
    private static InMemoryFileSystem Game(params (string Key, string Value)[] worldKeys)
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Plain}.vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Trigger}.vmt"),
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        VmfDocument library = RoomHarness.LibraryVmf(Rooms);
        foreach ((string key, string value) in worldKeys)
        {
            library.GetChunk(MapFileLoader.WorldChunk)!.AddKey(key, value);
        }

        float step = RoomHarness.Cell + RoomHarness.LibraryGap;
        List<VmfChunk> markers = [.. library.GetChunks(MapFileLoader.EntityChunk).Where(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity)];
        markers[0].AddKey(RoomPois.RoleKey, "up");
        markers[2].AddKey(RoomPois.RoleKey, "down");
        library.Chunks.Add(RoomPoiTests.PoiEntity(new Vec3(100, 128, 16), ("poi_type", "arrival"), ("angles", "0 0 0"), ("targetname", "start")));
        library.Chunks.Add(RoomPoiTests.PoiEntity(new Vec3(step + 128, 100, 16), ("poi_type", "cover"), ("poi_agents", "standing"), ("targetname", "cxry_cover")));
        library.Chunks.Add(RoomPoiTests.PoiEntity(new Vec3((2 * step) + 150, 128, 16), ("poi_type", "arrival"), ("angles", "0 180 0")));
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        fs.AddText(Rooted("/levels/level.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "up, hall, down"));
        return fs;
    }

    private static async Task<(int Exit, string Log)> RoomAsync(InMemoryFileSystem fs, params string[] more)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", .. more, "/game/maps/rooms.vmf", "-out", "/rooms.roompack"], output);
        return (exit, output.ToString());
    }

    private static async Task<(int Exit, string Log)> LinkAsync(IFileSystem fs, params string[] more)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunLinkAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "-out", "/out/level.bsp", .. more], output);
        return (exit, output.ToString());
    }

    private static async Task<BspData> MapAsync(InMemoryFileSystem fs)
    {
        using MemoryStream stream = new(fs.GetBytes(At("/out/level.bsp"))!);
        return await BspFile.LoadAsync(stream);
    }

    [Fact]
    public async Task RoomThenLinkWritesTheNavigationBesideTheMapWithOneIdInBoth()
    {
        InMemoryFileSystem fs = Game();
        (int exit, string log) = await RoomAsync(fs);
        Assert.True(exit == Program.ExitSuccess, log);
        (exit, log) = await LinkAsync(fs);
        Assert.True(exit == Program.ExitSuccess, log);
        // Three authored points and one per door (two joins, a door each side).
        Match wrote = Regex.Match(log, $@"ssmap link: wrote {Regex.Escape(Path.GetFullPath("/out/level.nav3d"))} \((\d+) leaves, 2 presets, 7 points of interest, (\d+) jump links, (\d+) bytes\)");
        Assert.True(wrote.Success, log);
        Assert.DoesNotContain("warning", log, StringComparison.Ordinal);

        // The map's line comes first: it is written before the navigation is stitched.
        Assert.True(log.IndexOf("ssmap link: wrote " + Path.GetFullPath("/out/level.bsp"), StringComparison.Ordinal) < wrote.Index);

        byte[] file = fs.GetBytes(At("/out/level.nav3d"))!;
        Nav3dReader nav = Nav3dReader.Open(file);
        Assert.Equal((nav.LeafCount, nav.JumpCount, file.Length), (int.Parse(wrote.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(wrote.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(wrote.Groups[3].Value, CultureInfo.InvariantCulture)));

        // Stored with the default codec, Brotli.
        Assert.Equal(LevelNavFromPack.DefaultCompression.Codec, nav.Codec);
        BspData map = await MapAsync(fs);
        BspEntity world = EntityLump.Parse(map[BspLump.Entities])[0];
        Assert.True(nav.MatchesMap(world.Get(RoomCompileIds.LevelIdKey)));
        Assert.Equal(nav.PackId.ToString(), world.Get(RoomCompileIds.PackIdKey));
        Assert.NotEqual(Guid.Empty, nav.PackId);

        // Every room is reachable standing; the ids tie the files together.
        Assert.Equal(3, NavInspector.Stats(nav, 0).RoomsInLargestComponent);

        // The points: two arrivals (the up room's is the spawn), the cover
        // point named for its cell, and a door point per door for every preset.
        Assert.True(nav.TryGetSpawn(out Vec3 spawn, out float yaw));
        Assert.Equal((new Vec3(100, 128, 16), 0f), (spawn, yaw));
        Assert.Equal(Nav3dRoomRole.Down, nav.Poi(nav.DownArrivalPoi).Role);
        Assert.Equal(new Vec3(512 + 150, 128, 16), nav.PoiPosition(nav.DownArrivalPoi));
        Nav3dPoi cover = Enumerable.Range(0, nav.PoiCount).Select(nav.Poi).Single(p => p.Type == "cover");
        Assert.Equal("c1r0_cover", cover.Name);
        Assert.Equal(new Vec3(256 + 128, 100, 16), cover.Position);
        Assert.All(Enumerable.Range(0, nav.PoiCount).Select(nav.Poi).Where(p => p.Type == RoomPois.DoorType),
            p => Assert.True((p.Flags & Nav3dPoiFlags.Joined) != 0 && p.AgentMask == 0b11));
        Assert.Equal(4, Enumerable.Range(0, nav.PoiCount).Count(p => nav.Poi(p).Type == RoomPois.DoorType));
    }

    /// <summary>
    /// <c>ssmap link</c> writes the map, then stitches and writes the
    /// navigation: when the navigation's write fails, the map is already on
    /// disk, whole, and the link says so and fails.
    /// </summary>
    [Fact]
    public async Task TheMapIsWrittenBeforeTheNavigationAndSurvivesItsFailure()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        List<string> order = [];
        ProbeFileSystem probe = new(fs)
        {
            BeforeReplace = path =>
            {
                order.Add(path.ToString());
                if (path.ToString().EndsWith(".nav3d", StringComparison.Ordinal))
                {
                    Assert.NotNull(fs.GetBytes(At("/out/level.bsp")));
                    throw new IOException("the disk is full");
                }

                return Task.CompletedTask;
            },
        };
        (int exit, string log) = await LinkAsync(probe);
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Equal(2, order.Count);
        Assert.EndsWith("level.bsp", order[0], StringComparison.Ordinal);
        Assert.EndsWith("level.nav3d", order[1], StringComparison.Ordinal);
        Assert.Contains("the map was written, but its navigation failed: the disk is full", log, StringComparison.Ordinal);
        Assert.Null(fs.GetBytes(At("/out/level.nav3d")));
        Assert.NotNull(RoomCompileIds.LevelIdOf(await MapAsync(fs)));
    }

    [Fact]
    public async Task PointEntitiesAreGoneFromThePackedRoomsAndTheLinkedMap()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs)).Exit);
        BspData map = await MapAsync(fs);
        Assert.DoesNotContain(EntityLump.Parse(map[BspLump.Entities]), e => e.ClassName == RoomPois.Entity);

        using MemoryStream stream = new(fs.GetBytes(At("/rooms.roompack"))!);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        foreach (RoomObject room in await RoomPack.LoadRoomsAsync(stream, index, ["up", "hall", "down"]))
        {
            Assert.DoesNotContain(EntityLump.Parse(room.Bsp[BspLump.Entities]), e => e.ClassName == RoomPois.Entity);

            // The rooms' points cost no entity: the stored counts have none.
            Assert.DoesNotContain(room.EntityCounts!.Classes, c => c.ClassName == RoomPois.Entity);
            Assert.Equal(RoomEntityCounts.Of(room.Bsp).Classes, room.EntityCounts.Classes);
        }

        // Each turn's names and navigation right after that turn's link sections (a
        // -cooker none room has no collision sections).
        Assert.All(index.Entries, e => Assert.Equal(
            ["ROOM", "ECNT", "LNKA", "GEO0", "NAM0", "NVR0", "GEO1", "NAM1", "NVR1", "GEO2", "NAM2", "NVR2", "GEO3", "NAM3", "NVR3"], e.Sections.Select(s => s.Tag)));
        Assert.Equal(["CMPL"], index.LibrarySections.Select(s => s.Tag));
    }

    [Fact]
    public async Task APackWithoutNavigationLinksWithoutItAndOneWarning()
    {
        InMemoryFileSystem fs = Game(("nav", "0"));
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        (int exit, string log) = await LinkAsync(fs);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Single(log.Split('\n'), l => l.Contains("warning", StringComparison.Ordinal));
        Assert.Contains("ssmap link: warning: the room pack holds no navigation for \"down\", \"hall\", \"up\"", log, StringComparison.Ordinal);
        Assert.Null(fs.GetBytes(At("/out/level.nav3d")));
        // No navigation, so no ids: the map is the one a link without
        // navigation always wrote.
        Assert.Null(RoomCompileIds.LevelIdOf(await MapAsync(fs)));

        (exit, log) = await LinkAsync(fs, "-require-nav");
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("ssmap link: error: the room pack holds no navigation", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoNavWritesNoNavigationAndAMapWithoutIds()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs)).Exit);
        BspData withNav = await MapAsync(fs);
        Assert.NotNull(RoomCompileIds.LevelIdOf(withNav));
        await fs.DeleteAsync(At("/out/level.nav3d"));
        (int exit, string log) = await LinkAsync(fs, "-no-nav");
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.DoesNotContain("warning", log, StringComparison.Ordinal);
        Assert.Null(fs.GetBytes(At("/out/level.nav3d")));
        BspData without = await MapAsync(fs);
        Assert.Null(RoomCompileIds.LevelIdOf(without));

        // The two maps differ in exactly the two worldspawn keys.
        List<BspEntity> a = EntityLump.Parse(withNav[BspLump.Entities]);
        List<BspEntity> b = EntityLump.Parse(without[BspLump.Entities]);
        a[0].Pairs.RemoveAll(p => p.Key is RoomCompileIds.LevelIdKey or RoomCompileIds.PackIdKey);
        Assert.Equal(EntityLump.Write(a).Data.ToArray(), without[BspLump.Entities].Data.ToArray());
        for (int lump = 0; lump < BspData.HeaderLumps; lump++)
        {
            // The game lump's header holds file offsets, which move with the
            // entity lump's length; its entries are compared below.
            if (lump is not ((int)BspLump.Entities or (int)BspLump.GameLump))
            {
                Assert.True(withNav[lump].Data.Span.SequenceEqual(without[lump].Data.Span), $"lump {(BspLump)lump}");
            }
        }

        Assert.Equal(b.Count, a.Count);
        Assert.Equal(without.GameLumps.Count, withNav.GameLumps.Count);
        for (int g = 0; g < without.GameLumps.Count; g++)
        {
            Assert.Equal(without.GameLumps[g].Id, withNav.GameLumps[g].Id);
            Assert.True(without.GameLumps[g].Data.Span.SequenceEqual(withNav.GameLumps[g].Data.Span));
        }
    }

    /// <summary>
    /// The pack and the navigation file are the same bytes at one thread and
    /// four, run after run, and so are the ids; the pack's id changes when
    /// the library does, and the level's when the level file does.
    /// </summary>
    [Fact]
    public async Task ThePackTheNavigationAndTheIdsAreTheSameAtAnyThreadCountRunAfterRun()
    {
        InMemoryFileSystem fs = Game();
        List<(byte[] Pack, byte[] Nav, byte[] Map)> runs = [];
        foreach (string threads in new[] { "1", "4", "1", "4" })
        {
            Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs, "-threads", threads)).Exit);
            Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs)).Exit);
            runs.Add((fs.GetBytes(At("/rooms.roompack"))!, fs.GetBytes(At("/out/level.nav3d"))!, fs.GetBytes(At("/out/level.bsp"))!));
        }

        Assert.All(runs, r => Assert.Equal(runs[0].Pack, r.Pack));
        Assert.All(runs, r => Assert.Equal(runs[0].Nav, r.Nav));
        Assert.All(runs, r => Assert.Equal(runs[0].Map, r.Map));
        Guid pack = Nav3dReader.Open(runs[0].Nav).PackId;
        Guid level = Nav3dReader.Open(runs[0].Nav).LevelId;

        fs.AddText(Rooted("/levels/level.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "up, hall, down") + "# changed\n");
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs)).Exit);
        Nav3dReader relinked = Nav3dReader.Open(fs.GetBytes(At("/out/level.nav3d"))!);
        Assert.Equal(pack, relinked.PackId);
        Assert.NotEqual(level, relinked.LevelId);

        InMemoryFileSystem other = Game(("nav_max_slope", "40"));
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(other)).Exit);
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(other)).Exit);
        Assert.NotEqual(pack, Nav3dReader.Open(other.GetBytes(At("/out/level.nav3d"))!).PackId);
    }

    [Fact]
    public async Task ALinkReadsOnlyTheIndexThePlacedRoomsAndTheirNavigation()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        fs.AddText(Rooted("/levels/level.yaml"), RoomHarness.LevelText("../game/maps/rooms.vmf", "up, hall@180"));
        TapFileSystem tap = new(fs);
        (int exit, string log) = await LinkAsync(tap);
        Assert.True(exit == Program.ExitSuccess, log);

        using MemoryStream stream = new(fs.GetBytes(At("/rooms.roompack"))!);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        // The index, the library's sections, and per placed room its
        // container, its entity counts, its shared link section, and its
        // turn's link, name and navigation sections: none of the other turns'.
        long expected = index.IndexEnd + index.LibrarySections.Sum(s => s.Length);
        foreach ((string room, int turn) in new[] { ("up", 0), ("hall", 2) })
        {
            RoomPackEntry entry = index.Find(room)!;
            expected += entry.Room.Length + entry.Find("ECNT")!.Value.Length + entry.Find("LNKA")!.Value.Length + entry.Find($"GEO{turn}")!.Value.Length
                + entry.Find($"NAM{turn}")!.Value.Length + entry.Find($"NVR{turn}")!.Value.Length;
        }

        Assert.Equal(expected, tap.BytesReadFrom(At("/rooms.roompack")));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("deflate:6")]
    [InlineData("brotli:5")]
    public async Task EveryCodecLinksToTheSameNavigation(string codec)
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs, "-nav-codec", codec)).Exit);
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs, "-nav-codec", codec)).Exit);
        Nav3dReader nav = Nav3dReader.Open(fs.GetBytes(At("/out/level.nav3d"))!);
        Assert.Equal(NavCompression.TryParse(codec, out NavCompression c) ? c.Codec : NavCodec.None, nav.Codec);
        Assert.Equal(3, NavInspector.Stats(nav, 0).RoomsInLargestComponent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABadCodecIsAUsageError(bool room)
    {
        InMemoryFileSystem fs = Game();
        (int exit, string log) = room ? await RoomAsync(fs, "-nav-codec", "zip") : await LinkAsync(fs, "-nav-codec", "zip");
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("-nav-codec \"zip\" is not none, deflate[:0-9] or brotli[:0-11]", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APointInSolidFailsItsRoomNamingTheEntity()
    {
        InMemoryFileSystem fs = Game();
        VmfDocument library = await VmfDocument.ParseAsync(fs.GetBytes(At("/game/maps/rooms.vmf"))!);
        library.Chunks.Add(RoomPoiTests.PoiEntity(new Vec3(128, 128, 4), ("poi_type", "cover")));
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), library.ToBytes());
        (int exit, string log) = await RoomAsync(fs);
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("ssmap room: room \"up\" is not linkable: room \"up\": info_poi 4242 at (128 128 4) is where agent \"standing\" does not fit", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadNavigationSettingsAreReportedBeforeAnythingCompiles()
    {
        (int exit, string log) = await RoomAsync(Game(("nav_voxel_size", "24")));
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("does not divide the 256-unit cell", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavReportsOnAFileOrALevelAndExportsObj()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs)).Exit);

        using StringWriter fromFile = new();
        Assert.Equal(Program.ExitSuccess, await NavCommand.RunAsync(fs, ["/out/level.nav3d", "--obj", "/out/floor.obj", "--floor", "--agent", "flyer"], fromFile));
        Assert.Contains("grid 3 x 1 cells of 256 units, 3 placed", fromFile.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent 1 \"flyer\"", fromFile.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("# ssmap nav: agent \"flyer\", floors", Encoding.UTF8.GetString(fs.GetBytes(At("/out/floor.obj"))!), StringComparison.Ordinal);

        using StringWriter fromLevel = new();
        Assert.Equal(Program.ExitSuccess, await NavCommand.RunAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack", "--obj", "/out/boxes.obj"], fromLevel));
        Assert.Equal(fromFile.ToString().Split('\n')[..^1].Where(l => !l.StartsWith("ssmap", StringComparison.Ordinal)),
            fromLevel.ToString().Split('\n')[..^1].Where(l => !l.StartsWith("ssmap", StringComparison.Ordinal)));
        Assert.StartsWith("# ssmap nav: every leaf, leaves\n", Encoding.UTF8.GetString(fs.GetBytes(At("/out/boxes.obj"))!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "usage: ssmap nav")]
    [InlineData(new[] { "a.nav3d", "b.nav3d" }, "usage: ssmap nav")]
    [InlineData(new[] { "/missing.nav3d" }, "ssmap nav: /missing.nav3d:")]
    [InlineData(new[] { "/levels/level.yaml", "-rooms", "/nope.roompack" }, "ssmap nav: /levels/level.yaml:")]
    [InlineData(new[] { "/out/level.nav3d", "--obj", "/o.obj", "--agent", "tank" }, "no agent \"tank\"")]
    public async Task NavReportsItsProblems(string[] args, string expected)
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        Assert.Equal(Program.ExitSuccess, (await LinkAsync(fs)).Exit);
        using StringWriter output = new();
        Assert.NotEqual(Program.ExitSuccess, await NavCommand.RunAsync(fs, args, output));
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavOnALevelWhosePackHasNoNavigationSaysSo()
    {
        InMemoryFileSystem fs = Game(("nav", "0"));
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs)).Exit);
        using StringWriter output = new();
        Assert.Equal(RoomCommands.ExitFailed, await NavCommand.RunAsync(fs, ["/levels/level.yaml", "-rooms", "/rooms.roompack"], output));
        Assert.Contains("holds no navigation", output.ToString(), StringComparison.Ordinal);
    }
}
