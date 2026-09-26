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
/// <see cref="ComplianceOptions"/> and the <c>-compliance</c> flag.
/// </summary>
/// <remarks>
/// The load-bearing fact in here is
/// <see cref="DefaultIsCorrectNotStock"/>: every other default on the option
/// records is stock's, and this one is deliberately not, so a change of mind
/// about it must break a test rather than quietly change what every compile
/// produces.
/// </remarks>
public class ComplianceTests
{
    private const string Map = "maps/testmap.bsp";

    [Fact]
    public void DefaultIsCorrectNotStock()
    {
        Assert.Equal(CompliancePolicy.Correct, ComplianceOptions.Correct.Policy);
        Assert.Equal(CompliancePolicy.Correct, new ComplianceOptions().Policy);
    }

    [Fact]
    public void CorrectEmulatesNoQuirk()
    {
        foreach (StockQuirk quirk in Enum.GetValues<StockQuirk>())
        {
            Assert.False(ComplianceOptions.Correct.Emulates(quirk));
        }
    }

    [Fact]
    public void StockEmulatesEveryQuirk()
    {
        foreach (StockQuirk quirk in Enum.GetValues<StockQuirk>())
        {
            Assert.True(ComplianceOptions.Stock.Emulates(quirk));
        }
    }

    [Fact]
    public void EveryQuirkIsDistinctlyNamed()
    {
        // A duplicated enum value would silently alias two quirks onto one
        // switch, so a gate isolating one would be isolating both.
        StockQuirk[] all = Enum.GetValues<StockQuirk>();
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Equal(all.Length, Enum.GetNames<StockQuirk>().Distinct().Count());
    }

    [Fact]
    public void FlippingOneQuirkLeavesTheRestAlone()
    {
        ComplianceOptions isolated =
            ComplianceOptions.Stock.Flipping(StockQuirk.BaseWindingNormalise);

        Assert.False(isolated.Emulates(StockQuirk.BaseWindingNormalise));
        Assert.True(isolated.Emulates(StockQuirk.AddQuadSecondTriangleId));
        Assert.True(isolated.Emulates(StockQuirk.LeakFileUnnudgedOrigin));
    }

    [Fact]
    public void FlippingTwiceReturnsToThePolicy()
    {
        ComplianceOptions twice = ComplianceOptions.Correct
            .Flipping(StockQuirk.AddQuadSecondTriangleId)
            .Flipping(StockQuirk.AddQuadSecondTriangleId);

        Assert.False(twice.Emulates(StockQuirk.AddQuadSecondTriangleId));
        Assert.Empty(twice.Except);
    }

    [Fact]
    public void FlippingDoesNotMutateTheOriginal()
    {
        ComplianceOptions original = ComplianceOptions.Stock;
        _ = original.Flipping(StockQuirk.BaseWindingNormalise);

        Assert.True(original.Emulates(StockQuirk.BaseWindingNormalise));
        Assert.Empty(original.Except);
    }

    [Fact]
    public void VvisDefaultsToCorrect()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis([Map]);

        Assert.Equal(CompliancePolicy.Correct, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void VbspDefaultsToCorrect()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp([Map]);

        Assert.Equal(CompliancePolicy.Correct, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void VradDefaultsToCorrect()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad([Map]);

        Assert.Equal(CompliancePolicy.Correct, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void VvisAcceptsStock()
    {
        StockArgsResult<VvisOptions> result =
            StockArgs.ParseVvis(["-compliance", "stock", Map]);

        Assert.Equal(CompliancePolicy.Stock, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void VbspAcceptsStock()
    {
        StockArgsResult<VbspOptions> result =
            StockArgs.ParseVbsp(["-compliance", "stock", Map]);

        Assert.Equal(CompliancePolicy.Stock, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void VradAcceptsStock()
    {
        StockArgsResult<VradOptions> result =
            StockArgs.ParseVrad(["-compliance", "stock", Map]);

        Assert.Equal(CompliancePolicy.Stock, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ValueIsCaseInsensitiveLikeEveryOtherFlag()
    {
        StockArgsResult<VvisOptions> result =
            StockArgs.ParseVvis(["-COMPLIANCE", "Stock", Map]);

        Assert.Equal(CompliancePolicy.Stock, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ExplicitCorrectIsAcceptedAndIsNotAnError()
    {
        StockArgsResult<VradOptions> result =
            StockArgs.ParseVrad(["-compliance", "correct", Map]);

        Assert.Equal(CompliancePolicy.Correct, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void AnUnrecognisedValueIsAnErrorRatherThanASilentDefault()
    {
        // Stock's habit with atoi is to read nonsense as 0 and carry on. This
        // flag decides what the OUTPUT MEANS, so a typo must stop the compile
        // instead of quietly producing the other tool's answer.
        StockArgsResult<VvisOptions> result =
            StockArgs.ParseVvis(["-compliance", "stcok", Map]);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == StockArgsCodes.MalformedValue
                && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AnUnrecognisedValueLeavesThePolicyAtCorrect()
    {
        StockArgsResult<VvisOptions> result =
            StockArgs.ParseVvis(["-compliance", "stcok", Map]);

        Assert.Equal(CompliancePolicy.Correct, result.Options.Compliance.Policy);
    }

    [Fact]
    public void AMissingValueIsAnError()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis([Map, "-compliance"]);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == StockArgsCodes.MissingValue);
    }

    [Fact]
    public void TheValueIsConsumedAndNotTakenForAMapPath()
    {
        StockArgsResult<VvisOptions> result =
            StockArgs.ParseVvis(["-compliance", "stock", Map]);

        Assert.Equal(Map, result.MapPath);
        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Code == StockArgsCodes.TooManyMapPaths);
    }

    [Fact]
    public void TheLastSpellingWins()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(
            ["-compliance", "stock", "-compliance", "correct", Map]);

        Assert.Equal(CompliancePolicy.Correct, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ComplianceDoesNotDisturbTheOtherOptions()
    {
        StockArgsResult<VvisOptions> result =
            StockArgs.ParseVvis(["-fast", "-compliance", "stock", "-threads", "8", Map]);

        Assert.True(result.Options.Fast);
        Assert.Equal(8, result.Threads);
        Assert.Equal(CompliancePolicy.Stock, result.Options.Compliance.Policy);
        Assert.False(result.HasErrors);
    }
}
