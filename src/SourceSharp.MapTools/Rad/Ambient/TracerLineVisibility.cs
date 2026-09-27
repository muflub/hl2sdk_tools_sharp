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

        // Every segment starts at `start`, so the tracer takes it once rather
        // than as a copy per segment. The flags fit on the stack for a small
        // batch; a large one (a full-size map's leaves are thousands of
        // samples) borrows pooled storage and returns it, rather than
        // allocating per call, which was most of what leaf ambient allocated.
        bool[]? rented = null;
        Span<bool> blocked = ends.Length <= MaxStackFlags
            ? stackalloc bool[ends.Length]
            : (rented = ArrayPool<bool>.Shared.Rent(ends.Length)).AsSpan(0, ends.Length);
        try
        {
            _tracer.TestLines(start, ends, blocked, _stockReciprocal);
            for (int i = 0; i < ends.Length; i++)
            {
                fractions[i] = blocked[i] ? 0.0f : 1.0f;
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<bool>.Shared.Return(rented);
            }
        }
    }

    // The largest batch whose flags go on the stack.
    private const int MaxStackFlags = 1024;
}
