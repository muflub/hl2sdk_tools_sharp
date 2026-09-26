//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// Facts for <c>lights.rad</c>, as the reference implementation's
/// <c>ReadLightFile</c> and <c>LightForString</c> define them.
/// </summary>
public class RadLightFileTests
{
    private static RadLightFile Parse(string text, RadLightOptions? options = null) =>
        RadLightFile.ParseAsync(text, options, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    [Fact]
    public void MaterialNameIsTheFirstWordOnTheLine()
    {
        // Layout: one %s.
        RadLightFile file = Parse("lights/white 255 255 255 200\n");

        Assert.Equal("lights/white", file.TexLights[0].Name);
    }

    [Fact]
    public void ThereIsNoCommentSyntaxSoASlashSlashLineBecomesATexlight()
    {
        // Nothing in the reference implementation strips '//', '#' or ';'. A line that
        // looks like a comment is parsed as a texlight named "//".
        RadLightFile file = Parse("// this is not a comment\n");

        Assert.Equal("//", file.TexLights[0].Name);
    }

    [Fact]
    public void OneNumberIsAGreyscaleIntensity()
    {
        // Layout: intensity[1] = intensity[2] = intensity[0].
        RadLightFile file = Parse("lights/grey 128\n");
        Vec3 intensity = file.TexLights[0].Intensity;

        Assert.Equal(intensity.X, intensity.Y);
        Assert.Equal(intensity.X, intensity.Z);
    }

    [Fact]
    public void ThreeNumbersAreAColour()
    {
        RadLightFile file = Parse("lights/red 255 0 0\n");
        Vec3 intensity = file.TexLights[0].Intensity;

        Assert.True(intensity.X > 0);
        Assert.Equal(0f, intensity.Y);
        Assert.Equal(0f, intensity.Z);
    }

    [Fact]
    public void ColourIsConvertedFromGammaToLinearWithAnExponentOf22()
    {
        // Pow(r / 255.0, 2.2) * 255.
        RadLightFile file = Parse("lights/half 128 128 128\n");

        Assert.Equal(
            (float)(Math.Pow(128 / 255.0, 2.2) * 255),
            file.TexLights[0].Intensity.X);
    }

    [Fact]
    public void FourthNumberIsABrightnessMultiplierNormalisedBy255()
    {
        // Layout: VectorScale(intensity, scaler / 255.0).
        // Not a plain factor: a "scale" of 255 means one times.
        RadLightFile plain = Parse("lights/a 255 255 255\n");
        RadLightFile scaled = Parse("lights/a 255 255 255 255\n");

        Assert.Equal(plain.TexLights[0].Intensity.X, scaled.TexLights[0].Intensity.X);
    }

    [Fact]
    public void EightNumbersAreTwoFourTuplesAndLdrTakesTheFirst()
    {
        // Fields 5 to 8 replace 1 to 4 under -hdr.
        RadLightFile ldr = Parse(
            "lights/a 255 0 0 255 0 0 255 255\n",
            new RadLightOptions(Hdr: false));

        Assert.True(ldr.TexLights[0].Intensity.X > 0);
        Assert.Equal(0f, ldr.TexLights[0].Intensity.Z);
    }

    [Fact]
    public void EightNumbersUnderHdrTakeTheSecondFourTuple()
    {
        RadLightFile hdr = Parse(
            "lights/a 255 0 0 255 0 0 255 255\n",
            new RadLightOptions(Hdr: true));

        Assert.Equal(0f, hdr.TexLights[0].Intensity.X);
        Assert.True(hdr.TexLights[0].Intensity.Z > 0);
    }

    [Fact]
    public void FiveNumbersIsNotTheTwoTupleFormAndFallsIntoTheErrorBranch()
    {
        // Tests for EXACTLY 8. Five, six and seven all reach
        // the default, which prints and returns false -- and the
        // entry is STORED ANYWAY because the reference implementation ignores the result.
        RadLightFile file = Parse("lights/a 255 255 255 200 1\n");

        Assert.False(file.TexLights[0].ValueParsed);
        Assert.Single(file.TexLights);
    }

    [Fact]
    public void NegativeComponentMakesTheWholeLightBlack()
    {
        RadLightFile file = Parse("lights/a -1 255 255\n");

        Assert.Equal(Vec3.Zero, file.TexLights[0].Intensity);
    }

    [Fact]
    public void GlobalLightScaleIsAppliedLast()
    {
        // Layout: VectorScale by `lightscale`, the -scale
        // command-line option, after everything else.
        RadLightFile plain = Parse("lights/a 255 255 255\n");
        RadLightFile doubled = Parse(
            "lights/a 255 255 255\n", new RadLightOptions(LightScale: 2.0f));

        Assert.Equal(plain.TexLights[0].Intensity.X * 2f, doubled.TexLights[0].Intensity.X);
    }

    [Fact]
    public void HdrPrefixedLineIsSkippedInLdr()
    {
        RadLightFile file = Parse("hdr:lights/a 255 255 255\n", new RadLightOptions(Hdr: false));

        Assert.Empty(file.TexLights);
    }

    [Fact]
    public void LdrPrefixedLineIsSkippedInHdr()
    {
        RadLightFile file = Parse("ldr:lights/a 255 255 255\n", new RadLightOptions(Hdr: true));

        Assert.Empty(file.TexLights);
    }

    [Fact]
    public void ModePrefixIsCaseInsensitive()
    {
        // Uses strnicmp.
        RadLightFile file = Parse("HDR:lights/a 255 255 255\n", new RadLightOptions(Hdr: true));

        Assert.Single(file.TexLights);
    }

    [Fact]
    public void ModePrefixMustStartAtColumnZero()
    {
        // The reference's prefix test runs BEFORE its whitespace skip,
        // so an indented "hdr:" is not a prefix -- it is the start of the
        // material name.
        RadLightFile file = Parse("  hdr:lights/a 255 255 255\n", new RadLightOptions(Hdr: false));

        Assert.Equal("hdr:lights/a", file.TexLights[0].Name);
    }

    [Fact]
    public void BothPrefixesAreStrippedBecauseTheChecksAreNotExclusive()
    {
        // Are two sequential ifs, not an if/else. So
        // "hdr:ldr:x" has BOTH stripped, and in LDR mode the second check
        // drops the line.
        RadLightFile ldr = Parse("hdr:ldr:lights/a 255 255 255\n", new RadLightOptions(Hdr: false));

        Assert.Empty(ldr.TexLights);
    }

    [Fact]
    public void NoshadowRecordsTheMaterialWithItsExtensionStripped()
    {
        // The reference truncates at the FIRST '.'.
        RadLightFile file = Parse("noshadow glass/window01.vmt\n");

        Assert.Equal(["glass/window01"], file.NonShadowCastingMaterials);
    }

    [Fact]
    public void NoshadowTruncatesAtTheFirstDotEvenMidPath()
    {
        // strchr finds the first '.', not the extension: "a.b/c" becomes "a".
        RadLightFile file = Parse("noshadow a.b/c\n");

        Assert.Equal(["a"], file.NonShadowCastingMaterials);
    }

    [Fact]
    public void ForcetextureshadowRecordsTheModel()
    {
        RadLightFile file = Parse("forcetextureshadow models/props/tree.mdl\n");

        Assert.Equal(["models/props/tree.mdl"], file.ForcedTextureShadowModels);
    }

    [Fact]
    public void DirectiveKeywordsAreCaseSensitive()
    {
        // sscanf matches the literal "noshadow " byte for byte, unlike the
        // hdr:/ldr: prefixes which use strnicmp. So "NoShadow" is a texlight.
        RadLightFile file = Parse("NoShadow glass/x\n");

        Assert.Empty(file.NonShadowCastingMaterials);
        Assert.Equal("NoShadow", file.TexLights[0].Name);
    }

    [Fact]
    public void ThereIsNoNoskyfillDirective()
    {
        // The plan's brief mentioned one; a case-insensitive search of the
        // reference finds nothing. Only hdr:, ldr:, noshadow and
        // forcetextureshadow exist, so this line is a
        // texlight named "noskyfill".
        RadLightFile file = Parse("noskyfill 1\n");

        Assert.Equal("noskyfill", file.TexLights[0].Name);
    }

    [Fact]
    public void BlankLineIsIgnoredSilently()
    {
        // A line of four characters or fewer produces no
        // message at all.
        RadLightFile file = Parse("\n\n");

        Assert.Empty(file.TexLights);
        Assert.Empty(file.IgnoredLines);
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        // LightForTexture, the reference implementation, uses Q_strcasecmp.
        RadLightFile file = Parse("lights/White 255 255 255\n");

        Assert.NotNull(file.Lookup("LIGHTS/WHITE"));
    }

    [Fact]
    public void TwoSpellingsOfTheSameNameBecomeTwoEntriesWithNoWarning()
    {
        // The mismatch: the duplicate check during a merge is strcmp
        // (the reference implementation, case SENSITIVE) while the lookup is Q_strcasecmp
        // (, case INSENSITIVE). So "WOOD" and "wood" coexist and the
        // FIRST in table order silently wins every lookup.
        RadLightFile first = Parse("WOOD 255 0 0\n");
        RadLightFile second = Parse("wood 0 0 255\n");

        IReadOnlyList<RadLightOverride> overrides = first.Merge(second);

        Assert.Empty(overrides);
        Assert.Equal(2, first.TexLights.Count);
        Assert.Equal(first.TexLights[0].Intensity, first.Lookup("wood"));
    }

    [Fact]
    public void LaterFileOverridesAnEarlierDefinitionInPlace()
    {
        // Writes to the EXISTING slot, keeps the
        // count, so the table does not grow.
        RadLightFile global = Parse(
            "lights/a 255 0 0\nlights/b 0 255 0\n",
            new RadLightOptions(SourceFile: "lights.rad"));
        RadLightFile level = Parse(
            "lights/a 0 0 255\n",
            new RadLightOptions(SourceFile: "mymap.rad"));

        global.Merge(level);

        Assert.Equal(2, global.TexLights.Count);
        Assert.Equal("lights/a", global.TexLights[0].Name);
        Assert.Equal(0f, global.TexLights[0].Intensity.X);
    }

    [Fact]
    public void OverrideFromTheSameFileIsFlaggedDifferentlyFromOneAcrossFiles()
    {
        // Reports a same-file duplicate as an "ERROR" (with an
        // embedded BEL), which is still only a Msg; reports a
        // cross-file one as a Warning.
        RadLightFile first = Parse("lights/a 255 0 0\n", new RadLightOptions(SourceFile: "x.rad"));
        RadLightFile again = Parse("lights/a 0 0 255\n", new RadLightOptions(SourceFile: "x.rad"));

        RadLightOverride entry = Assert.Single(first.Merge(again));

        Assert.True(entry.SameFile);
        Assert.False(entry.Redundant);
    }

    [Fact]
    public void RedefinitionWithTheSameValueIsFlaggedAsRedundant()
    {
        // "Redundant '%s' def in '%s' AND '%s'!".
        RadLightFile first = Parse("lights/a 255 0 0\n", new RadLightOptions(SourceFile: "x.rad"));
        RadLightFile again = Parse("lights/a 255 0 0\n", new RadLightOptions(SourceFile: "y.rad"));

        RadLightOverride entry = Assert.Single(first.Merge(again));

        Assert.True(entry.Redundant);
        Assert.False(entry.SameFile);
    }

    [Fact]
    public void MoreThanTheTexlightLimitIsFatal()
    {
        // The only condition in this parser that aborts
        // stock vrad.
        System.Text.StringBuilder text = new();
        for (int i = 0; i <= RadLightFile.MaxTexLights; i++)
        {
            text.Append("lights/l").Append(i).Append(" 255 255 255\n");
        }

        RadLightFileException error =
            Assert.Throws<RadLightFileException>(() => Parse(text.ToString()));

        Assert.Contains("Too many texlights", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyTheTexlightLimitIsAccepted()
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i < RadLightFile.MaxTexLights; i++)
        {
            text.Append("lights/l").Append(i).Append(" 255 255 255\n");
        }

        Assert.Equal(RadLightFile.MaxTexLights, Parse(text.ToString()).TexLights.Count);
    }

    [Fact]
    public async Task ReadAsyncOverAStreamMatchesParse()
    {
        using MemoryStream stream = new("lights/a 255 255 255\n"u8.ToArray());
        RadLightFile file = await RadLightFile.ReadAsync(stream, null, CancellationToken.None);

        Assert.Single(file.TexLights);
    }
}
