using System.Buffers;
using System.Text;

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// <c>|appid_N|</c> search paths (<c>public/filesystem_init.cpp:694-735</c>)
/// and the Steam library they resolve through, on an in-memory copy of a
/// Steam install's <c>libraryfolders.vdf</c> and app manifests.
/// </summary>
public class AppIdMountTests
{
    /// <summary>
    /// The shape of the file the Steam client writes today: a numbered block
    /// per library with its <c>path</c> and the <c>apps</c> installed there.
    /// App 440 lives in the SECOND library, not the Steam install.
    /// </summary>
    private const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"/steam"
        		"label"		""
        		"contentid"		"7459462487234795232"
        		"apps"
        		{
        			"243750"		"3401809202"
        		}
        	}
        	"1"
        	{
        		"path"		"/games/lib"
        		"apps"
        		{
        			"440"		"32683898869"
        		}
        	}
        }
        """;

    private const string Tf2Manifest = """
        "AppState"
        {
        	"appid"		"440"
        	"name"		"Team Fortress 2"
        	"installdir"		"Team Fortress 2"
        }
        """;

    private const string SdkManifest = """
        "AppState"
        {
        	"appid"		"243750"
        	"installdir"		"Source SDK Base 2013 Multiplayer"
        }
        """;

    private const string AppIdGameInfo = """
        "GameInfo"
        {
            game	"tf by appid"
            FileSystem
            {
                SteamAppId	440
                SearchPaths
                {
                    game+mod			|appid_440|tf
                    game				|APPID_243750|hl2
                }
            }
        }
        """;

    private static InMemoryFileSystem Steam() => new InMemoryFileSystem()
        .AddText("steam/steamapps/libraryfolders.vdf", LibraryFolders)
        .AddText("steam/steamapps/appmanifest_243750.acf", SdkManifest)
        .AddText("games/lib/steamapps/appmanifest_440.acf", Tf2Manifest)
        .AddText("games/lib/steamapps/common/Team Fortress 2/tf/materials/tf.vmt", "from tf")
        .AddText("steam/steamapps/common/Source SDK Base 2013 Multiplayer/hl2/materials/hl2.vmt", "from sdk")
        .AddText("mod/gameinfo.txt", AppIdGameInfo);

    private static GameInfoSearchPath Line(string location) => new(["game"], location);

    [Fact]
    public void AnAppIdPrefixIsReadWithItsId()
    {
        Assert.True(Line("|appid_440|tf/tf2_misc.vpk").TryGetAppId(out int id, out _));
        Assert.Equal(440, id);
    }

    [Fact]
    public void AnAppIdPrefixLeavesTheRestRelativeToTheInstall()
    {
        Line("|appid_440|tf/tf2_misc.vpk").TryGetAppId(out _, out string rest);

        Assert.Equal("tf/tf2_misc.vpk", rest);
    }

    [Fact]
    public void TheAppIdPrefixIsMatchedWhateverItsCasing()
    {
        // Q_stristr, filesystem_init.cpp:694.
        Assert.True(Line("|AppID_440|tf").TryGetAppId(out int id, out _));
        Assert.Equal(440, id);
    }

    [Fact]
    public void AnAppIdTokenNotAtTheStartIsNotAnAppIdLocation()
    {
        // Q_stristr( pLocation, APPID_PREFIX_TOKEN ) == pLocation: only a prefix counts.
        Assert.False(Line("tf/|appid_440|x").TryGetAppId(out int id, out _));
        Assert.Equal(0, id);
    }

    [Fact]
    public void AnAppIdWithNoClosingBarIsMalformed()
    {
        // "Malformed gameinfo.txt", filesystem_init.cpp:700-703.
        Assert.Throws<InvalidDataException>(() => Line("|appid_440tf").TryGetAppId(out _, out _));
    }

    [Fact]
    public void AZeroAppIdIsRefused()
    {
        // "Can't mount content from invalid appid.", filesystem_init.cpp:705-708.
        Assert.Throws<InvalidDataException>(() => Line("|appid_0|tf").TryGetAppId(out _, out _));
    }

    [Fact]
    public void ANonNumericAppIdIsRefused()
    {
        // V_atoi("tf|...") is 0.
        Assert.Throws<InvalidDataException>(() => Line("|appid_tf|tf").TryGetAppId(out _, out _));
    }

    [Fact]
    public async Task TheSteamInstallIsAlwaysTheFirstLibrary()
    {
        SteamLibraryFolders libraries = await SteamLibraryFolders.LoadAsync(Steam(), VPath.Create("steam"));

        Assert.Equal(["steam", "games/lib"], libraries.Libraries.Select(l => l.Path.Value));
    }

    [Fact]
    public async Task EachLibrarysAppsAreRead()
    {
        SteamLibraryFolders libraries = await SteamLibraryFolders.LoadAsync(Steam(), VPath.Create("steam"));

        Assert.Equal([440], libraries.Libraries[1].Apps!);
    }

    [Fact]
    public async Task AnAppIsFoundInTheLibraryThatListsIt()
    {
        SteamLibraryFolders libraries = await SteamLibraryFolders.LoadAsync(Steam(), VPath.Create("steam"));

        Assert.Equal(
            "games/lib/steamapps/common/Team Fortress 2",
            (await libraries.FindInstallDirectoryAsync(440))?.Value);
    }

    [Fact]
    public async Task AnAppIsFoundInTheSteamInstallItself()
    {
        SteamLibraryFolders libraries = await SteamLibraryFolders.LoadAsync(Steam(), VPath.Create("steam"));

        Assert.Equal(
            "steam/steamapps/common/Source SDK Base 2013 Multiplayer",
            (await libraries.FindInstallDirectoryAsync(243750))?.Value);
    }

    [Fact]
    public async Task AnAppNoLibraryListsIsNotInstalled()
    {
        SteamLibraryFolders libraries = await SteamLibraryFolders.LoadAsync(Steam(), VPath.Create("steam"));

        Assert.Null(await libraries.FindInstallDirectoryAsync(220));
    }

    [Fact]
    public async Task AnAppListedWithoutItsManifestIsNotInstalled()
    {
        // The apps block says 440 is in games/lib; with no appmanifest there
        // is no installdir to answer with, so it is not installed.
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("steam/steamapps/libraryfolders.vdf", LibraryFolders);
        SteamLibraryFolders libraries = await SteamLibraryFolders.LoadAsync(disk, VPath.Create("steam"));

        Assert.Null(await libraries.FindInstallDirectoryAsync(440));
    }

    [Fact]
    public async Task TheOlderFormatsPlainLibraryPathIsALibrary()
    {
        // Before 2021: "1" "D:\\SteamLibrary", no apps block.
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddText("steam/steamapps/libraryfolders.vdf", "\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\"1\"\n\t\"1\"\t\"D:\\\\SteamLibrary\"\n}\n")
            .AddText("SteamLibrary/steamapps/appmanifest_440.acf", Tf2Manifest);
        SteamLibraryFolders libraries = await SteamLibraryFolders.LoadAsync(disk, VPath.Create("steam"));

        Assert.Equal(
            "SteamLibrary/steamapps/common/Team Fortress 2",
            (await libraries.FindInstallDirectoryAsync(440))?.Value);
    }

    [Fact]
    public async Task ASteamInstallWithNoLibraryFileIsItsOwnOnlyLibrary()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("steam/steamapps/appmanifest_440.acf", Tf2Manifest);
        SteamLibraryFolders libraries = await SteamLibraryFolders.LoadAsync(disk, VPath.Create("steam"));

        Assert.Equal("steam/steamapps/common/Team Fortress 2", (await libraries.FindInstallDirectoryAsync(440))?.Value);
    }

    [Fact]
    public async Task AnAppIdSearchPathMountsTheAppsContent()
    {
        await using ContentFileSystem content = (await MountAsync(Steam())).Content;

        using IMemoryOwner<byte>? owner = await content.ReadAsync(VPath.Create("materials/tf.vmt"));

        Assert.Equal("from tf", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    [Fact]
    public async Task AppIdSearchPathsKeepTheFilesOrder()
    {
        await using ContentFileSystem content = (await MountAsync(Steam())).Content;

        Assert.Equal(
            ["games/lib/steamapps/common/Team Fortress 2/tf", "steam/steamapps/common/Source SDK Base 2013 Multiplayer/hl2"],
            content.Mounts.Select(static m => m.Name));
    }

    [Fact]
    public async Task AnAppThatIsNotInstalledIsRefusedNotSkipped()
    {
        // "This mod requires %s (%d) to be installed", filesystem_init.cpp:711-719.
        InMemoryFileSystem disk = Steam().AddText(
            "mod/gameinfo.txt",
            AppIdGameInfo.Replace("|appid_440|", "|appid_220|", StringComparison.Ordinal));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await MountAsync(disk));
    }

    [Fact]
    public async Task AnAppIdSearchPathWithNoLocatorIsRefused()
    {
        InMemoryFileSystem disk = Steam();
        GameInfo gameInfo = await GameInfo.LoadAsync(disk, VPath.Create("mod/gameinfo.txt"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await GameContentMounter.MountAsync(
            disk, gameInfo, new GameContentRoots(VPath.Create("mod"), VPath.Empty)));
    }

    [Fact]
    public async Task ADeferredLocatorNeverReadsSteamForAGameInfoWithoutAppIds()
    {
        // No libraryfolders.vdf on this disk at all: a plain gameinfo must
        // mount without the locator ever being asked.
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddText("mod/gameinfo.txt", "\"GameInfo\"\n{\n FileSystem\n {\n  SearchPaths\n  {\n   game |gameinfo_path|.\n  }\n }\n}\n")
            .AddText("mod/materials/a.vmt", "a");
        RecordingFileSystem recording = new(disk);
        GameInfo gameInfo = await GameInfo.LoadAsync(disk, VPath.Create("mod/gameinfo.txt"));

        await using ContentFileSystem content = (await GameContentMounter.MountAsync(
            recording,
            gameInfo,
            new GameContentRoots(VPath.Create("mod"), VPath.Empty) { Steam = SteamLibraryFolders.Deferred(recording, VPath.Create("steam")) })).Content;

        Assert.Null(recording.Recorder.Find(VPath.Create("steam/steamapps/libraryfolders.vdf")));
    }

    private static async Task<GameContentMounter.Result> MountAsync(InMemoryFileSystem disk)
    {
        GameInfo gameInfo = await GameInfo.LoadAsync(disk, VPath.Create("mod/gameinfo.txt"));

        return await GameContentMounter.MountAsync(
            disk,
            gameInfo,
            new GameContentRoots(VPath.Create("mod"), VPath.Empty)
            {
                Steam = await SteamLibraryFolders.LoadAsync(disk, VPath.Create("steam")),
            });
    }
}
