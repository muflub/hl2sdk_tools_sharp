//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// How much work the portal flow actually did, as opposed to how long it took.
/// </summary>
/// <param name="Chains">
/// How many times <c>RecursiveLeafFlow</c> was entered, over every portal --
/// Stock's <c>c_chains</c> summed over the
/// whole run instead of printed per portal.
/// </param>
/// <param name="Candidates">
/// How many candidate portals passed the <c>mightsee</c> bit test at
/// -- the portals a frame actually considered.
/// </param>
/// <param name="SeparatorClips">
/// How many times <c>ClipToSeperators</c> was called from the flow
/// Counting the first of each pair.
/// </param>
/// <param name="BaseRays">
/// How many portal-pair <c>portalfront</c> tests <see cref="VisBaseFlow"/>
/// performed — the <c>BasePortalVis</c> inner loop in the reference
/// dispatch. Counted per (source portal, candidate portal)
/// pair and summed over the whole run. This is the pass the P12 anomaly-3
/// report misremembered <c>-fast</c> as skipping: it skips NOTHING, in stock
/// or here (the reference dispatch runs the base pass unconditionally; the
/// one fastvis branch lives in <c>CalcPortalVis</c>), so this counter is the gate
/// that keeps a future
/// well-meaning "skip the base pass under -fast" from ever merging.
/// </param>
/// <remarks>
/// <para>
/// <b>Why a compiler reports its own operation counts.</b> The performance
/// gate requires a per-operation comparison against stock and forbids inferring one
/// from a scaled wall time. A wall time divided by the portal count cannot
/// serve: this port prunes with <c>portalflood</c> where stock may prune with a
/// neighbour's finished <c>portalvis</c>, so the two do a DIFFERENT AMOUNT of
/// work on the same map and a per-portal rate would quietly compare two things.
/// Dividing by the counters below compares the same operation.
/// </para>
/// <para>
/// <b>When they are a function of the map, and when they are not.</b>
/// <see cref="BaseRays"/> always is: the base pass reads nothing another
/// portal writes. The three flow counters are a function of the map and the
/// options on the UNTIGHTENED walk (<c>-loose</c>) at any thread count, and
/// on the tightened walk -- the default -- at ONE thread, where every
/// neighbour a flow reads has finished before it starts and the walk is
/// stock's own, read for read. On the tightened walk at more than one
/// worker they are a property of the schedule: a flow may read a neighbour
/// that is still being flowed, and <see cref="VisTightening"/> then judges
/// the run once that neighbour finishes and walks it again if the read
/// missed anything. How many chains the first walk took, whether a second
/// one happens, and how much of the tree it may skip all depend on how far
/// the neighbour had got. The ANSWER does not: that is what the tightening
/// proves and what the byte-identity facts pin.
/// </para>
/// <para>
/// So on the schedule-invariant arms a change in one of these across an
/// optimisation is a change in the answer, and a defect -- a second,
/// independent check on the byte-identity gate: bytes can agree while the
/// work behind them differs only if the extra work was wasted. Elsewhere they
/// are a diagnostic of one run: compare them only between runs of the same
/// arm at one thread, and never write them into an artefact that must be a
/// function of its input. The <c>.room</c> container used to, and a room
/// compiled twice came out with different bytes; it no longer does (see
/// <see cref="Rooms.RoomObjectStore"/>).
/// </para>
/// </remarks>
public readonly record struct VisWorkCounters(long Chains, long Candidates, long SeparatorClips, long BaseRays)
{
    /// <summary>The counters of a run that did nothing.</summary>
    public static VisWorkCounters Zero => default;

    /// <summary>Adds two workers' counters together.</summary>
    /// <param name="left">One worker's counts.</param>
    /// <param name="right">Another's.</param>
    /// <returns>The sum, field by field.</returns>
    public static VisWorkCounters operator +(VisWorkCounters left, VisWorkCounters right) =>
        new(
            left.Chains + right.Chains,
            left.Candidates + right.Candidates,
            left.SeparatorClips + right.SeparatorClips,
            left.BaseRays + right.BaseRays);

    /// <summary>Adds two workers' counters together.</summary>
    /// <param name="left">One worker's counts.</param>
    /// <param name="right">Another's.</param>
    /// <returns>The sum, field by field.</returns>
    /// <remarks>
    /// The named form of <c>operator +</c>, which CA2225 asks for so a language
    /// without operator overloading can still add them.
    /// </remarks>
    public static VisWorkCounters Add(VisWorkCounters left, VisWorkCounters right) => left + right;
}
