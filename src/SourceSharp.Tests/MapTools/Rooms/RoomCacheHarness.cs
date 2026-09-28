//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Io;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// What the room cache facts share: a small library, its game content (as
/// it is, or with one material changed), a build of it through
/// <see cref="RoomLibraryBuild"/>, and a store that outlives a run.
/// </summary>
internal static class RoomCacheHarness
{
    public static readonly RoomDefinition[] Definitions =
    [
        RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY),
        RoomHarness.WalkableRoom("End", RoomFacing.PositiveX),
        RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        RoomHarness.WalkableRoom("tee", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY),
        RoomHarness.WalkableRoom("corner", RoomFacing.PositiveX, RoomFacing.PositiveY),
    ];

    public const string PlainVmt = "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n";

    public const string TriggerVmt = "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n";

    /// <summary>The library of <paramref name="count"/> rooms, with an extra target in each room listed in <paramref name="edited"/>.</summary>
    public static VmfDocument Library(int count = 5, params int[] edited)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Definitions[..count]);
        foreach (int room in edited)
        {
            AddTarget(library, room);
        }

        return library;
    }

    /// <summary>Adds an <c>info_target</c> inside a room's cell: an edit of that room alone.</summary>
    public static void AddTarget(VmfDocument library, int room)
    {
        float x = (room * (RoomHarness.Cell + RoomHarness.LibraryGap)) + 64f;
        VmfChunk target = new(MapFileLoader.EntityChunk);
        target.AddKey("id", (710000 + room).ToString(CultureInfo.InvariantCulture));
        target.AddKey("classname", "info_target");
        target.AddKey("targetname", "edit" + room.ToString(CultureInfo.InvariantCulture));
        target.AddKey("origin", VmfPlacement.Format(new Vec3(x, 64f, 64f)));
        library.Chunks.Add(target);
    }

    /// <summary>The harness materials as a mounted game; <paramref name="plain"/> replaces the plain material's text.</summary>
    public static async Task<ContentFileSystem> ContentAsync(string plain = PlainVmt, params (string Path, string Text)[] extra)
    {
        InMemoryFileSystem files = new();
        files.AddText($"materials/{RoomHarness.Plain}.vmt", plain);
        files.AddText($"materials/{RoomHarness.Trigger}.vmt", TriggerVmt);
        foreach ((string path, string text) in extra)
        {
            files.AddText(path, text);
        }

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        return new ContentFileSystem([mount]);
    }

    /// <summary>A store that outlives the runs a fact makes over it.</summary>
    public static async Task<InMemoryCacheStore> StoreAsync()
    {
        InMemoryCacheStore store = new();
        await store.OpenAsync("memory", CancellationToken.None);
        return store;
    }

    /// <summary>Builds a library, collecting every outcome in delivery order.</summary>
    public static async Task<List<RoomBuildOutcome>> BuildAsync(
        IReadOnlyList<LibraryRoom> rooms,
        IContentFileSystem content,
        RoomCompileCache? cache,
        Func<RoomLibraryCompileSettings, RoomLibraryCompileSettings>? adjust = null,
        CancellationToken cancellationToken = default)
    {
        RoomLibraryCompileSettings settings = new(VbspOptions.Default, content)
        {
            Parallelism = new CompileParallelism { MaxDegree = 2 },
        };
        List<RoomBuildOutcome> delivered = [];
        await RoomLibraryBuild.BuildAsync(
            rooms,
            adjust?.Invoke(settings) ?? settings,
            new SourceSharp.MapTools.Nav.RoomNavPackOptions(),
            cache,
            (outcome, _) =>
            {
                delivered.Add(outcome);
                return ValueTask.CompletedTask;
            },
            cancellationToken);
        return delivered;
    }

    /// <summary>A pack item's sections as tag and bytes, in order.</summary>
    public static List<(string Tag, byte[] Bytes)> Sections(RoomPackItem item) =>
        [(RoomPack.RoomSection, item.Room.ToArray()), .. item.Extra.Select(s => (s.Tag, s.Bytes.ToArray()))];

    /// <summary>Whether two items hold the same sections, byte for byte.</summary>
    public static bool Same(RoomPackItem a, RoomPackItem b)
    {
        List<(string Tag, byte[] Bytes)> x = Sections(a);
        List<(string Tag, byte[] Bytes)> y = Sections(b);
        return a.Name == b.Name && x.Count == y.Count
            && x.Zip(y).All(p => p.First.Tag == p.Second.Tag && p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes));
    }

    /// <summary>The cache inputs the harness builds with.</summary>
    public static RoomCacheInputs Inputs { get; } = new(VbspOptions.Default);
}

