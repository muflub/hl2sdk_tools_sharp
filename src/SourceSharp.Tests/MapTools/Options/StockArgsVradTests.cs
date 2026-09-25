using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// <see cref="StockArgs.ParseVrad"/> against the spellings
/// <c>src/utils/vrad/vrad.cpp</c> accepts, plus <c>-both</c> from
/// <c>vrad_launcher</c>.
/// </summary>
public class StockArgsVradTests
{
    private const string Map = "maps/testmap.bsp";

    public static TheoryData<string, string, bool> BooleanFlags => new()
    {
        { "-v", nameof(VradOptions.Verbose), true },
        { "-verbose", nameof(VradOptions.Verbose), true },
        { "-fast", nameof(VradOptions.Fast), true },
        { "-noextra", nameof(VradOptions.Supersample), false },
        { "-debugextra", nameof(VradOptions.DebugExtra), true },
        { "-fastambient", nameof(VradOptions.FastAmbient), true },
        { "-centersamples", nameof(VradOptions.CenterSamples), true },
        { "-dlightmap", nameof(VradOptions.SeparateDirectLightmap), true },
        { "-noskyboxrecurse", nameof(VradOptions.NoSkyboxRecurse), true },
        { "-onlydetail", nameof(VradOptions.OnlyDetail), true },
        { "-nodetaillight", nameof(VradOptions.NoDetailLighting), true },
        { "-rederrors", nameof(VradOptions.ShowErrorsInRed), true },
        { "-StaticPropLighting", nameof(VradOptions.StaticPropLighting), true },
        { "-StaticPropPolys", nameof(VradOptions.StaticPropPolys), true },
        { "-StaticPropNormals", nameof(VradOptions.StaticPropNormals), true },
        { "-OnlyStaticProps", nameof(VradOptions.OnlyStaticProps), true },
        { "-nossprops", nameof(VradOptions.DisablePropSelfShadowing), true },
        { "-textureshadows", nameof(VradOptions.TextureShadows), true },
        { "-LargeDispSampleRadius", nameof(VradOptions.LargeDispSampleRadius), true },
    };

    [Theory]
    [MemberData(nameof(BooleanFlags))]
    public void ABooleanFlagSetsExactlyItsOwnOption(string flag, string property, bool expected)
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad([flag, Map]);

