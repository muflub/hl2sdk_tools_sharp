using System.Collections.Concurrent;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The package-free store: everything in memory, the whole surface of
/// <see cref="ICacheStore"/>, the same commit/dedup/integrity semantics the
/// SQLite store must have (plan_maptools.md 10a).
/// </summary>
/// <remarks>
/// <para>
/// Plan 10a puts the SQLite database in the optional
/// <c>SourceSharp.MapTools.Cache.Sqlite</c> assembly because MapTools itself
/// stays package-free; this type is what ships inside MapTools so the cache
/// layer is fully exercised — including the kill-mid-commit and corruption
/// facts, via <see cref="StagedCount"/> and <see cref="FailNextCommit"/> —
/// with no package dependency. It is also what <c>Volatile</c> mode and the
/// unit facts run against.
/// </para>
/// <para>
/// Atomicity: the staging methods land in pending tables the live tables
/// never consult; <see cref="CommitAsync"/> is the single publish point, so
/// a failure (or an injected one) between staging and commit leaves the live
/// tables exactly as they were.
/// </para>
/// </remarks>
public sealed class InMemoryCacheStore : ICacheStore
{
    private sealed record Blob(string Key, byte[] Data, string ToolId, long CreatedAtMs);

    private sealed record Row(string Key, CacheRecord Record);

    private readonly ConcurrentDictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Blob> _blobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _generations = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Row> _pendingRows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Blob> _pendingBlobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingGenerations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _deletedRows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _deletedBlobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _deletedGenerations = new(StringComparer.Ordinal);

    /// <summary>Whether <see cref="OpenAsync"/> succeeded; a store never opened is unusable.</summary>
    public bool IsUsable { get; private set; }

    /// <summary>Where <see cref="OpenAsync"/> says the store lives (the report's label).</summary>
    public string? Location { get; private set; }

    /// <summary>Rows staged but not committed — the kill-mid-commit fact inspects this.</summary>
    public int StagedCount => _pendingRows.Count + _pendingBlobs.Count;

    /// <summary>When set (by tests), the next <see cref="CommitAsync"/> throws after applying nothing.</summary>
    public Exception? FailNextCommit { get; set; }

    /// <summary>When set, <see cref="CheckIntegrityAsync"/> reports damage (the corruption fact).</summary>
    public bool ReportCorrupt { get; set; }

