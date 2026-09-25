using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>CreateDirectLights</c> and the four
/// <c>ParseLight*</c> functions it dispatches to.
/// </summary>
/// <remarks>
/// <para>
/// Turns patches and entities into the flat list every sample iterates. Two
/// halves that look unrelated and are not: the patch half produces
/// <see cref="EmitType.Surface"/> lights, so <b>every texlight in a map is a
/// consequence of subdivision</b> -- change the chop and the number of surface
/// lights changes with it, each one dimmer.
/// </para>
/// <para>
/// <b>The entity filter is a PREFIX test.</b> <c>strncmp(name, "light", 5)</c>
/// Accepts <c>light</c>, <c>light_spot</c>,
/// <c>light_environment</c>, <c>light_dynamic</c> -- which is then explicitly
/// skipped -- and also <c>lightmap_something</c> or any other classname that
/// happens to start with those five letters, which reach the
/// <c>unsupported light entity</c> branch. Reproduced.
/// </para>
/// </remarks>
public static class DirectLightBuilder
{
    /// <summary>
    /// <c>dlight_threshold</c>: 0.1, the average
    /// emissivity a patch needs before it becomes a light.
    /// </summary>
    public const float DLightThreshold = 0.1f;

    /// <summary>
    /// <c>lightscale</c>: 1.0, and not settable in a
    /// release build.
    /// </summary>
    public const float LightScale = 1.0f;

    /// <summary>
    /// The smallest <c>basearea</c> a patch may have and still emit:
    /// <c>1e-6</c>.
    /// </summary>
    /// <remarks>
    /// <c>basearea</c> is the material's texel count, so this rejects a
    /// material whose VTF reported a zero dimension -- which would otherwise
    /// divide by zero two lines later.
    /// </remarks>
    public const float MinBaseArea = 1e-6f;

    /// <summary>
    /// The inner cone angle a <c>light_spot</c> gets when it names none: 10
    /// Degrees.
    /// </summary>
    public const float DefaultInnerCone = 10f;

    /// <summary>
    /// The cone angle DirectX 8 can express, which stock clamps to: 90 degrees
    /// </summary>
    public const float MaxConeAngle = 90f;

    /// <summary>
    /// Builds every light in a map.
    /// </summary>
    /// <param name="geometry">The map's lumps.</param>
    /// <param name="patches">The subdivided patch set.</param>
    /// <param name="entities">The entity lump, in file order.</param>
    /// <param name="visibility">The PVS accessor.</param>
    /// <param name="tree">The compiled tree.</param>
    /// <param name="options">HDR, -scale, -dlight, -softsun and the normalise fork.</param>
    /// <param name="skyVis">
    /// The sky-visibility pass, or null to skip it. See
    /// <see cref="SkyLeafVisibility"/> for what skipping costs.
    /// </param>
    /// <returns>The lights, in stock's order.</returns>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    public static DirectLightSet Build(
        LightGeometry geometry,
        PatchSet patches,
        IReadOnlyList<BspEntity> entities,
        LightVisibility visibility,
        CompiledBspTree tree,
        DirectLightOptions? options = null,
        SkyLeafVisibility? skyVis = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(tree);

        options ??= new DirectLightOptions();
        DirectLightSet lights = new() { SunAngularExtent = options.SunAngularExtent };

        AddSurfaceLights(patches, visibility, tree, lights, options);

        for (int i = 0; i < entities.Count; i++)
        {
            BspEntity entity = entities[i];
            string name = EntityKeys.ValueForKey(entity, "classname");

            if (!name.StartsWith("light", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(name, "light_dynamic", StringComparison.Ordinal))
            {
                // A real game entity, not a compile-time light.
                continue;
            }

            switch (name)
            {
                case "light_spot":
                    ParseLightSpot(entities, entity, visibility, tree, lights, options);
                    break;
                case "light_environment":
                    ParseLightEnvironment(
                        geometry, entities, entity, visibility, tree, lights, options, skyVis);
                    break;
                case "light":
                    ParseLightPoint(entities, entity, visibility, tree, lights, options);
                    break;
                default:
                    lights.UnsupportedClassNames.Add(name);
                    break;
            }
        }

        return lights;
    }

