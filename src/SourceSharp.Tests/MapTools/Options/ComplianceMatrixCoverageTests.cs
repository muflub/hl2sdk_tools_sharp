using System.Collections.Immutable;
using System.Text.RegularExpressions;

using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// The compliance matrix's completeness gates: every quirk is spoken for by an
/// attributed fact or a resolving ledger citation (never silence), the ledger is
/// honest, the Q17 downstream notes and the ++ cross-check hold, and the committed
/// <c>docs/compliance-matrix.md</c> is what the code generates.
/// </summary>
/// <remarks>
/// The catalogue side (an entry per quirk, a real C++ citation, exactly the methods
/// that consult the switch) is <see cref="ComplianceCatalogueTests"/>'s, including its
/// IL scan of the built assembly - that is the "a site honours <c>Emulates</c>"
/// dimension. What lives here is the EVIDENCE dimension §11a adds: per quirk, a fact
/// that demonstrates the flip, plus the honesty of the matrix around it.
/// A gap fails rather than being quietly missing - the gap's removal (a new fact, or a
/// ledger citation that resolves) is the fix.
/// This file is excluded from the evidence scan it drives: it is ABOUT the quirks.
/// </remarks>
public class ComplianceMatrixCoverageTests
{
    public static TheoryData<StockQuirk> Quirks => [.. Enum.GetValues<StockQuirk>()];

    [Theory]
    [MemberData(nameof(Quirks))]
    public void EveryQuirkHasProvenEvidenceOrAGapWithAReason(StockQuirk quirk)
    {
        (ImmutableDictionary<StockQuirk, ImmutableArray<string>> evidence,
         ImmutableDictionary<string, ImmutableArray<string>> fileMethods) = ComplianceMatrix.ScanTestSources();

        if (!evidence[quirk].IsDefaultOrEmpty)
        {
            return;
        }

        Assert.True(ComplianceMatrix.Ledger.TryGetValue(quirk, out var entry),
            $"{quirk}: no attributed fact names or flips it, and no ledger entry speaks for it. "
            + "Add a fact that exercises the flip, or a curated citation naming one.");

        if (entry.Gap is { Length: > 0 })
        {
            Assert.True(entry.Citations.IsEmpty,
                $"{quirk}: a gap entry may not also carry citations; resolve them into evidence.");
            return;
        }

        Assert.False(entry.Citations.IsEmpty, $"{quirk}: ledger entry with neither citations nor a gap reason.");
        foreach (QuirkCitation citation in entry.Citations)
        {
            bool found = fileMethods.TryGetValue(citation.File, out ImmutableArray<string> methods);
            Assert.True(found && !methods.IsDefault && methods.Contains(citation.Method, StringComparer.Ordinal),
                $"{quirk}: ledger citation {citation.File}::{citation.Method} is not an attributed fact "
                + "method any more - fix the citation or restore the fact.");
        }
    }

    [Fact]
    public void TheScannerIsNotBlind()
    {
        // The evidence scan is the instrument here, so it gets a known-answer check
        // like the IL scan has: a quirk whose flip is demonstrated inside an attributed
        // fact must be found by file and method, and a scanner that finds nothing
        // cannot pass the per-quirk fact above by comparing two empty sets.
        (ImmutableDictionary<StockQuirk, ImmutableArray<string>> evidence, _) = ComplianceMatrix.ScanTestSources();

        Assert.NotEmpty(evidence[StockQuirk.NodeAreaWrittenBeforeSet]);
        Assert.Contains(evidence[StockQuirk.NodeAreaWrittenBeforeSet],
            s => s.Contains("VbspCompileTests.cs::UnderStockEveryNodeAreaIsZero", StringComparison.Ordinal));
    }

    [Fact]
    public void LedgerEntriesBelongToRealQuirksAndDoNotDuplicateMachineEvidence()
    {
        (ImmutableDictionary<StockQuirk, ImmutableArray<string>> evidence, _) = ComplianceMatrix.ScanTestSources();

        foreach ((StockQuirk quirk, (ImmutableArray<QuirkCitation> Citations, string? Gap) entry) in ComplianceMatrix.Ledger)
        {
            Assert.True(Enum.IsDefined(quirk), $"ledger names {quirk}, which is not a StockQuirk");
            Assert.True(!string.IsNullOrWhiteSpace(entry.Gap) || !entry.Citations.IsEmpty, $"{quirk}: empty ledger row");
            Assert.True(entry.Citations.IsEmpty || entry.Gap is null, $"{quirk}: citations AND a gap reason");
            Assert.True(entry.Gap is null || evidence[quirk].IsDefaultOrEmpty,
                $"{quirk}: declared a gap, yet the scanner found {evidence[quirk].Length} evidence site(s) - drop the gap entry");
        }
    }