        Assert.Empty(result.Diagnostics);
        OptionAssert.OnlyChanged(result.Options, VradOptions.Default, property, expected);
    }

    [Fact]
    public void HdrAsksForHdrOnly()
    {
        // vrad.cpp:2624
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-hdr", Map]);

        Assert.Equal(VradLightingRange.Hdr, result.Options.Range);
    }

    [Fact]
    public void LdrAsksForLdrOnly()
    {
        // vrad.cpp:2628
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-ldr", Map]);

        Assert.Equal(VradLightingRange.Ldr, result.Options.Range);
    }

    [Fact]
    public void BothAsksForOneCompileProducingBothRanges()
    {
        // vrad_launcher.cpp:68 runs the whole DLL twice for this. Plan 4p
        // shares the geometry, KD-tree, patches and transfers instead, so it
        // is one compile here.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-both", Map]);

        Assert.Equal(VradLightingRange.Both, result.Options.Range);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void TheLastRangeFlagOnTheLineWins()
    {
        // Each of stock's branches calls SetHDRMode outright, so the last one
        // parsed is the mode.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-hdr", "-ldr", Map]);

        Assert.Equal(VradLightingRange.Ldr, result.Options.Range);
    }

    [Fact]
    public void FinalIsExactlyExtraSkySixteen()
    {
        // vrad.cpp:2514 sets g_flSkySampleScale = 16.0 and nothing else, which
        // is why there is no separate Final option.
        StockArgsResult<VradOptions> fromFinal = StockArgs.ParseVrad(["-final", Map]);
        StockArgsResult<VradOptions> fromExtraSky = StockArgs.ParseVrad(["-extrasky", "16", Map]);

        Assert.Equal(fromExtraSky.Options, fromFinal.Options);
    }

    [Fact]
    public void ExtraSkyParsesItsMultiplier()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-extrasky", "4", Map]);

        OptionAssert.OnlyChanged(result.Options, VradOptions.Default, nameof(VradOptions.SkySampleScale), 4.0f);
    }

    [Fact]
    public void BounceParsesItsCount()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-bounce", "25", Map]);

        OptionAssert.OnlyChanged(result.Options, VradOptions.Default, nameof(VradOptions.Bounces), 25);
    }

    [Fact]
    public void ZeroBouncesIsLegalAndMeansDirectLightOnly()
    {
        // vrad.cpp:2448 refuses only a NEGATIVE value.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-bounce", "0", Map]);

        Assert.Equal(0, result.Options.Bounces);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ANegativeBounceCountIsAnError()
    {
        // vrad.cpp:2448-2452
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-bounce", "-1", Map]);

        Assert.Equal(StockArgsCodes.ValueOutOfRange, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(100, result.Options.Bounces);
    }

    [Fact]
    public void SmoothKeepsTheAngleInDegrees()
    {
        // vrad.cpp:2538 stores cos(radians(n)). The option holds what was
        // typed.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-smooth", "60", Map]);

        Assert.Equal(60.0f, result.Options.SmoothingAngleDegrees);
    }

    [Fact]
    public void LuxelDensityKeepsWhatWasTypedRatherThanStocksReciprocal()
    {
        // vrad.cpp:2555-2556 turns 2.0 into 0.5 while the usage text at
        // vrad.cpp:2857-2858 claims a value above 1.0 "will be ignored".
        // Neither is what the other says, so the option records the request
        // and the stage applies the reciprocal.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-luxeldensity", "2", Map]);

        Assert.Equal(2.0f, result.Options.LuxelDensity);
    }

    [Fact]
    public void SoftSunKeepsTheExtentInDegrees()
    {
        // vrad.cpp:2581 stores sin(radians(n)).
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-softsun", "5", Map]);

        Assert.Equal(5.0f, result.Options.SunAngularExtentDegrees);
    }

    [Fact]
    public void MaxChopParsesItsValue()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-maxchop", "16", Map]);

        OptionAssert.OnlyChanged(result.Options, VradOptions.Default, nameof(VradOptions.MaxChop), 16.0f);
    }

    [Fact]
    public void ChopBeforeMaxChopIsClampedByTheDEFAULTMaxChop()
    {
        // vrad.cpp:2659 clamps minchop against maxchop AS IT STANDS, so the
        // two flags are order-dependent. Here maxchop is still 4.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-chop", "8", "-maxchop", "16", Map]);

        Assert.Equal(4.0f, result.Options.MinChop);
        Assert.Equal(16.0f, result.Options.MaxChop);
    }

    [Fact]
    public void ChopAfterMaxChopIsClampedByTheNewMaxChop()
    {
        // The same two flags, swapped: now maxchop is 16 when -chop is read.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-maxchop", "16", "-chop", "8", Map]);

        Assert.Equal(8.0f, result.Options.MinChop);
    }

    [Fact]
    public void ChopBelowOneIsAnError()
    {
        // vrad.cpp:2653-2657
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-chop", "0.5", Map]);

        Assert.Equal(StockArgsCodes.ValueOutOfRange, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void MaxChopBelowOneIsAnError()
    {
        // vrad.cpp:2636-2640
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-maxchop", "0", Map]);

        Assert.Equal(StockArgsCodes.ValueOutOfRange, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void DispChopParsesItsValue()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-dispchop", "16", Map]);

        Assert.Equal(16.0f, result.Options.DispChop);
    }

    [Fact]
    public void DispChopBelowOneIsAnError()
    {
        // vrad.cpp:2670-2675 -- the branch whose own error message misspells
        // the flag as "-dipschop".
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-dispchop", "0.5", Map]);

        Assert.Equal(StockArgsCodes.ValueOutOfRange, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void DispPatchRadiusBelowTenIsAnError()
    {
        // vrad.cpp:2687-2692
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-disppatchradius", "9", Map]);

        Assert.Equal(StockArgsCodes.ValueOutOfRange, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void DispPatchRadiusAtTenIsAccepted()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-disppatchradius", "10", Map]);

        Assert.Equal(10.0f, result.Options.MaxDispPatchRadius);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void MaxDispSampleSizeParsesItsValue()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-maxdispsamplesize", "256", Map]);

        Assert.Equal(256.0f, result.Options.MaxDispSampleSize);
    }

    [Fact]
    public void LightsRecordsItsPath()
    {
        // vrad.cpp:2482
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-lights", "extra.rad", Map]);

        Assert.Equal("extra.rad", result.Options.LightsFile);
    }

    [Fact]
    public void AFlagParsesInMixedCase()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-STATICPROPPOLYS", Map]);

        Assert.True(result.Options.StaticPropPolys);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void DumpIsAcceptedAndIgnoredInAnyCaseEvenThoughStockIsCaseSensitiveAboutIt()
    {
        // vrad.cpp:2415 uses strcmp, so stock accepts -dump and rejects -DUMP.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-DUMP", Map]);

        CompileDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.DroppedOption, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void StopOnExitIsAcceptedAndIgnored()
    {
        // vrad.cpp:2602 waits for a keypress, which a library cannot do.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-StopOnExit", Map]);

        Assert.Equal(StockArgsCodes.DroppedOption, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void ThreadsIsRecordedRatherThanDropped()
    {
        // IT USED TO BE DROPPED, with the diagnostic "accepted and ignored:
        // use CompileParallelism" -- and that was FALSE. `ssmap vrad` read the
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
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-threads", "12", Map]);

        Assert.Equal(12, result.Threads);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(Map, result.MapPath);
    }

    [Fact]
    public void ThreadsIsNullWhenTheFlagIsAbsent()
    {
        Assert.Null(StockArgs.ParseVrad([Map]).Threads);
    }

    [Fact]
    public void ExtraIsUnknownBecauseThereIsNoSuchStockFlag()
    {
        // There is only -noextra and -extrasky in this tree.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-extra", Map]);

        Assert.Equal(StockArgsCodes.UnknownOption, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void StaticPropLightingFinalIsUnknownBecauseItIsNotInThisTree()
    {
        // The spelling appears nowhere under src/utils.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-staticproplightingfinal", Map]);

        Assert.Equal(StockArgsCodes.UnknownOption, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void CoringIsUnknownBecauseItsBranchIsDebugOnly()
    {
        // vrad.cpp:2702 is `#if ALLOWDEBUGOPTIONS`, defined as (0 || _DEBUG)
        // at vrad.cpp:23, so a release vrad rejects it too.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-coring", "0.5", Map]);

        Assert.Contains(result.Diagnostics, d => d.Code == StockArgsCodes.UnknownOption);
    }

    [Fact]
    public void TheFinalPresetMatchesParsingBothAndFinal()
    {
        Assert.Equal(VradOptions.FinalDefault, StockArgs.ParseVrad(["-both", "-final", Map]).Options);
    }
}
