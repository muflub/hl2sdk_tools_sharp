using System.Globalization;

using SourceSharp.MapTools.Compile.Cache;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap cache</c>: what the incremental collision cache holds, and the
/// housekeeping around it.
/// </summary>
/// <remarks>
/// <para>
/// The verbs read the file <c>-incremental</c> writes (<c>&lt;map&gt;.sscache.db</c>
/// beside the map, ruling Q7, or one named outright), so a script can ask what
/// a compile reused without compiling. Corruption is reported out loud here:
/// the compiler degrades a corrupt store to no-cache silently (t-7i's posture),
/// which is right for a build and wrong for a human asking "why is nothing
/// hitting".
/// </para>
/// <para>
/// The store itself is opened through <see cref="HostBackends"/> so the absent-
/// package case stays a message, never a crash.
/// </para>
/// </remarks>
public static class CacheCommand
{
    /// <summary>The usage line for a cache invocation this program cannot act on.</summary>
    public const string Usage = "usage: ssmap cache stats <store-or-map> | explain <store-or-map> [<key>] | gc <store-or-map> [max] | clear <store-or-map> | check <store-or-map>";

    /// <summary>Runs <c>ssmap cache</c>.</summary>
    /// <param name="args">The arguments after <c>cache</c>.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>An <see cref="Program"/> exit code.</returns>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string verb = args.Count > 0 ? args[0] : string.Empty;
        if (args.Count < 2)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string path = ResolveStore(args[1]);

        switch (verb)
        {
            case "stats":
            {
                CacheStats? stats = await HostBackends.ReadStatsAsync(path, cancellationToken).ConfigureAwait(false);
                if (stats is null)
                {
                    return await UnavailableAsync(path, output).ConfigureAwait(false);
                }

                await output.WriteLineAsync(
                    $"cache: {stats.KeyCount} key(s), {stats.BlobCount} blob(s), "
                    + $"{stats.GenerationCount} generation(s), {stats.ToolIdCount} tool id(s); "
                    + $"{stats.FileBytes} bytes on disk").ConfigureAwait(false);
                return await IntegrityNoteAsync(path, output, cancellationToken).ConfigureAwait(false);
            }

            case "explain":
            {
                if (HostBackends.SqliteStore is null)
                {
                    return await UnavailableAsync(path, output).ConfigureAwait(false);
                }

                if (!File.Exists(path))
                {
                    await output.WriteLineAsync($"cache: no store at {path}; nothing has been cached for it").ConfigureAwait(false);
                    return Program.ExitSuccess;
                }

                if (args.Count >= 3)
                {
                    CacheRecord? record = await HostBackends.ExplainAsync(path, args[2], cancellationToken).ConfigureAwait(false);
                    if (record is null)
                    {
                        await output.WriteLineAsync($"cache: key {args[2]} not in {path}").ConfigureAwait(false);
                        return Program.ExitSuccess;
                    }

                    await output.WriteLineAsync(
                        $"cache: key {args[2]} present; stage={record.Stage}, tool={record.ToolId}, "
                        + $"deps={record.Dependencies.Count}, context={record.ContextTags.Count} tag(s), "
                        + $"blobs={record.Blobs.Count}")
                        .ConfigureAwait(false);
                    return Program.ExitSuccess;
                }

                IReadOnlyList<string>? keys = await HostBackends.ListKeysAsync(path, cancellationToken).ConfigureAwait(false);
                foreach (string key in keys ?? [])
                {
                    await output.WriteLineAsync(key).ConfigureAwait(false);
                }

                return Program.ExitSuccess;
            }

            case "gc":
            {
                int max = args.Count >= 3
                    && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1
                        ? n
                        : 1024;
                IReadOnlyList<string>? dropped = await HostBackends.GcAsync(path, max, cancellationToken).ConfigureAwait(false);
                if (dropped is null)
                {
                    return await UnavailableAsync(path, output).ConfigureAwait(false);
                }

                await output.WriteLineAsync($"cache: gc dropped {dropped.Count} unreferenced blob(s) from {path}").ConfigureAwait(false);
                return Program.ExitSuccess;
            }

            case "clear":
            {
                if (!await HostBackends.ClearAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    return await UnavailableAsync(path, output).ConfigureAwait(false);
                }

                await output.WriteLineAsync($"cache: cleared {path}").ConfigureAwait(false);
                return Program.ExitSuccess;
            }

            case "check":
            {
                bool? verdict = await HostBackends.CheckIntegrityAsync(path, cancellationToken).ConfigureAwait(false);
                if (verdict is null)
                {
                    return await UnavailableAsync(path, output).ConfigureAwait(false);
                }

                await output.WriteLineAsync(
                    verdict.Value
                        ? $"cache: {path} passes SQLite's integrity_check"
                        : $"cache: {path} is CORRUPT — compiles fall back to cooking everything; delete it or clear it")
                        .ConfigureAwait(false);
                return verdict.Value ? Program.ExitSuccess : Program.ExitFailure;
            }

            default:
                await output.WriteLineAsync(Usage).ConfigureAwait(false);
                return Program.ExitUsage;
        }
    }

    /// <summary>
    /// A store argument is either a file or the map whose beside-the-map store
    /// is meant (ruling Q7's naming).
    /// </summary>
    /// <param name="arg">What the command line named.</param>
    /// <returns>The store path to open.</returns>
    public static string ResolveStore(string arg)
    {
        if (arg.EndsWith(".sscache.db", StringComparison.OrdinalIgnoreCase))
        {
            return arg;
        }

        if (arg.EndsWith(".vmf", StringComparison.OrdinalIgnoreCase)
            || arg.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(arg))
        {
            // The map whose beside-the-map store is meant — or a bare store
            // name that doesn't exist yet: Q7's naming decides.
            string full = Path.GetFullPath(arg);
            string directory = Path.GetDirectoryName(full)!;
            string name = Path.GetFileNameWithoutExtension(full);
            return Path.Combine(directory, name + ".sscache.db");
        }

        return arg; // an existing file: the store itself
    }

    private static async Task<int> UnavailableAsync(string path, TextWriter output)
    {
        await output.WriteLineAsync(
            $"cache: no SQLite backend for {path} ({HostBackends.MissingReason ?? "the store could not be opened"})").ConfigureAwait(false);
        return Program.ExitFailure;
    }

    private static async Task<int> IntegrityNoteAsync(string path, TextWriter output, CancellationToken cancellationToken)
    {
        bool? verdict = await HostBackends.CheckIntegrityAsync(path, cancellationToken).ConfigureAwait(false);
        if (verdict == false)
        {
            await output.WriteLineAsync($"cache: {path} is CORRUPT — compiles fall back to cooking everything").ConfigureAwait(false);
            return Program.ExitFailure;
        }

        return Program.ExitSuccess;
    }
}
