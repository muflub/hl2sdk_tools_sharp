//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Tracing;

/// <summary>
/// The numbers a batch of rays is traced with, as opposed to the rays
/// themselves.
/// </summary>
/// <param name="MinDistance">
/// How far along a ray a hit starts counting, in the same units as
/// <see cref="Ray.MaxDistance"/> -- lengths of <c>Direction</c>, so a ray
/// spanning a whole map and one spanning an inch use the same number for
/// "just off the surface I started on". Hits closer than this are ignored.
/// </param>
/// <remarks>
/// <para>
/// THIS EXISTS BECAUSE ONE UNWRITTEN NUMBER WAS WORTH A FACTOR OF 93.
/// §10c measured the same 6,319,296 rays through two tracers: with
/// <c>MinDistance = 0</c>, which is what stock's arithmetic implies, 1,769 hit
/// bits disagreed; with <c>MinDistance = 1e-3</c>, 19 did. Nobody had written
/// the number down, so each implementation picked its own and the difference
/// was read as a property of the tracers.
/// </para>
/// <para>
/// There is deliberately no single "right" default here, because the two
/// callers want opposite things and both are correct:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="StockExact"/> for anything being compared against stock vrad's
/// own output. Stock starts its BSP walk at fraction 0 exactly
/// And its KD-tree at the caller's <c>TMin</c>, and a
/// managed tracer that quietly nudged that would stop being a reference.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="SelfIntersectionSafe"/> for a hardware backend, or any tracer
/// whose intersection arithmetic is not the CPU one bit for bit, where the
/// epsilon is what stops a sample point re-hitting the surface it sits on.
/// </description>
/// </item>
/// </list>
/// <para>
/// The three init-only members below exist for stock's <c>TestLine</c>
/// callers -- the prop and leaf-ambient samplers -- whose segments are not
/// plain visibility rays: a static prop must not shadow itself, a sky sample
/// must count the sky as reached rather than as blocked, and each segment's
/// answer must not depend on which other segments share its batch. Their
/// defaults are the plain query every other caller makes, so a value built
/// from <see cref="StockExact"/> or <see cref="SelfIntersectionSafe"/> means
/// exactly what it meant before they existed. Not every tracer can answer
/// every combination: <see cref="IRayTracer.Supports"/> says which, and a
/// caller holding a tracer that lacks one routes that batch elsewhere
/// (<see cref="Rad.HybridRayTracer"/>) rather than getting a silently
/// different answer.
/// </para>
/// </remarks>
public readonly record struct RayTraceOptions(float MinDistance)
{
    /// <summary>
    /// Triangles whose id equals this are ignored by the trace, as if absent;
    /// null ignores nothing.
    /// </summary>
    /// <remarks>
    /// Stock's <c>skip_id</c>: prop lighting passes
    /// <see cref="Rad.TraceId.StaticProp"/> ORed with the prop's index so a
    /// prop is not shadowed by its own triangles. Nullable rather than -1 by
    /// convention because 0 is a real id and a defaulted struct must not skip
    /// it.
    /// </remarks>
    public int? SkipId { get; init; }

    /// <summary>
    /// Visibility only: a ray whose first hit is a sky triangle (its id has
    /// <see cref="Rad.TraceId.Sky"/>) is not blocked.
    /// </summary>
    /// <remarks>
    /// Stock's <c>TestLine_DoesHitSky</c>: a sky sample that reaches the sky
    /// face has seen the sky, which is the point of the sample. Only the FIRST
    /// hit is looked at, so a sky face behind an opaque one still blocks.
    /// Closest-hit queries report a sky hit as it is and refuse this flag.
    /// </remarks>
    public bool SkyDoesNotBlock { get; init; }

    /// <summary>
    /// Each ray's answer must be a function of that ray alone, not of the
    /// other rays in the batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="KdRayTracer"/> traces four rays per packet, and a packet's
    /// shared traversal can decide an edge-grazing ray differently from a
    /// traversal of that ray alone. Stock's <c>TestLine</c> duplicates its one
    /// segment into all four lanes, so a batch that must reproduce
    /// <c>TestLine</c> bit for bit asks for the same: the KD tracer then traces
    /// each ray in a packet of its own. That costs the lanes stock also
    /// wastes, and nothing on a tracer that already traces rays one at a time,
    /// which is every GPU backend.
    /// </para>
    /// <para>
    /// It changes no answer's meaning, only which of two equally valid answers
    /// a grazing ray gets, so every tracer supports it.
    /// </para>
    /// </remarks>
    public bool IsolatedRays { get; init; }

    /// <summary>
    /// Whether this is the plain query every tracer answers: no id is skipped
    /// and the sky blocks like anything else.
    /// </summary>
    public bool IsPlain => SkipId is null && !SkyDoesNotBlock;

    /// <summary>
    /// The options stock's <c>TestLine</c> traces a segment with: no epsilon,
    /// each segment on its own, optionally one id skipped and the sky passing.
    /// </summary>
    /// <param name="skipId">The id to ignore, or a negative value to ignore nothing (stock's -1).</param>
    /// <param name="skyDoesNotBlock"><c>TestLine_DoesHitSky</c> rather than <c>TestLine</c>.</param>
    /// <returns>The options.</returns>
    public static RayTraceOptions TestLine(int skipId = -1, bool skyDoesNotBlock = false) =>
        new(0.0f)
        {
            SkipId = skipId < 0 ? null : skipId,
            SkyDoesNotBlock = skyDoesNotBlock,
            IsolatedRays = true,
        };

    /// <summary>
    /// Stock's own epsilon: none. Use this whenever the result is compared
    /// against stock vrad.
    /// </summary>
    public static RayTraceOptions StockExact => new(0.0f);

    /// <summary>
    /// The 1e-3 that §10c measured, which is what a ray-query backend wants.
    /// </summary>
    public static RayTraceOptions SelfIntersectionSafe => new(1.0e-3f);
}