    [Fact]
    public void DownstreamNotesNameRealQuirksAndExistingFiles()
    {
        HashSet<StockQuirk> known = [.. Enum.GetValues<StockQuirk>()];
        foreach (DownstreamNote note in ComplianceMatrix.DownstreamVerified)
        {
            Assert.True(known.Contains(note.Quirk), $"{note.Quirk} is not a StockQuirk");
            Assert.False(string.IsNullOrWhiteSpace(note.Note));
            if (note.File is { Length: > 0 } file)
            {
                Assert.True(File.Exists(Path.Combine(ComplianceMatrix.RepoRoot, file)),
                    $"downstream note for {note.Quirk} cites {file}, which does not exist");
            }
        }
    }

    [Fact]
    public void NoStockQuirkIsNamedLikeAToolsPlusPlusFeature()
    {
        // Ruling (plan_toolspp_support.md §0): everything ++ is a feature, not a stock
        // bug. A ++ flag name appearing as a quirk would fake an oracle-gated feature
        // into the defect switchboard. Whole Pascal words only - 'dir' may live inside
        // 'Indirect' harmlessly; standing alone as a quirk word it would not.
        foreach (string flag in ComplianceMatrix.ToolsPlusPlusFlags)
        {
            foreach (StockQuirk quirk in Enum.GetValues<StockQuirk>())
            {
                string name = quirk.ToString();
                string[] words = [.. SplitPascal(name).Select(w => w.ToLowerInvariant())];
                Assert.False(string.Equals(name, flag, StringComparison.OrdinalIgnoreCase),
                    $"{name} is a Tools++ flag name, not a stock defect");
                Assert.False(flag.Length >= 6 && name.Contains(flag, StringComparison.OrdinalIgnoreCase),
                    $"{name} embeds the multi-word Tools++ flag '{flag}'");
                Assert.False(flag.Length < 6 && words.Contains(flag, StringComparer.Ordinal),
                    $"{name} has the Tools++ flag '{flag}' as its own word");
            }
        }
    }

    [Fact]
    public void TheCatalogueTextDoesNotNameAToolsPlusPlusFeature()
    {
        // Second arm of the same ruling: not even a catalogue summary smuggling a ++
        // flag in as if it were stock behaviour. Whole words only, and the flags that
        // are ALSO stock vrad options (their text legitimately names them) are exempt.
        string joined = string.Join(
            ' ',
            ComplianceCatalogue.All.Select(e => e.Quirk + " " + e.Summary + " " + e.Note));
        string[] tokens = [.. Regex.Matches(joined, @"[A-Za-z0-9]+").Select(m => m.Value.ToLowerInvariant())];
        string[] alsoStock = ["bounce", "extra"];

        foreach (string flag in ComplianceMatrix.ToolsPlusPlusFlags.Except(alsoStock, StringComparer.Ordinal))
        {
            Assert.False(tokens.Contains(flag, StringComparer.Ordinal),
                $"the catalogue text names '{flag}' as a word - ++ is not stock behaviour");
        }
    }

    [Fact]
    public void MatrixRowsAreExactlyTheQuirks()
    {
        string doc = ComplianceMatrix.GenerateMatrix();
        string[] rows = [.. doc.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal))];

        // header + one row per quirk (the |---| separator does not start "| ")
        Assert.Equal(Enum.GetValues<StockQuirk>().Length + 1, rows.Length);
        Assert.Contains("|---|", doc, StringComparison.Ordinal);
        foreach (StockQuirk quirk in Enum.GetValues<StockQuirk>())
        {
            Assert.Contains("| " + quirk + " |", doc, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ComplianceMatrixDocIsGeneratedFromCode()
    {
        string generated = ComplianceMatrix.GenerateMatrix();

        if (Environment.GetEnvironmentVariable("MAPTOOLS_COMPLIANCE_MATRIX") is "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ComplianceMatrix.MatrixDocPath)!);
            File.WriteAllText(ComplianceMatrix.MatrixDocPath, generated);
            return;
        }

        Assert.True(File.Exists(ComplianceMatrix.MatrixDocPath),
            "docs/compliance-matrix.md is missing - generate it with MAPTOOLS_COMPLIANCE_MATRIX=1");
        Assert.True(
            string.Equals(Normalize(File.ReadAllText(ComplianceMatrix.MatrixDocPath)), Normalize(generated), StringComparison.Ordinal),
            "the committed matrix is stale - regenerate with MAPTOOLS_COMPLIANCE_MATRIX=1");
    }

    private static IEnumerable<string> SplitPascal(string name) =>
        Regex.Matches(name, @"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z0-9]+").Select(m => m.Value);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
