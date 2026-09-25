using System.Runtime.Intrinsics;

namespace SourceSharp.MapTools.Geometry;

/// <summary>
/// The layout rules for the packed bit sets vvis and vrad work in: PVS, PAS,
/// the per-portal <c>mightsee</c> and <c>portalflood</c> masks, and vrad's
/// visibility matrix.
/// </summary>
/// <remarks>
/// <para>
/// A bit vector here is a <c>ulong[]</c>, padded up to a whole number of
/// 512-bit blocks. The padding is not an optimisation detail, it is what lets
/// the hot loop be written as a straight vector loop with no scalar tail and no
/// trailing branch — and the tail branch is what would otherwise cost the most,
/// because vvis's inner loop is run once per portal pair and the vectors are
/// short (a 4000-cluster map is 63 words).
/// </para>
/// <para>
/// <b>The bit order matches stock's byte arrays exactly.</b> vvis addresses bits
/// as <c>bits[i&gt;&gt;3] &amp; (1&lt;&lt;(i&amp;7))</c>
/// (<c>public/bitvec.h</c>, used all through <c>utils/vvis/flow.cpp</c>), and it
/// also casts those same bytes to <c>long*</c> and works on them word-wise
/// (<c>flow.cpp:531</c>). On a little-endian machine — the only kind Source
/// ships the tools for — bit <c>i</c> of that byte array is bit
/// <c>i &amp; 63</c> of word <c>i &gt;&gt; 6</c>, which is what
/// <see cref="BitVectorOps.GetBit"/> does. So a vector built here can be
/// written straight into the VISIBILITY lump after RLE compression with no
/// reordering.
/// </para>
/// <para>
/// The padding bits past the caller's bit count are held at zero by
/// <see cref="Allocate"/> and every operation preserves that, so
/// <see cref="BitVectorOps.CountBits"/> needs no masking.
/// </para>
/// </remarks>
public static class BitVector
{
    /// <summary>Bits in one word.</summary>
    public const int BitsPerWord = 64;

    /// <summary>
    /// Words in one block: the width of the widest vector path.
    /// </summary>
    /// <remarks>
    /// Eight <see cref="ulong"/>s is one <see cref="System.Runtime.Intrinsics.Vector512{T}"/>.
    /// Every vector is padded to a multiple of this, so the 256-bit and scalar
    /// paths — which divide into it evenly — are also tail-free.
    /// </remarks>
    public const int WordsPerBlock = 8;

    /// <summary>How many words a vector of the given bit count needs.</summary>
    /// <param name="bitCount">How many bits the caller wants.</param>
    /// <returns>The padded word count, a multiple of <see cref="WordsPerBlock"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bitCount"/> is negative.
    /// </exception>
    /// <remarks>
    /// Zero bits give zero words, not one: an empty vector is a legitimate
    /// thing for a map with one cluster and no portals, and every operation
    /// below is a no-op on it.
    /// </remarks>
    public static int WordsFor(int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        int words = (bitCount + (BitsPerWord - 1)) / BitsPerWord;
        return (words + (WordsPerBlock - 1)) / WordsPerBlock * WordsPerBlock;
    }

    /// <summary>A zeroed vector with room for the given number of bits.</summary>
    /// <param name="bitCount">How many bits the caller wants.</param>
    /// <returns>The vector.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bitCount"/> is negative.
    /// </exception>
    public static ulong[] Allocate(int bitCount) => new ulong[WordsFor(bitCount)];

    /// <summary>
    /// Whether a word count is a whole number of blocks, as every operation
    /// requires.
    /// </summary>
    /// <param name="wordCount">The length to check.</param>
    /// <returns>True when it is.</returns>
    public static bool IsBlockAligned(int wordCount) =>
        wordCount >= 0 && wordCount % WordsPerBlock == 0;

    /// <summary>
    /// How many bits a vector of this many words can hold, padding included.
    /// </summary>
    /// <param name="wordCount">The word count.</param>
    /// <returns>The bit capacity.</returns>
    public static long CapacityInBits(int wordCount) => (long)wordCount * BitsPerWord;

    /// <summary>
    /// The number of bits set, counted the slow way stock does.
    /// </summary>
    /// <param name="bits">The vector.</param>
    /// <param name="bitCount">How many bits to consider.</param>
    /// <returns>The population count.</returns>
    /// <remarks>
    /// <para>
    /// This is stock's <c>CountBits</c>, <c>utils/vvis/flow.cpp:32</c>: a loop
    /// over every bit calling <c>CheckBit</c>. It is kept, and it is kept
    /// SLOW, because it is the reference the fast one is checked against — a
    /// popcount that agreed with a second popcount written the same way would
    /// prove nothing.
    /// </para>
    /// <para>
    /// Nothing in the compilers should call this. Use
    /// <see cref="BitVectorOps.CountBits"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bitCount"/> is negative or exceeds the vector.
    /// </exception>
    public static int CountBitsReference(ReadOnlySpan<ulong> bits, int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, CapacityInBits(bits.Length));

        int c = 0;
        for (int i = 0; i < bitCount; i++)
        {
            if (BitVectorOps.GetBit(bits, i))
            {
                c++;
            }
        }

        return c;
    }

    /// <summary>
    /// How wide the vectors are that <see cref="BitVectorPath.Auto"/> resolves
    /// to on this machine, in words.
    /// </summary>
    /// <remarks>
    /// A computed property over <see cref="Vector512"/> and
    /// <see cref="Vector256"/>'s hardware-acceleration flags, not a cached
    /// field: a mutable static is forbidden here, and the JIT folds these flags
    /// to constants anyway.
    /// </remarks>
    public static int AutoWidthInWords => BitVectorOps.Resolve(BitVectorPath.Auto) switch
    {
        BitVectorPath.Vector512 => 8,
        BitVectorPath.Vector256 => 4,
        _ => 1,
    };
}
