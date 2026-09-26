//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Validation;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap check</c>: instrument I1 — does the engine's loader accept this map?
/// </summary>
/// <remarks>
/// A thin renderer over <see cref="BspValidator"/>. The rules and their
/// diagnostic codes live in the library, so a host gets exactly what this
/// prints, as data.
/// </remarks>
public static class CheckCommand
{
    /// <summary>The exit code when a map breaks a rule the engine enforces.</summary>
    public const int ExitInvalid = 1;

    /// <summary>
    /// Validates one or more maps and prints what the engine would object to.
    /// </summary>
    /// <param name="fileSystem">Where the maps are read from.</param>
    /// <param name="args">The arguments after <c>check</c>: one or more maps.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>
    /// <see cref="Program.ExitSuccess"/> when every map is clean,
    /// <see cref="ExitInvalid"/> when any rule fired, and
    /// <see cref="Program.ExitUsage"/> when the command line made no sense.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (args.Count == 0)
        {
            await output.WriteLineAsync("usage: ssmap check <map.bsp> [<map.bsp> ...]")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        int worst = Program.ExitSuccess;

        foreach (string arg in args)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!VPath.TryCreate(arg, out VPath path))
            {
                await output.WriteLineAsync($"ssmap check: \"{arg}\" is not a usable path")
                    .ConfigureAwait(false);
                worst = Program.ExitUsage;
                continue;
            }

            if (!await fileSystem.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync($"ssmap check: no such file: {arg}").ConfigureAwait(false);
                worst = Program.ExitUsage;
                continue;
            }

            // CheckFileAsync rather than CheckAsync: the ident and version gate
            // is the FIRST thing the loader does, and
            // a BspData cannot represent a file that failed it -- the loader
            // throws instead. Checking from the stream is the only way that
            // rule is reachable at all.
            ValidationReport report;
            await using (Stream stream =
                await fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(false))
            {
                report = await BspValidator.CheckFileAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
            }

            await WriteReportAsync(arg, report, output).ConfigureAwait(false);

            if (!report.IsClean)
            {
                worst = ExitInvalid;
            }
        }

        return worst;
    }

    /// <summary>Renders one map's findings.</summary>
    /// <param name="name">What to call the map in the output.</param>
    /// <param name="report">What the validator found.</param>
    /// <param name="output">Where the report goes.</param>
    /// <returns>A task that completes when the report has been written.</returns>
    public static async Task WriteReportAsync(
        string name,
        ValidationReport report,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        if (report.IsClean)
        {
            await output.WriteLineAsync($"{name}: clean").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(
            $"{name}: {report.ErrorCount} error(s), {report.WarningCount} warning(s)")
            .ConfigureAwait(false);

        foreach (CompileDiagnostic diagnostic in report.Diagnostics)
        {
            // The CODE first, because it is the stable part: a host keys on it
            // and the message text is free to be reworded.
            string severity = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info",
            };

            await output.WriteLineAsync($"  {diagnostic.Code} {severity}: {diagnostic.Message}")
                .ConfigureAwait(false);
        }
    }
}
