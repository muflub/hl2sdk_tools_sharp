//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.IO.Compression;
using System.Text;

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// The BSP's embedded pak, read so it can be mounted as content.
/// </summary>
/// <remarks>
/// The fixtures are built with <see cref="ZipArchive"/> — an independent
/// implementation of the format, in the BCL — rather than by this reader's own
/// mirror image, so a reader that misread an offset cannot agree with a writer
/// that wrote it the same wrong way.
/// </remarks>
public class PakArchiveTests
{
    [Fact]
    public async Task AStoredEntryReadsBack()
    {
        // vbsp writes STORE, so this is the case that matters.
        await using PakArchive pak = PakArchive.Open(
            Zip(CompressionLevel.NoCompression, ("materials/a.vmt", "LightmappedGeneric")),
            "test.bsp:PAKFILE");

        using IMemoryOwner<byte>? owner = await pak.ReadAsync(VPath.Create("materials/a.vmt"));

        Assert.Equal("LightmappedGeneric", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    [Fact]
    public async Task ADeflatedEntryReadsBack()
    {
        // Not what vbsp writes, but what a modder's zip tool writes, and
        // DeflateStream is in the BCL, so refusing it would be a confusing
        // failure for four lines saved.
        string text = new('a', 4096);

        await using PakArchive pak = PakArchive.Open(
            Zip(CompressionLevel.Optimal, ("scripts/a.txt", text)),
            "test.bsp:PAKFILE");

        using IMemoryOwner<byte>? owner = await pak.ReadAsync(VPath.Create("scripts/a.txt"));

        Assert.Equal(text, Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    [Fact]
    public async Task EveryEntryIsListed()
    {
        await using PakArchive pak = PakArchive.Open(
            Zip(CompressionLevel.NoCompression, ("a.txt", "a"), ("b/c.txt", "c")),
            "test.bsp:PAKFILE");

        Assert.Equal(2, pak.Count);
    }

    [Fact]
    public async Task AnAbsentEntryReadsAsNull()
    {
        await using PakArchive pak = PakArchive.Open(
            Zip(CompressionLevel.NoCompression, ("a.txt", "a")),
            "test.bsp:PAKFILE");

        Assert.Null(await pak.ReadAsync(VPath.Create("b.txt")));
    }

    [Fact]
    public async Task AnEmptyLumpIsAnEmptyPak()
    {
        // A map that packs nothing. Normal, and not an error.
        await using PakArchive pak = PakArchive.Open(ReadOnlyMemory<byte>.Empty, "test.bsp:PAKFILE");

        Assert.Equal(0, pak.Count);
    }

    [Fact]
    public void BytesThatAreNotAZipAreRefused()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PakArchive.Open(new byte[64], "test.bsp:PAKFILE"));

        Assert.Contains("end-of-central-directory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiagnosticNamesTheLump()
    {
        // "no end-of-central-directory record" on its own does not say which of
        // a compile's inputs was bad.
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PakArchive.Open(new byte[64], "ss_sandbox.bsp:PAKFILE"));

        Assert.Contains("ss_sandbox.bsp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryEntryIsNotContent()
    {
        // A zip records a directory as a zero-length entry whose name ends in a
        // slash. Mounting one would put an empty file at "materials" and shadow
        // whatever a later mount has there.
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("materials/");
            WriteEntry(zip, "materials/a.vmt", "a", CompressionLevel.NoCompression);
        }

        await using PakArchive pak = PakArchive.Open(buffer.ToArray(), "test.bsp:PAKFILE");

        Assert.Equal([VPath.Create("materials/a.vmt")], pak.Paths);
    }

    [Fact]
    public async Task APakMountsAsContentCaseInsensitively()
    {
        await using PakArchive pak = PakArchive.Open(
            Zip(CompressionLevel.NoCompression, ("materials/metal/metalwall048a.vmt", "wall")),
            "test.bsp:PAKFILE");

        await using ContentFileSystem content = new([ArchiveContentMount.Mount(pak, ownsArchive: false)]);

        Assert.NotNull(await content.ResolveAsync(VPath.Create("Materials/Metal/Metalwall048a.vmt")));
    }

    [Fact]
    public async Task APakMountedFirstShadowsTheGame()
    {
        // The engine mounts a map's pak at the TOP of the search path, which is
        // how a map ships its own version of a material.
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("game/materials/a.vmt", "from the game");
        DirectoryContentMount game = await DirectoryContentMount.MountAsync(disk, VPath.Create("game"));

        await using PakArchive pak = PakArchive.Open(
            Zip(CompressionLevel.NoCompression, ("materials/a.vmt", "from the pak")),
            "test.bsp:PAKFILE");

        await using ContentFileSystem content = new(
            [ArchiveContentMount.Mount(pak, ownsArchive: false), game]);

        using IMemoryOwner<byte>? owner = await content.ReadAsync(VPath.Create("materials/a.vmt"));

        Assert.Equal("from the pak", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    private static byte[] Zip(CompressionLevel level, params (string Path, string Text)[] files)
    {
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string text) in files)
            {
                WriteEntry(zip, path, text, level);
            }
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string path, string text, CompressionLevel level)
    {
        using Stream stream = zip.CreateEntry(path, level).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }
}
