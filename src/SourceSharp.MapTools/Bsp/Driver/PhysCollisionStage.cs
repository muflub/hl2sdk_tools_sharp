using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Write;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.Driver;

/// <summary>
/// <c>EmitPhysCollision</c> in the vbsp driver: Phase
/// 3h's <see cref="PhysCollisionEmitter"/> over the lumps the write stage has
/// emitted, at <see cref="VbspExtensionPoint.PhysCollision"/>
/// With LUMP_PHYSCOLLIDE and LUMP_PHYSDISP placed
/// at <see cref="VbspExtensionPoint.WriteFile"/>.
/// </summary>
/// <remarks>
/// <para>
/// Attached by <see cref="Vbsp.CompileAsync(MapFile, VbspContext, CancellationToken)"/>
/// when the context carries a <see cref="VbspContext.CollisionCooker"/>.
/// Without one the map gets no collision lumps: stock's
/// <c>"!!! WARNING: Can't build collision data!"</c> road.
/// </para>
/// <para>
/// The emitter reads three things that are not in any lump, and they come from
/// the compile: which original brush sides <c>MarkVisibleSides</c> found
/// visible (<see cref="MapBrushSide.Visible"/>), the water volumes
/// <c>EmitWaterVolumesForBSP</c> recorded (<see cref="WaterVolumeSet.Models"/>),
/// and the displacements' built surfaces (<see cref="DisplacementStage"/>).
/// </para>
/// </remarks>
internal sealed class PhysCollisionStage : IVbspExtension
{
    private readonly ICollisionCooker _cooker;
    private readonly ICollisionModelCache? _cache;
    private PhysCollisionResult? _result;

    /// <summary>Creates the stage over the host's one cooker and optional collision cache.</summary>
    /// <param name="cooker">The collision cooker.</param>
    /// <param name="cache">The per-model cooked-collision cache, or null.</param>
    internal PhysCollisionStage(ICollisionCooker cooker, ICollisionModelCache? cache = null)
    {
        _cooker = cooker;
        _cache = cache;
    }

    /// <summary>What the emitter produced, once the point has run.</summary>
    internal PhysCollisionResult? Result => _result;

    /// <inheritdoc/>
    public async ValueTask RunAsync(VbspExtensionPoint point, VbspStageContext stage, CancellationToken cancellationToken)
    {
        switch (point)
        {
            case VbspExtensionPoint.PhysCollision:
                await EmitAsync(stage, cancellationToken).ConfigureAwait(false);
                break;

            case VbspExtensionPoint.WriteFile when _result is { } result:
                stage.Bsp.SetLump(BspLump.PhysCollide, result.PhysCollide);

                // The -novirtualmesh road writes no LUMP_PHYSDISP at all
                // (g_pPhysDisp stays NULL).
                if (result.PhysDisp is { } physDisp)
                {
                    stage.Bsp.SetLump(BspLump.PhysDisp, physDisp);
                }

                break;
        }
    }

    private async Task EmitAsync(VbspStageContext stage, CancellationToken cancellationToken)
    {
        VbspContext compile = stage.Compile;
        BspWriteState state = stage.State;

        PhysCollisionInput input = new()
        {
            Planes = state.Planes,
            Brushes = state.Brushes,
            BrushSides = state.BrushSides,
            Nodes = state.Nodes,
            Leafs = state.Leafs,
            LeafBrushes = state.LeafBrushes,
            Models = state.Models,
            Faces = state.DrawFaces,
            TexInfos = compile.TexInfos.TexInfos,
            SurfaceProperties = compile.TexDatas.SurfaceProperties,
            SurfaceProps = compile.TexDatas.PropertyTable ?? new SurfacePropertyTable(),
            SideVisible = SideVisibility(stage.Map),
            WaterModels = stage.Water.Models,
            Displacements = await DisplacementsAsync(stage, cancellationToken).ConfigureAwait(false),
            NoVirtualMesh = compile.Options.NoVirtualMesh,
            Compliance = compile.Options.Compliance,
            MaxDegree = compile.Parallelism.MaxDegree,
        };

        _result = await PhysCollisionEmitter.EmitAsync(input, _cooker, _cache, cancellationToken).ConfigureAwait(false);

        // What EmitPhysCollision writes back into dleafs: ClearLeafWaterData's
        // cleared CONTENTS_TESTFOGVOLUME and each water leaf's id.
        for (int i = 0; i < state.Leafs.Count; i++)
        {
            DLeaf leaf = state.Leafs[i];
            leaf.LeafWaterDataId = _result.LeafWaterDataIds[i];
            leaf.Contents = _result.LeafContents[i];
            state.Leafs[i] = leaf;
        }
    }

    /// <summary>
    /// <c>g_MainMap-&gt;mapbrushes[b].original_sides[i].visible</c>, per brush
    /// in LUMP_BRUSHES order (<c>EmitBrushes</c> writes every map brush,
    ///); a brush's axial box sides past its own
    /// count are stock's <c>i &gt;= numsides</c> case.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <returns>The flags.</returns>
    internal static IReadOnlyList<IReadOnlyList<bool>> SideVisibility(MapFile map)
    {
        List<IReadOnlyList<bool>> all = new(map.Brushes.Count);
        foreach (MapBrush brush in map.Brushes)
        {
            bool[] visible = new bool[brush.SideCount];
            for (int i = 0; i < visible.Length; i++)
            {
                visible[i] = map.BrushSides[brush.FirstSide + i].Visible;
            }

            all.Add(visible);
        }

        return all;
    }

    // g_CoreDispInfos with mapdispinfo's contents and texinfo, and each
    // Texdata's $surfaceprop2(GetSurfaceProperties2).
    private static async Task<IReadOnlyList<CollisionDisplacement>> DisplacementsAsync(
        VbspStageContext stage, CancellationToken cancellationToken)
    {
        if (stage.Displacements is not { Count: > 0 } displacements)
        {
            return [];
        }

        VbspContext compile = stage.Compile;
        SurfacePropertyTable? table = compile.TexDatas.PropertyTable;
        Dictionary<int, int> prop2OfTexData = [];
        List<CollisionDisplacement> result = [];

        foreach ((Disp.CoreDispInfo core, int contents, int faceIndex) in displacements.CollisionSources())
        {
            // mapdispinfo[i].face.texinfo: the base face's, as it was emitted.
            int texInfo = stage.State.DrawFaces[faceIndex].TexInfo;
            int texData = compile.TexInfos[texInfo].TexData;

            if (!prop2OfTexData.TryGetValue(texData, out int prop2))
            {
                prop2 = -1;
                if (table is not null && texData >= 0)
                {
                    MaterialFacts facts = await compile.Materials
                        .GetAsync(compile.TexDatas.NameOf(texData), cancellationToken).ConfigureAwait(false);
                    prop2 = facts.Found ? table.ResolveMaterial2(facts.SurfaceProp2) : -1;
                }

                prop2OfTexData[texData] = prop2;
            }

            result.Add(new CollisionDisplacement(core, contents, texInfo, prop2));
        }

        return result;
    }
}
