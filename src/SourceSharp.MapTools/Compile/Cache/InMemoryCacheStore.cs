//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The package-free store: everything in memory, the whole surface of
/// <see cref="ICacheStore"/>, the same commit/dedup/integrity semantics the
/// SQLite store must have.
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
/// <para>
/// <b>Bound.</b> A long-lived host keeps one store for every compile it runs,
/// so the store holds its own ceiling, <see cref="MaxBytes"/>, rather than
/// leaving it to <see cref="CacheCollector"/> alone. The collector trims to
/// <see cref="CachePolicy.MaxStoreBytes"/> after a commit, but only when the
/// committing compile is the only one in flight
/// (<see cref="ICacheStore.RunsInFlight"/>): a service whose compiles always
/// overlap never gets that quiet moment, and its store used to grow for as
/// long as the process lived. The ceiling here is applied inside every
/// <see cref="CommitAsync"/>, whoever else is running, and it counts what the
/// process actually holds: every committed blob's bytes plus an estimate of
/// every row (<see cref="RowCost"/>), so a store of many small rows is bounded
/// too and not only one of large blobs.
/// </para>
/// <para>
/// When a commit leaves the store above the ceiling, the least recently
/// committed entries go first until it is back at
/// <see cref="CacheCollector.TrimTarget"/> of it (the collector's headroom,
/// so a store sitting at its ceiling is not trimmed again on every commit).
/// "Recently committed" is recently used: the seams re-stage every row a hit
/// replays (<see cref="CacheBlobReader.RenewAsync"/>), so a row that keeps
/// being hit keeps moving to the young end. A lookup alone does not count as
/// a use, because the collector reads every row to plan its work, and that
/// must not make every row look young. Dropping a row drops the blobs no
/// remaining row names; a blob a newer row still names stays. Committed blobs
/// that no row names at all (left by a discarded run) are dropped in the same
/// age order. Ties are broken by key, so the order is the same on every run.
/// </para>
/// <para>
/// Unlike the collector, this ceiling protects no generation: it is the
/// process's memory limit, and a row it drops costs a miss, never a wrong
/// hit, because every blob is content-addressed. A compile in flight may
/// commit a row whose blob was dropped meanwhile: it found the blob stored
/// and did not stage it again, or it staged it and another compile's commit
/// published it before the row was staged and then trimmed it away. The
/// commit leaves such a row out (it would only ever read as a miss), so a
/// committed row never names a blob the store does not hold; the compile
/// that made it recomputes and stores both next time.
/// </para>
/// <para>
/// <b>Concurrency.</b> Staging is lock-free; commits, clears and the trim run
/// one at a time under one lock, and a commit publishes only what it found
/// staged when it started: something another compile stages while the commit
/// runs stays staged for the next commit instead of being cleared unseen. A
/// staged row whose blob is staged but was not yet staged when the commit
/// read the blobs waits for the next commit the same way, so a row never goes
/// in ahead of its blob. Reads never take the lock.
/// </para>
/// </remarks>
public sealed class InMemoryCacheStore : ICacheStore
{
    /// <summary>The default ceiling: the same 1 GiB as <see cref="CachePolicy.DefaultMaxStoreBytes"/>.</summary>
    public const long DefaultMaxBytes = CachePolicy.DefaultMaxStoreBytes;

    /// <summary>
    /// What each committed row is charged besides the text it carries: its
    /// record, dictionaries and list nodes, rounded up (see <see cref="RowCost"/>).
    /// </summary>
    public const int RowOverheadBytes = 256;

    // Seq is the commit that last published the entry: the trim's age order.
    private sealed record Blob(string Key, byte[] Data, string ToolId, long CreatedAtMs, long Seq);

    private sealed record Row(string Key, CacheRecord Record, long Seq, long Cost);

    private readonly Lock _gate = new();
    private long _commitSeq;
    private long _bytes;
    private long _rowsEvicted;
    private long _blobsEvicted;

