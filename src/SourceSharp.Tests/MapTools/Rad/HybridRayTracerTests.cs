//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// <see cref="HybridRayTracer"/>'s fallback rule: a batch goes to the GPU
/// when the GPU honours its options and to the KD tracer when it does not,
/// so a skipped id or sky pass-through never depends on the device.
/// </summary>
public sealed class HybridRayTracerTests
{
    private const int PropId = TraceId.StaticProp | 1;

    private static readonly KdRayTracer Cpu = KdRayTracer.Build(
    [
        new TracedTriangle(PropId, new Vec3(-100, -100, 0), new Vec3(100, -100, 0), new Vec3(0, 100, 0), 0),
    ]);

    private static readonly Ray[] Through = [Ray.Segment(new Vec3(0, 0, 10), new Vec3(0, 0, -10), false)];

    [Fact]
    public async Task PlainBatchesGoToTheGpu()
    {
        CountingRayTracer gpu = new(Cpu, plainOnly: true);
        using HybridRayTracer hybrid = new(gpu, Cpu);
        ulong[] bits = new ulong[1];

        await hybrid.TraceVisibilityAsync(Through, bits, RayTraceOptions.TestLine());
        await hybrid.TraceClosestAsync(Through, new HitId[1], RayTraceOptions.StockExact);

        Assert.Single(gpu.VisibilityCalls);
        Assert.Equal(1, gpu.ClosestCalls);
        Assert.Equal(1UL, bits[0]);
        Assert.Same(gpu, hybrid.TracerFor(RayTraceOptions.TestLine()));
    }

    [Fact]
    public async Task ASkippedIdTheGpuCannotHonourIsAnsweredByTheCpu()
    {
        CountingRayTracer gpu = new(Cpu, plainOnly: true);
        using HybridRayTracer hybrid = new(gpu, Cpu);
        ulong[] bits = [ulong.MaxValue];
        HitId[] hits = new HitId[1];

        await hybrid.TraceVisibilityAsync(Through, bits, RayTraceOptions.TestLine(PropId));
        await hybrid.TraceClosestAsync(Through, hits, RayTraceOptions.TestLine(PropId));

        Assert.Empty(gpu.VisibilityCalls);
        Assert.Equal(0, gpu.ClosestCalls);
        Assert.Equal(0UL, bits[0]);
        Assert.Equal(HitId.Miss, hits[0].Surface);
        Assert.Same(Cpu, hybrid.TracerFor(RayTraceOptions.TestLine(PropId)));
        Assert.Same(Cpu, hybrid.TracerFor(RayTraceOptions.TestLine(skyDoesNotBlock: true)));
    }

    [Fact]
    public void AGpuThatHonoursAnOptionKeepsTheBatch()
    {
        CountingRayTracer gpu = new(Cpu);
        using HybridRayTracer hybrid = new(gpu, Cpu);

        Assert.Same(gpu, hybrid.TracerFor(RayTraceOptions.TestLine(PropId, skyDoesNotBlock: true)));
    }

    [Fact]
    public void TheHybridSupportsEveryOptionAndNamesBothHalves()
    {
        using HybridRayTracer hybrid = new(new CountingRayTracer(Cpu, plainOnly: true), Cpu);

        Assert.True(hybrid.Supports(RayTraceOptions.TestLine(PropId, skyDoesNotBlock: true)));
        Assert.Equal("counting+" + Cpu.TracerIdentity + "+kd-fallback", hybrid.TracerIdentity);
        Assert.Same(Cpu, hybrid.CpuTracer);
    }
}
