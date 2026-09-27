//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text.Json;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using Microsoft.Diagnostics.Tracing.Analysis.JIT;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

using TraceLog = Microsoft.Diagnostics.Tracing.Etlx.TraceLog;
using TraceProcess = Microsoft.Diagnostics.Tracing.Analysis.TraceProcess;

namespace PerfTraceReport;

/// <summary>
/// Summarises one runtime-events trace (dotnet-trace's gc-verbose profile plus
/// the contention, thread pool, exception and JIT keywords) as JSON.
/// </summary>
/// <remarks>
/// <para>
/// Allocation figures come from AllocationTick events, which the runtime
/// raises about once per 100 KB allocated on a thread; each carries the bytes
/// since the last one, so the per-type sums are estimates whose error shrinks
/// with volume. They rank hot allocators well and should not be quoted to the
/// byte.
/// </para>
/// <para>
/// A "site" is the first frame on the stack in this tree's code (a
/// <c>SourceSharp.</c> method), which is where a change would be made; the
/// BCL frames above it (List growth, LINQ, string building) are named in the
/// <c>via</c> field so the mechanism is visible too.
/// </para>
/// </remarks>
internal static class Program
{
    private const string OurPrefix = "SourceSharp.";

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: PerfTraceReport <trace.nettrace> <out.json> [--top N]");
            return 2;
        }

        int top = 40;
        for (int i = 2; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--top")
            {
                top = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            }
        }

        Report report = Analyse(args[0], top);
        File.WriteAllText(args[1], JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static Report Analyse(string path, int top)
    {
        // TraceLog resolves the JIT'd methods on each stack from the trace's
        // rundown; it writes an .etlx beside the trace, removed afterwards.
        string etlx = TraceLog.CreateFromEventPipeDataFile(path);
        try
        {
            using TraceLog log = new(etlx);
            using TraceLogEventSource source = log.Events.GetSource();
            source.NeedLoadedDotNetRuntimes();

            Dictionary<string, Tally> byType = [];
            Dictionary<string, Tally> bySite = [];
            Dictionary<string, Tally> contentionSites = [];
            Dictionary<string, Tally> exceptions = [];
            // A thread waits on one lock at a time, and the stop event names no lock.
            Dictionary<int, (double At, string Site)> contentionOpen = [];
            double contentionMs = 0;
            long contentionCount = 0;
            int starvation = 0;
            int maxWorkers = 0;
            long lohBytes = 0;
            long pohBytes = 0;
            long allocBytes = 0;

            source.Clr.GCAllocationTick += data =>
            {
                long bytes = data.AllocationAmount64 > 0 ? data.AllocationAmount64 : data.AllocationAmount;
                allocBytes += bytes;
                if (data.AllocationKind == GCAllocationKind.Large)
                {
                    lohBytes += bytes;
                }
                else if ((int)data.AllocationKind == 2)
                {
                    pohBytes += bytes;
                }

                string type = string.IsNullOrEmpty(data.TypeName) ? "?" : data.TypeName;
                Add(byType, type, bytes, null);
                (string site, string via) = Site(data.CallStack());
                Add(bySite, $"{type} @ {site}", bytes, via);
            };

            source.Clr.ContentionStart += data =>
            {
                (string site, _) = Site(data.CallStack());
                contentionOpen[data.ThreadID] = (data.TimeStampRelativeMSec, site);
            };

            source.Clr.ContentionStop += data =>
            {
                contentionCount++;
                double ms = data.DurationNs > 0 ? data.DurationNs / 1e6 : 0;
                if (contentionOpen.Remove(data.ThreadID, out var open))
                {
                    if (ms == 0)
                    {
                        ms = data.TimeStampRelativeMSec - open.At;
                    }

                    Add(contentionSites, open.Site, 1, null, ms);
                }

                contentionMs += ms;
            };

            source.Clr.ThreadPoolWorkerThreadAdjustmentAdjustment += data =>
            {
                maxWorkers = Math.Max(maxWorkers, (int)data.NewWorkerThreadCount);
                if (data.Reason == ThreadAdjustmentReason.Starvation)
                {
                    starvation++;
                }
            };

            source.Clr.ExceptionStart += data =>
                Add(exceptions, data.ExceptionType + ": " + data.ExceptionMessage, 1, Site(data.CallStack()).Site);

            source.Process();

            // The compile is the process with the most GC activity (the trace
            // may also hold dotnet-trace's own launcher host).
            TraceProcess? proc = source.Processes()
                .Where(p => p.LoadedDotNetRuntime() is not null)
                .OrderByDescending(p => p.LoadedDotNetRuntime().GC.GCs.Count)
                .FirstOrDefault();
            TraceLoadedDotNetRuntime? runtime = proc?.LoadedDotNetRuntime();

            return new Report(
                Trace: Path.GetFileName(path),
                DurationS: log.SessionDuration.TotalSeconds,
                Gc: runtime is null ? null : GcSummary(runtime),
                Allocations: new AllocationSummary(
                    EstimatedBytes: allocBytes,
                    LargeObjectBytes: lohBytes,
                    PinnedObjectBytes: pohBytes,
                    ByType: Top(byType, top, t => t.Bytes),
                    BySite: Top(bySite, top, t => t.Bytes)),
                Contention: new ContentionSummary(contentionCount, contentionMs, Top(contentionSites, top, t => t.Ms)),
                ThreadPool: new ThreadPoolSummary(maxWorkers, starvation),
                Exceptions: Top(exceptions, top, t => t.Count),
                Jit: runtime is null ? null : JitSummary(runtime));
        }
        finally
        {
            File.Delete(etlx);
        }
    }

    private static GcReport GcSummary(TraceLoadedDotNetRuntime runtime)
    {
        List<TraceGC> gcs = [.. runtime.GC.GCs.Where(g => g.PauseDurationMSec >= 0)];
        GenReport Gen(int gen)
        {
            List<TraceGC> g = [.. gcs.Where(x => x.Generation == gen)];
            return new GenReport(
                gen,
                g.Count,
                g.Sum(x => x.PauseDurationMSec),
                g.Count == 0 ? 0 : g.Max(x => x.PauseDurationMSec),
                g.Count == 0 ? 0 : g.Average(x => x.PromotedMB));
        }

        return new GcReport(
            Count: gcs.Count,
            TotalPauseMs: gcs.Sum(x => x.PauseDurationMSec),
            MaxPauseMs: gcs.Count == 0 ? 0 : gcs.Max(x => x.PauseDurationMSec),
            PeakHeapAfterMb: gcs.Count == 0 ? 0 : gcs.Max(x => x.HeapSizeAfterMB),
            PeakHeapBeforeMb: gcs.Count == 0 ? 0 : gcs.Max(x => x.HeapSizeBeforeMB),
            ServerGc: gcs.Any(x => x.HeapCount > 1),
            Generations: [Gen(0), Gen(1), Gen(2)],
            Reasons: gcs.GroupBy(x => x.Reason.ToString())
                .Select(r => new NameCount(r.Key, r.Count()))
                .OrderByDescending(r => r.Count)
                .ToList(),
            Timeline: gcs.Select(x => new GcEvent(
                x.StartRelativeMSec / 1000, x.Generation, x.Reason.ToString(), x.Type.ToString(),
                x.PauseDurationMSec, x.HeapSizeBeforeMB, x.HeapSizeAfterMB)).ToList());
    }

    private static JitReport JitSummary(TraceLoadedDotNetRuntime runtime)
    {
        JITStats stats = runtime.JIT.Stats();
        return new JitReport(stats.Count, stats.TotalCpuTimeMSec, stats.TotalILSize);
    }

    // The first frame in this tree's code, and the frame just above it.
    private static (string Site, string Via) Site(TraceCallStack? stack)
    {
        string via = string.Empty;
        for (TraceCallStack? s = stack; s is not null; s = s.Caller)
        {
            string name = s.CodeAddress.FullMethodName;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name.StartsWith(OurPrefix, StringComparison.Ordinal))
            {
                return (Trim(name), via);
            }

            if (via.Length == 0)
            {
                via = Trim(name);
            }
        }

        return ("(no SourceSharp frame)", via);
    }

    // Drops the parameter list: the method name is enough to find it.
    private static string Trim(string name)
    {
        int paren = name.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? name : name[..paren];
    }

    private static void Add(Dictionary<string, Tally> map, string key, long bytesOrCount, string? via, double ms = 0)
    {
        map.TryGetValue(key, out Tally t);
        map[key] = new Tally(t.Count + 1, t.Bytes + bytesOrCount, t.Ms + ms, t.Via ?? via);
    }

    private static List<Row> Top(Dictionary<string, Tally> map, int top, Func<Tally, double> by) =>
        [.. map.OrderByDescending(kv => by(kv.Value)).Take(top)
            .Select(kv => new Row(kv.Key, kv.Value.Count, kv.Value.Bytes, kv.Value.Ms, kv.Value.Via))];

    private readonly record struct Tally(long Count, long Bytes, double Ms, string? Via);

    private sealed record Row(string Name, long Count, long Bytes, double Ms, string? Via);

    private sealed record NameCount(string Name, int Count);

    private sealed record GenReport(int Generation, int Count, double PauseMs, double MaxPauseMs, double MeanPromotedMb);

    private sealed record GcEvent(double AtS, int Gen, string Reason, string Type, double PauseMs, double HeapBeforeMb, double HeapAfterMb);

    private sealed record GcReport(
        int Count, double TotalPauseMs, double MaxPauseMs, double PeakHeapAfterMb, double PeakHeapBeforeMb, bool ServerGc,
        List<GenReport> Generations, List<NameCount> Reasons, List<GcEvent> Timeline);

    private sealed record AllocationSummary(long EstimatedBytes, long LargeObjectBytes, long PinnedObjectBytes, List<Row> ByType, List<Row> BySite);

    private sealed record ContentionSummary(long Count, double TotalMs, List<Row> BySite);

    private sealed record ThreadPoolSummary(int MaxWorkers, int StarvationAdjustments);

    private sealed record JitReport(long Methods, double CpuMs, long IlBytes);

    private sealed record Report(
        string Trace, double DurationS, GcReport? Gc, AllocationSummary Allocations, ContentionSummary Contention,
        ThreadPoolSummary ThreadPool, List<Row> Exceptions, JitReport? Jit);
}
