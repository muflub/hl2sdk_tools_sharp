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
/// <see cref="StockArgs.ParseVbsp"/> against the spellings the reference
/// vbsp accepts.
/// </summary>
public class StockArgsVbspTests
{
    private const string Map = "maps/testmap.vmf";

    public static TheoryData<string, string> BooleanFlags => new()
    {
        { "-v", nameof(VbspOptions.Verbose) },
        { "-verbose", nameof(VbspOptions.Verbose) },
        { "-verboseentities", nameof(VbspOptions.VerboseEntities) },
        { "-noweld", nameof(VbspOptions.NoWeld) },
        { "-nocsg", nameof(VbspOptions.NoCsg) },
        { "-noshare", nameof(VbspOptions.NoShare) },
        { "-notjunc", nameof(VbspOptions.NoTJunc) },
        { "-nowater", nameof(VbspOptions.NoWater) },
        { "-noopt", nameof(VbspOptions.NoOpt) },
        { "-noprune", nameof(VbspOptions.NoPrune) },
        { "-nomerge", nameof(VbspOptions.NoMerge) },
        { "-nomergewater", nameof(VbspOptions.NoMergeWater) },
        { "-nosubdiv", nameof(VbspOptions.NoSubdiv) },
        { "-nodetail", nameof(VbspOptions.NoDetail) },
        { "-fulldetail", nameof(VbspOptions.FullDetail) },
        { "-onlyents", nameof(VbspOptions.OnlyEnts) },
        { "-onlyprops", nameof(VbspOptions.OnlyProps) },
        { "-leaktest", nameof(VbspOptions.LeakTest) },
        { "-snapaxial", nameof(VbspOptions.SnapAxialPlanes) },
        { "-forceskyvis", nameof(VbspOptions.ForceSkyVis) },
        { "-bumpall", nameof(VbspOptions.BumpAll) },
        { "-lightifmissing", nameof(VbspOptions.LightIfMissing) },
        { "-keepstalezip", nameof(VbspOptions.KeepStaleZip) },
        { "-allowdetailcracks", nameof(VbspOptions.AllowDetailCracks) },
        { "-novirtualmesh", nameof(VbspOptions.NoVirtualMesh) },
        { "-replacematerials", nameof(VbspOptions.ReplaceMaterials) },
        { "-nodrawtriggers", nameof(VbspOptions.NoDrawTriggers) },
    };

    [Theory]
    [MemberData(nameof(BooleanFlags))]
    public void ABooleanFlagSetsExactlyItsOwnOption(string flag, string property)
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp([flag, Map]);

