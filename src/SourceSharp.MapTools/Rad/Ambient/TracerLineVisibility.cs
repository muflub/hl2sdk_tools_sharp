using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <see cref="IAmbientLightVisibility"/> over the KD-tree of the map's shadow
/// casters -- stock's <c>g_RtEnv</c> -- with <c>TestLine</c>'s own arithmetic.
/// </summary>
/// <remarks>
/// <c>TestLine</c> with texture shadows off: a segment
/// is blocked when a triangle is hit strictly before its end, and the fraction
/// visible is then 0, else 1. The ray is stock's: a direction normalised with
/// <c>ReciprocalSIMD</c> under <see cref="StockQuirk.AmbientCubeReciprocalEstimate"/>
/// (exactly otherwise), traced over <c>[0, len]</c>.
/// </remarks>
public sealed class TracerLineVisibility : IAmbientLightVisibility
{
    private readonly KdRayTracer _tracer;
    private readonly bool _stockReciprocal;

    /// <summary>Wraps a tracer.</summary>
    /// <param name="tracer">The KD tracer over the map's shadow casters.</param>
    /// <param name="compliance">Whether to normalise as stock does.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public TracerLineVisibility(KdRayTracer tracer, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(compliance);
        _tracer = tracer;
        _stockReciprocal = compliance.Emulates(StockQuirk.AmbientCubeReciprocalEstimate);
    }

    /// <inheritdoc />
    public void FractionsVisible(Vec3 start, ReadOnlySpan<Vec3> ends, Span<float> fractions)
    {
        if (fractions.Length < ends.Length)
        {
            throw new ArgumentException("one fraction per end", nameof(fractions));
        }

        Span<Vec3> starts = ends.Length <= 256 ? stackalloc Vec3[ends.Length] : new Vec3[ends.Length];
        Span<bool> blocked = ends.Length <= 1024 ? stackalloc bool[ends.Length] : new bool[ends.Length];
        starts.Fill(start);
        _tracer.TestLines(starts, ends, blocked, _stockReciprocal);
        for (int i = 0; i < ends.Length; i++)
        {
            fractions[i] = blocked[i] ? 0.0f : 1.0f;
        }
    }
}
