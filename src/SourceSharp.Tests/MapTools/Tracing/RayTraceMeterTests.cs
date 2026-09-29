//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="RayTraceMeter"/> and <see cref="MeteredRayTracer"/>: every batch
/// counted once, on the way it really went, by what it asked; the GPU and CPU
/// counts add up to every ray the tracers were handed; and the answers are the
/// inner tracer's, untouched.
/// </summary>
public sealed class RayTraceMeterTests
{
    private const int PropId = TraceId.StaticProp | 1;

    private static readonly KdRayTracer Cpu = KdRayTracer.Build(
    [
        new TracedTriangle(PropId, new Vec3(-100, -100, 0), new Vec3(100, -100, 0), new Vec3(0, 100, 0), 0),
    ]);

    private static Ray[] Rays(int n)
    {
        Ray[] rays = new Ray[n];
        for (int i = 0; i < n; i++)
        {
            // Alternately through the triangle and beside it.
            float x = (i & 1) == 0 ? 0 : 500;
            rays[i] = Ray.Segment(new Vec3(x, 0, 10), new Vec3(x, 0, -10), false);
        }

        return rays;
    }

    [Fact]
    public async Task ACpuTracerCountsEveryBatchAsCpuByKindAndAnswersUnchanged()
    {
        ScrambledRayTracer inner = new(Cpu, asynchronous: false);
        RayTraceMeter meter = new();
        MeteredRayTracer metered = new(inner, meter);
        ulong[] bits = new ulong[2];
        ulong[] sky = new ulong[1];
        HitId[] hits = new HitId[20];

        await metered.TraceVisibilityAsync(Rays(100), bits, RayTraceOptions.StockExact);
        await metered.TraceVisibilityAsync(Rays(30), sky, RayTraceOptions.TestLine(skyDoesNotBlock: true));
        await metered.TraceClosestAsync(Rays(20), hits, RayTraceOptions.StockExact);

        Assert.Equal(new RayRouteCounts(100, 20, 30, 3), meter.Route(gpu: false));
        Assert.Equal(new RayRouteCounts(0, 0, 0, 0), meter.Route(gpu: true));
        Assert.Equal(inner.TotalRays, meter.Route(false).Rays + meter.Route(true).Rays);

        // Every even ray goes through the triangle.
        Assert.Equal(0x5555555555555555UL, bits[0]);
        Assert.Equal(PropId, hits[0].Surface);
        Assert.Equal(HitId.Miss, hits[1].Surface);
        Assert.False(metered.IsGpu(RayTraceOptions.StockExact));
        Assert.Null(metered.GpuStatistics);
    }

    [Fact]
    public async Task ThroughTheHybridPlainBatchesCountAsGpuAndTheFallbackAsCpu()
    {
        ScrambledRayTracer gpu = new(new CountingRayTracer(Cpu, plainOnly: true), asynchronous: false);
        using HybridRayTracer hybrid = new(gpu, Cpu);
        RayTraceMeter meter = new();
        MeteredRayTracer metered = new(hybrid, meter);
        ulong[] bits = new ulong[4];
        HitId[] hits = new HitId[64];

        await metered.TraceVisibilityAsync(Rays(200), bits, RayTraceOptions.TestLine());
        await metered.TraceVisibilityAsync(Rays(40), bits, RayTraceOptions.TestLine(PropId));
        await metered.TraceVisibilityAsync(Rays(50), bits, RayTraceOptions.TestLine(skyDoesNotBlock: true));
        await metered.TraceClosestAsync(Rays(64), hits, RayTraceOptions.StockExact);
        await metered.TraceClosestAsync(Rays(7), hits, RayTraceOptions.TestLine(PropId));

        Assert.Equal(new RayRouteCounts(200, 64, 0, 2), meter.Route(gpu: true));
        Assert.Equal(new RayRouteCounts(40, 7, 50, 3), meter.Route(gpu: false));

        // The GPU half saw exactly what the meter says went to it, and the
        // two ways add up to every ray asked.
        Assert.Equal(gpu.TotalRays, meter.Route(true).Rays);
        Assert.Equal(200 + 40 + 50 + 64 + 7, meter.Route(true).Rays + meter.Route(false).Rays);
        Assert.Equal(hybrid.TracerIdentity, metered.TracerIdentity);
        Assert.True(metered.Supports(RayTraceOptions.TestLine(PropId)));
    }

    [Fact]
    public async Task ConcurrentBatchesFromManyThreadsAddUpExactly()
    {
        ScrambledRayTracer gpu = new(new CountingRayTracer(Cpu, plainOnly: true), seed: 3, maxDelayMs: 1);
        using HybridRayTracer hybrid = new(gpu, Cpu);
        RayTraceMeter meter = new();
        MeteredRayTracer metered = new(hybrid, meter);
        long asked = 0;

        await System.Threading.Tasks.Parallel.ForAsync(0, 400, async (i, ct) =>
        {
            int n = 1 + (i * 37 % 300);
            Interlocked.Add(ref asked, n);
            Ray[] rays = Rays(n);
            switch (i % 3)
            {
                case 0:
                    await metered.TraceVisibilityAsync(rays, new ulong[(n + 63) / 64], RayTraceOptions.StockExact, ct);
                    break;
                case 1:
                    await metered.TraceVisibilityAsync(rays, new ulong[(n + 63) / 64], RayTraceOptions.TestLine(skyDoesNotBlock: true), ct);
                    break;
                default:
                    await metered.TraceClosestAsync(rays, new HitId[n], RayTraceOptions.StockExact, ct);
                    break;
            }
        });

        RayRouteCounts onGpu = meter.Route(true);
        RayRouteCounts onCpu = meter.Route(false);
        Assert.Equal(asked, onGpu.Rays + onCpu.Rays);
        Assert.Equal(gpu.TotalRays, onGpu.Rays);
        Assert.Equal(400, onGpu.Batches + onCpu.Batches);
        Assert.Equal(0, onGpu.Sky);
        Assert.Equal(0, onCpu.Visibility + onCpu.Closest);
    }

