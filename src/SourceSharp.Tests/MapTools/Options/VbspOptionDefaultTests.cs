using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// Every default on <see cref="VbspOptions"/> is stock's default, checked
/// against the line of <c>src/utils/vbsp/vbsp.cpp</c> it was read from.
/// </summary>
/// <remarks>
/// These are the facts that keep the record honest as it is edited. A default
/// that drifts from stock's is not a style difference: it silently compiles a
/// different map from the same command line.
/// </remarks>
public class VbspOptionDefaultTests
{
    public static TheoryData<string> BooleanOptions => OptionAssert.BooleanProperties<VbspOptions>();

    [Theory]
    [MemberData(nameof(BooleanOptions))]
    public void EveryBooleanOptionDefaultsToFalse(string property)
    {
        // Stock's booleans are file-scope globals in vbsp.cpp:31-58; the ones
        // with an initialiser say `= false`, and C zero-initialises the rest.
        // There is no vbsp switch that is on by default.
        Assert.False(OptionAssert.BooleanValue(VbspOptions.Default, property));
    }

    [Fact]
    public void MicroVolumeDefaultsToStockOne()
    {
        // vbsp.cpp:30 -- `vec_t microvolume = 1.0;`
        Assert.Equal(1.0f, VbspOptions.Default.MicroVolume);
    }

    [Fact]
    public void LuxelScaleDefaultsToStockOne()
    {
        // vbsp.cpp:61 -- `float g_luxelScale = 1.0f;`
        Assert.Equal(1.0f, VbspOptions.Default.LuxelScale);
    }

    [Fact]
    public void MinLuxelScaleDefaultsToStockOne()
    {
        // vbsp.cpp:62 -- `float g_minLuxelScale = 1.0f;`
        Assert.Equal(1.0f, VbspOptions.Default.MinLuxelScale);
    }

    [Fact]
    public void DxLevelDefaultsToStockZero()
    {
        // vbsp.cpp:65 -- `int g_nDXLevel = 0;`, whose comment reads "default
        // dxlevel if you don't specify it on the command-line".
        Assert.Equal(0, VbspOptions.Default.DxLevel);
    }

    [Fact]
    public void EmbedDirectoryDefaultsToNothing()
    {
        // vbsp.cpp:69 -- `char g_szEmbedDir[MAX_PATH] = { 0 };`
        Assert.Null(VbspOptions.Default.EmbedDirectory);
    }

    [Fact]
    public void BlocksDefaultToTheWholeGrid()
    {
        // vbsp.cpp:80 -- block_xl/yl = BLOCKS_MIN, block_xh/yh = BLOCKS_MAX.
        Assert.Equal(BspBlockGrid.Full, VbspOptions.Default.Blocks);
    }

    [Fact]
    public void TheFullGridIsStocksComputedBounds()
    {
        // BLOCKS_SIZE 1024 (vbsp.cpp:73) into COORD_EXTENT 2*16384
        // (worldsize.h:19,28) gives BLOCKS_SPACE 32, so BLOCKS_MIN is -16
        // (vbsp.cpp:77) and BLOCKS_MAX is 15 (vbsp.cpp:78). Computed here the
        // same way rather than written out, so the constant and its derivation
        // cannot disagree.
        const int blocksSize = 1024;
        const int coordExtent = 2 * 16384;
        const int blocksSpace = coordExtent / blocksSize;

        Assert.Equal(new BspBlockGrid(-(blocksSpace / 2), -(blocksSpace / 2), (blocksSpace / 2) - 1, (blocksSpace / 2) - 1), BspBlockGrid.Full);
    }

    [Fact]
    public void SingleBlockCoversExactlyThatBlock()
    {
        Assert.Equal(new BspBlockGrid(3, -4, 3, -4), BspBlockGrid.Single(3, -4));
    }

    [Fact]
    public void DefaultIsTheSameAsAFreshRecord()
    {
        Assert.Equal(new VbspOptions(), VbspOptions.Default);
    }

    [Fact]
    public void OnlyEntsDefaultChangesOnlyOnlyEnts()
    {
        OptionAssert.OnlyChanged(VbspOptions.OnlyEntsDefault, VbspOptions.Default, nameof(VbspOptions.OnlyEnts), true);
    }

    [Fact]
    public void WithLeavesTheOriginalAlone()
    {
        // The record is immutable, which is what lets two compiles in one
        // process share one options value safely.
        VbspOptions original = VbspOptions.Default;
        VbspOptions changed = original with { NoWater = true };

        Assert.False(original.NoWater);
        Assert.True(changed.NoWater);
    }
}
