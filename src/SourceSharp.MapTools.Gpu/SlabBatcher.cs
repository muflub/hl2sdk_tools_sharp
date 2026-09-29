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
/// slot can be filled or scattered while others are on the device.
/// </summary>
/// <remarks>
/// A slot moves through stage (or open), submit, complete, in that order,
/// and is never staged, opened or submitted again until its completion has
/// returned (or its submit has thrown). The batcher makes every call from
/// one thread at a time, so an implementation needs no locking of its own;
/// only the memory <see cref="OpenRays"/> hands out is written from other
/// threads, and never while its slot is on the device.
/// </remarks>
internal interface ISlabDevice
{
    /// <summary>Rays one slot carries at most; a whole number of 64-ray workgroups.</summary>
    int MaxSlabRays { get; }

    /// <summary>How many slots the device has; at least 1.</summary>
    int SlotCount { get; }

    /// <summary>
    /// How many slots may be on the device at once; 1 to <see cref="SlotCount"/>.
    /// The rest are there to be filled while those are traced
    /// (<see cref="SlabWrites.Callers"/>).
    /// </summary>
    int MaxSlabsInFlight => SlotCount;

    /// <summary>The staging for a slot's next dispatch, <see cref="RayRecord.Words"/> words per ray.</summary>
    /// <param name="slot">The slot; it must not be in flight.</param>
    /// <param name="rayCount">How many rays the dispatch will carry.</param>
    /// <param name="record">The record the rays are packed in.</param>
    /// <returns>
    /// The memory to fill, exactly <c>rayCount * record.Words</c> words. It is
    /// memory rather than a span so that several threads can pack one slab.
    /// </returns>
    Memory<uint> StageRays(int slot, int rayCount, RayRecord record);

    /// <summary>
    /// The whole of a free slot's ray memory, <see cref="MaxSlabRays"/> times
    /// <see cref="RayRecord.WideWords"/> words, for the callers who write
    /// their own rays into it before the slot's next dispatch.
    /// </summary>
    /// <param name="slot">The slot; it must not be in flight.</param>
    /// <returns>
    /// The memory the slot's next <see cref="Submit"/> reads its rays from,
    /// from word 0. It may be written from any thread until that submit, and
    /// must never be read by the host: on a device that reads rays in place
    /// it is write-combined memory across the bus.
    /// </returns>
    /// <remarks>
    /// Called by the batcher when a slot comes back from the device, before
    /// any caller is let near it. A slot whose last wait failed may still be
    /// in use by the device; the call waits it out first, and throws when it
    /// cannot, so a caller never writes into a buffer a kernel may be reading.
    /// </remarks>
    Memory<uint> OpenRays(int slot);

    /// <summary>Starts tracing a slot's staged rays and returns without waiting for them.</summary>
    /// <param name="slot">The slot staged last.</param>
    /// <param name="mode">0 any-hit bits, 1 closest hit.</param>
    /// <param name="rayCount">Rays staged.</param>
    /// <param name="outWordCount">Words the dispatch returns: 2 per 64-ray workgroup (mode 0) or per ray (mode 1).</param>
    /// <param name="tminBits">The ray epsilon, as float bits.</param>
    /// <param name="tmaxScaleBits">The any-hit tmax scale, as float bits.</param>
    /// <param name="record">The record the slot's rays were staged in.</param>
    void Submit(int slot, int mode, int rayCount, int outWordCount, uint tminBits, uint tmaxScaleBits, RayRecord record);

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

/// <summary>Who writes a slab's rays into the memory the device reads them from.</summary>
internal enum SlabWrites
{
    /// <summary>
    /// The drainer, when it plans the slab: it packs the callers' rays from
    /// their own memory into the slot's staging, with up to
    /// <see cref="SlabBatcher.MaxPackThreads"/> - 1 helpers.
    /// </summary>
    Drainer,

