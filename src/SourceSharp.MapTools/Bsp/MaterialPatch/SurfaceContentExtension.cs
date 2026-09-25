using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.Cubemaps;
using SourceSharp.MapTools.Bsp.Detail;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Bsp.Overlays;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Bsp.Write;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.MaterialPatch;

/// <summary>
/// Phase 3g in the vbsp driver: cubemaps, overlays, static props, detail
/// props and the pak file, each at the point stock produces it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stock call order</b>, which this follows point for point
/// (<see cref="VbspExtensionPoint"/>):
/// </para>
/// <list type="number">
/// <item>AfterLoad: the <c>WorldVertexTransitionFixup</c> patches (3a's, run
/// by the driver) go into the pak — <c>WriteMaterialKeyValuesToPak</c> at
/// <c>worldvertextransitionfixup.cpp:94</c> — then
/// <c>Cubemap_FixupBrushSidesMaterials</c>,
/// <c>Cubemap_AttachDefaultCubemapToSpecularSides</c>,
/// <c>Cubemap_AddUnreferencedCubemaps</c> (<c>vbsp.cpp:1419-1424</c>). The
/// overlays stock parsed during the load (<c>map.cpp:1649</c>,
/// <c>:1060</c>) are collected here; nothing between the load and this point
/// reads them.</item>
/// <item>BeforeProcessModels: <c>LoadEmitDetailObjectDictionary</c>.</item>
/// <item>DefaultCubemaps: the water depth patches the model loop asked for
/// (<c>ivp.cpp:815</c>, created there in stock; only the pak and the
/// translation table see them, and nothing reads either before this point),
/// then <c>Cubemap_CreateDefaultCubemaps</c> (<c>vbsp.cpp:884</c>).</item>
/// <item>OverlayFaces: <c>Overlay_EmitOverlayFaces</c> and
/// <c>OverlayTransition_EmitOverlayFaces</c>; their texinfos are renumbered by
/// <c>CompactTexinfos</c> (<c>writebsp.cpp:800-823</c>).</item>
/// <item>StaticProps, DetailObjects: <c>EmitStaticProps</c>,
/// <c>EmitDetailObjects</c>, over the written but not yet compacted
/// arrays.</item>
/// <item>WriteFile: the game lumps, LUMP_PAKFILE, LUMP_CUBEMAPS and the
/// three overlay lumps.</item>
/// </list>
/// <para>
/// Under <c>-onlyents</c> / <c>-onlyprops</c> only the prop lumps are
/// rewritten; the existing file's pak, cubemaps and overlays are kept, as
/// stock's <c>LoadBSPFile</c> + <c>WriteBSPFile</c> keeps them.
/// </para>
/// </remarks>
internal sealed class SurfaceContentExtension : IVbspExtension
{
    private readonly VmfDocument? _document;
    private readonly IStaticPropCollision _collision;

    private MaterialPatcher? _patcher;
    private CubemapFixups? _cubemaps;
    private OverlaySet? _overlays;
    private DetailDictionary? _dictionary;
    private OverlayTexInfos? _overlayTexInfos;
    private StaticPropLump? _staticProps;
    private StaticPropEmitter? _propEmitter;
    private DetailPropLump? _detailProps;

    /// <summary>The extension for one compile.</summary>
    /// <param name="document">
    /// The VMF the map was loaded from, whose <c>overlaytransition</c> chunks
    /// are then read instead of the ones the loader kept
    /// (<see cref="MapFile.WaterOverlayData"/>); null, the normal case, uses
    /// the map's.
    /// </param>
    /// <param name="cooker">
    /// The compile's collision cooker; null answers the prop hull questions
    /// with <see cref="ManagedStaticPropCollision"/>.
    /// </param>
    internal SurfaceContentExtension(VmfDocument? document = null, ICollisionCooker? cooker = null)
    {
        _document = document;
        _collision = cooker is null ? new ManagedStaticPropCollision() : new CookedStaticPropCollision(cooker);
    }

    /// <summary>The pak being built; null before <see cref="VbspExtensionPoint.AfterLoad"/>.</summary>
    internal MapPakFile? Pak => _patcher?.Pak;

    /// <inheritdoc/>
    public async ValueTask RunAsync(VbspExtensionPoint point, VbspStageContext stage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stage);
        VbspContext compile = stage.Compile;
        bool update = compile.Options.OnlyEnts || compile.Options.OnlyProps;

