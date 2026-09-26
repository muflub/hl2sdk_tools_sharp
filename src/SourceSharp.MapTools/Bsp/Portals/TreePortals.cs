//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Portals;

/// <summary>
/// Turning a built BSP tree into portals: the boundary faces between every
/// Pair of adjacent nodes.
/// </summary>
/// <remarks>
/// <para>
/// The shape of the algorithm is: give the head node six portals to the outside
/// leaf, then walk down. At each splitting node, build the full winding of its
/// plane, clip it by every portal already bounding that node, and hand the
/// result to the two children as a new portal between them; then cut every
/// portal the node had into a front half and a back half and give those to the
/// children too.
/// </para>
/// <para>
/// Three different epsilons take part in that, which is worth knowing before
/// anyone tries to tidy them into one: the plane winding is clipped by its
/// parents at <see cref="BaseWindingEpsilon"/> (0.001), by the node's sibling
/// portals at a hardcoded 0.1, and existing portals
/// are split at <see cref="SplitWindingEpsilon"/> (0.001). They are not
/// tunables.
/// </para>
/// </remarks>
public sealed class TreePortals
{
    /// <summary><c>SIDESPACE</c>: slack around the tree's bounds.</summary>
    public const float SideSpace = 8f;

    /// <summary><c>BASE_WINDING_EPSILON</c>.</summary>
    public const float BaseWindingEpsilon = 0.001f;

    /// <summary><c>SPLIT_WINDING_EPSILON</c>.</summary>
    public const float SplitWindingEpsilon = 0.001f;

    /// <summary>The epsilon <c>MakeNodePortal</c> clips by, written inline.</summary>
    public const float NodePortalClipEpsilon = 0.1f;

    /// <summary><c>MIN_COORD_INTEGER</c>.</summary>
    public const float MinCoordInteger = GeometryEpsilons.MinCoordInteger;

    /// <summary><c>MAX_COORD_INTEGER</c>.</summary>
    public const float MaxCoordInteger = GeometryEpsilons.MaxCoordInteger;

    /// <summary>A node's volume came out empty.</summary>
    public const string NodeWithoutVolume = "VBSP0301";

    /// <summary>A node's volume ran past the world's coordinate limits.</summary>
    public const string NodeWithUnboundedVolume = "VBSP0302";

    private readonly WindingArena _arena;
    private readonly PlaneTable _planes;
    private int _nextPortalId;

    /// <summary>Creates a portaliser over one compile's winding arena and plane table.</summary>
    /// <param name="windings">The arena every portal winding is allocated from.</param>
    /// <param name="planes">The map's plane table; node plane numbers index it.</param>
    public TreePortals(WindingArena windings, PlaneTable planes)
    {
        ArgumentNullException.ThrowIfNull(windings);
        ArgumentNullException.ThrowIfNull(planes);
        _arena = windings;
        _planes = planes;
    }

    /// <summary>Where warnings about the tree's geometry are collected.</summary>
    public IList<CompileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Names the material on a brush side, for the unbounded-volume warning.
    /// Left unset, that warning says <c>&lt;NO BRUSH&gt;</c>, which is also what
    /// stock says when the node has no side.
    /// </summary>
    public Func<MapBrushSide, string>? MaterialName { get; init; }

    /// <summary>How many portals are currently allocated (<c>c_active_portals</c>).</summary>
    public int ActivePortals { get; private set; }

    /// <summary>The high-water mark of <see cref="ActivePortals"/> (<c>c_peak_portals</c>).</summary>
    public int PeakPortals { get; private set; }

    /// <summary>
    /// How many windings were discarded by <see cref="IsTiny"/>
    /// (<c>c_tinyportals</c>).
    /// </summary>
    public int TinyPortals { get; private set; }

    /// <summary>
    /// <c>EDGE_LENGTH</c> as stock spells it: <c>0.2</c> with no <c>f</c>, so a
    /// DOUBLE.
    /// </summary>
    /// <remarks>
    /// The type is the behaviour. See
    /// <see cref="StockQuirk.WindingIsTinyEdgePromotion"/>. Kept beside
    /// <see cref="CorrectEdgeLength"/> so the pair reads as one decision;
    /// <c>BrushGeometry.EdgeLength</c> is the same number for the other copy of
    /// this function and a fact holds the two together.
    /// </remarks>
    public const double StockEdgeLength = 0.2;

