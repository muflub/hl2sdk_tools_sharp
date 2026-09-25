using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.Collision;

/// <summary>
/// Everything <c>EmitPhysCollision</c> (<c>ivp.cpp:1498</c>) reads, as the
/// finished lumps and tables it reads them from.
/// </summary>
/// <remarks>
/// <para>
/// Stock reads the <c>d*</c> globals of the BSP being written plus three
/// things that are not in any lump: which original brush sides are visible
/// (<c>g_MainMap-&gt;mapbrushes[b].original_sides[i].visible</c>, for the
/// brush-entity shrink, <c>ivp.cpp:505</c>), the surface-property index per
/// texdata (<c>g_SurfaceProperties</c>), and the water volumes
/// <c>EmitWaterVolumesForBSP</c> recorded while the tree still existed
/// (<see cref="WaterVolumeBuilder"/>). Those come in explicitly.
/// </para>
/// <para>
/// CALL POSITION, for the vbsp driver: stock calls <c>EmitPhysCollision</c>
/// from <c>EndBSPFile</c> (<c>writebsp.cpp:1264</c>), after
/// <c>EmitBrushes</c>/<c>EmitPlanes</c> and the displacement neighbour and
/// alpha passes, and before <c>EmitStaticProps</c>. Everything it reads is
/// final by then, and what it writes back -- each leaf's
/// <c>leafWaterDataID</c> and the cleared <c>CONTENTS_TESTFOGVOLUME</c> bit
/// (<see cref="PhysCollisionResult.LeafWaterDataIds"/>,
/// <see cref="PhysCollisionResult.LeafContents"/>) -- must be applied to
/// LUMP_LEAFS before it is written.
/// </para>
/// </remarks>
public sealed record PhysCollisionInput
{
    /// <summary>LUMP_PLANES.</summary>
    public required IReadOnlyList<DPlane> Planes { get; init; }

    /// <summary>LUMP_BRUSHES.</summary>
    public required IReadOnlyList<DBrush> Brushes { get; init; }

    /// <summary>LUMP_BRUSHSIDES.</summary>
    public required IReadOnlyList<DBrushSide> BrushSides { get; init; }

    /// <summary>LUMP_NODES.</summary>
    public required IReadOnlyList<DNode> Nodes { get; init; }

    /// <summary>LUMP_LEAFS (contents and leaf-brush ranges are read).</summary>
    public required IReadOnlyList<DLeaf> Leafs { get; init; }

    /// <summary>LUMP_LEAFBRUSHES.</summary>
    public required IReadOnlyList<ushort> LeafBrushes { get; init; }

    /// <summary>LUMP_MODELS.</summary>
    public required IReadOnlyList<DModel> Models { get; init; }

    /// <summary>LUMP_FACES (texinfo and area are read, for a brush entity's mass).</summary>
    public required IReadOnlyList<DFace> Faces { get; init; }

    /// <summary>LUMP_TEXINFO.</summary>
    public required IReadOnlyList<TexInfo> TexInfos { get; init; }

    /// <summary>
    /// <c>g_SurfaceProperties</c>: a surface-property index per texdata, -1
    /// for none (<see cref="SurfacePropertyTable.ResolveMaterial"/>).
    /// </summary>
    public required IReadOnlyList<int> SurfaceProperties { get; init; }

    /// <summary>The surface-property database the indices point into.</summary>
    public required SurfacePropertyTable SurfaceProps { get; init; }

    /// <summary>
    /// Per brush, per original side, whether <c>MarkVisibleSides</c> found it
    /// visible. Null means "every side is visible", which shrinks every side
    /// of a brush entity; a brush with fewer entries than it has sides treats
    /// the rest as stock's <c>i &gt;= numsides</c> case (shrunk).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<bool>>? SideVisible { get; init; }

    /// <summary>The water volumes <c>EmitWaterVolumesForBSP</c> recorded.</summary>
    public IReadOnlyList<WaterModel> WaterModels { get; init; } = [];

