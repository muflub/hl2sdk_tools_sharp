//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Tracing;

/// <summary>
/// One ray: where it starts, which way it goes, and how far it counts.
/// </summary>
/// <param name="OriginX">Start position, X.</param>
/// <param name="OriginY">Start position, Y.</param>
/// <param name="OriginZ">Start position, Z.</param>
/// <param name="DirectionX">Direction, X. Not required to be unit length.</param>
/// <param name="DirectionY">Direction, Y.</param>
/// <param name="DirectionZ">Direction, Z.</param>
/// <param name="MaxDistance">
/// How far along <c>Direction</c> a hit still counts, in units of
/// <c>Direction</c>'s length.
/// </param>
/// <remarks>
/// Six floats and a distance, laid out flat rather than as two vectors, because
/// these are uploaded to a GPU by the thousand and the layout is the wire
/// format.
/// </remarks>
public readonly record struct Ray(
    float OriginX,
    float OriginY,
    float OriginZ,
    float DirectionX,
    float DirectionY,
    float DirectionZ,
    float MaxDistance)
{
    /// <summary>
    /// The ray stock's <c>TestLine</c> traces for the segment from
    /// <paramref name="start"/> to <paramref name="end"/>: the direction
    /// normalised by the segment's length and the length as the reach.
    /// </summary>
    /// <param name="start">Where the segment starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="stockReciprocal">
    /// Normalise with stock's reciprocal estimate plus one Newton step, as its
    /// <c>ReciprocalSIMD</c> does; false divides exactly. Which one a caller
    /// wants is that caller's compliance decision, so it is a parameter here.
    /// </param>
    /// <returns>The ray.</returns>
    /// <remarks>
    /// <para>
    /// ONE PLACE FOR THE ARITHMETIC. <see cref="KdRayTracer.TestLines(ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, Span{bool}, bool, bool, int)"/>
    /// and every sampler that hands <c>TestLine</c> segments to an
    /// <see cref="IRayTracer"/> build their rays here, so a batch traced
    /// through the seam starts from the same floats as the direct call and
    /// the answers can be the same bits.
    /// </para>
    /// <para>
    /// The length is <c>sqrt(x*x + y*y + z*z)</c> in float, in that order,
    /// which is stock's vector length. A zero-length segment divides by zero
    /// and yields a NaN direction, which stock also traces; it hits nothing.
    /// When the machine has no estimate instruction the exact reciprocal is
    /// used whatever <paramref name="stockReciprocal"/> says, as everywhere
    /// else <see cref="FloatEstimate"/> is consulted.
    /// </para>
    /// </remarks>
    public static Ray Segment(Vec3 start, Vec3 end, bool stockReciprocal)
    {
        Vec3 d = end - start;
        float len = MathF.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));
        float inv;
        if (stockReciprocal && FloatEstimate.IsSupported)
        {
            Vector128<float> a = Vector128.Create(len);
            Vector128<float> est = FloatEstimate.Reciprocal(a);
            inv = Vector128.Subtract(Vector128.Add(est, est), Vector128.Multiply(a, Vector128.Multiply(est, est))).ToScalar();
        }
        else
        {
            inv = 1.0f / len;
        }

        return new Ray(start.X, start.Y, start.Z, d.X * inv, d.Y * inv, d.Z * inv, len);
    }
}