    /// <summary>
    /// Each caller, on its own thread, when it hands its rays over: it
    /// reserves room in an open slab of its kind and writes its rays there,
    /// so the drainer only submits. What will not fit is left to the drainer.
    /// </summary>
    Callers,
}

/// <summary>
/// Feeds a device from many concurrent callers: requests queue, and one
/// drainer puts every queued request of the same kind into each slab,
/// keeping up to <see cref="ISlabDevice.MaxSlabsInFlight"/> slabs on the device.
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
/// Here no caller thread blocks and nothing is copied twice: a request keeps
/// the caller's memory, its rays are written once into the device's staging,
/// and its answers are written straight back. While slabs are on the
/// device, the next requests gather, so the busier the device, the fuller
/// its slabs.
/// </para>
/// <para>
/// The drainer keeps several slabs in flight. With one slab at a time the
/// device idled while the drainer filled the next slab and scattered the
/// last, and the drainer idled while the device worked: the two costs added
/// up, and vrad's workers spent their time parked. Now the drainer launches
/// a slab whenever a slot is free and work is waiting, and waits for the
/// oldest slab only when every slot is busy or nothing is left to launch, so
/// the device has the next slab queued behind the one it is running while
/// the drainer scatters. Slabs are waited for in the order they were
/// launched, which is the order one queue runs them in.
/// </para>
/// <para>
/// Every ray's answer is a function of that ray alone (the kernel reads no
/// other lane), so how requests share a slab, who wrote them there, or how
/// many slabs are in flight, cannot change a bit or a hit. The one layout
/// rule is the any-hit output: each 64-ray workgroup writes two words, so a
/// visibility request starts on a 64-ray boundary of its slab, the gap
/// before it is filled with copies of its first ray, and the bits past its
/// last ray are masked off, which leaves them zero, as a dispatch of that
/// request alone would.
/// </para>
/// <para>
/// A slab carries one <see cref="RayRecord"/>: requests whose every ray
/// shares a reach (<see cref="Request.UniformReach"/>) share slabs with
/// requests that share the same one, and go on the wire in 24 bytes a ray;
/// the rest share 28-byte slabs. Keeping the two apart is what lets a slab
/// be narrow at all: one ray with its own reach would make the whole slab
/// wide. Like the mode and the epsilon, the record cannot change an answer,
/// only how many bytes carry the question.
/// </para>
/// <para>
/// <b>Who writes the rays (<see cref="SlabWrites"/>).</b> With
/// <see cref="SlabWrites.Drainer"/>, a slab is planned when a slot comes
/// free and packed then, from the callers' memory, by the drainer and up to
/// <see cref="MaxPackThreads"/> - 1 helpers (<see cref="PackCrew"/>). That
/// pack is a serial stage between vrad's workers and the device: on an RX
/// 9070 on 2fort, which reads rays in place over resizable BAR, it took
/// about 1.65 s of a 2.08 s facelights stage while the device was seldom
/// waited on, and spreading it over four threads and narrowing the records
/// only shortened it. With <see cref="SlabWrites.Callers"/> the stage is
/// gone: a slot that is not on the device is an open slab of one kind
/// (mode, epsilon, record), and a caller reserves its room there when it
/// hands its rays over and writes them itself, on its own thread, before
/// the call returns. The drainer then only picks the oldest open slab and
/// submits it. Up to <see cref="ISlabDevice.MaxSlabsInFlight"/> slabs are on the
/// device; the device's other slots are what the callers fill meanwhile, so
/// the device has as much to hold as before, and the pack is spread over
/// every worker instead of four threads.
/// </para>
/// <para>
/// <b>Why at hand-over, not while the batch is filled.</b> vrad's workers
/// could emit each ray straight into device memory instead of into their
/// own log. That was weighed and not done. The emit is interleaved with the
/// gather arithmetic, so its stores would trickle across the bus in partial
/// write-combining lines rather than stream; the log pads a packet by
/// copying its last ray and grows by copying itself, both reads of memory
/// the host must never read; the scatter needs each ray's reach again; the
/// CPU tracer needs the rays in host memory anyway; and a region reserved
/// for the whole fill (several milliseconds, of unknown size, of a kind the
/// fill decides only as it goes) would hold device memory hostage and keep
/// the slab it is in off the device. Writing at hand-over is one block copy
/// of rays the worker has just written, so still in its cache, into a
/// region whose size and kind are known: sequential stores, never a read.
/// </para>
/// <para>
/// <b>When the ring is full.</b> A caller never waits. If no open slab of its
/// kind has room and no slot is free, the rest of its rays are queued as
/// before and the drainer packs them into the next slot it frees (counted
/// as pack time on the bench). The ring is full only when the device is
/// behind, which is when a pack on the drainer costs nothing: it overlaps
/// the slabs still on the device. So there is no backpressure to deadlock
/// on: a caller holds a reservation only while it copies, which never
/// blocks, and the drainer waits for a slab's writers only after it has
/// closed that slab to new ones.
/// </para>
/// <para>
/// <b>Its own threads.</b> The drainer is a thread the batcher owns, not a
/// pool work item: it blocks on fences for as long as slabs are on the
/// device, which is most of a GPU compile's lighting, and a compile never
/// holds the host's thread pool (<see cref="Parallel.CompileParallelism"/>;
/// the host may be a web service). It starts with the first request and is
/// joined by <see cref="Close"/>. The pack helpers are the batcher's too,
/// made the first time a slab the drainer packs is big enough to share. A
/// caller's own write runs on the caller's thread, which is the compile's.
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
    /// <summary>
    /// Rays one packing thread takes at a time: 8,192 rays is 192 to 224 KB
    /// of records, big enough that handing a chunk to a helper costs little
    /// next to copying it, and small enough that a typical slab (tens of
    /// thousands of rays) still spreads over several threads.
    /// </summary>
    internal const int PackChunkRays = 8192;

    /// <summary>
    /// The most threads that pack one slab, the drainer included. A few
    /// writers already saturate what a bus or a memory controller takes from
    /// one socket; more would only take cores from vrad's workers, which are
    /// filling the next requests meanwhile.
    /// </summary>
    internal const int MaxPackThreads = 4;

    /// <summary>
    /// Slots a device keeps beyond the ones on it, for callers to fill
    /// (<see cref="SlabWrites.Callers"/>): one for each kind of request
    /// vrad's face lighting has out at once, its visibility rays and its sky
    /// rays. A third kind, or a burst that fills both, waits for a slab to
    /// land or is packed by the drainer.
    /// </summary>
    internal const int OpenSlabs = 2;

    private const int WorkgroupRays = 64;

