using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;
using SourceSharp.Tests.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.ToolsPlusPlus;

/// <summary>
/// <see cref="StockArgs.ParseVrad"/> against the ZHLT-flavored (Tools++) vrad
/// family from <c>vradplusplus.exe</c>. Every registrar here is cited from the
/// decompilation at <c>/home/lodle/re/toolsplusplus/decompilations/vrad/all.c</c>;
/// the triage (what each flag means, what is REFUSED, what is parse-only and
/// why) is <c>T5-findings.md</c>.
/// </summary>
/// <remarks>
/// The ++ registrars use the binary's generic parameter parsers
/// (<c>FUN_140053b40</c> float, <c>FUN_140053a80</c> int), so the value-error
/// texts are this parser's generic ones, and no ++ flag validates its range:
/// the registrars store whatever the parser yields. That is why out-of-range
/// facts here assert ACCEPTANCE where a stock flag would error.
/// </remarks>
public class T5VradFamilyParseTests
{
    private const string Map = "maps/testmap.bsp";

    public static TheoryData<string, string, bool> BooleanFlags => new()
    {
        { "-ambientocclusion", nameof(VradOptions.AmbientOcclusion), true },
        { "-ao", nameof(VradOptions.AmbientOcclusion), true },
        { "-supportslightdirectional", nameof(VradOptions.SupportsLightDirectional), true },
        { "-supportslightprojected", nameof(VradOptions.SupportsLightProjected), true },
        { "-sphericalharmonics", nameof(VradOptions.SphericalHarmonics), true },
    };

    [Theory]
    [MemberData(nameof(BooleanFlags))]
    public void ABooleanPlusPlusFlagSetsExactlyItsOwnOption(string flag, string property, bool expected)
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad([flag, Map]);

