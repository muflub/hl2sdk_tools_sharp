//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Bsp.Write;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;

using BlockGrid = SourceSharp.MapTools.Bsp.Tree.BlockGrid;
using BrushBspTree = SourceSharp.MapTools.Bsp.Tree.BrushBspTree;
using BspTreeParallelism = SourceSharp.MapTools.Bsp.Tree.BspTreeParallelism;
using TreeNode = SourceSharp.MapTools.Bsp.Tree.BspNode;
using TreeOperations = SourceSharp.MapTools.Bsp.Tree.TreeOperations;
using TreeTree = SourceSharp.MapTools.Bsp.Tree.BspTree;

namespace SourceSharp.MapTools.Bsp.Driver;

/// <summary>
/// One full vbsp compile: <c>RunVBSP</c>'s "start from scratch" branch from
/// <c>WorldVertexTransitionFixup</c> on,
/// <c>ProcessModels</c> and <c>EndBSPFile</c>
/// </summary>
internal sealed class VbspCompilation
{
    private readonly MapFile _map;
    private readonly VbspContext _compile;
    private readonly IReadOnlyList<IVbspExtension> _extensions;
    private readonly BspData _bsp = new();

    private FaceBuildContext _faces = null!;
    private BspWriteState _state = null!;
    private BspBuildContext _build = null!;
    private BspTreeWriter _writer = null!;
    private OccluderEmitter _occluders = null!;
    private VbspStageContext _stage = null!;
    private WaterVolumes _water = null!;
    private DisplacementStage _displacements = null!;

    private int[] _surfaceProperties = [];
    private PortalFile? _portals;
    private LeakReport? _leak;
    private bool _stopped;

    internal VbspCompilation(MapFile map, VbspContext compile, IReadOnlyList<IVbspExtension> extensions)
    {
        _map = map;
        _compile = compile;
        _extensions = extensions;
    }

    private VbspOptions Options => _compile.Options;

    private ComplianceOptions Compliance => _compile.Options.Compliance;

    /// <summary>The compile's CSG and tree state, once the load phase has made it.</summary>
    internal BspBuildContext? Build => _build;

    internal async Task<VbspResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The build's pooled brush side arrays go when the compile does,
            // however it ended: a failed or cancelled compile must not leave
            // them in a context the caller might still reference.
            _build?.ReleaseBrushSidePool();

            // And so must the arenas the tree build's forks handed back.
            _build?.ReleaseWindingArenaPool();

