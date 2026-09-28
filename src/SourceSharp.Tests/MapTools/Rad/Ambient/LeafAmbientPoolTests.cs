//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// The leaf-ambient stage over pooled worker scratch: its batches and sample
/// arenas come from a pool that recycles and poisons, and the lumps are
/// byte for byte what fresh arrays give -- after larger or smaller builds,
/// after a build that faulted or was cancelled, and with builds running at
/// the same time -- with every array back in the pool when each build ends.
/// </summary>
public sealed class LeafAmbientPoolTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientFixture _fixture;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="fixture">The loaded map.</param>
    public LeafAmbientPoolTests(AmbientFixture fixture) => _fixture = fixture;

    public enum Ending
    {
        Faulted,
        Cancelled,
    }

    private async Task<KdRayTracer> CastersAsync()
    {
        await using ContentFileSystem content = new([]);
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            _fixture.Bsp, VradOptions.Default, content, NullPropCollisionSource.Instance);
        return casters.Set.BuildTracer();
    }

    private Task<LeafAmbientResult> BuildAsync(
        IRayTracer tracer, LeafAmbientOptions options, IScratchArrayPool pool, CancellationToken cancellationToken = default) =>
        LeafAmbientBuilder.BuildAsync(
            _fixture.Ldr,
            _fixture.Ldr.WorldLights.ToArray(),
            options with { ScratchPool = pool },
            new TracerLineVisibility(tracer, ComplianceOptions.Stock),
            cancellationToken);

    /// <summary>The lumps from arrays nobody has used before: the baseline every pooled build must match.</summary>
    private Task<LeafAmbientResult> FreshAsync(IRayTracer tracer, LeafAmbientOptions options) =>
        BuildAsync(tracer, options, new FreshPool());

    private static byte[] Bytes<T>(T[] lump)
        where T : unmanaged => MemoryMarshal.AsBytes(lump.AsSpan()).ToArray();

    private static void AssertSameLumps(LeafAmbientResult expected, LeafAmbientResult actual)
    {
        Assert.Equal(Bytes(expected.Index), Bytes(actual.Index));
        Assert.Equal(Bytes(expected.Lighting), Bytes(actual.Lighting));
        Assert.Equal(expected.LightsInAmbientCube, actual.LightsInAmbientCube);
    }

    private static void AssertAllReturned(RecyclingScratchPool pool)
    {
        Assert.True(pool.Rented > 0, "the stage should rent its scratch from the pool it was given");
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    /// <summary>
    /// Reused, poisoned scratch gives the lumps fresh arrays give, serially
    /// and on four workers, with a tracer that answers inside the call and
    /// one that answers later; and every array is back when the build ends.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task ReusedScratchGivesTheLumpsFreshArraysGive(int parallelism, bool asynchronous)
    {
        KdRayTracer kd = await CastersAsync();
        LeafAmbientOptions options = LeafAmbientOptions.StockParity with { Parallelism = parallelism };
        LeafAmbientResult fresh = await FreshAsync(kd, options);
        RecyclingScratchPool pool = new();

        LeafAmbientResult pooled = await BuildAsync(new CountingRayTracer(kd, asynchronous), options, pool);

        AssertSameLumps(fresh, pooled);
        Assert.True(fresh.LightsInAmbientCube > 0, "the fixture should bake some light into the cubes");
        Assert.True(pool.Recycled > 0, "a worker's growth and its next batches should reuse returned arrays");
        AssertAllReturned(pool);
    }

    /// <summary>
    /// One pool through builds of different sizes -- one sample a leaf, the
    /// full count, one leaf a batch, then one sample a leaf again -- so the
    /// scratch serves a larger build after a smaller and a smaller after a
    /// larger. Each build's lumps are its fresh lumps: nothing past what a
    /// build wrote reaches its answers.
    /// </summary>
    [Fact]
    public async Task LargerAfterSmallerAndSmallerAfterLargerReadOnlyTheirOwnData()
    {
        KdRayTracer kd = await CastersAsync();
        LeafAmbientOptions full = LeafAmbientOptions.StockParity with { Parallelism = 2 };
        LeafAmbientOptions[] sequence =
        [
            full with { FastAmbient = true },
            full,
            full with { BatchSegments = 1 },
            full with { FastAmbient = true },
        ];
        RecyclingScratchPool pool = new();

        foreach (LeafAmbientOptions options in sequence)
        {
            LeafAmbientResult fresh = await FreshAsync(kd, options);
            LeafAmbientResult pooled = await BuildAsync(kd, options, pool);

            AssertSameLumps(fresh, pooled);
            AssertAllReturned(pool);
        }

        Assert.True(pool.Recycled > 0);
    }

    /// <summary>
    /// A build whose tracer faults, or whose compile is cancelled, part way
    /// through gives every array back; the next build on the same pool gets
    /// exactly the fresh lumps, so a failed compile leaves no poisoned scratch
    /// for the next.
    /// </summary>
    [Theory]
    [InlineData(Ending.Faulted, false)]
    [InlineData(Ending.Faulted, true)]
    [InlineData(Ending.Cancelled, false)]
    [InlineData(Ending.Cancelled, true)]
    public async Task AFaultedOrCancelledBuildLeavesThePoolFitForTheNext(Ending ending, bool asynchronous)
    {
        KdRayTracer kd = await CastersAsync();
        LeafAmbientOptions options = LeafAmbientOptions.StockParity with { Parallelism = 3, BatchSegments = 64 };
        LeafAmbientResult fresh = await FreshAsync(kd, options);
        RecyclingScratchPool pool = new();
        using CancellationTokenSource source = new();
        StopAfter stopping = new(kd, calls: 5, ending, source, asynchronous);

        if (ending == Ending.Faulted)
        {
            await Assert.ThrowsAsync<IOException>(() => BuildAsync(stopping, options, pool, source.Token));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildAsync(stopping, options, pool, source.Token));
        }

        Assert.True(stopping.Calls > 5, "the build should have been stopped part way, not before it began");
        AssertAllReturned(pool);

        LeafAmbientResult after = await BuildAsync(kd, options, pool);

        AssertSameLumps(fresh, after);
        AssertAllReturned(pool);
    }

    /// <summary>
    /// Two builds at once, four workers each, over ONE recycling pool: arrays
    /// pass between the builds' workers as they are returned, and neither
    /// build's lumps move -- no worker keeps using an array it gave back, and
    /// no two workers are lent the same one.
    /// </summary>
    [Fact]
    public async Task ConcurrentBuildsOverOnePoolShareNoScratch()
    {
        KdRayTracer kd = await CastersAsync();
        LeafAmbientOptions options = LeafAmbientOptions.StockParity with { Parallelism = 4, BatchSegments = 256 };
        LeafAmbientResult fresh = await FreshAsync(kd, options);
        RecyclingScratchPool pool = new();

        LeafAmbientResult[] results = await Task.WhenAll(
            Task.Run(() => BuildAsync(new CountingRayTracer(kd, asynchronous: true), options, pool)),
            Task.Run(() => BuildAsync(kd, options, pool)));

        AssertSameLumps(fresh, results[0]);
        AssertSameLumps(fresh, results[1]);
        Assert.True(pool.PeakOutstanding > 2, "several workers should have held scratch at once");
        AssertAllReturned(pool);
    }

    /// <summary>
    /// A worker's batch reserves its bound once and never grows past it: every
    /// traced batch fits, and one worker rents one ray array for the whole
    /// stage, whether batches close on segments or hold one leaf each.
    /// </summary>
    [Theory]
    [InlineData(TestLineStage.DefaultBatchSegments)]
    [InlineData(1)]
    [InlineData(700)]
    public async Task TheBatchReservesItsBoundOnceAndEveryBatchFits(int batchSegments)
    {
        KdRayTracer kd = await CastersAsync();
        LeafAmbientOptions options = LeafAmbientOptions.StockParity with { Parallelism = 1, BatchSegments = batchSegments };
        int lights = (await FreshAsync(kd, options)).LightsInAmbientCube;
        int bound = LeafAmbientBuilder.BatchSegmentBound(_fixture.Ldr, options, lights, batchSegments, TestLineStage.DefaultBatchItems);
        RecyclingScratchPool pool = new();
        CountingRayTracer counting = new(kd);

        await BuildAsync(counting, options, pool);

        Assert.True(bound > 0);
        Assert.All(counting.VisibilityCalls, c => Assert.InRange(c.Rays, 1, bound));
        if (batchSegments == 1)
        {
            // One leaf a batch: the bound is the largest leaf exactly.
            Assert.Equal(bound, counting.VisibilityCalls.Max(c => c.Rays));
        }

        Assert.Equal(1, pool.RentedOf<Ray>());
    }

    [Fact]
    public void NoBakedLightReservesNothing()
    {
        Assert.Equal(0, LeafAmbientBuilder.BatchSegmentBound(
            _fixture.Ldr, LeafAmbientOptions.StockParity, 0, TestLineStage.DefaultBatchSegments, TestLineStage.DefaultBatchItems));
    }

    /// <summary>
    /// The per-call path (a visibility that is not the tracer's) rents one
    /// leaf's scratch per call from the options' pool and gives it back
    /// before returning, with the lumps the traced path gives.
    /// </summary>
    [Fact]
    public async Task ThePerCallPathReturnsItsScratchToo()
    {
        KdRayTracer kd = await CastersAsync();
        LeafAmbientOptions options = LeafAmbientOptions.StockParity with { Parallelism = 2 };
        LeafAmbientResult traced = await FreshAsync(kd, options);
        RecyclingScratchPool pool = new();

        LeafAmbientResult perCall = await LeafAmbientBuilder.BuildAsync(
            _fixture.Ldr,
            _fixture.Ldr.WorldLights.ToArray(),
            options with { ScratchPool = pool },
            new Wrapped(new TracerLineVisibility(kd, ComplianceOptions.Stock)),
            CancellationToken.None);

        AssertSameLumps(traced, perCall);
        AssertAllReturned(pool);
    }

    // Hides the tracer behind the interface, so the stage takes its per-call
    // path rather than the batched one.
    private sealed class Wrapped(IAmbientLightVisibility inner) : IAmbientLightVisibility
    {
        public void FractionsVisible(Vec3 start, ReadOnlySpan<Vec3> ends, Span<float> fractions) =>
            inner.FractionsVisible(start, ends, fractions);
    }

    // The tracer, until the given number of calls have started; from then on
    // each call faults, or cancels the build's token and reports that.
    private sealed class StopAfter(IRayTracer inner, int calls, Ending ending, CancellationTokenSource source, bool asynchronous)
        : IRayTracer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string TracerIdentity => "stop-after";

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            bool stop = Interlocked.Increment(ref _calls) > calls;
            if (!asynchronous)
            {
                return Answer(stop, rays, hitBits, options, cancellationToken);
            }

            return new ValueTask(Task.Run(async () =>
            {
                await Task.Yield();
                await Answer(stop, rays, hitBits, options, cancellationToken);
            }));
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceClosestAsync(rays, hits, options, cancellationToken);

        private ValueTask Answer(
            bool stop, ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken)
        {
            if (!stop)
            {
                return inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
            }

            if (ending == Ending.Faulted)
            {
                return ValueTask.FromException(new IOException("device lost"));
            }

            source.Cancel();
            return ValueTask.FromCanceled(source.Token);
        }
    }

    /// <summary>A pool that always allocates and never reuses: the fresh-array baseline.</summary>
    private sealed class FreshPool : IScratchArrayPool
    {
        public T[] Rent<T>(int minimumLength) => new T[minimumLength];

        public void Return<T>(T[] array)
        {
        }
    }
}
