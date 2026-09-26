//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Data.Sqlite;

using SourceSharp.MapTools.Compile.Cache;

namespace SourceSharp.MapTools.Cache.Sqlite;

/// <summary>
/// The on-disk store the cache design rules commit to: one
/// <c>&lt;map&gt;.sscache.db</c> SQLite database beside the map, WAL journal,
/// blobs keyed by content hash.
/// </summary>
/// <remarks>
/// <para>
/// This is the optional assembly — the ONLY place a SQLite package is
/// permitted (plan ruling Q3): <see cref="ICacheStore"/> lives in package-free
/// MapTools, this implements it, and nothing in MapTools references back. A
/// host that wants no disk cache never loads this type.
/// </para>
/// <para>
/// <b>Staged commit.</b> The same posture the in-memory store ships: every
/// write method lands in a pending set that no read consults;
/// <see cref="CommitAsync"/> applies the whole pending set inside ONE SQLite
/// transaction, so the committed state jumps atomically from old to new. A
/// <c>SIGKILL</c> between staging and commit (or inside the transaction)
/// leaves the file exactly as the last commit left it — WAL rolls the
/// half-transaction back on the next open — which is the plan's
/// kill-mid-commit fact. <see cref="FailNextCommit"/> injects the same failure
/// from the test side.
/// </para>
/// <para>
/// <b>Self-identifying.</b> The database pins <c>PRAGMA user_version</c> and a
/// <c>meta</c> row naming its schema; a file that is not this store (foreign
/// version, foreign tool, truncated header) fails <see cref="OpenAsync"/> or
/// leaves <see cref="IsUsable"/> false, so the cache degrades to cold instead
/// of serving rows whose bytes came from elsewhere.
/// </para>
/// <para>
/// All members serialise on one async gate over a single connection; the
/// cache layer overlaps reads with cooking and must not interleave SQLite
/// statements.
/// </para>
/// </remarks>
public sealed partial class SqliteCacheStore : ICacheStore
{
    /// <summary>The schema tag written to <c>meta</c>; opening a foreign tag fails.</summary>
    public const string SchemaTag = "sscache-1";

    private const int UserVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheRecord> _pendingRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (byte[] Data, string ToolId, long CreatedAtMs)> _pendingBlobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingGenerations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedRows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedBlobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedGenerations = new(StringComparer.Ordinal);

    private SqliteConnection? _connection;

    /// <summary>
    /// Creates the store; call <see cref="OpenAsync"/> before use.
    /// </summary>
    /// <param name="readOnly">
    /// Open the file read-only: every read serves, every write throws — the
    /// posture for compiling against somebody else's cache (or a shared
    /// corpus store). A missing file has nothing to read and fails the open.
    /// </param>
    public SqliteCacheStore(bool readOnly = false) => ReadOnly = readOnly;

    /// <summary>Whether this store opened its file read-only.</summary>
    public bool ReadOnly { get; }

    /// <summary>Where <see cref="OpenAsync"/> put the file.</summary>
    public string? Location { get; private set; }

    /// <summary>Whether <see cref="OpenAsync"/> succeeded and rows may be trusted.</summary>
    public bool IsUsable { get; private set; }

    /// <summary>Pending rows + blobs not yet committed — the staged-invisibility fact inspects this.</summary>
    public int StagedCount => _pendingRows.Count + _pendingBlobs.Count;

    /// <summary>When set (by tests), the next <see cref="CommitAsync"/> throws after applying nothing.</summary>
    public Exception? FailNextCommit { get; set; }

    /// <summary>When set (by tests), <see cref="CheckIntegrityAsync"/> reports damage without running the check.</summary>
    public bool ReportCorrupt { get; set; }

