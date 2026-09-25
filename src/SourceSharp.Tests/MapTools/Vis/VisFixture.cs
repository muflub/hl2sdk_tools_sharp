using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// Maps and portal files small enough to reason about by hand, built in memory.
/// </summary>
/// <remarks>
/// <para>
/// vvis reads very little of a BSP: it needs the node and face lumps to be
/// non-empty (the "Empty map" check), the leaf lump for clusters, contents and
/// bounds, the entity lump for <c>env_fog_controller</c>, and the face/edge/
/// vertex lumps only when a leaf touches water. So a fixture map is a handful
/// of leaves and nothing else, and the unit tier never needs a compiler or a
/// disk.
/// </para>
/// <para>
/// Everything geometric is stated in the PORTAL file, which is where vvis's
/// geometry actually comes from.
/// </para>
/// </remarks>
internal static class VisFixture
{
    /// <summary>
    /// A map with one leaf per cluster and nothing else worth reading.
    /// </summary>
    /// <param name="clusterCount">How many clusters, and so how many leaves.</param>
    /// <returns>The map.</returns>
    internal static BspData Map(int clusterCount)
    {
        BspData bsp = new();

        DLeaf[] leaves = new DLeaf[clusterCount];
        for (int i = 0; i < clusterCount; i++)
        {
            leaves[i] = new DLeaf
            {
                Contents = 0,
                Cluster = (short)i,

                // -1 is "not in water". Zero would name water volume 0 and send
                // the leaf-to-water pass off looking for faces that are not
                // there.
                LeafWaterDataId = -1,
            };
        }

        bsp.SetLump(BspLump.Leafs, MemoryMarshal.AsBytes<DLeaf>(leaves).ToArray(), version: 1);
        bsp.SetLump(BspLump.Nodes, MemoryMarshal.AsBytes<DNode>(new DNode[1]).ToArray());
        bsp.SetLump(BspLump.Faces, MemoryMarshal.AsBytes<DFace>(new DFace[1]).ToArray());

        return bsp;
    }

    /// <summary>
    /// A portal file naming its cluster count and its portals.
    /// </summary>
    /// <param name="clusterCount">The header's cluster count.</param>
    /// <param name="portals">One entry per FILE portal.</param>
    /// <returns>The portal file.</returns>
    internal static PortalFile Portals(int clusterCount, params FilePortal[] portals)
    {
        PortalFile file = new() { ClusterCount = clusterCount };
        foreach (FilePortal portal in portals)
        {
            file.Portals.Add(portal);
        }

        return file;
    }

    /// <summary>
    /// A rectangular portal in the plane x = <paramref name="x"/>, wound so
    /// that the FORWARD memory portal's normal points along +x.
    /// </summary>
    /// <param name="a">The cluster on the -x side.</param>
    /// <param name="b">The cluster on the +x side.</param>
    /// <param name="x">Where the plane is.</param>
    /// <param name="yMin">The window's near edge.</param>
    /// <param name="yMax">The window's far edge.</param>
    /// <returns>The file portal.</returns>
    /// <remarks>
    /// The winding order is load-bearing and not arbitrary:
    /// <c>PlaneFromWinding</c> takes <c>cross(p0 - p1, p2 - p1)</c>, and the
    /// forward portal then NEGATES that (<c>vvis.cpp:538</c>). Wound the other
    /// way round, every portal in the fixture would face backwards and the
    /// flood would see nothing -- which looks exactly like a broken vvis.
    /// </remarks>
    internal static FilePortal WindowAtX(int a, int b, float x, float yMin, float yMax) =>
        new(a, b,
        [
            new Vec3(x, yMin, 0f),
            new Vec3(x, yMax, 0f),
            new Vec3(x, yMax, 16f),
            new Vec3(x, yMin, 16f),
        ]);

    /// <summary>
    /// A rectangular portal in the plane y = <paramref name="y"/>, wound so
    /// that the FORWARD memory portal's normal points along +y.
    /// </summary>
    /// <param name="a">The cluster on the -y side.</param>
    /// <param name="b">The cluster on the +y side.</param>
    /// <param name="y">Where the plane is.</param>
    /// <param name="xMin">The window's near edge.</param>
    /// <param name="xMax">The window's far edge.</param>
    /// <returns>The file portal.</returns>
    /// <remarks>
    /// <see cref="WindowAtX"/> turned a quarter: <c>cross(p0 - p1, p2 - p1)</c>
    /// is <c>(w,0,0) x (0,0,16) = (0,-16w,0)</c>, which the forward portal
    /// negates to +y.
    /// </remarks>
    internal static FilePortal WindowAtY(int a, int b, float y, float xMin, float xMax) =>
        new(a, b,
        [
            new Vec3(xMax, y, 0f),
            new Vec3(xMin, y, 0f),
            new Vec3(xMin, y, 16f),
            new Vec3(xMax, y, 16f),
        ]);

