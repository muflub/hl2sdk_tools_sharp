//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rad;

namespace SourceSharp.MapCompile;

/// <summary>What <see cref="AllCommand.Parse"/> made of an <c>ssmap all</c> command line.</summary>
/// <param name="Vbsp">vbsp's options.</param>
/// <param name="Vvis">vvis's options.</param>
/// <param name="Vrad">vrad's options.</param>
/// <param name="MapPath">The map argument, or null.</param>
/// <param name="GameDirectory">The <c>-game</c> directory, or null for the map directory's parent.</param>
/// <param name="Threads">The one <c>-threads</c> for the chain, or null for every processor.</param>
/// <param name="LightsFile">vrad's <c>-lights</c> file, or null.</param>
/// <param name="ListCompliance">Whether <c>-listcompliance</c> was given: print the catalogue and compile nothing.</param>
/// <param name="NoCache">Whether <c>-nocache</c> was given.</param>
/// <param name="NoWrite">Whether <see cref="AllCommand.NoWriteSwitch"/> was given: compile in memory, write nothing.</param>
/// <param name="Diagnostics">Everything the parsers said, each prefixed with its stage.</param>
public sealed record AllArgs(
    VbspOptions Vbsp,
    VvisOptions Vvis,
    VradOptions Vrad,
    string? MapPath,
    string? GameDirectory,
    int? Threads,
    string? LightsFile,
    bool ListCompliance,
    bool NoCache,
    bool NoWrite,
    IReadOnlyList<CompileDiagnostic> Diagnostics)
{
    /// <summary>
    /// The vbsp section's raw format-family CLI overlay plus the toggles the
    /// format pipeline needs; <c>CompileAsync</c> resolves them against the
    /// mounted gameinfo (defaults → appid preset → Tools key → CLI), exactly
    /// like <c>ssmap vbsp</c> does after its mount.
    /// </summary>
    public FormatOverrides? Format { get; init; }

    /// <summary>The last preset flag the vbsp section named, or null.</summary>
    public string? PresetName { get; init; }

    /// <summary>Whether the line carried <c>-noformatdetect</c>.</summary>
    public bool NoFormatDetect { get; init; }

    /// <summary>Whether the line carried <c>-notoolsargs</c>.</summary>
    public bool NoToolsArgs { get; init; }

    /// <summary>Whether the chain runs against the incremental collision cache.</summary>
    public bool Incremental { get; init; }

    /// <summary>The <c>-cache-dir</c> directory, or null for the map's own directory.</summary>
    public string? CacheDir { get; init; }

    /// <summary>The <c>-gpu</c> device pin, or null: CPU tracer only (the default).</summary>
    public string? GpuDeviceMatch { get; init; }

    /// <summary>The <c>-gpu_slabs</c> ray count, or null for the backend's default.</summary>
    public int? GpuRaysPerSlab { get; init; }

    /// <summary>
    /// The <c>-gpu_depth</c> batches each face-lighting worker keeps in flight
    /// on the GPU, or null for the default (<see cref="VradContext.GpuPipelineDepth"/>).
    /// </summary>
    public int? GpuPipelineDepth { get; init; }

    /// <summary>
    /// Whether <c>-overlap</c> was given: each stage starts when its inputs exist
    /// (<see cref="CompileRequest.Overlap"/>).
    /// </summary>
    public bool Overlap { get; init; }

    /// <summary>
    /// The <see cref="AllCommand.RecordContentSwitch"/> zip path, or null: record
    /// which game content the compile touched and write it out as a
    /// self-contained game directory (<see cref="ContentRecording"/>).
    /// </summary>
    public string? RecordContent { get; init; }

    /// <summary>Whether any diagnostic is an error.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// <c>ssmap all</c>: vbsp, vvis and vrad in one process, the BSP in memory
/// between them (<see cref="MapCompiler.CompileAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Syntax.</b> Hammer runs three programs with three command lines, so a
/// chain in one process needs to say which switch belongs to which stage:
/// </para>
/// <code>
/// ssmap all [chain options] &lt;map&gt; [--vbsp &lt;vbsp options&gt;] [--vvis &lt;vvis options&gt;] [--vrad &lt;vrad options&gt;]
/// </code>
/// <para>
/// Each <c>--vbsp</c>/<c>--vvis</c>/<c>--vrad</c> starts a section that runs
/// to the next marker, and a section is parsed by that stage's stock parser
/// (<see cref="StockArgs"/>), so it takes exactly what <c>ssmap vbsp</c>,
/// <c>ssmap vvis</c> and <c>ssmap vrad</c> take, in stock's spelling -- a
/// Hammer "run map" line converts by putting its three argument lists behind
/// the three markers. The markers have two dashes so that no stock switch can
/// be mistaken for one.
/// </para>
/// <para>
/// Before the first marker are the map and the options that mean one thing for
/// the whole chain: <c>-game</c>/<c>-vproject</c> (one content mount),
/// <c>-threads</c> (one degree: <see cref="CompileRequest.Parallel"/>),
/// <c>-compliance</c> and <c>-v</c> (given to all three stages),
/// <c>-fast</c> (vvis <c>-fast</c> and vrad <c>-fast</c>, Hammer's "fast"
/// preset), <c>-tighten</c>/<c>-loose</c> (vvis — tightening is the promoted
/// default, <c>-loose</c> opts out to the conservative walk; both is an
/// error), <c>-cooker native|vphysics|managed|none</c>
/// and <c>-vphysics</c> (vbsp's collision cooker, <see cref="VbspOptions.Cooker"/>;
/// only the native cooker relaunches the process), <c>-listcompliance</c>,
/// <c>-nocache</c>, <c>-overlap</c> (<see cref="CompileRequest.Overlap"/>),
/// <see cref="NoWriteSwitch"/> and <see cref="RecordContentSwitch"/>. Anything else there is
/// an error that names the sections, never a silent guess at a stage. A
/// section's own <c>-compliance</c> comes after the chain's and wins for that
/// stage; the chain's <c>-threads</c> and any section's must agree.
/// </para>
/// </remarks>
public static class AllCommand
{
    /// <summary>Starts vbsp's section.</summary>
    public const string VbspSection = "--vbsp";

    /// <summary>Starts vvis's section.</summary>
    public const string VvisSection = "--vvis";

    /// <summary>Starts vrad's section.</summary>
    public const string VradSection = "--vrad";

    /// <summary>Compiles in memory and writes nothing: no .bsp, .prt, .lin or .log.</summary>
    public const string NoWriteSwitch = "--no-write";

    /// <summary>
    /// Records the game content the compile touched and writes it to the zip
    /// that follows (<see cref="ContentRecording"/>).
    /// </summary>
    /// <remarks>
    /// Two dashes, like <see cref="NoWriteSwitch"/>: it is this tool's own
    /// switch, not a stock one, and no stock spelling can be mistaken for it.
    /// </remarks>
    public const string RecordContentSwitch = "--record-content";

    /// <summary>The diagnostic code for a chain option the command line got wrong.</summary>
    public const string ChainArgument = "ALLARGS";

    /// <summary>Splits and parses an <c>ssmap all</c> command line.</summary>
    /// <param name="args">The arguments after <c>all</c>.</param>
    /// <returns>The options and every diagnostic.</returns>
    public static AllArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<string> shared = [];
        List<string> vbspSection = [];
        List<string> vvisSection = [];
        List<string> vradSection = [];
        List<string> current = shared;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case VbspSection: current = vbspSection; continue;
                case VvisSection: current = vvisSection; continue;
                case VradSection: current = vradSection; continue;
                default: current.Add(arg); break;
            }
        }

        List<CompileDiagnostic> diagnostics = [];
        void Problem(string message) =>
            diagnostics.Add(new CompileDiagnostic(ChainArgument, DiagnosticSeverity.Error, message));

        List<string> toAll = [];
        List<string> toVvis = [];
        List<string> toVrad = [];
        List<string> toVbsp = [];
        string? map = null;
        string? game = null;
        int? threads = null;
        bool listCompliance = false;
        bool noCache = false;
        bool noWrite = false;
        bool? tighten = null;
    bool incremental = false;
    string? cacheDir = null;
    string? gpuDeviceMatch = null;
    int? gpuRaysPerSlab = null;
    int? gpuPipelineDepth = null;
    bool overlap = false;
    string? recordContent = null;

        for (int i = 0; i < shared.Count; i++)
        {
            string arg = shared[i];
            string key = arg.ToUpperInvariant();

            bool TakeValue(out string value)
            {
                if (i + 1 < shared.Count)
                {
                    value = shared[++i];
                    return true;
                }

                Problem($"{arg} needs a value");
                value = string.Empty;
                return false;
            }

            switch (key)
            {
                case "-GAME" or "-VPROJECT":
                    if (TakeValue(out string g))
                    {
                        game = g;
                    }

                    break;

                case "-THREADS":
                    if (TakeValue(out string t))
                    {
                        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                        {
                            threads = n;
                        }
                        else
                        {
                            Problem($"-threads \"{t}\" is not a positive number");
                        }
                    }

                    break;

                case "-COMPLIANCE":
                    if (TakeValue(out string c))
                    {
                        toAll.Add(arg);
                        toAll.Add(c);
                    }

                    break;

                case "-V" or "-VERBOSE":
                    toAll.Add(arg);
                    break;

                case "-FAST":
                    toVvis.Add(arg);
                    toVrad.Add(arg);
                    break;

                case "-TIGHTEN":
                    if (tighten is false)
                    {
                        Problem("-tighten and -loose: the chain has one vvis arm, give it once");
                    }

                    tighten = true;
                    break;

                case "-LOOSE":
                    if (tighten is true)
                    {
                        Problem("-tighten and -loose: the chain has one vvis arm, give it once");
                    }

                    tighten = false;
                    break;

                case "-COOKER" or "-VPHYSICS":
                    if (TakeValue(out string v))
                    {
                        toVbsp.Add(arg);
                        toVbsp.Add(v);
                    }

                    break;

                case "-LISTCOMPLIANCE":
                    listCompliance = true;
                    break;

                case "-NOCACHE":
                    noCache = true;
                    break;

                case "-INCREMENTAL":
                    incremental = true;
                    break;

                case "-OVERLAP":
                    overlap = true;
                    break;

                case "-CACHE-DIR":
                    if (TakeValue(out string dir))
                    {
                        cacheDir = dir;
                    }

                    break;

                case "-GPU":
                    if (TakeValue(out string device))
                    {
                        gpuDeviceMatch = device;
                    }

                    break;

                case "-GPU_SLABS":
                    if (TakeValue(out string slab))
                    {
                        if (int.TryParse(slab, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rays) && rays >= 1)
                        {
                            gpuRaysPerSlab = rays;
                        }
                        else
                        {
                            Problem($"-gpu_slabs \"{slab}\" is not a whole number of at least 1");
                        }
                    }

                    break;

                case "-GPU_DEPTH":
                    if (TakeValue(out string depthText))
                    {
                        if (int.TryParse(depthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int depth)
                            && depth >= 1 && depth <= MapTools.Rad.Light.RadWorld.MaxFacelightPipelineDepth)
                        {
                            gpuPipelineDepth = depth;
                        }
                        else
                        {
                            Problem(
                                $"-gpu_depth \"{depthText}\" is not a whole number from 1 to "
                                + $"{MapTools.Rad.Light.RadWorld.MaxFacelightPipelineDepth}");
                        }
                    }

                    break;

                default:
                    if (string.Equals(arg, NoWriteSwitch, StringComparison.Ordinal))
                    {
                        noWrite = true;
                    }
                    else if (string.Equals(arg, RecordContentSwitch, StringComparison.Ordinal))
                    {
                        // One zip per run: a second path would be a silent
                        // choice of which recording the user meant.
                        if (TakeValue(out string zip))
                        {
                            if (recordContent is not null)
                            {
                                Problem($"{RecordContentSwitch} \"{recordContent}\" and \"{zip}\": the chain writes one bundle, give it once");
                            }

                            recordContent = zip;
                        }
                    }
                    else if (arg.StartsWith('-'))
                    {
                        Problem(
                            $"\"{arg}\" is not a whole-chain option: put it after {VbspSection}, {VvisSection} or {VradSection}");
                    }
                    else if (map is null)
                    {
                        map = arg;
                    }
                    else
                    {
                        Problem($"two maps named: \"{map}\" and \"{arg}\"");
                    }

                    break;
            }
        }

        if (listCompliance)
        {
            return new AllArgs(
                VbspOptions.Default, VvisOptions.Default, VradOptions.Default, map, game, threads,
                null, true, noCache, noWrite, diagnostics);
        }

        if (map is null)
        {
            Problem("no map named (it goes before the first section)");
        }

        // vvis: -tighten/-loose are not stock's, so the section's copy comes out too.
        List<string> vvisArgs = [];
        foreach (string arg in vvisSection)
        {
            if (string.Equals(arg, VvisCommand.TightenSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (tighten is false)
                {
                    Problem("-tighten and -loose: the chain has one vvis arm, give it once");
                }

                tighten = true;
            }
            else if (string.Equals(arg, VvisCommand.LooseSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (tighten is true)
                {
                    Problem("-tighten and -loose: the chain has one vvis arm, give it once");
                }

                tighten = false;
            }
            else
            {
                vvisArgs.Add(arg);
            }
        }

        string mapArg = map ?? "unnamed";
        StockArgsResult<VbspOptions> vbsp = StockArgs.ParseVbsp([.. toAll, .. toVbsp, .. vbspSection, mapArg]);
        StockArgsResult<VvisOptions> vvis = StockArgs.ParseVvis([.. toAll, .. toVvis, .. vvisArgs, mapArg]);
        StockArgsResult<VradOptions> vrad = StockArgs.ParseVrad([.. toAll, .. toVrad, .. vradSection, mapArg]);

        Prefix(diagnostics, "vbsp", vbsp.Diagnostics);
        Prefix(diagnostics, "vvis", vvis.Diagnostics);
        Prefix(diagnostics, "vrad", vrad.Diagnostics);

        foreach (int? section in new[] { vbsp.Threads, vvis.Threads, vrad.Threads })
        {
            if (section is not int n)
            {
                continue;
            }

            if (threads is int chosen && chosen != n)
            {
                Problem($"-threads {chosen} and -threads {n}: the chain has one degree, give it once");
            }

            threads = n;
        }

        if (vbsp.ListCompliance || vvis.ListCompliance || vrad.ListCompliance)
        {
            listCompliance = true;
        }

        return new AllArgs(
            vbsp.Options,
            // `tighten is null` keeps the parsed record, whose Tighten is the
            // promoted default (on).
            tighten is null ? vvis.Options : vvis.Options with { Tighten = tighten.Value },
            vrad.Options,
            map,
            game ?? vbsp.GameDirectory ?? vvis.GameDirectory ?? vrad.GameDirectory,
            threads,
            vrad.Options.LightsFile,
            listCompliance,
            noCache || vbsp.NoCache,
            noWrite,
            diagnostics)
        {
            Format = vbsp.Format,
            PresetName = vbsp.PresetName,
            NoFormatDetect = vbsp.NoFormatDetect,
            NoToolsArgs = vbsp.NoToolsArgs,
            Incremental = incremental || vbsp.Incremental,
            CacheDir = vbsp.CachePath ?? cacheDir,
            GpuDeviceMatch = vrad.GpuDeviceMatch ?? gpuDeviceMatch,
            GpuRaysPerSlab = vrad.GpuRaysPerSlab ?? gpuRaysPerSlab,
            GpuPipelineDepth = vrad.GpuPipelineDepth ?? gpuPipelineDepth,
            Overlap = overlap,
            RecordContent = recordContent,
        };
    }

    /// <summary>
    /// The end-of-run report's cache block. The per-
    /// model hit/miss accounting is <c>CacheRunReport</c>'s own line,
    /// which <see cref="MapCompiler"/> writes to the .log; this is the
    /// chain-level posture line.
    /// </summary>
    /// <param name="noCache">Whether <c>-nocache</c> was given.</param>
    /// <param name="elapsed">The run's total wall time.</param>
    /// <param name="incremental">Whether <c>-incremental</c> ran the store.</param>
    /// <returns>The line.</returns>
    public static string CacheReport(bool noCache, TimeSpan elapsed, bool incremental = false) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"cache  {(noCache ? "off (-nocache): nothing reused" : incremental ? "incremental: the cache: line says what was reused" : "off (-incremental not given): nothing reused")}; total {elapsed.TotalSeconds:F1} s this run");

    /// <summary>
    /// Fills the request's two host-owned members: the <c>-incremental</c>
    /// collision-cache store (plan 10a, ruling Q7 — beside the map, or in
    /// <c>-cache-dir</c>; off unless asked, so the flag-free path opens
    /// nothing) and the <c>-gpu</c> tracer factory (plan 10c — vrad asks it
    /// once the casters exist; a decline is one VRAD0707 warning and the CPU
    /// KD tracer, never a crash).
    /// </summary>
    /// <param name="request">The request the chain built.</param>
    /// <param name="parsed">The parsed command line.</param>
    /// <param name="mapDirectory">The directory holding the <c>.vmf</c>.</param>
    /// <param name="mapName">The level's base name.</param>
    /// <param name="presetName">The resolved format preset, or null.</param>
    /// <param name="cancellationToken">Cancels the store open.</param>
    /// <returns>The request with its host members set.</returns>
    /// <remarks>
    /// Public because the CLI gets no <c>InternalsVisibleTo</c>: the facts pin
    /// exactly what the chain wires. The caller disposes
    /// <see cref="CompileRequest.Cache"/> when the run ends.
    /// </remarks>
    public static async Task<CompileRequest> WithBackendsAsync(
        CompileRequest request,
        AllArgs parsed,
        string mapDirectory,
        string mapName,
        string? presetName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parsed);

        ICacheStore? cache = parsed.Incremental && !parsed.NoCache
            ? await HostBackends.OpenCacheAsync(parsed.CacheDir, mapDirectory, mapName, cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (cache is null && parsed.Incremental && !parsed.NoCache)
        {
            // P15: a requested store that cannot open is LOUD. The shipped
            // tree once carried no SQLite package, `-incremental` opened no
            // store, and every corpus run reported its zeros without ever
            // saying the cache was off — a fleet default-off looked like a
            // cache that never engages. The compile still runs (a cache miss
            // never kills a compile); this line only ends the silence.
            request.Log?.Write(
                DiagnosticSeverity.Info,
                "cache: -incremental opened no store ("
                + (HostBackends.MissingReason ?? "the store could not be opened")
                + ") - everything cooked this run");
        }

        IGpuTracerFactory? gpuFactory = parsed.GpuDeviceMatch is null
            ? null
            : new HostBackends.GpuFactory(parsed.GpuDeviceMatch, parsed.GpuRaysPerSlab);

        return request with
        {
            Cache = cache,
            ContextTags = HostBackends.ContextTagsFor(presetName, request.CollisionCooker),
            TracerFactory = gpuFactory,
            GpuPipelineDepth = parsed.GpuPipelineDepth ?? 0,
        };
    }

    /// <summary>Runs <c>ssmap all</c>.</summary>
    /// <param name="disk">The host file system, rooted at <c>/</c>.</param>
    /// <param name="searchRoots">Where game installs are, for the cooker's library and <c>|appid_N|</c> mounts.</param>
    /// <param name="args">The arguments after <c>all</c>.</param>
    /// <param name="output">Where the commentary goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        PhysicalFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(searchRoots);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        AllArgs parsed = Parse(args);

        if (parsed.ListCompliance && !parsed.HasErrors)
        {
            await output.WriteAsync(ComplianceCatalogue.Format(CompileTools.Vbsp | CompileTools.Vvis | CompileTools.Vrad))
                .ConfigureAwait(false);
            return Program.ExitSuccess;
        }

        foreach (CompileDiagnostic d in parsed.Diagnostics)
        {
            await output.WriteLineAsync($"{d.Code}: {d.Message}").ConfigureAwait(false);
        }

        if (parsed.HasErrors || parsed.MapPath is null)
        {
            await output.WriteLineAsync(
                $"usage: ssmap all [chain options] <map> [{VbspSection} <vbsp options>] [{VvisSection} <vvis options>] [{VradSection} <vrad options>]")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        VbspCommand.MapPaths paths = VbspCommand.MapPaths.From(parsed.MapPath);
        string? mapFile = await VbspCommand.ResolveMapFileAsync(disk, paths, cancellationToken).ConfigureAwait(false);
        if (mapFile is null)
        {
            await output.WriteLineAsync($"ssmap all: no such map: {paths.Name}").ConfigureAwait(false);
            return VbspCommand.ExitFailed;
        }

        // Only the native cooker relaunches (with this whole command line);
        // managed and none never do.
        VbspHost.CookerSetup setup = await VbspHost.OpenCookerAsync(
            disk, searchRoots, parsed.Vbsp, ["all", .. args], "ssmap all", output, cancellationToken)
            .ConfigureAwait(false);
        if (setup.Exit is int exit)
        {
            return exit;
        }

        await using ICollisionCooker? cooker = setup.Cooker;
        return await CompileAsync(
            disk, searchRoots, parsed, paths, mapFile, cooker, CommandLine(args), output, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> CompileAsync(
        PhysicalFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        AllArgs parsed,
        VbspCommand.MapPaths paths,
        string mapFile,
        ICollisionCooker? cooker,
        string commandLine,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        long start = TimeProvider.System.GetTimestamp();

        string gameDirectory = parsed.GameDirectory is null
            ? Path.GetDirectoryName(Path.GetDirectoryName(paths.Source)!)!
            : Path.GetFullPath(parsed.GameDirectory);

        GameContentMounter.Result mounted;
        try
        {
            mounted = await VbspCommand.MountGameAsync(
                disk, gameDirectory, VbspHost.SteamFor(disk, searchRoots), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            await output.WriteLineAsync($"ssmap all: cannot mount {gameDirectory}: {exception.Message}").ConfigureAwait(false);
            return VbspCommand.ExitFailed;
        }

        await VbspCommand.WriteSkippedAsync(mounted, "ssmap all", output).ConfigureAwait(false);

        // The format pipeline, mounted-gameinfo first (defaults -> appid
        // preset -> Tools key -> CLI), same as `ssmap vbsp`.
        FormatResolution.Result resolution = FormatResolution.Resolve(
            parsed.Format,
            parsed.PresetName,
            parsed.NoFormatDetect,
            parsed.NoToolsArgs,
            mounted.GameInfo);
        foreach (CompileDiagnostic d in resolution.Diagnostics)
        {
            await output.WriteLineAsync($"{d.Severity} {d.Code}: {d.Message}").ConfigureAwait(false);
        }


        await output.WriteLineAsync(
            $"ssmap all: format preset={resolution.Resolved.PresetName ?? "(default)"} "
            + $"bsp={resolution.Resolved.BspVersion} light={resolution.Resolved.WorldLightVersion} "
            + $"staticprops={resolution.Resolved.StaticPropsToken ?? "(default)"} "
            + $"appid={resolution.Resolved.DetectedSteamAppId}")
            .ConfigureAwait(false);

        // --record-content: the compile gets the recording wrapper in place
        // of the mount, UNDER the loose files below, so what is recorded is
        // game content at its content-relative path. Everything the chain
        // reads from the game -- vbsp's materials and props, vrad's
        // lights.rad, materials, models and textures -- goes through this one
        // object; the loose files are added to the bundle by name.
        ContentRecording? recording = parsed.RecordContent is null ? null : new(mounted.Content, disk);
        IContentFileSystem game = recording is null ? mounted.Content : recording.Content;

        // vrad's <map>.rad beside the map and its -lights file, over the game,
        // as `ssmap vrad` layers them.
        string mapName = Path.GetFileName(paths.Source);
        VradCommand.LooseFileContent content = new(disk, game);
        content.Add(mapName + ".rad", paths.Source + ".rad");
        recording?.AddLoose(ContentRecording.LoosePath(paths.Source + ".rad"), VPath.Create(paths.Source + ".rad"), "map");
        if (parsed.LightsFile is { Length: > 0 } lights)
        {
            content.Add(lights, Path.GetFullPath(lights));
            recording?.AddLoose(ContentRecording.LoosePath(lights), VPath.Create(Path.GetFullPath(lights)), "lights");
        }

        // The fallback is asked of the recorded game, so the manifest shows
        // the game's own lights.rad lookup missing; the bundle then carries
        // the fallback at the root, where the replay's search paths find it.
        string? fallback = await LightsRadLocator.AddFallbackAsync(
            content, disk, game, mounted.GameInfo, VbspHost.SteamFor(disk, searchRoots), output, cancellationToken)
            .ConfigureAwait(false);
        if (fallback is not null)
        {
            recording?.AddLoose(LightsRadLocator.FileName, VPath.Create(fallback), "fallback");
        }

        ContentRecordTarget? record = recording is null
            ? null
            : new ContentRecordTarget(
                recording,
                disk,
                VPath.Create(Path.GetFullPath(parsed.RecordContent!)),
                await ReadGameInfoTextAsync(disk, gameDirectory, cancellationToken).ConfigureAwait(false),
                commandLine);

        // One thread pool for the whole chain: -threads is its size, and the
        // managed cooker's cooks run on it too rather than on the .NET pool.
        CompileParallelism parallel = ChainParallelism(parsed);
        using CompilePool pool = new(parallel.MaxDegree);
        return await CompileOnPoolAsync(
            disk, parsed, paths, mapFile, CookOnPool(cooker, pool), output, start, resolution, content, mapName,
            parallel with { Pool = pool }, record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The recorded game's own <c>gameinfo.txt</c>, which the bundle's is made from.</summary>
    private static async Task<string?> ReadGameInfoTextAsync(
        IFileSystem disk, string gameDirectory, CancellationToken cancellationToken)
    {
        VPath path = VPath.Create(Path.Combine(gameDirectory, "gameinfo.txt"));
        if (!await disk.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using System.Buffers.IMemoryOwner<byte> bytes = await disk.ReadAllAsync(path, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes.Memory.Span);
    }

    /// <summary>The command line as the manifest records it: <c>ssmap all</c> and each argument, quoted when it has a space.</summary>
    /// <param name="args">The arguments after <c>all</c>.</param>
    /// <returns>One line.</returns>
    public static string CommandLine(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return string.Join(
            ' ',
            new[] { "ssmap", "all" }.Concat(args.Select(static a => a.Length == 0 || a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)));
    }

    /// <summary>
    /// Runs the chain and, when <paramref name="record"/> is given, writes its
    /// content bundle whatever the compile's outcome.
    /// </summary>
    /// <param name="request">The wired request.</param>
    /// <param name="record">What <see cref="RecordContentSwitch"/> asked for, or null.</param>
    /// <param name="output">Where the commentary goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The result, or null when the compile failed, and the exit code.</returns>
    /// <remarks>
    /// <para>
    /// A compile that fails part way still writes the bundle: the files read
    /// before the failure are exactly what is needed to reproduce it, which is
    /// usually why the bundle was asked for. A <see cref="MapCompileException"/>
    /// is the compile's own failure and exits <see cref="VbspCommand.ExitFailed"/>
    /// after the bundle; anything else is written up the same way and then
    /// rethrown, so the host's handler still reports it as it would have.
    /// </para>
    /// <para>
    /// A cancelled compile writes nothing: the user stopped the run, and the
    /// token that would write the zip is already cancelled.
    /// </para>
    /// <para>
    /// A bundle that cannot be written (the content changed under the
    /// compile, the disk is full) is reported and turns a successful compile's
    /// exit code into a failure, since the run did not do what was asked; it
    /// never hides the compile's own error.
    /// </para>
    /// <para>
    /// Public because the CLI gets no <c>InternalsVisibleTo</c>: the facts run
    /// this exact seam against in-memory content.
    /// </para>
    /// </remarks>
    public static async Task<ChainOutcome> CompileRecordingAsync(
        CompileRequest request,
        ContentRecordTarget? record,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);

        CompileResult? result = null;
        int exit = Program.ExitSuccess;
        try
        {
            result = await MapCompiler.CompileAsync(request, null, cancellationToken).ConfigureAwait(false);
        }
        catch (MapCompileException exception)
        {
            await output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            exit = VbspCommand.ExitFailed;
        }
        catch (Exception exception) when (record is not null && exception is not OperationCanceledException)
        {
            _ = await WriteRecordAsync(record, output, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (record is not null
            && !await WriteRecordAsync(record, output, cancellationToken).ConfigureAwait(false)
            && exit == Program.ExitSuccess)
        {
            exit = VbspCommand.ExitFailed;
        }

        return new ChainOutcome(result, exit);
    }

    private static async Task<bool> WriteRecordAsync(
        ContentRecordTarget record, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            ContentBundleSummary summary = await record.Recording.WriteAsync(
                record.Files, record.ZipPath, record.GameInfoText, record.CommandLine, cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync(summary.Line("/" + record.ZipPath)).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap all: {RecordContentSwitch} failed: {exception.Message}").ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>The chain's parallelism: <c>-threads</c> when given, else every processor.</summary>
    /// <param name="parsed">The parsed chain arguments.</param>
    /// <returns>The parallelism, with no pool yet.</returns>
    /// <remarks>
    /// Shared with <c>ssmap bench</c>, whose chain cells must run the compile
    /// exactly as <c>ssmap all</c> does or they time something else.
    /// </remarks>
    public static CompileParallelism ChainParallelism(AllArgs parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        return parsed.Threads is int degree
            ? new CompileParallelism { MaxDegree = degree }
            : CompileParallelism.Default;
    }

    /// <summary>
    /// The cooker one compile on <paramref name="pool"/> should use: a managed
    /// cooker's view whose cooks run on the pool, or any other cooker as it is.
    /// </summary>
    /// <param name="cooker">The chain's cooker, or null for none.</param>
    /// <param name="pool">The chain's pool.</param>
    /// <returns>The cooker to put in that compile's request.</returns>
    /// <remarks>
    /// <para>
    /// Without this the cooks run on the .NET thread pool, beside the
    /// chain's own <c>-threads</c> workers rather than among them, so a
    /// one-thread compile would not be one thread.
    /// </para>
    /// <para>
    /// A view, not a change to the cooker: this used to set the cooker's
    /// scheduler for the compile and reset it afterwards, and a host sharing
    /// one cooker between concurrent compiles (as this pattern invites) then
    /// had one compile's reset move the other's cooks, or leave them on a
    /// disposed pool. The view is the compile's alone and needs no undoing;
    /// the cooker stays the caller's to dispose.
    /// </para>
    /// </remarks>
    public static ICollisionCooker? CookOnPool(ICollisionCooker? cooker, CompilePool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return cooker is ManagedCollisionCooker managed ? managed.On(pool.Scheduler) : cooker;
    }

    private static async Task<int> CompileOnPoolAsync(
        PhysicalFileSystem disk,
        AllArgs parsed,
        VbspCommand.MapPaths paths,
        string mapFile,
        ICollisionCooker? cooker,
        TextWriter output,
        long start,
        FormatResolution.Result resolution,
        VradCommand.LooseFileContent content,
        string mapName,
        CompileParallelism parallel,
        ContentRecordTarget? record,
        CancellationToken cancellationToken)
    {
        // The -incremental store and the -gpu factory (plans 10a/10c), on the
        // same public seam the facts pin.
        CompileRequest request = await WithBackendsAsync(
            new CompileRequest
            {
                Source = MapSource.FromVmf(disk, VPath.Create(mapFile)),
                Content = content,
                Vbsp = parsed.Vbsp with { Format = resolution.Resolved },
                Vvis = parsed.Vvis,
                Vrad = parsed.Vrad,
                Parallel = parallel,
                Overlap = parsed.Overlap,
                CollisionCooker = cooker,
                Output = parsed.NoWrite
                    ? CompileOutput.InMemory
                    : CompileOutput.ToDirectory(disk, VPath.Create(Path.GetDirectoryName(paths.Source)!), mapName),
                Log = new WriterLog(output),
            },
            parsed,
            Path.GetDirectoryName(paths.Source)!,
            mapName,
            resolution.Resolved.PresetName,
            cancellationToken).ConfigureAwait(false);

        ChainOutcome outcome;
        try
        {
            outcome = await CompileRecordingAsync(request, record, output, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeCacheAsync(request).ConfigureAwait(false);
            throw;
        }

        if (outcome.Result is not { } result)
        {
            await DisposeCacheAsync(request).ConfigureAwait(false);
            return outcome.ExitCode;
        }

        // The Q12 line is already in the .log (MapCompiler flushed it with
        // the store still open); the run's lease on the file ends here.
        await DisposeCacheAsync(request).ConfigureAwait(false);

        foreach (VPath written in result.Written)
        {
            await output.WriteLineAsync($"wrote /{written}").ConfigureAwait(false);
        }

        string report = CacheReport(parsed.NoCache, TimeProvider.System.GetElapsedTime(start), parsed.Incremental);
        await output.WriteLineAsync(report).ConfigureAwait(false);

        // The block is appended to the .log too.
        if (!parsed.NoWrite && result.Written.Count > 0)
        {
            VPath log = VPath.Create(paths.Source + ".log");
            if (await disk.ExistsAsync(log, cancellationToken).ConfigureAwait(false))
            {
                byte[] old;
                using (System.Buffers.IMemoryOwner<byte> bytes = await disk.ReadAllAsync(log, cancellationToken).ConfigureAwait(false))
                {
                    old = bytes.Memory.ToArray();
                }

                byte[] line = System.Text.Encoding.UTF8.GetBytes(report + "\n");
                await disk.ReplaceAsync(
                    log,
                    async (stream, token) =>
                    {
                        await stream.WriteAsync(old, token).ConfigureAwait(false);
                        await stream.WriteAsync(line, token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return outcome.ExitCode;
    }

    /// <summary>Closes the run's cache store, if the line asked for one.</summary>
    /// <param name="request">The wired request.</param>
    private static async Task DisposeCacheAsync(CompileRequest request)
    {
        if (request.Cache is { } store)
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void Prefix(List<CompileDiagnostic> into, string stage, IReadOnlyList<CompileDiagnostic> from)
    {
        foreach (CompileDiagnostic d in from)
        {
            into.Add(d with { Message = $"{stage}: {d.Message}" });
        }
    }

    /// <summary>The library's commentary, on this command's writer.</summary>
    private sealed class WriterLog(TextWriter output) : ICompileLog
    {
        public void Write(DiagnosticSeverity severity, string message) => output.WriteLine(message);

        public void Report(CompileDiagnostic diagnostic) =>
            output.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
    }
}

/// <summary>What <see cref="AllCommand.CompileRecordingAsync"/> ended with.</summary>
/// <param name="Result">The compile's result, or null when it failed.</param>
/// <param name="ExitCode">The process exit code the chain ends with.</param>
public sealed record ChainOutcome(CompileResult? Result, int ExitCode);

/// <summary>What <see cref="AllCommand.RecordContentSwitch"/> needs once the compile ends.</summary>
/// <param name="Recording">The recording the compile read through.</param>
/// <param name="Files">Where the zip is written.</param>
/// <param name="ZipPath">The zip's path.</param>
/// <param name="GameInfoText">The recorded game's <c>gameinfo.txt</c>, or null.</param>
/// <param name="CommandLine">The command line, for the manifest.</param>
public sealed record ContentRecordTarget(
    ContentRecording Recording,
    IFileSystem Files,
    VPath ZipPath,
    string? GameInfoText,
    string CommandLine);
