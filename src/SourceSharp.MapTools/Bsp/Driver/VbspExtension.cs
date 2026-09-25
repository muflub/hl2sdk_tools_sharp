using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Write;

namespace SourceSharp.MapTools.Bsp.Driver;

/// <summary>
/// The points in a vbsp compile where another lane's lumps are produced, in
/// the order stock reaches them.
/// </summary>
/// <remarks>
/// <para>
/// The order is stock's and is load-bearing: each point sees exactly the state
/// stock's function saw. Listed with the call site; the lane that owns each is
/// in brackets.
/// </para>
/// <list type="number">
/// <item><see cref="AfterLoad"/> — after <c>WorldVertexTransitionFixup</c>,
/// where <c>Cubemap_FixupBrushSidesMaterials</c>,
/// <c>Cubemap_AttachDefaultCubemapToSpecularSides</c> and
/// <c>Cubemap_AddUnreferencedCubemaps</c> run (<c>vbsp.cpp:1419-1424</c>)
/// [3g]. Also where an extension registers its
/// <see cref="VbspStageContext.OverlayFaces"/> sink and its
/// <see cref="VbspStageContext.TexInfoReferences"/>.</item>
/// <item><see cref="BeforeProcessModels"/> — <c>LoadEmitDetailObjectDictionary</c>
/// (<c>vbsp.cpp:1427</c>) [3g].</item>
/// <item><see cref="InitialDispInfos"/> — <c>EmitInitialDispInfos</c>, after
/// <c>BeginBSPFile</c> and <c>MarkNoDynamicShadowSides</c>, before the occluders
/// (<c>vbsp.cpp:849</c>) [3f].</item>
/// <item><see cref="ModelDisplacementFaces"/> — once per model inside
/// <c>WriteBSP</c>, after the tree and area portals are written and before the
/// water volumes: <c>EmitFaceVertexes</c> + <c>EmitFace</c> for every
/// <c>mapdispinfo</c> of this entity (<c>writebsp.cpp:927-935</c>) [3f].</item>
/// <item><see cref="DefaultCubemaps"/> — <c>Cubemap_CreateDefaultCubemaps</c>,
/// after the model loop (<c>vbsp.cpp:884</c>) [3g].</item>
/// <item><see cref="DispLightmapAlphaAndNeighbors"/> —
/// <c>EmitDispLMAlphaAndNeighbors</c> (<c>writebsp.cpp:1256</c>), after brushes,
/// planes, vertex normals and lightmap extents [3f].</item>
/// <item><see cref="OverlayFaces"/> — <c>Overlay_EmitOverlayFaces</c> and
/// <c>OverlayTransition_EmitOverlayFaces</c> (<c>writebsp.cpp:1259-1260</c>)
/// [3g].</item>
/// <item><see cref="PhysCollision"/> — <c>EmitPhysCollision</c>
/// (<c>writebsp.cpp:1264</c>) [3h]. The leaf water ids it assigns are done by
/// the driver just before, whether or not a cooker is attached.</item>
/// <item><see cref="StaticProps"/> — <c>EmitStaticProps</c>
/// (<c>writebsp.cpp:1272</c>) [3g].</item>
/// <item><see cref="DetailObjects"/> — <c>EmitDetailObjects</c>
/// (<c>writebsp.cpp:1275</c>) [3g].</item>
/// <item><see cref="WriteFile"/> — inside <c>WriteBSPFile</c>, after every lump
/// this lane owns has been placed in <see cref="VbspStageContext.Bsp"/>: game
/// lumps, the pak file, cubemap and overlay lumps [3g], physics lumps [3h],
/// displacement lumps [3f]. Anything placed here replaces the minimal default
/// the driver wrote.</item>
/// </list>
/// </remarks>
internal enum VbspExtensionPoint
{
    AfterLoad,
    BeforeProcessModels,
    InitialDispInfos,
    ModelDisplacementFaces,
    DefaultCubemaps,
    DispLightmapAlphaAndNeighbors,
    OverlayFaces,
    PhysCollision,
    StaticProps,
    DetailObjects,
    WriteFile,
}

/// <summary>A lump producer another lane plugs into the vbsp driver.</summary>
internal interface IVbspExtension
{
    /// <summary>Runs at one point of the compile. Most extensions ignore most points.</summary>
    /// <param name="point">Where the compile is.</param>
    /// <param name="stage">Everything the compile holds at that point.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task.</returns>
    ValueTask RunAsync(VbspExtensionPoint point, VbspStageContext stage, CancellationToken cancellationToken);
}

/// <summary>What an extension can see and change at its point.</summary>
internal sealed class VbspStageContext
{
    internal VbspStageContext(VbspContext compile, MapFile map, BspWriteState state, BspData bsp)
    {
        Compile = compile;
        Map = map;
        State = state;
        Bsp = bsp;
    }

    /// <summary>The compile's context: options, content, tables.</summary>
    internal VbspContext Compile { get; }

    /// <summary>The map.</summary>
    internal MapFile Map { get; }

    /// <summary>The <c>d*</c> arrays emitted so far.</summary>
    internal BspWriteState State { get; }

    /// <summary>The file being assembled; filled in at <see cref="VbspExtensionPoint.WriteFile"/>.</summary>
    internal BspData Bsp { get; }

    /// <summary>The tree writer, for <see cref="VbspExtensionPoint.ModelDisplacementFaces"/>'s <c>EmitFace</c>.</summary>
    internal BspTreeWriter? Writer { get; set; }

    /// <summary>The entity whose model is being written, at <see cref="VbspExtensionPoint.ModelDisplacementFaces"/>.</summary>
    internal int EntityNumber { get; set; }

    /// <summary>The overlay face lists sink <c>EmitFace</c> feeds; set at <see cref="VbspExtensionPoint.AfterLoad"/>.</summary>
    internal IOverlayFaceSink? OverlayFaces { get; set; }

    /// <summary>Texinfo holders <c>CompactTexinfos</c> must count and renumber, in stock's order.</summary>
    internal List<ITexInfoReferences> TexInfoReferences { get; } = [];

    /// <summary>The displacements' bounds, in dispinfo order, for <c>ComputeBoundsNoSkybox</c>.</summary>
    internal List<(Vec3 Mins, Vec3 Maxs)> DisplacementBounds { get; } = [];

    /// <summary><c>$macro_texture</c> lookup for <c>DiscoverMacroTextures</c>; null means none.</summary>
    internal IMacroTextureResolver? MacroTextures { get; set; }

    /// <summary>The patched water materials, for the pak writer.</summary>
    internal WaterVolumeSet Water { get; } = new();

    /// <summary>The displacements, for <see cref="VbspExtensionPoint.PhysCollision"/>'s <c>disp_ivp.cpp</c>.</summary>
    internal DisplacementStage? Displacements { get; set; }

    /// <summary>The patched world-vertex-transition materials, for the pak writer.</summary>
    internal IReadOnlyList<WorldVertexTransitionPatch> WorldVertexTransitionPatches { get; set; } = [];
}
