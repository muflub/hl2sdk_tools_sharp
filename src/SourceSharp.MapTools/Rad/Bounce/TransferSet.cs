//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// <c>transfer_t</c>: how much of one patch's emitted
/// light another receives.
/// </summary>
/// <param name="Patch">The SHOOTING patch the light comes from.</param>
/// <param name="Weight">
/// The form factor times the shooter's area, scaled by <c>MakeScales</c> so a
/// receiver's weights sum to at most 1.
/// </param>
/// <remarks>
/// Eight bytes, as stock's is: the arena of these is the largest structure in
/// the bounce (31.8 million of them on ss_sandbox).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Transfer(int Patch, float Weight);

/// <summary>
/// Every patch's transfer list, in a few large segments with a per-patch
/// (segment, offset, count).
/// </summary>
/// <remarks>
/// <para>
/// Stock allocates each patch's list separately (<c>calloc</c> in
/// <c>MakeScales</c>) -- hundreds of thousands of small
/// allocations on a real map -- and stages them through three
/// <c>MAX_PATCHES</c>-sized arrays per thread.
/// Here each chunk of receivers the build traced together owns one array,
/// written in place by the build and never copied again: joining them into
/// one arena was a second full copy of the largest structure in the bounce,
/// made on one thread. A patch that never shoots (a parent, a sky patch, a
/// patch in no cluster) has a count of zero.
/// </para>
/// <para>
/// A patch's list is in the order stock builds it -- the order of the
/// visibility tests in <c>BuildVisRow</c> -- because <c>GatherLight</c> sums
/// over it in that order and float addition is not associative.
/// </para>
/// </remarks>
public sealed class TransferSet
{
    private readonly Transfer[][] _segments;
    private readonly int[] _segmentOf;
    private readonly int[] _offsets;
    private readonly int[] _counts;
    private Transfer[]? _joined;

    /// <summary>Wraps the lists.</summary>
    /// <param name="segments">The arrays the lists live in, in build order.</param>
    /// <param name="segmentOf">Per patch, which segment holds its list.</param>
    /// <param name="offsets">Per patch, where its list starts in that segment.</param>
    /// <param name="counts">Per patch, how long its list is.</param>
    /// <param name="max">The longest list.</param>
    internal TransferSet(Transfer[][] segments, int[] segmentOf, int[] offsets, int[] counts, int max)
    {
        _segments = segments;
        _segmentOf = segmentOf;
        _offsets = offsets;
        _counts = counts;
        Max = max;
        long total = 0;
        foreach (Transfer[] segment in segments)
        {
            total += segment.LongLength;
        }

        Total = total;
    }

    /// <summary>How many patches this covers: every patch, shooting or not.</summary>
    public int PatchCount => _counts.Length;

    /// <summary><c>total_transfer</c>, the first number of "transfers %d, max %d".</summary>
    public long Total { get; }

    /// <summary><c>max_transfer</c>: the longest single patch's list.</summary>
    public int Max { get; }

    /// <summary>
    /// Every transfer, segment after segment: the build's order, receiver
    /// after receiver.
    /// </summary>
    /// <remarks>
    /// Joined into one array on first use and kept, so it costs a copy of the
    /// whole set; the bounce itself reads <see cref="For"/> only.
    /// </remarks>
    public ReadOnlySpan<Transfer> Arena => _joined ??= Join();

    /// <summary>One patch's transfers, in stock's order.</summary>
    /// <param name="patch">The receiving patch.</param>
    /// <returns>Its list; empty for a patch with none.</returns>
    public ReadOnlySpan<Transfer> For(int patch)
    {
        int count = _counts[patch];
        return count == 0
            ? []
            : _segments[_segmentOf[patch]].AsSpan(_offsets[patch], count);
    }

    /// <summary><c>numtransfers</c> of one patch.</summary>
    /// <param name="patch">The patch.</param>
    /// <returns>How many transfers it has.</returns>
    public int CountFor(int patch) => _counts[patch];

    private Transfer[] Join()
    {
        Transfer[] joined = new Transfer[Total];
        long at = 0;
        foreach (Transfer[] segment in _segments)
        {
            segment.CopyTo(joined, at);
            at += segment.LongLength;
        }

        return joined;
    }
}
