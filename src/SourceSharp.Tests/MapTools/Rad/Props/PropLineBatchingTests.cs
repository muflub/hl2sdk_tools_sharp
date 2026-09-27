//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapGen.Content;
using SourceSharp.Tests.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// Prop lighting's <c>TestLine</c> segments through the <see cref="IRayTracer"/>
/// seam: planned per prop, traced as one batch per distinct options, and
/// answered exactly as one sample at a time.
/// </summary>
public sealed class PropLineBatchingTests : IClassFixture<AmbientFixture>, IClassFixture<DetailPropFixture>
{
    private const int PropId = TraceId.StaticProp | 0;

    private readonly AmbientFixture _ambient;
    private readonly DetailPropFixture _detail;

    /// <summary>Takes the shared fixtures.</summary>
    /// <param name="ambient">The leaf-ambient map, for the static-prop scene.</param>
    /// <param name="detail">The detail-prop map.</param>
    public PropLineBatchingTests(AmbientFixture ambient, DetailPropFixture detail)
    {
        _ambient = ambient;
        _detail = detail;
    }

    // A floor, a wall, a sky ceiling and a prop triangle, so the segments are
    // blocked by each kind, the prop's own id can be skipped, and the sky can
    // pass.
    private static readonly KdRayTracer Scene = KdRayTracer.Build(
    [
        new TracedTriangle(TraceId.Opaque, new Vec3(-4000, -4000, -8), new Vec3(4000, -4000, -8), new Vec3(0, 4000, -8), 0),
        new TracedTriangle(TraceId.Opaque, new Vec3(60, -100, -50), new Vec3(60, 100, -50), new Vec3(60, 0, 300), 0),
        new TracedTriangle(TraceId.Sky, new Vec3(-4000, -4000, 400), new Vec3(4000, -4000, 400), new Vec3(0, 4000, 400), 0),
        new TracedTriangle(PropId, new Vec3(-30, -30, 30), new Vec3(30, -30, 30), new Vec3(0, 30, 30), 0),
    ]);

    private static byte[] Bytes<T>(T[] lump)
        where T : unmanaged => System.Runtime.InteropServices.MemoryMarshal.AsBytes(lump.AsSpan()).ToArray();

    private static byte[] AllClusters() => [.. Enumerable.Repeat((byte)0xFF, 8192)];

    private static List<PropLight> Lights() =>
    [
        new() { Type = EmitType.Point, Origin = new Vec3(20, 10, 150), Intensity = new Vec3(3, 2, 1), QuadraticAttn = 1, Pvs = AllClusters() },
        new() { Type = EmitType.Point, Origin = new Vec3(200, 0, 40), Intensity = new Vec3(1, 1, 1), QuadraticAttn = 1, Pvs = AllClusters() },
        new()
        {
            Type = EmitType.Surface, Origin = new Vec3(-40, 0, 120), Normal = new Vec3(0, 0, -1),
            Intensity = new Vec3(2, 2, 2), Pvs = AllClusters(),
        },
        new()
        {
            Type = EmitType.Spotlight, Origin = new Vec3(0, 0, 200), Normal = new Vec3(0, 0, -1), StopDot = 0.9f, StopDot2 = 0.5f,
            Exponent = 2, Intensity = new Vec3(1, 1, 1), QuadraticAttn = 1, Pvs = AllClusters(),
        },
        new() { Type = EmitType.SkyLight, Normal = new Vec3(0.3f, 0.1f, -0.95f), Intensity = new Vec3(1, 1, 1), Pvs = AllClusters() },
        new() { Type = EmitType.SkyAmbient, Intensity = new Vec3(0.2f, 0.2f, 0.3f), Pvs = AllClusters() },
    ];

