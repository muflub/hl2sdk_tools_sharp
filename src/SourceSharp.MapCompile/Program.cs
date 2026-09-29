//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap</c>: a thin client of the map-compiler libraries.
/// </summary>
/// <remarks>
/// <para>
/// Everything this program can do, a host can do, and that is enforced rather
/// than intended: SourceSharp.MapCompile references the libraries as an
/// ordinary consumer with no <c>InternalsVisibleTo</c>, so a command that needs
/// something internal does not compile.
/// </para>
/// <para>
/// What lives here and nowhere else: stock-spelling argument parsing, rendering
/// the pacifier and the cache report, exit codes, and wiring Ctrl-C to the
/// cancellation token. The libraries have no <c>Console</c>, read no
/// environment variables, and never call <c>Environment.Exit</c>.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>The exit code for a run that did what it was asked.</summary>
    public const int ExitSuccess = 0;

    /// <summary>The exit code for a command line this program cannot act on.</summary>
    public const int ExitUsage = 2;

    /// <summary>
    /// The exit code for a compile that failed for a reason the program
    /// understood: a <see cref="MapCompileException"/> or running out of
    /// memory.
    /// </summary>
    public const int ExitFailure = 1;

    /// <summary>
    /// The exit code for an exception nothing expected: <c>EX_SOFTWARE</c>
    /// from <c>sysexits.h</c>, an internal error in this program.
    /// </summary>
    public const int ExitSoftware = 70;

    /// <summary>
    /// Runs one <c>ssmap</c> invocation.
    /// </summary>
    /// <param name="args">The command line, without the program name.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // Take the first Ctrl-C as "wind down": the compile observes the
            // token, releases its arenas and leaves no half-written .bsp. A
            // second one gets the runtime's default behaviour, because a user
            // pressing it twice means it.
            e.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += onCancel;
        try
        {
            return await RunAsync(args, Console.Out, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("ssmap: cancelled").ConfigureAwait(false);
            return ExitUsage;
        }
        catch (Exception exception)
        {
            // Never an unhandled exception: that is a core dump and a runtime
            // banner in place of an answer.
            return await ReportFailureAsync(
                exception, args.Length > 0 ? args[0] : null, Console.Error).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// Dispatches one command, with the output sink injected.
    /// </summary>
    /// <param name="args">The command line, without the program name.</param>
    /// <param name="output">Where ordinary output goes.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// Separate from <see cref="Main"/>, and taking its writer as a parameter,
    /// so the CLI's own logic is reachable from a fact without a process or a
    /// console (the CLI has little logic by construction,
    /// and what it has is tested).
    /// </remarks>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        cancellationToken.ThrowIfCancellationRequested();

        if (args.Length == 0)
        {
            await WriteUsageAsync(output).ConfigureAwait(false);
            return ExitUsage;
        }

        switch (args[0])
        {
            case "-h" or "--help" or "help":
                await WriteUsageAsync(output).ConfigureAwait(false);
                return ExitSuccess;

            case "check":
            {
                // Rooted at "/" so a map can be named by any path the shell
                // accepts, absolute or relative to the working directory.
                PhysicalFileSystem disk = new("/");
                string[] maps = [.. args[1..].Select(a => Path.GetFullPath(a))];

                return await CheckCommand.RunAsync(disk, maps, output, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "vbsp":
            {
                // The collision cooker is host knowledge, like `phys`: which
                // library, where the games are, and the relaunch it needs.
                PhysicalFileSystem disk = new("/");
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                IReadOnlyList<VPath> roots = [.. DefaultSteamRoots(home).Select(VPath.Create)];

                return await VbspHost.RunAsync(disk, roots, args[1..], output, cancellationToken).ConfigureAwait(false);
            }

            case "vvis":
            {
                // Rooted at "/", like `check`: a map is named by whatever path
                // the shell accepts, and the .prt beside it is found from that.
                PhysicalFileSystem disk = new("/");

                return await VvisCommand.RunAsync(disk, args[1..], output, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "vrad":
            {
                // The game mount resolves |appid_N| search paths through the
                // Steam library, as vbsp's does.
                PhysicalFileSystem disk = new("/");
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                IReadOnlyList<VPath> roots = [.. DefaultSteamRoots(home).Select(VPath.Create)];

                return await VradCommand.RunAsync(disk, args[1..], VbspHost.SteamFor(disk, roots), output, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "diff":
                return await DiffCommand.RunAsync(new PhysicalFileSystem("/"), args[1..], output, cancellationToken)
                    .ConfigureAwait(false);

            case "all":
            {
                // vbsp's host knowledge (the cooker, the Steam library) plus
                // vvis and vrad, one process.
                PhysicalFileSystem disk = new("/");
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                IReadOnlyList<VPath> roots = [.. DefaultSteamRoots(home).Select(VPath.Create)];

                return await AllCommand.RunAsync(disk, roots, args[1..], output, cancellationToken).ConfigureAwait(false);
            }

            case "room":
            {
                // vbsp's host half, in miniature: the game mount, the cooker,
                // the Steam roots — and a room pack out the other end
                // instead of a .bsp.
                PhysicalFileSystem disk = new("/");
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                IReadOnlyList<VPath> roots = [.. DefaultSteamRoots(home).Select(VPath.Create)];

                return await RoomCommands.RunRoomAsync(disk, roots, args[1..], output, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "link":
            {
                // The link needs no game: the room objects carry their own
                // compiled bytes and their own vis, which is the point of
                // compiling them separately.
                PhysicalFileSystem disk = new("/");

                return await RoomCommands.RunLinkAsync(disk, args[1..], output, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "rooms":
            {
                // A listing of a library: reads the VMF, mounts no game.
                PhysicalFileSystem disk = new("/");

                return await RoomCommands.RunRoomsAsync(disk, args[1..], output, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "nav":
            {
                // Inspection: reads a .nav3d, or a level and its room pack.
                PhysicalFileSystem disk = new("/");

                return await NavCommand.RunAsync(disk, args[1..], output, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "layout":
            {
                // A seeded level needs only the library's socket sets, which
                // the library VMF's geometry gives without a game.
                PhysicalFileSystem disk = new("/");

                return await RoomCommands.RunLayoutAsync(disk, args[1..], output, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "phys":
            {
                // The exe is where host knowledge lives: which filesystem to
                // use, and where this machine keeps its games. The library
                // takes both as parameters and reads no environment variable,
                // which is what lets the same discovery run against a fixture.
                // Rooted at "/" because the game installs are wherever Steam
                // put them, which is not under any one tree this tool owns. A
                // VPath drops the leading slash, so an absolute path and this
                // root compose back to the same file.
                PhysicalFileSystem disk = new("/");
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                IReadOnlyList<VPath> roots = [.. DefaultSteamRoots(home).Select(VPath.Create)];

                return await PhysCommand.RunAsync(
                    disk, roots, args[1..], output, disk.Root, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "compliance" or "-listcompliance":
                return await ListComplianceAsync(args[1..], output).ConfigureAwait(false);

            case "presets" or "--print-presets":
                return PrintPresets(output);

            case "cache":
                return await CacheCommand.RunAsync(args[1..], output, cancellationToken).ConfigureAwait(false);

            case "bench":
            {
                // Phase 12's instrument: the corpus at a thread count, timed runs
                // in this process, plus the one-process child mode the AOT rows need.
                PhysicalFileSystem disk = new("/");
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                IReadOnlyList<VPath> roots = [.. DefaultSteamRoots(home).Select(VPath.Create)];

                return await BenchCommand.RunAsync(disk, roots, args[1..], output, cancellationToken)
                    .ConfigureAwait(false);
            }

            default:
                await output.WriteLineAsync($"ssmap: unknown command \"{args[0]}\"")
                    .ConfigureAwait(false);
                await WriteUsageAsync(output).ConfigureAwait(false);
                return ExitUsage;
        }
    }

    /// <summary>
    /// Where a Source install keeps its games, for physics-library discovery.
    /// </summary>
    /// <param name="home">The user's home directory.</param>
    /// <returns>Search roots, most likely first.</returns>
    /// <remarks>
    /// Host knowledge, so it lives in the exe. The library takes its roots as a
    /// parameter and reads no environment variable, which is what lets the same
    /// discovery run against a fixture in a fact.
    /// </remarks>
    public static IReadOnlyList<string> DefaultSteamRoots(string home)
    {
        ArgumentNullException.ThrowIfNull(home);

        return
        [
            Path.Combine(home, ".steam", "steam", "steamapps", "common"),
            Path.Combine(home, ".local", "share", "Steam", "steamapps", "common"),
        ];
    }

    /// <summary>
    /// Turns an exception that reached the top of the program into a message
    /// and an exit code.
    /// </summary>
    /// <param name="exception">What escaped.</param>
    /// <param name="stage">The command that was running, or null.</param>
    /// <param name="error">Where the message goes: standard error.</param>
    /// <returns>
    /// <see cref="ExitFailure"/> for a <see cref="MapCompileException"/> or an
    /// <see cref="OutOfMemoryException"/>, otherwise <see cref="ExitSoftware"/>.
    /// </returns>
    /// <remarks>
    /// A <see cref="MapCompileException"/> prints its stable
    /// <see cref="MapCompileException.Code"/> before the message when the
    /// thrower gave one, so a script can key on it; nothing more is printed
    /// for it. Anything unexpected gets its type and
    /// stack as well, because that is the bug report.
    /// </remarks>
    public static async Task<int> ReportFailureAsync(Exception exception, string? stage, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(error);

        string where = stage is null ? string.Empty : $" in {stage}";

        switch (exception)
        {
            case MapCompileException compile:
                string code = compile.Code is null ? string.Empty : $"{compile.Code}: ";
                await error.WriteLineAsync($"ssmap: error{where}: {code}{compile.Message}").ConfigureAwait(false);
                return ExitFailure;

            case OutOfMemoryException:
                await error.WriteLineAsync($"ssmap: out of memory{where}").ConfigureAwait(false);
                return ExitFailure;

            default:
                await error.WriteLineAsync(
                    $"ssmap: internal error{where}: {exception.GetType().FullName}: {exception.Message}")
                    .ConfigureAwait(false);
                await error.WriteLineAsync(exception.StackTrace ?? "(no stack trace)").ConfigureAwait(false);
                return ExitSoftware;
        }
    }

    /// <summary><c>ssmap compliance [vbsp|vvis|vrad]</c>: the quirk catalogue.</summary>
    private static async Task<int> ListComplianceAsync(string[] args, TextWriter output)
    {
        CompileTools tools = CompileTools.None;
        foreach (string arg in args)
        {
            CompileTools one = arg.ToLowerInvariant() switch
            {
                "vbsp" => CompileTools.Vbsp,
                "vvis" => CompileTools.Vvis,
                "vrad" => CompileTools.Vrad,
                _ => CompileTools.None,
            };

            if (one == CompileTools.None)
            {
                await output.WriteLineAsync($"ssmap compliance: unknown tool \"{arg}\"").ConfigureAwait(false);
                return ExitUsage;
            }

            tools |= one;
        }

        if (tools == CompileTools.None)
        {
            tools = CompileTools.Vbsp | CompileTools.Vvis | CompileTools.Vrad;
        }

        await output.WriteAsync(ComplianceCatalogue.Format(tools)).ConfigureAwait(false);
        return ExitSuccess;
    }

    /// <summary>
    /// <c>ssmap presets</c>: the format-preset table and the appid
    /// auto-detect table, presented as the product's own discoverable
    /// surface (the surface-parity audit asked for
    /// a discoverable one).
    /// </summary>
    private static int PrintPresets(TextWriter output)
    {
        output.WriteLine("format presets (apply in token order, last writer wins per field):");
        foreach (MapFormatPreset preset in MapFormatPreset.All)
        {
            output.WriteLine(
                $"  -{preset.Name,-12} bsp={(preset.BspVersion?.ToString() ?? "-"),-2} "
                + $"light={(preset.WorldLightVersion?.ToString() ?? "-"),-2} "
                + $"staticprops={(preset.StaticPropsToken ?? "-"),-7} "
                + $"matsys={(preset.MatsysCompat is true ? "on" : "-"),-2} "
                + $"ladders={(preset.SimpleLadders is true ? "on" : "-"),-2} "
                + $"nodisp4vm={(preset.NoDisp4VirtualMesh is true ? "on" : "-"),-2} "
                + $"noinelig={(preset.NoIneligibleVertexLitProps is true ? "on" : "-"),-2} "
                + $"csgoclip={(preset.CsgoClipContents is true ? "on" : "-"),-2} "
                + $"dispinfolimit={(preset.DispInfoLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"),-5} "
                + $"l4d2layout={(preset.L4d2LumpDirLayout is true ? "on" : "-")}");
        }

        output.WriteLine();
        output.WriteLine("auto-detected by SteamAppId (mounted gameinfo):");
        foreach ((int appid, MapFormatPreset preset) in MapFormatPreset.AutoDetectTable)
        {
            output.WriteLine($"  appid {appid,-8} -> {preset.Name}");
        }

        output.WriteLine("  (anything else: no preset, the default behaviour)");
        return ExitSuccess;
    }

    private static async Task WriteUsageAsync(TextWriter output) =>
        await output.WriteLineAsync(
            """
            usage: ssmap <command> [options]

              vbsp|vvis|vrad <stock-compatible args>   one stage, stock spelling
                                                       each also: --bench (stage times);
                                                       vvis also: -fastflow[=N] (a faster
                                                       portal flow whose PVS is approximate:
                                                       each walk stops early after N exact
                                                       steps, 1000 by default, 0 everywhere;
                                                       larger N is slower and loses less;
                                                       it warns, and is off by default);
                                                       vrad also: --no-game-content (light even when
                                                       -game cannot be mounted; the default
                                                       fails, as stock does);
                                                       -gpu <match|auto> traces on the Vulkan
                                                       device whose name contains <match>
                                                       (always, if it passes its self-test);
                                                       -gpu auto tries each capable device in
                                                       turn (discrete, integrated, virtual; within
                                                       a type more VRAM, then more shader cores,
                                                       first), skipping one that is
                                                       a CPU implementation (llvmpipe), fails
                                                       its self-test or uploads rays below
                                                       2.5 GB/s; if none is left it keeps the
                                                       CPU tracer and says why for each
              all [chain options] <map> [--vbsp ...] [--vvis ...] [--vrad ...]
                                                       vbsp+vvis+vrad, one process, BSP in memory;
                                                       each --stage section takes that stage's
                                                       stock options (--vvis also -fastflow[=N]);
                                                       chain options: -game -threads
                                                       -compliance -v -fast -tighten -loose -cooker -vphysics
                                                       -listcompliance -nocache -incremental
                                                       -cache-dir <dir> -gpu <match|auto>
                                                       -gpu_slabs <n> -gpu_depth <n> -overlap
                                                       --no-write
                                                       --record-content <zip>: also write every
                                                       game file the compile read, as a game
                                                       directory with its own gameinfo.txt
              room <library.vmf> [-out <pack.roompack>] [-nav-turn0] [-nav-codec <c>]
                   [-incremental [-cache-dir <dir>] | -nocache] [vbsp options]
                                                      every room of a library VMF (one
                                                      info_room each), -threads at once
                                                      -> one <library>.roompack, with each
                                                      room's 3D navigation and entity counts;
                                                      -incremental reuses unchanged rooms from
                                                      <library>.sscache.db (the same pack)
              link <level.yaml> [-rooms <pack.roompack>] [-entity-reserve <n>] [-out <map.bsp>]
                   [-no-nav | -require-nav] [-nav-codec <c>] [-mod-entities] [-nofold] [-nodoorvis]
                                                      the level's rooms -> one linked map
                                                      and its <map>.nav3d beside it;
                                                      reports its edicts against 2048 less
                                                      the reserve (512, or the library's);
                                                      joints are the sockets that face;
                                                      cxry_ names resolved to their cells,
                                                      -mod-entities writes logic_room;
                                                      touching box brushes fold into one
                                                      (-nofold keeps them apart);
                                                      visibility through the doorways
                                                      (-nodoorvis: every cluster sees all)
              link <level.yaml> --flatten [-mod-entities] [-out <map.vmf>]
                                                      the same level as one VMF, for vbsp
              rooms <library.vmf> [-rooms <pack.roompack>]
                                                      list a library's rooms: name, cell,
                                                      each door's box and size, and with
                                                      its pack each room's entities and names
              rooms -rooms <pack.roompack>             a pack's section table: tag, offset,
                                                      length, codec, revision, hash
              layout <library.vmf> -rows R -columns C -seed N [-empty <ratio>]
                     [-rooms <pack.roompack>] [-entity-budget <n>] [-mod-entities] [-out <level.yaml>]
                                                      a seeded level of the library's rooms,
                                                      within the entity budget when the
                                                      pack has the rooms' counts
              nav <map.nav3d | level.yaml> [-rooms <pack>] [--obj <out.obj>] [--floor] [--agent N]
                                                      a level navigation's cells, free volume,
                                                      components and door links; OBJ export
              check | diff | bench                     the acceptance instruments
              cache stats|explain|gc|clear|check       the incremental-compile cache
              phys list | phys select <game>           which vphysics library to cook with
              phys cook <game>                         load one and cook a test cube
              compliance [vbsp|vvis|vrad]              the stock quirks -compliance switches

            Two builds of vphysics cook the same shape to different bytes, so the
            physics lump depends on which game's library was used. `phys list` shows
            what this machine has; `vbsp -vphysics <game>` chooses one for a compile
            (default: source-sdk-base-2013-multiplayer), `vbsp -cooker none` writes none.
            """).ConfigureAwait(false);
}
