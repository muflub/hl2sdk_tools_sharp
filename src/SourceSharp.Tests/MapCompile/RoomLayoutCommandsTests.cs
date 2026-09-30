//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Io;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap layout</c> over several libraries and heights (the rooms design,
/// PR 21, 17.9 for one-cell rooms): <c>key=path</c> operands and the
/// <c>libraries</c> output in the shortest spelling, <c>-large</c>,
/// <c>-group</c> and <c>-max-height</c>, the budget and the pack lookup over
/// every library, every message the verb adds; that a one-library layout is
/// the file it always was; and <c>ssmap rooms</c> listing a room's height.
/// </summary>
/// <remarks>
/// Every path the commands are given is <see cref="Rooted"/>, and every path
/// a message is expected to print is the host's full path
/// (<see cref="Path.GetFullPath(string)"/>), which on Windows carries a drive
/// letter.
/// </remarks>
public sealed class RoomLayoutCommandsTests
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

    private static readonly RoomFacing[] All = [RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY];

    /// <summary>The base library: the sample's five kinds.</summary>
    private static RoomDefinition[] BaseRooms() =>
    [
        RoomHarness.WalkableRoom("cross", All),
        RoomHarness.WalkableRoom("tee", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY),
        RoomHarness.WalkableRoom("corner", RoomFacing.PositiveX, RoomFacing.PositiveY),
        RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        RoomHarness.WalkableRoom("end", RoomFacing.PositiveX),
    ];

    /// <summary>
    /// The halls library: a hall of the base's name, and tall rooms of two
    /// heights (two and three cells), every socket set a group needs.
    /// </summary>
    private static RoomDefinition[] HallRooms() =>
    [
        RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        RoomHarness.WalkableRoom("nave", All) with { Height = 512 },
        RoomHarness.WalkableRoom("aisle", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY) with { Height = 512 },
        RoomHarness.WalkableRoom("apse", RoomFacing.PositiveX, RoomFacing.PositiveY) with { Height = 512 },
        RoomHarness.WalkableRoom("tower", All) with { Height = 768 },
        RoomHarness.WalkableRoom("belfry", RoomFacing.PositiveX, RoomFacing.NegativeX) with { Height = 768 },
    ];

    // ---- one library: the file it always was ----------------------------------------------------

    /// <summary>
    /// The checked-in 3x3 library laid out by this build writes, for several
    /// seeds, the bytes the build before height-aware layout wrote (their
    /// SHA-256, taken from that build's <c>ssmap layout</c>), and
    /// <c>-large 0</c> and a <c>-max-height</c> no room reaches change no
    /// level (only the header, which names the limit).
    /// </summary>
    [RepoSourceFact("samples/rooms-3x3/rooms.vmf")]
    public async Task AOneLibraryLayoutIsTheFileItAlwaysWas()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Rooted("/s/rooms.vmf"), await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("samples/rooms-3x3/rooms.vmf")!));
        (ulong Seed, string Sha)[] golden =
        [
            (1, "6e64000966ec8cf5cfea63a552858e99692a9b87054518e82476a1dabcad9a72"),
            (2, "e156afb5c5251a1687ef126bc0d213135705c04a69fe7ffeedf64776ba6a241a"),
            (3, "bb26021e8075782bd941e1e692a0e649ebe5c26cc0e5970cdb62f7aa9c9fa695"),
            (7, "8ddd2bb94b7be124927355d6b9c06a270058a487a805788255c9ec2c380d0adb"),
            (42, "19bcdf2d2135af756d6e60bbc3b4b6749f44f00cc1158ce348b0f3e20189f566"),
        ];
        foreach ((ulong seed, string sha) in golden)
        {
            string[] args = ["/s/rooms.vmf", "-rows", "4", "-columns", "5", "-seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "-empty", "0.25"];
            byte[] plain = await LayoutBytesAsync(fs, [.. args, "-out", "/s/plain.yaml"]);
            Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(plain)));
            Assert.Equal(plain, await LayoutBytesAsync(fs, [.. args, "-large", "0", "-group", "5", "-out", "/s/zero.yaml"]));

            string limited = Encoding.UTF8.GetString(await LayoutBytesAsync(fs, [.. args, "-max-height", "300", "-out", "/s/limited.yaml"]));
            string[] a = Encoding.UTF8.GetString(plain).Split('\n'), b = limited.Split('\n');
            Assert.Equal(a[0] + ", rooms at most 300 tall", b[0]);
            Assert.Equal(a[1..], b[1..]);
        }
    }

    // ---- several libraries ------------------------------------------------------------------------

    /// <summary>
    /// Operands <c>key=path</c> and a bare path (keyed by its stem) write a
    /// level naming its libraries under <c>libraries</c> in operand order,
    /// relative to the level, each cell in its shortest spelling (the hall
    /// both libraries have qualified, every other room bare); the same file
    /// every time; and it flattens, the proof every room is reached. Without
    /// <c>-large</c> no tall room is placed.
    /// </summary>
    [Fact]
    public async Task SeveralLibrariesWriteALevelOfThemAll()
    {
        InMemoryFileSystem fs = Game();
        string[] args = ["base=/game/maps/base.vmf", "/game/maps/halls.vmf", "-rows", "3", "-columns", "4", "-seed", "5"];
        string text = Encoding.UTF8.GetString(await LayoutBytesAsync(fs, [.. args, "-out", "/game/levels/a.yaml"]));
        Assert.Equal(text, Encoding.UTF8.GetString(await LayoutBytesAsync(fs, [.. args, "-out", "/game/levels/b.yaml"])));
        Assert.StartsWith(
            "# generated by ssmap layout: seed 5, 3 rows x 4 columns, 0 empty cell(s)\n"
            + "# the grid's first line is the north row; each line runs west to east\n"
            + "libraries:\n  base: ../maps/base.vmf\n  halls: ../maps/halls.vmf\nrows: 3\n",
            text,
            StringComparison.Ordinal);

        LevelGrid level = LevelYaml.Parse(text, "a");
        string[] spelled = [.. level.Placed.Select(p => p.Cell.Room)];
        Assert.DoesNotContain("hall", spelled);
        Assert.All(spelled, s => Assert.Contains(s, (string[])["cross", "tee", "corner", "end", "base.hall", "halls.hall"]));

        using StringWriter link = new();
        Assert.True(await RoomCommands.RunLinkAsync(fs, ["/game/levels/a.yaml", "--flatten"], link) == Program.ExitSuccess, link.ToString());
    }

    /// <summary>
    /// With <c>-large</c> the level places tall rooms in groups, names the
    /// share in its header, and still flattens (every group reached, every
    /// joint matched across heights); <c>-max-height</c> keeps the towers
    /// out; the file is the same on every run.
    /// </summary>
    [Fact]
    public async Task ALargeShareGroupsTheTallRooms()
    {
        InMemoryFileSystem fs = Game();
        string[] args = ["base=/game/maps/base.vmf", "halls=/game/maps/halls.vmf", "-rows", "4", "-columns", "4", "-seed", "3", "-large", "0.4", "-group", "4"];
        string text = Encoding.UTF8.GetString(await LayoutBytesAsync(fs, [.. args, "-out", "/game/levels/big.yaml"]));
        Assert.Equal(text, Encoding.UTF8.GetString(await LayoutBytesAsync(fs, [.. args, "-out", "/game/levels/again.yaml"])));
        Assert.StartsWith("# generated by ssmap layout: seed 3, 4 rows x 4 columns, 0 empty cell(s), large share 0.4, groups of 4\n", text, StringComparison.Ordinal);
        string[] tall = ["nave", "aisle", "apse", "tower", "belfry"];
        LevelGrid level = LevelYaml.Parse(text, "big");
        Assert.Equal(6, level.Placed.Count(p => tall.Contains(p.Cell.Room)));

        using StringWriter link = new();
        Assert.True(await RoomCommands.RunLinkAsync(fs, ["/game/levels/big.yaml", "--flatten"], link) == Program.ExitSuccess, link.ToString());

        LevelGrid low = LevelYaml.Parse(
            Encoding.UTF8.GetString(await LayoutBytesAsync(fs, [.. args, "-max-height", "600", "-out", "/game/levels/low.yaml"])), "low");
        Assert.DoesNotContain(low.Placed, p => p.Cell.Room is "tower" or "belfry");
        Assert.Equal(6, low.Placed.Count(p => tall.Contains(p.Cell.Room)));
    }

    /// <summary>A single <c>key=path</c> operand writes <c>libraries</c> with that key, since only then does the key reach the link.</summary>
    [Fact]
    public async Task AKeyedOperandWritesLibrariesEvenAlone()
    {
        InMemoryFileSystem fs = Game();
        string text = Encoding.UTF8.GetString(await LayoutBytesAsync(
            fs, ["main=/game/maps/base.vmf", "-rows", "2", "-columns", "2", "-seed", "1", "-out", "/game/levels/one.yaml"]));
        Assert.Contains("libraries:\n  main: ../maps/base.vmf\nrows: 2\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("library:", text, StringComparison.Ordinal);
    }

    /// <summary>The messages of the operands and the new options, each a usage error.</summary>
    [Theory]
    [InlineData(new[] { "a=/game/maps/base.vmf", "A=/game/maps/halls.vmf" }, "ssmap layout: the key A is given twice.")]
    [InlineData(new[] { "a=/game/maps/base.vmf", "/game/maps/1st.vmf" }, "ssmap layout: /game/maps/1st.vmf gives no key (1st is not a key); write key=/game/maps/1st.vmf.")]
    [InlineData(new[] { "/game/maps/base.vmf", "-large", "1" }, "ssmap layout: -large is a share of the occupied cells, at least 0 and below 1")]
    [InlineData(new[] { "/game/maps/base.vmf", "-large", "lots" }, "ssmap layout: -large is a share of the occupied cells, at least 0 and below 1")]
    [InlineData(new[] { "/game/maps/base.vmf", "-group", "0" }, "ssmap layout: -group is a whole number of rooms from 1")]
    [InlineData(new[] { "/game/maps/base.vmf", "-max-height", "0" }, "ssmap layout: -max-height is a whole number of units from 1")]
    [InlineData(new[] { "/game/maps/base.vmf", "-max-height", "300.5" }, "ssmap layout: -max-height is a whole number of units from 1")]
    [InlineData(new[] { "a=/game/maps/base.vmf", "b=/game/maps/halls.vmf", "-rooms", "c=/p.roompack" }, "ssmap layout: -rooms names library c, which the level does not list; its libraries are a and b.")]
    public async Task BadOperandsAndOptionsAreUsageErrors(string[] operands, string expected)
    {
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLayoutAsync(Game(), [.. operands, "-rows", "2", "-columns", "2", "-seed", "1"], output));
        Assert.Equal(expected + Environment.NewLine, output.ToString());
    }

    /// <summary>Two keys naming one file are refused, naming the file as the host resolved it.</summary>
    [Fact]
    public async Task TwoKeysOfOneFileAreRefused()
    {
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunLayoutAsync(
            Game(), ["a=/game/maps/base.vmf", "b=/game/maps/../maps/base.vmf", "-rows", "2", "-columns", "2", "-seed", "1"], output));
        Assert.Equal($"ssmap layout: libraries a and b name the same file, {Path.GetFullPath("/game/maps/base.vmf")}." + Environment.NewLine, output.ToString());
    }

    /// <summary>
    /// What would make the level unlinkable is refused before any draw: a
    /// library of another cell size (17.5's text, naming the paths as the
    /// level writes them), and a room whose name reads as another key's
    /// (17.2's dotted-name text). A refusal of the generator's is printed
    /// without a library path, since it is the level's, and a grid past the
    /// cap is refused before any library is read.
    /// </summary>
    [Fact]
    public async Task WhatTheLinkWouldRefuseIsRefused()
    {
        InMemoryFileSystem fs = Game();
        fs.AddFile(Rooted("/game/maps/wide.vmf"), RoomHarness.LibraryVmf(new RoomDefinition("wide", 384, new SocketKit(96, 352, 16), [new RoomSocket(RoomFacing.PositiveX, "east")])).ToBytes());
        fs.AddFile(Rooted("/game/maps/dotted.vmf"), RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("base.x", RoomFacing.PositiveX)).ToBytes());
        string[] grid = ["-rows", "2", "-columns", "2", "-seed", "1", "-out", "/game/levels/l.yaml"];

        using (StringWriter output = new())
        {
            Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, ["base=/game/maps/base.vmf", "/game/maps/wide.vmf", .. grid], output));
            Assert.Equal(
                "ssmap layout: libraries base (../maps/base.vmf) and wide (../maps/wide.vmf) are built for different grids: cell_size 256 against 384;"
                + " the rooms of a level share one cell size." + Environment.NewLine,
                output.ToString());
        }

        using (StringWriter output = new())
        {
            Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, ["base=/game/maps/base.vmf", "/game/maps/dotted.vmf", .. grid], output));
            Assert.Equal(
                "ssmap layout: room \"base.x\" of library dotted reads as room \"x\" of library base; rename the library key base." + Environment.NewLine,
                output.ToString());
        }

        using (StringWriter output = new())
        {
            Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(
                fs, ["base=/game/maps/base.vmf", "/game/maps/halls.vmf", "-rows", "1", "-columns", "5", "-seed", "9", "-large", "0.8", "-group", "1"], output));
            Assert.Equal(
                "ssmap layout: layout: no level of 1x5 with seed 9 covers 4 cells with large rooms in groups of 1; lower -large or grow the grid."
                + Environment.NewLine,
                output.ToString());
        }

        using (StringWriter output = new())
        {
            Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(
                fs, ["base=/nowhere/base.vmf", "/nowhere/halls.vmf", "-rows", "512", "-columns", "512", "-seed", "1"], output));
            Assert.Equal("ssmap layout: a 512x512 grid has more than 4096 cells (Parameter 'options')" + Environment.NewLine, output.ToString());
        }

        using (StringWriter output = new())
        {
            Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, ["base=/game/maps/base.vmf", "/game/maps/none.vmf", .. grid], output));
            Assert.StartsWith($"ssmap layout: {Path.GetFullPath("/game/maps/none.vmf")}: ", output.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The budget covers every library: each key's pack is found beside its
    /// library (or by <c>-rooms key=pack</c>), the level's own entities are
    /// the singleton rule's over every library (a sun only the second has is
    /// the level's, D29), and a budget that cannot hold them is refused
    /// counting them; a missing pack names its key.
    /// </summary>
    [Fact]
    public async Task TheBudgetCoversEveryLibrary()
    {
        InMemoryFileSystem fs = Game();
        VmfDocument halls = RoomHarness.LibraryVmf(HallRooms());
        halls.Chunks.Add(Sun());
        fs.AddFile(Rooted("/game/maps/halls.vmf"), halls.ToBytes());
        string[] args = ["base=/game/maps/base.vmf", "halls=/game/maps/halls.vmf", "-rows", "1", "-columns", "3", "-seed", "2"];

        using (StringWriter output = new())
        {
            Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, [.. args, "-entity-budget", "4"], output));
            Assert.Equal(
                $"ssmap layout: -entity-budget counts the rooms' entities, and there is no room pack {Path.GetFullPath("/game/maps/base.roompack")} for library base;"
                + " compile the library with ssmap room, or point -rooms base= at its pack." + Environment.NewLine,
                output.ToString());
        }

        foreach (string key in new[] { "base", "halls" })
        {
            using StringWriter compile = new();
            Assert.True(
                await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "-nolight", $"/game/maps/{key}.vmf", "-out", $"/packs/{key}.roompack"], compile) == Program.ExitSuccess,
                compile.ToString());
        }

        string[] packs = ["-rooms", "base=/packs/base.roompack", "-rooms", "halls=/packs/halls.roompack"];
        using (StringWriter output = new())
        {
            // A room of either library is one edict; with the second
            // library's sun the level's own entities are one more.
            Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, [.. args, .. packs, "-entity-budget", "4"], output));
            Assert.Equal(
                "ssmap layout: no level of 1x3 cells keeps within the entity budget of 4 edicts:"
                + " its 3 room(s) bring at least 5, the worldspawn and the library's own entities included." + Environment.NewLine,
                output.ToString());
        }

        using (StringWriter output = new())
        {
            Assert.True(
                await RoomCommands.RunLayoutAsync(fs, [.. args, .. packs, "-entity-budget", "5", "-out", "/game/levels/budget.yaml"], output) == Program.ExitSuccess,
                output.ToString());
        }

        // A combined pack serves every key; a plain pack given to every key
        // of several is refused with the link's text.
        using (StringWriter compile = new())
        {
            Assert.True(
                await RoomCommands.RunRoomPackAsync(fs, [], ["-cooker", "none", "-nolight", "-out", "/packs/both.roompack", "base=/game/maps/base.vmf", "halls=/game/maps/halls.vmf"], compile)
                    == Program.ExitSuccess,
                compile.ToString());
        }

        using (StringWriter output = new())
        {
            Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, [.. args, "-rooms", "/packs/both.roompack", "-entity-budget", "4"], output));
            Assert.Contains("its 3 room(s) bring at least 5,", output.ToString(), StringComparison.Ordinal);
        }

        using (StringWriter output = new())
        {
            Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, [.. args, "-rooms", "/packs/base.roompack", "-entity-budget", "5"], output));
            string pack = Path.GetFullPath("/packs/base.roompack");
            Assert.Equal(
                $"ssmap layout: room pack {pack} holds one library without a namespace; give it to one key with -rooms base={pack}." + Environment.NewLine,
                output.ToString());
        }
    }

    // ---- ssmap rooms ------------------------------------------------------------------------------

    /// <summary><c>ssmap rooms</c> lists each room's own box: a tall room's height, a cube's line as it always was.</summary>
    [Fact]
    public async Task TheListingGivesEachRoomItsHeight()
    {
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(Game(), ["/game/maps/halls.vmf"], output));
        string[] lines = output.ToString().Split('\n');
        Assert.Contains("hall: cell at (0, 0, 0), 256 x 256 x 256, 2 door(s)", lines);
        Assert.Contains(lines, l => l.StartsWith("nave: cell at (", StringComparison.Ordinal) && l.EndsWith("), 256 x 256 x 512, 4 door(s)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("tower: cell at (", StringComparison.Ordinal) && l.EndsWith("), 256 x 256 x 768, 4 door(s)", StringComparison.Ordinal));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static async Task<byte[]> LayoutBytesAsync(InMemoryFileSystem fs, string[] args)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunLayoutAsync(fs, args, output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());
        string target = args[Array.IndexOf(args, "-out") + 1];
        return fs.GetBytes(VPath.Create(Rooted(target))) ?? throw new FileNotFoundException(target);
    }

    /// <summary>A game with the harness materials and the two libraries.</summary>
    private static InMemoryFileSystem Game()
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Plain}.vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Trigger}.vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        fs.AddFile(Rooted("/game/maps/base.vmf"), RoomHarness.LibraryVmf(BaseRooms()).ToBytes());
        fs.AddFile(Rooted("/game/maps/halls.vmf"), RoomHarness.LibraryVmf(HallRooms()).ToBytes());
        return fs;
    }

    /// <summary>A library sun in the gaps between rooms.</summary>
    private static VmfChunk Sun()
    {
        VmfChunk entity = new("entity");
        entity.AddKey("id", "900101");
        entity.AddKey("classname", "light_environment");
        entity.AddKey("origin", "-64 -64 128");
        entity.AddKey("angles", "-45 30 0");
        return entity;
    }

    /// <summary>Where the commands look for a rooted path they are given: they resolve against the host.</summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
