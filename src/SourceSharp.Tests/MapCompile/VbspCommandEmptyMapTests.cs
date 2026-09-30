//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap vbsp</c> on a map with no brushes: a map error the user can
/// read, with the failed exit code, and no file written.
/// </summary>
/// <remarks>
/// Before the check the compile ran on to the world bounds with no model
/// and threw an <see cref="ArgumentOutOfRangeException"/>, which the CLI
/// reports as an internal error with a stack trace: a bug report for what
/// is only an empty map.
/// </remarks>
public sealed class VbspCommandEmptyMapTests
{
    [Fact]
    public async Task AMapWithNoBrushesIsAnErrorThatSaysSo()
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        fs.AddText(Rooted("/game/maps/empty.vmf"), EmptyVmf);
        using StringWriter output = new();

        int exit = await VbspCommand.RunAsync(fs, ["-game", "/game", "/game/maps/empty.vmf"], output);

        Assert.True(exit == VbspCommand.ExitFailed, output.ToString());
        Assert.Contains(
            "Error: the map has no brushes: worldspawn and every brush entity are empty, so there is no world model to build."
            + Environment.NewLine,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.False(await fs.ExistsAsync(VPath.Create(Rooted("/game/maps/empty.bsp"))));
    }

    private const string EmptyVmf = """
        world
        {
        	"id" "1"
        	"classname" "worldspawn"
        }
        entity
        {
        	"id" "2"
        	"classname" "info_player_start"
        	"origin" "0 0 0"
        }
        """;

    private const string GameInfoText = """
        "GameInfo"
        {
        	game	"Written"
        	FileSystem
        	{
        		SearchPaths
        		{
        			game	|gameinfo_path|.
        		}
        	}
        }
        """;

    // The commands resolve a rooted path against the host (/game is D:\game
    // on Windows), so the in-memory files go where that lands.
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
