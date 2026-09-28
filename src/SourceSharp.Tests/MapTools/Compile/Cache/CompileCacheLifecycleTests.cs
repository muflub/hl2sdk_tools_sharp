//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.Tests.MapTools.Rad;

using Xunit;

using static SourceSharp.Tests.MapTools.Compile.MapCompilerTests;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// What a compile does with its store around the stages: it leaves nothing
/// staged or running when it fails, it records its generation and collects
/// the store when it succeeds, and its closing report tells an unusable store
/// and a compile with no store from an empty one.
/// </summary>
public sealed class CompileCacheLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<InMemoryCacheStore> OpenAsync()
    {
        InMemoryCacheStore store = new();
        await store.OpenAsync("memory");
        return store;
    }

    // The room with bounces, so vrad builds (and the chain stages) transfers.
    private static CompileRequest WithStore(
        InMemoryFileSystem files, IContentFileSystem content, ICacheStore? store, CompileOutput? output = null) =>
        Request(files, content, output) with
        {
            Vrad = VradOptions.Default with { Bounces = 2 },
            Cache = store,
            Time = new FixedTime(Now),
        };

    private static string CacheLine(CompileResult result) =>
        Assert.Single(result.Log, l => l.StartsWith("cache: ", StringComparison.Ordinal)
            && !l.StartsWith("cache: gc", StringComparison.Ordinal)
            && !l.StartsWith("cache: store stats", StringComparison.Ordinal));

    [Fact]
    public async Task AFailureAfterTheTransferStoreStopsItsStagingAndDropsWhatWasStaged()
    {
        // The .bsp write fails while the transfer set is still being staged in
        // the background (held inside a blob write): the compile must stop the
        // staging, wait for it, and drop the staged vvis and transfer rows, so
        // that neither runs on after it nor rides the next commit.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InMemoryCacheStore inner = await OpenAsync();
        ProbeCacheStore store = new(inner);
        TaskCompletionSource staging = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int armed = 0;
        store.BeforePutBlob = async (_, token) =>
        {
            if (Volatile.Read(ref armed) != 0)
            {
                staging.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };

        IOException planted = new("planted .bsp failure");
        ProbeFileSystem output = new(files)
        {
            BeforeReplace = async path =>
            {
                if (path.FileName.EndsWith(".bsp", StringComparison.Ordinal))
                {
                    await staging.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    throw planted;
                }
            },
        };

        // Armed once vvis has stored its row: the blobs after that are vrad's.
        ActAt progress = new(
            p => p.Stage == MapCompiler.ChainStage && p.Done == 2,
            () => Volatile.Write(ref armed, 1));
        try
        {
            Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => MapCompiler.CompileAsync(
                WithStore(files, content, store, CompileOutput.ToDirectory(output, VPath.Create(MapDirectory))), progress));

            Assert.Same(planted, thrown);
            Assert.Equal(0, store.PutBlobsInFlight);
            Assert.Equal(0, inner.StagedCount);

            // The next compile's commit publishes nothing of this one's.
            await inner.CommitAsync(CancellationToken.None);
            Assert.Empty(await inner.KeysAsync(CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task ACancelAfterTheTransferStoreStopsItsStagingAndDropsWhatWasStaged()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InMemoryCacheStore inner = await OpenAsync();
        ProbeCacheStore store = new(inner);
        using CancellationTokenSource cancel = new();
        int armed = 0;
        store.BeforePutBlob = async (_, token) =>
        {
            if (Volatile.Read(ref armed) != 0)
            {
                await cancel.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        ActAt progress = new(
            p => p.Stage == MapCompiler.ChainStage && p.Done == 2,
            () => Volatile.Write(ref armed, 1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MapCompiler.CompileAsync(WithStore(files, content, store), progress, cancel.Token));

        Assert.Equal(0, store.PutBlobsInFlight);
        Assert.Equal(0, inner.StagedCount);
        await inner.CommitAsync(CancellationToken.None);
        Assert.Empty(await inner.KeysAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACompileWithNoStoreSaysTheCacheWasOff()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());

        CompileResult result = await MapCompiler.CompileAsync(WithStore(files, content, null), null);

        Assert.Equal("cache: off", CacheLine(result));
        Assert.Null(result.Cache);
    }

    [Fact]
    public async Task AnUnusableStoreIsReportedAsUnusableNotAsEmpty()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InMemoryCacheStore unopened = new();

        CompileResult result = await MapCompiler.CompileAsync(WithStore(files, content, unopened), null);

        Assert.True(result.Succeeded);
        string line = CacheLine(result);
        Assert.Contains("store: unusable, the run was cold", line, StringComparison.Ordinal);
        Assert.DoesNotContain("row(s)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUsableStoreReportsItsOwnFigures()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InMemoryCacheStore store = await OpenAsync();

        CompileResult result = await MapCompiler.CompileAsync(WithStore(files, content, store), null);
        CacheStats stats = await store.ReadStatsAsync(CancellationToken.None);

        Assert.True(stats.KeyCount > 0);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"; store: {stats.KeyCount} row(s), {stats.BlobCount} blob(s)"),
            CacheLine(result),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoreWhoseFiguresCannotBeReadSaysSoAndStillFinishes()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        ProbeCacheStore store = new(await OpenAsync()) { FailReadStats = new InvalidDataException("stats boom") };

        CompileResult result = await MapCompiler.CompileAsync(WithStore(files, content, store), null);

        Assert.True(result.Succeeded);
        Assert.Contains("cache: store stats unavailable (stats boom)", result.Log);
        Assert.DoesNotContain("; store:", CacheLine(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommittedCompileRecordsItsGenerationOnEveryRow()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InMemoryCacheStore store = await OpenAsync();

        _ = await MapCompiler.CompileAsync(WithStore(files, content, store), null);

        long stamp = Now.ToUnixTimeMilliseconds();
        Assert.Equal([stamp.ToString(CultureInfo.InvariantCulture)], await store.GenerationsAsync(CancellationToken.None));
        foreach (string key in await store.KeysAsync(CancellationToken.None))
        {
            Assert.Equal(stamp, (await store.LookupAsync(key, CancellationToken.None))!.CreatedAtMs);
        }
    }

    [Fact]
    public async Task AHitRenewsTheRowsItReplays()
    {
        // The second compile replays vvis and the transfers; their rows now
        // carry its stamp, so the GC counts their age from this use.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InMemoryCacheStore store = await OpenAsync();
        _ = await MapCompiler.CompileAsync(WithStore(files, content, store) with { Time = new FixedTime(Now.AddDays(-3)) }, null);

        CompileResult again = await MapCompiler.CompileAsync(WithStore(files, content, store), null);

        Assert.Equal(["vvis", "vrad.transfers"], again.Cache!.StageHits);
        foreach (string key in await store.KeysAsync(CancellationToken.None))
        {
            Assert.Equal(Now.ToUnixTimeMilliseconds(), (await store.LookupAsync(key, CancellationToken.None))!.CreatedAtMs);
        }
    }

    [Fact]
    public async Task ACompileCollectsTheStoreAfterItsCommit()
    {
        // A row nobody has used for a year is past the default 30 days.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InMemoryCacheStore store = await OpenAsync();
        string stale = await CacheCollectorTests.PutRowAsync(store, "stale", Now.AddDays(-365), 1000);

        CompileResult result = await MapCompiler.CompileAsync(WithStore(files, content, store), null);

        Assert.Null(await store.LookupAsync(stale, CancellationToken.None));
        Assert.Contains(result.Log, l => l.StartsWith("cache: gc dropped 1 row(s), 1 blob(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheGcWaitsWhileAnotherCompileUsesTheStore()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InMemoryCacheStore store = await OpenAsync();
        string stale = await CacheCollectorTests.PutRowAsync(store, "stale", Now.AddDays(-365), 1000);
        using IDisposable other = store.BeginRun();

        CompileResult result = await MapCompiler.CompileAsync(WithStore(files, content, store), null);

        Assert.NotNull(await store.LookupAsync(stale, CancellationToken.None));
        Assert.Contains("cache: gc deferred (another compile is using the store)", result.Log);
        Assert.Equal(1, store.RunsInFlight);
    }

    [Fact]
    public async Task AGcOffCompileLeavesTheStoreAlone()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InMemoryCacheStore store = await OpenAsync();
        string stale = await CacheCollectorTests.PutRowAsync(store, "stale", Now.AddDays(-365), 1000);

        CompileResult result = await MapCompiler.CompileAsync(
            WithStore(files, content, store) with { CachePolicy = CachePolicy.Default with { GcOnCommit = false } }, null);

        Assert.NotNull(await store.LookupAsync(stale, CancellationToken.None));
        Assert.DoesNotContain(result.Log, l => l.StartsWith("cache: gc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryRunVacuumsAfterAnEviction()
    {
        // The second compile moves a brush: it stores a new transfer set and
        // evicts the room's old one, which under EveryRun is a reclaim.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        (InMemoryFileSystem edited, IContentFileSystem editedContent) = await DiskAsync(Room(sealedRoom: true), "room");
        ProbeCacheStore store = new(await OpenAsync());
        CachePolicy everyRun = CachePolicy.Default with { Vacuum = CacheVacuum.EveryRun };
        _ = await MapCompiler.CompileAsync(WithStore(files, content, store) with { CachePolicy = everyRun }, null);
        Assert.Equal(0, store.Vacuums);

        CompileResult second = await MapCompiler.CompileAsync(
            WithStore(edited, editedContent, store) with
            {
                CachePolicy = everyRun,
                Vrad = VradOptions.Default with { Bounces = 2, Compliance = ComplianceOptions.Stock },
            },
            null);

        Assert.Contains("vrad.transfers", second.Cache!.StageMisses);
        Assert.Equal(1, store.Vacuums);
    }

    /// <summary>A clock stopped at one instant: every stamp a compile takes is the same.</summary>
    internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
