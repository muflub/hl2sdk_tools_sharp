//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// One cluster's row, as the two maps disagree about it.
/// </summary>
/// <param name="Cluster">The cluster index.</param>
/// <param name="OnlyInA">Bits set in A's row and clear in B's: visibility B LOST.</param>
/// <param name="OnlyInB">Bits set in B's row and clear in A's: visibility B ADDED.</param>
public readonly record struct ClusterBitDifference(int Cluster, int OnlyInA, int OnlyInB)
{
    /// <summary>How many bits differ either way.</summary>
    public int Total => OnlyInA + OnlyInB;
}

/// <summary>
/// One visibility column -- the PVS or the PAS -- compared row by row.
/// </summary>
/// <remarks>
/// <para>
/// The direction is reported and not judged. A row of B that is a strict
/// SUPERSET of A's is a legitimate vvis result: a cluster that believes it sees
/// more than it does renders too much and is never wrong, which is exactly what
/// <c>-fast</c> produces: the acceptance shape for vvis is containment, never
/// equality. A row that has LOST
/// bits is the dangerous direction, because geometry disappears at runtime.
/// </para>
/// </remarks>
public sealed class PvsDifference
{
    internal PvsDifference(
        string column,
        int clusterCount,
        long onlyInABits,
        long onlyInBBits,
        int differingClusters,
        ImmutableArray<ClusterBitDifference> clusters)
    {
        Column = column;
        ClusterCount = clusterCount;
        OnlyInABits = onlyInABits;
        OnlyInBBits = onlyInBBits;
        DifferingClusters = differingClusters;
        Clusters = clusters;
    }

    /// <summary>Which column this is: <c>pvs</c> or <c>pas</c>.</summary>
    public string Column { get; }

    /// <summary>How many clusters were compared.</summary>
    public int ClusterCount { get; }

    /// <summary>Bits set in A and clear in B, over the whole map.</summary>
    public long OnlyInABits { get; }

    /// <summary>Bits set in B and clear in A, over the whole map.</summary>
    public long OnlyInBBits { get; }

    /// <summary>The total number of differing bits, either direction.</summary>
    public long DifferingBits => OnlyInABits + OnlyInBBits;

    /// <summary>How many clusters have at least one differing bit.</summary>
    public int DifferingClusters { get; }

    /// <summary>
    /// The worst clusters, most differing bits first, bounded by
    /// <see cref="DiffOptions.MaxReportedItems"/>.
    /// </summary>
    public ImmutableArray<ClusterBitDifference> Clusters { get; }

    /// <summary>
    /// Whether every bit set in A is also set in B: B sees at least everything
    /// A sees.
    /// </summary>
    public bool BContainsA => OnlyInABits == 0;

    /// <summary>Whether every bit set in B is also set in A.</summary>
    public bool AContainsB => OnlyInBBits == 0;

    /// <summary>Whether the two columns agree bit for bit.</summary>
    public bool Identical => DifferingBits == 0;
}

/// <summary>
/// LUMP_VISIBILITY, compared as both of its columns.
/// </summary>
public sealed class VisibilityDifference
{
    internal VisibilityDifference(int clusterCountA, int clusterCountB, PvsDifference? pvs, PvsDifference? pas)
    {
        ClusterCountA = clusterCountA;
        ClusterCountB = clusterCountB;
        Pvs = pvs;
        Pas = pas;
    }

    /// <summary>The cluster count A's lump declares.</summary>
    public int ClusterCountA { get; }

    /// <summary>The cluster count B's lump declares.</summary>
    public int ClusterCountB { get; }

    /// <summary>
    /// The potentially visible set, or null when the cluster counts differ and
    /// no row-by-row comparison was attempted.
    /// </summary>
    public PvsDifference? Pvs { get; }

    /// <summary>
    /// The potentially audible set, or null when the cluster counts differ and
    /// no row-by-row comparison was attempted.
    /// </summary>
    public PvsDifference? Pas { get; }

    /// <summary>Whether both columns agree bit for bit.</summary>
    public bool Identical =>
        ClusterCountA == ClusterCountB
        && Pvs is { Identical: true }
        && Pas is { Identical: true };
}
