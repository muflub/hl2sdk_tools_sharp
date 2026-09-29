//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
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
    /// How many batches one face-lighting worker keeps in flight on a tracer
    /// that answers asynchronously, unless the compile asks for another depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enough that a worker always has a batch the device is working on
    /// while it resolves the last one and fills the next: one in flight, one
    /// answered and waiting to be resolved, one being filled, and one more to
    /// cover a round trip that runs longer than a fill. At 16,384 rays a
    /// batch, 32 workers at this depth keep about two million rays queued,
    /// which is a slab and a half of the Vulkan tracer's default budget, so
    /// its slot ring stays full without any one worker holding more than
    /// four logs of scratch.
    /// </para>
    /// <para>
    /// A tracer that answers inside the call (the CPU tracer) never has a
    /// batch in flight, so the depth costs it nothing: each worker's pipeline
    /// then holds one batch at a time, exactly as before pipelining.
    /// </para>
    /// </remarks>
    public const int DefaultFacelightPipelineDepth = 4;

    /// <summary>The deepest a face-lighting worker's pipeline may be asked to run.</summary>
    /// <remarks>
    /// A bound, not a target: every batch in flight holds its worker's
    /// pooled rays, tape and answers, a few megabytes each, and past a few
    /// batches per worker a device's queue is already full.
    /// </remarks>
    public const int MaxFacelightPipelineDepth = 64;

    private int _facelightPipelineDepth = DefaultFacelightPipelineDepth;

    /// <summary>
    /// The batch size the face lighting uses, <see cref="RaysPerBatch"/> unless
    /// a test asks for another: the output must not depend on it.
    /// </summary>
    internal int FacelightBatchRays { get; set; } = RaysPerBatch;

    /// <summary>
    /// How many batches each face-lighting worker may have traced and not yet
    /// resolved: <see cref="DefaultFacelightPipelineDepth"/> unless the
    /// compile (<see cref="VradContext.GpuPipelineDepth"/>) or a fact asks
    /// for another. The output must not depend on it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1, or more than <see cref="MaxFacelightPipelineDepth"/>.</exception>
    internal int FacelightPipelineDepth
    {
        get => _facelightPipelineDepth;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxFacelightPipelineDepth);
            _facelightPipelineDepth = value;
        }
    }

    /// <summary>
    /// The most batches any one face-lighting worker had traced and not yet
    /// resolved at once, in the last <see cref="LightFacesAsync"/>: never
    /// more than <see cref="FacelightPipelineDepth"/>, and 1 with a tracer
    /// that answers inside the call. For the facts that hold the bound.
    /// </summary>
    internal int FacelightPeakBatchesInFlight { get; private set; }

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
    /// are handed out LARGEST FIRST to pipelines, one per worker, that each
    /// run with no barrier between them: a pipeline emits the next items of
    /// the faces it holds (and claims more faces) into a pooled
    /// <see cref="LightRayLog"/> until it holds <see cref="RaysPerBatch"/>
    /// rays, traces them in one call per kind, traces the skybox rays the
    /// answers call for, resolves every item in emission order, and repeats.
    /// </para>
    /// <para>
    /// <b>Pipelined.</b> With a tracer that answers asynchronously (a GPU),
    /// a pipeline does not wait for a batch it has traced: it fills and traces
    /// the next, up to <see cref="FacelightPipelineDepth"/> batches traced and
    /// not yet resolved, and resolves each as its answers arrive. An earlier
    /// driver let each worker hold one batch and park it, and ran the workers
    /// again only once every worker had parked and every slab had come back:
    /// the device idled while the workers filled and resolved, and the
    /// workers idled while it traced, which measured about 6 % device
    /// occupancy on a 16-thread run and made direct light slower on the GPU
    /// than on the CPU. Now a pipeline stalls only when its every batch is in
    /// flight, and the resolve of one batch runs while later ones trace.
    /// </para>
    /// <para>
    /// <b>Pipelines are not threads.</b> The workers draw from every pipeline
    /// (<see cref="WorkQueue.RunLoopAsync{TScratch}"/>): a worker prefers its
    /// own, takes any other that has something to do and is not taken, and
    /// when none has, leaves the run until an answer arrives and wakes one.
    /// So no thread is ever blocked on a device, and a worker whose pipeline
    /// is waiting keeps the others going instead. A face stays with the
    /// pipeline that claimed it for its whole life.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> A face's result depends only on its own inputs and
    /// on its rays' answers, and those do not depend on which batch they were
    /// traced in, or when: every item's rays start a packet of their own (see
    /// <see cref="LightRayLog"/>), one skybox camera's rays are one packet,
    /// and each batch's answers land in that batch's own log, by ray index.
    /// A pipeline resolves its batches strictly in the order it filled them,
    /// whatever order they came back in, so every face resolves its items in
    /// emission order and allocates its styles and sums its lights in the
    /// same order as a single-threaded run with the CPU tracer. The output
    /// is byte-identical at any degree of parallelism, any batching, any
    /// depth and any timing of the answers. Faces, warnings and statistics
    /// are committed in face order.
    /// </para>
    /// <para>
    /// The CPU tracer completes every call inside it, so a CPU pipeline holds
    /// one batch at a time and runs fill, trace and resolve back to back, as
    /// the unpipelined driver did.
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

        LoopWaker waker = new();
        FacelightShared shared = new(jobs, order, Gatherer, tracer, FacelightBatchRays, FacelightPipelineDepth, waker);
        // The compile's pool when there is one, so the outgrown logs of one
        // worker are what the next worker grows into; otherwise a pool of the
        // stage's own, dropped when the stage ends.
        using CompileScratchPool? own = FacelightScratchPool is null && ScratchPool is null ? new() : null;
        IScratchArrayPool pool = FacelightScratchPool ?? ScratchPool ?? own!;
        FacelightPipeline[] pipelines = new FacelightPipeline[queue.Degree];
        int made = 0;
        try
        {
            for (; made < pipelines.Length; made++)
            {
                pipelines[made] = new FacelightPipeline(shared, Geometry.StockEstimates, pool);
            }

            await queue.RunLoopAsync(
                (index, worker) => Serve(pipelines, index, worker),
                static index => index,
                waker,
                stage,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Every pipeline's rented ray logs go back however the stage ends.
            // The run is over, so no worker is inside a pipeline; but a stage
            // that failed or was cancelled may leave batches in flight (any
            // pipeline's), and each is awaited first, because it still reads
            // its batch's rays and writes its answers. Their own failures are
            // not reported -- the stage's first failure already is.
            for (int p = 0; p < made; p++)
            {
                foreach (Task call in pipelines[p].InFlightCalls())
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

            for (int p = 0; p < made; p++)
            {
                pipelines[p].Dispose();
            }
        }

        FaceLights = new FaceLight?[faceCount];
        RayTraceMeter? meter = RayTraceMeter.Of(tracer);
        FacelightPeakBatchesInFlight = 0;
        foreach (FacelightPipeline pipeline in pipelines)
        {
            meter?.AddParked(TraceWaitStage.Facelights, pipeline.ParkedTicks);
            Statistics.VisibilityRays += pipeline.VisibilityRays;
            Statistics.SkyRays += pipeline.SkyRays;
            Statistics.Batches += pipeline.Batches;
            FacelightPeakBatchesInFlight = Math.Max(FacelightPeakBatchesInFlight, pipeline.PeakInFlight);
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

    /// <summary>
    /// One unit of face lighting for the worker at <paramref name="index"/>:
    /// its own pipeline first, then any other that is free and has something
    /// to do.
    /// </summary>
    /// <returns>
    /// <see cref="LoopStep.Worked"/> when a pipeline moved; <see cref="LoopStep.Finished"/>
    /// when every pipeline is done; otherwise <see cref="LoopStep.Idle"/>, and
    /// the worker leaves until an answer arrives (a pipeline's completion
    /// callback wakes one) or a pipeline another worker held is let go with
    /// work in it (<see cref="FacelightPipeline.Exit"/> wakes one).
    /// </returns>
    private static LoopStep Serve(FacelightPipeline[] pipelines, int index, WorkerContext worker)
    {
        int n = pipelines.Length;
        for (int k = 0; k < n; k++)
        {
            FacelightPipeline pipeline = pipelines[(index + k) % n];
            if (pipeline.IsFinished || !pipeline.TryEnter())
            {
                continue;
            }

            bool worked;
            try
            {
                worked = pipeline.Step(worker);
            }
            finally
            {
                pipeline.Exit();
            }

            if (worked)
            {
                return LoopStep.Worked;
            }
        }

        foreach (FacelightPipeline pipeline in pipelines)
        {
            if (!pipeline.IsFinished)
            {
                return LoopStep.Idle;
            }
        }

        return LoopStep.Finished;
    }

    /// <summary>What every face-lighting pipeline shares: the jobs, the claim counter and the waker.</summary>
    private sealed class FacelightShared(
        FaceLightJob[] jobs, int[] order, DirectLightGatherer gatherer, IRayTracer tracer, int batchRays, int depth, LoopWaker waker)
    {
        private int _next;

        public int BatchRays { get; } = batchRays;

        public int Depth { get; } = depth;

        public DirectLightGatherer Gatherer { get; } = gatherer;

        public IRayTracer Tracer { get; } = tracer;

        public LoopWaker Waker { get; } = waker;

        /// <summary>The next unclaimed face, largest first; null when none is left.</summary>
        public FaceLightJob? Claim()
        {
            int k = Interlocked.Increment(ref _next) - 1;
            return k < order.Length ? jobs[order[k]] : null;
        }
    }

    /// <summary>Where one batch of a pipeline is.</summary>
    private enum BatchState
    {
        /// <summary>The slot holds nothing; its log may be reused.</summary>
        Free,

        /// <summary>Its visibility and sky rays are being traced.</summary>
        FirstStage,

        /// <summary>Its skybox rays, chosen from the first answers, are being traced.</summary>
        SecondStage,

        /// <summary>Every answer is in; it waits its turn to be resolved.</summary>
        Answered,
    }

    /// <summary>
    /// One batch: a pooled ray log (rays, tape, answers), the items it
    /// carries, and the tracer calls of its current stage that had not
    /// finished inside the call.
    /// </summary>
    private sealed class FacelightBatch(LightRayLog log) : IDisposable
    {
        public LightRayLog Log { get; } = log;

        /// <summary>The faces whose items this batch carries, in emission order, and how many of each.</summary>
        public List<(FaceLightJob Job, int Items)> Items { get; } = [];

        /// <summary>
        /// The current stage's calls that had not finished inside the call.
        /// Kept until every one of them has finished and been checked, so the
        /// driver can tell, however the stage ends, whether a call may still
        /// be reading the log's rays or writing its answers.
        /// </summary>
        public List<Task> Calls { get; } = [];

        public BatchState State { get; set; }

        /// <summary>Whether every call of the current stage has finished.</summary>
        public bool CallsDone
        {
            get
            {
                foreach (Task call in Calls)
                {
                    if (!call.IsCompleted)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>Throws the first failure or cancellation among the finished calls, then forgets them.</summary>
        public void TakeCalls(CancellationToken cancellationToken)
        {
            foreach (Task call in Calls)
            {
                TestLineBatch.RethrowIfFailed(call, cancellationToken);
            }

            Calls.Clear();
        }

        /// <summary>
        /// Returns the log's rented storage. Only once every call in
        /// <see cref="Calls"/> has finished -- the driver awaits them first --
        /// because the pool would lend the storage out while a call still
        /// read the rays or wrote the answers.
        /// </summary>
        public void Dispose()
        {
            Calls.Clear();
            Log.Dispose();
        }
    }

    /// <summary>
    /// One worker's face-lighting pipeline: the faces it holds, a ring of up
    /// to <see cref="FacelightShared.Depth"/> batches traced and not yet
    /// resolved, oldest first, and its counters. One worker at a time runs
    /// it (<see cref="TryEnter"/>); completions of its calls only flag it and
    /// wake a worker.
    /// </summary>
    private sealed class FacelightPipeline : IDisposable
    {
        private readonly FacelightShared _shared;
        private readonly bool _stockRays;
        private readonly IScratchArrayPool _pool;
        private readonly List<FaceLightJob> _active = [];
        private readonly FacelightBatch?[] _ring;
        private readonly Action _answered;
        private int _head;
        private int _count;
        private bool _exhausted;
        private int _busy;
        private int _signal;
        private volatile bool _finished;

        // When the pipeline last found every batch it could move in flight,
        // in Stopwatch ticks, or 0: the span to the next move it makes is
        // parked time (RayTraceMeter's remarks).
        private long _stalledSince;

        public FacelightPipeline(FacelightShared shared, bool stockRays, IScratchArrayPool pool)
        {
            _shared = shared;
            _stockRays = stockRays;
            _pool = pool;
            _ring = new FacelightBatch?[shared.Depth];

            // One delegate for the pipeline's life, handed to every call that
            // is still running when the pipeline moves on: it flags the
            // pipeline and wakes a worker to move it. The flag is what Exit
            // reads, so an answer that lands while a worker is inside is not
            // lost when that worker leaves.
            _answered = () =>
            {
                Volatile.Write(ref _signal, 1);
                _shared.Waker.Wake(1);
            };
        }

        /// <summary>Whether every face this pipeline claimed is lit and no more are left to claim.</summary>
        public bool IsFinished => _finished;

        public long VisibilityRays { get; private set; }

        public long SkyRays { get; private set; }

        public int Batches { get; private set; }

        /// <summary>The most batches it held traced and not yet resolved.</summary>
        public int PeakInFlight { get; private set; }

        /// <summary>
        /// Stopwatch ticks it spent with nothing it could do but wait for a
        /// batch in flight: from the step that found it so to the step that
        /// could move it again.
        /// </summary>
        public long ParkedTicks { get; private set; }

        /// <summary>Takes the pipeline for one step; false when another worker has it.</summary>
        public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

        /// <summary>
        /// Lets the pipeline go. If an answer arrived while it was held, a
        /// worker is woken for it: the worker that answer woke may have found
        /// the pipeline taken and left.
        /// </summary>
        public void Exit()
        {
            Volatile.Write(ref _busy, 0);
            if (Volatile.Read(ref _signal) != 0)
            {
                _shared.Waker.Wake(1);
            }
        }

        /// <summary>Every call of every batch that has not been checked yet: the ones the driver must await.</summary>
        public IEnumerable<Task> InFlightCalls() =>
            _ring.Where(static b => b is not null).SelectMany(static b => b!.Calls);

        /// <summary>
        /// Moves the pipeline as far as it can without waiting: advances every
        /// batch whose calls have finished, resolves the answered batches at
        /// the head in order, and fills and traces new batches while the ring
        /// has room.
        /// </summary>
        /// <param name="worker">The worker running it: cancellation, and the token the calls carry.</param>
        /// <returns>Whether it moved at all.</returns>
        /// <remarks>
        /// It returns after the first batch it resolves once it has traced a
        /// new one behind it, so a worker gives the others a turn (and its
        /// pool thread to other stages) about once a batch, and leaves the
        /// device with work when it does.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool Step(WorkerContext worker)
        {
            CancellationToken cancellationToken = worker.CancellationToken;

            // Cleared before anything is looked at: an answer after this line
            // sets it again, and Exit sees it.
            Volatile.Write(ref _signal, 0);
            bool worked = false;
            bool resolved = false;
            while (true)
            {
                worker.ThrowIfShouldStop();
                bool moved = Advance(cancellationToken);
                while (_count > 0 && _ring[_head]!.State == BatchState.Answered)
                {
                    Resolve(_ring[_head]!);
                    _head = (_head + 1) % _ring.Length;
                    _count--;
                    moved = true;
                    resolved = true;
                }

                bool launched = false;
                if (!moved && _count < _ring.Length)
                {
                    launched = Launch(cancellationToken);
                    moved = launched;
                }

                if (!moved)
                {
                    break;
                }

                worked = true;
                if (_stalledSince != 0)
                {
                    ParkedTicks += Stopwatch.GetTimestamp() - _stalledSince;
                    _stalledSince = 0;
                }

                if (resolved && launched)
                {
                    return true;
                }
            }

            if (_count == 0 && _active.Count == 0 && _exhausted)
            {
                _finished = true;
                return true;
            }

            if (_count == 0)
            {
                // Faces held, nothing in flight, and none of them could emit:
                // no answer will ever arrive to move this pipeline, so waiting
                // would hang the stage. A face's next round always has items
                // once its last one is resolved, so this is a broken
                // invariant, reported rather than waited on.
                throw new InvalidOperationException(
                    $"the face lighting holds {_active.Count} face(s) with nothing in flight and nothing to emit");
            }

            // Nothing moves until an answer arrives.
            if (_count > 0 && _stalledSince == 0)
            {
                _stalledSince = Stopwatch.GetTimestamp();
            }

            return worked;
        }

        // Moves every batch whose current stage's calls have all finished to
        // its next stage; true when any moved. Each batch's stages depend
        // only on its own answers, so the order batches move in here decides
        // nothing; only the resolve order does, and that is the ring's.
        private bool Advance(CancellationToken cancellationToken)
        {
            bool moved = false;
            for (int k = 0; k < _count; k++)
            {
                FacelightBatch batch = _ring[(_head + k) % _ring.Length]!;
                if (batch.State == BatchState.FirstStage && batch.CallsDone)
                {
                    batch.TakeCalls(cancellationToken);
                    _shared.Gatherer.EmitDeferredRecursion(batch.Log);
                    batch.State = BatchState.SecondStage;
                    TraceSecondStage(batch, cancellationToken);
                    moved = true;
                }

                if (batch.State == BatchState.SecondStage && batch.CallsDone)
                {
                    batch.TakeCalls(cancellationToken);
                    batch.State = BatchState.Answered;
                    moved = true;
                }
            }

            return moved;
        }

        // Fills the next ring slot and traces its first stage; false when
        // nothing could be emitted (every face held waits on a batch in
        // flight, and none is left to claim), leaving the slot free.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Launch(CancellationToken cancellationToken)
        {
            int slot = (_head + _count) % _ring.Length;
            FacelightBatch batch = _ring[slot] ??= new FacelightBatch(new LightRayLog
            {
                StockRays = _stockRays,
                DeferRecursion = true,
                PadCalls = true,
                Pool = _pool,
            });

            if (!Fill(batch))
            {
                return false;
            }

            _count++;
            PeakInFlight = Math.Max(PeakInFlight, _count);
            batch.State = BatchState.FirstStage;
            TraceFirstStage(batch, cancellationToken);
            return true;
        }

        // Emits into a fresh batch: the faces already held first, then newly
        // claimed ones. False when there is nothing to emit.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Fill(FacelightBatch batch)
        {
            LightRayLog rays = batch.Log;
            rays.Reset();
            batch.Items.Clear();

            foreach (FaceLightJob job in _active)
            {
                if (Full(rays))
                {
                    break;
                }

                Add(batch, job);
            }

            while (!Full(rays) && !_exhausted)
            {
                if (_shared.Claim() is not FaceLightJob claimed)
                {
                    _exhausted = true;
                    break;
                }

                if (claimed.Done)
                {
                    continue;
                }

                _active.Add(claimed);
                Add(batch, claimed);
            }

            return batch.Items.Count > 0;
        }

        private bool Full(LightRayLog rays) =>
            rays.TotalCount >= _shared.BatchRays || rays.Tape.Length >= TapeWordsPerBatch;

        // A face waiting on the end of a round that is still in flight emits
        // nothing and is left out of the batch.
        private void Add(FacelightBatch batch, FaceLightJob job)
        {
            int items = job.EmitItems(batch.Log, _shared.BatchRays, TapeWordsPerBatch);
            if (items > 0)
            {
                batch.Items.Add((job, items));
            }
        }

        private void TraceFirstStage(FacelightBatch batch, CancellationToken cancellationToken)
        {
            LightRayLog rays = batch.Log;
            Batches++;
            VisibilityRays += rays.VisibilityCount;
            SkyRays += rays.SkyCount;
            (Memory<ulong> bits, Memory<HitId> hits) = rays.FirstStageAnswers();

            // The tracer answers for the SEGMENT (IRayTracer.TraceVisibilityAsync),
            // which is TestLine's HitDistance < len.
            if (rays.VisibilityCount > 0)
            {
                Keep(batch, _shared.Tracer.TraceVisibilityAsync(
                    rays.VisibilityMemory, bits, RayTraceOptions.StockExact, cancellationToken));
            }

            if (rays.SkyCount > 0)
            {
                Keep(batch, _shared.Tracer.TraceClosestAsync(
                    rays.SkyMemory, hits, RayTraceOptions.StockExact, cancellationToken));
            }
        }

        private void TraceSecondStage(FacelightBatch batch, CancellationToken cancellationToken)
        {
            LightRayLog rays = batch.Log;
            if (rays.Sky2Count == 0)
            {
                return;
            }

            SkyRays += rays.Sky2Count;
            Keep(batch, _shared.Tracer.TraceClosestAsync(
                rays.Sky2Memory, rays.SecondStageAnswers(), RayTraceOptions.StockExact, cancellationToken));
        }

        // A CPU tracer finishes inside the call; an asynchronous one returns
        // an incomplete task, kept on its batch and, when it finishes, flags
        // the pipeline and wakes a worker to move it.
        private void Keep(FacelightBatch batch, ValueTask task)
        {
            if (task.IsCompletedSuccessfully)
            {
                return;
            }

            Task call = task.AsTask();
            batch.Calls.Add(call);
            if (!call.IsCompleted)
            {
                call.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_answered);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Resolve(FacelightBatch batch)
        {
            LightRayLog rays = batch.Log;
            rays.BeginResolve();
            foreach ((FaceLightJob job, int items) in batch.Items)
            {
                job.ResolveItems(rays, items);
            }

            if (!rays.ReplayComplete)
            {
                throw new InvalidOperationException(
                    "the face lighting resolved a different set of rays than it emitted");
            }

            batch.Items.Clear();
            batch.State = BatchState.Free;
            _active.RemoveAll(static j => j.Done);
        }

        /// <summary>
        /// Returns every batch's rented storage. Only once no call of any
        /// batch can still be running -- the driver awaits
        /// <see cref="InFlightCalls"/> first.
        /// </summary>
        public void Dispose()
        {
            foreach (FacelightBatch? batch in _ring)
            {
                batch?.Dispose();
            }
        }
    }
}
