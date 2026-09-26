//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// A node or a leaf of the BSP tree, reduced to what portalisation, the entity
/// flood, leak detection and the area flood actually touch
/// (<c>node_t</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is an interface rather than the tree's own class because the tree is
/// built by a different stage(<c>BuildTree_r</c>) than
/// the one that portalises it, and the two were ported by different lanes. The
/// members here are the whole contract between them: everything stock's
/// And read or write
/// on a <c>node_t</c>, and nothing else. A tree builder satisfies it by putting
/// <c>: IBspNode</c> on its node type.
/// </para>
/// <para>
/// Half of it is mutable, and that is not an accident of style: portalisation
/// is the stage that computes <see cref="Mins"/>/<see cref="Maxs"/>,
/// <c>FillOutside</c> is the stage that turns unreachable leaves solid by
/// writing <see cref="Contents"/>, and the cluster and area numbers are written
/// here and read by everything downstream.
/// </para>
/// </remarks>
public interface IBspNode
{
    /// <summary><c>PLANENUM_LEAF</c>: the value of <see cref="PlaneNumber"/> on a leaf.</summary>
    public const int LeafPlaneNumber = -1;

    /// <summary>A number unique within the tree, for diagnostics only.</summary>
    int Id { get; }

    /// <summary>
    /// The index into the map's plane table of the plane this node splits on,
    /// or <see cref="LeafPlaneNumber"/> when this is a leaf.
    /// </summary>
    int PlaneNumber { get; }

    /// <summary>The node this one hangs off, or <see langword="null"/> at the head.</summary>
    IBspNode? Parent { get; }

    /// <summary><c>children[0]</c>: the child in front of <see cref="PlaneNumber"/>.</summary>
    IBspNode? Front { get; }

    /// <summary><c>children[1]</c>: the child behind <see cref="PlaneNumber"/>.</summary>
    IBspNode? Back { get; }

    /// <summary>
    /// The brush side that created this node, when there was one. Read only to
    /// name a material in the unbounded-volume warning
    /// </summary>
    MapBrushSide? Side { get; }

    /// <summary>The node's bounding box minimum, valid after portalisation.</summary>
    Vec3 Mins { get; set; }

    /// <summary>The node's bounding box maximum, valid after portalisation.</summary>
    Vec3 Maxs { get; set; }

    /// <summary>
    /// The OR of every brush content bit in this leaf. <c>FillOutside</c>
    /// overwrites it with <c>CONTENTS_SOLID</c> on leaves no entity reached.
    /// </summary>
    int Contents { get; set; }

    /// <summary>
    /// Zero until the entity flood reaches this leaf, then one plus the number
    /// of portal hops from the entity that reached it.
    /// </summary>
    int Occupied { get; set; }

    /// <summary>The entity that was placed in this leaf, for the leak report.</summary>
    MapEntity? Occupant { get; set; }

    /// <summary>The vis cluster this leaf belongs to; -1 on solid, -99 on a node.</summary>
    int Cluster { get; set; }

    /// <summary>The area this leaf belongs to; -1 on a node whose children disagree.</summary>
    int Area { get; set; }

    /// <summary>
    /// The head of this node's portal list. Every portal appears on the lists
    /// of both the nodes it separates, threaded through
    /// <see cref="Portal.NextAt"/>.
    /// </summary>
    Portal? Portals { get; set; }

    /// <summary>
    /// The original map brushes with a fragment in this leaf, in stock's
    /// <c>brushlist</c> order and with stock's duplicates: one entry per
    /// <c>bspbrush_t</c>, several of which can share an original.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing twice over. <c>AreaportalBrushForNode</c>
    /// Takes the FIRST areaportal brush it finds, and
    /// <c>FindPortalSide</c> breaks distance ties by taking the
    /// first candidate at the best distance.
    /// </remarks>
    IReadOnlyList<MapBrush> LeafBrushes { get; }
}

/// <summary>Reading a tree of <see cref="IBspNode"/> the way stock reads <c>node_t</c>.</summary>
public static class BspNodes
{
    /// <summary>Whether this is a leaf rather than a splitting node.</summary>
    /// <param name="node">The node to ask about.</param>
    /// <returns><see langword="true"/> when <see cref="IBspNode.PlaneNumber"/> is <see cref="IBspNode.LeafPlaneNumber"/>.</returns>
    public static bool IsLeaf(this IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.PlaneNumber == IBspNode.LeafPlaneNumber;
    }

    /// <summary>The child on one side of the split plane, indexed as stock indexes <c>children</c>.</summary>
    /// <param name="node">The splitting node.</param>
    /// <param name="side">0 for the front child, 1 for the back child.</param>
    /// <returns>That child.</returns>
    public static IBspNode? ChildAt(this IBspNode node, int side)
    {
        ArgumentNullException.ThrowIfNull(node);
        return side == 0 ? node.Front : node.Back;
    }

    /// <summary>
    /// Whether this leaf is part of an areaportal brush
    /// (<c>IsAreaportalNode</c>).
    /// </summary>
    /// <param name="node">The leaf to ask about.</param>
    /// <returns><see langword="true"/> when <c>CONTENTS_AREAPORTAL</c> is set.</returns>
    public static bool IsAreaportal(this IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return (node.Contents & PortalContents.AreaPortal) != 0;
    }
}
