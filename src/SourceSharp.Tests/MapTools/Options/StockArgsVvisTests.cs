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
/// <see cref="StockArgs.ParseVvis"/> against the spellings the reference
/// vvis accepts.
/// </summary>
public class StockArgsVvisTests
{
    private const string Map = "maps/testmap.bsp";

    [Fact]
    public void FastSetsExactlyFast()
    {
        // -fast sets exactly one flag.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-fast", Map]);

        OptionAssert.OnlyChanged(result.Options, VvisOptions.Default, nameof(VvisOptions.Fast), true);
    }

    [Fact]
    public void NoSortSetsExactlyNoSort()
    {
        // -nosort sets exactly one flag.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-nosort", Map]);

        OptionAssert.OnlyChanged(result.Options, VvisOptions.Default, nameof(VvisOptions.NoSort), true);
    }

    [Fact]
    public void VerboseSetsExactlyVerbose()
    {
        // -verbose sets exactly one flag -- both spellings.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-verbose", Map]);

        OptionAssert.OnlyChanged(result.Options, VvisOptions.Default, nameof(VvisOptions.Verbose), true);
    }

    [Fact]
    public void ShortVerboseIsTheSameFlag()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-v", Map]);

        Assert.True(result.Options.Verbose);
    }

    [Fact]
    public void RadiusOverrideKeepsTheRadiusUnsquared()
    {
        // The reference parser squares the radius on the way in "so distance
        // check can be
        // squared". The option holds the radius that was asked for; squaring
        // is the stage's.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-radius_override", "2048", Map]);

        Assert.Equal(2048.0f, result.Options.RadiusOverride);
    }

    [Fact]
    public void RadiusOverrideIsAbsentWhenNotGiven()
    {
        // Stock carries a separate use-radius flag; null says
        // the same thing without a second field that can disagree.
        Assert.Null(StockArgs.ParseVvis([Map]).Options.RadiusOverride);
    }

    [Fact]
    public void TraceParsesBothClusterNumbers()
    {
        // -trace takes both cluster numbers.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-trace", "12", "34", Map]);

        Assert.Equal((12, 34), result.Options.Trace);
    }

    [Fact]
    public void ACleanLineProducesNoDiagnostics()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-fast", "-nosort", Map]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(Map, result.MapPath);
    }

    [Fact]
    public void AFlagParsesInMixedCase()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-RaDiUs_OvErRiDe", "512", Map]);

        Assert.Equal(512.0f, result.Options.RadiusOverride);
    }

    [Fact]
    public void ThreadsIsRecordedRatherThanDropped()
    {
        // IT USED TO BE DROPPED, with the diagnostic "accepted and ignored:
        // use CompileParallelism" -- and that was FALSE. `ssmap vvis` read the
        // flag itself in a second, private scan and honoured it, so the message
        // said the opposite of what happened. Measured on one map afterwards:
        // 0.595 s at -threads 1, 0.286 at 4, 0.271 at 32.
        //
        // The cost of the lie was not cosmetic. A round of crash probing read
        // the diagnostic, concluded every thread count had run at the same
        // degree, and discarded a real signal on that basis.
        //
        // So the value is RECORDED here, for the host to map onto
        // CompileParallelism.MaxDegree, and there is one parser instead of two.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-threads", "12", Map]);

        Assert.Equal(12, result.Threads);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(Map, result.MapPath);
    }

    [Fact]
    public void ThreadsIsNullWhenTheFlagIsAbsent()
    {
        Assert.Null(StockArgs.ParseVvis([Map]).Threads);
    }

    [Fact]
    public void TmpinIsAcceptedAndIgnored()
    {
        // The reference tool hardcodes /tmp as the input base.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-tmpin", Map]);

        Assert.Equal(StockArgsCodes.DroppedOption, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(VvisOptions.Default, result.Options);
    }

    [Fact]
    public void MpiFlagsAreAcceptedAndIgnored()
    {
        // The -mpi prefix branch, compiled in only for the parallel build.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-mpi", Map]);

        Assert.Equal(StockArgsCodes.DroppedOption, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void MpiPasswordConsumesItsValue()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-mpi_pw", "secret", Map]);

        Assert.Equal(Map, result.MapPath);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void AnUnknownFlagIsAnErrorAndDoesNotThrow()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-tighten", Map]);

        Assert.Equal(StockArgsCodes.UnknownOption, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void TightAndLooseHaveNoStockSpellingAndStayAtTheDefaultThroughThisParser()
    {
        // Neither is a stock option -- see the remarks on VvisOptions.Tighten
        // -- so both spellings are unknown-option errors here, and the options
        // keep the record's promoted default (tightening ON, vis-repair
        // rung2). The managed command owns the switches and takes them off
        // the line before this parser sees them.
        string[] spellings = ["-tighten", "-loose"];
        foreach (string spelling in spellings)
        {
            StockArgsResult<VvisOptions> result = StockArgs.ParseVvis([spelling, Map]);

            Assert.Equal(StockArgsCodes.UnknownOption, Assert.Single(result.Diagnostics).Code);
            Assert.True(result.Options.Tighten);
        }
    }

    [Fact]
    public void FastFlowIsOffUnlessAskedFor()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis([Map]);

        Assert.Null(result.Options.FastFlowSteps);
        Assert.Equal(VvisOptions.Default, result.Options);
    }

    [Theory]
    [InlineData("-fastflow", VvisOptions.DefaultFastFlowSteps)]
    [InlineData("-FastFlow", VvisOptions.DefaultFastFlowSteps)]
    [InlineData("-fastflow=1000", 1000)]
    [InlineData("-fastflow=0", 0)]
    [InlineData("-fastflow=5000", 5000)]
    [InlineData("-FASTFLOW=20000", 20000)]
    [InlineData("-fastflow=+7", 7)]
    [InlineData("-fastflow=007", 7)]
    [InlineData("-fastflow=2147483647", int.MaxValue)]
    public void FastFlowParsesItsStepCount(string spelling, int steps)
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis([spelling, Map]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(VvisOptions.Default with { FastFlowSteps = steps }, result.Options);
        Assert.Equal(Map, result.MapPath);
    }

    [Theory]
    [InlineData("-fastflow=", StockArgsCodes.MissingValue)]
    [InlineData("-fastflow=many", StockArgsCodes.MalformedValue)]
    [InlineData("-fastflow=1.5", StockArgsCodes.MalformedValue)]
    [InlineData("-fastflow=1e3", StockArgsCodes.MalformedValue)]
    [InlineData("-fastflow= 5", StockArgsCodes.MalformedValue)]
    [InlineData("-fastflow=-", StockArgsCodes.MalformedValue)]
    [InlineData("-fastflow=-1", StockArgsCodes.ValueOutOfRange)]
    [InlineData("-fastflow=-5000", StockArgsCodes.ValueOutOfRange)]
    [InlineData("-fastflow=2147483648", StockArgsCodes.ValueOutOfRange)]
    [InlineData("-fastflow=99999999999999999999", StockArgsCodes.ValueOutOfRange)]
    [InlineData("-fastflow=-99999999999999999999", StockArgsCodes.ValueOutOfRange)]
    public void ABadStepCountIsAUsageErrorThatNamesTheFlag(string spelling, string code)
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis([spelling, Map]);

        CompileDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("-fastflow", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(result.HasErrors);
        Assert.Null(result.Options.FastFlowSteps);

        // The bad token is not taken for the map.
        Assert.Equal(Map, result.MapPath);
    }

    [Fact]
    public void TheValueIsPartOfTheFlagSoTheNextTokenStaysTheMap()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-fastflow", "5000"]);

        // "-fastflow 5000" is -fastflow and a map named 5000, not a count.
        Assert.Equal(VvisOptions.DefaultFastFlowSteps, result.Options.FastFlowSteps);
        Assert.Equal("5000", result.MapPath);
    }

    [Theory]
    [InlineData(new[] { "-fastflow=5000", "-fastflow" }, VvisOptions.DefaultFastFlowSteps)]
    [InlineData(new[] { "-fastflow", "-fastflow=5000" }, 5000)]
    [InlineData(new[] { "-fastflow=0", "-fastflow=20000" }, 20000)]
    public void GivenTwiceTheLastWins(string[] flags, int steps)
    {
        // As every other option here: a later value replaces an earlier one.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis([.. flags, Map]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(steps, result.Options.FastFlowSteps);
    }

    [Fact]
    public void ABadCountAfterAGoodOneLeavesTheGoodOne()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-fastflow=5000", "-fastflow=x", Map]);

        Assert.True(result.HasErrors);
        Assert.Equal(5000, result.Options.FastFlowSteps);
    }

    [Fact]
    public void FastFlowIsNotStocksFast()
    {
        // Two different switches: -fast skips the flow, -fastflow shortens it.
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-fastflow", "-fast", Map]);

        Assert.Equal(VvisOptions.DefaultFastFlowSteps, result.Options.FastFlowSteps);
        Assert.True(result.Options.Fast);
        Assert.Null(StockArgs.ParseVvis(["-fast", Map]).Options.FastFlowSteps);
    }

    [Fact]
    public void AnotherFlagStartingWithFastFlowIsStillUnknown() =>
        Assert.Equal(
            StockArgsCodes.UnknownOption,
            Assert.Single(StockArgs.ParseVvis(["-fastflows", Map]).Diagnostics).Code);

    [Fact]
    public void TheFastPresetMatchesParsingTheFastFlag()
    {
        Assert.Equal(VvisOptions.FastDefault, StockArgs.ParseVvis(["-fast", Map]).Options);
    }
}
