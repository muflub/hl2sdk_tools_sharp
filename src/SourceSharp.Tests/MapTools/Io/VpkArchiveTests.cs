using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Vpk;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// The VPK directory reader: the header, the three nested name loops, preload
/// bytes, the embedded chunk, multi-part archives, and what happens when the
/// bytes an entry promises are not there.
/// </summary>
/// <remarks>
/// Most of these run against <see cref="VpkFixture"/>, which needs no installed
/// game. A fixture is a stand-in for something native and this project has been
/// caught by one drifting before, so the reader is also driven over a REAL shipped
/// archive by the <see cref="InstalledGameFactAttribute"/> facts at the bottom.
/// </remarks>
public class VpkArchiveTests
{
    [Fact]
    public async Task AFileInAnArchivePartReadsBack()
    {
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture()
            .AddText("materials/metal/metalwall048a.vmt", "LightmappedGeneric")
            .Write(disk, "hl2/hl2_misc");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            using IMemoryOwner<byte>? owner = await archive.ReadAsync(
                VPath.Create("materials/metal/metalwall048a.vmt"));

            Assert.Equal("LightmappedGeneric", Encoding.UTF8.GetString(owner!.Memory.Span));
        }
    }

    [Fact]
    public async Task AnAbsentPathReadsAsNull()
    {
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture().AddText("a/b.txt", "x").Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            Assert.Null(await archive.ReadAsync(VPath.Create("a/missing.txt")));
        }
    }

    [Fact]
    public async Task ARootLevelFileKeepsItsPath()
    {
        // The format spells the archive root as a single space. A reader that
        // took it literally would produce the path " /readme.txt".
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture().AddText("readme.txt", "hello").Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            Assert.Contains(VPath.Create("readme.txt"), archive.Paths);
        }
    }

    [Fact]
    public async Task PreloadBytesArePrependedToTheArchivePart()
    {
        // A file split between the directory and an archive part. Getting the
        // order wrong, or dropping either half, still yields a plausible
        // buffer of the right length.
        byte[] contents = RandomNumberGenerator.GetBytes(256);
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture()
            .AddWithPreload("materials/a.vtf", contents, preloadBytes: 64)
            .Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            using IMemoryOwner<byte>? owner = await archive.ReadAsync(VPath.Create("materials/a.vtf"));
            Assert.Equal(contents, owner!.Memory.ToArray());
        }
    }

    [Fact]
    public async Task AFullyPreloadedFileNeedsNoArchivePart()
    {
        byte[] contents = RandomNumberGenerator.GetBytes(32);
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture()
            .AddWithPreload("scripts/a.txt", contents, preloadBytes: 32)
            .Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            using IMemoryOwner<byte>? owner = await archive.ReadAsync(VPath.Create("scripts/a.txt"));
            Assert.Equal(contents, owner!.Memory.ToArray());
        }
    }

    [Fact]
    public async Task AnEmbeddedChunkFileReadsOutOfTheDirectoryFile()
    {
        // Archive index 0x7fff means "in this same file, after the directory".
        // A reader that looked for pak01_32767.vpk would fail loudly; one that
        // forgot the base offset would silently read the directory's own bytes.
        byte[] contents = RandomNumberGenerator.GetBytes(100);
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture().AddEmbedded("scripts/a.txt", contents).Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            using IMemoryOwner<byte>? owner = await archive.ReadAsync(VPath.Create("scripts/a.txt"));
            Assert.Equal(contents, owner!.Memory.ToArray());
        }
    }

    [Fact]
    public async Task AnEmbeddedChunkArchiveWritesNoNumberedPart()
    {
        InMemoryFileSystem disk = new();
        new VpkFixture().AddEmbedded("scripts/a.txt", [1, 2, 3]).Write(disk, "pak/pak01");

        Assert.Equal([VPath.Create("pak/pak01_dir.vpk")], disk.Paths);
    }

    [Fact]
    public async Task FilesInDifferentArchivePartsBothReadBack()
    {
        byte[] first = RandomNumberGenerator.GetBytes(64);
        byte[] second = RandomNumberGenerator.GetBytes(64);
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture()
            .Add("a/one.bin", first, archiveIndex: 0)
            .Add("a/two.bin", second, archiveIndex: 3)
            .Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            using IMemoryOwner<byte>? owner = await archive.ReadAsync(VPath.Create("a/two.bin"));
            Assert.Equal(second, owner!.Memory.ToArray());
        }
    }

    [Fact]
    public async Task ANumberedPartIsNamedWithThreeDigits()
    {
        InMemoryFileSystem disk = new();
        new VpkFixture().Add("a/two.bin", [1], archiveIndex: 3).Write(disk, "pak/pak01");

        Assert.Contains(VPath.Create("pak/pak01_003.vpk"), disk.Paths);
    }

    [Fact]
    public async Task TwoFilesSharingAnExtensionAndFolderBothAppear()
    {
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture()
            .AddText("materials/a.vmt", "a")
            .AddText("materials/b.vmt", "b")
            .Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            Assert.Equal(2, archive.Count);
        }
    }

    [Fact]
    public async Task TheCrcIsReadBackFromTheDirectory()
    {
        byte[] contents = "LightmappedGeneric"u8.ToArray();
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture().Add("materials/a.vmt", contents).Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            Assert.Equal(VpkFixture.Crc32(contents), archive.Find(VPath.Create("materials/a.vmt"))!.Crc);
        }
    }

    [Fact]
    public async Task EntryLengthIsPreloadPlusParts()
    {
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture()
            .AddWithPreload("materials/a.vtf", new byte[256], preloadBytes: 64)
            .Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            Assert.Equal(256, archive.Find(VPath.Create("materials/a.vtf"))!.Length);
        }
    }

    [Fact]
    public async Task AVersionOneArchiveReadsBack()
    {
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture().AddText("a/b.txt", "v1").Write(disk, "pak/pak01", version: 1);

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            using IMemoryOwner<byte>? owner = await archive.ReadAsync(VPath.Create("a/b.txt"));
            Assert.Equal("v1", Encoding.UTF8.GetString(owner!.Memory.Span));
        }
    }

    [Fact]
    public async Task AVersionOneArchiveReportsVersionOne()
    {
        // The two headers are 12 and 28 bytes. Reading a v1 directory at a v2
        // header's offset produces garbage names rather than an error, so the
        // version has to be read and acted on, not assumed.
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture().AddText("a/b.txt", "v1").Write(disk, "pak/pak01", version: 1);

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
            Assert.Equal(1, archive.Version);
        }
    }

    [Fact]
    public async Task TheGameInfoSpellingWithoutDirOpensTheArchive()
    {
        // gameinfo.txt writes "hl2/hl2_misc.vpk"; the disk holds
        // "hl2/hl2_misc_dir.vpk".
        InMemoryFileSystem disk = new();
        new VpkFixture().AddText("a/b.txt", "x").Write(disk, "hl2/hl2_misc");

        VpkArchive archive = await VpkArchive.OpenAsync(disk, VPath.Create("hl2/hl2_misc.vpk"));
        await using (archive.ConfigureAwait(false))
        {
            Assert.Equal(1, archive.Count);
        }
    }

    [Fact]
    public async Task AnAbsentArchiveThrowsFileNotFound()
    {
        InMemoryFileSystem disk = new();

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await VpkArchive.OpenAsync(disk, VPath.Create("hl2/nope.vpk")));
    }

    [Fact]
    public async Task AFileWithTheWrongMarkerIsRefused()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddFile("pak/pak01_dir.vpk", new byte[64]);

        InvalidVpkException error = await Assert.ThrowsAsync<InvalidVpkException>(async () =>
            await VpkArchive.OpenAsync(disk, VPath.Create("pak/pak01_dir.vpk")));

        Assert.Contains("marker", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionTheFormatDoesNotDefineIsRefused()
    {
        byte[] header = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(header, VpkArchive.Marker);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 7);
        InMemoryFileSystem disk = new InMemoryFileSystem().AddFile("pak/pak01_dir.vpk", header);

        InvalidVpkException error = await Assert.ThrowsAsync<InvalidVpkException>(async () =>
            await VpkArchive.OpenAsync(disk, VPath.Create("pak/pak01_dir.vpk")));

        Assert.Contains("version 7", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryThatRunsPastTheEndOfTheFileIsRefused()
    {
        byte[] header = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(header, VpkArchive.Marker);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1 << 20);
        InMemoryFileSystem disk = new InMemoryFileSystem().AddFile("pak/pak01_dir.vpk", header);

        InvalidVpkException error = await Assert.ThrowsAsync<InvalidVpkException>(async () =>
            await VpkArchive.OpenAsync(disk, VPath.Create("pak/pak01_dir.vpk")));

        Assert.Contains("past the end", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShortReadOfAnArchivePartIsADiagnosticNamingTheEntry()
    {
        // The gate: a short read is a diagnostic, not a crash and not a short
        // buffer handed on to a VTF parser that fails somewhere unrelated.
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture()
            .Add("materials/a.vtf", RandomNumberGenerator.GetBytes(4096))
            .Write(disk, "pak/pak01");

        FaultInjectingFileSystem faulted = new(disk, FaultPlan.ShortReadAfter(100));

        VpkArchive archive = await VpkArchive.OpenAsync(disk, path);
        await using (archive.ConfigureAwait(false))
        {
        }

        VpkArchive overFault = await VpkArchive.OpenAsync(faulted, path);
        await using (overFault.ConfigureAwait(false))
        {
            InvalidVpkException error = await Assert.ThrowsAsync<InvalidVpkException>(async () =>
                await overFault.ReadAsync(VPath.Create("materials/a.vtf")));

            Assert.Contains("materials/a.vtf", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AShortReadSaysItCameUpShort()
    {
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture()
            .Add("materials/a.vtf", RandomNumberGenerator.GetBytes(4096))
            .Write(disk, "pak/pak01");

        VpkArchive archive = await VpkArchive.OpenAsync(
            new FaultInjectingFileSystem(disk, FaultPlan.ShortReadAfter(100)),
            path);

        await using (archive.ConfigureAwait(false))
        {
            InvalidVpkException error = await Assert.ThrowsAsync<InvalidVpkException>(async () =>
                await archive.ReadAsync(VPath.Create("materials/a.vtf")));

            Assert.Contains("came up short", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReadsThroughARecorderAppearInTheDependencySet()
    {
        // Everything the archive touches goes through IFileSystem, so a VPK
        // cannot be a hole in the recorder.
        InMemoryFileSystem disk = new();
        VPath path = new VpkFixture().AddText("a/b.txt", "x").Write(disk, "pak/pak01");
        RecordingFileSystem recording = new(disk);

        VpkArchive archive = await VpkArchive.OpenAsync(recording, path);
        await using (archive.ConfigureAwait(false))
        {
            Assert.Equal(
                DependencyKind.Read,
                recording.Recorder.Find(VPath.Create("pak/pak01_dir.vpk"))!.Value.Kind);
        }
    }

    [InstalledGameFact]
    public async Task ARealInstalledArchiveOpens()
    {
        await using VpkArchive archive = await OpenInstalledArchive();

        Assert.True(archive.Count > 0, $"{InstalledGameContent.ProbeArchive} reported no entries at all");
    }

    [InstalledGameFact]
    public async Task ARealInstalledArchiveIsVersionTwo()
    {
        await using VpkArchive archive = await OpenInstalledArchive();

        Assert.Equal(2, archive.Version);
    }

    [InstalledGameFact]
    public async Task ARealInstalledArchiveHoldsTheParticlesManifest()
    {
        // A named file rather than "some file", so a reader that produced 18000
        // entries with mangled paths fails this.
        await using VpkArchive archive = await OpenInstalledArchive();

        Assert.NotNull(archive.Find(VPath.Create("particles/particles_manifest.txt")));
    }

    [InstalledGameFact]
    public async Task ARealInstalledArchiveEntryMatchesItsRecordedCrc()
    {
        // The strongest check available without a second implementation: the writer
        // wrote the CRC, this reader assembled the bytes from the directory's
        // offsets and preload, and the two have to agree. Every part of the
        // parse -- header size, part offsets, preload length, the base offset
        // of an embedded chunk -- is in the blast radius.
        await using VpkArchive archive = await OpenInstalledArchive();

        VpkEntry entry = archive.Find(VPath.Create("particles/particles_manifest.txt"))!;
        using IMemoryOwner<byte>? owner = await archive.ReadAsync(entry.Path);

        Assert.Equal(entry.Crc, VpkFixture.Crc32(owner!.Memory.Span));
    }

    [InstalledGameFact]
    public async Task EveryEntryInARealInstalledArchiveHasAPathWithAnExtension()
    {
        // A three-loop parse that slipped by one byte produces entries whose
        // names are fragments of the next name. Checking the whole directory
        // rather than one file is what catches a drift that happens to leave
        // the first entries intact.
        await using VpkArchive archive = await OpenInstalledArchive();

        Assert.DoesNotContain(archive.Paths, static p => p.Extension.Length == 0);
    }

    [InstalledGameFact]
    public async Task ARealInstalledArchiveSpansMoreThanOneFile()
    {
        // hl2_misc is multi-part, which is the case an in-memory fixture with
        // one archive cannot exercise; if this ever reports one file the probe
        // archive has changed and the multi-part path is no longer covered
        // against real content.
        await using VpkArchive archive = await OpenInstalledArchive();

        int archives = archive.Paths
            .Select(archive.Find)
            .SelectMany(static e => e!.Parts)
            .Select(static p => p.ArchiveIndex)
            .Distinct()
            .Count();

        Assert.True(archives > 1, $"{InstalledGameContent.ProbeArchive} used {archives} archive part(s)");
    }

    private static async ValueTask<VpkArchive> OpenInstalledArchive()
    {
        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        VPath path = host.ToVirtualPath(InstalledGameContent.ProbeArchivePath);
        return await VpkArchive.OpenAsync(new ReadOnlyFileSystem(host), path);
    }
}
