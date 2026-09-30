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
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapTools.Io;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// Combined packs through the CLI (the rooms design, PR 18, 17.10):
/// <c>ssmap roompack</c> over two lit libraries whose rooms share names, the
/// same libraries packed separately (plain, and with <c>ssmap room
/// -namespace</c>), and a level of both linked from each; <c>-only</c>,
/// <c>-incremental</c>, <c>-level</c>, the <c>-rooms</c> forms of
/// <c>ssmap link</c>, <c>ssmap rooms</c> and <c>ssmap layout</c>, and every
/// message the verbs add.
/// </summary>
/// <remarks>
/// Every path the commands are given is <see cref="Rooted"/>, and every path
/// a message is expected to print is the host's full path
/// (<see cref="Path.GetFullPath(string)"/>), which on Windows carries a drive
/// letter: the commands resolve against the host and print what they
/// resolved.
/// </remarks>
public sealed class RoomPackCommandsTests
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
    /// The level of both libraries: every quarter turn once, both
    /// libraries' hubs and both their sunlit others (four rooms, two names).
    /// </summary>
    private const string MultiLevel =
        "libraries:\n  base: ../maps/base.vmf\n  caves: ../maps/caves.vmf\nrows: 2\ncolumns: 2\ngrid:\n"
        + "  - [base.hub, caves.other@90]\n  - [caves.hub@180, base.other@270]\n";

    private static readonly string[] Cooker = ["-cooker", "none"];

    // ---- equivalence ----------------------------------------------------------

    /// <summary>
    /// The combined pack and the separate packs (plain, and one namespace
    /// each with <c>ssmap room -namespace</c>) link the level of both
    /// libraries at all four turns to the same map, lit with door light and
    /// unlit: without navigation byte for byte; with it, every lump but the
    /// entities' the same, and the entities differing only in the pack and
    /// level ids, which name different packs. The combined pack is the same
    /// bytes at one thread and at four; a pack of one library's namespace
    /// from <c>ssmap roompack</c> is the one <c>ssmap room -namespace</c>
    /// writes. The combined pack's link warns of nothing, where the
    /// separate packs' warns of the dropped sun: <c>ssmap roompack</c> said
    /// it, once, when it built the pack.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACombinedPackLinksToTheMapOfSeparatePacksAtEveryTurn(bool lit)
    {
        InMemoryFileSystem fs = Game();
        string[] light = lit ? [] : ["-nolight"];

        (int exit, string log) = await PackAsync(fs, ["-out", "/packs/both.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf", "-threads", "4", .. light]);
        Assert.True(exit == Program.ExitSuccess, log);
        const string Dropped = "library caves: 1 singleton(s) equal to library base's dropped (light_environment).";
        Assert.Contains("ssmap roompack: warning: " + Dropped, log, StringComparison.Ordinal);
        Assert.Contains("ssmap roompack: compiled caves.hub (", log, StringComparison.Ordinal);
        Assert.Contains($"ssmap roompack: wrote {Path.GetFullPath("/packs/both.roompack")} (4 of 4 room(s))", log, StringComparison.Ordinal);
        byte[] combined = Bytes(fs, "/packs/both.roompack");

        (exit, log) = await PackAsync(fs, ["-out", "/packs/both1.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf", "-threads", "1", .. light]);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Equal(combined, Bytes(fs, "/packs/both1.roompack"));

        foreach (string key in new[] { "base", "caves" })
        {
            (exit, log) = await RoomAsync(fs, [$"/game/maps/{key}.vmf", "-out", $"/packs/{key}.roompack", .. light]);
            Assert.True(exit == Program.ExitSuccess, log);
            (exit, log) = await RoomAsync(fs, [$"/game/maps/{key}.vmf", "-namespace", key, "-out", $"/ns/{key}.roompack", .. light]);
            Assert.True(exit == Program.ExitSuccess, log);
            Assert.Contains($"ssmap room: compiled {key}.hub (", log, StringComparison.Ordinal);
        }

        (exit, log) = await PackAsync(fs, ["-out", "/ns/base2.roompack", "base=/game/maps/base.vmf", .. light]);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Equal(Bytes(fs, "/ns/base.roompack"), Bytes(fs, "/ns/base2.roompack"));

        // What the combined pack holds: every room qualified, grouped by
        // namespace, the first library's singletons, and caves' rooms
        // compiled with base's worldspawn.
        using (MemoryStream stream = new(combined))
        {
            RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
            Assert.Equal(["base.hub", "base.other", "caves.hub", "caves.other"], index.Entries.Select(e => e.Name));
            Assert.Equal(["CMPL", "LENT", "NSPC"], index.LibrarySections.Select(s => s.Tag));
            IReadOnlyList<RoomPackNamespace> spaces = (await RoomPack.ReadNamespacesAsync(stream, index))!;
            Assert.Equal(["base", "caves"], spaces.Select(s => s.Key));
            Assert.Equal(["../game/maps/base.vmf", "../game/maps/caves.vmf"], spaces.Select(s => s.Source));
            Assert.Equal([(0, 2), (2, 2)], spaces.Select(s => (s.FirstRoom, s.RoomCount)));
            Assert.Equal(RoomPackNamespaces.Digest(Bytes(fs, "/game/maps/caves.vmf")), spaces[1].VmfSha256);
            Assert.Equal(spaces[0].SingletonsSha256, spaces[1].SingletonsSha256);
            Assert.Null(spaces[0].NameKeys);
            Assert.Equal("friend", spaces[1].NameKeys);
            RoomObject caves = (await RoomPack.LoadRoomsAsync(stream, index, ["caves.hub"]))[0];
            Assert.Equal("caves.hub", caves.Definition.Name);
            Assert.Equal("base", LevelLinker.WorldspawnOf(caves).Single(k => k.Key == "comment").Value);
            Assert.Equal(lit, caves.LightingOfCompile is not null);
            Assert.Equal(lit, caves.DoorLightOfCompile is not null);
        }

        fs.AddText(Rooted("/game/levels/multi.yaml"), MultiLevel);
        (exit, log) = await LinkAsync(fs, "combined", "-no-nav", "-rooms", "/packs/both.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.DoesNotContain("warning", log, StringComparison.Ordinal);
        byte[] map = Bytes(fs, "/out/combined.bsp");
        ValidationReport report = await BspValidator.CheckAsync(await BspFile.LoadAsync(new MemoryStream(map)), CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));

        (exit, log) = await LinkAsync(fs, "plain", "-no-nav", "-rooms", "base=/packs/base.roompack", "-rooms", "caves=/packs/caves.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Contains("ssmap link: warning: " + Dropped, log, StringComparison.Ordinal);
        Assert.Equal(map, Bytes(fs, "/out/plain.bsp"));

        (exit, log) = await LinkAsync(fs, "spaced", "-no-nav", "-rooms", "base=/ns/base.roompack", "-rooms", "caves=/ns/caves.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Equal(map, Bytes(fs, "/out/spaced.bsp"));

        // The combined pack named per key: the same map.
        (exit, log) = await LinkAsync(fs, "keyed", "-no-nav", "-rooms", "caves=/packs/both.roompack", "-rooms", "base=/packs/both.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Equal(map, Bytes(fs, "/out/keyed.bsp"));

        // caves' name key reached its rooms: the marker names the lamp of
        // its own cell, as the naming rule rewrites it.
        List<BspEntity> entities = EntityLump.Parse((await BspFile.LoadAsync(new MemoryStream(map)))[BspLump.Entities]);
        BspEntity marker = Assert.Single(entities, e => e.ClassName == "info_target" && e.Get("friend") is not null);
        Assert.Contains(entities, e => e.ClassName == "light" && e.Get("targetname") == marker.Get("friend"));
        Assert.DoesNotContain("cxry", marker.Get("friend")!, StringComparison.Ordinal);

        // With navigation: the same map but for the ids that name the packs.
        (exit, log) = await LinkAsync(fs, "combined-nav", "-rooms", "/packs/both.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        (exit, log) = await LinkAsync(fs, "spaced-nav", "-rooms", "base=/ns/base.roompack", "-rooms", "caves=/ns/caves.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        BspData withIds = await BspFile.LoadAsync(new MemoryStream(Bytes(fs, "/out/combined-nav.bsp")));
        BspData separate = await BspFile.LoadAsync(new MemoryStream(Bytes(fs, "/out/spaced-nav.bsp")));
        foreach (BspLump lump in Enum.GetValues<BspLump>().Where(l => l != BspLump.Entities && (int)l < 64))
        {
            Assert.True(withIds[lump].Data.Span.SequenceEqual(separate[lump].Data.Span), $"lump {lump}");
        }

        static string Stripped(BspData bsp) => string.Join(
            "\n",
            EntityLump.Parse(bsp[BspLump.Entities]).SelectMany(e => e.Pairs)
                .Where(p => p.Key is not (RoomCompileIds.PackIdKey or RoomCompileIds.LevelIdKey)).Select(p => $"{p.Key}={p.Value}"));
        Assert.Equal(Stripped(separate), Stripped(withIds));
        Assert.NotEqual(RoomCompileIds.LevelIdOf(separate), RoomCompileIds.LevelIdOf(withIds));

        // The combined pack's own id is the map's pack id: one pack.
        using MemoryStream packStream = new(combined);
        RoomPackIndex packIndex = await RoomPack.ReadIndexAsync(packStream);
        Guid packId = (await SourceSharp.MapTools.Nav.RoomNavPack.ReadPackIdAsync(packStream, packIndex))!.Value;
        Assert.Equal(packId.ToString("D"), EntityLump.Parse(withIds[BspLump.Entities])[0].Get(RoomCompileIds.PackIdKey));
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>
    /// A game with the harness content, the lit harness's sky, and two
    /// libraries of rooms named alike: <c>base</c> (the lit fixture's hub
    /// and sunlit other) and <c>caves</c> (its own lamps, a marker naming
    /// one through its <c>friend</c> name key), both with the same sun and
    /// both marked in their worldspawn's <c>comment</c>.
    /// </summary>
    private static InMemoryFileSystem Game(VmfDocument? caves = null)
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        foreach ((string path, byte[] bytes) in RoomBrushHarness.Files())
        {
            fs.AddFile(Rooted("/game/" + path), bytes);
        }

        fs.AddText(Rooted($"/game/materials/{RoomLightHarness.Sky}.vmt"), "\"UnlitGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileSky\" \"1\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Plain}.vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Trigger}.vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.PlayerClip}.vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%playerClip\" \"1\"\n}\n");
        fs.AddFile(Rooted("/game/maps/base.vmf"), Base().ToBytes());
        fs.AddFile(Rooted("/game/maps/caves.vmf"), (caves ?? Caves()).ToBytes());
        return fs;
    }

    /// <summary>The base library: the lit fixture's.</summary>
    private static VmfDocument Base()
    {
        VmfDocument library = RoomLightHarness.Library(
            true,
            [1],
            (0, RoomLightHarness.Light(800, LitRoomsFixture.HubLight)),
            (0, RoomPropHarness.Prop(801, RoomPropHarness.BoxModel, new Vec3(60, 70, 16), "0 30 0")),
            (1, RoomLightHarness.Light(810, new Vec3(100, 60, 120), "cxry_lamp")));
        library.GetChunk("world")!.AddKey("comment", "base");
        return library;
    }

    /// <summary>The caves library: other lamps and a marker naming one by a name key of its own.</summary>
    private static VmfDocument Caves()
    {
        VmfDocument library = RoomLightHarness.Library(
            true,
            [1],
            (0, RoomLightHarness.Light(900, new Vec3(60, 190, 150), "cxry_lamp")),
            (0, RoomPropHarness.Entity("info_target", 901, new Vec3(190, 60, 40), ("targetname", "cxry_mark"), ("friend", "cxry_lamp"))),
            (1, RoomLightHarness.Light(910, new Vec3(190, 190, 100))));
        VmfChunk world = library.GetChunk("world")!;
        world.AddKey("comment", "caves");
        world.AddKey(RoomLibraryOptions.NameKeysKey, "friend");
        return library;
    }

    private static async Task<(int Exit, string Log)> PackAsync(InMemoryFileSystem fs, string[] args)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunRoomPackAsync(fs, [], [.. Cooker, "-game", "/game", .. args], output);
        return (exit, output.ToString());
    }

    private static async Task<(int Exit, string Log)> RoomAsync(InMemoryFileSystem fs, string[] args)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], [.. Cooker, "-game", "/game", .. args], output);
        return (exit, output.ToString());
    }

    private static async Task<(int Exit, string Log)> LinkAsync(InMemoryFileSystem fs, string name, params string[] args)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunLinkAsync(fs, ["/game/levels/multi.yaml", .. args, "-out", $"/out/{name}.bsp"], output);
        return (exit, output.ToString());
    }

    private static byte[] Bytes(InMemoryFileSystem fs, string path) =>
        fs.GetBytes(VPath.Create(Rooted(path))) ?? throw new FileNotFoundException(path);

    /// <summary>Where the commands look for a rooted path they are given: they resolve against the host.</summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
