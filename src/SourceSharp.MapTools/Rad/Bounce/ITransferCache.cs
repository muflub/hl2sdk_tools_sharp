//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// The bounce's transfer cache seam: the transfer build reads geometry, vis
/// and the shadow casters but no light, so a recompile that only moved or
/// retuned lights can replay the previous compile's transfers instead of
/// tracing them again.
/// </summary>
/// <remarks>
/// The key (<see cref="Light.RadWorld"/> computes it) covers everything the
/// build reads; the implementation owns storage, self-checks and the report.
/// A replay must be the transfers a fresh build gives, entry for entry.
/// </remarks>
public interface ITransferCache
{
    /// <summary>Looks up the transfers for a key; null on miss.</summary>
    /// <param name="key">The key digest.</param>
    /// <param name="patchCount">The patch count the caller expects (a self-check).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<TransferSet?> TryGetAsync(string key, int patchCount, CancellationToken cancellationToken);

    /// <summary>Offers freshly built transfers for storage; may drop them.</summary>
    /// <param name="key">The key digest.</param>
    /// <param name="transfers">The transfers.</param>
    /// <param name="costMs">How long the build took, for a later hit's saved estimate.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask StoreAsync(string key, TransferSet transfers, long costMs, CancellationToken cancellationToken);
}
