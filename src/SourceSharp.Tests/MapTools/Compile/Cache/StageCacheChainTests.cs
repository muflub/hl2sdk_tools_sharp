//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.Tests.MapTools.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// The vvis and bounce-transfer stage caches through <see cref="MapCompiler"/>:
/// a compile that only moved a light replays both and writes the map an
/// uncached compile writes; a brush edit computes both again.
/// </summary>
public sealed class StageCacheChainTests
{
    // A sealed room with a pillar (two clusters), a start and one light.
    private static VmfDocument Room(string lightOrigin = "200 200 200", bool extraBrush = false)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        int id = 10;
        void Slab((float, float, float) mins, (float, float, float) maxs) =>
            world.Children.Add(UnitMap.Box(UnitMap.Plain, mins, maxs, id++));

        Slab((-16, -16, -16), (272, 272, 0));
        Slab((-16, -16, 256), (272, 272, 272));
        Slab((-16, -16, 0), (0, 272, 256));
        Slab((256, -16, 0), (272, 272, 256));
        Slab((0, -16, 0), (256, 0, 256));
        Slab((0, 256, 0), (256, 272, 256));
        Slab((96, 96, 0), (160, 160, 256));
        if (extraBrush)
        {
            Slab((16, 16, 0), (48, 48, 32));
        }

