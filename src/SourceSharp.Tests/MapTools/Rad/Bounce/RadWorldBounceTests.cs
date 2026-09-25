using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary><see cref="RadWorld.BounceAsync"/>: the bounce half of <c>RadWorld_Go</c>.</summary>
public sealed class RadWorldBounceTests
{
    /// <summary>The bounce needs the patches' direct light, so it refuses to run before the faces are lit.</summary>
    [Fact]
    public async Task BouncingBeforeLightingThrows()
    {
        LightTestMap map = BounceBox.Map();
        RadWorld world = BounceBox.Build(map);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.BounceAsync(map.Tracer(), BounceBox.One, CancellationToken.None));
    }

    /// <summary>A second bounce would bounce nothing -- the direct light was moved out -- so it throws.</summary>
    [Fact]
    public async Task BouncingTwiceThrows()
    {
        LightTestMap map = BounceBox.Map();
        RadWorld world = await BounceBox.BouncedAsync(map);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.BounceAsync(map.Tracer(), BounceBox.One, CancellationToken.None));
    }

    /// <summary><c>-bounce 0</c> does nothing.</summary>
    [Fact]
    public async Task ZeroBouncesLeavesTheWorldUnbounced()
    {
        RadWorld world = await BounceBox.BouncedAsync(
            BounceBox.Map(), LightBox.Settings() with { Bounces = 0 });
        Assert.False(world.IsBounced);
        Assert.Null(world.Transfers);
    }

    /// <summary>A map with no vis is direct-only: nothing to bounce.</summary>
    [Fact]
    public async Task AMapWithNoVisIsNotBounced()
    {
        LightTestMap map = BounceBox.Map();
        map.Visibility = null;
        RadWorld world = await BounceBox.BouncedAsync(map);
        Assert.False(world.IsBounced);
    }

    /// <summary>A lit box bounces: transfers exist and the first bounce adds light.</summary>
    [Fact]
    public async Task ALitBoxBounces()
    {
        RadWorld world = await BounceBox.BouncedAsync(BounceBox.Map());
        Assert.True(world.IsBounced);
        Assert.True(world.Transfers!.Total > 0);
        Assert.True(world.BounceEnergies[0].X > 1);
    }

    /// <summary>Each bounce adds less than the one before in a closed grey box.</summary>
    [Fact]
    public async Task EachBounceAddsLessThanTheLast()
    {
        RadWorld world = await BounceBox.BouncedAsync(BounceBox.Map());
        for (int i = 1; i < world.BounceEnergies.Count; i++)
        {
            Assert.True(world.BounceEnergies[i].X < world.BounceEnergies[i - 1].X);
        }
    }

    /// <summary>The floor's leaf patches end with bounced light in their totals.</summary>
    [Fact]
    public async Task TheFloorEndsWithBouncedLight()
    {
        RadWorld world = await BounceBox.BouncedAsync(BounceBox.Map());
        Assert.All(BounceBox.Leaves(world, 0), p => Assert.True(world.Patches.At(p).TotalLight.Flat.X > 0));
    }

    /// <summary>The bounce does not touch a patch's direct light.</summary>
    [Fact]
    public async Task TheDirectLightIsUntouched()
    {
        LightTestMap map = BounceBox.Map();
        RadWorld lit = await BounceBox.LitAsync(map);
        Vec3[] before = [.. Enumerable.Range(0, lit.Patches.Count).Select(p => lit.Patches.At(p).DirectLight)];
        await lit.BounceAsync(map.Tracer(), BounceBox.One, CancellationToken.None);
        Assert.Equal(before, Enumerable.Range(0, lit.Patches.Count).Select(p => lit.Patches.At(p).DirectLight));
    }

    /// <summary>
    /// One worker and four give the same transfers, totals and energies, byte
    /// for byte -- the patch light AddSampleToPatch handed over included.
    /// </summary>
    [Fact]
    public async Task OneWorkerAndFourAreByteIdentical()
    {
        LightTestMap map = BounceBox.Map();
        map.AddOccluder(new(96, 96, 64), new(160, 96, 64), new(160, 160, 64), new(96, 160, 64));
        RadWorld one = await BounceBox.BouncedAsync(map, parallelism: BounceBox.One);
        RadWorld four = await BounceBox.BouncedAsync(map, parallelism: new CompileParallelism { MaxDegree = 4 });
        Assert.Equal(RadWorldBounceGateTests.Hash(one), RadWorldBounceGateTests.Hash(four));
    }

    /// <summary>A pre-cancelled token does no work.</summary>
    [Fact]
    public async Task APreCancelledTokenDoesNoWork()
    {
        LightTestMap map = BounceBox.Map();
        RadWorld world = await BounceBox.LitAsync(map);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => world.BounceAsync(map.Tracer(), BounceBox.One, cts.Token));
        Assert.False(world.IsBounced);
    }

    /// <summary>The transfer rays go through the tracer's visibility operation, in slabs.</summary>
    [Fact]
    public async Task EveryTransferRayGoesThroughTheTracer()
    {
        LightTestMap map = BounceBox.Map();
        RadWorld world = await BounceBox.LitAsync(map);
        CountingTracer tracer = new(map.Tracer());
        await world.BounceAsync(tracer, BounceBox.One, CancellationToken.None);
        Assert.Equal(world.VisMatrixStatistics!.Rays, tracer.VisibilityRays);
    }

    private sealed class CountingTracer(IRayTracer inner) : IRayTracer
    {
        private long _visibility;

        public long VisibilityRays => Interlocked.Read(ref _visibility);

        public string TracerIdentity => inner.TracerIdentity;

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Add(ref _visibility, rays.Length);
            return inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceClosestAsync(rays, hits, options, cancellationToken);
    }
}
