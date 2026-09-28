//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Vis;

/// <summary>A frame's arguments, as <see cref="VisPortalFlow.FrameEntered"/> reports them.</summary>
/// <param name="depth">The frame's depth.</param>
/// <param name="cluster">The frame's cluster.</param>
/// <param name="source">Its source winding.</param>
/// <param name="pass">Its pass winding.</param>
/// <param name="mightSee">Its mask.</param>
internal delegate void VisFrameObserver(
    int depth,
    int cluster,
    ReadOnlySpan<Vec3> source,
    ReadOnlySpan<Vec3> pass,
    ReadOnlySpan<ulong> mightSee);

/// <summary>
/// The real answer: <c>PortalFlow</c> and <c>RecursiveLeafFlow</c>
/// </summary>
/// <remarks>
/// <para>
/// One instance per worker, because it owns a <see cref="VisFrameStack"/>. The
/// portal it is flowing is passed to <see cref="Run(int, WorkerContext)"/>, so the same instance
/// handles every item that worker claims.
/// </para>
/// <para>
/// <b>The one deliberate difference from stock, and the reason for it.</b>
/// Stock's candidate test reads a neighbouring
/// portal's FINISHED <c>portalvis</c> when that portal happens to be done and
/// its <c>portalflood</c> otherwise. It is a good optimisation and it is a data
/// race on the answer: how much pruning has happened depends on how many
/// workers are running and what order they took the portals in. Spike 0c
/// measured that on a frozen 2fort -- stock at one thread and at sixteen
/// produce different visibility lumps, and <c>-nosort</c> differs again, in both
/// directions.
/// </para>
/// <para>
/// The <b>untightened</b> walk (<c>-loose</c>, <see
/// cref="SourceSharp.MapTools.Options.VvisOptions.Untightened"/>) always uses <c>portalflood</c>. Every
/// portal's flow then depends only on immutable data plus its own
/// <c>portalvis</c>, so the result is identical at any degree of parallelism
/// and in any order -- which is what the determinism gate asks for and what
/// stock cannot offer. The cost is a more conservative answer:
/// <c>portalvis</c> is always a subset of <c>portalflood</c>, so pruning with
/// the flood prunes no more than stock and this arm's PVS is a SUPERSET of
/// stock's, never a subset -- and measurably so: 2fort flows 569.6M chains
/// this way against the tightened walk's 145.4M, work NO stock thread count
/// does (vis-repair ledger, rung1). A superset is the safe direction -- it
/// can only cost overdraw, never make geometry vanish.
/// </para>
/// <para>
/// The DEFAULT walk puts stock's read back via <c>-tighten</c>'s machinery:
/// "finished" replaced by "ranked below the limit" -- a function of the map,
/// not of the schedule -- and <see cref="VisTightening"/> accepts a flow only
/// once every such neighbour it read has finished and every read is proven to
/// have seen what the final vector would have shown it. See that type for the
/// argument. It is the default because the plan said to promote it once it
/// never ADDED a bit (plan 2c/Q4), and it never does: it is bit-identical to
/// stock at <c>-threads 1</c>, sorted, on every catalogue and L4 map.
/// </para>
/// </remarks>
internal sealed class VisPortalFlow
{
    /// <summary>
    /// How deep the flood may go before this gives up.
    /// </summary>
    /// <remarks>
    /// There is no proof that the funnel always closes, in stock or here; what
    /// there is, is twenty-five years of stock surviving on a 1 MB stack with an
    /// 8.7 KB frame, which bounds real maps at about 115. A thousand is an order
    /// of magnitude past that and still nowhere near a stack this loop could
    /// overflow, so hitting it means something is wrong rather than that the map
    /// is big -- and it is reported as an error instead of killing the process.
    /// </remarks>
    internal const int MaxDepth = 1024;

    private readonly PortalSet _portals;
    private readonly VisPortalState _state;
    private readonly VisFrameStack _frames;
    private readonly BitVectorPath _path;
    private readonly VisTraceSink? _trace;
    private readonly List<Vec3[]>? _chain;

