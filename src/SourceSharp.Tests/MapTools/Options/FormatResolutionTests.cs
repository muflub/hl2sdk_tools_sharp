//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// The format-resolution pipeline: defaults, then the mounted appid's preset,
/// then the gameinfo <c>Tools</c> key parsed as if typed, then the real command
/// line — so the Tools line WINS over the auto-detected preset and the typed
/// flags win over both.
/// </summary>
/// <remarks>
/// The appid auto-apply stores its preset during flag registration, ahead of
/// the real argv walk; the <c>Tools</c> tokens are then prepended and walked,
/// and finally the typed argv is walked. Each step is an overlay, so
/// the test for "later wins" is that the later step's fields are present and
/// the earlier step's unmentioned fields survive.
/// </remarks>
public class FormatResolutionTests
{
    private const int UnlistedAppId = 243750; // An app id the preset table does not list.

    private static GameInfo Game(int appid, string? toolsLine = null)
    {
        // The Tools block sits beside FileSystem, not inside it: GameInfo's
        // parser reads it at the block path GameInfo/Tools (or top-level
        // Tools).
        string tools = toolsLine is null
            ? string.Empty
            : "    Tools\n    {\n        vbsp\t\"" + toolsLine + "\"\n    }\n";

        return GameInfo.Parse(
            "\"GameInfo\"\n{\n"
            + "    game\t\"probe\"\n"
            + "    FileSystem\n    {\n        SteamAppId\t" + appid + "\n    }\n"
            + tools
            + "}\n");
    }

    // ---- resolution order ----

    [Fact]
    public void WithNothingSaidEverythingStaysAtDefault()
    {
        FormatResolution.Result r = FormatResolution.Resolve(null, null, false, false, null);

        Assert.Equal(FormatOptions.Default, r.Resolved);
        Assert.True(r.Resolved.IsDefault);
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public void TheAppidPresetAppliesOverTheDefaults()
    {
        FormatResolution.Result r = FormatResolution.Resolve(null, null, false, false, Game(620));

        Assert.Equal(21, r.Resolved.BspVersion);
        Assert.Equal(1, r.Resolved.WorldLightVersion);
        Assert.Equal("9", r.Resolved.StaticPropsToken);
        Assert.Equal("portal2", r.Resolved.PresetName);
        Assert.Equal(620, r.Resolved.DetectedSteamAppId);
        Assert.False(r.Resolved.IsDefault);
    }

    [Fact]
    public void TheToolsLineBeatsTheAutoDetectedPreset()
    {
        // The step order, stated as an assertion: appid 620 auto-selects
        // portal2 (token "9"), and a Tools line naming "6" overwrites it while
        // leaving portal2's 21/1 standing — because the Tools overlay speaks
        // only StaticPropsToken.
        FormatResolution.Result r = FormatResolution.Resolve(
            null, null, false, false, Game(620, "-staticpropformat 6"));

        Assert.Equal("6", r.Resolved.StaticPropsToken);
        Assert.Equal(21, r.Resolved.BspVersion);
        Assert.Equal(1, r.Resolved.WorldLightVersion);
    }

    [Fact]
    public void TheCommandLineBeatsTheToolsLine()
    {
        FormatResolution.Result r = FormatResolution.Resolve(
            new FormatOverrides(StaticPropsToken: "7"),
            null,
            false,
            false,
            Game(620, "-staticpropformat 6"));

        Assert.Equal("7", r.Resolved.StaticPropsToken);
    }

    [Fact]
    public void TheCommandLineBeatsTheAutoDetectedPreset()
    {
        FormatResolution.Result r = FormatResolution.Resolve(
            new FormatOverrides(BspVersion: 19), null, false, false, Game(620));

        Assert.Equal(19, r.Resolved.BspVersion);
        Assert.Equal("9", r.Resolved.StaticPropsToken); // portal2's, untouched by the CLI
    }

    [Fact]
    public void AToolsLineMaySelectItsOwnPreset()
    {
        // A preset token in the Tools line is an overlay too, so a csgo Tools
        // line over the portal2 appid keeps the 21 both presets ask for,
        // gains csgo's token "11" and cap, and names csgo as the provenance.
        FormatResolution.Result r = FormatResolution.Resolve(
            null, null, false, false, Game(620, "-csgo"));

        Assert.Equal(21, r.Resolved.BspVersion);
        Assert.Equal("11", r.Resolved.StaticPropsToken);
        Assert.Equal(32768, r.Resolved.DispInfoLimit);
        Assert.True(r.Resolved.CsgoClipContents);
        Assert.Equal("csgo", r.Resolved.PresetName);
    }

    [Fact]
    public void ACommandLinePresetFlagSetsProvenanceAndFields()
    {
        FormatResolution.Result r = FormatResolution.Resolve(
            MapFormatPreset.Csgo.ToOverrides(), "csgo", false, false, null);

        Assert.Equal("csgo", r.Resolved.PresetName);
        Assert.Equal("11", r.Resolved.StaticPropsToken);
        Assert.Equal(32768, r.Resolved.DispInfoLimit);
    }

    // ---- the two kill switches ----

    [Fact]
    public void NoFormatDetectSuppressesTheAutoPresetAndItsMessage()
    {
        FormatResolution.Result r = FormatResolution.Resolve(null, null, true, false, Game(620));

        Assert.True(r.Resolved.IsDefault);
        Assert.Null(r.Resolved.PresetName);
        // The appid is still reported as what was seen — the flag suppresses
        // the APPLY, not the detection's provenance.
        Assert.Equal(620, r.Resolved.DetectedSteamAppId);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == FormatResolution.AutoDetectCode);
    }