        document.Chunks.Add(world);
        Entity(document, "info_player_start", "32 32 64");
        Entity(document, "light", lightOrigin).AddKey("_light", "255 255 255 200");
        return document;
    }

    private static VmfChunk Entity(VmfDocument document, string className, string origin)
    {
        VmfChunk e = new(MapFileLoader.EntityChunk);
        e.AddKey("id", (document.Chunks.Count + 100).ToString(CultureInfo.InvariantCulture));
        e.AddKey("classname", className);
        e.AddKey("origin", origin);
        document.Chunks.Add(e);
        return e;
    }

    private static async Task<CompileResult> CompileAsync(
        VmfDocument document, ICacheStore? store, bool overlap = false, TimeProvider? fileClock = null)
    {
        InMemoryFileSystem files = new(fileClock ?? TimeProvider.System);
        files.AddText($"materials/{UnitMap.Plain}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        files.AddText("maps/room.vmf", Encoding.UTF8.GetString(document.ToBytes()));
        IContentFileSystem content = new ContentFileSystem([await DirectoryContentMount.MountAsync(files, VPath.Empty)]);
        return await MapCompiler.CompileAsync(
            new CompileRequest
            {
                Source = MapSource.FromVmf(files, VPath.Create("maps/room.vmf")),
                Content = content,
                Vrad = VradOptions.Default with { Bounces = 2 },
                Parallel = new CompileParallelism { MaxDegree = 2 },
                Output = CompileOutput.InMemory,
                Cache = store,
                Overlap = overlap,
            },
            null);
    }

    private static async Task<byte[]> BytesAsync(BspData bsp)
    {
        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
        return stream.ToArray();
    }

    private static async Task<InMemoryCacheStore> StoreAsync()
    {
        InMemoryCacheStore store = new();
        await store.OpenAsync("memory");
        return store;
    }

    [Fact]
    public async Task AMovedLightReusesVisAndTheTransfers()
    {
        InMemoryCacheStore store = await StoreAsync();
        CompileResult first = await CompileAsync(Room(), store);
        CompileResult moved = await CompileAsync(Room("120 200 180"), store);

        Assert.Equal(["vvis", "vrad.transfers"], first.Cache!.StageMisses);
        Assert.Equal(["vvis", "vrad.transfers"], moved.Cache!.StageHits);
    }

    [Fact]
    public async Task TheReplayedMapIsTheUncachedMap()
    {
        InMemoryCacheStore store = await StoreAsync();
        _ = await CompileAsync(Room(), store);
        CompileResult replayed = await CompileAsync(Room("120 200 180"), store);
        CompileResult fresh = await CompileAsync(Room("120 200 180"), null);

        Assert.Equal(await BytesAsync(fresh.Bsp!), await BytesAsync(replayed.Bsp!));
        Assert.Equal(fresh.Vis!.VisDataSize, replayed.Vis!.VisDataSize);
        Assert.Equal(fresh.Vis.Pvs(0).ToArray(), replayed.Vis.Pvs(0).ToArray());
    }

    [Fact]
    public async Task ConcurrentCompilesOnOneSharedStoreWriteTheUncachedMap()
    {
        // What a service does: one store for every compile, compiles
        // overlapping. Every one of them, cold or warm, writes the map a
        // compile with no store writes.
        InMemoryCacheStore store = await StoreAsync();
        byte[] fresh = await BytesAsync((await CompileAsync(Room(), null)).Bsp!);

        CompileResult[] cold = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => CompileAsync(Room(), store))));
        CompileResult[] warm = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => CompileAsync(Room(), store))));

        foreach (CompileResult result in cold.Concat(warm))
        {
            Assert.Equal(fresh, await BytesAsync(result.Bsp!));
        }

        Assert.Contains(warm, r => r.Cache!.StageHits.Count > 0);
        Assert.Equal(0, store.RunsInFlight);
    }

    [Fact]
    public async Task AStoreWithACeilingBelowOneCompileStaysUnderItAndChangesNoOutput()
    {
        // A ceiling too small for even one compile's rows: every commit
        // trims, the store never holds more than the ceiling, and the maps
        // are still the uncached ones.
        InMemoryCacheStore store = new(4096);
        await store.OpenAsync("memory");
        byte[] fresh = await BytesAsync((await CompileAsync(Room(), null)).Bsp!);

        for (int i = 0; i < 2; i++)
        {
            CompileResult result = await CompileAsync(Room(), store);
            Assert.Equal(fresh, await BytesAsync(result.Bsp!));
            Assert.True(store.Bytes <= store.MaxBytes);
        }

        Assert.True(store.RowsEvicted > 0);
    }

    // Content, never mtimes: no key and no re-hash may read a modification
    // time. The two facts below pin both directions of the rule, the files'
    // times coming from a clock the fact sets.

    [Fact]
    public async Task TheSameFilesWithNewModificationTimesStillHit()
    {
        InMemoryCacheStore store = await StoreAsync();
        DateTimeOffset then = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _ = await CompileAsync(Room(), store, fileClock: new CompileCacheLifecycleTests.FixedTime(then));

        CompileResult touched = await CompileAsync(
            Room(), store, fileClock: new CompileCacheLifecycleTests.FixedTime(then.AddYears(3)));

        Assert.Equal(["vvis", "vrad.transfers"], touched.Cache!.StageHits);
        Assert.Empty(touched.Cache.StageMisses);
    }

    [Fact]
    public async Task ChangedFilesWithTheOldModificationTimesStillMiss()
    {
        // What a restore that keeps modification times does: new bytes under
        // the old stamps. The edit must still be seen.
        InMemoryCacheStore store = await StoreAsync();
        TimeProvider stamp = new CompileCacheLifecycleTests.FixedTime(new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        _ = await CompileAsync(Room(), store, fileClock: stamp);

        CompileResult edited = await CompileAsync(Room(extraBrush: true), store, fileClock: stamp);
        CompileResult fresh = await CompileAsync(Room(extraBrush: true), null);

        Assert.Empty(edited.Cache!.StageHits);
        Assert.Equal(await BytesAsync(fresh.Bsp!), await BytesAsync(edited.Bsp!));
    }

    [Fact]
    public async Task ABrushEditComputesBothAgain()
    {
        InMemoryCacheStore store = await StoreAsync();
        _ = await CompileAsync(Room(), store);
        CompileResult edited = await CompileAsync(Room(extraBrush: true), store);
        CompileResult fresh = await CompileAsync(Room(extraBrush: true), null);

        Assert.Empty(edited.Cache!.StageHits);
        Assert.Equal(await BytesAsync(fresh.Bsp!), await BytesAsync(edited.Bsp!));
    }

    [Fact]
    public async Task AnOverlappedCompileStoresAndReusesBoth()
    {
        // The miss stores vvis only after the overlapped tail writes its
        // lumps; the hit stops the early flow and restores them instead.
        InMemoryCacheStore store = await StoreAsync();
        CompileResult first = await CompileAsync(Room(), store, overlap: true);
        CompileResult moved = await CompileAsync(Room("120 200 180"), store, overlap: true);
        CompileResult fresh = await CompileAsync(Room("120 200 180"), null);

        Assert.Equal(["vvis", "vrad.transfers"], first.Cache!.StageMisses);
        Assert.Equal(["vvis", "vrad.transfers"], moved.Cache!.StageHits);
        Assert.Equal(await BytesAsync(fresh.Bsp!), await BytesAsync(moved.Bsp!));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OverlapDoesNotChangeTheKeys(bool storeOverlapped, bool reuseOverlapped)
    {
        // The transfer key's tracer digest rides the early vrad load when the
        // chain overlaps, and the plain load otherwise: both must agree.
        InMemoryCacheStore store = await StoreAsync();
        _ = await CompileAsync(Room(), store, storeOverlapped);
        CompileResult moved = await CompileAsync(Room("120 200 180"), store, reuseOverlapped);
        CompileResult fresh = await CompileAsync(Room("120 200 180"), null);

        Assert.Equal(["vvis", "vrad.transfers"], moved.Cache!.StageHits);
        Assert.Equal(await BytesAsync(fresh.Bsp!), await BytesAsync(moved.Bsp!));
    }

    [Fact]
    public void TheVisKeyIgnoresEntitiesButNotFogRadius()
    {
        BspData bsp = new();
        bsp.SetLump(BspLump.Nodes, new byte[] { 1, 2, 3 });
        string plain = VvisStageCache.InputDigest([1, 2], bsp, VvisOptions.Default);

        bsp.SetLump(BspLump.Entities, Encoding.ASCII.GetBytes("{\n\"classname\" \"light\"\n\"origin\" \"1 2 3\"\n}\n\0"));
        Assert.Equal(plain, VvisStageCache.InputDigest([1, 2], bsp, VvisOptions.Default));

        bsp.SetLump(BspLump.Entities, Encoding.ASCII.GetBytes("{\n\"classname\" \"env_fog_controller\"\n\"farz\" \"2000\"\n}\n\0"));
        Assert.NotEqual(plain, VvisStageCache.InputDigest([1, 2], bsp, VvisOptions.Default));
    }

    [Fact]
    public void TheVisKeyFollowsThePortalsAndOptions()
    {
        BspData bsp = new();
        string plain = VvisStageCache.InputDigest([1, 2], bsp, VvisOptions.Default);

        Assert.NotEqual(plain, VvisStageCache.InputDigest([1, 3], bsp, VvisOptions.Default));
        Assert.NotEqual(plain, VvisStageCache.InputDigest([1, 2], bsp, VvisOptions.Default with { Fast = true }));
        Assert.Equal(plain, VvisStageCache.InputDigest([1, 2], bsp, VvisOptions.Default with { Verbose = true }));
    }

    [Fact]
    public void TraceRunsAreNotCached()
    {
        Assert.True(VvisStageCache.Applies(VvisOptions.Default));
        Assert.False(VvisStageCache.Applies(VvisOptions.Default with { Trace = (0, 1) }));
    }
}
