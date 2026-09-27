//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <see cref="IAmbientLightVisibility"/> over the map's shadow casters --
/// stock's <c>g_RtEnv</c> -- with <c>TestLine</c>'s own arithmetic, asked of
/// an <see cref="IRayTracer"/> in batches.
/// </summary>
/// <remarks>
/// <para>
/// <c>TestLine</c> with texture shadows off: a segment
/// is blocked when a triangle is hit strictly before its end, and the fraction
/// visible is then 0, else 1. The ray is stock's (<see cref="Ray.Segment"/>):
/// a direction normalised with <c>ReciprocalSIMD</c> under
/// <see cref="StockQuirk.AmbientCubeReciprocalEstimate"/> (exactly otherwise),
/// traced over <c>[0, len]</c>, each segment on its own
/// (<see cref="RayTraceOptions.TestLine"/>).
/// </para>
/// <para>
/// THROUGH THE SEAM rather than the KD tracer's own <c>TestLines</c>, so a
/// GPU tracer gets these segments too; the KD tracer answers the seam's
/// isolated rays with the same bits its <c>TestLines</c> does. A leaf's
/// samples come as one call
/// (<see cref="FractionsVisible(ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, Span{float})"/>),
/// which is one tracer batch.
/// </para>
/// </remarks>
public sealed class TracerLineVisibility : IAmbientLightVisibility
{
    private readonly IRayTracer _tracer;
    private readonly bool _stockReciprocal;

    /// <summary>Wraps a tracer.</summary>
    /// <param name="tracer">The tracer over the map's shadow casters.</param>
    /// <param name="compliance">Whether to normalise as stock does.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public TracerLineVisibility(IRayTracer tracer, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(compliance);
        _tracer = tracer;
        _stockReciprocal = compliance.Emulates(StockQuirk.AmbientCubeReciprocalEstimate);
    }

    /// <summary>The tracer the segments go to.</summary>
    internal IRayTracer Tracer => _tracer;

    /// <summary>Whether the rays are normalised with stock's reciprocal estimate.</summary>
    internal bool StockReciprocal => _stockReciprocal;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The tracer answers asynchronously (a GPU). This call is synchronous and
    /// never waits; the leaf-ambient stage sees this visibility and traces its
    /// segments through <see cref="TestLineStage"/> instead.
    /// </exception>
    public void FractionsVisible(Vec3 start, ReadOnlySpan<Vec3> ends, Span<float> fractions) =>
        FractionsVisible([start], ends, fractions);

    /// <inheritdoc />
    public void FractionsVisible(ReadOnlySpan<Vec3> starts, ReadOnlySpan<Vec3> ends, Span<float> fractions)
    {
        int count = starts.Length * ends.Length;
        if (fractions.Length < count)
        {
            throw new ArgumentException("one fraction per start and end", nameof(fractions));
        }

        if (count == 0)
        {
            return;
        }

        // The rays and bits are pooled: this instance is shared by every
        // worker, and a leaf's batch is thousands of segments on a map that
        // bakes many lights, which allocated per call was most of what leaf
        // ambient allocated. They go back to the pool only once the tracer
        // has finished with them: a call still in flight keeps them.
        Ray[] rays = ArrayPool<Ray>.Shared.Rent(count);
        ulong[] bits = ArrayPool<ulong>.Shared.Rent((count + 63) >> 6);
        bool inFlight = false;
        try
        {
            int at = 0;
            for (int s = 0; s < starts.Length; s++)
            {
                for (int e = 0; e < ends.Length; e++)
                {
                    rays[at++] = Ray.Segment(starts[s], ends[e], _stockReciprocal);
                }
            }

            ValueTask task = _tracer.TraceVisibilityAsync(
                rays.AsMemory(0, count), bits.AsMemory(0, (count + 63) >> 6), RayTraceOptions.TestLine(), CancellationToken.None);
            if (!task.IsCompletedSuccessfully)
            {
                // Never waited for: this call is synchronous, and a compile
                // worker must not block on a GPU. The leaf-ambient stage
                // traces through TestLineStage instead, which parks.
                Task t = task.AsTask();
                if (!t.IsCompleted)
                {
                    inFlight = true;
                    throw new InvalidOperationException(
                        $"{_tracer.TracerIdentity} answers asynchronously; its segments must be traced through the staged driver");
                }

                TestLineBatch.RethrowIfFailed(t, CancellationToken.None);
            }

            for (int i = 0; i < count; i++)
            {
                fractions[i] = (bits[i >> 6] & (1UL << (i & 63))) != 0 ? 0.0f : 1.0f;
            }
        }
        finally
        {
            if (!inFlight)
            {
                ArrayPool<ulong>.Shared.Return(bits);
                ArrayPool<Ray>.Shared.Return(rays);
            }
        }
    }
}
