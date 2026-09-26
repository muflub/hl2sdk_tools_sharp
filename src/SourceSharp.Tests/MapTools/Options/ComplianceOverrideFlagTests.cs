//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// <c>-compliance &lt;mode&gt;[,&lt;+/-Quirk&gt;…]</c>: the per-quirk CLI seam
/// the compliance matrix (<c>docs/compliance-matrix.md</c>) and plan §11a
/// promise, and the only supported way a user reaches
/// <see cref="ComplianceOptions.Except"/>. <c>+Quirk</c> demands that quirk
/// behave AS STOCK and <c>-Quirk</c> demands it behave correctly, whichever way
/// the baseline falls — the tokens name the wanted side, and the parser puts
/// the ones that differ from the baseline into <c>Except</c>, which
/// <see cref="ComplianceOptions.Emulates"/> already reads.
/// </summary>
public class ComplianceOverrideFlagTests
{
    private const string Map = "maps/testmap.bsp";

    /// <summary>A real quirk with an owning fact (P11's matrix), not a made-up name.</summary>
    private const string Quirk = "KdZeroDirectionReachCut";

    private static ComplianceOptions ParseVbsp(string value) =>
        StockArgs.ParseVbsp(["-compliance", value, Map]).Options.Compliance;

    // ---- the baseline still works alone ----

    [Fact]
    public void APlainModeStillParsesAndCarriesNoExceptions()
    {
        ComplianceOptions c = ParseVbsp("stock");

        Assert.Equal(CompliancePolicy.Stock, c.Policy);
        Assert.Empty(c.Except);
        Assert.Equal(ComplianceOptions.Stock, c);
    }

    [Fact]
    public void TheDefaultLineChangesNothingAtAll()
    {
        // The gate for the whole feature: a run that never names -compliance
        // is byte-identical to before it existed, so Correct with an empty
        // Except — Emulates answers false for every quirk.
        ComplianceOptions c = StockArgs.ParseVbsp([Map]).Options.Compliance;

        Assert.Equal(ComplianceOptions.Correct, c);
        Assert.Empty(c.Except);
        foreach (StockQuirk quirk in Enum.GetValues<StockQuirk>())
        {
            Assert.False(c.Emulates(quirk));
        }
    }

    // ---- the override tokens ----

    [Fact]
    public void PlusQuirkUnderCorrectDemandsStockBehaviourForThatQuirk()
    {
        // The seam the flip facts use programmatically, now reachable from a
        // command line: -compliance correct,+KdZero… = correct everywhere but
        // stock for this one quirk, exactly ComplianceOptions.Flipping's
        // answer for the same quirk.
        ComplianceOptions c = ParseVbsp($"correct,+{Quirk}");

        Assert.Equal(CompliancePolicy.Correct, c.Policy);
        Assert.Equal([Enum.Parse<StockQuirk>(Quirk)], c.Except);
        Assert.True(c.Emulates(Enum.Parse<StockQuirk>(Quirk)));
        Assert.Equal(ComplianceOptions.Correct.Flipping(Enum.Parse<StockQuirk>(Quirk)), c);
    }

    [Fact]
    public void MinusQuirkUnderStockDemandsCorrectBehaviourForThatQuirk()
    {
        ComplianceOptions c = ParseVbsp($"stock,-{Quirk}");

        Assert.Equal(CompliancePolicy.Stock, c.Policy);
        Assert.Equal([Enum.Parse<StockQuirk>(Quirk)], c.Except);
        Assert.False(c.Emulates(Enum.Parse<StockQuirk>(Quirk)));
        // Everything else still behaves as stock.
        Assert.True(c.Emulates(StockQuirk.BaseWindingNormalise));
    }