    private static void AddSurfaceLights(
        PatchSet patches,
        LightVisibility visibility,
        CompiledBspTree tree,
        DirectLightSet lights,
        DirectLightOptions options)
    {
        // In PATCH INDEX order -- so the list ends up
        // in reverse patch order, which is the order the worldlights lump
        // records.
        for (int i = 0; i < patches.Count; i++)
        {
            ref readonly Patch patch = ref patches.At(i);

            // Only leaves, and only materials with a real texel
            // area.
            if (patch.HasChildren || patch.BaseArea < 1e-6)
            {
                continue;
            }

            // VectorAvg, the arithmetic mean of the three channels.
            float average = (patch.BaseLight.X + patch.BaseLight.Y + patch.BaseLight.Z) / 3f;
            if (average < options.DLightThreshold)
            {
                continue;
            }

            DirectLight light = lights.Alloc(visibility, tree, patch.Origin, addToList: true);
            light.Type = EmitType.Surface;
            light.Normal = patch.Normal;

            // The texture SCALE multiplies in here, which is why
            // -notexscale changes every texlight's brightness: a patch's
            // emission is per texture instance, so a stretched texture emits
            // proportionally more.
            float scale = options.LightScale * patch.Area * patch.ScaleS * patch.ScaleT / patch.BaseArea;
            light.Intensity = patch.BaseLight * scale;

            light.Intensity *= LightConstants.DirectScale;

            lights.SurfaceLights++;
        }
    }

