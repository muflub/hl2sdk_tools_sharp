//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Parallel;

/// <summary>
/// Runs a compile stage's work items across the whole machine: the replacement
/// For.
/// </summary>
/// <remarks>
/// <para>
/// Stock's runner cannot use a modern machine, and that is measured rather than
/// asserted. <c>MAX_TOOL_THREADS</c> is 16
/// and <c>RunThreads_Start</c> clamps to it.
/// <c>ThreadSetDefault</c> is worse: it reads the processor count and then
/// <c>if (numthreads &lt; 1 || numthreads &gt; 32) numthreads = 1</c>
/// — so a 64-thread workstation runs the compile on
/// ONE thread. And every single work item is handed out under one global
/// <c>CRITICAL_SECTION</c> with the progress pacifier called inside it
/// Which the winding allocator then contends with
/// because it takes the same lock.
/// </para>
/// <para>
/// This replaces all of that: dedicated threads with no cap, lock-free claiming
/// through <see cref="Interlocked"/>, longest-job-first dynamic partitioning,
/// a scratch object per worker, and progress reported outside any lock.
/// </para>
/// <para>
/// <b>Dedicated threads, never the thread pool, and never
/// <see cref="Task.Run(Action)"/>.</b> A compile saturates every core it is
/// given for minutes. Parked on an ASP.NET service's pool that is
/// indistinguishable from an outage, and pool starvation would also deadlock
/// the host's own continuations. When a host would rather lend its own
/// scheduler it sets <see cref="CompileParallelism.Scheduler"/> and that is
/// honoured exactly.
/// </para>
/// <para>
/// The threads are a <see cref="CompilePool"/>'s: the one
/// <see cref="CompileParallelism.Pool"/> names, shared with every other queue
/// of the compile, or else one this queue creates and owns.
/// </para>
/// <para>
/// One run at a time per queue. A second overlapping run throws rather than
/// interleaving, because two stages sharing per-worker scratch would corrupt
/// it.
/// </para>
/// <para>
/// <see cref="Dispose"/> stops and joins the threads it owns (never a shared pool's). A queue that is
/// never disposed keeps them parked for the life of the process.
/// </para>
/// </remarks>
public sealed class WorkQueue : IDisposable
{
    private readonly CompileParallelism _parallelism;
    private readonly int _degree;
    private readonly object _sync = new();
    // The pool the runs go to: the host's (CompileParallelism.Pool), or one
    // this queue creates on its first run and owns.
    //
    // Worker indices are the pool's job slots, not threads: a slot is lent to
    // one thread for one chunk and every slot's scratch is built exactly once,
    // so the indices are a partition by construction.
    private CompilePool? _ownPool;

    private volatile bool _disposed;
    private int _busy;

    /// <summary>Creates a queue with the default parallelism.</summary>
    public WorkQueue()
        : this(CompileParallelism.Default)
    {
    }

    /// <summary>Creates a queue.</summary>
    /// <param name="parallelism">
    /// How much of the machine to use, and whose threads to use.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="parallelism"/> is null.
    /// </exception>
    public WorkQueue(CompileParallelism parallelism)
    {
        ArgumentNullException.ThrowIfNull(parallelism);
        _parallelism = parallelism;
        _degree = Math.Max(1, parallelism.MaxDegree);
        if (parallelism.Pool is { } pool)
        {
            // A shared pool caps every job at its own size.
            _degree = Math.Min(_degree, pool.Degree);
        }
    }

    /// <summary>How many workers a run uses.</summary>
    /// <remarks>
    /// <see cref="CompileParallelism.MaxDegree"/>, floored at one. There is no
    /// ceiling: stock's is 16, and the comment on
    /// <see cref="CompileParallelism"/> explains why that one is not copied.
    /// </remarks>
    public int Degree => _degree;