    private readonly ISlabDevice _device;
    private readonly int[] _triangleIds;
    private readonly uint _tmaxScaleBits;
    private readonly uint[] _words;
    private readonly SlabWrites _writes;
    private readonly int _maxInFlight;
    private readonly object _queueLock = new();
    private readonly object _deviceLock = new();
    private readonly List<Request> _queue = [];

    // Under the queue lock: the slots that are neither on the device nor an
    // open slab, and, with callers writing, each slot's memory as the device
    // last handed it out; the open slabs, oldest first; and the reservations
    // being written. A slot whose memory could not be handed out (its last
    // wait failed) is "suspect": callers never open it, and only the
    // drainer's own pack, whose staging call waits the slot out again, uses it.
    private readonly Queue<int> _freeSlots = new();
    private readonly Queue<int> _suspectSlots = new();
    private readonly Memory<uint>[] _slotRays;
    private readonly List<Page> _open = [];
    private int _writers;
    private long _nextSeq;
    private bool _awaitingWriters;

    // The drainer's own state, touched only under the device lock: the slabs
    // on the device, oldest first.
    private readonly Queue<Slab> _inFlight = new();
    private bool _draining;
    private bool _closed;

    // The drainer thread, started under the queue lock by the first request
    // and woken once per start; the pack helpers, made by the drainer.
    // Both are joined by Close. The semaphore is never disposed: it holds no
    // handle unless one is asked for, and a request racing Close may still
    // release it.
    private readonly SemaphoreSlim _wake = new(0);
    private Thread? _drainer;
    private PackCrew? _crew;
    private volatile bool _stopping;

