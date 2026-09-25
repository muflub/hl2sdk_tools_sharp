namespace SourceSharp.MapTools.Vis;

/// <summary>
/// The C runtime's <c>qsort</c>, as stock vvis runs it: <c>SortPortals</c>
/// (<c>src/utils/vvis/vvis.cpp:156</c>) sorts with <c>PComp</c>
/// (<c>vvis.cpp:127</c>), which compares <c>nummightsee</c> ONLY, so the
/// order of equal counts is whatever the runtime's sort leaves.
/// </summary>
/// <remarks>
/// <para>
/// The x64 tools link the runtime statically (<c>vvis_dll.dll</c> imports
/// only <c>tier0</c>, <c>vstdlib</c> and <c>KERNEL32</c>), so this is the
/// Microsoft C runtime's quicksort, not the host's: median of three over the
/// first, middle and last elements, the two-pointer partition that skips runs
/// equal to the partition element, the smaller side recursed first through an
/// explicit stack, and a selection sort ("shortsort", which moves the FIRST of
/// several equal maxima to the end) for partitions of at most eight.
/// </para>
/// <para>
/// Under <c>-tighten</c> the order decides which neighbours' finished
/// <c>portalvis</c> each flow reads (<see cref="VisTightening"/>), so the tie
/// order is part of the answer: sorted any other way, ties by ascending or by
/// descending index, dustbowl came out +2 PVS / +10 PAS bits and goldrush +6
/// PVS bits away from stock at one thread (p2c-findings.md).
/// </para>
/// </remarks>
internal static class VisStockSort
{
    /// <summary>Partitions of this many or fewer are selection-sorted.</summary>
    private const int Cutoff = 8;

    /// <summary>The runtime's stack: <c>8 * sizeof(void*) - 2</c> on x64.</summary>
    private const int StackSize = (8 * 8) - 2;

    /// <summary>Sorts in place, exactly as the runtime's <c>qsort</c> would.</summary>
    /// <param name="items">The elements.</param>
    /// <param name="compare">Negative, zero or positive, as <c>qsort</c>'s comparator.</param>
    internal static void Sort(Span<int> items, Comparison<int> compare)
    {
        int num = items.Length;
        if (num < 2)
        {
            return;
        }

        Span<int> loStack = stackalloc int[StackSize];
        Span<int> hiStack = stackalloc int[StackSize];
        int stack = 0;

        int lo = 0;
        int hi = num - 1;

        while (true)
        {
            int size = hi - lo + 1;
            if (size <= Cutoff)
            {
                ShortSort(items, lo, hi, compare);
            }
            else
            {
                int mid = lo + (size / 2);

                // The first, middle and last elements, sorted into order.
                if (compare(items[lo], items[mid]) > 0)
                {
                    Swap(items, lo, mid);
                }

                if (compare(items[lo], items[hi]) > 0)
                {
                    Swap(items, lo, hi);
                }

                if (compare(items[mid], items[hi]) > 0)
                {
                    Swap(items, mid, hi);
                }

                int loGuy = lo;
                int hiGuy = hi;

                while (true)
                {
                    if (mid > loGuy)
                    {
                        do
                        {
                            loGuy++;
                        }
                        while (loGuy < mid && compare(items[loGuy], items[mid]) <= 0);
                    }

                    if (mid <= loGuy)
                    {
                        do
                        {
                            loGuy++;
                        }
                        while (loGuy <= hi && compare(items[loGuy], items[mid]) <= 0);
                    }

                    do
                    {
                        hiGuy--;
                    }
                    while (hiGuy > mid && compare(items[hiGuy], items[mid]) > 0);

                    if (hiGuy < loGuy)
                    {
                        break;
                    }

                    Swap(items, loGuy, hiGuy);

                    // The partition element moved: follow it.
                    if (mid == hiGuy)
                    {
                        mid = loGuy;
                    }
                }

                // Skip the run of elements equal to the partition element on
                // the high side of the lower partition.
                hiGuy++;
                if (mid < hiGuy)
                {
                    do
                    {
                        hiGuy--;
                    }
                    while (hiGuy > mid && compare(items[hiGuy], items[mid]) == 0);
                }

                if (mid >= hiGuy)
                {
                    do
                    {
                        hiGuy--;
                    }
                    while (hiGuy > lo && compare(items[hiGuy], items[mid]) == 0);
                }

                // The smaller side next, the larger one saved.
                if (hiGuy - lo >= hi - loGuy)
                {
                    if (lo < hiGuy)
                    {
                        loStack[stack] = lo;
                        hiStack[stack] = hiGuy;
                        stack++;
                    }

                    if (loGuy < hi)
                    {
                        lo = loGuy;
                        continue;
                    }
                }
                else
                {
                    if (loGuy < hi)
                    {
                        loStack[stack] = loGuy;
                        hiStack[stack] = hi;
                        stack++;
                    }

                    if (lo < hiGuy)
                    {
                        hi = hiGuy;
                        continue;
                    }
                }
            }

            stack--;
            if (stack < 0)
            {
                return;
            }

            lo = loStack[stack];
            hi = hiStack[stack];
        }
    }

    /// <summary>
    /// The runtime's <c>shortsort</c>: repeatedly swap the maximum -- the
    /// FIRST of equal maxima -- to the end.
    /// </summary>
    private static void ShortSort(Span<int> items, int lo, int hi, Comparison<int> compare)
    {
        while (hi > lo)
        {
            int max = lo;
            for (int p = lo + 1; p <= hi; p++)
            {
                if (compare(items[p], items[max]) > 0)
                {
                    max = p;
                }
            }

            Swap(items, max, hi);
            hi--;
        }
    }

    private static void Swap(Span<int> items, int a, int b)
    {
        if (a != b)
        {
            (items[a], items[b]) = (items[b], items[a]);
        }
    }
}
