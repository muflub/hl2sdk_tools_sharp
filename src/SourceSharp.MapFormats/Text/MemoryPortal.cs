using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One of the two <c>portal_t</c>s the reference visibility pass builds from a
/// single file portal.
/// </summary>
/// <param name="OwningCluster">
/// The cluster whose portal list this portal is filed under: <c>leafnums[0]</c>
/// for the forward portal, <c>leafnums[1]</c> for the backward one.
/// </param>
/// <param name="Leaf">
/// <c>portal_t::leaf</c>, the cluster on the OTHER side -- the one flooding
/// through this portal reaches. Note it is the neighbour, not the owner.
/// </param>
/// <param name="Normal">The plane normal, negated on the forward portal.</param>
/// <param name="Distance">The plane distance, negated on the forward portal.</param>
/// <param name="Points">
/// The winding: as written for the forward portal, reversed for the backward
/// one.
/// </param>
/// <param name="IsOriginalWinding">
/// <c>winding_t::original</c>. True only on the forward portal, which SHARES
/// the winding object the loader built; the backward portal gets a fresh copy
/// whose flag is left false by the zeroing in <c>NewWinding</c>.
/// </param>
public sealed record MemoryPortal(
    int OwningCluster,
    int Leaf,
    Vec3 Normal,
    float Distance,
    IReadOnlyList<Vec3> Points,
    bool IsOriginalWinding);
