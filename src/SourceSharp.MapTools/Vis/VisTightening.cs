//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;
using System.Diagnostics;

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// <c>-tighten</c>: prune with finished neighbours'
/// <c>portalvis</c>, as stock does, but decide WHICH neighbours from the map
/// alone, so the answer is the same at every thread count.
/// </summary>
/// <remarks>
/// <para>
/// <b>What stock does, and why the port stopped doing it.</b> A candidate
/// portal is intersected with its FINISHED <c>portalvis</c> when it happens to
/// be done and with its <c>portalflood</c> otherwise,
/// portals having been sorted cheapest first. At one
/// thread "done" means exactly "ranked lower in that sort". At more, it means
/// whatever finished first -- spike 0c measured stock's lump moving between
/// <c>-threads 1</c> and <c>-threads 16</c>. Phase 2a therefore always read the
/// flood: deterministic, a strict superset of stock, and 3.9x the chains on
/// 2fort.
/// </para>
/// <para>
/// <b>The rule here.</b> Portals are ranked exactly as stock ranks them:
/// ascending <c>nummightsee</c>, ties in the order the C runtime's
/// <c>qsort</c> leaves them (<see cref="VisStockSort"/>). The ANSWER for the
/// portal at rank <c>k</c> is defined as the flow that prunes with the FINAL
/// <c>portalvis</c> of every candidate whose rank is below
/// <c>k - </c><see cref="Lag"/> and with the flood of every other. With
/// <see cref="Lag"/> zero that is stock at <c>-threads 1</c> read for read
/// (measured: PVS and PAS identical to stock's on 2fort, goldrush, dustbowl
/// and every catalogue map). A positive lag reads fewer finished answers --
/// looser, never tighter.
/// </para>
/// <para>
/// <b>How it is computed without waiting for it (phase 5).</b> A worker takes
/// the lowest-ranked portal nobody has taken and flows it at once. A candidate
/// below the limit that has finished is read exactly. One that has not is read
/// as its vector stands -- a subset of its final one, because a vector's bits
/// only go from clear to set and (below) every bit ever set is a bit of the
/// answer -- and <see cref="VisRepairTree"/> records, per candidate, the
/// union of <c>prev AND NOT seen</c> over every such read. The run is then
/// SETTLED: when each of those candidates has finished, the run was exact if
/// and only if no recorded vector meets that candidate's final
/// <c>portalvis</c>. An exact run makes the portal done. An inexact one is
/// flowed again, KEEPING the bits it already set.
/// </para>
/// <para>
/// <b>Why that is the answer, whatever the schedule.</b> By induction on the
/// rank. (1) Every bit any run sets is a bit of the answer: a run's tests are
/// each a subset of the exact test (a finished vector is exact by induction; a
/// vector read while growing is a subset of an exact one by induction), the
/// windings along a chain depend only on the portals in it, so the chains a
/// run walks are chains the exact flow may walk, and the <c>!more</c> early-out
/// Skips a candidate only when recursing could set no bit
/// not already set -- which holds whatever valid bits the vector started with.
/// (2) The settled run computes the exact intersection at every frame it
/// visits: that is what "no recorded vector meets the final one" says, read by
/// read. So it visits the exact flow's frames, minus subtrees the early-out
/// proves add nothing, and it ends holding every bit of the answer. A
/// candidate's done flag is published only after the run that settled it, so
/// a reader that sees "done" reads the final vector. At one thread every
/// candidate below the limit is done before the portal starts, no read is
/// speculative, and the walk is stock's own.
/// </para>
/// <para>
/// <b>Why it always finishes.</b> A portal waits only for LOWER ranks. The
/// lowest-ranked portal that is not done has every candidate below it done, so
/// its next run reads nothing speculatively and settles at once; workers always
/// take the lowest rank on offer, so that run happens. Each portal is flowed
/// again only after some candidate it speculated on has finished, so the
/// number of runs is bounded.
/// </para>
/// <para>
/// <b>Why the answer is a subset of the untightened one, and a superset of
/// stock's.</b> A portal's <c>portalvis</c> is the set of candidates reached by
/// chains whose every step passes the same geometric tests and finds its bit in
/// the running intersection of test vectors. A smaller test vector can only
/// remove chains; <c>portalvis</c> is always inside the flood, so by induction
/// over the rank every tightened <c>portalvis</c> is inside the untightened
/// one; and since this reads a finished answer only where stock at one thread
/// also does, every one contains stock's.
/// </para>
/// </remarks>
internal sealed class VisTightening : IVisFlowSplitter
{
    /// <summary>
    /// How many ranks below its own a portal's neighbours must be for their
    /// <c>portalvis</c> to be used.
    /// </summary>
    /// <remarks>
    /// A constant, and that is load-bearing: it decides which finished answers
    /// each flow reads, so it may not depend on the machine. Zero reproduces
    /// stock's single-threaded pruning exactly.
    /// </remarks>
    internal const int Lag = 0;

