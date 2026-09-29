//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The merge rules of the linked level's pak (<see cref="LevelPakFiles"/>),
/// on archives built in memory: which names are renamed to the level's map
/// name, which are kept, how equal files are written once, how different
/// ones are refused, and that the archive is the same bytes however the
/// rooms come.
/// </summary>
public sealed class LevelPakFilesTests
{
    // ---- the rename rule -----------------------------------------------------

    /// <summary>A room's default cubemap pair goes under the level's name; the room's name is matched ignoring case.</summary>
    [Theory]
    [InlineData("materials/maps/hub/cubemapdefault.vtf", "hub", "materials/maps/level/cubemapdefault.vtf")]
    [InlineData("materials/maps/hub/cubemapdefault.hdr.vtf", "hub", "materials/maps/level/cubemapdefault.hdr.vtf")]
    [InlineData("materials/maps/hub/cubemapdefault.vtf", "Hub", "materials/maps/level/cubemapdefault.vtf")]
    public void TheDefaultCubemapsAreRenamedToTheLevel(string file, string room, string expected)
    {
        Assert.True(LevelPakFiles.IsRenamed(file, room));
        Assert.Equal(expected, LevelPakFiles.LinkedName(file, room, "level"));
    }

    /// <summary>The level's name comes out lower-cased, as every pak name is and as the engine looks it up.</summary>
    [Fact]
    public void ARenamedFileIsLowerCase() =>
        Assert.Equal("materials/maps/level_one/cubemapdefault.vtf", LevelPakFiles.LinkedName("materials/maps/hub/cubemapdefault.vtf", "hub", "Level_One"));

    /// <summary>
    /// Everything else keeps its name: files named after the room that its
    /// faces use (a WVT patch, a water depth patch), another room's default
    /// cubemap, a cubemap sample's copy, a file in a subfolder, and a file
    /// that is not a map's at all.
    /// </summary>
    [Theory]
    [InlineData("materials/maps/hub/unit/plain_wvt_patch.vmt")]
    [InlineData("materials/maps/hub/unit/water_depth_64.vmt")]
    [InlineData("materials/maps/hall/cubemapdefault.vtf")]
    [InlineData("materials/maps/hubx/cubemapdefault.vtf")]
    [InlineData("materials/maps/hub/c0_0_64.vtf")]
    [InlineData("materials/maps/hub/sub/cubemapdefault.vtf")]
    [InlineData("materials/maps/hub")]
    [InlineData("materials/maps/hub/")]
    [InlineData("materials/maps/hub_cubemapdefault.vtf")]
    [InlineData("materials/hub/cubemapdefault.vtf")]
    [InlineData("sound/author/embedded.wav")]
    public void EveryOtherFileKeepsItsName(string file)
    {
        Assert.False(LevelPakFiles.IsRenamed(file, "hub"));
        Assert.Equal(file, LevelPakFiles.LinkedName(file, "hub", "level"));
    }

