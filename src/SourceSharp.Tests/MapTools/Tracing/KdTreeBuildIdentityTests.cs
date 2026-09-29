//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapFormats;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The KD-tree is the same bytes however it is built: serially, or on any
/// number of workers with the top of the tree refined level by level, and
/// the same as <see cref="ReferenceKdTreeBuilder"/>'s straight recursion.
/// </summary>
/// <remarks>
/// <para>
/// "The same" is the whole serialised result: every node's link word and
/// split bits, the index list, every intersection triangle's bytes, and the
/// bounds. Each scene is built at degrees 1, 2, 3, 4 and 8, and with the
/// smallest frontier job at the default and at 1 and 16, which drives the
/// level-by-level top down into nodes of a handful of triangles, where it
/// meets the leaf-outright, depth-cap and pushed-down paths a big scene only
/// reaches inside a job.
/// </para>
/// <para>
/// The scenes are chosen to reach different rules: random soups in general
/// position; dense clusters under big straddling triangles, which make the
/// subtrees very unequal and duplicate triangles across splits; axis-aligned
/// boxes on a coarse grid, which make flat classifications, one-sided splits
/// that grow an empty side, and the depth cap; triangles that all span the
/// box, whose root is a leaf; and the committed golden map's and, when it has
/// been compiled, the sandbox map's shadow casters.
/// </para>
/// </remarks>
public sealed class KdTreeBuildIdentityTests
{
    private static readonly int[] Degrees = [1, 2, 3, 4, 8];

    private static readonly int[] MinimumJobs = [KdTreeBuilder.ParallelMinimumTriangles, 16, 1];

    /// <summary>A result's bytes, in one comparable array.</summary>
    private static byte[] Serialise(KdBuildResult r)
    {
        using MemoryStream stream = new();
        stream.Write(MemoryMarshal.AsBytes(r.Nodes.AsSpan()));
        stream.Write(MemoryMarshal.AsBytes(r.Indices.AsSpan()));
        stream.Write(MemoryMarshal.AsBytes(r.Triangles.AsSpan()));
        Vec3[] bounds = [r.Min, r.Max];
        stream.Write(MemoryMarshal.AsBytes(bounds.AsSpan()));
        return stream.ToArray();
    }

