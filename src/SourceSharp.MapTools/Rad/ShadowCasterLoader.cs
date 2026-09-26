//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// How long each half of the load took, and what it produced.
/// </summary>
/// <param name="Set">The caster set.</param>
/// <param name="Props">What the static prop pass did.</param>
/// <param name="BrushMilliseconds">Time in the brush and sky pass.</param>
/// <param name="DisplacementMilliseconds">Time tessellating and adding displacements.</param>
/// <param name="PropMilliseconds">Time loading models and adding prop triangles.</param>
/// <remarks>
/// The split is here because §4b asks for one specific number and stock does
/// not report it. <c>-StaticPropPolys</c> was measured on 2fort to take stock's
/// serial time from 4.03 s to 15.00 s with every traced stage unchanged, which
/// makes it the one option a faster tracer cannot help -- the cost is in
/// loading and in the acceleration-structure build, both of which live here.
/// Stock's only visible half of that is its own
/// <c>Setting up ray-trace acceleration structure... Done (%.2f seconds)</c>
/// Which on this project's golden map goes from
/// 0.41 s to 1.27 s; the LOAD half it never prints at all.
/// </remarks>
public readonly record struct ShadowCasterLoadReport(
    ShadowCasterSet Set,
    StaticPropShadowCasterReport Props,
    double BrushMilliseconds,
    double DisplacementMilliseconds,
    double PropMilliseconds)
{
    /// <summary>The whole load.</summary>
    public double TotalMilliseconds =>
        BrushMilliseconds + DisplacementMilliseconds + PropMilliseconds;
}

/// <summary>
/// vrad's load path: everything that goes into <c>g_RtEnv</c> before lighting.
/// </summary>
/// <remarks>
/// <para>
/// This is <c>VRAD_LoadBSP</c>'s caster half (<c>, 2277, 2278,
/// 2279</c>) with the globals replaced by arguments and the result returned
/// rather than left in a file-scope variable. The ORDER of the four calls is
/// reproduced exactly, and it is not cosmetic: the caster list is append-only,
/// ids are not unique within a class, and a KD-tree hit reports a triangle
/// index -- so position in this list is the only thing that names a triangle.
/// </para>
/// <para>
/// Stock's order has one feature worth stating because it looks like a
/// mistake: <c>ExtractBrushEntityShadowCasters</c> runs at
/// Thirty-seven lines BEFORE the world's own brushes at
///So an entity's brushes come first in the list. Nothing
/// stock depends on that, but a dump compared triangle for triangle does.
/// </para>
/// <para>
/// WHAT THIS DELIBERATELY DOES NOT DO is build the acceleration structure.
/// Stock keeps those separate too, and it has to:
/// <c>ChangeIntoIntersectionFormat</c> overwrites
/// each triangle's vertices with plane and edge equations, so after the build
/// there is nothing left to compare against stock's own dump. Call
/// <see cref="ShadowCasterSet.BuildTracer(Options.ComplianceOptions)"/> afterwards.
/// </para>
/// </remarks>
public static class ShadowCasterLoader
{
    /// <summary>
    /// Loads every shadow caster in a map, in stock's order.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="options">vrad's switches; <c>-StaticPropPolys</c> is read from it.</param>
    /// <param name="content">Where the prop models live.</param>
    /// <param name="collision">
    /// Where a prop's <c>.phy</c> collision triangles come from. Pass
    /// <see cref="NullPropCollisionSource.Instance"/> to exercise stock's AABB
    /// branch; see <see cref="IPropCollisionSource"/> for why there is no
    /// managed answer yet.
    /// </param>
    /// <param name="noShadowMaterials">
    /// The <c>noshadow</c> names from <c>lights.rad</c>.
    /// </param>
    /// <param name="transparency">
    /// The <c>-textureshadows</c> hook, or null when the switch is off.
    /// </param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The caster set and the time each pass took.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="bsp"/>, <paramref name="options"/>,
    /// <paramref name="content"/> or <paramref name="collision"/> is null.
    /// </exception>
    public static async ValueTask<ShadowCasterLoadReport> LoadAsync(
        BspData bsp,
        VradOptions options,
        IContentFileSystem content,
        IPropCollisionSource collision,
        IReadOnlyList<string>? noShadowMaterials = null,
        StaticPropTriangleTransparency? transparency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(collision);

        ShadowCasterBuilder builder = new() { Compliance = options.Compliance };

        // Picks g_pFaces once, before any of this. The HDR
        // face lump is written only when it DIFFERS from the LDR one, so an
        // HDR run on a map without it reads the LDR faces -- which is why the
        // emptiness test is here and not only the range test.
        bool useHdrFaces = options.Range != VradLightingRange.Ldr
            && !bsp[BspLump.FacesHdr].IsEmpty;

        long start = Stopwatch.GetTimestamp();

        // Entities first, before the world's own brushes.
        List<BspEntity> entities = EntityLump.Parse(bsp[BspLump.Entities]);
        BrushShadowCasters.AddBrushEntities(bsp, entities, builder, options.Compliance);

        // Brushes, then the sky faces of the same model.
        BrushShadowCasters.AddWorld(bsp, useHdrFaces, builder, options.Compliance);
        long afterBrushes = Stopwatch.GetTimestamp();

        DisplacementShadowCasters.Add(bsp, builder);
        long afterDisplacements = Stopwatch.GetTimestamp();

        StaticPropShadowCasterReport props = await AddPropsAsync(
                bsp, options, content, collision, noShadowMaterials, transparency, builder,
                cancellationToken)
            .ConfigureAwait(false);
        long afterProps = Stopwatch.GetTimestamp();

        return new ShadowCasterLoadReport(
            builder.Build(),
            props,
            Elapsed(start, afterBrushes),
            Elapsed(afterBrushes, afterDisplacements),
            Elapsed(afterDisplacements, afterProps));
    }

    private static async ValueTask<StaticPropShadowCasterReport> AddPropsAsync(
        BspData bsp,
        VradOptions options,
        IContentFileSystem content,
        IPropCollisionSource collision,
        IReadOnlyList<string>? noShadowMaterials,
        StaticPropTriangleTransparency? transparency,
        ShadowCasterBuilder builder,
        CancellationToken cancellationToken)
    {
        GameLumpEntry? sprp = null;
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            if (entry.Id == GameLumpId.MakeId(GameLumpId.StaticProps))
            {
                sprp = entry;
                break;
            }
        }

        if (sprp is null)
        {
            // UnserializeStaticProps reads GameLumpSize BEFORE checking the
            // handle, so an absent sprp indexes a
            // CUtlLinkedList out of range and reads garbage, which then trips
            // the version check. That is measured, not inferred -- an earlier
            // lane deleted the lump from this map and watched it happen. A map
            // without props simply has none here.
            builder.BeginSource(ShadowCasterSource.StaticProp);
            return new StaticPropShadowCasterReport(
                0, 0, 0, 0, 0, 0, StaticPropAbandonReason.None, -1);
        }

        StaticPropLump props = StaticPropLump.Read(sprp.Value);

        StaticPropShadowCasterOptions propOptions = new()
        {
            StaticPropPolys = options.StaticPropPolys,
            NoShadowMaterials = noShadowMaterials ?? [],
            Transparency = options.TextureShadows ? transparency : null,
        };

        return await StaticPropShadowCasters
            .AddAsync(props, content, collision, propOptions, builder, cancellationToken)
            .ConfigureAwait(false);
    }

    private static double Elapsed(long from, long to) =>
        (to - from) * 1000.0 / Stopwatch.Frequency;
}
