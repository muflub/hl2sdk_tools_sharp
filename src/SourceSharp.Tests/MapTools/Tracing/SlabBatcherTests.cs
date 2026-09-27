//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//


using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="SlabBatcher"/> on a CPU stand-in for the device: concurrent
/// requests share slabs, every ray still gets exactly its own answer, and
/// cancellation, failure and closing touch only the requests they should.
/// </summary>
public sealed class SlabBatcherTests
{
    // Answers each staged ray from its own fields, like a kernel whose lanes
    // are independent: any-hit when MaxDistance > 5; closest hits primitive
    // (int)OriginX at t = OriginY, or misses when DirectionX < 0.
    private sealed class FakeDevice(int maxSlabRays) : ISlabDevice
    {
        private readonly float[] _staging = new float[maxSlabRays * 8];
        private int _inside;

        public int MaxSlabRays { get; } = maxSlabRays;

        public List<int> RaysPerDispatch { get; } = [];

        public ManualResetEventSlim Gate { get; } = new(true);

        public TaskCompletionSource FirstDispatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? FailNext { get; set; }

        public Span<float> StageRays(int rayCount) => _staging.AsSpan(0, rayCount * 8);

        public void Dispatch(int mode, int rayCount, Span<uint> outWords, uint tminBits, uint tmaxScaleBits)
        {
            Assert.Equal(1, Interlocked.Increment(ref _inside));
            try
            {
                FirstDispatchStarted.TrySetResult();
                Gate.Wait(TimeSpan.FromSeconds(30));
                RaysPerDispatch.Add(rayCount);
                if (FailNext is { } e)
                {
                    FailNext = null;
                    throw e;
                }

                outWords.Clear();
                for (int i = 0; i < rayCount; i++)
                {
                    int b = i * 8;
                    if (mode == 0)
                    {
                        if (_staging[b + 7] > 5)
                        {
                            outWords[(i / 64 * 2) + (i % 64 / 32)] |= 1u << (i % 32);
                        }
                    }
                    else
                    {
                        outWords[i * 2] = _staging[b + 4] < 0 ? 0xFFFFFFFFu : (uint)_staging[b];
                        outWords[(i * 2) + 1] = (uint)BitConverter.SingleToInt32Bits(_staging[b + 1]);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }

    private static readonly int[] Ids = [100, 101, 102, 103];

    private static Ray[] Rays(int n, int seed)
    {
        Random random = new(seed);
        Ray[] rays = new Ray[n];
        for (int i = 0; i < n; i++)
        {
            rays[i] = new Ray(
                random.Next(0, 6), (float)random.NextDouble(), 0,
                random.Next(2) == 0 ? -1 : 1, 0, 0,
                random.Next(2) == 0 ? 1f : random.Next(1, 10));
        }

        return rays;
    }

    private static bool Hit(Ray r) => r.MaxDistance > 5;

    private static HitId Closest(Ray r)
    {
        int prim = (int)r.OriginX;
        if (r.DirectionX < 0 || prim >= Ids.Length)
        {
            return HitId.Missed;
        }

        return new HitId(Ids[prim], r.MaxDistance == 1f ? r.OriginY : r.OriginY / r.MaxDistance);
    }

    [Fact]
    public async Task ConcurrentRequestsOfEverySizeGetExactlyTheirOwnAnswers()
    {
        FakeDevice device = new(256);
        SlabBatcher batcher = new(device, Ids, 0);
        int[] sizes = [1, 63, 64, 65, 127, 200, 256, 257, 700];
        List<(Ray[] Rays, ulong[] Bits, HitId[] Hits, Task Vis, Task Closest)> calls = [];
        for (int k = 0; k < sizes.Length; k++)
        {
            Ray[] rays = Rays(sizes[k], k);
            ulong[] bits = new ulong[((rays.Length + 63) / 64) + 1];
            bits[^1] = ulong.MaxValue; // past the request: must stay as it was
            Array.Fill(bits, ulong.MaxValue, 0, bits.Length - 1); // inside: every bit must be rewritten
            HitId[] hits = new HitId[rays.Length];
            calls.Add((rays, bits, hits,
                batcher.TraceVisibilityAsync(rays, bits, 0, CancellationToken.None),
                batcher.TraceClosestAsync(rays, hits, 0, CancellationToken.None)));
        }

        await Task.WhenAll(calls.SelectMany(c => new[] { c.Vis, c.Closest }));

        foreach ((Ray[] rays, ulong[] bits, HitId[] hits, _, _) in calls)
        {
            for (int i = 0; i < ((rays.Length + 63) / 64) * 64; i++)
            {
                bool set = (bits[i / 64] & (1UL << (i % 64))) != 0;
                Assert.Equal(i < rays.Length && Hit(rays[i]), set);
            }

            Assert.Equal(ulong.MaxValue, bits[^1]);
            Assert.Equal(rays.Select(Closest), hits);
        }
    }

    [Fact]
    public async Task RequestsThatQueueWhileTheDeviceWorksShareASlab()
    {
        FakeDevice device = new(4096);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);

        Task first = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await device.FirstDispatchStarted.Task;
        Task[] queued = [.. Enumerable.Range(0, 20).Select(k => batcher.TraceClosestAsync(Rays(30, k), new HitId[30], 0, CancellationToken.None))];
        device.Gate.Set();
        await Task.WhenAll([first, .. queued]);

        Assert.Equal([10, 600], device.RaysPerDispatch);
        Assert.Equal(2, batcher.Dispatches);
    }

    [Fact]
    public void VisibilitySegmentsStartOnAWorkgroupAndSplitOnOne()
    {
        List<SlabBatcher.Segment> slab = [];
        SlabBatcher.Request a = new(0, Rays(70, 1), new ulong[2], default, 0, default);
        SlabBatcher.Request b = new(0, Rays(200, 2), new ulong[4], default, 0, default);
        SlabBatcher.Request other = new(1, Rays(5, 3), default, new HitId[5], 0, default);
        SlabBatcher.Request epsilon = new(0, Rays(5, 4), new ulong[1], default, 7, default);

        int total = SlabBatcher.Plan([a, other, epsilon, b], 0, 0, 256, slab);

        Assert.Equal(
            [new SlabBatcher.Segment(a, 0, 70, 0), new SlabBatcher.Segment(b, 0, 128, 128)],
            slab);
        Assert.Equal(256, total);

        b.Next = 128;
        total = SlabBatcher.Plan([b], 0, 0, 256, slab);
        Assert.Equal([new SlabBatcher.Segment(b, 128, 72, 0)], slab);
        Assert.Equal(72, total);
    }

    [Fact]
    public void ClosestSegmentsPackWithoutGaps()
    {
        List<SlabBatcher.Segment> slab = [];
        SlabBatcher.Request a = new(1, Rays(70, 1), default, new HitId[70], 0, default);
        SlabBatcher.Request b = new(1, Rays(300, 2), default, new HitId[300], 0, default);

        int total = SlabBatcher.Plan([a, b], 1, 0, 256, slab);

        Assert.Equal([new SlabBatcher.Segment(a, 0, 70, 0), new SlabBatcher.Segment(b, 0, 186, 70)], slab);
        Assert.Equal(256, total);
    }

    [Fact]
    public async Task ACancelledRequestIsDroppedBeforeItsSlab()
    {
        FakeDevice device = new(256);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);
        using CancellationTokenSource cancel = new();

        Task first = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await device.FirstDispatchStarted.Task;
        Task doomed = batcher.TraceClosestAsync(Rays(10, 2), new HitId[10], 0, cancel.Token);
        await cancel.CancelAsync();
        device.Gate.Set();

        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed);
        Assert.Equal([10], device.RaysPerDispatch);
    }