    private static async Task<KdBuildResult> BuildOnAsync(TracedTriangle[] scene, int degree, int minimumJob)
    {
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = degree });
        return await KdTreeBuilder.BuildAsync(scene, stockNormalise: false, queue, minimumJob, CancellationToken.None);
    }

    /// <summary>
    /// Every degree and frontier size gives the serial build's bytes, and the
    /// serial build gives the reference's tree.
    /// </summary>
    private static async Task AssertIdenticalEverywhereAsync(TracedTriangle[] scene)
    {
        KdBuildResult serial = KdTreeBuilder.Build(scene, stockNormalise: false);
        (int[] children, int[] splitBits, int[] indices) = ReferenceKdTreeBuilder.Build(scene);
        Assert.Equal(children, serial.Nodes.Select(n => n.Children).ToArray());
        Assert.Equal(splitBits, serial.Nodes.Select(n => BitConverter.SingleToInt32Bits(n.Split)).ToArray());
        Assert.Equal(indices, serial.Indices);

        byte[] expected = Serialise(serial);
        foreach (int degree in Degrees)
        {
            foreach (int minimumJob in MinimumJobs)
            {
                byte[] actual = Serialise(await BuildOnAsync(scene, degree, minimumJob));
                Assert.True(
                    expected.AsSpan().SequenceEqual(actual),
                    $"degree {degree}, smallest job {minimumJob}: the tree's bytes differ from the serial build's");
            }
        }
    }

    private static Vec3 RandomPoint(Random random, float scale) =>
        new(
            (float)((random.NextDouble() * 2) - 1) * scale,
            (float)((random.NextDouble() * 2) - 1) * scale,
            (float)((random.NextDouble() * 2) - 1) * scale);

    /// <summary>Triangles in general position, of every size up to a tenth of the box.</summary>
    internal static TracedTriangle[] Soup(int seed, int count)
    {
        Random random = new(seed);
        TracedTriangle[] tris = new TracedTriangle[count];
        for (int i = 0; i < count; i++)
        {
            Vec3 centre = RandomPoint(random, 1000);
            float size = (float)random.NextDouble() * 100;
            tris[i] = new TracedTriangle(
                i, centre + RandomPoint(random, size), centre + RandomPoint(random, size),
                centre + RandomPoint(random, size), (byte)(i & 1));
        }

        return tris;
    }

    /// <summary>Dense clusters of tiny triangles under long triangles that straddle everything.</summary>
    internal static TracedTriangle[] Clusters(int seed)
    {
        Random random = new(seed);
        List<TracedTriangle> tris = [];
        for (int c = 0; c < 4; c++)
        {
            Vec3 centre = RandomPoint(random, 2000);
            int size = 100 + (c * 150);
            for (int i = 0; i < size; i++)
            {
                Vec3 at = centre + RandomPoint(random, 8);
                tris.Add(new TracedTriangle(
                    tris.Count, at, at + RandomPoint(random, 1), at + RandomPoint(random, 1), 0));
            }
        }

        for (int i = 0; i < 60; i++)
        {
            tris.Add(new TracedTriangle(
                tris.Count, RandomPoint(random, 2500), RandomPoint(random, 2500), RandomPoint(random, 2500), 0));
        }

        return [.. tris];
    }

    /// <summary>
    /// The faces of axis-aligned boxes on a coarse grid, overlapping and
    /// coincident: what brush geometry is.
    /// </summary>
    internal static TracedTriangle[] Boxes(int seed, int boxes)
    {
        Random random = new(seed);
        List<TracedTriangle> tris = [];
        for (int b = 0; b < boxes; b++)
        {
            Vec3 lo = new(random.Next(-8, 8) * 64, random.Next(-8, 8) * 64, random.Next(-8, 8) * 64);
            Vec3 hi = lo + new Vec3(random.Next(1, 4) * 64, random.Next(1, 4) * 64, random.Next(1, 4) * 64);
            for (int axis = 0; axis < 3; axis++)
            {
                foreach (float plane in (float[])[lo[axis], hi[axis]])
                {
                    int u = (axis + 1) % 3;
                    int v = (axis + 2) % 3;
                    Vec3 Corner(float a, float b2)
                    {
                        float[] p = new float[3];
                        p[axis] = plane;
                        p[u] = a;
                        p[v] = b2;
                        return new Vec3(p[0], p[1], p[2]);
                    }

                    tris.Add(new TracedTriangle(tris.Count, Corner(lo[u], lo[v]), Corner(hi[u], lo[v]), Corner(hi[u], hi[v]), 0));
                    tris.Add(new TracedTriangle(tris.Count, Corner(lo[u], lo[v]), Corner(hi[u], hi[v]), Corner(lo[u], hi[v]), 0));
                }
            }
        }

        return [.. tris];
    }

    /// <summary>Triangles that each reach across the whole box: no split beats a leaf.</summary>
    internal static TracedTriangle[] Spanning(int count)
    {
        TracedTriangle[] tris = new TracedTriangle[count];
        for (int i = 0; i < count; i++)
        {
            float o = i * 0.001f;
            tris[i] = new TracedTriangle(
                i, new Vec3(-100 + o, -100, -100), new Vec3(100, 100 - o, -100), new Vec3(0, 0, 100 + o), 0);
        }

        return tris;
    }

    /// <summary>A soup of 3,000 triangles.</summary>
    [Fact]
    public Task ASoupIsTheSameTreeEverywhere() => AssertIdenticalEverywhereAsync(Soup(1, 3000));

    /// <summary>A small soup, where the frontier sizes reach the tiniest nodes.</summary>
    [Fact]
    public Task ASmallSoupIsTheSameTreeEverywhere() => AssertIdenticalEverywhereAsync(Soup(2, 60));

    /// <summary>A soup bigger than one extent chunk, so a top node's extents are folded from several.</summary>
    [Fact]
    public Task ASoupOfSeveralExtentChunksIsTheSameTreeEverywhere() =>
        AssertIdenticalEverywhereAsync(Soup(3, (2 * KdTreeBuilder.ExtentChunk) + 777));

    /// <summary>Unequal clusters under straddling triangles.</summary>
    [Fact]
    public Task ClustersAreTheSameTreeEverywhere() => AssertIdenticalEverywhereAsync(Clusters(4));

    /// <summary>Axis-aligned boxes: flat triangles, grown sides and the depth cap.</summary>
    [Fact]
    public Task BoxesAreTheSameTreeEverywhere() => AssertIdenticalEverywhereAsync(Boxes(5, 150));

    /// <summary>A scene whose root is a leaf, decided in the top rather than in a job.</summary>
    [Fact]
    public async Task ASceneWhoseRootIsALeafIsTheSameTreeEverywhere()
    {
        TracedTriangle[] scene = Spanning(40);
        Assert.Single(KdTreeBuilder.Build(scene, stockNormalise: false).Nodes);
        await AssertIdenticalEverywhereAsync(scene);
    }

    /// <summary>Two triangles: a leaf before any trial, in the top.</summary>
    [Fact]
    public Task TwoTrianglesAreTheSameTreeEverywhere() => AssertIdenticalEverywhereAsync(Soup(6, 2));

    /// <summary>The committed golden map's shadow casters.</summary>
    [Fact]
    public async Task TheGoldenMapsCastersAreTheSameTreeEverywhere() =>
        await AssertIdenticalEverywhereAsync(await CastersAsync(GoldenBsp.Lockdown()));

    /// <summary>The generated sandbox map's shadow casters.</summary>
    [SandboxBspFact]
    public async Task TheSandboxMapsCastersAreTheSameTreeEverywhere() =>
        await AssertIdenticalEverywhereAsync(await CastersAsync(GoldenBsp.Sandbox()!));

    private static async Task<TracedTriangle[]> CastersAsync(string bspPath)
    {
        await using FileStream stream = File.OpenRead(bspPath);
        BspData bsp = await BspFile.LoadAsync(stream, CancellationToken.None);
        ShadowCasterLoadReport loaded = await ShadowCasterLoader.LoadAsync(
            bsp, VradOptions.Default, new ContentFileSystem([]), NullPropCollisionSource.Instance);
        TracedTriangle[] tris = [.. loaded.Set.Triangles];
        Assert.True(tris.Length > 1000, $"{bspPath}: only {tris.Length} casters");
        return tris;
    }

    /// <summary>The committed stock trees come out of every degree with the smallest jobs.</summary>
    [Theory]
    [InlineData("kd-scene")]
    [InlineData("kd-boxes")]
    public async Task StocksTreesComeOutOfEveryDegree(string prefix)
    {
        StockKdScene scene = StockKdScene.Load(prefix);
        foreach (int degree in Degrees)
        {
            KdBuildResult r = await BuildOnAsync(scene.Triangles, degree, 1);
            Assert.Equal(scene.NodeChildren, r.Nodes.Select(n => n.Children).ToArray());
            Assert.Equal(scene.NodeSplitBits, r.Nodes.Select(n => BitConverter.SingleToInt32Bits(n.Split)).ToArray());
            Assert.Equal(scene.Indices, r.Indices);
        }
    }

    /// <summary>The stock normalisation reaches the parallel build's conversion too.</summary>
    [Fact]
    public async Task TheStockNormalisationIsTheSameOnEveryDegree()
    {
        TracedTriangle[] scene = Soup(7, 2000);
        byte[] expected = Serialise(KdTreeBuilder.Build(scene, stockNormalise: true));
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = 4 });
        KdBuildResult r = await KdTreeBuilder.BuildAsync(scene, stockNormalise: true, queue, 1, CancellationToken.None);
        Assert.True(expected.AsSpan().SequenceEqual(Serialise(r)));
    }
}
