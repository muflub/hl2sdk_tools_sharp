//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Detail;

/// <summary>
/// <see cref="IDetailDisplacementSurfaces"/> over a loaded map's displacements,
/// as <c>EmitDetailObjects</c> builds them.
/// </summary>
/// <remarks>
/// <para>
/// Stock makes a fresh <c>CCoreDispInfo</c> per displacement face with
/// <c>DispMapToCoreDispInfo(pMapDisp, &amp;coreDispInfo, NULL, NULL)</c>: from
/// the <c>mapdispinfo</c> alone, with no face, so the texture coordinates are
/// the unit square and no neighbour is known. That is lane 3f's
/// <see cref="DisplacementLumpBuilder.DispMapToCoreDispInfo"/> with
/// <c>withFace: false</c>, exactly as its <c>ComputeDispInfoBounds</c> calls it.
/// </para>
/// <para>
/// The <c>dispinfo</c> index is the order <c>EmitInitialDispInfos</c> numbered
/// them: the brush sides that carry one, in side order.
/// </para>
/// </remarks>
public sealed class MapDisplacementSurfaces : IDetailDisplacementSurfaces
{
    private readonly VbspContext _compile;
    private readonly List<MapBrushSide> _sides = [];
    private readonly Dictionary<int, CoreDispInfo> _cores = [];

    /// <summary>The surfaces of one map.</summary>
    /// <param name="compile">The compile: windings, texinfos and compliance.</param>
    /// <param name="map">The loaded map.</param>
    public MapDisplacementSurfaces(VbspContext compile, MapFile map)
    {
        ArgumentNullException.ThrowIfNull(compile);
        ArgumentNullException.ThrowIfNull(map);

        _compile = compile;
        foreach (MapBrushSide side in map.BrushSides)
        {
            if (side.Displacement is MapDisplacement)
            {
                _sides.Add(side);
            }
        }
    }

    /// <summary>How many displacements there are: <c>nummapdispinfo</c>.</summary>
    public int Count => _sides.Count;

    /// <inheritdoc/>
    public (Vec3 Point, Vec3 Normal, float Alpha) PositionOnSurface(int dispInfo, float u, float v)
    {
        if (!_cores.TryGetValue(dispInfo, out CoreDispInfo? core))
        {
            MapBrushSide side = _sides[dispInfo];
            MapDisplacement disp = (MapDisplacement)side.Displacement!;
            core = new CoreDispInfo(disp.Power);
            DisplacementFace face = DispVbspHooks.Face(
                0, [.. _compile.Windings.Points(side.Winding)], disp.Contents, _compile.TexInfos[side.TexInfo]);
            DisplacementLumpBuilder.DispMapToCoreDispInfo(
                disp, face, core, _compile.Options.Compliance.Emulates(StockQuirk.DispVertNormalise), withFace: false);
            _cores.Add(dispInfo, core);
        }

        // Stock's pt/normal/alpha are uninitialised stack when the query
        // misses; a miss cannot happen for u, v in [0, 1].
        Vec3 point = Vec3.Zero, normal = Vec3.Zero;
        float alpha = 0;
        core.GetPositionOnSurface(u, v, ref point, ref normal, ref alpha);
        return (point, normal, alpha);
    }
}
