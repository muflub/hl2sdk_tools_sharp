//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
using System.Runtime.InteropServices;

using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Gpu;

/// <summary>
/// The part of a device a <see cref="SlabBatcher"/> drives: a fixed ring of
/// slab slots, each with its own staging, device buffers and fence, so one
/// slot can be packed or scattered while others are on the device.
/// </summary>
/// <remarks>
/// A slot moves through stage, submit, complete, in that order, and is
/// never staged or submitted again until its completion has returned (or
/// its submit has thrown). The batcher makes every call from one thread at
/// a time, so an implementation needs no locking of its own.
/// </remarks>
internal interface ISlabDevice
{
    /// <summary>Rays one slot carries at most; a whole number of 64-ray workgroups.</summary>
    int MaxSlabRays { get; }

    /// <summary>How many slots the device has; at least 1.</summary>
    int SlotCount { get; }

    /// <summary>The staging for a slot's next dispatch, 8 floats per ray.</summary>
    /// <param name="slot">The slot; it must not be in flight.</param>
    /// <param name="rayCount">How many rays the dispatch will carry.</param>
    /// <returns>The span to fill.</returns>
    Span<float> StageRays(int slot, int rayCount);

    /// <summary>Starts tracing a slot's staged rays and returns without waiting for them.</summary>
    /// <param name="slot">The slot staged last.</param>
    /// <param name="mode">0 any-hit bits, 1 closest hit.</param>
    /// <param name="rayCount">Rays staged.</param>
    /// <param name="outWordCount">Words the dispatch returns: 2 per 64-ray workgroup (mode 0) or per ray (mode 1).</param>
    /// <param name="tminBits">The ray epsilon, as float bits.</param>
    /// <param name="tmaxScaleBits">The any-hit tmax scale, as float bits.</param>
    void Submit(int slot, int mode, int rayCount, int outWordCount, uint tminBits, uint tmaxScaleBits);

    /// <summary>Waits for a submitted slot and reads its answers back; the slot is free afterwards.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="outWords">Receives the words its submit promised.</param>
    /// <returns>
    /// <see cref="System.Diagnostics.Stopwatch"/> ticks spent reading the
    /// answers out once the wait was over, for the bench; the rest of the
    /// call is the wait.
    /// </returns>
    long Complete(int slot, Span<uint> outWords);

    /// <summary>
    /// Whether the kernel reads rays where <see cref="StageRays"/> hands them
    /// out (no upload copy), and writes answers where
    /// <see cref="Complete"/> reads them (no download copy). For the bench:
    /// a device that stages both ways moves every slab twice.
    /// </summary>
    SlabMemoryLayout Layout => default;
}