        switch (point)
        {
            case VbspExtensionPoint.AfterLoad:
                await AfterLoadAsync(stage, cancellationToken).ConfigureAwait(false);
                break;

            case VbspExtensionPoint.BeforeProcessModels:
                _dictionary = await DetailPropEmitter.LoadDictionaryAsync(stage.Map.Entities, compile.Content, cancellationToken)
                    .ConfigureAwait(false);

                // The prop hulls need nothing but their models, so they start
                // cooking now and overlap the BSP build (plan 3p). Not under
                // -leaktest, which may stop before EmitStaticProps.
                if (!update && !compile.Options.LeakTest)
                {
                    _propEmitter = new StaticPropEmitter(compile, _collision);
                    await _propEmitter.PrefetchAsync(stage.Map.Entities, cancellationToken).ConfigureAwait(false);
                }

                break;

            case VbspExtensionPoint.DefaultCubemaps:
                await DefaultCubemapsAsync(stage, cancellationToken).ConfigureAwait(false);
                break;

            case VbspExtensionPoint.OverlayFaces:
                if (_overlays is not null && _overlayTexInfos is not null)
                {
                    _overlayTexInfos.Lumps = await _overlays.EmitAsync(compile, cancellationToken).ConfigureAwait(false);
                }

                break;

            case VbspExtensionPoint.StaticProps:
                _staticProps = await (_propEmitter ?? new StaticPropEmitter(compile, _collision))
                    .EmitAsync(stage.Map.Entities, update ? BspTreeView.FromBsp(stage.Bsp) : TreeOf(stage.State), cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VbspExtensionPoint.DetailObjects:
                await DetailObjectsAsync(stage, update, cancellationToken).ConfigureAwait(false);
                break;

            case VbspExtensionPoint.WriteFile:
                WriteFile(stage, update);
                break;

            default:
                break;
        }
    }

    private async Task AfterLoadAsync(VbspStageContext stage, CancellationToken cancellationToken)
    {
        VbspContext compile = stage.Compile;
        _patcher = compile.Patcher;

        foreach (WorldVertexTransitionPatch patch in stage.WorldVertexTransitionPatches)
        {
            _patcher.WriteMaterialKeyValuesToPak(patch.Name, patch.Material);
        }

        _cubemaps = new CubemapFixups(compile, stage.Map, _patcher);
        await _cubemaps.FixupBrushSidesMaterialsAsync(cancellationToken).ConfigureAwait(false);
        await _cubemaps.AttachDefaultCubemapToSpecularSidesAsync(cancellationToken).ConfigureAwait(false);
        _cubemaps.AddUnreferencedCubemaps();

        // The water overlays: the loader keeps the overlaytransition chunks on
        // the map (MapFile.WaterOverlayData); a document, when one is given,
        // is read instead.
        _overlays = _document is null
            ? OverlaySet.Load(stage.Map, compile.MaterialReplacements, compile.Options.Compliance)
            : OverlaySet.Load(stage.Map, _document, compile.MaterialReplacements, compile.Options.Compliance);
        _overlayTexInfos = new OverlayTexInfos();
        stage.OverlayFaces = new OverlayFaceSink(_overlays);
        stage.TexInfoReferences.Add(_overlayTexInfos);
    }

