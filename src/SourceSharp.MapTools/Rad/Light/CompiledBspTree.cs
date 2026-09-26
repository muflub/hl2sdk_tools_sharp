//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// Point queries against a COMPILED map's node and leaf lumps:
/// <c>PointInLeaf</c> / <c>ClusterFromPoint</c> and
/// <c>PointLeafnum</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are two different descents and both are needed.</b>
/// <see cref="ClusterFromPoint"/> treats a point within 0.1 of a splitting
/// plane as being on BOTH sides and prefers whichever child gives a real
/// cluster; <see cref="LeafFromPoint"/> takes the front side of a
/// zero-distance point with no epsilon at all, and uses the plane's axial
/// TYPE field to skip the dot product. The first is what patch clustering
/// wants -- a patch sitting exactly on a wall should not report "solid" -- and
/// the second is what a ray walk wants. Merging them would quietly change
/// which patches get lit.
/// </para>
/// <para>
/// Distinct from <c>SourceSharp.MapTools.Bsp.Tree.BrushBspTree.PointInLeaf</c>,
/// which walks vbsp's IN-PROGRESS tree of <c>BspNode</c> objects. This one
/// walks the lumps, which is all vrad ever has.
/// </para>
/// <para>
/// This type is a candidate to be hoisted out of the lighting namespace: leaf
/// ambient needs the same queries. It lives here because this lane wrote it
/// first, not because it belongs here.
/// </para>
/// </remarks>
public sealed class CompiledBspTree
{
    /// <summary>
    /// <c>TEST_EPSILON</c>: 0.1, how near a splitting
    /// plane counts as being on it.
    /// </summary>
    public const float TestEpsilon = 0.1f;

    private readonly DNode[] _nodes;
    private readonly DPlane[] _planes;
    private readonly LeafInfo[] _leaves;

    /// <summary>Binds the queries to a map's lumps.</summary>
    /// <param name="geometry">The map.</param>
    /// <exception cref="ArgumentNullException"><paramref name="geometry"/> is null.</exception>
    public CompiledBspTree(LightGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        _nodes = geometry.Nodes;
        _planes = geometry.Planes;
        _leaves = geometry.Leaves;
    }

    /// <summary>How many leaves the map has.</summary>
    public int LeafCount => _leaves.Length;

    /// <summary>
    /// <c>ClusterFromPoint</c>.
    /// </summary>
    /// <param name="point">A world point.</param>
    /// <returns>The vis cluster, or -1 when the point is in solid space.</returns>
    /// <remarks>
    /// A map with no nodes -- which nothing vbsp writes, but a synthetic
    /// fixture can be -- descends straight into leaf 0.
    /// </remarks>
    public int ClusterFromPoint(Vec3 point) => _leaves[PointInLeaf(0, point)].Cluster;

    /// <summary>
    /// <c>PointInLeaf</c>: the descent that straddles.
    /// </summary>
    /// <param name="node">
    /// The node to start at, or a negative value naming a leaf as
    /// <c>-1 - leaf</c>.
    /// </param>
    /// <param name="point">A world point.</param>
    /// <returns>The leaf index.</returns>
    /// <remarks>
    /// <para>
    /// The straddle branch is the whole point of this function. A point within
    /// <see cref="TestEpsilon"/> of the plane descends the FRONT child first;
    /// if that leaf has no cluster -- it is solid -- it descends the back child
    /// instead and returns whatever that gives, cluster or not. So a patch
    /// whose origin sits exactly on a wall's surface gets the cluster of the
    /// open side, which is what makes it visible to lights.
    /// </para>
    /// <para>
    /// Stock recurses; this iterates where it can and recurses only on the
    /// straddle branch, which is the only one that needs a second descent.
    /// </para>
    /// </remarks>
    public int PointInLeaf(int node, Vec3 point)
    {
        while (node >= 0)
        {
            if (node >= _nodes.Length)
            {
                // A tree with no interior nodes: stock would read past the
                // end of dnodes. There is nothing to reproduce, so this
                // answers leaf 0, which is the solid leaf every map has.
                return 0;
            }

            ref readonly DNode n = ref _nodes[node];
            ref readonly DPlane plane = ref _planes[n.PlaneNum];

            float dist = Vec3.Dot(point, plane.Normal) - plane.Dist;

            if (dist > TestEpsilon)
            {
                node = n.Children[0];
            }
            else if (dist < -TestEpsilon)
            {
                node = n.Children[1];
            }
            else
            {
                int front = PointInLeaf(n.Children[0], point);
                if (_leaves[front].Cluster != -1)
                {
                    return front;
                }

                node = n.Children[1];
            }
        }

        return -1 - node;
    }

    /// <summary>
    /// <c>PointLeafnum</c>: the descent that does not
    /// straddle.
    /// </summary>
    /// <param name="point">A world point.</param>
    /// <returns>The leaf index.</returns>
    /// <remarks>
    /// Uses <see cref="DPlane.Type"/> to read one component instead of taking
    /// a dot product when the plane is axial, which is the only reason the two
    /// descents can disagree by more than the epsilon: on an axial plane
    /// <c>point[type] - dist</c> and <c>Dot(normal, point) - dist</c> differ
    /// whenever the normal's off-axis components are not exactly zero.
    /// </remarks>
    public int LeafFromPoint(Vec3 point)
    {
        int node = 0;
        while (node >= 0)
        {
            if (node >= _nodes.Length)
            {
                return 0;
            }

            ref readonly DNode n = ref _nodes[node];
            ref readonly DPlane plane = ref _planes[n.PlaneNum];

            float dist = plane.Type < 3
                ? Component(point, plane.Type) - plane.Dist
                : Vec3.Dot(plane.Normal, point) - plane.Dist;

            node = dist < 0.0f ? n.Children[1] : n.Children[0];
        }

        return -1 - node;
    }

    private static float Component(Vec3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };
}
