namespace SourceSharp.MapTools.Bsp.MaterialPatch;

/// <summary>
/// The diagnostic codes of Phase 3g's emitters: static props, detail props,
/// overlays, cubemaps and material patches. Each names the stock message it
/// replaces.
/// </summary>
public static class SurfaceContentDiagnostics
{
    /// <summary>"Material %s is depending on itself through materialvar %s! Ignoring..." (<c>cubemap.cpp:161</c>).</summary>
    public const string MaterialDependsOnItself = "VBSP0701";

    /// <summary>"Multiple references for cubemap on texture %s!!!" (<c>cubemap.cpp:611</c>).</summary>
    public const string CubemapMultipleReferences = "VBSP0702";

    /// <summary>"env_cubemap pointing at deleted brushside near (%d, %d, %d)" (<c>cubemap.cpp:718</c>).</summary>
    public const string CubemapDeletedSide = "VBSP0703";

    /// <summary>"Generated env_cubemap patch name : %s too long!" — fatal in stock (<c>cubemap.cpp:519</c>).</summary>
    public const string CubemapPatchNameTooLong = "VBSP0704";

    /// <summary>"Can't load skybox file %s to build the default cubemap!" (<c>cubemap.cpp:306</c>).</summary>
    public const string DefaultCubemapSkyboxMissing = "VBSP0705";

    /// <summary>"*** Error: Skybox vtf files for %s weren't compiled with the same size texture and/or same flags!" (<c>cubemap.cpp:250</c>).</summary>
    public const string DefaultCubemapSkyboxMismatch = "VBSP0706";

    /// <summary>"*** Error unserializing skybox texture: %s" (<c>cubemap.cpp:237</c>).</summary>
    public const string DefaultCubemapSkyboxUnreadable = "VBSP0707";

    /// <summary>"Error loading studio model \"%s\"!" (<c>staticprop.cpp:270</c>, <c>detailobjects.cpp:448</c>).</summary>
    public const string StudioModelLoadFailed = "VBSP0710";

    /// <summary>"Error! To use model \"%s\" with %s, it must be compiled with $staticprop!" (<c>staticprop.cpp:175</c>).</summary>
    public const string StudioModelNotStaticProp = "VBSP0711";

    /// <summary>"Error! %s using model \"%s\", which must be used on a dynamic entity (i.e. prop_physics). Deleted." (<c>staticprop.cpp:180</c>).</summary>
    public const string StudioModelDynamicOnly = "VBSP0712";

    /// <summary>"Static prop %s outside the map (%.2f, %.2f, %.2f)" (<c>staticprop.cpp:489</c>).</summary>
    public const string StaticPropOutsideMap = "VBSP0713";

    /// <summary>"Bad geometry on \"%s\"!" (<c>staticprop.cpp:290</c>).</summary>
    public const string StaticPropBadGeometry = "VBSP0714";

    /// <summary>"Material %s uses unknown detail object type %s!" (<c>detailobjects.cpp:868</c>).</summary>
    public const string DetailUnknownType = "VBSP0720";

    /// <summary>"Error! Too many detail props on this map. %d were not emitted!" (<c>detailobjects.cpp:964</c>).</summary>
    public const string DetailOverflow = "VBSP0721";

    /// <summary>"Invalid arguments to \"sprite\" in detail.vbsp" — fatal in stock (<c>detailobjects.cpp:173</c>).</summary>
    public const string DetailInvalidSprite = "VBSP0722";

    /// <summary>"Overlay (%s) at %f %f %f has invalid render order (%d)." — fatal in stock (<c>overlay.cpp:59</c>).</summary>
    public const string OverlayInvalidRenderOrder = "VBSP0730";

    /// <summary>"Overlay Material Name (%s) too long!" — fatal in stock (<c>overlay.cpp:76</c>, <c>map.cpp:1335</c>).</summary>
    public const string OverlayMaterialNameTooLong = "VBSP0731";

    /// <summary>"Overlay touching too many faces" — fatal in stock (<c>overlay.cpp:277,366</c>).</summary>
    public const string OverlayTooManyFaces = "VBSP0732";

    /// <summary>"Too Many Overlays!" / "Too many water overlays!" — fatal in stock (<c>overlay.cpp:213,304</c>).</summary>
    public const string TooManyOverlays = "VBSP0733";
}
