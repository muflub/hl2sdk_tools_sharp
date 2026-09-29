//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

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
/// <remarks>
/// The catalogue exists so that "every rule has a fact that can make it fire"
/// is checkable rather than asserted: a test enumerates
/// <see cref="BspRuleCatalog.All"/> and requires a corruption fact per code.
/// A validator whose rules cannot fire is worth nothing.
/// </remarks>
public sealed record BspRule(
    string Code,
    DiagnosticSeverity Severity,
    string Title);

/// <summary>
/// Every rule <see cref="BspValidator"/> knows, in code order.
/// </summary>
public static class BspRuleCatalog
{
    /// <summary>The whole rule set, ordered by <see cref="BspRule.Code"/>.</summary>
    public static ImmutableArray<BspRule> All { get; } =
    [
        new(BspRuleCodes.Ident, DiagnosticSeverity.Error,
            "the file header's ident must be VBSP"),

        new(BspRuleCodes.FileVersion, DiagnosticSeverity.Error,
            "the file version must be between MINBSPVERSION and BSPVERSION, 19..20"),

        new(BspRuleCodes.LumpElementSize, DiagnosticSeverity.Error,
            "a lump's length must be a whole number of its element"),

        new(BspRuleCodes.LumpCap, DiagnosticSeverity.Error,
            "a lump's element count must not exceed its MAX_MAP_* cap"),

        new(BspRuleCodes.RequiredLumpEmpty, DiagnosticSeverity.Error,
            "a lump the collision loader requires must not be empty"),

        new(BspRuleCodes.PakFileLast, DiagnosticSeverity.Error,
            "the PAKFILE lump must be the last lump in the file"),

        new(BspRuleCodes.OcclusionVersion, DiagnosticSeverity.Error,
            "the OCCLUSION lump's version must be 0, 1 or 2"),

        new(BspRuleCodes.LeafsVersion, DiagnosticSeverity.Error,
            "the LEAFS lump's version must be 0 or 1"),

        new(BspRuleCodes.LeafAmbientLegacyPath, DiagnosticSeverity.Warning,
            "leaf ambient lighting is read directly only at lump version 1 with a non-empty index"),

        new(BspRuleCodes.LeafAmbientLegacyCount, DiagnosticSeverity.Error,
            "on the legacy path the ambient lump must hold one light cube per leaf"),

        new(BspRuleCodes.StaticPropVersion, DiagnosticSeverity.Warning,
            "the sprp game lump's version must be at least 4 or every static prop is dropped"),

        new(BspRuleCodes.DetailPropVersion, DiagnosticSeverity.Warning,
            "the dprp game lump's version must be at least 4 or every detail prop is dropped"),

        new(BspRuleCodes.HdrLumpPair, DiagnosticSeverity.Warning,
            "HDR is usable only when LIGHTING_HDR and WORLDLIGHTS_HDR are both non-empty"),

        new(BspRuleCodes.FaceTexInfo, DiagnosticSeverity.Error,
            "a face's texinfo index must be inside the TEXINFO lump"),

        new(BspRuleCodes.LeafFaceSurface, DiagnosticSeverity.Error,
            "a leafface must index a face that exists"),

        new(BspRuleCodes.SurfEdgeCount, DiagnosticSeverity.Error,
            "the surfedge count must be at least 1 and below MAX_MAP_SURFEDGES"),

        new(BspRuleCodes.SurfEdgeEdge, DiagnosticSeverity.Error,
            "a surfedge's magnitude must index an edge that exists"),

        new(BspRuleCodes.EdgeVertex, DiagnosticSeverity.Error,
            "an edge a surfedge names must have endpoints that index vertices that exist"),

        new(BspRuleCodes.NodeChildren, DiagnosticSeverity.Error,
            "a node's children must index a node or a leaf that exists"),

        new(BspRuleCodes.LeafCluster, DiagnosticSeverity.Error,
            "a leaf's cluster must be inside the visibility lump's cluster table"),

        new(BspRuleCodes.BrushSideTexInfo, DiagnosticSeverity.Error,
            "a brush side's texinfo must be inside the TEXINFO lump, or -1"),

        new(BspRuleCodes.ModelHeadNode, DiagnosticSeverity.Error,
            "a model's head node must index a node that exists"),

        new(BspRuleCodes.TexDataStringIndex, DiagnosticSeverity.Error,
            "a texdata's name must resolve through the string table into the string data"),

        new(BspRuleCodes.OverlayFaces, DiagnosticSeverity.Error,
            "an overlay's face count must fit its fixed face array and name faces that exist"),

        new(BspRuleCodes.StaticPropDictIndex, DiagnosticSeverity.Error,
            "a static prop's type must index the model dictionary"),

        new(BspRuleCodes.StaticPropLeafRun, DiagnosticSeverity.Error,
            "a static prop's leaf run must lie inside the leaf list"),

        new(BspRuleCodes.Leaf0Solid, DiagnosticSeverity.Error,
            "leaf 0's contents must be CONTENTS_SOLID"),

        new(BspRuleCodes.SurfaceExtents, DiagnosticSeverity.Error,
            "a lit face's lightmap extents must be within the lightmap limit"),

        new(BspRuleCodes.NoCubemaps, DiagnosticSeverity.Warning,
            "a map should carry at least one cubemap sample"),

        new(BspRuleCodes.TexDataStringNul, DiagnosticSeverity.Error,
            "the texdata string data must end in a NUL"),

        new(BspRuleCodes.PhysFraming, DiagnosticSeverity.Error,
            "the physics lump must frame as dphysmodel_t records ending in a -1 terminator"),

        new(BspRuleCodes.PhysModelIndex, DiagnosticSeverity.Error,
            "a physics record's model index must name a model that exists"),

        new(BspRuleCodes.PlaneIndex, DiagnosticSeverity.Error,
            "every plane reference must be inside the PLANES lump"),

        new(BspRuleCodes.BrushSideRun, DiagnosticSeverity.Error,
            "a brush's side run must lie inside the BRUSHSIDES lump"),

        new(BspRuleCodes.LeafRuns, DiagnosticSeverity.Error,
            "a leaf's leafface and leafbrush runs must lie inside their lumps"),

        new(BspRuleCodes.DispPower, DiagnosticSeverity.Error,
            "a displacement's power must be at most MAX_MAP_DISP_POWER"),

        new(BspRuleCodes.DispRuns, DiagnosticSeverity.Error,
            "the displacement vertex and triangle runs must lie inside their lumps"),

        new(BspRuleCodes.SubLumpFraming, DiagnosticSeverity.Error,
            "a lump built from counted runs must frame exactly within its bytes"),

        new(BspRuleCodes.TooManyCubemaps, DiagnosticSeverity.Warning,
            "a map should carry at most MAX_MAP_CUBEMAPSAMPLES cubemap samples"),
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
