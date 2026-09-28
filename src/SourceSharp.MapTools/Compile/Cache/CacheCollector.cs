//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>What one <see cref="CacheCollector.CollectAsync"/> did.</summary>
/// <param name="Skipped">Why nothing was collected, or null when the GC ran.</param>
/// <param name="RowsDropped">Rows deleted (for age and for size together).</param>
/// <param name="BlobsDropped">Blobs deleted: those only dropped rows named, and those no row named.</param>
/// <param name="BytesFreed">The dropped blobs' bytes.</param>
/// <param name="GenerationsDropped">Generation records older than the kept ones, removed.</param>
/// <param name="BudgetSpent">True when the GC stopped because <see cref="CachePolicy.GcBudget"/> ran out.</param>
/// <param name="Vacuumed">True when the store was asked to return space to the OS.</param>
public sealed record CacheGcResult(
    string? Skipped,
    int RowsDropped,
    int BlobsDropped,
    long BytesFreed,
    int GenerationsDropped,
    bool BudgetSpent,
    bool Vacuumed)
{
    /// <summary>True when the GC deleted anything.</summary>
    public bool Reclaimed => RowsDropped != 0 || BlobsDropped != 0;

    internal static CacheGcResult Skip(string reason) => new(reason, 0, 0, 0, 0, false, false);
}

/// <summary>
/// The store's garbage collector: what keeps a cache that a long-lived
/// service writes on every compile from growing without bound.
/// </summary>
/// <remarks>
/// <para>
/// <b>When.</b> After a compile's final commit, when
/// <see cref="CachePolicy.GcOnCommit"/> is on and the posture writes. Its
/// deletions are staged and published in a commit of their own.
/// </para>
/// <para>
/// <b>What it drops</b>, in this order:
/// </para>
/// <list type="number">
/// <item>rows older than <see cref="CachePolicy.MaxAgeDays"/>;</item>
/// <item>while the store's blob bytes are above <see cref="CachePolicy.MaxStoreBytes"/>,
/// the oldest rows, until it is at 90 % of the cap (ties by key, so the order
/// is the same on every run);</item>
/// <item>the blobs only the dropped rows named, and the blobs no row names at
/// all (left by an interrupted run or an older build).</item>
/// </list>
/// <para>
/// A row's age is its <see cref="CacheRecord.CreatedAtMs"/>, which the seams
/// renew on every hit, so "oldest" is least recently used. Rows at or after
/// the oldest of the newest <see cref="CachePolicy.GenerationsKept"/>
/// generations are never dropped, and the generation records older than
/// those are removed. A blob is never deleted while a remaining row names it.
/// </para>
/// <para>
/// <b>Concurrent compiles.</b> A compile in flight may be about to read a row,
/// or to commit a row naming a blob it found already stored and so did not
/// stage again. Deleting under it would cost it that row (a miss, or a row
/// that reads as corrupt next time), never a wrong hit, but the rule here is
/// stricter: the GC runs only when the collecting compile is the only one in
/// flight on the store object (<see cref="ICacheStore.RunsInFlight"/>), and
/// is otherwise deferred to a later commit. A compile that starts while the
/// GC runs is the one window left; what it can meet is a miss.
/// </para>
/// <para>
/// <b>Budget.</b> The GC checks <see cref="CachePolicy.GcBudget"/> between
/// rows. When it runs out, the GC publishes what it has dropped so far and
/// stops; the next commit's GC continues from the then-oldest rows.
/// </para>
/// </remarks>
public static class CacheCollector
{
    /// <summary>The share of <see cref="CachePolicy.MaxStoreBytes"/> a size trim stops at.</summary>
    public const double TrimTarget = 0.9;

    /// <summary>How many row-less blobs one GC deletes at most.</summary>
    public const int OrphanSweepLimit = 4096;

    private const long MsPerDay = 24L * 60 * 60 * 1000;

    /// <summary>Collects the store under the policy.</summary>
    /// <param name="store">The store; its staged changes should already be committed.</param>
    /// <param name="policy">The knobs.</param>
    /// <param name="time">The clock for the age cut and the budget.</param>
    /// <param name="ownRuns">
    /// How many of the store's <see cref="ICacheStore.RunsInFlight"/> leases
    /// the caller holds itself: 1 for a compile collecting after its commit,
    /// 0 for a host collecting outside any compile.
    /// </param>
    /// <param name="cancellationToken">Cancels the collection; nothing staged is committed then.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async ValueTask<CacheGcResult> CollectAsync(
        ICacheStore store,
        CachePolicy policy,
        TimeProvider time,
        int ownRuns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(time);

        if (!policy.GcOnCommit)
        {
            return CacheGcResult.Skip("off");
        }

        if (!policy.Writes)
        {
            return CacheGcResult.Skip("the posture does not write");
        }

        if (!store.IsUsable)
        {
            return CacheGcResult.Skip("the store is unusable");
        }

        if (store.RunsInFlight > ownRuns)
        {
            return CacheGcResult.Skip("another compile is using the store");
        }

        long started = time.GetTimestamp();
        bool Spent() => policy.GcBudget <= TimeSpan.Zero || time.GetElapsedTime(started) >= policy.GcBudget;
        bool budgetSpent = false;

