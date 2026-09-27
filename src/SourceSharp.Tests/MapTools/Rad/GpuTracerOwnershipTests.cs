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
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;

using Xunit;

using static SourceSharp.Tests.MapTools.Compile.MapCompilerTests;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// A tracer vrad got from the host's <see cref="IGpuTracerFactory"/> is the
/// compile's, and the compile releases it exactly once, however it ends: lit,
/// failed mid-lighting, failed right after the tracer was built, cancelled,
/// or prepared and never lit. A tracer the host passed in
/// <see cref="VradContext.Tracer"/> stays the host's.
/// </summary>
public sealed class GpuTracerOwnershipTests
{
    [Theory]
    [InlineData(VradLightingRange.Ldr)]
    [InlineData(VradLightingRange.Both)]
    public async Task ALitCompileReleasesTheOfferedTracerOnce(VradLightingRange range)
    {
        CountingGpuTracerFactory factory = new();
        VradContext context = await ContextAsync(factory, null) with
        {
            Options = VradOptions.Default with { Bounces = 1, Range = range },
        };

        RadResult result = await Vrad.LightAsync(await BspAsync(), context);

        Assert.NotEmpty(result.Passes);
        CountingTracer tracer = Assert.Single(factory.Offered);
        Assert.Equal(1, tracer.Disposals);
    }

    [Fact]
    public async Task TheOfferedTracerStaysOpenThroughOtherLightingAndIsReleasedAfter()
    {
        // The other-lighting stages (props, leaf ambient) trace through the
        // pass's tracer, the offered one: the release must come after them.
        CountingGpuTracerFactory factory = new();
        List<int> seen = [];
        VradContext context = await ContextAsync(
            factory,
            new ActAt(p => p.Stage == Vrad.OtherStage, () => seen.Add(Assert.Single(factory.Offered).Disposals)));

        _ = await Vrad.LightAsync(await BspAsync(), context);

        Assert.Equal([0, 0], seen);
        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
    }

    [Fact]
    public async Task AFailureMidLightingReleasesTheOfferedTracerOnce()
    {
        CountingGpuTracerFactory factory = new();
        InvalidDataException planted = new("planted failure");
        VradContext context = await ContextAsync(
            factory,
            new ActAt(p => p.Stage == Vrad.FacelightsStage && p.Done == 1, () => throw planted));

        BspData bsp = await BspAsync();

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => Vrad.LightAsync(bsp, context));

        Assert.Same(planted, thrown);
        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
    }

    [Fact]
    public async Task AFailureRightAfterTheTracerIsBuiltReleasesItOnce()
    {
        // The load's closing report runs with the tracer built but not yet in
        // a preparation: the one moment nothing else holds it.
        CountingGpuTracerFactory factory = new();
        InvalidDataException planted = new("planted failure");
        VradContext context = await ContextAsync(
            factory,
            new ActAt(p => p.Stage == Vrad.LoadStage && p.Done == 1, () => throw planted));

        BspData bsp = await BspAsync();

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => Vrad.PrepareAsync(bsp, context));

        Assert.Same(planted, thrown);
        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
    }

    [Fact]
    public async Task ACancelledCompileReleasesTheOfferedTracerOnce()
    {
        CountingGpuTracerFactory factory = new();
        using CancellationTokenSource cts = new();
        VradContext context = await ContextAsync(
            factory,
            new ActAt(p => p.Stage == Vrad.FacelightsStage && p.Done == 1, cts.Cancel));
        BspData bsp = await BspAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Vrad.LightAsync(bsp, context, cts.Token));

        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
    }

    [Fact]
    public async Task APreparationLitReleasesTheTracerOnceAndDisposingItAfterIsANoOp()
    {
        CountingGpuTracerFactory factory = new();
        VradContext context = await ContextAsync(factory, null);
        BspData bsp = await BspAsync();

        VradPreparation prepared = await Vrad.PrepareAsync(bsp, context);
        CountingTracer tracer = Assert.Single(factory.Offered);
        Assert.Equal(0, tracer.Disposals);

        _ = await Vrad.LightAsync(bsp, prepared, context);
        Assert.Equal(1, tracer.Disposals);

        prepared.Dispose();
        prepared.Dispose();
        Assert.Equal(1, tracer.Disposals);
    }

    [Fact]
    public async Task APreparationNeverLitReleasesTheTracerWhenDisposedAndCannotBeLitAfter()
    {
        CountingGpuTracerFactory factory = new();
        VradContext context = await ContextAsync(factory, null);
        BspData bsp = await BspAsync();

        VradPreparation prepared = await Vrad.PrepareAsync(bsp, context);
        prepared.Dispose();
        prepared.Dispose();

        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Vrad.LightAsync(bsp, prepared, context));
        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
    }

    [Fact]
    public async Task ATracerTheHostPassedIsNeverReleased()
    {
        BspData bsp = await BspAsync();
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            bsp, VradOptions.Default, new ContentFileSystem([]), NullPropCollisionSource.Instance);
        CountingTracer hosts = new(casters.Set.BuildTracer());
        VradContext context = await ContextAsync(null, null) with { Tracer = hosts };

        _ = await Vrad.LightAsync(bsp, context);
        VradPreparation prepared = await Vrad.PrepareAsync(await BspAsync(), context);
        prepared.Dispose();

        Assert.Equal(0, hosts.Disposals);
    }

    private static async Task<VradContext> ContextAsync(IGpuTracerFactory? factory, IProgress<SourceSharp.MapTools.Diagnostics.CompileProgress>? progress)
    {
        (_, IContentFileSystem content) = await DiskAsync(Room());
        return new VradContext
        {
            Options = VradOptions.Default with { Bounces = 1 },
            MapName = "room",
            Content = content,
            Parallelism = new CompileParallelism { MaxDegree = 2 },
            GpuTracerFactory = factory,
            Progress = progress,
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
}
