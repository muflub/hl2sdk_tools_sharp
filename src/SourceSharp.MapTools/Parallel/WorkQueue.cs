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
/// One run at a time per queue. A second overlapping run throws rather than
/// interleaving, because two stages sharing per-worker scratch would corrupt
/// it.
/// </para>
/// <para>
/// <see cref="Dispose"/> stops and joins the threads it owns. A queue that is
/// never disposed keeps them parked for the life of the process.
/// </para>
/// </remarks>
public sealed class WorkQueue : IDisposable
{
    private readonly CompileParallelism _parallelism;
    private readonly int _degree;
    private readonly object _sync = new();
    // ONE SIGNAL PER WORKER, not one counting semaphore released N times.
    //
    // A single semaphore released `_degree` times hands out the right NUMBER of
    // permits and says nothing about WHO takes them: a worker that finishes its
    // share, loops round and waits again can take a second permit before a
    // slower thread has taken its first. That was not theoretical -- the fact
    // that every worker sees its own index caught indices [0,1,3,4,4], with
    // worker 4 running twice and worker 2 not at all.
    //
    // Work items were still claimed exactly once (that is Interlocked on the
    // claim counter, and independent of this), so nothing computed a wrong
    // answer. What broke is the contract this type advertises: per-worker
    // scratch arenas indexed by worker number. Two runs writing slot 4 and none
    // writing slot 2 is exactly the shape that corrupts a stage which trusts
    // the index to be a partition.
    private readonly List<SemaphoreSlim> _workerSignals = [];
    private readonly List<Thread> _threads = [];

    private volatile Job? _currentJob;
    private volatile bool _shutdown;
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
                int live = 0;
                foreach (Thread thread in _threads)
                {
                    if (thread.IsAlive)
                    {
                        live++;
                    }
                }

                return live;
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

    /// <summary>Stops the queue's threads and waits for them.</summary>
    /// <remarks>
    /// Safe to call twice. A run still in flight is NOT cancelled by this —
    /// cancel it with its token first, or this waits for it to finish.
    /// </remarks>
    public void Dispose()
    {
        Thread[] threads;
        SemaphoreSlim[] signals;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown = true;
            threads = [.. _threads];
            signals = [.. _workerSignals];
        }

