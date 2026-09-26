//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// One worker's record of the reads its current <c>-tighten</c> walk made of
/// neighbours that had NOT finished, per (repair-tree node, neighbour), until
/// the walk hands them to the portal's <see cref="VisRepairTree"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a speculative read is.</b> Under <c>-tighten</c> the portal at rank
/// <c>k</c> tests a lower-ranked candidate <c>q</c> against <c>q</c>'s FINAL
/// <c>portalvis</c>(as stock reads it at one thread).
/// When <c>q</c> is still being flowed, this reads <c>q</c>'s vector as it
/// stands instead. Bits of a <c>portalvis</c> only ever go from clear to set,
/// and every bit a tightened flow sets is a bit of its final answer (see
/// <see cref="VisTightening"/>), so whatever the read sees -- at whatever
/// granularity another thread's stores land -- is a SUBSET of <c>q</c>'s final
/// vector.
/// </para>
/// <para>
/// <b>What it could have missed.</b> The exact read would have produced
/// <c>prev AND final(q)</c> where this produced <c>prev AND seen(q)</c>; they
/// differ exactly on <c>prev AND NOT seen(q) AND final(q)</c>. The first two
/// factors are known at the read, so they are OR-ed into one vector per (node,
/// candidate) (<see cref="AndSpeculative"/>), and once <c>q</c> finishes, every
/// read of it charged to that node was exact if and only if that vector shares
/// no bit with <c>final(q)</c>. <c>seen(q)</c> is read ONCE per word and the
/// same word feeds both the intersection and the record, so a store landing
/// between the two cannot make the record claim less than the read missed.
/// </para>
/// <para>
/// Kept per worker, not on the tree, because a portal's walk may be split
/// between workers and the reads are the hot path; the tree takes them under
/// its lock when each worker's piece of the walk ends.
/// </para>
/// </remarks>
internal sealed class VisSpeculativeReads
{
    private readonly int _words;
    private readonly Func<ulong[]> _rent;
    private readonly List<(int Node, int Portal, ulong[] Missed)> _reads = [];
    private readonly Dictionary<long, int> _index = [];

    /// <summary>Prepares one worker's record.</summary>
    /// <param name="words">The length of one portal vector, in words.</param>
    /// <param name="rent">Hands out a cleared vector of <paramref name="words"/> words.</param>
    internal VisSpeculativeReads(int words, Func<ulong[]> rent)
    {
        _words = words;
        _rent = rent;
    }

    /// <summary>How many (node, candidate) records are held.</summary>
    internal int Count => _reads.Count;

    /// <summary>
    /// Hands every record over and forgets them; the vectors now belong to
    /// the caller.
    /// </summary>
    /// <param name="into">Receives each (node, candidate, missed) record.</param>
    internal void MoveTo(List<(int Node, int Portal, ulong[] Missed)> into)
    {
        into.AddRange(_reads);
        _reads.Clear();
        _index.Clear();
    }

    /// <summary>
    /// <see cref="BitVectorOps.AndWithNewBits"/> against a vector that another
    /// worker may still be writing, recording what the read could have missed
    /// against <paramref name="node"/>.
    /// </summary>
    /// <param name="node">The repair-tree node the read is charged to.</param>
    /// <param name="portal">The candidate whose <c>portalvis</c> is being read.</param>
    /// <param name="prev">The mask inherited from the previous stack level.</param>
    /// <param name="seen">The candidate's <c>portalvis</c>, possibly still growing.</param>
    /// <param name="vis">What the flowing portal already knows it sees.</param>
    /// <param name="might">Where the intersection is written.</param>
    /// <returns>True when anything new was found.</returns>
    internal bool AndSpeculative(
        int node,
        int portal,
        ReadOnlySpan<ulong> prev,
        ReadOnlySpan<ulong> seen,
        ReadOnlySpan<ulong> vis,
        Span<ulong> might)
    {
        ulong[] missed = Missed(node, portal);
        int n = _words;
        if (prev.Length != n || seen.Length != n || vis.Length != n || might.Length != n)
        {
            throw new ArgumentException("Bit vectors must be the same length.", nameof(might));
        }

        ref ulong p = ref MemoryMarshal.GetReference(prev);
        ref ulong s = ref MemoryMarshal.GetReference(seen);
        ref ulong v = ref MemoryMarshal.GetReference(vis);
        ref ulong m = ref MemoryMarshal.GetReference(might);
        ref ulong x = ref MemoryMarshal.GetArrayDataReference(missed);

        if (Vector256.IsHardwareAccelerated && (n & 3) == 0)
        {
            Vector256<ulong> more = Vector256<ulong>.Zero;
            for (nuint j = 0; j < (nuint)n; j += 4)
            {
                Vector256<ulong> pw = Vector256.LoadUnsafe(ref p, j);
                Vector256<ulong> sw = Vector256.LoadUnsafe(ref s, j);
                Vector256<ulong> mw = pw & sw;
                mw.StoreUnsafe(ref m, j);
                more |= mw & ~Vector256.LoadUnsafe(ref v, j);
                (Vector256.LoadUnsafe(ref x, j) | (pw & ~sw)).StoreUnsafe(ref x, j);
            }

            return more != Vector256<ulong>.Zero;
        }

        ulong any = 0;
        for (int j = 0; j < n; j++)
        {
            ulong pw = Unsafe.Add(ref p, j);
            ulong sw = Unsafe.Add(ref s, j);
            ulong mw = pw & sw;
            Unsafe.Add(ref m, j) = mw;
            any |= mw & ~Unsafe.Add(ref v, j);
            Unsafe.Add(ref x, j) |= pw & ~sw;
        }

        return any != 0;
    }

    private ulong[] Missed(int node, int portal)
    {
        long key = ((long)node << 32) | (uint)portal;
        if (_index.TryGetValue(key, out int slot))
        {
            return _reads[slot].Missed;
        }

        ulong[] vector = _rent();
        _index[key] = _reads.Count;
        _reads.Add((node, portal, vector));
        return vector;
    }
}
