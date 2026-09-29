//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//


using System.Diagnostics;

using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="SlabBatcher"/> on a CPU stand-in for the device: concurrent
/// requests share slabs, several slabs are on the device at once, every ray
/// still gets exactly its own answer, and cancellation, failure and closing
/// touch only the requests they should.
/// </summary>
public sealed partial class SlabBatcherTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // Answers each staged ray from its own fields, like a kernel whose lanes
    // are independent: any-hit when MaxDistance > 5; closest hits primitive
    // (int)OriginX at t = OriginY, or misses when DirectionX < 0. Answers are
    // computed from the slot's staging when the slab completes, not when it
    // is submitted, so a batcher that restaged a slot in flight would give
    // wrong answers as well as tripping the assertion in StageRays.
    private sealed class FakeDevice : ISlabDevice
    {
        private readonly uint[][] _staging;
        private readonly Submitted?[] _slots;
        private readonly Random _jitter = new(5);
        private int _inside;
        private int _inFlight;
        private int _submits;
        private int _completesEntered;

        public FakeDevice(int maxSlabRays, int slots = 1, int? maxInFlight = null)
        {
            MaxSlabRays = maxSlabRays;
            SlotCount = slots;
            MaxSlabsInFlight = maxInFlight ?? slots;
            _staging = [.. Enumerable.Range(0, slots).Select(_ => new uint[maxSlabRays * RayRecord.WideWords])];
            _slots = new Submitted?[slots];
        }

        public int MaxSlabRays { get; }

        public int SlotCount { get; }

        public int MaxSlabsInFlight { get; }

        /// <summary>Times the drainer staged a slot to pack it: each is a host copy of rays by the drainer.</summary>
        public int StageCalls { get; private set; }

        /// <summary>Times a slot's whole memory was handed out for callers to write.</summary>
        public int OpenCalls { get; private set; }

        /// <summary>Fails the <see cref="OpenRays"/> call with this index (0-based), as a slot whose wait failed does.</summary>
        public (int Index, Exception Error)? FailOpen { get; set; }

        /// <summary>Rays per submitted slab, in submission order; read once the drainer is parked.</summary>
        public List<int> RaysPerDispatch { get; } = [];

        /// <summary>The record of each submitted slab, in submission order.</summary>
        public List<RayRecord> RecordPerDispatch { get; } = [];

        /// <summary>Each submitted slab's staged words, as packed, in submission order.</summary>
        public List<uint[]> StagedPerDispatch { get; } = [];

        /// <summary>Slabs submitted and not yet completed.</summary>
        public int InFlight => Volatile.Read(ref _inFlight);

        /// <summary>The most slabs that were ever in flight together.</summary>
        public int MaxInFlight { get; private set; }

        /// <summary>How many times the drainer has started waiting for a slab.</summary>
        public int CompletesEntered => Volatile.Read(ref _completesEntered);

        /// <summary>Closed to hold every completion until it is set.</summary>
        public ManualResetEventSlim Gate { get; } = new(true);

        /// <summary>When set, each completion takes one permit, so a fact can land slabs one at a time.</summary>
        public SemaphoreSlim? Steps { get; set; }

        /// <summary>Signalled the first time the drainer waits for a slab.</summary>
        public TaskCompletionSource DrainerWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Fails the submit with this index (0-based, in submission order).</summary>
        public (int Index, Exception Error)? FailSubmit { get; set; }

        /// <summary>Fails the completion of the slab with this submission index.</summary>
        public (int Index, Exception Error)? FailComplete { get; set; }

        /// <summary>Sleeps a random 0-1 ms in each completion, to vary how the queue and the slots interleave.</summary>
        public bool Jitter { get; set; }

        public Memory<uint> OpenRays(int slot)
        {
            using Call call = Enter();
            Assert.Null(_slots[slot]); // a slot in flight must never be handed out
            int index = OpenCalls++;
            if (FailOpen is { } f && f.Index == index)
            {
                throw f.Error;
            }

            // Poison the whole slot, so a word the callers fail to write shows.
            Array.Fill(_staging[slot], 0xDEADBEEFu);
            return _staging[slot];
        }

        public Memory<uint> StageRays(int slot, int rayCount, RayRecord record)
        {
            using Call call = Enter();
            Assert.Null(_slots[slot]); // a slot in flight must never be restaged
            Assert.InRange(rayCount, 1, MaxSlabRays);
            StageCalls++;

            // Poison the slot, so a word the batcher fails to write shows.
            Array.Fill(_staging[slot], 0xDEADBEEFu);
            return _staging[slot].AsMemory(0, rayCount * record.Words);
        }

        public void Submit(int slot, int mode, int rayCount, int outWordCount, uint tminBits, uint tmaxScaleBits, RayRecord record)
        {
            using Call call = Enter();
            Assert.Null(_slots[slot]);
            int index = _submits++;
            RaysPerDispatch.Add(rayCount);
            RecordPerDispatch.Add(record);
            StagedPerDispatch.Add(_staging[slot][..(rayCount * record.Words)]);
            if (FailSubmit is { } f && f.Index == index)
            {
                throw f.Error;
            }

            _slots[slot] = new Submitted(mode, rayCount, outWordCount, index, record);
            MaxInFlight = Math.Max(MaxInFlight, Interlocked.Increment(ref _inFlight));
        }

        /// <summary>What <see cref="Complete"/> reports as its readback time, in ticks.</summary>
        public long ReadbackTicks { get; set; }

        /// <summary>The slab memory layout reported to the bench.</summary>
        public SlabMemoryLayout Layout { get; set; }

        public long Complete(int slot, Span<uint> outWords)
        {
            using Call call = Enter();
            Submitted sub = _slots[slot] ?? throw new InvalidOperationException("completed a slot never submitted");
            Assert.Equal(sub.Words, outWords.Length);
            Interlocked.Increment(ref _completesEntered);
            DrainerWaiting.TrySetResult();
            Assert.True(Gate.Wait(Patience));
            if (Steps is { } steps)
            {
                Assert.True(steps.Wait(Patience));
            }

            if (Jitter && _jitter.Next(3) == 0)
            {
                Thread.Sleep(_jitter.Next(2));
            }

            _slots[slot] = null;
            Interlocked.Decrement(ref _inFlight);
            if (FailComplete is { } f && f.Index == sub.Index)
            {
                throw f.Error;
            }

            // Each lane reads its ray as the kernel's load_ray does.
            uint[] staging = _staging[slot];
            int words = sub.Record.Words;
            outWords.Clear();
            for (int i = 0; i < sub.Rays; i++)
            {
                Ray r = sub.Record.Decode(staging.AsSpan(i * words, words));
                if (sub.Mode == 0)
                {
                    if (r.MaxDistance > 5)
                    {
                        outWords[(i / 64 * 2) + (i % 64 / 32)] |= 1u << (i % 32);
                    }
                }
                else
                {
                    outWords[i * 2] = r.DirectionX < 0 ? 0xFFFFFFFFu : (uint)r.OriginX;
                    outWords[(i * 2) + 1] = (uint)BitConverter.SingleToInt32Bits(r.OriginY);
                }
            }

            // A real device's readback is part of the time Complete takes, and
            // the batcher subtracts it from that time to get the fence wait.
            // Reporting it without spending it would make the fence wait come
            // out short by the whole readback, so spend it here.
            long readbackEnd = Stopwatch.GetTimestamp() + ReadbackTicks;
            while (Stopwatch.GetTimestamp() < readbackEnd)
            {
                Thread.SpinWait(64);
            }

            return ReadbackTicks;
        }

        // The batcher promises one caller at a time; overlapping calls fail the fact.
        private Call Enter()
        {
            Assert.Equal(1, Interlocked.Increment(ref _inside));
            return new Call(this);
        }

        private readonly record struct Submitted(int Mode, int Rays, int Words, int Index, RayRecord Record);

        private readonly struct Call(FakeDevice device) : IDisposable
        {
            public void Dispose() => Interlocked.Decrement(ref device._inside);
        }
    }

    private static readonly int[] Ids = [100, 101, 102, 103];

    private static readonly HitId Sentinel = new(-7, -7f);

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

    private static HitId[] Sentinels(int n)
    {
        HitId[] hits = new HitId[n];
        Array.Fill(hits, Sentinel);
        return hits;
    }

    private static void WaitForCompletes(FakeDevice device, int count) =>
        Assert.True(SpinWait.SpinUntil(() => device.CompletesEntered >= count, Patience),
            $"the drainer never reached completion {count}");

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(8, true)]
    public async Task ConcurrentRequestsOfEverySizeGetExactlyTheirOwnAnswers(int slots, bool jitter)
    {
        FakeDevice device = new(256, slots) { Jitter = jitter };
        SlabBatcher batcher = new(device, Ids, 0);
        int[] sizes = [1, 63, 64, 65, 127, 200, 256, 257, 700];
        (Ray[] Rays, ulong[] Bits, HitId[] Hits, Task Vis, Task Closest)[] calls =
            new (Ray[], ulong[], HitId[], Task, Task)[sizes.Length * 4];

        // From many threads at once, so the queue grows while slabs are out.
        System.Threading.Tasks.Parallel.For(0, calls.Length, k =>
        {
            Ray[] rays = Rays(sizes[k % sizes.Length], k);
            ulong[] bits = new ulong[((rays.Length + 63) / 64) + 1];
            bits[^1] = ulong.MaxValue; // past the request: must stay as it was
            Array.Fill(bits, ulong.MaxValue, 0, bits.Length - 1); // inside: every bit must be rewritten
            HitId[] hits = Sentinels(rays.Length);
            calls[k] = (rays, bits, hits,
                batcher.TraceVisibilityAsync(rays, bits, 0, CancellationToken.None),
                batcher.TraceClosestAsync(rays, hits, 0, CancellationToken.None));
        });

        await Task.WhenAll(calls.SelectMany(c => new[] { c.Vis, c.Closest })).WaitAsync(Patience);

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

        Assert.InRange(device.MaxInFlight, 1, slots);
        Assert.Equal(0, device.InFlight);
    }

    [Fact]
    public async Task SeveralSlabsAreOnTheDeviceAtOnce()
    {
        FakeDevice device = new(64, 3);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);
        Ray[] rays = Rays(1000, 1);
        HitId[] hits = new HitId[rays.Length];
        Ray[] visRays = Rays(500, 2);
        ulong[] bits = new ulong[8];

        Task closest = batcher.TraceClosestAsync(rays, hits, 0, CancellationToken.None);
        Task vis = batcher.TraceVisibilityAsync(visRays, bits, 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);

        // Every slot was filled before the drainer waited for the first.
        Assert.Equal(3, device.InFlight);
        Assert.Equal([64, 64, 64], device.RaysPerDispatch);

        device.Gate.Set();
        await Task.WhenAll(closest, vis).WaitAsync(Patience);
        Assert.Equal(rays.Select(Closest), hits);
        for (int i = 0; i < visRays.Length; i++)
        {
            Assert.Equal(Hit(visRays[i]), (bits[i / 64] & (1UL << (i % 64))) != 0);
        }

        Assert.Equal(3, device.MaxInFlight);
        Assert.Equal(16 + 8, batcher.Dispatches);
    }

    [Fact]
    public async Task ASplitRequestCompletesOnlyWhenItsLastSlabLandsAndLaterRequestsFollowIt()
    {
        FakeDevice device = new(64, 2) { Steps = new SemaphoreSlim(0) };
        SlabBatcher batcher = new(device, Ids, 0);
        Ray[] big = Rays(200, 1);
        Ray[] small = Rays(30, 2);
        HitId[] bigHits = new HitId[big.Length];
        HitId[] smallHits = new HitId[small.Length];

        Task a = batcher.TraceClosestAsync(big, bigHits, 0, CancellationToken.None);
        WaitForCompletes(device, 1);
        Task b = batcher.TraceClosestAsync(small, smallHits, 0, CancellationToken.None);

        // Slabs land one at a time; the big request's parts are answered, in
        // order, while it stays pending.
        for (int landed = 1; landed <= 3; landed++)
        {
            device.Steps.Release();
            WaitForCompletes(device, landed + 1);
            Assert.False(a.IsCompleted, $"completed after {landed} of its 4 slabs");
            Assert.False(b.IsCompleted);
        }

        device.Steps.Release();
        await Task.WhenAll(a, b).WaitAsync(Patience);

        // Oldest first: the small request rides in the big one's last slab.
        Assert.Equal([64, 64, 64, 8 + 30], device.RaysPerDispatch);
        Assert.Equal(big.Select(Closest), bigHits);
        Assert.Equal(small.Select(Closest), smallHits);
    }

    [Fact]
    public async Task RequestsThatQueueWhileTheDeviceWorksShareASlab()
    {
        FakeDevice device = new(4096);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);

        Task first = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        Task[] queued = [.. Enumerable.Range(0, 20).Select(k => batcher.TraceClosestAsync(Rays(30, k), new HitId[30], 0, CancellationToken.None))];
        device.Gate.Set();
        await Task.WhenAll([first, .. queued]).WaitAsync(Patience);

        Assert.Equal([10, 600], device.RaysPerDispatch);
        Assert.Equal(2, batcher.Dispatches);
    }

    [Fact]
    public async Task TheStatisticsCountRequestsSlabsThePeakAndTheSpansTheDeviceWasBusy()
    {
        FakeDevice device = new(64, 3);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);
        Assert.Equal(new GpuTraceStatistics(0, 0, TimeSpan.Zero, TimeSpan.Zero, 0, 3), batcher.Statistics);
        Assert.False(batcher.Statistics.RaysInPlace);

        // 1000 closest rays are 16 slabs of 64; the drainer fills all three
        // slots before it waits on the first, and waits behind a closed gate.
        Task closest = batcher.TraceClosestAsync(Rays(1000, 1), new HitId[1000], 0, CancellationToken.None);
        Task empty = batcher.TraceClosestAsync(ReadOnlyMemory<Ray>.Empty, Memory<HitId>.Empty, 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);

        // A span still open is counted up to the read.
        Thread.Sleep(20);
        GpuTraceStatistics during = batcher.Statistics;
        Assert.True(during.Busy >= TimeSpan.FromMilliseconds(15), $"busy {during.Busy} while a slab waits");
        Assert.Equal(3, during.PeakSlabsInFlight);

        device.Gate.Set();
        await Task.WhenAll(closest, empty).WaitAsync(Patience);
        GpuTraceStatistics after = batcher.Statistics;

        // An empty request is answered without the device and is not counted.
        Assert.Equal(1, after.Requests);
        Assert.Equal(16, after.Slabs);
        Assert.Equal(batcher.Dispatches, after.Slabs);
        Assert.Equal(3, after.PeakSlabsInFlight);
        Assert.Equal(3, after.Slots);

        // The wait behind the gate was the drainer blocked on a fence, inside
        // the span the device was busy; with nothing in flight the span is
        // closed, so a later read does not grow it.
        Assert.True(after.FenceWait >= TimeSpan.FromMilliseconds(15), $"fence wait {after.FenceWait}");
        Assert.True(after.Busy >= after.FenceWait, $"busy {after.Busy} < fence wait {after.FenceWait}");
        Thread.Sleep(20);
        Assert.Equal(after.Busy, batcher.Statistics.Busy);
    }

    [Fact]
    public async Task ThePackAndReadbackTimesAreTheHostsCopiesAndTheLayoutIsTheDevices()
    {
        long tick = Stopwatch.Frequency / 1000;
        FakeDevice device = new(64, 2) { ReadbackTicks = 3 * tick, Layout = new SlabMemoryLayout(true, false) };
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);

        Task closest = batcher.TraceClosestAsync(Rays(200, 1), new HitId[200], 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        Thread.Sleep(30);
        device.Gate.Set();
        await closest.WaitAsync(Patience);
        GpuTraceStatistics stats = batcher.Statistics;

        // Four slabs, each reporting 3 ms of readback; the fence wait is the
        // rest of each Complete, so it does not count the readback twice.
        Assert.Equal(4, stats.Slabs);
        Assert.Equal(TimeSpan.FromMilliseconds(12), stats.Readback);
        Assert.True(stats.FenceWait >= TimeSpan.FromMilliseconds(20), $"fence wait {stats.FenceWait}");
        Assert.True(stats.RaysInPlace);
        Assert.False(stats.AnswersInPlace);
    }

    [Fact]
    public async Task AFailedCompletionStillClosesTheBusySpan()
    {
        FakeDevice device = new(256) { FailComplete = (0, new InvalidOperationException("device lost")) };
        SlabBatcher batcher = new(device, Ids, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None).WaitAsync(Patience));

        TimeSpan busy = batcher.Statistics.Busy;
        Thread.Sleep(20);
        Assert.Equal(busy, batcher.Statistics.Busy);
        Assert.Equal(1, batcher.Statistics.Slabs);
    }

    [Fact]
    public void VisibilitySegmentsStartOnAWorkgroupAndSplitOnOne()
    {
        List<SlabBatcher.Segment> slab = [];
        SlabBatcher.Request a = new(0, Rays(70, 1), new ulong[2], default, 0, default);
        SlabBatcher.Request b = new(0, Rays(200, 2), new ulong[4], default, 0, default);
        SlabBatcher.Request other = new(1, Rays(5, 3), default, new HitId[5], 0, default);
        SlabBatcher.Request epsilon = new(0, Rays(5, 4), new ulong[1], default, 7, default);

        int total = SlabBatcher.Plan([a, other, epsilon, b], 0, 0, null, 256, slab);

        Assert.Equal(
            [new SlabBatcher.Segment(a, 0, 70, 0), new SlabBatcher.Segment(b, 0, 128, 128)],
            slab);
        Assert.Equal(256, total);

        b.Next = 128;
        total = SlabBatcher.Plan([b], 0, 0, null, 256, slab);
        Assert.Equal([new SlabBatcher.Segment(b, 128, 72, 0)], slab);
        Assert.Equal(72, total);
    }

    [Fact]
    public void ClosestSegmentsPackWithoutGaps()
    {
        List<SlabBatcher.Segment> slab = [];
        SlabBatcher.Request a = new(1, Rays(70, 1), default, new HitId[70], 0, default);
        SlabBatcher.Request b = new(1, Rays(300, 2), default, new HitId[300], 0, default);

        int total = SlabBatcher.Plan([a, b], 1, 0, null, 256, slab);

        Assert.Equal([new SlabBatcher.Segment(a, 0, 70, 0), new SlabBatcher.Segment(b, 0, 186, 70)], slab);
        Assert.Equal(256, total);
    }

    [Fact]
    public void ARequestWhoseRaysAreAllOnTheDeviceTakesNoRoomInTheNextSlab()
    {
        List<SlabBatcher.Segment> slab = [];
        SlabBatcher.Request launched = new(0, Rays(10, 1), new ulong[1], default, 0, default) { Next = 10 };
        SlabBatcher.Request waiting = new(0, Rays(10, 2), new ulong[1], default, 0, default);

        int total = SlabBatcher.Plan([launched, waiting], 0, 0, null, 256, slab);

        // Not even the 64-ray alignment a visibility segment would have cost.
        Assert.Equal([new SlabBatcher.Segment(waiting, 0, 10, 0)], slab);
        Assert.Equal(10, total);
    }

    [Fact]
    public async Task ACancelledRequestIsDroppedBeforeItsSlab()
    {
        FakeDevice device = new(256);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);
        using CancellationTokenSource cancel = new();

        Task first = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        Task doomed = batcher.TraceClosestAsync(Rays(10, 2), new HitId[10], 0, cancel.Token);
        await cancel.CancelAsync();
        device.Gate.Set();

        await first.WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed.WaitAsync(Patience));
        Assert.Equal([10], device.RaysPerDispatch);
    }

    [Fact]
    public async Task ARequestCancelledWithSlabsInFlightIsNeverWrittenAgain()
    {
        FakeDevice device = new(64, 2) { Steps = new SemaphoreSlim(0) };
        SlabBatcher batcher = new(device, Ids, 0);
        using CancellationTokenSource cancel = new();

        Task blocker = batcher.TraceClosestAsync(Rays(64, 1), new HitId[64], 0, CancellationToken.None);
        WaitForCompletes(device, 1);
        Ray[] rays = Rays(300, 2);
        HitId[] hits = Sentinels(rays.Length);
        Task doomed = batcher.TraceClosestAsync(rays, hits, 0, cancel.Token);

        // The blocker lands; the doomed request's first two slabs go out and
        // the drainer waits on the first of them.
        device.Steps.Release();
        WaitForCompletes(device, 2);
        await cancel.CancelAsync();
        device.Steps.Release(10);

        await blocker.WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed.WaitAsync(Patience));
        Assert.True(SpinWait.SpinUntil(() => device.InFlight == 0, Patience));

        // The slab being waited on when the cancel came was still the
        // request's, and was written; the one behind it landed after the
        // task was cancelled and was not; nothing else was launched.
        Assert.Equal([64, 64, 64], device.RaysPerDispatch);
        Assert.Equal(rays.Take(64).Select(Closest), hits.Take(64));
        Assert.All(hits.Skip(64), h => Assert.Equal(Sentinel, h));
    }

    [Fact]
    public async Task AFailedDispatchFaultsOnlyItsRequests()
    {
        FakeDevice device = new(256) { FailComplete = (0, new InvalidOperationException("device lost")) };
        SlabBatcher batcher = new(device, Ids, 0);

        Task failed = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(Patience));

        HitId[] hits = new HitId[10];
        Ray[] rays = Rays(10, 2);
        await batcher.TraceClosestAsync(rays, hits, 0, CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(rays.Select(Closest), hits);
    }

    [Fact]
    public async Task AFailedSubmitFreesItsSlotAndFaultsOnlyItsRequests()
    {
        FakeDevice device = new(256) { FailSubmit = (0, new InvalidOperationException("submit refused")) };
        SlabBatcher batcher = new(device, Ids, 0);

        Task failed = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(Patience));

        // The one slot is usable again: the submit never reached the device.
        HitId[] hits = new HitId[10];
        Ray[] rays = Rays(10, 2);
        await batcher.TraceClosestAsync(rays, hits, 0, CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(rays.Select(Closest), hits);
        Assert.Equal(0, device.InFlight);
    }

    [Fact]
    public async Task AFailedSlabAmongSeveralInFlightFaultsOnlyItsRequestsAndStopsWritingThem()
    {
        FakeDevice device = new(64, 3) { Steps = new SemaphoreSlim(0) };
        SlabBatcher batcher = new(device, Ids, 0);

        Ray[] firstRays = Rays(64, 1);
        HitId[] firstHits = new HitId[64];
        Task first = batcher.TraceClosestAsync(firstRays, firstHits, 0, CancellationToken.None);
        WaitForCompletes(device, 1);

        // Queued while the first slab is out: the big request takes the next
        // three slabs (submissions 1, 2, 3) and the last request waits for a
        // slot. Submission 2, the big request's middle, fails.
        Ray[] bigRays = Rays(192, 2);
        HitId[] bigHits = Sentinels(192);
        Ray[] lastRays = Rays(10, 3);
        HitId[] lastHits = new HitId[10];
        device.FailComplete = (2, new InvalidOperationException("device lost"));
        Task big = batcher.TraceClosestAsync(bigRays, bigHits, 0, CancellationToken.None);
        Task last = batcher.TraceClosestAsync(lastRays, lastHits, 0, CancellationToken.None);
        device.Steps.Release(10);

        await first.WaitAsync(Patience);
        await last.WaitAsync(Patience);
        await Assert.ThrowsAsync<InvalidOperationException>(() => big.WaitAsync(Patience));
        Assert.True(SpinWait.SpinUntil(() => device.InFlight == 0, Patience));

        Assert.Equal([64, 64, 64, 64, 10], device.RaysPerDispatch);
        Assert.Equal(3, device.MaxInFlight);
        Assert.Equal(firstRays.Select(Closest), firstHits);
        Assert.Equal(lastRays.Select(Closest), lastHits);

        // The slab after the failed one landed on a faulted request, and
        // wrote nothing into memory its caller already owns again.
        Assert.All(bigHits.Skip(128), h => Assert.Equal(Sentinel, h));
    }

    [Fact]
    public async Task ClosingFailsQueuedRequestsAndReleasesTheDeviceOnce()
    {
        FakeDevice device = new(256);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);
        int released = 0;

        Task inFlight = batcher.TraceClosestAsync(Rays(10, 1), new HitId[10], 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        Task queued = batcher.TraceClosestAsync(Rays(10, 2), new HitId[10], 0, CancellationToken.None);

        Task closing = Task.Run(() => batcher.Close(() => released++));

        // Close has begun (and is waiting out the dispatch) before the device finishes.
        Assert.True(SpinWait.SpinUntil(() => batcher.IsClosed, Patience));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(Patience));
        device.Gate.Set();
        await closing.WaitAsync(Patience);
        batcher.Close(() => released++);

        // The request on the device when Close began still gets its answers.
        await inFlight.WaitAsync(Patience);
        Assert.Equal([10], device.RaysPerDispatch);
        Assert.Equal(1, released);
        // Refused at the call, not through the task.
        Assert.Throws<ObjectDisposedException>(
            () => { _ = batcher.TraceClosestAsync(Rays(1, 3), new HitId[1], 0, CancellationToken.None); });
    }

    [Fact]
    public async Task ClosingWithSeveralSlabsInFlightLandsThemAllBeforeReleasingTheDevice()
    {
        FakeDevice device = new(64, 3) { Steps = new SemaphoreSlim(0) };
        SlabBatcher batcher = new(device, Ids, 0);
        int released = 0;
        int inFlightAtRelease = -1;

        Task blocker = batcher.TraceClosestAsync(Rays(64, 1), new HitId[64], 0, CancellationToken.None);
        WaitForCompletes(device, 1);

        // Queued behind the blocker. Once it lands, three slabs go out:
        // whole[0..64), whole[64..100) + part[0..28), part[28..92). The whole
        // request is then entirely on the device, the part request is not,
        // and the queued one never gets a slot.
        Ray[] wholeRays = Rays(100, 2);
        HitId[] wholeHits = new HitId[100];
        Task whole = batcher.TraceClosestAsync(wholeRays, wholeHits, 0, CancellationToken.None);
        Task part = batcher.TraceClosestAsync(Rays(200, 3), new HitId[200], 0, CancellationToken.None);
        Task queued = batcher.TraceVisibilityAsync(Rays(10, 4), new ulong[1], 0, CancellationToken.None);
        device.Steps.Release();
        WaitForCompletes(device, 2);
        Assert.Equal(3, device.InFlight);

        Task closing = Task.Run(() => batcher.Close(() =>
        {
            released++;
            inFlightAtRelease = device.InFlight;
        }));
        Assert.True(SpinWait.SpinUntil(() => batcher.IsClosed, Patience));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(Patience));
        Assert.False(closing.IsCompleted, "the device was released with slabs still on it");

        device.Steps.Release(10);
        await closing.WaitAsync(Patience);

        await blocker.WaitAsync(Patience);
        await whole.WaitAsync(Patience);
        Assert.Equal(wholeRays.Select(Closest), wholeHits);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => part.WaitAsync(Patience));
        Assert.Equal([64, 64, 64, 64], device.RaysPerDispatch);
        Assert.Equal(1, released);
        Assert.Equal(0, inFlightAtRelease);
    }

    [Fact]
    public async Task ARequestPartAnsweredWhenClosedFailsInsteadOfWaitingForever()
    {
        FakeDevice device = new(64);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);

        Task big = batcher.TraceClosestAsync(Rays(200, 1), new HitId[200], 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        Task closing = Task.Run(() => batcher.Close(() => { }));
        Assert.True(SpinWait.SpinUntil(() => batcher.IsClosed, Patience));
        device.Gate.Set();
        await closing.WaitAsync(Patience);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => big.WaitAsync(Patience));
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

    /// <summary>Rays like <see cref="Rays"/> but every one with the same reach.</summary>
    private static Ray[] RaysWithReach(int n, int seed, float reach) =>
        [.. Rays(n, seed).Select(r => r with { MaxDistance = reach })];

    [Fact]
    public void OnlyRequestsWithTheSlabsReachShareIt()
    {
        List<SlabBatcher.Segment> slab = [];
        SlabBatcher.Request one = new(1, RaysWithReach(10, 1, 1f), default, new HitId[10], 0, default);
        SlabBatcher.Request mixed = new(1, Rays(10, 2), default, new HitId[10], 0, default);
        SlabBatcher.Request two = new(1, RaysWithReach(10, 3, 2f), default, new HitId[10], 0, default);
        SlabBatcher.Request oneAgain = new(1, RaysWithReach(10, 4, 1f), default, new HitId[10], 0, default);
        SlabBatcher.Request mixedAgain = new(1, Rays(10, 5), default, new HitId[10], 0, default);
        SlabBatcher.Request[] queue = [one, mixed, two, oneAgain, mixedAgain];

        Assert.Equal(0x3F800000u, one.UniformReach);
        Assert.Null(mixed.UniformReach);
        Assert.Equal(0x40000000u, two.UniformReach);

        Assert.Equal(20, SlabBatcher.Plan(queue, 1, 0, 0x3F800000u, 256, slab));
        Assert.Equal([new SlabBatcher.Segment(one, 0, 10, 0), new SlabBatcher.Segment(oneAgain, 0, 10, 10)], slab);

        Assert.Equal(20, SlabBatcher.Plan(queue, 1, 0, null, 256, slab));
        Assert.Equal([new SlabBatcher.Segment(mixed, 0, 10, 0), new SlabBatcher.Segment(mixedAgain, 0, 10, 10)], slab);

        Assert.Equal(10, SlabBatcher.Plan(queue, 1, 0, 0x40000000u, 256, slab));
        Assert.Equal([new SlabBatcher.Segment(two, 0, 10, 0)], slab);
    }

    [Fact]
    public async Task ASlabOfOneSharedReachGoesNarrowAndAnyOtherWide()
    {
        FakeDevice device = new(4096);
        device.Gate.Reset();
        SlabBatcher batcher = new(device, Ids, 0);

        // Queue all three behind the first slab, so the planner sees them together.
        Ray[] first = RaysWithReach(10, 1, 7f);
        Task blocker = batcher.TraceClosestAsync(first, new HitId[10], 0, CancellationToken.None);
        await device.DrainerWaiting.Task.WaitAsync(Patience);
        Ray[] narrow = RaysWithReach(100, 2, 1f);
        Ray[] wide = Rays(100, 3);
        Ray[] narrowToo = RaysWithReach(50, 4, 1f);
        HitId[][] hits = [new HitId[100], new HitId[100], new HitId[50]];
        Task[] queued =
        [
            batcher.TraceClosestAsync(narrow, hits[0], 0, CancellationToken.None),
            batcher.TraceClosestAsync(wide, hits[1], 0, CancellationToken.None),
            batcher.TraceClosestAsync(narrowToo, hits[2], 0, CancellationToken.None),
        ];
        device.Gate.Set();
        await Task.WhenAll([blocker, .. queued]).WaitAsync(Patience);

        Assert.Equal(
            [RayRecord.UniformReach(0x40E00000u), RayRecord.UniformReach(0x3F800000u), RayRecord.Wide],
            device.RecordPerDispatch);
        Assert.Equal([10, 150, 100], device.RaysPerDispatch);

        // The narrow slab is the two requests' rays, 24 bytes each, reach left out.
        uint[] expected = new uint[150 * 6];
        RayRecord.UniformReach(0x3F800000u).Pack(narrow, expected);
        RayRecord.UniformReach(0x3F800000u).Pack(narrowToo, expected.AsSpan(600));
        Assert.Equal(expected, device.StagedPerDispatch[1]);

        // And every ray still gets exactly its own answer.
        Assert.Equal(narrow.Select(Closest), hits[0]);
        Assert.Equal(wide.Select(Closest), hits[1]);
        Assert.Equal(narrowToo.Select(Closest), hits[2]);

        GpuTraceStatistics stats = batcher.Statistics;
        Assert.Equal(260, stats.SlabRays);
        Assert.Equal((160 * 24) + (100 * 28), stats.RayBytes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task ASlabLargerThanAChunkIsPackedInPiecesToTheSameWords(int mode, bool narrow)
    {
        // Three whole chunks and a part: the pack goes to the pool in four
        // pieces, and the words must be those of one serial pack.
        int n = (3 * SlabBatcher.PackChunkRays) + 100;
        FakeDevice device = new(n + 60);
        SlabBatcher batcher = new(device, Ids, 0);
        Ray[] rays = narrow ? RaysWithReach(n, 9, 6f) : Rays(n, 9);
        ulong[] bits = new ulong[(n + 63) / 64];
        HitId[] hits = new HitId[n];

        await (mode == 0
            ? batcher.TraceVisibilityAsync(rays, bits, 0, CancellationToken.None)
            : batcher.TraceClosestAsync(rays, hits, 0, CancellationToken.None)).WaitAsync(Patience);

        RayRecord record = narrow ? RayRecord.UniformReach(0x40C00000u) : RayRecord.Wide;
        uint[] expected = new uint[n * record.Words];
        record.Pack(rays, expected);
        Assert.Equal([record], device.RecordPerDispatch);
        Assert.Equal(expected, device.StagedPerDispatch[0]);
        if (mode == 0)
        {
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(Hit(rays[i]), (bits[i / 64] & (1UL << (i % 64))) != 0);
            }
        }
        else
        {
            Assert.Equal(rays.Select(Closest), hits);
        }
    }

    /// <summary>
    /// A visibility slab with alignment gaps, packed whole and in every way
    /// of cutting it into ranges, including cuts inside a gap and inside a
    /// segment: the words never depend on the cut.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackingASlabInRangesWritesWhatOnePackWrites(bool narrow)
    {
        Ray[] Make(int count, int seed) => narrow ? RaysWithReach(count, seed, 3f) : Rays(count, seed);
        SlabBatcher.Request a = new(0, Make(70, 1), new ulong[2], default, 0, default);
        SlabBatcher.Request b = new(0, Make(5, 2), new ulong[1], default, 0, default);
        SlabBatcher.Request c = new(0, Make(90, 3), new ulong[2], default, 0, default);
        List<SlabBatcher.Segment> segments = [];
        uint? reach = narrow ? 0x40400000u : null;
        int total = SlabBatcher.Plan([a, b, c], 0, 0, reach, 1024, segments);
        Assert.Equal(128 + 64 + 90, total);
        RayRecord record = RayRecord.For(reach);
        int words = record.Words;

        uint[] whole = new uint[total * words];
        SlabBatcher.Pack(segments, record, whole, 0, total);

        // Position by position: each segment's rays at its offset, and in a
        // gap, the next segment's first ray.
        Ray Expected(int at) => at switch
        {
            < 70 => a.Rays.Span[at],
            < 128 => b.Rays.Span[0],
            < 133 => b.Rays.Span[at - 128],
            < 192 => c.Rays.Span[0],
            _ => c.Rays.Span[at - 192],
        };
        for (int at = 0; at < total; at++)
        {
            Assert.Equal(Expected(at), record.Decode(whole.AsSpan(at * words, words)));
        }

        Random random = new(11);
        for (int trial = 0; trial < 50; trial++)
        {
            int[] cuts = [0, .. Enumerable.Range(0, random.Next(1, 6)).Select(_ => random.Next(total + 1)).Order(), total];
            uint[] pieces = new uint[total * words];
            Array.Fill(pieces, 0xDEADBEEFu);
            for (int k = 0; k + 1 < cuts.Length; k++)
            {
                SlabBatcher.Pack(segments, record, pieces, cuts[k], cuts[k + 1]);
            }

            Assert.Equal(whole, pieces);
        }

        // A range writes only its own positions.
        uint[] one = new uint[total * words];
        Array.Fill(one, 0xDEADBEEFu);
        SlabBatcher.Pack(segments, record, one, 100, 140);
        for (int at = 0; at < total; at++)
        {
            bool inside = at is >= 100 and < 140;
            Assert.Equal(
                inside,
                one.AsSpan(at * words, words).ToArray().SequenceEqual(whole.AsSpan(at * words, words).ToArray()));
        }
    }

    /// <summary>
    /// Packing, serial or shared, runs on the batcher's own threads and never
    /// on the thread pool, even when every request comes from a pool thread.
    /// </summary>
    [Fact]
    public async Task NoPackingRunsOnTheThreadPool()
    {
        int n = (3 * SlabBatcher.PackChunkRays) + 100;
        FakeDevice device = new(n + 60);
        SlabBatcher batcher = new(device, Ids, 0);
        System.Collections.Concurrent.ConcurrentBag<(int Chunk, bool Pool, int Thread)> seen = [];
        batcher.ObservePackChunk = c => seen.Add((c, Thread.CurrentThread.IsThreadPoolThread, Environment.CurrentManagedThreadId));
        Ray[] big = Rays(n, 3);
        Ray[] small = Rays(100, 4);
        try
        {
            await Task.Run(() => batcher.TraceClosestAsync(big, new HitId[n], 0, CancellationToken.None)).WaitAsync(Patience);
            await Task.Run(() => batcher.TraceClosestAsync(small, new HitId[100], 0, CancellationToken.None)).WaitAsync(Patience);
        }
        finally
        {
            batcher.Close(() => { });
        }

        // Four chunks of the big slab and the small slab's one.
        Assert.Equal([0, 0, 1, 2, 3], seen.Select(x => x.Chunk).Order());
        Assert.All(seen, x => Assert.False(x.Pool, $"chunk {x.Chunk} was packed on a pool thread"));
        int[] ours = [.. batcher.Threads.Select(t => t.ManagedThreadId)];
        Assert.All(seen, x => Assert.Contains(x.Thread, ours));
    }

    /// <summary>
    /// Close joins every thread the batcher started, the drainer and the
    /// pack helpers, whether its slabs succeeded or failed.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TheBatchersThreadsAreGoneAfterClose(int failure)
    {
        int n = (2 * SlabBatcher.PackChunkRays) + 7;
        FakeDevice device = new(n + 57);
        if (failure == 1)
        {
            device.FailSubmit = (1, new InvalidOperationException("submit"));
        }
        else if (failure == 2)
        {
            device.FailComplete = (1, new InvalidOperationException("complete"));
        }

        SlabBatcher batcher = new(device, Ids, 0);
        Task first = batcher.TraceClosestAsync(Rays(n, 5), new HitId[n], 0, CancellationToken.None);
        await first.WaitAsync(Patience);
        Task second = batcher.TraceClosestAsync(Rays(n, 6), new HitId[n], 0, CancellationToken.None);
        if (failure == 0)
        {
            await second.WaitAsync(Patience);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Patience));
        }

        IReadOnlyList<Thread> threads = batcher.Threads;
        Assert.Equal(SlabBatcher.MaxPackThreads, threads.Count); // the drainer and its helpers
        Assert.All(threads, t => Assert.True(t.IsAlive));

        batcher.Close(() => { });

        Assert.All(threads, t => Assert.False(t.IsAlive, $"{t.Name} outlived Close"));
        batcher.Close(() => { }); // a second close finds nothing to do
    }

    [Fact]
    public void ABatcherThatNeverTracedStartsNoThreads()
    {
        SlabBatcher batcher = new(new FakeDevice(64), Ids, 0);
        batcher.Close(() => { });

        Assert.Empty(batcher.Threads);
    }

    [Fact]
    public void ADeviceWithNoSlotsIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlabBatcher(new FakeDevice(64, 0), Ids, 0));
}