    [Fact]
    public async Task AFailedDispatchFaultsOnlyItsRequests()
    {
        FakeDevice device = new(256) { FailNext = new InvalidOperationException("device lost") };
        SlabBatcher batcher = new(device, Ids, 0);

        Task failed = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);

        HitId[] hits = new HitId[10];
        Ray[] rays = Rays(10, 2);
        await batcher.TraceClosestAsync(rays, hits, 0, CancellationToken.None);
        Assert.Equal(rays.Select(Closest), hits);
    }

    [Fact]
    public async Task ClosingFailsQueuedRequestsAndReleasesTheDeviceOnce()
    {
        FakeDevice device = new(256);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);
        int released = 0;

        Task inFlight = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await device.FirstDispatchStarted.Task;
        Task queued = batcher.TraceClosestAsync(Rays(10, 2), new HitId[10], 0, CancellationToken.None);

        Task closing = Task.Run(() => batcher.Close(() => released++));

        // Close has begun (and is waiting out the dispatch) before the device finishes.
        Assert.True(SpinWait.SpinUntil(() => batcher.IsClosed, TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        device.Gate.Set();
        await closing;
        batcher.Close(() => released++);

        // The request on the device when Close began still gets its answers.
        await inFlight;
        Assert.Equal([10], device.RaysPerDispatch);
        Assert.Equal(1, released);
        // Refused at the call, not through the task.
        Assert.Throws<ObjectDisposedException>(
            () => { _ = batcher.TraceClosestAsync(Rays(1, 3), new HitId[1], 0, CancellationToken.None); });
    }

    [Fact]
    public async Task ARequestPartAnsweredWhenClosedFailsInsteadOfWaitingForever()
    {
        FakeDevice device = new(64);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);

        Task big = batcher.TraceClosestAsync(Rays(200, 1), new HitId[200], 0, CancellationToken.None);
        await device.FirstDispatchStarted.Task;
        Task closing = Task.Run(() => batcher.Close(() => { }));
        Assert.True(SpinWait.SpinUntil(() => batcher.IsClosed, TimeSpan.FromSeconds(30)));
        device.Gate.Set();
        await closing;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => big);
        Assert.Equal([64], device.RaysPerDispatch);
    }

    [Fact]
    public async Task AnEmptyRequestCompletesWithoutADispatch()
    {
        FakeDevice device = new(256);
        SlabBatcher batcher = new(device, Ids, 0);

        await batcher.TraceVisibilityAsync(ReadOnlyMemory<Ray>.Empty, Memory<ulong>.Empty, 0, CancellationToken.None);

        Assert.Empty(device.RaysPerDispatch);
    }
}
