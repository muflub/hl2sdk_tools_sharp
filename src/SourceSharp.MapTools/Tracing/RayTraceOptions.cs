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
/// (<c>bsplib.cpp:3736</c>) and its KD-tree at the caller's <c>TMin</c>, and a
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
/// </remarks>
public readonly record struct RayTraceOptions(float MinDistance)
{
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
