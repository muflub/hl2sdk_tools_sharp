using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// A sample-hash voxel: stock's <c>SampleData_t</c> / <c>PatchSampleData_t</c>
/// key, the integer voxel coordinates of a 64-unit grid.
/// </summary>
/// <param name="X">The voxel column.</param>
/// <param name="Y">The voxel row.</param>
/// <param name="Z">The voxel layer.</param>
/// <remarks>
/// Stock stores <c>x * 100</c>, <c>y * 10</c> and <c>z</c> and hashes their SUM
/// But compares the three fields
/// So a voxel's identity is exactly its three coordinates and
/// the scaled form is only a bucket choice. The coordinates come from
/// <c>(int)(p / 64)</c>, which truncates toward zero: voxel 0 spans (-64, 64).
/// </remarks>
public readonly record struct VoxelKey(int X, int Y, int Z)
{
    /// <summary><c>SAMPLEHASH_VOXEL_SIZE</c>.</summary>
    public const float VoxelSize = 64.0f;

    /// <summary>
    /// The voxel of a point, as <c>SampleData_InsertIntoHashTable</c>
 /// And <c>GetPatchSampleHashXYZ</c>
    /// compute it: a float divide by 64, truncated.
    /// </summary>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="z">Z.</param>
    /// <returns>The voxel.</returns>
    public static VoxelKey Of(float x, float y, float z) =>
        new((int)(x / VoxelSize), (int)(y / VoxelSize), (int)(z / VoxelSize));

    internal int Shard(int shardMask)
    {
        // Any mix works: shards only spread the grouping over workers.
        uint h = (uint)((X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791));
        return (int)(h & (uint)shardMask);
    }
}

