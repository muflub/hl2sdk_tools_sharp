namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// The feature vocabulary: every compiler behaviour the catalogue is supposed to
/// cover, transcribed from plan_maptools.md §2a's feature table.
///
/// <para>
/// THE WHOLE LIST IS HERE, not just the part this lane built entries for. That
/// is deliberate and it is what makes the matrix fact worth running: coverage is
/// a fraction of the real denominator, and a feature nobody has written an entry
/// for shows up as a named gap with the phase that owns it beside it. A
/// vocabulary trimmed to what is already covered would report 100% and mean
/// nothing.
/// </para>
///
/// <para>
/// A plain enum rather than [Flags], because there are more than 64 of these and
/// an entry declares a small SET of them (<see cref="CatalogEntry.Features"/>).
/// </para>
/// </summary>
public enum MapFeature
{
    // ------------------------------------------------------------------
    // vbsp — geometry.
    // ------------------------------------------------------------------

    /// <summary>A plain world brush: the thing everything else is measured against.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "a plain structural world brush")]
    StructuralBrush,

    /// <summary>Two solids sharing a volume, resolved by CSG.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "overlapping-brush CSG")]
    OverlappingBrushCsg,

    /// <summary>A brush with a face on no axis.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "non-axial brush faces")]
    NonAxialBrush,

    /// <summary>A brush small enough for `-micro` to report it.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "tiny brushes, reported by -micro")]
    MicroBrush,

    /// <summary>A solid that is not convex, or has no volume at all.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "invalid solids")]
    InvalidSolid,

    /// <summary>Geometry tied to func_detail: drawn, collided with, and not part of the BSP tree.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "func_detail against the same brush left structural")]
    FuncDetail,

    /// <summary>A hint brush with skip on its other faces, forcing a split.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "hint/skip")]
    HintSkip,

    /// <summary>clip, playerclip, npcclip, nodraw, trigger, invisible, ladder, blocklos, origin.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "the tool textures")]
    ToolTextures,

    /// <summary>A vertex of one face landing in the middle of another's edge.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "T-junction fixing")]
    TJunctions,

    /// <summary>Coplanar faces merged into one.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "face merge")]
    FaceMerge,

    /// <summary>A face cut up by $subdivsize.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "face subdivision at $subdivsize")]
    FaceSubdivision,

    /// <summary>A face big enough to press on the lightmap extent limit.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "large faces against the lightmap-extent limit")]
    LargeFaceLightmapLimit,

    /// <summary>An entity outside the sealed world: the leak, and the `.lin` it writes.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "a leak — entity in the void, LeakReport and .lin")]
    Leak,

    /// <summary>An areaportal that does not seal, which leaks differently.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "an areaportal leak")]
    AreaportalLeak,

    // ------------------------------------------------------------------
    // vbsp — structure.
    // ------------------------------------------------------------------

    /// <summary>func_areaportal in a doorway, open and closed.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "areaportals, open and closed")]
    Areaportal,

    /// <summary>Areas inside areas.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "nested areas")]
    NestedAreas,

    /// <summary>func_occluder and the occlusion lump.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "occluders")]
    Occluder,

    /// <summary>sky_camera and the 3D skybox's own area.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "3D skybox (sky_camera)")]
    SkyboxThreeD,

    /// <summary>A water brush: the surface, the volume below it, and the leaf water data.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "water — leaf water data")]
    Water,

    /// <summary>water_lod_control and the LOD distances it writes.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "water LOD control")]
    WaterLodControl,

    /// <summary>A fog volume and the leaves inside it.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "fog volumes")]
    FogVolume,

    /// <summary>The per-leaf distance to the nearest water surface.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "distance-to-water")]
    DistanceToWater,

    /// <summary>Brushes tied to an entity, compiled as their own model.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "brush entities")]
    BrushEntity,

    /// <summary>Which brush entity gets which *N model index, and in what order.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "brush-entity model numbering")]
    ModelNumbering,

    /// <summary>func_instance, including nested and rotated instances.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "func_instance, nested and rotated")]
    FuncInstance,

    /// <summary>The $-prefixed name fix-up an instance applies to its contents.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "instance name fix-up")]
    InstanceNameFixup,

    /// <summary>A .vmm manifest of several maps.</summary>
    [MapFeature(CompileTool.Vbsp, 3, ".vmm manifests")]
    VmmManifest,

    /// <summary>A cordon: only what is inside the box gets compiled.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "cordons")]
    Cordon,

    // ------------------------------------------------------------------
    // vbsp — surfaces and content.
    // ------------------------------------------------------------------

    /// <summary>A power-2 displacement.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "displacement, power 2")]
    DisplacementPower2,

    /// <summary>A power-3 displacement.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "displacement, power 3")]
    DisplacementPower3,

