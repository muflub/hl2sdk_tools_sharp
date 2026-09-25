using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// <c>CMapFile::MergeInstance</c> and the five merges under it:
/// </summary>
/// <remarks>
/// <para>
/// A <c>func_instance</c> is a whole second VMF, loaded into its own
/// <see cref="MapFile"/> and then folded into the host at the instance
/// entity's origin and angles. The five merges run in a fixed order — planes,
/// brushes, sides, entities, overlays — and the
/// order is load-bearing twice over: the brush merge rebases side ranges that
/// the side merge then walks, and the entity merge's
/// <c>MoveBrushesToWorldGeneral</c> at its end assumes the brushes are already
/// in place.
/// </para>
/// <para>
/// <b>What is transformed and what is not.</b> Only the instance's WORLDSPAWN
/// brushes and its ladder brushes move (and
///); a brush belonging to any other entity of the instance keeps
/// its coordinates and is moved by that entity's own origin instead. That
/// asymmetry is stock's and is why the side merge has two separate loops
/// looking for which range a side falls in.
/// </para>
/// <para>
/// Name fixup (<c>GD.RemapKeyValue</c>) needs an FGD, which this port does not
/// read. Instances written by a manifest use <c>fixup_style 2</c>
/// (<c>NAME_FIXUP_NONE</c>), so for those the FGD changes nothing — and
/// <see cref="MergeAsync"/> reports when a non-NONE style was asked for and
/// could not be honoured, rather than silently producing unfixed names.
/// </para>
/// </remarks>
public static class MapInstanceMerger
{
    /// <summary>
    /// The key prefix a <c>func_instance</c> uses to substitute values into
    /// its contents: <c>INSTANCE_VARIABLE_KEY</c>.
    /// </summary>
    public const string InstanceVariableKey = "replace";

    /// <summary>
    /// The <c>fixup_style</c> that renames nothing:
    /// <c>GameData::NAME_FIXUP_NONE</c>.
    /// </summary>
    public const int NameFixupNone = 2;

    /// <summary>
    /// Merges a loaded instance into a host map: <c>MergeInstance</c>,
    /// </summary>
    /// <param name="context">The compile.</param>
    /// <param name="host">The map the instance is folded into.</param>
    /// <param name="instanceEntity">The host's <c>func_instance</c> entity.</param>
    /// <param name="instance">The loaded instance.</param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>A task.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static async Task MergeAsync(
        VbspContext context,
        MapFile host,
        MapEntity instanceEntity,
        MapFile instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(instanceEntity);
        ArgumentNullException.ThrowIfNull(instance);

        context.InstanceCount++;

        Vec3 origin = instanceEntity.Origin;
        Vec3 angles = instanceEntity.GetVectorForKey("angles");
        InstanceTransform transform = InstanceTransform.FromAngles(angles, origin);