    private readonly VisPortalState _state;
    private readonly int[] _order;
    private readonly int[] _rank;
    private readonly int _lag;
    private readonly int _levels;
    private readonly object _gate = new();

    // Everything below is guarded by _gate, except that _next, _againCount,
    // _lowestNotDone and _completed are also READ without it, as a hint
    // (MayClaim), and so are written with Volatile.Write.
    private readonly bool[] _done;
    private readonly bool[] _flowed;
    private readonly VisRepairTree?[] _trees;
    private readonly int[] _pending;
    private readonly List<int>?[] _waiters;
    private readonly PriorityQueue<int, int> _again = new();
    private readonly ConcurrentStack<ulong[]> _vectors = new();
    private readonly Stack<VisRepairTree> _spareTrees = new();
    private readonly Queue<int> _finishing = new();
    private readonly List<int> _reads = [];
    private readonly ConcurrentStack<VisFrameTask> _frameTasks = new();
    private readonly ConcurrentStack<VisFrameTask> _spareFrameTasks = new();
    // How idle workers are brought back when something is put on offer.
    // Thread-safe on its own, not guarded by the gate.
    private readonly LoopWaker _waker = new();

    // Per worker index: whether its last step found nothing, and since when.
    // RunAsync sizes them to the degree before any worker starts.
    private bool[] _idleSlot = [];
    private long[] _idleSince = [];
    private readonly object _timelineLock = new();
    private int _queuedFrames;
    private readonly int[] _outstanding;
    private readonly int[] _readyCursor;
    private int _idleWorkers;
    private bool _splits0;
    private readonly VisHunger _hunger = new();
    private long _splits;
    private int _next;
    private int _againCount;
    private int _lowestNotDone;
    private int _completed;
    private long _idleTicks;
    private long _runs;
    private long _speculativeRuns;
    private long _reruns;
    private long _rerunChains;
    private int _vectorsOut;
    private int _vectorsPeak;

    /// <summary>Ranks one compile's portals.</summary>
    /// <param name="state">The state, after the base flow has filled every flood.</param>
    internal VisTightening(VisPortalState state)
        : this(state, Lag, VisRepairTree.DefaultLevels)
    {
    }

    /// <summary>Ranks one compile's portals, with an explicit lag.</summary>
    /// <param name="state">The state, after the base flow has filled every flood.</param>
    /// <param name="lag">See <see cref="Lag"/>.</param>
    /// <param name="levels">How many recursion levels each portal's <see cref="VisRepairTree"/> tracks.</param>
    internal VisTightening(VisPortalState state, int lag, int levels)
    {
        _state = state;
        _lag = lag;
        _levels = levels;

        int count = state.Count;
        _order = new int[count];
        for (int i = 0; i < count; i++)
        {
            _order[i] = i;
        }

        // SortPortals: the runtime's qsort with PComp
        // Counts only -- the tie order is the runtime's.
        VisStockSort.Sort(_order, (a, b) =>
        {
            int ca = state.MightSeeCount(a);
            int cb = state.MightSeeCount(b);
            return ca == cb ? 0 : ca < cb ? -1 : 1;
        });

        _rank = new int[count];
        for (int k = 0; k < count; k++)
        {
            _rank[_order[k]] = k;
        }

        _done = new bool[count];
        _flowed = new bool[count];
        _trees = new VisRepairTree?[count];
        _pending = new int[count];
        _waiters = new List<int>?[count];
        _outstanding = new int[count];
        _readyCursor = new int[count];
    }

