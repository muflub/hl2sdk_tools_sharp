//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The end-of-run cache report (the cache rules: printed at
/// the end of every run — what was reused and why the rest was not, bytes,
/// estimated time saved net of cache overhead, GC summary).
/// </summary>
public static class CacheRunReport
{
    /// <summary>Renders one run's counters as the report block.</summary>
    /// <param name="counters">The run's counters; null renders the no-cache line.</param>
    /// <param name="stats">
    /// The store's size/GC figures, read by the caller while it is on an
    /// async path — rendering never blocks (the sync-over-async gate).
    /// </param>
    /// <returns>The report text (no trailing newline).</returns>
    public static string Render(CacheRunCounters? counters, CacheStats? stats = null)
    {
        if (counters is null)
        {
            return "cache: off";
        }

        System.Text.StringBuilder text = new();
        text.Append($"cache: {counters.Hits} model(s) reused, {counters.Misses} cooked");
        text.Append($", reused {FormatBytes(counters.BytesReused)}");
        text.Append($", stored {FormatBytes(counters.BytesStored)}");
        text.Append($", est. saved {counters.EstimatedSavedMs} ms (net of cache overhead on the stage timings)");

        if (counters.StageHits.Count != 0)
        {
            text.Append($"; stages reused: {string.Join(", ", counters.StageHits)}");
        }

        if (counters.StageMisses.Count != 0)
        {
            text.Append($"; stages computed: {string.Join(", ", counters.StageMisses)}");
        }

        if (counters.CorruptRows != 0)
        {
            text.Append($"; {counters.CorruptRows} row(s) rejected as corrupt (treated as misses)");
        }

        if (counters.ReadSkips != 0 || counters.WriteSkips != 0)
        {
            text.Append($"; posture skipped {counters.ReadSkips} read(s), {counters.WriteSkips} write(s)");
        }

        if (counters.MissesByModel.Count is > 0 and <= 16)
        {
            string which = string.Join(
                ", ",
                counters.MissesByModel.Select(static m => string.Create(
                    CultureInfo.InvariantCulture,
                    $"model {m.Key}")));
            text.Append($"; re-cooked: {which}");
        }

        if (stats is not null)
        {
            text.Append($"; store: {stats.KeyCount} row(s), {stats.BlobCount} blob(s), {FormatBytes(stats.SizeBytes)}"
                + (stats.FileBytes != stats.SizeBytes ? $" on disk {FormatBytes(stats.FileBytes)}" : string.Empty)
                + $", {stats.GenerationCount} generation(s), {stats.ToolIdCount} tool(s)");
        }

        return text.ToString();
    }

    /// <summary>KB/MB with one decimal, invariant.</summary>
    internal static string FormatBytes(long bytes) =>
        bytes >= 1L << 20
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB");
}
