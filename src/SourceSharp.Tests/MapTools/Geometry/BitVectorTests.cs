using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using SourceSharp.MapTools.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Geometry;

/// <summary>
/// The packed bit sets vvis and vrad work in, and the equivalence gate between
/// the 512-bit, 256-bit and scalar implementations of the two hot operations.
/// </summary>
public class BitVectorTests
{
    // ---- Layout -------------------------------------------------------------

    [Fact]
    public void WordsForRoundsUpToAWholeBlock()
    {
        // The padding is what removes the tail branch from the hot loop, so it
        // has to be a whole 512-bit block and not a whole word.
        Assert.Equal(8, BitVector.WordsFor(1));
        Assert.Equal(8, BitVector.WordsFor(512));
        Assert.Equal(16, BitVector.WordsFor(513));
    }

    [Fact]
    public void WordsForZeroBitsIsZeroWordsAndNotOne()
    {
        // A map with one cluster and no portals is legitimate, and every
        // operation is a no-op on an empty vector.
        Assert.Equal(0, BitVector.WordsFor(0));
    }

    [Fact]
    public void AllocateStartsZeroed()
    {
        // The invariant CountBits relies on: the padding past the caller's bit
        // count is zero, so no masking is needed.
        Assert.True(BitVectorOps.IsZero(BitVector.Allocate(4000)));
    }

    [Fact]
    public void BlockAlignmentIsAMultipleOfEightWords()
    {
        Assert.True(BitVector.IsBlockAligned(0));
        Assert.True(BitVector.IsBlockAligned(8));
        Assert.False(BitVector.IsBlockAligned(4));
    }

    [Fact]
    public void BitOrderMatchesStocksByteAddressing()
    {
        // vvis writes bits[i>>3] |= 1<<(i&7) over a byte array and elsewhere
        // casts the same bytes to long*. On little-endian, bit
        // 65 is byte 8 bit 1 -- so a vector built here can go straight into the
        // VISIBILITY lump with no reordering.
        ulong[] bits = BitVector.Allocate(128);
        BitVectorOps.SetBit(bits, 65);

        Span<byte> bytes = MemoryMarshal.AsBytes(bits.AsSpan());
        Assert.Equal(0b0000_0010, bytes[8]);
    }

    [Fact]
    public void SetAndGetAgreeAcrossAWordBoundary()
    {
        ulong[] bits = BitVector.Allocate(200);
        BitVectorOps.SetBit(bits, 63);
        BitVectorOps.SetBit(bits, 64);

        Assert.True(BitVectorOps.GetBit(bits, 63));
        Assert.True(BitVectorOps.GetBit(bits, 64));
        Assert.False(BitVectorOps.GetBit(bits, 65));
    }

    [Fact]
    public void ClearBitRemovesOnlyThatBit()
    {
        ulong[] bits = BitVector.Allocate(128);
        BitVectorOps.SetBit(bits, 10);
        BitVectorOps.SetBit(bits, 11);
        BitVectorOps.ClearBit(bits, 10);

        Assert.False(BitVectorOps.GetBit(bits, 10));
        Assert.True(BitVectorOps.GetBit(bits, 11));
    }

    // ---- CountBits ----------------------------------------------------------