    /// <summary>How many flows were run, over every portal (at least one each).</summary>
    internal long Runs => _runs;

    /// <summary>How many of those runs read a neighbour that had not finished.</summary>
    internal long SpeculativeRuns => _speculativeRuns;

    /// <summary>How many frames were split off to idle workers.</summary>
    internal long Splits => _splits;

    /// <inheritdoc/>
    public VisHunger Hunger => _hunger;

    /// <summary>How many runs were found inexact and flowed again.</summary>
    internal long Reruns => _reruns;

    /// <summary>How many of the chains were walked by runs after a portal's first.</summary>
    internal long RerunChains => _rerunChains;

    /// <summary>Diagnostics: every run, as (rank, start, end, chains, records); null for none.</summary>
    internal List<(int Rank, long Start, long End, int Chains, int Records)>? Timeline { get; init; }

    /// <summary>Diagnostics: when each rank was published done; null for none.</summary>
    internal List<(int Rank, long At)>? DoneAt { get; init; }

    /// <summary>How far past the lowest portal not done a fresh portal may be taken.</summary>
    internal int Window { get; init; } = 128;

    /// <summary>See <see cref="VisContext.TighteningClaimProbe"/>; null in every real compile.</summary>
    internal Action<int>? ClaimProbe { get; init; }

    /// <summary>See <see cref="VisContext.TighteningSettleProbe"/>; null in every real compile.</summary>
    internal Action<int, bool>? SettleProbe { get; init; }

    /// <summary>Seconds workers spent waiting for something to flow, summed over workers.</summary>
    internal double IdleSeconds => (double)_idleTicks / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The most record vectors that were alive at once.</summary>
    internal int VectorsPeak => _vectorsPeak;

    /// <summary>A portal's rank: stock's <c>SortPortals</c> order.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>Its rank.</returns>
    internal int RankOf(int portal) => _rank[portal];

    /// <summary>The portal at a rank.</summary>
    /// <param name="rank">A rank.</param>
    /// <returns>The memory-portal index.</returns>
    internal int PortalAt(int rank) => _order[rank];

