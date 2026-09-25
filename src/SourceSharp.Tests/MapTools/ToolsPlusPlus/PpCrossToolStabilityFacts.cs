using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.ToolsPlusPlus;

/// <summary>
/// ++-side stability for the cross-tool tiers (plan tools++ §T6): the parity
/// gates consume the corpus's ++ final outputs, so before trusting them this
/// tier checks that ++ itself reproduces those outputs — two <c>vvis++</c>
/// runs over the same <c>.bsp</c>/<c>.prt</c> agree lump-for-lump, two
/// <c>vrad++ -threads 1</c> runs over the same <c>.ppvis</c> agree, and the
/// repeat runs reproduce the corpus reference where the reference is
/// reproducible. The products come from <c>tools/t6-stability.sh</c>; the
/// compare files carry one “<c>&lt;lump&gt; same=true|false lens=… </c>”
/// line per compared lump.
/// </summary>
/// <remarks>
/// Measured here (and the reason the vrad++ reference gate is narrower than
/// the vvis++ one): <c>vrad++</c> LIGHTING from a <c>-threads 1</c> rerun does
/// NOT byte-reproduce the corpus <c>.pprad.bsp</c>, which was produced at the
/// default 32 threads (masked comparison: run1-vs-run2 all-equal with 128,620
/// masked samples, run1-vs-corpus differs with only 1,548 masked) — the corpus
/// goldens are stable by sha256 pin, not by reproducibility. WORLDLIGHTS and
/// VISIBILITY DO reproduce. This is also why the frozen vrad tolerance exists.
/// </remarks>
public sealed class PpCrossToolStabilityFacts(ITestOutputHelper output)
{
    [PpCrossToolStabilityFact]
    public void VvisPlusPlusReproducesItselfAndTheCorpusOutput()
    {
        AssertAllSame("vis-compare.txt");      // run1 vs run2, VISIBILITY
        AssertAllSame("vis-ref-compare.txt");  // run1 vs corpus .ppvis.bsp
    }

    [PpCrossToolStabilityFact]
    public void VradPlusPlusSingleThreadReproducesItself()
    {
        // Two independent -threads 1 runs over the same .ppvis agree on every
        // compared lump (LIGHTING under the uninit-exponent mask).
        AssertAllSame("rad-compare.txt");
    }

    [PpCrossToolStabilityFact]
    public void VradPlusPlusReproducesTheCorpusWorldLightsButNotItsLighting()
    {
        // The gateable half: WORLDLIGHTS (the deterministic lump) must match
        // the corpus reference exactly.
        AssertSame("rad-ref-compare.txt", "WorldLights");

        // The recorded half: LIGHTING differs from the 32-thread corpus
        // golden even for a 1-thread rerun of ++ against itself — the
        // reference is pinned by the corpus sha256, and the numeric parity
        // contract lives in VradCrossToolParityTests' frozen tolerance. This
        // assertion pins the MEASUREMENT shape, not equality.
        string[] lines = Loaded("rad-ref-compare.txt");
        string lighting = Assert.Single(lines, l => l.StartsWith("Lighting ", StringComparison.Ordinal));
        output.WriteLine(lighting);
        Assert.Contains("lens=906692,906692", lighting, StringComparison.Ordinal);
    }

    [PpCrossToolStabilityFact]
    public void DefaultThreadVradNondeterminismIsRecorded()
    {
        // Not asserted-true and not asserted-false: the script always records
        // the default-threads pair, and this fact only pins that the record
        // exists with parseable lines, so the measurement stays visible in
        // test output without encoding nondeterminism as a requirement.
        string[] lines = Loaded("raddef-compare.txt");
        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            Assert.Contains("same=", line, StringComparison.Ordinal);
            output.WriteLine(line);
        }
    }

    private string[] Loaded(string name)
    {
        string path = Path.Combine(PpCrossToolHarness.StabDir, name);
        Assert.True(File.Exists(path), $"{path} missing: run tools/t6-stability.sh");
        return [.. File.ReadAllLines(path).Where(l => l.Contains("same=", StringComparison.Ordinal))];
    }

    private void AssertAllSame(string name)
    {
        string[] lines = Loaded(name);
        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            output.WriteLine(line);
            Assert.Contains("same=true", line, StringComparison.Ordinal);
        }
    }

    private void AssertSame(string name, string lump)
    {
        string line = Assert.Single(Loaded(name), l => l.StartsWith(lump + " ", StringComparison.Ordinal));
        output.WriteLine(line);
        Assert.Contains("same=true", line, StringComparison.Ordinal);
    }
}

/// <summary>Fact attribute that skips the stability tier when it is not mounted.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PpCrossToolStabilityFactAttribute : FactAttribute
{
    /// <summary>Names the skip reason.</summary>
    public PpCrossToolStabilityFactAttribute() => Skip = PpCrossToolHarness.StabilitySkipReason();
}
