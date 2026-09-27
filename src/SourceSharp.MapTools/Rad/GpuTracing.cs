//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// What an <see cref="IGpuTracerFactory"/> made of a shadow-caster set: a
/// tracer, or the reason the device was refused.
/// </summary>
/// <param name="Tracer">
/// The GPU-ready tracer holding the casters, or null when the host declined
/// the device. Null is a normal answer, not a failure: §10c's rule is that
/// absence of a capable device is a clean fallback, never an error. An
/// offered tracer is handed over: vrad releases it when the compile ends
/// (see <see cref="IGpuTracerFactory"/>'s remarks).
/// </param>
/// <param name="DeclineReason">
/// Why <paramref name="Tracer"/> is null, in the backend's own words (the
/// self-test signature it matched, or the device inventory when a pin named
/// nothing). Ignored when a tracer was offered.
/// </param>
public readonly record struct GpuTracerOffer(IRayTracer? Tracer, string? DeclineReason);

/// <summary>
/// The host's GPU-tracer seam: build the ray tracer for this shadow-caster
/// set on the GPU, or decline.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="VradContext.Tracer"/> for a host that cannot
/// supply the tracer before the casters exist: the casters are vrad's own
/// load, and only once they are known can a GPU scene be built. The host —
/// <c>ssmap</c> under <c>-gpu</c> — supplies the factory; the core assembly
/// references no GPU code and loads nothing itself (the same posture as the
/// SQLite cache: package knowledge lives in the host).
/// </para>
/// <para>
/// A factory that offers a tracer gets it wrapped with the CPU KD tracer
/// (<see cref="HybridRayTracer"/>) and used for the batch operations. A
/// factory that declines — no device, no package, a pin matching nothing, a
/// self-test the driver failed — costs one warning and the run proceeds on
/// the CPU tracer, byte-for-byte the flag-free build.
/// </para>
/// <para>
/// <b>Ownership.</b> An offered tracer becomes the compile's: vrad releases
/// it, through <see cref="IDisposable"/> when the tracer implements it, once
/// the lighting ends, whether it finished, failed or was cancelled, and a
/// <see cref="VradPreparation"/> that is disposed unlit releases it too. The
/// factory and the host keep no reference and never dispose it themselves.
/// That is what lets a long-lived host run compile after compile without a
/// device staying open behind each one.
/// </para>
/// </remarks>
public interface IGpuTracerFactory
{
    /// <summary>Build the GPU scene for these casters, or decline with the reason.</summary>
    /// <param name="casters">The map's shadow casters, exactly as vrad loaded them.</param>
    /// <param name="cancellationToken">The compile's token; honour it or throw it.</param>
    /// <returns>The tracer or the reason.</returns>
    ValueTask<GpuTracerOffer> TryCreateAsync(ShadowCasterSet casters, CancellationToken cancellationToken);
}

/// <summary>
/// The GPU batch tracer with the CPU KD tracer riding along: batch
/// visibility and closest-hit go to the GPU; the line-sampling stages
/// (<see cref="RadPass"/>'s prop and leaf-ambient samplers, which call
/// <c>KdRayTracer.TestLines</c> outside the <see cref="IRayTracer"/> seam)
/// keep the KD tree they were built with.
/// </summary>
/// <remarks>
/// <para>
/// The two tracers answer the same scene — the hybrid hands
/// <c>casters.Triangles</c> to the factory verbatim — so the only difference
/// between a hybrid run and a CPU run is which arithmetic answers the batch
/// queries, which is exactly the population §10d's parity gate measures (0
/// hit-bit disagreements off-plane; closest-hit ids and fractions inside the
/// 1e-3 band).
/// </para>
/// <para>
/// <see cref="TracerIdentity"/> names both halves: it feeds the lighting
/// cache key (plan 10a), and a product made through a hybrid must never be
/// read by a pure-CPU or pure-GPU build.
/// </para>
/// </remarks>
public sealed class HybridRayTracer : IRayTracer, IDisposable
{
    private readonly IRayTracer _gpu;

    /// <summary>Wraps a GPU batch tracer over the CPU tracer of the same casters.</summary>
    /// <param name="gpu">The GPU tracer; owned by this instance and disposed with it.</param>
    /// <param name="cpu">The KD tracer, built first; not owned (its lifetime is vrad's).</param>
    public HybridRayTracer(IRayTracer gpu, KdRayTracer cpu)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        CpuTracer = cpu ?? throw new ArgumentNullException(nameof(cpu));
    }

    /// <summary>The KD tracer the line samplers use.</summary>
    public KdRayTracer CpuTracer { get; }

    /// <inheritdoc/>
    public string TracerIdentity => _gpu.TracerIdentity + "+kd-lines";

    /// <inheritdoc/>
    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default) =>
        _gpu.TraceVisibilityAsync(rays, hitBits, options, cancellationToken);

    /// <inheritdoc/>
    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<HitId> hits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default) =>
        _gpu.TraceClosestAsync(rays, hits, options, cancellationToken);

    /// <summary>Releases the GPU tracer this hybrid owns, if it is disposable; the KD tracer is vrad's.</summary>
    public void Dispose() => (_gpu as IDisposable)?.Dispose();
}
