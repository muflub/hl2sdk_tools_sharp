//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Gpu;

/// <summary>
/// The part of a device a <see cref="SlabBatcher"/> drives: one staging
/// buffer and one synchronous dispatch at a time.
/// </summary>
internal interface ISlabDevice
{
    /// <summary>Rays one dispatch carries at most; a whole number of 64-ray workgroups.</summary>
    int MaxSlabRays { get; }

    /// <summary>The staging for the next dispatch's rays, 8 floats each.</summary>
    /// <param name="rayCount">How many rays the dispatch will carry.</param>
    /// <returns>The span to fill.</returns>
    Span<float> StageRays(int rayCount);

    /// <summary>Traces the staged rays and reads the answers back.</summary>
    /// <param name="mode">0 any-hit bits, 1 closest hit.</param>
    /// <param name="rayCount">Rays staged.</param>
    /// <param name="outWords">Receives 2 words per 64-ray workgroup (mode 0) or per ray (mode 1).</param>
    /// <param name="tminBits">The ray epsilon, as float bits.</param>
    /// <param name="tmaxScaleBits">The any-hit tmax scale, as float bits.</param>
    void Dispatch(int mode, int rayCount, Span<uint> outWords, uint tminBits, uint tmaxScaleBits);
}

/// <summary>
/// Feeds a device from many concurrent callers: requests queue, and one
/// drainer packs every queued request of the same kind into each dispatch.
/// </summary>
/// <remarks>
/// <para>
/// vrad's workers each hand the tracer a small batch (a few thousand rays)
/// and park until it is answered. A device takes one dispatch at a time, and
/// each dispatch is a submit and a fence wait whose cost barely depends on
/// the ray count, so answering the batches one by one spends the device on
/// overhead and queues every worker behind one lock. Measured on ss_sandbox
/// on a 32-thread machine, that design allocated about 25 GB (every batch
/// was copied into and out of fresh arrays), ran 268 gen-2 collections, and
/// spent 375 s of thread time waiting on the lock, for a lighting pass the
/// CPU tracer finished several times faster.
/// </para>
/// <para>
/// Here no caller thread blocks and nothing is copied: a request keeps the
/// caller's memory, the drainer packs rays straight from it into the device's
/// staging, and writes the answers straight back. While the device works on
/// one slab, the next requests queue, so the busier the device, the fuller
/// its slabs.
/// </para>
/// <para>
/// Every ray's answer is a function of that ray alone (the kernel reads no
/// other lane), so how requests share a slab cannot change a bit or a hit.
/// The one layout rule is the any-hit output: each 64-ray workgroup writes
/// two words, so a visibility request starts on a 64-ray boundary of its
/// slab, the gap before it is filled with copies of its first ray, and the
/// bits past its last ray are masked off, which leaves them zero, as a
/// dispatch of that request alone would.
/// </para>
/// <para>
/// A request is cancelled between slabs (a dispatch in flight cannot be
/// recalled), and a failed dispatch faults exactly the requests it carried.
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
    private bool _draining;
    private bool _closed;

    /// <summary>Wraps a device.</summary>
    /// <param name="device">The device; its dispatches are made only from the drainer.</param>
    /// <param name="triangleIds">The caller's id for each BLAS primitive.</param>
    /// <param name="tmaxScaleBits">The any-hit tmax scale every dispatch passes.</param>
    public SlabBatcher(ISlabDevice device, int[] triangleIds, uint tmaxScaleBits)
    {
        _device = device;
        _triangleIds = triangleIds;
        _tmaxScaleBits = tmaxScaleBits;

        // 2 words per ray covers the closest mode, and any-hit's 2 per 64.
        _words = new uint[device.MaxSlabRays * 2];
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

    /// <summary>Dispatches made so far, for facts and telemetry.</summary>
    public int Dispatches { get; private set; }

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
    /// Refuses new requests, fails queued ones, waits out a dispatch in
    /// flight (whose requests complete normally), then runs
    /// <paramref name="release"/> with the device idle.
    /// </summary>
    /// <param name="release">Releases the device; called once, under the device lock.</param>
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

            // A request on the device is the drainer's to finish; the rest
            // never reach it.
            orphans = [.. _queue.Where(r => !r.InFlight)];
            _queue.RemoveAll(r => !r.InFlight);
        }

        // Fail the queued requests before waiting out the dispatch: they will
        // never run, and their callers should not wait on a slab that is not
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
            // One drainer at a time, on the pool; it exits when the queue is empty.
            _ = Task.Run(Drain);
        }

        return request.Done.Task;
    }

    private void Drain()
    {
        List<Segment> slab = [];
        while (true)
        {
            int mode;
            uint tminBits;
            int total;
            lock (_queueLock)
            {
                for (int i = _queue.Count - 1; i >= 0; i--)
                {
                    if (_queue[i].CancellationToken.IsCancellationRequested)
                    {
                        _queue[i].Done.TrySetCanceled(_queue[i].CancellationToken);
                        _queue.RemoveAt(i);
                    }
                }

                if (_queue.Count == 0 || _closed)
                {
                    _draining = false;
                    return;
                }

                mode = _queue[0].Mode;
                tminBits = _queue[0].TminBits;
                total = Plan(_queue, mode, tminBits, _device.MaxSlabRays, slab);
                foreach (Segment s in slab)
                {
                    s.Request.InFlight = true;
                }
            }

            Exception? failure = null;
            try
            {
                lock (_deviceLock)
                {
                    ObjectDisposedException.ThrowIf(_closed, typeof(VulkanRayTracer));
                    Stage(slab, total);
                    int wordCount = mode == 0 ? (total + WorkgroupRays - 1) / WorkgroupRays * 2 : total * 2;
                    Span<uint> words = _words.AsSpan(0, wordCount);
                    _device.Dispatch(mode, total, words, tminBits, _tmaxScaleBits);
                    Dispatches++;
                    Scatter(slab, mode, words);
                }
            }
            catch (Exception e)
            {
                failure = e;
            }

            lock (_queueLock)
            {
                foreach (Segment s in slab)
                {
                    s.Request.InFlight = false;
                    if (failure is not null)
                    {
                        s.Request.Done.TrySetException(failure);
                        _queue.Remove(s.Request);
                        continue;
                    }

                    s.Request.Next += s.Count;
                    if (s.Request.Next == s.Request.Rays.Length)
                    {
                        s.Request.Done.TrySetResult();
                        _queue.Remove(s.Request);
                    }
                    else if (_closed)
                    {
                        // Part answered when the tracer closed: the rest never will be.
                        s.Request.Done.TrySetException(new ObjectDisposedException(nameof(VulkanRayTracer)));
                        _queue.Remove(s.Request);
                    }
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
    /// each segment's bits are whole words of the request's output.
    /// </remarks>
    internal static int Plan(IReadOnlyList<Request> queue, int mode, uint tminBits, int capacity, List<Segment> slab)
    {
        slab.Clear();
        int offset = 0;
        foreach (Request r in queue)
        {
            if (r.Mode != mode || r.TminBits != tminBits)
            {
                continue;
            }

            if (mode == 0)
            {
                offset = (offset + WorkgroupRays - 1) / WorkgroupRays * WorkgroupRays;
            }

            int room = capacity - offset;
            int remaining = r.Rays.Length - r.Next;
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
    private void Stage(List<Segment> slab, int total)
    {
        Span<float> staging = _device.StageRays(total);
        int filled = 0;
        foreach (Segment s in slab)
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

    private void Scatter(List<Segment> slab, int mode, ReadOnlySpan<uint> words)
    {
        foreach (Segment s in slab)
        {
            if (mode == 0)
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

        /// <summary>Rays already answered; only the drainer moves it.</summary>
        public int Next { get; set; }

        /// <summary>Whether a slab on the device carries part of it; guarded by the queue lock.</summary>
        public bool InFlight { get; set; }

        // Continuations run off the drainer, which must get back to the device.
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A run of one request's rays at a slab offset.</summary>
    /// <param name="Request">The request.</param>
    /// <param name="Start">The first ray, in the request's numbering.</param>
    /// <param name="Count">How many rays.</param>
    /// <param name="Offset">Where they sit in the slab.</param>
    internal readonly record struct Segment(Request Request, int Start, int Count, int Offset);
}
