//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap vbsp [stock options] &lt;map&gt;</c>: stock vbsp's command line over
/// <see cref="Vbsp.CompileAsync(MapFile, VbspContext, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The file handling is stock's: the map argument
/// has its extension stripped to form the output base, a name with no
/// extension is tried as <c>.vmm</c> then <c>.vmf</c>, the stale
/// <c>.prt</c> and <c>.lin</c> are deleted before compiling, and the compile
/// writes <c>&lt;base&gt;.bsp</c>, plus <c>&lt;base&gt;.prt</c> for a sealed
/// map or <c>&lt;base&gt;.lin</c> for a leaked one.
/// </para>
/// <para>
/// Content comes from <c>-game</c>'s <c>gameinfo.txt</c>; with no
/// <c>-game</c> the map directory's parent is tried, as a Hammer layout has
/// it. Every write goes through the file system seam, and the BSP is written
/// with <see cref="IFileSystem.ReplaceAsync"/> semantics so a killed compile
/// never leaves half a file.
/// </para>
/// </remarks>
public static class VbspCommand
{
    /// <summary>The exit code for a map that could not be compiled.</summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// Prints the wall time of every vbsp stage after the compile, as
    /// <c>bench &lt;stage&gt; &lt;seconds&gt;s</c> lines and a total. Not a
    /// stock option: it is taken out before the stock arguments are parsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same instrument as vvis's and vrad's <c>--bench</c>
    /// (<see cref="VvisCommand.StageClock"/>): the compile already reports
    /// each stage transition to <see cref="VbspContext.Progress"/>, so timing
    /// it costs the library nothing and changes nothing it writes. Two stages
    /// are the command's own and are reported here: <c>vbsp.load</c>, reading
    /// and parsing the map, and <c>vbsp.write</c>, the portal and BSP files.
    /// </para>
    /// <para>
    /// The rows are the main flow's wall time. Work that overlaps it on other
    /// threads, such as the static-prop hulls cooked in the background while
    /// the tree is built, is billed to whichever stage was running when it
    /// was done, and only the wait for what is left of it shows up in its own
    /// stage.
    /// </para>
    /// </remarks>
    public const string BenchSwitch = "--bench";

    /// <summary>
    /// Runs one compile.
    /// </summary>
    /// <param name="fileSystem">Where the map, the content and the outputs live.</param>
    /// <param name="args">The stock-spelling arguments, without <c>vbsp</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default) =>
        RunAsync(fileSystem, args, null, output, cancellationToken);

