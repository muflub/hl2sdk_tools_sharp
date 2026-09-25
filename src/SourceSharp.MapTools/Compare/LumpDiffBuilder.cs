using System.Collections.Immutable;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// Accumulates one lump's verdict while a comparer walks it.
/// </summary>
/// <remarks>
/// Internal and mutable, which is fine: it lives inside one call, is never
/// shared, and the <see cref="LumpDiff"/> it produces is immutable. The
/// alternative -- every comparer assembling ten constructor arguments by hand
/// -- is where a forgotten count comes from.
/// </remarks>
internal sealed class LumpDiffBuilder
{
    private readonly List<DiffDifference> _reported = [];
    private readonly List<ThresholdJudgement> _judgements = [];
    private readonly int _maxReported;
    private readonly int? _maxEnumerated;

    internal LumpDiffBuilder(int lumpIndex, DiffKind kind, int lengthA, int lengthB, DiffOptions options)
    {
        LumpIndex = lumpIndex;
        Kind = kind;
        LengthA = lengthA;
        LengthB = lengthB;
        _maxReported = Math.Max(0, options.MaxReportedItems);
        _maxEnumerated = options.MaxEnumeratedDifferences;
    }

    internal int LumpIndex { get; }

    internal DiffKind Kind { get; }

    internal int LengthA { get; }

    internal int LengthB { get; }

    internal int DifferenceCount { get; private set; }

    internal bool BytesIdentical { get; set; }

    internal bool ComparedAsProperty { get; set; }

    internal string? Note { get; set; }

    internal SetDifference? Set { get; set; }

    internal LightmapStatistics? Lightmap { get; set; }

    internal VisibilityDifference? Visibility { get; set; }

    /// <summary>
    /// Whether enumeration should stop: the caller capped it and the cap is
    /// reached. Counts already recorded stay complete.
    /// </summary>
    internal bool Saturated => _maxEnumerated is { } cap && DifferenceCount >= cap;

    internal void Add(string subject, string inA, string inB)
    {
        DifferenceCount++;
        if (_reported.Count < _maxReported)
        {
            _reported.Add(new DiffDifference(subject, inA, inB));
        }
    }

    /// <summary>
    /// Records that differences were found without naming them one by one --
    /// used where a set comparison already carries the detail.
    /// </summary>
    /// <param name="count">How many.</param>
    internal void AddCount(int count) => DifferenceCount += count;

    internal void Judge(string name, double measured, double? limit) =>
        _judgements.Add(ThresholdJudgement.Judge(name, measured, limit));

    internal void JudgeProperty(string name, bool holds, bool? required) =>
        _judgements.Add(ThresholdJudgement.JudgeProperty(name, holds, required));

    internal LumpDiff Build() => new(
        LumpIndex,
        Kind,
        LengthA,
        LengthB,
        BytesIdentical,
        DifferenceCount == 0,
        ComparedAsProperty,
        Note,
        [.. _reported],
        DifferenceCount,
        Set,
        Lightmap,
        Visibility,
        [.. _judgements]);
}
