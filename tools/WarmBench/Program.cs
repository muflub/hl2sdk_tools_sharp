//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;

namespace WarmBench;

/// <summary>
/// A warm-service harness: compiles one map through the public
/// <see cref="MapCompiler"/> seam again and again in one process, in waves of
/// one or more concurrent compiles, and reports each run's cost and what the
/// process still holds between waves.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it prints.</b> Every line starts with a tag, so the output greps
/// and diffs well:
/// </para>
/// <list type="bullet">
/// <item><c>#</c> the configuration, including the GC mode the runtime
/// actually chose and every <c>DOTNET_GC*</c> variable it was started
/// with.</item>
/// <item><c>R</c> one compile: wall time, each stage's wall and CPU seconds,
/// the output BSP's SHA-256 (and whether it matches the first run's), and
/// what it read from the game content.</item>
/// <item><c>W</c> one wave: wall, process CPU, bytes allocated, gen0/1/2
/// collection counts, total GC pause, and peak RSS for that wave.</item>
/// <item><c>S</c> with <c>--substages</c>: the time spent under each progress
/// stage name a compile reported (for example <c>vrad.direct</c>).</item>
/// <item><c>L</c> with <c>--leak</c>: after each wave, the heap before and
/// after the host yields, committed bytes, LOH and POH, RSS, open file
/// descriptors, threads and memory mappings.</item>
/// <item><c>LEAK</c> with <c>--leak</c>: the growth from the first
/// measured wave to the last, per wave, and a verdict.</item>
/// <item><c>SUMMARY</c> the median compile wall and wave CPU, leaving out
/// the first wave, whose JIT and first-touch costs no warm service pays
/// again.</item>
/// </list>
/// <para>
/// <b>Per-stage CPU</b> is the process's CPU time between the chain's
/// progress marks, so it is only the stage's own when one compile runs at a
/// time; with <c>--concurrency</c> above one those columns print <c>-</c>.
/// </para>
/// <para>
/// <b>Why the leak snapshot is taken twice.</b> The heap is measured once
/// straight after the wave's last compile returns, before the host has
/// yielded, and again after a yield. A compile that hands its host back on
/// its own finished stack keeps its scratch alive until the host yields, so
/// the first number shows it and the second does not; a real leak shows in
/// both, and grows wave to wave.
/// </para>
/// <para>
/// The tool is Linux-first, like the service it models: peak RSS, the
/// descriptor and mapping counts and <c>malloc_trim</c> come from
/// <c>/proc</c> and glibc, and print as zero elsewhere.
/// </para>
/// </remarks>
internal static class Program
{
    private const string Usage = """
        usage: WarmBench --map <file.vmf> --game <dir> [options]

          --runs N            waves to run (default 6); the first is the warm-up
          --concurrency N     compiles per wave, started together (default 1)
          --threads N         worker threads per compile (default: processor count)
          --mount shared|per  one game mount for every compile, or one each (default per)
          --cooker shared|per one collision cooker for every compile, or one each (default shared)
          --cache off|mem     no incremental cache, or one in-memory cache for the process (default off)
          --leak              snapshot heap, descriptors, threads and mappings after each wave
          --substages         print each compile's time under each progress stage
          --gc-between        force a compacting full GC between waves
          --vbsp "<args>"     stock vbsp arguments, for example "-nodrawtriggers"
          --vvis "<args>"     stock vvis arguments, for example "-fast"
          --vrad "<args>"     stock vrad arguments, for example "-bounce 2 -compliance stock"
          --marks <file>      write stage boundaries (monotonic ns) for perf_by_stage.py
          --label <name>      a name printed on every line (default run)

        The GC mode comes from the environment (DOTNET_gcServer and friends);
        README.md, "Measuring performance", lists the knobs.
        Exit code: 0, 1 when a compile failed or two outputs differed, 2 on bad arguments.
        """;

