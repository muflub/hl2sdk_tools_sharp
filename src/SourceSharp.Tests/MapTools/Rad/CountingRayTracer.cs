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
/// An <see cref="IRayTracer"/> that records every batch it is asked and
/// answers it with an inner tracer, optionally only after yielding -- the
/// shape of a GPU tracer's incomplete task -- and optionally supporting only
/// the plain query, as a kernel without id skipping would.
/// </summary>
/// <param name="inner">The tracer that answers.</param>
/// <param name="asynchronous">Complete each batch on another thread, after the call returns.</param>
/// <param name="plainOnly">Say no to a skipped id or sky pass-through, and throw if given one.</param>
internal sealed class CountingRayTracer(IRayTracer inner, bool asynchronous = false, bool plainOnly = false) : IRayTracer
{
    /// <summary>Every visibility batch: its size and options, in call order (per thread, then merged).</summary>
    public ConcurrentQueue<(int Rays, RayTraceOptions Options)> VisibilityCalls { get; } = new();

    /// <summary>How many closest-hit batches were asked.</summary>
    public int ClosestCalls => _closest;

    private int _closest;

    public string TracerIdentity => "counting+" + inner.TracerIdentity;

    public bool Supports(RayTraceOptions options) => !plainOnly || options.IsPlain;

    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
    {
        if (!Supports(options))
        {
            throw new NotSupportedException("plain queries only");
        }

        VisibilityCalls.Enqueue((rays.Length, options));
        if (!asynchronous)
        {
            return inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
        }

        return new ValueTask(Task.Run(
            async () =>
            {
                await Task.Yield();
                await inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
            },
            cancellationToken));
    }

    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _closest);
        return inner.TraceClosestAsync(rays, hits, options, cancellationToken);
    }
}
