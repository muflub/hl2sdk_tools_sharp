//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Compare;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap diff [-v] &lt;a.bsp&gt; &lt;b.bsp&gt;</c>: the BSP comparison instrument,
/// <see cref="BspDiff"/> on two files.
/// </summary>
/// <remarks>
/// Measures and reports; judges nothing (<see cref="DiffOptions.ReportOnly"/>),
/// because every threshold is frozen by the phase that owns its lump and lives
/// with that phase's gate, not here. Exit codes follow <c>diff(1)</c>: 0 when
/// the maps are identical, 1 when they differ, 2 for a bad command line.
/// </remarks>
public static class DiffCommand
{
    /// <summary>The exit code for two maps that differ.</summary>
    public const int ExitDiffer = 1;

    /// <summary>Runs one comparison.</summary>
    /// <param name="fileSystem">Where the maps are read from.</param>
    /// <param name="args">The arguments after <c>diff</c>.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        bool verbose = false;
        List<string> maps = [];
        foreach (string arg in args)
        {
            if (arg is "-v" or "-verbose")
            {
                verbose = true;
            }
            else
            {
                maps.Add(arg);
            }
        }

        if (maps.Count != 2)
        {
            await output.WriteLineAsync("usage: ssmap diff [-v] <a.bsp> <b.bsp>").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        BspData[] loaded = new BspData[2];
        for (int i = 0; i < 2; i++)
        {
            if (!VPath.TryCreate(Path.GetFullPath(maps[i]), out VPath path)
                || !await fileSystem.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync($"ssmap diff: no such file: {maps[i]}").ConfigureAwait(false);
                return Program.ExitUsage;
            }

            await using Stream stream = await fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            loaded[i] = await BspFile.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        DiffReport report = await BspDiff.CompareAsync(loaded[0], loaded[1], cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(report.ToText(verbose)).ConfigureAwait(false);
        return report.Identical ? Program.ExitSuccess : ExitDiffer;
    }
}
