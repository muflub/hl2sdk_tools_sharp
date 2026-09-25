using System.Collections.Immutable;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Validation;

/// <summary>
/// One rule the engine enforces when it loads a map, named by the code
/// <see cref="BspValidator"/> reports it under.
/// </summary>
/// <param name="Code">
/// The stable diagnostic code, <c>BSP0001</c> upwards. A host keys on this, so
/// it is a contract: it may never be reused for a different rule, and a rule
/// that is withdrawn leaves its number retired rather than recycled.
/// </param>
/// <param name="Severity">
/// <see cref="DiagnosticSeverity.Error"/> when breaking the rule stops the
/// engine loading the map or makes it read outside a lump;
/// <see cref="DiagnosticSeverity.Warning"/> when the engine loads the map but
/// silently drops content.
/// </param>
/// <param name="Title">What the rule demands, in one line.</param>
/// <param name="Citation">
/// The engine source the rule is transcribed from, as <c>file:line</c>. The
/// authority is the 2018 engine drop; <c>bsplib.cpp</c> citations are this
/// tree's own <c>src/utils/common/bsplib.cpp</c>.
/// </param>
/// <remarks>
/// The catalogue exists so that "every rule has a fact that can make it fire"
/// is checkable rather than asserted: a test enumerates
/// <see cref="BspRuleCatalog.All"/> and requires a corruption fact per code.
/// A validator whose rules cannot fire is worth nothing.
/// </remarks>
public sealed record BspRule(
    string Code,
    DiagnosticSeverity Severity,
    string Title,
    string Citation);