    /// <summary>Flows every portal.</summary>
    /// <param name="queue">The compile's queue.</param>
    /// <param name="flows">One flow per worker, indexed by worker, filled on demand.</param>
    /// <param name="makeFlow">Builds a worker's flow.</param>
    /// <param name="progress">Where the flow stage's progress goes, or null.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>A task that completes when every portal is done.</returns>
    internal Task RunAsync(
        WorkQueue queue,
        VisPortalFlow?[] flows,
        Func<VisPortalFlow> makeFlow,
        IProgress<CompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Every worker draws from the shared schedule, one unit (a portal's
        // run, or a frame split off one) per step: the queue's own claiming
        // cannot express "again, once these have finished". A worker that
        // finds nothing on offer gives its pool thread back rather than
        // parking on it (WorkQueue.RunLoopAsync says why that matters on a
        // shared pool), and Wake brings one back for each new offer.
        int workers = Math.Max(1, queue.Degree);

        // One worker has nobody to split for: its walks skip the ledger.
        _splits0 = workers == 1;

        // One idle mark per worker index: the queue lends each index to one
        // thread at a time, so no two concurrent steps share a mark.
        _idleSlot = new bool[workers];
        _idleSince = new long[workers];
        return queue.RunLoopAsync(
            (flow, worker) => Step(flow, progress, worker),
            workerIndex =>
            {
                VisPortalFlow flow = flows[workerIndex] ??= makeFlow();
                flow.UseTightening(_rank, new VisSpeculativeReads(_state.Words, RentVector));
                return flow;
            },
            _waker,
            new WorkQueueOptions { Stage = Vvis.FlowStage },
            cancellationToken);
    }

    /// <summary>
    /// The first rank whose <c>portalvis</c> the portal at <paramref name="rank"/>
    /// may NOT read.
    /// </summary>
    private int Limit(int rank) => Math.Max(rank - _lag, 0);

    /// <summary>One unit of the schedule: a split-off frame, or a portal's run, and its settlement.</summary>
    /// <returns>
    /// <see cref="LoopStep.Worked"/> after a unit, <see cref="LoopStep.Idle"/>
    /// when nothing is on offer, <see cref="LoopStep.Finished"/> once every
    /// portal is done.
    /// </returns>
    private LoopStep Step(VisPortalFlow flow, IProgress<CompileProgress>? progress, WorkerContext worker)
    {
        int count = _order.Length;
        int rank = -1;
        VisRepairTree? tree = null;

        // A frame split off a run first: runs being split are the lowest
        // ranks in flight, which everything else waits for. Taken without
        // the gate -- measured (p5-vis-findings.md), the gate was where
        // 32 workers spent their waiting when every hand-off took it.
        VisFrameTask? frame = TakeFrame();
        if (frame is not null)
        {
            rank = frame.Rank;
            tree = Volatile.Read(ref _trees[rank]);
        }
        else
        {
            // The gate is taken only when there may be something to take
            // or the run may be over. Most passes through here at high
            // thread counts are idle workers finding nothing: every
            // portal not done is being flowed or waits for a lower one.
            // When each of those passes took the gate to learn that, the
            // idle workers queued on it behind each other and behind the
            // workers settling runs (measured on 2fort at 32 threads: the
            // gate was the most contended lock of the whole chain, and at
            // 32 threads on 4 cores 96 % of its acquisitions were such
            // failed claims). The hint is read without the gate, so it
            // can be stale either way: a stale "yes" costs one failed
            // claim, as every pass did before; a stale "no" is closed by
            // the wake protocol below.
            if (MayClaim(count))
            {
                lock (_gate)
                {
                    rank = Claim();
                    if (rank >= 0)
                    {
                        // A run whose every candidate below the limit has
                        // finished cannot speculate and needs no tree; any
                        // other records its reads in one.
                        if (_trees[rank] is null && !IsReady(rank))
                        {
                            _trees[rank] = SpareTree();
                        }

                        _outstanding[rank] = 1;
                        tree = _trees[rank];
                    }
                    else if (_completed >= count)
                    {
                        return LoopStep.Finished;
                    }
                }
            }

            if (rank < 0)
            {
                // Nothing on offer: every portal not done is being flowed
                // or is waiting for a lower one to finish. The worker leaves
                // (its thread goes back to the pool) and settling and
                // splitting wake one back per offer. Nothing is lost between
                // this read and the leaving: the pool thread read its signal
                // count before this step began, the offerer publishes before
                // it wakes, and a wake after that read keeps the thread from
                // sleeping (WorkQueue.RunLoopAsync).
                GoIdle(worker.WorkerIndex);
                return LoopStep.Idle;
            }
        }

        if (frame is null)
        {
            ClaimProbe?.Invoke(rank);
        }

        LeaveIdle(worker.WorkerIndex);
        long started = Stopwatch.GetTimestamp();
        if (frame is not null)
        {
            flow.RunFrame(frame, Limit(rank), tree, this, worker);
        }
        else
        {
            flow.Run(_order[rank], Limit(rank), tree, _splits0 ? null : this, worker);
        }

        long ended = Stopwatch.GetTimestamp();
        if (Timeline is not null)
        {
            lock (_timelineLock)
            {
                Timeline.Add((rank, started, ended, flow.Chains, frame is null ? 0 : -1));
            }
        }

        if (frame is not null)
        {
            _spareFrameTasks.Push(frame);
        }
        else
        {
            CountRun(rank, flow.Chains);
        }

        // Every piece of a run holds one count; the last to finish
        // settles it. A split adds its count while the splitting piece
        // still holds its own, so the count cannot reach zero early.
        if (Interlocked.Decrement(ref _outstanding[rank]) != 0)
        {
            return LoopStep.Worked;
        }

        int before;
        int after;
        int offered;
        bool speculated;
        lock (_gate)
        {
            before = _completed;

            // Read before Settle, which releases the tree of a run it accepts.
            speculated = _trees[rank] is { Speculated: true };
            Settle(rank);
            after = _completed;
            offered = Offered(count);
        }

        // Outside the gate: a woken worker's first act is to take the gate,
        // and it should not find this worker still holding it.
        Wake(offered);

        // After the wake and outside the gate, so a fact that blocks here
        // blocks only this worker, as the claim probe does.
        SettleProbe?.Invoke(rank, speculated);

        for (int done = before + 1; done <= after; done++)
        {
            progress?.Report(new CompileProgress(Vvis.FlowStage, done, count));
        }

        return LoopStep.Worked;
    }

    // A worker index that found nothing on offer counts as idle (for the
    // hunger that decides whether a flow splits) until it next takes a unit.
    // Marked per index, not per step, so a worker that comes back and finds
    // nothing again is not counted twice.
    private void GoIdle(int slot)
    {
        if (_idleSlot[slot])
        {
            return;
        }

        _idleSlot[slot] = true;
        _idleSince[slot] = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _idleWorkers);
        UpdateHunger();
    }

