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
/// The content layer: case-insensitive lookup over a case-sensitive disk, and
/// first-match-wins over an ordered list of mounts.
/// </summary>
public class ContentFileSystemTests
{
    [Fact]
    public async Task AMaterialResolvesThroughADifferentCasing()
    {
        // THE rule this layer exists for. A map references
        // "Metal/Metalwall048a"; the disk holds "metal/metalwall048a.vmt".
        // This works on Windows by accident and on Linux not at all.
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem()
            .AddText("game/materials/metal/metalwall048a.vmt", "wall"));

        ContentSource? source = await content.ResolveAsync(
            VPath.Create("Materials/Metal/Metalwall048a.vmt"));

        Assert.NotNull(source);
    }

    [Fact]
    public async Task AResolvedPathIsTheMountsOwnSpelling()
    {
        // Not the spelling that was asked for: the dependency recorder writes
        // down what it got, and two spellings have to agree about that or they
        // are two records for one file.
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem()
            .AddText("game/materials/metal/metalwall048a.vmt", "wall"));

        ContentSource? source = await content.ResolveAsync(
            VPath.Create("MATERIALS/METAL/METALWALL048A.VMT"));

        Assert.Equal(VPath.Create("materials/metal/metalwall048a.vmt"), source!.Value.Path);
    }

    [Fact]
    public async Task TwoSpellingsOfOneMaterialResolveToOneFile()
    {
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem()
            .AddText("game/materials/metal/metalwall048a.vmt", "wall"));

        ContentSource? first = await content.ResolveAsync(VPath.Create("Metal/../materials/Metal/Metalwall048a.vmt"));
        ContentSource? second = await content.ResolveAsync(VPath.Create("materials/metal/metalwall048a.vmt"));

        Assert.Equal(first!.Value.Path, second!.Value.Path);
    }

    [Fact]
    public async Task TwoSpellingsOfOneMaterialProduceOneDependencyRecord()
    {
        // The plan's gate, spelled out: the cache keys on the recorded input
        // set, and a material read twice under two casings must not look like
        // two inputs.
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem()
            .AddText("game/materials/metal/metalwall048a.vmt", "wall"));
        RecordingContentFileSystem recording = new(content);

        using (await recording.ReadAsync(VPath.Create("Materials/Metal/Metalwall048a.vmt")))
        {
        }

        using (await recording.ReadAsync(VPath.Create("materials/metal/metalwall048a.vmt")))
        {
        }

        Assert.Equal(1, recording.Recorder.Count);
    }

    [Fact]
    public async Task ThatOneRecordCarriesTheContentHash()
    {
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem()
            .AddText("game/materials/metal/metalwall048a.vmt", "wall"));
        RecordingContentFileSystem recording = new(content);

        using (await recording.ReadAsync(VPath.Create("Materials/Metal/Metalwall048a.vmt")))
        {
        }

        Assert.Equal(
            DependencyRecorder.Hash("wall"u8),
            recording.Recorder.Find(VPath.Create("materials/metal/metalwall048a.vmt"))!.Value.ContentHash);
    }

    [Fact]
    public async Task AnUnknownExtensionStillResolves()
    {
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem()
            .AddText("game/scripts/Surfaceproperties.TXT", "x"));

        Assert.NotNull(await content.ResolveAsync(VPath.Create("scripts/surfaceproperties.txt")));
    }

    [Fact]
    public async Task AMissResolvesToNull()
    {
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem()
            .AddText("game/materials/a.vmt", "a"));

        Assert.Null(await content.ResolveAsync(VPath.Create("materials/nope.vmt")));
    }

    [Fact]
    public async Task TheEarlierMountWins()
    {
        // The plan's gate: resolution order pinned against a fixture with the
        // same name in two mounts. Observable through ResolveAsync's mount
        // name, rather than inferred from the bytes that came back -- which
        // would also pass if the two mounts were searched in the right order
        // for the wrong reason.
        await using ContentFileSystem content = await MountTwo();

        ContentSource? source = await content.ResolveAsync(VPath.Create("materials/shared.vmt"));

        Assert.Equal("mod", source!.Value.Mount);
    }

    [Fact]
    public async Task TheEarlierMountsBytesAreWhatIsRead()
    {
        await using ContentFileSystem content = await MountTwo();

        using IMemoryOwner<byte>? owner = await content.ReadAsync(VPath.Create("materials/shared.vmt"));

        Assert.Equal("from the mod", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    [Fact]
    public async Task ALaterMountStillAnswersWhatOnlyItHas()
    {
        await using ContentFileSystem content = await MountTwo();

        ContentSource? source = await content.ResolveAsync(VPath.Create("materials/only-in-game.vmt"));

        Assert.Equal("game", source!.Value.Mount);
    }

    [Fact]
    public async Task ResolutionOrderHoldsAcrossCasingsToo()
    {
        // A shadowed file spelled differently in the two mounts. Folding is per
        // mount, so the earlier mount has to win on the FOLDED name and not
        // only on an exact one.
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddText("mod/materials/Shared.VMT", "from the mod")
            .AddText("game/materials/shared.vmt", "from the game");

        DirectoryContentMount mod = await DirectoryContentMount.MountAsync(disk, VPath.Create("mod"));
        DirectoryContentMount game = await DirectoryContentMount.MountAsync(disk, VPath.Create("game"));

        await using ContentFileSystem content = new([Named(mod, "mod"), Named(game, "game")]);

        using IMemoryOwner<byte>? owner = await content.ReadAsync(VPath.Create("materials/shared.vmt"));

        Assert.Equal("from the mod", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    [Fact]
    public async Task AVpkMountAndADirectoryMountObeyTheSameOrder()
    {
        // The two mount kinds behave the same way, which is the reason
        // IContentMount exists rather than the content file system knowing
        // about directories and archives separately.
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("game/materials/shared.vmt", "loose");
        VPath vpkPath = new VpkFixture()
            .AddText("materials/shared.vmt", "packed")
            .Write(disk, "game/pak01");

        await using SourceSharp.MapTools.Vpk.VpkArchive archive =
            await SourceSharp.MapTools.Vpk.VpkArchive.OpenAsync(disk, vpkPath);
        DirectoryContentMount loose = await DirectoryContentMount.MountAsync(disk, VPath.Create("game"));

        await using ContentFileSystem content = new(
            [ArchiveContentMount.Mount(archive, ownsArchive: false), Named(loose, "loose")]);

        using IMemoryOwner<byte>? owner = await content.ReadAsync(VPath.Create("materials/shared.vmt"));

        Assert.Equal("packed", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    [Fact]
    public async Task ARecordedMissIsInTheDependencySet()
    {
        // The plan's gate, and the reason the content layer needs its own
        // recorder: a miss is answered out of the mounts' own indexes and never
        // touches IFileSystem at all, so a recorder underneath would see
        // nothing.
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem()
            .AddText("game/materials/a.vmt", "a"));
        RecordingContentFileSystem recording = new(content);

        await recording.ResolveAsync(VPath.Create("materials/nope.vmt"));

        Assert.Equal(
            DependencyKind.Missing,
            recording.Recorder.Find(VPath.Create("materials/nope.vmt"))!.Value.Kind);
    }

    [Fact]
    public async Task ARecordedMissSurvivesIntoTheSnapshot()
    {
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem());
        RecordingContentFileSystem recording = new(content);

        await recording.ResolveAsync(VPath.Create("materials/nope.vmt"));

        Assert.Contains(
            recording.Recorder.Snapshot(),
            static d => d.Path == VPath.Create("materials/nope.vmt") && d.Kind == DependencyKind.Missing);
    }

    [Fact]
    public async Task AMissedReadIsRecordedAsAMissToo()
    {
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem());
        RecordingContentFileSystem recording = new(content);

        Assert.Null(await recording.ReadAsync(VPath.Create("materials/nope.vmt")));
        Assert.Equal(1, recording.Recorder.Count);
    }

    [Fact]
    public async Task TwoSpellingsOfOneMissAreOneRecord()
    {
        // A miss has no resolved path, so it is recorded folded -- otherwise
        // "Materials/Nope" and "materials/nope" would be two inputs for one
        // absent file and the digest would depend on how a map spelled it.
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem());
        RecordingContentFileSystem recording = new(content);

        await recording.ResolveAsync(VPath.Create("Materials/Nope.vmt"));
        await recording.ResolveAsync(VPath.Create("materials/nope.vmt"));

        Assert.Equal(1, recording.Recorder.Count);
    }

    [Fact]
    public async Task AMissBecomesAHitWhenTheFileIsAddedToAnEarlierMount()
    {
        // The scenario the recorded miss exists for. Nothing the first run READ
        // changed; the answer changed anyway, and the digest has to say so.
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("game/materials/a.vmt", "a");

        await using ContentFileSystem before = await MountDirectory(disk);
        RecordingContentFileSystem firstRun = new(before);
        await firstRun.ResolveAsync(VPath.Create("materials/new.vmt"));
        string digest = firstRun.Recorder.ComputeDigest();

        disk.AddText("game/materials/new.vmt", "new");
        await using ContentFileSystem after = await MountDirectory(disk);
        RecordingContentFileSystem secondRun = new(after);
        await secondRun.ResolveAsync(VPath.Create("materials/new.vmt"));

        Assert.NotEqual(digest, secondRun.Recorder.ComputeDigest());
    }

    [Fact]
    public async Task ReadsThroughTheContentLayerAlsoReachTheFileSystemRecorder()
    {
        // Both recorders share one DependencyRecorder, so a stage has ONE input
        // set however it reached a file -- and the file system underneath
        // cannot be read without the record either.
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("game/materials/a.vmt", "a");
        DependencyRecorder recorder = new();
        RecordingFileSystem recordingDisk = new(disk, recorder);

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(
            recordingDisk,
            VPath.Create("game"));

        await using ContentFileSystem content = new([mount]);
        RecordingContentFileSystem recording = new(content, recorder);

        using (await recording.ReadAsync(VPath.Create("Materials/A.vmt")))
        {
        }

        // The content path, recorded by the content recorder, and the file
        // system path, recorded underneath. Two entries for one read because
        // they are two different paths -- the point is that neither is missing.
        Assert.Equal(
            [VPath.Create("game/materials/a.vmt"), VPath.Create("materials/a.vmt")],
            recorder.Snapshot().Select(static d => d.Path));
    }

    [Fact]
    public async Task EnumerateSeesFilesFromEveryMount()
    {
        await using ContentFileSystem content = await MountTwo();

        List<VPath> found = await InMemoryFileSystemTests.Collect(
            content.EnumerateAsync(VPath.Create("materials"), "*.vmt"));

        Assert.Equal(
            [VPath.Create("materials/only-in-game.vmt"), VPath.Create("materials/shared.vmt")],
            found);
    }

    [Fact]
    public async Task EnumerateListsAShadowedFileOnce()
    {
        await using ContentFileSystem content = await MountTwo();

        List<VPath> found = await InMemoryFileSystemTests.Collect(
            content.EnumerateAsync(VPath.Create("materials"), "shared.vmt"));

        Assert.Single(found);
    }

    [Fact]
    public async Task EnumerateFoldsTheDirectoryItIsGiven()
    {
        await using ContentFileSystem content = await MountDirectory(new InMemoryFileSystem()
            .AddText("game/materials/metal/a.vmt", "a"));

        List<VPath> found = await InMemoryFileSystemTests.Collect(
            content.EnumerateAsync(VPath.Create("Materials/Metal")));

        Assert.Single(found);
    }

    [Fact]
    public async Task AMountRecordsTwoFilesThatDifferOnlyByCase()
    {
        // Impossible on the Windows install a mod was built on and routine on a
        // Linux one. The index keeps the first, always the same one, and says
        // which pair collided so a linter can report it -- rather than probing,
        // which would answer differently depending on which spelling was asked
        // for first.
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddText("game/materials/A.vmt", "upper")
            .AddText("game/materials/a.vmt", "lower");

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(disk, VPath.Create("game"));

        Assert.Single(mount.Collisions);
    }

    [Fact]
    public async Task AMountWithACollisionStillHoldsOnlyOneEntryForIt()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddText("game/materials/A.vmt", "upper")
            .AddText("game/materials/a.vmt", "lower");

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(disk, VPath.Create("game"));

        Assert.Single(mount.Paths);
    }

    [Fact]
    public async Task AMountAtTheRootNeedsNoPrefixStripping()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("materials/a.vmt", "a");

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(disk, VPath.Empty);

        Assert.Contains(VPath.Create("materials/a.vmt"), mount.Paths);
    }

    [Fact]
    public async Task ReadingFromAMountAtTheRootWorks()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("materials/a.vmt", "a");
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(disk, VPath.Empty);

        using IMemoryOwner<byte>? owner = await mount.ReadAsync(VPath.Create("Materials/A.vmt"));

        Assert.Equal("a", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    [Fact]
    public async Task ContentMountsCanBeReadOnly()
    {
        // What ReadOnlyFileSystem is for: a game install is an input, and a
        // compile that wrote a cubemap into it would be writing into the user's
        // Steam directory.
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("game/materials/a.vmt", "a");
        ReadOnlyFileSystem guarded = new(disk);

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(guarded, VPath.Create("game"));

        using IMemoryOwner<byte>? owner = await mount.ReadAsync(VPath.Create("materials/a.vmt"));
        Assert.Equal("a", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    /// <summary>
    /// A shared mount disposed twice (by a host's last compile and again at
    /// its shutdown), or by two callers at once, disposes each mount once;
    /// content that does not own its mounts disposes none.
    /// </summary>
    [Fact]
    public async Task DisposingTwiceDisposesEachMountOnce()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("game/materials/a.vmt", "a");
        CountingMount owned = new(await DirectoryContentMount.MountAsync(disk, VPath.Create("game")));
        ContentFileSystem content = new([owned]);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => content.DisposeAsync().AsTask()));
        await content.DisposeAsync();
        Assert.Equal(1, owned.Disposed);

        CountingMount borrowed = new(await DirectoryContentMount.MountAsync(disk, VPath.Create("game")));
        await new ContentFileSystem([borrowed], ownsMounts: false).DisposeAsync();
        Assert.Equal(0, borrowed.Disposed);
    }

    private static IContentMount Named(IContentMount mount, string name) => new RenamedMount(mount, name);

    private static async ValueTask<ContentFileSystem> MountDirectory(InMemoryFileSystem disk)
    {
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(disk, VPath.Create("game"));
        return new ContentFileSystem([mount]);
    }

    private static async ValueTask<ContentFileSystem> MountTwo()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddText("mod/materials/shared.vmt", "from the mod")
            .AddText("game/materials/shared.vmt", "from the game")
            .AddText("game/materials/only-in-game.vmt", "game only");

        DirectoryContentMount mod = await DirectoryContentMount.MountAsync(disk, VPath.Create("mod"));
        DirectoryContentMount game = await DirectoryContentMount.MountAsync(disk, VPath.Create("game"));

        return new ContentFileSystem([Named(mod, "mod"), Named(game, "game")]);
    }

    /// <summary>
    /// A mount under a chosen name, so a fact can say which mount answered
    /// without depending on how a directory mount spells itself.
    /// </summary>
    private sealed class RenamedMount(IContentMount inner, string name) : IContentMount
    {
        public string Name => name;

        public IReadOnlyCollection<VPath> Paths => inner.Paths;

        public bool TryResolve(VPath path, out VPath actual) => inner.TryResolve(path, out actual);

        public ValueTask<IMemoryOwner<byte>?> ReadAsync(
            VPath actual,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(actual, cancellationToken);

        public ValueTask<FileRange?> ReadRangeAsync(
            VPath actual,
            long offset,
            int length,
            CancellationToken cancellationToken = default) =>
            inner.ReadRangeAsync(actual, offset, length, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // A mount that counts its disposals.
    private sealed class CountingMount(IContentMount inner) : IContentMount
    {
        private int _disposed;

        public int Disposed => Volatile.Read(ref _disposed);

        public string Name => inner.Name;

        public IReadOnlyCollection<VPath> Paths => inner.Paths;

        public bool TryResolve(VPath path, out VPath actual) => inner.TryResolve(path, out actual);

        public ValueTask<IMemoryOwner<byte>?> ReadAsync(
            VPath actual,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(actual, cancellationToken);

        public ValueTask<FileRange?> ReadRangeAsync(
            VPath actual,
            long offset,
            int length,
            CancellationToken cancellationToken = default) =>
            inner.ReadRangeAsync(actual, offset, length, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            return inner.DisposeAsync();
        }
    }
}
