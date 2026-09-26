//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapFormats;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The 4a2 throughput gate: per-ray, single-threaded, on the same rays stock
/// was measured on.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE ASSERTION IS A FLOOR AND THE COMPARISON IS IN THE OUTPUT. The
/// number that matters -- "faster than stock per ray" -- is a ratio between
/// two binaries on one box, and stock's half cannot run inside this process.
/// Baking the box's stock figure in as a threshold would make this fact fail
/// on a slower machine for reasons that have nothing to do with the tracer,
/// which is the sort of check that gets disabled rather than fixed. So the
/// floor is set where only a catastrophe crosses it, the measurement is
/// printed with its ray count, and the ratio against stock is recorded in
/// <c>Fixtures/README-bsp-rays.md</c> with the commands that produced both
/// halves.
/// </para>
/// <para>
/// WHAT MAKES IT MUTATION-PROVABLE ANYWAY is the pair of structural facts
/// below. The speed comes from three decisions -- the plane inlined into the
/// node, the per-face data gathered into one record, the candidate lists
/// prefiltered -- and each of those leaves a fingerprint in the data that a
/// deterministic fact can pin. Undoing one to make the tracer slower breaks a
/// fact, on any machine, without a stopwatch.
/// </para>
/// <para>
/// Point the <c>SS_TRACE_RAY_SET</c> environment variable at a bigger ray
/// file, as the README's commands do, to measure the 324,000-ray set the
/// reported figure came from instead of the 8,100-ray fixture.
/// </para>
/// </remarks>
public sealed class BspSurfaceThroughputTests : IClassFixture<BspParityFixture>
{
    /// <summary>
    /// The floor, in millions of rays per second, single-threaded.
    /// </summary>
    /// <remarks>
    /// One tenth of what this tracer does on the box it was written on, and a
    /// quarter of what a prototype managed BVH did on the same rays. A machine
    /// four times slower than that box still clears it; a tracer that has lost
    /// its structure does not.
    /// </remarks>
    private const double FloorMraysPerSecond = 0.5;

    private readonly BspParityFixture _fixture;
    private readonly ITestOutputHelper _output;

    /// <summary>Takes the shared tracer and the runner's output sink.</summary>
    /// <param name="fixture">The shared fixture.</param>
    /// <param name="output">Where the measurement is written.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public BspSurfaceThroughputTests(BspParityFixture fixture, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(output);
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// Measures closest-hit throughput on one thread and reports it.
    /// </summary>
    [Fact]
    public void ClosestHitThroughputIsReportedAndClearsTheFloor()
    {
        (Ray[] rays, string source) = BenchmarkRays();
        HitId[] hits = new HitId[rays.Length];

        // Warm the JIT and the caches. The first pass through a fresh tracer
        // measures tiered compilation, not traversal, and the difference is
        // large enough to decide a comparison on its own: stock's own first
        // rep in the oracle run was 3.43 Mray/s against 4.49 warm.
        _fixture.Tracer.TraceClosest(rays, hits, RayTraceOptions.StockExact);
        _fixture.Tracer.TraceClosest(rays, hits, RayTraceOptions.StockExact);

        double best = double.MaxValue;
        for (int rep = 0; rep < 7; rep++)
        {
            long start = Stopwatch.GetTimestamp();
            _fixture.Tracer.TraceClosest(rays, hits, RayTraceOptions.StockExact);
            double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            best = Math.Min(best, seconds);
        }

        double mraysPerSecond = rays.Length / best / 1.0e6;
        _output.WriteLine(
            $"BSP surface tracer, closest hit, single thread: {mraysPerSecond:F4} Mray/s "
            + $"({rays.Length} rays, best of 7, {best * 1000.0:F3} ms) from {source}");

        Assert.True(
            mraysPerSecond > FloorMraysPerSecond,
            $"{mraysPerSecond:F4} Mray/s is below the {FloorMraysPerSecond} Mray/s floor");
    }

    /// <summary>
    /// Visibility throughput, reported alongside closest hit.
    /// </summary>
    /// <remarks>
    /// The two should be within noise of each other, because the visibility
    /// answer comes off the same closest-hit walk: this walk already stops at
    /// the first hit in front-to-back order, so there is no cheaper any-hit
    /// variant to write. A large gap here would mean one of them had grown
    /// work the other has not.
    /// </remarks>
    [Fact]
    public void VisibilityThroughputIsReportedAndClearsTheFloor()
    {
        (Ray[] rays, string source) = BenchmarkRays();
        ulong[] bits = new ulong[(rays.Length + 63) / 64];

        _fixture.Tracer.TraceVisibility(rays, bits, RayTraceOptions.StockExact);
        _fixture.Tracer.TraceVisibility(rays, bits, RayTraceOptions.StockExact);

        double best = double.MaxValue;
        for (int rep = 0; rep < 7; rep++)
        {
            long start = Stopwatch.GetTimestamp();
            _fixture.Tracer.TraceVisibility(rays, bits, RayTraceOptions.StockExact);
            double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            best = Math.Min(best, seconds);
        }

        double mraysPerSecond = rays.Length / best / 1.0e6;
        _output.WriteLine(
            $"BSP surface tracer, visibility, single thread: {mraysPerSecond:F4} Mray/s "
            + $"({rays.Length} rays, best of 7) from {source}");

        Assert.True(
            mraysPerSecond > FloorMraysPerSecond,
            $"{mraysPerSecond:F4} Mray/s is below the {FloorMraysPerSecond} Mray/s floor");
    }

