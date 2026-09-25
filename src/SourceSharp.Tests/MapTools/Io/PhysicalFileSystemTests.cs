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
}
