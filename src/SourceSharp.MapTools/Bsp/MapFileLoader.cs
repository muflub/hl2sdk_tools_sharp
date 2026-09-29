//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// The VMF-to-<see cref="MapFile"/> half of the reference implementation: the
/// entity, solid and side callbacks, and the per-classname dispatch that
/// follows an entity.
/// </summary>
/// <remarks>
/// <para>
/// Stock drives this from <c>CChunkFile</c>'s callbacks, so keys and
/// sub-chunks arrive interleaved in FILE order. That interleaving is
/// behaviour, not an artefact: <c>classname func_detail</c> sets the base
/// contents that the <c>solid</c> chunks after it are loaded with
/// (feeding), and inside a side
/// the <c>material</c> key seeds flags that a later <c>flags</c> key ORs into
///So this walks the parsed chunk's
/// children in order and dispatches each, rather than reading keys first and
/// chunks second.
/// </para>
/// <para>
/// Everything here takes a <see cref="VbspContext"/> and a
/// <see cref="MapFile"/>. There is no <c>g_LoadingMap</c>.
/// </para>
/// </remarks>
public static class MapFileLoader
{
    /// <summary>The VMF chunk holding worldspawn.</summary>
    public const string WorldChunk = "world";

    /// <summary>The VMF chunk holding a point or brush entity.</summary>
    public const string EntityChunk = "entity";

    /// <summary>The VMF chunk holding a brush.</summary>
    public const string SolidChunk = "solid";

    /// <summary>The VMF chunk holding a brush side.</summary>
    public const string SideChunk = "side";

    /// <summary>The VMF chunk holding an entity's outputs.</summary>
    public const string ConnectionsChunk = "connections";

    /// <summary>The VMF chunk holding an entity's water overlays.</summary>
    public const string OverlayTransitionChunk = "overlaytransition";

    /// <summary>The VMF chunk holding one water overlay, inside <see cref="OverlayTransitionChunk"/>.</summary>
    public const string OverlayDataChunk = "overlaydata";