/// <summary>
/// An immutable voxel-to-items table built in parallel and deterministically:
/// the storage behind stock's <c>g_SampleHashTable</c> and
/// <c>g_PatchSampleHashTable</c>.
/// </summary>
/// <typeparam name="T">The item (a sample handle, a patch index).</typeparam>
/// <remarks>
/// <para>
/// Stock builds both tables in one serial loop -- "make threaded!!!"
/// -- appending each item to its voxel's
/// <c>CUtlVector</c>, so a voxel lists its items in INSERTION order, and the
/// radial filter that reads them sums floats in that order. This builds the
/// same lists in parallel: entries are cut into fixed chunks, each chunk
/// counted and scattered into fixed shards in chunk order, and each shard
/// grouped by voxel in entry order. Every boundary is a function of the entry
/// count alone, so the lists -- and every float summed from them -- are
/// identical at any thread count, and identical to stock's order.
/// </para>
/// </remarks>
public sealed class VoxelTable<T>
    where T : unmanaged
{
    /// <summary>Entries per chunk in the parallel passes.</summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>How many shards the voxels are spread over.</summary>
    public const int ShardCount = 64;

    private readonly Dictionary<VoxelKey, (int Start, int Count)>[] _index;
    private readonly T[][] _items;

    private VoxelTable(Dictionary<VoxelKey, (int Start, int Count)>[] index, T[][] items, int entries)
    {
        _index = index;
        _items = items;
        Entries = entries;
    }

    /// <summary>How many items were inserted.</summary>
    public int Entries { get; }

    /// <summary>How many distinct voxels hold an item.</summary>
    public int Voxels => _index.Sum(d => d.Count);

    /// <summary>An empty table.</summary>
    /// <returns>A table with no voxels.</returns>
    public static VoxelTable<T> CreateEmpty() => new(
        [.. Enumerable.Range(0, ShardCount).Select(_ => new Dictionary<VoxelKey, (int, int)>())],
        [.. Enumerable.Range(0, ShardCount).Select(_ => Array.Empty<T>())],
        0);

    /// <summary><c>CUtlHash::Find</c> then <c>m_Samples</c>: a voxel's items, in insertion order.</summary>
    /// <param name="key">The voxel.</param>
    /// <returns>Its items; empty when the voxel holds none.</returns>
    public ReadOnlySpan<T> Find(VoxelKey key)
    {
        int shard = key.Shard(ShardCount - 1);
        return _index[shard].TryGetValue(key, out (int Start, int Count) r)
            ? _items[shard].AsSpan(r.Start, r.Count)
            : [];
    }

    /// <summary>
    /// Builds the table from entries in insertion order.
    /// </summary>
    /// <param name="keys">Each entry's voxel.</param>
    /// <param name="items">Each entry's item.</param>
    /// <param name="queue">The workers.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The arrays differ in length.</exception>
    public static async Task<VoxelTable<T>> BuildAsync(
        VoxelKey[] keys, T[] items, WorkQueue queue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(queue);
        if (keys.Length != items.Length)
        {
            throw new ArgumentException("one item per key", nameof(items));
        }

        int n = keys.Length;
        int chunks = (n + ChunkSize - 1) / ChunkSize;
        const int mask = ShardCount - 1;
        WorkQueueOptions stage = new() { Stage = "Build Patch/Sample Hash Table(s)" };

        // Count per (chunk, shard).
        int[] counts = new int[chunks * ShardCount];
        await queue.RunAsync(
            chunks,
            (c, _) =>
            {
                int end = Math.Min(n, (c + 1) * ChunkSize);
                for (int i = c * ChunkSize; i < end; i++)
                {
                    counts[(c * ShardCount) + keys[i].Shard(mask)]++;
                }
            },
            stage,
            cancellationToken).ConfigureAwait(false);

        // Offsets: shard-major, chunk order inside a shard.
        int[] offsets = new int[chunks * ShardCount];
        int[] shardSize = new int[ShardCount];
        for (int s = 0; s < ShardCount; s++)
        {
            int at = 0;
            for (int c = 0; c < chunks; c++)
            {
                offsets[(c * ShardCount) + s] = at;
                at += counts[(c * ShardCount) + s];
            }

            shardSize[s] = at;
        }

        VoxelKey[][] shardKeys = new VoxelKey[ShardCount][];
        T[][] shardItems = new T[ShardCount][];
        for (int s = 0; s < ShardCount; s++)
        {
            shardKeys[s] = new VoxelKey[shardSize[s]];
            shardItems[s] = new T[shardSize[s]];
        }

        // Scatter, keeping entry order within each shard.
        await queue.RunAsync(
            chunks,
            (c, _) =>
            {
                Span<int> cursor = stackalloc int[ShardCount];
                for (int s = 0; s < ShardCount; s++)
                {
                    cursor[s] = offsets[(c * ShardCount) + s];
                }

                int end = Math.Min(n, (c + 1) * ChunkSize);
                for (int i = c * ChunkSize; i < end; i++)
                {
                    int s = keys[i].Shard(mask);
                    int at = cursor[s]++;
                    shardKeys[s][at] = keys[i];
                    shardItems[s][at] = items[i];
                }
            },
            stage,
            cancellationToken).ConfigureAwait(false);

        // Group each shard by voxel, in entry order.
        Dictionary<VoxelKey, (int Start, int Count)>[] index = new Dictionary<VoxelKey, (int, int)>[ShardCount];
        T[][] grouped = new T[ShardCount][];
        await queue.RunAsync(
            ShardCount,
            (s, _) =>
            {
                VoxelKey[] sk = shardKeys[s];
                T[] si = shardItems[s];
                Dictionary<VoxelKey, (int Start, int Count)> d = [];
                foreach (VoxelKey k in sk)
                {
                    d[k] = d.TryGetValue(k, out (int Start, int Count) r) ? (0, r.Count + 1) : (0, 1);
                }

                // Voxels laid out in first-appearance order; items in entry order.
                Dictionary<VoxelKey, int> fill = new(d.Count);
                int at = 0;
                foreach (VoxelKey k in sk)
                {
                    if (!fill.ContainsKey(k))
                    {
                        int count = d[k].Count;
                        d[k] = (at, count);
                        fill[k] = at;
                        at += count;
                    }
                }

                T[] outItems = new T[si.Length];
                for (int i = 0; i < sk.Length; i++)
                {
                    outItems[fill[sk[i]]++] = si[i];
                }

                index[s] = d;
                grouped[s] = outItems;
            },
            stage,
            cancellationToken).ConfigureAwait(false);

        return new VoxelTable<T>(index, grouped, n);
    }
}
