//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Portals;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>
/// A BSP tree rebuilt out of a stock <c>.bsp</c>, so that the portal stages can
/// be run against real vbsp geometry before there is a managed tree builder.
/// </summary>
/// <remarks>
/// <para>
/// This is a genuinely good oracle and it is worth being precise about why.
/// <c>WritePortalFile</c> is the LAST thing <c>ProcessWorldModel</c> does, after
/// <c>WriteBSP</c>, and the first thing it does is throw every portal away and
/// portalise again. The tree it portalises is therefore
/// the tree that was just written to the file — the same nodes, the same
/// planes, the same leaf contents. Rebuilding that tree from the lumps and
/// running the managed stage over it is not an approximation of what stock did;
/// it is the same input.
/// </para>
/// <para>
/// Three things are reconstructed rather than read, and each is checked rather
/// than assumed:
/// </para>
/// <list type="bullet">
/// <item>
/// The plane table. <c>LUMP_PLANES</c> is <c>mapplanes</c> verbatim and
/// <c>CreateNewFloatPlane</c> always appends a front/back pair with the axial
/// one facing positive first, so feeding the even entries back through
/// <see cref="PlaneTable.Create"/> rebuilds the table exactly.
/// <see cref="FromBsp"/> asserts every index agrees before returning.
/// </item>
/// <item>
/// The tree bounds. <c>dnodes[0]</c> carries the head node's box, which is the
/// tree's own bounds padded by <c>SIDESPACE</c>, so the bounds are that box
/// pulled back in by 8. <c>EmitDrawNode_r</c> copies them float-to-short, which
/// is exact for the integer bounds a block grid produces.
/// </item>
/// <item>
/// The outside leaf, which is <c>tree_t</c>'s own field and never appears in
/// the file. It is a fresh empty leaf, which is what
/// <c>MakeHeadnodePortals</c> resets it to anyway.
/// </item>
/// </list>
/// <para>
/// What is NOT reconstructed is the leaf brush lists. Nothing the <c>.prt</c>,
/// the leak line or the area flood does reads them — only
/// <c>FindPortalSide</c> and <c>AreaportalBrushForNode</c> do, and neither runs
/// in these gates.
/// </para>
/// </remarks>
internal sealed class StockBspTree
{
    private StockBspTree(
        BspTree tree,
        PlaneTable planes,
        IReadOnlyList<BspNode> leaves,
        IReadOnlyList<MapEntity> entities)
    {
        Tree = tree;
        Planes = planes;
        Leaves = leaves;
        Entities = entities;
    }

    /// <summary>The rebuilt tree.</summary>
    internal BspTree Tree { get; }

    /// <summary>The plane table, index for index with <c>LUMP_PLANES</c>.</summary>
    internal PlaneTable Planes { get; }

    /// <summary>The leaves, in <c>LUMP_LEAFS</c> order.</summary>
    internal IReadOnlyList<BspNode> Leaves { get; }

    /// <summary>The entities, parsed out of <c>LUMP_ENTITIES</c>.</summary>
    internal IReadOnlyList<MapEntity> Entities { get; }

    /// <summary>Rebuilds a tree from a loaded BSP.</summary>
    /// <param name="bsp">The stock BSP.</param>
    /// <returns>The tree and everything the portal stages need alongside it.</returns>
    internal static StockBspTree FromBsp(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        PlaneTable planes = RebuildPlanes(bsp);

        ReadOnlySpan<DLeaf> dleaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        ReadOnlySpan<DNode> dnodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);

        Assert.NotEmpty(dnodes.ToArray());
        Assert.NotEmpty(dleaves.ToArray());

        BspNode[] leaves = new BspNode[dleaves.Length];
        int nextId = 0;

        for (int i = 0; i < dleaves.Length; i++)
        {
            leaves[i] = new BspNode(nextId++)
            {
                Contents = dleaves[i].Contents,
            };
        }

        BspNode[] nodes = new BspNode[dnodes.Length];

        for (int i = 0; i < dnodes.Length; i++)
        {
            nodes[i] = new BspNode(nextId++);
        }

        for (int i = 0; i < dnodes.Length; i++)
        {
            nodes[i].SplitOn(
                dnodes[i].PlaneNum,
                ChildOf(dnodes[i].Children[0], nodes, leaves),
                ChildOf(dnodes[i].Children[1], nodes, leaves));
        }