    /// <summary>A power-4 displacement.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "displacement, power 4")]
    DisplacementPower4,

    /// <summary>Two displacements sewn along a shared edge.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "displacement neighbours and sewing")]
    DisplacementNeighbours,

    /// <summary>WorldVertexTransition alpha blending across a displacement.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "displacement alpha-blend / WorldVertexTransition")]
    DisplacementAlphaBlend,

    /// <summary>The collision surface a displacement contributes.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "displacement collision")]
    DisplacementCollision,

    /// <summary>info_overlay on a world face.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "overlays")]
    Overlay,

    /// <summary>An overlay on a water surface.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "water overlays")]
    WaterOverlay,

    /// <summary>env_cubemap and the VTF it patches materials to point at.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "env_cubemap")]
    EnvCubemap,

    /// <summary>The default cubemap vbsp writes when a map has none.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "default cubemaps")]
    DefaultCubemap,

    /// <summary>prop_static and the sprp game lump.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "static props")]
    StaticProp,

    /// <summary>Each static-prop solid type: none, bounding box, VPhysics.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "static-prop solid types")]
    StaticPropSolidTypes,

    /// <summary>A prop_static naming a model that is not there.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "a missing prop model")]
    MissingPropModel,

    /// <summary>The `-onlyprops` pass.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "-onlyprops")]
    OnlyProps,

    /// <summary>detail.vbsp and the dprp game lump.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "detail props (detail.vbsp)")]
    DetailProps,

    /// <summary>%detailtype on a material, choosing which detail group grows on it.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "%detailtype")]
    DetailTypeMaterial,

    /// <summary>A material built with `patch`.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "material patch")]
    MaterialPatch,

    /// <summary>A material built with `include`.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "material include")]
    MaterialInclude,

    /// <summary>The %compile* flags on a material.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "every %compile* flag")]
    CompileFlags,

    /// <summary>$surfaceprop, and the game material it resolves to.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "$surfaceprop")]
    SurfaceProp,

    /// <summary>What ends up in the embedded pak.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "pak contents")]
    PakContents,

    /// <summary>`-embed`, which adds a directory to the pak.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "-embed")]
    EmbedDirectory,

    /// <summary>`-onlyents`, which rewrites the entity lump and nothing else.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "-onlyents")]
    OnlyEnts,

    // ------------------------------------------------------------------
    // vbsp — collision.
    // ------------------------------------------------------------------

    /// <summary>The world's collision record in LUMP_PHYSCOLLIDE.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "world collision")]
    WorldCollision,

    /// <summary>A brush entity's own collision record.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "brush-entity collision")]
    BrushEntityCollision,

    /// <summary>The fluid records water contributes.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "water collision")]
    WaterCollision,

    /// <summary>`-novirtualmesh`, which changes how displacement collision is written.</summary>
    [MapFeature(CompileTool.Vbsp, 3, "-novirtualmesh")]
    NoVirtualMesh,

    // ------------------------------------------------------------------
    // vvis. Phase 2 — the first phase with a managed compiler to assert
    // against, and so the only phase whose features this lane must cover.
    // ------------------------------------------------------------------

    /// <summary>One sealed room: the trivial PVS, and the baseline everything else is read against.</summary>
    [MapFeature(CompileTool.Vvis, 2, "one room — the trivial PVS")]
    TrivialPvs,

    /// <summary>Two rooms joined by a doorway: one portal, and the simplest real flood.</summary>
    [MapFeature(CompileTool.Vvis, 2, "two rooms and a door")]
    PortalThroughDoorway,

    /// <summary>A chain of rooms: portal flow that has to walk a long way.</summary>
    [MapFeature(CompileTool.Vvis, 2, "a long corridor — deep portal chains")]
    DeepPortalChain,

    /// <summary>An open room broken up by pillars: many mutually visible portals, the expensive case.</summary>
    [MapFeature(CompileTool.Vvis, 2, "an open arena — many mutually visible portals")]
    WideOpenPvs,

    /// <summary>Four rooms with their doorways aligned: the first must see the last.</summary>
    [MapFeature(CompileTool.Vvis, 2, "a see-through-three-doors line")]
    SightlineThroughDoors,

    /// <summary>The same rooms with one doorway nudged off the line: the first must NOT see the last.</summary>
    [MapFeature(CompileTool.Vvis, 2, "an almost-line that must not see")]
    NearMissSightline,

    /// <summary>`-fast`: base vis only, no portal flow.</summary>
    [MapFeature(CompileTool.Vvis, 2, "-fast")]
    VvisFast,

    /// <summary>worldspawn's `farz`, which clips visibility at a distance.</summary>
    [MapFeature(CompileTool.Vvis, 2, "farz")]
    Farz,

    /// <summary>`-radius_override`, which does the same from the command line.</summary>
    [MapFeature(CompileTool.Vvis, 2, "-radius_override")]
    VvisRadiusOverride,

