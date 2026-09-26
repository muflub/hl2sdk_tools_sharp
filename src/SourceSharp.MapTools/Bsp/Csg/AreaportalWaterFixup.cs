//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// The areaportal-inside-water hack: <c>FixupAreaportalWaterBrushes</c> and
/// <c>CopyMatchingTexinfos</c>.
/// </summary>
/// <remarks>
/// <para>
/// Stock calls it a hack in its own comment and explains why: an areaportal
/// assumes the space around it is empty, so one placed inside water floods the
/// whole water volume. The fix is to give the areaportal brush the water's
/// contents and the water's texinfos, so the space it occupies has the same
/// properties as what surrounds it.
/// </para>
/// <para>
/// <b>This is the one part of the CSG stage that reaches the BRUSHES and
/// BRUSHSIDES lumps.</b> Everything else here carves <see cref="BspBrush"/>
/// copies, which are thrown away when the tree is freed; this writes
/// <c>pAreaportal-&gt;original-&gt;contents</c> and the texinfos of
/// <c>pAreaportal-&gt;original-&gt;original_sides</c> — the MAP brush and the
/// MAP sides, which <c>EmitBrushes</c> copies into
/// the lumps verbatim. Stock's own comment says so: "Ideally, this should have
/// been done before the bspbrush_t was created from the map brush."
/// </para>
/// <para>
/// It runs once per block and, because the world model is built twice
/// (the <c>optimize</c> loop), up to twice per block per
/// compile. Both effects are idempotent — an <c>|=</c> of the same bits and an
/// assignment of the same texinfo — which is why running it repeatedly is
/// harmless and why nothing guards against it.
/// </para>
/// </remarks>
public static class AreaportalWaterFixup
{
    /// <summary>
    /// Gives an areaportal inside water the water's contents and texinfos:
    /// <c>FixupAreaportalWaterBrushes</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="list">The block's brush list, before <c>ChopBrushes</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The inner loop skips brushes that already carry
    /// <c>CONTENTS_AREAPORTAL</c> so that an areaportal fixed up a moment ago —
    /// which now has water contents and would otherwise look like water — does
    /// not become a donor. That is the whole reason the check is on the
    /// contents and not on a separate flag.
    /// </para>
    /// <para>
    /// <see cref="BrushCsg.BrushesDisjoint"/> is the cheap rejection and
    /// <see cref="BrushCsg.IntersectBrush"/> is the exact one; the intersection
    /// brush is allocated, tested for null and freed, and its geometry is never
    /// read.
    /// </para>
    /// </remarks>
    public static void FixupAreaportalWaterBrushes(BspBuildContext context, BspBrush? list)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (BspBrush? areaportal = list; areaportal is not null; areaportal = areaportal.Next)
        {
            if ((areaportal.Original!.Contents & (int)Materials.BrushContents.AreaPortal) == 0)
            {
                continue;
            }

            for (BspBrush? water = list; water is not null; water = water.Next)
            {
                if ((water.Original!.Contents & (int)Materials.BrushContents.AreaPortal) != 0)
                {
                    continue;
                }

                if ((water.Original.Contents & BrushCsg.SplitAreaPortalMask) == 0)
                {
                    continue;
                }

                if (BrushCsg.BrushesDisjoint(areaportal, water))
                {
                    continue;
                }

                BspBrush? intersect = BrushCsg.IntersectBrush(context, areaportal, water);
                if (intersect is null)
                {
                    continue;
                }

                context.FreeBrush(intersect);
                areaportal.Original.Contents |= water.Original.Contents;

                CopyMatchingTexinfos(context, areaportal, water);
                CopyMatchingTexinfos(context, areaportal.Original, water);
            }
        }
    }

    /// <summary>
    /// Retextures a carved brush's sides from a source brush's map sides:
    /// <c>CopyMatchingTexinfos</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="destination">The brush whose sides are retextured.</param>
    /// <param name="source">The brush the texinfos come from.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static void CopyMatchingTexinfos(
        BspBuildContext context,
        BspBrush destination,
        BspBrush source)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        for (int i = 0; i < destination.SideCount; i++)
        {
            int texInfo = MatchingTexInfo(
                context, destination.Sides[i].PlaneNumber, source, destination.Sides[i].TexInfo);

            if (texInfo != NoMatch)
            {
                destination.Sides[i].TexInfo = texInfo;
            }
        }
    }

    /// <summary>
    /// The same, over a map brush's own sides:
    /// <c>CopyMatchingTexinfos(pAreaportal-&gt;original-&gt;original_sides, ...)</c>.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="destination">The map brush whose sides are retextured.</param>
    /// <param name="source">The brush the texinfos come from.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// The overload that changes the compiled map: these sides are the ones
    /// <c>EmitBrushes</c> writes into BRUSHSIDES.
    /// </remarks>
    public static void CopyMatchingTexinfos(
        BspBuildContext context,
        MapBrush destination,
        BspBrush source)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        for (int i = 0; i < destination.SideCount; i++)
        {
            MapBrushSide side = context.Map.BrushSides[destination.FirstSide + i];

            int texInfo = MatchingTexInfo(context, side.PlaneNumber, source, side.TexInfo);
            if (texInfo != NoMatch)
            {
                side.TexInfo = texInfo;
            }
        }
    }

    private const int NoMatch = int.MinValue;

    /// <summary>
    /// The texinfo of the source brush's most nearly parallel map side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three details of stock's search are load-bearing. It walks the source
    /// brush's <b>original</b> map sides and not the carved ones — stock's
    /// comment says why: a carved side can carry
    /// <see cref="BspBrushSide.TexInfoNode"/>, and an areaportal that inherited
    /// one "will flood into the entire water volume". It stops at the FIRST
    /// exact match, on either an identical plane index or a dot product of
    /// exactly 1.0f. And its running best starts at -1.0f, so a side facing
    /// directly away still wins over nothing.
    /// </para>
    /// <para>
    /// The no-match path is stock's <c>Msg("Found no matching plane for %s")</c>
    /// — which builds that message by indexing <c>texinfo[pSide-&gt;texinfo]</c>
    /// with a texinfo that may be -1. It is unreachable while the source brush
    /// has any side that is not already on a node, which is exactly what using
    /// the original sides guarantees, so the read has never happened. The
    /// diagnostic here names the destination side's texinfo index instead of
    /// dereferencing it.
    /// </para>
    /// </remarks>
    private static int MatchingTexInfo(
        BspBuildContext context,
        int destinationPlaneNumber,
        BspBrush source,
        int destinationTexInfo)
    {
        Vec3 normal = context.Planes[destinationPlaneNumber].Normal;

        MapBrush sourceBrush = source.Original!;
        int best = NoMatch;
        float bestDot = -1.0f;

        for (int j = 0; j < sourceBrush.SideCount; j++)
        {
            MapBrushSide sourceSide = context.Map.BrushSides[sourceBrush.FirstSide + j];

            if (sourceSide.TexInfo == BspBrushSide.TexInfoNode)
            {
                continue;
            }

            float dot = Vec3.Dot(normal, context.Planes[sourceSide.PlaneNumber].Normal);

            if (dot == 1.0f || destinationPlaneNumber == sourceSide.PlaneNumber)
            {
                return sourceSide.TexInfo;
            }

            if (dot > bestDot)
            {
                best = sourceSide.TexInfo;
                bestDot = dot;
            }
        }

        if (best == NoMatch)
        {
            context.Diagnostics.Add(new CompileDiagnostic(
                BspBuildCodes.NoMatchingPlane,
                DiagnosticSeverity.Warning,
                "Found no matching plane for texinfo "
                    + destinationTexInfo.ToString(CultureInfo.InvariantCulture),
                new MapLocation(BrushId: sourceBrush.Id)));
        }

        return best;
    }
}
