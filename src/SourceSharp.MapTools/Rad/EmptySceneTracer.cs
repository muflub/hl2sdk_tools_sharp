using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// The tracer of a map with no shadow casters: every ray misses.
/// </summary>
/// <remarks>
/// <see cref="ShadowCasterSet.BuildTracer(Options.ComplianceOptions)"/> refuses an empty set, because
/// stock's KD build would divide by zero computing its bounds. An empty scene
/// has an obvious answer that needs no acceleration structure, so the driver
/// uses this instead of failing on a map (a generated fixture, say) that has
/// faces and no brushes.
/// </remarks>
internal sealed class EmptySceneTracer : IRayTracer
{
    public string TracerIdentity => "empty-scene";

    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        hitBits.Span[..((rays.Length + 63) / 64)].Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<HitId> hits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        hits.Span[..rays.Length].Fill(HitId.Missed);
        return ValueTask.CompletedTask;
    }
}
