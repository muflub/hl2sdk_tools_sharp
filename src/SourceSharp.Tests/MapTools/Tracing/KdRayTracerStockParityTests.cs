//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Tracing;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The recorded KD scene and a tree built from it, shared across the facts.
/// </summary>
public sealed class KdParityFixture
{
    /// <summary>Loads the scene and builds the tree.</summary>
    public KdParityFixture()
    {
        Scene = StockKdScene.Load();
        Tracer = KdRayTracer.Build(Scene.Triangles);
        Ours = new HitId[Scene.RayCount];
        Tracer.TraceClosest(Scene.Rays, Ours, RayTraceOptions.StockExact);
    }

    /// <summary>The scene, tree and answers stock produced.</summary>
    internal StockKdScene Scene { get; }

    /// <summary>The tree this port built from the same triangles.</summary>
    public KdRayTracer Tracer { get; }

    /// <summary>This port's answers for the same rays.</summary>
    public HitId[] Ours { get; }
}

/// <summary>
/// The 4a gate for the KD-tree: the same TREE as stock, node for node, and the
/// same hits.
/// </summary>
/// <remarks>
/// <para>
/// The scene is 2,000 triangles in general position and 8,000 rays crossing
/// them, all generated from one seed. **Not a lattice and nothing
/// axis-aligned**: a prior lane measured 20,319 differing bits in 5 M rays on
/// lattice geometry falling to 4 with a 0.35-unit jitter, so a lattice would
/// make this fact a test of the tracer's epsilons.
/// </para>
/// <para>
/// The reference drop ships no ready-made ray-tracer test or recorded
/// reference output, so compiling the reference tracer itself
/// and comparing against it is what this does instead, and it is a stronger
/// check than a recorded text file: it compares the acceleration structure as
/// well as the answers.
/// </para>
/// </remarks>
public sealed class KdRayTracerStockParityTests : IClassFixture<KdParityFixture>
{
    private readonly KdParityFixture _fixture;

