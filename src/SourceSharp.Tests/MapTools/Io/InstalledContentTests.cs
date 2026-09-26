//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// The content layer driven end to end over a real installed game: a real
/// <c>gameinfo.txt</c>, real VPKs, real loose directories, on a real
/// case-sensitive disk.
/// </summary>
/// <remarks>
/// <para>
/// Every fact here skips VISIBLY when the game is not installed, with the
/// reason in the runner's output. The in-memory facts elsewhere in this folder
/// are the ones that must always run; these exist because a fixture is a
/// stand-in and the thing it stands in for is 18000 shipped files,
/// half of them in archives, all of them lower-case on a disk that maps
/// reference in mixed case.
/// </para>
/// <para>
/// Read through a <see cref="ReadOnlyFileSystem"/>, and not only as a matter of
/// principle: these run against the user's actual Steam install.
/// </para>
/// </remarks>
public class InstalledContentTests
{
    private const string GameInfo = "hl2mp/gameinfo.txt";

    [InstalledGameFact]
    public async Task TheInstalledGameInfoMountsSomething()
    {
        (GameContentMounter.Result result, _) = await MountInstalled();

        await using ContentFileSystem content = result.Content;

        Assert.NotEmpty(content.Mounts);
    }

    [InstalledGameFact]
    public async Task TheInstalledGameInfoNamesItsSteamAppId()
    {
        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        SourceSharp.MapTools.Io.GameInfo info = await SourceSharp.MapTools.Io.GameInfo.LoadAsync(
            new ReadOnlyFileSystem(host),
            host.ToVirtualPath(Path.Combine(InstalledGameContent.BaseDirectory, GameInfo)));

        Assert.Equal(320, info.SteamAppId);
    }

    [InstalledGameFact]
    public async Task AMaterialResolvesOutOfTheInstalledContent()
    {
        // A material every HL2 install has, referenced the way a map would.
        (GameContentMounter.Result result, _) = await MountInstalled();

        await using ContentFileSystem content = result.Content;

        Assert.NotNull(await content.ResolveAsync(VPath.Create("materials/metal/metalwall048a.vmt")));
    }

    [InstalledGameFact]
    public async Task TheSameMaterialResolvesInAMapsCasing()
    {
        // THE gate, against real content on a real case-sensitive disk: Hammer
        // writes "Metal/Metalwall048a" into a VMF and the file on disk is
        // "materials/metal/metalwall048a.vmt".
        (GameContentMounter.Result result, _) = await MountInstalled();

        await using ContentFileSystem content = result.Content;

        Assert.NotNull(await content.ResolveAsync(VPath.Create("materials/Metal/Metalwall048a.vmt")));
    }

    [InstalledGameFact]
    public async Task BothCasingsResolveToTheSameFile()
    {
        (GameContentMounter.Result result, _) = await MountInstalled();

        await using ContentFileSystem content = result.Content;

        ContentSource? lower = await content.ResolveAsync(VPath.Create("materials/metal/metalwall048a.vmt"));
        ContentSource? mixed = await content.ResolveAsync(VPath.Create("Materials/Metal/METALWALL048A.VMT"));

        Assert.Equal(lower!.Value.Path, mixed!.Value.Path);
    }

    [InstalledGameFact]
    public async Task BothCasingsProduceOneDependencyRecord()
    {
        // The plan's gate, against real content: two spellings of one material
        // resolve to ONE file and produce ONE dependency record.
        (GameContentMounter.Result result, _) = await MountInstalled();

        await using ContentFileSystem content = result.Content;
        RecordingContentFileSystem recording = new(content);

        using (await recording.ReadAsync(VPath.Create("materials/metal/metalwall048a.vmt")))
        {
        }

        using (await recording.ReadAsync(VPath.Create("Materials/Metal/METALWALL048A.VMT")))
        {
        }

        Assert.Equal(1, recording.Recorder.Count);
    }

    [InstalledGameFact]
    public async Task AMaterialReadOutOfTheInstalledContentIsAVmt()
    {
        // Resolving proves the index; reading proves the bytes came back
        // through the right mount and the right offset.
        (GameContentMounter.Result result, _) = await MountInstalled();

        await using ContentFileSystem content = result.Content;

        using IMemoryOwner<byte>? owner = await content.ReadAsync(
            VPath.Create("materials/Metal/Metalwall048a.vmt"));

        Assert.True(owner!.Memory.Length > 0, "the material read back empty");
    }

    [InstalledGameFact]
    public async Task MountingTheInstalledGameNamesWhatItSkipped()
    {
        // Real gameinfo.txt files list content an install may not have -- the
        // _english archives, the download directory. A skip is normal; a silent
        // one is not.
        (GameContentMounter.Result result, _) = await MountInstalled();

        await using ContentFileSystem content = result.Content;

        Assert.All(result.Skipped, static s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }

    [InstalledGameFact]
    public async Task EveryMountedSearchPathHoldsSomething()
    {
        // A mount with nothing in it means a search path was resolved to the
        // wrong place and quietly indexed an empty directory -- which reads as
        // a successful mount and fails much later as a missing material.
        (GameContentMounter.Result result, _) = await MountInstalled();

        await using ContentFileSystem content = result.Content;

        Assert.DoesNotContain(content.Mounts, static m => m.Paths.Count == 0);
    }

    private static async ValueTask<(GameContentMounter.Result Result, PhysicalFileSystem Host)> MountInstalled()
    {
        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        ReadOnlyFileSystem guarded = new(host);

        VPath gameInfo = host.ToVirtualPath(Path.Combine(InstalledGameContent.BaseDirectory, GameInfo));
        VPath baseDirectory = host.ToVirtualPath(InstalledGameContent.BaseDirectory);

        return (await GameContentMounter.MountAsync(guarded, gameInfo, baseDirectory), host);
    }
}
