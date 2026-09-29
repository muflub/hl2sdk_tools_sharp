//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The door-graph replay, as reusable assertions: the transitive closure of
/// "own vvis row, shifted into the global cluster space, plus the door edges
/// of the joint graph", computed here from the public primitives — each
/// room's own compile, the kit's seal boxes against the compile's open leaf
/// boxes, the test's own Warshall — shares no code with the linker. A level
/// linked with door visibility off must equal it; one linked with it on (the
/// default) must lie within it, keep its rooms' own rows, and keep every
/// sight line of the same level compiled whole.
/// </summary>
/// <remarks>
/// <see cref="LevelLinkerTests"/> carries its own private copy of this replay
/// (its lane owns that file); the 3x3 sample's facts, through the API and
/// through the CLI verbs, call this one rather than re-deriving the gate a
/// third way. The two copies are deliberately kept structurally identical —
/// same shapes, same epsilon, same closure, and the same world-space step
/// from a socket to its neighbour: a joint is followed through the placement's
/// turn (<see cref="RoomTransform.WorldNormal"/>), so a turned room's
/// room-local east socket finds the neighbour its turn faces.
/// </remarks>
internal static class DoorGraphFacts
{
    /// <summary>
    /// Asserts the linked level's whole visibility story for a level linked
    /// with the door graph's closure (<see cref="LevelLinkOptions.DoorVisibility"/>
    /// off): the cluster space is every room's own clusters in layout order;
    /// every row equals the door graph replay; and the delivered BSP's
    /// visibility lump decompresses back to exactly those rows.
    /// </summary>
    public static void AssertDoorGraph(LinkedLevel link, LevelLayout layout, RoomLibrary library)
    {
        // The cluster space is every room's own clusters, in layout order.
        int expectedClusters = layout.Rooms.Sum(i => library.Get(i.Placement.Room).ClusterCount);
        Assert.Equal(expectedClusters, link.Vis.ClusterCount);

        bool[][] expected = ExpectedRows(layout, library);
        for (int from = 0; from < link.Vis.ClusterCount; from++)
        {
            for (int to = 0; to < link.Vis.ClusterCount; to++)
            {
                Assert.True(
                    expected[from][to] == link.Vis.CanSee(from, to),
                    $"cluster {from} seeing {to}: the door-graph replay says {expected[from][to]}, "
                    + $"the linked vis says {link.Vis.CanSee(from, to)}");
            }
        }

        AssertLump(link);
    }

    /// <summary>
    /// Asserts what a level linked with door visibility (the default) must
    /// hold whatever the doorways let through: the cluster space is every
    /// room's own clusters in layout order; every row is within the door
    /// graph replay (the door flow only ever removes pairs from the closure);
    /// every room's own vvis rows are kept; the rows are symmetric and every
    /// cluster sees itself; and the lump decompresses back to the rows.
    /// </summary>
    /// <returns>The visible pairs, and the door graph's, for the caller's report.</returns>
    public static (int Visible, int DoorGraph) AssertWithinDoorGraph(LinkedLevel link, LevelLayout layout, RoomLibrary library)
    {
        int expectedClusters = layout.Rooms.Sum(i => library.Get(i.Placement.Room).ClusterCount);
        Assert.Equal(expectedClusters, link.Vis.ClusterCount);

        bool[][] closure = ExpectedRows(layout, library);
        int visible = 0, graph = 0;
        for (int from = 0; from < link.Vis.ClusterCount; from++)
        {
            Assert.True(link.Vis.CanSee(from, from), $"cluster {from} does not see itself");
            for (int to = 0; to < link.Vis.ClusterCount; to++)
            {
                bool sees = link.Vis.CanSee(from, to);
                visible += sees ? 1 : 0;
                graph += closure[from][to] ? 1 : 0;
                Assert.True(!sees || closure[from][to], $"cluster {from} sees {to}, which the door graph's closure does not");
                Assert.True(sees == link.Vis.CanSee(to, from), $"cluster {from} seeing {to} is not symmetric");
            }
        }

        // Each room's own vvis survives into its rows.
        int clusterBase = 0;
        foreach (RoomInstance instance in layout.Rooms)
        {
            RoomObject room = library.Get(instance.Placement.Room);
            for (int c = 0; c < room.ClusterCount; c++)
            {
                for (int d = 0; d < room.ClusterCount; d++)
                {
                    Assert.True(
                        !room.Vis.CanSee(c, d) || link.Vis.CanSee(clusterBase + c, clusterBase + d),
                        $"room {room.Definition.Name}'s own cluster {c} sees {d}; linked, it does not");
                }
            }

            clusterBase += room.ClusterCount;
        }

        AssertLump(link);
        return (visible, graph);
    }