    /// <summary>Takes the shared tree.</summary>
    /// <param name="fixture">The shared fixture.</param>
    /// <exception cref="ArgumentNullException">The fixture is null.</exception>
    public KdRayTracerStockParityTests(KdParityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    /// <summary>A node is eight bytes, as <c>CacheOptimizedKDNode</c> is.</summary>
    [Fact]
    public void ANodeIsEightBytes()
    {
        Assert.Equal(8, KdRayTracer.NodeStrideBytes);
    }

    /// <summary>
    /// An intersection-format triangle is 48 bytes, which is what stock's
    /// actually is.
    /// </summary>
    /// <remarks>
    /// the reference implementation's own comment says "this structure is 16longs=64
    /// bytes for cache line packing" and §4a repeats it. A build of stock's
    /// header in this tree prints <c>sizeof(CacheOptimizedTriangle)=48</c>:
    /// four floats of plane, an int id, six floats of edge equation and four
    /// bytes. The comment is stale, and this fact records which of the two is
    /// the truth.
    /// </remarks>
    [Fact]
    public void AnIntersectionTriangleIsFortyEightBytesNotSixtyFour()
    {
        Assert.Equal(48, KdRayTracer.TriangleStrideBytes);
    }

    /// <summary>The tree has exactly as many nodes as stock's.</summary>
    [Fact]
    public void TheTreeHasTheSameNodeCountAsStocks()
    {
        Assert.Equal(_fixture.Scene.NodeChildren.Length, _fixture.Tracer.NodeCount);
    }

    /// <summary>
    /// Every node is stock's node: the same type, the same child, the same
    /// splitting plane bits.
    /// </summary>
    /// <remarks>
    /// The strongest fact in this lane. A tree that merely gave the same hits
    /// could have a different shape and a different cost, and the SAH
    /// constants, the strided candidate selection and the empty-side growing
    /// would all be unverified. Comparing the packed words compares all three
    /// at once.
    /// </remarks>
    [Fact]
    public void EveryNodeMatchesStocksNodeExactly()
    {
        int differing = 0;
        int firstBad = -1;
        for (int i = 0; i < _fixture.Tracer.NodeCount; i++)
        {
            (int children, int splitBits) = _fixture.Tracer.Node(i);
            if (children != _fixture.Scene.NodeChildren[i]
                || splitBits != _fixture.Scene.NodeSplitBits[i])
            {
                differing++;
                if (firstBad < 0)
                {
                    firstBad = i;
                }
            }
        }

        Assert.True(
            differing == 0,
            differing == 0
                ? string.Empty
                : $"{differing} of {_fixture.Tracer.NodeCount} nodes differ; the first is node "
                  + $"{firstBad}");
    }

    /// <summary>The triangle index list is stock's, entry for entry.</summary>
    /// <remarks>
    /// This is where the partition order shows up: stock fills the left
    /// group forwards, the right group BACKWARDS from the end, and the
    /// straddling group into the gap between them. Any other order gives the
    /// same tree with a differently ordered leaf, and a leaf's order decides
    /// which of two equidistant triangles wins.
    /// </remarks>
    [Fact]
    public void TheTriangleIndexListMatchesStocks()
    {
        Assert.Equal(_fixture.Scene.Indices.Length, _fixture.Tracer.IndexCount);

        int differing = 0;
        for (int i = 0; i < _fixture.Tracer.IndexCount; i++)
        {
            if (_fixture.Tracer.Index(i) != _fixture.Scene.Indices[i])
            {
                differing++;
            }
        }

        Assert.Equal(0, differing);
    }

    /// <summary>
    /// Every triangle's intersection format is stock's, bit for bit.
    /// </summary>
    /// <remarks>
    /// Including the normalisation, which goes through
    /// <c>NormaliseLikeStock</c>: the plane normal comes from
    /// <c>VectorNormalize</c>, an <c>rsqrtss</c> estimate plus one Newton
    /// step, and the dropped axis is chosen by comparing its components. An
    /// exact normalise would pick a different axis on a triangle whose normal
    /// has two nearly equal components, and every edge equation after it would
    /// differ.
    /// </remarks>
    [ReferenceRsqrtFact]
    public void EveryTriangleConvertsExactlyAsStockDoes()
    {
        (string[] stock, string[] ours) = KdVendorLines.Triangles(_fixture.Scene, _fixture.Tracer);
        IReadOnlyList<string> expected = VendorGolden.Expected("kd-scene.triangles", stock, ours);

        int normalDiff = 0;
        int edgeDiff = 0;
        int axisDiff = 0;

        for (int i = 0; i < ours.Length; i++)
        {
            string[] want = expected[i].Split('|');
            string[] got = ours[i].Split('|');
            normalDiff += want[0] == got[0] ? 0 : 1;
            edgeDiff += want[1] == got[1] ? 0 : 1;
            axisDiff += want[2] == got[2] ? 0 : 1;
        }

        Assert.Equal(expected.Count, ours.Length);
        Assert.Equal(0, normalDiff);
        Assert.Equal(0, edgeDiff);
        Assert.Equal(0, axisDiff);
    }

    /// <summary>The scene bounds are stock's.</summary>
    [Fact]
    public void TheSceneBoundsMatchStocks()
    {
        Assert.Equal(_fixture.Scene.Min, _fixture.Tracer.MinBound);
        Assert.Equal(_fixture.Scene.Max, _fixture.Tracer.MaxBound);
    }

    /// <summary>Most rays hit something, so the comparison is not vacuous.</summary>
    [Fact]
    public void StockHitsSomethingOnMostRays()
    {
        int hits = 0;
        for (int i = 0; i < _fixture.Scene.RayCount; i++)
        {
            if (_fixture.Scene.StockHitId[i] >= 0)
            {
                hits++;
            }
        }

        Assert.True(
            hits > _fixture.Scene.RayCount / 4,
            $"stock hit on {hits} of {_fixture.Scene.RayCount} rays");
    }

    /// <summary>Every ray hits the same triangle as stock.</summary>
    [Fact]
    public void EveryRayHitsTheSameTriangleAsStock()
    {
        int differing = 0;
        int firstBad = -1;
        for (int i = 0; i < _fixture.Scene.RayCount; i++)
        {
            // Stock reports the tree's triangle INDEX; the seam reports the
            // caller's id, and the recorded scene numbers its triangles 0..n-1
            // so the two coincide. A miss is -1 on both sides.
            int stock = _fixture.Scene.StockHitId[i];
            int ours = _fixture.Ours[i].Surface;
            if (stock != ours)
            {
                differing++;
                if (firstBad < 0)
                {
                    firstBad = i;
                }
            }
        }

        Assert.True(
            differing == 0,
            differing == 0
                ? string.Empty
                : $"{differing} of {_fixture.Scene.RayCount} rays hit a different triangle; the "
                  + $"first is ray {firstBad}, stock {_fixture.Scene.StockHitId[firstBad]}, "
                  + $"ours {_fixture.Ours[firstBad].Surface}");
    }

    /// <summary>Every hit is at the same distance as stock's, bit for bit.</summary>
    [ReferenceRsqrtFact]
    public void EveryHitDistanceMatchesStockBitForBit()
    {
        (string[] stock, string[] ours) = KdVendorLines.HitDistances(_fixture.Scene, _fixture.Ours);
        IReadOnlyList<string> expected = VendorGolden.Expected("kd-scene.distances", stock, ours);

        Assert.Equal(expected.Count, ours.Length);
        Assert.Empty(ours.Where((line, i) => line != expected[i]));
    }

    /// <summary>
    /// Visibility bits agree with stock's hits SHORT OF THE SEGMENT'S END --
    /// the test stock's callers make on an unclipped trace.
    /// The scene's rays reach 1,
    /// so a stock hit at a distance of 1 or more is not a block.
    /// </summary>
    [Fact]
    public void VisibilityBitsAgreeWithStocksHitsShortOfTheEnd()
    {
        ulong[] bits = new ulong[(_fixture.Scene.RayCount + 63) / 64];
        _fixture.Tracer.TraceVisibility(_fixture.Scene.Rays, bits, RayTraceOptions.StockExact);

        int differing = 0;
        for (int i = 0; i < _fixture.Scene.RayCount; i++)
        {
            bool bit = (bits[i >> 6] & (1UL << (i & 63))) != 0;
            bool stockBlocks = _fixture.Scene.StockHitId[i] >= 0 && _fixture.Scene.StockDistance[i] < 1.0f;
            if (bit != stockBlocks)
            {
                differing++;
            }
        }

        Assert.Equal(0, differing);
    }

    private static bool SameBits(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
}

/// <summary>
/// A KD scene's stock-normalised quantities as lines, the unit a
/// <see cref="VendorGolden"/> delta records.
/// </summary>
internal static class KdVendorLines
{
    /// <summary>Per ray, the hit distance's bits, or <c>-</c> where stock missed.</summary>
    public static (string[] Stock, string[] Ours) HitDistances(StockKdScene scene, HitId[] ours)
    {
        string[] stock = new string[scene.RayCount];
        string[] mine = new string[scene.RayCount];
        for (int i = 0; i < scene.RayCount; i++)
        {
            bool hit = scene.StockHitId[i] >= 0;
            stock[i] = hit ? VendorGolden.Bits(scene.StockDistance[i]) : "-";
            mine[i] = hit ? VendorGolden.Bits(ours[i].Fraction) : "-";
        }

        return (stock, mine);
    }

    /// <summary>Per triangle, <c>normal and d | six edges | two axes</c>.</summary>
    public static (string[] Stock, string[] Ours) Triangles(StockKdScene scene, KdRayTracer tracer)
    {
        string[] stock = new string[tracer.TriangleCount];
        string[] mine = new string[tracer.TriangleCount];
        for (int i = 0; i < tracer.TriangleCount; i++)
        {
            (var normal, float d, _, float[] edges, int cs0, int cs1) = tracer.Triangle(i);
            mine[i] = Line(normal.X, normal.Y, normal.Z, d, edges, cs0, cs1);

            Vec3 n = scene.TriangleNormals[i];
            stock[i] = Line(
                n.X, n.Y, n.Z, scene.TriangleD[i], scene.TriangleEdges.AsSpan(i * 6, 6),
                scene.TriangleCoordSelect[i * 2], scene.TriangleCoordSelect[(i * 2) + 1]);
        }

        return (stock, mine);
    }

    private static string Line(float nx, float ny, float nz, float d, ReadOnlySpan<float> edges, int cs0, int cs1)
    {
        string[] e = new string[edges.Length];
        for (int k = 0; k < edges.Length; k++)
        {
            e[k] = VendorGolden.Bits(edges[k]);
        }

        return $"{VendorGolden.Bits(nx)} {VendorGolden.Bits(ny)} {VendorGolden.Bits(nz)} {VendorGolden.Bits(d)}"
            + $"|{string.Join(' ', e)}|{cs0} {cs1}";
    }
}
