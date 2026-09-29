//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// The per-worker slab that stands in for stock's <c>pstack_t</c>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <c>pstack_t</c> is a local variable of
/// <c>RecursiveLeafFlow</c> and it is about 8.7 KB, because its
/// <c>mightsee</c> is sized for <c>MAX_PORTALS</c> (65,536 bits) whatever the
/// map actually has. Stock gets away with that on a 1 MB Windows thread stack
/// only because real maps never recurse more than about a hundred deep -- which
/// is itself the empirical bound, since a map that went deeper would have
/// crashed stock for the last twenty-five years. A <c>stackalloc</c> of the
/// same shape in .NET would have the same ceiling and no way to report hitting
/// it: a managed stack overflow is not an exception, it is the process.
/// </para>
/// <para>
/// So the frames live on the heap, one array per depth, allocated the first
/// time that depth is reached and kept for the life of the worker. Sized from
/// the map's REAL portal count rather than <c>MAX_PORTALS</c>, which on a
/// typical map is a fiftieth of stock's frame.
/// </para>
/// <para>
/// <b>One array per depth, never one array resized.</b> The flow passes each
/// frame's storage to its child as a span, and a span into an array that a
/// deeper frame then reallocated would be a span into the old copy. Growing by
/// appending to the list cannot do that.
/// </para>
/// </remarks>
internal sealed class VisFrameStack
{
    /// <summary>Where one frame's chopped <c>pass</c> winding starts.</summary>
    /// <remarks>
    /// Stock's frame holds three windings behind a free list,
    /// which is the same budget arrived at the other way round. Here each has a
    /// fixed role, so there is no allocator and no "already free" error to hit.
    /// </remarks>
    internal const int PassOffset = 0;

    /// <summary>Where one frame's chopped <c>source</c> winding starts.</summary>
    internal const int SourceOffset = VisClip.MaxPointsOnFixedWinding;

    /// <summary>Where one frame's separator-clip output starts.</summary>
    internal const int ClipOffset = 2 * VisClip.MaxPointsOnFixedWinding;

    /// <summary>
    /// How many points one frame's winding storage holds.
    /// </summary>
    /// <remarks>
    /// The first two are chop outputs, which stock caps at
    /// <see cref="VisClip.MaxPointsOnFixedWinding"/>. The third is the
    /// separator clip's output, and that one needs the FULL
    /// <see cref="VisClip.MaxPointsOnWinding"/>: when no separating plane
    /// clips anything, <c>ClipToSeperators</c> hands back the target winding
    /// untouched, and the target can be a portal's own winding of up to
    /// sixty-four points.
    /// </remarks>
    private const int PointsPerFrame = ClipOffset + VisClip.MaxPointsOnWinding;

    /// <summary>
    /// How many separating planes one frame caches for one ordering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A derivation can produce at most <c>source.Length * pass.Length</c>
    /// planes, and both windings can in principle be a whole portal of
    /// <see cref="VisClip.MaxPointsOnWinding"/> points -- 4,096 planes, 64 KB a
    /// frame, and the cache would cost more than it saved on the one map that
    /// had such a portal. So the cache takes the frames it fits and the flow
    /// derives the rest inline: 256 covers a sixteen-by-sixteen pairing, and
    /// real portals are quads.
    /// </para>
    /// <para>
    /// This is only the CAP. What is actually allocated is
    /// <c>source.Length * pass.Length</c> planes per depth per ordering, which
    /// for the quads real portals are made of is sixteen -- 320 bytes, not the
    /// 4 KB the cap would reserve. See <see cref="SeparatorNormals"/> for what
    /// reserving the cap cost on a small map.
    /// </para>
    /// </remarks>
    internal const int MaxCachedSeparators = 256;

    private readonly List<ulong[]> _mightSee = [];
    private readonly List<(int Lo, int Hi)> _mightDirty = [];
    private readonly List<Vec3[]> _windings = [];
    private readonly List<Vec3[]> _separatorNormals = [];
    private readonly List<float[]> _separatorDistances = [];
    private readonly int _words;

    /// <summary>Creates a stack for one worker.</summary>
    /// <param name="portalCount">The map's memory-portal count.</param>
    internal VisFrameStack(int portalCount) => _words = BitVector.WordsFor(portalCount);

    /// <summary>One frame's <c>mightsee</c>.</summary>
    /// <param name="depth">The recursion depth, from one.</param>
    /// <returns>A vector of the map's portal width.</returns>
    internal ulong[] MightSee(int depth)
    {
        while (_mightSee.Count <= depth)
        {
            _mightSee.Add(new ulong[_words]);
            _mightDirty.Add((0, 0));
        }

        return _mightSee[depth];
    }

