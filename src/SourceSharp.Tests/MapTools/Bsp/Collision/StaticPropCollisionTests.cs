using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.Tests.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>The static-prop hull cache and name rules (unit tier).</summary>
public class StaticPropHullCacheTests
{
    private static Vec3[] Box(Vec3 mins, Vec3 maxs) => VPhysicsBindingTests.BoxCorners(mins, maxs);

    [Fact]
    public void TheCacheKeyIsLowerCaseWithForwardSlashes()
    {
        // GetCollisionModel, staticprop.cpp:248-258.
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
        // "This way we don't try to load it multiple times", staticprop.cpp:272.
        StaticPropHullCache cache = new(new FakeCollisionCooker());

        StaticPropHull hull = await cache.GetOrCookAsync("models/missing.mdl", _ => Task.FromResult<IReadOnlyList<Vec3[]>?>(null));

        Assert.Null(hull.Blob);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task AModelWhoseMeshesGiveNoConvexHasNoHull()
    {
        // "Bad geometry", staticprop.cpp:288: every mesh too small for a hull.
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

/// <summary>The leaf walk against the real library (native tier).</summary>
[Collection(VPhysicsCollection.Name)]
[Trait("Tier", PinnedVPhysics.Tier)]
[VPhysicsJournal]
public class StaticPropLeafTests
{
    private readonly VPhysicsCookerFixture _fixture;

    public StaticPropLeafTests(VPhysicsCookerFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A room: six nodes, one per face of the cube [-64, 64]^3, each with the
    /// solid outside in front (children[0]) and the next node behind; the
    /// last node's back child is the empty inside leaf 6. Leaves 0..5 are solid.
    /// </summary>
    private static (DNode[] Nodes, DPlane[] Planes, DLeaf[] Leafs) Room()
    {
        (Vec3 n, float d)[] faces =
        [
            (new(1, 0, 0), 64), (new(-1, 0, 0), 64), (new(0, 1, 0), 64),
            (new(0, -1, 0), 64), (new(0, 0, 1), 64), (new(0, 0, -1), 64),
        ];

        DNode[] nodes = new DNode[6];
        DPlane[] planes = new DPlane[6];
        DLeaf[] leafs = new DLeaf[7];
        for (int i = 0; i < 6; i++)
        {
            planes[i] = new DPlane { Normal = faces[i].n, Dist = faces[i].d };
            nodes[i].PlaneNum = i;
            nodes[i].Children[0] = -1 - i;
            nodes[i].Children[1] = i < 5 ? i + 1 : -1 - 6;
            leafs[i].Contents = CollisionContents.Solid;
        }

        return (nodes, planes, leafs);
    }

    private async Task<IReadOnlyList<ushort>> LeavesAsync(Vec3 origin)
    {
        (DNode[] nodes, DPlane[] planes, DLeaf[] leafs) = Room();
        StaticPropHull hull = await StaticPropCollision.CookHullAsync(
            _fixture.Cooker, "crate.mdl", [VPhysicsBindingTests.BoxCorners(new(-8, -8, -8), new(8, 8, 8))]);
        return await StaticPropCollision.ComputeStaticPropLeavesAsync(_fixture.Cooker, hull, origin, Vec3.Zero, nodes, planes, leafs);
    }

    [VPhysicsNativeFact]
    public async Task APropInsideTheRoomTouchesTheRoomsLeaf()
    {
        Assert.Equal([(ushort)6], await LeavesAsync(Vec3.Zero));
    }

    [VPhysicsNativeFact]
    public async Task APropOutsideTheRoomTouchesNothing()
    {
        // Solid leaves are never added (staticprop.cpp:424).
        Assert.Empty(await LeavesAsync(new Vec3(500, 0, 0)));
    }

    [VPhysicsNativeFact]
    public async Task APropThroughAWallStillTouchesTheRoom()
    {
        // The box straddles x = 64: the walk splits and keeps the inside half.
        Assert.Equal([(ushort)6], await LeavesAsync(new Vec3(64, 0, 0)));
    }

    [VPhysicsNativeFact]
    public async Task ACookedHullIsTheModelsHull()
    {
        StaticPropHull hull = await StaticPropCollision.CookHullAsync(
            _fixture.Cooker, "crate.mdl", [VPhysicsBindingTests.BoxCorners(new(-8, -8, 0), new(8, 8, 32))]);

        float volume = await _fixture.Cooker.RunAsync(s =>
        {
            var c = s.UnserializeCollide(hull.Blob, 0);
            float v = s.CollideVolume(c);
            s.DestroyCollide(c);
            return v;
        });

        Assert.Equal(16f * 16f * 32f, volume, 1f);
    }
}
