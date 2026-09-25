using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Portals;

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// A face of the BSP tree. <b>Phase 3d owns the type.</b>
/// </summary>
/// <remarks>
/// Stock's <c>face_t</c> carries a winding, a
/// texinfo, a dispinfo, merge and split back-pointers and a vertex list, none
/// of which the CSG or the tree build reads. What THEY do with a face is
/// exactly three things: <c>PruneNodes_r</c> refuses to collapse a node that
/// has any, <c>FreeTree_r</c> releases the chain
/// And both walk it through <c>next</c>. That is this
/// interface, and nothing in Phase 3b constructs one.
/// </remarks>
public interface IBspFace
{
    /// <summary>The next face on the same node: <c>face_t::next</c>.</summary>
    IBspFace? Next { get; set; }
}

/// <summary>
/// One node or leaf of the BSP tree: <c>node_t</c>,
/// </summary>
/// <remarks>
/// <para>
/// <b>Nodes and leaves are the same type, told apart by
/// <see cref="PlaneNumber"/>.</b> <see cref="Leaf"/> (-1) means a leaf, and
/// then <see cref="Contents"/>, <see cref="BrushList"/> and the
/// <c>occupied</c>/<c>cluster</c>/<c>area</c> fields are the live ones; any
/// other value means a node, and then <see cref="Side"/>,
/// <see cref="Children"/> and <see cref="Faces"/> are. Stock comments the
/// struct in exactly those three groups and this keeps them.
/// </para>
/// <para>
/// A leaf is NOT a permanent state: <c>PruneNodes_r</c> turns a node into one
/// by assigning <see cref="Leaf"/> over its plane number
/// After which its children are still reachable through
/// <see cref="Children"/> and are simply never walked again.
/// </para>
/// </remarks>
public sealed class BspNode : IBspNode
{
    private readonly BspNode?[] _children = new BspNode?[2];

    /// <summary>
    /// <c>PLANENUM_LEAF</c>: the
    /// <see cref="PlaneNumber"/> of a leaf.
    /// </summary>
    public const int Leaf = -1;

    /// <summary>The node's serial number: <c>node_t::id</c>.</summary>
    /// <remarks>
    /// Stock's <c>s_NodeCount</c> counts every node ever allocated in the
    /// process, so ids are unique across blocks and
    /// across models but are not the order a tree is walked in.
    /// </remarks>
    public int Id { get; set; }

    /// <summary>
    /// The splitting plane, or <see cref="Leaf"/>: <c>planenum</c>.
    /// </summary>
    /// <remarks>
    /// Always the EVEN member of a plane pair on a real node —
    /// <c>BuildTree_r</c> assigns <c>bestside-&gt;planenum &amp; ~1</c>,
    /// "always use front facing" — which is what
    /// makes <see cref="Children"/>[0] the front child everywhere.
    /// </remarks>
    public int PlaneNumber { get; set; }

    /// <summary>The node above this one, or null at the head.</summary>
    public BspNode? Parent { get; set; }

    /// <summary>The node's bounds. Valid only after portalization (3c).</summary>
    public Vec3 Mins { get; set; }

    /// <summary>The node's bounds. Valid only after portalization (3c).</summary>
    public Vec3 Maxs { get; set; }

    /// <summary>
    /// The convex volume this node occupies: <c>volume</c>.
    /// </summary>
    /// <remarks>
    /// One brush per node and per leaf, built by splitting the parent's volume
    /// on the parent's plane. The head node's is the
    /// whole block, from <c>BrushFromBounds</c>. It is what
    /// <c>CheckPlaneAgainstVolume</c> tests a candidate splitter against, so a
    /// plane that misses the node's own volume is never chosen.
    /// </remarks>
    public BspBrush? Volume { get; set; }

    /// <summary>
    /// The brush side whose plane created this node: <c>side</c>.
    /// </summary>
    /// <remarks>
    /// A copy and not a reference, because the side it names is a
    /// <see cref="BspBrushSide"/> inside a brush that <c>BuildTree_r</c> frees
    /// on the next line. Stock keeps the pointer and
    /// it dangles; every later reader of <c>node-&gt;side</c> —
    /// <c>FindPortalSide</c> in Phase 3c is the main one — reads freed memory
    /// that happens still to hold the side. Copying the nine fields is the same
    /// behaviour without the read.
    /// </remarks>
    public BspBrushSide Side { get; set; }

    /// <summary>Whether <see cref="Side"/> holds a side at all.</summary>
    /// <remarks>
    /// Stock sets <c>node-&gt;side = NULL</c> on a leaf
    /// And a value type has no null, so the
    /// distinction is carried here.
    /// </remarks>
    public bool HasSide { get; set; }