    /// <summary>Wraps a device.</summary>
    /// <param name="device">The device; its calls are made only from the drainer (and, for the first memory, here).</param>
    /// <param name="triangleIds">The caller's id for each BLAS primitive.</param>
    /// <param name="tmaxScaleBits">The any-hit tmax scale every dispatch passes.</param>
    /// <param name="writes">Who writes the rays into the device's memory.</param>
    public SlabBatcher(ISlabDevice device, int[] triangleIds, uint tmaxScaleBits, SlabWrites writes = SlabWrites.Drainer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(device.SlotCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(device.MaxSlabsInFlight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(device.MaxSlabsInFlight, device.SlotCount);
        _device = device;
        _triangleIds = triangleIds;
        _tmaxScaleBits = tmaxScaleBits;
        _writes = writes;
        _maxInFlight = device.MaxSlabsInFlight;

        // 2 words per ray covers the closest mode, and any-hit's 2 per 64.
        // One buffer serves every slot: slabs are scattered one at a time.
        _words = new uint[device.MaxSlabRays * 2];
        _slotRays = new Memory<uint>[device.SlotCount];
        for (int slot = 0; slot < device.SlotCount; slot++)
        {
            // No slot is on the device yet, so handing the memory out waits
            // for nothing; the drainer does it again each time a slot lands.
            if (writes == SlabWrites.Callers)
            {
                _slotRays[slot] = device.OpenRays(slot);
            }

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

    /// <summary>Who writes the slabs' rays.</summary>
    public SlabWrites Writes => _writes;

    /// <summary>Slabs submitted so far, for facts and telemetry.</summary>
    public int Dispatches => Volatile.Read(ref _dispatches);

    private int _dispatches;

    // The bench's counters (GpuTraceStatistics says what each means). The
    // drainer alone writes the tick counts and the peak, one thread at a
    // time under the device lock; callers add their write ticks; the bench
    // reads them from another thread, hence the interlocked reads and writes
    // rather than a lock.
    private long _requests;
    private long _busyTicks;
    private long _fenceWaitTicks;
    private long _busySince;
    private long _packTicks;
    private long _writeTicks;
    private long _readbackTicks;
    private long _rayBytes;
    private long _slabRays;
    private long _callerRays;
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
                _maxInFlight,
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _packTicks)),
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _readbackTicks)),
                _device.Layout.DirectRays,
                _device.Layout.DirectOut,
                Interlocked.Read(ref _rayBytes),
                Interlocked.Read(ref _slabRays))
            {
                Write = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _writeTicks)),
                CallerRays = Interlocked.Read(ref _callerRays),
            };
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
    /// Refuses new requests, waits for callers still writing their rays,
    /// fails the requests with nothing on the device, waits out every slab
    /// in flight (whose requests complete when those slabs finish them, and
    /// fail when they cannot), then runs <paramref name="release"/> with the
    /// device idle.
    /// </summary>
    /// <param name="release">Releases the device; called once, under the device lock.</param>
    /// <remarks>
    /// <para>
    /// The drainer holds the device lock for as long as it has slabs in
    /// flight and launches nothing once the batcher is closed, so taking the
    /// lock here is what waits the device out: <paramref name="release"/>
    /// never runs with a slab still on it.
    /// </para>
    /// <para>
    /// A caller writing into a slot's memory holds a reservation until its
    /// copy is done, and a copy never blocks, so the wait for writers is
    /// short and certain; it has to come first, because releasing the device
    /// unmaps that memory. No reservation is made once the batcher is closed,
    /// and the open slabs are then dropped, so none is left behind.
    /// </para>
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
            while (_writers > 0)
            {
                Monitor.Wait(_queueLock);
            }

            // Open slabs never reach the device now; the drainer drops one it
            // had claimed when it sees the batcher closed.
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                if (!_open[i].Claimed)
                {
                    DropPage(_open[i]);
                }
            }

            // A request with a slab on the device is the drainer's to finish;
            // the rest never reach it.
            orphans = [.. _queue.Where(r => r.HeldSlabs == 0)];
            _queue.RemoveAll(r => r.HeldSlabs == 0);
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

        // The device is released and the drainer has nothing to launch: stop
        // it, then the pack helpers, so no thread of the batcher's outlives
        // the tracer, whether its compile succeeded, failed or was cancelled.
        _stopping = true;
        _wake.Release();
        Thread? drainer;
        lock (_queueLock)
        {
            drainer = _drainer;
        }

        if (drainer is not null && drainer != Thread.CurrentThread)
        {
            drainer.Join();
        }

        _crew?.Dispose();
    }

    /// <summary>
    /// A hook the pack calls with each chunk's index, on the thread packing
    /// it; for the fact that no packing runs on the thread pool.
    /// </summary>
    internal Action<int>? ObservePackChunk { get; set; }

    /// <summary>
    /// A hook a caller's write calls with the slab positions it wrote, on
    /// the caller's thread, once the write is done and before its
    /// reservation is released; for facts that check who wrote what.
    /// </summary>
    internal Action<int>? ObserveCallerWrite { get; set; }

    /// <summary>
    /// A hook the drainer calls with a slot it has just put back on the free
    /// list, outside the queue lock and before the slab's requests are
    /// finished; for facts that make a caller take the slot at that moment.
    /// </summary>
    internal Action<int>? ObserveSlotFreed { get; set; }

    /// <summary>
    /// Reservations currently being written by callers, for the facts that
    /// check none is left behind.
    /// </summary>
    internal int PendingWriters
    {
        get
        {
            lock (_queueLock)
            {
                return _writers;
            }
        }
    }

    /// <summary>
    /// Whether the drainer has claimed an open slab and is waiting for the
    /// callers still writing into it, for facts.
    /// </summary>
    internal bool AwaitingWriters
    {
        get
        {
            lock (_queueLock)
            {
                return _awaitingWriters;
            }
        }
    }

    /// <summary>Open slabs (slots callers are filling), for facts.</summary>
    internal int OpenSlabCount
    {
        get
        {
            lock (_queueLock)
            {
                return _open.Count;
            }
        }
    }

    /// <summary>The threads the batcher has started so far: the drainer, then any pack helpers.</summary>
    internal IReadOnlyList<Thread> Threads
    {
        get
        {
            List<Thread> threads = [];
            lock (_queueLock)
            {
                if (_drainer is not null)
                {
                    threads.Add(_drainer);
                }
            }

            if (Volatile.Read(ref _crew) is { } crew)
            {
                threads.AddRange(crew.Threads);
            }

            return threads;
        }
    }

    private Task Enqueue(Request request)
    {
        if (request.Rays.Length == 0)
        {
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _requests);

        List<Reservation>? writes = null;
        bool start = false;
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_closed, typeof(VulkanRayTracer));
            if (_drainer is null)
            {
                Thread drainer = new(DrainerLoop) { IsBackground = true, Name = "ssmap gpu drainer" };
                drainer.Start();
                _drainer = drainer;
            }

            request.Seq = _nextSeq++;
            _queue.Add(request);
            if (_writes == SlabWrites.Callers && !request.CancellationToken.IsCancellationRequested)
            {
                writes = Reserve(request);
            }

            // Rays left for the drainer need it now; reserved ones need it
            // once they are written, below.
            if (request.Next < request.Rays.Length)
            {
                start = Wake();
            }
        }

        if (writes is { Count: > 0 })
        {
            start |= Write(request, writes);
        }

        if (start)
        {
            // One drain at a time; it ends when nothing is left to launch and
            // nothing is on the device, and the next request that finds it
            // ended wakes the drainer again.
            _wake.Release();
        }

        return request.Done.Task;
    }

    // Under the queue lock: whether the caller must release the drainer.
    private bool Wake()
    {
        if (_draining)
        {
            return false;
        }

        _draining = true;
        return true;
    }

    /// <summary>
    /// Under the queue lock: reserves room for a request's rays in the open
    /// slabs of its kind, opening free slots as it needs them, as far as the
    /// ring allows, and moves <see cref="Request.Next"/> past what it
    /// reserved.
    /// </summary>
    /// <remarks>
    /// Every reservation is laid out by <see cref="Fit"/>, the rule the
    /// drainer's own <see cref="Plan"/> follows, so a caller's segments start
    /// and split where the drainer's would. A slab that cannot take the next
    /// piece is sealed (nothing more joins it), and the piece goes to the
    /// next open slab or a fresh slot.
    /// </remarks>
    private List<Reservation> Reserve(Request request)
    {
        List<Reservation> reserved = [];
        int capacity = _device.MaxSlabRays;
        int next = 0;
        int length = request.Rays.Length;
        while (next < length)
        {
            Page? page = _open.Find(p => !p.Sealed && p.Accepts(request));
            if (page is null)
            {
                if (_freeSlots.Count == 0)
                {
                    break;
                }

                int slot = _freeSlots.Dequeue();
                page = new Page(slot, request.Seq, request.Mode, request.TminBits, request.UniformReach, _slotRays[slot]);
                _open.Add(page);
            }

            int take = Fit(page.Used, capacity, request.Mode, length - next, out int offset);
            if (take <= 0)
            {
                page.Sealed = true;
                continue;
            }

            Segment segment = new(request, next, take, offset);
            reserved.Add(new Reservation(page, segment, page.Used));
            page.Segments.Add(segment);
            page.Used = offset + take;
            page.Writers++;
            _writers++;
            request.HeldSlabs++;
            if (page.Used >= capacity)
            {
                page.Sealed = true;
            }

            next += take;
        }

        request.Next = next;
        return reserved;
    }

    /// <summary>
    /// A caller's write: each reservation's padding and rays, in address
    /// order, straight into the slot's memory; then the reservations are
    /// released. Returns whether the drainer must be woken.
    /// </summary>
    private bool Write(Request request, List<Reservation> writes)
    {
        Exception? failure = null;
        long start = Stopwatch.GetTimestamp();
        int written = 0;
        try
        {
            ReadOnlySpan<Ray> rays = request.Rays.Span;
            foreach (Reservation w in writes)
            {
                PackSegment(w.Segment, rays.Slice(w.Segment.Start, w.Segment.Count), w.Page.Record, w.Page.Rays.Span, w.From, int.MaxValue);
                written += w.Segment.Offset + w.Segment.Count - w.From;
            }

            ObserveCallerWrite?.Invoke(written);
        }
        catch (Exception e)
        {
            // Only a broken plan can fail a copy. The slab keeps the
            // segment's room (its neighbours' offsets depend on it), and the
            // request is faulted, so nothing reads its answers.
            failure = e;
        }

        Interlocked.Add(ref _writeTicks, Stopwatch.GetTimestamp() - start);
        lock (_queueLock)
        {
            foreach (Reservation w in writes)
            {
                w.Page.Writers--;
                _writers--;
            }

            // The drainer waits for a claimed slab's writers, and Close for
            // every writer; either may be waiting on this release.
            Monitor.PulseAll(_queueLock);
            if (failure is not null && request.Done.TrySetException(failure))
            {
                _queue.Remove(request);
            }

            return !_closed && Wake();
        }
    }

    private void DrainerLoop()
    {
        while (true)
        {
            _wake.Wait();
            if (_stopping)
            {
                return;
            }

            try
            {
                Drain();
            }
            catch (Exception e)
            {
                // Launch and Land catch everything a slab can throw, so this
                // is a broken invariant, not a device fault. On the pool it
                // would have been an unobserved task and a hung queue; here
                // the queued requests fail with it and the drainer lives on.
                FailQueued(e);
            }
        }
    }

    private void FailQueued(Exception e)
    {
        List<Request> failed;
        lock (_queueLock)
        {
            failed = [.. _queue];
            _queue.Clear();
            _draining = false;
        }

        foreach (Request r in failed)
        {
            r.Done.TrySetException(e);
        }
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
                    if (!_closed && _inFlight.Count < _maxInFlight)
                    {
                        launch = NextSlab();
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
                    Launch(launch);
                }
                else
                {
                    Land(_inFlight.Dequeue());
                }
            }
        }
    }

    // Under the queue lock, with room on the device: the oldest work
    // waiting, whether an open slab the callers wrote or rays left for the
    // drainer to pack, or null. Rays left over wait for a slot, and when
    // every slot off the device is an open slab, the oldest slab goes first
    // even if they are older: it is what frees a slot for them. Returning
    // null with a slab still open and nothing on the device would leave the
    // drainer asleep with work nobody else will wake it for.
    private Slab? NextSlab()
    {
        Page? page = null;
        foreach (Page p in _open)
        {
            if (page is null || p.Seq < page.Seq)
            {
                page = p;
            }
        }

        Request? head = _queue.Find(r => r.Next < r.Rays.Length);
        bool slotForHead = _freeSlots.Count > 0 || _suspectSlots.Count > 0;
        if (head is not null && slotForHead && (page is null || head.Seq < page.Seq))
        {
            int slot = _freeSlots.Count > 0 ? _freeSlots.Dequeue() : _suspectSlots.Dequeue();
            return PlanNext(slot, head);
        }

        if (page is null)
        {
            // Nothing written, and rays left over only if every slot is on
            // the device: the drainer lands one and comes back.
            return null;
        }

        // Closed to new reservations first, so the writers still copying
        // into it are the last; each copy is short and never blocks.
        page.Sealed = true;
        page.Claimed = true;
        while (page.Writers > 0)
        {
            _awaitingWriters = true;
            Monitor.Wait(_queueLock);
        }

        _awaitingWriters = false;
        if (_closed)
        {
            // Close came in while the writers finished and dropped the
            // other open slabs; this one goes the same way.
            DropPage(page);
            return null;
        }

        _open.Remove(page);
        Slab slab = new(page.Slot, page.Mode, page.TminBits, page.Record) { Total = page.Used, WrittenByCallers = true };
        slab.Segments.AddRange(page.Segments);
        return slab;
    }

    // Under the queue lock: an open slab that will never be submitted, whose
    // slot goes back to the free list untouched. Its requests lose a slab
    // they were waiting on; once closed, one with nothing else out fails.
    private void DropPage(Page page)
    {
        _open.Remove(page);
        _freeSlots.Enqueue(page.Slot);
        foreach (Segment s in page.Segments)
        {
            Request r = s.Request;
            r.HeldSlabs--;
            if (_closed && r.HeldSlabs == 0 && r.Done.TrySetException(new ObjectDisposedException(nameof(VulkanRayTracer))))
            {
                _queue.Remove(r);
            }
        }
    }

    // Under the queue lock. A cancelled request leaves the queue whether or
    // not a slab carries it; a slab still in flight skips it when it lands.
    // An open slab whose every request has finished (all cancelled, say) and
    // that nobody is writing is dropped rather than traced.
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

        for (int i = _open.Count - 1; i >= 0; i--)
        {
            Page page = _open[i];
            if (!page.Claimed && page.Writers == 0 && page.Segments.TrueForAll(s => s.Request.Done.Task.IsCompleted))
            {
                DropPage(page);
            }
        }
    }

    // Under the queue lock: lays out the next slab from the oldest request
    // with rays still to launch, and claims those rays.
    private Slab PlanNext(int slot, Request head)
    {
        Slab slab = new(slot, head.Mode, head.TminBits, RayRecord.For(head.UniformReach));
        slab.Total = Plan(_queue, head.Mode, head.TminBits, head.UniformReach, _device.MaxSlabRays, slab.Segments);
        foreach (Segment s in slab.Segments)
        {
            s.Request.Next += s.Count;
            s.Request.HeldSlabs++;
        }

        return slab;
    }

    private void Launch(Slab slab)
    {
        try
        {
            if (!slab.WrittenByCallers)
            {
                long packStart = Stopwatch.GetTimestamp();
                Stage(slab);
                Interlocked.Add(ref _packTicks, Stopwatch.GetTimestamp() - packStart);
            }

            _device.Submit(slab.Slot, slab.Mode, slab.Total, slab.WordCount, slab.TminBits, _tmaxScaleBits, slab.Record);
            Interlocked.Add(ref _rayBytes, (long)slab.Total * slab.Record.Bytes);
            Interlocked.Add(ref _slabRays, slab.Total);
            if (slab.WrittenByCallers)
            {
                Interlocked.Add(ref _callerRays, slab.Total);
            }

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
            FreeSlot(slab.Slot);
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

        FreeSlot(slab.Slot);
        Finish(slab, failure);
    }

    /// <summary>
    /// On the drainer: a slot is off the device. With callers writing, its
    /// memory is handed out again first (<see cref="ISlabDevice.OpenRays"/>
    /// waits out a slot whose last wait failed), and a slot that cannot be
    /// handed out is kept for the drainer's own pack, whose staging call
    /// tries the wait again and fails the slab's requests if it still fails.
    /// </summary>
    private void FreeSlot(int slot)
    {
        Memory<uint> rays = default;
        bool open = false;
        if (_writes == SlabWrites.Callers)
        {
            try
            {
                rays = _device.OpenRays(slot);
                open = true;
            }
            catch (Exception)
            {
                // Suspect: see above. The failure itself reaches the callers
                // through the slab that failed, or the next one staged here.
            }
        }

        lock (_queueLock)
        {
            if (_writes == SlabWrites.Callers && !open)
            {
                _suspectSlots.Enqueue(slot);
                return;
            }

            if (open)
            {
                _slotRays[slot] = rays;
            }

            _freeSlots.Enqueue(slot);
        }

        ObserveSlotFreed?.Invoke(slot);
    }

    private void Finish(Slab slab, Exception? failure)
    {
        lock (_queueLock)
        {
            foreach (Segment s in slab.Segments)
            {
                Request r = s.Request;
                r.HeldSlabs--;
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
                else if (_closed && r.HeldSlabs == 0)
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
    /// Where the next segment of a request goes in a slab that already holds
    /// <paramref name="used"/> positions, and how many of its rays fit.
    /// </summary>
    /// <param name="used">Positions the slab already holds.</param>
    /// <param name="capacity">Rays the slab holds; a multiple of 64.</param>
    /// <param name="mode">The request's mode.</param>
    /// <param name="remaining">Its rays not yet in a slab.</param>
    /// <param name="offset">Where the segment starts: <paramref name="used"/>, rounded up to 64 for a visibility request.</param>
    /// <returns>
    /// How many rays fit; for a visibility request that does not fit whole, a
    /// multiple of 64, so each segment's bits are whole words of the request's
    /// output. Zero or less when nothing fits.
    /// </returns>
    internal static int Fit(int used, int capacity, int mode, int remaining, out int offset)
    {
        offset = mode == 0 ? (used + WorkgroupRays - 1) / WorkgroupRays * WorkgroupRays : used;
        int take = Math.Min(capacity - offset, remaining);
        if (mode == 0 && take < remaining)
        {
            take -= take % WorkgroupRays;
        }

        return take;
    }

    /// <summary>
    /// Lays out one slab: the queued requests of one kind, oldest first, each
    /// from where it left off, until the slab is full.
    /// </summary>
    /// <param name="queue">The queue, oldest first.</param>
    /// <param name="mode">The kind this slab carries.</param>
    /// <param name="tminBits">The epsilon this slab carries.</param>
    /// <param name="uniformReach">
    /// The reach this slab's rays share, or null for a wide slab; only
    /// requests with that <see cref="Request.UniformReach"/> join it.
    /// </param>
    /// <param name="capacity">Rays the slab holds; a multiple of 64.</param>
    /// <param name="slab">Receives the segments.</param>
    /// <returns>Rays the dispatch carries, padding included.</returns>
    /// <remarks>
    /// A visibility segment starts on a 64-ray boundary of the slab, and a
    /// request that does not fit is split on a 64-ray boundary of its own
    /// (<see cref="Fit"/>). A request whose every ray is already in a slab is
    /// passed over and takes no room, not even alignment.
    /// </remarks>
    internal static int Plan(IReadOnlyList<Request> queue, int mode, uint tminBits, uint? uniformReach, int capacity, List<Segment> slab)
    {
        slab.Clear();
        int used = 0;
        foreach (Request r in queue)
        {
            int remaining = r.Rays.Length - r.Next;
            if (r.Mode != mode || r.TminBits != tminBits || r.UniformReach != uniformReach || remaining <= 0)
            {
                continue;
            }

            int take = Fit(used, capacity, mode, remaining, out int offset);
            if (take <= 0)
            {
                break;
            }

            slab.Add(new Segment(r, r.Next, take, offset));
            used = offset + take;
            if (used >= capacity)
            {
                break;
            }
        }

        return used;
    }

    // The wire layout is the slab's RayRecord. A gap before an aligned
    // segment repeats that segment's first ray, a valid query whose answer
    // is masked away.
    private void Stage(Slab slab)
    {
        Memory<uint> staging = _device.StageRays(slab.Slot, slab.Total, slab.Record);
        int chunks = (slab.Total + PackChunkRays - 1) / PackChunkRays;
        if (chunks <= 1)
        {
            ObservePackChunk?.Invoke(0);
            Pack(slab.Segments, slab.Record, staging.Span, 0, slab.Total);
            return;
        }

        // The drainer takes part and never waits on a helper that has not
        // started (PackCrew), so the pack finishes even if no helper wakes.
        List<Segment> segments = slab.Segments;
        RayRecord record = slab.Record;
        int total = slab.Total;
        Action<int>? observe = ObservePackChunk;
        if (_crew is null)
        {
            Volatile.Write(ref _crew, new PackCrew(MaxPackThreads - 1));
        }

        _crew.Run(chunks, c =>
        {
            observe?.Invoke(c);
            Pack(segments, record, staging.Span, c * PackChunkRays, Math.Min(total, (c + 1) * PackChunkRays));
        });
    }

    /// <summary>
    /// Packs slab positions <paramref name="from"/> to <paramref name="to"/>
    /// (exclusive) of a planned slab: each segment's rays at its offset, and
    /// before an aligned segment, copies of its first ray in the gap.
    /// </summary>
    /// <param name="segments">The slab's segments, in offset order.</param>
    /// <param name="record">The slab's record.</param>
    /// <param name="staging">The whole slab's staging.</param>
    /// <param name="from">The first position to write.</param>
    /// <param name="to">One past the last.</param>
    /// <remarks>
    /// Any split of a slab into ranges writes the same words as one range,
    /// because every position's words are a function of the plan alone; a
    /// fact packs slabs whole and in pieces and compares them. A caller
    /// writing its own reservation writes the same words through
    /// <see cref="PackSegment"/>, the range from the end of the segment
    /// before it to the end of its own.
    /// </remarks>
    internal static void Pack(IReadOnlyList<Segment> segments, RayRecord record, Span<uint> staging, int from, int to)
    {
        int gapStart = 0;
        foreach (Segment s in segments)
        {
            int end = s.Offset + s.Count;
            if (end <= from)
            {
                gapStart = end;
                continue;
            }

            if (gapStart >= to)
            {
                break;
            }

            PackSegment(s, s.Request.Rays.Span.Slice(s.Start, s.Count), record, staging, Math.Max(gapStart, from), to);
            gapStart = end;
        }
    }

    /// <summary>
    /// Writes one segment and the gap before it, clipped to positions
    /// <paramref name="from"/> to <paramref name="to"/>: the gap from
    /// <paramref name="from"/> to the segment's offset takes copies of its
    /// first ray, then its own rays follow. Every store is in address order.
    /// </summary>
    /// <param name="s">The segment.</param>
    /// <param name="rays">Its rays, <c>s.Count</c> of them.</param>
    /// <param name="record">The slab's record.</param>
    /// <param name="staging">The whole slab's staging.</param>
    /// <param name="from">The first position to write; at most the segment's offset for its gap to be written.</param>
    /// <param name="to">One past the last position to write.</param>
    private static void PackSegment(Segment s, ReadOnlySpan<Ray> rays, RayRecord record, Span<uint> staging, int from, int to)
    {
        int words = record.Words;
        int end = s.Offset + s.Count;

        // The padding before this segment, clipped to the range.
        int gapTo = Math.Min(s.Offset, to);
        if (gapTo > from)
        {
            record.Fill(rays[0], gapTo - from, staging.Slice(from * words, (gapTo - from) * words));
        }

        // The segment's own rays, clipped to the range.
        int runFrom = Math.Max(s.Offset, from);
        int runTo = Math.Min(end, to);
        if (runTo > runFrom)
        {
            record.Pack(
                rays.Slice(runFrom - s.Offset, runTo - runFrom),
                staging.Slice(runFrom * words, (runTo - runFrom) * words));
        }
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
                // hit distance is. The reach comes from the caller's rays,
                // never from the slab: that may be device memory.
                hits[i] = new HitId(_triangleIds[prim], length == 1.0f ? t : t / length);
            }
        }
    }

    /// <summary>One caller's batch and how far through it the batcher is.</summary>
    internal sealed class Request(
        int mode,
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        Memory<HitId> hits,
        uint tminBits,
        CancellationToken cancellationToken)
    {
        public int Mode { get; } = mode;

        /// <summary>
        /// The reach every one of its rays shares, as float bits, or null
        /// (<see cref="RayRecord.UniformReachOf"/>). Worked out once, on the
        /// caller's thread, when the request is made.
        /// </summary>
        public uint? UniformReach { get; } = RayRecord.UniformReachOf(rays.Span);

        public ReadOnlyMemory<Ray> Rays { get; } = rays;

        public Memory<ulong> HitBits { get; } = hitBits;

        public Memory<HitId> Hits { get; } = hits;

        public uint TminBits { get; } = tminBits;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        /// <summary>Its place in arrival order, for the drainer's oldest-first choice.</summary>
        public long Seq { get; set; }

        /// <summary>
        /// The first ray not yet laid out in a slab; moves when a caller
        /// reserves room or a slab is planned, under the queue lock. Rays
        /// before it are in a slab (open, on the device) or answered.
        /// </summary>
        public int Next { get; set; }

        /// <summary>Rays answered so far; the request completes when this reaches its length.</summary>
        public int Answered { get; set; }

        /// <summary>
        /// How many slabs, open or on the device, carry part of it; guarded
        /// by the queue lock.
        /// </summary>
        public int HeldSlabs { get; set; }

        // Continuations run off the drainer, which must get back to the device.
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether a slab of this kind (mode, epsilon, record) may carry it.</summary>
        public bool Is(int mode, uint tminBits, uint? uniformReach) =>
            Mode == mode && TminBits == tminBits && UniformReach == uniformReach;
    }

    /// <summary>A run of one request's rays at a slab offset.</summary>
    /// <param name="Request">The request.</param>
    /// <param name="Start">The first ray, in the request's numbering.</param>
    /// <param name="Count">How many rays.</param>
    /// <param name="Offset">Where they sit in the slab.</param>
    internal readonly record struct Segment(Request Request, int Start, int Count, int Offset);

    /// <summary>A caller's claim on a slab's positions <c>From</c> to the segment's end.</summary>
    private readonly record struct Reservation(Page Page, Segment Segment, int From);

    /// <summary>
    /// A slot callers are filling: its kind, its memory, the segments
    /// reserved in it and how many are still being written. Guarded by the
    /// queue lock; only the words of <see cref="Rays"/> are written outside it.
    /// </summary>
    private sealed class Page(int slot, long seq, int mode, uint tminBits, uint? uniformReach, Memory<uint> rays)
    {
        public int Slot { get; } = slot;

        /// <summary>The <see cref="Request.Seq"/> of the request that opened it.</summary>
        public long Seq { get; } = seq;

        public int Mode { get; } = mode;

        public uint TminBits { get; } = tminBits;

        public uint? UniformReach { get; } = uniformReach;

        public RayRecord Record { get; } = RayRecord.For(uniformReach);

        public Memory<uint> Rays { get; } = rays;

        public List<Segment> Segments { get; } = [];

        /// <summary>Positions reserved, padding included: the dispatch's ray count.</summary>
        public int Used { get; set; }

        /// <summary>Reservations still being written.</summary>
        public int Writers { get; set; }

        /// <summary>Takes no more reservations: full, or claimed.</summary>
        public bool Sealed { get; set; }

        /// <summary>Taken by the drainer, which is waiting for its writers to submit it.</summary>
        public bool Claimed { get; set; }

        public bool Accepts(Request r) => r.Is(Mode, TminBits, UniformReach);
    }

    /// <summary>One planned slab: its slot, its kind, its record and its segments.</summary>
    private sealed class Slab(int slot, int mode, uint tminBits, RayRecord record)
    {
        public RayRecord Record { get; } = record;

        public int Slot { get; } = slot;

        public int Mode { get; } = mode;

        public uint TminBits { get; } = tminBits;

        public List<Segment> Segments { get; } = [];

        /// <summary>Rays the dispatch carries, padding included.</summary>
        public int Total { get; set; }

        /// <summary>Its rays are already in the slot, written by the callers: the drainer stages nothing.</summary>
        public bool WrittenByCallers { get; init; }

        /// <summary>Words the dispatch returns: 2 per 64-ray workgroup for any-hit, 2 per ray for closest.</summary>
        public int WordCount => Mode == 0 ? (Total + WorkgroupRays - 1) / WorkgroupRays * 2 : Total * 2;
    }
}
