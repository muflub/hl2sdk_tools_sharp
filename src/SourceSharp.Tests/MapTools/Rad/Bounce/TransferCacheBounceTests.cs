//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// The bounce's cross-compile transfer cache: a compile that only moved a
/// light replays the previous compile's transfers and bounces the same light
/// as building its own; a geometry or scene change builds again.
/// </summary>
public sealed class TransferCacheBounceTests
{
    private const string Scene = "kd/test-scene";

    private static LightTestMap Map(string lightOrigin = "128 128 128")
    {
        LightTestMap map = BounceBox.Map(light: false);
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", lightOrigin), ("_light", "255 255 255 200")));
        return map;
    }

    private static async Task<RadWorld> BouncedAsync(
        LightTestMap map, ICacheStore? store, string scene = Scene, float chop = 4.0f)
    {
        RadWorld world = await BounceBox.LitAsync(map, LightBox.Settings() with { MinChop = chop, MaxChop = chop });
        StoreTransferCache? cache = store is null ? null : new(store, CachePolicy.Default, [], new CacheRunCounters());
        world.TransferCache = cache;
        world.TransferTracerDigest = scene;
        await world.BounceAsync(map.Tracer(), BounceBox.One, CancellationToken.None);
        if (cache is not null)
        {
            await cache.FlushAsync();
            await store!.CommitAsync(CancellationToken.None);
        }

        return world;
    }

    private static async Task<InMemoryCacheStore> StoreAsync()
    {
        InMemoryCacheStore store = new();
        await store.OpenAsync("memory");
        return store;
    }

    private static byte[] BouncedLight(RadWorld world)
    {
        List<byte> bytes = [];
        for (int p = 0; p < world.Patches.Count; p++)
        {
            BumpLights total = world.Patches.At(p).TotalLight;
            bytes.AddRange(MemoryMarshal.AsBytes(new ReadOnlySpan<BumpLights>(in total)).ToArray());
        }

        return [.. bytes];
    }

    [Fact]
    public async Task AMovedLightReplaysTheTransfers()
    {
        InMemoryCacheStore store = await StoreAsync();
        RadWorld first = await BouncedAsync(Map(), store);
        RadWorld moved = await BouncedAsync(Map("96 128 160"), store);

        Assert.False(first.TransfersWereCached);
        Assert.True(moved.TransfersWereCached);
    }

    [Fact]
    public async Task AReplayBouncesTheSameLightAsAFreshBuild()
    {
        InMemoryCacheStore store = await StoreAsync();
        _ = await BouncedAsync(Map(), store);
        RadWorld replayed = await BouncedAsync(Map("96 128 160"), store);
        RadWorld fresh = await BouncedAsync(Map("96 128 160"), null);

        Assert.True(replayed.TransfersWereCached);
        Assert.Equal(BouncedLight(fresh), BouncedLight(replayed));
        Assert.Equal(fresh.BounceEnergies, replayed.BounceEnergies);
    }

    [Fact]
    public async Task ADifferentPatchTreeBuildsAgain()
    {
        InMemoryCacheStore store = await StoreAsync();
        _ = await BouncedAsync(Map(), store);
        RadWorld other = await BouncedAsync(Map(), store, chop: 16.0f);

        Assert.False(other.TransfersWereCached);
    }

    [Fact]
    public async Task ADifferentSceneBuildsAgain()
    {
        InMemoryCacheStore store = await StoreAsync();
        _ = await BouncedAsync(Map(), store);
        RadWorld other = await BouncedAsync(Map(), store, scene: "kd/another-scene");

        Assert.False(other.TransfersWereCached);
    }

    [Fact]
    public async Task TheKeyIgnoresLightsAndFollowsTheScene()
    {
        RadWorld a = await BounceBox.LitAsync(Map());
        RadWorld b = await BounceBox.LitAsync(Map("96 128 160"));

        Assert.Equal(a.TransferKey(Scene), b.TransferKey(Scene));
        Assert.NotEqual(a.TransferKey(Scene), a.TransferKey("kd/another-scene"));
    }

    [Fact]
    public async Task WithoutASceneDigestNothingIsCached()
    {
        InMemoryCacheStore store = await StoreAsync();
        RadWorld world = await BounceBox.LitAsync(Map());
        world.TransferCache = new StoreTransferCache(store, CachePolicy.Default, [], new CacheRunCounters());
        world.TransferTracerDigest = null;
        await world.BounceAsync(Map().Tracer(), BounceBox.One, CancellationToken.None);
        await store.CommitAsync(CancellationToken.None);

        Assert.Empty(await store.KeysAsync(CancellationToken.None));
    }
}
