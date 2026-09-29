//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap vrad</c>: the lighting stage, spelled the way stock spells it.
/// </summary>
/// <remarks>
/// <para>
/// A thin client of <see cref="Vrad.LightAsync(SourceSharp.MapFormats.Bsp.BspData, VradContext, CancellationToken)"/>; everything printed here is a
/// property of the <see cref="RadResult"/> the library returned.
/// </para>
/// <para>
/// Stock reads <c>lights.rad</c> from the game's search path and
/// <c>&lt;map&gt;.rad</c> from beside the <c>.bsp</c>;
/// the library reads both through one <see cref="IContentFileSystem"/>, so this
/// command layers the map's own <c>.rad</c> and the <c>-lights</c> file over
/// the mounted game. Stock's last resort -- <c>lights.rad</c> beside
/// <c>vrad.exe</c>, which for a Steam game is the app's <c>bin</c> folder -- is
/// the host's to find (<see cref="LightsRadLocator"/>), and is added as one
/// more loose file when the game has none.
/// </para>
/// </remarks>
public static class VradCommand
{
    /// <summary>The exit code for a map that could not be lit.</summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// Prints a per-stage wall clock and the work counters after the compile
    /// (<c>bench &lt;stage&gt; &lt;seconds&gt;s</c> lines, as <c>vvis --bench</c>
    /// prints them). Not a stock option: it is taken out before the stock
    /// arguments are parsed.
    /// </summary>
    public const string BenchSwitch = "--bench";

    /// <summary>
    /// Lights the map even when no game content can be mounted: a missing
    /// <c>gameinfo.txt</c>, or one whose search paths cannot be mounted (an
    /// <c>|appid_N|</c> app that is not installed, no Steam library), becomes
    /// a note and the compile carries on with only the loose files (the
    /// level's <c>.rad</c> and <c>-lights</c>). Not a stock option: it is
    /// taken out before the stock arguments are parsed.
    /// </summary>
    /// <remarks>
    /// Without it a mount that fails is a failed compile (<see cref="ExitFailed"/>),
    /// as it is in stock vrad and in <c>ssmap vbsp</c> and <c>ssmap all</c>.
    /// Lighting without content is not the same compile: no material's
    /// reflectivity, no <c>lights.rad</c> texlights, no model for a static
    /// prop's shadow. A host that asked for a game and got none would
    /// otherwise receive a wrongly lit <c>.bsp</c> and a success code, so
    /// carrying on is something the caller has to ask for by name.
    /// </remarks>
    public const string NoGameContentSwitch = "--no-game-content";

    /// <summary>Runs one <c>vrad</c> invocation.</summary>
    /// <param name="fileSystem">Where maps and game content are read and written.</param>
    /// <param name="args">The arguments after <c>vrad</c>.</param>
    /// <param name="output">Where the running commentary goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>
    /// <see cref="Program.ExitSuccess"/>, <see cref="ExitFailed"/> or
    /// <see cref="Program.ExitUsage"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default) =>
        RunAsync(fileSystem, args, steam: null, output, cancellationToken);

    /// <summary>Runs one <c>vrad</c> invocation, with a Steam library for the game mount.</summary>
    /// <param name="fileSystem">Where maps and game content are read and written.</param>
    /// <param name="args">The arguments after <c>vrad</c>.</param>
    /// <param name="steam">
    /// Resolves the gameinfo's <c>|appid_N|</c> search paths, as vbsp's mount
    /// does; null when there is no Steam library, and a gameinfo that names
    /// one then fails to mount (a failed compile, unless
    /// <see cref="NoGameContentSwitch"/> is given).
    /// </param>
    /// <param name="output">Where the running commentary goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>
    /// <see cref="Program.ExitSuccess"/>, <see cref="ExitFailed"/> or
    /// <see cref="Program.ExitUsage"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        ISteamAppLocator? steam,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        bool bench = args.Contains(BenchSwitch, StringComparer.Ordinal);
        bool withoutContent = args.Contains(NoGameContentSwitch, StringComparer.Ordinal);
        StockArgsResult<VradOptions> parsed = StockArgs.ParseVrad(
            [.. args.Where(a => !string.Equals(a, BenchSwitch, StringComparison.Ordinal)
                && !string.Equals(a, NoGameContentSwitch, StringComparison.Ordinal))]);

        if (parsed.ListCompliance && !parsed.HasErrors)
        {
            await output.WriteAsync(ComplianceCatalogue.Format(CompileTools.Vrad)).ConfigureAwait(false);
            return Program.ExitSuccess;
        }

