using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// <see cref="StockRandomStream"/> against golden sequences drawn from the shipped
/// <c>libvstdlib.so</c>, and against the live library when it can be loaded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two tiers.</b> vstdlib ships without sources, so the port is of a
/// published algorithm. The unit tier compares against
/// <see cref="StockRandomStreamVectors"/>, drawn from the reference library by
/// <c>gen_stock_random_vectors.py</c> (committed beside this file) and so needing
/// no native library at run time. The <c>[VstdlibFact]</c> facts repeat the
/// comparison against the live library, and one of them checks the table itself
/// against the binary so the stand-in cannot drift silently; they skip, with the
/// reason, when the library cannot be loaded (see <see cref="StockVstdlib"/>).
/// </para>
/// <para>
/// It matters more here than the word "random" suggests. The stream decides
/// every ambient sample POSITION, the positions decide every cube, and the
/// cubes decide how many records the lump has. One draw out of step is a
/// completely different map, not a slightly noisier one.
/// </para>
/// </remarks>
public sealed class StockRandomStreamTests
{
    /// <summary>How many draws each live comparison takes.</summary>
    private const int Draws = 100_000;

    [Fact]
    public void DefaultConstructedStreamMatchesTheGoldenSequence()
    {
        // CLeafSampler's stream: default-constructed.
        StockRandomStream managed = new();

        Assert.Equal(0, CountGoldenDifferences(StockRandomStreamVectors.DefaultUnit, ref managed, 0f, 1f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12345)]
    [InlineData(-3)]
    [InlineData(int.MaxValue)]
    public void SeededStreamMatchesTheGoldenSequence(int seed)
    {
        StockRandomStream managed = new(seed);

        Assert.Equal(0, CountGoldenDifferences(StockRandomStreamVectors.ForSeed(seed), ref managed, 0f, 1f));
    }

    [Fact]
    public void RangedDrawsMatchTheGoldenSequence()
    {
        // The sampler's own shape: zero to a leaf extent.
        StockRandomStream managed = new();

        Assert.Equal(
            0, CountGoldenDifferences(StockRandomStreamVectors.DefaultRanged1024, ref managed, 0f, 1024f));
    }

    [Fact]
    public void TheGoldenComparisonCanFail()
    {
        // Mutation proof: one draw out of step must disagree almost everywhere.
        StockRandomStream managed = new();
        _ = managed.RandomFloat();

        int different = CountGoldenDifferences(StockRandomStreamVectors.DefaultUnit, ref managed, 0f, 1f);

        Assert.True(different > StockRandomStreamVectors.Count * 9 / 10, $"only {different} differed");
    }


    [Fact]
    public void DefaultValueIsTheSameStreamAsTheDefaultConstructor()
    {
        // CUniformRandomStream's constructor calls SetSeed(0), and SetSeed(0)
        // leaves both state fields zero -- so a zeroed struct is already the
        // right state, and a caller that writes `default` must get the same
        // stream rather than a subtly different one.
        StockRandomStream constructed = new();
        StockRandomStream defaulted = default;

        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(constructed.GenerateRandomNumber(), defaulted.GenerateRandomNumber());
        }
    }

    [Fact]
    public void SeedSignIsFoldedSoPositiveAndNegativeSeedsAgree()
    {
        // SetSeed stores `iSeed < 0 ? iSeed: -iSeed`, so 7 and -7 name the
        // same stream. Reproduced rather than corrected.
        StockRandomStream positive = new(7);
        StockRandomStream negative = new(-7);

        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(positive.GenerateRandomNumber(), negative.GenerateRandomNumber());
        }
    }

    [Fact]
    public void DrawsFromAZeroWidthRangeAreExactlyTheLowBound()
    {
        // A leaf with no extent along one axis draws from (0, 0); the result
        // has to be exactly the bound or the 1/32 plane test flips.
        StockRandomStream stream = new();

        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(0.0f, stream.RandomFloat(0.0f, 0.0f));
        }
    }

    [Fact]
    public void EveryDrawIsBelowTheUpperBound()
    {
        // RandomFloat clamps at RNMX = 1 - 1.2e-7 before scaling, so a draw is
        // strictly below `high`.
        StockRandomStream stream = new();

        for (int i = 0; i < 100_000; i++)
        {
            float value = stream.RandomFloat(0.0f, 512.0f);
            Assert.InRange(value, 0.0f, MathF.BitDecrement(512.0f));
        }
    }

    /// <summary>Counts how many draws differ from a golden sequence bit for bit.</summary>
    /// <param name="golden">The expected bit patterns.</param>
    /// <param name="managed">The ported stream.</param>
    /// <param name="low">The draw's lower bound.</param>
    /// <param name="high">The draw's upper bound.</param>
    /// <returns>How many disagreed.</returns>
    private static int CountGoldenDifferences(
        ReadOnlySpan<uint> golden, ref StockRandomStream managed, float low, float high)
    {
        Assert.Equal(StockRandomStreamVectors.Count, golden.Length);

        int different = 0;
        foreach (uint expected in golden)
        {
            if (BitConverter.SingleToUInt32Bits(managed.RandomFloat(low, high)) != expected)
            {
                different++;
            }
        }

        return different;
    }
}
