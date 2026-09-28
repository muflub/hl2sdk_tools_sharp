//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Bsp.Write;

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// The volumes of a map's <c>func_viscluster</c> entities, and the answer to
/// "which of them does this leaf belong to" that the portal file stage asks.
/// </summary>
/// <remarks>
/// <para>
/// A <c>func_viscluster</c> tells vbsp that every empty leaf it covers is one
/// vis cluster: vvis then never works out visibility between those leaves,
/// which is the point of the entity (a big open yard that sees itself anyway
/// costs nothing to flow through as one cluster, and a great deal as two
/// hundred). Before this class existed the loader recorded the entities and
/// nothing consumed them, so every covered leaf became its own cluster. On
/// <c>ctf_2fort</c> that was a couple of hundred clusters and several hundred
/// portals more than the reference compiler writes, and vvis paid for every
/// one of them.
/// </para>
/// <para>
/// <b>The volumes are built while the map loads, not when the portal file is
/// written.</b> The reference compiler turns each entity's brushes into a
/// clipped, chopped brush list as soon as it parses the entity. Clipping to
/// the coordinate box looks up the box's bounding planes, and the first
/// lookup APPENDS them to the plane table; doing it at load time, at the
/// entity's position in the file, is what puts those planes where the
/// reference puts them. Building the lists later would give the same
/// clusters and a differently numbered plane lump.
/// </para>
/// <para>
/// <b>A leaf belongs to a volume when the volume covers more than a tenth of
/// the leaf,</b> measured as the summed volume of the intersections between
/// the leaf's volume brush and each of the entity's chopped brushes. A leaf
/// that only grazes a viscluster keeps its own cluster. That threshold is the
/// reference's, and so is the way overlapping visclusters resolve: the
/// entities are tested from the last to the first, and the threshold is not
/// raised when one qualifies, so the leaf goes to the LOWEST-numbered
/// viscluster that covers more than a tenth of it, not to the one that
/// covers the most. The reference warns that two overlapping visclusters do
/// not merge with each other, and this is the consequence: a leaf in both is
/// simply claimed by one.
/// </para>
/// </remarks>
public sealed class VisClusterVolumes : IVisClusterResolver
{
    /// <summary>
    /// The fraction of a leaf's volume a viscluster has to cover to claim it.
    /// </summary>
    public const float CoverageThreshold = 0.10f;

    private readonly List<BspBrush?> _volumes = [];

    /// <summary>
    /// The build context the intersections are carved in.
    /// </summary>
    /// <remarks>
    /// The first <see cref="Add"/> sets it to the load-time context, which is
    /// over the same map and so over the same planes; the compile replaces it
    /// with its own before the portal file is written, so the carving is
    /// counted against the compile it belongs to.
    /// </remarks>
    public BspBuildContext? Context { get; set; }

    /// <summary>How many visclusters have been added.</summary>
    public int Count => _volumes.Count;

    /// <summary>
    /// The chopped brush list of one viscluster, in the order it was added.
    /// </summary>
    /// <param name="index">The viscluster's index.</param>
    /// <returns>The head of its brush list, or null when it had no brushes.</returns>
    public BspBrush? Volume(int index) => _volumes[index];

    /// <summary>
    /// Turns one <c>func_viscluster</c> entity's brushes into a volume and
    /// appends it.
    /// </summary>
    /// <param name="context">
    /// A build context over the map the entity was loaded into.
    /// </param>
    /// <param name="firstBrush">The entity's first brush.</param>
    /// <param name="brushCount">How many brushes it has.</param>
    /// <returns>The new viscluster's index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// The brushes are clipped to the whole coordinate box, detail brushes are
    /// left out (a viscluster is structural by definition), and the list is
    /// then chopped so no two brushes overlap. That last step is what makes
    /// the summed intersection volume a real volume: two overlapping brushes
    /// of one entity would otherwise count their shared part twice.
    /// </remarks>
    public int Add(BspBuildContext context, int firstBrush, int brushCount)
    {
        ArgumentNullException.ThrowIfNull(context);

        Vec3 mins = new(WriteConstants.MinCoordInteger, WriteConstants.MinCoordInteger, WriteConstants.MinCoordInteger);
        Vec3 maxs = new(WriteConstants.MaxCoordInteger, WriteConstants.MaxCoordInteger, WriteConstants.MaxCoordInteger);

        // build the map brushes out into the minimum non-overlapping set of brushes
        BspBrush? list = BrushCsg.MakeBspBrushList(
            context, firstBrush, firstBrush + brushCount, mins, maxs, DetailScreen.NoDetail);
        list = BrushCsg.ChopBrushes(context, list);

        Context ??= context;
        _volumes.Add(list);
        return _volumes.Count - 1;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A leaf with no volume brush (one that did not come out of a brush BSP,
    /// which no empty leaf of a finished world tree is) cannot be measured and
    /// is left alone.
    /// </remarks>
    public int GetVisCluster(IBspNode leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);

        if (_volumes.Count == 0 || leaf is not BspNode node || node.Volume is null)
        {
            return -1;
        }

        BspBuildContext context = Context
            ?? throw new InvalidOperationException("VisClusterVolumes.Context is not set.");

        // needs to cover at least 10% of the volume to overlap
        float threshold = BrushGeometry.BrushVolume(context, node.Volume) * CoverageThreshold;
        int found = -1;

        for (int i = _volumes.Count; --i >= 0;)
        {
            float volume = VolumeOfIntersection(context, _volumes[i], node);
            if (volume > threshold)
            {
                // The threshold stays where it was: see the class remarks.
                found = i;
            }
        }

        return found;
    }

    /// <summary>
    /// The total volume a brush list shares with a leaf.
    /// </summary>
    /// <param name="context">The build context.</param>
    /// <param name="brushes">The chopped brush list.</param>
    /// <param name="node">The leaf; its volume brush and bounds are read.</param>
    /// <returns>The summed volume of every non-empty intersection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="node"/> is null.</exception>
    /// <remarks>
    /// The box test is inclusive, so a brush that only touches the leaf's box
    /// is carved anyway; the carve then finds nothing, or a sliver, and the
    /// threshold ignores it. The leaf's box is the one the portaliser worked
    /// out from its portals, which is the box of its volume.
    /// </remarks>
    public static float VolumeOfIntersection(BspBuildContext context, BspBrush? brushes, BspNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        float volume = 0f;
        if (node.Volume is null)
        {
            return volume;
        }

        for (BspBrush? brush = brushes; brush is not null; brush = brush.Next)
        {
            if (!BoxesIntersect(node.Mins, node.Maxs, brush.Mins, brush.Maxs))
            {
                continue;
            }

            BspBrush? intersect = BrushCsg.IntersectBrush(context, node.Volume, brush);
            if (intersect is not null)
            {
                volume += BrushGeometry.BrushVolume(context, intersect);
                context.FreeBrush(intersect);
            }
        }

        return volume;
    }

    // Inclusive on every face: boxes that share a face intersect.
    private static bool BoxesIntersect(Vec3 mins1, Vec3 maxs1, Vec3 mins2, Vec3 maxs2)
    {
        return mins1.X <= maxs2.X && maxs1.X >= mins2.X
            && mins1.Y <= maxs2.Y && maxs1.Y >= mins2.Y
            && mins1.Z <= maxs2.Z && maxs1.Z >= mins2.Z;
    }
}
