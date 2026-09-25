namespace SourceSharp.MapTools.Bsp.Detail;

/// <summary>
/// The Microsoft C runtime's <c>qsort</c>: median-of-three quicksort with an
/// explicit stack, falling to a selection sort for eight elements or fewer.
/// </summary>
/// <remarks>
/// <para>
/// Not stable, and that is the point of porting it: vbsp sorts the detail
/// props by leaf alone (<c>SortFunc</c>, <c>detailobjects.cpp:781-798</c>), so
/// the order of props within a leaf — which LUMP_GAME_LUMP stores — is
/// whatever this algorithm leaves. Like <see cref="MsvcRandom"/> it is the
/// specification of the output rather than a defect, and is reproduced
/// unconditionally.
/// </para>
/// <para>
/// Transcribed from the CRT's <c>qsort.c</c> (the VS2005-and-later shape, with
/// the partition that tracks the pivot's position as it is swapped): the
/// selection sort swaps the maximum to the END each pass, and the partition
/// skips elements EQUAL to the pivot on the left and stops on them on the
/// right, then collapses runs equal to the pivot before recursing on the
/// smaller side.
/// </para>
/// </remarks>
public static class MsvcQsort
{
    private const int Cutoff = 8;

    /// <summary>Sorts in place with the CRT's algorithm.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">The array segment to sort.</param>
    /// <param name="compare">The comparison, as the C callback.</param>
    public static void Sort<T>(Span<T> items, Comparison<T> compare)
    {
        ArgumentNullException.ThrowIfNull(compare);

        int num = items.Length;
        if (num < 2)
        {
            return;
        }

        Span<int> loStack = stackalloc int[64];
        Span<int> hiStack = stackalloc int[64];
        int stackPointer = 0;

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

                    if (mid == hiGuy)
                    {
                        mid = loGuy;
                    }
                }

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

                if (hiGuy - lo >= hi - loGuy)
                {
                    if (lo < hiGuy)
                    {
                        loStack[stackPointer] = lo;
                        hiStack[stackPointer] = hiGuy;
                        stackPointer++;
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
                        loStack[stackPointer] = loGuy;
                        hiStack[stackPointer] = hi;
                        stackPointer++;
                    }

                    if (lo < hiGuy)
                    {
                        hi = hiGuy;
                        continue;
                    }
                }
            }

            stackPointer--;
            if (stackPointer < 0)
            {
                return;
            }

            lo = loStack[stackPointer];
            hi = hiStack[stackPointer];
        }
    }

    // shortsort: the maximum of lo..hi to hi, repeatedly. "> 0" keeps the
    // FIRST of equal maxima as the one swapped.
    private static void ShortSort<T>(Span<T> items, int lo, int hi, Comparison<T> compare)
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

    private static void Swap<T>(Span<T> items, int a, int b)
    {
        if (a != b)
        {
            (items[a], items[b]) = (items[b], items[a]);
        }
    }
}