    /// <summary>The front and back children: <c>children[2]</c>.</summary>
    /// <remarks>
    /// <c>children[0]</c> is the FRONT side of <see cref="PlaneNumber"/> and
    /// <c>children[1]</c> is the back, everywhere:
    /// <c>SplitBrushList</c>'s outputs,
    /// <c>PointInLeaf</c>'s descent and
    /// <c>BlockTree</c>'s construction all agree.
    /// </remarks>
    public Span<BspNode?> Children => _children;

    /// <summary>
    /// The faces in the plane of <see cref="Side"/>: <c>faces</c>. Phase 3d
    /// fills it.
    /// </summary>
    public IBspFace? Faces { get; set; }

    /// <summary>
    /// The brush fragments that ended up in this leaf: <c>brushlist</c>.
    /// </summary>
    public BspBrush? BrushList { get; set; }

    /// <summary>
    /// The OR of every brush's contents in this leaf: <c>contents</c>.
    /// </summary>
    /// <remarks>
    /// Set by <c>LeafNode</c>, with one exception
    /// that overrides the OR entirely: a solid brush whose every side is
    /// already on a node "eats everything", and the leaf becomes exactly
    /// <c>CONTENTS_SOLID</c>.
    /// </remarks>
    public int Contents { get; set; }

    /// <summary>
    /// How far this leaf is from an entity: <c>occupied</c>. Phase 3c fills it.
    /// </summary>
    public int Occupied { get; set; }

    /// <summary>
    /// The entity that reached this leaf: <c>occupant</c>. Phase 3c fills it.
    /// </summary>
    public MapEntity? Occupant { get; set; }

    /// <summary>The vis cluster: <c>cluster</c>. Phase 3c fills it.</summary>
    public int Cluster { get; set; }

    /// <summary>The area: <c>area</c>. Phase 3c fills it.</summary>
    public int Area { get; set; }

    /// <summary>
    /// The portals threaded through this node: <c>portals</c>. Phase 3c fills
    /// it.
    /// </summary>
    public Portal? Portals { get; set; }

    /// <summary>
    /// The index this node was written to in the BSP: <c>diskId</c>.
    /// </summary>
    /// <remarks>
    /// -1 until <c>WriteBSP</c> (Phase 3e) assigns it, exactly as
    /// <c>AllocNode</c> initialises it — which is why
    /// it is the one field of a freshly allocated node that is not zero.
    /// </remarks>
    public int DiskId { get; set; } = -1;

    /// <summary>Whether this node is a leaf.</summary>
    public bool IsLeaf => PlaneNumber == Leaf;

    // --- IBspNode: the portal, face and write stages' view of the same node ---
    //
    // Stock has ONE node_t that BuildTree_r fills, MakeTreePortals
    // annotates, MakeFaces reads and WriteBSP numbers. Phase 3b and 3c each
    // defined their half of it; this is where the halves meet, so every stage
    // walks the same objects and reference identity -- which every portal loop
    // relies on (s = (p->nodes[1] == node)) -- is the node's own. Only the
    // members whose TYPES differ between the two halves are explicit.

    /// <inheritdoc/>
    IBspNode? IBspNode.Parent => Parent;

    /// <inheritdoc/>
    IBspNode? IBspNode.Front => _children[0];

    /// <inheritdoc/>
    IBspNode? IBspNode.Back => _children[1];

    /// <summary>
    /// Always null: this node carries its side BY VALUE (<see cref="Side"/>, a
    /// <see cref="BspBrushSide"/> copy), and the interface wants the MAP side,
    /// which the copy does not record.
    /// </summary>
    /// <remarks>
    /// The only reader is the material name in the unbounded-volume warning
    /// So a null costs that one warning its texture
    /// name and nothing else.
    /// </remarks>
    MapBrushSide? IBspNode.Side => null;

    /// <summary>
    /// The originals of this leaf's brush fragments, one entry per fragment and
    /// in <c>brushlist</c> order, duplicates kept.
    /// </summary>
    /// <remarks>
    /// Projected on every read rather than cached: <c>RemoveAreaPortalBrushes_R</c>
    /// unlinks fragments through <c>prev-&gt;next</c>, which a cache keyed on
    /// <see cref="BrushList"/> could not see. The lists are a handful of
    /// entries and the readers (<c>FindPortalSide</c>,
    /// <c>AreaportalBrushForNode</c>) are per portal, not per sample.
    /// </remarks>
    IReadOnlyList<MapBrush> IBspNode.LeafBrushes
    {
        get
        {
            List<MapBrush> originals = [];
            for (BspBrush? brush = BrushList; brush is not null; brush = brush.Next)
            {
                if (brush.Original is not null)
                {
                    originals.Add(brush.Original);
                }
            }

            return originals;
        }
    }
}
