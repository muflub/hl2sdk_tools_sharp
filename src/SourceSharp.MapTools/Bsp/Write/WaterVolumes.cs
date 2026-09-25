using System.Globalization;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;

using TreeNode = SourceSharp.MapTools.Bsp.Tree.BspNode;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>A water material vbsp must write into the pak: <c>EmitWaterMaterialFile</c>.</summary>
/// <param name="MaterialName">The lowercased base material.</param>
/// <param name="PatchedName">The patched material's name, <c>maps/&lt;map&gt;/&lt;material&gt;_depth_&lt;n&gt;</c>.</param>
/// <param name="WaterDepth">The <c>$waterdepth</c> value inserted.</param>
internal readonly record struct WaterMaterialPatch(string MaterialName, string PatchedName, int WaterDepth);

/// <summary>
/// The water state that outlives one model: <c>g_WaterModels</c> and
/// <c>g_WaterLeafList</c> (as <see cref="Models"/>, each with its leaves), and
/// <c>g_WaterTexInfos</c>.
/// </summary>
internal sealed class WaterVolumeSet
{
    /// <summary>The water volumes <c>EmitWaterVolumesForBSP</c> recorded, for <c>EmitPhysCollision</c>.</summary>
    internal IReadOnlyList<WaterModel> Models { get; set; } = [];

    /// <summary>Full name to texinfo, <c>g_WaterTexInfos</c>.</summary>
    internal Dictionary<string, int> TexInfos { get; } = new(StringComparer.Ordinal);

    /// <summary>The patched materials, in creation order, for Phase 3g's pak writer.</summary>
    internal List<WaterMaterialPatch> Patches { get; } = [];
}

/// <summary>
/// <c>EmitWaterVolumesForBSP</c> and <c>WriteFogVolumeIDs</c>
/// (<c>ivp.cpp:875-1161</c>) as the write stage drives them: the volumes
/// themselves are Phase 3h's <see cref="WaterVolumeBuilder"/>; this class is
/// its face and texture stages -- the per-depth water texinfo
/// (<see cref="IWaterTexInfoSink"/>), LUMP_LEAFWATERDATA, every warped face's
/// fog volume, and the leaf water ids.
/// </summary>
/// <remarks>
/// <para>
/// This lives in stock's <c>ivp.cpp</c> but writes nothing physical; it is
/// called from <c>WriteBSP</c> (<c>writebsp.cpp:937</c>) while the model's tree
/// and portals exist, and its outputs are ordinary lumps. The collision pass
/// later reads <see cref="WaterVolumeSet.Models"/>.
/// </para>
/// <para>
/// <b>In stock, <c>WriteFogVolumeIDs</c> writes nothing.</b> It runs inside
/// <c>WriteBSP</c> and loops to <c>firstface + numfaces</c> while the model's
/// <c>numfaces</c> is still 0 (<see cref="Options.StockQuirk.FogVolumeLoopOverNoFaces"/>).
/// Its body would also have read leaf water ids before
/// <c>EmitPhysCollision</c> assigns them and planes before <c>EmitPlanes</c>
/// writes them; the Correct side uses the values those would have been.
/// </para>
/// </remarks>
internal sealed class WaterVolumes
{
    private readonly BspWriteState _state;
    private readonly WaterVolumeSet _set;
    private readonly VbspContext _compile;
    private readonly PlaneTable _planes;
    private readonly WaterVolumeBuilder _builder;
    private readonly DeferredWaterTexInfos _texInfos = new();

    internal WaterVolumes(BspWriteState state, WaterVolumeSet set, VbspContext compile, PlaneTable planes)
    {
        _state = state;
        _set = set;
        _compile = compile;
        _planes = planes;
        _builder = new WaterVolumeBuilder(
            planes.Planes,
            state.Faces.Windings,
            node => node is TreeNode leaf ? leaf.DiskId : -1,
            (node, contents) => node is TreeNode leaf ? FirstWaterTexinfo(leaf, contents) : 0,
            _texInfos,
            compile.Options.Compliance);
        _set.Models = _builder.WaterModels;
    }

