//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Bsp.Faces;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// The Xbox tristripper (<c>Stripify</c>.
/// </summary>
/// <remarks>
/// The facts here check the PROPERTY the output must have — that walking the
/// strip reproduces exactly the input triangle set, degenerates aside — rather
/// than a hard-coded index sequence. A transcribed sequence would pass whether
/// or not it described a valid strip, which is the shape of check this project
/// keeps finding.
/// </remarks>
public class TriangleStripperTests
{
    [Fact]
    public void AnEmptyListStripsToNothing()
    {
        Assert.Empty(TriangleStripper.Stripify([]));
    }

    [Fact]
    public void AListWithAPartialTriangleIsRejected()
    {
        Assert.Throws<ArgumentException>(() => TriangleStripper.Stripify([0, 1]));
    }

    [Fact]
    public void OneTriangleStripsToThreeIndices()
    {
        ushort[] strip = TriangleStripper.Stripify([0, 1, 2]);

        Assert.Equal(3, strip.Length);
        Assert.Equal([0, 1, 2], Triangles(strip).Single());
    }

    [Fact]
    public void TwoTrianglesSharingAnEdgeStripToFourIndices()
    {
        // A quad as two triangles: 0-1-2 and 0-2-3.
        ushort[] strip = TriangleStripper.Stripify([0, 1, 2, 0, 2, 3]);

        Assert.Equal(4, strip.Length);
    }

    [Fact]
    public void AQuadStripReproducesBothTriangles()
    {
        ushort[] strip = TriangleStripper.Stripify([0, 1, 2, 0, 2, 3]);

        Assert.Equal(2, Triangles(strip).Count);
        AssertSameTriangleSet([[0, 1, 2], [0, 2, 3]], Triangles(strip));
    }

    [Fact]
    public void AGridStripReproducesEveryTriangleExactlyOnce()
    {
        List<ushort> triangles = [];
        const int side = 4;

        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                ushort a = (ushort)((y * (side + 1)) + x);
                ushort b = (ushort)(a + 1);
                ushort c = (ushort)(a + side + 1);
                ushort d = (ushort)(c + 1);

                triangles.AddRange([a, c, b]);
                triangles.AddRange([b, c, d]);
            }
        }

        ushort[] strip = TriangleStripper.Stripify(triangles);

        List<int[]> expected = [];

        for (int i = 0; i < triangles.Count; i += 3)
        {
            expected.Add([triangles[i], triangles[i + 1], triangles[i + 2]]);
        }

        AssertSameTriangleSet(expected, Triangles(strip));
    }

    [Fact]
    public void ADisconnectedMeshIsStitchedIntoOneStrip()
    {
        // Two quads that share no vertices at all.
        ushort[] strip = TriangleStripper.Stripify(
            [0, 1, 2, 0, 2, 3, 10, 11, 12, 10, 12, 13]);

        AssertSameTriangleSet(
            [[0, 1, 2], [0, 2, 3], [10, 11, 12], [10, 12, 13]],
            Triangles(strip));
    }

    [Fact]
    public void TheCacheCountsAHitOnlyWhenAnotherStripPutTheVertexThere()
    {
        TriangleStripper.VertCache cache = new();

        cache.Add(1, 7);
        Assert.Equal(0, cache.CacheHits);

        // Same strip: not a hit.
        cache.Add(1, 7);
        Assert.Equal(0, cache.CacheHits);

        // Different strip: a hit.
        cache.Add(2, 7);
        Assert.Equal(1, cache.CacheHits);
    }

    [Fact]
    public void TheCacheForgetsAVertexAfterEighteenOthers()
    {
        TriangleStripper.VertCache cache = new();

        cache.Add(1, 7);

        for (int i = 100; i < 100 + TriangleStripper.CacheSize; i++)
        {
            cache.Add(1, i);
        }

        cache.Add(2, 7);
        Assert.Equal(0, cache.CacheHits);
    }

    [Fact]
    public void TheCostEstimateIsTheLengthsPlusTwoPerJoin()
    {
        List<TriangleStripper.StripVerts> strips =
        [
            new([0, 1, 2], true),
            new([3, 4, 5, 6], true),
        ];

        Assert.Equal(3 + 4 + 2, TriangleStripper.EstimateStripCost(strips));
    }

    /// <summary>
    /// Walks a strip back into triangles, dropping the degenerate ones.
    /// </summary>
    /// <param name="strip">The strip indices.</param>
    /// <returns>One array per non-degenerate triangle, in strip order.</returns>
    private static List<int[]> Triangles(ushort[] strip)
    {
        List<int[]> output = [];

        for (int i = 0; i + 2 < strip.Length; i++)
        {
            int a = strip[i];
            int b = strip[i + 1];
            int c = strip[i + 2];

            if (a == b || b == c || a == c)
            {
                continue;
            }

            // Odd positions flip winding, which a triangle set comparison
            // normalises away below.
            output.Add((i & 1) == 0 ? [a, b, c] : [a, c, b]);
        }

        return output;
    }

    private static void AssertSameTriangleSet(List<int[]> expected, List<int[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        List<string> a = [.. expected.Select(Canonical).Order(StringComparer.Ordinal)];
        List<string> b = [.. actual.Select(Canonical).Order(StringComparer.Ordinal)];

        Assert.Equal(a, b);
    }

    /// <summary>
    /// A triangle as a string, rotated so the smallest index is first — which
    /// keeps the winding but ignores where the strip started it.
    /// </summary>
    /// <param name="triangle">The three indices.</param>
    /// <returns>The canonical spelling.</returns>
    private static string Canonical(int[] triangle)
    {
        int start = 0;

        for (int i = 1; i < 3; i++)
        {
            if (triangle[i] < triangle[start])
            {
                start = i;
            }
        }

        return $"{triangle[start]},{triangle[(start + 1) % 3]},{triangle[(start + 2) % 3]}";
    }
}
