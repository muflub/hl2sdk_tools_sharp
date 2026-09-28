//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;

using SourceSharp.MapCompile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Vpk;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// The ranged read, on every implementation of the two file-system seams and
/// of the mounts and archives under them.
/// </summary>
/// <remarks>
/// <para>
/// One contract, checked the same way everywhere: a range in the middle of a
/// file, a range that runs past the end (short), one that starts past the end
/// (empty), a zero length, a missing file (a throw on
/// <see cref="IFileSystem"/>, null on the content side, exactly as the whole
/// read behaves), a cancelled token and negative arguments. Each also checks
/// that <see cref="FileRange.FileLength"/> is the whole file's length, because
/// that is the number a header parser judges offsets against.
/// </para>
/// <para>
/// The file is 1000 bytes of a position-dependent pattern, so a range that
/// came from the wrong offset cannot pass by having the right values.
/// </para>
/// </remarks>
public class RangeReadTests
{
    private const string FilePath = "dir/file.bin";
    private const int FileLength = 1000;

    /// <summary>Every <see cref="IFileSystem"/> in the library.</summary>
    public static TheoryData<string> FileSystems => new()
    {
        "memory", "physical", "physical-mapping", "read-only", "recording", "fault-free",
    };

    /// <summary>Every <see cref="IContentFileSystem"/>, over every kind of mount.</summary>
    public static TheoryData<string> Contents => new()
    {
        "directory", "vpk-part", "vpk-preload", "vpk-embedded", "pak-store", "pak-deflate",
        "recording", "loose-file",
    };