    /// <summary>The same threshold in the float the surrounding types imply.</summary>
    public const float CorrectEdgeLength = 0.2f;

    /// <summary>
    /// Whether a winding would be crunched out of existence by vertex snapping
    /// (<c>WindingIsTiny</c>).
    /// </summary>
    /// <param name="arena">The arena holding the winding.</param>
    /// <param name="winding">The winding to measure.</param>
    /// <returns><see langword="true"/> when it has fewer than three edges longer than 0.2.</returns>
    /// <remarks>
    /// Lives here rather than with the rest of the reference implementation because
    /// Is its only other caller and the two stages were
    /// ported separately; it is eight lines and has no state.
    /// </remarks>
    public static bool IsTiny(WindingArena arena, Winding winding)
    {
        ArgumentNullException.ThrowIfNull(arena);

        // StockQuirk.WindingIsTinyEdgePromotion. EDGE_LENGTH
        // is spelled `0.2` with no `f`, so `len > EDGE_LENGTH` promotes the
        // float length to double and an edge of exactly 0.2f
        // (0.20000000298...) compares as LONGER than the threshold. Comparing
        // in float, as the surrounding types imply, flips that.
        bool stock = arena.Compliance.Emulates(StockQuirk.WindingIsTinyEdgePromotion);

        Span<Vec3> points = arena.Points(winding);
        int edges = 0;

        for (int i = 0; i < points.Length; i++)
        {
            int j = i == points.Length - 1 ? 0 : i + 1;
            float len = (points[j] - points[i]).Length();

            bool longEdge = stock
                ? len > StockEdgeLength
                : len > CorrectEdgeLength;

            if (longEdge && ++edges == 3)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>VectorSubtract(vec3_origin, v, out)</c>: a negation that turns
    /// <c>-0.0</c> into <c>+0.0</c>, where unary minus would not.
    /// </summary>
    /// <param name="v">The vector to negate.</param>
    /// <returns>Zero minus it, component by component.</returns>
    public static Vec3 SubtractFromOrigin(Vec3 v) => new(0f - v.X, 0f - v.Y, 0f - v.Z);

    /// <summary>Allocates a portal(<c>AllocPortal</c>).</summary>
    /// <returns>A fresh portal with a unique id.</returns>
    public Portal AllocPortal()
    {
        ActivePortals++;

        if (ActivePortals > PeakPortals)
        {
            PeakPortals = ActivePortals;
        }

        return new Portal(_nextPortalId++);
    }

    /// <summary>Frees a portal and its winding(<c>FreePortal</c>).</summary>
    /// <param name="portal">The portal to free.</param>
    public void FreePortal(Portal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);

        if (!portal.Winding.IsNull)
        {
            _arena.Free(portal.Winding);
            portal.Winding = Winding.Null;
        }

        ActivePortals--;
    }

    /// <summary>
    /// Threads a portal onto both of the nodes it separates
    /// (<c>AddPortalToNodes</c>).
    /// </summary>
    /// <param name="portal">The portal to link.</param>
    /// <param name="front">The node in front of its plane.</param>
    /// <param name="back">The node behind it.</param>
    /// <exception cref="MapCompileException">The portal is already on a list.</exception>
    public static void AddPortalToNodes(Portal portal, IBspNode front, IBspNode back)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(front);
        ArgumentNullException.ThrowIfNull(back);

        if (portal.FrontNode is not null || portal.BackNode is not null)
        {
            throw new MapCompileException("AddPortalToNode: allready included");
        }

        portal.FrontNode = front;
        portal.NextFront = front.Portals;
        front.Portals = portal;

        portal.BackNode = back;
        portal.NextBack = back.Portals;
        back.Portals = portal;
    }

