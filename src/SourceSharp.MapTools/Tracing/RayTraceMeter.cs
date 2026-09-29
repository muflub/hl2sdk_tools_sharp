//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;

namespace SourceSharp.MapTools.Tracing;

/// <summary>The three questions a batch can ask an <see cref="IRayTracer"/>.</summary>
/// <remarks>
/// Split by what the tracer is asked, not by which stage asks it: a GPU kernel
/// answers the three with different amounts of work (the sky pass-through is a
/// closest-hit trace folded to bits on the host), so a run that is slow on the
/// GPU is explained by this split, not by the stage names.
/// </remarks>
public enum RayQueryKind
{
    /// <summary><see cref="IRayTracer.TraceVisibilityAsync"/> where the sky blocks like anything else.</summary>
    Visibility,

    /// <summary><see cref="IRayTracer.TraceClosestAsync"/>.</summary>
    Closest,

    /// <summary>
    /// <see cref="IRayTracer.TraceVisibilityAsync"/> with
    /// <see cref="RayTraceOptions.SkyDoesNotBlock"/>: the leaf-ambient and prop
    /// samplers' sky test.
    /// </summary>
    Sky,
}

/// <summary>The stages whose workers can park on a tracer batch that has not come back.</summary>
public enum TraceWaitStage
{
    /// <summary>Direct light, <c>BuildFacelights</c>.</summary>
    Facelights,

    /// <summary>The bounce's visibility matrix, <c>BuildVisLeafs</c>.</summary>
    Bounce,

    /// <summary>Detail-prop, leaf-ambient and static-prop lighting (<c>ComputeOtherLighting</c>).</summary>
    Other,
}

/// <summary>Which backend answered a compile's batches.</summary>
public enum GpuTraceStatus
{
    /// <summary>No GPU was asked for: every batch was the CPU tracer's.</summary>
    Off,

    /// <summary>A GPU was asked for and the host's factory declined it; every batch was the CPU tracer's.</summary>
    Declined,

    /// <summary>A GPU tracer answered every batch whose options it honours.</summary>
    On,
}

/// <summary>Rays and batches that went one way (GPU or CPU), by query kind.</summary>
/// <param name="Visibility">Rays asked <see cref="RayQueryKind.Visibility"/>.</param>
/// <param name="Closest">Rays asked <see cref="RayQueryKind.Closest"/>.</param>
/// <param name="Sky">Rays asked <see cref="RayQueryKind.Sky"/>.</param>
/// <param name="Batches">Calls made, of every kind.</param>
public readonly record struct RayRouteCounts(long Visibility, long Closest, long Sky, long Batches)
{
    /// <summary>Every ray, whatever it asked.</summary>
    public long Rays => Visibility + Closest + Sky;
}

