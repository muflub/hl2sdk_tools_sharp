//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//


using SourceSharp.MapCompile;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>
/// <see cref="LightsRadLocator"/>: stock's last resort for <c>lights.rad</c>,
/// beside the tool and then in each mounted Steam app's <c>bin</c> folder,
/// used only when the game's search paths have none.
/// </summary>
public sealed class LightsRadLocatorTests
{
    private const string GameInfoText =
        "\"GameInfo\"\n{\n\tFileSystem\n\t{\n\t\tSteamAppId\t243750\n\t\tSearchPaths\n\t\t{\n"
        + "\t\t\tgame\t|appid_220|hl2/hl2_misc.vpk\n\t\t\tgame\t|appid_243750|hl2mp/hl2mp_pak.vpk\n"
        + "\t\t\tgame\t|appid_220|hl2/hl2_textures.vpk\n\t\t\tgame\t|gameinfo_path|.\n\t\t}\n\t}\n}\n";

    private sealed class Steam(Dictionary<int, string> installs) : ISteamAppLocator
    {
        public List<int> Asked { get; } = [];

        public ValueTask<VPath?> FindInstallDirectoryAsync(int appId, CancellationToken cancellationToken = default)
        {
            Asked.Add(appId);
            return ValueTask.FromResult(installs.TryGetValue(appId, out string? dir) ? (VPath?)VPath.Create(dir) : null);
        }
    }

    private static async Task<ContentFileSystem> GameAsync(InMemoryFileSystem disk) =>
        new([await DirectoryContentMount.MountAsync(disk, VPath.Create("game"))]);

    [Fact]
    public async Task AGameThatHasLightsRadNeedsNoFallback()
    {
        InMemoryFileSystem disk = new();
        disk.AddText("game/lights.rad", "x 1 1 1 1\n");
        disk.AddText("tool/lights.rad", "y 1 1 1 1\n");
        await using ContentFileSystem game = await GameAsync(disk);

        Assert.Null(await LightsRadLocator.FindFallbackAsync(disk, game, GameInfo.Parse(GameInfoText), null, "tool"));
    }

    [Fact]
    public async Task TheToolsOwnDirectoryComesFirst()
    {
        InMemoryFileSystem disk = new();
        disk.AddText("game/gameinfo.txt", GameInfoText);
        disk.AddText("tool/lights.rad", "y 1 1 1 1\n");
        disk.AddText("sdk/bin/lights.rad", "z 1 1 1 1\n");
        await using ContentFileSystem game = await GameAsync(disk);
        Steam steam = new(new() { [243750] = "sdk" });

        Assert.Equal(
            Path.Combine("tool", "lights.rad"),
            await LightsRadLocator.FindFallbackAsync(disk, game, GameInfo.Parse(GameInfoText), steam, "tool"));
        Assert.Empty(steam.Asked);
    }

    [Fact]
    public async Task ASteamAppsBinFolderIsNextInTheGamesOwnAppFirst()
    {
        InMemoryFileSystem disk = new();
        disk.AddText("game/gameinfo.txt", GameInfoText);
        disk.AddText("hl2/bin/lights.rad", "a 1 1 1 1\n");
        await using ContentFileSystem game = await GameAsync(disk);
        Steam steam = new(new() { [243750] = "sdk", [220] = "hl2" });

        string? found = await LightsRadLocator.FindFallbackAsync(disk, game, GameInfo.Parse(GameInfoText), steam, "tool");

        Assert.Equal(Path.Combine("hl2", "bin", "lights.rad"), found);
        Assert.Equal([243750, 220], steam.Asked);
    }

    [Fact]
    public async Task NothingAnywhereLeavesVradToWarn()
    {
        InMemoryFileSystem disk = new();
        disk.AddText("game/gameinfo.txt", GameInfoText);
        await using ContentFileSystem game = await GameAsync(disk);

        Assert.Null(await LightsRadLocator.FindFallbackAsync(
            disk, game, GameInfo.Parse(GameInfoText), new Steam(new() { [243750] = "sdk" }), "tool"));
        Assert.Null(await LightsRadLocator.FindFallbackAsync(disk, null, null, null, "tool"));
    }

    [Fact]
    public void AppsAreTheGamesOwnThenEachMountedOnceInOrder()
    {
        Assert.Equal([243750, 220], LightsRadLocator.AppIds(GameInfo.Parse(GameInfoText)));
    }
}