    private int[]? _rank;
    private VisRepairTree? _tree;
    private IVisFlowSplitter? _splitter;
    private VisHunger? _hunger;
    private VisFrameLedger? _ledger;
    private VisFrameTask? _task;
    private VisSpeculativeReads? _reads;
    private bool _atomic;
    private int _limit;

    private int _chains;
    private long _totalChains;
    private long _candidates;
    private long _separatorClips;

    /// <summary>Prepares one worker's flow.</summary>
    /// <param name="portals">The map's memory portals.</param>
    /// <param name="state">The shared bit vectors.</param>
    /// <param name="path">Which bit-vector implementation to use.</param>
    /// <param name="trace">
    /// Where a <c>-trace</c> route goes, or null for an ordinary run. When it
    /// is null nothing on the hot path pays for it.
    /// </param>
    internal VisPortalFlow(
        PortalSet portals,
        VisPortalState state,
        BitVectorPath path,
        VisTraceSink? trace = null)
    {
        _portals = portals;
        _state = state;
        _path = path;
        _trace = trace;
        _chain = trace is null ? null : [];
        _frames = new VisFrameStack(portals.Count);
    }

    /// <summary>
    /// Lets this flow prune with a neighbour's <c>portalvis</c> when that
    /// neighbour's rank is below the limit passed to
    /// <see cref="Run(int, int, VisRepairTree, IVisFlowSplitter, WorkerContext)"/>: its final
    /// vector when it has finished, and otherwise the vector as it stands,
    /// recorded in the run's <see cref="VisRepairTree"/> so
    /// <see cref="VisTightening"/> can tell afterwards whether the run was exact.
    /// </summary>
    /// <param name="rank">Each portal's rank in the fixed schedule.</param>
    /// <param name="reads">Where this worker records reads of unfinished neighbours.</param>
    internal void UseTightening(int[] rank, VisSpeculativeReads reads)
    {
        _rank = rank;
        _reads = reads;
    }

    /// <summary>How many recursion steps the last flow took.</summary>
    internal int Chains => _chains;

    /// <summary>
    /// What this worker did, over every portal it has flowed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-operation floor is a rate, and a rate needs a
    /// numerator this port can state about ITSELF: a wall time divided by a
    /// portal count says nothing, because the pruning choice at
    /// <see cref="VisPortalFlow"/> changes how many candidates a portal has.
    /// These are the denominators that do not move on the untightened walk,
    /// at any thread count, and on the tightened walk at one thread. On the
    /// tightened walk at more they include the re-walks of runs that read a
    /// neighbour still being flowed, so they move with the schedule while the
    /// answer does not (see <see cref="VisWorkCounters"/>).
    /// </para>
    /// <para>
    /// Three counters on the hot path, each an increment of a field already in
    /// L1 next to a bit-vector pass over hundreds of words. Measured at the
    /// noise floor of the run they instrument.
    /// </para>
    /// </remarks>
    internal VisWorkCounters Work => new(_totalChains, _candidates, _separatorClips, BaseRays: 0);

    /// <summary>The deepest frame this worker has ever needed.</summary>
    internal int HighWaterMark => _frames.HighWaterMark;

    /// <summary>
    /// Diagnostics: told the arguments of every frame as it is entered; null
    /// (the default) costs one test per frame.
    /// </summary>
    internal VisFrameObserver? FrameEntered { get; set; }

    /// <summary>Computes one portal's <c>portalvis</c>.</summary>
    /// <param name="portalIndex">A memory-portal index.</param>
    /// <param name="context">The worker, for cancellation.</param>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    /// <exception cref="InvalidOperationException">
    /// The flood went past <see cref="MaxDepth"/>.
    /// </exception>
    internal void Run(int portalIndex, WorkerContext context) => Run(portalIndex, -1, context);

    /// <summary>
    /// Computes one portal's <c>portalvis</c>, pruning with finished
    /// neighbours below a rank.
    /// </summary>
    /// <param name="portalIndex">A memory-portal index.</param>
    /// <param name="limit">
    /// Neighbours whose rank is below this are read as <c>portalvis</c>; -1
    /// for none.
    /// </param>
    /// <param name="context">The worker, for cancellation.</param>
    internal void Run(int portalIndex, int limit, WorkerContext context)
    {
        _limit = limit;
        _tree = null;
        Walk(portalIndex, context);
        _state.SetStatus(portalIndex, VisPortalStatus.Done);
    }