    [Fact]
    public void ATokenNamingTheBaselineSideAddsNothing()
    {
        // +Quirk under stock asks for what stock already does: Except stays
        // empty, so Emulates is the plain baseline answer and the compile is
        // an ordinary stock compile.
        ComplianceOptions plus = ParseVbsp($"stock,+{Quirk}");
        ComplianceOptions minus = ParseVbsp($"correct,-{Quirk}");

        Assert.Equal(ComplianceOptions.Stock, plus);
        Assert.Equal(ComplianceOptions.Correct, minus);
    }

    [Fact]
    public void SeveralOverridesCompose()
    {
        ComplianceOptions c = ParseVbsp($"correct,+{Quirk},+BaseWindingNormalise");

        Assert.Equal(2, c.Except.Count);
        Assert.True(c.Emulates(Enum.Parse<StockQuirk>(Quirk)));
        Assert.True(c.Emulates(StockQuirk.BaseWindingNormalise));
        Assert.False(c.Emulates(StockQuirk.NodeAreaWrittenBeforeSet));
    }

    [Fact]
    public void TheLastTokenForAQuirkWins()
    {
        // Last-writer-wins like every other token pair in the parser. Under
        // 'correct', the last token's wanted side lands in Except iff it
        // differs from the baseline:
        Assert.Empty(ParseVbsp($"correct,+{Quirk},-{Quirk}").Except);
        Assert.Equal([Enum.Parse<StockQuirk>(Quirk)], ParseVbsp($"correct,-{Quirk},+{Quirk}").Except);

        // Same rule under the other baseline, where the polarity inverts.
        Assert.Empty(ParseVbsp($"stock,-{Quirk},+{Quirk}").Except);
        Assert.Equal([Enum.Parse<StockQuirk>(Quirk)], ParseVbsp($"stock,+{Quirk},-{Quirk}").Except);
    }

    [Fact]
    public void QuirkNamesAreCaseInsensitiveAndWhitespaceTolerant()
    {
        ComplianceOptions c = ParseVbsp($" correct , + {Quirk.ToUpperInvariant()} ");

        Assert.Equal(CompliancePolicy.Correct, c.Policy);
        Assert.Single(c.Except);
        Assert.True(c.Emulates(Enum.Parse<StockQuirk>(Quirk)));
    }

    // ---- refusals ----

    [Fact]
    public void AnUnknownQuirkIsAnErrorNamingTheListCommand()
    {
        StockArgsResult<VbspOptions> r =
            StockArgs.ParseVbsp(["-compliance", "correct,+NotAQuirk", Map]);

        Assert.True(r.HasErrors);
        Assert.Contains(
            r.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("NotAQuirk", StringComparison.Ordinal)
                && d.Message.Contains("-listcompliance", StringComparison.Ordinal));
    }

    [Fact]
    public void ATokenWithoutASignIsAnError()
    {
        StockArgsResult<VbspOptions> r =
            StockArgs.ParseVbsp(["-compliance", "correct," + Quirk, Map]);

        Assert.True(r.HasErrors);
    }

    [Fact]
    public void AnUnknownModeIsStillRefusedEvenWithGoodOverrides()
    {
        StockArgsResult<VbspOptions> r =
            StockArgs.ParseVbsp(["-compliance", $"stcok,+{Quirk}", Map]);

        Assert.True(r.HasErrors);
        Assert.Equal(CompliancePolicy.Correct, r.Options.Compliance.Policy);
    }

    [Fact]
    public void TheOverridesAreSharedByAllThreeTools()
    {
        // One parser seam: vvis and vrad take the same spelling.
        Assert.Single(
            StockArgs.ParseVvis(["-compliance", $"stock,-{Quirk}", Map]).Options.Compliance.Except);
        Assert.Single(
            StockArgs.ParseVrad(["-compliance", $"stock,-{Quirk}", Map]).Options.Compliance.Except);
    }

    [Fact]
    public void TheCommaFormDoesNotBreakTheMapPath()
    {
        StockArgsResult<VbspOptions> r =
            StockArgs.ParseVbsp(["-compliance", $"stock,-{Quirk}", Map]);

        Assert.Equal(Map, r.MapPath);
        Assert.False(r.HasErrors);
    }
}
