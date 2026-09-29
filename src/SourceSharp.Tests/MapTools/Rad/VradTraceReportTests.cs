//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;

using static SourceSharp.Tests.MapTools.Compile.MapCompilerTests;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// <see cref="RadResult.Tracing"/> over a whole compile: every ray the tracers
/// were handed is counted once, on the way it went, GPU plus CPU adding up to
/// the total; a GPU that was declined or never asked for says so; and a
/// tracer whose batches come back late shows up as parked worker time in the
/// stages that waited on it.
/// </summary>
public sealed class VradTraceReportTests
{
    [Fact]
    public async Task AHostTracerIsCountedRayForRayAsTheCpu()
    {
        BspData bsp = await BspAsync();
        ScrambledRayTracer host = new(await KdAsync(bsp), asynchronous: false);
        VradContext context = await ContextAsync(null) with { Tracer = host };

        RadResult result = await Vrad.LightAsync(bsp, context);

        RayTraceReport report = Assert.IsType<RayTraceReport>(result.Tracing);
        Assert.Equal(GpuTraceStatus.Off, report.Gpu);
        Assert.Equal(host.TracerIdentity, report.TracerIdentity);
        Assert.Equal(0, report.GpuRays.Rays);
        Assert.Equal(host.VisibilityRays, report.CpuRays.Visibility);
        Assert.Equal(host.SkyRays, report.CpuRays.Sky);
        Assert.Equal(host.ClosestRays, report.CpuRays.Closest);
        Assert.Equal(host.Calls, report.CpuRays.Batches);
        Assert.Equal(host.TotalRays, report.TotalRays);
        Assert.True(report.TotalRays > 0);

        // A tracer that answers inside the call never parks a worker.
        Assert.Equal(TimeSpan.Zero, report.TotalParked);
        Assert.Null(report.Device);
    }

    [Fact]
    public async Task AnOfferedGpuCountsItsOwnRaysTheFallbackTheRestAndLateBatchesAsParkedTime()
    {
        BspData bsp = await BspAsync();
        ScrambledFactory factory = new();
        VradContext context = await ContextAsync(factory);

        RadResult result = await Vrad.LightAsync(bsp, context);

        RayTraceReport report = Assert.IsType<RayTraceReport>(result.Tracing);
        ScrambledRayTracer gpu = Assert.IsType<ScrambledRayTracer>(factory.Offered);
        Assert.Equal(GpuTraceStatus.On, report.Gpu);
        Assert.Null(report.GpuDeclineReason);
        Assert.Equal(gpu.TotalRays, report.GpuRays.Rays);
        Assert.Equal(gpu.Calls, report.GpuRays.Batches);
        Assert.True(report.GpuRays.Rays > 0);

        // The GPU stand-in answers only plain queries, so the prop and
        // leaf-ambient sky tests fall back; every one of them is a CPU ray.
        Assert.Equal(0, report.GpuRays.Sky);
        Assert.Equal(report.TotalRays, report.GpuRays.Rays + report.CpuRays.Rays);

        // Every batch came back after the call: the face lighting parked on
        // them. (The room has no vis, so its bounce is forced off; the
        // bounce's own parked time has its fact beside the visibility matrix.)
        Assert.True(report.ParkedIn(TraceWaitStage.Facelights) > TimeSpan.Zero);
        Assert.Equal(0, gpu.Outstanding);
    }

    [Fact]
    public async Task ADeclinedGpuSaysSoWithItsReasonAndEveryRayIsTheCpus()
    {
        BspData bsp = await BspAsync();
        VradContext context = await ContextAsync(new DecliningFactory());

        RadResult result = await Vrad.LightAsync(bsp, context);

        RayTraceReport report = Assert.IsType<RayTraceReport>(result.Tracing);
        Assert.Equal(GpuTraceStatus.Declined, report.Gpu);
        Assert.Equal("no device here", report.GpuDeclineReason);
        Assert.Contains(result.Diagnostics, d => d.Code == VradCodes.GpuTracerDeclined
            && d.Message == "gpu tracer declined: no device here — CPU KD tracer for this run");
        Assert.Equal(0, report.GpuRays.Rays);
        Assert.True(report.CpuRays.Rays > 0);
    }

