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
    public void TheFastPresetMatchesParsingTheFastFlag()
    {
        Assert.Equal(VvisOptions.FastDefault, StockArgs.ParseVvis(["-fast", Map]).Options);
    }
}