    private static Vec3[] Points(int count, int seed)
    {
        Random random = new(seed);
        return [.. Enumerable.Range(0, count).Select(_ => new Vec3(
            (float)(random.NextDouble() * 160) - 80,
            (float)(random.NextDouble() * 160) - 80,
            (float)(random.NextDouble() * 80) - 2))];
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(true, PropId)]
    [InlineData(false, PropId)]
    public void APlannedBatchAnswersEverySampleAsGatherDoes(bool stock, int skipId)
    {
        ComplianceOptions compliance = stock ? ComplianceOptions.Stock : ComplianceOptions.Correct;
        PropLightSampler sampler = new(Scene, compliance, sunAngularExtent: 0.05f);
        List<PropLight> lights = Lights();
        Vec3[] points = Points(60, 21);
        Vec3 normal = new Vec3(0.2f, -0.1f, 1).Normalise().Normalised;

        CountingRayTracer counting = new(Scene);
        TestLineBatch batch = new PropLightSampler(counting, compliance, sunAngularExtent: 0.05f).CreateBatch();
        List<PendingPropSample> planned = [];
        foreach (Vec3 p in points)
        {
            foreach (PropLight light in lights)
            {
                planned.Add(sampler.Plan(light, p, normal, batch, PropGatherFlags.ForceFast, 0.0f, skipId));
            }
        }

        batch.Trace(CancellationToken.None);

        int k = 0;
        int lit = 0;
        foreach (Vec3 p in points)
        {
            foreach (PropLight light in lights)
            {
                PropLightSample expected = sampler.Gather(light, p, normal, PropGatherFlags.ForceFast, 0.0f, skipId);
                PropLightSample actual = sampler.Resolve(planned[k++], batch);
                Assert.Equal(expected, actual);
                lit += actual.Dot > 0 ? 1 : 0;
            }
        }

        // Plain and sky-passing segments: two batches, whatever the count.
        Assert.Equal(2, counting.VisibilityCalls.Count);
        Assert.True(lit > 0 && lit < planned.Count, $"{lit} of {planned.Count} lit");
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(PropId, true)]
    public void EverySegmentASamplePlansIsATestLineAnsweredAlone(int skipId, bool stock)
    {
        // Each segment carries TestLine's options -- isolated, the sample's
        // skipped id -- and its answer in the batch is its answer traced on
        // its own. (That an isolated seam ray answers as KdRayTracer.TestLines
        // does on the same start and end is TestLineSeamTests' fact.)
        ComplianceOptions compliance = stock ? ComplianceOptions.Stock : ComplianceOptions.Correct;
        Recording recording = new(Scene);
        PropLightSampler sampler = new(recording, compliance, sunAngularExtent: 0.05f);
        foreach (Vec3 p in Points(30, 4))
        {
            foreach (PropLight light in Lights())
            {
                sampler.Gather(light, p, new Vec3(0, 0, 1), PropGatherFlags.None, 0.5f, skipId);
            }
        }

        Assert.NotEmpty(recording.Segments);
        foreach ((Ray ray, RayTraceOptions options, bool blocked) in recording.Segments)
        {
            Assert.True(options.IsolatedRays);
            Assert.Equal(0.0f, options.MinDistance);
            Assert.Equal(skipId < 0 ? null : skipId, options.SkipId);

            ulong[] bit = new ulong[1];
            Scene.TraceVisibility([ray], bit, options);
            Assert.Equal(blocked, bit[0] != 0);
        }

        Assert.Contains(recording.Segments, s => s.Blocked);
        Assert.Contains(recording.Segments, s => !s.Blocked);
        Assert.Contains(recording.Segments, s => s.Options.SkyDoesNotBlock);
        Assert.Contains(recording.Segments, s => !s.Options.SkyDoesNotBlock);
    }

    [Fact]
    public void ASamplerRefusesATracerThatCannotSkipOrPassTheSky()
    {
        Assert.Throws<NotSupportedException>(() =>
            new PropLightSampler(new CountingRayTracer(Scene, plainOnly: true), ComplianceOptions.Correct));
    }

    [Fact]
    public void ALightOfNoKnownTypeIsRefusedAtPlanning()
    {
        PropLightSampler sampler = new(Scene, ComplianceOptions.Correct);
        PropLight odd = new() { Type = (EmitType)99 };

        Assert.Throws<InvalidOperationException>(() => sampler.Plan(odd, Vec3.Zero, new Vec3(0, 0, 1), sampler.CreateBatch()));
    }

