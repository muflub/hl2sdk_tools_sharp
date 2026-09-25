using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

using SourceSharp.MapTools.Options;
using SourceSharp.Tests.MapTools.Rad.Light;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>One curated evidence citation: a test file and the fact in it.</summary>
/// <param name="File">Path relative to the test-project root.</param>
/// <param name="Method">The <c>[Fact]</c>/<c>[Theory]</c> method that carries the evidence.</param>
internal sealed record QuirkCitation(string File, string Method);

/// <summary>One row of the compliance matrix: everything §11a asks per quirk.</summary>
/// <param name="Quirk">The quirk.</param>
/// <param name="MachineSites">
/// Evidence the scanner found on its own: test files (relative to the test-project root)
/// and the attributed method whose segment names the quirk or flips it.
/// </param>
/// <param name="Ledger">Curated citations for evidence the scanner cannot attribute.</param>
/// <param name="Gap">Why the quirk has neither, or null when it has one of the two.</param>
internal sealed record QuirkEvidenceRow(
    StockQuirk Quirk,
    ImmutableArray<string> MachineSites,
    ImmutableArray<QuirkCitation> Ledger,
    string? Gap);

/// <summary>A Q17 downstream-verification note for one quirk.</summary>
/// <param name="Quirk">The quirk.</param>
/// <param name="Note">What was verified downstream, and with what.</param>
/// <param name="File">A repository file that carries the verification, or null.</param>
internal sealed record DownstreamNote(StockQuirk Quirk, string Note, string? File);

/// <summary>
/// The data behind <c>docs/compliance-matrix.md</c>: the scanner that finds which
/// attributed facts speak for each quirk, the curated ledger for the ones the scanner
/// cannot see, the curated Q17 downstream notes, and the matrix generator itself.
/// </summary>
/// <remarks>
/// <para>
/// The scanner segments each test file on its <c>*Fact</c>/<c>*Theory</c> attributes and
/// attributes a quirk mention to the method whose attribute opens the segment holding it
/// (a bounded window, so a mention several methods down cannot count). A mention in a
/// class doc comment or a helper is NOT evidence: it proves the name is known, not that
/// a fact exercises it. Those quirks need a ledger citation naming the fact, and
/// the coverage fact checks the ledger against the same scanner, so a citation to a
/// deleted fact fails the suite.
/// </para>
/// <para>
/// No mutable statics: every collection here is rebuilt on each call.
/// </para>
/// </remarks>
internal static class ComplianceMatrix
{
    /// <summary>The repository root.</summary>
    internal static string RepoRoot =>
        RepoTree.FindRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("no checkout root above the test binary");

    /// <summary>The test project's source directory.</summary>
    internal static string TestsRoot =>
        Path.Combine(RepoRoot, "src", "sourcesharp", "managed", "SourceSharp.Tests");

    /// <summary>The committed matrix document.</summary>
    internal static string MatrixDocPath => Path.Combine(RepoRoot, "docs", "compliance-matrix.md");

    /// <summary>How long after an attribute a mention still counts as that fact's.</summary>
    private const int SegmentWindow = 1400;

    private static readonly Regex FactAttribute =
        new(@"\[\w*(?:Fact|Theory)\b[^\]]*\]", RegexOptions.CultureInvariant);

    private static readonly Regex Signature =
        new(@"(?:public|internal)\s+(?:static\s+|async\s+|sealed\s+|partial\s+)*[\w<>\[\],.?\s]*?\b(\w+)\s*\(",
            RegexOptions.CultureInvariant);