    /// <inheritdoc/>
    public async ValueTask OpenAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        if (IsUsable)
        {
            throw new InvalidOperationException("the store is already open; dispose it before reopening");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteConnectionStringBuilder builder = new() { DataSource = location, Pooling = false };
            if (ReadOnly)
            {
                builder.Mode = SqliteOpenMode.ReadOnly;
            }

            SqliteConnection connection = new(builder.ToString());
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await PragmaAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
                if (!ReadOnly)
                {
                    // WAL + NORMAL: the cache survives a killed process (the
                    // plan's kill-mid-commit fact) and does not fsync per
                    // commit; losing the last commit to an OS crash is, by
                    // ruling Q7, a miss and not a defect.
                    await PragmaAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
                    await PragmaAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);
                    await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
                }

                await CheckIdentityAsync(connection, ReadOnly, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                IsUsable = false;
                throw;
            }

            _connection = connection;
            Location = location;
            IsUsable = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<CacheRecord?> LookupAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable || !CacheKey.LooksLikeDigest(key))
        {
            return null;
        }

        return await RunAsync(
            async (connection, token) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "SELECT stage, tool_id, tags, parts, deps, cost_ms, created_ms FROM rows_"
                    + " WHERE key = $key;";
                command.Parameters.AddWithValue("$key", key);
                using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return null;
                }

                Dictionary<string, string> blobs = [];
                using (SqliteCommand blobCommand = connection.CreateCommand())
                {
                    blobCommand.CommandText = "SELECT role, blob_key FROM row_blobs WHERE key = $key;";
                    blobCommand.Parameters.AddWithValue("$key", key);
                    using SqliteDataReader blobReader = await blobCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
                    while (await blobReader.ReadAsync(token).ConfigureAwait(false))
                    {
                        blobs[blobReader.GetString(0)] = blobReader.GetString(1);
                    }
                }

                return new CacheRecord(
                    key,
                    reader.GetString(0),
                    reader.GetString(1),
                    ReadStrings(reader.GetString(2)),
                    ReadParts(reader.GetString(3)),
                    blobs,
                    ReadDeps(reader.GetString(4)),
                    reader.GetInt64(5),
                    reader.GetInt64(6));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> KeysAsync(CancellationToken cancellationToken) =>
        ScalarListAsync("SELECT key FROM rows_ ORDER BY key;", cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> FindKeysByDepAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return RunAsync(
            async (connection, token) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT DISTINCT key FROM deps WHERE path = $path ORDER BY key;";
                command.Parameters.AddWithValue("$path", path);
                return await ReadStringsAsync(command, token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> LiveToolIdsAsync(CancellationToken cancellationToken) =>
        ScalarListAsync("SELECT DISTINCT tool_id FROM rows_ ORDER BY tool_id;", cancellationToken);

    /// <inheritdoc/>
    public async ValueTask PutAsync(CacheRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        ThrowIfReadOnly();
        _ = record.ToolId; // shape check: a record without an identity must never reach the table
        _pendingRows[record.Key] = record;
        _deletedRows.Remove(record.Key);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask PutBlobAsync(string blobKey, ReadOnlyMemory<byte> data, string toolId, long createdAtMs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(blobKey);
        ArgumentException.ThrowIfNullOrEmpty(toolId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        ThrowIfReadOnly();
        _pendingBlobs[blobKey] = (data.ToArray(), toolId, createdAtMs);
        _deletedBlobs.Remove(blobKey);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> HasBlobAsync(string blobKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable || !CacheKey.LooksLikeDigest(blobKey))
        {
            return false;
        }

        object? found = await RunAsync(
            async (connection, token) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM blobs WHERE key = $key;";
                command.Parameters.AddWithValue("$key", blobKey);
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return found is not null;
    }

    /// <inheritdoc/>
    public async ValueTask<byte[]?> GetBlobAsync(string blobKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable || !CacheKey.LooksLikeDigest(blobKey))
        {
            return null;
        }

        return await RunAsync(
            async (connection, token) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT data FROM blobs WHERE key = $key;";
                command.Parameters.AddWithValue("$key", blobKey);
                object? data = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return data is byte[] bytes ? (byte[])bytes.Clone() : null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        ThrowIfReadOnly();
        _pendingRows.Remove(key);
        _ = _deletedRows.Add(key);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        ThrowIfReadOnly();
        if (FailNextCommit is Exception failure)
        {
            FailNextCommit = null;
            throw failure;
        }

        if (_pendingRows.Count == 0 && _pendingBlobs.Count == 0 && _pendingGenerations.Count == 0
            && _deletedRows.Count == 0 && _deletedBlobs.Count == 0 && _deletedGenerations.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteConnection connection = Connection;
            using SqliteTransaction transaction = connection.BeginTransaction();

            // Deletions first: a staged row may re-add a key a delete names,
            // and the insert phase must win that contest (same net effect as
            // the in-memory store's delete-then-apply order).
            foreach (string key in _deletedRows)
            {
                await ExecAsync(connection, transaction, "DELETE FROM rows_ WHERE key = $key; DELETE FROM row_blobs WHERE key = $key; DELETE FROM deps WHERE key = $key;", key, cancellationToken).ConfigureAwait(false);
            }

            foreach (string blobKey in _deletedBlobs)
            {
                await ExecAsync(connection, transaction, "DELETE FROM blobs WHERE key = $key;", blobKey, cancellationToken).ConfigureAwait(false);
            }

            foreach (string generation in _deletedGenerations)
            {
                await ExecAsync(connection, transaction, "DELETE FROM generations WHERE id = $id;", generation, cancellationToken).ConfigureAwait(false);
            }

            foreach ((string blobKey, (byte[] data, string toolId, long createdAtMs)) in _pendingBlobs)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO blobs(key, tool_id, created_ms, data) VALUES ($key, $tool, $created, $data)"
                    + " ON CONFLICT(key) DO UPDATE SET tool_id = excluded.tool_id, created_ms = excluded.created_ms, data = excluded.data;";
                command.Parameters.AddWithValue("$key", blobKey);
                command.Parameters.AddWithValue("$tool", toolId);
                command.Parameters.AddWithValue("$created", createdAtMs);
                command.Parameters.AddWithValue("$data", data);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach ((string key, CacheRecord record) in _pendingRows)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO rows_(key, stage, tool_id, tags, parts, deps, cost_ms, created_ms)"
                    + " VALUES ($key, $stage, $tool, $tags, $parts, $deps, $cost, $created)"
                    + " ON CONFLICT(key) DO UPDATE SET stage = excluded.stage, tool_id = excluded.tool_id,"
                    + " tags = excluded.tags, parts = excluded.parts, deps = excluded.deps,"
                    + " cost_ms = excluded.cost_ms, created_ms = excluded.created_ms;";
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$stage", record.Stage);
                command.Parameters.AddWithValue("$tool", record.ToolId);
                command.Parameters.AddWithValue("$tags", WriteStrings(record.ContextTags));
                command.Parameters.AddWithValue("$parts", WriteParts(record.Parts));
                command.Parameters.AddWithValue("$deps", WriteDeps(record.Dependencies));
                command.Parameters.AddWithValue("$cost", record.CostMs);
                command.Parameters.AddWithValue("$created", record.CreatedAtMs);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await ExecAsync(connection, transaction, "DELETE FROM row_blobs WHERE key = $key; DELETE FROM deps WHERE key = $key;", key, cancellationToken).ConfigureAwait(false);

                using (SqliteCommand children = connection.CreateCommand())
                {
                    children.Transaction = transaction;
                    children.CommandText = "INSERT INTO row_blobs(key, role, blob_key) VALUES ($key, $role, $blob);";
                    SqliteParameter keyParameter = children.Parameters.Add("$key", SqliteType.Text);
                    SqliteParameter roleParameter = children.Parameters.Add("$role", SqliteType.Text);
                    SqliteParameter blobParameter = children.Parameters.Add("$blob", SqliteType.Text);
                    keyParameter.Value = key;
                    foreach ((string role, string blobKey) in record.Blobs)
                    {
                        roleParameter.Value = role;
                        blobParameter.Value = blobKey;
                        await children.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                if (record.Dependencies.Count > 0)
                {
                    using SqliteCommand children = connection.CreateCommand();
                    children.Transaction = transaction;
                    children.CommandText = "INSERT INTO deps(key, path, content_hash) VALUES ($key, $path, $hash);";
                    SqliteParameter keyParameter = children.Parameters.Add("$key", SqliteType.Text);
                    SqliteParameter pathParameter = children.Parameters.Add("$path", SqliteType.Text);
                    SqliteParameter hashParameter = children.Parameters.Add("$hash", SqliteType.Text);
                    keyParameter.Value = key;
                    foreach ((string depPath, string? contentHash) in record.Dependencies)
                    {
                        pathParameter.Value = depPath;
                        // A recorded miss is a NULL column: SqliteParameter reads a
                        // null Value as "unset", so the miss must bind DBNull.
                        hashParameter.Value = (object?)contentHash ?? DBNull.Value;
                        await children.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            foreach (string generation in _pendingGenerations)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO generations(id) VALUES ($id) ON CONFLICT(id) DO NOTHING;";
                command.Parameters.AddWithValue("$id", generation);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
            DiscardStaged();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void DiscardStaged()
    {
        _pendingRows.Clear();
        _pendingBlobs.Clear();
        _pendingGenerations.Clear();
        _deletedRows.Clear();
        _deletedBlobs.Clear();
        _deletedGenerations.Clear();
    }

    /// <inheritdoc/>
    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        ThrowIfReadOnly();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteTransaction transaction = Connection.BeginTransaction();
            using SqliteCommand command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM rows_; DELETE FROM row_blobs; DELETE FROM deps; DELETE FROM blobs; DELETE FROM generations;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }

        DiscardStaged();
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GenerationsAsync(CancellationToken cancellationToken) =>
        ScalarListAsync("SELECT id FROM generations ORDER BY id;", cancellationToken);

    /// <inheritdoc/>
    public async ValueTask RecordGenerationAsync(string generationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(generationId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        ThrowIfReadOnly();
        _ = _pendingGenerations.Add(generationId);
        _deletedGenerations.Remove(generationId);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask RemoveGenerationAsync(string generationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(generationId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        ThrowIfReadOnly();
        _pendingGenerations.Remove(generationId);
        _ = _deletedGenerations.Add(generationId);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<CacheStats> ReadStatsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable)
        {
            return new CacheStats(0, 0, 0, 0, 0, 0);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteConnection connection = Connection;
            (int keys, int tools) = await OneRowAsync(connection,
                "SELECT COUNT(*), COUNT(DISTINCT tool_id) FROM rows_;", cancellationToken).ConfigureAwait(false);
            (int blobs, long bytes) = await OneBlobRowAsync(connection,
                "SELECT COUNT(*), COALESCE(SUM(LENGTH(data)), 0) FROM blobs;", cancellationToken).ConfigureAwait(false);
            int generations = (int)(long)(await ScalarAsync(connection, "SELECT COUNT(*) FROM generations;", cancellationToken).ConfigureAwait(false) ?? 0L);

            long fileBytes = 0;
            if (Location is { } location && File.Exists(location))
            {
                fileBytes = new FileInfo(location).Length;
            }

            return new CacheStats(keys, blobs, bytes, fileBytes, generations, tools);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<string>> CollectGarbageAsync(int maxCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable || maxCount <= 0)
        {
            return [];
        }

        // Mark-sweep in one query: a blob is garbage when no committed row
        // names it, oldest first — the same rules the in-memory store applies.
        return await RunAsync(
            async (connection, token) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "SELECT key FROM blobs WHERE key NOT IN (SELECT blob_key FROM row_blobs)"
                    + " ORDER BY created_ms, key LIMIT $max;";
                command.Parameters.AddWithValue("$max", maxCount);
                return await ReadStringsAsync(command, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DeleteBlobsAsync(IReadOnlyList<string> blobKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blobKeys);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        ThrowIfReadOnly();
        foreach (string key in blobKeys)
        {
            _pendingBlobs.Remove(key);
            _ = _deletedBlobs.Add(key);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask VacuumAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnusable();
        ThrowIfReadOnly();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PragmaAsync(Connection, "VACUUM;", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> CheckIntegrityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable || ReportCorrupt)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            object? verdict = await ScalarAsync(Connection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
            return string.Equals(verdict as string, "ok", StringComparison.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        DiscardStaged();
        IsUsable = false;
        if (_connection is { } connection)
        {
            _connection = null;
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private SqliteConnection Connection =>
        _connection ?? throw new InvalidOperationException("the cache store is not open");

    private void ThrowIfUnusable()
    {
        if (!IsUsable)
        {
            throw new InvalidOperationException("the cache store is not open (or was found unusable); open it before use");
        }
    }

    private void ThrowIfReadOnly()
    {
        if (ReadOnly)
        {
            throw new InvalidOperationException("the cache store is open read-only; writes need a read-write open");
        }
    }

    private async ValueTask<T> RunAsync<T>(Func<SqliteConnection, CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUsable)
        {
            return default!;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(Connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ValueTask<IReadOnlyList<string>> ScalarListAsync(string sql, CancellationToken cancellationToken) =>
        RunAsync(
            async (connection, token) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = sql;
                return await ReadStringsAsync(command, token).ConfigureAwait(false);
            },
            cancellationToken);

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        List<string> values = [];
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async ValueTask<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(int, int)> OneRowAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (checked((int)reader.GetInt64(0)), checked((int)reader.GetInt64(1)));
    }

    private static async Task<(int, long)> OneBlobRowAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (checked((int)reader.GetInt64(0)), reader.GetInt64(1));
    }
    private async ValueTask ExecAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, string parameter, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (sql.Contains("$key", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$key", parameter);
        }

        if (sql.Contains("$id", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$id", parameter);
        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private static async ValueTask PragmaAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        object? version = await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        long current = version is long v ? v : 0;
        if (current > UserVersion)
        {
            throw new InvalidDataException($"the cache store is from a newer schema (user_version {current}); this build reads {UserVersion}");
        }

        using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.CommandText =
                "PRAGMA user_version = " + UserVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";"
                + "CREATE TABLE IF NOT EXISTS meta(k TEXT NOT NULL PRIMARY KEY, v TEXT NOT NULL);"
                + "CREATE TABLE IF NOT EXISTS rows_(key TEXT NOT NULL PRIMARY KEY, stage TEXT NOT NULL, tool_id TEXT NOT NULL,"
                + " tags TEXT NOT NULL, parts TEXT NOT NULL, deps TEXT NOT NULL, cost_ms INTEGER NOT NULL, created_ms INTEGER NOT NULL);"
                + "CREATE TABLE IF NOT EXISTS row_blobs(key TEXT NOT NULL, role TEXT NOT NULL, blob_key TEXT NOT NULL, PRIMARY KEY(key, role));"
                + "CREATE TABLE IF NOT EXISTS deps(key TEXT NOT NULL, path TEXT NOT NULL, content_hash TEXT);"
                + "CREATE TABLE IF NOT EXISTS blobs(key TEXT NOT NULL PRIMARY KEY, tool_id TEXT NOT NULL, created_ms INTEGER NOT NULL, data BLOB NOT NULL);"
                + "CREATE TABLE IF NOT EXISTS generations(id TEXT NOT NULL PRIMARY KEY);"
                + "CREATE INDEX IF NOT EXISTS row_blobs_blob ON row_blobs(blob_key);"
                + "CREATE INDEX IF NOT EXISTS deps_path ON deps(path);";
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (SqliteCommand meta = connection.CreateCommand())
        {
            meta.CommandText = "INSERT INTO meta(k, v) VALUES('schema', $tag) ON CONFLICT(k) DO NOTHING;";
            meta.Parameters.AddWithValue("$tag", SchemaTag);
            await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask CheckIdentityAsync(SqliteConnection connection, bool readOnly, CancellationToken cancellationToken)
    {
        using SqliteCommand meta = connection.CreateCommand();
        meta.CommandText = "SELECT v FROM meta WHERE k = 'schema';";
        object? tag = await meta.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (tag is string text && !string.Equals(text, SchemaTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"the cache store names schema '{text}', not '{SchemaTag}'");
        }

        if (tag is null)
        {
            // An empty-but-valid file (never written, or fully cleared before
            // this build stamped it) carries no rows to distrust; stamp it.
            if (readOnly)
            {
                throw new InvalidDataException("a read-only store was opened over a file that never held a cache");
            }

            using SqliteCommand stamp = connection.CreateCommand();
            stamp.CommandText = "INSERT INTO meta(k, v) VALUES('schema', $tag);";
            stamp.Parameters.AddWithValue("$tag", SchemaTag);
            await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // The record's list/dictionary members travel as compact JSON — System.Text.Json
    // is BCL, so this assembly's only package stays Microsoft.Data.Sqlite.
    //
    // The serialisation goes through the source-generated metadata of
    // <see cref="RecordJson"/> rather than the reflection-based entry points:
    // under NativeAOT (where this assembly is linked, t-14) the reflection
    // path has no JIT to build its converters with and throws outright, so the
    // store would be unusable in the very posture the optional package is
    // meant to serve. The generated shape is the same JSON — a flat array of
    // strings, an array of two-element string arrays — so files written by
    // either build read on the other (t13 lineage stays readable).
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(List<string[]>))]
    [JsonSerializable(typeof(List<string?[]>))]
    private sealed partial class RecordJson : JsonSerializerContext;

    private static readonly JsonTypeInfo<string[]> StringsTypeInfo =
        (JsonTypeInfo<string[]>)RecordJson.Default.GetTypeInfo(typeof(string[]))!;
    private static readonly JsonTypeInfo<List<string[]>> PairsTypeInfo =
        (JsonTypeInfo<List<string[]>>)RecordJson.Default.GetTypeInfo(typeof(List<string[]>))!;
    private static readonly JsonTypeInfo<List<string?[]>> DepsTypeInfo =
        (JsonTypeInfo<List<string?[]>>)RecordJson.Default.GetTypeInfo(typeof(List<string?[]>))!;
    private static string WriteStrings(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values as string[] ?? [.. values], StringsTypeInfo);

    private static IReadOnlyList<string> ReadStrings(string json) =>
        JsonSerializer.Deserialize(json, StringsTypeInfo) ?? [];

    private static string WriteParts(IReadOnlyList<(string Name, string Value)> parts) =>
        JsonSerializer.Serialize(
            [.. parts.Select(static p => new string[] { p.Name, p.Value })],
            PairsTypeInfo);

    private static IReadOnlyList<(string Name, string Value)> ReadParts(string json) =>
        (JsonSerializer.Deserialize(json, PairsTypeInfo) ?? [])
            .Select(static pair => (pair[0], pair[1]))
            .ToList();

    private static string WriteDeps(IReadOnlyList<(string Path, string? ContentHash)> deps) =>
        JsonSerializer.Serialize(
            [.. deps.Select(static d => new string?[] { d.Path, d.ContentHash })],
            DepsTypeInfo);

    private static IReadOnlyList<(string Path, string? ContentHash)> ReadDeps(string json) =>
        (JsonSerializer.Deserialize(json, PairsTypeInfo) ?? [])
            .Select(static pair => (pair[0], pair.Length > 1 ? pair[1] : null))
            .ToList();
}
