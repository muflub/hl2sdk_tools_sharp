//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Overlays;

/// <summary>
/// One overlay as the map described it: <c>mapoverlay_t</c>
/// Shared by <c>info_overlay</c> and the water
/// overlays of an <c>overlaytransition</c> block.
/// </summary>
public sealed class MapOverlay
{
    /// <summary><c>MAX_MAP_OVERLAYS</c>.</summary>
    public const int MaxMapOverlays = 512;

    /// <summary><c>MAX_MAP_WATEROVERLAYS</c>.</summary>
    public const int MaxMapWaterOverlays = 16384;

    /// <summary><c>OVERLAY_MAP_STRLEN</c>.</summary>
    public const int MaterialNameLength = 256;

    /// <summary><c>OVERLAY_NUM_RENDER_ORDERS</c>: <c>1 &lt;&lt; OVERLAY_RENDER_ORDER_NUM_BITS</c>.</summary>
    public const int RenderOrders = 4;

    /// <summary><c>OVERLAY_BSP_FACE_COUNT</c>.</summary>
    public const int MaxFaces = 64;

    /// <summary><c>WATEROVERLAY_BSP_FACE_COUNT</c>.</summary>
    public const int MaxWaterFaces = 256;

    /// <summary>
    /// <c>nId</c>: the overlay's index for an <c>info_overlay</c>; for a water
    /// overlay, <c>MAX_MAP_OVERLAYS + 1</c> plus its index.
    /// </summary>
    public int Id { get; set; }

    /// <summary><c>flU[2]</c>: StartU, EndU.</summary>
    public (float Start, float End) U { get; set; }

    /// <summary><c>flV[2]</c>: StartV, EndV.</summary>
    public (float Start, float End) V { get; set; }

    /// <summary>
    /// <c>flFadeDistMinSq</c>: <c>fademindist</c>, squared when positive
    /// Never set for a water overlay, whose fades
    /// are not emitted.
    /// </summary>
    public float FadeDistMinSq { get; set; }

    /// <summary><c>flFadeDistMaxSq</c>.</summary>
    public float FadeDistMaxSq { get; set; }

    /// <summary><c>vecOrigin</c>: <c>BasisOrigin</c>.</summary>
    public Vec3 Origin { get; set; }

    /// <summary><c>vecBasis[3]</c>: <c>BasisU</c>, <c>BasisV</c>, <c>BasisNormal</c>.</summary>
    public Vec3[] Basis { get; } = new Vec3[3];

    /// <summary><c>vecUVPoints[4]</c>: <c>uv0</c>..<c>uv3</c>.</summary>
    public Vec3[] UvPoints { get; } = new Vec3[4];

    /// <summary><c>szMaterialName</c>.</summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary><c>m_nRenderOrder</c>.</summary>
    public int RenderOrder { get; set; }

    /// <summary><c>aSideList</c>: the brush side ids from <c>sides</c>.</summary>
    public List<int> SideList { get; } = [];

    /// <summary>
    /// <c>aFaceList</c>: the BSP faces the overlay's sides became, in emit
    /// order and without duplicates.
    /// </summary>
    public List<int> FaceList { get; } = [];
}
