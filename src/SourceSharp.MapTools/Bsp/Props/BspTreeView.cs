using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Props;

/// <summary>
/// The written world tree the prop emitters walk: <c>dnodes</c>,
/// <c>dplanes</c> and <c>dleafs</c> as <c>WriteBSP</c> left them.
/// </summary>
/// <remarks>
/// Both emitters run in <c>EndBSPFile</c> after the tree is written, and read
/// it, not the in-memory <c>node_t</c> tree. Under stage isolation the lumps
/// are stock's own; in the managed driver they are whatever 3e wrote.
/// </remarks>
/// <param name="Nodes">LUMP_NODES.</param>
/// <param name="Planes">LUMP_PLANES.</param>
/// <param name="LeafContents">Each leaf's <c>contents</c>, from LUMP_LEAFS.</param>
public sealed record BspTreeView(DNode[] Nodes, DPlane[] Planes, int[] LeafContents)
{
    /// <summary>LUMP_LEAFS whole, when the tree came from version-1 leaves; the cooked leaf walk reads it.</summary>
    public DLeaf[]? Leafs { get; init; }

    /// <summary><c>CONTENTS_SOLID</c>.</summary>
    public const int ContentsSolid = 0x1;

    /// <summary>Reads the three lumps of a BSP.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <returns>The view.</returns>
    public static BspTreeView FromBsp(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        int[] contents;
        DLeaf[]? full = null;
        if (bsp[BspLump.Leafs].Version == 0)
        {
            DLeafVersion0[] leafs = BspStructView.As<DLeafVersion0>(bsp[BspLump.Leafs]).ToArray();
            contents = [.. leafs.Select(l => l.Contents)];
        }
        else
        {
            full = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
            contents = [.. full.Select(l => l.Contents)];
        }

        return new BspTreeView(
            BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray(),
            BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray(),
            contents)
        {
            Leafs = full,
        };
    }

    /// <summary>
    /// <c>ComputeDetailLeaf</c> (<c>detailobjects.cpp:414-429</c>): the leaf a
    /// point falls in, a point exactly on a plane going FRONT.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns>The leaf index.</returns>
    public int LeafOf(Vec3 point)
    {
        int node = 0;
        while (node >= 0)
        {
            DNode n = Nodes[node];
            DPlane plane = Planes[n.PlaneNum];

            // DotProduct(pt, normal): the point first.
            node = Vec3.Dot(point, plane.Normal) < plane.Dist ? n.Children[1] : n.Children[0];
        }

        return -node - 1;
    }
}
