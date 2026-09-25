using System.Buffers.Binary;
using System.Globalization;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
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
        return RunAsync(bsp, portals, context, cancellationToken);
    }

    private static async Task<VisResult> RunAsync(
        BspData bsp,
        PortalSet portals,
        VisContext context,
        CancellationToken cancellationToken)
    {
        int clusters = portals.ClusterCount;
        int portalCount = portals.Count;
        int rowBytes = (clusters + 7) >> 3;

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
        bool useRadius = false;
        double radiusSquared = 0.0;

        Begin(context, BaseStage, portalCount);
        await queue.RunAsync(
            1,
            (_, _) =>
            {
                // -- the emptiness check is on nodes and faces,
                // before anything else is read.
                if (bsp[BspLump.Nodes].IsEmpty || bsp[BspLump.Faces].IsEmpty)
                {
                    throw new InvalidBspException("Empty map");
                }

                leaves = VisLeaves.From(bsp);
                (useRadius, radiusSquared) = DetermineRadius(bsp, context);

                if (useRadius)
                {
                    // MarkLeavesAsRadial. Every leaf, not just the
                    // ones the radius actually culled.
                    for (int leaf = 0; leaf < leaves.Count; leaf++)
                    {
                        leaves.AddFlags(leaf, LeafFlags.Radial);
                    }
                }

                state = new VisPortalState(portalCount);
            },
            new WorkQueueOptions { Stage = BaseStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

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
                VisBaseFlow flow = new(portals, state, useRadius, radiusSquared);
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

        if (context.Options.Trace is (int start, int stop))
        {
            return await TraceAsync(
                queue, portals, state, context, clusters, portalCount, rowBytes, start, stop,
                useRadius, radiusSquared, baseRays, cancellationToken).ConfigureAwait(false);
        }

        int[] sorted = SortPortals(state, portalCount, context.Options.NoSort);

        Begin(context, FlowStage, portalCount);

        int deepest = 0;
        // The base pass already ran — it runs for EVERY option set, -fast
        // included (stock dispatches it unconditionally) — so its
        // cast count seeds the record and survives to the result whatever the
        // flow stage below does or skips.
        VisWorkCounters work = new(Chains: 0, Candidates: 0, SeparatorClips: 0, BaseRays: baseRays);
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
            VisTightening tightening = new(state);
            await tightening.RunAsync(
                queue,
                workers,
                () => new VisPortalFlow(portals, state, context.Path),
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
                    VisPortalFlow flow = new(portals, state, context.Path);
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

        bsp.SetLump(BspLump.Visibility, visLump);
        bsp[BspLump.Leafs] = new BspLumpData(leaves.Bytes, leaves.Version, 0);
        bsp.SetLump(BspLump.LeafMinDistToWater, ToBytes(minDistanceToWater));

        return new VisResult(
            clusters, portalCount, rowBytes, pvs, pas, visLump.Length,
            totalVis, optimized, totalAudible, useRadius, radiusSquared, deepest, work, trace: null);
    }

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
            _ => new VisPortalFlow(portals, state, context.Path, sink),
            new WorkQueueOptions { Stage = FlowStage, Progress = context.Progress },
            cancellationToken).ConfigureAwait(false);

        return new VisResult(
            clusters, portalCount, rowBytes, [], [], 0, 0, 0, 0,
            useRadius, radiusSquared, 0,
            new VisWorkCounters(Chains: 0, Candidates: 0, SeparatorClips: 0, BaseRays: baseRays),
            sink.Points);
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
    private static (bool UseRadius, double RadiusSquared) DetermineRadius(
        BspData bsp,
        VisContext context)
    {
        if (context.Options.RadiusOverride is float given)
        {
            double wide = given;
            return (true, wide * wide);
        }

        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            if (!string.Equals(entity.ClassName, "env_fog_controller", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // -- the FIRST one wins, and a farz of exactly zero
            // means "no radius" rather than "a radius of zero".
            float far = ParseFloat(entity.Get("farz"));
            return far > 0f ? (true, (double)(far * far)) : (false, 0.0);
        }

        return (false, 0.0);
    }

    /// <summary>
    /// <c>atof</c> as <c>FloatForKey</c> uses it: a missing key reads as the
    /// empty string and the empty string reads as zero.
    /// </summary>
    private static float ParseFloat(string? value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : 0f;

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
