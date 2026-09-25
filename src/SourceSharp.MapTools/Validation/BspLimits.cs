using System.Collections.Immutable;

using SourceSharp.MapFormats.Bsp;

namespace SourceSharp.MapTools.Validation;

/// <summary>
/// The <c>MAX_MAP_*</c> caps the engine checks while loading, and the constants
/// the rules that are not a simple cap are written against.
/// </summary>
/// <remarks>
/// <para>
/// Only the caps the LOADER checks are here. <c>public/bspfile.h:60-102</c>
/// declares many more, but most of them bound a compile-time array in vbsp and
/// are never re-checked against a finished file; a validator that enforced
/// those would reject maps the engine loads happily.
/// </para>
/// <para>
/// The engine's own messages are sometimes wrong and are reproduced in the
/// rules rather than corrected: the LEAFS count is compared against
/// <c>MAX_MAP_PLANES</c> and the BRUSHSIDES count reports "Map has too many
/// planes" (<c>engine/cmodel_bsp.cpp:429</c>, <c>:745</c>). Both caps are
/// 65536, so the behaviour is right even where the wording is not.
/// </para>
/// </remarks>
public static class BspLimits
{
    /// <summary><c>CONTENTS_SOLID</c>, <c>public/bspflags.h:26</c>.</summary>
    public const int ContentsSolid = 0x1;

    /// <summary><c>SURF_NOLIGHT</c>, <c>public/bspflags.h:92</c>.</summary>
    public const int SurfNoLight = 0x0400;

    /// <summary>
    /// <c>MAX_BRUSH_LIGHTMAP_DIM_INCLUDING_BORDER</c>, <c>public/bspfile.h:32</c>.
    /// The lightmap extent limit for a face that is not a displacement.
    /// </summary>
    public const int MaxBrushLightmapDim = 35;

    /// <summary>
    /// <c>MAX_DISP_LIGHTMAP_DIM_INCLUDING_BORDER</c>, <c>public/bspfile.h:36</c>.
    /// The limit for a displacement face, which may be lit far more finely.
    /// </summary>
    public const int MaxDispLightmapDim = 128;

    /// <summary><c>MAX_MAP_SURFEDGES</c>, <c>public/bspfile.h:89</c>.</summary>
    public const int MaxMapSurfEdges = 512000;

    /// <summary><c>MAX_MAP_DISP_POWER</c>, <c>public/bspfile.h:48</c>.</summary>
    public const int MaxDispPower = 4;

    /// <summary><c>OVERLAY_BSP_FACE_COUNT</c>, <c>public/bspfile.h:1000</c>.</summary>
    public const int OverlayFaceCount = 64;

    /// <summary><c>WATEROVERLAY_BSP_FACE_COUNT</c>, <c>public/bspfile.h:1064</c>.</summary>
    public const int WaterOverlayFaceCount = 256;

    /// <summary>The lowest <c>sprp</c> version the engine will read.</summary>
    /// <remarks><c>engine/staticpropmgr.cpp:1321</c>.</remarks>
    public const int MinStaticPropVersion = 4;

    /// <summary>The lowest <c>dprp</c> version the client will read.</summary>
    /// <remarks><c>game/client/detailobjectsystem.cpp:1448</c>.</remarks>
    public const int MinDetailPropVersion = 4;

    /// <summary>
    /// The lumps the loader caps, with the cap and the name of the
    /// <c>MAX_MAP_*</c> constant it uses.
    /// </summary>
    public static ImmutableArray<(BspLump Lump, int Max, string Constant)> Caps { get; } =
    [
        // engine/cmodel_bsp.cpp:325-328.
        (BspLump.TexData, 2048, "MAX_MAP_TEXDATA"),

        // engine/cmodel_bsp.cpp:384-385.
        (BspLump.TexInfo, 12288, "MAX_MAP_TEXINFO"),

        // engine/cmodel_bsp.cpp:427-430 and :490-493. Compared against
        // MAX_MAP_PLANES, not MAX_MAP_LEAFS; both are 65536.
        (BspLump.Leafs, 65536, "MAX_MAP_PLANES"),

        // engine/cmodel_bsp.cpp:573-576.
        (BspLump.LeafBrushes, 65536, "MAX_MAP_LEAFBRUSHES"),

        // engine/cmodel_bsp.cpp:613-616.
        (BspLump.Planes, 65536, "MAX_MAP_PLANES"),

        // engine/cmodel_bsp.cpp:659-662.
        (BspLump.Brushes, 8192, "MAX_MAP_BRUSHES"),

        // engine/cmodel_bsp.cpp:743-746.
        (BspLump.BrushSides, 65536, "MAX_MAP_BRUSHSIDES"),

        // engine/cmodel_bsp.cpp:840-841.
        (BspLump.Models, 1024, "MAX_MAP_MODELS"),

        // engine/cmodel_bsp.cpp:878-879.
        (BspLump.Nodes, 65536, "MAX_MAP_NODES"),

        // engine/cmodel_bsp.cpp:917-920.
        (BspLump.Areas, 256, "MAX_MAP_AREAS"),

        // engine/cmodel_bsp.cpp:954-957.
        (BspLump.AreaPortals, 1024, "MAX_MAP_AREAPORTALS"),
    ];

    /// <summary>
    /// The lumps whose absence stops the collision loader, with the message the
    /// engine prints.
    /// </summary>
    /// <remarks>
    /// "Map with no planes" for an empty LEAFBRUSHES lump
    /// (<c>engine/cmodel_bsp.cpp:569</c>) is the engine's own wording, copied
    /// from the plane loader above it. It is reproduced rather than corrected so
    /// that a search for the engine's message finds this rule.
    /// </remarks>
    public static ImmutableArray<(BspLump Lump, string EngineMessage)> Required { get; } =
    [
        (BspLump.TexData, "Map with no textures"),
        (BspLump.TexInfo, "Map with no texinfo"),
        (BspLump.Leafs, "Map with no leafs"),
        (BspLump.LeafBrushes, "Map with no planes"),
        (BspLump.Planes, "Map with no planes"),
        (BspLump.Models, "Map with no models"),
        (BspLump.Nodes, "Map has no nodes"),
    ];

    /// <summary>
    /// <c>MAX_MAP_VISIBILITY</c> in bytes, <c>public/bspfile.h:91</c>. The
    /// visibility lump is capped on its BYTE length rather than an element
    /// count, because it is not an array (<c>engine/cmodel_bsp.cpp:985-987</c>).
    /// </summary>
    public const int MaxMapVisibilityBytes = 0x1000000;
}