    private static readonly Regex QuirkToken =
        new(@"\b[A-Z]\w*\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// Walks every test source once and groups the attributed evidence.
    /// </summary>
    /// <returns>
    /// Per quirk, the sorted <c>file::method</c> sites whose attribute-segment names the
    /// quirk or flips it; and per file, every attributed method name (for citation checks).
    /// </returns>
    internal static (
        ImmutableDictionary<StockQuirk, ImmutableArray<string>> Evidence,
        ImmutableDictionary<string, ImmutableArray<string>> FileMethods) ScanTestSources()
    {
        HashSet<string> quirkNames = new(Enum.GetNames<StockQuirk>(), StringComparer.Ordinal);
        Dictionary<StockQuirk, SortedSet<string>> evidence = new();
        Dictionary<string, SortedSet<string>> fileMethods = new(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(TestsRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(TestsRoot, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || string.Equals(relative, "MapTools/Options/ComplianceMatrixCoverageTests.cs", StringComparison.Ordinal))
            {
                // The coverage file is ABOUT the quirks; it must not count as exercising them.
                continue;
            }
            string text = File.ReadAllText(path);
            Match[] attributes = [.. FactAttribute.Matches(text).Cast<Match>()];
            for (int i = 0; i < attributes.Length; i++)
            {
                int start = attributes[i].Index + attributes[i].Length;
                int end = i + 1 < attributes.Length
                    ? Math.Min(attributes[i + 1].Index, start + SegmentWindow)
                    : Math.Min(text.Length, start + SegmentWindow);
                string segment = text[start..end];

                Match sig = Signature.Match(segment);
                if (!sig.Success)
                {
                    continue;
                }

                string method = sig.Groups[1].Value;
                SortedSet<string> methods = GetOrAdd(fileMethods, relative);
                methods.Add(method);

                foreach (Match token in QuirkToken.Matches(segment))
                {
                    if (!quirkNames.Contains(token.Value)
                        || !Enum.TryParse<StockQuirk>(token.Value, out StockQuirk quirk))
                    {
                        continue;
                    }

                    GetOrAdd(evidence, quirk).Add(relative + "::" + method);
                }
            }
        }

        ImmutableDictionary<StockQuirk, ImmutableArray<string>> evidenceMap =
            Enum.GetValues<StockQuirk>().ToImmutableDictionary(
                q => q,
                q => evidence.TryGetValue(q, out SortedSet<string>? s)
                    ? s.ToImmutableArray()
                    : ImmutableArray<string>.Empty);
        ImmutableDictionary<string, ImmutableArray<string>> methodMap =
            fileMethods.ToImmutableDictionary(
                k => k.Key,
                k => k.Value.ToImmutableArray(),
                StringComparer.Ordinal);
        return (evidenceMap, methodMap);
    }

    private static SortedSet<string> GetOrAdd(Dictionary<StockQuirk, SortedSet<string>> d, StockQuirk key)
        => d.TryGetValue(key, out SortedSet<string>? s) ? s : d[key] = [];

    private static SortedSet<string> GetOrAdd(Dictionary<string, SortedSet<string>> d, string key)
        => d.TryGetValue(key, out SortedSet<string>? s) ? s : d[key] = [];

    /// <summary>
    /// The curated ledger: evidence the scanner cannot attribute, and the honest gap.
    /// </summary>
    /// <remarks>
    /// A ledger entry exists for a quirk whose exercising facts mention the quirk only
    /// outside any fact's attribute segment (a class doc, a shared helper, a helper
    /// parameter carrying the policy). Each entry names the facts; the citation fact
    /// proves they exist. Exactly one entry is a gap, and gaps need a reason.
    /// </remarks>
    internal static IReadOnlyDictionary<StockQuirk, (ImmutableArray<QuirkCitation> Citations, string? Gap)> Ledger =>
        new Dictionary<StockQuirk, (ImmutableArray<QuirkCitation>, string?)>
        {
            [StockQuirk.SkyboxRecursionFromLaneZero] =
            (
                [new QuirkCitation("MapTools/Rad/Light/GatherTests.cs", "AStockGroupRecursesByLaneZerosArea")],
                null),
            [StockQuirk.SkyProbeTailDoubleCount] =
            (
                [
                    new QuirkCitation("MapTools/Rad/Light/RadWorldTests.cs", "StockCastsTheTailDirectionThreeTimes"),
                    new QuirkCitation("MapTools/Rad/Light/RadWorldTests.cs", "CorrectCastsEachOfTheHundredAndSixtyTwoOnce"),
                ],
                null),
            [StockQuirk.SecondSunSpreadAngleWins] =
            (
                [
                    new QuirkCitation("MapTools/Rad/Light/RadWorldTests.cs", "StockLetsTheSecondSunsSpreadWin"),
                    new QuirkCitation("MapTools/Rad/Light/RadWorldTests.cs", "CorrectKeepsTheFirstSunsSpread"),
                ],
                null),
            [StockQuirk.TransferRayReciprocalEstimate] =
            (
                [new QuirkCitation("MapTools/Rad/Light/GatherTests.cs", "TheStockEstimateIsNotAlwaysTheExactReciprocal")],
                null),
            [StockQuirk.SampleRadialEdgeOffByOne] =
            (
                [
                    new QuirkCitation("MapTools/Rad/Final/LuxelRadialTests.cs", "StocksEdgeTestLetsAPointOnePastTheLastColumnThrough"),
                    new QuirkCitation("MapTools/Rad/Final/LuxelRadialTests.cs", "TheCorrectEdgeTestPutsAPointOnePastTheLastColumnOffTheGrid"),
                ],
                null),
            [StockQuirk.PatchRadialNeighbourBumpFromSelf] =
            (
                [
                    new QuirkCitation("MapTools/Rad/Final/FinalLightFaceTests.cs", "StocksBounceFilterTakesANeighboursBumpinessFromTheFaceItself"),
                    new QuirkCitation("MapTools/Rad/Final/FinalLightFaceTests.cs", "TheCorrectBounceFilterAsksTheNeighbour"),
                ],
                null),
            [StockQuirk.CollisionCookerSinglePrecision] =
            (
                [
                    new QuirkCitation("MapTools/Phys/Managed/ManagedCollisionCookerTests.cs", "StockComplianceCooksInSinglePrecision"),
                    new QuirkCitation("MapTools/Phys/Managed/ManagedCollisionCookerTests.cs", "CorrectComplianceCooksInDoublePrecision"),
                ],
                null),
            [StockQuirk.KdZeroDirectionReachCut] =
            (
                [
                    new QuirkCitation("MapTools/Tracing/KdRayTracerZeroDirectionTests.cs", "StockCutsAPositiveZeroRayShortOfTheWall"),
                    new QuirkCitation("MapTools/Tracing/KdRayTracerZeroDirectionTests.cs", "CorrectLetsAPositiveZeroRayReachTheWall"),
                ],
                null),
        };

    /// <summary>
    /// The curated Q17 downstream-verification notes: where a quirk's correct-mode
    /// change was checked against something downstream of the compiler, not just at
    /// the decision site.
    /// </summary>
    internal static IReadOnlyList<DownstreamNote> DownstreamVerified =>
    [
        new DownstreamNote(
            StockQuirk.DispLightmapSwapDropped,
            "Stock vrad runs on the swap-patched BSP the tool writes, and the extents it "
            + "reports are the check that the repointed lightmap survives downstream.",
            "src/sourcesharp/managed/SourceSharp.Tests/MapTools/Disp/DispSwapDownstreamTool.cs"),
        new DownstreamNote(
            StockQuirk.KdZeroDirectionReachCut,
            "The stock-side expectations are produced by stock's own raytrace.cpp compiled "
            + "unchanged into the p5a oracle, not by a reading of the source.",
            null),
        new DownstreamNote(
            StockQuirk.BaseWindingNormalise,
            "The PLANES lump vbsp writes — which vvis, vrad and the engine linker all read — "
            + "is checked element-for-element against stock on every catalogue map under stock "
            + "policy, and the octahedron pair pins which half of the plane bytes each normalise "
            + "quirk controls: the stored TYPE follows EdgeBevelNormalise, the DISTANCE follows "
            + "BaseWindingNormalise, and neither flip alone re-matches a stock plane.",
            "src/sourcesharp/managed/SourceSharp.Tests/MapTools/Bsp/StockLoadCatalogueTests.cs"),
        new DownstreamNote(
            StockQuirk.CollisionCookerSinglePrecision,
            "The managed cooker IS the compile's cooker in this gate's twin: every cooked blob "
            + "is read back through the pinned native VPhysics library — its query model and "
            + "VCollideLoad, the engine's own reader — so a single-precision cook is accepted or "
            + "rejected by the engine, not by the cooker's own bookkeeping.",
            "src/sourcesharp/managed/SourceSharp.Tests/MapTools/Bsp/Driver/VbspPhysStockGateTests.cs"),
        new DownstreamNote(
            StockQuirk.DispVertexNormalMeanUnnormalised,
            "Stock vrad's direct light agrees with the managed run on displacement luxels at "
            + "bounce 0 (the unnormalised mean flows through the blend weights it produces), and "
            + "the correct-mode fast pass shows no black edge row — the fixed normal's effect is "
            + "read back from the lighting lump, not from the vertex array.",
            "src/sourcesharp/managed/SourceSharp.Tests/MapTools/Rad/Displacement/DispStockGateTests.cs"),
        new DownstreamNote(
            StockQuirk.VradVectorNormalise,
            "Measured, not prose: the p4f full gate under Stock with only this quirk forced to "
            + "correct (`P4F_FLIP_QUIRK`) fails 51 facts — 6 VertNormals byte-identity and 45 "
            + "frozen lighting-tolerance — so the quirk is corpus-active and its fixed vector is "
            + "visible in the VERTNORMALS and LIGHTING lumps against stock's own references.",
            "fixtures/compliance-flip/p4f-flip-gates.txt"),
        new DownstreamNote(
            StockQuirk.GatherReciprocalEstimate,
            "Measured: the same flip-gate mechanism forcing only this quirk to correct fails 38 "
            + "frozen lighting-tolerance facts on the catalogue — the gather estimate's product "
            + "moves the LIGHTING lump past stock's frozen tolerance, downstream of the gather.",
            "fixtures/compliance-flip/p4f-flip-gates.txt"),
    ];

    /// <summary>
    /// The Tools++ option names from <c>plan_toolspp_support.md</c> §0/§2, stripped of
    /// their leading dash and lower-cased. None of them may name a <see cref="StockQuirk"/>:
    /// ++ is a feature surface, not a stock defect.
    /// </summary>
    internal static ImmutableArray<string> ToolsPlusPlusFlags =>
    [
        "aoc", "aocpasssamples", "aoscale",
        "bounce", "bouncedisplace",
        "bspformat", "lightformat", "staticpropformat", "noformatdetect",
        "centeramples", "largedisksampleradius",
        "compress", "repack", "addlist", "deletecubemaps", "dir", "extract", "extractcubemaps",
        "csgoclipcontents",
        "cullbrushes", "cullbrushsides", "cullverts", "cullplanes", "cullall",
        "dedupeldrhdr", "entfirst",
        "extra", "extraordinary",
        "instancebevel", "warnmissinginstances", "nonamefixup",
        "keepstalezip",
        "matsyscompat",
        "maxbrushes", "maxbrushsides", "maxtexinfo", "micro", "maxdispinfo",
        "missingmaterial", "forcematerial", "materialdebug",
        "nobevel", "nocull", "nodetailclip", "nophysics",
        "nodisp4virtualmesh", "noineligiblevertexlitprops",
        "noldrcubemap", "nohdrcubemap",
        "notoolsargs", "onlystaticprops",
        "report_search_paths",
        "simpleladders", "sphericalharmonics",
        "staticpropindirectmode", "staticpropsamplescale",
        "supports", "translucentshadows", "worldtextureshadows",
    ];

    /// <summary>The tools a quirk moves, as the matrix prints them.</summary>
    /// <param name="tools">The catalogue's tools flag.</param>
    /// <returns>Like <c>vbsp+vrad</c>.</returns>
    internal static string ToolsLabel(CompileTools tools)
    {
        List<string> names = [];
        if (tools.HasFlag(CompileTools.Vbsp))
        {
            names.Add("vbsp");
        }

        if (tools.HasFlag(CompileTools.Vvis))
        {
            names.Add("vvis");
        }

        if (tools.HasFlag(CompileTools.Vrad))
        {
            names.Add("vrad");
        }

        return names.Count == 0 ? "-" : string.Join("+", names);
    }

    /// <summary>
    /// Builds the whole matrix document from the catalogue, the scan and the ledger.
    /// </summary>
    /// <returns>The document text, byte-stable for the committed file.</returns>
    internal static string GenerateMatrix()
    {
        (ImmutableDictionary<StockQuirk, ImmutableArray<string>> evidence, _) = ScanTestSources();
        IReadOnlyDictionary<StockQuirk, (ImmutableArray<QuirkCitation> Citations, string? Gap)> ledger = Ledger;

        StringBuilder sb = new();
        sb.AppendLine("# Compliance matrix");
        sb.AppendLine();
        sb.AppendLine("> GENERATED FILE - do not edit by hand. Regenerate with");
        sb.AppendLine("> `MAPTOOLS_COMPLIANCE_MATRIX=1 dotnet test src/sourcesharp/managed/SourceSharp.Tests --filter FullyQualifiedName~ComplianceMatrixDocIsGeneratedFromCode`");
        sb.AppendLine("> from the repository root (a `run-capped 4G` wrapper belongs in front of it on a shared box).");
        sb.AppendLine("> A fact compares the committed file against this generator, so an edit here fails the suite.");
        sb.AppendLine();
        sb.AppendLine("Every `StockQuirk` - every place this port can either reproduce a stock defect or fix");
        sb.AppendLine("it - with its policy split (`stock` reproduces, `correct` is the default and fixes per");
        sb.AppendLine("ruling Q17), its evidence, and its downstream verification. The catalogue text comes");
        sb.AppendLine("from `ComplianceCatalogue`; the Evidence column comes from a scan of the test sources");
        sb.AppendLine("(an attributed fact's method segment must name the quirk or flip it) merged with the");
        sb.AppendLine("curated ledger in `ComplianceMatrix.cs`. `GAP` means neither exists, with the reason");
        sb.AppendLine("listed under Gaps below. A blank Downstream cell means the correct-mode change has");
        sb.AppendLine("NOT yet been verified downstream - Q17 debt, tracked in the lane findings.");
        sb.AppendLine();
        sb.AppendLine("The CLI is per-quirk (`plan_maptools.md` §11a): `-compliance");
        sb.AppendLine("<correct|stock>[,+Quirk][,-Quirk]...` on vbsp, vvis, vrad and `ssmap all`");
        sb.AppendLine("(`StockArgs.TryCompliance`, one parser shared by the three verbs and the chain).");
        sb.AppendLine("`+Quirk` demands stock behaviour for that quirk and `-Quirk` correct behaviour: the");
        sb.AppendLine("tokens name the wanted side, a later token for a quirk overwrites an earlier one,");
        sb.AppendLine("names match case-insensitively, and an unknown mode or quirk is an error naming the");
        sb.AppendLine("observed token and pointing at `-listcompliance` - never a silent ignore, never a");
        sb.AppendLine("nearest-name coercion. On `ssmap all` the chain's `-compliance` goes to all three");
        sb.AppendLine("stages and a section's own `-compliance` comes after it and wins for that stage.");
        sb.AppendLine("The value the flag builds is the library-side one: `ComplianceOptions.Flipping` /");
        sb.AppendLine("`Except`, read by `Emulates`, which is what the flip facts use.");
        sb.AppendLine();
        sb.AppendLine("Tools++ options are feature flags in `Options/*`, never `StockQuirk` members (the ++");
        sb.AppendLine("plan §0 ground rule); `ComplianceMatrixCoverageTests` fails if a ++ flag name ever");
        sb.AppendLine("shows up as a quirk.");
        sb.AppendLine();
        sb.AppendLine("| Quirk | Tools | Stock site | What stock does | Evidence | Downstream (Q17) |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (ComplianceQuirkInfo info in ComplianceCatalogue.All)
        {
            List<string> cells =
            [
                ToolsLabel(info.Tools),
                "`" + info.StockSite + "`",
                info.Summary.Replace("|", "\\|", StringComparison.Ordinal),
            ];

            ImmutableArray<string> machine = evidence[info.Quirk];
            (ImmutableArray<QuirkCitation> citations, string? gap) =
                ledger.TryGetValue(info.Quirk, out var entry) ? entry : ([], null);

            List<string> evidenceCells = [.. machine.Select(s => "`" + s + "`")];
            evidenceCells.AddRange(citations.Select(c => "`" + c.File + "::" + c.Method + "` (ledger)"));
            cells.Add(evidenceCells.Count > 0
                ? string.Join(", ", evidenceCells)
                : "GAP - " + (gap ?? "none"));

            DownstreamNote? note = DownstreamVerified.FirstOrDefault(n => n.Quirk == info.Quirk);
            cells.Add(note is null ? string.Empty : note.Note.Replace("|", "\\|", StringComparison.Ordinal));

            sb.Append("| ").Append(info.Quirk);
            foreach (string cell in cells)
            {
                sb.Append(" | ").Append(cell);
            }

            sb.AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine("## Gaps");
        sb.AppendLine();
        bool any = false;
        foreach (StockQuirk quirk in Enum.GetValues<StockQuirk>())
        {
            if (ledger.TryGetValue(quirk, out var entry) && entry.Gap is { Length: > 0 } reason && machine_empty(evidence[quirk]))
            {
                sb.AppendLine("- **").Append(quirk).Append("**: ").Append(reason).Append('.');
                any = true;
            }
        }

        if (!any)
        {
            sb.AppendLine("- none");
        }

        sb.AppendLine();
        sb.AppendLine("## Downstream notes (Q17)");
        sb.AppendLine();
        foreach (DownstreamNote note in DownstreamVerified)
        {
            sb.Append("- **").Append(note.Quirk).Append("**: ").Append(note.Note);
            if (note.File is { Length: > 0 })
            {
                sb.Append(" (`").Append(note.File).Append("`).");
            }
            else
            {
                sb.Append('.');
            }

            sb.AppendLine();
        }

        return sb.ToString();

        static bool machine_empty(ImmutableArray<string> sites) => sites.IsDefaultOrEmpty;
    }
}