    private void LeaveIdle(int slot)
    {
        if (!_idleSlot[slot])
        {
            return;
        }

        _idleSlot[slot] = false;
        Interlocked.Add(ref _idleTicks, Stopwatch.GetTimestamp() - _idleSince[slot]);
        Interlocked.Decrement(ref _idleWorkers);
        UpdateHunger();
    }

    private VisFrameTask? TakeFrame()
    {
        if (!_frameTasks.TryPop(out VisFrameTask? frame))
        {
            return null;
        }

        Interlocked.Decrement(ref _queuedFrames);
        UpdateHunger();
        return frame;
    }

    /// <inheritdoc/>
    public void Split(
        int basePortal,
        int cluster,
        int depth,
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        ReadOnlySpan<ulong> mightSee,
        int node,
        int from,
        int to)
    {
        VisFrameTask frame = _spareFrameTasks.TryPop(out VisFrameTask? spare)
            ? spare
            : new VisFrameTask(_state.Words);
        int rank = _rank[basePortal];
        frame.Set(rank, basePortal, cluster, depth, source, pass, mightSee, node, from, to);
        Interlocked.Increment(ref _outstanding[rank]);
        Interlocked.Increment(ref _splits);
        Interlocked.Increment(ref _queuedFrames);
        _frameTasks.Push(frame);
        UpdateHunger();
        Wake(1);
    }

    /// <summary>Whether a worker is waiting that no split-off frame is waiting for.</summary>
    private void UpdateHunger() =>
        _hunger.Set(Volatile.Read(ref _idleWorkers) > Volatile.Read(ref _queuedFrames));

    /// <summary>Brings back up to <paramref name="workers"/> idle workers.</summary>
    /// <remarks>
    /// Whatever the caller published (a split-off frame, a portal on offer)
    /// is visible to the woken: <see cref="LoopWaker.Wake"/> signals through
    /// interlocked writes.
    /// </remarks>
    private void Wake(int workers) => _waker.Wake(workers);

