//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

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
/// One per worker, used by one thread at a time; it owns its scratch
/// (displacement marks and the like) as well as the batch.
/// </remarks>
internal abstract class TestLineWorker<TState, TResult>
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
/// traces, and a batch closes once it holds <c>batchSegments</c> segments or
/// <c>batchItems</c> items (the second bound keeps the planned state of
/// items with few segments -- a leaf with no baked lights -- from piling
/// up). Neither bound changes an answer: every segment is traced on its own
/// (<see cref="RayTraceOptions.IsolatedRays"/>), and each item resolves from
/// its own segments in the order it planned them. Results land by item
/// index, so which worker lit what, and in which batch, is invisible in the
/// output.
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
        for (int w = 0; w < runners.Length; w++)
        {
            runners[w] = new Runner<TState, TResult>(workerFactory(), claims, results, batchSegments, batchItems);
        }

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
        private bool _parked;

        /// <summary>The batches this worker left in flight; the driver awaits and clears them.</summary>
        public List<Task> Pending { get; } = [];

        public void Run(WorkerContext context)
        {
            while (true)
            {
                context.ThrowIfShouldStop();
                if (!_parked)
                {
                    worker.Lines.Clear();
                    worker.BeginBatch();
                    _planned.Clear();
                    while (worker.Lines.Count < batchSegments && _planned.Count < batchItems && claims.TryClaim(out int item))
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