        Assert.Empty(result.Diagnostics);
        OptionAssert.OnlyChanged(result.Options, VradOptions.Default, property, expected);
    }

    // ---------- the AO family ----------

    [Fact]
    public void AmbientOcclusionAndAoWriteTheSameByte()
    {
        // Both registrars take &DAT_1417194f9 (all.c:43471, 43482): -ao is a
        // true alias, not a second knob. The records stay distinguishable
        // (AoDebug-style fidelity is only ever the one gate), and a consumer
        // can only read the shared byte -- so both spellings light exactly the
        // same property.
        StockArgsResult<VradOptions> spelled = StockArgs.ParseVrad(["-ambientocclusion", Map]);
        StockArgsResult<VradOptions> alias = StockArgs.ParseVrad(["-ao", Map]);

        Assert.True(spelled.Options.AmbientOcclusion);
        Assert.True(alias.Options.AmbientOcclusion);
        Assert.Equal(spelled.Options, alias.Options);
    }

    [Fact]
    public void AoRadiusKeepsItsValue()
    {
        // all.c:43485 float registrar, .data 0x1401062d8 = 40.0.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-aoradius", "120.5", Map]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(120.5f, result.Options.AoRadius);
    }

    [Fact]
    public void AoScaleKeepsItsValue()
    {
        // all.c:43488 float registrar, .data 0x1417194e5-adjacent .data
        // 0x1401062d4 = 0.5.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-aoscale", "2", Map]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2.0f, result.Options.AoScale);
    }

    [Theory]
    [InlineData("-aoradius")]
    [InlineData("-aoscale")]
    public void AnAoFloatFlagWithoutAValueIsAnError(string flag)
    {
        // The generic float parser (FUN_140053b40) demands the next token.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad([Map, flag]);

        CompileDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.MissingValue, error.Code);
        Assert.Equal("vrad: expected a value after '" + flag + "'", error.Message);
    }

    [Theory]
    [InlineData("-aoradius")]
    [InlineData("-aoscale")]
    public void AnAoFloatFlagGivenWordsIsAnError(string flag)
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad([flag, "sunrise", Map]);

        CompileDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.MalformedValue, error.Code);
        Assert.Equal("vrad: '" + flag + "' expected a number but was given 'sunrise'", error.Message);
    }

    [Fact]
    public void TheAoSampleCountsKeepTheirValues()
    {
        // all.c:43491 (.data 0x1401062dc = 32), all.c:43495 (.data
        // 0x1401062e0 = 16): the model's defaults are those .data words, and
        // an explicit value replaces them.
        StockArgsResult<VradOptions> defaults = StockArgs.ParseVrad([Map]);
        Assert.Equal(32, defaults.Options.AoFaceSamples);
        Assert.Equal(16, defaults.Options.AoPropSamples);

        StockArgsResult<VradOptions> faces = StockArgs.ParseVrad(["-aofacesamples", "7", Map]);
        Assert.Empty(faces.Diagnostics);
        Assert.Equal(7, faces.Options.AoFaceSamples);

        StockArgsResult<VradOptions> props = StockArgs.ParseVrad(["-aopropsamples", "7", Map]);
        Assert.Empty(props.Diagnostics);
        Assert.Equal(7, props.Options.AoPropSamples);
    }

    [Theory]
    [InlineData("-aofacesamples")]
    [InlineData("-aopropsamples")]
    public void AnAoSampleCountGivenWordsIsAnError(string flag)
    {
        // The generic int parser (FUN_140053a80) is strtol-shaped: words fail.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad([flag, "many", Map]);

        CompileDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.MalformedValue, error.Code);
        Assert.Equal("vrad: '" + flag + "' expected a whole number but was given 'many'", error.Message);
    }

    [Theory]
    [InlineData("-aofacesamples", "-1")]
    [InlineData("-aopropsamples", "0")]
    public void AnAoSampleCountAcceptsAnyInt(string flag, string value)
    {
        // No registrar-side validation exists: the int registrar stores
        // whatever strtol yields (FUN_140053a80), unlike stock's -bounce.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad([flag, value, Map]);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AoDebugTurnsOnAoItsOwnDebugFlagAndSupersamplingOff()
    {
        // The callback registrar's callback (disasm 0x14003b140) writes
        // exactly three things: 0x1417194f9=1 (AO on), 0x1417194fa=1
        // (AO-debug on), and 0 at 0x1401062b8 -- the SAME global -noextra
        // registers (all.c:43312), i.e. supersampling off. It is NOT the
        // -scale lightscale (0x1401062a0, all.c:43384): the predecessor's
        // "lightscale=0" reading of the third write is the corrected bytes.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-aodebug", Map]);

        Assert.Empty(result.Diagnostics);
        Assert.True(result.Options.AmbientOcclusion);
        Assert.True(result.Options.AoDebug);
        Assert.False(result.Options.Supersample);
    }

    [Fact]
    public void AoDebugLeavesEverythingElseAtADefaults()
    {
        // Three properties move; the rest of the ++ family in particular
        // stays at its cited .data default.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-aodebug", Map]);
        VradOptions o = result.Options;

        Assert.Equal(VradOptions.Default.AoRadius, o.AoRadius);
        Assert.Equal(VradOptions.Default.AoScale, o.AoScale);
        Assert.Equal(VradOptions.Default.AoFaceSamples, o.AoFaceSamples);
        Assert.Equal(VradOptions.Default.AoPropSamples, o.AoPropSamples);
        Assert.Equal(VradOptions.Default.StaticPropIndirectMode, o.StaticPropIndirectMode);
        Assert.False(o.SphericalHarmonics);
        Assert.False(o.WorldTextureShadows);
    }

    // ---------- StaticPropSampleScale ----------

    [Fact]
    public void StaticPropSampleScaleKeepsItsValue()
    {
        // all.c:43409 float registrar, .data 0x1401062ac = 1.0.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-StaticPropSampleScale", "2.5", Map]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2.5f, result.Options.StaticPropSampleScale);
    }

    [Fact]
    public void StaticPropSampleScaleWithoutAValueIsAnError()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad([Map, "-StaticPropSampleScale"]);

        CompileDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.MissingValue, error.Code);
        Assert.Equal("vrad: expected a value after '-StaticPropSampleScale'", error.Message);
    }

    [Fact]
    public void StaticPropSampleScaleGivenWordsIsAnError()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-StaticPropSampleScale", "huge", Map]);

        CompileDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.MalformedValue, error.Code);
        Assert.Equal("vrad: '-StaticPropSampleScale' expected a number but was given 'huge'", error.Message);
    }

    [Fact]
    public void StaticPropSampleScaleIsSpelledCaseInsensitively()
    {
        // Command-line tokens reach the dispatcher through the tool's
        // case-insensitive Is(), matching the binary's stricmp-based
        // CommandLineParam registration.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-staticpropsamplescale", "3", Map]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(3.0f, result.Options.StaticPropSampleScale);
    }

    // ---------- StaticPropIndirectMode ----------

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void StaticPropIndirectModeAcceptsTheDocumentedModes(string value)
    {
        // all.c:43405 int registrar, byte 0x1417194ec (default 0); usage
        // "0 - default, 1 - TF2, 2 - Orangebox".
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-StaticPropIndirectMode", value, Map]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(int.Parse(value), result.Options.StaticPropIndirectMode);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("-7")]
    public void StaticPropIndirectModeAcceptsAnyIntWithoutAWarning(string value)
    {
        // The consumer (FUN_14003ecb0, all.c:46468/46478/46496) takes NONE of
        // its weighting branches outside 0..2 -- the value contributes
        // nothing -- and the registrar validates nothing either. So the parse
        // silently accepts it, exactly as the binary does.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-StaticPropIndirectMode", value, Map]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(int.Parse(value), result.Options.StaticPropIndirectMode);
    }

    [Fact]
    public void StaticPropIndirectModeGivenWordsIsAnError()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-StaticPropIndirectMode", "tf2", Map]);

        CompileDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.MalformedValue, error.Code);
        Assert.Equal("vrad: '-StaticPropIndirectMode' expected a whole number but was given 'tf2'", error.Message);
    }

    [Fact]
    public void StaticPropIndirectModeWithoutAValueIsAnError()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad([Map, "-StaticPropIndirectMode"]);

        CompileDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.MissingValue, error.Code);
        Assert.Equal("vrad: expected a value after '-StaticPropIndirectMode'", error.Message);
    }

    // ---------- the shared texture-shadow gate ----------

    [Fact]
    public void WorldTextureShadowsAndTranslucentShadowsAreTheSameByte()
    {
        // Disasm 0x14003a634..0x14003a668: ONE global (0x1417194e5) is loaded
        // into r13 and passed to BOTH registrars -- a byte-level shared
        // target, not a Ghidra merge artifact. So either spelling flips one
        // gate, and the record of either spelling reads as both properties
        // set (the consumer reads the gate, which is up under either name).
        StockArgsResult<VradOptions> world = StockArgs.ParseVrad(["-worldtextureshadows", Map]);
        StockArgsResult<VradOptions> translucent = StockArgs.ParseVrad(["-translucentshadows", Map]);

        Assert.True(world.Options.WorldTextureShadows);
        Assert.True(world.Options.TranslucentShadows);
        Assert.True(translucent.Options.WorldTextureShadows);
        Assert.True(translucent.Options.TranslucentShadows);
        Assert.Equal(world.Options, translucent.Options);
    }

    [Fact]
    public void TheTextureShadowGateDoesNotTouchTheStockTextureShadowsFlag()
    {
        // Stock's -textureshadows (vrad.cpp:2705, the world-lightmap
        // face-texture shadowing) is a DIFFERENT knob from the ++ shared
        // gate; accepting the ++ gate must leave it alone.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-worldtextureshadows", Map]);

        Assert.False(result.Options.TextureShadows);
    }

    // ---------- the no-op-by-construction rows ----------

    [Fact]
    public void TheSupportsLightFlagsAreAcceptedAndRecorded()
    {
        // -supportslightdirectional (all.c:43459-ish registrar; consumer
        // all.c:22694) and -supportslightprojected (all.c:43462; consumer
        // all.c:22498) disable ++'s light_directional/light_projected ->
        // light_spot classname renames. The port never renames classnames
        // (vrad writes no entity lump), so there is nothing to disable --
        // parse-only is exact behavior parity. Still recorded, never dropped.
        StockArgsResult<VradOptions> directional = StockArgs.ParseVrad(["-supportslightdirectional", Map]);
        StockArgsResult<VradOptions> projected = StockArgs.ParseVrad(["-supportslightprojected", Map]);

        Assert.Empty(directional.Diagnostics);
        Assert.Empty(projected.Diagnostics);
        OptionAssert.OnlyChanged(directional.Options, VradOptions.Default, nameof(VradOptions.SupportsLightDirectional), true);
        OptionAssert.OnlyChanged(projected.Options, VradOptions.Default, nameof(VradOptions.SupportsLightProjected), true);
    }

    [Fact]
    public void SphericalHarmonicsIsAcceptedQuietlyAndStaysParseOnly()
    {
        // STAGED: the flag swaps the whole final-lighting stage to an SH9
        // kernel (nine consumer sites, e.g. all.c:13942), which the plan
        // gives its own sub-lane with a golden-vs-++ gate. No -- oracle
        // exists for the kernel today, so the behavior stays absent (the
        // documented gap, T5-findings.md); the PARSE is complete.
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-sphericalharmonics", Map]);

        Assert.Empty(result.Diagnostics);
        Assert.True(result.Options.SphericalHarmonics);
    }

    // ---------- whole-line sanity ----------

    [Fact]
    public void TheWholePlusPlusFamilyParsesWithZeroDiagnostics()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(
        [
            "-ambientocclusion", "-aoradius", "60", "-aoscale", "0.25",
            "-aofacesamples", "64", "-aopropsamples", "8", "-aodebug",
            "-StaticPropSampleScale", "1.5", "-StaticPropIndirectMode", "2",
            "-worldtextureshadows", "-translucentshadows",
            "-supportslightdirectional", "-supportslightprojected",
            "-sphericalharmonics",
            Map,
        ]);

        Assert.Empty(result.Diagnostics);
        VradOptions o = result.Options;
        Assert.True(o.AmbientOcclusion);
        Assert.Equal(60.0f, o.AoRadius);
        Assert.Equal(0.25f, o.AoScale);
        Assert.Equal(64, o.AoFaceSamples);
        Assert.Equal(8, o.AoPropSamples);
        Assert.True(o.AoDebug);
        Assert.Equal(1.5f, o.StaticPropSampleScale);
        Assert.Equal(2, o.StaticPropIndirectMode);
        Assert.True(o.WorldTextureShadows);
        Assert.True(o.TranslucentShadows);
        Assert.True(o.SupportsLightDirectional);
        Assert.True(o.SupportsLightProjected);
        Assert.True(o.SphericalHarmonics);
    }

    [Fact]
    public void NoneOfThePlusPlusFlagsIsAStockQuirkName()
    {
        // ++ is a feature surface, not a stock-defect catalogue: the matrix
        // rule (ComplianceMatrix.ToolsPlusPlusFlags) forbids any ++ option
        // name from naming a StockQuirk. Exact-name matching is the rule --
        // quirk names may freely MENTION static props (there is a real
        // StaticPropBadVertexDropsPropFlags); they may not BE an option.
        foreach (string flag in ComplianceMatrix.ToolsPlusPlusFlags)
        {
            foreach (string quirk in Enum.GetNames<StockQuirk>())
            {
                Assert.False(
                    string.Equals(flag, quirk, StringComparison.OrdinalIgnoreCase),
                    flag + " is also the quirk " + quirk);
            }
        }
    }
}
