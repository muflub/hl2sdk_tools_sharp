using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// Which displacements each BSP leaf touches, and the per-leaf ray clip that
/// uses it: <c>CVRadDispMgr</c>'s <c>m_pBSPTreeData</c>
/// (<c>vraddisps.cpp:288-312</c>, <c>public/bsptreedata.cpp</c>) and
/// <c>ClipRayToDispInLeaf</c> (<c>vraddisps.cpp:583-612</c>).
/// </summary>
/// <remarks>
/// <para>
/// A displacement is inserted into every leaf its collision bounds
/// (<see cref="DispCollisionTree.Mins"/>/<see cref="DispCollisionTree.Maxs"/>)
/// reach, walking the map's own BSP with <c>EnumerateLeavesInBox_R</c>
/// (<c>bsplib.cpp:3461</c>, <c>TEST_EPSILON</c> 0.03125). Each leaf's list is
/// built by PREPENDING (<c>AddHandleToLeaf</c>, <c>bsptreedata.cpp:204</c>), so
/// it is walked from the last-inserted displacement to the first; that order
/// decides which of two equally distant hits wins.
/// </para>
/// <para>
/// This is the hook leaf ambient (4g, <c>CastRayInLeaf</c>) and detail-prop
/// lighting (<c>CLightSurface::EnumerateLeaf</c>) need: stock's
/// <c>s_DispTested[iThread]</c> becomes a <see cref="DispRayTestState"/> owned
/// by the work item. Immutable after construction.
/// </para>
/// </remarks>
public sealed class DispLeafIndex
{
    /// <summary><c>TEST_EPSILON</c>, <c>bsplib.cpp:3403</c>.</summary>
    public const double TestEpsilon = 0.03125;

    private readonly int[][] _leafDisps;
    private readonly IReadOnlyList<VradDispSurface?> _surfaces;

    private DispLeafIndex(int[][] leafDisps, IReadOnlyList<VradDispSurface?> surfaces)
    {
        _leafDisps = leafDisps;
        _surfaces = surfaces;
    }

    /// <summary>The displacements whose bounds reach a leaf, in stock's walk order.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <returns>Displacement indices.</returns>
    public ReadOnlySpan<int> InLeaf(int leaf) => _leafDisps[leaf];

    /// <summary>How many displacements there are (the size a <see cref="DispRayTestState"/> needs).</summary>
    public int DisplacementCount => _surfaces.Count;

    /// <summary>Builds the index over a map's tree.</summary>
    /// <param name="geometry">The map (nodes, planes, leaves).</param>
    /// <param name="surfaces">The displacements, in LUMP_DISPINFO order.</param>
    /// <returns>The index.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static DispLeafIndex Build(LightGeometry geometry, IReadOnlyList<VradDispSurface?> surfaces)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(surfaces);

        List<int>[] lists = new List<int>[geometry.Leaves.Length];
        for (int d = 0; d < surfaces.Count; d++)
        {
            VradDispSurface? s = surfaces[d];
            if (s is null || geometry.Nodes.Length == 0)
            {
                continue;
            }

            EnumerateLeavesInBox(geometry, 0, s.Tree.Mins, s.Tree.Maxs, leaf => (lists[leaf] ??= []).Add(d));
        }

        int[][] leafDisps = new int[lists.Length][];
        for (int i = 0; i < lists.Length; i++)
        {
            if (lists[i] is null)
            {
                leafDisps[i] = [];
                continue;
            }

            // Prepended in stock: the newest first.
            int[] a = [.. lists[i]];
            Array.Reverse(a);
            leafDisps[i] = a;
        }

