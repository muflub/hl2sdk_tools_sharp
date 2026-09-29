//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.MaterialPatch;

/// <summary>
/// The vbsp compile with Phase 3g's lumps made real: the driver
/// (<see cref="Vbsp"/>) with <see cref="SurfaceContentExtension"/> attached.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Vbsp.CompileAsync(MapFile, VbspContext, CancellationToken)"/>
/// attaches the extension itself now (lane p3j), and <see cref="MapFile"/>
/// keeps the <c>overlaytransition</c> chunks (<see cref="MapFile.WaterOverlayData"/>),
/// so this entry is only for a caller that wants the water overlays read from
/// a different document, or a prop-hull cooker other than the context's
/// collision cooker.
/// </para>
/// </remarks>
public static class SurfaceContentVbsp
{
    /// <summary>Compiles a loaded map with cubemaps, overlays, props and the pak.</summary>
    /// <param name="map">The loaded map.</param>
    /// <param name="context">The compile context it was loaded with.</param>
    /// <param name="document">The VMF it was loaded from, or null for no water overlays.</param>
    /// <param name="cooker">The collision cooker for static prop leaves; null uses the context's <see cref="VbspContext.CollisionCooker"/>, and the managed approximation when that is null too.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The driver's result.</returns>
    public static Task<VbspResult> CompileAsync(
        MapFile map,
        VbspContext context,
        VmfDocument? document = null,
        ICollisionCooker? cooker = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Options.OnlyEnts || context.Options.OnlyProps)
        {
            throw new ArgumentException(
                "-onlyents / -onlyprops update an existing BSP; call UpdateAsync", nameof(context));
        }

        return HostHandoff.ReturnAsync(Vbsp.CompileAsync(map, context, document, cooker, cancellationToken));
    }

    /// <summary><c>-onlyents</c> / <c>-onlyprops</c> with the prop lumps rewritten.</summary>
    /// <param name="existing">The previously compiled BSP. It is modified and returned.</param>
    /// <param name="map">The freshly loaded map.</param>
    /// <param name="context">Its context; exactly one of the two options must be set.</param>
    /// <param name="cooker">The collision cooker, or null.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The updated BSP.</returns>
    public static Task<BspData> UpdateAsync(
        BspData existing,
        MapFile map,
        VbspContext context,
        ICollisionCooker? cooker = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Options.OnlyEnts && !context.Options.OnlyProps)
        {
            throw new ArgumentException("UpdateAsync needs -onlyents or -onlyprops", nameof(context));
        }

        return HostHandoff.ReturnAsync(
            OnlyEntsUpdate.RunAsync(existing, map, context, [new SurfaceContentExtension(null, cooker)], cancellationToken));
    }
}