/// <summary>
/// What a GPU tracer did over its life: the slabs it put on the device, how
/// long the device had work, and how deep the queue ran.
/// </summary>
/// <param name="Requests">Batches the tracer was handed (a request can span several slabs, and a slab carries many requests).</param>
/// <param name="Slabs">Dispatches submitted to the device.</param>
/// <param name="Busy">
/// Wall time during which at least one slab was on the device: from the
/// submit that took the device from idle to busy, to the landing that took it
/// back. The union, not the sum, so overlapped slabs are counted once.
/// </param>
/// <param name="FenceWait">
/// Wall time the tracer's drainer spent blocked on a slab's fence: the part of
/// <paramref name="Busy"/> in which the host had nothing left to pack and was
/// waiting on the device.
/// </param>
/// <param name="PeakSlabsInFlight">The most slabs that were on the device at once.</param>
/// <param name="Slots">How many slabs the device may hold at once.</param>
/// <param name="Pack">
/// Host time spent writing slabs' rays into the memory the device reads them
/// from: over the bus into device memory when <paramref name="RaysInPlace"/>,
/// into a host staging buffer (which the device then copies itself) when not.
/// </param>
/// <param name="Readback">
/// Host time spent reading slabs' answers out of the memory the device wrote
/// them to, after the fence; from device memory when
/// <paramref name="AnswersInPlace"/>, from a host buffer the device copied
/// them into when not.
/// </param>
/// <param name="RaysInPlace">
/// Whether the kernel reads rays where the host packs them (a device heap
/// the host can map: resizable BAR or an integrated GPU), so a slab has no
/// upload copy; false when every slab is staged and copied by the device.
/// </param>
/// <param name="AnswersInPlace">Whether the kernel writes answers where the host reads them, so a slab has no download copy.</param>
/// <remarks>
/// <para>
/// <b>Why the fence span and not device timestamps.</b> Timestamps would time
/// the kernel alone, but they need a query pool, a per-device tick period and
/// a readback in the dispatch path, all in the interop layer. The span the
/// host sees costs two clock reads per slab and answers the question the
/// bench is asked -- does the device ever run dry while the compile waits on
/// it? -- directly: <paramref name="Busy"/> against the stage's wall time is
/// how fed the device was. It is an upper bound on kernel time: it includes
/// the transfer, queueing behind the previous slab, and the latency between
/// the fence signalling and the drainer noticing, and a slab that finished
/// while the drainer was still packing the next one is counted until the
/// drainer lands it.
/// </para>
/// <para>
/// <b>The copies.</b> <paramref name="Pack"/> and <paramref name="Readback"/>
/// are the host's side of moving a slab, timed on the host, so they are
/// exact. The device's own staging copies, when the layout is not in place,
/// run in the slab's command buffer between the host's submit and the
/// fence, and are inside <paramref name="Busy"/> without being separable
/// from the trace (that would take the device timestamps above). The layout
/// flags say whether there are any: a device without resizable BAR stages
/// every slab both ways, and a slab's rays cross the bus twice as long a
/// path.
/// </para>
/// </remarks>
public readonly record struct GpuTraceStatistics(
    long Requests,
    long Slabs,
    TimeSpan Busy,
    TimeSpan FenceWait,
    int PeakSlabsInFlight,
    int Slots,
    TimeSpan Pack = default,
    TimeSpan Readback = default,
    bool RaysInPlace = false,
    bool AnswersInPlace = false);

/// <summary>A tracer that can say what its device did, for the bench.</summary>
/// <remarks>
/// In the core so a host's GPU backend, which the core never references, can
/// report through it; <see cref="Rad.HybridRayTracer"/> forwards its GPU half's.
/// </remarks>
public interface IGpuTraceStatistics
{
    /// <summary>What the device has done so far; safe to read while it works.</summary>
    GpuTraceStatistics GpuStatistics { get; }
}

/// <summary>A compile's tracer counters, as the bench prints them.</summary>
/// <param name="TracerIdentity">The traced-with identity, <see cref="IRayTracer.TracerIdentity"/>.</param>
/// <param name="Gpu">Whether a GPU was asked for and whether it answered.</param>
/// <param name="GpuDeclineReason">Why the GPU was declined, in the backend's words; null unless <see cref="GpuTraceStatus.Declined"/>.</param>
/// <param name="GpuRays">What the GPU answered.</param>
/// <param name="CpuRays">What the CPU answered: everything when the GPU is off or declined, and the fallback batches when it is on.</param>
/// <param name="Parked">Worker time spent parked on batches in flight, per <see cref="TraceWaitStage"/>, indexed by it.</param>
/// <param name="Device">The GPU's own statistics, when the tracer reports them.</param>
public sealed record RayTraceReport(
    string TracerIdentity,
    GpuTraceStatus Gpu,
    string? GpuDeclineReason,
    RayRouteCounts GpuRays,
    RayRouteCounts CpuRays,
    IReadOnlyList<TimeSpan> Parked,
    GpuTraceStatistics? Device)
{
    /// <summary>Every ray traced through the seam, both ways.</summary>
    public long TotalRays => GpuRays.Rays + CpuRays.Rays;

    /// <summary>Parked time over every stage.</summary>
    public TimeSpan TotalParked
    {
        get
        {
            TimeSpan sum = TimeSpan.Zero;
            foreach (TimeSpan t in Parked)
            {
                sum += t;
            }

            return sum;
        }
    }

    /// <summary>Parked time in one stage.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>Its parked worker time, zero when it never parked.</returns>
    public TimeSpan ParkedIn(TraceWaitStage stage) => (int)stage < Parked.Count ? Parked[(int)stage] : TimeSpan.Zero;
}

