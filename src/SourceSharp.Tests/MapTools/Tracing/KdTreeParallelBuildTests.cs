//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="KdRayTracer.BuildAsync"/>: the tree built by subtree on a
/// queue's workers is stock's tree, node for node, whatever the worker count.
/// </summary>
/// <remarks>
/// Both committed scenes (<c>kd-scene</c>, general position; <c>kd-boxes</c>,
/// axis-aligned, which reaches the empty-side growing and the depth cap) are
/// compared against stock's own serialised tree. With four workers the frontier
/// jobs are 512 triangles (<c>KdTreeBuilder.ParallelMinimumTriangles</c>), so
/// both scenes are cut into several jobs and spliced back.
/// </remarks>
public sealed class KdTreeParallelBuildTests
{
    private static async Task<KdRayTracer> BuildOnAsync(StockKdScene scene, int workers)
    {
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = workers });
        return await KdRayTracer.BuildAsync(scene.Triangles, ComplianceOptions.Stock, queue);
    }

    private static int DifferingNodes(StockKdScene scene, KdRayTracer tracer)
    {
        if (tracer.NodeCount != scene.NodeChildren.Length)
        {
            return -1;
        }

        int differing = 0;
        for (int i = 0; i < tracer.NodeCount; i++)
        {
            (int children, int splitBits) = tracer.Node(i);
            differing += children != scene.NodeChildren[i] || splitBits != scene.NodeSplitBits[i] ? 1 : 0;
        }

        return differing;
    }

    private static int DifferingIndices(StockKdScene scene, KdRayTracer tracer)
    {
        if (tracer.IndexCount != scene.Indices.Length)
        {
            return -1;
        }

        int differing = 0;
        for (int i = 0; i < tracer.IndexCount; i++)
        {
            differing += tracer.Index(i) != scene.Indices[i] ? 1 : 0;
        }

        return differing;
    }

    /// <summary>The general-position scene's nodes are stock's, built on four workers.</summary>
    [Fact]
    public async Task TheSceneTreesNodesAreStocksOnFourWorkers() =>
        Assert.Equal(0, DifferingNodes(StockKdScene.Load(), await BuildOnAsync(StockKdScene.Load(), 4)));

    /// <summary>The general-position scene's index list is stock's, built on four workers.</summary>
    [Fact]
    public async Task TheSceneTreesIndicesAreStocksOnFourWorkers() =>
        Assert.Equal(0, DifferingIndices(StockKdScene.Load(), await BuildOnAsync(StockKdScene.Load(), 4)));

    /// <summary>The axis-aligned scene's nodes are stock's, built on four workers.</summary>
    [Fact]
    public async Task TheBoxTreesNodesAreStocksOnFourWorkers() =>
        Assert.Equal(0, DifferingNodes(StockKdScene.Load("kd-boxes"), await BuildOnAsync(StockKdScene.Load("kd-boxes"), 4)));

    /// <summary>The axis-aligned scene's index list is stock's, built on four workers.</summary>
    [Fact]
    public async Task TheBoxTreesIndicesAreStocksOnFourWorkers() =>
        Assert.Equal(0, DifferingIndices(StockKdScene.Load("kd-boxes"), await BuildOnAsync(StockKdScene.Load("kd-boxes"), 4)));

    /// <summary>One worker takes the serial path and gives the same tree.</summary>
    [Fact]
    public async Task OneWorkerBuildsStocksTree() =>
        Assert.Equal(0, DifferingNodes(StockKdScene.Load(), await BuildOnAsync(StockKdScene.Load(), 1)));

    /// <summary>The scene bounds survive the parallel build.</summary>
    [Fact]
    public async Task TheBoundsAreStocksOnFourWorkers()
    {
        StockKdScene scene = StockKdScene.Load();
        KdRayTracer tracer = await BuildOnAsync(scene, 4);
        Assert.Equal(scene.Min, tracer.MinBound);
        Assert.Equal(scene.Max, tracer.MaxBound);
    }

    /// <summary>The parallel build's tracer answers stock's hits on the committed rays.</summary>
    [Fact]
    public async Task TheParallelTreeHitsWhatStockHits()
    {
        StockKdScene scene = StockKdScene.Load();
        KdRayTracer tracer = await BuildOnAsync(scene, 4);
        HitId[] hits = new HitId[scene.RayCount];
        tracer.TraceClosest(scene.Rays, hits, RayTraceOptions.StockExact);
        int differing = 0;
        for (int i = 0; i < hits.Length; i++)
        {
            differing += hits[i].Surface != scene.StockHitId[i] ? 1 : 0;
        }

        Assert.Equal(0, differing);
    }
}
