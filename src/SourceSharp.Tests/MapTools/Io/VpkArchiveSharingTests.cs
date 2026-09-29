//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Globalization;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Vpk;
using SourceSharp.Tests.MapTools.Compile;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// <see cref="VpkArchive"/> as a long-lived service holds it: one archive,
/// many concurrent readers, and a dispose that leaves no stream open however
/// it races them.
/// </summary>
public sealed class VpkArchiveSharingTests
{
    // Sixty files over three numbered parts, each distinct.
    private static (ProbeFileSystem Disk, VPath Directory, Dictionary<string, byte[]> Files) Archive(
        Func<VPath, Task>? beforeOpenRead = null)
    {
        Dictionary<string, byte[]> files = [];
        VpkFixture fixture = new();
        for (int i = 0; i < 60; i++)
        {
            string path = string.Create(CultureInfo.InvariantCulture, $"models/m{i:D2}.bin");
            byte[] bytes = new byte[100 + (i * 37)];
            for (int b = 0; b < bytes.Length; b++)
            {
                bytes[b] = (byte)(i + (b * 7));
            }

            files[path] = bytes;
            _ = fixture.Add(path, bytes, archiveIndex: i % 3);
        }

        InMemoryFileSystem memory = new();
        VPath directory = fixture.Write(memory, "game/pak01");
        return (new ProbeFileSystem(memory) { BeforeOpenRead = beforeOpenRead }, directory, files);
    }

    private static async Task<byte[]> ReadAsync(VpkArchive archive, string path)
    {
        using IMemoryOwner<byte> owner = (await archive.ReadAsync(VPath.Create(path)))!;
        return owner.Memory.ToArray();
    }

    [Fact]
    public async Task ConcurrentReadersOfOneArchiveEachGetTheirOwnBytes()
    {
        (ProbeFileSystem disk, VPath directory, Dictionary<string, byte[]> files) = Archive();
        VpkArchive archive = await VpkArchive.OpenAsync(disk, directory);

        // Eight readers, each over every file in its own order, so reads of
        // one part from different readers interleave on its shared stream.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(r => Task.Run(async () =>
        {
            foreach (string path in files.Keys.OrderBy(p => (p.GetHashCode(StringComparison.Ordinal) * (r + 1)) & 0xffff))
            {
                Assert.Equal(files[path], await ReadAsync(archive, path));
            }
        })));

        // One stream per part, however many readers shared it.
        Assert.Equal(3, disk.Opened);
        await archive.DisposeAsync();
        Assert.Equal(0, disk.OpenReads);
    }

    [Fact]
    public async Task DisposeClosesEveryPartItOpened()
    {
        (ProbeFileSystem disk, VPath directory, Dictionary<string, byte[]> files) = Archive();
        VpkArchive archive = await VpkArchive.OpenAsync(disk, directory);
        foreach (string path in files.Keys)
        {
            _ = await ReadAsync(archive, path);
        }

        Assert.Equal(3, disk.OpenReads);

        await archive.DisposeAsync();
        await archive.DisposeAsync();

        Assert.Equal(0, disk.OpenReads);
    }

    [Fact]
    public async Task AReadAfterDisposeFailsAndOpensNothing()
    {
        (ProbeFileSystem disk, VPath directory, _) = Archive();
        VpkArchive archive = await VpkArchive.OpenAsync(disk, directory);
        int opened = disk.Opened;

        await archive.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await ReadAsync(archive, "models/m00.bin"));
        Assert.Equal(opened, disk.Opened);
        Assert.Equal(0, disk.OpenReads);
    }

    [Fact]
    public async Task ADisposeRacingAReadThatIsOpeningAPartLeavesNoStreamOpen()
    {
        // The read has taken its part's gate and is inside the stream open
        // when the dispose runs. The dispose used to pass the part (no stream
        // yet), and the read then opened one that nothing ever closed. Now
        // the dispose waits for the read, and closes what it opened.
        TaskCompletionSource opening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        (ProbeFileSystem disk, VPath directory, Dictionary<string, byte[]> files) = Archive(async path =>
        {
            if (Volatile.Read(ref hold) && path.Value.EndsWith("_000.vpk", StringComparison.Ordinal))
            {
                opening.TrySetResult();
                await release.Task;
            }
        });
        VpkArchive archive = await VpkArchive.OpenAsync(disk, directory);
        Volatile.Write(ref hold, true);

        Task<byte[]> read = ReadAsync(archive, "models/m00.bin");
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task dispose = archive.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);

        release.SetResult();
        Assert.Equal(files["models/m00.bin"], await read.WaitAsync(TimeSpan.FromSeconds(30)));
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, disk.OpenReads);
    }

    [Fact]
    public async Task ACancelledReadLeavesThePartFitForTheNextReader()
    {
        (ProbeFileSystem disk, VPath directory, Dictionary<string, byte[]> files) = Archive();
        VpkArchive archive = await VpkArchive.OpenAsync(disk, directory);
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await archive.ReadAsync(VPath.Create("models/m01.bin"), cancel.Token));

        Assert.Equal(files["models/m01.bin"], await ReadAsync(archive, "models/m01.bin"));
        await archive.DisposeAsync();
        Assert.Equal(0, disk.OpenReads);
    }
}
