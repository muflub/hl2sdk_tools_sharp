using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// Where a ray ENDS, for the two operations of the KD tracer.
/// </summary>
/// <remarks>
/// <para>
/// Stock's <c>Trace4Rays</c> does not clip a hit to <c>TMax</c>: the clip is
/// commented out, so a triangle sitting in the same
/// KD leaf as the segment's end is reported even when it lies beyond it. Every
/// visibility caller in vrad therefore tests the distance itself --
/// <c>TestLine</c> keeps a hit only when <c>HitDistance &lt; len</c>
/// and <c>CTransferMaker::Finish</c> makes a transfer
/// when <c>HitID == -1 || HitDistance &gt;= ray_length</c>.
/// </para>
/// <para>
/// The seam's visibility operation answers "does anything block this
/// SEGMENT", so the distance test belongs inside it. The closest-hit operation
/// keeps stock's raw answer, a fraction that can exceed 1.
/// </para>
/// </remarks>
public sealed class KdRayTracerSegmentTests
{
    /// <summary>
    /// One quad at z = <paramref name="z"/>, as two triangles, and a second
    /// one behind every ray's origin at z = -5.
    /// </summary>
    /// <remarks>
    /// The second quad is there so the scene's bounds contain the rays'
    /// starts: with the far quad alone, the entry clip against the scene box
    /// already rejects a segment that stops short
    /// of it, and the defect never reaches the leaf test.
    /// </remarks>
    private static KdRayTracer QuadAt(float z) =>
        KdRayTracer.Build(
        [
            new TracedTriangle(7, new Vec3(-10, -10, z), new Vec3(10, -10, z), new Vec3(10, 10, z), 0),
            new TracedTriangle(7, new Vec3(-10, -10, z), new Vec3(10, 10, z), new Vec3(-10, 10, z), 0),
            new TracedTriangle(3, new Vec3(-10, -10, -5), new Vec3(10, -10, -5), new Vec3(10, 10, -5), 0),
            new TracedTriangle(3, new Vec3(-10, -10, -5), new Vec3(10, 10, -5), new Vec3(-10, 10, -5), 0),
        ]);

    private static bool Blocked(KdRayTracer tracer, Ray ray)
    {
        ulong[] bits = new ulong[1];
        tracer.TraceVisibility([ray], bits, RayTraceOptions.StockExact);
        return (bits[0] & 1UL) != 0;
    }

    /// <summary>
    /// A surface past the end of the segment does not block it.
    /// This was red before the
    /// fix: the bit was set for any hit on the infinite line's first KD leaf.
    /// </summary>
    [Fact]
    public void ASurfaceBeyondTheSegmentsEndDoesNotBlockIt()
    {
        KdRayTracer tracer = QuadAt(15f);
        Assert.False(Blocked(tracer, new Ray(0.5f, 0.25f, 0f, 0f, 0f, 10f, 1.0f)));
    }

    /// <summary>A surface inside the segment blocks it.</summary>
    [Fact]
    public void ASurfaceInsideTheSegmentBlocksIt()
    {
        KdRayTracer tracer = QuadAt(5f);
        Assert.True(Blocked(tracer, new Ray(0.5f, 0.25f, 0f, 0f, 0f, 10f, 1.0f)));
    }

    /// <summary>
    /// The segment's end is <c>Origin + MaxDistance * Direction</c>, not
    /// <c>Origin + Direction</c>: a unit direction with a reach of 10 stops
    /// short of a surface at 15.
    /// </summary>
    [Fact]
    public void MaxDistanceScalesTheDirection()
    {
        KdRayTracer tracer = QuadAt(15f);
        Assert.False(Blocked(tracer, new Ray(0.5f, 0.25f, 0f, 0f, 0f, 1f, 10.0f)));
        Assert.True(Blocked(tracer, new Ray(0.5f, 0.25f, 0f, 0f, 0f, 1f, 20.0f)));
    }

    /// <summary>
    /// A hit exactly AT the segment's end does not block it: stock's test is
    /// strict(<c>HitDistance &lt; len</c>), and
    /// makes the transfer at <c>&gt;=</c>.
    /// </summary>
    [Fact]
    public void AHitExactlyAtTheEndDoesNotBlock()
    {
        KdRayTracer tracer = QuadAt(8f);
        Assert.False(Blocked(tracer, new Ray(0.5f, 0.25f, 0f, 0f, 0f, 8f, 1.0f)));
    }

    /// <summary>
    /// The closest-hit operation keeps stock's unclipped answer: a fraction
    /// above 1 names a surface beyond the segment (documented on
    /// <see cref="HitId.Fraction"/>).
    /// </summary>
    [Fact]
    public void ClosestHitReportsAFractionAboveOneBeyondTheEnd()
    {
        KdRayTracer tracer = QuadAt(15f);
        HitId[] hits = new HitId[1];
        tracer.TraceClosest([new Ray(0.5f, 0.25f, 0f, 0f, 0f, 10f, 1.0f)], hits, RayTraceOptions.StockExact);
        Assert.True(hits[0].IsHit);
        Assert.Equal(1.5f, hits[0].Fraction, 5);
    }

    /// <summary>
    /// Batches keep their per-ray answers: a blocked ray, a clear one and one
    /// stopping short, packed into one packet.
    /// </summary>
    [Fact]
    public void EachRayOfAPacketIsClippedToItsOwnEnd()
    {
        KdRayTracer tracer = QuadAt(15f);
        Ray[] rays =
        [
            new(0.5f, 0.25f, 0f, 0f, 0f, 20f, 1.0f),
            new(0.5f, 0.25f, 0f, 0f, 0f, 10f, 1.0f),
            new(-0.5f, 0.25f, 0f, 0f, 0f, 16f, 1.0f),
            new(0.5f, -0.25f, 0f, 0f, 0f, 14f, 1.0f),
        ];
        ulong[] bits = new ulong[1];
        tracer.TraceVisibility(rays, bits, RayTraceOptions.StockExact);
        Assert.Equal(0b0101UL, bits[0]);
    }
}
