//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;

using static SourceSharp.Tests.MapTools.Compile.MapCompilerTests;

using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// The face lighting's pipelines: each worker keeps up to
/// <see cref="RadWorld.FacelightPipelineDepth"/> batches in flight on a tracer
/// that answers late, and the light is the same bytes as a tracer that
/// answers in the call, and as the CPU KD tracer, at every worker count and
/// every depth, however the answers are ordered in time. The bound holds, and
/// a cancelled or failed stage leaves nothing running and nothing rented.
/// </summary>
public sealed class FacelightPipelineTests
{
    /// <summary>
    /// A sky-lit box with a sun (spread, so several sky rays a sample), sky
    /// ambient, a point light, a narrow spot and an occluder: every kind of
    /// gather record, visibility and sky rays in the same batches, and groups
    /// on the spot's cone edge that trace only some of their lanes.
    /// </summary>
    private static LightTestMap RichBox()
    {
        LightTestMap map = LightBox.Map(ceiling: SurfaceFlags.Sky);
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_environment"), ("origin", "128 128 128"), ("pitch", "-70"), ("angles", "0 30 0"),
            ("_light", "255 240 200 300"), ("_ambient", "80 100 140 40"), ("SunSpreadAngle", "10")));
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", "60 190 100"), ("_light", "255 255 255 150")));
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_spot"), ("origin", "60 60 200"), ("angles", "-90 0 0"),
            ("_light", "255 128 64 300"), ("_inner_cone", "10"), ("_cone", "17")));
        map.AddOccluder(new(96, 96, 64), new(160, 96, 64), new(160, 160, 64), new(96, 160, 64));
        return map;
    }

    private static async Task<RadWorld> LightAsync(
        LightTestMap map,
        IRayTracer tracer,
        int threads,
        int batchRays = RadWorld.RaysPerBatch,
        int depth = RadWorld.DefaultFacelightPipelineDepth,
        IScratchArrayPool? pool = null,
        CancellationToken cancellationToken = default)
    {
        CompileParallelism parallelism = new() { MaxDegree = threads };
        RadWorld world = await RadWorld.StartAsync(
            map.Build(), LightBox.Settings(), new TextureLightTable(new(), "box"), tracer, parallelism, CancellationToken.None);
        world.FacelightBatchRays = batchRays;
        world.FacelightPipelineDepth = depth;
        world.FacelightScratchPool = pool;
        await world.LightFacesAsync(tracer, parallelism, cancellationToken);
        return world;
    }

    /// <summary>Every face's light and styles, as raw bytes, so a value that moved in its last bit fails.</summary>
    private static List<byte[]> Bytes(RadWorld world)
    {
        List<byte[]> all = [];
        foreach (FaceLight? face in world.FaceLights)
        {
            if (face is null)
            {
                all.Add([]);
                continue;
            }

            all.Add(face.Styles.ToArray());
            foreach (LightingValue[]? light in face.Light)
            {
                all.Add(light is null ? [0xFF] : MemoryMarshal.AsBytes(light.AsSpan()).ToArray());
            }
        }

        return all;
    }

    /// <summary>
    /// The heart of it: a tracer that answers every batch late and out of
    /// order gives the same bytes as the KD tracer answering in the call, at
    /// one worker and several, depth one and deep, batches of a few dozen
    /// rays (so faces and rounds span many batches in flight at once) and of
    /// the production size.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 40)]
    [InlineData(1, 4, 40)]
    [InlineData(2, 2, 40)]
    [InlineData(3, 8, 40)]
    [InlineData(4, 3, 200)]
    [InlineData(4, 1, 64)]
    [InlineData(2, 4, RadWorld.RaysPerBatch)]
    public async Task LateOutOfOrderAnswersLightTheSameBytesAsTheCpuTracer(int threads, int depth, int batchRays)
    {
        LightTestMap map = RichBox();
        IRayTracer kd = map.Tracer();
        RadWorld expected = await LightAsync(map, kd, 1);

        ScrambledRayTracer late = new(kd, seed: (threads * 100) + depth, maxDelayMs: 2);
        RadWorld actual = await LightAsync(map, late, threads, batchRays, depth);

        Assert.Equal(Bytes(expected), Bytes(actual));
        Assert.Equal(expected.Statistics.VisibilityRays, actual.Statistics.VisibilityRays);
        Assert.Equal(expected.Statistics.SkyRays, actual.Statistics.SkyRays);
        Assert.Equal(expected.Statistics.Samples, actual.Statistics.Samples);
        Assert.Equal(expected.Statistics.LightRecords, actual.Statistics.LightRecords);
        Assert.True(expected.Statistics.SkyRays > 0, "the box should trace sky rays");
        Assert.Equal(0, late.Outstanding);
        Assert.All(late.Tasks, t => Assert.True(t.IsCompletedSuccessfully));
    }

    /// <summary>
    /// The same late tracer against the same tracer answering in the call,
    /// both through the packet-lane stand-in whose answer is a ray's position
    /// in its packet: only packet alignment within each batch keeps these
    /// equal, so a pipeline that let two batches share or shift packets fails.
    /// </summary>
    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 6)]
    public async Task LateAnswersLightTheSameBytesAsASynchronousTracer(int threads, int depth)
    {
        LightTestMap map = RichBox();
        RadWorld expected = await LightAsync(map, new PacketLaneTracer(), threads, 40, depth);
        RadWorld actual = await LightAsync(map, new ScrambledRayTracer(new PacketLaneTracer(), seed: depth), threads, 40, depth);

        Assert.Equal(Bytes(expected), Bytes(actual));
    }

    /// <summary>
    /// No worker ever holds more batches traced and not yet resolved than
    /// its depth; with a slow tracer it does fill the depth; and the calls
    /// outstanding at once are bounded by the workers, the depth and the two
    /// calls a batch makes at most (visibility and sky, then skybox). A
    /// tracer that answers in the call never has more than one batch.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 5)]
    public async Task TheInFlightBoundIsNeverExceededAndIsReached(int threads, int depth)
    {
        LightTestMap map = RichBox();
        ScrambledRayTracer late = new(map.Tracer(), seed: 5, maxDelayMs: 4);
        RadWorld world = await LightAsync(map, late, threads, 40, depth);

        Assert.InRange(world.FacelightPeakBatchesInFlight, 1, depth);
        Assert.Equal(depth, world.FacelightPeakBatchesInFlight);
        Assert.InRange(late.PeakOutstanding, 1, threads * depth * 2);

        RadWorld prompt = await LightAsync(map, map.Tracer(), threads, 40, depth);
        Assert.Equal(1, prompt.FacelightPeakBatchesInFlight);
    }

    /// <summary>
    /// Cancelled with batches in flight on every worker: the stage throws
    /// the cancellation, and by the time it has, every call it started has
    /// finished (nothing still reads a log's rays or writes its answers) and
    /// every rented array is back.
    /// </summary>
    [Fact]
    public async Task ACancelledStageWithBatchesInFlightLeavesNothingRunningOrRented()
    {
        LightTestMap map = RichBox();
        RecyclingScratchPool pool = new();
        using CancellationTokenSource source = new();
        ScrambledRayTracer late = new(map.Tracer(), seed: 7, maxDelayMs: 5);
        CancelOnCall cancelling = new(late, 25, source);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LightAsync(map, cancelling, 3, 40, depth: 4, pool: pool, cancellationToken: source.Token));

        Assert.True(cancelling.Calls > 25);
        Assert.Equal(0, late.Outstanding);
        Assert.All(late.Tasks, t => Assert.True(t.IsCompleted));
        Assert.True(pool.Rented > 0);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);

        // The pool is fit for the next stage: it lights exactly as a fresh one.
        RadWorld again = await LightAsync(map, new ScrambledRayTracer(map.Tracer(), seed: 8), 3, 40, depth: 4, pool: pool);
        RadWorld fresh = await LightAsync(map, map.Tracer(), 1);
        Assert.Equal(Bytes(fresh), Bytes(again));
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>
    /// One batch fails part way through the stage, with others in flight:
    /// the stage reports that failure, every other call has finished before
    /// it does, and every rented array is back.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(60)]
    public async Task ASlabFailingMidStageFailsTheStageAndLeavesNothingRunningOrRented(int failCall)
    {
        LightTestMap map = RichBox();
        RecyclingScratchPool pool = new();
        ScrambledRayTracer failing = new(map.Tracer(), seed: 3, maxDelayMs: 3) { FailCall = failCall };

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LightAsync(map, failing, 3, 40, depth: 4, pool: pool));

        Assert.Equal("planted slab failure", thrown.Message);
        Assert.Equal(0, failing.Outstanding);
        Assert.All(failing.Tasks, t => Assert.True(t.IsCompleted));
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    /// <summary>A tracer that fails inside the call fails the stage the same way.</summary>
    [Fact]
    public async Task ACallFailingInsideTheCallFailsTheStage()
    {
        LightTestMap map = RichBox();
        RecyclingScratchPool pool = new();
        ScrambledRayTracer failing = new(map.Tracer(), asynchronous: false) { FailCall = 5 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => LightAsync(map, failing, 2, 40, depth: 3, pool: pool));
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RadWorld.MaxFacelightPipelineDepth + 1)]
    public void ADepthOutsideItsBoundIsRefused(int depth)
    {
        RadWorld world = LightBox.Build(RichBox());
        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => world.FacelightPipelineDepth = depth);
        Assert.Equal(depth, refused.ActualValue);
        Assert.Equal(RadWorld.DefaultFacelightPipelineDepth, world.FacelightPipelineDepth);

        world.FacelightPipelineDepth = RadWorld.MaxFacelightPipelineDepth;
        Assert.Equal(RadWorld.MaxFacelightPipelineDepth, world.FacelightPipelineDepth);
    }

    /// <summary>
    /// The whole compile through the hybrid, with a GPU stand-in that answers
    /// late and out of order, lights the map to the same bytes as the CPU
    /// tracer, at two worker counts and the depth the compile asks for.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    public async Task AWholeCompileThroughALateGpuWritesTheSameLightingAsTheCpu(int threads, int depth)
    {
        BspData cpu = await VradBspAsync(factory: null, threads: 1, depth: 0);
        LateGpuFactory factory = new();
        BspData gpu = await VradBspAsync(factory, threads, depth);

        Assert.NotNull(factory.Offered);
        Assert.True(factory.Offered!.TotalRays > 0);
        Assert.Equal(0, factory.Offered.Outstanding);
        foreach (BspLump lump in (BspLump[])[BspLump.Lighting, BspLump.Faces, BspLump.WorldLights, BspLump.LeafAmbientLighting])
        {
            Assert.Equal(cpu[lump].Data.ToArray(), gpu[lump].Data.ToArray());
        }
    }

    /// <summary>The compile's depth reaches the face lighting: one outside the bound fails it there.</summary>
    [Fact]
    public async Task ACompileDepthOutsideItsBoundIsRefusedByTheFaceLighting()
    {
        ArgumentOutOfRangeException refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => VradBspAsync(factory: null, threads: 1, depth: RadWorld.MaxFacelightPipelineDepth + 1));
        Assert.Equal(RadWorld.MaxFacelightPipelineDepth + 1, refused.ActualValue);
    }

    private static async Task<BspData> VradBspAsync(IGpuTracerFactory? factory, int threads, int depth)
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        VbspContext vbspContext = new(VbspOptions.Default, content) { MapBase = "room" };
        MapFile vmf = await new MapFileReader(vbspContext, files).LoadAsync(VPath.Create("maps/room.vmf"));
        BspData bsp = (await Vbsp.CompileAsync(vmf, vbspContext)).Bsp!;
        VradContext context = new()
        {
            Options = VradOptions.Default with { Bounces = 1 },
            MapName = "room",
            Content = content,
            Parallelism = new CompileParallelism { MaxDegree = threads },
            GpuTracerFactory = factory,
            GpuPipelineDepth = depth,
        };

        _ = await Vrad.LightAsync(bsp, context);
        return bsp;
    }

    /// <summary>
    /// Opt-in: a real map's face lighting, pipelined through a late tracer at
    /// four workers, against the KD tracer answering in the call. Set
    /// <c>SS_FACELIGHT_PIPELINE_BSP</c> to a post-vvis <c>.bsp</c> (2fort
    /// takes a few minutes) to run it.
    /// </summary>
    [EnvironmentBspFact]
    public async Task ARealMapLightsTheSameBytesThroughALatePipelinedTracer()
    {
        string path = Environment.GetEnvironmentVariable(EnvironmentBspFactAttribute.Variable)!;
        BspData bsp = await StockRadWorld.LoadBspAsync(path);
        IRayTracer kd = StockRadWorld.Tracer(bsp, hdr: false);
        CompileParallelism four = new() { MaxDegree = 4 };
        TextureLightTable texLights = new(new(), Path.GetFileNameWithoutExtension(path));

        RadWorld expected = await RadWorld.StartAsync(bsp, StockRadWorld.Settings(false), texLights, kd, four, CancellationToken.None);
        await expected.LightFacesAsync(kd, four, CancellationToken.None);

        ScrambledRayTracer late = new(kd, seed: 1, maxDelayMs: 1);
        RadWorld actual = await RadWorld.StartAsync(
            await StockRadWorld.LoadBspAsync(path), StockRadWorld.Settings(false), texLights, kd, four, CancellationToken.None);
        await actual.LightFacesAsync(late, four, CancellationToken.None);

        Assert.Equal(Bytes(expected), Bytes(actual));
        Assert.Equal(expected.Statistics.SkyRays, actual.Statistics.SkyRays);
        Assert.Equal(RadWorld.DefaultFacelightPipelineDepth, actual.FacelightPeakBatchesInFlight);
    }

    /// <summary>Skips unless <see cref="Variable"/> names a file.</summary>
    private sealed class EnvironmentBspFactAttribute : FactAttribute
    {
        public const string Variable = "SS_FACELIGHT_PIPELINE_BSP";

        public EnvironmentBspFactAttribute()
        {
            string? path = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Skip = $"set {Variable} to a post-vvis .bsp to compare its face lighting through a late tracer";
            }
        }
    }

    /// <summary>Offers a late GPU stand-in over the casters' KD tree that answers only plain queries.</summary>
    private sealed class LateGpuFactory : IGpuTracerFactory
    {
        public ScrambledRayTracer? Offered { get; private set; }

        public ValueTask<GpuTracerOffer> TryCreateAsync(ShadowCasterSet casters, CancellationToken cancellationToken)
        {
            Offered = new ScrambledRayTracer(new CountingRayTracer(casters.BuildTracer(), plainOnly: true), seed: 13);
            return ValueTask.FromResult(new GpuTracerOffer(Offered, null));
        }
    }

    /// <summary>The inner tracer, until the given call; from then on it cancels the stage's token.</summary>
    private sealed class CancelOnCall(IRayTracer inner, int call, CancellationTokenSource source) : IRayTracer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string TracerIdentity => "cancel-on-call";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            Count();
            return inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            Count();
            return inner.TraceClosestAsync(rays, hits, options, cancellationToken);
        }

        private void Count()
        {
            if (Interlocked.Increment(ref _calls) > call)
            {
                source.Cancel();
            }
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
            bits[..((rays.Length + 63) / 64)].Clear();
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