    /// <summary>
    /// What <see cref="AssertKeepsEverySightLine"/> counted over its sample
    /// points' pairs: the sight lines, and the pairs each map's PVS keeps.
    /// </summary>
    internal readonly record struct SightLineCounts(int Samples, int SightLines, int LinkedPairs, int MonolithicPairs);

    /// <summary>
    /// Asserts that a linked level keeps every sight line of its monolithic
    /// twin (the flattened level compiled whole), and that the monolithic
    /// map's own vvis keeps each of them too.
    /// </summary>
    /// <remarks>
    /// The sample points are the centre of each open monolithic leaf and
    /// eight points two thirds of the way to its corners, kept where the
    /// monolithic walk agrees the point is in that leaf and the linked map
    /// puts it in a cluster. A pair is a sight line when the segment between
    /// them passes only through leaves that belong to a cluster
    /// (<see cref="LevelProbe.SightLine"/>): what vvis treats as open. vvis
    /// keeping every such pair shows the sampled lines are ones the flattened
    /// level's own PVS must keep, and the linked PVS keeping them is the
    /// conservative claim: the link culls nothing the flattened level shows.
    /// </remarks>
    public static SightLineCounts AssertKeepsEverySightLine(
        string name, LinkedLevel linked, LevelProbe linkedProbe, LevelProbe monolithicProbe, VisResult monolithicVis)
    {
        List<(Vec3 Point, int Monolithic, int Linked)> samples = [];
        IReadOnlyList<DLeaf> leafs = monolithicProbe.Leafs;
        for (int l = 0; l < leafs.Count; l++)
        {
            DLeaf leaf = leafs[l];
            if (leaf.Cluster < 0 || (leaf.Contents & (int)BrushContents.Solid) != 0)
            {
                continue;
            }

            Vec3 lo = new(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]);
            Vec3 hi = new(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]);
            Vec3 mid = (lo + hi) * 0.5f;
            for (int b = -1; b < 8; b++)
            {
                Vec3 corner = b < 0
                    ? mid
                    : new Vec3((b & 1) == 0 ? lo.X : hi.X, (b & 2) == 0 ? lo.Y : hi.Y, (b & 4) == 0 ? lo.Z : hi.Z);
                Vec3 sample = mid + ((corner - mid) * (2f / 3f));
                if (monolithicProbe.Leaf(sample) != l)
                {
                    continue;
                }

                int cluster = linkedProbe.Leafs[linkedProbe.Leaf(sample)].Cluster;
                if (cluster >= 0)
                {
                    samples.Add((sample, leaf.Cluster, cluster));
                }
            }
        }

        Assert.NotEmpty(samples);
        int sightLines = 0, linkedPairs = 0, monolithicPairs = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            (Vec3 p, int pm, int pl) = samples[i];
            for (int j = 0; j < samples.Count; j++)
            {
                (Vec3 q, int qm, int ql) = samples[j];
                bool linkedSees = linked.Vis.CanSee(pl, ql);
                linkedPairs += linkedSees ? 1 : 0;
                monolithicPairs += monolithicVis.CanSee(pm, qm) ? 1 : 0;
                if (j <= i || !monolithicProbe.SightLine(p, q))
                {
                    continue;
                }

                sightLines++;
                Assert.True(
                    monolithicVis.CanSee(pm, qm) && monolithicVis.CanSee(qm, pm),
                    $"{name}: {p} and {q} see each other, and the monolithic map's vvis does not keep clusters {pm} and {qm}");
                Assert.True(
                    linkedSees && linked.Vis.CanSee(ql, pl),
                    $"{name}: {p} and {q} see each other in the monolithic map, and the linked map's clusters {pl} and {ql} do not");
            }
        }

        Assert.True(sightLines > 0, $"{name}: no sample pair is a sight line");
        return new SightLineCounts(samples.Count, sightLines, linkedPairs, monolithicPairs);
    }

    /// <summary>
    /// The visibility lump is not just the VisResult's mirror: it is the
    /// file's row set, RLE-packed with the header vvis writes, and each of
    /// its PVS and PAS rows must decompress back to exactly the result's.
    /// </summary>
    private static void AssertLump(LinkedLevel link)
    {
        byte[] lump = [.. link.Bsp[BspLump.Visibility].Data.Span];
        Assert.Equal(link.Vis.ClusterCount, BitConverter.ToInt32(lump, 0));
        Assert.Equal(lump.Length, link.Vis.VisDataSize);
        int rowBytes = link.Vis.RowBytes;
        for (int cluster = 0; cluster < link.Vis.ClusterCount; cluster++)
        {
            int offset = BitConverter.ToInt32(lump, 4 + cluster * 8);
            Assert.True(offset > 0 && offset < lump.Length, $"row {cluster} offset {offset} is outside the lump");
            byte[] row = new byte[rowBytes];
            VisRunLength.Decompress(lump.AsSpan(offset, lump.Length - offset), row);
            Assert.True(row.SequenceEqual(link.Vis.Pvs(cluster)), $"row {cluster} does not survive the lump");

            int pasOffset = BitConverter.ToInt32(lump, 8 + cluster * 8);
            Assert.True(pasOffset > 0 && pasOffset < lump.Length, $"PAS row {cluster} offset {pasOffset} is outside the lump");
            VisRunLength.Decompress(lump.AsSpan(pasOffset, lump.Length - pasOffset), row);
            Assert.True(row.SequenceEqual(link.Vis.Pas(cluster)), $"PAS row {cluster} does not survive the lump");
        }
    }

    /// <summary>
    /// The door graph computed the other way: own rows from each room's own
    /// vvis result shifted by the layout-order cluster bases, door edges from
    /// the kit's seal boxes against the compile's open leaf boxes, transitive
    /// closure by the test's own Warshall. Shares no code with the linker.
    /// </summary>
    private static bool[][] ExpectedRows(LevelLayout layout, RoomLibrary library)
    {
        RoomObject[] rooms = [.. layout.Rooms.Select(i => library.Get(i.Placement.Room))];
        int[] baseOf = new int[rooms.Length];
        int total = 0;
        for (int i = 0; i < rooms.Length; i++)
        {
            baseOf[i] = total;
            total += rooms[i].ClusterCount;
        }

        bool[][] seen = [.. Enumerable.Range(0, total).Select(_ => new bool[total])];

        for (int i = 0; i < rooms.Length; i++)
        {
            for (int c = 0; c < rooms[i].ClusterCount; c++)
            {
                for (int d = 0; d < rooms[i].ClusterCount; d++)
                {
                    seen[baseOf[i] + c][baseOf[i] + d] = rooms[i].Vis.CanSee(c, d);
                }

                seen[baseOf[i] + c][baseOf[i] + c] = true;
            }
        }

        for (int i = 0; i < layout.Rooms.Count; i++)
        {
            RoomInstance instance = layout.Rooms[i];
            RoomDefinition definition = rooms[i].Definition;
            foreach ((string socketName, string neighbourSocketName) in instance.Joints)
            {
                RoomSocket socket = definition.Sockets.First(s => s.Name == socketName);
                // The socket's WORLD face: a turned room's east socket may face north.
                (int axis, int sign) = new RoomTransform(instance.Placement, layout.CellSize).WorldNormal(socket.Facing);
                (int dx, int dy) = axis == 0 ? (sign, 0) : (0, sign);
                int j = -1;
                for (int k = 0; k < layout.Rooms.Count; k++)
                {
                    if (layout.Rooms[k].Placement.CellX == instance.Placement.CellX + dx
                        && layout.Rooms[k].Placement.CellY == instance.Placement.CellY + dy)
                    {
                        j = k;
                    }
                }

                Assert.True(j >= 0, $"the joint at {instance.Placement} has no neighbour");

                RoomSocket neighbourSocket = rooms[j].Definition.Sockets.First(s => s.Name == neighbourSocketName);
                foreach (int x in Facing(rooms[i], definition, socket))
                {
                    foreach (int y in Facing(rooms[j], rooms[j].Definition, neighbourSocket))
                    {
                        seen[baseOf[i] + x][baseOf[j] + y] = true;
                        seen[baseOf[j] + y][baseOf[i] + x] = true;
                    }
                }
            }
        }

        for (int k = 0; k < total; k++)
        {
            for (int i = 0; i < total; i++)
            {
                if (seen[i][k])
                {
                    for (int j = 0; j < total; j++)
                    {
                        seen[i][j] |= seen[k][j];
                    }
                }
            }
        }

        return seen;
    }

    /// <summary>The open clusters whose leaf boxes overlap a socket's kit plug box.</summary>
    private static int[] Facing(RoomObject room, RoomDefinition definition, RoomSocket socket)
    {
        Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);
        List<int> clusters = [];
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(room.Bsp[BspLump.Leafs]))
        {
            if ((leaf.Contents & (int)BrushContents.Solid) != 0 || leaf.Cluster < 0)
            {
                continue;
            }

            Box box = new(
                new Vec3(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]),
                new Vec3(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]));
            if (box.Overlaps(plug, LevelLinker.DoorOverlapEpsilon))
            {
                clusters.Add(leaf.Cluster);
            }
        }

        return [.. clusters.Order()];
    }
}
