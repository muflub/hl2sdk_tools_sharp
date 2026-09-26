//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// A named place in the map, used to say which parts must and must not see each
/// other.
///
/// <para>
/// A POSITION rather than a cluster number, and that is the point. Cluster
/// numbering is an output of vvis — it does not exist when the entry is
/// declared, it changes when the geometry is nudged, and an expectation written
/// as "cluster 7 sees cluster 12" is a fact about one particular compile rather
/// than about the map. A probe is a point a human can reason about; the
/// assertion a later phase makes is "the cluster containing A sees the cluster
/// containing B", which survives every renumbering.
/// </para>
/// </summary>
/// <param name="Name">What the place is called, unique within the entry.</param>
/// <param name="At">Where it is, in world units, and inside the sealed volume.</param>
public readonly record struct VisProbe(string Name, Point At);

/// <summary>
/// Two probes, and whether one must be able to see the other.
///
/// <para>
/// Both directions are declared by one pair because PVS is symmetric in vvis's
/// output: <c>PortalFlow</c> writes a cluster into another's visibility set and
/// the reverse holds. An asymmetric expectation would be a bug report, not a
/// declaration.
/// </para>
/// </summary>
/// <param name="From">One probe's name.</param>
/// <param name="To">The other probe's name.</param>
public readonly record struct ProbePair(string From, string To);
