//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// The face list a cluster's receivers share must be, receiver by receiver,
/// the faces stock's per-receiver <c>BuildVisRow</c> walk tests, in its order.
/// </summary>
public sealed class VisRowFacesTests
{
    private const int Faces = 60;

    /// <summary>A random map shape: shared faces, displacements in several clusters.</summary>
    private sealed record Shape(ClusterTables Tables, int[][] Leaves, int[][] Disps, LeafInfo[] LeafInfos, ushort[] LeafFaces);

    private static Shape RandomShape(Random random, int clusters)
    {
        List<LeafInfo> leafInfos = [];
        List<ushort> leafFaces = [];
        int[][] leaves = new int[clusters][];
        int[][] disps = new int[clusters][];
        for (int c = 0; c < clusters; c++)
        {
            leaves[c] = new int[random.Next(0, 5)];
            for (int i = 0; i < leaves[c].Length; i++)
            {
                int first = leafFaces.Count;
                int n = random.Next(0, 7);
                for (int k = 0; k < n; k++)
                {
                    // Few distinct faces, so leaves and clusters share them.
                    leafFaces.Add((ushort)random.Next(Faces));
                }

                leaves[c][i] = leafInfos.Count;
                leafInfos.Add(new LeafInfo(0, c, 0, 0, Vec3.Zero, Vec3.Zero, first, n));
            }

            disps[c] = [.. Enumerable.Range(0, random.Next(0, 4)).Select(_ => random.Next(Faces))];
        }

        return new Shape(ClusterTables.FromLists(leaves, disps), leaves, disps, [.. leafInfos], [.. leafFaces]);
    }

    /// <summary>
    /// Stock's walk for one receiver, written out as stock has it: both
    /// "tested" sets cleared per receiver, and the receiver's own face
    /// skipped after it is marked tested.
    /// </summary>
    private static List<int> StockWalk(Shape shape, byte[] pvs, int clusters, int ownFace)
    {
        bool[] faceTested = new bool[Faces];
        bool[] dispTested = new bool[Faces];
        List<int> tested = [];
        for (int j = 0; j < clusters; j++)
        {
            if ((pvs[j >> 3] & (1 << (j & 7))) == 0)
            {
                continue;
            }

            foreach (int leafIndex in shape.Leaves[j])
            {
                LeafInfo leaf = shape.LeafInfos[leafIndex];
                for (int k = 0; k < leaf.NumLeafFaces; k++)
                {
                    int l = shape.LeafFaces[leaf.FirstLeafFace + k];
                    if (faceTested[l])
                    {
                        continue;
                    }

                    faceTested[l] = true;
                    if (l != ownFace)
                    {
                        tested.Add(l);
                    }
                }
            }

            foreach (int face in shape.Disps[j])
            {
                if (dispTested[face])
                {
                    continue;
                }

                dispTested[face] = true;
                if (face != ownFace)
                {
                    tested.Add(face);
                }
            }
        }

        return tested;
    }

    /// <summary>
    /// Many rows walked one after another over the same stamp arrays, as one
    /// worker does: each row's list, less any one face, is stock's walk for a
    /// receiver on that face.
    /// </summary>
    [Fact]
    public void EveryReceiverOfARowTestsStocksFacesInStocksOrder()
    {
        Random random = new(97);
        const int Clusters = 13;
        int[] faceStamp = new int[Faces];
        int[] dispStamp = new int[Faces];
        int[] faces = new int[2 * Faces];
        int stamp = 0;
        bool sawDuplicate = false;

        for (int trial = 0; trial < 200; trial++)
        {
            Shape shape = RandomShape(random, Clusters);
            byte[] pvs = new byte[(Clusters + 7) / 8];
            random.NextBytes(pvs);

            int count = VisMatrix.FacesInRow(
                pvs, Clusters, shape.Tables, shape.LeafInfos, shape.LeafFaces, faceStamp, dispStamp, ++stamp, faces);
            int[] row = faces[..count];
            sawDuplicate |= row.Distinct().Count() < row.Length;

            for (int own = -1; own < Faces; own++)
            {
                Assert.Equal(StockWalk(shape, pvs, Clusters, own), row.Where(f => f != own));
            }
        }

        // A face filed both in a leaf and as a displacement is tested twice.
        Assert.True(sawDuplicate);
    }

    /// <summary>A row that sees no cluster tests nothing.</summary>
    [Fact]
    public void AnEmptyRowHasNoFaces()
    {
        Shape shape = RandomShape(new Random(5), 8);
        int count = VisMatrix.FacesInRow(
            new byte[1], 8, shape.Tables, shape.LeafInfos, shape.LeafFaces, new int[Faces], new int[Faces], 1, new int[2 * Faces]);
        Assert.Equal(0, count);
    }
}
