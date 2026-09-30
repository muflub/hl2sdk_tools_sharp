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
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// A level of two libraries through the CLI as a user runs it (the rooms
/// design, PR 17): the 3x3 sample's library and a copy of it packed by
/// <c>ssmap room</c>, one pack each, a level naming both by key, an alias
/// and qualified cells, linked by <c>ssmap link</c> (each pack found beside
/// its library, or named by <c>-rooms key=pack</c>), flattened, listed by
/// <c>ssmap rooms</c>, and every refusal the commands add.
/// </summary>
public sealed class RoomsMultiLibraryCommandsTests
{
    private const string MultiLevel =
        "libraries:\n  base: ../rooms.vmf\n  caves: ../caves.vmf\naliases:\n  X: caves.cross\nrows: 1\ncolumns: 3\ngrid:\n  - [base.cross, X@90, caves.cross@180]\n";

    /// <summary>
    /// Two libraries packed, a level of both linked from the packs beside
    /// them and from packs named per key (the same map), passing the loader
    /// checks; flattened; listed; and a library level with an alias linked.
    /// </summary>
    [Fact]
    public async Task ALevelOfTwoLibrariesLinksFlattensAndListsThroughTheCli()
    {
        InMemoryFileSystem fs = await PackedAsync();

        using StringWriter link = new();
        int linked = await RoomCommands.RunLinkAsync(fs, ["/sample/levels/multi.yaml", "-no-nav", "-out", "/sample/out/multi.bsp"], link);
        Assert.True(linked == Program.ExitSuccess, link.ToString());
        Assert.Contains("ssmap link: wrote", link.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("warning", link.ToString(), StringComparison.Ordinal);
        byte[] beside = fs.GetBytes(VPath.Create(Rooted("/sample/out/multi.bsp")))!;
        BspData map = await BspFile.LoadAsync(new MemoryStream(beside));
        ValidationReport report = await BspValidator.CheckAsync(map, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));

        // Each key's pack named on the command line: the same map.
        fs.AddFile(Rooted("/sample/elsewhere.roompack"), fs.GetBytes(VPath.Create(Rooted("/sample/caves.roompack")))!);
        using StringWriter named = new();
        int again = await RoomCommands.RunLinkAsync(
            fs, ["/sample/levels/multi.yaml", "-no-nav", "-rooms", "caves=" + "/sample/elsewhere.roompack", "-rooms", "base=" + "/sample/rooms.roompack", "-out", "/sample/out/named.bsp"], named);
        Assert.True(again == Program.ExitSuccess, named.ToString());
        Assert.Equal(beside, fs.GetBytes(VPath.Create(Rooted("/sample/out/named.bsp")))!);

        using StringWriter flatten = new();
        int flat = await RoomCommands.RunLinkAsync(fs, ["/sample/levels/multi.yaml", "--flatten", "-out", "/sample/out/multi.vmf"], flatten);
        Assert.True(flat == Program.ExitSuccess, flatten.ToString());
        Assert.Contains("(3 rooms, flattened)", flatten.ToString(), StringComparison.Ordinal);

        using StringWriter rooms = new();
        int listed = await RoomCommands.RunRoomsAsync(fs, ["/sample/levels/multi.yaml"], rooms);
        Assert.True(listed == Program.ExitSuccess, rooms.ToString());
        Assert.Contains("library base: ../rooms.vmf\n", rooms.ToString(), StringComparison.Ordinal);
        Assert.Contains("library caves: ../caves.vmf\n", rooms.ToString(), StringComparison.Ordinal);

        using StringWriter single = new();
        int one = await RoomCommands.RunRoomsAsync(fs, ["/sample/levels/alias.yaml"], single);
        Assert.True(one == Program.ExitSuccess, single.ToString());
        Assert.StartsWith("library: ../rooms.vmf\n", single.ToString(), StringComparison.Ordinal);

        using StringWriter nav = new();
        Assert.Equal(1, await NavCommand.RunAsync(fs, ["/sample/levels/multi.yaml"], nav));
        Assert.Contains("the level names several libraries; ssmap nav stitches a level of one library, and ssmap link writes the .nav3d of any level.", nav.ToString(), StringComparison.Ordinal);
        using StringWriter aliasNav = new();
        Assert.True(
            await NavCommand.RunAsync(fs, ["/sample/levels/alias.yaml", "-rooms", "/sample/rooms.roompack"], aliasNav) == Program.ExitSuccess,
            aliasNav.ToString());

        using StringWriter alias = new();
        int aliased = await RoomCommands.RunLinkAsync(fs, ["/sample/levels/alias.yaml", "-no-nav", "-out", "/sample/out/alias.bsp"], alias);
        Assert.True(aliased == Program.ExitSuccess, alias.ToString());
        using StringWriter aliasFlat = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLinkAsync(fs, ["/sample/levels/alias.yaml", "--flatten", "-out", "/sample/out/alias.vmf"], aliasFlat));
    }

    /// <summary>
    /// The refusals and warnings the commands add for several libraries,
    /// each by its text: a plain pack for two keys, a key the level does not
    /// list, a missing pack, a cell that does not resolve, libraries on
    /// different grids, two keys naming one file, and a library level given
    /// two packs; and a later library's differing singleton warned in link,
    /// flatten and listing alike.
    /// </summary>
    [Fact]
    public async Task TheCommandsRefuseAndWarnForSeveralLibraries()
    {
        InMemoryFileSystem fs = await PackedAsync();
        string level = "/sample/levels/multi.yaml";

        (int exit, string text) = await LinkAsync(fs, level, "-rooms", "/sample/rooms.roompack");
        Assert.Equal(1, exit);
        Assert.Contains(
            $"room pack {"/sample/rooms.roompack"} holds one library without a namespace; give it to one key with -rooms base={"/sample/rooms.roompack"}.",
            text,
            StringComparison.Ordinal);

        (exit, text) = await LinkAsync(fs, level, "-rooms", "halls=" + "/sample/rooms.roompack");
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("-rooms names library halls, which the level does not list; its libraries are base and caves.", text, StringComparison.Ordinal);

        (exit, text) = await LinkAsync(fs, level, "-rooms", "caves=" + "/sample/none.roompack");
        Assert.Equal(1, exit);
        Assert.Contains("there is no room pack", text, StringComparison.Ordinal);
        Assert.Contains("for library caves; compile the library with ssmap room, or point -rooms caves= at its pack", text, StringComparison.Ordinal);

        fs.AddFile(Rooted("/sample/levels/bare.yaml"), Encoding.UTF8.GetBytes(MultiLevel.Replace("[base.cross,", "[cross,", StringComparison.Ordinal)));
        (exit, text) = await LinkAsync(fs, "/sample/levels/bare.yaml");
        Assert.Equal(1, exit);
        Assert.Contains(
            "line 9, column 6: the room \"cross\" is in libraries base and caves; write base.cross or caves.cross, or name one with an alias.",
            text,
            StringComparison.Ordinal);
        (exit, text) = await FlattenAsync(fs, "/sample/levels/bare.yaml");
        Assert.Equal(1, exit);
        Assert.Contains("the room \"cross\" is in libraries base and caves", text, StringComparison.Ordinal);

        fs.AddFile(Rooted("/sample/levels/same.yaml"), Encoding.UTF8.GetBytes(MultiLevel.Replace("../caves.vmf", "./../rooms.vmf", StringComparison.Ordinal)));
        (exit, text) = await LinkAsync(fs, "/sample/levels/same.yaml");
        Assert.Equal(1, exit);
        string same = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath("/sample/levels/same.yaml"))!, "./../rooms.vmf"));
        Assert.Contains($"line 3, column 3: libraries base and caves name the same file, {same}.", text, StringComparison.Ordinal);

        (exit, text) = await LinkAsync(fs, "/sample/levels/alias.yaml", "-rooms", "a.roompack", "-rooms", "b.roompack");
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("names one library; give -rooms once, with its pack.", text, StringComparison.Ordinal);

        // ssmap rooms over a level: a -rooms that names no key, a pack per key, a level that does not read.
        using (StringWriter list = new())
        {
            Assert.Equal(Program.ExitUsage, await RoomCommands.RunRoomsAsync(fs, [level, "-rooms", "/sample/rooms.roompack"], list));
            Assert.Contains("-rooms \"/sample/rooms.roompack\" names no library of the level; write -rooms <key>=<pack>.", list.ToString(), StringComparison.Ordinal);
        }

        using (StringWriter list = new())
        {
            Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, [level, "-rooms", "caves=/sample/rooms.roompack"], list));
        }

        using (StringWriter list = new())
        {
            Assert.Equal(1, await RoomCommands.RunRoomsAsync(fs, ["/sample/levels/bare.yaml"], list));
            Assert.Contains("the room \"cross\" is in libraries base and caves", list.ToString(), StringComparison.Ordinal);
            fs.AddFile(Rooted("/sample/levels/broken.yaml"), "rows: [\n"u8.ToArray());
            Assert.Equal(1, await RoomCommands.RunRoomsAsync(fs, ["/sample/levels/broken.yaml"], list));
        }

        // A pack that is not a pack, and a pack naming a skybox it does not hold.
        byte[] caves0 = fs.GetBytes(VPath.Create(Rooted("/sample/caves.roompack")))!;
        fs.AddFile(Rooted("/sample/caves.roompack"), "not a pack"u8.ToArray());
        (exit, text) = await LinkAsync(fs, level);
        Assert.Equal(1, exit);
        // The CLI names a pack by its full host path (Path.GetFullPath), which on
        // Windows carries the drive letter, so the fact expects the same.
        Assert.Contains($"ssmap link: {Path.GetFullPath("/sample/caves.roompack")}: ", text, StringComparison.Ordinal);
        using (MemoryStream skyless = new())
        {
            await RoomPack.SaveAsync([RoomLibrarySkybox.ToSection("nosuch")], [await RoomPackItem.CreateAsync(await CrossAsync())], skyless, CancellationToken.None);
            fs.AddFile(Rooted("/sample/caves.roompack"), skyless.ToArray());
        }

        (exit, text) = await LinkAsync(fs, level);
        Assert.Equal(1, exit);
        Assert.Contains("names skybox room \"nosuch\" but does not hold it", text, StringComparison.Ordinal);
        fs.AddFile(Rooted("/sample/caves.roompack"), caves0);

        // A later library with its own sun, which the first lacks: the level's
        // (D29), in the linked map and the flattened VMF alike, and no line
        // from link, flatten or ssmap rooms. (Before D29 it was dropped with
        // a "which has none" line from all three.)
        string caves = Encoding.UTF8.GetString(fs.GetBytes(VPath.Create(Rooted("/sample/caves.vmf")))!);
        fs.AddFile(
            Rooted("/sample/caves.vmf"),
            Encoding.UTF8.GetBytes(caves + "entity\n{\n\t\"id\" \"990900\"\n\t\"classname\" \"light_environment\"\n\t\"origin\" \"-64 -64 64\"\n\t\"angles\" \"0 45 0\"\n}\n"));
        using (StringWriter room = new())
        {
            Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(
                fs, [], ["/sample/caves.vmf", "-game", "/sample", "-nolight", "-out", "/sample/caves.roompack"], room));
        }

        (exit, text) = await LinkAsync(fs, level);
        Assert.True(exit == Program.ExitSuccess, text);
        Assert.DoesNotContain("warning", text, StringComparison.Ordinal);
        BspData withSun = await BspFile.LoadAsync(new MemoryStream(fs.GetBytes(VPath.Create(Rooted("/sample/out/x.bsp")))!));
        Assert.Equal("0 45 0", Assert.Single(EntityLump.Parse(withSun[BspLump.Entities]), e => e.ClassName == "light_environment").Get("angles"));
        (exit, text) = await FlattenAsync(fs, level);
        Assert.True(exit == Program.ExitSuccess, text);
        Assert.DoesNotContain("warning", text, StringComparison.Ordinal);
        string flattened = Encoding.UTF8.GetString(fs.GetBytes(VPath.Create(Rooted("/sample/out/x.vmf")))!);
        Assert.Contains("\"angles\" \"0 45 0\"", flattened, StringComparison.Ordinal);
        using StringWriter listing = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, [level], listing));
        Assert.DoesNotContain("warning", listing.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Two libraries on different grids are refused by the link (from the
    /// packs), the flatten and the listing (from the VMFs), naming both.
    /// </summary>
    [Fact]
    public async Task LibrariesOnDifferentGridsAreRefusedThroughTheCli()
    {
        InMemoryFileSystem fs = await PackedAsync();

        // caves: one cross on a half-size grid (its kit walkable there), packed as ssmap room packs it.
        using (MemoryStream pack = new())
        {
            await RoomPack.SaveAsync([await RoomPackItem.CreateAsync(await CrossAsync())], pack);
            fs.AddFile(Rooted("/sample/caves.roompack"), pack.ToArray());
        }

        fs.AddFile(Rooted("/sample/caves.vmf"), RoomHarness.LibraryVmf(HalfCross).ToBytes());

        string message = "libraries base (../rooms.vmf) and caves (../caves.vmf) are built for different grids: cell_size 256 against 128; the rooms of a level share one cell size.";
        string level = "/sample/levels/multi.yaml";
        (int exit, string text) = await LinkAsync(fs, level);
        Assert.Equal(1, exit);
        Assert.Contains(message, text, StringComparison.Ordinal);
        (exit, text) = await FlattenAsync(fs, level);
        Assert.Equal(1, exit);
        Assert.True(text.Contains(message, StringComparison.Ordinal), text);
        using StringWriter listing = new();
        Assert.Equal(1, await RoomCommands.RunRoomsAsync(fs, [level], listing));
        Assert.Contains(message, listing.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A cross on a half-size grid, its kit walkable there.</summary>
    private static RoomDefinition HalfCross => new(
        "cross", 128, new SocketKit(64, 96, 16),
        [.. new[] { RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY }
            .Select(f => new RoomSocket(f, RoomLibraryVmf.WallName(f)))]);

    /// <summary>The half-size cross compiled as <c>ssmap room</c> compiles a room.</summary>
    private static async Task<RoomObject> CrossAsync() =>
        await RoomCompiler.CompileAsync(RoomHarness.BuildRoomModel(HalfCross), HalfCross, await RoomHarness.ContextAsync());

    private static async Task<(int Exit, string Text)> LinkAsync(InMemoryFileSystem fs, string level, params string[] extra)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunLinkAsync(fs, [level, "-no-nav", .. extra, "-out", "/sample/out/x.bsp"], output);
        return (exit, output.ToString());
    }

    private static async Task<(int Exit, string Text)> FlattenAsync(InMemoryFileSystem fs, string level)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunLinkAsync(fs, [level, "--flatten", "-out", "/sample/out/x.vmf"], output);
        return (exit, output.ToString());
    }

    /// <summary>
    /// The 3x3 sample under <c>/sample</c>, its library copied as
    /// <c>caves.vmf</c>, both packed unlit beside themselves, and two level
    /// files: <c>multi.yaml</c> of both libraries, <c>alias.yaml</c> of one
    /// with an alias.
    /// </summary>
    private static async Task<InMemoryFileSystem> PackedAsync()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in Rooms3x3Sample.Build())
        {
            fs.AddFile(Rooted("/sample/" + path), bytes);
            if (path == Rooms3x3Kit.LibraryFile)
            {
                fs.AddFile(Rooted("/sample/caves.vmf"), bytes);
            }
        }

        fs.AddFile(Rooted("/sample/levels/multi.yaml"), Encoding.UTF8.GetBytes(MultiLevel));
        fs.AddFile(
            Rooted("/sample/levels/alias.yaml"),
            Encoding.UTF8.GetBytes("library: ../rooms.vmf\naliases:\n  X: cross\nrows: 1\ncolumns: 2\ngrid:\n  - [X, cross@90]\n"));
        foreach (string library in new[] { "rooms", "caves" })
        {
            using StringWriter output = new();
            int exit = await RoomCommands.RunRoomAsync(
                fs, [], [$"/sample/{library}.vmf", "-game", "/sample", "-nolight", "-out", $"/sample/{library}.roompack"], output);
            Assert.True(exit == Program.ExitSuccess, output.ToString());
        }

        return fs;
    }

    /// <summary>Where the commands look for a rooted path they are given: they resolve against the host.</summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
