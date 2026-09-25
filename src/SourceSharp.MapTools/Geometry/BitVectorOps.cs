using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SourceSharp.MapTools.Geometry;

/// <summary>
/// The operations vvis and vrad spend their time in, over the packed bit sets
/// <see cref="BitVector"/> describes.
/// </summary>
/// <remarks>
/// <para>
/// Each of the two hot operations has three implementations — 512-bit, 256-bit
/// and scalar — and all three are PUBLIC and separately callable. That is
/// deliberate: it is the only way the equivalence gate can be honest. A single
/// method with a "which path" argument would leave a test unable to show that
/// the argument was honoured, and a test that only ran the dispatcher would
/// exercise whichever path this particular CPU happens to select. Naming the
/// three entry points means the gate calls three different pieces of code and
/// there is nothing in between to short-circuit.
/// </para>
/// <para>
/// All four spans of an operation must be the same length and that length must
/// be a whole number of blocks (see <see cref="BitVector.IsBlockAligned"/>).
/// That is what makes the loops tail-free.
/// </para>
/// </remarks>
public static class BitVectorOps
{
    /// <summary>
    /// Which concrete path a request resolves to on this machine.
    /// </summary>
    /// <param name="path">The requested path.</param>
    /// <returns>
    /// A concrete path, never <see cref="BitVectorPath.Auto"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="path"/> is not a defined value.
    /// </exception>
    /// <remarks>
    /// A concrete request is returned unchanged even when the hardware has no
    /// such unit. <see cref="Vector512{T}"/> and <see cref="Vector256{T}"/> are
    /// defined everywhere .NET runs and the JIT emits a software sequence when
    /// there is no instruction for them, so forcing a wide path on a narrow
    /// machine is slow but correct — which is what lets the gate be a plain
    /// <c>[Theory]</c> instead of something conditioned on the host.
    /// </remarks>
    public static BitVectorPath Resolve(BitVectorPath path) => path switch
    {
        BitVectorPath.Auto when Vector512.IsHardwareAccelerated => BitVectorPath.Vector512,
        BitVectorPath.Auto when Vector256.IsHardwareAccelerated => BitVectorPath.Vector256,
        BitVectorPath.Auto => BitVectorPath.Scalar,
        BitVectorPath.Scalar or BitVectorPath.Vector256 or BitVectorPath.Vector512 => path,
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };

    /// <summary>Reads one bit.</summary>
    /// <param name="bits">The vector.</param>
    /// <param name="index">Which bit.</param>
    /// <returns>True when it is set.</returns>
    /// <remarks>
    /// Stock's <c>CheckBit(bits, i)</c> over a byte array is
    /// <c>bits[i&gt;&gt;3] &amp; (1&lt;&lt;(i&amp;7))</c>; on a little-endian
    /// machine that is this expression over the same bytes read as words. See
    /// <see cref="BitVector"/>.
    /// </remarks>
    public static bool GetBit(ReadOnlySpan<ulong> bits, int index) =>
        (bits[index >> 6] & (1UL << (index & 63))) != 0;

    /// <summary>Sets one bit.</summary>
    /// <param name="bits">The vector.</param>
    /// <param name="index">Which bit.</param>
    public static void SetBit(Span<ulong> bits, int index) =>
        bits[index >> 6] |= 1UL << (index & 63);

    /// <summary>Clears one bit.</summary>
    /// <param name="bits">The vector.</param>
    /// <param name="index">Which bit.</param>
    public static void ClearBit(Span<ulong> bits, int index) =>
        bits[index >> 6] &= ~(1UL << (index & 63));

    /// <summary>Clears every bit.</summary>
    /// <param name="bits">The vector.</param>
    public static void Clear(Span<ulong> bits) => bits.Clear();

