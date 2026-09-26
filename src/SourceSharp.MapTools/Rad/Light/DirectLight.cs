//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// One light source as vrad resolves it: <c>directlight_t</c>
/// </summary>
/// <remarks>
/// <para>
/// A class, and the one place in this lane where that is right: stock's
/// <c>activelights</c> is an intrusive singly-linked list built by
/// <c>AllocDLight</c> and walked by every sample, so identity matters and the
/// count is in the hundreds rather than the millions.
/// </para>
/// <para>
/// <b>It is NOT <c>dworldlight_t</c>.</b> The BSP struct is a subset written
/// out by <see cref="WorldLightExporter"/>, with the intensity divided by 255
/// and the <see cref="Pvs"/>, fade distances and cap dropped entirely -- so a
/// map reloaded from its own worldlights lump cannot reproduce its own
/// lighting. The extra fields are why.
/// </para>
/// <para>
/// The six <see cref="EmitType"/> values are not six equal cases.
/// <see cref="EmitType.QuakeLight"/> is declared in the reference implementation and
/// produced by nothing in <c>src/utils/vrad</c>, and
/// <c>GatherSampleLightSSE</c>'s switch has no
/// case for it -- it falls to <c>Error("Bad dl->light.type")</c>. The other
/// five split three ways: sky and sky-ambient trace toward the sky, and point,
/// surface and spotlight share one falloff function.
/// </para>
/// </remarks>
public sealed class DirectLight
{
    /// <summary>Its position in creation order: <c>index</c>.</summary>
    public int Index { get; init; }

    /// <summary>Which kind of source this is.</summary>
    public EmitType Type { get; set; }

    /// <summary>The lightstyle this light animates on.</summary>
    public int Style { get; set; }

    /// <summary>Where the light is.</summary>
    public Vec3 Origin { get; set; }

    /// <summary>
    /// The light's colour, in vrad's 0..255 linear scale.
    /// </summary>
    /// <remarks>
    /// Divided by 255 on the way into the BSP, with
    /// stock's own comment asking why. The scale is arbitrary-but-consistent
    /// and so is reproduced rather than normalised.
    /// </remarks>
    public Vec3 Intensity { get; set; }

    /// <summary>The direction a surface or spotlight faces.</summary>
    public Vec3 Normal { get; set; }

    /// <summary>The vis cluster the light sits in, or -1.</summary>
    public int Cluster { get; set; }

    /// <summary>The cosine where a spotlight's penumbra starts.</summary>
    public float StopDot { get; set; }

    /// <summary>The cosine where it ends.</summary>
    public float StopDot2 { get; set; }

    /// <summary>The spotlight falloff exponent.</summary>
    public float Exponent { get; set; }

    /// <summary>The <c>_distance</c> cutoff. Written out and never read by vrad.</summary>
    public float Radius { get; set; }

    /// <summary>The constant term of the attenuation denominator.</summary>
    public float ConstantAttn { get; set; }

    /// <summary>The linear term.</summary>
    public float LinearAttn { get; set; }

    /// <summary>The quadratic term.</summary>
    public float QuadraticAttn { get; set; }

    /// <summary>
    /// The face this light was emitted from, or -1.
    /// </summary>
    /// <remarks>
    /// Set to -1 by <c>AllocDLight</c> and never
    /// assigned anything else anywhere in <c>src/utils/vrad</c>. It is still
    /// READ, where <c>dl-&gt;facenum == -1</c>
    /// gates whether the light's own origin is used as the ray source -- so the
    /// branch is always taken and the other branch leaves the source at the
    /// world origin. Carried because the test is real code on a real path, and
    /// because a future lane that sets it would silently change every light.
    /// </remarks>
    public int FaceNum { get; set; } = -1;

    /// <summary>
    /// The accumulated PVS of every cluster this light can reach, one bit per
    /// cluster.
    /// </summary>
    /// <remarks>
    /// A point light's is just its own cluster's row. The sky light's is the
    /// UNION of every sky-touching leaf's row, merged in
    /// <c>BuildVisForLightEnvironment</c>, which is why a sun reaches the whole
    /// map and why that merge has to happen before any sample is taken.
    /// </remarks>
    public byte[] Pvs { get; set; } = [];

    /// <summary>Where a hard-falloff light starts fading.</summary>
    public float StartFadeDistance { get; set; }

    /// <summary>
    /// Where it reaches zero. Less than <see cref="StartFadeDistance"/> means
    /// no hard falloff.
    /// </summary>
    /// <remarks>
    /// Initialised to -1 with <see cref="StartFadeDistance"/> at 0
    /// And stock's comment says the encoding out loud:
    /// "end&lt;start indicates not set".
    /// </remarks>
    public float EndFadeDistance { get; set; } = -1.0f;

    /// <summary>
    /// The greatest distance fed into the falloff curve: <c>m_flCapDist</c>.
    /// </summary>
    /// <remarks>
    /// <c>1.0e22</c> by default, which is effectively no cap. It exists
    /// because an aggressive <c>_fifty_percent_distance</c> can give the
    /// quadratic a positive leading coefficient, so past the curve's minimum
    /// the light would get BRIGHTER with distance; the cap freezes the falloff
    /// at that minimum and a fade takes over.
    /// </remarks>
    public float CapDist { get; set; } = 1.0e22f;

    /// <summary>Whether this light has a hard falloff to zero.</summary>
    public bool HasHardFalloff => EndFadeDistance > StartFadeDistance;
}
