//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// <see cref="GameContentMounter.Result.SkippedPaths"/>: where each skipped
/// search path was looked for, beside the spelling
/// <see cref="GameContentMounter.Result.Skipped"/> keeps.
/// </summary>
public sealed class SkippedSearchPathTests
{
    /// <summary>
    /// A missing directory, a missing archive and an empty wildcard are each
    /// skipped, and each names the rooted place it was looked for, in the
    /// same order as the spellings. A <c>|appid_N|</c> line that resolved to
    /// the wrong install is only diagnosable from the second list.
    /// </summary>
    [Fact]
    public async Task EachSkippedLocationNamesWhereItWasLookedFor()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem()
            .AddText("games/mod/gameinfo.txt", """
                "GameInfo" { game "M" FileSystem { SearchPaths {
                    game |gameinfo_path|.
                    game |gameinfo_path|missing_dir
                    game |gameinfo_path|missing.vpk
                    game |gameinfo_path|custom/*
                } } }
                """)
            .AddText("games/mod/materials/a.vmt", "x");

        GameContentMounter.Result result = await GameContentMounter.MountAsync(
            fs, VPath.Create("games/mod/gameinfo.txt"), VPath.Create("games"), cancellationToken: CancellationToken.None);
        await using ContentFileSystem content = result.Content;

        Assert.Equal(["|gameinfo_path|missing_dir", "|gameinfo_path|missing.vpk", "|gameinfo_path|custom/*"], result.Skipped);
        Assert.Equal(
            ["games/mod/missing_dir", "games/mod/missing.vpk", "games/mod/custom"],
            result.SkippedPaths.Select(static p => p.Value));
    }

    /// <summary>A mount that skipped nothing has no skipped paths either.</summary>
    [Fact]
    public async Task AMountThatSkippedNothingHasNoSkippedPaths()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem()
            .AddText("mod/gameinfo.txt", "\"GameInfo\" { game \"M\" FileSystem { SearchPaths { game |gameinfo_path|. } } }")
            .AddText("mod/materials/a.vmt", "x");

        GameContentMounter.Result result = await GameContentMounter.MountAsync(
            fs, VPath.Create("mod/gameinfo.txt"), VPath.Empty, cancellationToken: CancellationToken.None);
        await using ContentFileSystem content = result.Content;

        Assert.Empty(result.Skipped);
        Assert.Empty(result.SkippedPaths);
    }
}
