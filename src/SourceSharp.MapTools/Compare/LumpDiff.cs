using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// One lump slot's verdict.
/// </summary>
/// <remarks>
/// Every one of the 64 slots gets one of these, whatever
/// <see cref="Kind"/> says, so the report is total and a lump nobody compares
/// is visibly a lump nobody compares.
/// </remarks>
public sealed class LumpDiff
{
    internal LumpDiff(
        int lumpIndex,
        DiffKind kind,
        int lengthA,
        int lengthB,
        bool bytesIdentical,
        bool identical,
        bool comparedAsProperty,
        string? note,
        ImmutableArray<DiffDifference> differences,
        int differenceCount,
        SetDifference? set,
        LightmapStatistics? lightmap,
        VisibilityDifference? visibility,
        ImmutableArray<ThresholdJudgement> judgements)
    {
        LumpIndex = lumpIndex;
        Kind = kind;
        LengthA = lengthA;
        LengthB = lengthB;
        BytesIdentical = bytesIdentical;
        Identical = identical;
        ComparedAsProperty = comparedAsProperty;
        Note = note;
        Differences = differences;
        DifferenceCount = differenceCount;
        Set = set;
        Lightmap = lightmap;
        Visibility = visibility;
        Judgements = judgements;
    }

    /// <summary>The slot number, 0..63.</summary>
    public int LumpIndex { get; }

    /// <summary>
    /// The named lump, or null for slots 61..63, which <c>bspfile.h</c> never
    /// assigned and which therefore have no name to give.
    /// </summary>
    public BspLump? Lump =>
        Enum.IsDefined(typeof(BspLump), LumpIndex) ? (BspLump)LumpIndex : null;

    /// <summary>The lump's name, or its slot number when it has none.</summary>
    public string Name =>
        Lump is { } lump
            ? lump.ToString()
            : LumpIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>How this lump was compared.</summary>
    public DiffKind Kind { get; }

    /// <summary>The lump's length in A.</summary>
    public int LengthA { get; }

    /// <summary>The lump's length in B.</summary>
    public int LengthB { get; }

    /// <summary>
    /// Whether the two lumps hold exactly the same bytes.
    /// </summary>
    /// <remarks>
    /// Reported for every lump regardless of <see cref="Kind"/>, and it is what
    /// makes the canonical kind provable: a pair of maps whose PLANES compare
    /// IDENTICAL as a set while this says false is the whole reason that kind
    /// exists.
    /// </remarks>
    public bool BytesIdentical { get; }

    /// <summary>
    /// Whether this lump's comparison found no difference, by the rules of
    /// <see cref="Kind"/>.
    /// </summary>
    public bool Identical { get; }

    /// <summary>
    /// Whether this lump was compared on a PROPERTY -- record counts, lengths
    /// -- instead of element by element.
    /// </summary>
    /// <remarks>
    /// Set for the leaf-ambient lumps when the two record counts disagree.
    /// plan_maptools_lane_notes.md spike 0d measured stock vrad producing
    /// 9788 / 9788 / 9790 / 9790 / 9789 leaf-ambient records for the SAME map,
    /// so where the counts differ an element-wise comparison is misaligned
    /// garbage. This flag is how the report says it declined rather than
    /// quietly aligning.
    /// </remarks>
    public bool ComparedAsProperty { get; }

    /// <summary>Why this lump was compared the way it was, when that needs saying.</summary>
    public string? Note { get; }

    /// <summary>
    /// The individual differences, bounded by
    /// <see cref="DiffOptions.MaxReportedItems"/>.
    /// </summary>
    public ImmutableArray<DiffDifference> Differences { get; }

    /// <summary>
    /// How many differences were found, which can exceed
    /// <see cref="Differences"/>'s length.
    /// </summary>
    public int DifferenceCount { get; }

    /// <summary>The multiset comparison, for <see cref="DiffKind.CanonicalSet"/> lumps.</summary>
    public SetDifference? Set { get; }

    /// <summary>The error histogram, for the lighting lumps.</summary>
    public LightmapStatistics? Lightmap { get; }

    /// <summary>The bit comparison, for LUMP_VISIBILITY.</summary>
    public VisibilityDifference? Visibility { get; }

    /// <summary>Every number this lump measured, with the caller's limits applied.</summary>
    public ImmutableArray<ThresholdJudgement> Judgements { get; }

    /// <summary>Whether any judgement on this lump exceeded a limit the caller set.</summary>
    public bool ExceedsThreshold =>
        Judgements.Any(j => j.Verdict == ThresholdVerdict.Exceeded);

    /// <summary>This lump's verdict as report text.</summary>
    /// <returns>One headline line, then a line per difference and judgement.</returns>
    public string ToText()
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"[{LumpIndex,2}] {Name} ({Kind})");
        text.Append(Identical ? " identical" : " DIFFERS");
        if (LengthA != LengthB)
        {
            text.Append(CultureInfo.InvariantCulture, $" lengths {LengthA} vs {LengthB}");
        }

        if (ComparedAsProperty)
        {
            text.Append(" [compared as a property, NOT element-wise]");
        }

        text.AppendLine();

        if (Note is not null)
        {
            text.Append("       ").AppendLine(Note);
        }

        if (Set is { } set && !set.Identical)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"       only in A: {set.OnlyInACount}, only in B: {set.OnlyInBCount}, shared: {set.CommonCount}");
            text.AppendLine();
            foreach (string key in set.OnlyInA)
            {
                text.Append("       A only: ").AppendLine(key);
            }

            foreach (string key in set.OnlyInB)
            {
                text.Append("       B only: ").AppendLine(key);
            }
        }

        if (Lightmap is { } stats)
        {
            text.Append("       ").AppendLine(stats.ToText());
            foreach (FaceLightError face in stats.WorstFaces)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"       face {face.Face}: max {face.MaxLinear:G9}, mean {face.MeanLinear:G9}, "
                    + $"{face.DifferingSampleCount}/{face.SampleCount} differ");
                text.AppendLine();
            }
        }

        if (Visibility is { } vis)
        {
            foreach (PvsDifference? column in new[] { vis.Pvs, vis.Pas })
            {
                if (column is null)
                {
                    continue;
                }

                text.Append(CultureInfo.InvariantCulture,
                    $"       {column.Column}: {column.DifferingBits} bits differ in "
                    + $"{column.DifferingClusters}/{column.ClusterCount} clusters "
                    + $"(A-only {column.OnlyInABits}, B-only {column.OnlyInBBits}; "
                    + $"B superset of A: {column.BContainsA})");
                text.AppendLine();
            }
        }

        foreach (DiffDifference difference in Differences)
        {
            text.Append("       ").AppendLine(difference.ToText());
        }

        if (DifferenceCount > Differences.Length)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"       ... and {DifferenceCount - Differences.Length} more");
            text.AppendLine();
        }

        foreach (ThresholdJudgement judgement in Judgements)
        {
            text.Append("       ").AppendLine(judgement.ToText());
        }

        return text.ToString();
    }
}