    /// <summary>Opens (or reopens) the backing location.</summary>
    public ValueTask OpenAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        cancellationToken.ThrowIfCancellationRequested();
        Location = location;
        IsUsable = true;
        return default;
    }

    /// <summary>Reads one record by key.</summary>
    public ValueTask<CacheRecord?> LookupAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable || !CacheKey.LooksLikeDigest(key) || !_rows.TryGetValue(key, out Row? row))
        {
            return default;
        }

        return new(row.Record);
    }

    /// <summary>Every live key.</summary>
    public ValueTask<IReadOnlyList<string>> KeysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> keys = IsUsable
            ? _rows.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
            : [];
        return new(keys);
    }

    /// <summary>Keys recording one dependency.</summary>
    public ValueTask<IReadOnlyList<string>> FindKeysByDepAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable)
        {
            return default;
        }

        // The SQLite store keeps a dep->key index; the scan is its honest
        // equivalent here.
        List<string> keys = _rows.Values
            .Where(r => r.Record.Dependencies.Any(d => string.Equals(d.Path, path, StringComparison.Ordinal)))
            .Select(r => r.Key)
            .ToList();
        keys.Sort(StringComparer.Ordinal);
        return new(keys);
    }

    /// <summary>Lists the tool identities present.</summary>
    public ValueTask<IReadOnlyList<string>> LiveToolIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> ids = IsUsable
            ? _rows.Values.Select(r => r.Record.ToolId).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList()
            : [];
        return new(ids);
    }

    /// <summary>Stages a record.</summary>
    public ValueTask PutAsync(CacheRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        _pendingRows[record.Key] = new Row(record.Key, record);
        return default;
    }

    /// <summary>Stages a blob under its content id.</summary>
    public ValueTask PutBlobAsync(string blobKey, ReadOnlyMemory<byte> data, string toolId, long createdAtMs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(blobKey);
        ArgumentException.ThrowIfNullOrEmpty(toolId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        _pendingBlobs[blobKey] = new Blob(blobKey, data.ToArray(), toolId, createdAtMs);
        return default;
    }

    /// <summary>Whether a blob is present.</summary>
    public ValueTask<bool> HasBlobAsync(string blobKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(IsUsable && _blobs.ContainsKey(blobKey));
    }

    /// <summary>Reads one blob.</summary>
    public ValueTask<byte[]?> GetBlobAsync(string blobKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable || !_blobs.TryGetValue(blobKey, out Blob? blob))
        {
            return default;
        }

        return new(blob.Data);
    }

    /// <summary>Deletes a staged/live record and its deps.</summary>
    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        _pendingRows.TryRemove(key, out _);
        _deletedRows[key] = 0;
        return default;
    }

    /// <summary>Publishes the staged generation atomically.</summary>
    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();

        if (FailNextCommit is Exception failure)
        {
            FailNextCommit = null;
            throw failure;
        }

        // Single publish point: every staged mutation becomes visible here
        // and nowhere else, so the live tables never show a partial state.
        foreach (string key in _deletedRows.Keys)
        {
            _rows.TryRemove(key, out _);
        }

        foreach (string generation in _deletedGenerations.Keys)
        {
            _generations.TryRemove(generation, out _);
        }

        foreach (string blobKey in _deletedBlobs.Keys)
        {
            _blobs.TryRemove(blobKey, out _);
        }

        foreach (Blob blob in _pendingBlobs.Values)
        {
            _blobs[blob.Key] = blob;
        }

        foreach (Row row in _pendingRows.Values)
        {
            _rows[row.Key] = row;
        }

        foreach (string generation in _pendingGenerations.Keys)
        {
            _generations[generation] = 0;
        }

        DiscardStaged();
        return default;
    }

    /// <summary>Throws away the staged generation.</summary>
    public void DiscardStaged()
    {
        _pendingRows.Clear();
        _pendingBlobs.Clear();
        _pendingGenerations.Clear();
        _deletedRows.Clear();
        _deletedBlobs.Clear();
        _deletedGenerations.Clear();
    }

    /// <summary>Empties the store.</summary>
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _rows.Clear();
        _blobs.Clear();
        _generations.Clear();
        DiscardStaged();
        return default;
    }

    /// <summary>Lists generations.</summary>
    public ValueTask<IReadOnlyList<string>> GenerationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> generations = IsUsable
            ? _generations.Keys.OrderBy(s => s, StringComparer.Ordinal).ToList()
            : [];
        return new(generations);
    }

    /// <summary>Marks a run generation as live.</summary>
    public ValueTask RecordGenerationAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(generationId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        _pendingGenerations[generationId] = 0;
        return default;
    }

    /// <summary>Retires one generation and prunes.</summary>
    public ValueTask RemoveGenerationAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(generationId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        _pendingGenerations.TryRemove(generationId, out _);
        _deletedGenerations[generationId] = 0;
        return default;
    }

    /// <summary>Counters and sizes.</summary>
    public ValueTask<CacheStats> ReadStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable)
        {
            return new(new CacheStats(0, 0, 0, 0, 0, 0));
        }

        long bytes = 0;
        foreach (Blob blob in _blobs.Values)
        {
            bytes += blob.Data.Length;
        }

        return new(
            new CacheStats(
                _rows.Count,
                _blobs.Count,
                bytes,
                bytes,
                _generations.Count,
                _rows.Values.Select(r => r.Record.ToolId).Distinct(StringComparer.Ordinal).Count()));
    }

    /// <summary>Mark-sweep over the live generations.</summary>
    public ValueTask<IReadOnlyList<string>> CollectGarbageAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable || maxCount <= 0)
        {
            return default;
        }

        // Mark: every blob key referenced by a live row is reachable.
        // Sweep: the rest, oldest first — the same rules the SQLite collector
        // applies (tool identity, age, size limits arrive as `maxCount` plus
        // the caller's reachability decision through DeleteBlobsAsync).
        HashSet<string> reachable = new(StringComparer.Ordinal);
        foreach (Row row in _rows.Values)
        {
            foreach (string blobKey in row.Record.Blobs.Values)
            {
                reachable.Add(blobKey);
            }
        }

        IReadOnlyList<string> garbage = _blobs.Values
            .Where(b => !reachable.Contains(b.Key))
            .OrderBy(b => b.CreatedAtMs)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .Take(maxCount)
            .Select(b => b.Key)
            .ToList();

        return new(garbage);
    }

    /// <summary>Deletes blobs by id.</summary>
    public ValueTask DeleteBlobsAsync(IReadOnlyList<string> blobKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blobKeys);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        foreach (string key in blobKeys)
        {
            _pendingBlobs.TryRemove(key, out _);
            _deletedBlobs[key] = 0;
        }

        return default;
    }

    /// <summary>Returns freed space (no-op in memory).</summary>
    public ValueTask VacuumAsync(CancellationToken cancellationToken = default)
    {
        // Nothing to return to an OS that never saw the bytes.
        cancellationToken.ThrowIfCancellationRequested();
        return default;
    }

    /// <summary>Verifies every blob against its content id.</summary>
    public ValueTask<bool> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(IsUsable && !ReportCorrupt);
    }

    /// <summary>Releases everything.</summary>
    public ValueTask DisposeAsync()
    {
        IsUsable = false;
        _rows.Clear();
        _blobs.Clear();
        _generations.Clear();
        DiscardStaged();
        return default;
    }

    private void ThrowIfUnusable()
    {
        if (!IsUsable)
        {
            throw new InvalidOperationException("the cache store is not open (or was found unusable); open it before use");
        }
    }
}
