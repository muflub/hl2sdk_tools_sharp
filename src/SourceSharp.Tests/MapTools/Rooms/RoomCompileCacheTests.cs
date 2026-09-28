//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The room cache's store seam: a compiled room is served back section for
/// section, a changed input or damaged row is a miss and never a wrong room,
/// nothing reaches the store before the commit, a failed or cancelled run
/// leaves the store as it was, and nothing outlives the run.
/// </summary>
public sealed class RoomCompileCacheTests
{
    // ---- hits ----------------------------------------------------------------

    /// <summary>
    /// A room compiled and committed by one run is served to the next, its
    /// sections byte for byte the ones the compile wrote, with the cluster
    /// count and warnings the verb prints.
    /// </summary>
    [Fact]
    public async Task ACompiledRoomIsServedByteForByteOnTheNextRun()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(3));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();

        List<RoomBuildOutcome> first;
        using (RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content))
        {
            first = await RoomCacheHarness.BuildAsync(rooms, content, cache);
            Assert.Equal(3, cache.Misses);
            Assert.Equal(0, cache.Hits);
            RoomCacheCommit commit = await cache.CommitAsync(CancellationToken.None);
            Assert.Equal(3, commit.RowsStored);
            Assert.True(commit.BytesStored > 0);
            Assert.NotNull(commit.Gc);
            Assert.Null(commit.GcFailure);
        }

        using RoomCompileCache again = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        List<RoomBuildOutcome> second = await RoomCacheHarness.BuildAsync(rooms, content, again);
        Assert.Equal(3, again.Hits);
        Assert.Equal(0, again.Misses);
        for (int i = 0; i < rooms.Count; i++)
        {
            Assert.False(first[i].Reused);
            Assert.True(second[i].Reused);
            Assert.True(RoomCacheHarness.Same(first[i].Item!, second[i].Item!), rooms[i].Definition.Name);
            Assert.Equal(first[i].ClusterCount, second[i].ClusterCount);
            Assert.Equal(first[i].NameWarnings, second[i].NameWarnings);
        }
    }

    /// <summary>
    /// The rows go in only at the commit: after the build the store has
    /// nothing staged and nothing committed, and the rooms wait in the run.
    /// </summary>
    [Fact]
    public async Task NothingReachesTheStoreBeforeTheCommit()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(2));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        await RoomCacheHarness.BuildAsync(rooms, content, cache);
        Assert.Equal(2, cache.PendingCount);
        Assert.Equal(0, store.StagedCount);
        Assert.Empty(await store.KeysAsync(CancellationToken.None));
    }

    /// <summary>
    /// Every row the run stores carries the run's recorded content: the
    /// materials read, with their hashes, and the lookups that missed.
    /// </summary>
    [Fact]
    public async Task ARowCarriesTheContentTheRunRead()
    {
        (InMemoryCacheStore store, _, _) = await PrimedAsync(2);
        IReadOnlyList<string> keys = await store.KeysAsync(CancellationToken.None);
        Assert.Equal(2, keys.Count);
        CacheRecord record = (await store.LookupAsync(keys[0], CancellationToken.None))!;
        Assert.Contains(record.Dependencies, d => d.Path.EndsWith($"{RoomHarness.Plain}.vmt", StringComparison.OrdinalIgnoreCase)
            && d.ContentHash == DependencyRecorder.Hash(System.Text.Encoding.UTF8.GetBytes(RoomCacheHarness.PlainVmt)));
        Assert.Contains(record.Dependencies, d => d.ContentHash is null);
        Assert.Equal(RoomCacheKey.StageName, record.Stage);
        Assert.Equal(ToolIdentity.Current, record.ToolId);
    }

    // ---- misses ----------------------------------------------------------------

    /// <summary>A material whose bytes changed turns every room that ran with it into a miss.</summary>
    [Fact]
    public async Task AChangedMaterialIsAMiss()
    {
        (InMemoryCacheStore store, IReadOnlyList<LibraryRoom> rooms, _) = await PrimedAsync(2);
        ContentFileSystem changed = await RoomCacheHarness.ContentAsync(RoomCacheHarness.PlainVmt + "\n");
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, changed);
        Assert.Null(await cache.TryGetAsync(rooms[0], CancellationToken.None));
        Assert.Equal(1, cache.Misses);
    }

    /// <summary>
    /// A file the compile looked for and did not find, added since, is a
    /// miss: resolution is first match wins, so the added file is what the
    /// compile would read now.
    /// </summary>
    [Fact]
    public async Task AFileAddedWhereTheCompileFoundNoneIsAMiss()
    {
        (InMemoryCacheStore store, IReadOnlyList<LibraryRoom> rooms, ContentFileSystem content) = await PrimedAsync(1);
        CacheRecord record = (await store.LookupAsync((await store.KeysAsync(CancellationToken.None))[0], CancellationToken.None))!;
        string missing = record.Dependencies.First(d => d.ContentHash is null).Path;

        using (RoomCompileCache same = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content))
        {
            Assert.NotNull(await same.TryGetAsync(rooms[0], CancellationToken.None));
        }

        ContentFileSystem added = await RoomCacheHarness.ContentAsync(RoomCacheHarness.PlainVmt, (missing, "now here"));
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, added);
        Assert.Null(await cache.TryGetAsync(rooms[0], CancellationToken.None));
    }

    /// <summary>
    /// A file the compile found without reading must still be found: a row
    /// that records one serves while it is there, and misses once it is gone.
    /// </summary>
    [Fact]
    public async Task AFoundFileThatIsGoneIsAMiss()
    {
        (InMemoryCacheStore store, IReadOnlyList<LibraryRoom> rooms, ContentFileSystem content) = await PrimedAsync(1);
        string key = (await store.KeysAsync(CancellationToken.None))[0];
        CacheRecord record = (await store.LookupAsync(key, CancellationToken.None))!;
        string plain = $"materials/{RoomHarness.Plain}.vmt";
        await store.PutAsync(record with { Dependencies = [(plain, "resolved")] }, CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);

        using (RoomCompileCache there = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content))
        {
            Assert.NotNull(await there.TryGetAsync(rooms[0], CancellationToken.None));
        }

        ContentFileSystem gone = new([await DirectoryContentMount.MountAsync(new InMemoryFileSystem(), VPath.Empty)]);
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, gone);
        Assert.Null(await cache.TryGetAsync(rooms[0], CancellationToken.None));
    }

    /// <summary>A recorded path that is not a content path at all is a miss, found or hashed.</summary>
    [Fact]
    public async Task AnUnusablePathIsAMiss()
    {
        (InMemoryCacheStore store, IReadOnlyList<LibraryRoom> rooms, ContentFileSystem content) = await PrimedAsync(1);
        string key = (await store.KeysAsync(CancellationToken.None))[0];
        CacheRecord record = (await store.LookupAsync(key, CancellationToken.None))!;
        foreach (string? hash in new[] { "resolved", new string('0', 64) })
        {
            await store.PutAsync(record with { Dependencies = [("bad\0path", hash)] }, CancellationToken.None);
            await store.CommitAsync(CancellationToken.None);
            using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
            Assert.Null(await cache.TryGetAsync(rooms[0], CancellationToken.None));
        }
    }

    /// <summary>
    /// A row that is not quite this key's (other tags stored under the key's
    /// digest), one missing a blob, one whose blob does not hash to its key,
    /// one whose meta blob does not read, and one whose first section is not
    /// the container: all misses.
    /// </summary>
    [Fact]
    public async Task ADamagedOrForeignRowIsAMiss()
    {
        (InMemoryCacheStore store, IReadOnlyList<LibraryRoom> rooms, ContentFileSystem content) = await PrimedAsync(1);
        string key = (await store.KeysAsync(CancellationToken.None))[0];
        CacheRecord record = (await store.LookupAsync(key, CancellationToken.None))!;
        string absent = new('a', 64);
        await store.PutBlobAsync(CacheKey.HashBytes([1, 2, 3]), new byte[] { 9, 9, 9 }, "t", 0, CancellationToken.None);
        byte[] badMeta = [0, 0, 0, 9];
        await store.PutBlobAsync(CacheKey.HashBytes(badMeta), badMeta, "t", 0, CancellationToken.None);
        byte[] swappedMeta = RoomCompileCache.Meta.Write(new RoomCompileCache.Meta(["ECNT"], 1, [], []));
        await store.PutBlobAsync(CacheKey.HashBytes(swappedMeta), swappedMeta, "t", 0, CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);

        CacheRecord[] damaged =
        [
            record with { ContextTags = ["cooker=other"] },
            record with { Stage = "vvis" },
            record with { Blobs = new Dictionary<string, string>(record.Blobs) { ["s00"] = absent } },
            record with { Blobs = new Dictionary<string, string>(record.Blobs) { ["s00"] = CacheKey.HashBytes([1, 2, 3]) } },
            record with { Blobs = new Dictionary<string, string>(record.Blobs) { ["meta"] = CacheKey.HashBytes(badMeta) } },
            record with { Blobs = new Dictionary<string, string>(record.Blobs.Where(b => b.Key != "meta")) },
            record with { Blobs = new Dictionary<string, string>(record.Blobs) { ["meta"] = CacheKey.HashBytes(swappedMeta) } },
        ];
        foreach (CacheRecord bad in damaged)
        {
            await store.PutAsync(bad, CancellationToken.None);
            await store.CommitAsync(CancellationToken.None);
            using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
            Assert.Null(await cache.TryGetAsync(rooms[0], CancellationToken.None));
        }
    }

    /// <summary>
    /// A meta blob round-trips, and one cut short, of another revision, with
    /// bytes after its end or with a count out of range reads as nothing.
    /// </summary>
    [Fact]
    public void TheMetaBlobRoundTripsAndRefusesDamage()
    {
        RoomCompileCache.Meta meta = new(["ROOM", "ECNT", "LNKA"], 7, ["one", "twö"], ["prop_physics crate: no hull"]);
        byte[] bytes = RoomCompileCache.Meta.Write(meta);
        RoomCompileCache.Meta? back = RoomCompileCache.Meta.Read(bytes);
        Assert.NotNull(back);
        Assert.Equal(meta.Tags, back.Tags);
        Assert.Equal(7, back.ClusterCount);
        Assert.Equal(meta.Warnings, back.Warnings);
        Assert.Equal(meta.NavWarnings, back.NavWarnings);

        for (int cut = 0; cut < bytes.Length; cut++)
        {
            Assert.Null(RoomCompileCache.Meta.Read(bytes.AsSpan(0, cut)));
        }

        Assert.Null(RoomCompileCache.Meta.Read([.. bytes, 0]));
        byte[] revision = [.. bytes];
        revision[3] = 3;
        Assert.Null(RoomCompileCache.Meta.Read(revision));
        byte[] sections = [.. bytes];
        sections[8] = 0x7F;
        Assert.Null(RoomCompileCache.Meta.Read(sections));

        // Each list's count out of range: the naming list's, then the
        // navigation list's (the last word of a blob with both empty).
        byte[] names = RoomCompileCache.Meta.Write(new RoomCompileCache.Meta([], 0, [], []));
        names[^8] = 0xFF;
        Assert.Null(RoomCompileCache.Meta.Read(names));
        byte[] navs = RoomCompileCache.Meta.Write(new RoomCompileCache.Meta([], 0, [], []));
        navs[^4] = 0xFF;
        Assert.Null(RoomCompileCache.Meta.Read(navs));

        // Each list's item length out of range: the high byte of the one
        // item's length word, ahead of its one byte of text (and, for the
        // naming list, of the navigation list's empty count).
        byte[] nameLength = RoomCompileCache.Meta.Write(new RoomCompileCache.Meta([], 0, ["x"], []));
        nameLength[^9] = 0xFF;
        Assert.Null(RoomCompileCache.Meta.Read(nameLength));
        byte[] navLength = RoomCompileCache.Meta.Write(new RoomCompileCache.Meta([], 0, [], ["x"]));
        navLength[^5] = 0xFF;
        Assert.Null(RoomCompileCache.Meta.Read(navLength));
    }

    /// <summary>
    /// The navigation warnings round-trip through the meta blob exactly: none,
    /// one, several in order, empty strings among them, and text outside
    /// ASCII (UTF-8, several bytes per character), independent of the naming
    /// warnings beside them.
    /// </summary>
    [Fact]
    public void TheMetaBlobCarriesTheNavigationWarnings()
    {
        IReadOnlyList<string>[] lists =
        [
            [],
            ["prop_physics 800006: model \"models/missing.mdl\" has no hull the content gives, so it is left out of the navigation's obstacles."],
            ["prop_physics kiste_ö: model \"models/größe/箱.mdl\" fehlt", "", "second — with a dash", "\U0001F4E6 crate"],
        ];
        foreach (IReadOnlyList<string> nav in lists)
        {
            foreach (IReadOnlyList<string> names in (IReadOnlyList<string>[])[[], ["naming ✓"]])
            {
                RoomCompileCache.Meta meta = new(["ROOM", "NVR0"], 3, names, nav);
                RoomCompileCache.Meta? back = RoomCompileCache.Meta.Read(RoomCompileCache.Meta.Write(meta));
                Assert.NotNull(back);
                Assert.Equal(names, back.Warnings);
                Assert.Equal(nav, back.NavWarnings);
                Assert.Equal(meta.Tags, back.Tags);
                Assert.Equal(3, back.ClusterCount);
            }
        }
    }

    /// <summary>
    /// A meta blob of revision 1 (the layout before the navigation warnings:
    /// the naming warnings are its last list) reads as nothing, and a row
    /// that carries one is a miss, so the room compiles again and its
    /// navigation warnings are printed rather than silently dropped.
    /// </summary>
    [Fact]
    public async Task ARevisionOneMetaBlobIsAMiss()
    {
        byte[] old = RevisionOneMeta(["ROOM"], 1, ["an old naming warning"]);
        Assert.Null(RoomCompileCache.Meta.Read(old));

        // The same blob relabelled as the current revision is not a
        // readable current blob either: it lacks the navigation list.
        byte[] relabelled = [.. old];
        relabelled[3] = 2;
        Assert.Null(RoomCompileCache.Meta.Read(relabelled));

        (InMemoryCacheStore store, IReadOnlyList<LibraryRoom> rooms, ContentFileSystem content) = await PrimedAsync(1);
        string key = (await store.KeysAsync(CancellationToken.None))[0];
        CacheRecord record = (await store.LookupAsync(key, CancellationToken.None))!;
        using (RoomCompileCache current = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content))
        {
            Assert.NotNull(await current.TryGetAsync(rooms[0], CancellationToken.None));
        }

        byte[] stored = (await store.GetBlobAsync(record.Blobs["meta"], CancellationToken.None))!;
        RoomCompileCache.Meta meta = RoomCompileCache.Meta.Read(stored)!;
        byte[] downgraded = RevisionOneMeta(meta.Tags, meta.ClusterCount, meta.Warnings);
        await store.PutBlobAsync(CacheKey.HashBytes(downgraded), downgraded, "t", 0, CancellationToken.None);
        await store.PutAsync(
            record with { Blobs = new Dictionary<string, string>(record.Blobs) { ["meta"] = CacheKey.HashBytes(downgraded) } },
            CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        Assert.Null(await cache.TryGetAsync(rooms[0], CancellationToken.None));
        Assert.Equal(1, cache.Misses);
    }

    /// <summary>A meta blob as revision 1 wrote it: revision, clusters, the tags, then the naming warnings and nothing after.</summary>
    private static byte[] RevisionOneMeta(IReadOnlyList<string> tags, int clusters, IReadOnlyList<string> warnings)
    {
        List<byte> w = [];
        void Int(int value)
        {
            byte[] word = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(word, value);
            w.AddRange(word);
        }

        Int(1);
        Int(clusters);
        Int(tags.Count);
        foreach (string tag in tags)
        {
            w.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
        }

        Int(warnings.Count);
        foreach (string warning in warnings)
        {
            byte[] text = System.Text.Encoding.UTF8.GetBytes(warning);
            Int(text.Length);
            w.AddRange(text);
        }

        return [.. w];
    }

    // ---- posture -----------------------------------------------------------------

    /// <summary>A read-only posture serves hits and stores nothing; a write-only one stores and never serves.</summary>
    [Fact]
    public async Task ThePostureDecidesReadsAndWrites()
    {
        (InMemoryCacheStore store, IReadOnlyList<LibraryRoom> rooms, ContentFileSystem content) = await PrimedAsync(1);
        using (RoomCompileCache readOnly = new(store, CachePolicy.ReadOnly, RoomCacheHarness.Inputs, content))
        {
            Assert.NotNull(await readOnly.TryGetAsync(rooms[0], CancellationToken.None));
            readOnly.Add(rooms[0], (await RoomCacheHarness.BuildAsync(rooms, content, null))[0].Item!, 1, [], []);
            Assert.Equal(0, (await readOnly.CommitAsync(CancellationToken.None)).RowsStored);
        }

        InMemoryCacheStore fresh = await RoomCacheHarness.StoreAsync();
        CachePolicy writeOnly = new() { Mode = CacheMode.WriteOnly };
        using (RoomCompileCache writer = new(fresh, writeOnly, RoomCacheHarness.Inputs, content))
        {
            await RoomCacheHarness.BuildAsync(rooms, content, writer);
            Assert.Equal(1, (await writer.CommitAsync(CancellationToken.None)).RowsStored);
        }

        using RoomCompileCache again = new(fresh, writeOnly, RoomCacheHarness.Inputs, content);
        Assert.Null(await again.TryGetAsync(rooms[0], CancellationToken.None));
    }

    /// <summary>A store that never opened serves nothing and stores nothing, and the run goes on.</summary>
    [Fact]
    public async Task AnUnusableStoreIsAlwaysAMiss()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(1));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore closed = new();
        using RoomCompileCache cache = new(closed, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        List<RoomBuildOutcome> built = await RoomCacheHarness.BuildAsync(rooms, content, cache);
        Assert.NotNull(built[0].Item);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(new RoomCacheCommit(0, 0, null, null), await cache.CommitAsync(CancellationToken.None));
    }

    /// <summary>A room with a section over the blob cap is not stored; the others are.</summary>
    [Fact]
    public async Task ARoomOverTheBlobCapIsNotStored()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(1));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        foreach (long cap in new long[] { 16, 200 })
        {
            using RoomCompileCache cache = new(store, new CachePolicy { MaxBlobBytes = cap }, RoomCacheHarness.Inputs, content);
            await RoomCacheHarness.BuildAsync(rooms, content, cache);
            Assert.Equal(0, (await cache.CommitAsync(CancellationToken.None)).RowsStored);
        }

        Assert.Empty(await store.KeysAsync(CancellationToken.None));
    }

    // ---- failure, cancellation, lifetime ---------------------------------------

    /// <summary>
    /// A commit the store fails leaves nothing staged and nothing committed,
    /// and the next run compiles every room and stores it.
    /// </summary>
    [Fact]
    public async Task AFailedCommitLeavesNoRows()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(2));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        using (RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content))
        {
            await RoomCacheHarness.BuildAsync(rooms, content, cache);
            store.FailNextCommit = new IOException("disk full");
            await Assert.ThrowsAsync<IOException>(async () => await cache.CommitAsync(CancellationToken.None));
        }

        Assert.Equal(0, store.StagedCount);
        Assert.Empty(await store.KeysAsync(CancellationToken.None));

        using RoomCompileCache next = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        await RoomCacheHarness.BuildAsync(rooms, content, next);
        Assert.Equal(2, next.Misses);
        Assert.Equal(2, (await next.CommitAsync(CancellationToken.None)).RowsStored);
    }

    /// <summary>A commit cancelled as it reaches the store discards what it staged.</summary>
    [Fact]
    public async Task ACancelledCommitLeavesNoRows()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(1));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore inner = await RoomCacheHarness.StoreAsync();
        using CancellationTokenSource cancel = new();
        HookedCacheStore store = new(inner)
        {
            BeforeCommit = token =>
            {
                cancel.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
        };
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        await RoomCacheHarness.BuildAsync(rooms, content, cache);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cache.CommitAsync(cancel.Token));
        Assert.Equal(0, inner.StagedCount);
        Assert.Empty(await inner.KeysAsync(CancellationToken.None));
    }

    /// <summary>
    /// A build cancelled as its second room starts leaves the store
    /// untouched and the lease closed, and the next run is a clean one.
    /// </summary>
    [Fact]
    public async Task ACancelledBuildLeavesNothingAndTheNextRunIsClean()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(3));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        using (CancellationTokenSource cancel = new())
        using (RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RoomCacheHarness.BuildAsync(
                rooms,
                content,
                cache,
                settings => new SourceSharp.MapTools.Rooms.RoomLibraryCompileSettings(settings.Options, settings.Content)
                {
                    Parallelism = new SourceSharp.MapTools.Parallel.CompileParallelism { MaxDegree = 1 },
                    BeforeRoomProbe = (index, _) =>
                    {
                        if (index == 1)
                        {
                            cancel.Cancel();
                        }

                        return ValueTask.CompletedTask;
                    },
                },
                cancel.Token));
            Assert.Equal(1, store.RunsInFlight);
        }

        Assert.Equal(0, store.RunsInFlight);
        Assert.Equal(0, store.StagedCount);
        Assert.Empty(await store.KeysAsync(CancellationToken.None));

        List<RoomBuildOutcome> clean = await RoomCacheHarness.BuildAsync(rooms, content, null);
        using RoomCompileCache next = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        List<RoomBuildOutcome> built = await RoomCacheHarness.BuildAsync(rooms, content, next);
        Assert.All(built.Zip(clean), p => Assert.True(RoomCacheHarness.Same(p.First.Item!, p.Second.Item!)));
    }

    /// <summary>Disposing the run drops what it held and closes its lease; the pending rooms are gone.</summary>
    [Fact]
    public async Task DisposingTheRunDropsWhatItHeld()
    {
        (InMemoryCacheStore store, IReadOnlyList<LibraryRoom> rooms, ContentFileSystem content) = await PrimedAsync(1);
        RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        Assert.Equal(1, store.RunsInFlight);
        Assert.NotNull(await cache.TryGetAsync(rooms[0], CancellationToken.None));
        cache.Add(rooms[0], (await RoomCacheHarness.BuildAsync(rooms, content, null))[0].Item!, 1, [], []);
        Assert.Equal(1, cache.PendingCount);
        cache.Dispose();
        Assert.Equal(0, cache.PendingCount);
        Assert.Equal(0, store.RunsInFlight);
        cache.Dispose();
        Assert.Equal(0, store.RunsInFlight);
    }

    /// <summary>A collection that fails is reported with the commit, whose rows stay committed.</summary>
    [Fact]
    public async Task AFailedCollectionIsReportedNotThrown()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(1));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore inner = await RoomCacheHarness.StoreAsync();
        HookedCacheStore store = new(inner) { FailStats = new InvalidOperationException("stats unavailable") };
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        await RoomCacheHarness.BuildAsync(rooms, content, cache);
        RoomCacheCommit commit = await cache.CommitAsync(CancellationToken.None);
        Assert.Equal(1, commit.RowsStored);
        Assert.Equal("stats unavailable", commit.GcFailure);
        Assert.Single(await inner.KeysAsync(CancellationToken.None));
    }

    /// <summary>
    /// A hit is renewed under the run's stamp at the commit, so the store's
    /// collection counts a row's age from its last use; the run's generation
    /// is recorded with it.
    /// </summary>
    [Fact]
    public async Task AHitIsRenewedAtTheCommit()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(1));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        SetClock clock = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        using (RoomCompileCache first = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content, clock))
        {
            await RoomCacheHarness.BuildAsync(rooms, content, first);
            await first.CommitAsync(CancellationToken.None);
        }

        clock.Now = DateTimeOffset.FromUnixTimeMilliseconds(2_000_000);
        using RoomCompileCache second = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content, clock);
        Assert.Equal(2_000_000, second.RunStamp);
        Assert.NotNull(await second.TryGetAsync(rooms[0], CancellationToken.None));
        await second.CommitAsync(CancellationToken.None);
        CacheRecord record = (await store.LookupAsync((await store.KeysAsync(CancellationToken.None))[0], CancellationToken.None))!;
        Assert.Equal(2_000_000, record.CreatedAtMs);
        Assert.Contains("2000000", await store.GenerationsAsync(CancellationToken.None));
    }

    /// <summary>
    /// The content check reads each recorded file once per run however many
    /// rooms record it, and never through the recorder the run's compiles
    /// write to.
    /// </summary>
    [Fact]
    public async Task TheContentCheckReadsEachFileOncePerRun()
    {
        (InMemoryCacheStore store, IReadOnlyList<LibraryRoom> rooms, ContentFileSystem content) = await PrimedAsync(3);
        CountingContentFs counting = new(content);
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, counting);
        foreach (LibraryRoom room in rooms)
        {
            Assert.NotNull(await cache.TryGetAsync(room, CancellationToken.None));
        }

        Assert.Equal(1, counting.Reads($"materials/{RoomHarness.Plain}.vmt"));
        Assert.Empty(cache.Dependencies());
    }

    /// <summary>The arguments are checked.</summary>
    [Fact]
    public async Task NullArgumentsAreRefused()
    {
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        Assert.Throws<ArgumentNullException>(() => new RoomCompileCache(null!, CachePolicy.Default, RoomCacheHarness.Inputs, content));
        Assert.Throws<ArgumentNullException>(() => new RoomCompileCache(store, null!, RoomCacheHarness.Inputs, content));
        Assert.Throws<ArgumentNullException>(() => new RoomCompileCache(store, CachePolicy.Default, null!, content));
        Assert.Throws<ArgumentNullException>(() => new RoomCompileCache(store, CachePolicy.Default, RoomCacheHarness.Inputs, null!));
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        LibraryRoom room = RoomLibraryVmf.Split(RoomCacheHarness.Library(1))[0];
        RoomPackItem item = new("hub", new byte[] { 1 });
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.TryGetAsync(null!, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => cache.Add(null!, item, 1, [], []));
        Assert.Throws<ArgumentNullException>(() => cache.Add(room, null!, 1, [], []));
        Assert.Throws<ArgumentNullException>(() => cache.Add(room, item, 1, null!, []));
        Assert.Throws<ArgumentNullException>(() => cache.Add(room, item, 1, [], null!));
    }

    /// <summary>A store with the library's rooms compiled and committed once.</summary>
    private static async Task<(InMemoryCacheStore Store, IReadOnlyList<LibraryRoom> Rooms, ContentFileSystem Content)> PrimedAsync(int count)
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(count));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        await RoomCacheHarness.BuildAsync(rooms, content, cache);
        await cache.CommitAsync(CancellationToken.None);
        return (store, rooms, content);
    }
}