    /// <summary>How many bits are set.</summary>
    /// <param name="bits">The vector.</param>
    /// <returns>The population count.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="BitOperations.PopCount(ulong)"/>, which is one instruction.
    /// Stock counts a bit at a time and does it
    /// once per leaf when it prints the average cluster visibility, and again
    /// per cluster in <c>CalcVis</c>.
    /// </para>
    /// <para>
    /// There is no bit-count argument because the padding words past the
    /// caller's bit count are held at zero. That invariant is what makes this
    /// safe, and <see cref="BitVector.Allocate"/> establishes it.
    /// </para>
    /// </remarks>
    public static int CountBits(ReadOnlySpan<ulong> bits)
    {
        int count = 0;
        foreach (ulong word in bits)
        {
            count += BitOperations.PopCount(word);
        }

        return count;
    }

    /// <summary>Whether no bit is set.</summary>
    /// <param name="bits">The vector.</param>
    /// <returns>True when the vector is empty.</returns>
    public static bool IsZero(ReadOnlySpan<ulong> bits)
    {
        foreach (ulong word in bits)
        {
            if (word != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// vvis's inner loop: intersect two masks, and say whether the result holds
    /// anything not already visible.
    /// </summary>
    /// <param name="prev">The mask inherited from the previous stack level.</param>
    /// <param name="test">
    /// The candidate portal's <c>portalvis</c> or <c>portalflood</c>.
    /// </param>
    /// <param name="vis">What is already known to be visible.</param>
    /// <param name="might">Where the intersection is written.</param>
    /// <param name="path">Which implementation to run.</param>
    /// <returns>
    /// True when <paramref name="might"/> holds at least one bit that
    /// <paramref name="vis"/> does not.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The spans differ in length or are not block-aligned.
    /// </exception>
    /// <remarks>
    /// <para>
    ///:
    /// </para>
    /// <code>
    /// more = 0;
    /// for (j=0 ; j&lt;portallongs ; j++)
    /// {
    ///     might[j] = ((long *)prevstack-&gt;mightsee)[j] &amp; test[j];
    ///     more |= (might[j] &amp; ~vis[j]);
    /// }
    /// </code>
    /// <para>
    /// This is over 90% of vvis's runtime, and the two halves cannot be split
    /// apart: the intersection has to be KEPT — the recursion descends with it —
    /// as well as tested. Hence four spans rather than a function that returns a
    /// count.
    /// </para>
    /// <para>
    /// <c>more</c> is accumulated rather than tested per word, in stock as well
    /// as here, so the loop has no early exit and no branch in it at all.
    /// </para>
    /// </remarks>
    public static bool AndWithNewBits(
        ReadOnlySpan<ulong> prev,
        ReadOnlySpan<ulong> test,
        ReadOnlySpan<ulong> vis,
        Span<ulong> might,
        BitVectorPath path = BitVectorPath.Auto) => Resolve(path) switch
        {
            BitVectorPath.Vector512 => AndWithNewBitsVector512(prev, test, vis, might),
            BitVectorPath.Vector256 => AndWithNewBitsVector256(prev, test, vis, might),
            _ => AndWithNewBitsScalar(prev, test, vis, might),
        };

    /// <summary>
    /// <see cref="AndWithNewBits"/>, one word at a time.
    /// </summary>
    /// <param name="prev">The mask inherited from the previous stack level.</param>
    /// <param name="test">The candidate portal's mask.</param>
    /// <param name="vis">What is already known to be visible.</param>
    /// <param name="might">Where the intersection is written.</param>
    /// <returns>True when anything new was found.</returns>
    /// <exception cref="ArgumentException">
    /// The spans differ in length or are not block-aligned.
    /// </exception>
    /// <remarks>
    /// The reference implementation: the plain scalar loop. The
    /// two vector paths are checked against this one.
    /// </remarks>
    public static bool AndWithNewBitsScalar(
        ReadOnlySpan<ulong> prev,
        ReadOnlySpan<ulong> test,
        ReadOnlySpan<ulong> vis,
        Span<ulong> might)
    {
        int n = Validate(prev, test, vis, might);

        ulong more = 0;
        for (int j = 0; j < n; j++)
        {
            ulong m = prev[j] & test[j];
            might[j] = m;
            more |= m & ~vis[j];
        }

        return more != 0;
    }

    /// <summary>
    /// <see cref="AndWithNewBits"/>, four words at a time.
    /// </summary>
    /// <param name="prev">The mask inherited from the previous stack level.</param>
    /// <param name="test">The candidate portal's mask.</param>
    /// <param name="vis">What is already known to be visible.</param>
    /// <param name="might">Where the intersection is written.</param>
    /// <returns>True when anything new was found.</returns>
    /// <exception cref="ArgumentException">
    /// The spans differ in length or are not block-aligned.
    /// </exception>
    public static bool AndWithNewBitsVector256(
        ReadOnlySpan<ulong> prev,
        ReadOnlySpan<ulong> test,
        ReadOnlySpan<ulong> vis,
        Span<ulong> might)
    {
        int n = Validate(prev, test, vis, might);

        ref ulong p = ref MemoryMarshal.GetReference(prev);
        ref ulong t = ref MemoryMarshal.GetReference(test);
        ref ulong v = ref MemoryMarshal.GetReference(vis);
        ref ulong m = ref MemoryMarshal.GetReference(might);

        Vector256<ulong> more = Vector256<ulong>.Zero;
        for (nuint j = 0; j < (nuint)n; j += 4)
        {
            Vector256<ulong> mask =
                Vector256.LoadUnsafe(ref p, j) & Vector256.LoadUnsafe(ref t, j);
            mask.StoreUnsafe(ref m, j);
            more |= mask & ~Vector256.LoadUnsafe(ref v, j);
        }

        return more != Vector256<ulong>.Zero;
    }

    /// <summary>
    /// <see cref="AndWithNewBits"/>, eight words at a time.
    /// </summary>
    /// <param name="prev">The mask inherited from the previous stack level.</param>
    /// <param name="test">The candidate portal's mask.</param>
    /// <param name="vis">What is already known to be visible.</param>
    /// <param name="might">Where the intersection is written.</param>
    /// <returns>True when anything new was found.</returns>
    /// <exception cref="ArgumentException">
    /// The spans differ in length or are not block-aligned.
    /// </exception>
    public static bool AndWithNewBitsVector512(
        ReadOnlySpan<ulong> prev,
        ReadOnlySpan<ulong> test,
        ReadOnlySpan<ulong> vis,
        Span<ulong> might)
    {
        int n = Validate(prev, test, vis, might);

        ref ulong p = ref MemoryMarshal.GetReference(prev);
        ref ulong t = ref MemoryMarshal.GetReference(test);
        ref ulong v = ref MemoryMarshal.GetReference(vis);
        ref ulong m = ref MemoryMarshal.GetReference(might);

        Vector512<ulong> more = Vector512<ulong>.Zero;
        for (nuint j = 0; j < (nuint)n; j += 8)
        {
            Vector512<ulong> mask =
                Vector512.LoadUnsafe(ref p, j) & Vector512.LoadUnsafe(ref t, j);
            mask.StoreUnsafe(ref m, j);
            more |= mask & ~Vector512.LoadUnsafe(ref v, j);
        }

        return more != Vector512<ulong>.Zero;
    }

    /// <summary>
    /// Merges one vector into another, and says whether anything changed.
    /// </summary>
    /// <param name="destination">The vector to merge into.</param>
    /// <param name="source">The vector to merge in.</param>
    /// <param name="path">Which implementation to run.</param>
    /// <returns>True when <paramref name="destination"/> gained a bit.</returns>
    /// <exception cref="ArgumentException">
    /// The spans differ in length or are not block-aligned.
    /// </exception>
    /// <remarks>
    /// The accumulate half of vvis's <c>ClusterMerge</c> and of vrad's
    /// visibility gathering. The "did it change" answer is what a fixed-point
    /// flood needs to know when to stop, and computing it from the XOR of the
    /// old and new words costs nothing on top of the OR.
    /// </remarks>
    public static bool OrInto(
        Span<ulong> destination,
        ReadOnlySpan<ulong> source,
        BitVectorPath path = BitVectorPath.Auto) => Resolve(path) switch
        {
            BitVectorPath.Vector512 => OrIntoVector512(destination, source),
            BitVectorPath.Vector256 => OrIntoVector256(destination, source),
            _ => OrIntoScalar(destination, source),
        };

    /// <summary><see cref="OrInto"/>, one word at a time.</summary>
    /// <param name="destination">The vector to merge into.</param>
    /// <param name="source">The vector to merge in.</param>
    /// <returns>True when the destination gained a bit.</returns>
    /// <exception cref="ArgumentException">
    /// The spans differ in length or are not block-aligned.
    /// </exception>
    public static bool OrIntoScalar(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        int n = Validate(destination, source);

        ulong changed = 0;
        for (int j = 0; j < n; j++)
        {
            ulong before = destination[j];
            ulong after = before | source[j];
            destination[j] = after;
            changed |= after ^ before;
        }

        return changed != 0;
    }

    /// <summary><see cref="OrInto"/>, four words at a time.</summary>
    /// <param name="destination">The vector to merge into.</param>
    /// <param name="source">The vector to merge in.</param>
    /// <returns>True when the destination gained a bit.</returns>
    /// <exception cref="ArgumentException">
    /// The spans differ in length or are not block-aligned.
    /// </exception>
    public static bool OrIntoVector256(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        int n = Validate(destination, source);

        ref ulong d = ref MemoryMarshal.GetReference(destination);
        ref ulong s = ref MemoryMarshal.GetReference(source);

        Vector256<ulong> changed = Vector256<ulong>.Zero;
        for (nuint j = 0; j < (nuint)n; j += 4)
        {
            Vector256<ulong> before = Vector256.LoadUnsafe(ref d, j);
            Vector256<ulong> after = before | Vector256.LoadUnsafe(ref s, j);
            after.StoreUnsafe(ref d, j);
            changed |= after ^ before;
        }

        return changed != Vector256<ulong>.Zero;
    }

    /// <summary><see cref="OrInto"/>, eight words at a time.</summary>
    /// <param name="destination">The vector to merge into.</param>
    /// <param name="source">The vector to merge in.</param>
    /// <returns>True when the destination gained a bit.</returns>
    /// <exception cref="ArgumentException">
    /// The spans differ in length or are not block-aligned.
    /// </exception>
    public static bool OrIntoVector512(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        int n = Validate(destination, source);

        ref ulong d = ref MemoryMarshal.GetReference(destination);
        ref ulong s = ref MemoryMarshal.GetReference(source);

        Vector512<ulong> changed = Vector512<ulong>.Zero;
        for (nuint j = 0; j < (nuint)n; j += 8)
        {
            Vector512<ulong> before = Vector512.LoadUnsafe(ref d, j);
            Vector512<ulong> after = before | Vector512.LoadUnsafe(ref s, j);
            after.StoreUnsafe(ref d, j);
            changed |= after ^ before;
        }

        return changed != Vector512<ulong>.Zero;
    }

    private static int Validate(
        ReadOnlySpan<ulong> prev,
        ReadOnlySpan<ulong> test,
        ReadOnlySpan<ulong> vis,
        Span<ulong> might)
    {
        int n = prev.Length;
        if (test.Length != n || vis.Length != n || might.Length != n)
        {
            throw new ArgumentException(
                "Bit vectors must be the same length.", nameof(might));
        }

        if (!BitVector.IsBlockAligned(n))
        {
            throw new ArgumentException(
                $"Bit vector length {n} is not a multiple of {BitVector.WordsPerBlock} words.",
                nameof(prev));
        }

        return n;
    }

    private static int Validate(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        int n = destination.Length;
        if (source.Length != n)
        {
            throw new ArgumentException(
                "Bit vectors must be the same length.", nameof(source));
        }

        if (!BitVector.IsBlockAligned(n))
        {
            throw new ArgumentException(
                $"Bit vector length {n} is not a multiple of {BitVector.WordsPerBlock} words.",
                nameof(destination));
        }

        return n;
    }
}