    /// <summary>
    /// The node carries its own plane, which is what removes stock's
    /// dependent <c>dplanes[planenum]</c> load per node.
    /// </summary>
    /// <remarks>
    /// The fingerprint is the size: 32 bytes is four floats of plane, two
    /// children, a face offset and two counts. Putting the plane back behind
    /// an index would shrink it, and this fact would say so.
    /// </remarks>
    [Fact]
    public void ANodeIsThirtyTwoBytesWithItsPlaneInside()
    {
        Assert.Equal(32, _fixture.Geometry.NodeStrideBytes);
    }

    /// <summary>
    /// A candidate's whole test fits in one 64-byte record, which is what
    /// removes stock's walk through faces, planes and texinfo per candidate.
    /// </summary>
    [Fact]
    public void ASurfaceRecordIsOneCacheLine()
    {
        Assert.Equal(64, _fixture.Geometry.SurfaceStrideBytes);
    }

    /// <summary>
    /// The leaf candidate lists are PREFILTERED, so the walk does not re-test
    /// <c>dispinfo</c> and <c>onNode</c> per candidate per ray.
    /// </summary>
    /// <remarks>
    /// Stock walks <c>dleaffaces</c> and skips displacements and on-node faces
    /// inside the ray loop. If that filtering moved back into the walk, the
    /// prefiltered list would equal the raw one and this fact would fail --
    /// which is the point, because the equality is what makes the walk slower
    /// without changing a single answer.
    /// </remarks>
    [Fact]
    public void LeafCandidateListsAreShorterThanTheRawLeafFaceLump()
    {
        int raw = BspStructView.As<ushort>(_fixture.Bsp[BspLump.LeafFaces]).Length;
        int filtered = _fixture.Geometry.LeafCandidateCount;

        Assert.True(
            filtered < raw,
            $"{filtered} prefiltered leaf candidates against {raw} raw leaffaces: the filtering "
            + "has moved back into the per-ray walk");
    }

    /// <summary>
    /// Sky windings are built once at load, not per sky candidate per ray.
    /// </summary>
    /// <remarks>
    /// Stock calls <c>WindingFromFace</c> -- which allocates, walks surfedges
    /// and runs <c>RemoveColinearPoints</c> -- and then <c>FreeWinding</c>,
    /// inside the ray loop, every time a sky face is tested. A non-empty
    /// precomputed point pool is the evidence that this port does not.
    /// </remarks>
    [Fact]
    public void SkyWindingsArePrecomputed()
    {
        Assert.True(
            _fixture.Geometry.SkyWindingPointCount > 0,
            "dm_lockdown has sky faces, so the precomputed winding pool cannot be empty");
    }

    private static (Ray[] Rays, string Source) BenchmarkRays()
    {
        string? overridePath = Environment.GetEnvironmentVariable("SS_TRACE_RAY_SET");
        if (!string.IsNullOrEmpty(overridePath))
        {
            // The answers file next to it is not read here; only the rays are
            // needed to time a trace, and requiring the pair would stop anyone
            // timing a set they have not oracled.
            byte[] bytes = File.ReadAllBytes(overridePath);
            int n = BitConverter.ToInt32(bytes, 0);
            Ray[] rays = new Ray[n];
            for (int i = 0; i < n; i++)
            {
                int o = 4 + (i * 24);
                float sx = BitConverter.ToSingle(bytes, o);
                float sy = BitConverter.ToSingle(bytes, o + 4);
                float sz = BitConverter.ToSingle(bytes, o + 8);
                rays[i] = new Ray(
                    sx,
                    sy,
                    sz,
                    BitConverter.ToSingle(bytes, o + 12) - sx,
                    BitConverter.ToSingle(bytes, o + 16) - sy,
                    BitConverter.ToSingle(bytes, o + 20) - sz,
                    1.0f);
            }

            return (rays, Path.GetFileName(overridePath));
        }

        StockRaySet set = StockRaySet.Load();
        return (set.Rays, "the committed 8,100-ray fixture");
    }
}
