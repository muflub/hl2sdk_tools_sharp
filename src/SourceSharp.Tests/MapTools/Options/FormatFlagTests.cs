using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// The twelve format-family flags <see cref="StockArgs.ParseVbsp"/> gained for
/// the Tools++ preset surface, and the two kill switches. Each flag lands in
/// the <see cref="StockArgsResult{VbspOptions}.Format"/> overlay (never on
/// <c>VbspOptions</c> itself) with ++'s validation table.
/// </summary>
public class FormatFlagTests
{
    private const string Map = "maps/testmap.vmf";

    private static StockArgsResult<VbspOptions> Parse(params string[] args) =>
        StockArgs.ParseVbsp([.. args, Map]);

    // ---- the boolean five ----

    [Theory]
    [InlineData("-matsyscompat", nameof(FormatOverrides.MatsysCompat))]
    [InlineData("-simpleladders", nameof(FormatOverrides.SimpleLadders))]
    [InlineData("-nodisp4virtualmesh", nameof(FormatOverrides.NoDisp4VirtualMesh))]
    [InlineData("-noineligiblevertexlitprops", nameof(FormatOverrides.NoIneligibleVertexLitProps))]
    [InlineData("-csgoclipcontents", nameof(FormatOverrides.CsgoClipContents))]
    public void AFormatBooleanSetsExactlyItsOverlayField(string flag, string field)
    {
        StockArgsResult<VbspOptions> r = Parse(flag);

        Assert.Empty(r.Diagnostics);
        FormatOverrides f = r.Format!;
        Assert.True((bool?)typeof(FormatOverrides).GetProperty(field)!.GetValue(f));

        // Every other overlay field stays unsaid, so the resolver cannot
        // accidentally inherit a preset from a hand-typed flag.
        // GetProperties() also yields the record's static members (None);
        // only the instance fields are the overlay's vocabulary.
        foreach (var p in typeof(FormatOverrides).GetProperties()
            .Where(p => p.Name != field
                && p.Name != nameof(FormatOverrides.IsEmpty)
                && p.GetMethod is { IsStatic: false }))
        {
            Assert.Null(p.GetValue(f));
        }
    }

    [Fact]
    public void NoFormatFlagLeavesTheOverlayNull()
    {
        // The default path: a plain line says nothing about format, so the
        // resolver sees no CLI overlay at all (not an empty one).
        StockArgsResult<VbspOptions> r = Parse("-onlyents");

        Assert.Null(r.Format);
        Assert.Null(r.PresetName);
        Assert.False(r.NoFormatDetect);
        Assert.False(r.NoToolsArgs);
    }

    // ---- the value flags and their validation tables ----

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void BspFormatAcceptsTheWriterTable(string version)
    {
        StockArgsResult<VbspOptions> r = Parse("-bspformat", version);

        Assert.Empty(r.Diagnostics);
        Assert.Equal(int.Parse(version), r.Format!.BspVersion);
    }

