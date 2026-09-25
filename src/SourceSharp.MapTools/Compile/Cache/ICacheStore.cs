namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The content-addressed store behind the incremental cache
/// (plan_maptools.md 10a, ruling Q7: beside the map, in a SQLite database).
/// </summary>
/// <remarks>
/// <para>
/// The contract the cache layer relies on and that BOTH shipped stores
/// (the package-free in-memory store and the optional
/// <c>SourceSharp.MapTools.Cache.Sqlite</c> store) must satisfy:
/// </para>
/// <list type="bullet">
/// <item><b>Atomic commit.</b> <see cref="CommitAsync"/> either applies
/// every staged row or applies none. A process killed (<c>SIGKILL</c>, by
/// PID) mid-commit must leave the store openable and readable, and the next
/// compile must pass I5 (the plan's kill-mid-commit fact). The SQLite
/// implementation gets this from a transaction; the in-memory one holds
/// additions out of the live tables until the swap.</item>
/// <item><b>Self-identifying.</b> A store written by another tool identity,
/// another schema, or a truncated/corrupted file must fail
/// <see cref="OpenAsync"/> (or report itself unusable) — never serve rows
/// whose bytes came from elsewhere. A dropped cache is a miss, never a
/// wrong hit.</item>
/// <item><b>Robust to corruption.</b> Damaged bytes are reported through the
/// returned exception (or a false usability result); the cache layer treats
/// an unusable store as fully cold and continues the compile.</item>
/// </list>
/// <para>
/// All members are safe for concurrent callers; the cache layer drives one
/// compile at a time but the record/replay verification reads overlap with
/// cooking.
/// </para>
/// </remarks>
public interface ICacheStore : IAsyncDisposable
{
    /// <summary>
    /// Opens the store at <paramref name="location"/>, or creates it. A file
    /// that exists but is not a valid store of this schema and tool identity
    /// throws, or the implementation marks itself unusable
    /// (<see cref="IsUsable"/> false) so the cache degrades to cold.
    /// </summary>
    /// <param name="location">Where the store lives (a path the SQLite store creates beside the map).</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    ValueTask OpenAsync(string location, CancellationToken cancellationToken);

    /// <summary>Whether the store answered <see cref="OpenAsync"/> and may be trusted for hits.</summary>
    bool IsUsable { get; }

    /// <summary>Looks up one key; null when absent.</summary>
    /// <param name="key">The key digest.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<CacheRecord?> LookupAsync(string key, CancellationToken cancellationToken);

    /// <summary>All keys currently committed.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<string>> KeysAsync(CancellationToken cancellationToken);

    /// <summary>All keys whose dependency rows record a read of <paramref name="path"/>.</summary>
    /// <param name="path">The recorded file path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<string>> FindKeysByDepAsync(string path, CancellationToken cancellationToken);

    /// <summary>The tool identities pinned by live rows — the GC's reachability input.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<string>> LiveToolIdsAsync(CancellationToken cancellationToken);

    /// <summary>Stages a product row (its blobs must be staged separately) for the next <see cref="CommitAsync"/>.</summary>
    /// <param name="record">The product.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    ValueTask PutAsync(CacheRecord record, CancellationToken cancellationToken);

    /// <summary>Stages one blob's bytes under its content-hash key; duplicate content is stored once.</summary>
    /// <param name="blobKey">The content-hash key.</param>
    /// <param name="data">The payload.</param>
    /// <param name="toolId">The producing tool identity (per-row, not meta — plan 10a).</param>
    /// <param name="createdAtMs">Unix ms of the producing run.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    ValueTask PutBlobAsync(string blobKey, ReadOnlyMemory<byte> data, string toolId, long createdAtMs, CancellationToken cancellationToken);

    /// <summary>Whether a blob is already stored (dedup before staging).</summary>
    /// <param name="blobKey">The content-hash key.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<bool> HasBlobAsync(string blobKey, CancellationToken cancellationToken);

    /// <summary>Reads a committed blob's bytes, or null.</summary>
    /// <param name="blobKey">The content-hash key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<byte[]?> GetBlobAsync(string blobKey, CancellationToken cancellationToken);

    /// <summary>Stages deletion of one key's rows for the next <see cref="CommitAsync"/>.</summary>
    /// <param name="key">The key digest.</param>
    /// <param name="cancellationToken">Cancels the staging.</param>
    ValueTask DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Applies every staged change, atomically.</summary>
    /// <param name="cancellationToken">Cancels the commit.</param>
    ValueTask CommitAsync(CancellationToken cancellationToken);

    /// <summary>Drops every staged (uncommitted) change.</summary>
    void DiscardStaged();

    /// <summary>Drops everything: rows, blobs, generations.</summary>
    /// <param name="cancellationToken">Cancels the clear.</param>
    ValueTask ClearAsync(CancellationToken cancellationToken);

    /// <summary>The generations recorded by <see cref="RecordGenerationAsync"/>.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<string>> GenerationsAsync(CancellationToken cancellationToken);

    /// <summary>Records a generation id (the mark phase's root set, plan 10a GC).</summary>
    /// <param name="generationId">The id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RecordGenerationAsync(string generationId, CancellationToken cancellationToken);

    /// <summary>Removes a generation row.</summary>
    /// <param name="generationId">The id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RemoveGenerationAsync(string generationId, CancellationToken cancellationToken);

    /// <summary>Store-wide counters for the report and the GC.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<CacheStats> ReadStatsAsync(CancellationToken cancellationToken);

    /// <summary>Blobs reachable from no live row, oldest first, capped.</summary>
    /// <param name="maxCount">Maximum keys to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<string>> CollectGarbageAsync(int maxCount, CancellationToken cancellationToken);

    /// <summary>Stages deletion of many blobs for the next <see cref="CommitAsync"/> (the sweep).</summary>
    /// <param name="blobKeys">The blob keys.</param>
    /// <param name="cancellationToken">Cancels the staging.</param>
    ValueTask DeleteBlobsAsync(IReadOnlyList<string> blobKeys, CancellationToken cancellationToken);

    /// <summary>Returns unused storage to the OS (SQLite: <c>VACUUM</c>; in-memory: a no-op).</summary>
    /// <param name="cancellationToken">Cancels the vacuum.</param>
    ValueTask VacuumAsync(CancellationToken cancellationToken);

    /// <summary>Whether the underlying bytes pass the store's own integrity check.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<bool> CheckIntegrityAsync(CancellationToken cancellationToken);
}

/// <summary>One committed cache row: a stage product's shape, its blob keys and its dependencies.</summary>
/// <param name="Key">The key digest.</param>
/// <param name="Stage">The stage name.</param>
/// <param name="ToolId">The tool identity that produced it.</param>
/// <param name="ContextTags">The host's opaque tags at production time, in order.</param>
/// <param name="Parts">The key's named components in fold order (for explain).</param>
/// <param name="Blobs">The product's payloads: role name (<c>entries</c>, <c>text</c>, …) -> blob key.</param>
/// <param name="Dependencies">The recorded file dependencies: path, content-hash text, null for a recorded miss.</param>
/// <param name="CostMs">Measured production cost in milliseconds (the report's saved estimate).</param>
/// <param name="CreatedAtMs">Unix ms when the run that produced it started — the generation marker the GC roots on.</param>
public sealed record CacheRecord(
    string Key,
    string Stage,
    string ToolId,
    IReadOnlyList<string> ContextTags,
    IReadOnlyList<(string Name, string Value)> Parts,
    IReadOnlyDictionary<string, string> Blobs,
    IReadOnlyList<(string Path, string? ContentHash)> Dependencies,
    long CostMs,
    long CreatedAtMs);