/// <summary>
/// Feeds a device from many concurrent callers: requests queue, and one
/// drainer packs every queued request of the same kind into each slab,
/// keeping up to <see cref="ISlabDevice.SlotCount"/> slabs on the device.
/// </summary>
/// <remarks>
/// <para>
/// vrad's workers each hand the tracer a small batch (a few thousand rays)
/// and park until it is answered. Each dispatch is a submit and a fence wait
/// whose cost barely depends on the ray count, so answering the batches one
/// by one spends the device on overhead and queues every worker behind one
/// lock. Measured on ss_sandbox on a 32-thread machine, that design
/// allocated about 25 GB (every batch was copied into and out of fresh
/// arrays), ran 268 gen-2 collections, and spent 375 s of thread time
/// waiting on the lock, for a lighting pass the CPU tracer finished several
/// times faster.
/// </para>
/// <para>
/// Here no caller thread blocks and nothing is copied: a request keeps the
/// caller's memory, the drainer packs rays straight from it into the
/// device's staging, and writes the answers straight back. While slabs are
/// on the device, the next requests queue, so the busier the device, the
/// fuller its slabs.
/// </para>
/// <para>
/// The drainer keeps several slabs in flight. With one slab at a time the
/// device idled while the drainer packed the next slab and scattered the
/// last, and the drainer idled while the device worked: the two costs added
/// up, and vrad's workers spent their time parked. Now the drainer launches
/// a slab whenever a slot is free and work is queued, and waits for the
/// oldest slab only when every slot is busy or nothing is left to launch, so
/// the device has the next slab queued behind the one it is running while
/// the drainer packs and scatters. Slabs are waited for in the order they
/// were launched, which is the order one queue runs them in.
/// </para>
/// <para>
/// Every ray's answer is a function of that ray alone (the kernel reads no
/// other lane), so how requests share a slab, or how many slabs are in
/// flight, cannot change a bit or a hit. The one layout rule is the any-hit
/// output: each 64-ray workgroup writes two words, so a visibility request
/// starts on a 64-ray boundary of its slab, the gap before it is filled
/// with copies of its first ray, and the bits past its last ray are masked
/// off, which leaves them zero, as a dispatch of that request alone would.
/// </para>
/// <para>
/// A request larger than a slab is split across slabs, and those slabs may
/// be in flight together; it completes when the last of them is answered.
/// A request is cancelled between slabs (a dispatch in flight cannot be
/// recalled), and a failed slab faults exactly the requests it carried.
/// Once a request's task has completed, whether cancelled, faulted or
/// closed, no later slab writes into its memory: the caller owns it again
/// the moment the task says so.
/// </para>
/// </remarks>
internal sealed class SlabBatcher
{
    private const int WorkgroupRays = 64;

    private readonly ISlabDevice _device;
    private readonly int[] _triangleIds;
    private readonly uint _tmaxScaleBits;
    private readonly uint[] _words;
    private readonly object _queueLock = new();
    private readonly object _deviceLock = new();
    private readonly List<Request> _queue = [];

    // The drainer's own state, touched only under the device lock: the free
    // slots, and the slabs on the device, oldest first.
    private readonly Queue<int> _freeSlots = new();
    private readonly Queue<Slab> _inFlight = new();
    private bool _draining;
    private bool _closed;

    /// <summary>Wraps a device.</summary>
    /// <param name="device">The device; its calls are made only from the drainer.</param>
    /// <param name="triangleIds">The caller's id for each BLAS primitive.</param>
    /// <param name="tmaxScaleBits">The any-hit tmax scale every dispatch passes.</param>
    public SlabBatcher(ISlabDevice device, int[] triangleIds, uint tmaxScaleBits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(device.SlotCount, 1);
        _device = device;
        _triangleIds = triangleIds;
        _tmaxScaleBits = tmaxScaleBits;

        // 2 words per ray covers the closest mode, and any-hit's 2 per 64.
        // One buffer serves every slot: slabs are scattered one at a time.
        _words = new uint[device.MaxSlabRays * 2];
        for (int slot = 0; slot < device.SlotCount; slot++)
        {
            _freeSlots.Enqueue(slot);
        }
    }

