//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapCompile;

/// <summary>
/// One timed compile, as the bench harness measured it (the Phase 12
/// instrument). A run the warm-up excluded carries <see cref="Timed"/> false:
/// the aggregate never counts it, and the reason it exists — the JIT warming
/// the code before the first number is taken — is Phase 5's rule, not an
/// outlier to throw away.
/// </summary>
/// <param name="Cell">The cell this sample belongs to: map|arm|stage|options|threads|cache.</param>
/// <param name="Run">Zero-based timed-run index; negative for warm-up runs.</param>
/// <param name="Timed">False for a warm-up run.</param>
/// <param name="Ok">Whether the compile completed.</param>
/// <param name="WallSeconds">Wall time of the compile.</param>
/// <param name="CpuSeconds">Process CPU time the compile burned.</param>
/// <param name="PeakRssBytes">
/// <c>VmHWM</c> for the process. Timed runs of a cell share a process, so this
/// is the kernel's process-life high-water mark, not a per-run peak; the
/// report says so beside every RSS column.
/// </param>
/// <param name="GcPauseSeconds">
/// <see cref="GC.GetTotalPauseDuration"/> delta across the run — the managed
/// arm's GC cost, the mechanism Phase 5's instrument quotes.
/// </param>
/// <param name="Stages">Per-stage wall, as <c>stage|seconds</c> (chain runs: load/vbsp/vvis/vrad/write).</param>
/// <param name="Outputs">The written files as <c>path|sha256</c>.</param>
/// <param name="CacheReused">Collision models the cache replayed (0 without a cache).</param>
/// <param name="CacheCooked">Collision models cooked (0 without a cache).</param>
/// <param name="Failure">The failure line, when <see cref="Ok"/> is false.</param>
/// <param name="CacheStagesReused">Whole stages the cache replayed (e.g. <c>vvis</c>, <c>vrad.transfers</c>).</param>
/// <param name="CacheStagesComputed">Cacheable stages that missed and were computed.</param>
/// <param name="CacheSavedMs">The cache's own estimate of the time it saved (models and stages).</param>
/// <param name="CacheBytesStored">Bytes the run staged into the store.</param>
public sealed record BenchSample(
    string Cell,
    int Run,
    bool Timed,
    bool Ok,
    double WallSeconds,
    double CpuSeconds,
    long PeakRssBytes,
    double GcPauseSeconds,
    IReadOnlyList<string> Stages,
    IReadOnlyList<string> Outputs,
    int CacheReused,
    int CacheCooked,
    string? Failure,
    IReadOnlyList<string>? CacheStagesReused = null,
    IReadOnlyList<string>? CacheStagesComputed = null,
    long CacheSavedMs = 0,
    long CacheBytesStored = 0)
{
    /// <summary>Writes the sample as one JSON line (the harness's raw ledger).
    /// Hand-rolled: the AOT arm runs with reflection-based serialization
    /// disabled, and this line is the ONE format every arm's runs share.</summary>
    /// <returns>The JSON line.</returns>
    public string ToJsonLine()
    {
        StringBuilder j = new("{\"Cell\":");
        Str(j, Cell);
        j.Append(",\"Run\":").Append(Run.ToString(CultureInfo.InvariantCulture));
        j.Append(",\"Timed\":").Append(Timed ? "true" : "false");
        j.Append(",\"Ok\":").Append(Ok ? "true" : "false");
        j.Append(",\"WallSeconds\":").Append(WallSeconds.ToString("R", CultureInfo.InvariantCulture));
        j.Append(",\"CpuSeconds\":").Append(CpuSeconds.ToString("R", CultureInfo.InvariantCulture));
        j.Append(",\"PeakRssBytes\":").Append(PeakRssBytes.ToString(CultureInfo.InvariantCulture));
        j.Append(",\"GcPauseSeconds\":").Append(GcPauseSeconds.ToString("R", CultureInfo.InvariantCulture));
        j.Append(",\"Stages\":[");
        Strs(j, Stages);
        j.Append("],\"Outputs\":[");
        Strs(j, Outputs);
        j.Append("],\"CacheReused\":").Append(CacheReused.ToString(CultureInfo.InvariantCulture));
        j.Append(",\"CacheCooked\":").Append(CacheCooked.ToString(CultureInfo.InvariantCulture));
        j.Append(",\"Failure\":");
        Str(j, Failure ?? string.Empty);
        j.Append(",\"CacheStagesReused\":[");
        Strs(j, CacheStagesReused ?? []);
        j.Append("],\"CacheStagesComputed\":[");
        Strs(j, CacheStagesComputed ?? []);
        j.Append("],\"CacheSavedMs\":").Append(CacheSavedMs.ToString(CultureInfo.InvariantCulture));
        j.Append(",\"CacheBytesStored\":").Append(CacheBytesStored.ToString(CultureInfo.InvariantCulture));
        j.Append('}');
        return j.ToString();
    }

    /// <summary>Reads <see cref="ToJsonLine"/>'s form back.</summary>
    /// <param name="line">One JSON line.</param>
    /// <returns>The sample.</returns>
    /// <exception cref="InvalidDataException">The line is not a sample.</exception>
    public static BenchSample FromJsonLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement r = doc.RootElement;
        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty : throw new InvalidDataException("bench field " + name);
        static double Real(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.TryGetDouble(out double d)
                ? d : throw new InvalidDataException("bench field " + name);
        static int Whole(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n)
                ? n : throw new InvalidDataException("bench field " + name);
        static string[] Arr(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException(name))]
                : throw new InvalidDataException("bench field " + name);
        // Ledgers written before the stage caches lack these; they read as none.
        static string[] OptArr(JsonElement e, string name) =>
            e.TryGetProperty(name, out _) ? Arr(e, name) : [];
        static long OptLong(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.TryGetInt64(out long n) ? n : 0;
        bool ok = r.TryGetProperty("Ok", out JsonElement okv) && okv.ValueKind == JsonValueKind.True;
        bool timed = r.TryGetProperty("Timed", out JsonElement tv) && tv.ValueKind == JsonValueKind.True;
        string? failure = r.TryGetProperty("Failure", out JsonElement f) && f.ValueKind == JsonValueKind.String
            ? f.GetString() : null;
        return new BenchSample(
            Text(r, "Cell"), Whole(r, "Run"), timed, ok,
            Real(r, "WallSeconds"), Real(r, "CpuSeconds"),
            r.TryGetProperty("PeakRssBytes", out JsonElement p) && p.TryGetInt64(out long rss) ? rss : 0,
            Real(r, "GcPauseSeconds"), Arr(r, "Stages"), Arr(r, "Outputs"),
            Whole(r, "CacheReused"), Whole(r, "CacheCooked"), failure,
            OptArr(r, "CacheStagesReused"), OptArr(r, "CacheStagesComputed"),
            OptLong(r, "CacheSavedMs"), OptLong(r, "CacheBytesStored"));
    }

    private static void Str(StringBuilder j, string value)
    {
        j.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': j.Append("\\\""); break;
                case '\\': j.Append("\\\\"); break;
                case '\n': j.Append("\\n"); break;
                case '\r': j.Append("\\r"); break;
                case '\t': j.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        j.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        j.Append(c);
                    }

                    break;
            }
        }

        j.Append('"');
    }

    private static void Strs(StringBuilder j, IReadOnlyList<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                j.Append(',');
            }

            Str(j, values[i]);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Records compare collections by reference; the ledger's whole
    /// contract is that a line means the same sample whatever read it, so
    /// equality here is equality of the wire form.</remarks>
    public bool Equals(BenchSample? other) => other is not null && ToJsonLine() == other.ToJsonLine();

    /// <inheritdoc/>
    public override int GetHashCode() => ToJsonLine().GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// One median row of the matrix (the CSV's unit): a cell's timed samples,