    /// <summary>
    /// One frame's <c>mightsee</c>, made zero everywhere outside the words
    /// the frame is about to write.
    /// </summary>
    /// <param name="depth">The recursion depth, from one.</param>
    /// <param name="lo">The first word the frame will write.</param>
    /// <param name="hi">One past the last word it will write.</param>
    /// <returns>A vector of the map's portal width.</returns>
    /// <remarks>
    /// <para>
    /// The flow writes a frame's vector only over the extent of its parent's
    /// (see <see cref="Extent(ReadOnlySpan{ulong})"/>), because outside that
    /// extent the intersection is zero. For the vector to still BE the
    /// intersection, the words outside must hold zeros, and they may not: the
    /// buffer is reused by every frame that reaches this depth, and an earlier
    /// frame may have had a wider extent.
    /// </para>
    /// <para>
    /// So each depth remembers the extent it last handed out, and this clears
    /// only the part of that which falls outside the new one. Sibling frames
    /// at one depth mostly have similar extents, so this is usually a pair of
    /// integer compares; it is never more than the words an earlier frame
    /// actually wrote.
    /// </para>
    /// </remarks>
    internal ulong[] MightSee(int depth, int lo, int hi)
    {
        ulong[] buffer = MightSee(depth);
        (int dirtyLo, int dirtyHi) = _mightDirty[depth];
        if (dirtyLo < dirtyHi)
        {
            if (dirtyLo < lo)
            {
                buffer.AsSpan(dirtyLo, Math.Min(dirtyHi, lo) - dirtyLo).Clear();
            }

            if (dirtyHi > hi)
            {
                int from = Math.Max(dirtyLo, hi);
                buffer.AsSpan(from, dirtyHi - from).Clear();
            }
        }

        _mightDirty[depth] = lo < hi ? (lo, hi) : (0, 0);
        return buffer;
    }

    /// <summary>
    /// The block-aligned range of words that holds every set bit of a vector.
    /// </summary>
    /// <param name="bits">A block-aligned bit vector.</param>
    /// <returns>
    /// <c>(Lo, Hi)</c>, multiples of <see cref="BitVector.WordsPerBlock"/>,
    /// with every word outside it zero; <c>(0, 0)</c> for an empty vector.
    /// </returns>
    internal static (int Lo, int Hi) Extent(ReadOnlySpan<ulong> bits) => Extent(bits, (0, bits.Length));

    /// <summary>
    /// <see cref="Extent(ReadOnlySpan{ulong})"/>, searching only a range the
    /// caller already knows holds every set bit.
    /// </summary>
    /// <param name="bits">A block-aligned bit vector.</param>
    /// <param name="within">A block-aligned range outside which every word is zero.</param>
    /// <returns>The narrowed range, still block-aligned.</returns>
    /// <remarks>
    /// Block-aligned because the intersection runs on
    /// <see cref="BitVectorOps.AndWithNewBits"/>, whose vector forms take
    /// whole blocks. Rounding outwards only ever adds words that are zero.
    /// </remarks>
    internal static (int Lo, int Hi) Extent(ReadOnlySpan<ulong> bits, (int Lo, int Hi) within)
    {
        const int block = BitVector.WordsPerBlock;
        if (Vector256.IsHardwareAccelerated
            && within.Lo % block == 0
            && within.Hi % block == 0
            && (uint)within.Lo <= (uint)within.Hi
            && (uint)within.Hi <= (uint)bits.Length)
        {
            return ExtentByBlocks(bits, within);
        }

        return ExtentScalar(bits, within);
    }

