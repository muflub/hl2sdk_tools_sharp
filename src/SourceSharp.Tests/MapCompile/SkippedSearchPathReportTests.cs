//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// Every command that mounts a game says which search paths mounted nothing,
/// and where it looked. They used to be dropped without a word, so a compile
/// whose Steam content never mounted produced water, glass and invisible-tool
/// brushes as solid walls with nothing on the console to say why.
/// </summary>
public sealed class SkippedSearchPathReportTests
{
    /// <summary>A gameinfo whose content is itself, plus two lines that name nothing.</summary>
    private const string GameInfoText = """
        "GameInfo"
        {
        	game	"Skips"
        	FileSystem
        	{
        		SearchPaths
        		{
        			game	|gameinfo_path|.
        			game	|gameinfo_path|missing_dir
        			game	|gameinfo_path|missing.vpk
        		}
        	}
        }
        """;

    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    // ---- the line itself ----------------------------------------------------

    /// <summary>
    /// Where it looked is spelled as the host spells it, root and all: the
    /// line used to say <c>games/mod/gone</c> for <c>/games/mod/gone</c>.
    /// </summary>
    [Fact]
    public async Task EachSkippedPathIsOneWarningLineNamingWhereItLooked()
    {
        GameContentMounter.Result mounted = new(new ContentFileSystem([]), ["|gameinfo_path|gone"])
        {
            SkippedPaths = [VPath.Create("games/mod/gone")],
        };
        using StringWriter output = new();

        await VbspCommand.WriteSkippedAsync(mounted, "ssmap vbsp", output);

        Assert.Equal(
            "ssmap vbsp: warning: search path \"|gameinfo_path|gone\" mounted nothing (looked in "
            + Path.GetFullPath("/games/mod/gone") + ")"
            + Environment.NewLine,
            output.ToString());
    }

    /// <summary>
    /// A result built without paths (a host's own, or an older caller's)
    /// still reports its spellings rather than throwing on the shorter list.
    /// </summary>
    [Fact]
    public async Task ASkipWithoutAPathIsStillReported()
    {
        GameContentMounter.Result mounted = new(new ContentFileSystem([]), ["hl2/gone.vpk"]);
        using StringWriter output = new();

        await VbspCommand.WriteSkippedAsync(mounted, "ssmap all", output);

        Assert.Contains("search path \"hl2/gone.vpk\" mounted nothing (looked in (unknown))", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingSkippedPrintsNothing()
    {
        using StringWriter output = new();
        await VbspCommand.WriteSkippedAsync(new(new ContentFileSystem([]), []), "ssmap vbsp", output);
        Assert.Equal(string.Empty, output.ToString());
    }

    // ---- each command -------------------------------------------------------

    [Fact]
    public async Task VbspReportsTheSkippedSearchPaths()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();

        await VbspCommand.RunAsync(fs, ["-game", "/game", "/game/maps/box.vmf"], output);

        AssertReported(output.ToString(), "ssmap vbsp");
    }

    [Fact]
    public async Task VradReportsTheSkippedSearchPaths()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter vbsp = new();
        Assert.Equal(Program.ExitSuccess, await VbspCommand.RunAsync(fs, ["-game", "/game", "/game/maps/box.vmf"], vbsp));
        using StringWriter output = new();

        await VradCommand.RunAsync(fs, ["-game", "/game", "-bounce", "1", "/game/maps/box"], output);

        AssertReported(output.ToString(), "ssmap vrad");
    }

    [Fact]
    public async Task RoomReportsTheSkippedSearchPaths()
    {
        InMemoryFileSystem fs = Game();
        RoomDefinition hub = RoomHarness.Hub();
        fs.AddFile(Rooted("/game/maps/hub.vmf"), RoomHarness.BuildRoomModel(hub).ToBytes());
        fs.AddFile(Rooted("/game/maps/hub.vmf.roomdef.json"), Encoding.UTF8.GetBytes(RoomDefinitionJson.Write(hub)));
        using StringWriter output = new();

        await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "/game/maps/hub.vmf", "-out", "/rooms"], output);

        AssertReported(output.ToString(), "ssmap room");
    }

    [Fact]
    public async Task AllReportsTheSkippedSearchPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "skipped-" + Guid.NewGuid().ToString("N"));
        string mod = Path.Combine(root, "mod");
        Directory.CreateDirectory(Path.Combine(mod, "maps"));
        try
        {
            File.WriteAllText(Path.Combine(mod, "gameinfo.txt"), GameInfoText);
            string vmf = Path.Combine(mod, "maps", "box.vmf");
            File.WriteAllText(vmf, TestMapCatalog.SealedRoom().Write());
            using StringWriter output = new();

            await AllCommand.RunAsync(
                new PhysicalFileSystem("/"), [], ["-game", mod, "-cooker", "none", vmf], output);

            string text = output.ToString();
            string where = Path.Combine(mod, "missing_dir");
            Assert.Contains($"ssmap all: warning: search path \"|gameinfo_path|missing_dir\" mounted nothing (looked in {where})", text, StringComparison.Ordinal);
            Assert.Contains("ssmap all: warning: search path \"|gameinfo_path|missing.vpk\" mounted nothing", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        fs.AddText(Rooted("/game/maps/box.vmf"), TestMapCatalog.SealedRoom().Write());
        return fs;
    }

    private static void AssertReported(string output, string tool)
    {
        Assert.Contains(
            $"{tool}: warning: search path \"|gameinfo_path|missing_dir\" mounted nothing (looked in {Path.GetFullPath("/game/missing_dir")})",
            output, StringComparison.Ordinal);
        Assert.Contains(
            $"{tool}: warning: search path \"|gameinfo_path|missing.vpk\" mounted nothing (looked in {Path.GetFullPath("/game/missing.vpk")})",
            output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"|gameinfo_path|.\" mounted nothing", output, StringComparison.Ordinal);
    }
}
