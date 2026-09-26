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
/// The KD tracer's single-thread throughput, against a catastrophe-only floor.
/// </summary>
/// <remarks>
/// In the <see cref="ThroughputCollection"/>, so nothing else runs while it
/// times: with test classes in parallel, a hosted four-core runner measured
/// 0.03 Mray/s here, against 0.22 on an idle machine.
/// </remarks>
[Collection(ThroughputCollection.Name)]
public sealed class KdThroughputTests : IClassFixture<KdParityFixture>
{
    private readonly KdParityFixture _fixture;
    private readonly ITestOutputHelper _output;

    public KdThroughputTests(KdParityFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// KD throughput, single-threaded, reported for the comparison against
    /// stock.
    /// </summary>
    /// <remarks>
    /// A floor rather than a threshold, for the reason
    /// <see cref="BspSurfaceThroughputTests"/> gives at length: the ratio
    /// against stock is a comparison of two binaries on one box and cannot be
    /// evaluated inside this process. And as there: a figure taken without
    /// <c>-c Release</c> is a measurement of the Debug JIT, not of this code.
    /// </remarks>
    [Fact]
    public void KdThroughputIsReportedAndClearsTheFloor()
    {
        Ray[] rays = _fixture.Scene.Rays;
        HitId[] hits = new HitId[rays.Length];

        _fixture.Tracer.TraceClosest(rays, hits, RayTraceOptions.StockExact);
        _fixture.Tracer.TraceClosest(rays, hits, RayTraceOptions.StockExact);

        double best = double.MaxValue;
        for (int rep = 0; rep < 9; rep++)
        {
            long start = Stopwatch.GetTimestamp();
            _fixture.Tracer.TraceClosest(rays, hits, RayTraceOptions.StockExact);
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds);
        }

        double mrays = rays.Length / best / 1.0e6;
        _output.WriteLine(
            $"KD tracer, closest hit, single thread: {mrays:F4} Mray/s ({rays.Length} rays, "
            + $"{_fixture.Tracer.TriangleCount} triangles, {_fixture.Tracer.NodeCount} nodes, "
            + $"best of 9, {best * 1000.0:F3} ms)");

        Assert.True(mrays > 0.05, $"{mrays:F4} Mray/s is below the 0.05 Mray/s floor");
    }
}