    /// <summary>
    /// <c>ParseLightGeneric</c>: style, colour and
    /// direction, shared by all three entity kinds.
    /// </summary>
    /// <param name="entities">The entity list, for target lookup.</param>
    /// <param name="entity">The light entity.</param>
    /// <param name="light">The light being filled in.</param>
    /// <param name="lights">Where warnings go.</param>
    /// <param name="options">HDR, -scale, -dlight, -softsun and the normalise fork.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static void ParseGeneric(
        IReadOnlyList<BspEntity> entities,
        BspEntity entity,
        DirectLight light,
        DirectLightSet lights,
        DirectLightOptions options)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(light);
        ArgumentNullException.ThrowIfNull(lights);

        // The style is read as a FLOAT and truncated, so "style" "1.9"
        // is style 1.
        light.Style = (int)EntityKeys.FloatForKey(entity, "style");

        // 1133-1139. _lightHDR wins only if it parses; a present-but-broken
        // one falls through to _light, which re-zeroes and re-parses.
        RadLightOptions radOptions = new() { Hdr = options.Hdr, LightScale = options.LightScale };
        bool haveHdr = false;
        if (options.Hdr)
        {
            (Vec3 hdrIntensity, bool parsed) = RadLightFile.ParseIntensity(
                EntityKeys.ValueForKey(entity, "_lightHDR"), radOptions);
            if (parsed)
            {
                light.Intensity = hdrIntensity;
                haveHdr = true;
            }
        }

        if (!haveHdr)
        {
            (Vec3 intensity, _) = RadLightFile.ParseIntensity(
                EntityKeys.ValueForKey(entity, "_light"), radOptions);
            light.Intensity = intensity;
        }

        string target = EntityKeys.ValueForKey(entity, "target");
        if (target.Length != 0)
        {
            // Point at the target's origin. A missing target
            // warns and leaves the normal at ZERO, which for a spotlight means
            // every dot product is zero and the light contributes nothing.
            BspEntity? targetEntity = FindTargetEntity(entities, target);
            if (targetEntity is null)
            {
                lights.Warnings.Add(
                    $"WARNING: light at ({(int)light.Origin.X} {(int)light.Origin.Y} "
                    + $"{(int)light.Origin.Z}) has missing target");
            }
            else
            {
                Vec3 dest = EntityKeys.GetVectorForKey(targetEntity, "origin");
                light.Normal = BumpBasis.Normalise(
                    dest - light.Origin, options.Compliance.Emulates(StockQuirk.VradVectorNormalise));
            }
        }
        else
        {
            Vec3 angles = EntityKeys.GetVectorForKey(entity, "angles");
            float pitch = EntityKeys.FloatForKey(entity, "pitch");
            float angle = EntityKeys.FloatForKey(entity, "angle");
            light.Normal = LightNormals.FromProps(
                angles,
                angle,
                pitch,
                options.Compliance.Emulates(StockQuirk.CrtCosineAtRightAngle),
                options.Compliance.Emulates(StockQuirk.DegreesToRadiansByReciprocal));
        }

        // Applied AFTER the direction, and to whichever intensity
        // was chosen -- including the LDR one, when _lightHDR was absent.
        if (options.Hdr)
        {
            light.Intensity *= EntityKeys.FloatForKeyWithDefault(entity, "_lightscaleHDR", 1.0f);
        }
    }

    /// <summary>
    /// <c>FindTargetEntity</c>.
    /// </summary>
    /// <param name="entities">The entity list.</param>
    /// <param name="target">The <c>targetname</c> to find.</param>
    /// <returns>The first entity with that name, or null.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static BspEntity? FindTargetEntity(IReadOnlyList<BspEntity> entities, string target)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(target);

        foreach (BspEntity entity in entities)
        {
            if (string.Equals(EntityKeys.ValueForKey(entity, "targetname"), target, StringComparison.Ordinal))
            {
                return entity;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>SetLightFalloffParams</c> with the two stock defects its solve carries.
    /// </summary>
    private static void ApplyFalloff(BspEntity entity, DirectLight light, DirectLightSet lights, DirectLightOptions options) =>
        LightFalloff.Apply(
            entity,
            light,
            lights.Warnings,
            options.Compliance.Emulates(StockQuirk.InverseQuadraticReciprocal),
            options.Compliance.Emulates(StockQuirk.MonotonicDerivativeAtOne));

    private static void ParseLightPoint(
        IReadOnlyList<BspEntity> entities,
        BspEntity entity,
        LightVisibility visibility,
        CompiledBspTree tree,
        DirectLightSet lights,
        DirectLightOptions options)
    {
        Vec3 dest = EntityKeys.GetVectorForKey(entity, "origin");
        DirectLight light = lights.Alloc(visibility, tree, dest, addToList: true);

        ParseGeneric(entities, entity, light, lights, options);
        light.Type = EmitType.Point;
        ApplyFalloff(entity, light, lights, options);
    }

    private static void ParseLightSpot(
        IReadOnlyList<BspEntity> entities,
        BspEntity entity,
        LightVisibility visibility,
        CompiledBspTree tree,
        DirectLightSet lights,
        DirectLightOptions options)
    {
        Vec3 dest = EntityKeys.GetVectorForKey(entity, "origin");
        DirectLight light = lights.Alloc(visibility, tree, dest, addToList: true);

        ParseGeneric(entities, entity, light, lights, options);
        light.Type = EmitType.Spotlight;

        // Both cones are ANGLES here and become
        // COSINES below, so anything reading them between the two is reading a
        // different quantity.
        light.StopDot = EntityKeys.FloatForKey(entity, "_inner_cone");
        if (light.StopDot == 0f)
        {
            light.StopDot = DefaultInnerCone;
        }

        light.StopDot2 = EntityKeys.FloatForKey(entity, "_cone");
        if (light.StopDot2 == 0f)
        {
            light.StopDot2 = light.StopDot;
        }

        if (light.StopDot2 < light.StopDot)
        {
            light.StopDot2 = light.StopDot;
        }

        if (light.StopDot == 180f && light.StopDot2 == 180f)
        {
            // A fully-open spotlight is a point light, and its
            // cone values are zeroed rather than converted -- so a
            // worldlights lump records stopdot 0 for it, not cos(180) = -1.
            light.StopDot = 0;
            light.StopDot2 = 0;
            light.Type = EmitType.Point;
            light.Exponent = 0;
        }
        else
        {
            if (light.StopDot > MaxConeAngle)
            {
                lights.Warnings.Add(
                    $"WARNING: light_spot at ({(int)light.Origin.X} {(int)light.Origin.Y} "
                    + $"{(int)light.Origin.Z}) has inner angle larger than 90 degrees! "
                    + "Clamping to 90...");
                light.StopDot = MaxConeAngle;
            }

            if (light.StopDot2 > MaxConeAngle)
            {
                lights.Warnings.Add(
                    $"WARNING: light_spot at ({(int)light.Origin.X} {(int)light.Origin.Y} "
                    + $"{(int)light.Origin.Z}) has outer angle larger than 90 degrees! "
                    + "Clamping to 90...");
                light.StopDot2 = MaxConeAngle;
            }

            // The OUTER cone is converted first. Order matters
            // only for readability here, but the conversion is float-cast
            // double cosine, which is what the BSP records.
            bool reciprocal = options.Compliance.Emulates(StockQuirk.DegreesToRadiansByReciprocal);
            light.StopDot2 = (float)Math.Cos(LightNormals.Radians(light.StopDot2, reciprocal));
            light.StopDot = (float)Math.Cos(LightNormals.Radians(light.StopDot, reciprocal));
            light.Exponent = EntityKeys.FloatForKey(entity, "_exponent");
        }

        ApplyFalloff(entity, light, lights, options);
    }

    private static void ParseLightEnvironment(
        LightGeometry geometry,
        IReadOnlyList<BspEntity> entities,
        BspEntity entity,
        LightVisibility visibility,
        CompiledBspTree tree,
        DirectLightSet lights,
        DirectLightOptions options,
        SkyLeafVisibility? skyVis)
    {
        Vec3 dest = EntityKeys.GetVectorForKey(entity, "origin");

        // NOT added to the list here; only the first one
        // ever joins, forty lines down.
        DirectLight light = lights.Alloc(visibility, tree, dest, addToList: false);

        ParseGeneric(entities, entity, light, lights, options);

        // Read from EVERY light_environment, including ones whose
        // light is discarded below -- so a second sun's spread angle wins.
        // StockQuirk.SecondSunSpreadAngleWins: stock reads it before knowing
        // whether this entity will be the sun; correct only lets the sun's own.
        string? spread = EntityKeys.ValueForKeyWithDefault(entity, "SunSpreadAngle");
        bool spreadApplies = lights.SkyLight is null
            || options.Compliance.Emulates(StockQuirk.SecondSunSpreadAngleWins);
        if (spread is not null && spreadApplies)
        {
            float degrees = VmfValue.ParseFloat(spread);
            lights.SunAngularExtent = (float)Math.Sin(Math.PI / 180.0 * degrees);
        }

        if (lights.SkyLight is not null)
        {
            // The light just allocated is dropped -- it keeps its slot
            // in numdlights and its PVS allocation and is never seen again.
            lights.OrphanedSkyLights++;
            return;
        }

        lights.SkyLight = light;
        light.Type = EmitType.SkyLight;

        // The ambient partner takes the SUN's origin, not the
        // entity's -- the same point, since the sun's origin was the entity's.
        DirectLight ambient = lights.Alloc(visibility, tree, light.Origin, addToList: false);
        ambient.Type = EmitType.SkyAmbient;
        lights.Ambient = ambient;

        RadLightOptions radOptions = new() { Hdr = options.Hdr, LightScale = options.LightScale };
        bool haveAmbient = false;

        if (options.Hdr)
        {
            (Vec3 value, bool parsed) = RadLightFile.ParseIntensity(
                EntityKeys.ValueForKey(entity, "_ambientHDR"), radOptions);
            if (parsed)
            {
                ambient.Intensity = value;
                haveAmbient = true;
            }
        }

        if (!haveAmbient)
        {
            (Vec3 value, bool parsed) = RadLightFile.ParseIntensity(
                EntityKeys.ValueForKey(entity, "_ambient"), radOptions);
            if (parsed)
            {
                ambient.Intensity = value;
            }
            else
            {
                // No ambient key at all: half the sun's colour.
                ambient.Intensity = light.Intensity * 0.5f;
            }
        }

        if (options.Hdr)
        {
            ambient.Intensity *=
                EntityKeys.FloatForKeyWithDefault(entity, "_AmbientScaleHDR", 1.0f);
        }

        // The sun's reach is the union of every sky-touching leaf's
        // PVS, so this has to run before either light is used.
        skyVis?.Build(geometry, visibility, light, ambient);

        // Sun first, then ambient -- so the ambient is the LIST
        // HEAD and comes FIRST in the worldlights lump.
        lights.AddToActiveList(light);
        lights.AddToActiveList(ambient);
    }
}

/// <summary>
/// The switches <see cref="DirectLightBuilder"/> reads, at stock's defaults.
/// </summary>
public sealed record DirectLightOptions
{
    /// <summary><c>g_bHDR</c>: the HDR keys win.</summary>
    public bool Hdr { get; init; }

    /// <summary><c>lightscale</c> (<c>-scale</c>).</summary>
    public float LightScale { get; init; } = DirectLightBuilder.LightScale;

    /// <summary><c>dlight_threshold</c> (<c>-dlight</c>).</summary>
    public float DLightThreshold { get; init; } = DirectLightBuilder.DLightThreshold;

    /// <summary><c>g_SunAngularExtent</c> from <c>-softsun</c>, a sine; <c>SunSpreadAngle</c> overrides it.</summary>
    public float SunAngularExtent { get; init; }

    /// <summary>
    /// Which stock defects to reproduce: the target-direction normalise
    /// (<see cref="StockQuirk.VradVectorNormalise"/>) and the falloff solve
    /// (<see cref="StockQuirk.InverseQuadraticReciprocal"/>).
    /// </summary>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;
}
