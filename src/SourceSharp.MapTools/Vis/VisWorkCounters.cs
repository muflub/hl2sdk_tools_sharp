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
/// performed — stock's <c>BasePortalVis</c> inner loop
/// Counted per (source portal, candidate portal)
/// pair and summed over the whole run. This is the pass the P12 anomaly-3
/// report misremembered <c>-fast</c> as skipping: it skips NOTHING, in stock
/// or here (<c>all.c</c> dispatches the base pass unconditionally; the
/// one fastvis branch is <c>all.c</c>, and it lives in
/// <c>CalcPortalVis</c>), so this counter is the gate that keeps a future
/// well-meaning "skip the base pass under -fast" from ever merging.
/// </param>
/// <remarks>
/// <para>
/// <b>Why a compiler reports its own operation counts.</b> plan_maptools.md 5
/// requires a per-operation comparison against stock and forbids inferring one
/// from a scaled wall time. A wall time divided by the portal count cannot
/// serve: this port prunes with <c>portalflood</c> where stock may prune with a
/// neighbour's finished <c>portalvis</c>, so the two do a DIFFERENT AMOUNT of
/// work on the same map and a per-portal rate would quietly compare two things.
/// Dividing by the counters below compares the same operation.
/// </para>
/// <para>
/// Every one of these is a pure function of the map and the options, and in
/// particular is the same at one thread and at thirty-two -- the same property
/// the output has, from the same choice. So a change in one of them across a
/// 2b optimisation is a change in the ANSWER, and is a defect. That makes them
/// a second, independent check on the byte-identity gate: bytes can agree while
/// the work behind them differs only if the extra work was wasted, which is
/// worth knowing either way.
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
