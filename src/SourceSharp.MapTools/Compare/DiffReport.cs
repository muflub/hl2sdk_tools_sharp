using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// What <c>ssmap diff A.bsp B.bsp</c> found: one verdict per lump slot, plus
/// the header fields that sit outside every lump.
/// </summary>
public sealed class DiffReport
{
    internal DiffReport(
        ImmutableArray<LumpDiff> lumps,
        ImmutableArray<DiffDifference> header,
        DiffOptions options)
    {
        Lumps = lumps;
        Header = header;
        Options = options;
    }

    /// <summary>One entry per lump slot, in slot order, all 64 of them.</summary>
    public ImmutableArray<LumpDiff> Lumps { get; }

    /// <summary>
    /// Differences in the fields the file header carries outside the lump
    /// table: the BSP version and the map revision.
    /// </summary>
    public ImmutableArray<DiffDifference> Header { get; }

    /// <summary>The options the comparison ran under.</summary>
    public DiffOptions Options { get; }

    /// <summary>
    /// Whether every lump compared identical and the headers agree.
    /// </summary>
    /// <remarks>
    /// This is the semantic verdict, so two maps whose geometry is the same in a
    /// different index order are identical here and NOT identical under
    /// <see cref="BytesIdentical"/>. That gap is the point of
    /// <see cref="DiffKind.CanonicalSet"/>.
    /// </remarks>
    public bool Identical => Header.Length == 0 && Lumps.All(l => l.Identical);

    /// <summary>Whether the two files' lumps hold the same bytes, slot for slot.</summary>
    public bool BytesIdentical => Lumps.All(l => l.BytesIdentical);

    /// <summary>
    /// How many differences were found in total, header and every lump,
    /// counting those the report did not have room to list.
    /// </summary>
    public int DifferenceCount => Header.Length + Lumps.Sum(l => l.DifferenceCount);

    /// <summary>Every measured number from every lump, with the caller's limits applied.</summary>
    public ImmutableArray<ThresholdJudgement> Judgements =>
        [.. Lumps.SelectMany(l => l.Judgements)];

    /// <summary>Whether any judgement exceeded a limit the caller set.</summary>
    public bool ExceedsThreshold => Lumps.Any(l => l.ExceedsThreshold);

    /// <summary>The verdict for one named lump.</summary>
    /// <param name="lump">Which lump.</param>
    /// <returns>Its entry, which always exists.</returns>
    public LumpDiff For(BspLump lump) => Lumps[(int)lump];

    /// <summary>Every lump compared by one kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Those lumps' entries, in slot order.</returns>
    public IEnumerable<LumpDiff> OfKind(DiffKind kind) => Lumps.Where(l => l.Kind == kind);

    /// <summary>
    /// The lumps that differ, in slot order.
    /// </summary>
    /// <returns>Their entries.</returns>
    public IEnumerable<LumpDiff> Differing() => Lumps.Where(l => !l.Identical);

    /// <summary>The whole report as text.</summary>
    /// <param name="verbose">
    /// True to print every lump; false to print only the ones that differ, plus
    /// a one-line summary of the rest.
    /// </param>
    /// <returns>The report.</returns>
    public string ToText(bool verbose)
    {
        StringBuilder text = new();

        foreach (DiffDifference difference in Header)
        {
            text.Append("header ").AppendLine(difference.ToText());
        }

        int identical = 0;
        foreach (LumpDiff lump in Lumps)
        {
            if (!verbose && lump.Identical && lump.Judgements.IsEmpty)
            {
                identical++;
                continue;
            }

            text.Append(lump.ToText());
        }

        if (identical > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"{identical} further lumps identical");
            text.AppendLine();
        }

        text.Append(CultureInfo.InvariantCulture,
            $"=> {(Identical ? "identical" : $"{DifferenceCount} differences")}, "
            + $"bytes {(BytesIdentical ? "identical" : "differ")}");
        text.AppendLine();
        return text.ToString();
    }
}