    [Fact]
    public void AnUnreachableLightPlansNoSegmentAndResolvesToZero()
    {
        PropLightSampler sampler = new(Scene, ComplianceOptions.Correct);
        TestLineBatch batch = sampler.CreateBatch();

        // Behind the normal: the sky light's dot is zero before any trace.
        PendingPropSample pending = sampler.Plan(
            Lights()[4], new Vec3(0, 0, 10), new Vec3(0, 0, -1), batch);
        batch.Trace(CancellationToken.None);

        Assert.Equal(PropSampleKind.None, pending.Kind);
        Assert.Equal(0, batch.Count);
        Assert.Equal(default, sampler.Resolve(in pending, batch));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, true)]
    public async Task DetailPropsTraceInBatchesOfWholePropsAndLightTheSame(int parallelism, bool asynchronous)
    {
        // Each prop alone, answered inside the call: the old granularity.
        CountingRayTracer alone = new(_detail.Environment);
        DetailPropLightingResult expected = await RunDetailAsync(alone, parallelism, batchSegments: 1);

        CountingRayTracer batched = new(_detail.Environment, asynchronous);
        DetailPropLightingResult actual = await RunDetailAsync(batched, parallelism, TestLineStage.DefaultBatchSegments);

        Assert.Equal(Bytes(expected.Props), Bytes(actual.Props));
        Assert.Equal(Bytes(expected.LightStyles), Bytes(actual.LightStyles));

        // Alone, a prop is at most two calls (plain and sky-passing segments;
        // detail props skip no id) however many lights it sees; batched, the
        // same segments go in far fewer calls.
        int props = _detail.Stock.Props.Count;
        Assert.InRange(alone.VisibilityCalls.Count, 1, 2 * props);
        Assert.True(batched.VisibilityCalls.Count < alone.VisibilityCalls.Count);
        Assert.Equal(alone.VisibilityCalls.Sum(c => c.Rays), batched.VisibilityCalls.Sum(c => c.Rays));
        Assert.All(batched.VisibilityCalls, c => Assert.Null(c.Options.SkipId));
    }

    [Fact]
    public async Task DetailPropsLightAsTheFixtureRunDoes()
    {
        DetailPropLightingResult fixture = await _detail.RunAsync(LightingMode.Ldr);
        DetailPropLightingResult seam = await RunDetailAsync(new CountingRayTracer(_detail.Environment, asynchronous: true), 2, 7);

        Assert.Equal(Bytes(fixture.Props), Bytes(seam.Props));
        Assert.Equal(Bytes(fixture.LightStyles), Bytes(seam.LightStyles));
    }

    [Fact]
    public async Task StaticPropsTraceInBatchesAndLightTheSame()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await CratesAsync();
        CountingRayTracer alone = new(Scene);
        StaticPropLightingResult expected = await LightAsync(alone, lump, models, batchSegments: 1);

        CountingRayTracer batched = new(Scene, asynchronous: true);
        StaticPropLightingResult actual = await LightAsync(batched, lump, models, TestLineStage.DefaultBatchSegments);

        Assert.Equal(expected.Files.Select(f => f.Data), actual.Files.Select(f => f.Data));
        Assert.Equal(expected.BadVertices, actual.BadVertices);
        Assert.Equal(2, actual.Files.Length);

        // Prop 0 skips its own id, prop 1 does not. Alone, each prop is one
        // call for its plain segments and one for its sky-passing ones,
        // however many vertices it has; batched together, the same four
        // kinds of segment are still four calls.
        (int Rays, RayTraceOptions Options)[] calls = [.. alone.VisibilityCalls];
        Assert.Equal(4, calls.Length);
        Assert.Equal([PropId, PropId], calls.Take(2).Select(c => c.Options.SkipId));
        Assert.Equal([null, null], calls.Skip(2).Select(c => c.Options.SkipId));
        Assert.All(calls, c => Assert.True(c.Rays > 1));
        Assert.Equal(4, batched.VisibilityCalls.Count);
        Assert.Equal(calls.Sum(c => c.Rays), batched.VisibilityCalls.Sum(c => c.Rays));
    }

    private Task<DetailPropLightingResult> RunDetailAsync(IRayTracer tracer, int parallelism, int batchSegments)
    {
        AmbientScene scene = AmbientScene.Create(_detail.Bsp, LightingMode.Ldr);
        IReadOnlyList<PropLight> lights = PropLights.FromWorldLights(_detail.Bsp, LightingMode.Ldr, out _);
        return DetailPropLighting.ComputeAsync(
            scene, _detail.Stock, Array.Empty<Vec3>(), lights, new PropLightSampler(tracer, ComplianceOptions.Stock),
            ComplianceOptions.Stock, parallelism, null, batchSegments, CancellationToken.None);
    }

    private async Task<(StaticPropLump Lump, IReadOnlyList<StaticPropModel> Models)> CratesAsync()
    {
        const string Crate = "models/props_junk/wood_crate001a.mdl";
        await using ContentFileSystem content = await StudioModelWriterTests.MountAsync(SyntheticContent.Build());
        IReadOnlyList<StaticPropModel> models = await new StaticPropModelLoader(content, NullPropCollisionSource.Instance)
            .LoadDictionaryAsync([Crate], CancellationToken.None);
        Assert.NotNull(models[0].Mdl);
        Assert.NotNull(models[0].Vtx);

        Vec3 origin = _ambient.Ldr.WorldLights[0].Origin;
        StaticPropLump lump = new();
        lump.ModelNames.Add(Crate);
        lump.Props.Add(new StaticProp { PropType = 0, Flags = StaticPropFlags.NoSelfShadowing, Origin = origin });
        lump.Props.Add(new StaticProp { PropType = 0, Origin = origin + new Vec3(16, 0, 0) });
        return (lump, models);
    }

    private Task<StaticPropLightingResult> LightAsync(
        IRayTracer tracer, StaticPropLump lump, IReadOnlyList<StaticPropModel> models, int batchSegments) =>
        StaticPropLighting.ComputeAsync(
            _ambient.Ldr,
            lump,
            models,
            Lights(),
            new PropLightSampler(tracer, ComplianceOptions.Correct, sunAngularExtent: 0.05f),
            new StaticPropLightingOptions { Parallelism = 1, Indirect = false, BatchSegments = batchSegments },
            CancellationToken.None);

    // Answers with the KD tracer and keeps every segment with its answer.
    private sealed class Recording(KdRayTracer inner) : IRayTracer
    {
        public List<(Ray Ray, RayTraceOptions Options, bool Blocked)> Segments { get; } = [];

        public string TracerIdentity => "recording";

        public bool Supports(RayTraceOptions options) => true;

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            inner.TraceVisibility(rays.Span, hitBits.Span, options);
            for (int i = 0; i < rays.Length; i++)
            {
                Segments.Add((rays.Span[i], options, (hitBits.Span[i >> 6] & (1UL << (i & 63))) != 0));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("prop lighting asks no closest hits");
    }
}
