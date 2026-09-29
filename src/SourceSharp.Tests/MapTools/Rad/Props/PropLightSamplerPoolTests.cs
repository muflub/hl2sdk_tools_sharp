//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// <see cref="PropLightSampler.CreateBatch(int)"/>: a prop stage's worker
/// rents its batch through its own shard of the compile's pool, and without
/// a pool its batch keeps plain storage.
/// </summary>
public sealed class PropLightSamplerPoolTests
{
    private static readonly Ray Down = Ray.Segment(new Vec3(0, 0, 10), Vec3.Zero, stockReciprocal: true);

    /// <summary>
    /// A worker's batch rents through that worker's shard: the same worker's
    /// next batch finds the arrays in its own shard, and another worker's
    /// batch has to borrow them from there.
    /// </summary>
    [Fact]
    public void AWorkersBatchRentsThroughThatWorkersShard()
    {
        using CompileScratchPool pool = new();
        PropLightSampler sampler = new(new EmptySceneTracer(), ComplianceOptions.Correct) { ScratchPool = pool };

        using (TestLineBatch batch = sampler.CreateBatch(2))
        {
            _ = batch.Add(Down, RayTraceOptions.TestLine());
        }

        Assert.Equal(3, pool.WorkerShards);
        int misses = pool.Statistics.Misses;
        Assert.True(misses > 0);

        using (TestLineBatch again = sampler.CreateBatch(2))
        {
            _ = again.Add(Down, RayTraceOptions.TestLine());
        }

        Assert.Equal(misses, pool.Statistics.Misses);
        Assert.Equal(0, pool.Statistics.CrossWorkerHits);

        using (TestLineBatch other = sampler.CreateBatch(0))
        {
            _ = other.Add(Down, RayTraceOptions.TestLine());
        }

        Assert.Equal(misses, pool.Statistics.Misses);
        Assert.True(pool.Statistics.CrossWorkerHits > 0);
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>Without a pool, a worker's batch is the same plain batch <see cref="PropLightSampler.CreateBatch()"/> makes.</summary>
    [Fact]
    public void WithoutAPoolAWorkersBatchKeepsItsOwnStorage()
    {
        PropLightSampler sampler = new(new EmptySceneTracer(), ComplianceOptions.Correct);

        using TestLineBatch batch = sampler.CreateBatch(5);
        int index = batch.Add(Down, RayTraceOptions.TestLine());
        batch.Trace(CancellationToken.None);

        Assert.False(batch.IsBlocked(index));
    }
}
