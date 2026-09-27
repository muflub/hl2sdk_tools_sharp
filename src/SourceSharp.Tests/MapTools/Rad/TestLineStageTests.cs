//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// <see cref="TestLineStage"/>: workers batch whole items, park while a batch
/// is in flight rather than wait on it, and every item's result lands by its
/// index whatever the batching, the worker count or the tracer's timing.
/// </summary>
public sealed class TestLineStageTests
{
    private static readonly KdRayTracer Floor = KdRayTracer.Build(
    [
        new TracedTriangle(TraceId.Opaque, new Vec3(-1000, -1000, 0), new Vec3(1000, -1000, 0), new Vec3(0, 1000, 0), 0),
    ]);

    // Item i plans i % 5 segments; segment k goes below the floor when k is
    // even. The result is the item's blocked count, so the right answer is
    // known without a tracer.
    private static int Expected(int item) => (item % 5 + 1) / 2;

    private static Task<int[]> RunAsync(
        IRayTracer tracer, int items, int degree, int batchSegments, int batchItems = TestLineStage.DefaultBatchItems,
        int[]? order = null, CancellationToken cancellationToken = default) =>
        TestLineStage.RunAsync(
            items,
            order,
            new CompileParallelism { MaxDegree = degree },
            () => new Counter(new TestLineBatch(tracer)),
            batchSegments,
            batchItems,
            "test",
            cancellationToken);

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 1_000, false)]
    [InlineData(4, 7, true)]
    [InlineData(4, 1_000, true)]
    [InlineData(3, 1, true)]
    public async Task EveryItemGetsItsOwnAnswerWhateverTheBatching(int degree, int batchSegments, bool asynchronous)
    {
        CountingRayTracer tracer = new(Floor, asynchronous);

        int[] results = await RunAsync(tracer, 300, degree, batchSegments);

        Assert.Equal(Enumerable.Range(0, 300).Select(Expected), results);
        Assert.Equal(Enumerable.Range(0, 300).Sum(i => i % 5), tracer.VisibilityCalls.Sum(c => c.Rays));
    }

    [Fact]
    public async Task ABatchClosesAtItsSegmentBoundWithWholeItems()
    {
        CountingRayTracer tracer = new(Floor);

        await RunAsync(tracer, 50, 1, batchSegments: 10);

        // Whole items only: a batch passes the bound by less than one item.
        Assert.All(tracer.VisibilityCalls, c => Assert.InRange(c.Rays, 1, 10 + 4 - 1));
    }

    [Fact]
    public async Task ABatchClosesAtItsItemBound()
    {
        CountingRayTracer tracer = new(Floor);

        await RunAsync(tracer, 50, 1, batchSegments: 1_000_000, batchItems: 5);

        // Five items of 0..4 segments: ten a batch.
        Assert.Equal(10, tracer.VisibilityCalls.Count);
        Assert.All(tracer.VisibilityCalls, c => Assert.Equal(10, c.Rays));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public async Task AWorkerThatSaysItIsFullClosesTheBatchAfterThatItem(int degree, bool asynchronous)
    {
        CountingRayTracer tracer = new(Floor, asynchronous);
        List<Counter> workers = [];

        int[] results = await RunFullAtAsync(tracer, 50, degree, fullAtItems: 3, workers);

        // The segment and item bounds are far off; the worker's own bound
        // closes every batch at three items, and changes no answer.
        Assert.Equal(Enumerable.Range(0, 50).Select(Expected), results);
        Assert.All(workers, w => Assert.InRange(w.MostItemsInABatch, 0, 3));
        Assert.Equal(3, workers.Max(w => w.MostItemsInABatch));
        if (degree == 1)
        {
            // 17 batches of up to three items, and the fill that finds none.
            Assert.Equal(18, workers[0].Batches);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task AWorkerFullOnAnEmptyBatchStillTakesOneItemABatch(int degree, bool asynchronous)
    {
        CountingRayTracer tracer = new(Floor, asynchronous);
        List<Counter> workers = [];

        int[] results = await RunFullAtAsync(tracer, 20, degree, fullAtItems: 0, workers);

        // Always full: a batch still takes its first item, so every item is
        // lit, one a batch, rather than left unclaimed.
        Assert.Equal(Enumerable.Range(0, 20).Select(Expected), results);
        Assert.Equal(1, workers.Max(w => w.MostItemsInABatch));
        Assert.Equal(20 + workers.Count, workers.Sum(w => w.Batches));
    }

    [Fact]
    public void AWorkerIsNeverFullUnlessItSaysSo()
    {
        Assert.False(new Counter(new TestLineBatch(Floor)).IsBatchFull);
    }

    [Fact]
    public async Task TheClaimOrderDoesNotChangeTheResults()
    {
        int[] reversed = [.. Enumerable.Range(0, 40).Reverse()];

        int[] results = await RunAsync(new CountingRayTracer(Floor, asynchronous: true), 40, 2, 3, order: reversed);

        Assert.Equal(Enumerable.Range(0, 40).Select(Expected), results);
    }

    [Fact]
    public async Task NoItemsIsNoWork()
    {
        CountingRayTracer tracer = new(Floor);

        Assert.Empty(await RunAsync(tracer, 0, 2, 10));
        Assert.Empty(tracer.VisibilityCalls);
    }

    [Fact]
    public async Task AFailedBatchFailsTheStageAfterEveryBatchHasFinished()
    {
        await Assert.ThrowsAsync<IOException>(() => RunAsync(new CountingRayTracer(new Failing(), asynchronous: true), 100, 3, 4));
        await Assert.ThrowsAsync<IOException>(() => RunAsync(new Failing(), 100, 3, 4));
    }

    [Fact]
    public async Task ACancelledStageDoesNoWork()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        CountingRayTracer tracer = new(Floor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(tracer, 10, 2, 10, cancellationToken: source.Token));
        Assert.Empty(tracer.VisibilityCalls);
    }

    [Fact]
    public async Task BadBoundsAreRefused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RunAsync(Floor, 1, 1, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RunAsync(Floor, 1, 1, 1, batchItems: 0));
        await Assert.ThrowsAsync<ArgumentNullException>(() => TestLineStage.RunAsync<int, int>(
            1, null, new CompileParallelism(), null!, 1, 1, "test", CancellationToken.None));
    }

    private static Task<int[]> RunFullAtAsync(IRayTracer tracer, int items, int degree, int fullAtItems, List<Counter> workers) =>
        TestLineStage.RunAsync(
            items,
            null,
            new CompileParallelism { MaxDegree = degree },
            () =>
            {
                Counter c = new FullAt(new TestLineBatch(tracer), fullAtItems);
                lock (workers)
                {
                    workers.Add(c);
                }

                return c;
            },
            1_000_000,
            TestLineStage.DefaultBatchItems,
            "test",
            CancellationToken.None);

    private class Counter(TestLineBatch lines) : TestLineWorker<(int First, int Count), int>(lines)
    {
        public int Batches { get; private set; }

        public int ItemsInBatch { get; private set; }

        public int MostItemsInABatch { get; private set; }

        public override void BeginBatch()
        {
            Batches++;
            ItemsInBatch = 0;
        }

        public override (int First, int Count) Plan(int item, CancellationToken cancellationToken)
        {
            ItemsInBatch++;
            MostItemsInABatch = Math.Max(MostItemsInABatch, ItemsInBatch);
            int first = Lines.Count;
            for (int k = 0; k < item % 5; k++)
            {
                Vec3 start = new(item, k, 10);
                Lines.Add(Ray.Segment(start, start + new Vec3(0, 0, k % 2 == 0 ? -20 : 20), false), RayTraceOptions.TestLine());
            }

            return (first, item % 5);
        }

        public override int Resolve(int item, (int First, int Count) state) =>
            Enumerable.Range(state.First, state.Count).Count(Lines.IsBlocked);
    }

    // Full, by its own count, once it has planned this many items.
    private sealed class FullAt(TestLineBatch lines, int items) : Counter(lines)
    {
        public override bool IsBatchFull => ItemsInBatch >= items;
    }

    private sealed class Failing : IRayTracer
    {
        public string TracerIdentity => "failing";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("device lost"));

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("device lost"));
    }
}
