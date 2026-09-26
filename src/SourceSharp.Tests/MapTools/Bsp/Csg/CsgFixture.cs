//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// Brushes to carve, built without a disk: <see cref="UnitMap"/> plus the one
/// step the CSG stage needs on top of it.
/// </summary>
/// <remarks>
/// The CSG stage takes a LOADED map — brushes with bevels, windings and
/// bounds — so every fact here goes through the real loader rather than
/// hand-building a <see cref="MapBrush"/>. That costs a few milliseconds and
/// buys the guarantee that what is carved is what Phase 3a produces, including
/// the canonical side order <c>AddBrushBevels</c> puts sides in.
/// </remarks>
internal static class CsgFixture
{
    /// <summary>Loads a document and wraps it in a build context.</summary>
    /// <param name="document">The VMF.</param>
    /// <param name="options">The compile's switches, or null for the default.</param>
    /// <returns>The build context and the loaded map.</returns>
    public static async Task<(BspBuildContext Build, MapFile Map)> LoadAsync(
        VmfDocument document,
        VbspOptions? options = null)
    {
        VbspContext context = await UnitMap.ContextAsync(options);
        MapFile map = await MapFileLoader.LoadAsync(context, document);

        // The last thing LoadMapFile does, and what
        // ProcessWorldModel's block clamping reads. Without it map_mins and
        // map_maxs are zero and the grid collapses to one block.
        MapFileReader.TakeBounds(map);

        BspBuildContext build = new(context, map)
        {
            BrushStart = map.Entities.Count > 0 ? map.Entities[0].FirstBrush : 0,
            BrushEnd = map.Entities.Count > 0
                ? map.Entities[0].FirstBrush + map.Entities[0].BrushCount
                : map.BrushCount,
        };

        return (build, map);
    }

    /// <summary>A world of axis-aligned boxes, each with its own material.</summary>
    /// <param name="boxes">The boxes, in VMF order.</param>
    /// <returns>The document.</returns>
    public static VmfDocument World(
        params (string Material, (float X, float Y, float Z) Mins, (float X, float Y, float Z) Maxs)[] boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);

        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");

        for (int i = 0; i < boxes.Length; i++)
        {
            world.Children.Add(UnitMap.Box(boxes[i].Material, boxes[i].Mins, boxes[i].Maxs, i + 1));
        }

        document.Chunks.Add(world);
        return document;
    }

    /// <summary>
    /// The whole legal world, as a submodel compile clips to.
    /// </summary>
    public static Vec3 WorldMins => new(
        GeometryEpsilons.MinCoordInteger,
        GeometryEpsilons.MinCoordInteger,
        GeometryEpsilons.MinCoordInteger);

    /// <summary>The whole legal world.</summary>
    public static Vec3 WorldMaxs => new(
        GeometryEpsilons.MaxCoordInteger,
        GeometryEpsilons.MaxCoordInteger,
        GeometryEpsilons.MaxCoordInteger);

    /// <summary>Every brush of a map, as one unclipped CSG list.</summary>
    /// <param name="build">The build context.</param>
    /// <returns>The head of the list, in reverse map order.</returns>
    public static BspBrush? AllBrushes(BspBuildContext build)
    {
        ArgumentNullException.ThrowIfNull(build);

        return BrushCsg.MakeBspBrushList(
            build, 0, build.Map.BrushCount, WorldMins, WorldMaxs, DetailScreen.FullDetail);
    }

    /// <summary>The brushes of a list, head first.</summary>
    /// <param name="head">The head of the list, or null.</param>
    /// <returns>The brushes.</returns>
    public static List<BspBrush> ToList(BspBrush? head)
    {
        List<BspBrush> list = [];
        for (BspBrush? b = head; b is not null; b = b.Next)
        {
            list.Add(b);
        }

        return list;
    }

    /// <summary>The VMF solid ids of a list, head first.</summary>
    /// <param name="head">The head of the list, or null.</param>
    /// <returns>The ids.</returns>
    public static List<int> OriginalIds(BspBrush? head) =>
        [.. ToList(head).Select(b => b.Original!.Id)];

    /// <summary>A box brush built straight from bounds, with no map behind it.</summary>
    /// <param name="build">The build context.</param>
    /// <param name="mins">The box minimum.</param>
    /// <param name="maxs">The box maximum.</param>
    /// <returns>The brush.</returns>
    public static BspBrush Box(BspBuildContext build, Vec3 mins, Vec3 maxs)
    {
        ArgumentNullException.ThrowIfNull(build);
        return BrushGeometry.BrushFromBounds(build, mins, maxs);
    }
}
