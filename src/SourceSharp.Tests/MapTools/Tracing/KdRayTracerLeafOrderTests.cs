using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// Which triangle of a leaf wins, when two are tested together.
/// </summary>
/// <remarks>
/// <para>
/// Stock tests a leaf's triangles one at a time in index order and keeps a hit
/// only when it is STRICTLY nearer than the best so far
/// (<c>raytrace.cpp:496</c>: <c>CmpLtSIMD(isect_t, rslt_out-&gt;HitDistance)</c>),
/// so of two triangles at the same distance the first one listed wins. The
/// tracer tests two triangles in one eight-lane pass on AVX; these facts pin
/// that the pass still answers exactly what testing them one after the other
/// would.
/// </para>
/// <para>
/// Two triangles make one leaf: <c>RefineNode</c> never splits fewer than
/// three (<c>raytrace.cpp:726</c>), so the leaf's order is the build order.
/// </para>
/// </remarks>
public sealed class KdRayTracerLeafOrderTests
{
    private static TracedTriangle Floor(int id, float z) =>
        new(id, new Vec3(-10, -10, z), new Vec3(10, -10, z), new Vec3(0, 10, z), 0);

    private static HitId Down(KdRayTracer tracer)
    {
        Ray ray = new(0.5f, 0.25f, 20f, 0f, 0f, -1f, 100f);
        HitId[] hits = new HitId[4];
        tracer.TraceClosest([ray, ray, ray, ray], hits, RayTraceOptions.StockExact);
        return hits[0];
    }

    /// <summary>Two coincident triangles: the first listed wins the tie.</summary>
    /// <remarks>Red when the pair's merge keeps the second on an equal distance.</remarks>
    [Fact]
    public void OfTwoTrianglesAtTheSameDistanceTheFirstListedWins() =>
        Assert.Equal(7, Down(KdRayTracer.Build([Floor(7, 0f), Floor(9, 0f)])).Surface);

    /// <summary>The same two in the other order: the other one wins.</summary>
    [Fact]
    public void TheTieFollowsTheListOrder() =>
        Assert.Equal(9, Down(KdRayTracer.Build([Floor(9, 0f), Floor(7, 0f)])).Surface);

    /// <summary>A nearer second triangle beats a farther first one.</summary>
    [Fact]
    public void ANearerSecondTriangleWins() =>
        Assert.Equal(9, Down(KdRayTracer.Build([Floor(7, 0f), Floor(9, 5f)])).Surface);

    /// <summary>A farther second triangle does not displace a nearer first one.</summary>
    [Fact]
    public void AFartherSecondTriangleLoses() =>
        Assert.Equal(7, Down(KdRayTracer.Build([Floor(7, 5f), Floor(9, 0f)])).Surface);

    /// <summary>The winner's distance is its own: 15 of the ray's 100 for the floor at 5.</summary>
    [Fact]
    public void TheWinnersDistanceIsReported() =>
        Assert.Equal(0.15f, Down(KdRayTracer.Build([Floor(7, 0f), Floor(9, 5f)])).Fraction);
}
