//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap phys list</c> names each library by a path the user can paste:
/// under the root its file system stands for, in the host's spelling.
/// </summary>
public sealed class PhysCommandTests
{
    /// <summary>
    /// The CLI's disk is rooted at the host root and discovery lists full
    /// paths, so each library is shown as the host path it was found at.
    /// </summary>
    [Fact]
    public async Task ALibraryIsListedByItsHostPath()
    {
        string host = Path.GetFullPath("/steam/steamapps/common/Half-Life 2/bin/linux64/vphysics.so");
        using StringWriter output = new();

        await PhysCommand.WriteTableAsync([Library(VPath.Create(host))], output, HostRoot);

        Assert.Contains($"     {host}  (", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>With no root to show, a path is listed as the file system names it.</summary>
    [Fact]
    public async Task WithoutARootALibraryIsListedAsTheFileSystemNamesIt()
    {
        using StringWriter output = new();

        await PhysCommand.WriteTableAsync([Library(VPath.Create("games/hl2/bin/vphysics.so"))], output);

        Assert.Contains("     games/hl2/bin/vphysics.so  (", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A disk rooted at a directory shows its paths under that directory.</summary>
    [UnixFact]
    public async Task ALibraryUnderADirectoryRootIsListedUnderIt()
    {
        using StringWriter output = new();

        await PhysCommand.WriteTableAsync([Library(VPath.Create("hl2/bin/vphysics.so"))], output, "/srv/fixture");

        Assert.Contains("     /srv/fixture/hl2/bin/vphysics.so  (", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A Steam library on another drive is found as <c>E:/SteamLibrary/...</c>
    /// and keeps that drive, where gluing the root on as text listed it as
    /// <c>C:\/E:/SteamLibrary/...</c>.
    /// </summary>
    [WindowsFact]
    public async Task ALibraryOnAnotherDriveKeepsItsDrive()
    {
        using StringWriter output = new();

        await PhysCommand.WriteTableAsync(
            [Library(VPath.Create("E:/SteamLibrary/steamapps/common/HL2/bin/vphysics.dll"))], output, @"C:\");

        Assert.Contains(@"     E:\SteamLibrary\steamapps\common\HL2\bin\vphysics.dll  (", output.ToString(), StringComparison.Ordinal);
    }

    private static string HostRoot => Path.GetPathRoot(Path.GetFullPath("/"))!;

    private static VPhysicsLibrary Library(VPath path) =>
        new("Half-Life 2", path, new ElfIdentity(ElfArchitecture.X64, "abc123"), 1024);
}
