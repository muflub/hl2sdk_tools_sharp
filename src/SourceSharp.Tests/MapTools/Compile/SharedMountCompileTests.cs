//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.Tests.MapTools.Bsp;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile;

/// <summary>
/// One mounted game shared by concurrent compiles, as a long-lived service
/// holds it: every compile writes the map a compile over its own mount writes,
/// the archives are opened once for all of them, and disposing the mount
/// leaves no stream open, including while a compile is still reading.
/// </summary>
/// <remarks>
/// The game is a <c>gameinfo.txt</c> whose one search path is a VPK in three
/// numbered parts holding the unit material and the synthetic prop models, so
/// every model and material read goes through the archive's held part streams
/// (whole reads and range reads both), mounted by
/// <see cref="GameContentMounter"/> exactly as a real game is.
/// </remarks>
public sealed class SharedMountCompileTests
{
    private const string GameInfoPath = "game/gameinfo.txt";

    private static readonly string[] Models =
    [
        "models/props_junk/wood_crate001a.mdl",
        "models/props_c17/oildrum001.mdl",
        "models/props_c17/furniturechair001a.mdl",
        "models/props_junk/propanecanister001a.mdl",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> Synthetic = new(SyntheticContent.Build);

    // The chain's room with every model twice in its open half.
    private static VmfDocument FurnishedRoom()
    {
        VmfDocument room = MapCompilerTests.Room();
        for (int i = 0; i < Models.Length * 2; i++)
        {
            float x = 40 + (24 * (i % 4));
            float y = i < 4 ? 40 : 200;
            VmfChunk prop = MapCompilerTests.Entity(
                room, "prop_static", string.Create(CultureInfo.InvariantCulture, $"{x} {y} 0"));
            prop.AddKey("model", Models[i % Models.Length]);
            prop.AddKey("angles", string.Create(CultureInfo.InvariantCulture, $"0 {15 * i} 0"));
        }

        return room;
    }

    // The game's disk: gameinfo.txt and a three-part VPK of the content,
    // behind a probe that counts the read streams still open.
    private static ProbeFileSystem GameDisk(Func<VPath, Task>? beforeOpenRead = null)
    {
        InMemoryFileSystem disk = new();
        disk.AddText(GameInfoPath, "\"GameInfo\" { game \"Shared\" FileSystem { SearchPaths { game |gameinfo_path|pak01.vpk } } }");

        VpkFixture pak = new();
        pak.AddText(
            $"materials/{UnitMap.Plain}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        int index = 0;
        foreach ((string path, byte[] bytes) in Synthetic.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            _ = pak.Add(path, bytes, archiveIndex: index++ % 3);
        }

        _ = pak.Write(disk, "game/pak01");
        return new ProbeFileSystem(disk) { BeforeOpenRead = beforeOpenRead };
    }

    private static async Task<ContentFileSystem> MountAsync(IFileSystem disk)
    {
        GameInfo info = await GameInfo.LoadAsync(disk, VPath.Create(GameInfoPath), CancellationToken.None);
        GameContentMounter.Result mounted = await GameContentMounter.MountAsync(
            disk, info, new GameContentRoots(VPath.Create("game"), VPath.Empty), cancellationToken: CancellationToken.None);
        Assert.Empty(mounted.Skipped);
        return mounted.Content;
    }

    private static async Task<byte[]> CompileAsync(
        IContentFileSystem content, ManagedCollisionCooker cooker, CancellationToken cancellationToken = default)
    {
        (InMemoryFileSystem files, _) = await MapCompilerTests.DiskAsync(FurnishedRoom());
        CompileRequest request = MapCompilerTests.Request(files, content) with { CollisionCooker = cooker };
        await MapCompiler.CompileAsync(request, null, cancellationToken);
        return await MapCompilerTests.ReadAsync(files, $"{MapCompilerTests.MapDirectory}/room.bsp");
    }

    /// <summary>
    /// Four compiles at once over one mount write the bytes a compile over a
    /// mount of its own writes, each archive part a compile reads is opened
    /// once for all of them, and disposing the mount closes every one.
    /// </summary>
    [Fact]
    public async Task ConcurrentCompilesOverOneMountWriteThePerCompileBytes()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);

        ProbeFileSystem ownDisk = GameDisk();
        byte[] own;
        int partsOneCompileOpens;
        await using (ContentFileSystem ownMount = await MountAsync(ownDisk))
        {
            int before = ownDisk.Opened;
            own = await CompileAsync(ownMount, cooker);
            partsOneCompileOpens = ownDisk.Opened - before;
        }

        Assert.Equal(0, ownDisk.OpenReads);
        Assert.InRange(partsOneCompileOpens, 1, 3);

        ProbeFileSystem sharedDisk = GameDisk();
        ContentFileSystem shared = await MountAsync(sharedDisk);
        int openedByMount = sharedDisk.Opened;
        byte[][] concurrent = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => CompileAsync(shared, cooker))));

        foreach (byte[] bsp in concurrent)
        {
            Assert.Equal(own, bsp);
        }

        // gameinfo.txt and the directory are read whole at mount time; after
        // that, one stream per part a compile reads serves every compile.
        Assert.Equal(openedByMount + partsOneCompileOpens, sharedDisk.Opened);
        Assert.Equal(partsOneCompileOpens, sharedDisk.OpenReads);

        await shared.DisposeAsync();
        Assert.Equal(0, sharedDisk.OpenReads);

        // A second dispose (a host's shutdown after its last compile's) is a no-op.
        await shared.DisposeAsync();
        Assert.Equal(0, sharedDisk.OpenReads);
    }

    /// <summary>
    /// Sequential compiles over one mount, the way a service runs one map
    /// after another, write the same bytes each time and open nothing more
    /// after the first.
    /// </summary>
    [Fact]
    public async Task SequentialCompilesOverOneMountReuseItsStreams()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        ProbeFileSystem disk = GameDisk();
        await using ContentFileSystem shared = await MountAsync(disk);

        byte[] first = await CompileAsync(shared, cooker);
        int opened = disk.Opened;
        byte[] second = await CompileAsync(shared, cooker);

        Assert.Equal(first, second);
        Assert.Equal(opened, disk.Opened);
    }

    /// <summary>
    /// A compile cancelled mid-read leaves the shared mount fit for the next
    /// compile, which writes the uncancelled bytes.
    /// </summary>
    [Fact]
    public async Task ACancelledCompileLeavesTheSharedMountFitForTheNext()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using CancellationTokenSource cancel = new();
        bool armed = true;
        ProbeFileSystem disk = GameDisk(async path =>
        {
            // The first archive part opened after the mount cancels its compile.
            if (Volatile.Read(ref armed) && path.Value.EndsWith("_000.vpk", StringComparison.Ordinal))
            {
                Volatile.Write(ref armed, false);
                await cancel.CancelAsync();
            }
        });
        await using ContentFileSystem shared = await MountAsync(disk);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompileAsync(shared, cooker, cancel.Token));

        ProbeFileSystem ownDisk = GameDisk();
        byte[] own;
        await using (ContentFileSystem ownMount = await MountAsync(ownDisk))
        {
            own = await CompileAsync(ownMount, cooker);
        }

        Assert.Equal(own, await CompileAsync(shared, cooker));
    }

    /// <summary>
    /// A host that disposes the mount while a compile is still reading from
    /// it: the dispose waits for the read that is opening an archive part and
    /// closes that part's stream, no part is opened again afterwards, and no
    /// stream is left open however the compile ends (a read after the dispose
    /// fails, which the compile may report as a failure or as missing
    /// content; either way it is the host's doing).
    /// </summary>
    [Fact]
    public async Task DisposingTheMountUnderARunningCompileLeavesNothingOpen()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        TaskCompletionSource opening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool armed = true;
        ProbeFileSystem disk = GameDisk(async path =>
        {
            if (Volatile.Read(ref armed) && path.Value.EndsWith(".vpk", StringComparison.Ordinal)
                && !path.Value.EndsWith("_dir.vpk", StringComparison.Ordinal))
            {
                Volatile.Write(ref armed, false);
                opening.TrySetResult();
                await release.Task;
            }
        });
        ContentFileSystem shared = await MountAsync(disk);
        int openedByMount = disk.Opened;

        Task<byte[]> compile = Task.Run(() => CompileAsync(shared, cooker));
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Task dispose = shared.DisposeAsync().AsTask();
        release.SetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(60));

        try
        {
            _ = await compile.WaitAsync(TimeSpan.FromSeconds(120));
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            // The compile's own outcome is not what this fact is about.
        }

        Assert.Equal(openedByMount + 1, disk.Opened);
        Assert.Equal(0, disk.OpenReads);
    }
}
