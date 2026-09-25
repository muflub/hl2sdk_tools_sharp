namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The switches the cache honours, all defaults matching "a cache you can
/// leave switched on" (plan_maptools.md 10a).
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

    /// <summary>The store size cap that triggers the automatic GC at 90 %.</summary>
    public const long DefaultMaxStoreBytes = 1L << 30;

    /// <summary>Posture.</summary>
    public CacheMode Mode { get; init; } = CacheMode.ReadWrite;

    /// <inheritdoc cref="MaxBlobBytes"/>
    public long MaxBlobBytes { get; init; } = DefaultMaxBlobBytes;

    /// <inheritdoc cref="MaxStoreBytes"/>
    public long MaxStoreBytes { get; init; } = DefaultMaxStoreBytes;

    /// <inheritdoc cref="MaxAgeDays"/>
    public int MaxAgeDays { get; init; } = 30;

    /// <inheritdoc cref="GenerationsKept"/>
    public int GenerationsKept { get; init; } = 2;

    /// <inheritdoc cref="UseMtimeHint"/>
    public bool UseMtimeHint { get; init; }

    /// <inheritdoc cref="MtimeHintGrace"/>
    public TimeSpan MtimeHintGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc cref="VerifyOnHit"/>
    public bool VerifyOnHit { get; init; } = true;

    /// <inheritdoc cref="GcOnCommit"/>
    public bool GcOnCommit { get; init; } = true;

    /// <inheritdoc cref="GcBudget"/>
    public TimeSpan GcBudget { get; init; } = TimeSpan.FromSeconds(1);

    /// <inheritdoc cref="Vacuum"/>
    public CacheVacuum Vacuum { get; init; } = CacheVacuum.OnGc;

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

    /// <summary>Every commit that reclaimed anything (the plan's "aggressive" option).</summary>
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
