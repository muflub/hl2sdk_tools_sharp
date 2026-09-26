//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// What IVP's point-soup builder needs from qhull: run <c>qh_new_qhull(3, n, coords, ...)</c> with an
/// option string, then walk <c>FORALLfacets</c> reading each facet's normal and its
/// <c>qh_facet3vertex</c> vertex order as point ids.
/// </summary>
/// <remarks>
/// One instance per thread of work: a run replaces the previous run's facets (the moral equivalent
/// of <c>qh_freeqhull</c> before the next <c>qh_new_qhull</c>), and nothing is shared.
/// </remarks>
internal interface IQhullRunner
{
    /// <summary>Builds a hull.</summary>
    /// <param name="coords">x, y, z per point, doubles, as IVP passes them.</param>
    /// <param name="pointCount">Number of points.</param>
    /// <param name="options">The qhull command, starting with <c>"qhull "</c>.</param>
    /// <returns>The qhull exit code; 0 on success.</returns>
    int Run(ReadOnlySpan<double> coords, int pointCount, string options);

    /// <summary>Facets of the last successful run, in <c>facet_list</c> order.</summary>
    int FacetCount { get; }

    /// <summary>A facet's outward normal.</summary>
    /// <param name="facet">Facet index.</param>
    /// <returns>The normal.</returns>
    (double X, double Y, double Z) Normal(int facet);

    /// <summary>A facet's vertices as input point ids, in <c>qh_facet3vertex</c> order.</summary>
    /// <param name="facet">Facet index.</param>
    /// <returns>The point ids.</returns>
    ReadOnlySpan<int> Vertices(int facet);
}
