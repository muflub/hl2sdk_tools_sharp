using System.Globalization;
using System.Text.RegularExpressions;

using SourceSharp.Tests.MapTools.Vis;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// The eleven numbers a stock <c>vbsp -v</c> log prints about the face stage.
/// </summary>
/// <param name="MakeFaces">"%5i makefaces" — <c>c_nodefaces</c>.</param>
/// <param name="Merged">"%5i merged" — <c>c_merge</c>.</param>
/// <param name="Subdivided">"%5i subdivided" — <c>c_subdivide</c>.</param>
/// <param name="UniqueVerts">the first number of "%i unique from %i".</param>
/// <param name="TotalVerts">the second.</param>
/// <param name="DegenerateEdges">"%5i edges degenerated".</param>
/// <param name="CollapsedFaces">"%5i faces degenerated".</param>
/// <param name="TJunctions">"%5i edges added by tjunctions".</param>
/// <param name="FaceOverflows">"%5i faces added by tjunctions".</param>
/// <param name="BadStartVerts">"%5i bad start verts".</param>
internal readonly record struct StockFaceCounts(
    int MakeFaces,
    int Merged,
    int Subdivided,
    int UniqueVerts,
    int TotalVerts,
    int DegenerateEdges,
    int CollapsedFaces,
    int TJunctions,
    int FaceOverflows,
    int BadStartVerts);

/// <summary>
/// Reading the face stage's numbers out of a stock verbose log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the log and not the face lump.</b> LUMP_FACES is written after
/// displacements have replaced some faces, brush models have added their own
/// and <c>WriteBSP</c> has built the original-face table, so comparing against
/// it would be comparing three stages at once and attributing any difference
/// to the wrong one. The <c>-v</c> block between "--- MakeFaces ---" and
/// "PruneNodes..." is stock stating, in its own words, what THIS stage did —
/// and it is printed before anything downstream has touched the result.
/// </para>
/// <para>
/// Every line is taken at its FIRST occurrence, which is the world model's.
/// The block repeats once per brush model and those runs have their own
/// numbers; the world is what a single-model harness can reproduce.
/// </para>
/// </remarks>
internal static class StockFaceLog
{
    /// <summary>Parses the world model's face numbers out of an entry's log.</summary>
    /// <param name="name">The catalogue entry.</param>
    /// <returns>The counts.</returns>
    internal static StockFaceCounts Counts(string name)
    {
        string text = WorldModelText(File.ReadAllText(
            Path.Combine(StockCatalogue.Directory!, name + ".vbspv.log")));

        Match unique = Match(text, @"^\s*(\d+) unique from (\d+)\s*$");

        return new StockFaceCounts(
            Labelled(text, "makefaces"),
            Labelled(text, "merged"),
            Labelled(text, "subdivided"),
            int.Parse(unique.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(unique.Groups[2].Value, CultureInfo.InvariantCulture),
            Labelled(text, "edges degenerated"),
            Labelled(text, "faces degenerated"),
            Labelled(text, "edges added by tjunctions"),
            Labelled(text, "faces added by tjunctions"),
            Labelled(text, "bad start verts"));
    }

    /// <summary>
    /// Whether this entry's log records more than one model's face pass.
    /// </summary>
    /// <param name="name">The catalogue entry.</param>
    /// <returns>True when "--- MakeFaces ---" appears more than once.</returns>
    /// <remarks>
    /// Stock runs the whole face stage again for every brush entity, and the
    /// counters that are NOT reset between models — <c>c_badstartverts</c> is
    /// the only one — then accumulate. So on a multi-model map the log's "bad
    /// start verts" is a total and the world-only harness cannot match it,
    /// which is a fact the gates state rather than work around.
    /// </remarks>
    internal static bool HasBrushModels(string name)
    {
        string text = File.ReadAllText(
            Path.Combine(StockCatalogue.Directory!, name + ".vbspv.log"));

        return Regex.Matches(text, @"--- MakeFaces ---", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Count > 1;
    }

    /// <summary>
    /// The log from the world model's banner on (<c>vbsp.cpp:862</c>).
    /// </summary>
    /// <remarks>
    /// A map with a <c>func_occluder</c> runs a whole face pass over the
    /// occluder tree BEFORE model 0 (<c>EmitOccluderBrushes</c>,
    /// <c>vbsp.cpp:853</c>), and its "makefaces" / "unique from" lines come
    /// first. ss_sandbox has one: its first block read 6 makefaces against the
    /// world's 1934.
    /// </remarks>
    internal static string WorldModelText(string text)
    {
        int banner = text.IndexOf("############### model 0 ###############", StringComparison.Ordinal);
        return banner < 0 ? text : text[banner..];
    }

    private static int Labelled(string text, string label)
    {
        Match match = Match(text, @"^\s*(-?\d+) " + Regex.Escape(label) + @"\s*$");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static Match Match(string text, string pattern)
    {
        Match match = Regex.Match(
            text, pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(5));

        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"the stock log has no line matching /{pattern}/");
        }

        return match;
    }
}
