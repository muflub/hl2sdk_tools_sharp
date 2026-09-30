//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Nav;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Everything besides the room itself that decides a room's pack sections:
/// what <see cref="RoomCacheKey"/> folds next to the room's own content.
/// </summary>
/// <param name="Options">
/// The vbsp switches every room compiles with, after the format pipeline
/// (<see cref="VbspOptions.Format"/> is the resolved format, which the
/// mounted game's <c>gameinfo.txt</c> can change).
/// </param>
/// <remarks>
/// <para>
/// <b>What is in it, and why each part.</b> A room's sections are
/// <see cref="RoomPackItem.CreateAsync(RoomObject, RoomNavPackOptions, CancellationToken)"/>
/// of the room compiled by <see cref="RoomLibraryCompiler"/>, and that
/// compile reads exactly: the room's own VMF and <c>info_room</c> claims
/// (the key's semantic part, <see cref="RoomCacheKey.RoomDigest"/>), the
/// vbsp options, the library's navigation settings (from its worldspawn's
/// <c>nav*</c> keys) with the pack's navigation storage, the library's
/// name keys (<c>rooms_name_keys</c>, which widen what the naming rule
/// reads as a name and so change the <c>NAM</c> sections and the warnings),
/// the collision cooker (a context tag), and the game content the compile
/// read (verified per row, <see cref="RoomCompileCache"/>).
/// </para>
/// <para>
/// <b>What is deliberately not in it.</b> The library-wide entities
/// (<c>LENT</c>) and the library's other settings (<c>LOPT</c>: the entity
/// reserve, logic folding) are library sections the link reads; no room
/// compile sees them, so an edit to them rewrites those sections and
/// reuses every room. <c>-threads</c> is left out because the rooms'
/// bytes are the same at any thread count. <see cref="VbspOptions.Verbose"/>
/// and <see cref="VbspOptions.VerboseEntities"/> are left out because they
/// change what is logged and nothing that is written; a fact holds a room's
/// sections to that.
/// </para>
/// </remarks>
public sealed record RoomCacheInputs(VbspOptions Options)
{
    /// <summary>The library's navigation settings, or null when it builds none.</summary>
    public NavSettings? Nav { get; init; }

    /// <summary>How the pack stores each room's navigation (<c>-nav-turn0</c>, <c>-nav-codec</c>).</summary>
    public RoomNavPackOptions PackOptions { get; init; } = new();

    /// <summary>The library's name-valued keys (<see cref="RoomLibraryOptions.NameKeySet"/>), or null.</summary>
    public IReadOnlySet<string>? NameKeys { get; init; }

    /// <summary>
    /// How the rooms are lit (<see cref="RoomLibraryCompileSettings.Lighting"/>),
    /// or null for unlit rooms: the vrad switches and the library's sun both
    /// shape the lighting section, so both are in the key. Folded only when
    /// set, so an unlit library keeps the keys it had before the bake.
    /// </summary>
    public RoomLightingSettings? Lighting { get; init; }

    /// <summary>
    /// The host's opaque context tags, folded verbatim: <c>ssmap room</c>
    /// passes the format preset and the collision cooker's identity, the same
    /// tags <c>ssmap all -incremental</c> folds, so rooms cooked by one
    /// cooker are never served to a run with another.
    /// </summary>
    public IReadOnlyList<string> ContextTags { get; init; } = [];
}

