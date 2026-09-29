//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapTools.Tracing;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// An <see cref="IRayTracer"/> shaped like the GPU one at its worst: every
/// batch comes back on another thread, after a random delay, so batches finish
/// out of the order they were asked in. It answers with an inner tracer AT
/// COMPLETION, from the caller's memory as it is then, so a caller that
/// reused a batch's rays or answers while the batch was in flight gets wrong
/// answers rather than lucky ones.
/// </summary>
/// <remarks>
/// <para>
/// It counts every ray it is asked, by query kind, so a fact can check the
/// bench's counters against what really reached the tracer, and it tracks how
/// many batches are outstanding at once, so a fact can check an in-flight
/// bound. <see cref="FailCall"/> faults one chosen batch, as a slab the device
/// failed would, and <see cref="Tasks"/> lets a fact prove that every batch had
/// finished by the time the stage returned (nothing left writing into memory
/// the stage has given back).
/// </para>
/// <para>
/// A cancelled token is honoured only at completion, as the slab batcher
/// honours it between slabs: the batch is then cancelled without writing.
/// </para>
/// </remarks>
/// <param name="inner">The tracer that answers; asked synchronously at completion.</param>
/// <param name="seed">Seeds the delays, so a failing run can be repeated.</param>
/// <param name="maxDelayMs">The longest delay, in milliseconds; 0 only yields.</param>
/// <param name="asynchronous">False answers inside the call, like the CPU tracer.</param>
internal sealed class ScrambledRayTracer(IRayTracer inner, int seed = 1, int maxDelayMs = 2, bool asynchronous = true)
    : IRayTracer
{
    private readonly Random _random = new(seed);
    private long _visibilityRays;
    private long _skyRays;
    private long _closestRays;
    private int _calls;
    private int _outstanding;
    private int _peakOutstanding;

    /// <summary>Faults the batch with this call number (0-based, in call order); -1 for none.</summary>
    public int FailCall { get; init; } = -1;

    /// <summary>Plain visibility rays asked.</summary>
    public long VisibilityRays => Interlocked.Read(ref _visibilityRays);

    /// <summary>Sky pass-through visibility rays asked.</summary>
    public long SkyRays => Interlocked.Read(ref _skyRays);

    /// <summary>Closest-hit rays asked.</summary>
    public long ClosestRays => Interlocked.Read(ref _closestRays);

    /// <summary>Every ray asked.</summary>
    public long TotalRays => VisibilityRays + SkyRays + ClosestRays;

    /// <summary>Batches asked.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Batches asked and not yet finished.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>The most batches that were outstanding at once.</summary>
    public int PeakOutstanding => Volatile.Read(ref _peakOutstanding);

    /// <summary>Every asynchronous batch's task.</summary>
    public ConcurrentBag<Task> Tasks { get; } = [];

    /// <inheritdoc/>
    public string TracerIdentity => "scrambled+" + inner.TracerIdentity;

    /// <inheritdoc/>
    public bool Supports(RayTraceOptions options) => inner.Supports(options);

    /// <inheritdoc/>
    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref options.SkyDoesNotBlock ? ref _skyRays : ref _visibilityRays, rays.Length);
        return Run(() => inner.TraceVisibilityAsync(rays, hitBits, options, CancellationToken.None), cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _closestRays, rays.Length);
        return Run(() => inner.TraceClosestAsync(rays, hits, options, CancellationToken.None), cancellationToken);
    }

    private ValueTask Run(Func<ValueTask> answer, CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _calls) - 1;
        if (!asynchronous)
        {
            return call == FailCall ? ValueTask.FromException(Planted()) : answer();
        }

        int delay;
        lock (_random)
        {
            delay = _random.Next(maxDelayMs + 1);
        }

        int now = Interlocked.Increment(ref _outstanding);
        int peak;
        while (now > (peak = Volatile.Read(ref _peakOutstanding))
               && Interlocked.CompareExchange(ref _peakOutstanding, now, peak) != peak)
        {
        }

        Task task = Task.Run(async () =>
        {
            try
            {
                if (delay > 0)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }
                else
                {
                    await Task.Yield();
                }

                if (call == FailCall)
                {
                    throw Planted();
                }

                cancellationToken.ThrowIfCancellationRequested();
                await answer().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _outstanding);
            }
        });
        Tasks.Add(task);
        return new ValueTask(task);
    }

    private static InvalidOperationException Planted() => new("planted slab failure");
}
