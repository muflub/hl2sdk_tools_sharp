using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.Tests.MapTools.Bsp.Collision;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// The per-model collision cache across compiles: what a repeat compile
/// reuses, what an edit elsewhere in the map still reuses, and that every
/// replay is the bytes a fresh cook gives.
/// </summary>
public sealed class CollisionModelCacheTests
{
    // A world floor and two brush entities. extraWorldBrush adds a world
    // brush (and its own texinfo) ahead of the entities, which renumbers
    // every entity brush, plane, side and texinfo — the edit that used to
    // miss every model.
    private static PhysCollisionInput Map(bool extraWorldBrush = false, float doorHeight = 32f, string doorMaterial = "wood")
    {
        CollisionFixture f = new();
        f.Box(0, new Vec3(-256, -256, -16), new Vec3(256, 256, 0), CollisionContents.Solid, f.TexInfoFor("default"));
        if (extraWorldBrush)
        {
            f.Box(0, new Vec3(-32, -32, 64), new Vec3(0, 0, 96), CollisionContents.Solid, f.TexInfoFor("metal"));
        }

        int door = f.TexInfoFor(doorMaterial);
        f.Box(1, new Vec3(0, 0, 0), new Vec3(8, 48, doorHeight), CollisionContents.Solid, door);
        f.Face(1, door, 48f * doorHeight);

        int crate = f.TexInfoFor("metal");
        f.Box(2, new Vec3(64, 64, 0), new Vec3(96, 96, 32), CollisionContents.Solid, crate);
        f.Box(2, new Vec3(64, 64, 32), new Vec3(80, 80, 48), CollisionContents.Solid, crate);
        f.Face(2, crate, 1024f);
        return f.Build(3);
    }

    private static async Task<(PhysCollisionResult Result, CacheRunCounters Counters)> EmitAsync(
        PhysCollisionInput input, ICacheStore? store)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        CacheRunCounters counters = new();
        CollisionModelCache? cache = store is null
            ? null
            : new CollisionModelCache(store, CachePolicy.Default, cooker.CookerIdentity, ["preset=(default)"], counters);
        PhysCollisionResult result = await PhysCollisionEmitter.EmitAsync(input, cooker, cache);
        if (cache is not null)
        {
            await cache.CommitAsync();
        }

        return (result, counters);
    }

    private static async Task<InMemoryCacheStore> StoreAsync()
    {
        InMemoryCacheStore store = new();
        await store.OpenAsync("memory");
        return store;
    }

    [Fact]
    public async Task ARepeatCompileReusesEveryModelByteForByte()
    {
        InMemoryCacheStore store = await StoreAsync();
        (PhysCollisionResult cold, CacheRunCounters first) = await EmitAsync(Map(), store);
        (PhysCollisionResult warm, CacheRunCounters second) = await EmitAsync(Map(), store);

        Assert.Equal(3, first.Misses);
        Assert.Equal(3, second.Hits);
        Assert.Equal(0, second.Misses);
        Assert.Equal(cold.PhysCollide, warm.PhysCollide);
    }

    [Fact]
    public async Task AWorldBrushAddedReusesTheEntityModels()
    {
        InMemoryCacheStore store = await StoreAsync();
        _ = await EmitAsync(Map(), store);
        (_, CacheRunCounters edited) = await EmitAsync(Map(extraWorldBrush: true), store);

        Assert.Equal(2, edited.Hits);
        Assert.Equal([0], edited.MissesByModel.Keys);
    }

    [Fact]
    public async Task AReplayAfterRenumberingIsTheFreshCook()
    {
        InMemoryCacheStore store = await StoreAsync();
        _ = await EmitAsync(Map(), store);
        (PhysCollisionResult replayed, CacheRunCounters counters) = await EmitAsync(Map(extraWorldBrush: true), store);
        (PhysCollisionResult fresh, _) = await EmitAsync(Map(extraWorldBrush: true), null);

        Assert.Equal(2, counters.Hits);
        Assert.Equal(fresh.PhysCollide, replayed.PhysCollide);
    }

    [Fact]
    public async Task AnEditedEntityIsCookedAgainAndTheOthersAreNot()
    {
        InMemoryCacheStore store = await StoreAsync();
        _ = await EmitAsync(Map(), store);
        (_, CacheRunCounters edited) = await EmitAsync(Map(doorHeight: 64f), store);

        Assert.Equal([1], edited.MissesByModel.Keys);
        Assert.Equal(2, edited.Hits);
    }

    [Fact]
    public async Task AChangedMaterialMissesTheModelThatUsesIt()
    {
        InMemoryCacheStore store = await StoreAsync();
        _ = await EmitAsync(Map(), store);
        (_, CacheRunCounters edited) = await EmitAsync(Map(doorMaterial: "shell"), store);

        Assert.Contains(1, edited.MissesByModel.Keys);
        Assert.DoesNotContain(2, edited.MissesByModel.Keys);
    }

    [Fact]
    public async Task AHitReportsTheCookTimeItSaved()
    {
        InMemoryCacheStore store = await StoreAsync();
        PhysCollisionInput input = Map();
        CacheRunCounters counters = new();
        CollisionModelCache cache = new(store, CachePolicy.Default, "cooker", [], counters);
        CachedCollisionModel cooked;
        await using (ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct))
        {
            PhysCollisionResult result = await PhysCollisionEmitter.EmitAsync(input, cooker);
            PhysCollideModel door = result.Models.Single(m => m.ModelIndex == 1);
            cooked = new CachedCollisionModel(1, door.Solids, door.KeyData, null, null, null) { CostMs = 42 };
        }

        await cache.CookedAsync(input, 1, cooked);
        await cache.CommitAsync();
        _ = await cache.TryGetAsync(input, 1);

        Assert.Equal(42, counters.EstimatedSavedMs);
    }
}
