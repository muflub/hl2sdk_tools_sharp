//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The pieces of <see cref="KdTreeBuilder"/> whose exactness the whole-tree
/// facts (<see cref="KdTreeBuildIdentityTests"/>) rely on but cannot reach
/// with ordinary geometry: the vector side count against the scalar rule on
/// NaNs, signed zeros and flat triangles; the chunked extent fold against the
/// one-pass fold on the same; the item-to-node map; the frontier size; and a
/// build's refusals and cancellation.
/// </summary>
public sealed class KdTreeBuilderPartsTests
{
    private const float Huge = 1.0e23f;

    private static readonly float[] Special =
    [
        0f, -0f, 1f, -1f, 0.5f, 1e-40f, -1e-40f, float.PositiveInfinity, float.NegativeInfinity,
        float.NaN, BitConverter.Int32BitsToSingle(0x7FC0_1234), BitConverter.Int32BitsToSingle(unchecked((int)0xFFC0_0042)),
        1e30f, -1e30f, Huge, -Huge, 3f, -3f,
    ];

    /// <summary>A triangle list's extents on one axis, as random picks from the special values.</summary>
    private static (float[] Mins, float[] Maxs) RandomExtents(int seed, int count)
    {
        Random random = new(seed);
        float[] mins = new float[count];
        float[] maxs = new float[count];
        for (int i = 0; i < count; i++)
        {
            float a = Special[random.Next(Special.Length)];
            float b = random.Next(4) == 0 ? a : Special[random.Next(Special.Length)];
            mins[i] = a;
            maxs[i] = b;
        }

        return (mins, maxs);
    }

    /// <summary>
    /// The vector count gives the scalar rule's counts on every length
    /// around a vector's width and every special split.
    /// </summary>
    [Fact]
    public void CountSidesAgreesWithTheScalarRuleOnSpecialValues()
    {
        for (int count = 0; count <= 70; count++)
        {
            (float[] mins, float[] maxs) = RandomExtents(count, count);
            foreach (float split in Special)
            {
                int left = 0;
                int right = 0;
                for (int t = 0; t < count; t++)
                {
                    switch (KdTreeBuilder.ClassifyAgainstAxisSplit(mins[t], maxs[t], split))
                    {
                        case KdTreeBuilder.PlaneCheckNegative: left++; break;
                        case KdTreeBuilder.PlaneCheckPositive: right++; break;
                    }
                }

                KdTreeBuilder.CountSides(mins, maxs, split, out int nl, out int nr, out int nb);
                Assert.Equal((left, right, count - left - right), (nl, nr, nb));
            }
        }
    }

    /// <summary>The classification's four outcomes, one each.</summary>
    [Fact]
    public void ClassificationFollowsTheRule()
    {
        Assert.Equal(KdTreeBuilder.PlaneCheckPositive, KdTreeBuilder.ClassifyAgainstAxisSplit(2, 3, 2));
        Assert.Equal(KdTreeBuilder.PlaneCheckNegative, KdTreeBuilder.ClassifyAgainstAxisSplit(1, 2, 2));
        Assert.Equal(KdTreeBuilder.PlaneCheckStraddling, KdTreeBuilder.ClassifyAgainstAxisSplit(1, 3, 2));
        Assert.Equal(KdTreeBuilder.PlaneCheckStraddling, KdTreeBuilder.ClassifyAgainstAxisSplit(float.NaN, float.NaN, 2));
    }

    private static KdBuildTriangle Triangle(float a, float b, float c, int axis)
    {
        KdBuildTriangle t = default;
        t.V[axis] = a;
        t.V[3 + axis] = b;
        t.V[6 + axis] = c;
        return t;
    }

    /// <summary>
    /// Folding the extents of chunks, of any sizes, gives the bits of one
    /// pass over the whole list: NaN payloads, signed zeros, infinities and
    /// values beyond the starting 1e23 included.
    /// </summary>
    [Fact]
    public void ChunkedExtentsFoldToTheOnePassBits()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            Random random = new(seed);
            int count = 1 + random.Next(60);
            int axis = seed % 3;
            KdBuildTriangle[] tris = new KdBuildTriangle[count];
            int[] list = new int[count];
            for (int i = 0; i < count; i++)
            {
                tris[i] = Triangle(
                    Special[random.Next(Special.Length)], Special[random.Next(Special.Length)],
                    Special[random.Next(Special.Length)], axis);
                list[i] = count - 1 - i;
            }

