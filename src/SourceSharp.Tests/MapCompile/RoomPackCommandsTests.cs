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

        // The marker names the lamp of its own cell, as the naming rule
        // rewrites it, whichever pack the room came from.
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

    // ---- rebuilding one library -------------------------------------------------

    /// <summary>
    /// <c>-only</c> compiles the named libraries and copies every other one
    /// from the existing pack: the pack is the one a full build writes, the
    /// copied rooms' sections are the old pack's bytes, and the log names
    /// what was copied. A copied library whose VMF changed, or that was
    /// compiled under other singletons of the first library, is refused; so
    /// are a key that is not given, a pack that is not there, and a pack
    /// without the library.
    /// </summary>
    [Fact]
    public async Task OnlyRebuildsTheNamedLibrariesAndCopiesTheRest()
    {
        InMemoryFileSystem fs = Game();
        string[] both = ["base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf", "-nolight"];
        string pack = Path.GetFullPath("/packs/both.roompack");
        Assert.Equal(Program.ExitSuccess, (await PackAsync(fs, ["-out", "/packs/both.roompack", .. both])).Exit);
        byte[] before = Bytes(fs, "/packs/both.roompack");

        // caves changed: rebuilt alone, base copied.
        fs.AddFile(Rooted("/game/maps/caves.vmf"), WithEntity(Caves(), 1, RoomLightHarness.Light(920, new Vec3(60, 60, 60))).ToBytes());
        (int exit, string log) = await PackAsync(fs, ["-out", "/packs/both.roompack", "-only", "caves", .. both]);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Contains($"ssmap roompack: library base: copied 2 room(s) from {pack}", log, StringComparison.Ordinal);
        Assert.Contains("ssmap roompack: compiled caves.other (", log, StringComparison.Ordinal);
        Assert.DoesNotContain("compiled base.", log, StringComparison.Ordinal);
        Assert.Contains($"ssmap roompack: wrote {pack} (4 of 4 room(s))", log, StringComparison.Ordinal);
        byte[] only = Bytes(fs, "/packs/both.roompack");
        Assert.Equal(Program.ExitSuccess, (await PackAsync(fs, ["-out", "/packs/full.roompack", .. both])).Exit);
        Assert.Equal(Bytes(fs, "/packs/full.roompack"), only);
        Assert.Equal(await SectionsAsync(before, 0, 2), await SectionsAsync(only, 0, 2));
        Assert.NotEqual(await SectionsAsync(before, 2, 2), await SectionsAsync(only, 2, 2));

        // base's rooms changed (not its singletons): -only caves refuses it;
        // -only base rebuilds it and copies caves.
        VmfDocument edited = WithEntity(Base(), 0, RoomPropHarness.Entity("info_target", 830, new Vec3(40, 40, 40), ("targetname", "spot")));
        fs.AddFile(Rooted("/game/maps/base.vmf"), edited.ToBytes());
        (exit, log) = await PackAsync(fs, ["-out", "/packs/both.roompack", "-only", "caves", .. both]);
        Assert.Equal(1, exit);
        Assert.Contains($"ssmap roompack: library base changed since {pack} was built (its VMF); rebuild it too, or leave out -only.", log, StringComparison.Ordinal);
        Assert.Equal(only, Bytes(fs, "/packs/both.roompack"));
        (exit, log) = await PackAsync(fs, ["-out", "/packs/both.roompack", "-only", "base", .. both]);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Contains($"ssmap roompack: library caves: copied 2 room(s) from {pack}", log, StringComparison.Ordinal);
        Assert.Equal(Program.ExitSuccess, (await PackAsync(fs, ["-out", "/packs/full.roompack", .. both])).Exit);
        Assert.Equal(Bytes(fs, "/packs/full.roompack"), Bytes(fs, "/packs/both.roompack"));

        // base's worldspawn changed: every caves room was compiled under the
        // old one, so -only base refuses to copy them.
        edited.GetChunk("world")!.AddKey("detailmaterial", "detail/other");
        fs.AddFile(Rooted("/game/maps/base.vmf"), edited.ToBytes());
        (exit, log) = await PackAsync(fs, ["-out", "/packs/both.roompack", "-only", "base", .. both]);
        Assert.Equal(1, exit);
        Assert.Contains(
            $"ssmap roompack: library caves changed since {pack} was built (the first library's singletons); rebuild it too, or leave out -only.",
            log,
            StringComparison.Ordinal);

        // -only names a library that is not given; there is no pack; the pack has no such library.
        (exit, log) = await PackAsync(fs, ["-out", "/packs/both.roompack", "-only", "base,halls", .. both]);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("ssmap roompack: -only names halls, which is not one of base and caves.", log, StringComparison.Ordinal);
        (exit, log) = await PackAsync(fs, ["-out", "/packs/none.roompack", "-only", "base", .. both]);
        Assert.Equal(1, exit);
        Assert.Contains(
            $"ssmap roompack: -only copies the other libraries from {Path.GetFullPath("/packs/none.roompack")}, and there is none; build it once without -only.",
            log,
            StringComparison.Ordinal);
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs, ["/game/maps/caves.vmf", "-nolight", "-out", "/packs/plain.roompack"])).Exit);
        (exit, log) = await PackAsync(fs, ["-out", "/packs/plain.roompack", "-only", "base", .. both]);
        Assert.Equal(1, exit);
        Assert.Contains(
            $"ssmap roompack: {Path.GetFullPath("/packs/plain.roompack")} holds no library caves; rebuild it too, or leave out -only.",
            log,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>-incremental</c> sends every room of every library through the
    /// room cache: a second run reuses all four; an edit to one room of one
    /// library compiles that room alone; an edit to the first library's
    /// worldspawn compiles every room, the later library's too, since each
    /// carries it. Each pack is the one a clean build writes.
    /// </summary>
    [Fact]
    public async Task IncrementalRebuildsOnlyWhatChangedInOneLibrary()
    {
        using TempTree tree = new();
        InMemoryFileSystem fs = Game();
        string[] both = ["base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf", "-nolight"];
        async Task Check(string counts)
        {
            (int exit, string log) = await PackAsync(fs, ["-out", "/packs/both.roompack", "-incremental", "-cache-dir", tree.Root, .. both]);
            Assert.True(exit == Program.ExitSuccess, log);
            Assert.Contains("ssmap roompack: " + counts, log, StringComparison.Ordinal);
            Assert.Equal(Program.ExitSuccess, (await PackAsync(fs, ["-out", "/packs/clean.roompack", .. both])).Exit);
            Assert.Equal(Bytes(fs, "/packs/clean.roompack"), Bytes(fs, "/packs/both.roompack"));
        }

        await Check("4 compiled, 0 reused");
        Assert.True(File.Exists(Path.Combine(tree.Root, "both.sscache.db")));
        await Check("0 compiled, 4 reused");
        fs.AddFile(Rooted("/game/maps/caves.vmf"), WithEntity(Caves(), 1, RoomLightHarness.Light(920, new Vec3(60, 60, 60))).ToBytes());
        await Check("1 compiled, 3 reused");
        VmfDocument based = Base();
        based.GetChunk("world")!.AddKey("detailmaterial", "detail/other");
        fs.AddFile(Rooted("/game/maps/base.vmf"), based.ToBytes());
        await Check("4 compiled, 0 reused");
    }

    // ---- messages -------------------------------------------------------------

    /// <summary>
    /// What <c>ssmap roompack</c> and <c>ssmap room -namespace</c> refuse,
    /// each by its text: the usage, a key given twice (ignoring case), a
    /// bare path whose stem is not a key, a namespace that is not a key, a
    /// library that does not split (named by its path), and two libraries on
    /// different grids (named by the paths the pack records). A bare path
    /// whose stem is a key takes it.
    /// </summary>
    [Fact]
    public async Task TheVerbRefusesEachCaseWithItsText()
    {
        InMemoryFileSystem fs = Game();
        (int exit, string log) = await PackAsync(fs, ["base=/game/maps/base.vmf"]);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.StartsWith("usage: ssmap roompack -out <pack.roompack> <key>=<library.vmf>", log, StringComparison.Ordinal);
        (exit, _) = await PackAsync(fs, ["-out", "/packs/x.roompack"]);
        Assert.Equal(Program.ExitUsage, exit);
        (exit, _) = await PackAsync(fs, ["-level", "/game/levels/multi.yaml", "base=/game/maps/base.vmf"]);
        Assert.Equal(Program.ExitUsage, exit);

        (exit, log) = await PackAsync(fs, ["-out", "/packs/x.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf", "Base=/game/maps/caves.vmf"]);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("ssmap roompack: the key Base is given twice.", log, StringComparison.Ordinal);

        string stemless = Path.GetFullPath("/game/maps/3x3.vmf");
        (exit, log) = await PackAsync(fs, ["-out", "/packs/x.roompack", stemless]);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains($"ssmap roompack: {stemless} gives no key (3x3 is not a key); write key={stemless}.", log, StringComparison.Ordinal);

        (exit, log) = await RoomAsync(fs, ["/game/maps/base.vmf", "-namespace", "3x3"]);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains(
            "ssmap room: -namespace \"3x3\" is not a key; a key starts with a letter and holds only letters, digits, '_' and '-'.",
            log,
            StringComparison.Ordinal);

        // A bare path whose stem is a key: that key.
        (exit, log) = await PackAsync(fs, ["-out", "/packs/bare.roompack", "-nolight", Path.GetFullPath("/game/maps/base.vmf")]);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Contains("ssmap roompack: compiled base.hub (", log, StringComparison.Ordinal);

        // A library that does not split, named by its path, before any room compiles.
        fs.AddFile(Rooted("/game/maps/caves.vmf"), new VmfDocument { Chunks = { new VmfChunk("world") } }.ToBytes());
        (exit, log) = await PackAsync(fs, ["-out", "/packs/x.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf"]);
        Assert.Equal(1, exit);
        Assert.Contains($"ssmap roompack: {Path.GetFullPath("/game/maps/caves.vmf")}: the library has no info_room entity;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("compiled", log, StringComparison.Ordinal);

        // Two libraries on different grids.
        fs.AddFile(Rooted("/game/maps/caves.vmf"), RoomHarness.LibraryVmf(new RoomDefinition(
            "cross", 128, new SocketKit(64, 96, 16), [new RoomSocket(RoomFacing.PositiveX, "east")])).ToBytes());
        (exit, log) = await PackAsync(fs, ["-out", "/packs/x.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf"]);
        Assert.Equal(1, exit);
        Assert.Contains(
            "ssmap roompack: libraries base (../game/maps/base.vmf) and caves (../game/maps/caves.vmf) are built for different grids: cell_size 256 against 128;"
            + " the rooms of a level share one cell size.",
            log,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The rest of the verb's refusals: a bad <c>-nav-codec</c>, <c>-vrad</c>
    /// with <c>-nolight</c>, an unknown stock option, a library that cannot
    /// be read, a navigation voxel that does not divide the first library's
    /// cell, and a <c>-only</c> pack that is not a pack.
    /// </summary>
    [Fact]
    public async Task TheVerbRefusesItsOptionsAndInputs()
    {
        InMemoryFileSystem fs = Game();
        string[] both = ["-out", "/packs/x.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf"];
        (int exit, string log) = await PackAsync(fs, [.. both, "-nav-codec", "zip"]);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("ssmap roompack: -nav-codec \"zip\" is not none, deflate[:0-9] or brotli[:0-11]", log, StringComparison.Ordinal);
        (exit, log) = await PackAsync(fs, [.. both, "-nolight", "-vrad", "-both"]);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("ssmap roompack: -vrad sets how the rooms are lit, and -nolight lights none", log, StringComparison.Ordinal);
        (exit, log) = await PackAsync(fs, [.. both, "-bogus"]);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("usage: ssmap roompack", log, StringComparison.Ordinal);

        (exit, log) = await PackAsync(fs, ["-out", "/packs/x.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/nowhere.vmf"]);
        Assert.Equal(1, exit);
        Assert.Contains($"ssmap roompack: {Path.GetFullPath("/game/maps/nowhere.vmf")}: ", log, StringComparison.Ordinal);

        VmfDocument voxel = Base();
        voxel.GetChunk("world")!.AddKey(SourceSharp.MapTools.Nav.NavSettings.VoxelKey, "7");
        fs.AddFile(Rooted("/game/maps/voxel.vmf"), voxel.ToBytes());
        (exit, log) = await PackAsync(fs, ["-out", "/packs/x.roompack", "base=/game/maps/voxel.vmf", "-nolight"]);
        Assert.Equal(1, exit);
        Assert.Contains("ssmap roompack: the nav voxel (nav_voxel_size 7) does not divide the 256-unit cell into whole voxels", log, StringComparison.Ordinal);

        fs.AddText(Rooted("/packs/x.roompack"), "not a pack");
        (exit, log) = await PackAsync(fs, [.. both, "-only", "caves"]);
        Assert.Equal(1, exit);
        Assert.Contains($"ssmap roompack: {Path.GetFullPath("/packs/x.roompack")}: not a room pack", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first library's skybox room packs under its qualified name, last
    /// of its rooms, and the pack names it so: a level linked from the
    /// combined pack places it below the grid as the plain pack's link
    /// does, the same map; the later library's skybox is not packed.
    /// </summary>
    [Fact]
    public async Task TheFirstLibrarysSkyboxLinksFromTheCombinedPack()
    {
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/base.vmf"), RoomSkyboxHarness.AddSkybox(Base()).ToBytes());
        fs.AddFile(Rooted("/game/maps/caves.vmf"), RoomSkyboxHarness.AddSkybox(Caves()).ToBytes());
        (int exit, string log) = await PackAsync(fs, ["-out", "/packs/both.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf", "-nolight"]);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Contains("ssmap roompack: warning: library caves: its skybox room \"sky\" is dropped; the level's skybox is library base's, \"sky\".", log, StringComparison.Ordinal);
        using (MemoryStream stream = new(Bytes(fs, "/packs/both.roompack")))
        {
            RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
            Assert.Equal(["base.hub", "base.other", "base.sky", "caves.hub", "caves.other"], index.Entries.Select(e => e.Name));
            Assert.Equal("base.sky", await RoomPack.ReadLibrarySkyboxAsync(stream, index));
        }

        foreach (string key in new[] { "base", "caves" })
        {
            Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs, [$"/game/maps/{key}.vmf", "-nolight", "-out", $"/packs/{key}.roompack"])).Exit);
        }

        fs.AddText(Rooted("/game/levels/multi.yaml"), MultiLevel);
        (exit, log) = await LinkAsync(fs, "combined", "-no-nav", "-rooms", "/packs/both.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        (exit, log) = await LinkAsync(fs, "plain", "-no-nav", "-rooms", "base=/packs/base.roompack", "-rooms", "caves=/packs/caves.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        byte[] map = Bytes(fs, "/out/combined.bsp");
        Assert.Equal(map, Bytes(fs, "/out/plain.bsp"));
        Assert.Contains(EntityLump.Parse((await BspFile.LoadAsync(new MemoryStream(map)))[BspLump.Entities]), e => e.ClassName == "sky_camera");

        // A level of one library lists its namespace's rooms by its stem.
        fs.AddText(Rooted("/game/levels/one.yaml"), RoomHarness.LevelText("../maps/caves.vmf", "hub"));
        using StringWriter list = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/game/levels/one.yaml", "-rooms", "/packs/both.roompack"], list));
        Assert.Equal(2, list.ToString().Split('\n').Count(l => l.StartsWith("  entities: ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// <c>-level</c> takes the libraries from a level file, in its order,
    /// and writes the pack beside it, which the level then links from; a
    /// level of one library keys it by its stem, and one whose stem is not a
    /// key is refused.
    /// </summary>
    [Fact]
    public async Task LevelTakesTheLibrariesFromALevelFile()
    {
        InMemoryFileSystem fs = Game();
        fs.AddText(Rooted("/game/levels/multi.yaml"), MultiLevel);
        (int exit, string log) = await PackAsync(fs, ["-level", "/game/levels/multi.yaml", "-nolight"]);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Contains($"ssmap roompack: wrote {Path.GetFullPath("/game/levels/multi.roompack")} (4 of 4 room(s))", log, StringComparison.Ordinal);
        (exit, log) = await LinkAsync(fs, "level", "-no-nav", "-rooms", "/game/levels/multi.roompack");
        Assert.True(exit == Program.ExitSuccess, log);

        fs.AddText(Rooted("/game/levels/one.yaml"), RoomHarness.LevelText("../maps/caves.vmf", "hub, other@90"));
        (exit, log) = await PackAsync(fs, ["-level", "/game/levels/one.yaml", "-nolight"]);
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Contains("ssmap roompack: compiled caves.hub (", log, StringComparison.Ordinal);

        fs.AddFile(Rooted("/game/maps/3x3.vmf"), Bytes(fs, "/game/maps/caves.vmf"));
        fs.AddText(Rooted("/game/levels/stem.yaml"), RoomHarness.LevelText("../maps/3x3.vmf", "hub"));
        (exit, log) = await PackAsync(fs, ["-level", "/game/levels/stem.yaml"]);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("ssmap roompack: ../maps/3x3.vmf gives no key (3x3 is not a key); write key=../maps/3x3.vmf.", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// How <c>ssmap link</c> finds a level's rooms in packs with namespaces:
    /// a combined <c>-rooms</c> pack without one of the level's keys is
    /// refused naming its namespaces; a namespace built from a file of
    /// another name warns and links; two keyless <c>-rooms</c> are a usage
    /// error; and a level of one library links from its namespace (a pack
    /// of <c>ssmap room -namespace</c>, beside the library or named) to the
    /// map its plain pack gives, or is refused when the pack lacks its key.
    /// </summary>
    [Fact]
    public async Task TheLinkFindsLibrariesInNamespaces()
    {
        InMemoryFileSystem fs = Game();
        string combined = Path.GetFullPath("/packs/both.roompack");
        Assert.Equal(Program.ExitSuccess, (await PackAsync(fs, ["-out", "/packs/both.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf", "-nolight"])).Exit);
        fs.AddText(Rooted("/game/levels/multi.yaml"), MultiLevel);
        (int exit, string log) = await LinkAsync(fs, "multi", "-no-nav", "-rooms", "/packs/both.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        byte[] map = Bytes(fs, "/out/multi.bsp");

        fs.AddText(Rooted("/game/levels/multi.yaml"), MultiLevel.Replace("caves: ../maps/caves.vmf", "halls: ../maps/caves.vmf", StringComparison.Ordinal)
            .Replace("caves.", "halls.", StringComparison.Ordinal));
        (exit, log) = await LinkAsync(fs, "halls", "-no-nav", "-rooms", "/packs/both.roompack");
        Assert.Equal(1, exit);
        Assert.Contains($"ssmap link: room pack {combined} combines libraries base and caves; it has none named halls.", log, StringComparison.Ordinal);

        // A namespace built from another file name: warned, and the same map.
        fs.AddText(Rooted("/game/levels/multi.yaml"), MultiLevel.Replace("../maps/caves.vmf", "../elsewhere/cavern.vmf", StringComparison.Ordinal));
        (exit, log) = await LinkAsync(fs, "moved", "-no-nav", "-rooms", "/packs/both.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.Contains(
            $"ssmap link: warning: library caves: the level names ../elsewhere/cavern.vmf, but room pack {combined} built it from ../game/maps/caves.vmf.",
            log,
            StringComparison.Ordinal);
        Assert.Equal(map, Bytes(fs, "/out/moved.bsp"));
        fs.AddText(Rooted("/game/levels/multi.yaml"), MultiLevel.Replace("../maps/caves.vmf", "../elsewhere/caves.vmf", StringComparison.Ordinal));
        (exit, log) = await LinkAsync(fs, "same-name", "-no-nav", "-rooms", "/packs/both.roompack");
        Assert.True(exit == Program.ExitSuccess, log);
        Assert.DoesNotContain("warning", log, StringComparison.Ordinal);

        string one = Path.GetFullPath("/packs/one.roompack"), two = Path.GetFullPath("/packs/two.roompack");
        (exit, log) = await LinkAsync(fs, "twice", "-rooms", one, "-rooms", two);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains(
            $"ssmap link: -rooms {one} and -rooms {two} each name a pack for every library of the level; give one, or -rooms <key>=<pack> for each key.",
            log,
            StringComparison.Ordinal);

        // A level of one library: its namespace pack beside it, named, or the combined pack.
        fs.AddText(Rooted("/game/levels/one.yaml"), RoomHarness.LevelText("../maps/base.vmf", "hub, other@90"));
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs, ["/game/maps/base.vmf", "-nolight", "-out", "/packs/plain.roompack"])).Exit);
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs, ["/game/maps/base.vmf", "-nolight", "-namespace", "base"])).Exit);
        async Task<byte[]> One(string name, params string[] args)
        {
            using StringWriter output = new();
            int code = await RoomCommands.RunLinkAsync(fs, ["/game/levels/one.yaml", "-no-nav", .. args, "-out", $"/out/{name}.bsp"], output);
            Assert.True(code == Program.ExitSuccess, output.ToString());
            Assert.DoesNotContain("warning", output.ToString(), StringComparison.Ordinal);
            return Bytes(fs, $"/out/{name}.bsp");
        }

        byte[] plain = await One("plain", "-rooms", "/packs/plain.roompack");
        Assert.Equal(plain, await One("beside"));
        Assert.Equal(plain, await One("combined", "-rooms", "/packs/both.roompack"));

        fs.AddText(Rooted("/game/levels/one.yaml"), RoomHarness.LevelText("../maps/halls.vmf", "hub"));
        using StringWriter missing = new();
        Assert.Equal(1, await RoomCommands.RunLinkAsync(fs, ["/game/levels/one.yaml", "-no-nav", "-rooms", "/packs/both.roompack", "-out", "/out/x.bsp"], missing));
        Assert.Contains($"ssmap link: room pack {combined} combines libraries base and caves; it has none named halls.", missing.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A build that does not know the <c>NSPC</c> tag skips it and reads the
    /// rooms under their qualified names, with the pack's own singletons: a
    /// pack with the section taken out links the level that names those
    /// rooms to the map the namespace gives the level that names them
    /// within the library. A damaged section is named by the link.
    /// </summary>
    [Fact]
    public async Task AnOlderReaderSkipsTheNamespaces()
    {
        InMemoryFileSystem fs = Game();
        Assert.Equal(Program.ExitSuccess, (await RoomAsync(fs, ["/game/maps/base.vmf", "-nolight", "-namespace", "base", "-out", "/ns/base.roompack"])).Exit);
        byte[] spaced = Bytes(fs, "/ns/base.roompack");
        RoomPackNamespace space;
        async Task Rewrite(string path, Func<RoomPackSection, byte[], RoomPackSectionData?> library)
        {
            using MemoryStream stream = new(spaced);
            RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
            IReadOnlyList<RoomPackItem> items = await RoomPack.ReadItemsAsync(stream, index, 0, index.Entries.Count);
            List<RoomPackSectionData> sections = [];
            foreach (RoomPackSection section in index.LibrarySections)
            {
                if (library(section, await RoomPack.ReadSectionAsync(stream, index, section)) is { } kept)
                {
                    sections.Add(kept);
                }
            }

            using MemoryStream written = new();
            await RoomPack.SaveAsync(sections, items, written, CancellationToken.None);
            fs.AddFile(Rooted(path), written.ToArray());
        }

        using (MemoryStream stream = new(spaced))
        {
            space = Assert.Single((await RoomPack.ReadNamespacesAsync(stream, await RoomPack.ReadIndexAsync(stream)))!);
        }

        await Rewrite("/old/base.roompack", (s, b) => s.Tag == RoomPackNamespaces.SectionTag ? null : new RoomPackSectionData(s.Tag, b));
        fs.AddText(Rooted("/game/levels/new.yaml"), RoomHarness.LevelText("../maps/base.vmf", "hub, other@90"));
        fs.AddText(Rooted("/game/levels/old.yaml"), RoomHarness.LevelText("../maps/base.vmf", "base.hub, base.other@90"));
        async Task<(int Exit, string Log)> Link(string level, string pack)
        {
            using StringWriter output = new();
            int exit = await RoomCommands.RunLinkAsync(fs, [$"/game/levels/{level}.yaml", "-no-nav", "-rooms", pack, "-out", $"/out/{level}.bsp"], output);
            return (exit, output.ToString());
        }

        Assert.Equal(Program.ExitSuccess, (await Link("new", "/ns/base.roompack")).Exit);
        Assert.Equal(Program.ExitSuccess, (await Link("old", "/old/base.roompack")).Exit);
        Assert.Equal(Bytes(fs, "/out/new.bsp"), Bytes(fs, "/out/old.bsp"));

        await Rewrite(
            "/old/base.roompack",
            (s, b) => s.Tag == RoomPackNamespaces.SectionTag ? RoomPackNamespaces.ToSection([space with { RoomCount = 1 }]) : new RoomPackSectionData(s.Tag, b));
        (int code, string log) = await Link("new", "/old/base.roompack");
        Assert.Equal(1, code);
        Assert.Contains(
            $"ssmap link: {Path.GetFullPath("/old/base.roompack")}: the room pack's NSPC section covers 1 of the pack's 2 rooms; every room of a pack with namespaces belongs to one.",
            log,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ssmap rooms</c> and <c>ssmap layout</c> read a library's rooms out
    /// of a combined pack by its key (a level's key, or a library VMF's
    /// stem), and refuse a pack without it with the link's text.
    /// </summary>
    [Fact]
    public async Task TheListingAndTheLayoutReadANamespace()
    {
        InMemoryFileSystem fs = Game();
        string combined = Path.GetFullPath("/packs/both.roompack");
        Assert.Equal(Program.ExitSuccess, (await PackAsync(fs, ["-out", "/packs/both.roompack", "base=/game/maps/base.vmf", "caves=/game/maps/caves.vmf", "-nolight"])).Exit);
        fs.AddText(Rooted("/game/levels/multi.yaml"), MultiLevel);

        using (StringWriter list = new())
        {
            Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/game/levels/multi.yaml", "-rooms", "/packs/both.roompack"], list));
            string text = list.ToString();
            Assert.Contains("library caves: ../maps/caves.vmf\n", text, StringComparison.Ordinal);
            Assert.Equal(4, text.Split('\n').Count(l => l.StartsWith("  entities: ", StringComparison.Ordinal)));
            Assert.Contains("library: 1 entities per level", text, StringComparison.Ordinal);
        }

        using (StringWriter list = new())
        {
            Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/game/maps/caves.vmf", "-rooms", "/packs/both.roompack"], list));
            Assert.Equal(2, list.ToString().Split('\n').Count(l => l.StartsWith("  entities: ", StringComparison.Ordinal)));
        }

        using (StringWriter layout = new())
        {
            int exit = await RoomCommands.RunLayoutAsync(
                fs, ["/game/maps/caves.vmf", "-rows", "2", "-columns", "2", "-seed", "1", "-rooms", "/packs/both.roompack", "-entity-budget", "200"], layout);
            Assert.True(exit == Program.ExitSuccess, layout.ToString());
        }

        fs.AddFile(Rooted("/game/maps/halls.vmf"), Bytes(fs, "/game/maps/caves.vmf"));
        string none = $"room pack {combined} combines libraries base and caves; it has none named halls.";
        using (StringWriter list = new())
        {
            Assert.Equal(1, await RoomCommands.RunRoomsAsync(fs, ["/game/maps/halls.vmf", "-rooms", "/packs/both.roompack"], list));
            Assert.Contains("ssmap rooms: " + none, list.ToString(), StringComparison.Ordinal);
        }

        using (StringWriter layout = new())
        {
            Assert.Equal(1, await RoomCommands.RunLayoutAsync(
                fs, ["/game/maps/halls.vmf", "-rows", "2", "-columns", "2", "-seed", "1", "-rooms", "/packs/both.roompack"], layout));
            Assert.Contains("ssmap layout: " + none, layout.ToString(), StringComparison.Ordinal);
        }
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>
    /// A game with the harness content, the lit harness's sky, and two
    /// libraries of rooms named alike: <c>base</c> (the lit fixture's hub
    /// and sunlit other) and <c>caves</c> (its own lamps, a marker naming
    /// one, and a name key of its own, <c>friend</c>, which its namespace
    /// keeps), both with the same sun and both marked in their worldspawn's
    /// <c>comment</c>.
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

    /// <summary>A library with one more entity in a room, room-local.</summary>
    private static VmfDocument WithEntity(VmfDocument library, int room, VmfChunk entity)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        Vec3 corner = split.Rooms[room].Corner;
        Vec3 origin = Vec3Of(entity.GetValue("origin")!) + corner;
        entity.Children.Remove(entity.Keys.First(k => k.Name == "origin"));
        entity.AddKey("origin", VmfPlacement.Format(origin));
        library.Chunks.Add(entity);
        return library;
    }

    private static Vec3 Vec3Of(string text)
    {
        float[] v = [.. text.Split(' ').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture))];
        return new Vec3(v[0], v[1], v[2]);
    }

    /// <summary>A run of a pack's rooms, every section's bytes, as text to compare.</summary>
    private static async Task<string> SectionsAsync(byte[] pack, int first, int count)
    {
        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        IReadOnlyList<RoomPackItem> items = await RoomPack.ReadItemsAsync(stream, index, first, count);
        return string.Join("\n", items.Select(i => $"{i.Name} {Convert.ToHexString(i.Room.Span)}"));
    }

    private static byte[] Bytes(InMemoryFileSystem fs, string path) =>
        fs.GetBytes(VPath.Create(Rooted(path))) ?? throw new FileNotFoundException(path);

    /// <summary>Where the commands look for a rooted path they are given: they resolve against the host.</summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
