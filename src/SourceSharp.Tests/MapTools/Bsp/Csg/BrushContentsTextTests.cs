using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Materials;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// <c>PrintBrushContentsToString</c>, which Phase 3c's "mixed face contents"
/// message is built out of.
/// </summary>
public class BrushContentsTextTests
{
    [Fact]
    public void NoBitsIsAnEmptyString()
    {
        Assert.Equal(string.Empty, BrushContentsText.Describe(0));
    }

    [Fact]
    public void EveryNameCarriesATrailingSpace()
    {
        Assert.Equal("CONTENTS_SOLID ", BrushContentsText.Describe((int)BrushContents.Solid));
    }

    [Fact]
    public void NamesComeOutInStocksMacroOrderAndNotInBitOrder()
    {
        int contents = (int)(BrushContents.Detail | BrushContents.Solid | BrushContents.Water);

        Assert.Equal(
            "CONTENTS_SOLID CONTENTS_WATER CONTENTS_DETAIL ",
            BrushContentsText.Describe(contents));
    }

    /// <summary>
    /// Stock's <c>ADD_CONTENTS</c> list covers 26 of the 31 flags and omits
    /// these five, so a brush carrying only one of them renders as nothing at
    /// all.
    /// </summary>
    [Theory]
    [InlineData(BrushContents.Unused)]
    [InlineData(BrushContents.Unused6)]
    [InlineData(BrushContents.Team1)]
    [InlineData(BrushContents.Team2)]
    [InlineData(BrushContents.IgnoreNoDrawOpaque)]
    public void FiveFlagsHaveNoNameBecauseStocksListSkipsThem(BrushContents flag)
    {
        Assert.Equal(string.Empty, BrushContentsText.Describe((int)flag));
    }

    [Fact]
    public void TheNamedSetIsExactlyTwentySixFlags()
    {
        int all = 0;
        foreach (BrushContents flag in Enum.GetValues<BrushContents>())
        {
            all |= (int)flag;
        }

        string text = BrushContentsText.Describe(all);

        Assert.Equal(26, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
