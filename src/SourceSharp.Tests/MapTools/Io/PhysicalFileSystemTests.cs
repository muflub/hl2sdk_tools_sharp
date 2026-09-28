//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Security.Cryptography;

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// The one implementation that touches a disk, and the promises only a disk can
/// break: the rename that makes a write atomic, and the memory-mapped read a
/// caller must not be able to tell from a copied one.
/// </summary>
public class PhysicalFileSystemTests
{
    [Fact]
    public void ARelativeRootIsRefused()
    {
        // A file system rooted at "." would follow the process's working
        // directory, which two compiles in one host process do not share.
        Assert.Throws<ArgumentException>(() => new PhysicalFileSystem("relative/path"));
    }

    [Fact]
    public void ToHostPathJoinsUnderTheRoot()
    {
        using TempTree tree = new();

        Assert.Equal(
            Path.Combine(tree.Root, "materials", "a.vmt"),
            tree.FileSystem().ToHostPath(VPath.Create("materials/a.vmt")));
    }

    [Fact]
    public void ToVirtualPathRefusesAPathOutsideTheRoot()
    {
        using TempTree tree = new();

        Assert.Throws<ArgumentException>(() => tree.FileSystem().ToVirtualPath("/etc/passwd"));
    }

    [Fact]
    public void ToVirtualPathRoundTripsAHostPath()
    {
        using TempTree tree = new();
        PhysicalFileSystem fs = tree.FileSystem();
        VPath path = VPath.Create("materials/metal/a.vmt");

        Assert.Equal(path, fs.ToVirtualPath(fs.ToHostPath(path)));
    }

    [Fact]
    public void AVPathCannotEscapeTheRoot()
    {
        // Containment is a property of VPath, checked at construction, and not
        // a string test in the file system that one code path forgets.
        Assert.Throws<ArgumentException>(() => VPath.Create("../../etc/passwd"));
    }

    [Fact]
    public async Task ReadAllReadsAFileBackExactly()
    {
        using TempTree tree = new();
        byte[] contents = RandomNumberGenerator.GetBytes(4096);
        tree.Write("a.bin", contents);

        using IMemoryOwner<byte> owner = await tree.FileSystem().ReadAllAsync(VPath.Create("a.bin"));

        Assert.Equal(contents, owner.Memory.ToArray());
    }

    [Fact]
    public async Task AMemoryMappedReadGivesTheSameBytesAsACopiedOne()
    {
        // The whole point of the mapping is that a caller cannot tell. The two
        // paths are driven over one file and compared, rather than each being
        // checked against the fixture separately, because a fixture that both
        // paths got wrong the same way would pass that.
        using TempTree tree = new();
        byte[] contents = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        tree.Write("big.bin", contents);

        using IMemoryOwner<byte> mapped = await tree.MappingFileSystem()
            .ReadAllAsync(VPath.Create("big.bin"));
        using IMemoryOwner<byte> copied = await tree.CopyingFileSystem()
            .ReadAllAsync(VPath.Create("big.bin"));

        Assert.True(mapped.Memory.Span.SequenceEqual(copied.Memory.Span));
    }

    [Fact]
    public async Task AMemoryMappedReadReportsTheFileLength()
    {
        using TempTree tree = new();
        tree.Write("big.bin", new byte[1234]);

        using IMemoryOwner<byte> owner = await tree.MappingFileSystem()
            .ReadAllAsync(VPath.Create("big.bin"));

        Assert.Equal(1234, owner.Memory.Length);
    }

    /// <summary>
    /// A small file is read where the caller is (see ReadAllAsync's remarks):
    /// the task is already complete when it comes back.
    /// </summary>
    [Fact]
    public async Task ASmallFileIsReadWithoutWaiting()
    {
        using TempTree tree = new();
        byte[] contents = RandomNumberGenerator.GetBytes(300);
        tree.Write("a.vmt", contents);

        ValueTask<IMemoryOwner<byte>> read = tree.CopyingFileSystem().ReadAllAsync(VPath.Create("a.vmt"));

        Assert.True(read.IsCompletedSuccessfully);
        using IMemoryOwner<byte> owner = await read;
        Assert.Equal(contents, owner.Memory.ToArray());
    }