    /// <summary>
    /// Unthreads a portal from one node's list
    /// (<c>RemovePortalFromNode</c>).
    /// </summary>
    /// <param name="portal">The portal to unlink.</param>
    /// <param name="node">The node to unlink it from.</param>
    /// <exception cref="MapCompileException">The portal is not on that node's list.</exception>
    public static void RemovePortalFromNode(Portal portal, IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(node);

        // Stock walks a pointer-to-pointer; the managed equivalent is to
        // remember the portal that holds the link and which of its two links
        // that is.
        Portal? holder = null;
        int holderSide = 0;
        Portal? current = node.Portals;

        while (true)
        {
            if (current is null)
            {
                throw new MapCompileException("RemovePortalFromNode: portal not in leaf");
            }

            if (ReferenceEquals(current, portal))
            {
                break;
            }

            if (ReferenceEquals(current.FrontNode, node))
            {
                holder = current;
                holderSide = 0;
                current = current.NextFront;
            }
            else if (ReferenceEquals(current.BackNode, node))
            {
                holder = current;
                holderSide = 1;
                current = current.NextBack;
            }
            else
            {
                throw new MapCompileException("RemovePortalFromNode: portal not bounding leaf");
            }
        }

        Portal? replacement;

        if (ReferenceEquals(portal.FrontNode, node))
        {
            replacement = portal.NextFront;
            portal.FrontNode = null;
        }
        else if (ReferenceEquals(portal.BackNode, node))
        {
            replacement = portal.NextBack;
            portal.BackNode = null;
        }
        else
        {
            // Stock has no else here: the list link is left alone.
            return;
        }

        if (holder is null)
        {
            node.Portals = replacement;
        }
        else
        {
            holder.SetNextAt(holderSide, replacement);
        }
    }

    /// <summary>
    /// Frees every portal in a subtree
    /// (<c>FreeTreePortals_r</c>).
    /// </summary>
    /// <param name="node">The root of the subtree.</param>
    /// <remarks>
    /// This also clears the outside leaf's list, because each box portal is
    /// unlinked from BOTH its nodes before being freed.
    /// </remarks>
    public void FreeTreePortals(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.IsLeaf())
        {
            FreeTreePortals(node.Front!);
            FreeTreePortals(node.Back!);
        }

        Portal? p = node.Portals;

        while (p is not null)
        {
            int side = p.SideOf(node);
            Portal? next = p.NextAt(side);

            RemovePortalFromNode(p, p.NodeAt(1 - side)!);
            FreePortal(p);

            p = next;
        }

