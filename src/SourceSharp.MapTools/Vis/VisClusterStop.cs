//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// What a <c>-fastflow</c> portal publishes as its <c>portalvis</c> for the
/// portals ranked above it to prune with.
/// </summary>
/// <remarks>
/// Internal, and only <see cref="Truncated"/> is reachable from
/// <see cref="Options.VvisOptions.FastFlowSteps"/>. The other two are arms the
/// fast flow was chosen against, kept so the facts can pin the containments
/// between them (see <see cref="VisClusterStop"/>) and so the measurement can
/// be repeated.
/// </remarks>
internal enum VisFastFlowFilter
{
    /// <summary>
    /// Only the portals the cut-short walk marked: a subset of the exact
    /// walk's <c>portalvis</c>, so the portals ranked above prune chains the
    /// exact flow keeps, and the PVS is a subset of the exact one. The
    /// shipped arm.
    /// </summary>
    Truncated,

    /// <summary>
    /// The marked portals plus every portal a cut could have reached (the
    /// might-see of each frame it cut): a superset of the exact
    /// <c>portalvis</c>, so the PVS is a superset of the exact one. Loses
    /// nothing, and on 2fort saves only 6 % of the chains.
    /// </summary>
    Conservative,

    /// <summary>
    /// Every portal of the flood that leads into a cluster the portal sees:
    /// a superset of <see cref="Conservative"/>, and saves even less.
    /// </summary>
    ClusterGranular,
}

/// <summary>
/// The cluster-granular early stop of the <c>-fastflow</c> walk, and the
/// per-compile table it needs: for every cluster, the memory portals that
/// lead INTO it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stop.</b> The exact walk abandons a candidate only when the
/// candidate's might-see has no bit its <c>portalvis</c> lacks, because only
/// then can recursing into it mark nothing new. That is a PORTAL-granular
/// test, and the PVS is CLUSTER-granular: once the base portal sees a
/// cluster, a further portal into that cluster changes no PVS row. The fast
/// walk keeps a COVER beside <c>portalvis</c> -- every portal it has marked,
/// plus every portal leading into a cluster it already sees -- and abandons a
/// candidate when its might-see has no bit the cover lacks. Everything the
/// abandoned subtree could reach is in that might-see, so it could only reach
/// clusters already seen: given the same neighbours' vectors, the base
/// portal's own cluster set is exactly the exact walk's.
/// </para>
/// <para>
/// <b>Where the answer moves.</b> Not in the portal's own row but in what it
/// PUBLISHES. The default walk prunes each candidate with the
/// <c>portalvis</c> of the neighbours ranked below it, and a cut-short walk's
/// marked set lacks every portal behind its cuts. Portals ranked above then
/// prune chains the exact flow keeps, and so lose clusters: the fast PVS is a
/// SUBSET of the exact one. That pruning is also where almost all the speed
/// comes from. Publishing a vector that holds everything the cuts could have
/// reached (<see cref="VisFastFlowFilter.Conservative"/>) keeps the PVS a
/// superset of the exact one, but then the neighbours prune LESS than the
/// exact walk would, which cancels the stop's saving: on 2fort that arm
/// walked 137.3M chains against the exact 146.3M.
/// </para>
/// <para>
/// <b>The threshold.</b> A walk takes <see cref="MinChains"/> steps exactly
/// (<c>-fastflow=N</c>; 1,000 for <c>-fastflow</c> alone),
/// portal-granular, before the stop may cut anything. Most portals' walks are
/// shorter than that, so their published vectors are exact, and those are
/// the cheap, low-ranked portals every expensive one prunes with. Measured on
/// 2fort (main's correct-mode vbsp tree, 2492 clusters, 6367 portals):
/// </para>
/// <list type="table">
/// <listheader><term>threshold</term><description>chains; PVS pairs lost before / after the symmetric pass</description></listheader>
/// <item><term>exact walk</term><description>146.3M; 0 / 0</description></item>
/// <item><term>0 (cut from the first step)</term><description>61.5M; 33,432 / 60,786</description></item>
/// <item><term>500</term><description>65.4M; 13,323 / 24,620</description></item>
/// <item><term>1,000 (the default)</term><description>71.1M; 9,599 / 17,622</description></item>
/// <item><term>2,000</term><description>80.1M; 6,762 / 12,416</description></item>
/// <item><term>5,000</term><description>91.6M; 3,980 / 7,286</description></item>
/// <item><term>20,000</term><description>114.3M; 1,186 / 2,156</description></item>
/// <item><term>50,000</term><description>128.4M; 540 / 1,036</description></item>
/// </list>
/// <para>
/// A depth threshold instead (cut only frames at least N deep) was measured
/// worse at every speed, and so was checking each cut's candidate against the
/// geometry and publishing it when it passed: the loss is in the shallow cuts
/// of long walks, and a step count is what finds long walks.
/// </para>
/// <para>
/// <b>Why it is deterministic.</b> The stop depends on the order the walk
/// meets clusters in, and the threshold on how many steps it has taken, so a
/// cut-short vector is a function of the walk and not only of the map. The
/// walk is kept a function of the map: each portal is flowed whole by one
/// worker, and only once every neighbour it may prune with has finished
/// (<see cref="VisTightening"/> defers it until then instead of
/// speculating), so it reads exactly the vectors a one-thread run would. The
/// untightened walk (<c>-loose</c>) reads no neighbour's vector at all, so
/// there the stop is exact.
/// </para>
/// <para>
/// Immutable once built, and built once per compile: nothing here outlives
/// the compile that made it.
/// </para>
/// </remarks>
internal sealed class VisClusterStop
{
    private readonly int[] _intoStart;
    private readonly int[] _into;

