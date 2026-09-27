//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapCompile;

/// <summary>
/// Where <c>lights.rad</c> comes from when the game's search paths do not
/// have it: stock vrad's last resort, <c>lights.rad</c> beside the tool.
/// </summary>
/// <remarks>
/// <para>
/// Stock vrad looks for <c>lights.rad</c> through the game's search paths and,
/// failing that, says "Trying VRAD BIN directory instead..." and reads the
/// one beside <c>vrad.exe</c>. Steam installs the tools, and that file, in
/// the app's <c>bin</c> folder, which is not a search path. So for a Steam
/// game the texlights stock compiles with come from <c>bin/lights.rad</c>, and
/// a compile that only searched the game mounted none: every texlight surface
/// went unlit, with only a "Couldn't open texlight file" warning to show for
/// it.
/// </para>
/// <para>
/// This tool does not live in a Steam <c>bin</c> folder, so the fallback
/// looks where the stock tool would have been:
/// </para>
/// <list type="number">
/// <item><description>beside this tool, which is stock's rule taken literally;</description></item>
/// <item><description>
/// <c>bin/lights.rad</c> in the Steam install of the game's own app
/// (<c>SteamAppId</c>), then of each app its <c>|appid_N|</c> search paths
/// mount, in their order.
/// </description></item>
/// </list>
/// <para>
/// It lives in the host because it is a decision about where this process
/// and the user's Steam library are; the libraries read <c>lights.rad</c> only
/// through the content they are given.
/// </para>
/// </remarks>
public static class LightsRadLocator
{
    /// <summary>The file stock vrad always requires.</summary>
    public const string FileName = "lights.rad";

    /// <summary>
    /// Finds the file to use when the game's content lacks <c>lights.rad</c>.
    /// </summary>
    /// <param name="disk">Where the tool directory and Steam installs are.</param>
    /// <param name="game">The mounted game, or null when none was mounted.</param>
    /// <param name="gameInfo">The game's <c>gameinfo.txt</c>, or null.</param>
    /// <param name="steam">Finds Steam app installs, or null when there is no Steam.</param>
    /// <param name="toolDirectory">The directory this tool runs from.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>
    /// The path to read, or null when the game already has one (nothing to
    /// add) or no fallback exists either (vrad warns as stock does).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="disk"/> or <paramref name="toolDirectory"/> is null.</exception>
    public static async Task<string?> FindFallbackAsync(
        IFileSystem disk,
        IContentFileSystem? game,
        GameInfo? gameInfo,
        ISteamAppLocator? steam,
        string toolDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(toolDirectory);

        if (game is not null
            && await game.ResolveAsync(VPath.Create(FileName), cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        if (await ExistsAsync(disk, Path.Combine(toolDirectory, FileName), cancellationToken).ConfigureAwait(false))
        {
            return Path.Combine(toolDirectory, FileName);
        }

        if (steam is null || gameInfo is null)
        {
            return null;
        }

        foreach (int appId in AppIds(gameInfo))
        {
            VPath? install = await steam.FindInstallDirectoryAsync(appId, cancellationToken).ConfigureAwait(false);
            if (install is not { } dir)
            {
                continue;
            }

            string candidate = Path.Combine(dir.ToString(), "bin", FileName);
            if (await ExistsAsync(disk, candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The Steam apps whose <c>bin</c> folders are searched, in order.</summary>
    /// <param name="gameInfo">The game's <c>gameinfo.txt</c>.</param>
    /// <returns>The game's own app, then each app a search path mounts; each once.</returns>
    public static IReadOnlyList<int> AppIds(GameInfo gameInfo)
    {
        ArgumentNullException.ThrowIfNull(gameInfo);
        List<int> ids = [];
        if (gameInfo.SteamAppId > 0)
        {
            ids.Add(gameInfo.SteamAppId);
        }

        foreach (GameInfoSearchPath path in gameInfo.SearchPaths)
        {
            if (path.TryGetAppId(out int appId, out _) && appId > 0 && !ids.Contains(appId))
            {
                ids.Add(appId);
            }
        }

        return ids;
    }

    /// <summary>
    /// Adds the fallback to a command's loose files when the game lacks
    /// <c>lights.rad</c>, and says which file it used, as stock does.
    /// </summary>
    internal static async Task AddFallbackAsync(
        VradCommand.LooseFileContent content,
        IFileSystem disk,
        IContentFileSystem? game,
        GameInfo? gameInfo,
        ISteamAppLocator? steam,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string? path = await FindFallbackAsync(disk, game, gameInfo, steam, AppContext.BaseDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (path is null)
        {
            return;
        }

        content.Add(FileName, path);
        await output.WriteLineAsync($"{FileName} is not in the game's search paths; using {path}").ConfigureAwait(false);
    }

    private static async ValueTask<bool> ExistsAsync(IFileSystem disk, string path, CancellationToken cancellationToken) =>
        VPath.TryCreate(path, out VPath vpath) && !vpath.IsEmpty
        && await disk.ExistsAsync(vpath, cancellationToken).ConfigureAwait(false);
}