    /// <summary>The displacements, in <c>g_CoreDispInfos</c> order.</summary>
    public IReadOnlyList<CollisionDisplacement> Displacements { get; init; } = [];

    /// <summary><c>-novirtualmesh</c>: displacement terrain as polysoups in the world model.</summary>
    public bool NoVirtualMesh { get; init; }

    /// <summary>
    /// Which stock defects to reproduce. Default correct; a gate against
    /// stock bytes passes <see cref="ComplianceOptions.Stock"/>.
    /// </summary>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>
    /// How many models may be cooked at once (plan 3p); 1, the default, is
    /// stock's one-at-a-time loop. The records are assembled in model order
    /// whatever the degree, and no model's cook reads another's.
    /// </summary>
    public int MaxDegree { get; init; } = 1;
}

/// <summary>
/// One water volume: <c>watermodel_t</c> (<c>ivp.cpp:1091</c>) with its
/// leaves.
/// </summary>
/// <param name="ModelIndex">The brush model it belongs to (only model 0's are emitted).</param>
/// <param name="Contents">The water leaf's contents.</param>
/// <param name="HasSurface">Whether a portal to air gave a surface plane.</param>
/// <param name="SurfaceNormal">That plane's normal (ignored without a surface).</param>
/// <param name="SurfaceDist">That plane's distance (ignored without a surface).</param>
/// <param name="FogVolumeIndex">The <c>dleafwaterdata</c> index each leaf gets.</param>
/// <param name="Leaves">The connected leaves.</param>
/// <param name="SurfaceTexInfo">
/// The water surface's texinfo, or -1; read only when
/// <see cref="StockQuirk.FluidSurfacePropIgnored"/> is corrected.
/// </param>
public sealed record WaterModel(
    int ModelIndex,
    int Contents,
    bool HasSurface,
    Vec3 SurfaceNormal,
    float SurfaceDist,
    int FogVolumeIndex,
    IReadOnlyList<int> Leaves,
    int SurfaceTexInfo = -1);

/// <summary>
/// One displacement as the collision code reads it: <c>g_CoreDispInfos[i]</c>
/// plus the three things <c>disp_ivp.cpp</c> takes from <c>mapdispinfo[i]</c>.
/// </summary>
/// <param name="Core">The built surface, allowed-verts already set up.</param>
/// <param name="Contents"><c>mapdispinfo[i].contents</c>.</param>
/// <param name="TexInfo"><c>mapdispinfo[i].face.texinfo</c>.</param>
/// <param name="SurfaceProp2">
/// The material's <c>$surfaceprop2</c> index (<c>GetSurfaceProperties2</c>,
/// <c>textures.cpp:368</c>), or -1.
/// </param>
public sealed record CollisionDisplacement(CoreDispInfo Core, int Contents, int TexInfo, int SurfaceProp2);

/// <summary>What <see cref="PhysCollisionEmitter"/> produced.</summary>
/// <param name="PhysCollide">LUMP_PHYSCOLLIDE.</param>
/// <param name="PhysDisp">LUMP_PHYSDISP, or null on the <c>-novirtualmesh</c> road (stock writes none).</param>
/// <param name="Models">The per-model records, as written.</param>
/// <param name="LeafWaterDataIds">Every leaf's <c>leafWaterDataID</c> after the pass.</param>
/// <param name="LeafContents">Every leaf's contents after <c>ClearLeafWaterData</c>.</param>
/// <param name="WorldMaterials"><c>s_WorldPropList</c>: the world's per-triangle material table.</param>
public sealed record PhysCollisionResult(
    byte[] PhysCollide,
    byte[]? PhysDisp,
    IReadOnlyList<PhysCollideModel> Models,
    IReadOnlyList<short> LeafWaterDataIds,
    IReadOnlyList<int> LeafContents,
    IReadOnlyList<int> WorldMaterials);