    private async Task DefaultCubemapsAsync(VbspStageContext stage, CancellationToken cancellationToken)
    {
        if (_patcher is null || _cubemaps is null)
        {
            return;
        }

        VbspContext compile = stage.Compile;

        // CreateMaterialPatch(material, "maps/<map>/<material>_depth_<n>",
        // "$waterdepth", "%i", PATCH_INSERT), ivp.cpp:812-815.
        foreach (WaterMaterialPatch water in stage.Water.Patches)
        {
            await _patcher.CreatePatchAsync(
                water.MaterialName,
                water.PatchedName,
                [new MaterialPatchInfo("$waterdepth", water.WaterDepth.ToString(CultureInfo.InvariantCulture))],
                MaterialPatchType.Insert,
                cancellationToken).ConfigureAwait(false);
        }

        MapEntity? world = stage.Map.Entities.Count > 0 ? stage.Map.Entities[0] : null;
        string? skyName = world?.ValueForKey("skyname");
        await DefaultCubemapBuilder.CreateAsync(
            skyName, compile.MapBase, _cubemaps.DefaultCubemapNames, compile.Materials, compile.Content,
            _patcher.Pak, compile.Diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private async Task DetailObjectsAsync(VbspStageContext stage, bool update, CancellationToken cancellationToken)
    {
        VbspContext compile = stage.Compile;
        _patcher ??= compile.Patcher;
        DetailDictionary dictionary = _dictionary ?? new DetailDictionary();

        DetailPropEmitter emitter = new(compile, _patcher, dictionary);
        if (update)
        {
            _detailProps = await emitter
                .EmitAsync(stage.Map.Entities, stage.Bsp, BspTreeView.FromBsp(stage.Bsp), null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        BspWriteState state = stage.State;
        DetailPropEmitter.FaceGeometry geometry = new(
            [.. state.DrawFaces],
            [.. state.SurfEdges],
            [.. state.Edges.Edges],
            [.. state.Vertices.Vertexes],
            [.. state.FaceIds],
            texInfo =>
            {
                int texData = compile.TexInfos[texInfo].TexData;
                return texData < 0 ? string.Empty : compile.TexDatas.NameOf(texData);
            });

        _detailProps = await emitter
            .EmitAsync(stage.Map.Entities, geometry, TreeOf(state), new MapDisplacementSurfaces(compile, stage.Map), cancellationToken)
            .ConfigureAwait(false);
    }

    private void WriteFile(VbspStageContext stage, bool update)
    {
        BspData bsp = stage.Bsp;

        if (_staticProps is not null)
        {
            ReplaceGameLump(bsp, _staticProps.Write());
        }

        if (_detailProps is not null)
        {
            ReplaceGameLump(bsp, _detailProps.Write());
        }

        if (update || _patcher is null)
        {
            return;
        }

        bsp.SetLump(BspLump.PakFile, _patcher.Pak.ToWriter().ToBytes());
        bsp.SetLump(BspLump.Cubemaps, CubemapSampleLump.ToBytes(stage.Compile.CubemapSamples));

        if (_overlayTexInfos?.Lumps is OverlayLumps lumps)
        {
            bsp.SetLump(BspLump.Overlays, lumps.OverlayBytes());
            bsp.SetLump(BspLump.OverlayFades, lumps.FadeBytes());
            bsp.SetLump(BspLump.WaterOverlays, lumps.WaterOverlayBytes());
        }
    }

    // The entry keeps its place in the directory: sprp then dprp, as
    // AddGameLump is called in stock (gamebspfile / bsplib).
    private static void ReplaceGameLump(BspData bsp, GameLumpEntry entry)
    {
        for (int i = 0; i < bsp.GameLumps.Count; i++)
        {
            if (bsp.GameLumps[i].Id == entry.Id)
            {
                bsp.GameLumps[i] = entry;
                return;
            }
        }

        bsp.GameLumps.Add(entry);
    }

    private static BspTreeView TreeOf(BspWriteState state) =>
        new([.. state.Nodes], [.. state.Planes], [.. state.Leafs.Select(l => l.Contents)])
        {
            Leafs = [.. state.Leafs],
        };

    // EmitFace's two list calls (writebsp.cpp:519-534).
    private sealed class OverlayFaceSink(OverlaySet overlays) : IOverlayFaceSink
    {
        public void AddFace(int faceIndex, MapBrushSide side) => overlays.AddOverlayFace(faceIndex, side);

        public void AddWaterFace(int faceIndex, MapBrushSide side) => overlays.AddWaterOverlayFace(faceIndex, side);
    }

    // CompactTexinfos' overlay and water overlay loops (writebsp.cpp:800-823):
    // every overlay counts; a water overlay only with a texinfo.
    private sealed class OverlayTexInfos : ITexInfoReferences
    {
        public OverlayLumps? Lumps { get; set; }

        public void CountReferences(Span<int> refCounts)
        {
            if (Lumps is null)
            {
                return;
            }

            foreach (DOverlay overlay in Lumps.Overlays)
            {
                refCounts[overlay.TexInfo]++;
            }

            foreach (DWaterOverlay overlay in Lumps.WaterOverlays)
            {
                if (overlay.TexInfo >= 0)
                {
                    refCounts[overlay.TexInfo]++;
                }
            }
        }

        public void Remap(ReadOnlySpan<int> outputIndex)
        {
            if (Lumps is null)
            {
                return;
            }

            for (int i = 0; i < Lumps.Overlays.Length; i++)
            {
                Lumps.Overlays[i].TexInfo = checked((short)outputIndex[Lumps.Overlays[i].TexInfo]);
            }

            for (int i = 0; i < Lumps.WaterOverlays.Length; i++)
            {
                if (Lumps.WaterOverlays[i].TexInfo >= 0)
                {
                    Lumps.WaterOverlays[i].TexInfo = checked((short)outputIndex[Lumps.WaterOverlays[i].TexInfo]);
                }
            }
        }
    }
}
