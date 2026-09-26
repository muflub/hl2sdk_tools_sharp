//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Bsp.MaterialPatch;

/// <summary>
/// The diagnostic codes of Phase 3g's emitters: static props, detail props,
/// overlays, cubemaps and material patches. Each names the stock message it
/// replaces.
/// </summary>
public static class SurfaceContentDiagnostics
{
    /// <summary>"Material %s is depending on itself through materialvar %s! Ignoring...".</summary>
    public const string MaterialDependsOnItself = "VBSP0701";

    /// <summary>"Multiple references for cubemap on texture %s!!!".</summary>
    public const string CubemapMultipleReferences = "VBSP0702";

    /// <summary>"env_cubemap pointing at deleted brushside near (%d, %d, %d)".</summary>
    public const string CubemapDeletedSide = "VBSP0703";

    /// <summary>"Generated env_cubemap patch name: %s too long!" — fatal in stock.</summary>
    public const string CubemapPatchNameTooLong = "VBSP0704";

    /// <summary>"Can't load skybox file %s to build the default cubemap!".</summary>
    public const string DefaultCubemapSkyboxMissing = "VBSP0705";

    /// <summary>"*** Error: Skybox vtf files for %s weren't compiled with the same size texture and/or same flags!".</summary>
    public const string DefaultCubemapSkyboxMismatch = "VBSP0706";

    /// <summary>"*** Error unserializing skybox texture: %s".</summary>
    public const string DefaultCubemapSkyboxUnreadable = "VBSP0707";

    /// <summary>"Error loading studio model \"%s\"!".</summary>
    public const string StudioModelLoadFailed = "VBSP0710";

    /// <summary>"Error! To use model \"%s\" with %s, it must be compiled with $staticprop!".</summary>
    public const string StudioModelNotStaticProp = "VBSP0711";

    /// <summary>"Error! %s using model \"%s\", which must be used on a dynamic entity (i.e. prop_physics). Deleted.".</summary>
    public const string StudioModelDynamicOnly = "VBSP0712";

    /// <summary>"Static prop %s outside the map (%.2f, %.2f, %.2f)".</summary>
    public const string StaticPropOutsideMap = "VBSP0713";

    /// <summary>"Bad geometry on \"%s\"!".</summary>
    public const string StaticPropBadGeometry = "VBSP0714";

    /// <summary>"Material %s uses unknown detail object type %s!".</summary>
    public const string DetailUnknownType = "VBSP0720";

    /// <summary>"Error! Too many detail props on this map. %d were not emitted!".</summary>
    public const string DetailOverflow = "VBSP0721";

    /// <summary>"Invalid arguments to \"sprite\" in detail.vbsp" — fatal in stock.</summary>
    public const string DetailInvalidSprite = "VBSP0722";

    /// <summary>"Overlay (%s) at %f %f %f has invalid render order (%d)." — fatal in stock.</summary>
    public const string OverlayInvalidRenderOrder = "VBSP0730";

    /// <summary>"Overlay Material Name (%s) too long!" — fatal in stock.</summary>
    public const string OverlayMaterialNameTooLong = "VBSP0731";

    /// <summary>"Overlay touching too many faces" — fatal in stock.</summary>
    public const string OverlayTooManyFaces = "VBSP0732";

    /// <summary>"Too Many Overlays!" / "Too many water overlays!" — fatal in stock.</summary>
    public const string TooManyOverlays = "VBSP0733";
}