    [Fact]
    public async Task ATracerThatReportsGpuStatisticsIsCountedAsTheGpuAndItsStatisticsForwarded()
    {
        GpuStatisticsTracer gpu = new(Cpu);
        RayTraceMeter meter = new();
        MeteredRayTracer direct = new(gpu, meter);
        await direct.TraceClosestAsync(Rays(5), new HitId[5], RayTraceOptions.StockExact);

        Assert.Equal(new RayRouteCounts(0, 5, 0, 1), meter.Route(gpu: true));
        Assert.Equal(GpuStatisticsTracer.Statistics, direct.GpuStatistics);

        // Behind the hybrid, the GPU half's statistics.
        using HybridRayTracer hybrid = new(gpu, Cpu);
        Assert.Equal(GpuStatisticsTracer.Statistics, new MeteredRayTracer(hybrid, new RayTraceMeter()).GpuStatistics);
        Assert.Null(new MeteredRayTracer(new HybridRayTracer(new CountingRayTracer(Cpu), Cpu), new RayTraceMeter()).GpuStatistics);
    }

    [Fact]
    public void OfFindsTheMeterOnlyBehindAMeteredTracer()
    {
        RayTraceMeter meter = new();
        Assert.Same(meter, RayTraceMeter.Of(new MeteredRayTracer(Cpu, meter)));
        Assert.Null(RayTraceMeter.Of(Cpu));
        Assert.Throws<ArgumentNullException>(() => new MeteredRayTracer(null!, meter));
        Assert.Throws<ArgumentNullException>(() => new MeteredRayTracer(Cpu, null!));
    }

    [Fact]
    public void ParkedTimeAddsUpPerStageAndIgnoresNothingSpans()
    {
        RayTraceMeter meter = new();
        long second = Stopwatch.Frequency;
        meter.AddParked(TraceWaitStage.Facelights, second);
        meter.AddParked(TraceWaitStage.Facelights, second / 2);
        meter.AddParked(TraceWaitStage.Other, second / 4);
        meter.AddParked(TraceWaitStage.Bounce, 0);
        meter.AddParked(TraceWaitStage.Bounce, -5);

        Assert.Equal(1.5, meter.Parked(TraceWaitStage.Facelights).TotalSeconds, 6);
        Assert.Equal(0.25, meter.Parked(TraceWaitStage.Other).TotalSeconds, 6);
        Assert.Equal(TimeSpan.Zero, meter.Parked(TraceWaitStage.Bounce));

        RayTraceReport report = meter.Report(new MeteredRayTracer(Cpu, meter), GpuTraceStatus.Off, "ignored");
        Assert.Equal(1.75, report.TotalParked.TotalSeconds, 6);
        Assert.Equal(1.5, report.ParkedIn(TraceWaitStage.Facelights).TotalSeconds, 6);
        Assert.Null(report.GpuDeclineReason);
        Assert.Equal(TimeSpan.Zero, (report with { Parked = [] }).ParkedIn(TraceWaitStage.Other));
    }

    [Fact]
    public async Task TheReportCarriesTheDeclineReasonOnlyWhenDeclinedAndTheCountsAsTheyStand()
    {
        RayTraceMeter meter = new();
        MeteredRayTracer metered = new(Cpu, meter);
        await metered.TraceVisibilityAsync(Rays(10), new ulong[1], RayTraceOptions.StockExact);

        RayTraceReport declined = meter.Report(metered, GpuTraceStatus.Declined, "no device");
        Assert.Equal("no device", declined.GpuDeclineReason);
        Assert.Equal(GpuTraceStatus.Declined, declined.Gpu);
        Assert.Equal(10, declined.TotalRays);
        Assert.Equal(Cpu.TracerIdentity, declined.TracerIdentity);
        Assert.Null(declined.Device);
        Assert.Null(meter.Report(metered, GpuTraceStatus.On, "no device").GpuDeclineReason);
        Assert.Throws<ArgumentNullException>(() => meter.Report(null!, GpuTraceStatus.Off, null));
    }

    /// <summary>A CPU-answered tracer that says it is a device, with fixed statistics.</summary>
    private sealed class GpuStatisticsTracer(IRayTracer inner) : IRayTracer, IGpuTraceStatistics
    {
        public static readonly GpuTraceStatistics Statistics =
            new(7, 3, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(0.25), 2, 3);

        public GpuTraceStatistics GpuStatistics => Statistics;

        public string TracerIdentity => "gpu-stats";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceClosestAsync(rays, hits, options, cancellationToken);
    }
}
