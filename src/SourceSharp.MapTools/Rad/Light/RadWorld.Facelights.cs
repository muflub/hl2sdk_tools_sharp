//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>The face-lighting driver of <see cref="RadWorld"/>: <c>BuildFacelights</c> over every face.</summary>
public sealed partial class RadWorld
{
    /// <summary>
    /// How many rays one worker gathers before it traces them: the size of the
    /// slabs the face lighting hands the tracer. Large, because the tracer
    /// seam is built for batches (a GPU wants tens of thousands of rays per
    /// submission); small enough that every worker's pooled buffers stay a
    /// few megabytes.
    /// </summary>
    internal const int RaysPerBatch = 16 * 1024;

    /// <summary>
    /// The batch size the face lighting uses, <see cref="RaysPerBatch"/> unless
    /// a test asks for another: the output must not depend on it.
    /// </summary>
    internal int FacelightBatchRays { get; set; } = RaysPerBatch;

    /// <summary>
    /// Where the face-lighting workers rent their ray logs' storage, ahead of
    /// <see cref="ScratchPool"/>. Internal so the facts can count what was
    /// rented against what came back, and hand out arrays full of junk; the
    /// output must not depend on it.
    /// </summary>
    internal IScratchArrayPool? FacelightScratchPool { get; set; }

    /// <summary>
    /// The compile's scratch pool, which every stage of this world rents its
    /// large scratch from: the radial sky probe's and the face-lighting
    /// workers' ray logs, and the transfer build's chunk buffers. Null when
    /// the world was made outside a compile; each stage then keeps a pool of
    /// its own for as long as it runs.
    /// </summary>
    /// <remarks>
    /// Set by the driver that owns the pool (<see cref="Vrad"/>), which also
    /// trims it between stages and drops it when the compile ends; the world
    /// never empties it itself.
    /// </remarks>
    internal IScratchArrayPool? ScratchPool { get; set; }

    /// <summary>
    /// How many tape words one worker's batch may hold, whatever its ray
    /// count: an item can write far more tape than rays (every light that
    /// passes the PVS test leaves a record, traced or not), so the rays alone
    /// do not bound a worker's pooled memory. 4 MB.
    /// </summary>
    internal const int TapeWordsPerBatch = 1 << 18;

