namespace SourceSharp.MapFormats.Bsp;

/// <summary>
/// The 64 lump slots of a Source BSP file, numbered as in
/// <c>src/public/bspfile.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// The numbering is the file format, so these values are fixed forever and the
/// gaps are real: <see cref="Unused0"/> through <see cref="Unused3"/> were
/// never assigned, and <see cref="PhysCollideSurface"/> and
/// <see cref="XZipPakFile"/> are deprecated slots the engine still skips over.
/// They are named rather than omitted because a lump table is read by index and
/// a hole that is not in the enum reads as a corrupt file.
/// </para>
/// <para>
/// Slots 61-63 have no assignment in <c>bspfile.h</c> at all. There is no name
/// to give them, so they have none here; <see cref="BspData"/> still carries
/// their bytes, because a lump this port does not understand is data to preserve
/// and not licence to drop.
/// </para>
/// </remarks>
public enum BspLump
{
    /// <summary>Entity keyvalue text, NUL-terminated.</summary>
    Entities = 0,

    /// <summary>World planes. Index order is assigned by vbsp and is output.</summary>
    Planes = 1,

    /// <summary>Texture data: reflectivity, dimensions, string-table index.</summary>
    TexData = 2,

    /// <summary>World vertex positions.</summary>
    Vertexes = 3,

    /// <summary>Compressed PVS and PAS, written by vvis.</summary>
    Visibility = 4,

    /// <summary>BSP tree interior nodes.</summary>
    Nodes = 5,

    /// <summary>Texture mapping: the two texture-space axes, flags, texdata index.</summary>
    TexInfo = 6,

    /// <summary>Faces, low dynamic range.</summary>
    Faces = 7,

    /// <summary>Lightmap samples, low dynamic range. <c>ColorRGBExp32</c>.</summary>
    Lighting = 8,

    /// <summary>Occluder polygons and planes.</summary>
    Occlusion = 9,

    /// <summary>BSP tree leaves. 32 bytes at lump version 1, 56 at version 0.</summary>
    Leafs = 10,

    /// <summary>Hammer face ids, for round-tripping edits back to the VMF.</summary>
    FaceIds = 11,

    /// <summary>Edges: pairs of vertex indices.</summary>
    Edges = 12,

    /// <summary>Signed edge indices; the sign selects the edge's direction.</summary>
    SurfEdges = 13,

    /// <summary>Brush models. Model 0 is the world; the rest are brush entities.</summary>
    Models = 14,

    /// <summary>Light sources as vrad resolved them, low dynamic range.</summary>
    WorldLights = 15,

    /// <summary>Leaf-to-face index list.</summary>
    LeafFaces = 16,

    /// <summary>Leaf-to-brush index list.</summary>
    LeafBrushes = 17,

    /// <summary>Collision brushes.</summary>
    Brushes = 18,

    /// <summary>Brush sides: plane, texinfo, dispinfo, bevel flag.</summary>
    BrushSides = 19,

    /// <summary>Visibility areas, joined by areaportals.</summary>
    Areas = 20,

    /// <summary>Areaportals: the doors between areas the engine can close.</summary>
    AreaPortals = 21,

    /// <summary>Never assigned.</summary>
    Unused0 = 22,

    /// <summary>Never assigned.</summary>
    Unused1 = 23,

    /// <summary>Never assigned.</summary>
    Unused2 = 24,

    /// <summary>Never assigned.</summary>
    Unused3 = 25,

    /// <summary>Displacement headers.</summary>
    DispInfo = 26,

    /// <summary>Faces before merging and subdivision; vrad lights these.</summary>
    OriginalFaces = 27,

    /// <summary>
    /// Per-displacement collision sizes. A size table only: the engine rebuilds
    /// the geometry at load through <c>CreateVirtualMesh</c>.
    /// </summary>
    PhysDisp = 28,

    /// <summary>Cooked collision blobs, framed as <c>dphysmodel_t</c> records.</summary>
    PhysCollide = 29,

    /// <summary>Vertex normals, for phong-smoothed lighting.</summary>
    VertNormals = 30,

    /// <summary>Indices into <see cref="VertNormals"/>, one per face vertex.</summary>
    VertNormalIndices = 31,

    /// <summary>Displacement lightmap alphas. Unused in this branch.</summary>
    DispLightmapAlphas = 32,

    /// <summary>Displacement vertex offsets, alphas and distances.</summary>
    DispVerts = 33,

    /// <summary>Per-displacement lightmap sample positions, run-length coded.</summary>
    DispLightmapSamplePositions = 34,

    /// <summary>The game lump: a nested directory of game-specific lumps.</summary>
    GameLump = 35,

    /// <summary>Water volumes, for fog and surface height.</summary>
    LeafWaterData = 36,

    /// <summary>Triangle strips and fans for faces that need them.</summary>
    Primitives = 37,

    /// <summary>Vertices referenced by <see cref="Primitives"/>.</summary>
    PrimVerts = 38,

    /// <summary>Indices referenced by <see cref="Primitives"/>.</summary>
    PrimIndices = 39,

    /// <summary>
    /// The embedded zip. The engine expects it LAST in the file
    /// (<c>modelloader.cpp:643</c>).
    /// </summary>
    PakFile = 40,

    /// <summary>Areaportal clip polygon vertices.</summary>
    ClipPortalVerts = 41,

    /// <summary>Cubemap sample positions and sizes.</summary>
    Cubemaps = 42,

    /// <summary>Material name characters. Must end in a NUL.</summary>
    TexDataStringData = 43,

    /// <summary>Offsets into <see cref="TexDataStringData"/>.</summary>
    TexDataStringTable = 44,

    /// <summary>Decals and overlays projected onto faces.</summary>
    Overlays = 45,

    /// <summary>Per-leaf distance to the nearest water surface.</summary>
    LeafMinDistToWater = 46,

    /// <summary>Per-face macro texture indices.</summary>
    FaceMacroTextureInfo = 47,

    /// <summary>Displacement triangle tags: walkable, buildable, surface.</summary>
    DispTris = 48,

    /// <summary>
    /// Deprecated: win32-specific Havok terrain compression. Never written.
    /// </summary>
    PhysCollideSurface = 49,

    /// <summary>Overlays applied to water surfaces.</summary>
    WaterOverlays = 50,

    /// <summary>Index into <see cref="LeafAmbientLightingHdr"/>.</summary>
    LeafAmbientIndexHdr = 51,

    /// <summary>Index into <see cref="LeafAmbientLighting"/>.</summary>
    LeafAmbientIndex = 52,

    /// <summary>Lightmap samples, high dynamic range.</summary>
    LightingHdr = 53,

    /// <summary>Light sources as vrad resolved them, high dynamic range.</summary>
    WorldLightsHdr = 54,

    /// <summary>Per-leaf ambient cubes, high dynamic range.</summary>
    LeafAmbientLightingHdr = 55,

    /// <summary>Per-leaf ambient cubes, low dynamic range.</summary>
    LeafAmbientLighting = 56,

    /// <summary>Deprecated: the Xbox 1 xzip pak. Never written.</summary>
    XZipPakFile = 57,

    /// <summary>Faces, high dynamic range. Written only when they differ.</summary>
    FacesHdr = 58,

    /// <summary>Level-wide feature flags. Absent from many maps.</summary>
    MapFlags = 59,

    /// <summary>Overlay fade distances, parallel to <see cref="Overlays"/>.</summary>
    OverlayFades = 60,
}