        // dnodes[0] carries the padded head-node box; the tree's own bounds are
        // that box pulled back in by SIDESPACE on every side.
        Vec3 boxMins = new(dnodes[0].Mins[0], dnodes[0].Mins[1], dnodes[0].Mins[2]);
        Vec3 boxMaxs = new(dnodes[0].Maxs[0], dnodes[0].Maxs[1], dnodes[0].Maxs[2]);

        Vec3 mins = new(
            boxMins.X + TreePortals.SideSpace,
            boxMins.Y + TreePortals.SideSpace,
            boxMins.Z + TreePortals.SideSpace);
        Vec3 maxs = new(
            boxMaxs.X - TreePortals.SideSpace,
            boxMaxs.Y - TreePortals.SideSpace,
            boxMaxs.Z - TreePortals.SideSpace);

        BspNode outside = new(nextId);
        BspTree tree = new(nodes[0], outside, mins, maxs);

        CutBlockParents(nodes[0], planes, mins, maxs);

        List<MapEntity> entities = ReadEntities(bsp);
        RestoreAreaportalBrushes(leaves, entities);

        return new StockBspTree(tree, planes, leaves, entities);
    }

    /// <summary>
    /// Puts an areaportal brush back into every leaf that says it is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RemoveAreaPortalBrushes_R</c> strips these
    /// from the leaf lists right after <c>FloodAreas</c> and before the BSP is
    /// written — "we don't want them in the engine at runtime but we do want
    /// their flags in the leaves". So a compiled map carries
    /// <c>CONTENTS_AREAPORTAL</c> on the leaf and nothing that says WHICH
    /// areaportal, and <c>AreaportalBrushForNode</c> cannot be satisfied from
    /// the file alone.
    /// </para>
    /// <para>
    /// The catalogue's two areaportal entries each have exactly one
    /// <c>func_areaportal</c>, so the mapping is not ambiguous — and that is
    /// asserted rather than assumed, because a third entry with two of them
    /// would otherwise be quietly given the wrong answer.
    /// </para>
    /// </remarks>
    private static void RestoreAreaportalBrushes(BspNode[] leaves, List<MapEntity> entities)
    {
        bool anyAreaportalLeaf = false;

        foreach (BspNode leaf in leaves)
        {
            if ((leaf.Contents & PortalContents.AreaPortal) != 0)
            {
                anyAreaportalLeaf = true;
                break;
            }
        }

        if (!anyAreaportalLeaf)
        {
            return;
        }

        List<int> areaportals = [];

        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i].ValueForKey("classname") == "func_areaportal")
            {
                areaportals.Add(i);
            }
        }

        Assert.True(
            areaportals.Count == 1,
            $"this fixture can only place areaportal brushes when the map has exactly one "
            + $"func_areaportal; this one has {areaportals.Count}");

        int entityNumber = areaportals[0];
        entities[entityNumber].AreaPortalNumber = 1;

        foreach (BspNode leaf in leaves)
        {
            if ((leaf.Contents & PortalContents.AreaPortal) == 0)
            {
                continue;
            }

            leaf.AddLeafBrush(new MapBrush
            {
                Contents = PortalContents.AreaPortal,
                EntityNumber = entityNumber,
                Id = entityNumber,
            });
        }
    }

    /// <summary>
    /// Unlinks each block subtree's root from the block grid above it, because
    /// stock's tree is not linked there either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is load-bearing and it is not obvious.</b> The world tree is two
    /// things stapled together: <c>BlockTree</c> builds
    /// the 1024-unit grid, and <c>BrushBSP</c> builds one subtree per block.
    /// <c>BuildTree_r</c> sets <c>node-&gt;parent</c> inside a subtree;
    /// <c>BlockTree</c> never sets it at all, and <c>AllocNode</c> zeroes the
    /// struct. So every block subtree's root has a null parent even though the
    /// grid node above it points at it.
    /// </para>
    /// <para>
    /// <c>BaseWindingForNode</c> walks that chain, so at a block root it clips
    /// the plane's winding by NOTHING and hands <c>MakeNodePortal</c> a
    /// 65536-unit quad to cut down with the node's portals instead. The result
    /// is the same polygon either way — the portals bound the node completely —
    /// but the clip that first cuts a corner off decides which vertex the
    /// winding starts at, and that IS in the <c>.prt</c>. Reconstructing the
    /// parent links from the file, which is the natural thing to do, makes 7 of
    /// 17 catalogue maps come out with correct but rotated windings.
    /// </para>
    /// <para>
    /// The grid's shape is recomputed rather than guessed: the tree's bounds
    /// give the block range, and <c>BlockTree</c>'s own split rule (largest
    /// axis, midpoint <c>lo + (hi-lo)/2 + 1</c>) then says which node is which,
    /// checked against the plane the file actually has.
    /// </para>
    /// </remarks>
    private static void CutBlockParents(BspNode root, PlaneTable planes, Vec3 mins, Vec3 maxs)
    {
        const int blocksSize = 1024;
        // ProcessWorldModel calls BlockTree(xl-1, yl-1, xh+1, yh+1), and
        // the tree's own bounds are xl*1024.. (xh+1)*1024.
        int xl = (int)(mins.X / blocksSize) - 1;
        int yl = (int)(mins.Y / blocksSize) - 1;
        int xh = ((int)(maxs.X / blocksSize) - 1) + 1;
        int yh = ((int)(maxs.Y / blocksSize) - 1) + 1;

        CutBlockParents(root, planes, xl, yl, xh, yh);
    }

    private static void CutBlockParents(BspNode node, PlaneTable planes, int xl, int yl, int xh, int yh)
    {
        const int blocksSize = 1024;

        // EVERY node BlockTree makes has a null parent, separators included.
        // it never writes the field and AllocNode zeroes it. Only the block
        // subtrees underneath, which BuildTree_r built, have parent chains, and
        // those chains stop at their own root.
        node.Parent = null;

        if ((xl == xh && yl == yh) || node.PlaneNumber == IBspNode.LeafPlaneNumber)
        {
            // A whole block, or a grid node PruneNodes collapsed into one solid
            // leaf. Either way this is where the grid stops.
            return;
        }

        bool splitX = xh - xl > yh - yl;
        int mid = splitX ? xl + ((xh - xl) / 2) + 1 : yl + ((yh - yl) / 2) + 1;
        Vec3 expected = splitX ? new Vec3(1f, 0f, 0f) : new Vec3(0f, 1f, 0f);

        Plane actual = planes[node.PlaneNumber];

        Assert.True(
            actual.Normal == expected && actual.Dist == mid * blocksSize,
            $"block grid reconstruction diverged: expected {expected} at {mid * blocksSize}, "
            + $"the file has {actual.Normal} at {actual.Dist}");

        BspNode front = (BspNode)node.Front!;
        BspNode back = (BspNode)node.Back!;

        if (splitX)
        {
            CutBlockParents(front, planes, mid, yl, xh, yh);
            CutBlockParents(back, planes, xl, yl, mid - 1, yh);
        }
        else
        {
            CutBlockParents(front, planes, xl, mid, xh, yh);
            CutBlockParents(back, planes, xl, yl, xh, mid - 1);
        }
    }

    private static BspNode ChildOf(int child, BspNode[] nodes, BspNode[] leaves) =>
        child >= 0 ? nodes[child] : leaves[-1 - child];

    private static PlaneTable RebuildPlanes(BspData bsp)
    {
        ReadOnlySpan<DPlane> dplanes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        PlaneTable planes = new();

        for (int i = 0; i + 1 < dplanes.Length; i += 2)
        {
            planes.Create(dplanes[i].Normal, dplanes[i].Dist);
        }

        // The reconstruction is only sound if it came out identical. A plane
        // table that is off by one pair produces a.prt that looks plausible
        // and is wrong everywhere, so this is checked and not trusted.
        Assert.Equal(dplanes.Length, planes.Count);

        for (int i = 0; i < dplanes.Length; i++)
        {
            Assert.Equal(dplanes[i].Normal, planes[i].Normal);
            Assert.Equal(dplanes[i].Dist, planes[i].Dist);
        }

        return planes;
    }

    private static List<MapEntity> ReadEntities(BspData bsp)
    {
        List<MapEntity> entities = [];

        foreach (BspEntity source in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            MapEntity entity = new();

            foreach (BspKeyValue pair in source.Pairs)
            {
                entity.AddKeyValue(pair.Key, pair.Value);
            }

            entities.Add(entity);
        }

        return entities;
    }
}
