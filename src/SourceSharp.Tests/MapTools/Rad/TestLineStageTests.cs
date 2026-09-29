//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

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
    public async Task RunnersParkedOnLateBatchesAreMeteredAsOtherLightingAndPromptOnesAreNot()
    {
        RayTraceMeter late = new();
        int[] results = await RunAsync(
            new MeteredRayTracer(new ScrambledRayTracer(Floor, seed: 4), late), 200, 3, batchSegments: 20);

        Assert.Equal(Enumerable.Range(0, 200).Select(Expected), results);
        Assert.True(late.Parked(TraceWaitStage.Other) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, late.Parked(TraceWaitStage.Facelights));
        Assert.Equal(Enumerable.Range(0, 200).Sum(i => i % 5), late.Route(gpu: false).Rays);

        RayTraceMeter prompt = new();
        await RunAsync(new MeteredRayTracer(Floor, prompt), 200, 3, batchSegments: 20);
        Assert.Equal(TimeSpan.Zero, prompt.Parked(TraceWaitStage.Other));
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
            // 50 items, three a batch: 16 full batches and one of two.
            Assert.Equal(17, workers[0].BatchesWithItems);
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
        Assert.Equal(20, workers.Sum(w => w.BatchesWithItems));
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

    public enum Ending
    {
        Finished,
        Faulted,
        Cancelled,
    }

    /// <summary>
    /// However the stage ends -- finished, a batch faulting mid-stage, or
    /// cancelled mid-stage -- every worker is disposed exactly once, after
    /// its last batch completed, and every array the workers rented is back
    /// in the pool: nothing the stage acquired outlives it.
    /// </summary>
    [Theory]
    [InlineData(Ending.Finished, false)]
    [InlineData(Ending.Finished, true)]
    [InlineData(Ending.Faulted, false)]
    [InlineData(Ending.Faulted, true)]
    [InlineData(Ending.Cancelled, false)]
    [InlineData(Ending.Cancelled, true)]
    public async Task EveryWorkerIsDisposedOnceAndReturnsItsScratchHoweverTheStageEnds(Ending ending, bool asynchronous)
    {
        RecyclingScratchPool pool = new();
        using CancellationTokenSource source = new();
        FaultAfter tracer = new(Floor, asynchronous, calls: 6, ending, source);
        List<Disposing> workers = [];

        Task<int[]> run = TestLineStage.RunAsync(
            200,
            null,
            new CompileParallelism { MaxDegree = 3 },
            () =>
            {
                Disposing w = new(new TestLineBatch(tracer, pool), tracer);
                lock (workers)
                {
                    workers.Add(w);
                }

                return w;
            },
            batchSegments: 8,
            TestLineStage.DefaultBatchItems,
            "test",
            source.Token);

        switch (ending)
        {
            case Ending.Finished:
                Assert.Equal(Enumerable.Range(0, 200).Select(Expected), await run);
                break;
            case Ending.Faulted:
                await Assert.ThrowsAsync<IOException>(() => run);
                break;
            default:
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
                break;
        }

        Assert.Equal(3, workers.Count);
        Assert.All(workers, w => Assert.Equal(1, w.Disposals));
        Assert.All(workers, w => Assert.Equal(0, w.CallsInFlightAtDisposal));
        Assert.True(pool.Rented > 0);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    /// <summary>A worker factory that fails partway disposes the workers it had already made.</summary>
    [Fact]
    public async Task WorkersMadeBeforeAFactoryFailureAreDisposed()
    {
        RecyclingScratchPool pool = new();
        List<Disposing> workers = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() => TestLineStage.RunAsync(
            10,
            null,
            new CompileParallelism { MaxDegree = 3 },
            () =>
            {
                if (workers.Count == 2)
                {
                    throw new InvalidOperationException("no third worker");
                }

                Disposing w = new(new TestLineBatch(Floor, pool), null);
                workers.Add(w);
                return w;
            },
            8,
            TestLineStage.DefaultBatchItems,
            "test",
            CancellationToken.None));

        Assert.Equal(2, workers.Count);
        Assert.All(workers, w => Assert.Equal(1, w.Disposals));
    }

    /// <summary>
    /// Once the stage has returned, it holds on to no worker: the workers,
    /// their batches and their scratch are garbage for the collector.
    /// </summary>
    [Fact]
    public async Task NoWorkerOutlivesTheStage()
    {
        List<WeakReference> workers = await RunAndForgetWorkersAsync();

        // The queue's own threads wind down just after the stage returns, and
        // one may still hold its last job's frame for a moment: collect until
        // the workers are gone or a generous deadline passes.
        for (int attempt = 0; attempt < 100 && workers.Any(w => w.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }

        Assert.Equal(2, workers.Count);
        Assert.All(workers, w => Assert.False(w.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> RunAndForgetWorkersAsync()
    {
        List<WeakReference> workers = [];
        await TestLineStage.RunAsync(
            50,
            null,
            new CompileParallelism { MaxDegree = 2 },
            () =>
            {
                Disposing w = new(new TestLineBatch(Floor, new RecyclingScratchPool()), null);
                lock (workers)
                {
                    workers.Add(new WeakReference(w));
                }

                return w;
            },
            8,
            TestLineStage.DefaultBatchItems,
            "test",
            CancellationToken.None);
        return workers;
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
        /// <summary>
        /// Batches that planned at least one item. Not every
        /// <see cref="BeginBatch"/>: a fill that finds no item left opens an
        /// empty batch, and how many of those a worker opens depends on
        /// timing -- with an asynchronous tracer the driver runs every worker
        /// again after each wait for a parked batch, and a worker with
        /// nothing left opens one more empty batch each time.
        /// </summary>
        public int BatchesWithItems { get; private set; }

        public int ItemsInBatch { get; private set; }

        public int MostItemsInABatch { get; private set; }

        public override void BeginBatch() => ItemsInBatch = 0;

        public override (int First, int Count) Plan(int item, CancellationToken cancellationToken)
        {
            if (ItemsInBatch++ == 0)
            {
                BatchesWithItems++;
            }

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

    // Counts its disposals, and how many tracer calls were still running
    // when the stage disposed it (none may be).
    private sealed class Disposing(TestLineBatch lines, FaultAfter? tracer) : Counter(lines)
    {
        public int Disposals { get; private set; }

        public int CallsInFlightAtDisposal { get; private set; }

        public override void Dispose()
        {
            Disposals++;
            CallsInFlightAtDisposal = tracer?.InFlight ?? 0;
            base.Dispose();
        }
    }

    // The floor, until the given number of calls have started; from then on
    // each call faults, or cancels the stage's token and reports it.
    private sealed class FaultAfter(IRayTracer inner, bool asynchronous, int calls, Ending ending, CancellationTokenSource source)
        : IRayTracer
    {
        private int _calls;
        private int _inFlight;

        public int InFlight => Volatile.Read(ref _inFlight);

        public string TracerIdentity => "fault-after";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            bool fail = Interlocked.Increment(ref _calls) > calls && ending != Ending.Finished;
            if (!asynchronous)
            {
                return Answer(fail, rays, hitBits, options, cancellationToken);
            }

            Interlocked.Increment(ref _inFlight);
            return new ValueTask(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1);
                    await Answer(fail, rays, hitBits, options, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }));
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceClosestAsync(rays, hits, options, cancellationToken);

        private ValueTask Answer(
            bool fail, ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken)
        {
            if (!fail)
            {
                return inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
            }

            if (ending == Ending.Faulted)
            {
                return ValueTask.FromException(new IOException("device lost"));
            }

            source.Cancel();
            return ValueTask.FromCanceled(source.Token);
        }
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
