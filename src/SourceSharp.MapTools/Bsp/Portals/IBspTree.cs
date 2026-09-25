using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// A built BSP tree, reduced to what the portal stages need
/// (<c>tree_t</c>, <c>src/utils/vbsp/vbsp.h:239</c>).
/// </summary>
/// <remarks>
/// <see cref="OutsideNode"/> is the one part worth a sentence. Stock holds it
/// <em>by value</em> inside <c>tree_t</c>, so it is not reachable by walking
/// from <see cref="HeadNode"/> and every tree walk in vbsp silently excludes
/// it. It is a leaf with no brushes whose whole job is to be on the far side of
/// the six box portals, so that "the flood got out of the map" has somewhere to
/// be recorded.
/// </remarks>
public interface IBspTree
{
    /// <summary>The root of the tree.</summary>
    IBspNode HeadNode { get; }

    /// <summary>The leaf on the outside of the six head-node box portals.</summary>
    IBspNode OutsideNode { get; }

    /// <summary>
    /// The tree's bounds before <c>MakeHeadnodePortals</c> pads them by
    /// <c>SIDESPACE</c>. For the world model these are the block grid's bounds
    /// in x and y and the map's bounds ±8 in z, not the map's own bounds.
    /// </summary>
    Vec3 Mins { get; }

    /// <summary>The other half of <see cref="Mins"/>.</summary>
    Vec3 Maxs { get; }

    /// <summary>
    /// Set once a leak file has been written, so that an areaportal leak does
    /// not overwrite a real one (<c>leakfile.cpp:104</c>).
    /// </summary>
    bool Leaked { get; set; }
}
