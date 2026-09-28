//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The one read every cache seam replays a row's blobs through: the key must
/// look like a content digest, the blob must be there, and, under
/// <see cref="CachePolicy.VerifyOnHit"/>, its bytes must hash back to the key.
/// </summary>
/// <remarks>
/// <para>
/// A blob's key is the SHA-256 of its bytes, so a present blob can only
/// differ from what was stored through damage: a torn write, a bit flip on
/// disk, a store file edited by hand. Re-hashing on every hit is what turns
/// that damage into a miss instead of a wrong product, and it is on by
/// default. Turning it off trades that check for the hashing time on large
/// rows (the transfer set runs to hundreds of megabytes), for a host that
/// trusts its store's own integrity checking.
/// </para>
/// <para>
/// A missing blob is a miss either way: a row whose blob went away (another
/// compile's eviction, the GC, a store shared between processes) never
/// serves anything.
/// </para>
/// </remarks>
internal static class CacheBlobReader
{
    /// <summary>Reads one blob of a row being replayed.</summary>
    /// <param name="store">The store.</param>
    /// <param name="policy">The run's policy; <see cref="CachePolicy.VerifyOnHit"/> decides the re-hash.</param>
    /// <param name="blobKey">The key the row names.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes, or null when the read must count as a miss.</returns>
    public static async ValueTask<byte[]?> ReadAsync(
        ICacheStore store, CachePolicy policy, string blobKey, CancellationToken cancellationToken)
    {
        if (!CacheKey.LooksLikeDigest(blobKey))
        {
            return null;
        }

        byte[]? data = await store.GetBlobAsync(blobKey, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            return null;
        }

        return !policy.VerifyOnHit || CacheKey.HashBytes(data) == blobKey ? data : null;
    }

    /// <summary>
    /// Re-stages a row a hit just replayed under this run's stamp, so the GC
    /// sees it as used by this run: its age (<see cref="CachePolicy.MaxAgeDays"/>)
    /// and its generation (<see cref="CachePolicy.GenerationsKept"/>) are
    /// counted from its last use, not from the run that first made it.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="policy">The run's policy; a posture that does not write renews nothing.</param>
    /// <param name="record">The row replayed.</param>
    /// <param name="runStamp">This run's generation stamp.</param>
    /// <param name="cancellationToken">Cancels the staging.</param>
    /// <returns>A task that completes once the renewal is staged, or skipped.</returns>
    /// <remarks>
    /// The renewal changes nothing but the stamp, and it is only staged: it
    /// is published with the run's other rows, and dropped with them when the
    /// run fails. A store that refuses the write (opened read-only under a
    /// writing posture) costs the renewal, never the hit.
    /// </remarks>
    public static async ValueTask RenewAsync(
        ICacheStore store, CachePolicy policy, CacheRecord record, long runStamp, CancellationToken cancellationToken)
    {
        if (!policy.Writes || record.CreatedAtMs >= runStamp)
        {
            return;
        }

        try
        {
            await store.PutAsync(record with { CreatedAtMs = runStamp }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A read-only store: the row stays as old as it was.
        }
    }
}
