//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// Vvis: the visibility compiler.
/// </summary>
/// <remarks>
/// <para>
/// Takes a map that vbsp has already partitioned and the <c>.prt</c> file that
/// came with it, and works out which clusters can see and hear which. It writes
/// three lumps: LUMP_VISIBILITY, the leaf flags and contents it changes in
/// LUMP_LEAFS, and LUMP_LEAFMINDISTTOWATER.
/// </para>
/// <para>
/// <b>Output does not depend on the thread count or the portal order.</b> Stock
/// cannot say that -- measured, not assumed; see
/// <see cref="VisPortalFlow"/> for what was dropped to make it true here and
/// which direction the resulting difference from stock goes.
/// </para>
/// <para>
/// Dropped from the port, with reasons in the port's plan §9: the VMPI cluster
/// mode (VMPI is Windows-only), the water-distance pass (not referenced
/// by anything), <c>-low</c> (a process priority is the host's), and the
/// <c>BetterPortalVis</c> second-order approximation, which stock itself marks
/// "WAAAAAAY too slow" and never calls.
/// </para>
/// </remarks>
public static class Vvis
{
    /// <summary>
    /// The stage name <see cref="CompileProgress"/> carries while the flood
    /// approximation runs.
    /// </summary>
    public const string BaseStage = "vvis.BasePortalVis";

    /// <summary>
    /// The stage name <see cref="CompileProgress"/> carries during the real
    /// flow.
    /// </summary>
    public const string FlowStage = "vvis.PortalFlow";

    /// <summary>
    /// The stage name <see cref="CompileProgress"/> carries while the portal
    /// vectors are folded into per-cluster rows.
    /// </summary>
    public const string ClusterMergeStage = "vvis.ClusterMerge";

    /// <summary>
    /// The stage name <see cref="CompileProgress"/> carries during the symmetry
    /// crosscheck.
    /// </summary>
    public const string CrosscheckStage = "vvis.Crosscheck";

    /// <summary>
    /// The stage name <see cref="CompileProgress"/> carries while the PAS is
    /// built.
    /// </summary>
    public const string PasStage = "vvis.CalcPAS";

    /// <summary>
    /// The stage name <see cref="CompileProgress"/> carries while the
    /// visibility lump is run-length coded and assembled.
    /// </summary>
    public const string LumpStage = "vvis.VisLump";

    /// <summary>
    /// The stage name <see cref="CompileProgress"/> carries during the fog
    /// volume and leaf-to-water passes.
    /// </summary>
    public const string WaterStage = "vvis.Water";

    /// <summary>
    /// The warnings a set of options earns before anything is computed: one
    /// line per option that makes the output knowingly approximate.
    /// </summary>
    /// <param name="options">The options a compile will run with.</param>
    /// <returns>The warnings, empty for an exact compile.</returns>
    /// <remarks>
    /// <para>
    /// A function of the options rather than something the compile reports,
    /// so a host says it whether the stage runs or its cached result is
    /// replayed, and before a long flow rather than after it. The library
    /// never prints: <c>ssmap vvis</c> writes these to its output and
    /// <see cref="Compile.MapCompiler"/> reports them through the chain's
    /// log, and any other host decides for itself.
    /// </para>
    /// <para>
    /// <see cref="VvisOptions.FastFlowSteps"/> under <see cref="VvisOptions.Fast"/>
    /// earns nothing: <c>-fast</c> skips the flow the flag would shorten.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static IReadOnlyList<CompileDiagnostic> OptionWarnings(VvisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.FastFlowSteps is not int steps || options.Fast)
        {
            return [];
        }