/// <summary>
/// Answers batches of geometric questions about a fixed set of surfaces.
/// </summary>
/// <remarks>
/// <para>
/// BATCH-ONLY, deliberately. Most rays vrad casts are any-hit visibility
/// queries -- "is anything between this sample and that light?" -- with no
/// shading at the hit, and the useful implementations of that want thousands
/// of rays at once: the wide CPU packet tracer wants them to fill its lanes,
/// and a GPU wants them to amortise a PCIe round trip. A ray-at-a-time entry
/// point would be used by accident and would erase both.
/// </para>
/// <para>
/// TWO first-class operations, not one and a variant. The seam was written
/// visibility-only, and that was wrong by wall clock: leaf ambient is 51.6 %
/// of stock vrad's run and it is a CLOSEST-HIT stage --
/// <c>CalcRayAmbientLighting</c> samples the lightmap of the surface the ray
/// landed on, so it needs an identity and a distance, not a bit. A stage that
/// large cannot hang off an afterthought, and the two operations are gated
/// differently besides: over 6,319,296 rays on a real map, hit BITS disagreed
/// between two tracers in 19 rays while closest-hit IDS disagreed in 19,155.
/// </para>
/// <para>
/// The tracer returns ONLY hit bits. Falloff, dot products, accumulation and
/// <c>ColorRGBExp32</c> encoding stay on the CPU in their fixed order. The
/// consequence is worth stating plainly: wherever a GPU's bits equal the CPU's,
/// the lightmap bytes are IDENTICAL rather than merely close, and the only
/// possible differences are rays that graze an edge -- which can be counted. A
/// second implementation of the lighting maths in shader code, to be kept in
/// parity forever, is exactly what this avoids.
/// </para>
/// </remarks>
public interface IRayTracer
{
    /// <summary>
    /// Answers, for each ray, whether anything blocks it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Blocks" means a hit strictly SHORT of the segment's end,
    /// <c>Origin + MaxDistance * Direction</c>: a surface at or past the end
    /// sets no bit. That is stock's own test, which its callers make after an
    /// unclipped trace (<c>HitDistance &lt; len</c>,
    /// <c>HitDistance &gt;= ray_length</c> makes a
    /// transfer). Every implementation, GPU included, must honour it.
    /// </para>
    /// <para>
    /// <see cref="Memory{T}"/> rather than <see cref="Span{T}"/> because the
    /// call crosses an <c>await</c> for the GPU backend. It returns a
    /// <see cref="ValueTask"/> so that the CPU tracer, which finishes
    /// synchronously on a worker thread, allocates nothing per batch.
    /// </para>
    /// </remarks>
    /// <param name="rays">The rays to trace.</param>
    /// <param name="hitBits">
    /// Receives one bit per ray, least-significant bit first: set when the ray
    /// hit something. Must hold at least <c>(rays.Length + 63) / 64</c>
    /// elements.
    /// </param>
    /// <param name="options">
    /// The ray epsilon this batch is traced with. §10c: the difference between
    /// 0 and 1e-3 here was 1,769 differing bits against 19 on the same rays, so
    /// it is a parameter rather than a constant somebody picks per tracer.
    /// </param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    /// <returns>A task that completes when every bit has been written.</returns>
    ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers, for each ray, WHICH surface it hit first and how far along.
    /// </summary>
    /// <param name="rays">The rays to trace.</param>
    /// <param name="hits">
    /// Receives one <see cref="HitId"/> per ray, in the rays' own order. Must
    /// be at least as long as <paramref name="rays"/>. A ray that hits nothing
    /// writes <see cref="HitId.Missed"/>.
    /// </param>
    /// <param name="options">The ray epsilon this batch is traced with.</param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    /// <returns>A task that completes when every hit has been written.</returns>
    /// <remarks>
    /// This serves the single largest stage in vrad, so it is not the
    /// "closest-hit variant" an earlier draft of §10c called it. The identity
    /// it returns is the tracer's own numbering -- see <see cref="HitId"/> --
    /// which is why <see cref="TracerIdentity"/> has to go in the cache key
    /// alongside any product built from these.
    /// </remarks>
    ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<HitId> hits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this tracer honours <paramref name="options"/> in
    /// <see cref="TraceVisibilityAsync"/>.
    /// </summary>
    /// <param name="options">The options a batch would be traced with.</param>
    /// <returns>
    /// True when a visibility batch with these options gets the answer they
    /// ask for; false when this tracer would have to ignore one of them.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A DEFAULT, so a tracer written before <see cref="RayTraceOptions.SkipId"/>
    /// and <see cref="RayTraceOptions.SkyDoesNotBlock"/> existed keeps
    /// compiling and says, correctly, that it answers only the plain query.
    /// <see cref="RayTraceOptions.IsolatedRays"/> needs nothing from a tracer
    /// that traces rays one at a time and is not part of the answer.
    /// </para>
    /// <para>
    /// Callers ask before they trace. A tracer handed options it does not
    /// support may simply not look at them, and would then answer a different
    /// question: a static prop that silently shadowed itself is a wrong
    /// lightmap with nothing in the log to say why. The GPU tracer throws
    /// instead, and the samplers that need these options refuse, up front, a
    /// tracer that says no.
    /// </para>
    /// </remarks>
    bool Supports(RayTraceOptions options) => options.IsPlain;

    /// <summary>
    /// Identifies which tracer produced a result, for the cache.
    /// </summary>
    /// <remarks>
    /// Backend, device and driver. A cached lighting product made on a GPU must
    /// never be consumed by a CPU build or the reverse, so this goes into the
    /// cache key rather than being merely informative.
    /// </remarks>
    string TracerIdentity { get; }
}