    /// <summary>
    /// How many of the threads this queue created are still running.
    /// </summary>
    /// <remarks>
    /// Zero before the first run, and zero again after <see cref="Dispose"/>
    /// returns — which is the whole point of exposing it: "the queue does not
    /// leak threads" is then a fact a test can assert about the threads this
    /// object actually created, rather than a guess from the process's total
    /// thread count, which the GC and tiered compilation both move around.
    /// Always zero when a <see cref="CompileParallelism.Scheduler"/> is in use,
    /// because then the queue creates no threads at all.
    /// </remarks>
    public int LiveWorkerCount
    {
        get
        {
            lock (_sync)
            {
                return _ownPool?.LiveThreadCount ?? 0;
            }
        }
    }

    /// <summary>Runs a body over every index from zero to a count.</summary>
    /// <param name="itemCount">How many items there are.</param>
    /// <param name="body">What to do with one item.</param>
    /// <param name="options">Scheduling and reporting options, or null.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>A task that completes when every item has run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="itemCount"/> is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A run is already in progress, or the queue has been disposed.
    /// </exception>
    /// <remarks>
    /// The equivalent of <c>RunThreadsOnIndividual</c>
    /// For a stage whose items write into
    /// storage the caller already owns and indexes itself. When the per-item
    /// result is a value, use
    /// <see cref="RunAsync{TScratch, TResult}"/> instead and get the
    /// deterministic merge for free.
    /// </remarks>
    public Task RunAsync(
        int itemCount,
        Action<int, WorkerContext> body,
        WorkQueueOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return RunCoreAsync<object?, bool>(
            itemCount,
            (index, _, context) =>
            {
                body(index, context);
                return false;
            },
            static _ => null,
            options,
            keepResults: false,
            cancellationToken);
    }

    /// <summary>
    /// Runs a body over every index, giving each worker its own scratch and
    /// collecting a result per item.
    /// </summary>
    /// <typeparam name="TScratch">
    /// The per-worker scratch type, for example a
    /// <see cref="Geometry.WindingArena"/>. Disposed after the run if it
    /// implements <see cref="IDisposable"/>.
    /// </typeparam>
    /// <typeparam name="TResult">What one item produces.</typeparam>
    /// <param name="itemCount">How many items there are.</param>
    /// <param name="body">What to do with one item.</param>
    /// <param name="scratchFactory">
    /// Builds a worker's scratch, called once per worker ON that worker.
    /// </param>
    /// <param name="options">Scheduling and reporting options, or null.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>
    /// The results, indexed by ITEM index — not by completion order.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="body"/> or <paramref name="scratchFactory"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="itemCount"/> is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A run is already in progress, or the queue has been disposed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The merge order is the item index, whatever order the items finish
    /// in.</b> That is the whole reason results come back as an array rather
    /// than through a callback: a stage that appended to a shared list as
    /// items completed would produce a different file every run, and the
    /// difference would only show up as a byte-comparison failure on a big map
    /// after a hundred identical small ones.
    /// </para>
    /// <para>
    /// <b>Scratch is per worker, not per item.</b> Creating a
    /// <see cref="Geometry.WindingArena"/> per portal would defeat the arena;
    /// creating one and sharing it would be stock's global lock again. It is
    /// built on the worker thread so a scratch that is thread-affine — anything
    /// holding a native handle, for instance — is created where it will be
    /// used.
    /// </para>
    /// </remarks>
    public Task<TResult[]> RunAsync<TScratch, TResult>(
        int itemCount,
        Func<int, TScratch, WorkerContext, TResult> body,
        Func<int, TScratch> scratchFactory,
        WorkQueueOptions? options,
        CancellationToken cancellationToken) =>
        RunCoreAsync(itemCount, body, scratchFactory, options, keepResults: true, cancellationToken);