    private static async Task<int> Main(string[] args)
    {
        Settings settings;
        try
        {
            settings = Settings.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (settings.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        using CancellationTokenSource cancel = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            return await new Bench(settings).RunAsync(cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
    }
}

/// <summary>The command line, parsed.</summary>
internal sealed record Settings
{
    public bool Help { get; init; }

    public string Map { get; init; } = "";

    public string Game { get; init; } = "";

    public int Runs { get; init; } = 6;

    public int Concurrency { get; init; } = 1;

    public int Threads { get; init; } = Environment.ProcessorCount;

    public bool SharedMount { get; init; }

    public bool SharedCooker { get; init; } = true;

    public bool MemoryCache { get; init; }

    public bool Leak { get; init; }

    public bool Substages { get; init; }

    public bool GcBetween { get; init; }

    public string[] VbspArgs { get; init; } = [];

    public string[] VvisArgs { get; init; } = [];

    public string[] VradArgs { get; init; } = [];

    public string? Marks { get; init; }

    public string Label { get; init; } = "run";

    public static Settings Parse(string[] args)
    {
        Settings s = new();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (key is "-h" or "--help")
            {
                return s with { Help = true };
            }

            switch (key)
            {
                case "--leak": s = s with { Leak = true }; continue;
                case "--substages": s = s with { Substages = true }; continue;
                case "--gc-between": s = s with { GcBetween = true }; continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"{key} needs a value, or is not an option");
            }

            string v = args[++i];
            s = key switch
            {
                "--map" => s with { Map = Path.GetFullPath(v) },
                "--game" => s with { Game = Path.GetFullPath(v) },
                "--runs" => s with { Runs = Positive(key, v) },
                "--concurrency" => s with { Concurrency = Positive(key, v) },
                "--threads" => s with { Threads = Positive(key, v) },
                "--mount" => s with { SharedMount = OneOf(key, v, "shared", "per") },
                "--cooker" => s with { SharedCooker = OneOf(key, v, "shared", "per") },
                "--cache" => s with { MemoryCache = OneOf(key, v, "mem", "off") },
                "--vbsp" => s with { VbspArgs = Words(v) },
                "--vvis" => s with { VvisArgs = Words(v) },
                "--vrad" => s with { VradArgs = Words(v) },
                "--marks" => s with { Marks = Path.GetFullPath(v) },
                "--label" => s with { Label = v },
                _ => throw new ArgumentException($"unknown option {key}"),
            };
        }

        if (s.Map.Length == 0 || s.Game.Length == 0)
        {
            throw new ArgumentException("--map and --game are required");
        }

        return s;
    }

    private static int Positive(string key, string v) =>
        int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : throw new ArgumentException($"{key} takes a positive whole number, not '{v}'");

    // True for the first value, false for the second.
    private static bool OneOf(string key, string v, string yes, string no) =>
        v == yes ? true : v == no ? false : throw new ArgumentException($"{key} is {yes} or {no}, not '{v}'");

    private static string[] Words(string v) => v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>The harness itself: the mount, the waves and the reports.</summary>
internal sealed class Bench(Settings settings)
{
    private readonly PhysicalFileSystem _disk = new("/");
    private readonly object _marksLock = new();
    private StreamWriter? _marks;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // Stock arguments go through the same parser ssmap uses; the trailing
        // "x" stands in for the map name the parser expects last.
        VbspOptions vbsp = StockArgs.ParseVbsp([.. settings.VbspArgs, "x"]).Options;
        VvisOptions vvis = StockArgs.ParseVvis([.. settings.VvisArgs, "x"]).Options;
        VradOptions vrad = StockArgs.ParseVrad([.. settings.VradArgs, "x"]).Options;
        byte[] mapText = File.ReadAllBytes(settings.Map);
        string mapName = Path.GetFileNameWithoutExtension(settings.Map);

        PrintHeader();
        if (settings.Marks is { } marksPath)
        {
            _marks = new StreamWriter(marksPath) { AutoFlush = true };
        }

        Mounted? shared = settings.SharedMount ? await MountAsync(cancellationToken) : null;
        InMemoryCacheStore? cache = null;
        if (settings.MemoryCache)
        {
            cache = new InMemoryCacheStore();
            await cache.OpenAsync("warmbench", cancellationToken);
        }

        ManagedCollisionCooker? sharedCooker = settings.SharedCooker ? ManagedCollisionCooker.Create(vbsp.Compliance) : null;
        Job job = new(settings, mapText, mapName, vbsp, vvis, vrad, shared, sharedCooker, cache, MountAsync, Mark);

        ProcessSnapshot start = ProcessSnapshot.Take(yielded: true, heapBeforeYield: 0);
        Console.WriteLine($"# start {start.Describe()}");

        string? firstHash = null;
        bool failed = false;
        List<double> compileWalls = [];
        List<double> waveCpus = [];
        List<ProcessSnapshot> leaks = [];
        try
        {
            for (int wave = 0; wave < settings.Runs; wave++)
            {
                WaveMeter meter = WaveMeter.Start();
                Mark($"wave{wave} begin");
                JobResult[] results = await Task.WhenAll(Enumerable.Range(0, settings.Concurrency)
                    .Select(i => Task.Run(() => job.RunAsync(wave, i, cancellationToken), cancellationToken)));
                Mark($"wave{wave} end");
                WaveMeter.Reading w = meter.Stop();

                // Measured here, before this method yields: see the remarks
                // on Program for why the heap is read on both sides of it.
                long heapBeforeYield = settings.Leak ? ProcessSnapshot.CollectedHeap() : 0;

                foreach (JobResult r in results)
                {
                    firstHash ??= r.Hash;
                    bool same = r.Hash == firstHash;
                    failed |= !r.Succeeded || !same;
                    string stages = string.Join(' ', r.Stages.Select(s =>
                        $"{s.Name}={s.Wall:F2}/{(double.IsNaN(s.Cpu) ? "-" : s.Cpu.ToString("F2", CultureInfo.InvariantCulture))}"));
                    Console.WriteLine(
                        $"R {settings.Label} wave={wave} job={r.Job} ok={r.Succeeded} wall={r.Wall:F2} {stages} " +
                        $"sha256={Short(r.Hash)} {(same ? "same" : "DIFF")} reads=[{r.Reads}]{r.CacheLine}");
                    if (settings.Substages)
                    {
                        Console.WriteLine($"S {settings.Label} wave={wave} job={r.Job} {r.Substages}");
                    }

                    if (wave > 0)
                    {
                        compileWalls.Add(r.Wall);
                    }
                }

                Console.WriteLine($"W {settings.Label} wave={wave} {w.Describe()}");
                if (wave > 0)
                {
                    waveCpus.Add(w.Cpu);
                }

                if (settings.Leak)
                {
                    await Task.Yield();
                    await Task.Delay(50, cancellationToken);
                    ProcessSnapshot s = ProcessSnapshot.Take(yielded: true, heapBeforeYield);
                    leaks.Add(s);
                    Console.WriteLine($"L {settings.Label} wave={wave} {s.Describe()}");
                }

                if (settings.GcBetween)
                {
                    await Task.Yield();
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                }
            }
        }
        finally
        {
            if (shared is { } sh)
            {
                await sh.Content.DisposeAsync();
            }

            sharedCooker?.Dispose();
            if (cache is not null)
            {
                await cache.DisposeAsync();
            }

            _marks?.Dispose();
        }

        if (settings.Leak)
        {
            Console.WriteLine($"LEAK {settings.Label} {LeakSummary.Describe(leaks, settings.Threads)}");
            ProcessSnapshot end = ProcessSnapshot.Take(yielded: true, heapBeforeYield: 0);
            Console.WriteLine($"L {settings.Label} final(after releasing shared state) {end.Describe()}");
        }

        Console.WriteLine(
            $"SUMMARY {settings.Label} compiles={settings.Runs * settings.Concurrency} " +
            $"medianWall={Median(compileWalls):F2}s medianWaveCpu={Median(waveCpus):F2}s " +
            $"sha256={(firstHash is null ? "-" : Short(firstHash))} {(failed ? "FAILED-OR-DIFFERENT" : "all-same")}");
        return failed ? 1 : 0;
    }

    private void PrintHeader()
    {
        string gcVars = string.Join(' ', Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k.StartsWith("DOTNET_GC", StringComparison.OrdinalIgnoreCase) || k.StartsWith("DOTNET_gc", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}"));
        Console.WriteLine(
            $"# {settings.Label} runtime={RuntimeInformation.FrameworkDescription} cpus={Environment.ProcessorCount} " +
            $"gc: server={GCSettings.IsServerGC} latency={GCSettings.LatencyMode} env=[{gcVars}]");
        Console.WriteLine(
            $"# map={settings.Map} game={settings.Game} runs={settings.Runs} concurrency={settings.Concurrency} " +
            $"threads={settings.Threads} mount={(settings.SharedMount ? "shared" : "per")} " +
            $"cooker={(settings.SharedCooker ? "shared" : "per")} cache={(settings.MemoryCache ? "mem" : "off")} " +
            $"vbsp='{string.Join(' ', settings.VbspArgs)}' vvis='{string.Join(' ', settings.VvisArgs)}' vrad='{string.Join(' ', settings.VradArgs)}'");
    }

    // Mounts the game the way ssmap does: gameinfo.txt's search paths, read
    // only, then the format its gameinfo asks for. No Steam locator, so a
    // game whose gameinfo mounts |appid_N| paths wants a copy with those
    // lines removed (README, "Without the Steam content").
    private async Task<Mounted> MountAsync(CancellationToken cancellationToken)
    {
        ReadOnlyFileSystem ro = new(_disk);
        VPath gameInfoPath = VPath.Create(Path.Combine(settings.Game, "gameinfo.txt"));
        GameInfo info = await GameInfo.LoadAsync(ro, gameInfoPath, cancellationToken);
        GameContentMounter.Result mounted = await GameContentMounter.MountAsync(
            ro,
            info,
            new GameContentRoots(gameInfoPath.Directory, VPath.Create(Path.GetDirectoryName(settings.Game)!)),
            cancellationToken: cancellationToken);
        FormatResolution.Result format = FormatResolution.Resolve(null, null, false, false, mounted.GameInfo);
        return new Mounted(mounted.Content, format.Resolved);
    }

    private void Mark(string what)
    {
        if (_marks is null)
        {
            return;
        }

        lock (_marksLock)
        {
            _marks.WriteLine($"{Stopwatch.GetTimestamp()} {what}");
        }
    }

    private static string Short(string hash) => hash.Length > 16 ? hash[..16] : hash;

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        double[] sorted = [.. values.Order()];
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

/// <summary>A mounted game and the format it resolved to.</summary>
internal sealed record Mounted(ContentFileSystem Content, FormatOptions Format);

/// <summary>What one compile reports back to the wave.</summary>
internal sealed record JobResult(
    int Job,
    bool Succeeded,
    double Wall,
    IReadOnlyList<StageTime> Stages,
    string Hash,
    string Reads,
    string CacheLine,
    string Substages);

/// <summary>One top-level stage's wall and CPU seconds; CPU is NaN when not attributable.</summary>
internal readonly record struct StageTime(string Name, double Wall, double Cpu);

/// <summary>One compile: its own in-memory disk, pool and (optionally) mount and cooker.</summary>
internal sealed class Job(
    Settings settings,
    byte[] mapText,
    string mapName,
    VbspOptions vbsp,
    VvisOptions vvis,
    VradOptions vrad,
    Mounted? shared,
    ManagedCollisionCooker? sharedCooker,
    InMemoryCacheStore? cache,
    Func<CancellationToken, Task<Mounted>> mount,
    Action<string> mark)
{
    public async Task<JobResult> RunAsync(int wave, int job, CancellationToken cancellationToken)
    {
        Stopwatch wall = Stopwatch.StartNew();
        StageClock clock = new(trackCpu: settings.Concurrency == 1, mark, $"w{wave}j{job}");
        clock.Enter("mount");
        Mounted? own = shared is null ? await mount(cancellationToken) : null;
        try
        {
            Mounted m = (shared ?? own)!;
            CountingContent content = new(m.Content);
            InMemoryFileSystem files = new();
            files.AddFile($"in/{mapName}.vmf", mapText);
            using CompilePool pool = new(settings.Threads);
            using ManagedCollisionCooker? ownCooker = sharedCooker is null ? ManagedCollisionCooker.Create(vbsp.Compliance) : null;
            ICollisionCooker cooker = (sharedCooker ?? ownCooker)!.On(pool.Scheduler);
            CompileRequest request = new()
            {
                Source = MapSource.FromVmf(files, VPath.Create($"in/{mapName}.vmf")),
                Content = content,
                Vbsp = vbsp with { Format = m.Format },
                Vvis = vvis,
                Vrad = vrad,
                Parallel = new CompileParallelism { MaxDegree = settings.Threads, Pool = pool },
                CollisionCooker = cooker,
                Output = CompileOutput.ToDirectory(files, VPath.Create("out"), mapName),
                Cache = cache,
                ContextTags = cache is null ? [] : ["warmbench"],
            };

            CompileResult result = await MapCompiler.CompileAsync(request, clock, cancellationToken);
            clock.Enter("end");
            byte[]? bsp = files.GetBytes(VPath.Create($"out/{mapName}.bsp"));
            string hash = bsp is null ? "none" : Convert.ToHexString(SHA256.HashData(bsp)).ToLowerInvariant();
            string cacheLine = result.Cache is { } c
                ? $" cache=hits{c.Hits}/miss{c.Misses}/stageHits[{string.Join(",", c.StageHits)}]/stageMiss[{string.Join(",", c.StageMisses)}]/saved{c.EstimatedSavedMs}ms/stored{c.BytesStored / 1e6:F1}MB"
                : "";
            return new JobResult(job, result.Succeeded && bsp is not null, wall.Elapsed.TotalSeconds, clock.Stages,
                hash, content.Summary(), cacheLine, clock.SubstageSummary());
        }
        finally
        {
            if (own is not null)
            {
                await own.Content.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// Turns the chain's progress reports into stage times: the chain stage's
/// done count says which tool is running, and any dotted stage name (for
/// example <c>vrad.direct</c>) starts a substage.
/// </summary>
internal sealed class StageClock(bool trackCpu, Action<string> mark, string tag) : IProgress<CompileProgress>
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly List<StageTime> _stages = [];
    private readonly Dictionary<string, double> _substages = [];
    private string _stage = "";
    private double _stageWall;
    private double _stageCpu;
    private string _substage = "";
    private double _substageWall;

    public IReadOnlyList<StageTime> Stages
    {
        get
        {
            lock (_lock)
            {
                return [.. _stages];
            }
        }
    }

    public void Enter(string stage)
    {
        lock (_lock)
        {
            double now = _sw.Elapsed.TotalSeconds;
            double cpu = trackCpu ? ProcessCpu() : double.NaN;
            if (_stage.Length > 0)
            {
                _stages.Add(new StageTime(_stage, now - _stageWall, cpu - _stageCpu));
            }

            mark($"{tag} {stage}");
            _stage = stage;
            _stageWall = now;
            _stageCpu = cpu;
            EnterSubstage(stage + ".setup", now);
        }
    }

    public void Report(CompileProgress value)
    {
        if (value.Stage == MapCompiler.ChainStage)
        {
            // Done counts 0..3: loading starts, then vbsp, vvis and vrad finish.
            Enter(value.Done switch { 0 => "vbsp", 1 => "vvis", 2 => "vrad", _ => "write" });
            return;
        }

        // Undotted names are work-queue ticks inside a substage.
        if (!value.Stage.Contains('.'))
        {
            return;
        }

        lock (_lock)
        {
            if (value.Stage != _substage)
            {
                EnterSubstage(value.Stage, _sw.Elapsed.TotalSeconds);
            }
        }
    }

    public string SubstageSummary()
    {
        lock (_lock)
        {
            return string.Join(' ', _substages.OrderByDescending(kv => kv.Value).Select(kv =>
                $"{kv.Key}={kv.Value.ToString("F2", CultureInfo.InvariantCulture)}"));
        }
    }

    private void EnterSubstage(string substage, double now)
    {
        if (_substage.Length > 0)
        {
            _substages[_substage] = _substages.GetValueOrDefault(_substage) + (now - _substageWall);
        }

        _substage = substage;
        _substageWall = now;
        mark($"{tag} sub {substage}");
    }

    private static double ProcessCpu()
    {
        using Process p = Process.GetCurrentProcess();
        return p.TotalProcessorTime.TotalSeconds;
    }
}

/// <summary>Process-wide costs across one wave.</summary>
internal sealed class WaveMeter
{
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly double _cpu;
    private readonly long _allocated;
    private readonly TimeSpan _pause;
    private readonly int[] _collections;

    private WaveMeter()
    {
        _cpu = Cpu();
        _allocated = GC.GetTotalAllocatedBytes(precise: true);
        _pause = GC.GetTotalPauseDuration();
        _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    }

    public static WaveMeter Start()
    {
        // The kernel's peak-RSS counter is per process; writing 5 to
        // clear_refs resets it, so each wave's peak is its own.
        LinuxProc.ResetPeakRss();
        return new WaveMeter();
    }

    public Reading Stop()
    {
        _wall.Stop();
        return new Reading(
            _wall.Elapsed.TotalSeconds,
            Cpu() - _cpu,
            GC.GetTotalAllocatedBytes(precise: true) - _allocated,
            GC.CollectionCount(0) - _collections[0],
            GC.CollectionCount(1) - _collections[1],
            GC.CollectionCount(2) - _collections[2],
            (GC.GetTotalPauseDuration() - _pause).TotalMilliseconds,
            LinuxProc.StatusKb("VmHWM") * 1024);
    }

    private static double Cpu()
    {
        using Process p = Process.GetCurrentProcess();
        return p.TotalProcessorTime.TotalSeconds;
    }

    public readonly record struct Reading(
        double Wall, double Cpu, long Allocated, int Gen0, int Gen1, int Gen2, double PauseMs, long PeakRss)
    {
        public string Describe() =>
            $"wall={Wall:F2} cpu={Cpu:F2} alloc={Allocated / 1e9:F2}GB gc={Gen0}/{Gen1}/{Gen2} " +
            $"pause={PauseMs:F0}ms peakRss={PeakRss / 1e6:F0}MB";
    }
}

/// <summary>What the process holds at one moment, after full compacting collections.</summary>
internal sealed record ProcessSnapshot(
    long HeapBeforeYield,
    long Heap,
    long Committed,
    long Loh,
    long Poh,
    long Rss,
    long RssAnon,
    long RssAnonAfterTrim,
    int Fds,
    int Threads,
    int Maps)
{
    public static ProcessSnapshot Take(bool yielded, long heapBeforeYield)
    {
        long heap = CollectedHeap();
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        ReadOnlySpan<GCGenerationInfo> gens = info.GenerationInfo;
        long anon = LinuxProc.StatusKb("RssAnon") * 1024;

        // glibc keeps freed native memory (the collision cooker's, the
        // runtime's own) in its arenas; trimming shows how much of RSS is
        // that rather than something still in use.
        LinuxProc.MallocTrim();
        return new ProcessSnapshot(
            yielded ? heapBeforeYield : heap,
            heap,
            info.TotalCommittedBytes,
            gens.Length > 3 ? gens[3].SizeAfterBytes : 0,
            gens.Length > 4 ? gens[4].SizeAfterBytes : 0,
            LinuxProc.StatusKb("VmRSS") * 1024,
            anon,
            LinuxProc.StatusKb("RssAnon") * 1024,
            LinuxProc.Count("/proc/self/fd"),
            LinuxProc.Count("/proc/self/task"),
            LinuxProc.Lines("/proc/self/maps"));
    }

    /// <summary>The live managed heap after full, compacting, finalizer-draining collections.</summary>
    public static long CollectedHeap()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    public string Describe() =>
        $"heapBeforeYield={HeapBeforeYield / 1e6:F1}MB heap={Heap / 1e6:F1}MB committed={Committed / 1e6:F1}MB " +
        $"loh={Loh / 1e6:F1}MB poh={Poh / 1e6:F1}MB rss={Rss / 1e6:F1}MB rssAnon={RssAnon / 1e6:F1}MB " +
        $"afterTrim={RssAnonAfterTrim / 1e6:F1}MB fds={Fds} threads={Threads} maps={Maps}";
}

/// <summary>Reads the leak snapshots as a trend.</summary>
internal static class LeakSummary
{
    // Growth per wave above which the heap counts as growing. A warm service
    // settles within a wave or two; a few hundred KB of noise per wave is
    // interning and tiered-JIT bookkeeping, not a compile's scratch.
    private const double HeapSlackMbPerWave = 1.0;

    /// <summary>The trend from the first measured wave to the last.</summary>
    /// <param name="waves">One snapshot per wave, wave 0 first.</param>
    /// <param name="compileThreads">
    /// Each compile's worker count. The runtime's thread pool adds and
    /// retires threads as it sees fit, so the thread count drifts by one or
    /// two without anything leaking; a compile pool that is never disposed
    /// adds all its workers at once, so only growth of at least that many a
    /// wave counts.
    /// </param>
    /// <returns>The LEAK line's text.</returns>
    public static string Describe(IReadOnlyList<ProcessSnapshot> waves, int compileThreads)
    {
        // Wave 0 is the warm-up: it loads, JITs and sizes every pool for the
        // first time, so the trend starts from wave 1.
        if (waves.Count < 3)
        {
            return "needs --runs 3 or more (the first wave is the warm-up)";
        }

        ProcessSnapshot first = waves[1];
        ProcessSnapshot last = waves[^1];
        int span = waves.Count - 2;
        double heapPerWave = (last.Heap - first.Heap) / 1e6 / span;
        double maxBeforeYield = waves.Skip(1).Max(w => w.HeapBeforeYield - w.Heap) / 1e6;
        int fds = last.Fds - first.Fds;
        int threads = last.Threads - first.Threads;
        int maps = last.Maps - first.Maps;
        bool growing = heapPerWave > HeapSlackMbPerWave || fds > 0 || threads >= compileThreads * span;
        return
            $"waves=1..{waves.Count - 1} heap {first.Heap / 1e6:F1}->{last.Heap / 1e6:F1}MB ({heapPerWave:+0.00;-0.00}MB/wave) " +
            $"rssAnon {first.RssAnon / 1e6:F1}->{last.RssAnon / 1e6:F1}MB fds {fds:+0;-0;0} threads {threads:+0;-0;0} maps {maps:+0;-0;0} " +
            $"heldUntilYield<={maxBeforeYield:F1}MB verdict={(growing ? "GROWING" : "steady")}";
    }
}

/// <summary>The Linux-only readings; each is zero or a no-op elsewhere.</summary>
internal static class LinuxProc
{
    public static long StatusKb(string key)
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                return long.Parse(line[(key.Length + 1)..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }

    public static int Count(string directory) =>
        OperatingSystem.IsLinux() ? Directory.GetFileSystemEntries(directory).Length : 0;

    public static int Lines(string file) =>
        OperatingSystem.IsLinux() ? File.ReadLines(file).Count() : 0;

    public static void ResetPeakRss()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.WriteAllText("/proc/self/clear_refs", "5");
        }
        catch (IOException)
        {
            // Some kernels and containers refuse it; the peak is then the
            // process's peak so far.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void MallocTrim()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            _ = malloc_trim(0);
        }
        catch (DllNotFoundException)
        {
            // musl has no malloc_trim; nothing to trim then.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    // DllImport rather than LibraryImport: the signature is blittable, and
    // the source-generated form would need unsafe code for nothing.
    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int malloc_trim(nuint pad);
}

/// <summary>
/// Counts what a compile reads from game content, by file extension, so a
/// run that reads the same file twice, or reads far more than the last, is
/// visible beside its time.
/// </summary>
internal sealed class CountingContent(IContentFileSystem inner) : IContentFileSystem
{
    private readonly ConcurrentDictionary<string, long[]> _byExtension = new();
    private readonly ConcurrentDictionary<string, int> _paths = new();

    public async ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default)
    {
        long t = Stopwatch.GetTimestamp();
        ContentSource? r = await inner.ResolveAsync(path, cancellationToken);
        Add(path, 0, Stopwatch.GetTimestamp() - t, 0);
        return r;
    }

    public async ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default)
    {
        long t = Stopwatch.GetTimestamp();
        IMemoryOwner<byte>? r = await inner.ReadAsync(path, cancellationToken);
        Add(path, r?.Memory.Length ?? 0, Stopwatch.GetTimestamp() - t, 1);
        return r;
    }

    public async ValueTask<FileRange?> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        long t = Stopwatch.GetTimestamp();
        FileRange? r = await inner.ReadRangeAsync(path, offset, length, cancellationToken);
        Add(path, r?.Memory.Length ?? 0, Stopwatch.GetTimestamp() - t, 2);
        return r;
    }

    public IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        CancellationToken cancellationToken = default) =>
        inner.EnumerateAsync(directory, searchPattern, cancellationToken);

    public string Summary()
    {
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture,
            $"ops={_paths.Values.Sum(v => (long)v)} distinct={_paths.Count} repeated={_paths.Values.Count(v => v > 1)}");
        foreach ((string ext, long[] v) in _byExtension.OrderByDescending(kv => kv.Value[3]))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $" {ext}:res{v[0]}/rd{v[1]}/rng{v[2]}/{v[3] / 1e6:F1}MB/{v[4] * 1000.0 / Stopwatch.Frequency:F0}ms");
        }

        return sb.ToString();
    }

    // kind: 0 resolve, 1 whole read, 2 range read.
    private void Add(VPath path, long bytes, long ticks, int kind)
    {
        string s = path.ToString();
        int dot = s.LastIndexOf('.');
        string ext = dot >= 0 ? s[(dot + 1)..].ToLowerInvariant() : "(none)";
        long[] counts = _byExtension.GetOrAdd(ext, _ => new long[5]);
        Interlocked.Increment(ref counts[kind]);
        Interlocked.Add(ref counts[3], bytes);
        Interlocked.Add(ref counts[4], ticks);
        _paths.AddOrUpdate(kind + ":" + s.ToLowerInvariant(), 1, (_, n) => n + 1);
    }
}