/// <summary>
/// The cache key of one room's finished pack sections: which room, compiled
/// how, by which build.
/// </summary>
/// <remarks>
/// <para>
/// <b>The room's own content, canonicalised by the split.</b> The semantic
/// part is not the library's text but the room as
/// <see cref="RoomLibraryVmf.SplitLibrary"/> hands it to the compile: its
/// room-local document (the library's <c>versioninfo</c> and worldspawn
/// keys less the library-only ones, its own world brushes and entities,
/// moved to its cell) written back out with the VMF writer, and its
/// <c>info_room</c> claims (name, cell, door kit, sockets, role). Parsing
/// and writing again drops what the compile never sees (whitespace, comments,
/// Hammer's view and camera chunks), and another room's brushes and
/// entities are simply not in the document, so an edit to another room, or a
/// reorder of the library that interleaves another room's entities with
/// this one's, leaves the key alone. What stays in is what the compile reads,
/// such as the order of the room's own entities (the entity lump keeps it).
/// The library's <c>mapversion</c>, which an editor bumps on every save, is
/// not an input: the split gives every room a fixed one and the link stamps
/// the library's (<see cref="RoomLibraryOptions.MapVersionKey"/>), so a save
/// that changes nothing else recompiles nothing.
/// </para>
/// <para>
/// <b>The fold.</b> The parts go through the shared <see cref="CacheKey"/>
/// fold, under the stage name <see cref="StageName"/> and this build's
/// <see cref="ToolIdentity"/>, so <c>ssmap cache explain</c> reads a room
/// row like any other and a new build never reads an old build's rooms.
/// <see cref="Revision"/> is folded into the semantic part so a change to
/// how a row is laid out retires every row of the old layout.
/// </para>
/// </remarks>
public static class RoomCacheKey
{
    /// <summary>The stage name room rows carry.</summary>
    public const string StageName = "room";

    /// <summary>The row layout's revision; raised when <see cref="RoomCompileCache"/> stores rows differently.</summary>
    public const int Revision = 1;

    /// <summary>The key of a room compiled with these inputs.</summary>
    /// <param name="room">The room, as the library split gave it.</param>
    /// <param name="inputs">The library-wide inputs.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static CacheKey Of(LibraryRoom room, RoomCacheInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(inputs);
        return new CacheKey
        {
            Stage = StageName,
            ToolId = ToolIdentity.Current,
            SemanticDigest = RoomDigest(room),

            // A room of a combined pack carries its own library's name keys
            // (LibraryRoom.Namespace); every other room, the run's.
            OptionsDigest = OptionsDigestOf(room.Namespace is null ? inputs : inputs with { NameKeys = room.NameKeysOr(inputs.NameKeys) }),
            DependencyDigest = string.Empty,
            ContextTags = inputs.ContextTags,
        };
    }

    /// <summary>
    /// The digest of the room itself: its room-local document as the VMF
    /// writer writes it, and its <c>info_room</c> claims.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    /// <remarks>
    /// <see cref="LibraryRoom.Corner"/> is not folded: the document is
    /// already room-local, so a room moved to another cell of the library
    /// with the same local content compiles to the same bytes.
    /// </remarks>
    public static string RoomDigest(LibraryRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        using Folder fold = new();
        fold.Text("room/" + Revision.ToString(CultureInfo.InvariantCulture));
        fold.Bytes(room.Document.ToBytes());
        RoomDefinition definition = room.Definition;
        fold.Text(definition.Name);
        fold.Float(definition.CellSize);
        fold.Float(definition.Kit.Width);
        fold.Float(definition.Kit.Height);
        fold.Float(definition.Kit.Depth);
        fold.Int(definition.Sockets.Count);
        foreach (RoomSocket socket in definition.Sockets)
        {
            fold.Int((int)socket.Facing);
            fold.Text(socket.Name);
        }

        fold.Int((int)room.Role);

        // The water sockets, only when the room declares any, so the digest
        // of every room without them is what it was before them.
        if (room.WaterSockets.Count > 0)
        {
            foreach ((string socket, RoomWaterSocket water) in room.WaterSockets.OrderBy(w => w.Key, StringComparer.Ordinal))
            {
                fold.Text("water/" + socket);
                fold.Float(water.Level);
                fold.Text(water.Material);
            }
        }

        return fold.Finish();
    }

    /// <summary>The digest of the library-wide inputs: options, navigation, name keys.</summary>
    /// <param name="inputs">The inputs.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    /// <remarks>
    /// The option records go through <see cref="OptionsDigest"/>, the fold
    /// every other stage uses, so a switch added to <see cref="VbspOptions"/>
    /// joins the key without anyone remembering to add it here; only the two
    /// logging switches are cleared first (<see cref="RoomCacheInputs"/>
    /// says why). The name keys fold sorted, since they are a set.
    /// </remarks>
    public static string OptionsDigestOf(RoomCacheInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        using Folder fold = new();
        fold.Text(RoomOptionsDigest.Of(inputs.Options with { Verbose = false, VerboseEntities = false }));
        fold.Text(inputs.Nav is null ? "-" : RoomOptionsDigest.Of(inputs.Nav));
        fold.Text(RoomOptionsDigest.Of(inputs.PackOptions));
        if (inputs.NameKeys is null)
        {
            fold.Int(-1);
        }
        else
        {
            List<string> keys = [.. inputs.NameKeys];
            keys.Sort(StringComparer.Ordinal);
            fold.Int(keys.Count);
            foreach (string key in keys)
            {
                fold.Text(key);
            }
        }

        if (inputs.Lighting is { } lighting)
        {
            fold.Text(lighting.Describe());
        }

        return fold.Finish();
    }

    /// <summary>A length-prefixed SHA-256 fold, so no two part lists hash alike.</summary>
    private sealed class Folder : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Bytes(ReadOnlySpan<byte> data)
        {
            Long(data.Length);
            _hash.AppendData(data);
        }

        public void Text(string text) => Bytes(Encoding.UTF8.GetBytes(text));

        public void Int(int value) => Long(value);

        public void Float(float value) => Long(BitConverter.SingleToInt32Bits(value));

        public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();

        private void Long(long value)
        {
            Span<byte> word = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(word, value);
            _hash.AppendData(word);
        }
    }
}

