using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <see cref="VisStockSort"/>: the C runtime's <c>qsort</c> that stock's
/// <c>SortPortals</c> (<c>vvis.cpp:156</c>) runs with <c>PComp</c>
/// (<c>vvis.cpp:127</c>), which compares counts only.
/// </summary>
public class VisStockSortTests
{
    private static int[] SortByKey(int[] keys)
    {
        int[] items = [.. Enumerable.Range(0, keys.Length)];
        VisStockSort.Sort(items, (a, b) => keys[a] == keys[b] ? 0 : keys[a] < keys[b] ? -1 : 1);
        return items;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(100)]
    [InlineData(5000)]
    public void ItSortsByKey(int count)
    {
        Random random = new(count);
        int[] keys = [.. Enumerable.Range(0, count).Select(_ => random.Next(0, 40))];

        int[] items = SortByKey(keys);

        Assert.Equal(Enumerable.Range(0, count), items.Order());
        for (int i = 1; i < count; i++)
        {
            Assert.True(keys[items[i - 1]] <= keys[items[i]]);
        }
    }

    [Fact]
    public void AShortRunOfEqualKeysComesOutAsTheRuntimesSelectionSortLeavesIt()
    {
        // shortsort: the FIRST of equal maxima is swapped to the end, each
        // pass. [a b c] -> max a to the end -> [c b a] -> max c to position 1
        // -> [b c a].
        Assert.Equal([1, 2, 0], SortByKey([5, 5, 5]));
    }

    [Fact]
    public void TiesComeOutNeitherAscendingNorDescending()
    {
        // Why neither index tie-break was stock's order. shortsort on keys
        // [1 0 1 0]: max is item 0 (the first 1) -> end: [3 1 2 0]; then item
        // 2 stays; then item 3 (the first 0) -> position 1: [1 3 2 0]. The
        // zeros come out ascending (1, 3), the ones descending (2, 0).
        Assert.Equal([1, 3, 2, 0], SortByKey([1, 0, 1, 0]));
    }

    [Fact]
    public void APartitionOfEqualKeysPastTheCutoffIsLeftAlone()
    {
        // Nine equal keys, one past the cutoff: the median of three swaps
        // nothing; loguy runs past hi (every element <= the partition
        // element), higuy stops at mid - 1; after the break the high side is
        // skipped as equal down to mid and then down to lo, so neither side
        // is pushed or recursed into and the range is never touched.
        Assert.Equal(Enumerable.Range(0, 9), SortByKey([.. Enumerable.Repeat(4, 9)]));
    }

    [Fact]
    public void EightEqualKeysAreShuffledBySelectionSort()
    {
        // At the cutoff the whole range goes through shortsort: each pass
        // moves the first remaining element to the end of what is left, so
        // the last element comes first ... traced: [1 2 3 4 5 6 7 0].
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 0], SortByKey([.. Enumerable.Repeat(4, 8)]));
    }

    [Fact]
    public void TheTightenRankingBreaksTiesAsTheRuntimesQsortDoes()
    {
        // Regression: the ranking used to break ties by index, which is not
        // the order stock's qsort leaves (dustbowl was +2 PVS / +10 PAS bits
        // from stock at one thread; with this order it is exact). Counts
        // [5 5 5 1], shortsort: the first 5 (item 0) swaps with the end ->
        // [3 1 2 0]; then the first 5 left (item 1) with position 2 ->
        // [3 2 1 0]; then item 2 stays. Index ties would give [3 0 1 2].
        VisPortalState state = new(4);
        int[] counts = [5, 5, 5, 1];
        for (int p = 0; p < counts.Length; p++)
        {
            state.SetMightSeeCount(p, counts[p]);
        }

        VisTightening tightening = new(state);

        Assert.Equal([3, 2, 1, 0], Enumerable.Range(0, 4).Select(tightening.PortalAt));
    }
}
