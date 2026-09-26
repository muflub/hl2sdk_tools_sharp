//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// <c>LoadMapFile</c>: opens a <c>.vmf</c> or
/// <c>.vmm</c>, loads it, merges its instances, cordons it, and takes its
/// bounds.
/// </summary>
/// <remarks>
/// <para>
/// The file-level half, kept apart from <see cref="MapFileLoader"/> so that
/// the loader can be exercised on a <see cref="VmfDocument"/> built in code
/// with no disk at all — which is what the unit tier runs on.
/// </para>
/// <para>
/// This is also the recursion point: <c>CheckForInstances</c> loads each
/// <c>func_instance</c> through this same function, and stock's loop
/// deliberately re-reads the growing entity list so that an instance inside an
/// instance is reached without recursing further.
/// </para>
/// </remarks>
public sealed class MapFileReader
{
    private readonly VbspContext _context;
    private readonly IFileSystem _files;

    /// <summary>Creates a reader.</summary>
    /// <param name="context">The compile.</param>
    /// <param name="files">
    /// Where map files are opened from. Separate from
    /// <see cref="VbspContext.Content"/>: a VMF is named by a path on disk,
    /// not resolved through the game's search paths.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public MapFileReader(VbspContext context, IFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(files);
        _context = context;
        _files = files;
    }

    /// <summary>
    /// The statistics of the most recent <see cref="LoadAsync"/>.
    /// </summary>
    public MapLoadStatistics Statistics { get; private set; }

    /// <summary>
    /// Loads a map and everything it pulls in.
    /// </summary>
    /// <param name="path">The <c>.vmf</c> or <c>.vmm</c> to load.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The loaded map.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <remarks>
    /// The order is stock's and is not interchangeable: load, then merge
    /// instances, then cordon, then take the bounds — cordoning removes
    /// brushes, so taking the bounds first would size the map to geometry that
    /// is no longer in it.
    /// </remarks>
    public async Task<MapFile> LoadAsync(VPath path, CancellationToken cancellationToken = default)
    {
        bool isManifest = path.Value.EndsWith(".vmm", StringComparison.OrdinalIgnoreCase);

        MapFile map;
        MapManifest? manifest = null;
        VPath basePath = path;

        if (isManifest)
        {
            manifest = await MapManifest.LoadAsync(_context, _files, path, null, cancellationToken)
                .ConfigureAwait(false);
            map = manifest.Map;
            basePath = VPath.Create(manifest.InstanceDirectory);
        }
        else
        {
            VmfDocument document = await ReadDocumentAsync(path, cancellationToken)
                .ConfigureAwait(false);
            return await LoadDocumentAsync(document, basePath, cancellationToken).ConfigureAwait(false);
        }

        await CheckForInstancesAsync(map, basePath, cancellationToken).ConfigureAwait(false);

        manifest!.CordonWorld();

        return Finish(map);
    }

    /// <summary>
    /// Loads a map from a VMF already parsed (or built in code), with the same
    /// steps after the parse as <see cref="LoadAsync"/>: instances, bounds,
    /// area-portal-window contents.
    /// </summary>
    /// <param name="document">The parsed VMF.</param>
    /// <param name="basePath">
    /// The file the document stands for: <c>func_instance</c> paths are
    /// resolved against it, through this reader's file system.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The loaded map.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <remarks>
    /// <c>LoadMapFile</c> with the text parse taken out,
    /// so a host that generates a map needs no VMF text in between.
    /// </remarks>
    public async Task<MapFile> LoadDocumentAsync(
        VmfDocument document,
        VPath basePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        MapFile map = await MapFileLoader.LoadAsync(_context, document, cancellationToken)
            .ConfigureAwait(false);

        await CheckForInstancesAsync(map, basePath, cancellationToken).ConfigureAwait(false);

        return Finish(map);
    }

    private MapFile Finish(MapFile map)
    {
        TakeBounds(map);
        MapFileLoader.ForceFuncAreaPortalWindowContents(map);

        _context.LoadingMap = map;
        Statistics = MapLoadStatistics.Of(_context, map);
        return map;
    }

    /// <summary>
    /// Merges every <c>func_instance</c> in a map, and every one those pull
    /// in: <c>CMapFile::CheckForInstances</c>.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="basePath">The file the map was read from.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A task.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <para>
    /// The loop index walks a list that GROWS as instances are merged, so a
    /// nested <c>func_instance</c> is reached by the same loop rather than by
    /// recursion. Every <c>func_instance</c> entity is blanked afterwards
    /// whether it loaded or not, so a missing
    /// instance leaves an empty entity slot and a red message, not a failure.
    /// </para>
    /// <para>
    /// The classname test is <c>strcmp</c> — case SENSITIVE — which is why
    /// <see cref="FuncInstance.ClassName"/> says so.
    /// </para>
    /// </remarks>
    public async Task CheckForInstancesAsync(
        MapFile map,
        VPath basePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);

        for (int i = 0; i < map.Entities.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MapEntity entity = map.Entities[i];

            if (!string.Equals(
                    entity.ValueForKey("classname"), FuncInstance.ClassName, StringComparison.Ordinal))
            {
                continue;
            }

            string file = entity.ValueForKey(FuncInstance.FileKey);

            if (file.Length > 0)
            {
                VPath resolved = await ResolveInstanceAsync(basePath, file, cancellationToken)
                    .ConfigureAwait(false);

                if (resolved.Value.Length == 0)
                {
                    _context.Diagnostics.Add(new CompileDiagnostic(
                        MapLoadDiagnostics.InstanceNotFound,
                        DiagnosticSeverity.Error,
                        $"Could not open instance file {file}"));
                }
                else
                {
                    VmfDocument document = await ReadDocumentAsync(resolved, cancellationToken)
                        .ConfigureAwait(false);
                    MapFile instance = await MapFileLoader
                        .LoadAsync(_context, document, cancellationToken)
                        .ConfigureAwait(false);

                    await CheckForInstancesAsync(instance, resolved, cancellationToken)
                        .ConfigureAwait(false);

                    await MapInstanceMerger
                        .MergeAsync(_context, map, entity, instance, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            entity.Clear();
        }
    }

    /// <summary>
    /// Takes the map's bounds from its worldspawn brushes:
    /// </summary>
    /// <param name="map">The map.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <remarks>
    /// A brush whose <c>mins.x</c> is past the coordinate limit is skipped as
    /// "no valid points", which is how the <c>ClearBounds</c> sentinel of 99999
    /// on a brush with no surviving sides is excluded.
    /// </remarks>
    public static void TakeBounds(MapFile map)
    {
        ArgumentNullException.ThrowIfNull(map);

        Vec3 mins = MapFile.ClearedMins;
        Vec3 maxs = MapFile.ClearedMaxs;

        int worldBrushes = map.Entities.Count > 0 ? map.Entities[0].BrushCount : 0;

        for (int i = 0; i < worldBrushes; i++)
        {
            MapBrush brush = map.Brushes[i];

            if (brush.Mins.X > GeometryEpsilons.MaxCoordInteger)
            {
                continue;
            }

            mins = new Vec3(
                MathF.Min(MathF.Min(mins.X, brush.Mins.X), brush.Maxs.X),
                MathF.Min(MathF.Min(mins.Y, brush.Mins.Y), brush.Maxs.Y),
                MathF.Min(MathF.Min(mins.Z, brush.Mins.Z), brush.Maxs.Z));
            maxs = new Vec3(
                MathF.Max(MathF.Max(maxs.X, brush.Mins.X), brush.Maxs.X),
                MathF.Max(MathF.Max(maxs.Y, brush.Mins.Y), brush.Maxs.Y),
                MathF.Max(MathF.Max(maxs.Z, brush.Mins.Z), brush.Maxs.Z));
        }

        map.Mins = mins;
        map.Maxs = maxs;
    }

    private async Task<VmfDocument> ReadDocumentAsync(VPath path, CancellationToken cancellationToken)
    {
        await using Stream stream = await _files.OpenReadAsync(path, cancellationToken)
            .ConfigureAwait(false);

        return await VmfDocument.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VPath> ResolveInstanceAsync(
        VPath basePath,
        string file,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> candidates = FuncInstance.ResolveCandidates(
            basePath.Value,
            file,
            _context.InstancePath.Length > 0 ? _context.InstancePath : null);

        foreach (string candidate in candidates)
        {
            VPath path = VPath.Create(candidate);

            if (await _files.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                return path;
            }
        }

        return default;
    }
}