        Assert.Empty(result.Diagnostics);
        OptionAssert.OnlyChanged(result.Options, VbspOptions.Default, property, true);
    }

    [Fact]
    public void ACleanLineProducesNoDiagnostics()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-onlyents", "-v", Map]);

        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void TheMapPathIsRecordedExactlyAsWritten()
    {
        // The reference vbsp strips the extension and lowercases the basename
        // in its run phase. That is the path resolver's business,
        // not the parser's.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["maps/MixedCase.VMF"]);

        Assert.Equal("maps/MixedCase.VMF", result.MapPath);
    }

    [Fact]
    public void AFlagParsesInMixedCase()
    {
        // The reference parser compares flags case-insensitively, so every
        // stock spelling is.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-OnLyEnTs", Map]);

        Assert.True(result.Options.OnlyEnts);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void MinLuxelScaleParsesInMixedCaseEvenThoughStockCannot()
    {
        // The reference build's one min-luxel-scale branch compares the flag
        // case-sensitively, so stock rejects this spelling. That is a typo in stock.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-minLuxelScale", "2.5", Map]);

        Assert.Equal(2.5f, result.Options.MinLuxelScale);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void MicroParsesItsValue()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-micro", "0.5", Map]);

        OptionAssert.OnlyChanged(result.Options, VbspOptions.Default, nameof(VbspOptions.MicroVolume), 0.5f);
    }

    [Fact]
    public void LuxelScaleParsesItsValue()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-luxelscale", "2", Map]);

        Assert.Equal(2.0f, result.Options.LuxelScale);
    }

    [Fact]
    public void MinLuxelScaleBelowOneIsClampedUpToOne()
    {
        // The reference parser clamps as it parses.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-minluxelscale", "0.25", Map]);

        Assert.Equal(1.0f, result.Options.MinLuxelScale);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void MinLuxelScaleAboveOneIsKept()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-minluxelscale", "4", Map]);

        Assert.Equal(4.0f, result.Options.MinLuxelScale);
    }

    [Fact]
    public void DxLevelParsesItsValue()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-dxlevel", "90", Map]);

        Assert.Equal(90, result.Options.DxLevel);
    }

    [Fact]
    public void DxLevelSeventyDoesNotRaiseLuxelScaleAtParseTime()
    {
        // The reference tool raises the luxel scale for dxlevel 70 AFTER
        // parsing, in its run phase. The parser says
        // what was asked for; the stage applies stock's post-parse rules.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-dxlevel", "70", Map]);

        Assert.Equal(1.0f, result.Options.LuxelScale);
    }

    [Fact]
    public void BlockSelectsASingleBlock()
    {
        // -block sets the low and high x from one value, likewise y.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-block", "2", "-3", Map]);

        Assert.Equal(BspBlockGrid.Single(2, -3), result.Options.Blocks);
    }

    [Fact]
    public void BlocksSelectsTheWholeRectangleInStocksArgumentOrder()
    {
        // -blocks reads xl, yl, xh, yh -- not xl, xh, yl, yh.
        StockArgsResult<VbspOptions> result =
            StockArgs.ParseVbsp(["-blocks", "-1", "-2", "3", "4", Map]);

        Assert.Equal(new BspBlockGrid(-1, -2, 3, 4), result.Options.Blocks);
    }

    [Fact]
    public void EmbedRecordsItsDirectory()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-embed", "content/extra", Map]);

        Assert.Equal("content/extra", result.Options.EmbedDirectory);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void EmbedWithOnlyEntsIsAnErrorRatherThanAnExit()
    {
        // The reference tool warns and then exits the process.
        StockArgsResult<VbspOptions> result =
            StockArgs.ParseVbsp(["-embed", "content/extra", "-onlyents", Map]);

        CompileDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.ConflictingOptions, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void EmbedWithOnlyPropsIsAlsoAnError()
    {
        StockArgsResult<VbspOptions> result =
            StockArgs.ParseVbsp(["-embed", "content/extra", "-onlyprops", Map]);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void NoDetailWithFullDetailIsAWarningAndNotAnError()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-nodetail", "-fulldetail", Map]);

        CompileDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ThreadsIsRecordedWithItsValueConsumed()
    {
        // The reference vbsp parses it (and then serialises stock anyway);
        // this port's parallel stages honour it. The "8" must not be mistaken
        // for the map.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-threads", "8", Map]);

        Assert.Equal(8, result.Threads);
        Assert.Equal(Map, result.MapPath);
    }

    [Fact]
    public void ThreadsIsNoLongerADroppedOption()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-threads", "8", Map]);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ThreadsIsNullWhenAbsent()
    {
        Assert.Null(StockArgs.ParseVbsp([Map]).Threads);
    }

    [Fact]
    public void XboxIsAcceptedAndIgnored()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-xbox", Map]);

        Assert.Equal(StockArgsCodes.DroppedOption, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(VbspOptions.Default, result.Options);
    }

    [Fact]
    public void GlviewIsAcceptedAndIgnored()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-glview", Map]);

        Assert.Equal(StockArgsCodes.DroppedOption, Assert.Single(result.Diagnostics).Code);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void LauncherNoOpsAreAcceptedAndIgnored()
    {
        // The reference tool already does nothing with these three.
        StockArgsResult<VbspOptions> result =
            StockArgs.ParseVbsp(["-novconfig", "-allowdebug", "-steam", Map]);

        Assert.Equal(3, result.Diagnostics.Count);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void GameIsRecordedAndNotActedOn()
    {
        // The reference parser skips the value; its filesystem layer had
        // already mounted it off the global command line. There is no such side
        // effect here.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-game", "/hl2/hl2", Map]);

        Assert.Equal("/hl2/hl2", result.GameDirectory);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void VprojectIsTheSameFlagAsGame()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-vproject", "/hl2/hl2", Map]);

        Assert.Equal("/hl2/hl2", result.GameDirectory);
    }

    [Fact]
    public void InsertSearchPathsAccumulateInOrder()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(
            ["-insert_search_path", "a", "-insert_search_path", "b", Map]);

        Assert.Equal(new[] { "a", "b" }, result.ExtraSearchPaths);
    }

    [Fact]
    public void DumpStaticPropsPluralIsUnknownBecauseStockOnlyParsesTheSingular()
    {
        // The reference usage text advertises -dumpstaticprops while its parser
        // matches -dumpstaticprop. Stock rejects the spelling in its own usage text.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-dumpstaticprops", Map]);

        Assert.Equal(StockArgsCodes.UnknownOption, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void VirtualDispPhysicsIsUnknownBecauseNoBuildParsesIt()
    {
        // The reference usage text advertises it and no branch matches it.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-virtualdispphysics", Map]);

        Assert.Equal(StockArgsCodes.UnknownOption, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void MaxLightmapDimIsUnknownBecauseNoReferenceBuildParsesIt()
    {
        // Advertised but never matched in any reference build.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-maxlightmapdim", "256", Map]);

        Assert.Contains(result.Diagnostics, d => d.Code == StockArgsCodes.UnknownOption);
    }
}
