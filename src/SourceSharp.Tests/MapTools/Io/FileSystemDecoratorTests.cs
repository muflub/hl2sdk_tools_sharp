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
/// The three decorators that sit at the <see cref="IFileSystem"/> seam:
/// the dependency recorder, the read-only guard for content mounts, and the
/// fault injector that makes a full disk a one-line fixture.
/// </summary>
public class FileSystemDecoratorTests
{
    [Fact]
    public async Task AReadIsRecordedWithItsContentHash()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "hello"));

        using (await fs.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        FileDependency entry = fs.Recorder.Find(VPath.Create("a.txt"))!.Value;
        Assert.Equal(DependencyRecorder.Hash("hello"u8), entry.ContentHash);
    }

    [Fact]
    public async Task AReadIsRecordedAsRead()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "hello"));

        using (await fs.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        Assert.Equal(DependencyKind.Read, fs.Recorder.Find(VPath.Create("a.txt"))!.Value.Kind);
    }

    [Fact]
    public async Task OpeningAStreamIsRecordedToo()
    {
        // The recorder cannot be bypassed: every way of getting bytes out of
        // the seam has to appear in the record, not just ReadAllAsync.
        RecordingFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "hello"));

        Stream stream = await fs.OpenReadAsync(VPath.Create("a.txt"));
        await using (stream.ConfigureAwait(false))
        {
        }

        Assert.Equal(DependencyKind.Read, fs.Recorder.Find(VPath.Create("a.txt"))!.Value.Kind);
    }

    [Fact]
    public async Task AStreamOpenedThroughTheRecorderStillYieldsTheFile()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "hello"));

        Stream stream = await fs.OpenReadAsync(VPath.Create("a.txt"));
        await using (stream.ConfigureAwait(false))
        {
            using StreamReader reader = new(stream);
            Assert.Equal("hello", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task AReadThatFailedBecauseTheFileIsAbsentIsRecordedAsAMiss()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem());

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await fs.ReadAllAsync(VPath.Create("gone.txt")));

        Assert.Equal(DependencyKind.Missing, fs.Recorder.Find(VPath.Create("gone.txt"))!.Value.Kind);
    }

    [Fact]
    public async Task AFailedExistsCheckIsRecordedAsAMiss()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem());

        await fs.ExistsAsync(VPath.Create("gone.txt"));

        Assert.Equal(DependencyKind.Missing, fs.Recorder.Find(VPath.Create("gone.txt"))!.Value.Kind);
    }

    [Fact]
    public async Task ASuccessfulExistsCheckIsRecordedAsResolved()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "x"));

        await fs.ExistsAsync(VPath.Create("a.txt"));

        Assert.Equal(DependencyKind.Resolved, fs.Recorder.Find(VPath.Create("a.txt"))!.Value.Kind);
    }

    [Fact]
    public async Task AFailedGetInfoIsRecordedAsAMiss()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem());

        await fs.GetInfoAsync(VPath.Create("gone.txt"));

        Assert.Equal(DependencyKind.Missing, fs.Recorder.Find(VPath.Create("gone.txt"))!.Value.Kind);
    }

    [Fact]
    public async Task AnExistsCheckFollowedByAReadIsOneRecord()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "x"));

        await fs.ExistsAsync(VPath.Create("a.txt"));
        using (await fs.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        Assert.Equal(1, fs.Recorder.Count);
    }

    [Fact]
    public async Task AnExistsCheckFollowedByAReadUpgradesToRead()
    {
        // Merging takes the strongest observation. The other order must give
        // the same answer, or the input set would depend on the order a stage
        // happened to touch its files in.
        RecordingFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "x"));

        await fs.ExistsAsync(VPath.Create("a.txt"));
        using (await fs.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        Assert.Equal(DependencyKind.Read, fs.Recorder.Find(VPath.Create("a.txt"))!.Value.Kind);
    }

    [Fact]
    public async Task AReadFollowedByAnExistsCheckStaysRead()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "x"));

        using (await fs.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        await fs.ExistsAsync(VPath.Create("a.txt"));

        Assert.Equal(DependencyKind.Read, fs.Recorder.Find(VPath.Create("a.txt"))!.Value.Kind);
    }

    [Fact]
    public async Task WritesAreNotRecorded()
    {
        // An output is not an input; recording one would make a stage depend on
        // its own product.
        RecordingFileSystem fs = new(new InMemoryFileSystem());

        await fs.ReplaceAsync(
            VPath.Create("out.bsp"),
            async (stream, token) => await stream.WriteAsync(new byte[] { 1 }, token));

        Assert.Equal(0, fs.Recorder.Count);
    }

    [Fact]
    public async Task TheSnapshotIsSortedByPath()
    {
        RecordingFileSystem fs = new(new InMemoryFileSystem()
            .AddText("z.txt", "z")
            .AddText("a.txt", "a"));

        using (await fs.ReadAllAsync(VPath.Create("z.txt")))
        {
        }

        using (await fs.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        Assert.Equal(
            [VPath.Create("a.txt"), VPath.Create("z.txt")],
            fs.Recorder.Snapshot().Select(static d => d.Path));
    }

    [Fact]
    public async Task ADigestChangesWhenAMissingFileAppears()
    {
        // The reason misses are recorded at all: nothing the first run READ has
        // changed, and the answer is different anyway.
        InMemoryFileSystem disk = new();
        RecordingFileSystem before = new(disk);
        await before.ExistsAsync(VPath.Create("a.txt"));
        string missing = before.Recorder.ComputeDigest();

        disk.AddText("a.txt", "now it is here");
        RecordingFileSystem after = new(disk);
        await after.ExistsAsync(VPath.Create("a.txt"));

        Assert.NotEqual(missing, after.Recorder.ComputeDigest());
    }

    [Fact]
    public async Task ADigestIsStableAcrossTwoIdenticalRuns()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("a.txt", "x");

        RecordingFileSystem first = new(disk);
        using (await first.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        RecordingFileSystem second = new(disk);
        using (await second.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        Assert.Equal(first.Recorder.ComputeDigest(), second.Recorder.ComputeDigest());
    }

    [Fact]
    public async Task ADigestChangesWhenContentChanges()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("a.txt", "x");
        RecordingFileSystem first = new(disk);
        using (await first.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        disk.AddText("a.txt", "y");
        RecordingFileSystem second = new(disk);
        using (await second.ReadAllAsync(VPath.Create("a.txt")))
        {
        }

        Assert.NotEqual(first.Recorder.ComputeDigest(), second.Recorder.ComputeDigest());
    }

    [Fact]
    public async Task TheReadOnlyGuardRefusesOpenWrite()
    {
        ReadOnlyFileSystem fs = new(new InMemoryFileSystem());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fs.OpenWriteAsync(VPath.Create("a.txt")));
    }

    [Fact]
    public async Task TheReadOnlyGuardRefusesReplace()
    {
        ReadOnlyFileSystem fs = new(new InMemoryFileSystem());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fs.ReplaceAsync(VPath.Create("a.txt"), (stream, token) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task TheReadOnlyGuardRefusesDelete()
    {
        ReadOnlyFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "x"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fs.DeleteAsync(VPath.Create("a.txt")));
    }

    [Fact]
    public async Task TheReadOnlyGuardLeavesTheFileAlone()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("a.txt", "x");
        ReadOnlyFileSystem fs = new(disk);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fs.DeleteAsync(VPath.Create("a.txt")));

        Assert.Equal(1, disk.Count);
    }

    [Fact]
    public async Task TheReadOnlyGuardStillReads()
    {
        ReadOnlyFileSystem fs = new(new InMemoryFileSystem().AddText("a.txt", "x"));

        using IMemoryOwner<byte> owner = await fs.ReadAllAsync(VPath.Create("a.txt"));

        Assert.Equal("x"u8.ToArray(), owner.Memory.ToArray());
    }

    [Fact]
    public async Task ADiskThatFillsMidWriteThrows()
    {
        FaultInjectingFileSystem fs = new(new InMemoryFileSystem(), FaultPlan.DiskFullAfter(16));

        IOException error = await Assert.ThrowsAsync<IOException>(async () =>
            await fs.ReplaceAsync(
                VPath.Create("a.bsp"),
                async (stream, token) => await stream.WriteAsync(new byte[64], token)));

        Assert.Equal(FaultPlan.DiskFullMessage, error.Message);
    }

    [Fact]
    public async Task ADiskThatFillsMidWriteLeavesThePreviousOutputIntact()
    {
        // The gate this decorator exists for. The INNER file system does the
        // real temp-then-rename; only the stream handed to the writer is
        // faulted, so this is a test of the atomicity path and not of a
        // reimplementation of it.
        InMemoryFileSystem disk = new InMemoryFileSystem().AddFile("a.bsp", [1, 2, 3, 4]);
        FaultInjectingFileSystem fs = new(disk, FaultPlan.DiskFullAfter(16));

        await Assert.ThrowsAsync<IOException>(async () =>
            await fs.ReplaceAsync(
                VPath.Create("a.bsp"),
                async (stream, token) => await stream.WriteAsync(new byte[64], token)));

        Assert.Equal([1, 2, 3, 4], disk.GetBytes(VPath.Create("a.bsp")));
    }

    [Fact]
    public async Task ADiskThatFillsMidWriteOnARealDiskLeavesThePreviousBsp()
    {
        using TempTree tree = new();
        tree.Write("a.bsp", [1, 2, 3, 4]);
        FaultInjectingFileSystem fs = new(tree.FileSystem(), FaultPlan.DiskFullAfter(16));

        await Assert.ThrowsAsync<IOException>(async () =>
            await fs.ReplaceAsync(
                VPath.Create("a.bsp"),
                async (stream, token) => await stream.WriteAsync(new byte[64], token)));

        Assert.Equal([1, 2, 3, 4], tree.Read("a.bsp"));
    }

    [Fact]
    public async Task ADiskThatFillsMidWriteOnARealDiskLeavesNoTemporary()
    {
        using TempTree tree = new();
        tree.Write("a.bsp", [1, 2, 3, 4]);
        FaultInjectingFileSystem fs = new(tree.FileSystem(), FaultPlan.DiskFullAfter(16));

        await Assert.ThrowsAsync<IOException>(async () =>
            await fs.ReplaceAsync(
                VPath.Create("a.bsp"),
                async (stream, token) => await stream.WriteAsync(new byte[64], token)));

        Assert.Equal(["a.bsp"], tree.ListRoot());
    }

    [Fact]
    public async Task AShortReadTruncatesAStreamRatherThanThrowing()
    {
        // A short read is a TRUNCATION, not an error: the stream simply says
        // there is no more. Whatever reads it has to notice — which is the
        // whole reason this fault exists.
        FaultInjectingFileSystem fs = new(
            new InMemoryFileSystem().AddFile("a.bin", new byte[64]),
            FaultPlan.ShortReadAfter(10));

        Stream stream = await fs.OpenReadAsync(VPath.Create("a.bin"));
        await using (stream.ConfigureAwait(false))
        {
            using MemoryStream sink = new();
            await stream.CopyToAsync(sink);
            Assert.Equal(10, sink.Length);
        }
    }

    [Fact]
    public async Task AShortReadMakesReadExactlyThrowEndOfStream()
    {
        FaultInjectingFileSystem fs = new(
            new InMemoryFileSystem().AddFile("a.bin", new byte[64]),
            FaultPlan.ShortReadAfter(10));

        Stream stream = await fs.OpenReadAsync(VPath.Create("a.bin"));
        await using (stream.ConfigureAwait(false))
        {
            await Assert.ThrowsAsync<EndOfStreamException>(async () =>
                await stream.ReadExactlyAsync(new byte[64]));
        }
    }

    [Fact]
    public async Task AShortReadTruncatesReadAll()
    {
        FaultInjectingFileSystem fs = new(
            new InMemoryFileSystem().AddFile("a.bin", new byte[64]),
            FaultPlan.ShortReadAfter(10));

        using IMemoryOwner<byte> owner = await fs.ReadAllAsync(VPath.Create("a.bin"));

        Assert.Equal(10, owner.Memory.Length);
    }

    [Fact]
    public async Task ACancellationMidReadThrows()
    {
        FaultInjectingFileSystem fs = new(
            new InMemoryFileSystem().AddFile("a.bin", new byte[64]),
            FaultPlan.CancelReadAfter(10));

        Stream stream = await fs.OpenReadAsync(VPath.Create("a.bin"));
        await using (stream.ConfigureAwait(false))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using MemoryStream sink = new();
                await stream.CopyToAsync(sink);
            });
        }
    }

    [Fact]
    public async Task AnUnfaultedPlanChangesNothing()
    {
        FaultInjectingFileSystem fs = new(
            new InMemoryFileSystem().AddFile("a.bin", new byte[64]),
            FaultPlan.None);

        using IMemoryOwner<byte> owner = await fs.ReadAllAsync(VPath.Create("a.bin"));

        Assert.Equal(64, owner.Memory.Length);
    }

    [Fact]
    public void TheRecorderRefusesAnEmptyHash()
    {
        DependencyRecorder recorder = new();

        Assert.ThrowsAny<ArgumentException>(() => recorder.RecordRead(VPath.Create("a"), string.Empty));
    }

    [Fact]
    public void HashIsLowerCaseHexSha256()
    {
        // Pinned against the published SHA-256 of the empty input rather than
        // against this implementation's own output, which would agree with
        // itself whatever it computed.
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            DependencyRecorder.Hash([]));
    }
}