        node.Portals = null;
    }

    /// <summary>
    /// Gives the head node six portals to the outside leaf
    /// (<c>MakeHeadnodePortals</c>).
    /// </summary>
    /// <param name="tree">The tree to bound.</param>
    /// <remarks>
    /// The box is the tree's own bounds padded by <see cref="SideSpace"/> on
    /// every side, "so there will never be null volume leafs". That padding is
    /// visible in the output: the first point of a leak line is the centre of
    /// one of these six faces, and it sits 8 units outside the tree bounds,
    /// which are themselves 8 units outside the map in z.
    /// </remarks>
    public void MakeHeadnodePortals(IBspTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        IBspNode node = tree.HeadNode;
        IBspNode outside = tree.OutsideNode;

        Span<Vec3> bounds =
        [
            new(tree.Mins.X - SideSpace, tree.Mins.Y - SideSpace, tree.Mins.Z - SideSpace),
            new(tree.Maxs.X + SideSpace, tree.Maxs.Y + SideSpace, tree.Maxs.Z + SideSpace),
        ];

        outside.Contents = 0;
        outside.Portals = null;

        Portal[] portals = new Portal[6];
        Plane[] boxPlanes = new Plane[6];

        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                int n = (j * 3) + i;

                Portal p = AllocPortal();
                portals[n] = p;

                Vec3 normal;
                float dist;

                if (j != 0)
                {
                    normal = Axis(i, -1f);
                    dist = -bounds[j][i];
                }
                else
                {
                    normal = Axis(i, 1f);
                    dist = bounds[j][i];
                }

                boxPlanes[n] = new Plane(normal, dist);
                p.Plane = boxPlanes[n];
                p.Winding = _arena.BaseWindingForPlane(normal, dist);
                AddPortalToNodes(p, node, outside);
            }
        }

        // clip the basewindings by all the other planes
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                if (j == i)
                {
                    continue;
                }

                portals[i].Winding = _arena.ChopInPlace(
                    portals[i].Winding,
                    boxPlanes[j].Normal,
                    boxPlanes[j].Dist,
                    (float)GeometryEpsilons.OnEpsilon);
            }
        }
    }

    /// <summary>
    /// The full winding of a node's split plane, clipped by every ancestor
    /// (<c>BaseWindingForNode</c>).
    /// </summary>
    /// <param name="node">The splitting node.</param>
    /// <returns>The winding, or <see cref="Winding.Null"/> if it clipped away.</returns>
    public Winding BaseWindingForNode(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Plane nodePlane = _planes[node.PlaneNumber];
        Winding w = _arena.BaseWindingForPlane(nodePlane.Normal, nodePlane.Dist);

        IBspNode child = node;
        IBspNode? parent = node.Parent;

        while (parent is not null && !w.IsNull)
        {
            Plane plane = _planes[parent.PlaneNumber];

            if (ReferenceEquals(parent.Front, child))
            {
                // take front
                w = _arena.ChopInPlace(w, plane.Normal, plane.Dist, BaseWindingEpsilon);
            }
            else
            {
                // take back
                w = _arena.ChopInPlace(w, SubtractFromOrigin(plane.Normal), -plane.Dist, BaseWindingEpsilon);
            }

            child = parent;
            parent = parent.Parent;
        }

        return w;
    }

    /// <summary>
    /// Creates the portal between a node's two children
    /// (<c>MakeNodePortal</c>).
    /// </summary>
    /// <param name="node">The splitting node.</param>
    public void MakeNodePortal(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Winding w = BaseWindingForNode(node);

        // clip the portal by all the other portals in the node
        Portal? p = node.Portals;

        while (p is not null && !w.IsNull)
        {
            Vec3 normal;
            float dist;
            int side;

            if (ReferenceEquals(p.FrontNode, node))
            {
                side = 0;
                normal = p.Plane.Normal;
                dist = p.Plane.Dist;
            }
            else if (ReferenceEquals(p.BackNode, node))
            {
                side = 1;
                normal = SubtractFromOrigin(p.Plane.Normal);
                dist = -p.Plane.Dist;
            }
            else
            {
                throw new MapCompileException("CutNodePortals_r: mislinked portal");
            }

            w = _arena.ChopInPlace(w, normal, dist, NodePortalClipEpsilon);
            p = p.NextAt(side);
        }

        if (w.IsNull)
        {
            return;
        }

        if (IsTiny(_arena, w))
        {
            TinyPortals++;
            _arena.Free(w);
            return;
        }

        Portal newPortal = AllocPortal();
        newPortal.Plane = _planes[node.PlaneNumber];
        newPortal.OnNode = node;
        newPortal.Winding = w;

        AddPortalToNodes(newPortal, node.Front!, node.Back!);
    }

    /// <summary>
    /// Hands a node's portals down to its two children, splitting the ones the
    /// Node's plane cuts(<c>SplitNodePortals</c>).
    /// </summary>
    /// <param name="node">The splitting node.</param>
    public void SplitNodePortals(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Plane plane = _planes[node.PlaneNumber];
        IBspNode front = node.Front!;
        IBspNode back = node.Back!;

        Portal? p = node.Portals;

        while (p is not null)
        {
            int side;

            if (ReferenceEquals(p.FrontNode, node))
            {
                side = 0;
            }
            else if (ReferenceEquals(p.BackNode, node))
            {
                side = 1;
            }
            else
            {
                throw new MapCompileException("CutNodePortals_r: mislinked portal");
            }

            Portal? nextPortal = p.NextAt(side);
            IBspNode otherNode = p.NodeAt(1 - side)!;

            RemovePortalFromNode(p, p.FrontNode!);
            RemovePortalFromNode(p, p.BackNode!);

            // cut the portal into two portals, one on each side of the cut plane
            _arena.ClipEpsilon(
                p.Winding, plane.Normal, plane.Dist, SplitWindingEpsilon,
                out Winding frontWinding, out Winding backWinding);

            if (!frontWinding.IsNull && IsTiny(_arena, frontWinding))
            {
                _arena.Free(frontWinding);
                frontWinding = Winding.Null;
                TinyPortals++;
            }

            if (!backWinding.IsNull && IsTiny(_arena, backWinding))
            {
                _arena.Free(backWinding);
                backWinding = Winding.Null;
                TinyPortals++;
            }

            if (frontWinding.IsNull && backWinding.IsNull)
            {
                // tiny windings on both sides
                p = nextPortal;
                continue;
            }

            if (frontWinding.IsNull)
            {
                _arena.Free(backWinding);

                if (side == 0)
                {
                    AddPortalToNodes(p, back, otherNode);
                }
                else
                {
                    AddPortalToNodes(p, otherNode, back);
                }

                p = nextPortal;
                continue;
            }

            if (backWinding.IsNull)
            {
                _arena.Free(frontWinding);

                if (side == 0)
                {
                    AddPortalToNodes(p, front, otherNode);
                }
                else
                {
                    AddPortalToNodes(p, otherNode, front);
                }

                p = nextPortal;
                continue;
            }

            // the winding is split
            Portal newPortal = AllocPortal();
            newPortal.CopyFrom(p);
            newPortal.Winding = backWinding;
            _arena.Free(p.Winding);
            p.Winding = frontWinding;

            if (side == 0)
            {
                AddPortalToNodes(p, front, otherNode);
                AddPortalToNodes(newPortal, back, otherNode);
            }
            else
            {
                AddPortalToNodes(p, otherNode, front);
                AddPortalToNodes(newPortal, otherNode, back);
            }

            p = nextPortal;
        }

        node.Portals = null;
    }

    /// <summary>
    /// Recomputes a node's bounds from the portals that bound it
    /// (<c>CalcNodeBounds</c>).
    /// </summary>
    /// <param name="node">The node or leaf to measure.</param>
    public void CalcNodeBounds(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Vec3 mins = MapFile.ClearedMins;
        Vec3 maxs = MapFile.ClearedMaxs;

        for (Portal? p = node.Portals; p is not null;)
        {
            int side = p.SideOf(node);

            foreach (Vec3 point in _arena.Points(p.Winding))
            {
                mins = new Vec3(
                    MathF.Min(mins.X, point.X),
                    MathF.Min(mins.Y, point.Y),
                    MathF.Min(mins.Z, point.Z));
                maxs = new Vec3(
                    MathF.Max(maxs.X, point.X),
                    MathF.Max(maxs.Y, point.Y),
                    MathF.Max(maxs.Z, point.Z));
            }

            p = p.NextAt(side);
        }

        node.Mins = mins;
        node.Maxs = maxs;
    }

    /// <summary>
    /// Portalises a whole tree
    /// (<c>MakeTreePortals</c>).
    /// </summary>
    /// <param name="tree">The tree to portalise.</param>
    public void MakeTreePortals(IBspTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        MakeHeadnodePortals(tree);
        MakeTreePortalsRecursive(tree.HeadNode);
    }

    /// <summary>
    /// The second portalisation, for the portal file: no bounds, no warnings,
    /// and it stops at the leaves
    /// (<c>CreateVisPortals_r</c>).
    /// </summary>
    /// <param name="node">The node to portalise from.</param>
    public void CreateVisPortals(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // stop as soon as we get to a leaf
        if (node.IsLeaf())
        {
            return;
        }

        MakeNodePortal(node);
        SplitNodePortals(node);

        CreateVisPortals(node.Front!);
        CreateVisPortals(node.Back!);
    }

    private static Vec3 Axis(int i, float value) => i switch
    {
        0 => new Vec3(value, 0f, 0f),
        1 => new Vec3(0f, value, 0f),
        _ => new Vec3(0f, 0f, value),
    };

    private void MakeTreePortalsRecursive(IBspNode node)
    {
        CalcNodeBounds(node);

        if (node.Mins.X >= node.Maxs.X)
        {
            Diagnostics.Add(new CompileDiagnostic(
                NodeWithoutVolume,
                DiagnosticSeverity.Warning,
                "node without a volume"));
        }

        for (int i = 0; i < 3; i++)
        {
            if (node.Mins[i] >= MinCoordInteger - SideSpace && node.Maxs[i] <= MaxCoordInteger + SideSpace)
            {
                continue;
            }

            string materialName = "<NO BRUSH>";

            if (node.Side is not null && MaterialName is not null)
            {
                materialName = MaterialName(node.Side);
            }

            (float X, float Y, float Z)? position = null;

            if (node.Portals is not null)
            {
                Span<Vec3> points = _arena.Points(node.Portals.Winding);

                if (points.Length > 0)
                {
                    position = (points[0].X, points[0].Y, points[0].Z);
                }
            }

            Diagnostics.Add(new CompileDiagnostic(
                NodeWithUnboundedVolume,
                DiagnosticSeverity.Warning,
                $"BSP node with unbounded volume (material: {materialName})",
                new MapLocation(Position: position)));
            break;
        }

        if (node.IsLeaf())
        {
            return;
        }

        MakeNodePortal(node);
        SplitNodePortals(node);

        MakeTreePortalsRecursive(node.Front!);
        MakeTreePortalsRecursive(node.Back!);
    }
}