    /// <summary>Builds the per-cluster table for one compile's portals.</summary>
    /// <param name="portals">The memory portals.</param>
    /// <param name="filter">What each portal publishes.</param>
    /// <param name="minChains">See <see cref="MinChains"/>.</param>
    internal VisClusterStop(
        PortalSet portals,
        VisFastFlowFilter filter = VisFastFlowFilter.Truncated,
        int minChains = Options.VvisOptions.DefaultFastFlowSteps)
    {
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentOutOfRangeException.ThrowIfNegative(minChains);

        Filter = filter;
        MinChains = minChains;
        ClusterCount = portals.ClusterCount;

        // A counting sort of the portals by the cluster they lead into, so
        // each cluster's list is one contiguous, ascending run.
        int[] start = new int[portals.ClusterCount + 1];
        for (int portal = 0; portal < portals.Count; portal++)
        {
            start[portals.Leaf(portal) + 1]++;
        }

        for (int cluster = 0; cluster < portals.ClusterCount; cluster++)
        {
            start[cluster + 1] += start[cluster];
        }

        int[] into = new int[portals.Count];
        int[] fill = (int[])start.Clone();
        for (int portal = 0; portal < portals.Count; portal++)
        {
            into[fill[portals.Leaf(portal)]++] = portal;
        }

        _intoStart = start;
        _into = into;
    }

    /// <summary>What each portal publishes.</summary>
    internal VisFastFlowFilter Filter { get; }

    /// <summary>
    /// How many steps a walk takes with the exact, portal-granular test
    /// before the cluster stop may cut: <see cref="Options.VvisOptions.FastFlowSteps"/>.
    /// A constant of the compile, and that is load-bearing in the same way as
    /// <see cref="VisTightening.Lag"/>: it decides what every portal
    /// publishes, so it may not depend on the machine.
    /// </summary>
    internal int MinChains { get; }

    /// <summary>How many clusters the table covers.</summary>
    internal int ClusterCount { get; }

    /// <summary>The memory portals whose far side is a cluster.</summary>
    /// <param name="cluster">A cluster index.</param>
    /// <returns>Memory-portal indices, ascending.</returns>
    internal ReadOnlySpan<int> PortalsInto(int cluster) =>
        _into.AsSpan(_intoStart[cluster], _intoStart[cluster + 1] - _intoStart[cluster]);
}
