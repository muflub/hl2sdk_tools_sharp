//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
using System.Runtime.ExceptionServices;

using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// One worker of a <see cref="TestLineStage"/>: plans items' segments into its
/// batch and, once the batch is answered, turns each item's answers into its
/// result.
/// </summary>
/// <typeparam name="TState">What an item keeps between its plan and its resolve.</typeparam>
/// <typeparam name="TResult">What an item produces.</typeparam>
/// <remarks>
/// <para>
/// One per worker, used by one thread at a time; it owns its scratch
/// (displacement marks and the like) as well as the batch.
/// </para>
/// <para>
/// DISPOSABLE because that scratch is rented: the batch's arrays, and
/// whatever pooled buffers a stage's worker adds, go back to their pool when
/// <see cref="TestLineStage"/> disposes the worker at the end of the stage --
/// finished, failed or cancelled, and only once no trace of its batch is in
/// flight. A worker that overrides <see cref="Dispose"/> calls the base, which
/// disposes the batch.
/// </para>
/// </remarks>
internal abstract class TestLineWorker<TState, TResult> : IDisposable
{
    /// <summary>Makes a worker over its batch.</summary>
    /// <param name="lines">The worker's own batch.</param>
    protected TestLineWorker(TestLineBatch lines) => Lines = lines;

    /// <summary>The worker's batch; items add their segments to it in <see cref="Plan"/>.</summary>
    public TestLineBatch Lines { get; }

    /// <summary>Everything an item computes before its traces, its segments added to <see cref="Lines"/>.</summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The stage's token.</param>
    /// <returns>What the item needs back in <see cref="Resolve"/>.</returns>
    public abstract TState Plan(int item, CancellationToken cancellationToken);

    /// <summary>The item's result, from its state and its answers in the traced <see cref="Lines"/>.</summary>
    /// <param name="item">The item.</param>
    /// <param name="state">What <see cref="Plan"/> returned for it.</param>
    /// <returns>The result.</returns>
    public abstract TResult Resolve(int item, TState state);

    /// <summary>Called before a batch is filled, to clear what the last batch's items shared.</summary>
    public virtual void BeginBatch()
    {
    }

    /// <summary>
    /// Whether the worker's own planned state is as large as it should grow:
    /// once true, the batch closes before claiming another item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stage's two bounds count segments and items, which is all it can
    /// see. A worker whose items keep state of their own besides segments --
    /// a pending sample per light, most of which plan no segment at all --
    /// can pile that state up far past what either bound notices; this is
    /// the worker's own bound for it. It is read between items, like the
    /// other two, and only once the batch holds an item: a batch always takes
    /// at least one, so a worker that reports full on an empty batch still
    /// makes progress (one item a batch) instead of stranding the rest.
    /// </para>
    /// <para>
    /// Like the other bounds it only decides where one batch ends and the
    /// next begins, so it changes no answer.
    /// </para>
    /// </remarks>
    public virtual bool IsBatchFull => false;

    /// <summary>Returns the worker's rented scratch, the batch's included. Called once, by the stage.</summary>
    public virtual void Dispose() => Lines.Dispose();
}

/// <summary>
/// Runs a stage whose items test <c>TestLine</c> segments: each worker plans
/// items into its own <see cref="TestLineBatch"/> until it is full, traces it
/// through the <see cref="IRayTracer"/> seam, and resolves the items.
/// </summary>
/// <remarks>
/// <para>
/// THE SHAPE OF <see cref="Light.RadWorld.LightFacesAsync"/>, for the other
/// lighting stages. With the CPU tracer every batch is answered inside the
/// call, so a run is one pass over the workers. With a tracer that answers
/// asynchronously (a GPU), a worker whose batch is in flight parks and
/// returns; the driver awaits the batches outside the workers and runs the
/// workers again, and each resumes where it parked. No worker ever blocks on
/// a task: that would take one of the compile's dedicated threads out of
/// circulation for the length of a GPU round trip.
/// </para>
/// <para>
/// A batch holds whole items: an item's segments are never split across
/// traces, and a batch closes once it holds <c>batchSegments</c> segments,
/// <c>batchItems</c> items (the second bound keeps the planned state of
/// items with few segments -- a leaf with no baked lights -- from piling
/// up), or once the worker itself says it is full
/// (<see cref="TestLineWorker{TState, TResult}.IsBatchFull"/>, for planned
/// state the stage cannot count). The bounds are checked between items, so
/// a batch passes them by at most one item; a stage whose items can be large
/// keeps its items small instead (static-prop lighting plans a few vertices
/// an item, not a whole prop), because every worker keeps the capacity its
/// largest batch needed. No bound changes an answer: every segment is traced
/// on its own (<see cref="RayTraceOptions.IsolatedRays"/>), and each item
/// resolves from its own segments in the order it planned them. Results
/// land by item index, so which worker lit what, and in which batch, is
/// invisible in the output.
/// </para>
/// </remarks>
internal static class TestLineStage
{
    /// <summary>The segments a batch closes at, unless the stage says otherwise.</summary>
    /// <remarks>
    /// About 3 MB of rays: large enough that a GPU round trip carries a real
    /// batch, small enough that every worker's batch together stays a few
    /// tens of megabytes. One item larger than this is traced whole.
    /// </remarks>
    public const int DefaultBatchSegments = 1 << 16;

    /// <summary>The items a batch closes at, unless the stage says otherwise.</summary>
    public const int DefaultBatchItems = 256;

