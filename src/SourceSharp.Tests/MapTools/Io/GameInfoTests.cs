//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Text;

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// <c>gameinfo.txt</c>: the search-path list, its order, and what mounting it
/// produces.
/// </summary>
public class GameInfoTests
{
    /// <summary>
    /// The shape of a real <c>gameinfo.txt</c>, trimmed: comments, unquoted and
    /// quoted values, <c>+</c>-joined tags, both <c>|…|</c> tokens, a wildcard
    /// line, and tags a compile must NOT mount.
    /// </summary>
    private const string RealShapedGameInfo = """
        "GameInfo"
        {
            game	"Half-Life 2 DM"
            type multiplayer_only

            FileSystem
            {
                SteamAppId				320

                // Search paths are relative to the base directory.
                SearchPaths
                {
                    game+mod			hl2mp/custom/*
                    game+mod			hl2mp/hl2mp_pak.vpk
                    game				|all_source_engine_paths|hl2/hl2_misc.vpk
                    mod+mod_write+default_write_path		|gameinfo_path|.
                    game+game_write		hl2mp
                    gamebin				hl2mp/bin
                    game				|all_source_engine_paths|hl2
                    platform			|all_source_engine_paths|platform
                    game+download	hl2mp/download
                }
            }
        }
        """;

    [Fact]
    public void TheGameNameIsRead()
    {
        Assert.Equal("Half-Life 2 DM", GameInfo.Parse(RealShapedGameInfo).Game);
    }

    [Fact]
    public void TheSteamAppIdIsRead()
    {
        Assert.Equal(320, GameInfo.Parse(RealShapedGameInfo).SteamAppId);
    }

    [Fact]
    public void EverySearchPathLineIsRead()
    {
        Assert.Equal(9, GameInfo.Parse(RealShapedGameInfo).SearchPaths.Count);
    }

    [Fact]
    public void SearchPathOrderIsTheFilesOrder()
    {
        // The file's order IS the resolution order, first match wins, and real
        // files order it deliberately -- VPKs before the loose directories
        // holding the same content.
        Assert.Equal(
            [
                "hl2mp/custom/*",
                "hl2mp/hl2mp_pak.vpk",
                "|all_source_engine_paths|hl2/hl2_misc.vpk",
                "|gameinfo_path|.",
                "hl2mp",
                "hl2mp/bin",
                "|all_source_engine_paths|hl2",
                "|all_source_engine_paths|platform",
                "hl2mp/download",
            ],
            GameInfo.Parse(RealShapedGameInfo).SearchPaths.Select(static p => p.Location));
    }

    [Fact]
    public void JoinedTagsAreSplit()
    {
        GameInfoSearchPath path = GameInfo.Parse(RealShapedGameInfo).SearchPaths[3];

        Assert.Equal(["mod", "mod_write", "default_write_path"], path.Kinds);
    }

    [Fact]
    public void ATagIsMatchedWhateverItsCasing()
    {
        Assert.True(GameInfo.Parse(RealShapedGameInfo).SearchPaths[1].HasKind("GAME"));
    }

    [Fact]
    public void AWildcardLineIsRecognised()
    {
        Assert.True(GameInfo.Parse(RealShapedGameInfo).SearchPaths[0].IsWildcard);
    }

    [Fact]
    public void ACommentIsNotASearchPath()
    {
        Assert.DoesNotContain(
            GameInfo.Parse(RealShapedGameInfo).SearchPaths,
            static p => p.Location.Contains("relative", StringComparison.Ordinal));
    }

    [Fact]
    public void ABlockCalledSearchPathsSomewhereElseIsIgnored()
    {
        // The block PATH is checked, not just the block name: a mod with its
        // own SearchPaths block elsewhere in the file must not contribute
        // mounts.
        GameInfo info = GameInfo.Parse("""
            "GameInfo"
            {
                SomethingElse
                {
                    SearchPaths
                    {
                        game	not-a-real-search-path
                    }
                }
            }
            """);

        Assert.Empty(info.SearchPaths);
    }

    [Fact]
    public void TheGameInfoPathTokenExpands()
    {
        Assert.Equal(
            "home/me/mod/.",
            GameInfo.ExpandTokens("|gameinfo_path|.", "home/me/mod", "base"));
    }

    [Fact]
    public void TheAllSourceEnginePathsTokenExpands()
    {
        Assert.Equal(
            "base/hl2/hl2_misc.vpk",
            GameInfo.ExpandTokens("|all_source_engine_paths|hl2/hl2_misc.vpk", "mod", "base"));
    }

    [Fact]
    public void ADriveLetterIsStripped()
    {
        // This repo's own toolgame gameinfo.txt writes Z:/... for wine. A
        // reader that kept it would look for a directory called "Z:".
        Assert.Equal("/home/me/mod", GameInfo.ExpandTokens("Z:/home/me/mod", "x", "y"));
    }

    [Fact]
    public void BackslashesBecomeForwardSlashes()
    {
        Assert.Equal("hl2/hl2_misc.vpk", GameInfo.ExpandTokens(@"hl2\hl2_misc.vpk", "x", "y"));
    }

