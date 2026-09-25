using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>ExportDirectLightsToWorldLights</c> (<c>lightmap.cpp:1626</c>): the
/// LUMP_WORLDLIGHTS the engine reads back.
/// </summary>
/// <remarks>
/// <para>
/// The only part of a light that survives the compile. The engine uses these
/// for dynamic lighting of models and for the flashlight-style projected
/// lights, so a wrong value here is visible in game rather than in the
/// lightmap.
/// </para>
/// <para>
/// <b>Two fields of <c>dworldlight_t</c> are never assigned</b>:
/// <see cref="DWorldLight.TexInfo"/> and <see cref="DWorldLight.Owner"/>. Stock
/// writes twelve fields into an entry of the file-scope <c>dworldlights</c>
/// array and leaves those two holding whatever the array held -- which for a
/// map compiled once is the zero of a static, and for a map being RE-lit is the
/// previous run's value at the same index, matched to a completely different
/// light. Reproduced as zero, which is what a fresh compile produces; a
/// re-light is a case this port does not have to reproduce because it never
/// reads the old lump.
/// </para>
/// <para>
/// <b>The intensity is divided by 255</b> (<c>:1647</c>), with stock's own
/// comment asking "why does vrad want 0 to 255 and not 0 to 1??". So the
/// lump's units are not vrad's internal units, and a value read back cannot be
/// fed to the sampler without multiplying again.
/// </para>
/// </remarks>
public static class WorldLightExporter
{
    /// <summary>
    /// <c>MAX_MAP_WORLDLIGHTS</c> (<c>bspfile.h</c>): 8192.
    /// </summary>
    public const int MaxWorldLights = 8192;

    /// <summary>
    /// Writes every active light into worldlight records, in stock's order.
    /// </summary>
    /// <param name="lights">The light set.</param>
    /// <returns>The records, ready to serialise as LUMP_WORLDLIGHTS.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lights"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The map has more than <see cref="MaxWorldLights"/> lights.
    /// </exception>
    /// <remarks>
    /// The order is <see cref="DirectLightSet.Active"/>'s, which is reverse
    /// creation order, which is the order stock's prepending list produces. It
    /// is the whole reason this lump can be compared byte for byte against
    /// stock's.
    /// </remarks>
    public static DWorldLight[] Export(DirectLightSet lights)
    {
        ArgumentNullException.ThrowIfNull(lights);

        if (lights.Active.Count > MaxWorldLights)
        {
            throw new InvalidOperationException(
                $"too many lights {lights.Active.Count} / {MaxWorldLights}");
        }

        DWorldLight[] result = new DWorldLight[lights.Active.Count];

        for (int i = 0; i < result.Length; i++)
        {
            DirectLight light = lights.Active[i];
            result[i] = new DWorldLight
            {
                Cluster = light.Cluster,
                Type = (int)light.Type,
                Style = light.Style,
                Origin = light.Origin,
                Intensity = light.Intensity * (1.0f / 255.0f),
                Normal = light.Normal,
                StopDot = light.StopDot,
                StopDot2 = light.StopDot2,
                Exponent = light.Exponent,
                Radius = light.Radius,
                ConstantAttn = light.ConstantAttn,
                LinearAttn = light.LinearAttn,
                QuadraticAttn = light.QuadraticAttn,
                Flags = 0,
            };
        }

        return result;
    }
}
