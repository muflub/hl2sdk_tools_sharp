//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Final;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Compile;

/// <summary>Diagnostic codes the chain itself raises, as opposed to its stages.</summary>
public static class MapCompilerCodes
{
    /// <summary>
    /// vbsp wrote no portal file (the map leaked, or has no sealed interior),
    /// so vvis did not run and vrad lit the unvised map. That is what a
    /// Hammer-style chain of the stock tools does: vvis fails to open the
    /// <c>.prt</c>(<c>LoadPortals</c>) and vrad runs regardless.
    /// </summary>
    public const string VisSkipped = "ALL0001";
}

/// <summary>
/// The whole compile, vbsp then vvis then vrad, in one process with the BSP
/// held in memory between the stages.
/// </summary>
/// <remarks>
/// <para>
/// Each stage is the same public entry point a host can call on its own --
/// <see cref="Bsp.Driver.Vbsp.CompileAsync(MapFile, VbspContext, CancellationToken)"/>,
/// <see cref="Vis.Vvis.ComputeAsync"/>, <see cref="Rad.Vrad.LightAsync(BspData, Rad.VradContext, CancellationToken)"/> -- so
/// this adds sequencing and nothing else, and a chain's output is by
/// construction what the three stages give when run one after another.
/// </para>
/// <para>
/// <b>The portal file is handed over as its text.</b> Stock vvis reads the
/// windings vbsp wrote with <c>%f</c>, so the
/// precision it computes with is six decimals, not the float vbsp held. So
/// the chain renders the <c>.prt</c> and parses it back, in memory: the text
/// is the contract between the two tools, and the chain keeps it, so that its
/// input to vvis is by construction what a chain through files reads. (Measured
/// in lane p7: on a fixture whose portal text does round, and on the whole
/// catalogue, whose portal coordinates are all integral, handing vvis the
/// floats instead changed no output byte. The step costs one text render.)
/// The BSP needs no such step: it is binary, and every lump vvis and vrad
/// read is the bytes vbsp would have written -- a fact compares the chain
/// with the three stages run through files.
/// </para>
/// <para>
/// A map with no portal file (leaked, or nothing sealed in it) gets no vvis
/// and is lit unvised, with a <see cref="MapCompilerCodes.VisSkipped"/>
/// warning. <c>-leaktest</c> on a leak stops the chain after vbsp, as stock
/// Vbsp stops.
/// </para>
/// </remarks>
public static class MapCompiler
{
    /// <summary>The progress stage reported at each stage boundary; <c>Done</c> counts finished stages out of three.</summary>
    public const string ChainStage = "all";

