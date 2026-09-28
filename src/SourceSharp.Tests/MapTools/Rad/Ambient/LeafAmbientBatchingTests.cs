//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// Leaf ambient's surface-light <c>TestLine</c>s through the
/// <see cref="IRayTracer"/> seam: a leaf's samples go to the tracer as one
/// batch, and the lumps are the bytes a sample-at-a-time visibility gives.
/// </summary>
public sealed class LeafAmbientBatchingTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientFixture _fixture;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="fixture">The loaded map.</param>
    public LeafAmbientBatchingTests(AmbientFixture fixture) => _fixture = fixture;

    private async Task<KdRayTracer> CastersAsync()
    {
        await using ContentFileSystem content = new([]);
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            _fixture.Bsp, VradOptions.Default, content, NullPropCollisionSource.Instance);
        return casters.Set.BuildTracer();
    }

    private Task<LeafAmbientResult> BuildAsync(IAmbientLightVisibility visibility, LeafAmbientOptions options) =>
        LeafAmbientBuilder.BuildAsync(
            _fixture.Ldr, _fixture.Ldr.WorldLights.ToArray(), options, visibility, CancellationToken.None);

    private static byte[] Bytes<T>(T[] lump)
        where T : unmanaged => System.Runtime.InteropServices.MemoryMarshal.AsBytes(lump.AsSpan()).ToArray();

    private static void AssertSameLumps(LeafAmbientResult expected, LeafAmbientResult actual)
    {
        Assert.Equal(Bytes(expected.Index), Bytes(actual.Index));
        Assert.Equal(Bytes(expected.Lighting), Bytes(actual.Lighting));
        Assert.Equal(expected.LightsInAmbientCube, actual.LightsInAmbientCube);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, true)]
    public async Task LeavesTraceThroughTheSeamAndTheLumpsAreASampleAtATimes(int parallelism, bool asynchronous)
    {
        KdRayTracer kd = await CastersAsync();
        LeafAmbientOptions options = LeafAmbientOptions.StockParity with { Parallelism = parallelism };

        // The old granularity: one visibility call per sample, straight to
        // the KD tracer's TestLines, on the queue path.
        DirectPerSample direct = new(kd, stockReciprocal: true);
        SurfaceLightPairs.LeafRecorder recorder = new(direct);
        LeafAmbientResult perSample = await BuildAsync(recorder, options);

        CountingRayTracer alone = new(kd);
        LeafAmbientResult eachLeaf = await BuildAsync(
            new TracerLineVisibility(alone, ComplianceOptions.Stock), options with { BatchSegments = 1 });

        CountingRayTracer batched = new(kd, asynchronous);
        LeafAmbientResult together = await BuildAsync(new TracerLineVisibility(batched, ComplianceOptions.Stock), options);

        AssertSameLumps(perSample, eachLeaf);
        AssertSameLumps(perSample, together);
        Assert.True(together.LightsInAmbientCube > 0, "the fixture should bake some light into the cubes");

        // The per-sample path asks about every sample and every baked light:
        // one call per leaf, a segment from each sample to each light, so
        // each leaf's pairs are a multiple of the light count.
        int lights = together.LightsInAmbientCube;
        int openLeaves = _fixture.Ldr.Leaves.ToArray().Count(l => (l.Contents & LeafAmbientBuilder.ContentsSolid) == 0);
        Assert.Equal(openLeaves, recorder.Leaves.Count);
        Assert.All(recorder.Leaves, l => Assert.Equal(lights, l.Ends.Length));
        Assert.All(recorder.Leaves, l => Assert.Equal(0, l.Starts.Length * l.Ends.Length % lights));
        Assert.Equal(recorder.Leaves.Sum(l => l.Starts.Length), direct.Calls);

        // Through the seam, alone, a leaf is exactly one call of exactly the
        // pairs whose line can matter -- counted here from the leaf's samples
        // and the lights (SurfaceLightPairs), not by the stage.
        int[] expected = recorder.TracedPerLeaf(_fixture.Ldr.WorldLights.ToArray(), options.Compliance);
        int[] actual = [.. alone.VisibilityCalls.Select(c => c.Rays)];
        Assert.All(alone.VisibilityCalls, c => Assert.Equal(RayTraceOptions.TestLine(), c.Options));
        if (parallelism == 1)
        {
            Assert.Equal(expected, actual);
        }
        else
        {
            // Workers finish leaves in any order: the same counts, as a multiset.
            Assert.Equal(expected.Order(), actual.Order());
        }

        // Neither count is the trivial one: some pairs are skipped.
        Assert.True(expected.Sum() < direct.Calls * lights, "no pair was skipped");

        // Batched, whole leaves share calls.
        Assert.True(batched.VisibilityCalls.Count < openLeaves);
        Assert.Equal(alone.VisibilityCalls.Sum(c => c.Rays), batched.VisibilityCalls.Sum(c => c.Rays));
    }

    [Fact]
    public async Task AFailingTracerFailsTheStage()
    {
        TracerLineVisibility failing = new(new CountingRayTracer(new Throwing(), asynchronous: true), ComplianceOptions.Stock);

        await Assert.ThrowsAsync<IOException>(() => BuildAsync(failing, LeafAmbientOptions.StockParity));
    }

    [Fact]
    public async Task AnAsynchronousTracerIsRefusedByTheSynchronousCall()
    {
        KdRayTracer kd = await CastersAsync();
        // Held until after the check: a batch that finished on its own
        // thread first would leave nothing to refuse.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TracerLineVisibility visibility = new(
            new CountingRayTracer(kd, asynchronous: true, release: release.Task), ComplianceOptions.Stock);

        Assert.Throws<InvalidOperationException>(() => visibility.FractionsVisible(Vec3.Zero, [new Vec3(0, 0, 50)], new float[1]));
        release.SetResult();
    }

    [Fact]
    public void ASynchronousFailureIsRethrownByTheSynchronousCall()
    {
        TracerLineVisibility visibility = new(new Throwing(), ComplianceOptions.Stock);

        Assert.Throws<IOException>(() => visibility.FractionsVisible(Vec3.Zero, [new Vec3(0, 0, 50)], new float[1]));
    }

    [Fact]
    public async Task ManyStartsAnswerAsEachStartAlone()
    {
        KdRayTracer kd = await CastersAsync();
        TracerLineVisibility visibility = new(kd, ComplianceOptions.Stock);
        Vec3[] lights = [.. _fixture.Ldr.WorldLights.ToArray().Select(w => w.Origin)];
        Vec3[] starts = [.. lights.Select(o => o + new Vec3(3, -2, 5)), new Vec3(0, 0, 0)];

        float[] batched = new float[starts.Length * lights.Length];
        visibility.FractionsVisible(starts, lights, batched);

        float[] one = new float[lights.Length];
        for (int s = 0; s < starts.Length; s++)
        {
            visibility.FractionsVisible(starts[s], lights, one);
            Assert.Equal(one, batched.AsSpan(s * lights.Length, lights.Length).ToArray());
        }

        Assert.Contains(0.0f, batched);
        Assert.Contains(1.0f, batched);
    }

    [Fact]
    public void ManyStartsWithTooFewSlotsAreRefused()
    {
        TracerLineVisibility visibility = new(new EmptySceneTracer(), ComplianceOptions.Correct);
        IAmbientLightVisibility perSample = new DirectPerSample(KdRayTracer.Build(
            [new TracedTriangle(1, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), 0)]), false);

        Assert.Throws<ArgumentException>(() => visibility.FractionsVisible([Vec3.Zero, Vec3.Zero], [Vec3.Zero], new float[1]));
        Assert.Throws<ArgumentException>(() => perSample.FractionsVisible([Vec3.Zero, Vec3.Zero], [Vec3.Zero], new float[1]));
    }

    [Fact]
    public void NoSegmentsAskNothing()
    {
        CountingRayTracer counting = new(new EmptySceneTracer());
        TracerLineVisibility visibility = new(counting, ComplianceOptions.Correct);

        visibility.FractionsVisible([], [Vec3.Zero], []);
        visibility.FractionsVisible([Vec3.Zero], [], []);

        Assert.Empty(counting.VisibilityCalls);
    }

    [Fact]
    public void TheDefaultManyStartsAsksEachStartInTurn()
    {
        DirectPerSample perSample = new(KdRayTracer.Build(
            [new TracedTriangle(1, new Vec3(-10, -10, 0), new Vec3(10, -10, 0), new Vec3(0, 10, 0), 0)]), false);
        float[] fractions = new float[4];

        ((IAmbientLightVisibility)perSample).FractionsVisible(
            [new Vec3(0, 0, 5), new Vec3(0, 0, -5)], [new Vec3(0, 0, 1), new Vec3(0, 0, -1)], fractions);

        Assert.Equal([1.0f, 0.0f, 0.0f, 1.0f], fractions);
        Assert.Equal(2, perSample.Calls);
    }

    [Fact]
    public void ACubeInTwoHalvesIsTheCubeInOne()
    {
        AmbientSampler whole = new(_fixture.Ldr, _fixture.Ldr.WorldLights.ToArray(), _fixture.Visibility, ComplianceOptions.Stock);
        AmbientSampler halves = new(_fixture.Ldr, _fixture.Ldr.WorldLights.ToArray(), _fixture.Visibility, ComplianceOptions.Stock);
        Vec3[] starts = [.. _fixture.Ldr.WorldLights.ToArray().Take(3).Select(w => w.Origin + new Vec3(0, 0, 8))];

        Vec3[] expected = new Vec3[starts.Length * AmbientCube.Sides];
        Vec3[] actual = new Vec3[expected.Length];
        for (int s = 0; s < starts.Length; s++)
        {
            whole.ComputeCube(starts[s], expected.AsSpan(s * AmbientCube.Sides, AmbientCube.Sides));
            halves.ComputeRayCube(starts[s], actual.AsSpan(s * AmbientCube.Sides, AmbientCube.Sides));
        }

        halves.AddSurfaceLights(starts, actual);

        Assert.Equal(expected, actual);
        Assert.True(whole.SurfaceLightCount > 0);
    }

    [Fact]
    public void SurfaceLightsWithoutVisibilityAreAllSeen()
    {
        DWorldLight[] lights = _fixture.Ldr.WorldLights.ToArray();
        AmbientSampler blind = new(_fixture.Ldr, lights, null, ComplianceOptions.Stock);
        AmbientSampler open = new(_fixture.Ldr, lights, new AllVisible(), ComplianceOptions.Stock);
        Vec3 start = lights[0].Origin + new Vec3(0, 0, 8);
        Vec3[] a = new Vec3[AmbientCube.Sides];
        Vec3[] b = new Vec3[AmbientCube.Sides];

        blind.ComputeCube(start, a);
        open.ComputeCube(start, b);

        Assert.Equal(b, a);
    }

    [Fact]
    public void TooFewCubesAreRefused()
    {
        AmbientSampler sampler = new(_fixture.Ldr, _fixture.Ldr.WorldLights.ToArray(), _fixture.Visibility, ComplianceOptions.Stock);

        Assert.Throws<ArgumentException>(() => sampler.AddSurfaceLights([Vec3.Zero, Vec3.Zero], new Vec3[AmbientCube.Sides]));
    }

    // The visibility as the stage asked it before the seam: the KD tracer's
    // own TestLines, one sample at a time (the interface's default
    // many-starts form loops over this).
    private sealed class DirectPerSample(KdRayTracer tracer, bool stockReciprocal) : IAmbientLightVisibility
    {
        public int Calls;

        public void FractionsVisible(Vec3 start, ReadOnlySpan<Vec3> ends, Span<float> fractions)
        {
            Interlocked.Increment(ref Calls);
            bool[] blocked = new bool[ends.Length];
            tracer.TestLines(start, ends, blocked, stockReciprocal);
            for (int i = 0; i < ends.Length; i++)
            {
                fractions[i] = blocked[i] ? 0.0f : 1.0f;
            }
        }
    }

    private sealed class Throwing : IRayTracer
    {
        public string TracerIdentity => "throwing";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("device lost"));

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("device lost"));
    }

    private sealed class AllVisible : IAmbientLightVisibility
    {
        public void FractionsVisible(Vec3 start, ReadOnlySpan<Vec3> ends, Span<float> fractions) =>
            fractions[..ends.Length].Fill(1.0f);
    }
}
