using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Displacement;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// The sample-hash storage: voxel keys, insertion
/// order, and parallel determinism.
/// </summary>
public sealed class VoxelTableTests
{
    [Theory]
    [InlineData(10.0f, 0)]
    [InlineData(-10.0f, 0)]
    [InlineData(64.0f, 1)]
    [InlineData(-64.0f, -1)]
    [InlineData(-127.9f, -1)]
    [InlineData(200.0f, 3)]
    public void AVoxelCoordinateTruncatesTowardZero(float p, int voxel) =>
        Assert.Equal(voxel, VoxelKey.Of(p, 0, 0).X);

    [Fact]
    public async Task AVoxelListsItsItemsInInsertionOrder()
    {
        VoxelTable<int> t = await Build([new(1, 2, 3), new(0, 0, 0), new(1, 2, 3), new(1, 2, 3)], [10, 20, 30, 40], 1);
        Assert.Equal([10, 30, 40], t.Find(new VoxelKey(1, 2, 3)).ToArray());
    }

    [Fact]
    public async Task AnEmptyVoxelFindsNothing()
    {
        VoxelTable<int> t = await Build([new(1, 2, 3)], [10], 1);
        Assert.True(t.Find(new VoxelKey(3, 2, 1)).IsEmpty);
    }

    [Fact]
    public async Task VoxelsAreDistinctByAllThreeCoordinates()
    {
        // Stock hashes x*100 + y*10 + z but compares the fields.
        VoxelTable<int> t = await Build([new(0, 1, 0), new(0, 0, 10)], [1, 2], 1);
        Assert.Equal([1], t.Find(new VoxelKey(0, 1, 0)).ToArray());
        Assert.Equal([2], t.Find(new VoxelKey(0, 0, 10)).ToArray());
    }

    [Fact]
    public async Task TheCountsAreEntriesAndDistinctVoxels()
    {
        VoxelTable<int> t = await Build([new(1, 1, 1), new(2, 2, 2), new(1, 1, 1)], [1, 2, 3], 1);
        Assert.Equal(3, t.Entries);
        Assert.Equal(2, t.Voxels);
    }

    [Fact]
    public async Task OneWorkerAndEightBuildTheSameLists()
    {
        // 300,000 entries: several chunks, every shard.
        Random rng = new(1234);
        VoxelKey[] keys = new VoxelKey[300_000];
        int[] items = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new VoxelKey(rng.Next(-20, 20), rng.Next(-20, 20), rng.Next(-5, 5));
            items[i] = i;
        }

        VoxelTable<int> one = await Build(keys, items, 1);
        VoxelTable<int> eight = await Build(keys, items, 8);
        foreach (VoxelKey k in keys.Distinct())
        {
            int[] a = one.Find(k).ToArray();
            Assert.Equal(a, eight.Find(k).ToArray());
            Assert.True(a.SequenceEqual(a.Order()), $"voxel {k} lost insertion order");
        }
    }

    [Fact]
    public void AnEmptyTableFindsNothing() =>
        Assert.True(VoxelTable<int>.CreateEmpty().Find(new VoxelKey(0, 0, 0)).IsEmpty);

    private static async Task<VoxelTable<int>> Build(VoxelKey[] keys, int[] items, int workers)
    {
        using WorkQueue q = new(new CompileParallelism { MaxDegree = workers });
        return await VoxelTable<int>.BuildAsync(keys, items, q, CancellationToken.None);
    }
}
