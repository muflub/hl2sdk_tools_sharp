//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp.Write;

namespace SourceSharp.MapTools.Bsp.Driver;

/// <summary>
/// <c>-onlyents</c> and <c>-onlyprops</c>: the
/// entities (or the props) of a freshly loaded map written into a BSP that was
/// compiled earlier, with every other lump kept.
/// </summary>
internal static class OnlyEntsUpdate
{
    /// <summary>The name and payload stock adds to the pak to mark lighting stale.</summary>
    internal const string StaleName = "stale.txt";

    internal static async Task<BspData> RunAsync(
        BspData existing,
        MapFile map,
        VbspContext compile,
        IReadOnlyList<IVbspExtension> extensions,
        CancellationToken cancellationToken)
    {
        compile.MainMap ??= map;

        // Only the stage's map/compile/bsp are meaningful here: there is no
        // tree, so the write state is an empty one.
        BspWriteState state = new(new Faces.FaceBuildContext(
            compile.Windings, map.Planes, compile.TexInfos, compile.Options));
        VbspStageContext stage = new(compile, map, state, existing);

        if (compile.Options.OnlyEnts)
        {
            // Mark as stale since the lighting could be screwed with new ents.
            await AddStaleMarkerAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        EntityStage.SetModelNumbers(map);
        EntityStage.SetLightStyles(map);

        // NOTE: If we ever precompute lighting for static props in vrad,
        // EmitStaticProps should be removed here
        await RunAsync(extensions, VbspExtensionPoint.StaticProps, stage, cancellationToken).ConfigureAwait(false);

        if (compile.Options.OnlyEnts)
        {
            // NOTE: Don't deal with detail props here, it blows away lighting

            // Recompute the skybox
            EntityStage.ComputeBoundsNoSkybox(map, WorldLumps.From(existing), stage.DisplacementBounds);

            // Make sure that we have a water lod control eneity if we have water in the map.
            EntityStage.EnsurePresenceOfWaterLodControlEntity(
                map, compile.TextureReferences.HasWater, compile.Diagnostics);

            // Make sure the func_occluders have the appropriate data set
            FixupOnlyEntsOccluderEntities(map);

            // Doing this here because stuff abov may filter out entities
            existing[BspLump.Entities] = EntityStage.Unparse(map);
        }
        else
        {
            // In the only props case, deal with static + detail props only
            await RunAsync(extensions, VbspExtensionPoint.BeforeProcessModels, stage, cancellationToken)
                .ConfigureAwait(false);
            await RunAsync(extensions, VbspExtensionPoint.DetailObjects, stage, cancellationToken)
                .ConfigureAwait(false);
        }

        await RunAsync(extensions, VbspExtensionPoint.WriteFile, stage, cancellationToken).ConfigureAwait(false);
        return existing;
    }

    /// <summary>
    /// <c>FixupOnlyEntsOccluderEntities</c>: the same
    /// numbering <c>EmitOccluderBrushes</c> gives, without the geometry.
    /// </summary>
    /// <param name="map">The map.</param>
    internal static void FixupOnlyEntsOccluderEntities(MapFile map)
    {
        int occluder = 0;
        for (int i = 1; i < map.Entities.Count; ++i)
        {
            if (!EntityStage.IsFuncOccluder(map.Entities[i]))
            {
                continue;
            }

            map.Entities[i].SetKeyValue("occludernumber", occluder.ToString(CultureInfo.InvariantCulture));
            ++occluder;
        }
    }

    // AddBufferToPak(GetPakFile(), "stale.txt", "stale", strlen("stale") + 1, false)
    private static async Task AddStaleMarkerAsync(BspData bsp, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> pak = bsp[BspLump.PakFile].Data;
        ZipArchiveWriter writer = pak.IsEmpty
            ? new ZipArchiveWriter()
            : (await ZipArchiveReader.ParseAsync(pak, cancellationToken).ConfigureAwait(false)).ToWriter();

        writer.Add(StaleName, "stale\0"u8.ToArray());
        bsp.SetLump(BspLump.PakFile, writer.ToBytes());
    }

    private static async ValueTask RunAsync(
        IReadOnlyList<IVbspExtension> extensions,
        VbspExtensionPoint point,
        VbspStageContext stage,
        CancellationToken cancellationToken)
    {
        foreach (IVbspExtension extension in extensions)
        {
            await extension.RunAsync(point, stage, cancellationToken).ConfigureAwait(false);
        }
    }
}
