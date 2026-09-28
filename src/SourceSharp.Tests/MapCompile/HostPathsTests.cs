//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// A path the CLI prints names the file the user can open: the root a
/// <see cref="VPath"/> drops is put back, where messages used to print
/// <c>tmp/x/rooms.roompack</c> for <c>/tmp/x/rooms.roompack</c>.
/// </summary>
public sealed class HostPathsTests
{
    // ---- the spelling, per host ---------------------------------------------

    [UnixFact]
    public void OnLinuxTheLeadingSlashIsRestored() =>
        Assert.Equal("/tmp/x/rooms.roompack", HostPaths.Display(VPath.Create("tmp/x/rooms.roompack")));

    /// <summary>Off Windows <c>C:</c> is a directory like any other, not a drive.</summary>
    [UnixFact]
    public void OnLinuxADriveLikeSegmentKeepsItsSlash() =>
        Assert.Equal("/C:/x", HostPaths.Display(VPath.Create("C:/x")));

    [UnixFact]
    public void OnLinuxAPathGoesUnderItsRoot() =>
        Assert.Equal("/srv/fixture/x/y", HostPaths.Display(VPath.Create("x/y"), "/srv/fixture"));

    /// <summary>
    /// A full Windows path carries its own drive, which is not the root's
    /// drive prefixed again (<c>C:\D:\...</c>).
    /// </summary>
    [WindowsFact]
    public void OnWindowsThePathsOwnDriveIsKept() =>
        Assert.Equal(@"D:\game\maps\a.bsp", HostPaths.Display(VPath.Create("D:/game/maps/a.bsp"), @"C:\"));

    /// <summary>A path without a drive sits on the root's drive, in backslashes.</summary>
    [WindowsFact]
    public void OnWindowsAPathWithoutADriveIsOnTheRootsDrive() =>
        Assert.Equal(@"C:\Users\x", HostPaths.Display(VPath.Create("Users/x"), @"C:\"));

    [Fact]
    public void TheEmptyPathIsTheRoot()
    {
        string root = Path.GetPathRoot(Path.GetFullPath("/"))!;
        Assert.Equal(root, HostPaths.Display(VPath.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ARootIsRequired(string? root) =>
        Assert.ThrowsAny<ArgumentException>(() => HostPaths.Display(VPath.Create("x"), root!));

    /// <summary>
    /// On this host, a full path made into a <see cref="VPath"/> the way every
    /// verb makes one displays as the full path it came from.
    /// </summary>
    [Theory]
    [InlineData("/tmp/rooms3x3/maps/rooms3x3.bsp")]
    [InlineData("relative/out/x.bsp")]
    public void OnThisHostAFullPathRoundTrips(string path)
    {
        string full = Path.GetFullPath(path);
        Assert.Equal(full, HostPaths.Display(VPath.Create(full)));
    }

    // ---- the messages --------------------------------------------------------

    [Fact]
    public async Task VbspSaysWhereItWroteTheMap()
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        fs.AddText(Rooted("/game/maps/box.vmf"), TestMapCatalog.SealedRoom().Write());
        using StringWriter output = new();

        int exit = await VbspCommand.RunAsync(fs, ["-game", "/game", "/game/maps/box.vmf"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains(
            "Writing " + Path.GetFullPath("/game/maps/box.bsp") + Environment.NewLine,
            output.ToString(),
            StringComparison.Ordinal);
    }

    // ---- helpers -----------------------------------------------------------

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

    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