/// <summary>
/// <see cref="OptionsDigest.Of"/> for the room key's option records, with
/// their accessors rooted for the trimmer.
/// </summary>
/// <remarks>
/// The fold reads properties by reflection, and a property the trimmer
/// stripped would drop out of the key silently (the fold fails loudly on a
/// type it reads nothing from, but not on one it reads a little less of).
/// So every record the room key folds is named here, the same way
/// <see cref="OptionsDigest.Of"/> names the collision cache's.
/// </remarks>
internal static class RoomOptionsDigest
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods, typeof(VbspOptions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods, typeof(FormatOptions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods, typeof(BspBlockGrid))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods, typeof(ComplianceOptions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods, typeof(NavSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods, typeof(NavAgentSpec))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods, typeof(RoomNavPackOptions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods, typeof(NavCompression))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods, typeof(VradOptions))]
    public static string Of(object options) => OptionsDigest.Of(options);
}

/// <summary>A room served from the cache: its finished pack item and what <c>ssmap room</c> reports of it.</summary>
/// <param name="Item">The room's pack item, section for section the bytes its compile wrote.</param>
/// <param name="ClusterCount">The room's vis cluster count, as its compile reported it.</param>
/// <param name="NameWarnings">What the naming rule warned of when the room compiled.</param>
/// <param name="NavWarnings">
/// What the room's navigation build warned of when the room compiled (a prop
/// whose model the content lacked), so a reused room prints the lines its
/// compile printed.
/// </param>
public sealed record RoomCacheHit(
    RoomPackItem Item, int ClusterCount, IReadOnlyList<string> NameWarnings, IReadOnlyList<string> NavWarnings);

/// <summary>What <see cref="RoomCompileCache.CommitAsync"/> did.</summary>
/// <param name="RowsStored">Room rows written this run.</param>
/// <param name="BytesStored">Section bytes those rows hold (before blob dedup).</param>
/// <param name="Gc">The collection that followed the commit, or null when none ran.</param>
/// <param name="GcFailure">Why the collection failed, or null; a failed collection leaves the store as it was.</param>
public sealed record RoomCacheCommit(int RowsStored, long BytesStored, CacheGcResult? Gc, string? GcFailure);