        MergePlanes(host, instance);
        MergeBrushes(host, instance, transform);
        await MergeBrushSidesAsync(context, host, instance, origin, transform, cancellationToken)
            .ConfigureAwait(false);
        MergeEntities(context, host, instanceEntity, instance, transform);
    }

    /// <summary>
    /// Re-adds every plane PAIR of the instance to the host's table:
    /// <c>MergePlanes</c>.
    /// </summary>
    /// <param name="host">The host map.</param>
    /// <param name="instance">The instance.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// <para>
    /// The step is 2 and only the FRONT half of each pair is offered, because
    /// <see cref="PlaneTable.Find"/> appends both halves itself. Offering both
    /// would double the table.
    /// </para>
    /// <para>
    /// <b>The planes are added UNTRANSFORMED.</b> The instance's planes go into
    /// the host's table in the instance's own coordinates, and the transformed
    /// ones are added separately by the side merge — so an instance
    /// contributes up to twice as many planes as it has, and the untransformed
    /// copies stay in the host's table whether anything references them or not.
    /// That is stock's, and since plane numbers are the output it cannot be
    /// optimised away.
    /// </para>
    /// </remarks>
    public static void MergePlanes(MapFile host, MapFile instance)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(instance);

        for (int i = 0; i < instance.Planes.Count; i += 2)
        {
            Plane plane = instance.Planes[i];
            host.Planes.Find(plane.Normal, plane.Dist);
        }
    }

    private static void MergeBrushes(MapFile host, MapFile instance, InstanceTransform transform)
    {
        int maxBrushId = 0;
        foreach (MapBrush brush in host.Brushes)
        {
            maxBrushId = Math.Max(maxBrushId, brush.Id);
        }

        int hostBrushCount = host.BrushCount;
        int hostSideCount = host.BrushSideCount;
        int hostEntityCount = host.Entities.Count;
        int instanceWorldBrushes = instance.Entities.Count > 0 ? instance.Entities[0].BrushCount : 0;

        for (int i = 0; i < instance.BrushCount; i++)
        {
            MapBrush source = instance.Brushes[i];

            MapBrush brush = new()
            {
                EntityNumber = source.EntityNumber + hostEntityCount,
                BrushNumber = source.BrushNumber + hostBrushCount,
                Id = source.Id + maxBrushId,
                Contents = source.Contents,
                Mins = source.Mins,
                Maxs = source.Maxs,
                SideCount = source.SideCount,
                FirstSide = hostSideCount + source.FirstSide,
            };

            // Worldspawn brushes and ladders are physically
            // moved; everything else keeps its coordinates and is placed by
            // its own entity's origin instead.
            if (i < instanceWorldBrushes ||
                (brush.Contents & (int)Materials.BrushContents.Ladder) != 0)
            {
                (Vec3 mins, Vec3 maxs) = transform.TransformBounds(source.Mins, source.Maxs);
                brush.Mins = mins;
                brush.Maxs = maxs;
            }

            host.Brushes.Add(brush);
        }
    }

    private static async Task MergeBrushSidesAsync(
        VbspContext context,
        MapFile host,
        MapFile instance,
        Vec3 origin,
        InstanceTransform transform,
        CancellationToken cancellationToken)
    {
        int maxSideId = 0;
        foreach (MapBrushSide side in host.BrushSides)
        {
            maxSideId = Math.Max(maxSideId, side.Id);
        }

        int hostEntityCount = host.Entities.Count;
        int instanceWorldBrushes = instance.Entities.Count > 0 ? instance.Entities[0].BrushCount : 0;

        for (int i = 0; i < instance.BrushSideCount; i++)
        {
            MapBrushSide source = instance.BrushSides[i];

            MapBrushSide side = new()
            {
                TexInfo = source.TexInfo,
                Displacement = source.Displacement,
                Winding = source.Winding,
                Contents = source.Contents,
                Surface = source.Surface,
                Visible = source.Visible,
                Tested = source.Tested,
                Bevel = source.Bevel,
                Id = source.Id + maxSideId,
                SmoothingGroups = source.SmoothingGroups,
                DynamicShadowsEnabled = source.DynamicShadowsEnabled,
            };

            // The instance's planes were re-added to the host's table by
            // MergePlanes, so the plane NUMBER has to be looked up again --
            // Stock's comment says an index map would be
            // faster and that it did not build one.
            Plane sourcePlane = instance.Planes[source.PlaneNumber];
            side.PlaneNumber = host.Planes.Find(sourcePlane.Normal, sourcePlane.Dist);

            BrushTexture texture = instance.SideBrushTextures[i];

            if (NeedsTranslation(instance, i, instanceWorldBrushes, source))
            {
                if (!side.Winding.IsNull)
                {
                    Span<Vec3> points = host.Windings.Points(side.Winding);
                    for (int p = 0; p < points.Length; p++)
                    {
                        points[p] = transform.TransformPoint(points[p]);
                    }
                }

                Plane transformed = transform.TransformPlane(host.Planes[side.PlaneNumber]);
                side.PlaneNumber = host.Planes.Find(transformed.Normal, transformed.Dist);

                texture.UAxis = transform.RotateVector(texture.UAxis);
                texture.VAxis = transform.RotateVector(texture.VAxis);
                texture.ShiftU -= Vec3.Dot(origin, texture.UAxis) / texture.TextureWorldUnitsPerTexelU;
                texture.ShiftV -= Vec3.Dot(origin, texture.VAxis) / texture.TextureWorldUnitsPerTexelV;

                if (!context.Options.OnlyEnts)
                {
                    (side.TexInfo, _) = await TextureBuilder.TexinfoForBrushTextureAsync(
                            texture,
                            Vec3.Zero,
                            context.TexInfos,
                            context.TexDatas,
                            context.Materials,
                            context.Diagnostics,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (side.Displacement is { } displacement)
            {
                displacement.BrushSideId = side.Id;
                displacement.EntityNumber += hostEntityCount;
            }

            host.AddBrushSide(side, texture);
        }
    }

    // Three ways a side needs moving, tested in this order:
    // it carries a worldspawn displacement, it belongs to a worldspawn brush,
    // or it belongs to a ladder brush outside worldspawn.
    private static bool NeedsTranslation(
        MapFile instance,
        int sideIndex,
        int instanceWorldBrushes,
        MapBrushSide side)
    {
        if (side.Displacement is { EntityNumber: 0 })
        {
            return true;
        }

        for (int j = 0; j < instanceWorldBrushes; j++)
        {
            MapBrush brush = instance.Brushes[j];
            if (sideIndex >= brush.FirstSide && sideIndex < brush.FirstSide + brush.SideCount)
            {
                return true;
            }
        }

        for (int j = instanceWorldBrushes; j < instance.BrushCount; j++)
        {
            MapBrush brush = instance.Brushes[j];

            if (sideIndex >= brush.FirstSide &&
                sideIndex < brush.FirstSide + brush.SideCount &&
                (brush.Contents & (int)Materials.BrushContents.Ladder) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void MergeEntities(
        VbspContext context,
        MapFile host,
        MapEntity instanceEntity,
        MapFile instance,
        InstanceTransform transform)
    {
        int maxEntityId = 0;
        foreach (MapEntity entity in host.Entities)
        {
            maxEntityId = Math.Max(maxEntityId, entity.IntForKey("hammerid"));
        }

        int fixupStyle = instanceEntity.IntForKey("fixup_style");

        if (fixupStyle != NameFixupNone)
        {
            context.Diagnostics.Add(new Diagnostics.CompileDiagnostic(
                MapLoadDiagnostics.InstanceNameFixupUnsupported,
                Diagnostics.DiagnosticSeverity.Warning,
                $"func_instance asked for fixup_style {fixupStyle}; this compiler reads no "
                    + "FGD, so target names inside the instance are left as written"));
        }

        int hostBrushCount = host.BrushCount;
        MapEntity? worldspawn = null;

        for (int i = 0; i < instance.Entities.Count; i++)
        {
            MapEntity source = instance.Entities[i];
            MapEntity entity = new()
            {
                FirstBrush = source.FirstBrush + (hostBrushCount - instance.BrushCount),
                BrushCount = source.BrushCount,
                Origin = source.Origin,
                AreaPortalNumber = source.AreaPortalNumber,
            };

            foreach (MapKeyValue pair in source.Pairs)
            {
                entity.Pairs.Add(new MapKeyValue(pair.Key, pair.Value));
            }

            host.Entities.Add(entity);

            if (entity.HasKey("hammerid"))
            {
                entity.SetKeyValue(
                    "hammerid",
                    (entity.IntForKey("hammerid") + maxEntityId).ToString(CultureInfo.InvariantCulture));
            }

            if (string.Equals(
                    entity.ValueForKey("classname"), "worldspawn", StringComparison.OrdinalIgnoreCase))
            {
                worldspawn = entity;
                continue;
            }

            entity.Origin = transform.TransformPoint(source.Origin);

            foreach (MapKeyValue pair in entity.Pairs)
            {
                pair.Value = ReplaceInstanceVariables(pair.Value, instanceEntity);
            }

            if (string.Equals(
                    entity.ValueForKey("classname"),
                    "func_simpleladder",
                    StringComparison.OrdinalIgnoreCase))
            {
                MapFileLoader.AddLadderKeys(host, entity);
            }
        }

        foreach (MapKeyValue pair in instance.ConnectionPairs)
        {
            pair.Value = ReplaceInstanceVariables(pair.Value, instanceEntity);
        }

        if (worldspawn is not null)
        {
            host.MoveBrushesToWorldGeneral(worldspawn);
            worldspawn.Clear();
        }
    }

    /// <summary>
    /// Substitutes a <c>func_instance</c>'s <c>replace</c> variables into one
    /// value: <c>ReplaceInstancePair</c>.
    /// </summary>
    /// <param name="value">The value to substitute into.</param>
    /// <param name="instanceEntity">The <c>func_instance</c>.</param>
    /// <returns>The substituted value.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    /// Each <c>replace*</c> key's value is a variable name, a SPACE, and the
    /// replacement; a value with no space is skipped. Substitutions are applied
    /// in key order and each sees the previous one's output, so they chain.
    /// </remarks>
    public static string ReplaceInstanceVariables(string value, MapEntity instanceEntity)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(instanceEntity);

        string result = value;

        foreach (MapKeyValue pair in instanceEntity.Pairs)
        {
            if (!pair.Key.StartsWith(InstanceVariableKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int space = pair.Value.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                continue;
            }

            string variable = pair.Value[..space];
            string replacement = pair.Value[(space + 1)..];

            if (variable.Length > 0)
            {
                result = result.Replace(variable, replacement, StringComparison.Ordinal);
            }
        }

        return result;
    }
}