    /// <summary>
    /// Whether every candidate the portal at <paramref name="rank"/> could
    /// read as <c>portalvis</c> has finished -- every bit of its flood ranked
    /// below its limit -- so its run cannot speculate. Called under the gate.
    /// </summary>
    /// <remarks>
    /// Resumes from the word it last stopped at: done only ever goes from
    /// false to true, so a word that was clear stays clear.
    /// </remarks>
    private bool IsReady(int rank)
    {
        int limit = Limit(rank);
        if (limit == 0)
        {
            return true;
        }

        ReadOnlySpan<ulong> flood = _state.Flood(_order[rank]);
        for (int w = _readyCursor[rank]; w < flood.Length; w++)
        {
            ulong bits = flood[w];
            while (bits != 0)
            {
                int candidate = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                int other = _rank[candidate];
                if (other < limit && !_done[other])
                {
                    _readyCursor[rank] = w;
                    return false;
                }
            }
        }

        _readyCursor[rank] = flood.Length;
        return true;
    }

    private void CountRun(int rank, int chains)
    {
        // Only a run's own worker touches _flowed[rank]: one run of a rank
        // at a time.
        Interlocked.Increment(ref _runs);
        if (_flowed[rank])
        {
            Interlocked.Add(ref _rerunChains, chains);
        }

        _flowed[rank] = true;
    }

    /// <summary>
    /// The lowest-ranked portal to flow next -- one found inexact, or the next
    /// never flowed -- or -1. Called under the gate.
    /// </summary>
    private int Claim()
    {
        int again = _again.TryPeek(out int lowest, out _) ? lowest : int.MaxValue;
        int fresh = _next < _order.Length && _next < _lowestNotDone + Window ? _next : int.MaxValue;

        if (again == int.MaxValue && fresh == int.MaxValue)
        {
            return -1;
        }

        if (again < fresh)
        {
            _again.Dequeue();
            Volatile.Write(ref _againCount, _again.Count);
            return again;
        }

        Volatile.Write(ref _next, _next + 1);
        return fresh;
    }

    /// <summary>
    /// Whether <see cref="Claim"/> might find something, or the run might be
    /// over: read WITHOUT the gate, as a hint for whether to take it.
    /// </summary>
    /// <remarks>
    /// Each field it reads is written only under the gate and read here as
    /// it stands, so the answer can be stale; see the idle path of
    /// <see cref="Step"/> for why a stale answer costs at most a failed
    /// claim or one worker's trip out of the job and back. Every change that
    /// can turn it from false to true is made by a settlement, which then
    /// wakes <see cref="Offered"/> workers, and <see cref="Offered"/> is
    /// positive exactly when this is true. It never decides WHAT is claimed --
    /// <see cref="Claim"/> does, under the gate, lowest rank first -- so it
    /// cannot change which run happens when, only whether an idle worker asks.
    /// </remarks>
    /// <param name="count">How many portals there are.</param>
    internal bool MayClaim(int count)
    {
        if (Volatile.Read(ref _againCount) > 0 || Volatile.Read(ref _completed) >= count)
        {
            return true;
        }

        int next = Volatile.Read(ref _next);
        return next < count && next < Volatile.Read(ref _lowestNotDone) + Window;
    }

    /// <summary>
    /// How many claims <see cref="Claim"/> would grant now -- every run found
    /// inexact plus every fresh portal inside the window -- or every worker
    /// once the last portal is done, so that all of them see the end. Called
    /// under the gate.
    /// </summary>
    /// <remarks>
    /// A settled run used to wake EVERY idle worker. At 32 threads that was a
    /// herd: all of them went for the gate at once to claim the one or two
    /// portals the settlement had put on offer, and all but those went back
    /// to waiting. Waking only as many as there are claims leaves the rest
    /// asleep; a claim that one of the woken loses to a worker that was not
    /// asleep leaves that worker to find the next offer, or its timeout.
    /// </remarks>
    /// <param name="count">How many portals there are.</param>
    /// <returns>How many idle workers are worth waking.</returns>
    internal int Offered(int count)
    {
        if (_completed >= count)
        {
            return int.MaxValue;
        }

        int fresh = Math.Max(0, Math.Min(count, _lowestNotDone + Window) - _next);
        return _again.Count + fresh;
    }