        // Every worker gets its OWN release so each parked loop wakes, sees the
        // shutdown flag and returns. Outside the lock, because a woken worker
        // reads _threads through LiveWorkerCount.
        foreach (SemaphoreSlim signal in signals)
        {
            signal.Release();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        foreach (SemaphoreSlim signal in signals)
        {
            signal.Dispose();
        }
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

            job.Begin(_degree);
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

        TaskScheduler? scheduler = _parallelism.Scheduler;
        if (scheduler is not null)
        {
            // The host lends its scheduler, so one task per worker per run.
            // NOT a persistent loop: a scheduler with a concurrency limit below
            // the degree would never start the later loops, and the run would
            // wait forever on workers that are queued behind each other.
            _currentJob = job;
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

        EnsureThreads();
        _currentJob = job;
        for (int i = 0; i < _degree; i++)
        {
            _workerSignals[i].Release();
        }
    }

    private void EnsureThreads()
    {
        lock (_sync)
        {
            if (_threads.Count == _degree)
            {
                return;
            }

            int first = _threads.Count;

            // TWO PHASES, AND THE ORDER IS THE WHOLE POINT.
            //
            // Every signal must exist before any worker can index the list,
            // because WorkerLoop reads `_workerSignals[workerIndex]` WITHOUT
            // taking `_sync` -- it cannot take it, since it then parks. The
            // first version of this started each thread inside the same loop
            // that was still `Add`ing, so a running worker could index the list
            // while `List<T>.Add` reallocated its backing array underneath it.
            //
            // This is a real race and the fix is correct, but BE CLEAR ABOUT
            // WHAT IT DID NOT FIX. `ssmap vvis` on `l3_arena_144_pillars` dies
            // with `Internal CLR error (0x80131506)` in roughly one run in
            // eight, and it still does after this change: 8/50 before, 6/50
            // after, which at that rate is the same number. The List-growth
            // race was diagnosed from reading the code, looked sufficient, and
            // was not. The probe is what said so.
            //
            // Three further things the probe established, each of which
            // narrows the search and none of which is the answer:
            //   - it is NOT Server GC (workstation GC still fails 2/25);
            //   - it is NOT the degree -- `ssmap vvis` REPORTS that `-threads`
            //     is accepted and ignored, so every "thread count" probe ran at
            //     the same default degree and the earlier t1/t4 "clean" results
            //     compared nothing;
            //   - it dies in SETUP, right after the portal counts print and
            //     entering the first parallel stage, on a 352-cluster map, so
            //     it is not a large allocation.
            // It produces no output at all, so it can never produce a WRONG
            // answer -- only no answer.
            //
            // After this, nothing mutates `_workerSignals` while a worker can
            // read it: the list is complete before the first `Start()`, and
            // EnsureThreads returns early on every later call.
            for (int i = first; i < _degree; i++)
            {
                _workerSignals.Add(new SemaphoreSlim(0));
            }

            for (int i = first; i < _degree; i++)
            {
                int workerIndex = i;
                var thread = new Thread(() => WorkerLoop(workerIndex))
                {
                    IsBackground = true,
                    Name = $"ssmap-work-{workerIndex}",
                };
                _threads.Add(thread);
                thread.Start();
            }
        }
    }

    private void WorkerLoop(int workerIndex)
    {
        while (true)
        {
            _workerSignals[workerIndex].Wait();

            if (_shutdown)
            {
                return;
            }

            Job? job = _currentJob;
            if (job is not null)
            {
                RunJob(job, workerIndex);
            }
        }
    }

    private static void RunJob(Job job, int workerIndex)
    {
        var context = new WorkerContext(workerIndex, job.PollInterval, job.Token);
        object? scratch = null;

        try
        {
            scratch = job.CreateScratch(workerIndex);

            while (!job.Token.IsCancellationRequested)
            {
                int chunk = job.NextChunkSize();
                int start = Interlocked.Add(ref job.Claimed, chunk) - chunk;
                if (start >= job.ItemCount)
                {
                    break;
                }

                int end = Math.Min(start + chunk, job.ItemCount);
                for (int slot = start; slot < end; slot++)
                {
                    if (job.Token.IsCancellationRequested)
                    {
                        break;
                    }

                    job.Execute(job.Order[slot], scratch, context);

                    // Reported here, holding nothing. This is the line stock
                    // has inside its critical section.
                    IProgress<CompileProgress>? progress = job.Progress;
                    if (progress is not null)
                    {
                        long done = Interlocked.Increment(ref job.Done);
                        progress.Report(new CompileProgress(job.Stage, done, job.ItemCount));
                    }
                }
            }
        }
        catch (OperationCanceledException)
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

    private static int[] BuildOrder(int itemCount, Func<int, long>? cost)
    {
        if (cost is null)
        {
            int[] identity = new int[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                identity[i] = i;
            }

            return identity;
        }

        long[] costs = new long[itemCount];
        for (int i = 0; i < itemCount; i++)
        {
            costs[i] = cost(i);
        }

        // OrderByDescending is a stable sort, so equal costs keep ascending
        // index order and the claim order is a pure function of the costs.
        return [.. Enumerable.Range(0, itemCount).OrderByDescending(i => costs[i])];
    }

    private abstract class Job
    {
        private readonly CancellationTokenSource _cts = new();
        private CancellationTokenRegistration _registration;
        private Exception? _error;
        private int _remaining;

        public int Claimed;
        public long Done;

        public int ItemCount { get; init; }

        public int[] Order { get; init; } = [];

        public int ChunkSize { get; init; }

        public string Stage { get; init; } = string.Empty;

        public IProgress<CompileProgress>? Progress { get; init; }

        public int PollInterval { get; init; }

        public int Degree { get; init; }

        public CancellationToken CallerToken { get; init; }

        public Action? Finished { get; set; }

        public CancellationToken Token => _cts.Token;

        public void Begin(int degree)
        {
            _remaining = degree;
            _registration = CallerToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _cts);
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

        public void Stop() => _cts.Cancel();

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

            _registration.Dispose();
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
