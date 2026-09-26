//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// The eight numbers <c>LoadMapFile</c> prints when vbsp is run with
/// <c>-v</c>:.
/// </summary>
/// <param name="Brushes">"%5i brushes", <c>nummapbrushes</c>.</param>
/// <param name="ClipBrushes">"%5i clipbrushes", <c>c_clipbrushes</c>.</param>
/// <param name="TotalSides">"%5i total sides", <c>nummapbrushsides</c>.</param>
/// <param name="BoxBevels">"%5i boxbevels", <c>c_boxbevels</c>.</param>
/// <param name="EdgeBevels">"%5i edgebevels", <c>c_edgebevels</c>.</param>
/// <param name="Entities">"%5i entities", <c>num_entities</c>.</param>
/// <param name="Planes">"%5i planes", <c>nummapplanes</c>.</param>
/// <param name="AreaPortals">"%5i areaportals", <c>c_areaportals</c>.</param>
/// <param name="Mins">The map's lower bound.</param>
/// <param name="Maxs">The map's upper bound.</param>
/// <remarks>
/// <para>
/// <b>This is a gate, not a log line.</b> Stock prints these for every compile
/// under <c>-v</c>, which makes them an exact, independently produced
/// reference for the whole load stage — including <c>Planes</c>, which is the
/// only externally visible count of the plane table this lane exists to get
/// right. A port that agreed on brushes and sides but disagreed on planes
/// would be building a different file.
/// </para>
/// <para>
/// The counts are of the LOADING map, except <c>AreaPortals</c>, which stock
/// keeps as a class static shared across every map in the process
/// And which is therefore cumulative over the main map and
/// all its instances.
/// </para>
/// </remarks>
public readonly record struct MapLoadStatistics(
    int Brushes,
    int ClipBrushes,
    int TotalSides,
    int BoxBevels,
    int EdgeBevels,
    int Entities,
    int Planes,
    int AreaPortals,
    Vec3 Mins,
    Vec3 Maxs)
{
    /// <summary>Takes the statistics of a loaded map.</summary>
    /// <param name="context">The compile.</param>
    /// <param name="map">The map.</param>
    /// <returns>The statistics.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static MapLoadStatistics Of(VbspContext context, MapFile map)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(map);

        return new MapLoadStatistics(
            map.BrushCount,
            map.ClipBrushes,
            map.BrushSideCount,
            map.BoxBevels,
            map.EdgeBevels,
            map.Entities.Count,
            map.Planes.Count,
            context.AreaPortalCount,
            map.Mins,
            map.Maxs);
    }
}