/// <summary>
/// One compile's tracer counters: how many rays went to the GPU and to the
/// CPU, by query kind, and how long the stages' workers sat parked on a batch
/// still in flight.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per compile, never static.</b> vrad makes one when a compile starts and
/// drops it with the compile, so two compiles in one service count their own
/// rays. Every counter moves by <see cref="Interlocked"/>: the tracer is called
/// from every worker at once, and a lock per batch would be the very
/// contention the batch seam exists to avoid. A batch costs two interlocked
/// adds, against a batch of thousands of rays.
/// </para>
/// <para>
/// <b>Parked time is worker time</b>, the sum over workers of the spans in
/// which a worker had nothing to do but wait for its batch, so 4 workers parked
/// for 1 s is 4 s. Each stage measures it where its workers park:
/// the face lighting per worker, from the moment one cannot go on until an
/// answer lets it; the prop and leaf-ambient stages likewise, per runner; the
/// bounce, whose workers never park one by one but all wait together while
/// the chunk's slabs finish, as that wait times the stage's worker count.
/// </para>
/// </remarks>
public sealed class RayTraceMeter
{
    private const int Kinds = 3;
    private const int Stages = 3;

    // [route * Kinds + kind], route 0 CPU and 1 GPU. Instance arrays, one per
    // compile; the no-mutable-statics rule is about sharing, not arrays.
    private readonly long[] _rays = new long[2 * Kinds];
    private readonly long[] _batches = new long[2];
    private readonly long[] _parkedTicks = new long[Stages];

    /// <summary>Counts one batch.</summary>
    /// <param name="gpu">Whether the GPU answers it.</param>
    /// <param name="kind">What it asks.</param>
    /// <param name="rays">How many rays it carries.</param>
    public void AddBatch(bool gpu, RayQueryKind kind, int rays)
    {
        int route = gpu ? 1 : 0;
        Interlocked.Add(ref _rays[(route * Kinds) + (int)kind], rays);
        Interlocked.Increment(ref _batches[route]);
    }

    /// <summary>Adds parked worker time to a stage.</summary>
    /// <param name="stage">The stage whose workers parked.</param>
    /// <param name="stopwatchTicks">The time, in <see cref="Stopwatch"/> ticks.</param>
    public void AddParked(TraceWaitStage stage, long stopwatchTicks)
    {
        if (stopwatchTicks > 0)
        {
            Interlocked.Add(ref _parkedTicks[(int)stage], stopwatchTicks);
        }
    }

    /// <summary>The rays one way went, so far.</summary>
    /// <param name="gpu">The GPU's, or the CPU's.</param>
    /// <returns>The counts.</returns>
    public RayRouteCounts Route(bool gpu)
    {
        int b = gpu ? Kinds : 0;
        return new RayRouteCounts(
            Interlocked.Read(ref _rays[b + (int)RayQueryKind.Visibility]),
            Interlocked.Read(ref _rays[b + (int)RayQueryKind.Closest]),
            Interlocked.Read(ref _rays[b + (int)RayQueryKind.Sky]),
            Interlocked.Read(ref _batches[gpu ? 1 : 0]));
    }