/// <summary>
/// One room-library run's view of the compile cache: serves unchanged rooms'
/// finished pack sections, and stores the rooms the run compiled.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a row holds.</b> A room's pack sections exactly as the pack
/// writer takes them (<see cref="RoomPackItem"/>: its <c>ROOM</c> container,
/// then <c>ECNT</c>, <c>LNKA</c>, the per-turn <c>GEO</c>, <c>COL</c>,
/// <c>ENT</c>, <c>NAM</c> and <c>NVR</c> sections), one content-addressed
/// blob per section, plus a small <c>meta</c> blob with the section tags in
/// order, the cluster count, and the naming and navigation warnings that
/// <c>ssmap room</c> prints. A hit rebuilds the pack item from those bytes
/// and nothing else, so the pack a run with hits writes is byte for byte the
/// pack a clean run writes: the sections are copied, not recomputed, and
/// the pack's own id (<see cref="RoomCompileIds.PackId"/>) is derived from
/// the library's bytes and the options each run, never from the rooms, so a
/// hit cannot change it. Link speed decides over disk size: the sections
/// are stored as the pack stores them (compressed only when the pack is),
/// so a hit is a copy with no decode.
/// </para>
/// <para>
/// <b>Game content.</b> The rooms that compile read the game's content
/// through <see cref="Content"/>, a <see cref="RecordingContentFileSystem"/>
/// that notes every file read (with its hash), found, or looked for and
/// missed. The rooms of one run share one material store
/// (<see cref="Bsp.SharedMaterialFacts"/>), which reads each material once
/// for all of them, so which room a read belongs to is not known; every row
/// the run stores therefore carries the run's whole set. That is a superset
/// of what each room read, so it can only turn a would-be hit into a miss,
/// never the reverse. A lookup re-checks a row's set against the content as
/// it is now: every read file still hashes the same, every found file is
/// still found, every missed file is still missing (a file added to an
/// earlier mount is exactly the change a miss guards against). The check
/// reads the live content, not the recorder, and remembers each path's
/// answer for the run, so a library's rooms that share a kit check each file
/// once.
/// </para>
/// <para>
/// <b>Nothing is staged until the run is done.</b> Rows the run makes are
/// held in memory with the items the pack needs anyway, and
/// <see cref="CommitAsync"/> stages and commits them in one step, after the
/// host has written the pack. A run that is cancelled or fails before that
/// stages nothing, so it leaves no row behind, and one that fails inside the
/// commit discards what it staged: the store's commit is atomic, so the
/// next run sees the store as it was or with every row of this one.
/// </para>
/// <para>
/// <b>Concurrent runs.</b> Two runs on one store file each open their own
/// store object (<c>ssmap room</c> does; a service should too): the store
/// file's transactions keep their commits apart, a row two runs both store
/// holds the same bytes whoever wins, and a blob is keyed by its content. A
/// row whose blob another run's collection removed reads as a miss. Two
/// runs must not share one store object: its staging area is the object's,
/// and one run's commit would publish or discard the other's rows.
/// </para>
/// <para>
/// <b>Nothing outlives the run.</b> The object holds the run's pending rows,
/// the hit rows to renew and the content check's answers, and
/// <see cref="Dispose"/> drops all of them and ends the run's lease on the
/// store (<see cref="ICacheStore.BeginRun"/>). The store is the host's.
/// </para>
/// </remarks>
public sealed class RoomCompileCache : IDisposable
{
    private const string MetaRole = "meta";
    private const int MetaRevision = 2;
    private const string ResolvedMarker = "resolved";

    private readonly ICacheStore _store;
    private readonly CachePolicy _policy;
    private readonly RoomCacheInputs _inputs;
    private readonly IContentFileSystem _live;
    private readonly RecordingContentFileSystem _recording;
    private readonly TimeProvider _time;
    private readonly IDisposable _lease;
    private readonly Lock _gate = new();
    private readonly List<Pending> _pending = [];
    private readonly List<CacheRecord> _renew = [];
    private readonly ConcurrentDictionary<string, Task<string?>> _hashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<bool>> _found = new(StringComparer.Ordinal);
    private int _hits;
    private int _misses;

