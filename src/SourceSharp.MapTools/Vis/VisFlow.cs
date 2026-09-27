//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Vis;

/// <summary>The fog radius vvis culls the flow with.</summary>
/// <param name="Use">Whether there is one.</param>
/// <param name="Squared">Its square, in map units.</param>
public readonly record struct VisRadius(bool Use, double Squared)
{
    /// <summary>
    /// The radius a map's entities give: <c>-radius_override</c> if the options
    /// carry one, else the first <c>env_fog_controller</c>'s <c>farz</c>.
    /// </summary>
    /// <param name="entities">Each entity's class name and <c>farz</c> value, in map order.</param>
    /// <param name="options">vvis's options.</param>
    /// <returns>The radius.</returns>
    public static VisRadius FromEntities(IEnumerable<(string ClassName, string? FarZ)> entities, VvisOptions options)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(options);

        if (options.RadiusOverride is float given)
        {
            double wide = given;
            return new VisRadius(true, wide * wide);
        }

        foreach ((string className, string? farZ) in entities)
        {
            if (!string.Equals(className, "env_fog_controller", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // -- the FIRST one wins, and a farz of exactly zero
            // means "no radius" rather than "a radius of zero".
            float far = float.TryParse(farZ, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : 0f;
            return far > 0f ? new VisRadius(true, far * far) : default;
        }

        return default;
    }
}

/// <summary>
/// What <see cref="Vvis.FlowAsync"/> made of a map's portals: every portal's
/// finished visibility, waiting for the map to fold it into cluster rows.
/// </summary>
public sealed class VisFlow
{
    internal VisFlow(PortalSet portals, VisPortalState state, VisRadius radius, int deepestFlow, VisWorkCounters work)
    {
        Portals = portals;
        State = state;
        Radius = radius;
        DeepestFlow = deepestFlow;
        Work = work;
    }

    /// <summary>The portals that were flowed.</summary>
    public PortalSet Portals { get; }

    /// <summary>How many portals.</summary>
    public int PortalCount => Portals.Count;

    /// <summary>The radius the flow culled with.</summary>
    public VisRadius Radius { get; }

    /// <summary>The deepest recursion the flow reached.</summary>
    public int DeepestFlow { get; }

    /// <summary>The flow's work counters.</summary>
    public VisWorkCounters Work { get; }

    internal VisPortalState State { get; }
}

/// <summary>The three lumps vvis writes, before they are written.</summary>
internal sealed record VisLumps(byte[] Visibility, BspLumpData Leafs, byte[] LeafMinDistToWater)
{
    /// <summary>Writes them into the map.</summary>
    public void WriteTo(BspData bsp)
    {
        bsp.SetLump(BspLump.Visibility, Visibility);
        bsp[BspLump.Leafs] = Leafs;
        bsp.SetLump(BspLump.LeafMinDistToWater, LeafMinDistToWater);
    }
}