            float[] mins = new float[count];
            float[] maxs = new float[count];
            (float wholeMin, float wholeMax) = KdTreeBuilder.AxisExtents(tris, list, 0, count, axis, mins, maxs);

            List<float> chunkMin = [];
            List<float> chunkMax = [];
            float[] chunkMins = new float[count];
            float[] chunkMaxs = new float[count];
            for (int start = 0; start < count;)
            {
                int length = Math.Min(count - start, 1 + random.Next(7));
                (float cmin, float cmax) = KdTreeBuilder.AxisExtents(
                    tris, list, start, length, axis, chunkMins.AsSpan(start, length), chunkMaxs.AsSpan(start, length));
                chunkMin.Add(cmin);
                chunkMax.Add(cmax);
                start += length;
            }

            (float foldMin, float foldMax) = KdTreeBuilder.FoldChunkExtents([.. chunkMin], [.. chunkMax]);
            Assert.Equal(BitConverter.SingleToInt32Bits(wholeMin), BitConverter.SingleToInt32Bits(foldMin));
            Assert.Equal(BitConverter.SingleToInt32Bits(wholeMax), BitConverter.SingleToInt32Bits(foldMax));
            Assert.Equal(mins, chunkMins);
            Assert.Equal(maxs, chunkMaxs);
        }
    }

    /// <summary>A node with no chunks is the starting pair.</summary>
    [Fact]
    public void NoChunksFoldToTheStartingPair() =>
        Assert.Equal((Huge, -Huge), KdTreeBuilder.FoldChunkExtents([], []));

    /// <summary>Items map to their node, skipping nodes with none.</summary>
    [Fact]
    public void ItemsMapToTheirNode()
    {
        int[] firstItem = [0, 0, 3, 3, 3, 9];
        int[] owners = [.. Enumerable.Range(0, 9).Select(i => KdTreeBuilder.ItemOwner(firstItem, i))];
        Assert.Equal([1, 1, 1, 4, 4, 4, 4, 4, 4], owners);
        Assert.Equal(0, KdTreeBuilder.ItemOwner([0, 5], 4));
    }

    /// <summary>One worker has no top; more share the scene, never below the floor.</summary>
    [Fact]
    public void TheFrontierSizeFollowsTheWorkers()
    {
        Assert.Equal(int.MaxValue, KdTreeBuilder.JobSize(1_000_000, 1, 512));
        Assert.Equal(1_000_000 / (4 * 32), KdTreeBuilder.JobSize(1_000_000, 4, 512));
        Assert.Equal(512, KdTreeBuilder.JobSize(10_000, 4, 512));
    }

    /// <summary>An empty scene is refused by both builds.</summary>
    [Fact]
    public async Task AnEmptySceneIsRefused()
    {
        Assert.Throws<ArgumentException>(() => KdTreeBuilder.Build([], stockNormalise: false));
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = 2 });
        await Assert.ThrowsAsync<ArgumentException>(
            () => KdTreeBuilder.BuildAsync(ReadOnlyMemory<TracedTriangle>.Empty, false, queue, CancellationToken.None));
    }

    /// <summary>A frontier smaller than one triangle is refused.</summary>
    [Fact]
    public async Task ASmallestJobBelowOneIsRefused()
    {
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = 2 });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => KdTreeBuilder.BuildAsync(KdTreeBuildIdentityTests.Soup(1, 10), false, queue, 0, CancellationToken.None));
    }

    /// <summary>
    /// A cancelled build throws, and the same queue then builds the same tree
    /// as a fresh one: nothing of the cancelled build is left behind.
    /// </summary>
    [Fact]
    public async Task ACancelledBuildLeavesTheQueueFit()
    {
        TracedTriangle[] scene = KdTreeBuildIdentityTests.Soup(9, 5000);
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = 4 });
        using CancellationTokenSource cts = new();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => KdTreeBuilder.BuildAsync(scene, false, queue, 16, cts.Token));

        KdBuildResult after = await KdTreeBuilder.BuildAsync(scene, false, queue, 16, CancellationToken.None);
        KdBuildResult serial = KdTreeBuilder.Build(scene, stockNormalise: false);
        Assert.Equal(serial.Indices, after.Indices);
        Assert.Equal(serial.Nodes.Select(n => (n.Children, n.Split)), after.Nodes.Select(n => (n.Children, n.Split)));
    }
}
