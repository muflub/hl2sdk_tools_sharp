//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>The <c>MAX_MAP_*</c> caps the write stage enforces.</summary>
internal static class WriteLimits
{
    internal const int MaxMapModels = 1024;
    internal const int MaxMapBrushes = 8192;
    internal const int MaxMapAreas = 256;
    internal const int MaxMapAreaPortals = 1024;
    internal const int MaxMapPlanes = 65536;
    internal const int MaxMapNodes = 65536;
    internal const int MaxMapBrushSides = 65536;
    internal const int MaxMapLeafs = 65536;
    internal const int MaxMapFaces = 65536;
    internal const int MaxMapLeafFaces = 65536;
    internal const int MaxMapLeafBrushes = 65536;
    internal const int MaxMapLeafWaterData = 32768;
    internal const int MaxMapPortalVerts = 128000;
    internal const int MaxMapSurfEdges = 512000;

    /// <summary>
    /// <c>MAX_MAP_CUBEMAPSAMPLES</c>: the most <c>env_cubemap</c> samples
    /// the SDK's vbsp holds (its sample array's size), so the most a map it
    /// compiles carries; a linked level is held to it too, since the
    /// flattened level's compile must be able to write the same map.
    /// </summary>
    internal const int MaxMapCubemapSamples = 1024;

    /// <summary>
    /// <c>MAX_MAP_DISPINFO</c>: the most displacements the SDK's vbsp loads
    /// from a map (the <c>-maxdispinfo</c> of later branches raises it), so
    /// the most a map it compiles carries; a linked level is held to it too,
    /// since the flattened level's compile must be able to write the same
    /// map.
    /// </summary>
    internal const int MaxMapDispInfo = 2048;

    /// <summary><c>MAX_SWITCHED_LIGHTS</c>.</summary>
    internal const int MaxSwitchedLights = 32;

    /// <summary>
    /// <c>MAX_LIGHTMAP_DIM_WITHOUT_BORDER</c>, which this
    /// SDK defines as the DISPLACEMENT limit, 125 -- not the brush limit of 32
    /// <c>CalcFaceExtents</c> uses it for brush faces.
    /// </summary>
    internal const int MaxLightmapDimWithoutBorder = 125;

    /// <summary><c>MAX_DISP_LIGHTMAP_DIM_WITHOUT_BORDER</c>.</summary>
    internal const int MaxDispLightmapDimWithoutBorder = 125;
}

/// <summary>Diagnostic codes raised by the write stage and the driver.</summary>
/// <remarks>
/// VBSP06xx: 0501-0505 are Phase 3f's displacement codes.
/// </remarks>
public static class WriteCodes
{
    /// <summary><c>EmitFace</c>: a NODRAW face kept because it is a displacement.</summary>
    public const string NoDrawTerrain = "VBSP0601";

    /// <summary><c>EnsurePresenceOfWaterLODControlEntity</c> created a default entity.</summary>
    public const string DefaultWaterLodControl = "VBSP0602";

    /// <summary><c>SetOccluderArea</c>: an occluder spans two areas.</summary>
    public const string OccluderStraddlesAreas = "VBSP0603";

    /// <summary><c>ProcessWorldModel</c>: the map leaked.</summary>
    public const string Leaked = "VBSP0605";

    /// <summary>A <c>MAX_MAP_*</c> limit (or <c>MAX_SWITCHED_LIGHTS</c>) was exceeded.</summary>
    public const string LimitExceeded = "VBSP0610";

    /// <summary><c>ProcessSubModel</c>: a brush model whose tree is a single leaf.</summary>
    public const string ModelHasNoHeadNode = "VBSP0611";

    /// <summary><c>Compute3DSkyboxAreas</c>: a <c>sky_camera</c> inside solid.</summary>
    public const string SkyCameraInSolid = "VBSP0612";

    /// <summary><c>CalcFaceExtents</c>: a face too big to have a lightmap.</summary>
    public const string BadSurfaceExtents = "VBSP0613";

    /// <summary>
    /// <c>ProcessModels</c> wrote no model: worldspawn and every brush entity
    /// have no brushes, so there is no world model for <c>EndBSPFile</c> to
    /// bound, and nothing a loader could open.
    /// </summary>
    /// <remarks>
    /// The reference implementation has no check here: it skips every entity
    /// without brushes, the world included, and goes on to bound a world
    /// model it never wrote. There is no map to match, so refusing it with a
    /// message changes no output, and no map that compiles is affected (an empty worldspawn beside a brush entity still
    /// writes that entity as model 0, as the reference does).
    /// </remarks>
    public const string NoBrushes = "VBSP0614";

    /// <summary>
    /// An invariant of the write stage failed (odd node plane, bad leaf face,
    /// a displacement with no base face): a compiler bug, not a map error.
    /// </summary>
    public const string Internal = "VBSP0619";
}