    /// <summary>
    /// One <c>-tighten</c> run of a portal. A neighbour below the limit that
    /// has not finished is read as it stands and the read is recorded for
    /// <paramref name="tree"/>; subtrees an earlier run of the same portal
    /// proved complete are skipped; and whenever <paramref name="splitter"/>
    /// reports an idle worker, the shallowest open frame hands the second half
    /// of its remaining candidates to it. Never marks the portal done -- only
    /// the schedule knows when every piece has finished and whether the run
    /// was exact.
    /// </summary>
    /// <param name="portalIndex">A memory-portal index.</param>
    /// <param name="limit">Neighbours whose rank is below this are read as <c>portalvis</c>.</param>
    /// <param name="tree">
    /// This portal's tree, or null when every neighbour below the limit has
    /// finished and so the run cannot speculate.
    /// </param>
    /// <param name="splitter">Where split-off frames go, or null for a run that never splits.</param>
    /// <param name="context">The worker, for cancellation.</param>
    internal void Run(
        int portalIndex,
        int limit,
        VisRepairTree? tree,
        IVisFlowSplitter? splitter,
        WorkerContext context)
    {
        _ledger ??= new VisFrameLedger(MaxDepth);
        _ledger.Root = 1;
        _ledger.Arguments(
            1,
            VisFrameLedger.SourcePortal, portalIndex, 0,
            VisFrameLedger.PassEmpty, 0, 0,
            VisFrameLedger.MightFlood, portalIndex);
        Begin(limit, tree, splitter, task: null);
        try
        {
            Walk(portalIndex, context);
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Walks a frame another worker split off (see
    /// <see cref="Run(int, int, VisRepairTree, IVisFlowSplitter, WorkerContext)"/>):
    /// a range of the candidates of one frame, with that frame's windings and mask.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="limit">Neighbours whose rank is below this are read as <c>portalvis</c>.</param>
    /// <param name="tree">The run's tree, or null.</param>
    /// <param name="splitter">Where frames split off again go.</param>
    /// <param name="context">The worker, for cancellation.</param>
    internal void RunFrame(
        VisFrameTask frame,
        int limit,
        VisRepairTree? tree,
        IVisFlowSplitter splitter,
        WorkerContext context)
    {
        _ledger ??= new VisFrameLedger(MaxDepth);
        _ledger.Root = frame.Depth;
        _ledger.Arguments(
            frame.Depth,
            VisFrameLedger.SourceTask, 0, 0,
            VisFrameLedger.PassTask, 0, 0,
            VisFrameLedger.MightTask, 0);
        Begin(limit, tree, splitter, frame);
        _chains = 0;
        try
        {
            Flow(
                frame.Cluster,
                frame.Depth,
                frame.BasePortal,
                frame.Source,
                frame.Pass,
                frame.MightSee,
                VisFrameStack.Extent(frame.MightSee),
                frame.Node,
                frame.From,
                frame.To,
                context);
        }
        finally
        {
            End();
        }
    }

    private void Begin(int limit, VisRepairTree? tree, IVisFlowSplitter? splitter, VisFrameTask? task)
    {
        _limit = limit;
        _tree = tree;
        _splitter = splitter;
        _hunger = splitter?.Hunger;
        _task = task;

        // A split-off frame shares its portal's vector with the worker that
        // split it; a whole run shares it only once it has split (see
        // SplitShallowest), and its stores before that happened-before the
        // split's hand-over.
        _atomic = task is not null;
    }

    private void End()
    {
        if (_reads is not null && _reads.Count > 0)
        {
            _tree!.Absorb(_reads);
        }

        _tree = null;
        _splitter = null;
        _hunger = null;
        _task = null;
        _atomic = false;
    }

    private void Walk(int portalIndex, WorkerContext context)
    {
        _chains = 0;
        _state.SetStatus(portalIndex, VisPortalStatus.Working);

        // The head frame's `source` is the portal's own
        // winding, its `pass` is null (the memset ), its plane is the
        // portal's, and its `mightsee` is a copy of portalflood. The copy is
        // skipped here: nothing writes the head frame's mightsee, so handing the
        // flood vector itself down is the same values.
        ReadOnlySpan<Vec3> source = _portals.Winding(portalIndex);
        ReadOnlySpan<ulong> flood = _state.Flood(portalIndex);

        // -- the head frame has no `pass`, so the trace walk uses
        // its portal's own winding for that link.
        _chain?.Clear();
        _chain?.Add(source.ToArray());

        Flow(
            _portals.Leaf(portalIndex),
            depth: 1,
            basePortal: portalIndex,
            prevSource: source,
            prevPass: [],
            prevMightSee: flood,
            mightExtent: VisFrameStack.Extent(flood),
            node: VisRepairTree.Root,
            from: 0,
            to: -1,
            context);
    }

    private void Flow(
        int cluster,
        int depth,
        int basePortal,
        ReadOnlySpan<Vec3> prevSource,
        ReadOnlySpan<Vec3> prevPass,
        ReadOnlySpan<ulong> prevMightSee,
        (int Lo, int Hi) mightExtent,
        int node,
        int from,
        int to,
        WorkerContext context)
    {
        if (_trace is not null && cluster == _trace.Stop)
        {
            // -- reaching the stop cluster records the route
            // and stops descending. It is checked before the chain counter, so
            // the terminating step is not counted.
            _trace.Capture(_portals, _chain!);
            return;
        }

        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"the portal flow recursed past {MaxDepth} levels on portal {basePortal}; "
                + "a real map is bounded near a hundred, so this is a defect rather than a big map");
        }

        _chains++;
        _totalChains++;
        FrameEntered?.Invoke(depth, cluster, prevSource, prevPass, prevMightSee);

        // THE MIGHT-SEE EXTENT.
        //
        // Every word of prevMightSee outside mightExtent is zero -- the caller
        // proved it -- so every word of prev AND test outside it is zero too.
        // The intersection below is therefore computed over the extent only,
        // and the frame's might buffer is made zero outside it once, here,
        // rather than rewritten with zeros by every candidate. The buffer
        // stays the whole, exact intersection every reader expects (the
        // candidate test's GetBit, the speculative reads, a split handing the
        // vector to another worker); only the work shrinks.
        //
        // It shrinks a lot. On 2fort a vector is 216 words, the nonzero words
        // of a frame's might-see span 49 of them on average and only 9 are
        // actually nonzero: deep in a flow almost everything has been ruled
        // out, and the ruled-out bits are the ones the full-width AND spent
        // its time on. The result is identical by construction: the words
        // skipped are words whose AND is zero, and a zero word contributes
        // nothing to `more` either.
        Span<ulong> might = _frames.MightSee(depth, mightExtent.Lo, mightExtent.Hi);
        ReadOnlySpan<ulong> prevSpan = prevMightSee[mightExtent.Lo..mightExtent.Hi];
        Span<ulong> mightSpan = might[mightExtent.Lo..mightExtent.Hi];
        Vec3[] windings = _frames.Windings(depth);
        Span<Vec3> passBuffer = windings.AsSpan(
            VisFrameStack.PassOffset, VisClip.MaxPointsOnFixedWinding);
        Span<Vec3> sourceBuffer = windings.AsSpan(
            VisFrameStack.SourceOffset, VisClip.MaxPointsOnFixedWinding);
        Span<Vec3> clipBuffer = windings.AsSpan(
            VisFrameStack.ClipOffset, VisClip.MaxPointsOnWinding);

        Span<ulong> vis = _state.Vis(basePortal);
        ReadOnlySpan<ulong> visSpan = vis[mightExtent.Lo..mightExtent.Hi];
        Vec3 basePlaneNormal = _portals.Normal(basePortal);
        float basePlaneDistance = _portals.Distance(basePortal);
        Vec3 baseOrigin = _portals.Origin(basePortal);
        float baseRadius = _portals.Radius(basePortal);

        // THE FRAME'S SEPARATOR CACHE.
        //
        // Both of stock's ClipToSeperators calls derive their planes from
        // (prevSource, prevPass) -- in one order and then the other -- and
        // NEITHER derivation looks at the winding being clipped. prevPass is
        // this frame's parameter and so is prevSource, so for every candidate
        // whose own `source` comes back as prevSource untouched the planes are
        // the same ones, re-derived from scratch with a cross product, a double
        // square root and two side tests per edge-vertex pair. Stock has
        // nowhere to keep them; a per-worker frame slab does.
        //
        // Built on first use rather than on entry, because a frame that rejects
        // every candidate on the sphere tests must not pay for a derivation
        // nothing reads.
        bool cacheable = !prevPass.IsEmpty
            && prevSource.Length * prevPass.Length <= VisFrameStack.MaxCachedSeparators;
        //
        // And derived only as far as a clip reaches: each ordering's list is a
        // VisSeparatorMemo, filled one source edge at a time when a clip has
        // used every plane in it, so planes past the furthest point any
        // candidate's clip got to are never derived at all, and a frame whose
        // candidates all die in the forward clip never derives the reverse
        // list. The order of the planes is the full derivation's, so each
        // clip sees the same sequence either way.
        bool memoReady = false;
        // Fetched with the first use below rather than here: an eager fetch
        // was measured at 1.4 % SLOWER across three interleaved pairs, because
        // most frames never reach a candidate that can use the cache and would
        // pay four slab lookups for nothing. 570 million frames against 326
        // million cached candidates is the whole of that difference.
        VisSeparatorMemo forward = default;
        VisSeparatorMemo reverse = default;

        ReadOnlySpan<int> candidates = _portals.ClusterPortals(cluster);
        int end = to < 0 ? candidates.Length : to;
        PrefetchCandidates(candidates, from, end, prevMightSee, mightExtent);
        VisFrameLedger? ledger = _splitter is null ? null : _ledger;
        ledger?.Enter(depth, cluster, node, end);
        for (int i = from; i < (ledger is null ? end : ledger.End(depth)); i++)
        {
            // A shared -tighten run hands half of what is left of the
            // SHALLOWEST open frame to an idle worker (this frame's end is
            // re-read every step because a deeper split may have shortened
            // it). The frame's arguments are copied, so the donor goes on
            // reusing its own slab; the order the halves run in cannot change
            // the answer (see VisTightening).
            if (ledger is not null)
            {
                ledger.At(depth, i);
                if (_hunger!.Hungry)
                {
                    SplitShallowest(depth, basePortal);
                }
            }

            // The token is polled INSIDE the item, not only
            // between items. One portal's flow is minutes of work on a dense
            // map, and this loop is where those minutes are spent.
            context.ThrowIfShouldStop();

            int pnum = candidates[i];

            if (!BitVectorOps.GetBit(prevMightSee, pnum))
            {
                continue;
            }

            _candidates++;

            // THE TWO SPHERE REJECTIONS, HOISTED ABOVE THE BIT-VECTOR PASS.
            //
            // The reference ran the bit-vector pass first and these two
            // tests afterwards. Neither test reads `might` or
            // `more`, neither has a side effect, and either one rejecting the
            // candidate skips the same `continue` the pass would have led to --
            // so doing the cheap halves first cannot change which portals are
            // marked visible or which are recursed into. It only stops the
            // 200-word intersection from being computed for a candidate that
            // was going to be thrown away after a dot product and a compare.
            //
            // The CHOPS stay below: they are the expensive half of each test
            // and they are only needed once the candidate has survived the
            // pruning as well. Hence the distances are computed here
            // and carried down rather than recomputed.
            Vec3 portalOrigin = _portals.Origin(pnum);
            float portalRadius = _portals.Radius(pnum);

            // The candidate portal's sphere against the BASE
            // portal's plane -- `thread->pstack_head.portalplane`, not this
            // Frame's. (stack.portalplane is assigned and never read;
            // it is dead in stock and absent here.)
            float passSide = Vec3.Dot(portalOrigin, basePlaneNormal);
            passSide -= basePlaneDistance;

            if (passSide < -portalRadius)
            {
                continue;
            }

            Vec3 portalNormal = _portals.Normal(pnum);
            float portalDistance = _portals.Distance(pnum);

            // The BASE portal's sphere against the candidate's
            // plane.
            float sourceSide = Vec3.Dot(baseOrigin, portalNormal);
            sourceSide -= portalDistance;

            if (sourceSide > baseRadius)
            {
                continue;
            }

            // WITHOUT the opportunistic read of a finished
            // neighbour's portalvis: see the type's remarks. Under -tighten the
            // portalvis IS read, but only for a neighbour ranked below the
            // limit -- so which vector is meant is a function of the ranks. If
            // that neighbour has not finished, the vector as it stands is a
            // subset of its final one; the read is recorded, and VisTightening
            // does not accept this run until every such read is proven to have
            // seen what the final vector would have shown it.
            //
            // -tighten's repair tree: in a tracked frame the candidate is a
            // node of its own, skipped outright when an earlier run of this
            // portal proved its subtree complete; deeper, reads are charged to
            // the deepest tracked node above.
            int child = node;
            bool tracked = false;
            if (_tree is not null && depth <= _tree.Levels)
            {
                child = _tree.Child(node, i, candidates.Length, _atomic);
                if (_tree.IsComplete(child, _atomic))
                {
                    continue;
                }

                _tree.Walk(child, _atomic);
                tracked = true;
            }

            bool more;
            if (_rank is not null && _rank[pnum] < _limit)
            {
                if (_state.Status(pnum) == VisPortalStatus.Done)
                {
                    more = BitVectorOps.AndWithNewBits(
                        prevSpan, _state.Vis(pnum)[mightExtent.Lo..mightExtent.Hi], visSpan, mightSpan, _path);
                }
                else if (_tree is not null)
                {
                    more = _reads!.AndSpeculative(child, pnum, prevMightSee, _state.Vis(pnum), vis, might);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"portal {basePortal} read unfinished portal {pnum} in a run that may not speculate");
                }
            }
            else
            {
                more = BitVectorOps.AndWithNewBits(
                    prevSpan, _state.Flood(pnum)[mightExtent.Lo..mightExtent.Hi], visSpan, mightSpan, _path);
            }

            if (!more && BitVectorOps.GetBit(vis, pnum))
            {
                if (tracked)
                {
                    // Nothing below can add a bit: whatever an earlier run
                    // kept of this subtree is no longer needed.
                    _tree!.Prune(child, _atomic);
                }

                continue;
            }

            ReadOnlySpan<Vec3> portalWinding = _portals.Winding(pnum);

            ReadOnlySpan<Vec3> pass;
            bool passIsChopped = false;
            {
                if (passSide > portalRadius)
                {
                    pass = portalWinding;
                }
                else
                {
                    VisChopResult chopped = VisClip.ChopWinding(
                        portalWinding, basePlaneNormal, basePlaneDistance, passBuffer, out int count);

                    if (chopped == VisChopResult.Empty)
                    {
                        continue;
                    }

                    passIsChopped = chopped == VisChopResult.Clipped;
                    pass = passIsChopped ? passBuffer[..count] : portalWinding;
                }
            }

            // The source winding clipped by the candidate's plane REVERSED.
            //
            // `sourceIsPrev` is what decides whether this candidate can use the
            // frame's cached separators, and BOTH ways of ending up with the
            // frame's own winding count: the sphere test skipping the chop, and
            // the chop reporting that it changed nothing.
            ReadOnlySpan<Vec3> source;
            bool sourceIsPrev;
            {
                if (sourceSide < -baseRadius)
                {
                    source = prevSource;
                    sourceIsPrev = true;
                }
                else
                {
                    Vec3 backNormal = VisClip.Negate(portalNormal);
                    float backDistance = -portalDistance;

                    VisChopResult chopped = VisClip.ChopWinding(
                        prevSource, backNormal, backDistance, sourceBuffer, out int count);

                    if (chopped == VisChopResult.Empty)
                    {
                        continue;
                    }

                    sourceIsPrev = chopped != VisChopResult.Clipped;
                    source = sourceIsPrev ? prevSource : sourceBuffer[..count];
                }
            }

            if (prevPass.IsEmpty)
            {
                // -- the second leaf can only be blocked if
                // coplanar, so there is nothing to clip against yet.
                SetVisible(vis, pnum);
                _chain?.Add(pass.ToArray());
                ledger?.Arguments(
                    depth + 1,
                    sourceIsPrev ? SourceKind(depth) : VisFrameLedger.SourceSlab,
                    sourceIsPrev ? SourceA(depth) : depth,
                    sourceIsPrev ? SourceB(depth) : source.Length,
                    passIsChopped ? VisFrameLedger.PassSlab : VisFrameLedger.PassPortal,
                    passIsChopped ? depth : pnum,
                    pass.Length,
                    VisFrameLedger.MightSlab,
                    depth);
                Flow(
                    _portals.Leaf(pnum),
                    depth + 1,
                    basePortal,
                    source,
                    pass,
                    might,
                    VisFrameStack.Extent(might, mightExtent),
                    child,
                    0,
                    -1,
                    context);
                _chain?.RemoveAt(_chain.Count - 1);
                continue;
            }

            _separatorClips++;

            int firstCount;
            int secondCount;
            bool useCache = cacheable && sourceIsPrev;

            if (useCache && !memoReady)
            {
                // Room for every plane the pairing can produce -- one per
                // edge-vertex pair -- so the lazy derivation can never run out
                // of space part-way and clip by a TRUNCATED list, which would
                // let sight lines through. `cacheable` bounds this product by
                // the slab's cap.
                int room = prevSource.Length * prevPass.Length;
                forward = new VisSeparatorMemo(
                    _frames.SeparatorNormals(depth, 0, room), _frames.SeparatorDistances(depth, 0, room));
                reverse = new VisSeparatorMemo(
                    _frames.SeparatorNormals(depth, 1, room), _frames.SeparatorDistances(depth, 1, room));
                memoReady = true;
            }

            if (useCache)
            {
                if (!VisClipLanes.ClipToSeparators(
                    ref forward,
                    prevSource,
                    prevPass,
                    pass,
                    flipClip: false,
                    clipBuffer,
                    out firstCount))
                {
                    continue;
                }

                if (!VisClipLanes.ClipToSeparators(
                    ref reverse,
                    prevPass,
                    prevSource,
                    clipBuffer[..firstCount],
                    flipClip: true,
                    clipBuffer,
                    out secondCount))
                {
                    continue;
                }
            }
            else
            {
                if (!VisClip.ClipToSeparators(
                    source, prevPass, pass, false, clipBuffer, out firstCount))
                {
                    continue;
                }

                if (!VisClip.ClipToSeparators(
                    prevPass, source, clipBuffer[..firstCount], true, clipBuffer, out secondCount))
                {
                    continue;
                }
            }

            SetVisible(vis, pnum);
            _chain?.Add(clipBuffer[..secondCount].ToArray());
            ledger?.Arguments(
                depth + 1,
                sourceIsPrev ? SourceKind(depth) : VisFrameLedger.SourceSlab,
                sourceIsPrev ? SourceA(depth) : depth,
                sourceIsPrev ? SourceB(depth) : source.Length,
                VisFrameLedger.PassClip,
                depth,
                secondCount,
                VisFrameLedger.MightSlab,
                depth);
            Flow(
                _portals.Leaf(pnum),
                depth + 1,
                basePortal,
                source,
                clipBuffer[..secondCount],
                might,
                VisFrameStack.Extent(might, mightExtent),
                child,
                0,
                -1,
                context);
            _chain?.RemoveAt(_chain.Count - 1);
        }
    }