    /// <summary>
    /// Loads a whole parsed VMF into a fresh map.
    /// </summary>
    /// <param name="context">The compile.</param>
    /// <param name="document">The parsed VMF.</param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>The map.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="document"/> is null.
    /// </exception>
    /// <remarks>
    /// The root-level chunks vbsp registers handlers for are exactly
    /// <c>world</c> and <c>entity</c>, both going to the same callback
    /// And root-level KEYS are ignored — stock's
    /// own comment says so. So <c>versioninfo</c>,
    /// <c>visgroups</c>, <c>viewsettings</c>, <c>cameras</c> and <c>cordon</c>
    /// at the root are skipped, and worldspawn is entity 0 only because it
    /// comes first in the file.
    /// </remarks>
    public static async Task<MapFile> LoadAsync(
        VbspContext context,
        VmfDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(document);

        // LoadSurfaceProperties runs before LoadMapFile, so
        // every texdata the load creates resolves its $surfaceprop.
        //
        // Loaded once for a batch of compiles that share their material
        // reads (a room library): the table is a function of the content,
        // and no compile changes it.
        context.TexDatas.PropertyTable ??= context.SharedMaterials is { } shared
            ? await shared.GetSurfacePropertiesAsync(cancellationToken).ConfigureAwait(false)
            : await SurfacePropertyTable.LoadAsync(context.Content, cancellationToken).ConfigureAwait(false);

        MapFile map = new(context.Windings);
        context.Maps.Add(map);
        context.MainMap ??= map;
        context.LoadingMap = map;

        foreach (VmfChunk chunk in document.Chunks)
        {
            if (string.Equals(chunk.Name, WorldChunk, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(chunk.Name, EntityChunk, StringComparison.OrdinalIgnoreCase))
            {
                await LoadEntityAsync(context, map, chunk, cancellationToken).ConfigureAwait(false);
            }
        }

        return map;
    }

    /// <summary>
    /// Loads one <c>world</c> or <c>entity</c> chunk:
    /// <c>CMapFile::LoadEntityCallback</c>.
    /// </summary>
    /// <param name="context">The compile.</param>
    /// <param name="map">The map being loaded.</param>
    /// <param name="chunk">The entity chunk.</param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>The entity, which may have been blanked.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="MapCompileException">A map limit was exceeded.</exception>
    public static async Task<MapEntity> LoadEntityAsync(
        VbspContext context,
        MapFile map,
        VmfChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(chunk);

        if (map.Entities.Count == MapFile.MaxMapEntities)
        {
            throw new MapCompileException("num_entities == MAX_MAP_ENTITIES");
        }

        MapEntity entity = new() { FirstBrush = map.BrushCount };
        map.Entities.Add(entity);

        int baseContents = 0;
        const int baseFlags = 0;

        // Indexed, not foreach: Children is an IList, and enumerating one
        // through the interface boxes an enumerator per chunk -- one per
        // entity, solid and side of the map. The loader never edits the
        // document it reads, so the index walk sees the same children.
        IList<VmfNode> nodes = chunk.Children;
        for (int n = 0; n < nodes.Count; n++)
        {
            VmfNode node = nodes[n];
            cancellationToken.ThrowIfCancellationRequested();

            if (node is VmfKey key)
            {
                if (string.Equals(key.Name, "mapversion", StringComparison.OrdinalIgnoreCase))
                {
                    // G_MapRevision -- stamped into the BSP
                    // header later. The key is still stored under its own name.
                    context.MapRevision = VmfValue.ParseInt(key.Value);
                }

                ApplyEntityKey(entity, key.Name, key.Value, ref baseContents);
                continue;
            }

            if (node is not VmfChunk child)
            {
                continue;
            }

            if (string.Equals(child.Name, SolidChunk, StringComparison.OrdinalIgnoreCase))
            {
                await LoadSolidAsync(context, map, entity, child, baseFlags, baseContents, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (string.Equals(child.Name, ConnectionsChunk, StringComparison.OrdinalIgnoreCase))
            {
                LoadConnections(map, entity, child);
            }
            else if (string.Equals(child.Name, OverlayTransitionChunk, StringComparison.OrdinalIgnoreCase))
            {
                // LoadOverlayTransitionCallback: only its
                // overlaydata chunks, in order.
                foreach (VmfChunk data in child.Chunks)
                {
                    if (string.Equals(data.Name, OverlayDataChunk, StringComparison.OrdinalIgnoreCase))
                    {
                        map.WaterOverlayData.Add(data);
                    }
                }
            }
        }

        await FinishEntityAsync(context, map, entity, cancellationToken).ConfigureAwait(false);
        return entity;
    }

    /// <summary>
    /// Applies one entity key: <c>LoadEntityKeyCallback</c>,
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="baseContents">
    /// The contents every brush of this entity starts with, updated by a
    /// <c>classname</c> of <c>func_detail</c>, <c>func_ladder</c> or
    /// <c>func_water</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <para>
    /// Two keys are renamed or intercepted. <c>id</c> is stored as
    /// <c>hammerid</c> — stock's comment: "rename this field since DME code
    /// uses this name" — and <c>mapversion</c> is also captured into the BSP
    /// header's revision. Both then <c>return</c>, so neither falls through to
    /// a second <c>SetKeyValue</c>.
    /// </para>
    /// <para>
    /// The classname branch does NOT return, so the classname is stored as
    /// well as read.
    /// </para>
    /// </remarks>
    public static void ApplyEntityKey(
        MapEntity entity,
        string key,
        string value,
        ref int baseContents)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        if (string.Equals(key, "classname", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(value, "func_detail", StringComparison.OrdinalIgnoreCase))
            {
                baseContents = (int)BrushContents.Detail;
            }
            else if (string.Equals(value, "func_ladder", StringComparison.OrdinalIgnoreCase))
            {
                baseContents = (int)BrushContents.Ladder;
            }
            else if (string.Equals(value, "func_water", StringComparison.OrdinalIgnoreCase))
            {
                baseContents = (int)BrushContents.Water;
            }
        }
        else if (string.Equals(key, "id", StringComparison.OrdinalIgnoreCase))
        {
            entity.SetKeyValue("hammerid", value);
            return;
        }

        // "mapversion" also sets g_MapRevision, which is
        // stamped into the BSP header. The revision lives on the context; the
        // key itself is stored under its own name either way, which is why
        // that branch and this line are the same line here.
        entity.SetKeyValue(key, value);
    }

    /// <summary>
    /// Loads one <c>solid</c> chunk: <c>CMapFile::LoadSolidCallback</c>,
    /// </summary>
    /// <param name="context">The compile.</param>
    /// <param name="map">The map being loaded.</param>
    /// <param name="entity">The entity the brush belongs to.</param>
    /// <param name="chunk">The solid chunk.</param>
    /// <param name="baseFlags">The entity's base surface flags.</param>
    /// <param name="baseContents">The entity's base contents.</param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>The brush, or null when it was not kept.</returns>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    /// <exception cref="MapCompileException">A map limit was exceeded.</exception>
    /// <remarks>
    /// <para>
    /// Five things discard a brush after its sides are read, and each returns
    /// without committing it: <c>-nodetail</c> on a detail brush that is not a
    /// displacement, <c>-nowater</c> on a water brush, an origin brush (whose
    /// centre becomes the entity's <c>origin</c> first), and a brush carrying a
    /// displacement. A discarded brush's SIDES stay in the side array — stock
    /// leaves <c>nummapbrushsides</c> advanced — so side indices are stable
    /// either way.
    /// </para>
    /// <para>
    /// A world clip brush has every side's texinfo replaced with
    /// <see cref="TexInfoTable.TexInfoNode"/> so the BSP never splits on it,
    /// and the first one's texinfo is remembered in
    /// <see cref="MapFile.ClipTexInfo"/> because it is about to be erased.
    /// </para>
    /// </remarks>
    public static async Task<MapBrush?> LoadSolidAsync(
        VbspContext context,
        MapFile map,
        MapEntity entity,
        VmfChunk chunk,
        int baseFlags,
        int baseContents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(chunk);

        if (map.BrushCount == MapFile.MaxMapBrushes)
        {
            throw new MapCompileException("nummapbrushes == MAX_MAP_BRUSHES");
        }

        MapBrush brush = new()
        {
            FirstSide = map.BrushSideCount,
            EntityNumber = map.Entities.Count - 1,
            BrushNumber = map.BrushCount - entity.FirstBrush,
        };

        IList<VmfNode> nodes = chunk.Children;
        for (int n = 0; n < nodes.Count; n++)
        {
            VmfNode node = nodes[n];
            cancellationToken.ThrowIfCancellationRequested();

            if (node is VmfKey key)
            {
                if (string.Equals(key.Name, "id", StringComparison.OrdinalIgnoreCase))
                {
                    brush.Id = VmfValue.ParseInt(key.Value);
                }

                continue;
            }

            if (node is VmfChunk side &&
                string.Equals(side.Name, SideChunk, StringComparison.OrdinalIgnoreCase))
            {
                await LoadSideAsync(context, map, brush, side, baseFlags, baseContents, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        brush.Contents = BrushContentsOf(map, brush);

        if (context.Options.NoDetail &&
            (brush.Contents & (int)BrushContents.Detail) != 0 &&
            !HasDisplacement(map, brush))
        {
            return null;
        }

        if (context.Options.NoWater && (brush.Contents & MaskWater) != 0)
        {
            return null;
        }

        map.MakeBrushWindings(brush, context.Diagnostics);

        if (brush.EntityNumber == 0 &&
            (brush.Contents & (int)(BrushContents.PlayerClip | BrushContents.MonsterClip)) != 0)
        {
            if (map.ClipTexInfo < 0)
            {
                map.ClipTexInfo = map.BrushSides[brush.FirstSide].TexInfo;
            }

            map.ClipBrushes++;

            for (int i = 0; i < brush.SideCount; i++)
            {
                map.BrushSides[brush.FirstSide + i].TexInfo = TexInfoTable.TexInfoNode;
            }
        }

        if ((brush.Contents & (int)BrushContents.Origin) != 0)
        {
            if (map.Entities.Count == 1)
            {
                throw new MapCompileException(
                    $"Brush {brush.Id}: origin brushes not allowed in world");
            }

            Vec3 origin = (brush.Mins + brush.Maxs) * 0.5f;
            MapEntity owner = map.Entities[brush.EntityNumber];

            // sprintf("%i %i %i") on floats cast to int: truncation toward
            // zero, so a brush centred at -0.5 gives 0 and not -1.
            owner.SetKeyValue(
                "origin",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(int)origin.X} {(int)origin.Y} {(int)origin.Z}"));
            owner.Origin = origin;

            return null;
        }

        if (HasDisplacement(map, brush))
        {
            // DispGetFaceInfo hands the base
            // face to the displacement: the side (winding, texinfo, planenum,
            // id) stays in the side array, and the brush's contents and entity
            // go onto the displacement. The brush itself is not kept.
            string className = map.Entities[brush.EntityNumber].ValueForKey("classname");
            for (int i = 0; i < brush.SideCount; i++)
            {
                MapBrushSide side = map.BrushSides[brush.FirstSide + i];
                if (side.Displacement is not Disp.MapDisplacement disp)
                {
                    continue;
                }

                Disp.DispVbspHooks.CheckBrush(
                    brush.EntityNumber, className, brush.BrushNumber, map.Windings.Points(side.Winding).Length);

                disp.Contents = brush.Contents;
                disp.EntityNumber = brush.EntityNumber;
                disp.BrushSideId = side.Id;
            }

            return null;
        }

        map.AddBrushBevels(brush);
        map.Brushes.Add(brush);
        entity.BrushCount++;
        return brush;
    }

    /// <summary>
    /// Loads one <c>side</c> chunk: <c>CMapFile::LoadSideCallback</c>,
    /// </summary>
    /// <param name="context">The compile.</param>
    /// <param name="map">The map being loaded.</param>
    /// <param name="brush">The brush the side belongs to.</param>
    /// <param name="chunk">The side chunk.</param>
    /// <param name="baseFlags">The entity's base surface flags.</param>
    /// <param name="baseContents">The entity's base contents.</param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>The side, or null when its plane was already used.</returns>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    /// <exception cref="MapCompileException">A map limit was exceeded.</exception>
    /// <remarks>
    /// <para>
    /// The contents fix-ups happen in this order and each depends on the last:
    /// the entity's base contents and flags are OR'd in; a clip side is forced
    /// to detail; <c>-fulldetail</c> then clears detail everywhere; a side with
    /// no visible contents and no clip contents is made solid; and finally a
    /// hint or skip side has its contents cleared to nothing at all
    /// Reordering any two of those changes which
    /// brushes the BSP treats as solid.
    /// </para>
    /// <para>
    /// A side whose plane duplicates or mirrors one already on the brush is
    /// DROPPED — the side is not committed and the brush's count does not grow
    /// — but the plane it created stays in the plane
    /// table, because <c>PlaneFromPoints</c> ran before the check.
    /// </para>
    /// </remarks>
    public static async Task<MapBrushSide?> LoadSideAsync(
        VbspContext context,
        MapFile map,
        MapBrush brush,
        VmfChunk chunk,
        int baseFlags,
        int baseContents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(chunk);

        if (map.BrushSideCount == MapFile.MaxMapBrushSides)
        {
            throw new MapCompileException("MAX_MAP_BRUSHSIDES");
        }

        MapBrushSide side = new() { TexInfo = 0 };
        BrushTexture td = new() { Name = string.Empty };
        Vec3[] planePoints = new Vec3[3];

        IList<VmfNode> nodes = chunk.Children;
        for (int n = 0; n < nodes.Count; n++)
        {
            VmfNode node = nodes[n];
            cancellationToken.ThrowIfCancellationRequested();

            if (node is not VmfKey key)
            {
                continue;
            }

            td = await ApplySideKeyAsync(
                    context, side, td, key.Name, key.Value, planePoints, cancellationToken)
                .ConfigureAwait(false);
        }

        side.Contents |= baseContents;
        side.Surface |= baseFlags;
        td.Flags |= baseFlags;

        if ((side.Contents & (int)(BrushContents.PlayerClip | BrushContents.MonsterClip)) != 0)
        {
            side.Contents |= (int)BrushContents.Detail;
        }

        if (context.Options.FullDetail)
        {
            side.Contents &= ~(int)BrushContents.Detail;
        }

        if ((side.Contents & (AllVisibleContents |
                (int)(BrushContents.PlayerClip | BrushContents.MonsterClip))) == 0)
        {
            side.Contents |= (int)BrushContents.Solid;
        }

        if ((side.Surface & (int)(SurfaceFlags.Hint | SurfaceFlags.Skip)) != 0)
        {
            side.Contents = 0;
        }

        // StockQuirk.PlaneFromPointsNormalise: stock normalises the side's
        // cross product with the estimate, so under Stock the table holds
        // the estimate's bits and every later epsilon match is made against
        // them.
        int planeNumber = map.Planes.FromPoints(
            planePoints[0],
            planePoints[1],
            planePoints[2],
            context.Options.SnapAxialPlanes,
            context.Options.Compliance.Emulates(StockQuirk.PlaneFromPointsNormalise));

        for (int k = 0; k < brush.SideCount; k++)
        {
            MapBrushSide other = map.BrushSides[brush.FirstSide + k];

            if (other.PlaneNumber == planeNumber)
            {
                context.Diagnostics.Add(new CompileDiagnostic(
                    MapLoadDiagnostics.DuplicatePlane,
                    DiagnosticSeverity.Warning,
                    $"Brush {brush.Id}, Side {k}: duplicate plane",
                    new MapLocation(BrushId: brush.Id)));
                return null;
            }

            if (other.PlaneNumber == (planeNumber ^ 1))
            {
                context.Diagnostics.Add(new CompileDiagnostic(
                    MapLoadDiagnostics.MirroredPlane,
                    DiagnosticSeverity.Warning,
                    $"Brush {brush.Id}, Side {k}: mirrored plane",
                    new MapLocation(BrushId: brush.Id)));
                return null;
            }
        }

        side.PlaneNumber = planeNumber;

        if (!context.Options.OnlyEnts)
        {
            (side.TexInfo, td) = await TextureBuilder.TexinfoForBrushTextureAsync(
                    td,
                    Vec3.Zero,
                    context.TexInfos,
                    context.TexDatas,
                    context.Materials,
                    context.Diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // LoadSideCallback's "dispinfo" sub-chunk handler:
        // the displacement is read with the side and hangs off it.
        VmfChunk? dispinfo = chunk.GetChunk("dispinfo");
        if (dispinfo is not null)
        {
            side.Displacement = Disp.VmfDisplacementReader.Read(dispinfo);
        }

        map.AddBrushSide(side, td);
        brush.SideCount++;
        return side;
    }

    /// <summary>
    /// The contents of a whole brush: <c>BrushContents</c>,
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="brush">The brush.</param>
    /// <returns>The brush's contents.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// The base is the FIRST side's contents alone; the other sides contribute
    /// only through the union, and the union only matters for the four
    /// transparent bits. So a brush with one water side and five solid ones is
    /// solid-and-water-and-translucent, while a brush with one nodraw side
    /// first and five solid ones is whatever the nodraw side was.
    /// </remarks>
    public static int BrushContentsOf(MapFile map, MapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(brush);

        if (brush.SideCount == 0)
        {
            return 0;
        }

        int contents = map.BrushSides[brush.FirstSide].Contents;
        int union = contents;

        for (int i = 1; i < brush.SideCount; i++)
        {
            union |= map.BrushSides[brush.FirstSide + i].Contents;
        }

        int transparent = union & (int)(BrushContents.Window | BrushContents.Grate |
            BrushContents.Water | BrushContents.Slime);

        if (transparent != 0)
        {
            contents |= transparent | (int)BrushContents.Translucent;
            contents &= ~(int)BrushContents.Solid;
        }

        return contents;
    }

    /// <summary>
    /// Whether a classname is an areaportal: <c>IsAreaPortal</c>,
    /// </summary>
    /// <param name="className">The classname.</param>
    /// <returns>True when it starts with <c>func_areaportal</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="className"/> is null.</exception>
    /// <remarks>
    /// A PREFIX test, and case-sensitive: the hand-written loop compares
    /// characters until one differs or either string ends, then answers "did
    /// the prefix run out". So <c>func_areaportalwindow</c> is one and
    /// <c>Func_AreaPortal</c> is not — and, because the loop also stops when
    /// the CLASSNAME runs out, <c>func_area</c> is not one either.
    /// </remarks>
    public static bool IsAreaPortal(string className)
    {
        ArgumentNullException.ThrowIfNull(className);
        return className.StartsWith("func_areaportal", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether any of a brush's sides carries a displacement:
    /// <c>HasDispInfo</c>.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="brush">The brush.</param>
    /// <returns>True when a side has one.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static bool HasDisplacement(MapFile map, MapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(brush);

        for (int i = 0; i < brush.SideCount; i++)
        {
            if (map.BrushSides[brush.FirstSide + i].Displacement is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Forces <c>CONTENTS_WINDOW</c> on the brushes an areaportal window points
    /// at: <c>ForceFuncAreaPortalWindowContents</c>,
    /// </summary>
    /// <param name="map">The map.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    /// <remarks>
    /// Plain <c>func_areaportal</c> is excluded by name, and stock says why:
    /// those are tied to doors and should be opaque when closed, while a
    /// distance-based areaportal is normally open. The two keys followed are
    /// <c>target</c> and <c>BackgroundBModel</c>, in that order.
    /// </remarks>
    public static void ForceFuncAreaPortalWindowContents(MapFile map)
    {
        ArgumentNullException.ThrowIfNull(map);

        string[] targets = ["target", "BackgroundBModel"];

        // EntityByName's answers, built once on the first target that needs
        // one. The scan it replaces walked every entity's keys per target; a
        // map with dozens of areaportal windows and thousands of entities
        // spent tens of milliseconds here. Nothing in this pass changes a key,
        // only brush contents, so one index serves the whole pass. First
        // entity with the name wins, as the scan's does.
        Dictionary<string, MapEntity>? byName = null;

        foreach (MapEntity entity in map.Entities)
        {
            string className = entity.ValueForKey("classname");

            if (!IsAreaPortal(className) ||
                string.Equals(className, "func_areaportal", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string target in targets)
            {
                string name = entity.ValueForKey(target);
                if (name.Length == 0)
                {
                    continue;
                }

                byName ??= EntitiesByName(map);
                if (!byName.TryGetValue(name, out MapEntity? brushEntity))
                {
                    continue;
                }

                for (int i = 0; i < brushEntity.BrushCount; i++)
                {
                    MapBrush brush = map.Brushes[brushEntity.FirstBrush + i];
                    brush.Contents &= ~(int)BrushContents.Solid;
                    brush.Contents |= (int)(BrushContents.Translucent | BrushContents.Window);
                }
            }
        }
    }

    // Every entity by targetname, case-insensitively, the first of each name
    // kept: EntityByName for every name at once.
    private static Dictionary<string, MapEntity> EntitiesByName(MapFile map)
    {
        Dictionary<string, MapEntity> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (MapEntity entity in map.Entities)
        {
            byName.TryAdd(entity.ValueForKey("targetname"), entity);
        }

        return byName;
    }

    /// <summary>
    /// The first entity with a given <c>targetname</c>: <c>EntityByName</c>,
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="name">The target name, matched case-insensitively.</param>
    /// <returns>The entity, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public static MapEntity? EntityByName(MapFile map, string? name)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (name is null)
        {
            return null;
        }

        foreach (MapEntity entity in map.Entities)
        {
            if (string.Equals(entity.ValueForKey("targetname"), name, StringComparison.OrdinalIgnoreCase))
            {
                return entity;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes a brush entity's bounds onto it as six keys:
    /// <c>CMapFile::AddLadderKeys</c>.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="entity">The ladder entity.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// Formatted with <c>"%2.2f"</c>, which is two decimal places and a minimum
    /// field width of two — so the width never has any effect and the value is
    /// always <c>F2</c>. The six keys are set in mins-then-maxs order and
    /// <see cref="MapEntity.SetKeyValue(string, string)"/> prepends, so they
    /// come out reversed in the entity lump.
    /// </remarks>
    public static void AddLadderKeys(MapFile map, MapEntity entity)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(entity);

        Vec3 mins = MapFile.ClearedMins;
        Vec3 maxs = MapFile.ClearedMaxs;

        for (int i = 0; i < entity.BrushCount; i++)
        {
            MapBrush brush = map.Brushes[entity.FirstBrush + i];

            // AddPointToBounds of BOTH corners, which widens mins and maxs by
            // each in turn -- not "mins from mins, maxs from maxs".
            mins = Min(Min(mins, brush.Mins), brush.Maxs);
            maxs = Max(Max(maxs, brush.Mins), brush.Maxs);
        }

        entity.SetKeyValue("mins.x", F2(mins.X));
        entity.SetKeyValue("mins.y", F2(mins.Y));
        entity.SetKeyValue("mins.z", F2(mins.Z));
        entity.SetKeyValue("maxs.x", F2(maxs.X));
        entity.SetKeyValue("maxs.y", F2(maxs.Y));
        entity.SetKeyValue("maxs.z", F2(maxs.Z));
    }

    /// <summary>
    /// Rewrites a space-separated list of side ids as side indices:
    /// <c>ConvertSideList</c>.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="entity">The entity holding the list.</param>
    /// <param name="key">The key holding the list.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// An id that no side has is DROPPED from the list rather than kept or
    /// turned into -1, so the rewritten list can be shorter than the original
    /// and can be empty.
    /// </remarks>
    public static void ConvertSideList(MapFile map, MapEntity entity, string key)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(key);

        string list = entity.ValueForKey(key);
        List<string> indices = [];

        foreach (string token in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                continue;
            }

            int index = map.SideIdToIndex(id);
            if (index != -1)
            {
                indices.Add(index.ToString(CultureInfo.InvariantCulture));
            }
        }

        entity.SetKeyValue(key, string.Join(' ', indices));
    }

    /// <summary>
    /// The contents that make a surface visible:
    /// <c>ALL_VISIBLE_CONTENTS</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Spelled as stock spells it, and that is the point.</b> Stock writes
    /// <c>LAST_VISIBLE_CONTENTS | (LAST_VISIBLE_CONTENTS-1)</c> — a CONTIGUOUS
    /// mask of every bit up to and including <c>CONTENTS_OPAQUE</c>, whether or
    /// not that bit has a name today. Writing it instead as an OR of the named
    /// flags is how this constant came to be <c>0xBF</c>:
    /// <c>CONTENTS_BLOCKLOS</c> (<c>0x40</c>) sits inside the range and was not
    /// in the list, so the solid default fired for a
    /// <c>%compileBlockLOS</c> brush and gave it a <c>CONTENTS_SOLID</c> stock
    /// does not. Observed on <c>l1_tool_textures</c> brush 9: stock
    /// <c>0x8000040</c>, this port <c>0x8000041</c>.
    /// </para>
    /// <para>
    /// Not a compliance quirk. Stock's mask is the right one and the port's was
    /// simply a different number; both policies want <c>0xFF</c>.
    /// </para>
    /// </remarks>
    public const int AllVisibleContents =
        LastVisibleContents | (LastVisibleContents - 1);

    /// <summary>
    /// The highest contents bit that makes a surface visible:
    /// <c>LAST_VISIBLE_CONTENTS</c>.
    /// </summary>
    /// <remarks>
    /// <c>CONTENTS_OPAQUE</c>'s bit. Stock's own comment above the unused
    /// contents says a new visible bit is taken "from the top" and this
    /// constant updated with it, which is why
    /// <see cref="AllVisibleContents"/> is derived from it rather than listed.
    /// </remarks>
    public const int LastVisibleContents = (int)BrushContents.Opaque;

    /// <summary>
    /// The contents <c>-nowater</c> removes: <c>MASK_WATER</c>.
    /// </summary>
    public const int MaskWater =
        (int)(BrushContents.Water | BrushContents.Slime);

    private static string F2(float value) =>
        value.ToString("F2", CultureInfo.InvariantCulture);

    private static Vec3 Min(Vec3 a, Vec3 b) =>
        new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));

    private static Vec3 Max(Vec3 a, Vec3 b) =>
        new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));

    private static void LoadConnections(MapFile map, MapEntity entity, VmfChunk chunk)
    {
        IList<VmfNode> nodes = chunk.Children;
        for (int n = 0; n < nodes.Count; n++)
        {
            VmfNode node = nodes[n];
            if (node is not VmfKey key)
            {
                continue;
            }

            MapKeyValue pair = entity.AddKeyValue(key.Name, key.Value);

            // m_ConnectionPairs is a stack: newest first.
            map.ConnectionPairs.Insert(0, pair);
        }
    }

    /// <summary>
    /// Applies one side key: <c>LoadSideKeyCallback</c>,
    /// </summary>
    /// <param name="context">The compile.</param>
    /// <param name="side">The side being built.</param>
    /// <param name="td">The side's placement so far.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="planePoints">Receives the three plane points.</param>
    /// <param name="cancellationToken">Cancels the material read.</param>
    /// <returns>The placement with this key applied.</returns>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    /// <exception cref="MapCompileException">A value could not be parsed.</exception>
    /// <remarks>
    /// <para>
    /// The <c>material</c> key is the one with side effects: it runs the name
    /// through <c>-replacematerials</c>, classifies it through
    /// <see cref="TextureReferenceTable.FindMiptexAsync"/>, and then SETS —
    /// not ORs — the side's contents and surface from the result
    /// A <c>contents</c> or <c>flags</c> key
    /// earlier in the same side is therefore overwritten by a later
    /// <c>material</c>, which is why the keys are applied in file order.
    /// </para>
    /// <para>
    /// The placement comes back rather than being mutated because
    /// <see cref="BrushTexture"/> is a struct; the caller reassigns it.
    /// </para>
    /// </remarks>
    public static async ValueTask<BrushTexture> ApplySideKeyAsync(
        VbspContext context,
        MapBrushSide side,
        BrushTexture td,
        string key,
        string value,
        Vec3[] planePoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(planePoints);

        if (string.Equals(key, "plane", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParsePlanePoints(value, planePoints))
            {
                throw new MapCompileException("parsing plane definition");
            }
        }
        else if (string.Equals(key, "material", StringComparison.OrdinalIgnoreCase))
        {
            string name = context.MaterialReplacements is { } replacements
                ? replacements.Replace(value)
                : value;

            td.Name = name;

            int miptex = await context.TextureReferences
                .FindMiptexAsync(
                    name,
                    context.Materials,
                    context.MaterialOptions,
                    context.Diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);

            TextureReference reference = context.TextureReferences[miptex];
            td.Flags = reference.Flags;
            td.LightmapWorldUnitsPerLuxel = reference.LightmapWorldUnitsPerLuxel;
            side.Contents = reference.Contents;
            side.Surface = td.Flags;
        }
        else if (string.Equals(key, "uaxis", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseAxis(value, out Vec3 axis, out float shift, out float scale))
            {
                throw new MapCompileException("parsing U axis definition");
            }

            td.UAxis = axis;
            td.ShiftU = shift;
            td.TextureWorldUnitsPerTexelU = scale;
        }
        else if (string.Equals(key, "vaxis", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseAxis(value, out Vec3 axis, out float shift, out float scale))
            {
                throw new MapCompileException("parsing V axis definition");
            }

            td.VAxis = axis;
            td.ShiftV = shift;
            td.TextureWorldUnitsPerTexelV = scale;
        }
        else if (string.Equals(key, "contents", StringComparison.OrdinalIgnoreCase))
        {
            side.Contents |= VmfValue.ParseInt(value);
        }
        else if (string.Equals(key, "id", StringComparison.OrdinalIgnoreCase))
        {
            side.Id = VmfValue.ParseInt(value);
        }
        else if (string.Equals(key, "smoothing_groups", StringComparison.OrdinalIgnoreCase))
        {
            side.SmoothingGroups = unchecked((uint)VmfValue.ParseInt(value));
        }
        else if (string.Equals(key, "lightmapscale", StringComparison.OrdinalIgnoreCase))
        {
            // atoi, NOT atof: "16.5" is 16.
            float luxel = VmfValue.ParseInt(value);

            if (luxel == 0f)
            {
                context.Diagnostics.Add(new CompileDiagnostic(
                    TextureDiagnostics.LuxelSizeZero,
                    DiagnosticSeverity.Warning,
                    "luxel size of 0"));
                luxel = context.DefaultLuxelSize;
            }

            luxel *= context.Options.LuxelScale;

            if (luxel < context.Options.MinLuxelScale)
            {
                luxel = context.Options.MinLuxelScale;
            }

            td.LightmapWorldUnitsPerLuxel = luxel;
        }
        else if (string.Equals(key, "flags", StringComparison.OrdinalIgnoreCase))
        {
            td.Flags |= VmfValue.ParseInt(value);

            // ASSIGNMENT, not an OR: a flags key replaces whatever the
            // material contributed to the side's surf.
            side.Surface = td.Flags;
        }

        return td;
    }

    // "(x y z) (x y z) (x y z)": each parenthesised group split on single
    // spaces, empties dropped, exactly three fields, each through atof.
    //
    // Scanned in place rather than cut up. The obvious spelling -- slice the
    // group out, Split it, parse each piece -- allocates a substring, a string
    // array and three more substrings per point, nine points per side; on a
    // full-size map that was the largest allocator in the whole load. The
    // field rules are the Split rules exactly: only ' ' separates (a tab stays
    // inside its field, where atof stops at it), and runs of spaces make no
    // empty fields. A point is written as soon as its group parses, so a
    // failure in the third group leaves the first two written, as before.
    internal static bool TryParsePlanePoints(string value, Vec3[] points)
    {
        int position = 0;
        Span<float> fields = stackalloc float[3];

        for (int i = 0; i < 3; i++)
        {
            int open = value.IndexOf('(', position);
            int close = open < 0 ? -1 : value.IndexOf(')', open);

            if (open < 0 || close < 0)
            {
                return false;
            }

            ReadOnlySpan<char> group = value.AsSpan(open + 1, close - open - 1);
            int count = 0;
            int at = 0;

            while (at < group.Length)
            {
                if (group[at] == ' ')
                {
                    at++;
                    continue;
                }

                int length = group[at..].IndexOf(' ');
                if (length < 0)
                {
                    length = group.Length - at;
                }

                if (count == 3)
                {
                    // A fourth field: Split would have made four parts.
                    return false;
                }

                fields[count++] = VmfValue.ParseFloat(group.Slice(at, length));
                at += length;
            }

            if (count != 3)
            {
                return false;
            }

            points[i] = new Vec3(fields[0], fields[1], fields[2]);
            position = close + 1;
        }

        return true;
    }

    // sscanf("[%f %f %f %f] %f") -- all five fields, or the key is an error
    // The bracketed four are the axis and the shift; the
    // trailing one is world units per texel. The tail after the LAST ']' is
    // trimmed of whitespace and must not be empty; it is read in place, as
    // the plane's fields are, for the same reason.
    internal static bool TryParseAxis(string value, out Vec3 axis, out float shift, out float scale)
    {
        axis = default;
        shift = 0f;
        scale = 0f;

        if (!VmfValue.TryParseVector4(value.AsSpan(), out (float X, float Y, float Z, float W) vector))
        {
            return false;
        }

        int close = value.LastIndexOf(']');
        if (close < 0)
        {
            return false;
        }

        ReadOnlySpan<char> tail = value.AsSpan(close + 1).Trim();
        if (tail.IsEmpty)
        {
            return false;
        }

        axis = new Vec3(vector.X, vector.Y, vector.Z);
        shift = vector.W;
        scale = VmfValue.ParseFloat(tail);
        return true;
    }

    private static async Task FinishEntityAsync(
        VbspContext context,
        MapFile map,
        MapEntity entity,
        CancellationToken cancellationToken)
    {
        entity.Origin = entity.GetVectorForKey("origin");

        if (ShouldCullForDxLevel(context, entity))
        {
            entity.Clear();
            return;
        }

        if (entity.Origin.X != 0f || entity.Origin.Y != 0f || entity.Origin.Z != 0f)
        {
            await RebuildForOriginAsync(context, map, entity, cancellationToken).ConfigureAwait(false);
        }

        string className = entity.ValueForKey("classname");

        if (string.Equals(className, "func_detail", StringComparison.Ordinal))
        {
            map.MoveBrushesToWorld(entity);
            entity.Clear();
            return;
        }

        if (string.Equals(className, "func_viscluster", StringComparison.Ordinal))
        {
            // The volume is built now, not at portal time: clipping it looks
            // up the coordinate box's planes, and the first lookup appends
            // them to the plane table at this point in the load. Then the
            // entity goes, brushes and keys both: it is an instruction to vbsp
            // and nothing in the game reads it.
            map.VisClusterEntities.Add(map.Entities.Count - 1);
            map.VisClusters.Add(new Csg.BspBuildContext(context, map), entity.FirstBrush, entity.BrushCount);
            entity.Clear();
            return;
        }

        if (string.Equals(className, "func_ladder", StringComparison.Ordinal))
        {
            AddLadderKeys(map, entity);
            map.MoveBrushesToWorld(entity);
            entity.SetKeyValue("classname", "info_ladder");
            return;
        }

        if (string.Equals(className, "env_cubemap", StringComparison.Ordinal))
        {
            if (context.Options.DxLevel == 0 || context.Options.DxLevel >= 70)
            {
                context.CubemapSamples.Add(new CubemapSample(
                    entity.Origin,
                    entity.IntForKey("cubemapsize"),
                    entity.ValueForKey("sides")));
            }

            entity.Clear();
            return;
        }

        if (string.Equals(className, "test_sidelist", StringComparison.Ordinal))
        {
            ConvertSideList(map, entity, "sides");
            return;
        }

        if (string.Equals(className, "info_overlay", StringComparison.Ordinal))
        {
            // Overlay_GetFromEntity parses the overlay and
            // converts the entity to info_overlay_accessor. Overlays are a
            // later lane's; the entity number is recorded so that lane has
            // its list and nothing is silently dropped here.
            map.OverlayEntities.Add(map.Entities.Count - 1);
            return;
        }

        if (string.Equals(className, "info_overlay_transition", StringComparison.Ordinal))
        {
            entity.Clear();
            return;
        }

        if (string.Equals(className, "info_no_dynamic_shadow", StringComparison.OrdinalIgnoreCase))
        {
            HandleNoDynamicShadowsEntity(map, entity);
            return;
        }

        if (string.Equals(className, "func_instance_parms", StringComparison.OrdinalIgnoreCase))
        {
            entity.Clear();
            return;
        }

        if (IsAreaPortal(className))
        {
            if (entity.BrushCount != 1)
            {
                throw new MapCompileException(
                    $"Entity {map.Entities.Count - 1}: func_areaportal can only be a single brush");
            }

            map.Brushes[^1].Contents = (int)BrushContents.AreaPortal;
            context.AreaPortalCount++;
            entity.AreaPortalNumber = context.AreaPortalCount;
            entity.SetKeyValue("portalnumber", context.AreaPortalCount);
            map.MoveBrushesToWorld(entity);
            return;
        }

        if (!ReferenceEquals(entity, map.Entities[0]))
        {
            map.RemoveContentsDetailFromEntity(entity);
        }
    }

    private static bool ShouldCullForDxLevel(VbspContext context, MapEntity entity)
    {
        string minText = entity.ValueForKey("mindxlevel");
        string maxText = entity.ValueForKey("maxdxlevel");

        if (minText.Length == 0 && maxText.Length == 0)
        {
            return false;
        }

        int min = minText.Length > 0 ? VmfValue.ParseInt(minText) : 0;
        int max = maxText.Length > 0 ? VmfValue.ParseInt(maxText) : 0;

        if (min == 0)
        {
            min = context.Options.DxLevel;
        }

        if (max == 0)
        {
            max = context.Options.DxLevel;
        }

        return context.Options.DxLevel != 0 &&
            (context.Options.DxLevel < min || context.Options.DxLevel > max);
    }

    private static async Task RebuildForOriginAsync(
        VbspContext context,
        MapFile map,
        MapEntity entity,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < entity.BrushCount; i++)
        {
            MapBrush brush = map.Brushes[entity.FirstBrush + i];

            for (int j = 0; j < brush.SideCount; j++)
            {
                int sideIndex = brush.FirstSide + j;
                MapBrushSide side = map.BrushSides[sideIndex];

                Plane plane = map.Planes[side.PlaneNumber];
                float newDist = plane.Dist - Vec3.Dot(plane.Normal, entity.Origin);
                side.PlaneNumber = map.Planes.Find(plane.Normal, newDist);

                if (context.Options.OnlyEnts)
                {
                    continue;
                }

                // The ORIGINAL placement, kept in side_brushtextures, is what
                // the rebuilt texinfo is computed from -- not
                // the one the first pass already baked an origin into, because
                // the first pass baked vec3_origin.
                (side.TexInfo, BrushTexture fixedUp) = await TextureBuilder
                    .TexinfoForBrushTextureAsync(
                        map.SideBrushTextures[sideIndex],
                        entity.Origin,
                        context.TexInfos,
                        context.TexDatas,
                        context.Materials,
                        context.Diagnostics,
                        cancellationToken)
                    .ConfigureAwait(false);

                map.SetSideBrushTexture(sideIndex, fixedUp);
            }

            map.MakeBrushWindings(brush, context.Diagnostics);
        }
    }

    private static void HandleNoDynamicShadowsEntity(MapFile map, MapEntity entity)
    {
        string sides = entity.ValueForKey("sides");

        foreach (string token in sides.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                continue;
            }

            if (!map.NoDynamicShadowSides.Contains(id))
            {
                map.NoDynamicShadowSides.Add(id);
            }
        }

        entity.Clear();
    }
}

/// <summary>
/// One <c>env_cubemap</c> the map placed: <c>Cubemap_InsertSample</c>,
/// </summary>
/// <param name="Origin">Where the sample is taken.</param>
/// <param name="Size">The <c>cubemapsize</c> key, or zero for the default.</param>
/// <param name="Sides">
/// The raw <c>sides</c> key. The brush sides it names are attached to the
/// sample by <c>Cubemap_SaveBrushSides</c>, which is pipeline work and not this
/// lane's; the string is kept so nothing is lost.
/// </param>
public readonly record struct CubemapSample(Vec3 Origin, int Size, string Sides);
