//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;

using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// How far one portal has got: stock's <c>vstatus_t</c>
/// </summary>
public enum VisPortalStatus
{
    /// <summary>Not started (<c>stat_none</c>).</summary>
    None = 0,

    /// <summary>A worker is flowing it (<c>stat_working</c>).</summary>
    Working = 1,

    /// <summary>Its <c>portalvis</c> is final (<c>stat_done</c>).</summary>
    Done = 2,
}

/// <summary>
/// The mutable half of a vis computation: the two bit vectors that outlive a
/// work item, and the status, from what stock hangs off each <c>portal_t</c>
/// </summary>
/// <remarks>
/// <para>
/// One of these belongs to one <see cref="Vvis.ComputeAsync"/> call. The
/// geometry it is paired with (<see cref="PortalSet"/>) is immutable and could
/// be shared between concurrent compiles; this cannot, and the split is what
/// makes that obvious rather than a rule to remember.
/// </para>
/// <para>
/// Flat arrays with a stride, rather than an array of arrays: the vectors are
/// short (a 2,500-cluster map is a few hundred words) and there are two per
/// file portal, so the per-object overhead of tens of thousands of small arrays
/// would dwarf the data.
/// </para>
/// <para>
/// <b>Two vectors per portal, not stock's three.</b> <c>portalfront</c>
/// Is written and read entirely inside one portal's
/// <c>BasePortalVis</c> -- nothing in vvis ever looks at another portal's -- so
/// it is a WORKER's scratch here and lives on <see cref="VisFloodScratch"/>.
/// One vector per worker instead of one per portal is 20 MB saved on 2fort and
/// 536 MB on a map at the portal limit.
/// </para>
/// </remarks>
internal sealed class VisPortalState
{
    private readonly ulong[] _flood;
    private readonly ulong[] _vis;
    private readonly int[] _mightSee;
    private readonly int[] _status;

    /// <summary>Allocates the state for a set of memory portals.</summary>
    /// <param name="portalCount">How many memory portals there are.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="portalCount"/> is negative, or the two vectors would
    /// not fit in an array. The portal-file reader caps the count well below
    /// that, so this is a guard against being handed a set built by hand.
    /// </exception>
    internal VisPortalState(int portalCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(portalCount);

        Count = portalCount;
        Words = BitVector.WordsFor(portalCount);

        long total = (long)portalCount * Words;
        if (total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(portalCount),
                portalCount,
                $"{portalCount} portals of {Words} words each does not fit in one array");
        }

        _flood = new ulong[total];
        _vis = new ulong[total];
        _mightSee = new int[portalCount];
        _status = new int[portalCount];
    }

    /// <summary>How many memory portals the vectors are sized for.</summary>
    internal int Count { get; }

    /// <summary>
    /// How many <see cref="ulong"/>s one vector occupies -- stock's
    /// <c>portallongs</c>, rounded up to a whole vector block.
    /// </summary>
    internal int Words { get; }

    /// <summary><c>portalflood</c>: what a flood fill says this one might see.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>The vector.</returns>
    internal Span<ulong> Flood(int portal) => _flood.AsSpan(portal * Words, Words);

    /// <summary><c>portalvis</c>: what the full flow says this one does see.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>The vector.</returns>
    internal Span<ulong> Vis(int portal) => _vis.AsSpan(portal * Words, Words);

    /// <summary><c>nummightsee</c>, the sort key.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>The bit count of <see cref="Flood"/>.</returns>
    internal int MightSeeCount(int portal) => _mightSee[portal];

    /// <summary>Records <c>nummightsee</c>.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <param name="count">The bit count.</param>
    internal void SetMightSeeCount(int portal, int count) => _mightSee[portal] = count;

    /// <summary>Reads one portal's status.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>The status.</returns>
    /// <remarks>
    /// <para>
    /// <b><see cref="Volatile"/>, and it stays volatile even though this port
    /// does not race on it.</b> Stock's flow reads a NEIGHBOUR's status to
    /// decide which of its bit vectors to prune with while
    /// another thread is writing it, with no barrier of any kind. That is benign
    /// only because x86 is total-store-ordered and the two values it chooses
    /// between are both valid; on a weaker model it is a torn read of a vector
    /// being written.
    /// </para>
    /// <para>
    /// The untightened flow does not make that read at all (see
    /// <see cref="VisContext"/>). <c>-tighten</c> does, and there the
    /// volatility is load-bearing: <see cref="VisTightening"/> publishes
    /// "done" only after the run that settled a portal, and a flow that reads
    /// "done" then reads that portal's <c>portalvis</c>, so the write here must
    /// be a release and the read an acquire -- or a reader could see "done"
    /// and a vector still missing stores. A neighbour not yet done is read as
    /// it stands and the read recorded (<see cref="VisSpeculativeReads"/>).
    /// </para>
    /// </remarks>
    internal VisPortalStatus Status(int portal) => (VisPortalStatus)Volatile.Read(ref _status[portal]);

    /// <summary>Publishes one portal's status.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <param name="status">The new status.</param>
    internal void SetStatus(int portal, VisPortalStatus status) =>
        Volatile.Write(ref _status[portal], (int)status);

    /// <summary>
    /// Copies every <c>portalflood</c> into its <c>portalvis</c> and marks every
    /// portal done -- stock's <c>-fast</c> path.
    /// </summary>
    /// <remarks>
    /// Stock ALIASES the two pointers rather than copying, which is why its
    /// <c>-fast</c> run leaks nothing and also why nothing may write through
    /// either afterwards. A copy is clearer and costs one pass over memory that
    /// the flow it replaces would have read many times.
    /// </remarks>
    internal void UseFloodAsVis()
    {
        _flood.AsSpan().CopyTo(_vis);
        for (int i = 0; i < Count; i++)
        {
            SetStatus(i, VisPortalStatus.Done);
        }
    }

    /// <summary>
    /// The bit count of one <c>portalflood</c>, using
    /// <see cref="BitOperations.PopCount(ulong)"/>.
    /// </summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>How many portals it might see.</returns>
    /// <remarks>
    /// Stock's <c>CountBits</c> is a loop over every bit
    /// calling a macro; the padding past <see cref="Count"/> is held at zero by
    /// <see cref="BitVector"/>, so counting the whole vector gives the same
    /// answer as counting the first <c>g_numportals*2</c> bits.
    /// </remarks>
    internal int CountFlood(int portal) => BitVectorOps.CountBits(Flood(portal));
}
