//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The switches the cache honours, all defaults matching "a cache you can
/// Leave switched on".
/// </summary>
public sealed record CachePolicy
{
    /// <summary>Everything on, defaults everywhere — what <c>ssmap all</c> uses when a cache is configured.</summary>
    public static CachePolicy Default { get; } = new();

    /// <summary>Read hits, never write: the shared-reference posture.</summary>
    public static CachePolicy ReadOnly { get; } = new() { Mode = CacheMode.ReadOnly };

    /// <summary>Read hits only from rows produced in this process; nothing persists (the in-memory posture).</summary>
    public static CachePolicy Volatile { get; } = new() { Mode = CacheMode.Volatile };

    /// <summary>The 16 MB blob threshold named by the plan.</summary>
    public const long DefaultMaxBlobBytes = 16L * 1024 * 1024;

    /// <summary>The store size cap the GC keeps the store under, trimming it to 90 % of the cap.</summary>
    public const long DefaultMaxStoreBytes = 1L << 30;

    /// <summary>Posture.</summary>
    public CacheMode Mode { get; init; } = CacheMode.ReadWrite;

    /// <summary>
    /// The largest single blob a seam stages. The transfer set is cut into
    /// chunks of at most this size; a vvis product with a larger lump is not
    /// stored at all.
    /// </summary>
    public long MaxBlobBytes { get; init; } = DefaultMaxBlobBytes;

    /// <summary>
    /// The store size, in blob bytes, above which the GC after a compile's
    /// commit drops the oldest rows until the store is at 90 % of it; zero or
    /// less for no size limit.
    /// </summary>
    /// <remarks>
    /// Trimming to 90 % rather than to the cap itself leaves headroom, so a
    /// store sitting at its cap is not collected again on every commit. The
    /// size is what <see cref="ICacheStore.ReadStatsAsync"/> reports as
    /// <see cref="CacheStats.SizeBytes"/>; rows of the newest
    /// <see cref="GenerationsKept"/> generations are never dropped for size, so
    /// a store whose recent rows alone exceed the cap stays above it.
    /// </remarks>
    public long MaxStoreBytes { get; init; } = DefaultMaxStoreBytes;

    /// <summary>
    /// Rows no compile has made or read for longer than this many days are
    /// dropped by the GC whatever the store's size; zero or less keeps rows
    /// of any age.
    /// </summary>
    /// <remarks>
    /// A row's age is its <see cref="CacheRecord.CreatedAtMs"/>, which a hit
    /// renews (the seams re-stage a row they replay under the run's stamp),
    /// so this is "unused for", not "made before". Rows of the newest
    /// <see cref="GenerationsKept"/> generations are kept even when older.
    /// </remarks>
    public int MaxAgeDays { get; init; } = 30;

    /// <summary>
    /// How many of the newest compiles' generations the GC protects: a row
    /// made or read by one of them is never dropped, for age or for size.
    /// </summary>
    /// <remarks>
    /// Each compile that commits records its start stamp as a generation
    /// (<see cref="ICacheStore.RecordGenerationAsync"/>), and every row it
    /// stages or replays carries that stamp. The GC protects every row at or
    /// after the oldest of the kept generations, and removes the older
    /// generation records, so the generation table stays this long. Two keeps
    /// the edit loop's previous compile safe even when a compile of another
    /// map ran in between.
    /// </remarks>
    public int GenerationsKept { get; init; } = 2;

    /// <summary>
    /// Whether a replayed blob is re-hashed against its content key before
    /// it is used; on by default (<see cref="CacheBlobReader"/>).
    /// </summary>
    public bool VerifyOnHit { get; init; } = true;

    /// <summary>Whether a compile runs the GC after its final commit (<see cref="CacheCollector"/>).</summary>
    public bool GcOnCommit { get; init; } = true;

    /// <summary>
    /// How long the GC after a commit may run. It stops between rows once the
    /// budget is spent and commits what it has dropped so far; the next
    /// commit's GC carries on. Zero or less does no work at all.
    /// </summary>
    public TimeSpan GcBudget { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>When the store returns reclaimed space to the OS.</summary>
    public CacheVacuum Vacuum { get; init; } = CacheVacuum.OnGc;

    // There is no mtime switch. The key design rules a timestamp out of every
    // key and every re-hash (CacheKey's remarks: a restore that preserved a
    // modification time once faked a mutation proof), dependency rows record
    // content hashes only, with no size or time to compare a hint against,
    // and no stage reads a dependency it could skip. An "mtime hint" knob
    // with no reader promised a fast path that does not exist, so it was
    // removed rather than implemented against the rule.

    /// <summary>Whether hits may be served in this posture.</summary>
    public bool Reads => Mode is CacheMode.ReadWrite or CacheMode.ReadOnly or CacheMode.Volatile;

    /// <summary>Whether rows may be written in this posture.</summary>
    public bool Writes => Mode is CacheMode.ReadWrite or CacheMode.WriteOnly;
}

/// <summary>Store posture (plan 10a: read-only, write-only, read-write, volatile).</summary>
public enum CacheMode
{
    /// <summary>Hits allowed, writes allowed — the default.</summary>
    ReadWrite,

    /// <summary>Hits allowed, nothing written; a shared reference directory stays pristine.</summary>
    ReadOnly,

    /// <summary>Writes allowed, never hits (fills a cache another run will read; the cold half of the I5 gate).</summary>
    WriteOnly,

    /// <summary>Hits from rows this process produced; the store receives nothing.</summary>
    Volatile,
}

/// <summary>When the SQLite store returns reclaimed space to the OS.</summary>
public enum CacheVacuum
{
    /// <summary>Never (the SQLite default; space is reused inside the file).</summary>
    Never,

    /// <summary>After the automatic GC when it reclaimed anything — the default.</summary>
    OnGc,

    /// <summary>
    /// After every compile whose commit reclaimed anything: the GC's drops,
    /// or the transfer seam's eviction of the map's older set (the plan's
    /// "aggressive" option).
    /// </summary>
    EveryRun,
}

/// <summary>Store-wide counters: the report's size figures and the GC's inputs.</summary>
public sealed record CacheStats(
    int KeyCount,
    int BlobCount,
    long SizeBytes,
    long FileBytes,
    int GenerationCount,
    int ToolIdCount);