    private static byte[] Pattern(int length = FileLength)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) ^ (i >> 8));
        }

        return bytes;
    }

    // ------------------------------------------------------------ IFileSystem

    [Theory]
    [MemberData(nameof(FileSystems))]
    public async Task AFileSystemReadsARangeInTheMiddle(string kind)
    {
        using Built<IFileSystem> fs = BuildFileSystem(kind);

        using FileRange range = await fs.Value.ReadRangeAsync(VPath.Create(FilePath), 100, 50);

        Assert.Equal(Pattern().AsSpan(100, 50).ToArray(), range.Memory.ToArray());
        Assert.Equal(100, range.Offset);
        Assert.Equal(FileLength, range.FileLength);
    }

    [Theory]
    [MemberData(nameof(FileSystems))]
    public async Task AFileSystemRangePastTheEndComesBackShort(string kind)
    {
        using Built<IFileSystem> fs = BuildFileSystem(kind);

        using FileRange tail = await fs.Value.ReadRangeAsync(VPath.Create(FilePath), 990, 50);
        using FileRange beyond = await fs.Value.ReadRangeAsync(VPath.Create(FilePath), 5000, 50);

        Assert.Equal(Pattern().AsSpan(990).ToArray(), tail.Memory.ToArray());
        Assert.Equal(FileLength, tail.FileLength);
        Assert.Equal(0, beyond.Memory.Length);
        Assert.Equal(FileLength, beyond.FileLength);
    }

    [Theory]
    [MemberData(nameof(FileSystems))]
    public async Task AFileSystemZeroLengthRangeIsEmptyButKnowsTheLength(string kind)
    {
        using Built<IFileSystem> fs = BuildFileSystem(kind);

        using FileRange range = await fs.Value.ReadRangeAsync(VPath.Create(FilePath), 10, 0);

        Assert.Equal(0, range.Memory.Length);
        Assert.Equal(FileLength, range.FileLength);
    }

    [Theory]
    [MemberData(nameof(FileSystems))]
    public async Task AFileSystemRangeOfAMissingFileFailsLikeAWholeRead(string kind)
    {
        using Built<IFileSystem> fs = BuildFileSystem(kind);

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await fs.Value.ReadAllAsync(VPath.Create("dir/missing.bin")));
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await fs.Value.ReadRangeAsync(VPath.Create("dir/missing.bin"), 0, 10));
    }

    [Theory]
    [MemberData(nameof(FileSystems))]
    public async Task AFileSystemRangeObservesACancelledToken(string kind)
    {
        using Built<IFileSystem> fs = BuildFileSystem(kind);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fs.Value.ReadRangeAsync(VPath.Create(FilePath), 0, 10, new CancellationToken(canceled: true)));
    }

    [Theory]
    [MemberData(nameof(FileSystems))]
    public async Task AFileSystemRefusesANegativeOffsetOrLength(string kind)
    {
        using Built<IFileSystem> fs = BuildFileSystem(kind);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await fs.Value.ReadRangeAsync(VPath.Create(FilePath), -1, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await fs.Value.ReadRangeAsync(VPath.Create(FilePath), 0, -1));
    }

    /// <summary>
    /// A symlinked file reads its TARGET's bytes and reports the target's
    /// length, the same file <see cref="IFileSystem.ReadAllAsync"/> reads --
    /// not the link, whose own length on Linux is the length of the path it
    /// holds.
    /// </summary>
    [SymlinkFact]
    public async Task APhysicalRangeOfASymlinkedFileIsItsTargets()
    {
        using TempTree tree = new();
        byte[] content = Pattern(5000);
        tree.Write("elsewhere/deep/target.vtf", content);
        File.CreateSymbolicLink(
            Path.Combine(tree.Root, "link.vtf"),
            Path.Combine(tree.Root, "elsewhere", "deep", "target.vtf"));
        PhysicalFileSystem fs = tree.FileSystem();

        using FileRange head = await fs.ReadRangeAsync(VPath.Create("link.vtf"), 0, 336);
        using FileRange tail = await fs.ReadRangeAsync(VPath.Create("link.vtf"), 4900, 336);
        using IMemoryOwner<byte> whole = await fs.ReadAllAsync(VPath.Create("link.vtf"));

        Assert.Equal(content.AsSpan(0, 336).ToArray(), head.Memory.ToArray());
        Assert.Equal(5000, head.FileLength);
        Assert.Equal(content.AsSpan(4900).ToArray(), tail.Memory.ToArray());
        Assert.Equal(whole.Memory.Length, head.FileLength);
    }

    /// <summary>A link whose target is gone is a missing file for a range read too.</summary>
    [SymlinkFact]
    public async Task APhysicalRangeOfADanglingSymlinkIsAMissingFile()
    {
        using TempTree tree = new();
        File.CreateSymbolicLink(Path.Combine(tree.Root, "link.vtf"), Path.Combine(tree.Root, "gone.vtf"));

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await tree.FileSystem().ReadRangeAsync(VPath.Create("link.vtf"), 0, 16));
    }

    /// <summary>
    /// A physical range read never maps the file, however low the threshold:
    /// the bytes come back in pooled storage, not in a view of the file.
    /// </summary>
    [Fact]
    public async Task APhysicalRangeIsNeverMemoryMapped()
    {
        using TempTree tree = new();
        tree.Write(FilePath, Pattern());

        using FileRange range = await tree.MappingFileSystem().ReadRangeAsync(VPath.Create(FilePath), 0, 100);

        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
            (ReadOnlyMemory<byte>)range.Memory, out ArraySegment<byte> _));
    }

    /// <summary>
    /// The recording decorator records a range read as a read of the WHOLE
    /// file, with the whole file's hash, so the dependency set is the same
    /// whichever read a stage used.
    /// </summary>
    [Fact]
    public async Task ARecordedRangeIsRecordedWithTheWholeFilesHash()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddFile(FilePath, Pattern());
        RecordingFileSystem recording = new(disk);

        using (await recording.ReadRangeAsync(VPath.Create(FilePath), 0, 16))
        {
        }

        FileDependency? dependency = recording.Recorder.Find(VPath.Create(FilePath));
        Assert.NotNull(dependency);
        Assert.Equal(DependencyKind.Read, dependency.Value.Kind);
        Assert.Equal(DependencyRecorder.Hash(Pattern()), dependency.Value.ContentHash);
    }

    [Fact]
    public async Task ARecordedRangeOfAMissingFileIsRecordedAsAMiss()
    {
        RecordingFileSystem recording = new(new InMemoryFileSystem());

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await recording.ReadRangeAsync(VPath.Create("dir/missing.bin"), 0, 16));

        Assert.Equal(DependencyKind.Missing, recording.Recorder.Find(VPath.Create("dir/missing.bin"))!.Value.Kind);
    }

    /// <summary>
    /// A short-read plan truncates a range at the planned byte of the FILE,
    /// and the range still reports the length the file claims: the shape a
    /// header reader has to notice.
    /// </summary>
    [Fact]
    public async Task AFaultPlanCutsARangeShortAtItsFilePosition()
    {
        FaultInjectingFileSystem fs = new(
            new InMemoryFileSystem().AddFile(FilePath, Pattern()), FaultPlan.ShortReadAfter(120));

        using FileRange across = await fs.ReadRangeAsync(VPath.Create(FilePath), 100, 50);
        using FileRange after = await fs.ReadRangeAsync(VPath.Create(FilePath), 200, 50);
        using FileRange before = await fs.ReadRangeAsync(VPath.Create(FilePath), 0, 100);

        Assert.Equal(Pattern().AsSpan(100, 20).ToArray(), across.Memory.ToArray());
        Assert.Equal(FileLength, across.FileLength);
        Assert.Equal(0, after.Memory.Length);
        Assert.Equal(Pattern().AsSpan(0, 100).ToArray(), before.Memory.ToArray());
    }

    [Fact]
    public async Task AFaultPlanCancelsARangeThatReachesItsByte()
    {
        FaultInjectingFileSystem fs = new(
            new InMemoryFileSystem().AddFile(FilePath, Pattern()), FaultPlan.CancelReadAfter(120));

        using (FileRange ok = await fs.ReadRangeAsync(VPath.Create(FilePath), 0, 120))
        {
            Assert.Equal(120, ok.Memory.Length);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fs.ReadRangeAsync(VPath.Create(FilePath), 100, 50));
    }

    // ------------------------------------------------------ IContentFileSystem

    [Theory]
    [MemberData(nameof(Contents))]
    public async Task ContentReadsARangeInTheMiddle(string kind)
    {
        await using BuiltContent content = await BuildContentAsync(kind);

        using FileRange? range = await content.Value.ReadRangeAsync(VPath.Create("Materials/File.VTF"), 100, 50);

        Assert.NotNull(range);
        Assert.Equal(Pattern().AsSpan(100, 50).ToArray(), range.Memory.ToArray());
        Assert.Equal(FileLength, range.FileLength);
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public async Task ContentRangePastTheEndComesBackShort(string kind)
    {
        await using BuiltContent content = await BuildContentAsync(kind);

        using FileRange? tail = await content.Value.ReadRangeAsync(VPath.Create("materials/file.vtf"), 990, 50);
        using FileRange? beyond = await content.Value.ReadRangeAsync(VPath.Create("materials/file.vtf"), 5000, 50);

        Assert.Equal(Pattern().AsSpan(990).ToArray(), tail!.Memory.ToArray());
        Assert.Equal(FileLength, tail.FileLength);
        Assert.Equal(0, beyond!.Memory.Length);
        Assert.Equal(FileLength, beyond.FileLength);
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public async Task ContentZeroLengthRangeIsEmptyButKnowsTheLength(string kind)
    {
        await using BuiltContent content = await BuildContentAsync(kind);

        using FileRange? range = await content.Value.ReadRangeAsync(VPath.Create("materials/file.vtf"), 10, 0);

        Assert.Equal(0, range!.Memory.Length);
        Assert.Equal(FileLength, range.FileLength);
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public async Task ContentRangeOfAMissingFileIsNullLikeAWholeRead(string kind)
    {
        await using BuiltContent content = await BuildContentAsync(kind);

        Assert.Null(await content.Value.ReadAsync(VPath.Create("materials/missing.vtf")));
        Assert.Null(await content.Value.ReadRangeAsync(VPath.Create("materials/missing.vtf"), 0, 10));
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public async Task ContentRangeObservesACancelledToken(string kind)
    {
        await using BuiltContent content = await BuildContentAsync(kind);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await content.Value.ReadRangeAsync(
                VPath.Create("materials/file.vtf"), 0, 10, new CancellationToken(canceled: true)));
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public async Task ContentRefusesANegativeOffsetOrLength(string kind)
    {
        await using BuiltContent content = await BuildContentAsync(kind);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await content.Value.ReadRangeAsync(VPath.Create("materials/file.vtf"), -1, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await content.Value.ReadRangeAsync(VPath.Create("materials/file.vtf"), 0, -1));
    }

    /// <summary>
    /// A range comes from the mount a whole read would use, even when a later
    /// mount's copy is longer and would have filled the range.
    /// </summary>
    [Fact]
    public async Task ContentRangeComesFromTheFirstMountOnly()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddFile("mod/materials/file.vtf", Pattern(10))
            .AddFile("game/materials/file.vtf", Pattern());
        await using ContentFileSystem content = new(
        [
            await DirectoryContentMount.MountAsync(disk, VPath.Create("mod")),
            await DirectoryContentMount.MountAsync(disk, VPath.Create("game")),
        ]);

        using FileRange? range = await content.ReadRangeAsync(VPath.Create("materials/file.vtf"), 0, 100);

        Assert.Equal(Pattern(10), range!.Memory.ToArray());
        Assert.Equal(10, range.FileLength);
    }

    [Fact]
    public async Task AMountAnswersNullForAPathItDoesNotHold()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddFile("game/materials/file.vtf", Pattern());
        DirectoryContentMount directory = await DirectoryContentMount.MountAsync(disk, VPath.Create("game"));
        await using ArchiveContentMount archive = ArchiveContentMount.Mount(
            PakArchive.Open(Zip(CompressionLevel.NoCompression, ("materials/file.vtf", Pattern())), "t.bsp:PAKFILE"));

        Assert.Null(await directory.ReadRangeAsync(VPath.Create("materials/other.vtf"), 0, 10));
        Assert.Null(await archive.ReadRangeAsync(VPath.Create("materials/other.vtf"), 0, 10));
    }

    /// <summary>
    /// A range inside a VPK entry's preload never opens the archive part: the
    /// part is deleted, and a whole read -- which needs it -- fails where the
    /// range read does not.
    /// </summary>
    [Fact]
    public async Task AVpkRangeInsideThePreloadTouchesNoArchivePart()
    {
        InMemoryFileSystem disk = new();
        VPath dir = new VpkFixture().AddWithPreload("materials/file.vtf", Pattern(), 400).Write(disk, "pak/pak01");
        await disk.DeleteAsync(VPath.Create("pak/pak01_000.vpk"));
        await using VpkArchive archive = await VpkArchive.OpenAsync(disk, dir);

        using FileRange? header = await archive.ReadRangeAsync(VPath.Create("materials/file.vtf"), 0, 336);

        Assert.Equal(Pattern().AsSpan(0, 336).ToArray(), header!.Memory.ToArray());
        Assert.Equal(FileLength, header.FileLength);
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await archive.ReadAsync(VPath.Create("materials/file.vtf")));
    }

    /// <summary>
    /// A range that starts after the preload reads only from the part, at the
    /// right place in it.
    /// </summary>
    [Fact]
    public async Task AVpkRangePastThePreloadReadsFromThePart()
    {
        InMemoryFileSystem disk = new();
        VPath dir = new VpkFixture()
            .Add("materials/before.vtf", Pattern(77))
            .AddWithPreload("materials/file.vtf", Pattern(), 100)
            .Write(disk, "pak/pak01");
        await using VpkArchive archive = await VpkArchive.OpenAsync(disk, dir);

        using FileRange? range = await archive.ReadRangeAsync(VPath.Create("materials/file.vtf"), 500, 40);

        Assert.Equal(Pattern().AsSpan(500, 40).ToArray(), range!.Memory.ToArray());
    }

    /// <summary>
    /// A truncated archive part fails a range with the same exception a whole
    /// read gives, but only when the missing bytes are inside the range.
    /// </summary>
    [Fact]
    public async Task AVpkRangeFailsOnlyWhenItsOwnBytesAreMissing()
    {
        InMemoryFileSystem disk = new();
        VPath dir = new VpkFixture().Add("materials/file.vtf", Pattern()).Write(disk, "pak/pak01");
        VpkArchive archive = await VpkArchive.OpenAsync(
            new FaultInjectingFileSystem(disk, FaultPlan.ShortReadAfter(500)), dir);

        await using (archive.ConfigureAwait(false))
        {
            using (FileRange? head = await archive.ReadRangeAsync(VPath.Create("materials/file.vtf"), 0, 336))
            {
                Assert.Equal(Pattern().AsSpan(0, 336).ToArray(), head!.Memory.ToArray());
            }

            await Assert.ThrowsAsync<InvalidVpkException>(async () =>
                await archive.ReadRangeAsync(VPath.Create("materials/file.vtf"), 400, 336));
            await Assert.ThrowsAsync<InvalidVpkException>(async () =>
                await archive.ReadAsync(VPath.Create("materials/file.vtf")));
        }
    }

    [Fact]
    public async Task AVpkRangeAfterDisposeIsRefused()
    {
        InMemoryFileSystem disk = new();
        VPath dir = new VpkFixture().Add("materials/file.vtf", Pattern()).Write(disk, "pak/pak01");
        VpkArchive archive = await VpkArchive.OpenAsync(disk, dir);
        await archive.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await archive.ReadRangeAsync(VPath.Create("materials/file.vtf"), 0, 10));
    }

    /// <summary>
    /// A deflated pak entry that inflates to more than its directory declares
    /// is refused by a range read exactly as by a whole read: the range does
    /// not get to skip the check.
    /// </summary>
    [Fact]
    public async Task APakRangeOfAMalformedDeflatedEntryFailsLikeAWholeRead()
    {
        byte[] zip = Zip(CompressionLevel.Optimal, ("materials/file.vtf", Pattern()));
        // The uncompressed size in the central directory, shrunk by one.
        int central = LastIndexOf(zip, 0x02014b50);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + 24), FileLength - 1);
        await using PakArchive pak = PakArchive.Open(zip, "t.bsp:PAKFILE");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await pak.ReadAsync(VPath.Create("materials/file.vtf")));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await pak.ReadRangeAsync(VPath.Create("materials/file.vtf"), 0, 16));
    }

    /// <summary>
    /// The recording content decorator records a range as a whole-file read:
    /// the hash in the dependency set is the whole file's, which is what a
    /// content bundle checks the file against later.
    /// </summary>
    [Fact]
    public async Task ARecordedContentRangeIsRecordedWithTheWholeFilesHash()
    {
        await using BuiltContent built = await BuildContentAsync("directory");
        RecordingContentFileSystem recording = new(built.Value);

        using (await recording.ReadRangeAsync(VPath.Create("MATERIALS/file.vtf"), 0, 16))
        {
        }

        Assert.Null(await recording.ReadRangeAsync(VPath.Create("materials/missing.vtf"), 0, 16));

        FileDependency? read = recording.Recorder.Find(VPath.Create("materials/file.vtf"));
        Assert.Equal(DependencyKind.Read, read!.Value.Kind);
        Assert.Equal(DependencyRecorder.Hash(Pattern()), read.Value.ContentHash);
        Assert.Equal(DependencyKind.Missing, recording.Recorder.Find(VPath.Create("materials/missing.vtf"))!.Value.Kind);
    }

    /// <summary>
    /// vrad's loose-file layer serves a named disk file ahead of the game for
    /// a range, as it does for a whole read, and hands anything else on.
    /// </summary>
    [Fact]
    public async Task TheLooseFileLayerServesItsNamedFileFirst()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddFile("maps/lights.rad", Pattern(20))
            .AddFile("game/lights.rad", Pattern(30));
        await using ContentFileSystem game = new([await DirectoryContentMount.MountAsync(disk, VPath.Create("game"))]);
        IContentFileSystem loose = Loose(disk, game, ("lights.rad", "maps/lights.rad"));
        IContentFileSystem looseAlone = Loose(disk, inner: null, ("lights.rad", "maps/lights.rad"));

        using FileRange? named = await loose.ReadRangeAsync(VPath.Create("lights.rad"), 0, 100);
        await disk.DeleteAsync(VPath.Create("maps/lights.rad"));
        using FileRange? fellThrough = await loose.ReadRangeAsync(VPath.Create("lights.rad"), 0, 100);

        Assert.Equal(20, named!.FileLength);
        Assert.Equal(30, fellThrough!.FileLength);
        Assert.Null(await looseAlone.ReadRangeAsync(VPath.Create("lights.rad"), 0, 100));
    }

    // --------------------------------------------------------------- FileRange

    [Fact]
    public void AvailableClipsToTheFile()
    {
        Assert.Equal(50, FileRange.Available(100, 50, 1000));
        Assert.Equal(10, FileRange.Available(990, 50, 1000));
        Assert.Equal(0, FileRange.Available(1000, 50, 1000));
        Assert.Equal(0, FileRange.Available(long.MaxValue, 50, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileRange.Available(-1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileRange.Available(0, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileRange.Available(0, 1, -1));
    }

    [Fact]
    public void ARentedRangeMayNotClaimBytesPastTheEnd()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FileRange.Rent(11, 990, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileRange.Rent(-1, 0, 1000));
        using FileRange empty = FileRange.Rent(0, 5000, 1000);
        Assert.Equal(0, empty.Memory.Length);
    }

    [Fact]
    public void ACopiedRangePastAnyIntOffsetIsEmpty()
    {
        using FileRange range = FileRange.Copy(Pattern(), long.MaxValue, 10);

        Assert.Equal(0, range.Memory.Length);
        Assert.Equal(FileLength, range.FileLength);
        Assert.Equal(long.MaxValue, range.Offset);
    }

    [Fact]
    public void ADisposedRangeRefusesItsMemoryAndDisposesTwiceSafely()
    {
        FileRange range = FileRange.Copy(Pattern(), 0, 10);
        range.Dispose();
        range.Dispose();

        Assert.Throws<ObjectDisposedException>(() => range.Memory);
    }

    // ---------------------------------------------------------------- fixtures

    private static Built<IFileSystem> BuildFileSystem(string kind)
    {
        InMemoryFileSystem memory = new InMemoryFileSystem().AddFile(FilePath, Pattern());

        switch (kind)
        {
            case "memory":
                return new(memory, null);
            case "physical":
            case "physical-mapping":
                TempTree tree = new();
                tree.Write(FilePath, Pattern());
                return new(kind == "physical" ? tree.FileSystem() : tree.MappingFileSystem(), tree);
            case "read-only":
                return new(new ReadOnlyFileSystem(memory), null);
            case "recording":
                return new(new RecordingFileSystem(memory), null);
            case "fault-free":
                return new(new FaultInjectingFileSystem(memory, FaultPlan.None), null);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static async Task<BuiltContent> BuildContentAsync(string kind)
    {
        InMemoryFileSystem disk = new();

        switch (kind)
        {
            case "directory":
            case "recording":
            case "loose-file":
            {
                disk.AddFile("game/materials/file.vtf", Pattern());
                ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Create("game"))]);
                return kind switch
                {
                    "recording" => new(new RecordingContentFileSystem(content), content),
                    "loose-file" => new(Loose(disk, content), content),
                    _ => new(content, content),
                };
            }

            case "vpk-part":
            case "vpk-preload":
            case "vpk-embedded":
            {
                VpkFixture fixture = new VpkFixture().Add("materials/other.vtf", Pattern(33));
                fixture = kind switch
                {
                    "vpk-part" => fixture.Add("materials/file.vtf", Pattern()),
                    // 120 preloaded bytes, so the middle range spans the
                    // preload and the part.
                    "vpk-preload" => fixture.AddWithPreload("materials/file.vtf", Pattern(), 120),
                    _ => fixture.AddEmbedded("materials/file.vtf", Pattern()),
                };
                VPath dir = fixture.Write(disk, "pak/pak01");
                VpkArchive archive = await VpkArchive.OpenAsync(disk, dir);
                ContentFileSystem content = new([ArchiveContentMount.Mount(archive)]);
                return new(content, content);
            }

            case "pak-store":
            case "pak-deflate":
            {
                PakArchive pak = PakArchive.Open(
                    Zip(kind == "pak-store" ? CompressionLevel.NoCompression : CompressionLevel.Optimal,
                        ("materials/file.vtf", Pattern())),
                    "t.bsp:PAKFILE");
                ContentFileSystem content = new([ArchiveContentMount.Mount(pak)]);
                return new(content, content);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>
    /// vrad's loose-file content layer. Internal to the CLI, which gets no
    /// <c>InternalsVisibleTo</c>, so it is built by reflection and used
    /// through the public interface it implements.
    /// </summary>
    private static IContentFileSystem Loose(
        IFileSystem disk, IContentFileSystem? inner, params (string Name, string Path)[] files)
    {
        Type type = typeof(VradCommand).GetNestedType("LooseFileContent", BindingFlags.NonPublic)!;
        object loose = Activator.CreateInstance(type, disk, inner)!;
        MethodInfo add = type.GetMethod("Add")!;
        foreach ((string name, string path) in files)
        {
            add.Invoke(loose, [name, path]);
        }

        return (IContentFileSystem)loose;
    }

    private static byte[] Zip(CompressionLevel level, params (string Path, byte[] Bytes)[] files)
    {
        using MemoryStream buffer = new();
        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in files)
            {
                using Stream stream = zip.CreateEntry(path, level).Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    private static int LastIndexOf(byte[] haystack, uint signature)
    {
        for (int i = haystack.Length - 4; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(haystack.AsSpan(i)) == signature)
            {
                return i;
            }
        }

        throw new InvalidOperationException("no such signature");
    }

    private sealed class Built<T>(T value, IDisposable? cleanup) : IDisposable
    {
        public T Value { get; } = value;

        public void Dispose() => cleanup?.Dispose();
    }

    private sealed class BuiltContent(IContentFileSystem value, IAsyncDisposable owner) : IAsyncDisposable
    {
        public IContentFileSystem Value { get; } = value;

        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }
}