    /// <summary>
    /// <c>BuildFacelights</c> over every face, then
    /// <c>PrecompLightmapOffsets</c>.
    /// </summary>
    /// <param name="tracer">The tracer.</param>
    /// <param name="parallelism">How many workers.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>A task that completes when every face is lit.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// Every face is prepared first (its samples and luxels), then the faces
    /// are handed out LARGEST FIRST to workers that each run their own
    /// pipeline with no barrier between them: a worker emits the next items of
    /// the faces it holds (and claims more faces) into its own pooled
    /// <see cref="LightRayLog"/> until it holds <see cref="RaysPerBatch"/>
    /// rays, traces them in one slab per kind, traces the skybox rays the
    /// answers call for, resolves every item in emission order, and repeats.
    /// </para>
    /// <para>
    /// A face's result depends only on its own inputs and on its rays'
    /// answers, and those do not depend on which batch they were traced in:
    /// every item's rays start a packet of their own (see
    /// <see cref="LightRayLog"/>), and one skybox camera's rays are one packet.
    /// So the output is byte-identical at any degree of parallelism and under
    /// any batching. Faces, warnings and statistics are committed in face order.
    /// </para>
    /// <para>
    /// With a tracer that answers asynchronously (a GPU), a worker whose slab
    /// is still in flight parks its batch and returns; the driver awaits the
    /// slabs outside the workers and runs the workers again, and each resumes
    /// where it parked. The CPU tracer completes every slab in the call, so a
    /// CPU run is one pass over the workers.
    /// </para>
    /// </remarks>
    public async Task LightFacesAsync(
        IRayTracer tracer,
        CompileParallelism parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(parallelism);
        cancellationToken.ThrowIfCancellationRequested();

        int faceCount = Geometry.Faces.Length;
        FaceLightContext context = new(Geometry, Neighbours, Patches, Tree, Settings, Gatherer, Displacements);
        FaceLightJob[] jobs = new FaceLightJob[faceCount];
        WorkQueueOptions stage = new() { Stage = "BuildFacelights" };

        using WorkQueue queue = new(parallelism);

        await queue.RunAsync<WindingArena, int>(
            faceCount,
            (f, arena, _) =>
            {
                FaceLightJob job = new(context, f);
                job.Prepare(arena);
                jobs[f] = job;
                return 0;
            },
            _ => new WindingArena { Compliance = Settings.Compliance },
            stage,
            cancellationToken).ConfigureAwait(false);

        // Largest first (plan 4p: face cost varies ~100x), so the tail is
        // made of small faces. The order changes only who lights what.
        int[] order = [.. Enumerable.Range(0, faceCount)
            .Where(f => !jobs[f].Done)
            .OrderByDescending(f => jobs[f].SampleCount)
            .ThenBy(f => f)];

        FacelightShared shared = new(jobs, order, Gatherer, tracer, FacelightBatchRays);
        // The compile's pool when there is one, so the outgrown logs of one
        // worker are what the next worker grows into; otherwise a pool of the
        // stage's own, dropped when the stage ends.
        using CompileScratchPool? own = FacelightScratchPool is null && ScratchPool is null ? new() : null;
        IScratchArrayPool pool = FacelightScratchPool ?? ScratchPool ?? own!;
        FacelightWorker[] workers = new FacelightWorker[queue.Degree];
        int made = 0;
        try
        {
            for (; made < workers.Length; made++)
            {
                workers[made] = new FacelightWorker(shared, Geometry.StockEstimates, pool);
            }

            List<Task> pending = [];
            while (true)
            {
                await queue.RunAsync(
                    workers.Length,
                    (w, worker) => workers[w].Run(worker),
                    stage with { ChunkSize = 1 },
                    cancellationToken).ConfigureAwait(false);

                pending.Clear();
                foreach (FacelightWorker worker in workers)
                {
                    pending.AddRange(worker.TakePending());
                }

                if (pending.Count == 0)
                {
                    break;
                }

                foreach (Task task in pending)
                {
                    await task.ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Every worker's rented ray log goes back however the stage ends.
            // A stage that failed or was cancelled may leave slabs in flight
            // (another worker's, or ones the failing await never reached):
            // each is awaited first, because it still reads its worker's rays
            // and writes its answers. Their own failures are not reported --
            // the stage's first failure already is.
            for (int w = 0; w < made; w++)
            {
                foreach (Task call in workers[w].Started)
                {
                    try
                    {
                        await call.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Already failing; see above.
                    }
                }
            }

            for (int w = 0; w < made; w++)
            {
                workers[w].Dispose();
            }
        }

        FaceLights = new FaceLight?[faceCount];
        foreach (FacelightWorker worker in workers)
        {
            Statistics.VisibilityRays += worker.VisibilityRays;
            Statistics.SkyRays += worker.SkyRays;
            Statistics.Batches += worker.Batches;
        }

        foreach (FaceLightJob job in jobs)
        {
            FaceLights[job.FaceNum] = job.Result;
            Warnings.AddRange(job.Warnings);
            if (job.Result is null)
            {
                continue;
            }

            if (job.Result.IsDisplacementDeferred)
            {
                Statistics.DeferredDisplacementFaces++;
                continue;
            }

            Statistics.LitFaces++;
            Statistics.Samples += job.Result.Samples.Length;
            Statistics.LightRecords += job.LightRecords;
            Statistics.CulledLightRecords += job.CulledLightRecords;
        }

        Layout = LightmapOffsets.Compute(Geometry, FaceLights, Settings.SeparateDirectLightmap);
    }

    /// <summary>What every face-lighting worker shares: the jobs and the claim counter.</summary>
    private sealed class FacelightShared(
        FaceLightJob[] jobs, int[] order, DirectLightGatherer gatherer, IRayTracer tracer, int batchRays)
    {
        private int _next;

        public int BatchRays { get; } = batchRays;

        public DirectLightGatherer Gatherer { get; } = gatherer;

        public IRayTracer Tracer { get; } = tracer;

        /// <summary>The next unclaimed face, largest first; null when none is left.</summary>
        public FaceLightJob? Claim()
        {
            int k = Interlocked.Increment(ref _next) - 1;
            return k < order.Length ? jobs[order[k]] : null;
        }
    }

    /// <summary>
    /// One worker's face-lighting pipeline and its pooled buffers: a ray log
    /// (rays, tape, answers), the faces it holds, and where it parked.
    /// </summary>
    private sealed class FacelightWorker : IDisposable
    {
        private readonly FacelightShared _shared;
        private readonly LightRayLog _rays;
        private readonly List<FaceLightJob> _active = [];
        private readonly List<(FaceLightJob Job, int Items)> _batch = [];
        private readonly List<Task> _pending = [];

        // Every call the current batch started that had not finished inside
        // the call. Unlike _pending, which the driver takes, these are kept
        // until the next batch begins, so Dispose can tell whether a call may
        // still be reading the log's rays or writing its answers.
        private readonly List<Task> _started = [];
        private Step _step = Step.Fill;

        public FacelightWorker(FacelightShared shared, bool stockRays, IScratchArrayPool pool)
        {
            _shared = shared;
            _rays = new LightRayLog { StockRays = stockRays, DeferRecursion = true, PadCalls = true, Pool = pool };
        }

        /// <summary>The calls the current batch started that did not finish inside the call.</summary>
        public IReadOnlyList<Task> Started => _started;

        /// <summary>
        /// Returns the log's rented storage. Only once every call in
        /// <see cref="Started"/> has completed -- the driver awaits them
        /// first -- because the pool would lend the storage out while a call
        /// still read the rays or wrote the answers.
        /// </summary>
        public void Dispose()
        {
            _started.Clear();
            _rays.Dispose();
        }

        private enum Step
        {
            Fill,
            FirstStageTraced,
            SecondStageTraced,
        }

        public long VisibilityRays { get; private set; }

        public long SkyRays { get; private set; }

        public int Batches { get; private set; }

        /// <summary>The slabs still in flight; the driver awaits them before running the worker again.</summary>
        public List<Task> TakePending()
        {
            List<Task> tasks = [.. _pending];
            _pending.Clear();
            return tasks;
        }

        /// <summary>Runs batches until no face is left, or a slab is in flight.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Run(WorkerContext worker)
        {
            while (true)
            {
                worker.ThrowIfShouldStop();
                switch (_step)
                {
                    case Step.Fill:
                        if (!Fill())
                        {
                            return;
                        }

                        _step = Step.FirstStageTraced;
                        if (TraceFirstStage(worker.CancellationToken))
                        {
                            return;
                        }

                        break;

                    case Step.FirstStageTraced:
                        _shared.Gatherer.EmitDeferredRecursion(_rays);
                        _step = Step.SecondStageTraced;
                        if (TraceSecondStage(worker.CancellationToken))
                        {
                            return;
                        }

                        break;

                    case Step.SecondStageTraced:
                        Resolve();
                        _step = Step.Fill;
                        break;
                }
            }
        }

        // Emits into a fresh batch: the faces already held first, then newly
        // claimed ones. False when there is nothing left to light.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Fill()
        {
            // The last batch's calls were all awaited before this worker ran
            // again, so none can still be using the log.
            _started.Clear();
            _rays.Reset();
            _batch.Clear();

            foreach (FaceLightJob job in _active)
            {
                if (Full)
                {
                    break;
                }

                Add(job);
            }

            while (!Full && _shared.Claim() is FaceLightJob claimed)
            {
                if (claimed.Done)
                {
                    continue;
                }

                _active.Add(claimed);
                Add(claimed);
            }

            return _batch.Count > 0;
        }

        private bool Full => _rays.TotalCount >= _shared.BatchRays || _rays.Tape.Length >= TapeWordsPerBatch;

        private void Add(FaceLightJob job)
        {
            int items = job.EmitItems(_rays, _shared.BatchRays, TapeWordsPerBatch);
            if (items > 0)
            {
                _batch.Add((job, items));
            }
        }

        // Returns true when a slab is still in flight (the worker parks).
        private bool TraceFirstStage(CancellationToken cancellationToken)
        {
            Batches++;
            VisibilityRays += _rays.VisibilityCount;
            SkyRays += _rays.SkyCount;
            (Memory<ulong> bits, Memory<HitId> hits) = _rays.FirstStageAnswers();

            // The tracer answers for the SEGMENT (IRayTracer.TraceVisibilityAsync),
            // which is TestLine's HitDistance < len.
            if (_rays.VisibilityCount > 0)
            {
                Keep(_shared.Tracer.TraceVisibilityAsync(
                    _rays.VisibilityMemory, bits, RayTraceOptions.StockExact, cancellationToken));
            }

            if (_rays.SkyCount > 0)
            {
                Keep(_shared.Tracer.TraceClosestAsync(
                    _rays.SkyMemory, hits, RayTraceOptions.StockExact, cancellationToken));
            }

            return _pending.Count > 0;
        }

        private bool TraceSecondStage(CancellationToken cancellationToken)
        {
            if (_rays.Sky2Count == 0)
            {
                return false;
            }

            SkyRays += _rays.Sky2Count;
            Keep(_shared.Tracer.TraceClosestAsync(
                _rays.Sky2Memory, _rays.SecondStageAnswers(), RayTraceOptions.StockExact, cancellationToken));
            return _pending.Count > 0;
        }

        // A CPU tracer finishes inside the call; an asynchronous one returns
        // an incomplete task, kept for the driver to await.
        private void Keep(ValueTask task)
        {
            if (!task.IsCompletedSuccessfully)
            {
                Task call = task.AsTask();
                _pending.Add(call);
                _started.Add(call);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Resolve()
        {
            _rays.BeginResolve();
            foreach ((FaceLightJob job, int items) in _batch)
            {
                job.ResolveItems(_rays, items);
            }

            if (!_rays.ReplayComplete)
            {
                throw new InvalidOperationException(
                    "the face lighting resolved a different set of rays than it emitted");
            }

            _active.RemoveAll(static j => j.Done);
        }
    }
}