    /// <summary>
    /// Compiles a map: vbsp, vvis, vrad.
    /// </summary>
    /// <param name="request">The map, content, options and seams.</param>
    /// <param name="progress">Receives progress from every stage, or null.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The finished map and everything the chain had to say.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The vbsp options ask for <c>-onlyents</c> or <c>-onlyprops</c>, which
    /// update an existing BSP and are not a chain.
    /// </exception>
    /// <exception cref="MapCompileException">A stage met an unrecoverable map error.</exception>
    /// <exception cref="OperationCanceledException">The compile was cancelled.</exception>
    public static Task<CompileResult> CompileAsync(
        CompileRequest request,
        IProgress<CompileProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Vbsp.OnlyEnts || request.Vbsp.OnlyProps)
        {
            throw new ArgumentException(
                "-onlyents / -onlyprops update an existing BSP; they are not a full compile", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Resume the host on a fresh stack, not on the worker the compile
        // finished on: that worker's frames keep the compile's scratch alive
        // until they return (HostHandoff says why).
        return HostHandoff.ReturnAsync(RunAsync(request, progress, cancellationToken));
    }

    private static async Task<CompileResult> RunAsync(
        CompileRequest request,
        IProgress<CompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        // One thread pool for every stage of the run, unless the host lent one.
        if (request.Parallel.Pool is not null)
        {
            return await RunChainAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }

        using CompilePool pool = new(request.Parallel.MaxDegree);
        return await RunChainAsync(
            request with { Parallel = request.Parallel with { Pool = pool } }, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<CompileResult> RunChainAsync(
        CompileRequest request,
        IProgress<CompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        string name = request.Source.Name;
        Chain chain = new(request);

        // The store sees this compile as in flight until it returns, so a GC
        // run by another compile on the same store waits for a quiet moment
        // rather than deleting what this one may be reading or about to name.
        using IDisposable? storeRun = request.Cache?.BeginRun();

        // Cancels whatever runs ahead of the chain (the early vvis flow, the
        // early vrad load) when the chain itself stops.
        using CancellationTokenSource ahead = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TaskCompletionSource<PortalFile?> portalsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        VbspContext vbspContext = new(request.Vbsp, request.Content)
        {
            // mapbase: the file's base name, lowercased
#pragma warning disable CA1308 // strlwr
            MapBase = name.ToLowerInvariant(),
#pragma warning restore CA1308
            CollisionCooker = request.CollisionCooker,
            Parallelism = request.Parallel,
            CollisionModelCache = chain.CollisionCache,
            PortalFileReady = request.Overlap ? portals => portalsReady.TrySetResult(portals) : null,
        };

        progress?.Report(new CompileProgress(ChainStage, 0, 3));
        chain.Line($"ssmap all: {name}");

        long mark = request.Time.GetTimestamp();
        MapFile map = await request.Source.LoadAsync(vbspContext, cancellationToken).ConfigureAwait(false);
        mark = chain.Time("load", mark);

        CompileOutput output = request.Output;
        if (output.WritesFiles)
        {
            // delete portal and line files
            await chain.DeleteAsync(output.PathFor(name, ".prt"), cancellationToken).ConfigureAwait(false);
            await chain.DeleteAsync(output.PathFor(name, ".lin"), cancellationToken).ConfigureAwait(false);
        }

        VisContext visContext = new()
        {
            Options = request.Vvis,
            Parallelism = request.Parallel,
            Progress = progress,
        };

        // Overlap: vvis's flow starts the moment vbsp's world portal file is
        // final. Its radius is read from the loaded map now, before vbsp can
        // touch the entity list; Vvis.FinishAsync flows again if the finished
        // map's entity lump disagrees, so the guess costs time, never bytes.
        Task<EarlyFlow?>? flowTask = null;
        if (request.Overlap && request.Vvis.Trace is null)
        {
            VisRadius guess = VisRadius.FromEntities(
                [.. map.Entities.Select(static e => (e.ValueForKey("classname"), (string?)e.ValueForKey("farz")))],
                request.Vvis);
            flowTask = FlowWhenReadyAsync(portalsReady.Task, guess, visContext, ahead.Token);
        }

        // Everything a failure can leave behind is released on the way out,
        // whatever the exit. The early vvis flow is stopped and awaited: it
        // runs on the chain's pool, which may be the host's and outlive this
        // call. The rows staged but not yet published -- the vvis row, and
        // vrad's transfer row still packing in the background -- are stopped
        // and dropped, so they neither run on after the compile nor ride the
        // next compile's commit into the store.
        bool published = false;
        try
        {
            return await RunStagesAsync().ConfigureAwait(false);
        }
        catch
        {
            await AbandonAsync(ahead, portalsReady, flowTask).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!published)
            {
                await chain.AbandonStagingAsync().ConfigureAwait(false);
            }

            chain.Dispose();
        }

        async Task<CompileResult> RunStagesAsync()
        {
            VbspResult vbsp = await Vbsp.CompileAsync(map, vbspContext, cancellationToken).ConfigureAwait(false);

            // vbsp is done: a world with no model or a -leaktest stop never
            // signalled, so the result is the portal file's last word.
            portalsReady.TrySetResult(vbsp.Portals);

            mark = chain.Time("vbsp", mark);
            chain.Report(vbsp.Diagnostics);
            progress?.Report(new CompileProgress(ChainStage, 1, 3));

            if (vbsp.Leak is not null)
            {
                chain.Line("**** leaked ****");
                await chain.WriteAsync(output.PathFor(name, ".lin"), LeakTrace.Write(vbsp.Leak), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (vbsp.Bsp is not { } bsp)
            {
                await AbandonAsync(ahead, portalsReady, flowTask).ConfigureAwait(false);

                // A run that produced no BSP produced no products worth keeping:
                // the staged collision rows go back uncommitted.
                chain.CollisionCache?.DiscardPending();
                published = true;
                chain.Line("--- MAP LEAKED --- (-leaktest: nothing else is compiled)");
                await chain.FlushLogAsync(output.PathFor(name, ".log"), cancellationToken).ConfigureAwait(false);
                return chain.Result(name, vbsp, null, null);
            }

            // The collision stage finished inside Vbsp.CompileAsync: what it
            // staged is now visible to the next run. A
            // commit that throws is a lost cache, not a lost compile.
            if (chain.CollisionCache is { } collisionCache)
            {
                try
                {
                    await collisionCache.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    chain.Line($"cache: commit failed ({ex.Message}); this run's products were dropped");
                    collisionCache.DiscardPending();
                }
            }

            VradContext radContext = new()
            {
                Options = request.Vrad,
                MapName = name,
                Content = request.Content,
                Parallelism = request.Parallel,
                Progress = progress,
                Tracer = request.Tracer,
                GpuTracerFactory = request.TracerFactory,
                GpuPipelineDepth = request.GpuPipelineDepth,
                TransferCache = chain.TransferCache,
            };

            VisResult? vis = null;
            VradPreparation? prepared = null;
            RadResult rad;
            try
            {
                if (vbsp.Portals is { } portalFile)
                {
                    byte[] prt = portalFile.ToBytes(PortalLineEnding.CrLf);
                    await chain.WriteAsync(output.PathFor(name, ".prt"), prt, cancellationToken).ConfigureAwait(false);

                    // A light or entity edit leaves everything vvis reads alone: a hit
                    // restores vvis's lumps and skips the stage, overlapped or not.
                    long visStart = mark;
                    Cache.VvisStageCache? visCache = chain.VisCache is { } vc && Cache.VvisStageCache.Applies(request.Vvis) ? vc : null;
                    string? visKey = visCache is null ? null : Cache.VvisStageCache.InputDigest(prt, bsp, request.Vvis);

                    // The .prt's text is what vvis reads; see the remarks.
                    PortalFile? read = null;
                    if (visCache is not null)
                    {
                        read = await PortalFile.ParseAsync(prt, cancellationToken).ConfigureAwait(false);
                        vis = await visCache.TryGetAsync(visKey!, bsp, PortalSet.FromPortalFile(read).Count, cancellationToken)
                            .ConfigureAwait(false);
                        if (vis is not null)
                        {
                            // The early flow, if one started, is no longer needed.
                            await AbandonAsync(ahead, portalsReady, flowTask).ConfigureAwait(false);
                            chain.Line($"{read.ClusterCount,4} portalclusters");
                            chain.Line($"{read.Portals.Count,4} numportals");
                            mark = chain.Time("vvis", mark);
                        }
                    }

                    if (vis is null)
                    {
                        if (flowTask is null)
                        {
                            read ??= await PortalFile.ParseAsync(prt, cancellationToken).ConfigureAwait(false);
                            chain.Line($"{read.ClusterCount,4} portalclusters");
                            chain.Line($"{read.Portals.Count,4} numportals");

                            vis = await Vvis.ComputeAsync(bsp, PortalSet.FromPortalFile(read), visContext, cancellationToken)
                                .ConfigureAwait(false);
                            mark = chain.Time("vvis", mark);
                        }
                        else
                        {
                            (vis, prepared, mark) = await FinishOverlappedAsync(
                                chain, bsp, flowTask, visContext, radContext, ahead, mark, cancellationToken).ConfigureAwait(false);
                        }

                        // Stored after the lumps are in the map: the overlapped path
                        // writes them only once vrad's early load has read the map.
                        if (visCache is not null)
                        {
                            long costMs = (long)request.Time.GetElapsedTime(visStart).TotalMilliseconds;
                            await visCache.StoreAsync(visKey!, bsp, vis, costMs, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    chain.Line($"visdatasize:{vis.VisDataSize}");
                }
                else
                {
                    await AbandonAsync(ahead, portalsReady, flowTask).ConfigureAwait(false);
                    chain.Report(
                    [
                        new CompileDiagnostic(
                            MapCompilerCodes.VisSkipped,
                            DiagnosticSeverity.Warning,
                            "no portal file (the map leaked or has no sealed interior): vvis skipped, vrad lights the map unvised"),
                    ]);
                }

                progress?.Report(new CompileProgress(ChainStage, 2, 3));

                rad = prepared is null
                    ? await Vrad.LightAsync(bsp, radContext, cancellationToken).ConfigureAwait(false)
                    : await Vrad.LightAsync(bsp, prepared, radContext, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // A preparation vrad's early load made owns the tracer it built;
                // the lighting takes it over, and this releases it when the chain
                // fails first (a no-op once the lighting has taken it).
                prepared?.Dispose();
            }
            mark = chain.Time("vrad", mark);
            foreach (RadPassResult pass in rad.Passes)
            {
                string range = pass.Hdr ? "HDR" : "LDR";
                chain.Line($"[{range}] {pass.World.Faces} faces, {pass.World.DirectLights} direct lights, lightdata {pass.LightDataSize} bytes");
            }

            chain.Report(rad.Diagnostics);
            progress?.Report(new CompileProgress(ChainStage, 3, 3));

            if (output.WritesFiles)
            {
                // Sized once for the whole file and handed on without a copy:
                // see BspFile.SizeBound.
                long bound = BspFile.SizeBound(bsp);
                using MemoryStream buffer = new(bound <= Array.MaxLength ? (int)bound : 0);
                // The T3 seam: a resolved format beyond today's default is handed
                // the writer's format overload; the default asks for null and so
                // runs literally the legacy call T1 left here (corpus identity).
                BspWriteFormat? writeFormat = BspFormatWriter.ToWriteFormat(request.Vbsp.Format);
                if (writeFormat is { } format)
                {
                    await BspFile.SaveAsync(bsp, buffer, BspWriteMode.Canonical, format, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await BspFile.SaveAsync(bsp, buffer, BspWriteMode.Canonical, cancellationToken)
                        .ConfigureAwait(false);
                }
                await chain.WriteAsync(output.PathFor(name, ".bsp"), buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                chain.Time("write", mark);
            }

            // The vvis row and vrad's transfer row (staged in the background while
            // vrad finished), published once the map is written, like the collision rows.
            // The run's generation goes with them: the GC protects the rows of
            // the newest generations.
            Cache.CachePolicy policy = request.CachePolicy ?? Cache.CachePolicy.Default;
            bool committed = false;
            if (chain.TransferCache is not null && request.Cache is { } transferStore)
            {
                try
                {
                    await chain.TransferCache.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (transferStore.IsUsable)
                    {
                        if (policy.Writes)
                        {
                            await transferStore.RecordGenerationAsync(chain.Generation, cancellationToken).ConfigureAwait(false);
                        }

                        await transferStore.CommitAsync(cancellationToken).ConfigureAwait(false);
                        committed = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    chain.Line($"cache: commit failed ({ex.Message}); this run's products were dropped");
                    transferStore.DiscardStaged();
                }
            }

            published = true;

            // The store's own bound: a service that compiles into one store
            // forever must not grow it forever. A GC that fails is a store
            // left as it was, not a failed compile.
            if (committed && request.Cache is { } gcStore)
            {
                try
                {
                    Cache.CacheGcResult gc = await Cache.CacheCollector.CollectAsync(
                        gcStore, policy, request.Time, ownRuns: 1, cancellationToken).ConfigureAwait(false);
                    if (gc.Reclaimed || gc.BudgetSpent)
                    {
                        string spent = gc.BudgetSpent ? " (budget spent; the next commit continues)" : string.Empty;
                        chain.Line(string.Create(
                            CultureInfo.InvariantCulture,
                            $"cache: gc dropped {gc.RowsDropped} row(s), {gc.BlobsDropped} blob(s), {Cache.CacheRunReport.FormatBytes(gc.BytesFreed)}{spent}"));
                    }
                    else if (gc.Skipped is { } why && policy.GcOnCommit && policy.Writes)
                    {
                        chain.Line($"cache: gc deferred ({why})");
                    }

                    if (!gc.Vacuumed && policy.Vacuum == Cache.CacheVacuum.EveryRun && chain.TransferCache!.EvictedRows > 0)
                    {
                        await gcStore.VacuumAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    chain.Line($"cache: gc failed ({ex.Message}); the store was left as it was");
                }
            }

            // The store figures ride the report; the read happens
            // here, on the async path — Render itself never blocks. A store
            // that is not usable has no figures: it ran cold, and says so,
            // rather than reading as an empty store.
            Cache.CacheStats? cacheStats = null;
            bool storeUnusable = false;
            if (request.Cache is { } cacheStore)
            {
                if (!cacheStore.IsUsable)
                {
                    storeUnusable = true;
                }
                else
                {
                    try
                    {
                        cacheStats = await cacheStore.ReadStatsAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        chain.Line($"cache: store stats unavailable ({ex.Message})");
                    }
                }
            }

            // A compile with no store says the cache was off; the counters
            // exist either way, so it is the store that decides.
            chain.Line(Cache.CacheRunReport.Render(
                request.Cache is null ? null : chain.CacheCounters, cacheStats, storeUnusable));
            await chain.FlushLogAsync(output.PathFor(name, ".log"), cancellationToken).ConfigureAwait(false);
            return chain.Result(name, vbsp, vis, rad);
        }
    }

    /// <summary>The early flow's products: the portal text vvis read and the flowed portals.</summary>
    private sealed record EarlyFlow(PortalFile Read, VisFlow Flow);

    // Waits for vbsp's world portal file, then reads its text and flows it,
    // while vbsp goes on with the rest of the map. Null when there is none.
    private static async Task<EarlyFlow?> FlowWhenReadyAsync(
        Task<PortalFile?> portalsReady,
        VisRadius radius,
        VisContext context,
        CancellationToken cancellationToken)
    {
        PortalFile? portals = await portalsReady.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (portals is null)
        {
            return null;
        }

        // The .prt's text is what vvis reads, exactly as the serial chain.
        PortalFile read = await PortalFile.ParseAsync(portals.ToBytes(PortalLineEnding.CrLf), cancellationToken)
            .ConfigureAwait(false);
        VisFlow flow = await Vvis.FlowAsync(PortalSet.FromPortalFile(read), radius, context, cancellationToken)
            .ConfigureAwait(false);
        return new EarlyFlow(read, flow);
    }

    // The overlapped vvis tail: vrad's load starts beside vvis's finish, and
    // vvis's lumps go into the map only once that load (which reads the map)
    // is done. The vvis timing ends with vvis, not with the load.
    private static async Task<(VisResult Vis, VradPreparation? Prepared, long Mark)> FinishOverlappedAsync(
        Chain chain,
        BspData bsp,
        Task<EarlyFlow?> flowTask,
        VisContext visContext,
        VradContext radContext,
        CancellationTokenSource ahead,
        long mark,
        CancellationToken cancellationToken)
    {
        // A -luxeldensity below one edits the map in the load: that one waits.
        Task<VradPreparation>? prepareTask =
            LuxelDensity.Effective(radContext.Options.LuxelDensity) >= 1.0f
                ? Vrad.PrepareAsync(bsp, radContext, ahead.Token)
                : null;

        VisResult vis;
        VisLumps lumps;
        try
        {
            EarlyFlow early = await flowTask.ConfigureAwait(false)
                ?? throw new InvalidOperationException("vbsp returned a portal file it never signalled");
            chain.Line($"{early.Read.ClusterCount,4} portalclusters");
            chain.Line($"{early.Read.Portals.Count,4} numportals");

            (vis, lumps) = await Vvis.FinishCoreAsync(bsp, early.Flow, visContext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await ahead.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(prepareTask).ConfigureAwait(false);

            // The load may have finished before vvis failed (nothing in it
            // observes the cancellation once its tracer is built): its
            // preparation holds that tracer, and nobody else will light it.
            if (prepareTask is { IsCompletedSuccessfully: true })
            {
                (await prepareTask.ConfigureAwait(false)).Dispose();
            }

            throw;
        }

        mark = chain.Time("vvis", mark);

        VradPreparation? prepared = prepareTask is null
            ? null
            : await prepareTask.ConfigureAwait(false);
        try
        {
            lumps.WriteTo(bsp);
        }
        catch
        {
            // Not yet the caller's to release.
            prepared?.Dispose();
            throw;
        }

        return (vis, prepared, mark);
    }

    // Stops whatever ran ahead and waits for it, so no work outlives the chain.
    private static async Task AbandonAsync(
        CancellationTokenSource ahead,
        TaskCompletionSource<PortalFile?> portalsReady,
        Task<EarlyFlow?>? flowTask)
    {
        await ahead.CancelAsync().ConfigureAwait(false);
        portalsReady.TrySetResult(null);
        await ObserveAsync(flowTask).ConfigureAwait(false);
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The chain is already failing, or never needed this branch's answer.
        }
    }

    /// <summary>
    /// Builds the run's collision-cache seam when the request carries a store
    /// and a cooker; null otherwise.
    /// </summary>
    private static Cache.CollisionModelCache? NewCollisionCache(
        CompileRequest request,
        Cache.CacheRunCounters counters,
        long runStamp) =>
        request.Cache is { } store && request.CollisionCooker is { } cooker
            ? new Cache.CollisionModelCache(
                store,
                request.CachePolicy ?? Cache.CachePolicy.Default,
                cooker.CookerIdentity,
                request.ContextTags,
                counters)
            {
                CreatedAtMs = runStamp,
            }
            : null;

    /// <summary>The chain's running state: log, diagnostics, timings, files written.</summary>
    private sealed class Chain(CompileRequest request) : IDisposable
    {
        /// <summary>
        /// The run's one generation stamp (Unix milliseconds, from the
        /// request's clock): every row this run stages or renews carries it,
        /// and the final commit records it as the store's newest generation.
        /// </summary>
        private readonly long _runStamp = request.Time.GetUtcNow().ToUnixTimeMilliseconds();

        /// <summary>The generation id the final commit records: the run stamp as invariant text.</summary>
        public string Generation => _runStamp.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Stops and drops what this run staged but never published: the
        /// transfer row still packing in the background is cancelled and
        /// awaited, then the store's staged changes are discarded.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called on every exit that did not reach the final publish (a failure,
        /// a cancel). Without it the background staging holds the whole
        /// transfer set, keeps running on the default thread pool after the
        /// compile has returned, and stages into a store the host may hand the
        /// next compile, whose commit would then publish this compile's rows
        /// and its eviction of the older ones.
        /// </para>
        /// <para>
        /// The store's staging is one set per store, not per compile, so the
        /// discard also drops whatever a concurrent compile on the same store
        /// object had staged. That costs the other compile its rows (a miss on
        /// its next run, or a row whose blob is gone, which reads as corrupt
        /// and is rebuilt), never a wrong hit: every blob is content-addressed.
        /// </para>
        /// <para>
        /// Nothing here may replace the exception the chain is already
        /// leaving with, so a store that fails the discard is only logged.
        /// </para>
        /// </remarks>
        public async Task AbandonStagingAsync()
        {
            if (request.Cache is not { } store)
            {
                return;
            }

            if (_transferCache is { } transfers)
            {
                await transfers.AbandonAsync().ConfigureAwait(false);
            }

            try
            {
                store.DiscardStaged();
            }
            catch (Exception ex)
            {
                Line($"cache: discarding this run's staged rows failed ({ex.Message})");
            }
        }

        /// <summary>Releases the per-run seams that hold a cancellation source.</summary>
        public void Dispose() => _transferCache?.Dispose();

        private readonly List<string> _log = [];
        private readonly SourceSharp.MapTools.Compile.Cache.CacheRunCounters _cacheCounters = new();
        private readonly List<CompileDiagnostic> _diagnostics = [];
        private readonly List<CompileStageTiming> _timings = [];
        private readonly List<VPath> _written = [];

        public void Line(string line)
        {
            _log.Add(line);
            request.Log?.Write(DiagnosticSeverity.Info, line);
        }

        public void Report(IReadOnlyList<CompileDiagnostic> diagnostics)
        {
            foreach (CompileDiagnostic d in diagnostics)
            {
                _diagnostics.Add(d);
                _log.Add($"{d.Severity} {d.Code}: {d.Message}");
                request.Log?.Report(d);
            }
        }

        public long Time(string stage, long since)
        {
            long now = request.Time.GetTimestamp();
            TimeSpan elapsed = request.Time.GetElapsedTime(since, now);
            _timings.Add(new CompileStageTiming(stage, elapsed));
            Line(string.Create(CultureInfo.InvariantCulture, $"{stage} {elapsed.TotalSeconds:F1} seconds elapsed"));
            return now;
        }

        public async Task WriteAsync(VPath path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (request.Output.Files is not { } files)
            {
                return;
            }

            await files.ReplaceAsync(
                path,
                async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            _written.Add(path);
        }

        public async Task DeleteAsync(VPath path, CancellationToken cancellationToken)
        {
            IFileSystem files = request.Output.Files!;
            if (await files.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                await files.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }

        // Opens the log with "a": each compile appends.
        public async Task FlushLogAsync(VPath path, CancellationToken cancellationToken)
        {
            if (request.Output.Files is not { } files)
            {
                return;
            }

            StringBuilder text = new();
            if (await files.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                using System.Buffers.IMemoryOwner<byte> old =
                    await files.ReadAllAsync(path, cancellationToken).ConfigureAwait(false);
                text.Append(Encoding.UTF8.GetString(old.Memory.Span));
            }

            foreach (string line in _log)
            {
                text.Append(line).Append('\n');
            }

            await WriteAsync(path, Encoding.UTF8.GetBytes(text.ToString()), cancellationToken).ConfigureAwait(false);
        }

        private SourceSharp.MapTools.Compile.Cache.CollisionModelCache? _collisionCache;

        /// <summary>The per-model collision seam, built once per run, or null.</summary>
        public SourceSharp.MapTools.Compile.Cache.CollisionModelCache? CollisionCache =>
            _collisionCache ??= NewCollisionCache(request, _cacheCounters, _runStamp);

        private SourceSharp.MapTools.Compile.Cache.VvisStageCache? _visCache;

        /// <summary>The vvis stage seam, built once per run when the request carries a store, or null.</summary>
        public SourceSharp.MapTools.Compile.Cache.VvisStageCache? VisCache =>
            _visCache ??= request.Cache is { } store
                ? new SourceSharp.MapTools.Compile.Cache.VvisStageCache(
                    store, request.CachePolicy ?? Cache.CachePolicy.Default, request.ContextTags, _cacheCounters)
                {
                    CreatedAtMs = _runStamp,
                }
                : null;

        private SourceSharp.MapTools.Compile.Cache.StoreTransferCache? _transferCache;

        /// <summary>The bounce transfer seam, built once per run when the request carries a store, or null.</summary>
        public SourceSharp.MapTools.Compile.Cache.StoreTransferCache? TransferCache =>
            _transferCache ??= request.Cache is { } store
                ? new SourceSharp.MapTools.Compile.Cache.StoreTransferCache(
                    store,
                    request.CachePolicy ?? Cache.CachePolicy.Default,
                    request.ContextTags,
                    _cacheCounters,
                    request.Parallel.MaxDegree,
                    request.Source.Name)
                {
                    CreatedAtMs = _runStamp,
                }
                : null;

        /// <summary>The run's cache counters (report data, ruling Q12).</summary>
        public SourceSharp.MapTools.Compile.Cache.CacheRunCounters CacheCounters => _cacheCounters;

        public CompileResult Result(string name, VbspResult vbsp, VisResult? vis, RadResult? rad) =>
            new(name, vbsp, vis, rad, [.. _diagnostics], [.. _timings], [.. _written], [.. _log])
            {
                Cache = request.Cache is null ? null : _cacheCounters,
            };
    }
}
