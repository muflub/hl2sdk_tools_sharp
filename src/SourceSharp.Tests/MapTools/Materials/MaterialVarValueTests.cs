using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;

using Xunit;

namespace SourceSharp.Tests.MapTools.Materials;

/// <summary>
/// The string-to-value conversions a VMT's values go through.
/// </summary>
public class MaterialVarValueTests
{
    [Fact]
    public void BracketedReflectivityIsThreeFloatsAsWritten()
    {
        Assert.True(MaterialVarValue.TryParseVector("[0.5 0.25 0.125]", out Vec3 value));
        Assert.Equal(new Vec3(0.5f, 0.25f, 0.125f), value);
    }

    [Fact]
    public void BracedReflectivityIsDividedByTwoHundredAndFiftyFive()
    {
        // The difference that turns a reflectivity of 1 into 255 if missed.
        Assert.True(MaterialVarValue.TryParseVector("{255 128 0}", out Vec3 value));
        Assert.Equal(1f, value.X, 5);
        Assert.Equal(128f / 255f, value.Y, 5);
        Assert.Equal(0f, value.Z, 5);
    }

    [Fact]
    public void AnUnbracketedNumberReplicatesAcrossAllThreeComponents()
    {
        Assert.True(MaterialVarValue.TryParseVector(".25", out Vec3 value));
        Assert.Equal(new Vec3(0.25f, 0.25f, 0.25f), value);
    }

    [Fact]
    public void AnAbsentReflectivityDoesNotParse()
    {
        Assert.False(MaterialVarValue.TryParseVector(null, out _));
    }

    [Fact]
    public void ExtraWhitespaceInsideTheBracketsIsIgnored()
    {
        Assert.True(MaterialVarValue.TryParseVector("[  1   0\t0 ]", out Vec3 value));
        Assert.Equal(new Vec3(1f, 0f, 0f), value);
    }

    [Fact]
    public void AFlagValueOfOneIsSet()
    {
        Assert.True(MaterialVarValue.IsFlagSet("1"));
    }

    [Fact]
    public void AFlagValueOfTrueIsNotSet()
    {
        // The asymmetry worth one fact of its own: MaterialVarFlags_t values
        // go through KeyValues::GetInt, which is atoi, and atoi("true") is 0.
        // vbsp's own %compile vars use StringIsTrue instead, where "true" IS
        // true -- see TheWordTrueSetsACompileVar.
        Assert.False(MaterialVarValue.IsFlagSet("true"));
    }

    [Fact]
    public void AFractionalFlagValueTruncatesToZero()
    {
        Assert.False(MaterialVarValue.IsFlagSet("0.9"));
    }

    [Fact]
    public void AtoiReadsTheLeadingDigitsAndStops()
    {
        Assert.Equal(12, MaterialVarValue.ToInt("12abc"));
    }

    [Fact]
    public void AtofOfANonNumberIsZero()
    {
        Assert.Equal(0f, MaterialVarValue.ToFloat("nope"));
    }

    [Fact]
    public void TheWordTrueSetsACompileVar()
    {
        Assert.True(MaterialCompileVars.IsTrue("TRUE"));
    }

    [Fact]
    public void TheDigitOneSetsACompileVar()
    {
        Assert.True(MaterialCompileVars.IsTrue("1"));
    }

    [Fact]
    public void TheWordYesDoesNotSetACompileVar()
    {
        // StringIsTrue tests exactly two spellings (textures.cpp:36-47), so a
        // material written "%compileNoDraw" "yes" draws.
        Assert.False(MaterialCompileVars.IsTrue("yes"));
    }

    [Fact]
    public void TheDigitTwoDoesNotSetACompileVar()
    {
        Assert.False(MaterialCompileVars.IsTrue("2"));
    }

    [Fact]
    public void AnAbsentCompileVarIsNotSet()
    {
        Assert.False(MaterialCompileVars.IsTrue(null));
    }

    [Fact]
    public void AllTwentyTwoCompileVariablesAreInTheTable()
    {
        // Enumerated from textures.cpp:85-261. The count is the fact: a
        // twenty-third variable added to vbsp, or one of these dropped, shows
        // up here rather than as a silently unflagged surface.
        Assert.Equal(22, MaterialCompileVars.All.Length);
    }

    [Fact]
    public void EveryCompileVariableHasItsOwnBit()
    {
        MaterialCompileFlags all = MaterialCompileFlags.None;

        foreach (MaterialCompileVar var in MaterialCompileVars.All)
        {
            Assert.Equal(MaterialCompileFlags.None, all & var.Flag);
            all |= var.Flag;
        }
    }

    [Fact]
    public void TheTwoVariablesThatAreNotSpelledCompileAreInTheTable()
    {
        // %playerClip and %noPortal. A table keyed on a "%compile" prefix
        // loses both, and a map's clip brushes stop being clip brushes.
        Assert.Contains(MaterialCompileVars.All, v => v.Name == "%playerClip");
        Assert.Contains(MaterialCompileVars.All, v => v.Name == "%noPortal");
    }
}