    /// <summary>Creates a store with the <see cref="DefaultMaxBytes"/> ceiling.</summary>
    public InMemoryCacheStore()
        : this(DefaultMaxBytes)
    {
    }

    /// <summary>Creates a store with a ceiling of its own.</summary>
    /// <param name="maxBytes">
    /// The most the committed rows and blobs may hold together (see
    /// <see cref="MaxBytes"/>); zero or less for no ceiling, which only a
    /// short-lived owner (one command, one test) should choose.
    /// </param>
    public InMemoryCacheStore(long maxBytes) => MaxBytes = maxBytes;

    /// <summary>
    /// The ceiling, in bytes, on what the committed entries hold: every blob's
    /// length plus every row's <see cref="RowCost"/>. Zero or less is none.
    /// </summary>
    public long MaxBytes { get; }

    /// <summary>What the committed entries are charged against <see cref="MaxBytes"/> now.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Rows the ceiling has dropped since the store was made or last cleared.</summary>
    public long RowsEvicted
    {
        get
        {
            lock (_gate)
            {
                return _rowsEvicted;
            }
        }
    }

    /// <summary>Blobs the ceiling has dropped since the store was made or last cleared.</summary>
    public long BlobsEvicted
    {
        get
        {
            lock (_gate)
            {
                return _blobsEvicted;
            }
        }
    }

    /// <summary>
    /// What one row is charged against <see cref="MaxBytes"/>: two bytes per
    /// character of every string it carries (key, stage, tool, tags, key
    /// parts, blob roles and keys, dependency paths and hashes) plus
    /// <see cref="RowOverheadBytes"/>.
    /// </summary>
    /// <param name="record">The row.</param>
    /// <returns>Its charge.</returns>
    /// <remarks>
    /// An estimate, deliberately simple and stable: what matters for a bound
    /// is that a row's charge grows with what it really holds (a vbsp row
    /// records hundreds of dependency paths), not that it matches the
    /// allocator to the byte.
    /// </remarks>
    public static long RowCost(CacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        long chars = record.Key.Length + record.Stage.Length + record.ToolId.Length;
        foreach (string tag in record.ContextTags)
        {
            chars += tag.Length;
        }

        foreach ((string name, string value) in record.Parts)
        {
            chars += name.Length + value.Length;
        }

        foreach ((string role, string blob) in record.Blobs)
        {
            chars += role.Length + blob.Length;
        }

        foreach ((string path, string? hash) in record.Dependencies)
        {
            chars += path.Length + (hash?.Length ?? 0);
        }

        return RowOverheadBytes + (2 * chars);
    }

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

