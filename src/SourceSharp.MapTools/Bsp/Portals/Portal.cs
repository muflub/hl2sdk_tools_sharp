using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// One face of the boundary between two BSP nodes
/// (<c>portal_t</c>, <c>src/utils/vbsp/vbsp.h:225</c>).
/// </summary>
/// <remarks>
/// <para>
/// A portal is on the portal list of BOTH the nodes it separates at once, which
/// is why the links are indexed by side: <see cref="NextAt"/> with 0 gives the
/// next portal on <see cref="FrontNode"/>'s list, with 1 the next on
/// <see cref="BackNode"/>'s. Every loop over a node's portals therefore has to
/// work out which side it is on before it can take a step, and stock writes
/// that as <c>s = (p-&gt;nodes[1] == node)</c> in the loop body.
/// </para>
/// <para>
/// The two sides are exposed as named properties rather than a two-element
/// array so that nothing hands out a mutable array from a property; the
/// indexed accessors exist because the porting fidelity is easier to see when
/// the code reads as stock's does.
/// </para>
/// </remarks>
public sealed class Portal
{
    /// <summary>Creates a portal with the given identity.</summary>
    /// <param name="id">A number unique within the compile, for diagnostics.</param>
    public Portal(int id) => Id = id;

    /// <summary>A number unique within the compile, for diagnostics only.</summary>
    public int Id { get; }

    /// <summary>The plane the portal lies in, facing <see cref="FrontNode"/>.</summary>
    public Plane Plane { get; set; }

    /// <summary>
    /// The node whose split plane created this portal, or <see langword="null"/>
    /// when it is one of the six box portals bounding the whole tree. A
    /// <see langword="null"/> here is what "edge of world" means, and it stops
    /// both <c>Portal_VisFlood</c> and <c>MarkVisibleSides_r</c>.
    /// </summary>
    public IBspNode? OnNode { get; set; }

    /// <summary><c>nodes[0]</c>: the node in front of <see cref="Plane"/>.</summary>
    public IBspNode? FrontNode { get; set; }

    /// <summary><c>nodes[1]</c>: the node behind <see cref="Plane"/>.</summary>
    public IBspNode? BackNode { get; set; }

    /// <summary><c>next[0]</c>: the next portal on <see cref="FrontNode"/>'s list.</summary>
    public Portal? NextFront { get; set; }

    /// <summary><c>next[1]</c>: the next portal on <see cref="BackNode"/>'s list.</summary>
    public Portal? NextBack { get; set; }

    /// <summary>The portal's polygon, in the arena that allocated it.</summary>
    public Winding Winding { get; set; } = Winding.Null;

    /// <summary>Whether <see cref="Side"/> has been looked for yet.</summary>
    public bool SideFound { get; set; }

    /// <summary>
    /// The brush side chosen to texture this portal, or <see langword="null"/>
    /// when it is not a visible content change.
    /// </summary>
    public MapBrushSide? Side { get; set; }

    /// <summary>The node on one side, indexed as stock indexes <c>nodes</c>.</summary>
    /// <param name="side">0 for the front node, 1 for the back node.</param>
    /// <returns>That node.</returns>
    public IBspNode? NodeAt(int side) => side == 0 ? FrontNode : BackNode;

    /// <summary>Replaces the node on one side.</summary>
    /// <param name="side">0 for the front node, 1 for the back node.</param>
    /// <param name="node">The node to put there.</param>
    public void SetNodeAt(int side, IBspNode? node)
    {
        if (side == 0)
        {
            FrontNode = node;
        }
        else
        {
            BackNode = node;
        }
    }

    /// <summary>The next portal on one side's list, indexed as stock indexes <c>next</c>.</summary>
    /// <param name="side">0 for the front node's list, 1 for the back node's.</param>
    /// <returns>That link.</returns>
    public Portal? NextAt(int side) => side == 0 ? NextFront : NextBack;

    /// <summary>Replaces the next link on one side's list.</summary>
    /// <param name="side">0 for the front node's list, 1 for the back node's.</param>
    /// <param name="next">The portal to link to.</param>
    public void SetNextAt(int side, Portal? next)
    {
        if (side == 0)
        {
            NextFront = next;
        }
        else
        {
            NextBack = next;
        }
    }

    /// <summary>
    /// Which side of this portal a node is on, as stock's
    /// <c>s = (p-&gt;nodes[1] == node)</c>.
    /// </summary>
    /// <param name="node">The node to locate.</param>
    /// <returns>0 when it is the front node, 1 when it is the back node.</returns>
    public int SideOf(IBspNode node) => ReferenceEquals(BackNode, node) ? 1 : 0;

    /// <summary>Copies every field except the identity, as stock's <c>*new_portal = *p</c>.</summary>
    /// <param name="source">The portal to copy from.</param>
    public void CopyFrom(Portal source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Plane = source.Plane;
        OnNode = source.OnNode;
        FrontNode = source.FrontNode;
        BackNode = source.BackNode;
        NextFront = source.NextFront;
        NextBack = source.NextBack;
        Winding = source.Winding;
        SideFound = source.SideFound;
        Side = source.Side;
    }
}
