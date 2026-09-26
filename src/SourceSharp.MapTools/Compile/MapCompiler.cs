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
using SourceSharp.MapTools.Rad;
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
/// <see cref="Vis.Vvis.ComputeAsync"/>, <see cref="Rad.Vrad.LightAsync"/> -- so
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
        return RunAsync(request, progress, cancellationToken);
    }

    private static async Task<CompileResult> RunAsync(
        CompileRequest request,
        IProgress<CompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        string name = request.Source.Name;
        Chain chain = new(request);

        VbspContext vbspContext = new(request.Vbsp, request.Content)
        {
            // mapbase: the file's base name, lowercased
#pragma warning disable CA1308 // strlwr
            MapBase = name.ToLowerInvariant(),
#pragma warning restore CA1308
            CollisionCooker = request.CollisionCooker,
            Parallelism = request.Parallel,
            CollisionModelCache = chain.CollisionCache,
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

        VbspResult vbsp = await Vbsp.CompileAsync(map, vbspContext, cancellationToken).ConfigureAwait(false);
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
            // A run that produced no BSP produced no products worth keeping:
            // the staged collision rows go back uncommitted.
            chain.CollisionCache?.DiscardPending();
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

        VisResult? vis = null;
        if (vbsp.Portals is { } portalFile)
        {
            byte[] prt = portalFile.ToBytes(PortalLineEnding.CrLf);
            await chain.WriteAsync(output.PathFor(name, ".prt"), prt, cancellationToken).ConfigureAwait(false);

            // The .prt's text is what vvis reads; see the remarks.
            PortalFile read = await PortalFile.ParseAsync(prt, cancellationToken).ConfigureAwait(false);
            chain.Line($"{read.ClusterCount,4} portalclusters");
            chain.Line($"{read.Portals.Count,4} numportals");

            VisContext visContext = new()
            {
                Options = request.Vvis,
                Parallelism = request.Parallel,
                Progress = progress,
            };

            PortalSet portalSet = PortalSet.FromPortalFile(read);

            // A light or entity edit leaves everything vvis reads alone.
            Cache.VvisStageCache? visCache = chain.VisCache is { } vc && Cache.VvisStageCache.Applies(request.Vvis) ? vc : null;
            string? visKey = visCache is null ? null : Cache.VvisStageCache.InputDigest(prt, bsp, request.Vvis);
            vis = visCache is null
                ? null
                : await visCache.TryGetAsync(visKey!, bsp, portalSet.Count, cancellationToken).ConfigureAwait(false);
            if (vis is null)
            {
                vis = await Vvis.ComputeAsync(bsp, portalSet, visContext, cancellationToken)
                    .ConfigureAwait(false);
                if (visCache is not null)
                {
                    long costMs = (long)request.Time.GetElapsedTime(mark).TotalMilliseconds;
                    await visCache.StoreAsync(visKey!, bsp, vis, costMs, cancellationToken).ConfigureAwait(false);
                }
            }

            mark = chain.Time("vvis", mark);
            chain.Line($"visdatasize:{vis.VisDataSize}");
        }
        else
        {
            chain.Report(
            [
                new CompileDiagnostic(
                    MapCompilerCodes.VisSkipped,
                    DiagnosticSeverity.Warning,
                    "no portal file (the map leaked or has no sealed interior): vvis skipped, vrad lights the map unvised"),
            ]);
        }

        progress?.Report(new CompileProgress(ChainStage, 2, 3));

        VradContext radContext = new()
        {
            Options = request.Vrad,
            MapName = name,
            Content = request.Content,
            Parallelism = request.Parallel,
            Progress = progress,
            Tracer = request.Tracer,
            GpuTracerFactory = request.TracerFactory,
            TransferCache = chain.TransferCache,
        };

        RadResult rad = await Vrad.LightAsync(bsp, radContext, cancellationToken).ConfigureAwait(false);
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
            using MemoryStream buffer = new();
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
            await chain.WriteAsync(output.PathFor(name, ".bsp"), buffer.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            chain.Time("write", mark);
        }

        // The vvis row and vrad's transfer row (staged in the background while
        // vrad finished), published once the map is written, like the collision rows.
        if (chain.TransferCache is not null && request.Cache is { } transferStore)
        {
            try
            {
                await chain.TransferCache.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (transferStore.IsUsable)
                {
                    await transferStore.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                chain.Line($"cache: commit failed ({ex.Message}); this run's products were dropped");
                transferStore.DiscardStaged();
            }
        }

        // The store figures ride the report; the read happens
        // here, on the async path — Render itself never blocks.
        Cache.CacheStats? cacheStats = null;
        if (request.Cache is { } cacheStore)
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

        chain.Line(Cache.CacheRunReport.Render(chain.CacheCounters, cacheStats));
        await chain.FlushLogAsync(output.PathFor(name, ".log"), cancellationToken).ConfigureAwait(false);
        return chain.Result(name, vbsp, vis, rad);
    }

    /// <summary>
    /// Builds the run's collision-cache seam when the request carries a store
    /// and a cooker; null otherwise.
    /// </summary>
    private static Cache.CollisionModelCache? NewCollisionCache(
        CompileRequest request,
        Cache.CacheRunCounters counters) =>
        request.Cache is { } store && request.CollisionCooker is { } cooker
            ? new Cache.CollisionModelCache(
                store,
                request.CachePolicy ?? Cache.CachePolicy.Default,
                cooker.CookerIdentity,
                request.ContextTags,
                counters)
            : null;

    /// <summary>The chain's running state: log, diagnostics, timings, files written.</summary>
    private sealed class Chain(CompileRequest request)
    {
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

        public async Task WriteAsync(VPath path, byte[] bytes, CancellationToken cancellationToken)
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
            _collisionCache ??= NewCollisionCache(request, _cacheCounters);

        private SourceSharp.MapTools.Compile.Cache.VvisStageCache? _visCache;

        /// <summary>The vvis stage seam, built once per run when the request carries a store, or null.</summary>
        public SourceSharp.MapTools.Compile.Cache.VvisStageCache? VisCache =>
            _visCache ??= request.Cache is { } store
                ? new SourceSharp.MapTools.Compile.Cache.VvisStageCache(
                    store, request.CachePolicy ?? Cache.CachePolicy.Default, request.ContextTags, _cacheCounters)
                : null;

        private SourceSharp.MapTools.Compile.Cache.StoreTransferCache? _transferCache;

        /// <summary>The bounce transfer seam, built once per run when the request carries a store, or null.</summary>
        public SourceSharp.MapTools.Compile.Cache.StoreTransferCache? TransferCache =>
            _transferCache ??= request.Cache is { } store
                ? new SourceSharp.MapTools.Compile.Cache.StoreTransferCache(
                    store, request.CachePolicy ?? Cache.CachePolicy.Default, request.ContextTags, _cacheCounters, request.Parallel.MaxDegree)
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
