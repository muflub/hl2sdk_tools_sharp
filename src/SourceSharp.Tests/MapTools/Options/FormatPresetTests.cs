using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// The six Tools++ format presets and the appid auto-detect table, as frozen
/// overlays. Every field here is decompilation ground truth, not the report:
/// the callback bodies at <c>vbspplusplus.exe</c> <c>0x1400429c0…0x140042aff</c>
/// and the auto-detect switch at <c>0x140041c9a…0x140041dbe</c>
/// (<c>~/re/toolsplusplus/dumps/vbsp.all.c</c>). Where
/// <c>toolsplusplus-findings.md</c> §1.1 and the dump disagree the dump wins,
/// and the three corrections the table carries are pinned as facts of their
/// own: 550 is l4d2, 4465480 is csgo, and 619 selects nothing.
/// </summary>
public class FormatPresetTests
{
    [Fact]
    public void ThereAreSixPresetsInRegistrationOrder()
    {
        // The order ++ walks its registration table; the printer and every
        // lookup follow it.
        Assert.Equal(
            ["singleplayer", "portal2", "l4d2", "asw", "insurgency", "csgo"],
            MapFormatPreset.All.Select(p => p.Name));
    }

    // ---- per-preset field sets (each callback's stores, verbatim) ----

    [Fact]
    public void SingleplayerStoresMatsysAndTokenSixAndNothingElse()
    {
        // Callback 0x1400429c0: exactly two stores. The report's "resets
        // defaults / writes none" cell is wrong against the dump, and the
        // bsp/light versions are left at the writer defaults (null overlay
        // fields), NOT written as 19.
        MapFormatPreset p = MapFormatPreset.Singleplayer;

        Assert.Null(p.BspVersion);
        Assert.Null(p.WorldLightVersion);
        Assert.Equal("6", p.StaticPropsToken);
        Assert.True(p.MatsysCompat);
        Assert.Null(p.SimpleLadders);
        Assert.Null(p.NoDisp4VirtualMesh);
        Assert.Null(p.NoIneligibleVertexLitProps);
        Assert.Null(p.CsgoClipContents);
        Assert.Null(p.DispInfoLimit);
        Assert.Null(p.L4d2LumpDirLayout);
    }

    [Fact]
    public void Portal2StoresTwentyOneOneNineMatsysNoDisp4()
    {
        MapFormatPreset p = MapFormatPreset.Portal2;

        Assert.Equal(21, p.BspVersion);
        Assert.Equal(1, p.WorldLightVersion);
        Assert.Equal("9", p.StaticPropsToken);
        Assert.True(p.MatsysCompat);
        Assert.Null(p.SimpleLadders);
        Assert.True(p.NoDisp4VirtualMesh);
        Assert.Null(p.NoIneligibleVertexLitProps);
        Assert.Null(p.CsgoClipContents);
        Assert.Null(p.DispInfoLimit);
        Assert.Null(p.L4d2LumpDirLayout);
    }

    [Fact]
    public void L4d2AddsSimpleLaddersAndTheLumpDirLayout()
    {
        // Callback 0x140042a40, tail `cl = 1` into the layout setter.
        MapFormatPreset p = MapFormatPreset.L4d2;

        Assert.Equal(21, p.BspVersion);
        Assert.Equal(1, p.WorldLightVersion);
        Assert.Equal("9", p.StaticPropsToken);
        Assert.True(p.MatsysCompat);
        Assert.True(p.SimpleLadders);
        Assert.True(p.NoDisp4VirtualMesh);
        Assert.True(p.L4d2LumpDirLayout);
        Assert.Null(p.NoIneligibleVertexLitProps);
        Assert.Null(p.CsgoClipContents);
        Assert.Null(p.DispInfoLimit);
    }

    [Fact]
    public void AswUsesTheVersionSevenToken()
    {
        // Callback 0x1400429e0. The report's table cell MISSED the
        // lightgroups store; the dump has it, so WorldLightVersion is 1.
        MapFormatPreset p = MapFormatPreset.Asw;

        Assert.Equal(21, p.BspVersion);
        Assert.Equal(1, p.WorldLightVersion);
        Assert.Equal("7", p.StaticPropsToken);
        Assert.True(p.MatsysCompat);
        Assert.True(p.NoDisp4VirtualMesh);
        Assert.Null(p.L4d2LumpDirLayout);
    }

    [Fact]
    public void InsurgencyUsesThePlainTenTokenNotTheTf2Flavour()
    {
        // Callback 0x140042a80; the token table compares "10_TF2" first, so
        // the plain "10" is the Insurgency flavour (stored index 5).
        MapFormatPreset p = MapFormatPreset.Insurgency;

        Assert.Equal(21, p.BspVersion);
        Assert.Equal("10", p.StaticPropsToken);
        Assert.True(p.MatsysCompat);
        Assert.True(p.NoDisp4VirtualMesh);
        Assert.True(p.NoIneligibleVertexLitProps);
        Assert.Null(p.CsgoClipContents);
    }

