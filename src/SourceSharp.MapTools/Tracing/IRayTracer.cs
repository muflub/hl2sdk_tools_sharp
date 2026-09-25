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
    float MaxDistance);

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
/// of stock vrad's run (§10c) and it is a CLOSEST-HIT stage --
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
    /// unclipped trace (<c>trace.cpp:171</c> <c>HitDistance &lt; len</c>,
    /// <c>vismat.cpp:86</c> <c>HitDistance &gt;= ray_length</c> makes a
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
    /// Identifies which tracer produced a result, for the cache.
    /// </summary>
    /// <remarks>
    /// Backend, device and driver. A cached lighting product made on a GPU must
    /// never be consumed by a CPU build or the reverse, so this goes into the
    /// cache key rather than being merely informative.
    /// </remarks>
    string TracerIdentity { get; }
}