    /// <summary>Stops the threads this queue created and waits for them.</summary>
    /// <remarks>
    /// <para>
    /// Safe to call twice. What happens to a run still in flight depends on
    /// whose threads it is on:
    /// </para>
    /// <list type="bullet">
    /// <item>On the pool this queue created (no
    /// <see cref="CompileParallelism.Pool"/> and no
    /// <see cref="CompileParallelism.Scheduler"/>), the pool is disposed: each
    /// thread stops once the chunk it is running ends, the threads are
    /// joined, and the run is then failed with an
    /// <see cref="ObjectDisposedException"/>, its remaining items not run.
    /// The body is not interrupted mid-chunk, so this waits for the chunks
    /// already started, not for the run.</item>
    /// <item>On a host's pool or scheduler, nothing here touches the run: the
    /// threads are the host's, and the run goes on to completion (or to its
    /// token).</item>
    /// </list>
    /// <para>
    /// To stop a run cleanly in either case, cancel its token and await it
    /// before disposing.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        CompilePool? own;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            own = _ownPool;
        }

        // Only the pool this queue created: a host's pool outlives its queues.
        own?.Dispose();
    }

    private Task<TResult[]> RunCoreAsync<TScratch, TResult>(
        int itemCount,
        Func<int, TScratch, WorkerContext, TResult> body,
        Func<int, TScratch> scratchFactory,
        WorkQueueOptions? options,
        bool keepResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(scratchFactory);
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A token that is already cancelled does NO work: not one item, and
        // not one scratch object. Checked before the busy flag is taken so a
        // cancelled call cannot lock the queue out either.
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TResult[]>(cancellationToken);
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "This WorkQueue is already running a stage. Await it before starting another.");
        }

        try
        {
            if (itemCount == 0)
            {
                Interlocked.Exchange(ref _busy, 0);
                return Task.FromResult(Array.Empty<TResult>());
            }

            options ??= new WorkQueueOptions();

            var job = new Job<TScratch, TResult>
            {
                ItemCount = itemCount,
                Order = BuildOrder(itemCount, options.ItemCost),
                ChunkSize = ResolveChunkSize(options),
                Stage = options.Stage,
                Progress = options.Progress,
                PollInterval = _parallelism.CancellationPollInterval,
                Degree = _degree,
                CallerToken = cancellationToken,
                Body = body,
                ScratchFactory = scratchFactory,
                Results = keepResults ? new TResult[itemCount] : [],
                KeepResults = keepResults,
            };

            Dispatch(job);
            return job.Completion.Task;
        }
        catch
        {
            Interlocked.Exchange(ref _busy, 0);
            throw;
        }
    }

    private void Dispatch(Job job)
    {
        job.Finished = () => Interlocked.Exchange(ref _busy, 0);

        // A shared pool wins over a lent scheduler: the pool is the one that
        // can interleave stages chunk by chunk.
        TaskScheduler? scheduler = _parallelism.Pool is null ? _parallelism.Scheduler : null;
        if (scheduler is not null)
        {
            job.Begin(_degree);
            // The host lends its scheduler, so one task per worker per run.
            // NOT a persistent loop: a scheduler with a concurrency limit below
            // the degree would never start the later loops, and the run would
            // wait forever on workers that are queued behind each other.
            for (int i = 0; i < _degree; i++)
            {
                int workerIndex = i;
                _ = Task.Factory.StartNew(
                    () => RunJob(job, workerIndex),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                    scheduler);
            }

            return;
        }

        CompilePool pool = _parallelism.Pool ?? OwnPool();
        job.BeginPooled(pool);
        pool.Submit(job);
    }

    private CompilePool OwnPool()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _ownPool ??= new CompilePool(_degree);
        }
    }

    private static void RunJob(Job job, int workerIndex)
    {
        var context = new WorkerContext(workerIndex, job.PollInterval, job.Token);
        object? scratch = null;

        try
        {
            scratch = job.CreateScratch(workerIndex);
            while (job.RunChunk(scratch, context))
            {
            }
        }
        catch (OperationCanceledException) when (job.CancelWasAskedFor)
        {
            job.Stop();
        }
        catch (Exception ex)
        {
            job.Fail(ex);
        }
        finally
        {
            if (scratch is IDisposable disposable)
            {
                disposable.Dispose();
            }

            job.WorkerDone();
        }
    }

    private static int ResolveChunkSize(WorkQueueOptions options)
    {
        if (options.ChunkSize > 0)
        {
            return options.ChunkSize;
        }

        // With costs, one at a time: claiming a block would hand one worker
        // several of the most expensive items and put the tail back.
        return options.ItemCost is not null ? 1 : 0;
    }

    /// <summary>The order items are claimed in, or null for index order.</summary>
    /// <remarks>
    /// <para>
    /// <b>No costs means no array.</b> Without costs the claim order is the
    /// index order, and the only reader maps a claim position to an item, so
    /// the identity is applied in place (<see cref="Job.ItemAt"/>) rather than
    /// materialised: a stage over millions of items would otherwise allocate
    /// a four-byte-per-item table on every run only to read <c>order[i] == i</c>
    /// back out of it.
    /// </para>
    /// <para>
    /// <b>With costs, one sort in place.</b> The order is most expensive
    /// first, ties in ascending index order, which is exactly what a stable
    /// descending sort by cost gives. <see cref="Array.Sort{T}(T[], Comparison{T})"/>
    /// is not stable, so the comparison itself breaks ties on the index: with
    /// a total order stability no longer matters and the result is identical
    /// to the stable sort item for item. A LINQ <c>OrderByDescending</c> gave
    /// the same order but copied the indices into a buffer, built a key array
    /// and an index map beside it and then copied the result out again.
    /// </para>
    /// </remarks>
    internal static int[]? BuildOrder(int itemCount, Func<int, long>? cost)
    {
        if (cost is null)
        {
            return null;
        }

        long[] costs = new long[itemCount];
        int[] order = new int[itemCount];
        for (int i = 0; i < itemCount; i++)
        {
            costs[i] = cost(i);
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            int byCost = costs[b].CompareTo(costs[a]);
            return byCost != 0 ? byCost : a.CompareTo(b);
        });
        return order;
    }

    /// <summary>Whether a pooled step can decline without entering its job.</summary>
    /// <remarks>
    /// True for a job that is not stopping, has every item claimed and every
    /// slot built, and still has a thread inside: nothing can be handed out,
    /// and the thread inside finishes the job itself. A stopping job is
    /// entered instead, because a stopped job with nobody inside must be
    /// finished by whichever thread looks next.
    /// </remarks>
    internal static bool IsDrainingWithoutMe(bool stopping, bool exhausted, int unbuiltLeft, int inFlight) =>
        !stopping && exhausted && unbuiltLeft == 0 && inFlight > 0;

    /// <summary>
    /// Whether a slot handed back to a pooled job should wake an idle pool
    /// thread; a wake answers (and uncounts) one turned-away scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only when a thread was turned away from the job because it was full,
    /// no earlier slot has answered it, and items are still left. A run-out or
    /// stopped job has nothing a woken thread could take (its last thread out
    /// finishes it). Waking on every slot regardless was the pool's worst
    /// herd: at a job's tail every chunk is one item, and each one sent every
    /// idle thread through the pool's lock and then the job's, to be told no.
    /// </para>
    /// <para>
    /// A count rather than a flag: two threads turned away between two returns
    /// are owed two wakes, or the second slot would sit free while its thread
    /// slept out the pool's timeout. A count can overshoot (a thread turned
    /// away that then found other work still holds its entry), and that costs
    /// one spare wake, never a lost one.
    /// </para>
    /// <para>
    /// The count is decremented with a compare-exchange, never below zero,
    /// because returns run concurrently with no lock (see
    /// <see cref="JobSlots"/>): two returns answering one entry would owe
    /// a wake that nobody was turned away for, and the count, once negative,
    /// would swallow the next real entry.
    /// </para>
    /// </remarks>
    /// <param name="finishing">This return finishes the job.</param>
    /// <param name="stopping">The job is cancelled or faulted.</param>
    /// <param name="exhausted">Every item has been claimed.</param>
    /// <param name="turnedAway">Unanswered turned-away scans; decremented on a wake.</param>
    internal static bool TakeWakeForFreedSlot(bool finishing, bool stopping, bool exhausted, ref int turnedAway)
    {
        if (finishing || stopping || exhausted)
        {
            return false;
        }

        int count = Volatile.Read(ref turnedAway);
        while (count > 0)
        {
            int seen = Interlocked.CompareExchange(ref turnedAway, count - 1, count);
            if (seen == count)
            {
                return true;
            }

            count = seen;
        }

        return false;
    }

    private abstract class Job : CompilePool.PoolJob
    {
        private readonly CancellationTokenSource _cts = new();
        private CancellationTokenRegistration _registration;
        private Exception? _error;
        private int _remaining;

        // The pooled run's slots: worker indices 0..Degree-1, each lent to one
        // thread for one chunk, handed out with no lock (see JobSlots). The
        // scheduler path never uses them; its placeholder is never finished,
        // which is right because that path's job is never in a pool.
        private JobSlots _slots = new(1);
        private object?[] _scratch = [];
        private WorkerContext?[] _contexts = [];
        private CompilePool? _pool;

        public int Claimed;
        public long Done;

        public int ItemCount { get; init; }

        /// <summary>The claim order, or null for index order (see <see cref="BuildOrder"/>).</summary>
        public int[]? Order { get; init; }

        public int ChunkSize { get; init; }

        public string Stage { get; init; } = string.Empty;

        public IProgress<CompileProgress>? Progress { get; init; }

        public int PollInterval { get; init; }

        public int Degree { get; init; }

        public CancellationToken CallerToken { get; init; }

        public Action? Finished { get; set; }

        public CancellationToken Token => _cts.Token;

        public override bool IsFinished => _slots.IsFinished;

        // The scheduler path: one worker loop per index, counted down.
        public void Begin(int degree)
        {
            _remaining = degree;
            _registration = CallerToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _cts);
        }

        // The pool path: every slot starts unbuilt, handed out lowest first so
        // the first thread in gets index 0.
        public void BeginPooled(CompilePool pool)
        {
            _pool = pool;
            _scratch = new object?[Degree];
            _contexts = new WorkerContext?[Degree];
            _slots = new JobSlots(Degree);

            // A cancel with no thread inside the job still has to finish it,
            // so it wakes the pool.
            _registration = CallerToken.Register(
                static state =>
                {
                    Job job = (Job)state!;
                    job._cts.Cancel();
                    job._pool?.Signal();
                },
                this);
        }

        public int NextChunkSize()
        {
            if (ChunkSize > 0)
            {
                return ChunkSize;
            }

            // Guided: big bites while there is plenty left, down to one at the
            // end so the run does not finish with a single worker holding a
            // block and the rest idle.
            int remaining = ItemCount - Volatile.Read(ref Claimed);
            if (remaining <= 0)
            {
                return 1;
            }

            return Math.Clamp(remaining / (Degree * 4), 1, 64);
        }

        /// <summary>The item claimed at a position of the claim order.</summary>
        public int ItemAt(int position) => Order is { } order ? order[position] : position;

        public void Stop() => _cts.Cancel();

        /// <summary>
        /// Whether an <see cref="OperationCanceledException"/> out of a body
        /// is this run being stopped, rather than a failure of the body's own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// True once the caller's token or the run's own (a fault elsewhere,
        /// or the pool going away) has been cancelled. Only then is the
        /// exception the answer to a stop somebody asked for. A body can also
        /// throw one for reasons of its own: a timeout inside a library it
        /// calls, or a token it made itself. Taken as a stop, that ended the
        /// run with no error recorded and the caller's token untouched, so
        /// <see cref="Complete"/> reported success with every item after it
        /// missing from the results. It is a failure of the body like any
        /// other exception, and faults the run with itself.
        /// </para>
        /// <para>
        /// The caller's token is read as well as the run's because a body
        /// that watches the caller's token directly can see it cancelled
        /// before the registration that forwards it has cancelled the run's.
        /// </para>
        /// </remarks>
        public bool CancelWasAskedFor => _cts.IsCancellationRequested || CallerToken.IsCancellationRequested;

        public void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref _error, error, null);
            _cts.Cancel();
        }

        public void WorkerDone()
        {
            if (Interlocked.Decrement(ref _remaining) != 0)
            {
                return;
            }

            Complete();
        }

        public override bool Step()
        {
            // The tail of a job, declined without entering it: every item
            // claimed, every slot built, and a thread still inside. Nothing
            // here can be handed out, and the thread inside finishes the job
            // itself, so the answer is no. Every value read only ever moves
            // one way (Claimed up, unbuilt slots down) except the count inside,
            // and a stale non-zero count means the last thread out has already
            // seen the same run-out job and finished it. Without this, every
            // idle thread's scan would enter and leave the job to be told no,
            // and each entry is a write to the job's one shared state word.
            if (IsDrainingWithoutMe(
                    _cts.IsCancellationRequested,
                    Volatile.Read(ref Claimed) >= ItemCount,
                    _slots.UnbuiltLeft,
                    _slots.InFlight))
            {
                return false;
            }

            // Entered before a slot is looked for, so a thread holding a slot
            // is always counted and the job cannot finish under it.
            if (!_slots.TryEnter())
            {
                return false;
            }

            int slot = -1;
            bool build = false;
            if (!_cts.IsCancellationRequested)
            {
                // Unbuilt slots first, even when the items have run out:
                // every slot's scratch is built once, on a pool thread,
                // whether or not items remain for it.
                slot = _slots.TryTakeUnbuilt();
                build = slot >= 0;
                if (slot < 0 && Volatile.Read(ref Claimed) < ItemCount)
                {
                    slot = _slots.TryTakeFree();
                    if (slot < 0)
                    {
                        // Full: items are left but every slot is lent out.
                        // Counted so a slot coming back wakes someone for it,
                        // then looked at once more in case one came back
                        // before the count went up (JobSlots.TurnAway).
                        _slots.TurnAway();
                        slot = _slots.TryTakeFree();
                    }
                }
            }

            if (slot < 0)
            {
                // Nothing to hand out. A job this thread was last out of, that
                // has run out (or been stopped), finishes here, on this thread.
                // The state is read after the leave (JobSlots.Leave says why).
                if (_slots.Leave() && IsDone() && _slots.TryFinishIdle())
                {
                    Complete();
                    return true;
                }

                return false;
            }

            try
            {
                if (build)
                {
                    _scratch[slot] = CreateScratch(slot);
                    _contexts[slot] = new WorkerContext(slot, PollInterval, Token);
                }

                RunChunk(_scratch[slot], _contexts[slot]!);
            }
            catch (OperationCanceledException) when (CancelWasAskedFor)
            {
                Stop();
            }
            catch (Exception ex)
            {
                Fail(ex);
            }

            // Back before the leave, so the slot is never free while its
            // borrower is uncounted, and so the job's last thread out finds
            // every slot home when it disposes their scratch. The state that
            // decides the finish is read after the leave (JobSlots.Leave).
            _slots.Return(slot);
            bool last = _slots.Leave();
            bool stopping = _cts.IsCancellationRequested;
            bool exhausted = Volatile.Read(ref Claimed) >= ItemCount;
            bool finish = last
                && (stopping || (exhausted && _slots.UnbuiltLeft == 0))
                && _slots.TryFinishIdle();
            bool wake = _slots.TakeWake(finish, stopping, exhausted);

            if (finish)
            {
                Complete();
            }
            else if (wake)
            {
                // A slot came free and a thread found this job full: that
                // thread (or any idle one) may take it.
                _pool?.Signal();
            }

            return true;
        }

        // Stopped, or run out with every slot taken to build: nothing is left
        // for a thread to do, so the last one out finishes the job.
        private bool IsDone() =>
            _cts.IsCancellationRequested
            || (Volatile.Read(ref Claimed) >= ItemCount && _slots.UnbuiltLeft == 0);

        // One claim, with the per-item checks; false when nothing was claimed.
        public bool RunChunk(object? scratch, WorkerContext context)
        {
            if (Token.IsCancellationRequested)
            {
                return false;
            }

            int chunk = NextChunkSize();
            int start = Interlocked.Add(ref Claimed, chunk) - chunk;
            if (start >= ItemCount)
            {
                return false;
            }

            int end = Math.Min(start + chunk, ItemCount);
            for (int slot = start; slot < end; slot++)
            {
                if (Token.IsCancellationRequested)
                {
                    break;
                }

                Execute(ItemAt(slot), scratch, context);

                // Reported here, holding nothing. This is the line stock
                // has inside its critical section.
                IProgress<CompileProgress>? progress = Progress;
                if (progress is not null)
                {
                    long done = Interlocked.Increment(ref Done);
                    progress.Report(new CompileProgress(Stage, done, ItemCount));
                }
            }

            return true;
        }

        // The pool was disposed with this job still in it: its threads are
        // gone, so the job ends here rather than leaving its caller waiting.
        public override void Abandon()
        {
            // The pool's threads are joined, so nobody is left inside to
            // finish it; ForceFinish still makes this the only finisher.
            if (!_slots.ForceFinish())
            {
                return;
            }

            Fail(new ObjectDisposedException(nameof(CompilePool)));
            Complete();
        }

        private void Complete()
        {
            _registration.Dispose();

            // The pooled run's scratch, all of it, before the result: the
            // scheduler path disposes each worker's as that worker ends.
            foreach (object? scratch in _scratch)
            {
                if (scratch is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _scratch = [];
            Exception? error = Volatile.Read(ref _error);
            Finished?.Invoke();

            if (error is not null)
            {
                SetException(error);
            }
            else if (CallerToken.IsCancellationRequested)
            {
                SetCanceled(CallerToken);
            }
            else
            {
                SetResult();
            }

            _cts.Dispose();
        }

        public abstract object? CreateScratch(int workerIndex);

        public abstract void Execute(int index, object? scratch, WorkerContext context);

        protected abstract void SetResult();

        protected abstract void SetException(Exception error);

        protected abstract void SetCanceled(CancellationToken token);
    }

    private sealed class Job<TScratch, TResult> : Job
    {
        // RunContinuationsAsynchronously: without it the caller's continuation
        // would run ON the worker thread that happened to finish last, which
        // would mean an await after a compile stage silently executing the
        // host's next code on a thread this queue owns and is about to reuse.
        public TaskCompletionSource<TResult[]> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<int, TScratch, WorkerContext, TResult> Body { get; init; } = default!;

        public Func<int, TScratch> ScratchFactory { get; init; } = default!;

        public TResult[] Results { get; init; } = [];

        public bool KeepResults { get; init; }

        public override object? CreateScratch(int workerIndex) => ScratchFactory(workerIndex);

        public override void Execute(int index, object? scratch, WorkerContext context)
        {
            TResult result = Body(index, (TScratch)scratch!, context);
            if (KeepResults)
            {
                // Written to the ITEM's slot, so the merge order is the item
                // order however the run was scheduled.
                Results[index] = result;
            }
        }

        protected override void SetResult() => Completion.TrySetResult(Results);

        protected override void SetException(Exception error) => Completion.TrySetException(error);

        protected override void SetCanceled(CancellationToken token) =>
            Completion.TrySetCanceled(token);
    }
}