    [Fact]
    public async Task ALargeFileReadWithoutMappingComesBackWhole()
    {
        // Larger than any single positioned read is promised to return.
        using TempTree tree = new();
        byte[] contents = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 17);
        tree.Write("big.bin", contents);

        using IMemoryOwner<byte> owner = await tree.CopyingFileSystem().ReadAllAsync(VPath.Create("big.bin"));

        Assert.True(owner.Memory.Span.SequenceEqual(contents));
    }

    [Fact]
    public async Task AnEmptyFileReadsAsNoBytes()
    {
        using TempTree tree = new();
        tree.Write("empty.vmt", []);

        using IMemoryOwner<byte> owner = await tree.FileSystem().ReadAllAsync(VPath.Create("empty.vmt"));

        Assert.Equal(0, owner.Memory.Length);
    }

    [Fact]
    public void AnAbsentFileFaultsTheTaskRatherThanThrowing()
    {
        // As an async method would: the failure travels in the task, so a
        // caller that starts several reads and awaits them later sees it
        // where it awaits.
        using TempTree tree = new();

        ValueTask<IMemoryOwner<byte>> read = tree.FileSystem().ReadAllAsync(VPath.Create("nope.bin"));

        Assert.True(read.IsFaulted);
        Assert.IsType<FileNotFoundException>(read.AsTask().Exception!.InnerException);
    }

    [Fact]
    public void ACancelledTokenCancelsTheTask()
    {
        using TempTree tree = new();
        tree.Write("a.bin", [1, 2, 3]);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        ValueTask<IMemoryOwner<byte>> read = tree.FileSystem().ReadAllAsync(VPath.Create("a.bin"), cancelled.Token);

        Assert.True(read.IsCanceled);
    }

    [ShortReadFileFact]
    public async Task AFileShorterThanItsSizeIsAnEndOfStream()
    {
        // The exact-length read the stream used to do reported a file that
        // ended early as EndOfStreamException; the positioned reads do too.
        PhysicalFileSystem sysfs = new(ShortReadFileFactAttribute.Root, long.MaxValue);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await sysfs.ReadAllAsync(VPath.Create(ShortReadFileFactAttribute.Relative)));
    }

    [Fact]
    public async Task ReadAllOfAnAbsentFileThrowsFileNotFound()
    {
        using TempTree tree = new();

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await tree.FileSystem().ReadAllAsync(VPath.Create("nope.bin")));
    }

    [Fact]
    public async Task OpenWriteCreatesMissingDirectories()
    {
        using TempTree tree = new();
        PhysicalFileSystem fs = tree.FileSystem();

        Stream stream = await fs.OpenWriteAsync(VPath.Create("deep/er/still/a.bin"));
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(new byte[] { 7 });
        }

        Assert.Equal([7], tree.Read("deep/er/still/a.bin"));
    }

    [Fact]
    public async Task ReplaceWritesTheNewContents()
    {
        using TempTree tree = new();
        tree.Write("a.bsp", [1]);

        await tree.FileSystem().ReplaceAsync(
            VPath.Create("a.bsp"),
            async (stream, token) => await stream.WriteAsync(new byte[] { 2, 3 }, token));

        Assert.Equal([2, 3], tree.Read("a.bsp"));
    }

    [Fact]
    public async Task ReplaceLeavesThePreviousBspWhenTheWriterThrows()
    {
        // The reason ReplaceAsync exists: a compile killed part-way through
        // WriteBSPFile must not leave a half-written.bsp that looks loadable.
        using TempTree tree = new();
        tree.Write("a.bsp", [1, 1, 1, 1]);

        await Assert.ThrowsAsync<IOException>(async () =>
            await tree.FileSystem().ReplaceAsync(
                VPath.Create("a.bsp"),
                async (stream, token) =>
                {
                    await stream.WriteAsync(new byte[4096], token);
                    throw new IOException("the disk filled up");
                }));

        Assert.Equal([1, 1, 1, 1], tree.Read("a.bsp"));
    }

    [Fact]
    public async Task ReplaceLeavesNoTemporaryBehindWhenItFails()
    {
        // A temp file that survived would be found by the next content mount's
        // directory walk, which is how a failed compile poisons the following
        // one.
        using TempTree tree = new();
        tree.Write("a.bsp", [1]);

        await Assert.ThrowsAsync<IOException>(async () =>
            await tree.FileSystem().ReplaceAsync(
                VPath.Create("a.bsp"),
                (stream, token) => throw new IOException("no")));

        Assert.Equal(["a.bsp"], tree.ListRoot());
    }

    [Fact]
    public async Task ReplaceLeavesThePreviousBspWhenTheTokenFires()
    {
        using TempTree tree = new();
        tree.Write("a.bsp", [1]);
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await tree.FileSystem().ReplaceAsync(
                VPath.Create("a.bsp"),
                async (stream, token) =>
                {
                    await stream.WriteAsync(new byte[] { 2 }, CancellationToken.None);
                    await cancellation.CancelAsync();
                },
                cancellation.Token));

        Assert.Equal([1], tree.Read("a.bsp"));
    }

    [Fact]
    public async Task ReplaceCreatesAFileThatWasNotThere()
    {
        using TempTree tree = new();

        await tree.FileSystem().ReplaceAsync(
            VPath.Create("sub/new.bsp"),
            async (stream, token) => await stream.WriteAsync(new byte[] { 5 }, token));

        Assert.Equal([5], tree.Read("sub/new.bsp"));
    }

    [Fact]
    public async Task ExistsIsTrueForAFileOnDisk()
    {
        using TempTree tree = new();
        tree.Write("a.bin", [1]);

        Assert.True(await tree.FileSystem().ExistsAsync(VPath.Create("a.bin")));
    }

    [Fact]
    public async Task GetInfoReportsTheLengthOnDisk()
    {
        using TempTree tree = new();
        tree.Write("a.bin", new byte[13]);

        FileInfoSnapshot? info = await tree.FileSystem().GetInfoAsync(VPath.Create("a.bin"));

        Assert.Equal(13, info!.Value.Length);
    }

    [Fact]
    public async Task DeleteOfAnAbsentFileSucceeds()
    {
        using TempTree tree = new();

        await tree.FileSystem().DeleteAsync(VPath.Create("never-existed.bin"));
    }

    [Fact]
    public async Task EnumerateFiltersByPatternCaseInsensitively()
    {
        // The disk is case-sensitive; the pattern is not, because an extension
        // is a format tag. A map referencing FOO.VMT means a material.
        using TempTree tree = new();
        tree.Write("materials/FOO.VMT", [1]);
        tree.Write("materials/bar.vtf", [1]);

        List<VPath> found = await InMemoryFileSystemTests.Collect(
            tree.FileSystem().EnumerateAsync(VPath.Create("materials"), "*.vmt"));

        Assert.Equal([VPath.Create("materials/FOO.VMT")], found);
    }

    [Fact]
    public async Task EnumerateOfAnAbsentDirectoryIsEmpty()
    {
        using TempTree tree = new();

        List<VPath> found = await InMemoryFileSystemTests.Collect(
            tree.FileSystem().EnumerateAsync(VPath.Create("nope")));

        Assert.Empty(found);
    }

    [Fact]
    public async Task RecursiveEnumerateDescends()
    {
        using TempTree tree = new();
        tree.Write("materials/a.vmt", [1]);
        tree.Write("materials/metal/b.vmt", [1]);

        List<VPath> found = await InMemoryFileSystemTests.Collect(
            tree.FileSystem().EnumerateAsync(VPath.Create("materials"), "*.vmt", recursive: true));

        Assert.Equal(
            [VPath.Create("materials/a.vmt"), VPath.Create("materials/metal/b.vmt")],
            found);
    }

    /// <summary>
    /// A file system at a drive root answers for other drives too, keeping
    /// the drive: on Windows a game on another drive than the one the command
    /// runs from failed to mount, its directory listing refused as "not
    /// below the root".
    /// </summary>
    [Theory]
    [InlineData(@"D:\", @"C:\Users\me\mod\gameinfo.txt", "C:/Users/me/mod/gameinfo.txt")]
    [InlineData(@"D:", @"e:\Steam\steamapps", "e:/Steam/steamapps")]
    [InlineData("D:/", "C:/x/y.vmt", "C:/x/y.vmt")]
    public void ADriveRootMapsAPathOnAnotherDriveWithItsDrive(string root, string full, string expected)
    {
        Assert.True(PhysicalFileSystem.TryMapAcrossDrives(root, full, out VPath path));
        Assert.Equal(expected, path.Value);
    }

    /// <summary>
    /// Everything else still refuses: the same drive (the prefix test's
    /// case, not this one's), a root that is a directory rather than a drive
    /// (containment is why a file system is rooted there), a host path with
    /// no drive, and a POSIX root.
    /// </summary>
    [Theory]
    [InlineData(@"D:\", @"d:\elsewhere\a.txt")]
    [InlineData(@"D:\work", @"C:\Users\a.txt")]
    [InlineData(@"D:\", @"\\server\share\a.txt")]
    [InlineData("/", "/home/me/a.txt")]
    [InlineData("/", @"C:\a.txt")]
    public void AnythingElseIsNotMappedAcrossDrives(string root, string full) =>
        Assert.False(PhysicalFileSystem.TryMapAcrossDrives(root, full, out _));

    /// <summary>
    /// A symlinked file reads as its target's bytes, all of them. The size
    /// came from the link itself, which on Linux is the length of the target
    /// path, so a symlinked gameinfo.txt read as its first few dozen bytes
    /// and a whole compile ran with no game content. Both read paths are
    /// covered: the pooled copy and the memory map.
    /// </summary>
    [SymlinkFact]
    public async Task ASymlinkedFileReadsAsItsTargetWhole()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfs-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "elsewhere", "deep"));
        try
        {
            byte[] content = new byte[5000];
            for (int i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(i * 7);
            }

            string target = Path.Combine(dir, "elsewhere", "deep", "gameinfo.txt");
            File.WriteAllBytes(target, content);
            File.CreateSymbolicLink(Path.Combine(dir, "gameinfo.txt"), target);

            foreach (long threshold in new[] { long.MaxValue, 1L })
            {
                PhysicalFileSystem fs = new(dir, threshold);
                using IMemoryOwner<byte> read = await fs.ReadAllAsync(VPath.Create("gameinfo.txt"));
                Assert.Equal(content, read.Memory.ToArray());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A symlinked file's info is its target's: its size and its write time,
    /// so a cache stamp moves when the target is edited.
    /// </summary>
    [SymlinkFact]
    public async Task ASymlinkedFilesInfoIsItsTargets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfs-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "a"));
        try
        {
            string target = Path.Combine(dir, "a", "target.txt");
            File.WriteAllText(target, new string('x', 1234));
            File.SetLastWriteTimeUtc(target, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            File.CreateSymbolicLink(Path.Combine(dir, "link.txt"), target);

            FileInfoSnapshot? info = await new PhysicalFileSystem(dir).GetInfoAsync(VPath.Create("link.txt"));

            Assert.NotNull(info);
            Assert.Equal(1234, info.Value.Length);
            Assert.Equal(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), info.Value.LastWriteTimeUtc);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A link whose target is gone reads as a missing file, not a crash.</summary>
    [SymlinkFact]
    public async Task ADanglingSymlinkIsAMissingFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfs-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "link.txt"), Path.Combine(dir, "gone.txt"));
            PhysicalFileSystem fs = new(dir);

            await Assert.ThrowsAsync<FileNotFoundException>(async () => await fs.ReadAllAsync(VPath.Create("link.txt")));
            Assert.Null(await fs.GetInfoAsync(VPath.Create("link.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
