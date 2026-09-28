//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A library build: without a cache, the items the verb always wrote; with
/// one, the unchanged rooms reused and the rest compiled, delivered in
/// library order either way; a failed room never cached.
/// </summary>
public sealed class RoomLibraryBuildTests
{
    /// <summary>
    /// Without a cache, every room compiles and its item is exactly
    /// <see cref="RoomPackItem.CreateAsync(RoomObject, RoomNavPackOptions, CancellationToken)"/>
    /// of the room the library compiler delivers.
    /// </summary>
    [Fact]
    public async Task WithoutACacheEachItemIsTheCompiledRoomsItem()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(3));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        List<RoomBuildOutcome> built = await RoomCacheHarness.BuildAsync(rooms, content, null);

        List<RoomPackItem> expected = [];
        await RoomLibraryCompiler.CompileAsync(
            rooms,
            new RoomLibraryCompileSettings(VbspOptions.Default, content),
            async (outcome, token) => expected.Add(await RoomPackItem.CreateAsync(outcome.Compiled!, new RoomNavPackOptions(), token)));

        Assert.Equal([0, 1, 2], built.Select(o => o.Index));
        for (int i = 0; i < rooms.Count; i++)
        {
            Assert.False(built[i].Reused);
            Assert.Null(built[i].Error);
            Assert.Same(rooms[i], built[i].Room);
            Assert.True(built[i].ClusterCount > 0);
            Assert.True(RoomCacheHarness.Same(expected[i], built[i].Item!));
        }
    }

    /// <summary>
    /// After an edit to rooms 0, 2 and 4 of five, rooms 1 and 3 are reused
    /// and delivered in their places between the compiled ones; after an
    /// edit to room 1 alone, the three reused rooms after the last compiled
    /// one come out after it, in order; after an edit to room 3, the three
    /// before it come out first. Each item is the item a clean build of the
    /// edited library makes. The edits are all different, so no edited room
    /// is one an earlier case already stored.
    /// </summary>
    [Fact]
    public async Task ReusedRoomsAreDeliveredInLibraryOrder()
    {
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        await RunAsync(store, RoomLibraryVmf.Split(RoomCacheHarness.Library()), content);

        foreach (int[] edited in new[] { new[] { 0, 2, 4 }, new[] { 1 }, new[] { 3 } })
        {
            IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(5, edited));
            List<RoomBuildOutcome> built = await RunAsync(store, rooms, content);
            List<RoomBuildOutcome> clean = await RoomCacheHarness.BuildAsync(rooms, content, null);
            Assert.Equal([0, 1, 2, 3, 4], built.Select(o => o.Index));
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(!edited.Contains(i), built[i].Reused);
                Assert.True(RoomCacheHarness.Same(clean[i].Item!, built[i].Item!), $"room {i} after editing {string.Join(",", edited)}");
            }

            // Back to the unedited library for the next case: every room hits.
            List<RoomBuildOutcome> restored = await RunAsync(store, RoomLibraryVmf.Split(RoomCacheHarness.Library()), content);
            Assert.All(restored, o => Assert.True(o.Reused));
        }
    }

    /// <summary>When every room hits, no room compiles: the compiler's per-room probe never runs.</summary>
    [Fact]
    public async Task WhenEveryRoomHitsNothingCompiles()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(2));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        await RunAsync(store, rooms, content);

        int started = 0;
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        List<RoomBuildOutcome> built = await RoomCacheHarness.BuildAsync(rooms, content, cache, settings => new RoomLibraryCompileSettings(settings.Options, settings.Content)
        {
            BeforeRoomProbe = (_, _) =>
            {
                Interlocked.Increment(ref started);
                return ValueTask.CompletedTask;
            },
        });
        Assert.Equal(0, started);
        Assert.All(built, o => Assert.True(o.Reused));
        Assert.Equal(0, cache.PendingCount);
    }

    /// <summary>
    /// A room that fails is delivered with its error in its place and is not
    /// cached; the rooms around it are, and the next run compiles only it.
    /// </summary>
    [Fact]
    public async Task AFailedRoomIsReportedAndNotCached()
    {
        VmfDocument library = RoomCacheHarness.Library(3);
        BreakPlugs(library, 1);
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        using (RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content))
        {
            List<RoomBuildOutcome> built = await RoomCacheHarness.BuildAsync(rooms, content, cache);
            Assert.NotNull(built[0].Item);
            Assert.Null(built[1].Item);
            Assert.IsType<RoomLintException>(built[1].Error);
            Assert.Equal(0, built[1].ClusterCount);
            Assert.Empty(built[1].NameWarnings);
            Assert.NotNull(built[2].Item);
            Assert.Equal(2, cache.PendingCount);
            await cache.CommitAsync(CancellationToken.None);
        }

        using RoomCompileCache next = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        List<RoomBuildOutcome> again = await RoomCacheHarness.BuildAsync(rooms, content, next);
        Assert.Equal([true, false, true], again.Select(o => o.Reused));
        Assert.NotNull(again[1].Error);
    }

    /// <summary>
    /// The two logging switches the key leaves out change no byte of a
    /// room's sections: the proof that leaving them out is sound.
    /// </summary>
    [Fact]
    public async Task TheLoggingSwitchesChangeNoSection()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(2));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        List<RoomBuildOutcome> quiet = await RoomCacheHarness.BuildAsync(rooms, content, null);
        List<RoomBuildOutcome> loud = await RoomCacheHarness.BuildAsync(
            rooms,
            content,
            null,
            settings => new RoomLibraryCompileSettings(settings.Options with { Verbose = true, VerboseEntities = true }, settings.Content));
        Assert.All(quiet.Zip(loud), p => Assert.True(RoomCacheHarness.Same(p.First.Item!, p.Second.Item!)));
    }

    /// <summary>A cancelled token stops the build before any lookup.</summary>
    [Fact]
    public async Task ACancelledBuildStartsNothing()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(1));
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RoomCacheHarness.BuildAsync(rooms, content, cache, cancellationToken: new CancellationToken(true)));
        Assert.Equal(0, cache.Misses + cache.Hits);
    }

    /// <summary>The arguments are checked.</summary>
    [Fact]
    public async Task NullArgumentsAreRefused()
    {
        IReadOnlyList<LibraryRoom> rooms = [];
        RoomLibraryCompileSettings settings = new(VbspOptions.Default, await RoomCacheHarness.ContentAsync());
        RoomNavPackOptions pack = new();
        static ValueTask Finished(RoomBuildOutcome outcome, CancellationToken token) => ValueTask.CompletedTask;
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomLibraryBuild.BuildAsync(null!, settings, pack, null, Finished));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomLibraryBuild.BuildAsync(rooms, null!, pack, null, Finished));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomLibraryBuild.BuildAsync(rooms, settings, null!, null, Finished));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomLibraryBuild.BuildAsync(rooms, settings, pack, null, null!));
        await RoomLibraryBuild.BuildAsync(rooms, settings, pack, null, Finished);
    }

    /// <summary>
    /// The settings an incremental build compiles with are the host's over
    /// the cache's recording content: every other member carried over.
    /// </summary>
    [Fact]
    public async Task TheCompileSettingsCarryOverToTheRecordingContent()
    {
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        ContentFileSystem other = await RoomCacheHarness.ContentAsync();
        Func<int, CancellationToken, ValueTask> before = (_, _) => ValueTask.CompletedTask;
        Action<int> compiled = _ => { };
        Action<SourceSharp.MapTools.Bsp.SharedMaterialFacts> materials = _ => { };
        Action<CompilePool> pool = _ => { };
        RoomLibraryCompileSettings settings = new(VbspOptions.Default with { NoWeld = true }, content)
        {
            Nav = NavSettings.Default,
            NameKeys = new HashSet<string> { "k" },
            Parallelism = new CompileParallelism { MaxDegree = 3 },
            BeforeRoomProbe = before,
            RoomCompiledProbe = compiled,
            MaterialsProbe = materials,
            PoolProbe = pool,
        };
        RoomLibraryCompileSettings moved = settings.WithContent(other);
        Assert.Same(other, moved.Content);
        Assert.Same(settings.Options, moved.Options);
        Assert.Same(settings.Nav, moved.Nav);
        Assert.Same(settings.NameKeys, moved.NameKeys);
        Assert.Equal(settings.Parallelism, moved.Parallelism);
        Assert.Same(before, moved.BeforeRoomProbe);
        Assert.Same(compiled, moved.RoomCompiledProbe);
        Assert.Same(materials, moved.MaterialsProbe);
        Assert.Same(pool, moved.PoolProbe);
        Assert.Null(moved.CollisionCooker);
    }

    /// <summary>
    /// A room whose navigation warned (a prop whose model the content lacks)
    /// carries the warning in its outcome when it compiles, and the same
    /// list, in the same order, when the next run reuses it; a room without
    /// such a prop carries none either way, and a build without navigation
    /// carries none at all.
    /// </summary>
    [Fact]
    public async Task AReusedRoomCarriesTheNavigationWarningsItsCompileGave()
    {
        VmfDocument library = RoomCacheHarness.Library(2);
        VmfChunk prop = new(SourceSharp.MapTools.Bsp.MapFileLoader.EntityChunk);
        prop.AddKey("id", "720001");
        prop.AddKey("classname", "prop_physics");
        prop.AddKey("model", "models/unit/missing_crate.mdl");
        prop.AddKey("origin", "64 64 16");
        library.Chunks.Add(prop);
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        ContentFileSystem content = await RoomCacheHarness.ContentAsync();
        InMemoryCacheStore store = await RoomCacheHarness.StoreAsync();
        RoomCacheInputs inputs = RoomCacheHarness.Inputs with { Nav = NavSettings.Default };

        async Task<List<RoomBuildOutcome>> NavRunAsync()
        {
            using RoomCompileCache cache = new(store, CachePolicy.Default, inputs, content);
            List<RoomBuildOutcome> built = await RoomCacheHarness.BuildAsync(
                rooms,
                content,
                cache,
                s => new RoomLibraryCompileSettings(s.Options, s.Content) { Nav = NavSettings.Default, Parallelism = s.Parallelism });
            await cache.CommitAsync(CancellationToken.None);
            return built;
        }

        List<RoomBuildOutcome> first = await NavRunAsync();
        List<RoomBuildOutcome> second = await NavRunAsync();
        Assert.All(first, o => Assert.False(o.Reused));
        Assert.All(second, o => Assert.True(o.Reused));
        string warning = Assert.Single(first[0].NavWarnings);
        Assert.Contains("\"models/unit/missing_crate.mdl\"", warning, StringComparison.Ordinal);
        Assert.Empty(first[1].NavWarnings);
        for (int i = 0; i < rooms.Count; i++)
        {
            Assert.Equal(first[i].NavWarnings, second[i].NavWarnings);
        }

        List<RoomBuildOutcome> plain = await RoomCacheHarness.BuildAsync(rooms, content, null);
        Assert.All(plain, o => Assert.Empty(o.NavWarnings));
    }

    private static async Task<List<RoomBuildOutcome>> RunAsync(ICacheStore store, IReadOnlyList<LibraryRoom> rooms, IContentFileSystem content)
    {
        using RoomCompileCache cache = new(store, CachePolicy.Default, RoomCacheHarness.Inputs, content);
        List<RoomBuildOutcome> built = await RoomCacheHarness.BuildAsync(rooms, content, cache);
        await cache.CommitAsync(CancellationToken.None);
        return built;
    }

    /// <summary>Makes a room's door plugs plain brushes, which the room lint refuses.</summary>
    private static void BreakPlugs(VmfDocument library, int room)
    {
        float low = room * (RoomHarness.Cell + RoomHarness.LibraryGap);
        foreach (VmfChunk solid in library.GetChunk("world")!.GetChunks("solid"))
        {
            if (VmfPlacement.Bounds(solid).Mins.X < low - 1f || VmfPlacement.Bounds(solid).Maxs.X > low + RoomHarness.Cell + 1f)
            {
                continue;
            }

            foreach (VmfKey key in solid.GetChunks("side").SelectMany(s => s.Keys).Where(k => k.Name == "material" && k.Value == RoomHarness.Trigger))
            {
                key.Value = RoomHarness.Plain;
            }
        }
    }
}
