//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// <see cref="DispCollisionSet.ClipRayInLeaf"/> on the leaf-ambient fixture,
/// whose power-3 floor sits in some of its 37 leaves and not in the others:
/// a leaf with no displacement answers "no hit" without testing anything,
/// and every leaf answers what the plain walk over its list answers.
/// </summary>
public sealed class DispCollisionSetTests(AmbientFixture fixture) : IClassFixture<AmbientFixture>
{
    private static readonly DispRayHit NoHit = new(float.MaxValue, -1, 0, 0, Vec3.Zero);

    [Fact]
    public void ALeafWithNoDisplacementAnswersNoHitAndTestsNothing()
    {
        DispCollisionSet set = DispCollisionSet.Build(fixture.Bsp);
        int leafCount = AmbientScene.ReadLeaves(fixture.Bsp).Length;
        int[] empty = [.. Enumerable.Range(0, leafCount).Where(l => set.InLeaf(l).IsEmpty)];
        Assert.NotEmpty(empty);
        Assert.True(set.Count > 0);

        DispTestedScratch scratch = set.CreateScratch();
        foreach (int leaf in empty)
        {
            foreach ((Vec3 start, Vec3 delta) in Rays(set, 16))
            {
                scratch.StartRayTest();
                set.ClipRayInLeaf(scratch, start, delta, leaf, out DispRayHit hit);

                Assert.Equal(NoHit, hit);
                // Nothing was marked tested for this ray.
                for (int d = 0; d < set.Count; d++)
                {
                    Assert.True(scratch.TryMark(d));
                }
            }
        }
    }

    [Fact]
    public void AMapWithNoDisplacementsAnswersNoHitInEveryLeaf()
    {
        DispCollisionSet set = DispCollisionSet.Empty(4);
        DispTestedScratch scratch = set.CreateScratch();
        for (int leaf = 0; leaf < 4; leaf++)
        {
            scratch.StartRayTest();
            set.ClipRayInLeaf(scratch, new Vec3(0, 0, 64), new Vec3(0, 0, -128), leaf, out DispRayHit hit);
            Assert.Equal(NoHit, hit);
        }
    }

    [Fact]
    public void EveryLeafAnswersWhatThePlainWalkOverItsListAnswers()
    {
        // The reference is the scalar walk, written out: each listed tree not
        // yet tested this ray, in list order, its own ray test, strictly
        // nearer wins. It covers the empty leaves and the batched walk's
        // leaves alike; a fresh ray per leaf so the tested set starts clean.
        DispCollisionSet set = DispCollisionSet.Build(fixture.Bsp);
        DispCollisionTree[] trees = Trees(set);
        int leafCount = AmbientScene.ReadLeaves(fixture.Bsp).Length;
        DispTestedScratch scratch = set.CreateScratch();
        int hits = 0;
        int emptyVisits = 0;

        foreach ((Vec3 start, Vec3 delta) in Rays(set, 400))
        {
            for (int leaf = 0; leaf < leafCount; leaf++)
            {
                DispRayHit expected = NoHit;
                foreach (int d in set.InLeaf(leaf))
                {
                    if (trees[d].Ray(start, delta, out DispRayHit one) && one.Distance < expected.Distance)
                    {
                        expected = one;
                    }
                }

                scratch.StartRayTest();
                set.ClipRayInLeaf(scratch, start, delta, leaf, out DispRayHit actual);

                Assert.Equal(expected, actual);
                hits += actual.Face >= 0 ? 1 : 0;
                emptyVisits += set.InLeaf(leaf).IsEmpty ? 1 : 0;
            }
        }

        // Both roads were taken, and the non-empty one found the floor.
        Assert.True(hits > 0);
        Assert.True(emptyVisits > 0);
    }

    // Rays between points in the displacements' bounds grown by 64 units, so
    // a good share cross the floor; a fixed seed keeps them the same run to run.
    private static IEnumerable<(Vec3 Start, Vec3 Delta)> Rays(DispCollisionSet set, int count)
    {
        DispCollisionTree[] trees = Trees(set);
        Vec3 min = trees[0].Mins;
        Vec3 max = trees[0].Maxs;
        foreach (DispCollisionTree t in trees)
        {
            min = new Vec3(Math.Min(min.X, t.Mins.X), Math.Min(min.Y, t.Mins.Y), Math.Min(min.Z, t.Mins.Z));
            max = new Vec3(Math.Max(max.X, t.Maxs.X), Math.Max(max.Y, t.Maxs.Y), Math.Max(max.Z, t.Maxs.Z));
        }

        Random random = new(1234);
        Vec3 Point() => new(
            (float)(min.X - 64 + (random.NextDouble() * (max.X - min.X + 128))),
            (float)(min.Y - 64 + (random.NextDouble() * (max.Y - min.Y + 128))),
            (float)(min.Z - 64 + (random.NextDouble() * (max.Z - min.Z + 128))));

        for (int i = 0; i < count; i++)
        {
            Vec3 a = Point();
            Vec3 b = Point();
            yield return (a, b - a);
        }
    }

    // The set's trees, by displacement index, for the reference walk.
    private static DispCollisionTree[] Trees(DispCollisionSet set) =>
        [.. Enumerable.Range(0, set.Count).Select(set.Tree)];
}