    /// <summary>
    /// Hints the words of every candidate vector a frame will intersect into
    /// cache, before the frame tests the first of them.
    /// </summary>
    /// <param name="candidates">The frame's cluster's portals.</param>
    /// <param name="from">The first candidate the frame walks.</param>
    /// <param name="end">One past the last.</param>
    /// <param name="prevMightSee">The frame's might-see mask: a candidate whose bit is clear is never tested.</param>
    /// <param name="mightExtent">The words the intersection reads.</param>
    /// <returns>How many lines were hinted, for the facts.</returns>
    /// <remarks>
    /// The vector hinted is the one the candidate loop will read: the
    /// candidate's <c>portalvis</c> when its rank is below the limit (whether
    /// that read is of the finished vector or a speculative one, it is that
    /// array), its flood otherwise. Hinting a candidate the sphere tests then
    /// reject costs a line fetched for nothing; that is cheaper than deciding
    /// the sphere tests twice. See <see cref="VisPrefetch"/> for why none of
    /// this can change what the flow computes.
    /// </remarks>
    internal int PrefetchCandidates(
        ReadOnlySpan<int> candidates,
        int from,
        int end,
        ReadOnlySpan<ulong> prevMightSee,
        (int Lo, int Hi) mightExtent)
    {
        if (!System.Runtime.Intrinsics.X86.Sse.IsSupported || mightExtent.Hi <= mightExtent.Lo)
        {
            return 0;
        }

        int lines = 0;
        for (int i = from; i < end; i++)
        {
            int pnum = candidates[i];
            if (!BitVectorOps.GetBit(prevMightSee, pnum))
            {
                continue;
            }

            ReadOnlySpan<ulong> vector = _rank is not null && _rank[pnum] < _limit
                ? _state.Vis(pnum)
                : _state.Flood(pnum);
            lines += VisPrefetch.Lines(vector[mightExtent.Lo..mightExtent.Hi]);
        }

        return lines;
    }

