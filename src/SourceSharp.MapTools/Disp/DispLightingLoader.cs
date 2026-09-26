//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// Rebuilds every displacement of a compiled BSP the way vrad does, sewn
/// normals included: <c>CVRadDispMgr::UnserializeDisps</c> up to the
/// Collision-tree build.
/// </summary>
/// <remarks>
/// <para>
/// The entry point for vrad's displacement lighting (lane 4e): it gets, per
/// LUMP_DISPINFO entry, a <see cref="CoreDispInfo"/> whose
/// <see cref="CoreDispInfo.Verts"/>, <see cref="CoreDispInfo.Normals"/>,
/// luxel coordinates and <see cref="CoreDispInfo.TriIndices"/> are what
/// stock's <c>CVRADDispColl::Create</c> and the patch/sample code read.
/// </para>
/// <para>
/// A pure CPU pass over lumps already in memory, synchronous like
/// <see cref="DisplacementLumpBuilder.Build"/>; the async stage that calls
/// it owns cancellation. The per-displacement init is independent, so the
/// stock face-order loop and a displacement-order loop give the
/// same cores; the smoothing that follows is order-dependent and serial.
/// </para>
/// </remarks>
public static class DispLightingLoader
{
    /// <summary>
    /// Builds, creates and smooths every displacement.
    /// </summary>
    /// <param name="bsp">The compiled map.</param>
    /// <param name="compliance">
    /// The compile's compliance: <see cref="StockQuirk.DispVertNormalise"/>
    /// picks the normalise, <see cref="StockQuirk.DispVertexNormalMeanUnnormalised"/>
    /// whether the vertex normals are renormalised.
    /// </param>
    /// <returns>One core per LUMP_DISPINFO entry, in that order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// A displacement no valid face points at (<c>ValidDispFace</c>,
    /// Dispinfo set and four edges) is left
    /// un-initialised and not created — stock's <c>Create</c> refuses it too
    /// (point count not four).
    /// </remarks>
    public static CoreDispInfo[] Load(BspData bsp, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(compliance);

        bool stockNormalise = compliance.Emulates(StockQuirk.DispVertNormalise);
        bool stockNormalMean = compliance.Emulates(StockQuirk.DispVertexNormalMeanUnnormalised);

        ReadOnlySpan<DispInfo> infos = BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]);
        ReadOnlySpan<DispVert> verts = BspStructView.As<DispVert>(bsp[BspLump.DispVerts]);
        ReadOnlySpan<DispTri> tris = BspStructView.As<DispTri>(bsp[BspLump.DispTris]);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<Vec3> vertexes = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);

        CoreDispInfo[] cores = new CoreDispInfo[infos.Length];
        bool[] initialised = new bool[infos.Length];

        for (int i = 0; i < infos.Length; i++)
        {
            cores[i] = new CoreDispInfo(infos[i].Power)
            {
                ListIndex = i,
                StockNormalise = stockNormalise,
                StockVertexNormalMean = stockNormalMean,
            };
        }

        foreach (CoreDispInfo core in cores)
        {
            core.SetListBase(cores);
        }

        for (int f = 0; f < faces.Length; f++)
        {
            DFace face = faces[f];
            if (face.DispInfo == -1 || face.NumEdges != 4)
            {
                continue;
            }

            Vec3[] winding = new Vec3[4];
            for (int k = 0; k < 4; k++)
            {
                int se = surfEdges[face.FirstEdge + k];
                winding[k] = se < 0 ? vertexes[edges[-se].V[1]] : vertexes[edges[se].V[0]];
            }

            DispInfo info = infos[face.DispInfo];
            TexInfo tex = texInfos[face.TexInfo];

            BuilderInit(
                cores[face.DispInfo],
                info,
                f,
                winding,
                new Vec3(tex.LightmapVecsLuxelsPerWorldUnits[0], tex.LightmapVecsLuxelsPerWorldUnits[1], tex.LightmapVecsLuxelsPerWorldUnits[2]),
                new Vec3(tex.LightmapVecsLuxelsPerWorldUnits[4], tex.LightmapVecsLuxelsPerWorldUnits[5], tex.LightmapVecsLuxelsPerWorldUnits[6]),
                verts.Slice(info.DispVertStart),
                tris.Slice(info.DispTriStart));

            initialised[face.DispInfo] = true;
        }

        for (int i = 0; i < cores.Length; i++)
        {
            if (initialised[i])
            {
                cores[i].Create();
            }
        }

        DispNormalSmoother.SmoothNeighboringDispSurfNormals(cores, stockNormalise);

        return cores;
    }

    /// <summary>
    /// One displacement from its lump entry and base face:
    /// <c>CVRadDispMgr::DispBuilderInit</c>.
    /// </summary>
    /// <param name="core">The core to fill (not yet created).</param>
    /// <param name="info">Its LUMP_DISPINFO entry.</param>
    /// <param name="faceIndex">Its base face, stored as the surface handle.</param>
    /// <param name="winding">The base face's four points, in surfedge order.</param>
    /// <param name="lightmapU">The face texinfo's lightmap row 0, spatial part.</param>
    /// <param name="lightmapV">Its row 1.</param>
    /// <param name="verts">LUMP_DISP_VERTS from this displacement's start.</param>
    /// <param name="tris">LUMP_DISP_TRIS from this displacement's start.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <remarks>
    /// Differs from vbsp's <c>DispMapToCoreDispInfo</c> in three ways that
    /// matter: no texture coordinates are computed (they stay zero); the
    /// neighbour tables are COPIED from the lump rather than found; and the
    /// field comes from the lump, i.e. after <c>SnapRemainingVertsToSurface</c>.
    /// </remarks>
    public static void BuilderInit(
        CoreDispInfo core,
        DispInfo info,
        int faceIndex,
        Vec3[] winding,
        Vec3 lightmapU,
        Vec3 lightmapV,
        ReadOnlySpan<DispVert> verts,
        ReadOnlySpan<DispTri> tris)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(winding);

        CoreDispSurface surf = core.Surface;
        surf.Handle = faceIndex;
        surf.Contents = info.Contents;

        for (int i = 0; i < 4; i++)
        {
            surf.SetPoint(i, winding[i]);
        }

        Vec3 faceNormal = surf.GetNormal();
        for (int i = 0; i < 4; i++)
        {
            surf.SetPointNormal(i, faceNormal);
        }

        surf.PointStart = info.StartPosition;
        surf.FindSurfPointStartIndex();
        surf.AdjustSurfPointData();

        int luxelsPerWorldUnit = (int)(1.0f / lightmapU.Length());
        surf.CalcLuxelCoords(luxelsPerWorldUnit, adjust: false, lightmapU, lightmapV);

        DispNeighbor[] edges = new DispNeighbor[4];
        DispCornerNeighbors[] corners = new DispCornerNeighbors[4];
        for (int i = 0; i < 4; i++)
        {
            edges[i] = info.EdgeNeighbors[i];
            corners[i] = info.CornerNeighbors[i];
        }

        surf.SetNeighborData(edges, corners);

        core.InitDispInfo(info.MinTess, verts, tris);
    }
}
