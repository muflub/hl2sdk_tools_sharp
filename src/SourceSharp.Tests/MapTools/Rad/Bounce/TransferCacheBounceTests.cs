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

using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

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

    // One fact per field of the transfer key that the patch tree does not
    // already carry: each changes that one field on an otherwise identical
    // world, so leaving the field out of the digest makes its fact fail.
    // (The switches' StockNormalise is derived from the compliance options,
    // so no world can change it alone: the compliance fact covers both.)

    [Fact]
    public async Task TheKeyFollowsTheComplianceOptions()
    {
        DirectLightingSettings settings = LightBox.Settings();
        RadWorld correct = await BounceBox.LitAsync(Map(), settings);
        RadWorld flipped = await BounceBox.LitAsync(
            Map(), settings with { Compliance = settings.Compliance.Flipping(SourceSharp.MapTools.Options.StockQuirk.OverlayFaceLimitOffByOne) });

        Assert.NotEqual(correct.TransferKey(Scene), flipped.TransferKey(Scene));
    }

    [Fact]
    public async Task TheKeyFollowsAClustersVisRow()
    {
        RadWorld sees = await BounceBox.LitAsync(Map());
        LightTestMap blind = BounceBox.Map(seesItself: false, light: false);
        blind.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200")));
        RadWorld notSees = await BounceBox.LitAsync(blind);

        Assert.NotEqual(sees.TransferKey(Scene), notSees.TransferKey(Scene));
    }

    [Fact]
    public async Task TheKeyFollowsAFacesSkyFlag()
    {
        RadWorld world = await BounceBox.LitAsync(Map());
        string before = world.TransferKey(Scene);
        int texInfo = world.Geometry.Faces[0].TexInfo;
        world.Geometry.TexInfos[texInfo].Flags ^= (int)SurfaceFlags.Sky;

        Assert.NotEqual(before, world.TransferKey(Scene));
    }

    [Fact]
    public async Task TheKeyFollowsAFacesDisplacementFlag()
    {
        RadWorld world = await BounceBox.LitAsync(Map());
        string before = world.TransferKey(Scene);
        world.Geometry.Faces[0].DispInfo = (short)(world.Geometry.Faces[0].DispInfo == -1 ? 0 : -1);

        Assert.NotEqual(before, world.TransferKey(Scene));
    }

    [Theory]
    [InlineData("cluster")]
    [InlineData("first")]
    [InlineData("count")]
    public async Task TheKeyFollowsALeafsFaceList(string field)
    {
        RadWorld world = await BounceBox.LitAsync(Map());
        string before = world.TransferKey(Scene);
        LeafInfo leaf = world.Geometry.Leaves[0];
        world.Geometry.Leaves[0] = field switch
        {
            "cluster" => leaf with { Cluster = leaf.Cluster + 1 },
            "first" => leaf with { FirstLeafFace = leaf.FirstLeafFace + 1 },
            _ => leaf with { NumLeafFaces = leaf.NumLeafFaces + 1 },
        };

        Assert.NotEqual(before, world.TransferKey(Scene));
    }

    [Fact]
    public async Task TheKeyFollowsTheLeafFaceIndices()
    {
        RadWorld world = await BounceBox.LitAsync(Map());
        string before = world.TransferKey(Scene);
        world.Geometry.LeafFaces[0] ^= 1;

        Assert.NotEqual(before, world.TransferKey(Scene));
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