    /// <summary>
    /// Takes a run every piece of which has finished: done, or waiting for the
    /// candidates it read unfinished. Called under the gate.
    /// </summary>
    private void Settle(int rank)
    {
        VisRepairTree? tree = _trees[rank];
        if (tree is null || !tree.Speculated)
        {
            if (tree is not null)
            {
                Release(rank, tree);
            }

            _finishing.Enqueue(rank);
            Finish();
            return;
        }

        _speculativeRuns++;

        _reads.Clear();
        tree.CollectPending(_reads);
        int pending = 0;
        foreach (int portal in _reads)
        {
            if (!_done[_rank[portal]])
            {
                (_waiters[portal] ??= []).Add(rank);
                pending++;
            }
        }

        _pending[rank] = pending;
        if (pending == 0)
        {
            Judge(rank);
            Finish();
        }
    }

    /// <summary>
    /// Judges a run once every candidate it read unfinished has finished: done
    /// if every read was exact, flowed again otherwise. Called under the gate.
    /// </summary>
    private void Judge(int rank)
    {
        VisRepairTree tree = _trees[rank]!;
        if (tree.Validate(MissedAnything))
        {
            Release(rank, tree);
            _finishing.Enqueue(rank);
            return;
        }

        _reruns++;
        _again.Enqueue(rank, rank);
        Volatile.Write(ref _againCount, _again.Count);
    }

    private bool MissedAnything(int portal, ulong[] missed) =>
        VisRepairTree.MissedAnything(missed, _state.Vis(portal));

    /// <summary>
    /// Publishes every portal queued as done and judges every run that was
    /// waiting only for it, and so on down the cascade. Called under the gate.
    /// </summary>
    private void Finish()
    {
        while (_finishing.TryDequeue(out int finished))
        {
            int portal = _order[finished];

            // The release that makes every store of the settled run visible to
            // a reader that sees Done (VisPortalFlow reads the status first).
            _state.SetStatus(portal, VisPortalStatus.Done);
            _done[finished] = true;
            DoneAt?.Add((finished, System.Diagnostics.Stopwatch.GetTimestamp()));
            Volatile.Write(ref _completed, _completed + 1);
            int lowest = _lowestNotDone;
            while (lowest < _done.Length && _done[lowest])
            {
                lowest++;
            }

            Volatile.Write(ref _lowestNotDone, lowest);

            List<int>? waiting = _waiters[portal];
            _waiters[portal] = null;
            if (waiting is not null)
            {
                foreach (int other in waiting)
                {
                    if (--_pending[other] == 0)
                    {
                        Judge(other);
                    }
                }
            }
        }
    }

    private VisRepairTree SpareTree()
    {
        if (_spareTrees.TryPop(out VisRepairTree? tree))
        {
            return tree;
        }

        return new VisRepairTree(ReturnVector, _levels);
    }

    private void Release(int rank, VisRepairTree tree)
    {
        _trees[rank] = null;
        tree.Clear();
        _spareTrees.Push(tree);
    }

    internal ulong[] RentVector()
    {
        int live = Interlocked.Increment(ref _vectorsOut);
        int peak;
        while (live > (peak = Volatile.Read(ref _vectorsPeak))
            && Interlocked.CompareExchange(ref _vectorsPeak, live, peak) != peak)
        {
        }

        if (_vectors.TryPop(out ulong[]? vector))
        {
            // Cleared here, by the flow worker that rents it, and not when it
            // was given back: vectors come back from Settle, Judge and
            // Release, all under the gate, and a run's tree can hold hundreds
            // of them, so clearing on return held every other worker off the
            // gate for a pass over each one. A vector's contents between
            // return and rent are never read.
            Array.Clear(vector);
            return vector;
        }

        return new ulong[_state.Words];
    }

    /// <summary>Takes a record vector back; see <see cref="RentVector"/> for why it is not cleared here.</summary>
    internal void ReturnVector(ulong[] vector)
    {
        Interlocked.Decrement(ref _vectorsOut);
        _vectors.Push(vector);
    }
}
