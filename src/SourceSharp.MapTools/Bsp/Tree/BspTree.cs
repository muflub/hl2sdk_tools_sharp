//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Portals;

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// A BSP tree: <c>tree_t</c>.
/// </summary>
/// <remarks>
/// <para>
/// A head node, a bounding box and the sentinel leaf outside it. Stock's
/// <c>AllocTree</c> <c>memset</c>s the whole thing
/// and then calls <c>ClearBounds</c>, so a fresh tree's bounds are inside out
/// at ±99999 — which is what <c>BrushBSP</c>'s union over the brush list then
/// relies on.
/// </para>
/// <para>
/// <b><see cref="OutsideNode"/> is embedded in stock, not pointed to.</b>
/// <c>tree_t::outside_node</c> is a <c>node_t</c> by value
/// So it is zeroed with the tree and is never passed
/// through <c>AllocNode</c> — it has id 0, <c>diskId</c> 0 rather than -1, and
/// it does not advance the node counter. Phase 3c's <c>MakeHeadnodePortals</c>
/// is what puts it to work, as the node on the far side of the six portals that
/// wrap the world. It is allocated here for the same reason it is embedded
/// there: so that a tree always has one.
/// </para>
/// </remarks>
public sealed class BspTree : IBspTree
{
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The tree has not been built.</exception>
    IBspNode IBspTree.HeadNode =>
        HeadNode ?? throw new InvalidOperationException("the tree has no head node yet");

    /// <inheritdoc/>
    IBspNode IBspTree.OutsideNode => OutsideNode;

    /// <summary>The root of the tree.</summary>
    public BspNode? HeadNode { get; set; }

    /// <summary>
    /// The leaf representing everything outside the tree:
    /// <c>outside_node</c>.
    /// </summary>
    /// <remarks>
    /// Its <see cref="BspNode.DiskId"/> is 0 and not -1, because stock's is
    /// part of the <c>memset</c> that <c>AllocNode</c>'s -1 never reaches.
    /// Nothing in Phase 3b reads it; it is here so that 3c does not have to
    /// change this type to add it.
    /// <para>
    /// <b>It is a LEAF from the start.</b> Stock's memset leaves
    /// <c>planenum</c> 0 and <c>MakeHeadnodePortals</c> then assigns
    /// <c>PLANENUM_LEAF</c>; the portal contract's
    /// <c>IBspNode.PlaneNumber</c> has no setter, so the assignment is made
    /// here instead. Nothing reads it in between. Left at 0, every leaked map
    /// failed in the area flood with "Portal_EntityFlood: not a leaf", because
    /// the box portals' far side looked like a node.
    /// </para>
    /// </remarks>
    public BspNode OutsideNode { get; } = new() { DiskId = 0, PlaneNumber = BspNode.Leaf };

    /// <summary>
    /// The tree's bounds, seeded inside out by <c>ClearBounds</c>.
    /// </summary>
    public Vec3 Mins { get; set; } = new(99999f, 99999f, 99999f);

    /// <summary>
    /// The tree's bounds, seeded inside out by <c>ClearBounds</c>.
    /// </summary>
    public Vec3 Maxs { get; set; } = new(-99999f, -99999f, -99999f);

    /// <summary>Whether the map leaked: <c>tree_t::leaked</c>. Phase 3c sets it.</summary>
    public bool Leaked { get; set; }

    /// <summary>
    /// The six counts <c>BrushBSP</c> prints under <c>-v</c>.
    /// </summary>
    /// <remarks>
    /// Not part of stock's <c>tree_t</c>: stock prints them and forgets them.
    /// They are kept because they are the only per-block record of the tree's
    /// shape that a stock compile leaves in its log, and so the only thing a
    /// managed tree can be held against before Phase 3e can write a BSP.
    /// </remarks>
    public BspTreeStatistics Statistics { get; set; }
}