/// <summary>
/// Every rule <see cref="BspValidator"/> knows, in code order.
/// </summary>
public static class BspRuleCatalog
{
    /// <summary>The whole rule set, ordered by <see cref="BspRule.Code"/>.</summary>
    public static ImmutableArray<BspRule> All { get; } =
    [
        new(BspRuleCodes.Ident, DiagnosticSeverity.Error,
            "the file header's ident must be VBSP",
            "engine/modelloader.cpp:454-459"),

        new(BspRuleCodes.FileVersion, DiagnosticSeverity.Error,
            "the file version must be between MINBSPVERSION and BSPVERSION, 19..20",
            "engine/modelloader.cpp:462-468, public/bspfile.h:24-25"),

        new(BspRuleCodes.LumpElementSize, DiagnosticSeverity.Error,
            "a lump's length must be a whole number of its element",
            "engine/modelloader.cpp:1780-1782, engine/cmodel_bsp.cpp:317-320"),

        new(BspRuleCodes.LumpCap, DiagnosticSeverity.Error,
            "a lump's element count must not exceed its MAX_MAP_* cap",
            "engine/cmodel_bsp.cpp:327,385,429,575,615,661,745,841,879,919,956,986"),

        new(BspRuleCodes.RequiredLumpEmpty, DiagnosticSeverity.Error,
            "a lump the collision loader requires must not be empty",
            "engine/cmodel_bsp.cpp:323,383,423,569,609,839,877"),

        new(BspRuleCodes.PakFileLast, DiagnosticSeverity.Error,
            "the PAKFILE lump must be the last lump in the file",
            "engine/modelloader.cpp:639-645, :3108-3112"),

        new(BspRuleCodes.OcclusionVersion, DiagnosticSeverity.Error,
            "the OCCLUSION lump's version must be 0, 1 or 2",
            "engine/modelloader.cpp:1331-1399"),

        new(BspRuleCodes.LeafsVersion, DiagnosticSeverity.Error,
            "the LEAFS lump's version must be 0 or 1",
            "engine/modelloader.cpp:2286-2308, engine/cmodel_bsp.cpp:527-544"),

        new(BspRuleCodes.LeafAmbientLegacyPath, DiagnosticSeverity.Warning,
            "leaf ambient lighting is read directly only at lump version 1 with a non-empty index",
            "engine/modelloader.cpp:2203-2205"),

        new(BspRuleCodes.LeafAmbientLegacyCount, DiagnosticSeverity.Error,
            "on the legacy path the ambient lump must hold one light cube per leaf",
            "engine/modelloader.cpp:2210-2212, :2226"),

        new(BspRuleCodes.StaticPropVersion, DiagnosticSeverity.Warning,
            "the sprp game lump's version must be at least 4 or every static prop is dropped",
            "engine/staticpropmgr.cpp:1320-1325"),

        new(BspRuleCodes.DetailPropVersion, DiagnosticSeverity.Warning,
            "the dprp game lump's version must be at least 4 or every detail prop is dropped",
            "game/client/detailobjectsystem.cpp:1447-1451"),

        new(BspRuleCodes.HdrLumpPair, DiagnosticSeverity.Warning,
            "HDR is usable only when LIGHTING_HDR and WORLDLIGHTS_HDR are both non-empty",
            "engine/modelloader.cpp:1029-1037"),

        new(BspRuleCodes.FaceTexInfo, DiagnosticSeverity.Error,
            "a face's texinfo index must be inside the TEXINFO lump",
            "engine/modelloader.cpp:1913-1917"),

        new(BspRuleCodes.LeafFaceSurface, DiagnosticSeverity.Error,
            "a leafface must index a face that exists",
            "engine/modelloader.cpp:2525-2527"),

        new(BspRuleCodes.SurfEdgeCount, DiagnosticSeverity.Error,
            "the surfedge count must be at least 1 and below MAX_MAP_SURFEDGES",
            "engine/modelloader.cpp:2603-2605"),

        new(BspRuleCodes.SurfEdgeEdge, DiagnosticSeverity.Error,
            "a surfedge's magnitude must index an edge that exists",
            "engine/modelloader.cpp:2612-2620"),

        new(BspRuleCodes.EdgeVertex, DiagnosticSeverity.Error,
            "an edge a surfedge names must have endpoints that index vertices that exist",
            "engine/modelloader.cpp:2619-2620, engine/cmodel_bsp.cpp:1213-1223"),

        new(BspRuleCodes.NodeChildren, DiagnosticSeverity.Error,
            "a node's children must index a node or a leaf that exists",
            "engine/modelloader.cpp:2081-2088"),

        new(BspRuleCodes.LeafCluster, DiagnosticSeverity.Error,
            "a leaf's cluster must be inside the visibility lump's cluster table",
            "engine/cmodel_bsp.cpp:448-452, engine/cmodel.cpp:2339-2345"),

        new(BspRuleCodes.BrushSideTexInfo, DiagnosticSeverity.Error,
            "a brush side's texinfo must be inside the TEXINFO lump, or -1",
            "engine/cmodel_bsp.cpp:806-812"),

        new(BspRuleCodes.ModelHeadNode, DiagnosticSeverity.Error,
            "a model's head node must index a node that exists",
            "engine/modelloader.cpp:4662-4666"),

        new(BspRuleCodes.TexDataStringIndex, DiagnosticSeverity.Error,
            "a texdata's name must resolve through the string table into the string data",
            "engine/cmodel_bsp.cpp:340-345"),

        new(BspRuleCodes.OverlayFaces, DiagnosticSeverity.Error,
            "an overlay's face count must fit its fixed face array and name faces that exist",
            "engine/Overlay.cpp:1200-1204, public/bspfile.h:1000,1023"),

        new(BspRuleCodes.StaticPropDictIndex, DiagnosticSeverity.Error,
            "a static prop's type must index the model dictionary",
            "engine/staticpropmgr.cpp:1348"),

        new(BspRuleCodes.StaticPropLeafRun, DiagnosticSeverity.Error,
            "a static prop's leaf run must lie inside the leaf list",
            "engine/staticpropmgr.cpp:1521-1524, :1882-1886"),

        new(BspRuleCodes.Leaf0Solid, DiagnosticSeverity.Error,
            "leaf 0's contents must be CONTENTS_SOLID",
            "engine/cmodel_bsp.cpp:457-459, :520-522"),

        new(BspRuleCodes.SurfaceExtents, DiagnosticSeverity.Error,
            "a lit face's lightmap extents must be within the lightmap limit",
            "engine/modelloader.cpp:1617-1620, engine/gl_model_private.h:649-653"),

        new(BspRuleCodes.NoCubemaps, DiagnosticSeverity.Warning,
            "a map should carry at least one cubemap sample",
            "engine/modelloader.cpp:2433-2441"),

        new(BspRuleCodes.TexDataStringNul, DiagnosticSeverity.Error,
            "the texdata string data must end in a NUL",
            "src/utils/common/bsplib.cpp:2279-2280"),

        new(BspRuleCodes.PhysFraming, DiagnosticSeverity.Error,
            "the physics lump must frame as dphysmodel_t records ending in a -1 terminator",
            "engine/cmodel_bsp.cpp:1049-1070, src/utils/common/bsplib.cpp:1573-1645"),

        new(BspRuleCodes.PhysModelIndex, DiagnosticSeverity.Error,
            "a physics record's model index must name a model that exists",
            "engine/cmodel_bsp.cpp:1058-1061"),

        new(BspRuleCodes.PlaneIndex, DiagnosticSeverity.Error,
            "every plane reference must be inside the PLANES lump",
            "engine/modelloader.cpp:2073-2074, :1911, engine/cmodel_bsp.cpp:805"),

        new(BspRuleCodes.BrushSideRun, DiagnosticSeverity.Error,
            "a brush's side run must lie inside the BRUSHSIDES lump",
            "engine/cmodel_bsp.cpp:800-807"),

        new(BspRuleCodes.LeafRuns, DiagnosticSeverity.Error,
            "a leaf's leafface and leafbrush runs must lie inside their lumps",
            "engine/modelloader.cpp:2144-2150, engine/cmodel_bsp.cpp:441-447"),

        new(BspRuleCodes.DispPower, DiagnosticSeverity.Error,
            "a displacement's power must be at most MAX_MAP_DISP_POWER",
            "engine/cmodel_bsp.cpp:1186-1194, public/bspfile.h:47-48"),

        new(BspRuleCodes.DispRuns, DiagnosticSeverity.Error,
            "the displacement vertex and triangle runs must lie inside their lumps",
            "engine/cmodel_bsp.cpp:1187-1194"),

        new(BspRuleCodes.SubLumpFraming, DiagnosticSeverity.Error,
            "a lump built from counted runs must frame exactly within its bytes",
            "engine/staticpropmgr.cpp:1268-1296, engine/cmodel_bsp.cpp:1066-1068"),
    ];

    /// <summary>The rule a code names.</summary>
    /// <param name="code">A code from <see cref="BspRuleCodes"/>.</param>
    /// <returns>The rule.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No rule has that code.</exception>
    public static BspRule ByCode(string code)
    {
        foreach (BspRule rule in All)
        {
            if (rule.Code == code)
            {
                return rule;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(code), code, "no BSP validation rule has that code");
    }
}
