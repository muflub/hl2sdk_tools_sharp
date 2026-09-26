using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Phys;
using SourceSharp.Tests.MapTools.Bsp.Collision;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="CollisionModelKey"/>: a brush model's key follows its content,
/// not where the BSP numbered it; the world's follows the world.
/// </summary>
public sealed class CollisionModelKeyTests
{
    private static PhysCollisionInput Map(int worldBrushes = 1, float doorX = 0f, string doorMaterial = "wood", bool doorFace = true)
    {
        CollisionFixture f = new();
        for (int i = 0; i < worldBrushes; i++)
        {
            f.Box(0, new Vec3(-256, -256, -16 - (32 * i)), new Vec3(256, 256, -32 * i), CollisionContents.Solid, f.TexInfoFor("default"));
        }

        int door = f.TexInfoFor(doorMaterial);
        f.Box(1, new Vec3(doorX, 0, 0), new Vec3(doorX + 8, 48, 96), CollisionContents.Solid, door);
        if (doorFace)
        {
            f.Face(1, door, 48f * 96f);
        }

        return f.Build(2);
    }

    [Fact]
    public void ABrushModelKeyIgnoresRenumbering()
    {
        Assert.Equal(CollisionModelKey.OfModel(Map(), 1), CollisionModelKey.OfModel(Map(worldBrushes: 3), 1));
    }

    [Fact]
    public void ABrushModelKeyFollowsItsPlanes()
    {
        Assert.NotEqual(CollisionModelKey.OfModel(Map(), 1), CollisionModelKey.OfModel(Map(doorX: 16f), 1));
    }

    [Fact]
    public void ABrushModelKeyFollowsItsSurfaceProperty()
    {
        Assert.NotEqual(CollisionModelKey.OfModel(Map(), 1), CollisionModelKey.OfModel(Map(doorMaterial: "metal"), 1));
    }

    [Fact]
    public void ABrushModelKeyFollowsItsFaces()
    {
        Assert.NotEqual(CollisionModelKey.OfModel(Map(), 1), CollisionModelKey.OfModel(Map(doorFace: false), 1));
    }

    [Fact]
    public void TheWorldKeyFollowsTheWorldsBrushes()
    {
        Assert.NotEqual(CollisionModelKey.OfModel(Map(), 0), CollisionModelKey.OfModel(Map(worldBrushes: 2), 0));
    }

    [Fact]
    public void TheWorldKeyIgnoresAnEntityMoving()
    {
        Assert.Equal(CollisionModelKey.OfModel(Map(), 0), CollisionModelKey.OfModel(Map(doorX: 16f), 0));
    }

    [Fact]
    public void BrushesOfIsTheCooksWalk()
    {
        Assert.Equal([3], CollisionModelKey.BrushesOf(Map(worldBrushes: 3), 1));
    }
}