    /// <summary>Leaves that are inside water or a fog volume, which vvis treats specially.</summary>
    [MapFeature(CompileTool.Vvis, 2, "water and fog volume leaves")]
    WaterAndFogLeaves,

    // ------------------------------------------------------------------
    // vrad.
    // ------------------------------------------------------------------

    /// <summary>A plain `light`.</summary>
    [MapFeature(CompileTool.Vrad, 4, "light")]
    PointLight,

    /// <summary>A `light_spot` and its cone.</summary>
    [MapFeature(CompileTool.Vrad, 4, "light_spot")]
    SpotLight,

    /// <summary>`light_environment`: the sun and the ambient term.</summary>
    [MapFeature(CompileTool.Vrad, 4, "light_environment — sun and ambient")]
    LightEnvironment,

    /// <summary>A texlight declared in lights.rad.</summary>
    [MapFeature(CompileTool.Vrad, 4, "texlights via lights.rad")]
    TexLight,

    /// <summary>The falloff variants a light can declare.</summary>
    [MapFeature(CompileTool.Vrad, 4, "falloff variants")]
    LightFalloff,

    /// <summary>A light's fade distances.</summary>
    [MapFeature(CompileTool.Vrad, 4, "fade distances")]
    FadeDistance,

    /// <summary>A named, styled light that the game can switch.</summary>
    [MapFeature(CompileTool.Vrad, 4, "named/styled switchable lights")]
    SwitchableLight,

    /// <summary>One light and one plane: a scene whose answer can be written down in closed form.</summary>
    [MapFeature(CompileTool.Vrad, 4, "an analytically solvable scene")]
    AnalyticLightScene,

    /// <summary>A closed box: radiosity bounce, where the energy must not grow.</summary>
    [MapFeature(CompileTool.Vrad, 4, "bounce in a closed box")]
    BounceEnergy,

    /// <summary>A bumped surface's three extra lightmap sets.</summary>
    [MapFeature(CompileTool.Vrad, 4, "bump-mapped lightmaps")]
    BumpedLightmap,

    /// <summary>LDR lighting.</summary>
    [MapFeature(CompileTool.Vrad, 4, "LDR")]
    LdrLighting,

    /// <summary>HDR lighting, which is a second pair of lumps.</summary>
    [MapFeature(CompileTool.Vrad, 4, "HDR")]
    HdrLighting,

    /// <summary>`-both`, which has to produce a consistent pair.</summary>
    [MapFeature(CompileTool.Vrad, 4, "-both")]
    BothLighting,

    /// <summary>Per-face luxel scale.</summary>
    [MapFeature(CompileTool.Vrad, 4, "luxel scales")]
    LuxelScale,

    /// <summary>Smoothing groups and the phong normals they produce.</summary>
    [MapFeature(CompileTool.Vrad, 4, "phong smoothing")]
    PhongSmoothing,

    /// <summary>`-extra` / `-final` supersampling.</summary>
    [MapFeature(CompileTool.Vrad, 4, "supersampling (-extra/-final)")]
    Supersampling,

    /// <summary>A static prop casting a shadow from its collision model.</summary>
    [MapFeature(CompileTool.Vrad, 4, "prop shadows")]
    PropShadow,

    /// <summary>`-StaticPropPolys`, which casts from the render mesh instead.</summary>
    [MapFeature(CompileTool.Vrad, 4, "-StaticPropPolys")]
    StaticPropPolys,

    /// <summary>`-textureshadows`, which reads the prop's texture alpha.</summary>
    [MapFeature(CompileTool.Vrad, 4, "-textureshadows")]
    TextureShadows,

    /// <summary>Per-vertex prop lighting and the .vhv it writes.</summary>
    [MapFeature(CompileTool.Vrad, 4, "per-vertex prop lighting and .vhv")]
    PerVertexPropLighting,

    /// <summary>The lighting a detail prop gets.</summary>
    [MapFeature(CompileTool.Vrad, 4, "detail-prop lighting")]
    DetailPropLighting,

    /// <summary>LUMP_LEAF_AMBIENT_LIGHTING and its index.</summary>
    [MapFeature(CompileTool.Vrad, 4, "leaf ambient")]
    LeafAmbient,

    /// <summary>Lighting a displacement's own luxel grid.</summary>
    [MapFeature(CompileTool.Vrad, 4, "displacement lighting")]
    DisplacementLighting,

    /// <summary>A macro texture modulating a surface's light.</summary>
    [MapFeature(CompileTool.Vrad, 4, "macro textures")]
    MacroTexture,

    /// <summary>Sky light reaching the inside of a room through a hole.</summary>
    [MapFeature(CompileTool.Vrad, 4, "skylight through an opening")]
    SkylightThroughOpening,
}
