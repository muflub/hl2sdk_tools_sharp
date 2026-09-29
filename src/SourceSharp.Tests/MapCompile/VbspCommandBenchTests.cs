//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text.RegularExpressions;

using SourceSharp.MapCompile;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap vbsp --bench</c>: the stage wall times after the compile, from
/// the progress the compile already reports, and nothing else changed.
/// </summary>
public sealed class VbspCommandBenchTests
{
    private const string GameInfoText = """
        "GameInfo"
        {
        	game	"Bench"
        	FileSystem
        	{
        		SearchPaths
        		{
        			game	|gameinfo_path|.
        		}
        	}
        }
        """;

    [Fact]
    public async Task BenchPrintsTheCommandsStagesTheCompilesStagesAndATotal()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();

        int code = await VbspCommand.RunAsync(
            fs, ["-game", "/game", VbspCommand.BenchSwitch, "/game/maps/box.vmf"], output);

        string text = output.ToString();
        Assert.Equal(Program.ExitSuccess, code);
        foreach (string stage in (string[])["vbsp.load", "vbsp.fixups", "vbsp.world.tree", "vbsp.world.faces", "vbsp.end.tables", "vbsp.write"])
        {
            Assert.Matches(new Regex($"(?m)^bench {Regex.Escape(stage)} [0-9]+\\.[0-9]{{3}}s\\r?$"), text);
        }

        Assert.Matches(new Regex(@"(?m)^bench total [0-9]+\.[0-9]{3}s\r?$"), text);

        // The load comes first and the write last, as they ran.
        int load = text.IndexOf("bench vbsp.load ", StringComparison.Ordinal);
        int tree = text.IndexOf("bench vbsp.world.tree ", StringComparison.Ordinal);
        int write = text.IndexOf("bench vbsp.write ", StringComparison.Ordinal);
        Assert.True(load < tree && tree < write, text);
    }

    [Fact]
    public async Task WithoutBenchNothingIsTimedAndTheBytesAreTheSame()
    {
        InMemoryFileSystem plain = Game();
        InMemoryFileSystem timed = Game();
        using StringWriter plainOutput = new();
        using StringWriter timedOutput = new();

        Assert.Equal(Program.ExitSuccess, await VbspCommand.RunAsync(
            plain, ["-game", "/game", "/game/maps/box.vmf"], plainOutput));
        Assert.Equal(Program.ExitSuccess, await VbspCommand.RunAsync(
            timed, ["-game", "/game", VbspCommand.BenchSwitch, "/game/maps/box.vmf"], timedOutput));

        Assert.DoesNotContain("bench ", plainOutput.ToString(), StringComparison.Ordinal);
        foreach (string file in (string[])["/game/maps/box.bsp", "/game/maps/box.prt"])
        {
            Assert.Equal(await ReadAsync(plain, file), await ReadAsync(timed, file));
        }
    }

    /// <summary>The switch is the command's, not stock's: the stock parser never sees it.</summary>
    [Fact]
    public async Task TheSwitchIsNotHandedToTheStockParser()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();

        await VbspCommand.RunAsync(fs, ["-game", "/game", VbspCommand.BenchSwitch, "/game/maps/box.vmf"], output);

        Assert.DoesNotContain("--bench", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("usage:", output.ToString(), StringComparison.Ordinal);
    }

    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    private static async Task<byte[]> ReadAsync(InMemoryFileSystem fs, string path)
    {
        await using Stream stream = await fs.OpenReadAsync(VPath.Create(Rooted(path)));
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static InMemoryFileSystem Game()
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        fs.AddText(Rooted("/game/maps/box.vmf"), TestMapCatalog.SealedRoom().Write());
        return fs;
    }
}
