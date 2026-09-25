using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The door-graph replay, as a reusable assertion: the linked map's PVS must
/// equal the transitive closure of "own vvis row, shifted into the global
/// cluster space, plus the door edges of the joint graph", computed here from
/// the public primitives — each room's own compile, the kit's seal boxes
/// against the compile's open leaf boxes, the test's own Warshall. Shares no
/// code with the linker.
/// </summary>
/// <remarks>
/// <see cref="LevelLinkerTests"/> carries its own private copy of this replay
/// (its lane owns that file); the CLI link-verb facts call this one rather
/// than re-deriving the gate a third way. The two copies are deliberately
/// kept structurally identical — same shapes, same epsilon, same closure.
/// </remarks>
internal static class DoorGraphFacts
{
    /// <summary>
    /// Asserts the linked level's whole visibility story: the cluster space is
    /// every room's own clusters in layout order; every row equals the door
    /// graph replay; and the delivered BSP's visibility lump decompresses back
    /// to exactly those rows.
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

        // The visibility lump is not just the VisResult's mirror: it is the
        // file's row set, RLE-packed with the header vvis writes, and it must
        // decompress back to exactly those rows.
        byte[] lump = [.. link.Bsp[BspLump.Visibility].Data.Span];
        Assert.Equal(link.Vis.ClusterCount, BitConverter.ToInt32(lump, 0));
        int rowBytes = link.Vis.RowBytes;
        for (int cluster = 0; cluster < link.Vis.ClusterCount; cluster++)
        {
            int offset = BitConverter.ToInt32(lump, 4 + cluster * 8);
            Assert.True(offset > 0 && offset < lump.Length, $"row {cluster} offset {offset} is outside the lump");
            byte[] row = new byte[rowBytes];
            VisRunLength.Decompress(lump.AsSpan(offset, lump.Length - offset), row);
            Assert.True(row.SequenceEqual(link.Vis.Pvs(cluster)), $"row {cluster} does not survive the lump");
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
                (int dx, int dy) = Offset(socket.Facing);
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

    private static (int Dx, int Dy) Offset(RoomFacing facing) => facing switch
    {
        RoomFacing.PositiveX => (1, 0),
        RoomFacing.NegativeX => (-1, 0),
        RoomFacing.PositiveY => (0, 1),
        _ => (0, -1),
    };
}