            // As must the threads, if the compile made its own.
            _ownPool?.Dispose();
            _ownPool = null;
        }
    }

    // The compile's own threads, when the host lent neither a pool nor a
    // scheduler and asked for more than one: what a WorkQueue with no pool
    // makes for itself, made once here so that the serial model queue and
    // the tree build's helpers share one set. Its threads start with the
    // first job, so a compile that never forks starts none of them.
    private CompilePool? _ownPool;

    /// <summary>The pool this compile made for itself, while it has one.</summary>
    internal CompilePool? OwnPool => _ownPool;

    /// <summary>
    /// How the tree builds may fork, from the compile's parallelism: never
    /// at one thread, otherwise on the lent pool, the lent scheduler, or the
    /// compile's own pool, in that order. Always carries the token, so even
    /// a serial tree build stops at the next node when the compile is
    /// cancelled.
    /// </summary>
    internal BspTreeParallelism TreeParallelism(CancellationToken cancellationToken)
    {
        CompileParallelism parallelism = _compile.Parallelism;
        int degree = parallelism.Pool is { } pool
            ? Math.Min(parallelism.MaxDegree, pool.Degree)
            : parallelism.MaxDegree;

        TaskScheduler? scheduler = null;
        if (degree > 1)
        {
            scheduler = parallelism.Pool?.Scheduler ?? parallelism.Scheduler;
            if (scheduler is null)
            {
                _ownPool ??= new CompilePool(degree);
                scheduler = _ownPool.Scheduler;
            }
        }

        return new BspTreeParallelism
        {
            Scheduler = scheduler,
            MaxForkDepth = scheduler is null ? 0 : BspTreeParallelism.ForkDepthFor(degree),
            MaxDegree = Math.Max(1, degree),
            MinBrushes = _compile.TreeForkMinBrushes,
            CancellationToken = cancellationToken,
        };
    }

    private async Task<VbspResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        // ---- the async load phase: everything that reads content ----------

        _compile.MainMap ??= _map;

        Stage("vbsp.fixups");

        IReadOnlyList<WorldVertexTransitionPatch> wvt = await WorldVertexTransitionFixup
            .RunAsync(_compile, _map, cancellationToken).ConfigureAwait(false);

        // Read AFTER the post-load fixups, below: AssignBottomWaterMaterialToFace
        // reads a water's $bottommaterial from the cubemap-PATCHED material,
        // "This happens *after* cubemap fixup".
        FaceMaterialFacts materials = FaceMaterialFacts.Create(_compile);

        _faces = new FaceBuildContext(_compile.Windings, _map.Planes, _compile.TexInfos, Options)
        {
            Compliance = Compliance,
            Diagnostics = _compile.Diagnostics,
            Materials = materials,
        };

        _state = new BspWriteState(_faces);
        _build = new BspBuildContext(_compile, _map);
        _stage = new VbspStageContext(_compile, _map, _state, _bsp) { WorldVertexTransitionPatches = wvt };
        _water = new WaterVolumes(_state, _stage.Water, _compile, _map.Planes);
        _displacements = new DisplacementStage(_map);
        _stage.Displacements = _displacements;

        await RunExtensionsAsync(VbspExtensionPoint.AfterLoad, cancellationToken).ConfigureAwait(false);

        Stage("vbsp.materials");
        await materials.LoadAsync(cancellationToken).ConfigureAwait(false);

        EntityStage.SetModelNumbers(_map);
        EntityStage.SetLightStyles(_map);

        await RunExtensionsAsync(VbspExtensionPoint.BeforeProcessModels, cancellationToken).ConfigureAwait(false);

        // ---- ProcessModels ----------------------------------------------

        // Every tree this compile builds may fork its subtrees onto other
        // threads; in practice only the world's blocks are large enough to.
        _build.TreeParallelism = TreeParallelism(cancellationToken);

        // Serial, but on the compile's shared pool when it has one, or on its
        // own when it made one for the tree build.
        using WorkQueue queue = new(
            CompileParallelism.Serial with { Pool = _compile.Parallelism.Pool ?? _ownPool });

        Stage("vbsp.begin");
        await OnWorkerAsync(queue, BeginProcessModels, Vbsp.ModelsStage, cancellationToken).ConfigureAwait(false);
        await RunExtensionsAsync(VbspExtensionPoint.InitialDispInfos, cancellationToken).ConfigureAwait(false);
        Stage("vbsp.occluders");
        await OnWorkerAsync(queue, _occluders.Emit, Vbsp.ModelsStage, cancellationToken).ConfigureAwait(false);

        for (int entityNumber = 0; entityNumber < _map.Entities.Count; ++entityNumber)
        {
            if (_map.Entities[entityNumber].BrushCount == 0)
            {
                continue;
            }

            int modelIndex = _state.Models.Count;
            TreeNode? head = null;
            int number = entityNumber;

            await OnWorkerAsync(
                queue,
                () => head = ProcessModelTree(number, modelIndex),
                Vbsp.ModelsStage,
                cancellationToken).ConfigureAwait(false);

            if (_stopped)
            {
                // -leaktest: "--- MAP LEAKED ---", exit(0) with nothing written.
                return new VbspResult(null, null, _leak, _compile.Diagnostics);
            }

            // The tail of WriteBSP: displacement faces, then the water volumes.
            Stage("vbsp.dispfaces");
            _stage.Writer = _writer;
            _stage.EntityNumber = entityNumber;
            await OnWorkerAsync(
                queue,
                () => _displacements.EmitFaces(number, _faces, _writer),
                Vbsp.ModelsStage,
                cancellationToken).ConfigureAwait(false);
            await RunExtensionsAsync(VbspExtensionPoint.ModelDisplacementFaces, cancellationToken)
                .ConfigureAwait(false);
            Stage("vbsp.water");
            await _water.EmitAsync(modelIndex, head!, cancellationToken).ConfigureAwait(false);

            await OnWorkerAsync(queue, () => FinishModel(number, head!), Vbsp.ModelsStage, cancellationToken)
                .ConfigureAwait(false);
        }

        // Turn the skybox into a cubemap in case we don't build env_cubemap textures.
        await RunExtensionsAsync(VbspExtensionPoint.DefaultCubemaps, cancellationToken).ConfigureAwait(false);

        // ---- EndBSPFile ---------------------------------------------------

        Stage("vbsp.end.geometry");
        await OnWorkerAsync(queue, EndBspFileGeometry, Vbsp.EndStage, cancellationToken).ConfigureAwait(false);
        await RunExtensionsAsync(VbspExtensionPoint.DispLightmapAlphaAndNeighbors, cancellationToken).ConfigureAwait(false);
        await RunExtensionsAsync(VbspExtensionPoint.OverlayFaces, cancellationToken).ConfigureAwait(false);

        // EmitPhysCollision's ClearLeafWaterData and water leaf ids.
        Stage("vbsp.end.leafwater");
        _water.AssignLeafWaterData();
        await RunExtensionsAsync(VbspExtensionPoint.PhysCollision, cancellationToken).ConfigureAwait(false);

        // ClearDistToClosestWater is the zero lump the assembler writes.
        await RunExtensionsAsync(VbspExtensionPoint.StaticProps, cancellationToken).ConfigureAwait(false);
        await RunExtensionsAsync(VbspExtensionPoint.DetailObjects, cancellationToken).ConfigureAwait(false);

        // g_SurfaceProperties: resolved as each texdata was created
        // (TexDataTable.PropertyTable), including those the face and water
        // stages created.
        _surfaceProperties = [.. _compile.TexDatas.SurfaceProperties];

        Stage("vbsp.end.tables");
        await OnWorkerAsync(queue, EndBspFileTables, Vbsp.EndStage, cancellationToken).ConfigureAwait(false);

        Stage("vbsp.end.assemble");
        BspAssembler.Assemble(_state, _bsp, _compile.MapRevision);
        _displacements.WriteLumps(_bsp);
        await RunExtensionsAsync(VbspExtensionPoint.WriteFile, cancellationToken).ConfigureAwait(false);

        Stage("vbsp.done");
        return new VbspResult(_bsp, _portals, _leak, _compile.Diagnostics);
    }

    // One report per stage transition, to the host's clock (VbspContext.Progress).
    private void Stage(string name) => _compile.Progress?.Report(new CompileProgress(name, 0, 0));

    // ProcessModels' prologue: BeginBSPFile, MarkNoDynamicShadowSides.
    private void BeginProcessModels()
    {
        _state.BeginBspFile();
        _map.MarkNoDynamicShadowSides();

        _writer = new BspTreeWriter(_state, _map, _compile.TexInfos)
        {
            Diagnostics = _compile.Diagnostics,
            Overlays = _stage.OverlayFaces,
        };

        _occluders = new OccluderEmitter(_state, _map, _build, _compile);
    }

    // BeginModel + ProcessWorldModel/ProcessSubModel as far as the head of
    // WriteBSP (EmitDrawNode_r and, for the world, EmitAreaPortals).
    private TreeNode? ProcessModelTree(int entityNumber, int modelIndex)
    {
        Stage(entityNumber == 0 ? "vbsp.world.tree" : "vbsp.submodels");
        BeginModel(entityNumber);

        return entityNumber == 0
            ? ProcessWorldModel()
            : ProcessSubModel(entityNumber);
    }

    /// <summary><c>BeginModel</c>.</summary>
    private void BeginModel(int entityNumber)
    {
        if (_state.Models.Count == WriteLimits.MaxMapModels)
        {
            throw new MapCompileException(WriteCodes.LimitExceeded, $"Too many brush models in map, max = {WriteLimits.MaxMapModels}");
        }

        _state.FirstModelEdge = _state.Edges.Count;

        // bound the brushes
        MapEntity e = _map.Entities[entityNumber];
        float minX = 99999f, minY = 99999f, minZ = 99999f;
        float maxX = -99999f, maxY = -99999f, maxZ = -99999f;

        for (int j = e.FirstBrush; j < e.FirstBrush + e.BrushCount; j++)
        {
            MapBrush b = _map.Brushes[j];
            if (b.SideCount == 0)
            {
                continue; // not a real brush (origin brush)
            }

            foreach (Vec3 v in (ReadOnlySpan<Vec3>)[b.Mins, b.Maxs])
            {
                if (v.X < minX) { minX = v.X; }
                if (v.X > maxX) { maxX = v.X; }
                if (v.Y < minY) { minY = v.Y; }
                if (v.Y > maxY) { maxY = v.Y; }
                if (v.Z < minZ) { minZ = v.Z; }
                if (v.Z > maxZ) { maxZ = v.Z; }
            }
        }

        _state.Models.Add(new DModel
        {
            Mins = new Vec3(minX, minY, minZ),
            Maxs = new Vec3(maxX, maxY, maxZ),
            FirstFace = _state.DrawFaces.Count,
        });
    }

    /// <summary><c>ProcessWorldModel</c> up to the head of <c>WriteBSP</c>.</summary>
    private TreeNode? ProcessWorldModel()
    {
        MapEntity e = _map.Entities[0];
        int brushStart = e.FirstBrush;
        int brushEnd = brushStart + e.BrushCount;
        _build.BrushStart = brushStart;
        _build.BrushEnd = brushEnd;

        BspBlockGrid grid = BlockGrid.Clamp(Options.Blocks, _map.Mins, _map.Maxs);

        TreeTree tree = null!;
        TreePortals portals = null!;
        bool leaked = false;

        for (int optimize = 0; optimize <= 1; optimize++)
        {
            tree = BlockGrid.BuildWorldPass(_build, grid, _map.Mins, _map.Maxs, out _);

            // make the portals/faces by traversing down to each empty leaf
            Stage("vbsp.world.portals");
            portals = new TreePortals(_compile.Windings, _map.Planes);
            portals.MakeTreePortals(tree);

            Stage("vbsp.world.flood");
            FloodResult flood = EntityFlood.FloodEntities(tree, _map.Planes, _map.Entities);
            if (flood.Sealed)
            {
                // turns everthing outside into solid
                EntityFlood.FillOutside(tree.HeadNode!);
            }
            else
            {
                leaked = true;
                _leak = LeakTrace.Trace(tree, _compile.Windings, _map.Entities);
                _compile.Diagnostics.Add(_leak is null
                    ? new CompileDiagnostic(WriteCodes.Leaked, DiagnosticSeverity.Warning, "**** leaked ****")
                    : LeakTrace.Diagnose(_leak));

                if (Options.LeakTest)
                {
                    _stopped = true;
                    return null;
                }
            }

            // mark the brush sides that actually turned into faces
            Stage("vbsp.world.visiblesides");
            VisibleSides sides = new(_compile.Windings, _map.Planes, _map.BrushSides)
            {
                Diagnostics = _compile.Diagnostics,
            };
            sides.MarkVisibleSides(tree, _map.Brushes, brushStart, brushEnd, DetailScreen.NoDetail);

            if (Options.NoOpt || leaked)
            {
                break;
            }

            // If we are optimizing, free the tree. Next time we will construct
            // it again, but we'll use the information in MarkVisibleSides() so
            // we'll only split with planes that actually contribute renderable
            // geometry.
            if (optimize == 0)
            {
                TreeOperations.FreeTreeBrushes(_build, tree.HeadNode!);
            }

            Stage("vbsp.world.tree");
        }

        TreeNode head = tree.HeadNode!;

        Stage("vbsp.world.areas");
        AreaFlood areas = new(_map.Entities) { Diagnostics = _compile.Diagnostics };
        areas.FloodAreas(tree, _compile.Windings);
        if (_leak is null && areas.AreaportalLeak is not null)
        {
            _leak = areas.AreaportalLeak;
        }

        BrushBspTree.RemoveAreaPortalBrushes(head);

        // this turns portals with one solid side into faces; it also
        // subdivides each face if necessary to fit max lightmap dimensions
        Stage("vbsp.world.faces");
        _faces.EntityNumber = 0;
        new FaceBuilder(_faces).MakeFaces(head);

        _occluders.AssignAreas(head, _compile.Diagnostics);
        Compute3DSkyboxAreas(head);

        Stage("vbsp.world.detail");
        Face? leafFaceList = null;
        if (!Options.NoDetail)
        {
            leafFaceList = new DetailFaces(_faces, _build, _map.BrushSides)
                .MergeDetailTree(head, brushStart, brushEnd, _map.Mins, _map.Maxs);
        }

        // This unifies the vertex list for all edges (splits collinear edges
        // to remove t-junctions). It also welds the list of vertices out of
        // each winding/portal and rounds nearly integer verts to integer.
        Stage("vbsp.world.tjuncs");
        leafFaceList = new TJunctionFixer(_faces).FixTjuncs(head, leafFaceList);

        // this merges all of the solid nodes that have separating planes
        if (!Options.NoPrune)
        {
            TreeOperations.PruneNodes(_build, head);
        }

        Stage("vbsp.world.write");
        WriteModelTree(head, leafFaceList, isWorld: true);

        // Only emit area portals for the main world.
        Stage("vbsp.world.areaportals");
        AreaPortalEmitter.Emit(_state, head, _map, areas, _compile.Windings, Compliance, _compile.Diagnostics);

        _worldTree = tree;
        _worldPortals = portals;
        _worldLeaked = leaked;
        return head;
    }

    private TreeTree? _worldTree;
    private TreePortals? _worldPortals;
    private bool _worldLeaked;

    /// <summary><c>ProcessSubModel</c> up to the head of <c>WriteBSP</c>.</summary>
    private TreeNode ProcessSubModel(int entityNumber)
    {
        MapEntity e = _map.Entities[entityNumber];
        int start = e.FirstBrush;
        int end = start + e.BrushCount;
        _build.BrushStart = start;
        _build.BrushEnd = end;

        Vec3 mins = new(WriteConstants.MinCoordInteger, WriteConstants.MinCoordInteger, WriteConstants.MinCoordInteger);
        Vec3 maxs = new(WriteConstants.MaxCoordInteger, WriteConstants.MaxCoordInteger, WriteConstants.MaxCoordInteger);

        BspBrush? list = BrushCsg.MakeBspBrushList(_build, start, end, mins, maxs, DetailScreen.FullDetail);
        if (!Options.NoCsg)
        {
            list = BrushCsg.ChopBrushes(_build, list);
        }

        TreeTree tree = BrushBspTree.BrushBsp(_build, list, mins, maxs);

        // This would wind up crashing the engine because we'd have a negative
        // leaf index in dmodel_t::headnode.
        if (tree.HeadNode!.IsLeaf)
        {
            throw new MapCompileException(WriteCodes.ModelHasNoHeadNode,
                $"bmodel {entityNumber} has no head node (class '{e.ValueForKey("classname")}', "
                + $"targetname '{e.ValueForKey("targetname")}')");
        }

        new TreePortals(_compile.Windings, _map.Planes).MakeTreePortals(tree);

        VisibleSides sides = new(_compile.Windings, _map.Planes, _map.BrushSides)
        {
            Diagnostics = _compile.Diagnostics,
        };
        sides.MarkVisibleSides(tree, _map.Brushes, start, end, DetailScreen.FullDetail);

        _faces.EntityNumber = entityNumber;
        new FaceBuilder(_faces).MakeFaces(tree.HeadNode);

        // nDetailScreen is FULL_DETAIL, so no detail merge.
        new TJunctionFixer(_faces).FixTjuncs(tree.HeadNode, null);
        WriteModelTree(tree.HeadNode, null, isWorld: false);

        return tree.HeadNode;
    }

    // The head of WriteBSP: emit the leaf faces and the tree.
    private void WriteModelTree(TreeNode head, Face? leafFaceList, bool isWorld)
    {
        int headNode = _writer.WriteTree(head, leafFaceList, isWorld);
        DModel model = _state.Models[^1];
        model.HeadNode = headNode;
        _state.Models[^1] = model;
    }

    // After WriteBSP: the portal file (world only, if sealed) and EndModel.
    private void FinishModel(int entityNumber, TreeNode head)
    {
        Stage(entityNumber == 0 ? "vbsp.world.prtfile" : "vbsp.submodels");
        if (entityNumber == 0)
        {
            if (!_worldLeaked)
            {
                WritePortalFile();
            }

            _compile.PortalFileReady?.Invoke(_portals);
        }

        // EndModel
        DModel model = _state.Models[^1];
        model.NumFaces = _state.DrawFaces.Count - model.FirstFace;
        _state.Models[^1] = model;
        _ = head;
    }

    /// <summary>
    /// <c>WritePortalFile</c>: re-portalise the
    /// finished world tree for vis and number the clusters into the leaves.
    /// </summary>
    private void WritePortalFile()
    {
        // The carving is this compile's work, so it is counted in this
        // compile's build context rather than the one the loader made.
        _map.VisClusters.Context = _build;

        PortalFileBuilder builder = new(_worldPortals!, _compile.Windings)
        {
            VisClusters = _map.VisClusters,
            SkyAreas = [.. _state.SkyAreas],
            SkyVis = Options.ForceSkyVis,
        };

        _portals = builder.Build(_worldTree!);

        // we need to store the clusters out now because ordering issues made
        // us do this after writebsp... (clusterleaf = 1)
        IReadOnlyList<int> clusters = builder.LastResult.LeafClusters;
        for (int i = 0; i < clusters.Count; i++)
        {
            DLeaf leaf = _state.Leafs[1 + i];
            leaf.Cluster = (short)clusters[i];
            _state.Leafs[1 + i] = leaf;
        }
    }

    /// <summary><c>Compute3DSkyboxAreas</c>.</summary>
    private void Compute3DSkyboxAreas(TreeNode head)
    {
        foreach (MapEntity e in _map.Entities)
        {
            if (!string.Equals(e.ValueForKey("classname"), "sky_camera", StringComparison.Ordinal))
            {
                continue;
            }

            // Found a 3D skybox camera, get a leaf that lies in it
            TreeNode leaf = BrushBspTree.PointInLeaf(_build, head, e.Origin);
            if ((leaf.Contents & (int)BrushContents.Solid) != 0)
            {
                throw new MapCompileException(WriteCodes.SkyCameraInSolid,
                    $"Error! Entity sky_camera in solid volume! at {e.Origin.X:F1} {e.Origin.Y:F1} {e.Origin.Z:F1}");
            }

            _state.SkyAreas.Add(leaf.Area);
        }
    }

    // EndBSPFile up to EmitDispLMAlphaAndNeighbors.
    private void EndBspFileGeometry()
    {
        BrushLumps.EmitBrushes(_map, _state);
        BrushLumps.EmitPlanes(_map, _state);

        // stick flat normals at the verts
        Stage("vbsp.end.normals");
        FaceLumpStages.SaveVertexNormals(_state);
        Stage("vbsp.end.displacements");

        // EmitDispLMAlphaAndNeighbors (Phase 3f's builder), the base faces'
        // texinfos and the boxes ComputeBoundsNoSkybox adds -- ahead of the
        // extents, in p3f2's order (see DisplacementStage.Build).
        _displacements.Build(_compile, _state, _stage.DisplacementBounds);

        // Figure out lightmap extents for all faces.
        Stage("vbsp.end.extents");
        FaceLumpStages.UpdateAllFaceLightmapExtents(_state, _compile.TexInfos, MaterialNameOf);
    }

    // EndBSPFile from ComputeBoundsNoSkybox to DiscoverMacroTextures.
    private void EndBspFileTables()
    {
        // Compute bounds after creating disp info because we need to reference it
        EntityStage.ComputeBoundsNoSkybox(
            _map, WorldLumps.From(_state, _compile.TexInfos), _stage.DisplacementBounds);

        // Make sure that we have a water lod control eneity if we have water in the map.
        EntityStage.EnsurePresenceOfWaterLodControlEntity(
            _map, _compile.TextureReferences.HasWater, _compile.Diagnostics);

        // Doing this here because stuff about may filter out entities
        _state.EntityData = EntityStage.Unparse(_map).Data.ToArray();

        // remove unused texinfos
        TexInfoCompactor.Compact(
            _state, _compile.TexInfos, _compile.TexDatas, _surfaceProperties, _stage.TexInfoReferences);

        // Figure out which faces want macro textures.
        FaceLumpStages.DiscoverMacroTextures(_state, _stage.MacroTextures);
    }

    // Literal names, so a clock can compare them by reference.
    private static string ExtensionStage(VbspExtensionPoint point) => point switch
    {
        VbspExtensionPoint.AfterLoad => "vbsp.ext.AfterLoad",
        VbspExtensionPoint.BeforeProcessModels => "vbsp.ext.BeforeProcessModels",
        VbspExtensionPoint.InitialDispInfos => "vbsp.ext.InitialDispInfos",
        VbspExtensionPoint.ModelDisplacementFaces => "vbsp.ext.ModelDisplacementFaces",
        VbspExtensionPoint.DefaultCubemaps => "vbsp.ext.DefaultCubemaps",
        VbspExtensionPoint.DispLightmapAlphaAndNeighbors => "vbsp.ext.DispLightmapAlphaAndNeighbors",
        VbspExtensionPoint.OverlayFaces => "vbsp.ext.OverlayFaces",
        VbspExtensionPoint.PhysCollision => "vbsp.ext.PhysCollision",
        VbspExtensionPoint.StaticProps => "vbsp.ext.StaticProps",
        VbspExtensionPoint.DetailObjects => "vbsp.ext.DetailObjects",
        VbspExtensionPoint.WriteFile => "vbsp.ext.WriteFile",
        _ => "vbsp.ext.other",
    };

    private string MaterialNameOf(int texInfo)
    {
        int texData = _compile.TexInfos[texInfo].TexData;
        return texData < 0 ? string.Empty : _compile.TexDatas.NameOf(texData);
    }

    private async ValueTask RunExtensionsAsync(VbspExtensionPoint point, CancellationToken cancellationToken)
    {
        if (_compile.Progress is not null && _extensions.Count > 0)
        {
            Stage(ExtensionStage(point));
        }

        foreach (IVbspExtension extension in _extensions)
        {
            await extension.RunAsync(point, _stage, cancellationToken).ConfigureAwait(false);
        }
    }

    // Runs one serial section on the compile's dedicated worker: never on the
    // caller's thread, never on the host's thread pool (plan 1a).
    private static Task OnWorkerAsync(WorkQueue queue, Action body, string stage, CancellationToken cancellationToken) =>
        queue.RunAsync(1, (_, _) => body(), new WorkQueueOptions { Stage = stage }, cancellationToken);
}
