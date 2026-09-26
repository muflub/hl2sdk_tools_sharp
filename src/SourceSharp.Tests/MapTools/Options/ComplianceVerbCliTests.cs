//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// The per-quirk <c>-compliance</c> flag, proven end to end through EACH verb's
/// own parser entry into that verb's own options record (§11a).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ComplianceOverrideFlagTests"/> pins the grammar at the shared
/// parser (<see cref="StockArgs"/>'s <c>TryCompliance</c>). What lives here is
/// the wiring behind it: facts that start from each verb's real entry point —
/// <see cref="StockArgs.ParseVbsp"/>, <see cref="StockArgs.ParseVvis"/>,
/// <see cref="StockArgs.ParseVrad"/>, <see cref="AllCommand.Parse"/> — and land
/// on the verb's own options record
/// (<see cref="VbspOptions.Compliance"/>, <see cref="VvisOptions.Compliance"/>,
/// <see cref="VradOptions.Compliance"/>) with the exact <see cref="ComplianceOptions.Except"/>
/// set the library-side flip would have built. A flag that parses in the shared
/// code and is then dropped by one verb's arm is exactly what these catch.
/// </para>
/// <para>
/// A second quirk witnesses the rows that need one the vbsp arm treats
/// specially: <see cref="StockQuirk.CollisionCookerSinglePrecision"/> also
/// decides the managed cooker's precision, so the <c>--vbsp</c>-section facts
/// use it as an ordinary quirk name while the cooker-side effect itself stays
/// with the library facts (<c>ManagedCollisionCookerTests</c>).
/// </para>
/// <para>
/// <b>Atomicity is the parser's decision.</b> <c>TryCompliance</c> builds the
/// record locally and assigns it only after every token parsed, so a refused
/// <c>-compliance</c> — unknown mode or unknown quirk, first token or tenth —
/// leaves the policy exactly as it found it on every stage. That
/// all-or-nothing is what <see cref="AChainUnknownQuirkRefusesEveryStage"/>
/// pins (xcheck P11a-1 adjudication: a behavior decision belongs in the doc,
/// not only in a fact name).
/// </para>
/// </remarks>
public sealed class ComplianceVerbCliTests
{
    private const string Map = "maps/testmap.bsp";

    /// <summary>A quirk whose site the vbsp arm's cooker path reads.</summary>
    private const string CookerQuirk = nameof(StockQuirk.CollisionCookerSinglePrecision);
    private const string Quirk = nameof(StockQuirk.BaseWindingNormalise);

    // ---- vvis ----

    [Fact]
    public void VvisPlusQuirkReachesTheVvisRecordWithTheFlipsExceptSet()
    {
        StockArgsResult<VvisOptions> parsed =
            StockArgs.ParseVvis(["-compliance", $"correct,+{Quirk}", Map]);

        Assert.False(parsed.HasErrors);
        Assert.Equal(CompliancePolicy.Correct, parsed.Options.Compliance.Policy);
        Assert.Equal([StockQuirk.BaseWindingNormalise], parsed.Options.Compliance.Except);
        // The library-side flip and the command line must be the SAME value,
        // which is what makes the flip facts' evidence the CLI's evidence too.
        Assert.Equal(ComplianceOptions.Correct.Flipping(StockQuirk.BaseWindingNormalise),
            parsed.Options.Compliance);
    }

    [Fact]
    public void VvisMinusQuirkUnderStockInvertsThatQuirkOnly()
    {
        ComplianceOptions c =
            StockArgs.ParseVvis(["-compliance", $"stock,-{Quirk}", Map]).Options.Compliance;

        Assert.Equal(CompliancePolicy.Stock, c.Policy);
        Assert.Equal([StockQuirk.BaseWindingNormalise], c.Except);
        Assert.False(c.Emulates(StockQuirk.BaseWindingNormalise));
        Assert.True(c.Emulates(StockQuirk.LeafAmbientSampleCountAxes));
    }

    [Fact]
    public void VvisLastTokenForAQuirkWins()
    {
        // Parsed through PARSEVVIS: a last-token fact that ran the vbsp parser
        // would prove nothing about vvis (xcheck P11a-1 adjudication).
        ComplianceOptions off = StockArgs.ParseVvis(
            ["-compliance", $" correct , + {Quirk.ToUpperInvariant()} , - {Quirk} ", Map]).Options.Compliance;
        ComplianceOptions on = StockArgs.ParseVvis(
            ["-compliance", $"CORRECT,+{Quirk.ToLowerInvariant()}", Map]).Options.Compliance;

        Assert.Empty(off.Except);
        Assert.Equal([StockQuirk.BaseWindingNormalise], on.Except);
    }