    /// <summary>
    /// Marks a portal visible from the one being flowed: atomically when the
    /// run is shared, because two workers may set bits of one word.
    /// </summary>
    private void SetVisible(Span<ulong> vis, int portal)
    {
        if (!_atomic)
        {
            BitVectorOps.SetBit(vis, portal);
            return;
        }

        // Most calls find the bit already set (many chains reach one portal),
        // and a bit is never cleared, so a plain read that sees it set is
        // final; only a clear bit needs the atomic.
        ref ulong word = ref vis[portal >> 6];
        ulong bit = 1UL << (portal & 63);
        if ((Volatile.Read(ref word) & bit) == 0)
        {
            Interlocked.Or(ref word, bit);
        }
    }

    private int SourceKind(int depth) => _ledger!.Source(depth).Kind;

    private int SourceA(int depth) => _ledger!.Source(depth).A;

    private int SourceB(int depth) => _ledger!.Source(depth).B;

    /// <summary>
    /// Hands the second half of the shallowest open frame with candidates left
    /// to the splitter, copying the frame's arguments out of wherever the
    /// ledger says they live.
    /// </summary>
    private void SplitShallowest(int depth, int basePortal)
    {
        VisFrameLedger ledger = _ledger!;
        int d = ledger.Shallowest(depth);
        if (d < 0)
        {
            return;
        }

        (int from, int to) = ledger.Halve(d);
        _atomic = true;

        (int sourceKind, int sourceA, int sourceB) = ledger.Source(d);
        ReadOnlySpan<Vec3> source = sourceKind switch
        {
            VisFrameLedger.SourcePortal => _portals.Winding(sourceA),
            VisFrameLedger.SourceSlab => _frames.Windings(sourceA).AsSpan(VisFrameStack.SourceOffset, sourceB),
            _ => _task!.Source,
        };

        (int passKind, int passA, int passB) = ledger.Pass(d);
        ReadOnlySpan<Vec3> pass = passKind switch
        {
            VisFrameLedger.PassEmpty => [],
            VisFrameLedger.PassPortal => _portals.Winding(passA),
            VisFrameLedger.PassSlab => _frames.Windings(passA).AsSpan(VisFrameStack.PassOffset, passB),
            VisFrameLedger.PassClip => _frames.Windings(passA).AsSpan(VisFrameStack.ClipOffset, passB),
            _ => _task!.Pass,
        };

        (int mightKind, int mightA) = ledger.Might(d);
        ReadOnlySpan<ulong> mightSee = mightKind switch
        {
            VisFrameLedger.MightFlood => _state.Flood(mightA),
            VisFrameLedger.MightSlab => _frames.MightSee(mightA),
            _ => _task!.MightSee,
        };

        _splitter!.Split(basePortal, ledger.Cluster(d), d, source, pass, mightSee, ledger.Node(d), from, to);
    }
}