    /// <summary>The leaf a point falls in, by walking the BSP tree.</summary>
    /// <param name="bsp">A map with node and plane lumps.</param>
    /// <param name="point">Where to look.</param>
    /// <returns>A leaf index.</returns>
    /// <remarks>
    /// <para>
    /// <c>PointLeafnum_r</c> (<c>utils/vrad/trace.cpp:435</c>, and the same
    /// walk as the engine's <c>CM_PointLeafnum_r</c>). A negative child is a
    /// leaf, encoded as <c>-(leaf + 1)</c>.
    /// </para>
    /// <para>
    /// <b>The tie-break is load-bearing and was got wrong first.</b> The test is
    /// <c>dist &lt; 0</c> goes BACK and everything else goes FRONT, so a point
    /// exactly ON a splitting plane lands in the front child. Written as
    /// <c>dist &gt; 0 ? front : back</c> instead, every probe that sits on a
    /// cut lands in the other leaf -- and the catalogue's probes sit at
    /// <c>y = 0</c>, which is exactly where vbsp's 1024-unit block grid cuts
    /// (<c>vbsp.cpp:73</c>). That reported a real must-see pair as unreachable
    /// and looked like a vvis defect.
    /// </para>
    /// <para>
    /// The axial shortcut is reproduced too: for <c>type &lt; 3</c> the engine
    /// reads the component instead of forming a dot product, which is not the
    /// same arithmetic on a point sitting on the plane.
    /// </para>
    /// </remarks>
    internal static int LeafAt(BspData bsp, Vec3 point)
    {
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);

        int at = 0;
        while (at >= 0)
        {
            DNode node = nodes[at];
            DPlane plane = planes[node.PlaneNum];

            float d = plane.Type switch
            {
                0 => point.X - plane.Dist,
                1 => point.Y - plane.Dist,
                2 => point.Z - plane.Dist,
                _ => Vec3.Dot(plane.Normal, point) - plane.Dist,
            };

            at = d < 0f ? node.Children[1] : node.Children[0];
        }

        return -1 - at;
    }

    /// <summary>The vis cluster a point falls in, or -1 in solid.</summary>
    /// <param name="bsp">A compiled map.</param>
    /// <param name="point">Where to look.</param>
    /// <returns>A cluster index, or -1.</returns>
    internal static int ClusterAt(BspData bsp, Vec3 point)
    {
        int leaf = LeafAt(bsp, point);
        ReadOnlySpan<DLeaf> leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        return leaves[leaf].Cluster;
    }

    /// <summary>
    /// How far off a splitting plane a point may be and still count as being on
    /// both sides of it.
    /// </summary>
    /// <remarks>
    /// An eighth of a unit: far below vbsp's own grid, so this can only ever
    /// catch a point that is genuinely ON a cut, never one that is merely near
    /// one.
    /// </remarks>
    internal const float BoundaryEpsilon = 0.125f;

    /// <summary>
    /// Every vis cluster a point touches, which is more than one when it sits
    /// on a splitting plane.
    /// </summary>
    /// <param name="bsp">A compiled map.</param>
    /// <param name="point">Where to look.</param>
    /// <returns>The clusters, ascending, with solid leaves left out.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why a SET and not the single leaf the engine would pick.</b> The
    /// catalogue's probes stand in the middle of a room, at <c>y = 0</c> -- and
    /// <c>y = 0</c> is exactly where vbsp's 1024-unit block grid cuts
    /// (<c>vbsp.cpp:73</c>), so every one of them is sitting on a cluster
    /// boundary. Which cluster the engine's walk hands back is then decided by
    /// a tie-break, and a room whose doorway is offset to one side has its two
    /// halves seeing genuinely different things.
    /// </para>
    /// <para>
    /// Measured, not argued: with the engine's tie-break (on the plane means
    /// the FRONT child) six catalogue entries report a must-see pair as
    /// unreachable, and with the other one a seventh does -- different six. The
    /// PVS is bit-identical to stock's on every one of those maps, so it is the
    /// probe placement that is ambiguous, not the visibility.
    /// </para>
    /// <para>
    /// A declaration is about a PLACE. So "the cluster containing A" is read as
    /// every cluster A is in, must-see is satisfied by any pair of them, and
    /// must-not-see requires ALL pairs to be blocked -- which keeps the half
    /// that can actually fail strict, and makes it stricter than a single-leaf
    /// reading rather than weaker.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<int> ClustersAt(BspData bsp, Vec3 point)
    {
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        ReadOnlySpan<DLeaf> leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);

        SortedSet<int> clusters = [];
        Stack<int> pending = new();
        pending.Push(0);

        while (pending.Count > 0)
        {
            int at = pending.Pop();

            if (at < 0)
            {
                short cluster = leaves[-1 - at].Cluster;
                if (cluster >= 0)
                {
                    clusters.Add(cluster);
                }

                continue;
            }

            DNode node = nodes[at];
            DPlane plane = planes[node.PlaneNum];
            float d = plane.Type switch
            {
                0 => point.X - plane.Dist,
                1 => point.Y - plane.Dist,
                2 => point.Z - plane.Dist,
                _ => Vec3.Dot(plane.Normal, point) - plane.Dist,
            };

            if (d > -BoundaryEpsilon)
            {
                pending.Push(node.Children[0]);
            }

            if (d < BoundaryEpsilon)
            {
                pending.Push(node.Children[1]);
            }
        }

        return [.. clusters];
    }
}
