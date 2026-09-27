//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// A stand-in for a host's GPU factory: offers a disposable tracer over the
/// casters' own KD tree, so the lighting is the CPU run's, and records every
/// tracer it handed out so a fact can count how often each was released.
/// </summary>
internal sealed class CountingGpuTracerFactory : IGpuTracerFactory
{
    private readonly List<CountingTracer> _offered = [];
    private readonly TaskCompletionSource _firstOffer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Every tracer offered so far.</summary>
    public IReadOnlyList<CountingTracer> Offered
    {
        get
        {
            lock (_offered)
            {
                return [.. _offered];
            }
        }
    }

    /// <summary>Completes once the first tracer has been handed out.</summary>
    public Task FirstOffer => _firstOffer.Task;

    /// <inheritdoc/>
    public ValueTask<GpuTracerOffer> TryCreateAsync(ShadowCasterSet casters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CountingTracer tracer = new(casters.BuildTracer());
        lock (_offered)
        {
            _offered.Add(tracer);
        }

        _firstOffer.TrySetResult();
        return ValueTask.FromResult(new GpuTracerOffer(tracer, null));
    }
}

/// <summary>
/// A disposable tracer that counts its disposals and refuses to trace once
/// disposed, so a release that comes too early fails the compile rather than
/// passing unnoticed.
/// </summary>
/// <param name="inner">The tracer that answers the rays.</param>
internal sealed class CountingTracer(KdRayTracer inner) : IRayTracer, IDisposable
{
    private int _disposals;

    /// <summary>How many times <see cref="Dispose"/> ran.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    /// <inheritdoc/>
    public string TracerIdentity => "counting-" + inner.TracerIdentity;

    /// <inheritdoc/>
    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposals > 0, this);
        return inner.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposals > 0, this);
        return inner.TraceClosestAsync(rays, hits, options, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose() => Interlocked.Increment(ref _disposals);
}

/// <summary>A synchronous progress sink that runs an action on the reports it picks.</summary>
/// <param name="when">Which reports to act on.</param>
/// <param name="act">What to do; throwing here fails the stage that reported.</param>
internal sealed class ActAt(Func<CompileProgress, bool> when, Action act) : IProgress<CompileProgress>
{
    /// <inheritdoc/>
    public void Report(CompileProgress value)
    {
        if (when(value))
        {
            act();
        }
    }
}
