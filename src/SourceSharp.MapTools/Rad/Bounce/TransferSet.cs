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
/// Every patch's transfer list, in ONE flat arena with a per-patch
/// (offset, count).
/// </summary>
/// <remarks>
/// <para>
/// Stock allocates each patch's list separately (<c>calloc</c> in
/// <c>MakeScales</c>) -- hundreds of thousands of small
/// allocations on a real map -- and stages them through three
/// <c>MAX_PATCHES</c>-sized arrays per thread.
/// Here they are one array. A patch that never shoots (a parent, a sky patch,
/// a patch in no cluster) has a count of zero.
/// </para>
/// <para>
/// A patch's list is in the order stock builds it -- the order of the
/// visibility tests in <c>BuildVisRow</c> -- because <c>GatherLight</c> sums
/// over it in that order and float addition is not associative.
/// </para>
/// </remarks>
public sealed class TransferSet
{
    private readonly Transfer[] _arena;
    private readonly long[] _offsets;
    private readonly int[] _counts;

    internal TransferSet(Transfer[] arena, long[] offsets, int[] counts, int max)
    {
        _arena = arena;
        _offsets = offsets;
        _counts = counts;
        Max = max;
    }

    /// <summary>How many patches this covers: every patch, shooting or not.</summary>
    public int PatchCount => _counts.Length;

    /// <summary><c>total_transfer</c>, the first number of "transfers %d, max %d".</summary>
    public long Total => _arena.LongLength;

    /// <summary><c>max_transfer</c>: the longest single patch's list.</summary>
    public int Max { get; }

    /// <summary>Every transfer, patch after patch.</summary>
    public ReadOnlySpan<Transfer> Arena => _arena;

    /// <summary>One patch's transfers, in stock's order.</summary>
    /// <param name="patch">The receiving patch.</param>
    /// <returns>Its list; empty for a patch with none.</returns>
    public ReadOnlySpan<Transfer> For(int patch) =>
        _arena.AsSpan((int)_offsets[patch], _counts[patch]);

    /// <summary><c>numtransfers</c> of one patch.</summary>
    /// <param name="patch">The patch.</param>
    /// <returns>How many transfers it has.</returns>
    public int CountFor(int patch) => _counts[patch];
}
