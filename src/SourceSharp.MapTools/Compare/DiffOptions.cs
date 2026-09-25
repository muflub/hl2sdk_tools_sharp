namespace SourceSharp.MapTools.Compare;

/// <summary>
/// What <see cref="BspDiff"/> is allowed to judge, and how finely it looks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every threshold here is null by default, and null means "report the
/// number, judge nothing".</b> The comparison rules are explicit that a tolerance
/// is frozen from the first measurement in the phase that owns the lump, and
/// this instrument is built before those phases -- so it must not ship a
/// tolerance of its own. A threshold that has not been set comes back as
/// <see cref="ThresholdVerdict.Unset"/>, which is a different report line from
/// "within limits", not a silent zero.
/// </para>
/// <para>
/// Phase 0 is the reason this matters
/// more than usual for the lighting lumps. Stock vrad is bit-exact at
/// <c>-threads 1</c> and differs on EVERY run above one thread, where 2.90 % of
/// 2.4 M samples move, the p99 envelope is 0.000245 and the maximum is
/// 3.396078 linear -- a factor of 5.5 on one real sample, exponent byte and
/// all. So there is no per-sample maximum that can be set in the threaded
/// regime, and the honest default is to set none.
/// </para>
/// </remarks>
public sealed class DiffOptions
{
    /// <summary>
    /// Options that measure everything and judge nothing. This is the default.
    /// </summary>
    /// <remarks>
    /// A computed property rather than a cached instance: a static field
    /// holding a reference type is the mutable static state the design rules
    /// bans, and <c>LibraryRuleTests</c> measures that on the built assembly.
    /// </remarks>
    public static DiffOptions ReportOnly => new();

    /// <summary>
    /// How close two floats must be to canonicalise to the same key, or null to
    /// require the same value exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies to vertex positions in a face's ring and to plane normals and
    /// distances. Null -- the default -- is not "epsilon zero" in disguise: it
    /// selects a different code path that formats the float itself rather than
    /// a quantised multiple, so a caller who has not measured an epsilon gets an
    /// exact key and a report that says the epsilon is unset.
    /// </para>
    /// <para>
    /// Quantisation is snapping to the nearest multiple, so two values either
    /// side of a snap boundary still differ however small the epsilon. That is
    /// inherent to canonicalising a float into a hash key and is why this is a
    /// caller's decision rather than a default.
    /// </para>
    /// </remarks>
    public float? GeometryEpsilon { get; init; }

    /// <summary>
    /// How many individual items each set or element difference lists before it
    /// stops listing and only counts.
    /// </summary>
    /// <remarks>
    /// Not a threshold: it bounds the size of the REPORT, never the verdict.
    /// The full counts are always carried, so "what is in one and not the other"
    /// is answered by <see cref="SetDifference.OnlyInACount"/> even when the
    /// samples are truncated.
    /// </remarks>
    public int MaxReportedItems { get; init; } = 16;

    /// <summary>The largest per-sample linear error accepted, or null to judge nothing.</summary>
    public double? LightmapMaxLinear { get; init; }

    /// <summary>The largest mean linear error accepted, or null to judge nothing.</summary>
    public double? LightmapMeanLinear { get; init; }

    /// <summary>The largest 99th-percentile linear error accepted, or null to judge nothing.</summary>
    public double? LightmapP99Linear { get; init; }

    /// <summary>The largest 99.9th-percentile linear error accepted, or null to judge nothing.</summary>
    public double? LightmapP999Linear { get; init; }

    /// <summary>The largest 99.99th-percentile linear error accepted, or null to judge nothing.</summary>
    public double? LightmapP9999Linear { get; init; }

    /// <summary>
    /// The largest fraction of samples allowed to differ at all, in 0..1, or
    /// null to judge nothing.
    /// </summary>
    public double? LightmapDifferingFraction { get; init; }

    /// <summary>
    /// The largest number of visibility bits allowed to differ, or null to
    /// judge nothing.
    /// </summary>
    public long? VisibilityDifferingBits { get; init; }

    /// <summary>
    /// True to require B's PVS to be a superset of A's, false to require it not
    /// to be, or null to judge nothing.
    /// </summary>
    /// <remarks>
    /// A conservative superset is a legitimate vvis result rather than an error
    /// -- a cluster that sees more than it needs to renders too much and is
    /// never wrong -- so this is reported as a property by default and only
    /// becomes a verdict when a caller asks for one.
    /// </remarks>
    public bool? RequireVisibilitySupersetOfA { get; init; }

    /// <summary>
    /// The largest number of set or element differences a comparison may report
    /// before it stops enumerating, or null for no limit.
    /// </summary>
    /// <remarks>
    /// A guard against a pathological pair of maps producing a multi-million
    /// entry report, not a threshold. Counts stay complete when it bites.
    /// </remarks>
    public int? MaxEnumeratedDifferences { get; init; }
}