    /// <summary>Whether <see cref="Close"/> has begun; new requests are refused from then on.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_queueLock)
            {
                return _closed;
            }
        }
    }

    /// <summary>Slabs submitted so far, for facts and telemetry.</summary>
    public int Dispatches => Volatile.Read(ref _dispatches);

    private int _dispatches;

    // The bench's counters (GpuTraceStatistics says what each means). The
    // drainer alone writes the tick counts and the peak, one thread at a
    // time under the device lock; the bench reads them from another thread,
    // hence the interlocked reads and writes rather than a lock.
    private long _requests;
    private long _busyTicks;
    private long _fenceWaitTicks;
    private long _busySince;
    private long _packTicks;
    private long _readbackTicks;
    private int _peakInFlight;

    /// <summary>What the device has done so far: the bench's GPU line.</summary>
    /// <remarks>
    /// Busy is the union of the spans with a slab on the device, measured on
    /// the host between the submit that took the device from idle and the
    /// landing that emptied it; <see cref="GpuTraceStatistics"/> says why the
    /// host's span and not device timestamps. A span still open when this is
    /// read is counted up to now.
    /// </remarks>
    public GpuTraceStatistics Statistics
    {
        get
        {
            long busy = Interlocked.Read(ref _busyTicks);
            long since = Interlocked.Read(ref _busySince);
            if (since != 0)
            {
                busy += Stopwatch.GetTimestamp() - since;
            }

            return new GpuTraceStatistics(
                Interlocked.Read(ref _requests),
                Volatile.Read(ref _dispatches),
                Stopwatch.GetElapsedTime(0, busy),
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _fenceWaitTicks)),
                Volatile.Read(ref _peakInFlight),
                _device.SlotCount,
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _packTicks)),
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _readbackTicks)),
                _device.Layout.DirectRays,
                _device.Layout.DirectOut);
        }
    }

    /// <summary>Queues an any-hit request.</summary>
    /// <param name="rays">The rays; read until the task completes.</param>
    /// <param name="hitBits">Receives one bit per ray; written only in the words the rays cover.</param>
    /// <param name="tminBits">The ray epsilon as float bits.</param>
    /// <param name="cancellationToken">Cancels the request between slabs.</param>
    /// <returns>Completes when every bit has been written.</returns>
    public Task TraceVisibilityAsync(ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, uint tminBits, CancellationToken cancellationToken) =>
        Enqueue(new Request(0, rays, hitBits, default, tminBits, cancellationToken));

    /// <summary>Queues a closest-hit request.</summary>
    /// <param name="rays">The rays; read until the task completes.</param>
    /// <param name="hits">Receives one hit per ray.</param>
    /// <param name="tminBits">The ray epsilon as float bits.</param>
    /// <param name="cancellationToken">Cancels the request between slabs.</param>
    /// <returns>Completes when every hit has been written.</returns>
    public Task TraceClosestAsync(ReadOnlyMemory<Ray> rays, Memory<HitId> hits, uint tminBits, CancellationToken cancellationToken) =>
        Enqueue(new Request(1, rays, default, hits, tminBits, cancellationToken));

    /// <summary>
    /// Refuses new requests, fails the ones with nothing on the device,
    /// waits out every slab in flight (whose requests complete when those
    /// slabs finish them, and fail when they cannot), then runs
    /// <paramref name="release"/> with the device idle.
    /// </summary>
    /// <param name="release">Releases the device; called once, under the device lock.</param>
    /// <remarks>
    /// The drainer holds the device lock for as long as it has slabs in
    /// flight and launches nothing once the batcher is closed, so taking the
    /// lock here is what waits the device out: <paramref name="release"/>
    /// never runs with a slab still on it.
    /// </remarks>
    public void Close(Action release)
    {
        List<Request> orphans;
        lock (_queueLock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            // A request with a slab on the device is the drainer's to finish;
            // the rest never reach it.
            orphans = [.. _queue.Where(r => r.InFlightSlabs == 0)];
            _queue.RemoveAll(r => r.InFlightSlabs == 0);
        }

        // Fail the queued requests before waiting out the slabs: they will
        // never run, and their callers should not wait on slabs that are not
        // theirs. Continuations run asynchronously, so this cannot re-enter.
        foreach (Request r in orphans)
        {
            r.Done.TrySetException(new ObjectDisposedException(nameof(VulkanRayTracer)));
        }

        lock (_deviceLock)
        {
            release();
        }
    }

    private Task Enqueue(Request request)
    {
        if (request.Rays.Length == 0)
        {
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _requests);

        bool start;
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_closed, typeof(VulkanRayTracer));
            _queue.Add(request);
            start = !_draining;
            _draining = true;
        }

        if (start)
        {
            // One drainer at a time, on the pool; it exits when the queue has
            // nothing to launch and nothing is on the device.
            _ = Task.Run(Drain);
        }

        return request.Done.Task;
    }

    private void Drain()
    {
        // Held for the whole run, not per call: Close waits on it, and must
        // not get in between a slab's submit and its completion.
        lock (_deviceLock)
        {
            while (true)
            {
                Slab? launch = null;
                lock (_queueLock)
                {
                    DropCancelled();
                    if (!_closed && _freeSlots.Count > 0)
                    {
                        launch = PlanNext(_freeSlots.Peek());
                    }

                    if (launch is null && _inFlight.Count == 0)
                    {
                        // Nothing to launch and nothing to wait for. A request
                        // queued after this sees _draining false and starts a
                        // new drainer.
                        _draining = false;
                        return;
                    }
                }

                if (launch is not null)
                {
                    _freeSlots.Dequeue();
                    Launch(launch);
                }
                else
                {
                    Land(_inFlight.Dequeue());
                }
            }
        }
    }

    // Under the queue lock. A cancelled request leaves the queue whether or
    // not a slab carries it; a slab still in flight skips it when it lands.
    private void DropCancelled()
    {
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            if (_queue[i].CancellationToken.IsCancellationRequested)
            {
                _queue[i].Done.TrySetCanceled(_queue[i].CancellationToken);
                _queue.RemoveAt(i);
            }
        }
    }

    // Under the queue lock: lays out the next slab from the oldest request
    // with rays still to launch, and claims those rays.
    private Slab? PlanNext(int slot)
    {
        Request? head = _queue.Find(r => r.Next < r.Rays.Length);
        if (head is null)
        {
            return null;
        }

        Slab slab = new(slot, head.Mode, head.TminBits);
        slab.Total = Plan(_queue, head.Mode, head.TminBits, _device.MaxSlabRays, slab.Segments);
        foreach (Segment s in slab.Segments)
        {
            s.Request.Next += s.Count;
            s.Request.InFlightSlabs++;
        }

        return slab;
    }

    private void Launch(Slab slab)
    {
        try
        {
            long packStart = Stopwatch.GetTimestamp();
            Stage(slab);
            Interlocked.Add(ref _packTicks, Stopwatch.GetTimestamp() - packStart);
            _device.Submit(slab.Slot, slab.Mode, slab.Total, slab.WordCount, slab.TminBits, _tmaxScaleBits);
            Volatile.Write(ref _dispatches, _dispatches + 1);
            if (_inFlight.Count == 0)
            {
                Interlocked.Exchange(ref _busySince, Stopwatch.GetTimestamp());
            }

            _inFlight.Enqueue(slab);
            if (_inFlight.Count > _peakInFlight)
            {
                Volatile.Write(ref _peakInFlight, _inFlight.Count);
            }
        }
        catch (Exception e)
        {
            // It never reached the device, so its slot is free again at once.
            _freeSlots.Enqueue(slab.Slot);
            Finish(slab, e);
        }
    }

    private void Land(Slab slab)
    {
        Exception? failure = null;
        try
        {
            Span<uint> words = _words.AsSpan(0, slab.WordCount);
            long waitStart = Stopwatch.GetTimestamp();
            long readback = 0;
            try
            {
                readback = _device.Complete(slab.Slot, words);
            }
            finally
            {
                // The call is the fence wait and then the copy out; the
                // device says how long the copy took.
                Interlocked.Add(ref _readbackTicks, readback);
                Interlocked.Add(ref _fenceWaitTicks, Stopwatch.GetTimestamp() - waitStart - readback);
                if (_inFlight.Count == 0)
                {
                    // The device is empty again: close the busy span. Land
                    // has already taken this slab off the in-flight queue.
                    long since = Interlocked.Exchange(ref _busySince, 0);
                    Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - since);
                }
            }

            Scatter(slab, words);
        }
        catch (Exception e)
        {
            failure = e;
        }

        _freeSlots.Enqueue(slab.Slot);
        Finish(slab, failure);
    }

    private void Finish(Slab slab, Exception? failure)
    {
        lock (_queueLock)
        {
            foreach (Segment s in slab.Segments)
            {
                Request r = s.Request;
                r.InFlightSlabs--;
                if (r.Done.Task.IsCompleted)
                {
                    // Cancelled, or faulted by an earlier slab: already out of
                    // the queue, and its memory is the caller's again.
                    continue;
                }

                if (failure is not null)
                {
                    r.Done.TrySetException(failure);
                    _queue.Remove(r);
                    continue;
                }

                r.Answered += s.Count;
                if (r.Answered == r.Rays.Length)
                {
                    r.Done.TrySetResult();
                    _queue.Remove(r);
                }
                else if (_closed && r.InFlightSlabs == 0)
                {
                    // Part answered when the tracer closed: the rest never will
                    // be. Only once none of it is left on the device, so nothing
                    // writes into it after its task has failed.
                    r.Done.TrySetException(new ObjectDisposedException(nameof(VulkanRayTracer)));
                    _queue.Remove(r);
                }
            }
        }
    }

    /// <summary>
    /// Lays out one slab: the queued requests of one kind, oldest first, each
    /// from where it left off, until the slab is full.
    /// </summary>
    /// <param name="queue">The queue, oldest first.</param>
    /// <param name="mode">The kind this slab carries.</param>
    /// <param name="tminBits">The epsilon this slab carries.</param>
    /// <param name="capacity">Rays the slab holds; a multiple of 64.</param>
    /// <param name="slab">Receives the segments.</param>
    /// <returns>Rays the dispatch carries, padding included.</returns>
    /// <remarks>
    /// A visibility segment starts on a 64-ray boundary of the slab, and a
    /// request that does not fit is split on a 64-ray boundary of its own, so
    /// each segment's bits are whole words of the request's output. A request
    /// whose every ray is already on the device is passed over and takes no
    /// room, not even alignment.
    /// </remarks>
    internal static int Plan(IReadOnlyList<Request> queue, int mode, uint tminBits, int capacity, List<Segment> slab)
    {
        slab.Clear();
        int offset = 0;
        foreach (Request r in queue)
        {
            int remaining = r.Rays.Length - r.Next;
            if (r.Mode != mode || r.TminBits != tminBits || remaining <= 0)
            {
                continue;
            }

            if (mode == 0)
            {
                offset = (offset + WorkgroupRays - 1) / WorkgroupRays * WorkgroupRays;
            }

            int room = capacity - offset;
            int take = Math.Min(room, remaining);
            if (mode == 0 && take < remaining)
            {
                take -= take % WorkgroupRays;
            }

            if (take <= 0)
            {
                break;
            }

            slab.Add(new Segment(r, r.Next, take, offset));
            offset += take;
            if (offset >= capacity)
            {
                break;
            }
        }

        return offset;
    }

    // The wire layout: two vec4 per ray, (ox,oy,oz,0) and (dx,dy,dz,tmax),
    // with the direction unnormalised and tmax the caller's reach. A gap
    // before an aligned segment repeats that segment's first ray, a valid
    // query whose answer is masked away.
    private void Stage(Slab slab)
    {
        Span<float> staging = _device.StageRays(slab.Slot, slab.Total);
        int filled = 0;
        foreach (Segment s in slab.Segments)
        {
            ReadOnlySpan<Ray> rays = s.Request.Rays.Span.Slice(s.Start, s.Count);
            for (; filled < s.Offset; filled++)
            {
                Put(staging, filled, rays[0]);
            }

            for (int i = 0; i < s.Count; i++)
            {
                Put(staging, filled++, rays[i]);
            }
        }
    }

    private static void Put(Span<float> staging, int at, in Ray r)
    {
        int b = at * 8;
        staging[b] = r.OriginX;
        staging[b + 1] = r.OriginY;
        staging[b + 2] = r.OriginZ;
        staging[b + 3] = 0f;
        staging[b + 4] = r.DirectionX;
        staging[b + 5] = r.DirectionY;
        staging[b + 6] = r.DirectionZ;
        staging[b + 7] = r.MaxDistance;
    }

    private void Scatter(Slab slab, ReadOnlySpan<uint> words)
    {
        foreach (Segment s in slab.Segments)
        {
            // Only the drainer completes a request that has a slab in flight
            // (Close leaves those alone), so this cannot race a completion.
            if (s.Request.Done.Task.IsCompleted)
            {
                continue;
            }

            if (slab.Mode == 0)
            {
                // Workgroup g's two words are bits 64g..64g+63, a ulong on
                // the little-endian hosts this runs on.
                Span<ulong> bits = s.Request.HitBits.Span;
                int wordPairs = (s.Count + WorkgroupRays - 1) / WorkgroupRays;
                words.Slice(s.Offset / 32, wordPairs * 2)
                    .CopyTo(MemoryMarshal.Cast<ulong, uint>(bits).Slice(s.Start / 32, wordPairs * 2));
                int tail = s.Count % WorkgroupRays;
                if (tail != 0)
                {
                    bits[(s.Start + s.Count - 1) / WorkgroupRays] &= (1UL << tail) - 1;
                }

                continue;
            }

            Span<HitId> hits = s.Request.Hits.Span.Slice(s.Start, s.Count);
            ReadOnlySpan<Ray> rays = s.Request.Rays.Span.Slice(s.Start, s.Count);
            for (int i = 0; i < s.Count; i++)
            {
                int at = (s.Offset + i) * 2;
                uint prim = words[at];
                if (prim == 0xFFFFFFFFu || prim >= (uint)_triangleIds.Length)
                {
                    hits[i] = HitId.Missed;
                    continue;
                }

                float t = BitConverter.Int32BitsToSingle(unchecked((int)words[at + 1]));
                float length = rays[i].MaxDistance;

                // The KD tracer's fraction rule, reproduced exactly: the
                // divide-by-1 is skipped because it is exact, and the t the
                // kernel committed is in ray-parameter units, as stock's
                // hit distance is.
                hits[i] = new HitId(_triangleIds[prim], length == 1.0f ? t : t / length);
            }
        }
    }

    /// <summary>One caller's batch and how far through it the drainer is.</summary>
    internal sealed class Request(
        int mode,
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        Memory<HitId> hits,
        uint tminBits,
        CancellationToken cancellationToken)
    {
        public int Mode { get; } = mode;

        public ReadOnlyMemory<Ray> Rays { get; } = rays;

        public Memory<ulong> HitBits { get; } = hitBits;

        public Memory<HitId> Hits { get; } = hits;

        public uint TminBits { get; } = tminBits;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        /// <summary>
        /// The first ray not yet laid out in a slab; moves when a slab is
        /// planned, under the queue lock. Rays before it are on the device or
        /// answered.
        /// </summary>
        public int Next { get; set; }

        /// <summary>Rays answered so far; the request completes when this reaches its length.</summary>
        public int Answered { get; set; }

        /// <summary>How many slabs on the device carry part of it; guarded by the queue lock.</summary>
        public int InFlightSlabs { get; set; }

        // Continuations run off the drainer, which must get back to the device.
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A run of one request's rays at a slab offset.</summary>
    /// <param name="Request">The request.</param>
    /// <param name="Start">The first ray, in the request's numbering.</param>
    /// <param name="Count">How many rays.</param>
    /// <param name="Offset">Where they sit in the slab.</param>
    internal readonly record struct Segment(Request Request, int Start, int Count, int Offset);

    /// <summary>One planned slab: its slot, its kind and its segments.</summary>
    private sealed class Slab(int slot, int mode, uint tminBits)
    {
        public int Slot { get; } = slot;

        public int Mode { get; } = mode;

        public uint TminBits { get; } = tminBits;

        public List<Segment> Segments { get; } = [];

        /// <summary>Rays the dispatch carries, padding included.</summary>
        public int Total { get; set; }

        /// <summary>Words the dispatch returns: 2 per 64-ray workgroup for any-hit, 2 per ray for closest.</summary>
        public int WordCount => Mode == 0 ? (Total + WorkgroupRays - 1) / WorkgroupRays * 2 : Total * 2;
    }
}