    /// <summary>Opens the run's view of a store.</summary>
    /// <param name="store">The open store; the host's, left open.</param>
    /// <param name="policy">The posture: whether hits are read and rows written.</param>
    /// <param name="inputs">The library-wide inputs every room's key folds.</param>
    /// <param name="content">The game content, as mounted.</param>
    /// <param name="time">The clock the run's generation stamp is read from; the system clock when null.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public RoomCompileCache(
        ICacheStore store, CachePolicy policy, RoomCacheInputs inputs, IContentFileSystem content, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(content);
        _store = store;
        _policy = policy;
        _inputs = inputs;
        _live = content;
        _recording = new RecordingContentFileSystem(content);
        _time = time ?? TimeProvider.System;
        RunStamp = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _lease = store.BeginRun();
    }

    /// <summary>
    /// The content the rooms that miss compile through: the mounted content,
    /// with every read, find and miss recorded for the rows they store.
    /// </summary>
    public IContentFileSystem Content => _recording;

    /// <summary>The run's generation stamp (Unix milliseconds) every row it stores or renews carries.</summary>
    public long RunStamp { get; }

    /// <summary>How many rooms were served from the store.</summary>
    public int Hits => Volatile.Read(ref _hits);

    /// <summary>How many rooms were looked up and not served.</summary>
    public int Misses => Volatile.Read(ref _misses);

    /// <summary>How many compiled rooms wait for <see cref="CommitAsync"/>.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>A room's finished sections from the store, or null when it must compile.</summary>
    /// <param name="room">The room.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The hit, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="room"/> is null.</exception>
    /// <remarks>
    /// A row that is not quite this key's (another stage, tool, part list or
    /// tag list), is missing a blob, fails the blob's hash, has a
    /// <c>meta</c> blob this build does not read, or whose game content has
    /// changed, is a miss: a dropped row costs a compile, never a wrong room.
    /// </remarks>
    public async ValueTask<RoomCacheHit?> TryGetAsync(LibraryRoom room, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        RoomCacheHit? hit = null;
        if (_policy.Reads && _store.IsUsable)
        {
            CacheKey key = RoomCacheKey.Of(room, _inputs);
            CacheRecord? record = await _store.LookupAsync(key.Digest, cancellationToken).ConfigureAwait(false);
            if (record is not null && Matches(record, key)
                && await ContentUnchangedAsync(record, cancellationToken).ConfigureAwait(false))
            {
                hit = await ReadAsync(room, record, cancellationToken).ConfigureAwait(false);
                if (hit is not null)
                {
                    lock (_gate)
                    {
                        _renew.Add(record);
                    }
                }
            }
        }

        Interlocked.Increment(ref hit is null ? ref _misses : ref _hits);
        return hit;
    }

    /// <summary>Holds a compiled room's pack item for <see cref="CommitAsync"/>.</summary>
    /// <param name="room">The room, as the library split gave it.</param>
    /// <param name="item">The pack item written for it.</param>
    /// <param name="clusterCount">Its vis cluster count.</param>
    /// <param name="nameWarnings">What the naming rule warned of.</param>
    /// <param name="navWarnings">What the room's navigation build warned of; empty when the library builds none.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void Add(
        LibraryRoom room, RoomPackItem item, int clusterCount, IReadOnlyList<string> nameWarnings, IReadOnlyList<string> navWarnings)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(nameWarnings);
        ArgumentNullException.ThrowIfNull(navWarnings);
        lock (_gate)
        {
            _pending.Add(new Pending(room, item, clusterCount, [.. nameWarnings], [.. navWarnings]));
        }
    }

    /// <summary>
    /// Stages every room this run compiled, renews the rows it served, and
    /// commits them in one transaction; then runs the store's collection.
    /// </summary>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>What was stored and collected.</returns>
    /// <exception cref="OperationCanceledException">The token fired; nothing this run staged was committed.</exception>
    /// <remarks>
    /// Call once the pack is written. Any exception from the store leaves it
    /// as it was: what this run staged is discarded before the exception
    /// leaves. A collection that fails is reported in the result, not thrown:
    /// the rows are already committed and the store is intact.
    /// </remarks>
    public async ValueTask<RoomCacheCommit> CommitAsync(CancellationToken cancellationToken)
    {
        List<Pending> pending;
        List<CacheRecord> renew;
        lock (_gate)
        {
            pending = [.. _pending];
            renew = [.. _renew];
            _pending.Clear();
            _renew.Clear();
        }

        if (!_policy.Writes || !_store.IsUsable)
        {
            return new RoomCacheCommit(0, 0, null, null);
        }

        int rows = 0;
        long bytes = 0;
        try
        {
            IReadOnlyList<(string Path, string? ContentHash)> dependencies = Dependencies();
            foreach (Pending room in pending)
            {
                long stored = await StageAsync(room, dependencies, cancellationToken).ConfigureAwait(false);
                if (stored >= 0)
                {
                    rows++;
                    bytes += stored;
                }
            }

            foreach (CacheRecord record in renew)
            {
                await CacheBlobReader.RenewAsync(_store, _policy, record, RunStamp, cancellationToken).ConfigureAwait(false);
            }

            await _store.RecordGenerationAsync(RunStamp.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            await _store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _store.DiscardStaged();
            throw;
        }

        CacheGcResult? gc = null;
        string? failure = null;
        try
        {
            gc = await CacheCollector.CollectAsync(_store, _policy, _time, ownRuns: 1, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception.Message;
            _store.DiscardStaged();
        }

        return new RoomCacheCommit(rows, bytes, gc, failure);
    }

    /// <summary>Drops the run's pending rows, renewals and content answers, and ends its lease on the store.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _pending.Clear();
            _renew.Clear();
        }

        _hashes.Clear();
        _found.Clear();
        _lease.Dispose();
    }

    /// <summary>The run's recorded content, as row dependencies: read files by hash, found ones by a marker, misses as null.</summary>
    internal IReadOnlyList<(string Path, string? ContentHash)> Dependencies() =>
    [
        .. _recording.Recorder.Snapshot().Select(d => (d.Path.Value, d.Kind switch
        {
            DependencyKind.Read => d.ContentHash,
            DependencyKind.Resolved => ResolvedMarker,
            _ => null,
        })),
    ];

    private static bool Matches(CacheRecord record, CacheKey key) =>
        record.Stage == key.Stage
        && record.ToolId == key.ToolId
        && record.Key == key.Digest
        && record.Parts.SequenceEqual(key.Parts)
        && record.ContextTags.SequenceEqual(key.ContextTags);

    private async ValueTask<bool> ContentUnchangedAsync(CacheRecord record, CancellationToken cancellationToken)
    {
        foreach ((string path, string? recorded) in record.Dependencies)
        {
            bool same = recorded switch
            {
                null => !await FoundAsync(path, cancellationToken).ConfigureAwait(false),
                ResolvedMarker => await FoundAsync(path, cancellationToken).ConfigureAwait(false),
                _ => await HashAsync(path, cancellationToken).ConfigureAwait(false) == recorded,
            };
            if (!same)
            {
                return false;
            }
        }

        return true;
    }

    // Each answer is asked once per run, whoever asks first; a failed or
    // cancelled ask is dropped so the next asker tries again.
    private async ValueTask<bool> FoundAsync(string path, CancellationToken cancellationToken)
    {
        Task<bool> task = _found.GetOrAdd(path, p => FindAsync(p, cancellationToken));
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            _found.TryRemove(new KeyValuePair<string, Task<bool>>(path, task));
            throw;
        }
    }

    private async Task<bool> FindAsync(string path, CancellationToken cancellationToken) =>
        VPath.TryCreate(path, out VPath at)
        && await _live.ResolveAsync(at, cancellationToken).ConfigureAwait(false) is not null;

    private async ValueTask<string?> HashAsync(string path, CancellationToken cancellationToken)
    {
        Task<string?> task = _hashes.GetOrAdd(path, p => ReadHashAsync(p, cancellationToken));
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            _hashes.TryRemove(new KeyValuePair<string, Task<string?>>(path, task));
            throw;
        }
    }

    private async Task<string?> ReadHashAsync(string path, CancellationToken cancellationToken)
    {
        if (!VPath.TryCreate(path, out VPath at))
        {
            return null;
        }

        using IMemoryOwner<byte>? owner = await _live.ReadAsync(at, cancellationToken).ConfigureAwait(false);
        return owner is null ? null : DependencyRecorder.Hash(owner.Memory.Span);
    }

    private async ValueTask<RoomCacheHit?> ReadAsync(LibraryRoom room, CacheRecord record, CancellationToken cancellationToken)
    {
        if (!record.Blobs.TryGetValue(MetaRole, out string? metaKey)
            || await CacheBlobReader.ReadAsync(_store, _policy, metaKey, cancellationToken).ConfigureAwait(false) is not { } metaBytes
            || Meta.Read(metaBytes) is not { } meta)
        {
            return null;
        }

        List<RoomPackSectionData> sections = [];
        for (int i = 0; i < meta.Tags.Count; i++)
        {
            if (!record.Blobs.TryGetValue(SectionRole(i), out string? blobKey)
                || await CacheBlobReader.ReadAsync(_store, _policy, blobKey, cancellationToken).ConfigureAwait(false) is not { } data)
            {
                return null;
            }

            sections.Add(new RoomPackSectionData(meta.Tags[i], data));
        }

        if (sections.Count == 0 || sections[0].Tag != RoomPack.RoomSection)
        {
            return null;
        }

        RoomPackItem item = new(room.Definition.Name, sections[0].Bytes) { Extra = sections[1..] };
        return new RoomCacheHit(item, meta.ClusterCount, meta.Warnings, meta.NavWarnings);
    }

    /// <summary>Stages one room's row and blobs; the section bytes stored, or -1 when a section is over the blob cap.</summary>
    private async ValueTask<long> StageAsync(
        Pending room, IReadOnlyList<(string Path, string? ContentHash)> dependencies, CancellationToken cancellationToken)
    {
        List<RoomPackSectionData> sections = [new(RoomPack.RoomSection, room.Item.Room), .. room.Item.Extra];
        byte[] meta = Meta.Write(new Meta([.. sections.Select(s => s.Tag)], room.ClusterCount, room.Warnings, room.NavWarnings));
        if (meta.LongLength > _policy.MaxBlobBytes || sections.Any(s => s.Bytes.Length > _policy.MaxBlobBytes))
        {
            return -1;
        }

        Dictionary<string, string> blobs = new(StringComparer.Ordinal)
        {
            [MetaRole] = await PutBlobAsync(meta, cancellationToken).ConfigureAwait(false),
        };
        long bytes = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            blobs[SectionRole(i)] = await PutBlobAsync(sections[i].Bytes, cancellationToken).ConfigureAwait(false);
            bytes += sections[i].Bytes.Length;
        }

        CacheKey key = RoomCacheKey.Of(room.Room, _inputs);
        await _store.PutAsync(
            new CacheRecord(key.Digest, key.Stage, key.ToolId, key.ContextTags, key.Parts, blobs, dependencies, 0, RunStamp),
            cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private async ValueTask<string> PutBlobAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        string blobKey = CacheKey.HashBytes(data.Span);
        if (!await _store.HasBlobAsync(blobKey, cancellationToken).ConfigureAwait(false))
        {
            await _store.PutBlobAsync(blobKey, data, ToolIdentity.Current, RunStamp, cancellationToken).ConfigureAwait(false);
        }

        return blobKey;
    }

    private static string SectionRole(int index) => "s" + index.ToString("D2", CultureInfo.InvariantCulture);

    private sealed record Pending(
        LibraryRoom Room, RoomPackItem Item, int ClusterCount, IReadOnlyList<string> Warnings, IReadOnlyList<string> NavWarnings);

    /// <summary>
    /// A row's <c>meta</c> blob: an <c>int32</c> revision, the cluster count,
    /// the section count and each section's four tag bytes, then the naming
    /// warnings and the navigation warnings, each list as its count and each
    /// warning (an <c>int32</c> byte length and UTF-8); all integers
    /// big-endian, as in the pack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the warnings are here at all.</b> They are what the room's
    /// compile said, not what it wrote: no pack section holds them, so a hit,
    /// which rebuilds the item from the stored sections and compiles nothing,
    /// would otherwise print a quieter log than the clean run it stands in
    /// for. Keeping them beside the sections makes a reused room print the
    /// lines its compile printed, in the same order.
    /// </para>
    /// <para>
    /// <b>Revisions.</b> Revision 1 carried the naming warnings only.
    /// Revision 2 appends the navigation warnings (a prop whose model the
    /// content lacks, left out of the obstacles). A reader refuses every
    /// revision but its own rather than reading an old blob with an empty
    /// navigation list: an old row's room may well have had navigation
    /// warnings, and serving it would silently drop them from the log, so
    /// an old row is a miss and the room compiles once more under the new
    /// layout. The cost is one compile per room the first run after an
    /// upgrade, which a new build pays anyway since the tool identity is
    /// part of the key.
    /// </para>
    /// </remarks>
    internal sealed record Meta(
        IReadOnlyList<string> Tags, int ClusterCount, IReadOnlyList<string> Warnings, IReadOnlyList<string> NavWarnings)
    {
        public static byte[] Write(Meta meta)
        {
            using MemoryStream w = new();
            Int(w, MetaRevision);
            Int(w, meta.ClusterCount);
            Int(w, meta.Tags.Count);
            foreach (string tag in meta.Tags)
            {
                w.Write(Encoding.ASCII.GetBytes(tag));
            }

            Strings(w, meta.Warnings);
            Strings(w, meta.NavWarnings);
            return w.ToArray();
        }

        /// <summary>The meta, or null for bytes of another revision or cut short.</summary>
        public static Meta? Read(ReadOnlySpan<byte> bytes)
        {
            int at = 0;
            if (!TryInt(bytes, ref at, out int revision) || revision != MetaRevision
                || !TryInt(bytes, ref at, out int clusters)
                || !TryInt(bytes, ref at, out int count) || count < 0 || count > RoomPack.MaxSections
                || bytes.Length - at < count * 4L)
            {
                return null;
            }

            List<string> tags = [];
            for (int i = 0; i < count; i++, at += 4)
            {
                tags.Add(Encoding.ASCII.GetString(bytes.Slice(at, 4)));
            }

            return TryStrings(bytes, ref at, out List<string>? text)
                && TryStrings(bytes, ref at, out List<string>? nav)
                && at == bytes.Length
                ? new Meta(tags, clusters, text, nav)
                : null;
        }

        private static void Strings(MemoryStream w, IReadOnlyList<string> list)
        {
            Int(w, list.Count);
            foreach (string item in list)
            {
                byte[] text = Encoding.UTF8.GetBytes(item);
                Int(w, text.Length);
                w.Write(text);
            }
        }

        private static bool TryStrings(ReadOnlySpan<byte> bytes, ref int at, [NotNullWhen(true)] out List<string>? list)
        {
            list = null;
            if (!TryInt(bytes, ref at, out int count) || count < 0)
            {
                return false;
            }

            List<string> read = [];
            for (int i = 0; i < count; i++)
            {
                if (!TryInt(bytes, ref at, out int length) || length < 0 || bytes.Length - at < length)
                {
                    return false;
                }

                read.Add(Encoding.UTF8.GetString(bytes.Slice(at, length)));
                at += length;
            }

            list = read;
            return true;
        }

        private static void Int(MemoryStream w, int value)
        {
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(word, value);
            w.Write(word);
        }

        private static bool TryInt(ReadOnlySpan<byte> bytes, ref int at, out int value)
        {
            if (bytes.Length - at < 4)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadInt32BigEndian(bytes[at..]);
            at += 4;
            return true;
        }
    }
}
