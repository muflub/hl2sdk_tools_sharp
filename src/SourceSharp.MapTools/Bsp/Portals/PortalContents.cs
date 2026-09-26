//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// The content tests that decide what a portal lets through
/// </summary>
public static class PortalContents
{
    /// <summary><c>CONTENTS_SOLID</c>.</summary>
    public const int Solid = (int)BrushContents.Solid;

    /// <summary><c>CONTENTS_AREAPORTAL</c>.</summary>
    public const int AreaPortal = (int)BrushContents.AreaPortal;

    /// <summary><c>CONTENTS_DETAIL</c>.</summary>
    public const int Detail = (int)BrushContents.Detail;

    /// <summary><c>CONTENTS_TRANSLUCENT</c>.</summary>
    public const int Translucent = (int)BrushContents.Translucent;

    /// <summary>
    /// <c>LAST_VISIBLE_CONTENTS</c>: the
    /// highest bit <see cref="VisibleContents"/> will consider.
    /// </summary>
    public const int LastVisibleContents = 0x80;

    /// <summary>
    /// The single strongest visible content bit present
    /// (<c>VisibleContents</c>).
    /// </summary>
    /// <param name="contents">A mask of <c>CONTENTS_*</c> bits.</param>
    /// <returns>
    /// The lowest set bit at or below <see cref="LastVisibleContents"/>, or
    /// zero when none is set.
    /// </returns>
    /// <remarks>
    /// "Strongest" is the comment's word and "lowest bit" is the code: solid is
    /// 1, window 2, water 0x20, so the scan from the bottom finds solid before
    /// water. The loop bound is <c>&lt;=</c>, so 0x80 is included.
    /// </remarks>
    public static int VisibleContents(int contents)
    {
        for (int i = 1; i <= LastVisibleContents; i <<= 1)
        {
            if ((contents & i) != 0)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// The contents of a whole subtree, ORed together
    /// (<c>ClusterContents</c>).
    /// </summary>
    /// <param name="node">The node or leaf to collapse.</param>
    /// <returns>The combined <c>CONTENTS_*</c> mask.</returns>
    /// <remarks>
    /// The one surprise is the solid rule: if EITHER child is not entirely
    /// solid, the solid bit is cleared from the result. A cluster that contains
    /// some solid detail is still a cluster you can see into, and stock says so
    /// by dropping the bit rather than by weighting it.
    /// </remarks>
    public static int ClusterContents(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsLeaf())
        {
            return node.Contents;
        }

        int front = ClusterContents(node.Front!);
        int back = ClusterContents(node.Back!);
        int combined = front | back;

        if ((front & Solid) == 0 || (back & Solid) == 0)
        {
            combined &= ~Solid;
        }

        return combined;
    }

    /// <summary>
    /// Whether the PVS calculation can see through this portal
    /// (<c>Portal_VisFlood</c>).
    /// </summary>
    /// <param name="portal">The portal to test.</param>
    /// <returns><see langword="true"/> when vis may flood through it.</returns>
    /// <remarks>
    /// A portal with no <see cref="Portal.OnNode"/> is one of the six box
    /// portals and leads to the global outside leaf, so it is never flooded
    /// through — which is also why the box portals never appear in a
    /// <c>.prt</c> file.
    /// </remarks>
    public static bool VisFlood(Portal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);

        if (portal.OnNode is null)
        {
            return false;   // to global outsideleaf
        }

        int c1 = ClusterContents(portal.FrontNode!);
        int c2 = ClusterContents(portal.BackNode!);

        if (VisibleContents(c1 ^ c2) == 0)
        {
            return true;
        }

        if ((c1 & (Translucent | Detail)) != 0)
        {
            c1 = 0;
        }

        if ((c2 & (Translucent | Detail)) != 0)
        {
            c2 = 0;
        }

        if (((c1 | c2) & Solid) != 0)
        {
            return false;   // can't see through solid
        }

        if ((c1 ^ c2) == 0)
        {
            return true;    // identical on both sides
        }

        return VisibleContents(c1 ^ c2) == 0;
    }

    /// <summary>
    /// Whether the entity flood can cross this portal
    /// (<c>Portal_EntityFlood</c>).
    /// </summary>
    /// <param name="portal">The portal to test.</param>
    /// <returns><see langword="true"/> unless either side is solid.</returns>
    /// <exception cref="MapCompileException">Either side is not a leaf.</exception>
    public static bool EntityFlood(Portal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);

        if (!portal.FrontNode!.IsLeaf() || !portal.BackNode!.IsLeaf())
        {
            throw new MapCompileException("Portal_EntityFlood: not a leaf");
        }

        // can never cross to a solid
        return (portal.FrontNode!.Contents & Solid) == 0
            && (portal.BackNode!.Contents & Solid) == 0;
    }

    /// <summary>
    /// Whether the areaportal-leak flood can cross this portal
    /// (<c>Portal_AreaLeakFlood</c>).
    /// </summary>
    /// <param name="portal">The portal to test.</param>
    /// <returns>
    /// <see cref="EntityFlood"/>, and additionally <see langword="false"/> when
    /// either side is an areaportal.
    /// </returns>
    public static bool AreaLeakFlood(Portal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);

        if (!EntityFlood(portal))
        {
            return false;
        }

        return (portal.FrontNode!.Contents & AreaPortal) == 0
            && (portal.BackNode!.Contents & AreaPortal) == 0;
    }
}
