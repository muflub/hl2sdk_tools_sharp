//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// The transfer build's scratch: pooled arrays that always go back, reused
/// enumerators and staging laid over the ray buffer -- none of which may move
/// a single transfer.
/// </summary>
public sealed class VisMatrixScratchTests
{
    /// <summary>The box with a shelf across it, so some rays are blocked and the bits matter.</summary>
    private static LightTestMap OccludedBox()
    {
        LightTestMap map = BounceBox.Map();
        map.AddOccluder(new(8, 8, 128), new(128, 8, 128), new(128, 248, 128), new(8, 248, 128));
        return map;
    }

    private static async Task<(VisMatrix Matrix, TransferSet Transfers)> BuildAsync(
        LightTestMap map,
        int degree = 1,
        int chunkRays = VisMatrix.RaysPerChunk,
        int slabRays = VisMatrix.RaysPerTraceSlab,
        IScratchArrayPool? pool = null,
        IRayTracer? tracer = null,
        CancellationToken cancellationToken = default)
    {
        RadWorld world = BounceBox.Build(map);
        VisMatrix matrix = new(world.BounceContext())
        {
            ChunkRays = chunkRays,
            TraceSlabRays = slabRays,
            ScratchPool = pool,
        };
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = degree });
        TransferSet set = await matrix.BuildAsync(tracer ?? map.Tracer(), queue, cancellationToken);
        return (matrix, set);
    }

    /// <summary>Every patch's list, as raw bytes, so a float that moved in its last bit fails.</summary>
    private static List<byte[]> Lists(TransferSet set)
    {
        List<byte[]> lists = [];
        for (int p = 0; p < set.PatchCount; p++)
        {
            lists.Add(MemoryMarshal.AsBytes(set.For(p)).ToArray());
        }

        return lists;
    }

    /// <summary>
    /// A slab that is not whole words of hit bits is refused when it is set:
    /// two slabs sharing a word would race on it, and that shows as a
    /// transfer that depends on thread timing, not as an error.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-64)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(100)]
    public void ASlabThatIsNotWholeWordsOfBitsIsRefused(int slabRays)
    {
        RadWorld world = BounceBox.Build(OccludedBox());

        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => new VisMatrix(world.BounceContext()) { TraceSlabRays = slabRays });
        Assert.Equal(slabRays, refused.ActualValue);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(192)]
    [InlineData(VisMatrix.RaysPerTraceSlab)]
    public void ASlabOfWholeWordsIsTaken(int slabRays)
    {
        RadWorld world = BounceBox.Build(OccludedBox());

        Assert.Equal(slabRays, new VisMatrix(world.BounceContext()) { TraceSlabRays = slabRays }.TraceSlabRays);
        Assert.Equal(VisMatrix.RaysPerTraceSlab, new VisMatrix(world.BounceContext()).TraceSlabRays);
    }

    /// <summary>
    /// The transfers are the same bytes whatever the chunking, the slab size
    /// and the worker count: the chunk bound and the scratch reuse are
    /// invisible in the output.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 64)]
    [InlineData(3, 1, 64)]
    [InlineData(1, 100, 64)]
    [InlineData(3, 100, 128)]
    [InlineData(4, 1000, 64)]
    [InlineData(2, VisMatrix.RaysPerChunk, VisMatrix.RaysPerTraceSlab)]
    public async Task ChunkingAndThreadsDoNotMoveATransfer(int degree, int chunkRays, int slabRays)
    {
        (VisMatrix reference, TransferSet expected) = await BuildAsync(OccludedBox());
        (VisMatrix matrix, TransferSet actual) = await BuildAsync(OccludedBox(), degree, chunkRays, slabRays);

        Assert.True(reference.Statistics.Blocked > 0);
        Assert.True(expected.Total > 0);
        Assert.Equal(expected.Total, actual.Total);
        Assert.Equal(expected.Max, actual.Max);
        Assert.Equal(
            MemoryMarshal.AsBytes(expected.Arena).ToArray(),
            MemoryMarshal.AsBytes(actual.Arena).ToArray());
        Assert.Equal(Lists(expected), Lists(actual));
        Assert.Equal(reference.Statistics.Rays, matrix.Statistics.Rays);
        Assert.Equal(reference.Statistics.Blocked, matrix.Statistics.Blocked);
    }

    /// <summary>
    /// A tracer whose slabs come back late leaves the whole stage waiting on
    /// them, which the meter counts as bounce parked time; the transfers are
    /// the same bytes, and a tracer that answers in the call parks nobody.
    /// </summary>
    [Fact]
    public async Task LateSlabsAreMeteredAsBounceParkedTimeAndMoveNoTransfer()
    {
        (_, TransferSet expected) = await BuildAsync(OccludedBox());
        RayTraceMeter meter = new();
        ScrambledRayTracer late = new(OccludedBox().Tracer(), seed: 9);
        (VisMatrix matrix, TransferSet actual) = await BuildAsync(
            OccludedBox(), degree: 2, chunkRays: 500, slabRays: 64, tracer: new MeteredRayTracer(late, meter));

        Assert.Equal(Lists(expected), Lists(actual));
        Assert.True(meter.Parked(TraceWaitStage.Bounce) > TimeSpan.Zero);
        Assert.Equal(matrix.Statistics.Rays, meter.Route(gpu: false).Visibility);
        Assert.Equal(0, late.Outstanding);

        RayTraceMeter prompt = new();
        await BuildAsync(OccludedBox(), degree: 2, tracer: new MeteredRayTracer(OccludedBox().Tracer(), prompt));
        Assert.Equal(TimeSpan.Zero, prompt.Parked(TraceWaitStage.Bounce));
    }

    /// <summary>A small chunk bound really does cut the build into many chunks.</summary>
    [Fact]
    public async Task ASmallBoundMakesManyChunks()
    {
        (VisMatrix one, _) = await BuildAsync(OccludedBox());
        (VisMatrix many, _) = await BuildAsync(OccludedBox(), chunkRays: 100);

        Assert.Equal(1, one.Statistics.Chunks);
        Assert.True(many.Statistics.Chunks > 3, $"only {many.Statistics.Chunks} chunks");
        Assert.True(many.Statistics.LargestChunk < one.Statistics.LargestChunk);
    }

    /// <summary>
    /// The count pass and every chunk's fill pass share one enumerator per
    /// worker: never more than the queue has workers, however many passes.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task EachWorkerKeepsOneEnumerator(int degree)
    {
        (VisMatrix m, _) = await BuildAsync(OccludedBox(), degree, chunkRays: 1);

        Assert.True(m.Statistics.Chunks > degree);
        Assert.InRange(m.Statistics.Enumerators, 1, degree);
    }

    /// <summary>A finished build hands back every array it rented, and rented some.</summary>
    [Fact]
    public async Task AFinishedBuildReturnsItsScratch()
    {
        CountingPool pool = new();
        await BuildAsync(OccludedBox(), degree: 2, chunkRays: 100, pool: pool);

        Assert.True(pool.Rented >= 5, $"rented {pool.Rented}");
        Assert.Equal(pool.Rented, pool.Returned);
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>
    /// An empty build -- a PVS that hides the only cluster, so no rays --
    /// rents nothing for its empty buffers.
    /// </summary>
    [Fact]
    public async Task AnEmptyBuildRentsNoRayBuffers()
    {
        CountingPool pool = new();
        (VisMatrix m, TransferSet t) = await BuildAsync(BounceBox.Map(seesItself: false), pool: pool);

        Assert.Equal(0, t.Total);
        Assert.Equal(0, m.Statistics.LargestChunk);
        Assert.DoesNotContain(typeof(Ray[]), pool.Types);
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>A build cancelled in the middle of tracing still returns every array.</summary>
    [Fact]
    public async Task ACancelledBuildReturnsItsScratch()
    {
        LightTestMap map = OccludedBox();
        CountingPool pool = new();
        using CancellationTokenSource cts = new();
        HookTracer tracer = new(map.Tracer(), (call, _) =>
        {
            if (call == 2)
            {
                cts.Cancel();
            }

            return null;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BuildAsync(map, degree: 2, chunkRays: 100, slabRays: 64, pool: pool, tracer: tracer, cancellationToken: cts.Token));

        Assert.True(pool.Rented > 0);
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>A build whose tracer throws still returns every array.</summary>
    [Fact]
    public async Task AFailedBuildReturnsItsScratch()
    {
        LightTestMap map = OccludedBox();
        CountingPool pool = new();
        HookTracer tracer = new(map.Tracer(), (call, _) =>
            call == 3 ? throw new InvalidOperationException("tracer failed") : null);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(map, chunkRays: 100, slabRays: 64, pool: pool, tracer: tracer));

        Assert.Equal("tracer failed", error.Message);
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>
    /// When one slab fails while another is still being traced
    /// asynchronously (a GPU's way), the build waits for the running slab
    /// before its buffers go back to the pool: otherwise the slab would write
    /// its bits into an array another compile had already rented.
    /// </summary>
    [Fact]
    public async Task AFailureWaitsForSlabsInFlightBeforeReturningScratch()
    {
        LightTestMap map = OccludedBox();
        int inFlight = 0;
        CountingPool pool = new() { OnReturn = () => Assert.Equal(0, Volatile.Read(ref inFlight)) };
        HookTracer tracer = new(map.Tracer(), (call, trace) =>
        {
            if (call == 0)
            {
                Interlocked.Increment(ref inFlight);
                return Task.Run(async () =>
                {
                    await Task.Delay(200);
                    await trace();
                    Interlocked.Decrement(ref inFlight);
                });
            }

            return call == 1 ? throw new InvalidOperationException("second slab failed") : null;
        });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(map, slabRays: 64, pool: pool, tracer: tracer));

        Assert.Equal("second slab failed", error.Message);
        Assert.Equal(0, inFlight);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.FailedChecks);
    }

    /// <summary>
    /// A slab that fails asynchronously is reported once every slab has
    /// finished, and it is the first failure that is reported.
    /// </summary>
    [Fact]
    public async Task AnAsynchronousFailureIsReported()
    {
        LightTestMap map = OccludedBox();
        HookTracer tracer = new(map.Tracer(), (call, _) => call switch
        {
            0 => Task.Run(async () =>
            {
                await Task.Delay(50);
                throw new InvalidOperationException("slab 0 failed late");
            }),
            1 => Task.FromException(new InvalidOperationException("slab 1 failed late")),
            _ => null,
        });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(map, slabRays: 64, tracer: tracer));

        Assert.Equal("slab 0 failed late", error.Message);
    }

    /// <summary>
    /// A pool that hands out oversized arrays full of junk -- what a shared
    /// pool returns after another compile -- changes nothing: every buffer
    /// is written before it is read, and only its requested length is used.
    /// </summary>
    [Fact]
    public async Task StalePooledArraysChangeNothing()
    {
        (VisMatrix reference, TransferSet expected) = await BuildAsync(OccludedBox());
        (VisMatrix m, TransferSet actual) = await BuildAsync(
            OccludedBox(), degree: 2, chunkRays: 100, slabRays: 64, pool: new CountingPool { Junk = true });

        Assert.Equal(Lists(expected), Lists(actual));
        Assert.Equal(reference.Statistics.Blocked, m.Statistics.Blocked);
    }

    /// <summary>
    /// The staging view is the ray buffer's own memory, 3.5 transfers to a
    /// ray, so element <c>i</c> of it sits inside ray <c>i</c>'s range or
    /// before it -- never in a later receiver's.
    /// </summary>
    [Fact]
    public void StagingIsLaidOverTheRays()
    {
        Ray[] rays = new Ray[4];
        Span<Transfer> staging = VisMatrix.StagingOver(rays);

        Assert.Equal(14, staging.Length);
        staging[1] = new Transfer(7, 2.5f);
        Assert.Equal(BitConverter.Int32BitsToSingle(7), rays[0].OriginZ);
        Assert.Equal(2.5f, rays[0].DirectionX);
    }

    /// <summary>The receiver order is allocated at its exact size: no trailing slack.</summary>
    [Fact]
    public void TheReceiverOrderIsExactlySized()
    {
        RadWorld w = BounceBox.Build(BounceBox.Map());
        int[] order = new VisMatrix(w.BounceContext()).ReceiverOrder();

        Assert.NotEmpty(order);
        Assert.Equal(order.Length, order.Distinct().Count());
        Assert.All(order, p => Assert.NotEqual(Patch.Invalid, p));
    }

    /// <summary>Zero-length requests never reach the pool, and dispose returns each array once.</summary>
    [Fact]
    public void ScratchArraysReturnEachArrayOnce()
    {
        CountingPool pool = new();
        ScratchArrays scratch = new(pool);

        Assert.Empty(scratch.Rent<int>(0));
        Assert.Equal(0, pool.Rented);
        Assert.True(scratch.Rent<int>(10).Length >= 10);
        Assert.True(scratch.Rent<Ray>(3).Length >= 3);
        Assert.Equal(2, pool.Outstanding);

        scratch.Dispose();
        scratch.Dispose();

        Assert.Equal(2, pool.Returned);
        Assert.Equal(0, pool.Outstanding);
    }

    /// <summary>
    /// A build given the compile's pool rents from it and gives everything
    /// back, and makes the same transfers as a build left to make a pool of
    /// its own (no pool given), which drops that pool when it ends.
    /// </summary>
    [Fact]
    public async Task TheCompilePoolTakesBackWhatTheBuildRented()
    {
        using CompileScratchPool pool = new();
        (_, TransferSet pooled) = await BuildAsync(OccludedBox(), degree: 2, chunkRays: 64, slabRays: 64, pool: pool);
        (_, TransferSet own) = await BuildAsync(OccludedBox(), degree: 2, chunkRays: 64, slabRays: 64);

        Assert.Equal(Lists(own), Lists(pooled));
        Assert.True(pool.Allocations > 0);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(pool.Allocations, pool.IdleArrays);
    }

    /// <summary>
    /// A pool that counts, and optionally poisons: rented arrays are larger
    /// than asked and filled with 0xFF bytes.
    /// </summary>
    private sealed class CountingPool : IScratchArrayPool
    {
        private readonly object _sync = new();
        private readonly HashSet<object> _out = new(ReferenceEqualityComparer.Instance);
        private int _failedChecks;

        public bool Junk { get; init; }

        public Action? OnReturn { get; init; }

        public int Rented { get; private set; }

        public int Returned { get; private set; }

        public int FailedChecks => _failedChecks;

        public List<Type> Types { get; } = [];

        public int Outstanding
        {
            get
            {
                lock (_sync)
                {
                    return _out.Count;
                }
            }
        }

        public T[] Rent<T>(int minimumLength)
        {
            T[] array = new T[Junk ? minimumLength + 37 : minimumLength];
            if (Junk && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                MemoryMarshal.CreateSpan(
                    ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array)),
                    array.Length * Unsafe.SizeOf<T>()).Fill(0xFF);
            }

            lock (_sync)
            {
                Rented++;
                Types.Add(typeof(T[]));
                _out.Add(array);
            }

            return array;
        }

        public void Return<T>(T[] array)
        {
            try
            {
                OnReturn?.Invoke();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failedChecks);
            }

            lock (_sync)
            {
                Assert.True(_out.Remove(array), "returned an array this pool did not lend, or returned it twice");
                Returned++;
            }
        }
    }

    /// <summary>
    /// A tracer whose calls a test can intercept by call number: the hook
    /// returns null to trace normally, a task to stand in for the call
    /// (given a way to run the real trace), or throws.
    /// </summary>
    private sealed class HookTracer(IRayTracer inner, Func<int, Func<Task>, Task?> hook) : IRayTracer
    {
        private int _calls = -1;

        public string TracerIdentity => inner.TracerIdentity;

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            Task? replaced = hook(call, () => inner.TraceVisibilityAsync(rays, hitBits, options, CancellationToken.None).AsTask());
            return replaced is null
                ? inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken)
                : new ValueTask(replaced);
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            inner.TraceClosestAsync(rays, hits, options, cancellationToken);
    }
}
