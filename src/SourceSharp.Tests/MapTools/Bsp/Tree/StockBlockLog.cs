using System.Globalization;
using System.Text.RegularExpressions;

using SourceSharp.MapTools.Bsp.Tree;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// The per-block CSG and tree counts parsed out of a stock <c>vbsp -v</c> log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a log and not a BSP.</b> The NODES and LEAFS lumps of a finished map
/// are the product of this lane's tree build AND Phase 3c's portals, fills and
/// area assignment AND Phase 3d's face and detail merging AND Phase 3e's
/// numbering — so comparing them says nothing about 3b until all five exist. A
/// <c>-v</c> log, by contrast, records what each block's <c>ChopBrushes</c> and
/// <c>BrushBSP</c> decided, before anything else touches it. That is an exact
/// comparison on exactly this lane's output, available now.
/// </para>
/// <para>
/// <b>Only the first pass of model 0 is read, and that is the point.</b>
/// <c>ProcessWorldModel</c> builds the world TWICE
/// (<c>vbsp.cpp:259</c>): the second pass runs after
/// <c>MarkVisibleSides</c> has rewritten <c>visible</c> on every map brush
/// side, so its numbers are a function of Phase 3c's work and cannot be
/// reproduced here. The first pass is. The parse therefore stops at
/// <c>--- FloodEntities ---</c>, which is the first thing after pass one's
/// block loop, and refuses to read anything from model 1 onwards — those are
/// submodels, which do not use the block grid at all.
/// </para>
/// </remarks>
internal static class StockBlockLog
{
    /// <summary>
    /// The block records of one map's first world pass, in the order stock
    /// compiled them.
    /// </summary>
    /// <param name="name">The catalogue entry's name.</param>
    /// <returns>One record per block.</returns>
    public static IReadOnlyList<BlockBuildStatistics> FirstWorldPass(string name)
    {
        string text = File.ReadAllText(
            Path.Combine(SourceSharp.Tests.MapTools.Vis.StockCatalogue.Directory!,
                name + ".vbspv.log"));

        int start = text.IndexOf(
            "############### model 0 ###############", StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        int end = text.IndexOf("--- FloodEntities ---", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = text.Length;
        }

        return Parse(text[start..end]);
    }

    private static readonly Regex BlockHeader = new(
        @"#+ block\s*(-?\d+)\s*,\s*(-?\d+) #+",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static IReadOnlyList<BlockBuildStatistics> Parse(string region)
    {
        List<BlockBuildStatistics> blocks = [];

        MatchCollection headers = BlockHeader.Matches(region);

        for (int i = 0; i < headers.Count; i++)
        {
            Match header = headers[i];
            int bodyStart = header.Index + header.Length;
            int bodyEnd = i + 1 < headers.Count ? headers[i + 1].Index : region.Length;
            string body = region[bodyStart..bodyEnd];

            int x = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
            int y = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);

            ChopStatistics? chop = null;
            int? input = Labelled(body, "original brushes:");
            int? output = Labelled(body, "output brushes:");
            if (input is not null && output is not null)
            {
                chop = new ChopStatistics(input.Value, output.Value);
            }

            BspTreeStatistics? tree = null;
            int? brushes = Counted(body, "brushes");
            if (brushes is not null)
            {
                tree = new BspTreeStatistics(
                    brushes.Value,
                    Counted(body, "visible faces") ?? -1,
                    Counted(body, "nonvisible faces") ?? -1,
                    Counted(body, "visible nodes") ?? -1,
                    Counted(body, "nonvis nodes") ?? -1,
                    Counted(body, "leafs") ?? -1);
            }

            blocks.Add(new BlockBuildStatistics(x, y, chop, tree));
        }

        return blocks;
    }

    /// <summary>"original brushes: 4" — printed with no field width.</summary>
    private static int? Labelled(string body, string label)
    {
        Match match = Regex.Match(
            body,
            Regex.Escape(label) + @"\s*(\d+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// "    4 visible faces" — <c>%5i</c> then the label, on its own line.
    /// </summary>
    /// <remarks>
    /// Anchored to the line so that "brushes" does not also match the
    /// "original brushes:" line above it, and so that "visible faces" does not
    /// match "nonvisible faces".
    /// </remarks>
    private static int? Counted(string body, string label)
    {
        Match match = Regex.Match(
            body,
            @"^\s*(-?\d+) " + Regex.Escape(label) + @"\s*$",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(5));

        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }
}