        foreach (CompileDiagnostic diagnostic in parsed.Diagnostics)
        {
            await output.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}").ConfigureAwait(false);
        }

        if (parsed.HasErrors || parsed.MapPath is null)
        {
            await output.WriteLineAsync("usage: ssmap vrad [stock options] <map>").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // Stock: the extension is stripped, the
        // base name is the level's name, and ".bsp" is put back.
        string full = Path.GetFullPath(parsed.MapPath);
        string source = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full));
        string bspPath = source + ".bsp";
        string mapName = Path.GetFileName(source);

        if (!VPath.TryCreate(bspPath, out VPath bsp))
        {
            await output.WriteLineAsync($"ssmap vrad: \"{bspPath}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        if (!await fileSystem.ExistsAsync(bsp, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync($"ssmap vrad: no such file: {bspPath}").ConfigureAwait(false);
            return ExitFailed;
        }

        Stopwatch clock = Stopwatch.StartNew();

        (bool mountedOk, IContentFileSystem? game, GameInfo? gameInfo) = await MountGameAsync(
            fileSystem, parsed.GameDirectory, source, steam, withoutContent, output, cancellationToken).ConfigureAwait(false);
        if (!mountedOk)
        {
            return ExitFailed;
        }

        LooseFileContent content = new(fileSystem, game);
        content.Add(mapName + ".rad", source + ".rad");
        if (parsed.Options.LightsFile is { Length: > 0 } lights)
        {
            content.Add(lights, Path.GetFullPath(lights));
        }

        // Stock's last resort, lights.rad beside the tool: for a Steam game,
        // the app's bin folder (LightsRadLocator).
        await LightsRadLocator.AddFallbackAsync(content, fileSystem, game, gameInfo, steam, output, cancellationToken)
            .ConfigureAwait(false);

        await output.WriteLineAsync($"Loading {bspPath}").ConfigureAwait(false);
        BspData map;
        await using (Stream stream = await fileSystem.OpenReadAsync(bsp, cancellationToken).ConfigureAwait(false))
        {
            map = await BspFile.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        VvisCommand.StageClock? stageClock = bench ? new VvisCommand.StageClock() : null;
        VradContext contextBase = new()
        {
            Options = parsed.Options,
            MapName = mapName,
            Content = content,
            Progress = stageClock,
            Parallelism = parsed.Threads is int degree && degree > 0
                ? new CompileParallelism { MaxDegree = degree }
                : CompileParallelism.Default,
        };

        // The -gpu seam (plan 10c): the same host factory the chain wires. A
        // decline is one VRAD0707 warning in the result and the CPU KD tracer.
        VradContext context = parsed.GpuDeviceMatch is null
            ? contextBase
            : contextBase with
            {
                GpuTracerFactory = new HostBackends.GpuFactory(
                    parsed.GpuDeviceMatch, parsed.GpuRaysPerSlab),
                GpuPipelineDepth = parsed.GpuPipelineDepth ?? 0,
            };

        RadResult result;
        try
        {
            result = await Vrad.LightAsync(map, context, cancellationToken).ConfigureAwait(false);
        }
        catch (MapCompileException exception)
        {
            await output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        await WriteResultAsync(result, output).ConfigureAwait(false);
        if (stageClock is not null)
        {
            await WriteBenchAsync(stageClock, result, output).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"Writing {bspPath}").ConfigureAwait(false);
        using (MemoryStream buffer = new())
        {
            await BspFile.SaveAsync(map, buffer, BspWriteMode.Canonical, cancellationToken).ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();
            await fileSystem.ReplaceAsync(
                bsp,
                async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture, $"{clock.Elapsed.TotalSeconds:F1} seconds elapsed")).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// The game named by <c>-game</c>, or the one two directories above the
    /// map (<c>game/maps/x.bsp</c>) when there is no <c>-game</c>, the way
    /// <c>ssmap vbsp</c> finds it.
    /// </summary>
    /// <remarks>
    /// A game that cannot be mounted (no <c>gameinfo.txt</c> there, or a mount
    /// that throws) fails the compile unless <paramref name="withoutContent"/>
    /// (<see cref="NoGameContentSwitch"/>) is set; then it is a note and the
    /// content is null. The parsed gameinfo is still returned when only the
    /// mount failed: the <c>lights.rad</c> fallback needs to know which Steam
    /// apps it names.
    /// </remarks>
    private static async Task<(bool Ok, IContentFileSystem? Content, GameInfo? GameInfo)> MountGameAsync(
        IFileSystem fileSystem,
        string? gameDirectory,
        string source,
        ISteamAppLocator? steam,
        bool withoutContent,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string directory = gameDirectory is null
            ? Path.GetDirectoryName(Path.GetDirectoryName(source)!) ?? "/"
            : Path.GetFullPath(gameDirectory);
        string gameInfo = Path.Combine(directory, "gameinfo.txt");

        if (!VPath.TryCreate(gameInfo, out VPath info)
            || !await fileSystem.ExistsAsync(info, cancellationToken).ConfigureAwait(false))
        {
            if (!withoutContent)
            {
                await output.WriteLineAsync(
                    $"ssmap vrad: cannot mount {directory}: no gameinfo.txt there; pass -game <dir>, "
                    + $"or {NoGameContentSwitch} to light without game content").ConfigureAwait(false);
                return (false, null, null);
            }

            await output.WriteLineAsync($"ssmap vrad: no game content (no gameinfo.txt in {directory})")
                .ConfigureAwait(false);
            return (true, null, null);
        }

        try
        {
            GameContentMounter.Result mounted = await VbspCommand.MountGameAsync(
                fileSystem, directory, steam, cancellationToken).ConfigureAwait(false);
            await VbspCommand.WriteSkippedAsync(mounted, "ssmap vrad", output).ConfigureAwait(false);
            return (true, mounted.Content, mounted.GameInfo);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            await output.WriteLineAsync($"ssmap vrad: cannot mount {directory}: {exception.Message}").ConfigureAwait(false);
            if (!withoutContent)
            {
                return (false, null, null);
            }

            await output.WriteLineAsync("ssmap vrad: no game content; lighting without it").ConfigureAwait(false);
            return (true, null, await ReadGameInfoAsync(fileSystem, info, cancellationToken).ConfigureAwait(false));
        }
    }

    private static async Task<GameInfo?> ReadGameInfoAsync(IFileSystem fileSystem, VPath info, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await fileSystem.OpenReadAsync(info, cancellationToken).ConfigureAwait(false);
            using StreamReader reader = new(stream);
            return GameInfo.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task WriteBenchAsync(VvisCommand.StageClock clock, RadResult result, TextWriter output)
    {
        foreach (string line in clock.Report())
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }

        foreach (RadPassResult pass in result.Passes)
        {
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"bench work {(pass.Hdr ? "hdr" : "ldr")} samples={pass.World.Samples} "
                + $"visibilityrays={pass.World.VisibilityRays} skyrays={pass.World.SkyRays} "
                + $"batches={pass.World.Batches} lightrecords={pass.World.LightRecords} "
                + $"culledlightrecords={pass.World.CulledLightRecords}")).ConfigureAwait(false);
        }

        if (result.Tracing is { } tracing)
        {
            foreach (string line in FormatTraceBench(tracing))
            {
                await output.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The bench's tracer lines: where the rays went and what the device did.
    /// </summary>
    /// <param name="report">The compile's tracer report (<see cref="RadResult.Tracing"/>).</param>
    /// <returns>
    /// One <c>bench trace</c> line, then a <c>bench gpu</c> line when a GPU was
    /// asked for: its counters when it answered, the reason when it was declined.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <c>bench trace</c> always says <c>gpu=off</c>, <c>gpu=declined</c> or
    /// <c>gpu=on</c>, so a run that fell back to the CPU cannot be mistaken
    /// for a GPU run, then the rays each way by query kind
    /// (<see cref="RayQueryKind"/>) and the batches each way, whose
    /// <c>gpu.*</c> and <c>cpu.*</c> add up to <c>rays=</c>. Then the worker
    /// time parked on batches still in flight, in all and per stage
    /// (<see cref="RayTraceMeter"/> says how each stage measures it).
    /// </para>
    /// <para>
    /// <c>bench gpu</c> for a GPU that answered: the batches handed to it
    /// (<c>requests</c>), the slabs submitted, the host-side span with a slab
    /// on the device (<c>busy</c>) and the part of it spent blocked on a fence
    /// (<c>fencewait</c>) -- <see cref="GpuTraceStatistics"/> says why the span
    /// and not device timestamps -- the host's side of moving the slabs
    /// (<c>pack</c>, writing rays where the device reads them, and
    /// <c>readback</c>, reading the answers out, each in all and per slab),
    /// whether the rays and the answers stay where the device reads and writes
    /// them (<c>direct</c>) or are staged and copied by the device each slab
    /// (<c>staged</c>, a device without resizable BAR), the deepest the slot
    /// ring ran against its size, and <c>fallbackrays</c>, the rays the hybrid
    /// sent to the CPU because the kernel cannot express their options. For a
    /// declined GPU, the backend's reason.
    /// </para>
    /// <para>
    /// Every number is invariant-culture and every duration seconds to three
    /// places, as the stage lines are; a fact pins the whole shape, so a
    /// script that parses it can rely on it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> FormatTraceBench(RayTraceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string status = report.Gpu switch
        {
            GpuTraceStatus.On => "on",
            GpuTraceStatus.Declined => "declined",
            _ => "off",
        };

        List<string> lines =
        [
            string.Create(
                CultureInfo.InvariantCulture,
                $"bench trace tracer={report.TracerIdentity} gpu={status} rays={report.TotalRays} "
                + $"gpu.visibility={report.GpuRays.Visibility} gpu.closest={report.GpuRays.Closest} gpu.sky={report.GpuRays.Sky} "
                + $"cpu.visibility={report.CpuRays.Visibility} cpu.closest={report.CpuRays.Closest} cpu.sky={report.CpuRays.Sky} "
                + $"batches.gpu={report.GpuRays.Batches} batches.cpu={report.CpuRays.Batches} "
                + $"parked={report.TotalParked.TotalSeconds:F3}s "
                + $"parked.facelights={report.ParkedIn(TraceWaitStage.Facelights).TotalSeconds:F3}s "
                + $"parked.bounce={report.ParkedIn(TraceWaitStage.Bounce).TotalSeconds:F3}s "
                + $"parked.other={report.ParkedIn(TraceWaitStage.Other).TotalSeconds:F3}s"),
        ];

        if (report.Gpu == GpuTraceStatus.Declined)
        {
            lines.Add("bench gpu declined: " + (report.GpuDeclineReason ?? "no reason given"));
        }
        else if (report.Gpu == GpuTraceStatus.On && report.Device is { } d)
        {
            double slabs = Math.Max(1, d.Slabs);
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"bench gpu requests={d.Requests} slabs={d.Slabs} busy={d.Busy.TotalSeconds:F3}s "
                + $"fencewait={d.FenceWait.TotalSeconds:F3}s "
                + $"pack={d.Pack.TotalSeconds:F3}s pack.perslab={d.Pack.TotalMilliseconds / slabs:F3}ms "
                + $"readback={d.Readback.TotalSeconds:F3}s readback.perslab={d.Readback.TotalMilliseconds / slabs:F3}ms "
                + $"rays={(d.RaysInPlace ? "direct" : "staged")} answers={(d.AnswersInPlace ? "direct" : "staged")} "
                + $"peakinflight={d.PeakSlabsInFlight}/{d.Slots} fallbackrays={report.CpuRays.Rays}"));
        }

        return lines;
    }

    private static async Task WriteResultAsync(RadResult result, TextWriter output)
    {
        foreach (RadPassResult pass in result.Passes)
        {
            string range = pass.Hdr ? "HDR" : "LDR";
            await output.WriteLineAsync($"[{range}] {pass.World.Faces} faces").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"[{range}] {pass.World.Subdivision.PatchesBefore} patches before subdivision").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"[{range}] {pass.World.Subdivision.PatchesAfter} patches after subdivision").ConfigureAwait(false);
            await output.WriteLineAsync($"[{range}] {pass.World.DirectLights} direct lights").ConfigureAwait(false);
            await output.WriteLineAsync($"[{range}] lightdata {pass.LightDataSize} bytes").ConfigureAwait(false);
        }

        foreach (CompileDiagnostic d in result.Diagnostics)
        {
            await output.WriteLineAsync($"{d.Severity} {d.Code}: {d.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A few named files from disk, found by name ahead of the game content:
    /// the level's <c>.rad</c> beside the map, and the <c>-lights</c> file.
    /// </summary>
    internal sealed class LooseFileContent(IFileSystem fileSystem, IContentFileSystem? inner) : IContentFileSystem
    {
        private readonly Dictionary<string, VPath> _files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Serves <paramref name="name"/> from the disk file <paramref name="path"/>.</summary>
        public void Add(string name, string path)
        {
            if (VPath.TryCreate(name, out VPath key) && VPath.TryCreate(path, out VPath file))
            {
                _files[key.Value] = file;
            }
        }

        public async ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default)
        {
            if (_files.TryGetValue(path.Value, out VPath file)
                && await fileSystem.ExistsAsync(file, cancellationToken).ConfigureAwait(false))
            {
                return new ContentSource(file, "map");
            }

            return inner is null ? null : await inner.ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default)
        {
            if (_files.TryGetValue(path.Value, out VPath file)
                && await fileSystem.ExistsAsync(file, cancellationToken).ConfigureAwait(false))
            {
                return await fileSystem.ReadAllAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return inner is null ? null : await inner.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<FileRange?> ReadRangeAsync(
            VPath path,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            // The same precedence as ReadAsync: a named loose file that exists
            // wins over the game's copy.
            if (_files.TryGetValue(path.Value, out VPath file)
                && await fileSystem.ExistsAsync(file, cancellationToken).ConfigureAwait(false))
            {
                return await fileSystem.ReadRangeAsync(file, offset, length, cancellationToken).ConfigureAwait(false);
            }

            return inner is null
                ? null
                : await inner.ReadRangeAsync(path, offset, length, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<VPath> EnumerateAsync(
            VPath directory,
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (inner is null)
            {
                yield break;
            }

            await foreach (VPath path in inner.EnumerateAsync(directory, searchPattern, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return path;
            }
        }
    }
}
