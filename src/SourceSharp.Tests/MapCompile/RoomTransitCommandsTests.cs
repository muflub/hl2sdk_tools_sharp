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
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.RoomContracts;
using SourceSharp.Tests.MapTools.Io;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// Transition rooms through the CLI (the rooms design, section 11):
/// <c>ssmap layout</c>'s role options and its <c>-sequence</c> run,
/// <c>ssmap rooms</c> listing the roles, and the transit sample built end to
/// end, a run of levels compiled, linked and flattened in both modes.
/// </summary>
public sealed class RoomTransitCommandsTests
{
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    /// <summary>A game holding the transit harness library at <c>/game/maps/rooms.vmf</c>.</summary>
    private static InMemoryFileSystem Game(VmfDocument? library = null)
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), "\"GameInfo\"\n{\n\tgame\t\"Rooms\"\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tgame\t|gameinfo_path|.\n\t\t}\n\t}\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Plain}.vmt"), "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        fs.AddText(Rooted($"/game/materials/{RoomHarness.Trigger}.vmt"),
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        fs.AddFile(Rooted("/game/maps/rooms.vmf"), (library ?? TransitHarness.Library()).ToBytes());
        return fs;
    }

    /// <summary>The transit sample as a game folder at <c>/transit</c>.</summary>
    private static InMemoryFileSystem Sample()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in RoomsTransitSample.Build())
        {
            fs.AddFile(Rooted("/transit/" + path), bytes);
        }

        return fs;
    }

    private static async Task<string> TextAsync(InMemoryFileSystem fs, string path)
    {
        await using Stream stream = await fs.OpenReadAsync(VPath.Create(Rooted(path)));
        using StreamReader reader = new(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<BspData> MapAsync(InMemoryFileSystem fs, string path)
    {
        await using Stream stream = await fs.OpenReadAsync(VPath.Create(Rooted(path)));
        return await BspFile.LoadAsync(stream);
    }

    /// <summary>
    /// <c>-sequence 3 -name run</c> writes three chained levels into
    /// <c>-out</c>'s folder, each what the generator's run makes, naming the
    /// library relative to the folder.
    /// </summary>
    [Fact]
    public async Task ASequenceWritesAChainedRun()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();
        int exit = await RoomCommands.RunLayoutAsync(
            fs, ["/game/maps/rooms.vmf", "-rows", "2", "-columns", "3", "-seed", "5", "-sequence", "3", "-name", "run", "-out", "/levels"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());

        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(TransitHarness.Library());
        LevelGeneratorOptions options = new(2, 3, 5);
        IReadOnlyList<LevelGrid> run = LevelGenerator.GenerateSequence(
            [.. rooms.Select(r => r.Definition)], options, 3, "run", "../game/maps/rooms.vmf", null, [.. rooms.Select(r => r.Role)]);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(
                LevelYaml.Write(run[i], LevelGenerator.Header(options with { Seed = 5 + (ulong)i }, run[i])),
                await TextAsync(fs, $"/levels/run_0{i + 1}.yaml"));
            Assert.Contains($"ssmap layout: wrote {HostPaths.Display(VPath.Create(Rooted($"/levels/run_0{i + 1}.yaml")))}", output.ToString(), StringComparison.Ordinal);
        }

        Assert.Contains("up: none\ndown_map: run_02\n", await TextAsync(fs, "/levels/run_01.yaml"), StringComparison.Ordinal);
        Assert.Contains("up_map: run_01\ndown_map: run_03\n", await TextAsync(fs, "/levels/run_02.yaml"), StringComparison.Ordinal);
        Assert.Contains("down: none\nup_map: run_02\n", await TextAsync(fs, "/levels/run_03.yaml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// One level of a library with roles names its maps, or switches a role
    /// off; without, it is refused with a message that says which option to give.
    /// </summary>
    [Fact]
    public async Task OneLevelOfARoleLibraryNamesItsMaps()
    {
        InMemoryFileSystem fs = Game();
        string[] grid = ["/game/maps/rooms.vmf", "-rows", "1", "-columns", "3", "-seed", "2"];
        using StringWriter refused = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, grid, refused));
        Assert.Contains(
            "the library has role rooms, so the level holds an up room and names its map: give -up-map <map>, or -no-up for a level without one.",
            refused.ToString(), StringComparison.Ordinal);

        using StringWriter down = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunLayoutAsync(fs, [.. grid, "-up-map", "a"], down));
        Assert.Contains("holds a down room and names its map: give -down-map <map>", down.ToString(), StringComparison.Ordinal);

        using StringWriter written = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(fs, [.. grid, "-up-map", "a", "-no-down", "-out", "/levels/l.yaml"], written));
        LevelGrid level = LevelYaml.Parse(await TextAsync(fs, "/levels/l.yaml"), "l");
        Assert.Equal(new LevelTransitions { NoDown = true, UpMap = "a" }, level.Transitions);
        Assert.Single(level.Placed, p => p.Cell.Room == "up");
        Assert.DoesNotContain(level.Placed, p => p.Cell.Room == "down");
    }

    /// <summary>The option combinations <c>ssmap layout</c> refuses as usage, and the bad values.</summary>
    [Theory]
    [InlineData("-sequence", "2")]
    [InlineData("-name", "run")]
    [InlineData("-no-up", "-up-map", "a")]
    [InlineData("-no-down", "-down-map", "a")]
    [InlineData("-sequence", "2", "-name", "run", "-up-map", "a")]
    [InlineData("-sequence", "0", "-name", "run")]
    [InlineData("-transition-distance", "-1", "-no-up", "-no-down")]
    [InlineData("-up-map", "a b", "-no-down")]
    [InlineData("-sequence", "2", "-name", "../run")]
    public async Task BadTransitionOptionsAreUsageErrors(params string[] options)
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();
        Assert.Equal(
            Program.ExitUsage,
            await RoomCommands.RunLayoutAsync(fs, ["/game/maps/rooms.vmf", "-rows", "2", "-columns", "2", "-seed", "1", .. options], output));
    }

    /// <summary>A library without roles writes the level it always wrote, the transition keys only when asked.</summary>
    [Fact]
    public async Task ALibraryWithoutRolesWritesItsLevelsAsBefore()
    {
        VmfDocument library = RoomHarness.LibraryVmf(TransitHarness.Plain, RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX));
        InMemoryFileSystem fs = Game(library);
        string[] args = ["/game/maps/rooms.vmf", "-rows", "2", "-columns", "2", "-seed", "3"];
        using StringWriter plain = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(fs, args, plain));
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        LevelGeneratorOptions options = new(2, 2, 3);
        string relative = Path.GetRelativePath(Path.GetFullPath("."), Path.GetFullPath("/game/maps/rooms.vmf")).Replace('\\', '/');
        LevelGrid expected = LevelGenerator.Generate([.. rooms.Select(r => r.Definition)], options, "level", relative);
        Assert.Equal(LevelYaml.Write(expected, LevelGenerator.Header(options, expected)), plain.ToString());

        using StringWriter keyed = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(fs, [.. args, "-no-up", "-down-map", "next"], keyed));
        Assert.Contains("up: none\ndown_map: next\ngrid:", keyed.ToString(), StringComparison.Ordinal);
    }

    /// <summary><c>ssmap rooms</c> says which rooms have a role; an ordinary room's line is as it was.</summary>
    [Fact]
    public async Task TheListingNamesTheRoles()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/game/maps/rooms.vmf"], output));
        string text = output.ToString();
        Assert.Contains(", 4 door(s), role up\n", text, StringComparison.Ordinal);
        Assert.Contains(", 4 door(s), role down\n", text, StringComparison.Ordinal);
        Assert.Contains("plain: cell at (640, 0, 0), 256 x 256 x 256, 4 door(s)\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The transit sample end to end through the CLI: <c>ssmap room</c>
    /// compiles its library, and each level of its three-level run links in
    /// both modes to a map the loader validation passes, whose entities are
    /// the flattened level's whole-map compile's; the top level has no up
    /// transition, the bottom no down one; with <c>-mod-entities</c> and no
    /// navigation the link warns that the mod has no arrival points.
    /// </summary>
    [Fact]
    public async Task TheTransitSampleBuildsEndToEnd()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter output = new();
        Assert.True(
            await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/transit/rooms.vmf", "-out", "/transit/rooms.roompack"], output) == Program.ExitSuccess,
            output.ToString());

        for (int i = 1; i <= RoomsTransitSample.Levels; i++)
        {
            string name = $"transit_0{i}";
            foreach (bool mod in new[] { false, true })
            {
                string[] extra = mod ? ["-mod-entities"] : [];
                string map = $"/out/{name}_{(mod ? "mod" : "stock")}";
                using StringWriter link = new();
                int exit = await RoomCommands.RunLinkAsync(
                    fs, [$"/transit/levels/{name}.yaml", "-rooms", "/transit/rooms.roompack", "-out", map + ".bsp", .. extra], link);
                Assert.True(exit == Program.ExitSuccess, link.ToString());
                using StringWriter flatten = new();
                Assert.Equal(
                    Program.ExitSuccess,
                    await RoomCommands.RunLinkAsync(fs, [$"/transit/levels/{name}.yaml", "--flatten", "-out", map + ".vmf", .. extra], flatten));

                BspData linked = await MapAsync(fs, map + ".bsp");
                Assert.Equal(0, (await BspValidator.CheckAsync(linked, CancellationToken.None)).ErrorCount);
                VmfDocument flat = await VmfDocument.ParseAsync(Encoding.UTF8.GetBytes(await TextAsync(fs, map + ".vmf")));
                VbspContext context = await RoomHarness.ContextAsync(extraFiles: RoomsTransitSample.Build()
                    .Where(f => f.Key.StartsWith("materials/", StringComparison.Ordinal))
                    .ToDictionary(f => f.Key, f => f.Value));
                VbspResult whole = await RoomHarness.CompileAsync(flat, context);
                Assert.Equal(TransitHarness.Comparable(whole.Bsp!), TransitHarness.Comparable(linked));

                int transitions = mod
                    ? TransitHarness.OfClass(linked, LevelTransition.ClassName).Count
                    : TransitHarness.OfClass(linked, "trigger_changelevel").Count;
                Assert.Equal(i is 1 or RoomsTransitSample.Levels ? 1 : 2, transitions);
                Assert.Equal(mod ? 0 : transitions, TransitHarness.OfClass(linked, "info_landmark").Count);
            }
        }

        using StringWriter warned = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunLinkAsync(
                fs, ["/transit/levels/transit_02.yaml", "-rooms", "/transit/rooms.roompack", "-out", "/out/nonav.bsp", "-mod-entities", "-no-nav"], warned));
        Assert.Contains(
            "ssmap link: warning: level transit_02: -mod-entities places players at the arrival and spawn points of the navigation sidecar,"
            + " and the level links without navigation; build the library's navigation, or link without -mod-entities.",
            warned.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The sample's levels are exactly what <c>ssmap layout</c> writes for its
    /// run: <c>-rows 3 -columns 3 -seed 1 -transition-distance 2 -sequence 3
    /// -name transit -out levels</c>.
    /// </summary>
    [Fact]
    public async Task TheSampleLevelsAreWhatSsmapLayoutWrites()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter output = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunLayoutAsync(
                fs,
                ["/transit/rooms.vmf", "-rows", "3", "-columns", "3", "-seed", "1", "-transition-distance", "2", "-sequence", "3", "-name", "transit",
                 "-out", "/transit/levels"],
                output));
        IReadOnlyDictionary<string, byte[]> sample = RoomsTransitSample.Build();
        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(Encoding.UTF8.GetString(sample[$"levels/transit_0{i}.yaml"]), await TextAsync(fs, $"/transit/levels/transit_0{i}.yaml"));
        }
    }
}