    [Fact]
    public void NoToolsArgsSuppressesTheSpliceAndItsMessage()
    {
        FormatResolution.Result r = FormatResolution.Resolve(
            null, null, false, true, Game(0, "-bspformat 19"));

        Assert.Equal(FormatOptions.DefaultBspVersion, r.Resolved.BspVersion);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == FormatResolution.ToolsSpliceCode);
    }

    [Fact]
    public void NoFormatDetectLeavesTheToolsLineWorking()
    {
        // The two switches are independent: suppressing auto-detect must not
        // quietly suppress the splice too.
        FormatResolution.Result r = FormatResolution.Resolve(
            null, null, true, false, Game(620, "-bspformat 19"));

        Assert.Equal(19, r.Resolved.BspVersion);
        Assert.Contains(r.Diagnostics, d => d.Code == FormatResolution.ToolsSpliceCode);
    }

    // ---- the message strings ----

    [Fact]
    public void TheAutoDetectMessageNamesTheDetectedFormat()
    {
        // "Auto-detected that this game requires %s BSP format".
        FormatResolution.Result r = FormatResolution.Resolve(null, null, false, false, Game(550));

        CompileDiagnostic d = Assert.Single(
            r.Diagnostics, d => d.Code == FormatResolution.AutoDetectCode);
        Assert.Equal(DiagnosticSeverity.Info, d.Severity);
        Assert.Equal("Auto-detected that this game requires l4d2 BSP format", d.Message);
    }

    [Fact]
    public void TheSpliceMessageQuotesTheToolsLine()
    {
        // "Adding arguments from gameinfo: %s".
        FormatResolution.Result r = FormatResolution.Resolve(
            null, null, false, false, Game(0, "-cullall -staticpropformat 8"));

        CompileDiagnostic d = Assert.Single(
            r.Diagnostics, d => d.Code == FormatResolution.ToolsSpliceCode);
        Assert.Equal(DiagnosticSeverity.Info, d.Severity);
        Assert.Equal("Adding arguments from gameinfo: -cullall -staticpropformat 8", d.Message);
    }

    [Fact]
    public void TheStepsLogInResolutionOrder()
    {
        FormatResolution.Result r = FormatResolution.Resolve(
            null, null, false, false, Game(620, "-bspformat 21"));

        Assert.Equal(
            [FormatResolution.AutoDetectCode, FormatResolution.ToolsSpliceCode],
            r.Diagnostics.Select(d => d.Code));
    }

    // ---- Tools-parse problems are warnings, never fatal ----

    [Fact]
    public void ABrokenToolsLineWarnsNamesTheKeyAndKeepsTheValidFlags()
    {
        // The file system authored this line. Splicing it would let the
        // unknown flag reach the real parser (which exits); this tool
        // reports and carries on, because a broken Tools line must not fail a
        // compile the rest of which is fine.
        FormatResolution.Result r = FormatResolution.Resolve(
            null, null, false, false, Game(0, "-nonsense -bspformat 21"));

        CompileDiagnostic d = Assert.Single(
            r.Diagnostics, d => d.Code == FormatResolution.ToolsProblemCode);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("from the gameinfo Tools key", d.Message, StringComparison.Ordinal);
        Assert.Contains("-nonsense", d.Message, StringComparison.Ordinal);

        // The valid half of the same line was still applied.
        Assert.Equal(21, r.Resolved.BspVersion);
    }

    [Fact]
    public void AToolsLineCannotSmuggleAnUnwritableVersion()
    {
        // An invalid VALUE (not an unknown flag) surfaces through the same
        // warning path and is NOT applied, so a Tools line cannot reach a
        // format this port's writer refuses.
        FormatResolution.Result r = FormatResolution.Resolve(
            null, null, false, false, Game(0, "-bspformat 99"));

        Assert.Contains(
            r.Diagnostics,
            d => d.Code == FormatResolution.ToolsProblemCode
                && d.Message.Contains("-bspformat 99", StringComparison.Ordinal));
        Assert.Equal(FormatOptions.DefaultBspVersion, r.Resolved.BspVersion);
    }

    [Fact]
    public void ABlankToolsLineSplicesNothing()
    {
        // Whitespace-only is "not there": tokenising produces no tokens, and
        // the splice message would be a lie.
        FormatResolution.Result r = FormatResolution.Resolve(null, null, false, false, Game(0, "   "));

        Assert.Empty(r.Diagnostics);
        Assert.True(r.Resolved.IsDefault);
    }

    // ---- default-path identity ----

    [Fact]
    public void AnUnlistedAppidWithNoToolsLineIsTheDefaultPath()
    {
        // The plain default gameinfo shape must land exactly on the default
        // WRITABLE state so the writer gets the legacy null. The comparison
        // is field-by-field on the writable fields plus IsDefault, not
        // Assert.Equal against
        // FormatOptions.Default: the provenance fields (DetectedSteamAppId
        // above all) legitimately differ from a hand-built Default, and that
        // difference is reporting, not format.
        FormatResolution.Result r = FormatResolution.Resolve(null, null, false, false, Game(UnlistedAppId));

        Assert.True(r.Resolved.IsDefault);
        Assert.Null(BspFormatWriter.ToWriteFormat(r.Resolved));
        Assert.Equal(FormatOptions.Default.BspVersion, r.Resolved.BspVersion);
        Assert.Equal(FormatOptions.Default.WorldLightVersion, r.Resolved.WorldLightVersion);
        Assert.Null(r.Resolved.StaticPropsToken);
        Assert.False(r.Resolved.MatsysCompat);
        Assert.False(r.Resolved.SimpleLadders);
        Assert.False(r.Resolved.NoDisp4VirtualMesh);
        Assert.False(r.Resolved.NoIneligibleVertexLitProps);
        Assert.False(r.Resolved.CsgoClipContents);
        Assert.Null(r.Resolved.DispInfoLimit);
        Assert.False(r.Resolved.L4d2LumpDirLayout);
        // The appid really is outside the table: auto-detect said nothing.
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == FormatResolution.AutoDetectCode);
    }

    [Fact]
    public void AProvenanceOnlyChangeStillCountsAsDefault()
    {
        // A preset that changes nothing writable keeps IsDefault, so the write
        // path stays the legacy call: provenance is reporting, not format.
        FormatOptions o = new(presetName: "singleplayer", detectedSteamAppId: 220);

        Assert.True(o.IsDefault);
        Assert.Null(BspFormatWriter.ToWriteFormat(o));
    }

    [Fact]
    public void OneWritableFieldOffDefaultReachesTheWriter()
    {
        FormatResolution.Result r = FormatResolution.Resolve(
            new FormatOverrides(BspVersion: 21), null, false, false, null);

        Assert.False(r.Resolved.IsDefault);
        Assert.NotNull(BspFormatWriter.ToWriteFormat(r.Resolved));
    }
}
