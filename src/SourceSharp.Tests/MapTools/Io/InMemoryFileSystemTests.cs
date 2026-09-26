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
/// The file system the unit tier runs on: a dictionary that keeps every promise
/// <see cref="IFileSystem"/> makes, so that a fixture built here cannot pass a
/// fact that a real disk would fail.
/// </summary>
public class InMemoryFileSystemTests
{
    [Fact]
    public async Task AddedTextReadsBackAsUtf8()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem()
            .AddText("materials/metal/metalwall048a.vmt", "LightmappedGeneric { }");

        using IMemoryOwner<byte> owner = await fs.ReadAllAsync(
            VPath.Create("materials/metal/metalwall048a.vmt"));

        Assert.Equal("LightmappedGeneric { }", Encoding.UTF8.GetString(owner.Memory.Span));
    }

    [Fact]
    public async Task ReadOfAnAbsentFileThrowsFileNotFound()
    {
        InMemoryFileSystem fs = new();

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await fs.ReadAllAsync(VPath.Create("nothing.txt")));
    }

    [Fact]
    public async Task ReadAllHandsBackExactlyTheFileLength()
    {
        // The pooled owner rounds its array up to a power of two; a caller that
        // trusted Memory.Length would otherwise read the previous tenant's
        // bytes, which for a lump reader is a wrong answer and not a crash.
        InMemoryFileSystem fs = new InMemoryFileSystem().AddFile("a.bin", new byte[37]);

        using IMemoryOwner<byte> owner = await fs.ReadAllAsync(
            VPath.Create("a.bin"));

        Assert.Equal(37, owner.Memory.Length);
    }

    [Fact]
    public async Task ExistsIsFalseForAnAbsentFile()
    {
        InMemoryFileSystem fs = new();

        Assert.False(await fs.ExistsAsync(VPath.Create("a.txt")));
    }

    [Fact]
    public async Task GetInfoReportsTheLength()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem().AddFile("a.bin", new byte[9]);

        FileInfoSnapshot? info = await fs.GetInfoAsync(
            VPath.Create("a.bin"));

        Assert.Equal(9, info!.Value.Length);
    }

    [Fact]
    public async Task GetInfoIsNullForAnAbsentFile()
    {
        InMemoryFileSystem fs = new();

        Assert.Null(await fs.GetInfoAsync(VPath.Create("a.bin")));
    }

    [Fact]
    public async Task GetInfoTimestampComesFromTheInjectedClock()
    {
        // The timestamp may be SHOWN to a user and may never decide
        // correctness, so a fact about it must not have to sleep.
        DateTimeOffset when = new(2001, 11, 16, 0, 0, 0, TimeSpan.Zero);
        FakeTimeProvider clock = new(when);
        InMemoryFileSystem fs = new InMemoryFileSystem(clock).AddFile("a.bin", new byte[1]);

        FileInfoSnapshot? info = await fs.GetInfoAsync(
            VPath.Create("a.bin"));

        Assert.Equal(when, info!.Value.LastWriteTimeUtc);
    }

    [Fact]
    public async Task WritingThroughAStreamPublishesOnDispose()
    {
        InMemoryFileSystem fs = new();

        Stream stream = await fs.OpenWriteAsync(
            VPath.Create("out/a.bin"));

        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
        }

        Assert.Equal([1, 2, 3], fs.GetBytes(VPath.Create("out/a.bin")));
    }

    [Fact]
    public async Task ReplaceWritesTheNewContents()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem().AddFile("a.bin", new byte[] { 9 });

        await fs.ReplaceAsync(
            VPath.Create("a.bin"),
            async (stream, token) => await stream.WriteAsync(new byte[] { 1, 2 }, token));

        Assert.Equal([1, 2], fs.GetBytes(VPath.Create("a.bin")));
    }

    [Fact]
    public async Task ReplaceLeavesThePreviousFileWhenTheWriterThrows()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem().AddFile("a.bin", new byte[] { 9 });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fs.ReplaceAsync(
                VPath.Create("a.bin"),
                async (stream, token) =>
                {
                    await stream.WriteAsync(new byte[] { 1 }, token);
                    throw new InvalidOperationException("the compile died");
                }));

        Assert.Equal([9], fs.GetBytes(VPath.Create("a.bin")));
    }

    [Fact]
    public async Task ReplaceLeavesThePreviousFileWhenTheTokenFires()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem().AddFile("a.bin", new byte[] { 9 });
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fs.ReplaceAsync(
                VPath.Create("a.bin"),
                async (stream, token) =>
                {
                    await stream.WriteAsync(new byte[] { 1 }, token);
                    await cancellation.CancelAsync();
                },
                cancellation.Token));

        Assert.Equal([9], fs.GetBytes(VPath.Create("a.bin")));
    }

    [Fact]
    public async Task DeleteOfAnAbsentFileSucceeds()
    {
        InMemoryFileSystem fs = new();

        await fs.DeleteAsync(VPath.Create("a.bin"));

        Assert.Equal(0, fs.Count);
    }

    [Fact]
    public async Task DeleteRemovesTheFile()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem().AddFile("a.bin", new byte[1]);

        await fs.DeleteAsync(VPath.Create("a.bin"));

        Assert.False(await fs.ExistsAsync(VPath.Create("a.bin")));
    }

    [Fact]
    public async Task NonRecursiveEnumerateStaysInOneDirectory()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem()
            .AddText("materials/a.vmt", "a")
            .AddText("materials/metal/b.vmt", "b");

        List<VPath> found = await Collect(fs.EnumerateAsync(
            VPath.Create("materials"),
            "*",
            recursive: false));

        Assert.Equal([VPath.Create("materials/a.vmt")], found);
    }

    [Fact]
    public async Task RecursiveEnumerateDescends()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem()
            .AddText("materials/a.vmt", "a")
            .AddText("materials/metal/b.vmt", "b");

        List<VPath> found = await Collect(fs.EnumerateAsync(
            VPath.Create("materials"),
            "*",
            recursive: true));

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task EnumeratePatternIsCaseInsensitive()
    {
        // Source content is case-insensitive and an extension is a format tag,
        // so "*.vmt" has to find FOO.VMT on a case-sensitive disk.
        InMemoryFileSystem fs = new InMemoryFileSystem().AddText("materials/FOO.VMT", "a");

        List<VPath> found = await Collect(fs.EnumerateAsync(
            VPath.Create("materials"),
            "*.vmt",
            recursive: false));

        Assert.Single(found);
    }

    [Fact]
    public async Task EnumerateAtTheRootSeesRootFilesOnly()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem()
            .AddText("a.txt", "a")
            .AddText("sub/b.txt", "b");

        List<VPath> found = await Collect(fs.EnumerateAsync(
            VPath.Empty,
            "*",
            recursive: false));

        Assert.Equal([VPath.Create("a.txt")], found);
    }

    [Fact]
    public async Task AddingTheSamePathTwiceReplacesIt()
    {
        InMemoryFileSystem fs = new InMemoryFileSystem()
            .AddText("a.txt", "first")
            .AddText("a.txt", "second");

        using IMemoryOwner<byte> owner = await fs.ReadAllAsync(
            VPath.Create("a.txt"));

        Assert.Equal("second", Encoding.UTF8.GetString(owner.Memory.Span));
    }

    [Fact]
    public void AddFileCopiesTheCallersArray()
    {
        byte[] scratch = [1, 2, 3];
        InMemoryFileSystem fs = new InMemoryFileSystem().AddFile("a.bin", scratch);
        scratch[0] = 99;

        Assert.Equal([1, 2, 3], fs.GetBytes(VPath.Create("a.bin")));
    }

    internal static async Task<List<VPath>> Collect(IAsyncEnumerable<VPath> paths)
    {
        List<VPath> found = [];

        await foreach (VPath path in paths)
        {
            found.Add(path);
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        return found;
    }

    /// <summary>
    /// A clock that does not move, so a timestamp fact is a fact and not a race.
    /// </summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