    /// <summary><c>EmitWaterVolumesForBSP</c> for the model just written.</summary>
    /// <param name="modelIndex">The model's index (<c>nummodels</c>).</param>
    /// <param name="headNode">The model's tree.</param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>A task.</returns>
    internal async Task EmitAsync(int modelIndex, TreeNode headNode, CancellationToken cancellationToken)
    {
        int firstModel = _builder.WaterModels.Count;
        _texInfos.Pending.Clear();
        _builder.EmitWaterVolumesForModel(modelIndex, headNode);

        // FindOrCreateWaterTexInfo, in the order the volumes asked for it.
        // Deferred only because the aliased texdata reads a material; nothing
        // between the request and this loop creates a texinfo or a texdata.
        for (int i = 0; i < _texInfos.Pending.Count; i++)
        {
            (int baseTexInfo, float depth) = _texInfos.Pending[i];
            _builder.DepthTexInfos[firstModel + i] = await FindOrCreateWaterTexInfoAsync(baseTexInfo, depth, cancellationToken)
                .ConfigureAwait(false);
        }

        // FindOrCreateLeafWaterData's table is dleafwaterdata itself.
        for (int i = _state.LeafWaterData.Count; i < _builder.LeafWaterData.Count; i++)
        {
            if (_state.LeafWaterData.Count >= WriteLimits.MaxMapLeafWaterData)
            {
                throw new MapCompileException(WriteCodes.LimitExceeded, "MAX_MAP_LEAFWATERDATA");
            }

            _state.LeafWaterData.Add(_builder.LeafWaterData[i]);
        }

        WriteFogVolumeIds(modelIndex);
    }

    /// <summary>
    /// <c>ClearLeafWaterData</c> then the id assignment of
    /// <c>ConvertWaterModelToPhysCollide</c> (<c>ivp.cpp:1475</c>, <c>:1187</c>):
    /// every leaf -1 and <c>CONTENTS_TESTFOGVOLUME</c> cleared, then each water
    /// model's leaves get its fog volume.
    /// </summary>
    /// <remarks>
    /// Stock does this inside <c>EmitPhysCollision</c>, and the id assignment
    /// only when a physics library loaded. The ids are pure bookkeeping over
    /// the water models, so they are assigned here whether or not a collision
    /// cooker is attached; the driver calls this at the moment stock's
    /// <c>EmitPhysCollision</c> would run, and a cooker's own result
    /// (<see cref="PhysCollisionResult.LeafWaterDataIds"/>) then replaces it
    /// with the same values.
    /// </remarks>
    internal void AssignLeafWaterData()
    {
        for (int i = 0; i < _state.Leafs.Count; i++)
        {
            DLeaf leaf = _state.Leafs[i];
            leaf.LeafWaterDataId = -1;
            leaf.Contents &= ~(int)BrushContents.TestFogVolume;
            _state.Leafs[i] = leaf;
        }

        // Only the WORLD's water models: ConvertWaterModelToPhysCollide is
        // called from BuildWorldPhysModel alone, for model 0 (ivp.cpp:1335);
        // a brush model's water leaves keep -1.
        foreach (WaterModel water in _builder.WaterModels)
        {
            if (water.ModelIndex != 0)
            {
                continue;
            }

            foreach (int leafIndex in water.Leaves)
            {
                DLeaf leaf = _state.Leafs[leafIndex];
                leaf.LeafWaterDataId = (short)water.FogVolumeIndex;
                _state.Leafs[leafIndex] = leaf;
            }
        }
    }

    /// <summary><c>FirstWaterTexinfo</c> (<c>ivp.cpp:1069</c>).</summary>
    private int FirstWaterTexinfo(TreeNode leaf, int contents)
    {
        // The leaf's brushlist in stock order: the detail fragments
        // MergeDetailTree prepended, then its own.
        foreach (BspBrush? head in new[] { _state.Faces.Lists.DetailBrushesOf(leaf), leaf.BrushList })
        {
            for (BspBrush? b = head; b is not null; b = b.Next)
            {
                MapBrush original = b.Original!;
                if ((original.Contents & contents) == 0)
                {
                    continue;
                }

                for (int i = 0; i < original.SideCount; i++)
                {
                    MapBrushSide side = _compile.MainMap!.BrushSides[original.FirstSide + i];
                    if ((side.Contents & contents) != 0)
                    {
                        return side.TexInfo;
                    }
                }
            }
        }

        return 0;
    }