/// reduced. Empty numeric fields mean "not measured here" (a single-tool arm
/// has no per-stage columns); a refused cell carries no runs and an
/// <c>r:</c>-prefixed note instead of them rather than a fake zero.
/// </summary>
/// <param name="Map">The map's base name.</param>
/// <param name="Arm">stock_x64, managed_jit or managed_aot.</param>
/// <param name="Stage">chain, vbsp, vvis or vrad.</param>
/// <param name="Options">The option-set label (default, -fast, -onlyents, ...).</param>
/// <param name="Threads">The <c>-threads</c> the cell ran at, or 0 when the refusal precedes a thread count.</param>
/// <param name="Cache">off, cold or warm (managed arms; native arms carry off).</param>
/// <param name="Rc">ok, refused or failed.</param>
/// <param name="N">Timed runs the medians are of.</param>
/// <param name="WallMed">Median wall seconds.</param>
/// <param name="WallMin">Fastest timed wall.</param>
/// <param name="WallMax">Slowest timed wall — with <see cref="WallMin"/> the row's spread.</param>
/// <param name="StageWalls">The stage medians (load,vbsp,vvis,vrad,write); empty for single-tool arms.</param>
/// <param name="CpuMed">Median process CPU seconds.</param>
/// <param name="PeakRssKb">Median of the runs' peak-RSS readings, kB.</param>
/// <param name="GcPauseMed">Median GC pause total, seconds (managed arms).</param>
/// <param name="Reused">Collision models the arm replayed (cache columns).</param>
/// <param name="Cooked">Collision models cooked.</param>
/// <param name="BspSha">The produced .bsp's sha256, or empty.</param>
/// <param name="Notes">Flags: divergence, failure counts, refusal reasons.</param>
public sealed record BenchCellResult(
    string Map,
    string Arm,
    string Stage,
    string Options,
    int Threads,
    string Cache,
    string Rc,
    int N,
    double WallMed,
    double WallMin,
    double WallMax,
    IReadOnlyList<double> StageWalls,
    double CpuMed,
    long PeakRssKb,
    double GcPauseMed,
    int Reused,
    int Cooked,
    string BspSha,
    string Notes)
{
    /// <summary>The row as CSV text (quoting what needs quoting).</summary>
    /// <returns>One CSV line.</returns>
    public string ToCsv()
    {
        List<string> fields =
        [
            Map, Arm, Stage, Options, Threads.ToString(CultureInfo.InvariantCulture), Cache, Rc,
            N.ToString(CultureInfo.InvariantCulture),
            Num(WallMed), Num(WallMin), Num(WallMax),
            StageWalls.Count == 0 ? string.Empty : string.Join(';', StageWalls.Select(Num)),
            Num(CpuMed),
            PeakRssKb.ToString(CultureInfo.InvariantCulture),
            Num(GcPauseMed),
            Reused.ToString(CultureInfo.InvariantCulture), Cooked.ToString(CultureInfo.InvariantCulture),
            BspSha, Notes,
        ];
        return string.Join(',', fields.Select(Escape));
    }

    /// <summary>Reads <see cref="ToCsv"/>'s form back.</summary>
    /// <param name="line">One CSV line (not the header).</param>
    /// <returns>The row.</returns>
    /// <exception cref="InvalidDataException">The line is not <see cref="BenchCommand.CsvHeader"/>'s shape.</exception>
    public static BenchCellResult FromCsv(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        List<string> f = Split(line);
        if (f.Count != 19)
        {
            throw new InvalidDataException($"bench row: {f.Count} fields, want 19: {line}");
        }

        return new BenchCellResult(
            f[0], f[1], f[2], f[3], int.Parse(f[4], CultureInfo.InvariantCulture), f[5], f[6],
            f[7].Length == 0 ? 0 : int.Parse(f[7], CultureInfo.InvariantCulture),
            Num(f[8]), Num(f[9]), Num(f[10]),
            f[11].Length == 0 ? [] : f[11].Split(';').Select(Num).ToArray(),
            Num(f[12]),
            f[13].Length == 0 ? 0 : long.Parse(f[13], CultureInfo.InvariantCulture),
            Num(f[14]),
            int.Parse(f[15], CultureInfo.InvariantCulture), int.Parse(f[16], CultureInfo.InvariantCulture),
            f[17], f[18]);
    }

    /// <summary>The refused-cell row: no numbers, one quoted reason.</summary>
    /// <param name="map">The map.</param>
    /// <param name="arm">The arm that cannot run it.</param>
    /// <param name="stage">The stage.</param>
    /// <param name="options">The option set.</param>
    /// <param name="threads">The thread count the refusal happened at, or 0.</param>
    /// <param name="cache">The cache mode.</param>
    /// <param name="reason">Why there is no number, in the words of the tool that refused.</param>
    /// <returns>The row.</returns>
    public static BenchCellResult Refused(
        string map, string arm, string stage, string options, int threads, string cache, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new BenchCellResult(map, arm, stage, options, threads, cache, "refused", 0,
            0, 0, 0, [], 0, 0, 0, 0, 0, string.Empty, "r:" + reason);
    }

    /// <inheritdoc/>
    /// <remarks>The CSV's contract is that a line means the same row whatever
    /// read it, so equality here is equality of the wire form.</remarks>
    public bool Equals(BenchCellResult? other) => other is not null && ToCsv() == other.ToCsv();

    /// <inheritdoc/>
    public override int GetHashCode() => ToCsv().GetHashCode(StringComparison.Ordinal);

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static double Num(string text) =>
        text.Length == 0 ? 0 : double.Parse(text, CultureInfo.InvariantCulture);

    private static string Escape(string field) =>
        field.AsSpan().IndexOfAny(',', '"', '\n') >= 0
            ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : field;

    private static List<string> Split(string line)
    {
        List<string> fields = [];
        StringBuilder current = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}

/// <summary>
/// <c>ssmap bench</c>: the Phase 12 measuring instrument. It runs one map at
/// one option set and thread count, N timed runs
/// after a warm-up run the timings exclude, and writes a raw JSON ledger;
/// <c>bench summarize</c> reduces ledgers — from any arm, since the native
/// drivers emit the same rows — to median CSV with a <c>#</c>-comment
/// provenance header.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it measures and what it does not.</b> The wine arms (the stock
/// x64 binaries) are not this command's business — no managed process starts
/// wine honestly; the series drivers time them with
/// <c>/usr/bin/time</c> and emit rows in exactly
/// <see cref="CsvHeader"/>'s shape so the matrix stays one table. This command
/// owns the managed arms, in-process through the product's own seams
/// (<see cref="AllCommand.WithBackendsAsync"/> and
/// <see cref="MapCompiler.CompileAsync"/> for the chain, the same
/// <c>RunAsync</c> the CLI dispatch calls for the single-tool stages), plus the
/// one-process mode the AOT-vs-JIT startup rows use
/// (<see cref="ChildSwitch"/>) — where process start is inside the caller's
/// wall because that IS the number.
/// </para>
/// <para>
/// <b>One run at a time.</b> The harness never parallelises cells or runs —
/// <c>matched-power-to-see-throttling</c> means the box must be idle for every
/// number, and the drivers take everything through <c>run-capped</c>. Within a
/// cell the harness loops the runs itself: that IS one run at a time.
/// </para>
/// <para>
/// <b>Same work, same map (Phase 5 rule 2b).</b> Every timed run mounts the
/// game and cooks collision exactly as the product CLI does, and the warm-up
/// run is the same command line as the timed ones. Stage timings come from
/// <see cref="CompileResult.Timings"/>; a single-tool arm reports the stage it
/// is, which is also how the stock arms are run — three separate tools — and
/// what the chain row splits for the per-stage columns.
/// </para>
/// </remarks>
public static class BenchCommand
{
    /// <summary>The child switch: run exactly one compile and print its metric JSON on stdout.</summary>
    public const string ChildSwitch = "--child";

    /// <summary>The CSV header every arm's rows follow.</summary>
    public const string CsvHeader =
        "map,arm,stage,options,threads,cache,rc,n,wall_med_s,wall_min_s,wall_max_s,"
        + "wall_stages_s,cpu_med_s,peak_rss_kb,gc_pause_med_s,reused,cooked,bsp_sha256,notes";

    /// <summary>The chain's stage names, in <see cref="BenchCellResult.StageWalls"/> order.</summary>
    public static IReadOnlyList<string> StageNames { get; } = ["load", "vbsp", "vvis", "vrad", "write"];

    /// <summary>Runs <c>ssmap bench</c>.</summary>
    /// <param name="disk">The host file system, rooted at <c>/</c>.</param>
    /// <param name="searchRoots">Where game installs are (cooker discovery, mounts).</param>
    /// <param name="args">The arguments after <c>bench</c>.</param>
    /// <param name="output">Where the per-run summary lines go.</param>
    /// <param name="cancellationToken">Cancels the series.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        PhysicalFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (args.Count > 0 && args[0] == "summarize")
        {
            return Summarize([.. args.Skip(1)], output);
        }

        if (args.Count > 0 && args[0] == ChildSwitch)
        {
            return await ChildAsync([.. args.Skip(1)], cancellationToken).ConfigureAwait(false);
        }

        return await RunSeriesAsync(disk, searchRoots, args, output, cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- pure math

    /// <summary>The median of a run of measurements.</summary>
    /// <param name="values">The values; at least one.</param>
    /// <returns>The middle value; the mean of the two middles when the count is even.</returns>
    /// <exception cref="ArgumentException">No values.</exception>
    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("median of nothing", nameof(values));
        }

        double[] sorted = [.. values];
        Array.Sort(sorted);
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }

    /// <summary>Parallel efficiency: how much of the ideal <c>N×</c> the thread count reached.</summary>
    /// <param name="oneThread">The <c>-threads 1</c> time.</param>
    /// <param name="nThread">The <c>-threads N</c> time.</param>
    /// <param name="threads">The N.</param>
    /// <returns><c>(T1/TN)/N</c>; 1.0 is perfect scaling.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A non-positive time or thread count.</exception>
    public static double ScalingEfficiency(double oneThread, double nThread, int threads)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nThread);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads);
        return oneThread / nThread / threads;
    }

    /// <summary>Where one run's collision-model store lives for a cache mode.</summary>
    /// <param name="cacheBase">The lane's store root.</param>
    /// <param name="cell">The cell id, cache component included.</param>
    /// <param name="mode">cold, warm, or anything else for no cache.</param>
    /// <param name="run">The timed-run index (cold isolates by it).</param>
    /// <param name="timed">False for a warm-up run.</param>
    /// <returns>The store directory, or null when the mode is neither cold nor warm.</returns>
    /// <remarks>
    /// The plan's rule, verbatim: every cold run gets a fresh store no run has
    /// ever used, and a warm run reuses precisely the store its cell's cold arm
    /// built — cold run 0's store, never one another cell borrowed (which is
    /// why the warm path names the COLD sibling cell). A warm-up run of a cold
    /// arm warms the JIT and nothing else: pre-filling the store the timed cold
    /// run is meant to cook into would turn the cold number into a lie.
    /// </remarks>
    public static string? StoreDirFor(string cacheBase, string cell, string mode, int run, bool timed)
    {
        ArgumentNullException.ThrowIfNull(cacheBase);
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(mode);

        return mode switch
        {
            "cold" when timed => Path.Combine(cacheBase, cell, "r" + run.ToString(CultureInfo.InvariantCulture)),
            "cold" => Path.Combine(cacheBase, cell, "warmup"),
            "warm" => Path.Combine(cacheBase, ColdSibling(cell), "r0"),
            _ => null,
        };
    }

    /// <summary>The cold-sibling cell id a warm cell borrows its store from.</summary>
    /// <param name="cell">The warm cell id.</param>
    /// <returns>The same id with its cache component set to <c>cold</c>.</returns>
    public static string ColdSibling(string cell)
    {
        (string map, string arm, string stage, string options, int threads, string cache) = ParseCell(cell);
        return string.Join('|', map, arm, stage, options, threads.ToString(CultureInfo.InvariantCulture), "cold");
    }

    /// <summary>Splits a cell id into its six identity parts.</summary>
    /// <param name="cell">The id: <c>map|arm|stage|options|threads|cache</c>.</param>
    /// <returns>The parts.</returns>
    /// <exception cref="InvalidDataException">Not six pipe-separated parts, or threads is not a number.</exception>
    public static (string Map, string Arm, string Stage, string Options, int Threads, string Cache) ParseCell(
        string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        string[] p = cell.Split('|');
        if (p.Length != 6 || !int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int threads))
        {
            throw new InvalidDataException("bench cell id: " + cell);
        }

        return (p[0], p[1], p[2], p[3], threads, p[5]);
    }

    /// <summary>The median rows a raw ledger reduces to: one row per cell.</summary>
    /// <param name="samples">The samples, in any order; warm-up samples contribute only a last-ditch failure line.</param>
    /// <returns>One row per cell, in the ledger's order of first appearance.</returns>
    public static IReadOnlyList<BenchCellResult> Reduce(IReadOnlyList<BenchSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        List<string> order = [];
        Dictionary<string, List<BenchSample>> byCell = [];
        foreach (BenchSample s in samples)
        {
            if (!byCell.TryGetValue(s.Cell, out List<BenchSample>? list))
            {
                byCell[s.Cell] = list = [];
                order.Add(s.Cell);
            }

            list.Add(s);
        }

        List<BenchCellResult> rows = [];
        foreach (string cell in order)
        {
            (string map, string arm, string stage, string options, int threads, string cache) = ParseCell(cell);
            List<BenchSample> all = byCell[cell];
            List<BenchSample> timed = [.. all.Where(static s => s.Timed)];
            if (timed.Count == 0)
            {
                rows.Add(FailRow(cell, all[0].Failure ?? "no timed run"));
                continue;
            }

            List<BenchSample> ok = [.. timed.Where(static s => s.Ok)];
            if (ok.Count == 0)
            {
                rows.Add(FailRow(cell, timed[0].Failure ?? "compile failed"));
                continue;
            }

            List<string> notes = [];
            if (ok.Count < timed.Count)
            {
                notes.Add($"{timed.Count - ok.Count}/{timed.Count} timed run(s) failed");
            }

            string[] shas = [.. ok
                .Select(s => s.Outputs.FirstOrDefault(o => o.Contains('|', StringComparison.Ordinal)) is { } o
                    ? o[(o.IndexOf('|') + 1)..]
                    : string.Empty)
                .Where(sha => sha.Length == 64)
                .Distinct()];
            if (shas.Length > 1)
            {
                notes.Add("sha-divergent across timed runs");
            }

            rows.Add(new BenchCellResult(
                map, arm, stage, options, threads, cache, "ok", ok.Count,
                Median([.. ok.Select(s => s.WallSeconds)]),
                ok.Min(s => s.WallSeconds), ok.Max(s => s.WallSeconds),
                StageMedians(ok),
                Median([.. ok.Select(s => s.CpuSeconds)]),
                (long)(Median([.. ok.Select(s => (double)s.PeakRssBytes)]) / 1024.0),
                Median([.. ok.Select(s => s.GcPauseSeconds)]),
                ok[^1].CacheReused, ok[^1].CacheCooked,
                shas.Length == 0 ? string.Empty : shas[0],
                string.Join("; ", notes)));
        }

        return rows;
    }

    /// <summary>The CSV for a reduced ledger: <c>#</c>-comment provenance lines, then header and rows.</summary>
    /// <param name="provenance">Key/value stamps: commit, box, builds, date.</param>
    /// <param name="rows">The reduced cells.</param>
    /// <returns>The file text.</returns>
    public static string Csv(IReadOnlyList<(string Key, string Value)> provenance, IReadOnlyList<BenchCellResult> rows)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(rows);

        StringBuilder text = new();
        foreach ((string key, string value) in provenance)
        {
            text.Append("# ").Append(key).Append(": ").Append(value).Append('\n');
        }

        text.Append(CsvHeader).Append('\n');
        foreach (BenchCellResult row in rows)
        {
            text.Append(row.ToCsv()).Append('\n');
        }

        return text.ToString();
    }

    private static BenchCellResult FailRow(string cell, string reason)
    {
        (string map, string arm, string stage, string options, int threads, string cache) = ParseCell(cell);
        return new BenchCellResult(map, arm, stage, options, threads, cache, "failed", 0,
            0, 0, 0, [], 0, 0, 0, 0, 0, string.Empty, "r:" + reason);
    }

    private static IReadOnlyList<double> StageMedians(IReadOnlyList<BenchSample> ok)
    {
        // A stage a map never reaches (no portals: no vvis row) leaves its
        // column empty; NaN says "not measured" in the CSV, not zero.
        double[] medians = new double[StageNames.Count];
        for (int i = 0; i < StageNames.Count; i++)
        {
            string prefix = StageNames[i] + "|";
            double[] column = [.. ok
                .Select(s => s.Stages.FirstOrDefault(st => st.StartsWith(prefix, StringComparison.Ordinal)))
                .Where(static st => st is not null)
                .Select(st => double.Parse(st!.AsSpan(st!.IndexOf('|') + 1), CultureInfo.InvariantCulture))];
            medians[i] = column.Length == 0 ? double.NaN : Median(column);
        }

        return medians;
    }

    // ---------------------------------------------------------------- summarize

    private static int Summarize(IReadOnlyList<string> args, TextWriter output)
    {
        string? input = null;
        string? outPath = null;
        List<(string Key, string Value)> provenance = [];
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--in" when i + 1 < args.Count:
                    input = args[++i];
                    break;
                case "--out" when i + 1 < args.Count:
                    outPath = args[++i];
                    break;
                case "--prov" when i + 1 < args.Count:
                {
                    string kv = args[++i];
                    int eq = kv.IndexOf('=', StringComparison.Ordinal);
                    if (eq <= 0)
                    {
                        output.WriteLine($"bench: --prov wants key=value, got {kv}");
                        return Program.ExitUsage;
                    }

                    provenance.Add((kv[..eq], kv[(eq + 1)..]));
                    break;
                }

                default:
                    output.WriteLine($"bench: unknown summarize flag {args[i]}");
                    return Program.ExitUsage;
            }
        }

        if (input is null)
        {
            output.WriteLine("usage: ssmap bench summarize --in <jsonl> [--out <csv>] [--prov key=value]...");
            return Program.ExitUsage;
        }

        List<BenchSample> samples = [.. File.ReadLines(input)
            .Where(static l => l.Trim().Length > 0)
            .Select(BenchSample.FromJsonLine)];
        string csv = Csv(provenance, Reduce(samples));
        if (outPath is null)
        {
            output.Write(csv);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, csv);
        }

        return Program.ExitSuccess;
    }

    // -------------------------------------------------------------------- child

    // One compile, its metrics as one JSON line on stdout, nothing else there.
    // For the rows that must be a whole fresh process — the AOT-vs-JIT startup
    // rows the plan pins to t-12's comparison — the caller spawns this and
    // times the whole process: process start belongs in those walls because
    // that IS the number, and the JSON's wall excludes it, honestly labelled.
    private static async Task<int> ChildAsync(string[] args, CancellationToken cancellationToken)
    {
        int sep = args.IndexOf("--");
        if (sep < 1 || sep + 1 >= args.Length)
        {
            await Console.Error.WriteLineAsync(
                "usage: ssmap bench " + ChildSwitch
                + " <command> [--child-logs-to <log>] -- <map> [stock options...]").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string command = args[0];
        string? logsTo = null;
        for (int i = 1; i < sep; i++)
        {
            if (args[i] == "--child-logs-to" && i + 1 < sep)
            {
                logsTo = args[++i];
            }
        }

        string[] compileArgs = args[(sep + 1)..];

        StreamWriter? log = logsTo is null ? null : new StreamWriter(logsTo, append: false);
        TextWriter logger = (TextWriter?)log ?? TextWriter.Null;
        Process proc = Process.GetCurrentProcess();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        TimeSpan gc0 = GC.GetTotalPauseDuration();
        Stopwatch clock = Stopwatch.StartNew();
        BenchOutcome outcome;
        try
        {
            outcome = command switch
            {
                "all" or "chain" => await ChainAsync(
                    new PhysicalFileSystem("/"), DefaultRoots(), compileArgs, logger, cancellationToken)
                    .ConfigureAwait(false),
                "vbsp" => await SingleAsync("vbsp", compileArgs, cancellationToken).ConfigureAwait(false),
                "vvis" => await SingleAsync("vvis", compileArgs, cancellationToken).ConfigureAwait(false),
                "vrad" => await SingleAsync("vrad", compileArgs, cancellationToken).ConfigureAwait(false),
                _ => BenchOutcome.Fail("unknown command " + command),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            outcome = BenchOutcome.Fail(exception.GetType().Name + ": " + exception.Message);
        }

        clock.Stop();
        if (log is not null)
        {
            await log.FlushAsync(cancellationToken).ConfigureAwait(false);
            await log.DisposeAsync().ConfigureAwait(false);
        }

        BenchSample sample = new(
            Cell: "child|" + command + "|child|" + string.Join(' ', compileArgs) + "|0|off",
            Run: 0,
            Timed: true,
            Ok: outcome.Ok,
            WallSeconds: clock.Elapsed.TotalSeconds,
            CpuSeconds: (proc.TotalProcessorTime - cpu0).TotalSeconds,
            PeakRssBytes: PeakRssBytes(),
            GcPauseSeconds: Math.Max(0, (GC.GetTotalPauseDuration() - gc0).TotalSeconds),
            Stages: [.. outcome.Timings.Select(t =>
                $"{t.Stage}|{t.Elapsed.TotalSeconds.ToString("0.####", CultureInfo.InvariantCulture)}")],
            Outputs: [.. outcome.Written.Select(p => "/" + p.Value + "|" + Sha256Of("/" + p.Value))],
            CacheReused: outcome.Cache?.Hits ?? 0,
            CacheCooked: outcome.Cache?.Misses ?? 0,
            Failure: outcome.Failure,
            CacheStagesReused: [.. outcome.Cache?.StageHits ?? []],
            CacheStagesComputed: [.. outcome.Cache?.StageMisses ?? []],
            CacheSavedMs: outcome.Cache?.EstimatedSavedMs ?? 0,
            CacheBytesStored: outcome.Cache?.BytesStored ?? 0);
        await Console.Out.WriteLineAsync(sample.ToJsonLine()).ConfigureAwait(false);
        return outcome.Ok ? Program.ExitSuccess : Program.ExitFailure;
    }

    // ------------------------------------------------------------------- series

    private static async Task<int> RunSeriesAsync(
        PhysicalFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string map = string.Empty;
        string? game = null;
        string stage = "chain";
        string options = "default";
        string arm = "managed_jit";
        string cacheMode = "off";
        string? cacheBase = null;
        string outPath = "bench.jsonl";
        string workDir = ".";
        int threads = 0;
        int runs = 5;
        int warmups = 1;
        List<string> extra = [];

        for (int i = 0; i < args.Count; i++)
        {
            string Next()
            {
                if (i + 1 >= args.Count)
                {
                    throw new InvalidDataException(args[i] + " needs a value");
                }

                return args[++i];
            }

            switch (args[i])
            {
                case "--map": map = Next(); break;
                case "--game": game = Next(); break;
                case "--stages": stage = Next(); break;
                case "--options": options = Next(); break;
                case "--arm": arm = Next(); break;
                case "--cache": cacheMode = Next(); break;
                case "--cache-base": cacheBase = Next(); break;
                case "--out": outPath = Next(); break;
                case "--workdir": workDir = Next(); break;
                case "--threads": threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--runs": runs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--warmups": warmups = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--":
                    extra.AddRange([.. args.Skip(i + 1)]);
                    i = args.Count;
                    break;
                default:
                    await output.WriteLineAsync($"bench: unknown flag {args[i]}").ConfigureAwait(false);
                    return Program.ExitUsage;
            }
        }

        if (map.Length == 0 || threads <= 0 || runs < 1)
        {
            await output.WriteLineAsync(
                "usage: ssmap bench --map <vmf> --game <dir> --stages chain|vbsp|vvis|vrad --threads N "
                + "[--runs 5] [--warmups 1] [--arm LABEL] [--cache off|cold|warm] [--cache-base <dir>] "
                + "[--options LABEL] --out <jsonl> [--workdir <dir>] -- [stock options for the stage]").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // A native cooker needs LD_LIBRARY_PATH set before the process starts
        // (glibc reads it once), so — like `ssmap vbsp` — the whole series
        // relaunches ONCE when that is what the cooker asks for. One relaunch
        // per run would tax every timed number with a process start the stock
        // arms pay only once per process; t-12's startup comparison has its own
        // row (the child mode) instead.
        if (Environment.GetEnvironmentVariable(PhysCommand.RelaunchedVariable) != "1"
            && (await NativeRelaunchValueAsync(disk, searchRoots, extra, cancellationToken)
                .ConfigureAwait(false)) is string relaunch)
        {
            return await PhysCommand.RelaunchAsync(relaunch, ["bench", .. args], cancellationToken)
                .ConfigureAwait(false);
        }

        string cell = string.Join('|',
            Path.GetFileNameWithoutExtension(map), arm, stage, options,
            threads.ToString(CultureInfo.InvariantCulture), cacheMode);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        // Same bytes for every run: vvis/vrad bake into the .bsp they read and
        // -onlyents patches its way forward, so the stage's mutable inputs are
        // snapshotted once (before the warm-up) and restored before every run
        // — the native drivers' cp-per-run rule, in-process.
        (string[] snapFrom, string[] snapTo) = SnapshotStageInputs(map, stage);

        using StreamWriter ledger = new(outPath, append: false);
        for (int run = -warmups; run < runs; run++)
        {
            bool timed = run >= 0;
            RestoreStageInputs(snapFrom, snapTo);
            string? storeDir = StoreDirFor(
                cacheBase ?? Path.Combine(workDir, "bench-cache-store"), cell, cacheMode, run, timed);
            if (storeDir is not null)
            {
                Directory.CreateDirectory(storeDir);
            }

            List<string> stageArgs = [map];
            if (game is not null)
            {
                stageArgs.Add("-game");
                stageArgs.Add(game);
            }

            stageArgs.Add("-threads");
            stageArgs.Add(threads.ToString(CultureInfo.InvariantCulture));
            stageArgs.AddRange(extra);
            if (storeDir is not null)
            {
                stageArgs.Add("-incremental");
                stageArgs.Add("-cache-dir");
                stageArgs.Add(storeDir);
            }

            Process proc = Process.GetCurrentProcess();
            TimeSpan cpu0 = proc.TotalProcessorTime;
            TimeSpan gc0 = GC.GetTotalPauseDuration();
            Stopwatch clock = Stopwatch.StartNew();
            BenchOutcome outcome;
            try
            {
                outcome = stage switch
                {
                    "chain" or "all" => await ChainAsync(disk, searchRoots, stageArgs, TextWriter.Null, cancellationToken)
                        .ConfigureAwait(false),
                    "vbsp" => await SingleAsync("vbsp", stageArgs, cancellationToken).ConfigureAwait(false),
                    "vvis" => await SingleAsync("vvis", stageArgs, cancellationToken).ConfigureAwait(false),
                    "vrad" => await SingleAsync("vrad", stageArgs, cancellationToken).ConfigureAwait(false),
                    _ => BenchOutcome.Fail("unknown --stages " + stage),
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcome = BenchOutcome.Fail(exception.GetType().Name + ": " + exception.Message);
            }

            clock.Stop();
            BenchSample sample = new(
                cell, run, timed, outcome.Ok,
                clock.Elapsed.TotalSeconds,
                (proc.TotalProcessorTime - cpu0).TotalSeconds,
                PeakRssBytes(),
                Math.Max(0, (GC.GetTotalPauseDuration() - gc0).TotalSeconds),
                [.. outcome.Timings.Select(t =>
                    $"{t.Stage}|{t.Elapsed.TotalSeconds.ToString("0.####", CultureInfo.InvariantCulture)}")],
                [.. outcome.Written.Select(p => p.Value + "|" + Sha256Of("/" + p.Value))],
                outcome.Cache?.Hits ?? 0,
                outcome.Cache?.Misses ?? 0,
                outcome.Failure,
                [.. outcome.Cache?.StageHits ?? []],
                [.. outcome.Cache?.StageMisses ?? []],
                outcome.Cache?.EstimatedSavedMs ?? 0,
                outcome.Cache?.BytesStored ?? 0);
            await ledger.WriteLineAsync(sample.ToJsonLine()).ConfigureAwait(false);
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{cell} {(timed ? "run=" + run : "warmup")} wall={sample.WallSeconds:F3} cpu={sample.CpuSeconds:F3} rss={sample.PeakRssBytes / 1024}kB gc={sample.GcPauseSeconds * 1000:F0}ms{(outcome.Ok ? string.Empty : " FAILED " + outcome.Failure)}")).ConfigureAwait(false);
            if (!outcome.Ok && !timed)
            {
                break;
            }
        }

        await ledger.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// Snapshots the single-stage cell's mutable inputs (the .bsp/.prt the tool
    /// reads and bakes into) to sibling <c>.p12snap</c> files, returning the
    /// (from,to) copy pairs <see cref="RestoreStageInputs"/> replays before
    /// every run. The chain is excluded: it writes beside the vmf from the vmf
    /// alone, so every run already starts from the same vmf bytes; the
    /// single-tool stages mutate the very file they were handed — an unrestored
    /// run 2 lights an already-lit map and its wall is a lie (measured: 0.038 s
    /// then 0.003 s on the same vvis cell). A stage run whose input does not
    /// exist yet (the driver preps it) snapshots nothing.
    /// </summary>
    /// <param name="map">The map argument the stage will be handed.</param>
    /// <param name="stage">The stage label (<c>chain</c>/<c>all</c> opt out).</param>
    /// <returns>The (snapshot, original) path pairs to replay per run.</returns>
    public static (string[] From, string[] To) SnapshotStageInputs(string map, string stage)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(stage);

        if (stage is ("chain" or "all"))
        {
            return ([], []);
        }

        VbspCommand.MapPaths paths = VbspCommand.MapPaths.From(map);
        List<string> from = [];
        List<string> to = [];
        foreach (string candidate in new[] { paths.Bsp, paths.Prt })
        {
            if (File.Exists(candidate))
            {
                string snap = candidate + ".p12snap";
                File.Copy(candidate, snap, overwrite: true);
                from.Add(snap);
                to.Add(candidate);
            }
        }

        return ([.. from], [.. to]);
    }

    /// <summary>
    /// Replays a <see cref="SnapshotStageInputs"/> pair list so the next run
    /// starts from the same bytes as the last (idempotent; an empty pair list
    /// is the chain's no-op).
    /// </summary>
    /// <param name="from">The snapshot files.</param>
    /// <param name="to">The originals they replace.</param>
    public static void RestoreStageInputs(IReadOnlyList<string> from, IReadOnlyList<string> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Count != to.Count)
        {
            throw new ArgumentException("snapshot pairs must come in pairs", nameof(to));
        }

        for (int i = 0; i < to.Count; i++)
        {
            File.Copy(from[i], to[i], overwrite: true);
        }
    }
    private readonly record struct BenchOutcome(
        bool Ok,
        string? Failure,
        IReadOnlyList<CompileStageTiming> Timings,
        IReadOnlyList<VPath> Written,
        CacheRunCounters? Cache)
    {
        public static BenchOutcome Fail(string reason) => new(false, reason, [], [], null);
    }

    // The chain, on the product's own seams — the same path `ssmap all` walks
    // (cooker, mount, format pipeline, cache wiring through WithBackendsAsync),
    // because a harness imitation of the compile would measure the imitation.
    private static async Task<BenchOutcome> ChainAsync(
        PhysicalFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> chainArgs,
        TextWriter logger,
        CancellationToken ct)
    {
        AllArgs parsed = AllCommand.Parse(chainArgs);
        if (parsed.HasErrors || parsed.MapPath is null)
        {
            return BenchOutcome.Fail(string.Join("; ", parsed.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        }

        VbspCommand.MapPaths paths = VbspCommand.MapPaths.From(parsed.MapPath);
        string? mapFile = await VbspCommand.ResolveMapFileAsync(disk, paths, ct).ConfigureAwait(false);
        if (mapFile is null)
        {
            return BenchOutcome.Fail("no such map: " + paths.Name);
        }

        VbspHost.CookerSetup setup = await VbspHost.OpenCookerAsync(
            disk, searchRoots, parsed.Vbsp,
            ["bench", ChildSwitch, "all", "--", .. chainArgs], "ssmap bench", logger, ct)
            .ConfigureAwait(false);
        if (setup.Exit is int exit)
        {
            return BenchOutcome.Fail("cooker relaunch exit " + exit);
        }

        await using ICollisionCooker? cooker = setup.Cooker;

        string gameDirectory = parsed.GameDirectory is null
            ? Path.GetDirectoryName(Path.GetDirectoryName(paths.Source)!)!
            : Path.GetFullPath(parsed.GameDirectory);
        GameContentMounter.Result mounted;
        try
        {
            mounted = await VbspCommand.MountGameAsync(
                disk, gameDirectory, VbspHost.SteamFor(disk, searchRoots), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            return BenchOutcome.Fail("cannot mount " + gameDirectory + ": " + exception.Message);
        }

        FormatResolution.Result resolution = FormatResolution.Resolve(
            parsed.Format, parsed.PresetName, parsed.NoFormatDetect, parsed.NoToolsArgs, mounted.GameInfo);

        string mapName = Path.GetFileName(paths.Source);
        VradCommand.LooseFileContent content = new(disk, mounted.Content);
        content.Add(mapName + ".rad", paths.Source + ".rad");
        if (parsed.LightsFile is { Length: > 0 } lights)
        {
            content.Add(lights, Path.GetFullPath(lights));
        }

        // The store's LIFECYCLE is the harness's (a fresh dir per cold run, the
        // plan's isolation rule); its OPENING is the product's, through the same
        // WithBackendsAsync seam the Phase 11 facts pin.
        CompileRequest request = await AllCommand.WithBackendsAsync(
            new CompileRequest
            {
                Source = MapSource.FromVmf(disk, VPath.Create(mapFile)),
                Content = content,
                Vbsp = parsed.Vbsp with { Format = resolution.Resolved },
                Vvis = parsed.Vvis,
                Vrad = parsed.Vrad,
                Parallel = parsed.Threads is int degree
                    ? new CompileParallelism { MaxDegree = degree }
                    : CompileParallelism.Default,
                CollisionCooker = cooker,
                Output = CompileOutput.ToDirectory(disk, VPath.Create(Path.GetDirectoryName(paths.Source)!), mapName),
            },
            parsed,
            Path.GetDirectoryName(paths.Source)!,
            mapName,
            resolution.Resolved.PresetName,
            ct).ConfigureAwait(false);

        try
        {
            CompileResult result = await MapCompiler.CompileAsync(request, null, ct).ConfigureAwait(false);
            return new BenchOutcome(true, null, result.Timings, result.Written, result.Cache);
        }
        catch (MapCompileException exception)
        {
            return BenchOutcome.Fail("Error: " + exception.Message);
        }
        finally
        {
            if (request.Cache is { } store)
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<BenchOutcome> SingleAsync(
        string command,
        IReadOnlyList<string> stageArgs,
        CancellationToken ct)
    {
        PhysicalFileSystem disk = new("/");
        string mapArg = stageArgs.First(static a => !a.StartsWith("-", StringComparison.Ordinal));
        VbspCommand.MapPaths paths = VbspCommand.MapPaths.From(mapArg);
        List<VPath> written = [];
        // The stage's own wall, so a single-tool row is never Stages=[]: an
        // option cell that silently ran different work (the -fast/label leak,
        // T15-findings) shows up as an unexpected per-stage time, not an empty
        // column. Includes cooker setup — this is the whole stage, honestly.
        Stopwatch stageClock = Stopwatch.StartNew();
        int rc;
        switch (command)
        {
            case "vbsp":
            {
                VbspOptions options = StockArgs.ParseVbsp(stageArgs).Options;
                IReadOnlyList<VPath> roots = DefaultRoots();
                VbspHost.CookerSetup setup = await VbspHost.OpenCookerAsync(
                    disk, roots, options,
                    ["bench", ChildSwitch, "vbsp", "--", .. stageArgs], "ssmap bench", TextWriter.Null, ct)
                    .ConfigureAwait(false);
                if (setup.Exit is int exit)
                {
                    return BenchOutcome.Fail("cooker relaunch exit " + exit);
                }

                await using ICollisionCooker? cooker = setup.Cooker;
                rc = await VbspCommand.RunAsync(
                    disk, [.. stageArgs], cooker, VbspHost.SteamFor(disk, roots), TextWriter.Null, ct)
                    .ConfigureAwait(false);
                foreach (string ext in new[] { ".bsp", ".prt", ".lin" })
                {
                    if (File.Exists(paths.Source + ext))
                    {
                        written.Add(VPath.Create(paths.Source + ext));
                    }
                }

                break;
            }

            case "vvis":
            case "vrad":
            {
                rc = command == "vvis"
                    ? await VvisCommand.RunAsync(disk, [.. stageArgs], TextWriter.Null, ct).ConfigureAwait(false)
                    : await VradCommand.RunAsync(
                        disk, [.. stageArgs], VbspHost.SteamFor(disk, DefaultRoots()), TextWriter.Null, ct)
                        .ConfigureAwait(false);
                if (File.Exists(paths.Bsp))
                {
                    written.Add(VPath.Create(paths.Bsp));
                }

                break;
            }

            default:
                return BenchOutcome.Fail("unknown stage " + command);
        }

        stageClock.Stop();
        return rc == Program.ExitSuccess
            ? new BenchOutcome(true, null,
                [new CompileStageTiming(command, stageClock.Elapsed)], written, null)
            : BenchOutcome.Fail(command + " exit " + rc);
    }

    // The LD_LIBRARY_PATH the whole bench would need relaunched with, or null.
    // Only asked when the line's options reach the cooker at all: the default
    // arm cooks managed, needs no library, and must never relaunch — a relaunch
    // per cell would smuggle process start into every timed wall.
    private static async Task<string?> NativeRelaunchValueAsync(
        PhysicalFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> extra,
        CancellationToken ct)
    {
        VbspOptions options = StockArgs.ParseVbsp([.. extra]).Options;
        if (options.Cooker != CollisionCookerKind.Native)
        {
            return null;
        }

        ImmutableArray<VPhysicsLibrary> found =
            await VPhysicsLocator.DiscoverAsync(disk, searchRoots, ct).ConfigureAwait(false);
        if (!VbspHost.TrySelectLibrary(found, options.VPhysicsLibrary, out VPhysicsLibrary? chosen, out _)
            || chosen is null
            || disk is not PhysicalFileSystem host)
        {
            return null;
        }

        string libraryFile = host.ToHostPath(chosen.Path);
        string directory = libraryFile[..libraryFile.LastIndexOf('/')];
        return PhysCommand.RelaunchLibraryPath(Environment.GetEnvironmentVariable("LD_LIBRARY_PATH"), directory);
    }

    private static IReadOnlyList<VPath> DefaultRoots()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [.. Program.DefaultSteamRoots(home).Select(VPath.Create)];
    }

    private static long PeakRssBytes()
    {
        // /proc/self/status VmHWM: the kernel's own high-water mark. No sampler
        // thread, no racing the compile to catch its peak.
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                {
                    string digits = new([.. line.SkipWhile(static c => !char.IsDigit(c)).TakeWhile(char.IsDigit)]);
                    return long.Parse(digits, CultureInfo.InvariantCulture) * 1024;
                }
            }
        }
        catch (IOException)
        {
        }

        return 0;
    }

    private static string Sha256Of(string path)
    {
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}