    [Fact]
    public void CountBitsIsZeroForAnEmptyVector() =>
        Assert.Equal(0, BitVectorOps.CountBits(BitVector.Allocate(1000)));

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4242)]
    [InlineData(20250920)]
    public void PopcountAgreesWithStocksPerBitLoop(int seed)
    {
        // counts a bit at a time. BitOperations.PopCount is one
        // instruction, and this is the only thing that says the two agree.
        ulong[] bits = Random(seed, 64);
        Assert.Equal(
            BitVector.CountBitsReference(bits, 64 * 64),
            BitVectorOps.CountBits(bits));
    }

    // ---- The equivalence gate ----------------------------------------------

    /// <summary>
    /// The three implementations of vvis's inner loop must agree bit for bit.
    /// </summary>
    /// <param name="seed">Which pseudo-random input to use.</param>
    /// <remarks>
    /// Each path is called by NAME, so there is no dispatcher in the way that
    /// could quietly resolve all three to whichever one this CPU prefers.
    /// The widths are checked too: at 64 words the 512-bit loop runs 8
    /// iterations, the 256-bit one 16 and the scalar one 64, so a bug in the
    /// index arithmetic of any of them shows up as a difference rather than as
    /// three identical wrong answers.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(99)]
    [InlineData(1234)]
    [InlineData(20250920)]
    public void EveryPathComputesTheSameIntersection(int seed)
    {
        const int words = 64;
        ulong[] prev = Random(seed, words);
        ulong[] test = Random(seed + 7919, words);
        ulong[] vis = Random(seed + 104729, words);

        ulong[] scalar = new ulong[words];
        ulong[] wide256 = new ulong[words];
        ulong[] wide512 = new ulong[words];

        bool moreScalar = BitVectorOps.AndWithNewBitsScalar(prev, test, vis, scalar);
        bool more256 = BitVectorOps.AndWithNewBitsVector256(prev, test, vis, wide256);
        bool more512 = BitVectorOps.AndWithNewBitsVector512(prev, test, vis, wide512);

        Assert.Equal(scalar, wide256);
        Assert.Equal(scalar, wide512);
        Assert.Equal(moreScalar, more256);
        Assert.Equal(moreScalar, more512);
    }

    /// <summary>
    /// The three paths must also agree about <c>more</c> when the answer is
    /// actually in doubt.
    /// </summary>
    /// <param name="seed">Which pseudo-random input to use.</param>
    /// <remarks>
    /// The fact above compares <c>more</c> too, but on dense random input every
    /// path answers true and the comparison is vacuous. This was not a
    /// hypothetical: dropping the <c>~</c> from the 512-bit path left
    /// <c>EveryPathComputesTheSameIntersection</c> entirely green. So here the
    /// two decisive shapes are constructed rather than hoped for — <c>vis</c>
    /// containing the whole intersection, where the answer is false, and
    /// <c>vis</c> disjoint from it, where it is true. A path that confused
    /// <c>might &amp; ~vis</c> for <c>might &amp; vis</c> gets both backwards.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(4242)]
    [InlineData(20250920)]
    public void EveryPathAgreesAboutMoreWhenTheAnswerIsInDoubt(int seed)
    {
        const int words = 32;
        ulong[] prev = Random(seed, words);
        ulong[] test = Random(seed + 7919, words);

        ulong[] covering = new ulong[words];
        ulong[] disjoint = new ulong[words];
        for (int i = 0; i < words; i++)
        {
            covering[i] = prev[i] & test[i];
            disjoint[i] = ~covering[i];
        }

        ulong[] might = new ulong[words];

        Assert.False(BitVectorOps.AndWithNewBitsScalar(prev, test, covering, might));
        Assert.False(BitVectorOps.AndWithNewBitsVector256(prev, test, covering, might));
        Assert.False(BitVectorOps.AndWithNewBitsVector512(prev, test, covering, might));

        Assert.True(BitVectorOps.AndWithNewBitsScalar(prev, test, disjoint, might));
        Assert.True(BitVectorOps.AndWithNewBitsVector256(prev, test, disjoint, might));
        Assert.True(BitVectorOps.AndWithNewBitsVector512(prev, test, disjoint, might));
    }

    [Theory]
    [InlineData(BitVectorPath.Scalar)]
    [InlineData(BitVectorPath.Vector256)]
    [InlineData(BitVectorPath.Vector512)]
    public void SomethingNewIsReportedWhenNoCandidateBitIsVisibleYet(BitVectorPath path)
    {
        // The other direction from NothingNewIsReported...: with vis empty,
        // every bit of the intersection is new.
        ulong[] prev = Random(11, 8);
        ulong[] test = Random(13, 8);
        ulong[] might = new ulong[8];

        Assert.True(BitVectorOps.AndWithNewBits(prev, test, new ulong[8], might, path));
        Assert.NotEqual(0, BitVectorOps.CountBits(might));
    }

    /// <summary>
    /// And they must agree with the reference expression read one bit at a time.
    /// </summary>
    /// <param name="seed">Which pseudo-random input to use.</param>
    /// <remarks>
    /// Three implementations agreeing with each other would still leave them
    /// free to be wrong together, so this one is written from
    /// bit by bit with no word arithmetic in it at all.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4242)]
    [InlineData(20250920)]
    public void ThePathsAgreeWithABitByBitReadingOfTheCpp(int seed)
    {
        const int words = 16;
        const int bits = words * 64;
        ulong[] prev = Random(seed, words);
        ulong[] test = Random(seed + 7919, words);
        ulong[] vis = Random(seed + 104729, words);

        ulong[] expected = new ulong[words];
        bool expectedMore = false;
        for (int i = 0; i < bits; i++)
        {
            bool might = BitVectorOps.GetBit(prev, i) && BitVectorOps.GetBit(test, i);
            if (might)
            {
                BitVectorOps.SetBit(expected, i);
                if (!BitVectorOps.GetBit(vis, i))
                {
                    expectedMore = true;
                }
            }
        }

        ulong[] actual = new ulong[words];
        bool more = BitVectorOps.AndWithNewBits(prev, test, vis, actual);

        Assert.Equal(expected, actual);
        Assert.Equal(expectedMore, more);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(1234)]
    [InlineData(20250920)]
    public void EveryPathMergesIdentically(int seed)
    {
        const int words = 64;
        ulong[] source = Random(seed, words);

        ulong[] scalar = Random(seed + 11, words);
        ulong[] wide256 = [.. scalar];
        ulong[] wide512 = [.. scalar];

        bool changedScalar = BitVectorOps.OrIntoScalar(scalar, source);
        bool changed256 = BitVectorOps.OrIntoVector256(wide256, source);
        bool changed512 = BitVectorOps.OrIntoVector512(wide512, source);

        Assert.Equal(scalar, wide256);
        Assert.Equal(scalar, wide512);
        Assert.Equal(changedScalar, changed256);
        Assert.Equal(changedScalar, changed512);
    }

    [Theory]
    [InlineData(BitVectorPath.Scalar)]
    [InlineData(BitVectorPath.Vector256)]
    [InlineData(BitVectorPath.Vector512)]
    public void MergingReportsNoChangeWhenTheSourceIsAlreadyContained(BitVectorPath path)
    {
        // The stop condition of a fixed-point flood. If this ever answered
        // "changed" spuriously the flood would not terminate.
        ulong[] destination = Random(3, 16);
        ulong[] source = new ulong[16];
        for (int i = 0; i < 16; i++)
        {
            source[i] = destination[i] & 0x0F0F_0F0F_0F0F_0F0FUL;
        }

        Assert.False(BitVectorOps.OrInto(destination, source, path));
    }

    [Theory]
    [InlineData(BitVectorPath.Scalar)]
    [InlineData(BitVectorPath.Vector256)]
    [InlineData(BitVectorPath.Vector512)]
    public void MergingReportsAChangeWhenOneNewBitArrives(BitVectorPath path)
    {
        ulong[] destination = new ulong[8];
        ulong[] source = new ulong[8];
        BitVectorOps.SetBit(source, 499);

        Assert.True(BitVectorOps.OrInto(destination, source, path));
        Assert.True(BitVectorOps.GetBit(destination, 499));
    }

    [Theory]
    [InlineData(BitVectorPath.Scalar)]
    [InlineData(BitVectorPath.Vector256)]
    [InlineData(BitVectorPath.Vector512)]
    public void NothingNewIsReportedWhenEveryCandidateBitIsAlreadyVisible(BitVectorPath path)
    {
        // The case that makes vvis skip a portal entirely.
        ulong[] prev = Random(11, 8);
        ulong[] test = Random(13, 8);
        ulong[] vis = new ulong[8];
        for (int i = 0; i < 8; i++)
        {
            vis[i] = prev[i] & test[i];
        }

        ulong[] might = new ulong[8];
        Assert.False(BitVectorOps.AndWithNewBits(prev, test, vis, might, path));
    }

    [Theory]
    [InlineData(BitVectorPath.Scalar)]
    [InlineData(BitVectorPath.Vector256)]
    [InlineData(BitVectorPath.Vector512)]
    public void TheIntersectionIsStillWrittenWhenNothingIsNew(BitVectorPath path)
    {
        // The two halves cannot be separated: the recursion descends with
        // `might` whether or not `more` was set.
        ulong[] prev = Random(11, 8);
        ulong[] test = Random(13, 8);
        ulong[] vis = new ulong[8];
        for (int i = 0; i < 8; i++)
        {
            vis[i] = prev[i] & test[i];
        }

        ulong[] might = new ulong[8];
        BitVectorOps.AndWithNewBits(prev, test, vis, might, path);
        Assert.Equal(vis, might);
    }

    [Theory]
    [InlineData(BitVectorPath.Scalar)]
    [InlineData(BitVectorPath.Vector256)]
    [InlineData(BitVectorPath.Vector512)]
    public void AnEmptyVectorIsHandledWithoutATailBranch(BitVectorPath path) =>
        Assert.False(BitVectorOps.AndWithNewBits([], [], [], [], path));

    // ---- Path selection -----------------------------------------------------

    [Fact]
    public void ResolveNeverReturnsAuto() =>
        Assert.NotEqual(BitVectorPath.Auto, BitVectorOps.Resolve(BitVectorPath.Auto));

    [Fact]
    public void ResolveReturnsAConcreteRequestUnchanged()
    {
        // Including one the hardware has no unit for: Vector512<T> is defined
        // everywhere.NET runs and the JIT emits a software sequence, which is
        // what lets the gate above be a plain [Theory].
        Assert.Equal(BitVectorPath.Scalar, BitVectorOps.Resolve(BitVectorPath.Scalar));
        Assert.Equal(BitVectorPath.Vector256, BitVectorOps.Resolve(BitVectorPath.Vector256));
        Assert.Equal(BitVectorPath.Vector512, BitVectorOps.Resolve(BitVectorPath.Vector512));
    }

    [Fact]
    public void AutoPicksTheWidestPathTheMachineAccelerates()
    {
        BitVectorPath expected = Vector512.IsHardwareAccelerated ? BitVectorPath.Vector512
            : Vector256.IsHardwareAccelerated ? BitVectorPath.Vector256
            : BitVectorPath.Scalar;

        Assert.Equal(expected, BitVectorOps.Resolve(BitVectorPath.Auto));
    }

    [Fact]
    public void AutoWidthMatchesTheResolvedPath()
    {
        int expected = BitVectorOps.Resolve(BitVectorPath.Auto) switch
        {
            BitVectorPath.Vector512 => 8,
            BitVectorPath.Vector256 => 4,
            _ => 1,
        };

        Assert.Equal(expected, BitVector.AutoWidthInWords);
    }

    [Fact]
    public void ResolveRejectsAnUndefinedPath() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BitVectorOps.Resolve((BitVectorPath)99));

    // ---- Argument checking --------------------------------------------------

    [Fact]
    public void AnUnalignedLengthIsRefusedRatherThanGrowingATail()
    {
        ulong[] four = new ulong[4];
        Assert.Throws<ArgumentException>(() =>
            BitVectorOps.AndWithNewBitsScalar(four, four, four, four));
    }

    [Fact]
    public void MismatchedLengthsAreRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            BitVectorOps.AndWithNewBitsScalar(new ulong[8], new ulong[16], new ulong[8], new ulong[8]));
    }

    [Fact]
    public void MergingRefusesMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            BitVectorOps.OrIntoScalar(new ulong[8], new ulong[16]));
    }

    [Fact]
    public void PopCountIsTheOneFromBitOperations()
    {
        // Pins the claim in CountBits's remarks rather than leaving it as a
        // comment: two set bits in one word, counted by the same primitive.
        Assert.Equal(2, BitOperations.PopCount(0b1001UL));
    }

    private static ulong[] Random(int seed, int words)
    {
        var random = new Random(seed);
        ulong[] bits = new ulong[words];
        for (int i = 0; i < words; i++)
        {
            bits[i] = ((ulong)(uint)random.Next() << 32) | (uint)random.Next();
        }

        return bits;
    }
}