    /// <summary>Runs the stage.</summary>
    /// <typeparam name="TState">What an item keeps between plan and resolve.</typeparam>
    /// <typeparam name="TResult">What an item produces.</typeparam>
    /// <param name="itemCount">How many items.</param>
    /// <param name="order">The order items are claimed in (largest first, say), or null for index order.</param>
    /// <param name="parallelism">The compile's parallelism.</param>
    /// <param name="workerFactory">Makes one worker; called once per worker, before any runs.</param>
    /// <param name="batchSegments">The segments a batch closes at.</param>
    /// <param name="batchItems">The items a batch closes at.</param>
    /// <param name="stage">The stage's name, for progress and diagnostics.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>The results, by item index.</returns>
    public static async Task<TResult[]> RunAsync<TState, TResult>(
        int itemCount,
        int[]? order,
        CompileParallelism parallelism,
        Func<TestLineWorker<TState, TResult>> workerFactory,
        int batchSegments,
        int batchItems,
        string stage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parallelism);
        ArgumentNullException.ThrowIfNull(workerFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSegments, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchItems, 1);
        cancellationToken.ThrowIfCancellationRequested();

        TResult[] results = new TResult[itemCount];
        if (itemCount == 0)
        {
            return results;
        }

        using WorkQueue queue = new(parallelism);
        Claims claims = new(itemCount, order);
        Runner<TState, TResult>[] runners = new Runner<TState, TResult>[queue.Degree];
        int made = 0;
        try
        {
            for (; made < runners.Length; made++)
            {
                runners[made] = new Runner<TState, TResult>(workerFactory(), claims, results, batchSegments, batchItems);
            }

            return await DriveAsync(queue, runners, results, stage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Every worker's rented scratch goes back however the stage ends.
            // DriveAsync returns or throws only once every batch it started
            // has completed, so no tracer call still reads a worker's rays or
            // writes its bits here.
            for (int w = 0; w < made; w++)
            {
                runners[w].Worker.Dispose();
            }
        }
    }

    /// <summary>Runs the workers until every item is resolved, awaiting their batches between runs.</summary>
    private static async Task<TResult[]> DriveAsync<TState, TResult>(
        WorkQueue queue,
        Runner<TState, TResult>[] runners,
        TResult[] results,
        string stage,
        CancellationToken cancellationToken)
    {
        WorkQueueOptions options = new() { Stage = stage, ChunkSize = 1 };
        List<Task> pending = [];
        while (true)
        {
            ExceptionDispatchInfo? failure = null;
            try
            {
                await queue.RunAsync(
                    runners.Length,
                    (w, worker) => runners[w].Run(worker),
                    options,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            pending.Clear();
            foreach (Runner<TState, TResult> runner in runners)
            {
                pending.AddRange(runner.Pending);
                runner.Pending.Clear();
            }

            // Every batch in flight is awaited whatever the others ended in:
            // it reads a worker's rays and writes its bits, and neither may be
            // left to a call still running when the stage unwinds. The first
            // failure is the one reported.
            foreach (Task task in pending)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure ??= ExceptionDispatchInfo.Capture(ex);
                }
            }

            failure?.Throw();
            if (pending.Count == 0)
            {
                return results;
            }
        }
    }

    /// <summary>The shared claim counter over the items' order.</summary>
    private sealed class Claims(int count, int[]? order)
    {
        private int _next;

        public bool TryClaim(out int item)
        {
            int k = Interlocked.Increment(ref _next) - 1;
            if (k >= count)
            {
                item = -1;
                return false;
            }

            item = order is null ? k : order[k];
            return true;
        }
    }

    /// <summary>One worker's fill / trace / resolve loop, and where it parked.</summary>
    private sealed class Runner<TState, TResult>(
        TestLineWorker<TState, TResult> worker, Claims claims, TResult[] results, int batchSegments, int batchItems)
    {
        private readonly List<(int Item, TState State)> _planned = [];
        private readonly RayTraceMeter? _meter = RayTraceMeter.Of(worker.Lines.Tracer);
        private bool _parked;

        // When this runner parked on its batch, in Stopwatch ticks; the span
        // to its next run is parked worker time (RayTraceMeter's remarks).
        private long _parkedAt;

        /// <summary>The worker this runner drives; the stage disposes it.</summary>
        public TestLineWorker<TState, TResult> Worker => worker;

        /// <summary>The batches this worker left in flight; the driver awaits and clears them.</summary>
        public List<Task> Pending { get; } = [];

        public void Run(WorkerContext context)
        {
            if (_parked && _meter is not null)
            {
                _meter.AddParked(TraceWaitStage.Other, Stopwatch.GetTimestamp() - _parkedAt);
            }

            while (true)
            {
                context.ThrowIfShouldStop();
                if (!_parked)
                {
                    worker.Lines.Clear();
                    worker.BeginBatch();
                    _planned.Clear();
                    while (worker.Lines.Count < batchSegments
                        && _planned.Count < batchItems
                        && (_planned.Count == 0 || !worker.IsBatchFull)
                        && claims.TryClaim(out int item))
                    {
                        _planned.Add((item, worker.Plan(item, context.CancellationToken)));
                    }

                    if (_planned.Count == 0)
                    {
                        return;
                    }

                    worker.Lines.BeginTrace(Pending, context.CancellationToken);
                    if (Pending.Count > 0)
                    {
                        _parked = true;
                        _parkedAt = Stopwatch.GetTimestamp();
                        return;
                    }
                }

                _parked = false;
                worker.Lines.EndTrace();
                foreach ((int item, TState state) in _planned)
                {
                    results[item] = worker.Resolve(item, state);
                }
            }
        }
    }
}
