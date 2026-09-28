//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// What a vis computation produced.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Vvis.ComputeAsync"/> has already written the three lumps it owns
/// into the <c>BspData</c> it was handed. This carries what a caller wants
/// BESIDES the lumps: the decompressed rows, so a gate can compare visibility
/// without re-implementing the run-length coder, and the counters stock prints
/// on its way past.
/// </para>
/// <para>
/// The rows are the UNCOMPRESSED PVS and PAS, one row of
/// <see cref="RowBytes"/> per cluster, in the bit order the lump stores
/// (<c>bits[i&gt;&gt;3] &amp; (1&lt;&lt;(i&amp;7))</c>). The PVS rows are the
/// ones AFTER the symmetry crosscheck, which is what the lump holds and what
/// the engine reads.
/// </para>
/// </remarks>
public sealed class VisResult
{
    private readonly byte[] _pvs;
    private readonly byte[] _pas;

    internal VisResult(
        int clusterCount,
        int portalCount,
        int rowBytes,
        byte[] pvs,
        byte[] pas,
        int visDataSize,
        int totalVisibleClusters,
        int optimizedClusters,
        int totalAudibleClusters,
        bool usedRadius,
        double visRadiusSquared,
        int deepestFlow,
        VisWorkCounters work,
        IReadOnlyList<Vec3>? trace)
    {
        Work = work;
        ClusterCount = clusterCount;
        PortalCount = portalCount;
        RowBytes = rowBytes;
        _pvs = pvs;
        _pas = pas;
        VisDataSize = visDataSize;
        TotalVisibleClusters = totalVisibleClusters;
        OptimizedClusters = optimizedClusters;
        TotalAudibleClusters = totalAudibleClusters;
        UsedRadius = usedRadius;
        VisRadiusSquared = visRadiusSquared;
        DeepestFlow = deepestFlow;
        Trace = trace;
    }

    /// <summary>How many vis clusters the map has.</summary>
    public int ClusterCount { get; }

    /// <summary>
    /// How many MEMORY portals were flowed: twice the <c>.prt</c>'s count.
    /// </summary>
    public int PortalCount { get; }

    /// <summary>
    /// One uncompressed row's length, <c>(clusters + 7) &gt;&gt; 3</c>.
    /// </summary>
    public int RowBytes { get; }

    /// <summary>The size of the visibility lump that was written.</summary>
    /// <remarks>Stock's <c>visdatasize</c>.</remarks>
    public int VisDataSize { get; }

    /// <summary>
    /// Stock's <c>totalvis</c>: the sum over clusters of how many clusters each
    /// could see, BEFORE the symmetry crosscheck.
    /// </summary>
    public int TotalVisibleClusters { get; }

    /// <summary>
    /// How many cluster-to-cluster claims the symmetry crosscheck removed --
    /// the number stock prints as "Optimized".
    /// </summary>
    public int OptimizedClusters { get; }

    /// <summary>
    /// The sum over clusters of how many clusters each can hear -- what stock
    /// divides by the cluster count to print "Average clusters audible".
    /// </summary>
    public int TotalAudibleClusters { get; }

    /// <summary>Whether radial vis was in force.</summary>
    public bool UsedRadius { get; }

    /// <summary>
    /// <c>g_VisRadius</c>, already squared. Meaningless when
    /// <see cref="UsedRadius"/> is false.
    /// </summary>
    public double VisRadiusSquared { get; }

    /// <summary>
    /// The deepest recursion any worker's portal flow reached.
    /// </summary>
    /// <remarks>
    /// Not a stock statistic. It is here because it is the number that says how
    /// close a map came to the limit stock's 8.7 KB stack frame imposes on
    /// stock, and therefore whether
    /// <see cref="VisPortalFlow"/>'s own limit is anywhere near being an issue.
    /// <para>
    /// Like the flow counters in <see cref="Work"/>, it is a property of the
    /// run, not of the map, on the tightened walk at more than one worker: a
    /// run that read an unfinished neighbour's vector while it was still
    /// growing prunes differently from the exact run, and may reach deeper
    /// before it is judged and walked again. The 3x3 sample's corner room
    /// reports 3 or 4 from one compile to the next with identical rows. Zero
    /// for a room loaded from a file, which ran no flow and whose container
    /// must not depend on the schedule.
    /// </para>
    /// </remarks>
    public int DeepestFlow { get; }

    /// <summary>
    /// How much work the flow did, for a per-operation comparison.
    /// </summary>
    /// <remarks>
    /// Zero for a <c>-fast</c> run, which has no flow, and for a
    /// <c>-trace</c> run, which answers a different question; zero too for a
    /// room loaded from a file and for a vvis stage-cache replay, which ran
    /// none. See <see cref="VisWorkCounters"/> for why a wall time alone
    /// cannot be compared with stock's, and for when these depend on the
    /// schedule rather than the map.
    /// </remarks>
    public VisWorkCounters Work { get; }

    /// <summary>
    /// The line strip a <c>-trace</c> run produced, or null.
    /// </summary>
    /// <remarks>
    /// Stock writes these to <c>&lt;map&gt;.lin</c> as one <c>%f %f %f</c> per
    /// line. Returning the points rather than writing
    /// a file is the library rule: rendering it is the caller's, and a host that
    /// wants to draw it in its own viewer never touches a disk.
    /// </remarks>
    public IReadOnlyList<Vec3>? Trace { get; }

    /// <summary>One cluster's potentially visible set, uncompressed.</summary>
    /// <param name="cluster">A cluster index.</param>
    /// <returns><see cref="RowBytes"/> bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cluster"/> is not a cluster of this map.
    /// </exception>
    public ReadOnlySpan<byte> Pvs(int cluster)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cluster);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cluster, ClusterCount);
        return _pvs.AsSpan(cluster * RowBytes, RowBytes);
    }

    /// <summary>
    /// Every PVS row back to back, exactly as held: what a reader of an
    /// untrusted row set checks the length and padding bits of before any
    /// <see cref="Pvs(int)"/> slices it (the room store and the linker do).
    /// </summary>
    internal ReadOnlySpan<byte> PvsBytes => _pvs;

    /// <summary>Every PAS row back to back, exactly as held.</summary>
    internal ReadOnlySpan<byte> PasBytes => _pas;

    /// <summary>One cluster's potentially audible set, uncompressed.</summary>
    /// <param name="cluster">A cluster index.</param>
    /// <returns><see cref="RowBytes"/> bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cluster"/> is not a cluster of this map.
    /// </exception>
    public ReadOnlySpan<byte> Pas(int cluster)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cluster);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cluster, ClusterCount);
        return _pas.AsSpan(cluster * RowBytes, RowBytes);
    }

    /// <summary>Whether one cluster can see another.</summary>
    /// <param name="from">The cluster being looked from.</param>
    /// <param name="to">The cluster being looked at.</param>
    /// <returns>True when <paramref name="to"/> is in
    /// <paramref name="from"/>'s PVS.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either index is not a cluster of this map.
    /// </exception>
    public bool CanSee(int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(to);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(to, ClusterCount);
        return (Pvs(from)[to >> 3] & (1 << (to & 7))) != 0;
    }

    /// <summary>Whether one cluster can hear another.</summary>
    /// <param name="from">The cluster being listened from.</param>
    /// <param name="to">The cluster being listened to.</param>
    /// <returns>True when <paramref name="to"/> is in
    /// <paramref name="from"/>'s PAS.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either index is not a cluster of this map.
    /// </exception>
    public bool CanHear(int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(to);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(to, ClusterCount);
        return (Pas(from)[to >> 3] & (1 << (to & 7))) != 0;
    }
}
