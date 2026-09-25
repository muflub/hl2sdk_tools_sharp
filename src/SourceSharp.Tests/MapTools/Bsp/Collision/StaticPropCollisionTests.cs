using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>The static-prop hull cache and name rules (unit tier).</summary>
public class StaticPropHullCacheTests
{
    private static Vec3[] Box(Vec3 mins, Vec3 maxs)
    {
        Vec3[] corners = new Vec3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vec3(
                (i & 1) != 0 ? maxs.X : mins.X,
                (i & 2) != 0 ? maxs.Y : mins.Y,
                (i & 4) != 0 ? maxs.Z : mins.Z);
        }

        return corners;
    }

    [Fact]
    public void TheCacheKeyIsLowerCaseWithForwardSlashes()
    {
        // GetCollisionModel.
        Assert.Equal("models/props/crate.mdl", StaticPropCollision.NormalizeModelName("Models\\Props\\CRATE.mdl"));
    }

    [Fact]
    public async Task EachModelIsCookedOnce()
    {
        FakeCollisionCooker cooker = new();
        StaticPropHullCache cache = new(cooker);
        int loads = 0;

        Task<IReadOnlyList<Vec3[]>?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<IReadOnlyList<Vec3[]>?>([Box(new(-8, -8, 0), new(8, 8, 16))]);
        }

        StaticPropHull a = await cache.GetOrCookAsync("models/Crate.mdl", Load);
        StaticPropHull b = await cache.GetOrCookAsync("MODELS\\crate.mdl", Load);

        Assert.Same(a, b);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task AModelThatDoesNotLoadIsRememberedAsNoHull()
    {
        // "This way we don't try to load it multiple times".
        StaticPropHullCache cache = new(new FakeCollisionCooker());

        StaticPropHull hull = await cache.GetOrCookAsync("models/missing.mdl", _ => Task.FromResult<IReadOnlyList<Vec3[]>?>(null));

        Assert.Null(hull.Blob);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task AModelWhoseMeshesGiveNoConvexHasNoHull()
    {
        // "Bad geometry": every mesh too small for a hull.
        StaticPropHull hull = await StaticPropCollision.CookHullAsync(
            new FakeCollisionCooker(), "m.mdl", [[Vec3.Zero, new Vec3(1, 0, 0)]]);

        Assert.Null(hull.Blob);
    }

    [Fact]
    public async Task ANullHullTouchesNoLeaf()
    {
        IReadOnlyList<ushort> leaves = await StaticPropCollision.ComputeStaticPropLeavesAsync(
            new FakeCollisionCooker(), new StaticPropHull("m", null), Vec3.Zero, Vec3.Zero, [], [], []);

        Assert.Empty(leaves);
    }
}

