using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// A face's lighting must not depend on the rays laid out before its own.
/// </summary>
/// <remarks>
/// The tracer answers four consecutive rays as one packet, and stock's
/// <c>Trace4Rays</c> -- which <see cref="KdRayTracer"/> ports -- can answer
/// one ray differently depending on its three packet-mates (which KD cells
/// the packet walks decides which of two equally distant hits is found first;
/// see <c>KdRayTracer.TestLines</c>). The tracer here makes that dependence
/// total: it answers by a ray's position in its packet. Lit through the face
/// driver or lit alone, a face must come out the same.
/// </remarks>
public sealed class FaceLightPacketTests
{
    [Fact]
    public async Task AFaceIsLitTheSameAloneAsAmongTheOtherFaces()
    {
        LightTestMap map = SpotAndOccluderBox();
        PacketLaneTracer tracer = new();
        CompileParallelism one = new() { MaxDegree = 1 };

        RadWorld together = await RadWorld.StartAsync(
            map.Build(), LightBox.Settings(), new TextureLightTable(new(), "box"), tracer, one, CancellationToken.None);
        await together.LightFacesAsync(tracer, one, CancellationToken.None);

        RadWorld alone = await RadWorld.StartAsync(
            map.Build(), LightBox.Settings(), new TextureLightTable(new(), "box"), tracer, one, CancellationToken.None);
        FaceLightContext context = new(
            alone.Geometry, alone.Neighbours, alone.Patches, alone.Tree, alone.Settings, alone.Gatherer, alone.Displacements);

        int compared = 0;
        for (int f = 0; f < together.FaceLights.Length; f++)
        {
            FaceLight? expected = together.FaceLights[f];
            FaceLightJob job = new(context, f);
            job.Prepare(new WindingArena { Compliance = alone.Settings.Compliance });
            while (!job.Done)
            {
                job.RunRound();
                LightRayLog rays = job.Rays;
                ulong[] bits = new ulong[(rays.VisibilityCount + 63) / 64];
                HitId[] hits = new HitId[rays.SkyCount];
                await tracer.TraceVisibilityAsync(rays.VisibilityRays().ToArray(), bits, RayTraceOptions.StockExact);
                await tracer.TraceClosestAsync(rays.SkyRays().ToArray(), hits, RayTraceOptions.StockExact);
                rays.BeginReplay(bits, 0, hits, 0);
                job.RunRound();
            }

            if (expected is null)
            {
                Assert.Null(job.Result);
                continue;
            }

            for (int k = 0; k < expected.Light.Length; k++)
            {
                Assert.Equal(expected.Light[k], job.Result!.Light[k]);
            }

            compared++;
        }

        Assert.True(compared > 1);
    }

    // A lit box with an occluder and a narrow spot: groups on the spot's
    // cone edge trace only some of their lanes, so faces' ray counts are not
    // whole packets.
    private static LightTestMap SpotAndOccluderBox()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200")));
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_spot"), ("origin", "60 60 200"), ("angles", "-90 0 0"),
            ("_light", "255 128 64 300"), ("_inner_cone", "10"), ("_cone", "17")));
        map.AddOccluder(new(96, 96, 64), new(160, 96, 64), new(160, 160, 64), new(96, 160, 64));
        return map;
    }

    private static async Task<FaceLight?[]> LightAsync(
        LightTestMap map, IRayTracer tracer, int threads, int batchRays)
    {
        CompileParallelism parallelism = new() { MaxDegree = threads };
        RadWorld world = await RadWorld.StartAsync(
            map.Build(), LightBox.Settings(), new TextureLightTable(new(), "box"), tracer, parallelism, CancellationToken.None);
        world.FacelightBatchRays = batchRays;
        await world.LightFacesAsync(tracer, parallelism, CancellationToken.None);
        return world.FaceLights;
    }

    private static void AssertSameLight(FaceLight?[] expected, FaceLight?[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int f = 0; f < expected.Length; f++)
        {
            if (expected[f] is null)
            {
                Assert.Null(actual[f]);
                continue;
            }

            Assert.Equal(expected[f]!.Styles, actual[f]!.Styles);
            for (int k = 0; k < expected[f]!.Light.Length; k++)
            {
                Assert.Equal(expected[f]!.Light[k], actual[f]!.Light[k]);
            }
        }
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(4, 50)]
    [InlineData(3, 1000)]
    public async Task TheBatchSizeAndThreadCountDoNotChangeTheLight(int threads, int batchRays)
    {
        // The face driver hands each worker's batch to the tracer when it
        // holds batchRays rays, splitting faces and rounds across batches;
        // with a tracer that answers by packet lane, only packet alignment
        // keeps the answers the same.
        LightTestMap map = SpotAndOccluderBox();
        PacketLaneTracer tracer = new();
        FaceLight?[] expected = await LightAsync(map, tracer, 1, RadWorld.RaysPerBatch);
        AssertSameLight(expected, await LightAsync(map, tracer, threads, batchRays));
    }

    [Fact]
    public async Task TheBatchSizeDoesNotChangeTheLightThroughTheRealTracer()
    {
        LightTestMap map = SpotAndOccluderBox();
        IRayTracer tracer = map.Tracer();
        FaceLight?[] expected = await LightAsync(map, tracer, 1, RadWorld.RaysPerBatch);
        AssertSameLight(expected, await LightAsync(map, tracer, 4, 8));
    }

    [Fact]
    public async Task AnAsynchronousTracerLightsTheSameAsASynchronousOne()
    {
        // A worker whose slab is still in flight parks; the driver awaits it
        // outside the workers and runs them again.
        LightTestMap map = SpotAndOccluderBox();
        FaceLight?[] expected = await LightAsync(map, new PacketLaneTracer(), 2, 40);
        AssertSameLight(expected, await LightAsync(map, new YieldingTracer(new PacketLaneTracer()), 2, 40));
    }

    [Fact]
    public async Task APreCancelledTokenLightsNothing()
    {
        LightTestMap map = SpotAndOccluderBox();
        IRayTracer tracer = map.Tracer();
        CompileParallelism one = new() { MaxDegree = 1 };
        RadWorld world = await RadWorld.StartAsync(
            map.Build(), LightBox.Settings(), new TextureLightTable(new(), "box"), tracer, one, CancellationToken.None);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.LightFacesAsync(tracer, one, cts.Token));
        Assert.Empty(world.FaceLights);
    }

    /// <summary>A tracer that answers after yielding: every slab comes back incomplete.</summary>
    private sealed class YieldingTracer(IRayTracer inner) : IRayTracer
    {
        public string TracerIdentity => "yielding";

        public async ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
        }

        public async ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await inner.TraceClosestAsync(rays, hits, options, cancellationToken);
        }
    }

    /// <summary>
    /// A tracer whose answer is the ray's lane in its packet of four: lane 1
    /// is blocked (and hits something opaque), every other lane is clear.
    /// </summary>
    private sealed class PacketLaneTracer : IRayTracer
    {
        public string TracerIdentity => "packet-lane";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            Span<ulong> bits = hitBits.Span;
            bits.Clear();
            for (int i = 0; i < rays.Length; i++)
            {
                if ((i & 3) == 1)
                {
                    bits[i >> 6] |= 1UL << (i & 63);
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            Span<HitId> answers = hits.Span;
            for (int i = 0; i < rays.Length; i++)
            {
                answers[i] = (i & 3) == 1 ? new HitId(TraceId.Opaque, 0.5f) : HitId.Missed;
            }

            return ValueTask.CompletedTask;
        }
    }
}