    /// <summary>The rule's arguments are required.</summary>
    [Fact]
    public void TheRuleRefusesNulls()
    {
        Assert.Throws<ArgumentNullException>(() => LevelPakFiles.LinkedName(null!, "hub", "level"));
        Assert.Throws<ArgumentNullException>(() => LevelPakFiles.LinkedName("a", null!, "level"));
        Assert.Throws<ArgumentNullException>(() => LevelPakFiles.LinkedName("a", "hub", null!));
        Assert.Throws<ArgumentNullException>(() => LevelPakFiles.IsRenamed(null!, "hub"));
        Assert.Throws<ArgumentNullException>(() => LevelPakFiles.IsRenamed("a", null!));
        Assert.Throws<ArgumentNullException>(() => LevelPakFiles.Merge(null!, "level", CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => LevelPakFiles.Merge([], null!, CancellationToken.None));
    }

    // ---- the merge -------------------------------------------------------------

    /// <summary>No room with a file (no pak, or only empty ones and empty files) merges to nothing, so the link keeps the lump it has.</summary>
    [Fact]
    public void NoFilesMergeToNothing()
    {
        (byte[]? pak, int files) = LevelPakFiles.Merge([("hub", Pak()), ("hall", Pak(("empty.txt", [])))], "level", CancellationToken.None);
        Assert.Null(pak);
        Assert.Equal(0, files);
    }

    /// <summary>
    /// Two rooms packing equal bytes under one name, and two placements'
    /// default cubemaps renamed onto one level name, are written once; the
    /// archive lists every file in ordinal order of its linked name.
    /// </summary>
    [Fact]
    public async Task EqualFilesAreWrittenOnceInNameOrder()
    {
        byte[] cube = [1, 2, 3, 4];
        (byte[]? pak, int files) = LevelPakFiles.Merge(
            [
                ("hub", Pak(("materials/maps/hub/cubemapdefault.vtf", cube), ("z/shared.txt", [9]), ("materials/maps/hub/x_wvt_patch.vmt", [5]))),
                ("hall", Pak(("materials/maps/hall/cubemapdefault.vtf", cube), ("z/shared.txt", [9]), ("a/own.txt", [7]))),
            ],
            "level",
            CancellationToken.None);

        Assert.Equal(4, files);
        ZipArchiveReader merged = await ZipArchiveReader.ParseAsync(pak!);
        Assert.Equal(
            ["a/own.txt", "materials/maps/hub/x_wvt_patch.vmt", "materials/maps/level/cubemapdefault.vtf", "z/shared.txt"],
            merged.Entries.Select(e => e.Name));
        Assert.Equal(cube, merged.Find("materials/maps/level/cubemapdefault.vtf")!.Data);
        Assert.Equal(ZipArchiveWriter.BuildComment(), merged.Comment);
    }

    /// <summary>The archive is a function of the files: the rooms in another order give the same bytes.</summary>
    [Fact]
    public void TheArchiveDoesNotDependOnTheRoomsOrder()
    {
        (string, ZipArchiveReader) hub = ("hub", Pak(("b.txt", [1]), ("materials/maps/hub/cubemapdefault.vtf", [2])));
        (string, ZipArchiveReader) hall = ("hall", Pak(("a.txt", [3]), ("materials/maps/hall/cubemapdefault.vtf", [2])));
        Assert.Equal(
            LevelPakFiles.Merge([hub, hall], "level", CancellationToken.None).Pak,
            LevelPakFiles.Merge([hall, hub], "level", CancellationToken.None).Pak);
    }

    /// <summary>An entry's method, checksum and stored bytes are carried as the room's pak held them, a renamed one too.</summary>
    [Fact]
    public async Task AnEntryIsCarriedAsStored()
    {
        ZipArchiveWriter room = new();
        room.Add(new ZipEntry("materials/maps/hub/cubemapdefault.vtf", [1, 2, 3], ZipCompressionMethod.Lzma, 0xDEADBEEF, 99));
        ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(room.ToBytes());

        ZipEntry carried = (await ZipArchiveReader.ParseAsync(LevelPakFiles.Merge([("hub", pak)], "level", CancellationToken.None).Pak!)).Entries.Single();
        Assert.Equal("materials/maps/level/cubemapdefault.vtf", carried.Name);
        Assert.Equal(ZipCompressionMethod.Lzma, carried.CompressionMethod);
        Assert.Equal(0xDEADBEEFu, carried.Crc);
        Assert.Equal(99u, carried.UncompressedSize);
        Assert.Equal([1, 2, 3], carried.Data);
    }

    // ---- refusals ------------------------------------------------------------------

    /// <summary>Two rooms packing one name with different bytes are refused with the design's message, naming both.</summary>
    [Fact]
    public void DifferentBytesUnderOneNameAreRefused()
    {
        LinkException refused = Assert.Throws<LinkException>(() => LevelPakFiles.Merge(
            [("hub", Pak(("sound/door.wav", [1]))), ("hall", Pak(("sound/door.wav", [2])))], "level", CancellationToken.None));
        Assert.Equal("rooms hub and hall both pack sound/door.wav with different bytes.", refused.Message);
    }

    /// <summary>Default cubemaps that differ are refused under the name both are renamed to.</summary>
    [Fact]
    public void DifferentDefaultCubemapsAreRefusedUnderTheLevelsName()
    {
        LinkException refused = Assert.Throws<LinkException>(() => LevelPakFiles.Merge(
            [
                ("hub", Pak(("materials/maps/hub/cubemapdefault.vtf", [1]))),
                ("hall", Pak(("materials/maps/hall/cubemapdefault.vtf", [2]))),
            ],
            "level",
            CancellationToken.None));
        Assert.Equal("rooms hub and hall both pack materials/maps/level/cubemapdefault.vtf with different bytes.", refused.Message);
    }

    /// <summary>The same bytes under another method or checksum are other bytes: the archive would hold one of the two.</summary>
    [Fact]
    public async Task AnotherMethodOrChecksumIsAConflict()
    {
        ZipArchiveWriter lzma = new();
        lzma.Add(new ZipEntry("a.bin", [1, 2], ZipCompressionMethod.Lzma, 5, 2));
        ZipArchiveWriter crc = new();
        crc.Add(new ZipEntry("a.bin", [1, 2], ZipCompressionMethod.Store, 5, 2));
        ZipArchiveWriter size = new();
        size.Add(new ZipEntry("a.bin", [1, 2], ZipCompressionMethod.Store, Crc32.Compute([1, 2]), 3));

        foreach (ZipArchiveWriter other in new[] { lzma, crc, size })
        {
            ZipArchiveReader parsed = await ZipArchiveReader.ParseAsync(other.ToBytes());
            Assert.Throws<LinkException>(() => LevelPakFiles.Merge([("hub", Pak(("a.bin", [1, 2]))), ("hall", parsed)], "level", CancellationToken.None));
        }
    }

    /// <summary>One room's pak holding a name twice is merged when the bytes agree and refused, naming the room once, when not.</summary>
    [Fact]
    public void ANameTwiceInOneRoom()
    {
        Assert.Equal(1, LevelPakFiles.Merge([("hub", Pak(("a.txt", [1]), ("a.txt", [1])))], "level", CancellationToken.None).Files);

        LinkException refused = Assert.Throws<LinkException>(
            () => LevelPakFiles.Merge([("hub", Pak(("a.txt", [1]), ("a.txt", [2])))], "level", CancellationToken.None));
        Assert.Equal("room hub packs a.txt twice with different bytes.", refused.Message);
    }

    /// <summary>A default cubemap with no map name to rename it to is refused, rather than carried under the room's name, where nothing reads it.</summary>
    [Fact]
    public void ARenameWithoutAMapNameIsRefused()
    {
        LinkException refused = Assert.Throws<LinkException>(
            () => LevelPakFiles.Merge([("hub", Pak(("materials/maps/hub/cubemapdefault.vtf", [1])))], string.Empty, CancellationToken.None));
        Assert.Equal(
            "room hub packs materials/maps/hub/cubemapdefault.vtf, which is named after its map; the link renames it to the level's map name,"
            + " and was given none (the map name of the link's compile context).",
            refused.Message);

        // A file the link keeps needs no map name.
        Assert.Equal(1, LevelPakFiles.Merge([("hub", Pak(("a.txt", [1])))], string.Empty, CancellationToken.None).Files);
    }

    /// <summary>The merge observes its token.</summary>
    [Fact]
    public void TheMergeObservesCancellation()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => LevelPakFiles.Merge([("hub", Pak(("a.txt", [1])))], "level", cancelled.Token));
    }

    /// <summary>An archive of the given files, as a room's pak lump reads back.</summary>
    /// <remarks>
    /// The writer drops an empty file, as vbsp's does, so a pak lump written
    /// by the tools never holds one; the reader takes one, though, and a pak
    /// from elsewhere may. An empty file is therefore written as one byte
    /// and its central-directory sizes are then set to zero, which the
    /// reader reads as an empty entry.
    /// </remarks>
    internal static ZipArchiveReader Pak(params (string Name, byte[] Data)[] files)
    {
        ZipArchiveWriter writer = new();
        foreach ((string name, byte[] data) in files)
        {
            writer.Add(name, data.Length == 0 ? [0] : data);
        }

        byte[] bytes = writer.ToBytes();
        int cursor = 0;
        foreach ((_, byte[] data) in files)
        {
            cursor = bytes.AsSpan(cursor).IndexOf("PK\u0001\u0002"u8) + cursor;
            if (data.Length == 0)
            {
                bytes.AsSpan(cursor + 20, 8).Clear();
            }

            cursor += 4;
        }

        return ZipArchiveReader.ParseAsync(bytes).AsTask().Result;
    }
}