    /// <summary>
    /// For the facts: runs inside <see cref="CommitAsync"/>, under the gate,
    /// after the staged blobs are read and before the staged rows are.
    /// </summary>
    /// <remarks>
    /// Null in every real store. Staging never takes the gate, so a compile
    /// can stage a blob and its row in exactly this gap; the probe widens it
    /// to whatever the fact stages there, making that interleaving
    /// deterministic instead of a matter of scheduling.
    /// </remarks>
    internal Action? BlobsSnapshotProbe { get; init; }

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
        _pendingRows[record.Key] = new Row(record.Key, record, 0, RowCost(record));
        return default;
    }

    /// <summary>Stages a blob under its content id.</summary>
    public ValueTask PutBlobAsync(string blobKey, ReadOnlyMemory<byte> data, string toolId, long createdAtMs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(blobKey);
        ArgumentException.ThrowIfNullOrEmpty(toolId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        _pendingBlobs[blobKey] = new Blob(blobKey, data.ToArray(), toolId, createdAtMs, 0);
        return default;
    }

    /// <summary>Whether a blob is present.</summary>
    public ValueTask<bool> HasBlobAsync(string blobKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(IsUsable && _blobs.ContainsKey(blobKey));
    }

    /// <summary>The size of one committed blob.</summary>
    public ValueTask<long?> BlobSizeAsync(string blobKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(IsUsable && _blobs.TryGetValue(blobKey, out Blob? blob) ? blob.Data.LongLength : null);
    }

    /// <inheritdoc/>
    public IDisposable BeginRun() => _runs.Begin();

    /// <inheritdoc/>
    public int RunsInFlight => _runs.Count;

    private readonly CacheRunLeases _runs = new();

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

    /// <summary>Publishes the staged generation atomically, then holds the store to <see cref="MaxBytes"/>.</summary>
    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();

        lock (_gate)
        {
            if (FailNextCommit is Exception failure)
            {
                FailNextCommit = null;
                throw failure;
            }

            // What is staged now is this commit; each entry is taken out of
            // staging only if it is still the one read here, so a concurrent
            // compile's later staging survives for the next commit.
            // ConcurrentDictionary.ToArray is an atomic snapshot; a collection
            // expression would copy by a count taken before the copy and
            // throw when a writer adds in between.
            KeyValuePair<string, byte>[] deletedRows = _deletedRows.ToArray();
            KeyValuePair<string, byte>[] deletedGenerations = _deletedGenerations.ToArray();
            KeyValuePair<string, byte>[] deletedBlobs = _deletedBlobs.ToArray();
            KeyValuePair<string, Blob>[] pendingBlobs = _pendingBlobs.ToArray();
            BlobsSnapshotProbe?.Invoke();
            KeyValuePair<string, Row>[] pendingRows = _pendingRows.ToArray();
            KeyValuePair<string, byte>[] pendingGenerations = _pendingGenerations.ToArray();
            long seq = ++_commitSeq;

            // Single publish point: every staged mutation becomes visible here
            // and nowhere else. Deletions first, so a row re-staged after a
            // delete wins, as in the SQLite store.
            foreach (KeyValuePair<string, byte> entry in deletedRows)
            {
                if (_rows.TryRemove(entry.Key, out Row? gone))
                {
                    _bytes -= gone.Cost;
                }

                _deletedRows.TryRemove(entry);
            }

            foreach (KeyValuePair<string, byte> entry in deletedGenerations)
            {
                _generations.TryRemove(entry.Key, out _);
                _deletedGenerations.TryRemove(entry);
            }

            foreach (KeyValuePair<string, byte> entry in deletedBlobs)
            {
                if (_blobs.TryRemove(entry.Key, out Blob? gone))
                {
                    _bytes -= gone.Data.LongLength;
                }

                _deletedBlobs.TryRemove(entry);
            }

            foreach (KeyValuePair<string, Blob> entry in pendingBlobs)
            {
                if (_blobs.TryGetValue(entry.Key, out Blob? before))
                {
                    _bytes -= before.Data.LongLength;
                }

                _blobs[entry.Key] = entry.Value with { Seq = seq };
                _bytes += entry.Value.Data.LongLength;
                _pendingBlobs.TryRemove(entry);
            }

            foreach (KeyValuePair<string, Row> entry in pendingRows)
            {
                // A row is published only when the store holds every blob it
                // names, checked under the gate after this commit's blobs are
                // in, so no committed row ever names a blob that is gone.
                switch (BlobsFor(entry.Value.Record))
                {
                    case BlobState.Staged:
                        // Staged after the blob snapshot above, with the row
                        // staged before the row snapshot: both belong to the
                        // next commit, so the row waits in staging for it.
                        continue;
                    case BlobState.Gone:
                        // Neither held nor staged. Staging is lock-free and a
                        // compile stages its blob before its row, so another
                        // compile's commit can land between the two: it
                        // publishes the blob alone, and its trim, seeing no
                        // committed row name the blob once the older rows
                        // sharing it go, drops it. (A compile that found the
                        // blob stored and did not stage it again ends up
                        // here too.) Publishing the row would leave it naming
                        // a blob that is gone; it would only ever read as a
                        // miss, so it is dropped from staging as that miss.
                        _pendingRows.TryRemove(entry);
                        continue;
                }

                if (_rows.TryGetValue(entry.Key, out Row? before))
                {
                    _bytes -= before.Cost;
                }

                _rows[entry.Key] = entry.Value with { Seq = seq };
                _bytes += entry.Value.Cost;
                _pendingRows.TryRemove(entry);
            }

            foreach (KeyValuePair<string, byte> entry in pendingGenerations)
            {
                _generations[entry.Key] = 0;
                _pendingGenerations.TryRemove(entry);
            }

            TrimToBound();
        }

        return default;
    }

    /// <summary>
    /// Drops the least recently committed entries until the store is back at
    /// <see cref="CacheCollector.TrimTarget"/> of <see cref="MaxBytes"/>, when
    /// it is above the ceiling. Runs under the gate.
    /// </summary>
    private void TrimToBound()
    {
        if (MaxBytes <= 0 || _bytes <= MaxBytes)
        {
            return;
        }

        long target = (long)(MaxBytes * CacheCollector.TrimTarget);

        // How many rows name each blob: a blob goes only when none is left.
        Dictionary<string, int> references = new(StringComparer.Ordinal);
        foreach (Row row in _rows.Values)
        {
            foreach (string blob in row.Record.Blobs.Values.Distinct(StringComparer.Ordinal))
            {
                references[blob] = references.GetValueOrDefault(blob) + 1;
            }
        }

        // Rows, and blobs no row names, oldest commit first; ties by key,
        // rows before blobs, so the order never depends on hashing.
        List<(long Seq, bool IsBlob, string Key)> order = [];
        foreach (Row row in _rows.Values)
        {
            order.Add((row.Seq, false, row.Key));
        }

        foreach (Blob blob in _blobs.Values)
        {
            if (!references.ContainsKey(blob.Key))
            {
                order.Add((blob.Seq, true, blob.Key));
            }
        }

        order.Sort(static (a, b) =>
        {
            int bySeq = a.Seq.CompareTo(b.Seq);
            if (bySeq != 0)
            {
                return bySeq;
            }

            int byKind = a.IsBlob.CompareTo(b.IsBlob);
            return byKind != 0 ? byKind : string.CompareOrdinal(a.Key, b.Key);
        });

        foreach ((_, bool isBlob, string key) in order)
        {
            if (_bytes <= target)
            {
                break;
            }

            if (isBlob)
            {
                DropBlob(key);
                continue;
            }

            if (!_rows.TryRemove(key, out Row? row))
            {
                continue;
            }

            _bytes -= row.Cost;
            _rowsEvicted++;
            foreach (string blob in row.Record.Blobs.Values.Distinct(StringComparer.Ordinal))
            {
                if (--references[blob] == 0)
                {
                    DropBlob(blob);
                }
            }
        }
    }

    private enum BlobState
    {
        Held,
        Staged,
        Gone,
    }

    // Whether every blob a row names is held; if not, whether each missing
    // one is at least staged (a later commit will hold it) or gone. Runs
    // under the gate, after the commit's blobs are published.
    private BlobState BlobsFor(CacheRecord record)
    {
        BlobState state = BlobState.Held;
        foreach (string blob in record.Blobs.Values)
        {
            if (_blobs.ContainsKey(blob))
            {
                continue;
            }

            if (!_pendingBlobs.ContainsKey(blob))
            {
                return BlobState.Gone;
            }

            state = BlobState.Staged;
        }

        return state;
    }

    private void DropBlob(string key)
    {
        if (_blobs.TryRemove(key, out Blob? blob))
        {
            _bytes -= blob.Data.LongLength;
            _blobsEvicted++;
        }
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
        lock (_gate)
        {
            _rows.Clear();
            _blobs.Clear();
            _generations.Clear();
            DiscardStaged();
            _bytes = 0;
            _rowsEvicted = 0;
            _blobsEvicted = 0;
        }

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
        lock (_gate)
        {
            IsUsable = false;
            _rows.Clear();
            _blobs.Clear();
            _generations.Clear();
            DiscardStaged();
            _bytes = 0;
        }

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