    [Fact]
    public void CsgoIsTheWidestPresetIncludingThe32768DispInfoCap()
    {
        // Callback 0x140042ac0, tail `mov ecx,0x8000; jmp 0x140055ec0` — the
        // 32,768 literal is the ground truth for the disp-info cap.
        MapFormatPreset p = MapFormatPreset.Csgo;

        Assert.Equal(21, p.BspVersion);
        Assert.Equal(1, p.WorldLightVersion);
        Assert.Equal("11", p.StaticPropsToken);
        Assert.True(p.MatsysCompat);
        Assert.True(p.NoDisp4VirtualMesh);
        Assert.True(p.NoIneligibleVertexLitProps);
        Assert.True(p.CsgoClipContents);
        Assert.Equal(32768, p.DispInfoLimit);
        Assert.Null(p.L4d2LumpDirLayout);
    }

    // ---- overlay composition ----

    [Fact]
    public void AOverlayWritesOnlyTheFieldsThePresetSpeaks()
    {
        // The null-vs-value rule: an overlay names only what its callback
        // stores, so ApplyOver cannot touch a field the preset never mentions.
        FormatOverrides o = MapFormatPreset.Singleplayer.ToOverrides();

        Assert.False(o.IsEmpty);
        Assert.Null(o.BspVersion);
        Assert.Null(o.WorldLightVersion);
        Assert.Equal("6", o.StaticPropsToken);
        Assert.True(o.MatsysCompat);
        Assert.Null(o.NoDisp4VirtualMesh);
        Assert.Null(o.DispInfoLimit);
        Assert.Null(o.L4d2LumpDirLayout);
    }

    [Fact]
    public void TwoPresetsComposeLastWriterWinsPerField()
    {
        // ++ applies each callback when its token is walked, so the later one
        // overwrites only the fields it names: -asw -singleplayer keeps asw's
        // 21/1/noDisp4 stores and gains singleplayer's token "6".
        FormatOverrides merged = MapFormatPreset.Singleplayer
            .ToOverrides()
            .ApplyOver(MapFormatPreset.Asw.ToOverrides());

        Assert.Equal(21, merged.BspVersion); // from asw; singleplayer does not touch it
        Assert.Equal(1, merged.WorldLightVersion); // from asw
        Assert.Equal("6", merged.StaticPropsToken); // overwritten by singleplayer
        Assert.True(merged.MatsysCompat);
        Assert.True(merged.NoDisp4VirtualMesh); // still asw's
        Assert.Null(merged.L4d2LumpDirLayout); // neither preset speaks it
    }

    // ---- name lookup ----

    [Theory]
    [InlineData("-csgo")]
    [InlineData("csgo")]
    [InlineData("-CSGO")]
    [InlineData("CsGo")]
    public void PresetNamesMatchWithOrWithoutDashAnyCase(string name)
    {
        Assert.True(MapFormatPreset.TryByName(name, out MapFormatPreset? preset));
        Assert.Same(MapFormatPreset.Csgo, preset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("tf2")]
    [InlineData("hl2")]
    [InlineData("-csgoo")]
    public void NamesThatAreNotPresetsMatchNothing(string name)
    {
        Assert.False(MapFormatPreset.TryByName(name, out MapFormatPreset? preset));
        Assert.Null(preset);
    }

    // ---- appid auto-detect table (dump constants, not report guesses) ----

    [Theory]
    // The singleplayer bucket.
    [InlineData(220, "singleplayer")]
    [InlineData(400, "singleplayer")]
    [InlineData(17520, "singleplayer")]
    [InlineData(243730, "singleplayer")]
    [InlineData(714070, "singleplayer")]
    [InlineData(1583720, "singleplayer")]
    // The dump's own compare/register for 550 — the report placed it elsewhere.
    [InlineData(550, "l4d2")]
    // Portal 2 and the Alien Swarm pair.
    [InlineData(620, "portal2")]
    [InlineData(630, "asw")]
    [InlineData(563560, "asw")]
    // The csgo bucket — 4465480 included (the report placed it in
    // singleplayer; the dump's constant lands here).
    [InlineData(730, "csgo")]
    [InlineData(222880, "csgo")]
    [InlineData(362890, "csgo")]
    [InlineData(447820, "csgo")]
    [InlineData(4465480, "csgo")]
    public void TheListedAppidsSelectTheirBucket(int appid, string presetName)
    {
        Assert.True(MapFormatPreset.TryFromSteamAppId(appid, out MapFormatPreset? preset));
        Assert.Equal(presetName, preset!.Name);
    }

    [Theory]
    // 619 is a range bound in the dump's compare chain, not a bucket member.
    [InlineData(619)]
    // No TF2 bucket exists at all: 440 (and 440's neighbour 550's old report
    // home) proves the absence; TF2 lands in the default bucket.
    [InlineData(440)]
    // The SDK bases and everything else.
    [InlineData(218)]
    [InlineData(219)]
    [InlineData(243750)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1583721)]
    public void AnUnlistedAppidSelectsNothingAndIsNotAnError(int appid)
    {
        // The dump's default arm jumps to 0x140041dbe: applies nothing,
        // prints nothing.
        Assert.False(MapFormatPreset.TryFromSteamAppId(appid, out MapFormatPreset? preset));
        Assert.Null(preset);
    }

    [Fact]
    public void TheTableHoldsExactlyTheDumpConstants()
    {
        // Catches a member added without a dump citation.
        Assert.Equal(
            [220, 400, 550, 620, 630, 730, 17520, 222880, 243730, 362890, 447820, 563560, 714070, 1583720, 4465480],
            MapFormatPreset.AutoDetectTable.Keys.OrderBy(k => k));
    }
}