        // Every committed row, oldest first.
        List<CacheRecord> rows = [];
        foreach (string key in await store.KeysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await store.LookupAsync(key, cancellationToken).ConfigureAwait(false) is { } row)
            {
                rows.Add(row);
            }
        }

        rows.Sort(static (a, b) =>
        {
            int byAge = a.CreatedAtMs.CompareTo(b.CreatedAtMs);
            return byAge != 0 ? byAge : string.CompareOrdinal(a.Key, b.Key);
        });

        // The kept generations: rows at or after the oldest of them stay.
        List<(string Id, long Stamp)> generations = [];
        foreach (string id in await store.GenerationsAsync(cancellationToken).ConfigureAwait(false))
        {
            generations.Add((id, long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long stamp) ? stamp : long.MinValue));
        }

        generations.Sort(static (a, b) => b.Stamp.CompareTo(a.Stamp));
        int kept = Math.Max(policy.GenerationsKept, 0);
        long protectFrom = kept > 0 && generations.Count > 0
            ? generations[Math.Min(kept, generations.Count) - 1].Stamp
            : long.MaxValue;
        List<string> oldGenerations = [.. generations.Skip(kept).Select(static g => g.Id)];

        Dictionary<string, int> references = new(StringComparer.Ordinal);
        foreach (CacheRecord row in rows)
        {
            foreach (string blob in row.Blobs.Values.Distinct(StringComparer.Ordinal))
            {
                references[blob] = references.GetValueOrDefault(blob) + 1;
            }
        }

        List<CacheRecord> dropped = [];
        List<string> freedBlobs = [];
        long freedBytes = 0;
        async ValueTask DropAsync(CacheRecord row)
        {
            dropped.Add(row);
            foreach (string blob in row.Blobs.Values.Distinct(StringComparer.Ordinal))
            {
                if (--references[blob] == 0)
                {
                    freedBlobs.Add(blob);
                    freedBytes += await store.BlobSizeAsync(blob, cancellationToken).ConfigureAwait(false) ?? 0;
                }
            }
        }

        // 1. Age.
        int next = 0;
        if (policy.MaxAgeDays > 0)
        {
            long cutoff = time.GetUtcNow().ToUnixTimeMilliseconds() - (policy.MaxAgeDays * MsPerDay);
            for (; next < rows.Count && rows[next].CreatedAtMs < cutoff && rows[next].CreatedAtMs < protectFrom; next++)
            {
                if (Spent())
                {
                    budgetSpent = true;
                    break;
                }

                await DropAsync(rows[next]).ConfigureAwait(false);
            }
        }

        // 2. Blobs no row names: free space the size trim can count.
        IReadOnlyList<string> orphans = budgetSpent
            ? []
            : await store.CollectGarbageAsync(OrphanSweepLimit, cancellationToken).ConfigureAwait(false);
        foreach (string orphan in orphans)
        {
            freedBytes += await store.BlobSizeAsync(orphan, cancellationToken).ConfigureAwait(false) ?? 0;
        }

        // 3. Size.
        if (!budgetSpent && policy.MaxStoreBytes > 0)
        {
            CacheStats stats = await store.ReadStatsAsync(cancellationToken).ConfigureAwait(false);
            long target = (long)(policy.MaxStoreBytes * TrimTarget);
            if (stats.SizeBytes - freedBytes > policy.MaxStoreBytes)
            {
                for (; next < rows.Count && stats.SizeBytes - freedBytes > target && rows[next].CreatedAtMs < protectFrom; next++)
                {
                    if (Spent())
                    {
                        budgetSpent = true;
                        break;
                    }

                    await DropAsync(rows[next]).ConfigureAwait(false);
                }
            }
        }

        List<string> blobs = [.. freedBlobs.Concat(orphans).Distinct(StringComparer.Ordinal)];
        if (dropped.Count == 0 && blobs.Count == 0 && oldGenerations.Count == 0)
        {
            return new CacheGcResult(null, 0, 0, 0, 0, budgetSpent, false);
        }

        try
        {
            foreach (CacheRecord row in dropped)
            {
                await store.DeleteAsync(row.Key, cancellationToken).ConfigureAwait(false);
            }

            if (blobs.Count > 0)
            {
                await store.DeleteBlobsAsync(blobs, cancellationToken).ConfigureAwait(false);
            }

            foreach (string generation in oldGenerations)
            {
                await store.RemoveGenerationAsync(generation, cancellationToken).ConfigureAwait(false);
            }

            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Half a collection is none: the rows it did not get to stay, and
            // so do the blobs they name.
            store.DiscardStaged();
            throw;
        }

        bool reclaimed = dropped.Count != 0 || blobs.Count != 0;
        bool vacuum = reclaimed && policy.Vacuum != CacheVacuum.Never;
        if (vacuum)
        {
            await store.VacuumAsync(cancellationToken).ConfigureAwait(false);
        }

        return new CacheGcResult(null, dropped.Count, blobs.Count, freedBytes, oldGenerations.Count, budgetSpent, vacuum);
    }
}