    /// <summary><c>FindOrCreateWaterTexInfo</c> (<c>ivp.cpp:825</c>).</summary>
    private async Task<int> FindOrCreateWaterTexInfoAsync(
        int baseTexInfo, float depth, CancellationToken cancellationToken)
    {
        TexInfo baseInfo = _compile.TexInfos[baseTexInfo];

        // Get the base texture/material name
        string name = _compile.TexDatas.NameOf(baseInfo.TexData);

        string fullName = WaterVolumeBuilder.WaterTextureName(_compile.MapBase, name, (int)depth);

        // See if we already have an entry for this depth
        if (_set.TexInfos.TryGetValue(fullName, out int existing))
        {
            return existing;
        }

        // Remember the current material name
#pragma warning disable CA1308 // strlwr, ivp.cpp:842
        string materialName = name.ToLowerInvariant();
#pragma warning restore CA1308

        // Make a copy, with a texdata that is based on the underlying existing entry
        TexInfo ti = baseInfo;
        // FindAliasedTexData describes it with FindOriginalMaterial of the
        // water's texdata (textures.cpp:431): through the patch chain, since a
        // cubemap-patched water's own name exists only in the pak.
        ti.TexData = await _compile.TexDatas
            .FindAliasedAsync(fullName, _compile.Patcher.OriginalNameFor(name), _compile.Materials, _compile.Diagnostics, cancellationToken)
            .ConfigureAwait(false);

        // Find or create a new index
        int texInfo = _compile.TexInfos.FindOrCreate(ti);
        _set.TexInfos[fullName] = texInfo;

        // Go ahead and create the new vmt file.
        _set.Patches.Add(new WaterMaterialPatch(materialName, fullName, (int)depth));

        return texInfo;
    }

    /// <summary><c>WriteFogVolumeIDs</c> (<c>ivp.cpp:875</c>).</summary>
    private void WriteFogVolumeIds(int modelIndex)
    {
        // The model's face range: WriteBSP runs before EndModel, so the
        // model's own record is not complete yet; its first face is.
        int firstFace = _state.Models[modelIndex].FirstFace;
        int end = _state.DrawFaces.Count;

        // StockQuirk.FogVolumeLoopOverNoFaces: stock's bound is
        // firstface + numfaces, and numfaces is not set until EndModel.
        if (_state.Faces.Compliance.Emulates(Options.StockQuirk.FogVolumeLoopOverNoFaces))
        {
            end = firstFace;
        }

        Dictionary<int, int> fogOfLeaf = FogVolumesOfModel(modelIndex);

        for (int i = firstFace; i < end; i++)
        {
            DFace face = _state.DrawFaces[i];
            IBspNode? faceNode = _state.FaceNodes[i];
            TexInfo tex = _compile.TexInfos[face.TexInfo];
            face.SurfaceFogVolumeId = -1;

            if (faceNode is TreeNode node
                && (tex.Flags & (int)SurfaceFlags.Warp) != 0
                && node.IsLeaf
                && node.DiskId >= 0)
            {
                // The id the leaf WILL have once EmitPhysCollision assigns it.
                int fogId = fogOfLeaf.GetValueOrDefault(node.DiskId, -1);
                face.SurfaceFogVolumeId = (short)fogId;

                if (fogId >= 0)
                {
                    DLeafWaterData data = _state.LeafWaterData[fogId];

                    // HACKHACK: Use a heuristic, if it points up, it's the water top.
                    if (_planes[face.PlaneNum].Normal.Z > 0)
                    {
                        face.TexInfo = data.SurfaceTexInfoId;
                    }
                }
            }

            _state.DrawFaces[i] = face;
        }
    }

    // The fog volume AssignLeafWaterData will give each of a model's water
    // leaves, known already because the water models are.
    private Dictionary<int, int> FogVolumesOfModel(int modelIndex)
    {
        // Only world leaves ever get an id (see AssignLeafWaterData).
        Dictionary<int, int> map = [];
        foreach (WaterModel water in _builder.WaterModels)
        {
            if (water.ModelIndex != modelIndex || modelIndex != 0)
            {
                continue;
            }

            foreach (int leaf in water.Leaves)
            {
                map[leaf] = water.FogVolumeIndex;
            }
        }

        return map;
    }

    /// <summary>
    /// The builder's texinfo sink: it records each request, in order, and
    /// <see cref="EmitAsync"/> resolves them once the model's volumes are
    /// found (the aliased texdata reads a material, which is asynchronous).
    /// </summary>
    private sealed class DeferredWaterTexInfos : IWaterTexInfoSink
    {
        internal List<(int BaseTexInfo, float Depth)> Pending { get; } = [];

        public int FindOrCreateWaterTexInfo(int baseTexInfo, float depth)
        {
            Pending.Add((baseTexInfo, depth));
            return -1;
        }
    }
}

/// <summary>Coordinate limits (<c>public/worldsize.h</c>).</summary>
internal static class WriteConstants
{
    /// <summary><c>MAX_COORD_INTEGER</c>.</summary>
    internal const int MaxCoordInteger = 16384;

    /// <summary><c>MIN_COORD_INTEGER</c>.</summary>
    internal const int MinCoordInteger = -16384;
}