    /// <summary>A stage's parked worker time so far.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>The time.</returns>
    public TimeSpan Parked(TraceWaitStage stage) =>
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _parkedTicks[(int)stage]));

    /// <summary>The meter behind a tracer, when it is a <see cref="MeteredRayTracer"/>.</summary>
    /// <param name="tracer">The tracer a stage was handed.</param>
    /// <returns>Its meter, or null for an unmetered tracer (a stage run outside vrad, as the facts do).</returns>
    public static RayTraceMeter? Of(IRayTracer tracer) => (tracer as MeteredRayTracer)?.Meter;

    /// <summary>The report the bench prints.</summary>
    /// <param name="tracer">The metered tracer.</param>
    /// <param name="gpu">Whether a GPU was asked for and answered.</param>
    /// <param name="declineReason">Why it was declined, or null.</param>
    /// <returns>The counters as they stand.</returns>
    public RayTraceReport Report(MeteredRayTracer tracer, GpuTraceStatus gpu, string? declineReason)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        TimeSpan[] parked = new TimeSpan[Stages];
        for (int s = 0; s < Stages; s++)
        {
            parked[s] = Parked((TraceWaitStage)s);
        }

        return new RayTraceReport(
            tracer.TracerIdentity,
            gpu,
            gpu == GpuTraceStatus.Declined ? declineReason : null,
            Route(gpu: true),
            Route(gpu: false),
            parked,
            tracer.GpuStatistics);
    }
}

/// <summary>
/// Counts every batch on its way to the compile's tracer, by the way it goes
/// (GPU or CPU) and the question it asks, and answers nothing itself.
/// </summary>
/// <param name="inner">The tracer that answers.</param>
/// <param name="meter">Where the counts go.</param>
/// <remarks>
/// <para>
/// A decorator rather than counters inside each tracer, so the CPU tracer, the
/// hybrid and a host's tracer are all counted the same way, and a batch is
/// counted once however the hybrid routes it. It forwards the call and returns
/// the inner tracer's own task, so a CPU batch still completes inside the call
/// and nothing is allocated per batch.
/// </para>
/// <para>
/// <b>Which way a batch goes.</b> Through a <see cref="Rad.HybridRayTracer"/>,
/// the hybrid's own routing (<see cref="Rad.HybridRayTracer.TracerFor"/>): the
/// GPU when it honours the options, the KD tracer otherwise. Any other tracer
/// is a GPU when it reports GPU statistics (<see cref="IGpuTraceStatistics"/>)
/// and the CPU otherwise.
/// </para>
/// <para>
/// Not disposable: vrad releases the tracer it wraps, not this.
/// </para>
/// </remarks>
public sealed class MeteredRayTracer(IRayTracer inner, RayTraceMeter meter) : IRayTracer
{
    private readonly IRayTracer _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The meter the counts go to.</summary>
    public RayTraceMeter Meter { get; } = meter ?? throw new ArgumentNullException(nameof(meter));

    /// <summary>The tracer that answers.</summary>
    public IRayTracer Inner => _inner;

    /// <inheritdoc/>
    /// <remarks>The inner tracer's: counting changes no answer, so a cached product is the same product.</remarks>
    public string TracerIdentity => _inner.TracerIdentity;

    /// <summary>The GPU's statistics, when the inner tracer (or the hybrid's GPU half) reports them.</summary>
    public GpuTraceStatistics? GpuStatistics => _inner switch
    {
        Rad.HybridRayTracer hybrid => (hybrid.GpuTracer as IGpuTraceStatistics)?.GpuStatistics,
        IGpuTraceStatistics gpu => gpu.GpuStatistics,
        _ => null,
    };

    /// <inheritdoc/>
    public bool Supports(RayTraceOptions options) => _inner.Supports(options);

    /// <summary>Whether a batch with these options is answered on the GPU.</summary>
    /// <param name="options">The batch's options.</param>
    /// <returns>True for the GPU.</returns>
    public bool IsGpu(RayTraceOptions options) => _inner switch
    {
        Rad.HybridRayTracer hybrid => !ReferenceEquals(hybrid.TracerFor(options), hybrid.CpuTracer),
        IGpuTraceStatistics => true,
        _ => false,
    };

    /// <inheritdoc/>
    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        Meter.AddBatch(IsGpu(options), options.SkyDoesNotBlock ? RayQueryKind.Sky : RayQueryKind.Visibility, rays.Length);
        return _inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<HitId> hits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        Meter.AddBatch(IsGpu(options), RayQueryKind.Closest, rays.Length);
        return _inner.TraceClosestAsync(rays, hits, options, cancellationToken);
    }
}