    [Theory]
    [InlineData("18")]
    [InlineData("22")]
    [InlineData("0")]
    [InlineData("tf2")]
    public void BspFormatRefusesAnythingElse(string version)
    {
        StockArgsResult<VbspOptions> r = Parse("-bspformat", version);

        Assert.True(r.HasErrors);
        Assert.Contains(
            r.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("-bspformat", StringComparison.Ordinal)
                && d.Message.Contains("19, 20 or 21", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void LightFormatAcceptsItsTwoValues(string version)
    {
        StockArgsResult<VbspOptions> r = Parse("-lightformat", version);

        Assert.Empty(r.Diagnostics);
        Assert.Equal(int.Parse(version), r.Format!.WorldLightVersion);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    public void LightFormatRefusesAnythingElse(string version)
    {
        StockArgsResult<VbspOptions> r = Parse("-lightformat", version);

        Assert.True(r.HasErrors);
        Assert.Contains(
            r.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("0 or 1", StringComparison.Ordinal));
    }

    [Theory]
    // ++'s token table verbatim: the digit range and the TF2 flavour, with
    // "10_TF2" compared first (dump 0x1400e696c, FUN_140042b20).
    [InlineData("6")]
    [InlineData("7")]
    [InlineData("8")]
    [InlineData("9")]
    [InlineData("10")]
    [InlineData("10_TF2")]
    [InlineData("11")]
    [InlineData("12")]
    [InlineData("13")]
    [InlineData("14")]
    public void StaticPropFormatAcceptsTsTokenTable(string token)
    {
        StockArgsResult<VbspOptions> r = Parse("-staticpropformat", token);

        Assert.Empty(r.Diagnostics);
        Assert.Equal(token, r.Format!.StaticPropsToken);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("15")]
    [InlineData("10_tf2")] // ++'s first compare is a case-exact strcmp
    public void StaticPropFormatRefusesWithDumpText(string token)
    {
        // ++: "Unrecognized prop format %s" — the port says ++'s words with
        // the typed token substituted, verbatim, under ARGS0011.
        StockArgsResult<VbspOptions> r = Parse("-staticpropformat", token);

        Assert.True(r.HasErrors);
        CompileDiagnostic d = Assert.Single(
            r.Diagnostics, d => d.Code == FormatResolution.UnrecognizedPropFormatCode);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal($"Unrecognized prop format {token}", d.Message);
    }

    [Fact]
    public void AnEmptyStaticPropFormatValueIsAMissingValueNotARefusedFormat()
    {
        // An empty token never reaches ++'s token table: the cursor's
        // TryValue rejects "" as "no value" (ARGS0002) before the flag
        // handler sees it, so the dump's "Unrecognized prop format" text is
        // the wrong complaint here.
        StockArgsResult<VbspOptions> r = Parse("-staticpropformat", string.Empty);

        Assert.True(r.HasErrors);
        CompileDiagnostic d = Assert.Single(
            r.Diagnostics, x => x.Code == StockArgsCodes.MissingValue);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.DoesNotContain(
            r.Diagnostics, x => x.Code == FormatResolution.UnrecognizedPropFormatCode);
    }

    [Fact]
    public void MaxDispInfoSetsTheCap()
    {
        StockArgsResult<VbspOptions> r = Parse("-maxdispinfo", "1024");

        Assert.Empty(r.Diagnostics);
        Assert.Equal(1024, r.Format!.DispInfoLimit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("lots")]
    public void MaxDispInfoRefusesANonPositiveOrUnparsableLimit(string value)
    {
        StockArgsResult<VbspOptions> r = Parse("-maxdispinfo", value);

        Assert.True(r.HasErrors);
        Assert.Contains(
            r.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("-maxdispinfo", StringComparison.Ordinal));
    }

    // ---- preset flags on the command line ----

    [Theory]
    [InlineData("-singleplayer")]
    [InlineData("-portal2")]
    [InlineData("-l4d2")]
    [InlineData("-asw")]
    [InlineData("-insurgency")]
    [InlineData("-csgo")]
    public void APresetFlagNamesItselfAsProvenance(string flag)
    {
        StockArgsResult<VbspOptions> r = Parse(flag);

        Assert.Empty(r.Diagnostics);
        Assert.Equal(flag[1..], r.PresetName);
        Assert.NotNull(r.Format);
    }

    [Fact]
    public void TypedFlagsBeatAnEarlierPresetFlagInTheSameLine()
    {
        // One accumulator in token order, like ++ storing globals as the
        // argv walk reaches each token: -csgo then -bspformat 19 writes 19.
        StockArgsResult<VbspOptions> r = Parse("-csgo", "-bspformat", "19");

        Assert.Equal(19, r.Format!.BspVersion);
        Assert.Equal("11", r.Format.StaticPropsToken); // csgo's, never rewritten
    }

    [Fact]
    public void APresetFlagAfterATypedFlagWins()
    {
        // The other order, same rule: -bspformat 19 then -csgo writes 21.
        StockArgsResult<VbspOptions> r = Parse("-bspformat", "19", "-csgo");

        Assert.Equal(21, r.Format!.BspVersion);
        Assert.Equal("csgo", r.PresetName);
    }

    [Fact]
    public void ASecondPositionalIsNotTreatedAsAPresetName()
    {
        // Preset lookup demands the leading dash (StockArgs.cs:294), so a bare
        // "csgo" reaches the map-path setter rather than the preset table.
        StockArgsResult<VbspOptions> r = StockArgs.ParseVbsp(["maps/a.vmf", "csgo"]);

        Assert.Null(r.PresetName);
        Assert.Null(r.Format);
    }

    // ---- the kill switches ----

    [Fact]
    public void TheTwoSwitchesAreRecordedOnTheResult()
    {
        StockArgsResult<VbspOptions> r = Parse("-noformatdetect", "-notoolsargs");

        Assert.True(r.NoFormatDetect);
        Assert.True(r.NoToolsArgs);
        Assert.Empty(r.Diagnostics);
    }
}