        return new DispLeafIndex(leafDisps, surfaces);
    }

    /// <summary>
    /// <c>EnumerateLeavesInBox_R</c> (<c>bsplib.cpp:3461</c>): every leaf a box
    /// reaches, front child first.
    /// </summary>
    /// <param name="geometry">The map.</param>
    /// <param name="node">The node to start at (0 for the head).</param>
    /// <param name="mins">Box mins.</param>
    /// <param name="maxs">Box maxs.</param>
    /// <param name="visit">Called for each leaf.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void EnumerateLeavesInBox(LightGeometry geometry, int node, Vec3 mins, Vec3 maxs, Action<int> visit)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(visit);

        while (node >= 0)
        {
            ref readonly DNode n = ref geometry.Nodes[node];
            ref readonly DPlane plane = ref geometry.Planes[n.PlaneNum];
            Vec3 normal = plane.Normal;
            Vec3 cornerMin = new(
                normal.X >= 0 ? mins.X : maxs.X,
                normal.Y >= 0 ? mins.Y : maxs.Y,
                normal.Z >= 0 ? mins.Z : maxs.Z);
            Vec3 cornerMax = new(
                normal.X >= 0 ? maxs.X : mins.X,
                normal.Y >= 0 ? maxs.Y : mins.Y,
                normal.Z >= 0 ? maxs.Z : mins.Z);

            if ((Vec3.Dot(normal, cornerMax) - plane.Dist) <= -TestEpsilon)
            {
                node = n.Children[1];
            }
            else if ((Vec3.Dot(normal, cornerMin) - plane.Dist) >= TestEpsilon)
            {
                node = n.Children[0];
            }
            else
            {
                EnumerateLeavesInBox(geometry, n.Children[0], mins, maxs, visit);
                node = n.Children[1];
            }
        }

        visit(-node - 1);
    }

    /// <summary>
    /// <c>ClipRayToDispInLeaf</c> with a face and luxel coordinate
    /// (<c>vraddisps.cpp:583</c>, <c>DispRayDistance_EnumerateElement</c> :702):
    /// the nearest displacement hit in a leaf, as a fraction of the ray.
    /// </summary>
    /// <param name="state">The work item's tested marks; call <see cref="DispRayTestState.StartRayTest"/> first.</param>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray delta.</param>
    /// <param name="leaf">The leaf.</param>
    /// <param name="stockNormalise">Whether the hit normal uses stock's estimate.</param>
    /// <returns>
    /// The distance (1 when nothing is nearer), the hit face (-1 for none), its
    /// luxel coordinate, and the hit triangle's normal.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <remarks>
    /// A displacement already tested since the last
    /// <see cref="DispRayTestState.StartRayTest"/> is skipped, so a ray walked
    /// through several leaves tests each displacement once. The hit face is
    /// the displacement's parent face; the luxel coordinate is the barycentric
    /// blend of the three vertices' luxel coordinates; the normal is
    /// <c>(v1 - v0) x (v2 - v0)</c> in the hit's vertex order, normalised.
    /// </remarks>
    public (float Dist, int Face, DispUv LuxelCoord, Vec3 Normal) ClipRayToDispInLeaf(
        DispRayTestState state, Vec3 start, Vec3 delta, int leaf, bool stockNormalise)
    {
        ArgumentNullException.ThrowIfNull(state);

        float best = 1.0f;
        int face = -1;
        DispUv luxel = default;
        Vec3 normal = default;
        foreach (int d in _leafDisps[leaf])
        {
            if (!state.MarkTested(d))
            {
                continue;
            }

            VradDispSurface s = _surfaces[d]!;
            DispRayHit hit = new(float.MaxValue, -1.0f, -1.0f, -1, -1, -1);
            if (!DispCollision.Ray(s, start, delta, ref hit) || !(hit.Dist < best))
            {
                continue;
            }

            best = hit.Dist;
            face = s.ParentFace;
            ReadOnlySpan<DispUv> lc = s.LuxelCoords;
            luxel = DispCollision.PointFromBarycentric(lc[hit.Vert0], lc[hit.Vert1], lc[hit.Vert2], hit.U, hit.V);
            Vec3 v0 = s.Verts[hit.Vert0];
            Vec3 e0 = s.Verts[hit.Vert1] - v0;
            Vec3 e1 = s.Verts[hit.Vert2] - v0;
            normal = VradDispSurface.Normalise(Vec3.Cross(e0, e1), stockNormalise);
        }

        return (best, face, luxel, normal);
    }
}

/// <summary>
/// <c>DispTested_t</c> (<c>vrad.h</c>): per work item, which displacements
/// the current ray has already been tested against.
/// </summary>
/// <remarks>
/// Stock keeps one per thread (<c>s_DispTested[iThread]</c>,
/// <c>trace.cpp:84</c>); owning one per work item is what makes the answer
/// independent of the thread count. A counter bumps per ray, so the marks never
/// need clearing (it wraps after 2^31 rays, at which point it clears once).
/// </remarks>
public sealed class DispRayTestState
{
    private readonly int[] _tested;
    private int _enum;

    /// <summary>Creates the marks for a map's displacements.</summary>
    /// <param name="displacementCount">How many there are.</param>
    public DispRayTestState(int displacementCount) => _tested = new int[Math.Max(displacementCount, 0)];

    /// <summary><c>StartRayTest</c> (<c>vraddisps.cpp:539</c>): begin a new ray.</summary>
    public void StartRayTest()
    {
        if (_enum == int.MaxValue)
        {
            Array.Clear(_tested);
            _enum = 0;
        }

        _enum++;
    }

    /// <summary>Marks a displacement tested for the current ray.</summary>
    /// <param name="disp">The displacement.</param>
    /// <returns>False when it was already tested for this ray.</returns>
    public bool MarkTested(int disp)
    {
        if (_tested[disp] == _enum)
        {
            return false;
        }

        _tested[disp] = _enum;
        return true;
    }
}