    [Fact]
    public async Task ALoadedGameInfoIsRecordedAsADependency()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("mod/gameinfo.txt", RealShapedGameInfo);
        RecordingFileSystem recording = new(disk);

        await GameInfo.LoadAsync(recording, VPath.Create("mod/gameinfo.txt"));

        Assert.Equal(
            DependencyKind.Read,
            recording.Recorder.Find(VPath.Create("mod/gameinfo.txt"))!.Value.Kind);
    }

    [Fact]
    public async Task MountingSkipsTagsAMapCompileMustNotRead()
    {
        // gamebin is binaries, platform is the UI's files, and download is
        // whatever a server sent -- which the engine searches LAST precisely so
        // it cannot override real content. None of the three is content a
        // compile reads.
        GameContentMounter.Result result = await MountRealShaped();

        await using ContentFileSystem content = result.Content;

        Assert.DoesNotContain(content.Mounts, static m => m.Name.Contains("bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MountingKeepsTheFilesOrder()
    {
        GameContentMounter.Result result = await MountRealShaped();

        await using ContentFileSystem content = result.Content;

        Assert.Equal(
            [
                "custom_pak_dir.vpk",
                "base/hl2mp/custom/sub",
                "hl2mp_pak_dir.vpk",
                "base/hl2mp",
                "base/hl2",
            ],
            content.Mounts.Select(static m => m.Name));
    }

    [Fact]
    public async Task TheFirstMountedSearchPathWins()
    {
        // The end-to-end version of the resolution-order gate: the same
        // material in a custom VPK, the mod pak and the shared hl2 directory,
        // resolved through a gameinfo.txt rather than a hand-ordered list.
        GameContentMounter.Result result = await MountRealShaped();

        await using ContentFileSystem content = result.Content;

        using IMemoryOwner<byte>? owner = await content.ReadAsync(VPath.Create("materials/shared.vmt"));

        Assert.Equal("from custom", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    [Fact]
    public async Task ASharedContentFileStillResolves()
    {
        GameContentMounter.Result result = await MountRealShaped();

        await using ContentFileSystem content = result.Content;

        Assert.Equal("base/hl2", (await content.ResolveAsync(VPath.Create("materials/hl2only.vmt")))!.Value.Mount);
    }

    [Fact]
    public async Task ASearchPathThatIsNotThereIsSkippedAndReported()
    {
        // Real gameinfo.txt files list content an install may not have. The
        // engine skips it; refusing would make the common install unmountable.
        // But a compile that cannot find its materials has to be able to say
        // which search paths were missing rather than leave the user guessing.
        GameContentMounter.Result result = await MountRealShaped();

        await using ContentFileSystem content = result.Content;

        // Reported AS THE FILE WROTE IT, not as the expanded path it turned
        // into, so the message names a line a user can go and look at.
        Assert.Equal(
            ["|all_source_engine_paths|hl2/hl2_misc.vpk", "|gameinfo_path|.", "hl2mp/download"],
            result.Skipped);
    }

    [Fact]
    public async Task AWildcardMountsItsVpkBeforeItsSubdirectories()
    {
        GameContentMounter.Result result = await MountRealShaped();

        await using ContentFileSystem content = result.Content;

        Assert.Equal("custom_pak_dir.vpk", content.Mounts[0].Name);
    }

    [Fact]
    public async Task AWildcardDoesNotMountANumberedArchivePartOnItsOwn()
    {
        // "<base>_000.vpk" is the data half of a multi-part archive, not an
        // archive. Opening one would fail on its missing directory.
        GameContentMounter.Result result = await MountRealShaped();

        await using ContentFileSystem content = result.Content;

        Assert.DoesNotContain(content.Mounts, static m => m.Name.Contains("_000", StringComparison.Ordinal));
    }

    /// <summary>
    /// A game laid out the way <see cref="RealShapedGameInfo"/> describes one:
    /// a custom VPK and subdirectory, a mod pak, and the shared hl2 content.
    /// </summary>
    private static async ValueTask<GameContentMounter.Result> MountRealShaped()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddText("base/hl2/materials/shared.vmt", "from hl2")
            .AddText("base/hl2/materials/hl2only.vmt", "hl2 only")
            .AddText("base/hl2mp/materials/shared.vmt", "from hl2mp loose")
            .AddText("base/hl2mp/custom/sub/materials/in-a-subdirectory.vmt", "sub");

        new VpkFixture().AddText("materials/shared.vmt", "from custom")
            .Write(disk, "base/hl2mp/custom/custom_pak");

        new VpkFixture().AddText("materials/shared.vmt", "from the mod pak")
            .Write(disk, "base/hl2mp/hl2mp_pak");

        // The install is at "base"; |all_source_engine_paths| is the same
        // place; the gameinfo.txt sits in "mod", which holds nothing, so that
        // the skip-and-report path is exercised too.
        return await GameContentMounter.MountAsync(
            disk,
            GameInfo.Parse(RealShapedGameInfo),
            new GameContentRoots(VPath.Create("mod"), VPath.Create("base")));
    }
}
