using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.Tests.MapTools.Bsp.Collision;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="CollisionGameData"/>: reading and moving the brush numbers a
/// cooked brush model stores in its convexes.
/// </summary>
public sealed class CollisionGameDataTests
{
    // Model 1 is two brushes, numbered 1 and 2 after the world's one.
    private static async Task<byte[]> CookedEntityAsync()
    {
        CollisionFixture f = new();
        f.Box(0, new Vec3(-64, -64, -16), new Vec3(64, 64, 0), CollisionContents.Solid, f.TexInfoFor("default"));
        int metal = f.TexInfoFor("metal");
        f.Box(1, new Vec3(0, 0, 0), new Vec3(16, 16, 16), CollisionContents.Solid, metal);
        f.Box(1, new Vec3(0, 0, 32), new Vec3(16, 16, 48), CollisionContents.Solid, metal);

        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        PhysCollisionResult result = await PhysCollisionEmitter.EmitAsync(f.Build(2), cooker);
        return result.Models.Single(m => m.ModelIndex == 1).Solids.Single();
    }

    [Fact]
    public async Task TheCookedConvexesCarryTheirBrushNumbers()
    {
        Assert.True(CollisionGameData.TryRead(await CookedEntityAsync(), out List<uint>? gameData));
        Assert.Equal([1u, 2u], gameData!.Order());
    }

    [Fact]
    public async Task RebaseMovesEveryBrushNumber()
    {
        byte[] moved = CollisionGameData.Rebase(await CookedEntityAsync(), 5)!;

        Assert.True(CollisionGameData.TryRead(moved, out List<uint>? gameData));
        Assert.Equal([6u, 7u], gameData!.Order());
    }

    [Fact]
    public async Task RebaseLeavesTheOriginalAlone()
    {
        byte[] blob = await CookedEntityAsync();
        byte[] before = (byte[])blob.Clone();
        _ = CollisionGameData.Rebase(blob, 3);

        Assert.Equal(before, blob);
    }

    [Fact]
    public void ABlobThatIsNotACompactSurfaceIsUnreadable()
    {
        byte[] junk = new byte[64];

        Assert.False(CollisionGameData.TryRead(junk, out _));
        Assert.Null(CollisionGameData.Rebase(junk, 1));
    }
}