        return
        [
            new CompileDiagnostic(VvisCodes.ApproximateFlow, DiagnosticSeverity.Warning, FastFlowWarning(steps)),
        ];
    }

    /// <summary>
    /// The one line <see cref="OptionWarnings"/> says for
    /// <see cref="VvisOptions.FastFlowSteps"/>, naming the step count so a
    /// log says which approximation the map got.
    /// </summary>
    /// <param name="steps">The step count.</param>
    /// <returns>The message.</returns>
    public static string FastFlowWarning(int steps) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"-fastflow={steps}: the PVS is approximate (walks stop early after {steps} exact steps) and may cull visible geometry; compile without it for a release");

    /// <summary>
    /// Computes a map's visibility and writes it into the map.
    /// </summary>
    /// <param name="bsp">
    /// The map, as vbsp left it. Its LUMP_VISIBILITY, LUMP_LEAFS and
    /// LUMP_LEAFMINDISTTOWATER are replaced.
    /// </param>
    /// <param name="portals">The memory portals from the map's <c>.prt</c>.</param>
    /// <param name="context">What was asked for, and how much machine to use.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The uncompressed rows and the counters stock prints.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidBspException">
    /// The map has no nodes or no faces (stock's "Empty map",
    ///), a leaf lump this port cannot read, or a cluster
    /// count that disagrees with the portal file's.
    /// </exception>
    /// <exception cref="OperationCanceledException">The compile was cancelled.</exception>
    public static Task<VisResult> ComputeAsync(
        BspData bsp,
        PortalSet portals,
        VisContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(context);

        cancellationToken.ThrowIfCancellationRequested();
        return HostHandoff.ReturnAsync(RunAsync(bsp, portals, context, cancellationToken));
    }

    /// <summary>
    /// The expensive half of <see cref="ComputeAsync"/> on its own: the base
    /// flood and the portal flow, which need only the portals and the fog radius,
    /// not the map.
    /// </summary>
    /// <param name="portals">The memory portals from the map's <c>.prt</c>.</param>
    /// <param name="radius">
    /// The <c>env_fog_controller</c> radius the flow culls with. A chain that
    /// starts the flow before the map is assembled passes its best reading of the
    /// map's entities; <see cref="FinishAsync"/> checks it against the map and
    /// flows again if it was wrong, so a wrong guess costs time, never bytes.
    /// </param>
    /// <param name="context">What was asked for, and how much machine to use.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The flowed portals, for <see cref="FinishAsync"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The options ask for a <c>-trace</c>, which only <see cref="ComputeAsync"/> runs.</exception>
    /// <exception cref="OperationCanceledException">The compile was cancelled.</exception>
    public static Task<VisFlow> FlowAsync(
        PortalSet portals,
        VisRadius radius,
        VisContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Options.Trace is not null)
        {
            throw new ArgumentException("-trace runs through ComputeAsync, not the split flow", nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return HostHandoff.ReturnAsync(FlowCoreAsync(portals, radius, context, cancellationToken));
    }

    /// <summary>
    /// The rest of <see cref="ComputeAsync"/> once the map exists: the leaf
    /// flags, the cluster rows, the PAS, the lump and the water passes.
    /// </summary>
    /// <param name="bsp">
    /// The map, as vbsp left it. Its LUMP_VISIBILITY, LUMP_LEAFS and
    /// LUMP_LEAFMINDISTTOWATER are replaced.
    /// </param>
    /// <param name="flow">What <see cref="FlowAsync"/> made of the map's portals.</param>
    /// <param name="context">The same context the flow ran with.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>Exactly what <see cref="ComputeAsync"/> returns for the same map and portals.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidBspException">As <see cref="ComputeAsync"/>.</exception>
    /// <exception cref="OperationCanceledException">The compile was cancelled.</exception>
    public static Task<VisResult> FinishAsync(
        BspData bsp,
        VisFlow flow,
        VisContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return HostHandoff.ReturnAsync(FinishWrittenAsync(bsp, flow, context, cancellationToken));
    }

    // FinishAsync's work: the tail, then its lumps into the BSP.
    private static async Task<VisResult> FinishWrittenAsync(
        BspData bsp,
        VisFlow flow,
        VisContext context,
        CancellationToken cancellationToken)
    {
        (VisResult result, VisLumps lumps) = await FinishCoreAsync(bsp, flow, context, cancellationToken)
            .ConfigureAwait(false);
        lumps.WriteTo(bsp);
        return result;
    }

    /// <summary>
    /// <see cref="FinishAsync"/> without the write: the lumps come back for the
    /// caller to commit when nothing else is reading the map.
    /// </summary>
    internal static async Task<(VisResult Result, VisLumps Lumps)> FinishCoreAsync(
        BspData bsp,
        VisFlow flow,
        VisContext context,
        CancellationToken cancellationToken)
    {
        using WorkQueue queue = new(context.Parallelism);

        VisLeaves leaves = null!;
        VisRadius radius = default;
        Begin(context, BaseStage, flow.PortalCount);
        await queue.RunAsync(
            1,
            (_, _) =>
            {
                CheckNotEmpty(bsp);
                leaves = VisLeaves.From(bsp);
                radius = DetermineRadius(bsp, context);
                MarkRadial(leaves, radius);
            },
            new WorkQueueOptions { Stage = BaseStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        if (radius != flow.Radius)
        {
            // The guess the flow started from was not the map's radius: flow
            // again with the right one, so the answer is ComputeAsync's.
            flow = await FlowCoreAsync(flow.Portals, radius, context, cancellationToken).ConfigureAwait(false);
        }

        return await TailAsync(queue, bsp, leaves, flow, context, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VisResult> RunAsync(
        BspData bsp,
        PortalSet portals,
        VisContext context,
        CancellationToken cancellationToken)
    {
        int portalCount = portals.Count;

        using WorkQueue queue = new(context.Parallelism);

        // The whole of the setup runs on a worker, not on the thread that
        // awaited (the port's plan §1a: an ...Async method returns an incomplete
        // task promptly and never runs the compile on the caller's thread).
        // That is not ceremony here: the two per-portal bit vectors alone are
        // portalCount * portalbytes * 2, which is over a gigabyte on a map at
        // the portal limit, and allocating it is not something a host's request
        // thread should be doing.
        VisLeaves leaves = null!;
        VisPortalState state = null!;
        VisRadius radius = default;

        Begin(context, BaseStage, portalCount);
        await queue.RunAsync(
            1,
            (_, _) =>
            {
                CheckNotEmpty(bsp);
                leaves = VisLeaves.From(bsp);
                radius = DetermineRadius(bsp, context);
                MarkRadial(leaves, radius);
                state = new VisPortalState(portalCount);
            },
            new WorkQueueOptions { Stage = BaseStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        long baseRays = await BaseFlowAsync(queue, portals, state, radius, context, cancellationToken)
            .ConfigureAwait(false);

        if (context.Options.Trace is (int start, int stop))
        {
            return await TraceAsync(
                queue, portals, state, context, portals.ClusterCount, portalCount, (portals.ClusterCount + 7) >> 3,
                start, stop, radius.Use, radius.Squared, baseRays, cancellationToken).ConfigureAwait(false);
        }

        VisFlow flow = await PortalFlowAsync(queue, portals, state, radius, baseRays, context, cancellationToken)
            .ConfigureAwait(false);
        (VisResult result, VisLumps lumps) = await TailAsync(queue, bsp, leaves, flow, context, cancellationToken)
            .ConfigureAwait(false);
        lumps.WriteTo(bsp);
        return result;
    }

    private static async Task<VisFlow> FlowCoreAsync(
        PortalSet portals,
        VisRadius radius,
        VisContext context,
        CancellationToken cancellationToken)
    {
        int portalCount = portals.Count;
        using WorkQueue queue = new(context.Parallelism);

        VisPortalState state = null!;
        Begin(context, BaseStage, portalCount);
        await queue.RunAsync(
            1,
            (_, _) => state = new VisPortalState(portalCount),
            new WorkQueueOptions { Stage = BaseStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        long baseRays = await BaseFlowAsync(queue, portals, state, radius, context, cancellationToken)
            .ConfigureAwait(false);
        return await PortalFlowAsync(queue, portals, state, radius, baseRays, context, cancellationToken)
            .ConfigureAwait(false);
    }

    // -- the emptiness check is on nodes and faces, before anything else is read.
    private static void CheckNotEmpty(BspData bsp)
    {
        if (bsp[BspLump.Nodes].IsEmpty || bsp[BspLump.Faces].IsEmpty)
        {
            throw new InvalidBspException("Empty map");
        }
    }

    // MarkLeavesAsRadial. Every leaf, not just the ones the radius actually culled.
    private static void MarkRadial(VisLeaves leaves, VisRadius radius)
    {
        if (!radius.Use)
        {
            return;
        }

        for (int leaf = 0; leaf < leaves.Count; leaf++)
        {
            leaves.AddFlags(leaf, LeafFlags.Radial);
        }
    }

    private static async Task<long> BaseFlowAsync(
        WorkQueue queue,
        PortalSet portals,
        VisPortalState state,
        VisRadius radius,
        VisContext context,
        CancellationToken cancellationToken)
    {
        int portalCount = portals.Count;

        // One VisBaseFlow for every worker: it holds no per-item state, and its
        // only scratch is the flood stack, which IS per worker and is the
        // queue's own scratch rather than anything static. The array is the
        // house pattern for reading a worker back afterwards (see the flow
        // stage below): the per-worker instance counts its base-pass casts,
        // and the run reports the sum.
        VisBaseFlow[] baseFlows = new VisBaseFlow[Math.Max(1, queue.Degree)];
        await queue.RunAsync(
            portalCount,
            (index, stack, worker) =>
            {
                baseFlows[worker.WorkerIndex].Run(index, stack, worker);
                return 0;
            },
            workerIndex =>
            {
                VisBaseFlow flow = new(portals, state, radius.Use, radius.Squared);
                baseFlows[workerIndex] = flow;
                return new VisFloodScratch(portalCount);
            },
            new WorkQueueOptions { Stage = BaseStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        long baseRays = 0;
        foreach (VisBaseFlow? flow in baseFlows)
        {
            if (flow is not null)
            {
                baseRays += flow.BaseRays;
            }
        }

        return baseRays;
    }

    private static async Task<VisFlow> PortalFlowAsync(
        WorkQueue queue,
        PortalSet portals,
        VisPortalState state,
        VisRadius radius,
        long baseRays,
        VisContext context,
        CancellationToken cancellationToken)
    {
        int portalCount = portals.Count;
        int[] sorted = SortPortals(state, portalCount, context.Options.NoSort);
        VisSeparatorPath separators = ResolveSeparators(context);

        Begin(context, FlowStage, portalCount);

        int deepest = 0;
        // The base pass already ran — it runs for EVERY option set, -fast
        // included (stock dispatches it unconditionally) — so its
        // cast count seeds the record and survives to the result whatever the
        // flow stage below does or skips.
        VisWorkCounters work = new(Chains: 0, Candidates: 0, SeparatorClips: 0, BaseRays: baseRays);
        // -fastflow: the cluster-granular early stop (VisClusterStop). Null
        // on every exact compile, and then nothing below differs from it.
        VisClusterStop? stop = context.Options.FastFlowSteps is int steps && !context.Options.Fast
            ? new VisClusterStop(portals, context.FastFlowFilter, steps)
            : null;
        if (context.Options.Fast)
        {
            state.UseFloodAsVis();
        }
        else if (context.Options.Tighten)
        {
            // The port's plan §5, 2c: prune with finished neighbours' portalvis
            // where stock at one thread would, and nowhere else. See
            // VisTightening for why that makes the answer independent of the
            // degree and of -nosort (the ranking is stock's sort, whatever
            // -nosort says), a subset of the untightened flow's and a superset
            // of stock's single-threaded one.
            VisPortalFlow?[] workers = new VisPortalFlow?[Math.Max(1, queue.Degree)];
            VisTightening tightening = new(state, whole: stop is not null)
            {
                ClaimProbe = context.TighteningClaimProbe,
                SettleProbe = context.TighteningSettleProbe,
            };
            await tightening.RunAsync(
                queue,
                workers,
                () => new VisPortalFlow(portals, state, context.Path, stop: stop, separators: separators),
                context.Progress,
                cancellationToken).ConfigureAwait(false);

            foreach (VisPortalFlow? flow in workers)
            {
                if (flow is not null)
                {
                    deepest = Math.Max(deepest, flow.HighWaterMark);
                    work += flow.Work;
                }
            }
        }
        else
        {
            VisPortalFlow[] workers = new VisPortalFlow[Math.Max(1, queue.Degree)];
            await queue.RunAsync(
                portalCount,
                (index, flow, worker) =>
                {
                    flow.Run(sorted[index], worker);
                    return 0;
                },
                workerIndex =>
                {
                    VisPortalFlow flow = new(portals, state, context.Path, stop: stop, separators: separators);
                    workers[workerIndex] = flow;
                    return flow;
                },
                new WorkQueueOptions
                {
                    Stage = FlowStage,
                    Progress = context.Progress,

                    // Stock sorts cheapest first so the later portals can reuse
                    // earlier answers; that reuse is gone here (it is the race),
                    // so the order is only a scheduling choice and the queue's
                    // own cost-sorted partitioning is told what each item costs.
                    ItemCost = index => state.MightSeeCount(sorted[index]),
                },
                cancellationToken).ConfigureAwait(false);

            foreach (VisPortalFlow? flow in workers)
            {
                if (flow is not null)
                {
                    deepest = Math.Max(deepest, flow.HighWaterMark);
                    work += flow.Work;
                }
            }
        }

        return new VisFlow(portals, state, radius, deepest, work) { SeparatorPath = separators };
    }

    private static async Task<(VisResult Result, VisLumps Lumps)> TailAsync(
        WorkQueue queue,
        BspData bsp,
        VisLeaves leaves,
        VisFlow flow,
        VisContext context,
        CancellationToken cancellationToken)
    {
        PortalSet portals = flow.Portals;
        VisPortalState state = flow.State;
        int clusters = portals.ClusterCount;
        int rowBytes = (clusters + 7) >> 3;

        byte[] pvs = new byte[clusters * rowBytes];
        byte[] pas = new byte[clusters * rowBytes];
        ushort[] minDistanceToWater = new ushort[leaves.Count];

        int totalVis = 0;
        int optimized = 0;
        int totalAudible = 0;
        byte[] visLump = [];

        // The tail, one stage per pass. Five names rather than one because a
        // tail is judged on its FRACTION of the wall time and a single name
        // cannot say which pass owns it -- this split is what found that the
        // leaf-to-water pass, not CalcPAS, is the whole of it.
        //
        // Each still runs on a worker rather than on the thread that awaited:
        // the library never runs a compile on the caller's thread.
        Begin(context, ClusterMergeStage, clusters);
        await queue.RunAsync(
            1,
            (_, worker) =>
                totalVis = ClusterMerge(portals, state, clusters, rowBytes, pvs, context.Path, worker),
            new WorkQueueOptions { Stage = ClusterMergeStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);
        Begin(context, CrosscheckStage, clusters);
        await queue.RunAsync(
            1,
            (_, worker) => optimized = Crosscheck(clusters, rowBytes, pvs, worker),
            new WorkQueueOptions { Stage = CrosscheckStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        Begin(context, PasStage, clusters);
        await queue.RunAsync(
            1,
            (_, worker) => totalAudible = CalcPas(clusters, rowBytes, pvs, pas, worker),
            new WorkQueueOptions { Stage = PasStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        Begin(context, LumpStage, clusters);
        await queue.RunAsync(
            1,
            (_, _) => visLump = BuildVisibilityLump(clusters, rowBytes, pvs, pas),
            new WorkQueueOptions { Stage = LumpStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        Begin(context, WaterStage, leaves.Count);
        await queue.RunAsync(
            1,
            (_, worker) =>
            {
                int[][] clusterLeaves = VisWater.BuildClusterTable(leaves, clusters);
                VisWater.CalcVisibleFogVolumes(leaves, pvs, rowBytes, clusterLeaves, minDistanceToWater);
                VisWater.CalcDistanceFromLeavesToWater(
                    bsp, leaves, pvs, rowBytes, clusterLeaves, minDistanceToWater,
                    worker.CancellationToken);
            },
            new WorkQueueOptions { Stage = WaterStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        VisLumps lumps = new(visLump, new BspLumpData(leaves.Bytes, leaves.Version, 0), ToBytes(minDistanceToWater));
        VisResult result = new(
            clusters, portals.Count, rowBytes, pvs, pas, visLump.Length,
            totalVis, optimized, totalAudible, flow.Radius.Use, flow.Radius.Squared, flow.DeepestFlow, flow.Work,
            trace: null)
        {
            SeparatorPath = flow.SeparatorPath,
        };
        return (result, lumps);
    }

    /// <summary>
    /// The separator clip a flow runs: <see cref="VvisOptions.SeparatorPath"/>
    /// resolved against the CPU (<see cref="VisContext.Cpu"/> when a fact set
    /// one, otherwise the one this process runs on).
    /// </summary>
    /// <remarks>
    /// Called once as each flow starts and handed to its workers, never kept
    /// in a static: the libraries hold no shared mutable state, and two
    /// compiles in one process each resolve their own. See
    /// <see cref="VisSeparatorPaths.Resolve"/> for the rule.
    /// </remarks>
    internal static VisSeparatorPath ResolveSeparators(VisContext context) =>
        VisSeparatorPaths.Resolve(context.Options.SeparatorPath, context.Cpu ?? CpuCapabilities.Detect());

    /// <summary>
    /// Announces that a stage is starting, before any of its work runs.
    /// </summary>
    /// <param name="context">The compile, for its progress sink.</param>
    /// <param name="stage">Which stage.</param>
    /// <param name="total">How many items it has.</param>
    /// <remarks>
    /// A stage of ONE item otherwise reports only when it has finished, so
    /// everything it did is indistinguishable from the stage before it. That is
    /// not a hypothetical: the first stage timing of the tail charged the whole
    /// merge to the portal flow and printed the merge as one millisecond. A
    /// <c>0 of N</c> at the head costs one report per stage and makes the
    /// boundary observable to any host drawing a progress bar as well.
    /// </remarks>
    private static void Begin(VisContext context, string stage, int total) =>
        context.Progress?.Report(new CompileProgress(stage, 0, total));

    /// <summary>
    /// <c>CalcVisTrace</c>: flow only the portals leaving
    /// the start cluster and record the first route that reaches the end one.
    /// </summary>
    /// <remarks>
    /// Writes no lumps -- stock skips the whole write path for a trace
    /// -- so the rows come back empty and only
    /// <see cref="VisResult.Trace"/> is meaningful.
    /// </remarks>
    private static async Task<VisResult> TraceAsync(
        WorkQueue queue,
        PortalSet portals,
        VisPortalState state,
        VisContext context,
        int clusters,
        int portalCount,
        int rowBytes,
        int start,
        int stop,
        bool useRadius,
        double radiusSquared,
        long baseRays,
        CancellationToken cancellationToken)
    {
        if ((uint)start >= (uint)clusters || (uint)stop >= (uint)clusters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                $"Invalid cluster trace: {start} to {stop}, valid range is 0 to {clusters - 1}");
        }

        VisTraceSink sink = new(start, stop);
        VisSeparatorPath separators = ResolveSeparators(context);

        // BuildTracePortals: the scheduled portals are exactly the
        // start cluster's, in its own list order.
        int[] scheduled = portals.ClusterPortals(start).ToArray();

        await queue.RunAsync(
            scheduled.Length,
            (index, flow, worker) =>
            {
                flow.Run(scheduled[index], worker);
                return 0;
            },
            _ => new VisPortalFlow(portals, state, context.Path, sink, separators: separators),
            new WorkQueueOptions { Stage = FlowStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        return new VisResult(
            clusters, portalCount, rowBytes, [], [], 0, 0, 0, 0,
            useRadius, radiusSquared, 0,
            new VisWorkCounters(Chains: 0, Candidates: 0, SeparatorClips: 0, BaseRays: baseRays),
            sink.Points)
        {
            SeparatorPath = separators,
        };
    }

    /// <summary>
    /// <c>DetermineVisRadius</c> and the <c>-radius_override</c> path.
    /// </summary>
    /// <remarks>
    /// The two paths square the radius in different precisions and that is
    /// reproduced. <c>-radius_override</c> reads with <c>atof</c> into a double
    /// and squares in double; the map's own <c>farz</c> arrives as a float and
    /// is squared in FLOAT before being widened. One residual difference
    /// remains and is stated rather than hidden: <c>VvisOptions</c> stores the
    /// override as a float, so a command line whose radius is not exactly
    /// representable has already been rounded by the time it gets here.
    /// </remarks>
    private static VisRadius DetermineRadius(
        BspData bsp,
        VisContext context) =>
        VisRadius.FromEntities(
            EntityLump.Parse(bsp[BspLump.Entities]).Select(static e => (e.ClassName, e.Get("farz"))),
            context.Options);

    /// <summary>
    /// <c>SortPortals</c>: cheapest first, or file order
    /// under <c>-nosort</c>.
    /// </summary>
    /// <remarks>
    /// Stock uses <c>qsort</c>, which is not stable, so its order among equal
    /// <c>nummightsee</c> values is the implementation's business. Here the
    /// index breaks ties, which makes the schedule reproducible -- and that
    /// costs nothing, because the ANSWER does not depend on the order at all in
    /// this port. That is the whole point of the pruning change; if this sort
    /// mattered, the I4 gate would already be failing.
    /// </remarks>
    private static int[] SortPortals(VisPortalState state, int portalCount, bool noSort)
    {
        int[] sorted = new int[portalCount];
        for (int i = 0; i < portalCount; i++)
        {
            sorted[i] = i;
        }

        if (noSort)
        {
            return sorted;
        }

        Array.Sort(sorted, (a, b) =>
        {
            int byCount = state.MightSeeCount(a).CompareTo(state.MightSeeCount(b));
            return byCount != 0 ? byCount : a.CompareTo(b);
        });

        return sorted;
    }

    /// <summary>
    /// <c>ClusterMerge</c> and <c>LeafVectorFromPortalVector</c>
    /// </summary>
    private static int ClusterMerge(
        PortalSet portals,
        VisPortalState state,
        int clusters,
        int rowBytes,
        byte[] pvs,
        BitVectorPath path,
        WorkerContext worker)
    {
        ulong[] portalVector = new ulong[state.Words];
        int totalVis = 0;

        for (int cluster = 0; cluster < clusters; cluster++)
        {
            worker.ThrowIfShouldStop();

            Array.Clear(portalVector);

            foreach (int portal in portals.ClusterPortals(cluster))
            {
                if (state.Status(portal) != VisPortalStatus.Done)
                {
                    throw new InvalidOperationException(
                        $"portal {portal} of cluster {cluster} was never flowed");
                }

                BitVectorOps.OrInto(portalVector, state.Vis(portal), path);
                BitVectorOps.SetBit(portalVector, portal);
            }

            Span<byte> row = pvs.AsSpan(cluster * rowBytes, rowBytes);
            row.Clear();

            int numVis = 0;
            for (int i = 0; i < portals.Count; i++)
            {
                if (!BitVectorOps.GetBit(portalVector, i))
                {
                    continue;
                }

                int leaf = portals.Leaf(i);
                ref byte at = ref row[leaf >> 3];
                if ((at & (1 << (leaf & 7))) == 0)
                {
                    at |= (byte)(1 << (leaf & 7));
                    numVis++;
                }
            }

            // -- a cluster always sees itself, and the count is
            // incremented whether or not the bit was already there. It never is:
            // a cluster's portals point AWAY from it.
            row[cluster >> 3] |= (byte)(1 << (cluster & 7));
            numVis++;

            totalVis += numVis;
        }

        return totalVis;
    }

    /// <summary>
    /// The symmetry pass of <c>CompressAndCrosscheckClusterVis</c>
    /// </summary>
    /// <remarks>
    /// Stock does this in place while walking clusters in order, so a row it has
    /// already trimmed is read while trimming a later one. That is safe and the
    /// result is the symmetric intersection either way: a bit is only cleared
    /// from row <c>n</c> when row <c>i</c> lacks the matching bit, and the pass
    /// never clears a bit that would have made some other decision go the other
    /// way. Done in place here too, in the same order, so the two cannot drift
    /// even if that argument is ever found to be wrong.
    /// </remarks>
    private static int Crosscheck(int clusters, int rowBytes, byte[] pvs, WorkerContext worker)
    {
        int optimized = 0;

        for (int cluster = 0; cluster < clusters; cluster++)
        {
            worker.ThrowIfShouldStop();

            Span<byte> row = pvs.AsSpan(cluster * rowBytes, rowBytes);
            for (int i = 0; i < clusters; i++)
            {
                if (i == cluster)
                {
                    continue;
                }

                if ((row[i >> 3] & (1 << (i & 7))) == 0)
                {
                    continue;
                }

                if ((pvs[(i * rowBytes) + (cluster >> 3)] & (1 << (cluster & 7))) != 0)
                {
                    continue;
                }

                row[i >> 3] &= (byte)~(1 << (i & 7));
                optimized++;
            }
        }

        return optimized;
    }

    /// <summary>
    /// <c>CalcPAS</c>: each cluster hears the union of
    /// what every cluster it can see can see.
    /// </summary>
    /// <remarks>
    /// Reads the CROSSCHECKED rows, because stock runs this after the crosscheck
    /// has already rewritten <c>uncompressedvis</c> in place, and it reads the
    /// original row rather than the accumulating one -- so a cluster brought in
    /// by the union does not itself pull in a third. This is a potentially
    /// AUDIBLE set of radius two, not a transitive closure.
    /// </remarks>
    private static int CalcPas(int clusters, int rowBytes, byte[] pvs, byte[] pas, WorkerContext worker)
    {
        int count = 0;

        for (int cluster = 0; cluster < clusters; cluster++)
        {
            worker.ThrowIfShouldStop();

            ReadOnlySpan<byte> scan = pvs.AsSpan(cluster * rowBytes, rowBytes);
            Span<byte> row = pas.AsSpan(cluster * rowBytes, rowBytes);
            scan.CopyTo(row);

            for (int j = 0; j < rowBytes; j++)
            {
                int bitByte = scan[j];
                if (bitByte == 0)
                {
                    continue;
                }

                for (int k = 0; k < 8; k++)
                {
                    if ((bitByte & (1 << k)) == 0)
                    {
                        continue;
                    }

                    int index = (j << 3) + k;
                    if (index >= clusters)
                    {
                        throw new InvalidOperationException("Bad bit in PVS");
                    }

                    ReadOnlySpan<byte> other = pvs.AsSpan(index * rowBytes, rowBytes);
                    for (int b = 0; b < rowBytes; b++)
                    {
                        row[b] |= other[b];
                    }
                }
            }

            for (int j = 0; j < clusters; j++)
            {
                if ((row[j >> 3] & (1 << (j & 7))) != 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Assembles LUMP_VISIBILITY: the header, then every PVS row, then every
    /// PAS row.
    /// </summary>
    private static byte[] BuildVisibilityLump(int clusters, int rowBytes, byte[] pvs, byte[] pas)
    {
        int headerBytes = sizeof(int) + (clusters * 2 * sizeof(int));
        byte[] scratch = new byte[VisRunLength.MaxCompressedLength(rowBytes)];

        List<byte> body = new(clusters * rowBytes);
        int[] pvsOffset = new int[clusters];
        int[] pasOffset = new int[clusters];

        for (int cluster = 0; cluster < clusters; cluster++)
        {
            pvsOffset[cluster] = headerBytes + body.Count;
            int written = VisRunLength.Compress(pvs.AsSpan(cluster * rowBytes, rowBytes), scratch);
            body.AddRange(scratch.AsSpan(0, written));
        }

        for (int cluster = 0; cluster < clusters; cluster++)
        {
            pasOffset[cluster] = headerBytes + body.Count;
            int written = VisRunLength.Compress(pas.AsSpan(cluster * rowBytes, rowBytes), scratch);
            body.AddRange(scratch.AsSpan(0, written));
        }

        byte[] lump = new byte[headerBytes + body.Count];
        BinaryPrimitives.WriteInt32LittleEndian(lump, clusters);
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            int at = sizeof(int) + (cluster * 2 * sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(lump.AsSpan(at, 4), pvsOffset[cluster]);
            BinaryPrimitives.WriteInt32LittleEndian(lump.AsSpan(at + 4, 4), pasOffset[cluster]);
        }

        body.CopyTo(lump, headerBytes);
        return lump;
    }

    private static byte[] ToBytes(ushort[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(ushort)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort), sizeof(ushort)), values[i]);
        }

        return bytes;
    }

}
