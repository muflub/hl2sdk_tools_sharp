using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Portals;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// The three per-node lists faces live on: <c>node-&gt;faces</c>,
/// <c>node-&gt;leaffacelist</c> and the detail brushes
/// <c>node-&gt;brushlist</c> gains (<c>node_t</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a side table and not three properties on the node.</b> The face
/// stage runs over <see cref="IBspNode"/>, which is the contract Phase 3c
/// defined and which deliberately carries only what portalisation, the floods
/// and the leak report touch. Putting the face lists on it would make every
/// implementation of that interface — including the one the portal gates
/// rebuild out of a stock <c>.bsp</c> — carry state it has no use for, and
/// would make this stage editable only by editing another stage's file.
/// Keyed on reference identity, which is what stock's pointer is.
/// </para>
/// <para>
/// The tree's own node type now implements <see cref="IBspNode"/> directly
/// (Phase 3e), so the key is the tree node itself; the side tables stay
/// because hand-made portal trees key on the same interface.
/// </para>
/// </remarks>
public sealed class NodeFaceLists
{
    private readonly Dictionary<IBspNode, Face?> _faces = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IBspNode, LeafFace?> _leafFaces = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IBspNode, BspBrush?> _detailBrushes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Portal, Face?[]> _portalFaces = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The face a portal produced on one side (<c>portal_t::face[2]</c>), as
    /// <c>MakeFaces_r</c> assigned it.
    /// </summary>
    /// <param name="portal">The portal.</param>
    /// <param name="side">0 for the front node's side, 1 for the back's.</param>
    /// <returns>The face, or null when that side is not visible.</returns>
    /// <remarks>
    /// <c>EmitLeaf</c> builds every leaf's face list
    /// by walking the leaf's portals and reading exactly this, so it is the
    /// write stage's only way from a leaf to the faces that bound it.
    /// </remarks>
    public Face? PortalFaceOf(Portal portal, int side)
    {
        ArgumentNullException.ThrowIfNull(portal);
        return _portalFaces.TryGetValue(portal, out Face?[]? faces) ? faces[side] : null;
    }

    /// <summary>Records the face a portal produced on one side.</summary>
    /// <param name="portal">The portal.</param>
    /// <param name="side">0 or 1.</param>
    /// <param name="face">The face, or null.</param>
    public void SetPortalFace(Portal portal, int side, Face? face)
    {
        ArgumentNullException.ThrowIfNull(portal);

        if (!_portalFaces.TryGetValue(portal, out Face?[]? faces))
        {
            faces = new Face?[2];
            _portalFaces[portal] = faces;
        }

        faces[side] = face;
    }

    /// <summary>How many nodes have a non-empty face list.</summary>
    public int NodesWithFaces
    {
        get
        {
            int count = 0;

            foreach (KeyValuePair<IBspNode, Face?> pair in _faces)
            {
                if (pair.Value is not null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>The head of a node's face list (<c>node-&gt;faces</c>).</summary>
    /// <param name="node">The node.</param>
    /// <returns>The first face, or null.</returns>
    public Face? FacesOf(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _faces.GetValueOrDefault(node);
    }

    /// <summary>Replaces the head of a node's face list.</summary>
    /// <param name="node">The node.</param>
    /// <param name="head">The new head, or null.</param>
    public void SetFacesOf(IBspNode node, Face? head)
    {
        ArgumentNullException.ThrowIfNull(node);
        _faces[node] = head;
    }

    /// <summary>Pushes a face onto the front of a node's face list.</summary>
    /// <param name="node">The node.</param>
    /// <param name="face">The face to link in.</param>
    public void PrependFace(IBspNode node, Face face)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(face);

        face.Next = FacesOf(node);
        SetFacesOf(node, face);
    }

    /// <summary>The head of a leaf's overlapping-face list (<c>leaffacelist</c>).</summary>
    /// <param name="node">The leaf.</param>
    /// <returns>The first reference, or null.</returns>
    public LeafFace? LeafFacesOf(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _leafFaces.GetValueOrDefault(node);
    }

    /// <summary>Pushes a face reference onto a leaf's list.</summary>
    /// <param name="node">The leaf.</param>
    /// <param name="face">The face that overlaps it.</param>
    public void AddLeafFace(IBspNode node, Face face)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(face);

        _leafFaces[node] = new LeafFace(face, LeafFacesOf(node));
    }

    /// <summary>
    /// The detail brush fragments filtered into a leaf
    /// (<c>AddBrushToLeaf</c>).
    /// </summary>
    /// <param name="node">The leaf.</param>
    /// <returns>The head of the chain, or null.</returns>
    /// <remarks>
    /// Stock prepends these to the leaf's existing <c>brushlist</c>, which at
    /// that point holds the world fragments <c>LeafNode</c> put there. Held
    /// separately here because <see cref="IBspNode.LeafBrushes"/> is read-only
    /// by design; whatever emits LUMP_LEAFBRUSHES has to read both, and that is
    /// the one thing the split costs.
    /// </remarks>
    public BspBrush? DetailBrushesOf(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _detailBrushes.GetValueOrDefault(node);
    }

    /// <summary>Links a detail brush fragment into a leaf.</summary>
    /// <param name="node">The leaf.</param>
    /// <param name="brush">The fragment.</param>
    public void AddBrushToLeaf(IBspNode node, BspBrush brush)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(brush);

        brush.Next = DetailBrushesOf(node);
        _detailBrushes[node] = brush;
    }
}