/// <summary>A clock a fact sets.</summary>
internal sealed class SetClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Content that counts every resolve and read by path.</summary>
internal sealed class CountingContentFs(IContentFileSystem inner) : IContentFileSystem
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _resolves = new(StringComparer.Ordinal);

    public int Reads(string path) => _reads.GetValueOrDefault(path);

    public int Resolves(string path) => _resolves.GetValueOrDefault(path);

    public int TotalReads => _reads.Values.Sum();

    public ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default)
    {
        _resolves.AddOrUpdate(path.Value, 1, (_, n) => n + 1);
        return inner.ResolveAsync(path, cancellationToken);
    }

    public ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default)
    {
        _reads.AddOrUpdate(path.Value, 1, (_, n) => n + 1);
        return inner.ReadAsync(path, cancellationToken);
    }

    public ValueTask<FileRange?> ReadRangeAsync(VPath path, long offset, int length, CancellationToken cancellationToken = default)
    {
        _reads.AddOrUpdate(path.Value, 1, (_, n) => n + 1);
        return inner.ReadRangeAsync(path, offset, length, cancellationToken);
    }

    public IAsyncEnumerable<VPath> EnumerateAsync(VPath directory, string searchPattern = "*", CancellationToken cancellationToken = default) =>
        inner.EnumerateAsync(directory, searchPattern, cancellationToken);
}

/// <summary>
/// A store that hands every call to another, with hooks a fact sets: keep
/// the inner store open when the verb disposes this one, fail the stats
/// read the collection starts with, or run something as a commit starts.
/// </summary>
internal sealed class HookedCacheStore(ICacheStore inner) : ICacheStore
{
    public bool KeepOpen { get; init; } = true;

    public Exception? FailStats { get; set; }

    public Func<CancellationToken, ValueTask>? BeforeCommit { get; set; }

    public int Opened { get; private set; }

    public ValueTask OpenAsync(string location, CancellationToken cancellationToken)
    {
        Opened++;
        return inner.IsUsable ? ValueTask.CompletedTask : inner.OpenAsync(location, cancellationToken);
    }

    public bool IsUsable => inner.IsUsable;

    public ValueTask<CacheRecord?> LookupAsync(string key, CancellationToken cancellationToken) => inner.LookupAsync(key, cancellationToken);

    public ValueTask<IReadOnlyList<string>> KeysAsync(CancellationToken cancellationToken) => inner.KeysAsync(cancellationToken);

    public ValueTask<IReadOnlyList<string>> FindKeysByDepAsync(string path, CancellationToken cancellationToken) =>
        inner.FindKeysByDepAsync(path, cancellationToken);

    public ValueTask<IReadOnlyList<string>> LiveToolIdsAsync(CancellationToken cancellationToken) => inner.LiveToolIdsAsync(cancellationToken);

    public ValueTask PutAsync(CacheRecord record, CancellationToken cancellationToken) => inner.PutAsync(record, cancellationToken);

    public ValueTask PutBlobAsync(string blobKey, ReadOnlyMemory<byte> data, string toolId, long createdAtMs, CancellationToken cancellationToken) =>
        inner.PutBlobAsync(blobKey, data, toolId, createdAtMs, cancellationToken);

    public ValueTask<bool> HasBlobAsync(string blobKey, CancellationToken cancellationToken) => inner.HasBlobAsync(blobKey, cancellationToken);

    public ValueTask<long?> BlobSizeAsync(string blobKey, CancellationToken cancellationToken) => inner.BlobSizeAsync(blobKey, cancellationToken);

    public IDisposable BeginRun() => inner.BeginRun();

    public int RunsInFlight => inner.RunsInFlight;

    public ValueTask<byte[]?> GetBlobAsync(string blobKey, CancellationToken cancellationToken) => inner.GetBlobAsync(blobKey, cancellationToken);

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        if (BeforeCommit is { } before)
        {
            await before(cancellationToken);
        }

        await inner.CommitAsync(cancellationToken);
    }

    public void DiscardStaged() => inner.DiscardStaged();

    public ValueTask ClearAsync(CancellationToken cancellationToken) => inner.ClearAsync(cancellationToken);

    public ValueTask<IReadOnlyList<string>> GenerationsAsync(CancellationToken cancellationToken) => inner.GenerationsAsync(cancellationToken);

    public ValueTask RecordGenerationAsync(string generationId, CancellationToken cancellationToken) =>
        inner.RecordGenerationAsync(generationId, cancellationToken);

    public ValueTask RemoveGenerationAsync(string generationId, CancellationToken cancellationToken) =>
        inner.RemoveGenerationAsync(generationId, cancellationToken);

    public ValueTask<CacheStats> ReadStatsAsync(CancellationToken cancellationToken) =>
        FailStats is { } failure ? ValueTask.FromException<CacheStats>(failure) : inner.ReadStatsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<string>> CollectGarbageAsync(int maxCount, CancellationToken cancellationToken) =>
        inner.CollectGarbageAsync(maxCount, cancellationToken);

    public ValueTask DeleteBlobsAsync(IReadOnlyList<string> blobKeys, CancellationToken cancellationToken) =>
        inner.DeleteBlobsAsync(blobKeys, cancellationToken);

    public ValueTask VacuumAsync(CancellationToken cancellationToken) => inner.VacuumAsync(cancellationToken);

    public ValueTask<bool> CheckIntegrityAsync(CancellationToken cancellationToken) => inner.CheckIntegrityAsync(cancellationToken);

    public ValueTask DisposeAsync() => KeepOpen ? ValueTask.CompletedTask : inner.DisposeAsync();
}