    [Fact]
    public async Task WithNoFactoryTheGpuIsOff()
    {
        RadResult result = await Vrad.LightAsync(await BspAsync(), await ContextAsync(null));

        Assert.Equal(GpuTraceStatus.Off, result.Tracing!.Gpu);
        Assert.True(result.Tracing.CpuRays.Rays > 0);
    }

    [Fact]
    public void TheDeclineReasonIsTheBackendsOwnWordsWhateverWrapsThem()
    {
        static CompileDiagnostic Warning(string code, string message) =>
            new(code, DiagnosticSeverity.Warning, message);

        Assert.Null(Vrad.DeclineReason([Warning(VradCodes.StageWarning, "gpu tracer declined: x")]));
        Assert.Equal("x", Vrad.DeclineReason([Warning(VradCodes.GpuTracerDeclined, "gpu tracer declined: x — CPU KD tracer for this run")]));
        Assert.Equal("bare", Vrad.DeclineReason([Warning(VradCodes.GpuTracerDeclined, "bare")]));
    }

    [Fact]
    public void TheGpuStatusFollowsTheTracerAndTheFactory()
    {
        KdRayTracer kd = KdRayTracer.Build(
            [new TracedTriangle(1, new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), 0)]);
        VradContext plain = new() { Options = VradOptions.Default };
        VradContext asked = plain with { GpuTracerFactory = new DecliningFactory() };
        using HybridRayTracer hybrid = new(new CountingRayTracer(kd), kd);

        Assert.Equal(GpuTraceStatus.Off, Vrad.GpuStatusOf(plain, kd));
        Assert.Equal(GpuTraceStatus.Declined, Vrad.GpuStatusOf(asked, kd));
        Assert.Equal(GpuTraceStatus.On, Vrad.GpuStatusOf(asked, hybrid));
        Assert.Equal(GpuTraceStatus.On, Vrad.GpuStatusOf(plain, new DeviceTracer(kd)));

        // A host's own tracer is not a declined GPU, even with a factory set:
        // the factory is never asked.
        Assert.Equal(GpuTraceStatus.Off, Vrad.GpuStatusOf(asked with { Tracer = kd }, kd));
    }

    private static async Task<KdRayTracer> KdAsync(BspData bsp)
    {
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            bsp, VradOptions.Default, new ContentFileSystem([]), NullPropCollisionSource.Instance);
        return casters.Set.BuildTracer();
    }

    private static async Task<VradContext> ContextAsync(IGpuTracerFactory? factory)
    {
        (_, IContentFileSystem content) = await DiskAsync(Room());
        return new VradContext
        {
            Options = VradOptions.Default with { Bounces = 1 },
            MapName = "room",
            Content = content,
            Parallelism = new CompileParallelism { MaxDegree = 2 },
            GpuTracerFactory = factory,
        };
    }

    private static async Task<BspData> BspAsync()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        VbspContext context = new(VbspOptions.Default, content) { MapBase = "room" };
        MapFile map = await new MapFileReader(context, files).LoadAsync(VPath.Create("maps/room.vmf"));
        VbspResult vbsp = await Vbsp.CompileAsync(map, context);
        return vbsp.Bsp!;
    }

    /// <summary>Offers a late-answering tracer over the casters' KD tree that answers only plain queries.</summary>
    private sealed class ScrambledFactory : IGpuTracerFactory
    {
        public IRayTracer? Offered { get; private set; }

        public ValueTask<GpuTracerOffer> TryCreateAsync(ShadowCasterSet casters, CancellationToken cancellationToken)
        {
            Offered = new ScrambledRayTracer(new CountingRayTracer(casters.BuildTracer(), plainOnly: true), seed: 11);
            return ValueTask.FromResult(new GpuTracerOffer(Offered, null));
        }
    }

    private sealed class DecliningFactory : IGpuTracerFactory
    {
        public ValueTask<GpuTracerOffer> TryCreateAsync(ShadowCasterSet casters, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GpuTracerOffer(null, "no device here"));
    }

    /// <summary>A tracer that says it is a device, as a host's own GPU tracer would.</summary>
    private sealed class DeviceTracer(IRayTracer inner) : IRayTracer, IGpuTraceStatistics
    {
        public GpuTraceStatistics GpuStatistics => default;

        public string TracerIdentity => "device";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceClosestAsync(rays, hits, options, cancellationToken);
    }
}