    [Fact]
    public void VvisUnknownQuirkNamesTheTokenAndPointsAtListCompliance()
    {
        StockArgsResult<VvisOptions> parsed =
            StockArgs.ParseVvis(["-compliance", "correct,+NotAQuirk", Map]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(
            parsed.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("NotAQuirk", StringComparison.Ordinal)
                && d.Message.Contains("-listcompliance", StringComparison.Ordinal));
    }

    [Fact]
    public void VvisUnknownModeNamesTheTokenAndLeavesThePolicyAlone()
    {
        StockArgsResult<VvisOptions> parsed =
            StockArgs.ParseVvis(["-compliance", $"stcok,+{Quirk}", Map]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(
            parsed.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("stcok", StringComparison.Ordinal));
        Assert.Equal(ComplianceOptions.Correct, parsed.Options.Compliance);
    }

    // ---- vrad ----

    [Fact]
    public void VradPlusQuirkReachesTheVradRecordWithTheFlipsExceptSet()
    {
        StockArgsResult<VradOptions> parsed =
            StockArgs.ParseVrad(["-compliance", $"correct,+{Quirk}", Map]);

        Assert.False(parsed.HasErrors);
        Assert.Equal(CompliancePolicy.Correct, parsed.Options.Compliance.Policy);
        Assert.Equal([StockQuirk.BaseWindingNormalise], parsed.Options.Compliance.Except);
        Assert.Equal(ComplianceOptions.Correct.Flipping(StockQuirk.BaseWindingNormalise),
            parsed.Options.Compliance);
    }

    [Fact]
    public void VradMinusQuirkUnderStockInvertsThatQuirkOnly()
    {
        ComplianceOptions c =
            StockArgs.ParseVrad(["-compliance", $"stock,-{Quirk}", Map]).Options.Compliance;

        Assert.Equal(CompliancePolicy.Stock, c.Policy);
        Assert.Equal([StockQuirk.BaseWindingNormalise], c.Except);
        Assert.False(c.Emulates(StockQuirk.BaseWindingNormalise));
        Assert.True(c.Emulates(StockQuirk.LeafAmbientSampleCountAxes));
    }

    [Fact]
    public void VradLastTokenForAQuirkWinsAndNamesAreCaseInsensitive()
    {
        ComplianceOptions off = StockArgs.ParseVrad(
            ["-compliance", $" Stock , - {Quirk} , + {Quirk.ToUpperInvariant()} ", Map]).Options.Compliance;
        ComplianceOptions on = StockArgs.ParseVrad(
            ["-compliance", $"STOCK,-{Quirk.ToLowerInvariant()}", Map]).Options.Compliance;

        Assert.Empty(off.Except);
        Assert.Equal([StockQuirk.BaseWindingNormalise], on.Except);
    }

    [Fact]
    public void VradUnknownQuirkNamesTheTokenAndPointsAtListCompliance()
    {
        StockArgsResult<VradOptions> parsed =
            StockArgs.ParseVrad(["-compliance", "correct,+NotAQuirk", Map]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(
            parsed.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("NotAQuirk", StringComparison.Ordinal)
                && d.Message.Contains("-listcompliance", StringComparison.Ordinal));
    }

    [Fact]
    public void VradUnknownModeNamesTheTokenAndLeavesThePolicyAlone()
    {
        StockArgsResult<VradOptions> parsed =
            StockArgs.ParseVrad(["-compliance", $"corect,-{Quirk}", Map]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(
            parsed.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("corect", StringComparison.Ordinal));
        Assert.Equal(ComplianceOptions.Correct, parsed.Options.Compliance);
    }

    // ---- vbsp (the arm the override facts already parse through: the record
    // is pinned here, the library below) ----

    [Fact]
    public void VbspPlusQuirkReachesTheVbspRecordWithTheFlipsExceptSet()
    {
        StockArgsResult<VbspOptions> parsed =
            StockArgs.ParseVbsp(["-compliance", $"correct,+{Quirk}", Map]);

        Assert.False(parsed.HasErrors);
        Assert.Equal(ComplianceOptions.Correct.Flipping(StockQuirk.BaseWindingNormalise),
            parsed.Options.Compliance);
    }

    [Fact]
    public void VbspUnknownQuirkNamesTheTokenAndPointsAtListCompliance()
    {
        StockArgsResult<VbspOptions> parsed =
            StockArgs.ParseVbsp(["-compliance", "correct,+NotAQuirk", Map]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(
            parsed.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("NotAQuirk", StringComparison.Ordinal)
                && d.Message.Contains("-listcompliance", StringComparison.Ordinal));
    }

    [Fact]
    public void VbspLastTokenForAQuirkWinsAndNamesAreCaseInsensitive()
    {
        ComplianceOptions off = StockArgs.ParseVbsp(
            ["-compliance", $" Correct , + {Quirk} , - {Quirk.ToUpperInvariant()} ", Map]).Options.Compliance;
        ComplianceOptions on = StockArgs.ParseVbsp(
            ["-compliance", $"STOCK,-{Quirk.ToLowerInvariant()}", Map]).Options.Compliance;

        Assert.Empty(off.Except);
        Assert.Equal([StockQuirk.BaseWindingNormalise], on.Except);
    }

    [Fact]
    public void VbspUnknownModeNamesTheTokenAndLeavesThePolicyAlone()
    {
        StockArgsResult<VbspOptions> parsed =
            StockArgs.ParseVbsp(["-compliance", $"corect,+{Quirk}", Map]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(
            parsed.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("corect", StringComparison.Ordinal));
        Assert.Equal(ComplianceOptions.Correct, parsed.Options.Compliance);
    }


    [Fact]
    public void ASectionsPerQuirkComplianceWinsForItsStageOnly()
{
        // The chain asks for correct with the bevel flipped to stock, and only
        // vrad's section then asks for stock with the bevel back on correct.
        // The section's tokens are concatenated after the chain's for that
        // stage alone (AllCommand.cs:367-369), so vrad's later token wins and
        // it lands on plain stock; the two untouched stages keep the chain's
        // answer, which is the whole point of the section override.
        AllArgs parsed = AllCommand.Parse(
        [
            "-compliance", $"correct,+{Quirk}", "x.vmf",
            "--vrad", "-compliance", $"stock,-{Quirk}",
        ]);

        Assert.False(parsed.HasErrors);
        // vrad's own `-compliance stock,-Q` arrives after the chain's, so its
        // later tokens are the ones that stick: stock baseline, that quirk back
        // on correct. The two stages with no section keep the chain's answer.
        Assert.Equal(ComplianceOptions.Stock.Flipping(StockQuirk.BaseWindingNormalise), parsed.Vrad.Compliance);
        Assert.Equal(ComplianceOptions.Correct.Flipping(StockQuirk.BaseWindingNormalise), parsed.Vbsp.Compliance);
        Assert.Equal(ComplianceOptions.Correct.Flipping(StockQuirk.BaseWindingNormalise), parsed.Vvis.Compliance);
    }

    [Fact]
    public void ASectionMayMoveOneQuirkAgainstTheChainsStockBaseline()
    {
        AllArgs parsed = AllCommand.Parse(
        [
            "-compliance", "stock", "x.vmf",
            "--vbsp", "-compliance", $"stock,-{CookerQuirk}",
        ]);

        Assert.Equal(ComplianceOptions.Stock.Flipping(StockQuirk.CollisionCookerSinglePrecision),
            parsed.Vbsp.Compliance);
        Assert.Equal(ComplianceOptions.Stock, parsed.Vvis.Compliance);
        Assert.Equal(ComplianceOptions.Stock, parsed.Vrad.Compliance);
    }

    [Fact]
    public void ASectionsUnknownQuirkIsAnErrorNamingTheTokenAndTheListCommand()
    {
        AllArgs parsed = AllCommand.Parse(["x.vmf", "--vvis", "-compliance", "correct,+NotAQuirk"]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(
            parsed.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("NotAQuirk", StringComparison.Ordinal)
                && d.Message.Contains("-listcompliance", StringComparison.Ordinal));
    }

    [Fact]
    public void AChainUnknownQuirkRefusesEveryStage()
    {
        AllArgs parsed = AllCommand.Parse(["-compliance", $"stock,+{CookerQuirk},+NotAQuirk", "x.vmf"]);

        Assert.True(parsed.HasErrors);
        // Nothing half-applied: the refusal leaves each stage on the default.
        Assert.Equal(ComplianceOptions.Correct, parsed.Vbsp.Compliance);
        Assert.Equal(ComplianceOptions.Correct, parsed.Vvis.Compliance);
        Assert.Equal(ComplianceOptions.Correct, parsed.Vrad.Compliance);
    }
}
