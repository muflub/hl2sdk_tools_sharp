using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// Every light a compile will gather from: stock's <c>activelights</c> list,
/// <c>numdlights</c>, <c>gSkyLight</c> and <c>gAmbient</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Active"/> is in stock's LIST order, which is reverse creation
/// order.</b> <c>AllocDLight</c> PREPENDS, so the
/// head of <c>activelights</c> is the last light made. Two things read that
/// order and both are observable:
/// <c>ExportDirectLightsToWorldLights</c> writes the worldlights lump in it,
/// byte for byte; and every sample accumulates light in it, and floating-point
/// addition is not associative.
/// </para>
/// <para>
/// <b><see cref="Count"/> is not <c>Active.Count</c>.</b> <c>numdlights</c>
/// counts every <c>AllocDLight</c>, including the ones never added to the list:
/// a SECOND <c>light_environment</c> allocates a light, increments the counter,
/// takes a PVS row, and is then dropped on the floor because
/// <c>gSkyLight</c> is already set. So a map with
/// two sky lights prints one more than it exports. That is what
/// <see cref="Count"/> is for, and the difference is
/// <see cref="OrphanedSkyLights"/>.
/// </para>
/// </remarks>
public sealed class DirectLightSet
{
    private readonly List<DirectLight> _active = [];

    /// <summary>
    /// The lights, in <c>activelights</c> order: newest first.
    /// </summary>
    public IReadOnlyList<DirectLight> Active => _active;

    /// <summary>
    /// <c>numdlights</c>: every light allocated, listed or not.
    /// </summary>
    public int Count { get; internal set; }

    /// <summary><c>gSkyLight</c>, or null when the map has no sun.</summary>
    public DirectLight? SkyLight { get; internal set; }

    /// <summary><c>gAmbient</c>, the sun's ambient partner.</summary>
    public DirectLight? Ambient { get; internal set; }

    /// <summary>
    /// <c>g_SunAngularExtent</c>: the SINE of the sun's angular radius.
    /// </summary>
    /// <remarks>
    /// Stored as the sine, not the angle, because that is what
    /// Stores and what the sun sampler multiplies by.
    /// Zero means a point sun and one ray per sample; anything else means
    /// <see cref="LightConstants.SunAreaLightSamples"/> jittered rays.
    /// </remarks>
    public float SunAngularExtent { get; internal set; }

    /// <summary>
    /// How many <c>light_environment</c> entities were allocated and then
    /// discarded because one was already in place.
    /// </summary>
    public int OrphanedSkyLights { get; internal set; }

    /// <summary>
    /// How many <see cref="EmitType.Surface"/> lights the patch pass produced.
    /// </summary>
    public int SurfaceLights { get; internal set; }

    /// <summary>
    /// The classnames beginning with <c>light</c> that vrad does not handle,
    /// in the order it met them.
    /// </summary>
    /// <remarks>
    /// Stock prints <c>unsupported light entity: "%s"</c> through
    /// <c>qprintf</c>, so it is visible only with <c>-verbose</c>. Collected
    /// rather than printed, since a library has no console.
    /// </remarks>
    public IList<string> UnsupportedClassNames { get; } = [];

    /// <summary>Diagnostics stock prints as <c>Warning</c>.</summary>
    public IList<string> Warnings { get; } = [];

    /// <summary>
    /// <c>AllocDLight</c>.
    /// </summary>
    /// <param name="visibility">The PVS accessor.</param>
    /// <param name="tree">The compiled tree, for the light's cluster.</param>
    /// <param name="origin">Where the light is.</param>
    /// <param name="addToList">
    /// Whether it joins <see cref="Active"/>. False for a
    /// <c>light_environment</c>, which is added later and only if it is the
    /// first.
    /// </param>
    /// <returns>The new light.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="visibility"/> or <paramref name="tree"/> is null.
    /// </exception>
    /// <remarks>
    /// The PVS row is taken HERE, at allocation, from the light's own cluster
    /// -- before the caller has decided what kind of light it is. A sky light
    /// therefore starts with the PVS of whatever leaf the entity happens to sit
    /// in, and <c>BuildVisForLightEnvironment</c> merges the rest in afterwards.
    /// </remarks>
    public DirectLight Alloc(
        LightVisibility visibility,
        CompiledBspTree tree,
        SourceSharp.MapFormats.Geometry.Vec3 origin,
        bool addToList)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(tree);

        DirectLight light = new()
        {
            Index = Count++,
            Origin = origin,
            FaceNum = -1,
        };

        light.Cluster = tree.ClusterFromPoint(origin);
        visibility.SetLightVis(light, light.Cluster);

        if (addToList)
        {
            AddToActiveList(light);
        }

        return light;
    }

    /// <summary>
    /// <c>AddDLightToActiveList</c>: prepend.
    /// </summary>
    /// <param name="light">The light.</param>
    /// <exception cref="ArgumentNullException"><paramref name="light"/> is null.</exception>
    public void AddToActiveList(DirectLight light)
    {
        ArgumentNullException.ThrowIfNull(light);
        _active.Insert(0, light);
    }
}