    /// <summary>
    /// <see cref="Extent(ReadOnlySpan{ulong}, ValueTuple{int, int})"/> a whole
    /// block -- two 256-bit loads -- at a time.
    /// </summary>
    /// <param name="bits">A block-aligned bit vector.</param>
    /// <param name="within">A block-aligned range inside it outside which every word is zero.</param>
    /// <returns>The narrowed range.</returns>
    /// <remarks>
    /// <para>
    /// The answer is a pair of block boundaries -- the start of the first
    /// block with a set bit and the end of the last -- so the word-by-word
    /// scan's only use of the exact word it stops at is to round it to its
    /// block. Asking each block at once whether it has a set bit finds the
    /// same two blocks, and so the same pair, with a quarter of the loads
    /// and none of the per-word branches: on 2fort a frame's range averaged
    /// 49 words of which 9 were set, so the scalar scan's branches were most
    /// of its cost.
    /// </para>
    /// <para>
    /// The range must start and end on block boundaries for the two to agree
    /// (a block straddling the range's edge would be rounded differently);
    /// every caller's range is either a whole vector or an earlier extent,
    /// both aligned, and <see cref="Extent(ReadOnlySpan{ulong}, ValueTuple{int, int})"/>
    /// sends anything else to the scalar scan.
    /// </para>
    /// </remarks>
    internal static (int Lo, int Hi) ExtentByBlocks(ReadOnlySpan<ulong> bits, (int Lo, int Hi) within)
    {
        const int block = BitVector.WordsPerBlock;
        ref ulong words = ref MemoryMarshal.GetReference(bits);
        int lo = within.Lo;
        int hi = within.Hi;
        while (lo < hi && !BlockHasBits(ref words, lo))
        {
            lo += block;
        }

        if (lo == hi)
        {
            return (0, 0);
        }

        while (!BlockHasBits(ref words, hi - block))
        {
            hi -= block;
        }

        return (lo, hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BlockHasBits(ref ulong words, int at) =>
        (Vector256.LoadUnsafe(ref words, (nuint)at) | Vector256.LoadUnsafe(ref words, (nuint)at + 4))
            != Vector256<ulong>.Zero;

    /// <summary>
    /// <see cref="Extent(ReadOnlySpan{ulong}, ValueTuple{int, int})"/> one word
    /// at a time: the definition, and the form any range not on block
    /// boundaries takes.
    /// </summary>
    /// <param name="bits">A block-aligned bit vector.</param>
    /// <param name="within">A range outside which every word is zero.</param>
    /// <returns>The narrowed range, still block-aligned.</returns>
    internal static (int Lo, int Hi) ExtentScalar(ReadOnlySpan<ulong> bits, (int Lo, int Hi) within)
    {
        int lo = within.Lo;
        int hi = within.Hi;
        while (lo < hi && bits[lo] == 0)
        {
            lo++;
        }

        if (lo == hi)
        {
            return (0, 0);
        }

        while (bits[hi - 1] == 0)
        {
            hi--;
        }

        const int mask = BitVector.WordsPerBlock - 1;
        return (lo & ~mask, Math.Min((hi + mask) & ~mask, within.Hi));
    }

    /// <summary>One frame's three winding buffers, end to end.</summary>
    /// <param name="depth">The recursion depth, from one.</param>
    /// <returns>
    /// Storage at <see cref="PassOffset"/>, <see cref="SourceOffset"/> and
    /// <see cref="ClipOffset"/>.
    /// </returns>
    internal Vec3[] Windings(int depth)
    {
        while (_windings.Count <= depth)
        {
            _windings.Add(new Vec3[PointsPerFrame]);
        }

        return _windings[depth];
    }

    /// <summary>
    /// One frame's separator-memo normals for one ordering.
    /// </summary>
    /// <param name="depth">The recursion depth, from one.</param>
    /// <param name="ordering">
    /// Zero for the source-then-pass derivation, one for the reverse -- the two
    /// Calls stock makes.
    /// </param>
    /// <param name="minimum">
    /// How many planes this frame could possibly derive, which is
    /// <c>source.Length * pass.Length</c>.
    /// </param>
    /// <returns>At least <paramref name="minimum"/> normals.</returns>
    /// <remarks>
    /// <b>Sized to the frame, not to the cap.</b> Handing out
    /// <see cref="MaxCachedSeparators"/> every time allocates 4 KB per depth
    /// per ordering per worker whatever the windings are, and on a map small
    /// enough that the whole compile is 30 ms that is 1,216 arrays and 4.9 MB
    /// of garbage for nothing: it measured 50 % slower on
    /// <c>l1_open_arena</c>. Real portals are quads, so the frames that
    /// actually occur want sixteen planes. The array is kept and reused at that
    /// depth, and only grows when a frame there needs more.
    /// </remarks>
    internal Vec3[] SeparatorNormals(int depth, int ordering, int minimum)
    {
        int slot = (depth * 2) + ordering;
        while (_separatorNormals.Count <= slot)
        {
            _separatorNormals.Add([]);
        }

        Vec3[] held = _separatorNormals[slot];
        if (held.Length < minimum)
        {
            held = new Vec3[minimum];
            _separatorNormals[slot] = held;
        }

        return held;
    }

    /// <summary>One frame's separator-memo distances for one ordering.</summary>
    /// <param name="depth">The recursion depth, from one.</param>
    /// <param name="ordering">Zero or one, as for <see cref="SeparatorNormals"/>.</param>
    /// <param name="minimum">How many planes this frame could derive.</param>
    /// <returns>At least <paramref name="minimum"/> distances.</returns>
    internal float[] SeparatorDistances(int depth, int ordering, int minimum)
    {
        int slot = (depth * 2) + ordering;
        while (_separatorDistances.Count <= slot)
        {
            _separatorDistances.Add([]);
        }

        float[] held = _separatorDistances[slot];
        if (held.Length < minimum)
        {
            held = new float[minimum];
            _separatorDistances[slot] = held;
        }

        return held;
    }

    /// <summary>The deepest frame this worker has ever needed.</summary>
    internal int HighWaterMark => Math.Max(_mightSee.Count - 1, 0);
}
