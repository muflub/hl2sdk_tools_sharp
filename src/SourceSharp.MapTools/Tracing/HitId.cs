//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Tracing;

/// <summary>
/// What a closest-hit trace found: which surface, and how far along the ray.
/// </summary>
/// <param name="Surface">
/// The identity of the surface hit, or <see cref="Miss"/> when the ray reached
/// <c>MaxDistance</c> without hitting anything. What the number MEANS is the
/// tracer's own affair -- the BSP surface tracer returns a face index, the
/// KD-tree tracer returns a triangle id -- so it is only comparable between
/// results from the same <see cref="IRayTracer.TracerIdentity"/>.
/// </param>
/// <param name="Fraction">
/// Where along the ray the hit is, in the same units as
/// <see cref="Ray.MaxDistance"/>: the hit point is
/// <c>Origin + Fraction * Direction</c>. Undefined on a miss. It CAN EXCEED
/// <see cref="Ray.MaxDistance"/> on <see cref="KdRayTracer"/>: stock's
/// <c>Trace4Rays</c> does not clip a hit to <c>TMax</c>
/// (is commented out), so a surface just beyond the
/// segment's end can be reported. A caller that asks about the segment tests
/// <c>Fraction &lt; MaxDistance</c> itself, as stock's do
/// Or asks <see cref="IRayTracer.TraceVisibilityAsync"/>,
/// which honours the end.
/// </param>
/// <remarks>
/// <para>
/// EIGHT BYTES, and that is a deliberate ceiling rather than a coincidence.
/// §10c sizes the closest-hit readback against the visibility one -- one bit
/// per ray against "32x larger" -- and a leaf-ambient batch is 6.3 million rays
/// on one real map, so every field added here costs 25 MB of PCIe traffic per
/// batch. A Vulkan ray query returns exactly these two numbers, so this is also
/// the wire format rather than a translation of it.
/// </para>
/// <para>
/// WHAT IS NOT HERE, and why it does not need to be. Stock's
/// <c>CLightSurface</c> carries two more fields out of a trace:
/// <c>m_LuxelCoord</c>, the hit point in the face's lightmap space, and
/// <c>m_bHasLuxel</c>, which is false when the ray ended on the sky.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// The luxel coordinate is six flops from <see cref="Surface"/> and
/// <see cref="Fraction"/>: it is
/// <c>Dot(pt, lightmapVecs[i]) + lightmapVecs[i][3] - mins[i]</c>, and stock
/// computes it from the same two numbers inside
/// <c>TestPointAgainstSurface</c>. Carrying it would double the readback to
/// save an operation the caller pays once per ray rather than once per
/// candidate.
/// </description>
/// </item>
/// <item>
/// <description>
/// "Has luxel" is not information either. Stock sets it on every path that
/// sets <c>m_pSurface</c> EXCEPT the sky path
/// So it is exactly
/// "hit something, and that something is not a <c>SURF_SKY</c> face" -- which
/// the caller reads off the returned surface's own texinfo flags.
/// </description>
/// </item>
/// </list>
/// </remarks>
public readonly record struct HitId(int Surface, float Fraction)
{
    /// <summary>The <see cref="Surface"/> value meaning "nothing was hit".</summary>
    /// <remarks>
    /// -1 rather than 0, because 0 is a perfectly good face index and a
    /// default-valued handle that reads as a real one is a mistake this project
    /// has already made once.
    /// </remarks>
    public const int Miss = -1;

    /// <summary>A miss: no surface, and the full ray length.</summary>
    /// <remarks>
    /// <see cref="Fraction"/> is 1 rather than 0 so that a miss reads as "got
    /// all the way to the end", which is what stock's <c>m_HitFrac</c> holds
    /// after a trace that found nothing.
    /// </remarks>
    public static HitId Missed => new(Miss, 1.0f);

    /// <summary>Whether this result names a surface.</summary>
    public bool IsHit => Surface != Miss;
}