    /// <summary>
    /// Runs one compile with a collision cooker (<see cref="VbspHost"/> makes it).
    /// </summary>
    /// <param name="fileSystem">Where the map, the content and the outputs live.</param>
    /// <param name="args">The stock-spelling arguments, without <c>vbsp</c>.</param>
    /// <param name="cooker">The cooker <c>EmitPhysCollision</c> drives, or null for no collision lumps.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        ICollisionCooker? cooker,
        TextWriter output,
        CancellationToken cancellationToken = default) =>
        RunAsync(fileSystem, args, cooker, null, output, cancellationToken);

    /// <summary>
    /// Runs one compile with a collision cooker and a Steam library for the
    /// gameinfo's <c>|appid_N|</c> search paths.
    /// </summary>
    /// <param name="fileSystem">Where the map, the content and the outputs live.</param>
    /// <param name="args">The stock-spelling arguments, without <c>vbsp</c>.</param>
    /// <param name="cooker">The cooker <c>EmitPhysCollision</c> drives, or null for no collision lumps.</param>
    /// <param name="steam">Finds a Steam app's install, or null when the gameinfo names none.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        ICollisionCooker? cooker,
        ISteamAppLocator? steam,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        bool bench = args.Contains(BenchSwitch, StringComparer.Ordinal);
        StockArgsResult<VbspOptions> parsed = StockArgs.ParseVbsp(
            bench ? [.. args.Where(a => !string.Equals(a, BenchSwitch, StringComparison.Ordinal))] : args);

        if (parsed.ListCompliance && !parsed.HasErrors)
        {
            await output.WriteAsync(ComplianceCatalogue.Format(CompileTools.Vbsp)).ConfigureAwait(false);
            return Program.ExitSuccess;
        }

        foreach (CompileDiagnostic diagnostic in parsed.Diagnostics)
        {
            await output.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}").ConfigureAwait(false);
        }

        if (parsed.HasErrors || parsed.MapPath is null)
        {
            await output.WriteLineAsync("usage: ssmap vbsp [stock options] <mapfile>").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        VbspOptions options = parsed.Options;
        MapPaths paths = MapPaths.From(parsed.MapPath);

        if (!VPath.TryCreate(paths.Bsp, out VPath bspPath)
            || !VPath.TryCreate(paths.Prt, out VPath prtPath)
            || !VPath.TryCreate(paths.Lin, out VPath linPath))
        {
            await output.WriteLineAsync($"ssmap vbsp: \"{parsed.MapPath}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string? mapFile = await ResolveMapFileAsync(fileSystem, paths, cancellationToken).ConfigureAwait(false);
        if (mapFile is null)
        {
            await output.WriteLineAsync($"ssmap vbsp: no such map: {paths.Name}").ConfigureAwait(false);
            return ExitFailed;
        }

        string gameDirectory = parsed.GameDirectory is null
            ? Path.GetDirectoryName(Path.GetDirectoryName(paths.Source)!)!
            : Path.GetFullPath(parsed.GameDirectory);

        Stopwatch clock = Stopwatch.StartNew();

        GameContentMounter.Result mounted;
        try
        {
            mounted = await MountGameAsync(fileSystem, gameDirectory, steam, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            await output.WriteLineAsync($"ssmap vbsp: cannot mount {gameDirectory}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitFailed;
        }

        await WriteSkippedAsync(mounted, "ssmap vbsp", output).ConfigureAwait(false);

        // The format pipeline, AFTER the mount (it reads the appid and Tools
        // key off the gameinfo the content was mounted from) and BEFORE the
        // compile. The parse collected the raw CLI overlay; this is where it
        // resolves: defaults -> appid auto-preset -> Tools splice -> CLI.
        FormatResolution.Result resolution = FormatResolution.Resolve(
            parsed.Format,
            parsed.PresetName,
            parsed.NoFormatDetect,
            parsed.NoToolsArgs,
            mounted.GameInfo);
        options = options with { Format = resolution.Resolved };
        await WriteDiagnosticsAsync(resolution.Diagnostics, output).ConfigureAwait(false);

        // Content provenance: every run names the format it wrote with and
        // the appid the detection saw.
        await output.WriteLineAsync(
            $"ssmap vbsp: format preset={resolution.Resolved.PresetName ?? "(default)"} "
            + $"bsp={resolution.Resolved.BspVersion} light={resolution.Resolved.WorldLightVersion} "
            + $"staticprops={resolution.Resolved.StaticPropsToken ?? "(default)"} "
            + $"appid={resolution.Resolved.DetectedSteamAppId}")
            .ConfigureAwait(false);

        VvisCommand.StageClock? stageClock = bench ? new VvisCommand.StageClock() : null;

        VbspContext context = new(options, mounted.Content)
        {
            Progress = stageClock,
            // mapbase: the file's base name, lowercased
#pragma warning disable CA1308 // strlwr
            MapBase = Path.GetFileName(paths.Source).ToLowerInvariant(),
#pragma warning restore CA1308
            CollisionCooker = cooker,
            Parallelism = parsed.Threads is int degree && degree > 0
                ? new CompileParallelism { MaxDegree = degree }
                : CompileParallelism.Default,
        };

        await output.WriteLineAsync($"ssmap vbsp: {mapFile}").ConfigureAwait(false);

        try
        {
            stageClock?.Report(new CompileProgress("vbsp.load", 0, 0));
            MapFileReader reader = new(context, fileSystem);
            MapFile map = await reader.LoadAsync(VPath.Create(mapFile), cancellationToken).ConfigureAwait(false);

            if (options.OnlyEnts || options.OnlyProps)
            {
                BspData existing;
                await using (Stream stream = await fileSystem.OpenReadAsync(bspPath, cancellationToken).ConfigureAwait(false))
                {
                    existing = await BspFile.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                BspData updated = await Vbsp.UpdateAsync(existing, map, context, cancellationToken).ConfigureAwait(false);
                await WriteBspAsync(fileSystem, bspPath, updated, options.Format, output, cancellationToken)
                    .ConfigureAwait(false);
                await WriteDiagnosticsAsync(context.Diagnostics, output).ConfigureAwait(false);
                return Program.ExitSuccess;
            }

            // delete portal and line files
            await DeleteIfPresentAsync(fileSystem, prtPath, cancellationToken).ConfigureAwait(false);
            await DeleteIfPresentAsync(fileSystem, linPath, cancellationToken).ConfigureAwait(false);

            VbspResult result = await Vbsp.CompileAsync(map, context, cancellationToken).ConfigureAwait(false);

            stageClock?.Report(new CompileProgress("vbsp.write", 0, 0));
            await WriteDiagnosticsAsync(result.Diagnostics, output).ConfigureAwait(false);

            if (result.Leak is not null)
            {
                await output.WriteLineAsync("**** leaked ****").ConfigureAwait(false);
                await WriteBytesAsync(fileSystem, linPath, LeakTrace.Write(result.Leak), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (result.Bsp is null)
            {
                await output.WriteLineAsync("--- MAP LEAKED ---").ConfigureAwait(false);
                return Program.ExitSuccess;
            }

            if (result.Portals is not null)
            {
                await output.WriteLineAsync($"writing {paths.Prt}...").ConfigureAwait(false);
                await WriteBytesAsync(
                    fileSystem, prtPath, result.Portals.ToBytes(PortalLineEnding.CrLf), cancellationToken)
                    .ConfigureAwait(false);
            }

            await WriteBspAsync(fileSystem, bspPath, result.Bsp, options.Format, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MapCompileException exception)
        {
            await output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        if (stageClock is not null)
        {
            foreach (string line in stageClock.Report())
            {
                await output.WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture, $"{clock.Elapsed.TotalSeconds:F1} seconds elapsed")).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>The file names one map argument stands for.</summary>
    /// <param name="Name">The argument as given, made absolute.</param>
    /// <param name="Source">The argument with its extension stripped: <c>source</c>.</param>
    /// <param name="Bsp"><c>source.bsp</c>.</param>
    /// <param name="Prt"><c>source.prt</c>.</param>
    /// <param name="Lin"><c>source.lin</c>.</param>
    public sealed record MapPaths(string Name, string Source, string Bsp, string Prt, string Lin)
    {
        /// <summary>
        /// Stock's derivation: regardless of the
        /// extension passed, it is stripped to get the source name, and the
        /// outputs append theirs.
        /// </summary>
        /// <param name="argument">The map argument.</param>
        /// <returns>The paths.</returns>
        public static MapPaths From(string argument)
        {
            ArgumentNullException.ThrowIfNull(argument);

            string name = Path.GetFullPath(argument);
            string source = Path.HasExtension(name)
                ? Path.Combine(Path.GetDirectoryName(name)!, Path.GetFileNameWithoutExtension(name))
                : name;

            return new MapPaths(name, source, source + ".bsp", source + ".prt", source + ".lin");
        }
    }

    /// <summary>
    /// Names every search path the mount found nothing at, and where it looked.
    /// </summary>
    /// <remarks>
    /// The engine skips a missing search path quietly, and so did every host
    /// here, which left a compile whose materials never loaded looking like
    /// one whose materials say "solid": water, glass and invisible-tool
    /// brushes all came out as plain walls with nothing on the console to
    /// say why. A <c>|appid_N|</c> line is the usual culprit, and its
    /// spelling alone cannot say which install it resolved to, so the
    /// location is printed beside it.
    /// </remarks>
    /// <param name="mounted">The mount to report on.</param>
    /// <param name="tool">The prefix for each line (<c>ssmap vbsp</c>, ...).</param>
    /// <param name="output">Where the lines go.</param>
    public static async Task WriteSkippedAsync(GameContentMounter.Result mounted, string tool, TextWriter output)
    {
        for (int i = 0; i < mounted.Skipped.Count; i++)
        {
            string where = i < mounted.SkippedPaths.Count ? HostPaths.Display(mounted.SkippedPaths[i]) : "(unknown)";
            await output.WriteLineAsync(
                $"{tool}: warning: search path \"{mounted.Skipped[i]}\" mounted nothing (looked in {where})")
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Mounts a game directory's <c>gameinfo.txt</c> search paths, read-only.
    /// </summary>
    /// <param name="fileSystem">The disk.</param>
    /// <param name="gameDirectory">The directory holding <c>gameinfo.txt</c>.</param>
    /// <param name="steam">Resolves <c>|appid_N|</c> search paths, or null.</param>
    /// <param name="cancellationToken">Cancels the mount.</param>
    /// <returns>The mounted content.</returns>
    internal static async Task<GameContentMounter.Result> MountGameAsync(
        IFileSystem fileSystem, string gameDirectory, ISteamAppLocator? steam, CancellationToken cancellationToken)
    {
        ReadOnlyFileSystem content = new(fileSystem);
        VPath gameInfoPath = VPath.Create(Path.Combine(gameDirectory, "gameinfo.txt"));
        GameInfo gameInfo = await GameInfo.LoadAsync(content, gameInfoPath, cancellationToken).ConfigureAwait(false);
        return await GameContentMounter.MountAsync(
            content,
            gameInfo,
            new GameContentRoots(gameInfoPath.Directory, VPath.Create(Path.GetDirectoryName(gameDirectory)!))
            {
                Steam = steam,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // A name with no extension tries .vmm, then .vmf.
    internal static async Task<string?> ResolveMapFileAsync(
        IFileSystem fileSystem, MapPaths paths, CancellationToken cancellationToken)
    {
        List<string> candidates = Path.HasExtension(paths.Name)
            ? [paths.Name]
            : [paths.Name + ".vmm", paths.Name + ".vmf"];

        foreach (string candidate in candidates)
        {
            if (VPath.TryCreate(candidate, out VPath path)
                && await fileSystem.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task WriteDiagnosticsAsync(IReadOnlyList<CompileDiagnostic> diagnostics, TextWriter output)
    {
        foreach (CompileDiagnostic d in diagnostics)
        {
            await output.WriteLineAsync($"{d.Severity} {d.Code}: {d.Message}").ConfigureAwait(false);
        }
    }

    private static async Task WriteBspAsync(
        IFileSystem fileSystem, VPath path, BspData bsp, FormatOptions format, TextWriter output,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync($"Writing {HostPaths.Display(path)}").ConfigureAwait(false);
        // Sized once for the whole file (BspFile.SizeBound), and handed on
        // without a copy, rather than doubled up to it and copied out.
        long bound = BspFile.SizeBound(bsp);
        using MemoryStream buffer = new(bound <= Array.MaxLength ? (int)bound : 0);
        // The T3 seam: a resolved format beyond today's default is handed the
        // writer's format overload; the default asks for null and so runs
        // literally the legacy call T1 left here (corpus identity).
        BspWriteFormat? writeFormat = BspFormatWriter.ToWriteFormat(format);
        if (writeFormat is { } explicitFormat)
        {
            await BspFile.SaveAsync(bsp, buffer, BspWriteMode.Canonical, explicitFormat, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await BspFile.SaveAsync(bsp, buffer, BspWriteMode.Canonical, cancellationToken)
                .ConfigureAwait(false);
        }
        await WriteBytesAsync(fileSystem, path, buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(
        IFileSystem fileSystem, VPath path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await fileSystem.ReplaceAsync(
            path,
            async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteIfPresentAsync(IFileSystem fileSystem, VPath path, CancellationToken cancellationToken)
    {
        if (await fileSystem.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
        {
            await fileSystem.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }
}
